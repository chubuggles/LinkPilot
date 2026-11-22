using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using LinkPilot.Core;

namespace LinkPilot.App
{
    /// <summary>Drag-the-dots fan curve graph: X = temperature, Y = speed %.</summary>
    internal sealed class CurveGraph : Control
    {
        public const double TMin = 20, TMax = 60;
        private const int DotR = 6;

        public List<CurvePoint> Points { get; private set; } = Curves.BalancedFan();
        public double? LiveTemp { get; set; }
        public int MinPercent { get; set; }
        public event EventHandler? CurveChanged;

        private int _drag = -1;
        private int _hover = -1;

        public CurveGraph()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Input;
            Cursor = Cursors.Cross;
        }

        public void SetPoints(List<CurvePoint> pts)
        {
            Points = Curves.Clone(pts);
            Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
            Invalidate();
            CurveChanged?.Invoke(this, EventArgs.Empty);
        }

        private Rectangle Plot => new Rectangle(Theme.S(44), Theme.S(14), Width - Theme.S(62), Height - Theme.S(52));

        private PointF ToScreen(double t, double pct)
        {
            var r = Plot;
            float x = r.Left + (float)((t - TMin) / (TMax - TMin) * r.Width);
            float y = r.Bottom - (float)(pct / 100.0 * r.Height);
            return new PointF(x, y);
        }

        private void FromScreen(Point p, out double t, out double pct)
        {
            var r = Plot;
            t = TMin + (p.X - r.Left) / (double)r.Width * (TMax - TMin);
            pct = (r.Bottom - p.Y) / (double)r.Height * 100.0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = Plot;

            using (var grid = new Pen(Theme.Line))
            using (var dim = new SolidBrush(Theme.Dim))
            using (var f = Theme.Ui(8f))
            {
                for (int pct = 0; pct <= 100; pct += 20)
                {
                    var y = ToScreen(TMin, pct).Y;
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                    var s = pct + "%";
                    var sz = g.MeasureString(s, f);
                    g.DrawString(s, f, dim, r.Left - sz.Width - Theme.S(4), y - sz.Height / 2);
                }
                for (int t = (int)TMin; t <= TMax; t += 5)
                {
                    var x = ToScreen(t, 0).X;
                    g.DrawLine(grid, x, r.Top, x, r.Bottom);
                    var s = t + "°";
                    var sz = g.MeasureString(s, f);
                    g.DrawString(s, f, dim, x - sz.Width / 2, r.Bottom + Theme.S(4));
                }
                g.DrawString("coolant / sensor temperature (°C)", f, dim, r.Left + r.Width / 2 - Theme.S(90), r.Bottom + Theme.S(24));
            }

            if (MinPercent > 0)
            {
                var y = ToScreen(TMin, MinPercent).Y;
                using (var b = new SolidBrush(Color.FromArgb(40, Theme.Pump)))
                    g.FillRectangle(b, r.Left, y, r.Width, r.Bottom - y);
                using (var f = Theme.Ui(8f))
                using (var b = new SolidBrush(Theme.Pump))
                    g.DrawString($"pump never goes below {MinPercent}%", f, b, r.Left + Theme.S(4), y + Theme.S(2));
            }

            // the line itself, extended flat to the edges
            var pts = new List<PointF> { ToScreen(TMin, Points[0].Percent) };
            foreach (var p in Points) pts.Add(ToScreen(p.Temp, p.Percent));
            pts.Add(ToScreen(TMax, Points[Points.Count - 1].Percent));

            var fill = new List<PointF>(pts) { new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom) };
            using (var b = new SolidBrush(Color.FromArgb(36, Theme.Accent))) g.FillPolygon(b, fill.ToArray());
            using (var pen = new Pen(Theme.Accent, Theme.S(2))) g.DrawLines(pen, pts.ToArray());

            if (LiveTemp.HasValue)
            {
                double t = Math.Max(TMin, Math.Min(TMax, LiveTemp.Value));
                double pct = Curves.Evaluate(Points, LiveTemp.Value);
                var top = ToScreen(t, 100);
                var bottom = ToScreen(t, 0);
                using (var pen = new Pen(Theme.Warn, 1) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, top, bottom);
                var at = ToScreen(t, pct);
                using (var b = new SolidBrush(Theme.Warn)) g.FillEllipse(b, at.X - 4, at.Y - 4, 8, 8);
                using (var f = Theme.Ui(8.5f, FontStyle.Bold))
                using (var b = new SolidBrush(Theme.Warn))
                    g.DrawString($"now {LiveTemp.Value:0.0}° → {pct:0}%", f, b, Math.Min(at.X + 6, r.Right - Theme.S(110)), Math.Max(r.Top, at.Y - Theme.S(20)));
            }

