using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace ThermalDot;

public sealed class DisplayTimeoutState
{
    public Guid? Scheme { get; set; }
    public uint? AcSeconds { get; set; }
    public uint? DcSeconds { get; set; }
    public string Error { get; set; } = "";
    public bool Available => Scheme.HasValue && AcSeconds.HasValue && DcSeconds.HasValue && Error == "";
}

public sealed record TimeoutChoice(uint Seconds, string Label)
{
    public override string ToString() => Label;
}

internal interface IDisplayTimeoutApi
{
    DisplayTimeoutState Read();
    void Write(Guid scheme, bool ac, uint seconds);
    void Activate(Guid scheme);
}

public static class DisplayTimeout
{
    public static string Label(uint? seconds) => seconds switch
    {
        null => "暂不可用",
        0 => "从不",
        _ => seconds.Value % 60 == 0 ? (seconds.Value / 60) + " 分钟" : seconds.Value + " 秒"
    };

    public static List<TimeoutChoice> Choices(uint? current)
    {
        var values = new uint[] { 60, 120, 180, 300, 600, 900, 1200, 1800, 3600, 7200, 0 };
        var choices = values.Select(v => new TimeoutChoice(v, Label(v))).ToList();
        if (current.HasValue && !values.Contains(current.Value))
            choices.Insert(0, new TimeoutChoice(current.Value, Label(current) + "（当前）"));
        return choices;
    }

    public static DisplayTimeoutState Read() => new NativeDisplayTimeoutApi().Read();
    public static DisplayTimeoutState Apply(DisplayTimeoutState expected, uint ac, uint dc)
        => Apply(expected, ac, dc, new NativeDisplayTimeoutApi());

    internal static DisplayTimeoutState Apply(DisplayTimeoutState expected, uint ac, uint dc, IDisplayTimeoutApi api)
    {
        var written = new List<(bool Ac, uint Old, uint New)>();
        DisplayTimeoutState before = api.Read();
        try
        {
            if (!expected.Available || !before.Available)
                throw new InvalidOperationException("无法读取当前设置，请重新打开此页");
            bool changeAc = ac != expected.AcSeconds, changeDc = dc != expected.DcSeconds;
            if (before.Scheme != expected.Scheme ||
                (changeAc && before.AcSeconds != expected.AcSeconds) ||
                (changeDc && before.DcSeconds != expected.DcSeconds))
                throw new InvalidOperationException("系统设置刚刚发生变化，请按最新值重新选择");
            Guid scheme = before.Scheme!.Value;
            if (changeAc) Write(true, before.AcSeconds!.Value, ac);
            if (changeDc) Write(false, before.DcSeconds!.Value, dc);
            if (written.Count != 0)
            {
                if (api.Read().Scheme != scheme) throw new InvalidOperationException("当前电源计划已切换，请重新选择");
                api.Activate(scheme);
            }
            var after = api.Read();
            if (!after.Available || after.Scheme != scheme ||
                (changeAc && after.AcSeconds != ac) || (changeDc && after.DcSeconds != dc))
                throw new InvalidOperationException("Windows 未确认设置，请检查系统电源设置");
            return after;

            void Write(bool isAc, uint oldValue, uint value)
            {
                if (api.Read().Scheme != scheme) throw new InvalidOperationException("当前电源计划已切换，请重新选择");
                api.Write(scheme, isAc, value);
                written.Add((isAc, oldValue, value));
            }
        }
        catch (Exception e)
        {
            // Restore only values written by this operation, without overwriting a later external change.
            if (before.Scheme.HasValue)
            {
                bool restored = false;
                foreach (var item in written)
                {
                    try
                    {
                        var current = api.Read();
                        if (current.Scheme == before.Scheme &&
                            (item.Ac ? current.AcSeconds : current.DcSeconds) == item.New)
                        { api.Write(before.Scheme.Value, item.Ac, item.Old); restored = true; }
                    }
                    catch { }
                }
                try { if (restored && api.Read().Scheme == before.Scheme) api.Activate(before.Scheme.Value); } catch { }
            }
            var after = api.Read();
            after.Error = "未能应用：" + e.Message + "。已重新读取当前值。";
            return after;
        }
    }
}

internal sealed class NativeDisplayTimeoutApi : IDisplayTimeoutApi
{
    // VIDEOIDLE is in seconds; zero means never. Sleep and lock-screen settings are separate.
    private static Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static Guid Idle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    public DisplayTimeoutState Read()
    {
        var state = new DisplayTimeoutState();
        IntPtr pointer = IntPtr.Zero;
        try
        {
            Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
            Guid scheme = Marshal.PtrToStructure<Guid>(pointer);
            state.Scheme = scheme;
            Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Video, ref Idle, out uint ac));
            state.AcSeconds = ac;
            Check(PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Video, ref Idle, out uint dc));
            state.DcSeconds = dc;
        }
        catch (Exception e) { state.Error = "无法读取屏幕设置：" + e.Message; }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
        return state;
    }
    public void Write(Guid scheme, bool ac, uint seconds)
        => Check(ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Video, ref Idle, seconds)
                    : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Video, ref Idle, seconds));
    public void Activate(Guid scheme) => Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
    private static void Check(uint code)
    {
        if (code != 0) throw new Win32Exception((int)code, code == 5 ? "Windows 拒绝更改，请检查管理员权限或组织策略" : new Win32Exception((int)code).Message);
    }
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}