// LegionKbdRgb - keyboard backlight control for Lenovo Legion laptops without Legion Space.
// Drives the keyboard through its standard HID LampArray collection (usage page 0x59).
// Hotkey Ctrl+Alt+B cycles: Off -> random solid color -> rainbow -> Off.
//
// Threading: the UI thread only handles the hotkey, tray icon and system events. All HID I/O
// runs on a dedicated LightingWorker thread, so a slow or missing device can never make the
// hotkey or tray icon unresponsive.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("LegionKbdRgb")]
[assembly: AssemblyProduct("LegionKbdRgb")]
[assembly: AssemblyVersion(LegionKbdRgbVersion.Value)]
[assembly: AssemblyFileVersion(LegionKbdRgbVersion.Value)]

enum Mode { Off = 0, Solid = 1, Rainbow = 2 }

// Small rolling log next to the executable (log.txt, rotated to log.old.txt at 256 KB).
static class Log {
  static readonly object gate = new object();
  static readonly string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");

  public static void Write(string msg) {
    lock (gate) {
      try {
        var fi = new FileInfo(file);
        if (fi.Exists && fi.Length > 256 * 1024) {
          File.Copy(file, Path.ChangeExtension(file, ".old.txt"), true);
          File.Delete(file);
        }
        File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
      } catch { }
    }
  }
}

class Lamp : IDisposable {
  SafeFileHandle h;
  int featLen, lampCount = 1;

  public bool IsOpen { get { return h != null && !h.IsInvalid; } }

  bool EnsureOpen() {
    if (IsOpen) return true;
    Close();
    var d = Hid.FindLampArray();
    if (d == null) return false;
    h = Hid.Open(d.Path, true);
    featLen = d.Caps.FeatLen;
    if (h.IsInvalid) { Close(); return false; }
    // Report 1: LampArrayAttributes, bytes 1-2 = LampCount.
    var attr = new byte[featLen]; attr[0] = 1;
    if (Hid.HidD_GetFeature(h, attr, attr.Length)) lampCount = Math.Max(1, attr[1] | (attr[2] << 8));
    Log.Write("Opened LampArray device (" + lampCount + " lamp(s)): " + d.Path);
    return true;
  }

  bool Send(params byte[] p) {
    for (int attempt = 0; attempt < 2; attempt++) {
      if (!EnsureOpen()) return false;
      var b = new byte[featLen];
      Array.Copy(p, b, p.Length);
      if (Hid.HidD_SetFeature(h, b, b.Length)) return true;
      Close(); // device may have been re-enumerated (resume, driver reload): reopen once
    }
    return false;
  }

  // Report 6: LampArrayControl (autonomous mode off). Report 5: LampRangeUpdate for every lamp.
  public bool SetColor(Color c) {
    if (!Send(6, 0)) return false;
    int last = lampCount - 1;
    return Send(5, 1, 0, 0, (byte)last, (byte)(last >> 8), c.R, c.G, c.B, (byte)((c.R | c.G | c.B) == 0 ? 0 : 1));
  }

  public void ReleaseToFirmware() { if (IsOpen) Send(6, 1); }

  public void Close() { if (h != null) { h.Dispose(); h = null; } }

  public void Dispose() { Close(); }
}

// Owns the device. Renders the requested mode: rainbow at ~30 fps, static modes on change and
// every 5 s (Windows Dynamic Lighting or the firmware may take the device back). When the device
// is missing it retries with backoff instead of re-enumerating HID devices 30 times a second.
class LightingWorker {
  readonly Lamp lamp = new Lamp();
  readonly AutoResetEvent wake = new AutoResetEvent(false);
  readonly Thread thread;
  readonly object gate = new object();
  Mode mode;
  Color solid;
  int requested, rendered = -1;
  volatile bool stopping;

  public LightingWorker() {
    thread = new Thread(Run) { IsBackground = true, Name = "LightingWorker" };
  }

  public void Start() { thread.Start(); }

  public void Set(Mode m, Color c) {
    lock (gate) { mode = m; solid = c; requested++; }
    wake.Set();
  }

  // Re-apply immediately, e.g. after resume or unlock.
  public void Kick() { lock (gate) requested++; wake.Set(); }

  public void Stop() {
    stopping = true; wake.Set();
    if (!thread.Join(3000)) Log.Write("LightingWorker did not stop within 3 s");
  }

