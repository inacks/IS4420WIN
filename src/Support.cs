// IS4420WIN Current Loop Receiver - small helpers: application info, settings file, card panel.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Inacks.IS4420Monitor
{
    internal static class AppInfo
    {
        public const string Title = "IS4420WIN Current Loop Receiver";
        public const string Version = "v0.05";
    }

    // Last port and window position, in %LOCALAPPDATA%\INACKS\IS4420WIN\settings.txt.
    internal static class AppSettings
    {
        public static string LastPort;
        public static Rectangle WindowBounds = Rectangle.Empty;
        public static bool Maximized;

        static string FilePath
        {
            get
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(Path.Combine(root, "INACKS"), "IS4420WIN"), "settings.txt");
            }
        }

        public static void Load()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "LastPort" && value.Length > 0)
                    {
                        LastPort = value;
                    }
                    else if (key == "Maximized")
                    {
                        Maximized = value == "1";
                    }
                    else if (key == "Window")
                    {
                        string[] p = value.Split(',');
                        int x, y, w, h;
                        if (p.Length == 4 &&
                            int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) &&
                            int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) &&
                            int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
                        {
                            WindowBounds = new Rectangle(x, y, w, h);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>();
                if (!String.IsNullOrEmpty(LastPort)) lines.Add("LastPort=" + LastPort);
                if (WindowBounds.Width > 0 && WindowBounds.Height > 0)
                {
                    lines.Add(String.Format(CultureInfo.InvariantCulture, "Window={0},{1},{2},{3}",
                        WindowBounds.X, WindowBounds.Y, WindowBounds.Width, WindowBounds.Height));
                }
                lines.Add("Maximized=" + (Maximized ? "1" : "0"));
                File.WriteAllLines(path, lines.ToArray());
            }
            catch (Exception)
            {
            }
        }

        public static bool IsOnScreen(Rectangle r)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle visible = Rectangle.Intersect(s.WorkingArea, r);
                if (visible.Width >= 120 && visible.Height >= 80) return true;
            }
            return false;
        }
    }

    // Draws an image scaled to the control height with high quality, right-aligned.
    internal sealed class LogoControl : Control
    {
        readonly Image image;

        public LogoControl(Image image)
        {
            this.image = image;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Window;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (image == null || Width < 2 || Height < 2) return;
            float k = Math.Min((float)Width / image.Width, (float)Height / image.Height);
            float w = image.Width * k, h = image.Height * k;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(image, new RectangleF(Width - w, (Height - h) / 2f, w, h));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null) image.Dispose();
            base.Dispose(disposing);
        }
    }

    // A panel drawn as a white card with rounded corners.
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.PaintCard(e.Graphics, ClientSize, e.Graphics.DpiX / 96f);
        }
    }
}
