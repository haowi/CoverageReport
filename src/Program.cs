// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.IO.Enumeration;
using System.Reflection;
using System.Text;

namespace CoverageReport;

/// <summary>Command line entry point: parses the arguments, builds the report and prints a summary.</summary>
static class Program
{
    public const string Product = "Coverage Report";
    public const string Copyright = "Copyright © 2026 Hanno Witt";
    public const string License = "Licensed under the Apache License 2.0";
    public const int MaxTitleLength = 200;

    const int Success = 0, RuntimeError = 1, UsageError = 2;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

    const string Usage = """
        Renders C/C++ and assembler code coverage as an HTML report. Works offline; nothing leaves this machine.

        Usage:
          coveragereport -i <file|dir|wildcard> [-i ...] -o <dir> [options]

        Required:
          -i, --input <path>       Coverage input: a file, a folder (searched recursively) or a wildcard
                                   such as reports\*.gcov. Repeatable; all inputs are merged.
                                   Formats (detected from the content): Cobertura XML, lcov, gcov text.
          -o, --output <dir>       Output folder for the report. Created if missing.

        Options:
          -t, --title <text>       Project title, shown as the report's heading.
          -x, --exclude <pattern>  Leave out files whose full path matches. * and ? are wildcards, * also
                                   spans folders, case is ignored, / and \ are equal. Repeatable.
                                   Examples: "/usr/*"  "*/test/*"  "*.h"
          -s, --source-dir <dir>   Folder to look for source code in, for files that are not at the path the
                                   coverage file names. Repeatable. Its C/C++ and assembler files without
                                   coverage data are listed in the report; they do not count in any total.
              --source-dir-only    Never read the paths the coverage files name, only --source-dir folders.
                                   Use it with coverage files from others.
              --history-dir <dir>  Keep one result per run here and show coverage history in the report.
              --markdown           Also write coverage.md to the output folder.
          -h, --help, -?           Show this help.
              --version            Show the version.

        Source code is read from the paths the coverage files name, then from --source-dir. Only C/C++ and
        assembler files (.s, .S, .asm) are read, never from a network path.

        All parameters also work with "/" instead of "-": /i is -i, /input is --input, /? is -?.

        Exit codes: 0 = report written, 1 = processing error, 2 = invalid command line.
        """;

    sealed class Options
    {
        public List<string> Inputs { get; } = [];
        public List<string> Excludes { get; } = [];
        public List<string> SourceDirs { get; } = [];
        public string? Output;
        public string? Title;
        public string? HistoryDir;
        public bool SourceDirOnly;
        public bool Markdown;
        public bool Help;
        public bool ShowVersion;
    }

    sealed class UsageException(string message) : Exception(message);

