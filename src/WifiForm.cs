using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetSplitter
{
    internal sealed class WifiForm : Form
    {
        private readonly float _scale;
        private readonly ListView _list;
        private readonly Label _statusLabel;
        private readonly Label _targetLabel;
        private readonly Label _hint;
        private readonly TextBox _password;
        private readonly Button _scanButton;
        private readonly Button _connectButton;
        private readonly Button _disconnectButton;
        private readonly Button _targetAButton;
        private readonly Button _targetBButton;
        private readonly Button _closeButton;
        private readonly ToolTip _tip;
        private List<WifiNetwork> _networks = new List<WifiNetwork>();
        private bool _busy;

        public bool WifiChanged;
        public string TargetFirst;
        public string TargetSecond;
        public bool ScanCompleted;

        public WifiForm(float scale, string first, string second)
        {
            _scale = scale;
            TargetFirst = first ?? "";
            TargetSecond = second ?? "";

            Text = "Wi-Fi 快速切换";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Colors.Bg;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            ClientSize = new Size(S(620), S(470));

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = S(76);
            header.BackColor = Color.White;
            header.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Colors.Line))
                {
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                }
            };

            _statusLabel = new Label();
            _statusLabel.Text = "当前连接：读取中…";
            _statusLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            _statusLabel.ForeColor = Colors.TextDark;
            _statusLabel.AutoSize = false;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.SetBounds(S(18), S(10), ClientSize.Width - S(36), S(24));
            header.Controls.Add(_statusLabel);

            _targetLabel = new Label();
            _targetLabel.Font = new Font("Microsoft YaHei UI", 8.8F);
            _targetLabel.ForeColor = Colors.TextMuted;
            _targetLabel.AutoSize = false;
            _targetLabel.AutoEllipsis = true;
            _targetLabel.SetBounds(S(18), S(40), ClientSize.Width - S(36), S(20));
            header.Controls.Add(_targetLabel);

            Panel footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = S(30);
            footer.BackColor = Colors.Bg;

            _hint = new Label();
            _hint.Text = "";
            _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
            _hint.ForeColor = Colors.TextMuted;
            _hint.AutoSize = false;
            _hint.AutoEllipsis = true;
            _hint.SetBounds(S(18), S(5), ClientSize.Width - S(36), S(20));
            footer.Controls.Add(_hint);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(S(16), S(10), S(16), S(8));
            content.BackColor = Colors.Bg;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));
            layout.BackColor = Colors.Bg;

            _list = new ListView();
            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.MultiSelect = false;
            _list.HideSelection = false;
            _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.BackColor = Color.White;
            _list.Font = new Font("Microsoft YaHei UI", 9F);
            _list.Margin = new Padding(0);
            _list.Columns.Add("网络名称", S(268));
            _list.Columns.Add("信号", S(70));
            _list.Columns.Add("安全", S(100));
            _list.Columns.Add("状态", S(118));
            _list.DoubleClick += delegate { ConnectClicked(); };
            layout.Controls.Add(_list, 0, 0);

            FlowLayoutPanel passwordRow = new FlowLayoutPanel();
            passwordRow.Dock = DockStyle.Fill;
            passwordRow.WrapContents = false;
            passwordRow.BackColor = Colors.Bg;
            passwordRow.Margin = new Padding(0);

            Label passwordTitle = new Label();
            passwordTitle.Text = "密码";
            passwordTitle.AutoSize = true;
            passwordTitle.ForeColor = Colors.TextDark;
            passwordTitle.Margin = new Padding(0, S(7), S(8), 0);
            passwordRow.Controls.Add(passwordTitle);

            _password = new TextBox();
            _password.Width = S(230);
            _password.Font = new Font("Microsoft YaHei UI", 9.5F);
            _password.UseSystemPasswordChar = true;
            _password.BorderStyle = BorderStyle.FixedSingle;
            _password.Margin = new Padding(0, S(4), S(10), 0);
            passwordRow.Controls.Add(_password);

            Label passwordHint = new Label();
            passwordHint.Text = "未保存过的加密网络填写密码；已保存的留空";
            passwordHint.AutoSize = true;
            passwordHint.ForeColor = Colors.TextMuted;
            passwordHint.Font = new Font("Microsoft YaHei UI", 8.5F);
            passwordHint.Margin = new Padding(0, S(8), 0, 0);
            passwordRow.Controls.Add(passwordHint);
            layout.Controls.Add(passwordRow, 0, 1);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Fill;
            buttons.WrapContents = false;
            buttons.BackColor = Colors.Bg;
            buttons.Margin = new Padding(0);
            buttons.Padding = new Padding(0, S(4), 0, 0);

            _scanButton = MakeButton("刷新扫描", false);
            _scanButton.Click += delegate { BeginScan(); };
            buttons.Controls.Add(_scanButton);

            _connectButton = MakeButton("连接", true);
            _connectButton.Click += delegate { ConnectClicked(); };
            buttons.Controls.Add(_connectButton);

            _disconnectButton = MakeButton("断开", false);
            _disconnectButton.Click += delegate { DisconnectClicked(); };
            buttons.Controls.Add(_disconnectButton);

            _targetAButton = MakeButton("设为 A", false);
            _targetAButton.Click += delegate { SetTargetClicked(true); };
            buttons.Controls.Add(_targetAButton);

            _targetBButton = MakeButton("设为 B", false);
            _targetBButton.Click += delegate { SetTargetClicked(false); };
            buttons.Controls.Add(_targetBButton);

            _closeButton = MakeButton("关闭", false);
            _closeButton.Click += delegate { Close(); };
            buttons.Controls.Add(_closeButton);
            layout.Controls.Add(buttons, 0, 2);

            content.Controls.Add(layout);

            Controls.Add(content);
            Controls.Add(footer);
            Controls.Add(header);

            _tip = new ToolTip();
            _tip.SetToolTip(_targetAButton, "把选中的网络设为一键切换的目标 A（与目标 B 之间来回切换）");
            _tip.SetToolTip(_targetBButton, "把选中的网络设为一键切换的目标 B（与目标 A 之间来回切换）");
            _tip.SetToolTip(_targetLabel, "主界面「一键切换」会在目标 A 与目标 B 之间交替切换。");

            UpdateTargetLabel();
            Shown += delegate { BeginScan(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
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
            button.Margin = new Padding(0, 0, S(8), 0);
            button.Font = new Font("Microsoft YaHei UI", 9F);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void BeginScan()
        {
            if (_busy) return;
            ScanListAsync();
        }

        private async void ScanListAsync()
        {
            SetBusy(true);
            SetHint("正在扫描附近的 Wi-Fi…", Colors.Blue);
            WifiScanResult result;
            try
            {
                result = await Task.Run(delegate { return WifiService.Scan(2200); });
            }
            catch (Exception ex)
            {
                result = new WifiScanResult();
                result.Error = ex.Message;
            }
            if (IsDisposed) return;

            _networks = result.Networks ?? new List<WifiNetwork>();
            FillList();
            UpdateStatus();
            SetBusy(false);
            if (!string.IsNullOrEmpty(result.Error)) SetHint("扫描失败：" + result.Error, Colors.Red);
            else SetHint("发现 " + _networks.Count + " 个网络，双击列表可直接连接。", Colors.TextMuted);
            ScanCompleted = true;
        }

        private void FillList()
        {
            string selected = "";
            WifiNetwork current = Selected();
            if (current != null) selected = current.Ssid;

            List<WifiNetwork> sorted = new List<WifiNetwork>(_networks);
            sorted.Sort(delegate(WifiNetwork x, WifiNetwork y)
            {
                if (x.Connected != y.Connected) return x.Connected ? -1 : 1;
                if (x.Signal != y.Signal) return y.Signal - x.Signal;
                return string.Compare(x.Ssid, y.Ssid, StringComparison.CurrentCultureIgnoreCase);
            });

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                int selectIndex = -1;
                foreach (WifiNetwork net in sorted)
                {
                    ListViewItem item = new ListViewItem(net.Ssid);
                    item.Tag = net;
                    item.SubItems.Add(net.Signal > 0 ? net.Signal + "%" : "—");
                    item.SubItems.Add(net.Secure ? (string.IsNullOrEmpty(net.Auth) ? "加密" : net.Auth) : "开放");
                    item.SubItems.Add(net.Connected ? "已连接" : (net.HasProfile ? "已保存" : ""));
                    if (net.Connected) item.ForeColor = Colors.Green;
                    else if (!net.HasProfile && net.Signal == 0) item.ForeColor = Colors.TextMuted;
                    _list.Items.Add(item);
                    if (net.Ssid == selected) selectIndex = _list.Items.Count - 1;
                }
                if (selectIndex < 0 && _list.Items.Count > 0)
                {
                    WifiStatus status = WifiService.ReadStatus();
                    if (status.Connected)
                    {
                        for (int i = 0; i < _list.Items.Count; i++)
                        {
                            WifiNetwork net = _list.Items[i].Tag as WifiNetwork;
                            if (net != null && net.Ssid == status.Ssid)
                            {
                                selectIndex = i;
                                break;
                            }
                        }
                    }
                }
                if (selectIndex >= 0)
                {
                    _list.Items[selectIndex].Selected = true;
                    _list.Items[selectIndex].Focused = true;
                }
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        private WifiNetwork Selected()
        {
            if (_list.SelectedItems.Count == 0) return null;
            return _list.SelectedItems[0].Tag as WifiNetwork;
        }

        private void UpdateStatus()
        {
            WifiStatus status = WifiService.ReadStatus();
            if (!status.Available)
            {
                _statusLabel.Text = "无线网卡不可用";
                _statusLabel.ForeColor = Colors.Red;
                if (!string.IsNullOrEmpty(status.Error)) SetHint(status.Error, Colors.Red);
                return;
            }
            if (status.Connected)
            {
                _statusLabel.Text = "当前连接：" + status.Ssid +
                                    (status.Signal > 0 ? "   ·   信号 " + status.Signal + "%" : "");
                _statusLabel.ForeColor = Colors.Green;
            }
            else
            {
                _statusLabel.Text = "当前连接：未连接";
                _statusLabel.ForeColor = Colors.TextMuted;
            }
            UpdateTargetLabel();
        }

        private void UpdateTargetLabel()
        {
            bool hasFirst = TargetFirst.Length > 0;
            bool hasSecond = TargetSecond.Length > 0;
            if (!hasFirst && !hasSecond)
            {
                _targetLabel.Text = "一键切换：未配置  ·  选中网络后点「设为 A」「设为 B」，两个 Wi-Fi 互相切换";
                _targetLabel.ForeColor = Colors.Amber;
            }
            else if (hasFirst && hasSecond)
            {
                _targetLabel.Text = "一键切换：" + TargetFirst + "  ⇄  " + TargetSecond + "  ·  主界面点「一键切换」交替切换";
                _targetLabel.ForeColor = Colors.TextDark;
            }
            else
            {
                _targetLabel.Text = "一键切换：" + (hasFirst ? TargetFirst : TargetSecond) +
                                    "（只配置了一个，再设置另一个即可互相切换）";
                _targetLabel.ForeColor = Colors.Amber;
            }
            _targetLabel.AutoEllipsis = true;
        }

        private void SetHint(string text, Color color)
        {
            _hint.Text = text ?? "";
            _hint.ForeColor = color;
            _tip.SetToolTip(_hint, text ?? "");
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _scanButton.Enabled = !busy;
            _connectButton.Enabled = !busy;
            _disconnectButton.Enabled = !busy;
            _targetAButton.Enabled = !busy;
            _targetBButton.Enabled = !busy;
            _password.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private async void ConnectClicked()
        {
            if (_busy) return;
            WifiNetwork net = Selected();
            if (net == null)
            {
                SetHint("请先在列表中选择要连接的网络", Colors.Amber);
                return;
            }
            if (net.Connected)
            {
                SetHint("当前已连接 " + net.Ssid, Colors.Green);
                return;
            }

            string password = _password.Text;
            string mode;
            if (password.Length > 0)
            {
                if (password.Length < 8)
                {
                    SetHint("Wi-Fi 密码至少需要 8 位", Colors.Red);
                    _password.Focus();
                    return;
                }
                mode = "使用输入的密码连接";
            }
            else if (net.HasProfile)
            {
                password = null;
                mode = "使用系统中已保存的配置";
            }
            else if (net.Secure)
            {
                SetHint("该网络没有保存过，请先在「密码」框输入密码", Colors.Amber);
                _password.Focus();
                return;
            }
            else
            {
                password = "";
                mode = "开放网络，无需密码";
            }

            SetBusy(true);
            SetHint("正在连接 " + net.Ssid + "（" + mode + "）…", Colors.Blue);
            WifiResult result;
            try
            {
                result = await Task.Run(delegate { return WifiService.Connect(net.Ssid, password); });
            }
            catch (Exception ex)
            {
                result = new WifiResult();
                result.Error = ex.Message;
            }
            if (IsDisposed) return;
            SetBusy(false);

            if (!result.Ok)
            {
                SetHint("连接 " + net.Ssid + " 失败：" + result.Error, Colors.Red);
                MessageBox.Show(this, "连接 " + net.Ssid + " 失败：\n" + result.Error, "Wi-Fi 切换",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            WifiChanged = true;
            _password.Clear();
            SetHint("已连接到 " + net.Ssid, Colors.Green);
            MarkConnectedLocal(net.Ssid);
            FillList();
            UpdateStatus();
        }

        private void MarkConnectedLocal(string ssid)
        {
            foreach (WifiNetwork net in _networks)
            {
                net.Connected = net.Ssid == ssid;
            }
        }

        private async void DisconnectClicked()
        {
            if (_busy) return;
            WifiStatus status = WifiService.ReadStatus();
            if (!status.Connected)
            {
                SetHint("当前没有连接任何 Wi-Fi", Colors.Amber);
                return;
            }
            DialogResult confirm = MessageBox.Show(this,
                "断开后无线网络会立即中断（若正依靠 Wi-Fi 上网，本次操作会断网）。\n确定断开「" + status.Ssid + "」吗？",
                "Wi-Fi 切换", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            SetBusy(true);
            SetHint("正在断开 " + status.Ssid + " …", Colors.Blue);
            WifiResult result;
            try
            {
                result = await Task.Run(delegate { return WifiService.Disconnect(); });
            }
            catch (Exception ex)
            {
                result = new WifiResult();
                result.Error = ex.Message;
            }
            if (IsDisposed) return;
            SetBusy(false);

            if (!result.Ok)
            {
                SetHint("断开失败：" + result.Error, Colors.Red);
                return;
            }
            WifiChanged = true;
            MarkConnectedLocal("");
            FillList();
            UpdateStatus();
            SetHint("已断开无线连接", Colors.Green);
        }

        private void SetTargetClicked(bool firstSlot)
        {
            WifiNetwork net = Selected();
            if (net == null)
            {
                SetHint("请先在列表中选择要作为一个切换目标的网络", Colors.Amber);
                return;
            }
            string other = firstSlot ? TargetSecond : TargetFirst;
            if (other.Length > 0 && other == net.Ssid)
            {
                SetHint("「" + net.Ssid + "」已经是另一个目标了，一键切换需要两个不同的网络", Colors.Amber);
                return;
            }
            string first = firstSlot ? net.Ssid : TargetFirst;
            string second = firstSlot ? TargetSecond : net.Ssid;
            if (!WifiConfig.SavePair(first, second))
            {
                SetHint("保存一键切换目标失败，请检查 " + Logger.Dir + " 是否可写", Colors.Red);
                return;
            }
            TargetFirst = first;
            TargetSecond = second;
            UpdateTargetLabel();
            SetHint("已设为切换目标 " + (firstSlot ? "A" : "B") + "：" + net.Ssid, Colors.Green);
        }
    }
}
