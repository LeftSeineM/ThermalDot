using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ThermalDot;

public sealed class NetworkReading
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Adapter { get; set; } = "";
    public bool? Connected { get; set; }
    public double? Download { get; set; }
    public double? Upload { get; set; }
    public string Error { get; set; } = "";
}

public sealed class NetworkSampler
{
    private NetworkInterface? active;
    private string previousId = "";
    private long previousRx, previousTx, previousTick;
    private long discoveryTick;
    public static double? Rate(long previous, long current, double seconds)
        => previous >= 0 && current >= previous && seconds > 0 && seconds <= 5 && double.IsFinite(seconds)
            ? (current - previous) / seconds : null;
    public static string Speed(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value) || value < 0) return "—";
        double n = value.Value;
        return n >= 1_000_000_000 ? (n / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " GB/s"
            : n >= 1_000_000 ? (n / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s"
            : n >= 1_000 ? (n / 1_000).ToString("0.0", CultureInfo.InvariantCulture) + " KB/s"
            : Math.Round(n).ToString(CultureInfo.InvariantCulture) + " B/s";
    }
    private static bool Candidate(NetworkInterface n) =>
        n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
        !new[] { "virtual", "vmware", "vmnet", "tailscale", "wintun", "wireguard", "tap-windows", "loopback", "hyper-v" }
          .Any(part => (n.Description + " " + n.Name).Contains(part, StringComparison.OrdinalIgnoreCase));
    public NetworkReading Read()
    {
        var result = new NetworkReading();
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (active == null || active.OperationalStatus != OperationalStatus.Up ||
                Stopwatch.GetElapsedTime(discoveryTick, now).TotalSeconds >= 5)
            {
                var all = NetworkInterface.GetAllNetworkInterfaces().Where(Candidate).ToList();
                uint routeIndex = 0;
                try { GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()), out routeIndex); } catch { }
                active = all.Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Select(n => new { Nic = n, Props = n.GetIPProperties() })
                    .Where(n => n.Props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                    .OrderByDescending(n => { try { return n.Props.GetIPv4Properties()?.Index == routeIndex; } catch { return false; } })
                    .ThenBy(n => n.Nic.Id, StringComparer.Ordinal)
                    .Select(n => n.Nic).FirstOrDefault();
                discoveryTick = now;
                if (active == null)
                {
                    previousId = ""; previousTick = 0;
                    result.Connected = all.Count > 0 && all.All(n => n.OperationalStatus != OperationalStatus.Up) ? false : null;
                    result.Error = result.Connected == false ? "网络接口已断开" : "暂未找到可统计的主网络接口";
                    return result;
                }
            }
            var counters = active.GetIPStatistics();
            result.Adapter = active.Name; result.Connected = true;
            if (previousId == active.Id && previousTick != 0)
            {
                double elapsed = Stopwatch.GetElapsedTime(previousTick, now).TotalSeconds;
                result.Download = Rate(previousRx, counters.BytesReceived, elapsed);
                result.Upload = Rate(previousTx, counters.BytesSent, elapsed);
            }
            previousId = active.Id; previousRx = counters.BytesReceived; previousTx = counters.BytesSent; previousTick = now;
        }
        catch { previousId = ""; active = null; result.Error = "网速暂不可用，正在重新检测接口"; }
        return result;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterface(uint destination, out uint bestIndex);
}