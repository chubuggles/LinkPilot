using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LinkPilot.Link;

namespace LinkPilot.App
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(18, 19, 26);
        public static readonly Color Card = Color.FromArgb(30, 32, 43);
        public static readonly Color CardHover = Color.FromArgb(37, 40, 54);
        public static readonly Color Line = Color.FromArgb(52, 56, 74);
        public static readonly Color Text = Color.FromArgb(232, 234, 242);
        public static readonly Color Dim = Color.FromArgb(146, 151, 172);
        public static readonly Color Accent = Color.FromArgb(64, 214, 196);   // coolant teal
        public static readonly Color Pump = Color.FromArgb(110, 160, 255);
        public static readonly Color Warn = Color.FromArgb(255, 176, 64);
        public static readonly Color Danger = Color.FromArgb(240, 80, 96);
        public static readonly Color Input = Color.FromArgb(24, 26, 35);

        private static float _scale = 0;

        public static float ScaleFactor
        {
            get
            {
                if (_scale <= 0)
                {
                    try
                    {
                        using (var g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiX / 96f;
                    }
                    catch { _scale = 1; }
                    if (_scale < 1) _scale = 1;
                }
                return _scale;
            }
        }

        /// <summary>Scales a 96-DPI pixel value for the current screen.</summary>
        public static int S(int px) => (int)Math.Round(px * ScaleFactor);

        public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", size, style);
        public static Font Mono(float size, FontStyle style = FontStyle.Regular) => new Font("Consolas", size, style);

        public static Color KindColor(LinkKind k) =>
            k == LinkKind.Pump ? Pump : k == LinkKind.Fan ? Accent : Dim;

        public static string KindLabel(LinkKind k) =>
            k == LinkKind.Pump ? "PUMP" : k == LinkKind.Fan ? "FAN" : k == LinkKind.Sensor ? "SENSOR" : "LINK";

        public static Button Button(string text, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Card,
                ForeColor = primary ? Bg : Text,
                Font = Ui(9f, primary ? FontStyle.Bold : FontStyle.Regular),
                Cursor = Cursors.Hand,
                AutoSize = true,
                Padding = new Padding(S(8), S(2), S(8), S(2)),
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Line;
            b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent) : CardHover;
            return b;
        }

        public static Label Label(string text, float size = 9f, Color? color = null, FontStyle style = FontStyle.Regular)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = color ?? Text,
                BackColor = Color.Transparent,
                Font = Ui(size, style),
            };
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Dark title bar on Windows 10 20H1+ / Windows 11 (silently ignored elsewhere).</summary>
        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)
                {
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                }
            }
            catch { }
        }
    }

    /// <summary>The little fan-in-a-droplet icon, drawn in code so there are no resource files.</summary>
    internal static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Bitmap DrawBitmap(int size, Color ring, float spin = 0)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float pad = size * 0.06f;
                var outer = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                using (var b = new SolidBrush(Theme.Bg)) g.FillEllipse(b, outer);
                using (var p = new Pen(ring, Math.Max(1.5f, size * 0.09f))) g.DrawEllipse(p, outer);

                float cx = size / 2f, cy = size / 2f;
                float r = size * 0.30f;
                using (var blade = new SolidBrush(ring))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var st = g.Save();
                        g.TranslateTransform(cx, cy);
                        g.RotateTransform(spin + i * 90);
                        using (var path = new GraphicsPath())
                        {
                            path.AddBezier(0, 0, r * 0.15f, -r * 0.55f, r * 0.75f, -r * 0.85f, r, -r * 0.25f);
                            path.AddBezier(r, -r * 0.25f, r * 0.7f, -r * 0.05f, r * 0.35f, 0, 0, 0);
                            g.FillPath(blade, path);
                        }
                        g.Restore(st);
                    }
                }
                float hub = size * 0.11f;
                using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, cx - hub, cy - hub, hub * 2, hub * 2);
            }
            return bmp;
        }

        public static Icon MakeIcon(int size, Color ring)
        {
            using (var bmp = DrawBitmap(size, ring))
            {
                var h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally
                {
                    try { DestroyIcon(h); } catch (EntryPointNotFoundException) { } catch (DllNotFoundException) { }
                }
            }
        }
    }
}
