using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Hid {
  [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
  [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
  [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr p);
  [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr p, out CAPS c);
  [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
  [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
  [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DID did);
  [DllImport("setupapi.dll", CharSet=CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DID did, IntPtr buf, int size, out int req, IntPtr di);
  [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
  [DllImport("kernel32.dll", CharSet=CharSet.Auto, SetLastError=true)] public static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
  [StructLayout(LayoutKind.Sequential)] struct SP_DID { public int cbSize; public Guid g; public int flags; public IntPtr r; }
  [StructLayout(LayoutKind.Sequential)] public struct CAPS { public ushort Usage, UsagePage, InLen, OutLen, FeatLen; [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] r; public ushort a,b,c,d,e,f,g,h,i,j; }

  public class Dev { public string Path; public CAPS Caps; }

  public static List<Dev> Find(string vidpid) {
    var list = new List<Dev>();
    Guid g; HidD_GetHidGuid(out g);
    IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12);
    for (int i = 0; ; i++) {
      var did = new SP_DID(); did.cbSize = Marshal.SizeOf(did);
      if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref did)) break;
      int req; SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
      IntPtr buf = Marshal.AllocHGlobal(req);
      Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
      SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out req, IntPtr.Zero);
      string path = Marshal.PtrToStringAuto(new IntPtr(buf.ToInt64() + 4));
      Marshal.FreeHGlobal(buf);
      if (path.IndexOf(vidpid, StringComparison.OrdinalIgnoreCase) < 0) continue;
      using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
        if (h.IsInvalid) continue;
        IntPtr pp; if (!HidD_GetPreparsedData(h, out pp)) continue;
        CAPS c; HidP_GetCaps(pp, out c); HidD_FreePreparsedData(pp);
        list.Add(new Dev { Path = path, Caps = c });
      }
    }
    SetupDiDestroyDeviceInfoList(set);
    return list;
  }

  // Lenovo Legion/LOQ keyboards use ITE controllers (VID 048D). Newer models expose a standard
  // HID LampArray collection (usage page 0x59, usage 0x01); prefer the PID verified on hardware.
  public static Dev FindLampArray() {
    Dev best = null;
    foreach (var d in Find("vid_048d&")) {
      if (d.Caps.UsagePage != 0x59 || d.Caps.Usage != 0x01) continue;
      if (d.Path.IndexOf("pid_c615", StringComparison.OrdinalIgnoreCase) >= 0) return d;
      if (best == null) best = d;
    }
    return best;
  }

  public static SafeFileHandle Open(string path, bool rw) {
    return CreateFile(path, rw ? 0xC0000000u : 0u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
  }
}
