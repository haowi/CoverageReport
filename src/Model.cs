// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace CoverageReport;

/// <summary>A function as the coverage tool reports it.</summary>
sealed class Method
{
    public required string Name;
    public int Line;
    public int EndLine;  // 0 if the coverage tool does not report it
    public long Hits;
}

/// <summary>A covered/total pair, such as covered lines out of instrumented lines.</summary>
readonly record struct Counts(long Covered, long Total)
{
    public static Counts operator +(Counts a, Counts b) => new(a.Covered + b.Covered, a.Total + b.Total);

    /// <summary>The coverage in percent, or <c>null</c> if nothing was measured. Shown as "n/a", never as 0 %.</summary>
    public double? Percent => Total == 0 ? null : 100.0 * Covered / Total;
}

/// <summary>Everything known about one source file: its coverage data, merged from all inputs, and its source text.</summary>
sealed class FileCoverage(string path)
{
    public const int MaxOrigins = 16;

    public string Path { get; } = PathUtil.Normalize(path);
    public string RawPath { get; } = PathUtil.StripControl(path);  // as written, so a leading ../ is kept
    public SortedDictionary<int, long> Lines { get; } = [];
    public SortedDictionary<int, List<long>> Branches { get; } = [];
    public Dictionary<string, Method> Methods { get; } = [];
    public bool HasBranchData;
    public bool HasMethodData;
    public string[]? Source;
    public bool SourceFromCoverageFile;
    public bool SourceMatchedByNameOnly;
    public string? SourceProblem;
    public SourceMetrics? Metrics;

    /// <summary>Set if the branch data was estimated from line coverage and source code, not measured.</summary>
    public bool BranchesEstimated;

    /// <summary>Set if the method data was estimated from line coverage and source code, not measured.</summary>
    public bool MethodsEstimated;

    /// <summary>Decisions the estimate found but could not resolve, by line, each with the reason.</summary>
    public SortedDictionary<int, List<string>> Unevaluable { get; } = [];

    public int DecisionsNotEvaluable => Unevaluable.Values.Sum(reasons => reasons.Count);

    /// <summary>
    /// Every spelling of this file's path found in the inputs, with the folder of the coverage file that used it,
    /// in the order seen. Capped at <see cref="MaxOrigins"/>: a hostile input can spell one path in countless ways
    /// (<c>/p/a.c</c>, <c>/p/./a.c</c>, ...), and only the first few are worth trying.
    /// </summary>
    public List<(string Path, string Folder)> Origins { get; } = [];

    HashSet<Method>? _fullyCovered;
    RangeIndex? _ranges;

    public void AddLine(int line, long hits)
    {
        Lines[line] = Saturate(Lines.GetValueOrDefault(line), hits);
        Invalidate();
    }

    /// <summary>Adds the taken counts of a line's branches.</summary>
    /// <remarks>
    /// Limitation: branches are merged by their position on the line, which assumes that all inputs come from the
    /// same build.
    /// </remarks>
    public void AddBranches(int line, IReadOnlyList<long> taken)
    {
        if (!Branches.TryGetValue(line, out var list))
            Branches[line] = list = [];
        for (var i = 0; i < taken.Count; i++)
        {
            if (i < list.Count)
                list[i] = Saturate(list[i], taken[i]);
            else
                list.Add(taken[i]);
        }
        Invalidate();
    }

    public void AddMethod(string rawName, int line, int endLine, long hits) =>
        MergeMethod(Demangler.Demangle(PathUtil.StripControl(rawName)), line, endLine, hits);

    /// <summary>Adds the outcomes of an estimated decision after those already on the line.</summary>
    /// <remarks>
    /// Unlike <see cref="AddBranches"/>, which merges the same branches from several inputs, this appends: two
    /// decisions on one line, such as an if and a loop, have different branches.
    /// </remarks>
    public void AddEstimatedBranches(int line, IReadOnlyList<long> taken)
    {
        if (!Branches.TryGetValue(line, out var list))
            Branches[line] = list = [];
        list.AddRange(taken);
        Invalidate();
    }

    /// <summary>Adds a function found by the source analysis as an estimated method.</summary>
    /// <remarks>
    /// Overloads share a name in the source, so the key is the function's position in the analysis instead.
    /// Estimates are only made when no input has method data, so no measured method can be met.
    /// </remarks>
    public void AddEstimatedMethod(int index, string name, int line, int endLine, long hits)
    {
        Methods[$"estimated:{index}"] = new Method { Name = name, Line = line, EndLine = endLine, Hits = hits };
        Invalidate();
    }

