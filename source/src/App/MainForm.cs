using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LinkPilot.Core;
using LinkPilot.Link;

namespace LinkPilot.App
{
    internal sealed class MainForm : Form
    {
        private readonly Controller _ctl;
        private readonly Func<bool> _exiting;
        private readonly Label _status, _coolant, _coolantSrc, _pump, _alarm, _empty, _lockInfo;
        private readonly CheckBox _fullBlast;
        private readonly FlowLayoutPanel _list;
        private readonly Timer _ui;
        private readonly Dictionary<string, DeviceRow> _rows = new Dictionary<string, DeviceRow>();
        private string _layoutKey = "";
        private readonly CheckBox _autostart, _closeToTray, _handBack;
        private readonly NumericUpDown _alarmTemp, _pumpMin;
        private bool _syncing;

        public event EventHandler? FullBlastToggled;
        /// <summary>Raised when the user closes the window and it isn't set to hide to the tray.</summary>
        public event EventHandler? ExitRequested;

        public MainForm(Controller ctl, Func<bool> exiting)
        {
            _ctl = ctl;
            _exiting = exiting;

            Text = "LinkPilot — iCUE LINK fan & pump control";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            ClientSize = new Size(Theme.S(860), Theme.S(620));
            MinimumSize = new Size(Theme.S(720), Theme.S(420));
            StartPosition = FormStartPosition.CenterScreen;
            Icon = IconFactory.MakeIcon(32, Theme.Accent);

            // ---------- header
            var header = new Panel { Dock = DockStyle.Top, Height = Theme.S(86), BackColor = Theme.Bg, Padding = new Padding(Theme.S(16)) };
            var logo = new PictureBox { Image = IconFactory.DrawBitmap(Theme.S(48), Theme.Accent), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.S(16), Theme.S(18)) };
            var title = Theme.Label("LinkPilot", 18f, Theme.Text, FontStyle.Bold);
            title.Location = new Point(Theme.S(72), Theme.S(14));
            _status = Theme.Label("Starting…", 9f, Theme.Dim);
            _status.Location = new Point(Theme.S(75), Theme.S(52));

            _coolant = Theme.Label("--.- °C", 20f, Theme.Accent, FontStyle.Bold);
            _coolantSrc = Theme.Label("coolant", 8f, Theme.Dim);
            _pump = Theme.Label("", 9f, Theme.Pump, FontStyle.Bold);

            _fullBlast = new CheckBox
            {
                Appearance = Appearance.Button,
                Text = "FULL BLAST",
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.Ui(9f, FontStyle.Bold),
                AutoSize = true,
                Padding = new Padding(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(4)),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            _fullBlast.FlatAppearance.BorderColor = Theme.Line;
            _fullBlast.FlatAppearance.CheckedBackColor = Theme.Danger;
            _fullBlast.CheckedChanged += (s, e) =>
            {
                if (_syncing) return;
                _ctl.FullBlast = _fullBlast.Checked;
                FullBlastToggled?.Invoke(this, EventArgs.Empty);
            };
            new ToolTip().SetToolTip(_fullBlast, "Everything to 100% right now (benchmarks, bleeding the loop, panic button)");

            header.Controls.AddRange(new Control[] { logo, title, _status, _coolant, _coolantSrc, _pump, _fullBlast });
            header.Resize += (s, e) => LayoutHeader(header);

            _alarm = new Label
            {
                Dock = DockStyle.Top,
                Height = Theme.S(34),
                BackColor = Theme.Danger,
                ForeColor = Color.White,
                Font = Theme.Ui(10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false,
            };

            // ---------- device list
            _list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Bg,
                Padding = new Padding(0, Theme.S(4), 0, Theme.S(8)),
            };
            _list.HorizontalScroll.Enabled = false;
            _list.HorizontalScroll.Visible = false;
            _list.Resize += (s, e) => SizeRows();

            _empty = Theme.Label("", 10f, Theme.Dim);
            _empty.AutoSize = false;
            _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.Dock = DockStyle.Fill;

            // ---------- footer
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Theme.S(96),
                BackColor = Theme.Card,
                Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(14), Theme.S(6)),
                ColumnCount = 3,
                RowCount = 3,
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            _autostart = Check("Start with Windows (in the tray)");
            _closeToTray = Check("Close button hides to tray");
            _handBack = Check("Hand control back to the hub on exit");
            new ToolTip().SetToolTip(_handBack, "On: the hub returns to its own built-in behaviour when LinkPilot quits.\nOff: LinkPilot leaves the pump at 100% and fans at ≥60%.");

