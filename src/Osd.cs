// On-screen display shown near the bottom center of the active monitor when the mode changes.
// Rendered with per-pixel alpha (UpdateLayeredWindow) so the panel has soft rounded corners and
// translucency; it never takes focus and lets clicks pass through, like the Windows volume flyout.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class Osd : Form {
  // Layout in logical (96 dpi) pixels.
  // Rows: header, mode chips, hotkey hint; in Solid mode also the hue bar, hex value and hold hint.
  const int W = 440, HeaderH = 42, ChipsH = 54, HintH = 34, PickerH = 104, BottomPad = 8, Pad = 16, Radius = 20;
  const int GapAboveTaskbar = 32;
  // Long enough to read the panel after a key press without lingering: 2 s, plus a little more
  // after a color pick so the chosen color can be confirmed, then a gentle fade.
  const int FadeInMs = 120, FadeOutMs = 450, VisibleMs = 2000, AfterPickMs = 3200;

  static readonly string[] Names = { "Off", "Solid", "Aurora" };

  readonly Timer frame = new Timer { Interval = 16 };
  readonly float scale;
  Mode mode;
  Color solid;
  int cycle = App.DefaultCycle;   // Aurora seconds per color cycle
  bool picking;
  double pickHue;
  DateTime shownAt, lastInput;
  double opacity;     // 0..1, animated
  int holdMs = VisibleMs;
  bool visible;
  int anchorLeft, anchorTop;   // top stays put; the Solid rows extend the panel downward
  Font capFont, hintFont, labelFont, hexFont;

  public Osd() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    TopMost = true;
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
    capFont = Px("Segoe UI Semibold", 9f);
    hintFont = Px("Segoe UI", 9.5f);
    labelFont = Px("Segoe UI Semibold", 12f);
    hexFont = Px("Consolas", 10.5f);
    frame.Tick += (s, e) => Animate();
  }

  protected override bool ShowWithoutActivation { get { return true; } }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      // LAYERED | TRANSPARENT (click-through) | TOOLWINDOW (no Alt+Tab) | NOACTIVATE | TOPMOST
      cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8;
      return cp;
    }
  }

  // Mode changed (tap, tray menu, or end of a color pick).
  public void ShowMode(Mode m, Color c, int cycleSeconds) {
    holdMs = picking ? AfterPickMs : VisibleMs;
    mode = m; solid = c; cycle = cycleSeconds; picking = false;
    Touch();
  }

  // Aurora speed adjustment in progress.
  public void ShowSpeed(int cycleSeconds) {
    mode = Mode.Rainbow; cycle = cycleSeconds; picking = true;
    Touch();
  }

  // Color pick in progress: hue in degrees, resulting color.
  public void ShowPicker(double hue, Color c) {
    mode = Mode.Solid; solid = c; pickHue = hue; picking = true;
    Touch();
  }

  void Touch() {
    lastInput = DateTime.UtcNow;
    if (!visible) {
      visible = true; shownAt = DateTime.UtcNow; opacity = 0;
      PlaceOnActiveMonitor();
      if (!IsHandleCreated) CreateHandle();
      ShowWindow(Handle, 4 /* SW_SHOWNOACTIVATE */);
      SetWindowPos(Handle, (IntPtr)(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }
    frame.Start();
    Animate();
  }

  int S(float v) { return (int)Math.Round(v * scale); }

  // Bitmaps render at 96 dpi, so size fonts in pixels scaled to the monitor DPI.
  Font Px(string family, float points) { return new Font(family, points * 96f / 72f * scale, FontStyle.Regular, GraphicsUnit.Pixel); }

  bool HasPickerRows { get { return mode != Mode.Off; } }

  int PanelHeight { get { return HeaderH + ChipsH + HintH + (HasPickerRows ? PickerH : 0) + BottomPad; } }

  void PlaceOnActiveMonitor() {
    var fg = GetForegroundWindow();
    var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
    var wa = screen.WorkingArea;
    anchorLeft = wa.Left + (wa.Width - S(W)) / 2;
    // Place the tallest (Solid) layout's bottom edge GapAboveTaskbar above the work area.
    anchorTop = wa.Bottom - S(GapAboveTaskbar) - S(HeaderH + ChipsH + HintH + PickerH + BottomPad);
  }

  void Animate() {
    var now = DateTime.UtcNow;
    double sinceInput = (now - lastInput).TotalMilliseconds;
    double target;
    if (picking || sinceInput < holdMs) target = 1;
    else target = Math.Max(0, 1 - (sinceInput - holdMs) / FadeOutMs);
    double fadeIn = Math.Min(1, (now - shownAt).TotalMilliseconds / FadeInMs);
    opacity = Math.Min(target, fadeIn);

    if (!picking && sinceInput >= holdMs + FadeOutMs) {
      frame.Stop(); visible = false; ShowWindow(Handle, 0 /* SW_HIDE */);
      return;
    }
    Render();
  }

  void Render() {
    int w = S(W), h = S(PanelHeight);
    var target = new Point(anchorLeft, anchorTop);
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) Draw(g, w, h);
      Present(bmp, target, (byte)Math.Round(255 * EaseOut(opacity)));
    }
  }

  // Development aid: render the panel to a PNG without showing a window (--render-osd).
  public static void RenderPreview(string path, Mode m, Color c, bool picker, double hue, int cycleSeconds = App.DefaultCycle) {
    var o = new Osd { mode = m, solid = c, picking = picker, pickHue = hue, cycle = cycleSeconds };
    int w = o.S(W), h = o.S(o.PanelHeight);
    using (var bmp = new Bitmap(w + 40, h + 40, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) {
        g.Clear(Color.FromArgb(255, 70, 90, 120));
        g.TranslateTransform(20, 20);
        o.Draw(g, w, h);
      }
      bmp.Save(path, ImageFormat.Png);
    }
  }

  static double EaseOut(double t) { return 1 - (1 - t) * (1 - t); }

  void Draw(Graphics g, int w, int h) {
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

    // Panel
    var panel = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
    using (var path = Round(panel, S(Radius))) {
      using (var bg = new LinearGradientBrush(panel, Color.FromArgb(236, 32, 32, 40), Color.FromArgb(236, 20, 20, 26), 90f))
        g.FillPath(bg, path);
      using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 1)) g.DrawPath(pen, path);
    }

    // Header
    using (var capBrush = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
      g.DrawString("KEYBOARD BACKLIGHT", capFont, capBrush, S(Pad + 2), S(14));

    // Mode chips
    float gap = S(8), chipW = (w - 2 * S(Pad) - 2 * gap) / 3f, chipH = S(ChipsH - 8), y = S(HeaderH);
    var label = labelFont;
    {
      for (int i = 0; i < 3; i++) {
        var r = new RectangleF(S(Pad) + i * (chipW + gap), y, chipW, chipH);
        bool sel = (int)mode == i;
        if (sel) {
          using (var p = Round(r, S(11)))
          using (var b = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
          using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1)) { g.FillPath(b, p); g.DrawPath(pen, p); }
        }
        var textSize = g.MeasureString(Names[i], label);
        float dot = S(14), contentW = dot + S(9) + textSize.Width;
        float x0 = r.X + (r.Width - contentW) / 2, cy = r.Y + r.Height / 2;
        DrawModeDot(g, (Mode)i, new RectangleF(x0, cy - dot / 2, dot, dot), sel);
        using (var tb = new SolidBrush(sel ? Color.White : Color.FromArgb(120, 255, 255, 255)))
          g.DrawString(Names[i], label, tb, x0 + dot + S(9), cy - textSize.Height / 2);
      }
    }

    // Hotkey hint under the chips
    DrawHint(g, w, S(HeaderH + ChipsH) + S(6), "Press", "Ctrl+Alt+B", "to switch mode");

    if (mode == Mode.Solid) DrawPicker(g, w);
    else if (mode == Mode.Rainbow) DrawSpeed(g, w);
  }

  // Centered hint line with the hotkey drawn as a keycap: "<before> [key] <after>".
  void DrawHint(Graphics g, int w, float y, string before, string key, string after) {
    var f = hintFont;
    var fmt = StringFormat.GenericTypographic;
    float sp = S(7), kpx = S(7), kpy = S(2);
    var sb = g.MeasureString(before, f, int.MaxValue, fmt);
    var sk = g.MeasureString(key, f, int.MaxValue, fmt);
    var sa = g.MeasureString(after, f, int.MaxValue, fmt);
    float total = sb.Width + sp + sk.Width + 2 * kpx + sp + sa.Width, x = (w - total) / 2;
    using (var tb = new SolidBrush(Color.FromArgb(150, 255, 255, 255))) {
      g.DrawString(before, f, tb, x, y, fmt);
      x += sb.Width + sp;
      var cap = new RectangleF(x, y - kpy, sk.Width + 2 * kpx, sk.Height + 2 * kpy);
      using (var p = Round(cap, S(6)))
      using (var kb = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
      using (var kp = new Pen(Color.FromArgb(70, 255, 255, 255), 1)) { g.FillPath(kb, p); g.DrawPath(kp, p); }
      using (var kt = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.DrawString(key, f, kt, x + kpx, y, fmt);
      x += cap.Width + sp;
      g.DrawString(after, f, tb, x, y, fmt);
    }
  }

  void DrawModeDot(Graphics g, Mode m, RectangleF r, bool sel) {
    int a = sel ? 255 : 140;
    if (m == Mode.Off) {
      using (var pen = new Pen(Color.FromArgb(a, 150, 150, 160), Math.Max(1.5f, S(2)))) g.DrawEllipse(pen, Inflate(r, -S(1)));
    } else if (m == Mode.Solid) {
      if (sel) using (var glow = new SolidBrush(Color.FromArgb(70, solid))) g.FillEllipse(glow, Inflate(r, S(3)));
      using (var b = new SolidBrush(Color.FromArgb(a, solid))) g.FillEllipse(b, r);
    } else {
      // Rotating hue sweep, like the Aurora effect itself.
      double phase = AuroraPhase();
      using (var br = HueBrush(r, phase, a)) g.FillEllipse(br, r);
    }
  }

  void DrawPicker(Graphics g, int w) {
    float top = S(HeaderH + ChipsH + HintH);
    using (var div = new Pen(Color.FromArgb(28, 255, 255, 255), 1)) g.DrawLine(div, S(Pad + 6), top, w - S(Pad + 6), top);
    var bar = new RectangleF(S(Pad + 10), top + S(18), w - 2 * S(Pad + 10), S(14));
    using (var p = Round(bar, bar.Height / 2))
    using (var br = HueBrush(bar, 0, 255)) {
      g.FillPath(br, p);
      using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1)) g.DrawPath(pen, p);
    }
    // Marker
    double hue = picking ? pickHue : solid.GetHue();
    float mx = bar.X + (float)(hue / 360.0) * bar.Width, my = bar.Y + bar.Height / 2, mr = S(10);
    using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillEllipse(shadow, mx - mr, my - mr + S(2), mr * 2, mr * 2);
    using (var fill = new SolidBrush(solid)) g.FillEllipse(fill, mx - mr, my - mr, mr * 2, mr * 2);
    using (var ring = new Pen(Color.White, Math.Max(2f, S(2.5f)))) g.DrawEllipse(ring, mx - mr, my - mr, mr * 2, mr * 2);

    // Swatch + hex value
    var f = hexFont;
    using (var tb = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) {
      string hex = "#" + (solid.ToArgb() & 0xFFFFFF).ToString("X6");
      var sz = g.MeasureString(hex, f);
      float sw = S(12), total = sw + S(7) + sz.Width, x = (w - total) / 2, y = bar.Bottom + S(12);
      using (var sb = new SolidBrush(solid)) g.FillEllipse(sb, x, y + (sz.Height - sw) / 2, sw, sw);
      g.DrawString(hex, f, tb, x + sw + S(7), y);
    }

    if (picking) DrawHint(g, w, bar.Bottom + S(44), "Release", "Ctrl+Alt+B", "to keep this color");
    else DrawHint(g, w, bar.Bottom + S(44), "Hold", "Ctrl+Alt+B", "to adjust the color");
  }

  // Hue the keyboard is roughly showing now, so the OSD's Aurora visuals move at the chosen speed.
  double AuroraPhase() { return (DateTime.UtcNow.TimeOfDay.TotalSeconds * 360.0 / cycle) % 360; }

  // Aurora row: discrete speed track (Slow ... Fast), one tick per step, value in seconds per cycle.
  void DrawSpeed(Graphics g, int w) {
    float top = S(HeaderH + ChipsH + HintH);
    using (var div = new Pen(Color.FromArgb(28, 255, 255, 255), 1)) g.DrawLine(div, S(Pad + 6), top, w - S(Pad + 6), top);
    var bar = new RectangleF(S(Pad + 10), top + S(18), w - 2 * S(Pad + 10), S(14));
    int steps = App.MaxCycle - App.MinCycle;
    float frac = (App.MaxCycle - cycle) / (float)steps;   // left = slow, right = fast
    float mx = bar.X + frac * bar.Width, my = bar.Y + bar.Height / 2;
    // Calm monochrome track: dim groove, light-gray fill up to the marker.
    var filled = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, mx - bar.X + bar.Height / 2), bar.Height);
    using (var p = Round(bar, bar.Height / 2)) {
      using (var track = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(track, p);
      using (var fp = Round(filled, bar.Height / 2))
      using (var fb = new SolidBrush(Color.FromArgb(150, 205, 208, 216))) g.FillPath(fb, fp);
      using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1)) g.DrawPath(pen, p);
    }
    // One tick per whole-second step: dark on the filled part, light on the groove.
    float tr = Math.Max(1f, S(1.5f)), inset = bar.Height / 2;
    using (var onFill = new SolidBrush(Color.FromArgb(90, 20, 20, 26)))
    using (var onTrack = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
      for (int i = 0; i <= steps; i++) {
        float tx = bar.X + inset + i * (bar.Width - 2 * inset) / steps;
        g.FillEllipse(tx <= mx ? onFill : onTrack, tx - tr, my - tr, tr * 2, tr * 2);
      }
    // Marker
    float mr = S(10);
    mx = Math.Max(bar.X + mr * 0.6f, Math.Min(bar.Right - mr * 0.6f, mx));
    using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillEllipse(shadow, mx - mr, my - mr + S(2), mr * 2, mr * 2);
    using (var fill = new SolidBrush(Color.FromArgb(255, 236, 238, 242))) g.FillEllipse(fill, mx - mr, my - mr, mr * 2, mr * 2);
    using (var ring = new Pen(Color.FromArgb(255, 150, 154, 164), Math.Max(1.5f, S(1.5f)))) g.DrawEllipse(ring, mx - mr, my - mr, mr * 2, mr * 2);

    // "Slow" / value / "Fast"
    float y = bar.Bottom + S(12);
    using (var dim = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) {
      g.DrawString("Slow", hintFont, dim, bar.X, y);
      var fs = g.MeasureString("Fast", hintFont);
      g.DrawString("Fast", hintFont, dim, bar.Right - fs.Width, y);
    }
    using (var tb = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) {
      string v = cycle + " s / cycle";
      var sz = g.MeasureString(v, hexFont);
      g.DrawString(v, hexFont, tb, (w - sz.Width) / 2, y);
    }

    if (picking) DrawHint(g, w, bar.Bottom + S(44), "Release", "Ctrl+Alt+B", "to keep this speed");
    else DrawHint(g, w, bar.Bottom + S(44), "Hold", "Ctrl+Alt+B", "to adjust the speed");
  }

  static LinearGradientBrush HueBrush(RectangleF r, double phase, int alpha) {
    var br = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Color.Red, Color.Red, 0f);
    var blend = new ColorBlend(7);
    for (int i = 0; i < 7; i++) {
      blend.Positions[i] = i / 6f;
      blend.Colors[i] = Color.FromArgb(alpha, LightingWorker.Hsv((i * 60 + phase) % 360, 1, 1));
    }
    br.InterpolationColors = blend;
    return br;
  }

  static RectangleF Inflate(RectangleF r, float d) { r.Inflate(d, d); return r; }

  static GraphicsPath Round(RectangleF r, float rad) {
    var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure(); return p;
  }

  // ---- per-pixel alpha presentation ----
  void Present(Bitmap bmp, Point pos, byte alpha) {
    IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
    IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
    try {
      hBmp = bmp.GetHbitmap(Color.FromArgb(0));
      old = SelectObject(memDc, hBmp);
      var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
      var src = new POINT();
      var dst = new POINT { x = pos.X, y = pos.Y };
      var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
      UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
      Bounds = new Rectangle(pos, bmp.Size);
    } finally {
      ReleaseDC(IntPtr.Zero, screenDc);
      if (hBmp != IntPtr.Zero) { SelectObject(memDc, old); DeleteObject(hBmp); }
      DeleteDC(memDc);
    }
  }

  [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
  [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
  [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
  [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
  [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
