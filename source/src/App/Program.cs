using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LinkPilot.Core;
using Microsoft.Win32;

namespace LinkPilot.App
{
    internal static class Program
    {
        private const string InstanceMutex = @"Local\LinkPilot.SingleInstance";
        private const string ShowEvent = @"Local\LinkPilot.ShowWindow";

        [STAThread]
        private static void Main(string[] args)
        {
            bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            using (var single = new Mutex(true, InstanceMutex, out bool first))
            {
                if (!first)
                {
                    // Already running: ask that copy to pop its window up, then bow out.
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEvent)) ev.Set(); } catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);

                Log.Write("LinkPilot starting" + (startInTray ? " (tray)" : ""));
                var settings = AppSettings.Load();
                Autostart.Repair();
                using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent))
                using (var app = new TrayApp(settings, !startInTray, showSignal))
                {
                    Application.Run(app);
                }
                Log.Write("LinkPilot stopped");
            }
        }
    }

    /// <summary>Owns the tray icon, the control loop and the (lazily created) main window.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Controller _ctl;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _blastItem;
        private readonly System.Windows.Forms.Timer _poll;
        private readonly Icon _iconOk, _iconHot, _iconOff;
        private readonly Control _invoker;
        private readonly RegisteredWaitHandle _showWait;
        private MainForm? _form;
        private bool _exiting;
        private Icon? _currentIcon;

        public TrayApp(AppSettings settings, bool showWindow, EventWaitHandle showSignal)
        {
            _ctl = new Controller(settings);

            _iconOk = IconFactory.MakeIcon(32, Theme.Accent);
            _iconHot = IconFactory.MakeIcon(32, Theme.Danger);
            _iconOff = IconFactory.MakeIcon(32, Theme.Dim);

            _invoker = new Control();
            _invoker.CreateControl();

            var menu = new ContextMenuStrip();
            var open = new ToolStripMenuItem("Open LinkPilot", null, (s, e) => ShowWindow()) { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            _blastItem = new ToolStripMenuItem("Full blast (everything 100%)", null, (s, e) => ToggleBlast()) { CheckOnClick = false };
            var exit = new ToolStripMenuItem("Exit", null, (s, e) => ExitApp());
            menu.Items.AddRange(new ToolStripItem[] { open, new ToolStripSeparator(), _blastItem, new ToolStripSeparator(), exit });

            _tray = new NotifyIcon
            {
                Icon = _iconOff,
                Text = "LinkPilot",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _currentIcon = _iconOff;
            _tray.DoubleClick += (s, e) => ShowWindow();
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

            _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
                (st, timedOut) => { try { _invoker.BeginInvoke((Action)ShowWindow); } catch { } }, null, -1, false);

            SystemEvents.SessionEnding += (s, e) => Shutdown();

            _poll = new System.Windows.Forms.Timer { Interval = 1000 };
            _poll.Tick += (s, e) => Poll();
            _poll.Start();

            _ctl.Start();

            if (showWindow)
            {
                ShowWindow();
            }
            else
            {
                _tray.ShowBalloonTip(2500, "LinkPilot is running", "Fan + pump control is active. Double-click the tray icon to open.", ToolTipIcon.Info);
            }
        }

        private void ShowWindow()
        {
            if (_exiting) return;
            if (_form == null || _form.IsDisposed)
            {
                _form = new MainForm(_ctl, () => _exiting);
                _form.FullBlastToggled += (s, e) => _blastItem.Checked = _ctl.FullBlast;
                // "Close button hides to tray" is off, so the close button means quit.
                _form.ExitRequested += (s, e) => { try { _invoker.BeginInvoke((Action)ExitApp); } catch { } };
            }
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            _form.BringToFront();
        }

        private void ToggleBlast()
        {
            _ctl.FullBlast = !_ctl.FullBlast;
            _blastItem.Checked = _ctl.FullBlast;
            _form?.SetFullBlast(_ctl.FullBlast);
        }

        private void Poll()
        {
            var snap = _ctl.Latest;

            var icon = !snap.Connected ? _iconOff : (snap.Alarm != null || snap.FullBlast) ? _iconHot : _iconOk;
            if (!ReferenceEquals(icon, _currentIcon)) { _tray.Icon = icon; _currentIcon = icon; }

            string tip;
            if (!snap.Connected) tip = "LinkPilot · hub not connected";
            else
            {
                tip = "LinkPilot";
                if (snap.CoolantTemp.HasValue) tip += $" · {snap.CoolantTemp.Value:0.0}°C";
                if (snap.PumpRpm.HasValue) tip += $" · pump {snap.PumpRpm.Value} rpm";
                if (snap.Alarm != null) tip = "⚠ " + snap.Alarm;
            }
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // NotifyIcon limit

            _blastItem.Checked = snap.FullBlast;

            while (_ctl.Notifications.TryDequeue(out var n))
            {
                _tray.ShowBalloonTip(5000, n.Title, n.Text, n.IsWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
        }

        private void ExitApp()
        {
            Shutdown();
            ExitThread();
        }

        private void Shutdown()
        {
            if (_exiting) return;
            _exiting = true;
            _poll.Stop();
            try { _ctl.Dispose(); } catch (Exception ex) { Log.Write("Shutdown error: " + ex.Message); }
            _tray.Visible = false;
            _form?.Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Shutdown();
                _showWait.Unregister(null);
                _tray.Dispose();
                _poll.Dispose();
                _invoker.Dispose();
                _iconOk.Dispose();
                _iconHot.Dispose();
                _iconOff.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LinkPilot";

        private static string Command => $"\"{Application.ExecutablePath}\" --tray";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>If LinkPilot.exe was moved, point the Windows startup entry at where it lives now.</summary>
        public static void Repair()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k?.GetValue(ValueName) is string s && s.Length > 0 && !s.Equals(Command, StringComparison.OrdinalIgnoreCase))
                    {
                        k.SetValue(ValueName, Command);
                        Log.Write("Startup entry pointed at an old location; updated it to " + Application.ExecutablePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Couldn't check the startup entry: " + ex.Message);
            }
        }

        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(ValueName, Command);
                    else k.DeleteValue(ValueName, false);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Autostart change failed: " + ex.Message);
                return false;
            }
        }
    }
}
