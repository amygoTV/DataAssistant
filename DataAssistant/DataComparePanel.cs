using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataAssistant
{
    /// <summary>
    /// 数据比对控件（可复用 UserControl）。
    /// 以表格A 的 SN 顺序为主轴，按 SN 从表格B 取<b>多列</b>值逐行比对，并导出比对结果。
    /// 直接拖到主窗体（Dock = Fill）即可。
    ///
    /// 排版：顶部左右分栏 —— 左边「表格A」、右边「表格B」，各自独立选文件 / 选 SN 列 / 勾选要比对的列；
    /// 下方一条「比对方式与导出」；中部为结果表（一致/不一致/缺失 配色，可只看差异行）；底部为操作行与日志。
    /// </summary>
    public class DataComparePanel : UserControl, ISettingsAware
    {
        // ---- 表格A（左） ----
        private readonly TextBox _txtFileA = new TextBox();
        private readonly Button _btnPickA = new Button();
        private readonly ComboBox _cmbSnA = new ComboBox();
        private readonly CheckedListBox _clbColsA = new CheckedListBox();

        // ---- 表格B（右） ----
        private readonly TextBox _txtFileB = new TextBox();
        private readonly Button _btnPickB = new Button();
        private readonly ComboBox _cmbSnB = new ComboBox();
        private readonly CheckedListBox _clbColsB = new CheckedListBox();

        // ---- 公共 ----
        private readonly CheckBox _chkHeader = new CheckBox();
        private readonly ComboBox _cmbPairMode = new ComboBox();
        private readonly ComboBox _cmbMode = new ComboBox();
        private readonly CheckBox _chkIgnoreCase = new CheckBox();
        private readonly CheckBox _chkExtraB = new CheckBox();
        private readonly CheckBox _chkIncludeUnmatched = new CheckBox();
        private readonly TextBox _txtTolerance = new TextBox();
        private readonly Label _lblPairPreview = new Label();

        private readonly TextBox _txtExportDir = new TextBox();
        private readonly Button _btnPickExport = new Button();
        private readonly ComboBox _cmbFormat = new ComboBox();

        // ---- 中部：结果 ----
        private readonly DataGridView _grid = new DataGridView();
        private readonly CheckBox _chkOnlyDiff = new CheckBox();

        // ---- 底部 ----
        private readonly Button _btnCompare = new Button();
        private readonly Button _btnExport = new Button();
        private readonly Button _btnSwap = new Button();
        private readonly ProgressBar _pb = new ProgressBar();
        private readonly Label _lblStatus = new Label();
        private readonly RichTextBox _rtbLog = new RichTextBox();
        private readonly ToolTip _tip = new ToolTip();

        private DataTable _tableA, _tableB;
        private DataTable _resultTable;
        private DataView _resultView;
        private DataCompareCore.CompareResultSet _result;
        private readonly List<string> _orderA = new List<string>();
        private readonly List<string> _orderB = new List<string>();
        private bool _busy;

        // 待恢复的表相关选择（SN列、勾选的比对列）—— 依赖表格加载后才能套用
        private string _pendingSnA;
        private string _pendingSnB;
        private List<string> _pendingColsA;
        private List<string> _pendingColsB;

        public DataComparePanel()
        {
            InitializeComponent();
            _chkHeader.Checked = true;
            _cmbPairMode.Items.AddRange(new object[] { "按列名配对", "按勾选顺序配对" });
            _cmbPairMode.SelectedIndex = 0;
            _cmbPairMode.SelectedIndexChanged += (s, e) => UpdatePairPreview();
            _cmbMode.Items.AddRange(new object[] { "文本精确", "数值比较", "包含比较" });
            _cmbMode.SelectedIndex = 0;
            _chkIgnoreCase.Checked = true;
            _chkExtraB.Checked = true;
            _txtTolerance.Text = "0.000001";
            _cmbFormat.Items.AddRange(new object[] { "CSV", "Excel(xlsx)" });
            _cmbFormat.SelectedIndex = 0;
            _txtExportDir.Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            _tip.SetToolTip(_chkIncludeUnmatched,
                "默认不勾：未匹配的行（只在表A有、或只在表B有）不写进结果表与导出文件，但会在统计与日志里报出数量，避免悄悄丢数据。\r\n" +
                "勾上后这些行也会输出，结果列标为『B表缺失』或『A表缺失』。");
            _tip.SetToolTip(_chkExtraB,
                "是否把表B中多出来（表A没有）的SN也纳入比对统计。\r\n" +
                "配合『输出未匹配行』决定它们最终是否出现在结果里。");
            _tip.SetToolTip(_cmbPairMode,
                "按列名配对：两侧勾选的列按同名对应（取交集），适合两表列名相同。\r\n" +
                "按勾选顺序配对：两侧勾选的列按勾选先后顺序一一对应，适合两表列名不同（如 A的判定 ↔ B的Result）。");
            _tip.SetToolTip(_clbColsA, "勾选表A中要比对的列（可多选）。");
            _tip.SetToolTip(_clbColsB, "勾选表B中要比对的列（可多选）。");
        }

        // ====================== 布局 ======================

        private void InitializeComponent()
        {
            Size = new System.Drawing.Size(900, 700);
            Font = new Font("Microsoft YaHei", 9.5f);
            AutoScroll = true;

            // ================= 顶部：左右分栏 + 方式导出 =================
            var top = new Panel { Dock = DockStyle.Top, Height = 368, Padding = new Padding(4) };

            var tlpTop = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0)
            };
            tlpTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tlpTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 240f));  // 左右分栏
            tlpTop.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));  // 方式与导出
            top.Controls.Add(tlpTop);

            // ---------- 左右分栏 ----------
            var tlpSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0)
            };
            tlpSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            tlpSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            tlpSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlpSplit.Controls.Add(BuildTableGroup(true), 0, 0);
            tlpSplit.Controls.Add(BuildTableGroup(false), 1, 0);
            tlpTop.Controls.Add(tlpSplit, 0, 0);

            // ---------- 比对方式与导出 ----------
            var gb3 = new GroupBox { Text = "③ 比对方式与导出", Dock = DockStyle.Fill };
            var tlp3 = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(8, 4, 6, 4),
                Margin = new Padding(0)
            };
            tlp3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78f));
            tlp3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tlp3.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            tlp3.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            tlp3.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));

            tlp3.Controls.Add(NewLabel("配对方式"), 0, 0);
            // 一行内控件用自动宽度排布，任何窗口宽度下都不会互相压到一起
            var pPair = MakeRow(120, -1, 100, -1, -1, 76, -1);
            _cmbPairMode.Dock = DockStyle.Fill; _cmbPairMode.Margin = new Padding(0, 2, 4, 2);
            _cmbPairMode.DropDownStyle = ComboBoxStyle.DropDownList;
            pPair.Controls.Add(_cmbPairMode, 0, 0);
            pPair.Controls.Add(MakeLabel("比对值"), 1, 0);
            _cmbMode.Dock = DockStyle.Fill; _cmbMode.Margin = new Padding(2, 2, 8, 2);
            _cmbMode.DropDownStyle = ComboBoxStyle.DropDownList;
            pPair.Controls.Add(_cmbMode, 2, 0);
            _chkIgnoreCase.Dock = DockStyle.Fill; _chkIgnoreCase.Margin = new Padding(0, 2, 8, 2);
            _chkIgnoreCase.Text = "忽略大小写";
            pPair.Controls.Add(_chkIgnoreCase, 3, 0);
            pPair.Controls.Add(MakeLabel("容差"), 4, 0);
            _txtTolerance.Dock = DockStyle.Fill; _txtTolerance.Margin = new Padding(2, 2, 8, 2);
            pPair.Controls.Add(_txtTolerance, 5, 0);
            _chkExtraB.Dock = DockStyle.Fill; _chkExtraB.Margin = new Padding(0, 2, 0, 2);
            _chkExtraB.Text = "包含表B多出的SN";
            pPair.Controls.Add(_chkExtraB, 6, 0);
            tlp3.Controls.Add(pPair, 1, 0);

            tlp3.Controls.Add(NewLabel("导出设置"), 0, 1);
            var pE = MakeRow(-1, -1, 0, 76, -1, 100);
            _chkIncludeUnmatched.Dock = DockStyle.Fill; _chkIncludeUnmatched.Margin = new Padding(0, 2, 8, 2);
            _chkIncludeUnmatched.Text = "输出未匹配行";
            _chkIncludeUnmatched.Checked = false;
            pE.Controls.Add(_chkIncludeUnmatched, 0, 0);
            _chkHeader.Dock = DockStyle.Fill; _chkHeader.Margin = new Padding(0, 2, 8, 2);
            _chkHeader.Text = "首行是表头";
            _chkHeader.CheckedChanged += (s, e) => { ReloadA(); ReloadB(); };
            pE.Controls.Add(_chkHeader, 1, 0);
            _txtExportDir.Dock = DockStyle.Fill; _txtExportDir.Margin = new Padding(0, 2, 4, 2);
            pE.Controls.Add(_txtExportDir, 2, 0);
            _btnPickExport.Dock = DockStyle.Fill; _btnPickExport.Margin = new Padding(0, 1, 8, 1);
            _btnPickExport.Text = "选择…";
            _btnPickExport.Click += (s, e) => PickExportFolder();
            pE.Controls.Add(_btnPickExport, 3, 0);
            pE.Controls.Add(MakeLabel("格式"), 4, 0);
            _cmbFormat.Dock = DockStyle.Fill; _cmbFormat.Margin = new Padding(2, 2, 0, 2);
            _cmbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            pE.Controls.Add(_cmbFormat, 5, 0);
            tlp3.Controls.Add(pE, 1, 1);

            _lblPairPreview.Dock = DockStyle.Fill;
            _lblPairPreview.Margin = new Padding(6, 2, 0, 0);
            _lblPairPreview.ForeColor = Color.DimGray;
            _lblPairPreview.TextAlign = ContentAlignment.MiddleLeft;
            _lblPairPreview.Text = "配对预览：勾选两侧要比较的列后显示配对关系。";
            tlp3.Controls.Add(_lblPairPreview, 0, 2);
            tlp3.SetColumnSpan(tlp3.GetControlFromPosition(0, 2), 2);
            gb3.Controls.Add(tlp3);
            tlpTop.Controls.Add(gb3, 0, 1);

            // ================= 底部：操作行 + 日志 =================
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 128, Padding = new Padding(4) };

            // 按钮行也用自适应表格：窄窗口时状态标签自动收缩，不再溢出
            var btnRow = MakeRow(112, 122, 110, -1, 110, 0);
            btnRow.Dock = DockStyle.Top;
            btnRow.Height = 32;
            _btnCompare.Dock = DockStyle.Fill; _btnCompare.Margin = new Padding(0, 2, 4, 2);
            _btnCompare.Text = "开始比对";
            _btnCompare.BackColor = Color.LightGreen;
            _btnCompare.Click += (s, e) =>
            {
                if (_busy) return;
                try { Task.Run(() => DoCompare()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            btnRow.Controls.Add(_btnCompare, 0, 0);

            _btnExport.Dock = DockStyle.Fill; _btnExport.Margin = new Padding(0, 2, 4, 2);
            _btnExport.Text = "导出比对结果";
            _btnExport.Click += (s, e) =>
            {
                if (_busy) return;
                try { Task.Run(() => DoExport()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            btnRow.Controls.Add(_btnExport, 1, 0);

            _btnSwap.Dock = DockStyle.Fill; _btnSwap.Margin = new Padding(0, 2, 4, 2);
            _btnSwap.Text = "A / B 互换";
            _btnSwap.Click += (s, e) =>
            {
                string t = _txtFileA.Text; _txtFileA.Text = _txtFileB.Text; _txtFileB.Text = t;
                ReloadA(); ReloadB();
            };
            btnRow.Controls.Add(_btnSwap, 2, 0);

            _chkOnlyDiff.Dock = DockStyle.Fill; _chkOnlyDiff.Margin = new Padding(0, 2, 8, 2);
            _chkOnlyDiff.Text = "只看差异行";
            _chkOnlyDiff.CheckedChanged += (s, e) => ApplyFilter();
            btnRow.Controls.Add(_chkOnlyDiff, 3, 0);

            _pb.Dock = DockStyle.Fill; _pb.Margin = new Padding(0, 5, 8, 5);
            _pb.Style = ProgressBarStyle.Continuous;
            btnRow.Controls.Add(_pb, 4, 0);

            _lblStatus.Dock = DockStyle.Fill; _lblStatus.Margin = new Padding(0, 0, 4, 0);
            _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            _lblStatus.Text = "就绪";
            _lblStatus.AutoEllipsis = true;
            btnRow.Controls.Add(_lblStatus, 5, 0);

            // 注意：日志框必须先加入（Fill），按钮行后加入（Top）。
            // 反过来的话日志框会先占满整个底部，把按钮行盖住。
            _rtbLog.Dock = DockStyle.Fill;
            _rtbLog.Margin = new Padding(0, 4, 0, 0);
            _rtbLog.ReadOnly = true;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            bottom.Controls.Add(_rtbLog);
            bottom.Controls.Add(btnRow);

            // ================= 中部：结果表 =================
            // 注意：不要给 _grid 再设 Anchor —— Anchor 赋值会把 Dock 重置为 None，
            // 结果表格就会缩成默认尺寸停在左上角（画成一个灰框盖住表格A）。
            _grid.Dock = DockStyle.Fill;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.EnableHeadersVisualStyles = false;
            _grid.CellFormatting += OnGridCellFormatting;

            Controls.Add(_grid);
            Controls.Add(bottom);
            Controls.Add(top);
        }

        /// <summary>构建左（表格A）或右（表格B）的一组控件。</summary>
        private GroupBox BuildTableGroup(bool isA)
        {
            var gb = new GroupBox
            {
                Text = isA ? "① 表格A（基准表 · 决定输出顺序）" : "② 表格B（被比对表）",
                Dock = DockStyle.Fill,
                Margin = isA ? new Padding(0, 0, 4, 0) : new Padding(4, 0, 0, 0)
            };

            var tlp = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(8, 4, 6, 4),
                Margin = new Padding(0)
            };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));

            var txt = isA ? _txtFileA : _txtFileB;
            var btn = isA ? _btnPickA : _btnPickB;
            var cmbSn = isA ? _cmbSnA : _cmbSnB;
            var clb = isA ? _clbColsA : _clbColsB;
            var order = isA ? _orderA : _orderB;

            tlp.Controls.Add(NewLabel("文件"), 0, 0);
            var pFile = MakeRow(0, 76);          // 文本框自动占满剩余宽度，按钮固定宽
            txt.Dock = DockStyle.Fill; txt.Margin = new Padding(0, 2, 4, 2); txt.ReadOnly = true;
            btn.Dock = DockStyle.Fill; btn.Margin = new Padding(0, 1, 0, 1); btn.Text = "选择…";
            btn.Click += (s, e) => PickFile(txt, isA);
            pFile.Controls.Add(txt, 0, 0);
            pFile.Controls.Add(btn, 1, 0);
            tlp.Controls.Add(pFile, 1, 0);

            tlp.Controls.Add(NewLabel("SN列"), 0, 1);
            cmbSn.Dock = DockStyle.Fill; cmbSn.Margin = new Padding(0, 2, 0, 2);
            cmbSn.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSn.SelectedIndexChanged += (s, e) => { if (isA) OnSnChangedA(); else OnSnChangedB(); };
            tlp.Controls.Add(cmbSn, 1, 1);

            var lblCols = NewLabel("比对列");
            lblCols.Anchor = AnchorStyles.Top | AnchorStyles.Left;   // 与列表顶端对齐，而不是垂直居中
            tlp.Controls.Add(lblCols, 0, 2);
            clb.Dock = DockStyle.Fill;
            clb.Margin = new Padding(0, 2, 0, 2);
            clb.CheckOnClick = true;
            clb.IntegralHeight = false;
            clb.BorderStyle = BorderStyle.FixedSingle;
            clb.ItemCheck += (s, e) =>
            {
                string name = clb.Items[e.Index] as string;
                if (name == null) return;
                if (e.NewValue == CheckState.Checked) { if (!order.Contains(name)) order.Add(name); }
                else order.Remove(name);
                UpdatePairPreview();
            };
            tlp.Controls.Add(clb, 1, 2);

            var hint = new Label
            {
                Text = "勾选要比对的列（可多选）",
                Dock = DockStyle.Fill,
                ForeColor = Color.DimGray,
                TextAlign = ContentAlignment.MiddleLeft
            };
            tlp.Controls.Add(hint, 0, 3);
            tlp.SetColumnSpan(tlp.GetControlFromPosition(0, 3), 2);

            gb.Controls.Add(tlp);
            return gb;
        }

        private static Label NewLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        }

        /// <summary>
        /// 创建一行横向排布的表格布局。
        /// widths：0 = 占满剩余宽度；-1 = 按内容自适应；正数 = 固定宽度(px)。
        /// 用它替代绝对坐标，窗口变窄时控件自动收缩/换位，不会互相重叠。
        /// </summary>
        private static TableLayoutPanel MakeRow(params int[] widths)
        {
            var t = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = widths.Length,
                RowCount = 1,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            foreach (int w in widths)
            {
                if (w == 0) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                else if (w < 0) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                else t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, w));
            }
            return t;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            return new Label { Text = text, Location = new Point(x, y), AutoSize = true };
        }

        /// <summary>自适应宽度的行内标签（放在 TableLayoutPanel 的 AutoSize 列里）。</summary>
        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        }

        // ====================== 文件与列 ======================

        private void PickFile(TextBox tb, bool isA)
        {
            try
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = isA ? "选择表格A（基准）" : "选择表格B（被比对）",
                    Filter = "表格文件(*.csv;*.txt;*.xls;*.xlsx)|*.csv;*.txt;*.xls;*.xlsx|所有文件(*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        tb.Text = dlg.FileName;
                        if (isA) ReloadA(); else ReloadB();
                    }
                }
            }
            catch (Exception ex) { Log("选择文件失败: " + ex.Message); }
        }

        private void PickExportFolder()        {
            try
            {
                using (var dlg = new FolderBrowserDialog { Description = "选择比对结果的导出文件夹" })
                {
                    if (Directory.Exists(_txtExportDir.Text)) dlg.SelectedPath = _txtExportDir.Text;
                    if (dlg.ShowDialog() == DialogResult.OK) _txtExportDir.Text = dlg.SelectedPath;
                }
            }
            catch (Exception ex) { Log("选择文件夹失败: " + ex.Message); }
        }

        private void ReloadA()
        {
            _tableA = null;
            _cmbSnA.Items.Clear();
            _clbColsA.Items.Clear();
            _orderA.Clear();
            if (string.IsNullOrWhiteSpace(_txtFileA.Text) || !File.Exists(_txtFileA.Text)) { UpdatePairPreview(); return; }
            try
            {
                _tableA = DataAggregatorCore.LoadTable(_txtFileA.Text, _chkHeader.Checked, Log);
                foreach (DataColumn c in _tableA.Columns) { _cmbSnA.Items.Add(c.ColumnName); _clbColsA.Items.Add(c.ColumnName); }
                if (_cmbSnA.Items.Count > 0) _cmbSnA.SelectedIndex = 0;
                OnSnChangedA();
                // 套用上次保存的 SN 列与勾选列
                ApplyPendingCols(_cmbSnA, _clbColsA, _orderA, _pendingSnA, _pendingColsA);
            }
            catch (Exception ex) { Log("读取表格A失败: " + ex.Message); }
            UpdatePairPreview();
        }

        private void ReloadB()
        {
            _tableB = null;
            _cmbSnB.Items.Clear();
            _clbColsB.Items.Clear();
            _orderB.Clear();
            if (string.IsNullOrWhiteSpace(_txtFileB.Text) || !File.Exists(_txtFileB.Text)) { UpdatePairPreview(); return; }
            try
            {
                _tableB = DataAggregatorCore.LoadTable(_txtFileB.Text, _chkHeader.Checked, Log);
                foreach (DataColumn c in _tableB.Columns) { _cmbSnB.Items.Add(c.ColumnName); _clbColsB.Items.Add(c.ColumnName); }
                if (_cmbSnB.Items.Count > 0) _cmbSnB.SelectedIndex = 0;
                OnSnChangedB();
                // 套用上次保存的 SN 列与勾选列
                ApplyPendingCols(_cmbSnB, _clbColsB, _orderB, _pendingSnB, _pendingColsB);
            }
            catch (Exception ex) { Log("读取表格B失败: " + ex.Message); }
            UpdatePairPreview();
        }

        /// <summary>SN 列变化时：重新勾选比对列（默认勾选除 SN 外的所有列）。</summary>
        private void OnSnChangedA()
        {
            CheckAllExceptSn(_clbColsA, _orderA, _cmbSnA.SelectedItem as string);
            UpdatePairPreview();
        }

        private void OnSnChangedB()
        {
            CheckAllExceptSn(_clbColsB, _orderB, _cmbSnB.SelectedItem as string);
            UpdatePairPreview();
        }

        private static void CheckAllExceptSn(CheckedListBox clb, List<string> order, string snCol)
        {
            order.Clear();
            for (int i = 0; i < clb.Items.Count; i++)
            {
                string name = clb.Items[i] as string;
                bool on = !string.Equals(name, snCol, StringComparison.OrdinalIgnoreCase);
                if (clb.GetItemChecked(i) != on) clb.SetItemChecked(i, on);
                if (on) order.Add(name);
            }
        }

        /// <summary>取勾选的列（按勾选先后顺序）。</summary>
        private static List<string> GetCheckedCols(CheckedListBox clb, List<string> order)
        {
            var result = new List<string>();
            foreach (string n in order)
                if (clb.Items.Contains(n) && !result.Contains(n)) result.Add(n);
            for (int i = 0; i < clb.Items.Count; i++)
            {
                if (!clb.GetItemChecked(i)) continue;
                string n = clb.Items[i] as string;
                if (n != null && !result.Contains(n)) result.Add(n);
            }
            return result;
        }

        /// <summary>更新配对关系预览。</summary>
        private void UpdatePairPreview()
        {
            try
            {
                if (_clbColsA.Items.Count == 0 && _clbColsB.Items.Count == 0)
                {
                    _lblPairPreview.Text = "配对预览：勾选两侧要比较的列后显示配对关系。";
                    return;
                }
                var opt = BuildOptions();
                var pairs = DataCompareCore.BuildPairs(opt, null);
                if (pairs.Count == 0)
                {
                    _lblPairPreview.Text = "配对预览：当前没有可配对的列（同名配对需两侧列名一致；顺序配对需两侧都勾选）。";
                    return;
                }
                _lblPairPreview.Text = "配对预览（" + pairs.Count + " 组）："
                    + string.Join("　", pairs.Select(p => p.ColA + " ↔ " + p.ColB).ToArray());
            }
            catch (Exception) { /* 预览失败不影响主流程 */ }
        }

        // ====================== 执行 ======================

        private DataCompareCore.CompareOptions BuildOptions()
        {
            double tol;
            if (!double.TryParse(_txtTolerance.Text, out tol)) tol = 0.000001;
            return new DataCompareCore.CompareOptions
            {
                FileA = _txtFileA.Text,
                FileB = _txtFileB.Text,
                HasHeader = _chkHeader.Checked,
                SnColA = _cmbSnA.SelectedItem as string,
                SnColB = _cmbSnB.SelectedItem as string,
                ValColsA = GetCheckedCols(_clbColsA, _orderA),
                ValColsB = GetCheckedCols(_clbColsB, _orderB),
                PairMode = (DataCompareCore.PairMode)_cmbPairMode.SelectedIndex,
                Mode = (DataCompareCore.CompareMode)_cmbMode.SelectedIndex,
                IgnoreCase = _chkIgnoreCase.Checked,
                TrimSpace = true,
                IncludeExtraInB = _chkExtraB.Checked,
                IncludeUnmatched = _chkIncludeUnmatched.Checked,
                Tolerance = tol
            };
        }

        private void DoCompare()
        {
            try
            {
                if (_busy) return;
                if (_tableA == null || _tableB == null)
                {
                    SetStatus("请先选择表格A和表格B");
                    Log("请先选择表格A和表格B。");
                    return;
                }
                _busy = true;
                try
                {
                    _result = DataCompareCore.Compare(BuildOptions(), Log);
                    _resultTable = _result.ToTable();
                    _resultView = new DataView(_resultTable);
                    ApplyFilter();
                    TuneColumns();
                    SetStatus(_result.Summary);
                    Log("  比对完成，输出 " + _result.Rows.Count + " 行。");
                    LogDiffDetails();
                    // 定位差异行是锦上添花，失败不能影响比对结果
                    try { ScrollToFirstDiff(); } catch (Exception) { }
                }
                catch (Exception ex)
                {
                    Log("比对失败: " + ex.Message);
                    SetStatus("比对失败");
                }
                finally
                {
                    _busy = false;
                }
            }
            catch (Exception ex) { Log("异常: " + ex.Message); }
        }

        /// <summary>把差异明细写进日志（表格里只留 SN 与勾选列，差异说明放日志）。</summary>
        private void LogDiffDetails()
        {
            if (_result == null) return;
            int shown = 0, total = 0;
            foreach (var r in _result.Rows)
            {
                if (r.Status != DataCompareCore.RowStatus.Different) continue;
                total++;
                if (shown++ >= 20) continue;
                Log("  差异 " + r.Sn + "：" + r.Note);
            }
            if (total > 20) Log("  …… 共 " + total + " 行不一致，仅列出前 20 行；勾选『只看差异行』可在表中用颜色定位。");
            else if (total > 0) Log("  共 " + total + " 行不一致。");
        }

        /// <summary>绑定结果表并按「只看差异」过滤。</summary>
        private void ApplyFilter()
        {
            if (_resultTable == null) return;
            if (_resultView == null) _resultView = new DataView(_resultTable);
            try
            {
                _resultView.RowFilter = _chkOnlyDiff.Checked ? "结果 <> '一致'" : "";
            }
            catch (Exception)
            {
                _resultView.RowFilter = "";
            }
            InvokeOnUi(() =>
            {
                _grid.DataSource = null;
                _grid.Columns.Clear();
                _grid.DataSource = _resultView;
                TuneColumns();
            });
        }

        /// <summary>
        /// 结果表列宽与显示：只显示 SN 与各组 A/B 值列；
        /// 「序号 / 结果 / 说明」三列仅内部保留（供“只看差异行”过滤与行配色使用），对用户隐藏。
        /// </summary>
        private void TuneColumns()
        {
            if (_grid.Columns.Count < 2) return;

            // 内部辅助列：隐藏但保留（过滤/配色依赖它们）
            foreach (string hidden in new[] { "序号", "结果", "说明" })
            {
                if (_grid.Columns[hidden] != null) _grid.Columns[hidden].Visible = false;
            }

            if (_grid.Columns["SN"] != null)
            {
                _grid.Columns["SN"].AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                _grid.Columns["SN"].Width = 150;
            }
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                if (!col.Visible) continue;
                if (col.Name == "SN") continue;
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                col.Width = 130;
            }
        }

        /// <summary>滚动到第一行差异，便于直接看问题。</summary>
        private void ScrollToFirstDiff()
        {
            try
            {
                if (_grid.Rows.Count == 0) return;
                // 注意：必须定位到"可见"列（SN），序号/结果/说明已隐藏，指向隐藏列会抛异常
                int colIndex = _grid.Columns["SN"] != null ? _grid.Columns["SN"].Index : 0;
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    string v = _grid.Rows[i].Cells["结果"].Value as string;
                    if (v == "不一致" || v == "B表缺失" || v == "A表缺失")
                    {
                        _grid.FirstDisplayedScrollingRowIndex = i;
                        var cell = _grid.Rows[i].Cells[colIndex];
                        if (cell.Visible) _grid.CurrentCell = cell;
                        return;
                    }
                }
            }
            catch (Exception) { /* 定位失败不影响比对结果 */ }
        }

        /// <summary>结果行配色：一致=浅绿，不一致=浅红，缺失=浅橙。</summary>
        private void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || _grid.Rows.Count == 0) return;
            string status = null;
            try { status = _grid.Rows[e.RowIndex].Cells["结果"].Value as string; }
            catch (Exception) { return; }
            if (string.IsNullOrEmpty(status)) return;

            e.CellStyle.BackColor =
                status == "一致" ? Color.FromArgb(226, 245, 226) :
                status == "不一致" ? Color.FromArgb(255, 214, 214) :
                Color.FromArgb(255, 236, 200);
            e.CellStyle.ForeColor =
                status == "不一致" ? Color.FromArgb(150, 0, 0) : Color.Black;
        }

        private void DoExport()
        {
            try
            {
                if (_result == null)
                {
                    SetStatus("请先执行比对");
                    Log("请先点『开始比对』生成结果，再导出。");
                    return;
                }
                if (!Directory.Exists(_txtExportDir.Text))
                {
                    SetStatus("导出文件夹无效");
                    Log("导出文件夹不存在：" + _txtExportDir.Text);
                    return;
                }
                string baseName = "比对结果_" + Path.GetFileNameWithoutExtension(_txtFileA.Text)
                               + "_vs_" + Path.GetFileNameWithoutExtension(_txtFileB.Text);
                string path = DataCompareCore.Export(_result, _txtExportDir.Text, baseName,
                    _cmbFormat.SelectedItem as string, Log);
                SetStatus("已导出：" + Path.GetFileName(path));
                MessageBox.Show("比对结果已导出：\r\n" + path, "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("导出失败: " + ex.Message);
                MessageBox.Show("导出失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ====================== 线程安全 ======================

        private void Log(string msg)
        {
            if (_rtbLog.InvokeRequired) { _rtbLog.Invoke(new Action<string>(Log), msg); return; }
            _rtbLog.AppendText(msg + Environment.NewLine);
            _rtbLog.ScrollToCaret();
        }

        private void SetStatus(string text)
        {
            if (_lblStatus.InvokeRequired) { _lblStatus.Invoke(new Action<string>(SetStatus), text); return; }
            _lblStatus.Text = text;
        }

        private void InvokeOnUi(Action action)
        {
            if (InvokeRequired) { Invoke(action); return; }
            action();
        }

        // ====================== 界面设置持久化 ======================

        public string SettingsSection { get { return "DataCompare"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;
            store.SetBool(s, "Header", _chkHeader.Checked);
            store.Set(s, "PairMode", _cmbPairMode.SelectedItem as string);
            store.Set(s, "Mode", _cmbMode.SelectedItem as string);
            store.SetBool(s, "IgnoreCase", _chkIgnoreCase.Checked);
            store.Set(s, "Tolerance", _txtTolerance.Text);
            store.SetBool(s, "ExtraB", _chkExtraB.Checked);
            store.SetBool(s, "IncludeUnmatched", _chkIncludeUnmatched.Checked);
            store.SetBool(s, "OnlyDiff", _chkOnlyDiff.Checked);
            store.Set(s, "Format", _cmbFormat.SelectedItem as string);

            // 表相关：SN 列 + 勾选的比对列（按勾选顺序保存，配对顺序才不会乱）
            store.Set(s, "SnA", _cmbSnA.SelectedItem as string);
            store.Set(s, "SnB", _cmbSnB.SelectedItem as string);
            store.SetList(s, "ColsA", GetCheckedCols(_clbColsA, _orderA));
            store.SetList(s, "ColsB", GetCheckedCols(_clbColsB, _orderB));
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            string s = SettingsSection;

            _chkHeader.Checked = store.GetBool(s, "Header", _chkHeader.Checked);
            _chkIgnoreCase.Checked = store.GetBool(s, "IgnoreCase", _chkIgnoreCase.Checked);
            _chkExtraB.Checked = store.GetBool(s, "ExtraB", _chkExtraB.Checked);
            _chkIncludeUnmatched.Checked = store.GetBool(s, "IncludeUnmatched", _chkIncludeUnmatched.Checked);
            _chkOnlyDiff.Checked = store.GetBool(s, "OnlyDiff", _chkOnlyDiff.Checked);

            _txtTolerance.Text = store.Get(s, "Tolerance", _txtTolerance.Text);

            SelectCombo(_cmbPairMode, store.Get(s, "PairMode", _cmbPairMode.SelectedItem as string));
            SelectCombo(_cmbMode, store.Get(s, "Mode", _cmbMode.SelectedItem as string));
            SelectCombo(_cmbFormat, store.Get(s, "Format", _cmbFormat.SelectedItem as string));

            _pendingSnA = store.Get(s, "SnA", null);
            _pendingSnB = store.Get(s, "SnB", null);
            _pendingColsA = store.GetList(s, "ColsA");
            _pendingColsB = store.GetList(s, "ColsB");
        }

        /// <summary>按文本选中下拉项；找不到就保持原选中项（不抛异常）。</summary>
        private static void SelectCombo(ComboBox cmb, string text)
        {
            if (cmb == null || string.IsNullOrEmpty(text)) return;
            int i = cmb.Items.IndexOf(text);
            if (i >= 0) cmb.SelectedIndex = i;
        }

        /// <summary>
        /// 表格加载后套用上次保存的 SN 列与勾选列。
        /// 保存的列在新表里不存在时自动忽略（不报错），缺失的保持默认勾选。
        /// </summary>
        private void ApplyPendingCols(ComboBox cmbSn, CheckedListBox clb, List<string> order,
                                      string pendingSn, List<string> pendingCols)
        {
            if (!string.IsNullOrEmpty(pendingSn)) SelectCombo(cmbSn, pendingSn);

            if (pendingCols == null || pendingCols.Count == 0) return;

            // 先按保存的列表逐项勾选（SetItemChecked 会触发 ItemCheck，进而维护 order）
            for (int i = 0; i < clb.Items.Count; i++)
            {
                string name = clb.Items[i] as string;
                if (name == null) continue;
                bool want = pendingCols.Contains(name);
                if (clb.GetItemChecked(i) != want) clb.SetItemChecked(i, want);
            }
            // 顺序以保存的为准（SetItemChecked 追加的顺序可能与保存顺序不同）
            order.Clear();
            foreach (string n in pendingCols)
                if (clb.Items.Contains(n)) order.Add(n);
        }
    }
}
