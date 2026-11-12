using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SsmsSqlPlugin.History
{
    internal static class HistoryTheme
    {
        public static readonly Color Background = Color.FromArgb(244, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color CardSoft = Color.FromArgb(251, 252, 254);
        public static readonly Color Border = Color.FromArgb(226, 229, 234);
        public static readonly Color BorderStrong = Color.FromArgb(212, 217, 225);
        public static readonly Color TextPrimary = Color.FromArgb(31, 36, 48);
        public static readonly Color TextSecondary = Color.FromArgb(107, 114, 128);
        public static readonly Color TextMuted = Color.FromArgb(156, 163, 175);

        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
        public static readonly Color AccentSoft = Color.FromArgb(232, 240, 254);

        public static readonly Color Hover = Color.FromArgb(247, 249, 252);
        public static readonly Color Alternate = Color.FromArgb(251, 252, 253);
        public static readonly Color Selection = Color.FromArgb(234, 241, 255);
        public static readonly Color HeaderBackground = Color.FromArgb(248, 249, 251);

        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color GreenSoft = Color.FromArgb(220, 252, 231);
        public static readonly Color Amber = Color.FromArgb(180, 83, 9);
        public static readonly Color AmberSoft = Color.FromArgb(254, 243, 199);
        public static readonly Color Purple = Color.FromArgb(109, 40, 217);
        public static readonly Color PurpleSoft = Color.FromArgb(237, 233, 254);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color RedSoft = Color.FromArgb(254, 226, 226);
        public static readonly Color GraySoft = Color.FromArgb(243, 244, 246);

        public static readonly Font UiFont = MakeFont("Microsoft YaHei UI", 9f, FontStyle.Regular);
        public static readonly Font UiFontMedium = MakeFont("Microsoft YaHei UI", 9f, FontStyle.Bold);
        public static readonly Font SmallFont = MakeFont("Microsoft YaHei UI", 8.25f, FontStyle.Regular);
        public static readonly Font TitleFont = MakeFont("Microsoft YaHei UI", 15f, FontStyle.Bold);
        public static readonly Font MonoFont = new Font("Consolas", 10f);
        public static readonly Font BadgeFont = MakeFont("Microsoft YaHei UI", 8f, FontStyle.Regular);

        private static Font MakeFont(string family, float size, FontStyle style)
        {
            try { return new Font(family, size, style); }
            catch
            {
                try { return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style); }
                catch { return SystemFonts.MessageBoxFont; }
            }
        }

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = Math.Min(Math.Max(0, radius) * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter <= 1)
            {
                path.AddRectangle(bounds);
                return path;
            }

            var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>圆角卡片：填色 + 细边框，Region 裁掉四角。</summary>
    internal sealed class RoundedPanel : Panel
    {
        public int Radius { get; set; } = 10;
        public Color FillColor { get; set; } = HistoryTheme.Card;
        public Color BorderColor { get; set; } = HistoryTheme.Border;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = FillColor;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            try
            {
                using (var path = HistoryTheme.RoundedRect(
                    new Rectangle(0, 0, Width, Height), Radius))
                {
                    var old = Region;
                    Region = new Region(path);
                    old?.Dispose();
                }
            }
            catch { }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = HistoryTheme.RoundedRect(
                new Rectangle(0, 0, Width, Height), Radius))
            using (var brush = new SolidBrush(FillColor))
            {
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (var path = HistoryTheme.RoundedRect(
                new Rectangle(0, 0, Width - 1, Height - 1), Radius))
            using (var pen = new Pen(BorderColor))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }
    }

    internal enum ModernButtonStyle
    {
        Primary,
        Secondary,
        Ghost,
        Danger,
        Success,
    }

    /// <summary>扁平圆角按钮，自带 hover/pressed/disabled 状态。</summary>
    internal sealed class ModernButton : Button
    {
        private ModernButtonStyle _style = ModernButtonStyle.Secondary;
        private bool _hover;
        private bool _pressed;

        public int CornerRadius { get; set; } = 7;

        public ModernButtonStyle Style
        {
            get { return _style; }
            set { _style = value; Invalidate(); }
        }

        public ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Height = 32;
            Font = HistoryTheme.UiFont;
            Cursor = Cursors.Hand;
            BackColor = HistoryTheme.Card;
            ForeColor = HistoryTheme.TextPrimary;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            _pressed = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _pressed = false;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var parentBrush = new SolidBrush(Parent?.BackColor ?? HistoryTheme.Background))
            {
                g.FillRectangle(parentBrush, ClientRectangle);
            }

            Color fill, border, text;
            ResolveColors(out fill, out border, out text);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = HistoryTheme.RoundedRect(rect, CornerRadius))
            using (var fillBrush = new SolidBrush(fill))
            {
                g.FillPath(fillBrush, path);
                if (border.A > 0)
                {
                    using (var pen = new Pen(border))
                    {
                        g.DrawPath(pen, path);
                    }
                }
            }

            TextRenderer.DrawText(
                g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        private void ResolveColors(out Color fill, out Color border, out Color text)
        {
            if (!Enabled)
            {
                fill = HistoryTheme.GraySoft;
                border = HistoryTheme.Border;
                text = HistoryTheme.TextMuted;
                return;
            }

            switch (Style)
            {
                case ModernButtonStyle.Primary:
                    fill = _pressed ? HistoryTheme.AccentPressed
                         : _hover ? HistoryTheme.AccentHover
                         : HistoryTheme.Accent;
                    border = Color.Transparent;
                    text = Color.White;
                    break;

                case ModernButtonStyle.Ghost:
                    fill = _pressed ? HistoryTheme.GraySoft
                         : _hover ? HistoryTheme.Card
                         : (Parent?.BackColor ?? HistoryTheme.Background);
                    border = HistoryTheme.BorderStrong;
                    text = HistoryTheme.TextSecondary;
                    break;

                case ModernButtonStyle.Danger:
                    fill = _pressed ? HistoryTheme.RedSoft
                         : _hover ? Color.FromArgb(255, 245, 245)
                         : HistoryTheme.Card;
                    border = Color.FromArgb(241, 181, 181);
                    text = HistoryTheme.Red;
                    break;

                case ModernButtonStyle.Success:
                    fill = _pressed ? HistoryTheme.GreenSoft
                         : _hover ? Color.FromArgb(244, 252, 246)
                         : HistoryTheme.Card;
                    border = Color.FromArgb(167, 215, 181);
                    text = HistoryTheme.Green;
                    break;

                default:
                    fill = _pressed ? HistoryTheme.GraySoft
                         : _hover ? HistoryTheme.Hover
                         : HistoryTheme.Card;
                    border = HistoryTheme.BorderStrong;
                    text = HistoryTheme.TextPrimary;
                    break;
            }
        }
    }

    /// <summary>回收站开关：画成圆角 chip，保留 CheckBox 的 Checked 语义。</summary>
    internal sealed class ModernCheckBox : CheckBox
    {
        private bool _hover;

        public ModernCheckBox()
        {
            AutoSize = false;
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            Height = 32;
            Width = 100;
            Font = HistoryTheme.UiFont;
            Cursor = Cursors.Hand;
            BackColor = HistoryTheme.Card;
            ForeColor = HistoryTheme.TextSecondary;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var parentBrush = new SolidBrush(Parent?.BackColor ?? HistoryTheme.Card))
            {
                g.FillRectangle(parentBrush, ClientRectangle);
            }

            Color fill = Checked ? HistoryTheme.AccentSoft
                       : _hover ? HistoryTheme.Hover
                       : HistoryTheme.Card;
            Color border = Checked ? Color.FromArgb(191, 219, 254) : HistoryTheme.BorderStrong;
            Color text = Checked ? HistoryTheme.Accent : HistoryTheme.TextSecondary;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = HistoryTheme.RoundedRect(rect, 7))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            var dot = new Rectangle(11, (Height - 14) / 2, 14, 14);
            if (Checked)
            {
                using (var brush = new SolidBrush(HistoryTheme.Accent))
                {
                    g.FillEllipse(brush, dot);
                }

                using (var pen = new Pen(Color.White, 1.8f))
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(dot.Left + 3.4f, dot.Top + 7.2f),
                        new PointF(dot.Left + 6f, dot.Top + 9.8f),
                        new PointF(dot.Left + 10.8f, dot.Top + 4.4f),
                    });
                }
            }
            else
            {
                using (var pen = new Pen(HistoryTheme.TextMuted, 1.3f))
                {
                    g.DrawEllipse(pen, dot);
                }
            }

            var textRect = new Rectangle(dot.Right + 7, 0, Width - dot.Right - 11, Height);
            TextRenderer.DrawText(
                g, Text, Font, textRect, text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>设置页用的开关：药丸滑块 + 右侧文字。</summary>
    internal sealed class ToggleSwitch : CheckBox
    {
        private bool _hover;

        public ToggleSwitch()
        {
            AutoSize = false;
            Height = 24;
            Width = 260;
            Font = HistoryTheme.UiFont;
            ForeColor = HistoryTheme.TextPrimary;
            BackColor = HistoryTheme.Card;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var parentBrush = new SolidBrush(Parent?.BackColor ?? HistoryTheme.Card))
            {
                g.FillRectangle(parentBrush, ClientRectangle);
            }

            const int pillWidth = 36;
            const int pillHeight = 18;
            var pill = new Rectangle(0, (Height - pillHeight) / 2, pillWidth, pillHeight);

            Color fill = Checked ? HistoryTheme.Accent
                       : _hover ? HistoryTheme.BorderStrong
                       : Color.FromArgb(203, 210, 220);

            using (var path = HistoryTheme.RoundedRect(pill, pillHeight / 2))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            var knob = new Rectangle(Checked ? pill.Right - 16 : pill.Left + 2,
                                     pill.Top + 2, 14, 14);
            using (var brush = new SolidBrush(Color.White))
            {
                g.FillEllipse(brush, knob);
            }

            var textRect = new Rectangle(pill.Right + 10, 0, Width - pill.Right - 10, Height);
            TextRenderer.DrawText(
                g, Text, Font, textRect,
                Enabled ? HistoryTheme.TextPrimary : HistoryTheme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// 现代列表：自绘表头/行，斑马纹、hover、选中态、类型和结果徽标。
    /// 仍使用 VirtualMode，只绘制可视行。
    /// </summary>
    internal sealed class HistoryListView : ListView
    {
        private int _hoverIndex = -1;

        public HistoryListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = true;
            GridLines = false;
            BorderStyle = BorderStyle.None;
            BackColor = HistoryTheme.Card;
            Font = HistoryTheme.UiFont;
            ForeColor = HistoryTheme.TextPrimary;
            SetStyle(ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.AllPaintingInWmPaint, true);

            DoubleBuffered = true;
            SmallImageList = new ImageList { ImageSize = new Size(1, 32) };

            DrawColumnHeader += OnDrawColumnHeader;
            DrawItem += OnDrawItem;
            DrawSubItem += OnDrawSubItem;
            MouseMove += OnMouseMove;
            MouseLeave += OnMouseLeave;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            int index = hit.Item == null ? -1 : hit.Item.Index;

            if (index == _hoverIndex) return;
            _hoverIndex = index;
            Invalidate();
        }

        private void OnMouseLeave(object sender, EventArgs e)
        {
            if (_hoverIndex == -1) return;
            _hoverIndex = -1;
            Invalidate();
        }

        private void OnDrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= Columns.Count) return;

            var column = Columns[e.ColumnIndex];
            if (column.Width <= 0) return;

            using (var brush = new SolidBrush(HistoryTheme.HeaderBackground))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }

            using (var pen = new Pen(HistoryTheme.Border))
            {
                e.Graphics.DrawLine(pen,
                    e.Bounds.Left, e.Bounds.Bottom - 1,
                    e.Bounds.Right, e.Bounds.Bottom - 1);
            }

            bool right = column.TextAlign == HorizontalAlignment.Right;
            var flags = TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine |
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix |
                        (right ? TextFormatFlags.Right : TextFormatFlags.Left);

            var textRect = new Rectangle(
                e.Bounds.Left + 10,
                e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 20),
                e.Bounds.Height);

            TextRenderer.DrawText(e.Graphics, column.Text, HistoryTheme.UiFontMedium,
                textRect, HistoryTheme.TextSecondary, flags);
        }

        private void OnDrawItem(object sender, DrawListViewItemEventArgs e)
        {
            bool selected = e.Item.Selected || SelectedIndices.Contains(e.ItemIndex);
            bool hover = e.ItemIndex == _hoverIndex;

            Color background = selected
                ? HistoryTheme.Selection
                : hover
                    ? HistoryTheme.Hover
                    : e.ItemIndex % 2 == 0
                        ? HistoryTheme.Card
                        : HistoryTheme.Alternate;

            using (var brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }

            if (selected)
            {
                using (var accent = new SolidBrush(HistoryTheme.Accent))
                {
                    e.Graphics.FillRectangle(accent,
                        e.Bounds.Left, e.Bounds.Top + 4, 3, e.Bounds.Height - 8);
                }
            }

            using (var pen = new Pen(Color.FromArgb(241, 243, 246)))
            {
                e.Graphics.DrawLine(pen,
                    e.Bounds.Left, e.Bounds.Bottom - 1,
                    e.Bounds.Right, e.Bounds.Bottom - 1);
            }

            e.DrawDefault = false;
        }

        private void OnDrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= Columns.Count) return;

            var column = Columns[e.ColumnIndex];
            if (column.Width <= 0) return;

            string text = e.SubItem?.Text ?? "";
            if (text.Length == 0) return;

            bool selected = e.Item.Selected || SelectedIndices.Contains(e.ItemIndex);

            if (column.Text == "类型")
            {
                DrawBadge(e.Graphics, e.Bounds, text);
                return;
            }

            DrawPlainText(e, column, text, selected);
        }

        private static void DrawPlainText(DrawListViewSubItemEventArgs e, ColumnHeader column,
                                          string text, bool selected)
        {
            bool right = column.TextAlign == HorizontalAlignment.Right;
            var flags = TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine |
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix |
                        (right ? TextFormatFlags.Right : TextFormatFlags.Left);

            var rect = new Rectangle(
                e.Bounds.Left + 10,
                e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 20),
                e.Bounds.Height);

            Color color = selected
                ? HistoryTheme.TextPrimary
                : e.ColumnIndex == 0
                    ? HistoryTheme.TextPrimary
                    : HistoryTheme.TextSecondary;

            var font = e.ColumnIndex == 0 ? HistoryTheme.UiFont : HistoryTheme.UiFont;
            TextRenderer.DrawText(e.Graphics, text, font, rect, color, flags);
        }

        private static void DrawBadge(Graphics g, Rectangle bounds, string text)
        {
            Color fill, border, textColor;

            switch (text)
            {
                case "执行":
                    fill = HistoryTheme.AccentSoft;
                    border = Color.FromArgb(191, 219, 254);
                    textColor = HistoryTheme.Accent;
                    break;
                case "修改":
                    fill = HistoryTheme.AmberSoft;
                    border = Color.FromArgb(253, 230, 138);
                    textColor = HistoryTheme.Amber;
                    break;
                default:
                    fill = HistoryTheme.PurpleSoft;
                    border = Color.FromArgb(221, 214, 254);
                    textColor = HistoryTheme.Purple;
                    break;
            }

            Size textSize = TextRenderer.MeasureText(text, HistoryTheme.BadgeFont);
            int width = Math.Min(bounds.Width - 8, textSize.Width + 16);
            int height = 20;
            var pill = new Rectangle(
                bounds.Left + 8,
                bounds.Top + (bounds.Height - height) / 2,
                Math.Max(0, width),
                height);

            using (var path = HistoryTheme.RoundedRect(pill, 10))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            var textRect = new Rectangle(
                pill.Left + 8,
                pill.Top,
                Math.Max(0, pill.Width - 16),
                pill.Height);

            TextRenderer.DrawText(g, text, HistoryTheme.BadgeFont, textRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    internal static class CueBanner
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public static void Set(TextBox box, string text)
        {
            try
            {
                if (box != null && box.IsHandleCreated)
                    SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            }
            catch { }
        }
    }
}
