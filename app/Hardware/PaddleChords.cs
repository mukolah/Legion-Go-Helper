using System.Runtime.InteropServices;

namespace LegionHelper.Hardware;

/// <summary>
/// Back-paddle chords: hold Y1 or Y2 and press a D-pad direction or A/B/X/Y.
/// The paddles are invisible to XInput, so while chords are enabled the controller maps them to hidden keys
/// (Y1 → F23, Y2 → F24). A low-level keyboard hook tracks (and swallows) those keys; only while a paddle is held
/// does a short XInput poll run to catch the second button. Zero cost when idle.
/// </summary>
public sealed class PaddleChords : IDisposable
{
    public const int VkY1 = 0x86, VkY2 = 0x87;            // F23, F24
    public const byte HidY1 = 0x72, HidY2 = 0x73;
    public const byte PaddleY1 = 0x1C, PaddleY2 = 0x1D;    // controller button ids

    /// <summary>Chord buttons (XInput bit, id used in settings, display name).</summary>
    public static readonly (ushort Bit, string Id, string Name)[] Buttons =
    [
        (0x0001, "DUp", "D-Pad Up"), (0x0002, "DDown", "D-Pad Down"), (0x0004, "DLeft", "D-Pad Left"), (0x0008, "DRight", "D-Pad Right"),
        (0x1000, "A", "A"), (0x2000, "B", "B"), (0x4000, "X", "X"), (0x8000, "Y", "Y"),
    ];

    /// <summary>Raised on a background thread with (paddle "Y1"/"Y2", button id).</summary>
    public event Action<string, string>? Chord;

    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);

    readonly HookProc _proc;
    readonly IntPtr _hook;
    volatile bool _y1, _y2;
    int _polling;

    /// <summary>Must be created on a thread with a message loop (the UI thread).</summary>
    public PaddleChords()
    {
        _proc = HookCallback;
        _hook = SetWindowsHookExW(13 /*WH_KEYBOARD_LL*/, _proc, IntPtr.Zero, 0);
    }

    IntPtr HookCallback(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(l);
            if (vk is VkY1 or VkY2)
            {
                bool down = w == 0x100 || w == 0x104;   // WM_KEYDOWN / WM_SYSKEYDOWN (auto-repeat is harmless)
                if (vk == VkY1) _y1 = down; else _y2 = down;
                if (down) StartPolling();
                return 1; // swallow: F23/F24 never reach other apps
            }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    void StartPolling()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        new Thread(PollLoop) { IsBackground = true, Name = "PaddleChords" }.Start();
    }

    void PollLoop()
    {
        try
        {
            // Buttons already held when the paddle went down don't count; only new presses trigger.
            ushort prev = Pad360.TryGet(out var s0) ? s0.Buttons : (ushort)0;
            while (_y1 || _y2)
            {
                Thread.Sleep(10);
                if (!Pad360.TryGet(out var s)) continue;
                ushort pressed = (ushort)(s.Buttons & ~prev);
                prev = s.Buttons;
                if (pressed == 0) continue;
                string paddle = _y1 ? "Y1" : "Y2";
                foreach (var b in Buttons)
                    if ((pressed & b.Bit) != 0) Chord?.Invoke(paddle, b.Id);
            }
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);
}
