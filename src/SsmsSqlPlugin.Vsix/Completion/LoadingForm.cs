using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SsmsSqlPlugin.History;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 元数据加载进度（非模态，不抢焦点）。
    /// 单条 set-based 查询拿不到百分比，用循环滚动条表示"正在加载"。
    /// </summary>
    internal sealed class LoadingForm : Form
    {
        private readonly Label _eyebrow;
        private readonly Label _title;
        private readonly Label _database;
        private readonly Label _hint;
        private readonly LoadingBar _bar;
        private readonly Font _badgeFont;
        private readonly StringFormat _centerFormat;

        public LoadingForm()
        {
            Text = "SSMS-SQLPlugin";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Width = 440;
            Height = 160;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = HistoryTheme.Card;
            Font = HistoryTheme.UiFont;
            DoubleBuffered = true;

            _badgeFont = new Font(HistoryTheme.UiFont, FontStyle.Bold);
            _centerFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            _eyebrow = new Label
            {
                AutoSize = false,
                Left = 72,
                Top = 20,
                Width = 340,
                Height = 16,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextMuted,
                BackColor = HistoryTheme.Card,
                Text = "SQL 补全",
            };

            _title = new Label
            {
                AutoSize = false,
                Left = 72,
                Top = 36,
                Width = 340,
                Height = 26,
                Font = new Font(HistoryTheme.UiFont.FontFamily, 12f, FontStyle.Bold),
                ForeColor = HistoryTheme.TextPrimary,
                BackColor = HistoryTheme.Card,
                Text = "正在加载元数据",
            };

            _database = new Label
            {
                AutoSize = false,
                Left = 72,
                Top = 62,
                Width = 340,
                Height = 20,
                Font = HistoryTheme.UiFontMedium,
                ForeColor = HistoryTheme.Accent,
                BackColor = HistoryTheme.Card,
                Text = "",
            };

            _hint = new Label
            {
                AutoSize = false,
                Left = 22,
                Top = 96,
                Width = 396,
                Height = 18,
                Font = HistoryTheme.SmallFont,
                ForeColor = HistoryTheme.TextSecondary,
                BackColor = HistoryTheme.Card,
                Text = "表较多时首次加载需要几秒，完成后自动关闭",
            };

            _bar = new LoadingBar
            {
                Left = 22,
                Top = 124,
                Width = 396,
                Height = 8,
            };

            Controls.Add(_eyebrow);
            Controls.Add(_title);
            Controls.Add(_database);
            Controls.Add(_hint);
            Controls.Add(_bar);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            try
            {
                using (var path = HistoryTheme.RoundedRect(
                    new Rectangle(0, 0, Width, Height), 12))
                {
                    var old = Region;
                    Region = new Region(path);
                    old?.Dispose();
                }
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var pen = new Pen(HistoryTheme.Border))
            using (var path = HistoryTheme.RoundedRect(
                new Rectangle(0, 0, Width - 1, Height - 1), 12))
            {
                g.DrawPath(pen, path);
            }

            var badge = new Rectangle(20, 20, 38, 38);
            using (var path = HistoryTheme.RoundedRect(badge, 10))
            using (var brush = new SolidBrush(HistoryTheme.Accent))
            {
                g.FillPath(brush, path);
            }

            using (var brush = new SolidBrush(Color.White))
            {
                g.DrawString("SQL", _badgeFont, brush, badge, _centerFormat);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _bar.Start();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) _bar.Start();
            else _bar.Stop();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _bar.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _badgeFont.Dispose();
                _centerFormat.Dispose();
            }
            base.Dispose(disposing);
        }

        public void SetDatabase(string database)
        {
            _database.Text = string.IsNullOrEmpty(database)
                ? ""
                : "数据库：" + database;
            PositionNearSsms();
        }

        private void PositionNearSsms()
        {
            try
            {
                IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
                var screen = handle != IntPtr.Zero
                    ? Screen.FromHandle(handle)
                    : Screen.PrimaryScreen;

                var bounds = screen.WorkingArea;
                Location = new Point(
                    Math.Max(bounds.Left, bounds.Right - Width - 32),
                    Math.Max(bounds.Top, bounds.Bottom - Height - 56));
            }
            catch
            {
                // 定位失败就用默认位置
            }
        }

        /// <summary>圆角轨道 + 循环蓝色滑块，替代系统绿色 marquee。</summary>
        private sealed class LoadingBar : Control
        {
            private readonly Timer _timer;
            private float _phase;

            public LoadingBar()
            {
                SetStyle(ControlStyles.UserPaint |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);

                _timer = new Timer { Interval = 33 };
                _timer.Tick += (s, e) =>
                {
                    _phase += 0.018f;
                    if (_phase > 1f) _phase -= 1f;
                    Invalidate();
                };
            }

            public void Start() { if (!_timer.Enabled) _timer.Start(); }

            public void Stop() { if (_timer.Enabled) _timer.Stop(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (var background = new SolidBrush(Parent?.BackColor ?? HistoryTheme.Card))
                {
                    g.FillRectangle(background, ClientRectangle);
                }

                int height = Math.Min(8, Height);
                var track = new Rectangle(0, (Height - height) / 2, Width - 1, height);

                using (var trackPath = HistoryTheme.RoundedRect(track, height / 2))
                using (var trackBrush = new SolidBrush(Color.FromArgb(232, 235, 240)))
                {
                    g.FillPath(trackBrush, trackPath);
                }

                int segmentWidth = Math.Max(48, (int)(Width * 0.32f));
                float x = -segmentWidth + _phase * (Width + segmentWidth);
                var segment = new Rectangle((int)x, track.Top, segmentWidth, height);

                using (var trackPath = HistoryTheme.RoundedRect(track, height / 2))
                {
                    g.SetClip(trackPath);

                    using (var segmentPath = HistoryTheme.RoundedRect(segment, height / 2))
                    using (var brush = new LinearGradientBrush(
                        segment, HistoryTheme.Accent,
                        Color.FromArgb(96, 165, 250), LinearGradientMode.Horizontal))
                    {
                        g.FillPath(brush, segmentPath);
                    }

                    g.ResetClip();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _timer.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