            _alarmTemp = Numeric(30, 70, 45);
            _pumpMin = Numeric(20, 100, 50);
            var alarmRow = Inline(Theme.Label("Safety: everything 100% at coolant ≥", 9f, Theme.Dim), _alarmTemp, Theme.Label("°C", 9f, Theme.Dim));
            var pumpRow = Inline(Theme.Label("Pump never below", 9f, Theme.Dim), _pumpMin, Theme.Label("%", 9f, Theme.Dim));

            var openLog = new LinkLabel { Text = "Open log + settings folder", UseMnemonic = false, AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, BackColor = Color.Transparent };
            openLog.LinkClicked += (s, e) => { try { System.IO.Directory.CreateDirectory(AppSettings.Folder); Process.Start("explorer.exe", AppSettings.Folder); } catch { } };
            _lockInfo = Theme.Label("", 8f, Theme.Dim);
            var ver = Theme.Label("v" + typeof(MainForm).Assembly.GetName().Version.ToString(3), 8f, Theme.Dim);

            footer.Controls.Add(_autostart, 0, 0);
            footer.Controls.Add(_closeToTray, 0, 1);
            footer.Controls.Add(_handBack, 0, 2);
            footer.Controls.Add(alarmRow, 1, 0);
            footer.Controls.Add(pumpRow, 1, 1);
            footer.Controls.Add(openLog, 2, 0);
            footer.Controls.Add(_lockInfo, 1, 2);
            footer.Controls.Add(ver, 2, 2);

            Controls.Add(_list);
            Controls.Add(_empty);
            Controls.Add(_alarm);
            Controls.Add(header);
            Controls.Add(footer);

            LoadFooter();
            _autostart.CheckedChanged += (s, e) =>
            {
                if (_syncing) return;
                bool on = _autostart.Checked;
                if (!Autostart.Set(on)) { _syncing = true; _autostart.Checked = !on; _syncing = false; return; }
                _ctl.Edit(x => x.StartWithWindows = on);
            };
            _closeToTray.CheckedChanged += (s, e) => { if (!_syncing) { var v = _closeToTray.Checked; _ctl.Edit(x => x.CloseToTray = v); } };
            _handBack.CheckedChanged += (s, e) => { if (!_syncing) { var v = _handBack.Checked; _ctl.Edit(x => x.ReturnToHardwareModeOnExit = v); } };
            _alarmTemp.ValueChanged += (s, e) => { if (!_syncing) { var v = (double)_alarmTemp.Value; _ctl.Edit(x => x.AlarmCoolantTemp = v); } };
            _pumpMin.ValueChanged += (s, e) =>
            {
                if (_syncing) return;
                var v = (int)_pumpMin.Value;
                _ctl.Edit(x => x.PumpMinimumPercent = v);
                foreach (var r in _rows.Values) r.RefreshPumpMinimum();
            };

            _ui = new Timer { Interval = 500 };
            _ui.Tick += (s, e) => RefreshUi();
            _ui.Start();
            RefreshUi();
        }

