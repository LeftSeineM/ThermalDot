using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ThermalDot;
public sealed record SystemProxyState(uint Flags, string Server, string Bypass, string AutoUrl);
internal static class SystemProxy
{
    [StructLayout(LayoutKind.Explicit)] private struct Value { [FieldOffset(0)] public uint Number; [FieldOffset(0)] public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential)] private struct Option { public uint Kind; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct OptionList { public uint Size; public IntPtr Connection; public uint Count, Error; public IntPtr Options; }
    internal static SystemProxyState Read()
    {
        int size = Marshal.SizeOf<Option>(); IntPtr buffer = Marshal.AllocHGlobal(size * 4);
        try
        {
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Kind = (uint)i + 1 }, buffer + i * size, false);
            var list = new OptionList { Size = (uint)Marshal.SizeOf<OptionList>(), Count = 4, Options = buffer };
            uint bytes = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, 75, ref list, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            string ReadText(int index) => Marshal.PtrToStringUni(Marshal.PtrToStructure<Option>(buffer + index * size).Value.Text) ?? "";
            return new SystemProxyState(Marshal.PtrToStructure<Option>(buffer).Value.Number, ReadText(1), ReadText(2), ReadText(3));
        }
        finally
        {
            for (int i = 1; i < 4; i++) { var pointer = Marshal.PtrToStructure<Option>(buffer + i * size).Value.Text; if (pointer != IntPtr.Zero) GlobalFree(pointer); }
            Marshal.FreeHGlobal(buffer);
        }
    }
    internal static void Write(SystemProxyState state)
    {
        int size = Marshal.SizeOf<Option>(); IntPtr buffer = Marshal.AllocHGlobal(size * 4);
        var texts = new[] { IntPtr.Zero, Marshal.StringToHGlobalUni(state.Server), Marshal.StringToHGlobalUni(state.Bypass), Marshal.StringToHGlobalUni(state.AutoUrl) };
        try
        {
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Kind = (uint)i + 1, Value = i == 0 ? new Value { Number = state.Flags } : new Value { Text = texts[i] } }, buffer + i * size, false);
            var list = new OptionList { Size = (uint)Marshal.SizeOf<OptionList>(), Count = 4, Options = buffer };
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!InternetNotify(IntPtr.Zero, 39, IntPtr.Zero, 0) || !InternetNotify(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Read() != state) throw new InvalidOperationException("Windows 未确认代理设置");
        }
        finally { foreach (var text in texts) if (text != IntPtr.Zero) Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(buffer); }
    }
    internal static bool IsOff(SystemProxyState state) => (state.Flags & 14) == 0;
    internal static bool IsClash(SystemProxyState state, int port) => (state.Flags & 14) == 2 && state.Server == "127.0.0.1:" + port;
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetQueryOption(IntPtr handle, uint option, ref OptionList list, ref uint bytes);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr handle, uint option, ref OptionList list, uint bytes);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetNotify(IntPtr handle, uint option, IntPtr buffer, uint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}