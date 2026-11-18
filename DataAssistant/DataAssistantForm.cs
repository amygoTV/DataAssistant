using System;
using System.Windows.Forms;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DataAssistant
{
    /// <summary>
    /// 主窗体：TabControl 承载各功能面板。
    ///   · 数据整理 / 数据比对 / 图片查找 / 系统优化 / 定时备份 / 定时删图 / 运行日志
    ///
    /// 托盘与生命周期：
    ///   · 关闭按钮(X) = 最小化到托盘（不退出），只有托盘右键「退出」才真正结束进程
    ///   · 双击托盘图标 = 前台显示主窗体
    ///   · /tray 模式（开机启动）启动即最小化到托盘，不弹主窗体
    ///
    /// 设置持久化：启动时(Shown)恢复各面板；关闭/定时保存写回（路径类不存，定时任务的路径除外）。
    /// </summary>
    public class DataAssistantForm : Form
    {
        private readonly List<ISettingsAware> _settingsPanels = new List<ISettingsAware>();
        private bool _settingsLoaded;
        private System.Windows.Forms.Timer _autoSaveTimer;

        // 调度器 / 托盘 / 状态栏
        private readonly SchedulerService _scheduler = new SchedulerService();
        private NotifyIcon _tray;
        private ContextMenuStrip _trayMenu;
        private StatusStrip _statusBar;
        private ToolStripStatusLabel _lblStatus;
        private ToolStripStatusLabel _lblClock;
        private RichTextBox _rtbLog;
        private System.Windows.Forms.Timer _clockTimer;
        private bool _reallyQuit;
        private bool _restoring; // 从托盘恢复过程中，阻止 Resize 事件再次把窗口 Hide 掉

        /// <summary>由 Program 在 /tray 开机启动时设为 true：启动即最小化到托盘。</summary>
        public static bool StartMinimized = false;

        public DataAssistantForm()
        {
            // 标题栏显示当前版本号（取程序集版本 Major.Minor.Build，与 installer 的 AppVersion 一致）
            Text = "数据助手 v" + AppVersionText();
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Width = 960;
            Height = 700;
            MinimumSize = new System.Drawing.Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.Font = new Font("Microsoft YaHei", 9.5f);

            var agg = new DataAggregatorPanel { Dock = DockStyle.Fill };
            var cmp = new DataComparePanel { Dock = DockStyle.Fill };
            var find = new DataAssistantPanel { Dock = DockStyle.Fill };
            var opt = new WindowsOptimizerPanel { Dock = DockStyle.Fill };
            var backup = new AutoBackupPanel(_scheduler) { Dock = DockStyle.Fill };
            var del = new AutoDeletePanel(_scheduler) { Dock = DockStyle.Fill };

            var logPage = new TabPage("运行日志");
            _rtbLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9f) };
            logPage.Controls.Add(_rtbLog);

            AddPage(tabs, "数据整理", agg);
            AddPage(tabs, "数据比对", cmp);
            AddPage(tabs, "图片查找", find);
            AddPage(tabs, "系统优化", opt);
            AddPage(tabs, "定时备份", backup);
            AddPage(tabs, "定时删图", del);
            tabs.TabPages.Add(logPage);
            Controls.Add(tabs);

            _settingsPanels.Add(agg);
            _settingsPanels.Add(cmp);
            _settingsPanels.Add(find);
            _settingsPanels.Add(opt);
            _settingsPanels.Add(backup);
            _settingsPanels.Add(del);

            // 状态栏：左侧状态 + 右侧实时日期时间
            _statusBar = new StatusStrip();
            _lblStatus = new ToolStripStatusLabel("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _lblClock = new ToolStripStatusLabel("") { TextAlign = ContentAlignment.MiddleRight };
            _statusBar.Items.Add(_lblStatus);
            _statusBar.Items.Add(_lblClock);
            Controls.Add(_statusBar);

            InitTray();

            Shown += OnShown;
            FormClosing += OnFormClosing;
            // 最小化时收进托盘；恢复过程中(_restoring)跳过，避免刚显示又被隐藏
            Resize += (s, e) =>
            {
                if (!_restoring && WindowState == FormWindowState.Minimized && Visible) Hide();
            };

            _autoSaveTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            _autoSaveTimer.Tick += (s, e) => SaveSettings();
            _autoSaveTimer.Start();

            _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _clockTimer.Tick += (s, e) => { if (_lblClock != null) _lblClock.Text = DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm:ss"); };
            _clockTimer.Start();

            // 日志页
            Logger.OnLog += OnLog;
            foreach (var en in Logger.Snapshot()) AppendLog(en);

            if (StartMinimized)
            {
                // 只用 Visible 控制显隐，切勿切换 ShowInTaskbar：
                // 切换 ShowInTaskbar 会重建窗口句柄，导致任务栏出现"点不动"的孤儿图标。
                WindowState = FormWindowState.Minimized;
            }

            Logger.Info("App", "数据助手已启动" + (StartMinimized ? "(托盘模式)" : ""));
        }

        /// <summary>从程序集版本读取显示号（1.0.3.0 → "1.0.3"）。</summary>
        private static string AppVersionText()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "1.0.3" : string.Format("{0}.{1}.{2}", v.Major, v.Minor, v.Build);
            }
            catch { return "1.0.3"; }
        }

        private void AddPage(TabControl tabs, string title, Control c)
        {
            var p = new TabPage(title);
            p.Controls.Add(c);
            tabs.TabPages.Add(p);
        }

        private void InitTray()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("显示主界面", null, (s, e) => ShowFromTray());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("退出", null, (s, e) => ExitApp());
            try
            {
                _tray = new NotifyIcon
                {
                    Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
                    Visible = true,
                    Text = "数据助手",
                    ContextMenuStrip = _trayMenu
                };
                _tray.DoubleClick += (s, e) => ShowFromTray();
            }
            catch { }
        }

        private void OnShown(object sender, EventArgs e)
        {
            LoadSettingsOnce();
            _scheduler.Start();
            if (StartMinimized)
            {
                // 延迟到消息循环空闲再隐藏，避免在 Shown 中 Hide 引发的句柄/状态竞态
                BeginInvoke(new Action(() =>
                {
                    Hide();
                    StartMinimized = false; // 只隐藏一次
                }));
            }
        }

        private void ShowFromTray()
        {
            // 绝不切换 ShowInTaskbar —— 它会重建窗口句柄，造成任务栏图标"点不动"。
            // 只用 Visible + WindowState 控制显隐，二者不会重建句柄。
            _restoring = true;
            try
            {
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                if (!Visible) Show();
                // 二次确保非最小化（隐藏态下设置 WindowState 可能不生效）
                if (WindowState == FormWindowState.Minimized)
                    WindowState = FormWindowState.Normal;
                ShowWindow(Handle, SW_RESTORE); // 原生 SW_RESTORE，比托管属性更可靠
                BringToFront();
                Activate();
                ForceForegroundWindow(Handle);
            }
            finally
            {
                _restoring = false;
            }
        }

        /// <summary>
        /// 绕过 Windows 焦点窃取防护，把本窗口强制提到最前。
        /// 三道手段叠加，兼容各种完整性级别：
        ///  1) 模拟一次 ALT 按下/松开：让系统认为"用户刚操作过键盘"，解除前台锁（对高/低完整性都有效）；
        ///  2) SetWindowPos 临时置顶再取消：强制把窗口推到 Z 序顶端；
        ///  3) AttachThreadInput + SetForegroundWindow：同完整性级别时的常规手段。
        /// 之前仅用第 3 招，程序以管理员(高完整性)运行时，Explorer 是中等完整性，
        /// 跨级别的 AttachThreadInput 会被 UIPI 静默拒绝，所以置前一直失败。
        /// </summary>
        private static void ForceForegroundWindow(IntPtr hWnd)
        {
            // 若处于最小化，先还原窗口
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);

            // 1) 模拟 ALT 键，解除前台锁
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 2) 临时置顶再取消，把窗口推到 Z 序顶端
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);

            // 3) 常规手段：附加到前台线程（同完整性时有效）
            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint appThread = GetCurrentThreadId();
            if (foreThread != 0 && foreThread != appThread)
            {
                if (AttachThreadInput(foreThread, appThread, true))
                {
                    SetForegroundWindow(hWnd);
                    AttachThreadInput(foreThread, appThread, false);
                }
                else
                {
                    // 跨完整性级别附加失败时，ALT 键解除的前台锁已生效，直接置前
                    SetForegroundWindow(hWnd);
                }
            }
            else
            {
                SetForegroundWindow(hWnd);
            }
        }

        private const int SW_RESTORE = 9;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const byte VK_MENU = 0x12;          // ALT
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        // 注意：GetCurrentThreadId 由 kernel32.dll 导出（不是 user32.dll），标错 DLL 会导致运行时找不到入口点
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private void ExitApp()
        {
            _reallyQuit = true;
            Close();
        }

        /// <summary>恢复界面设置（只执行一次）。</summary>
        private void LoadSettingsOnce()
        {
            if (_settingsLoaded) return;
            _settingsLoaded = true;
            try
            {
                var store = SettingsStore.Load();
                if (store == null) return;
                foreach (var p in _settingsPanels)
                {
                    try { p.LoadSettings(store); }
                    catch (Exception) { /* 单个面板恢复失败不影响其它面板 */ }
                }
            }
            catch (Exception) { }
        }

        /// <summary>把各面板当前状态写入配置文件。</summary>
        private void SaveSettings()
        {
            try
            {
                var store = new SettingsStore();
                foreach (var p in _settingsPanels)
                {
                    try { p.SaveSettings(store); }
                    catch (Exception) { }
                }
                store.Save();
            }
            catch (Exception) { }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            // 点关闭(X) / 最小化：仅收进托盘，不退出
            if (!_reallyQuit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            try { if (_autoSaveTimer != null) _autoSaveTimer.Stop(); } catch { }
            try { if (_scheduler != null) _scheduler.Stop(); } catch { }
            SaveSettings();
        }

        private void OnLog(Logger.LogEntry en) { AppendLog(en); }

        private void AppendLog(Logger.LogEntry en)
        {
            if (_rtbLog == null) return;
            if (_rtbLog.InvokeRequired) { _rtbLog.Invoke(new Action<Logger.LogEntry>(AppendLog), en); return; }
            _rtbLog.AppendText(en.Line + "\r\n");
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();
        }

        protected override void Dispose(bool disposing)
        {
            try { if (disposing) { if (_autoSaveTimer != null) _autoSaveTimer.Dispose(); if (_clockTimer != null) _clockTimer.Dispose(); if (_tray != null) _tray.Dispose(); } } catch { }
            base.Dispose(disposing);
        }
    }
}