  void Run() {
    double hue = 0;
    var last = DateTime.UtcNow;
    var nextRefresh = DateTime.MinValue;
    int failures = 0;
    bool reportedMissing = false;
    while (!stopping) {
      Mode m; Color c; int req;
      lock (gate) { m = mode; c = solid; req = requested; }
      var now = DateTime.UtcNow;
      int wait;
      try {
        bool due = m == Mode.Rainbow || req != rendered || now >= nextRefresh;
        if (due) {
          if (m == Mode.Rainbow) {
            hue = (hue + (now - last).TotalSeconds * 360.0 / 8.0) % 360.0; // full cycle every 8 s
            c = Hsv(hue, 1, 1);
          } else if (m == Mode.Off) c = Color.Black;
          last = now;
          if (lamp.SetColor(c)) {
            if (failures > 0 || reportedMissing) Log.Write("Keyboard lighting device available again");
            failures = 0; reportedMissing = false;
            rendered = req;
            nextRefresh = now.AddSeconds(5);
          } else {
            failures++;
            if (!reportedMissing) { Log.Write("Keyboard lighting device not available; retrying"); reportedMissing = true; }
          }
        }
      } catch (Exception ex) {
        failures++;
        lamp.Close();
        Log.Write("Lighting error: " + ex);
      }
      if (failures > 0) wait = Math.Min(10000, 500 * (1 << Math.Min(failures, 5))); // 1 s .. 10 s
      else if (m == Mode.Rainbow) wait = 33;
      else wait = Math.Max(50, (int)(nextRefresh - DateTime.UtcNow).TotalMilliseconds);
      wake.WaitOne(wait);
    }
    try { lamp.ReleaseToFirmware(); } catch { }
    lamp.Dispose();
  }

  public static Color Hsv(double h, double s, double v) {
    double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
    if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
    else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
    else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
    return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
  }
}

