// IS4420WIN Current Loop Receiver - colours, fonts and drawing helpers.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Inacks.IS4420Monitor
{
    internal static class Theme
    {
        public static readonly Color Window = Color.FromArgb(0xF1, 0xF2, 0xF4);
        public static readonly Color Card = Color.White;
        public static readonly Color CardBorder = Color.FromArgb(0xD8, 0xDC, 0xE1);
        public static readonly Color Text = Color.FromArgb(0x1B, 0x1F, 0x23);
        public static readonly Color TextSecondary = Color.FromArgb(0x5C, 0x64, 0x6E);
        public static readonly Color TextMuted = Color.FromArgb(0xA4, 0xAA, 0xB1);
        public static readonly Color Divider = Color.FromArgb(0xE6, 0xE8, 0xEB);
        public static readonly Color Scale = Color.FromArgb(0x26, 0x2A, 0x2F);
        public static readonly Color ScaleSecondary = Color.FromArgb(0x83, 0x8A, 0x93);
        public static readonly Color Zone = Color.FromArgb(0xF0, 0xA9, 0x2E);
        public static readonly Color Needle = Color.FromArgb(0xD2, 0x38, 0x2C);
        public static readonly Color NeedleIdle = Color.FromArgb(0xB4, 0xBA, 0xC1);
        public static readonly Color Pivot = Color.FromArgb(0x30, 0x35, 0x3A);
        public static readonly Color OverRange = Color.FromArgb(0xC6, 0x28, 0x28);
        public static readonly Color Warning = Color.FromArgb(0xB0, 0x5E, 0x00);
        public static readonly Color Ok = Color.FromArgb(0x1E, 0x7E, 0x3A);
        public static readonly Color Link = Color.FromArgb(0x1A, 0x5F, 0xB4);

        public static readonly string UiFont = PickFont("Segoe UI", "Tahoma", "Microsoft Sans Serif");
        public static readonly string DigitFont = PickFont("Bahnschrift", "Segoe UI", "Arial");
        public static readonly string MonoFont = PickMonoFont("Consolas", "Lucida Console", "Courier New");

        static string PickMonoFont(params string[] names)
        {
            string name = PickFont(names);
            return name == FontFamily.GenericSansSerif.Name ? FontFamily.GenericMonospace.Name : name;
        }

        static string PickFont(params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    using (FontFamily f = new FontFamily(name))
                    {
                        if (String.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) &&
                            f.IsStyleAvailable(FontStyle.Regular) && f.IsStyleAvailable(FontStyle.Bold))
                        {
                            return f.Name;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return FontFamily.GenericSansSerif.Name;
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180f, 90f);
            path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        // Fills the control with the window colour and draws a white rounded card on it.
        public static void PaintCard(Graphics g, Size size, float scale)
        {
            g.Clear(Window);
            if (size.Width < 4 || size.Height < 4) return;
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(0.5f, 0.5f, size.Width - 1.5f, size.Height - 1.5f);
            using (GraphicsPath path = RoundedRect(r, 8f * scale))
            using (SolidBrush fill = new SolidBrush(Card))
            using (Pen border = new Pen(CardBorder, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            g.SmoothingMode = old;
        }

        // Ascent of a font in the font's own unit (pixels for GraphicsUnit.Pixel fonts).
        public static float Ascent(Font f)
        {
            FontFamily ff = f.FontFamily;
            return f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        // Ascent in pixels for a font of any unit, on the given surface.
        public static float AscentPixels(Graphics g, Font f)
        {
            FontFamily ff = f.FontFamily;
            float lineSpacing = ff.GetLineSpacing(f.Style);
            return f.GetHeight(g) * ff.GetCellAscent(f.Style) / lineSpacing;
        }
    }
}