    public void AddUnevaluable(int line, string reason)
    {
        if (!Unevaluable.TryGetValue(line, out var reasons))
            Unevaluable[line] = reasons = [];
        reasons.Add(reason);
    }

    /// <summary>Adds the data of another input for the same file.</summary>
    public void Merge(FileCoverage other)
    {
        foreach (var (line, hits) in other.Lines)
            AddLine(line, hits);
        foreach (var (line, taken) in other.Branches)
            AddBranches(line, taken);
        foreach (var m in other.Methods.Values)
            MergeMethod(m.Name, m.Line, m.EndLine, m.Hits);
        HasBranchData |= other.HasBranchData;
        HasMethodData |= other.HasMethodData;
        if (Source == null && other.Source != null)
        {
            Source = other.Source;
            SourceFromCoverageFile = other.SourceFromCoverageFile;
        }
        foreach (var origin in other.Origins)
        {
            if (Origins.Count < MaxOrigins && !Origins.Contains(origin))
                Origins.Add(origin);
        }
    }

    // Hits add up; the first input that knows a method's start or end line provides it.
    void MergeMethod(string name, int line, int endLine, long hits)
    {
        if (Methods.TryGetValue(name, out var existing))
        {
            existing.Hits = Saturate(existing.Hits, hits);
            existing.Line = existing.Line == 0 ? line : existing.Line;
            existing.EndLine = existing.EndLine == 0 ? endLine : existing.EndLine;
        }
        else
        {
            Methods[name] = new Method { Name = name, Line = line, EndLine = endLine, Hits = hits };
        }
        Invalidate();
    }

    void Invalidate()
    {
        _fullyCovered = null;
        _ranges = null;
    }

    public Counts LineCounts => new(Lines.Values.Count(h => h > 0), Lines.Count);

    public Counts BranchCounts => new(
        Branches.Values.Sum(b => (long)b.Count(t => t > 0)),
        Branches.Values.Sum(b => (long)b.Count));

    public Counts MethodCounts => new(Methods.Values.Count(m => m.Hits > 0), Methods.Count);

    public Counts FullMethodCounts => new(Methods.Values.Count(IsFullyCovered), Methods.Count);

    /// <summary>Whether the method was called and none of its instrumented lines was missed.</summary>
    public bool IsFullyCovered(Method method) => (_fullyCovered ??= ComputeFullyCovered()).Contains(method);

    // Binary searches instead of a scan per method, since the number of methods comes from untrusted input.
    HashSet<Method> ComputeFullyCovered()
    {
        var missed = Lines.Where(l => l.Value <= 0).Select(l => l.Key).ToArray();  // sorted, as Lines is
        var result = new HashSet<Method>();
        foreach (var method in Methods.Values)
        {
            if (method.Hits <= 0)
                continue;
            if (method.Line == 0)
            {
                // Limitation: without line information, "called" is all that can be known.
                result.Add(method);
                continue;
            }
            var first = Array.BinarySearch(missed, method.Line);
            first = first < 0 ? ~first : first;
            if (first == missed.Length || missed[first] > EndOf(method))
                result.Add(method);
        }
        return result;
    }

    /// <summary>The method's last line: as reported, or else the line before the next method starts.</summary>
    public int EndOf(Method method)
    {
        if (method.EndLine > 0)
            return method.EndLine;
        var starts = Ranges.MethodStarts;
        var next = Array.BinarySearch(starts, method.Line) + 1;
        return next > 0 && next < starts.Length ? starts[next] - 1 : int.MaxValue;
    }

    /// <summary>Instrumented lines from <paramref name="first"/> to <paramref name="last"/>, both included.</summary>
    public Counts LinesBetween(int first, int last) => Ranges.Lines.Between(first, last);

    /// <summary>Branches from <paramref name="first"/> to <paramref name="last"/>, both included.</summary>
    public Counts BranchesBetween(int first, int last) => Ranges.Branches.Between(first, last);

    RangeIndex Ranges => _ranges ??= new RangeIndex(this);

    // Prefix sums by line number make every range query two binary searches. Range queries come per method, and
    // the number of methods comes from untrusted input.
    sealed class RangeIndex(FileCoverage file)
    {
        public readonly int[] MethodStarts = file.Methods.Values.Where(m => m.Line > 0).Select(m => m.Line).Distinct().Order().ToArray();
        public readonly PrefixSums Lines = new(file.Lines.Select(l => (l.Key, l.Value > 0 ? 1L : 0, 1L)));
        public readonly PrefixSums Branches = new(file.Branches.Select(b => (b.Key, (long)b.Value.Count(t => t > 0), (long)b.Value.Count)));
    }

