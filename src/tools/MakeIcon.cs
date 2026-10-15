// Build-time tool: draws the app icon (dark keyboard tile with rainbow keys) into a multi-size .ico.
// usage: MakeIcon.exe <out.ico>
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class MakeIcon {
  static GraphicsPath Round(RectangleF r, float rad) {
    var p = new GraphicsPath(); float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure(); return p;
  }

  static Color Hue(double h) {
    double x = 1 - Math.Abs(h / 60 % 2 - 1); double r, g, b;
    if (h < 60) { r = 1; g = x; b = 0; } else if (h < 120) { r = x; g = 1; b = 0; } else if (h < 180) { r = 0; g = 1; b = x; }
    else if (h < 240) { r = 0; g = x; b = 1; } else if (h < 300) { r = x; g = 0; b = 1; } else { r = 1; g = 0; b = x; }
    return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
  }

  static byte[] Draw(int s) {
    using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      float m = s * 0.04f;
      var tile = new RectangleF(m, s * 0.16f, s - 2 * m, s * 0.68f);
      using (var path = Round(tile, s * 0.12f)) using (var br = new SolidBrush(Color.FromArgb(255, 30, 30, 40))) g.FillPath(br, path);
      int rows = s < 32 ? 2 : 3, cols = s < 32 ? 3 : 5;
      float pad = tile.Width * 0.08f, gap = Math.Max(1f, s * 0.035f);
      float kw = (tile.Width - 2 * pad - (cols - 1) * gap) / cols, kh = (tile.Height - 2 * pad - (rows - 1) * gap) / rows;
      for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++) {
          var k = new RectangleF(tile.X + pad + c * (kw + gap), tile.Y + pad + r * (kh + gap), kw, kh);
          var col = Hue((c * 300.0 / Math.Max(1, cols - 1) + r * 25) % 360);
          using (var path = Round(k, Math.Max(0.5f, kw * 0.18f))) using (var br = new SolidBrush(col)) g.FillPath(br, path);
        }
      if (s >= 256) using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
      return Dib(bmp);
    }
  }

  // Classic ICO frame (BITMAPINFOHEADER + bottom-up BGRA + AND mask). System.Drawing on .NET 4
  // cannot convert PNG frames to bitmaps, so only the 256px frame is stored as PNG.
  static byte[] Dib(Bitmap bmp) {
    int s = bmp.Width, maskStride = ((s + 31) / 32) * 4;
    using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms)) {
      w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
      w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
      for (int y = s - 1; y >= 0; y--)
        for (int x = 0; x < s; x++) { var c = bmp.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
      w.Write(new byte[maskStride * s]);
      return ms.ToArray();
    }
  }

  static void Main(string[] a) {
    int[] sizes = { 16, 24, 32, 48, 256 };
    var imgs = new byte[sizes.Length][];
    for (int i = 0; i < sizes.Length; i++) imgs[i] = Draw(sizes[i]);
    using (var w = new BinaryWriter(File.Create(a[0]))) {
      w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
      int off = 6 + 16 * sizes.Length;
      for (int i = 0; i < sizes.Length; i++) {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
        w.Write(imgs[i].Length); w.Write(off); off += imgs[i].Length;
      }
      foreach (var im in imgs) w.Write(im);
    }
  }
}
