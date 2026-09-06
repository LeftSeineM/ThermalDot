using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ThermalDot;

public sealed class Reading
{
    public DateTime Time { get; set; } = DateTime.Now;
    public double? Cpu { get; set; }
    public double? Gpu { get; set; }
    public double? CpuLoad { get; set; }
    public double? GpuLoad { get; set; }
    public double? MemoryUsedGiB { get; set; }
    public double? MemoryTotalGiB { get; set; }
    public double? VramUsedGiB { get; set; }
    public double? VramTotalGiB { get; set; }
    public double? MemoryLoad => SensorReader.CapacityPercent(MemoryUsedGiB, MemoryTotalGiB);
    public double? VramLoad => SensorReader.CapacityPercent(VramUsedGiB, VramTotalGiB);
    public string Version => "1.4.0";
    public string CpuName { get; set; } = "CPU";
    public string GpuName { get; set; } = "GPU";
    public string CpuSource { get; set; } = "";
    public string GpuSource { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class SensorReader : IDisposable
{
    private Computer? computer;
    private string initError = "";
    public static double? Valid(double? value) => value.HasValue && double.IsFinite(value.Value) && value >= 0 && value <= 125 ? value : null;
    public static double? Percent(double? value) => value.HasValue && double.IsFinite(value.Value) && value >= 0 && value <= 100 ? value : null;
    public static double? CapacityPercent(double? used, double? total) => used.HasValue && total.HasValue && double.IsFinite(used.Value) && double.IsFinite(total.Value) && total > 0 && used >= 0 && used <= total ? Percent(used / total * 100) : null;
    private static double? Sensor(IHardware hardware, SensorType type, string name) => hardware.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
    public void Open()
    {
        try
        {
            computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            computer.Open();
        }
        catch (Exception e) { initError = e.GetType().Name + ": " + e.Message; }
    }
    public Reading Read()
    {
        var r = new Reading { Error = initError };
        try
        {
            if (computer != null)
                foreach (var h in computer.Hardware.OrderBy(h => h.HardwareType == HardwareType.GpuNvidia ? 0 : h.HardwareType == HardwareType.GpuAmd ? 1 : h.HardwareType == HardwareType.GpuIntel ? 2 : 3))
                {
                    h.Update();
                    if (h.HardwareType == HardwareType.Cpu)
                    {
                        r.CpuName = h.Name;
                        r.CpuLoad = Percent(Sensor(h, SensorType.Load, "CPU Total"));
                        var temps = h.Sensors.Where(s => s.SensorType == SensorType.Temperature && Valid(s.Value).HasValue).ToList();
                        var package = temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                            ?? temps.FirstOrDefault(s => s.Name.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase));
                        if (package != null) { r.Cpu = Valid(package.Value); r.CpuSource = package.Name; }
                        else
                        {
                            var cores = temps.Where(s => s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) && !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)).ToList();
                            if (cores.Count > 0) { r.Cpu = cores.Max(s => (double?)s.Value); r.CpuSource = "最高核心温度"; }
                        }
                    }
                    if ((h.HardwareType == HardwareType.GpuNvidia || h.HardwareType == HardwareType.GpuAmd || h.HardwareType == HardwareType.GpuIntel) && r.GpuName == "GPU")
                    {
                        r.GpuName = h.Name;
                        var core = h.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase));
                        if (core != null) { r.Gpu = Valid(core.Value); r.GpuSource = "GPU Core"; }
                        r.GpuLoad = Percent(Sensor(h, SensorType.Load, "GPU Core"));
                        // LHM SmallData is MiB. Total minus available includes driver allocations.
                        var usedGiB = Sensor(h, SensorType.SmallData, "GPU Memory Used") / 1024.0;
                        var totalGiB = Sensor(h, SensorType.SmallData, "GPU Memory Total") / 1024.0;
                        if (CapacityPercent(usedGiB, totalGiB).HasValue) { r.VramUsedGiB = usedGiB; r.VramTotalGiB = totalGiB; }
                    }
                }
        }
        catch (Exception e) { r.Error = e.GetType().Name + ": " + e.Message; }
        if ((r.GpuName == "GPU" || r.GpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) && (!r.Gpu.HasValue || !r.GpuLoad.HasValue || !r.VramLoad.HasValue)) ReadNvidia(r);
        ReadSystemMemory(r);
        r.Time = DateTime.Now;
        return r;
    }
    private static void ReadNvidia(Reading r)
    {
        try
        {
            var exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!File.Exists(exe)) exe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            if (!File.Exists(exe)) return;
            using var p = Process.Start(new ProcessStartInfo(exe, "--query-gpu=name,temperature.gpu,utilization.gpu,memory.free,memory.total --format=csv,noheader,nounits")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return;
            if (!p.WaitForExit(2000)) { p.Kill(); return; }
            var line = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (p.ExitCode != 0 || line == null) return;
            ApplyNvidiaCsv(r, line);
        }
        catch { }
    }
    public static void ApplyNvidiaCsv(Reading r, string line)
    {
        var fields = line.Split(',').Select(v => v.Trim()).ToArray();
        if (fields.Length != 5) return;
        double? Number(int i) => double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        if (!r.Gpu.HasValue) { r.Gpu = Valid(Number(1)); if (r.Gpu.HasValue) { r.GpuName = fields[0]; r.GpuSource = "NVIDIA 驱动"; } }
        r.GpuLoad ??= Percent(Number(2));
        var total = Number(4) / 1024.0;
        var used = total - Number(3) / 1024.0;
        if (!r.VramLoad.HasValue && CapacityPercent(used, total).HasValue) { r.VramUsedGiB = used; r.VramTotalGiB = total; }
    }
    private static void ReadSystemMemory(Reading r)
    {
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref state) && state.TotalPhysical > 0 && state.AvailablePhysical <= state.TotalPhysical)
        {
            r.MemoryTotalGiB = state.TotalPhysical / 1073741824.0;
            r.MemoryUsedGiB = (state.TotalPhysical - state.AvailablePhysical) / 1073741824.0;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus state);
    public string Report() { try { return computer?.GetReport() ?? initError; } catch { return initError; } }
    public void Dispose() { try { computer?.Close(); } catch { } }
}

