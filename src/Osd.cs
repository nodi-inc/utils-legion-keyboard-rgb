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
  const int W = 380, HeaderH = 34, ChipsH = 46, PickerH = 62, Pad = 14, Radius = 18;
  const int FadeInMs = 120, FadeOutMs = 260, VisibleMs = 1400;

  static readonly string[] Names = { "Off", "Solid", "Aurora" };

  readonly Timer frame = new Timer { Interval = 16 };
  readonly float scale;
  Mode mode;
  Color solid;
  bool picking, showPicker;
  double pickHue;
  DateTime shownAt, lastInput;
  double opacity;     // 0..1, animated
  bool visible;
  int anchorLeft, anchorBottom;   // panel grows upward when the picker row appears
  Font capFont, hintFont, labelFont, hexFont;

  public Osd() {
    FormBorderStyle = FormBorderStyle.None;
    ShowInTaskbar = false;
    StartPosition = FormStartPosition.Manual;
    TopMost = true;
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
    capFont = Px("Segoe UI Semibold", 7.5f);
    hintFont = Px("Segoe UI", 7.5f);
    labelFont = Px("Segoe UI Semibold", 10f);
    hexFont = Px("Consolas", 9f);
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
  public void ShowMode(Mode m, Color c) {
    mode = m; solid = c; picking = false;
    Touch();
  }

  // Color pick in progress: hue in degrees, resulting color.
  public void ShowPicker(double hue, Color c) {
    mode = Mode.Solid; solid = c; pickHue = hue; picking = true; showPicker = true;
    Touch();
  }

  void Touch() {
    lastInput = DateTime.UtcNow;
    if (!picking && mode != Mode.Solid) showPicker = false;
    if (!visible) {
      visible = true; shownAt = DateTime.UtcNow; opacity = 0;
      if (!picking) showPicker = false;
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

  int PanelHeight { get { return HeaderH + ChipsH + Pad + (showPicker ? PickerH : 0); } }

  void PlaceOnActiveMonitor() {
    var fg = GetForegroundWindow();
    var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
    var wa = screen.WorkingArea;
    anchorLeft = wa.Left + (wa.Width - S(W)) / 2;
    anchorBottom = wa.Bottom - S(64);
  }

  void Animate() {
    var now = DateTime.UtcNow;
    double sinceInput = (now - lastInput).TotalMilliseconds;
    double target;
    if (picking || sinceInput < VisibleMs) target = 1;
    else target = Math.Max(0, 1 - (sinceInput - VisibleMs) / FadeOutMs);
    double fadeIn = Math.Min(1, (now - shownAt).TotalMilliseconds / FadeInMs);
    opacity = Math.Min(target, fadeIn);

    if (!picking && sinceInput >= VisibleMs + FadeOutMs) {
      frame.Stop(); visible = false; ShowWindow(Handle, 0 /* SW_HIDE */);
      return;
    }
    Render();
  }

  void Render() {
    int w = S(W), h = S(PanelHeight);
    var target = new Point(anchorLeft, anchorBottom - h);
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) Draw(g, w, h);
      Present(bmp, target, (byte)Math.Round(255 * EaseOut(opacity)));
    }
  }

  // Development aid: render the panel to a PNG without showing a window (--render-osd).
  public static void RenderPreview(string path, Mode m, Color c, bool picker, double hue) {
    var o = new Osd { mode = m, solid = c, showPicker = picker, picking = picker, pickHue = hue };
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

    // Header: caption on the left, accent swatch strip on the right
    var cap = capFont; var hint = hintFont;
    using (var capBrush = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
      g.DrawString("KEYBOARD BACKLIGHT", cap, capBrush, S(Pad + 2), S(12));
    using (var hb = new SolidBrush(Color.FromArgb(110, 255, 255, 255))) {
      string t = picking ? "Release to keep" : "Ctrl+Alt+B";
      var sz = g.MeasureString(t, hint);
      g.DrawString(t, hint, hb, w - S(Pad + 2) - sz.Width, S(12));
    }

    // Mode chips
    float gap = S(6), chipW = (w - 2 * S(Pad) - 2 * gap) / 3f, chipH = S(ChipsH - 8), y = S(HeaderH);
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
        float dot = S(12), contentW = dot + S(8) + textSize.Width;
        float x0 = r.X + (r.Width - contentW) / 2, cy = r.Y + r.Height / 2;
        DrawModeDot(g, (Mode)i, new RectangleF(x0, cy - dot / 2, dot, dot), sel);
        using (var tb = new SolidBrush(sel ? Color.White : Color.FromArgb(120, 255, 255, 255)))
          g.DrawString(Names[i], label, tb, x0 + dot + S(8), cy - textSize.Height / 2);
      }
    }

    if (showPicker) DrawPicker(g, w);
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
      double phase = (DateTime.UtcNow.TimeOfDay.TotalSeconds * 45) % 360;
      using (var br = HueBrush(r, phase, a)) g.FillEllipse(br, r);
    }
  }

  void DrawPicker(Graphics g, int w) {
    float top = S(HeaderH + ChipsH + 4);
    var bar = new RectangleF(S(Pad + 6), top + S(10), w - 2 * S(Pad + 6), S(12));
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
      float sw = S(10), total = sw + S(6) + sz.Width, x = (w - total) / 2, y = bar.Bottom + S(10);
      using (var sb = new SolidBrush(solid)) g.FillEllipse(sb, x, y + (sz.Height - sw) / 2, sw, sw);
      g.DrawString(hex, f, tb, x + sw + S(6), y);
    }
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
