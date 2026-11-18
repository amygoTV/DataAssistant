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
    /// WindowsOptimizerPanel 的扩展页签：系统优化项 / 重复文件 / 路径配置。
    /// 与主文件里的页签共用同一套日志、进度与忙碌门闩。
    /// </summary>
    public partial class WindowsOptimizerPanel
    {
        // ---- 系统优化项 ----
        private readonly ListView _lvOpt = new ListView();
        private Button _btnOptScan = new Button();
        private Button _btnOptRun = new Button();
        private Button _btnOptRestorePoint = new Button();
        private Button _btnOptRollback = new Button();
        private Button _btnOptAll = new Button();
        private Button _btnOptNone = new Button();
        private readonly List<OptItem> _optItems =
            new List<OptItem>();

        // ---- 重复文件 ----
        private readonly ListView _lvDup = new ListView();
        private readonly TextBox _txtDupFolders = new TextBox();
        private Button _btnDupScan = new Button();
        private Button _btnDupDel = new Button();
        private readonly Label _lblDupStatus = new Label();
        private readonly List<WindowsOptimizerCore.DupFile> _dupFiles =
            new List<WindowsOptimizerCore.DupFile>();

        // ---- 路径配置 ----
        private readonly TextBox _txtDataRoot = new TextBox();
        private readonly TextBox _txtAIRoot = new TextBox();
        private readonly TextBox _txtAIJunk = new TextBox();
        private readonly TextBox _txtDupCfg = new TextBox();
        private readonly TextBox _txtAIExtra = new TextBox();
        private Button _btnPickDataRoot = new Button();
        private Button _btnPickAIRoot = new Button();
        private Button _btnSaveCfg = new Button();

        // ====================== 页签构建 ======================

        /// <summary>构建「系统优化项」页签（系统设置类 + 隐私强化类）。</summary>
        private TabPage BuildOptPage()
        {
            var p = new TabPage("系统优化项");
            var host = MakePageHost(p);

            var top = MakeTopPanel(26);
            var tip = new Label
            {
                Text = "勾选要执行的项目。所有注册表/服务改动会先自动备份，可在「一键还原」里回滚。",
                Location = new Point(0, 4),
                AutoSize = true
            };
            top.Controls.Add(tip);

            var bottom = MakeBottomPanel(36);
            _btnOptScan = MakeButton("扫描预览", 0, 4, 100, 28);
            _btnOptScan.Click += (s, e) =>
            {
                try { DoOptPreview(); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnOptRun = MakeButton("执行选中项", 108, 4, 110, 28);
            _btnOptRun.BackColor = Color.LightGreen;
            _btnOptRun.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoOptRun()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnOptRestorePoint = MakeButton("创建还原点", 226, 4, 110, 28);
            _btnOptRestorePoint.Click += (s, e) =>
            {
                if (_busy) return;
                try { Task.Run(() => DoCreateRestorePoint()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnOptRollback = MakeButton("一键还原", 344, 4, 100, 28);
            _btnOptRollback.BackColor = Color.LightCoral;
            _btnOptRollback.Click += (s, e) =>
            {
                if (_busy) return;
                try { Task.Run(() => DoOptRollback()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };

            _btnOptAll = MakeButton("全选", 452, 4, 80, 28);
            _btnOptAll.Click += (s, e) => { try { SetOptAllChecked(true); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            _btnOptNone = MakeButton("全不选", 540, 4, 90, 28);
            _btnOptNone.Click += (s, e) => { try { SetOptAllChecked(false); } catch (Exception ex) { Log("异常: " + ex.Message); } };

            bottom.Controls.AddRange(new Control[]
            {
                _btnOptScan, _btnOptRun, _btnOptRestorePoint,
                _btnOptRollback, _btnOptAll, _btnOptNone
            });

            _lvOpt.Dock = DockStyle.Fill;
            _lvOpt.View = View.Details;
            _lvOpt.FullRowSelect = true;
            _lvOpt.GridLines = true;
            _lvOpt.CheckBoxes = true;
            _lvOpt.Columns.Add("项目", 470);
            _lvOpt.Columns.Add("风险", 60);
            _lvOpt.Columns.Add("说明", -2);

            // 分组标题不可勾选：ItemCheck 无法取消，改为在状态变化后回勾
            _lvOpt.ItemChecked += (s, e) =>
            {
                try
                {
                    if (e.Item != null && e.Item.Tag == null && e.Item.Checked)
                        e.Item.Checked = false;
                }
                catch (Exception) { }
            };

            host.Controls.Add(_lvOpt);
            host.Controls.Add(bottom);
            host.Controls.Add(top);

            LoadOptItems();
            return p;
        }

        /// <summary>构建「重复文件」页签。</summary>
        private TabPage BuildDupPage()
        {
            var p = new TabPage("重复文件");
            var host = MakePageHost(p);

            var top = MakeTopPanel(122);
            var tip = new Label
            {
                Text = "扫描目录（每行一个）:",
                Location = new Point(0, 4),
                AutoSize = true
            };
            top.Controls.Add(tip);

            _txtDupFolders.Location = new Point(106, 2);
            _txtDupFolders.Size = new Size(620, 58);
            _txtDupFolders.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtDupFolders.Multiline = true;
            _txtDupFolders.ScrollBars = ScrollBars.Vertical;
            top.Controls.Add(_txtDupFolders);

            _btnDupScan = MakeButton("扫描重复文件", 736, 2, 104, 28);
            _btnDupScan.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoDupScan()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            top.Controls.Add(_btnDupScan);

            _btnDupDel = MakeButton("删除勾选的文件", 0, 68, 140, 28);
            _btnDupDel.BackColor = Color.LightCoral;
            _btnDupDel.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoDupDelete()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            top.Controls.Add(_btnDupDel);

            _lblDupStatus.Location = new Point(150, 72);
            _lblDupStatus.Size = new Size(690, 20);
            _lblDupStatus.Text = "就绪";
            _lblDupStatus.ForeColor = Color.DimGray;
            top.Controls.Add(_lblDupStatus);

            var hint = new Label
            {
                Text = "说明：每组重复文件中，路径最短/最早的一个会被保留，其余默认勾选为可删。请人工核对后再删除。",
                Location = new Point(0, 100),
                AutoSize = true
            };
            top.Controls.Add(hint);

            _lvDup.Dock = DockStyle.Fill;
            _lvDup.View = View.Details;
            _lvDup.FullRowSelect = true;
            _lvDup.GridLines = true;
            _lvDup.CheckBoxes = true;
            _lvDup.Columns.Add("文件路径", 660);
            _lvDup.Columns.Add("大小", 100);
            _lvDup.Columns.Add("修改时间", -2);

            host.Controls.Add(_lvDup);
            host.Controls.Add(top);
            return p;
        }

        /// <summary>构建「路径配置」页签。</summary>
        private TabPage BuildCfgPage()
        {
            var p = new TabPage("路径配置");
            var host = MakePageHost(p);

            var top = MakeTopPanel(404);
            var l1 = new Label { Text = "用户库根目录（迁移目标）:", Location = new Point(0, 8), AutoSize = true };
            top.Controls.Add(l1);
            _txtDataRoot.Location = new Point(170, 6);
            _txtDataRoot.Size = new Size(570, 23);
            _txtDataRoot.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            top.Controls.Add(_txtDataRoot);
            _btnPickDataRoot = MakeButton("浏览", 748, 6, 92, 23);
            _btnPickDataRoot.Click += (s, e) => { try { PickFolder(_txtDataRoot); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            top.Controls.Add(_btnPickDataRoot);

            var l2 = new Label { Text = "AI 产出重定向根目录:", Location = new Point(0, 36), AutoSize = true };
            top.Controls.Add(l2);
            _txtAIRoot.Location = new Point(170, 34);
            _txtAIRoot.Size = new Size(570, 23);
            _txtAIRoot.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            top.Controls.Add(_txtAIRoot);
            _btnPickAIRoot = MakeButton("浏览", 748, 34, 92, 23);
            _btnPickAIRoot.Click += (s, e) => { try { PickFolder(_txtAIRoot); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            top.Controls.Add(_btnPickAIRoot);

            var l3 = new Label
            {
                Text = "AI 工具垃圾清理路径（每行一个，可用 %环境变量% 与通配符）:",
                Location = new Point(0, 64),
                AutoSize = true
            };
            top.Controls.Add(l3);
            _txtAIJunk.Location = new Point(0, 84);
            _txtAIJunk.Size = new Size(840, 104);
            _txtAIJunk.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtAIJunk.Multiline = true;
            _txtAIJunk.ScrollBars = ScrollBars.Vertical;
            top.Controls.Add(_txtAIJunk);

            var l4 = new Label
            {
                Text = "重复文件扫描目录（每行一个）:",
                Location = new Point(0, 194),
                AutoSize = true
            };
            top.Controls.Add(l4);
            _txtDupCfg.Location = new Point(0, 214);
            _txtDupCfg.Size = new Size(840, 62);
            _txtDupCfg.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtDupCfg.Multiline = true;
            _txtDupCfg.ScrollBars = ScrollBars.Vertical;
            top.Controls.Add(_txtDupCfg);

            var l5 = new Label
            {
                Text = "额外 AI 产出重定向目录（每行一个源路径，自动以末级目录名建到 AI 根目录）:",
                Location = new Point(0, 282),
                AutoSize = true
            };
            top.Controls.Add(l5);
            _txtAIExtra.Location = new Point(0, 302);
            _txtAIExtra.Size = new Size(840, 62);
            _txtAIExtra.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _txtAIExtra.Multiline = true;
            _txtAIExtra.ScrollBars = ScrollBars.Vertical;
            top.Controls.Add(_txtAIExtra);

            var bottom = MakeBottomPanel(40);
            _btnSaveCfg = MakeButton("保存配置", 0, 5, 120, 30);
            _btnSaveCfg.Click += (s, e) =>
            {
                try { SaveConfig(); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            bottom.Controls.Add(_btnSaveCfg);

            host.Controls.Add(top);
            host.Controls.Add(bottom);

            LoadConfigToUi();
            return p;
        }

        private void PickFolder(TextBox tb)
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择目录" })
            {
                if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.SelectedPath;
            }
        }

        // ====================== 系统优化项 ======================

        private void LoadOptItems()
        {
            try
            {
                _optItems.Clear();
                _optItems.AddRange(WindowsOptimizerSettings.BuildAll());
            }
            catch (Exception ex) { Log("加载优化项失败: " + ex.Message); }
        }

        private void FillOptList()
        {
            if (_lvOpt.InvokeRequired)
            {
                _lvOpt.Invoke(new Action(FillOptList));
                return;
            }
            _lvOpt.Items.Clear();
            string curCat = null;
            foreach (var it in _optItems)
            {
                if (it.Category != curCat)
                {
                    curCat = it.Category;
                    var head = new ListViewItem("— " + curCat + " —");
                    head.Font = new Font(_lvOpt.Font, FontStyle.Bold);
                    head.ForeColor = Color.DimGray;
                    head.Tag = null; // 分组标题：不可勾选
                    _lvOpt.Items.Add(head);
                }
                var li = new ListViewItem(it.Title);
                li.SubItems.Add(it.RiskText);
                li.SubItems.Add(it.Note);
                li.Tag = it;
                if (it.Risk == OptRisk.High)
                {
                    li.ForeColor = Color.Red;
                    li.SubItems[1].ForeColor = Color.Red;
                }
                _lvOpt.Items.Add(li);
            }
            // 默认勾选
            for (int i = 0; i < _lvOpt.Items.Count; i++)
            {
                var it = _lvOpt.Items[i].Tag as OptItem;
                if (it != null) _lvOpt.Items[i].Checked = it.DefaultChecked;
            }
        }

        private void SetOptAllChecked(bool value)
        {
            if (_lvOpt.InvokeRequired)
            {
                _lvOpt.Invoke(new Action<bool>(SetOptAllChecked), value);
                return;
            }
            foreach (ListViewItem li in _lvOpt.Items)
            {
                if (li.Tag != null) li.Checked = value;
            }
        }

        private List<OptItem> GetCheckedOptItems()
        {
            var picked = new List<OptItem>();
            if (_lvOpt.InvokeRequired)
            {
                _lvOpt.Invoke(new Action(() =>
                {
                    foreach (ListViewItem li in _lvOpt.Items)
                    {
                        if (li.Tag is OptItem && li.Checked)
                            picked.Add((OptItem)li.Tag);
                    }
                }));
            }
            else
            {
                foreach (ListViewItem li in _lvOpt.Items)
                {
                    if (li.Tag is OptItem && li.Checked)
                        picked.Add((OptItem)li.Tag);
                }
            }
            return picked;
        }

        private void DoOptPreview()
        {
            var picked = GetCheckedOptItems();
            if (picked.Count == 0)
            {
                MessageBox.Show("请先勾选要执行的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log(">> 扫描预览：");
            int high = picked.Count(i => i.Risk == OptRisk.High);
            foreach (var it in picked)
            {
                Log(string.Format("   · [{0}] {1}", it.RiskText, it.Title));
                if (!string.IsNullOrEmpty(it.Note)) Log("      " + it.Note);
            }
            Log(string.Format("   共 {0} 项，其中高危 {1} 项。", picked.Count, high));
            if (high > 0)
                Log("   ⚠ 高危项会明显改变系统行为，执行前会再次确认。");
            Log("   已改动过的注册表/服务项会被自动备份，随时可「一键还原」。");
            SetStatus("预览完成，请确认后点「执行选中项」");
        }

        private void DoOptRun()
        {
            var picked = GetCheckedOptItems();
            if (picked.Count == 0)
            {
                MessageBox.Show("请先勾选要执行的项目。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 高危项二次确认
            var high = picked.Where(i => i.Risk == OptRisk.High).ToList();
            if (high.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("以下高危操作将被执行，确认继续？");
                sb.AppendLine();
                foreach (var it in high) sb.AppendLine("· " + it.Title + "\n    " + it.Note);
                var r = MessageBox.Show(sb.ToString(), "二次确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes)
                {
                    Log("用户取消了高危操作，已中止。");
                    return;
                }
            }

            EnterBusy("正在执行优化项...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int ok = 0, fail = 0;
            try
            {
                Log("########## 开始执行优化项 ##########");
                for (int i = 0; i < picked.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var it = picked[i];
                    SetProgress(i, picked.Count);
                    SetStatus("正在执行: " + it.Title);
                    Log(">> " + it.Title);
                    try
                    {
                        if (it.Apply != null) it.Apply(Log);
                        ok++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        fail++;
                        Log("   [失败] " + it.Title + " -> " + ex.Message);
                    }
                }
                SetProgress(picked.Count, picked.Count);
                Log(string.Format("########## 执行完成：成功 {0} 项，失败 {1} 项 ##########", ok, fail));
                SetStatus("执行完成（部分设置需重启/重新登录后生效）");
                MessageBox.Show(string.Format(
                    "优化完成！成功 {0} 项，失败 {1} 项。\n部分设置需重启或重新登录后生效。\n可随时用「一键还原」回滚。", ok, fail),
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Log("已取消执行。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void DoCreateRestorePoint()
        {
            EnterBusy("正在创建系统还原点...");
            try
            {
                Log(">> 创建系统还原点 ...");
                bool ok = WindowsOptimizerSettings.CreateRestorePoint(
                    "数据助手 优化前-" + DateTime.Now.ToString("HHmmss"), Log);
                SetStatus(ok ? "还原点已创建" : "还原点创建失败");
                MessageBox.Show(ok
                    ? "系统还原点已创建。\n若优化后出现问题，可在「系统保护」里回滚。"
                    : "创建还原点失败。\n可能是该系统未开启「系统保护」，或当前不是管理员权限。",
                    ok ? "完成" : "提示", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void DoOptRollback()
        {
            int n = WindowsOptimizerSettings.BackupCount;
            if (n == 0)
            {
                MessageBox.Show("没有可还原的备份记录。\n（只有执行过优化项之后才会产生备份）",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var r = MessageBox.Show(string.Format(
                "将把 {0} 项注册表/服务设置还原为优化前的状态。\n\n确定继续吗？", n),
                "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            EnterBusy("正在还原...");
            try
            {
                Log(">> 一键还原优化前的设置 ...");
                int ok = WindowsOptimizerSettings.RestoreAll(Log);
                SetStatus("还原完成");
                MessageBox.Show(string.Format("还原完成，共 {0} 项。", ok),
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        // ====================== 重复文件 ======================

        private void DoDupScan()
        {
            var folders = GetDupFoldersFromUi();
            if (folders.Count == 0)
            {
                MessageBox.Show("请在「重复文件」页填写至少一个扫描目录。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            EnterBusy("正在扫描重复文件...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                Log(">> 扫描重复文件，目录: " + string.Join(", ", folders.ToArray()));
                SetStatus("正在扫描重复文件...");
                var result = WindowsOptimizerCore.FindDuplicates(folders, token, n =>
                {
                    SetStatus(string.Format("扫描中... 已处理 {0:N0} 个文件", n));
                });

                _dupFiles.Clear();
                _dupFiles.AddRange(result.Duplicates);

                if (_lvDup.InvokeRequired)
                    _lvDup.Invoke(new Action(() => FillDupList()));
                else FillDupList();

                string msg = string.Format(
                    "完成：处理 {0:N0} 个文件，用时 {1:N0} 秒；{2} 组重复，可删 {3} 个（约 {4}）",
                    result.FilesScanned, result.Seconds, result.Groups,
                    result.Duplicates.Count, WindowsOptimizerCore.FormatSize(result.RecoverableBytes));

                if (_lblDupStatus.InvokeRequired)
                    _lblDupStatus.Invoke(new Action(() => _lblDupStatus.Text = msg));
                else _lblDupStatus.Text = msg;

                Log(string.Format("找到可删重复文件 {0} 个（处理 {1} 个文件，跳过 {2} 个）",
                    result.Duplicates.Count, result.FilesScanned, result.FilesSkipped));
                SetStatus("重复文件扫描完成");
            }
            catch (OperationCanceledException) { Log("已取消扫描。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private void FillDupList()
        {
            _lvDup.Items.Clear();
            foreach (var d in _dupFiles)
            {
                var li = new ListViewItem(d.FullPath);
                li.SubItems.Add(d.SizeText);
                li.SubItems.Add(SafeWriteTime(d.FullPath));
                li.Checked = true; // 默认勾选为可删
                _lvDup.Items.Add(li);
            }
        }

        private static string SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm"); }
            catch (Exception) { return ""; }
        }

        private void DoDupDelete()
        {
            var picked = new List<string>();
            if (_lvDup.InvokeRequired)
            {
                _lvDup.Invoke(new Action(() =>
                {
                    foreach (ListViewItem li in _lvDup.Items) if (li.Checked) picked.Add(li.Text);
                }));
            }
            else
            {
                foreach (ListViewItem li in _lvDup.Items) if (li.Checked) picked.Add(li.Text);
            }

            if (picked.Count == 0)
            {
                MessageBox.Show("没有勾选任何文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var r = MessageBox.Show(string.Format(
                "将永久删除 {0} 个文件（不进回收站，不可恢复）。\n\n确定继续吗？", picked.Count),
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            EnterBusy("正在删除重复文件...");
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int del = 0, fail = 0;
            try
            {
                Log(">> 删除勾选的重复文件 ...");
                for (int i = 0; i < picked.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        File.SetAttributes(picked[i], FileAttributes.Normal);
                        File.Delete(picked[i]);
                        del++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log("   删除失败: " + picked[i] + " -> " + ex.Message);
                    }
                }
                Log(string.Format("已删除重复文件 {0} 个（失败 {1} 个）", del, fail));
                SetStatus(string.Format("已删除 {0} 个重复文件", del));
                if (_lvDup.InvokeRequired)
                    _lvDup.Invoke(new Action(() => _lvDup.Items.Clear()));
                else _lvDup.Items.Clear();
                _dupFiles.Clear();
            }
            catch (OperationCanceledException) { Log("已取消删除。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        private List<string> GetDupFoldersFromUi()
        {
            var list = new List<string>();
            string text = _txtDupFolders.InvokeRequired ? (string)_txtDupFolders.Invoke(new Func<string>(() => _txtDupFolders.Text)) : _txtDupFolders.Text;
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        // ====================== 路径配置 ======================

        private void LoadConfigToUi()
        {
            try
            {
                var cfg = OptimizerConfig.Load();
                _txtDataRoot.Text = cfg.DataRoot;
                _txtAIRoot.Text = cfg.AIRoot;
                _txtAIJunk.Text = string.Join(Environment.NewLine, cfg.AIJunkPaths.ToArray());
                _txtDupCfg.Text = string.Join(Environment.NewLine, cfg.DupScanFolders.ToArray());
                _txtAIExtra.Text = string.Join(Environment.NewLine, cfg.AIRedirectExtra.ToArray());
                if (_txtDupFolders.Text.Length == 0)
                    _txtDupFolders.Text = string.Join(Environment.NewLine, cfg.DupScanFolders.ToArray());
            }
            catch (Exception ex) { Log("读取配置失败: " + ex.Message); }
        }

        private void SaveConfig()
        {
            try
            {
                var cfg = new OptimizerConfig
                {
                    DataRoot = _txtDataRoot.Text.Trim(),
                    AIRoot = _txtAIRoot.Text.Trim(),
                    AIJunkPaths = SplitLines(_txtAIJunk.Text),
                    DupScanFolders = SplitLines(_txtDupCfg.Text),
                    AIRedirectExtra = SplitLines(_txtAIExtra.Text)
                };
                cfg.Save();
                Log("配置已保存: " + OptimizerConfig.ConfigPath);
                SetStatus("配置已保存");
                MessageBox.Show("配置已保存。\n" + OptimizerConfig.ConfigPath,
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { Log("保存配置失败: " + ex.Message); }
        }

        private static List<string> SplitLines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        // ====================== 界面设置持久化 ======================

        public string SettingsSection { get { return "Optimizer"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;

            // 系统优化项：按 OptItem.Id 存勾选状态（分组标题 Tag 为 null，跳过）
            var optChecked = new List<string>();
            var optUnchecked = new List<string>();
            for (int i = 0; i < _lvOpt.Items.Count; i++)
            {
                var it = _lvOpt.Items[i].Tag as OptItem;
                if (it == null) continue;
                if (_lvOpt.Items[i].Checked) optChecked.Add(it.Id);
                else optUnchecked.Add(it.Id);
            }
            store.SetList(s, "OptChecked", optChecked);
            store.SetList(s, "OptUnchecked", optUnchecked);

            // 网络调优项：同理，按 NetOptItem.Id
            var netChecked = new List<string>();
            var netUnchecked = new List<string>();
            for (int i = 0; i < _lvNetItems.Items.Count; i++)
            {
                var it = _lvNetItems.Items[i].Tag as NetOptItem;
                if (it == null) continue;
                if (_lvNetItems.Items[i].Checked) netChecked.Add(it.Id);
                else netUnchecked.Add(it.Id);
            }
            store.SetList(s, "NetChecked", netChecked);
            store.SetList(s, "NetUnchecked", netUnchecked);

            // 重复文件扫描目录（多行文本框，按行拆开存）
            store.SetList(s, "DupFolders", SplitLines(_txtDupFolders.Text));
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;

            // 系统优化项
            var optChecked = store.GetList(s, "OptChecked");
            var optUnchecked = store.GetList(s, "OptUnchecked");
            if (optChecked.Count > 0 || optUnchecked.Count > 0)
            {
                for (int i = 0; i < _lvOpt.Items.Count; i++)
                {
                    var it = _lvOpt.Items[i].Tag as OptItem;
                    if (it == null) continue;
                    if (optChecked.Contains(it.Id)) _lvOpt.Items[i].Checked = true;
                    else if (optUnchecked.Contains(it.Id)) _lvOpt.Items[i].Checked = false;
                }
            }

            // 网络调优项
            var netChecked = store.GetList(s, "NetChecked");
            var netUnchecked = store.GetList(s, "NetUnchecked");
            if (netChecked.Count > 0 || netUnchecked.Count > 0)
            {
                for (int i = 0; i < _lvNetItems.Items.Count; i++)
                {
                    var it = _lvNetItems.Items[i].Tag as NetOptItem;
                    if (it == null) continue;
                    if (netChecked.Contains(it.Id)) _lvNetItems.Items[i].Checked = true;
                    else if (netUnchecked.Contains(it.Id)) _lvNetItems.Items[i].Checked = false;
                }
            }

            // 重复文件扫描目录（只在用户自己填过时才覆盖，避免覆盖掉从配置同步来的默认值）
            var dupFolders = store.GetList(s, "DupFolders");
            if (dupFolders.Count > 0)
                _txtDupFolders.Text = string.Join(Environment.NewLine, dupFolders.ToArray());
        }
    }
}