    sealed class PrefixSums
    {
        readonly int[] _lines;
        readonly long[] _coveredBefore, _totalBefore;

        public PrefixSums(IEnumerable<(int Line, long Covered, long Total)> rows)  // rows sorted by line
        {
            var list = rows.ToList();
            _lines = new int[list.Count];
            _coveredBefore = new long[list.Count + 1];
            _totalBefore = new long[list.Count + 1];
            for (var i = 0; i < list.Count; i++)
            {
                _lines[i] = list[i].Line;
                _coveredBefore[i + 1] = _coveredBefore[i] + list[i].Covered;
                _totalBefore[i + 1] = _totalBefore[i] + list[i].Total;
            }
        }

        public Counts Between(int first, int last)
        {
            if (last < first)
                return default;
            var from = LowerBound(first);
            var to = last == int.MaxValue ? _lines.Length : LowerBound(last + 1);
            return new Counts(_coveredBefore[to] - _coveredBefore[from], _totalBefore[to] - _totalBefore[from]);
        }

        int LowerBound(int line)
        {
            var at = Array.BinarySearch(_lines, line);
            return at < 0 ? ~at : at;
        }
    }

    // Counts are never negative, as every parser rejects a sign; a negative b would overflow the check.
    static long Saturate(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>Path and text helpers for untrusted input.</summary>
static class PathUtil
{
    // Checked per rune rather than per char, because some invisible characters (the tag block) lie above U+FFFF.
    static bool IsFormat(Rune r) => Rune.GetUnicodeCategory(r) == UnicodeCategory.Format;

    static string Rewrite(string text, Func<Rune, string?> replacement)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var rune in text.EnumerateRunes())
            sb.Append(replacement(rune) ?? rune.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Removes control characters, bidi overrides and other invisible characters, which could make a path or name
    /// look like something else.
    /// </summary>
    public static string StripControl(string text) =>
        Rewrite(text, r => Rune.IsControl(r) || IsFormat(r) ? "" : null);

    /// <summary>Makes invisible characters in source lines visible as <c>[U+202E]</c> ("Trojan Source").</summary>
    public static string RevealInvisible(string text) =>
        Rewrite(text, r => IsFormat(r) ? $"[U+{r.Value:X4}]" : null);

    /// <summary>
    /// Normalizes a coverage path lexically: forward slashes, no <c>.</c> or <c>..</c> segments, no invisible
    /// characters. The file system is never consulted, as coverage paths often come from another machine.
    /// </summary>
    public static string Normalize(string path)
    {
        path = StripControl(path).Replace('\\', '/');
        var absolute = path.StartsWith('/');
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part != "..")
                parts.Add(part);
            else if (parts.Count > 0)
                parts.RemoveAt(parts.Count - 1);
        }
        return (absolute ? "/" : "") + string.Join('/', parts);
    }

    /// <summary>
    /// Merges each relative path into the one absolute path it is the tail of, so that a file reported by gcovr
    /// (relative paths) and by lcov (absolute paths) ends up as a single entry.
    /// </summary>
    /// <remarks>
    /// A tail that fits several absolute paths stays a separate entry rather than being guessed. Sorting reversed
    /// paths places equal tails next to each other, which keeps this fast for hostile file counts.
    /// </remarks>
    public static void MergeRelativeIntoAbsolute(Dictionary<string, FileCoverage> files)
    {
        static string Key(string path)
        {
            var chars = path.ToLowerInvariant().ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }

        var absolute = files.Values.Where(f => IsAbsolute(f.Path))
            .Select(f => (Key: Key(f.Path), File: f))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToArray();
        var keys = absolute.Select(e => e.Key).ToArray();

        foreach (var relative in files.Values.Where(f => !IsAbsolute(f.Path)).ToList())
        {
            var tail = Key("/" + relative.Path);
            var first = Array.BinarySearch(keys, tail, StringComparer.Ordinal);
            first = first < 0 ? ~first : first;
            var unique = first < keys.Length && keys[first].StartsWith(tail, StringComparison.Ordinal)
                && (first + 1 == keys.Length || !keys[first + 1].StartsWith(tail, StringComparison.Ordinal));
            if (!unique)
                continue;
            absolute[first].File.Merge(relative);
            files.Remove(relative.Path);
        }
    }

    static bool IsAbsolute(string path) =>
        path.StartsWith('/') || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');
}
