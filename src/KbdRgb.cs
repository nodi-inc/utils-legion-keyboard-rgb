// LegionKbdRgb - Lenovo Legion 5 15AHP11 (ITE 048D:C615) keyboard backlight cycler.
// Drives the keyboard through its HID LampArray collection (usage page 0x59, single lamp).
// Hotkey Ctrl+Alt+B cycles: Off -> random solid colour -> rainbow -> Off.
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Linq;
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

class Lamp : IDisposable {
  SafeFileHandle h;
  int featLen, lampCount = 1;

  bool EnsureOpen() {
    if (h != null && !h.IsInvalid) return true;
    var d = Hid.FindLampArray();
    if (d == null) return false;
    h = Hid.Open(d.Path, true);
    featLen = d.Caps.FeatLen;
    if (h.IsInvalid) return false;
    // Report 1: LampArrayAttributes, bytes 1-2 = LampCount.
    var attr = new byte[featLen]; attr[0] = 1;
    if (Hid.HidD_GetFeature(h, attr, attr.Length)) lampCount = Math.Max(1, attr[1] | (attr[2] << 8));
    return true;
  }

  bool Send(params byte[] p) {
    for (int attempt = 0; attempt < 2; attempt++) {
      if (!EnsureOpen()) return false;
      var b = new byte[featLen];
      Array.Copy(p, b, p.Length);
      if (Hid.HidD_SetFeature(h, b, b.Length)) return true;
      h.Dispose(); h = null; // device may have been re-enumerated (resume), reopen once
    }
    return false;
  }

  // Report 6: LampArrayControl (autonomous mode off). Report 5: LampRangeUpdate for every lamp.
  public bool SetColor(byte r, byte g, byte b) {
    if (!Send(6, 0)) return false;
    int last = lampCount - 1;
    return Send(5, 1, 0, 0, (byte)last, (byte)(last >> 8), r, g, b, (byte)((r | g | b) == 0 ? 0 : 1));
  }

  public void ReleaseToFirmware() { Send(6, 1); }

  public void Dispose() { if (h != null) h.Dispose(); }
}

class App : ApplicationContext {
  const int WM_POWERBROADCAST = 0x0218, PBT_APMRESUMEAUTOMATIC = 0x12;
  const int WM_APP_EXIT = 0x8002;
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  const int WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_B = 0x42;
  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
  static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
  static readonly string StateFile = Path.Combine(Dir, "state.txt");

  readonly Lamp lamp = new Lamp();
  readonly Random rnd = new Random();
  readonly MsgWindow win;
  readonly NotifyIcon tray;
  readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer { Interval = 33 };
  readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
  Mode mode;
  Color solid = Color.Black;
  double hue;
  DateTime last = DateTime.UtcNow;

  App() {
    LoadState();
    win = new MsgWindow(this);
    if (!RegisterHotKey(win.Handle, 1, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_B))
      MessageBox.Show("The Ctrl+Alt+B hotkey could not be registered because it is in use by another application. Lighting modes can still be changed from the notification area icon.", "LegionKbdRgb");

    var menu = new ContextMenuStrip();
    menu.Items.Add("Next mode`tCtrl+Alt+B", null, (s, e) => Next());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Off", null, (s, e) => Apply(Mode.Off, false));
    menu.Items.Add("Random solid color", null, (s, e) => Apply(Mode.Solid, true));
    menu.Items.Add("Rainbow", null, (s, e) => Apply(Mode.Rainbow, false));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add("Exit", null, (s, e) => ExitThread());
    tray = new NotifyIcon { Icon = LoadIcon(), Text = "LegionKbdRgb", ContextMenuStrip = menu, Visible = true };
    tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Next(); };

    animTimer.Tick += (s, e) => Animate();
    refreshTimer.Tick += (s, e) => Render(); // re-assert static states in case firmware/Windows took over
    refreshTimer.Start();
    Apply(mode, false);

    // The installer signals this event to ask for a clean shutdown (hands lighting back to the firmware).
    var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LegionKbdRgb.Exit");
    IntPtr hwnd = win.Handle;
    new Thread(() => { exitEvent.WaitOne(); PostMessage(hwnd, WM_APP_EXIT, IntPtr.Zero, IntPtr.Zero); }) { IsBackground = true }.Start();
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
    if (m == Mode.Solid && (newColor || solid.ToArgb() == Color.Black.ToArgb())) solid = RandomVivid();
    if (m == Mode.Rainbow) { last = DateTime.UtcNow; animTimer.Start(); } else animTimer.Stop();
    Render();
    tray.Text = "LegionKbdRgb - " + (m == Mode.Off ? "Off" : m == Mode.Solid ? "Solid color" : "Rainbow");
    SaveState();
  }

  void Render() {
    if (mode == Mode.Off) lamp.SetColor(0, 0, 0);
    else if (mode == Mode.Solid) lamp.SetColor(solid.R, solid.G, solid.B);
    else Animate();
  }

  void Animate() {
    var now = DateTime.UtcNow;
    hue = (hue + (now - last).TotalSeconds * 360.0 / 8.0) % 360.0; // full cycle every 8 s
    last = now;
    var c = Hsv(hue, 1, 1);
    lamp.SetColor(c.R, c.G, c.B);
  }

  Color RandomVivid() { return Hsv(rnd.NextDouble() * 360, 0.75 + rnd.NextDouble() * 0.25, 1); }

  static Color Hsv(double h, double s, double v) {
    double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
    if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
    else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
    else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
    return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
  }

  void LoadState() {
    try {
      var p = File.ReadAllText(StateFile).Trim().Split(',');
      mode = (Mode)int.Parse(p[0]);
      solid = Color.FromArgb(int.Parse(p[1]));
    } catch { mode = Mode.Rainbow; }
  }

  void SaveState() {
    try { Directory.CreateDirectory(Dir); File.WriteAllText(StateFile, (int)mode + "," + solid.ToArgb()); } catch { }
  }

  protected override void ExitThreadCore() {
    animTimer.Stop(); refreshTimer.Stop();
    UnregisterHotKey(win.Handle, 1);
    tray.Visible = false;
    lamp.ReleaseToFirmware();
    lamp.Dispose();
    base.ExitThreadCore();
  }

  class MsgWindow : NativeWindow {
    readonly App app;
    public MsgWindow(App a) { app = a; CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m) {
      if (m.Msg == WM_HOTKEY) app.Next();
      else if (m.Msg == WM_APP_EXIT) app.ExitThread();
      else if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_APMRESUMEAUTOMATIC) app.Render();
      base.WndProc(ref m);
    }
  }

  [STAThread]
  static void Main() {
    bool created;
    using (new Mutex(true, "LegionKbdRgb.SingleInstance", out created)) {
      if (!created) return;
      Application.EnableVisualStyles();
      Application.Run(new App());
    }
  }
}
