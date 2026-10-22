// LegionKbdRgb setup wizard. Per-user install (no admin):
//   files     -> %LOCALAPPDATA%\Programs\LegionKbdRgb (LegionKbdRgb.exe + uninstall.exe = copy of this setup)
//   autostart -> Task Scheduler task "LegionKbdRgb" (at logon, runs on battery, no time limit)
//   ARP entry -> HKCU\...\Uninstall\LegionKbdRgb (Settings > Apps: Modify = repair, Uninstall)
// Command line: /install (fresh-install wizard even if installed), /repair, /uninstall [/quiet]
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("LegionKbdRgb Setup")]
[assembly: AssemblyProduct("LegionKbdRgb")]
[assembly: AssemblyVersion(LegionKbdRgbVersion.Value)]
[assembly: AssemblyFileVersion(LegionKbdRgbVersion.Value)]

static class Product {
  public const string AppName = "LegionKbdRgb";
  public const string DisplayName = "LegionKbdRgb Keyboard Backlight";
  public const string TaskName = "LegionKbdRgb";
  public const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LegionKbdRgb";
  public const string ExeName = AppName + ".exe";
  public const string UninstallerName = "uninstall.exe";

  public static string DefaultDir {
    get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
  }
  public static string ShortcutPath {
    get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk"); }
  }

  public static string InstalledDir() { return RegValue("InstallLocation"); }
  public static string InstalledVersion() { return RegValue("DisplayVersion"); }
  static string RegValue(string name) {
    using (var k = Registry.CurrentUser.OpenSubKey(UninstKey)) return k == null ? null : k.GetValue(name) as string;
  }

  public static bool KeyboardPresent() {
    try { return Hid.FindLampArray() != null; } catch { return false; }
  }
}

static class Installer {
  // Returns false when runNow was requested but the application did not start (for example,
  // blocked by Smart App Control or antivirus); the wizard then tells the user what to check.
  public static bool Install(string dir, bool shortcut, bool runNow, Action<string> log) {
    log("Stopping running instances of LegionKbdRgb");
    StopApp();

    log("Copying files to " + dir);
    Directory.CreateDirectory(dir);
    string exe = Path.Combine(dir, Product.ExeName);
    using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(Product.ExeName))
    using (var dst = File.Create(exe)) src.CopyTo(dst);
    string uninst = Path.Combine(dir, Product.UninstallerName);
    if (!SamePath(Application.ExecutablePath, uninst)) File.Copy(Application.ExecutablePath, uninst, true);

    log("Registering the sign-in task");
    RegisterTask(exe);

    if (shortcut) { log("Creating the Start menu shortcut"); CreateShortcut(Product.ShortcutPath, exe); }
    else if (File.Exists(Product.ShortcutPath)) File.Delete(Product.ShortcutPath);

