using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataAssistant
{
    /// <summary>
    /// Windows 系统优化控件（可复用 UserControl）。
    /// 页签：垃圾清理 / 磁盘分析 / 启动项管理 / 性能优化 / 系统优化项 / 重复文件 / 路径配置。
    /// 直接拖到主窗体（Dock = Fill）即可，也可单独嵌入。
    /// 全部采用 Dock 响应式布局，宿主窗体尺寸变化时不会裁掉控件。
    /// </summary>
    public partial class WindowsOptimizerPanel : UserControl, ISettingsAware
    {
        // ---- 公共区 ----
        private readonly TabControl _tabs = new TabControl();
        private readonly ProgressBar _pb = new ProgressBar();
        private readonly Label _lblStatus = new Label();
        private readonly RichTextBox _rtbLog = new RichTextBox();

        // ---- 垃圾清理 ----
        private readonly CheckedListBox _clbClean = new CheckedListBox();
        private Button _btnScanClean = new Button();
        private Button _btnClean = new Button();
        private Button _btnSelAll = new Button();
        private Button _btnSelNone = new Button();
        private readonly List<WindowsOptimizerCore.CleanTarget> _targets = new List<WindowsOptimizerCore.CleanTarget>();
        private readonly Label _lblPriv = new Label();

        // ---- 磁盘分析 ----
        private readonly ListView _lvDrives = new ListView();
        private readonly ListView _lvDirs = new ListView();
        private readonly TextBox _txtRoot = new TextBox();
        private Button _btnPickRoot = new Button();
        private Button _btnScanDirs = new Button();
        private Button _btnRefreshDrives = new Button();

        // ---- 启动项 ----
        private readonly ListView _lvStartup = new ListView();
        private Button _btnRefreshStartup = new Button();
        private Button _btnDisable = new Button();
        private Button _btnEnable = new Button();
        private readonly List<WindowsOptimizerCore.StartupItem> _startupItems = new List<WindowsOptimizerCore.StartupItem>();

        // ---- 性能优化 ----
        private readonly Label _lblMemory = new Label();
        private Button _btnFreeMem = new Button();
        private Button _btnFlushDns = new Button();
        private Button _btnClearArp = new Button();
        private Button _btnRefreshExplorer = new Button();
        private Button _btnAnimOff = new Button();
        private Button _btnAnimOn = new Button();

        private CancellationTokenSource _cts;
        private bool _busy;

        public WindowsOptimizerPanel()
        {
            InitializeComponent();
            FillOptList();
            RefreshDrives();
            RefreshMemory();
            RefreshPrivilege();
        }

        /// <summary>刷新权限提示标签。</summary>
        private void RefreshPrivilege()
        {
            try
            {
                bool admin = IsAdministrator();
                _lblPriv.Text = admin
                    ? "当前权限：管理员（可清理系统目录、修改 HKLM 启动项）"
                    : "当前权限：普通用户（系统目录与 HKLM 启动项会被跳过）";
                _lblPriv.ForeColor = admin ? Color.DarkGreen : Color.DarkOrange;
            }
            catch (Exception)
            {
                _lblPriv.Text = "当前权限：未知";
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception) { return false; }
        }

        // ====================== 布局 ======================

        private void InitializeComponent()
        {
            Size = new Size(880, 660);
            Font = new Font("Microsoft YaHei", 9.5f);

            // ---- TabControl：填充剩余空间 ----
            // 注意：不要同时设置 Anchor —— Anchor 设为四边锚定会把 Dock 重置为 None。
            _tabs.Dock = DockStyle.Fill;
            Controls.Add(_tabs);

            _tabs.TabPages.Add(BuildCleanPage());
            _tabs.TabPages.Add(BuildDiskPage());
            _tabs.TabPages.Add(BuildStartupPage());
            _tabs.TabPages.Add(BuildPerfPage());
            _tabs.TabPages.Add(BuildOptPage());
            _tabs.TabPages.Add(BuildDupPage());
            _tabs.TabPages.Add(BuildCfgPage());
            _tabs.TabPages.Add(BuildNetPage());

            // ---- 底部公共区：Dock 顺序自下而上 = 日志 / 状态 / 进度条 ----
            // 注意：WinForms 按 z 序从后往前停靠，后加入的先停靠，因此加入顺序为
            // _pb -> _lblStatus -> _rtbLog，实际显示顺序为 日志(最底) / 状态 / 进度条。
            _pb.Dock = DockStyle.Bottom;
            _pb.Height = 18;
            _pb.Style = ProgressBarStyle.Continuous;
            Controls.Add(_pb);

            _lblStatus.Dock = DockStyle.Bottom;
            _lblStatus.Height = 18;
            _lblStatus.Text = "就绪";
            Controls.Add(_lblStatus);

            _rtbLog.Dock = DockStyle.Bottom;
            _rtbLog.Height = 150;
            _rtbLog.ReadOnly = true;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            _rtbLog.BackColor = Color.White;
            Controls.Add(_rtbLog);
        }

        /// <summary>创建一个停靠在顶部的容器面板。</summary>
        private static Panel MakeTopPanel(int height)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = height };
            return p;
        }

        /// <summary>
        /// 为页签创建一个带内边距的宿主面板。
        /// 直接把 Fill 控件挂在 TabPage 上时，TabPage 的 Padding 会让控件底部溢出 4px；
        /// 套一层带 Padding 的 Panel 后，Fill 控件正好落在内边距之内。
        /// </summary>
        private static Panel MakePageHost(TabPage page)
        {
            page.Padding = new Padding(0);
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            page.Controls.Add(host);
            return host;
        }

        /// <summary>创建一个停靠在底部的容器面板（放按钮行）。</summary>
        private static Panel MakeBottomPanel(int height)
        {
            var p = new Panel { Dock = DockStyle.Bottom, Height = height };
            return p;
        }

        /// <summary>生成一个标准样式的按钮。</summary>
        private static Button MakeButton(string text, int x, int y, int w, int h)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h) };
            return b;
        }

        // ---------- 页签 1：垃圾清理 ----------
        private TabPage BuildCleanPage()
        {
            var p = new TabPage("垃圾清理");
            var host = MakePageHost(p);

            var top = MakeTopPanel(40);
            var tip = new Label
            {
                Text = "先「扫描」统计各项占用空间，确认后再「清理所选」。删除的文件不进回收站，不可恢复。",
                Location = new Point(0, 2),
                AutoSize = true
            };
            top.Controls.Add(tip);

            _lblPriv.Location = new Point(0, 20);
            _lblPriv.AutoSize = true;
            _lblPriv.ForeColor = Color.DimGray;
            _lblPriv.Text = "当前权限：检测中...";
            top.Controls.Add(_lblPriv);

            var bottom = MakeBottomPanel(36);
            _btnScanClean = MakeButton("扫描", 0, 4, 100, 28);
            _btnScanClean.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoScanClean()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnClean = MakeButton("清理所选", 108, 4, 110, 28);
            _btnClean.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoClean()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnSelAll = MakeButton("全选", 226, 4, 80, 28);
            _btnSelAll.Click += (s, e) => { try { SetAllChecked(true); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnSelNone = MakeButton("全不选", 314, 4, 90, 28);
            _btnSelNone.Click += (s, e) => { try { SetAllChecked(false); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            bottom.Controls.AddRange(new Control[] { _btnScanClean, _btnClean, _btnSelAll, _btnSelNone });

            _clbClean.Dock = DockStyle.Fill;
            _clbClean.CheckOnClick = true;
            _clbClean.IntegralHeight = false;

            host.Controls.Add(_clbClean);
            host.Controls.Add(bottom);
            host.Controls.Add(top);
            return p;
        }

        // ---------- 页签 2：磁盘分析 ----------
        private TabPage BuildDiskPage()
        {
            var p = new TabPage("磁盘分析");
            var host = MakePageHost(p);

            var top = MakeTopPanel(212);
            var l1 = new Label { Text = "磁盘空间:", Location = new Point(0, 2), AutoSize = true };
            top.Controls.Add(l1);

            _lvDrives.Location = new Point(0, 20);
            _lvDrives.Size = new Size(840, 100);
            _lvDrives.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lvDrives.View = View.Details;
            _lvDrives.FullRowSelect = true;
            _lvDrives.GridLines = true;
            _lvDrives.Columns.Add("磁盘", 90);
            _lvDrives.Columns.Add("文件系统", 90);
            _lvDrives.Columns.Add("总容量", 110);
            _lvDrives.Columns.Add("已用", 110);
            _lvDrives.Columns.Add("可用", 110);
            _lvDrives.Columns.Add("剩余%", 70);
            top.Controls.Add(_lvDrives);

            _btnRefreshDrives = MakeButton("刷新磁盘信息", 0, 126, 120, 28);
            _btnRefreshDrives.Click += (s, e) => { try { RefreshDrives(); Log("磁盘信息已刷新。"); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            top.Controls.Add(_btnRefreshDrives);

            var l2 = new Label { Text = "目录体积 TOP（扫描指定根目录下的一级子目录）:", Location = new Point(0, 160), AutoSize = true };
            top.Controls.Add(l2);

            _txtRoot.Location = new Point(0, 182);
            _txtRoot.Size = new Size(600, 23);
            _txtRoot.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtRoot.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            top.Controls.Add(_txtRoot);

            _btnPickRoot = MakeButton("选择目录", 608, 182, 90, 23);
            _btnPickRoot.Click += (s, e) =>
            {
                try
                {
                    using (var dlg = new FolderBrowserDialog { Description = "选择要分析的根目录" })
                    {
                        if (Directory.Exists(_txtRoot.Text)) dlg.SelectedPath = _txtRoot.Text;
                        if (dlg.ShowDialog() == DialogResult.OK) _txtRoot.Text = dlg.SelectedPath;
                    }
                }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            top.Controls.Add(_btnPickRoot);

            _btnScanDirs = MakeButton("扫描目录体积", 706, 182, 134, 23);
            _btnScanDirs.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoScanDirs()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            top.Controls.Add(_btnScanDirs);

            _lvDirs.Dock = DockStyle.Fill;
            _lvDirs.View = View.Details;
            _lvDirs.FullRowSelect = true;
            _lvDirs.GridLines = true;
            _lvDirs.Columns.Add("目录", 620);
            _lvDirs.Columns.Add("大小", 120);
            _lvDirs.Columns.Add("占比", 80);

            host.Controls.Add(_lvDirs);
            host.Controls.Add(top);
            return p;
        }

        // ---------- 页签 3：启动项管理 ----------
        private TabPage BuildStartupPage()
        {
            var p = new TabPage("启动项管理");
            var host = MakePageHost(p);

            var top = MakeTopPanel(26);
            var tip = new Label
            {
                Text = "禁用后该启动项会被移到备份位置（注册表项）或加 .disabled 后缀（启动文件夹），可随时还原。",
                Location = new Point(0, 4),
                AutoSize = true
            };
            top.Controls.Add(tip);

            var bottom = MakeBottomPanel(36);
            _btnRefreshStartup = MakeButton("刷新", 0, 4, 100, 28);
            _btnRefreshStartup.Click += (s, e) => { try { RefreshStartup(); Log("启动项已刷新。"); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnDisable = MakeButton("禁用所选", 108, 4, 110, 28);
            _btnDisable.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoSetStartup(false)); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnEnable = MakeButton("启用所选", 226, 4, 110, 28);
            _btnEnable.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoSetStartup(true)); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            bottom.Controls.AddRange(new Control[] { _btnRefreshStartup, _btnDisable, _btnEnable });

            _lvStartup.Dock = DockStyle.Fill;
            _lvStartup.View = View.Details;
            _lvStartup.FullRowSelect = true;
            _lvStartup.GridLines = true;
            _lvStartup.MultiSelect = true;
            _lvStartup.Columns.Add("名称", 180);
            _lvStartup.Columns.Add("位置", 150);
            _lvStartup.Columns.Add("状态", 70);
            _lvStartup.Columns.Add("命令", -2);

            host.Controls.Add(_lvStartup);
            host.Controls.Add(bottom);
            host.Controls.Add(top);
            return p;
        }

        // ---------- 页签 4：性能优化 ----------
        private TabPage BuildPerfPage()
        {
            var p = new TabPage("性能优化");
            var host = MakePageHost(p);

            var top = MakeTopPanel(160);
            _lblMemory.Location = new Point(0, 6);
            _lblMemory.Size = new Size(840, 30);
            _lblMemory.Text = "内存：读取中...";
            top.Controls.Add(_lblMemory);

            int x1 = 0, x2 = 220, x3 = 440, y1 = 44, y2 = 82;

            _btnFreeMem = MakeButton("释放内存（裁剪工作集）", x1, y1, 200, 30);
            _btnFreeMem.Click += (s, e) => { try { Task.Run(() => DoFreeMemory()); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnFlushDns = MakeButton("清理 DNS 缓存", x1, y2, 200, 30);
            _btnFlushDns.Click += (s, e) => { try { Task.Run(() => DoSimple("DNS 缓存清理", log => { WindowsOptimizerCore.FlushDnsCache(log); })); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnClearArp = MakeButton("清理 ARP 缓存", x2, y1, 200, 30);
            _btnClearArp.Click += (s, e) => { try { Task.Run(() => DoSimple("ARP 缓存清理", log => { WindowsOptimizerCore.ClearArpCache(log); })); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnRefreshExplorer = MakeButton("刷新资源管理器", x2, y2, 200, 30);
            _btnRefreshExplorer.Click += (s, e) => { try { Task.Run(() => DoSimple("刷新资源管理器", log => { WindowsOptimizerCore.RefreshExplorer(log); })); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnAnimOff = MakeButton("关闭窗口动画效果", x3, y1, 200, 30);
            _btnAnimOff.Click += (s, e) => { try { Task.Run(() => DoSimple("关闭窗口动画", log => { WindowsOptimizerCore.SetVisualEffects(false, log); })); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnAnimOn = MakeButton("恢复窗口动画效果", x3, y2, 200, 30);
            _btnAnimOn.Click += (s, e) => { try { Task.Run(() => DoSimple("恢复窗口动画", log => { WindowsOptimizerCore.SetVisualEffects(true, log); })); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            top.Controls.AddRange(new Control[] { _btnFreeMem, _btnFlushDns, _btnClearArp, _btnRefreshExplorer, _btnAnimOff, _btnAnimOn });

            var tip = new Label
            {
                Text = "提示：释放内存只是把闲置内存还给系统，真正占用的程序还在跑；关闭窗口动画能减少界面拖影。",
                Location = new Point(0, 122),
                AutoSize = true
            };
            top.Controls.Add(tip);

            host.Controls.Add(top);
            return p;
        }

        // ====================== 垃圾清理 ======================

        private void DoScanClean()
        {
            EnterBusy("正在扫描...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                Log("开始扫描可清理项...");
                var targets = WindowsOptimizerCore.BuildCleanTargets();
                for (int i = 0; i < targets.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var t = targets[i];
                    SetProgress(i, targets.Count);
                    SetStatus("正在统计: " + t.DisplayName);
                    WindowsOptimizerCore.Measure(t, token, Log);
                }
                SetProgress(targets.Count, targets.Count);

                _targets.Clear();
                _targets.AddRange(targets);
                FillCleanList();
                Log(string.Format("扫描完成，共 {0} 项，合计 {1}。",
                    targets.Count, WindowsOptimizerCore.FormatSize(targets.Sum(t => t.SizeBytes))));
                SetStatus("扫描完成");
            }
            catch (OperationCanceledException) { Log("已取消扫描。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void FillCleanList()
        {
            if (_clbClean.InvokeRequired)
            {
                _clbClean.Invoke(new Action(FillCleanList));
                return;
            }
            _clbClean.Items.Clear();
            foreach (var t in _targets)
            {
                int idx = _clbClean.Items.Add(t);
                _clbClean.SetItemChecked(idx, t.Selected && t.Accessible);
            }
        }

        private void SetAllChecked(bool value)
        {
            for (int i = 0; i < _clbClean.Items.Count; i++)
                _clbClean.SetItemChecked(i, value);
        }

        private void DoClean()
        {
            if (_targets.Count == 0)
            {
                MessageBox.Show("请先点「扫描」统计可清理项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 在 UI 线程收集勾选状态
            var picked = new List<int>();
            if (_clbClean.InvokeRequired)
                _clbClean.Invoke(new Action(() => { for (int i = 0; i < _clbClean.Items.Count; i++) if (_clbClean.GetItemChecked(i)) picked.Add(i); }));
            else
                for (int i = 0; i < _clbClean.Items.Count; i++) if (_clbClean.GetItemChecked(i)) picked.Add(i);

            if (picked.Count == 0)
            {
                MessageBox.Show("没有勾选任何项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                string.Format("即将清理 {0} 项，删除的文件不进回收站且不可恢复。\n\n确定继续吗？", picked.Count),
                "确认清理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            EnterBusy("正在清理...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                Log("开始清理...");
                long total = 0;
                for (int i = 0; i < picked.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int idx = picked[i];
                    if (idx < 0 || idx >= _targets.Count) continue;
                    var t = _targets[idx];
                    SetProgress(i, picked.Count);
                    SetStatus("正在清理: " + t.DisplayName);
                    total += WindowsOptimizerCore.Clean(t, Log, token);
                }
                SetProgress(picked.Count, picked.Count);
                Log(string.Format("清理结束，共释放 {0}。", WindowsOptimizerCore.FormatSize(total)));
                SetStatus("清理完成，释放 " + WindowsOptimizerCore.FormatSize(total));
                MessageBox.Show(string.Format("清理完成，共释放 {0}。\n建议重启一次系统。", WindowsOptimizerCore.FormatSize(total)),
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DoScanClean();
            }
            catch (OperationCanceledException) { Log("已取消清理。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        // ====================== 磁盘分析 ======================

        private void RefreshDrives()
        {
            try
            {
                var rows = WindowsOptimizerCore.GetDrives();
                if (_lvDrives.InvokeRequired)
                {
                    _lvDrives.Invoke(new Action(RefreshDrives));
                    return;
                }
                _lvDrives.Items.Clear();
                foreach (var r in rows)
                {
                    var item = new ListViewItem(r.Name);
                    item.SubItems.Add(r.Format);
                    item.SubItems.Add(r.TotalText);
                    item.SubItems.Add(r.UsedText);
                    item.SubItems.Add(r.FreeText);
                    item.SubItems.Add(r.FreePercent + "%");
                    if (r.FreePercent < 10) item.ForeColor = Color.Red;
                    else if (r.FreePercent < 20) item.ForeColor = Color.DarkOrange;
                    _lvDrives.Items.Add(item);
                }
            }
            catch (Exception ex) { Log("读取磁盘信息失败: " + ex.Message); }
        }

        private void DoScanDirs()
        {
            string root = _txtRoot.Text;
            if (!Directory.Exists(root))
            {
                MessageBox.Show("目录不存在: " + root, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            EnterBusy("正在统计目录体积...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                Log("开始统计目录体积: " + root);
                var rows = WindowsOptimizerCore.ScanBigDirs(root, token, s => SetStatus(s));
                if (_lvDirs.InvokeRequired)
                    _lvDirs.Invoke(new Action(() => FillDirs(rows)));
                else FillDirs(rows);
                Log(string.Format("统计完成，共 {0} 个目录。", rows.Count));
                SetStatus("统计完成");
            }
            catch (OperationCanceledException) { Log("已取消统计。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void FillDirs(List<WindowsOptimizerCore.DirSizeRow> rows)
        {
            _lvDirs.Items.Clear();
            long total = 0;
            foreach (var r in rows) total += r.SizeBytes;
            foreach (var r in rows)
            {
                var item = new ListViewItem(r.Path);
                item.SubItems.Add(r.SizeText);
                item.SubItems.Add(total <= 0 ? "0%" : (r.SizeBytes * 100 / total) + "%");
                if (r.SizeBytes > 1024L * 1024 * 1024) item.ForeColor = Color.DarkOrange;
                _lvDirs.Items.Add(item);
            }
        }

        // ====================== 启动项 ======================

        private void RefreshStartup()
        {
            try
            {
                var items = WindowsOptimizerCore.GetStartupItems();
                _startupItems.Clear();
                _startupItems.AddRange(items);
                if (_lvStartup.InvokeRequired)
                {
                    _lvStartup.Invoke(new Action(FillStartup));
                    return;
                }
                FillStartup();
            }
            catch (Exception ex) { Log("读取启动项失败: " + ex.Message); }
        }

        private void FillStartup()
        {
            _lvStartup.Items.Clear();
            foreach (var it in _startupItems)
            {
                var item = new ListViewItem(it.Name);
                item.SubItems.Add(it.LocationText);
                item.SubItems.Add(it.Enabled ? "已启用" : "已禁用");
                item.SubItems.Add(it.Command);
                item.ForeColor = it.Enabled ? SystemColors.WindowText : Color.Gray;
                item.Tag = it;
                _lvStartup.Items.Add(item);
            }
        }

        private void DoSetStartup(bool enable)
        {
            var picked = new List<WindowsOptimizerCore.StartupItem>();
            if (_lvStartup.InvokeRequired)
            {
                _lvStartup.Invoke(new Action(() =>
                {
                    foreach (ListViewItem li in _lvStartup.SelectedItems)
                    {
                        var it = li.Tag as WindowsOptimizerCore.StartupItem;
                        if (it != null) picked.Add(it);
                    }
                }));
            }
            else
            {
                foreach (ListViewItem li in _lvStartup.SelectedItems)
                {
                    var it = li.Tag as WindowsOptimizerCore.StartupItem;
                    if (it != null) picked.Add(it);
                }
            }

            if (picked.Count == 0)
            {
                MessageBox.Show("请先在列表中选中启动项。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            EnterBusy(enable ? "正在启用..." : "正在禁用...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                Log(enable ? "启用启动项:" : "禁用启动项:");
                int ok = 0;
                for (int i = 0; i < picked.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    SetProgress(i, picked.Count);
                    if (WindowsOptimizerCore.SetStartupEnabled(picked[i], enable, Log)) ok++;
                }
                SetProgress(picked.Count, picked.Count);
                Log(string.Format("完成，成功 {0} / 共 {1}。", ok, picked.Count));
                SetStatus((enable ? "启用" : "禁用") + "完成");
                RefreshStartup();
            }
            catch (OperationCanceledException) { Log("已取消。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        // ====================== 性能优化 ======================

        private void DoFreeMemory()
        {
            EnterBusy("正在释放内存...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                int n = WindowsOptimizerCore.ReleaseMemory(Log, token);
                SetStatus("内存优化完成，处理 " + n + " 个进程");
                RefreshMemory();
            }
            catch (OperationCanceledException) { Log("已取消。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void DoSimple(string title, Action<Action<string>> action)
        {
            EnterBusy(title + "...");
            _cts = new CancellationTokenSource();
            try
            {
                Log(title + "...");
                action(Log);
                SetStatus(title + "完成");
                RefreshMemory();
            }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void RefreshMemory()
        {
            try
            {
                var m = WindowsOptimizerCore.GetMemoryStatus();
                string text = string.Format("内存：总计 {0}    已用 {1} ({2}%)    可用 {3}",
                    m.TotalText, m.UsedText, m.UsedPercent, m.AvailableText);
                if (_lblMemory.InvokeRequired)
                {
                    _lblMemory.Invoke(new Action(() => _lblMemory.Text = text));
                    return;
                }
                _lblMemory.Text = text;
            }
            catch (Exception) { }
        }

        // ====================== 状态与日志 ======================

        private void EnterBusy(string status)
        {
            _busy = true;
            SetControlsEnabled(false);
            SetStatus(status);
        }

        private void LeaveBusy()
        {
            _busy = false;
            _cts = null;
            SetControlsEnabled(true);
        }

        private void SetControlsEnabled(bool enabled)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<bool>(SetControlsEnabled), enabled);
                return;
            }
            _btnScanClean.Enabled = enabled;
            _btnClean.Enabled = enabled;
            _btnSelAll.Enabled = enabled;
            _btnSelNone.Enabled = enabled;
            _btnScanDirs.Enabled = enabled;
            _btnDisable.Enabled = enabled;
            _btnEnable.Enabled = enabled;
            _btnFreeMem.Enabled = enabled;
            _btnFlushDns.Enabled = enabled;
            _btnClearArp.Enabled = enabled;
            _btnRefreshExplorer.Enabled = enabled;
            _btnAnimOff.Enabled = enabled;
            _btnAnimOn.Enabled = enabled;
            _btnOptScan.Enabled = enabled;
            _btnOptRun.Enabled = enabled;
            _btnOptRestorePoint.Enabled = enabled;
            _btnOptRollback.Enabled = enabled;
            _btnOptAll.Enabled = enabled;
            _btnOptNone.Enabled = enabled;
            _btnDupScan.Enabled = enabled;
            _btnDupDel.Enabled = enabled;
            _btnSaveCfg.Enabled = enabled;
        }

        private void Log(string msg)
        {
            if (_rtbLog.InvokeRequired)
            {
                _rtbLog.Invoke(new Action<string>(Log), msg);
                return;
            }
            _rtbLog.AppendText(msg + Environment.NewLine);
            _rtbLog.SelectionStart = _rtbLog.TextLength;
            _rtbLog.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            if (_lblStatus.InvokeRequired)
            {
                _lblStatus.Invoke(new Action<string>(SetStatus), text);
                return;
            }
            _lblStatus.Text = text;
        }

        private void SetProgress(int value, int maximum)
        {
            if (_pb.InvokeRequired)
            {
                _pb.Invoke(new Action<int, int>(SetProgress), value, maximum);
                return;
            }
            if (maximum <= 0) maximum = 1;
            _pb.Maximum = maximum;
            _pb.Value = Math.Max(0, Math.Min(value, maximum));
        }
    }
}
