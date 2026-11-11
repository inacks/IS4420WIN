// IS4420WIN Current Loop Receiver - needle indicator for the percentage.
// Panel-meter style: outer scale in % of the 4-20 mA span, inner scale in mA.
// The scale runs from -12.5 % to 112.5 % (2 to 22 mA); the parts outside 0-100 % are
// shaded. Beyond the ends the needle rests on a stop, like a real meter.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace Inacks.IS4420Monitor
{
    internal sealed class GaugeControl : Control
    {
        const float ScaleMin = -12.5f;     // 2 mA
        const float ScaleMax = 112.5f;     // 22 mA
        const float StopMargin = 1.6f;     // the needle stops this far beyond the scale ends
        const float HalfSweep = 50f;       // degrees each side of vertical
        const float RestValue = ScaleMin - StopMargin;
        const float TimeConstant = 0.08f;  // needle movement, seconds

        Is4420Reading reading;
        DisplayMode mode = DisplayMode.NoData;
        float shownValue = RestValue;
        float targetValue = RestValue;
        bool active;
        string caption;
        Color captionColor;

        Bitmap face;
        float faceDpi;
        PointF pivot;
        float radius;
        float scale = 1f;

        public GaugeControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
        }

        public void SetContent(Is4420Reading r, DisplayMode m)
        {
            if (ReferenceEquals(r, reading) && m == mode) return;
            reading = r;
            mode = m;
            if (r == null || m == DisplayMode.NoData)
            {
                targetValue = RestValue;
                active = false;
                caption = null;
            }
            else if (m == DisplayMode.Stale)
            {
                // Keep the last position, greyed.
                active = false;
                caption = "NO DATA";
                captionColor = Theme.TextSecondary;
            }
            else if (r.Kind == ReadingKind.OverRange)
            {
                targetValue = ScaleMax + StopMargin;
                active = true;
                caption = "OVER-RANGE";
                captionColor = Theme.OverRange;
            }
            else if (r.Kind == ReadingKind.NoMeasurement)
            {
                targetValue = RestValue;
                active = false;
                caption = "NO MEASUREMENT";
                captionColor = Theme.Warning;
            }
            else
            {
                float v = (float)r.Percent;
                targetValue = Math.Max(RestValue, Math.Min(ScaleMax + StopMargin, v));
                active = true;
                caption = null;
            }
            Invalidate();
        }

        // Moves the needle towards its target; call it from a timer.
        public void Animate(float seconds)
        {
            float diff = targetValue - shownValue;
            if (diff == 0f) return;
            if (Math.Abs(diff) < 0.02f || seconds <= 0f)
            {
                shownValue = targetValue;
            }
            else
            {
                shownValue += diff * (1f - (float)Math.Exp(-seconds / TimeConstant));
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && face != null)
            {
                face.Dispose();
                face = null;
            }
            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (face != null)
            {
                face.Dispose();
                face = null;
            }
        }

        static float AngleOf(float value)
        {
            return -HalfSweep + (value - ScaleMin) / (ScaleMax - ScaleMin) * 2f * HalfSweep;
        }

        PointF At(float r, float angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            return new PointF(pivot.X + r * (float)Math.Sin(a), pivot.Y - r * (float)Math.Cos(a));
        }

        void Arc(Graphics g, Pen pen, float r, float v1, float v2)
        {
            float a1 = AngleOf(v1), a2 = AngleOf(v2);
            g.DrawArc(pen, pivot.X - r, pivot.Y - r, 2f * r, 2f * r, a1 - 90f, a2 - a1);
        }

        void Tick(Graphics g, Pen pen, float value, float r1, float r2)
        {
            float a = AngleOf(value);
            g.DrawLine(pen, At(r1, a), At(r2, a));
        }

        static void CenteredText(Graphics g, string text, Font font, Brush brush, PointF center)
        {
            SizeF size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(text, font, brush, center.X - size.Width / 2f, center.Y - size.Height / 2f, StringFormat.GenericTypographic);
        }

        void BuildFace(float dpi)
        {
            if (face != null) face.Dispose();
            int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
            face = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            faceDpi = dpi;
            scale = dpi / 96f;

            float pad = 14f * scale;
            RectangleF area = new RectangleF(pad, pad, Math.Max(10f, w - 2f * pad), Math.Max(10f, h - 2f * pad));
            // Height used: 1.25 R above the pivot (scale and labels), 0.21 R below (needle tail).
            radius = Math.Max(20f, Math.Min(area.Height / 1.46f, area.Width / 2.05f));
            float top = area.Top + (area.Height - 1.46f * radius) / 2f;
            pivot = new PointF(area.Left + area.Width / 2f, top + 1.25f * radius);
            float R = radius;

            using (Graphics g = Graphics.FromImage(face))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Theme.PaintCard(g, ClientSize, scale);

                float thin = Math.Max(1f, 0.006f * R);
                float thick = Math.Max(1.4f, 0.011f * R);

                // Shaded zones outside the 4-20 mA span.
                using (Pen zone = new Pen(Color.FromArgb(90, Theme.Zone), 0.05f * R))
                {
                    Arc(g, zone, R + 0.025f * R, ScaleMin, 0f);
                    Arc(g, zone, R + 0.025f * R, 100f, ScaleMax);
                }

                // Scale line.
                using (Pen main = new Pen(Theme.Scale, thick))
                using (Pen ends = new Pen(Theme.ScaleSecondary, thin))
                {
                    Arc(g, ends, R, ScaleMin, ScaleMax);
                    Arc(g, main, R, 0f, 100f);
                }

                // Outer scale: % of span, a tick every 2 %, a long tick every 10 %.
                using (Pen major = new Pen(Theme.Scale, thick))
                using (Pen minor = new Pen(Theme.Scale, thin))
                {
                    for (int v = 0; v <= 100; v += 2)
                    {
                        bool big = v % 10 == 0;
                        Tick(g, big ? major : minor, v, R, R + (big ? 0.085f : 0.04f) * R);
                    }
                }

                // Inner scale: mA, a tick every 1 mA, a long tick every 4 mA.
                using (Pen major = new Pen(Theme.ScaleSecondary, thick))
                using (Pen minor = new Pen(Theme.ScaleSecondary, thin))
                {
                    for (int ma = 2; ma <= 22; ma++)
                    {
                        bool big = ma % 4 == 0;
                        Tick(g, big ? major : minor, (ma - 4) * 6.25f, R, R - (big ? 0.085f : 0.04f) * R);
                    }
                }

                // Labels with their unit: 0% ... 100% outside, 4mA ... 20mA inside. Each label
                // is placed by its own size and angle so it never touches the ticks.
                float labelPx = Math.Max(10f * scale, 0.074f * R);
                float innerPx = Math.Max(9f * scale, 0.062f * R);
                using (Font outerFont = new Font(Theme.UiFont, labelPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Font innerFont = new Font(Theme.UiFont, innerPx, FontStyle.Regular, GraphicsUnit.Pixel))
                using (SolidBrush outerBrush = new SolidBrush(Theme.Scale))
                using (SolidBrush innerBrush = new SolidBrush(Theme.ScaleSecondary))
                {
                    // A label every 10 % when there is room, otherwise every 20 %.
                    SizeF wide = g.MeasureString("100%", outerFont, PointF.Empty, StringFormat.GenericTypographic);
                    float spacing10 = (R * 1.2f) * (AngleOf(10f) - AngleOf(0f)) * (float)Math.PI / 180f;
                    int step = spacing10 >= wide.Width * 1.25f ? 10 : 20;
                    for (int v = 0; v <= 100; v += step)
                    {
                        string text = v.ToString(CultureInfo.InvariantCulture) + "%";
                        float a = AngleOf(v);
                        SizeF size = g.MeasureString(text, outerFont, PointF.Empty, StringFormat.GenericTypographic);
                        float r = R + 0.085f * R + 0.03f * R + HalfExtent(size, a);
                        CenteredText(g, text, outerFont, outerBrush, At(r, a));
                    }
                    for (int ma = 4; ma <= 20; ma += 4)
                    {
                        string text = ma.ToString(CultureInfo.InvariantCulture) + "mA";
                        float a = AngleOf((ma - 4) * 6.25f);
                        SizeF size = g.MeasureString(text, innerFont, PointF.Empty, StringFormat.GenericTypographic);
                        float r = R - 0.085f * R - 0.03f * R - HalfExtent(size, a);
                        CenteredText(g, text, innerFont, innerBrush, At(r, a));
                    }
                }

                // Legend in the middle of the dial.
                using (Font probe = new Font(Theme.UiFont, 100f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    float widthAt100 = g.MeasureString("Current Loop", probe, PointF.Empty, StringFormat.GenericTypographic).Width;
                    float px = Math.Max(10f * scale, Math.Min(0.10f * R, 0.62f * R * 100f / widthAt100));
                    using (Font legendFont = new Font(Theme.UiFont, px, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush legendBrush = new SolidBrush(Color.FromArgb(0xB9, 0xBF, 0xC6)))
                    {
                        CenteredText(g, "Current Loop", legendFont, legendBrush, new PointF(pivot.X, pivot.Y - 0.46f * R));
                    }
                }
            }
        }

        // Distance from the centre of a text box to its edge, along the radius at this angle.
        static float HalfExtent(SizeF size, float angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            return (float)(size.Width / 2.0 * Math.Abs(Math.Sin(a)) + size.Height / 2.0 * Math.Abs(Math.Cos(a)));
        }

        bool paintErrorReported;

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                PaintContent(e.Graphics);
            }
            catch (Exception ex)
            {
                e.Graphics.Clear(Theme.Window);
                if (!paintErrorReported)
                {
                    paintErrorReported = true;
                    Program.ReportError(ex, false);
                }
            }
        }

        void PaintContent(Graphics g)
        {
            if (face == null || face.Width != ClientSize.Width || face.Height != ClientSize.Height || faceDpi != g.DpiX)
            {
                BuildFace(g.DpiX);
            }
            g.DrawImageUnscaled(face, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float R = radius;

            // Needle: tapered, with a short counterweight tail and a soft shadow.
            double a = AngleOf(shownValue) * Math.PI / 180.0;
            float dx = (float)Math.Sin(a), dy = -(float)Math.Cos(a);
            float px = -dy, py = dx;   // perpendicular
            float tipR = R + 0.045f * R, tailR = 0.17f * R;
            float wBase = Math.Max(1.3f, 0.012f * R), wTail = wBase * 1.7f;
            PointF[] needle =
            {
                new PointF(pivot.X + dx * tipR, pivot.Y + dy * tipR),
                new PointF(pivot.X + px * wBase, pivot.Y + py * wBase),
                new PointF(pivot.X - dx * tailR + px * wTail, pivot.Y - dy * tailR + py * wTail),
                new PointF(pivot.X - dx * tailR - px * wTail, pivot.Y - dy * tailR - py * wTail),
                new PointF(pivot.X - px * wBase, pivot.Y - py * wBase),
            };
            float off = Math.Max(1f, 0.012f * R);
            PointF[] shadow = new PointF[needle.Length];
            for (int i = 0; i < needle.Length; i++) shadow[i] = new PointF(needle[i].X + off, needle[i].Y + off * 1.2f);
            using (SolidBrush s = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            {
                g.FillPolygon(s, shadow);
            }
            using (SolidBrush n = new SolidBrush(active ? Theme.Needle : Theme.NeedleIdle))
            {
                g.FillPolygon(n, needle);
            }

            float cap = Math.Max(4f, 0.055f * R);
            using (SolidBrush c = new SolidBrush(Theme.Pivot))
            {
                g.FillEllipse(c, pivot.X - cap, pivot.Y - cap, 2f * cap, 2f * cap);
            }
            float dot = cap * 0.35f;
            using (SolidBrush c = new SolidBrush(Color.FromArgb(0x6E, 0x75, 0x7C)))
            {
                g.FillEllipse(c, pivot.X - dot, pivot.Y - dot, 2f * dot, 2f * dot);
            }

            // State caption on a small label above the needle, so the needle never hides it.
            if (caption != null)
            {
                using (Font f = new Font(Theme.UiFont, Math.Max(10f * scale, 0.068f * R), FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF size = g.MeasureString(caption, f, PointF.Empty, StringFormat.GenericTypographic);
                    PointF center = new PointF(pivot.X, pivot.Y - 0.25f * R);
                    float padX = size.Height * 0.55f, padY = size.Height * 0.22f;
                    RectangleF box = new RectangleF(center.X - size.Width / 2f - padX, center.Y - size.Height / 2f - padY,
                        size.Width + 2f * padX, size.Height + 2f * padY);
                    using (GraphicsPath path = Theme.RoundedRect(box, box.Height / 2f))
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(242, 255, 255, 255)))
                    using (Pen border = new Pen(Color.FromArgb(110, captionColor), 1f))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(border, path);
                    }
                    using (SolidBrush b = new SolidBrush(captionColor))
                    {
                        CenteredText(g, caption, f, b, center);
                    }
                }
            }
        }
    }
}
