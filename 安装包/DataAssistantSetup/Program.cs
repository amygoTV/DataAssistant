using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace DataAssistantSetup
{
    /// <summary>
    /// 安装程序入口。
    ///   直接运行        → 图形安装向导
    ///   /S 或 /silent   → 静默安装（可加 /dir="路径" /noshortcut）
    ///   /uninstall      → 卸载（可加 /silent /dir="路径"）
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool silent = false, uninstall = false, desktop = true;
            string dir = null;

            foreach (string a in args)
            {
                string s = a.Trim();
                if (s.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (s.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)) uninstall = true;
                else if (s.Equals("/noshortcut", StringComparison.OrdinalIgnoreCase)) desktop = false;
                else if (s.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                    dir = s.Substring(5).Trim('"');
            }

            // 本体被复制为 Uninstall.exe 时（安装程序 File.Copy 生成），双击不带参数应直接进入卸载，
            // 而不是弹出安装向导。
            bool namedUninstall = Path.GetFileNameWithoutExtension(
                System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)
                .Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

            try
            {
                if (uninstall)
                {
                    string target = dir ?? InstallerCore.ReadInstallDir();
                    InstallerCore.RunUninstall(target, silent, m => { if (!silent) Console.WriteLine(m); });
                    return 0;
                }
                if (namedUninstall)
                {
                    // 双击 Uninstall.exe：走带确认与结果提示的 GUI 卸载
                    RunUninstallGui(dir);
                    return 0;
                }
                if (silent)
                {
                    return InstallerCore.SilentInstall(dir, desktop, m => Console.WriteLine(m));
                }
            }
            catch (Exception ex)
            {
                if (silent) { Console.WriteLine("失败：" + ex.Message); return 1; }
                MessageBox.Show("安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        /// <summary>
        /// 双击 Uninstall.exe 时的 GUI 卸载：先确认，再执行，最后用消息框反馈结果。
        /// </summary>
        private static void RunUninstallGui(string dir)
        {
            string target = dir ?? InstallerCore.ReadInstallDir();
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show("未找到 " + InstallerCore.AppName + " 的安装记录，无需卸载。",
                    "卸载 " + InstallerCore.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "确定要卸载 " + InstallerCore.AppName + " 吗？\n\n将删除安装目录及所有快捷方式：\n" + target,
                "卸载 " + InstallerCore.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            var sb = new StringBuilder();
            InstallerCore.RunUninstall(target, false, m => { if (!string.IsNullOrEmpty(m)) sb.AppendLine(m); });
            string msg = sb.Length > 0 ? sb.ToString().Trim() : "卸载完成。";
            MessageBox.Show(msg, "卸载 " + InstallerCore.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
