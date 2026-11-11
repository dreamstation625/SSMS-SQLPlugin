using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SsmsSqlPlugin.Std
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class StdPackage : AsyncPackage, IOleCommandTarget
    {
        public const string PackageGuidString = "EE966F9B-C731-4857-9A49-E4B010E4B615";

        public static readonly Guid CommandSet = new Guid("0C476A4F-7DBF-4A3E-A5C6-A68FAB905E44");
        public const uint CmdIdPing = 0x0100;

        private const int S_OK = 0;
        private const int OleCmdErrNotSupported = unchecked((int)0x80040100);
        private const uint OleCmdfSupported = 0x1;
        private const uint OleCmdfEnabled = 0x2;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            Log("=== InitializeAsync 进入（自动加载）===");
            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == CommandSet && cCmds > 0 && prgCmds[0].cmdID == CmdIdPing)
            {
                prgCmds[0].cmdf = OleCmdfSupported | OleCmdfEnabled;
                return S_OK;
            }

            return OleCmdErrNotSupported;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == CommandSet && nCmdID == CmdIdPing)
            {
                Log("=== 菜单命令被点击 ===");
                return S_OK;
            }

            return OleCmdErrNotSupported;
        }

        private static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "SsmsSqlPlugin");
                Directory.CreateDirectory(dir);
                var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;

                var asm = Assembly.GetExecutingAssembly();
                line += "    程序集: " + asm.Location + Environment.NewLine
                      + "    版本  : " + asm.GetName().Version + Environment.NewLine
                      + "    进程  : " + Process.GetCurrentProcess().ProcessName
                      + " / " + (Environment.Is64BitProcess ? "x64" : "x86") + Environment.NewLine
                      + "    CLR   : " + Environment.Version + Environment.NewLine;

                File.AppendAllText(Path.Combine(dir, "std-probe.log"), line);
            }
            catch
            {
                // 日志失败不能影响宿主
            }
        }
    }
}
