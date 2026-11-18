using System;
using System.Windows.Forms;
using System.IO;

namespace DataAssistant
{
    /// <summary>
    /// 定时自动备份配置面板。实现 ISettingsAware，把配置写入 SettingsStore 并推给 SchedulerService。
    /// 注意：定时任务的路径是长期配置，必须保存（与图片查找等一次性任务不同）。
    /// </summary>
    public class AutoBackupPanel : UserControl, ISettingsAware
    {
        private readonly TextBox _txtSrc = new TextBox();
        private readonly TextBox _txtDst = new TextBox();
        private readonly ComboBox _cmbInterval = new ComboBox();
        private readonly DateTimePicker _dtpTime = new DateTimePicker();
        private readonly NumericUpDown _numKeep = new NumericUpDown();
        private readonly CheckBox _chkEnabled = new CheckBox();
        private readonly CheckBox _chkAutoStart = new CheckBox();
        private readonly Button _btnSrc = new Button();
        private readonly Button _btnDst = new Button();
        private readonly Label _lblStatus = new Label();
        private readonly SchedulerService _scheduler;

        public AutoBackupPanel(SchedulerService scheduler)
        {
            _scheduler = scheduler;
            if (_scheduler != null)
                _scheduler.OnStatus += (k, t) => { if (k == "backup") SetStatus(t); };
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Size = new System.Drawing.Size(900, 620);
            Font = new System.Drawing.Font("Microsoft YaHei", 9.5f);

            _chkEnabled.SetBounds(12, 12, 240, 24);
            _chkEnabled.Text = "启用定时自动备份";
            _chkEnabled.Checked = false;
            Controls.Add(_chkEnabled);

            AddLabel("源路径(要备份的文件夹):", 12, 52);
            _txtSrc.SetBounds(210, 50, 470, 23);
            _btnSrc.SetBounds(690, 50, 90, 23); _btnSrc.Text = "选择目录"; _btnSrc.Click += (s, e) => Pick(_txtSrc);
            Controls.AddRange(new Control[] { _txtSrc, _btnSrc });

            AddLabel("备份路径(存放备份):", 12, 84);
            _txtDst.SetBounds(210, 82, 470, 23);
            _btnDst.SetBounds(690, 82, 90, 23); _btnDst.Text = "选择目录"; _btnDst.Click += (s, e) => Pick(_txtDst);
            Controls.AddRange(new Control[] { _txtDst, _btnDst });

            AddLabel("备份周期:", 12, 116);
            _cmbInterval.SetBounds(210, 114, 150, 23);
            _cmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbInterval.Items.AddRange(new object[] { "每天", "每周", "每月" });
            _cmbInterval.SelectedIndex = 0;
            Controls.Add(_cmbInterval);

            AddLabel("备份时间:", 12, 148);
            _dtpTime.SetBounds(210, 146, 110, 23);
            _dtpTime.Format = DateTimePickerFormat.Time;
            _dtpTime.ShowUpDown = true;
            _dtpTime.CustomFormat = "HH:mm";
            _dtpTime.Value = DateTime.Today.Add(new TimeSpan(2, 0, 0));
            Controls.Add(_dtpTime);

            AddLabel("保留个数:", 12, 180);
            _numKeep.SetBounds(210, 178, 90, 23);
            _numKeep.Minimum = 1; _numKeep.Maximum = 999; _numKeep.Value = 7;
            Controls.Add(_numKeep);

            _chkAutoStart.SetBounds(12, 214, 360, 24);
            _chkAutoStart.Text = "开机启动并最小化到托盘";
            _chkAutoStart.CheckedChanged += (s, e) =>
            {
                try { AutoStartManager.SetEnabled(_chkAutoStart.Checked); } catch { }
            };
            Controls.Add(_chkAutoStart);

            _lblStatus.SetBounds(12, 252, 860, 22);
            _lblStatus.Text = "空闲";
            _lblStatus.ForeColor = System.Drawing.Color.DarkGray;
            Controls.Add(_lblStatus);

            var tip = new Label();
            tip.SetBounds(12, 284, 860, 130);
            tip.Text = "说明：\n" +
                "· 到达备份时间时，把「源路径」整个文件夹完整复制到「备份路径\\YYYYMMDD」下。\n" +
                "· 完成后保留最近 N 个日期文件夹，超出的最旧备份自动删除。\n" +
                "· 备份在后台线程执行，不卡界面，不影响其它软件；所有操作记录在「运行日志」页。";
            Controls.Add(tip);
        }

