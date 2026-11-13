using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.VisualStudio.Shell;
using SsmsSqlPlugin.History;

namespace SsmsSqlPlugin.Infrastructure
{
    /// <summary>
    /// 非模态轻提示：不抢焦点，3 秒后自动消失。
    /// 用于补全缓存刷新、切库等操作的即时反馈。
    /// </summary>
    internal static class Toast
    {
        private static ToastForm _form;
        private static Timer _timer;

        public static void Show(string message, bool error = false)
        {
            if (string.IsNullOrEmpty(message)) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowCore(message, error);
            });
        }

        private static void ShowCore(string message, bool error)
        {
            try
            {
                if (_form == null || _form.IsDisposed)
                {
                    _form = new ToastForm();
                    _timer = new Timer { Interval = 3000 };
                    _timer.Tick += (sender, args) =>
                    {
                        _timer.Stop();
                        try { if (_form != null && !_form.IsDisposed) _form.Hide(); } catch { }
                    };
                }

                _form.SetMessage(message, error);
                if (!_form.Visible) _form.Show();

                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                _form.Location = new Point(
                    area.Left + (area.Width - _form.Width) / 2,
                    area.Bottom - _form.Height - 96);
                _form.BringToFront();

                _timer.Stop();
                _timer.Start();

                Log.Info("[提示] " + message);
            }
            catch (Exception ex)
            {
                Log.Warn("[提示] 显示失败: " + ex.Message);
            }
        }
    }

    internal sealed class ToastForm : Form
    {
        private static readonly Color InfoBackground = Color.FromArgb(32, 33, 36);
        private static readonly Color ErrorBackground = Color.FromArgb(196, 43, 28);

        private readonly Panel _bar;
        private readonly Label _label;

        public ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = InfoBackground;
            Font = HistoryTheme.UiFont;

            _bar = new Panel
            {
                Left = 0,
                Top = 0,
                Width = 4,
                BackColor = HistoryTheme.Accent,
            };

            _label = new Label
            {
                AutoSize = false,
                Left = 18,
                Top = 0,
                Width = 300,
                Height = 44,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.White,
                BackColor = InfoBackground,
                Font = HistoryTheme.UiFontMedium,
            };

            Controls.Add(_bar);
            Controls.Add(_label);
        }

        protected override bool ShowWithoutActivation => true;

        public void SetMessage(string message, bool error)
        {
            _label.Text = message;
            var background = error ? ErrorBackground : InfoBackground;
            _bar.BackColor = error ? HistoryTheme.Red : HistoryTheme.Accent;
            BackColor = background;
            _label.BackColor = background;

            var size = TextRenderer.MeasureText(
                message, _label.Font, new Size(560, int.MaxValue), TextFormatFlags.WordBreak);

            Width = Math.Min(620, Math.Max(260, size.Width + 48));
            Height = Math.Max(46, size.Height + 22);

            _bar.Height = Height;
            _label.Width = Width - 30;
            _label.Height = Height;
        }
    }
}
