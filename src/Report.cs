// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace CoverageReport;

/// <summary>Aggregated coverage and code metrics of a set of files: the project, a folder or a single file.</summary>
/// <param name="FilesWithMetrics">Files whose source was analyzed, so that their LOC is known.</param>
/// <param name="FilesWithComplexity">Analyzed files with functions and CCM, which excludes assembler files.</param>
/// <param name="MaxCrap">The highest CRAP score of any function, or <c>null</c> if no function has one.</param>
/// <param name="FunctionsWithCrap">Functions with a CRAP score, that is, with at least one measured line.</param>
/// <param name="FilesWithEstimatedBranchData">Files whose branch data was estimated rather than measured.</param>
/// <param name="DecisionsNotEvaluable">Decisions the branch estimate found but could not resolve from line coverage.</param>
sealed record Totals(
    Counts Line, Counts Branch, Counts Method, Counts FullMethod, int FilesWithBranchData, int FilesWithMethodData, int FileCount,
    long Loc, long Complexity, long Functions, int MaxComplexity, int FilesWithMetrics, int FilesWithComplexity,
    double? MaxCrap, long FunctionsWithCrap, long FunctionsAboveCrapLimit,
    int FilesIncomplete, int FilesMatchedByNameOnly,
    int FilesWithEstimatedBranchData, int FilesWithEstimatedMethodData, long DecisionsNotEvaluable)
{
    public double AverageComplexity => Functions == 0 ? 0 : (double)Complexity / Functions;

    // Estimates are made for all files or none, so any estimated file makes the whole total an estimate.
    public bool BranchEstimated => FilesWithEstimatedBranchData > 0;
    public bool MethodEstimated => FilesWithEstimatedMethodData > 0;

    public Snapshot ToSnapshot() => new(Line, FilesWithBranchData > 0 ? Branch : null, FilesWithMethodData > 0 ? Method : null, BranchEstimated, MethodEstimated);
}

/// <summary>A file in a <c>--source-dir</c> folder that no coverage file lists. <c>Loc</c> is <c>null</c> if the file could not be read.</summary>
sealed record UnmeasuredFile(string Path, int? Loc);

