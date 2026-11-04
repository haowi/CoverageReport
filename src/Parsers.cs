// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Xml;

namespace CoverageReport;

enum InputFormat { Unknown, Lcov, Gcov, Cobertura }

/// <summary>Parsers for the supported coverage formats: lcov, gcov text and Cobertura XML.</summary>
/// <remarks>
/// All input is untrusted. Every parser validates numbers against fixed limits, counts malformed records instead of
/// failing on them, and reports them as a warning, so that incomplete data never passes silently as complete.
/// </remarks>
static class Parsers
{
    public const long MaxInputBytes = 512L * 1024 * 1024;
    public const int MaxLine = 2_000_000;  // the report shows one row per line, so this also bounds a page's size
    const int MaxXmlDepth = 64;
    const int MaxBranchesPerLine = 256;

    /// <summary>Detects the format from the content, not from the file extension.</summary>
    public static InputFormat Detect(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('<'))
                return IsCobertura(text) ? InputFormat.Cobertura : InputFormat.Unknown;
            if (line.StartsWith("TN:", StringComparison.Ordinal) || line.StartsWith("SF:", StringComparison.Ordinal))
                return InputFormat.Lcov;
            if (TryParseGcovLine(raw, out _, out var number, out var rest) && number == 0 && rest.StartsWith("Source:", StringComparison.Ordinal))
                return InputFormat.Gcov;
            return InputFormat.Unknown;
        }
        return InputFormat.Unknown;
    }

    public static List<FileCoverage> Parse(InputFormat format, string text, List<string> warnings) => format switch
    {
        InputFormat.Lcov => ParseLcov(text, warnings),
        InputFormat.Gcov => ParseGcov(text, warnings),
        InputFormat.Cobertura => ParseCobertura(text, warnings),
        _ => throw new InvalidDataException("Unsupported coverage format."),
    };

    // ---- lcov ----

    /// <summary>Parses lcov tracefiles, including the <c>FNL</c>/<c>FNA</c> function records of lcov 2.2 and later.</summary>
    public static List<FileCoverage> ParseLcov(string text, List<string> warnings)
    {
        var result = new List<FileCoverage>();
        var malformed = 0;
        var unterminated = 0;
        var mismatched = 0;
        FileCoverage? file = null;
        int? reportedLines = null;
        var functions = new Dictionary<string, (int Line, int End, long Hits)>();
        var indexed = new Dictionary<string, (int Line, int End)>();  // FNL records, keyed by function index
        var branches = new SortedDictionary<int, List<long>>();

        void Flush()
        {
            if (file == null)
                return;
            if (reportedLines != null && reportedLines != file.Lines.Count)
                mismatched++;
            reportedLines = null;
            foreach (var (line, taken) in branches)
                file.AddBranches(line, taken);
            foreach (var (name, f) in functions)
                file.AddMethod(name, f.Line, f.End, f.Hits);
            file.HasBranchData = branches.Count > 0;
            file.HasMethodData = functions.Count > 0;
            result.Add(file);
            file = null;
            functions.Clear();
            indexed.Clear();
            branches.Clear();
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                if (file != null)
                    unterminated++;
                Flush();
                file = line.Length > 3 ? new FileCoverage(line[3..]) : null;
                continue;
            }
            if (line == "end_of_record")
            {
                Flush();
                continue;
            }
            var colon = line.IndexOf(':');
            if (file == null || colon < 0)
                continue;

            var value = line[(colon + 1)..];
            var ok = true;
            switch (line[..colon])
            {
                case "LF":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                        reportedLines = count;
                    break;
                case "DA":
                {
                    var parts = value.Split(',');
                    ok = parts.Length >= 2 && TryLine(parts[0], out var number) && TryHits(parts[1], out var hits);
                    if (ok)
                    {
                        TryLine(parts[0], out number);
                        TryHits(parts[1], out hits);
                        file.AddLine(number, hits);
                    }
                    break;
                }
                case "BRDA":
                {
                    var parts = value.Split(',');
                    ok = parts.Length == 4 && TryLine(parts[0], out _) && (parts[3] == "-" || TryHits(parts[3], out _));
                    if (ok)
                    {
                        TryLine(parts[0], out var number);
                        long taken = 0;
                        if (parts[3] != "-")
                            TryHits(parts[3], out taken);
                        if (!branches.TryGetValue(number, out var list))
                            branches[number] = list = [];
                        list.Add(taken);
                    }
                    break;
                }
                case "FN":
                {
                    // FN:<line>,<name> or FN:<line>,<end>,<name>. Demangled names may themselves contain commas.
                    var parts = value.Split(',', 3);
                    ok = parts.Length >= 2 && TryLine(parts[0], out _);
                    if (ok)
                    {
                        TryLine(parts[0], out var start);
                        var end = 0;
                        string name;
                        if (parts.Length == 3 && TryLine(parts[1], out end))
                            name = parts[2];
                        else
                            name = value[(parts[0].Length + 1)..];
                        var hits = functions.GetValueOrDefault(name).Hits;
                        functions[name] = (start, end, hits);
                    }
                    break;
                }
                case "FNDA":
                {
                    var comma = value.IndexOf(',');
                    ok = comma > 0 && TryHits(value[..comma], out _);
                    if (ok)
                    {
                        TryHits(value[..comma], out var hits);
                        var name = value[(comma + 1)..];
                        functions[name] = functions.GetValueOrDefault(name) with { Hits = hits };
                    }
                    break;
                }
                case "FNL":
                {
                    var parts = value.Split(',');
                    ok = parts.Length >= 2 && TryLine(parts[1], out _);
                    if (ok)
                    {
                        TryLine(parts[1], out var start);
                        var end = 0;
                        if (parts.Length > 2)
                            TryLine(parts[2], out end);
                        indexed[parts[0]] = (start, end);
                    }
                    break;
                }
                case "FNA":
                {
                    var parts = value.Split(',', 3);
                    ok = parts.Length == 3 && TryHits(parts[1], out _);
                    if (ok)
                    {
                        TryHits(parts[1], out var hits);
                        var location = indexed.GetValueOrDefault(parts[0]);
                        functions[parts[2]] = (location.Line, location.End, hits);
                    }
                    break;
                }
            }
            if (!ok)
                malformed++;
        }
        if (file != null)
            unterminated++;
        Flush();

        if (malformed > 0)
            warnings.Add($"Skipped {malformed} malformed lcov line(s).");
        if (unterminated > 0)
            warnings.Add($"{unterminated} record(s) have no end_of_record: the file may be truncated.");
        if (mismatched > 0)
            warnings.Add($"{mismatched} record(s) report a different line count than they list: the file may be incomplete.");
        return result;
    }

    // ---- gcov (text) ----

    /// <summary>Parses gcov text output. The embedded source text is kept, marked as coming from the coverage file.</summary>
    public static List<FileCoverage> ParseGcov(string text, List<string> warnings)
    {
        var result = new List<FileCoverage>();
        FileCoverage? file = null;
        var source = new List<string>();
        var pending = new List<(string Name, long Hits)>();
        var branches = new SortedDictionary<int, List<long>>();
        var inSection = false;       // inside a per-instantiation block, whose counts are already part of the aggregate
        var afterSeparator = false;
        var currentLine = 0;
        var malformed = 0;

        // gcov -t writes several files into one stream, each starting with its own Source: line.
        void Finish()
        {
            if (file == null)
                return;
            foreach (var (name, called) in pending)
                file.AddMethod(name, 0, 0, called);
            foreach (var (line, taken) in branches)
                file.AddBranches(line, taken);
            file.Source = source.ToArray();
            file.SourceFromCoverageFile = true;
            result.Add(file);
            file = null;
            source.Clear();
            pending.Clear();
            branches.Clear();
            inSection = false;
            currentLine = 0;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("------------------", StringComparison.Ordinal))
            {
                afterSeparator = true;
                continue;
            }

            if (TryParseGcovLine(line, out var hits, out var number, out var rest))
            {
                if (afterSeparator)
                {
                    inSection = false;
                    afterSeparator = false;
                }
                if (number == 0)
                {
                    if (rest.StartsWith("Source:", StringComparison.Ordinal) && rest.Length > 7)
                    {
                        Finish();
                        file = new FileCoverage(rest[7..]);
                    }
                    continue;
                }
                if (number > MaxLine)
                    throw new InvalidDataException($"gcov line number {number} is above the limit of {MaxLine}.");
                if (file == null)
                {
                    malformed++;
                    continue;
                }

                currentLine = number;
                foreach (var (name, called) in pending)
                    file.AddMethod(name, number, 0, called);
                pending.Clear();

                if (inSection)
                    continue;
                // gcov lists every source line in order, so a jump indicates a damaged or forged file. Creating rows
                // for the missing lines could exhaust memory, and skipping them would inflate the coverage.
                if (number > source.Count + 1)
                    throw new InvalidDataException($"gcov line numbers jump from {source.Count} to {number}: the file is damaged, or not plain gcov text.");
                if (hits is { } count)
                    file.AddLine(number, count);
                if (number > source.Count)
                    source.Add(rest);
                else
                    source[number - 1] = rest;
                continue;
            }

            if (afterSeparator)
            {
                inSection = line.EndsWith(':');
                afterSeparator = false;
                if (inSection)
                    continue;
            }
            if (file == null)
                continue;

            if (line.StartsWith("function ", StringComparison.Ordinal))
            {
                var called = line.LastIndexOf(" called ", StringComparison.Ordinal);
                var after = called < 0 ? -1 : line.IndexOf(' ', called + 8);
                if (called > 9 && after > 0 && TryCount(line[(called + 8)..after], out var callCount))
                {
                    pending.Add((line[9..called], callCount));
                    file.HasMethodData = true;
                    file.HasBranchData = true;  // function records are only written with -b, which also writes branches
                }
                else
                {
                    malformed++;
                }
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                file.HasBranchData = true;
                if (!TryBranch(line, out var taken) || currentLine == 0)
                {
                    malformed++;
                    continue;
                }
                if (!branches.TryGetValue(currentLine, out var list))
                    branches[currentLine] = list = [];
                list.Add(taken);
            }
            else if (line.StartsWith("call ", StringComparison.Ordinal))
            {
                file.HasBranchData = true;
            }
        }

        Finish();
        if (malformed > 0)
            warnings.Add($"Skipped {malformed} malformed gcov line(s).");
        return result;
    }

    // Parses a gcov source line such as "        3:   13:code", "    #####:   18:code" or "        -:    0:Source:x".
    static bool TryParseGcovLine(string line, out long? hits, out int number, out string rest)
    {
        hits = null;
        number = 0;
        rest = "";
        var first = line.IndexOf(':');
        if (first <= 0)
            return false;
        var second = line.IndexOf(':', first + 1);
        if (second < 0 || !int.TryParse(line.AsSpan(first + 1, second - first - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
            return false;

        var count = line[..first].Trim();
        rest = line[(second + 1)..];
        switch (count)
        {
            case "-":
                return true;
            case "#####" or "=====":
                hits = 0;
                return true;
        }
        if (!TryCount(count.TrimEnd('*'), out var value))
            return false;
        hits = value;
        return true;
    }

    // Parses a plain count or a gcov -H abbreviation such as 1.2k.
    static bool TryCount(string text, out long value)
    {
        value = 0;
        if (text.Length == 0)
            return false;
        var factor = text[^1] switch { 'k' => 1e3, 'M' => 1e6, 'G' => 1e9, 'T' => 1e12, 'P' => 1e15, 'E' => 1e18, _ => 1.0 };
        if (factor == 1.0)
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        if (!double.TryParse(text[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return false;
        var scaled = number * factor;
        value = scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
        return true;
    }

    // Parses "branch  0 taken 3 (fallthrough)", "branch  1 taken 50%" or "branch  2 never executed".
    static bool TryBranch(string line, out long taken)
    {
        taken = 0;
        if (line.Contains(" never executed", StringComparison.Ordinal))
            return true;
        var at = line.IndexOf(" taken ", StringComparison.Ordinal);
        if (at < 0)
            return false;
        var value = line[(at + 7)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return TryCount(value.TrimEnd('%'), out taken);
    }

    // ---- Cobertura ----

    // Real Cobertura files reference a DTD on the internet, so a DOCTYPE cannot simply be rejected. It is skipped
    // unread instead: without a resolver nothing is ever fetched, and the DTD's entities stay undefined.
    static XmlReaderSettings XmlSettings => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
    };

    static bool IsCobertura(string text)
    {
        try
        {
            using var xml = XmlReader.Create(new StringReader(text), XmlSettings);
            return xml.MoveToContent() == XmlNodeType.Element && xml.LocalName == "coverage";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Parses Cobertura XML as written by gcovr and similar tools.</summary>
    public static List<FileCoverage> ParseCobertura(string text, List<string> warnings)
    {
        var files = new List<FileCoverage>();
        var malformed = 0;
        string? sourceRoot = null;
        FileCoverage? file = null;
        var methodLines = new Dictionary<int, long>();
        var classLines = new HashSet<int>();
        var branchesByLine = new Dictionary<int, (long Covered, long Total)>();
        string? method = null;
        var methodHits = 0L;
        var methodStart = 0;
        var methodEnd = 0;
        var inMethodLines = false;

        void EndMethod()
        {
            if (file != null && method != null)
            {
                file.AddMethod(method, methodStart, methodEnd, methodHits);
                file.HasMethodData = true;
            }
            method = null;
            methodHits = 0;
            methodStart = 0;
            methodEnd = 0;
        }

        void EndClass()
        {
            if (file == null)
                return;
            // Lines listed only under <method> still count, but only once.
            foreach (var (number, hits) in methodLines)
            {
                if (!classLines.Contains(number))
                    file.AddLine(number, hits);
            }
            foreach (var (number, b) in branchesByLine)
            {
                var taken = new long[b.Total];
                // Limitation: Cobertura reports only "covered/total", so which branches were taken is unknown.
                for (var i = 0; i < b.Covered; i++)
                    taken[i] = 1;
                file.AddBranches(number, taken);
                file.HasBranchData = true;
            }
            files.Add(file);
            file = null;
            methodLines.Clear();
            classLines.Clear();
            branchesByLine.Clear();
        }

        try
        {
            using var xml = XmlReader.Create(new StringReader(text), XmlSettings);
            while (xml.Read())
            {
                if (xml.Depth > MaxXmlDepth)
                    throw new InvalidDataException("Invalid Cobertura XML: nested too deeply.");
                if (xml.NodeType == XmlNodeType.EndElement)
                {
                    switch (xml.LocalName)
                    {
                        case "class": EndClass(); break;
                        case "method": EndMethod(); break;
                        case "lines": inMethodLines = false; break;
                    }
                    continue;
                }
                if (xml.NodeType != XmlNodeType.Element)
                    continue;

                switch (xml.LocalName)
                {
                    case "source":
                        if (sourceRoot == null && !xml.IsEmptyElement)
                            sourceRoot = xml.ReadElementContentAsString().Trim();
                        break;
                    case "class":
                    {
                        var filename = xml.GetAttribute("filename");
                        var isEmpty = xml.IsEmptyElement;
                        if (string.IsNullOrWhiteSpace(filename))
                        {
                            malformed++;
                            break;
                        }
                        file = new FileCoverage(Combine(sourceRoot, filename));
                        if (isEmpty)
                            EndClass();
                        break;
                    }
                    case "method":
                    {
                        if (file == null)
                            break;
                        method = xml.GetAttribute("name");
                        var isEmpty = xml.IsEmptyElement;
                        if (method == null)
                        {
                            malformed++;
                            break;
                        }
                        // For a method without lines, its line-rate is the only indication that it ran.
                        if (double.TryParse(xml.GetAttribute("line-rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate > 0)
                            methodHits = 1;
                        if (isEmpty)
                            EndMethod();
                        break;
                    }
                    case "lines":
                        inMethodLines = method != null && !xml.IsEmptyElement;
                        break;
                    case "line":
                    {
                        if (file == null)
                            break;
                        if (!TryLine(xml.GetAttribute("number"), out var number) || !TryHits(xml.GetAttribute("hits"), out var hits))
                        {
                            malformed++;
                            break;
                        }
                        if (inMethodLines)
                        {
                            methodLines[number] = Math.Max(methodLines.GetValueOrDefault(number), hits);
                            methodHits = Math.Max(methodHits, hits);
                            if (methodStart == 0 || number < methodStart)
                                methodStart = number;
                            methodEnd = Math.Max(methodEnd, number);
                        }
                        else
                        {
                            classLines.Add(number);
                            file.AddLine(number, hits);
                        }
                        var condition = xml.GetAttribute("condition-coverage");
                        if (TryConditionCoverage(condition, out var covered, out var total))
                            branchesByLine[number] = (covered, total);
                        else if (condition != null)
                            malformed++;  // never dropped silently, or the totals would look complete
                        break;
                    }
                }
            }
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"Invalid Cobertura XML: {e.Message}");
        }

        if (malformed > 0)
            warnings.Add($"Skipped {malformed} malformed Cobertura element(s).");
        return files;
    }

    static string Combine(string? root, string filename)
    {
        var f = filename.Replace('\\', '/');
        var absolute = f.StartsWith('/') || (f.Length > 2 && char.IsAsciiLetter(f[0]) && f[1] == ':');
        return absolute || string.IsNullOrEmpty(root) ? f : root.TrimEnd('/', '\\') + "/" + f;
    }

    // Parses a condition-coverage attribute such as "50% (1/2)".
    static bool TryConditionCoverage(string? text, out long covered, out long total)
    {
        covered = total = 0;
        if (text == null)
            return false;
        var open = text.IndexOf('(');
        var slash = text.IndexOf('/');
        var close = text.IndexOf(')');
        return open >= 0 && slash > open && close > slash
            && long.TryParse(text.AsSpan(open + 1, slash - open - 1), NumberStyles.None, CultureInfo.InvariantCulture, out covered)
            && long.TryParse(text.AsSpan(slash + 1, close - slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out total)
            && total > 0 && total <= MaxBranchesPerLine && covered <= total;
    }

    static bool TryLine(string? text, out int line) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out line) && line is >= 1 and <= MaxLine;

    static bool TryHits(string? text, out long hits) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out hits);

    // ---- file access ----

    /// <summary>Reads a text file of at most <paramref name="limit"/> bytes as UTF-8, falling back to Latin-1.</summary>
    public static string ReadText(string path, long limit = MaxInputBytes)
    {
        var info = new FileInfo(path);
        if (info.Length > limit)
            throw new InvalidDataException($"File is larger than {limit / (1024 * 1024)} MB.");
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