    log("Registering with Installed apps");
    using (var k = Registry.CurrentUser.CreateSubKey(Product.UninstKey)) {
      k.SetValue("DisplayName", Product.DisplayName);
      k.SetValue("DisplayVersion", LegionKbdRgbVersion.Display);
      k.SetValue("Publisher", Product.AppName);
      k.SetValue("InstallLocation", dir);
      k.SetValue("DisplayIcon", exe + ",0");
      k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
      k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /quiet");
      k.SetValue("ModifyPath", "\"" + uninst + "\" /repair");
      k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
      k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
      k.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(uninst).Length) / 1024), RegistryValueKind.DWord);
      k.SetValue("StartMenuShortcut", shortcut ? 1 : 0, RegistryValueKind.DWord);
    }

    bool started = true;
    if (runNow) {
      log("Starting LegionKbdRgb");
      Schtasks("/run /tn " + Product.TaskName, false);
      started = WaitForApp(8000);
      log(started ? "LegionKbdRgb is running" : "Warning: LegionKbdRgb did not start");
    }
    log("Completed.");
    return started;
  }

  static bool WaitForApp(int timeoutMs) {
    var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    do {
      if (Process.GetProcessesByName(Product.AppName).Length > 0) return true;
      Thread.Sleep(250);
    } while (DateTime.UtcNow < until);
    return false;
  }

  public static void Uninstall(Action<string> log) {
    string dir = Product.InstalledDir() ?? AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

    log("Stopping LegionKbdRgb (keyboard lighting reverts to the firmware default)");
    StopApp();

    log("Removing the sign-in task");
    Schtasks("/delete /tn " + Product.TaskName + " /f", false);

    log("Removing the shortcut and Installed apps entry");
    if (File.Exists(Product.ShortcutPath)) File.Delete(Product.ShortcutPath);
    Registry.CurrentUser.DeleteSubKeyTree(Product.UninstKey, false);

    log("Deleting files from " + dir);
    foreach (var name in new[] { Product.ExeName, "state.txt", "log.txt", "log.old.txt" }) {
      var p = Path.Combine(dir, name);
      if (File.Exists(p)) File.Delete(p);
    }
    string uninst = Path.Combine(dir, Product.UninstallerName);
    if (SamePath(Application.ExecutablePath, uninst)) {
      // We are running from the folder being removed: delete ourselves after exit.
      // "rd" without /s only removes the folder if nothing else is left in it.
      var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + uninst + "\" & rd \"" + dir + "\"") {
        CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath()
      };
      Process.Start(psi);
    } else {
      if (File.Exists(uninst)) File.Delete(uninst);
      try { Directory.Delete(dir, false); } catch (IOException) { }
    }
    log("Completed.");
  }

  static void StopApp() {
    try { using (var ev = EventWaitHandle.OpenExisting("LegionKbdRgb.Exit")) ev.Set(); } catch { }
    foreach (var p in Process.GetProcessesByName(Product.AppName)) {
      try { if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
    }
  }

  static void RegisterTask(string exe) {
    string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
    string xml =
      "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
      "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
      "  <RegistrationInfo><Description>Keyboard backlight control (Ctrl+Alt+B)</Description></RegistrationInfo>\r\n" +
      "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>\r\n" +
      "  <Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
      "  <Settings>\r\n" +
      "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
      "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
      "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
      "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
      "    <Priority>7</Priority>\r\n" +
      "    <RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure>\r\n" +
      "  </Settings>\r\n" +
      "  <Actions Context=\"Author\"><Exec><Command>\"" + SecurityElement.Escape(exe) + "\"</Command></Exec></Actions>\r\n" +
      "</Task>\r\n";
    string tmp = Path.Combine(Path.GetTempPath(), "LegionKbdRgb-task.xml");
    File.WriteAllText(tmp, xml, Encoding.Unicode);
    try { Schtasks("/create /tn " + Product.TaskName + " /xml \"" + tmp + "\" /f", true); }
    finally { File.Delete(tmp); }
  }

  static void Schtasks(string args, bool mustSucceed) {
    var psi = new ProcessStartInfo("schtasks.exe", args) {
      CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
    };
    using (var p = Process.Start(psi)) {
      string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
      if (mustSucceed && p.ExitCode != 0) throw new Exception("Failed to register the scheduled task: " + err.Trim());
    }
  }

  static void CreateShortcut(string lnkPath, string target) {
    var t = Type.GetTypeFromProgID("WScript.Shell");
    object shell = Activator.CreateInstance(t);
    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
    var lt = lnk.GetType();
    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
    lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Keyboard backlight control (Ctrl+Alt+B)" });
    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
  }

  static bool SamePath(string a, string b) {
    return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
  }
}

class Wizard : Form {
  enum Op { Install, Repair, Uninstall }

  readonly Label title = new Label(), subtitle = new Label();
  readonly Panel body = new Panel();
  readonly Button back = new Button(), next = new Button(), cancel = new Button();
  readonly Panel welcome = new Panel(), options = new Panel(), maintenance = new Panel(), progress = new Panel(), finish = new Panel();
  readonly TextBox dirBox = new TextBox();
  readonly CheckBox shortcutBox = new CheckBox(), runBox = new CheckBox();
  readonly RadioButton repairRadio = new RadioButton(), uninstallRadio = new RadioButton();
  readonly ListBox logBox = new ListBox();
  readonly ProgressBar bar = new ProgressBar();
  readonly Label finishText = new Label();
  readonly string installedDir;
  Panel page;
  bool busy;

