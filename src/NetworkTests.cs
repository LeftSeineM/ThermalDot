using System;
using System.IO;
using System.Text.Json;
namespace ThermalDot;
internal static class NetworkTests
{
    private static void Check(bool ok, string reason) { if (!ok) throw new Exception("Network test: " + reason); }
    internal static void Run()
    {
        Check(NetworkSampler.Rate(100, 2100, 2) == 1000, "Use elapsed seconds");
        Check(NetworkSampler.Rate(100, 100, 1) == 0, "Idle is zero");
        Check(NetworkSampler.Rate(100, 50, 1) == null && NetworkSampler.Rate(100, 200, 0) == null &&
            NetworkSampler.Rate(0, 100000, 3600) == null, "Counter resets and resume gaps cannot create speed spikes");
        Check(NetworkSampler.Speed(null) == "—" && NetworkSampler.Speed(double.NaN) == "—" &&
            NetworkSampler.Speed(0) == "0 B/s" && NetworkSampler.Speed(1000) == "1.0 KB/s" &&
            NetworkSampler.Speed(12500000) == "12.50 MB/s", "Units and missing data");
        var config = ClashEndpoint.Parse("mixed-port: 7890\nexternal-controller: 127.0.0.1:54376\nsecret: 'test-only'\n");
        Check(ClashEndpoint.Parse("secret:\nexternal-controller: 127.0.0.1:9090").Secret == "", "Empty secrets cannot consume the next YAML key");
        Check(config.Address.Port == 54376 && config.Secret == "test-only", "Read local endpoint without fixed port");
        Check(ClashEndpoint.Parse("external-controller: 0.0.0.0:9090\nsecret: \"test-only\"").Address.Host == "127.0.0.1", "Wildcard listener must connect over loopback");
        Check(ClashEndpoint.Parse("external-controller: '[::1]:9090'").Address.IsLoopback, "IPv6 loopback");
        foreach (string bad in new[] { "example.com:9090", "192.168.1.1:9090", "127.0.0.1.evil.example:9090", "user@127.0.0.1:9090", "127.0.0.1:9090/elsewhere" })
        {
            bool rejected = false;
            try { ClashEndpoint.Parse("external-controller: " + bad); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Reject remote/malformed controllers before sending a secret");
        }
        using var data = JsonDocument.Parse("""
        {
          "GLOBAL":{"type":"Selector","all":["A","DIRECT"],"now":"A"},
          "Fallback":{"type":"Selector","all":["A"],"now":"A"},
          "A":{"type":"URLTest","all":["Node","DIRECT"],"now":"Node"},
          "Node":{"type":"Shadowsocks"},
          "DIRECT":{"type":"Direct"},
          "REJECT":{"type":"Reject"},
          "Loop":{"type":"Selector","all":["Loop"],"now":"Loop"},
          "Balanced":{"type":"LoadBalance","all":["Node"]}
        }
        """);
        var proxies = data.RootElement;
        Check(!ClashMonitor.Resolve(proxies, "unknown", "GLOBAL", "").Available, "Unknown modes are unavailable");
        var global = ClashMonitor.Resolve(proxies, "global", "", "");
        Check(global.Available && global.Node == "Node" && global.Group == "GLOBAL", "Follow nested current selections");
        var rule = ClashMonitor.Resolve(proxies, "rule", "", "Fallback");
        Check(rule.Available && rule.Group == "Fallback" && rule.Node == "Node", "Rule mode uses the final group");
        Check(!ClashMonitor.Resolve(proxies, "direct", "GLOBAL", "").Available, "Direct mode is not VPN latency");
        Check(!ClashMonitor.Resolve(proxies, "rule", "", "").Available, "Do not infer one global node in rule mode");
        Check(!ClashMonitor.Resolve(proxies, "rule", "missing", "Fallback").Available, "Do not silently change a missing user selection");
        Check(!ClashMonitor.Resolve(proxies, "rule", "DIRECT", "").Available &&
            !ClashMonitor.Resolve(proxies, "rule", "REJECT", "").Available, "Direct/reject are not proxy nodes");
        Check(!ClashMonitor.Resolve(proxies, "rule", "Loop", "").Available &&
            !ClashMonitor.Resolve(proxies, "rule", "Balanced", "").Available, "Cycles/load balancing do not have a unique node");
    }
}