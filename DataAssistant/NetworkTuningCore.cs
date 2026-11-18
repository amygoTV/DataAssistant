using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DataAssistant
{
    /// <summary>
    /// 「取相非正常失败（Buffer retrieve failed）」对策调优核心（与 UI 无关）。
    ///
    /// 依据 Cognex《"取相非正常失败" issues to improve》文档 + 工业相机取相稳定性实践，实现：
    ///   1. 巨帧数据包(Jumbo Packet) → 9014
    ///   2. 关闭网卡节能(EEE / Green Ethernet / Power Saving Mode)
    ///   3. 电源选项：USB 选择性暂停 → Disabled；PCI Express 链接状态电源管理 → Disabled
    ///   4. 网络绑定：只保留 eBUS Universal Pro Driver 与 TCP/IPv4
    ///   5. Receive Buffers → 最大值
    ///   6. Interrupt Moderation Rate → Extreme
    ///   7. 禁用第三方以太网驱动（可选，高危）
    ///
    /// 实现方式：网卡高级属性走 NetAdapter 模块（Get/Set-NetAdapterAdvancedProperty），
    /// 电源方案走 powercfg，全部为系统原生接口，不改注册表里驱动私有结构。
    /// </summary>
    public static class NetworkTuningCore
    {
        // ============ 电源方案 GUID ============
        // USB 选择性暂停：子组 2a737441... / 设置 48e6b7a6...
        public const string SubUsbSelectiveSuspend = "2a737441-1930-4402-8d77-b2bebba308a3";
        public const string SetUsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        // PCI Express 链接状态电源管理：子组 501a4d13...(GUID 别名 SUB_PCIEXPRESS) / 设置 ee12f906...(别名 ASPM)
        public const string SubPcieLinkState = "501a4d13-42af-4429-9fd1-a8218c268e20";
        public const string SetPcieLinkState = "ee12f906-d277-404b-b6da-e5fa1a576df5";

        /// <summary>Jumbo Packet 目标值（9000 负载 + 14 字节以太头 = 9014）。</summary>
        public const int JumboFrameSize = 9014;

        // ============ 数据模型 ============

        public class NetAdapter
        {
            public string Name = string.Empty;
            public string Description = string.Empty;
            public string Status = string.Empty;
            public string MacAddress = string.Empty;
            public string DriverInfo = string.Empty;

            /// <summary>是否 eBUS Universal Pro 驱动（Cognex 采集卡网卡）。</summary>
            public bool IsEbus
            {
                get { return Name.IndexOf("eBUS", StringComparison.OrdinalIgnoreCase) >= 0
                           || Description.IndexOf("eBUS", StringComparison.OrdinalIgnoreCase) >= 0
                           || DriverInfo.IndexOf("eBUS", StringComparison.OrdinalIgnoreCase) >= 0; }
            }

            public string Display
            {
                get { return Name + (IsEbus ? "  [eBUS]" : ""); }
            }
        }

        /// <summary>网卡高级属性。</summary>
        public class AdvProp
        {
            public string DisplayName = string.Empty;
            public string DisplayValue = string.Empty;
            public string RegistryKeyword = string.Empty;
            public string RegistryValue = string.Empty;

            public string Display
            {
                get
                {
                    return string.Format("{0} = {1}", DisplayName,
                        string.IsNullOrEmpty(DisplayValue) ? RegistryValue : DisplayValue);
                }
            }
        }

        /// <summary>网卡绑定项（网络连接属性里的协议/客户端）。</summary>
        public class NetBinding
        {
            public string DisplayName = string.Empty;
            public string ComponentId = string.Empty;
            public bool Enabled;

            public string Display
            {
                get { return (Enabled ? "[√] " : "[ ] ") + DisplayName + "  (" + ComponentId + ")"; }
            }
        }

        /// <summary>一次设置动作的结果。</summary>
        public class TuneResult
        {
            public bool Ok;
            public string Property = string.Empty;
            public string Before = string.Empty;
            public string After = string.Empty;
            public string Message = string.Empty;

            public string Display
            {
                get
                {
                    if (!Ok) return "失败：" + Message;
                    if (string.IsNullOrEmpty(Before) && string.IsNullOrEmpty(After)) return "完成";
                    return string.Format("{0}：{1} → {2}", Property, Before, After);
                }
            }
        }

        // ============ 电源方案 ============

        /// <summary>把某个电源子组/设置项在 AC 与 DC 下都设为指定值（0=Disabled）。</summary>
        public static bool SetPowerOption(string subgroupGuid, string settingGuid, int value,
            Action<string> log, string title)
        {
            bool ok = true;
            string[] cmds =
            {
                string.Format("/setacvalueindex SCHEME_CURRENT {0} {1} {2}", subgroupGuid, settingGuid, value),
                string.Format("/setdcvalueindex SCHEME_CURRENT {0} {1} {2}", subgroupGuid, settingGuid, value)
            };
            foreach (var c in cmds)
            {
                if (!RunCmd("powercfg.exe", c, log, title + "（电源方案写入）")) ok = false;
            }
            // 让改动立即生效
            RunCmd("powercfg.exe", "/setactive SCHEME_CURRENT", log, title + "（应用电源方案）");

            // powercfg 对部分隐藏设置项会报「指定的资源请求类型不被支持」，改用注册表直写兜底
            if (!ok)
            {
                if (SetPowerOptionRegistry(subgroupGuid, settingGuid, value, log))
                    ok = true;
            }

            if (log != null)
                log(ok ? string.Format("  [完成] {0} → {1}", title, value == 0 ? "Disabled" : value.ToString())
                        : string.Format("  [部分失败] {0}，请确认以管理员身份运行", title));
            return ok;
        }

        /// <summary>注册表兜底：直接写入当前电源方案下的隐藏设置项。</summary>
        private static bool SetPowerOptionRegistry(string subgroupGuid, string settingGuid, int value,
            Action<string> log)
        {
            try
            {
                string scheme = GetActiveSchemeGuid();
                if (string.IsNullOrEmpty(scheme)) return false;
                string path = string.Format(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{0}\{1}\{2}",
                    scheme, subgroupGuid, settingGuid);
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(path))
                {
                    if (key == null) return false;
                    key.SetValue("", value, Microsoft.Win32.RegistryValueKind.DWord);
                }
                if (log != null) log("  已用注册表兜底写入电源方案: " + path);
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("  注册表兜底失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>取当前电源方案 GUID。</summary>
        private static string GetActiveSchemeGuid()
        {
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", "/getactivescheme")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.Default
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    string outp = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                    var m = System.Text.RegularExpressions.Regex.Match(outp,
                        @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch (Exception) { return null; }
        }

        // ============ 网卡枚举与查询 ============

        /// <summary>枚举物理网络适配器。</summary>
        public static List<NetAdapter> ListAdapters(Action<string> log)
        {
            var list = new List<NetAdapter>();
            foreach (var row in RunPs("adapters", null, log))
            {
                if (row.Length < 3) continue;
                list.Add(new NetAdapter
                {
                    Name = row[0],
                    Description = row[1],
                    Status = row[2],
                    MacAddress = row.Length > 3 ? row[3] : "",
                    DriverInfo = row.Length > 4 ? row[4] : ""
                });
            }
            if (log != null) log(string.Format("  检测到 {0} 个物理网卡。", list.Count));
            return list;
        }

        /// <summary>读取指定网卡的高级属性。</summary>
        public static List<AdvProp> ListAdvancedProps(string adapter, Action<string> log)
        {
            var list = new List<AdvProp>();
            var payload = new Dictionary<string, string> { { "adapter", adapter } };
            foreach (var row in RunPs("props", payload, log))
            {
                if (row.Length < 4) continue;
                list.Add(new AdvProp
                {
                    DisplayName = row[0],
                    DisplayValue = row[1],
                    RegistryKeyword = row[2],
                    RegistryValue = row[3]
                });
            }
            return list;
        }

        /// <summary>列出网卡绑定项。</summary>
        public static List<NetBinding> ListBindings(string adapter, Action<string> log)
        {
            var list = new List<NetBinding>();
            var payload = new Dictionary<string, string> { { "adapter", adapter } };
            foreach (var row in RunPs("bindings", payload, log))
            {
                if (row.Length < 3) continue;
                list.Add(new NetBinding
                {
                    DisplayName = row[0],
                    ComponentId = row[1],
                    Enabled = string.Equals(row[2], "True", StringComparison.OrdinalIgnoreCase)
                });
            }
            return list;
        }

        // ============ 网卡高级属性写入 ============

        /// <summary>设置模式：按显示值匹配（如 Disabled / Extreme）。</summary>
        public const string ModeDisplay = "display";
        /// <summary>设置模式：按注册表数值设置（如 9014）。</summary>
        public const string ModeValue = "value";
        /// <summary>设置模式：取该属性可选值中的最大值（用于 Receive Buffers）。</summary>
        public const string ModeMax = "max";
        /// <summary>设置模式：取"禁用"语义的值；驱动无对应显示值时回退为 0。</summary>
        public const string ModeDisable = "disable";

        /// <summary>
        /// 设置网卡高级属性。matchers 为关键字候选（同时匹配 RegistryKeyword 与 DisplayName，忽略大小写）。
        /// </summary>
        public static TuneResult SetAdvancedProperty(string adapter, IList<string> matchers,
            string mode, string value, Action<string> log)
        {
            return SetAdvancedProperty(adapter, matchers, mode, value, null, log);
        }

        /// <summary>
        /// 设置网卡高级属性。values 为可接受的显示值候选（逗号/竖线分隔的多个，Unicode 安全地经临时文件传入）。
        /// </summary>
        public static TuneResult SetAdvancedProperty(string adapter, IList<string> matchers,
            string mode, string value, IList<string> values, Action<string> log)
        {
            var payload = new Dictionary<string, string>
            {
                { "adapter", adapter },
                { "mode", mode },
                { "value", value ?? "" },
                { "matchers", string.Join("|", matchers ?? new List<string>()) },
                { "values", values != null && values.Count > 0 ? string.Join("|", values) : "" }
            };
            var rows = RunPs("setprop", payload, log);
            var r = new TuneResult();
            if (rows.Count == 0 || rows[0].Length < 1)
            {
                r.Ok = false;
                r.Message = "未执行（可能是权限不足或模块不可用）";
                return r;
            }
            // 格式：R|ok|property|before|after|message
            var row = rows[0];
            r.Ok = string.Equals(row[0], "True", StringComparison.OrdinalIgnoreCase);
            r.Property = row.Length > 1 ? row[1] : "";
            r.Before = row.Length > 2 ? row[2] : "";
            r.After = row.Length > 3 ? row[3] : "";
            r.Message = row.Length > 4 ? row[4] : "";
            return r;
        }

        /// <summary>启用/禁用某个网卡绑定项。</summary>
        public static bool SetBinding(string adapter, string componentId, bool enabled, Action<string> log)
        {
            var payload = new Dictionary<string, string>
            {
                { "adapter", adapter },
                { "component", componentId },
                { "enabled", enabled ? "True" : "False" }
            };
            var rows = RunPs("setbinding", payload, log);
            bool ok = rows.Count > 0 && rows[0].Length > 0
                      && string.Equals(rows[0][0], "True", StringComparison.OrdinalIgnoreCase);
            if (!ok && log != null)
                log("  绑定项设置失败：" + (rows.Count > 0 && rows[0].Length > 1 ? rows[0][1] : "未知错误"));
            return ok;
        }

        // ============ 网卡启用/禁用/重启 ============

        public static bool DisableAdapter(string adapter, Action<string> log)
        {
            var payload = new Dictionary<string, string> { { "adapter", adapter } };
            var rows = RunPs("disableadapter", payload, log);
            return rows.Count > 0 && rows[0].Length > 0
                   && string.Equals(rows[0][0], "True", StringComparison.OrdinalIgnoreCase);
        }

        public static bool EnableAdapter(string adapter, Action<string> log)
        {
            var payload = new Dictionary<string, string> { { "adapter", adapter } };
            var rows = RunPs("enableadapter", payload, log);
            return rows.Count > 0 && rows[0].Length > 0
                   && string.Equals(rows[0][0], "True", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>重启网卡以让高级属性生效（会短暂断链）。</summary>
        public static bool RestartAdapter(string adapter, Action<string> log)
        {
            var payload = new Dictionary<string, string> { { "adapter", adapter } };
            var rows = RunPs("restartadapter", payload, log);
            return rows.Count > 0 && rows[0].Length > 0
                   && string.Equals(rows[0][0], "True", StringComparison.OrdinalIgnoreCase);
        }

        // ============ PowerShell 调用封装 ============

        /// <summary>执行 PowerShell 脚本，返回按 '|' 拆分的行结果。</summary>
        private static List<string[]> RunPs(string op, Dictionary<string, string> payload, Action<string> log)
        {
            var rows = new List<string[]>();
            string scriptPath = null, inPath = null, outPath = null;
            try
            {
                scriptPath = WriteTempFile("netset_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ps1", PsScript);
                inPath = WriteTempFile("netset_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt",
                    BuildPayload(op, payload));
                outPath = WriteTempFile("netset_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".out", string.Empty);

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath +
                    "\" \"" + inPath + "\" \"" + outPath + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    // PowerShell 的 stderr 走控制台 ANSI 代码页，按系统默认编码读才不会乱码
                    StandardErrorEncoding = Encoding.Default
                };
                // 注意：不要用 psi.EnvironmentVariables 传参 —— 本机环境变量里同时存在 Path/PATH，
                // .NET Framework 复制环境变量字典会抛「已添加项」异常。改用命令行参数传文件路径。

                string so = string.Empty, se = string.Empty;
                using (var p = Process.Start(psi))
                {
                    if (p == null) return rows;
                    so = p.StandardOutput.ReadToEnd();
                    se = p.StandardError.ReadToEnd();
                    p.WaitForExit(90000);
                    if (!string.IsNullOrWhiteSpace(se) && log != null)
                        log("  PowerShell: " + se.Trim());
                }

                if (File.Exists(outPath))
                {
                    foreach (var line in File.ReadAllLines(outPath, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        rows.Add(line.Split('|'));
                    }
                }
                if (rows.Count == 0 && !string.IsNullOrWhiteSpace(so) && log != null)
                    log("  PowerShell 输出: " + so.Trim());
            }
            catch (Exception ex)
            {
                if (log != null) log("  执行 PowerShell 失败: " + ex.Message);
            }
            finally
            {
                SafeDelete(scriptPath); SafeDelete(inPath); SafeDelete(outPath);
            }
            return rows;
        }

        private static string BuildPayload(string op, Dictionary<string, string> payload)
        {
            var sb = new StringBuilder();
            sb.AppendLine("op=" + op);
            if (payload != null)
            {
                foreach (var kv in payload)
                    sb.AppendLine(kv.Key + "=" + (kv.Value ?? string.Empty));
            }
            return sb.ToString();
        }

        private static string WriteTempFile(string name, string content)
        {
            string path = Path.Combine(Path.GetTempPath(), name);
            // UTF-8 带 BOM：Windows PowerShell 5.1 读取 .ps1 时按 BOM 判定编码，避免中文乱码
            File.WriteAllText(path, content, new UTF8Encoding(true));
            return path;
        }

        private static void SafeDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        private static bool RunCmd(string file, string args, Action<string> log, string title)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string outp = p.StandardOutput.ReadToEnd();
                    string errp = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    if (log != null)
                    {
                        if (!string.IsNullOrWhiteSpace(errp)) log("  " + title + " 输出: " + errp.Trim());
                        else if (!string.IsNullOrWhiteSpace(outp)) log("  " + title + ": " + outp.Trim());
                    }
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("  " + title + " 失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>是否以管理员身份运行（网络高级属性与 powercfg 需要）。</summary>
        public static bool IsAdministrator()
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

        // ============ PowerShell 脚本（纯 ASCII，避免编码问题） ============
        // 入参：$args[0] = 载荷文件(key=value 行)，$args[1] = 结果输出文件（'|' 分隔的行）
        private const string PsScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
function Clean([string]$s) { if ($null -eq $s) { return '' }; return ($s -replace '[|]', '/') -replace '[\r\n]', ' ' }
function FirstVal($v) { if ($null -eq $v) { return '' }; if ($v -is [array]) { if ($v.Count -eq 0) { return '' }; return $v[0] }; return $v }
$req = @{}
$inFile = ''
$outFile = ''
if ($args.Count -ge 1) { $inFile = [string]$args[0] }
if ($args.Count -ge 2) { $outFile = [string]$args[1] }
if ($inFile -and (Test-Path -LiteralPath $inFile)) {
  foreach ($line in (Get-Content -LiteralPath $inFile -Encoding UTF8)) {
    $i = $line.IndexOf('=')
    if ($i -gt 0) { $req[$line.Substring(0, $i).Trim()] = $line.Substring($i + 1) }
  }
}
$op = [string]$req['op']
$out = New-Object System.Collections.Generic.List[string]
try {
  if ($op -eq 'adapters') {
    $ads = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | Where-Object { $_.HardwareInterface }
    foreach ($a in $ads) {
      $di = ''
      try { $di = [string]$a.DriverInformation } catch { }
      $out.Add((Clean $a.Name) + '|' + (Clean $a.InterfaceDescription) + '|' + (Clean ([string]$a.Status)) + '|' + (Clean ([string]$a.MacAddress)) + '|' + (Clean $di))
    }
  }
  elseif ($op -eq 'props') {
    $name = [string]$req['adapter']
    $props = Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue
    foreach ($p in $props) {
      $out.Add((Clean $p.DisplayName) + '|' + (Clean ([string]$p.DisplayValue)) + '|' + (Clean ([string]$p.RegistryKeyword)) + '|' + (Clean ([string]$p.RegistryValue)))
    }
  }
  elseif ($op -eq 'setprop') {
    $name = [string]$req['adapter']
    $mode = [string]$req['mode']
    $want = [string]$req['value']
    $matchers = @()
    if ($req.ContainsKey('matchers') -and $req['matchers']) { $matchers = @($req['matchers'] -split '[|]') }
    $target = $null
    foreach ($p in (Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)) {
      $hit = $false
      foreach ($m in $matchers) {
        $ml = ([string]$m).ToLower()
        if ($ml -eq '') { continue }
        if ($p.RegistryKeyword -and ([string]$p.RegistryKeyword).ToLower().Contains($ml)) { $hit = $true; break }
        if ($p.DisplayName -and ([string]$p.DisplayName).ToLower().Contains($ml)) { $hit = $true; break }
      }
      if ($hit) { $target = $p; break }
    }
    if ($null -eq $target) {
      $msg = 'no property matched (keywords: ' + ($matchers -join ',') + ')'
      $out.Add('False||' + (Clean $msg) + '|')
    }
    else {
      $before = [string](FirstVal $target.DisplayValue)
      if (-not $before) { $before = [string](FirstVal $target.RegistryValue) }
      $newValue = $null
      if ($mode -eq 'max') {
        $best = $null
        foreach ($v in @($target.ValidRegistryValues)) {
          $n = 0
          if ([int]::TryParse([string]$v, [ref]$n)) { if ($null -eq $best -or $n -gt $best) { $best = $n } }
        }
        if ($null -ne $best) { $newValue = $best }
        else {
          # 部分驱动（如 Intel I219）不暴露可选值列表，退化为「试探 + 解析驱动报错的范围上限」
          $cur = 0
          foreach ($p3 in (Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)) {
            if ($p3.RegistryKeyword -eq $target.RegistryKeyword) { $cur = [int](FirstVal $p3.RegistryValue); break }
          }
          $cands = @(32768, 16384, 8192, 4096, 2048, 1024, 512, 256)
          $rangeMax = 0
          foreach ($cv in $cands) {
            if ($cv -le $cur) { break }
            try {
              Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $target.RegistryKeyword -RegistryValue ([string]$cv) -NoRestart -ErrorAction Stop
            } catch {
              # 驱动会提示允许范围（本地化文案，通用地抓「数字-数字」），直接取上限
              $mm = [regex]::Match($_.Exception.Message, '(\d+)\s*-\s*(\d+)')
              if ($mm.Success) {
                $a1 = [int]$mm.Groups[1].Value
                $a2 = [int]$mm.Groups[2].Value
                $rangeMax = [Math]::Max($a1, $a2)
              }
              break
            }
            $ok2 = $false
            foreach ($p4 in (Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)) {
              if ($p4.RegistryKeyword -eq $target.RegistryKeyword) { if ([int](FirstVal $p4.RegistryValue) -eq $cv) { $ok2 = $true }; break }
            }
            if ($ok2) { $newValue = $cv; break }
          }
          if ($null -eq $newValue -and $rangeMax -gt 0) {
            try {
              Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $target.RegistryKeyword -RegistryValue ([string]$rangeMax) -NoRestart -ErrorAction Stop
              $newValue = $rangeMax
            } catch { $newValue = $cur }
          }
          if ($null -eq $newValue) { $newValue = $cur }
        }
      }
      elseif ($mode -eq 'display' -or $mode -eq 'disable') {
        $cands = @()
        if ($req.ContainsKey('values') -and $req['values']) { $cands = @($req['values'] -split '[|]') }
        if ($cands.Count -eq 0 -and $want -ne '') { $cands = @($want) }
        $vd = @($target.ValidDisplayValues)
        $vr = @($target.ValidRegistryValues)
        for ($i = 0; $i -lt $vd.Count; $i++) {
          foreach ($c in $cands) {
            if (([string]$vd[$i]).Equals([string]$c, [StringComparison]::OrdinalIgnoreCase)) { $newValue = $vr[$i]; break }
          }
          if ($null -ne $newValue) { break }
        }
        if ($null -eq $newValue) {
          for ($i = 0; $i -lt $vd.Count; $i++) {
            foreach ($c in $cands) {
              if (([string]$vd[$i]).ToLower().Contains(([string]$c).ToLower())) { $newValue = $vr[$i]; break }
            }
            if ($null -ne $newValue) { break }
          }
        }
        if ($null -eq $newValue -and $mode -eq 'disable') { $newValue = 0 }
        if ($null -eq $newValue) {
          $valid = (@($target.ValidDisplayValues) -join ', ')
          $out.Add('False|' + (Clean $target.DisplayName) + '|' + (Clean $before) + '|' + (Clean '') + '|' + (Clean ('target value not supported by this driver. available: ' + $valid)))
        }
      }
      else {
        $n = 0
        if ([int]::TryParse($want, [ref]$n)) { $newValue = $n }
      }
      if ($null -eq $newValue) {
        $valid = (@($target.ValidDisplayValues) -join ', ')
        $out.Add('False|' + (Clean $target.DisplayName) + '|' + (Clean $before) + '|' + (Clean '') + '|' + (Clean ('target value not supported by this driver. available: ' + $valid)))
      }
      else {
        try {
          Set-NetAdapterAdvancedProperty -Name $name -RegistryKeyword $target.RegistryKeyword -RegistryValue ([string]$newValue) -NoRestart -ErrorAction Stop
          $after = ''
          foreach ($p2 in (Get-NetAdapterAdvancedProperty -Name $name -AllProperties -ErrorAction SilentlyContinue)) {
            if ($p2.RegistryKeyword -eq $target.RegistryKeyword) {
              if ($p2.DisplayValue) { $after = [string](FirstVal $p2.DisplayValue) } else { $after = [string](FirstVal $p2.RegistryValue) }
              break
            }
          }
          $out.Add('True|' + (Clean $target.DisplayName) + '|' + (Clean $before) + '|' + (Clean $after) + '|')
        } catch {
          $out.Add('False|' + (Clean $target.DisplayName) + '|' + (Clean $before) + '|' + (Clean '') + '|' + (Clean $_.Exception.Message))
        }
      }
    }
  }
  elseif ($op -eq 'bindings') {
    $name = [string]$req['adapter']
    foreach ($b in (Get-NetAdapterBinding -Name $name -ErrorAction SilentlyContinue)) {
      $out.Add((Clean $b.DisplayName) + '|' + (Clean ([string]$b.ComponentID)) + '|' + (Clean ([string]$b.Enabled)))
    }
  }
  elseif ($op -eq 'setbinding') {
    $name = [string]$req['adapter']
    $comp = [string]$req['component']
    $en = ([string]$req['enabled']) -eq 'True'
    try {
      if ($en) { Enable-NetAdapterBinding -Name $name -ComponentID $comp -ErrorAction Stop }
      else { Disable-NetAdapterBinding -Name $name -ComponentID $comp -ErrorAction Stop }
      $out.Add('True||')
    } catch {
      $out.Add('False||' + (Clean $_.Exception.Message))
    }
  }
  elseif ($op -eq 'disableadapter') {
    try {
      Disable-NetAdapter -Name ([string]$req['adapter']) -Confirm:$false -ErrorAction Stop
      $out.Add('True||')
    } catch { $out.Add('False||' + (Clean $_.Exception.Message)) }
  }
  elseif ($op -eq 'enableadapter') {
    try {
      Enable-NetAdapter -Name ([string]$req['adapter']) -Confirm:$false -ErrorAction Stop
      $out.Add('True||')
    } catch { $out.Add('False||' + (Clean $_.Exception.Message)) }
  }
  elseif ($op -eq 'restartadapter') {
    $name = [string]$req['adapter']
    try {
      Restart-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop
      $out.Add('True||')
    } catch {
      try {
        Disable-NetAdapter -Name $name -Confirm:$false -ErrorAction SilentlyContinue
        Enable-NetAdapter -Name $name -Confirm:$false -ErrorAction SilentlyContinue
        $out.Add('True||')
      } catch { $out.Add('False||' + (Clean $_.Exception.Message)) }
    }
  }
}
catch {
  $out.Add('False||' + (Clean $_.Exception.Message))
}
$outFile = $args[1]
if ($outFile) { $out | Set-Content -LiteralPath $outFile -Encoding UTF8 }
";
    }
}
