using System.Runtime.InteropServices;

namespace LegionHelper.Hardware;

/// <summary>
/// Software gyro mouse: reads the controller's HID gyrometer through the built-in Windows Sensor API (COM, no extra
/// dependencies), removes the sensor bias with continuous still-detection, and turns rotation rate into cursor motion.
/// Holding the device still = cursor still, at any angle. Runs only while enabled.
/// </summary>
public static class GyroMouse
{
    // ---- settings ----
    public static int Speed { get; set; } = 50;          // 1-100
    public static bool HorizontalFromRoll { get; set; }   // false = yaw (turn left/right), true = roll (steering wheel)
    public static bool InvertX { get; set; }
    public static bool InvertY { get; set; }

    public static string Status { get; private set; } = "Off";

    static Thread? _thread;
    static volatile bool _run, _recalibrate;

    public static void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "GyroMouse", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static void Stop() { _run = false; Status = "Off"; }

    /// <summary>Re-measure the sensor bias (hold the device still for ~1.5 s).</summary>
    public static void Recalibrate() => _recalibrate = true;

    static void Loop()
    {
        ISensor? sensor = null;
        var clicks = new PadClicks();
        try
        {
            sensor = OpenGyrometer();
            if (sensor is null) { Status = "No gyrometer found"; Log.Write("GyroMouse: no gyrometer"); _run = false; return; }
            TrySetReportInterval(sensor, 8);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double bx = 0, by = 0, bz = 0;          // bias (deg/s)
            bool calibrated = false;
            int calN = 0; double cx = 0, cy = 0, cz = 0; long calStart = 0;
            long stillSince = -1;
            double accX = 0, accY = 0;
            long lastT = sw.ElapsedMilliseconds;
            _recalibrate = true;

            while (_run)
            {
                Thread.Sleep(8);
                if (Pad360.TryGet(out var pad)) clicks.Update(pad);
                if (!Read(sensor, out double x, out double y, out double z)) continue;
                long now = sw.ElapsedMilliseconds;
                double dt = Math.Clamp((now - lastT) / 1000.0, 0, 0.05);
                lastT = now;

                // --- explicit calibration: average 1.5 s ---
                if (_recalibrate)
                {
                    _recalibrate = false; calibrated = false; calN = 0; cx = cy = cz = 0; calStart = now;
                    Status = "Calibrating - hold still";
                }
                if (!calibrated)
                {
                    cx += x; cy += y; cz += z; calN++;
                    if (now - calStart >= 1500 && calN > 10)
                    {
                        bx = cx / calN; by = cy / calN; bz = cz / calN;
                        calibrated = true; Status = "Active";
                        Log.Write($"GyroMouse bias: {bx:0.00} {by:0.00} {bz:0.00} deg/s ({calN} samples)");
                    }
                    continue;
                }

                double gx = x - bx, gy = y - by, gz = z - bz;
                double mag = Math.Sqrt(gx * gx + gy * gy + gz * gz);

                // --- continuous bias tracking while the device is still (handles temperature drift) ---
                if (mag < 2.0)
                {
                    if (stillSince < 0) stillSince = now;
                    if (now - stillSince > 600) { bx += 0.02 * gx; by += 0.02 * gy; bz += 0.02 * gz; }
                }
                else stillSince = -1;

                // --- deadzone + smooth ramp to suppress sensor noise ---
                const double dead = 0.8, ramp = 3.0;
                double scale = mag <= dead ? 0 : mag >= dead + ramp ? 1 : (mag - dead) / ramp;
                if (scale == 0) continue;

                double pxPerDeg = 1.5 + Speed * 0.35;   // ~19 px per degree at 50
                double h = HorizontalFromRoll ? gz : gy;
                double dx = -h * dt * pxPerDeg * scale;
                double dy = -gx * dt * pxPerDeg * scale;
                if (InvertX) dx = -dx;
                if (InvertY) dy = -dy;
                accX += dx; accY += dy;
                int ix = (int)accX, iy = (int)accY;
                accX -= ix; accY -= iy;
                if (ix != 0 || iy != 0) MouseOut.Move(ix, iy);
            }
        }
        catch (Exception ex) { Status = "Error: " + ex.Message; Log.Write("GyroMouse failed: " + ex); }
        finally
        {
            clicks.Release();
            if (sensor is not null) Marshal.ReleaseComObject(sensor);
            _run = false;
        }
    }

    // ---- Windows Sensor API (sensorsapi.h) ---------------------------------------------------------

    static readonly Guid CLSID_SensorManager = new("77A1C827-FCD2-4689-8915-9D613CC5FA3E");
    static readonly Guid SENSOR_TYPE_GYROMETER_3D = new("09485F5A-759E-42C2-BD4B-A349B75C8643");
    static readonly Guid SENSOR_DATA_TYPE_MOTION = new("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");
    static readonly Guid SENSOR_PROPERTY_GUID = new("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");
    static readonly Guid CLSID_PortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public double dbl;
        [FieldOffset(8)] public float flt;
        [FieldOffset(8)] public IntPtr ptr;
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISensorManager
    {
        [PreserveSig] int GetSensorsByCategory(ref Guid category, out ISensorCollection coll);
        [PreserveSig] int GetSensorsByType(ref Guid type, out ISensorCollection coll);
    }