/// <summary>Everything a report shows, independent of the output format.</summary>
sealed class Report
{
    public required List<FileCoverage> Files { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required List<string> Inputs { get; init; }
    public string? Title { get; init; }
    public List<HistoryRun>? History { get; set; }
    public List<UnmeasuredFile> Unmeasured { get; set; } = [];

    public Totals Totals => TotalsOf(Files);

    /// <summary>Aggregates the given files.</summary>
    /// <remarks>
    /// A file without data of a kind contributes nothing to that kind's totals, so missing data never pulls a
    /// percentage down.
    /// </remarks>
    public static Totals TotalsOf(IEnumerable<FileCoverage> files)
    {
        Counts line = default, branch = default, method = default, full = default;
        int withBranch = 0, withMethod = 0, count = 0, withMetrics = 0, withComplexity = 0, maxComplexity = 0, incomplete = 0, nameOnly = 0;
        int estimatedBranch = 0, estimatedMethod = 0;
        long loc = 0, complexity = 0, functions = 0, withCrap = 0, aboveCrapLimit = 0, notEvaluable = 0;
        double? maxCrap = null;
        foreach (var f in files)
        {
            count++;
            if (f.Metrics is { } m)
            {
                withMetrics++;
                if (!m.IsAssembly)
                    withComplexity++;
                if (m.Incomplete)
                    incomplete++;
                if (f.SourceMatchedByNameOnly)
                    nameOnly++;
                loc += m.Loc;
                complexity += m.Complexity;
                functions += m.Functions.Count;
                foreach (var function in m.Functions)
                {
                    maxComplexity = Math.Max(maxComplexity, function.Complexity);
                    if (function.Crap is not { } crap)
                        continue;
                    withCrap++;
                    if (IsAboveCrapLimit(crap))
                        aboveCrapLimit++;
                    maxCrap = Math.Max(maxCrap ?? 0, crap);
                }
            }
            line += f.LineCounts;
            if (f.HasBranchData)
            {
                withBranch++;
                branch += f.BranchCounts;
                if (f.BranchesEstimated)
                {
                    estimatedBranch++;
                    notEvaluable += f.DecisionsNotEvaluable;
                }
            }
            if (f.HasMethodData)
            {
                withMethod++;
                method += f.MethodCounts;
                full += f.FullMethodCounts;
                if (f.MethodsEstimated)
                    estimatedMethod++;
            }
        }
        return new Totals(line, branch, method, full, withBranch, withMethod, count, loc, complexity, functions, maxComplexity, withMetrics, withComplexity,
            maxCrap, withCrap, aboveCrapLimit, incomplete, nameOnly, estimatedBranch, estimatedMethod, notEvaluable);
    }

    /// <summary>The functions with the highest CRAP score.</summary>
    /// <remarks>
    /// Left out: functions without a measured line, about which nothing is known; simple functions that are fully
    /// covered, which carry no risk worth mentioning; and files matched to their source by name only, whose
    /// functions may belong to a different file.
    /// </remarks>
    public IEnumerable<(FileCoverage File, FunctionMetrics Function)> RiskiestFunctions(int count) =>
        Files.Where(f => f.Metrics != null && !f.SourceMatchedByNameOnly)
            .SelectMany(f => f.Metrics!.Functions.Select(function => (File: f, Function: function)))
            .Where(e => e.Function.Coverage.Total > 0
                && (e.Function.Coverage.Covered < e.Function.Coverage.Total || e.Function.Complexity > FunctionMetrics.ComplexityLimit))
            .OrderByDescending(e => e.Function.Crap!.Value)
            .ThenByDescending(e => e.Function.Complexity)
            .Take(count);

    /// <summary>The folder shared by all files without coverage data, and each file's path below it.</summary>
    public (string Folder, List<(string Shown, UnmeasuredFile File)> Files) UnmeasuredBelowCommonFolder()
    {
        var folders = Unmeasured.Select(u => System.IO.Path.GetDirectoryName(u.Path) ?? "").ToList();
        var common = folders.Count > 0 ? folders[0] : "";
        foreach (var folder in folders)
        {
            while (common.Length > 0 && !(folder.Equals(common, StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith(common.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                common = System.IO.Path.GetDirectoryName(common) ?? "";
        }
        var cut = common.Length == 0 ? 0 : common.TrimEnd('\\').Length + 1;
        return (common, Unmeasured.Select(u => (u.Path[cut..], u)).ToList());
    }

    public static Snapshot SnapshotOf(FileCoverage f) =>
        new(f.LineCounts, f.HasBranchData ? f.BranchCounts : null, f.HasMethodData ? f.MethodCounts : null,
            f.HasBranchData && f.BranchesEstimated, f.HasMethodData && f.MethodsEstimated);

    /// <summary>The mark in front of every estimated value.</summary>
    public const string Approx = "≈ ";

    public static string Decisions(long count) => count == 1 ? "1 decision" : $"{count.ToString(CultureInfo.InvariantCulture)} decisions";

    /// <summary>Says which coverage is estimated and how. Shared by the HTML and the Markdown report.</summary>
    public static string EstimateNote(Totals t)
    {
        var kinds = t.BranchEstimated && t.MethodEstimated ? "Branch and method coverage are" : t.BranchEstimated ? "Branch coverage is" : "Method coverage is";
        var note = $"{Approx}{kinds} estimated from line coverage and source code, not measured.";
        if (t.BranchEstimated)
        {
            note += " A branch of an if, a loop or a switch counts as taken if a line of its own ran; the false way of an if without"
                + " else and the exit of a loop count if the condition line ran more often than the branch was entered. The parts"
                + " of && and ||, ?: and exception paths do not show in line coverage, so measured branch coverage usually differs.";
            if (t.DecisionsNotEvaluable > 0)
                note += $" {Decisions(t.DecisionsNotEvaluable)} could not be evaluated and {(t.DecisionsNotEvaluable == 1 ? "is" : "are")} not counted.";
        }
        if (t.MethodEstimated)
            note += " A function counts as called if one of its measured lines ran.";
        return note;
    }

    /// <summary>Formats a percentage, rounded down so that 99.96 % is never shown as 100 %.</summary>
    public static string Percent(double? value) => value is { } p
        ? (Math.Floor(p * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "%"
        : "n/a";

    public static string PercentOf(Counts? counts) => Percent(counts?.Percent);

    /// <summary>Formats a percentage, marked with <see cref="Approx"/> if it is an estimate and known.</summary>
    public static string PercentOf(Counts? counts, bool estimated) =>
        estimated && counts?.Percent != null ? Approx + PercentOf(counts) : PercentOf(counts);

    /// <summary>Formats a CRAP score with at most one decimal.</summary>
    public static string Score(double? value) => value is { } v
        ? RoundScore(v).ToString("0.#", CultureInfo.InvariantCulture)
        : "n/a";

    /// <summary>
    /// Whether a CRAP score exceeds <see cref="FunctionMetrics.CrapLimit"/>. The check uses the displayed, rounded
    /// value, so that a score shown as "30" is never marked.
    /// </summary>
    public static bool IsAboveCrapLimit(double score) => RoundScore(score) > FunctionMetrics.CrapLimit;

    static double RoundScore(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>The folder part shared by all file paths. Tables omit it so that the differences stand out.</summary>
    public string CommonDirectory
    {
        get
        {
            if (Files.Count == 0)
                return "";
            var first = Files[0].Path.Split('/')[..^1];
            var n = first.Length;
            foreach (var f in Files)
            {
                var parts = f.Path.Split('/');
                var i = 0;
                while (i < n && i < parts.Length - 1 && string.Equals(first[i], parts[i], StringComparison.OrdinalIgnoreCase))
                    i++;
                n = i;
            }
            return string.Join('/', first[..n]);
        }
    }
}

/// <summary>Renders the report as Markdown, without source code.</summary>
static class MarkdownReport
{
    public const string CrapLegend = "CRAP = CCM² × (1 − line coverage)³ + CCM. It combines complexity with missing tests; a score above 30 marks a function that is risky to change.";
    public const string CcmLegend = "CCM is the cyclomatic complexity after McCabe; above 10, a function is considered hard to test.";

    public static string Render(Report report)
    {
        var t = report.Totals;
        var md = new StringBuilder();
        md.Append(HtmlReport.Marker).Append("\n\n# ").Append(report.Title is { } title ? Escape(title) : Program.Product).Append("\n\n");
        md.Append($"Generated by {Program.Product} {Program.Version} on {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} · {Program.Copyright}. Files: {t.FileCount}.\n\n");
        md.Append("## Summary\n\n");
        md.Append("| Metric | Covered | Total | Percent |\n|---|---:|---:|---:|\n");
        md.Append(Row("Line coverage", t.Line));
        md.Append(Row("Branch coverage", t.FilesWithBranchData > 0 ? t.Branch : null, t.BranchEstimated));
        md.Append(Row("Method coverage", t.FilesWithMethodData > 0 ? t.Method : null, t.MethodEstimated));
        md.Append(Row("Full method coverage", t.FilesWithMethodData > 0 ? t.FullMethod : null, t.MethodEstimated));
        if (t.BranchEstimated || t.MethodEstimated)
            md.Append('\n').Append(Report.EstimateNote(t)).Append('\n');

        string Metric(string text) => t.FilesWithComplexity > 0 ? text : "n/a";
        md.Append("\n## Code metrics\n\n");
        md.Append("| Metric | Value |\n|---|---:|\n");
        md.Append($"| Lines of code | {(t.FilesWithMetrics > 0 ? Number(t.Loc) : "n/a")} |\n");
        md.Append($"| Functions | {Metric(Number(t.Functions))} |\n");
        md.Append($"| Cyclomatic complexity after McCabe (CCM) | {Metric(Number(t.Complexity))} |\n");
        md.Append($"| Average CCM per function | {Metric(t.AverageComplexity.ToString("0.#", CultureInfo.InvariantCulture))} |\n");
        md.Append($"| Highest CCM in a function | {Metric(Number(t.MaxComplexity))} |\n");
        md.Append($"| Highest CRAP score in a function | {Report.Score(t.MaxCrap)} |\n");
        md.Append($"| Functions with a CRAP score above {FunctionMetrics.CrapLimit} | {(t.MaxCrap != null ? $"{t.FunctionsAboveCrapLimit} of {t.FunctionsWithCrap}" : "n/a")} |\n");
        md.Append($"| Files analyzed | {t.FilesWithMetrics} of {t.FileCount} |\n");
        if (t.FilesIncomplete > 0)
            md.Append($"| Files with incomplete analysis, numbers may be too low | {t.FilesIncomplete} |\n");
        if (t.FilesMatchedByNameOnly > 0)
            md.Append($"| Files matched by file name only, may be a different file | {t.FilesMatchedByNameOnly} |\n");
        md.Append('\n').Append(CcmLegend).Append(' ').Append(CrapLegend).Append('\n');

        md.Append("\n## Files\n\n");
        md.Append("| File | Line | Branch | Method | LOC | CCM | CRAP |\n|---|---:|---:|---:|---:|---:|---:|\n");
        var prefix = report.CommonDirectory;
        string Short(FileCoverage f) => prefix.Length > 0 ? f.Path[(prefix.Length + 1)..] : f.Path;
        foreach (var f in report.Files)
        {
            var s = Report.SnapshotOf(f);
            var loc = f.Metrics != null ? Number(f.Metrics.Loc) : "n/a";
            var complexity = f.Metrics is { IsAssembly: false } m ? Number(m.Complexity) : "n/a";
            md.Append($"| {CodeSpan(Short(f))} | {Cell(s.Line)} | {Cell(s.Branch, s.BranchEstimated)} | {Cell(s.Method, s.MethodEstimated)} | {loc} | {complexity} | {Report.Score(Report.TotalsOf([f]).MaxCrap)} |\n");
        }
        md.Append("\nCRAP: the highest score of a function in the file.\n");

        var functions = report.Files.Where(f => f.Metrics != null)
            .SelectMany(f => f.Metrics!.Functions.Select(function => (File: f, Function: function)))
            .OrderByDescending(e => e.Function.Complexity)
            .ThenBy(e => e.File.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Function.Line)
            .ToList();
        if (functions.Count > 0)
        {
            md.Append("\n## Functions\n\n");
            md.Append("| Function | File | Line | LOC | CCM | CRAP | Line coverage | Branch coverage |\n|---|---|---:|---:|---:|---:|---:|---:|\n");
            foreach (var (file, function) in functions)
            {
                md.Append($"| {CodeSpan(function.Name)} | {CodeSpan(Short(file))} | {function.Line} | {function.Loc} | {function.Complexity} | {Report.Score(function.Crap)} ")
                  .Append($"| {Cell(function.Coverage)} | {Cell(function.BranchCoverage, file.BranchesEstimated)} |\n");
            }
            md.Append('\n').Append(CrapLegend).Append('\n');
        }

        if (report.Unmeasured.Count > 0)
        {
            var (folder, unmeasured) = report.UnmeasuredBelowCommonFolder();
            md.Append("\n## Files without coverage data\n\n");
            md.Append("These files lie in the --source-dir folders, but no coverage file lists them. They do not count in any total.");
            md.Append(folder.Length > 0 ? $" Paths below {CodeSpan(PathUtil.StripControl(folder))}.\n\n" : "\n\n");
            md.Append("| File | LOC |\n|---|---:|\n");
            foreach (var (shown, u) in unmeasured)
                md.Append($"| {CodeSpan(PathUtil.StripControl(shown))} | {(u.Loc is { } loc ? Number(loc) : "n/a")} |\n");
        }
        return md.ToString();
    }

    static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    static string Row(string label, Counts? c, bool estimated = false) => c is { } v
        ? $"| {label} | {v.Covered} | {v.Total} | {Report.PercentOf(v, estimated)}{(estimated && v.Total > 0 ? " (estimated)" : "")} |\n"
        : $"| {label} | n/a | n/a | n/a |\n";

    static string Cell(Counts? c, bool estimated = false) => c is { Total: > 0 } v ? $"{Report.PercentOf(v, estimated)} ({v.Covered}/{v.Total})" : "n/a";

    /// <summary>
    /// Wraps untrusted text in a code span, inside which nothing becomes a link, image, mention or HTML. The fence is
    /// longer than any run of backticks in the text; inside a table cell, only pipes still need escaping.
    /// </summary>
    public static string CodeSpan(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return fence + pad + text.Replace("|", "\\|") + pad + fence;
    }

    /// <summary>Escapes every ASCII punctuation character, so that text such as the title renders literally.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c) || c > 127 || char.IsWhiteSpace(c) || c < 32)
                sb.Append(c);
            else
                sb.Append('\\').Append(c);
        }
        return sb.ToString();
    }
}
