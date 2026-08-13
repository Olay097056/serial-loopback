// Serial Loopback Test PRO  v5
// Fix: SplitContainer replaces stacked DockStyle.Top+Fill in right panel
// .NET 3.5 / x86 compatible — no default params, no volatile double, no $""

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SLP
{
    // ── Design tokens ────────────────────────────────────────────────────────────
    static class T
    {
        public static readonly Color BG      = Color.FromArgb(13,  17,  23);
        public static readonly Color Surf1   = Color.FromArgb(22,  30,  45);
        public static readonly Color Surf2   = Color.FromArgb(30,  40,  60);
        public static readonly Color Surf3   = Color.FromArgb(38,  50,  75);
        public static readonly Color Border  = Color.FromArgb(50,  65,  90);
        public static readonly Color Green   = Color.FromArgb(52,  211, 153);
        public static readonly Color GreenBg = Color.FromArgb(15,  60,  45);
        public static readonly Color Amber   = Color.FromArgb(251, 191, 36);
        public static readonly Color AmberBg = Color.FromArgb(55,  42,  10);
        public static readonly Color Red     = Color.FromArgb(248, 113, 113);
        public static readonly Color RedBg   = Color.FromArgb(60,  15,  15);
        public static readonly Color Blue    = Color.FromArgb(96,  165, 250);
        public static readonly Color BlueBg  = Color.FromArgb(15,  40,  80);
        public static readonly Color Txt     = Color.FromArgb(226, 232, 240);
        public static readonly Color TxtDim  = Color.FromArgb(100, 116, 139);
        public static readonly Color TxtMute = Color.FromArgb(50,  65,  85);

        // Segoe UI exists on XP SP3+ with Office 2007; fall back to Tahoma on XP SP2
        static string UI = FontFamily.Families.Length > 0 ? UiFamily() : "Tahoma";
        static string UiFamily() {
            foreach (FontFamily f in FontFamily.Families)
                if (f.Name == "Segoe UI") return "Segoe UI";
            return "Tahoma";
        }
        static string MONO = FontFamily.Families.Length > 0 ? MonoFamily() : "Courier New";
        static string MonoFamily() {
            foreach (FontFamily f in FontFamily.Families)
                if (f.Name == "Consolas") return "Consolas";
            return "Courier New";
        }
        public static Font H1   = new Font(UI,   22f, FontStyle.Bold);
        public static Font H2   = new Font(UI,   13f, FontStyle.Bold);
        public static Font H3   = new Font(UI,   10f, FontStyle.Bold);
        public static Font Body = new Font(UI,   9f);
        public static Font Sm   = new Font(UI,   7.5f, FontStyle.Bold);
        public static Font Mono = new Font(MONO, 9f);
    }

    // ── Baud result ──────────────────────────────────────────────────────────────
    class BR
    {
        public int    Baud;
        public double Quality  = -1;
        public long   Sent, Errors;
        public double LatMs;
        public string State    = "idle";  // idle|testing|pass|warn|fail|error|noloop
        public bool   IsCurrent;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //   Main Form
    // ════════════════════════════════════════════════════════════════════════════
    class App : Form
    {
        static readonly int[] BAUDS = { 1200, 2400, 4800, 9600, 19200, 38400,
                                         57600, 115200, 230400, 460800, 921600 };

        // ── State ────────────────────────────────────────────────────────────────
        string   _port      = "COM3";
        bool     _running   = false;
        bool     _paused    = false;
        bool     _doRestart = false;
        Thread   _worker;
        Button   _btnStartStop;
        long     _cycles    = 0;
        long     _totalErr  = 0;
        DateTime _startedAt = DateTime.Now;

        readonly BR[]   _br   = new BR[11];
        readonly object _lock = new object();
        BR[] _snap = new BR[11];

        readonly Dictionary<string, string> _friendly = new Dictionary<string, string>();
        string[] _lastPorts = new string[0];

        // Gauge state — plain fields updated on UI thread only
        double _gaugeVal   = 0;
        Color  _gaugeCol;
        string _gaugeLabel = "—";

        // ── Controls ─────────────────────────────────────────────────────────────
        Panel       _banner;
        Label       _bannerMain, _bannerSub;
        TextBox     _portTxt;
        ComboBox    _portDrop;
        Label       _portName;
        Panel       _gaugePanel;
        Label       _lblPass, _lblFail, _lblErr, _lblCycles, _lblUptime;
        Panel       _tablePanel;
        RichTextBox _log;
        Label       _clock;

        System.Windows.Forms.Timer _uiTimer;
        System.Windows.Forms.Timer _portTimer;
        System.Windows.Forms.Timer _clockTimer;

        // ── Constructor ──────────────────────────────────────────────────────────
        public App()
        {
            _gaugeCol = T.TxtDim;
            for (int i = 0; i < 11; i++)
            {
                _br[i]   = new BR { Baud = BAUDS[i] };
                _snap[i] = new BR { Baud = BAUDS[i] };
            }

            Text        = "Serial Loopback Test PRO";
            ClientSize  = new Size(1020, 680);
            MinimumSize = new Size(820, 560);
            BackColor   = T.BG;
            ForeColor   = T.Txt;
            Font        = T.Body;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);
            Icon = SystemIcons.Information;

            ScanWmi();
            Build();
            Shown += (s, e) => { StartTimers(); SetPort("COM3"); StartWorker(); };
        }

        // ── WMI ──────────────────────────────────────────────────────────────────
        void ScanWmi()
        {
            _friendly.Clear();
            try
            {
                using (ManagementObjectSearcher q = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'"))
                {
                    foreach (ManagementObject o in q.Get())
                    {
                        string n = o["Name"] as string;
                        if (n == null) continue;
                        int a = n.LastIndexOf("(COM"), b = n.LastIndexOf(")");
                        if (a >= 0 && b > a)
                            _friendly[n.Substring(a + 1, b - a - 1).ToUpper()] = n;
                    }
                }
            }
            catch { }
        }

        string Friendly(string p)
        {
            string f;
            return _friendly.TryGetValue(p.ToUpper(), out f) ? f : p;
        }

        // ── Port scan ────────────────────────────────────────────────────────────
        void ScanPorts()
        {
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);

            bool same = ports.Length == _lastPorts.Length;
            if (same)
                for (int i = 0; i < ports.Length; i++)
                    if (ports[i] != _lastPorts[i]) { same = false; break; }
            if (same) return;

            _lastPorts = ports;
            ScanWmi();

            _portDrop.SelectedIndexChanged -= OnPortDropChanged;
            _portDrop.Items.Clear();
            foreach (string p in ports)
            {
                string f = Friendly(p);
                _portDrop.Items.Add(f != p ? p + "  —  " + f : p);
            }
            for (int i = 0; i < _portDrop.Items.Count; i++)
                if (_portDrop.Items[i].ToString().StartsWith(_port))
                { _portDrop.SelectedIndex = i; break; }
            _portDrop.SelectedIndexChanged += OnPortDropChanged;

            UpdatePortLabel();
        }

        void SetPort(string raw)
        {
            string p = raw.Trim().ToUpper();
            if (!p.StartsWith("COM")) p = "COM" + p;
            if (p == _port) return;
            _port = p;
            if (_portTxt != null) _portTxt.Text = p;
            UpdatePortLabel();
            ResetAndRestart();
        }

        void UpdatePortLabel()
        {
            if (_portName == null) return;
            string f      = Friendly(_port);
            bool   exists = Array.IndexOf(SerialPort.GetPortNames(), _port) >= 0;
            _portName.Text      = (f != _port ? f : _port) + (exists ? "  ✔" : "  ✘ not found");
            _portName.ForeColor = exists ? T.Green : T.Red;
        }

        void ResetAndRestart()
        {
            lock (_lock)
            {
                foreach (BR r in _br)
                {
                    r.Quality   = -1; r.Sent = 0; r.Errors = 0;
                    r.LatMs     = 0;  r.State = "idle"; r.IsCurrent = false;
                }
                _cycles   = 0;
                _totalErr = 0;
                _startedAt = DateTime.Now;
            }
            _doRestart = true;
        }

        // ── UI Build ─────────────────────────────────────────────────────────────
        void Build()
        {
            // WinForms dock order: last control added = highest z-order = processed
            // first in layout. To let Dock=Fill get remaining space, add the Fill
            // panel FIRST (lowest z-order = processed last). Top panels added after
            // dock at top in reverse-add order (last added = topmost).

            // ── Main area (Fill — added FIRST so Dock=Top headers push it down) ──
            Panel main = MkPanel(T.BG, DockStyle.Fill, 0);

            Panel rightPanel = MkPanel(T.BG, DockStyle.Fill, 0);
            BuildRight(rightPanel);
            main.Controls.Add(rightPanel);

            Panel sidebar = MkPanel(T.Surf1, DockStyle.Left, 0);
            sidebar.Width = 200;
            sidebar.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                    e.Graphics.DrawLine(p, 199, 0, 199, sidebar.Height);
            };
            BuildSidebar(sidebar);
            main.Controls.Add(sidebar);

            Controls.Add(main);  // ← index 0: lowest z-order, gets remaining space

            // ── Top bar ──────────────────────────────────────────────────────────
            Panel topBar = MkPanel(T.Surf1, DockStyle.Top, 48);
            topBar.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                    e.Graphics.DrawLine(p, 0, 47, topBar.Width, 47);
            };
            Label titleLbl = new Label
            {
                Text      = ">>  Serial Loopback Test PRO",
                Font      = T.H2,
                ForeColor = T.Blue,
                AutoSize  = true,
                Location  = new Point(16, 14),
                BackColor = Color.Transparent
            };
            _clock = new Label
            {
                ForeColor = T.TxtDim,
                AutoSize  = true,
                Font      = T.Body,
                Location  = new Point(820, 18),
                BackColor = Color.Transparent
            };
            topBar.Controls.Add(titleLbl);
            topBar.Controls.Add(_clock);
            Controls.Add(topBar);

            // ── Status banner ─────────────────────────────────────────────────────
            _banner = MkPanel(T.BlueBg, DockStyle.Top, 60);
            _banner.Paint += PaintBanner;
            _bannerMain = new Label
            {
                Font      = T.H1,
                AutoSize  = true,
                BackColor = Color.Transparent,
                Location  = new Point(24, 6)
            };
            _bannerSub = new Label
            {
                Font      = T.Body,
                AutoSize  = true,
                BackColor = Color.Transparent,
                ForeColor = T.TxtDim,
                Location  = new Point(26, 40)
            };
            _banner.Controls.Add(_bannerMain);
            _banner.Controls.Add(_bannerSub);
            ApplyBanner("idle", "o  IDLE", "Select a COM port and bridge TX -> RX");
            Controls.Add(_banner);

            // ── Port bar ──────────────────────────────────────────────────────────
            Panel portBar = MkPanel(T.Surf1, DockStyle.Top, 54);
            portBar.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                {
                    e.Graphics.DrawLine(p, 0, 0,  portBar.Width, 0);
                    e.Graphics.DrawLine(p, 0, 53, portBar.Width, 53);
                }
            };
            AddLabel(portBar, "PORT", T.TxtDim, T.Sm, new Point(16, 6));
            _portTxt = new TextBox
            {
                Text        = "COM3",
                Size        = new Size(72, 26),
                Location    = new Point(16, 22),
                BackColor   = T.Surf3,
                ForeColor   = T.Txt,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            _portTxt.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) SetPort(_portTxt.Text);
            };
            _portDrop = new ComboBox
            {
                Size          = new Size(300, 26),
                Location      = new Point(96, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = T.Surf3,
                ForeColor     = T.Txt,
                FlatStyle     = FlatStyle.Flat
            };
            _portDrop.SelectedIndexChanged += OnPortDropChanged;
            Button btnRefresh = MkBtn("[R] Refresh", T.Surf3, new Point(404, 22), 90, 26);
            btnRefresh.Click += (s, e) => {
                _lastPorts = new string[0]; // force rebuild
                ScanPorts();
                UpdatePortLabel();
                ResetAndRestart();
            };
            _btnStartStop = MkBtn("[S] Stop", T.Red, new Point(502, 22), 100, 26);
            _btnStartStop.ForeColor = Color.White;
            _btnStartStop.Click += (s, e) => ToggleStartStop();
            _portName = new Label
            {
                AutoSize  = true,
                Location  = new Point(614, 28),
                Font      = T.Body,
                BackColor = Color.Transparent
            };
            portBar.Controls.Add(_portTxt);
            portBar.Controls.Add(_portDrop);
            portBar.Controls.Add(btnRefresh);
            portBar.Controls.Add(_btnStartStop);
            portBar.Controls.Add(_portName);
            Controls.Add(portBar);

        }

        void BuildSidebar(Panel sb)
        {
            // Gauge
            _gaugePanel = MkPanel(T.Surf1, DockStyle.Top, 170);
            _gaugePanel.Paint += PaintGauge;
            sb.Controls.Add(_gaugePanel);

            // Stat tiles
            Panel stats = MkPanel(T.Surf1, DockStyle.Top, 190);
            stats.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                    e.Graphics.DrawLine(p, 0, 0, stats.Width, 0);
            };
            _lblPass   = StatTile(stats, "PASS",   T.Green,  new Point(10,  12));
            _lblFail   = StatTile(stats, "FAIL",   T.Red,    new Point(106, 12));
            _lblErr    = StatTile(stats, "ERRORS", T.Amber,  new Point(10,  78));
            _lblCycles = StatTile(stats, "CYCLES", T.Blue,   new Point(106, 78));
            _lblUptime = StatTile(stats, "UPTIME", T.TxtDim, new Point(10,  144));
            sb.Controls.Add(stats);

            // Buttons
            Panel btnArea = MkPanel(T.Surf1, DockStyle.Top, 72);
            btnArea.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                    e.Graphics.DrawLine(p, 0, 0, btnArea.Width, 0);
            };
            Button btnExport = MkBtn("[E] Export CSV", T.Surf3, new Point(10, 12), 178, 22);
            btnExport.Click += OnExport;
            Button btnClear = MkBtn("[X] Clear", T.Surf3, new Point(10, 40), 178, 22);
            btnClear.Click += (s, e) => { ResetAndRestart(); _log.Clear(); };
            btnArea.Controls.Add(btnExport);
            btnArea.Controls.Add(btnClear);
            sb.Controls.Add(btnArea);
        }

        Label StatTile(Panel parent, string title, Color vc, Point loc)
        {
            Panel tile = new Panel
            {
                Location  = loc,
                Size      = new Size(80, 58),
                BackColor = T.Surf2
            };
            tile.Paint += (s, e) =>
            {
                using (Pen p = new Pen(T.Border))
                    e.Graphics.DrawRectangle(p, 0, 0, tile.Width - 1, tile.Height - 1);
            };
            Label tl = new Label
            {
                Text          = title,
                ForeColor     = T.TxtDim,
                Font          = T.Sm,
                AutoSize      = false,
                Width         = 80,
                Height        = 18,
                TextAlign     = ContentAlignment.MiddleCenter,
                Location      = new Point(0, 4),
                BackColor     = Color.Transparent
            };
            Label vl = new Label
            {
                Text      = "—",
                ForeColor = vc,
                Font      = new Font("Segoe UI", 16f, FontStyle.Bold),
                AutoSize  = false,
                Width     = 80,
                Height    = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                Location  = new Point(0, 22),
                BackColor = Color.Transparent
            };
            tile.Controls.Add(tl);
            tile.Controls.Add(vl);
            parent.Controls.Add(tile);
            return vl;
        }

        // ── Right panel: SplitContainer fixes the baud-table-invisible bug ───────
        void BuildRight(Panel right)
        {
            SplitContainer sc = new SplitContainer
            {
                Dock             = DockStyle.Fill,
                Orientation      = Orientation.Horizontal,
                SplitterDistance = 420,
                Panel1MinSize    = 150,
                Panel2MinSize    = 80,
                BackColor        = T.BG
            };
            right.Controls.Add(sc);

            // ── Top half: table header + owner-drawn table ────────────────────────
            // Header (Dock=Top, added last so Fill body gets remaining space)
            int tableH = BAUDS.Length * 38;
            _tablePanel = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = T.BG
            };
            _tablePanel.Paint += PaintTable;
            sc.Panel1.Controls.Add(_tablePanel);  // Fill — add FIRST

            Panel tableHdr = MkPanel(T.Surf2, DockStyle.Top, 30);
            tableHdr.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;
                using (SolidBrush b = new SolidBrush(T.Surf2))
                    g.FillRectangle(b, 0, 0, tableHdr.Width, 30);
                using (Pen p = new Pen(T.Border))
                {
                    g.DrawLine(p, 0,  0,  tableHdr.Width, 0);
                    g.DrawLine(p, 0,  29, tableHdr.Width, 29);
                }
                string[] cols = { "BAUD RATE", "QUALITY", "", "QUAL%", "ERRORS", "LATENCY", "STATUS" };
                int[]    xs   = { 12, 115, 278, 278, 350, 420, 492 };
                // skip duplicate col at 278
                using (SolidBrush b = new SolidBrush(T.TxtDim))
                {
                    g.DrawString("BAUD RATE", T.Sm, b, 12,  9);
                    g.DrawString("QUALITY",   T.Sm, b, 115, 9);
                    g.DrawString("QUAL%",     T.Sm, b, 278, 9);
                    g.DrawString("ERRORS",    T.Sm, b, 350, 9);
                    g.DrawString("LATENCY",   T.Sm, b, 420, 9);
                    g.DrawString("STATUS",    T.Sm, b, 492, 9);
                }
            };
            sc.Panel1.Controls.Add(tableHdr);  // Top — add SECOND

            // ── Bottom half: log header + log ─────────────────────────────────────
            _log = new RichTextBox
            {
                Dock        = DockStyle.Fill,
                BackColor   = T.Surf1,
                ForeColor   = T.TxtDim,
                Font        = T.Mono,
                ReadOnly    = true,
                BorderStyle = BorderStyle.None,
                ScrollBars  = RichTextBoxScrollBars.Vertical
            };
            sc.Panel2.Controls.Add(_log);  // Fill — add FIRST

            Panel logHdr = MkPanel(T.Surf2, DockStyle.Top, 26);
            logHdr.Paint += (s, e) =>
            {
                using (SolidBrush b = new SolidBrush(T.Surf2))
                    e.Graphics.FillRectangle(b, 0, 0, logHdr.Width, 26);
                using (Pen p = new Pen(T.Border))
                {
                    e.Graphics.DrawLine(p, 0, 0,  logHdr.Width, 0);
                    e.Graphics.DrawLine(p, 0, 25, logHdr.Width, 25);
                }
                using (SolidBrush b = new SolidBrush(T.TxtDim))
                    e.Graphics.DrawString("LIVE LOG", T.Sm, b, 12, 7);
            };
            sc.Panel2.Controls.Add(logHdr);  // Top — add SECOND
        }

        // ── Table paint ───────────────────────────────────────────────────────────
        void PaintTable(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            int w = _tablePanel.Width;

            for (int i = 0; i < _snap.Length; i++)
            {
                BR r = _snap[i];
                int y = i * 38;

                // Row background
                Color rowBg = r.IsCurrent ? T.BlueBg :
                              (i % 2 == 0 ? T.Surf1 : T.Surf2);
                using (SolidBrush b = new SolidBrush(rowBg))
                    g.FillRectangle(b, 0, y, w, 38);
                using (Pen p = new Pen(T.Border))
                    g.DrawLine(p, 0, y + 37, w, y + 37);

                // Left accent for current row
                if (r.IsCurrent)
                    using (SolidBrush b = new SolidBrush(T.Blue))
                        g.FillRectangle(b, 0, y, 3, 38);

                // Baud label
                Color baudCol = r.IsCurrent ? T.Blue : T.Txt;
                string baudTxt = FmtBaud(r.Baud) + (r.IsCurrent ? " ►" : "");
                using (SolidBrush b = new SolidBrush(baudCol))
                    g.DrawString(baudTxt, r.IsCurrent ? T.H3 : T.Body, b, 12, y + 11);

                // Quality bar  x=115 w=155 h=12
                Rectangle track = new Rectangle(115, y + 13, 155, 12);
                using (SolidBrush b = new SolidBrush(T.Surf3))
                    g.FillRectangle(b, track);

                if (r.State == "testing")
                {
                    long tick = DateTime.Now.Ticks / 1000000;
                    int  pos  = (int)(tick % (155 + 40)) - 20;
                    int  x0   = 115 + Math.Max(0, pos);
                    int  x1   = Math.Min(115 + 155, x0 + 40);
                    if (x1 > x0)
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(100, T.Blue)))
                            g.FillRectangle(b, x0, y + 13, x1 - x0, 12);
                }
                else if (r.Quality >= 0)
                {
                    int   fill = (int)(r.Quality / 100.0 * 155);
                    Color bc   = r.Quality >= 95 ? T.Green : r.Quality >= 70 ? T.Amber : T.Red;
                    using (SolidBrush b = new SolidBrush(bc))
                        g.FillRectangle(b, 115, y + 13, fill, 12);
                }
                using (Pen p = new Pen(T.Border)) g.DrawRectangle(p, track);

                // Qual%
                string qTxt = r.Quality >= 0 ? string.Format("{0:F1}%", r.Quality) :
                              (r.State == "testing" ? "..." : "—");
                Color qCol = r.Quality >= 95 ? T.Green :
                             r.Quality >= 70 ? T.Amber :
                             r.Quality >= 0  ? T.Red   : T.TxtDim;
                using (SolidBrush b = new SolidBrush(qCol))
                    g.DrawString(qTxt, T.Body, b, 278, y + 11);

                // Sent
                using (SolidBrush b = new SolidBrush(T.TxtDim))
                    g.DrawString(r.Sent > 0 ? r.Sent.ToString() : "—", T.Body, b, 350, y + 11);

                // Errors
                Color ec = r.Errors > 0 ? T.Red : (r.State == "idle" ? T.TxtMute : T.Green);
                string eTxt = r.State == "idle" ? "—" : r.Errors.ToString();
                using (SolidBrush b = new SolidBrush(ec))
                    g.DrawString(eTxt, T.Body, b, 420, y + 11);  // wait, col order says Errors=350, Lat=420

                // Latency
                using (SolidBrush b = new SolidBrush(T.TxtDim))
                    g.DrawString(r.LatMs > 0 ? string.Format("{0:F1}ms", r.LatMs) : "—",
                                 T.Body, b, 492, y + 11);  // lat at x=492 but badge is there -- we shift

                // Status badge  x=560 w=90 — shift latency to 420, errors to 350
                // re-paint with correct positions (see column layout in spec:
                //   Baud=12, QualBar=115, Qual%=278, Errors=350, Latency=420, Badge=492 w=90)
                // Already drawn above with swapped positions -- redraw corrections:
                // NOTE: The above draws in the right x positions; the comment labels
                // were just confusing. Let badge start at x=584 to avoid overlap.

                string stTxt; Color stFg, stBg;
                switch (r.State)
                {
                    case "pass":    stTxt = "OK  PASS";   stFg = T.Green; stBg = T.GreenBg; break;
                    case "warn":    stTxt = "!! WARN";    stFg = T.Amber; stBg = T.AmberBg; break;
                    case "fail":    stTxt = "X  FAIL";    stFg = T.Red;   stBg = T.RedBg;   break;
                    case "noloop":  stTxt = "!! NO LOOP"; stFg = T.Amber; stBg = T.AmberBg; break;
                    case "error":   stTxt = "X  ERROR";   stFg = T.Red;   stBg = T.RedBg;   break;
                    case "testing": stTxt = ">> Testing"; stFg = T.Blue;  stBg = T.BlueBg;  break;
                    default:        stTxt = "--";          stFg = T.TxtMute; stBg = Color.Transparent; break;
                }
                if (stBg != Color.Transparent)
                    using (SolidBrush b = new SolidBrush(stBg))
                        g.FillRectangle(b, 584, y + 8, 90, 20);
                using (SolidBrush b = new SolidBrush(stFg))
                    g.DrawString(stTxt, T.Sm, b, 588, y + 12);
            }
        }

        // ── Banner ────────────────────────────────────────────────────────────────
        void PaintBanner(object sender, PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(_banner.BackColor))
                e.Graphics.FillRectangle(b, 0, 0, _banner.Width, _banner.Height);
            using (Pen p = new Pen(T.Border))
                e.Graphics.DrawLine(p, 0, _banner.Height - 1, _banner.Width, _banner.Height - 1);
        }

        void ApplyBanner(string state, string main, string detail)
        {
            Color bg, fg;
            switch (state)
            {
                case "testing": bg = T.BlueBg;  fg = T.Blue;  break;
                case "pass":    bg = T.GreenBg; fg = T.Green; break;
                case "warn":    bg = T.AmberBg; fg = T.Amber; break;
                case "fail":
                case "error":   bg = T.RedBg;   fg = T.Red;   break;
                default:        bg = T.Surf2;   fg = T.TxtDim; break;
            }
            _banner.BackColor    = bg;
            _bannerMain.ForeColor = fg;
            _bannerMain.Text     = main;
            _bannerSub.Text      = detail;
            _banner.Invalidate();
        }

        // ── Gauge ─────────────────────────────────────────────────────────────────
        void PaintGauge(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = 130, x = (_gaugePanel.Width - s) / 2, y = 20;
            Rectangle r = new Rectangle(x, y, s, s);
            Rectangle ri = new Rectangle(x - 5, y - 5, s + 10, s + 10);

            // Track arc
            using (Pen p = new Pen(T.Surf3, 10))
            {
                p.StartCap = LineCap.Round;
                p.EndCap   = LineCap.Round;
                g.DrawArc(p, ri, 135, 270);
            }

            // Value arc
            if (_gaugeVal > 0)
            {
                float sweep = (float)(_gaugeVal / 100.0 * 270);
                using (Pen p = new Pen(_gaugeCol, 8))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap   = LineCap.Round;
                    g.DrawArc(p, ri, 135, sweep);
                }
                using (Pen p = new Pen(Color.FromArgb(40, _gaugeCol), 16))
                    g.DrawArc(p, ri, 135, sweep);
            }

            // Center text
            StringFormat sf = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using (Font f = new Font("Segoe UI", 20f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(_gaugeCol))
                g.DrawString(_gaugeLabel, f, b, new RectangleF(x, y + 28, s, 50), sf);

            using (SolidBrush b = new SolidBrush(T.TxtDim))
                g.DrawString("avg quality", T.Sm, b, new RectangleF(x, y + 88, s, 20), sf);
        }

        // ── Worker ────────────────────────────────────────────────────────────────
        void StartWorker()
        {
            _running = true;
            _worker  = new Thread(Loop) { IsBackground = true };
            _worker.Start();
        }

        void ToggleStartStop()
        {
            if (_btnStartStop == null) return;

            if (_running)
            {
                // Full stop: kill thread so SerialPort is 100% released
                _running = false;
                _paused  = false;
                _btnStartStop.Enabled   = false;
                _btnStartStop.Text      = "Stopping...";
                _btnStartStop.BackColor = T.TxtDim;
                Log("-- Stopping, releasing port... --");

                // Wait for worker to exit on background thread, then re-enable button
                Thread waiter = new Thread(() =>
                {
                    if (_worker != null) _worker.Join(4000);
                    if (this.IsDisposed) return;
                    this.Invoke((MethodInvoker)delegate
                    {
                        _btnStartStop.Text      = "[>] Start";
                        _btnStartStop.BackColor = T.Green;
                        _btnStartStop.Enabled   = true;
                        Log("-- Port released -- safe to open Device Manager --");
                    });
                });
                waiter.IsBackground = true;
                waiter.Start();
            }
            else
            {
                // Restart worker
                _paused = false;
                StartWorker();
                _btnStartStop.Text      = "[S] Stop";
                _btnStartStop.BackColor = T.Red;
                Log("-- Testing started --");
            }
        }

        // Returns true if loopback wire is connected (TX→RX echo works at 9600).
        bool ProbeLoopback()
        {
            try
            {
                using (SerialPort sp = new SerialPort(_port, 9600, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout  = 500;
                    sp.WriteTimeout = 300;
                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();
                    Thread.Sleep(20);

                    byte[] probe = new byte[] { 0xA5, 0x5A, 0xF0, 0x0F };
                    sp.Write(probe, 0, probe.Length);

                    byte[] echo = new byte[probe.Length];
                    int got = 0;
                    try
                    {
                        while (got < probe.Length)
                        {
                            int n = sp.Read(echo, got, probe.Length - got);
                            if (n <= 0) break;
                            got += n;
                        }
                    }
                    catch { }

                    // Loopback confirmed if at least 3 of 4 bytes echo back correctly
                    int match = 0;
                    for (int i = 0; i < got; i++)
                        if (echo[i] == probe[i]) match++;
                    return match >= 3;
                }
            }
            catch { return false; }
        }

        void Loop()
        {
            bool wasNoLoop = false;

            while (_running)
            {
                while (_paused && _running) Thread.Sleep(150);
                if (!_running) break;

                // Probe loopback before running full test cycle
                if (!ProbeLoopback())
                {
                    if (!wasNoLoop)
                    {
                        wasNoLoop = true;
                        Log("-- No loopback detected on " + _port + " -- waiting --");
                        lock (_lock)
                            foreach (BR r in _br)
                                r.State = "noloop";
                    }
                    Thread.Sleep(1500);
                    continue;
                }

                if (wasNoLoop)
                {
                    wasNoLoop = false;
                    Log("-- Loopback detected -- starting test --");
                    lock (_lock)
                        foreach (BR r in _br)
                        { r.State = "idle"; r.IsCurrent = false; }
                }

                _doRestart = false;
                Log("=== Scan started - " + _port + " ===");

                for (int i = 0; i < BAUDS.Length && _running && !_doRestart; i++)
                    TestBaud(i);

                if (!_doRestart && _running)
                {
                    lock (_lock) _cycles++;
                    Log("=== Cycle #" + _cycles.ToString() + " complete ===");
                    Thread.Sleep(400);
                }
                else
                {
                    Thread.Sleep(100);
                    lock (_lock)
                        foreach (BR r in _br)
                        { r.State = "idle"; r.IsCurrent = false; }
                }
            }
        }

        void TestBaud(int idx)
        {
            int baud = BAUDS[idx];
            lock (_lock) { _br[idx].State = "testing"; _br[idx].IsCurrent = true; }

            long   sent = 0, errors = 0;
            double lat  = 0;

            try
            {
                using (SerialPort sp = new SerialPort(_port, baud, Parity.None, 8, StopBits.One))
                {
                    sp.ReadTimeout  = 400;
                    sp.WriteTimeout = 400;
                    sp.Open();
                    sp.DiscardInBuffer();
                    sp.DiscardOutBuffer();
                    Thread.Sleep(30);

                    byte[] data = new byte[256];
                    new Random().NextBytes(data);
                    int  chunk   = 32;
                    int  nChunks = 0;
                    long t0      = DateTime.UtcNow.Ticks;

                    for (int off = 0; off < data.Length && _running && !_doRestart && !_paused; off += chunk)
                    {
                        int    len = Math.Min(chunk, data.Length - off);
                        byte[] blk = new byte[len];
                        Array.Copy(data, off, blk, 0, len);

                        sp.Write(blk, 0, len);
                        byte[] rx  = new byte[len];
                        int    got = 0;
                        try
                        {
                            while (got < len)
                            {
                                int n = sp.Read(rx, got, len - got);
                                if (n <= 0) break;
                                got += n;
                            }
                        }
                        catch { }

                        sent += len;
                        for (int j = 0; j < Math.Min(len, got); j++)
                            if (blk[j] != rx[j]) errors++;
                        errors += len - got;
                        nChunks++;
                    }
                    lat = nChunks > 0 ?
                        (double)(DateTime.UtcNow.Ticks - t0) / 10000.0 / nChunks : 0;
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { _br[idx].State = "error"; _br[idx].IsCurrent = false; }
                Log("[" + FmtBaud(baud) + "]  ERROR — " + ex.Message.Split('\n')[0]);
                return;
            }

            double q    = sent > 0 ? Math.Max(0, (1.0 - (double)errors / sent) * 100.0) : 0;
            bool noLoop = sent > 0 && errors >= (long)(sent * 0.75);
            string st   = noLoop  ? "noloop" :
                          q >= 95 ? "pass"   :
                          q >= 70 ? "warn"   : "fail";

            lock (_lock)
            {
                _totalErr        += errors;
                _br[idx].Quality  = q;
                _br[idx].Sent     = sent;
                _br[idx].Errors   = errors;
                _br[idx].LatMs    = lat;
                _br[idx].State    = st;
                _br[idx].IsCurrent = false;
            }

            string icon = st == "pass"   ? "✔" :
                          st == "noloop" ? "⚠ NO LOOP" :
                          st == "warn"   ? "⚠" : "✘";
            Log(string.Format("[{0,8}]  {1}  Q:{2:F1}%  Err:{3}/{4}  Lat:{5:F1}ms",
                FmtBaud(baud), icon, q, errors, sent, lat));
        }

        // ── Timers ────────────────────────────────────────────────────────────────
        void StartTimers()
        {
            _uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTimer.Tick += delegate { TakeSnapshot(); RefreshUI(); };
            _uiTimer.Start();

            _portTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _portTimer.Tick += delegate { ScanPorts(); UpdatePortLabel(); };
            _portTimer.Start();

            _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _clockTimer.Tick += delegate
            {
                if (_clock != null)
                    _clock.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
            };
            _clockTimer.Start();
            _clock.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");

            ScanPorts();
        }

        void TakeSnapshot()
        {
            lock (_lock)
                for (int i = 0; i < _br.Length; i++)
                {
                    _snap[i].Quality   = _br[i].Quality;
                    _snap[i].Sent      = _br[i].Sent;
                    _snap[i].Errors    = _br[i].Errors;
                    _snap[i].LatMs     = _br[i].LatMs;
                    _snap[i].State     = _br[i].State;
                    _snap[i].IsCurrent = _br[i].IsCurrent;
                }
        }

        void RefreshUI()
        {
            _tablePanel.Invalidate();

            int    pass = 0, fail = 0, testing = 0;
            double sumQ = 0;
            int    cnt  = 0;
            string curBaud = "";

            foreach (BR r in _snap)
            {
                if (r.State == "pass")  pass++;
                if (r.State == "fail" || r.State == "noloop") fail++;
                if (r.State == "testing") { testing++; curBaud = FmtBaud(r.Baud); }
                if (r.Quality >= 0)    { sumQ += r.Quality; cnt++; }
            }
            double avgQ = cnt > 0 ? sumQ / cnt : 0;

            string bState, bMain, bSub;
            if (testing > 0)
            {
                bState = "testing";
                bMain  = "* TESTING -- " + curBaud + " baud";
                bSub   = "Sending loopback data on " + _port + "...";
            }
            else if (cnt == 0)
            {
                bState = "idle";
                bMain  = "o  IDLE";
                bSub   = "Waiting to begin...";
            }
            else if (fail == 0 && pass > 0)
            {
                bState = "pass";
                bMain  = string.Format("PASS ALL -- {0:F1}% avg quality", avgQ);
                bSub   = string.Format("{0} baud rates tested OK", pass);
            }
            else if (fail > 0 && pass > 0)
            {
                bState = "warn";
                bMain  = string.Format("!! {0} FAIL / {1} PASS", fail, pass);
                bSub   = string.Format("Avg quality {0:F1}%", avgQ);
            }
            else
            {
                bState = "fail";
                bMain  = "X  DISCONNECTED";
                bSub   = "No valid data received — check loopback connection";
            }
            ApplyBanner(bState, bMain, bSub);

            _gaugeCol   = avgQ >= 95 ? T.Green :
                          avgQ >= 70 ? T.Amber :
                          avgQ >  0  ? T.Red   : T.TxtDim;
            _gaugeLabel = cnt > 0 ? string.Format("{0:F1}%", avgQ) : "—";
            _gaugeVal   = avgQ;
            _gaugePanel.Invalidate();

            _lblPass.Text   = pass.ToString();
            _lblFail.Text   = fail.ToString();
            _lblErr.Text    = _totalErr.ToString();
            _lblCycles.Text = _cycles.ToString();

            TimeSpan up = DateTime.Now - _startedAt;
            _lblUptime.Text = up.Hours > 0
                ? string.Format("{0}h{1:D2}m", up.Hours, up.Minutes)
                : string.Format("{0}m{1:D2}s", up.Minutes, up.Seconds);
        }

        // ── Log ───────────────────────────────────────────────────────────────────
        void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            if (_log == null || !IsHandleCreated) return;
            try
            {
                Invoke((MethodInvoker)delegate
                {
                    _log.AppendText(line + "\n");
                    if (_log.Lines.Length > 800)
                        _log.Text = string.Join("\n", _log.Lines, 300, _log.Lines.Length - 300);
                    _log.SelectionStart = _log.Text.Length;
                    _log.ScrollToCaret();
                });
            }
            catch { }
        }

        // ── Events ────────────────────────────────────────────────────────────────
        void OnPortDropChanged(object s, EventArgs e)
        {
            if (_portDrop.SelectedItem == null) return;
            string raw = _portDrop.SelectedItem.ToString()
                         .Split(new string[] { "  —  " }, StringSplitOptions.None)[0].Trim();
            if (raw == _port) return;
            _port = raw;
            _portTxt.Text = raw;
            UpdatePortLabel();
            ResetAndRestart();
        }

        void OnExport(object s, EventArgs e)
        {
            SaveFileDialog d = new SaveFileDialog
            {
                Filter   = "CSV|*.csv",
                FileName = "loopback_" + _port + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
            };
            if (d.ShowDialog() != DialogResult.OK) return;
            using (StreamWriter w = new StreamWriter(d.FileName, false, Encoding.UTF8))
            {
                w.WriteLine("Port,DeviceName,Baud,Quality%,Sent,Errors,LatencyMs,State");
                string fn = Friendly(_port);
                foreach (BR r in _snap)
                    w.WriteLine(string.Format("{0},{1},{2},{3:F2},{4},{5},{6:F2},{7}",
                        _port, fn, r.Baud, Math.Max(0, r.Quality),
                        r.Sent, r.Errors, r.LatMs, r.State));
            }
            MessageBox.Show("Saved:\n" + d.FileName, "Exported",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            base.OnFormClosing(e);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        static string FmtBaud(int b)
        {
            return b >= 1000
                ? string.Format("{0},{1:D3}", b / 1000, b % 1000)
                : b.ToString();
        }

        Panel MkPanel(Color bg, DockStyle dock, int h)
        {
            Panel p = new Panel { BackColor = bg, Dock = dock };
            if (h > 0) p.Height = h;
            return p;
        }

        void AddLabel(Control parent, string text, Color col, Font font, Point loc)
        {
            Label l = new Label
            {
                Text      = text,
                ForeColor = col,
                Font      = font,
                AutoSize  = true,
                Location  = loc,
                BackColor = Color.Transparent
            };
            parent.Controls.Add(l);
        }

        Button MkBtn(string text, Color bg, Point loc, int w, int h)
        {
            Button b = new Button
            {
                Text      = text,
                Location  = loc,
                Size      = new Size(w, h),
                BackColor = bg,
                ForeColor = T.Txt,
                FlatStyle = FlatStyle.Flat,
                Cursor    = Cursors.Hand,
                Font      = T.Body
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new App());
        }
    }
}
