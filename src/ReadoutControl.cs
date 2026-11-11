// IS4420WIN Current Loop Receiver - numeric readout: model and version small, mA and % big,
// the IS4420 temperature below them at half size.
// Digits are drawn in fixed-width cells with the decimal points lined up, so the numbers
// do not shift sideways when the value changes.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace Inacks.IS4420Monitor
{
    internal enum DisplayMode { NoData, Live, Stale }

    internal sealed class ReadoutControl : Control
    {
        const int IntegerCells = 3;        // "-25", "127", " 12"
        const int TemperatureCells = 4;    // "-125"
        const float TemperatureScale = 0.5f;  // temperature digits against the mA and % digits
        const float DefaultDigitHeight = 0.72f; // digit height in em, if it cannot be measured

        // The metrics of one digit size.
        sealed class Face : IDisposable
        {
            public Font Digits, Unit;
            public int IntegerCells;
            public float Em, DigitW, PointW, UnitGap, Ascent, UnitAscent, DigitHeightPx, StemPx;
            public float Room;   // width of the widest number: EOVER, ERROR and ERR are fitted into it

            public void Dispose()
            {
                if (Digits != null) Digits.Dispose();
                if (Unit != null) Unit.Dispose();
                Digits = Unit = null;
            }
        }

        Is4420Reading reading;
        DisplayMode mode = DisplayMode.NoData;
        string status = "";
        Color statusColor = Theme.TextMuted;

        // Layout cache, rebuilt when the size or the DPI changes.
        Size cachedSize = Size.Empty;
        float cachedDpi;
        Font captionFont, headerFont, statusFont;
        Face big, small;
        RectangleF inner;
        float scale, captionH;
        float dividerY, statusTop, baseline1, baseline2, baseline3, caption1Top, caption2Top, caption3Top;

        public ReadoutControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
        }

        public void SetContent(Is4420Reading r, DisplayMode m, string s, Color c)
        {
            if (ReferenceEquals(r, reading) && m == mode && s == status && c == statusColor) return;
            reading = r;
            mode = m;
            status = s ?? "";
            statusColor = c;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }

        void DisposeFonts()
        {
            Font[] fonts = { captionFont, headerFont, statusFont };
            foreach (Font f in fonts) if (f != null) f.Dispose();
            captionFont = headerFont = statusFont = null;
            if (big != null) big.Dispose();
            if (small != null) small.Dispose();
            big = small = null;
        }

        static float Measure(Graphics g, string text, Font f)
        {
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                return g.MeasureString(text, f, PointF.Empty, sf).Width;
            }
        }

        static float MaxDigitWidth(Graphics g, Font f)
        {
            float w = 0f;
            for (char c = '0'; c <= '9'; c++) w = Math.Max(w, Measure(g, c.ToString(), f));
            return w;
        }

        static Face MakeFace(Graphics g, float em, int integerCells, int fractionCells, float digitHeight, float stem)
        {
            Face f = new Face();
            f.Em = em;
            f.IntegerCells = integerCells;
            f.Digits = new Font(Theme.DigitFont, em, FontStyle.Regular, GraphicsUnit.Pixel);
            f.Unit = new Font(Theme.DigitFont, em * 0.42f, FontStyle.Regular, GraphicsUnit.Pixel);
            f.DigitW = MaxDigitWidth(g, f.Digits);
            f.PointW = Measure(g, ".", f.Digits);
            f.UnitGap = 0.15f * f.DigitW;
            f.Ascent = Theme.Ascent(f.Digits);
            f.UnitAscent = Theme.Ascent(f.Unit);
            f.DigitHeightPx = digitHeight * em;
            f.StemPx = Math.Max(1.5f, stem * em);
            f.Room = (integerCells + fractionCells) * f.DigitW + (fractionCells > 0 ? f.PointW : 0f);
            return f;
        }

        void EnsureLayout(Graphics g)
        {
            if (big != null && ClientSize == cachedSize && g.DpiX == cachedDpi) return;
            DisposeFonts();
            cachedSize = ClientSize;
            cachedDpi = g.DpiX;
            scale = g.DpiX / 96f;

            float padX = 18f * scale, padY = 14f * scale;
            inner = new RectangleF(padX, padY, Math.Max(20f, Width - 2f * padX), Math.Max(20f, Height - 2f * padY));

            captionFont = new Font(Theme.UiFont, 7.5f, FontStyle.Bold);
            headerFont = new Font(Theme.UiFont, 11f, FontStyle.Bold);
            statusFont = new Font(Theme.UiFont, 9.5f, FontStyle.Regular);
            captionH = captionFont.GetHeight(g);
            float headerH = headerFont.GetHeight(g);
            float statusH = statusFont.GetHeight(g);

            dividerY = inner.Top + captionH + headerH + 8f * scale;
            statusTop = inner.Bottom - statusH;
            float areaTop = dividerY + 6f * scale;
            float areaBottom = statusTop - 6f * scale;
            float areaH = Math.Max(10f, areaBottom - areaTop);
            float gap = 6f * scale;         // caption to digit tops
            float minSpace = 4f * scale;    // at least this between two rows

            float widthAt100, digitTop, digitHeight, stem;
            using (Font probe = new Font(Theme.DigitFont, 100f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font probeUnit = new Font(Theme.DigitFont, 42f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                float dw = MaxDigitWidth(g, probe);
                widthAt100 = (IntegerCells + 2) * dw + Measure(g, ".", probe) + 0.15f * dw + Measure(g, "mA", probeUnit);
                // Where the digits start inside the text cell and how tall they are, from the
                // glyph outlines, so the captions never touch the digits whatever the font.
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddString("0123456789", probe.FontFamily, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
                    RectangleF b = p.GetBounds();
                    digitTop = b.Top / 100f;
                    digitHeight = b.Height / 100f;
                }
                if (digitHeight < 0.4f || digitHeight > 1.1f)
                {
                    digitTop = 0.25f;
                    digitHeight = DefaultDigitHeight;
                }
                // Stroke width of the font, from the stem of a lower-case l: used to draw
                // the minus sign and the dashes with the same weight as the digits.
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddString("l", probe.FontFamily, (int)FontStyle.Regular, 100f, PointF.Empty, StringFormat.GenericTypographic);
                    stem = Math.Max(0.05f, Math.Min(0.14f, p.GetBounds().Width / 100f));
                }
            }

            // Three rows: mA and % at full size, the temperature at TemperatureScale. Each row
            // is a caption and the digits 'gap' below it. The temperature row is narrower than
            // the others, so only the mA and % rows limit the size by width.
            float emByWidth = inner.Width * 100f / widthAt100;
            float emByHeight = (areaH - 3f * (captionH + gap + minSpace)) / (digitHeight * (2f + TemperatureScale));
            float em = Math.Max(8f, Math.Min(emByWidth, emByHeight));
            float emT = Math.Max(6f, em * TemperatureScale);

            big = MakeFace(g, em, IntegerCells, 2, digitHeight, stem);
            small = MakeFace(g, emT, TemperatureCells, 0, digitHeight, stem);

            // The rows are spread evenly: half a space above the first and below the last.
            float h1 = captionH + gap + digitHeight * em;
            float h3 = captionH + gap + digitHeight * emT;
            float space = Math.Max(0f, (areaH - 2f * h1 - h3) / 3f);
            caption1Top = areaTop + space / 2f;
            caption2Top = caption1Top + h1 + space;
            caption3Top = caption2Top + h1 + space;
            baseline1 = caption1Top + captionH + gap - digitTop * em + big.Ascent;
            baseline2 = caption2Top + captionH + gap - digitTop * em + big.Ascent;
            baseline3 = caption3Top + captionH + gap - digitTop * emT + small.Ascent;
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
            EnsureLayout(g);
            Theme.PaintCard(g, ClientSize, scale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            bool have = reading != null;
            using (SolidBrush caption = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush header = new SolidBrush(have ? Theme.Text : Theme.TextMuted))
            {
                float x2 = inner.Left + inner.Width * 0.5f;
                g.DrawString("MODEL", captionFont, caption, inner.Left, inner.Top);
                g.DrawString("VERSION", captionFont, caption, x2, inner.Top);
                g.DrawString(have ? reading.Model : "\u2014", headerFont, header, inner.Left, inner.Top + captionH);
                g.DrawString(have ? reading.Version : "\u2014", headerFont, header, x2, inner.Top + captionH);
                using (Pen divider = new Pen(Theme.Divider, 1f))
                {
                    g.DrawLine(divider, inner.Left, dividerY, inner.Right, dividerY);
                }
                g.DrawString("LOOP CURRENT", captionFont, caption, inner.Left, caption1Top);
                g.DrawString("PERCENT OF 4\u201320 mA SPAN", captionFont, caption, inner.Left, caption2Top);
                g.DrawString("IS4420 TEMPERATURE", captionFont, caption, inner.Left, caption3Top);
            }

            bool live = have && mode != DisplayMode.NoData;
            bool stale = mode == DisplayMode.Stale;

            string maText, pctText;
            Color color;
            if (!live)
            {
                maText = "--.--";
                pctText = "--.-";
                color = Theme.TextMuted;
            }
            else if (reading.Kind == ReadingKind.OverRange)
            {
                maText = pctText = "EOVER";
                color = stale ? Theme.TextMuted : Theme.OverRange;
            }
            else if (reading.Kind == ReadingKind.NoMeasurement)
            {
                maText = pctText = "ERROR";
                color = stale ? Theme.TextMuted : Theme.Warning;
            }
            else
            {
                maText = reading.MilliAmps.ToString("0.00", CultureInfo.InvariantCulture);
                pctText = reading.Percent.ToString("0.0", CultureInfo.InvariantCulture);
                color = stale ? Theme.TextMuted : Theme.Text;
            }

            // The temperature has its own state: it is still sent during over-range, and
            // lines from earlier firmware do not carry it.
            string tText;
            Color tColor;
            if (!live || reading.TemperatureKind == TemperatureKind.None)
            {
                tText = "--";
                tColor = Theme.TextMuted;
            }
            else if (reading.TemperatureKind == TemperatureKind.NoMeasurement)
            {
                tText = "ERR";
                tColor = stale ? Theme.TextMuted : Theme.Warning;
            }
            else
            {
                tText = reading.Temperature.ToString("0", CultureInfo.InvariantCulture);
                tColor = stale ? Theme.TextMuted : Theme.Text;
            }

            using (SolidBrush number = new SolidBrush(color))
            {
                DrawValue(g, big, maText, "mA", baseline1, number);
                DrawValue(g, big, pctText, "%", baseline2, number);
            }
            using (SolidBrush number = new SolidBrush(tColor))
            {
                DrawValue(g, small, tText, "\u00B0C", baseline3, number);
            }

            if (status.Length > 0)
            {
                float h = statusFont.GetHeight(g);
                float d = 7f * scale;
                using (SolidBrush dot = new SolidBrush(statusColor))
                {
                    g.FillEllipse(dot, inner.Left, statusTop + (h - d) / 2f, d, d);
                }
                using (SolidBrush text = new SolidBrush(statusColor == Theme.Ok ? Theme.TextSecondary : statusColor))
                {
                    RectangleF r = new RectangleF(inner.Left + d + 6f * scale, statusTop, inner.Width - d - 6f * scale, h + 2f);
                    using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
                    {
                        sf.Trimming = StringTrimming.EllipsisCharacter;
                        g.DrawString(status, statusFont, text, r, sf);
                    }
                }
            }
        }

        void DrawValue(Graphics g, Face f, string text, string unit, float baseline, Brush brush)
        {
            float x0 = inner.Left;
            float y = baseline - f.Ascent;
            float end;
            bool numeric = text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-');
            if (numeric)
            {
                // A '-' next to digits is the sign (drawn against the first digit); without
                // digits the dashes mean "no value" (drawn centred, like a meter display).
                bool hasDigits = text.IndexOfAny("0123456789".ToCharArray()) >= 0;
                int dot = text.IndexOf('.');
                string integerPart = dot >= 0 ? text.Substring(0, dot) : text;
                string fractionPart = dot >= 0 ? text.Substring(dot + 1) : "";
                float x = x0 + Math.Max(0, f.IntegerCells - integerPart.Length) * f.DigitW;
                foreach (char c in integerPart)
                {
                    if (c == '-') DrawMinus(g, f, x, baseline, hasDigits, brush);
                    else DrawCell(g, f, c, x, y, f.DigitW, brush);
                    x += f.DigitW;
                }
                if (dot >= 0)
                {
                    DrawCell(g, f, '.', x, y, f.PointW, brush);
                    x += f.PointW;
                }
                foreach (char c in fractionPart)
                {
                    if (c == '-') DrawMinus(g, f, x, baseline, false, brush);
                    else DrawCell(g, f, c, x, y, f.DigitW, brush);
                    x += f.DigitW;
                }
                end = x;
            }
            else
            {
                // EOVER, ERROR or ERR: as wide as the number field at most.
                float w = Measure(g, text, f.Digits);
                if (w > f.Room)
                {
                    using (Font shrunk = new Font(f.Digits.FontFamily, f.Digits.Size * f.Room / w, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        g.DrawString(text, shrunk, brush, x0, baseline - Theme.Ascent(shrunk), StringFormat.GenericTypographic);
                    }
                    w = f.Room;
                }
                else
                {
                    g.DrawString(text, f.Digits, brush, x0, y, StringFormat.GenericTypographic);
                }
                end = x0 + w;
            }
            g.DrawString(unit, f.Unit, brush, end + f.UnitGap, baseline - f.UnitAscent, StringFormat.GenericTypographic);
        }

        static void DrawMinus(Graphics g, Face f, float cellX, float baseline, bool againstNext, Brush brush)
        {
            float w = 0.55f * f.DigitW;
            float x = againstNext ? cellX + f.DigitW - w - 0.10f * f.DigitW : cellX + (f.DigitW - w) / 2f;
            float yc = baseline - f.DigitHeightPx / 2f;
            g.FillRectangle(brush, x, yc - f.StemPx / 2f, w, f.StemPx);
        }

        static void DrawCell(Graphics g, Face f, char c, float x, float y, float cellWidth, Brush brush)
        {
            string s = c.ToString();
            float w = Measure(g, s, f.Digits);
            g.DrawString(s, f.Digits, brush, x + (cellWidth - w) / 2f, y, StringFormat.GenericTypographic);
        }
    }
}
