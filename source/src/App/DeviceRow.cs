using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LinkPilot.Core;
using LinkPilot.Link;

namespace LinkPilot.App
{
    internal sealed class SensorChoice
    {
        public string Id = "";
        public string Label = "";
        public override string ToString() => Label;
    }

    /// <summary>One card per LINK device: live RPM/temp, plus mode + speed controls for fans and pumps.</summary>
    internal sealed class DeviceRow : Panel
    {
        private readonly Controller _ctl;
        private readonly Func<HubSnapshot> _snapshot;
        public string DeviceId { get; }
        private readonly LinkKind _kind;
        private readonly int _channel;

        private readonly Label _badge, _name, _sub, _rpm, _temp, _applied, _note;
        private readonly ComboBox? _mode, _sensor;
        private readonly TrackBar? _slider;
        private readonly Label? _sliderValue;
        private readonly Button? _editCurve;
        private readonly Panel? _fixedPanel, _curvePanel;
        private bool _syncing;

        public DeviceRow(Controller ctl, Func<HubSnapshot> snapshot, ChannelState state, IList<SensorChoice> sensors)
        {
            _ctl = ctl;
            _snapshot = snapshot;
            DeviceId = state.DeviceId;
            _kind = state.Model.Kind;
            _channel = state.Channel;

            BackColor = Theme.Card;
            Height = Theme.S(state.Controllable ? 92 : 64);
            Margin = new Padding(Theme.S(12), Theme.S(6), Theme.S(12), Theme.S(6));
            Padding = new Padding(Theme.S(14), Theme.S(10), Theme.S(14), Theme.S(10));

            _badge = Theme.Label(Theme.KindLabel(_kind), 7.5f, Theme.Bg, FontStyle.Bold);
            _badge.AutoSize = false;
            _badge.TextAlign = ContentAlignment.MiddleCenter;
            _badge.BackColor = Theme.KindColor(_kind);
            _badge.Size = new Size(Theme.S(52), Theme.S(18));
            _badge.Location = new Point(Theme.S(14), Theme.S(12));

            _name = Theme.Label(state.DisplayName, 11f, Theme.Text, FontStyle.Bold);
            _name.Location = new Point(Theme.S(74), Theme.S(8));
            _name.Cursor = Cursors.Hand;
            _name.Click += (s, e) => Rename();
            new ToolTip().SetToolTip(_name, "Click to rename");

            _sub = Theme.Label($"Port {state.Channel} · {state.Model.Name}", 8f, Theme.Dim);
            _sub.Location = new Point(Theme.S(74), Theme.S(32));

            _rpm = Theme.Label("— rpm", 13f, Theme.Text, FontStyle.Bold);
            _temp = Theme.Label("", 9f, Theme.Dim);
            _applied = Theme.Label("", 13f, Theme.KindColor(_kind), FontStyle.Bold);
            _note = Theme.Label("", 8f, Theme.Dim);

            Controls.AddRange(new Control[] { _badge, _name, _sub, _rpm, _temp, _applied, _note });

            if (state.Controllable)
            {
                _mode = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Input,
                    ForeColor = Theme.Text,
                    Font = Theme.Ui(9f),
                    Width = Theme.S(96),
                };
                _mode.Items.AddRange(new object[] { "Fixed %", "Curve" });
                _mode.SelectedIndexChanged += (s, e) => OnModeChanged();

                _fixedPanel = new Panel { BackColor = Color.Transparent, Height = Theme.S(30) };
                _slider = new TrackBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    TickFrequency = 10,
                    SmallChange = 1,
                    LargeChange = 5,
                    TickStyle = TickStyle.None,
                    AutoSize = false,
                    Height = Theme.S(28),
                    BackColor = Theme.Card,
                };
                _sliderValue = Theme.Label("50%", 9f, Theme.Text, FontStyle.Bold);
                _slider.ValueChanged += (s, e) => OnSliderChanged();
                _fixedPanel.Controls.Add(_slider);
                _fixedPanel.Controls.Add(_sliderValue);

                _curvePanel = new Panel { BackColor = Color.Transparent, Height = Theme.S(30) };
                _sensor = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Input,
                    ForeColor = Theme.Text,
                    Font = Theme.Ui(9f),
                    Width = Theme.S(200),
                };
                SetSensorChoices(sensors);
                _sensor.SelectedIndexChanged += (s, e) => OnSensorChanged();
                _editCurve = Theme.Button("Edit curve…");
                _editCurve.Click += (s, e) => EditCurve();
                _curvePanel.Controls.Add(_sensor);
                _curvePanel.Controls.Add(_editCurve);

