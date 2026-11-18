using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Drawing;

namespace DataAssistant
{
    public class CsvCopyForm : Form
    {
        // ---- 控件 ----
        private readonly TextBox _txtCsv = new TextBox();
        private readonly TextBox _txtSrc = new TextBox();
        private readonly TextBox _txtDst = new TextBox();
        private readonly CheckBox _chkHeader = new CheckBox();
        private readonly Button _btnCsv = new Button();
        private readonly Button _btnSrc = new Button();
        private readonly Button _btnDst = new Button();
        private readonly Button _btnStart = new Button();
        private readonly ProgressBar _pb = new ProgressBar();
        private readonly RichTextBox _rtbLog = new RichTextBox();
        private readonly Label _lblStatus = new Label();

        private CancellationTokenSource _cts;

        public CsvCopyForm()
        {
            Text = "数据助手 - CSV 快速复制";
            // 标题栏与任务栏图标
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Width = 760;
            Height = 560;
            MinimumSize = new System.Drawing.Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new System.Drawing.Font("Microsoft YaHei", 9.5f);
            BuildUI();
        }

        private void BuildUI()
        {
            int y = 16;
            int rowH = 30;

            AddLabel("CSV 表格(含 SN 列):", 12, y);
            _txtCsv.SetBounds(180, y, 380, 23);
            _btnCsv.SetBounds(570, y, 90, 23);
            _btnCsv.Text = "选择文件";
            _btnCsv.Click += (s, e) => PickCsv();
            Controls.AddRange(new Control[] { _txtCsv, _btnCsv });
            y += rowH;

            AddLabel("源根目录(含图片文件夹):", 12, y);
            _txtSrc.SetBounds(180, y, 380, 23);
            _btnSrc.SetBounds(570, y, 90, 23);
            _btnSrc.Text = "选择目录";
            _btnSrc.Click += (s, e) => PickFolder(_txtSrc);
            Controls.AddRange(new Control[] { _txtSrc, _btnSrc });
            y += rowH;

            AddLabel("目标目录:", 12, y);
            _txtDst.SetBounds(180, y, 380, 23);
            _btnDst.SetBounds(570, y, 90, 23);
            _btnDst.Text = "选择目录";
            _btnDst.Click += (s, e) => PickFolder(_txtDst);
            Controls.AddRange(new Control[] { _txtDst, _btnDst });
            y += rowH;

            _chkHeader.SetBounds(180, y, 360, 23);
            _chkHeader.Text = "首行是表头(按列名 SN 取, 否则取第一列)";
            _chkHeader.Checked = true;
            Controls.Add(_chkHeader);
            y += rowH + 4;

            _btnStart.SetBounds(180, y, 140, 32);
            _btnStart.Text = "开始复制";
            _btnStart.Click += (s, e) => OnStartClicked();
            Controls.Add(_btnStart);
            y += 40;

            _pb.SetBounds(12, y, 720, 20);
            _pb.Style = ProgressBarStyle.Continuous;
            Controls.Add(_pb);
            y += 28;

            _lblStatus.SetBounds(12, y, 720, 20);
            _lblStatus.Text = "就绪";
            Controls.Add(_lblStatus);
            y += 24;

            AddLabel("日志:", 12, y);
            y += 22;
            _rtbLog.SetBounds(12, y, 720, Height - y - 24);
            _rtbLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _rtbLog.ReadOnly = true;
            _rtbLog.ScrollBars = RichTextBoxScrollBars.Vertical;
            Controls.Add(_rtbLog);
        }

        private void AddLabel(string text, int x, int y)
        {
            var lbl = new Label { Text = text, Left = x, Top = y + 3, AutoSize = true };
            Controls.Add(lbl);
        }

        private void PickCsv()
        {
            using (var dlg = new OpenFileDialog
            {
                Filter = "CSV/文本文件 (*.csv;*.txt)|*.csv;*.txt|所有文件 (*.*)|*.*",
                Title = "选择含 SN 的表格文件"
            })
            {
                if (dlg.ShowDialog() == DialogResult.OK) _txtCsv.Text = dlg.FileName;
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

        private void OnStartClicked()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                return;
            }
            Task.Run(() => RunAsync());
        }

        // ====================== 核心逻辑 ======================

