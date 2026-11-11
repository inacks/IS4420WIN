// IS4420WIN Current Loop Receiver - main window.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Inacks.IS4420Monitor
{
    internal enum ConnState { Disconnected, Connecting, Connected, Lost }

    internal sealed class MainForm : Form
    {
        const int LogLimit = 2000;            // lines kept in the log window
        const double StaleSeconds = 1.0;      // no valid line for this long: values greyed
        const double NoLineHintSeconds = 2.5; // connected without a valid line: show the wiring hint
        const int PeriodLines = 50;           // lines in the average period
        const string InacksUrl = "https://www.inacks.com";
        const string Is4420Url = "https://inacks.com/is4420";
        const string Kappa4420Url = "https://inacks.com/kappa4420usb";

        public static MainForm Instance;

        readonly ConcurrentQueue<LinkEvent> events = new ConcurrentQueue<LinkEvent>();
        readonly SerialLink link;
        readonly string startupPort;

        TableLayoutPanel body;
        RowStyle logRow;
        ComboBox portCombo;
        Button rescanButton, connectButton, logButton;
        Label hintLabel;
        ToolTip toolTip;
        ReadoutControl readout;
        GaugeControl gauge;
        Label logTitle;
        CheckBox pauseCheck;
        Button clearButton;
        ListBox logList;
        ToolStripStatusLabel stateLabel, csvLabel, linesLabel, rejectedLabel, periodLabel;
        System.Windows.Forms.Timer uiTimer, portTimer;

        ConnState state = ConnState.Disconnected;
        int session = -1;
        bool reconnecting, lostLogged;
        string activePort;
        bool scanning, userPickedDuringScan;
        // Connect by itself to the first port where a scan hears the module. On at start;
        // off after the user disconnects, so the port stays free; on again with Rescan.
        bool autoConnect = true;
        List<string> knownPorts = new List<string>();

        Is4420Reading lastReading;
        long lastValidTicks, connectedTicks, lastFrameTicks;
        bool noLineHintShown, countersDirty, oldFormatSeen, noTemperatureNoted;
        long linesOk, linesRejected, linesHidden;
        readonly Queue<long> lineTimes = new Queue<long>();
        double periodMs = double.NaN;

        StreamWriter csv;       // open while the CSV log is on
        string csvPath;
        long csvRows;

        sealed class LogEntry
        {
            public string Text;
            public bool Data;
        }
        readonly List<LogEntry> pendingLog = new List<LogEntry>();

        public MainForm(string startupPort)
        {
            Instance = this;
            this.startupPort = startupPort;
            link = new SerialLink(events);
            AppSettings.Load();
            BuildUi();
            RestoreWindow();
        }

        // ------------------------------------------------------------------ layout

        void BuildUi()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(Theme.UiFont, 9F);
            BackColor = Theme.Window;
            Text = AppInfo.Title + " " + AppInfo.Version;
            ClientSize = new Size(920, 740);
            MinimumSize = new Size(760, 660);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            LoadIcon();

            toolTip = new ToolTip();

            // Header: app name on the left, INACKS logo on the right (opens www.inacks.com).
            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Top;
            header.Height = 56;
            header.BackColor = Color.White;
            header.Padding = new Padding(14, 8, 14, 8);
            header.ColumnCount = 2;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Label appName = new Label();
            appName.Text = AppInfo.Title;
            appName.AutoSize = true;
            appName.Anchor = AnchorStyles.Left;
            appName.Font = new Font(Theme.UiFont, 13F, FontStyle.Bold);
            appName.ForeColor = Theme.Text;
            appName.BackColor = Color.White;
            appName.Margin = Padding.Empty;
            header.Controls.Add(appName, 0, 0);
            header.Controls.Add(BuildLogo(), 1, 0);
            Panel headerLine = new Panel();
            headerLine.Dock = DockStyle.Top;
            headerLine.Height = 1;
            headerLine.BackColor = Theme.CardBorder;

            // Control row: port, Rescan, Connect, Start log, hint.
            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 46;
            top.Padding = new Padding(10, 9, 12, 3);
            top.ColumnCount = 6;
            top.RowCount = 1;
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label portLabel = new Label();
            portLabel.Text = "Port";
            portLabel.AutoSize = true;
            portLabel.Anchor = AnchorStyles.Left;
            portLabel.Margin = new Padding(0, 0, 6, 0);

            portCombo = new ComboBox();
            portCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            portCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            portCombo.Margin = new Padding(0, 0, 8, 0);
            portCombo.SelectionChangeCommitted += delegate { if (scanning) userPickedDuringScan = true; };

            rescanButton = new Button();
            rescanButton.Text = "Rescan";
            rescanButton.Size = new Size(80, 27);
            rescanButton.Anchor = AnchorStyles.Left;
            rescanButton.Margin = new Padding(0, 0, 6, 0);
            rescanButton.UseVisualStyleBackColor = true;
            rescanButton.Click += delegate { StartScan(true); };
            toolTip.SetToolTip(rescanButton, "Listens on every free COM port and connects to the first one where an IS4420 is heard (F5).");

            connectButton = new Button();
            connectButton.Text = "Connect";
            connectButton.Size = new Size(96, 27);
            connectButton.Anchor = AnchorStyles.Left;
            connectButton.Margin = new Padding(0, 0, 6, 0);
            connectButton.UseVisualStyleBackColor = true;
            connectButton.Click += OnConnectClick;

            logButton = new Button();
            logButton.Text = "Start log";
            logButton.Size = new Size(90, 27);
            logButton.Anchor = AnchorStyles.Left;
            logButton.Margin = new Padding(0);
            logButton.UseVisualStyleBackColor = true;
            logButton.Click += delegate { if (csv == null) StartCsv(); else StopCsv(); };
            toolTip.SetToolTip(logButton, "Saves date, time, mA, %, temperature, model and version of every valid line to a CSV file in the program's folder.");

            hintLabel = new Label();
            hintLabel.AutoSize = false;
            hintLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right;   // one line, "..." when too long
            hintLabel.Height = 20;
            hintLabel.TextAlign = ContentAlignment.MiddleLeft;
            hintLabel.AutoEllipsis = true;
            hintLabel.ForeColor = Theme.TextSecondary;
            hintLabel.Margin = new Padding(12, 0, 0, 0);

            top.Controls.Add(portLabel, 0, 0);
            top.Controls.Add(portCombo, 1, 0);
            top.Controls.Add(rescanButton, 2, 0);
            top.Controls.Add(connectButton, 3, 0);
            top.Controls.Add(logButton, 4, 0);
            top.Controls.Add(hintLabel, 5, 0);

            // Display (readout and gauge) above, log below with a fixed height. A plain
            // TableLayoutPanel: it scales cleanly at 125 % and 150 % display settings.
            body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.BackColor = Theme.Window;
            body.Padding = new Padding(10, 4, 10, 6);
            body.ColumnCount = 1;
            body.RowCount = 2;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            logRow = new RowStyle(SizeType.Absolute, LogHeight);
            body.RowStyles.Add(logRow);
            body.Resize += delegate { FitLogHeight(); };

            TableLayoutPanel display = new TableLayoutPanel();
            display.Dock = DockStyle.Fill;
            display.ColumnCount = 2;
            display.RowCount = 1;
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43F));
            display.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57F));
            display.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            display.BackColor = Theme.Window;
            display.Dock = DockStyle.Fill;
            display.Margin = new Padding(0, 0, 0, 8);

            readout = new ReadoutControl();
            readout.Dock = DockStyle.Fill;
            readout.Margin = new Padding(0, 0, 5, 0);
            gauge = new GaugeControl();
            gauge.Dock = DockStyle.Fill;
            gauge.Margin = new Padding(5, 0, 0, 0);
            display.Controls.Add(readout, 0, 0);
            display.Controls.Add(gauge, 1, 0);
            body.Controls.Add(display, 0, 0);

            CardPanel logCard = new CardPanel();
            logCard.Dock = DockStyle.Fill;
            logCard.Margin = Padding.Empty;
            logCard.Padding = new Padding(12, 8, 4, 6);

            TableLayoutPanel logLayout = new TableLayoutPanel();
            logLayout.Dock = DockStyle.Fill;
            logLayout.BackColor = Theme.Card;
            logLayout.ColumnCount = 3;
            logLayout.RowCount = 2;
            logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            logLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            logTitle = new Label();
            logTitle.Text = "Received";
            logTitle.AutoSize = true;
            logTitle.Anchor = AnchorStyles.Left;
            logTitle.Font = new Font(Theme.UiFont, 9F, FontStyle.Bold);
            logTitle.ForeColor = Theme.Text;
            logTitle.BackColor = Theme.Card;
            logTitle.Margin = new Padding(0, 0, 0, 4);

            pauseCheck = new CheckBox();
            pauseCheck.Text = "Pause";
            pauseCheck.AutoSize = true;
            pauseCheck.Anchor = AnchorStyles.Right;
            pauseCheck.BackColor = Theme.Card;
            pauseCheck.Margin = new Padding(0, 0, 10, 4);
            pauseCheck.CheckedChanged += OnPauseChanged;

            clearButton = new Button();
            clearButton.Text = "Clear";
            clearButton.Size = new Size(70, 25);
            clearButton.Anchor = AnchorStyles.Right;
            clearButton.Margin = new Padding(0, 0, 8, 4);
            clearButton.UseVisualStyleBackColor = true;
            clearButton.Click += delegate { logList.Items.Clear(); };

            logList = new ListBox();
            logList.Dock = DockStyle.Fill;
            logList.BorderStyle = BorderStyle.None;
            logList.IntegralHeight = false;
            logList.SelectionMode = SelectionMode.MultiExtended;
            logList.HorizontalScrollbar = true;
            logList.Font = new Font(Theme.MonoFont, 9F);
            logList.BackColor = Theme.Card;
            logList.ForeColor = Theme.Text;
            logList.Margin = Padding.Empty;
            logList.KeyDown += OnLogKeyDown;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Copy", null, delegate { CopyLog(); });
            menu.Items.Add("Select all", null, delegate { SelectAllLog(); });
            menu.Items.Add("Clear", null, delegate { logList.Items.Clear(); });
            logList.ContextMenuStrip = menu;

            logLayout.Controls.Add(logTitle, 0, 0);
            logLayout.Controls.Add(pauseCheck, 1, 0);
            logLayout.Controls.Add(clearButton, 2, 0);
            logLayout.Controls.Add(logList, 0, 1);
            logLayout.SetColumnSpan(logList, 3);
            logCard.Controls.Add(logLayout);
            body.Controls.Add(logCard, 0, 1);

            StatusStrip status = new StatusStrip();
            status.SizingGrip = true;
            status.BackColor = Theme.Window;
            stateLabel = new ToolStripStatusLabel();
            stateLabel.Spring = true;
            stateLabel.TextAlign = ContentAlignment.MiddleLeft;
            csvLabel = NewStatusLabel();
            csvLabel.ForeColor = Theme.Ok;
            csvLabel.Visible = false;
            linesLabel = NewStatusLabel();
            rejectedLabel = NewStatusLabel();
            rejectedLabel.ToolTipText = "Lines dropped by the reading rule: exactly 37 characters before CR LF (29 from earlier firmware), in the IS4420 format.";
            periodLabel = NewStatusLabel();
            periodLabel.ToolTipText = "Average time between the last 50 valid lines, measured with the PC clock. USB latency adds jitter to single lines.";
            status.ShowItemToolTips = true;
            status.Items.AddRange(new ToolStripItem[] { stateLabel, csvLabel, linesLabel, rejectedLabel, periodLabel });

            // Product links, above the status bar.
            FlowLayoutPanel links = new FlowLayoutPanel();
            links.Dock = DockStyle.Bottom;
            links.Height = 24;
            links.WrapContents = false;
            links.BackColor = Theme.Window;
            links.Padding = new Padding(12, 2, 10, 0);
            links.Controls.Add(NewLink("View INACKS IS4420 Current Loop Receiver", Is4420Url));
            links.Controls.Add(NewLink("View Kappa4420USB USB Isolated Current Loop Receiver Product", Kappa4420Url));

            // Fill first, then the bars, so the bars are docked first.
            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(headerLine);
            Controls.Add(header);
            Controls.Add(links);
            Controls.Add(status);
            AcceptButton = connectButton;

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 30;
            uiTimer.Tick += OnUiTick;
            portTimer = new System.Windows.Forms.Timer();
            portTimer.Interval = 1000;
            portTimer.Tick += OnPortTimer;

            ResumeLayout(false);
            PerformLayout();
        }

        const float LogHeight = 210f, LogHeightMin = 110f;   // at 96 dpi

        // The log keeps 210 px while there is room; on a short window it gives up height so
        // that the readout and the gauge keep most of it (up to 36 % of the body).
        void FitLogHeight()
        {
            float k = CurrentAutoScaleDimensions.Height / 96f;
            if (k < 0.5f || k > 5f) k = 1f;
            float h = Math.Max(LogHeightMin * k, Math.Min(LogHeight * k, body.ClientSize.Height * 0.36f));
            h = (float)Math.Round(h);
            if (Math.Abs(logRow.Height - h) >= 1f) logRow.Height = h;
        }

        LinkLabel NewLink(string text, string url)
        {
            LinkLabel l = new LinkLabel();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 2, 28, 0);
            l.LinkBehavior = LinkBehavior.HoverUnderline;
            l.LinkColor = Theme.Link;
            l.ActiveLinkColor = Theme.Link;
            l.VisitedLinkColor = Theme.Link;
            l.LinkClicked += delegate { OpenUrl(url); };
            toolTip.SetToolTip(l, url);
            return l;
        }

        // The INACKS logo (embedded inacks_logo.png); a text link if the image is missing.
        Control BuildLogo()
        {
            Image image = null;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("inacks_logo.png"))
                {
                    if (s != null)
                    {
                        using (Image loaded = Image.FromStream(s)) image = new Bitmap(loaded);
                    }
                }
            }
            catch (Exception)
            {
                image = null;
            }
            Control c;
            if (image != null)
            {
                LogoControl logo = new LogoControl(image);
                logo.BackColor = Color.White;
                logo.Size = new Size((int)Math.Round(38f * image.Width / image.Height), 38);
                c = logo;
            }
            else
            {
                LinkLabel l = new LinkLabel();
                l.Text = "www.inacks.com";
                l.AutoSize = true;
                l.LinkBehavior = LinkBehavior.HoverUnderline;
                l.LinkColor = Theme.Link;
                l.ActiveLinkColor = Theme.Link;
                l.VisitedLinkColor = Theme.Link;
                c = l;
            }
            c.Anchor = AnchorStyles.Right;
            c.BackColor = Color.White;
            c.Margin = new Padding(10, 0, 0, 0);
            c.Cursor = Cursors.Hand;
            c.Click += delegate { OpenUrl(InacksUrl); };
            toolTip.SetToolTip(c, "www.inacks.com");
            return c;
        }

        static void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot open " + url + "\r\n\r\n" + ex.Message, AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        static ToolStripStatusLabel NewStatusLabel()
        {
            ToolStripStatusLabel l = new ToolStripStatusLabel();
            l.BorderSides = ToolStripStatusLabelBorderSides.Left;
            l.BorderStyle = Border3DStyle.Etched;
            l.Padding = new Padding(6, 0, 6, 0);
            return l;
        }

        void LoadIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IS4420WIN.ico"))
                {
                    if (s != null) Icon = new Icon(s);
                }
            }
            catch (Exception)
            {
            }
        }

        void RestoreWindow()
        {
            Rectangle b = AppSettings.WindowBounds;
            if (b.Width >= 400 && b.Height >= 300 && AppSettings.IsOnScreen(b))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
            if (AppSettings.Maximized) WindowState = FormWindowState.Maximized;
        }

        // ------------------------------------------------------------------ form events

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                FitToScreen();
                knownPorts = PortScanner.GetPortNames();
                FillPorts(PortsWithoutProbe(knownPorts), AppSettings.LastPort);
                UpdateControls();
                UpdateCounters();
                UpdateDisplay(Stopwatch.GetTimestamp());
            }
            catch (Exception ex)
            {
                Program.ReportError(ex, false);
            }
            uiTimer.Start();
            portTimer.Start();
            try
            {
                if (startupPort != null)
                {
                    SelectOrAdd(startupPort);
                    BeginConnect(startupPort, false);
                }
                else
                {
                    StartScan(false);
                }
            }
            catch (Exception ex)
            {
                Program.ReportError(ex, false);
            }
        }

        // Keeps the window inside the working area of its screen (small screens, large
        // display scaling): smaller if needed, and moved fully onto the screen.
        void FitToScreen()
        {
            if (WindowState != FormWindowState.Normal) return;
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            int w = Math.Min(Width, wa.Width);
            int h = Math.Min(Height, wa.Height);
            if (MinimumSize.Width > w || MinimumSize.Height > h)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
            }
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - w));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h));
            if (w != Width || h != Height || x != Left || y != Top) Bounds = new Rectangle(x, y, w, h);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            CloseCsv();
            uiTimer.Stop();
            portTimer.Stop();
            link.Stop();
            AppSettings.Maximized = WindowState == FormWindowState.Maximized;
            AppSettings.WindowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            AppSettings.Save();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5 && rescanButton.Enabled)
            {
                StartScan(true);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------------ ports and scan

        static bool Same(string a, string b)
        {
            return a != null && b != null && String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static bool SameList(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!Same(a[i], b[i])) return false;
            return true;
        }

        static List<PortInfo> PortsWithoutProbe(List<string> names)
        {
            List<PortInfo> ports = new List<PortInfo>();
            foreach (string n in names)
            {
                PortInfo p = new PortInfo();
                p.Name = n;
                ports.Add(p);
            }
            return ports;
        }

        string SelectedPortName()
        {
            PortInfo p = portCombo.SelectedItem as PortInfo;
            return p == null ? null : p.Name;
        }

        void FillPorts(List<PortInfo> ports, string pick)
        {
            string wanted = pick ?? SelectedPortName();
            portCombo.BeginUpdate();
            portCombo.Items.Clear();
            int index = -1;
            foreach (PortInfo p in ports)
            {
                int i = portCombo.Items.Add(p);
                if (index < 0 && Same(p.Name, wanted)) index = i;
            }
            if (index < 0 && portCombo.Items.Count > 0) index = 0;
            portCombo.SelectedIndex = index;
            portCombo.EndUpdate();

            int width = portCombo.Width;
            foreach (object o in portCombo.Items)
            {
                width = Math.Max(width, TextRenderer.MeasureText(o.ToString(), portCombo.Font).Width + SystemInformation.VerticalScrollBarWidth + 12);
            }
            portCombo.DropDownWidth = width;
        }

        void SelectOrAdd(string port)
        {
            foreach (object o in portCombo.Items)
            {
                PortInfo p = o as PortInfo;
                if (p != null && Same(p.Name, port))
                {
                    portCombo.SelectedItem = p;
                    return;
                }
            }
            PortInfo added = new PortInfo();
            added.Name = port;
            portCombo.Items.Add(added);
            portCombo.SelectedItem = added;
        }

        // Keeps the probe results of ports that are still there; used while connected.
        void MergePorts(List<string> names)
        {
            List<PortInfo> merged = new List<PortInfo>();
            List<string> wanted = new List<string>(names);
            if (activePort != null && !PortScanner.Contains(wanted, activePort))
            {
                wanted.Add(activePort);
                wanted.Sort(PortScanner.NaturalCompare);
            }
            foreach (string n in wanted)
            {
                PortInfo found = null;
                foreach (object o in portCombo.Items)
                {
                    PortInfo p = o as PortInfo;
                    if (p != null && Same(p.Name, n)) found = p;
                }
                if (found == null)
                {
                    found = new PortInfo();
                    found.Name = n;
                }
                merged.Add(found);
            }
            FillPorts(merged, activePort ?? SelectedPortName());
        }

        // userRequest: Rescan or F5. It turns auto-connect back on after a Disconnect.
        void StartScan(bool userRequest)
        {
            if (scanning || state != ConnState.Disconnected) return;
            if (userRequest) autoConnect = true;
            scanning = true;
            userPickedDuringScan = false;
            SetHint("Listening on the COM ports for an IS4420 line\u2026", Theme.TextSecondary);
            UpdateControls();
            Thread t = new Thread(delegate()
            {
                List<PortInfo> result = null;
                try { result = PortScanner.Scan(null); }
                catch (Exception) { }
                try { BeginInvoke(new Action<List<PortInfo>>(OnScanDone), result); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Name = "Port scan";
            t.Start();
        }

        static string JoinNames(List<PortInfo> ports)
        {
            List<string> names = new List<string>();
            foreach (PortInfo p in ports) names.Add(p.Name);
            return String.Join(", ", names.ToArray());
        }

        void OnScanDone(List<PortInfo> ports)
        {
            scanning = false;
            if (IsDisposed) return;
            if (ports == null) ports = PortsWithoutProbe(PortScanner.GetPortNames());
            knownPorts = new List<string>();
            foreach (PortInfo p in ports) knownPorts.Add(p.Name);

            List<PortInfo> detected = ports.FindAll(delegate(PortInfo p) { return p.Probe == ProbeResult.Detected; });
            List<PortInfo> busy = ports.FindAll(delegate(PortInfo p) { return p.Probe == ProbeResult.Busy; });
            List<PortInfo> oldFormat = ports.FindAll(delegate(PortInfo p) { return p.Probe == ProbeResult.OldFormat; });

            string pick = userPickedDuringScan ? SelectedPortName() : null;
            if (pick == null && detected.Count > 0)
            {
                pick = detected[0].Name;
                foreach (PortInfo d in detected) if (Same(d.Name, AppSettings.LastPort)) pick = d.Name;
            }
            if (pick == null && AppSettings.LastPort != null && PortScanner.Contains(knownPorts, AppSettings.LastPort))
            {
                pick = AppSettings.LastPort;
            }
            FillPorts(ports, pick);

            if (autoConnect && detected.Count > 0 && state == ConnState.Disconnected)
            {
                // The first port in the list where an IS4420 line was heard.
                string port = detected[0].Name;
                SelectOrAdd(port);
                string others = detected.Count > 1
                    ? " (also on " + JoinNames(detected.GetRange(1, detected.Count - 1)) + ")"
                    : "";
                LogInfo("IS4420 detected on " + JoinNames(detected) + ". Connecting to " + port + ".");
                BeginConnect(port, false);
                SetHint("IS4420 detected on " + port + others + ". Connecting\u2026", Theme.Ok);
                return;
            }

            if (ports.Count == 0)
            {
                SetHint("No COM port found. Plug in the USB-UART adapter.", Theme.Warning);
            }
            else if (detected.Count == 1)
            {
                SetHint("IS4420 detected on " + detected[0].Name + ". Press Connect.", Theme.Ok);
            }
            else if (detected.Count > 1)
            {
                SetHint("IS4420 lines on " + JoinNames(detected) + ". " + SelectedPortName() + " selected.", Theme.Ok);
            }
            else if (oldFormat.Count > 0)
            {
                SetHint("IS4420 with the old START_..._END line on " + JoinNames(oldFormat) + " (early firmware): not supported.", Theme.Warning);
            }
            else if (busy.Count > 0)
            {
                SetHint("No IS4420 line heard. " + JoinNames(busy) + (busy.Count == 1 ? " is" : " are") + " in use by another program.", Theme.Warning);
            }
            else
            {
                SetHint("No IS4420 line heard on any port. Check the module supply and wiring, then Rescan.", Theme.Warning);
            }
            UpdateControls();
            if (detected.Count > 0 && state == ConnState.Disconnected && ContainsFocus) connectButton.Focus();
        }

        void OnPortTimer(object sender, EventArgs e)
        {
            if (csv != null) ShowCsvCount();
            List<string> names = PortScanner.GetPortNames();
            bool changed = !SameList(names, knownPorts);
            if (changed) knownPorts = names;

            if (state == ConnState.Connected && activePort != null && !PortScanner.Contains(names, activePort))
            {
                link.Stop();
                state = ConnState.Lost;
                LogLost(activePort + " has gone (adapter unplugged?)");
                UpdateControls();
            }
            else if (state == ConnState.Lost && !link.IsRunning && activePort != null && PortScanner.Contains(names, activePort))
            {
                BeginConnect(activePort, true);
            }

            if (changed && !scanning)
            {
                if (state == ConnState.Disconnected) StartScan(false);
                else MergePorts(names);
            }
        }

        // ------------------------------------------------------------------ connection

        void OnConnectClick(object sender, EventArgs e)
        {
            if (state == ConnState.Disconnected)
            {
                string port = SelectedPortName();
                if (port == null)
                {
                    SetHint("Select a COM port first.", Theme.Warning);
                    return;
                }
                BeginConnect(port, false);
            }
            else
            {
                Disconnect();
            }
        }

        void BeginConnect(string port, bool automatic)
        {
            activePort = port;
            reconnecting = automatic;
            if (!automatic)
            {
                lastReading = null;
                linesOk = 0;
                linesRejected = 0;
                lostLogged = false;
                oldFormatSeen = false;
                noTemperatureNoted = false;
                LogInfo("Opening " + port + ", 115200 8N1, listen only");
            }
            lineTimes.Clear();
            periodMs = double.NaN;
            noLineHintShown = false;
            countersDirty = true;
            session = link.Start(port);
            state = ConnState.Connecting;
            UpdateControls();
        }

        // Only from the Disconnect button: auto-connect stays off until Rescan.
        void Disconnect()
        {
            link.Stop();
            session = -1;
            autoConnect = false;
            ConnState was = state;
            state = ConnState.Disconnected;
            reconnecting = false;
            lastReading = null;
            if (was != ConnState.Disconnected && activePort != null) LogInfo("Disconnected from " + activePort);
            SetHint("", Theme.TextSecondary);
            UpdateControls();
            List<string> names = PortScanner.GetPortNames();
            if (!SameList(names, knownPorts))
            {
                knownPorts = names;
                StartScan(false);
            }
        }

        void LogLost(string message)
        {
            if (lostLogged) return;
            lostLogged = true;
            LogInfo(message + ". Waiting for " + activePort + " to come back.");
        }

        void DrainEvents()
        {
            LinkEvent ev;
            int budget = 500;
            while (budget-- > 0 && events.TryDequeue(out ev))
            {
                if (ev.Session != session) continue;
                switch (ev.Kind)
                {
                    case LinkEventKind.Opened:
                        state = ConnState.Connected;
                        connectedTicks = Stopwatch.GetTimestamp();
                        AppSettings.LastPort = activePort;
                        LogInfo((reconnecting ? "Reconnected to " : "Connected to ") + activePort + ". Nothing is sent to the module.");
                        reconnecting = false;
                        lostLogged = false;
                        SetHint("", Theme.TextSecondary);
                        UpdateControls();
                        break;
                    case LinkEventKind.OpenFailed:
                        if (reconnecting)
                        {
                            state = ConnState.Lost;
                        }
                        else
                        {
                            state = ConnState.Disconnected;
                            LogInfo("Cannot open " + activePort + ": " + ev.Message);
                            SetHint("Cannot open " + activePort + ": " + ev.Message, Theme.OverRange);
                        }
                        UpdateControls();
                        break;
                    case LinkEventKind.Lost:
                        state = ConnState.Lost;
                        LogLost("Connection to " + activePort + " lost: " + ev.Message);
                        UpdateControls();
                        break;
                    case LinkEventKind.Data:
                        foreach (RxLine line in ev.Lines) HandleLine(line);
                        break;
                }
            }
        }

        void HandleLine(RxLine line)
        {
            string time = line.WallTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            switch (line.Status)
            {
                case LineStatus.Valid:
                    WriteCsv(line);
                    linesOk++;
                    lastReading = line.Reading;
                    lastValidTicks = line.Timestamp;
                    RecordPeriod(line.Timestamp);
                    if (line.Reading.Warning != null)
                        AddLog(time + "  ! " + line.Text + "   " + line.Reading.Warning, true);
                    else
                        AddLog(time + "    " + line.Text, true);
                    if (line.Reading.TemperatureKind == TemperatureKind.None && !noTemperatureNoted)
                    {
                        noTemperatureNoted = true;
                        LogInfo("Line without the temperature (earlier firmware): the temperature is not shown.");
                    }
                    break;
                case LineStatus.Rejected:
                    linesRejected++;
                    if (line.OldFormat) oldFormatSeen = true;
                    RecordPeriod(line.Timestamp);
                    AddLog(time + "  x " + line.Text + "   rejected: " + line.Reason, true);
                    break;
                default:
                    AddLog(time + "  ~ " + line.Text + "   partial line at connection, skipped", true);
                    break;
            }
            countersDirty = true;
        }

        // Line period from the end-of-line times of the last 50 lines (valid or rejected).
        // Each interval counts as round(interval / median) periods, so a lost line or a pause
        // does not inflate the result, and lines delivered together by the USB adapter
        // still average out.
        void RecordPeriod(long timestamp)
        {
            lineTimes.Enqueue(timestamp);
            while (lineTimes.Count > PeriodLines + 1) lineTimes.Dequeue();
            if (lineTimes.Count < 4) return;
            long[] t = lineTimes.ToArray();
            double[] d = new double[t.Length - 1];
            for (int i = 1; i < t.Length; i++) d[i - 1] = (t[i] - t[i - 1]) * 1000.0 / Stopwatch.Frequency;
            double[] sorted = (double[])d.Clone();
            Array.Sort(sorted);
            double median = sorted[sorted.Length / 2];
            if (median <= 0) return;
            double total = 0;
            long periods = 0;
            foreach (double x in d)
            {
                total += x;
                periods += (long)Math.Round(x / median);
            }
            if (periods > 0) periodMs = total / periods;
        }

        // ------------------------------------------------------------------ periodic update

        void OnUiTick(object sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            float dt = lastFrameTicks == 0 ? 0f : (float)((now - lastFrameTicks) / (double)Stopwatch.Frequency);
            lastFrameTicks = now;
            if (dt > 0.25f) dt = 0.25f;
            DrainEvents();
            UpdateDisplay(now);
            gauge.Animate(dt);
            FlushLog();
            if (countersDirty) UpdateCounters();
        }

        void UpdateDisplay(long now)
        {
            DisplayMode mode;
            Is4420Reading r = lastReading;
            string status;
            Color color;
            bool lost = state == ConnState.Lost || (state == ConnState.Connecting && reconnecting);
            if (state == ConnState.Disconnected)
            {
                mode = DisplayMode.NoData;
                r = null;
                status = "Not connected";
                color = Theme.TextMuted;
            }
            else if (lost)
            {
                mode = r != null ? DisplayMode.Stale : DisplayMode.NoData;
                status = "Connection lost. Waiting for " + activePort + "\u2026";
                color = Theme.Warning;
            }
            else if (state == ConnState.Connecting)
            {
                mode = DisplayMode.NoData;
                r = null;
                status = "Opening " + activePort + "\u2026";
                color = Theme.TextSecondary;
            }
            else if (r == null)
            {
                mode = DisplayMode.NoData;
                double since = (now - connectedTicks) / (double)Stopwatch.Frequency;
                if (since < NoLineHintSeconds)
                {
                    status = "Waiting for data\u2026";
                    color = Theme.TextSecondary;
                }
                else
                {
                    color = Theme.Warning;
                    string hint;
                    if (oldFormatSeen)
                    {
                        status = "Old line format (START_..._END)";
                        hint = "The module sends the old line with START_ and _END (early firmware). This program reads the line without them.";
                    }
                    else if (linesRejected > 0)
                    {
                        status = "Lines received, none valid";
                        hint = "Lines arrive but none is a valid IS4420 line; the log shows why each one was rejected.";
                    }
                    else
                    {
                        status = "No valid IS4420 line received";
                        hint = "No valid IS4420 line yet. Check the module supply (3.2 to 3.5 V), GND to the adapter GND, TX to the adapter RX, and the pull-up from TX to 3.3 V.";
                    }
                    if (!noLineHintShown)
                    {
                        noLineHintShown = true;
                        LogInfo(hint);
                    }
                }
            }
            else
            {
                double age = (now - lastValidTicks) / (double)Stopwatch.Frequency;
                if (age > StaleSeconds)
                {
                    mode = DisplayMode.Stale;
                    status = String.Format(CultureInfo.InvariantCulture, "No line for {0:0} s", Math.Floor(age));
                    color = Theme.Warning;
                }
                else
                {
                    mode = DisplayMode.Live;
                    if (r.Kind == ReadingKind.OverRange)
                    {
                        status = "Over-range: the module reports EOVER";
                        color = Theme.OverRange;
                    }
                    else if (r.Kind == ReadingKind.NoMeasurement)
                    {
                        status = "No measurement: the module reports ERROR";
                        color = Theme.Warning;
                    }
                    else
                    {
                        status = "Live";
                        color = Theme.Ok;
                    }
                }
            }
            readout.SetContent(r, mode, status, color);
            gauge.SetContent(r, mode);
        }

        void UpdateCounters()
        {
            countersDirty = false;
            linesLabel.Text = "Lines " + linesOk.ToString("N0", CultureInfo.InvariantCulture);
            rejectedLabel.Text = "Rejected " + linesRejected.ToString("N0", CultureInfo.InvariantCulture);
            periodLabel.Text = double.IsNaN(periodMs) || linesOk < 3
                ? "Period \u2014"
                : String.Format(CultureInfo.InvariantCulture, "Period {0:0.0} ms", periodMs);
        }

        void UpdateControls()
        {
            bool idle = state == ConnState.Disconnected;
            portCombo.Enabled = idle && !scanning;
            rescanButton.Enabled = idle && !scanning;
            connectButton.Enabled = !(idle && scanning);
            connectButton.Text = idle ? "Connect" : "Disconnect";
            AcceptButton = idle ? connectButton : null;

            string title = AppInfo.Title + " " + AppInfo.Version;
            Text = idle || activePort == null ? title : title + " \u2014 " + activePort;
            switch (state)
            {
                case ConnState.Disconnected:
                    stateLabel.Text = scanning ? "Scanning COM ports (listen only)" : "Not connected";
                    break;
                case ConnState.Connecting:
                    stateLabel.Text = (reconnecting ? "Reopening " : "Opening ") + activePort;
                    break;
                case ConnState.Connected:
                    stateLabel.Text = "Connected to " + activePort + ", 115200 8N1, listen only";
                    break;
                default:
                    stateLabel.Text = "Connection lost, waiting for " + activePort;
                    break;
            }
        }

        void SetHint(string text, Color color)
        {
            hintLabel.Text = text;
            toolTip.SetToolTip(hintLabel, text);
            hintLabel.ForeColor = color;
        }

        // ------------------------------------------------------------------ CSV log

        // Starts a CSV file in the program's folder: date, time, mA, %, temperature, model,
        // version of every valid line until Stop log.
        void StartCsv()
        {
            string folder = Path.GetDirectoryName(Application.ExecutablePath);
            string path = Path.Combine(folder, "IS4420_log_" +
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".csv");
            try
            {
                StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false));
                w.NewLine = "\r\n";
                w.AutoFlush = true;
                w.WriteLine("Date,Time,mA,%,degC,Model,Ver");
                csv = w;
                csvPath = path;
                csvRows = 0;
                logButton.Text = "Stop log";
                LogInfo("Logging to " + path);
                SetHint("Logging to " + Path.GetFileName(path), Theme.Ok);
                csvLabel.ToolTipText = path;
                csvLabel.Visible = true;
                ShowCsvCount();
            }
            catch (Exception ex)
            {
                LogInfo("Cannot create the log file in " + folder + ": " + ex.Message);
                SetHint("Cannot create the log file: " + ex.Message, Theme.OverRange);
            }
        }

        void StopCsv()
        {
            if (csv == null) return;
            CloseCsv();
            string rows = csvRows.ToString("N0", CultureInfo.InvariantCulture);
            LogInfo("Log saved: " + csvPath + " (" + rows + " rows)");
            SetHint("Log saved: " + Path.GetFileName(csvPath) + " (" + rows + " rows)", Theme.Ok);
        }

        void CloseCsv()
        {
            if (csv == null) return;
            try { csv.Dispose(); }
            catch (Exception) { }
            csv = null;
            logButton.Text = "Start log";
            csvLabel.Visible = false;
        }

        void ShowCsvCount()
        {
            csvLabel.Text = "Logging: " + csvRows.ToString("N0", CultureInfo.InvariantCulture) + " rows";
        }

        void WriteCsv(RxLine line)
        {
            if (csv == null) return;
            Is4420Reading r = line.Reading;
            string ma, pct;
            if (r.Kind == ReadingKind.OverRange)
            {
                ma = pct = "EOVER";
            }
            else if (r.Kind == ReadingKind.NoMeasurement)
            {
                ma = pct = "ERROR";
            }
            else
            {
                ma = r.MilliAmps.ToString("0.00", CultureInfo.InvariantCulture);
                pct = r.Percent.ToString("0.0", CultureInfo.InvariantCulture);
            }
            // Empty when the line has no temperature (earlier firmware).
            string temp = "";
            if (r.TemperatureKind == TemperatureKind.Value) temp = r.Temperature.ToString("0", CultureInfo.InvariantCulture);
            else if (r.TemperatureKind == TemperatureKind.NoMeasurement) temp = "ERR";
            try
            {
                csv.WriteLine(line.WallTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "," +
                    line.WallTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "," +
                    ma + "," + pct + "," + temp + "," + r.Model + "," + r.Version);
                csvRows++;
            }
            catch (Exception ex)
            {
                CloseCsv();
                LogInfo("Writing " + csvPath + " failed: " + ex.Message + ". The log is stopped.");
                SetHint("CSV log stopped: " + ex.Message, Theme.OverRange);
            }
        }

        // ------------------------------------------------------------------ log

        static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        void AddLog(string text, bool data)
        {
            LogEntry entry = new LogEntry();
            entry.Text = text;
            entry.Data = data;
            pendingLog.Add(entry);
        }

        void LogInfo(string text)
        {
            AddLog(Stamp() + "  - " + text, false);
        }

        public void LogException(Exception ex)
        {
            AddLog(Stamp() + "  ! Error: " + ex.GetType().Name + ": " + ex.Message, false);
        }

        void FlushLog()
        {
            if (pendingLog.Count == 0) return;
            bool paused = pauseCheck.Checked;
            bool added = false;
            logList.BeginUpdate();
            try
            {
                foreach (LogEntry entry in pendingLog)
                {
                    if (paused && entry.Data)
                    {
                        linesHidden++;
                        continue;
                    }
                    logList.Items.Add(entry.Text);
                    added = true;
                }
                int excess = logList.Items.Count - LogLimit;
                for (int i = 0; i < excess; i++) logList.Items.RemoveAt(0);
                if (added && !paused && logList.Items.Count > 0)
                {
                    int visible = Math.Max(1, logList.ClientSize.Height / Math.Max(1, logList.ItemHeight));
                    logList.TopIndex = Math.Max(0, logList.Items.Count - visible);
                }
            }
            finally
            {
                logList.EndUpdate();
            }
            pendingLog.Clear();
            if (paused)
            {
                logTitle.Text = "Received (paused, " + linesHidden.ToString("N0", CultureInfo.InvariantCulture) + " lines not shown)";
            }
        }

        void OnPauseChanged(object sender, EventArgs e)
        {
            if (pauseCheck.Checked)
            {
                logTitle.Text = "Received (paused)";
                return;
            }
            logTitle.Text = "Received";
            if (linesHidden > 0)
            {
                LogInfo(linesHidden.ToString("N0", CultureInfo.InvariantCulture) + " lines arrived while paused and are not shown");
                linesHidden = 0;
            }
        }

        void OnLogKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopyLog();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                SelectAllLog();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        void CopyLog()
        {
            if (logList.SelectedItems.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            foreach (object o in logList.SelectedItems) sb.Append(o).Append("\r\n");
            try { Clipboard.SetText(sb.ToString()); }
            catch (Exception) { }
        }

        void SelectAllLog()
        {
            logList.BeginUpdate();
            for (int i = 0; i < logList.Items.Count; i++) logList.SetSelected(i, true);
            logList.EndUpdate();
        }
    }
}
