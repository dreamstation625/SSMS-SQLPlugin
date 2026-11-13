using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SsmsSqlPlugin.Installer
{
    internal sealed class SsmsProduct
    {
        public string Version { get; set; }
        public string InstallDirectory { get; set; }
        public string VsixInstaller { get; set; }

        public string DisplayName
        {
            get { return "SSMS " + Version; }
        }

        public string PayloadResource
        {
            get { return "Payload.V" + Version + ".vsix"; }
        }

        public string PayloadFileName
        {
            get { return "SSMS-SQLPlugin.V" + Version + ".vsix"; }
        }
    }

    internal sealed class InstalledExtension
    {
        public SsmsProduct Product { get; set; }
        public string Directory { get; set; }
    }

    internal static class InstallerCore
    {
        public const string ProductName = "SSMS-SQLPlugin";
        private const string ExtensionId = "SSMS-SQLPlugin.7b3e2f18-5c94-4a67-b1d8-3f9e6a4c2d10";
        private const string PkgdefName = "SSMS-SQLPlugin.pkgdef";
        private const int VsixInstallerTimeoutMs = 90000;

        public static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SsmsSqlPlugin");
            }
        }

        public static List<SsmsProduct> FindProducts()
        {
            var products = new List<SsmsProduct>();
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };

            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
            {
                if (!Directory.Exists(root)) continue;

                foreach (string version in new[] { "21", "22" })
                {
                    if (products.Any(p => p.Version == version)) continue;

                    string dir = Path.Combine(root,
                        "Microsoft SQL Server Management Studio " + version);
                    string vsixInstaller = Path.Combine(dir, "Release", "Common7", "IDE",
                        "VSIXInstaller.exe");
                    if (!File.Exists(vsixInstaller)) continue;

                    products.Add(new SsmsProduct
                    {
                        Version = version,
                        InstallDirectory = dir,
                        VsixInstaller = vsixInstaller,
                    });
                }
            }

            return products.OrderBy(p => p.Version, StringComparer.Ordinal).ToList();
        }

        public static List<string> FindExtensionRoots(SsmsProduct product)
        {
            var roots = new List<string>();
            if (product == null) return roots;

            string ssmsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "SSMS");
            if (!Directory.Exists(ssmsRoot)) return roots;

            try
            {
                foreach (string instance in Directory.GetDirectories(
                             ssmsRoot, product.Version + ".0_*")
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string extensions = Path.Combine(instance, "Extensions");
                    if (Directory.Exists(extensions)) roots.Add(extensions);
                }
            }
            catch { }

            return roots;
        }

        public static string FindInstalledDirectory(SsmsProduct product)
        {
            foreach (string root in FindExtensionRoots(product))
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        if (File.Exists(Path.Combine(dir, PkgdefName))) return dir;
                    }
                }
                catch { }
            }
            return null;
        }

        public static List<InstalledExtension> FindInstalledExtensions()
        {
            var result = new List<InstalledExtension>();
            foreach (SsmsProduct product in FindProducts())
            {
                string dir = FindInstalledDirectory(product);
                if (!string.IsNullOrEmpty(dir))
                {
                    result.Add(new InstalledExtension { Product = product, Directory = dir });
                }
            }
            return result;
        }

        public static string GetPayloadVersion()
        {
            string temp = null;
            try
            {
                SsmsProduct product = FindProducts().FirstOrDefault(p => p.Version == "22")
                                      ?? FindProducts().FirstOrDefault();
                string resource = product != null ? product.PayloadResource : "Payload.V22.vsix";
                string vsix = ExtractPayloadVsix(resource, "SSMS-SQLPlugin.vsix", out temp);
                string dll = Path.Combine(temp, "SsmsSqlPlugin.dll");
                if (!File.Exists(dll))
                {
                    string[] files = Directory.GetFiles(temp, "SsmsSqlPlugin.dll",
                        SearchOption.AllDirectories);
                    if (files.Length > 0) dll = files[0];
                }

                if (!File.Exists(dll)) return "0.1.1";
                return AssemblyName.GetAssemblyName(dll).Version.ToString(3);
            }
            catch
            {
                return "0.1.1";
            }
            finally
            {
                SafeDeleteDirectory(temp);
            }
        }

        public static bool Install(out string message)
        {
            message = null;

            if (IsSsmsRunning())
            {
                message = "SSMS 正在运行。请先关闭所有 SSMS 窗口，再点「安装 / 更新」。";
                return false;
            }

            List<SsmsProduct> products = FindProducts();
            if (products.Count == 0)
            {
                message = "未找到已安装的 SSMS 21 或 SSMS 22。\r\n" +
                          "请先安装 SSMS，或重新修复安装后重试。";
                return false;
            }

            var succeeded = new List<string>();
            var failed = new List<string>();
            string firstInstalled = null;

            foreach (SsmsProduct product in products)
            {
                string temp = null;
                try
                {
                    string existing = FindInstalledDirectory(product);
                    if (!string.IsNullOrEmpty(existing))
                    {
                        string uninstallMessage;
                        if (!RunUninstall(product, out uninstallMessage))
                        {
                            failed.Add(product.DisplayName + " 卸载旧版本失败：" +
                                       (uninstallMessage ?? "未知错误"));
                            continue;
                        }
                    }

                    string vsix = ExtractPayloadVsix(product, out temp);
                    int code = RunVsixInstaller(product.VsixInstaller, "/quiet \"" + vsix + "\"");
                    string installed = code == 0
                        ? WaitForInstall(product)
                        : FindInstalledDirectory(product);

                    if (string.IsNullOrEmpty(installed))
                    {
                        failed.Add(product.DisplayName + " 安装失败" +
                                   (code == 0 ? "：未找到扩展目录" :
                                    "：VSIXInstaller 返回 " + code));
                        continue;
                    }

                    RefreshExtensionCache(product);
                    succeeded.Add(product.DisplayName + "：" + installed);
                    if (firstInstalled == null) firstInstalled = installed;
                }
                catch (Exception ex)
                {
                    failed.Add(product.DisplayName + " 安装异常：" + ex.Message);
                }
                finally
                {
                    SafeDeleteDirectory(temp);
                }
            }

            if (succeeded.Count == 0)
            {
                message = "安装失败：\r\n" + string.Join("\r\n", failed);
                return false;
            }

            WriteUninstallEntry(firstInstalled);
            CopyInstallerToDataDirectory();

            message = "安装完成。\r\n\r\n" + string.Join("\r\n", succeeded) +
                      "\r\n\r\n请重启对应版本的 SSMS 生效。";
            if (failed.Count > 0)
            {
                message += "\r\n\r\n未完成：\r\n" + string.Join("\r\n", failed);
            }
            return failed.Count == 0;
        }

        public static bool Uninstall(out string message)
        {
            message = null;

            if (IsSsmsRunning())
            {
                message = "SSMS 正在运行。请先关闭所有 SSMS 窗口，再卸载。";
                return false;
            }

            List<InstalledExtension> installed = FindInstalledExtensions();
            if (installed.Count == 0)
            {
                message = "未检测到已安装的 SSMS-SQLPlugin 扩展。";
                return true;
            }

            var succeeded = new List<string>();
            var failed = new List<string>();

            foreach (InstalledExtension item in installed)
            {
                string uninstallMessage;
                if (RunUninstall(item.Product, out uninstallMessage))
                {
                    succeeded.Add(item.Product.DisplayName + "：已卸载");
                }
                else
                {
                    failed.Add(item.Product.DisplayName + " 卸载失败：" +
                               (uninstallMessage ?? "未知错误"));
                }
            }

            if (FindInstalledExtensions().Count == 0)
            {
                RemoveUninstallEntry();
            }

            if (succeeded.Count == 0)
            {
                message = "卸载失败：\r\n" + string.Join("\r\n", failed);
                return false;
            }

            message = "卸载完成。\r\n\r\n" + string.Join("\r\n", succeeded) +
                      "\r\n\r\n历史记录和设置已保留：\r\n" + DataDirectory;
            if (failed.Count > 0)
            {
                message += "\r\n\r\n未完成：\r\n" + string.Join("\r\n", failed);
            }
            return failed.Count == 0;
        }

        public static bool IsSsmsRunning()
        {
            try { return Process.GetProcessesByName("SSMS").Length > 0; }
            catch { return false; }
        }

        private static bool RunUninstall(SsmsProduct product, out string message)
        {
            int code = RunVsixInstaller(product.VsixInstaller,
                "/quiet /uninstall:" + ExtensionId);
            if (code != 0)
            {
                if (FindInstalledDirectory(product) == null)
                {
                    message = null;
                    return true;
                }

                message = code == -2
                    ? "卸载超时：VSIXInstaller 可能被杀毒/安全软件拦截。\r\n" +
                      "请处理安全软件弹窗后重试。"
                    : "VSIXInstaller 返回 " + code + "。";
                return false;
            }

            if (!WaitForUninstall(product))
            {
                message = "卸载未完成：扩展目录仍然存在。";
                return false;
            }

            message = null;
            return true;
        }

        private static string WaitForInstall(SsmsProduct product)
        {
            for (int i = 0; i < 60; i++)
            {
                string dir = FindInstalledDirectory(product);
                if (!string.IsNullOrEmpty(dir)) return dir;
                System.Threading.Thread.Sleep(500);
            }
            return null;
        }

        private static bool WaitForUninstall(SsmsProduct product)
        {
            for (int i = 0; i < 60; i++)
            {
                if (FindInstalledDirectory(product) == null) return true;
                System.Threading.Thread.Sleep(500);
            }
            return false;
        }

        private static int RunVsixInstaller(string exe, string arguments)
        {
            try
            {
                var start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using (Process process = Process.Start(start))
                {
                    if (process == null) return -1;
                    if (!process.WaitForExit(VsixInstallerTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return -2;
                    }
                    return process.ExitCode;
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// VSIXInstaller 静默安装不会刷新 Shell 的扩展缓存；
        /// SSMS 21 尤其明显，不刷新会静默不加载。
        /// </summary>
        private static void RefreshExtensionCache(SsmsProduct product)
        {
            foreach (string root in FindExtensionRoots(product))
            {
                try
                {
                    string marker = Path.Combine(root, "extensions.configurationchanged");
                    if (!File.Exists(marker)) File.Create(marker).Dispose();
                    File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
                }
                catch { }

                foreach (string pattern in new[]
                         {
                             "ExtensionMetadata*.sqlite",
                             "ExtensionMetadata*.mpack",
                             "*.mpack",
                         })
                {
                    try
                    {
                        foreach (string file in Directory.GetFiles(root, pattern))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        private static string ExtractPayloadVsix(SsmsProduct product, out string tempDir)
        {
            return ExtractPayloadVsix(product.PayloadResource, product.PayloadFileName,
                out tempDir);
        }

        private static string ExtractPayloadVsix(string resource, string fileName,
            out string tempDir)
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "SsmsSqlPluginSetup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            using (Stream stream = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream(resource))
            {
                if (stream == null)
                    throw new InvalidOperationException("安装包缺少内嵌的 " + resource + "。");

                string vsix = Path.Combine(tempDir, fileName);
                using (FileStream file = File.Create(vsix)) stream.CopyTo(file);
                return vsix;
            }
        }

        private static void WriteUninstallEntry(string installDirectory)
        {
            string uninstaller = Path.Combine(DataDirectory, "Installer",
                "SSMS-SQLPlugin-Setup.exe");

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SSMS-SQLPlugin"))
            {
                if (key == null) return;

                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", GetPayloadVersion());
                key.SetValue("Publisher", "dreamstation625");
                key.SetValue("InstallLocation", installDirectory ?? string.Empty);
                key.SetValue("DisplayIcon", uninstaller);
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SSMS-SQLPlugin",
                    false);
            }
            catch { }
        }

        private static void CopyInstallerToDataDirectory()
        {
            try
            {
                string dir = Path.Combine(DataDirectory, "Installer");
                Directory.CreateDirectory(dir);
                File.Copy(Application.ExecutablePath,
                    Path.Combine(dir, "SSMS-SQLPlugin-Setup.exe"), true);
            }
            catch { }
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