        private CheckBox Check(string text) => new CheckBox
        {
            Text = text,
            AutoSize = true,
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, Theme.S(2), 0, Theme.S(2)),
        };

        private NumericUpDown Numeric(int min, int max, int value) => new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = Theme.S(52),
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0),
        };

        private static FlowLayoutPanel Inline(params Control[] controls)
        {
            var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
            foreach (var c in controls)
            {
                if (c is Label l) l.Margin = new Padding(0, Theme.S(4), 0, 0);
                p.Controls.Add(c);
            }
            return p;
        }

        private void LoadFooter()
        {
            _syncing = true;
            _autostart.Checked = Autostart.IsEnabled();
            _closeToTray.Checked = _ctl.Read(s => s.CloseToTray);
            _handBack.Checked = _ctl.Read(s => s.ReturnToHardwareModeOnExit);
            _alarmTemp.Value = (decimal)Math.Max(30, Math.Min(70, _ctl.Read(s => s.AlarmCoolantTemp)));
            _pumpMin.Value = _ctl.Read(s => s.PumpMinimumPercent);
            _syncing = false;
        }

        private void LayoutHeader(Panel header)
        {
            int right = header.ClientSize.Width - Theme.S(16);
            _fullBlast.Location = new Point(right - _fullBlast.Width, Theme.S(26));
            int x = _fullBlast.Left - Theme.S(24);
            _coolant.Location = new Point(x - _coolant.Width, Theme.S(8));
            _coolantSrc.Location = new Point(x - _coolantSrc.Width, Theme.S(48));
            _pump.Location = new Point(x - _pump.Width - _coolantSrc.Width - Theme.S(12), Theme.S(48));
        }

        public void SetFullBlast(bool on)
        {
            _syncing = true;
            _fullBlast.Checked = on;
            _syncing = false;
        }

        private void RefreshUi()
        {
            var snap = _ctl.Latest;

            _status.Text = snap.Connected ? $"{snap.Status} · hub firmware {snap.Firmware}" : snap.Status;
            _status.ForeColor = snap.Connected ? Theme.Dim : Theme.Warn;
            _coolant.Text = snap.CoolantTemp.HasValue ? $"{snap.CoolantTemp.Value:0.0} °C" : "--.- °C";
            _coolantSrc.Text = snap.CoolantTemp.HasValue ? "coolant · " + snap.CoolantSource : "no coolant sensor";
            _pump.Text = snap.PumpRpm.HasValue ? $"pump {snap.PumpRpm.Value:N0} rpm" : "";
            _lockInfo.Text = snap.LockInfo;
            if (_fullBlast.Checked != snap.FullBlast) SetFullBlast(snap.FullBlast);
            LayoutHeader((Panel)_coolant.Parent);

            _alarm.Visible = snap.Alarm != null;
            _alarm.Text = snap.Alarm != null ? $"⚠  {snap.Alarm}  —  all fans + pump at 100%" : "";

            if (!snap.Connected)
            {
                _empty.Text = snap.Status + "\n\nTips: fully quit iCUE (and stop the \"Corsair Service\"),\n"
                            + "turn OFF SignalRGB's own fan control, and don't run FanControl's Corsair plugin at the same time.";
                _empty.Visible = true;
                _list.Visible = false;
                return;
            }

            _empty.Visible = false;
            _list.Visible = true;

            var key = string.Join("|", snap.Channels.Select(c => c.Channel + ":" + c.DeviceId));
            var sensors = SensorChoices(snap);
            if (key != _layoutKey)
            {
                RebuildRows(snap, sensors);
                _layoutKey = key;
            }

            foreach (var ch in snap.Channels)
            {
                if (_rows.TryGetValue(ch.DeviceId, out var row))
                {
                    row.UpdateLive(ch);
                    row.SetSensorChoices(sensors);
                }
            }
        }

        private static List<SensorChoice> SensorChoices(HubSnapshot snap)
        {
            var list = new List<SensorChoice> { new SensorChoice { Id = "", Label = "Auto (loop coolant)" } };
            foreach (var c in snap.Channels.Where(c => c.Temp.HasValue))
            {
                list.Add(new SensorChoice { Id = c.DeviceId, Label = $"{c.DisplayName} · port {c.Channel} ({c.Model.TempLabel})" });
            }
            return list;
        }

        private void RebuildRows(HubSnapshot snap, List<SensorChoice> sensors)
        {
            _list.SuspendLayout();
            foreach (var r in _rows.Values) r.Dispose();
            _rows.Clear();
            _list.Controls.Clear();

            // pumps first, then fans, then everything else — same order as a loop diagram
            var ordered = snap.Channels
                .OrderBy(c => c.Model.Kind == LinkKind.Pump ? 0 : c.Model.Kind == LinkKind.Fan ? 1 : 2)
                .ThenBy(c => c.Channel);

            foreach (var ch in ordered)
            {
                var row = new DeviceRow(_ctl, () => _ctl.Latest, ch, sensors);
                _rows[ch.DeviceId] = row;
                _list.Controls.Add(row);
            }
            SizeRows();
            _list.ResumeLayout();
        }

        private void SizeRows()
        {
            // Always leave room for the vertical scrollbar so rows never trigger a horizontal one.
            int w = _list.Width - Theme.S(24) - SystemInformation.VerticalScrollBarWidth - Theme.S(2);
            foreach (Control c in _list.Controls) c.Width = Math.Max(Theme.S(560), w);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !_exiting() && _ctl.Read(s => s.CloseToTray))
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (e.CloseReason == CloseReason.UserClosing && !_exiting())
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ui.Stop();
                _ui.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
