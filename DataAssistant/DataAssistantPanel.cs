using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataAssistant
{
    /// <summary>
    /// 可复用的 SN 图片文件夹复制控件（数据助手）。
    /// 直接用 Visual Studio 工具箱拖到你的主窗体上（Dock = Fill 即可）。
    /// 也提供公共属性/方法，方便代码方式调用。
    /// </summary>
    public class DataAssistantPanel : UserControl, ISettingsAware
    {
        private readonly TextBox _txtFile = new TextBox();
        private readonly TextBox _txtSrc = new TextBox();
        private readonly TextBox _txtDst = new TextBox();
        private readonly CheckBox _chkHeader = new CheckBox();
        private readonly Button _btnFile = new Button();
        private readonly Button _btnSrc = new Button();
        private readonly Button _btnDst = new Button();
        private readonly Button _btnPreview = new Button();
        private readonly Button _btnStart = new Button();
        private readonly ProgressBar _pb = new ProgressBar();
        private readonly Label _lblStatus = new Label();
        private readonly ListView _lv = new ListView();
        private readonly RichTextBox _rtbLog = new RichTextBox();
        private readonly PictureBox _logo = new PictureBox();
        private readonly FlowLayoutPanel _pnlFmt = new FlowLayoutPanel();
        private readonly List<CheckBox> _fmtChecks = new List<CheckBox>();

        /// <summary>可选图片格式：显示名 -> 扩展名(不含点，JPG 同时含 jpg/jpeg)。默认全部勾选。</summary>
        private static readonly (string Label, string[] Exts)[] ImageFormats = new[]
        {
            ("JPG", new[] { "jpg", "jpeg" }),
            ("PNG", new[] { "png" }),
            ("BMP", new[] { "bmp" })
        };

        private CancellationTokenSource _cts;
        private bool _busy;

        public DataAssistantPanel()
        {
            InitializeComponent();
        }

        // -------- 对外属性（方便主程序预设）--------
        public string TableFilePath { get { return _txtFile.Text; } set { _txtFile.Text = value; } }
        public string SourceRoot { get { return _txtSrc.Text; } set { _txtSrc.Text = value; } }
        public string DestRoot { get { return _txtDst.Text; } set { _txtDst.Text = value; } }
        public bool HeaderRow { get { return _chkHeader.Checked; } set { _chkHeader.Checked = value; } }

        // -------- 对外方法（代码触发）--------
        public void StartPreview() { OnPreviewClicked(); }
        public void StartCopy() { OnStartClicked(); }

        private void InitializeComponent()
        {
            Size = new System.Drawing.Size(780, 680);
            Font = new System.Drawing.Font("Microsoft YaHei", 9.5f);

            // Logo
            _logo.SetBounds(8, 8, 300, 48);
            _logo.SizeMode = PictureBoxSizeMode.Zoom;
            _logo.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            _logo.Image = LoadLogo();
            Controls.Add(_logo);

            AddLabel("表格文件(CSV/TXT/Excel):", 8, 70);
            _txtFile.SetBounds(120, 68, 430, 23);
            _btnFile.SetBounds(560, 68, 90, 23); _btnFile.Text = "选择文件"; _btnFile.Click += (s, e) => PickFile();
            Controls.AddRange(new Control[] { _txtFile, _btnFile });

            AddLabel("源根目录:", 8, 102);
            _txtSrc.SetBounds(120, 102, 430, 23);
            _btnSrc.SetBounds(560, 102, 90, 23); _btnSrc.Text = "选择目录"; _btnSrc.Click += (s, e) => PickFolder(_txtSrc);
            Controls.AddRange(new Control[] { _txtSrc, _btnSrc });

            AddLabel("目标目录:", 8, 134);
            _txtDst.SetBounds(120, 134, 430, 23);
            _btnDst.SetBounds(560, 134, 90, 23); _btnDst.Text = "选择目录"; _btnDst.Click += (s, e) => PickFolder(_txtDst);
            Controls.AddRange(new Control[] { _txtDst, _btnDst });

            _chkHeader.SetBounds(120, 164, 360, 23);
            _chkHeader.Text = "首行是表头(按列名 SN 取, 否则取第一列)";
            _chkHeader.Checked = true;
            _btnPreview.SetBounds(500, 164, 120, 23); _btnPreview.Text = "扫描预览"; _btnPreview.Click += (s, e) => OnPreviewClicked();
            _btnStart.SetBounds(630, 164, 130, 23); _btnStart.Text = "开始复制"; _btnStart.Click += (s, e) => OnStartClicked();
            Controls.AddRange(new Control[] { _chkHeader, _btnPreview, _btnStart });

            // ---- 可选图片格式 ----
            AddLabel("图片格式(可多选):", 8, 196);
            _pnlFmt.SetBounds(8, 218, 752, 46);
            _pnlFmt.FlowDirection = FlowDirection.LeftToRight;
            _pnlFmt.WrapContents = true;
            _pnlFmt.AutoSize = false;
            _pnlFmt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            foreach (var f in ImageFormats)
            {
                var cb = new CheckBox
                {
                    Text = f.Label,
                    Tag = f.Exts,
                    Checked = true,
                    AutoSize = true,
                    Margin = new Padding(4, 2, 10, 2)
                };
                _fmtChecks.Add(cb);
                _pnlFmt.Controls.Add(cb);
            }
            Controls.Add(_pnlFmt);

            _pb.SetBounds(8, 272, 752, 18); _pb.Style = ProgressBarStyle.Continuous;
            Controls.Add(_pb);
            _lblStatus.SetBounds(8, 294, 752, 18); _lblStatus.Text = "就绪";
            Controls.Add(_lblStatus);

            AddLabel("预览(扫描结果):", 8, 320);
            _lv.SetBounds(8, 340, 752, 168);
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.GridLines = true;
            _lv.Columns.Add("SN", 130);
            _lv.Columns.Add("状态", 70);
            _lv.Columns.Add("文件夹数", 80);
            _lv.Columns.Add("文件数", 80);
            _lv.Columns.Add("命中路径", -2);
            _lv.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_lv);

            AddLabel("日志:", 8, 514);
            _rtbLog.SetBounds(8, 534, 752, Height - 534 - 12);
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
            catch
            {
                return null;
            }
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label { Text = text, Left = x, Top = y + 3, AutoSize = true });
        }

        private void PickFile()
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "表格文件 (*.csv;*.txt;*.xls;*.xlsx)|*.csv;*.txt;*.xls;*.xlsx|所有文件 (*.*)|*.*",
                Title = "选择含 SN 的表格文件"
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK) _txtFile.Text = dlg.FileName;
            }
        }

        private void PickFolder(TextBox tb)
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择目录" })
            {
                if (Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.SelectedPath;
            }
        }

        private void OnPreviewClicked()
        {
            if (_busy)
            {
                _cts?.Cancel();
                _btnPreview.Text = "正在取消...";
                return;
            }
            _busy = true;
            _btnPreview.Enabled = true;
            _btnPreview.Text = "取消";
            _btnStart.Enabled = false;
            var token = (_cts = new CancellationTokenSource()).Token;
            var fmts = SelectedFormats();
            Task.Run(() => DoPreview(token, fmts));
        }

        private void OnStartClicked()
        {
            if (_busy)
            {
                _cts?.Cancel();
                _btnStart.Text = "正在取消...";
                return;
            }
            _busy = true;
            _btnStart.Enabled = true;
            _btnStart.Text = "取消";
            _btnPreview.Enabled = false;
            var token = (_cts = new CancellationTokenSource()).Token;
            var fmts = SelectedFormats();
            Task.Run(() => DoCopy(token, fmts));
        }

        /// <summary>当前勾选的图片扩展名集合（不含点，小写；JPG 项同时含 jpg/jpeg）。</summary>
        private HashSet<string> SelectedFormats()
        {
            var hs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cb in _fmtChecks)
                if (cb.Checked) foreach (var e in (string[])cb.Tag) hs.Add(e);
            return hs;
        }

        // ====================== 扫描预览 ======================
        private void DoPreview(CancellationToken token, HashSet<string> fmts)
        {
            var (ok, sns) = Prepare();
            if (!ok) { LeaveBusy(); return; }

            try
            {
                Log("开始扫描(不复制)...");
                var matches = new List<DataAssistantCore.SnMatchInfo>();
                for (int i = 0; i < sns.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    matches.AddRange(DataAssistantCore.Scan(_txtSrc.Text, new[] { sns[i] }, fmts));
                    SetProgress(i + 1, sns.Count);
                }
                FillPreview(matches);
                int hit = matches.FindAll(m => m.Found).Count;
                Log(string.Format("扫描完成。共 {0} 个 SN, 命中 {1}, 缺失 {2}。", sns.Count, hit, sns.Count - hit));
                SetStatus(string.Format("扫描完成: 命中 {0} / 缺失 {1}", hit, sns.Count - hit));
            }
            catch (OperationCanceledException) { Log("已取消。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        // ====================== 复制 ======================
        private void DoCopy(CancellationToken token, HashSet<string> fmts)
        {
            var (ok, sns) = Prepare();
            if (!ok) { LeaveBusy(); return; }

            try
            {
                Directory.CreateDirectory(_txtDst.Text);
                Log("开始复制... (图片格式: " + (fmts.Count == 0 ? "无(将不复制任何图片)" : string.Join("/", fmts)) + ")");
                var res = DataAssistantCore.Copy(_txtSrc.Text, _txtDst.Text, sns, Log, token, fmts);
                Log(string.Format("处理结束。成功 {0}, 缺失 {1}, 重名{2}, 共复制 {3} 个文件。",
                    res.Ok, res.Missing, res.Duplicate > 0 ? "合并 " + res.Duplicate : "0", res.TotalFiles));
                MessageBox.Show(string.Format("处理结束。\n成功复制: {0}\n未找到: {1}\n重名(已取最早): {2}\n共复制文件: {3}",
                    res.Ok, res.Missing, res.Duplicate, res.TotalFiles), "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException) { Log("已取消。"); }
            catch (Exception ex) { Log("异常: " + ex.Message); }
            finally { LeaveBusy(); }
        }

        // 读取表格 + 校验输入；返回 (成功, SN列表)
        private (bool, List<string>) Prepare()
        {
            if (!File.Exists(_txtFile.Text))
            {
                MessageBox.Show("表格文件不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (false, null);
            }
            if (!Directory.Exists(_txtSrc.Text))
            {
                MessageBox.Show("源根目录不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (false, null);
            }
            if (string.IsNullOrWhiteSpace(_txtDst.Text))
            {
                MessageBox.Show("请选择目标目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (false, null);
            }
            List<string> sns;
            try { sns = DataAssistantCore.ReadSnList(_txtFile.Text, _chkHeader.Checked); }
            catch (Exception ex)
            {
                MessageBox.Show("读取表格失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return (false, null);
            }
            if (sns.Count == 0)
            {
                MessageBox.Show("未在表格中找到任何 SN。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return (false, null);
            }
            return (true, sns);
        }

        // ====================== UI 状态 ======================
        private void LeaveBusy()
        {
            _busy = false;
            _cts = null;
            _btnPreview.Enabled = true;
            _btnStart.Enabled = true;
            _btnPreview.Text = "扫描预览";
            _btnStart.Text = "开始复制";
        }

        private void FillPreview(List<DataAssistantCore.SnMatchInfo> matches)
        {
            if (_lv.InvokeRequired)
            {
                _lv.Invoke(new Action<List<DataAssistantCore.SnMatchInfo>>(FillPreview), matches);
                return;
            }
            _lv.Items.Clear();
            foreach (var m in matches)
            {
                var item = new ListViewItem(m.Sn);
                item.SubItems.Add(m.Found ? "命中" : "缺失");
                item.SubItems.Add(m.Folders.Count.ToString());
                item.SubItems.Add(m.FileCount.ToString());
                if (!m.Found)
                    item.SubItems.Add("");
                else if (m.Folders.Count > 1)
                    item.SubItems.Add(m.ChosenFolder + "  (+" + (m.Folders.Count - 1) + " 个重名, 已忽略)");
                else
                    item.SubItems.Add(m.ChosenFolder);
                if (!m.Found) item.ForeColor = System.Drawing.Color.Red;
                _lv.Items.Add(item);
            }
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
            else { _pb.Maximum = maximum; _pb.Value = value; }
        }

        // ====================== 界面设置持久化 ======================

        public string SettingsSection { get { return "ImageFinder"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            store.SetBool(SettingsSection, "Header", _chkHeader.Checked);
            var sel = new List<string>();
            foreach (var cb in _fmtChecks)
                if (cb.Checked) sel.Add(cb.Text);
            store.SetList(SettingsSection, "Fmts", sel);
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            _chkHeader.Checked = store.GetBool(SettingsSection, "Header", _chkHeader.Checked);
            var fmts = store.GetList(SettingsSection, "Fmts");
            if (fmts.Count > 0)
            {
                var saved = new HashSet<string>(fmts, StringComparer.OrdinalIgnoreCase);
                foreach (var cb in _fmtChecks)
                {
                    var exts = (string[])cb.Tag;
                    cb.Checked = saved.Contains(cb.Text) || exts.Any(e => saved.Contains(e));
                }
            }
        }
    }
}
