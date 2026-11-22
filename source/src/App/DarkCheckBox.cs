using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LinkPilot.App
{
    /// <summary>
    /// A check box drawn for the dark theme. The stock flat check box paints its tick in the text
    /// colour on a white box, so with light text a ticked box looks exactly like an empty one.
    /// </summary>
    internal sealed class DarkCheckBox : CheckBox
    {
        private bool _hover;

        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = true;
            Cursor = Cursors.Hand;
        }

        private int Box => Theme.S(16);
        private int Gap => Theme.S(8);

        public override Size GetPreferredSize(Size proposedSize)
        {
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            return new Size(Box + Gap + text.Width + Theme.S(2), Math.Max(Box, text.Height) + Theme.S(4));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var box = new Rectangle(0, (Height - Box) / 2, Box - 1, Box - 1);
            var accent = Enabled ? Theme.Accent : Theme.Dim;
            using (var fill = new SolidBrush(Checked ? accent : Theme.Input)) g.FillRectangle(fill, box);
            using (var border = new Pen(Checked ? accent : (_hover || Focused) && Enabled ? Theme.Accent : Theme.Dim)) g.DrawRectangle(border, box);

            if (Checked)
            {
                float u = Box / 16f;
                using (var pen = new Pen(Theme.Bg, Math.Max(2f, 2.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.X + 3.5f * u, box.Y + 8.2f * u),
                        new PointF(box.X + 6.8f * u, box.Y + 11.3f * u),
                        new PointF(box.X + 12.2f * u, box.Y + 4.6f * u),
                    });
                }
            }

            var textArea = new Rectangle(Box + Gap, 0, Math.Max(0, Width - Box - Gap), Height);
            TextRenderer.DrawText(g, Text, Font, textArea, Enabled ? ForeColor : Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }
}
