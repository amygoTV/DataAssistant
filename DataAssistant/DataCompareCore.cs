using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace DataAssistant
{
    /// <summary>
    /// 数据比对核心（与 UI 无关）：
    /// 以「表格A」的 SN 顺序为主轴，从「表格B」按 SN 取出<b>多个</b>指定列的值逐行比对，并导出比对结果。
    ///
    /// 典型场景：两份测试数据（表A = 基准/期望，表B = 实测），以 A 的 SN 顺序对齐，
    /// 逐条核对若干列（如 TestResult / 判定 / 尺寸）的值是否一致。
    ///
    /// 多列配对有两种方式：
    ///   · 按列名配对（ByName）：两侧勾选的列按<b>同名</b>配对（取交集）。
    ///   · 按顺序配对（ByOrder）：两侧勾选的列按<b>勾选先后顺序</b>一一对应。
    /// </summary>
    public static class DataCompareCore
    {
        /// <summary>列配对方式。</summary>
        public enum PairMode
        {
            /// <summary>按列名配对（取两侧勾选列的同名交集）。</summary>
            ByName = 0,
            /// <summary>按勾选顺序一一配对。</summary>
            ByOrder = 1
        }

        /// <summary>比对方式。</summary>
        public enum CompareMode
        {
            /// <summary>文本精确比对。</summary>
            Text = 0,
            /// <summary>数值比对（允许误差）。</summary>
            Number = 1,
            /// <summary>包含比对：一方包含另一方即视为一致。</summary>
            Contains = 2
        }

        /// <summary>单行的比对状态。</summary>
        public enum RowStatus
        {
            Same = 0,        // 一致
            Different = 1,   // 不一致
            MissingInB = 2,  // 表A有、表B没有
            MissingInA = 3   // 表B有、表A没有
        }

        /// <summary>一对要比较的列。</summary>
        public class ColumnPair
        {
            public string ColA = string.Empty;
            public string ColB = string.Empty;

            public ColumnPair() { }
            public ColumnPair(string a, string b) { ColA = a; ColB = b; }
        }

        /// <summary>比对参数。</summary>
        public class CompareOptions
        {
            public string FileA = string.Empty;   // 表格A（基准，决定输出顺序）
            public string FileB = string.Empty;   // 表格B（被比对）
            public bool HasHeader = true;         // 首行是表头
            public string SnColA = string.Empty;  // 表A 的 SN 列
            public string SnColB = string.Empty;  // 表B 的 SN 列

            /// <summary>表A 要比对的列（可多选）。</summary>
            public List<string> ValColsA = new List<string>();
            /// <summary>表B 要比对的列（可多选）。</summary>
            public List<string> ValColsB = new List<string>();
            /// <summary>单列兼容写法：ValColsA 为空时使用。</summary>
            public string ValueColA = string.Empty;
            public string ValueColB = string.Empty;

            public PairMode PairMode = PairMode.ByName;
            public CompareMode Mode = CompareMode.Text;
            public bool IgnoreCase = true;        // 文本比对忽略大小写
            public bool TrimSpace = true;         // 比对前去掉首尾空格
            public double Tolerance = 0.000001;   // 数值比对的容差
            public bool IncludeExtraInB = true;   // 把表B中表A没有的SN追加到结果末尾
            /// <summary>true=未匹配行（只在A有/只在B有）也一并输出；默认 false = 不输出。</summary>
            public bool IncludeUnmatched;
        }

        /// <summary>一行比对结果。</summary>
        public class CompareRow
        {
            public int Index;
            public string Sn = string.Empty;
            public readonly List<string> ValuesA = new List<string>();
            public readonly List<string> ValuesB = new List<string>();
            public RowStatus Status;
            public string Note = string.Empty;

            public string StatusText
            {
                get
                {
                    switch (Status)
                    {
                        case RowStatus.Same: return "一致";
                        case RowStatus.Different: return "不一致";
                        case RowStatus.MissingInB: return "B表缺失";
                        default: return "A表缺失";
                    }
                }
            }
        }

        /// <summary>整份比对结果。</summary>
        public class CompareResultSet
        {
            public readonly List<CompareRow> Rows = new List<CompareRow>();
            public readonly List<ColumnPair> Pairs = new List<ColumnPair>();
            /// <summary>参与比对的全部行数（含未匹配）。</summary>
            public int Total;
            /// <summary>实际输出的行数（未匹配行默认被剔除）。</summary>
            public int Output;
            public int Same, Different, MissingInB, MissingInA;

            /// <summary>转成完整结果表（含序号/结果/说明，供界面内部做过滤与配色用）。</summary>
            public DataTable ToTable()
            {
                var dt = new DataTable();
                dt.Columns.Add("序号", typeof(string));
                dt.Columns.Add("SN", typeof(string));
                foreach (var p in Pairs)
                {
                    dt.Columns.Add(p.ColA + "(A)", typeof(string));
                    dt.Columns.Add(p.ColB + "(B)", typeof(string));
                }
                dt.Columns.Add("结果", typeof(string));
                dt.Columns.Add("说明", typeof(string));

                foreach (var r in Rows)
                {
                    var dr = dt.NewRow();
                    dr[0] = r.Index.ToString(CultureInfo.InvariantCulture);
                    dr[1] = r.Sn;
                    for (int i = 0; i < Pairs.Count; i++)
                    {
                        dr[2 + i * 2] = i < r.ValuesA.Count ? r.ValuesA[i] : string.Empty;
                        dr[3 + i * 2] = i < r.ValuesB.Count ? r.ValuesB[i] : string.Empty;
                    }
                    dr[dt.Columns.Count - 2] = r.StatusText;
                    dr[dt.Columns.Count - 1] = r.Note;
                    dt.Rows.Add(dr);
                }
                return dt;
            }

            /// <summary>
            /// 转成输出表：只保留 SN 与勾选配对的列（每组 A 值 + B 值），不含序号/结果/说明。
            /// 界面显示与导出都用这张表。
            /// </summary>
            public DataTable ToOutputTable()
            {
                var dt = new DataTable();
                dt.Columns.Add("SN", typeof(string));
                foreach (var p in Pairs)
                {
                    dt.Columns.Add(p.ColA + "(A)", typeof(string));
                    dt.Columns.Add(p.ColB + "(B)", typeof(string));
                }
                foreach (var r in Rows)
                {
                    var dr = dt.NewRow();
                    dr[0] = r.Sn;
                    for (int i = 0; i < Pairs.Count; i++)
                    {
                        dr[1 + i * 2] = i < r.ValuesA.Count ? r.ValuesA[i] : string.Empty;
                        dr[2 + i * 2] = i < r.ValuesB.Count ? r.ValuesB[i] : string.Empty;
                    }
                    dt.Rows.Add(dr);
                }
                return dt;
            }

            public string Summary
            {
                get
                {
                    int unmatched = MissingInB + MissingInA;
                    if (unmatched > 0 && Output != Total)
                        return string.Format("输出 {0} 行 | 一致 {1} · 不一致 {2} · 未匹配 {3}（不输出）",
                            Output, Same, Different, unmatched);
                    return string.Format("共 {0} 行 | 一致 {1} · 不一致 {2} · 缺B {3} · 缺A {4}",
                        Output, Same, Different, MissingInB, MissingInA);
                }
            }
        }

        // ====================== 主流程 ======================

        /// <summary>按两侧勾选列生成配对方案。</summary>
        public static List<ColumnPair> BuildPairs(CompareOptions opt, Action<string> log)
        {
            var listA = opt.ValColsA != null && opt.ValColsA.Count > 0
                ? opt.ValColsA.Where(c => !string.IsNullOrEmpty(c)).ToList()
                : (string.IsNullOrEmpty(opt.ValueColA) ? new List<string>() : new List<string> { opt.ValueColA });
            var listB = opt.ValColsB != null && opt.ValColsB.Count > 0
                ? opt.ValColsB.Where(c => !string.IsNullOrEmpty(c)).ToList()
                : (string.IsNullOrEmpty(opt.ValueColB) ? new List<string>() : new List<string> { opt.ValueColB });

            var pairs = new List<ColumnPair>();
            if (opt.PairMode == PairMode.ByName)
            {
                foreach (var ca in listA)
                {
                    var cb = listB.FirstOrDefault(x => string.Equals(x, ca, StringComparison.OrdinalIgnoreCase));
                    if (cb != null) pairs.Add(new ColumnPair(ca, cb));
                    else if (log != null) log("  列名配对跳过：表A的「" + ca + "」在表B中没有同名且已勾选的列。");
                }
            }
            else
            {
                int n = Math.Min(listA.Count, listB.Count);
                for (int i = 0; i < n; i++) pairs.Add(new ColumnPair(listA[i], listB[i]));
                if (listA.Count != listB.Count && log != null)
                    log(string.Format("  两侧勾选列数量不同（A {0} 个 / B {1} 个），按顺序配对 {2} 组。",
                        listA.Count, listB.Count, n));
            }
            return pairs;
        }

        /// <summary>执行比对。</summary>
        public static CompareResultSet Compare(CompareOptions opt, Action<string> log)
        {
            if (log != null) log("—— 开始数据比对 ——");

            var ta = DataAggregatorCore.LoadTable(opt.FileA, opt.HasHeader, log);
            var tb = DataAggregatorCore.LoadTable(opt.FileB, opt.HasHeader, log);

            if (!ta.Columns.Contains(opt.SnColA)) throw new ArgumentException("表格A 中找不到 SN 列：" + opt.SnColA);
            if (!tb.Columns.Contains(opt.SnColB)) throw new ArgumentException("表格B 中找不到 SN 列：" + opt.SnColB);

            var pairs = BuildPairs(opt, log);
            if (pairs.Count == 0) throw new ArgumentException("没有可配对的比对列，请先在两侧勾选要比较的列。");
            foreach (var p in pairs)
            {
                if (!ta.Columns.Contains(p.ColA)) throw new ArgumentException("表格A 中找不到列：" + p.ColA);
                if (!tb.Columns.Contains(p.ColB)) throw new ArgumentException("表格B 中找不到列：" + p.ColB);
            }
            if (log != null)
                log("  配对列：" + string.Join("；", pairs.Select(p => p.ColA + " ↔ " + p.ColB).ToArray()));

            // 表B 建立 SN 索引（同一 SN 多次出现时取第一条，并记录重复情况）
            var indexB = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
            var dupB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow r in tb.Rows)
            {
                string key = Norm(GetStr(r, opt.SnColB), opt.TrimSpace);
                if (string.IsNullOrEmpty(key)) continue;
                if (indexB.ContainsKey(key)) { dupB.Add(key); continue; }
                indexB[key] = r;
            }
            if (dupB.Count > 0 && log != null)
                log(string.Format("  注意：表B 有 {0} 个 SN 重复出现，比对时取第一条。", dupB.Count));

            var rs = new CompareResultSet();
            rs.Pairs.AddRange(pairs);

            var usedInB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snOfA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int idx = 0;

            // 主体：按表A的顺序逐行比对
            foreach (DataRow ra in ta.Rows)
            {
                string snRaw = GetStr(ra, opt.SnColA);
                string key = Norm(snRaw, opt.TrimSpace);
                if (string.IsNullOrEmpty(key)) continue;   // SN 为空的行不参与比对
                snOfA.Add(key);

                var row = new CompareRow { Index = ++idx, Sn = snRaw };
                DataRow rb;
                if (indexB.TryGetValue(key, out rb))
                {
                    usedInB.Add(key);
                    bool allSame = true;
                    var notes = new List<string>();
                    foreach (var p in pairs)
                    {
                        string va = GetStr(ra, p.ColA);
                        string vb = GetStr(rb, p.ColB);
                        row.ValuesA.Add(va);
                        row.ValuesB.Add(vb);
                        bool same = IsSame(va, vb, opt, out string note);
                        if (!same)
                        {
                            allSame = false;
                            notes.Add(string.Format("{0}↔{1}: {2}", p.ColA, p.ColB, note));
                        }
                    }
                    row.Status = allSame ? RowStatus.Same : RowStatus.Different;
                    row.Note = allSame ? "" : string.Join("；", notes.ToArray());
                    if (allSame) rs.Same++; else rs.Different++;
                }
                else
                {
                    foreach (var p in pairs)
                    {
                        row.ValuesA.Add(GetStr(ra, p.ColA));
                        row.ValuesB.Add(string.Empty);
                    }
                    row.Status = RowStatus.MissingInB;
                    row.Note = "表B中找不到该SN";
                    rs.MissingInB++;
                }
                rs.Rows.Add(row);
            }

            // 附加：表B中多出来（表A没有）的SN
            if (opt.IncludeExtraInB)
            {
                foreach (DataRow rb in tb.Rows)
                {
                    string snRaw = GetStr(rb, opt.SnColB);
                    string key = Norm(snRaw, opt.TrimSpace);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (snOfA.Contains(key)) continue;
                    if (usedInB.Contains(key)) continue;   // 已作为重复项被跳过
                    usedInB.Add(key);
                    var row = new CompareRow { Index = ++idx, Sn = snRaw };
                    foreach (var p in pairs)
                    {
                        row.ValuesA.Add(string.Empty);
                        row.ValuesB.Add(GetStr(rb, p.ColB));
                    }
                    row.Status = RowStatus.MissingInA;
                    row.Note = "表A中无此SN（表B多出）";
                    rs.Rows.Add(row);
                    rs.MissingInA++;
                }
            }

            rs.Total = rs.Rows.Count;

            // 未匹配行（只在A有/只在B有）默认不输出，但仍计入统计
            if (!opt.IncludeUnmatched)
            {
                int before = rs.Rows.Count;
                rs.Rows.RemoveAll(r => r.Status == RowStatus.MissingInB || r.Status == RowStatus.MissingInA);
                int removed = before - rs.Rows.Count;
                if (removed > 0 && log != null)
                    log(string.Format("  未匹配行 {0} 行（只在A有 {1} / 只在B有 {2}）按设置不输出。",
                        removed, rs.MissingInB, rs.MissingInA));
            }
            rs.Output = rs.Rows.Count;

            if (log != null)
            {
                log("  " + rs.Summary);
                if (rs.Different > 0)
                    log(string.Format("  不一致 {0} 行。", rs.Different));
            }
            return rs;
        }

        // ====================== 导出 ======================

        /// <summary>导出比对结果。format：CSV 或 Excel(xlsx)。</summary>
        public static string Export(CompareResultSet rs, string folder, string fileName, string format,
            Action<string> log)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException("导出文件夹不存在：" + folder);
            string ext = string.Equals(format, "Excel", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) ? ".xlsx" : ".csv";
            string path = Path.Combine(folder, (fileName ?? "比对结果") + ext);
            int n = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, (fileName ?? "比对结果") + "_" + n + ext);
                n++;
            }

            var dt = rs.ToOutputTable();   // 导出只含 SN 与勾选配对的列
            if (ext == ".xlsx") WriteExcel(dt, path);
            else WriteCsv(dt, path);

            if (log != null) log("  已导出比对结果：" + path);
            return path;
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

        private static void WriteExcel(DataTable dt, string path)
        {
            IWorkbook wb = new XSSFWorkbook();
            var sheet = wb.CreateSheet("比对结果");
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

        private static string CsvEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // ====================== 工具 ======================

        private static string GetStr(DataRow r, string col)
        {
            if (r == null || string.IsNullOrEmpty(col)) return string.Empty;
            if (!r.Table.Columns.Contains(col)) return string.Empty;
            object v = r[col];
            return v == null ? string.Empty : v.ToString();
        }

        private static string Norm(string s, bool trim)
        {
            if (s == null) return string.Empty;
            return trim ? s.Trim() : s;
        }

        /// <summary>按设定的方式判断两个值是否一致；不一致时给出说明。</summary>
        private static bool IsSame(string a, string b, CompareOptions opt, out string note)
        {
            note = "";
            string x = Norm(a, opt.TrimSpace);
            string y = Norm(b, opt.TrimSpace);

            if (opt.Mode == CompareMode.Number)
            {
                double da, db;
                bool na = double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out da);
                bool nb = double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out db);
                if (!na) na = double.TryParse(x, NumberStyles.Float, CultureInfo.CurrentCulture, out da);
                if (!nb) nb = double.TryParse(y, NumberStyles.Float, CultureInfo.CurrentCulture, out db);
                if (na && nb)
                {
                    double diff = Math.Abs(da - db);
                    if (diff <= Math.Abs(opt.Tolerance)) return true;
                    note = string.Format("数值差 {0:G6}（容差 {1:G6}）", diff, opt.Tolerance);
                    return false;
                }
                note = "非数值，按文本比对";
            }
            else if (opt.Mode == CompareMode.Contains)
            {
                StringComparison cmp = opt.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (x.Length == 0 && y.Length == 0) return true;
                if (x.IndexOf(y, cmp) >= 0 || y.IndexOf(x, cmp) >= 0) return true;
                note = "互不包含";
                return false;
            }

            StringComparison c2 = opt.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(x, y, c2)) return true;
            note = string.Format("A=「{0}」 B=「{1}」", x, y);
            return false;
        }
    }
}
