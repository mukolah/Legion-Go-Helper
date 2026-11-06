using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LegionHelper.Hardware;

/// <summary>
/// Legion Go S built-in controller (USB 1A86:E310/E311, vendor HID usage page 0xFFA0, 64-byte reports).
/// Protocol from Legion Space's HuaqinUsb\LEGOQHWHandle.dll: report = [0x00, cmd, sub, data...] padded to 65 bytes.
/// Getters use odd command numbers, setters use getter+1; replies echo [0x00, cmd, sub, value...].
/// See docs/controller-protocol.md.
/// </summary>
public sealed class LegionController : IDisposable
{
    public const ushort Vid = 0x1A86;
    public static readonly ushort[] Pids = [0xE310, 0xE311];
    const ushort UsagePage = 0xFFA0;
    const int ReportLen = 65;

    readonly FileStream _stream;
    readonly object _lock = new();
    readonly System.Collections.Concurrent.BlockingCollection<byte[]> _inbox = new(64);
    readonly CancellationTokenSource _cts = new();

    LegionController(FileStream s)
    {
        _stream = s;
        new Thread(ReadLoop) { IsBackground = true, Name = "LegionController reader" }.Start();
    }

    void ReadLoop()
    {
        var buf = new byte[ReportLen];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = _stream.ReadAsync(buf, 0, ReportLen, _cts.Token).GetAwaiter().GetResult();
                if (n <= 0) break;
                _inbox.TryAdd(buf[..n]); // drops if nobody is consuming
            }
        }
        catch { /* device removed or disposed */ }
    }

    /// <summary>Opens the controller's 65-byte vendor interface, or returns null if not present.</summary>
    public static LegionController? Open()
    {
        foreach (var path in Native.FindHid(Vid, Pids, UsagePage, ReportLen))
        {
            var h = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
            if (h.IsInvalid) continue;
            return new LegionController(new FileStream(h, FileAccess.ReadWrite, ReportLen, true));
        }
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
    }

    /// <summary>Sends a command and waits for the reply echoing [cmd, sub]. Returns the reply payload (after cmd/sub) or null.</summary>
    public byte[]? Query(byte cmd, byte sub, ReadOnlySpan<byte> data = default, int timeoutMs = 500)
    {
        lock (_lock)
        {
            while (_inbox.TryTake(out _)) { }
            Write(cmd, sub, data);
            var deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                int left = (int)(deadline - Environment.TickCount64);
                if (left <= 0 || !_inbox.TryTake(out var r, left)) return null;
                if (r.Length >= 3 && r[1] == cmd && r[2] == sub) return r[3..];
            }
        }
    }

    /// <summary>Raw query for diagnostics: returns every report received within the timeout.</summary>
    public List<byte[]> QueryRaw(ReadOnlySpan<byte> payload, int timeoutMs = 500)
    {
        lock (_lock)
        {
            while (_inbox.TryTake(out _)) { }
            var report = new byte[ReportLen];
            payload[..Math.Min(payload.Length, ReportLen - 1)].CopyTo(report.AsSpan(1));
            _stream.Write(report, 0, ReportLen);
            var list = new List<byte[]>();
            var deadline = Environment.TickCount64 + timeoutMs;
            while (_inbox.TryTake(out var r, (int)Math.Max(0, deadline - Environment.TickCount64))) list.Add(r);
            return list;
        }
    }

    /// <summary>Fire-and-forget write.</summary>
    public void Send(byte cmd, byte sub, ReadOnlySpan<byte> data = default)
    {
        lock (_lock) Write(cmd, sub, data);
    }

    void Write(byte cmd, byte sub, ReadOnlySpan<byte> data)
    {
        var report = new byte[ReportLen];
        report[1] = cmd;
        report[2] = sub;
        data[..Math.Min(data.Length, ReportLen - 3)].CopyTo(report.AsSpan(3));
        _stream.Write(report, 0, ReportLen);
        _stream.Flush();
    }

    // ---- Typed API ---------------------------------------------------------------------------
    // Command numbers from LEGOQHWHandle.dll; names cross-checked with Linux drivers/hid/hid-lenovo-go-s.c.

    public static class Feature
    {
        public const byte GamepadMode = 0x01, AutoSleep = 0x04, ImuBypass = 0x05, RgbEnable = 0x06,
                          ImuEnable = 0x07, TouchpadEnable = 0x08, PollRate = 0x10;
    }

    /// <summary>Set commands reply [cmd, sub, status]; status 0 = OK.</summary>
    bool Set(byte cmd, byte sub, ReadOnlySpan<byte> data) => Query(cmd, sub, data) is { Length: > 0 } r && r[0] == 0;
    byte? Get1(byte cmd, byte sub) => Query(cmd, sub) is { Length: > 0 } r ? r[0] : null;

    public byte? GetFeature(byte f) => Get1(0x03, f);
    public bool SetFeature(byte f, byte value) => Set(0x04, f, [value]);

    /// <summary>Rumble strength: 0 off, 1 weak, 2 medium, 3 strong.</summary>
    public byte? GetVibration() => Get1(0x07, 0x01);
    public bool SetVibration(byte level) => Set(0x08, 0x01, [level]);

    // RGB (joystick rings). Profile n = 1..3 lives at sub 0x02 + n. Payload [effect, R, G, B, brightness 0-100, speed 0-100].
    public enum RgbEffect : byte { Solid = 0, Breathe = 1, Chroma = 2, Rainbow = 3 }
    public record RgbProfile(RgbEffect Effect, Color Color, byte Brightness, byte Speed);

    public RgbProfile? GetRgbProfile(int n)
    {
        var r = Query(0x0F, (byte)(0x02 + n));
        if (r is not { Length: >= 6 }) return null;
        return new RgbProfile((RgbEffect)r[0], Color.FromArgb(r[1], r[2], r[3]), r[4], r[5]);
    }

    public bool SetRgbProfile(int n, RgbProfile p) =>
        Set(0x10, (byte)(0x02 + n), [(byte)p.Effect, p.Color.R, p.Color.G, p.Color.B, Math.Min(p.Brightness, (byte)100), Math.Min(p.Speed, (byte)100)]);

    public byte? GetActiveRgbProfile() => Get1(0x0F, 0x02);
    public bool SelectRgbProfile(int n) => Set(0x10, 0x02, [(byte)n]);
    /// <summary>0 = dynamic, 1 = custom.</summary>
    public byte? GetRgbMode() => Get1(0x0F, 0x01);
    public bool SetRgbMode(byte mode) => Set(0x10, 0x01, [mode]);

    /// <summary>Switch to custom lighting and activate profile n.</summary>
    public bool UseRgbProfile(int n) => Set(0x10, 0x01, [1]) & Set(0x10, 0x02, [(byte)n]);

    // ---- Gyro (cmd 0x0D get / 0x0E set; sub from the DLL's gyro command builder) --------------
    // sub 1: gyro mapping status (0 = off; 1/2 = mapping modes), sub 2: gyro-as-mouse [srcX, srcY, sensX, sensY],
    // sub 3: gyro-as-joystick (9 bytes), sub 8..: axis inversion.
    // The firmware gyro mapping is not used (see docs/controller-protocol.md); Legion Helper keeps it off and
    // implements gyro-mouse in software (GyroMouse.cs).
    public byte? GetGyroMode() => Get1(0x0D, 0x01);
    public bool SetGyroMode(byte mode) => Set(0x0E, 0x01, [mode]);

    // ---- Button remapping -------------------------------------------------------------------
    // Entry (7 bytes): [button, 0x80 | type, code1..code5]. Write with 12 04 <entry>, then commit with 12 05.
    // Types (from LEGOQHWHandle.dll SetKeysMap): 0 keyboard (HID usage), 1 key combination,
    // 2 gamepad button (Lenovo button id), 3 mouse, 4 media (HID consumer usage).
    public enum MapType : byte { Keyboard = 0, Combo = 1, Gamepad = 2, Mouse = 3, Media = 4, System = 0xFF }
    public record KeyMapping(byte Button, MapType Type, byte[] Codes);

    public KeyMapping? GetKeyMap(byte button)
    {
        var r = Query(0x11, 0x04, [button]);
        if (r is not { Length: >= 7 } || r[0] != button) return null;
        var type = (r[1] & 0x80) == 0 ? MapType.System : (MapType)(r[1] & 0x07);
        return new KeyMapping(button, type, r[2..7].TakeWhile(b => b != 0).ToArray());
    }

    public bool SetKeyMap(KeyMapping m)
    {
        var entry = new byte[7];
        entry[0] = m.Button;
        entry[1] = m.Type == MapType.System ? (byte)0 : (byte)(0x80 | (byte)m.Type);
        for (int i = 0; i < Math.Min(5, m.Codes.Length); i++) entry[2 + i] = m.Codes[i];
        return Set(0x12, 0x04, entry) && Set(0x12, 0x05, []);
    }

    /// <summary>Factory mapping: Legion L/R are system buttons, everything else maps to itself.</summary>
    public static KeyMapping DefaultMapping(byte button) =>
        button is 0x01 or 0x02 ? new(button, MapType.System, []) : new(button, MapType.Gamepad, [button]);

    // Deadzones, side 1 = left, 2 = right. Stick: [inner, outer] %. Trigger: [deadzone, max] %.
    public (byte inner, byte outer)? GetStickDeadzone(byte side) => Query(0x0B, side) is { Length: >= 2 } r ? (r[0], r[1]) : null;
    public bool SetStickDeadzone(byte side, byte inner, byte outer) => Set(0x0C, side, [inner, outer]);
    public (byte dead, byte max)? GetTriggerDeadzone(byte side) => Query(0x09, side) is { Length: >= 2 } r ? (r[0], r[1]) : null;
    public bool SetTriggerDeadzone(byte side, byte dead, byte max) => Set(0x0A, side, [dead, max]);

    static class Native
    {
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attr a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out Caps c);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IfData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref IfData d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);

        [StructLayout(LayoutKind.Sequential)] struct Attr { public int Size; public ushort Vid, Pid, Ver; }
        [StructLayout(LayoutKind.Sequential)]
        struct Caps
        {
            public ushort Usage, UsagePage, InLen, OutLen, FeatLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
        }
        [StructLayout(LayoutKind.Sequential)] struct IfData { public int cbSize; public Guid g; public int flags; public IntPtr res; }

        public static List<string> FindHid(ushort vid, ushort[] pids, ushort page, int outLen)
        {
            var list = new List<string>();
            HidD_GetHidGuid(out var g);
            IntPtr set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12 /*PRESENT|INTERFACE*/);
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new IfData { cbSize = Marshal.SizeOf<IfData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) break;
                    SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, 8);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, req, out _, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(buf + 4)!;
                        using var h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                        if (h.IsInvalid) continue;
                        var a = new Attr { Size = Marshal.SizeOf<Attr>() };
                        if (!HidD_GetAttributes(h, ref a) || a.Vid != vid || !pids.Contains(a.Pid)) continue;
                        if (!HidD_GetPreparsedData(h, out var p)) continue;
                        HidP_GetCaps(p, out var caps);
                        HidD_FreePreparsedData(p);
                        if (caps.UsagePage == page && caps.OutLen == outLen) list.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }
    }
}