    internal static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = args.Length > 0 ? ParseArguments(args) : null;
            if (options == null || options.Help)
            {
                PrintBanner();
                Console.WriteLine();
                Console.WriteLine(Usage);
                return options == null ? UsageError : Success;
            }
            if (options.ShowVersion)
            {
                PrintBanner();
                return Success;
            }
            return Run(options);
        }
        catch (UsageException e)
        {
            Error(e.Message);
            Console.Error.WriteLine("Run coveragereport --help for usage.");
            return UsageError;
        }
        catch (OutOfMemoryException)
        {
            Error($"The input needs more memory than {Product} allows itself.");
            return RuntimeError;
        }
        catch (Exception e)
        {
            // Never let an exception escape: an unhandled crash could send a dump through Windows Error Reporting.
            Error(e.Message);
            return RuntimeError;
        }
    }

    static void PrintBanner()
    {
        Console.WriteLine($"{Product} {Version}");
        Console.WriteLine($"{Copyright}. {License}.");
    }

    static Options ParseArguments(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => ++i < args.Length ? args[i] : throw new UsageException($"Missing value for {arg}.");

            // Only option names are translated. Values such as "/usr/*" are consumed by Value() and never get here.
            switch (WithDashes(arg))
            {
                case "-h" or "--help" or "-?":
                    options.Help = true;
                    return options;
                case "--version":
                    options.ShowVersion = true;
                    return options;
                case "-i" or "--input": options.Inputs.Add(Value()); break;
                case "-o" or "--output": options.Output = Value(); break;
                case "-t" or "--title": options.Title = Title(Value()); break;
                case "-x" or "--exclude": options.Excludes.Add(Value()); break;
                case "-s" or "--source-dir": options.SourceDirs.Add(Value()); break;
                case "--source-dir-only": options.SourceDirOnly = true; break;
                case "--history-dir": options.HistoryDir = Value(); break;
                case "--markdown": options.Markdown = true; break;
                default: throw new UsageException($"Unknown argument: {arg}");
            }
        }
        if (options.Inputs.Count == 0)
            throw new UsageException("At least one --input is required.");
        if (options.Output == null)
            throw new UsageException("--output is required.");
        foreach (var dir in options.SourceDirs)
        {
            if (!Directory.Exists(dir))
                throw new UsageException($"Source folder not found: {dir}");
        }
        return options;
    }

    // Invisible characters are removed like everywhere else, so the heading shows what was typed.
    static string Title(string value)
    {
        var title = PathUtil.StripControl(value).Trim();
        if (title.Length == 0)
            throw new UsageException("--title needs a text.");
        if (title.Length > MaxTitleLength)
            throw new UsageException($"--title is longer than {MaxTitleLength} characters.");
        return title;
    }

    /// <summary>Maps the Windows option style onto the canonical one: <c>/i</c> becomes <c>-i</c>, <c>/input</c> becomes <c>--input</c>.</summary>
    static string WithDashes(string arg)
    {
        if (!arg.StartsWith('/'))
            return arg;
        return (arg.Length == 2 ? "-" : "--") + arg[1..];
    }

    static int Run(Options options)
    {
        var warnings = new List<string>();
        var (merged, parsed) = ReadInputs(options.Inputs, warnings);

        var excluded = merged.Values.Where(f => IsExcluded(f.Path, options.Excludes)).ToList();
        foreach (var file in excluded)
            merged.Remove(file.Path);
        if (merged.Count == 0)
            throw new InvalidDataException("--exclude removed every file from the report.");

        var report = new Report
        {
            Files = merged.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            GeneratedAt = DateTimeOffset.Now,
            Inputs = parsed,
            Title = options.Title,
        };

        var resolver = options.SourceDirs.Count > 0 ? new SourceResolver(options.SourceDirs) : null;
        var read = ResolveSources(report, resolver, options);
        foreach (var file in report.Files.Where(f => f.Source != null))
        {
            file.Metrics = SourceResolver.Analyze(file.Path, file.Source!);
            file.Metrics.AttachCoverage(file);
        }
        if (resolver != null)
            report.Unmeasured = FindUnmeasured(resolver, read, report.Files.Concat(excluded), options.Excludes, warnings);

        if (options.HistoryDir != null)
        {
            var runs = Directory.Exists(options.HistoryDir) ? History.Load(options.HistoryDir, warnings) : [];
            runs.Add(History.CurrentRun(report));
            report.History = runs.TakeLast(History.MaxRuns).ToList();
        }

        HtmlReport.Write(report, options.Output!, options.Markdown ? MarkdownReport.Render(report) : null, warnings);

        // Saved only after the report was written, so a failed run never shows up in the history.
        if (options.HistoryDir != null)
            History.Save(options.HistoryDir, report, warnings);

        foreach (var warning in warnings)
            Console.Error.WriteLine("warning: " + Safe(warning));
        PrintSummary(report, excluded.Count, options);
        return Success;
    }

    /// <summary>Parses every input and merges the results into one entry per source file.</summary>
    static (Dictionary<string, FileCoverage> Merged, List<string> Parsed) ReadInputs(List<string> inputs, List<string> warnings)
    {
        var merged = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<string>();
        foreach (var input in ExpandInputs(inputs))
        {
            var text = Parsers.ReadText(input.Path);
            var format = Parsers.Detect(text);
            if (format == InputFormat.Unknown)
            {
                if (input.Explicit)
                    throw new InvalidDataException($"Not a Cobertura, lcov or gcov file: {input.Path}");
                continue;  // picked up by a folder scan or wildcard, so an unrelated .xml is not an error
            }
            var inputWarnings = new List<string>();
            var files = Parsers.Parse(format, text, inputWarnings);
            warnings.AddRange(inputWarnings.Select(w => $"{input.Path}: {w}"));
            parsed.Add(input.Path);

            // Relative source paths are resolved against the folder of the coverage file that names them.
            var folder = Path.GetDirectoryName(Path.GetFullPath(input.Path))!;
            foreach (var file in files)
            {
                file.Origins.Add((file.RawPath, folder));
                if (merged.TryGetValue(file.Path, out var existing))
                    existing.Merge(file);
                else
                    merged[file.Path] = file;
            }
            if (merged.Count > HtmlReport.MaxFiles)
                throw new InvalidDataException($"The input lists more than {HtmlReport.MaxFiles} source files.");
        }

        if (merged.Count == 0)
            throw new InvalidDataException("The input contains no coverage data.");
        PathUtil.MergeRelativeIntoAbsolute(merged);
        return (merged, parsed);
    }

    readonly record struct Input(string Path, bool Explicit);

    static readonly string[] ScanExtensions = [".gcov", ".info", ".lcov", ".xml", ".dat", ".cobertura"];

    /// <summary>
    /// Expands folders and wildcards into input files, each listed once. Only files named directly are
    /// <see cref="Input.Explicit"/>: for them, an unknown format is an error rather than a file to skip.
    /// </summary>
    static List<Input> ExpandInputs(List<string> inputs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Input>();
        void Add(string path, bool isExplicit)
        {
            if (seen.Add(Path.GetFullPath(path)))
                result.Add(new Input(path, isExplicit));
        }

        var recursive = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device };
        var flat = new EnumerationOptions { MatchType = MatchType.Simple, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory };

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var found = Directory.EnumerateFiles(input, "*", recursive)
                    .Where(f => ScanExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (found.Count == 0)
                    throw new InvalidDataException($"No coverage files found in folder: {input}");
                found.ForEach(f => Add(f, false));
            }
            else if (input.Contains('*') || input.Contains('?'))
            {
                var directory = Path.GetDirectoryName(input);
                var pattern = Path.GetFileName(input);
                if (directory != null && (directory.Contains('*') || directory.Contains('?')))
                    throw new UsageException($"Wildcards are only allowed in the last path segment: {input}");
                directory = string.IsNullOrEmpty(directory) ? "." : directory;
                if (!Directory.Exists(directory))
                    throw new InvalidDataException($"Folder not found: {directory}");
                var found = Directory.EnumerateFiles(directory, pattern, flat).Order(StringComparer.OrdinalIgnoreCase).ToList();
                if (found.Count == 0)
                    throw new InvalidDataException($"No files match: {input}");
                found.ForEach(f => Add(f, false));
            }
            else if (File.Exists(input))
            {
                Add(input, true);
            }
            else
            {
                throw new InvalidDataException($"Input not found: {input}");
            }
        }
        return result;
    }

    internal static bool IsExcluded(string path, List<string> patterns) =>
        patterns.Any(p => FileSystemName.MatchesSimpleExpression(p.Replace('\\', '/'), path, ignoreCase: true));

    /// <summary>
    /// Loads the source text of every file in the report and returns the paths that were read.
    /// </summary>
    /// <remarks>
    /// The path named in the coverage file is tried first, then the <c>--source-dir</c> folders by path tail. Only
    /// C/C++ and assembler files are read, and never from a network location. With <c>--source-dir-only</c>, a
    /// coverage file cannot pull in anything outside the folders the user named. A file on disk takes precedence over
    /// the source text embedded in a gcov file, since that text could be forged.
    /// </remarks>
    static HashSet<string> ResolveSources(Report report, SourceResolver? resolver, Options options)
    {
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in report.Files)
        {
            string? path = null;
            var nameOnly = false;
            var ambiguous = false;
            if (!options.SourceDirOnly)
            {
                path = file.Origins.Select(o => SourceResolver.LocalPath(o.Path, o.Folder))
                    .FirstOrDefault(p => p != null && SourceResolver.IsPlainFile(p));
            }
            if (path == null && resolver != null)
            {
                path = resolver.Find(file.Path, out ambiguous, out nameOnly);

                // A file that merely shares the name must not replace the gcov text of the real one.
                if (nameOnly && file.Source != null)
                    continue;
            }
            if (path == null)
            {
                if (file.Source == null)
                    file.SourceProblem = SourceProblem(resolver != null, ambiguous, options.SourceDirOnly);
                continue;
            }

            // A tail match says nothing about the file's real location, and a relative path may lead into an
            // excluded folder, so the excludes are applied to the path on disk as well.
            if (IsExcluded(path.Replace('\\', '/'), options.Excludes) || (resolver != null && IsExcluded(resolver.RelativeToRoot(path), options.Excludes)))
            {
                file.Source = null;
                file.SourceProblem = "the matching file on disk is excluded by --exclude";
                continue;
            }

            var source = SourceResolver.Load(path);
            if (source != null)
            {
                file.Source = source;
                file.SourceFromCoverageFile = false;
                file.SourceMatchedByNameOnly = nameOnly;
                read.Add(path);
            }
            else if (file.Source == null)
            {
                file.SourceProblem = "the file could not be read";
            }
        }
        return read;
    }

    static string SourceProblem(bool sourceDirs, bool ambiguous, bool sourceDirOnly) =>
        ambiguous ? "several files in the source folders match this path"
        : sourceDirOnly && !sourceDirs ? "--source-dir-only was given without --source-dir"
        : sourceDirOnly ? "not found in the source folders"
        : sourceDirs ? "not found at the path in the coverage file, nor in the source folders"
        : "not found at the path in the coverage file";

    /// <summary>
    /// Lists the implementation files in the <c>--source-dir</c> folders that no coverage file covers, with their LOC.
    /// </summary>
    /// <remarks>
    /// A file counts as covered if it was read for the report or is a best tail match of any coverage path, ties
    /// included, so an ambiguous match never appears here. Excluded coverage entries count as covered.
    /// </remarks>
    static List<UnmeasuredFile> FindUnmeasured(SourceResolver resolver, HashSet<string> read, IEnumerable<FileCoverage> covered, List<string> excludes, List<string> warnings)
    {
        var listed = new HashSet<string>(read, StringComparer.OrdinalIgnoreCase);
        foreach (var file in covered)
            listed.UnionWith(resolver.Candidates(file.Path));

        var result = new List<UnmeasuredFile>();
        foreach (var path in resolver.Files().Where(SourceResolver.IsImplementation).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (listed.Contains(path) || IsExcluded(path.Replace('\\', '/'), excludes) || IsExcluded(resolver.RelativeToRoot(path), excludes))
                continue;
            if (result.Count == HtmlReport.MaxFiles)
            {
                warnings.Add($"More than {HtmlReport.MaxFiles} files without coverage data; only the first are listed.");
                break;
            }
            var source = SourceResolver.Load(path);
            result.Add(new UnmeasuredFile(path, source != null ? SourceResolver.Analyze(path, source).Loc : null));
        }
        return result;
    }

    static void PrintSummary(Report report, int excluded, Options options)
    {
        var t = report.Totals;
        Console.WriteLine($"Files:    {t.FileCount}" + (excluded > 0 ? $" ({excluded} excluded)" : ""));
        Console.WriteLine($"Line:     {Report.PercentOf(t.Line)} ({t.Line.Covered}/{t.Line.Total})");
        Console.WriteLine($"Branch:   {(t.FilesWithBranchData > 0 ? $"{Report.PercentOf(t.Branch)} ({t.Branch.Covered}/{t.Branch.Total})" : "n/a")}");
        Console.WriteLine($"Method:   {(t.FilesWithMethodData > 0 ? $"{Report.PercentOf(t.Method)} ({t.Method.Covered}/{t.Method.Total})" : "n/a")}");
        Console.WriteLine($"LOC:      {(t.FilesWithMetrics > 0 ? t.Loc.ToString() : "n/a")}");
        Console.WriteLine($"CCM:      {(t.FilesWithComplexity > 0 ? $"{t.Complexity} in {t.Functions} functions, highest {t.MaxComplexity}" : "n/a")}");
        Console.WriteLine($"CRAP:     {(t.MaxCrap is { } crap ? $"highest {Report.Score(crap)}, {t.FunctionsAboveCrapLimit} of {t.FunctionsWithCrap} functions above {FunctionMetrics.CrapLimit}" : "n/a")}");

        if (t.FilesWithBranchData < t.FileCount)
            Console.WriteLine($"info: no branch data for {t.FileCount - t.FilesWithBranchData} of {t.FileCount} file(s); shown as n/a.");
        if (t.FilesWithMethodData < t.FileCount)
            Console.WriteLine($"info: no method data for {t.FileCount - t.FilesWithMethodData} of {t.FileCount} file(s); shown as n/a.");
        var noSource = report.Files.Count(f => f.Source == null);
        if (noSource > 0)
            Console.WriteLine($"info: no source code for {noSource} file(s); use --source-dir.");
        if (report.Unmeasured.Count > 0)
            Console.WriteLine($"info: {report.Unmeasured.Count} file(s) in the source folders have no coverage data; listed in the report, not counted.");
        if (t.FilesIncomplete > 0)
            Console.WriteLine($"info: analysis incomplete in {t.FilesIncomplete} file(s): braces do not add up, LOC, CCM and CRAP may be too low.");
        if (t.FilesMatchedByNameOnly > 0)
            Console.WriteLine($"info: {t.FilesMatchedByNameOnly} file(s) matched by file name only: source, LOC, CCM and CRAP may belong to a different file.");

        Console.WriteLine("Report:   " + Safe(Path.Combine(options.Output!, "index.html")));
        if (options.Markdown)
            Console.WriteLine("Markdown: " + Safe(Path.Combine(options.Output!, "coverage.md")));
    }

    static void Error(string message) => Console.Error.WriteLine("error: " + Safe(message));

    // Paths can come from coverage files, so control characters are stripped before anything reaches the terminal.
    static string Safe(string text) => PathUtil.StripControl(text);
}
