using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace DataAssistant
{
    internal static class Program
    {
        private const string MutexName = "DataAssistant_SingleInstance_Mutex";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 升级兼容：把旧目录（改名前）的配置与优化备份迁移到新目录
            OptimizerConfig.MigrateLegacyConfig();

            // 自愈：把旧版 /RL HIGHEST 的开机任务降为标准权限，修复托盘窗口无法置前。
            // 必须在提权判断之前调用，否则标准权限下无权限改任务。放在单实例保护之前亦可静默执行。
            try { AutoStartManager.EnsureLowIntegrityTask(); } catch { }

            // 单实例保护
            bool created;
            var mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                mutex.Dispose();
                MessageBox.Show("数据助手已在运行。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 静默托盘模式（开机启动）：不强制提权，直接最小化到托盘后台运行
            bool trayMode = false;
            if (args != null)
                foreach (var a in args)
                    if (string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase)) { trayMode = true; break; }
            if (trayMode)
            {
                DataAssistantForm.StartMinimized = true;
                try { Application.Run(new DataAssistantForm()); }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch (Exception) { }
                    mutex.Dispose();
                }
                return;
            }

            // 权限：系统优化需要管理员权限才能清理系统目录、修改 HKLM 启动项
            if (!IsAdministrator())
            {
                // 先释放锁，否则提权后的新进程会误判为"已在运行"
                mutex.ReleaseMutex();
                mutex.Dispose();

                if (RelaunchElevated()) return;   // 提权成功，本进程退出

                // 用户取消了 UAC，降级以普通权限继续运行
                mutex = new Mutex(true, MutexName, out created);
                if (!created)
                {
                    mutex.Dispose();
                    MessageBox.Show("数据助手已在运行。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                MessageBox.Show(
                    "未能获取管理员权限。\n系统目录清理、HKLM 启动项修改等操作将被跳过，其余功能可正常使用。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try
            {
                Application.Run(new DataAssistantForm());
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch (Exception) { }
                mutex.Dispose();
            }
        }

        /// <summary>当前进程是否以管理员身份运行。</summary>
        internal static bool IsAdministrator()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(identity)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception) { return false; }
        }

        /// <summary>以管理员身份重新启动自身，成功返回 true。</summary>
        private static bool RelaunchElevated()
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(exe)) return false;

                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception) { return false; }   // 用户取消 UAC
        }
    }
}