public sealed class Preferences
{
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Size { get; set; } = 96;
    public bool Locked { get; set; }
    public string Display { get; set; } = "auto";
}

public static class Program
{
    public static readonly string Root = AppContext.BaseDirectory;
    public static readonly string Data = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThermalDot");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--charge-helper")
        {
            Console.WriteLine(JsonSerializer.Serialize(LenovoCharge.Execute(args[1])));
            return;
        }
        if (args.Length == 2 && args[0] == "--screen-probe")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(DisplayTimeout.Read(), Json)); return;
        }
        if (args.Length == 3 && args[0] == "--render-screen")
        {
            var appPreview = new System.Windows.Application();
            var preview = new DotWindow(preview: true);
            preview.RenderScreenPreview(JsonSerializer.Deserialize<DisplayTimeoutState>(File.ReadAllText(args[1])) ?? new(), args[2]);
            preview.Close(); appPreview.Shutdown(); return;
        }
        if (args.Length == 2 && args[0] == "--power-probe")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { Power = PowerControl.Read(), Charge = PowerControl.ReadCharge().GetAwaiter().GetResult(), Screen = DisplayTimeout.Read() }, Json));
            return;
        }
        if (args.Length == 3 && args[0] == "--render-power")
        {
            var appPreview = new System.Windows.Application();
            using var json = JsonDocument.Parse(File.ReadAllText(args[1]));
            var preview = new DotWindow(preview: true);
            preview.RenderPowerPreview(json.RootElement.GetProperty("Power").Deserialize<PowerState>() ?? new(), json.RootElement.GetProperty("Charge").Deserialize<ChargeState>() ?? new(), args[2]);
            preview.Close(); appPreview.Shutdown(); return;
        }
        try { Directory.CreateDirectory(Data); }
        catch (Exception e) { System.Windows.MessageBox.Show("无法创建温度球的数据目录：" + e.Message, "温度球"); return; }
        if (args.Length == 2 && args[0] == "--probe")
        {
            using var reader = new SensorReader(); reader.Open();
            var samples = new List<Reading>();
            for (int i = 0; i < 4; i++) { samples.Add(reader.Read()); Thread.Sleep(1000); }
            File.WriteAllText(args[1], JsonSerializer.Serialize(samples, Json));
            File.WriteAllText(System.IO.Path.ChangeExtension(args[1], ".txt"), reader.Report());
            return;
        }
        if (args.Length == 1 && args[0] == "--self-test")
        {
            DisplayTimeoutTests.Run();
            if (PowerControl.ModeName(PowerControl.ModeId("省电")) != "省电" || PowerControl.ModeName(PowerControl.ModeId("平衡")) != "平衡" || PowerControl.ModeName(PowerControl.ModeId("性能")) != "性能" || PowerControl.ModeName(Guid.NewGuid()) != null)
                throw new Exception("Unknown Windows power modes must not be mislabeled");
            if (LenovoCharge.Execute("9").Mode.HasValue || LenovoCharge.Execute("9").Error == "") throw new Exception("Invalid charging writes must be rejected before loading vendor code");
            if (SensorReader.Valid(double.NaN) != null || SensorReader.Valid(-1) != null || SensorReader.Valid(500) != null || SensorReader.Valid(51) != 51)
                throw new Exception("Invalid sensor values were not rejected");
            if (DotWindow.Risk(94, "CPU") != 1 || DotWindow.Risk(96, "CPU") != 2 || DotWindow.Risk(84, "GPU") != 1 || DotWindow.Risk(86, "GPU") != 2)
                throw new Exception("Temperature attention thresholds are incorrect");
            if (SensorReader.Percent(0) != 0 || SensorReader.Percent(100) != 100 || SensorReader.Percent(101) != null || SensorReader.Percent(null) != null || SensorReader.Percent(double.NaN) != null)
                throw new Exception("Invalid load values were not rejected");
            if (SensorReader.CapacityPercent(24, 96) != 25 || SensorReader.CapacityPercent(0, 8) != 0 || SensorReader.CapacityPercent(9, 8) != null || SensorReader.CapacityPercent(1, 0) != null || SensorReader.CapacityPercent(null, 96) != null)
                throw new Exception("Capacity conversion incorrectly treats invalid data as a measurement");
            var parsed = new Reading(); SensorReader.ApplyNvidiaCsv(parsed, "NVIDIA RTX 4060, 55, 26, 7680, 8192");
            if (parsed.Gpu != 55 || parsed.GpuLoad != 26 || parsed.VramUsedGiB != .5 || parsed.VramTotalGiB != 8 || parsed.VramLoad != 6.25)
                throw new Exception("NVIDIA fallback conversion failed");
            var missing = new Reading(); SensorReader.ApplyNvidiaCsv(missing, "NVIDIA RTX 4060, N/A, N/A, N/A, 8192");
            if (missing.Gpu.HasValue || missing.GpuLoad.HasValue || missing.VramLoad.HasValue)
                throw new Exception("N/A must stay unavailable");
            File.WriteAllText(System.IO.Path.Combine(Data, "self-test.txt"), "PASS: invalid readings, zero/full/missing capacity, MiB-to-GiB conversion, NVIDIA fallback/N/A, CPU/GPU thresholds; screen timeouts: never/custom, AC/DC isolation, rollback, stale-plan protection, readback verification");
            return;
        }
        if (args.Length == 3 && args[0] == "--render")
        {
            var appPreview = new System.Windows.Application();
            var reading = JsonSerializer.Deserialize<Reading>(File.ReadAllText(args[1])) ?? new Reading();
            var preview = new DotWindow(preview: true);
            preview.RenderPreview(reading, args[2]);
            preview.Close(); appPreview.Shutdown();
            return;
        }
        if (args.Length == 0 && Distribution.TryStartElevated()) return;
        using var mutex = new Mutex(true, "Local\\ThermalDot-Standalone-" + Environment.UserName, out bool isNew);
        if (!isNew)
        {
            // A second launch signals the first instance without adding another sampler.
            File.WriteAllText(System.IO.Path.Combine(Data, "show.request"), DateTime.UtcNow.ToString("O"));
            return;
        }
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try { File.WriteAllText(System.IO.Path.Combine(Data, "error.txt"), e.Exception.ToString()); } catch { }
            System.Windows.MessageBox.Show("温度球遇到了问题，请重新打开。", "温度球");
            e.Handled = true; app.Shutdown(1);
        };
        var win = new DotWindow();
        app.Run(win);
    }
}

