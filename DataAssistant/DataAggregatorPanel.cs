using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataAssistant
{
    /// <summary>
    /// 可复用的"数据整理页"控件（数据助手）。
    /// 功能：
    ///  1) 汇总一个文件夹内所有 CSV/TXT/Excel 表格，并删除用户指定的数据行
    ///     （SN 列为空 / SN 列像 SN/SN1/NOSN 占位符 / 其它列含 999/999.999 等关键字）；
    ///  2) 按用户关键字筛选数据行并导出到指定文件夹。
    /// 直接用 Visual Studio 工具箱拖到主窗体上（Dock = Fill 即可），也可代码方式调用。
    /// </summary>
    public class DataAggregatorPanel : UserControl, ISettingsAware
    {
        private readonly TextBox _txtDataFolder = new TextBox();
        private readonly TextBox _txtExportFolder = new TextBox();
        private readonly CheckBox _chkSubdirs = new CheckBox();
        private readonly CheckBox _chkHeader = new CheckBox();
        private readonly Button _btnPickData = new Button();
        private readonly Button _btnPickExport = new Button();
        private readonly Button _btnAggregate = new Button();
        private readonly Button _btnClear = new Button();

        private readonly DataGridView _grid = new DataGridView();

        // 删除规则分组
        private readonly GroupBox _grpDelete = new GroupBox();
        private readonly CheckBox _chkDelEmptySn = new CheckBox();
        private readonly CheckBox _chkDelDupSn = new CheckBox();
        private readonly ComboBox _cmbDupKeep = new ComboBox();
        private readonly CheckBox _chkDelSnJunk = new CheckBox();
        private readonly TextBox _txtSnJunk = new TextBox();
        private readonly CheckBox _chkDelKw = new CheckBox();
        private readonly TextBox _txtDelKw = new TextBox();
        private readonly Label _lblDelKwCol = new Label();
        private readonly ComboBox _cmbDelKwCol = new ComboBox();
        private readonly Label _lblSnCol = new Label();
        private readonly ComboBox _cmbSnCol = new ComboBox();
        private readonly Button _btnDelete = new Button();
        private readonly Button _btnPreview = new Button();
        private readonly Button _btnExportClean = new Button();
        private readonly ToolTip _tip = new ToolTip();

        // 筛选导出分组
        private readonly GroupBox _grpFilter = new GroupBox();
        private readonly Label _lblFilterKw = new Label();
        private readonly TextBox _txtFilterKw = new TextBox();
        private readonly Label _lblScope = new Label();
        private readonly ComboBox _cmbScope = new ComboBox();
        private readonly Label _lblMode = new Label();
        private readonly ComboBox _cmbMode = new ComboBox();
        private readonly CheckBox _chkPerFile = new CheckBox();
        private readonly CheckBox _chkCombined = new CheckBox();
        private readonly Label _lblCmbFmt = new Label();
        private readonly ComboBox _cmbCombinedFmt = new ComboBox();
        private readonly Button _btnExport = new Button();

        private readonly ProgressBar _pb = new ProgressBar();
        private readonly Label _lblStatus = new Label();
        private readonly RichTextBox _rtbLog = new RichTextBox();
        private readonly PictureBox _logo = new PictureBox();

        private DataAggregatorCore.AggregateResult _agg;
        private bool _busy;
        private CancellationTokenSource _cts;

        // 待恢复的列名：SN列/关键字匹配列/筛选列 三个下拉框要等"汇总"后才有选项，
        // 所以启动时先记下来，等汇总完成后再套用。
        private string _pendingSnCol;
        private string _pendingDelKwCol;
        private string _pendingFilterCol;

        public DataAggregatorPanel()
        {
            InitializeComponent();
        }

        // -------- 对外属性（方便主程序预设）--------
        public string DataFolder { get { return _txtDataFolder.Text; } set { _txtDataFolder.Text = value; } }
        public string ExportFolder { get { return _txtExportFolder.Text; } set { _txtExportFolder.Text = value; } }
        public bool HeaderRow { get { return _chkHeader.Checked; } set { _chkHeader.Checked = value; } }
        public bool IncludeSubfolders { get { return _chkSubdirs.Checked; } set { _chkSubdirs.Checked = value; } }

        // -------- 对外方法（代码触发）--------
        public void StartAggregate() { OnAggregateClicked(); }
        public void ApplyDelete() { OnDeleteClicked(); }
        public void ExportClean() { OnExportCleanClicked(); }
        public void FilterAndExport() { OnExportClicked(); }

        private void InitializeComponent()
        {
            Size = new System.Drawing.Size(900, 740);
            Font = new System.Drawing.Font("Microsoft YaHei", 9.5f);
            AutoScroll = true;

            // Logo
            _logo.SetBounds(8, 8, 300, 44);
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _logo.Image = LoadLogo();
            Controls.Add(_logo);

            // 数据文件夹
            AddLabel("数据文件夹:", 8, 60);
            _txtDataFolder.SetBounds(96, 58, 360, 23);
            _btnPickData.SetBounds(462, 58, 80, 23); _btnPickData.Text = "选择"; _btnPickData.Click += (s, e) => PickFolder(_txtDataFolder);
            _chkSubdirs.SetBounds(556, 60, 120, 23); _chkSubdirs.Text = "包含子文件夹";
            Controls.AddRange(new Control[] { _txtDataFolder, _btnPickData, _chkSubdirs });

            // 导出文件夹
            AddLabel("导出文件夹:", 8, 90);
            _txtExportFolder.SetBounds(96, 88, 360, 23);
            _btnPickExport.SetBounds(462, 88, 80, 23); _btnPickExport.Text = "选择"; _btnPickExport.Click += (s, e) => PickFolder(_txtExportFolder);
            _chkHeader.SetBounds(556, 90, 110, 23); _chkHeader.Text = "首行是表头"; _chkHeader.Checked = true;
            Controls.AddRange(new Control[] { _txtExportFolder, _btnPickExport, _chkHeader });

            // 汇总 / 清空
            _btnAggregate.SetBounds(676, 58, 100, 26); _btnAggregate.Text = "汇总"; _btnAggregate.Click += (s, e) => OnAggregateClicked();
            _btnClear.SetBounds(676, 90, 100, 26); _btnClear.Text = "清空"; _btnClear.Click += (s, e) => OnClearClicked();
            Controls.AddRange(new Control[] { _btnAggregate, _btnClear });

            // 数据网格
            _grid.SetBounds(8, 122, 864, 210);
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            _grid.MaximumSize = new Size(0, 0);
            Controls.Add(_grid);

            // ---- 删除规则分组 ----
            _grpDelete.SetBounds(8, 344, 430, 214);
            _grpDelete.Text = "删除规则（应用到上方汇总数据）";
            _grpDelete.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            // children（相对分组坐标）
            _chkDelEmptySn.SetBounds(12, 22, 160, 22);
            _chkDelEmptySn.Text = "删除 SN 列为空的行"; _chkDelEmptySn.Checked = true;
            _chkDelDupSn.SetBounds(180, 22, 150, 22);
            _chkDelDupSn.Text = "删除 SN 重复的行"; _chkDelDupSn.Checked = true;
            _cmbDupKeep.SetBounds(336, 20, 82, 23); _cmbDupKeep.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDupKeep.Items.AddRange(new object[] { "保留首条", "保留末条" });
            _cmbDupKeep.SelectedIndex = 0;
            _chkDelSnJunk.SetBounds(12, 50, 250, 22);
            _chkDelSnJunk.Text = "SN列=以下占位符(逗号分隔):"; _chkDelSnJunk.Checked = true;
            _txtSnJunk.SetBounds(268, 48, 150, 23); _txtSnJunk.Text = "SN,SN1,NOSN";
            _chkDelKw.SetBounds(12, 78, 250, 22);
            _chkDelKw.Text = "其它列含关键字(逗号分隔)"; _chkDelKw.Checked = true;
            _txtDelKw.SetBounds(268, 76, 150, 23); _txtDelKw.Text = "999,999.999";
            _lblDelKwCol.SetBounds(12, 106, 84, 22); _lblDelKwCol.Text = "关键字匹配列:"; _lblDelKwCol.TextAlign = ContentAlignment.MiddleLeft;
            _cmbDelKwCol.SetBounds(100, 104, 318, 23); _cmbDelKwCol.DropDownStyle = ComboBoxStyle.DropDownList;
            _lblSnCol.SetBounds(12, 134, 78, 22); _lblSnCol.Text = "SN列(序列号):"; _lblSnCol.TextAlign = ContentAlignment.MiddleLeft;
            _cmbSnCol.SetBounds(96, 132, 160, 23); _cmbSnCol.DropDownStyle = ComboBoxStyle.DropDownList;
            _btnPreview.SetBounds(270, 130, 72, 26); _btnPreview.Text = "预览删除"; _btnPreview.Click += (s, e) => OnPreviewClicked();
            _btnDelete.SetBounds(348, 130, 74, 26); _btnDelete.Text = "应用删除"; _btnDelete.Click += (s, e) => OnDeleteClicked();
            _btnExportClean.SetBounds(12, 164, 406, 28); _btnExportClean.Text = "导出清洗结果"; _btnExportClean.Click += (s, e) => OnExportCleanClicked();
            _grpDelete.Controls.AddRange(new Control[] { _chkDelEmptySn, _chkDelDupSn, _cmbDupKeep, _chkDelSnJunk, _txtSnJunk, _chkDelKw, _txtDelKw, _lblDelKwCol, _cmbDelKwCol, _lblSnCol, _cmbSnCol, _btnPreview, _btnDelete, _btnExportClean });
            _tip.SetToolTip(_lblSnCol, "SN 列即『序列号/编号』列。删除规则里的『空 SN』和『占位符(如 SN/SN1/NOSN)』都只检查这一列；筛选时也支持只在 SN 列范围内匹配。自动识别不准时可在此手动选择。");
            _tip.SetToolTip(_cmbSnCol, "选择哪一列作为序列号列。默认自动识别含 SN 的列；若没有表头或识别有误，请手动指定。");
            _tip.SetToolTip(_btnExportClean, "完整流程：基于汇总数据先按上面的删除规则筛选，再导出到导出文件夹——只导出合并后的总表（不再按原始文件分别导出）。无需先点『应用删除』。");
            _tip.SetToolTip(_btnPreview, "仅预览：高亮将被删除的行，并分规则统计『空SN / SN占位符 / 其它列含关键字』各会删多少行，不会真正删除。确认无误后再点『应用删除』。");
            _tip.SetToolTip(_cmbDelKwCol, "关键字的匹配范围：\n• 全部其它列 = 除 SN 列外的所有列（默认）\n• 具体列名 = 只在该列里匹配关键字，例如只查 StationID 列的 999\n选具体列可避免时间戳、编号等正常字段含 999 被误删。");
            _tip.SetToolTip(_chkDelDupSn, "SN 重复去重：不同行出现相同的 SN 时只保留一条，其余删除。使用上方『SN列(序列号)』选定的列作为判重依据；空 SN 不参与去重（由『空SN』规则处理）。");
            _tip.SetToolTip(_cmbDupKeep, "重复的 SN 保留哪一条：『保留首条』= 保留第一次出现的那行；『保留末条』= 保留最后一次出现的那行。");
            _tip.SetToolTip(_chkDelEmptySn, "SN 列为空的行视为无效记录，删除。");
            _tip.SetToolTip(_lblDelKwCol, "选择关键字在哪一列里匹配（逗号分隔的关键字）。默认『全部其它列』。");
            Controls.Add(_grpDelete);

            // ---- 筛选导出分组 ----
            _grpFilter.SetBounds(452, 344, 420, 168);
            _grpFilter.Text = "筛选并导出";
            _grpFilter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblFilterKw.SetBounds(12, 22, 120, 22); _lblFilterKw.Text = "关键字(逗号分隔):"; _lblFilterKw.TextAlign = ContentAlignment.MiddleLeft;
            _txtFilterKw.SetBounds(136, 20, 268, 23); _txtFilterKw.Text = "";
            _lblScope.SetBounds(12, 52, 64, 22); _lblScope.Text = "筛选列:"; _lblScope.TextAlign = ContentAlignment.MiddleLeft;
            _cmbScope.SetBounds(80, 50, 308, 23); _cmbScope.DropDownStyle = ComboBoxStyle.DropDownList;
            _lblMode.SetBounds(12, 80, 64, 22); _lblMode.Text = "方式:"; _lblMode.TextAlign = ContentAlignment.MiddleLeft;
            _cmbMode.SetBounds(80, 78, 140, 23); _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMode.Items.AddRange(new object[] { "包含", "等于", "开头", "正则" });
            _cmbMode.SelectedIndex = 0;
            _chkPerFile.SetBounds(240, 80, 160, 22); _chkPerFile.Text = "按原始文件分别导出"; _chkPerFile.Checked = true;
            _chkCombined.SetBounds(12, 106, 150, 22); _chkCombined.Text = "导出合并总表";
            _lblCmbFmt.SetBounds(170, 108, 44, 22); _lblCmbFmt.Text = "格式:"; _lblCmbFmt.TextAlign = ContentAlignment.MiddleLeft;
            _cmbCombinedFmt.SetBounds(214, 106, 80, 23); _cmbCombinedFmt.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCombinedFmt.Items.AddRange(new object[] { "CSV", "Excel(xlsx)" });
            _cmbCombinedFmt.SelectedIndex = 0;
            _btnExport.SetBounds(300, 104, 100, 30); _btnExport.Text = "筛选并导出"; _btnExport.Click += (s, e) => OnExportClicked();
            _tip.SetToolTip(_btnExport, "填写关键字=只导出匹配行；留空=导出当前全部数据（已删除后的剩余行）。");
            _tip.SetToolTip(_txtFilterKw, "多个关键字用英文逗号分隔，例如 苹果,香蕉。空 = 导出全部当前数据。");
            _tip.SetToolTip(_txtDelKw, "多个关键字用英文逗号分隔，例如 999,999.999。匹配范围由下方『关键字匹配列』决定：选『全部其它列』= 除 SN 外所有列；选具体列名 = 只在该列匹配。");
            _tip.SetToolTip(_txtSnJunk, "逗号分隔的占位符原文，例如 SN,SN1,NOSN,NA。整格精确等于其中之一（忽略大小写）才删除该行。请勿写成 SN 这样的片段或正则，否则会误删真实序列号。");
            _tip.SetToolTip(_cmbMode, "匹配方式：选『包含/等于/开头』时，关键字用英文逗号分隔多个值；选『正则』时整框作为单个正则表达式（不再按逗号拆分，例如 ^SN\\d+$）。");
            _tip.SetToolTip(_cmbScope, "筛选范围：\n• 任意列 = 整行任意单元格匹配\n• 具体列名 = 只在该列里匹配关键字（例如只查 StationID 列）\n列名在汇总后自动填充。");
            _grpFilter.Controls.AddRange(new Control[] { _lblFilterKw, _txtFilterKw, _lblScope, _cmbScope, _lblMode, _cmbMode, _chkPerFile, _chkCombined, _lblCmbFmt, _cmbCombinedFmt, _btnExport });
            Controls.Add(_grpFilter);

            // 进度 / 状态
            _pb.SetBounds(8, 566, 864, 18); _pb.Style = ProgressBarStyle.Continuous; _pb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_pb);
            _lblStatus.SetBounds(8, 588, 864, 18); _lblStatus.Text = "就绪"; _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_lblStatus);

            // 日志
            AddLabel("日志:", 8, 612);
            _rtbLog.SetBounds(8, 632, 864, Height - 632 - 12);
            _rtbLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _rtbLog.ReadOnly = true;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            Controls.Add(_rtbLog);
        }

        private Image LoadLogo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("DataAssistant.logo.png"))
                {
                    if (s == null) return null;
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    return Image.FromStream(ms);
                }
            }
            catch { return null; }
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label { Text = text, Left = x, Top = y + 3, AutoSize = true });
        }

        private void PickFolder(TextBox tb)
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择目录" })
            {
                if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.SelectedPath;
            }
        }

        // ====================== 汇总 ======================
        private void OnAggregateClicked()
        {
            if (_busy) { _cts?.Cancel(); return; }
            if (!Directory.Exists(_txtDataFolder.Text))
            {
                MessageBox.Show("请先选择有效的数据文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _busy = true;
            _btnAggregate.Enabled = false;
            _btnAggregate.Text = "取消";
            _cts = new CancellationTokenSource();
            _pb.Value = 0;
            Task.Run(() =>
            {
                try
                {
                    var agg = DataAggregatorCore.AggregateFolder(
                        _txtDataFolder.Text, _chkHeader.Checked, _chkSubdirs.Checked,
                        Log, (done, total) => SetProgress(done, total));
                    this.Invoke(new Action(() =>
                    {
                        _agg = agg;
                        BindGrid();
                        PopulateSnColumns();
                        SetStatus(string.Format("汇总完成：{0} 文件 / {1} 行", agg.FileCount, agg.RowCount));
                    }));
                }
                catch (OperationCanceledException) { Log("已取消。"); }
                catch (Exception ex) { Log("汇总异常: " + ex.Message); }
                finally { LeaveBusyAggregate(); }
            });
        }

        private void BindGrid()
        {
            _grid.DataSource = null;
            _grid.Columns.Clear();
            _grid.DataSource = _agg != null ? _agg.Table : null;
            // 让"源文件"列宽适中，不要过宽
            if (_grid.Columns["源文件"] != null)
            {
                _grid.Columns["源文件"].Width = 180;
                _grid.Columns["源文件"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
            }
        }

        private void PopulateSnColumns()
        {
            _cmbSnCol.Items.Clear();
            _cmbDelKwCol.Items.Clear();
            _cmbScope.Items.Clear();
            _cmbDelKwCol.Items.Add("全部其它列");   // 默认：匹配除 SN 外的所有列
            _cmbScope.Items.Add("任意列");          // 默认：整行任意单元格匹配
            if (_agg == null) return;
            foreach (DataColumn c in _agg.Table.Columns)
            {
                _cmbSnCol.Items.Add(c.ColumnName);
                _cmbDelKwCol.Items.Add(c.ColumnName);
                _cmbScope.Items.Add(c.ColumnName);
            }
            string sn = DataAggregatorCore.DetectSnColumn(_agg.Table);
            if (sn != null) _cmbSnCol.SelectedItem = sn;
            else if (_cmbSnCol.Items.Count > 0) _cmbSnCol.SelectedIndex = 0;
            _cmbDelKwCol.SelectedIndex = 0;
            _cmbScope.SelectedIndex = 0;

            // 套用上次保存的列选择（若有该列则选中，没有则保持自动识别的结果）
            if (!string.IsNullOrEmpty(_pendingSnCol))
                SelectCombo(_cmbSnCol, _pendingSnCol);
            if (!string.IsNullOrEmpty(_pendingDelKwCol))
                SelectCombo(_cmbDelKwCol, _pendingDelKwCol);
            if (!string.IsNullOrEmpty(_pendingFilterCol))
                SelectCombo(_cmbScope, _pendingFilterCol);
        }

        private void LeaveBusyAggregate()
        {
            if (InvokeRequired) { Invoke(new Action(LeaveBusyAggregate)); return; }
            _busy = false;
            _cts = null;
            _btnAggregate.Enabled = true;
            _btnAggregate.Text = "汇总";
        }

        private void OnClearClicked()
        {
            if (_busy) return;
            _agg = null;
            _grid.DataSource = null;
            _grid.Columns.Clear();
            _cmbSnCol.Items.Clear();
            _cmbDelKwCol.Items.Clear();
            _cmbScope.Items.Clear();
            _pb.Value = 0;
            SetStatus("已清空");
            Log("已清空汇总数据。");
        }

        // ====================== 删除 ======================
        private DataAggregatorCore.DeleteRules BuildRules()
        {
            return new DataAggregatorCore.DeleteRules
            {
                RemoveEmptySn = _chkDelEmptySn.Checked,
                RemoveSnJunk = _chkDelSnJunk.Checked,
                SnJunkPattern = _txtSnJunk.Text,
                RemoveByKeywordOtherColumns = _chkDelKw.Checked,
                Keywords = _txtDelKw.Text,
                KeywordColumn = GetKeywordColumn(),
                RemoveDuplicateSn = _chkDelDupSn.Checked,
                DuplicateKeepLast = (_cmbDupKeep.SelectedItem as string) == "保留末条",
                CaseSensitive = false
            };
        }

        /// <summary>取关键字匹配的目标列：null/空 = 全部其它列；否则为指定列名。</summary>
        private string GetKeywordColumn()
        {
            var sel = _cmbDelKwCol.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel) || sel == "全部其它列") return string.Empty;
            return sel;
        }

        private void ClearHighlights()
        {
            if (_grid == null) return;
            foreach (DataGridViewRow row in _grid.Rows)
                row.DefaultCellStyle.BackColor = Color.White;
        }

        private void OnPreviewClicked()
        {
            if (_busy) return;
            if (_agg == null || _agg.Table.Rows.Count == 0)
            {
                MessageBox.Show("请先汇总数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var rules = BuildRules();
            if (!rules.RemoveEmptySn && !rules.RemoveSnJunk && !rules.RemoveByKeywordOtherColumns && !rules.RemoveDuplicateSn)
            {
                MessageBox.Show("请至少勾选一条删除规则。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string snCol = _cmbSnCol.SelectedItem as string;
            var reasons = DataAggregatorCore.GetDeleteReasons(_agg, rules, snCol);
            int total = 0, nEmpty = 0, nJunk = 0, nKw = 0, nDup = 0;
            for (int i = 0; i < reasons.Length; i++)
            {
                var r = reasons[i];
                if (r == null) continue;
                total++;
                if (r == "空SN") nEmpty++;
                else if (r.StartsWith("SN占位符")) nJunk++;
                else if (r.StartsWith("SN重复")) nDup++;
                else nKw++;
                _grid.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(255, 200, 200);
            }
            int keep = reasons.Length - total;
            string msg = string.Format(
                "预览：将删除 {0} 行，保留 {1} 行。\n\n分规则统计：\n  • 空SN：{2} 行\n  • SN占位符：{3} 行\n  • 其它列含关键字：{4} 行\n  • SN重复：{5} 行\n\n红色高亮的行即将被删除。确认无误后点『应用删除』。",
                total, keep, nEmpty, nJunk, nKw, nDup);
            SetStatus(string.Format("预览：将删除 {0} 行（空SN {1} / 占位符 {2} / 关键字 {3} / SN重复 {4}），保留 {5} 行", total, nEmpty, nJunk, nKw, nDup, keep));
            Log(string.Format("预览删除：合计将删 {0} 行（空SN {1}、占位符 {2}、其它列关键字 {3}、SN重复 {4}），保留 {5} 行。", total, nEmpty, nJunk, nKw, nDup, keep));
            MessageBox.Show(msg, "删除预览", MessageBoxButtons.OK, total > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void OnDeleteClicked()
        {
            if (_busy) return;
            if (_agg == null || _agg.Table.Rows.Count == 0)
            {
                MessageBox.Show("请先汇总数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var rules = BuildRules();
            if (!rules.RemoveEmptySn && !rules.RemoveSnJunk && !rules.RemoveByKeywordOtherColumns && !rules.RemoveDuplicateSn)
            {
                MessageBox.Show("请至少勾选一条删除规则。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string snCol = _cmbSnCol.SelectedItem as string;
            try
            {
                ClearHighlights();
                int deleted = DataAggregatorCore.DeleteByRules(_agg, rules, snCol, Log);
                SetStatus(string.Format("删除 {0} 行，剩余 {1} 行", deleted, _agg.RowCount));
                MessageBox.Show(string.Format("已删除 {0} 行，剩余 {1} 行。", deleted, _agg.RowCount),
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("删除异常: " + ex.Message);
                MessageBox.Show("删除异常: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ====================== 导出清洗结果（删除后）======================
        private void OnExportCleanClicked()
        {
            if (_busy) return;
            if (_agg == null || _agg.Table.Rows.Count == 0)
            {
                MessageBox.Show("请先汇总数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_txtExportFolder.Text) || !Directory.Exists(_txtExportFolder.Text))
            {
                MessageBox.Show("请先选择有效的导出文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 关键：先基于汇总数据应用删除规则，再导出（无论是否先点过"应用删除"）。
            // 这样本按钮就是完整流程：汇总 → 删除规则筛选 → 导出。
            try
            {
                var rules = BuildRules();
                if (rules.RemoveEmptySn || rules.RemoveSnJunk || rules.RemoveByKeywordOtherColumns || rules.RemoveDuplicateSn)
                {
                    ClearHighlights();
                    string snCol = _cmbSnCol.SelectedItem as string;
                    int deleted = DataAggregatorCore.DeleteByRules(_agg, rules, snCol, Log);
                    if (deleted > 0)
                        Log(string.Format("导出前自动应用删除规则：删除 {0} 行，剩余 {1} 行。", deleted, _agg.RowCount));
                }
            }
            catch (Exception ex)
            {
                Log("删除规则应用异常: " + ex.Message);
                MessageBox.Show("删除规则应用异常: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // 清洗结果只导出「合并后」的总表，不再按原始文件分别导出。
            var opt = new DataAggregatorCore.ExportOptions
            {
                ExportFolder = _txtExportFolder.Text,
                Keywords = "",
                Scope = DataAggregatorCore.FilterScope.AnyColumn,
                Mode = DataAggregatorCore.MatchMode.Contains,
                CaseSensitive = false,
                PreservePerFile = false,
                Combined = true,
                CombinedFormat = (_cmbCombinedFmt.SelectedItem as string) ?? "CSV",
                SnColumnName = _cmbSnCol.SelectedItem as string
            };
            try
            {
                DataAggregatorCore.Export(_agg, opt, Log);
                SetStatus("已导出清洗结果(合并总表)");
                MessageBox.Show("已导出清洗结果（合并总表，删除后的剩余行）。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("导出异常: " + ex.Message);
                MessageBox.Show("导出异常: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ====================== 筛选导出 ======================
        private void OnExportClicked()
        {
            if (_busy) return;
            if (_agg == null || _agg.Table.Rows.Count == 0)
            {
                MessageBox.Show("请先汇总数据。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(_txtExportFolder.Text) || !Directory.Exists(_txtExportFolder.Text))
            {
                MessageBox.Show("请先选择有效的导出文件夹。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // 关键字为空时表示导出当前全部数据（已删除后的剩余行）。

            var opt = new DataAggregatorCore.ExportOptions
            {
                ExportFolder = _txtExportFolder.Text,
                Keywords = _txtFilterKw.Text,
                Scope = GetFilterScope(),
                ColumnName = GetFilterColumn(),
                Mode = ModeFromCombo(_cmbMode.SelectedItem as string),
                CaseSensitive = false,
                PreservePerFile = _chkPerFile.Checked,
                Combined = _chkCombined.Checked,
                CombinedFormat = _cmbCombinedFmt.SelectedItem as string,
                SnColumnName = _cmbSnCol.SelectedItem as string
            };

            try
            {
                DataAggregatorCore.Export(_agg, opt, Log);
                SetStatus("导出完成");
            }
            catch (Exception ex)
            {
                Log("导出异常: " + ex.Message);
                MessageBox.Show("导出异常: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>筛选范围：选『任意列』= 整行任意单元格匹配；选具体列名 = 只在该列匹配。</summary>
        private DataAggregatorCore.FilterScope GetFilterScope()
        {
            var sel = _cmbScope.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel) || sel == "任意列") return DataAggregatorCore.FilterScope.AnyColumn;
            return DataAggregatorCore.FilterScope.Column;
        }

        private string GetFilterColumn()
        {
            var sel = _cmbScope.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel) || sel == "任意列") return string.Empty;
            return sel;
        }

        private static DataAggregatorCore.MatchMode ModeFromCombo(string s)
        {
            if (s == "等于") return DataAggregatorCore.MatchMode.Equals;
            if (s == "开头") return DataAggregatorCore.MatchMode.StartsWith;
            if (s == "正则") return DataAggregatorCore.MatchMode.Regex;
            return DataAggregatorCore.MatchMode.Contains;
        }

        // ====================== 界面设置持久化 ======================

        public string SettingsSection { get { return "DataAggregator"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;
            store.SetBool(s, "Subdirs", _chkSubdirs.Checked);
            store.SetBool(s, "Header", _chkHeader.Checked);

            store.SetBool(s, "DelEmptySn", _chkDelEmptySn.Checked);
            store.SetBool(s, "DelDupSn", _chkDelDupSn.Checked);
            store.Set(s, "DupKeep", _cmbDupKeep.SelectedItem as string);
            store.SetBool(s, "DelSnJunk", _chkDelSnJunk.Checked);
            store.Set(s, "SnJunk", _txtSnJunk.Text);
            store.SetBool(s, "DelKw", _chkDelKw.Checked);
            store.Set(s, "DelKwText", _txtDelKw.Text);
            store.Set(s, "DelKwCol", _cmbDelKwCol.SelectedItem as string);
            store.Set(s, "SnCol", _cmbSnCol.SelectedItem as string);

            store.Set(s, "FilterKw", _txtFilterKw.Text);
            store.Set(s, "FilterCol", _cmbScope.SelectedItem as string);
            store.Set(s, "FilterMode", _cmbMode.SelectedItem as string);
            store.SetBool(s, "PerFile", _chkPerFile.Checked);
            store.SetBool(s, "Combined", _chkCombined.Checked);
            store.Set(s, "CombinedFmt", _cmbCombinedFmt.SelectedItem as string);
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;

            _chkSubdirs.Checked = store.GetBool(s, "Subdirs", _chkSubdirs.Checked);
            _chkHeader.Checked = store.GetBool(s, "Header", _chkHeader.Checked);

            _chkDelEmptySn.Checked = store.GetBool(s, "DelEmptySn", _chkDelEmptySn.Checked);
            _chkDelDupSn.Checked = store.GetBool(s, "DelDupSn", _chkDelDupSn.Checked);
            _chkDelSnJunk.Checked = store.GetBool(s, "DelSnJunk", _chkDelSnJunk.Checked);
            _chkDelKw.Checked = store.GetBool(s, "DelKw", _chkDelKw.Checked);

            _txtSnJunk.Text = store.Get(s, "SnJunk", _txtSnJunk.Text);
            _txtDelKw.Text = store.Get(s, "DelKwText", _txtDelKw.Text);
            _txtFilterKw.Text = store.Get(s, "FilterKw", _txtFilterKw.Text);

            _chkPerFile.Checked = store.GetBool(s, "PerFile", _chkPerFile.Checked);
            _chkCombined.Checked = store.GetBool(s, "Combined", _chkCombined.Checked);

            SelectCombo(_cmbDupKeep, store.Get(s, "DupKeep", _cmbDupKeep.SelectedItem as string));
            SelectCombo(_cmbMode, store.Get(s, "FilterMode", _cmbMode.SelectedItem as string));
            SelectCombo(_cmbCombinedFmt, store.Get(s, "CombinedFmt", _cmbCombinedFmt.SelectedItem as string));

            // 列名相关的下拉框要等汇总后才有选项，先记下来
            _pendingSnCol = store.Get(s, "SnCol", null);
            _pendingDelKwCol = store.Get(s, "DelKwCol", null);
            _pendingFilterCol = store.Get(s, "FilterCol", null);
        }

        /// <summary>按文本选中下拉项；找不到就保持原选中项（不抛异常）。</summary>
        private static void SelectCombo(ComboBox cmb, string text)
        {
            if (cmb == null || string.IsNullOrEmpty(text)) return;
            int i = cmb.Items.IndexOf(text);
            if (i >= 0) cmb.SelectedIndex = i;
        }

        // ====================== 线程安全更新 ======================
        private void Log(string msg)
        {
            if (_rtbLog.InvokeRequired)
                _rtbLog.Invoke(new Action<string>(Log), msg);
            else
            {
                _rtbLog.AppendText(msg + Environment.NewLine);
                _rtbLog.ScrollToCaret();
            }
        }

        private void SetStatus(string text)
        {
            if (_lblStatus.InvokeRequired) _lblStatus.Invoke(new Action<string>(SetStatus), text);
            else _lblStatus.Text = text;
        }

        private void SetProgress(int value, int maximum)
        {
            if (_pb.InvokeRequired) _pb.Invoke(new Action<int, int>(SetProgress), value, maximum);
            else { _pb.Maximum = Math.Max(maximum, 1); _pb.Value = Math.Min(value, _pb.Maximum); }
        }
    }
}
