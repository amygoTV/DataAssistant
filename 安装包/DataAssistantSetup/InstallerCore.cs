using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;

namespace DataAssistantSetup
{
    /// <summary>
    /// 安装核心：自解压载荷、创建快捷方式、注册"应用和功能"卸载项、执行卸载。
    /// 全部使用系统原生能力（zip / COM 快捷方式 / 注册表），不依赖任何第三方库。
    /// </summary>
    public static class InstallerCore
    {
        /// <summary>界面上显示的名称（中文）。</summary>
        public const string AppName = "数据助手";
        /// <summary>安装目录名 / 快捷方式名 / 卸载项键名（ASCII，避免 Program Files 下出现中文路径问题）。</summary>
        public const string AppId = "DataAssistant";
        public const string AppVersion = "1.0.3";
        public const string Publisher = "数据助手";
        public const string ExeName = "DataAssistant.exe";
        public const string UninstallKey = "DataAssistant";

        /// <summary>旧版本（改名前）的安装标识，升级时需要清理其残留。</summary>
        public const string LegacyAppId = "EasyFindImageTool";
        public const string LegacyExeName = "EasyFindImageTool.exe";

        /// <summary>载荷 zip 作为嵌入资源。</summary>
        public static Stream OpenPayload()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            string[] names = asm.GetManifestResourceNames();
            foreach (string n in names)
                if (n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(n);
            // 退回到按前缀查找
            foreach (string n in names)
                if (n.ToLowerInvariant().Contains("payload"))
                    return asm.GetManifestResourceStream(n);
            return null;
        }

