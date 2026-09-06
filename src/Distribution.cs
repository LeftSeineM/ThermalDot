using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace ThermalDot;

internal static class Distribution
{
    // Only interactive startup requests elevation. Read-only CLI diagnostics and rendering need none.
    internal static bool TryStartElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;
        try
        {
            string? executable = Environment.ProcessPath;
            if (executable == null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; } // A declined UAC prompt still permits GPU/load/memory monitoring.
    }
    internal static void OpenCpuComponent()
    {
        try
        {
            string setup = Path.Combine(Program.Root, "components", "PawnIO_setup.exe");
            if (File.Exists(setup) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(setup))) == "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032")
            {
                if (MessageBox.Show("CPU 温度通常需要 PawnIO 驱动。接下来将打开官方安装向导，由你决定是否安装或修复。驱动安装后会保留在系统中，可在 Windows 已安装的应用中卸载。\n\n继续打开向导？", "CPU 温度读取组件", MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK)
                    Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true });
            }
            else Process.Start(new ProcessStartInfo("https://pawnio.eu/") { UseShellExecute = true });
        }
        catch (Exception e) { MessageBox.Show("未能打开组件安装：" + e.Message, "温度球"); }
    }
    internal static void OpenVendorSettings()
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Lenovo", "PCManager");
            string? app = Directory.Exists(root) ? Directory.GetDirectories(root)
                .Select(p => new { Path = p, Version = Version.TryParse(Path.GetFileName(p), out var version) ? version : null })
                .Where(p => p.Version != null).OrderByDescending(p => p.Version)
                .Select(p => Path.Combine(p.Path, "BatterySetting.exe")).FirstOrDefault(File.Exists) : null;
            if (app == null) { MessageBox.Show("没有找到联想电池设置程序。请在本机原厂管理软件中查看电池养护或充电上限。", "原厂电池设置"); return; }
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
        }
        catch (Exception e) { MessageBox.Show("未能打开原厂设置：" + e.Message, "温度球"); }
    }
    internal static void ShowHelp()
    {
        MessageBox.Show("温度球 1.3.0 · Windows x64\n\n单击：展开详情。拖动：移动。右键：设置、隐藏或退出。\n右上角“电源 / 电池”：切换电源和养护模式。\n\nCPU 温度需要管理员权限及兼容的驱动；安装版附带官方 PawnIO 安装向导。读不到的项目显示 —。\n电池养护只支持已验证的联想组件，其他电脑请使用原厂软件。\n\n没有广告或自动联网，不会开机自启，也不会启动时自动改变电源设置。\n\n偏好和最新读数：\n" + Program.Data, "关于温度球", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    internal static void ShowLicenses()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ThermalDot.Notices.txt");
        using var reader = stream == null ? null : new StreamReader(stream);
        var text = reader?.ReadToEnd() ?? "许可文本暂不可用，请查看安装目录的 Notices.txt";
        new Window { Title = "温度球 · 第三方许可", Width = 720, Height = 600, Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) } }.Show();
    }
}
