using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace DataAssistant
{
    /// <summary>
    /// SN 图片文件夹复制核心引擎（与 UI 无关，可直接在任意程序里调用）。
    /// 支持 CSV/TXT 与 Excel(.xls/.xlsx)。匹配规则：磁盘上的文件夹名 == SN（不区分大小写）。
    /// </summary>
    public static class DataAssistantCore
    {
        // ====================== 读取 SN 列表 ======================

        /// <summary>
        /// 根据扩展名自动选择读取方式：Excel 走 NPOI，其余按 CSV/文本处理。
        /// </summary>
        public static List<string> ReadSnList(string path, bool hasHeader)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".xls" || ext == ".xlsx")
                return ReadSnListFromExcel(path, hasHeader);
            return ReadSnListFromCsv(path, hasHeader);
        }

        public static List<string> ReadSnListFromCsv(string path, bool hasHeader)
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
            return DistinctSn(sns);
        }

        public static List<string> ReadSnListFromExcel(string path, bool hasHeader)
        {
            var sns = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                IWorkbook wb = path.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
                    ? (IWorkbook)new HSSFWorkbook(fs)
                    : new XSSFWorkbook(fs);

                var sheet = wb.GetSheetAt(0);
                if (sheet == null) return sns;

                int snCol = 0;
                int start = 0;

                if (hasHeader)
                {
                    var headerRow = sheet.GetRow(0);
                    if (headerRow != null)
                    {
                        for (int i = 0; i < headerRow.LastCellNum; i++)
                        {
                            var v = GetCellText(headerRow.GetCell(i)).Trim();
                            if (string.Equals(v, "SN", StringComparison.OrdinalIgnoreCase) ||
                                v.IndexOf("SN", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                snCol = i;
                                break;
                            }
                        }
                    }
                    start = 1;
                }

                for (int r = start; r <= sheet.LastRowNum; r++)
                {
                    var row = sheet.GetRow(r);
                    if (row == null) continue;
                    var v = GetCellText(row.GetCell(snCol)).Trim();
                    if (v.Length > 0) sns.Add(v);
                }
            }
            return DistinctSn(sns);
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
                        return ((long)d).ToString();
                    return d.ToString();
                case CellType.Formula:
                    try
                    {
                        if (cell.CachedFormulaResultType == CellType.Numeric)
                        {
                            double dv = cell.NumericCellValue;
                            if (dv == Math.Floor(dv)) return ((long)dv).ToString();
                            return dv.ToString();
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

        private static List<string> DistinctSn(List<string> sns)
        {
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

        // ====================== 目录匹配 ======================

        /// <summary>从 root 递归查找“文件夹名 == sn”的所有目录（跳过无权限目录）。</summary>
        public static IEnumerable<string> FindFolders(string root, string sn)
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

        private static int CountFiles(string folder, HashSet<string> extensions)
        {
            int n = 0;
            try
            {
                foreach (var f in Directory.GetFiles(folder))
                    if (MatchExtension(f, extensions)) n++;
            }
            catch (Exception) { }
            try
            {
                foreach (var d in Directory.GetDirectories(folder))
                    n += CountFiles(d, extensions);
            }
            catch (Exception) { }
            return n;
        }

        // ====================== 扫描预览（不复制） ======================

        public class SnMatchInfo
        {
            public string Sn = string.Empty;
            public List<string> Folders = new List<string>();
            /// <summary>命中多个同名文件夹时，真正会采用（创建时间最早）的那个。</summary>
            public string ChosenFolder = string.Empty;
            public int FileCount;
            public bool Found { get { return Folders.Count > 0; } }
        }

        /// <summary>只扫描，返回每个 SN 命中的文件夹与文件数，不复制任何文件。</summary>
        public static List<SnMatchInfo> Scan(string srcRoot, IEnumerable<string> sns, HashSet<string> extensions)
        {
            var result = new List<SnMatchInfo>();
            foreach (var sn in sns)
            {
                var info = new SnMatchInfo { Sn = sn };
                foreach (var folder in FindFolders(srcRoot, sn))
                    info.Folders.Add(folder);
                if (info.Folders.Count > 0)
                {
                    info.ChosenFolder = PickEarliestFolder(info.Folders);
                    info.FileCount = CountFiles(info.ChosenFolder, extensions);
                }
                result.Add(info);
            }
            return result;
        }

        // ====================== 复制 ======================

        public class CopyResult
        {
            public int Ok;
            public int Missing;
            public int Duplicate;   // 命中多个同名文件夹的 SN 数
            public int TotalFiles;
        }

        /// <summary>按 SN 复制：目标/&lt;SN&gt;/ 下放对应文件夹的内容；log 回调用于上报进度/异常。</summary>
        public static CopyResult Copy(string srcRoot, string dstRoot, IEnumerable<string> sns, Action<string> log,
            CancellationToken token, HashSet<string> extensions)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dstRoot);
            var res = new CopyResult();
            foreach (var sn in sns)
            {
                token.ThrowIfCancellationRequested();
                var matches = FindFolders(srcRoot, sn).ToList();
                if (matches.Count == 0)
                {
                    res.Missing++;
                    log("[缺失] SN=" + sn + " 未找到对应文件夹");
                    continue;
                }
                // SN 重复（多个同名文件夹）：只采用创建时间最早的那一个
                string chosen = PickEarliestFolder(matches);
                if (matches.Count > 1)
                {
                    res.Duplicate++;
                    log("[重名] SN=" + sn + " 命中 " + matches.Count + " 个同名文件夹, 仅采用创建时间最早的: " + chosen);
                }
                var destDir = Path.Combine(dstRoot, sn);
                int copied = CopyDirectory(chosen, destDir, log, token, extensions);
                res.Ok++;
                res.TotalFiles += copied;
                log("[完成] SN=" + sn + " 复制 " + copied + " 个文件" +
                    (matches.Count > 1 ? " (已采用最早创建的 1 个文件夹)" : ""));
            }
            return res;
        }

        /// <summary>多个同名文件夹时，返回创建时间（CreationTime）最早的那一个。</summary>
        private static string PickEarliestFolder(List<string> folders)
        {
            if (folders.Count == 1) return folders[0];
            string best = folders[0];
            DateTime bestTime = GetCreationTimeSafe(best);
            for (int i = 1; i < folders.Count; i++)
            {
                DateTime t = GetCreationTimeSafe(folders[i]);
                if (t < bestTime) { bestTime = t; best = folders[i]; }
            }
            return best;
        }

        private static DateTime GetCreationTimeSafe(string folder)
        {
            try { return Directory.GetCreationTime(folder); }
            catch (Exception) { return DateTime.MaxValue; }
        }

        private static int CopyDirectory(string source, string dest, Action<string> log, CancellationToken token, HashSet<string> extensions)
        {
            int count = 0;
            bool dirReady = false;
            foreach (var file in Directory.GetFiles(source))
            {
                token.ThrowIfCancellationRequested();
                if (!MatchExtension(file, extensions)) continue;
                if (!dirReady) { Directory.CreateDirectory(dest); dirReady = true; }
                try
                {
                    File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
                    count++;
                }
                catch (Exception ex)
                {
                    log("  复制失败: " + file + " -> " + ex.Message);
                }
            }
            foreach (var dir in Directory.GetDirectories(source))
                count += CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)), log, token, extensions);
            return count;
        }

        /// <summary>
        /// 判断文件是否应被复制。
        ///   · extensions 为 null        -> 不过滤（复制全部文件）
        ///   · extensions 为空集合       -> 一个格式都没选，不复制任何文件
        ///   · 否则                       -> 仅复制扩展名在集合内的文件
        /// 比较时对扩展名去点并转小写。
        /// </summary>
        private static bool MatchExtension(string file, HashSet<string> extensions)
        {
            if (extensions == null) return true;
            if (extensions.Count == 0) return false;
            string ext = Path.GetExtension(file);
            if (ext.Length == 0) return false;
            return extensions.Contains(ext.Substring(1).ToLowerInvariant());
        }
    }
}
