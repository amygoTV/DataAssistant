using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DataAssistant
{
    /// <summary>优化项的风险等级。</summary>
    public enum OptRisk
    {
        /// <summary>普通：不改变系统行为，或本身即为"辅助动作"。</summary>
        Normal = 0,
        /// <summary>可逆：改的是注册表/服务项，可在「一键还原」里回滚。</summary>
        Reversible = 1,
        /// <summary>高危：会明显改变系统行为（关更新、迁用户库、迁数据目录等）。</summary>
        High = 2
    }

    /// <summary>一个可勾选执行的系统优化项。</summary>
    public class OptItem
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        /// <summary>分组名：系统设置 / 隐私强化。</summary>
        public string Category = string.Empty;
        public string Note = string.Empty;
        public OptRisk Risk = OptRisk.Normal;
        public bool DefaultChecked = true;
        /// <summary>true=需要管理员权限才能生效。</summary>
        public bool RequiresAdmin = true;
        /// <summary>实际执行体，入参为日志回调。</summary>
        public Action<Action<string>> Apply;

        public string RiskText
        {
            get
            {
                switch (Risk)
                {
                    case OptRisk.High: return "高危";
                    case OptRisk.Reversible: return "可逆";
                    default: return "普通";
                }
            }
        }

        public string Display
        {
            get { return string.Format("[{0}] {1}", RiskText, Title); }
        }
    }

    /// <summary>
    /// 优化工具的可持久化配置（路径配置页）。
    /// 用简单的 base64 行格式存盘，不依赖任何第三方序列化库。
    /// </summary>
    public class OptimizerConfig
    {
        public string DataRoot = @"D:\User";
        public string AIRoot = @"G:\Model";
        public List<string> AIJunkPaths = new List<string>();
        public List<string> DupScanFolders = new List<string>();
        public List<string> AIRedirectExtra = new List<string>();

        public static string ConfigDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DataAssistant");
            }
        }

        /// <summary>旧版本号（改名前的目录名）。升级时把配置与优化备份搬过去，避免用户数据丢失。</summary>
        private const string LegacyConfigDirName = "EasyFindImageTool";

        /// <summary>
        /// 首次运行时把旧目录 %APPDATA%\EasyFindImageTool 的内容迁移到新目录。
        /// 仅在新目录尚未建立时执行，迁移完成后在旧目录留下标记文件防止重复搬运。
        /// </summary>
        public static void MigrateLegacyConfig()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string legacy = Path.Combine(appData, LegacyConfigDirName);
                string current = ConfigDir;

                if (!Directory.Exists(legacy)) return;
                if (Directory.Exists(current))
                {
                    // 新目录已存在说明迁移做过了，只留标记，不再覆盖
                    try
                    {
                        string marker = Path.Combine(legacy, ".migrated_to_DataAssistant");
                        if (!File.Exists(marker)) File.WriteAllText(marker, current);
                    }
                    catch (Exception) { }
                    return;
                }

                Directory.CreateDirectory(current);
                foreach (string f in Directory.GetFiles(legacy))
                {
                    string name = Path.GetFileName(f);
                    if (name == ".migrated_to_DataAssistant") continue;
                    try { File.Copy(f, Path.Combine(current, name), true); }
                    catch (Exception) { }
                }
                foreach (string d in Directory.GetDirectories(legacy))
                {
                    string name = Path.GetFileName(d);
                    try
                    {
                        string dest = Path.Combine(current, name);
                        if (Directory.Exists(dest)) continue;
                        CopyDir(d, dest);
                    }
                    catch (Exception) { }
                }
                try { File.WriteAllText(Path.Combine(legacy, ".migrated_to_DataAssistant"), current); }
                catch (Exception) { }
            }
            catch (Exception) { }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch (Exception) { }
            foreach (string d in Directory.GetDirectories(src))
            {
                string target = Path.Combine(dst, Path.GetFileName(d));
                if (Directory.Exists(target)) continue;
                CopyDir(d, target);
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "optimizer.cfg"); }
        }

        /// <summary>AI 工具垃圾清理的默认路径清单（可含环境变量与通配符）。</summary>
        public static List<string> DefaultAIJunkPaths()
        {
            return new List<string>
            {
                // ComfyUI
                @"%LOCALAPPDATA%\ComfyUI\temp",
                @"%USERPROFILE%\ComfyUI\temp",
                @"%USERPROFILE%\ComfyUI\output\seg*",
                // Ollama（只清日志，不动模型）
                @"%LOCALAPPDATA%\Ollama\logs",
                @"%LOCALAPPDATA%\Programs\Ollama\logs",
                // 剪映 / CapCut
                @"%LOCALAPPDATA%\CapCut\Cache",
                @"%USERPROFILE%\AppData\Local\JianyingPro\Cache",
                // QClaw
                @"%LOCALAPPDATA%\QClaw\Cache",
                @"%LOCALAPPDATA%\QClaw\logs",
                // WorkBuddy（只清缓存，不动配置）
                @"%LOCALAPPDATA%\workbuddy\Cache",
                @"%APPDATA%\workbuddy\Cache",
                // 即梦 / 通用 AI 客户端临时
                @"%LOCALAPPDATA%\Dreamina\Cache",
                @"%LOCALAPPDATA%\..\Temp\ai_*",
                // OpenCode（只清日志/cache，不动核心配置）
                @"%USERPROFILE%\.config\opencode\logs",
                @"%LOCALAPPDATA%\opencode\logs",
                @"%LOCALAPPDATA%\opencode\cache",
                // CodeBuddy（腾讯）
                @"%LOCALAPPDATA%\CodeBuddy\Cache",
                @"%APPDATA%\CodeBuddy\Cache",
                @"%LOCALAPPDATA%\Tencent\CodeBuddy\Cache",
                @"%LOCALAPPDATA%\Tencent\CodeBuddy\logs",
                // 豆包（字节跳动）
                @"%LOCALAPPDATA%\Doubao\Cache",
                @"%APPDATA%\Doubao\Cache",
                @"%LOCALAPPDATA%\Doubao\logs",
                @"%LOCALAPPDATA%\Programs\Doubao\logs"
            };
        }

        /// <summary>AI 产出目录重定向映射：源路径（可含环境变量） -&gt; 目标根目录下的相对子目录。</summary>
        public static List<KeyValuePair<string, string>> DefaultAIRedirectMap()
        {
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(@"%USERPROFILE%\ComfyUI\output",   @"ComfyUI\output"),
                new KeyValuePair<string, string>(@"%USERPROFILE%\ComfyUI\temp",     @"ComfyUI\temp"),
                new KeyValuePair<string, string>(@"%USERPROFILE%\.ollama",          @"Ollama"),
                new KeyValuePair<string, string>(@"%USERPROFILE%\.config\opencode", @"OpenCode"),
                new KeyValuePair<string, string>(@"%USERPROFILE%\.workbuddy",       @"WorkBuddy"),
                new KeyValuePair<string, string>(@"%LOCALAPPDATA%\QClaw",            @"QClaw"),
                new KeyValuePair<string, string>(@"%LOCALAPPDATA%\Tencent\CodeBuddy", @"CodeBuddy"),
                new KeyValuePair<string, string>(@"%LOCALAPPDATA%\Doubao",           @"Doubao")
            };
        }

        /// <summary>重复文件扫描的默认目录。</summary>
        public static List<string> DefaultDupFolders()
        {
            return new List<string>
            {
                @"D:\User",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        public static OptimizerConfig Load()
        {
            var cfg = new OptimizerConfig();
            // 先铺默认值：配置文件不存在时用它；存在但某项为空则视为"用户主动清空"
            cfg.AIJunkPaths = DefaultAIJunkPaths();
            cfg.DupScanFolders = DefaultDupFolders();
            try
            {
                if (!File.Exists(ConfigPath)) return cfg;
                foreach (var raw in File.ReadAllLines(ConfigPath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string key = line.Substring(0, idx);
                    string val = line.Substring(idx + 1);
                    switch (key)
                    {
                        case "DataRoot": cfg.DataRoot = Unb(val); break;
                        case "AIRoot": cfg.AIRoot = Unb(val); break;
                        case "AIJunkPaths": cfg.AIJunkPaths = UnbList(val); break;
                        case "DupScanFolders": cfg.DupScanFolders = UnbList(val); break;
                        case "AIRedirectExtra": cfg.AIRedirectExtra = UnbList(val); break;
                    }
                }
            }
            catch (Exception) { }
            return cfg;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var sb = new StringBuilder();
                sb.AppendLine("# 数据助手 优化工具配置（base64 编码，勿手工改）");
                sb.AppendLine("DataRoot=" + B64(DataRoot));
                sb.AppendLine("AIRoot=" + B64(AIRoot));
                sb.AppendLine("AIJunkPaths=" + B64List(AIJunkPaths));
                sb.AppendLine("DupScanFolders=" + B64List(DupScanFolders));
                sb.AppendLine("AIRedirectExtra=" + B64List(AIRedirectExtra));
                File.WriteAllText(ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static string B64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }

        private static string Unb(string s)
        {
            try { return string.IsNullOrEmpty(s) ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (Exception) { return ""; }
        }

        private static string B64List(List<string> list)
        {
            if (list == null || list.Count == 0) return "";
            var parts = new List<string>();
            foreach (var s in list) parts.Add(B64(s));
            return string.Join("|", parts.ToArray());
        }

        private static List<string> UnbList(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split('|'))
            {
                if (part.Length == 0) continue;
                var v = Unb(part);
                if (!string.IsNullOrEmpty(v)) list.Add(v);
            }
            return list;
        }
    }

    /// <summary>
    /// 系统设置 / 隐私强化类优化项的核心实现。
    /// 所有对注册表与服务的改动都会先写入备份文件，可用 <see cref="RestoreAll"/> 一键回滚。
    /// </summary>
    public static class WindowsOptimizerSettings
    {
        // ====================== 备份 / 还原 ======================

        private const string BackupDirName = "OptimizerBackup";
        private const string BackupFileName = "registry_backup.txt";

        public static string BackupDir
        {
            get { return Path.Combine(OptimizerConfig.ConfigDir, BackupDirName); }
        }

        public static string BackupPath
        {
            get { return Path.Combine(BackupDir, BackupFileName); }
        }

        private class BackupEntry
        {
            public string Hive = "HKCU";
            public string Path = string.Empty;
            public string Name = string.Empty;
            /// <summary>String / ExpandString / DWord / QWord / Binary / MultiString / None / Custom</summary>
            public string Kind = "None";
            public byte[] Data;
        }

        private static readonly object _backupLock = new object();
        private static List<BackupEntry> _backupCache;

        private static List<BackupEntry> LoadBackup()
        {
            if (_backupCache != null) return _backupCache;
            var list = new List<BackupEntry>();
            try
            {
                if (File.Exists(BackupPath))
                {
                    foreach (var raw in File.ReadAllLines(BackupPath, Encoding.UTF8))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        var p = line.Split('|');
                        if (p.Length < 5) continue;
                        try
                        {
                            var e = new BackupEntry
                            {
                                Hive = Encoding.UTF8.GetString(Convert.FromBase64String(p[0])),
                                Path = Encoding.UTF8.GetString(Convert.FromBase64String(p[1])),
                                Name = Encoding.UTF8.GetString(Convert.FromBase64String(p[2])),
                                Kind = p[3],
                                Data = p[4].Length == 0 ? new byte[0] : Convert.FromBase64String(p[4])
                            };
                            list.Add(e);
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            _backupCache = list;
            return list;
        }

        private static void SaveBackup()
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                var sb = new StringBuilder();
                sb.AppendLine("# 数据助手 系统优化备份（base64 编码）");
                foreach (var e in LoadBackup())
                {
                    sb.AppendLine(string.Join("|",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Hive)),
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Path)),
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Name)),
                        e.Kind,
                        e.Data == null ? "" : Convert.ToBase64String(e.Data)));
                }
                File.WriteAllText(BackupPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static RegistryKey RootOf(string hive)
        {
            return hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        }

        /// <summary>
        /// 在改动某个注册表值之前调用：把原值（含"原本不存在"这一事实）记进备份。
        /// 同一项重复调用时只记录第一次的原值，保证还原能回到最初状态。
        /// </summary>
        private static void BackupValue(string hive, string path, string name)
        {
            lock (_backupLock)
            {
                var list = LoadBackup();
                foreach (var e in list)
                {
                    if (e.Hive == hive && e.Path == path && e.Name == name && e.Kind != "Custom")
                        return; // 已记录过，保留最初的原值
                }

                var entry = new BackupEntry { Hive = hive, Path = path, Name = name };
                try
                {
                    using (RegistryKey k = RootOf(hive).OpenSubKey(path, false))
                    {
                        if (k == null)
                        {
                            entry.Kind = "None";
                            entry.Data = new byte[0];
                        }
                        else
                        {
                            object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            RegistryValueKind kind = k.GetValueKind(name);
                            entry.Kind = kind.ToString();
                            entry.Data = EncodeValue(v, kind);
                        }
                    }
                }
                catch (Exception)
                {
                    entry.Kind = "None";
                    entry.Data = new byte[0];
                }
                list.Add(entry);
                SaveBackup();
            }
        }

        /// <summary>记录一条自定义备份（电源方案 GUID 等非注册表项）。</summary>
        private static void BackupCustom(string key, string value)
        {
            lock (_backupLock)
            {
                var list = LoadBackup();
                foreach (var e in list)
                    if (e.Kind == "Custom" && e.Name == key) return;
                list.Add(new BackupEntry
                {
                    Hive = "CUSTOM",
                    Path = string.Empty,
                    Name = key,
                    Kind = "Custom",
                    Data = Encoding.UTF8.GetBytes(value ?? string.Empty)
                });
                SaveBackup();
            }
        }

        private static byte[] EncodeValue(object v, RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    int d = Convert.ToInt32(v);
                    return BitConverter.GetBytes(d);
                case RegistryValueKind.QWord:
                    long q = Convert.ToInt64(v);
                    return BitConverter.GetBytes(q);
                case RegistryValueKind.Binary:
                    return (byte[])v;
                case RegistryValueKind.MultiString:
                    string[] arr = v as string[];
                    if (arr == null) return new byte[0];
                    return Encoding.Unicode.GetBytes(string.Join("\0", arr));
                default:
                    return Encoding.Unicode.GetBytes(v == null ? string.Empty : v.ToString());
            }
        }

        private static object DecodeValue(byte[] data, string kind)
        {
            switch (kind)
            {
                case "DWord":
                    return data != null && data.Length >= 4 ? (object)BitConverter.ToInt32(data, 0) : 0;
                case "QWord":
                    return data != null && data.Length >= 8 ? (object)BitConverter.ToInt64(data, 0) : 0L;
                case "Binary":
                    return data ?? new byte[0];
                case "MultiString":
                    if (data == null || data.Length == 0) return new string[0];
                    return Encoding.Unicode.GetString(data).Split('\0');
                default:
                    return data == null ? string.Empty : Encoding.Unicode.GetString(data);
            }
        }

        /// <summary>
        /// 一键还原：把备份里的所有注册表值/服务项恢复原状。
        /// 返回成功还原的条数。
        /// </summary>
        public static int RestoreAll(Action<string> log)
        {
            int ok = 0, fail = 0;
            lock (_backupLock)
            {
                var list = LoadBackup();
                // 倒序还原，后改的先还原
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var e = list[i];
                    try
                    {
                        if (e.Kind == "Custom")
                        {
                            string v = Encoding.UTF8.GetString(e.Data);
                            if (e.Name == "power_scheme" && !string.IsNullOrEmpty(v))
                            {
                                RunCmd("powercfg", "/setactive " + v, log, "还原电源计划");
                            }
                            else if (e.Name == "temp_path")
                            {
                                SetUserEnv("TEMP", v);
                                SetUserEnv("TMP", v);
                            }
                            else if (e.Name == "ollama_models")
                            {
                                SetUserEnv("OLLAMA_MODELS", v);
                            }
                            ok++;
                            continue;
                        }

                        RegistryKey root = RootOf(e.Hive);
                        using (RegistryKey k = root.OpenSubKey(e.Path, true))
                        {
                            if (k == null) { fail++; continue; }
                            if (e.Kind == "None")
                            {
                                k.DeleteValue(e.Name, false);
                            }
                            else
                            {
                                RegistryValueKind rk = RegistryValueKind.String;
                                if (e.Kind == "DWord") rk = RegistryValueKind.DWord;
                                else if (e.Kind == "QWord") rk = RegistryValueKind.QWord;
                                else if (e.Kind == "Binary") rk = RegistryValueKind.Binary;
                                else if (e.Kind == "MultiString") rk = RegistryValueKind.MultiString;
                                else if (e.Kind == "ExpandString") rk = RegistryValueKind.ExpandString;
                                k.SetValue(e.Name, DecodeValue(e.Data, e.Kind), rk);
                            }
                            ok++;
                            if (log != null) log("  已还原 " + e.Hive + "\\" + e.Path + "\\" + e.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        if (log != null) log("  还原失败 " + e.Hive + "\\" + e.Path + "\\" + e.Name + " -> " + ex.Message);
                    }
                }
                if (fail == 0) ClearBackup();
            }
            if (log != null) log(string.Format("还原完成：成功 {0} 项，失败 {1} 项。", ok, fail));
            return ok;
        }

        /// <summary>清空备份文件（还原成功后调用，或用户主动放弃还原时调用）。</summary>
        public static void ClearBackup()
        {
            lock (_backupLock)
            {
                _backupCache = new List<BackupEntry>();
                try { if (File.Exists(BackupPath)) File.Delete(BackupPath); }
                catch (Exception) { }
            }
        }

        public static int BackupCount
        {
            get { lock (_backupLock) { return LoadBackup().Count; } }
        }

        // ====================== 注册表写入助手 ======================

        public static void SetDword(string hive, string path, string name, int value, Action<string> log)
        {
            using (RegistryKey k = OpenOrCreate(hive, path))
            {
                BackupValue(hive, path, name);
                k.SetValue(name, value, RegistryValueKind.DWord);
            }
            if (log != null) log("    " + hive + "\\" + path + "\\" + name + " = " + value);
        }

        public static void SetString(string hive, string path, string name, string value, Action<string> log)
        {
            using (RegistryKey k = OpenOrCreate(hive, path))
            {
                BackupValue(hive, path, name);
                k.SetValue(name, value, RegistryValueKind.String);
            }
            if (log != null) log("    " + hive + "\\" + path + "\\" + name + " = " + value);
        }

        private static RegistryKey OpenOrCreate(string hive, string path)
        {
            return RootOf(hive).CreateSubKey(path);
        }

        private static void SetUserEnv(string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Environment", true))
                {
                    if (k != null)
                    {
                        k.SetValue(name, value, RegistryValueKind.ExpandString);
                        return;
                    }
                }
                using (RegistryKey k2 = Registry.CurrentUser.CreateSubKey(@"Environment"))
                {
                    if (k2 != null) k2.SetValue(name, value, RegistryValueKind.ExpandString);
                }
            }
            catch (Exception) { }
        }

        // ====================== 服务控制 ======================

        /// <summary>设置服务的启动类型并记录原值，便于还原。</summary>
        public static void SetServiceStartup(string serviceName, string mode, Action<string> log)
        {
            // 原启动类型存在注册表 Start 值里，先备份
            BackupValue("HKLM", @"SYSTEM\CurrentControlSet\Services\" + serviceName, "Start");
            string arg = mode == "disabled" ? "disabled" : (mode == "auto" ? "auto" : "demand");
            RunCmd("sc.exe", "config \"" + serviceName + "\" start= " + arg, log, "服务 " + serviceName + " 启动类型 -> " + arg);
        }

        public static void StopService(string serviceName, Action<string> log)
        {
            RunCmd("sc.exe", "stop \"" + serviceName + "\"", log, "停止服务 " + serviceName);
        }

        // ====================== 外部命令 ======================

        internal static bool RunCmd(string file, string args, Action<string> log, string title)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    if (log != null)
                    {
                        string msg = string.IsNullOrWhiteSpace(outp) ? err : outp;
                        log("    " + title + (string.IsNullOrWhiteSpace(msg) ? " 完成" : ": " + msg.Trim()));
                    }
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("    " + title + " 失败: " + ex.Message);
                return false;
            }
        }

        internal static bool RunCmdQuiet(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit(60000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception) { return false; }
        }

        // ====================== 系统设置类 ======================

        private class KnownFolderDef
        {
            public string Name;
            public string Guid;
            public KnownFolderDef(string name, string guid) { Name = name; Guid = guid; }
        }

        private static readonly KnownFolderDef[] KnownFolders = new KnownFolderDef[]
        {
            new KnownFolderDef("桌面",   "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"),
            new KnownFolderDef("文档",   "{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"),
            new KnownFolderDef("下载",   "{374DE290-123F-4565-9164-39C4925E467B}"),
            new KnownFolderDef("图片",   "{33E28130-4E1E-4676-835A-98395C3BC3BB}"),
            new KnownFolderDef("音乐",   "{4BD8D571-6D19-48D3-BE97-422220080E43}"),
            new KnownFolderDef("视频",   "{18989B1D-99B5-455B-841C-AB7C74E4DDFC}")
        };

        public static List<OptItem> BuildSystemItems()
        {
            var list = new List<OptItem>();

            list.Add(new OptItem
            {
                Id = "disable_updates",
                Title = "彻底关闭 Windows 自动更新",
                Category = "系统设置",
                Risk = OptRisk.High,
                Note = "写组策略 NoAutoUpdate=1 并停用 wuauserv。会明显降低安全性，建议定期手动更新 Defender 病毒库。",
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", 1, log);
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", 2, log);
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferFeatureUpdates", 1, log);
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DeferFeatureUpdatesPeriodInDays", 365, log);
                    StopService("wuauserv", log);
                    SetServiceStartup("wuauserv", "disabled", log);
                    RunCmdQuiet("gpupdate.exe", "/force");
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_faststartup",
                Title = "关闭快速启动",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Note = "HiberbootEnabled=0，需重启生效。",
                Apply = log =>
                {
                    SetDword("HKLM", @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "show_thispc",
                Title = "显示「此电脑」桌面图标",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Note = "刷新桌面或重新登录后生效。",
                Apply = log =>
                {
                    SetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
                        "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", 0, log);
                    RunCmdQuiet("rundll32.exe", "user32.dll,UpdatePerUserSystemParameters");
                }
            });

            list.Add(new OptItem
            {
                Id = "move_libraries",
                Title = "迁移用户库到指定盘（文档/下载/图片/音乐/视频/桌面）",
                Category = "系统设置",
                Risk = OptRisk.High,
                Note = "会用 SHSetKnownFolderPath 改路径并把旧文件 robocopy 过去。⚠ 会移动个人文件，请先备份！",
                Apply = log => MoveLibraries(log)
            });

            list.Add(new OptItem
            {
                Id = "temp_to_d",
                Title = "把用户 TEMP/TMP 指向数据盘",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Note = "设置用户环境变量，需重新登录生效。",
                Apply = log =>
                {
                    string root = OptimizerConfig.Load().DataRoot;
                    string tp = Path.Combine(root, "Temp");
                    try { Directory.CreateDirectory(tp); } catch (Exception) { }
                    BackupCustom("temp_path", Environment.GetEnvironmentVariable("TEMP", EnvironmentVariableTarget.User) ?? "");
                    SetUserEnv("TEMP", tp);
                    SetUserEnv("TMP", tp);
                    if (log != null) log("    TEMP/TMP -> " + tp + "（需重新登录生效）");
                }
            });

            list.Add(new OptItem
            {
                Id = "storage_sense",
                Title = "启用存储感知（自动清理）",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_delivery",
                Title = "关闭传递优化 P2P 上传",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_ads",
                Title = "关闭广告ID / 消费功能建议",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingId", "Enabled", 0, log);
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "show_file_ext",
                Title = "显示文件扩展名 + 隐藏文件",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
                    SetDword("HKCU", adv, "HideFileExt", 0, log);
                    SetDword("HKCU", adv, "Hidden", 1, log);
                    SetDword("HKCU", adv, "ShowSuperHidden", 1, log);
                    RunCmdQuiet("rundll32.exe", "user32.dll,UpdatePerUserSystemParameters");
                }
            });

            list.Add(new OptItem
            {
                Id = "power_high_perf",
                Title = "电源计划设为「高性能」",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Note = "高性能方案会提高功耗与风扇转速，笔记本慎用。",
                Apply = log =>
                {
                    string cur = GetActivePowerScheme();
                    if (!string.IsNullOrEmpty(cur)) BackupCustom("power_scheme", cur);
                    RunCmd("powercfg.exe", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", log, "切换电源计划");
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_tips",
                Title = "关闭 Windows 提示与技巧（诊断数据设为基本）",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
                    SetDword("HKCU", cdm, "SoftLandingEnabled", 0, log);
                    SetDword("HKCU", cdm, "SubscribedContent-338389Enabled", 0, log);
                    SetDword("HKCU", cdm, "SystemPaneSuggestionsEnabled", 0, log);
                    SetDword("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 1, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_lockscreen",
                Title = "关闭锁屏",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                DefaultChecked = false,
                Note = "重新登录后生效。",
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "disable_hibernate",
                Title = "关闭休眠释放空间（高风险，默认关）",
                Category = "系统设置",
                Risk = OptRisk.High,
                DefaultChecked = false,
                Note = "powercfg /h off，会删除 hiberfil.sys（约为内存大小），且快速启动随之不可用。",
                Apply = log =>
                {
                    RunCmd("powercfg.exe", "/h off", log, "关闭休眠");
                }
            });

            list.Add(new OptItem
            {
                Id = "ai_redirect",
                Title = "AI 产出/缓存重定向到数据盘（junction，可逆）",
                Category = "系统设置",
                Risk = OptRisk.High,
                Note = "把 ComfyUI/Ollama/OpenCode/WorkBuddy/CodeBuddy/QClaw/豆包 的产出目录用 mklink /J 指向统一根目录。⚠ 执行前请关闭相关 AI 工具；可逆：删掉 junction、把内容移回原路径。",
                Apply = log => AiRedirect(log)
            });

            list.Add(new OptItem
            {
                Id = "taskbar_nocombine",
                Title = "任务栏按钮不合并（永不合并）",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Note = "需重启文件资源管理器或重新登录后生效。",
                Apply = log =>
                {
                    string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
                    SetDword("HKCU", adv, "TaskbarGlomLevel", 2, log);
                    SetDword("HKCU", adv, "MMTaskbarGlomLevel", 2, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "explorer_nav_thispc",
                Title = "资源管理器左侧导航显示「此电脑」",
                Category = "系统设置",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    string nav = @"Software\Classes\CLSID\{20D04FE0-3AEA-1069-A2D8-08002B30309D}\ShellFolder";
                    SetDword("HKCU", nav, "Attributes", ExplorerNavAttributes, log);
                }
            });

            return list;
        }

        /// <summary>让「此电脑」在资源管理器左侧导航中显示的 Attributes 值（可逆）。</summary>
        private const int ExplorerNavAttributes = unchecked((int)0xB000004D);

        // ====================== 隐私强化类 ======================

        public static List<OptItem> BuildPrivacyItems()
        {
            var list = new List<OptItem>();

            list.Add(new OptItem
            {
                Id = "privacy_telemetry",
                Title = "关闭诊断遥测 + 反馈邀请 + 体验改善计划",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0, log);
                    SetDword("HKCU", @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, log);
                    SetDword("HKCU", @"Software\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", 0, log);
                    SetDword("HKLM", @"SOFTWARE\Microsoft\SQMClient\Windows", "CEIPEnable", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "privacy_location",
                Title = "禁用定位服务并清除位置历史",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetString("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location",
                        "Value", "Deny", log);
                    SetServiceStartup("lfsvc", "disabled", log);
                    StopService("lfsvc", log);
                    string hist = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\Windows\GeolocationHistory");
                    TryDeleteDirContents(hist, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "privacy_timeline",
                Title = "关闭活动历史 / Timeline 记录与云同步",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    string p = @"SOFTWARE\Policies\Microsoft\Windows\System";
                    SetDword("HKLM", p, "EnableActivityHistory", 0, log);
                    SetDword("HKLM", p, "PublishUserActivities", 0, log);
                    SetDword("HKLM", p, "UploadUserActivities", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "privacy_diagtrack",
                Title = "禁用 Diagnostic Tracking 遥测服务（diagtrack）",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetServiceStartup("diagtrack", "disabled", log);
                    StopService("diagtrack", log);
                }
            });

            list.Add(new OptItem
            {
                Id = "privacy_clipboard",
                Title = "关闭剪贴板历史与跨设备云同步",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                DefaultChecked = false,
                Apply = log =>
                {
                    string cb = @"Software\Microsoft\Windows\Clipboard";
                    SetDword("HKCU", cb, "EnableClipboardHistory", 0, log);
                    SetDword("HKCU", cb, "EnableCloudClipboard", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "privacy_cortana",
                Title = "禁用 Cortana（搜索功能仍可用）",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                DefaultChecked = false,
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "search_noweb",
                Title = "禁用搜索中的 Web/Bing 结果",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "DisableWebSearch", 1, log);
                    string s = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search";
                    SetDword("HKLM", s, "BingSearchEnabled", 0, log);
                    SetDword("HKLM", s, "CortanaConsent", 0, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "search_nosuggest",
                Title = "关闭搜索框在线（联网）建议",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    SetDword("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, log);
                }
            });

            list.Add(new OptItem
            {
                Id = "start_nosuggestions",
                Title = "关闭开始菜单广告 / 建议项",
                Category = "隐私强化",
                Risk = OptRisk.Reversible,
                Apply = log =>
                {
                    string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
                    SetDword("HKCU", cdm, "SystemPaneSuggestionsEnabled", 0, log);
                    SetDword("HKCU", cdm, "SubscribedContent-338388Enabled", 0, log);
                    SetDword("HKCU", cdm, "SubscribedContent-338389Enabled", 0, log);
                    SetDword("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", 0, log);
                }
            });

            return list;
        }

        /// <summary>系统设置类 + 隐私强化类 全部条目。</summary>
        public static List<OptItem> BuildAll()
        {
            var list = new List<OptItem>();
            list.AddRange(BuildSystemItems());
            list.AddRange(BuildPrivacyItems());
            return list;
        }

        // ====================== 具体动作实现 ======================

        private static void MoveLibraries(Action<string> log)
        {
            var cfg = OptimizerConfig.Load();
            string root = cfg.DataRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                if (log != null) log("    未配置用户库目标根目录，已跳过");
                return;
            }
            if (log != null) log("    目标根目录: " + root + "（⚠ 会移动个人文件，请确保已备份）");

            foreach (var kf in KnownFolders)
            {
                try
                {
                    string target = Path.Combine(root, kf.Name);
                    try { Directory.CreateDirectory(target); } catch (Exception) { }

                    // 备份原路径，便于还原
                    BackupValue("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", kf.Guid);

                    Guid g;
                    if (!Guid.TryParse(kf.Guid, out g)) continue;
                    int res = SHSetKnownFolderPath(g, 0, IntPtr.Zero, target);
                    if (res != 0)
                    {
                        if (log != null) log("    [" + kf.Name + "] 设置失败，错误码 " + res);
                        continue;
                    }
                    if (log != null) log("    已将 [" + kf.Name + "] 指向 " + target);

                    // 把旧位置的内容搬过去
                    string old = GetShellFolderPath(kf.Guid);
                    if (!string.IsNullOrEmpty(old) && Directory.Exists(old)
                        && !old.TrimEnd('\\').Equals(target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        if (log != null) log("    正在迁移旧内容: " + old + " -> " + target);
                        RunCmd("robocopy.exe", "\"" + old + "\" \"" + target + "\" /E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS",
                            null, "迁移 " + kf.Name);
                    }
                }
                catch (Exception ex)
                {
                    if (log != null) log("    [" + kf.Name + "] 出错: " + ex.Message);
                }
            }
            if (log != null) log("    用户库迁移完成（需重新登录后生效）");
        }

        private static string GetShellFolderPath(string guid)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                {
                    if (k == null) return null;
                    object v = k.GetValue(guid);
                    return v == null ? null : v.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        private static void AiRedirect(Action<string> log)
        {
            var cfg = OptimizerConfig.Load();
            string root = cfg.AIRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                if (log != null) log("    未配置 AI 目标根目录，已跳过");
                return;
            }
            try { Directory.CreateDirectory(root); } catch (Exception) { }
            if (log != null) log("    目标根目录: " + root + "（⚠ 请确保相关 AI 工具已关闭）");

            var map = OptimizerConfig.DefaultAIRedirectMap();
            foreach (var e in cfg.AIRedirectExtra)
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                string expanded = Environment.ExpandEnvironmentVariables(e.Trim());
                if (expanded.Length == 0) continue;
                map.Add(new KeyValuePair<string, string>(expanded, Path.GetFileName(expanded.TrimEnd('\\'))));
            }

            foreach (var kv in map)
            {
                string src = Environment.ExpandEnvironmentVariables(kv.Key);
                string sub = kv.Value;
                string target = Path.Combine(root, sub);
                try
                {
                    try { Directory.CreateDirectory(target); } catch (Exception) { }
                    if (Directory.Exists(src))
                    {
                        var attr = File.GetAttributes(src);
                        if ((attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                        {
                            if (log != null) log("    已是 junction，跳过: " + src);
                            continue;
                        }
                        if (log != null) log("    迁移现有内容: " + src + " -> " + target);
                        RunCmd("robocopy.exe", "\"" + src + "\" \"" + target + "\" /E /MOVE /R:1 /W:1 /NFL /NDL /NJH /NJS",
                            null, "迁移 " + sub);
                        try { Directory.Delete(src, true); } catch (Exception) { }
                    }
                    RunCmd("cmd.exe", "/c mklink /J \"" + src + "\" \"" + target + "\"", log, "建立 junction");
                    if (log != null) log("    已 junction: " + src + " -> " + target);
                }
                catch (Exception ex)
                {
                    if (log != null) log("    重定向失败: " + src + " -> " + target + " (" + ex.Message + ")");
                }
            }

            // Ollama 模型目录环境变量
            try
            {
                string om = Path.Combine(root, @"Ollama\models");
                Directory.CreateDirectory(om);
                BackupCustom("ollama_models", Environment.GetEnvironmentVariable("OLLAMA_MODELS", EnvironmentVariableTarget.User) ?? "");
                SetUserEnv("OLLAMA_MODELS", om);
                if (log != null) log("    已设置 OLLAMA_MODELS=" + om);
            }
            catch (Exception) { }
        }

        internal static string GetActivePowerScheme()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", "/getactivescheme")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    int i = outp.IndexOf('{');
                    int j = outp.IndexOf('}', i);
                    if (i >= 0 && j > i) return outp.Substring(i + 1, j - i - 1);
                    return null;
                }
            }
            catch (Exception) { return null; }
        }

        internal static void TryDeleteDirContents(string dir, Action<string> log)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { File.Delete(f); } catch (Exception) { }
                }
                foreach (var d in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(d, true); } catch (Exception) { }
                }
                if (log != null) log("    已清空 " + dir);
            }
            catch (Exception) { }
        }

        // ====================== 系统还原点 ======================

        /// <summary>创建系统还原点（需管理员权限，且系统需开启「系统保护」）。</summary>
        public static bool CreateRestorePoint(string description, Action<string> log)
        {
            try
            {
                string desc = string.IsNullOrWhiteSpace(description) ? "数据助手 优化前" : description;
                var info = new RESTOREPOINTINFOM
                {
                    dwEventType = 12,             // BEGIN_SYSTEM_CHANGE
                    dwRestorePointType = 12,      // MODIFY_SETTINGS
                    lpEventDescription = desc,
                    dwEventDescriptionLength = desc.Length,
                    frequency = 0
                };
                long id;
                bool ok = SRSetRestorePointW(ref info, out id);
                if (ok)
                {
                    if (log != null) log("    已创建系统还原点（序号 " + id + "）");
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                if (log != null)
                {
                    log("    创建还原点失败：Win32 错误 " + err + " —— " + DescribeRestoreError(err));
                    log("    常见原因：① 本机未开启「系统保护」；② System Restore 相关服务被禁用；③ 非管理员权限。");
                }
                return false;
            }
            catch (Exception ex)
            {
                if (log != null) log("    创建还原点异常: " + ex.Message);
                return false;
            }
        }

        private static string DescribeRestoreError(int err)
        {
            switch (err)
            {
                case 0: return "未提供错误信息";
                case 5: return "拒绝访问（非管理员权限）";
                case 1057: return "该服务未启动";
                case 1058: return "服务被禁用（系统还原/卷影复制未启用）";
                case 1069: return "服务无法启动（系统保护未开启）";
                default: return new System.ComponentModel.Win32Exception(err).Message;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RESTOREPOINTINFOM
        {
            public int dwEventType;
            public int dwRestorePointType;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpEventDescription;
            public int dwEventDescriptionLength;
            public int frequency;
        }

        [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFOM pRestorePointInfo, out long pRestorePointId);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHSetKnownFolderPath(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags, IntPtr hToken, string pszPath);
    }
}
