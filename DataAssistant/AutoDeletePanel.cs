using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using System.Globalization;

namespace DataAssistant
{
    /// <summary>
    /// 定时自动删图配置面板：最多 10 个删除任务，每个含路径/删除时间/保留天数/启用/状态。
    /// 每天到达删除时间时，删除该路径下「创建时间」超过保留天数的子文件夹。
    /// </summary>
    public class AutoDeletePanel : UserControl, ISettingsAware
    {
        private readonly DataGridView _dgv = new DataGridView();
        private readonly Button _btnAdd = new Button();
        private readonly Button _btnDel = new Button();
        private readonly Label _lblHint = new Label();
        private readonly SchedulerService _scheduler;
        private static readonly int MaxTasks = 10;

        public AutoDeletePanel(SchedulerService scheduler)
        {
            _scheduler = scheduler;
            if (_scheduler != null)
                _scheduler.OnStatus += (k, t) =>
                {
                    if (k.StartsWith("del"))
                    {
                        int idx; if (int.TryParse(k.Substring(3), out idx)) SetRowStatus(idx, t);
                    }
                };
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Size = new System.Drawing.Size(900, 620);
            Font = new System.Drawing.Font("Microsoft YaHei", 9.5f);

            _dgv.SetBounds(12, 12, 860, 420);
            _dgv.AllowUserToAddRows = false;
            _dgv.AllowUserToDeleteRows = false;
            _dgv.RowHeadersVisible = false;
            _dgv.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _dgv.BackgroundColor = System.Drawing.SystemColors.Window;
            _dgv.ColumnHeadersHeight = 28;
            _dgv.DataError += (s, e) => { e.ThrowException = false; };

            var cPath = new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "路径(删除任务的图片根目录)", Width = 320 };
            var cBrowse = new DataGridViewButtonColumn { Name = "Browse", HeaderText = "浏览", Text = "选择", UseColumnTextForButtonValue = true, Width = 60 };
            var cTime = new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "删除时间(HH:mm)", Width = 100 };
            var cDays = new DataGridViewTextBoxColumn { Name = "Days", HeaderText = "保留天数", Width = 70 };
            var cEn = new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "启用", Width = 55, TrueValue = true, FalseValue = false };
            var cStatus = new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", Width = 195 };
            _dgv.Columns.AddRange(new DataGridViewColumn[] { cPath, cBrowse, cTime, cDays, cEn, cStatus });
            _dgv.Columns["Status"].ReadOnly = true;
            _dgv.CellContentClick += Dgv_CellContentClick;
            Controls.Add(_dgv);

            _btnAdd.SetBounds(12, 444, 130, 28); _btnAdd.Text = "添加任务"; _btnAdd.Click += (s, e) => AddTask();
            _btnDel.SetBounds(152, 444, 130, 28); _btnDel.Text = "删除选中"; _btnDel.Click += (s, e) => RemoveSelected();
            Controls.AddRange(new Control[] { _btnAdd, _btnDel });

            _lblHint.SetBounds(12, 484, 860, 100);
            _lblHint.Text = "说明：\n" +
                "· 点「浏览」列的「选择」按钮挑选要清理的目录，也可直接双击「路径」列手填。\n" +
                "· 最多 10 个删除任务。每天到达各任务的「删除时间」时，自动删除该路径下\n" +
                "  「创建时间」距今超过「保留天数」的子文件夹（整个文件夹，不可恢复）。\n" +
                "· 删除在后台线程执行，不卡界面；所有操作记录在「运行日志」页。";
            Controls.Add(_lblHint);
        }

        private void AddTask()
        {
            int count = 0;
            foreach (DataGridViewRow r in _dgv.Rows) if (!r.IsNewRow) count++;
            if (count >= MaxTasks) { MessageBox.Show("最多 " + MaxTasks + " 个删除任务。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            int i = _dgv.Rows.Add();
            _dgv.Rows[i].Cells["Time"].Value = "03:00";
            _dgv.Rows[i].Cells["Days"].Value = "30";
            _dgv.Rows[i].Cells["Enabled"].Value = true;
            _dgv.Rows[i].Cells["Status"].Value = "空闲";
        }

        private void RemoveSelected()
        {
            if (_dgv.SelectedRows.Count == 0) return;
            foreach (DataGridViewRow r in _dgv.SelectedRows)
                if (!r.IsNewRow) _dgv.Rows.Remove(r);
        }

        // 每行「浏览」按钮：打开文件夹选择器，把选中的目录写回该行的路径列
        private void Dgv_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (_dgv.Columns[e.ColumnIndex].Name != "Browse") return;
            var row = _dgv.Rows[e.RowIndex];
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择要清理的图片根目录";
                string cur = (row.Cells["Path"].Value ?? string.Empty).ToString().Trim();
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog() == DialogResult.OK)
                    row.Cells["Path"].Value = dlg.SelectedPath;
            }
        }

        private void SetRowStatus(int idx, string t)
        {
            if (InvokeRequired) { Invoke(new Action<int, string>(SetRowStatus), idx, t); return; }
            if (idx >= 0 && idx < _dgv.Rows.Count && !_dgv.Rows[idx].IsNewRow)
                _dgv.Rows[idx].Cells["Status"].Value = t;
        }

        public string SettingsSection { get { return "AutoDelete"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            var tasks = new List<SchedulerService.DeleteTask>();
            var lines = new List<string>();
            for (int i = 0; i < _dgv.Rows.Count; i++)
            {
                var row = _dgv.Rows[i];
                if (row.IsNewRow) continue;
                string path = (row.Cells["Path"].Value ?? string.Empty).ToString().Trim();
                string time = (row.Cells["Time"].Value ?? string.Empty).ToString().Trim();
                string days = (row.Cells["Days"].Value ?? string.Empty).ToString().Trim();
                bool en = row.Cells["Enabled"].Value != null && (bool)row.Cells["Enabled"].Value;

                DateTime? last = null;
                if (_scheduler != null && i < _scheduler.DeleteTasks.Count) last = _scheduler.DeleteTasks[i].LastRun;

                TimeSpan ts; if (!TimeSpan.TryParse(time, out ts)) ts = new TimeSpan(3, 0, 0);
                int d; if (!int.TryParse(days, out d)) d = 30;
                if (d < 0) d = 0;

                tasks.Add(new SchedulerService.DeleteTask { Path = path, Time = ts, KeepDays = d, Enabled = en, LastRun = last });
                lines.Add(string.Format("{0}|{1}|{2}|{3}|{4}", path, time, days, en ? "1" : "0",
                    last.HasValue ? last.Value.ToString("o") : string.Empty));
            }
            if (_scheduler != null) _scheduler.UpdateDeleteTasks(tasks);
            store.SetList(SettingsSection, "Tasks", lines);
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            var lines = store.GetList(SettingsSection, "Tasks");
            var tasks = new List<SchedulerService.DeleteTask>();
            _dgv.Rows.Clear();
            foreach (var line in lines)
            {
                var p = line.Split('|');
                if (p.Length < 4) continue;
                string path = p[0], time = p[1], days = p[2], en = p[3];
                string last = p.Length > 4 ? p[4] : string.Empty;

                TimeSpan ts; if (!TimeSpan.TryParse(time, out ts)) ts = new TimeSpan(3, 0, 0);
                int d; if (!int.TryParse(days, out d)) d = 30;
                bool eb = en == "1";
                DateTime lr; DateTime? lastRun = null;
                if (!string.IsNullOrEmpty(last) && DateTime.TryParse(last, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out lr)) lastRun = lr;

                tasks.Add(new SchedulerService.DeleteTask { Path = path, Time = ts, KeepDays = d, Enabled = eb, LastRun = lastRun });

                int r = _dgv.Rows.Add();
                _dgv.Rows[r].Cells["Path"].Value = path;
                _dgv.Rows[r].Cells["Time"].Value = time;
                _dgv.Rows[r].Cells["Days"].Value = days;
                _dgv.Rows[r].Cells["Enabled"].Value = eb;
                _dgv.Rows[r].Cells["Status"].Value = lastRun.HasValue
                    ? ("上次删除: " + lastRun.Value.ToString("yyyy-MM-dd HH:mm")) : "空闲";
            }
            if (_scheduler != null) _scheduler.UpdateDeleteTasks(tasks);
        }
    }
}
