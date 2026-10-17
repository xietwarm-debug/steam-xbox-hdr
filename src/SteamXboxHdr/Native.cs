using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SteamXboxHdr;

public record HdrState(string Monitor, bool? Enabled, string Detail);

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] public struct Header { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential)] public struct Source { public Luid Adapter; public uint Id, Mode, Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct Target
    {
        public Luid Adapter; public uint Id, Mode, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLine;
        public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] public struct DisplayPath { public Source Source; public Target Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct SourceName
    { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [StructLayout(LayoutKind.Sequential)] public struct ColorInfo
    { public Header Header; public uint Flags, Encoding, Bits, Mode; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct MonitorInfo
    {
        public int Size; public Rectangle Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KeyData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KeyInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] public struct InputUnion { [FieldOffset(0)] public KeyInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
    public delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DisplayPath[] paths, ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", CharSet = CharSet.Unicode)] static extern int GetSourceName(ref SourceName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetColorInfo(ref ColorInfo info);
    [DllImport("user32.dll", SetLastError = true)] public static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);

    public static HdrState GetHdr(nint window)
    {
        var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref monitor)) return new("未知", null, "无法定位前台窗口显示器");
        for (var retry = 0; retry < 3; retry++)
        {
            var error = GetDisplayConfigBufferSizes(2, out var pathCount, out var modeCount);
            if (error != 0) return new(monitor.Device, null, "GetDisplayConfigBufferSizes: " + error);
            var paths = new DisplayPath[pathCount];
            var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                error = QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, 0);
                if (error == 122) continue;
                if (error != 0) return new(monitor.Device, null, "QueryDisplayConfig: " + error);
                var statuses = new List<bool?>();
                foreach (var path in paths.Take((int)pathCount))
                {
                    var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = path.Source.Adapter, Id = path.Source.Id }, Name = "" };
                    if (GetSourceName(ref source) != 0 || source.Name != monitor.Device) continue;
                    var color = new ColorInfo { Header = new Header { Type = 15, Size = (uint)Marshal.SizeOf<ColorInfo>(), Adapter = path.Target.Adapter, Id = path.Target.Id } };
                    if (GetColorInfo(ref color) == 0) statuses.Add(color.Mode == 2);
                    else if (Environment.OSVersion.Version.Build < 26100)
                    {
                        color.Header.Type = 9;
                        color.Header.Size = 32;
                        statuses.Add(GetColorInfo(ref color) == 0 ? (color.Flags & 2) != 0 : null);
                    }
                    else statuses.Add(null); // Legacy AdvancedColorEnabled can mean WCG on 24H2+.
                }
                if (statuses.Count == 1) return new(monitor.Device, statuses[0], statuses[0] == true ? "Windows HDR 已开启" : statuses[0] == false ? "Windows HDR 未开启" : "HDR API 不可用");
                if (statuses.Count > 1) return new(monitor.Device, null, "复制显示模式存在多个输出，请改用扩展显示后重试");
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        return new(monitor.Device, null, "无法匹配活动显示路径");
    }

    public static CaptureContext Context(nint window, DateTimeOffset started)
    {
        GetWindowThreadProcessId(window, out var id);
        string name;
        try { using var process = Process.GetProcessById((int)id); name = process.ProcessName; }
        catch (ArgumentException) { name = "unknown"; }
        return new(started, window, id, name, GetHdr(window));
    }

    public static void SendGameBarScreenshot()
    {
        int[] modifiers = [0x10, 0x11, 0x12, 0x5b, 0x5c, 0x2c];
        if (modifiers.Any(k => GetAsyncKeyState(k) < 0)) throw new InvalidOperationException("修饰键或 PrintScreen 正被按住，已跳过触发。请松开后重试。");
        Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = (up ? 2u : 0u) | (key is 0x5b or 0x2c ? 1u : 0u) } } };
        Input[] inputs = [Key(0x5b), Key(0x12), Key(0x2c), Key(0x2c, true), Key(0x12, true), Key(0x5b, true)];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
        {
            // Release only the keys this helper attempted to press.
            Input[] release = [Key(0x2c, true), Key(0x12, true), Key(0x5b, true)];
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Game Bar 快捷键发送失败；游戏权限等级可能不同。");
        }
    }
}

public sealed class ScreenshotHook : IDisposable
{
    private readonly Native.HookProc callback;
    private readonly nint handle;
    private bool down;
    public int Key { get; set; } = (int)Keys.F12;
    public bool Enabled { get; set; }
    public event Action<nint, DateTimeOffset>? Pressed;
    public ScreenshotHook()
    {
        callback = Handle;
        handle = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private nint Handle(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KeyData>(data);
            if (key.Key == Key && (key.Flags & 0x10) == 0)
            {
                if (message == 0x101 || message == 0x105) down = false;
                else if ((message == 0x100 || message == 0x104) && !down)
                {
                    down = true;
                    if (Enabled && !new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(k => Native.GetAsyncKeyState(k) < 0))
                        Pressed?.Invoke(Native.GetForegroundWindow(), DateTimeOffset.UtcNow);
                }
            }
        }
        // Always forward every event, including the Steam screenshot key.
        return Native.CallNextHookEx(handle, code, message, data);
    }
    public void Dispose() => Native.UnhookWindowsHookEx(handle);
}