class App : ApplicationContext {
  const int WM_APP_EXIT = 0x8002;
  const int WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_B = 0x42, HOTKEY_ID = 1;
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
  static readonly string StateFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "state.txt");

  readonly LightingWorker worker = new LightingWorker();
  readonly Random rnd = new Random();
  readonly MsgWindow win;
  readonly NotifyIcon tray;
  readonly ToolStripMenuItem offItem, solidItem, rainbowItem;
  readonly System.Windows.Forms.Timer hotkeyRetry = new System.Windows.Forms.Timer { Interval = 10000 };
  readonly EventWaitHandle exitEvent;
  bool hotkeyOk, hotkeyWarned;
  Mode mode;
  Color solid = Color.Black;

  App() {
    LoadState();
    win = new MsgWindow(this);

    var menu = new ContextMenuStrip();
    menu.Items.Add(new ToolStripMenuItem("Next mode", null, (s, e) => Next()) { ShortcutKeyDisplayString = "Ctrl+Alt+B" });
    menu.Items.Add(new ToolStripSeparator());
    offItem = new ToolStripMenuItem("Off", null, (s, e) => Apply(Mode.Off, false));
    solidItem = new ToolStripMenuItem("Random solid color", null, (s, e) => Apply(Mode.Solid, true));
    rainbowItem = new ToolStripMenuItem("Rainbow", null, (s, e) => Apply(Mode.Rainbow, false));
    menu.Items.AddRange(new ToolStripItem[] { offItem, solidItem, rainbowItem, new ToolStripSeparator() });
    menu.Items.Add("Open log folder", null, (s, e) => {
      try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); } catch { }
    });
    menu.Items.Add("Exit", null, (s, e) => ExitThread());
    tray = new NotifyIcon { Icon = LoadIcon(), Text = "LegionKbdRgb", ContextMenuStrip = menu, Visible = true };
    tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Next(); };

    // Another program may own Ctrl+Alt+B at sign-in (or briefly during start-up): keep retrying.
    hotkeyRetry.Tick += (s, e) => RegisterHotkey();
    RegisterHotkey();

    SystemEvents.PowerModeChanged += OnPowerModeChanged;
    SystemEvents.SessionSwitch += OnSessionSwitch;

    worker.Start();
    Apply(mode, false);

    // The installer signals this event to ask for a clean shutdown (hands lighting back to the firmware).
    exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LegionKbdRgb.Exit");
    IntPtr hwnd = win.Handle;
    new Thread(() => { exitEvent.WaitOne(); PostMessage(hwnd, WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true }.Start();
    Log.Write("Started LegionKbdRgb " + LegionKbdRgbVersion.Display + " (mode " + mode + ")");
  }

  void RegisterHotkey() {
    if (hotkeyOk) return;
    if (RegisterHotKey(win.Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_B)) {
      hotkeyOk = true;
      hotkeyRetry.Stop();
      if (hotkeyWarned) Log.Write("Registered Ctrl+Alt+B after retrying");
      UpdateTrayText();
      return;
    }
    int err = Marshal.GetLastWin32Error();
    hotkeyRetry.Start();
    if (!hotkeyWarned) {
      hotkeyWarned = true;
      Log.Write("RegisterHotKey(Ctrl+Alt+B) failed, error " + err + "; retrying every 10 s");
      tray.ShowBalloonTip(5000, "LegionKbdRgb",
        "Ctrl+Alt+B is in use by another application. Use the tray icon to change modes; the hotkey will be enabled as soon as it becomes available.",
        ToolTipIcon.Warning);
    }
    UpdateTrayText();
  }

  void OnPowerModeChanged(object s, PowerModeChangedEventArgs e) {
    if (e.Mode == PowerModes.Resume) { Log.Write("Resumed from sleep"); worker.Kick(); }
  }

  void OnSessionSwitch(object s, SessionSwitchEventArgs e) {
    if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect) worker.Kick();
  }

  static Icon LoadIcon() {
    using (var s = typeof(App).Assembly.GetManifestResourceStream("app.ico"))
      return s == null ? SystemIcons.Application : new Icon(s, SystemInformation.SmallIconSize);
  }

  void Next() {
    var m = (Mode)(((int)mode + 1) % 3);
    Apply(m, m == Mode.Solid);
  }

  void Apply(Mode m, bool newColor) {
    mode = m;
    if (m == Mode.Solid && (newColor || solid.ToArgb() == Color.Black.ToArgb()))
      solid = LightingWorker.Hsv(rnd.NextDouble() * 360, 0.75 + rnd.NextDouble() * 0.25, 1);
    worker.Set(m, solid);
    offItem.Checked = m == Mode.Off; solidItem.Checked = m == Mode.Solid; rainbowItem.Checked = m == Mode.Rainbow;
    UpdateTrayText();
    SaveState();
  }

  void UpdateTrayText() {
    string t = "LegionKbdRgb - " + (mode == Mode.Off ? "Off" : mode == Mode.Solid ? "Solid color" : "Rainbow");
    if (!hotkeyOk) t += " (hotkey unavailable)";
    tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
  }

  void LoadState() {
    try {
      var p = File.ReadAllText(StateFile).Trim().Split(',');
      mode = (Mode)int.Parse(p[0]);
      if (mode < Mode.Off || mode > Mode.Rainbow) mode = Mode.Rainbow;
      solid = Color.FromArgb(int.Parse(p[1]));
    } catch { mode = Mode.Rainbow; }
  }

  void SaveState() {
    try { File.WriteAllText(StateFile, (int)mode + "," + solid.ToArgb()); } catch (Exception ex) { Log.Write("Could not save state: " + ex.Message); }
  }

  protected override void ExitThreadCore() {
    Log.Write("Exiting");
    hotkeyRetry.Stop();
    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    SystemEvents.SessionSwitch -= OnSessionSwitch;
    if (hotkeyOk) UnregisterHotKey(win.Handle, HOTKEY_ID);
    tray.Visible = false;
    worker.Stop();
    base.ExitThreadCore();
  }

  class MsgWindow : NativeWindow {
    readonly App app;
    public MsgWindow(App a) { app = a; CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m) {
      if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID) app.Next();
      else if (m.Msg == WM_APP_EXIT) app.ExitThread();
      base.WndProc(ref m);
    }
  }

  [STAThread]
  static void Main() {
    bool created;
    using (new Mutex(true, "LegionKbdRgb.SingleInstance", out created)) {
      if (!created) return;
      // Never die silently: log unexpected errors and keep the message loop running.
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal error: " + e.ExceptionObject);
      Application.EnableVisualStyles();
      Application.Run(new App());
    }
  }
}
