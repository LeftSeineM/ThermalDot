using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ThermalDot;
public sealed class ProxyControlState
{
    public string Mode { get; set; } = "";
    public string ActiveMode { get; set; } = "";
    public int Port { get; set; }
    public bool Ready { get; set; }
    public bool Tun { get; set; }
    public string Error { get; set; } = "";
    public List<string> Nodes { get; set; } = new();
    public Dictionary<string, string> Selections { get; set; } = new();
    public SystemProxyState? System { get; set; }
    public string SelectedNode => Selections.Count > 0 && Selections.Values.Distinct().Count() == 1 ? Selections.Values.First() : "";
}
internal sealed class ClashSession : IDisposable
{
    private readonly HttpClient client;
    internal ClashSession()
    {
        var endpoint = ClashEndpoint.Discover();
        client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = endpoint.Address, Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        if (endpoint.Secret != "") client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
    }
    internal async Task<JsonDocument> Get(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    internal async Task Send(HttpMethod method, string path, object body, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, token); response.EnsureSuccessStatusCode();
    }
    public void Dispose() => client.Dispose();
}
public static class ProxyControl
{
    private static string Text(JsonElement value, string name) => ClashMonitor.Value(value, name);
    public static string NodeLabel(string name, int index)
    {
        var match = Regex.Match(name, @"美国\s*Y0?[12]", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : "美国节点 " + (index + 1);
    }
    internal static ProxyControlState Parse(JsonElement config, JsonElement proxies, JsonElement rules)
    {
        var state = new ProxyControlState { Mode = Text(config, "mode") };
        if (config.TryGetProperty("mixed-port", out var port) && port.TryGetInt32(out int mixed) && mixed > 0) state.Port = mixed;
        else if (config.TryGetProperty("port", out port) && port.TryGetInt32(out int http)) state.Port = http;
        state.Tun = config.TryGetProperty("tun", out var tun) && tun.ValueKind == JsonValueKind.Object &&
            tun.TryGetProperty("enable", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        state.Nodes = proxies.EnumerateObject().Where(p => !p.Value.TryGetProperty("all", out _) &&
            Regex.IsMatch(p.Name, "美国|United States|USA|🇺🇸|🇺🇲", RegexOptions.IgnoreCase))
            .OrderBy(p => Regex.IsMatch(p.Name, @"美国\s*Y0?1") ? 0 : Regex.IsMatch(p.Name, @"美国\s*Y0?2") ? 1 : 2)
            .ThenBy(p => p.Name, StringComparer.Ordinal).Take(2).Select(p => p.Name).ToList();
        bool Selectable(string group) => state.Nodes.Count == 2 && proxies.TryGetProperty(group, out var entry) &&
            Text(entry, "type").Equals("Selector", StringComparison.OrdinalIgnoreCase) &&
            entry.TryGetProperty("all", out var all) && state.Nodes.All(n => all.EnumerateArray().Any(v => v.GetString() == n));
        if (Selectable("GLOBAL")) state.Selections["GLOBAL"] = Text(proxies.GetProperty("GLOBAL"), "now");
        string current = "";
        if (rules.ValueKind == JsonValueKind.Array)
            foreach (var rule in rules.EnumerateArray()) if (Text(rule, "type").Equals("Match", StringComparison.OrdinalIgnoreCase) || Text(rule, "type").Equals("Final", StringComparison.OrdinalIgnoreCase)) current = Text(rule, "proxy");
        var visited = new HashSet<string>(); string ruleGroup = "";
        while (current != "" && visited.Add(current) && proxies.TryGetProperty(current, out var group))
        {
            if (Selectable(current)) ruleGroup = current;
            current = Text(group, "now");
        }
        if (ruleGroup != "") state.Selections[ruleGroup] = Text(proxies.GetProperty(ruleGroup), "now");
        state.Ready = state.Port is > 0 and <= 65535 && !state.Tun && state.Mode is "global" or "rule" or "direct";
        if (state.Tun) state.Error = "当前启用 TUN，请先在 Clash 中关闭 TUN 后使用此控制";
        else if (!state.Ready) state.Error = "Clash 当前配置不支持系统代理控制";
        return state;
    }
    private static async Task<ProxyControlState> Read(ClashSession api, CancellationToken token)
    {
        using var config = await api.Get("configs", token);
        using var proxies = await api.Get("proxies", token);
        using var rules = await api.Get("rules", token);
        var state = Parse(config.RootElement, proxies.RootElement.GetProperty("proxies"), rules.RootElement.GetProperty("rules"));
        state.System = SystemProxy.Read();
        state.ActiveMode = !state.Tun && (SystemProxy.IsOff(state.System) || (state.Mode == "direct" && SystemProxy.IsClash(state.System, state.Port))) ? "off" :
            SystemProxy.IsClash(state.System, state.Port) ? state.Mode : "";
        return state;
    }
    public static async Task<ProxyControlState> Read(CancellationToken token)
    {
        try { using var api = new ClashSession(); return await Read(api, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return new ProxyControlState { Error = "Clash 控制暂不可用，请确认客户端正在运行" }; }
    }
    internal static bool Matches(ProxyControlState before, ProxyControlState expected) =>
        before.Ready && before.Mode == expected.Mode && before.Port == expected.Port && before.System == expected.System &&
        before.Nodes.SequenceEqual(expected.Nodes) && before.Selections.Count == expected.Selections.Count &&
        before.Selections.All(p => expected.Selections.TryGetValue(p.Key, out var selected) && selected == p.Value);
    public static async Task<(ProxyControlState State, string Message)> Change(ProxyControlState expected, string action, string value, CancellationToken token)
    {
        if ((action == "mode" && value is not ("global" or "rule" or "off")) || (action != "mode" && action != "node"))
            return (expected, "无效操作");
        var written = new List<(string Group, string Before, string After)>();
        ProxyControlState? before = null; SystemProxyState? targetSystem = null; string? targetMode = null;
        try
        {
            using var api = new ClashSession();
            before = await Read(api, token);
            if (!Matches(before, expected)) return (before, "设置刚刚发生变化，请重新选择");
            if (action == "mode" && value != "off" && before.ActiveMode == value) return (before, "当前已是所选模式");
            if (action == "node")
            {
                if (!before.Nodes.Contains(value) || before.Selections.Count < 2) return (before, "未找到同时支持这两个节点的全局和规则策略组");
                foreach (var group in before.Selections)
                {
                    if (group.Value == value) continue;
                    // Record before sending: an interrupted response may still mean the server applied the write.
                    written.Add((group.Key, group.Value, value));
                    await api.Send(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(group.Key), new { name = value }, token);
                }
            }
            else
            {
                targetMode = value == "off" ? "direct" : value;
                await api.Send(HttpMethod.Patch, "configs", new { mode = targetMode }, token);
                using var check = await api.Get("configs", token);
                if (Text(check.RootElement, "mode") != targetMode) throw new InvalidOperationException();
                targetSystem = value == "off" ? before.System! with { Flags = 1 } :
                    before.System! with { Flags = 3, Server = "127.0.0.1:" + before.Port };
                SystemProxy.Write(targetSystem);
            }
            await Task.Delay(250, token);
            var after = await Read(api, token);
            bool confirmed = action == "mode" ? after.Mode == targetMode && after.System == targetSystem :
                after.SelectedNode == value && after.Selections.Count == before.Selections.Count;
            if (!confirmed) throw new InvalidOperationException();
            return (after, action == "mode" ? value == "off" ? "已关闭代理 · Clash 保持后台运行" : "已切换为" + (value == "global" ? "全局" : "规则") + "模式" :
                "已切换节点：" + NodeLabel(value, before.Nodes.IndexOf(value)) + (after.ActiveMode == "off" ? "（代理仍关闭）" : ""));
        }
        catch
        {
            // Best-effort compensation affects only values still equal to this operation's writes.
            if (before != null)
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    using var api = new ClashSession();
                    var current = await Read(api, rollback.Token);
                    if (targetSystem != null && current.System == targetSystem) SystemProxy.Write(before.System!);
                    if (targetMode != null && current.Mode == targetMode) await api.Send(HttpMethod.Patch, "configs", new { mode = before.Mode }, rollback.Token);
                    foreach (var item in written)
                        if (current.Selections.TryGetValue(item.Group, out var selected) && selected == item.After)
                            await api.Send(HttpMethod.Put, "proxies/" + Uri.EscapeDataString(item.Group), new { name = item.Before }, rollback.Token);
                }
                catch { }
            }
            var after = await Read(CancellationToken.None);
            return (after, "切换未确认，已重新读取当前状态，请检查后重试");
        }
    }
}