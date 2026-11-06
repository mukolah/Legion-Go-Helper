using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LegionHelper.Hardware;

/// <summary>
/// Reads the controller's state stream (HID mi_06, vendor page 0xFFA0, 33-byte input reports, ~100 Hz) to see
/// buttons that never reach Windows as input: Legion L (byte 1 bit 0) and Legion R (byte 1 bit 1).
/// The controller rejects remapping those buttons, but their presses are visible here (the same way Legion Space
/// sees them). Read-only and shared, so games and other apps are unaffected. Byte 3: View 0x40, Menu 0x80;
/// bytes 15-26: IMU data.
/// </summary>
public sealed class ControllerState : IDisposable
{
    public enum LegionButton { L, R }

    /// <summary>Raised on a background thread when a Legion button is pressed (rising edge).</summary>
    public event Action<LegionButton>? Pressed;

    volatile bool _run = true;
    FileStream? _stream;

    public ControllerState()
    {
        new Thread(Loop) { IsBackground = true, Name = "ControllerState" }.Start();
    }

    void Loop()
    {
        byte prev = 0;
        while (_run)
        {
            try
            {
                var path = Native.Find(0x1A86, 0xFFA0, 33);
                if (path is null) { Thread.Sleep(2000); continue; }
                var h = Native.CreateFileW(path, 0x80000000 /*GENERIC_READ*/, 3, IntPtr.Zero, 3, 0x40000000 /*OVERLAPPED*/, IntPtr.Zero);
                if (h.IsInvalid) { Thread.Sleep(2000); continue; }
                using var fs = _stream = new FileStream(h, FileAccess.Read, 33, true);
                var buf = new byte[33];
                while (_run)
                {
                    int n = fs.Read(buf, 0, buf.Length);
                    if (n < 2) continue;
                    byte now = buf[1], down = (byte)(now & ~prev);
                    prev = now;
                    if ((down & 0x01) != 0) Pressed?.Invoke(LegionButton.L);
                    if ((down & 0x02) != 0) Pressed?.Invoke(LegionButton.R);
                }
            }
            catch (Exception) when (_run)
            {
                // device re-enumerated (RGB mode switch, sleep): reopen after a moment
                prev = 0;
                Thread.Sleep(1500);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _run = false;
        try { _stream?.Dispose(); } catch { }
    }

    static class Native
    {
        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attr a);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, ushort[] caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IfData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref IfData d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [StructLayout(LayoutKind.Sequential)] struct Attr { public int Size; public ushort Vid, Pid, Ver; }
        [StructLayout(LayoutKind.Sequential)] struct IfData { public int cbSize; public Guid g; public int flags; public IntPtr res; }

        /// <summary>HID interface of the vendor with the given usage page and input report length.</summary>
        public static string? Find(ushort vid, ushort page, int inLen)
        {
            HidD_GetHidGuid(out var g);
            IntPtr set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
            try
            {
                for (int i = 0; ; i++)
                {
                    var d = new IfData { cbSize = Marshal.SizeOf<IfData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d)) return null;
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
                        if (!HidD_GetAttributes(h, ref a) || a.Vid != vid || !HidD_GetPreparsedData(h, out var p)) continue;
                        var caps = new ushort[64]; HidP_GetCaps(p, caps); HidD_FreePreparsedData(p);
                        if (caps[1] == page && caps[2] == inLen) return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