        /// <summary>把嵌入的载荷解压到目标目录。</summary>
        public static bool ExtractPayload(string installDir, Action<string> log,
            Action<int, string> progress, CancellationToken token)
        {
            try
            {
                Directory.CreateDirectory(installDir);
                using (Stream payload = OpenPayload())
                {
                    if (payload == null)
                    {
                        if (log != null) log("错误：未找到内嵌的安装载荷。");
                        return false;
                    }
                    using (var za = new ZipArchive(payload, ZipArchiveMode.Read))
                    {
                        long total = 0;
                        foreach (var e in za.Entries) total += e.Length;
                        long done = 0;
                        int lastPct = -1;

                        foreach (var entry in za.Entries)
                        {
                            if (token.IsCancellationRequested) return false;
                            string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                            if (rel.Length == 0) continue;
                            string dest = Path.GetFullPath(Path.Combine(installDir, rel));
                            // 安全校验：解压目标必须位于安装目录内
                            if (!dest.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
                                throw new IOException("非法路径条目：" + entry.FullName);

                            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                            {
                                Directory.CreateDirectory(dest);
                                continue;
                            }
                            string dir = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                            using (Stream src = entry.Open())
                            using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
                            {
                                src.CopyTo(fs);
                            }
                            done += entry.Length;
                            int pct = total > 0 ? (int)(done * 100 / total) : 0;
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                if (progress != null) progress(pct, entry.FullName);
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("解压失败：" + ex.Message);
                return false;
            }
        }

        // ====================== 快捷方式 ======================

        /// <summary>创建 .lnk 快捷方式（用 WScript.Shell 后期绑定，无需 COM 引用）。</summary>
        public static bool CreateShortcut(string shortcutPath, string targetExe, string workDir, string desc)
        {
            try
            {
                // 目标目录不存在时 WScript.Shell 会静默失败，先确保目录存在
                string dir = Path.GetDirectoryName(shortcutPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                object link = shell.GetType().InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { targetExe });
                link.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { workDir });
                link.GetType().InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { desc });
                link.GetType().InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { targetExe + ",0" });
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                return File.Exists(shortcutPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void DeleteShortcut(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }

        public static string StartMenuDir(string productDir)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), productDir);
        }

        public static string DesktopPath(string exeName)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), exeName);
        }

        // ====================== 卸载注册表 ======================

        /// <summary>注册到"应用和功能"（控制面板卸载项）。有管理员权限写 HKLM，否则写 HKCU。</summary>
        public static void WriteUninstallRegistry(string installDir, string uninstallExe)
        {
            Microsoft.Win32.RegistryKey root =
                IsAdministrator() ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser;
            string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKey;
            using (var k = root.CreateSubKey(path))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", AppVersion);
                k.SetValue("Publisher", Publisher);
                k.SetValue("InstallLocation", installDir);
                k.SetValue("UninstallString", "\"" + uninstallExe + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstallExe + "\" /uninstall /silent");
                k.SetValue("DisplayIcon", Path.Combine(installDir, ExeName) + ",0");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify", 1);
                k.SetValue("NoRepair", 1);
                k.SetValue("EstimatedSize", 18000);   // KB，约 18MB
            }
        }

        public static void DeleteUninstallRegistry()
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKey;
                    if (root.OpenSubKey(path) != null) root.DeleteSubKeyTree(path);
                }
                catch (Exception) { }
            }
        }

        /// <summary>读取已安装目录（供卸载用）。</summary>
        public static string ReadInstallDir()
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallKey;
                    using (var k = root.OpenSubKey(path))
                    {
                        if (k != null)
                        {
                            string v = k.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(v)) return v;
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        // ====================== 卸载 ======================

        /// <summary>
        /// 执行卸载：删除快捷方式、注册表项与安装目录。
        /// 若自身位于安装目录内（会被占用），先把自己复制到临时目录再由副本完成删除。
        /// </summary>
        public static void RunUninstall(string installDir, bool silent, Action<string> log)
        {
            if (string.IsNullOrEmpty(installDir)) installDir = ReadInstallDir();
            if (string.IsNullOrEmpty(installDir))
            {
                if (log != null) log("未找到安装记录，无需卸载。");
                return;
            }

            // 快捷方式
            DeleteShortcut(StartMenuDir(AppName) + "\\" + AppName + ".lnk");
            DeleteShortcut(DesktopPath(AppName + ".lnk"));
            try { if (Directory.Exists(StartMenuDir(AppName))) Directory.Delete(StartMenuDir(AppName), true); }
            catch (Exception) { }
            DeleteUninstallRegistry();
            CleanupLegacyInstall(log);

            // 卸载器自身在安装目录内 → 交给临时副本删除
            string self = Process.GetCurrentProcess().MainModule.FileName;
            bool selfInside = false;
            try { selfInside = self.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { }

            if (selfInside)
            {
                try
                {
                    string temp = Path.Combine(Path.GetTempPath(), "DA_Uninstall_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                    File.Copy(self, temp, true);
                    Process.Start(new ProcessStartInfo(temp, "/uninstall /silent /dir=\"" + installDir + "\"")
                    {
                        UseShellExecute = false
                    });
                    if (log != null) log("卸载程序已转交临时副本执行。");
                    return;
                }
                catch (Exception ex)
                {
                    if (log != null) log("转交卸载失败：" + ex.Message);
                }
            }

            // 直接删除安装目录
            try
            {
                if (Directory.Exists(installDir))
                {
                    // 自身（Uninstall.exe）可能仍被占用，且进程退出后句柄释放有延迟 → 重试若干次
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        foreach (string f in Directory.GetFiles(installDir))
                        {
                            try { File.Delete(f); } catch (Exception) { }
                        }
                        foreach (string d in Directory.GetDirectories(installDir))
                        {
                            try { Directory.Delete(d, true); } catch (Exception) { }
                        }
                        try { Directory.Delete(installDir); } catch (Exception) { }
                        if (!Directory.Exists(installDir)) break;
                        Thread.Sleep(600);
                    }
                }
                if (Directory.Exists(installDir))
                {
                    if (log != null) log("卸载未完全完成，残留：" + installDir);
                    if (log != null) log("（通常是因为程序仍在运行，请关闭后手动删除该目录）");
                }
                else if (log != null) log("已卸载：" + installDir);
            }
            catch (Exception ex)
            {
                if (log != null) log("删除目录失败（可能有文件被占用）：" + ex.Message);
            }
        }

        /// <summary>静默安装（/S 或 /silent）。</summary>
        public static int SilentInstall(string dir, bool desktopShortcut, Action<string> log)
        {
            string installDir = dir;
            if (string.IsNullOrEmpty(installDir))
                installDir = IsAdministrator()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppId)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

            if (!ExtractPayload(installDir, log, null, CancellationToken.None))
                return 2;

            string exe = Path.Combine(installDir, ExeName);
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string uninstaller = Path.Combine(installDir, "Uninstall.exe");
            try { File.Copy(self, uninstaller, true); } catch (Exception) { }

            bool sm = CreateShortcut(Path.Combine(StartMenuDir(AppName), AppName + ".lnk"), exe, installDir, AppName);
            if (log != null) log("  开始菜单快捷方式：" + (sm ? "已创建" : "创建失败"));
            if (desktopShortcut)
            {
                bool dp = CreateShortcut(DesktopPath(AppName + ".lnk"), exe, installDir, AppName);
                if (log != null) log("  桌面快捷方式：" + (dp ? "已创建" : "创建失败") + " → " + DesktopPath(AppName + ".lnk"));
            }
            WriteUninstallRegistry(installDir, uninstaller);
            if (log != null) log("  卸载项已注册（控制面板 → 程序和功能）");
            CleanupLegacyInstall(log);
            if (log != null) log("安装完成：" + installDir);
            return 0;
        }

        // ====================== 旧版本残留清理 ======================

        /// <summary>
        /// 清理改名前（EasyFindImageTool）遗留的安装痕迹：
        /// 旧注册表卸载项、旧快捷方式、旧安装目录。
        /// 升级安装与卸载时都会调用，避免用户机器上出现两套条目。
        /// </summary>
        public static void CleanupLegacyInstall(Action<string> log)
        {
            try
            {
                // 旧注册表卸载项
                foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    try
                    {
                        string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyAppId;
                        if (root.OpenSubKey(path) != null)
                        {
                            root.DeleteSubKeyTree(path);
                            if (log != null) log("  已清理旧版卸载项：" + LegacyAppId);
                        }
                    }
                    catch (Exception) { }
                }

                // 旧快捷方式（开始菜单目录 + 桌面）
                foreach (string legacyName in new[] { LegacyAppId, "EasyFindImageTool" })
                {
                    DeleteShortcut(StartMenuDir(legacyName) + "\\" + legacyName + ".lnk");
                    DeleteShortcut(DesktopPath(legacyName + ".lnk"));
                }
                try { if (Directory.Exists(StartMenuDir(LegacyAppId))) Directory.Delete(StartMenuDir(LegacyAppId), true); }
                catch (Exception) { }

                // 旧安装目录（Program Files 或 LOCALAPPDATA 下）
                foreach (string rootDir in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                {
                    if (string.IsNullOrEmpty(rootDir)) continue;
                    string legacyDir = Path.Combine(rootDir, LegacyAppId);
                    if (!Directory.Exists(legacyDir)) continue;
                    try
                    {
                        foreach (string f in Directory.GetFiles(legacyDir)) { try { File.Delete(f); } catch (Exception) { } }
                        foreach (string d in Directory.GetDirectories(legacyDir)) { try { Directory.Delete(d, true); } catch (Exception) { } }
                        try { Directory.Delete(legacyDir); } catch (Exception) { }
                        if (log != null) log("  已清理旧版安装目录：" + legacyDir);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }
}
