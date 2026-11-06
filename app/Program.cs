using LegionHelper.Hardware;

namespace LegionHelper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\LegionHelper.SingleInstance", out bool first);
        if (!first)
        {
            // Ask the running instance to show itself.
            using var ev = EventWaitHandle.OpenExisting(@"Global\LegionHelper.Show");
            ev.Set();
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (!LenovoWmi.IsAvailable())
        {
            MessageBox.Show("Lenovo Gaming Zone WMI interface not found.\nThis tool only supports Lenovo Legion handhelds and must run as administrator.",
                "Legion Helper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var mc = new ModeControl();
        try { mc.Init(); }
        catch (Exception ex) { Log.Write("Init failed: " + ex); }

        var form = new MainForm(mc);
        _ = form.Handle; // create handle so events can marshal to the UI thread

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\LegionHelper.Show");
        var showThread = new Thread(() => { while (showEvent.WaitOne()) form.BeginInvoke(form.ShowNearTray); }) { IsBackground = true };
        showThread.Start();

        mc.RefreshMode();
        if (mc.Cfg.MouseMode != "off") Task.Run(() => MouseModes.Apply(mc.Cfg));
        Lighting.Init(mc);
        if (mc.Cfg.RgbEffect is not null) Task.Run(Lighting.Apply);
        if (args.Contains("-fans")) form.OpenFans();
        else if (args.Contains("-controller")) { form.ShowNearTray(); form.OpenControllerWindow(); }
        else if (args.Contains("-rgb")) { form.ShowNearTray(); form.OpenRgbWindow(); }
        else if (!args.Contains("-tray")) form.ShowNearTray();

        Application.Run();
    }
}
