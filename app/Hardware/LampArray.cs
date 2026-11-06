using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LegionHelper.Hardware;

/// <summary>
/// Per-LED control of the Legion Go S stick rings through the HID LampArray (usage page 0x59) that the controller
/// exposes on mi_04 while its RGB mode is 0 ("dynamic"). 6 lamps; lamp N lights one LED on each ring
/// (left ring clockwise from 9 o'clock, right ring = left rotated 180°). See docs/controller-protocol.md.
/// </summary>
public sealed class LampArray : IDisposable
{
    public const int Count = 6;
    const ushort PAGE = 0x59;
    const int FEATURE = 2, HIDP_OK = 0x00110000;

    readonly SafeFileHandle _h;
    readonly IntPtr _pp;
    readonly int _len;
    readonly byte _ridRange, _ridControl;

    LampArray(SafeFileHandle h, IntPtr pp, int len, byte range, byte control)
    { _h = h; _pp = pp; _len = len; _ridRange = range; _ridControl = control; }

    /// <summary>Opens the LampArray interface, or null if the controller isn't in per-LED mode.</summary>
    public static LampArray? Open()
    {
        foreach (var path in Native.FindByUsagePage(0x1A86, PAGE))
        {
            var h = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!Native.HidD_GetPreparsedData(h, out var pp)) { h.Dispose(); continue; }
            var caps = new ushort[64]; Native.HidP_GetCaps(pp, caps);
            byte range = Native.ReportIdFor(pp, caps, 0x61), control = Native.ReportIdFor(pp, caps, 0x71);
            if (range == 0xFF || control == 0xFF) { h.Dispose(); continue; }
            return new LampArray(h, pp, caps[4], range, control);
        }
        return null;
    }

    /// <summary>false = the app drives the lamps; true = the device's own (default) behaviour.</summary>
    public bool SetAutonomous(bool on)
    {
        var r = new byte[_len];
        Native.HidP_InitializeReportForID(FEATURE, _ridControl, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x71, on ? 1u : 0u, _pp, r, _len);
        return Native.HidD_SetFeature(_h, r, _len);
    }

    bool Range(int start, int end, Color c, bool complete)
    {
        var r = new byte[_len];
        Native.HidP_InitializeReportForID(FEATURE, _ridRange, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x55, complete ? 1u : 0u, _pp, r, _len); // LampUpdateComplete
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x61, (uint)start, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x62, (uint)end, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x51, c.R, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x52, c.G, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x53, c.B, _pp, r, _len);
        Native.HidP_SetUsageValue(FEATURE, PAGE, 0, 0x54, 255, _pp, r, _len);
        return Native.HidD_SetFeature(_h, r, _len);
    }

    /// <summary>Sets all 6 lamps. Uniform colours go out as one range update.</summary>
    public bool Set(ReadOnlySpan<Color> lamps)
    {
        bool uniform = true;
        for (int i = 1; i < Count; i++) if (lamps[i].ToArgb() != lamps[0].ToArgb()) { uniform = false; break; }
        if (uniform) return Range(0, Count - 1, lamps[0], true);
        bool ok = true;
        for (int i = 0; i < Count; i++) ok &= Range(i, i, lamps[i], i == Count - 1);
        return ok;
    }

    public void Dispose()
    {
        Native.HidD_FreePreparsedData(_pp);
        _h.Dispose();
    }

    static class Native
    {
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] public static extern bool HidD_GetAttributes(SafeFileHandle h, ref Attr a);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr p);
        [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr p, ushort[] caps);
        [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr p);
        [DllImport("hid.dll")] public static extern int HidP_SetUsageValue(int type, ushort page, ushort link, ushort usage, uint v, IntPtr p, byte[] r, int len);
        [DllImport("hid.dll")] public static extern int HidP_InitializeReportForID(int type, byte id, IntPtr p, byte[] r, int len);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, int f);
        [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref IfData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref IfData d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
        [StructLayout(LayoutKind.Sequential)] public struct Attr { public int Size; public ushort Vid, Pid, Ver; }
        [StructLayout(LayoutKind.Sequential)] struct IfData { public int cbSize; public Guid g; public int flags; public IntPtr res; }

        public static List<string> FindByUsagePage(ushort vid, ushort page)
        {
            var list = new List<string>();
            HidD_GetHidGuid(out var g);
            IntPtr set = SetupDiGetClassDevsW(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
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
                        if (!HidD_GetAttributes(h, ref a) || a.Vid != vid) continue;
                        if (!HidD_GetPreparsedData(h, out var p)) continue;
                        var caps = new ushort[64]; HidP_GetCaps(p, caps); HidD_FreePreparsedData(p);
                        if (caps[1] == page && caps[0] == 0x01) list.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        /// <summary>Feature report id that carries a usage (HIDP_VALUE_CAPS: ReportID @2, IsRange @12, Usage/UsageMin @56).</summary>
        public static byte ReportIdFor(IntPtr pp, ushort[] caps, ushort usage)
        {
            ushort n = caps[30];
            var buf = new byte[72 * Math.Max(1, (int)n)];
            HidP_GetValueCaps(FEATURE, buf, ref n, pp);
            for (int i = 0; i < n; i++)
            {
                int o = i * 72;
                ushort u = BitConverter.ToUInt16(buf, o + 56), uMax = BitConverter.ToUInt16(buf, o + 58);
                if (BitConverter.ToUInt16(buf, o) == PAGE && (u == usage || (buf[o + 12] != 0 && usage >= u && usage <= uMax)))
                    return buf[o + 2];
            }
            return 0xFF;
        }
    }
}
