using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SsmsSqlPlugin.Infrastructure;

namespace SsmsSqlPlugin.Editor
{
    /// <summary>
    /// 进程内低层键盘钩子：只在 SSMS 前台时识别 Ctrl+K,Ctrl+D / Ctrl+K,Ctrl+F。
    ///
    /// 为什么不用 VS 键绑定：
    ///   SSMS 把 Ctrl+K,Ctrl+F 绑到 FormatSelection，但命令目标经常返回“当前不可用”，
    ///   按键根本进不到编辑器的命令过滤器。钩子在命令路由之前拿到原始按键，稳定可控。
    ///
    /// 只在当前进程是前台窗口时处理；其它应用里的 Ctrl+K 不受影响。
    /// </summary>
    internal static class FormatKeyHandler
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_CONTROL = 0x11;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_K = 0x4B;
        private const int VK_D = 0x44;
        private const int VK_F = 0x46;
        private const int VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private static readonly object Gate = new object();
        private static IntPtr _hook;
        private static HookProc _proc;
        private static Action<bool> _format;
        private static SynchronizationContext _context;
        private static int _processId;
        private static DateTime _pendingUntil;
        private static bool _ctrlDown;
        private static bool _hookSeen;

        public static void Start(Action<bool> format)
        {
            if (format == null) return;

            lock (Gate)
            {
                _format = format;

                if (_hook != IntPtr.Zero) return;

                _context = SynchronizationContext.Current;
                if (_context == null)
                {
                    Log.Warn("[格式化] 没有 UI SynchronizationContext，快捷键钩子未安装");
                    return;
                }

                _processId = Process.GetCurrentProcess().Id;
                _proc = HookCallback;
                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

                if (_hook == IntPtr.Zero)
                    Log.Warn("[格式化] 安装键盘钩子失败: " + Marshal.GetLastWin32Error());
                else
                    Log.Info("[格式化] 快捷键钩子已安装");
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _format = null;
                _pendingUntil = default;
                _ctrlDown = false;
                _hookSeen = false;

                if (_hook != IntPtr.Zero)
                {
                    try { UnhookWindowsHookEx(_hook); } catch { }
                    _hook = IntPtr.Zero;
                }

                _proc = null;
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    if (!_hookSeen)
                    {
                        _hookSeen = true;
                        Log.Info("[格式化] 键盘钩子已收到输入");
                    }

                    var data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(KBDLLHOOKSTRUCT));

                    uint vk = data.vkCode;
                    int message = wParam.ToInt32();
                    bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                    bool up = message == WM_KEYUP || message == WM_SYSKEYUP;

                    if (vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL)
                    {
                        _ctrlDown = down;
                        // 松开 Ctrl 不能清掉 Ctrl+K 的等待状态：
                        // Ctrl+K, Ctrl+D 是两个完整个的按键序列，中间会松开 Ctrl。
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

                    if (down && IsOwnProcessForeground())
                    {
                        if (_ctrlDown && vk == VK_K)
                        {
                            _pendingUntil = DateTime.UtcNow.AddSeconds(2);
                        }
                        else if (_pendingUntil != default &&
                                 DateTime.UtcNow <= _pendingUntil &&
                                 _ctrlDown && (vk == VK_D || vk == VK_F))
                        {
                            bool selectionOnly = vk == VK_F;
                            _pendingUntil = default;

                            Log.Info("[格式化] 快捷键 Ctrl+K, " +
                                     (selectionOnly ? "Ctrl+F（选定内容）" : "Ctrl+D"));

                            var callback = _format;
                            var context = _context;
                            if (callback != null && context != null)
                            {
                                context.Post(_ =>
                                {
                                    try { callback(selectionOnly); }
                                    catch (Exception ex) { Log.Error("[格式化] 快捷键执行失败", ex); }
                                }, null);
                            }

                            ClearChordState();
                            return (IntPtr)1;   // 吞掉第二键，避免 VS 再报“命令不可用”
                        }
                        else
                        {
                            _pendingUntil = default;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[格式化] 键盘钩子处理失败: " + ex.Message);
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsOwnProcessForeground()
        {
            try
            {
                int pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                return pid == _processId;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 第二键被我们吞掉后，VS 仍会停在“Ctrl+K 已按下，等待第二键”状态。
        /// 等 Ctrl 松开后补发一个 Esc，清掉该状态，避免影响后续菜单/快捷键。
        /// </summary>
        private static void ClearChordState()
        {
            Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 10; i++)
                    {
                        if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0) break;
                        await Task.Delay(30).ConfigureAwait(false);
                    }

                    if (!IsOwnProcessForeground()) return;

                    // 第一个 Esc 结束 VS 的 Ctrl+K 前缀（会被当成无效第二键），
                    // 第二个 Esc 把残留的前缀状态/提示清干净。
                    keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

                    await Task.Delay(120).ConfigureAwait(false);
                    if (!IsOwnProcessForeground()) return;

                    keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch { }
            });
        }
    }
}