  public Wizard(bool startUninstall, bool forceInstall) {
    installedDir = Product.InstalledDir();
    SuspendLayout();
    AutoScaleDimensions = new SizeF(96F, 96F);
    AutoScaleMode = AutoScaleMode.Dpi;
    Font = new Font("Segoe UI", 9F);
    Text = Product.DisplayName + " Setup";
    ClientSize = new Size(560, 400);
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false; MinimizeBox = false;
    StartPosition = FormStartPosition.CenterScreen;
    Icon = AppIcon(32);

    var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Color.White };
    title.SetBounds(20, 12, 420, 24); title.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
    subtitle.SetBounds(32, 38, 420, 22);
    title.BackColor = subtitle.BackColor = Color.Transparent;
    var logo = AppIcon(48);
    header.Paint += (s, e) => {
      int sz = Math.Min(header.Height - 16, 56);
      e.Graphics.DrawIcon(logo, new Rectangle(header.Width - sz - 16, (header.Height - sz) / 2, sz, sz));
    };
    header.Controls.AddRange(new Control[] { title, subtitle });
    var line1 = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };

    var footer = new Panel { Dock = DockStyle.Bottom, Height = 52 };
    var line2 = new Label { Dock = DockStyle.Top, Height = 1, BackColor = SystemColors.ControlDark };
    back.SetBounds(282, 14, 84, 26); back.Text = "< Back";
    next.SetBounds(368, 14, 84, 26); next.Text = "Next >";
    cancel.SetBounds(462, 14, 84, 26); cancel.Text = "Cancel";
    footer.Controls.AddRange(new Control[] { line2, back, next, cancel });

    body.Dock = DockStyle.Fill; body.Padding = new Padding(28, 18, 28, 10);
    Controls.Add(body); Controls.Add(line1); Controls.Add(header); Controls.Add(footer);

    BuildWelcome(); BuildOptions(); BuildMaintenance(); BuildProgress(); BuildFinish();
    foreach (var p in new[] { welcome, options, maintenance, progress, finish }) { p.Dock = DockStyle.Fill; p.Visible = false; body.Controls.Add(p); }

    back.Click += (s, e) => { if (page == options) Show(welcome); };
    next.Click += (s, e) => OnNext();
    cancel.Click += (s, e) => Close();
    AcceptButton = next;
    ResumeLayout(false);

    if (startUninstall) { uninstallRadio.Checked = true; Load += (s, e) => Run(Op.Uninstall); }
    else Show(installedDir != null && !forceInstall ? maintenance : welcome);
  }

  static Icon AppIcon(int size) {
    using (var s = typeof(Wizard).Assembly.GetManifestResourceStream("app.ico")) return new Icon(s, size, size);
  }

  Label AddLabel(Panel p, string text, int y, int h) {
    var l = new Label { Text = text, Bounds = new Rectangle(0, y, 500, h) };
    p.Controls.Add(l); return l;
  }

  void BuildWelcome() {
    AddLabel(welcome,
      "This wizard installs LegionKbdRgb, a lightweight replacement for the keyboard\n" +
      "lighting controls in Lenovo Legion Space.\n\n" +
      "  •  Ctrl+Alt+B cycles the backlight:  Off → Solid color → Aurora\n" +
      "  •  In solid-color mode, hold Ctrl+Alt+B to sweep through colors; release to keep one.\n" +
      "  •  Modes are also available from the notification area icon.\n" +
      "  •  Starts automatically when you sign in to Windows.\n" +
      "  •  Installs for the current user only; administrator rights are not required.\n\n" +
      "Verified on: Lenovo Legion 5 15AHP11 (ITE keyboard controller 048D:C615)", 0, 205);
    if (!Product.KeyboardPresent()) {
      var warn = AddLabel(welcome, "⚠ No compatible keyboard lighting controller was detected on this PC.\n    Setup can continue, but the application may have no effect.", 212, 40);
      warn.ForeColor = Color.DarkRed;
    }
  }

  void BuildOptions() {
    AddLabel(options, "Destination folder:", 0, 20);
    dirBox.SetBounds(0, 24, 404, 24); dirBox.Text = Product.DefaultDir;
    var browse = new Button { Text = "Browse...", Bounds = new Rectangle(412, 23, 88, 26) };
    browse.Click += (s, e) => {
      using (var dlg = new FolderBrowserDialog { SelectedPath = dirBox.Text, Description = "Select the folder to install LegionKbdRgb in." })
        if (dlg.ShowDialog(this) == DialogResult.OK) dirBox.Text = Path.Combine(dlg.SelectedPath, Product.AppName);
    };
    shortcutBox.Text = "Create a Start menu shortcut"; shortcutBox.Checked = true; shortcutBox.SetBounds(0, 70, 480, 24);
    runBox.Text = "Launch LegionKbdRgb when Setup completes"; runBox.Checked = true; runBox.SetBounds(0, 98, 480, 24);
    options.Controls.AddRange(new Control[] { dirBox, browse, shortcutBox, runBox });
  }

  void BuildMaintenance() {
    AddLabel(maintenance, "LegionKbdRgb " + (Product.InstalledVersion() ?? "") + " is already installed on this computer.\nLocation: " + installedDir, 0, 44);
    repairRadio.Text = "Repair / Reinstall (version " + LegionKbdRgbVersion.Display + ")"; repairRadio.Checked = true; repairRadio.SetBounds(0, 60, 480, 24);
    AddLabel(maintenance, "Reinstalls the program files, sign-in task and shortcut.\nYour current lighting mode is preserved.", 84, 36).Padding = new Padding(18, 0, 0, 0);
    uninstallRadio.Text = "Remove"; uninstallRadio.SetBounds(0, 126, 480, 24);
    AddLabel(maintenance, "Stops the application and removes its files, sign-in task\nand shortcut.", 150, 36).Padding = new Padding(18, 0, 0, 0);
    maintenance.Controls.AddRange(new Control[] { repairRadio, uninstallRadio });
  }

  void BuildProgress() {
    bar.SetBounds(0, 0, 500, 18); bar.Style = ProgressBarStyle.Marquee;
    logBox.SetBounds(0, 30, 500, 180); logBox.IntegralHeight = false;
    progress.Controls.AddRange(new Control[] { bar, logBox });
  }

  void BuildFinish() {
    finishText.SetBounds(0, 0, 500, 200);
    finish.Controls.Add(finishText);
  }

  void Show(Panel p) {
    if (page != null) page.Visible = false;
    page = p; p.Visible = true;
    back.Visible = p == options;
    next.Enabled = !busy; cancel.Enabled = !busy && p != finish;
    cancel.Visible = p != finish;
    next.Text = p == options ? "Install" : p == maintenance ? "Continue" : p == finish ? "Finish" : "Next >";
    if (p == welcome) { title.Text = "Welcome to LegionKbdRgb Setup"; subtitle.Text = "Keyboard backlight control for Lenovo Legion laptops, version " + LegionKbdRgbVersion.Display; }
    else if (p == options) { title.Text = "Installation Options"; subtitle.Text = "Choose the destination folder and additional options."; }
    else if (p == maintenance) { title.Text = "Program Maintenance"; subtitle.Text = "Repair or remove the installed application."; }
  }

  void OnNext() {
    if (page == welcome) Show(options);
    else if (page == options) Run(Op.Install);
    else if (page == maintenance) {
      if (uninstallRadio.Checked && MessageBox.Show(this, "Are you sure you want to completely remove LegionKbdRgb?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
      Run(uninstallRadio.Checked ? Op.Uninstall : Op.Repair);
    }
    else if (page == finish) Close();
  }

  void Run(Op op) {
    busy = true;
    Show(progress);
    title.Text = op == Op.Uninstall ? "Removing LegionKbdRgb" : op == Op.Repair ? "Repairing LegionKbdRgb" : "Installing LegionKbdRgb";
    subtitle.Text = "Please wait while Setup completes the requested operation.";
    string dir = op == Op.Install ? dirBox.Text.Trim() : installedDir;
    bool shortcut = op == Op.Install ? shortcutBox.Checked : RegShortcut();
    bool runNow = op == Op.Install ? runBox.Checked : op == Op.Repair;
    Action<string> log = m => BeginInvoke(new Action(() => { logBox.Items.Add(m); logBox.TopIndex = logBox.Items.Count - 1; }));
    new Thread(() => {
      Exception err = null;
      bool started = true;
      try {
        if (op == Op.Uninstall) Installer.Uninstall(log);
        else started = Installer.Install(dir, shortcut, runNow, log);
      } catch (Exception ex) { err = ex; }
      BeginInvoke(new Action(() => Done(op, err, started)));
    }) { IsBackground = true }.Start();
  }

  static bool RegShortcut() {
    using (var k = Registry.CurrentUser.OpenSubKey(Product.UninstKey)) return k == null || !(k.GetValue("StartMenuShortcut") is int) || (int)k.GetValue("StartMenuShortcut") != 0;
  }

  void Done(Op op, Exception err, bool started) {
    busy = false;
    bar.Style = ProgressBarStyle.Continuous; bar.Value = bar.Maximum;
    if (err != null) {
      title.Text = "Setup Failed"; subtitle.Text = err.Message;
      logBox.Items.Add("Error: " + err.Message);
      next.Text = "Close"; next.Enabled = true; page = finish; cancel.Visible = false;
      return;
    }
    title.Text = op == Op.Uninstall ? "Removal Complete" : op == Op.Repair ? "Repair Complete" : "Installation Complete";
    subtitle.Text = op == Op.Uninstall ? "LegionKbdRgb has been removed from this computer." : "LegionKbdRgb is installed and ready to use.";
    finishText.Text = op == Op.Uninstall
      ? "LegionKbdRgb has been successfully removed.\n\nKeyboard lighting has been returned to the firmware default."
      : "Press Ctrl+Alt+B to cycle the keyboard backlight:\n\n    Off → Solid color → Aurora\n" +
        "In solid-color mode, hold Ctrl+Alt+B to choose a color.\n\n" +
        "LegionKbdRgb starts automatically when you sign in. To repair or remove it, open\n" +
        "Settings > Apps > Installed apps and select LegionKbdRgb Keyboard Backlight.";
    if (!started) {
      subtitle.Text = "Installed, but the application did not start.";
      finishText.ForeColor = Color.DarkRed;
      finishText.Text =
        "LegionKbdRgb was installed, but Windows did not allow it to start.\n\n" +
        "This usually means Smart App Control or antivirus software blocked the program.\n" +
        "Check Windows Security > Protection history, and use an officially signed\n" +
        "release from the project's GitHub page.";
    }
    Show(finish);
  }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    if (busy) e.Cancel = true;
    base.OnFormClosing(e);
  }

  [STAThread]
  static void Main(string[] args) {
    bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
    bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
    if (uninstall && quiet) {
      try { Installer.Uninstall(_ => { }); } catch { Environment.Exit(1); }
      return;
    }
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    if (uninstall) {
      if (Product.InstalledDir() == null) { MessageBox.Show("LegionKbdRgb is not installed on this computer.", Product.DisplayName); return; }
      if (MessageBox.Show("Are you sure you want to completely remove LegionKbdRgb?", Product.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
    }
    bool created;
    using (var m = new Mutex(true, "LegionKbdRgb.Setup", out created)) {
      if (!created) return;
      Application.Run(new Wizard(uninstall, args.Any(a => a.Equals("/install", StringComparison.OrdinalIgnoreCase))));
    }
  }
}
