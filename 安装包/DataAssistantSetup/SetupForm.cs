using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DataAssistantSetup
{
    /// <summary>
    /// 安装向导界面：欢迎/目录选择 → 安装进度 → 完成。
    /// 支持静默安装：DataAssistantSetup.exe /S /dir="D:\xxx"
    /// 支持卸载：DataAssistantSetup.exe /uninstall
    /// </summary>
    public class SetupForm : Form
    {
        // ---- 页1：欢迎与设置 ----
        private readonly Panel _page1 = new Panel();
        private readonly TextBox _txtDir = new TextBox();
        private readonly CheckBox _chkDesktop = new CheckBox();
        private readonly CheckBox _chkLaunch = new CheckBox();
        private readonly Button _btnBrowse = new Button();

        // ---- 页2：安装进度 ----
        private readonly Panel _page2 = new Panel();
        private readonly ProgressBar _pb = new ProgressBar();
        private readonly Label _lblStep = new Label();
        private readonly RichTextBox _rtbLog = new RichTextBox();

        // ---- 页3：完成 ----
        private readonly Panel _page3 = new Panel();

        private readonly Button _btnNext = new Button();
        private readonly Button _btnCancel = new Button();

        private bool _installing;

        public SetupForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "安装 " + InstallerCore.AppName + " v" + InstallerCore.AppVersion;
            ClientSize = new Size(520, 400);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei", 9.5f);
            Icon = LoadIcon();

            _page1.Dock = DockStyle.Fill;
            _page2.Dock = DockStyle.Fill;
            _page3.Dock = DockStyle.Fill;
            _page2.Visible = false;
            _page3.Visible = false;

            // ================= 页1 =================
            var t1 = new Label
            {
                Text = "即将安装 " + InstallerCore.AppName + " " + InstallerCore.AppVersion + "\r\n\r\n" +
                       "本程序包含：\r\n" +
                       "  · 数据整理   —— 汇总表格、删除规则清洗、导出\r\n" +
                       "  · 数据比对   —— 两表按 SN 对齐的多列比对与导出\r\n" +
                       "  · 图片查找\r\n" +
                       "  · 系统优化   —— 含『网络与取相』调优页（巨帧 9014 等）\r\n\r\n" +
                       "安装过程中可随时点击『取消』退出。",
                Location = new Point(18, 16),
                Size = new Size(486, 210)
            };
            _page1.Controls.Add(t1);

            var lDir = new Label { Text = "安装到：", Location = new Point(18, 232), AutoSize = true };
            _page1.Controls.Add(lDir);
            _txtDir.SetBounds(78, 229, 320, 23);
            _txtDir.Text = DefaultInstallDir();
            _page1.Controls.Add(_txtDir);
            _btnBrowse.SetBounds(404, 229, 66, 23); _btnBrowse.Text = "浏览…";
            _btnBrowse.Click += (s, e) => BrowseDir();
            _page1.Controls.Add(_btnBrowse);

            _chkDesktop.SetBounds(78, 260, 200, 23); _chkDesktop.Text = "创建桌面快捷方式";
            _chkDesktop.Checked = true;
            _page1.Controls.Add(_chkDesktop);

            var lTip = new Label
            {
                Text = "提示：网卡调优、电源方案等功能需要以管理员身份运行本程序。",
                Location = new Point(18, 296),
                Size = new Size(486, 20),
                ForeColor = Color.DimGray
            };
            _page1.Controls.Add(lTip);

            // ================= 页2 =================
            var l2 = new Label { Text = "正在安装，请稍候…", Location = new Point(18, 16), AutoSize = true };
            _page2.Controls.Add(l2);
            _pb.SetBounds(18, 44, 486, 18);
            _page2.Controls.Add(_pb);
            _lblStep.SetBounds(18, 68, 486, 20);
            _lblStep.Text = "准备中…";
            _page2.Controls.Add(_lblStep);
            _rtbLog.SetBounds(18, 92, 486, 230);
            _rtbLog.ReadOnly = true;
            _page2.Controls.Add(_rtbLog);

            // ================= 页3 =================
            var l3 = new Label
            {
                Text = "安装完成！\r\n\r\n" +
                       "程序位置：" + "（见下方）\r\n\r\n" +
                       "可在『开始菜单 → " + InstallerCore.AppName + "』或桌面快捷方式启动。\r\n" +
                       "也可以到『控制面板 → 程序和功能』里卸载。",
                Location = new Point(18, 16),
                Size = new Size(486, 130)
            };
            _page3.Controls.Add(l3);
            _chkLaunch.SetBounds(18, 152, 260, 23); _chkLaunch.Text = "立即启动 " + InstallerCore.AppName;
            _chkLaunch.Checked = true;
            _page3.Controls.Add(_chkLaunch);

            // ================= 底部按钮 =================
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46 };
            _btnNext.SetBounds(310, 10, 100, 28); _btnNext.Text = "下一步";
            _btnNext.Click += (s, e) => OnNext();
            bottom.Controls.Add(_btnNext);
            _btnCancel.SetBounds(416, 10, 86, 28); _btnCancel.Text = "取消";
            _btnCancel.Click += (s, e) =>
            {
                if (_installing) { _cts?.Cancel(); return; }
                Close();
            };
            bottom.Controls.Add(_btnCancel);
            Controls.Add(bottom);
            Controls.Add(_page1);
            Controls.Add(_page2);
            Controls.Add(_page3);
            _page1.BringToFront();
        }

        private static string DefaultInstallDir()
        {
            return InstallerCore.IsAdministrator()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallerCore.AppId)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InstallerCore.AppId);
        }

        private void BrowseDir()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择安装目录", SelectedPath = _txtDir.Text })
            {
                if (dlg.ShowDialog() == DialogResult.OK) _txtDir.Text = dlg.SelectedPath;
            }
        }

        private CancellationTokenSource _cts;

        private void OnNext()
        {
            if (_page1.Visible)
            {
                string dir = _txtDir.Text.Trim();
                if (string.IsNullOrEmpty(dir)) { MessageBox.Show("请填写安装目录。", "提示"); return; }
                _page1.Visible = false;
                _page2.Visible = true;
                _page2.BringToFront();
                _btnNext.Enabled = false;
                _btnCancel.Text = "取消";
                _installing = true;
                _cts = new CancellationTokenSource();
                ThreadPool.QueueUserWorkItem(_ => DoInstall(dir, _cts.Token));
                return;
            }
            if (_page3.Visible)
            {
                if (_chkLaunch.Checked)
                {
                    try
                    {
                        Process.Start(Path.Combine(_txtDir.Text.Trim(), InstallerCore.ExeName));
                    }
                    catch (Exception) { }
                }
                Close();
            }
        }

        private void DoInstall(string dir, CancellationToken token)
        {
            try
            {
                Ui(() => _lblStep.Text = "正在解压文件…");
                bool ok = InstallerCore.ExtractPayload(dir, s => Ui(() => _rtbLog.AppendText(s + Environment.NewLine)),
                    (pct, name) => Ui(() => { _pb.Value = Math.Min(pct, 100); _lblStep.Text = "正在写入 " + name; }),
                    token);
                if (!ok)
                {
                    Ui(() => { _lblStep.Text = "安装已取消或失败。"; _pb.Value = 0; });
                    return;
                }

                Ui(() => _lblStep.Text = "正在创建快捷方式…");
                string exe = Path.Combine(dir, InstallerCore.ExeName);
                string self = Process.GetCurrentProcess().MainModule.FileName;
                string uninstaller = Path.Combine(dir, "Uninstall.exe");
                try { File.Copy(self, uninstaller, true); } catch (Exception) { }

                InstallerCore.CreateShortcut(
                    Path.Combine(InstallerCore.StartMenuDir(InstallerCore.AppName), InstallerCore.AppName + ".lnk"),
                    exe, dir, InstallerCore.AppName);
                if (_chkDesktop.Checked)
                    InstallerCore.CreateShortcut(InstallerCore.DesktopPath(InstallerCore.AppName + ".lnk"), exe, dir, InstallerCore.AppName);

                Ui(() => _lblStep.Text = "正在注册卸载项…");
                InstallerCore.WriteUninstallRegistry(dir, uninstaller);
                InstallerCore.CleanupLegacyInstall(s => Ui(() => _rtbLog.AppendText(s + Environment.NewLine)));

                Ui(() =>
                {
                    _pb.Value = 100;
                    _lblStep.Text = "安装完成。";
                    _page2.Visible = false;
                    _page3.Visible = true;
                    _page3.BringToFront();
                    _btnNext.Text = "完成";
                    _btnNext.Enabled = true;
                    _installing = false;
                    _btnCancel.Text = "关闭";
                });
            }
            catch (Exception ex)
            {
                Ui(() =>
                {
                    _lblStep.Text = "安装出错：" + ex.Message;
                    MessageBox.Show("安装出错：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
            }
        }

        private void Ui(Action a)
        {
            if (InvokeRequired) { BeginInvoke(a); return; }
            a();
        }

        private static Icon LoadIcon()
        {
            try
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, InstallerCore.ExeName);
                if (File.Exists(exe)) return Icon.ExtractAssociatedIcon(exe);
            }
            catch (Exception) { }
            return null;
        }
    }
}
