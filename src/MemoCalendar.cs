// 바탕화면 메모 달력 (가벼운 네이티브 버전). .NET Framework 4.x WinForms, 외부 의존성 없음.
// 메모는 memos.json 형식으로 OneDrive의 memo-calendar 폴더에 저장한다.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MemoCalendar
{
    class Memo
    {
        public string id, date, text, updated;
        public bool task, done;
    }

    static class Store
    {
        public static string Dir, FilePath;
        public static readonly string CfgPath, SettingsPath;
        // OneDrive 폴더를 못 찾아 문서 폴더로 대체했고, 아직 사용자가 저장 폴더를 정하지 않은 상태
        public static bool NeedsFolderPrompt;
        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();
        static Dictionary<string, object> Sett = new Dictionary<string, object>();
        const int KeepBackups = 30;

        static void SetDir(string d) { Dir = d; FilePath = Path.Combine(d, "memos.json"); }
        public static void UseTempDir(string d) { SetDir(d); } // 자체 점검용: 설정 파일에 기록하지 않고 저장 폴더만 바꾼다

        static Store()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoCalendar");
            CfgPath = Path.Combine(appData, "window.json");
            SettingsPath = Path.Combine(appData, "settings.json");

            string custom = null;
            try
            {
                var s = Js.DeserializeObject(File.ReadAllText(SettingsPath)) as Dictionary<string, object>;
                if (s != null) Sett = s;
                if (Sett.ContainsKey("dataDir") && Sett["dataDir"] != null) custom = Sett["dataDir"].ToString();
            }
            catch { }

            if (!string.IsNullOrEmpty(custom)) { SetDir(custom); return; }

            string root = Environment.GetEnvironmentVariable("OneDriveCommercial");
            bool commercial = !string.IsNullOrEmpty(root);
            if (!commercial) root = Environment.GetEnvironmentVariable("OneDrive");
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            SetDir(Path.Combine(root, "memo-calendar"));
            NeedsFolderPrompt = !commercial;
        }

        // 저장 폴더를 정하고 설정 파일에 기억한다
        public static void ChangeDir(string d)
        {
            SetDir(d);
            NeedsFolderPrompt = false;
            Sett["dataDir"] = d;
            SaveSettings();
        }

        static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, Js.Serialize(Sett));
            }
            catch { }
        }

        // ---- 자동 백업: memos.json 사본을 저장 폴더 아래 backup 폴더에 날짜별로 보관한다 ----
        // 백업 주기(일). 0이면 끔. 기본은 매일.
        public static int BackupDays
        {
            get
            {
                object v;
                if (Sett.TryGetValue("backupDays", out v)) { try { return Convert.ToInt32(v); } catch { } }
                return 1;
            }
        }
        public static void SetBackupDays(int days) { Sett["backupDays"] = days; SaveSettings(); }

        public static string BackupDir { get { return Path.Combine(Dir, "backup"); } }

        static string[] BackupFiles()
        {
            try
            {
                if (!Directory.Exists(BackupDir)) return new string[0];
                string[] fs = Directory.GetFiles(BackupDir, "memos_*.json");
                Array.Sort(fs, StringComparer.Ordinal); // 파일 이름에 시각이 들어 있어 이름순 = 시간순
                return fs;
            }
            catch { return new string[0]; }
        }

        static bool BackupTime(string file, out DateTime t)
        {
            string n = Path.GetFileNameWithoutExtension(file); // memos_yyyyMMdd_HHmmss
            t = DateTime.MinValue;
            return n.Length >= 21 && DateTime.TryParseExact(n.Substring(6, 15), "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        // force=true: 주기와 상관없이 지금 백업. 새 백업을 만들었으면 true.
        public static bool BackupNow(bool force)
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                if (!force && BackupDays <= 0) return false;
                string[] fs = BackupFiles();
                string latest = fs.Length > 0 ? fs[fs.Length - 1] : null;
                if (!force && latest != null)
                {
                    DateTime t;
                    if (BackupTime(latest, out t) && (DateTime.Now - t).TotalDays < BackupDays) return false;
                }
                byte[] data = File.ReadAllBytes(FilePath);
                { List<Memo> probe; if (!TryLoad(out probe)) return false; } // 읽을 수 없는 파일은 백업하지 않는다 (정상 백업이 밀려나는 것 방지)
                if (latest != null)
                {
                    byte[] prev = File.ReadAllBytes(latest);
                    if (prev.Length == data.Length) { bool same = true; for (int i = 0; i < data.Length; i++) if (data[i] != prev[i]) { same = false; break; } if (same) return false; }
                }
                Directory.CreateDirectory(BackupDir);
                File.WriteAllBytes(Path.Combine(BackupDir, "memos_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json"), data);
                fs = BackupFiles();
                for (int i = 0; i < fs.Length - KeepBackups; i++) { try { File.Delete(fs[i]); } catch { } }
                return true;
            }
            catch { return false; }
        }

        static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }
        static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        public static List<Memo> Load()
        {
            List<Memo> list;
            TryLoad(out list);
            return list;
        }

        // 파일이 없으면 빈 목록(true). 파일이 있는데 읽지 못하면(동기화 중 반쯤 쓰인 경우 등) false —
        // 이때 빈 목록으로 착각해서 저장하면 메모가 지워질 수 있으므로 호출한 쪽이 기존 목록을 유지해야 한다.
        public static bool TryLoad(out List<Memo> list)
        {
            list = new List<Memo>();
            try
            {
                if (!File.Exists(FilePath)) return true;
                var root = Js.DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("memos")) return false;
                var arr = root["memos"] as IEnumerable;
                if (arr == null) return false;
                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    list.Add(new Memo { id = S(d, "id"), date = S(d, "date"), text = S(d, "text"), updated = S(d, "updated"), task = B(d, "task"), done = B(d, "done") });
                }
                return true;
            }
            catch { list = new List<Memo>(); return false; }
        }

        public static void Save(List<Memo> memos)
        {
            Directory.CreateDirectory(Dir);
            var arr = new List<object>();
            foreach (Memo m in memos)
            {
                var d = new Dictionary<string, object>();
                d["id"] = m.id; d["date"] = m.date; d["text"] = m.text;
                d["task"] = m.task; d["done"] = m.done; d["updated"] = m.updated;
                arr.Add(d);
            }
            var root = new Dictionary<string, object>();
            root["memos"] = arr;
            string tmp = FilePath + ".tmp";
            string json = Js.Serialize(root);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            // 임시 파일을 다시 읽어 확인한 뒤에만 교체한다 (쓰다 만 파일이 원본을 덮어쓰지 않게)
            var chk = Js.DeserializeObject(File.ReadAllText(tmp, Encoding.UTF8)) as Dictionary<string, object>;
            if (chk == null || !chk.ContainsKey("memos")) { File.Delete(tmp); throw new IOException("임시 파일 검증 실패"); }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public static Dictionary<string, object> LoadCfg()
        {
            try { return Js.DeserializeObject(File.ReadAllText(CfgPath)) as Dictionary<string, object>; }
            catch { return null; }
        }
        public static void SaveCfg(Rectangle b, double opacity)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CfgPath));
                var d = new Dictionary<string, object>();
                d["x"] = b.X; d["y"] = b.Y; d["w"] = b.Width; d["h"] = b.Height; d["opacity"] = opacity;
                File.WriteAllText(CfgPath, Js.Serialize(d));
            }
            catch { }
        }
    }

    class EditForm : Form
    {
        public TextBox Box;
        public EditForm(string date, string text, float s)
        {
            Text = date;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            Font = new Font("Malgun Gothic", 9f);
            ClientSize = new Size((int)(400 * s), (int)(290 * s));

            var hint = new Label();
            hint.Text = "한 줄 = 메모 하나.";
            hint.SetBounds((int)(10 * s), (int)(8 * s), (int)(380 * s), (int)(36 * s));
            Controls.Add(hint);

            Box = new TextBox();
            Box.Multiline = true; Box.AcceptsReturn = true; Box.ScrollBars = ScrollBars.Vertical;
            Box.Text = text;
            Box.SetBounds((int)(10 * s), (int)(46 * s), (int)(380 * s), (int)(190 * s));
            Controls.Add(Box);

            var ok = new Button(); ok.Text = "저장"; ok.DialogResult = DialogResult.OK;
            ok.SetBounds((int)(224 * s), (int)(248 * s), (int)(80 * s), (int)(30 * s));
            var cancel = new Button(); cancel.Text = "취소"; cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds((int)(310 * s), (int)(248 * s), (int)(80 * s), (int)(30 * s));
            Controls.Add(ok); Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = null;
            string orig = text;
            Box.KeyDown += delegate (object o, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Return) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; }
            };
            // 내용을 고친 채 취소/Esc로 닫으면 한 번 확인한다
            FormClosing += delegate (object o, FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.OK || Box.Text == orig) return;
                if (MessageBox.Show(this, "고친 내용을 저장하지 않고 닫을까요?", "메모 달력", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
            };
            Shown += delegate { Box.Focus(); Box.SelectionStart = Box.TextLength; };
        }
    }

    // 마우스를 올린 날짜의 메모를 전부 보여 주는 작은 창. 포커스를 가져가지 않는다.
    class TipForm : Form
    {
        readonly Label lbl;
        readonly int pad, maxW;

        public TipForm(float s)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(30, 41, 59);
            Opacity = 0.97;
            pad = (int)(8 * s);
            maxW = (int)(460 * s);
            lbl = new Label { AutoSize = false, ForeColor = Color.White, Font = new Font("Malgun Gothic", 9.5f), BackColor = BackColor };
            Controls.Add(lbl);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        public void ShowAt(string text, Point screenPt)
        {
            lbl.Text = text;
            Size sz = TextRenderer.MeasureText(text, lbl.Font, new Size(maxW, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            lbl.Bounds = new Rectangle(pad, pad, sz.Width + 2, sz.Height + 2);
            Size = new Size(lbl.Width + pad * 2, lbl.Height + pad * 2);
            Rectangle wa = Screen.FromPoint(screenPt).WorkingArea;
            int x = screenPt.X + 16, y = screenPt.Y + 18;
            if (x + Width > wa.Right) x = Math.Max(wa.Left, screenPt.X - Width - 8);
            if (y + Height > wa.Bottom) y = Math.Max(wa.Top, screenPt.Y - Height - 8);
            Location = new Point(x, y);
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(255, 193, 7))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    class Hit
    {
        public string Date, Text;
        public override string ToString() { return Date + "    " + Text.Replace("\r", "").Replace("\n", " "); }
    }

    // 메모 검색창: 띄어쓰기로 나눈 단어가 모두 들어 있는 메모를 찾는다. 결과를 열면 달력이 그 달로 이동한다.
    class SearchForm : Form
    {
        readonly Func<List<Memo>> getMemos;
        readonly Action<string> goTo;
        TextBox box;
        ListBox list;
        Label info;

        public SearchForm(Func<List<Memo>> getMemos, Action<string> goTo, float s)
        {
            this.getMemos = getMemos; this.goTo = goTo;
            Text = "메모 검색";
            Font = new Font("Malgun Gothic", 9.5f);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size((int)(560 * s), (int)(420 * s));
            KeyPreview = true;

            list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            info = new Label { Dock = DockStyle.Bottom, Height = (int)(24 * s), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
            box = new TextBox { Dock = DockStyle.Top, Font = new Font("Malgun Gothic", 11f) };
            Controls.Add(list); Controls.Add(info); Controls.Add(box);

            box.TextChanged += delegate { Run(); };
            box.KeyDown += delegate (object o, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { Open(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 0; e.Handled = true; }
            };
            list.DoubleClick += delegate { Open(); };
            list.KeyDown += delegate (object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { Open(); e.SuppressKeyPress = true; } };
            KeyDown += delegate (object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Shown += delegate { box.Focus(); Run(); };
        }

        public void SetQuery(string q) { box.Text = q; }
        public void FocusBox() { Activate(); box.Focus(); box.SelectAll(); }

        void Run()
        {
            string[] words = box.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            list.BeginUpdate();
            list.Items.Clear();
            if (words.Length == 0) { info.Text = "찾을 단어를 입력하세요. 여러 단어는 띄어쓰기로 (모두 들어 있는 메모를 찾습니다)"; list.EndUpdate(); return; }
            int total = 0;
            foreach (Memo m in getMemos().OrderByDescending(delegate (Memo x) { return x.date; }, StringComparer.Ordinal))
            {
                bool all = true;
                foreach (string w in words) if (m.text.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
                if (!all) continue;
                total++;
                if (list.Items.Count < 300) list.Items.Add(new Hit { Date = m.date, Text = m.text });
            }
            list.EndUpdate();
            info.Text = total == 0 ? "찾는 메모가 없습니다" : (total + "개 찾음" + (total > 300 ? " (앞의 300개만 표시)" : "") + "  -  더블클릭 또는 Enter로 해당 날짜로 이동");
        }

        void Open()
        {
            Hit h = list.SelectedItem as Hit;
            if (h == null && list.Items.Count > 0) h = list.Items[0] as Hit;
            if (h != null) goTo(h.Date);
        }
    }

    class CalPanel : Panel
    {
        public MainForm Owner2;
        public DateTime Cur;
        public string HighlightKey;
        public Rectangle[] Cells = new Rectangle[42];
        public DateTime[] Dates = new DateTime[42];
        int hover = -1;

        // 날짜 칸만 불투명한 그림으로 만든다 (마우스 올린 칸 강조와 검색 테두리는 뺀다)
        public Bitmap Snapshot()
        {
            int oldHover = hover; string oldHl = HighlightKey;
            hover = -1; HighlightKey = null;
            try
            {
                var bmp = new Bitmap(Width, Height);
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                return bmp;
            }
            finally { hover = oldHover; HighlightKey = oldHl; }
        }
        float S;
        Font fDow, fDay, fHol, fMemo, fMemoDone;

        static readonly Dictionary<string, string> Hol = new Dictionary<string, string>
        {
            {"2026-01-01","신정"},{"2026-02-16","설날연휴"},{"2026-02-17","설날"},{"2026-02-18","설날연휴"},{"2026-03-01","삼일절"},{"2026-03-02","대체휴일"},
            {"2026-05-01","근로자의날"},{"2026-05-05","어린이날"},{"2026-05-24","부처님오신날"},{"2026-05-25","대체휴일"},{"2026-06-03","지방선거"},{"2026-06-06","현충일"},
            {"2026-08-15","광복절"},{"2026-08-17","대체휴일"},{"2026-09-24","추석연휴"},{"2026-09-25","추석"},{"2026-09-26","추석연휴"},
            {"2026-07-17","제헌절"},
            {"2026-10-03","개천절"},{"2026-10-05","대체휴일"},{"2026-10-09","한글날"},{"2026-12-25","성탄절"},

            // 2027 (우주항공청 2027년 월력요항 보도 기준: 제헌절 재지정, 노동절 공휴일 추가, 설날은 KST 기준 2/7)
            {"2027-01-01","신정"},
            {"2027-02-06","설날연휴"},{"2027-02-07","설날"},{"2027-02-08","설날연휴"},{"2027-02-09","대체휴일"},
            {"2027-03-01","삼일절"},
            {"2027-05-01","노동절"},{"2027-05-03","대체휴일"},{"2027-05-05","어린이날"},{"2027-05-13","부처님오신날"},
            {"2027-06-06","현충일"},
            {"2027-07-17","제헌절"},{"2027-07-19","대체휴일"},
            {"2027-08-15","광복절"},{"2027-08-16","대체휴일"},
            {"2027-09-14","추석연휴"},{"2027-09-15","추석"},{"2027-09-16","추석연휴"},
            {"2027-10-03","개천절"},{"2027-10-04","대체휴일"},{"2027-10-09","한글날"},{"2027-10-11","대체휴일"},
            {"2027-12-25","성탄절"},{"2027-12-27","대체휴일"}
        };

        public CalPanel(float s)
        {
            S = s;
            DoubleBuffered = true;
            ResizeRedraw = true;
            fDow = new Font("Malgun Gothic", 10f, FontStyle.Bold);
            fDay = new Font("Malgun Gothic", 10.5f, FontStyle.Bold);
            fHol = new Font("Malgun Gothic", 8f);
            fMemo = new Font("Malgun Gothic", 8.5f);
            fMemoDone = new Font("Malgun Gothic", 8.5f, FontStyle.Strikeout);
        }

        int P(int v) { return (int)(v * S); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 반투명 창에서는 ClearType(색 번짐)이 거슬려서 회색조 안티앨리어싱을 쓴다
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int dowH = P(24);
            // 그 달에 필요한 주(4~6줄)만 그려서 칸을 더 크게 쓴다
            DateTime firstDay = new DateTime(Cur.Year, Cur.Month, 1);
            int rows = (int)Math.Ceiling(((int)firstDay.DayOfWeek + DateTime.DaysInMonth(Cur.Year, Cur.Month)) / 7.0);
            float cw = Width / 7f, ch = (Height - dowH) / (float)rows;
            for (int z = rows * 7; z < 42; z++) Cells[z] = Rectangle.Empty;
            var sfC = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var sfWrap = new StringFormat(StringFormatFlags.LineLimit);

            using (var hb = new SolidBrush(Color.FromArgb(40, 108, 123))) g.FillRectangle(hb, 0, 0, Width, dowH);
            string[] dn = { "일요일", "월요일", "화요일", "수요일", "목요일", "금요일", "토요일" };
            for (int i = 0; i < 7; i++) g.DrawString(dn[i], fDow, Brushes.White, new RectangleF(i * cw, 0, cw, dowH), sfC);

            DateTime first = new DateTime(Cur.Year, Cur.Month, 1);
            DateTime start = first.AddDays(-(int)first.DayOfWeek);
            string todayKey = Key(DateTime.Today);

            // 날짜별 메모를 한 번에 묶는다
            var byDate = new Dictionary<string, List<Memo>>();
            foreach (Memo m in Owner2.Memos)
            {
                List<Memo> l;
                if (!byDate.TryGetValue(m.date, out l)) { l = new List<Memo>(); byDate[m.date] = l; }
                l.Add(m);
            }

            Color sunC = Color.FromArgb(255, 180, 180), satC = Color.FromArgb(185, 215, 255);
            for (int i = 0; i < rows * 7; i++)
            {
                int row = i / 7, col = i % 7;
                var rc = new RectangleF(col * cw, dowH + row * ch, cw, ch);
                Cells[i] = Rectangle.Round(rc);
                DateTime d = start.AddDays(i);
                Dates[i] = d;
                string k = Key(d);
                bool other = d.Month != Cur.Month;
                string hn; bool isHol = Hol.TryGetValue(k, out hn);
                bool past = !other && d < DateTime.Today; // 이번 달 안에서 이미 지난 날은 살짝 가라앉혀 "지금" 위치가 눈에 띄게 한다
                int alpha = other ? 120 : past ? 130 : 255;

                Color fill = other ? Color.FromArgb(60, 138, 156) : past ? Color.FromArgb(30, 82, 94) : Color.FromArgb(48, 123, 139);
                if (k == todayKey) fill = Color.FromArgb(96, 166, 182);
                if (i == hover) fill = ControlPaint.Light(fill, 0.25f);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, rc);
                using (var p = new Pen(Color.FromArgb(110, 255, 255, 255))) g.DrawRectangle(p, rc.X, rc.Y, rc.Width - 1, rc.Height - 1);

                Color dayC = (d.DayOfWeek == DayOfWeek.Sunday || isHol) ? sunC : d.DayOfWeek == DayOfWeek.Saturday ? satC : Color.White;
                dayC = Color.FromArgb(alpha, dayC);

                g.SetClip(RectangleF.Inflate(rc, -1, -1));
                string num = d.Day.ToString();
                SizeF numSz = g.MeasureString(num, fDay);
                bool isToday = k == todayKey;
                if (isToday) // 오늘: 날짜 숫자에 배지
                {
                    // 오늘: 칸의 왼쪽 위 모서리(격자선)에 딱 붙인 각진 배지
                    float bw = Math.Max(P(22), numSz.Width * 0.85f + P(12)), bh = P(21);
                    var badge = new RectangleF(rc.X + 1, rc.Y + 1, bw, bh);
                    using (var bb = new SolidBrush(Color.FromArgb(255, 193, 7))) g.FillRectangle(bb, badge);
                    using (var tb = new SolidBrush(Color.FromArgb(20, 30, 50)))
                    using (var fn = new Font(fDay.FontFamily, fDay.Size * 0.9f, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(num, fn, tb, badge, sf);
                }
                else using (var b = new SolidBrush(dayC)) g.DrawString(num, fDay, b, rc.X + P(4), rc.Y + P(2));
                if (isHol)
                {
                    float nx = rc.X + P(4) + (isToday ? Math.Max(P(22), numSz.Width * 0.85f + P(12)) + P(4) : numSz.Width + P(1));
                    using (var b = new SolidBrush(Color.FromArgb(alpha, sunC))) g.DrawString(hn, fHol, b, nx, rc.Y + P(5));
                }

                List<Memo> ms;
                if (byDate.TryGetValue(k, out ms))
                {
                    float top = rc.Y + P(24);
                    int w = (int)(cw - P(8));
                    float avail = rc.Bottom - 1 - top;

                    // 줄바꿈해서 다 들어가면 그대로, 넘치면 메모마다 한 줄(끝은 …)로 줄여 더 많이 보여 준다
                    float wrapTotal = 0;
                    foreach (Memo m in ms) wrapTotal += g.MeasureString("• " + m.text, m.done ? fMemoDone : fMemo, w, sfWrap).Height;
                    bool oneLine = wrapTotal > avail;
                    var sfOne = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

                    using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                    {
                        float y = top;
                        for (int mi = 0; mi < ms.Count; mi++)
                        {
                            Memo m = ms[mi];
                            string t = "• " + m.text;
                            Font f = m.done ? fMemoDone : fMemo;
                            float h = oneLine ? f.GetHeight(g) + P(1) : g.MeasureString(t, f, w, sfWrap).Height;
                            if (y + h > rc.Bottom - 1)
                            {
                                string more = "+" + (ms.Count - mi); // 가려진 메모 개수
                                using (var fb = new Font(fMemo, FontStyle.Bold))
                                {
                                    SizeF ms2 = g.MeasureString(more, fb);
                                    var pill = new RectangleF(rc.Right - ms2.Width - P(6), rc.Bottom - ms2.Height - P(2), ms2.Width + P(3), ms2.Height);
                                    using (var pb = new SolidBrush(Color.FromArgb(alpha, 255, 193, 7))) g.FillRectangle(pb, pill);
                                    using (var tb = new SolidBrush(Color.FromArgb(alpha, 20, 30, 50))) g.DrawString(more, fb, tb, pill.X + P(1), pill.Y);
                                }
                                break;
                            }
                            if (oneLine) g.DrawString(t, f, b, new RectangleF(rc.X + P(4), y, w, h), sfOne);
                            else g.DrawString(t, f, b, new RectangleF(rc.X + P(4), y, w, h), sfWrap);
                            y += h;
                        }
                    }
                }
                g.ResetClip();

                if (k == todayKey)
                    using (var p = new Pen(Color.FromArgb(198, 40, 40), 2f)) g.DrawRectangle(p, rc.X + 1, rc.Y + 1, rc.Width - 3, rc.Height - 3);
                if (k == HighlightKey) // 검색 결과로 이동한 날짜
                    using (var p = new Pen(Color.FromArgb(255, 193, 7), 3f)) g.DrawRectangle(p, rc.X + 2, rc.Y + 2, rc.Width - 5, rc.Height - 5);
            }
        }

        public static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        int HitTest(Point p)
        {
            for (int i = 0; i < 42; i++) if (Cells[i].Contains(p)) return i;
            return -1;
        }
        TipForm tip;
        System.Windows.Forms.Timer tipTimer;

        bool HasMemos(string key)
        {
            foreach (Memo m in Owner2.Memos) if (m.date == key) return true;
            return false;
        }

        void HideTip()
        {
            if (tipTimer != null) tipTimer.Stop();
            if (tip != null && tip.Visible) tip.Hide();
        }

        // 마우스를 잠깐 올려 두면 그날 메모 전체를 보여 준다 (칸이 좁아 …로 잘린 내용 확인용)
        void ShowTip()
        {
            if (tipTimer != null) tipTimer.Stop();
            if (hover < 0 || !Cells[hover].Contains(PointToClient(Cursor.Position))) return;
            DateTime d = Dates[hover];
            string k = Key(d), hn;
            var sb = new StringBuilder();
            sb.Append(d.ToString("yyyy-MM-dd (ddd)", CultureInfo.GetCultureInfo("ko-KR")));
            if (Hol.TryGetValue(k, out hn)) sb.Append("  " + hn);
            foreach (Memo m in Owner2.Memos)
                if (m.date == k) sb.Append("\r\n" + (m.done ? "✓ " : "• ") + m.text);
            if (tip == null) tip = new TipForm(S);
            tip.ShowAt(sb.ToString(), Cursor.Position);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover)
            {
                HideTip();
                hover = h;
                Invalidate();
                if (h >= 0 && HasMemos(Key(Dates[h])))
                {
                    if (tipTimer == null) { tipTimer = new System.Windows.Forms.Timer { Interval = 450 }; tipTimer.Tick += delegate { ShowTip(); }; }
                    tipTimer.Start();
                }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            HideTip();
            if (hover != -1) { hover = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            HideTip();
            int h = HitTest(e.Location);
            if (h >= 0 && e.Button == MouseButtons.Left) { HighlightKey = null; Owner2.EditDay(Key(Dates[h])); }
            base.OnMouseClick(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            HideTip();
            Owner2.ShiftMonth(e.Delta > 0 ? -1 : 1);
        }
    }

    class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);

        public List<Memo> Memos = new List<Memo>();
        float S;
        CalPanel cal;
        Panel header, accent;
        Label lblTitle, lblOp;
        Button btnPrev, btnNext, btnToday, btnClose, btnTall, btnSearch, btnCopy;
        SearchForm search;
        Rectangle normalBounds;
        TrackBar trk;
        NotifyIcon tray;
        System.Windows.Forms.Timer timer;
        DateTime lastWrite = DateTime.MinValue;
        // 트레이 아이콘에 마우스를 올리면 파일을 마지막으로 읽거나 저장한 시각이 보인다 (OneDrive 동기화 확인용)
        void MarkSynced() { if (tray != null) tray.Text = "메모 달력 · 마지막 갱신 " + DateTime.Now.ToString("HH:mm:ss"); }
        bool editing;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        int P(int v) { return (int)(v * S); }

        public MainForm(bool shotMode)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
            Text = "메모 달력";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // 자식 컨트롤이 창 전체를 덮으면 가장자리를 잡을 수 없어서, 가장자리에 폼이 드러나는 얇은 틀을 둔다 (크기 조절용).
            // 틀 색은 상단 바와 같은 남색이라 바와 이어져 보인다.
            BackColor = Color.FromArgb(30, 41, 59);
            Padding = new Padding(P(4));
            // 크기를 바꾼 뒤 가장자리 틀에 이전 그림이 줄무늬로 남지 않도록 크기가 바뀔 때마다 전체를 다시 그린다
            DoubleBuffered = true;
            ResizeRedraw = true;
            MinimumSize = new Size(P(720), P(400));
            Icon = MakeIcon();

            var cfg = shotMode ? null : Store.LoadCfg();
            Size = new Size(P(1000), P(640));
            Location = new Point(P(100), P(60));
            double op = 0.92;
            if (cfg != null)
            {
                try
                {
                    var r = new Rectangle(Convert.ToInt32(cfg["x"]), Convert.ToInt32(cfg["y"]), Convert.ToInt32(cfg["w"]), Convert.ToInt32(cfg["h"]));
                    if (Screen.AllScreens.Length > 0 && Screen.FromRectangle(r).Bounds.IntersectsWith(r) && r.Width >= MinimumSize.Width && r.Height >= MinimumSize.Height)
                    { Bounds = r; }
                    op = Convert.ToDouble(cfg["opacity"], CultureInfo.InvariantCulture);
                }
                catch { }
            }
            if (op < 0.4 || op > 1) op = 0.92;
            Opacity = shotMode ? 1.0 : op;

            // 달력(청록)과 확실히 구분되는 진한 남색 바 + 아래 노란 선. 끌 수 있는 곳이라 마우스 모양도 바뀐다.
            header = new Panel { Dock = DockStyle.Top, Height = P(42), BackColor = Color.FromArgb(30, 41, 59), Cursor = Cursors.SizeAll };
            // 노란 줄은 바에 그리지 않고 맨 위에 올린 별도 패널로 둔다 (슬라이더/버튼이 줄 위를 덮지 못하게)
            accent = new Panel { BackColor = Color.FromArgb(255, 193, 7) };
            Font hf = new Font("Malgun Gothic", 10.5f, FontStyle.Bold);
            lblTitle = new Label { ForeColor = Color.White, Font = new Font("Malgun Gothic", 12f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter };
            btnPrev = HBtn("◀"); btnNext = HBtn("▶"); btnToday = HBtn("오늘"); btnClose = HBtn("✕"); btnTall = HBtn("늘리기");
            btnTall.Click += delegate { ToggleTall(); };
            btnSearch = HBtn("검색");
            btnSearch.Click += delegate { OpenSearch(); };
            btnCopy = HBtn("달력복사");
            btnCopy.Click += delegate { CopyCalendarImage(); };
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F) { OpenSearch(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.PageUp || e.KeyCode == Keys.PageDown) { ShiftMonth(e.KeyCode == Keys.PageUp ? -1 : 1); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Home) { cal.Cur = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); UpdateTitle(); cal.Invalidate(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) // Esc: 검색 표시를 지우고 오늘이 있는 달로 돌아온다
                {
                    cal.HighlightKey = null;
                    cal.Cur = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                    UpdateTitle();
                    cal.Invalidate();
                    e.SuppressKeyPress = true;
                }
            };
            lblOp = new Label { Text = "투명도", ForeColor = Color.White, Font = new Font("Malgun Gothic", 9f), TextAlign = ContentAlignment.MiddleRight };
            // AutoSize를 끄지 않으면 슬라이더가 스스로 키가 커져 바 아래 노란 줄을 덮는다
            trk = new TrackBar { AutoSize = false, Minimum = 40, Maximum = 100, TickStyle = TickStyle.None, BackColor = header.BackColor, Value = (int)Math.Round(op * 100) };
            trk.Scroll += delegate { Opacity = trk.Value / 100.0; };
            trk.MouseUp += delegate { SaveCfg(); };
            btnPrev.Click += delegate { ShiftMonth(-1); };
            btnNext.Click += delegate { ShiftMonth(1); };
            btnToday.Click += delegate { cal.Cur = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); UpdateTitle(); cal.Invalidate(); };
            btnClose.Click += delegate { Close(); };
            foreach (Control c in new Control[] { lblTitle, btnPrev, btnNext, btnToday, btnTall, btnSearch, btnCopy, lblOp, trk, btnClose, accent }) header.Controls.Add(c);
            accent.BringToFront();
            header.Resize += delegate { LayoutHeader(); };
            foreach (Control c in new Control[] { header, lblTitle, lblOp })
            {
                c.MouseDown += DragStart; c.MouseMove += DragMove; c.MouseUp += DragEnd;
            }

            cal = new CalPanel(S) { Dock = DockStyle.Fill, Owner2 = this };
            cal.Cur = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Controls.Add(cal);
            Controls.Add(header);
            LayoutHeader();

            Memos = Store.Load();
            TouchStamp();
            UpdateTitle();
            UpdateTallText();

            if (shotMode) return;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("보이기/숨기기", null, delegate { ToggleVisible(); });
            pathItem = new ToolStripMenuItem { Enabled = false };
            menu.Items.Add(pathItem);
            menu.Items.Add("저장 폴더 열기", null, delegate { OpenFolder(); });
            menu.Items.Add("저장 폴더 변경...", null, delegate { ChangeFolder(); });
            menu.Items.Add(new ToolStripSeparator());

            // 자동 백업 주기 선택
            var backupMenu = new ToolStripMenuItem("자동 백업");
            string[] names = { "끄기", "매일", "3일마다", "매주", "2주마다", "매월" };
            int[] days = { 0, 1, 3, 7, 14, 30 };
            var picks = new List<ToolStripMenuItem>();
            for (int bi = 0; bi < names.Length; bi++)
            {
                int d = days[bi];
                var it = new ToolStripMenuItem(names[bi]) { Checked = Store.BackupDays == d };
                it.Click += delegate
                {
                    Store.SetBackupDays(d);
                    foreach (var p in picks) p.Checked = (p == it);
                    Store.BackupNow(false);
                };
                picks.Add(it);
                backupMenu.DropDownItems.Add(it);
            }
            backupMenu.DropDownItems.Add(new ToolStripSeparator());
            backupMenu.DropDownItems.Add("지금 백업", null, delegate
            {
                bool ok = Store.BackupNow(true);
                MessageBox.Show(ok ? "백업했습니다.\n" + Store.BackupDir : "백업할 메모 파일이 없거나 저장하지 못했습니다.", "메모 달력");
            });
            backupMenu.DropDownItems.Add("백업 폴더 열기", null, delegate
            {
                try { Directory.CreateDirectory(Store.BackupDir); System.Diagnostics.Process.Start("explorer.exe", "\"" + Store.BackupDir + "\""); } catch { }
            });
            menu.Items.Add(backupMenu);

            menu.Items.Add(new ToolStripSeparator());
            var auto = new ToolStripMenuItem("윈도우 시작 시 자동 실행") { Checked = IsAutoStart(), CheckOnClick = true };
            auto.CheckedChanged += delegate { SetAutoStart(auto.Checked); };
            menu.Items.Add(auto);
            menu.Items.Add("종료", null, delegate { Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "메모 달력 · 마지막 갱신 " + DateTime.Now.ToString("HH:mm:ss"), ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleVisible(); };
            EnsureFolder();
            UpdatePathItem();
            Store.BackupNow(false);
            nextBackupCheck = DateTime.Now.AddHours(1);
            Shown += delegate { BeginInvoke(new MethodInvoker(FirstRunCheck)); };

            // 다른 PC/OneDrive 동기화로 파일이 바뀌면 다시 읽는다
            timer = new System.Windows.Forms.Timer { Interval = 30000 };
            timer.Tick += delegate
            {
                // 컴퓨터를 오래 켜 두는 경우를 위해 한 시간마다 백업 주기가 됐는지 본다
                if (DateTime.Now >= nextBackupCheck) { Store.BackupNow(false); nextBackupCheck = DateTime.Now.AddHours(1); }
                // 날짜가 바뀌었으면 "오늘" 표시를 새 날짜로 옮긴다. 이번 달을 보고 있었다면 새 달로도 따라간다.
                if (DateTime.Today != lastToday)
                {
                    bool following = cal.Cur.Year == lastToday.Year && cal.Cur.Month == lastToday.Month;
                    lastToday = DateTime.Today;
                    if (following) { cal.Cur = new DateTime(lastToday.Year, lastToday.Month, 1); UpdateTitle(); }
                    cal.Invalidate();
                }
                if (editing) return;
                DateTime w = FileStamp();
                if (w != lastWrite)
                {
                    List<Memo> fresh;
                    // 읽기에 실패하면(동기화 도중 등) 화면을 비우지 않고 기존 내용을 두고 다음 주기에 다시 시도한다
                    if (Store.TryLoad(out fresh)) { Memos = fresh; lastWrite = w; MarkSynced(); cal.Invalidate(); }
                }
            };
            timer.Start();
        }

        ToolStripMenuItem pathItem;
        DateTime nextBackupCheck = DateTime.MaxValue;
        DateTime lastToday = DateTime.Today;

        // 저장 폴더가 없으면 실행 시점에 바로 만든다
        void EnsureFolder()
        {
            try { Directory.CreateDirectory(Store.Dir); }
            catch (Exception ex) { MessageBox.Show("저장 폴더를 만들지 못했습니다.\n" + Store.Dir + "\n" + ex.Message, "메모 달력"); }
        }

        void UpdatePathItem()
        {
            string p = Store.Dir;
            if (p.Length > 60) p = p.Substring(0, 25) + "…" + p.Substring(p.Length - 33);
            pathItem.Text = "저장 위치: " + p;
        }

        void OpenFolder()
        {
            EnsureFolder();
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Store.Dir + "\""); } catch { }
        }

        // OneDrive를 못 찾은 PC는 처음 한 번 저장 폴더를 고르게 한다
        void FirstRunCheck()
        {
            if (!Store.NeedsFolderPrompt) return;
            DialogResult r = MessageBox.Show(this,
                "OneDrive 폴더를 찾지 못했습니다.\n지금 저장 위치:\n" + Store.Dir +
                "\n\n여러 PC나 휴대폰과 동기화하려면 OneDrive 안의 폴더를 선택하세요.\n지금 선택할까요?",
                "메모 달력", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) ChangeFolder();
            else Store.ChangeDir(Store.Dir); // 다시 묻지 않도록 현재 위치를 기억
            UpdatePathItem();
        }

        void ChangeFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "메모(memos.json)를 저장할 폴더를 선택하세요.\nOneDrive 안의 폴더를 고르면 여러 기기와 동기화됩니다.";
                dlg.ShowNewFolderButton = true;
                if (Directory.Exists(Store.Dir)) dlg.SelectedPath = Store.Dir;
                if (dlg.ShowDialog() != DialogResult.OK) return;
                string newDir = dlg.SelectedPath;
                if (string.Equals(newDir.TrimEnd('\\'), Store.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;

                string oldFile = Store.FilePath, newFile = Path.Combine(newDir, "memos.json");
                if (!File.Exists(newFile) && File.Exists(oldFile))
                {
                    if (MessageBox.Show("기존 메모를 새 폴더로 복사할까요?\n(원본은 그대로 남습니다)", "메모 달력", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    {
                        try { File.Copy(oldFile, newFile); }
                        catch (Exception ex) { MessageBox.Show("복사하지 못했습니다.\n" + ex.Message, "메모 달력"); return; }
                    }
                }
                Store.ChangeDir(newDir);
                EnsureFolder();
                Memos = Store.Load();
                TouchStamp();
                UpdatePathItem();
                cal.Invalidate();
            }
        }

        Button HBtn(string t)
        {
            var b = new Button { Text = t, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(63, 84, 112), Font = new Font("Malgun Gothic", 9f), TabStop = false, Cursor = Cursors.Default };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        void LayoutHeader()
        {
            // 노란 줄(2px)을 뺀 나머지 높이의 한가운데에 모든 글자·버튼·슬라이더를 같은 선으로 맞춘다
            int ch = header.Height - P(2), bh = P(28), y = (ch - bh) / 2, x = P(8);
            btnPrev.SetBounds(x, y, P(32), bh); x += P(36);
            lblTitle.SetBounds(x, y, P(170), bh); x += P(174);
            btnNext.SetBounds(x, y, P(32), bh); x += P(38);
            btnToday.SetBounds(x, y, P(52), bh); x += P(58);
            btnTall.SetBounds(x, y, P(58), bh); x += P(64);
            btnSearch.SetBounds(x, y, P(48), bh); x += P(54);
            btnCopy.SetBounds(x, y, P(72), bh);
            int rx = header.Width - P(8) - P(32);
            btnClose.SetBounds(rx, y, P(32), bh); rx -= P(104);
            // 슬라이더 트랙은 컨트롤 안에서 위쪽에 그려져 조금 내려야 다른 것과 선이 맞는다
            trk.SetBounds(rx, y + P(3), P(100), bh); rx -= P(54);
            lblOp.SetBounds(rx, y, P(50), bh);
            accent.SetBounds(0, header.Height - P(2), header.Width, P(2));
        }

        void OpenSearch()
        {
            if (search != null && !search.IsDisposed) { search.FocusBox(); return; }
            search = new SearchForm(delegate { return Memos; }, GoTo, S);
            search.FormClosed += delegate { cal.HighlightKey = null; cal.Invalidate(); };
            search.Show(this);
        }

        // 검색 결과를 열면 그 달로 이동하고 해당 날짜 칸을 노란 테두리로 표시한다
        void GoTo(string key)
        {
            DateTime d;
            if (!DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return;
            cal.Cur = new DateTime(d.Year, d.Month, 1);
            cal.HighlightKey = key;
            UpdateTitle();
            cal.Invalidate();
        }

        // ↕ 버튼: 화면 높이(작업 표시줄 제외)까지 세로로 늘렸다가, 다시 누르면 원래 크기로
        bool IsTall()
        {
            return Height >= Screen.FromControl(this).WorkingArea.Height - 2;
        }

        void UpdateTallText() { btnTall.Text = IsTall() ? "줄이기" : "늘리기"; }

        void ToggleTall()
        {
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (!IsTall())
            {
                normalBounds = Bounds;
                Bounds = new Rectangle(Left, wa.Top, Width, wa.Height);
            }
            else
            {
                // 늘어난 채로 다시 시작한 경우 원래 크기를 모르므로 기본 높이로 줄인다
                Bounds = normalBounds.IsEmpty ? new Rectangle(Left, wa.Top + P(60), Width, P(640)) : normalBounds;
            }
            UpdateTallText();
            SaveCfg();
        }

        // 상단 바를 잡고 끄는 이동. 시스템 이동 루프 대신 직접 처리해서,
        // 바 안의 어느 곳을 잡아도 같은 방식으로 움직이고 화면 가장자리에 달라붙는다.
        bool dragging;
        Point dragCursor, dragOrigin;

        void DragStart(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragging = true;
            dragCursor = Cursor.Position;
            dragOrigin = Location;
            ((Control)s).Capture = true;
        }

        void DragMove(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point c = Cursor.Position;
            int x = dragOrigin.X + c.X - dragCursor.X;
            int y = dragOrigin.Y + c.Y - dragCursor.Y;
            Rectangle wa = Screen.FromPoint(c).WorkingArea;
            int th = P(10);
            if (Math.Abs(x - wa.Left) < th) x = wa.Left;
            else if (Math.Abs(x + Width - wa.Right) < th) x = wa.Right - Width;
            if (Math.Abs(y - wa.Top) < th) y = wa.Top;
            else if (Math.Abs(y + Height - wa.Bottom) < th) y = wa.Bottom - Height;
            Location = new Point(x, y);
        }

        void DragEnd(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ((Control)s).Capture = false;
            UpdateTallText();
            SaveCfg();
        }

        // 테두리 없는 창의 가장자리 크기 조절
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)
            {
                Point p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
                int g = P(7);
                bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
                int res = 0;
                if (t && l) res = 13; else if (t && r) res = 14; else if (b && l) res = 16; else if (b && r) res = 17;
                else if (l) res = 10; else if (r) res = 11; else if (t) res = 12; else if (b) res = 15;
                if (res != 0) { m.Result = (IntPtr)res; return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(150, 255, 255, 255))) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); UpdateTallText(); SaveCfg(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveCfg();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosing(e);
        }
        void SaveCfg() { Store.SaveCfg(Bounds, Opacity); }
        void ToggleVisible() { if (Visible) Hide(); else { Show(); } }

        void UpdateTitle() { lblTitle.Text = cal.Cur.Year + "년 " + cal.Cur.Month + "월"; }
        public void ShiftMonth(int d) { cal.Cur = cal.Cur.AddMonths(d); UpdateTitle(); cal.Invalidate(); }

        DateTime FileStamp() { try { return File.Exists(Store.FilePath) ? File.GetLastWriteTimeUtc(Store.FilePath) : DateTime.MinValue; } catch { return DateTime.MinValue; } }
        // 달력 칸을 그림으로 클립보드에 복사한다 (달력복사 버튼)
        public void CopyCalendarImage()
        {
            try
            {
                using (Bitmap bmp = cal.Snapshot()) Clipboard.SetImage(bmp);
                if (tray != null) tray.ShowBalloonTip(1500, "메모 달력", "달력을 클립보드에 복사했습니다", ToolTipIcon.None);
            }
            catch (Exception ex) { MessageBox.Show("클립보드에 복사하지 못했습니다.\n" + ex.Message, "메모 달력"); }
        }

        void TouchStamp() { lastWrite = FileStamp(); MarkSynced(); }

        static string Fmt(Memo m) { return m.text; }

        public void EditDay(string key)
        {
            var old = Memos.FindAll(delegate (Memo m) { return m.date == key; });
            var sb = new StringBuilder();
            foreach (Memo m in old) { if (sb.Length > 0) sb.Append("\r\n"); sb.Append(Fmt(m)); }
            editing = true;
            try
            {
                using (var dlg = new EditForm(key, sb.ToString(), S))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try { ApplyEdit(key, dlg.Box.Text); }
                    catch (Exception ex)
                    {
                        // 입력한 내용이 사라지지 않도록 클립보드에 넣어 둔다
                        try { Clipboard.SetText(dlg.Box.Text); } catch { }
                        MessageBox.Show(this, "저장하지 못했습니다. 입력한 내용은 클립보드에 복사해 두었습니다.\n" + ex.Message, "메모 달력");
                    }
                }
            }
            finally { editing = false; cal.Invalidate(); }
        }

        // 한 날짜의 메모를 입력창 텍스트(한 줄 = 메모 하나)로 바꿔 저장한다
        public void ApplyEdit(string key, string newText)
        {
            // 저장 직전에 최신 파일을 다시 읽어 다른 PC의 변경을 덮어쓰지 않는다.
            // 읽지 못하면(동기화 중 등) 이미 불러 둔 목록을 그대로 써서, 빈 목록으로 저장해 메모가 지워지는 일을 막는다.
            List<Memo> latest;
            if (Store.TryLoad(out latest)) Memos = latest;

            var old = Memos.FindAll(delegate (Memo m) { return m.date == key; });
            var rest = Memos.FindAll(delegate (Memo m) { return m.date != key; });
            var used = new HashSet<string>();
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var fresh = new List<Memo>();
            int i = 0;
            foreach (string raw in newText.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                bool task = false, done = false; string text = line;
                // 같은 내용(없으면 같은 순서)의 기존 메모 id를 유지 → 같은 메모를 가리키는 id가 바뀌지 않는다
                Memo o = old.Find(delegate (Memo x) { return !used.Contains(x.id) && x.text == text; });
                if (o == null && i < old.Count && !used.Contains(old[i].id)) o = old[i];
                if (o != null) used.Add(o.id);
                bool same = o != null && o.text == text && o.done == done && o.task == task;
                fresh.Add(new Memo { id = o != null ? o.id : NewId(), date = key, text = text, task = task, done = done, updated = same ? o.updated : now });
                i++;
            }
            rest.AddRange(fresh);
            // List.Sort는 같은 날짜 안의 순서를 섞을 수 있어서, 순서를 지키는 OrderBy를 쓴다
            var merged = rest.OrderBy(delegate (Memo x) { return x.date; }, StringComparer.Ordinal).ToList();
            Store.Save(merged); // 저장이 실패하면 예외가 나가고 화면 목록은 그대로 남는다
            Memos = merged;
            TouchStamp();
        }

        static readonly Random Rng = new Random();
        static string NewId()
        {
            const string a = "abcdefghijklmnopqrstuvwxyz0123456789";
            var sb = new StringBuilder();
            sb.Append(ToBase36((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds));
            for (int i = 0; i < 5; i++) sb.Append(a[Rng.Next(a.Length)]);
            return sb.ToString();
        }

        // Convert.ToString(값, 36)은 2/8/10/16진수만 지원해서 "기본이 잘못되었습니다" 오류가 났다
        static string ToBase36(long v)
        {
            const string d = "0123456789abcdefghijklmnopqrstuvwxyz";
            if (v <= 0) return "0";
            var sb = new StringBuilder();
            while (v > 0) { sb.Insert(0, d[(int)(v % 36)]); v /= 36; }
            return sb.ToString();
        }

        static Icon MakeIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(58, 150, 170)), 2, 4, 28, 26);
                    g.FillRectangle(new SolidBrush(Color.FromArgb(198, 40, 40)), 2, 4, 28, 8);
                    for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) g.FillRectangle(Brushes.White, 5 + c * 6, 15 + r * 5, 4, 3);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static bool IsAutoStart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MemoCalendar") != null; }
            catch { return false; }
        }
        static void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("MemoCalendar", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("MemoCalendar", false);
                }
            }
            catch { }
        }

        // 화면 캡처용: MemoCalendar.exe --shot 파일.png [yyyy-MM]
        public void Shot(string path, string month)
        {
            if (month != null)
            {
                DateTime d;
                if (DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) { cal.Cur = d; UpdateTitle(); }
            }
            // 컨트롤이 실제로 그려지도록 화면 밖에서 잠깐 띄운다
            Location = new Point(-32000, -32000);
            Show();
            Application.DoEvents();
            using (var bmp = new Bitmap(Width, Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Close();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && args[0] == "--shot")
            {
                var f = new MainForm(true);
                f.Shot(args[1], args.Length > 2 ? args[2] : null);
                return;
            }

            if (args.Length >= 3 && args[0] == "--selftest-save") // 저장 로직 자체 점검: 임시 폴더에서만 동작
            {
                Store.UseTempDir(args[2]);
                var tf = new MainForm(true);
                var report = new StringBuilder();
                try
                {
                    tf.ApplyEdit("2026-11-30", "첫째 줄\n둘째 줄");
                    List<Memo> a = Store.Load();
                    tf.ApplyEdit("2026-11-30", "첫째 줄\n둘째 줄\n셋째 줄");
                    List<Memo> b = Store.Load();
                    report.AppendLine("1차 저장: " + a.Count + "개, id=" + string.Join(",", a.ConvertAll(delegate (Memo m) { return m.id; }).ToArray()));
                    report.AppendLine("2차 저장: " + b.Count + "개, id=" + string.Join(",", b.ConvertAll(delegate (Memo m) { return m.id; }).ToArray()));
                    bool ok = a.Count == 2 && b.Count == 3 && a[0].id == b[0].id && a[1].id == b[1].id && !b[0].task && !b[1].task;
                    report.AppendLine(ok ? "결과: 정상 (id 유지)" : "결과: 이상");
                }
                catch (Exception ex) { report.AppendLine("오류: " + ex.GetType().Name + " " + ex.Message); }
                File.WriteAllText(args[1], report.ToString(), new UTF8Encoding(false));
                return;
            }

            if (args.Length >= 2 && args[0] == "--selftest-copy") // 클립보드 복사 점검: 복사 뒤 클립보드에서 다시 읽어 PNG로 저장
            {
                var cf = new MainForm(true);
                cf.Location = new Point(-32000, -32000);
                cf.Show(); Application.DoEvents();
                cf.CopyCalendarImage();
                Image img = Clipboard.GetImage();
                if (img != null) img.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                cf.Close();
                return;
            }

            if (args.Length >= 3 && args[0] == "--shot-search") // 검색창 화면 캡처용
            {
                var mf = new MainForm(true);
                var sf = new SearchForm(delegate { return mf.Memos; }, delegate { }, 1f);
                sf.Location = new Point(-32000, -32000);
                sf.Show();
                sf.SetQuery(args[2]);
                Application.DoEvents();
                using (var bmp = new Bitmap(sf.Width, sf.Height))
                {
                    sf.DrawToBitmap(bmp, new Rectangle(0, 0, sf.Width, sf.Height));
                    bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "MemoCalendarLite_SingleInstance", out created))
            {
                if (!created) return;
                Application.Run(new MainForm(false));
            }
        }
    }
}
