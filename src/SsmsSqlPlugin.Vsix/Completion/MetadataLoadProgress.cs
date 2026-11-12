using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Completion
{
    /// <summary>
    /// 元数据加载超过 600ms 才显示进度窗，避免小库闪一下。
    /// 加载完成/失败/切库都会隐藏；旧加载的结果用 generation 丢弃。
    /// </summary>
    internal sealed class MetadataLoadProgress
    {
        private const int DelayMilliseconds = 600;

        private int _generation;
        private CancellationTokenSource _delay;
        private LoadingForm _form;

        public void Begin(string database)
        {
            int generation = Interlocked.Increment(ref _generation);

            var old = Interlocked.Exchange(ref _delay, null);
            if (old != null)
            {
                try { old.Cancel(); old.Dispose(); } catch { }
            }

            var delay = new CancellationTokenSource();
            _delay = delay;

            Task.Delay(DelayMilliseconds, delay.Token).ContinueWith(task =>
            {
                if (task.IsCanceled) return;
                if (generation != Volatile.Read(ref _generation)) return;
                Show(database, generation);
            }, TaskScheduler.Default);
        }

        public void End()
        {
            Interlocked.Increment(ref _generation);
            CancelDelay();

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Hide();
            });
        }

        public void Close()
        {
            Interlocked.Increment(ref _generation);
            CancelDelay();

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    if (_form != null && !_form.IsDisposed) _form.Close();
                }
                catch { }
                _form = null;
            });
        }

        private void CancelDelay()
        {
            var delay = Interlocked.Exchange(ref _delay, null);
            if (delay != null)
            {
                try { delay.Cancel(); delay.Dispose(); } catch { }
            }
        }

        private void Show(string database, int generation)
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (generation != Volatile.Read(ref _generation)) return;

                try
                {
                    if (_form == null || _form.IsDisposed) _form = new LoadingForm();
                    _form.SetDatabase(database);
                    if (!_form.Visible) _form.Show();
                }
                catch (Exception ex)
                {
                    Log.Warn("[补全] 显示加载进度失败: " + ex.Message);
                }
            });
        }

        private void Hide()
        {
            try
            {
                if (_form != null && !_form.IsDisposed) _form.Hide();
            }
            catch { }
        }
    }
}
