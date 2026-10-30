using System;
using System.Drawing;
using System.Windows.Forms;

namespace NetSplitter
{
    internal sealed class AdapterCard : Panel
    {
        private readonly float _scale;
        private readonly Label _name;
        private readonly Label _status;
        private readonly Label _desc;
        private readonly Label _ip;
        private readonly Label _gw;
        private readonly Label _rx;
        private readonly Label _tx;
        private readonly Label _total;
        private readonly ToolTip _tip;
        public readonly string AdapterId;

        public AdapterCard(AdapterInfo a, int width, int height, float scale)
        {
            _scale = scale;
            AdapterId = a.Id;
            DoubleBuffered = true;
            BackColor = Color.White;
            Width = width;
            Height = height;
            Margin = new Padding(S(6));
            _tip = new ToolTip();

            _name = MakeLabel(string.Empty, 10.5F, FontStyle.Bold, Colors.TextDark);
            _name.AutoEllipsis = true;
            _name.SetBounds(S(12), S(8), width - S(100), S(22));
            Controls.Add(_name);

            _status = MakeLabel(string.Empty, 8F, FontStyle.Regular, Colors.Green);
            _status.TextAlign = ContentAlignment.MiddleRight;
            _status.SetBounds(width - S(94), S(9), S(82), S(20));
            Controls.Add(_status);

            _desc = MakeLabel(string.Empty, 8F, FontStyle.Regular, Colors.TextMuted);
            _desc.AutoEllipsis = true;
            _desc.SetBounds(S(12), S(32), width - S(24), S(16));
            Controls.Add(_desc);

            _ip = MakeLabel(string.Empty, 8.5F, FontStyle.Regular, Colors.TextDark);
            _ip.SetBounds(S(12), S(53), width - S(24), S(18));
            Controls.Add(_ip);

            _gw = MakeLabel(string.Empty, 8F, FontStyle.Regular, Colors.TextMuted);
            _gw.AutoEllipsis = true;
            _gw.SetBounds(S(12), S(73), width - S(24), S(16));
            Controls.Add(_gw);

            int half = (width - S(24)) / 2;
            _rx = MakeLabel(string.Empty, 9.5F, FontStyle.Bold, Colors.Blue);
            _rx.SetBounds(S(12), S(92), half, S(20));
            Controls.Add(_rx);

            _tx = MakeLabel(string.Empty, 9.5F, FontStyle.Bold, Colors.Green);
            _tx.SetBounds(S(12) + half, S(92), half, S(20));
            Controls.Add(_tx);

            _total = MakeLabel(string.Empty, 8F, FontStyle.Regular, Colors.TextMuted);
            _total.SetBounds(S(12), S(113), width - S(24), S(16));
            Controls.Add(_total);

            UpdateValues(a);
            Paint += CardPaint;
        }

        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = false;
            label.Font = new Font("Microsoft YaHei UI", size, style);
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.TextAlign = ContentAlignment.MiddleLeft;
            return label;
        }

        public void UpdateValues(AdapterInfo a)
        {
            if (a == null) return;
            _name.Text = a.Name;
            _status.Text = a.Up ? "● 已连接" : "● 未连接";
            _status.ForeColor = a.Up ? Colors.Green : Colors.TextMuted;
            _desc.Text = a.IsWifi ? WifiText(a) : a.Description;
            if (a.IsWifi) _desc.ForeColor = string.IsNullOrEmpty(a.WifiSsid) ? Colors.TextMuted : Colors.Blue;
            _ip.Text = "IP  " + a.IPv4 + "/" + a.PrefixLength;
            string gw = string.IsNullOrEmpty(a.Gateway) ? "无网关" : a.Gateway;
            _gw.Text = "网关 " + gw + "   ·   " + Fmt.LinkSpeed(a.Speed) + "   ·   idx " + a.IfIndex;
            _rx.Text = "↓ " + Fmt.PerSec(a.RxRate);
            _tx.Text = "↑ " + Fmt.PerSec(a.TxRate);
            _total.Text = "累计  ↓ " + Fmt.Bytes(a.RxTotal) + "   ↑ " + Fmt.Bytes(a.TxTotal);
            _tip.SetToolTip(_desc, a.Description);
        }

        public static string WifiText(AdapterInfo a)
        {
            if (string.IsNullOrEmpty(a.WifiSsid)) return "Wi-Fi  未连接";
            string text = "Wi-Fi  " + a.WifiSsid;
            if (a.WifiSignal > 0) text += "   ·   信号 " + a.WifiSignal + "%";
            return text;
        }

        private void CardPaint(object sender, PaintEventArgs e)
        {
            using (Pen pen = new Pen(Colors.Line))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    internal static class Colors
    {
        public static readonly Color TextDark = Color.FromArgb(17, 24, 39);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        public static readonly Color Line = Color.FromArgb(229, 231, 235);
        public static readonly Color Blue = Color.FromArgb(37, 99, 235);
        public static readonly Color Green = Color.FromArgb(5, 150, 105);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Amber = Color.FromArgb(180, 83, 9);
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color SoftBg = Color.FromArgb(249, 250, 251);
        public static readonly Color BannerBg = Color.FromArgb(254, 243, 199);
        public static readonly Color Selection = Color.FromArgb(219, 234, 254);
    }
}