            int dr = Theme.S(DotR);
            for (int i = 0; i < Points.Count; i++)
            {
                var c = ToScreen(Points[i].Temp, Points[i].Percent);
                bool hot = i == _hover || i == _drag;
                using (var b = new SolidBrush(hot ? Theme.Text : Theme.Accent)) g.FillEllipse(b, c.X - dr, c.Y - dr, dr * 2, dr * 2);
                using (var pen = new Pen(Theme.Bg, 2)) g.DrawEllipse(pen, c.X - dr, c.Y - dr, dr * 2, dr * 2);
                if (hot)
                {
                    using (var f = Theme.Ui(8.5f, FontStyle.Bold))
                    using (var b = new SolidBrush(Theme.Text))
                        g.DrawString($"{Points[i].Temp:0.#}°C → {Points[i].Percent}%", f, b, Math.Min(c.X + dr, r.Right - Theme.S(90)), Math.Max(0, c.Y - dr - Theme.S(18)));
                }
            }
        }

        private int HitTest(Point p)
        {
            int best = -1;
            double bestD = Theme.S(DotR + 6);
            for (int i = 0; i < Points.Count; i++)
            {
                var c = ToScreen(Points[i].Temp, Points[i].Percent);
                double d = Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y));
                if (d <= bestD) { bestD = d; best = i; }
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _drag = HitTest(e.Location);
            if (_drag >= 0) Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag >= 0)
            {
                FromScreen(e.Location, out double t, out double pct);
                double lo = _drag > 0 ? Points[_drag - 1].Temp + 1 : TMin;
                double hi = _drag < Points.Count - 1 ? Points[_drag + 1].Temp - 1 : TMax;
                Points[_drag].Temp = Math.Round(Math.Max(lo, Math.Min(hi, t)) * 2) / 2; // 0.5° steps
                Points[_drag].Percent = (int)Math.Round(Math.Max(0, Math.Min(100, pct)));
                Invalidate();
                CurveChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                int h = HitTest(e.Location);
                if (h != _hover) { _hover = h; Invalidate(); }
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Cross;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = -1;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_drag < 0 && _hover >= 0) { _hover = -1; Invalidate(); }
        }
    }

    internal sealed class CurveEditorForm : Form
    {
        private readonly CurveGraph _graph;
        private readonly Timer _live;
        private readonly Func<double?> _liveTemp;

        public List<CurvePoint> Result => _graph.Points;

        public CurveEditorForm(string deviceName, List<CurvePoint> curve, bool isPump, int pumpMin, Func<double?> liveTemp)
        {
            _liveTemp = liveTemp;
            Text = $"Curve · {deviceName}";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(640), Theme.S(420));
            MinimumSize = new Size(Theme.S(480), Theme.S(340));
            Icon = IconFactory.MakeIcon(32, Theme.Accent);

            _graph = new CurveGraph { Dock = DockStyle.Fill, MinPercent = isPump ? pumpMin : 0 };
            _graph.SetPoints(curve);

            var hint = Theme.Label("Drag the dots. Left/right = temperature, up/down = speed.", 8.5f, Theme.Dim);
            hint.Dock = DockStyle.Top;
            hint.Padding = new Padding(Theme.S(12), Theme.S(10), 0, Theme.S(4));

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = Theme.S(48),
                Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(8)),
                BackColor = Theme.Card,
                FlowDirection = FlowDirection.LeftToRight,
            };

            void Preset(string name, Func<List<CurvePoint>> make)
            {
                var b = Theme.Button(name);
                b.Click += (s, e) => _graph.SetPoints(make());
                bar.Controls.Add(b);
            }

            if (isPump)
            {
                Preset("Pump: steady", () => new List<CurvePoint> { new CurvePoint(25, 70), new CurvePoint(30, 70), new CurvePoint(35, 75), new CurvePoint(40, 90), new CurvePoint(45, 100) });
                Preset("Pump: balanced", Curves.BalancedPump);
            }
            else
            {
                Preset("Quiet", Curves.QuietFan);
                Preset("Balanced", Curves.BalancedFan);
                Preset("Performance", Curves.PerformanceFan);
            }

            var ok = Theme.Button("Save curve", primary: true);
            ok.DialogResult = DialogResult.OK;
            var cancel = Theme.Button("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var spacer = new Label { Width = Theme.S(24), AutoSize = false };
            bar.Controls.Add(spacer);
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(_graph);
            Controls.Add(hint);
            Controls.Add(bar);

            _live = new Timer { Interval = 1000 };
            _live.Tick += (s, e) => { _graph.LiveTemp = _liveTemp(); _graph.Invalidate(); };
            _graph.LiveTemp = _liveTemp();
            _live.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _live.Stop();
            _live.Dispose();
            base.OnFormClosed(e);
        }
    }
}