        private void RunAsync()
        {
            if (!File.Exists(_txtCsv.Text))
            {
                MessageBox.Show("CSV 文件不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!Directory.Exists(_txtSrc.Text))
            {
                MessageBox.Show("源根目录不存在。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (string.IsNullOrWhiteSpace(_txtDst.Text))
            {
                MessageBox.Show("请选择目标目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Directory.CreateDirectory(_txtDst.Text);

            List<string> sns;
            try
            {
                sns = ReadSnList(_txtCsv.Text, _chkHeader.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show("读取 CSV 失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (sns.Count == 0)
            {
                MessageBox.Show("未在表格中找到任何 SN。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _cts = new CancellationTokenSource();
            SetButton("取消");
            SetProgress(0, sns.Count);
            Log("共解析到 " + sns.Count + " 个 SN, 开始处理...");

            int ok = 0, missing = 0, dup = 0;
            var token = _cts.Token;

            try
            {
                for (int i = 0; i < sns.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var sn = sns[i];
                    var matches = FindFolders(_txtSrc.Text, sn).ToList();

                    if (matches.Count == 0)
                    {
                        missing++;
                        Log("[缺失] SN=" + sn + " 未找到对应文件夹");
                    }
                    else
                    {
                        if (matches.Count > 1) dup++;
                        var destDir = Path.Combine(_txtDst.Text, sn);
                        int copied = 0;
                        foreach (var src in matches)
                        {
                            copied += CopyDirectory(src, destDir);
                        }
                        ok++;
                        Log("[完成] SN=" + sn + " 复制 " + copied + " 个文件" +
                            (matches.Count > 1 ? " (命中 " + matches.Count + " 个同名文件夹, 已合并)" : ""));
                    }

                    SetProgress(i + 1, sns.Count);
                    SetStatus(string.Format("进度 {0}/{1}  |  成功 {2}  缺失 {3}  重名 {4}",
                        i + 1, sns.Count, ok, missing, dup));
                }

                Log(string.Format("处理结束。成功 {0}, 缺失 {1}{2}",
                    ok, missing, dup > 0 ? ", 重名合并 " + dup : ""));
                MessageBox.Show(string.Format("处理结束。\n成功复制: {0}\n未找到: {1}{2}",
                    ok, missing, dup > 0 ? "\n重名合并: " + dup : ""), "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                Log("已取消。");
                MessageBox.Show("已取消。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("异常: " + ex.Message);
                MessageBox.Show("异常: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _cts = null;
                SetButton("开始复制");
            }
        }

        // ---- CSV 解析 ----
        private List<string> ReadSnList(string path, bool hasHeader)
        {
            var lines = File.ReadAllLines(path);
            var sns = new List<string>();
            if (lines.Length == 0) return sns;

            char delim = DetectDelimiter(lines[0]);
            int snCol = 0;
            int start = 0;

            if (hasHeader)
            {
                var header = ParseCsvLine(lines[0], delim);
                for (int i = 0; i < header.Length; i++)
                {
                    var h = header[i].Trim();
                    if (string.Equals(h, "SN", StringComparison.OrdinalIgnoreCase) ||
                        h.IndexOf("SN", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        snCol = i;
                        break;
                    }
                }
                start = 1;
            }

            for (int i = start; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var fields = ParseCsvLine(lines[i], delim);
                if (snCol < fields.Length)
                {
                    var v = fields[snCol].Trim();
                    if (v.Length > 0) sns.Add(v);
                }
            }
            return sns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static char DetectDelimiter(string line)
        {
            int comma = line.Count(c => c == ',');
            int tab = line.Count(c => c == '\t');
            int semi = line.Count(c => c == ';');
            if (tab >= comma && tab >= semi && tab > 0) return '\t';
            if (semi > comma && semi > 0) return ';';
            return ',';
        }

        private static string[] ParseCsvLine(string line, char delim)
        {
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == delim) { result.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        // ---- 按文件夹名精确匹配 SN（迭代式递归搜索，跳过无权限目录）----
        private IEnumerable<string> FindFolders(string root, string sn)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                IEnumerable<string> sub;
                try { sub = Directory.EnumerateDirectories(cur); }
                catch (Exception) { continue; }

                foreach (var d in sub)
                {
                    if (string.Equals(Path.GetFileName(d), sn, StringComparison.OrdinalIgnoreCase))
                        yield return d;
                    stack.Push(d);
                }
            }
        }

        // ---- 复制文件夹内容（含子目录），已存在则覆盖 ----
        private int CopyDirectory(string source, string dest)
        {
            int count = 0;
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.GetFiles(source))
            {
                try
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
                    count++;
                }
                catch (Exception ex)
                {
                    Log("  复制失败: " + file + " -> " + ex.Message);
                }
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                count += CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
            }
            return count;
        }

        // ---- UI 线程安全更新 ----
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
            if (_lblStatus.InvokeRequired)
                _lblStatus.Invoke(new Action<string>(SetStatus), text);
            else
                _lblStatus.Text = text;
        }

        private void SetProgress(int value, int maximum)
        {
            if (_pb.InvokeRequired)
                _pb.Invoke(new Action<int, int>(SetProgress), value, maximum);
            else
            {
                _pb.Maximum = maximum;
                _pb.Value = value;
            }
        }

        private void SetButton(string text)
        {
            if (_btnStart.InvokeRequired)
                _btnStart.Invoke(new Action<string>(SetButton), text);
            else
                _btnStart.Text = text;
        }
    }
}
