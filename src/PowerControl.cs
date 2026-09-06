using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThermalDot;

public sealed class PowerState
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string? Selected { get; set; }
    public string? Effective { get; set; }
    public int? BatteryPercent { get; set; }
    public bool? PluggedIn { get; set; }
    public bool Charging { get; set; }
    public string Error { get; set; } = "";
}

public sealed class ChargeState
{
    public DateTime Time { get; set; } = DateTime.Now;
    public int? Mode { get; set; }
    public bool Supported { get; set; }
    public bool Storage80 { get; set; }
    public string Error { get; set; } = "";
    public string Label => Mode switch { 0 => "正常充电", 1 => "养护充电", 2 => "快速充电", _ => "暂不可用" };
}

public static class PowerControl
{
    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static string? ModeName(Guid mode) => mode == Efficiency ? "省电" : mode == Guid.Empty ? "平衡" : mode == Performance ? "性能" : null;
    public static Guid ModeId(string mode) => mode switch { "省电" => Efficiency, "平衡" => Guid.Empty, "性能" => Performance, _ => throw new ArgumentException("无效电源模式") };

    public static PowerState Read()
    {
        var result = new PowerState();
        if (GetSystemPowerStatus(out var battery))
        {
            result.PluggedIn = battery.Ac == 255 ? null : battery.Ac == 1;
            if (battery.Percent <= 100 && (battery.Flags & 128) == 0) result.BatteryPercent = battery.Percent;
            result.Charging = battery.Flags != 255 && (battery.Flags & 8) != 0;
        }
        try
        {
            uint status = PowerGetActualOverlayScheme(out var selected);
            if (status == 0) result.Selected = ModeName(selected);
            else result.Error = "Windows 暂时无法读取电源模式（" + status + "）";
            if (PowerGetEffectiveOverlayScheme(out var effective) == 0) result.Effective = ModeName(effective);
        }
        catch (Exception e) { result.Error = "电源模式接口不可用：" + e.Message; }
        return result;
    }

    public static async Task<PowerState> Select(string requested)
    {
        Guid mode = ModeId(requested);
        try
        {
            uint status = PowerSetActiveOverlayScheme(mode);
            await Task.Delay(500);
            var after = Read();
            if (status != 0) after.Error = "切换未成功，Windows 返回 " + status;
            else if (after.Selected != requested) after.Error = "Windows 未确认切换，请到系统设置检查";
            return after;
        }
        catch (Exception e) { var state = Read(); state.Error = "未能切换：" + e.Message; return state; }
    }

    // Keep vendor code in a bounded, separate process. A DLL failure cannot stop temperature sampling.
    public static async Task<ChargeState> ReadCharge(int? requested = null)
    {
        if (requested.HasValue && requested != 0 && requested != 1) throw new ArgumentException("只支持正常或养护充电");
        try
        {
            string host = Environment.ProcessPath ?? throw new IOException("无法定位当前程序");
            var info = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            // Framework-dependent development builds need their DLL; standalone releases relaunch themselves.
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ThermalDot.dll"));
            info.ArgumentList.Add("--charge-helper");
            info.ArgumentList.Add(requested?.ToString() ?? "read");
            using var process = Process.Start(info) ?? throw new IOException("无法启动电池读取组件");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch { } return new ChargeState { Error = "联想组件响应超时，可打开联想设置检查" }; }
            string json = await output;
            await error;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return new ChargeState { Error = "联想组件暂不可用，可打开联想设置检查" };
            return JsonSerializer.Deserialize<ChargeState>(json) ?? new ChargeState { Error = "电池状态读取失败" };
        }
        catch (Exception e) { return new ChargeState { Error = "电池状态读取失败：" + e.Message }; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryStatus { public byte Ac, Flags, Percent, Saver; public uint Life, FullLife; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out BatteryStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid scheme);
}

internal static class LenovoCharge
{
    // This installed version was verified with Authenticode (Lenovo). No vendor binaries are redistributed.
    private const string ProviderHash = "D1EAB3B34699FE05E7E9A5AF2560FE7F576B4329394F868B7716085F7DB40D3F";
    public static ChargeState Execute(string action)
    {
        if (action != "read" && action != "0" && action != "1") return new ChargeState { Error = "无效充电操作" };
        IntPtr module = IntPtr.Zero, instance = IntPtr.Zero;
        NativeVoid? destroy = null;
        bool constructed = false;
        try
        {
            if (!Environment.Is64BitProcess) return new ChargeState { Error = "联想电池组件需要 64 位运行环境" };
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lenovo", "Vantage", "Addins", "IdeaNotebookAddin");
            if (!Directory.Exists(root)) return new ChargeState { Error = "未检测到兼容的联想组件；本机养护请使用原厂设置" };
            string? dll = Directory.GetDirectories(root).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.Combine(p, "PowerBattery.dll"))
                .FirstOrDefault(p => File.Exists(p) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) == ProviderHash);
            if (dll == null) return new ChargeState { Error = "此联想组件版本尚未验证，请使用原厂电池设置" };
            module = LoadLibraryEx(dll, IntPtr.Zero, 0x00001100);
            if (module == IntPtr.Zero) return new ChargeState { Error = "无法载入联想电池组件" };
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
            var create = Function<NativeVoid>("??0CChargingMode@PowerBattery@@QEAA@XZ");
            destroy = Function<NativeVoid>("??1CChargingMode@PowerBattery@@QEAA@XZ");
            var supported = Function<NativeGet>("?DoesSupportConservationMode@CChargingMode@PowerBattery@@QEBAHXZ");
            var read = Function<NativeGet>("?GetChargingMode@CChargingMode@PowerBattery@@QEBAHXZ");
            var storage80 = Function<NativeStatic>("?DoesSupportStorage80@CChargingMode@PowerBattery@@SAHXZ");
            var write = Function<NativeSet>("?SetChargingMode@CChargingMode@PowerBattery@@QEBAHH@Z");
            instance = Marshal.AllocHGlobal(512);
            Marshal.Copy(new byte[512], 0, instance, 512);
            create(instance); constructed = true;
            var result = new ChargeState { Supported = supported(instance) == 1, Storage80 = storage80() == 1 };
            int mode = read(instance);
            result.Mode = mode is >= 0 and <= 2 ? mode : null;
            if (action != "read")
            {
                if (!result.Supported || !result.Mode.HasValue) { result.Error = "联想未确认支持此操作"; return result; }
                int target = action == "1" ? 1 : 0;
                if (mode != target)
                {
                    write(instance, target);
                    Thread.Sleep(500);
                    mode = read(instance);
                    result.Mode = mode is >= 0 and <= 2 ? mode : null;
                }
                if (mode != target) result.Error = "联想未确认切换，请打开联想电池设置检查";
            }
            if (!result.Mode.HasValue) result.Error = "联想返回的充电状态无法识别";
            return result;
        }
        catch (Exception e) { return new ChargeState { Error = "联想电池接口不可用：" + e.Message }; }
        finally
        {
            if (constructed) destroy?.Invoke(instance);
            if (instance != IntPtr.Zero) Marshal.FreeHGlobal(instance);
            if (module != IntPtr.Zero) NativeLibrary.Free(module);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void NativeVoid(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NativeGet(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NativeSet(IntPtr self, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NativeStatic();
}
