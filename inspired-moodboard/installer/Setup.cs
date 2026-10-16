// Inspired Moodboard V.1 - graphical installer and uninstaller for Windows.
// One small .exe: shows a moving wall of designs, downloads the official Electron engine once
// (checked against its SHA-256), installs per-user (no admin), adds Desktop / Start menu shortcuts
// and an entry in Settings > Apps so it can be removed like any other program.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Inspired Moodboard Setup")]
[assembly: AssemblyProduct("Inspired Moodboard")]
[assembly: AssemblyCompany("Inspired")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace InspiredSetup
{
    static class K
    {
        public const string App = "Inspired Moodboard";
        public const string Version = "1.0.0";
        public const string Engine = "38.8.6";
        public const string Sha = "366ae2b4aa9e6bc89b98c8b5831d46303e45cd49b2970c2b3921de5f2fcfc2e1";
        public static readonly string Url = "https://github.com/electron/electron/releases/download/v" + Engine + "/electron-v" + Engine + "-win32-x64.zip";
        public const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InspiredMoodboard";
        public const string ExeName = "Inspired Moodboard.exe";
        public const string UninstName = "Uninstall Inspired Moodboard.exe";
        public const string TempUninst = "Inspired Moodboard Uninstall.exe";
        public const string Folder = "INSPIRED TEST";
    }

    class UserError : Exception { public UserError(string m) : base(m) { } }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        internal static readonly string LogPath = Path.Combine(Path.GetTempPath(), "Inspired Moodboard Setup.log");

        internal static void Log(string line)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine); } catch { }
        }

        static void Crashed(object e)
        {
            Log("CRASH " + e);
            try
            {
                MessageBox.Show("Inspired Moodboard Setup hit a problem and has to close:\n\n" + (e is Exception ? ((Exception)e).Message : "unknown error") +
                    "\n\nDetails were saved to:\n" + LogPath, "Inspired Moodboard Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crashed(e.ExceptionObject);
            try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
            Application.ThreadException += (s, e) => Crashed(e.Exception);
            Log("start " + Application.ExecutablePath + " args=" + string.Join(" ", args) + " os=" + Environment.OSVersion + " 64bit=" + Environment.Is64BitProcess + " clr=" + Environment.Version);
            bool un = false; string dir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "/uninstall" || a == "-uninstall") un = true;
                else if ((a == "/dir" || a == "-dir") && i + 1 < args.Length) dir = args[++i];
            }
            string self = Application.ExecutablePath;
            if (Path.GetFileName(self).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) un = true;
            try { SetProcessDPIAware(); } catch { }

            // The uninstaller lives inside the folder it deletes, so it hands over to a copy in Temp.
            if (un && dir == null)
            {
                string inst = Path.GetDirectoryName(self);
                // Run from anywhere (e.g. a separate copy of the uninstaller): find where the app is installed.
                if (!File.Exists(Path.Combine(inst, K.ExeName))) inst = SetupForm.FindExisting();
                if (inst == null)
                {
                    MessageBox.Show("Inspired Moodboard isn't installed on this PC, so there's nothing to remove.", "Uninstall Inspired Moodboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                try
                {
                    string tmp = Path.Combine(Path.GetTempPath(), K.TempUninst);
                    File.Copy(self, tmp, true);
                    Process.Start(new ProcessStartInfo(tmp, "/uninstall /dir \"" + inst + "\"") { UseShellExecute = false });
                    return 0;
                }
                catch { dir = inst; }
            }

            bool fresh;
            using (var m = new Mutex(true, "InspiredMoodboardSetup", out fresh))
            {
                if (!fresh && Environment.GetEnvironmentVariable("IM_SHOT") == null) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var f = new SetupForm(un, dir);
                Application.Run(f);
                return f.ExitCode;
            }
        }
    }

    enum St { Welcome, Working, Done, Error, UnConfirm, UnWorking, UnDone }

    class Hot { public RectangleF R; public string Id; }

    class Tile
    {
        public Bitmap Src, B;
        public float Ar;            // natural height / width
        public int Fam;             // colour family: 0 red,1 orange,2 yellow,3 green,4 blue,5 purple/pink,10 light,11 mid grey,12 dark
        public float Hx, Hy, Br;    // weighted hue vector and brightness, for finer colour distance
        public string Name;
    }

    class Placed { public Tile T; public float Y, H; }

    class SetupForm : Form
    {
        // ---------- layout (logical px, scaled by S) ----------
        const int LW = 980, LH = 600, WALL = 560, PX = 604, PW = 336;
        float S = 1f;
        public int ExitCode = 0;

        static readonly Color BG = Hex("#0E0E12"), WALLBG = Hex("#0A0A0D"), CARD = Hex("#16161C"), CARD2 = Hex("#1C1C24"),
            INK = Hex("#F4F4F7"), MUT = Hex("#A3A3B1"), DIM = Hex("#6F6F7D"), RED = Hex("#E60023"), RED2 = Hex("#FF4D6D"), OK = Hex("#2FBF71");
        static readonly Color LINE = Color.FromArgb(24, 255, 255, 255);

        readonly bool uninstallMode;
        St state;
        string installDir;           // ...\Inspired Moodboard
        bool optDesktop = true, optOpen = true, existing = false, engineReady = false;
        string notice = null, errorMsg = null;

        // worker -> UI
        volatile bool cancel;
        bool closeAfterCancel;
        Thread worker;
        readonly object lk = new object();
        double wProgress; string wStatus = "", wDetail = "";
        double pDisp;

        // fonts
        PrivateFontCollection pfc = new PrivateFontCollection();
        Font fHead, fHead2, fPct, fPctSign, fBody, fSmall, fTiny, fSemi, fBtn, fBrand, fCap;

        // wall
        readonly List<Tile>[] cols = { new List<Tile>(), new List<Tile>(), new List<Tile>() };
        // The whole wall drifts up as one sheet, so the colour mix worked out below never changes while it moves.
        const float WallSpeed = 17f;
        static readonly float[] colShift = { 0f, -118f, -52f };
        int[] colH = new int[3];
        int[][] colY = new int[3][];
        Bitmap wallOverlay, panelBg, logo;
        int tileCount;

        // anim
        System.Windows.Forms.Timer timer;
        Stopwatch clock = Stopwatch.StartNew();
        double lastTick, wallT = 0, tipStart;
        int tipIdx;
        static readonly string[] Tips = {
            "Press T to keep your board on top of Photoshop or Figma, then hover it to make it see-through.",
            "Hold Ctrl and drag any image straight off the board into your design.",
            "Collections you save with Inspired Pin Saver show up on the left by themselves.",
            "Float shrinks the board into a small window that stays in the corner while you work.",
            "Click any colour swatch to copy its hex code.",
            "Export saves the whole board as one PNG, ready to send to a client.",
            "Press 2 to sort a board by colour, from reds round to neutrals."
        };

        List<Hot> hots = new List<Hot>();
        string hover, pressed;

        public SetupForm(bool uninstall, string dir)
        {
            uninstallMode = uninstall;
            Text = uninstall ? "Uninstall Inspired Moodboard" : "Inspired Moodboard Setup";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            try { using (var s = Res("icon.ico")) Icon = new Icon(s); } catch { }
            using (var g = CreateGraphics()) S = Math.Max(1f, g.DpiX / 96f);
            ClientSize = new Size((int)Math.Round(LW * S), (int)Math.Round(LH * S));

            LoadFonts();
            try { using (var s = Res("logo.png")) logo = new Bitmap(s); } catch { }

            if (uninstall)
            {
                installDir = dir ?? FindExisting() ?? DefaultDir();
                state = St.UnConfirm;
            }
            else
            {
                string ex = FindExisting();
                existing = ex != null;
                installDir = ex ?? DefaultDir();
                state = St.Welcome;
            }

            BuildWall();
            BuildPanelBg();

            timer = new System.Windows.Forms.Timer { Interval = 15 };
            timer.Tick += Tick;
            timer.Start();
        }

        // ================= window chrome =================
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000;          // WS_MINIMIZEBOX: taskbar click minimises
                cp.ClassStyle |= 0x00020000;     // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }   // Windows 11 rounded corners
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4); } catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            new Thread(LoadWall) { IsBackground = true, Name = "wall", Priority = ThreadPriority.BelowNormal }.Start();
            string shot = Environment.GetEnvironmentVariable("IM_SHOT");
            if (shot != null) { BeginInvoke((Action)(() => TestShot(shot))); return; }
            string auto = Environment.GetEnvironmentVariable("IM_AUTO");
            if (auto != null) BeginInvoke((Action)Primary);
        }

        // ================= assets =================
        static Stream Res(string name) { return Assembly.GetExecutingAssembly().GetManifestResourceStream(name); }

        void LoadFonts()
        {
            FontFamily disp = null, text = null, semi = null;
            foreach (var n in new[] { "IMDisplay", "IMText", "IMTextSemi" })
            {
                try
                {
                    byte[] b;
                    using (var s = Res(n + ".ttf")) using (var ms = new MemoryStream()) { s.CopyTo(ms); b = ms.ToArray(); }
                    IntPtr p = Marshal.AllocCoTaskMem(b.Length);       // kept for the life of the process
                    Marshal.Copy(b, 0, p, b.Length);
                    pfc.AddMemoryFont(p, b.Length);
                }
                catch { }
            }
            foreach (var f in pfc.Families)
            {
                if (f.Name == "IMDisplay") disp = f; else if (f.Name == "IMText") text = f; else if (f.Name == "IMTextSemi") semi = f;
            }
            if (Environment.GetEnvironmentVariable("IM_SYSFONTS") != null)
            { disp = SafeFamily("IMDisplay"); text = SafeFamily("IMText"); semi = SafeFamily("IMTextSemi"); }
            FontFamily fb = SafeFamily("Segoe UI");
            disp = disp ?? SafeFamily("Segoe UI Black") ?? fb; text = text ?? fb; semi = semi ?? SafeFamily("Segoe UI Semibold") ?? fb;
            fHead = F(disp, 50); fHead2 = F(disp, 30); fPct = F(disp, 84); fPctSign = F(disp, 30);
            fBody = F(text, 14.5f); fSmall = F(text, 12.5f); fCap = F(semi, 10.5f); fTiny = F(semi, 11.5f);
            fSemi = F(semi, 15f); fBtn = F(semi, 15.5f); fBrand = F(semi, 14f);
        }
        static FontFamily SafeFamily(string n) { try { return new FontFamily(n); } catch { return null; } }
        static Font F(FontFamily fam, float px) { return new Font(fam ?? FontFamily.GenericSansSerif, px, FontStyle.Regular, GraphicsUnit.Pixel); }

        // ---------- the design wall: read live from the INSPIRED TEST folder ----------
        const int MaxTiles = 30;
        volatile bool wallReady;
        double wallReadyAt;
        int folderCount;
        bool liveFolder;

        static string DesignFolder()
        {
            // The wall always uses the designs packed inside Setup. Reading the folder on this PC
            // (88 large, partly cut-off PNGs on Blessing's laptop) was slow and could crash Setup.
            string env = Environment.GetEnvironmentVariable("IM_DESIGNS");
            return string.IsNullOrEmpty(env) ? "" : env;
        }

        void BuildWall()
        {
            BuildWallOverlay();     // the designs themselves load in the background once the window is up (OnShown)
        }

        void LoadWall()
        {
            try { LoadWallInner(); }
            catch (Exception e) { Program.Log("wall: " + e); }   // the wall stays empty; Setup carries on
        }

        void LoadWallInner()
        {
            var rnd = new Random();
            var tiles = new List<Tile>();
            int found = 0;
            bool fromPack = false;
            try
            {
                string dir = DesignFolder();
                if (Directory.Exists(dir))
                {
                    var files = new List<string>();
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        string e = Path.GetExtension(f).ToLowerInvariant();
                        if (e == ".jpg" || e == ".jpeg" || e == ".png" || e == ".bmp" || e == ".gif") files.Add(f);
                    }
                    found = files.Count;
                    // a different random handful of the folder every time Setup opens
                    for (int i = files.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); var tmp = files[i]; files[i] = files[j]; files[j] = tmp; }
                    var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount)) };
                    System.Threading.Tasks.Parallel.ForEach(files, opts, (f, state) =>
                    {
                        lock (tiles) if (tiles.Count >= MaxTiles) { state.Stop(); return; }
                        var t = LoadThumb(f);
                        if (t == null) return;
                        bool keep;
                        lock (tiles) { keep = tiles.Count < MaxTiles; if (keep) tiles.Add(t); }
                        if (!keep) t.Src.Dispose();
                    });
                }
            }
            catch { }

            if (tiles.Count < 6)
            {
                // the folder isn't on this PC: use the copies of its designs packed inside Setup
                foreach (var t in tiles) t.Src.Dispose();
                tiles.Clear();
                foreach (var n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                {
                    if (!(n.StartsWith("d") && n.EndsWith(".jpg"))) continue;
                    try
                    {
                        Bitmap src; using (var s = Res(n)) src = new Bitmap(s);
                        tiles.Add(new Tile { Src = src, Name = n, Ar = Math.Max(0.56f, Math.Min(1.6f, src.Height / (float)src.Width)) });
                    }
                    catch { }
                }
                found = tiles.Count;
                fromPack = true;
            }
            if (tiles.Count < 3) return;
            bool live = !fromPack;
            foreach (var t in tiles) { try { Analyse(t); } catch { } }

            float colW = (WALL - 14 * 4) / 3f;
            const float GAP = 14f;
            var layout = Arrange(tiles, colW, GAP, rnd);

            // bake the tiles at their final on-screen size; every column loops over exactly the same height
            int pw = (int)Math.Round(colW * S), gap = (int)Math.Round(GAP * S);
            float rad = 12 * S;
            float period = 0; foreach (var p in layout[0]) period += p.H + GAP;
            int T = (int)Math.Round(period * S);
            var nCols = new[] { new List<Tile>(), new List<Tile>(), new List<Tile>() };
            var nY = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                var col = layout[c];
                var ys = new int[col.Count];
                int y = 0;
                for (int i = 0; i < col.Count; i++)
                {
                    int ph = i == col.Count - 1 ? T - y - gap : (int)Math.Round(col[i].H * S);
                    ph = Math.Max(20, ph);
                    ys[i] = y;
                    col[i].T.B = BakeTile(col[i].T.Src, pw, ph, rad);
                    nCols[c].Add(col[i].T);
                    y += ph + gap;
                }
                nY[c] = ys;
            }
            foreach (var t in tiles) { if (t.Src != null) { t.Src.Dispose(); t.Src = null; } }

            Ui(() =>
            {
                for (int c = 0; c < 3; c++) { cols[c].Clear(); cols[c].AddRange(nCols[c]); colY[c] = nY[c]; colH[c] = Math.Max(1, T); }
                folderCount = found;
                liveFolder = live;
                tileCount = nCols[0].Count + nCols[1].Count + nCols[2].Count;
                wallT = 0;
                wallReadyAt = clock.Elapsed.TotalSeconds;
                wallReady = true;
                Invalidate();
            });
        }

        // Opens one design and shrinks it to a 360px-wide thumbnail. Files that were cut off while saving
        // decode with an empty bottom; that part is trimmed away. Files that won't open are skipped.
        static Tile LoadThumb(string path)
        {
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[fs.Length]; int o = 0, n; while (o < bytes.Length && (n = fs.Read(bytes, o, bytes.Length - o)) > 0) o += n; }
                using (var ms = new MemoryStream(bytes))
                using (var img = Image.FromStream(ms, false, false))
                {
                    if (img.Width < 40 || img.Height < 40) return null;
                    int tw = Math.Min(360, img.Width), th = Math.Max(1, (int)Math.Round(img.Height * (tw / (float)img.Width)));
                    var thumb = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(thumb))
                    {
                        g.Clear(Color.FromArgb(0, 0, 0, 0));
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, 0, 0, tw, th);
                    }
                    int filled = FilledHeight(thumb);
                    if (filled < th * 0.45) { thumb.Dispose(); return null; }
                    if (filled < th - 2)
                    {
                        var crop = thumb.Clone(new Rectangle(0, 0, tw, filled - 2), PixelFormat.Format32bppArgb);
                        thumb.Dispose(); thumb = crop;
                    }
                    float ar = thumb.Height / (float)thumb.Width;
                    if (ar > 1.6f) { var crop = thumb.Clone(new Rectangle(0, 0, thumb.Width, (int)(thumb.Width * 1.6f)), PixelFormat.Format32bppArgb); thumb.Dispose(); thumb = crop; }
                    return new Tile { Src = thumb, Name = Path.GetFileNameWithoutExtension(path), Ar = Math.Max(0.56f, Math.Min(1.6f, thumb.Height / (float)thumb.Width)) };
                }
            }
            catch { return null; }
        }

        // Height down to the last row that has real picture in it (an unfinished file leaves transparent / pure-black rows).
        static int FilledHeight(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new int[b.Width];
                for (int y = b.Height - 1; y >= 0; y--)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, b.Width);
                    for (int x = 0; x < row.Length; x++)
                    {
                        int px = row[x];
                        if ((px & 0xFFFFFF) != 0 && ((px >> 24) & 0xFF) != 0) return y + 1;
                    }
                }
                return 0;
            }
            finally { b.UnlockBits(data); }
        }

        void BuildWallOverlay()
        {
            // fades over the wall, rendered once
            int ww = (int)Math.Round(WALL * S), wh = ClientSize.Height;
            wallOverlay = new Bitmap(ww, wh, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(wallOverlay))
            {
                using (var b = new SolidBrush(Color.FromArgb(28, BG))) g.FillRectangle(b, 0, 0, ww, wh);
                // drawn line by line so the fades look identical on every machine
                int top = (int)(80 * S), bot = (int)(210 * S), right = (int)(200 * S), left = (int)(36 * S);
                for (int y = 0; y < top; y++) Line(g, 0, y, ww, 1, 230 * Ease(1 - y / (float)top));
                for (int y = 0; y < bot; y++) Line(g, 0, wh - bot + y, ww, 1, 252 * Ease(y / (float)bot));
                for (int x = 0; x < right; x++) Line(g, ww - right + x, 0, 1, wh, 255 * Ease(x / (float)right));
                for (int x = 0; x < left; x++) Line(g, x, 0, 1, wh, 110 * Ease(1 - x / (float)left));
            }
        }

        static Bitmap BakeTile(Bitmap src, int pw, int ph, float rad)
        {
            var scaled = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sc = Math.Max(pw / (float)src.Width, ph / (float)src.Height);      // cover-crop
                float w = src.Width * sc, h = src.Height * sc;
                g.DrawImage(src, (pw - w) / 2f, (ph - h) / 2f, w, h);
            }
            var tile = new Bitmap(pw, ph, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(tile))
            using (var tb = new TextureBrush(scaled))
            using (var path = RR(new RectangleF(0, 0, pw, ph), rad))
            using (var pen = new Pen(Color.FromArgb(22, 255, 255, 255), 1f))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.FillPath(tb, path);
                using (var inner = RR(new RectangleF(0.5f, 0.5f, pw - 1, ph - 1), rad)) g.DrawPath(pen, inner);
            }
            scaled.Dispose();
            return tile;
        }

        // ---------- colour mixing ----------
        // Reads each design's main colour from a tiny thumbnail: a saturation-weighted hue, or a grey level when it has no strong colour.
        static void Analyse(Tile t)
        {
            const int W = 24, H = 30;
            using (var small = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(t.Src, 0, 0, W, H); }
                double sx = 0, sy = 0, br = 0; int n = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        var c = small.GetPixel(x, y);
                        double r = c.R / 255.0, gg = c.G / 255.0, b = c.B / 255.0;
                        double mx = Math.Max(r, Math.Max(gg, b)), mn = Math.Min(r, Math.Min(gg, b));
                        double v = mx, s = mx <= 0 ? 0 : (mx - mn) / mx;
                        double h = c.GetHue() * Math.PI / 180.0;
                        double w = s * v * v;
                        sx += w * Math.Cos(h); sy += w * Math.Sin(h); br += v; n++;
                    }
                sx /= n; sy /= n; br /= n;
                double mag = Math.Sqrt(sx * sx + sy * sy);
                double hue = (Math.Atan2(sy, sx) * 180 / Math.PI + 360) % 360;
                t.Hx = (float)sx; t.Hy = (float)sy; t.Br = (float)br;
                if (mag < 0.05) t.Fam = br > 0.62 ? 10 : br > 0.36 ? 11 : 12;
                else if (hue < 15 || hue >= 330) t.Fam = 0;
                else if (hue < 45) t.Fam = 1;
                else if (hue < 70) t.Fam = 2;
                else if (hue < 160) t.Fam = 3;
                else if (hue < 260) t.Fam = 4;
                else t.Fam = 5;
            }
        }

        // How much two designs clash when they touch: same colour family is the big cost, near-identical colours a small extra one.
        static float Clash(Tile a, Tile b)
        {
            float pen = a.Fam == b.Fam ? 10f : 0f;
            float dx = (a.Hx - b.Hx) * 4f, dy = (a.Hy - b.Hy) * 4f, db = a.Br - b.Br;
            float d = (float)Math.Sqrt(dx * dx + dy * dy + db * db);
            if (d < 0.35f) pen += (0.35f - d) * 4f;
            return pen;
        }

        // Shuffles the designs into three columns (new order every launch) and keeps the arrangement whose
        // neighbours - above/below, side by side, and across each column's loop point - clash the least.
        static List<Placed>[] Arrange(List<Tile> tiles, float colW, float gap, Random rnd)
        {
            List<Placed>[] best = null; float bestScore = float.MaxValue;
            var sw = Stopwatch.StartNew();
            for (int trial = 0; trial < 4000 && (trial < 300 || sw.ElapsedMilliseconds < 350); trial++)
            {
                var pool = new List<Tile>(tiles);
                for (int i = pool.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); var tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp; }
                var cl = new[] { new List<Placed>(), new List<Placed>(), new List<Placed>() };
                var bottom = new float[3];
                for (int c = 0; c < 3; c++) bottom[c] = colShift[c];
                while (pool.Count > 0)
                {
                    int c = 0;
                    for (int k = 1; k < 3; k++) if (bottom[k] < bottom[c]) c = k;
                    int pick = 0; float pickPen = float.MaxValue;
                    int look = Math.Min(pool.Count, 10);
                    for (int i = 0; i < look; i++)
                    {
                        var t = pool[i]; float h = colW * t.Ar, y = bottom[c];
                        float pen = (float)rnd.NextDouble() * 0.5f;
                        if (cl[c].Count > 0) pen += Clash(cl[c][cl[c].Count - 1].T, t);
                        for (int n = c - 1; n <= c + 1; n += 2)
                        {
                            if (n < 0 || n > 2) continue;
                            foreach (var p in cl[n]) if (Overlap(p.Y - 34, p.H + 68, y, h) > 0) pen += Clash(p.T, t) * 0.8f;
                        }
                        if (pen < pickPen) { pickPen = pen; pick = i; }
                    }
                    var chosen = pool[pick]; pool.RemoveAt(pick);
                    float ch = colW * chosen.Ar;
                    cl[c].Add(new Placed { T = chosen, Y = bottom[c], H = ch });
                    bottom[c] += ch + gap;
                }
                if (cl[0].Count < 2 || cl[1].Count < 2 || cl[2].Count < 2) continue;

                // even out the columns so they loop together (each design is gently re-cropped, never stretched)
                var tot = new float[3]; float target = 0;
                for (int c = 0; c < 3; c++) { foreach (var p in cl[c]) tot[c] += p.H + gap; target += tot[c] / 3f; }
                float score = 0;
                for (int c = 0; c < 3; c++)
                {
                    float sumH = tot[c] - cl[c].Count * gap;
                    float f = (target - cl[c].Count * gap) / sumH;
                    if (f < 0.82f || f > 1.22f) score += 25f;
                    float y = colShift[c];
                    foreach (var p in cl[c])
                    {
                        float ar = p.H * f / colW;
                        if (ar < 0.5f || ar > 1.78f) score += 6f;
                        p.Y = y; p.H *= f; y += p.H + gap;
                    }
                }
                score += Score(cl, target);
                if (score < bestScore) { bestScore = score; best = cl; if (score <= 0.001f) break; }
            }
            if (Environment.GetEnvironmentVariable("IM_LAYOUT") != null)
            {
                Console.WriteLine("layout score " + bestScore);
                for (int c = 0; c < 3; c++)
                {
                    var sb = new System.Text.StringBuilder("col" + c + ":");
                    foreach (var p in best[c]) sb.Append(" " + p.T.Name.Replace(".jpg", "") + "/f" + p.T.Fam + "@" + (int)p.Y + "+" + (int)p.H);
                    Console.WriteLine(sb);
                }
            }
            return best;
        }

        static float Overlap(float y1, float h1, float y2, float h2)
        {
            return Math.Max(0, Math.Min(y1 + h1, y2 + h2) - Math.Max(y1, y2));
        }

        static float Score(List<Placed>[] cl, float period)
        {
            float s = 0;
            for (int c = 0; c < 3; c++)
            {
                var col = cl[c];
                for (int i = 0; i < col.Count; i++) s += Clash(col[i].T, col[(i + 1) % col.Count].T);    // includes the loop seam
            }
            for (int c = 0; c < 2; c++)
                foreach (var a in cl[c])
                    foreach (var b in cl[c + 1])
                        for (int k = -1; k <= 1; k++)
                            if (Overlap(a.Y - 34, a.H + 68, b.Y + k * period, b.H) > 0) s += Clash(a.T, b.T) * 0.8f;
            return s;
        }

        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }

        static void Line(Graphics g, int x, int y, int w, int h, float a)
        {
            int ai = (int)Math.Round(a);
            if (ai <= 0) return;
            using (var b = new SolidBrush(Color.FromArgb(Math.Min(255, ai), BG))) g.FillRectangle(b, x, y, w, h);
        }

        void BuildPanelBg()
        {
            int x0 = (int)Math.Round(WALL * S);
            int w = ClientSize.Width - x0, h = ClientSize.Height;
            panelBg = new Bitmap(Math.Max(1, w), h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(panelBg))
            {
                g.Clear(BG);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // the app's glowing Pinterest red, top-right
                using (var path = new GraphicsPath())
                {
                    var r = new RectangleF(w - 330 * S, -300 * S, 620 * S, 560 * S);
                    path.AddEllipse(r);
                    using (var pg = new PathGradientBrush(path))
                    {
                        pg.CenterColor = Color.FromArgb(70, RED);
                        pg.SurroundColors = new[] { Color.FromArgb(0, RED) };
                        pg.FocusScales = new PointF(0.05f, 0.05f);
                        g.FillPath(pg, path);
                    }
                }
                using (var path = new GraphicsPath())
                {
                    var r = new RectangleF(-260 * S, h - 220 * S, 520 * S, 440 * S);
                    path.AddEllipse(r);
                    using (var pg = new PathGradientBrush(path))
                    {
                        pg.CenterColor = Color.FromArgb(22, RED2);
                        pg.SurroundColors = new[] { Color.FromArgb(0, RED2) };
                        g.FillPath(pg, path);
                    }
                }
            }
        }

        // ================= paths =================
        static string DefaultDir()
        {
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(la, "Programs"), K.App);
        }

        internal static string FindExisting()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(K.UninstKey))
                {
                    var v = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(v) && File.Exists(Path.Combine(v, K.ExeName))) return v;
                }
            }
            catch { }
            string d = DefaultDir();
            return File.Exists(Path.Combine(d, K.ExeName)) ? d : null;   // includes installs made by the old .cmd setup
        }

        void RefreshEngineReady()
        {
            engineReady = File.Exists(Path.Combine(installDir, K.ExeName)) && File.Exists(Path.Combine(installDir, "engine-" + K.Engine + ".txt"));
        }

        static bool LooksLikeOurs(string d)
        {
            if (!Directory.Exists(d)) return true;
            string res = Path.Combine(d, "resources");
            if (File.Exists(Path.Combine(d, K.ExeName)) || File.Exists(Path.Combine(res, "app.asar")) || File.Exists(Path.Combine(Path.Combine(res, "app"), "package.json"))) return true;
            try { if (Directory.GetFiles(d, "engine-*.txt").Length > 0) return true; } catch { }
            try { return Directory.GetFileSystemEntries(d).Length == 0; } catch { return false; }
        }

        // ================= animation =================
        void Tick(object s, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.1, now - lastTick); lastTick = now;
            if (WindowState == FormWindowState.Minimized) return;
            wallT += dt;
            Invalidate(new Rectangle(0, 0, (int)Math.Ceiling(WALL * S), ClientSize.Height));

            if (state == St.Working || state == St.UnWorking)
            {
                double tgt; lock (lk) tgt = wProgress;
                pDisp += (tgt - pDisp) * Math.Min(1.0, dt * 4.0);
                InvalidateL(new RectangleF(PX - 4, 200, PW + 8, 230));
                if (state == St.Working)
                {
                    if (now - tipStart > 7.5) { tipStart = now; tipIdx = (tipIdx + 1) % Tips.Length; }
                    if (now - tipStart < 0.6) InvalidateL(new RectangleF(PX - 4, 436, PW + 8, 100));
                }
            }
        }

        void InvalidateL(RectangleF r)
        {
            Invalidate(Rectangle.Inflate(Rectangle.Round(new RectangleF(r.X * S, r.Y * S, r.Width * S, r.Height * S)), 2, 2));
        }

        void SetState(St s)
        {
            state = s; hover = null; pressed = null;
            if (s == St.Working || s == St.UnWorking) { pDisp = 0; lock (lk) { wProgress = 0; wStatus = "Getting ready"; wDetail = ""; } tipStart = clock.Elapsed.TotalSeconds; }
            Cursor = Cursors.Default;
            Invalidate();
        }

        // ================= painting =================
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { Render(e.Graphics, e.ClipRectangle); }

        void Render(Graphics g, Rectangle clip)
        {
            int wallPx = (int)Math.Round(WALL * S);
            if (clip.Left < wallPx) DrawWall(g);
            if (clip.Right > wallPx - 2) DrawPanel(g);
        }

        void DrawWall(Graphics g)
        {
            int wallPx = (int)Math.Round(WALL * S);
            var st = g.Save();
            g.SetClip(new Rectangle(0, 0, wallPx, ClientSize.Height));
            using (var b = new SolidBrush(WALLBG)) g.FillRectangle(b, 0, 0, wallPx, ClientSize.Height);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            float colW = (WALL - 14 * 4) / 3f;
            int gap = (int)Math.Round(14 * S);
            for (int c = 0; c < 3; c++)
            {
                if (cols[c].Count == 0) continue;
                int x = (int)Math.Round((14 + c * (colW + 14)) * S);
                double off = (colShift[c] - WallSpeed * wallT) * S;
                int H = colH[c];
                int start = (int)Math.Floor(((off % H) + H) % H) - H;
                for (int rep = 0; start + rep * H < ClientSize.Height; rep++)
                {
                    int baseY = start + rep * H;
                    for (int i = 0; i < cols[c].Count; i++)
                    {
                        var t = cols[c][i];
                        int y = baseY + colY[c][i];
                        if (y + t.B.Height > 0 && y < ClientSize.Height) g.DrawImage(t.B, new Rectangle(x, y, t.B.Width, t.B.Height));
                    }
                }
            }
            g.DrawImage(wallOverlay, new Rectangle(0, 0, wallOverlay.Width, wallOverlay.Height));
            double fadeAge = wallReady ? clock.Elapsed.TotalSeconds - wallReadyAt : 0;
            if (fadeAge < 0.9)
            {
                int a = (int)(255 * (1 - Ease((float)(fadeAge / 0.9))));
                if (a > 0) using (var b = new SolidBrush(Color.FromArgb(a, WALLBG))) g.FillRectangle(b, 0, 0, wallPx, ClientSize.Height);
            }

            g.Restore(st);
        }

        void DrawPanel(Graphics g)
        {
            int wallPx = (int)Math.Round(WALL * S);
            var st = g.Save();
            g.SetClip(new Rectangle(wallPx, 0, ClientSize.Width - wallPx, ClientSize.Height), CombineMode.Intersect);
            g.DrawImage(panelBg, new Rectangle(wallPx, 0, panelBg.Width, panelBg.Height));
            g.ScaleTransform(S, S);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Hint(g);
            var hs = new List<Hot>();

            // window buttons
            WinBtn(g, hs, "min", new RectangleF(LW - 88, 10, 38, 32));
            WinBtn(g, hs, "close", new RectangleF(LW - 48, 10, 38, 32));

            // brand row
            if (logo != null) g.DrawImage(logo, new RectangleF(PX, 40, 30, 30));
            Txt(g, "Inspired", fBrand, INK, PX + 40, 46);
            float bw = Measure(g, "Inspired", fBrand);
            Txt(g, uninstallMode ? "Uninstall" : "Setup", fBrand, DIM, PX + 40 + bw + 6, 46);

            // headline
            Txt(g, "Inspired", fHead, INK, PX - 3, 96);
            Txt(g, "Moodboard", fHead, INK, PX - 3, 146);
            float hw = Measure(g, "Moodboard", fHead);
            var pill = new RectangleF(PX - 3 + hw + 10, 162, 40, 24);
            using (var path = RR(pill, 12))
            using (var b = new LinearGradientBrush(pill, RED, RED2, 20f)) g.FillPath(b, path);
            TxtC(g, "V.1", fTiny, Color.White, pill);

            switch (state)
            {
                case St.Welcome: DrawWelcome(g, hs); break;
                case St.Working: case St.UnWorking: DrawWorking(g, hs); break;
                case St.Done: DrawDone(g, hs); break;
                case St.Error: DrawError(g, hs); break;
                case St.UnConfirm: DrawUnConfirm(g, hs); break;
                case St.UnDone: DrawUnDone(g, hs); break;
            }
            hots = hs;
            g.Restore(st);
        }

        void DrawWelcome(Graphics g, List<Hot> hs)
        {
            Txt(g, "Your Pinterest saves as one living board that floats over Photoshop, Figma and Canva.", fBody, MUT, PX, 222, PW, 60);

            Txt(g, "INSTALL TO", fCap, DIM, PX, 300);
            var box = new RectangleF(PX, 318, PW, 42);
            using (var path = RR(box, 10)) using (var b = new SolidBrush(CARD)) using (var p = new Pen(LINE)) { g.FillPath(b, path); g.DrawPath(p, path); }
            var chg = new RectangleF(box.Right - 78, box.Y + 5, 72, 32);
            bool hc = hover == "change";
            if (hc) using (var path = RR(chg, 8)) using (var b = new SolidBrush(Color.FromArgb(18, 255, 255, 255))) g.FillPath(b, path);
            TxtC(g, "Change", fTiny, hc ? INK : RED2, chg);
            hs.Add(new Hot { R = chg, Id = "change" });
            TxtPath(g, installDir, fSmall, INK, new RectangleF(box.X + 14, box.Y + 12, box.Width - 100, 20));

            Check(g, hs, "desk", "Add a Desktop shortcut", optDesktop, 378);
            Check(g, hs, "open", "Open it when it's done", optOpen, 410);

            Button(g, hs, "primary", existing ? "Update Inspired Moodboard" : "Install", new RectangleF(PX, 470, PW, 54), true);

            string foot = notice ?? "Free  ·  Works offline  ·  Nothing to download";
            Txt(g, foot, fSmall, notice != null ? RED2 : DIM, PX, 540, PW, 40);
        }

        void DrawWorking(Graphics g, List<Hot> hs)
        {
            double p; string status, detail;
            lock (lk) { status = wStatus; detail = wDetail; }
            p = Math.Max(0, Math.Min(1, pDisp));
            string pct = ((int)Math.Floor(p * 100)).ToString();
            Txt(g, pct, fPct, INK, PX - 4, 206);
            float pw = Measure(g, pct, fPct);
            Txt(g, "%", fPctSign, RED2, PX - 4 + pw + 4, 222);

            Txt(g, status, fSemi, INK, PX, 316, PW, 22);
            Txt(g, detail, fSmall, MUT, PX, 340, PW, 20);

            var bar = new RectangleF(PX, 378, PW, 6);
            using (var path = RR(bar, 3)) using (var b = new SolidBrush(CARD2)) g.FillPath(b, path);
            float fw = (float)(bar.Width * p);
            if (fw > 1)
            {
                var fr = new RectangleF(bar.X, bar.Y, Math.Max(6, fw), bar.Height);
                using (var path = RR(fr, 3))
                using (var b = new LinearGradientBrush(new RectangleF(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), RED, RED2, 0f)) g.FillPath(b, path);
                // moving sheen
                double t = clock.Elapsed.TotalSeconds;
                float sx = bar.X + (float)((t * 160) % (bar.Width + 120)) - 60;
                var sr = new RectangleF(sx, bar.Y, 60, bar.Height);
                var old = g.Clip;
                using (var path = RR(fr, 3))
                {
                    g.SetClip(path, CombineMode.Intersect);
                    using (var b = new LinearGradientBrush(new RectangleF(sr.X - 1, sr.Y, sr.Width + 2, sr.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
                    {
                        var cb = new ColorBlend(3);
                        cb.Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) };
                        cb.Positions = new[] { 0f, 0.5f, 1f };
                        b.InterpolationColors = cb;
                        g.FillRectangle(b, sr);
                    }
                    g.Clip = old;
                }
            }

            if (state == St.Working)
            {
                var card = new RectangleF(PX, 436, PW, 92);
                using (var path = RR(card, 14)) using (var b = new SolidBrush(CARD)) using (var pn = new Pen(LINE)) { g.FillPath(b, path); g.DrawPath(pn, path); }
                double age = clock.Elapsed.TotalSeconds - tipStart;
                int a = (int)(255 * Math.Max(0, Math.Min(1, age / 0.5)));
                Txt(g, "WHILE YOU WAIT", fCap, Color.FromArgb(a, RED2), card.X + 18, card.Y + 16);
                Txt(g, Tips[tipIdx], fBody, Color.FromArgb(a, INK), card.X + 18, card.Y + 36, card.Width - 36, 48);

                Ghost(g, hs, "cancel", "Cancel", new RectangleF(PX - 10, 546, 84, 34), false);
            }
        }

        void DrawDone(Graphics g, List<Hot> hs)
        {
            Badge(g, PX, 222, OK, true);
            Txt(g, "You're all set", fHead2, INK, PX, 278);
            Txt(g, "Inspired Moodboard is on your Desktop and in the Start menu. Boards you save with Inspired Pin Saver show up on the left.", fBody, MUT, PX, 324, PW, 80);
            Button(g, hs, "primary", optOpen ? "Finish" : "Open Inspired Moodboard", new RectangleF(PX, 470, PW, 54), true);
            if (!optOpen) Ghost(g, hs, "close2", "Close", new RectangleF(PX + PW / 2 - 50, 534, 100, 36), true);
            else Txt(g, "It's opening now.", fSmall, DIM, PX, 540, PW, 20);
        }

        void DrawError(Graphics g, List<Hot> hs)
        {
            Badge(g, PX, 222, RED, false);
            Txt(g, "That didn't work", fHead2, INK, PX, 278);
            Txt(g, errorMsg ?? "Something went wrong.", fBody, MUT, PX, 324, PW, 120);
            Button(g, hs, "primary", "Try again", new RectangleF(PX, 470, PW, 54), true);
            Ghost(g, hs, "close2", "Close", new RectangleF(PX + PW / 2 - 50, 534, 100, 36), true);
        }

        void DrawUnConfirm(Graphics g, List<Hot> hs)
        {
            Txt(g, "Remove it from this PC?", fHead2, INK, PX, 230);
            Txt(g, "This removes the app, its shortcuts and its settings. Your images, boards and Pinterest folders stay exactly where they are.", fBody, MUT, PX, 276, PW, 100);
            Button(g, hs, "primary", "Remove", new RectangleF(PX, 470, PW, 54), false);
            Ghost(g, hs, "close2", "Keep it", new RectangleF(PX + PW / 2 - 50, 534, 100, 36), true);
        }

        void DrawUnDone(Graphics g, List<Hot> hs)
        {
            Badge(g, PX, 222, OK, true);
            Txt(g, "Removed", fHead2, INK, PX, 278);
            Txt(g, "Inspired Moodboard is gone from this PC. Your images weren't touched.", fBody, MUT, PX, 324, PW, 80);
            Button(g, hs, "primary", "Close", new RectangleF(PX, 470, PW, 54), true);
        }

        // ---------- widgets ----------
        void Button(Graphics g, List<Hot> hs, string id, string label, RectangleF r, bool brand)
        {
            bool h = hover == id, p = pressed == id;
            using (var path = RR(r, 13))
            {
                if (brand)
                {
                    using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), RED, RED2, 15f)) g.FillPath(b, path);
                }
                else using (var b = new SolidBrush(Hex("#2A2A33"))) g.FillPath(b, path);
                if (h && !p) using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(b, path);
                if (p) using (var b = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillPath(b, path);
                using (var pn = new Pen(Color.FromArgb(brand ? 40 : 20, 255, 255, 255))) g.DrawPath(pn, path);
            }
            TxtC(g, label, fBtn, Color.White, r);
            hs.Add(new Hot { R = r, Id = id });
        }

        void Ghost(Graphics g, List<Hot> hs, string id, string label, RectangleF r, bool center)
        {
            bool h = hover == id;
            if (h) using (var path = RR(r, 9)) using (var b = new SolidBrush(Color.FromArgb(16, 255, 255, 255))) g.FillPath(b, path);
            TxtC(g, label, fTiny, h ? INK : MUT, r);
            hs.Add(new Hot { R = r, Id = id });
        }

        void WinBtn(Graphics g, List<Hot> hs, string id, RectangleF r)
        {
            bool h = hover == id;
            if (h)
                using (var path = RR(r, 8))
                using (var b = new SolidBrush(id == "close" ? RED : Color.FromArgb(22, 255, 255, 255))) g.FillPath(b, path);
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var pn = new Pen(h ? Color.White : MUT, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (id == "close") { g.DrawLine(pn, cx - 5, cy - 5, cx + 5, cy + 5); g.DrawLine(pn, cx + 5, cy - 5, cx - 5, cy + 5); }
                else g.DrawLine(pn, cx - 5.5f, cy, cx + 5.5f, cy);
            }
            hs.Add(new Hot { R = r, Id = id });
        }

        void Check(Graphics g, List<Hot> hs, string id, string label, bool on, float y)
        {
            var r = new RectangleF(PX, y, 20, 20);
            bool h = hover == id;
            using (var path = RR(r, 6))
            {
                if (on) using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, 22, 22), RED, RED2, 45f)) g.FillPath(b, path);
                else
                {
                    using (var b = new SolidBrush(CARD)) g.FillPath(b, path);
                    using (var pn = new Pen(h ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(60, 255, 255, 255), 1.2f)) g.DrawPath(pn, path);
                }
            }
            if (on)
                using (var pn = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pn, new[] { new PointF(r.X + 5.2f, r.Y + 10.4f), new PointF(r.X + 8.6f, r.Y + 13.6f), new PointF(r.X + 14.8f, r.Y + 6.8f) });
            Txt(g, label, fBody, h ? Color.White : INK, PX + 32, y + 0.5f);
            float lw = Measure(g, label, fBody);
            hs.Add(new Hot { R = new RectangleF(PX - 4, y - 5, 32 + lw + 10, 30), Id = id });
        }

        void Badge(Graphics g, float x, float y, Color c, bool tick)
        {
            var r = new RectangleF(x, y, 44, 44);
            using (var b = new SolidBrush(Color.FromArgb(40, c))) g.FillEllipse(b, r);
            var r2 = RectangleF.Inflate(r, -6, -6);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r2);
            using (var pn = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (tick) g.DrawLines(pn, new[] { new PointF(x + 15, y + 22.5f), new PointF(x + 20, y + 27.5f), new PointF(x + 29.5f, y + 17) });
                else { g.DrawLine(pn, x + 22, y + 15, x + 22, y + 24); using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x + 20.5f, y + 27.5f, 3, 3); }
            }
        }

        // ---------- drawing helpers ----------
        static void Hint(Graphics g) { g.TextRenderingHint = TextRenderingHint.AntiAlias; }

        static StringFormat SF(bool wrap)
        {
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        static void Txt(Graphics g, string s, Font f, Color c, float x, float y, float w = 0, float h = 0)
        {
            using (var b = new SolidBrush(c)) using (var sf = SF(w > 0))
            {
                if (w > 0) g.DrawString(s, f, b, new RectangleF(x, y, w, h > 0 ? h : 1000), sf);
                else g.DrawString(s, f, b, x, y, sf);
            }
        }

        static void TxtPath(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            var old = g.Clip;
            g.SetClip(r, CombineMode.Intersect);
            using (var b = new SolidBrush(c)) using (var sf = SF(false)) { sf.Trimming = StringTrimming.EllipsisPath; g.DrawString(s, f, b, r, sf); }
            g.Clip = old;
        }

        static void TxtC(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (var b = new SolidBrush(c)) using (var sf = SF(false))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                g.DrawString(s, f, b, new RectangleF(r.X, r.Y + 0.5f, r.Width, r.Height), sf);
            }
        }

        static float Measure(Graphics g, string s, Font f)
        {
            using (var sf = SF(false)) return g.MeasureString(s, f, new PointF(0, 0), sf).Width;
        }

        static GraphicsPath RR(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Hex(string h)
        {
            return Color.FromArgb(Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));
        }

        // ================= input =================
        string HitTest(Point p)
        {
            var lp = new PointF(p.X / S, p.Y / S);
            for (int i = hots.Count - 1; i >= 0; i--) if (hots[i].R.Contains(lp)) return hots[i].Id;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(new Rectangle((int)(WALL * S), 0, ClientSize.Width, ClientSize.Height)); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != null) { hover = null; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string h = HitTest(e.Location);
            if (h != null) { pressed = h; Invalidate(); return; }
            try { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } catch { }   // drag the window
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            string h = HitTest(e.Location);
            string was = pressed; pressed = null;
            if (was != null && was == h) Act(h);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter) { Primary(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { RequestClose(); e.Handled = true; }
        }

        void Act(string id)
        {
            switch (id)
            {
                case "close": RequestClose(); break;
                case "close2": RequestClose(); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "desk": optDesktop = !optDesktop; Invalidate(); break;
                case "open": optOpen = !optOpen; Invalidate(); break;
                case "change": ChooseFolder(); break;
                case "cancel": RequestClose(); break;
                case "primary": Primary(); break;
            }
        }

        void Primary()
        {
            switch (state)
            {
                case St.Welcome: case St.Error:
                    if (uninstallMode) StartUninstall(); else StartInstall(); break;
                case St.Done:
                    if (!optOpen) Launch();
                    ExitCode = 0; Close(); break;
                case St.UnConfirm: StartUninstall(); break;
                case St.UnDone: Close(); break;
            }
        }

        void RequestClose()
        {
            if (state == St.UnWorking) return;
            if (state == St.Working)
            {
                var r = MessageBox.Show(this, "Stop installing Inspired Moodboard?", "Inspired Moodboard Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r == DialogResult.Yes) { cancel = true; closeAfterCancel = true; lock (lk) { wStatus = "Stopping"; wDetail = ""; } }
                return;
            }
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if ((state == St.Working || state == St.UnWorking) && worker != null && worker.IsAlive)
            {
                e.Cancel = true;
                if (state == St.Working && e.CloseReason == CloseReason.UserClosing) RequestClose();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // a Temp copy of the uninstaller removes itself once it has closed
            try
            {
                string self = Application.ExecutablePath;
                if (string.Equals(Path.GetFileName(self), K.TempUninst, StringComparison.OrdinalIgnoreCase) &&
                    Environment.OSVersion.Platform == PlatformID.Win32NT)
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + self + "\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }

        void ChooseFolder()
        {
            using (var d = new FolderBrowserDialog())
            {
                d.Description = "Choose where to install Inspired Moodboard. Setup makes an \"Inspired Moodboard\" folder inside the one you pick.";
                d.ShowNewFolderButton = true;
                try { d.SelectedPath = Path.GetDirectoryName(installDir); } catch { }
                if (d.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(d.SelectedPath))
                {
                    string p = d.SelectedPath.TrimEnd('\\', '/');
                    installDir = string.Equals(Path.GetFileName(p), K.App, StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, K.App);
                        notice = null;
                    Invalidate();
                }
            }
        }

        void Launch()
        {
            try
            {
                string exe = Path.Combine(installDir, K.ExeName);
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir, UseShellExecute = true });
            }
            catch { }
        }

        // ================= install =================
        void Report(double p, string status, string detail)
        {
            lock (lk) { wProgress = Math.Max(wProgress, p); if (status != null) wStatus = status; if (detail != null) wDetail = detail; }
        }

        void Ui(Action a) { try { BeginInvoke(a); } catch { } }

        void StartInstall()
        {
            notice = null; errorMsg = null; cancel = false;
            SetState(St.Working);
            worker = new Thread(InstallWork) { IsBackground = true, Name = "install" };
            worker.Start();
        }

        void InstallWork()
        {
            string dest = installDir;
            bool extracting = false;
            try
            {
                string parent = Path.GetDirectoryName(dest);
                Directory.CreateDirectory(parent);
                Report(0.01, "Getting ready", "");
                KillApp();
                string exe = Path.Combine(dest, K.ExeName);

                // Everything the app needs travels inside this Setup file, so nothing is downloaded.
                if (!LooksLikeOurs(dest))
                    throw new UserError("The folder \"" + dest + "\" already has other files in it. Click Close, run Setup again and choose a different place to install.");
                try
                {
                    var di = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(parent)));
                    if (di.IsReady && di.AvailableFreeSpace < 350L * 1024 * 1024)
                        throw new UserError("There isn't enough free space on drive " + di.Name.TrimEnd('\\') + ". Inspired Moodboard needs about 350 MB free.");
                }
                catch (UserError) { throw; }
                catch { }

                Program.Log("install to " + dest);
                Report(0.02, "Checking the installer", "Making sure nothing is damaged");
                VerifyPayload(0.02, 0.10);
                if (cancel) throw new OperationCanceledException();

                Program.Log("payload ok");
                Report(0.10, "Installing Inspired Moodboard", "");
                if (Directory.Exists(dest)) DeleteDir(dest);
                extracting = true;
                ExtractPayload(dest, 0.10, 0.94);
                extracting = false;
                try { foreach (var f in Directory.GetFiles(parent, K.App + "-engine-*")) TryDelete(f); } catch { }   // leftovers from older versions

                Program.Log("extracted");
                Report(0.95, "Installing Inspired Moodboard", "");
                string uninst = Path.Combine(dest, K.UninstName);
                WriteRes("uninstaller.exe", uninst);

                Report(0.97, "Adding shortcuts", "");
                string startLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), K.App + ".lnk");
                string deskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), K.App + ".lnk");
                Shortcut(startLnk, exe, dest);
                Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall " + K.App + ".lnk"), uninst, dest);
                if (optDesktop) Shortcut(deskLnk, exe, dest); else TryDelete(deskLnk);

                Report(0.99, "Finishing up", "");
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(K.UninstKey))
                    {
                        k.SetValue("DisplayName", K.App);
                        k.SetValue("DisplayVersion", K.Version);
                        k.SetValue("Publisher", "Inspired");
                        k.SetValue("DisplayIcon", Path.Combine(dest, "icon.ico"));
                        k.SetValue("InstallLocation", dest);
                        k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                        k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, DirSize(dest) / 1024), RegistryValueKind.DWord);
                    }
                }
                catch { }

                Report(1.0, "Done", "");
                Program.Log("install done");
                Thread.Sleep(450);
                Ui(() =>
                {
                    existing = true;
                    SetState(St.Done);
                    if (optOpen) Launch();
                    Console.WriteLine("RESULT done " + dest);
                    if (Environment.GetEnvironmentVariable("IM_AUTO") != null) { ExitCode = 0; Close(); }
                });
            }
            catch (Exception ex)
            {
                Program.Log("install failed: " + ex);
                if (extracting) { try { DeleteDir(dest); } catch { } }
                bool cancelled = ex is OperationCanceledException;
                Ui(() =>
                {
                    if (cancelled)
                    {
                        if (closeAfterCancel) { state = St.Welcome; Close(); return; }
                        notice = "Install cancelled. Nothing was changed."; SetState(St.Welcome);
                    }
                    else { errorMsg = Friendly(ex); SetState(St.Error); }
                    Console.WriteLine("RESULT error " + ex);
                    if (Environment.GetEnvironmentVariable("IM_AUTO") != null) { ExitCode = 1; state = St.Error; Close(); }
                });
            }
        }

        static string Friendly(Exception ex)
        {
            if (ex is UserError) return ex.Message;
            if (ex is InvalidDataException) return "This Setup file is damaged, probably from an incomplete download or copy. Get a fresh copy of the file and run it again.";
            if (ex is UnauthorizedAccessException) return "Windows wouldn't let Setup write to that folder. Try installing to a folder inside your user account.";
            if (ex is IOException && ex.Message.IndexOf("being used", StringComparison.OrdinalIgnoreCase) >= 0)
                return "A file is still in use. Close Inspired Moodboard if it's open, then click Try again.";
            return ex.Message;
        }

        // Reads the bundled app and checks it against the fingerprint recorded when Setup was built.
        void VerifyPayload(double p0, double p1)
        {
            string want;
            using (var s = Res("payload.sha256")) using (var r = new StreamReader(s)) want = r.ReadToEnd().Trim().ToLowerInvariant();
            using (var s = Res("payload.zip"))
            using (var h = SHA256.Create())
            {
                if (s == null) throw new InvalidDataException("missing payload");
                long total = s.Length, done = 0;
                var buf = new byte[1 << 20]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel) throw new OperationCanceledException();
                    h.TransformBlock(buf, 0, n, null, 0); done += n;
                    Report(p0 + (p1 - p0) * done / Math.Max(1, total), null, null);
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                string got = BitConverter.ToString(h.Hash).Replace("-", "").ToLowerInvariant();
                if (got != want) throw new InvalidDataException("payload hash mismatch");
            }
        }

        void ExtractPayload(string dest, double p0, double p1)
        {
            Directory.CreateDirectory(dest);
            string root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var s = Res("payload.zip"))
            using (var z = new ZipArchive(s, ZipArchiveMode.Read))
            {
                long total = 0, done = 0;
                foreach (var e in z.Entries) total += e.Length;
                var buf = new byte[1 << 18];
                foreach (var e in z.Entries)
                {
                    if (cancel) throw new OperationCanceledException();
                    string rel = e.FullName.Replace('\\', '/');
                    string target = Path.GetFullPath(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = e.Open())
                    using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cancel) throw new OperationCanceledException();
                            dst.Write(buf, 0, n); done += n;
                            Report(p0 + (p1 - p0) * done / Math.Max(1, total), "Installing Inspired Moodboard",
                                string.Format("{0:0} of {1:0} MB", done / 1048576.0, total / 1048576.0));
                        }
                    }
                    if (new FileInfo(target).Length != e.Length) throw new InvalidDataException("short file");
                }
            }
        }

        static string Sha256(string file)
        {
            using (var s = File.OpenRead(file)) using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        static void WriteRes(string name, string to)
        {
            using (var s = Res(name)) using (var f = File.Create(to)) s.CopyTo(f);
        }

        static void Shortcut(string lnk, string target, string dir)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { Path.Combine(dir, "icon.ico") + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { K.App });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc); Marshal.FinalReleaseComObject(sh);
            }
            catch { }
        }

        static void KillApp()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(K.ExeName)))
                {
                    try { p.Kill(); p.WaitForExit(4000); } catch { }
                }
            }
            catch { }
        }

        static void DeleteDir(string d)
        {
            for (int i = 0; ; i++)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); return; }
                catch (Exception) { if (i >= 6) throw; Thread.Sleep(700); }
            }
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        static long DirSize(string d)
        {
            long n = 0;
            try { foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) try { n += new FileInfo(f).Length; } catch { } } catch { }
            return n;
        }

        // ================= uninstall =================
        void StartUninstall()
        {
            errorMsg = null; cancel = false;
            SetState(St.UnWorking);
            worker = new Thread(UninstallWork) { IsBackground = true, Name = "uninstall" };
            worker.Start();
        }

        void UninstallWork()
        {
            try
            {
                string dest = installDir;
                Report(0.08, "Closing Inspired Moodboard", "");
                KillApp();
                Thread.Sleep(250);
                Report(0.25, "Removing shortcuts", "");
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), K.App + ".lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Uninstall " + K.App + ".lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), K.App + ".lnk"));
                Thread.Sleep(200);
                Report(0.45, "Removing the app", "");
                if (Directory.Exists(dest))
                {
                    if (!LooksLikeOurs(dest)) throw new UserError("\"" + dest + "\" doesn't look like an Inspired Moodboard folder, so it was left alone.");
                    DeleteDir(dest);
                }
                Report(0.80, "Removing settings", "");
                try
                {
                    string parent = Path.GetDirectoryName(dest);
                    if (Directory.Exists(parent))
                        foreach (var f in Directory.GetFiles(parent, K.App + "-engine-*")) TryDelete(f);
                }
                catch { }
                string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                foreach (var n in new[] { K.App, "inspired-moodboard" }) { try { DeleteDir(Path.Combine(ad, n)); } catch { } }
                try { Registry.CurrentUser.DeleteSubKeyTree(K.UninstKey, false); } catch { }
                Report(1.0, "Done", "");
                Thread.Sleep(450);
                Ui(() =>
                {
                    SetState(St.UnDone);
                    Console.WriteLine("RESULT removed " + dest);
                    if (Environment.GetEnvironmentVariable("IM_AUTO") != null) Close();
                });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    errorMsg = Friendly(ex); SetState(St.Error);
                    Console.WriteLine("RESULT error " + ex);
                    if (Environment.GetEnvironmentVariable("IM_AUTO") != null) { ExitCode = 1; state = St.Error; Close(); }
                });
            }
        }

        // ================= preview renders (development only) =================
        void TestShot(string spec)
        {
            // IM_SHOT=state:path[:progress]
            var parts = spec.Split('|');
            string which = parts[0], path = parts[1];
            var wait = Stopwatch.StartNew();
            while (!wallReady && wait.ElapsedMilliseconds < 120000) { Application.DoEvents(); Thread.Sleep(30); }
            Console.WriteLine("wall ready after " + wait.ElapsedMilliseconds + " ms, " + tileCount + " tiles of " + folderCount);
            wallReadyAt = -10;
            wallT = parts.Length > 2 ? double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 6;
            switch (which)
            {
                case "welcome": state = St.Welcome; break;
                case "hover": state = St.Welcome; hover = "primary"; break;
                case "working":
                    state = St.Working; pDisp = 0.42; tipIdx = 1; tipStart = clock.Elapsed.TotalSeconds - 3;
                    wStatus = "Installing Inspired Moodboard"; wDetail = "118 of 282 MB"; break;
                case "done": state = St.Done; optOpen = false; break;
                case "error": state = St.Error; errorMsg = "Setup couldn't reach the download server. Check your internet connection, then click Try again."; break;
                case "unconfirm": state = St.UnConfirm; break;
                case "undone": state = St.UnDone; break;
            }
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                using (var g = Graphics.FromImage(bmp)) Render(g, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            Close();
        }
    }
}
