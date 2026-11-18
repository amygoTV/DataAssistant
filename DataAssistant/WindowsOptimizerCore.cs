using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace DataAssistant
{
    /// <summary>
    /// Windows 系统优化核心引擎（与 UI 无关，可直接在任意程序里调用）。
    /// 包含：垃圾文件清理、磁盘空间分析、启动项管理、性能优化。
    /// 所有破坏性操作均需显式调用，引擎本身不做任何自动清理。
    /// </summary>
    public static class WindowsOptimizerCore
    {
        // ====================== 垃圾清理 ======================

        /// <summary>一个可清理目标。</summary>
        public class CleanTarget
        {
            public string Id = string.Empty;
            public string DisplayName = string.Empty;
            public string Path = string.Empty;
            /// <summary>true=目录，false=单个文件。</summary>
            public bool IsDirectory = true;
            /// <summary>回收站需要用 SHEmptyRecycleBin 单独处理。</summary>
            public bool IsRecycleBin;
            /// <summary>只清理目录下匹配这些通配符的文件；null=全部文件。</summary>
            public string[] FilePatterns;
            public long SizeBytes;
            public int FileCount;
            /// <summary>当前进程是否可访问（权限不足时置 false，清理时跳过）。</summary>
            public bool Accessible = true;
            public string Note = string.Empty;
            /// <summary>默认勾选。</summary>
            public bool Selected = true;

            public string SizeText { get { return FormatSize(SizeBytes); } }

            public override string ToString()
            {
                return string.Format("{0}    [{1}]", DisplayName, SizeText);
            }
        }

        /// <summary>列出所有可清理的目标（不计算大小，扫描阶段才统计）。</summary>
        public static List<CleanTarget> BuildCleanTargets()
        {
            var list = new List<CleanTarget>();

            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string temp = Path.GetTempPath();
            string systemDrive = Path.GetPathRoot(win) ?? "C:\\";

            Add(list, "tmp_user", "用户临时文件", temp, true, null,
                "正在运行的程序可能正在占用，删不掉的会自动跳过");
            Add(list, "tmp_sys", "系统临时文件", Path.Combine(win, "Temp"), true, null, "");
            Add(list, "wu", "Windows 更新缓存",
                Path.Combine(win, @"SoftwareDistribution\Download"), true, null,
                "清理后下次更新需重新下载补丁");
            Add(list, "do", "传递优化缓存",
                Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization"), true, null, "");
            Add(list, "prefetch", "预读取缓存", Path.Combine(win, "Prefetch"), true, null,
                "会略微拖慢下一次开机");
            Add(list, "logs", "系统日志", Path.Combine(win, "Logs"), true, null, "");
            Add(list, "wer", "错误报告",
                Path.Combine(progData, @"Microsoft\Windows\WER"), true, null, "");
            Add(list, "minidump", "小型转储", Path.Combine(win, "Minidump"), true, null, "");
            Add(list, "memdmp", "内存转储", Path.Combine(win, "MEMORY.DMP"), false, null,
                "单个文件通常较大，删除后无法再分析蓝屏");
            Add(list, "inetcache", "网络/IE 缓存",
                Path.Combine(local, @"Microsoft\Windows\INetCache"), true, null, "");
            Add(list, "thumbs", "缩略图与图标缓存",
                Path.Combine(local, @"Microsoft\Windows\Explorer"), true,
                new[] { "thumbcache_*.db", "iconcache_*.db", "thumbcache_*.db.lock" },
                "只清理缩略图与图标缓存文件，不动其他内容");
            Add(list, "d3dscache", "DirectX 着色器缓存",
                Path.Combine(local, "D3DSCache"), true, null, "首次运行游戏会重新编译着色器");
            Add(list, "crashdump", "程序崩溃转储",
                Path.Combine(local, "CrashDumps"), true, null, "");
            Add(list, "upgrade", "系统升级残留",
                Path.Combine(systemDrive, "$Windows.~BT"), true, null,
                "确认系统已升级完成且运行正常再清理");
            Add(list, "old", "系统升级旧版本",
                Path.Combine(systemDrive, "Windows.old"), true, null,
                "删除后无法回退到上一个 Windows 版本");
            Add(list, "downloads", "下载目录中的安装包",
                Path.Combine(userProfile, "Downloads"), true,
                new[] { "*.exe", "*.msi", "*.msix", "*.appx", "*.iso", "*.zip" },
                "只清理安装包类文件，其他内容不动");

            AddBrowserCacheTargets(list, local);
            AddAiJunkTargets(list);

            list.Add(new CleanTarget
            {
                Id = "recycle",
                DisplayName = "回收站",
                Path = "所有磁盘的回收站",
                IsRecycleBin = true,
                Note = "清空后文件不可恢复"
            });

            return list;
        }

        /// <summary>浏览器缓存（Edge / Chrome / Firefox）。只清 Cache，不碰书签与密码。</summary>
        private static void AddBrowserCacheTargets(List<CleanTarget> list, string local)
        {
            string edge = Path.Combine(local, @"Microsoft\Edge\User Data\Default");
            string chrome = Path.Combine(local, @"Google\Chrome\User Data\Default");

            Add(list, "edge_cache", "Edge 缓存",
                Path.Combine(edge, "Cache"), true, null, "不影响书签、密码与登录状态");
            Add(list, "edge_codecache", "Edge 代码缓存",
                Path.Combine(edge, "Code Cache"), true, null, "");
            Add(list, "edge_media", "Edge 媒体缓存",
                Path.Combine(edge, "Media Cache"), true, null, "");

            Add(list, "chrome_cache", "Chrome 缓存",
                Path.Combine(chrome, "Cache"), true, null, "不影响书签、密码与登录状态");
            Add(list, "chrome_codecache", "Chrome 代码缓存",
                Path.Combine(chrome, "Code Cache"), true, null, "");
            Add(list, "chrome_media", "Chrome 媒体缓存",
                Path.Combine(chrome, "Media Cache"), true, null, "");

            // Firefox：每个 profile 下的 cache2
            try
            {
                string ffRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(ffRoot))
                {
                    foreach (var prof in Directory.GetDirectories(ffRoot))
                    {
                        string c2 = Path.Combine(prof, "cache2");
                        if (!Directory.Exists(c2)) continue;
                        Add(list, "ff_cache_" + Path.GetFileName(prof), "Firefox 缓存 (" + Path.GetFileName(prof) + ")",
                            c2, true, null, "");
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>AI 工具垃圾（ComfyUI / Ollama / 剪映 / 即梦 / QClaw / WorkBuddy / OpenCode / CodeBuddy / 豆包 等）。</summary>
        private static void AddAiJunkTargets(List<CleanTarget> list)
        {
            var paths = OptimizerConfig.Load().AIJunkPaths;
            if (paths == null) paths = new List<string>();

            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string expanded = Environment.ExpandEnvironmentVariables(raw.Trim());

                // 含通配符：展开成实际存在的目录，逐个成为独立清理项
                if (expanded.IndexOf('*') >= 0 || expanded.IndexOf('?') >= 0)
                {
                    try
                    {
                        string parent = Path.GetDirectoryName(expanded);
                        string leaf = Path.GetFileName(expanded);
                        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) continue;
                        if (!Directory.Exists(parent)) continue;
                        foreach (var d in Directory.GetDirectories(parent, leaf))
                        {
                            Add(list, "ai_" + d.GetHashCode().ToString("x"), "AI 垃圾: " + d,
                                d, true, null, "仅清缓存/日志/temp，不删核心数据");
                        }
                    }
                    catch (Exception) { }
                    continue;
                }

                Add(list, "ai_" + expanded.GetHashCode().ToString("x"), "AI 垃圾: " + expanded,
                    expanded, true, null, "仅清缓存/日志/temp，不删核心数据");
            }
        }

        private static void Add(List<CleanTarget> list, string id, string name, string path,
            bool isDir, string[] patterns, string note)
        {
            list.Add(new CleanTarget
            {
                Id = id,
                DisplayName = name,
                Path = path,
                IsDirectory = isDir,
                FilePatterns = patterns,
                Note = note
            });
        }

        /// <summary>统计目标占用的大小与文件数（不删除任何东西）。</summary>
        public static void Measure(CleanTarget t, CancellationToken token, Action<string> log)
        {
            t.SizeBytes = 0;
            t.FileCount = 0;
            t.Accessible = false;

            try
            {
                if (t.IsRecycleBin)
                {
                    t.Accessible = true;
                    return;
                }
                if (!t.IsDirectory)
                {
                    var fi = new FileInfo(t.Path);
                    if (fi.Exists)
                    {
                        t.SizeBytes = fi.Length;
                        t.FileCount = 1;
                    }
                    t.Accessible = true;
                    return;
                }
                if (!Directory.Exists(t.Path))
                {
                    if (log != null) log("  [跳过] 路径不存在: " + t.Path);
                    return;
                }
                t.Accessible = true;
                t.SizeBytes = DirectorySize(t.Path, t.FilePatterns, token, null);
                t.FileCount = CountFiles(t.Path, t.FilePatterns, token);
            }
            catch (Exception ex)
            {
                if (log != null) log("  [无法访问] " + t.Path + " -> " + ex.Message);
            }
        }

        /// <summary>删除目标下的文件（保留目录本身），返回释放的字节数。</summary>
        public static long Clean(CleanTarget t, Action<string> log, CancellationToken token)
        {
            if (t.IsRecycleBin)
            {
                EmptyRecycleBin(log);
                return 0;
            }

            long freed = 0;
            int deleted = 0, failed = 0;

            try
            {
                if (!t.IsDirectory)
                {
                    var fi = new FileInfo(t.Path);
                    if (fi.Exists)
                    {
                        long len = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += len;
                        deleted++;
                    }
                }
                else
                {
                    if (!Directory.Exists(t.Path)) return 0;
                    foreach (var file in EnumerateFilesSafe(t.Path, t.FilePatterns))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(file);
                            long len = fi.Length;
                            fi.Attributes = FileAttributes.Normal;
                            fi.Delete();
                            freed += len;
                            deleted++;
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (log != null) log("  [失败] " + t.Path + " -> " + ex.Message);
                return freed;
            }

            if (log != null)
            {
                log(string.Format("  {0}：删除 {1} 个文件{2}，释放 {3}",
                    t.DisplayName, deleted,
                    failed > 0 ? string.Format("（{0} 个因权限占用跳过）", failed) : "",
                    FormatSize(freed)));
            }
            return freed;
        }

        /// <summary>清空回收站。</summary>
        public static bool EmptyRecycleBin(Action<string> log)
        {
            try
            {
                // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
                int r = SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
                bool ok = r == 0;
                if (log != null)
                    log(ok ? "  回收站：已清空" : "  回收站：清空失败或本来就是空的 (HRESULT=" + r + ")");
                return ok;
            }
            catch (Exception ex)
            {
                if (log != null) log("  回收站：清空异常 " + ex.Message);
                return false;
            }
        }

        // ====================== 磁盘分析 ======================

        public class DriveRow
        {
            public string Name = string.Empty;
            public string Format = string.Empty;
            public long TotalBytes;
            public long FreeBytes;
            public long UsedBytes { get { return TotalBytes - FreeBytes; } }
            public int FreePercent
            {
                get { return TotalBytes <= 0 ? 0 : (int)(FreeBytes * 100 / TotalBytes); }
            }
            public string TotalText { get { return FormatSize(TotalBytes); } }
            public string FreeText { get { return FormatSize(FreeBytes); } }
            public string UsedText { get { return FormatSize(UsedBytes); } }
        }

        /// <summary>列出本机就绪的磁盘。</summary>
        public static List<DriveRow> GetDrives()
        {
            var rows = new List<DriveRow>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType == DriveType.CDRom || d.DriveType == DriveType.Removable) continue;
                    rows.Add(new DriveRow
                    {
                        Name = d.Name,
                        Format = d.DriveFormat,
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.AvailableFreeSpace
                    });
                }
                catch (Exception) { }
            }
            return rows;
        }

        public class DirSizeRow
        {
            public string Path = string.Empty;
            public long SizeBytes;
            public string SizeText { get { return FormatSize(SizeBytes); } }
        }

        /// <summary>统计 root 下各一级子目录的体积（用于找"谁占满了 C 盘"）。</summary>
        public static List<DirSizeRow> ScanBigDirs(string root, CancellationToken token, Action<string> progress)
        {
            var rows = new List<DirSizeRow>();
            if (!Directory.Exists(root)) return rows;

            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex)
            {
                if (progress != null) progress("无法枚举目录: " + ex.Message);
                return rows;
            }

            int i = 0, total = dirs.Count();
            foreach (var d in dirs)
            {
                token.ThrowIfCancellationRequested();
                i++;
                if (progress != null && (i == 1 || i % 5 == 0))
                    progress(string.Format("正在统计 {0} ({1}/{2}) ...", Path.GetFileName(d), i, total));
                rows.Add(new DirSizeRow { Path = d, SizeBytes = DirectorySize(d, null, token, null) });
            }

            // 根目录下的散落文件也统计一下
            try
            {
                long rootFiles = Directory.GetFiles(root).Sum(f =>
                {
                    try { return new FileInfo(f).Length; } catch { return 0L; }
                });
                if (rootFiles > 0)
                    rows.Add(new DirSizeRow { Path = root + " (根目录散落文件)", SizeBytes = rootFiles });
            }
            catch (Exception) { }

            rows.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            return rows;
        }

        // ====================== 重复文件查找 ======================

        public class DupFile
        {
            public string FullPath = string.Empty;
            public long Length;
            public string Hash = string.Empty;
            /// <summary>同组内是否是被保留的那一个。</summary>
            public bool Keeper;
            public string SizeText { get { return FormatSize(Length); } }
        }

        public class DupScanResult
        {
            public List<DupFile> Duplicates = new List<DupFile>();
            public int FilesScanned;
            public int FilesSkipped;
            public int Groups;
            public long RecoverableBytes;
            public double Seconds;
        }

        /// <summary>
        /// 按 MD5 找出重复文件。每组保留"路径最短 / 最早"的一个，其余作为可删候选。
        /// 枚举与哈希阶段各自独立 try/catch：目录被删、无权限、文件被占用都只跳过单项。
        /// </summary>
        public static DupScanResult FindDuplicates(IEnumerable<string> folders,
            CancellationToken token, Action<int> progress)
        {
            var result = new DupScanResult();
            var sw = Stopwatch.StartNew();
            var hashMap = new Dictionary<string, List<DupFile>>(StringComparer.OrdinalIgnoreCase);

            if (folders != null)
            {
                foreach (var f in folders)
                {
                    if (token.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    string dir = Environment.ExpandEnvironmentVariables(f.Trim());
                    try { if (!Directory.Exists(dir)) continue; }
                    catch (Exception) { result.FilesSkipped++; continue; }

                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
                    catch (Exception) { result.FilesSkipped++; continue; }

                    using (IEnumerator<string> en = files.GetEnumerator())
                    {
                        while (true)
                        {
                            string cur;
                            try
                            {
                                if (token.IsCancellationRequested) break;
                                if (!en.MoveNext()) break;
                                cur = en.Current;
                            }
                            catch (Exception) { break; }

                            result.FilesScanned++;
                            try
                            {
                                var fi = new FileInfo(cur);
                                if (!fi.Exists) continue;
                                long len = fi.Length;
                                if (len == 0) continue; // 空文件无意义，跳过

                                string h = ComputeMd5(cur);
                                if (string.IsNullOrEmpty(h)) { result.FilesSkipped++; continue; }

                                List<DupFile> bucket;
                                if (!hashMap.TryGetValue(h, out bucket))
                                {
                                    bucket = new List<DupFile>();
                                    hashMap[h] = bucket;
                                }
                                bucket.Add(new DupFile { FullPath = cur, Length = len, Hash = h });
                            }
                            catch (Exception)
                            {
                                result.FilesSkipped++;
                            }

                            if (progress != null && result.FilesScanned % 2000 == 0)
                                progress(result.FilesScanned);
                        }
                    }
                }
            }

            foreach (var kv in hashMap)
            {
                if (kv.Value.Count <= 1) continue;
                result.Groups++;
                // 路径最短的保留，其次是最早修改的
                var grp = kv.Value
                    .OrderBy(d => d.FullPath.Length)
                    .ThenBy(d => SafeLastWrite(d.FullPath))
                    .ToList();
                grp[0].Keeper = true;
                for (int i = 1; i < grp.Count; i++)
                {
                    result.Duplicates.Add(grp[i]);
                    result.RecoverableBytes += grp[i].Length;
                }
            }

            sw.Stop();
            result.Seconds = sw.Elapsed.TotalSeconds;
            return result;
        }

        private static DateTime SafeLastWrite(string path)
        {
            try { return File.GetLastWriteTime(path); }
            catch (Exception) { return DateTime.MaxValue; }
        }

        private static string ComputeMd5(string path)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
                {
                    byte[] hash = md5.ComputeHash(fs);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception) { return null; }
        }

        // ====================== 启动项管理 ======================

        public class StartupItem
        {
            public string Name = string.Empty;
            public string Command = string.Empty;
            /// <summary>HKCU / HKLM / StartupFolder</summary>
            public string Location = string.Empty;
            public string LocationText
            {
                get
                {
                    if (Location == "HKCU") return "当前用户 (HKCU)";
                    if (Location == "HKLM") return "所有用户 (HKLM)";
                    return "启动文件夹";
                }
            }
            public bool Enabled = true;
            /// <summary>唯一的内部标识，用于启停操作。</summary>
            public string Key = string.Empty;
            /// <summary>启动文件夹项对应的真实文件名（含 .disabled 后缀时为禁用态）。</summary>
            public string FileName = string.Empty;
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string DisabledKeyPath = @"Software\DataAssistant\DisabledStartup";

        /// <summary>旧版本的禁用项备份键（改名前）。升级后仍需能读到，才能还原用户此前禁用的启动项。</summary>
        private const string LegacyDisabledKeyPath = @"Software\EasyFindImageTool\DisabledStartup";

        /// <summary>从备份键读取被禁用启动项的原值；新键没有则回退到旧键。</summary>
        private static object GetDisabledBackupValue(RegistryKey root, string name)
        {
            foreach (string path in new[] { DisabledKeyPath, LegacyDisabledKeyPath })
            {
                try
                {
                    using (RegistryKey backup = root.OpenSubKey(path, false))
                    {
                        if (backup == null) continue;
                        object val = backup.GetValue(name);
                        if (val != null) return val;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        /// <summary>列出开机自启项（注册表 Run + RunOnce + 启动文件夹）。</summary>
        public static List<StartupItem> GetStartupItems()
        {
            var items = new List<StartupItem>();

            ReadRunKey(items, Registry.CurrentUser, RunKeyPath, "HKCU", @"HKCU\" + RunKeyPath);
            ReadRunKey(items, Registry.LocalMachine, RunKeyPath, "HKLM", @"HKLM\" + RunKeyPath);
            ReadRunKey(items, Registry.CurrentUser, RunKeyPath + @"\Once", "HKCU", @"HKCU\" + RunKeyPath + @"\Once");
            ReadRunKey(items, Registry.LocalMachine, RunKeyPath + @"\Once", "HKLM", @"HKLM\" + RunKeyPath + @"\Once");

            // 启动文件夹
            try
            {
                string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (Directory.Exists(startup))
                {
                    foreach (var f in Directory.GetFiles(startup))
                    {
                        string name = Path.GetFileName(f);
                        bool disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                        items.Add(new StartupItem
                        {
                            Name = disabled ? name.Substring(0, name.Length - 9) : name,
                            Command = f,
                            Location = "StartupFolder",
                            Enabled = !disabled,
                            Key = "startup|" + f,
                            FileName = name
                        });
                    }
                }
            }
            catch (Exception) { }

            return items;
        }

        private static void ReadRunKey(List<StartupItem> items, RegistryKey root, string sub, string loc, string displayPath)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(sub, false))
                {
                    if (key == null) return;
                    foreach (string name in key.GetValueNames())
                    {
                        object val = key.GetValue(name);
                        if (val == null) continue;
                        items.Add(new StartupItem
                        {
                            Name = name,
                            Command = val.ToString(),
                            Location = loc,
                            Enabled = true,
                            Key = loc + "|" + displayPath + "|" + name
                        });
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 启用/禁用启动项。注册表项采用"搬到备份键"的方式，可完整还原；
        /// 启动文件夹项采用改名（加 .disabled 后缀）的方式。
        /// </summary>
        public static bool SetStartupEnabled(StartupItem item, bool enabled, Action<string> log)
        {
            try
            {
                if (item.Location == "StartupFolder")
                    return SetStartupFolderEnabled(item, enabled, log);

                var parts = item.Key.Split(new[] { '|' }, 3);
                if (parts.Length < 3) return false;
                string loc = parts[0], path = parts[1], name = parts[2];

                RegistryKey root = loc == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using (RegistryKey runKey = root.OpenSubKey(path, true))
                {
                    if (runKey == null)
                    {
                        if (log != null) log("  启动项已不存在: " + name);
                        return false;
                    }
                    if (!enabled)
                    {
                        object val = runKey.GetValue(name);
                        if (val == null) return false;
                        using (RegistryKey backup = root.CreateSubKey(DisabledKeyPath))
                        {
                            backup.SetValue(name, val, RegistryValueKind.String);
                        }
                        runKey.DeleteValue(name, false);
                    }
                    else
                    {
                        object val = GetDisabledBackupValue(root, name);
                        if (val == null)
                        {
                            if (log != null) log("  未找到备份值，无法启用: " + name);
                            return false;
                        }
                        runKey.SetValue(name, val, RegistryValueKind.String);
                        foreach (string bp in new[] { DisabledKeyPath, LegacyDisabledKeyPath })
                        {
                            using (RegistryKey backup = root.OpenSubKey(bp, true))
                            {
                                if (backup != null) backup.DeleteValue(name, false);
                            }
                        }
                    }
                }
                if (log != null)
                    log(string.Format("  {0}：{1}", name, enabled ? "已启用" : "已禁用"));
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("  操作失败 " + item.Name + " -> " + ex.Message);
                return false;
            }
        }

        private static bool SetStartupFolderEnabled(StartupItem item, bool enabled, Action<string> log)
        {
            try
            {
                string dir = Path.GetDirectoryName(item.Command);
                string target = Path.Combine(dir, item.FileName);

                if (enabled)
                {
                    if (!target.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return true;
                    string restored = target.Substring(0, target.Length - 9);
                    if (File.Exists(restored)) return true;
                    File.Move(target, restored);
                }
                else
                {
                    if (target.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) return true;
                    File.Move(target, target + ".disabled");
                }
                if (log != null)
                    log(string.Format("  {0}：{1}", item.Name, enabled ? "已启用" : "已禁用"));
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("  操作失败 " + item.Name + " -> " + ex.Message);
                return false;
            }
        }

        // ====================== 性能优化 ======================

        public class MemoryStatus
        {
            public long TotalBytes;
            public long AvailableBytes;
            public long UsedBytes { get { return TotalBytes - AvailableBytes; } }
            public int UsedPercent { get { return TotalBytes <= 0 ? 0 : (int)(UsedBytes * 100 / TotalBytes); } }
            public string TotalText { get { return FormatSize(TotalBytes); } }
            public string UsedText { get { return FormatSize(UsedBytes); } }
            public string AvailableText { get { return FormatSize(AvailableBytes); } }
        }

        public static MemoryStatus GetMemoryStatus()
        {
            var st = new MemoryStatus();
            try
            {
                var mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(mem))
                {
                    st.TotalBytes = (long)mem.ullTotalPhys;
                    st.AvailableBytes = (long)mem.ullAvailPhys;
                }
            }
            catch (Exception) { }
            return st;
        }

        /// <summary>裁剪各进程工作集以释放内存，返回被处理的进程数。</summary>
        public static int ReleaseMemory(Action<string> log, CancellationToken token)
        {
            int count = 0;
            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch (Exception ex)
            {
                if (log != null) log("  枚举进程失败: " + ex.Message);
                return 0;
            }

            int self = Process.GetCurrentProcess().Id;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Idle", "System", "Registry", "Memory Compression", "csrss", "wininit", "winlogon",
                "services", "lsass", "svchost", "fontdrvhost", "dwm", "spoolsv", "smss",
                "SecurityHealthService", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost"
            };

            foreach (var p in procs)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (p.Id == self) continue;
                    string name = p.ProcessName;
                    if (skip.Contains(name)) continue;

                    IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        if (EmptyWorkingSet(h)) count++;
                    }
                    finally { CloseHandle(h); }
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }

            if (log != null) log(string.Format("  内存优化：已裁剪 {0} 个进程的工作集", count));
            return count;
        }

        /// <summary>清理 DNS 缓存。</summary>
        public static bool FlushDnsCache(Action<string> log)
        {
            return RunCommand("ipconfig", "/flushdns", "DNS 缓存清理", log);
        }

        /// <summary>清理 ARP 缓存。</summary>
        public static bool ClearArpCache(Action<string> log)
        {
            return RunCommand("netsh", "interface ip delete arpcache", "ARP 缓存清理", log);
        }

        /// <summary>刷新资源管理器（让桌面图标立即更新）。</summary>
        public static bool RefreshExplorer(Action<string> log)
        {
            try
            {
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
                if (log != null) log("  资源管理器已刷新");
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("  刷新资源管理器失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>开关窗口动画等视觉效果。</summary>
        public static bool SetVisualEffects(bool enable, Action<string> log)
        {
            try
            {
                // SPI_SETCLIENTAREAANIMATION = 0x1043, SPI_SETANIMATION = 0x1042
                uint onOff = enable ? 1u : 0u;
                SystemParametersInfo(0x1043, onOff, IntPtr.Zero, 0x0003);
                SystemParametersInfo(0x1042, onOff, IntPtr.Zero, 0x0003);
                if (log != null)
                    log(enable ? "  已恢复窗口动画效果" : "  已关闭窗口动画效果");
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("  设置视觉效果失败: " + ex.Message);
                return false;
            }
        }

        private static bool RunCommand(string file, string args, string title, Action<string> log)
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
                    p.WaitForExit(15000);
                    string outp = p.StandardOutput.ReadToEnd();
                    if (log != null) log("  " + title + (string.IsNullOrWhiteSpace(outp) ? " 完成" : ": " + outp.Trim()));
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("  " + title + " 失败: " + ex.Message);
                return false;
            }
        }

        // ====================== 公共工具 ======================

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.##")) + " " + units[i];
        }

        private static IEnumerable<string> EnumerateFilesSafe(string dir, string[] patterns)
        {
            if (patterns == null || patterns.Length == 0)
            {
                IEnumerable<string> all;
                try { all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
                catch { yield break; }
                foreach (var f in all) yield return f;
                yield break;
            }

            foreach (var p in patterns)
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, p, SearchOption.AllDirectories); }
                catch { continue; }
                using (IEnumerator<string> en = files.GetEnumerator())
                {
                    while (true)
                    {
                        string cur;
                        try
                        {
                            if (!en.MoveNext()) break;
                            cur = en.Current;
                        }
                        catch { break; }
                        yield return cur;
                    }
                }
            }
        }

        private static long DirectorySize(string dir, string[] patterns, CancellationToken token, Action<string> progress)
        {
            long total = 0;
            foreach (var f in EnumerateFilesSafe(dir, patterns))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Exists) total += fi.Length;
                }
                catch (Exception) { }
            }
            return total;
        }

        private static int CountFiles(string dir, string[] patterns, CancellationToken token)
        {
            int n = 0;
            foreach (var f in EnumerateFilesSafe(dir, patterns))
            {
                token.ThrowIfCancellationRequested();
                n++;
            }
            return n;
        }

        // ====================== P/Invoke ======================

        private const int PROCESS_QUERY_INFORMATION = 0x0400;
        private const int PROCESS_SET_QUOTA = 0x0100;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, int dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);    }
}
