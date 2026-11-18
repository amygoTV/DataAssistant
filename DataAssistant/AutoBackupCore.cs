using System;
using System.IO;

namespace DataAssistant
{
    /// <summary>
    /// 定时自动备份核心：把源文件夹完整复制到「备份路径\YYYYMMDD」下，
    /// 完成后清理超过保留个数的最旧备份（按文件夹名日期降序保留前 N 个）。
    /// 所有耗时操作由调用方在后台线程执行，本类不碰 UI。
    /// </summary>
    public static class AutoBackupCore
    {
        /// <summary>
        /// 执行一次备份。
        /// </summary>
        /// <param name="source">要备份的源文件夹（整个目录）</param>
        /// <param name="backupRoot">备份根路径，内部按 YYYYMMDD 建子目录</param>
        /// <param name="keepCount">保留的备份个数（按日期降序）；≤0 表示不清理</param>
        /// <param name="log">进度/结果回调</param>
        public static void Run(string source, string backupRoot, int keepCount, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                log("备份源路径不存在或为空: " + (source ?? string.Empty));
                return;
            }
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                log("备份路径为空，已跳过");
                return;
            }

            Directory.CreateDirectory(backupRoot);
            string dateFolder = DateTime.Now.ToString("yyyyMMdd");
            string dest = Path.Combine(backupRoot, dateFolder);

            log("开始备份: " + source + "  ->  " + dest);
            // 当日若已存在则重建（保证当天这份是最新的）
            if (Directory.Exists(dest))
            {
                log("当日备份已存在，先清空重建: " + dest);
                try { Directory.Delete(dest, true); } catch (Exception ex) { log("清空失败: " + ex.Message); }
            }
            Directory.CreateDirectory(dest);

            int files = CopyDirectory(source, dest, log);
            log(string.Format("复制完成，共 {0} 个文件", files));

            // 清理超龄：仅处理 YYYYMMDD 格式文件夹，按名称降序（新在前）保留前 keepCount 个
            if (keepCount > 0)
            {
                var valid = new System.Collections.Generic.List<DirectoryInfo>();
                foreach (var d in Directory.GetDirectories(backupRoot))
                {
                    var di = new DirectoryInfo(d);
                    if (di.Name.Length == 8 && IsAllDigits(di.Name)) valid.Add(di);
                }
                valid.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name)); // 降序
                int removed = 0;
                for (int i = keepCount; i < valid.Count; i++)
                {
                    try { Directory.Delete(valid[i].FullName, true); removed++; }
                    catch (Exception ex) { log("删除旧备份失败 " + valid[i].FullName + ": " + ex.Message); }
                }
                int kept = Math.Min(keepCount, valid.Count);
                log(string.Format("清理完成：保留 {0} 个，删除 {1} 个旧备份", kept, removed));
            }
            else
            {
                log("未设置保留个数，保留全部历史备份");
            }
        }

        private static bool IsAllDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        private static int CopyDirectory(string src, string dst, Action<string> log)
        {
            int count = 0;
            try { Directory.CreateDirectory(dst); }
            catch (Exception ex) { log("创建目标目录失败 " + dst + ": " + ex.Message); return 0; }
            try
            {
                foreach (var f in Directory.GetFiles(src))
                {
                    try
                    {
                        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
                        count++;
                    }
                    catch (Exception ex) { log("复制失败 " + f + ": " + ex.Message); }
                }
                foreach (var d in Directory.GetDirectories(src))
                    count += CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)), log);
            }
            catch (Exception ex) { log("读取目录失败 " + src + ": " + ex.Message); }
            return count;
        }
    }
}
