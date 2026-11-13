// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

namespace CoverageReport;

/// <summary>
/// Locates and loads source files: at the path a coverage file names, or by the tail of that path inside the
/// <c>--source-dir</c> folders. Only files with a C/C++ or assembler extension are ever read.
/// </summary>
/// <remarks>
/// Coverage files are untrusted. A path in them must never pull an arbitrary file into the report, and it must never
/// make this tool contact another computer.
/// </remarks>
sealed class SourceResolver(IEnumerable<string> roots)
{
    const long MaxSourceBytes = 32 * 1024 * 1024;

    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cc", ".cpp", ".cxx", ".c++", ".hh", ".hpp", ".hxx", ".h++", ".inl", ".ipp", ".tpp", ".tcc", ".inc",
        ".s", ".asm",
    };

    // Files that are compiled on their own. Headers are left out, since they appear in coverage data only when they
    // contain inline code.
    static readonly HashSet<string> ImplementationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cc", ".cpp", ".cxx", ".c++", ".s", ".asm",
    };

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    // Attributes of a file whose content is stored elsewhere, such as online only. Reading it would make Windows
    // fetch it over the network. 0x40000 and 0x400000 are RECALL_ON_OPEN and RECALL_ON_DATA_ACCESS.
    const FileAttributes Remote = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    // Drive letter to "is a local drive", determined once per letter.
    static readonly Dictionary<char, bool> LocalDrives = [];

    readonly List<string> _roots = roots.Select(Path.GetFullPath).ToList();
    Dictionary<string, List<string>>? _byName;

    public static bool IsAssembly(string path) => Path.GetExtension(path).ToLowerInvariant() is ".s" or ".asm";

    public static bool IsImplementation(string path) => ImplementationExtensions.Contains(Path.GetExtension(path));

    /// <summary>Runs the analyzer that fits the file type. Decisions are recorded for C and C++ only.</summary>
    public static SourceMetrics Analyze(string path, string[] source, DecisionMap? decisions = null) => IsAssembly(path)
        ? AssemblyAnalyzer.Analyze(source, atStartsComment: !Path.GetExtension(path).Equals(".asm", StringComparison.OrdinalIgnoreCase))
        : CodeAnalyzer.Analyze(source, decisions);

    /// <summary>
    /// Maps a path from a coverage file to a path on this computer, or returns <c>null</c> if it must not be read.
    /// </summary>
    /// <remarks>
    /// Windows paths are used as they are, WSL paths such as <c>/mnt/d/x.c</c> become <c>D:\x.c</c>, and relative
    /// paths are resolved against the folder of the coverage file. Rejected are network paths and mapped network
    /// drives, since merely opening one would contact another computer and offer it the user's Windows login, as well
    /// as devices, alternate data streams and any extension other than C/C++ or assembler.
    /// </remarks>
    public static string? LocalPath(string path, string folder)
    {
        path = path.Replace('\\', '/');
        string full;
        if (path.StartsWith("//", StringComparison.Ordinal))
            return null;
        if (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
            full = path;
        else if (path.Split('/') is ["", "mnt", [var drive], .. var rest] && char.IsAsciiLetter(drive) && rest.Length > 0)
            full = char.ToUpperInvariant(drive) + ":/" + string.Join('/', rest);
        else if (path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
            return null;  // a Linux path, or a drive-relative path such as C:a.c
        else
            full = Path.Combine(folder, path);

        try
        {
            full = Path.GetFullPath(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0 || !IsLocalDrive(full[0]))
            return null;
        var name = Path.GetFileName(full);
        if (!Extensions.Contains(Path.GetExtension(full)) || DeviceNames.Contains(name.Split('.')[0].TrimEnd(' ')))
            return null;
        return full;
    }

    // Checked before any file on the drive is touched. A missing drive does not count as local, because accessing it
    // could make Windows reconnect a stored network mapping.
    static bool IsLocalDrive(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        lock (LocalDrives)
        {
            if (!LocalDrives.TryGetValue(letter, out var local))
            {
                try
                {
                    local = new DriveInfo(letter.ToString()).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;
                }
                catch (ArgumentException)
                {
                    local = false;
                }
                LocalDrives[letter] = local;
            }
            return local;
        }
    }

    /// <summary>Whether the path is an existing regular file: not a folder, link or device.</summary>
    public static bool IsPlainFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    }

    /// <summary>
    /// Reads a source file as lines, or returns <c>null</c> if it cannot be read, is too large, or is stored online only.
    /// </summary>
    public static string[]? Load(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & Remote) != 0)
                return null;
            var text = Parsers.ReadText(path, MaxSourceBytes);
            var lines = text.Split('\n');
            if (lines.Length > Parsers.MaxLine)
                return null;
            for (var i = 0; i < lines.Length; i++)
                lines[i] = lines[i].TrimEnd('\r');
            return text.EndsWith('\n') ? lines[..^1] : lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    // Indexes every source file below the roots by file name, on first use.
    // Limitation: a root as large as a whole drive makes this slow.
    Dictionary<string, List<string>> Index()
    {
        if (_byName != null)
            return _byName;
        _byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device,
        };
        foreach (var root in _roots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (!Extensions.Contains(Path.GetExtension(file)))
                    continue;
                var name = Path.GetFileName(file);
                if (!_byName.TryGetValue(name, out var list))
                    _byName[name] = list = [];
                list.Add(file);
            }
        }
        return _byName;
    }

    /// <summary>
    /// Finds the file whose path shares the longest tail with <paramref name="coveragePath"/>, so that
    /// <c>/mnt/d/proj/src/a.c</c> finds <c>D:\proj\src\a.c</c>. A tie is not resolved by guessing.
    /// </summary>
    public string? Find(string coveragePath, out bool ambiguous) => Find(coveragePath, out ambiguous, out _);

    /// <inheritdoc cref="Find(string, out bool)"/>
    /// <param name="nameOnly">Set if only the file name matched, so the result may be a different file of the same name.</param>
    public string? Find(string coveragePath, out bool ambiguous, out bool nameOnly)
    {
        var best = Best(coveragePath, out var score);
        ambiguous = best.Count > 1;
        nameOnly = score == 1 && coveragePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 1;
        return best.Count == 1 ? best[0] : null;
    }

    /// <summary>Every file that matches the path best, ties included. None of them can be said to lack coverage data.</summary>
    public List<string> Candidates(string coveragePath) => Best(coveragePath, out _);

    List<string> Best(string coveragePath, out int bestScore)
    {
        var best = new List<string>();
        bestScore = 0;
        var parts = coveragePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !Index().TryGetValue(parts[^1], out var candidates))
            return best;

        foreach (var candidate in candidates)
        {
            var score = CommonTail(parts, candidate.Split(Path.DirectorySeparatorChar));
            if (score > bestScore)
            {
                best.Clear();
                bestScore = score;
            }
            if (score == bestScore)
                best.Add(candidate);
        }
        return best;
    }

    /// <summary>All indexed source files below the roots.</summary>
    public IEnumerable<string> Files() => Index().Values.SelectMany(list => list);

    /// <summary>
    /// The file's path below its source folder, with forward slashes, so that <c>--exclude "vendor/*"</c> works like
    /// <c>"*/vendor/*"</c>.
    /// </summary>
    public string RelativeToRoot(string file)
    {
        foreach (var root in _roots)
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                return relative.Replace('\\', '/');
        }
        return file.Replace('\\', '/');
    }

    static int CommonTail(string[] a, string[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && string.Equals(a[^(n + 1)], b[^(n + 1)], StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }
}
