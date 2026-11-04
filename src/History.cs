// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;

namespace CoverageReport;

/// <summary>Line, branch and method counts of one run; <c>null</c> where the input carried no such data.</summary>
readonly record struct Snapshot(Counts? Line, Counts? Branch, Counts? Method);

/// <summary>One stored run: its time, the project totals and the counts per file.</summary>
sealed record HistoryRun(DateTimeOffset Time, Snapshot Totals, Dictionary<string, Snapshot> Files);

/// <summary>
/// Stores one JSON file per run in the history folder and reads the newest runs back for the trend chart.
/// The files hold paths and counts only, never source code.
/// </summary>
static class History
{
    public const int MaxRuns = 50;
    const int MaxFilesRead = 200;
    public const long MaxFileBytes = 64 * 1024 * 1024;
    const long MaxBytesRead = 256 * 1024 * 1024;
    const string Prefix = "coveragereport-";

    // Runs written by hw-coverage, the former name of this tool, are still read.
    static readonly string[] Prefixes = [Prefix, "hw-coverage-"];

    public static HistoryRun CurrentRun(Report report)
    {
        var files = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in report.Files)
            files[file.Path] = Report.SnapshotOf(file);
        return new HistoryRun(report.GeneratedAt, report.Totals.ToSnapshot(), files);
    }

    /// <summary>Writes the run to a new file. A run too large to be read back later is discarded with a warning.</summary>
    public static void Save(string directory, Report report, List<string>? warnings = null, long maxBytes = MaxFileBytes)
    {
        Directory.CreateDirectory(directory);
        var now = report.GeneratedAt.ToUniversalTime();
        var path = Path.Combine(directory, $"{Prefix}{now:yyyyMMddTHHmmssfff}Z-{Guid.NewGuid().ToString("N")[..8]}.json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            Write(json, now, report);

        // Keeping it would only produce an "invalid file" warning on every later run.
        if (new FileInfo(path).Length > maxBytes)
        {
            File.Delete(path);
            warnings?.Add($"This run is too large for the history (limit {maxBytes / (1024 * 1024)} MB) and was not saved.");
        }
    }

    static void Write(Utf8JsonWriter json, DateTimeOffset now, Report report)
    {
        json.WriteStartObject();
        json.WriteNumber("version", 1);
        json.WriteString("timestamp", now);
        WriteSnapshot(json, report.Totals.ToSnapshot());
        json.WriteStartArray("files");
        foreach (var file in report.Files)
        {
            json.WriteStartObject();
            json.WriteString("path", file.Path);
            WriteSnapshot(json, Report.SnapshotOf(file));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    static void WriteSnapshot(Utf8JsonWriter json, Snapshot s)
    {
        WriteCounts(json, "line", s.Line);
        WriteCounts(json, "branch", s.Branch);
        WriteCounts(json, "method", s.Method);
    }

    static void WriteCounts(Utf8JsonWriter json, string name, Counts? counts)
    {
        if (counts is not { } c)
        {
            json.WriteNull(name);
            return;
        }
        json.WriteStartObject(name);
        json.WriteNumber("covered", c.Covered);
        json.WriteNumber("total", c.Total);
        json.WriteEndObject();
    }

    /// <summary>
    /// Reads up to <see cref="MaxRuns"/> of the newest valid runs, oldest first. Invalid files and files dated in
    /// the future are skipped with a warning.
    /// </summary>
    /// <remarks>
    /// File names start with the timestamp, so the newest files are found without parsing any of them, and reading
    /// stops once enough runs are in. The number and size of the files therefore cannot drive memory use.
    /// Limitation: forged names or dates close to now can push real runs out of the chart. That requires write
    /// access to the history folder and affects nothing but the chart.
    /// </remarks>
    public static List<HistoryRun> Load(string directory, List<string> warnings)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Directory, MatchType = MatchType.Simple };
        var paths = Prefixes
            .SelectMany(prefix => Directory.EnumerateFiles(directory, prefix + "*.json", options).Select(path => (Key: Path.GetFileName(path)[prefix.Length..], Path: path)))
            .OrderByDescending(file => file.Key, StringComparer.Ordinal)
            .Select(file => file.Path);

        var latestAllowed = DateTimeOffset.Now.AddMinutes(10);
        var runs = new List<HistoryRun>();
        var budget = MaxBytesRead;
        var attempts = 0;
        foreach (var path in paths)
        {
            if (runs.Count == MaxRuns)
                break;
            var size = new FileInfo(path).Length;
            if (++attempts > MaxFilesRead || size > budget)
            {
                warnings.Add("History folder is too large to read completely: older runs are left out of the chart.");
                break;
            }
            budget -= size;

            var run = TryRead(path);
            if (run == null)
                warnings.Add($"Ignored invalid history file: {Path.GetFileName(path)}");
            else if (run.Time > latestAllowed)
                warnings.Add($"Ignored history file dated in the future: {Path.GetFileName(path)}");  // it would push real runs out
            else
                runs.Add(run);
        }
        return runs.OrderBy(r => r.Time).ToList();
    }

    static HistoryRun? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1)
                return null;
            var time = DateTimeOffset.Parse(root.GetProperty("timestamp").GetString() ?? "", CultureInfo.InvariantCulture);

            var files = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in root.GetProperty("files").EnumerateArray())
                files[file.GetProperty("path").GetString() ?? ""] = ReadSnapshot(file);
            return new HistoryRun(time, ReadSnapshot(root), files);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    static Snapshot ReadSnapshot(JsonElement e) => new(ReadCounts(e, "line"), ReadCounts(e, "branch"), ReadCounts(e, "method"));

    static Counts? ReadCounts(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return null;
        var covered = e.GetProperty("covered").GetInt64();
        var total = e.GetProperty("total").GetInt64();
        if (covered < 0 || total < 0 || covered > total)
            throw new FormatException("Invalid counts.");
        return new Counts(covered, total);
    }
}