        private void AddLabel(string text, int x, int y)
        {
            var l = new Label { Text = text, AutoSize = false };
            l.SetBounds(x, y, 190, 20);
            Controls.Add(l);
        }

        private void Pick(TextBox tb)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                if (!string.IsNullOrEmpty(tb.Text) && Directory.Exists(tb.Text)) dlg.SelectedPath = tb.Text;
                if (dlg.ShowDialog() == DialogResult.OK) tb.Text = dlg.SelectedPath;
            }
        }

        private void SetStatus(string t)
        {
            if (InvokeRequired) { Invoke(new Action<string>(SetStatus), t); return; }
            _lblStatus.Text = t;
        }

        public string SettingsSection { get { return "AutoBackup"; } }

        public void SaveSettings(SettingsStore store)
        {
            if (store == null) return;
            store.SetBool(SettingsSection, "Enabled", _chkEnabled.Checked);
            store.Set(SettingsSection, "Source", _txtSrc.Text);
            store.Set(SettingsSection, "Dest", _txtDst.Text);
            int interval = _cmbInterval.SelectedIndex == 1 ? 7 : (_cmbInterval.SelectedIndex == 2 ? 30 : 1);
            store.SetInt(SettingsSection, "Interval", interval);
            store.Set(SettingsSection, "Time", _dtpTime.Value.ToString("HH:mm"));
            store.SetInt(SettingsSection, "Keep", (int)_numKeep.Value);
            store.SetBool(SettingsSection, "AutoStart", _chkAutoStart.Checked);
            if (_scheduler != null && _scheduler.BackupLastRun.HasValue)
                store.Set(SettingsSection, "LastRun", _scheduler.BackupLastRun.Value.ToString("o"));
        }

        public void LoadSettings(SettingsStore store)
        {
            if (store == null) return;
            _chkEnabled.Checked = store.GetBool(SettingsSection, "Enabled", _chkEnabled.Checked);
            _txtSrc.Text = store.Get(SettingsSection, "Source", _txtSrc.Text);
            _txtDst.Text = store.Get(SettingsSection, "Dest", _txtDst.Text);
            int interval = store.GetInt(SettingsSection, "Interval", 1);
            _cmbInterval.SelectedIndex = interval == 7 ? 1 : (interval == 30 ? 2 : 0);
            string t = store.Get(SettingsSection, "Time", "02:00");
            TimeSpan ts; if (!TimeSpan.TryParse(t, out ts)) ts = new TimeSpan(2, 0, 0);
            _dtpTime.Value = DateTime.Today.Add(ts);
            _numKeep.Value = Math.Min(_numKeep.Maximum, Math.Max(_numKeep.Minimum, store.GetInt(SettingsSection, "Keep", 7)));

            bool autostart = store.GetBool(SettingsSection, "AutoStart", AutoStartManager.IsEnabled());
            _chkAutoStart.Checked = autostart;

            string last = store.Get(SettingsSection, "LastRun", null);
            DateTime lr;
            if (!string.IsNullOrEmpty(last) && DateTime.TryParse(last, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out lr) && _scheduler != null)
                _scheduler.BackupLastRun = lr;

            if (_scheduler != null)
            {
                _scheduler.UpdateBackup(_chkEnabled.Checked, _txtSrc.Text, _txtDst.Text, interval, ts, (int)_numKeep.Value);
                if (_scheduler.BackupLastRun.HasValue)
                    SetStatus("上次备份: " + _scheduler.BackupLastRun.Value.ToString("yyyy-MM-dd HH:mm"));
            }
        }
    }
}
