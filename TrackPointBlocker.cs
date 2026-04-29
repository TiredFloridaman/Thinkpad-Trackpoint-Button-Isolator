// TrackPointBlocker.cs  (v15)
//
// WHY PASS-COUNT (v14) FAILED:
//   WH_MOUSE_LL fires SYNCHRONOUSLY when input enters the system — before any
//   message is posted to the queue.  WM_INPUT is a posted message and therefore
//   always arrives AFTER the hook has already fired for the same hardware event.
//   _passCount is always 0 at hook time. The approach is fundamentally broken.
//
// CORRECT DESIGN — SENTINEL re-injection:
//   1. WH_MOUSE_LL hook blocks ALL WM_MOUSEMOVE unconditionally …
//      … EXCEPT moves whose dwExtraInfo == SENTINEL (our own re-injections).
//   2. We register for raw mouse input (RIDEV_INPUTSINK).
//   3. WM_INPUT is processed AFTER the hook has already blocked the hardware move.
//      For touchpad WM_INPUT we call SendInput to re-inject the move with SENTINEL.
//      The hook sees SENTINEL and passes it. Cursor tracks the touchpad.
//      For TrackPoint WM_INPUT we do nothing — move stays blocked.
//   4. Buttons / scroll are not WM_MOUSEMOVE — hook never touches them.
//   5. Emergency stop: End × 5 within 2 s.
//
// SENTINEL value: 0x49A7F3C2  (positive, fits in 32 bits, no sign-extension on x64)
// Compare in hook as: s.dwExtraInfo == (IntPtr)SENTINEL
//
// INPUT struct layout on x64 (THIS WAS THE BUG IN EARLIER VERSIONS):
//   offset  0: DWORD type  (4 bytes)
//   offset  4: padding     (4 bytes — aligns union to 8-byte boundary)
//   offset  8: MOUSEINPUT  (union)
// MOUSEINPUT must be at [FieldOffset(8)], NOT [FieldOffset(4)].
// With the wrong offset, dwFlags landed where Windows reads `time` → value 0
// → no event type → SendInput generated nothing → cursor never moved.
//
// COORDINATE INJECTION:
//   If touchpad reports ABSOLUTE (usFlags & 0x01):
//     lLastX/Y are in [0..65535].  Inject with MOUSEEVENTF_ABSOLUTE.
//   If touchpad reports RELATIVE deltas:
//     lLastX/Y are mickeys.  Inject with MOUSEEVENTF_MOVE (no ABSOLUTE).
//     The driver has already scaled them correctly; just relay them.
//
// COMPILE:  double-click build.bat

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using System.Text;
using Microsoft.Win32;

// =============================================================================
//  Win32  (struct offsets verified for x64)
// =============================================================================
static class W
{
    // ── message constants ────────────────────────────────────────────────────
    public const int  WH_MOUSE_LL          = 14;
    public const int  WH_KEYBOARD_LL       = 13;
    public const int  WM_INPUT             = 0x00FF;
    public const int  WM_MOUSEMOVE         = 0x0200;
    public const int  WM_KEYDOWN           = 0x0100;
    public const int  VK_END               = 0x23;

    // ── raw input ────────────────────────────────────────────────────────────
    public const uint RIDEV_INPUTSINK      = 0x00000100;
    public const uint RIDEV_REMOVE         = 0x00000001;
    public const uint RID_INPUT            = 0x10000003;
    public const uint RIDI_DEVICENAME      = 0x20000007;
    public const uint RIM_TYPEMOUSE        = 0;

    // ── mouse flags ──────────────────────────────────────────────────────────
    public const uint MOUSE_MOVE_ABSOLUTE  = 0x01;
    public const uint MOUSEEVENTF_MOVE     = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // ── screen ───────────────────────────────────────────────────────────────
    public const int  SM_CXSCREEN          = 0;
    public const int  SM_CYSCREEN          = 1;

