using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DataAssistant
{
    /// <summary>
    /// 全局日志：写文件(按天滚动) + 内存环形缓冲 + 事件通知 UI。
    /// 所有后台操作（备份 / 删图 / 开机启动 / 调度）都通过它留痕，
    /// 满足需求里「所有操作要有 Log 可查」。
    /// </summary>
    public static class Logger
    {
        public enum Level { Info, Warn, Error }

        private static readonly object _lock = new object();
        private static readonly List<LogEntry> _buffer = new List<LogEntry>();
        private const int MaxBuffer = 3000;

        /// <summary>UI 订阅此事件即可实时显示日志（跨线程请自行 Invoke）。</summary>
        public static event Action<LogEntry> OnLog;

        public class LogEntry
        {
            public DateTime Time;
            public Level Level;
            public string Source;
            public string Message;
            public string Line
            {
                get { return string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [{1}] [{2}] {3}",
                    Time, Level.ToString().ToUpper(), Source, Message); }
            }
        }

        /// <summary>
        /// 日志目录：优先 exe 启动目录下的 Log\；该目录不可写时退回 %APPDATA%\DataAssistant\logs。
        /// （首次确定后缓存，避免每次写日志都试探磁盘。）
        /// </summary>
        private static string _logDir;
        public static string LogDir
        {
            get
            {
                var cached = _logDir;
                if (cached != null) return cached;

                // 1) 启动目录\Log
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string dir = Path.Combine(baseDir, "Log");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write_test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _logDir = dir;
                    return dir;
                }
                catch { /* 不可写 → 回退 */ }

                // 2) 回退：%APPDATA%\DataAssistant\logs（与配置同目录）
                try
                {
                    string dir = Path.Combine(SettingsStore.ConfigDir, "logs");
                    Directory.CreateDirectory(dir);
                    _logDir = dir;
                    return dir;
                }
                catch
                {
                    return Path.Combine(SettingsStore.ConfigDir, "logs");
                }
            }
        }

        public static void Log(Level level, string source, string message)
        {
            var e = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Source = source ?? string.Empty,
                Message = message ?? string.Empty
            };
            lock (_lock)
            {
                _buffer.Add(e);
                if (_buffer.Count > MaxBuffer) _buffer.RemoveAt(0);
            }
            try
            {
                string file = Path.Combine(LogDir, "log_" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                File.AppendAllText(file, e.Line + "\r\n", Encoding.UTF8);
            }
            catch { }
            try { OnLog?.Invoke(e); }
            catch { }
        }

        public static void Info(string src, string msg) { Log(Level.Info, src, msg); }
        public static void Warn(string src, string msg) { Log(Level.Warn, src, msg); }
        public static void Error(string src, string msg) { Log(Level.Error, src, msg); }

        /// <summary>返回当前内存中的日志快照（供日志页初始化填充）。</summary>
        public static IList<LogEntry> Snapshot()
        {
            lock (_lock) return new List<LogEntry>(_buffer);
        }
    }
}