                Controls.Add(_mode);
                Controls.Add(_fixedPanel);
                Controls.Add(_curvePanel);

                LoadFromSettings();
            }
            else
            {
                _note.Text = state.Model.Kind == LinkKind.Sensor ? "Temperature sensor (read-only)" : "No speed control on this device";
            }

            Resize += (s, e) => DoLayout();
            DoLayout();
            UpdateLive(state);
        }

        public void SetSensorChoices(IList<SensorChoice> sensors)
        {
            if (_sensor == null) return;
            var current = (_sensor.SelectedItem as SensorChoice)?.Id ?? _ctl.Read(s => s.Find(DeviceId)?.SensorDeviceId ?? "");
            bool same = _sensor.Items.Count == sensors.Count && _sensor.Items.Cast<SensorChoice>().Select(x => x.Id + x.Label).SequenceEqual(sensors.Select(x => x.Id + x.Label));
            if (same) return;

            _syncing = true;
            _sensor.Items.Clear();
            foreach (var c in sensors) _sensor.Items.Add(c);
            var pick = sensors.FirstOrDefault(c => c.Id == current) ?? sensors.FirstOrDefault();
            if (pick == null && !string.IsNullOrEmpty(current))
            {
                // remembered sensor isn't connected right now; keep showing it so we don't silently swap
                pick = new SensorChoice { Id = current, Label = "(disconnected sensor)" };
                _sensor.Items.Add(pick);
            }
            _sensor.SelectedItem = pick;
            _syncing = false;
        }

        private void LoadFromSettings()
        {
            var st = _ctl.Read(s =>
            {
                var c = s.Find(DeviceId);
                return c == null ? null : new { c.Mode, c.FixedPercent, c.SensorDeviceId };
            });
            if (st == null) return;

            _syncing = true;
            _mode!.SelectedIndex = st.Mode == ControlMode.Fixed ? 0 : 1;
            int min = _kind == LinkKind.Pump ? _ctl.Read(s => s.PumpMinimumPercent) : 0;
            _slider!.Minimum = min;
            _slider.Value = Math.Max(min, Math.Min(100, st.FixedPercent));
            _sliderValue!.Text = _slider.Value + "%";
            var choice = _sensor!.Items.Cast<SensorChoice>().FirstOrDefault(c => c.Id == st.SensorDeviceId);
            if (choice != null) _sensor.SelectedItem = choice;
            _syncing = false;
            ShowModePanels();
        }

        public void RefreshPumpMinimum()
        {
            if (_slider == null || _kind != LinkKind.Pump) return;
            int min = _ctl.Read(s => s.PumpMinimumPercent);
            _syncing = true;
            if (_slider.Value < min) _slider.Value = min;
            _slider.Minimum = min;
            _sliderValue!.Text = _slider.Value + "%";
            _syncing = false;
        }

        private void ShowModePanels()
        {
            if (_mode == null) return;
            bool fixedMode = _mode.SelectedIndex == 0;
            _fixedPanel!.Visible = fixedMode;
            _curvePanel!.Visible = !fixedMode;
        }

        private void OnModeChanged()
        {
            ShowModePanels();
            if (_syncing) return;
            var mode = _mode!.SelectedIndex == 0 ? ControlMode.Fixed : ControlMode.Curve;
            _ctl.Edit(s => { var c = s.Find(DeviceId); if (c != null) c.Mode = mode; });
        }

        private void OnSliderChanged()
        {
            _sliderValue!.Text = _slider!.Value + "%";
            if (_syncing) return;
            int v = _slider.Value;
            _ctl.Edit(s => { var c = s.Find(DeviceId); if (c != null) c.FixedPercent = v; });
        }

        private void OnSensorChanged()
        {
            if (_syncing) return;
            var id = (_sensor!.SelectedItem as SensorChoice)?.Id ?? "";
            _ctl.Edit(s => { var c = s.Find(DeviceId); if (c != null) c.SensorDeviceId = id; });
        }

        private void EditCurve()
        {
            var curve = _ctl.Read(s => Curves.Clone(s.Find(DeviceId)?.Curve ?? Curves.BalancedFan()));
            var sensorId = (_sensor?.SelectedItem as SensorChoice)?.Id ?? "";
            int pumpMin = _ctl.Read(s => s.PumpMinimumPercent);

            Func<double?> live = () =>
            {
                var snap = _snapshot();
                if (string.IsNullOrEmpty(sensorId)) return snap.CoolantTemp;
                return snap.Channels.FirstOrDefault(c => c.DeviceId == sensorId)?.Temp;
            };

            using (var dlg = new CurveEditorForm(_name.Text, curve, _kind == LinkKind.Pump, pumpMin, live))
            {
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    var result = Curves.Clone(dlg.Result);
                    _ctl.Edit(s => { var c = s.Find(DeviceId); if (c != null) c.Curve = result; });
                }
            }
        }

        private void Rename()
        {
            var current = _ctl.Read(s => s.Find(DeviceId)?.Nickname ?? "");
            var name = InputBox.Show(FindForm(), "Rename device", "Give this one a nickname (blank = default name):", current);
            if (name == null) return;
            name = name.Trim();
            _ctl.Edit(s =>
            {
                var c = s.Find(DeviceId);
                if (c == null)
                {
                    c = new ChannelSettings { DeviceId = DeviceId, Curve = Curves.BalancedFan() };
                    s.Channels.Add(c);
                }
                c.Nickname = name;
            });
            _name.Text = name.Length > 0 ? name : _sub.Text.Split('·').Last().Trim();
            DoLayout();
        }

        public void UpdateLive(ChannelState st)
        {
            if (_name.Text != st.DisplayName && !_name.Capture) { _name.Text = st.DisplayName; }
            _rpm.Text = st.Rpm.HasValue ? $"{st.Rpm.Value:N0} rpm" : (st.Controllable ? "— rpm" : "");
            _temp.Text = st.Temp.HasValue ? $"{st.Temp.Value:0.0} °C {st.Model.TempLabel}" : "";
            if (st.Controllable)
            {
                _applied.Text = st.AppliedPercent + "%";
                _note.Text = st.DriverNote;
                _note.ForeColor = st.DriverNote.Contains("override") || st.DriverNote.Contains("missing") || st.DriverNote.Contains("blast") ? Theme.Warn : Theme.Dim;
            }
            else if (st.Temp.HasValue)
            {
                _rpm.Text = $"{st.Temp.Value:0.0} °C";
                _temp.Text = st.Model.TempLabel;
            }
            DoLayout();
        }

        private void DoLayout()
        {
            int w = ClientSize.Width;
            int right = w - Theme.S(14);

            // right column: applied % + note
            _applied.Location = new Point(right - _applied.Width, Theme.S(6));
            _note.Location = new Point(right - _note.Width, Theme.S(34));

            // middle column: rpm + temp
            int midX = Math.Max(Theme.S(300), w / 2 - Theme.S(30));
            _rpm.Location = new Point(midX, Theme.S(6));
            _temp.Location = new Point(midX, Theme.S(34));

            if (_mode != null)
            {
                int y = Theme.S(58);
                _mode.Location = new Point(Theme.S(74), y);
                int x = _mode.Right + Theme.S(12);
                int avail = right - x;

                _fixedPanel!.Location = new Point(x, y - Theme.S(2));
                _fixedPanel.Width = Math.Max(Theme.S(120), avail);
                _sliderValue!.Location = new Point(_fixedPanel.Width - Theme.S(44), Theme.S(5));
                _slider!.Location = new Point(0, 0);
                _slider.Width = Math.Max(Theme.S(60), _fixedPanel.Width - Theme.S(52));

                _curvePanel!.Location = new Point(x, y - Theme.S(1));
                _curvePanel.Width = Math.Max(Theme.S(120), avail);
                _sensor!.Location = new Point(0, Theme.S(1));
                _sensor.Width = Math.Min(Theme.S(260), Math.Max(Theme.S(120), avail - Theme.S(120)));
                _editCurve!.Location = new Point(_sensor.Right + Theme.S(10), 0);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.KindColor(_kind), Theme.S(3)))
                e.Graphics.DrawLine(pen, 1, 0, 1, Height);
        }
    }

    internal static class InputBox
    {
        public static string? Show(IWin32Window? owner, string title, string prompt, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.Text;
                f.Font = Theme.Ui(9f);
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(Theme.S(380), Theme.S(120));

                var lbl = Theme.Label(prompt);
                lbl.Location = new Point(Theme.S(12), Theme.S(12));
                var box = new TextBox
                {
                    Text = value,
                    Location = new Point(Theme.S(12), Theme.S(38)),
                    Width = Theme.S(356),
                    BackColor = Theme.Input,
                    ForeColor = Theme.Text,
                    BorderStyle = BorderStyle.FixedSingle,
                    MaxLength = 40,
                };
                var ok = Theme.Button("OK", primary: true);
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(Theme.S(210), Theme.S(78));
                var cancel = Theme.Button("Cancel");
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(Theme.S(290), Theme.S(78));
                f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.HandleCreated += (s, e) => Theme.DarkTitleBar(f);
                return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