public sealed partial class DotWindow : Window
{
    private readonly Preferences prefs;
    private readonly Grid face;
    private readonly TextBlock number, source, foot;
    private readonly System.Windows.Shapes.Path arc;
    private readonly Ellipse status;
    private readonly Window panel;
    private readonly Grid detail;
    private readonly Forms.NotifyIcon? tray;
    private readonly CancellationTokenSource cancel = new();
    private readonly DispatcherTimer? watchdog;
    private readonly bool isPreview;
    private readonly List<Reading> history = new();
    private Reading latest = new();
    private double? maxCpu, maxGpu;
    private bool closing, dragging;
    private Task? sampling;
    private Point pressedAt;
    private string showRequest = "";
    private readonly string prefsPath = System.IO.Path.Combine(Program.Data, "preferences.json");
    private static readonly Color Mint = (Color)ColorConverter.ConvertFromString("#67E7C1");
    private static readonly Color Blue = (Color)ColorConverter.ConvertFromString("#88B9FF");
    public static int Risk(double t, string component) => component == "CPU" ? (t >= 95 ? 2 : t >= 85 ? 1 : 0) : (t >= 85 ? 2 : t >= 80 ? 1 : 0);
    private static Brush Accent(int risk) => new SolidColorBrush(risk == 2 ? Color.FromRgb(255, 112, 112) : risk == 1 ? Color.FromRgb(255, 198, 105) : Mint);
    private static string Temp(double? t) => t.HasValue ? Math.Round(t.Value).ToString(CultureInfo.InvariantCulture) + "°" : "—";
    private static string Pct(double? p) => p.HasValue ? Math.Round(p.Value).ToString(CultureInfo.InvariantCulture) + "%" : "—";
    private static string Capacity(double? used, double? total) => SensorReader.CapacityPercent(used, total).HasValue ? used!.Value.ToString("0.0", CultureInfo.InvariantCulture) + " / " + total!.Value.ToString("0.0", CultureInfo.InvariantCulture) + " GiB" : "暂不可用";
    public DotWindow(bool preview = false)
    {
        isPreview = preview;
        try { prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(prefsPath)) ?? new(); } catch { prefs = new(); }
        Title = "温度球 · Thermal Dot"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        Width = Height = Math.Clamp(prefs.Size, 80, 116);
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        face = (Grid)XamlReader.Parse(FaceXaml);
        Content = new Viewbox { Child = face, Stretch = Stretch.Uniform };
        number = (TextBlock)face.FindName("Number"); source = (TextBlock)face.FindName("Source"); foot = (TextBlock)face.FindName("Foot");
        arc = (System.Windows.Shapes.Path)face.FindName("Arc"); status = (Ellipse)face.FindName("Status");
        var wa = SystemParameters.WorkArea;
        Left = prefs.Left < 0 ? wa.Right - Width - 22 : prefs.Left;
        Top = prefs.Top < 0 ? wa.Top + wa.Height * .42 : prefs.Top;
        ClampToScreen();
        detail = (Grid)XamlReader.Parse(PanelXaml);
        panel = new Window { Title = "温度与资源详情", Width = 376, Height = 490, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, Content = detail, FontFamily = FontFamily };
        InitializePowerView();
        ((Button)detail.FindName("ClosePanel")).Click += (_, _) => panel.Hide();
        panel.Deactivated += (_, _) => { if (!IsMouseOver && !IsScreenDropDownOpen) panel.Hide(); };
        MouseLeftButtonDown += (_, e) => { pressedAt = e.GetPosition(this); dragging = false; CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured && !prefs.Locked && (e.GetPosition(this) - pressedAt).Length > 5)
            {
                dragging = true; ReleaseMouseCapture(); panel.Hide();
                try { DragMove(); } catch { }
                ClampToScreen(); Save();
            }
        };
        MouseLeftButtonUp += (_, _) => { ReleaseMouseCapture(); if (!dragging) TogglePanel(); dragging = false; };
        MouseRightButtonUp += (_, _) => { ContextMenu = MakeMenu(); ContextMenu.IsOpen = true; };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) panel.Hide(); };
        Loaded += (_, _) => { sampling = Task.Run(SampleLoop); };
        Closed += (_, _) => { if (!isPreview) Quit(); };
        if (preview) return;
        tray = new Forms.NotifyIcon { Text = "温度球 · 正在读取", Icon = MakeTrayIcon(), Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); }));
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示温度球", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); })));
        trayMenu.Items.Add("温度详情", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { Show(); TogglePanel(true); })));
        trayMenu.Items.Add("退出", null, (_, _) => Dispatcher.BeginInvoke(new Action(Quit)));
        tray.ContextMenuStrip = trayMenu;
        watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        watchdog.Tick += (_, _) =>
        {
            if ((DateTime.Now - latest.Time).TotalSeconds > 12) UpdateUi(true);
            var requestFile = System.IO.Path.Combine(Program.Data, "show.request");
            try { var req = File.Exists(requestFile) ? File.ReadAllText(requestFile) : ""; if (req != "" && req != showRequest) { showRequest = req; Show(); Activate(); } } catch { }
        };
        watchdog.Start();
    }
    private async Task SampleLoop()
    {
        using var reader = new SensorReader(); reader.Open();
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var cycle = Stopwatch.StartNew();
                var reading = reader.Read();
                await Dispatcher.InvokeAsync(() =>
                {
                    latest = reading; history.Add(reading); if (history.Count > 40) history.RemoveAt(0);
                    if (reading.Cpu.HasValue) maxCpu = Math.Max(maxCpu ?? 0, reading.Cpu.Value);
                    if (reading.Gpu.HasValue) maxGpu = Math.Max(maxGpu ?? 0, reading.Gpu.Value);
                    UpdateUi();
                });
                try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "latest.json"), JsonSerializer.Serialize(reading, Program.Json)); } catch { }
                await Task.Delay((int)Math.Max(100, 3000 - cycle.ElapsedMilliseconds), cancel.Token);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { try { File.WriteAllText(System.IO.Path.Combine(Program.Data, "sensor-error.txt"), ex.ToString()); } catch { } }
    }
    private void UpdateUi(bool stale = false)
    {
        double? cpu = stale ? null : latest.Cpu, gpu = stale ? null : latest.Gpu;
        string who; double? value;
        if (prefs.Display == "cpu") { who = "CPU"; value = cpu; }
        else if (prefs.Display == "gpu") { who = "GPU"; value = gpu; }
        else if (cpu.HasValue && (!gpu.HasValue || cpu.Value / 95 >= gpu.Value / 85)) { who = "CPU"; value = cpu; }
        else { who = "GPU"; value = gpu; }
        number.Text = Temp(value); source.Text = who;
        int risk = value.HasValue ? Risk(value.Value, who) : -1;
        var accent = risk < 0 ? new SolidColorBrush(Color.FromRgb(116, 134, 151)) : Accent(risk);
        number.Foreground = Brushes.White; arc.Stroke = accent; status.Fill = accent;
        double? load = stale ? null : who == "CPU" ? latest.CpuLoad : latest.GpuLoad;
        foot.Text = stale ? "已断开" : "负载 " + Pct(load);
        double sweep = load.HasValue ? load.Value / 100 * 300 : 0;
        arc.Data = RingArc(sweep);
        ToolTip = "CPU " + Temp(cpu) + "C · " + Pct(stale ? null : latest.CpuLoad) + "  |  GPU " + Temp(gpu) + "C · " + Pct(stale ? null : latest.GpuLoad) + "\n外圈长度：负载 · 颜色：温度\n单击展开 · 拖动移动 · 右键设置";
        if (tray != null) tray.Text = ("温度球 | CPU " + Temp(cpu) + "C | GPU " + Temp(gpu) + "C")[..Math.Min(63, ("温度球 | CPU " + Temp(cpu) + "C | GPU " + Temp(gpu) + "C").Length)];
        Set("CpuTemp", Temp(cpu)); Set("GpuTemp", Temp(gpu));
        Set("CpuMax", "本次最高  " + Temp(maxCpu)); Set("GpuMax", "本次最高  " + Temp(maxGpu));
        Set("CpuName", latest.CpuName.Replace("Intel(R) Core(TM) ", ""));
        Set("GpuName", latest.GpuName.Replace("NVIDIA GeForce ", ""));
        Set("CpuLoad", Pct(stale ? null : latest.CpuLoad)); Set("GpuLoad", Pct(stale ? null : latest.GpuLoad));
        SetBar("CpuBar", stale ? null : latest.CpuLoad); SetBar("GpuBar", stale ? null : latest.GpuLoad);
        Set("MemoryPercent", Pct(stale ? null : latest.MemoryLoad)); Set("VramPercent", Pct(stale ? null : latest.VramLoad));
        Set("MemoryCapacity", stale ? "暂不可用" : Capacity(latest.MemoryUsedGiB, latest.MemoryTotalGiB));
        Set("VramCapacity", stale ? "暂不可用" : Capacity(latest.VramUsedGiB, latest.VramTotalGiB));
        SetBar("MemoryBar", stale ? null : latest.MemoryLoad); SetBar("VramBar", stale ? null : latest.VramLoad);
        Set("State", stale ? "数据已断开，等待重新读取" : !cpu.HasValue && !gpu.HasValue ? "温度传感器未连接" : !cpu.HasValue ? "CPU 温度未连接 · 显卡正在监测" : !gpu.HasValue ? "显卡温度未连接 · CPU 正在监测" : risk == 2 ? "温度较高，建议检查任务负载与通风" : risk == 1 ? "温度偏高，留意是否持续上升" : "温度与资源占用正在监测");
        Set("Updated", stale ? "没有新读数" : "每 3 秒更新 · " + latest.Time.ToString("HH:mm:ss"));
        DrawChart();
    }
    private void Set(string name, string text) => ((TextBlock)detail.FindName(name)).Text = text;
    private void SetBar(string name, double? value)
    {
        var bar = (ProgressBar)detail.FindName(name); bar.Value = value ?? 0; bar.Opacity = value.HasValue ? 1 : .3;
        bar.ToolTip = value.HasValue ? Pct(value) : "数据不可用";
    }
    private void DrawChart()
    {
        var chart = (Canvas)detail.FindName("Chart"); chart.Children.Clear();
        foreach (double y in new[] { 10d, 30d, 50d })
            chart.Children.Add(new Line { X1 = 0, X2 = 320, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), StrokeThickness = 1 });
        for (int component = 0; component < 2; component++)
        {
            Polyline? line = null;
            for (int i = 0; i < history.Count; i++)
            {
                double? v = component == 0 ? history[i].Cpu : history[i].Gpu;
                if (!v.HasValue) { line = null; continue; }
                if (line == null) { line = new Polyline { Stroke = new SolidColorBrush(component == 0 ? Mint : Blue), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round }; chart.Children.Add(line); }
                line.Points.Add(new Point(i * 320d / 39, 60 - Math.Clamp(v.Value, 0, 110) / 110d * 60));
            }
        }
    }
    private static Geometry RingArc(double sweep)
    {
        if (sweep <= 0) return Geometry.Empty;
        double start = 120 * Math.PI / 180, end = (120 + sweep) * Math.PI / 180;
        var p1 = new Point(50 + 36 * Math.Cos(start), 50 + 36 * Math.Sin(start));
        var p2 = new Point(50 + 36 * Math.Cos(end), 50 + 36 * Math.Sin(end));
        var g = new StreamGeometry(); using (var c = g.Open()) { c.BeginFigure(p1, false, false); c.ArcTo(p2, new Size(36, 36), 0, sweep > 180, SweepDirection.Clockwise, true, false); } g.Freeze(); return g;
    }
    public void RenderPreview(Reading reading, string output)
    {
        latest = reading; maxCpu = reading.Cpu; maxGpu = reading.Gpu; history.Add(reading); UpdateUi();
        face.Measure(new Size(100, 100)); face.Arrange(new Rect(0, 0, 100, 100)); face.UpdateLayout();
        detail.Width = panel.Width; detail.Height = panel.Height;
        detail.Measure(new Size(panel.Width, panel.Height)); detail.Arrange(new Rect(0, 0, panel.Width, panel.Height)); detail.UpdateLayout();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(11, 18, 29)), null, new Rect(0, 0, 536, 522));
            dc.DrawRectangle(new VisualBrush(detail) { Stretch = Stretch.Fill }, null, new Rect(12, 16, 376, 490));
            dc.DrawRectangle(new VisualBrush(face) { Stretch = Stretch.Fill }, null, new Rect(405, 205, 116, 116));
        }
        var bitmap = new RenderTargetBitmap(1072, 1044, 192, 192, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(output); encoder.Save(stream);
    }
    private void TogglePanel(bool force = false)
    {
        if (panel.IsVisible && !force) { panel.Hide(); return; }
        PositionPanel(); panel.Show(); panel.Activate();
    }
    private void PositionPanel()
    {
        var screen = ScreenWorkArea();
        panel.Left = Left >= screen.Left + panel.Width ? Left - panel.Width + 3 : Left + Width - 3;
        panel.Left = Math.Clamp(panel.Left, screen.Left, Math.Max(screen.Left, screen.Right - panel.Width));
        panel.Top = Math.Clamp(Top + Height / 2 - panel.Height / 2, screen.Top, Math.Max(screen.Top, screen.Bottom - panel.Height));
    }
    private Rect ScreenWorkArea()
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var screen = Forms.Screen.FromPoint(new Drawing.Point((int)((Left + Width / 2) * transform.M11), (int)((Top + Height / 2) * transform.M22))).WorkingArea;
        return new Rect(screen.Left / transform.M11, screen.Top / transform.M22, screen.Width / transform.M11, screen.Height / transform.M22);
    }
    private void ClampToScreen()
    {
        var r = ScreenWorkArea(); Left = Math.Clamp(Left, r.Left, Math.Max(r.Left, r.Right - Width)); Top = Math.Clamp(Top, r.Top, Math.Max(r.Top, r.Bottom - Height));
    }
    private void Save() { try { prefs.Left = Left; prefs.Top = Top; prefs.Size = Width; File.WriteAllText(prefsPath, JsonSerializer.Serialize(prefs, Program.Json)); } catch { } }
    private ContextMenu MakeMenu()
    {
        var menu = new ContextMenu();
        void Item(string label, Action action, bool isChecked = false) { var i = new MenuItem { Header = label, IsChecked = isChecked }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        Item("查看温度详情", () => TogglePanel(true));
        Item("电源与电池", () => { TogglePanel(true); ShowPowerView(); });
        Item("屏幕熄灭时间", () => { TogglePanel(true); ShowScreenView(); });
        Item("CPU 温度读取组件…", Distribution.OpenCpuComponent);
        Item("使用说明 / 关于", Distribution.ShowHelp);
        Item("第三方许可", Distribution.ShowLicenses);
        menu.Items.Add(new Separator());
        Item("自动选择需关注的温度", () => { prefs.Display = "auto"; Save(); UpdateUi(); }, prefs.Display == "auto");
        Item("圆球显示 CPU", () => { prefs.Display = "cpu"; Save(); UpdateUi(); }, prefs.Display == "cpu");
        Item("圆球显示显卡", () => { prefs.Display = "gpu"; Save(); UpdateUi(); }, prefs.Display == "gpu");
        menu.Items.Add(new Separator());
        Item("小一点", () => { Width = Height = 80; Save(); ClampToScreen(); });
        Item("标准大小", () => { Width = Height = 96; Save(); ClampToScreen(); });
        Item("大一点", () => { Width = Height = 116; Save(); ClampToScreen(); });
        Item("锁定位置", () => { prefs.Locked = !prefs.Locked; Save(); }, prefs.Locked);
        menu.Items.Add(new Separator());
        Item("重置本次最高温度", () => { maxCpu = latest.Cpu; maxGpu = latest.Gpu; UpdateUi(); });
        Item("暂时收起（托盘可恢复）", () => { panel.Hide(); Hide(); });
        Item("退出温度球", Quit);
        return menu;
    }
    private async void Quit()
    {
        if (closing) return; closing = true; Save(); cancel.Cancel(); watchdog?.Stop(); powerTimer?.Stop();
        if (tray != null) { tray.Visible = false; tray.Dispose(); } panel.Close(); Hide();
        if (sampling != null) { try { await sampling.WaitAsync(TimeSpan.FromSeconds(4)); } catch { } }
        System.Windows.Application.Current.Shutdown();
    }
    private static Drawing.Icon MakeTrayIcon()
    {
        using var b = new Drawing.Bitmap(32, 32); using var g = Drawing.Graphics.FromImage(b);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);
        using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 22, 34, 45)); g.FillEllipse(bg, 1, 1, 30, 30);
        using var pen = new Drawing.Pen(Drawing.Color.FromArgb(103, 231, 193), 3); g.DrawArc(pen, 4, 4, 24, 24, 120, 280);
        using var dot = new Drawing.SolidBrush(Drawing.Color.White); g.FillEllipse(dot, 12, 12, 8, 8);
        var h = b.GetHicon(); var icon = (Drawing.Icon)Drawing.Icon.FromHandle(h).Clone(); DestroyIcon(h); return icon;
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private const string FaceXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="100" Height="100" Background="Transparent">
  <Ellipse Width="82" Height="82" Stroke="#374B60" StrokeThickness="0.8">
    <Ellipse.Fill><RadialGradientBrush GradientOrigin="0.28,0.2" Center="0.4,0.3" RadiusX="0.85" RadiusY="0.85"><GradientStop Color="#F22C4152" Offset="0"/><GradientStop Color="#F0111C28" Offset="1"/></RadialGradientBrush></Ellipse.Fill>
    <Ellipse.Effect><DropShadowEffect BlurRadius="14" ShadowDepth="3" Opacity="0.35" Color="#071523"/></Ellipse.Effect>
  </Ellipse>
  <Ellipse Width="72" Height="72" Stroke="#293A48" StrokeThickness="3"/>
  <Path x:Name="Arc" Stroke="#67E7C1" StrokeThickness="3.2" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
  <StackPanel VerticalAlignment="Center" Margin="0,0,0,0">
    <TextBlock x:Name="Source" Text="温度" Foreground="#96AEBF" FontFamily="Segoe UI" FontSize="9" FontWeight="SemiBold" HorizontalAlignment="Center"/>
    <TextBlock x:Name="Number" Text="—" Foreground="White" FontFamily="Segoe UI" FontSize="28" FontWeight="SemiBold" HorizontalAlignment="Center" Margin="2,-3,0,-1"/>
    <TextBlock x:Name="Foot" Text="连接中" Foreground="#98B7BD" FontSize="8" HorizontalAlignment="Center"/>
  </StackPanel>
  <Ellipse x:Name="Status" Width="8" Height="8" Fill="#7E93A8" Stroke="#182733" StrokeThickness="2" HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,16,16"/>
