using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DataAssistant
{
    /// <summary>
    /// 开机启动管理。
    /// 优先用 Windows 任务计划程序（ONLOGON + 标准权限 LIMITED），登录时静默启动、
    /// 不弹 UAC；若创建失败则回退到 HKCU\Run 键。
    /// 注册的命令为「DataAssistant.exe /tray」（最小化到托盘、不弹主窗体、静默运行）。
    /// 注意：务必用标准权限（与 Explorer 同完整性级别）。若用 HIGHEST，程序以管理员/高完整性
    /// 运行，Explorer 是中等完整性，UIPI 会阻止跨级别的窗口激活与 AttachThreadInput，
    /// 导致双击托盘/点击任务栏都无法把窗口置前。需要管理员功能时由用户手动双击主程序提权即可。
    /// </summary>
    public static class AutoStartManager
    {
        private const string TaskName = "DataAssistantTray";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "DataAssistant";

        public static bool IsEnabled()
        {
            if (TaskExists()) return true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key != null)
                    {
                        var v = key.GetValue(RunValue) as string;
                        if (!string.IsNullOrEmpty(v) &&
                            v.IndexOf("DataAssistant.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static void SetEnabled(bool enable)
        {
            if (enable) Enable();
            else Disable();
        }

        /// <summary>
        /// 自愈：把旧版本用 /RL HIGHEST 注册的任务降为标准权限，修复托盘窗口无法置前。
        /// 每次启动静默调用；已是 LIMITED 或不存在时不做任何改动。
        /// </summary>
        public static void EnsureLowIntegrityTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\" /XML")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                string xml;
                using (var p = Process.Start(psi))
                {
                    xml = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(8000);
                }
                if (string.IsNullOrWhiteSpace(xml)) return;
                // HighestAvailable / Highest 都表示高完整性；LIMITED 无需改动
                if (xml.IndexOf("HighestAvailable", StringComparison.OrdinalIgnoreCase) < 0 &&
                    xml.IndexOf("<RunLevel>Highest", StringComparison.OrdinalIgnoreCase) < 0)
                    return;

                string exe = AssemblyLocation();
                if (string.IsNullOrEmpty(exe)) return;
                TryCreateTask(exe); // /F 覆盖重建为标准权限
                Logger.Info("AutoStart", "已把开机启动任务调整为标准权限，修复托盘窗口无法置前的问题");
            }
            catch { }
        }

        private static void Enable()
        {
            string exe = AssemblyLocation();
            if (string.IsNullOrEmpty(exe)) return;
            if (TryCreateTask(exe)) { Logger.Info("AutoStart", "已通过任务计划程序设置开机启动"); return; }
            // 回退 HKCU Run
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key != null) key.SetValue(RunValue, "\"" + exe + "\" /tray", RegistryValueKind.String);
                }
                Logger.Info("AutoStart", "已通过注册表 Run 键设置开机启动");
            }
            catch (Exception ex) { Logger.Warn("AutoStart", "写入 Run 键失败: " + ex.Message); }
        }

        private static void Disable()
        {
            TryDeleteTask();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null && key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
                }
            }
            catch { }
            Logger.Info("AutoStart", "已取消开机启动");
        }

        private static bool TryCreateTask(string exe)
        {
            try
            {
                // /TR 的值整体用双引号包住，内部路径再转义一层引号
                string tr = string.Format("\"\\\"{0}\\\" /tray\"", exe);
                // /RL LIMITED：与 Explorer 同完整性级别，保证托盘/任务栏能把窗口置前
                string arg = string.Format("/Create /TN \"{0}\" /TR {1} /SC ONLOGON /RL LIMITED /F", TaskName, tr);
                var psi = new ProcessStartInfo("schtasks.exe", arg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(15000);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("AutoStart", "创建计划任务失败: " + ex.Message);
                return false;
            }
        }

        private static bool TaskExists()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + TaskName + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static void TryDeleteTask()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi)) p.WaitForExit(10000);
            }
            catch { }
        }

        private static string AssemblyLocation()
        {
            try { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
            catch { return string.Empty; }
        }
    }
}
