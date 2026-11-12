using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 元数据加载进度（非模态，不抢焦点）。
    /// 单条 set-based 查询拿不到百分比，用 marquee 表示"正在加载"。
    /// </summary>
    internal sealed class LoadingForm : Form
    {
        private readonly Label _label;
        private readonly ProgressBar _bar;

        public LoadingForm()
        {
            Text = "SSMS-SQLPlugin";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Width = 420;
            Height = 118;
            MaximizeBox = false;
            MinimizeBox = false;

            _label = new Label
            {
                AutoSize = false,
                Left = 16,
                Top = 12,
                Width = 380,
                Height = 24,
                Text = "正在加载补全元数据…",
            };

            _bar = new ProgressBar
            {
                Left = 16,
                Top = 42,
                Width = 380,
                Height = 18,
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 30,
            };

            var hint = new Label
            {
                AutoSize = false,
                Left = 16,
                Top = 68,
                Width = 380,
                Height = 18,
                ForeColor = Color.DimGray,
                Text = "表较多时首次加载需要几秒，完成后自动关闭",
            };

            Controls.Add(_label);
            Controls.Add(_bar);
            Controls.Add(hint);
        }

        protected override bool ShowWithoutActivation => true;

        public void SetDatabase(string database)
        {
            _label.Text = "正在加载补全元数据：" + (database ?? string.Empty);
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
                    Math.Max(bounds.Left, bounds.Right - Width - 40),
                    Math.Max(bounds.Top, bounds.Bottom - Height - 60));
            }
            catch
            {
                // 定位失败就用默认位置
            }
        }
    }
}
