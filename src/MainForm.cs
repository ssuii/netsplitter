using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetSplitter
{
    public sealed class MainForm : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private readonly float _scale;
        private readonly bool _isAdmin;
        private readonly TrafficMonitor _monitor;
        private readonly RuleStore _store;
        private readonly List<SplitRule> _rules;
        private readonly ProcBindingStore _bindStore;
        private readonly List<ProcBinding> _bindings;
        private readonly Dictionary<string, AdapterCard> _cards = new Dictionary<string, AdapterCard>();
        private readonly ToolTip _tips = new ToolTip();

        private List<AdapterInfo> _adapters = new List<AdapterInfo>();
        private string _adapterSignature = "";

        private TableLayoutPanel _root;
        private Panel _banner;
        private SplitContainer _split;
        private FlowLayoutPanel _adapterFlow;
        private DataGridView _rulesGrid;
        private TextBox _matchBox;
        private ComboBox _adapterCombo;
        private CheckBox _persistentCheck;
        private DataGridView _procGrid;
        private DataGridView _ipGrid;
        private TabControl _tabs;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _modeLabel;
        private ToolStripStatusLabel _connLabel;
        private ToolStripStatusLabel _statLabel;
        private ToolStripStatusLabel _timeLabel;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _uiTimer;
        private System.Windows.Forms.Timer _debounceTimer;
        private System.Windows.Forms.Timer _domainTimer;

        internal bool PreviewMode;
        internal bool StartMinimized;
        internal bool SkipAutoApply;
        private bool _suppressGridEvents;
        private bool _applying;
        private bool _pendingApply;
        private bool _procAdapterDesc = true;
        private bool _ipAdapterDesc = true;
        private readonly HashSet<string> _expandedProc = new HashSet<string>();
        private List<ProcIpRow> _procIpRows = new List<ProcIpRow>();
        private ContextMenuStrip _procMenu;
        private Button _oneKeyWifiButton;
        private bool _wifiSwitching;

        private const string ChildTagPrefix = "ip:";
        private const char ChildTagSep = '\u0001';
        private string _notice;
        private DateTime _noticeUntil;
        private bool _demoMode;
        private bool _reallyExit;

        public MainForm()
        {
            _scale = GetSystemDpi() / 96f;
            _isAdmin = RouteManager.IsAdmin;
            _store = new RuleStore();
            _rules = _store.Load();
            _bindStore = new ProcBindingStore();
            _bindings = _bindStore.Load();
            _monitor = new TrafficMonitor(_isAdmin);

            Text = "NetSplitter — 多网卡分流与流量监控";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Colors.Bg;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;

            int width = Math.Min(S(1180), Screen.PrimaryScreen.WorkingArea.Width - S(40));
            int height = Math.Min(S(820), Screen.PrimaryScreen.WorkingArea.Height - S(60));
            ClientSize = new Size(Math.Max(width, S(960)), Math.Max(height, S(660)));
            MinimumSize = new Size(
                Math.Min(S(960), Screen.PrimaryScreen.WorkingArea.Width),
                Math.Min(S(620), Screen.PrimaryScreen.WorkingArea.Height));

            _monitor.Start();
            BuildUi();
            RefreshAdapterCards(_monitor.CurrentAdapters());
            RebuildRulesGrid();
            SetupTray();

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            _debounceTimer = new System.Windows.Forms.Timer();
            _debounceTimer.Interval = 3000;
            _debounceTimer.Tick += DebounceTimer_Tick;

            _domainTimer = new System.Windows.Forms.Timer();
            _domainTimer.Interval = 60000;
            _domainTimer.Tick += DomainTimer_Tick;
            _domainTimer.Start();

            NetworkChange.NetworkAddressChanged += NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkChanged;

            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private static int GetSystemDpi()
        {
            try
            {
                uint dpi = GetDpiForSystem();
                if (dpi >= 96) return (int)dpi;
            }
            catch
            {
            }
            return 96;
        }

        private void BuildUi()
        {
            _root = new TableLayoutPanel();
            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 1;
            _root.RowCount = 5;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(66)));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, _isAdmin ? 0 : S(38)));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(166)));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
            _root.BackColor = Colors.Bg;
            _root.Margin = new Padding(0);
            Controls.Add(_root);

            BuildHeader();
            BuildBanner();
            BuildAdapters();
            BuildSplit();
            BuildStatus();
        }

        private void BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Color.White;
            header.Margin = new Padding(0);
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Colors.Line))
                {
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                }
            };

            Label title = new Label();
            title.Text = "NetSplitter";
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
            title.ForeColor = Colors.TextDark;
            title.AutoSize = true;
            title.Location = new Point(S(18), S(6));
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "多网卡 IP 分流 · 实时流量监控";
            subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            subtitle.ForeColor = Colors.TextMuted;
            subtitle.AutoSize = true;
            subtitle.Location = new Point(S(21), S(39));
            header.Controls.Add(subtitle);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Right;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.BackColor = Color.Transparent;
            actions.Padding = new Padding(0, S(17), S(18), 0);

            Label badge = new Label();
            badge.Text = _isAdmin ? "● 管理员模式 · 规则自动应用" : "● 只读模式";
            badge.ForeColor = _isAdmin ? Colors.Green : Colors.Amber;
            badge.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            badge.AutoSize = true;
            badge.Margin = new Padding(S(10), S(6), 0, 0);
            actions.Controls.Add(badge);

            if (!_isAdmin)
            {
                Button elevate = MakeButton("以管理员身份重启", false);
                elevate.Click += delegate { Elevate(); };
                actions.Controls.Add(elevate);
            }

            _oneKeyWifiButton = MakeButton("一键切换", false);
            _oneKeyWifiButton.Click += delegate { OneKeyWifiSwitch(); };
            actions.Controls.Add(_oneKeyWifiButton);

            Button wifiSwitch = MakeButton("Wi-Fi 切换", false);
            wifiSwitch.Click += delegate { OpenWifiDialog(); };
            actions.Controls.Add(wifiSwitch);
            RefreshWifiButtonTip();

            header.Controls.Add(actions);
            _root.Controls.Add(header, 0, 0);
        }

        private void BuildBanner()
        {
            _banner = new Panel();
            _banner.Dock = DockStyle.Fill;
            _banner.BackColor = Colors.BannerBg;
            _banner.Visible = !_isAdmin;
            _banner.Margin = new Padding(0);

            Label text = new Label();
            text.Text = "只读模式：应用分流规则、采集进程/IP 流量需要管理员权限。点击右上角「以管理员身份重启」。";
            text.ForeColor = Colors.Amber;
            text.Font = new Font("Microsoft YaHei UI", 9F);
            text.AutoSize = true;
            text.Location = new Point(S(20), S(9));
            _banner.Controls.Add(text);

            _root.Controls.Add(_banner, 0, 1);
        }

        private void BuildAdapters()
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.BackColor = Colors.SoftBg;
            host.Margin = new Padding(0);

            _adapterFlow = new FlowLayoutPanel();
            _adapterFlow.Dock = DockStyle.Fill;
            _adapterFlow.AutoScroll = true;
            _adapterFlow.WrapContents = false;
            _adapterFlow.FlowDirection = FlowDirection.LeftToRight;
            _adapterFlow.Padding = new Padding(S(10), S(8), S(10), S(8));
            _adapterFlow.BackColor = Color.Transparent;
            host.Controls.Add(_adapterFlow);

            _root.Controls.Add(host, 0, 2);
        }

        private void BuildSplit()
        {
            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Horizontal;
            _split.SplitterWidth = S(6);
            _split.BackColor = Colors.Bg;
            _split.Margin = new Padding(0);
            _split.Panel1.BackColor = Colors.Bg;
            _split.Panel2.BackColor = Colors.Bg;
            _split.Panel1.Padding = new Padding(S(12), S(4), S(12), S(3));
            _split.Panel2.Padding = new Padding(S(12), S(3), S(12), S(8));

            _split.Panel1.Controls.Add(BuildRulesBox());
            _split.Panel2.Controls.Add(BuildTrafficBox());
            _root.Controls.Add(_split, 0, 3);
        }

        private GroupBox BuildRulesBox()
        {
            GroupBox box = new GroupBox();
            box.Text = "  分流规则（IP / 网段 / 域名 → 指定网卡）  ";
            box.Dock = DockStyle.Fill;
            box.ForeColor = Colors.TextMuted;
            box.BackColor = Colors.Bg;
            box.Padding = new Padding(S(10), S(10), S(10), S(10));
            box.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.BackColor = Colors.Bg;

            FlowLayoutPanel input = new FlowLayoutPanel();
            input.Dock = DockStyle.Fill;
            input.WrapContents = false;
            input.AutoScroll = true;
            input.BackColor = Colors.Bg;
            input.Padding = new Padding(0, S(2), 0, 0);

            _matchBox = new TextBox();
            _matchBox.Width = S(210);
            _matchBox.Font = new Font("Microsoft YaHei UI", 9.5F);
            _matchBox.Margin = new Padding(0, S(3), S(8), 0);
            _matchBox.BorderStyle = BorderStyle.FixedSingle;
            input.Controls.Add(_matchBox);

            _adapterCombo = new ComboBox();
            _adapterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _adapterCombo.Width = S(300);
            _adapterCombo.Font = new Font("Microsoft YaHei UI", 9.5F);
            _adapterCombo.Margin = new Padding(0, S(3), S(8), 0);
            input.Controls.Add(_adapterCombo);

            Button add = MakeButton("添加规则", true);
            add.Margin = new Padding(0, S(2), S(8), 0);
            add.Click += delegate { AddRule(); };
            input.Controls.Add(add);

            Button remove = MakeButton("删除所选", false);
            remove.Margin = new Padding(0, S(2), S(8), 0);
            remove.Click += delegate { RemoveSelectedRule(); };
            input.Controls.Add(remove);

            _persistentCheck = new CheckBox();
            _persistentCheck.Text = "重启后保留";
            _persistentCheck.AutoSize = true;
            _persistentCheck.Checked = true;
            _persistentCheck.ForeColor = Colors.TextMuted;
            _persistentCheck.Font = new Font("Microsoft YaHei UI", 8.5F);
            _persistentCheck.Margin = new Padding(0, S(7), 0, 0);
            _tips.SetToolTip(_persistentCheck,
                "默认开启：路由同时写入持久存储，系统重启后仍然有效。\n关闭后只写入当前会话（ActiveStore）。");
            input.Controls.Add(_persistentCheck);

            Button export = MakeButton("导出配置", false);
            export.Margin = new Padding(S(10), S(2), S(6), 0);
            export.Click += delegate { ExportConfigClicked(); };
            input.Controls.Add(export);

            Button import = MakeButton("导入配置", false);
            import.Margin = new Padding(0, S(2), S(6), 0);
            import.Click += delegate { ImportConfigClicked(); };
            input.Controls.Add(import);

            Label hint = new Label();
            hint.Text = "支持 IP / 网段 / 域名";
            hint.AutoSize = true;
            hint.ForeColor = Colors.TextMuted;
            hint.Font = new Font("Microsoft YaHei UI", 8.5F);
            hint.Margin = new Padding(S(6), S(8), 0, 0);
            _tips.SetToolTip(hint, "示例：8.8.8.8、10.0.0.0/8、example.com\n域名规则会自动解析为 IPv4 并逐个下发 /32 路由，每 60 秒刷新。\n导出包含全部规则与进程跟踪，可在其他机器导入。");
            input.Controls.Add(hint);

            layout.Controls.Add(input, 0, 0);

            _rulesGrid = new DataGridView();
            _rulesGrid.Dock = DockStyle.Fill;
            _rulesGrid.Margin = new Padding(0);
            StyleGrid(_rulesGrid);
            _rulesGrid.AutoGenerateColumns = false;
            _rulesGrid.ReadOnly = false;

            DataGridViewCheckBoxColumn colEnabled = new DataGridViewCheckBoxColumn();
            colEnabled.HeaderText = "启用";
            colEnabled.FillWeight = 5f;
            colEnabled.MinimumWidth = S(50);
            colEnabled.ReadOnly = false;

            DataGridViewTextBoxColumn colMatch = new DataGridViewTextBoxColumn();
            colMatch.HeaderText = "目标（IP / 网段 / 域名）";
            colMatch.FillWeight = 19f;
            colMatch.ReadOnly = true;

            DataGridViewTextBoxColumn colResolved = new DataGridViewTextBoxColumn();
            colResolved.HeaderText = "解析 IP（域名规则）";
            colResolved.FillWeight = 15f;
            colResolved.ReadOnly = true;

            DataGridViewTextBoxColumn colAdapter = new DataGridViewTextBoxColumn();
            colAdapter.HeaderText = "目标网卡";
            colAdapter.FillWeight = 16f;
            colAdapter.ReadOnly = true;

            DataGridViewTextBoxColumn colProcesses = new DataGridViewTextBoxColumn();
            colProcesses.HeaderText = "关联进程（实时）";
            colProcesses.FillWeight = 24f;
            colProcesses.ReadOnly = true;

            DataGridViewTextBoxColumn colStatus = new DataGridViewTextBoxColumn();
            colStatus.HeaderText = "状态";
            colStatus.FillWeight = 21f;
            colStatus.ReadOnly = true;

            _rulesGrid.Columns.AddRange(new DataGridViewColumn[] { colEnabled, colMatch, colResolved, colAdapter, colProcesses, colStatus });
            _rulesGrid.CellValueChanged += RulesGrid_CellValueChanged;
            _rulesGrid.CurrentCellDirtyStateChanged += RulesGrid_CurrentCellDirtyStateChanged;
            _rulesGrid.KeyDown += RulesGrid_KeyDown;

            layout.Controls.Add(_rulesGrid, 0, 1);
            box.Controls.Add(layout);
            return box;
        }

        private GroupBox BuildTrafficBox()
        {
            GroupBox box = new GroupBox();
            box.Text = "  流量明细（每秒刷新）  ";
            box.Dock = DockStyle.Fill;
            box.ForeColor = Colors.TextMuted;
            box.BackColor = Colors.Bg;
            box.Padding = new Padding(S(10), S(10), S(10), S(10));
            box.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.BackColor = Colors.Bg;

            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Colors.Bg;
            header.Margin = new Padding(0);

            Label note = new Label();
            note.Text = "点击进程展开远端 IP；右键可加入分流规则，或自动跟踪该进程的新 IP（⟳ 标记，持续加入并持久化）。";
            note.AutoSize = true;
            note.ForeColor = Colors.TextMuted;
            note.Font = new Font("Microsoft YaHei UI", 8.5F);
            note.Location = new Point(0, S(6));
            header.Controls.Add(note);

            Button reset = MakeButton("重置统计", false);
            reset.AutoSize = false;
            reset.Dock = DockStyle.Right;
            reset.Width = S(96);
            reset.Margin = new Padding(0);
            reset.Click += delegate
            {
                _monitor.ResetCounters();
                _expandedProc.Clear();
                _procIpRows.Clear();
                _procGrid.Rows.Clear();
                _ipGrid.Rows.Clear();
            };
            header.Controls.Add(reset);

            layout.Controls.Add(header, 0, 0);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Font = new Font("Microsoft YaHei UI", 9F);
            _tabs.Padding = new Point(S(14), S(4));

            TabPage procPage = new TabPage("程序流量 (TCP)");
            procPage.BackColor = Color.White;
            procPage.Padding = new Padding(S(2));
            _procGrid = CreateTrafficGrid(
                new string[] { "程序", "网卡", "接收速率", "发送速率", "累计接收", "累计发送" },
                new float[] { 30f, 22f, 13f, 13f, 12f, 12f });
            procPage.Controls.Add(_procGrid);
            _tabs.TabPages.Add(procPage);

            TabPage ipPage = new TabPage("远端 IP 流量 (TCP)");
            ipPage.BackColor = Color.White;
            ipPage.Padding = new Padding(S(2));
            _ipGrid = CreateTrafficGrid(
                new string[] { "远端 IP", "网卡", "接收速率", "发送速率", "累计接收", "累计发送" },
                new float[] { 30f, 22f, 13f, 13f, 12f, 12f });
            ipPage.Controls.Add(_ipGrid);
            _tabs.TabPages.Add(ipPage);

            _procGrid.Columns[1].HeaderCell.SortGlyphDirection = SortOrder.Descending;
            _procGrid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                ToggleAdapterSort(_procGrid, ref _procAdapterDesc, e.ColumnIndex);
            };
            _ipGrid.Columns[1].HeaderCell.SortGlyphDirection = SortOrder.Descending;
            _ipGrid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                ToggleAdapterSort(_ipGrid, ref _ipAdapterDesc, e.ColumnIndex);
            };

            _procGrid.CellClick += ProcGrid_CellClick;
            _procGrid.CellMouseDown += ProcGrid_CellMouseDown;

            layout.Controls.Add(_tabs, 0, 1);
            box.Controls.Add(layout);
            return box;
        }

        private void BuildStatus()
        {
            _statusStrip = new StatusStrip();
            _statusStrip.Dock = DockStyle.Fill;
            _statusStrip.SizingGrip = false;
            _statusStrip.AutoSize = false;
            _statusStrip.BackColor = Color.White;
            _statusStrip.Padding = new Padding(S(14), 0, S(14), 0);
            _statusStrip.Font = new Font("Microsoft YaHei UI", 8.5F);

            _modeLabel = new ToolStripStatusLabel();
            _modeLabel.Text = _isAdmin ? "模式：管理员" : "模式：只读（需要管理员）";
            _modeLabel.ForeColor = _isAdmin ? Colors.Green : Colors.Amber;

            _connLabel = new ToolStripStatusLabel();
            _connLabel.Text = "TCP 连接 0";

            _statLabel = new ToolStripStatusLabel();
            _statLabel.Text = "";

            ToolStripStatusLabel spacer = new ToolStripStatusLabel();
            spacer.Spring = true;

            _timeLabel = new ToolStripStatusLabel();
            _timeLabel.Text = "";

            _statusStrip.Items.AddRange(new ToolStripItem[]
            {
                _modeLabel,
                new ToolStripStatusLabel("   "),
                _connLabel,
                new ToolStripStatusLabel("   "),
                _statLabel,
                spacer,
                _timeLabel
            });

            _root.Controls.Add(_statusStrip, 0, 4);
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Colors.Line;
            button.BackColor = primary ? Colors.Blue : Color.White;
            button.ForeColor = primary ? Color.White : Colors.TextDark;
            button.Padding = new Padding(S(12), S(3), S(12), S(3));
            button.Margin = new Padding(S(4), 0, S(4), 0);
            button.Font = new Font("Microsoft YaHei UI", 9F);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private DataGridView CreateTrafficGrid(string[] headers, float[] weights)
        {
            DataGridView grid = new DataGridView();
            grid.Dock = DockStyle.Fill;
            grid.Margin = new Padding(0);
            StyleGrid(grid);
            grid.ReadOnly = true;
            grid.AutoGenerateColumns = false;

            for (int i = 0; i < headers.Length; i++)
            {
                DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
                column.HeaderText = headers[i];
                column.FillWeight = weights[i];
                column.ReadOnly = true;
                column.SortMode = i == 1 ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable;
                if (i >= 2)
                {
                    column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                    column.DefaultCellStyle.Font = new Font("Consolas", 9F);
                }
                grid.Columns.Add(column);
            }
            return grid;
        }

        private void StyleGrid(DataGridView grid)
        {
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.None;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = Color.FromArgb(243, 244, 246);
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 28;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Colors.SoftBg;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Colors.TextMuted;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Colors.SoftBg;
            grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F);
            grid.DefaultCellStyle.SelectionBackColor = Colors.Selection;
            grid.DefaultCellStyle.SelectionForeColor = Colors.TextDark;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Colors.SoftBg;
            grid.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);

            try
            {
                PropertyInfo property = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (property != null) property.SetValue(grid, true, null);
            }
            catch
            {
            }
        }

        internal static string WifiPairText(string first, string second)
        {
            if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(second)) return "";
            if (string.IsNullOrEmpty(second)) return first;
            if (string.IsNullOrEmpty(first)) return second;
            return first + "  ⇄  " + second;
        }

        private void RefreshWifiButtonTip()
        {
            if (_oneKeyWifiButton == null) return;
            string[] pair = WifiConfig.LoadPair();
            string text = WifiPairText(pair[0], pair[1]);
            if (text.Length == 0)
            {
                _tips.SetToolTip(_oneKeyWifiButton,
                    "还没有配置一键切换的 Wi-Fi。点「Wi-Fi 切换」选择两个网络，分别设为 A 和 B。");
            }
            else if (pair[0].Length == 0 || pair[1].Length == 0)
            {
                _tips.SetToolTip(_oneKeyWifiButton,
                    "一键切换：" + text + "\n只配置了一个目标；在「Wi-Fi 切换」中再设置另一个即可互相切换。");
            }
            else
            {
                _tips.SetToolTip(_oneKeyWifiButton,
                    "一键切换在两者之间交替：" + text + "\n连在 A 上就切到 B，连在 B 上就切到 A。");
            }
        }

        private void OpenWifiDialog()
        {
            string[] pair = WifiConfig.LoadPair();
            using (WifiForm dialog = new WifiForm(_scale, pair[0], pair[1]))
            {
                dialog.ShowDialog(this);
                RefreshWifiButtonTip();
                if (dialog.WifiChanged)
                {
                    AdapterService.InvalidateWifiCache();
                    try { RefreshAdapterCards(AdapterService.GetAdapters()); }
                    catch (Exception ex) { Logger.Write("刷新网卡失败: " + ex.Message); }
                }
            }
        }

        private async void OneKeyWifiSwitch()
        {
            if (_wifiSwitching || IsDisposed) return;
            string[] pair = WifiConfig.LoadPair();
            if (pair[0].Length == 0 && pair[1].Length == 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "还没有设置一键切换的 Wi-Fi。\n现在打开「Wi-Fi 切换」窗口去设置两个网络吗？",
                    "NetSplitter", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) OpenWifiDialog();
                return;
            }

            WifiStatus status;
            try
            {
                status = await Task.Run(delegate { return WifiService.ReadStatus(); });
            }
            catch (Exception ex)
            {
                Logger.Write("读取无线状态失败: " + ex.Message);
                _statLabel.Text = "读取无线状态失败";
                return;
            }
            if (IsDisposed) return;
            if (!status.Available)
            {
                MessageBox.Show(this, "未检测到可用的无线网卡：" + status.Error, "NetSplitter",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string current = status.Connected ? status.Ssid : "";
            string target = WifiConfig.PickTarget(pair[0], pair[1], current, status.Connected);
            if (target.Length == 0 || target == current)
            {
                _statLabel.Text = "已连接 Wi-Fi：" + current;
                ShowTrayTip("已连接到 Wi-Fi：" + current + "，没有需要切换的目标");
                return;
            }

            _wifiSwitching = true;
            _oneKeyWifiButton.Enabled = false;
            _statLabel.Text = "正在切换到 Wi-Fi：" + target + " …";
            try
            {
                WifiResult result = await Task.Run(delegate { return WifiService.Connect(target, null); });
                if (IsDisposed) return;
                if (result.Ok)
                {
                    _statLabel.Text = "已切换到 Wi-Fi：" + target;
                    string tip = "已切换到 Wi-Fi：" + target;
                    if (current.Length > 0 && (current == pair[0] || current == pair[1]))
                        tip += "，再点一次切回 " + current;
                    AdapterService.InvalidateWifiCache();
                    try { RefreshAdapterCards(AdapterService.GetAdapters()); }
                    catch (Exception ex) { Logger.Write("切换后刷新网卡失败: " + ex.Message); }
                    ShowTrayTip(tip);
                }
                else
                {
                    _statLabel.Text = "一键切换失败";
                    MessageBox.Show(this,
                        "切换到 Wi-Fi「" + target + "」失败：\n" + result.Error +
                        "\n\n如果该网络还没有保存过密码，请在「Wi-Fi 切换」里输入密码连接一次。",
                        "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("一键切换 Wi-Fi 失败: " + ex);
                _statLabel.Text = "一键切换失败";
            }
            finally
            {
                _wifiSwitching = false;
                if (!IsDisposed) _oneKeyWifiButton.Enabled = true;
            }
        }

        private void ShowTrayTip(string text)
        {
            try { _tray.ShowBalloonTip(1500, "NetSplitter", text, ToolTipIcon.Info); }
            catch { }
        }

        private void SetupTray()
        {
            _tray = new NotifyIcon();
            try
            {
                Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _tray.Icon = icon != null ? icon : SystemIcons.Application;
            }
            catch
            {
                _tray.Icon = SystemIcons.Application;
            }
            _tray.Text = "NetSplitter — 多网卡分流与流量监控";

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem wifiMenu = new ToolStripMenuItem("Wi-Fi 快速切换…");
            wifiMenu.Click += delegate { RestoreFromTray(); OpenWifiDialog(); };
            menu.Items.Add(wifiMenu);

            ToolStripMenuItem oneKeyMenu = new ToolStripMenuItem("一键切换 Wi-Fi");
            oneKeyMenu.Click += delegate { OneKeyWifiSwitch(); };
            menu.Items.Add(oneKeyMenu);
            menu.Opening += delegate
            {
                string[] pair = WifiConfig.LoadPair();
                string text = WifiPairText(pair[0], pair[1]);
                oneKeyMenu.Text = text.Length == 0 ? "一键切换 Wi-Fi（未配置）" : "一键切换 Wi-Fi：" + text;
            };

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem startup = new ToolStripMenuItem("开机启动");
            startup.Checked = StartupManager.IsEnabled();
            startup.ToolTipText = StartupManager.IsTaskEnabled()
                ? "以管理员身份开机启动（计划任务，登录时不弹 UAC）"
                : "普通开机启动（启动后为只读模式）";
            startup.Click += delegate
            {
                bool target = !startup.Checked;
                string error;
                if (StartupManager.TrySet(target, out error))
                {
                    startup.Checked = target;
                    if (StartupManager.IsTaskEnabled())
                        startup.ToolTipText = "以管理员身份开机启动（计划任务，登录时不弹 UAC）";
                    else
                        startup.ToolTipText = "普通开机启动（启动后为只读模式）";
                    try
                    {
                        string tip = !target
                            ? "已取消开机启动"
                            : (StartupManager.IsTaskEnabled()
                                ? "已设置开机启动：登录后以管理员身份运行，不会弹 UAC"
                                : "已加入开机启动（普通权限）");
                        _tray.ShowBalloonTip(1500, "NetSplitter", tip, ToolTipIcon.Info);
                    }
                    catch
                    {
                    }
                    if (target && !StartupManager.IsTaskEnabled())
                    {
                        MessageBox.Show(this,
                            "已加入开机启动，但程序会以普通权限启动（只读模式，无法下发分流规则）。\n\n" +
                            "如果想要开机后自动以管理员身份运行、登录时也不弹 UAC，" +
                            "请先用右上角「以管理员身份重启」，再在托盘菜单里勾选「开机启动」。",
                            "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    MessageBox.Show(this, "设置开机启动失败：" + error, "NetSplitter",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            menu.Items.Add(startup);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreFromTray(); };
            _tray.Visible = true;
        }

        private void MinimizeToTray()
        {
            Hide();
            ShowInTaskbar = false;
            try { _tray.ShowBalloonTip(1200, "NetSplitter", "已最小化到托盘，流量监控仍在继续。", ToolTipIcon.Info); }
            catch { }
        }

        private void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _reallyExit = true;
            Close();
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            try
            {
                int min1 = S(150);
                int min2 = S(170);
                int usable = _split.Height - _split.SplitterWidth;
                if (usable > min1 + min2)
                {
                    _split.Panel1MinSize = min1;
                    _split.Panel2MinSize = min2;
                    int distance = (int)(_split.Height * 0.42);
                    if (distance < min1) distance = min1;
                    if (distance > usable - min2) distance = usable - min2;
                    _split.SplitterDistance = distance;
                }
            }
            catch
            {
            }

            try
            {
                SendMessage(_matchBox.Handle, 0x1501, (IntPtr)1, "IP / 网段 / 域名，例如 example.com");
            }
            catch
            {
            }

            if (_isAdmin && !PreviewMode) StartupManager.EnsureElevatedAutostart();
            if (_isAdmin && !PreviewMode && !SkipAutoApply) ApplyAllRulesAsync();
            if (!_isAdmin)
            {
                foreach (SplitRule rule in _rules)
                {
                    if (rule.Status == "待应用" || rule.Status == "待解析") rule.Status = "需要管理员权限";
                }
                UpdateRuleRows();
            }

            if (StartMinimized && !PreviewMode)
            {
                BeginInvoke(new Action(delegate { MinimizeToTray(); }));
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyExit && !PreviewMode && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }

            if (_uiTimer != null) _uiTimer.Stop();
            if (_debounceTimer != null) _debounceTimer.Stop();
            if (_domainTimer != null) _domainTimer.Stop();
            NetworkChange.NetworkAddressChanged -= NetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkChanged;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_procMenu != null)
            {
                _procMenu.Dispose();
                _procMenu = null;
            }
            _monitor.Dispose();
        }

        private void NetworkChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (_debounceTimer == null) return;
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }));
            }
            catch
            {
            }
        }

        private void DebounceTimer_Tick(object sender, EventArgs e)
        {
            _debounceTimer.Stop();
            AdapterService.InvalidateWifiCache();
            if (_isAdmin) ApplyAllRulesAsync();
        }

        private void Elevate()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--elevated-child");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                _reallyExit = true;
                Close();
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223)
                {
                    MessageBox.Show(this, "无法以管理员身份重启：" + ex.Message, "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法以管理员身份重启：" + ex.Message, "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void ApplyAllRulesAsync()
        {
            await ApplyRulesCoreAsync(false);
        }

        private async void DomainTimer_Tick(object sender, EventArgs e)
        {
            if (PreviewMode || !_isAdmin || _applying || IsDisposed) return;
            bool hasDomain = false;
            foreach (SplitRule rule in _rules)
            {
                if (rule.Enabled && rule.IsDomain)
                {
                    hasDomain = true;
                    break;
                }
            }
            if (hasDomain) await ApplyRulesCoreAsync(true);
        }

        private async Task ApplyRulesCoreAsync(bool quiet)
        {
            if (_applying)
            {
                _pendingApply = true;
                return;
            }
            _applying = true;
            _pendingApply = false;
            if (!quiet)
            {
                _modeLabel.Text = "正在应用路由…";
                _modeLabel.ForeColor = Colors.Amber;
            }

            try
            {
                List<SplitRule> snapshot = new List<SplitRule>(_rules);
                bool admin = _isAdmin;

                List<AdapterInfo> adapters = await Task.Run(delegate { return AdapterService.GetAdapters(); });
                if (IsDisposed) return;
                List<RuleResult> results = await Task.Run(delegate { return RouteManager.ApplyRules(snapshot, adapters, admin); });
                if (IsDisposed) return;

                _adapters = adapters;
                RefreshAdapterCards(adapters);
                UpdateRuleRows();

                int failed = 0;
                foreach (RuleResult result in results)
                {
                    if (result.Rule.Enabled && !result.Ok) failed++;
                }

                if (!quiet)
                {
                    _modeLabel.Text = _isAdmin ? "模式：管理员" : "模式：只读（需要管理员）";
                    _modeLabel.ForeColor = _isAdmin ? Colors.Green : Colors.Amber;
                    if (failed > 0) _statLabel.Text = failed + " 条规则未生效";
                    else if (results.Count > 0) _statLabel.Text = "分流规则已应用";
                }
                else if (failed > 0)
                {
                    _statLabel.Text = failed + " 条规则未生效";
                }
            }
            catch (Exception ex)
            {
                Logger.Write("应用规则失败: " + ex);
                if (!quiet)
                {
                    _modeLabel.Text = _isAdmin ? "模式：管理员" : "模式：只读（需要管理员）";
                    _modeLabel.ForeColor = _isAdmin ? Colors.Green : Colors.Amber;
                    MessageBox.Show(this, "应用规则时出错：" + ex.Message, "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                _applying = false;
                if (_pendingApply)
                {
                    _pendingApply = false;
                    ApplyAllRulesAsync();
                }
            }
        }

        private void AddRule()
        {
            string text = (_matchBox.Text ?? "").Trim();
            System.Net.IPAddress network;
            int prefix;
            string error;
            bool isDomain = false;
            string canonical;
            if (RouteManager.TryParseRule(text, out network, out prefix, out error))
            {
                canonical = RouteManager.ToCidr(network, prefix);
            }
            else
            {
                string domainError;
                canonical = DomainResolver.Normalize(text, out domainError);
                if (canonical == null)
                {
                    MessageBox.Show(this, error + "\n" + domainError, "规则无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                isDomain = true;
            }

            AdapterChoice choice = _adapterCombo.SelectedItem as AdapterChoice;
            if (choice == null)
            {
                MessageBox.Show(this, "请选择目标网卡。", "规则无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            foreach (SplitRule existing in _rules)
            {
                if (existing.AdapterId == choice.Adapter.Id && existing.Match == canonical)
                {
                    MessageBox.Show(this, "相同规则已存在。", "重复规则", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            SplitRule rule = new SplitRule();
            rule.Enabled = true;
            rule.Kind = isDomain ? RuleKind.Domain : RuleKind.Ip;
            rule.Match = canonical;
            rule.AdapterId = choice.Adapter.Id;
            rule.AdapterName = choice.Adapter.Name;
            rule.Metric = 1;
            rule.Persistent = _persistentCheck.Checked;
            rule.Status = _isAdmin ? (isDomain ? "待解析" : "待应用") : "需要管理员权限";
            _rules.Add(rule);
            _store.Save(_rules);
            RebuildRulesGrid();
            _matchBox.Text = "";

            if (!_isAdmin)
            {
                _statLabel.Text = "规则已保存，需管理员权限下发";
                return;
            }
            ApplyAllRulesAsync();
        }

        private void RemoveSelectedRule()
        {
            SplitRule rule = null;
            if (_rulesGrid.SelectedRows.Count > 0) rule = _rulesGrid.SelectedRows[0].Tag as SplitRule;
            if (rule == null && _rulesGrid.CurrentRow != null) rule = _rulesGrid.CurrentRow.Tag as SplitRule;
            if (rule == null) return;

            string adapterName = ResolveAdapterName(rule);
            DialogResult result = MessageBox.Show(this,
                string.Format("删除规则 {0} → {1}？\n\n若该路由已下发，将尝试从系统中删除。", rule.Match, adapterName),
                "删除规则", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result != DialogResult.Yes) return;

            _rules.Remove(rule);
            _store.Save(_rules);
            RebuildRulesGrid();

            if (_isAdmin)
            {
                List<AdapterInfo> adapters = new List<AdapterInfo>(_adapters);
                SplitRule copy = rule;
                Task.Run(delegate { RouteManager.RemoveRule(copy, adapters); });
            }
            if (_isAdmin) ApplyAllRulesAsync();
        }

        private void RulesGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suppressGridEvents || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            DataGridViewRow row = _rulesGrid.Rows[e.RowIndex];
            SplitRule rule = row.Tag as SplitRule;
            if (rule == null) return;
            bool enabled = Convert.ToBoolean(row.Cells[0].Value);
            if (rule.Enabled == enabled) return;
            rule.Enabled = enabled;
            _store.Save(_rules);

            if (_isAdmin)
            {
                if (!enabled)
                {
                    List<AdapterInfo> adapters = new List<AdapterInfo>(_adapters);
                    SplitRule copy = rule;
                    Task.Run(delegate { RouteManager.RemoveRule(copy, adapters); });
                }
                ApplyAllRulesAsync();
            }
            else
            {
                RebuildRulesGrid();
            }
        }

        private void RulesGrid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_rulesGrid.IsCurrentCellDirty)
            {
                _rulesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void RulesGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedRule();
                e.Handled = true;
            }
        }

        private void RebuildRulesGrid()
        {
            if (_rulesGrid == null) return;
            _suppressGridEvents = true;
            try
            {
                _rulesGrid.Rows.Clear();
                foreach (SplitRule rule in _rules)
                {
                    int index = _rulesGrid.Rows.Add(rule.Enabled, rule.Match, ResolveSummary(rule),
                        ResolveAdapterName(rule), "", rule.Status);
                    DataGridViewRow row = _rulesGrid.Rows[index];
                    row.Tag = rule;
                    StyleRuleStatus(row.Cells[5], rule);
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
            RefreshRuleProcesses(_procIpRows);
        }

        private void UpdateRuleRows()
        {
            if (_rulesGrid == null) return;
            _suppressGridEvents = true;
            try
            {
                for (int i = 0; i < _rules.Count && i < _rulesGrid.Rows.Count; i++)
                {
                    SplitRule rule = _rules[i];
                    DataGridViewRow row = _rulesGrid.Rows[i];
                    row.Tag = rule;
                    row.Cells[0].Value = rule.Enabled;
                    row.Cells[1].Value = rule.Match;
                    row.Cells[2].Value = ResolveSummary(rule);
                    row.Cells[3].Value = ResolveAdapterName(rule);
                    row.Cells[5].Value = rule.Status;
                    StyleRuleStatus(row.Cells[5], rule);
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
            RefreshRuleProcesses(_procIpRows);
        }

        private void RefreshRuleProcesses(List<ProcIpRow> procIps)
        {
            if (_rulesGrid == null || _rulesGrid.Rows.Count == 0) return;
            List<ProcIpRow> rows = procIps ?? new List<ProcIpRow>();
            _suppressGridEvents = true;
            try
            {
                foreach (DataGridViewRow row in _rulesGrid.Rows)
                {
                    SplitRule rule = row.Tag as SplitRule;
                    if (rule == null) continue;
                    List<string> names = RuleProcesses(rule, rows);
                    string text = SummaryProcesses(names);
                    if (!string.Equals(Convert.ToString(row.Cells[4].Value), text, StringComparison.Ordinal))
                        row.Cells[4].Value = text;
                    row.Cells[4].ToolTipText = names.Count == 0
                        ? "当前没有监控到正在使用该规则的进程"
                        : "监控到的进程：" + string.Join("、", names.ToArray());
                }
            }
            finally
            {
                _suppressGridEvents = false;
            }
        }

        private static List<string> RuleProcesses(SplitRule rule, List<ProcIpRow> procIps)
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            System.Net.IPAddress network = null;
            int prefix = -1;
            if (!rule.IsDomain)
            {
                string error;
                if (!RouteManager.TryParseRule(rule.Match, out network, out prefix, out error)) return names;
            }
            HashSet<string> domainIps = new HashSet<string>(StringComparer.Ordinal);
            if (rule.IsDomain)
            {
                foreach (string part in (rule.LastIps ?? "").Split(','))
                {
                    string ip = (part ?? "").Trim();
                    if (ip.Length > 0) domainIps.Add(ip);
                }
                if (domainIps.Count == 0) return names;
            }

            foreach (ProcIpRow item in procIps)
            {
                if (item == null || string.IsNullOrEmpty(item.Process)) continue;
                bool match;
                if (rule.IsDomain)
                {
                    match = domainIps.Contains((item.RemoteIp ?? "").Trim());
                }
                else
                {
                    System.Net.IPAddress ip;
                    match = System.Net.IPAddress.TryParse(item.RemoteIp, out ip) &&
                            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            RouteManager.IsInSubnet(network, ip, prefix);
                }
                if (match && seen.Add(item.Process)) names.Add(item.Process);
            }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        private static string SummaryProcesses(List<string> names)
        {
            const int maxShown = 3;
            const int maxChars = 34;
            if (names.Count == 0) return "";
            List<string> shown = new List<string>();
            int length = 0;
            for (int i = 0; i < names.Count && i < maxShown; i++)
            {
                int add = names[i].Length + (shown.Count > 0 ? 1 : 0);
                if (shown.Count > 0 && length + add > maxChars) break;
                shown.Add(names[i]);
                length += add;
            }
            string text = string.Join("、", shown.ToArray());
            if (shown.Count < names.Count) text += " 等 " + names.Count + " 个";
            return text;
        }

        internal void ExportConfig(string path)
        {
            List<AdapterInfo> adapters = _adapters.Count > 0 ? _adapters : AdapterService.GetAdapters();
            ConfigIO.Export(path, _rules, _bindings, adapters);
        }

        private void ExportConfigClicked()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出分流配置";
                dialog.Filter = ConfigIO.FileFilter;
                dialog.FileName = "NetSplitter-config-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json";
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ExportConfig(dialog.FileName);
                    ShowNotice("已导出 " + _rules.Count + " 条规则、" + _bindings.Count + " 个进程跟踪 → " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    Logger.Write("导出配置失败: " + ex.Message);
                    MessageBox.Show(this, "导出配置失败：" + ex.Message, "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportConfigClicked()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "导入分流配置";
                dialog.Filter = ConfigIO.FileFilter;
                dialog.CheckFileExists = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    ImportConfig(dialog.FileName);
                }
                catch (FormatException ex)
                {
                    MessageBox.Show(this, ex.Message, "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch (Exception ex)
                {
                    Logger.Write("导入配置失败: " + ex.Message);
                    MessageBox.Show(this, "导入配置失败：" + ex.Message, "NetSplitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        internal string ImportConfig(string path)
        {
            ConfigBundle bundle = ConfigIO.Load(path);
            List<AdapterInfo> adapters = AdapterService.GetAdapters();
            _adapters = adapters;

            int addedRules = 0;
            int skippedRules = 0;
            int addedBinds = 0;
            int skippedBinds = 0;
            List<string> unmatched = new List<string>();

            foreach (ConfigRule item in bundle.Rules)
            {
                string match = (item.Match ?? "").Trim();
                bool domain = string.Equals(item.Kind, "domain", StringComparison.OrdinalIgnoreCase);
                if (match.Length == 0)
                {
                    skippedRules++;
                    continue;
                }
                if (domain)
                {
                    string error;
                    string normalized = DomainResolver.Normalize(match, out error);
                    if (normalized == null)
                    {
                        skippedRules++;
                        continue;
                    }
                    match = normalized;
                }
                else
                {
                    System.Net.IPAddress network;
                    int prefix;
                    string error;
                    if (!RouteManager.TryParseRule(match, out network, out prefix, out error))
                    {
                        skippedRules++;
                        continue;
                    }
                    match = RouteManager.ToCidr(network, prefix);
                }

                bool duplicate = false;
                foreach (SplitRule existing in _rules)
                {
                    if (string.Equals(existing.Match, match, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    skippedRules++;
                    continue;
                }

                AdapterInfo adapter = ConfigIO.ResolveAdapter(adapters, item.Adapter, item.AdapterKind);
                SplitRule rule = new SplitRule();
                rule.Enabled = item.Enabled;
                rule.Kind = domain ? RuleKind.Domain : RuleKind.Ip;
                rule.Match = match;
                rule.AdapterId = adapter != null ? adapter.Id : "";
                rule.AdapterName = adapter != null ? adapter.Name : (item.Adapter ?? "");
                rule.Metric = item.Metric > 0 ? item.Metric : 1;
                rule.Persistent = item.Persistent;
                rule.LastIps = item.LastIps ?? "";
                rule.Status = _isAdmin ? (domain ? "待解析" : "待应用") : "需要管理员权限";
                _rules.Add(rule);
                addedRules++;
                if (adapter == null) unmatched.Add(match + " → " + (string.IsNullOrEmpty(rule.AdapterName) ? "未指定网卡" : rule.AdapterName));
            }

            foreach (ConfigProcess item in bundle.Processes)
            {
                string process = (item.Process ?? "").Trim();
                if (process.Length == 0 || FindBinding(process) != null)
                {
                    skippedBinds++;
                    continue;
                }
                AdapterInfo adapter = ConfigIO.ResolveAdapter(adapters, item.Adapter, item.AdapterKind);
                if (adapter == null)
                {
                    skippedBinds++;
                    unmatched.Add(process + " → " + (string.IsNullOrEmpty(item.Adapter) ? "未指定网卡" : item.Adapter));
                    continue;
                }
                ProcBinding binding = new ProcBinding();
                binding.Process = process;
                binding.AdapterId = adapter.Id;
                binding.AdapterName = adapter.Name;
                binding.Metric = item.Metric > 0 ? item.Metric : 1;
                _bindings.Add(binding);
                addedBinds++;
            }

            if (addedRules > 0)
            {
                _store.Save(_rules);
                RebuildRulesGrid();
            }
            if (addedBinds > 0)
            {
                _bindStore.Save(_bindings);
                RefreshProcMarkers();
            }

            string summary = "导入完成：新增 " + addedRules + " 条规则、" + addedBinds + " 个进程跟踪";
            if (skippedRules > 0 || skippedBinds > 0)
                summary += "；跳过重复/无效 " + (skippedRules + skippedBinds) + " 项";
            if (unmatched.Count > 0)
                summary += "；网卡未匹配 " + unmatched.Count + " 项（已保留原网卡名称）";
            ShowNotice(summary);
            if (addedRules > 0 && _isAdmin && !PreviewMode) ApplyAllRulesAsync();
            return summary;
        }

        private static string ResolveSummary(SplitRule rule)
        {
            if (!rule.IsDomain) return "";
            return (rule.LastIps ?? "").Replace(",", ", ");
        }

        private string ResolveAdapterName(SplitRule rule)
        {
            foreach (AdapterInfo a in _adapters)
            {
                if (a.Id == rule.AdapterId) return a.Name;
            }
            if (!string.IsNullOrEmpty(rule.AdapterName)) return rule.AdapterName + "（不可用）";
            return "（已移除）";
        }

        private static void StyleRuleStatus(DataGridViewCell cell, SplitRule rule)
        {
            if (rule.Status != null && rule.Status.StartsWith("已生效", StringComparison.Ordinal))
            {
                cell.Style.ForeColor = Colors.Green;
            }
            else if (rule.Status != null && rule.Status.StartsWith("已停用", StringComparison.Ordinal))
            {
                cell.Style.ForeColor = Colors.TextMuted;
            }
            else if (rule.Status != null && rule.Status.StartsWith("需要管理员", StringComparison.Ordinal))
            {
                cell.Style.ForeColor = Colors.Amber;
            }
            else
            {
                cell.Style.ForeColor = Colors.Red;
            }
            cell.ToolTipText = rule.Status;
        }

        private void RefreshAdapterCards(List<AdapterInfo> list)
        {
            if (list == null) return;
            _adapters = list;
            bool changed = false;
            string signature = "";
            HashSet<string> ids = new HashSet<string>();

            foreach (AdapterInfo a in list)
            {
                ids.Add(a.Id);
                signature += a.Id + "|" + a.Name + "|" + a.IPv4 + "|" + a.Gateway + "|" + (a.Up ? "1" : "0") + ";";
                AdapterCard card;
                if (!_cards.TryGetValue(a.Id, out card))
                {
                    card = new AdapterCard(a, S(315), S(132), _scale);
                    _cards[a.Id] = card;
                    _adapterFlow.Controls.Add(card);
                    changed = true;
                }
                card.UpdateValues(a);
            }

            List<string> remove = new List<string>();
            foreach (KeyValuePair<string, AdapterCard> kv in _cards)
            {
                if (!ids.Contains(kv.Key)) remove.Add(kv.Key);
            }
            foreach (string id in remove)
            {
                AdapterCard card = _cards[id];
                _cards.Remove(id);
                _adapterFlow.Controls.Remove(card);
                card.Dispose();
                changed = true;
            }

            if (changed || signature != _adapterSignature)
            {
                _adapterSignature = signature;
                UpdateAdapterCombo();
            }
        }

        private void UpdateAdapterCombo()
        {
            if (_adapterCombo == null) return;
            string selectedId = null;
            AdapterChoice current = _adapterCombo.SelectedItem as AdapterChoice;
            if (current != null) selectedId = current.Adapter.Id;

            _adapterCombo.BeginUpdate();
            try
            {
                _adapterCombo.Items.Clear();
                int selectIndex = -1;
                foreach (AdapterInfo a in _adapters)
                {
                    AdapterChoice choice = new AdapterChoice();
                    choice.Adapter = a;
                    _adapterCombo.Items.Add(choice);
                    if (a.Id == selectedId) selectIndex = _adapterCombo.Items.Count - 1;
                }
                if (selectIndex < 0)
                {
                    for (int i = 0; i < _adapterCombo.Items.Count; i++)
                    {
                        AdapterChoice c = (AdapterChoice)_adapterCombo.Items[i];
                        if (c.Adapter.Up)
                        {
                            selectIndex = i;
                            break;
                        }
                    }
                }
                if (selectIndex < 0 && _adapterCombo.Items.Count > 0) selectIndex = 0;
                _adapterCombo.SelectedIndex = selectIndex;
            }
            finally
            {
                _adapterCombo.EndUpdate();
            }
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            TrafficSnapshot snapshot = _monitor.GetSnapshot();
            if (snapshot == null) return;

            RefreshAdapterCards(snapshot.Adapters);
            if (_demoMode)
            {
                _connLabel.Text = "TCP 连接 " + snapshot.TcpConnections.ToString();
                _timeLabel.Text = snapshot.Timestamp.ToString("HH:mm:ss");
                return;
            }
            SyncProcGrid(snapshot.Processes, snapshot.ProcIps);
            SyncIpGrid(snapshot.Ips);
            TrackNewIps(snapshot.ProcIps);
            RefreshRuleProcesses(snapshot.ProcIps);

            _connLabel.Text = "TCP 连接 " + snapshot.TcpConnections.ToString();
            if (_notice != null && DateTime.Now < _noticeUntil) _statLabel.Text = _notice;
            else if (!string.IsNullOrEmpty(snapshot.StatsMessage)) _statLabel.Text = snapshot.StatsMessage;
            else _statLabel.Text = snapshot.PerProcessAvailable ? "进程/IP 统计：已开启" : "进程/IP 统计：未开启";
            if (_notice != null && DateTime.Now >= _noticeUntil) _notice = null;
            _timeLabel.Text = snapshot.Timestamp.ToString("HH:mm:ss");
        }

        private void SyncProcGrid(List<ProcTrafficRow> rows, List<ProcIpRow> procIps)
        {
            _procIpRows = procIps ?? new List<ProcIpRow>();

            Dictionary<string, DataGridViewRow> existing = new Dictionary<string, DataGridViewRow>();
            foreach (DataGridViewRow row in _procGrid.Rows)
            {
                string tag = row.Tag as string;
                if (tag != null && !IsChildTag(tag)) existing[tag] = row;
            }

            HashSet<string> keys = new HashSet<string>();
            foreach (ProcTrafficRow data in rows)
            {
                string key = data.Process + "|" + data.Adapter;
                if (!keys.Add(key)) continue;

                DataGridViewRow row;
                if (!existing.TryGetValue(key, out row))
                {
                    int index = AdapterInsertIndex(_procGrid, data.Adapter, _procAdapterDesc);
                    _procGrid.Rows.Insert(index, 1);
                    row = _procGrid.Rows[index];
                    row.Tag = key;
                }
                row.Cells[0].Value = ProcCellText(_expandedProc.Contains(key), data.Process, FindBinding(data.Process) != null);
                row.Cells[0].Tag = data.Process;
                row.Cells[1].Value = data.Adapter;
                row.Cells[2].Value = Fmt.PerSec(data.RxRate);
                row.Cells[3].Value = Fmt.PerSec(data.TxRate);
                row.Cells[4].Value = Fmt.Bytes(data.RxTotal);
                row.Cells[5].Value = Fmt.Bytes(data.TxTotal);
            }

            RemoveStaleParents(_procGrid, keys);

            List<DataGridViewRow> expandedParents = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in _procGrid.Rows)
            {
                string tag = row.Tag as string;
                if (tag == null || IsChildTag(tag)) continue;
                if (_expandedProc.Contains(tag)) expandedParents.Add(row);
            }
            foreach (DataGridViewRow row in expandedParents)
            {
                SyncProcChildren(row, FilterProcIps(row.Tag as string));
            }
        }

        private static bool IsChildTag(string tag)
        {
            return tag != null && tag.StartsWith(ChildTagPrefix, StringComparison.Ordinal);
        }

        private void RemoveStaleParents(DataGridView grid, HashSet<string> keys)
        {
            for (int i = grid.Rows.Count - 1; i >= 0; i--)
            {
                string tag = grid.Rows[i].Tag as string;
                if (tag == null)
                {
                    grid.Rows.RemoveAt(i);
                    continue;
                }
                if (IsChildTag(tag)) continue;
                if (keys.Contains(tag)) continue;

                for (int j = i + 1; j < grid.Rows.Count; )
                {
                    if (!IsChildTag(grid.Rows[j].Tag as string)) break;
                    grid.Rows.RemoveAt(j);
                }
                grid.Rows.RemoveAt(i);
                _expandedProc.Remove(tag);
            }
        }

        private List<ProcIpRow> FilterProcIps(string parentKey)
        {
            List<ProcIpRow> list = new List<ProcIpRow>();
            int idx = parentKey.LastIndexOf('|');
            if (idx <= 0) return list;
            string proc = parentKey.Substring(0, idx);
            string adapter = parentKey.Substring(idx + 1);
            foreach (ProcIpRow r in _procIpRows)
            {
                if (r.Process == proc && r.Adapter == adapter) list.Add(r);
            }
            list.Sort(delegate(ProcIpRow x, ProcIpRow y)
            {
                double a = (double)x.RxTotal + (double)x.TxTotal;
                double b = (double)y.RxTotal + (double)y.TxTotal;
                return b.CompareTo(a);
            });
            return list;
        }

        private void SyncProcChildren(DataGridViewRow parent, List<ProcIpRow> ips)
        {
            string parentKey = parent.Tag as string;
            if (parentKey == null) return;
            string prefix = ChildTagPrefix + parentKey + ChildTagSep;

            Dictionary<string, DataGridViewRow> existing = new Dictionary<string, DataGridViewRow>();
            int blockEnd = parent.Index + 1;
            while (blockEnd < _procGrid.Rows.Count)
            {
                DataGridViewRow row = _procGrid.Rows[blockEnd];
                string tag = row.Tag as string;
                if (!IsChildTag(tag) || !tag.StartsWith(prefix, StringComparison.Ordinal)) break;
                existing[tag] = row;
                blockEnd++;
            }

            HashSet<string> keys = new HashSet<string>();
            foreach (ProcIpRow data in ips)
            {
                string tag = prefix + data.RemoteIp;
                if (!keys.Add(tag)) continue;

                DataGridViewRow row;
                if (!existing.TryGetValue(tag, out row))
                {
                    _procGrid.Rows.Insert(blockEnd, 1);
                    row = _procGrid.Rows[blockEnd];
                    blockEnd++;
                    row.Tag = tag;
                    StyleChildRow(row);
                }
                row.Cells[0].Value = "    " + data.RemoteIp;
                row.Cells[0].Tag = data.RemoteIp;
                row.Cells[1].Value = data.Adapter;
                row.Cells[2].Value = Fmt.PerSec(data.RxRate);
                row.Cells[3].Value = Fmt.PerSec(data.TxRate);
                row.Cells[4].Value = Fmt.Bytes(data.RxTotal);
                row.Cells[5].Value = Fmt.Bytes(data.TxTotal);
            }

            for (int j = parent.Index + 1; j < _procGrid.Rows.Count; )
            {
                string tag = _procGrid.Rows[j].Tag as string;
                if (!IsChildTag(tag) || !tag.StartsWith(prefix, StringComparison.Ordinal)) break;
                if (keys.Contains(tag)) j++;
                else _procGrid.Rows.RemoveAt(j);
            }
        }

        private static void StyleChildRow(DataGridViewRow row)
        {
            row.DefaultCellStyle.ForeColor = Color.FromArgb(94, 94, 104);
            row.DefaultCellStyle.BackColor = Color.FromArgb(250, 250, 253);
        }

        private void ClearChildRows(DataGridView grid)
        {
            for (int i = grid.Rows.Count - 1; i >= 0; i--)
            {
                if (IsChildTag(grid.Rows[i].Tag as string)) grid.Rows.RemoveAt(i);
            }
        }

        private void RefreshExpandedChildren()
        {
            List<DataGridViewRow> parents = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in _procGrid.Rows)
            {
                string tag = row.Tag as string;
                if (tag == null || IsChildTag(tag)) continue;
                if (_expandedProc.Contains(tag)) parents.Add(row);
            }
            foreach (DataGridViewRow row in parents)
            {
                SyncProcChildren(row, FilterProcIps(row.Tag as string));
            }
        }

        private void ToggleProcChildren(DataGridViewRow row)
        {
            string key = row.Tag as string;
            if (key == null || IsChildTag(key)) return;
            if (_expandedProc.Contains(key))
            {
                _expandedProc.Remove(key);
                string prefix = ChildTagPrefix + key + ChildTagSep;
                int j = row.Index + 1;
                while (j < _procGrid.Rows.Count)
                {
                    string tag = _procGrid.Rows[j].Tag as string;
                    if (!IsChildTag(tag)) break;
                    if (tag.StartsWith(prefix, StringComparison.Ordinal)) _procGrid.Rows.RemoveAt(j);
                    else j++;
                }
            }
            else
            {
                _expandedProc.Add(key);
                SyncProcChildren(row, FilterProcIps(key));
            }

            string proc = row.Cells[0].Tag as string;
            if (proc == null) proc = key;
            row.Cells[0].Value = ProcCellText(_expandedProc.Contains(key), proc, FindBinding(proc) != null);
        }

        private void ProcGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            DataGridViewRow row = _procGrid.Rows[e.RowIndex];
            string tag = row.Tag as string;
            if (tag == null || IsChildTag(tag)) return;
            ToggleProcChildren(row);
        }

        private void ProcGrid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            DataGridViewRow row = _procGrid.Rows[e.RowIndex];
            int column = e.ColumnIndex < 0 ? 0 : e.ColumnIndex;
            _procGrid.CurrentCell = row.Cells[column];
            if (_procMenu != null)
            {
                ContextMenuStrip old = _procMenu;
                _procMenu = null;
                old.Dispose();
            }
            ContextMenuStrip menu = BuildProcMenu(row);
            _procMenu = menu;
            menu.Show(Cursor.Position);
        }

        private ContextMenuStrip BuildProcMenu(DataGridViewRow row)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = new Font("Microsoft YaHei UI", 9F);
            string tag = row.Tag as string;

            if (IsChildTag(tag))
            {
                string ip = row.Cells[0].Tag as string;
                if (string.IsNullOrEmpty(ip)) ip = row.Cells[0].Value as string;
                ip = (ip ?? "").Trim();
                ToolStripMenuItem add = new ToolStripMenuItem("添加 IP " + ip + " 到分流规则");
                AddAdapterTargets(add, new List<string>(new string[] { ip }), row.Cells[1].Value as string);
                menu.Items.Add(add);

                string owner = ChildTagProcess(tag);
                if (!string.IsNullOrEmpty(owner))
                {
                    ProcBinding ownerBinding = FindBinding(owner);
                    if (ownerBinding == null)
                    {
                        ToolStripMenuItem trackOne = new ToolStripMenuItem("自动跟踪 " + owner + " 的新 IP（持续加入规则）");
                        AddTrackTargets(trackOne, owner, new List<string>(new string[] { ip }), row.Cells[1].Value as string);
                        menu.Items.Add(trackOne);
                    }
                    else
                    {
                        ToolStripMenuItem stopOne = new ToolStripMenuItem("停止自动跟踪 " + owner + "（→ " + ownerBinding.AdapterName + "）");
                        stopOne.Click += delegate { StopProcTracking(owner); };
                        menu.Items.Add(stopOne);
                    }
                }
                return menu;
            }

            List<ProcIpRow> ips = tag == null ? new List<ProcIpRow>() : FilterProcIps(tag);
            List<string> ipList = new List<string>();
            foreach (ProcIpRow ipRow in ips) ipList.Add(ipRow.RemoteIp);

            string proc = row.Cells[0].Tag as string;
            if (proc == null) proc = row.Cells[0].Value as string;
            ToolStripMenuItem addAll = new ToolStripMenuItem();
            if (ipList.Count > 0) addAll.Text = "添加 " + proc + " 的 " + ipList.Count + " 个 IP 到分流规则";
            else addAll.Text = "添加 " + proc + " 的 IP 到分流规则（暂无数据）";
            addAll.Enabled = ipList.Count > 0;
            AddAdapterTargets(addAll, ipList, row.Cells[1].Value as string);
            menu.Items.Add(addAll);

            ProcBinding binding = FindBinding(proc);
            if (binding == null)
            {
                ToolStripMenuItem track = new ToolStripMenuItem("自动跟踪 " + proc + " 的新 IP（持续加入规则）");
                AddTrackTargets(track, proc, ipList, row.Cells[1].Value as string);
                menu.Items.Add(track);
            }
            else
            {
                ToolStripMenuItem stop = new ToolStripMenuItem("停止自动跟踪 " + proc + "（→ " + binding.AdapterName + "）");
                stop.Click += delegate { StopProcTracking(proc); };
                menu.Items.Add(stop);

                ToolStripMenuItem retarget = new ToolStripMenuItem("改为自动跟踪到…");
                AddTrackTargets(retarget, proc, ipList, row.Cells[1].Value as string);
                menu.Items.Add(retarget);
            }

            menu.Items.Add(new ToolStripSeparator());
            bool expanded = tag != null && _expandedProc.Contains(tag);
            ToolStripMenuItem toggle = new ToolStripMenuItem(expanded ? "收起 IP 列表" : "展开 IP 列表");
            toggle.Click += delegate { ToggleProcChildren(row); };
            menu.Items.Add(toggle);
            return menu;
        }

        private void AddAdapterTargets(ToolStripMenuItem parent, List<string> ips, string currentAdapter)
        {
            int count = 0;
            foreach (AdapterInfo adapter in _adapters)
            {
                AdapterInfo target = adapter;
                string label = "分流到 " + target.Name + "（" + target.IPv4 + "）";
                if (target.Name == currentAdapter) label += "  · 当前";
                ToolStripMenuItem item = new ToolStripMenuItem(label);
                item.Click += delegate { AddIpRules(ips, target); };
                parent.DropDownItems.Add(item);
                count++;
            }
            if (count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（未检测到可用网卡）");
                none.Enabled = false;
                parent.DropDownItems.Add(none);
            }
        }

        private void AddTrackTargets(ToolStripMenuItem parent, string proc, List<string> ips, string currentAdapter)
        {
            int count = 0;
            foreach (AdapterInfo adapter in _adapters)
            {
                AdapterInfo target = adapter;
                string label = "自动跟踪到 " + target.Name + "（" + target.IPv4 + "）";
                if (target.Name == currentAdapter) label += "  · 当前";
                ToolStripMenuItem item = new ToolStripMenuItem(label);
                List<string> initial = ips;
                item.Click += delegate { StartProcTracking(proc, target, initial); };
                parent.DropDownItems.Add(item);
                count++;
            }
            if (count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("（未检测到可用网卡）");
                none.Enabled = false;
                parent.DropDownItems.Add(none);
            }
        }

        private static string ChildTagProcess(string tag)
        {
            if (!IsChildTag(tag)) return null;
            string body = tag.Substring(ChildTagPrefix.Length);
            int sep = body.IndexOf(ChildTagSep);
            if (sep >= 0) body = body.Substring(0, sep);
            int bar = body.LastIndexOf('|');
            return bar > 0 ? body.Substring(0, bar) : body;
        }

        private void AddIpRules(List<string> ips, AdapterInfo target)
        {
            AddIpRules(ips, target, false);
        }

        private void AddIpRules(List<string> ips, AdapterInfo target, bool forcePersistent)
        {
            if (ips == null || target == null) return;
            int added = 0;
            int skipped = 0;
            foreach (string text in ips)
            {
                System.Net.IPAddress network;
                int prefix;
                string error;
                if (!RouteManager.TryParseRule(text, out network, out prefix, out error)) continue;
                string canonical = RouteManager.ToCidr(network, prefix);
                if (FindRule(canonical) != null)
                {
                    skipped++;
                    continue;
                }

                SplitRule rule = new SplitRule();
                rule.Enabled = true;
                rule.Kind = RuleKind.Ip;
                rule.Match = canonical;
                rule.AdapterId = target.Id;
                rule.AdapterName = target.Name;
                rule.Metric = 1;
                rule.Persistent = forcePersistent || _persistentCheck.Checked;
                rule.Status = _isAdmin ? "待应用" : "需要管理员权限";
                _rules.Add(rule);
                added++;
            }

            if (added == 0)
            {
                _statLabel.Text = skipped > 0 ? "规则已存在，未重复添加" : "没有可添加的 IP";
                return;
            }

            _store.Save(_rules);
            RebuildRulesGrid();
            _statLabel.Text = "已添加 " + added + " 条分流规则" + (skipped > 0 ? "，跳过 " + skipped + " 条重复" : "");
            if (_isAdmin && !PreviewMode) ApplyAllRulesAsync();
        }

        private SplitRule FindRule(string match)
        {
            foreach (SplitRule rule in _rules)
            {
                if (rule.Match == match) return rule;
            }
            return null;
        }

        private void ShowNotice(string text)
        {
            _notice = text;
            _noticeUntil = DateTime.Now.AddSeconds(6);
            _statLabel.Text = text;
        }

        internal ProcBinding FindBinding(string process)
        {
            if (string.IsNullOrEmpty(process)) return null;
            foreach (ProcBinding b in _bindings)
            {
                if (string.Equals(b.Process, process, StringComparison.OrdinalIgnoreCase)) return b;
            }
            return null;
        }

        private static string ProcCellText(bool expanded, string proc, bool tracked)
        {
            return (expanded ? "▾ " : "▸ ") + proc + (tracked ? "  ⟳" : "");
        }

        private void RefreshProcMarkers()
        {
            foreach (DataGridViewRow row in _procGrid.Rows)
            {
                string tag = row.Tag as string;
                if (tag == null || IsChildTag(tag)) continue;
                string proc = row.Cells[0].Tag as string;
                if (string.IsNullOrEmpty(proc))
                {
                    int idx = tag.LastIndexOf('|');
                    proc = idx > 0 ? tag.Substring(0, idx) : tag;
                }
                row.Cells[0].Value = ProcCellText(_expandedProc.Contains(tag), proc, FindBinding(proc) != null);
            }
        }

        internal void StartProcTracking(string process, AdapterInfo target, List<string> ips)
        {
            if (string.IsNullOrEmpty(process) || target == null) return;
            ProcBinding binding = FindBinding(process);
            if (binding == null)
            {
                binding = new ProcBinding();
                binding.Process = process;
                _bindings.Add(binding);
            }
            binding.AdapterId = target.Id;
            binding.AdapterName = target.Name;
            binding.Metric = 1;
            _bindStore.Save(_bindings);

            if (ips != null && ips.Count > 0) AddIpRules(ips, target, true);
            RefreshProcMarkers();

            ShowNotice("正在自动跟踪 " + process + " 的新 IP → " + target.Name +
                       (_isAdmin || PreviewMode ? "；后续新 IP 会自动加入分流规则" : "；只读模式下仅记录当前 IP"));
        }

        internal void StopProcTracking(string process)
        {
            int removed = 0;
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_bindings[i].Process, process, StringComparison.OrdinalIgnoreCase)) continue;
                _bindings.RemoveAt(i);
                removed++;
            }
            if (removed == 0) return;
            _bindStore.Save(_bindings);
            RefreshProcMarkers();
            ShowNotice("已停止自动跟踪 " + process + "（已加入的分流规则保留）");
        }

        internal int TrackNewIps(List<ProcIpRow> procIps)
        {
            if (_bindings.Count == 0 || procIps == null || procIps.Count == 0) return 0;

            Dictionary<string, ProcBinding> map = new Dictionary<string, ProcBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcBinding b in _bindings) map[b.Process] = b;

            HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
            foreach (SplitRule rule in _rules) known.Add(rule.Match ?? "");

            int added = 0;
            List<string> touched = new List<string>();
            foreach (ProcIpRow row in procIps)
            {
                ProcBinding binding;
                if (!map.TryGetValue(row.Process ?? "", out binding)) continue;
                System.Net.IPAddress network;
                int prefix;
                string error;
                if (!RouteManager.TryParseRule(row.RemoteIp, out network, out prefix, out error)) continue;
                string canonical = RouteManager.ToCidr(network, prefix);
                if (!known.Add(canonical)) continue;

                SplitRule rule = new SplitRule();
                rule.Enabled = true;
                rule.Kind = RuleKind.Ip;
                rule.Match = canonical;
                rule.AdapterId = binding.AdapterId;
                rule.AdapterName = binding.AdapterName;
                rule.Metric = binding.Metric;
                rule.Persistent = true;
                rule.Status = _isAdmin ? "待应用" : "需要管理员权限";
                _rules.Add(rule);
                added++;
                if (!touched.Contains(row.Process)) touched.Add(row.Process);
            }

            if (added == 0) return 0;
            _store.Save(_rules);
            RebuildRulesGrid();
            ShowNotice("自动跟踪新增 " + added + " 条分流规则（" + string.Join("、", touched.ToArray()) + "）" +
                       (_isAdmin ? "" : "；需要管理员权限才会生效"));
            if (_isAdmin && !PreviewMode) ApplyAllRulesAsync();
            return added;
        }

        private void SyncIpGrid(List<IpTrafficRow> rows)
        {
            Dictionary<string, DataGridViewRow> existing = new Dictionary<string, DataGridViewRow>();
            foreach (DataGridViewRow row in _ipGrid.Rows)
            {
                string tag = row.Tag as string;
                if (tag != null) existing[tag] = row;
            }

            HashSet<string> keys = new HashSet<string>();
            foreach (IpTrafficRow data in rows)
            {
                string key = data.RemoteIp + "|" + data.Adapter;
                if (!keys.Add(key)) continue;

                DataGridViewRow row;
                if (!existing.TryGetValue(key, out row))
                {
                    int index = AdapterInsertIndex(_ipGrid, data.Adapter, _ipAdapterDesc);
                    _ipGrid.Rows.Insert(index, 1);
                    row = _ipGrid.Rows[index];
                    row.Tag = key;
                }
                row.Cells[0].Value = data.RemoteIp;
                row.Cells[1].Value = data.Adapter;
                row.Cells[2].Value = Fmt.PerSec(data.RxRate);
                row.Cells[3].Value = Fmt.PerSec(data.TxRate);
                row.Cells[4].Value = Fmt.Bytes(data.RxTotal);
                row.Cells[5].Value = Fmt.Bytes(data.TxTotal);
            }

            RemoveStaleRows(_ipGrid, keys);
        }

        private static void RemoveStaleRows(DataGridView grid, HashSet<string> keys)
        {
            for (int i = grid.Rows.Count - 1; i >= 0; i--)
            {
                string tag = grid.Rows[i].Tag as string;
                if (tag == null || !keys.Contains(tag)) grid.Rows.RemoveAt(i);
            }
        }

        private static int CompareAdapters(string a, string b)
        {
            return string.Compare(a ?? "", b ?? "", StringComparison.CurrentCultureIgnoreCase);
        }

        private static string AdapterText(DataGridViewRow row)
        {
            string text = row.Cells[1].Value as string;
            return text ?? "";
        }

        private static int AdapterInsertIndex(DataGridView grid, string adapter, bool desc)
        {
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                int cmp = CompareAdapters(adapter, AdapterText(grid.Rows[i]));
                if (desc ? cmp > 0 : cmp < 0) return i;
            }
            return grid.Rows.Count;
        }

        private void ToggleAdapterSort(DataGridView grid, ref bool desc, int columnIndex)
        {
            if (columnIndex != 1) return;
            desc = !desc;
            grid.Columns[1].HeaderCell.SortGlyphDirection = desc ? SortOrder.Descending : SortOrder.Ascending;

            bool isProc = ReferenceEquals(grid, _procGrid);
            if (isProc) ClearChildRows(grid);
            ApplyAdapterOrder(grid, desc);
            if (isProc) RefreshExpandedChildren();
        }

        private static void ApplyAdapterOrder(DataGridView grid, bool desc)
        {
            List<DataGridViewRow> source = new List<DataGridViewRow>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (IsChildTag(row.Tag as string)) continue;
                source.Add(row);
            }

            int count = source.Count;
            if (count < 2) return;

            int[] order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;

            for (int i = 1; i < count; i++)
            {
                int current = order[i];
                string adapter = AdapterText(source[current]);
                int j = i - 1;
                while (j >= 0)
                {
                    int cmp = CompareAdapters(adapter, AdapterText(source[order[j]]));
                    if (desc) cmp = -cmp;
                    if (cmp >= 0) break;
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = current;
            }

            List<object[]> values = new List<object[]>();
            List<object> tags = new List<object>();
            for (int i = 0; i < count; i++)
            {
                DataGridViewRow row = source[order[i]];
                object[] cells = new object[grid.Columns.Count];
                for (int c = 0; c < cells.Length; c++) cells[c] = row.Cells[c].Value;
                values.Add(cells);
                tags.Add(row.Tag);
            }

            grid.Rows.Clear();
            for (int i = 0; i < values.Count; i++)
            {
                DataGridViewRow row = grid.Rows[grid.Rows.Add()];
                for (int c = 0; c < values[i].Length; c++) row.Cells[c].Value = values[i][c];
                row.Tag = tags[i];
            }
        }

        internal void LoadDemoData()
        {
            _demoMode = true;

            string id0 = _adapters.Count > 0 ? _adapters[0].Id : "adapter-1";
            string name0 = _adapters.Count > 0 ? _adapters[0].Name : "WLAN";
            string id1 = _adapters.Count > 1 ? _adapters[1].Id : "adapter-2";
            string name1 = _adapters.Count > 1 ? _adapters[1].Name : "以太网";

            _rules.Clear();
            _rules.Add(DemoRule("8.8.8.8", id0, name0, "已生效", true));
            _rules.Add(DemoRule("142.250.0.0/16", id1, name1, "已生效", true));
            _rules.Add(DemoRule("203.0.113.0/24", id0, name0, "已生效（注意: 其他网卡存在同网段路由）", true));
            SplitRule domain = DemoRule("www.github.com", id1, name1, "已生效（2 个 IP，新增 2）", true);
            domain.Kind = RuleKind.Domain;
            domain.LastIps = "20.205.243.166,140.82.112.4";
            _rules.Add(domain);
            _rules.Add(DemoRule("198.51.100.0/24", id1, name1, "需要管理员权限", false));
            RebuildRulesGrid();

            _procGrid.Rows.Clear();
            _ipGrid.Rows.Clear();
            _expandedProc.Clear();
            _procIpRows = new List<ProcIpRow>();
            AddDemoProcIp("chrome", name0, "142.250.72.14", 1800000, 78000, 900000000, 40000000);
            AddDemoProcIp("chrome", name0, "142.250.72.46", 600000, 26000, 300000000, 12000000);
            AddDemoProcIp("chrome", name0, "8.8.8.8", 12000, 4000, 6000000, 900000);
            AddDemoProcIp("WeChat", name1, "203.0.113.9", 180000, 22000, 240000000, 15000000);
            AddDemoProcIp("msedge", name0, "13.107.42.14", 0, 12000, 600000000, 9000000);
            AddDemoProcIp("msedge", name0, "20.205.243.166", 0, 6000, 120000000, 3000000);

            AddDemoRow(_procGrid, new string[] { "chrome", name0, "2.4 MB/s", "310 KB/s", "1.2 GB", "88 MB" });
            AddDemoRow(_procGrid, new string[] { "WeChat", name1, "180 KB/s", "22 KB/s", "240 MB", "15 MB" });
            AddDemoRow(_procGrid, new string[] { "msedge", name0, "0 B/s", "12 KB/s", "600 MB", "9 MB" });
            AddDemoRow(_ipGrid, new string[] { "142.250.72.14", name0, "1.8 MB/s", "90 KB/s", "820 MB", "42 MB" });
            AddDemoRow(_ipGrid, new string[] { "203.0.113.9", name1, "210 KB/s", "30 KB/s", "340 MB", "18 MB" });
            AddDemoRow(_ipGrid, new string[] { "8.8.8.8", name0, "12 KB/s", "4 KB/s", "6 MB", "900 KB" });

            foreach (DataGridViewRow row in _procGrid.Rows)
            {
                if (row.Tag as string == "chrome|" + name0)
                {
                    ToggleProcChildren(row);
                    break;
                }
            }
            RefreshRuleProcesses(_procIpRows);
        }

        private void AddDemoProcIp(string process, string adapter, string ip, double rxRate, double txRate, ulong rxTotal, ulong txTotal)
        {
            ProcIpRow row = new ProcIpRow();
            row.Process = process;
            row.Adapter = adapter;
            row.RemoteIp = ip;
            row.RxRate = rxRate;
            row.TxRate = txRate;
            row.RxTotal = rxTotal;
            row.TxTotal = txTotal;
            _procIpRows.Add(row);
        }

        private static SplitRule DemoRule(string match, string adapterId, string adapterName, string status, bool ok)
        {
            SplitRule rule = new SplitRule();
            rule.Match = match;
            rule.AdapterId = adapterId;
            rule.AdapterName = adapterName;
            rule.Status = status;
            rule.Ok = ok;
            return rule;
        }

        private void AddDemoRow(DataGridView grid, string[] values)
        {
            bool desc = grid == _ipGrid ? _ipAdapterDesc : _procAdapterDesc;
            int index = AdapterInsertIndex(grid, values[1], desc);
            grid.Rows.Insert(index, 1);
            DataGridViewRow row = grid.Rows[index];
            for (int i = 0; i < values.Length; i++) row.Cells[i].Value = values[i];
            row.Tag = values[0] + "|" + values[1];
            if (ReferenceEquals(grid, _procGrid))
            {
                row.Cells[0].Value = "▸ " + values[0];
                row.Cells[0].Tag = values[0];
            }
        }

        private sealed class AdapterChoice
        {
            public AdapterInfo Adapter;

            public override string ToString()
            {
                if (Adapter == null) return "";
                return Adapter.Display;
            }
        }
    }
}
