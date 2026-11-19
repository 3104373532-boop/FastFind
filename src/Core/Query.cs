using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FastFind
{
    internal sealed class SearchTerm
    {
        public string Text;
        public bool Wildcard;
        public bool IsRegex;
        public Regex Compiled;
        public bool Negative;
    }

    internal enum NameFilterKind
    {
        Any = 0,
        FilesOnly = 1,
        DirsOnly = 2
    }

    /// <summary>
    /// Parses Everything style queries: plain terms (AND), -exclusion, wildcards, /regex/,
    /// ext:, size:, dm:/dc:, path:, type:.
    /// </summary>
    internal sealed class SearchQuery
    {
        public readonly List<SearchTerm> Terms = new List<SearchTerm>();
        public readonly List<SearchTerm> NegativeTerms = new List<SearchTerm>();
        public readonly List<string> Extensions = new List<string>();
        public readonly List<string> NegativeExtensions = new List<string>();
        public readonly List<string> PathTerms = new List<string>();
        public readonly List<string> NegativePathTerms = new List<string>();
        public long SizeMin = -1;
        public long SizeMax = -1;
        public int MTimeMin;   // unix seconds, 0 = unset
        public int MTimeMax;
        public NameFilterKind TypeFilter = NameFilterKind.Any;
        public int TypeMask = 0;   // FileCategory flags, 0 = all
        public string Warning;

        public bool IsEmpty
        {
            get
            {
                return Terms.Count == 0 && NegativeTerms.Count == 0 && Extensions.Count == 0 &&
                       NegativeExtensions.Count == 0 && PathTerms.Count == 0 && NegativePathTerms.Count == 0 &&
                       SizeMin < 0 && SizeMax < 0 && MTimeMin == 0 && MTimeMax == 0 && TypeFilter == NameFilterKind.Any && TypeMask == 0;
            }
        }

        public bool HasPositiveNameTerm { get { return Terms.Count > 0; } }

        private static readonly RegexOptions RxOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        public static SearchQuery Parse(string text)
        {
            var q = new SearchQuery();
            if (string.IsNullOrEmpty(text)) return q;
            var tokens = Tokenize(text);
            for (int i = 0; i < tokens.Count; i++)
            {
                Token tok = tokens[i];
                if (tok.Text.Length == 0) continue;
                bool negative = false;
                string t = tok.Text;
                if (!tok.Quoted && t.Length > 1 && (t[0] == '-' || t[0] == '!'))
                {
                    negative = true;
                    t = t.Substring(1);
                }
                if (t.Length == 0) continue;

                // /regex/
                if (!tok.Quoted && t.Length > 2 && t[0] == '/' && t[t.Length - 1] == '/')
                {
                    AddRegex(q, t.Substring(1, t.Length - 2), negative);
                    continue;
                }
                if (!tok.Quoted && t.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                {
                    AddRegex(q, t.Substring(6), negative);
                    continue;
                }
                if (!tok.Quoted && t.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = t.Substring(4);
                    if (v.Length == 0) continue;
                    string[] parts = v.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int k = 0; k < parts.Length; k++)
                    {
                        string ext = parts[k].Trim().TrimStart('.');
                        if (ext.Length == 0) continue;
                        if (negative) q.NegativeExtensions.Add(ext);
                        else q.Extensions.Add(ext);
                    }
                    continue;
                }
                if (!tok.Quoted && t.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!negative) ApplySize(q, t.Substring(5));
                    else q.Warning = AppendWarning(q.Warning, "size: 不支持排除");
                    continue;
                }
                if (!tok.Quoted && (t.StartsWith("dm:", StringComparison.OrdinalIgnoreCase) ||
                                    t.StartsWith("dc:", StringComparison.OrdinalIgnoreCase) ||
                                    t.StartsWith("date:", StringComparison.OrdinalIgnoreCase)))
                {
                    int colon = t.IndexOf(':');
                    if (!negative) ApplyDate(q, t.Substring(colon + 1));
                    else q.Warning = AppendWarning(q.Warning, "日期不支持排除");
                    continue;
                }
                if (!tok.Quoted && t.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = t.Substring(5).Trim();
                    if (v.Length == 0) continue;
                    if (negative) q.NegativePathTerms.Add(v);
                    else q.PathTerms.Add(v);
                    continue;
                }
                if (!tok.Quoted && t.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = t.Substring(5).Trim().ToLowerInvariant();
                    if (v == "file" || v == "files" || v == "f") q.TypeFilter = NameFilterKind.FilesOnly;
                    else if (v == "dir" || v == "dirs" || v == "folder" || v == "folders" || v == "d") q.TypeFilter = NameFilterKind.DirsOnly;
                    else if (v != "any" && v != "*") q.Warning = AppendWarning(q.Warning, "未知类型: " + v);
                    continue;
                }

                var term = new SearchTerm();
                term.Text = tok.Quoted ? t : t;
                term.Wildcard = !tok.Quoted && (t.IndexOf('*') >= 0 || t.IndexOf('?') >= 0);
                term.Negative = negative;
                if (negative) q.NegativeTerms.Add(term);
                else q.Terms.Add(term);
            }
            return q;
        }

        private static void AddRegex(SearchQuery q, string pattern, bool negative)
        {
            if (pattern.Length == 0) return;
            try
            {
                var rx = new Regex(pattern, RxOptions, TimeSpan.FromMilliseconds(250));
                var term = new SearchTerm();
                term.Text = pattern;
                term.IsRegex = true;
                term.Compiled = rx;
                term.Negative = negative;
                if (negative) q.NegativeTerms.Add(term);
                else q.Terms.Add(term);
            }
            catch (ArgumentException)
            {
                q.Warning = AppendWarning(q.Warning, "正则无效: " + pattern);
            }
        }

        private static string AppendWarning(string current, string msg)
        {
            if (string.IsNullOrEmpty(current)) return msg;
            if (current.Length > 160) return current;
            return current + "；" + msg;
        }

        private struct Token
        {
            public string Text;
            public bool Quoted;
        }

        private static List<Token> Tokenize(string text)
        {
            var list = new List<Token>();
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            bool inToken = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    inToken = true;
                    continue;
                }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (inToken)
                    {
                        list.Add(new Token { Text = sb.ToString(), Quoted = false });
                        sb.Length = 0;
                        inToken = false;
                    }
                    continue;
                }
                inToken = true;
                if (c == '\\' && i + 1 < text.Length && !quoted)
                {
                    char next = text[i + 1];
                    if (next == '*' || next == '?' || next == '"' || next == '\\')
                    {
                        sb.Append(next);
                        i++;
                        continue;
                    }
                }
                sb.Append(c);
            }
            if (inToken)
                list.Add(new Token { Text = sb.ToString(), Quoted = quoted });
            return list;
        }

        private static void ApplySize(SearchQuery q, string raw)
        {
            if (raw.Length == 0) return;
            try
            {
                string s = raw.Trim().ToLowerInvariant();
                int range = s.IndexOf("..", StringComparison.Ordinal);
                if (range >= 0)
                {
                    string a = s.Substring(0, range);
                    string b = s.Substring(range + 2);
                    if (a.Length > 0) q.SizeMin = ParseSize(a);
                    if (b.Length > 0) q.SizeMax = ParseSize(b);
                    return;
                }
                if (s.StartsWith(">=")) { q.SizeMin = ParseSize(s.Substring(2)); return; }
                if (s.StartsWith("<=")) { q.SizeMax = ParseSize(s.Substring(2)); return; }
                if (s.StartsWith(">")) { q.SizeMin = ParseSize(s.Substring(1)) + 1; return; }
                if (s.StartsWith("<")) { q.SizeMax = ParseSize(s.Substring(1)) - 1; return; }
                long exact = ParseSize(s);
                q.SizeMin = exact;
                q.SizeMax = exact;
            }
            catch (FormatException)
            {
                q.Warning = AppendWarning(q.Warning, "size 格式无效: " + raw);
            }
        }

        private static long ParseSize(string s)
        {
            s = s.Trim();
            long mul = 1;
            if (s.EndsWith("kb")) { mul = 1024L; s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("mb")) { mul = 1024L * 1024; s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("gb")) { mul = 1024L * 1024 * 1024; s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("tb")) { mul = 1024L * 1024 * 1024 * 1024; s = s.Substring(0, s.Length - 2); }
            else if (s.EndsWith("b")) { s = s.Substring(0, s.Length - 1); }
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new FormatException();
            return (long)(v * mul);
        }

        private static void ApplyDate(SearchQuery q, string raw)
        {
            if (raw.Length == 0) return;
            try
            {
                string s = raw.Trim().ToLowerInvariant();
                DateTime today = DateTime.Today;
                if (s == "today") { q.MTimeMin = Unix(today); q.MTimeMax = Unix(today.AddDays(1)); return; }
                if (s == "yesterday") { q.MTimeMin = Unix(today.AddDays(-1)); q.MTimeMax = Unix(today); return; }
                if (s == "thisweek" || s == "week")
                {
                    int dow = (int)today.DayOfWeek;
                    if (dow == 0) dow = 7;
                    DateTime start = today.AddDays(1 - dow);
                    q.MTimeMin = Unix(start); q.MTimeMax = Unix(start.AddDays(7)); return;
                }
                if (s == "thismonth" || s == "month")
                {
                    DateTime start = new DateTime(today.Year, today.Month, 1);
                    q.MTimeMin = Unix(start); q.MTimeMax = Unix(start.AddMonths(1)); return;
                }

                int range = s.IndexOf("..", StringComparison.Ordinal);
                if (range >= 0)
                {
                    string a = s.Substring(0, range);
                    string b = s.Substring(range + 2);
                    if (a.Length > 0) q.MTimeMin = Unix(ParseDateStart(a));
                    if (b.Length > 0) q.MTimeMax = Unix(ParseDateEnd(b));
                    return;
                }
                if (s.StartsWith(">=")) { q.MTimeMin = Unix(ParseDateStart(s.Substring(2))); return; }
                if (s.StartsWith("<=")) { q.MTimeMax = Unix(ParseDateEnd(s.Substring(2))); return; }
                if (s.StartsWith(">")) { q.MTimeMin = Unix(ParseDateEnd(s.Substring(1))); return; }
                if (s.StartsWith("<")) { q.MTimeMax = Unix(ParseDateStart(s.Substring(1))); return; }
                q.MTimeMin = Unix(ParseDateStart(s));
                q.MTimeMax = Unix(ParseDateEnd(s));
            }
            catch (FormatException)
            {
                q.Warning = AppendWarning(q.Warning, "日期格式无效: " + raw);
            }
        }

        private static DateTime ParseDateStart(string s)
        {
            s = s.Trim();
            int y, m, d;
            if (s.Length == 4 && int.TryParse(s, out y))
                return new DateTime(y, 1, 1);
            if (TryParseYmd(s, out y, out m, out d))
                return new DateTime(y, m, d);
            if (s.Length == 7 && s[4] == '-')
            {
                if (int.TryParse(s.Substring(0, 4), out y) && int.TryParse(s.Substring(5, 2), out m) && m >= 1 && m <= 12)
                    return new DateTime(y, m, 1);
            }
            throw new FormatException();
        }

        private static DateTime ParseDateEnd(string s)
        {
            DateTime start = ParseDateStart(s);
            if (s.Trim().Length == 4) return start.AddYears(1);
            if (s.Trim().Length == 7 && s.Trim()[4] == '-') return start.AddMonths(1);
            return start.AddDays(1);
        }

        private static bool TryParseYmd(string s, out int y, out int m, out int d)
        {
            y = m = d = 0;
            if (s.Length != 10 || s[4] != '-' || s[7] != '-') return false;
            return int.TryParse(s.Substring(0, 4), out y) &&
                   int.TryParse(s.Substring(5, 2), out m) &&
                   int.TryParse(s.Substring(8, 2), out d) && m >= 1 && m <= 12 && d >= 1 && d <= 31;
        }

        private static int Unix(DateTime dt)
        {
            DateTime utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            long seconds = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            return IndexTime.FromUnixSeconds(seconds);
        }
    }
}


