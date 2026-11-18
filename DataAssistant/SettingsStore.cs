using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace DataAssistant
{
    /// <summary>
    /// 界面设置的通用持久化容器（XML 格式）。
    ///
    /// 设计要点：
    ///   · 存盘为可读、可手工编辑的 XML（settings.xml），按「节(Section)」分组，各面板互不干扰
    ///   · 每个键存成 Item 元素的 key/value 属性；列表存成 List 元素的多个 Value 子元素
    ///     —— 值里可以含中文、竖线、逗号、等号等任意字符，XML 会自行转义，不会有分隔符歧义
    ///   · 读取时任何异常都吞掉并返回默认值 —— 配置文件损坏绝不能让程序起不来
    ///   · 缺字段自动回落默认值 → 旧版本配置文件能被新版本正常读取（向后兼容）
    ///   · 首次遇到旧版 base64 的 ui_settings.cfg 时自动导入并改名备份，用户设置不丢
    ///   · 文件路径类默认不存（用户明确要求）：记住旧路径反而容易误操作到过期目录。
    ///     例外：定时备份/定时删图的路径必须记住，否则定时任务无法工作。
    /// </summary>
    public class SettingsStore
    {
        /// <summary>标量值：Section.Key -> 值。</summary>
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>列表值：Section.Key -> 多个值。</summary>
        private readonly Dictionary<string, List<string>> _lists =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 配置文件所在目录。
        /// 优先放在 exe 启动目录（便携：随程序走，便于 U 盘/绿色版携带）；
        /// 若启动目录不可写（例如装进 Program Files 且非管理员），则退回 %APPDATA%\DataAssistant。
        /// 环境变量 DATAASSISTANT_CONFIG_DIR 可强制覆盖（测试用）。
        /// </summary>
        public static string ConfigDir
        {
            get
            {
                if (_resolvedDir != null) return _resolvedDir;

                // 1) 环境变量强制指定（测试/便携场景）
                string overrideDir = Environment.GetEnvironmentVariable("DATAASSISTANT_CONFIG_DIR");
                if (!string.IsNullOrEmpty(overrideDir)) { _resolvedDir = overrideDir; return _resolvedDir; }

                // 2) 优先 exe 启动目录（AppDomain.BaseDirectory 即程序所在目录）
                string exeDir = null;
                try { exeDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                if (!string.IsNullOrEmpty(exeDir) && IsWritableDir(exeDir)) { _resolvedDir = exeDir; return _resolvedDir; }

                // 3) 退回 %APPDATA%\DataAssistant（保证设置永不静默丢失）
                _resolvedDir = OptimizerConfig.ConfigDir;
                return _resolvedDir;
            }
        }

        private static string _resolvedDir;

        /// <summary>探测目录是否可写（不存在则尝试创建）。仅执行一次（ConfigDir 已缓存）。</summary>
        private static bool IsWritableDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".__da_wtest__");
                File.WriteAllText(probe, "t");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>界面设置配置文件路径（XML）。</summary>
        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDir, "settings.xml"); }
        }

        /// <summary>旧版配置文件路径（base64 的 .cfg），仅用于一次性迁移。</summary>
        private static string LegacyConfigPath
        {
            get { return Path.Combine(ConfigDir, "ui_settings.cfg"); }
        }

        /// <summary>节名分隔符：面板用各自的节，避免键名冲突。</summary>
        public const char SectionSep = '.';

        // ====================== 写入 ======================

        /// <summary>设置一个字符串值。</summary>
        public void Set(string section, string key, string value)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return;
            _values[section + SectionSep + key] = value ?? string.Empty;
        }

        /// <summary>设置一个布尔值。</summary>
        public void SetBool(string section, string key, bool value)
        {
            Set(section, key, value ? "1" : "0");
        }

        /// <summary>设置一个整数值。</summary>
        public void SetInt(string section, string key, int value)
        {
            Set(section, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>设置一个字符串列表。</summary>
        public void SetList(string section, string key, IList<string> values)
        {
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key)) return;
            string full = section + SectionSep + key;
            var list = new List<string>();
            if (values != null)
                foreach (var v in values) list.Add(v ?? string.Empty);
            _lists[full] = list;
            // 列表与标量互斥：清掉可能存在的同名标量，避免读回时混淆
            _values.Remove(full);
        }

        // ====================== 读取 ======================

        /// <summary>读取字符串值；不存在返回默认值。</summary>
        public string Get(string section, string key, string defaultValue)
        {
            string v;
            if (_values.TryGetValue(section + SectionSep + key, out v)) return v;
            return defaultValue;
        }

        /// <summary>读取布尔值；不存在或非法返回默认值。</summary>
        public bool GetBool(string section, string key, bool defaultValue)
        {
            string v = Get(section, key, null);
            if (v == null) return defaultValue;
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>读取整数值；不存在或非法返回默认值。</summary>
        public int GetInt(string section, string key, int defaultValue)
        {
            string v = Get(section, key, null);
            int r;
            if (v != null && int.TryParse(v, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out r))
                return r;
            return defaultValue;
        }

        /// <summary>
        /// 读取字符串列表；不存在返回空列表。
        /// 兼容旧版：若该键在旧配置里是以 \u0001 连接的标量（base64 时代），这里也能拆开还原。
        /// </summary>
        public List<string> GetList(string section, string key)
        {
            string full = section + SectionSep + key;
            List<string> list;
            if (_lists.TryGetValue(full, out list)) return new List<string>(list);

            // 旧格式兜底
            string v;
            if (_values.TryGetValue(full, out v) && !string.IsNullOrEmpty(v))
                return new List<string>(v.Split('\u0001'));

            return new List<string>();
        }

        /// <summary>某节是否存在（用于判断是否首次运行）。</summary>
        public bool HasSection(string section)
        {
            string prefix = section + SectionSep;
            foreach (var k in _values.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var k in _lists.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ====================== 存盘 / 读盘 ======================

        /// <summary>从磁盘读取全部设置。文件不存在或损坏时返回空容器（调用方用默认值）。</summary>
        public static SettingsStore Load()
        {
            var store = new SettingsStore();
            try
            {
                // 首次运行：把 %APPDATA% 下的旧配置、以及旧版 base64 的 .cfg 迁进来
                MigrateFromAppData();
                if (!File.Exists(ConfigPath))
                {
                    ImportLegacy(store);
                    return store;
                }
                ReadXml(store, ConfigPath);
            }
            catch (Exception) { }
            return store;
        }

        /// <summary>写盘。异常静默忽略 —— 保存失败不该影响用户正在做的事。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    Encoding = new UTF8Encoding(true),   // 带 BOM，中文 Windows 记事本打开不乱码
                    OmitXmlDeclaration = false
                };
                using (var w = XmlWriter.Create(ConfigPath, settings))
                {
                    w.WriteStartDocument();
                    w.WriteComment(" 数据助手 - 界面设置 ");
                    w.WriteComment(" 程序关闭时自动保存，下次启动自动恢复。可手工编辑；删掉本文件即恢复出厂设置。 ");
                    w.WriteStartElement("Settings");

                    // 按节分组输出，节名排序，便于阅读
                    var sections = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    var listSections = new SortedDictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in _values)
                    {
                        int i = kv.Key.IndexOf(SectionSep);
                        if (i <= 0) continue;
                        string sec = kv.Key.Substring(0, i);
                        string key = kv.Key.Substring(i + 1);
                        Dictionary<string, string> d;
                        if (!sections.TryGetValue(sec, out d)) { d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); sections[sec] = d; }
                        d[key] = kv.Value;
                    }
                    foreach (var kv in _lists)
                    {
                        int i = kv.Key.IndexOf(SectionSep);
                        if (i <= 0) continue;
                        string sec = kv.Key.Substring(0, i);
                        string key = kv.Key.Substring(i + 1);
                        Dictionary<string, List<string>> d;
                        if (!listSections.TryGetValue(sec, out d)) { d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase); listSections[sec] = d; }
                        d[key] = kv.Value;
                    }

                    var allSections = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in sections.Keys) allSections.Add(s);
                    foreach (var s in listSections.Keys) allSections.Add(s);

                    foreach (var sec in allSections)
                    {
                        w.WriteStartElement("Section");
                        w.WriteAttributeString("name", sec);

                        Dictionary<string, string> d;
                        if (sections.TryGetValue(sec, out d))
                        {
                            var keys = new List<string>(d.Keys);
                            keys.Sort(StringComparer.OrdinalIgnoreCase);
                            foreach (var k in keys)
                            {
                                w.WriteStartElement("Item");
                                w.WriteAttributeString("key", k);
                                w.WriteAttributeString("value", d[k] ?? string.Empty);
                                w.WriteEndElement();
                            }
                        }

                        Dictionary<string, List<string>> ld;
                        if (listSections.TryGetValue(sec, out ld))
                        {
                            var keys = new List<string>(ld.Keys);
                            keys.Sort(StringComparer.OrdinalIgnoreCase);
                            foreach (var k in keys)
                            {
                                w.WriteStartElement("List");
                                w.WriteAttributeString("name", k);
                                foreach (var v in ld[k])
                                {
                                    w.WriteStartElement("Value");
                                    w.WriteString(v ?? string.Empty);
                                    w.WriteEndElement();
                                }
                                w.WriteEndElement();
                            }
                        }

                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
            }
            catch (Exception) { }
        }

        // ====================== XML 读取 ======================

        private static void ReadXml(SettingsStore store, string path)
        {
            var doc = new XmlDocument();
            doc.Load(path);
            var root = doc.DocumentElement;
            if (root == null) return;
            foreach (XmlNode secNode in root.ChildNodes)
            {
                if (secNode.NodeType != XmlNodeType.Element) continue;
                string sec = GetAttr(secNode, "name");
                if (string.IsNullOrEmpty(sec)) continue;

                foreach (XmlNode n in secNode.ChildNodes)
                {
                    if (n.NodeType != XmlNodeType.Element) continue;
                    if (n.Name == "Item")
                    {
                        string k = GetAttr(n, "key");
                        if (string.IsNullOrEmpty(k)) continue;
                        store._values[sec + SectionSep + k] = GetAttr(n, "value") ?? string.Empty;
                    }
                    else if (n.Name == "List")
                    {
                        string k = GetAttr(n, "name");
                        if (string.IsNullOrEmpty(k)) continue;
                        var list = new List<string>();
                        foreach (XmlNode v in n.ChildNodes)
                        {
                            if (v.NodeType != XmlNodeType.Element) continue;
                            list.Add(v.InnerText ?? string.Empty);
                        }
                        store._lists[sec + SectionSep + k] = list;
                    }
                }
            }
        }

        private static string GetAttr(XmlNode node, string name)
        {
            if (node.Attributes == null) return null;
            var a = node.Attributes[name];
            return a == null ? null : a.Value;
        }

        // ====================== 迁移 ======================

        private static bool _migrated;

        /// <summary>
        /// 早期版本把 ui_settings.cfg 放在 %APPDATA%\DataAssistant（现在默认放启动目录）。
        /// 首次运行时把旧的搬过来，避免用户已保存的界面设置丢失。仅执行一次。
        /// </summary>
        private static void MigrateFromAppData()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string oldPath = Path.Combine(appData, "DataAssistant", "ui_settings.cfg");
                if (File.Exists(oldPath) && !File.Exists(LegacyConfigPath) && !File.Exists(ConfigPath))
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(LegacyConfigPath)); } catch { }
                    File.Copy(oldPath, LegacyConfigPath, false);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 旧版配置的最后一次兼容：ui_settings.cfg 是「Section.Key=base64」的行格式。
        /// 这里解码后灌进新容器，随后把旧文件改名备份，避免用户改错文件产生困惑。
        /// </summary>
        private static void ImportLegacy(SettingsStore store)
        {
            try
            {
                string legacy = LegacyConfigPath;
                if (!File.Exists(legacy)) return;

                foreach (var raw in File.ReadAllLines(legacy, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string key = line.Substring(0, idx);
                    string b64 = line.Substring(idx + 1);
                    if (key.Length == 0) continue;
                    string val = string.Empty;
                    try { val = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
                    catch (Exception) { val = b64; }   // 解码失败就当明文
                    store._values[key] = val;
                }

                // 导入完改名备份，之后一律用 settings.xml
                try { File.Move(legacy, legacy + ".bak"); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// 可持久化界面设置的控件：把自己的状态写入 SettingsStore / 从中恢复。
    /// 每个面板实现一份，主窗体在启动时统一 Load、退出时统一 Save。
    /// </summary>
    public interface ISettingsAware
    {
        /// <summary>设置在配置文件里的节名（各面板必须唯一）。</summary>
        string SettingsSection { get; }

        /// <summary>把当前界面状态写入存储。</summary>
        void SaveSettings(SettingsStore store);

        /// <summary>从存储恢复界面状态；存储里没有对应项时保持默认值不变。</summary>
        void LoadSettings(SettingsStore store);
    }
}
