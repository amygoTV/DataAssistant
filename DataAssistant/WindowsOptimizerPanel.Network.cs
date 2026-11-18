using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DataAssistant
{
    /// <summary>
    /// WindowsOptimizerPanel 的扩展页签：网络与取相优化。
    /// 依据 Cognex《"取相非正常失败" issues to improve》实现工业相机取相稳定性调优：
    ///   巨帧 9014 / 关闭网卡节能 / USB 与 PCIe 电源选项禁用 / Receive Buffers 最大值 /
    ///   Interrupt Moderation Rate = Extreme / 网络绑定精简为 eBUS + IPv4 / 禁用第三方以太网驱动。
    /// </summary>
    public partial class WindowsOptimizerPanel
    {
        /// <summary>风险等级。</summary>
        private enum NetRisk { Normal, Reversible, High }

        /// <summary>一个可勾选的调优项。</summary>
        private class NetOptItem
        {
            public string Id = string.Empty;
            public string Title = string.Empty;
            public string Note = string.Empty;
            public NetRisk Risk = NetRisk.Normal;
            public bool DefaultChecked = true;
            /// <summary>执行体：入参为目标网卡名与日志回调。</summary>
            public Action<string, Action<string>> Apply;

            public string RiskText
            {
                get
                {
                    switch (Risk)
                    {
                        case NetRisk.High: return "高危";
                        case NetRisk.Reversible: return "可逆";
                        default: return "普通";
                    }
                }
            }
        }

        // ---- 网络与取相优化 ----
        private readonly ListView _lvNetItems = new ListView();
        private readonly ComboBox _cmbNetAdapter = new ComboBox();
        private readonly Label _lblNetProps = new Label();
        private readonly List<NetworkTuningCore.NetAdapter> _netAdapters = new List<NetworkTuningCore.NetAdapter>();
        private readonly List<NetOptItem> _netOptItems = new List<NetOptItem>();

        // ====================== 页签构建 ======================

        /// <summary>构建「网络与取相」页签。</summary>
        private TabPage BuildNetPage()
        {
            var p = new TabPage("网络与取相");
            var host = MakePageHost(p);

            // ---------- 顶部 ----------
            var top = MakeTopPanel(168);
            var tip = new Label
            {
                Text = "针对 Cognex《“取相非正常失败”》对策：巨帧 9014、关闭网卡节能、USB/PCIe 电源选项禁用、" +
                       "Receive Buffers 取最大、Interrupt Moderation Rate = Extreme、网络绑定精简为 eBUS + IPv4。",
                Location = new Point(0, 4),
                AutoSize = true
            };
            top.Controls.Add(tip);

            var l1 = new Label { Text = "目标网卡:", Location = new Point(0, 28), AutoSize = true };
            top.Controls.Add(l1);
            _cmbNetAdapter.Location = new Point(64, 25);
            _cmbNetAdapter.Size = new Size(420, 23);
            _cmbNetAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbNetAdapter.SelectedIndexChanged += (s, e) => { try { RefreshNetProps(); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            top.Controls.Add(_cmbNetAdapter);

            var btnRefresh = MakeButton("刷新网卡", 492, 25, 90, 23);
            btnRefresh.Click += (s, e) => { try { Task.Run(() => DoNetScan()); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            top.Controls.Add(btnRefresh);

            var btnDev = MakeButton("打开设备管理器", 590, 25, 110, 23);
            btnDev.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("mmc.exe", "devmgmt.msc"); }
                catch (Exception ex) { Log("打开设备管理器失败: " + ex.Message); }
            };
            top.Controls.Add(btnDev);

            _lblNetProps.Location = new Point(0, 54);
            _lblNetProps.Size = new Size(840, 56);
            _lblNetProps.ForeColor = Color.DimGray;
            _lblNetProps.Text = "当前属性：点击『扫描当前状态』查看选中网卡的巨帧/节能/缓冲区等设置。";
            top.Controls.Add(_lblNetProps);

            var manual = new Label
            {
                Text = "【需人工处理】① BIOS → Chipset → PCIe Speed 设为 Auto；② CFG-8724 采集卡 4-pin 12V 电源线必须插紧；" +
                       "③ 取相仍异常时重启 IPC；④ 4-pin 供电的 Frame Grabber 需接 12V 而非仅 USB 供电。",
                Location = new Point(0, 114),
                Size = new Size(840, 46),
                ForeColor = Color.DarkOrange
            };
            top.Controls.Add(manual);

            // ---------- 底部 ----------
            var bottom = MakeBottomPanel(36);
            var btnScan = MakeButton("扫描当前状态", 0, 4, 110, 28);
            btnScan.Click += (s, e) => { try { Task.Run(() => DoNetScan()); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            bottom.Controls.Add(btnScan);

            var btnApply = MakeButton("执行选中项", 118, 4, 110, 28);
            btnApply.BackColor = Color.LightGreen;
            btnApply.Click += (s, e) =>
            {
                if (_busy) { _cts?.Cancel(); return; }
                try { Task.Run(() => DoNetApply()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            bottom.Controls.Add(btnApply);

            var btnAll = MakeButton("全选", 236, 4, 80, 28);
            btnAll.Click += (s, e) => { try { SetNetAllChecked(true); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            bottom.Controls.Add(btnAll);

            var btnNone = MakeButton("全不选", 324, 4, 90, 28);
            btnNone.Click += (s, e) => { try { SetNetAllChecked(false); } catch (Exception ex) { Log("异常: " + ex.Message); } };
            bottom.Controls.Add(btnNone);

            var btnRestart = MakeButton("重启网卡生效", 422, 4, 110, 28);
            btnRestart.Click += (s, e) =>
            {
                if (_busy) return;
                try { Task.Run(() => DoNetRestart()); }
                catch (Exception ex) { Log("异常: " + ex.Message); }
            };
            bottom.Controls.Add(btnRestart);

            bottom.Controls.AddRange(new Control[] { btnScan, btnApply, btnAll, btnNone, btnRestart });

            // ---------- 中间列表 ----------
            _lvNetItems.Dock = DockStyle.Fill;
            _lvNetItems.View = View.Details;
            _lvNetItems.FullRowSelect = true;
            _lvNetItems.GridLines = true;
            _lvNetItems.CheckBoxes = true;
            _lvNetItems.Columns.Add("项目", 300);
            _lvNetItems.Columns.Add("风险", 60);
            _lvNetItems.Columns.Add("说明", -2);

            BuildNetOptItems();
            foreach (var it in _netOptItems)
            {
                var row = new ListViewItem(it.Title);
                row.SubItems.Add(it.RiskText);
                row.SubItems.Add(it.Note);
                row.Checked = it.DefaultChecked;
                row.Tag = it;
                _lvNetItems.Items.Add(row);
            }

            host.Controls.Add(_lvNetItems);
            host.Controls.Add(bottom);
            host.Controls.Add(top);
            return p;
        }

        // ====================== 调优项定义 ======================

        private void BuildNetOptItems()
        {
            _netOptItems.Clear();

            _netOptItems.Add(new NetOptItem
            {
                Id = "jumbo",
                Title = "巨帧数据包 Jumbo Packet → 9014",
                Note = "网络适配器高级属性。9014 = 9000 负载 + 14 字节以太头，降低大包分片与丢包。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "jumbo packet", "jumbo frame", "jumbo", "巨帧", "巨型帧" },
                        NetworkTuningCore.ModeValue,
                        NetworkTuningCore.JumboFrameSize.ToString(), log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "eee",
                Title = "关闭节能以太网 EEE（Energy Efficient Ethernet）",
                Note = "关闭网卡节能，避免低负载时链路频繁协商导致取相丢帧。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "energy efficient ethernet", "eelink", "*eee", "ieee", "eee", "节能以太网", "高效以太网" },
                        NetworkTuningCore.ModeDisable, "Disabled",
                        new List<string> { "Disabled", "禁用", "关闭", "关", "Off", "No", "无" }, log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "green",
                Title = "关闭绿色以太网 Green Ethernet",
                Note = "关闭网卡节能（Green Ethernet / Energy Saving Ethernet）。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "green ethernet", "energy saving ethernet", "绿色以太网" },
                        NetworkTuningCore.ModeDisable, "Disabled",
                        new List<string> { "Disabled", "禁用", "关闭", "关", "Off", "No", "无" }, log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "psmode",
                Title = "关闭网卡节能模式 Power Saving Mode",
                Note = "部分网卡（如 Realtek）提供该节能选项，取相场景建议关闭。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "power saving mode", "power saving", "电源管理模式", "节能模式" },
                        NetworkTuningCore.ModeDisable, "Disabled",
                        new List<string> { "Disabled", "禁用", "关闭", "关", "Off", "No", "无" }, log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "rxbuf",
                Title = "Receive Buffers（接收缓冲区）→ 最大值",
                Note = "取该属性可选值中的最大值，减少高负载下的丢包。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "receive buffers", "receive buffer", "receive descriptors", "接收缓冲区", "接收缓冲" },
                        NetworkTuningCore.ModeMax, "", log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "imrate",
                Title = "Interrupt Moderation Rate（中断调节率）→ Extreme",
                Note = "中断调节设为 Extreme，降低 CPU 占用并提升吞吐稳定性。",
                Apply = (adapter, log) =>
                {
                    var r = NetworkTuningCore.SetAdvancedProperty(adapter,
                        new List<string> { "interrupt moderation", "中断调节", "中断缓和" },
                        NetworkTuningCore.ModeDisplay, "Extreme",
                        new List<string> { "Extreme", "极端", "最高", "Maximum", "Max" }, log);
                    log("  " + r.Display);
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "usb",
                Title = "电源选项：USB 选择性暂停 → Disabled",
                Note = "powercfg 关闭 USB 选择性暂停（AC 与 DC 均生效）。",
                Risk = NetRisk.Reversible,
                Apply = (adapter, log) =>
                {
                    NetworkTuningCore.SetPowerOption(NetworkTuningCore.SubUsbSelectiveSuspend,
                        NetworkTuningCore.SetUsbSelectiveSuspend, 0, log, "USB 选择性暂停 → Disabled");
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "pcie",
                Title = "电源选项：PCI Express 链接状态电源管理 → Disabled",
                Note = "powercfg 关闭 PCIe 链路省电，避免链路反复唤醒导致取相失败。",
                Risk = NetRisk.Reversible,
                Apply = (adapter, log) =>
                {
                    NetworkTuningCore.SetPowerOption(NetworkTuningCore.SubPcieLinkState,
                        NetworkTuningCore.SetPcieLinkState, 0, log, "PCI Express 链接状态电源管理 → Disabled");
                }
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "bindings",
                Title = "网络绑定：只保留 eBUS Universal Pro Driver 与 TCP/IPv4",
                Note = "取消其它协议/客户端绑定（IPv6、Bridge、文件与打印机共享等）。高危，默认不勾选。",
                Risk = NetRisk.High,
                DefaultChecked = false,
                Apply = (adapter, log) => ApplyKeepOnlyEbusAndIpv4(adapter, log)
            });

            _netOptItems.Add(new NetOptItem
            {
                Id = "third",
                Title = "禁用第三方以太网驱动（只保留 eBUS 采集卡网卡）",
                Note = "禁用非 eBUS 的物理以太网适配器。高危，默认不勾选；会断网，需现场确认。",
                Risk = NetRisk.High,
                DefaultChecked = false,
                Apply = (adapter, log) => ApplyDisableThirdPartyAdapters(adapter, log)
            });
        }

        // ====================== 具体动作 ======================

        /// <summary>网络绑定：只保留 eBUS 驱动与 TCP/IPv4，其余取消勾选。</summary>
        private void ApplyKeepOnlyEbusAndIpv4(string adapter, Action<string> log)
        {
            var bindings = NetworkTuningCore.ListBindings(adapter, log);
            if (bindings.Count == 0)
            {
                log("  未读取到绑定项（需要管理员权限）。");
                return;
            }
            int changed = 0;
            foreach (var b in bindings)
            {
                bool keep = string.Equals(b.ComponentId, "ms_tcpip", StringComparison.OrdinalIgnoreCase)
                           || b.DisplayName.IndexOf("eBUS", StringComparison.OrdinalIgnoreCase) >= 0
                           || b.ComponentId.IndexOf("eBUS", StringComparison.OrdinalIgnoreCase) >= 0;
                if (keep)
                {
                    if (!b.Enabled)
                    {
                        NetworkTuningCore.SetBinding(adapter, b.ComponentId, true, log);
                        log("  启用：" + b.DisplayName);
                        changed++;
                    }
                    else
                    {
                        log("  保留：" + b.DisplayName + "（" + b.ComponentId + "）");
                    }
                }
                else if (b.Enabled)
                {
                    NetworkTuningCore.SetBinding(adapter, b.ComponentId, false, log);
                    log("  取消绑定：" + b.DisplayName + "（" + b.ComponentId + "）");
                    changed++;
                }
            }
            log(string.Format("  网络绑定调整完成，共改动 {0} 项。", changed));
        }

        /// <summary>禁用非 eBUS 的物理以太网适配器。</summary>
        private void ApplyDisableThirdPartyAdapters(string adapter, Action<string> log)
        {
            var ads = NetworkTuningCore.ListAdapters(log);
            int n = 0;
            foreach (var a in ads)
            {
                if (a.IsEbus) { log("  保留（eBUS）：" + a.Name); continue; }
                if (string.Equals(a.Name, adapter, StringComparison.OrdinalIgnoreCase))
                {
                    log("  保留（当前目标网卡）：" + a.Name);
                    continue;
                }
                if (NetworkTuningCore.DisableAdapter(a.Name, log))
                {
                    log("  已禁用第三方网卡：" + a.Name + "（" + a.Description + "）");
                    n++;
                }
                else
                {
                    log("  禁用失败：" + a.Name);
                }
            }
            log(string.Format("  共禁用 {0} 个第三方网卡。", n));
        }

        // ====================== 执行流程 ======================

        private void DoNetScan()
        {
            try
            {
                Log("—— 扫描网络与取相相关状态 ——");
                if (!NetworkTuningCore.IsAdministrator())
                    Log("  ⚠ 当前不是管理员权限，网卡高级属性与电源方案可能无法修改，请以管理员身份运行。");

                var ads = NetworkTuningCore.ListAdapters(Log);
                InvokeOnUi(() =>
                {
                    _netAdapters.Clear();
                    _netAdapters.AddRange(ads);
                    _cmbNetAdapter.Items.Clear();
                    foreach (var a in ads) _cmbNetAdapter.Items.Add(a.Display);
                    if (_cmbNetAdapter.Items.Count > 0) _cmbNetAdapter.SelectedIndex = 0;
                });

                foreach (var a in ads)
                {
                    Log(string.Format("  · {0}  [{1}] {2}{3}", a.Name, a.Status, a.Description,
                        a.IsEbus ? "  (eBUS 采集卡网卡)" : ""));
                }
                if (ads.Count == 0)
                {
                    SetStatus("未检测到物理网卡");
                    return;
                }
                RefreshNetProps();
                SetStatus(string.Format("扫描完成，共 {0} 个网卡", ads.Count));
            }
            catch (Exception ex)
            {
                Log("扫描异常: " + ex.Message);
            }
        }

        /// <summary>读取当前选中网卡的关键属性并显示摘要。</summary>
        private void RefreshNetProps()
        {
            var a = CurrentNetAdapter();
            if (a == null) return;
            var props = NetworkTuningCore.ListAdvancedProps(a.Name, Log);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("当前网卡：" + a.Name + "  " + a.Description + "  [" + a.Status + "]");
            string[] keys = { "jumbo", "eee", "green", "power saving", "receive buffer", "interrupt moderation",
                              "巨帧", "节能", "绿色以太网", "接收缓冲", "中断调节", "电源管理" };
            var picked = new List<string>();
            foreach (var p in props)
            {
                string low = (p.DisplayName + " " + p.RegistryKeyword).ToLowerInvariant();
                foreach (var k in keys)
                {
                    if (low.Contains(k))
                    {
                        picked.Add(p.Display);
                        break;
                    }
                }
            }
            sb.AppendLine(picked.Count > 0
                ? "关键属性：" + string.Join("    |    ", picked)
                : "关键属性：未找到巨帧/节能/缓冲区等属性（该网卡可能不支持高级属性）。");
            SetNetPropsText(sb.ToString());
        }

        private void DoNetApply()
        {
            try
            {
                var a = CurrentNetAdapter();
                if (a == null)
                {
                    Log("请先选择目标网卡。");
                    return;
                }
                if (!NetworkTuningCore.IsAdministrator())
                {
                    Log("⚠ 当前不是管理员权限：部分设置会失败，请以管理员身份运行本程序。");
                }
                Log("—— 执行网络与取相调优 ——");
                Log("目标网卡：" + a.Name + "（" + a.Description + "）");

                var selected = _lvNetItems.Items.Cast<ListViewItem>()
                    .Where(r => r.Checked && r.Tag is NetOptItem)
                    .Select(r => (NetOptItem)r.Tag).ToList();
                if (selected.Count == 0)
                {
                    Log("没有勾选任何项目。");
                    SetStatus("未选择项目");
                    return;
                }

                int ok = 0, fail = 0;
                bool needRestart = false;
                foreach (var it in selected)
                {
                    Log("· " + it.Title);
                    try
                    {
                        it.Apply(a.Name, Log);
                        if (it.Id == "jumbo" || it.Id == "eee" || it.Id == "green"
                            || it.Id == "psmode" || it.Id == "rxbuf" || it.Id == "imrate")
                            needRestart = true;
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        Log("  失败: " + ex.Message);
                        fail++;
                    }
                }

                if (needRestart)
                {
                    Log("正在重启网卡以使高级属性生效（会短暂断链）...");
                    if (NetworkTuningCore.RestartAdapter(a.Name, Log))
                        Log("  网卡已重启。");
                    else
                        Log("  ⚠ 网卡重启失败，请手动重启网卡或重启电脑。");
                }

                RefreshNetProps();
                SetStatus(string.Format("调优完成：成功 {0} 项，失败 {1} 项", ok, fail));
                Log(string.Format("—— 调优结束：成功 {0} 项，失败 {1} 项 ——", ok, fail));
            }
            catch (Exception ex)
            {
                Log("执行异常: " + ex.Message);
            }
        }

        private void DoNetRestart()
        {
            try
            {
                var a = CurrentNetAdapter();
                if (a == null) { Log("请先选择目标网卡。"); return; }
                Log("正在重启网卡：" + a.Name);
                bool ok = NetworkTuningCore.RestartAdapter(a.Name, Log);
                Log(ok ? "  网卡已重启。" : "  重启失败，请检查权限。");
                SetStatus(ok ? "网卡已重启" : "网卡重启失败");
            }
            catch (Exception ex)
            {
                Log("重启网卡异常: " + ex.Message);
            }
        }

        // ====================== 辅助 ======================

        private NetworkTuningCore.NetAdapter CurrentNetAdapter()
        {
            int i = _cmbNetAdapter.SelectedIndex;
            if (i < 0 || i >= _netAdapters.Count) return null;
            return _netAdapters[i];
        }

        private void SetNetAllChecked(bool v)
        {
            foreach (ListViewItem r in _lvNetItems.Items) r.Checked = v;
        }

        private void SetNetPropsText(string text)
        {
            if (_lblNetProps.InvokeRequired)
            {
                _lblNetProps.Invoke(new Action<string>(SetNetPropsText), text);
                return;
            }
            _lblNetProps.Text = text;
        }

        private void InvokeOnUi(Action action)
        {
            if (InvokeRequired) { Invoke(action); return; }
            action();
        }
    }
}
