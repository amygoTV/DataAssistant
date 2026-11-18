using System;
using System.IO;

namespace DataAssistant
{
    /// <summary>
    /// 定时自动删图核心：删除指定路径下「创建时间」超过保留天数的子文件夹（整个文件夹）。
    /// 所有耗时操作由调用方在后台线程执行，本类不碰 UI。
    /// </summary>
    public static class AutoDeleteCore
    {
        /// <summary>
        /// 执行一次删图任务。
        /// </summary>
        /// <param name="path">待清理的根路径（其下直接子文件夹会被判断年龄）</param>
        /// <param name="keepDays">保留天数；创建时间距今超过该天数的文件夹被删除</param>
        /// <param name="log">进度/结果回调</param>
        /// <returns>删除的文件夹个数</returns>
        public static int Run(string path, int keepDays, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                log("删图路径不存在或为空: " + (path ?? string.Empty));
                return 0;
            }
            if (keepDays < 0) keepDays = 0;
            var cutoff = DateTime.Now.AddDays(-keepDays);
            int removed = 0;
            foreach (var d in Directory.GetDirectories(path))
            {
                try
                {
                    var di = new DirectoryInfo(d);
                    if (di.CreationTime < cutoff)
                    {
                        Directory.Delete(d, true);
                        removed++;
                        log("已删除超龄文件夹: " + d + " (创建于 " + di.CreationTime.ToString("yyyy-MM-dd") + ")");
                    }
                }
                catch (Exception ex) { log("删除失败 " + d + ": " + ex.Message); }
            }
            log(string.Format("删图完成：删除 {0} 个超过 {1} 天的文件夹", removed, keepDays));
            return removed;
        }
    }
}
