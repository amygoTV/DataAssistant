using System;
using System.Collections.Generic;
using System.Threading;

namespace DataAssistant
{
    /// <summary>
    /// 后台调度器：每分钟检查一次，到点触发备份 / 删图任务，在后台线程执行（不卡 UI）。
    /// 通过事件把状态变化推给 UI（UI 侧用 Invoke 更新控件）。
    ///
    /// 触发规则：
    ///   · 备份：lastRun 为空 或 (now - lastRun) ≥ 周期天数，且当前时间 ≥ 设定时间 → 触发
    ///   · 删图：每天一次，lastRun 不是今天 且当前时间 ≥ 设定时间 → 触发（按文件夹创建时间判断年龄）
    /// </summary>
    public class SchedulerService
    {
        // ---------------- 备份配置 ----------------
        public bool BackupEnabled;
        public string BackupSource = string.Empty;
        public string BackupDest = string.Empty;
        public int BackupIntervalDays = 1;        // 1=每天 7=每周 30=每月(近似)
        public TimeSpan BackupTime = new TimeSpan(2, 0, 0);
        public int BackupKeep = 7;
        public DateTime? BackupLastRun;

        // ---------------- 删图任务 ----------------
        public class DeleteTask
        {
            public string Path = string.Empty;
            public TimeSpan Time = new TimeSpan(3, 0, 0);
            public int KeepDays = 30;
            public bool Enabled = true;
            public DateTime? LastRun;
        }
        public List<DeleteTask> DeleteTasks = new List<DeleteTask>();

        // ---------------- 事件 ----------------
        /// <summary>状态变化：key="backup" 或 "del{n}"，text 为状态文字。</summary>
        public event Action<string, string> OnStatus;
        /// <summary>配置（含 lastRun）发生变化，需要重新持久化。</summary>
        public event Action OnConfigChanged;

        private Timer _timer;
        private readonly object _lock = new object();
        private bool _busy;

        public void Start()
        {
            if (_timer == null)
                _timer = new Timer(Tick, null, 2000, 60000); // 启动2秒后首检，之后每分钟
        }

        public void Stop()
        {
            try { if (_timer != null) { _timer.Dispose(); _timer = null; } }
            catch { }
        }

        private void Tick(object state)
        {
            if (_busy) return; // 上轮仍在跑则跳过本次，避免重入
            bool bkEnabled; string bkSrc, bkDst; int bkInt; TimeSpan bkTime; int bkKeep; DateTime? bkLast;
            List<DeleteTask> dels;
            lock (_lock)
            {
                bkEnabled = BackupEnabled; bkSrc = BackupSource; bkDst = BackupDest;
                bkInt = BackupIntervalDays; bkTime = BackupTime; bkKeep = BackupKeep; bkLast = BackupLastRun;
                dels = new List<DeleteTask>(DeleteTasks);
            }
            DateTime now = DateTime.Now;

            if (bkEnabled && !string.IsNullOrWhiteSpace(bkSrc) && !string.IsNullOrWhiteSpace(bkDst)
                && ShouldRun(bkLast, bkInt, bkTime, now))
                RunBackup(bkSrc, bkDst, bkKeep);

            for (int i = 0; i < dels.Count; i++)
            {
                var t = dels[i];
                if (t.Enabled && !string.IsNullOrWhiteSpace(t.Path) && ShouldRunDaily(t.LastRun, t.Time, now))
                    RunDelete(t, i);
            }
        }

        private static bool ShouldRun(DateTime? last, int intervalDays, TimeSpan time, DateTime now)
        {
            if (now.TimeOfDay < time) return false;
            if (last == null) return true;
            return (now - last.Value).TotalDays >= intervalDays;
        }

        private static bool ShouldRunDaily(DateTime? last, TimeSpan time, DateTime now)
        {
            if (now.TimeOfDay < time) return false;
            if (last == null) return true;
            return last.Value.Date != now.Date;
        }

        private void RunBackup(string src, string dst, int keep)
        {
            lock (_lock) _busy = true;
            try
            {
                OnStatus?.Invoke("backup", "正在备份中...");
                Logger.Info("Backup", "触发自动备份");
                AutoBackupCore.Run(src, dst, keep, msg => Logger.Info("Backup", msg));
                lock (_lock) BackupLastRun = DateTime.Now;
                OnStatus?.Invoke("backup", "上次备份: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                OnConfigChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Logger.Error("Backup", "备份异常: " + ex.Message);
                OnStatus?.Invoke("backup", "备份失败(见日志)");
            }
            finally { lock (_lock) _busy = false; }
        }

        private void RunDelete(DeleteTask t, int idx)
        {
            // 各删图任务在独立后台任务执行，避免阻塞定时器
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    OnStatus?.Invoke("del" + idx, "正在删除中...");
                    Logger.Info("Delete", "触发删图任务: " + t.Path);
                    AutoDeleteCore.Run(t.Path, t.KeepDays, msg => Logger.Info("Delete", msg));
                    lock (_lock) t.LastRun = DateTime.Now;
                    OnStatus?.Invoke("del" + idx, "上次删除: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                    OnConfigChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Error("Delete", "删图异常: " + ex.Message);
                    OnStatus?.Invoke("del" + idx, "删除失败(见日志)");
                }
            });
        }

        // ---------------- 配置更新（UI 调用，带锁）----------------
        public void UpdateBackup(bool en, string src, string dst, int intervalDays, TimeSpan time, int keep)
        {
            lock (_lock)
            {
                BackupEnabled = en; BackupSource = src; BackupDest = dst;
                BackupIntervalDays = intervalDays; BackupTime = time; BackupKeep = keep;
            }
            OnConfigChanged?.Invoke();
        }

        public void UpdateDeleteTasks(List<DeleteTask> tasks)
        {
            lock (_lock) { DeleteTasks = tasks ?? new List<DeleteTask>(); }
            OnConfigChanged?.Invoke();
        }
    }
}
