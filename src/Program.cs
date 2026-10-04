using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("N.AIM Benchmark Assistant")]
[assembly: AssemblyProduct("N.AIM Benchmark Assistant")]
[assembly: AssemblyDescription("N.AIM Benchmark Assistant")]
[assembly: AssemblyCompany("N.AIM")]
[assembly: AssemblyVersion(NAimBenchmarkAssistant.Meta.Version + ".0")]
[assembly: AssemblyFileVersion(NAimBenchmarkAssistant.Meta.Version + ".0")]

namespace NAimBenchmarkAssistant
{
    static class Meta
    {
        public const string Version = "1.1.0";                       // bump this for a release
        public const string Name = "N.AIM Benchmark Assistant";
        public const string Repo = "iwasmadeforhell/naim-benchmark-assistant";   // releases of this repo are the update source
        public const string SetupAsset = "NAIM-Benchmark-Assistant-Setup.exe";
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new System.Threading.Mutex(true, "NAIM.BenchmarkAssistant", out created))
            {
                if (!created) return;
                MainForm.MigrateOldData();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }

    // Minimal read-only SQLite access through winsqlite3.dll (ships with Windows 10/11).
    static class Sqlite
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern double sqlite3_column_double(IntPtr stmt, int col);

        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }
        static string Text(IntPtr st, int col)
        {
            IntPtr p = sqlite3_column_text(st, col);
            if (p == IntPtr.Zero) return null;
            int n = sqlite3_column_bytes(st, col);
            byte[] b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        // Runs a query and returns each row as string[] (null for NULL). Throws on failure.
        public static List<string[]> Query(string file, string sql, int cols)
        {
            IntPtr db;
            if (sqlite3_open_v2(Z(file), out db, 1 /*READONLY*/, IntPtr.Zero) != 0) { if (db != IntPtr.Zero) sqlite3_close(db); throw new Exception("cannot open database"); }
            try
            {
                sqlite3_busy_timeout(db, 800);
                IntPtr st;
                if (sqlite3_prepare_v2(db, Z(sql), -1, out st, IntPtr.Zero) != 0) throw new Exception("bad query");
                var rows = new List<string[]>();
                try
                {
                    int rc;
                    while ((rc = sqlite3_step(st)) == 100 /*ROW*/)
                    {
                        var r = new string[cols];
                        for (int i = 0; i < cols; i++) r[i] = Text(st, i);
                        rows.Add(r);
                    }
                    if (rc != 101 /*DONE*/) throw new Exception("query failed (" + rc + ")");
                }
                finally { sqlite3_finalize(st); }
                return rows;
            }
            finally { sqlite3_close(db); }
        }
    }

    class MainForm : Form
    {
        WebView2 wv = new WebView2();
        JavaScriptSerializer js = new JavaScriptSerializer();
        Timer watch = new Timer();
        // per game: "kv" / "al"
        Dictionary<string, HashSet<string>> known = new Dictionary<string, HashSet<string>>();
        Dictionary<string, bool> ready = new Dictionary<string, bool>();
        Dictionary<string, Dictionary<string, object>> parsedKv = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> alNames = new Dictionary<string, string>();       // taskId -> full name
        HashSet<string> alScanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string alLastSig = "";
        const string AppVersion = Meta.Version;
        const string UpdateRepo = Meta.Repo;
        static readonly string AppDir = AppDomain.CurrentDomain.BaseDirectory;
        // NAIM_DATA / NAIM_STATS exist for testing and screenshots (keep demo data away from the real files)
        static readonly string LocalApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string DataDir = Environment.GetEnvironmentVariable("NAIM_DATA") ?? Path.Combine(LocalApp, Meta.Name);
        // earlier versions kept their files in %LOCALAPPDATA%\SensSwitcher: copy them over once
        public static void MigrateOldData()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("NAIM_DATA") != null) return;
                string old = Path.Combine(LocalApp, "SensSwitcher");
                if (!Directory.Exists(old)) return;
                Directory.CreateDirectory(DataDir);
                foreach (string f in new[] { "tracker.json", "bench-index.json", "tracker-paths.json" })
                {
                    string src = Path.Combine(old, f), dst = Path.Combine(DataDir, f);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
            }
            catch { }
        }
        static readonly string DataFile = Path.Combine(DataDir, "tracker.json");
        static readonly string CfgFile = Path.Combine(DataDir, "tracker-paths.json");

        public MainForm()
        {
            js.MaxJsonLength = int.MaxValue;
            known["kv"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            known["al"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ready["kv"] = false; ready["al"] = false;
            Text = Meta.Name;
            FormBorderStyle = FormBorderStyle.None;      // the page draws its own title bar and buttons
            Padding = new Padding(4);                    // thin strip around the page that carries the resize edges
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(960, 820);
            MinimumSize = new Size(640, 480);
            BackColor = Color.FromArgb(14, 16, 21);
            StartPosition = FormStartPosition.CenterScreen;
            wv.Dock = DockStyle.Fill;
            wv.DefaultBackgroundColor = Color.FromArgb(14, 16, 21);
            Controls.Add(wv);
            Load += OnLoadAsync;
            watch.Interval = 2500;
            watch.Tick += delegate { Watch(); };
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1;
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x20000 | 0x10000; // WS_MINIMIZEBOX (taskbar click minimises) + WS_MAXIMIZEBOX (double-click / snap)
                cp.ClassStyle |= 0x20000;     // CS_DROPSHADOW
                return cp;
            }
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT && WindowState == FormWindowState.Normal)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = 10; bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
                if (t && l) m.Result = (IntPtr)HTTOPLEFT; else if (t && r) m.Result = (IntPtr)HTTOPRIGHT;
                else if (b && l) m.Result = (IntPtr)HTBOTTOMLEFT; else if (b && r) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (l) m.Result = (IntPtr)HTLEFT; else if (r) m.Result = (IntPtr)HTRIGHT;
                else if (t) m.Result = (IntPtr)HTTOP; else if (b) m.Result = (IntPtr)HTBOTTOM;
            }
        }
        // a borderless window would otherwise maximise over the taskbar
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            try { var sc = Screen.FromControl(this); var wa = sc.WorkingArea; MaximizedBounds = new Rectangle(wa.X - sc.Bounds.X, wa.Y - sc.Bounds.Y, wa.Width, wa.Height); } catch { }
        }
        void WindowCommand(string a)
        {
            if (a == "min") WindowState = FormWindowState.Minimized;
            else if (a == "max")
            {
                if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
                else { MaximizedBounds = Screen.FromControl(this).WorkingArea; WindowState = FormWindowState.Maximized; }
            }
            else if (a == "close") Close();
            else if (a == "drag" && WindowState == FormWindowState.Normal) { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); }
        }

        async void OnLoadAsync(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "web"));
                await wv.EnsureCoreWebView2Async(env);
                var c = wv.CoreWebView2;
                c.Settings.AreDevToolsEnabled = false;
                c.Settings.AreDefaultContextMenusEnabled = false;
                c.Settings.IsStatusBarEnabled = false;
                c.SetVirtualHostNameToFolderMapping("tracker.local", AppDir, CoreWebView2HostResourceAccessKind.Allow);
                c.WebMessageReceived += OnMessage;
                c.Navigate("https://tracker.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start the WebView2 runtime:\n" + ex.Message, Meta.Name);
                Close();
            }
        }

        // ================= shared =================
        Dictionary<string, object> LoadCfg()
        {
            try { return js.DeserializeObject(File.ReadAllText(CfgFile)) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
        string SteamPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k != null) { string p = k.GetValue("SteamPath") as string; if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\'); }
            }
            catch { }
            return @"C:\Program Files (x86)\Steam";
        }
        List<string> SteamLibs()
        {
            var libs = new List<string>();
            string sp = SteamPath();
            libs.Add(sp);
            try
            {
                string t = File.ReadAllText(Path.Combine(sp, "steamapps", "libraryfolders.vdf"));
                foreach (Match m in Regex.Matches(t, "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
            return libs;
        }
        static bool ProcRunning(string part)
        {
            foreach (Process p in Process.GetProcesses())
                if (p.ProcessName.ToLowerInvariant().Contains(part)) return true;
            return false;
        }
        static string Field(string body, string key)
        {
            Match m = Regex.Match(body, "\"" + key + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        static long UnixMs(DateTime utc) { return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(); }

        // ================= KovaaK's =================
        string KvRoot()
        {
            foreach (string l in SteamLibs())
            {
                string p = Path.Combine(l, "steamapps", "common", "FPSAimTrainer", "FPSAimTrainer");
                if (Directory.Exists(p)) return p;
            }
            return null;
        }
        string StatsDir()
        {
            string envStats = Environment.GetEnvironmentVariable("NAIM_STATS");
            if (!string.IsNullOrEmpty(envStats) && Directory.Exists(envStats)) return envStats;
            var c = LoadCfg();
            object v;
            if (c.TryGetValue("stats", out v) && v is string && Directory.Exists((string)v)) return (string)v;
            string r = KvRoot();
            if (r == null) return null;
            string s = Path.Combine(r, "stats");
            return Directory.Exists(s) ? s : null;
        }
        static readonly Regex FileTime = new Regex(@"(\d{4})\.(\d\d)\.(\d\d)-(\d\d)\.(\d\d)\.(\d\d) Stats\.csv$");
        Dictionary<string, object> ParseKv(string path)
        {
            string name = Path.GetFileName(path);
            Dictionary<string, object> cached;
            if (parsedKv.TryGetValue(name, out cached)) return cached;
            string scen = null; double score = double.NaN;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (scen == null && line.StartsWith("Scenario:,")) scen = line.Substring(10).Trim();
                    else if (double.IsNaN(score) && line.StartsWith("Score:,"))
                        double.TryParse(line.Substring(7).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out score);
                }
            }
            if (scen == null || double.IsNaN(score)) return null;
            long t = new DateTimeOffset(File.GetLastWriteTime(path)).ToUnixTimeMilliseconds();
            Match m = FileTime.Match(name);
            if (m.Success)
            {
                try
                {
                    var dt = new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                        int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value), DateTimeKind.Local);
                    t = new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                }
                catch { }
            }
            var run = new Dictionary<string, object> { { "f", name }, { "n", scen }, { "s", score }, { "t", t } };
            parsedKv[name] = run;
            return run;
        }
        List<Dictionary<string, object>> KvRuns()
        {
            var res = new List<Dictionary<string, object>>();
            string dir = StatsDir();
            if (dir == null) return res;
            foreach (string f in Directory.GetFiles(dir, "*Stats.csv"))
                try { var r = ParseKv(f); if (r != null) res.Add(r); } catch { }
            res.Sort(delegate (Dictionary<string, object> a, Dictionary<string, object> b) { return ((long)a["t"]).CompareTo((long)b["t"]); });
            return res;
        }

        // ================= Aimlabs =================
        string AlRoot()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Statespace", "aimlab_tb");
            return Directory.Exists(p) ? p : null;
        }
        // the user folder with the most recently written database
        string AlUserDir()
        {
            string root = AlRoot();
            if (root == null) return null;
            string best = null; DateTime bt = DateTime.MinValue;
            try
            {
                foreach (string d in Directory.GetDirectories(Path.Combine(root, "Users")))
                {
                    if (Path.GetFileName(d) == "Default") continue;
                    string db = Path.Combine(d, "LocalDB", "Klutch.bytes");
                    if (!File.Exists(db)) db = Path.Combine(d, "LocalDB", "klutch.bytes");
                    if (File.Exists(db)) { DateTime w = File.GetLastWriteTime(db); if (w > bt) { bt = w; best = d; } }
                }
            }
            catch { }
            return best;
        }
        string AlDb()
        {
            string d = AlUserDir();
            if (d == null) return null;
            string db = Path.Combine(d, "LocalDB", "Klutch.bytes");
            return File.Exists(db) ? db : Path.Combine(d, "LocalDB", "klutch.bytes");
        }
        static readonly Regex RxReplayName = new Regex("\"taskId\"\\s*:\\s*\"([^\"]*)\"\\s*,\\s*\"taskName\"\\s*:\\s*\"([^\"]*)\"");
        static readonly Regex RxNotifName = new Regex("\"Task\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"LevelId\"\\s*:\\s*\"([^\"]+)\"");
        static readonly Regex RxContentName = new Regex("\"id\"\\s*:\\s*\"(CsLevel[^\"]+)\"\\s*,\\s*\"label\"\\s*:\\s*\"([^\"]*)\"");
        static readonly Regex RxLevel = new Regex(@"^CsLevel\.[^.]*\.(.*)\.([A-Z0-9]{4,8})$");
        // learn full scenario names from replay files and notifications
        void AlLearnNames()
        {
            string ud = AlUserDir();
            if (ud == null) return;
            try
            {
                string rd = Path.Combine(ud, "Replay");
                if (Directory.Exists(rd))
                    foreach (string f in Directory.GetFiles(rd, "*.json"))
                    {
                        if (!alScanned.Add(f)) continue;
                        try
                        {
                            Match m = RxReplayName.Match(File.ReadAllText(f));
                            if (m.Success) alNames[m.Groups[1].Value] = m.Groups[2].Value;
                        }
                        catch { alScanned.Remove(f); }
                    }
                string cs = Path.Combine(AlRoot(), "CreatorStudio", "content.es3");
                if (File.Exists(cs) && alScanned.Add(cs + File.GetLastWriteTime(cs).Ticks))
                    foreach (Match m in RxContentName.Matches(File.ReadAllText(cs)))
                        if (!alNames.ContainsKey(m.Groups[1].Value)) alNames[m.Groups[1].Value] = m.Groups[2].Value;
                string nf = Path.Combine(ud, "Notifications.json");
                if (File.Exists(nf) && alScanned.Add(nf + File.GetLastWriteTime(nf).Ticks))
                    foreach (Match m in RxNotifName.Matches(File.ReadAllText(nf)))
                        if (!alNames.ContainsKey(m.Groups[2].Value)) alNames[m.Groups[2].Value] = m.Groups[1].Value;
            }
            catch { }
        }
        string AlName(string id, out bool resolved)
        {
            string n;
            if (alNames.TryGetValue(id, out n)) { resolved = true; return n; }
            resolved = false;
            Match m = RxLevel.Match(id);
            return m.Success ? m.Groups[1].Value + "\u2026 [" + m.Groups[2].Value + "]" : id;
        }
        List<Dictionary<string, object>> AlRuns()
        {
            var res = new List<Dictionary<string, object>>();
            string db = AlDb();
            if (db == null) return res;
            AlLearnNames();
            var rows = Sqlite.Query(db, "select taskId,taskName,score,createDate,playId from TaskData order by taskId", 5);
            foreach (var r in rows)
            {
                if (r[1] == null) continue;
                double score;
                if (!double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out score)) continue;
                DateTime dt;
                long t = DateTime.TryParse(r[3], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt) ? UnixMs(dt) : 0;
                bool ok;
                string name = AlName(r[1], out ok);
                string f = !string.IsNullOrEmpty(r[4]) ? r[4] : "al-" + r[0];
                res.Add(new Dictionary<string, object> { { "f", f }, { "k", r[1] }, { "n", name }, { "u", !ok }, { "s", score }, { "t", t } });
            }
            return res;
        }

        // ================= watching for new runs =================
        void Watch()
        {
            if (wv.CoreWebView2 == null) return;
            if (ready["kv"])
            {
                string dir = StatsDir();
                if (dir != null)
                    try
                    {
                        foreach (string f in Directory.GetFiles(dir, "*Stats.csv"))
                        {
                            string n = Path.GetFileName(f);
                            if (known["kv"].Contains(n)) continue;
                            Dictionary<string, object> run = null;
                            try { run = ParseKv(f); } catch { }
                            if (run == null) continue;            // still being written - retry next tick
                            known["kv"].Add(n);
                            Push(new Dictionary<string, object> { { "event", "newRun" }, { "game", "kv" }, { "run", run } });
                        }
                    }
                    catch { }
            }
            if (ready["al"])
            {
                try
                {
                    string db = AlDb();
                    if (db != null)
                    {
                        var sig = Sqlite.Query(db, "select max(taskId),count(*) from TaskData", 2);
                        string s = sig.Count > 0 ? sig[0][0] + "/" + sig[0][1] : "";
                        if (s != alLastSig)
                        {
                            alLastSig = s;
                            foreach (var run in AlRuns())
                            {
                                string f = (string)run["f"];
                                if (known["al"].Contains(f)) continue;
                                known["al"].Add(f);
                                Push(new Dictionary<string, object> { { "event", "newRun" }, { "game", "al" }, { "run", run } });
                            }
                        }
                    }
                }
                catch { }
            }
        }
        void Push(object o)
        {
            if (wv.CoreWebView2 != null) wv.CoreWebView2.PostWebMessageAsJson(js.Serialize(o));
        }

        // ================= account =================
        Dictionary<string, object> Account()
        {
            string sp = SteamPath();
            string auto = null;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) if (k != null) auto = k.GetValue("AutoLoginUser") as string; } catch { }
            string id = null, persona = null, acct = null;
            try
            {
                string t = File.ReadAllText(Path.Combine(sp, "config", "loginusers.vdf"));
                var blocks = Regex.Matches(t, "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}");
                foreach (Match b in blocks)
                {
                    string body = b.Groups[2].Value;
                    string a = Field(body, "AccountName"), p = Field(body, "PersonaName"), mr = Field(body, "MostRecent");
                    bool pick = (auto != null && string.Equals(a, auto, StringComparison.OrdinalIgnoreCase)) || (id == null && mr == "1");
                    if (pick) { id = b.Groups[1].Value; persona = p; acct = a; if (auto != null && string.Equals(a, auto, StringComparison.OrdinalIgnoreCase)) break; }
                }
                if (id == null && blocks.Count > 0)
                {
                    var b0 = blocks[0]; id = b0.Groups[1].Value; persona = Field(b0.Groups[2].Value, "PersonaName"); acct = Field(b0.Groups[2].Value, "AccountName");
                }
            }
            catch { }
            string alUser = AlUserDir();
            string alSteam = null;
            if (alUser != null)
                try
                {
                    string rd = Path.Combine(alUser, "Replay");
                    string newest = null; DateTime nt = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(rd, "*.json")) { DateTime w = File.GetLastWriteTime(f); if (w > nt) { nt = w; newest = f; } }
                    if (newest != null) { Match m = Regex.Match(File.ReadAllText(newest), "\"steamId\"\\s*:\\s*\"(\\d+)\""); if (m.Success) alSteam = m.Groups[1].Value; }
                }
                catch { }
            return new Dictionary<string, object>
            {
                { "steamId", id }, { "persona", persona }, { "account", acct },
                { "steamRunning", Process.GetProcessesByName("steam").Length > 0 },
                { "kvPath", KvRoot() }, { "statsDir", StatsDir() }, { "kvRunning", ProcRunning("fpsaimtrainer") },
                { "alPath", AlRoot() }, { "alUser", alUser == null ? null : Path.GetFileName(alUser) }, { "alDb", AlDb() },
                { "alSteamId", alSteam }, { "alRunning", ProcRunning("aimlab_tb") }
            };
        }

        // ================= KovaaK's public leaderboard =================
        static string HttpGet(string url)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Timeout = 15000;
            rq.UserAgent = "NAIMBenchmarkAssistant/" + AppVersion;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var rs = (HttpWebResponse)rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
        // Score at "top X %" for each X in tops (percent of players, e.g. 10 = top 10%).
        Dictionary<string, object> Leaderboard(string name, List<double> tops)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            string q = Uri.EscapeDataString(name);
            var s = js.DeserializeObject(HttpGet("https://kovaaks.com/webapp-backend/scenario/popular?page=0&max=20&scenarioNameSearch=" + q)) as Dictionary<string, object>;
            long lbId = 0; string matched = null;
            if (s != null && s.ContainsKey("data"))
                foreach (object o in (IEnumerable)s["data"])
                {
                    var it = o as Dictionary<string, object>;
                    if (it == null) continue;
                    string sn = it["scenarioName"] as string;
                    if (sn != null && string.Equals(sn.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) { lbId = Convert.ToInt64(it["leaderboardId"]); matched = sn; break; }
                }
            if (lbId == 0) throw new Exception("'" + name + "' was not found on the KovaaK's leaderboard (exact name needed)");
            var pages = new Dictionary<int, List<object>>();
            int total = 0;
            Func<int, List<object>> getPage = delegate (int p)
            {
                List<object> cached;
                if (pages.TryGetValue(p, out cached)) return cached;
                var d = js.DeserializeObject(HttpGet("https://kovaaks.com/webapp-backend/leaderboard/scores/global?leaderboardId=" + lbId + "&page=" + p + "&max=100")) as Dictionary<string, object>;
                var list = new List<object>();
                if (d != null && d.ContainsKey("data")) foreach (object o in (IEnumerable)d["data"]) list.Add(o);
                pages[p] = list;
                if (p == 0 && d != null && d.ContainsKey("total")) total = Convert.ToInt32(d["total"]);
                return list;
            };
            getPage(0);
            if (total < 12) throw new Exception("Only " + total + " scores on that leaderboard - too few for percentiles");
            var scores = new List<object>();
            foreach (double top in tops)
            {
                int rank = (int)Math.Ceiling(total * top / 100.0);
                rank = Math.Max(1, Math.Min(total, rank));
                var pg = getPage((rank - 1) / 100);
                int idx = (rank - 1) % 100;
                if (idx >= pg.Count) idx = pg.Count - 1;
                scores.Add(Convert.ToDouble(((Dictionary<string, object>)pg[idx])["score"], CultureInfo.InvariantCulture));
            }
            return new Dictionary<string, object> { { "name", matched }, { "total", total }, { "scores", scores } };
        }

        // ================= updates (GitHub releases) =================
        static void HttpDownload(string url, string path)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Timeout = 30000;
            rq.UserAgent = "NAIMBenchmarkAssistant/" + AppVersion;
            using (var rs = rq.GetResponse())
            using (var src = rs.GetResponseStream())
            using (var dst = File.Create(path))
            {
                var buf = new byte[81920]; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
            }
        }
        object UpdateCheck()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var res = new Dictionary<string, object> { { "current", AppVersion }, { "newer", false } };
            string body;
            try { body = HttpGet("https://api.github.com/repos/" + UpdateRepo + "/releases/latest"); }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) { res["none"] = true; return res; }   // no release published yet
                throw;
            }
            var d = NewJs().DeserializeObject(body) as Dictionary<string, object>;
            string tag = ((string)d["tag_name"]).TrimStart('v', 'V');
            Version latest, cur = new Version(AppVersion);
            if (!Version.TryParse(tag, out latest)) throw new Exception("unrecognised release tag " + tag);
            string url = null, sha = null, shaAsset = null;
            foreach (object o in (IEnumerable)d["assets"])
            {
                var a = (Dictionary<string, object>)o;
                string n = (string)a["name"];
                if (n == Meta.SetupAsset)
                {
                    url = (string)a["browser_download_url"];
                    object dg;
                    if (a.TryGetValue("digest", out dg) && dg is string && ((string)dg).StartsWith("sha256:")) sha = ((string)dg).Substring(7);
                }
                else if (n == Meta.SetupAsset + ".sha256") shaAsset = (string)a["browser_download_url"];
            }
            if (sha == null && shaAsset != null)
            {
                string t = HttpGet(shaAsset).Trim();
                sha = t.Split(' ', '\t', '\r', '\n')[0];
            }
            res["latest"] = tag; res["newer"] = latest > cur && url != null; res["url"] = url; res["sha"] = sha;
            res["name"] = d.ContainsKey("name") ? d["name"] : tag; res["notes"] = d.ContainsKey("body") ? d["body"] : "";
            return res;
        }
        // downloads the installer from this repo's releases, checks its SHA-256, runs it (it replaces this app and restarts it)
        object UpdateInstall(string url, string sha)
        {
            if (url == null || !url.StartsWith("https://github.com/" + UpdateRepo + "/releases/download/")) throw new Exception("refusing to download from an unexpected address");
            if (string.IsNullOrEmpty(sha)) throw new Exception("the release has no checksum, so it was not installed");
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            string tmp = Path.Combine(Path.GetTempPath(), "NAIM-Benchmark-Assistant-Setup-update.exe");
            HttpDownload(url, tmp);
            string got;
            using (var sh = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(tmp))
                got = BitConverter.ToString(sh.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (got != sha.Trim().ToLowerInvariant()) { try { File.Delete(tmp); } catch { } throw new Exception("the download did not match its checksum, so it was not installed"); }
            Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
            return new Dictionary<string, object> { { "ok", true } };
        }

        // ================= benchmarks (evxl.app list + KovaaK's boundaries) =================
        Dictionary<string, List<Dictionary<string, object>>> benchIdx = null;
        long benchBuilt = 0;
        string BenchFile { get { return Path.Combine(DataDir, "bench-index.json"); } }
        static JavaScriptSerializer NewJs() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

        // the benchmark list that evxl.app ships inside its own site code: name + difficulty ids
        List<object[]> EvxlList()
        {
            string html = HttpGet("https://evxl.app/");
            var seen = new HashSet<string>();
            foreach (Match m in Regex.Matches(html, @"_app/immutable/[A-Za-z0-9_./-]+\.js"))
            {
                if (!seen.Add(m.Value)) continue;
                string code;
                try { code = HttpGet("https://evxl.app/" + m.Value); } catch { continue; }
                if (code.IndexOf("\"benchmarkName\"") < 0) continue;
                Match jm = Regex.Match(code, @"JSON\.parse\(`(\[.*?\])`\)", RegexOptions.Singleline);
                if (!jm.Success) continue;
                string raw = jm.Groups[1].Value;
                object parsed;
                try { parsed = NewJs().DeserializeObject(raw); } catch { parsed = NewJs().DeserializeObject(raw.Replace("\\\\", "\\")); }
                var jobs = new List<object[]>();
                foreach (object o in (IEnumerable)parsed)
                {
                    var b = o as Dictionary<string, object>;
                    if (b == null || !b.ContainsKey("difficulties")) continue;
                    foreach (object od in (IEnumerable)b["difficulties"])
                    {
                        var d = od as Dictionary<string, object>;
                        if (d == null || !d.ContainsKey("kovaaksBenchmarkId")) continue;
                        jobs.Add(new object[] { (string)b["benchmarkName"], (string)d["difficultyName"], Convert.ToInt64(d["kovaaksBenchmarkId"]) });
                    }
                }
                if (jobs.Count > 0) return jobs;
            }
            throw new Exception("could not read the benchmark list from evxl.app");
        }

        void LoadBench()
        {
            if (benchIdx != null || !File.Exists(BenchFile)) return;
            try
            {
                var d = NewJs().DeserializeObject(File.ReadAllText(BenchFile)) as Dictionary<string, object>;
                var idx = new Dictionary<string, List<Dictionary<string, object>>>();
                foreach (object o in (IEnumerable)d["entries"])
                {
                    var e = (Dictionary<string, object>)o;
                    string key = ((string)e["s"]).ToLowerInvariant();
                    List<Dictionary<string, object>> l;
                    if (!idx.TryGetValue(key, out l)) { l = new List<Dictionary<string, object>>(); idx[key] = l; }
                    l.Add(e);
                }
                benchIdx = idx; benchBuilt = Convert.ToInt64(d["builtAt"]);
            }
            catch { }
        }

        object BenchBuild()
        {
            string steam = Account()["steamId"] as string;
            if (string.IsNullOrEmpty(steam)) throw new Exception("Steam account not detected (needed to query KovaaK's)");
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = 12;
            var jobs = EvxlList();
            var entries = new List<Dictionary<string, object>>();
            object gate = new object();
            int ok = 0, fail = 0;
            System.Threading.Tasks.Parallel.ForEach(jobs, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, delegate (object[] j)
            {
                try
                {
                    string body = HttpGet("https://kovaaks.com/webapp-backend/benchmarks/player-progress-rank-benchmark?benchmarkId=" + j[2] + "&steamId=" + steam + "&page=0&max=100");
                    var d = NewJs().DeserializeObject(body) as Dictionary<string, object>;
                    var names = new List<object>();
                    bool first = true;
                    foreach (object r in (IEnumerable)d["ranks"])
                    {
                        if (first) { first = false; continue; }          // "No Rank"
                        names.Add(((Dictionary<string, object>)r)["name"]);
                    }
                    var local = new List<Dictionary<string, object>>();
                    var cats = d["categories"] as Dictionary<string, object>;
                    foreach (var cp in cats)
                    {
                        var cd = cp.Value as Dictionary<string, object>;
                        var scs = cd != null && cd.ContainsKey("scenarios") ? cd["scenarios"] as Dictionary<string, object> : null;
                        if (scs == null) continue;
                        foreach (var sp in scs)
                        {
                            var sd = sp.Value as Dictionary<string, object>;
                            if (sd == null || !sd.ContainsKey("rank_maxes")) continue;
                            var maxes = new List<object>();
                            foreach (object m in (IEnumerable)sd["rank_maxes"]) maxes.Add(Convert.ToDouble(m, CultureInfo.InvariantCulture));
                            if (maxes.Count == 0) continue;
                            var nm = new List<object>(names);
                            if (nm.Count > maxes.Count) nm = nm.GetRange(nm.Count - maxes.Count, maxes.Count);
                            local.Add(new Dictionary<string, object> { { "s", sp.Key }, { "b", (string)j[0] }, { "d", (string)j[1] }, { "id", (long)j[2] }, { "cat", cp.Key }, { "ranks", nm }, { "maxes", maxes } });
                        }
                    }
                    lock (gate) { entries.AddRange(local); ok++; }
                }
                catch { lock (gate) fail++; }
            });
            if (entries.Count == 0) throw new Exception("no benchmark data could be downloaded");
            long now = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(BenchFile, NewJs().Serialize(new Dictionary<string, object> { { "builtAt", now }, { "entries", entries } }), new UTF8Encoding(false));
            benchIdx = null; LoadBench();
            return new Dictionary<string, object> { { "scenarios", entries.Count }, { "benchmarks", ok }, { "failed", fail } };
        }

        object BenchFind(string name)
        {
            LoadBench();
            if (benchIdx == null) return new Dictionary<string, object> { { "built", false } };
            List<Dictionary<string, object>> found;
            var matches = new List<object>();
            if (benchIdx.TryGetValue(name.Trim().ToLowerInvariant(), out found))
            {
                foreach (var m in found) matches.Add(m);
            }
            return new Dictionary<string, object> { { "built", true }, { "builtAt", benchBuilt }, { "matches", matches } };
        }

        // KovaaK's own scenario type (Clicking / Tracking / Target Switching / Other) per scenario name
        Dictionary<string, object> CategoryLookup(List<string> names)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            var res = new Dictionary<string, object>();
            foreach (string name in names)
            {
                try
                {
                    var s = js.DeserializeObject(HttpGet("https://kovaaks.com/webapp-backend/scenario/popular?page=0&max=20&scenarioNameSearch=" + Uri.EscapeDataString(name))) as Dictionary<string, object>;
                    string type = "";
                    if (s != null && s.ContainsKey("data"))
                        foreach (object o in (IEnumerable)s["data"])
                        {
                            var it = o as Dictionary<string, object>;
                            if (it == null) continue;
                            string sn = it["scenarioName"] as string;
                            if (sn != null && string.Equals(sn.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                            {
                                var sc = it["scenario"] as Dictionary<string, object>;
                                if (sc != null && sc.ContainsKey("aimType") && sc["aimType"] != null) type = sc["aimType"].ToString();
                                break;
                            }
                        }
                    res[name] = type;
                }
                catch { }   // offline / not found: skip, it is retried next time
            }
            return res;
        }

        void Reply(object id, object result)
        {
            var reply = new Dictionary<string, object> { { "id", id }, { "result", result } };
            if (wv.CoreWebView2 != null) wv.CoreWebView2.PostWebMessageAsJson(js.Serialize(reply));
        }

        // ================= messages =================
        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var msg = js.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>;
            object result = null;
            try
            {
                string cmd = (string)msg["cmd"];
                object data = msg.ContainsKey("data") ? msg["data"] : null;
                if (cmd == "version") { Reply(msg["id"], AppVersion); return; }
                if (cmd == "win") { string wa = (string)data; BeginInvoke(new Action(delegate { WindowCommand(wa); })); return; }   // after this callback returns, so a window-move loop never runs inside it
                if (cmd == "updateCheck" || cmd == "updateInstall")
                {
                    object uid = msg["id"];
                    var ud = data as Dictionary<string, object>;
                    string uurl = ud != null && ud.ContainsKey("url") ? ud["url"] as string : null, usha = ud != null && ud.ContainsKey("sha") ? ud["sha"] as string : null;
                    bool install = cmd == "updateInstall";
                    System.Threading.Tasks.Task.Factory.StartNew(delegate
                    {
                        object res;
                        try { res = install ? UpdateInstall(uurl, usha) : UpdateCheck(); }
                        catch (Exception ex) { res = new Dictionary<string, object> { { "error", ex.Message } }; }
                        BeginInvoke(new Action(delegate
                        {
                            Reply(uid, res);
                            var rd = res as Dictionary<string, object>;
                            if (install && rd != null && rd.ContainsKey("ok")) { var t = new Timer { Interval = 800 }; t.Tick += delegate { Application.Exit(); }; t.Start(); }
                        }));
                    });
                    return;
                }
                if (cmd == "benchFind")
                {
                    Reply(msg["id"], BenchFind((string)((Dictionary<string, object>)data)["name"]));
                    return;
                }
                if (cmd == "benchBuild")
                {
                    object bid = msg["id"];
                    System.Threading.Tasks.Task.Factory.StartNew(delegate
                    {
                        object res;
                        try { res = BenchBuild(); }
                        catch (Exception ex) { res = new Dictionary<string, object> { { "error", ex.Message } }; }
                        BeginInvoke(new Action(delegate { Reply(bid, res); }));
                    });
                    return;
                }
                if (cmd == "category")
                {
                    var cnames = new List<string>();
                    foreach (object o in (IEnumerable)((Dictionary<string, object>)data)["names"]) cnames.Add((string)o);
                    object cid = msg["id"];
                    System.Threading.Tasks.Task.Factory.StartNew(delegate
                    {
                        object res;
                        try { res = CategoryLookup(cnames); }
                        catch (Exception ex) { res = new Dictionary<string, object> { { "error", ex.Message } }; }
                        BeginInvoke(new Action(delegate { Reply(cid, res); }));
                    });
                    return;
                }
                if (cmd == "leaderboard")
                {
                    var args = (Dictionary<string, object>)data;
                    string lname = (string)args["name"];
                    var tops = new List<double>();
                    foreach (object o in (IEnumerable)args["tops"]) tops.Add(Convert.ToDouble(o, CultureInfo.InvariantCulture));
                    object mid = msg["id"];
                    System.Threading.Tasks.Task.Factory.StartNew(delegate
                    {
                        object res;
                        try { res = Leaderboard(lname, tops); }
                        catch (Exception ex) { res = new Dictionary<string, object> { { "error", ex.Message } }; }
                        BeginInvoke(new Action(delegate { Reply(mid, res); }));
                    });
                    return;
                }
                switch (cmd)
                {
                    case "load":
                        result = File.Exists(DataFile) ? File.ReadAllText(DataFile) : "";
                        break;
                    case "save":
                        Directory.CreateDirectory(DataDir);
                        File.WriteAllText(DataFile, (string)data, new UTF8Encoding(false));
                        result = true;
                        break;
                    case "runs":      // all runs for a game; also snapshots what is already known
                        string game = (string)data;
                        List<Dictionary<string, object>> all;
                        if (game == "al")
                        {
                            try { all = AlRuns(); } catch { all = new List<Dictionary<string, object>>(); }
                            try { var sg = Sqlite.Query(AlDb(), "select max(taskId),count(*) from TaskData", 2); if (sg.Count > 0) alLastSig = sg[0][0] + "/" + sg[0][1]; } catch { }
                        }
                        else all = KvRuns();
                        foreach (var r in all) known[game].Add((string)r["f"]);
                        ready[game] = true; watch.Start();
                        result = all;
                        break;
                    case "account":
                        result = Account();
                        break;
                    case "status":
                        result = new Dictionary<string, object> { { "kvRunning", ProcRunning("fpsaimtrainer") }, { "alRunning", ProcRunning("aimlab_tb") } };
                        break;
                    case "launch":
                        {
                            var la = (Dictionary<string, object>)data;
                            string lg = (string)la["game"], ln = (string)la["name"];
                            if (lg == "kv")
                            {
                                // documented KovaaK's link: opens straight into the scenario
                                // same link KovaaK's itself uses for its "Play this scenario" button (needs the mode part)
                                string link = "steam://run/824270//?action=jump-to-scenario&name=" + Uri.EscapeDataString(ln) + "&mode=challenge";
                                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                                result = new Dictionary<string, object> { { "ok", true }, { "msg", (ProcRunning("fpsaimtrainer-win64") ? "Opening \"" : "Starting KovaaK's, then opening \"") + ln + "\"..." } };
                            }
                            else
                            {
                                // Aimlabs has no link that opens a workshop task: start the game and put the name on the clipboard
                                bool was = ProcRunning("aimlab_tb");
                                try { Clipboard.SetText(ln); } catch { }
                                if (!was) Process.Start(new ProcessStartInfo("steam://rungameid/714010") { UseShellExecute = true });
                                result = new Dictionary<string, object> { { "ok", true }, { "msg", (was ? "Aimlabs is already running" : "Starting Aimlabs") + " - scenario name copied, search for it in-game" } };
                            }
                        }
                        break;
                    case "playlist":
                        {
                            var pa = (Dictionary<string, object>)data;
                            string pg = (string)pa["game"], pn = (string)pa["name"];
                            var names = new List<string>();
                            foreach (object o in (IEnumerable)pa["scenarios"]) names.Add((string)o);
                            if (pg == "kv")
                            {
                                string root = KvRoot();
                                if (root == null) throw new Exception("KovaaK's folder not found");
                                string dir = Path.Combine(root, "Saved", "SaveGames", "Playlists");
                                Directory.CreateDirectory(dir);
                                var list = new List<object>();
                                foreach (string n in names) list.Add(new Dictionary<string, object> { { "scenario_name", n }, { "play_Count", 1 } });
                                var pl = new Dictionary<string, object>
                                {
                                    { "playlistName", pn }, { "playlistId", 0 }, { "authorSteamId", "" }, { "authorName", "" },
                                    { "scenarioList", list }, { "description", "Created by N.AIM Benchmark Assistant" },
                                    { "hasOfflineScenarios", true }, { "hasEdited", true }, { "shareCode", "" },
                                    { "version", 31 }, { "updated", 0 }, { "isPrivate", false }
                                };
                                File.WriteAllText(Path.Combine(dir, pn + ".json"), js.Serialize(pl), new UTF8Encoding(false));
                                bool running = ProcRunning("fpsaimtrainer-win64");
                                if (!running) Process.Start(new ProcessStartInfo("steam://rungameid/824270") { UseShellExecute = true });
                                result = new Dictionary<string, object> { { "ok", true }, { "msg", "Playlist \"" + pn + "\" saved with " + names.Count + " scenarios" + (running ? "" : " - starting KovaaK's") + ". In KovaaK's open Playlists > Local Playlists and press play" } };
                            }
                            else
                            {
                                try { Clipboard.SetText(string.Join("\r\n", names.ToArray())); } catch { }
                                result = new Dictionary<string, object> { { "ok", true }, { "msg", "Aimlabs can't import playlists - the " + names.Count + " scenario names are copied to your clipboard" } };
                            }
                        }
                        break;
                    case "open":
                        string url = (string)data;
                        if (url.StartsWith("https://")) Process.Start(url);
                        result = true;
                        break;
                    case "browseStats":
                        using (var d = new FolderBrowserDialog())
                        {
                            d.Description = "Select the KovaaK's 'stats' folder";
                            if (d.ShowDialog(this) == DialogResult.OK)
                            {
                                var c = LoadCfg(); c["stats"] = d.SelectedPath;
                                File.WriteAllText(CfgFile, js.Serialize(c));
                                parsedKv.Clear(); known["kv"].Clear();
                                result = true;
                            }
                            else result = false;
                        }
                        break;
                }
            }
            catch (Exception ex) { result = new Dictionary<string, object> { { "error", ex.Message } }; }
            var reply = new Dictionary<string, object> { { "id", msg["id"] }, { "result", result } };
            wv.CoreWebView2.PostWebMessageAsJson(js.Serialize(reply));
        }
    }
}