    // ── SENTINEL ─────────────────────────────────────────────────────────────
    // Must be a positive value that fits in int so IntPtr stores it without
    // sign-extension on x64.  0x49A7F3C2 < 0x7FFFFFFF  → safe.
    public const int  SENTINEL             = 0x49A7F3C2;

    // ── delegates ────────────────────────────────────────────────────────────
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ── structs ──────────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT  pt;
        public uint   mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint   vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage, usUsage;
        public uint   dwFlags;
        public IntPtr hwndTarget;
    }

    // Size = 16 on x64 (IntPtr=8 + uint=4 + 4 pad).  No Pack override.
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint   dwType;
    }

    // RAWINPUTHEADER: 24 bytes on x64
    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint   dwType;
        public uint   dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    // RAWMOUSE: offsets from winuser.h
    [StructLayout(LayoutKind.Explicit)]
    public struct RAWMOUSE
    {
        [FieldOffset( 0)] public ushort usFlags;
        [FieldOffset( 4)] public uint   ulButtons;
        [FieldOffset( 4)] public ushort usButtonFlags;
        [FieldOffset( 6)] public ushort usButtonData;
        [FieldOffset( 8)] public uint   ulRawButtons;
        [FieldOffset(12)] public int    lLastX;
        [FieldOffset(16)] public int    lLastY;
        [FieldOffset(20)] public uint   ulExtraInformation;
    }

    // MOUSEINPUT on x64 — explicit offsets to guarantee no padding ambiguity:
    //   dx=0, dy=4, mouseData=8, dwFlags=12, time=16, (pad 4), dwExtraInfo=24
    //   Total = 32 bytes.
    [StructLayout(LayoutKind.Explicit)]
    public struct MOUSEINPUT
    {
        [FieldOffset( 0)] public int    dx;
        [FieldOffset( 4)] public int    dy;
        [FieldOffset( 8)] public uint   mouseData;
        [FieldOffset(12)] public uint   dwFlags;
        [FieldOffset(16)] public uint   time;
        [FieldOffset(24)] public IntPtr dwExtraInfo;  // 4 bytes padding before this
    }

    // INPUT on x64:
    //   [0]  DWORD type  (4 bytes)
    //   [4]  padding     (4 bytes — compiler aligns union to 8-byte boundary)
    //   [8]  MOUSEINPUT  (union member; must be at offset 8, NOT 4)
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset( 0)] public uint       type;   // 0 = INPUT_MOUSE
        [FieldOffset( 8)] public MOUSEINPUT mi;
    }

    // ── imports ──────────────────────────────────────────────────────────────
    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hk, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string s);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int n);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray)] RAWINPUTDEVICE[] d, uint n, uint sz);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(
        IntPtr hRaw, uint cmd, IntPtr data, ref uint sz, uint hdrSz);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        IntPtr list, ref uint count, uint sz);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(
        IntPtr dev, uint cmd, IntPtr data, ref uint sz);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(
        uint n, [MarshalAs(UnmanagedType.LPArray)] INPUT[] inp, int sz);
}

// =============================================================================
//  Entry point
// =============================================================================
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool autoStart = args.Length > 0 && args[0].ToLowerInvariant() == "/auto";
        Application.Run(new MainForm(autoStart));
    }
}

// =============================================================================
//  Main form
// =============================================================================
class MainForm : Form
{
    bool   _active    = false;
    IntPtr _mouseHook = IntPtr.Zero, _kbHook = IntPtr.Zero;
    W.HookProc _mouseProc, _kbProc;

    IntPtr _tpHandle  = IntPtr.Zero;
    IntPtr _padHandle = IntPtr.Zero;

    // Diagnostics (volatile — written by hook thread, read by timer on UI thread)
    volatile int  _cRawTP   = 0;  // WM_INPUT from TrackPoint
    volatile int  _cRawPad  = 0;  // WM_INPUT from touchpad
    volatile int  _cInject  = 0;  // SendInput calls
    volatile int  _cPass    = 0;  // hook: passed (sentinel seen)
    volatile int  _cBlock   = 0;  // hook: blocked
    volatile int  _lastExtraInfo = -1;  // lower 32 bits of dwExtraInfo  // last dwExtraInfo seen in hook