</Grid>
""";
    private const string PanelXaml = """
<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="Transparent">
 <Grid.Resources>
  <Style TargetType="ProgressBar">
   <Setter Property="Minimum" Value="0"/><Setter Property="Maximum" Value="100"/><Setter Property="Height" Value="4"/>
   <Setter Property="Background" Value="#344857"/><Setter Property="BorderThickness" Value="0"/>
   <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ProgressBar">
    <Grid x:Name="PART_Track"><Border Background="{TemplateBinding Background}" CornerRadius="2"/>
     <Border x:Name="PART_Indicator" HorizontalAlignment="Left" Background="{TemplateBinding Foreground}" CornerRadius="2"/>
    </Grid>
   </ControlTemplate></Setter.Value></Setter>
  </Style>
 </Grid.Resources>
 <Border Margin="9" CornerRadius="22" Background="#F41B2736" BorderBrush="#425263" BorderThickness="0.7">
  <Border.Effect><DropShadowEffect BlurRadius="16" ShadowDepth="3" Opacity="0.3"/></Border.Effect>
  <Grid Margin="18,16,18,15">
   <Grid.RowDefinitions><RowDefinition Height="35"/><RowDefinition Height="138"/><RowDefinition Height="10"/><RowDefinition Height="103"/><RowDefinition Height="25"/><RowDefinition Height="63"/><RowDefinition Height="*"/></Grid.RowDefinitions>
   <TextBlock Text="温度与资源" FontSize="17" FontWeight="SemiBold" Foreground="#ECF4F9"/>
   <Button x:Name="OpenPower" Content="电源 / 电池" FontSize="10" Foreground="#91DAC7" Background="Transparent" BorderThickness="0" Width="75" Height="25" VerticalAlignment="Top" HorizontalAlignment="Right" Margin="0,0,31,0" ToolTip="切换电源模式与充电养护"/>
   <Button x:Name="ClosePanel" Content="×" FontSize="19" Foreground="#92AABB" Background="Transparent" BorderThickness="0" Width="24" Height="24" VerticalAlignment="Top" HorizontalAlignment="Right" ToolTip="收起详情"/>
   <Grid Grid.Row="1">
    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
    <Border Background="#283B47" CornerRadius="13" Padding="12,10">
     <StackPanel><TextBlock Text="●  CPU" Foreground="#67E7C1" FontSize="10" FontWeight="SemiBold"/>
      <TextBlock x:Name="CpuTemp" Text="—" Foreground="#EFF8F5" FontFamily="Segoe UI" FontSize="29" FontWeight="SemiBold" Margin="0,1,0,0"/>
      <TextBlock x:Name="CpuName" Text="CPU" Foreground="#9CAEBB" FontSize="8.5" TextTrimming="CharacterEllipsis" ToolTip="CPU 封装温度；不可用时显示最高核心温度"/>
      <TextBlock x:Name="CpuMax" Text="本次最高  —" Foreground="#769391" FontSize="9" Margin="0,3,0,0"/>
      <Grid Margin="0,7,0,4"><TextBlock Text="负载" Foreground="#A1B9BA" FontSize="10"/><TextBlock x:Name="CpuLoad" Text="—" Foreground="#D4EAE3" FontSize="10" HorizontalAlignment="Right"/></Grid>
      <ProgressBar x:Name="CpuBar" Foreground="#67E7C1" ToolTip="所有 CPU 线程的平均占用率"/>
     </StackPanel>
    </Border>
    <Border Grid.Column="2" Background="#293749" CornerRadius="13" Padding="12,10">
     <StackPanel><TextBlock Text="●  GPU" Foreground="#88B9FF" FontSize="10" FontWeight="SemiBold"/>
      <TextBlock x:Name="GpuTemp" Text="—" Foreground="#EFF4FF" FontFamily="Segoe UI" FontSize="29" FontWeight="SemiBold" Margin="0,1,0,0"/>
      <TextBlock x:Name="GpuName" Text="GPU" Foreground="#9CAEBB" FontSize="8.5" TextTrimming="CharacterEllipsis" ToolTip="显卡核心温度"/>
      <TextBlock x:Name="GpuMax" Text="本次最高  —" Foreground="#7D94AE" FontSize="9" Margin="0,3,0,0"/>
      <Grid Margin="0,7,0,4"><TextBlock Text="负载" Foreground="#A1AFC3" FontSize="10"/><TextBlock x:Name="GpuLoad" Text="—" Foreground="#D4E2F8" FontSize="10" HorizontalAlignment="Right"/></Grid>
      <ProgressBar x:Name="GpuBar" Foreground="#88B9FF" ToolTip="NVIDIA 显卡核心占用率"/>
     </StackPanel>
    </Border>
   </Grid>
   <Grid Grid.Row="3">
    <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width="10"/><ColumnDefinition/></Grid.ColumnDefinitions>
    <Border Background="#302E46" CornerRadius="13" Padding="12,10">
     <StackPanel>
      <TextBlock Text="●  内存 RAM" Foreground="#C2ABFA" FontSize="10" FontWeight="SemiBold"/>
      <TextBlock x:Name="MemoryPercent" Text="—" Foreground="#F2EBFF" FontFamily="Segoe UI" FontSize="25" FontWeight="SemiBold" Margin="0,0,0,0"/>
      <TextBlock x:Name="MemoryCapacity" Text="暂不可用" Foreground="#AA9CBE" FontSize="9" ToolTip="物理内存：已用 / Windows 可用总量（GiB）"/>
      <ProgressBar x:Name="MemoryBar" Foreground="#C2ABFA" Background="#433E58" Margin="0,7,0,0"/>
     </StackPanel>
    </Border>
    <Border Grid.Column="2" Background="#38332D" CornerRadius="13" Padding="12,10">
     <StackPanel>
      <TextBlock Text="●  显存 VRAM" Foreground="#E9BC80" FontSize="10" FontWeight="SemiBold"/>
      <TextBlock x:Name="VramPercent" Text="—" Foreground="#FFF2DE" FontFamily="Segoe UI" FontSize="25" FontWeight="SemiBold" Margin="0,0,0,0"/>
      <TextBlock x:Name="VramCapacity" Text="暂不可用" Foreground="#BCA88C" FontSize="9" ToolTip="专用显存：已用 / 总量（GiB），包含驱动占用"/>
      <ProgressBar x:Name="VramBar" Foreground="#E9BC80" Background="#4C4338" Margin="0,7,0,0"/>
     </StackPanel>
    </Border>
   </Grid>
   <TextBlock Grid.Row="4" Text="温度趋势 · 最近 2 分钟" Foreground="#7F98AA" FontSize="9" VerticalAlignment="Center" Margin="0,5,0,0"/>
   <TextBlock Grid.Row="4" Text="°C" Foreground="#7F98AA" FontSize="9" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,5,0,0"/>
   <Canvas Grid.Row="5" x:Name="Chart" Width="320" Height="60" ClipToBounds="True"/>
   <StackPanel Grid.Row="6" VerticalAlignment="Bottom">
    <TextBlock x:Name="State" Text="正在连接温度传感器…" Foreground="#B1C4D0" FontSize="10" TextWrapping="Wrap"/>
    <TextBlock x:Name="Updated" Text="每 3 秒更新 · 本机读取" Foreground="#68869C" FontSize="9" Margin="0,5,0,0"/>
   </StackPanel>
  </Grid>
 </Border>
</Grid>
""";
}
