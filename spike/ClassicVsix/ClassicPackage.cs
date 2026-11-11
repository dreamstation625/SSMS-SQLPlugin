using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;

namespace SsmsSqlPlugin.Classic
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad("d114938f-591c-46cf-a785-500a82d97410", PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClassicPackage : AsyncPackage, IOleCommandTarget
    {
        public const string PackageGuidString = "909BF49B-6231-4C49-B584-19D20EB94407";

        public static readonly Guid CommandSet = new Guid("21FF5D7C-D921-421D-A187-66C9AA162991");
        public const uint CmdIdPing = 0x0100;

        private const int S_OK = 0;
        private const int OleCmdErrNotSupported = unchecked((int)0x80040100);

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            Log("=== Classic InitializeAsync 进入 ===");
            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup == CommandSet && cCmds > 0 && prgCmds[0].cmdID == CmdIdPing)
            {
                prgCmds[0].cmdf = 0x1 | 0x2;
                return S_OK;
            }

            return OleCmdErrNotSupported;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup == CommandSet && nCmdID == CmdIdPing)
            {
                Log("=== Classic 菜单命令被点击 ===");
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
                File.AppendAllText(
                    Path.Combine(dir, "classic.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