    readonly int  _hdrSize = Marshal.SizeOf(typeof(W.RAWINPUTHEADER));
    readonly int  _inputSz = Marshal.SizeOf(typeof(W.INPUT));
    readonly uint _listSz  = (uint)Marshal.SizeOf(typeof(W.RAWINPUTDEVICELIST));
    readonly uint _ridSz   = (uint)Marshal.SizeOf(typeof(W.RAWINPUTDEVICE));

    // Emergency stop: End × 5 within 2 s
    const int EN = 5, EMS = 2000;
    int[] _eTick = new int[EN];
    int   _eIdx  = 0;

    NotifyIcon        _tray;
    Button            _btn;
    Label             _status, _emerg, _diag;
    ToolStripMenuItem _tmi;

    static readonly string[] TP_KEYS = { "LEN0310", "TPPS2", "I8042", "PS2" };
    static readonly IntPtr   SENTINEL_PTR = new IntPtr(W.SENTINEL);

    bool _autoStart;

    public MainForm(bool autoStart)
    {
        _autoStart = autoStart;
        BuildUI();
        BuildTray();
        _mouseProc = MouseHook;
        _kbProc    = KbHook;
    }

    bool _firstShow = true;
    protected override void SetVisibleCore(bool v)
    {
        if (_firstShow)
        {
            _firstShow = false;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            // Auto-enable blocking if launched with /auto (e.g. from startup)
            if (_autoStart)
                BeginInvoke(new Action(() => { if (!_active) Toggle(); }));
            return;
        }
        base.SetVisibleCore(v);
    }

    // ── Device scan ───────────────────────────────────────────────────────────
    bool FindDevices(out IntPtr tpHandle, out IntPtr padHandle, out string log)
    {
        tpHandle  = IntPtr.Zero;
        padHandle = IntPtr.Zero;
        var sb = new StringBuilder();

        uint count = 0;
        if (W.GetRawInputDeviceList(IntPtr.Zero, ref count, _listSz) == uint.MaxValue || count == 0)
        {
            log = "GetRawInputDeviceList failed (error " + Marshal.GetLastWin32Error() + ")";
            return false;
        }

        IntPtr list = Marshal.AllocHGlobal((int)(count * _listSz));
        try
        {
            W.GetRawInputDeviceList(list, ref count, _listSz);
            for (uint i = 0; i < count; i++)
            {
                int    off   = (int)(i * _listSz);
                IntPtr hDev  = Marshal.ReadIntPtr(list, off);
                uint   dtype = (uint)Marshal.ReadInt32(list, off + IntPtr.Size);
                if (dtype != W.RIM_TYPEMOUSE) continue;

                uint nameSz = 0;
                W.GetRawInputDeviceInfo(hDev, W.RIDI_DEVICENAME, IntPtr.Zero, ref nameSz);
                string name = "";
                if (nameSz > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)(nameSz * 2 + 2));
                    try
                    {
                        W.GetRawInputDeviceInfo(hDev, W.RIDI_DEVICENAME, buf, ref nameSz);
                        name = Marshal.PtrToStringUni(buf);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }

                sb.AppendLine("0x" + hDev.ToString("X") + ": " + name);
                bool isTP = false;
                string up = name.ToUpperInvariant();
                foreach (string k in TP_KEYS) if (up.Contains(k)) { isTP = true; break; }
                if (isTP) tpHandle  = hDev;
                else      padHandle = hDev;
            }
        }
        finally { Marshal.FreeHGlobal(list); }

