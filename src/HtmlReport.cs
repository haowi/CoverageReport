// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace CoverageReport;

/// <summary>Writes the HTML report: a summary page plus one page per source file.</summary>
/// <remarks>
/// Every page is self-contained. All input is HTML-escaped, and as a second line of defense a Content-Security-Policy
/// allows only the exact embedded style and script, by hash, and no connections. A CSP cannot block a
/// <c>&lt;meta&gt;</c> refresh, so it complements the escaping rather than replacing it.
/// </remarks>
static class HtmlReport
{
    public const int MaxFiles = 100_000;
    public const long MaxOutputBytes = 4L * 1024 * 1024 * 1024;

    public const string Marker = "<!-- Coverage Report -->";
    const string TempPrefix = ".coveragereport-";

    // Pages and temporary folders written by hw-coverage, the former name of this tool, are recognized as well.
    static readonly string[] Markers = [Marker, "<!-- hw-coverage report -->"];
    static readonly string[] TempPrefixes = [TempPrefix, ".hw-coverage-"];

    static readonly string Css = Resource("report.css");
    static readonly string Js = Resource("report.js");

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var reader = new StreamReader(stream);
        // Browsers hash inline content as found in the page, which is written with LF line endings. Normalizing here
        // keeps the CSP hash valid even when the resource was checked out with CRLF.
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>Writes the report and, if given, the Markdown file into <paramref name="outputDir"/>.</summary>
    /// <remarks>
    /// Everything is written to a temporary folder first and then moved into place, so building and checking can fail
    /// without touching the existing report. Moving replaces a link at the destination instead of writing through it.
    /// <c>index.html</c> is moved last, because it links to the other pages.
    /// </remarks>
    public static void Write(Report report, string outputDir, string? markdown = null, List<string>? warnings = null)
    {
        if (report.Files.Count > MaxFiles)
            throw new InvalidDataException($"Report would have more than {MaxFiles} files.");

        Directory.CreateDirectory(outputDir);
        RemoveLeftovers(outputDir, warnings);
        var temp = Path.Combine(outputDir, TempPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var encoding = new UTF8Encoding(false);
            var written = 0L;
            void Put(string name, string content)
            {
                written += encoding.GetByteCount(content);
                if (written > MaxOutputBytes)
                    throw new InvalidDataException($"Report would be larger than {MaxOutputBytes / (1024 * 1024)} MB.");
                File.WriteAllText(Path.Combine(temp, name), content, encoding);
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in report.Files)
            {
                var name = PageName(file.Path);
                keep.Add(name);
                Put(name, FilePage(report, file));
            }
            Put("index.html", Index(report));
            if (markdown != null)
                Put("coverage.md", markdown);

            // Detect every foreseeable failure before the first move.
            string[] last = markdown != null ? ["coverage.md", "index.html"] : ["index.html"];
            var moves = keep.Concat(last).ToList();
            foreach (var name in moves)
                CheckReplaceable(Path.Combine(outputDir, name));

            foreach (var name in moves)
                File.Move(Path.Combine(temp, name), Path.Combine(outputDir, name), overwrite: true);

            // The new report is complete at this point, so a failed cleanup is only a warning.
            RemoveStalePages(outputDir, keep, markdown == null, warnings);
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Must not mask the original exception; only this run's own folder is affected.
            }
        }
    }

    static void CheckReplaceable(string path)
    {
        if (Directory.Exists(path))
            throw new IOException($"Cannot replace a folder with a report file: {path}");
        if (File.Exists(path))
        {
            // Opening and closing the file exposes a read-only or locked file now rather than halfway through the moves.
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    static bool HasMarker(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[128];
        var read = reader.Read(buffer, 0, buffer.Length);
        var head = new string(buffer, 0, read);
        return Markers.Any(marker => head.Contains(marker, StringComparison.Ordinal));
    }

    static void TryDelete(Action delete, string path, List<string>? warnings)
    {
        try
        {
            delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings?.Add($"Could not remove an old report file: {path}");
        }
    }

    /// <summary>Removes pages of files that are no longer in the report, and an outdated <c>coverage.md</c>.</summary>
    /// <remarks>
    /// A leftover page could still contain source code that is excluded by now. Only files written by this tool are
    /// removed, recognized by their name and marker.
    /// </remarks>
    static void RemoveStalePages(string outputDir, HashSet<string> keep, bool removeMarkdown, List<string>? warnings)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory, MatchType = MatchType.Simple };
        var stale = Directory.EnumerateFiles(outputDir, "f_*.html", options).Where(p => !keep.Contains(Path.GetFileName(p))).ToList();
        if (removeMarkdown)
            stale.AddRange(Directory.EnumerateFiles(outputDir, "coverage.md", options));

        foreach (var path in stale)
            TryDelete(() => { if (HasMarker(path)) File.Delete(path); }, path, warnings);
    }

    /// <summary>Removes the temporary folder left behind by a killed run, which may contain source code.</summary>
    /// <remarks>A folder is removed only if it contains nothing but files written by this tool.</remarks>
    static void RemoveLeftovers(string outputDir, List<string>? warnings)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Simple };
        foreach (var folder in TempPrefixes.SelectMany(prefix => Directory.EnumerateDirectories(outputDir, prefix + "????????", options)))
        {
            TryDelete(() =>
            {
                if (Directory.EnumerateDirectories(folder).Any())
                    return;
                if (Directory.EnumerateFiles(folder).All(HasMarker))
                    Directory.Delete(folder, recursive: true);
            }, folder, warnings);
        }
    }

    /// <summary>The page name for a source file.</summary>
    /// <remarks>
    /// It is a hash rather than the path, so a coverage file can never choose where a page is written. The path is
    /// upper-cased first, matching the case-insensitive comparison used elsewhere: "k" and the Kelvin sign are two
    /// different files and need two pages.
    /// </remarks>
    public static string PageName(string path) =>
        "f_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16] + ".html";

    public static string H(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    static string Hash(string content) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    static string Head(string title, bool script)
    {
        var scriptSrc = script ? $" script-src 'sha256-{Hash(Js)}';" : "";
        return "<!doctype html>" + Marker + "\n<html lang=\"en\"><head><meta charset=\"utf-8\">"
            + $"<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'sha256-{Hash(Css)}';{scriptSrc} base-uri 'none'; form-action 'none'\">"
            + "<meta name=\"referrer\" content=\"no-referrer\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<title>{H(title)}</title><style>{Css}</style></head><body>";
    }

    static string Footer(Report report) =>
        $"<footer>Generated by {Program.Product} {H(Program.Version)} &middot; {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} &middot; {H(Program.Copyright)} &middot; {H(Program.License)}</footer>";

    // The palette, as in report.css. SVG gets the colors as presentation attributes, which the CSP allows, whereas
    // style attributes would be blocked.
    const string Ink = "#0A0F14", Deep = "#0F3D3E", Teal = "#2E6F73", Mist = "#7FB3B8", Sand = "#EADCC8", Rust = "#C6601F";

    // Fixed thresholds for the bar colors. They are a visual aid only; a --threshold option can follow if needed.
    const int GoodFrom = 80, FairFrom = 50;

    const int RiskiestCount = 10;

    static readonly string CcmLegend = $"CCM is the cyclomatic complexity after McCabe; above {FunctionMetrics.ComplexityLimit} is marked.";
    static readonly string CrapLegend = $"CRAP = CCM² × (1 − line coverage)³ + CCM: complexity weighs heavily where tests are missing. Above {FunctionMetrics.CrapLimit} is marked as risky.";

    static string Tone(double percent) => percent >= GoodFrom ? Deep : percent >= FairFrom ? Mist : Rust;

    static string Bar(double percent, int width, int height)
    {
        var fill = percent > 0 ? Math.Max(height, width * percent / 100) : 0;
        return $"<svg width=\"{width}\" height=\"{height}\" aria-hidden=\"true\"><rect width=\"{width}\" height=\"{height}\" rx=\"{height / 2}\" fill=\"{Sand}\"/>"
             + $"<rect width=\"{fill.ToString("0.#", CultureInfo.InvariantCulture)}\" height=\"{height}\" rx=\"{height / 2}\" fill=\"{Tone(percent)}\"/></svg>";
    }

    static string TopBar(bool back, string? title = null) =>
        $"<header class=\"top\"><span class=\"brand\">{Program.Product}</span>"
        + (title != null ? $"<span class=\"project\">{H(title)}</span>" : "")
        + (back ? "<a class=\"back\" href=\"index.html\">&larr; Summary</a>" : "") + "</header>";

    // ---- summary page ----

    static string Index(Report report)
    {
        var t = report.Totals;
        var sb = new StringBuilder(Head(report.Title is { } title ? $"{title} - Coverage summary" : $"Coverage summary - {Program.Product}", true));
        sb.Append(TopBar(back: false)).Append("<main>");
        sb.Append($"<div class=\"title\"><h1>{H(report.Title ?? "Coverage summary")}</h1>")
          .Append("<p class=\"meta\">").Append(report.Title != null ? "Coverage summary &middot; " : "").Append($"Files: {N(t.FileCount)}")
          .Append(report.Unmeasured.Count > 0 ? $" &middot; Without coverage data: {N(report.Unmeasured.Count)}" : "")
          .Append($" &middot; Parsed reports: {N(report.Inputs.Count)} &middot; {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}</p></div>");

        var totalLines = report.Files.All(f => f.Source != null) ? report.Files.Sum(f => (long)f.Source!.Length) : (long?)null;
        AppendKpis(sb, t, totalLines, DataNote("Branch", t.FilesWithBranchData, t.FileCount), DataNote("Method", t.FilesWithMethodData, t.FileCount));

        AppendRiskiest(sb, report);

        if (report.History != null)
            AppendHistory(sb, report.History.Select(r => (r.Time, (Snapshot?)r.Totals)).ToList());

        sb.Append("<section><div class=\"section-head\"><h2>Files</h2><div class=\"toolbar\">")
          .Append("<label>Filter <input id=\"filter\" type=\"search\" autocomplete=\"off\" placeholder=\"Part of a path\"></label>")
          .Append("<button type=\"button\" id=\"collapse\">Collapse all</button>")
          .Append("<button type=\"button\" id=\"expand\">Expand all</button></div></div>");
        AppendFileTable(sb, report);
        sb.Append($"<p class=\"hint\">Bars: dark teal from {GoodFrom} %, light teal from {FairFrom} %, orange below. ")
          .Append($"CRAP shows the highest score of a function in the file or folder. {H(CrapLegend)}</p></section>");
        AppendUnmeasured(sb, report);
        sb.Append("</main>").Append(Footer(report));
        sb.Append($"<script>{Js}</script></body></html>");
        return sb.ToString();
    }

    static string? DataNote(string kind, int with, int all) =>
        with > 0 && with < all ? $"{kind} data: {with} of {all} files. Files without data are not counted." : null;

    static void AppendKpis(StringBuilder sb, Totals t, long? totalLines, string? branchNote, string? methodNote)
    {
        var branch = t.FilesWithBranchData > 0 ? t.Branch : (Counts?)null;
        var method = t.FilesWithMethodData > 0 ? t.Method : (Counts?)null;
        var known = t.FilesWithMetrics > 0;
        var noComplexity = known ? "Not computed for assembler" : "Needs source code: use --source-dir";

        sb.Append("<dl class=\"kpis\">");
        Kpi(sb, "Line coverage", Report.PercentOf(t.Line), t.Line.Percent,
            $"{N(t.Line.Covered)} of {N(t.Line.Total)} coverable lines, {N(t.Line.Total - t.Line.Covered)} uncovered");
        Kpi(sb, "Branch coverage", Report.PercentOf(branch), branch?.Percent,
            branch is { } b ? $"{N(b.Covered)} of {N(b.Total)} branches" : "No branch data", branchNote);
        Kpi(sb, "Method coverage", Report.PercentOf(method), method?.Percent,
            method is { } m ? $"{N(m.Covered)} of {N(m.Total)} methods, {N(t.FullMethod.Covered)} fully ({Report.PercentOf(t.FullMethod)})" : "No method data", methodNote);
        var caveats = new List<string>();
        if (t.FilesIncomplete > 0)
            caveats.Add($"Analysis incomplete in {N(t.FilesIncomplete)} of {N(t.FilesWithMetrics)} files: braces do not add up, numbers may be too low.");
        if (t.FilesMatchedByNameOnly > 0)
            caveats.Add($"{N(t.FilesMatchedByNameOnly)} of {N(t.FilesWithMetrics)} files matched by file name only: may be a different file.");

        Kpi(sb, "Lines of code", known ? N(t.Loc) : "n/a", null,
            known ? $"{(totalLines is { } total ? $"{N(total)} lines in total" : $"From {N(t.FilesWithMetrics)} of {N(t.FileCount)} files")}" : "Needs source code: use --source-dir",
            caveats.Count > 0 ? string.Join(" ", caveats) : null);
        Kpi(sb, "Complexity (CCM)", t.FilesWithComplexity > 0 ? N(t.Complexity) : "n/a", null,
            t.FilesWithComplexity > 0 ? $"{N(t.Functions)} functions, average {t.AverageComplexity.ToString("0.#", CultureInfo.InvariantCulture)}, highest {N(t.MaxComplexity)}" : noComplexity,
            legend: t.FilesWithComplexity > 0 ? CcmLegend : null);
        Kpi(sb, "Highest CRAP score", Report.Score(t.MaxCrap), null,
            t.MaxCrap != null ? $"{N(t.FunctionsAboveCrapLimit)} of {N(t.FunctionsWithCrap)} functions above {FunctionMetrics.CrapLimit}"
            : t.FilesWithComplexity > 0 ? "No function has a measured line" : noComplexity,
            legend: t.MaxCrap != null ? CrapLegend : null);
        sb.Append("</dl>");
    }

    static void AppendUnmeasured(StringBuilder sb, Report report)
    {
        if (report.Unmeasured.Count == 0)
            return;
        var (folder, files) = report.UnmeasuredBelowCommonFolder();
        sb.Append("<section><div class=\"section-head\"><h2>Files without coverage data</h2></div>")
          .Append($"<p class=\"meta\">{N(files.Count)} files in the --source-dir folders that no coverage file lists: not built with coverage, or no test ran them. They do not count in any total.")
          .Append(folder.Length > 0 ? $" Paths below <span class=\"path\">{H(PathUtil.StripControl(folder))}</span>." : "")
          .Append("</p><div class=\"scroll\"><table class=\"plain\"><thead><tr><th class=\"left\">File</th><th>LOC</th></tr></thead><tbody>");
        foreach (var (shown, file) in files)
            sb.Append($"<tr><td class=\"left fn\">{H(PathUtil.StripControl(shown))}</td><td{(file.Loc == null ? " class=\"na\"" : "")}>{N(file.Loc)}</td></tr>");
        sb.Append("</tbody></table></div></section>");
    }

    // A key figure tile. A note is a caveat and is highlighted; a legend explains the figure and is not.
    static void Kpi(StringBuilder sb, string label, string value, double? percent, string detail, string? note = null, string? legend = null)
    {
        var missing = value == "n/a" ? " na" : "";
        sb.Append($"<div class=\"kpi\"><dt>{label}</dt><dd class=\"kpi-value{missing}\">{value}</dd>");
        if (percent is { } p)
            sb.Append("<dd class=\"kpi-bar\">").Append(Bar(p, 140, 6)).Append("</dd>");
        sb.Append($"<dd class=\"kpi-detail\">{H(detail)}</dd>");
        if (note != null)
            sb.Append($"<dd class=\"kpi-detail\">{H(note)}</dd>");
        if (legend != null)
            sb.Append($"<dd class=\"kpi-detail kpi-legend\">{H(legend)}</dd>");
        sb.Append("</div>");
    }

    static string ComplexityCell(int complexity) =>
        complexity > FunctionMetrics.ComplexityLimit
            ? $"<td class=\"hot\" title=\"Above {FunctionMetrics.ComplexityLimit}\">{N(complexity)}</td>"
            : $"<td>{N(complexity)}</td>";

    static string CrapCell(double? crap) => crap switch
    {
        null => "<td class=\"na\">n/a</td>",
        { } c when Report.IsAboveCrapLimit(c) => $"<td class=\"hot\" title=\"Above {FunctionMetrics.CrapLimit}\">{Report.Score(c)}</td>",
        { } c => $"<td>{Report.Score(c)}</td>",
    };

    static string CoverageCell(Counts coverage) =>
        coverage.Total > 0 ? $"<td>{Report.PercentOf(coverage)}</td>" : "<td class=\"na\">n/a</td>";

    static void AppendRiskiest(StringBuilder sb, Report report)
    {
        var riskiest = report.RiskiestFunctions(RiskiestCount).ToList();
        if (riskiest.Count == 0)
            return;

        sb.Append("<section class=\"risk\"><div class=\"section-head\"><h2>Riskiest functions</h2></div><div class=\"scroll\"><table class=\"plain\"><thead><tr>")
          .Append("<th class=\"left\">Function</th><th class=\"left\">File</th><th>CRAP</th><th>CCM</th><th>LOC</th><th>Line coverage</th></tr></thead><tbody>");
        var prefix = report.CommonDirectory;
        foreach (var (file, function) in riskiest)
        {
            sb.Append($"<tr><td class=\"left fn\"><a href=\"{PageName(file.Path)}#L{function.Line}\">{H(function.Name)}</a></td>")
              .Append($"<td class=\"left fn\">{H(Relative(file.Path, prefix))}</td>")
              .Append(CrapCell(function.Crap))
              .Append(ComplexityCell(function.Complexity))
              .Append($"<td>{N(function.Loc)}</td>")
              .Append(CoverageCell(function.Coverage))
              .Append("</tr>");
        }
        sb.Append($"</tbody></table></div><p class=\"hint\">Ranked by CRAP score. {H(CrapLegend)} {H(CcmLegend)}");
        if (riskiest.Any(e => e.File.SourceFromCoverageFile))
            sb.Append(" Some entries use source text stored in gcov files, not read from disk.");
        sb.Append("</p></section>");
    }

    static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "n/a";

    // ---- file table ----

    static void AppendFileTable(StringBuilder sb, Report report)
    {
        string[] headers = ["Line", "Branch", "Method", "Functions", "LOC", "CCM", "CRAP"];
        sb.Append("<div class=\"scroll\"><table class=\"cov\" id=\"files\"><thead><tr>")
          .Append("<th class=\"name\" data-col=\"0\" aria-sort=\"none\"><button type=\"button\">Name</button></th>");
        for (var i = 0; i < headers.Length; i++)
            sb.Append($"<th data-col=\"{i + 1}\" aria-sort=\"none\"><button type=\"button\">{headers[i]}</button></th>");
        sb.Append("</tr></thead>");

        var prefix = report.CommonDirectory;
        var groups = report.Files
            .GroupBy(f => DirectoryOf(Relative(f.Path, prefix)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var title = group.Key.Length > 0 ? group.Key : prefix.Length > 0 ? prefix : "(root)";
            sb.Append("<tbody><tr class=\"dir\">");
            sb.Append($"<th scope=\"rowgroup\" class=\"name\" data-v=\"{H(title)}\"><button type=\"button\" class=\"toggle\" aria-expanded=\"true\">{H(title)}</button></th>");
            AppendCounts(sb, Report.TotalsOf(group));
            sb.Append("</tr>");
            foreach (var f in group)
            {
                var name = Relative(f.Path, prefix);
                var shortName = name[(name.LastIndexOf('/') + 1)..];
                sb.Append($"<tr class=\"file\" data-path=\"{H(f.Path)}\"><td class=\"name\" data-v=\"{H(shortName)}\" title=\"{H(f.Path)}\"><a href=\"{PageName(f.Path)}\">{H(shortName)}</a></td>");
                AppendCounts(sb, Report.TotalsOf([f]));
                sb.Append("</tr>");
            }
            sb.Append("</tbody>");
        }
        sb.Append("</table></div>");
    }

    static string Relative(string path, string prefix) => prefix.Length > 0 ? path[(prefix.Length + 1)..] : path;

    static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash > 0 ? path[..slash] : "";
    }

    static void AppendCounts(StringBuilder sb, Totals t)
    {
        PercentCell(sb, t.Line, null);
        PercentCell(sb, t.FilesWithBranchData > 0 ? t.Branch : null, null);
        PercentCell(sb, t.FilesWithMethodData > 0 ? t.Method : null, t.FilesWithMethodData > 0 ? $"{N(t.FullMethod.Covered)} fully covered" : null);

        Cell(sb, t.FilesWithComplexity > 0 ? t.Functions : null);
        Cell(sb, t.FilesWithMetrics > 0 ? t.Loc : null);
        Cell(sb, t.FilesWithComplexity > 0 ? t.Complexity : null);
        ScoreCell(sb, t.MaxCrap);
    }

    // The sortable table keeps each cell's raw value in data-v; report.js sorts "n/a", stored as -1, below every value.
    static void Cell(StringBuilder sb, long? value)
    {
        if (value is { } v)
            sb.Append($"<td data-v=\"{N(v)}\">{N(v)}</td>");
        else
            sb.Append("<td data-v=\"-1\" class=\"na\">n/a</td>");
    }

    static void ScoreCell(StringBuilder sb, double? score)
    {
        if (score is not { } s)
        {
            sb.Append("<td data-v=\"-1\" class=\"na\">n/a</td>");
            return;
        }
        var hot = Report.IsAboveCrapLimit(s) ? $" class=\"hot\" title=\"Above {FunctionMetrics.CrapLimit}\"" : "";
        sb.Append($"<td data-v=\"{Report.Score(s)}\"{hot}>{Report.Score(s)}</td>");
    }

    static void PercentCell(StringBuilder sb, Counts? counts, string? title)
    {
        if (counts is { Percent: { } p } c)
        {
            var titleAttribute = title != null ? $" title=\"{H(title)}\"" : "";
            sb.Append($"<td data-v=\"{p.ToString("0.###", CultureInfo.InvariantCulture)}\"{titleAttribute}><div class=\"pct\"><span>{Report.Percent(p)}</span>")
              .Append($"<span class=\"count\">{N(c.Covered)}/{N(c.Total)}</span>").Append(Bar(p, 64, 6)).Append("</div></td>");
        }
        else
        {
            sb.Append("<td data-v=\"-1\" class=\"na\">n/a</td>");
        }
    }

    // ---- history chart ----

    // The teal shades alone are hard to tell apart for some readers, so each series also has its own dash pattern
    // and a direct label.
    static readonly (string Label, string Color, string? Dash, Func<Snapshot, Counts?> Pick)[] Series =
    [
        ("Line", Teal, null, s => s.Line),
        ("Branch", Rust, "7 4", s => s.Branch),
        ("Method", Mist, "2 3", s => s.Method),
    ];

    static void AppendHistory(StringBuilder sb, List<(DateTimeOffset Time, Snapshot? Snapshot)> runs)
    {
        sb.Append("<section><div class=\"section-head\"><h2>Coverage history</h2></div><div class=\"chart\">");
        sb.Append(HistoryChart(runs));
        sb.Append("<details><summary>Data table</summary><div class=\"scroll\"><table class=\"plain\"><thead><tr><th class=\"left\">Run</th>");
        foreach (var s in Series)
            sb.Append($"<th>{s.Label}</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var (time, snapshot) in runs)
        {
            sb.Append($"<tr><td class=\"left\">{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}</td>");
            foreach (var s in Series)
                sb.Append($"<td>{(snapshot is { } v ? Report.PercentOf(s.Pick(v)) : "n/a")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div></details></div></section>");
    }

    /// <summary>Renders the coverage trend as an SVG chart.</summary>
    /// <remarks>A run without data for a series leaves a gap in its line; it never drops the curve to 0 %.</remarks>
    public static string HistoryChart(List<(DateTimeOffset Time, Snapshot? Snapshot)> runs)
    {
        const int width = 900, height = 240, left = 42, right = 96, top = 12, bottom = 28;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        double X(int i) => runs.Count == 1 ? left + plotWidth / 2.0 : left + plotWidth * i / (double)(runs.Count - 1);
        double Y(double percent) => top + plotHeight * (100 - percent) / 100;
        static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append("<div class=\"legend\">");
        string DashAttribute(string? dash) => dash != null ? $" stroke-dasharray=\"{dash}\"" : "";
        foreach (var s in Series)
            sb.Append($"<span><svg width=\"26\" height=\"4\" aria-hidden=\"true\"><line x1=\"0\" x2=\"26\" y1=\"2\" y2=\"2\" stroke=\"{s.Color}\" stroke-width=\"2\"{DashAttribute(s.Dash)}/></svg>{s.Label}</span>");
        sb.Append("</div>");
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Coverage percent per run; see the data table below\" font-size=\"12\" fill=\"{Teal}\">");

        foreach (var tick in new[] { 0, 25, 50, 75, 100 })
        {
            var y = F(Y(tick));
            sb.Append($"<line x1=\"{left}\" x2=\"{width - right}\" y1=\"{y}\" y2=\"{y}\" stroke=\"{Sand}\" stroke-opacity=\"0.6\"/>");
            sb.Append($"<text x=\"{left - 8}\" y=\"{y}\" text-anchor=\"end\" dominant-baseline=\"middle\">{tick}%</text>");
        }

        // As many date labels as fit, always including the latest run.
        var step = Math.Max(1, runs.Count / 6);
        for (var i = runs.Count - 1; i >= 0; i -= step)
        {
            var anchor = runs.Count == 1 ? "middle" : i == runs.Count - 1 ? "end" : "middle";
            sb.Append($"<text x=\"{F(X(i))}\" y=\"{height - 8}\" text-anchor=\"{anchor}\">{runs[i].Time.ToLocalTime():MM-dd HH:mm}</text>");
        }

        var directLabels = new List<(double Y, string Text)>();
        foreach (var s in Series)
        {
            var segment = new List<string>();
            void FlushSegment()
            {
                if (segment.Count > 1)
                    sb.Append($"<polyline fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\" stroke-linejoin=\"round\"{DashAttribute(s.Dash)} points=\"{string.Join(' ', segment)}\"/>");
                segment.Clear();
            }
            for (var i = 0; i < runs.Count; i++)
            {
                var percent = runs[i].Snapshot is { } v ? s.Pick(v)?.Percent : null;
                if (percent is { } p)
                    segment.Add($"{F(X(i))},{F(Y(p))}");
                else
                    FlushSegment();
            }
            FlushSegment();

            for (var i = 0; i < runs.Count; i++)
            {
                var percent = runs[i].Snapshot is { } v ? s.Pick(v)?.Percent : null;
                if (percent is not { } p)
                    continue;
                // A native tooltip; the transparent circle enlarges the hit target.
                sb.Append($"<g><title>{runs[i].Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} - {s.Label}: {Report.Percent(p)}</title>")
                  .Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(p))}\" r=\"10\" fill=\"transparent\"/>")
                  .Append($"<circle cx=\"{F(X(i))}\" cy=\"{F(Y(p))}\" r=\"4\" fill=\"{s.Color}\" stroke=\"#fff\" stroke-width=\"2\"/></g>");
            }

            var last = Enumerable.Range(0, runs.Count).Reverse()
                .Select(i => runs[i].Snapshot is { } v ? s.Pick(v)?.Percent : null)
                .FirstOrDefault(p => p != null);
            if (last is { } lastPercent)
                directLabels.Add((Y(lastPercent), $"{s.Label} {Report.Percent(lastPercent)}"));
        }

        // Direct labels are pushed apart so that series with equal values stay readable.
        var previous = double.NegativeInfinity;
        foreach (var label in directLabels.OrderBy(l => l.Y))
        {
            var y = Math.Max(label.Y, previous + 15);
            sb.Append($"<text x=\"{width - right + 10}\" y=\"{F(y)}\" dominant-baseline=\"middle\" fill=\"{Ink}\">{H(label.Text)}</text>");
            previous = y;
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---- file page ----

    static string FilePage(Report report, FileCoverage file)
    {
        var t = Report.TotalsOf([file]);
        var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(Head($"{name} - {report.Title ?? Program.Product}", false));
        sb.Append(TopBar(back: true, report.Title)).Append("<main>");
        sb.Append($"<div class=\"title\"><h1>{H(name)}</h1><p class=\"meta path\">{H(file.Path)}</p></div>");

        AppendKpis(sb, t, file.Source?.Length, null, null);

        if (report.History != null)
        {
            var runs = report.History
                .Select(r => (r.Time, r.Files.TryGetValue(file.Path, out var s) ? s : (Snapshot?)null))
                .ToList();
            AppendHistory(sb, runs);
        }

        AppendFunctions(sb, file);
        AppendSource(sb, file);
        sb.Append("</main>").Append(Footer(report)).Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Renders the function table of a file page.</summary>
    /// <remarks>
    /// There is one row per method the coverage tool reports, with each template instantiation on its own row, joined
    /// by line with the function the source analysis found there. Functions no coverage tool reports, such as
    /// templates that were never instantiated, follow with their own rows.
    /// </remarks>
    static void AppendFunctions(StringBuilder sb, FileCoverage file)
    {
        sb.Append("<section><div class=\"section-head\"><h2>Functions</h2></div>");
        var metrics = file.Metrics;
        var functions = metrics?.Functions ?? [];
        if (!file.HasMethodData && functions.Count == 0)
        {
            sb.Append("<p class=\"meta\">No method data.</p></section>");
            return;
        }

        sb.Append("<div class=\"scroll\"><table class=\"plain\"><thead><tr><th class=\"left\">Function</th><th>Line</th><th>LOC</th><th>CCM</th><th>CRAP</th>")
          .Append("<th>Calls</th><th>Line coverage</th><th>Branch coverage</th><th class=\"left\">Status</th></tr></thead><tbody>");

        // Only lines of the source listing have an anchor, and without source code there is no listing.
        string Link(string name, int line) => file.Source != null && line > 0 && (file.Lines.ContainsKey(line) || line <= file.Source.Length)
            ? $"<a href=\"#L{line}\">{H(name)}</a>"
            : H(name);

        var matched = new HashSet<FunctionMetrics>();
        var methods = file.Methods.Values.OrderBy(m => m.Line).ThenBy(m => m.Name, StringComparer.Ordinal);
        var rows = new List<(int Line, string Html)>();
        foreach (var m in methods)
        {
            var function = metrics?.FunctionAt(m.Line);
            if (function != null)
                matched.Add(function);
            var (status, tone) = m.Hits <= 0 ? ("Not covered", "none") : file.IsFullyCovered(m) ? ("Fully covered", "full") : ("Partly covered", "part");

            // A function found by the analysis already carries its coverage, with the reported end line taken into
            // account. Otherwise the method runs to its reported end line or up to the next method.
            var (lines, branches) = function != null ? (function.Coverage, function.BranchCoverage)
                : m.Line > 0 ? (file.LinesBetween(m.Line, file.EndOf(m)), file.BranchesBetween(m.Line, file.EndOf(m)))
                : (default, default);
            rows.Add((m.Line, $"<tr><td class=\"left fn\">{Link(m.Name, m.Line)}</td><td>{(m.Line > 0 ? N(m.Line) : "n/a")}</td>"
                + (function != null ? $"<td>{N(function.Loc)}</td>{ComplexityCell(function.Complexity)}{CrapCell(function.Crap)}" : "<td class=\"na\">n/a</td><td class=\"na\">n/a</td><td class=\"na\">n/a</td>")
                + $"<td>{N(m.Hits)}</td>{CoverageCell(lines)}{CoverageCell(branches)}"
                + $"<td class=\"left\"><span class=\"status {tone}\">{status}</span></td></tr>"));
        }
        foreach (var function in functions.Where(f => !matched.Contains(f)))
        {
            rows.Add((function.Line, $"<tr><td class=\"left fn\">{Link(function.Name, function.Line)}</td><td>{N(function.Line)}</td><td>{N(function.Loc)}</td>"
                + $"{ComplexityCell(function.Complexity)}{CrapCell(function.Crap)}<td class=\"na\">n/a</td>{CoverageCell(function.Coverage)}{CoverageCell(function.BranchCoverage)}"
                + "<td class=\"left\"><span class=\"status\">No method data</span></td></tr>"));
        }
        foreach (var (_, html) in rows.OrderBy(r => r.Line))
            sb.Append(html);
        sb.Append("</tbody></table></div><p class=\"hint\">LOC and CCM come from reading the source");
        sb.Append(metrics?.IsAssembly == true ? "; for assembler, neither CCM nor CRAP is computed." : $". {H(CcmLegend)} {H(CrapLegend)}");
        sb.Append(" Line and branch coverage count the function's lines: up to the end line the coverage file gives, else to the end the source analysis found, else up to the next function.</p></section>");
    }

    /// <summary>Renders the source listing, with each line marked by its coverage.</summary>
    /// <remarks>
    /// Without source code there is no listing at all, only the reason. Rows of bare line numbers would make the page
    /// long without telling more than the key figures and the function table above.
    /// </remarks>
    static void AppendSource(StringBuilder sb, FileCoverage file)
    {
        sb.Append("<section><div class=\"section-head\"><h2>Source</h2></div>");
        if (file.Source == null)
        {
            sb.Append($"<p class=\"warn\">Source not shown: {H(file.SourceProblem ?? "not found")}. Point --source-dir at the folder that contains it.</p></section>");
            return;
        }

        if (file.SourceFromCoverageFile)
            sb.Append("<p class=\"hint\">Source text as stored in the gcov file, not read from disk. Point --source-dir at its folder to show the file itself.</p>");
        else if (file.SourceMatchedByNameOnly)
            sb.Append("<p class=\"warn\">Matched to a source file by file name only: this may be a different file of the same name.</p>");

        sb.Append("<div class=\"scroll\"><table class=\"source\"><thead><tr><th>Hits</th><th>Line</th><th>Branches</th><th>Code</th></tr></thead><tbody>");
        for (var i = 0; i < file.Source.Length; i++)
            AppendSourceRow(sb, file, i + 1, file.Source[i]);
        foreach (var line in file.Lines.Keys.Where(l => l > file.Source.Length))
            AppendSourceRow(sb, file, line, "(line is not in this source: the file may have changed since the test run)");
        sb.Append("</tbody></table></div></section>");
    }

    static void AppendSourceRow(StringBuilder sb, FileCoverage file, int line, string code)
    {
        var hasHits = file.Lines.TryGetValue(line, out var hits);
        file.Branches.TryGetValue(line, out var branches);
        var covered = branches?.Count(t => t > 0) ?? 0;
        var total = branches?.Count ?? 0;

        var state = !hasHits ? "none" : hits <= 0 ? "miss" : covered < total ? "part" : "hit";
        var title = state switch { "miss" => "Not covered", "part" => "Partly covered", "hit" => "Covered", _ => "Not coverable" };
        var branchText = total > 0 ? $"<span class=\"badge\" title=\"{covered} of {total} branches taken\">{covered}/{total}</span>" : "";

        sb.Append($"<tr class=\"{state}\" id=\"L{line}\"><td class=\"hits\" title=\"{title}\">{(hasHits ? N(hits) : "")}</td>")
          .Append($"<td class=\"num\">{line}</td><td class=\"brc\">{branchText}</td><td class=\"code\">{H(PathUtil.RevealInvisible(code))}</td></tr>");
    }
}
