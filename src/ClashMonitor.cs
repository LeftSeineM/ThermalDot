using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThermalDot;

public sealed class ProxyReading
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Mode { get; set; } = "";
    public string Group { get; set; } = "";
    public string Node { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public int? DelayMs { get; set; }
    public bool Available { get; set; }
    public string Error { get; set; } = "";
}

internal sealed class ClashEndpoint
{
    public required Uri Address { get; init; }
    public string Secret { get; init; } = "";
    internal static string Scalar(string text, string key)
    {
        var match = Regex.Match(text, "^" + Regex.Escape(key) + @":[ \t]*([^\r\n]*)", RegexOptions.Multiline);
        if (!match.Success) return "";
        string value = match.Groups[1].Value.Trim();
        if (value.StartsWith('"')) return JsonSerializer.Deserialize<string>(value) ?? "";
        if (value.StartsWith("'") && value.EndsWith("'")) return value[1..^1].Replace("''", "'");
        return Regex.Replace(value, @"\s+#.*$", "").Trim();
    }
    internal static ClashEndpoint Parse(string text)
    {
        string controller = Scalar(text, "external-controller");
        if (!Uri.TryCreate("http://" + controller, UriKind.Absolute, out var uri) ||
            uri.Port <= 0 || uri.AbsolutePath != "/" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "")
            throw new InvalidDataException();
        // Control secrets must never be sent through a proxy or to a remote host.
        string host = uri.Host.Trim('[', ']');
        if (host == "0.0.0.0" || host == "::") uri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) uri = new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        else if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip)) throw new InvalidDataException();
        return new ClashEndpoint { Address = uri, Secret = Scalar(text, "secret") };
    }
    internal static ClashEndpoint Discover()
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "clash", "config.yaml");
        if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) throw new FileNotFoundException();
        return Parse(File.ReadAllText(file));
    }
}

public static class ClashMonitor
{
    public const string TestUrl = "https://www.gstatic.com/generate_204";
    internal static string Value(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    internal static ProxyReading Resolve(JsonElement proxies, string mode, string requestedGroup, string fallback)
    {
        var result = new ProxyReading { Mode = mode };
        if (proxies.ValueKind != JsonValueKind.Object) { result.Error = "Clash 返回的数据暂不可用"; return result; }
        result.Groups = proxies.EnumerateObject().Where(p => p.Value.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.Array)
            .Select(p => p.Name).OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (mode.Equals("direct", StringComparison.OrdinalIgnoreCase))
        { result.Node = "DIRECT"; result.Error = "Clash 当前为直连模式"; return result; }
        if (!mode.Equals("rule", StringComparison.OrdinalIgnoreCase) && !mode.Equals("global", StringComparison.OrdinalIgnoreCase))
        { result.Error = "当前 Clash 模式暂不支持节点监测"; return result; }
        string group = requestedGroup;
        if (group == "") group = mode.Equals("global", StringComparison.OrdinalIgnoreCase) ? "GLOBAL" : fallback;
        if (group == "" || !proxies.TryGetProperty(group, out _))
        { result.Error = requestedGroup == "" ? "规则模式：请选择要监测的策略组" : "所选策略组已不存在，请重新选择"; return result; }
        result.Group = group;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string current = group;
        for (int i = 0; i < 16; i++)
        {
            if (!visited.Add(current) || !proxies.TryGetProperty(current, out var node))
            { result.Error = "无法确认策略组当前节点"; return result; }
            if (node.TryGetProperty("all", out _))
            {
                string next = Value(node, "now");
                if (next == "") { result.Error = "该策略组没有唯一的当前节点"; return result; }
                current = next; continue;
            }
            result.Node = current;
            string type = Value(node, "type");
            if (type.Equals("Direct", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Reject", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("Pass", StringComparison.OrdinalIgnoreCase))
            { result.Error = "所选策略组当前为 " + current; return result; }
            if (type == "") { result.Error = "节点类型暂不可用"; return result; }
            result.Available = true; return result;
        }
        result.Error = "策略组层级过多，暂无法确认节点"; return result;
    }

    public static async Task<ProxyReading> Read(string requestedGroup, CancellationToken token)
    {
        var result = new ProxyReading();
        try
        {
            var endpoint = ClashEndpoint.Discover();
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = endpoint.Address, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            if (endpoint.Secret != "") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            using var config = await Get("configs");
            using var all = await Get("proxies");
            string mode = Value(config.RootElement, "mode");
            string fallback = "";
            if (mode.Equals("rule", StringComparison.OrdinalIgnoreCase) && requestedGroup == "")
            {
                using var rules = await Get("rules");
                if (rules.RootElement.TryGetProperty("rules", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var rule in entries.EnumerateArray())
                        if (Value(rule, "type").Equals("Match", StringComparison.OrdinalIgnoreCase) || Value(rule, "type").Equals("Final", StringComparison.OrdinalIgnoreCase))
                            fallback = Value(rule, "proxy");
            }
            if (!all.RootElement.TryGetProperty("proxies", out var proxies)) throw new InvalidDataException();
            result = Resolve(proxies, mode, requestedGroup, fallback);
            if (!result.Available) return result;
            string observedNode = result.Node;
            using var delay = await Get("proxies/" + Uri.EscapeDataString(observedNode) + "/delay?timeout=5000&url=" + Uri.EscapeDataString(TestUrl));
            if (!delay.RootElement.TryGetProperty("delay", out var value) || !value.TryGetInt32(out var milliseconds) || milliseconds < 0 || milliseconds > 10000)
            { result.Error = "节点延时暂不可用"; return result; }
            // The selected node may change while a request is in flight. Do not attach an old delay to a new node.
            using var after = await Get("proxies");
            using var afterConfig = await Get("configs");
            string afterMode = Value(afterConfig.RootElement, "mode");
            var confirmed = Resolve(after.RootElement.GetProperty("proxies"), afterMode, result.Group, fallback);
            if (afterMode != mode || confirmed.Node != observedNode || !confirmed.Available)
            { result.Error = "节点刚刚发生变化，等待下次检查"; result.DelayMs = null; return result; }
            result.DelayMs = milliseconds; result.Time = DateTime.Now;
            return result;

            async Task<JsonDocument> Get(string path)
            {
                using var response = await client.GetAsync(path, deadline.Token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Clash request failed", null, response.StatusCode);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (FileNotFoundException) { result.Error = "未检测到 Clash for Windows 的本地配置"; }
        catch (OperationCanceledException) { result.Error = "检查超时 · 暂不可用"; }
        catch (HttpRequestException e)
        {
            result.Error = e.StatusCode == HttpStatusCode.Unauthorized || e.StatusCode == HttpStatusCode.Forbidden
                ? "Clash 本地接口认证失败" : result.Node != "" ? "节点测试失败 · 暂不可用" : "Clash 本地接口暂不可用";
        }
        catch { result.Error = "Clash 配置或返回数据暂不可用"; }
        result.DelayMs = null; result.Time = DateTime.Now; return result;
    }
}