        sb.AppendLine("\nTrackPoint : 0x" + tpHandle.ToString("X"));
        sb.AppendLine("Touchpad   : 0x" + padHandle.ToString("X"));
        sb.AppendLine("INPUT size : " + Marshal.SizeOf(typeof(W.INPUT)) + " (should be 40 on x64)");
        log = sb.ToString();
        return padHandle != IntPtr.Zero;
    }

    // ── WM_INPUT ──────────────────────────────────────────────────────────────
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == W.WM_INPUT && _active)
            OnRawInput(m.LParam);
        base.WndProc(ref m);
    }

    void OnRawInput(IntPtr hRaw)
    {
        uint sz = 0;
        W.GetRawInputData(hRaw, W.RID_INPUT, IntPtr.Zero, ref sz, (uint)_hdrSize);
        if (sz == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)sz);
        try
        {
            if (W.GetRawInputData(hRaw, W.RID_INPUT, buf, ref sz, (uint)_hdrSize) == 0) return;

            var hdr = (W.RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(W.RAWINPUTHEADER));
            if (hdr.dwType != W.RIM_TYPEMOUSE) return;

            // TrackPoint: the hook already blocked its WM_MOUSEMOVE. Do nothing.
            if (hdr.hDevice == _tpHandle) { _cRawTP++; return; }  // TrackPoint — already blocked

            // hDevice == NULL means a synthetic move from SendInput (no physical device).
            // Two cases:
            //   (a) Our OWN re-injection: ulExtraInformation == SENTINEL  → skip (prevents feedback loop)
            //   (b) ELAN driver injection: ulExtraInformation != SENTINEL  → treat as touchpad move
            // We must read RAWMOUSE here to check ulExtraInformation.
            if (hdr.hDevice == IntPtr.Zero)
            {
                var rm0 = (W.RAWMOUSE)Marshal.PtrToStructure(
                    new IntPtr(buf.ToInt64() + _hdrSize), typeof(W.RAWMOUSE));
                if ((int)rm0.ulExtraInformation == W.SENTINEL) return;  // our own injection — stop loop
                // ELAN driver injection — fall through and treat as touchpad
            }
            _cRawPad++;

            // Touchpad: the hook blocked its WM_MOUSEMOVE too. Re-inject it now
            // with SENTINEL so the hook passes it through.
            var rm = (W.RAWMOUSE)Marshal.PtrToStructure(
                new IntPtr(buf.ToInt64() + _hdrSize), typeof(W.RAWMOUSE));

            bool isAbs = (rm.usFlags & W.MOUSE_MOVE_ABSOLUTE) != 0;

            // Skip pure button events (no position change)
            if (!isAbs && rm.lLastX == 0 && rm.lLastY == 0) return;

            var inp = new W.INPUT();
            inp.type           = 0;             // INPUT_MOUSE
            inp.mi.dwExtraInfo = SENTINEL_PTR;  // hook will pass this

            if (isAbs)
            {
                // lLastX/Y already in [0..65535] — pass directly as absolute
                inp.mi.dx      = rm.lLastX;
                inp.mi.dy      = rm.lLastY;
                inp.mi.dwFlags = W.MOUSEEVENTF_MOVE | W.MOUSEEVENTF_ABSOLUTE;
            }
            else
            {
                // Relative mickeys — relay as-is; driver already scaled them
                inp.mi.dx      = rm.lLastX;
                inp.mi.dy      = rm.lLastY;
                inp.mi.dwFlags = W.MOUSEEVENTF_MOVE;
            }

            _cInject++;
            uint sent = W.SendInput(1, new W.INPUT[] { inp }, _inputSz);

            if (_diag != null)
                _diag.Text = "Touchpad " + (isAbs ? "ABS" : "rel") +
                             " (" + rm.lLastX + "," + rm.lLastY + ")" +
                             (sent == 0 ? "  SendInput ERR=" + Marshal.GetLastWin32Error() : "  OK");
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ── Hooks ─────────────────────────────────────────────────────────────────
    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _active && (int)wParam == W.WM_MOUSEMOVE)
        {
            var ms = (W.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(W.MSLLHOOKSTRUCT));
            _lastExtraInfo = (int)ms.dwExtraInfo.ToInt64();
            if (ms.dwExtraInfo.ToInt64() == (long)W.SENTINEL)
            {
                _cPass++;
                return W.CallNextHookEx(_mouseHook, nCode, wParam, lParam);  // PASS
            }
            _cBlock++;
            return new IntPtr(1);  // BLOCK
        }
        return W.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == W.WM_KEYDOWN)
        {
            var kb = (W.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(W.KBDLLHOOKSTRUCT));
            if ((int)kb.vkCode == W.VK_END)
            {
                int now = Environment.TickCount;
                _eTick[_eIdx % EN] = now;
                _eIdx++;
                if (_eIdx >= EN && now - _eTick[_eIdx % EN] < EMS)
                    BeginInvoke(new Action(EmergencyDisable));
            }
        }
        return W.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    void EmergencyDisable() { if (_active) Toggle(); }

    // ── Toggle ────────────────────────────────────────────────────────────────
    void Toggle()
    {
        if (!_active)
        {
            string log;
            if (!FindDevices(out _tpHandle, out _padHandle, out log))
            {
                MessageBox.Show("Could not find devices:\n\n" + log,
                    "TrackPoint Button Isolator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool ok = W.RegisterRawInputDevices(new W.RAWINPUTDEVICE[] {
                new W.RAWINPUTDEVICE {
                    usUsagePage = 0x01, usUsage = 0x02,
                    dwFlags     = W.RIDEV_INPUTSINK,
                    hwndTarget  = Handle
                }
            }, 1, _ridSz);

            if (!ok)
            {
                MessageBox.Show("RegisterRawInputDevices failed (error " +
                    Marshal.GetLastWin32Error() + ").",
                    "TrackPoint Button Isolator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _mouseHook = W.SetWindowsHookEx(W.WH_MOUSE_LL, _mouseProc,
                W.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                W.RegisterRawInputDevices(new W.RAWINPUTDEVICE[] {
                    new W.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02,
                        dwFlags = W.RIDEV_REMOVE }
                }, 1, _ridSz);
                MessageBox.Show("SetWindowsHookEx failed (error " +
                    Marshal.GetLastWin32Error() + ").",
                    "TrackPoint Button Isolator", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _kbHook = W.SetWindowsHookEx(W.WH_KEYBOARD_LL, _kbProc,
                W.GetModuleHandle(null), 0);

            _active = true;
            SetUI(true);

            // Start diagnostics timer
            var t = new System.Windows.Forms.Timer();
            t.Interval = 200;
            t.Tick += (ts, te) =>
            {
                if (!_active) { ((System.Windows.Forms.Timer)ts).Stop(); return; }
                if (_diag != null)
                    _diag.Text =
                        "TP=" + _cRawTP + "  Pad=" + _cRawPad +
                        "  Inj=" + _cInject + "  Pass=" + _cPass + "  Blk=" + _cBlock +
                        "  lastExtra=0x" + ((uint)_lastExtraInfo).ToString("X") +
                        "  SENTINEL=0x" + ((long)W.SENTINEL).ToString("X") +
                        "  INPUTsz=" + _inputSz;
            };
            t.Start();
        }
        else
        {
            _active = false;
            W.RegisterRawInputDevices(new W.RAWINPUTDEVICE[] {
                new W.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02,
                    dwFlags = W.RIDEV_REMOVE }
            }, 1, _ridSz);
            if (_mouseHook != IntPtr.Zero)
            { W.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
            if (_kbHook != IntPtr.Zero)
            { W.UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
            if (_diag != null) _diag.Text = "";
            SetUI(false);
        }
    }

    void Cleanup()
    {
        _active = false;
        W.RegisterRawInputDevices(new W.RAWINPUTDEVICE[] {
            new W.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02,
                dwFlags = W.RIDEV_REMOVE }
        }, 1, _ridSz);
        if (_mouseHook != IntPtr.Zero) W.UnhookWindowsHookEx(_mouseHook);
        if (_kbHook    != IntPtr.Zero) W.UnhookWindowsHookEx(_kbHook);
        _tray.Visible = false;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    { Cleanup(); base.OnFormClosed(e); }

    // ── UI ────────────────────────────────────────────────────────────────────
    void BuildUI()
    {
        Text = "TrackPoint Button Isolator";
        Size = new Size(560, 440);
        MinimumSize = MaximumSize = new Size(560, 440);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(24, 24, 26);
        ForeColor = Color.FromArgb(230, 230, 240);

        var hdr = new Panel { Dock = DockStyle.Top, Height = 54,
            BackColor = Color.FromArgb(40, 40, 44) };
        hdr.Controls.Add(new Label { Text = "TrackPoint Button Isolator  v1.0",
            Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.White,
            AutoSize = false, Bounds = new Rectangle(14, 7, 520, 24) });
        hdr.Controls.Add(new Label {
            Text = "Hook blocks all moves  \u00b7  Touchpad re-injected with SENTINEL  \u00b7  Buttons unaffected",
            Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(150, 150, 165),
            AutoSize = false, Bounds = new Rectangle(15, 31, 525, 16) });

        var info = new Label {
            Text =
                "What this does:\r\n" +
                "  \u2022  Blocks all cursor movement from the TrackPoint (the red stick),\r\n" +
                "       which floods garbage input when the hardware is failing.\r\n" +
                "  \u2022  TrackPoint buttons (left, right, middle) continue to work normally.\r\n" +
                "  \u2022  Touchpad movement, taps, scrolling and gestures are unaffected.\r\n" +
                "  \u2022  Compatible with ThinkPad X1 Carbon and other ThinkPad models.\r\n" +
                "  \u2022  Emergency stop: press  End  five times within 2 seconds.",
            Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(155, 165, 175),
            AutoSize = false, Bounds = new Rectangle(14, 62, 528, 110) };

        _emerg = new Label {
            Text = "\u26a0  Active \u2014 End\u00d75 to stop",
            Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(200, 160, 50),
            AutoSize = false, Visible = false, Bounds = new Rectangle(14, 175, 400, 18) };

        _diag = new Label {
            Text = "", Font = new Font("Segoe UI", 7.8f),
            ForeColor = Color.FromArgb(90, 110, 140),
            AutoSize = false, Bounds = new Rectangle(14, 195, 528, 30) };

        _btn = new Button {
            Text = "Enable Blocker", Bounds = new Rectangle(14, 235, 186, 42),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(42, 140, 88),
            ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11f),
            Cursor = Cursors.Hand };
        _btn.FlatAppearance.BorderColor = Color.FromArgb(86, 86, 96);
        _btn.Click += (s, e) => Toggle();

        var scanBtn = new Button {
            Text = "Scan Devices", Bounds = new Rectangle(212, 235, 120, 42),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(50, 60, 80),
            ForeColor = Color.FromArgb(150, 170, 210),
            Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
        scanBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 100);
        scanBtn.Click += (s, e) =>
        {
            IntPtr tp, pad; string log;
            FindDevices(out tp, out pad, out log);
            MessageBox.Show(log, "Devices", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        _status = new Label {
            Text = "\u2b24  Inactive", Font = new Font("Segoe UI", 9.5f),
            ForeColor = Color.FromArgb(120, 120, 135),
            AutoSize = true, Location = new Point(384, 248) };

        // ── Startup row ──
        bool startupOn = IsStartupEnabled();
        var startupBtn = new Button {
            Text      = startupOn ? "Remove from Startup" : "Run at Windows Startup",
            Bounds    = new Rectangle(14, 288, 240, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = startupOn ? Color.FromArgb(60, 80, 60) : Color.FromArgb(50, 60, 80),
            ForeColor = startupOn ? Color.FromArgb(100, 210, 120) : Color.FromArgb(150, 170, 210),
            Font      = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
        startupBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 100);
        startupBtn.Click += (sb, eb) =>
        {
            bool nowOn = !IsStartupEnabled();
            SetStartup(nowOn);
            startupBtn.Text      = nowOn ? "Remove from Startup" : "Run at Windows Startup";
            startupBtn.BackColor = nowOn ? Color.FromArgb(60, 80, 60) : Color.FromArgb(50, 60, 80);
            startupBtn.ForeColor = nowOn ? Color.FromArgb(100, 210, 120) : Color.FromArgb(150, 170, 210);
        };

        var startupNote = new Label {
            Text      = "When enabled: launches minimised to tray and starts blocking automatically.",
            Font      = new Font("Segoe UI", 7.5f), ForeColor = Color.FromArgb(100, 110, 125),
            AutoSize  = false, Bounds = new Rectangle(14, 324, 528, 16) };

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 28,
            BackColor = Color.FromArgb(40, 40, 44) };
        foot.Controls.Add(new Label {
            Text = "Double-click tray icon to reopen  \u00b7  Right-click for menu",
            Font = new Font("Segoe UI", 7.8f), ForeColor = Color.FromArgb(110, 110, 125),
            AutoSize = true, Location = new Point(10, 7) });

        Controls.Add(hdr); Controls.Add(info); Controls.Add(_emerg);
        Controls.Add(_diag); Controls.Add(_btn); Controls.Add(scanBtn);
        Controls.Add(_status); Controls.Add(startupBtn); Controls.Add(startupNote);
        Controls.Add(foot);
        FormClosing += (s, e) => { e.Cancel = true; Hide(); };
    }

    void BuildTray()
    {
        _tray = new NotifyIcon { Icon = SystemIcons.Shield,
            Text = "TrackPoint Blocker \u2014 Inactive", Visible = true };
        var ctx = new ContextMenuStrip();
        _tmi = new ToolStripMenuItem("Enable Blocker");
        _tmi.Click += (s, e) => Toggle();
        var open = new ToolStripMenuItem("Open Window");
        open.Click += (s, e) => { Show(); BringToFront(); };
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (s, e) => { Cleanup(); Application.Exit(); };
        ctx.Items.Add(_tmi); ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add(open); ctx.Items.Add(exit);
        _tray.ContextMenuStrip = ctx;
        _tray.DoubleClick += (s, e) => { Show(); BringToFront(); };
    }


    // ── Startup registry ──────────────────────────────────────────────────────
    const string RUN_KEY  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    const string APP_NAME = "TrackPointBlocker";

    static string ExePath()
    {
        return System.Reflection.Assembly.GetExecutingAssembly().Location;
    }

    bool IsStartupEnabled()
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
            {
                if (k == null) return false;
                object v = k.GetValue(APP_NAME);
                return v != null && v.ToString().Contains(ExePath());
            }
        }
        catch { return false; }
    }

    void SetStartup(bool enable)
    {
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (k == null) return;
                if (enable)
                    k.SetValue(APP_NAME, "\"" + ExePath() + "\" /auto");
                else
                    k.DeleteValue(APP_NAME, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not update startup entry:\n" + ex.Message,
                "TrackPoint Button Isolator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void SetUI(bool on)
    {
        _btn.Text         = on ? "Disable Blocker" : "Enable Blocker";
        _btn.BackColor    = on ? Color.FromArgb(165, 50, 50) : Color.FromArgb(42, 140, 88);
        _status.Text      = on ? "\u2b24  Active" : "\u2b24  Inactive";
        _status.ForeColor = on ? Color.FromArgb(60, 200, 110) : Color.FromArgb(120, 120, 135);
        _emerg.Visible    = on;
        _tray.Text        = on ? "TrackPoint Blocker \u2014 ACTIVE" : "TrackPoint Blocker \u2014 Inactive";
        _tray.Icon        = on ? SystemIcons.Information : SystemIcons.Shield;
        if (_tmi != null) _tmi.Text = on ? "Disable Blocker" : "Enable Blocker";
    }
}
