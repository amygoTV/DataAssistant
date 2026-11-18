using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace DataAssistant
{
    /// <summary>
    /// 数据整体页核心引擎（与 UI 无关，可直接在任意程序里调用）。
    /// 功能：
    ///  1) 汇总一个文件夹内所有 CSV/TXT/Excel 表格为统一 DataTable（带"源文件"溯源列）；
    ///  2) 按用户规则删除数据行（SN 列为空、SN 列像 SN/SN1/NOSN 占位符、其它列含 999/999.999 等关键字）；
    ///  3) 按用户关键字筛选数据行并导出（按原始文件分别导出 / 合并总表，尽量保持原格式）。
    /// </summary>
    public static class DataAggregatorCore
    {
        // ====================== 公共类型 ======================

        /// <summary>单个源文件的信息（含原始列顺序，用于按原格式回写）。</summary>
        public class SourceInfo
        {
            public string FullPath;
            public string[] Columns;                 // 原始列顺序
            public List<string[]> Rows = new List<string[]>();
        }

        /// <summary>聚合结果。</summary>
        public class AggregateResult
        {
            public DataTable Table = new DataTable();
            public List<SourceInfo> Sources = new List<SourceInfo>();
            public int FileCount;
            public int RowCount;
        }

        public enum FilterScope { SnColumn, AnyColumn, Column }
        public enum MatchMode { Contains, Equals, StartsWith, Regex }

        /// <summary>删除规则（默认即覆盖用户示例：SN 列为空 / SN 列像 SN/SN1/NOSN / 其它列含 999 等）。</summary>
        public class DeleteRules
        {
            public bool RemoveEmptySn = true;                      // 删除 SN 列为空的行
            public bool RemoveSnJunk = true;                       // 删除 SN 列像 SN/SN1/NOSN 的占位/表头行
            public string SnJunkPattern = "SN,SN1,NOSN";          // 逗号分隔的占位符原文，整格精确匹配（忽略大小写）
            public bool RemoveByKeywordOtherColumns = true;        // 删除"除 SN 外"的其它列含指定关键字的行
            public string Keywords = "999,999.999";                // 默认关键字（仅用英文逗号 , 分隔）
            public string KeywordColumn = string.Empty;            // 关键字匹配的目标列：空=全部其它列；填列名=只匹配该列（含 SN 列本身）
            public bool RemoveDuplicateSn;                         // 删除 SN 重复的行（同一个 SN 只保留一条）
            public bool DuplicateKeepLast;                         // false=保留第一条（默认）；true=保留最后一条
            public bool CaseSensitive;
        }

        public class ExportOptions
        {
            public string ExportFolder;
            public string Keywords = string.Empty;
            public FilterScope Scope = FilterScope.AnyColumn;
            public MatchMode Mode = MatchMode.Contains;
            public bool CaseSensitive;
            public bool PreservePerFile = true;     // 按原始文件分别导出（保持原格式/原名）
            public bool Combined;                   // 同时导出合并总表
            public string CombinedFormat = "CSV";   // CSV 或 Excel(xlsx)
            public string SnColumnName;             // 当 Scope=SnColumn 时使用
            public string ColumnName;               // 当 Scope=Column 时使用（指定要筛选的列）
        }

        // ====================== 聚合：读取文件夹内所有表格 ======================

        public static AggregateResult AggregateFolder(string folder, bool hasHeader, bool includeSubdirs,
            Action<string> log, Action<int, int> progress)
        {
            var result = new AggregateResult();
            if (!Directory.Exists(folder))
            {
                log("数据文件夹不存在: " + folder);
                return result;
            }

            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv", ".txt", ".xls", ".xlsx" };
            var files = new List<string>();
            var dirs = includeSubdirs
                ? SafeEnumerateDirectories(folder)
                : new List<string> { folder };

            foreach (var d in dirs)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d))
                        if (exts.Contains(Path.GetExtension(f))) files.Add(f);
                }
                catch (Exception) { /* 无权限目录跳过 */ }
            }
            files.Sort(StringComparer.OrdinalIgnoreCase);

            int done = 0;
            foreach (var f in files)
            {
                try
                {
                    ReadTableFile(f, hasHeader, result);
                    log("已读取: " + Path.GetFileName(f) + " (" + result.Sources[result.Sources.Count - 1].Rows.Count + " 行)");
                }
                catch (Exception ex)
                {
                    log("读取失败: " + Path.GetFileName(f) + " -> " + ex.Message);
                }
                done++;
                progress?.Invoke(done, files.Count);
            }

            BuildTable(result);
            result.FileCount = result.Sources.Count;
            result.RowCount = result.Table.Rows.Count;
            log(string.Format("聚合完成。共 {0} 个文件，{1} 行数据，{2} 列。",
                result.FileCount, result.RowCount, result.Table.Columns.Count - 1));
            return result;
        }

        private static List<string> SafeEnumerateDirectories(string root)
        {
            var list = new List<string> { root };
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(cur))
                    {
                        list.Add(d);
                        stack.Push(d);
                    }
                }
                catch (Exception) { /* 无权限跳过 */ }
            }
            return list;
        }

        private static void ReadTableFile(string path, bool hasHeader, AggregateResult agg)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string[] header;
            List<string[]> rows;
            if (ext == ".xls" || ext == ".xlsx")
                ReadExcel(path, hasHeader, out header, out rows);
            else
                ReadCsv(path, hasHeader, out header, out rows);

            agg.Sources.Add(new SourceInfo { FullPath = path, Columns = header, Rows = rows });
        }

        /// <summary>把各源文件的行合并为统一 DataTable（列取并集，首列恒为"源文件"）。</summary>
        private static void BuildTable(AggregateResult agg)
        {
            var union = new List<string>();
            foreach (var s in agg.Sources)
                foreach (var col in s.Columns)
                    if (!union.Contains(col)) union.Add(col);

            var dt = new DataTable();
            dt.Columns.Add("源文件", typeof(string));
            foreach (var col in union) dt.Columns.Add(col, typeof(string));

            foreach (var s in agg.Sources)
            {
                foreach (var row in s.Rows)
                {
                    var dr = dt.NewRow();
                    dr["源文件"] = s.FullPath;
                    for (int j = 0; j < s.Columns.Length; j++)
                    {
                        var colName = s.Columns[j];
                        if (dt.Columns.Contains(colName))
                            dr[colName] = j < row.Length ? (row[j] ?? string.Empty) : string.Empty;
                    }
                    dt.Rows.Add(dr);
                }
            }
            agg.Table = dt;
        }

        // ====================== SN 列识别 ======================

        /// <summary>在聚合表中找出 SN 列名（列名含 "SN"，忽略大小写，排除"源文件"）；找不到返回 null。</summary>
        public static string DetectSnColumn(DataTable table)
        {
            foreach (DataColumn c in table.Columns)
            {
                if (string.Equals(c.ColumnName, "源文件", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.ColumnName.IndexOf("SN", StringComparison.OrdinalIgnoreCase) >= 0)
                    return c.ColumnName;
            }
            return null;
        }

        // ====================== 删除规则 ======================

        /// <summary>按规则就地删除数据行，返回删除行数。</summary>
        public static int DeleteByRules(AggregateResult agg, DeleteRules rules, string snCol, Action<string> log)
        {
            var dt = agg.Table;
            int idxSn = (snCol != null && dt.Columns.Contains(snCol)) ? dt.Columns[snCol].Ordinal : -1;
            var reasons = ComputeDeleteReasons(dt, rules, idxSn);
            var toDelete = new List<DataRow>();
            for (int i = 0; i < dt.Rows.Count; i++)
            {
                if (reasons[i] != null) toDelete.Add(dt.Rows[i]);
            }
            foreach (var r in toDelete) dt.Rows.Remove(r);
            agg.RowCount = dt.Rows.Count;

            log(string.Format("已删除 {0} 行（剩余 {1} 行）。", toDelete.Count, agg.RowCount));
            return toDelete.Count;
        }

        /// <summary>
        /// 预览删除：与应用删除完全相同的判定逻辑，但不真正删除，只返回每一行被删的原因（null 表示保留）。
        /// 原因分类：空SN / SN占位符(值) / 列[X]含关键字(值) / 其它列含关键字(值) / SN重复(值)。
        /// </summary>
        public static string[] GetDeleteReasons(AggregateResult agg, DeleteRules rules, string snCol)
        {
            var dt = agg.Table;
            int idxSn = (snCol != null && dt.Columns.Contains(snCol)) ? dt.Columns[snCol].Ordinal : -1;
            return ComputeDeleteReasons(dt, rules, idxSn);
        }

        /// <summary>
        /// 计算每一行是否会被删除以及删除原因（null = 保留）。
        /// 「应用删除」与「预览删除」共用此方法，保证两者结果完全一致。
        /// </summary>
        private static string[] ComputeDeleteReasons(DataTable dt, DeleteRules rules, int idxSn)
        {
            var kwList = SplitKeywords(rules.Keywords);
            var junkTokens = SplitKeywords(rules.SnJunkPattern);
            var reasons = new string[dt.Rows.Count];
            var marked = new HashSet<DataRow>();

            int i = 0;
            foreach (DataRow r in dt.Rows)
            {
                string reason = null;
                string snVal = idxSn >= 0 ? (r[idxSn] as string ?? string.Empty) : string.Empty;

                if (rules.RemoveEmptySn && string.IsNullOrWhiteSpace(snVal))
                    reason = "空SN";
                else if (rules.RemoveSnJunk && idxSn >= 0 && snVal.Length > 0)
                {
                    // 占位符为逗号分隔的原文列表，整格精确匹配（忽略大小写、首尾空格）即视为占位符
                    string s = snVal.Trim();
                    foreach (var j in junkTokens)
                    {
                        if (string.Equals(s, j, rules.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        { reason = "SN占位符(" + s + ")"; break; }
                    }
                }

                if (reason == null && rules.RemoveByKeywordOtherColumns && kwList.Length > 0)
                {
                    if (!string.IsNullOrWhiteSpace(rules.KeywordColumn) && dt.Columns.Contains(rules.KeywordColumn))
                    {
                        // 指定列：只匹配该列（可包含 SN 列本身）
                        int c = dt.Columns[rules.KeywordColumn].Ordinal;
                        string v = r[c] as string ?? string.Empty;
                        if (Matches(v, kwList, MatchMode.Contains, rules.CaseSensitive))
                            reason = "列[" + rules.KeywordColumn + "]含关键字(" + string.Join("/", kwList) + ")";
                    }
                    else
                    {
                        // 未指定列：匹配除 SN 外的所有列
                        for (int c = 0; c < dt.Columns.Count; c++)
                        {
                            if (c == idxSn) continue;
                            string v = r[c] as string ?? string.Empty;
                            if (Matches(v, kwList, MatchMode.Contains, rules.CaseSensitive))
                            { reason = "其它列含关键字(" + string.Join("/", kwList) + ")"; break; }
                        }
                    }
                }

                reasons[i] = reason;
                if (reason != null) marked.Add(r);
                i++;
            }

            // 去重：同一个 SN 只保留一条（空 SN 交给"空SN"规则处理，不参与去重）
            if (rules.RemoveDuplicateSn && idxSn >= 0)
            {
                var cmp = rules.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                var groups = new Dictionary<string, List<int>>(cmp);
                var order = new List<string>();
                int idx = 0;
                foreach (DataRow r in dt.Rows)
                {
                    if (!marked.Contains(r))
                    {
                        string key = (r[idxSn] as string ?? string.Empty).Trim();
                        if (key.Length > 0)
                        {
                            if (!groups.ContainsKey(key))
                            {
                                groups[key] = new List<int>();
                                order.Add(key);
                            }
                            groups[key].Add(idx);
                        }
                    }
                    idx++;
                }

                foreach (var key in order)
                {
                    var list = groups[key];
                    if (list.Count <= 1) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        // 保留最后一条 => 删前面的；保留第一条 => 删后面的
                        bool del = rules.DuplicateKeepLast ? (k < list.Count - 1) : (k > 0);
                        if (del)
                        {
                            reasons[list[k]] = "SN重复(" + key + ")";
                            marked.Add(dt.Rows[list[k]]);
                        }
                    }
                }
            }

            return reasons;
        }

        // ====================== 筛选 ======================

        /// <summary>按关键字筛选，返回新的 DataTable（保留原表结构），并通过 matchCount 返回命中行数。</summary>
        public static DataTable Filter(AggregateResult agg, ExportOptions opt, out int matchCount)
        {
            var dt = agg.Table;
            // 关键字为空：表示导出当前全部数据（例如"删除规则应用后"的剩余行），不必填写筛选关键字。
            if (string.IsNullOrWhiteSpace(opt.Keywords))
            {
                var all = dt.Clone();
                foreach (DataRow r in dt.Rows) all.ImportRow(r);
                matchCount = dt.Rows.Count;
                return all;
            }
            var kwList = SplitKeywords(opt.Keywords);
            int idxSn = -1;
            if (opt.Scope == FilterScope.SnColumn)
            {
                if (opt.SnColumnName != null && dt.Columns.Contains(opt.SnColumnName))
                    idxSn = dt.Columns[opt.SnColumnName].Ordinal;
            }
            else if (opt.Scope == FilterScope.Column)
            {
                // 指定某一列筛选
                if (opt.ColumnName != null && dt.Columns.Contains(opt.ColumnName))
                    idxSn = dt.Columns[opt.ColumnName].Ordinal;
            }

            var result = dt.Clone();
            matchCount = 0;
            foreach (DataRow r in dt.Rows)
            {
                bool hit = false;
                if (idxSn >= 0)
                {
                    hit = Matches(r[idxSn] as string ?? string.Empty, kwList, opt.Mode, opt.CaseSensitive);
                }
                else
                {
                    foreach (DataColumn c in dt.Columns)
                    {
                        if (Matches(r[c] as string ?? string.Empty, kwList, opt.Mode, opt.CaseSensitive)) { hit = true; break; }
                    }
                }
                if (hit) { result.ImportRow(r); matchCount++; }
            }
            return result;
        }

        // ====================== 导出 ======================

        public static void Export(AggregateResult agg, ExportOptions opt, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(opt.ExportFolder))
            {
                log("请先选择导出文件夹。");
                return;
            }
            Directory.CreateDirectory(opt.ExportFolder);

            DataTable filtered = Filter(agg, opt, out int matchCount);
            if (matchCount == 0)
            {
                log("没有匹配关键字的行，未写出任何文件。");
                return;
            }

            int written = 0;
            if (opt.PreservePerFile)
            {
                var groups = new Dictionary<string, List<DataRow>>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRow r in filtered.Rows)
                {
                    string key = r["源文件"] as string ?? string.Empty;
                    if (!groups.TryGetValue(key, out var list))
                    {
                        list = new List<DataRow>();
                        groups[key] = list;
                    }
                    list.Add(r);
                }
                foreach (var kv in groups)
                {
                    var src = agg.Sources.FirstOrDefault(s => string.Equals(s.FullPath, kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (src == null) continue;

                    var sub = filtered.Clone();
                    foreach (var r in kv.Value) sub.ImportRow(r);

                    string ext = Path.GetExtension(src.FullPath).ToLowerInvariant();
                    string baseName = Path.GetFileNameWithoutExtension(src.FullPath);
                    string outPath = UniquePath(Path.Combine(opt.ExportFolder, baseName + ext));
                    WriteFile(sub, src.Columns, ext, outPath);
                    written++;
                }
                log(string.Format("按原始文件分别导出: {0} 个文件。", written));
            }

            if (opt.Combined)
            {
                string ext = opt.CombinedFormat.Equals("CSV", StringComparison.OrdinalIgnoreCase) ? ".csv" : ".xlsx";
                string outPath = UniquePath(Path.Combine(opt.ExportFolder, "汇总_过滤结果" + ext));
                var colNames = new List<string>();
                foreach (DataColumn c in filtered.Columns) colNames.Add(c.ColumnName);
                WriteFile(filtered, colNames.ToArray(), ext, outPath);
                log("导出合并总表: " + outPath);
                written++;
            }

            log(string.Format("导出完成。匹配 {0} 行，共写出 {1} 个文件到: {2}",
                matchCount, written, opt.ExportFolder));
        }

        // ====================== 写入文件 ======================

        private static void WriteFile(DataTable src, string[] columns, string ext, string path)
        {
            var dt = new DataTable();
            foreach (var c in columns)
                if (src.Columns.Contains(c)) dt.Columns.Add(c, typeof(string));

            foreach (DataRow r in src.Rows)
            {
                var nr = dt.NewRow();
                foreach (DataColumn c in dt.Columns)
                    nr[c.ColumnName] = r[c.ColumnName] as string ?? string.Empty;
                dt.Rows.Add(nr);
            }

            if (ext == ".xls" || ext == ".xlsx")
                WriteExcel(dt, path, ext == ".xls");
            else
                WriteCsv(dt, path);
        }

        private static void WriteCsv(DataTable dt, string path)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(CsvEncode(dt.Columns[c].ColumnName));
            }
            sb.Append("\r\n");
            foreach (DataRow r in dt.Rows)
            {
                for (int c = 0; c < dt.Columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(CsvEncode(r[c] as string ?? string.Empty));
                }
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void WriteExcel(DataTable dt, string path, bool isXls)
        {
            IWorkbook wb = isXls ? (IWorkbook)new HSSFWorkbook() : new XSSFWorkbook();
            var sheet = wb.CreateSheet("数据");
            var header = sheet.CreateRow(0);
            for (int c = 0; c < dt.Columns.Count; c++)
                header.CreateCell(c).SetCellValue(dt.Columns[c].ColumnName);

            int r = 1;
            foreach (DataRow row in dt.Rows)
            {
                var wr = sheet.CreateRow(r++);
                for (int c = 0; c < dt.Columns.Count; c++)
                    wr.CreateCell(c).SetCellValue(row[c] as string ?? string.Empty);
            }
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                wb.Write(fs);
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int i = 1;
            while (File.Exists(Path.Combine(dir, name + "_" + i + ext))) i++;
            return Path.Combine(dir, name + "_" + i + ext);
        }

        // ====================== 匹配工具 ======================

        private static bool Matches(string value, string[] keywords, MatchMode mode, bool cs)
        {
            if (value == null) value = string.Empty;
            StringComparison cmp = cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var kw in keywords)
            {
                if (kw.Length == 0) continue;
                bool hit;
                if (mode == MatchMode.Regex)
                {
                    try { hit = Regex.IsMatch(value, kw, cs ? RegexOptions.None : RegexOptions.IgnoreCase); }
                    catch { hit = false; }
                }
                else if (mode == MatchMode.Equals)
                    hit = string.Equals(value, kw, cmp);
                else if (mode == MatchMode.StartsWith)
                    hit = cs ? value.StartsWith(kw) : value.StartsWith(kw, StringComparison.OrdinalIgnoreCase);
                else
                    hit = value.IndexOf(kw, cmp) >= 0;
                if (hit) return true;
            }
            return false;
        }

        private static string[] SplitKeywords(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new string[0];
            return s.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();
        }

        // ====================== 读取：CSV / TXT ======================

        /// <summary>
        /// 读取单个表格文件（.csv/.txt/.xls/.xlsx）为 DataTable（全部列按 string 存放）。
        /// 供「数据比对」等功能复用同一套编码识别与 Excel 解析逻辑。
        /// </summary>
        public static DataTable LoadTable(string path, bool hasHeader, Action<string> log)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string[] header;
            List<string[]> rows;
            if (ext == ".xls" || ext == ".xlsx")
                ReadExcel(path, hasHeader, out header, out rows);
            else
                ReadCsv(path, hasHeader, out header, out rows);

            var dt = new DataTable();
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in header)
            {
                string name = string.IsNullOrWhiteSpace(h) ? "列" : h.Trim();
                if (used.ContainsKey(name))
                {
                    used[name] = used[name] + 1;
                    name = name + "_" + used[name];
                }
                else used[name] = 0;
                dt.Columns.Add(name, typeof(string));
            }
            if (dt.Columns.Count == 0) dt.Columns.Add("列1", typeof(string));

            foreach (var row in rows)
            {
                var dr = dt.NewRow();
                for (int j = 0; j < dt.Columns.Count; j++)
                    dr[j] = j < row.Length ? (row[j] ?? string.Empty) : string.Empty;
                dt.Rows.Add(dr);
            }
            if (log != null)
                log(string.Format("  读取 {0}：{1} 列 / {2} 行", Path.GetFileName(path), dt.Columns.Count, dt.Rows.Count));
            return dt;
        }

        private static void ReadCsv(string path, bool hasHeader, out string[] header, out List<string[]> rows)
        {
            header = new string[0];
            rows = new List<string[]>();
            var lines = ReadAllLinesSafe(path);
            if (lines.Length == 0) return;
            char delim = DetectDelimiter(lines[0]);

            if (hasHeader)
            {
                header = ParseCsvLine(lines[0], delim);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    rows.Add(ParseCsvLine(lines[i], delim));
                }
            }
            else
            {
                int maxCols = 0;
                var parsed = new List<string[]>();
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    var f = ParseCsvLine(lines[i], delim);
                    parsed.Add(f);
                    if (f.Length > maxCols) maxCols = f.Length;
                }
                header = new string[maxCols];
                for (int i = 0; i < maxCols; i++) header[i] = "列" + (i + 1);
                rows = parsed;
            }
        }

        /// <summary>读取文本：优先按 BOM/UTF-8，若大量出现替换符则回退系统默认编码(中文 GBK)。</summary>
        private static string[] ReadAllLinesSafe(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string txt;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                txt = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }
            else
            {
                txt = Encoding.UTF8.GetString(bytes);
                int bad = 0;
                foreach (char ch in txt) if (ch == '\uFFFD') bad++;
                if (bad > bytes.Length / 50) txt = Encoding.Default.GetString(bytes);
            }
            return txt.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // ====================== 读取：Excel ======================

        private static void ReadExcel(string path, bool hasHeader, out string[] header, out List<string[]> rows)
        {
            header = new string[0];
            rows = new List<string[]>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                IWorkbook wb = path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
                    ? (IWorkbook)new HSSFWorkbook(fs)
                    : new XSSFWorkbook(fs);
                var sheet = wb.GetSheetAt(0);
                if (sheet == null) return;

                int start = 0;
                if (hasHeader)
                {
                    var hr = sheet.GetRow(0);
                    if (hr != null)
                    {
                        var list = new List<string>();
                        for (int i = 0; i < hr.LastCellNum; i++) list.Add(GetCellText(hr.GetCell(i)));
                        header = list.ToArray();
                    }
                    start = 1;
                }
                else
                {
                    int maxCols = 0;
                    for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
                    {
                        var row = sheet.GetRow(r);
                        if (row != null && row.LastCellNum > maxCols) maxCols = row.LastCellNum;
                    }
                    header = new string[maxCols];
                    for (int i = 0; i < maxCols; i++) header[i] = "列" + (i + 1);
                }

                for (int r = start; r <= sheet.LastRowNum; r++)
                {
                    var row = sheet.GetRow(r);
                    if (row == null) continue;
                    var arr = new string[header.Length];
                    for (int c = 0; c < header.Length; c++) arr[c] = GetCellText(row.GetCell(c));
                    rows.Add(arr);
                }
            }
        }

        private static string GetCellText(ICell cell)
        {
            if (cell == null) return string.Empty;
            switch (cell.CellType)
            {
                case CellType.Numeric:
                    if (DateUtil.IsCellDateFormatted(cell))
                        return cell.DateCellValue.ToString("yyyy-MM-dd");
                    double d = cell.NumericCellValue;
                    if (d == Math.Floor(d) && !double.IsInfinity(d))
                        return ((long)d).ToString(CultureInfo.InvariantCulture);
                    return d.ToString(CultureInfo.InvariantCulture);
                case CellType.Formula:
                    try
                    {
                        if (cell.CachedFormulaResultType == CellType.Numeric)
                        {
                            double dv = cell.NumericCellValue;
                            if (dv == Math.Floor(dv)) return ((long)dv).ToString(CultureInfo.InvariantCulture);
                            return dv.ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    catch { }
                    return cell.StringCellValue ?? string.Empty;
                case CellType.Boolean:
                    return cell.BooleanCellValue ? "TRUE" : "FALSE";
                default:
                    return cell.ToString() ?? string.Empty;
            }
        }

        // ====================== CSV 解析（与 DataAssistantCore 同款） ======================

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
            var sb = new StringBuilder();
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

        private static string CsvEncode(string s)
        {
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\r") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