    [ComImport, Guid("23571E11-E545-4DD8-A337-B89BF44B10DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISensorCollection
    {
        [PreserveSig] int GetAt(uint index, out ISensor sensor);
        [PreserveSig] int GetCount(out uint count);
    }

    [ComImport, Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISensor
    {
        [PreserveSig] int GetID(out Guid id);
        [PreserveSig] int GetCategory(out Guid cat);
        [PreserveSig] int GetType(out Guid type);
        [PreserveSig] int GetFriendlyName(out IntPtr bstr);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, out PROPVARIANT v);
        [PreserveSig] int GetProperties(IntPtr keys, out IntPtr values);
        [PreserveSig] int GetSupportedDataFields(out IntPtr keys);
        [PreserveSig] int SetProperties(IPortableDeviceValues values, out IntPtr results);
        [PreserveSig] int SupportsDataField(ref PROPERTYKEY key, out short supported);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetData(out ISensorDataReport report);
    }

    [ComImport, Guid("0AB9DF9B-C4B5-4796-8898-0470706A2E1D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISensorDataReport
    {
        [PreserveSig] int GetTimestamp(IntPtr systemTime); // unused; keeps the vtable slot
        [PreserveSig] int GetSensorValue(ref PROPERTYKEY key, out PROPVARIANT v);
    }

    [ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetStringValue(ref PROPERTYKEY k, [MarshalAs(UnmanagedType.LPWStr)] string s);
        [PreserveSig] int GetStringValue(ref PROPERTYKEY k, out IntPtr s);
        [PreserveSig] int SetUnsignedIntegerValue(ref PROPERTYKEY k, uint v);
    }

    static ISensor? OpenGyrometer()
    {
        var mgr = (ISensorManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_SensorManager)!)!;
        var type = SENSOR_TYPE_GYROMETER_3D;
        if (mgr.GetSensorsByType(ref type, out var coll) != 0 || coll is null) return null;
        coll.GetCount(out uint n);
        // Prefer the Legion controller's IMU (HID VID_1A86); fall back to the first gyrometer.
        ISensor? first = null;
        for (uint i = 0; i < n; i++)
        {
            if (coll.GetAt(i, out var s) != 0) continue;
            first ??= s;
            var key = new PROPERTYKEY { fmtid = new Guid("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920"), pid = 15 /*DEVICE_PATH*/ };
            if (s.GetProperty(ref key, out var pv) == 0 && pv.vt == 31 /*VT_LPWSTR*/)
            {
                var path = Marshal.PtrToStringUni(pv.ptr);
                PropVariantClear(ref pv);
                if (path?.Contains("VID_1A86", StringComparison.OrdinalIgnoreCase) == true) return s;
            }
        }
        return first;
    }

    static void TrySetReportInterval(ISensor s, uint ms)
    {
        try
        {
            var values = (IPortableDeviceValues)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_PortableDeviceValues)!)!;
            var key = new PROPERTYKEY { fmtid = SENSOR_PROPERTY_GUID, pid = 13 /*CURRENT_REPORT_INTERVAL*/ };
            values.SetUnsignedIntegerValue(ref key, ms);
            s.SetProperties(values, out _);
        }
        catch (Exception ex) { Log.Write("GyroMouse: report interval not set: " + ex.Message); }
    }

    static bool Read(ISensor s, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (s.GetData(out var report) != 0 || report is null) return false;
        try
        {
            return Get(report, 10, out x) & Get(report, 11, out y) & Get(report, 12, out z);
        }
        finally { Marshal.ReleaseComObject(report); }
    }

    static bool Get(ISensorDataReport r, uint pid, out double v)
    {
        var key = new PROPERTYKEY { fmtid = SENSOR_DATA_TYPE_MOTION, pid = pid }; // ANGULAR_VELOCITY_X/Y/Z_DEGREES_PER_SECOND
        v = 0;
        if (r.GetSensorValue(ref key, out var pv) != 0) return false;
        v = pv.vt switch { 5 /*VT_R8*/ => pv.dbl, 4 /*VT_R4*/ => pv.flt, _ => 0 };
        return pv.vt is 4 or 5;
    }
}

/// <summary>Shared SendInput helper for the mouse emulators.</summary>
static class MouseOut
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int dx, dy; public int data; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    struct Input { public uint type; public MouseInput mi; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, Input[] inputs, int size);

    public const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10, WHEEL = 0x800;

    static void Send(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        SendInput(1, [new Input { type = 0, mi = new MouseInput { dx = dx, dy = dy, data = data, flags = flags } }], Marshal.SizeOf<Input>());

    public static void Move(int dx, int dy) => Send(MOVE, dx, dy);
    public static void Wheel(int delta) => Send(WHEEL, data: delta);
    public static void Buttons(uint flags) => Send(flags);
}
