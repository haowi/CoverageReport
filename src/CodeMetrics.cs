// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

namespace CoverageReport;

/// <summary>Size, complexity and coverage of one function found by the source analysis.</summary>
sealed class FunctionMetrics
{
    /// <summary>McCabe's recommended upper limit for the complexity of a single function.</summary>
    public const int ComplexityLimit = 10;

    /// <summary>The CRAP score above which a function is considered risky to change.</summary>
    public const int CrapLimit = 30;

    public required string Name;
    public int Line;                // line of the name, which is where coverage tools place a function
    public int StartLine;           // first line of the head; with #if around the head, of its first side
    public int EndLine;
    public int Loc;
    public int Complexity;
    public Counts Coverage;         // instrumented lines; Total 0 if none was instrumented
    public Counts BranchCoverage;   // Total 0 if the function has no branches

    /// <summary>
    /// The CRAP score (Change Risk Anti-Patterns) of this function, or <c>null</c> if none of its lines was measured.
    /// </summary>
    public double? Crap => CrapOf(Complexity, Coverage);

    /// <summary>
    /// CCM² × (1 − line coverage)³ + CCM: complexity weighs little in well-tested code and heavily in untested code.
    /// A fully covered function scores its CCM, an untested one CCM² + CCM.
    /// </summary>
    public static double? CrapOf(int complexity, Counts lines)
    {
        if (lines.Total == 0)
            return null;
        var missed = 1 - (double)lines.Covered / lines.Total;
        return (double)complexity * complexity * missed * missed * missed + complexity;
    }
}

/// <summary>The result of analyzing one source file.</summary>
sealed class SourceMetrics
{
    public int Loc;
    public bool IsAssembly;  // assembler source: LOC only, no functions and no CCM
    public bool Incomplete;  // braces did not balance or the function limit was hit, so the numbers may be too low
    public List<FunctionMetrics> Functions { get; } = [];
    public long Complexity => Functions.Sum(f => (long)f.Complexity);

    /// <summary>The function whose lines include <paramref name="line"/>, or <c>null</c>.</summary>
    /// <remarks>
    /// Functions are sorted and do not overlap, so a binary search suffices. That matters because the number of
    /// lookups is driven by the method count of an untrusted coverage file.
    /// </remarks>
    public FunctionMetrics? FunctionAt(int line)
    {
        int low = 0, high = Functions.Count - 1;
        FunctionMetrics? found = null;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Functions[middle].StartLine <= line)
            {
                found = Functions[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found != null && line <= found.EndLine ? found : null;
    }

    /// <summary>Sets the line and branch coverage of every function.</summary>
    /// <remarks>
    /// A function's lines run from the start of its head to its closing brace, unless the coverage tool reports an
    /// end line for a method that starts inside it. The tool knows the compiled code, so its end line wins.
    /// </remarks>
    public void AttachCoverage(FileCoverage file)
    {
        var reportedEnd = new Dictionary<FunctionMetrics, int>();
        foreach (var method in file.Methods.Values)
        {
            if (method.Line > 0 && method.EndLine >= method.Line && FunctionAt(method.Line) is { } function)
                reportedEnd[function] = Math.Max(reportedEnd.GetValueOrDefault(function), method.EndLine);
        }
        foreach (var function in Functions)
        {
            var last = reportedEnd.TryGetValue(function, out var end) ? end : function.EndLine;
            function.Coverage = file.LinesBetween(function.StartLine, last);
            function.BranchCoverage = file.BranchesBetween(function.StartLine, last);
        }
    }
}

/// <summary>
/// Counts the lines of code in assembler source (<c>.s</c>, <c>.S</c>, <c>.asm</c>): every line except blank lines,
/// comments and <c>#if 0</c> blocks, as for C. CCM is not computed, since labels are not functions whose control
/// flow McCabe's metric could describe.
/// </summary>
/// <remarks>
/// One rule set covers all common dialects: <c>/* */</c> and <c>//</c> as in C; a line starting with <c>;</c>
/// (armasm, IAR, MASM) or, in <c>.s</c> and <c>.S</c> files, with <c>@</c> (GNU ARM); and <c>#</c> followed by
/// anything other than a preprocessor directive (GNU x86). A comment character after code only matters if it hides
/// a <c>/*</c>, so <c>;</c> and <c>@</c> end the scan of a line while <c>#</c> does not, as it marks immediates on
/// ARM. Limitation: assembler conditionals such as <c>.if 0</c> count as code.
/// </remarks>
static class AssemblyAnalyzer
{
    static readonly HashSet<string> Directives =
    [
        "include", "include_next", "import", "define", "undef", "if", "ifdef", "ifndef", "elif", "elifdef", "elifndef",
        "else", "endif", "error", "warning", "pragma", "line",
    ];

    /// <param name="lines">The source lines.</param>
    /// <param name="atStartsComment">Whether <c>@</c> at the start of a line begins a comment, as in GNU ARM <c>.s</c> and <c>.S</c> files.</param>
    public static SourceMetrics Analyze(string[] lines, bool atStartsComment)
    {
        var result = new SourceMetrics { IsAssembly = true };
        var inComment = false;
        var inactive = 0;  // depth inside #if 0
        foreach (var line in lines)
        {
            if (inactive > 0)
            {
                // Nothing inside #if 0 counts; only the directives that end the block matter.
                switch (DirectiveOf(line, 0, out _))
                {
                    case "if" or "ifdef" or "ifndef":
                        inactive++;
                        break;
                    case "else" or "elif" or "elifdef" or "elifndef" when inactive == 1:
                        inactive = 0;
                        result.Loc++;
                        break;
                    case "endif":
                        if (--inactive == 0)
                            result.Loc++;
                        break;
                }
                continue;
            }

            var code = false;
            var i = 0;
            while (i < line.Length)
            {
                if (inComment)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                        break;
                    inComment = false;
                    i = end + 2;
                    continue;
                }
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '/' && next == '*')
                {
                    inComment = true;
                    i += 2;
                }
                else if ((c == '/' && next == '/') || c == ';' || (c == '@' && atStartsComment))
                {
                    break;
                }
                else if (c == '#' && !code)
                {
                    var directive = DirectiveOf(line, i, out var after);
                    if (!Directives.Contains(directive))
                        break;  // a comment
                    code = true;
                    if (directive == "if" && CodeAnalyzer.IsAlwaysFalse(line, after))
                    {
                        // The lines inside #if 0 are not scanned, so a comment opened on this line must not be
                        // tracked either: it would otherwise outlast the #endif.
                        inactive = 1;
                        break;
                    }
                    i = after;
                }
                else if (c == '"')
                {
                    code = true;
                    i = AfterString(line, i + 1);
                }
                else
                {
                    code = true;
                    i++;
                }
            }
            if (code)
                result.Loc++;
        }
        result.Incomplete = inComment || inactive > 0;
        return result;
    }

    /// <summary>
    /// The directive name after a <c>#</c> that is the first non-blank character from <paramref name="i"/> on, or ""
    /// if there is none. <paramref name="after"/> receives the position behind the name.
    /// </summary>
    static string DirectiveOf(string line, int i, out int after)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;
        after = i;
        if (i == line.Length || line[i] != '#')
            return "";
        i++;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
            i++;
        var start = i;
        while (i < line.Length && (char.IsAsciiLetter(line[i]) || line[i] == '_'))
            i++;
        after = i;
        return line[start..i];
    }

    // Skips a string literal whose opening quote precedes i; an unterminated string ends with the line.
    static int AfterString(string line, int i)
    {
        while (i < line.Length)
        {
            if (line[i] == '\\')
                i += 2;
            else if (line[i] == '"')
                return i + 1;
            else
                i++;
        }
        return line.Length;
    }
}

/// <summary>
/// Computes lines of code and McCabe's cyclomatic complexity (CCM) from C/C++ source text. CCM is 1 plus one for each
/// <c>if</c>, <c>for</c>, <c>while</c>, <c>case</c>, <c>catch</c>, <c>?:</c>, <c>&amp;&amp;</c> and <c>||</c> in a
/// function. Coverage files carry neither metric, and running a compiler is not an option.
/// </summary>
/// <remarks>
/// <para>
/// The source is read as written, without a preprocessor, so the result is a heuristic. Known limitations: both sides
/// of an <c>#if</c> count (only <c>#if 0</c> is skipped), code hidden in macros does not count, K&amp;R function heads
/// are not recognized, functions returning function pointers get a wrong name, and <c>&amp;&amp;</c> in a local rvalue
/// reference counts as a decision. Exact numbers would require a real parser with the compiler's include paths.
/// </para>
/// <para>
/// The source is untrusted input. The analysis is a single pass over the text without recursion or regular
/// expressions, so its cost stays linear in the size of the input.
/// </para>
/// </remarks>
static class CodeAnalyzer
{
    const int MaxNameLength = 256;
    const int MaxConditionalDepth = 256;
    const int MaxRawStringPrefix = 18;
    public const int MaxFunctionsPerFile = 100_000;

    /// <summary>Analyzes the source; with <paramref name="decisions"/>, also records the decisions of its functions there.</summary>
    public static SourceMetrics Analyze(string[] lines, DecisionMap? decisions = null) => new Scanner(lines, decisions).Run();

    /// <summary>Whether the <c>#if</c> condition starting at <paramref name="i"/> is a literal <c>0</c> or <c>false</c>.</summary>
    /// <remarks>Comments may appear anywhere in the condition, so <c>#if 0 /* off */ || X</c> is not taken as false.</remarks>
    internal static bool IsAlwaysFalse(string line, int i)
    {
        var condition = new System.Text.StringBuilder();
        while (i < line.Length && condition.Length < 16)
        {
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    break;
                i = end + 2;
                continue;
            }
            if (!char.IsWhiteSpace(line[i]))
                condition.Append(line[i]);
            i++;
        }
        return condition.ToString() is "0" or "false";
    }

    enum Mode { Scope, Skip, Function }

    enum Text { Code, BlockComment, LineComment, String, Character, RawString }

    // The statement being read at scope level: everything since the last ;, { or }. The state is kept in one object
    // because each side of an #else restarts from the state at the #if, even in the middle of a function head.
    sealed class Head
    {
        public int Line, Parens, Angles, Brackets, InitializerBraces, NameLine, FunctionLine, Words, WordsAfterParen;
        public bool SawParen, SawEquals, SawClassKey, SawEnum, SawNamespace, SawExtern, SawArrow, SawTry, SawComma;
        public bool InInitializers, ReadingScopeName, InOperator, OperatorParen, NameHadType, NameAfterEnum;
        public string Last = "", BeforeLast = "", Candidate = "", Name = "", ScopeName = "", ScopeNameBefore = "";

        public Head Copy() => (Head)MemberwiseClone();
    }

    // The enclosing namespaces and classes, each with the qualifier for names declared inside it. An immutable chain,
    // so saving the position at an #if is free no matter how deeply a hostile file nests.
    sealed class Scope(Scope? parent, string prefix)
    {
        public Scope? Parent { get; } = parent;
        public string Prefix { get; } = prefix;
        public int Depth { get; } = parent == null ? 0 : parent.Depth + 1;
    }

    sealed class Conditional(Mode mode, int depth, Scope scope, Head head, object? decisions)
    {
        public Mode Mode { get; } = mode;
        public int Depth { get; } = depth;
        public Scope Scope { get; } = scope;
        public Head Head { get; } = head;
        public object? Decisions { get; } = decisions;

        // The deepest scope an earlier side ended in, as in: #ifdef __cplusplus / extern "C" { / #else / ... / #endif
        public Scope? Deepest { get; set; }
    }

    /// <summary>The single-pass state machine behind <see cref="Analyze"/>.</summary>
    sealed class Scanner(string[] lines, DecisionMap? decisions)
    {
        readonly bool[] _hasCode = new bool[lines.Length];
        readonly SourceMetrics _result = new();
        readonly Stack<Conditional> _conditionals = new();
        readonly DecisionMap? _decisions = decisions;
        readonly DecisionTracker? _tracker = decisions != null ? new DecisionTracker(decisions) : null;

        Scope _scope = new(null, "");

        Text _text = Text.Code;
        string _rawEnd = "";
        bool _continuedDirective;
        int _inactiveDepth;     // nesting depth inside #if 0

        Mode _mode = Mode.Scope;
        int _depth;             // open braces of the block being skipped, or of the function body
        Head _h = new();

        // The function being read. Kept apart from the head, because an #else inside the body must not reset it.
        int _complexity, _functionLine, _functionStart, _currentBody, _lastEndedBody;
        string _functionName = "";
        bool _functionHasTry, _tryBodyJustEnded, _functionIsMacroLike, _macroBodyJustEnded;

        public SourceMetrics Run()
        {
            for (var i = 0; i < lines.Length; i++)
                ScanLine(lines[i], i);

            // A function still open at the end is kept; dropping it would make the file's numbers look plausible.
            if (_mode == Mode.Function)
            {
                EndFunction(lines.Length);
                _result.Incomplete = true;
            }
            if (_mode == Mode.Skip || _scope.Depth > 0 || _inactiveDepth > 0 || _text is Text.BlockComment or Text.RawString)
                _result.Incomplete = true;

            MergeSidesOfOneFunction();

            var codeBefore = new int[lines.Length + 1];
            for (var i = 0; i < lines.Length; i++)
                codeBefore[i + 1] = codeBefore[i] + (_hasCode[i] ? 1 : 0);

            _result.Loc = codeBefore[lines.Length];
            foreach (var function in _result.Functions)
                function.Loc = codeBefore[function.EndLine] - codeBefore[function.StartLine - 1];
            return _result;
        }

        // "void f(int a / #ifdef X / , int b) { } / #else / ) { } / #endif" yields two bodies of one function. They are
        // merged into one entry, and the list is sorted, as FunctionAt requires.
        void MergeSidesOfOneFunction()
        {
            var sorted = _result.Functions.OrderBy(f => f.StartLine).ToList();
            _result.Functions.Clear();
            foreach (var function in sorted)
            {
                var previous = _result.Functions.Count > 0 ? _result.Functions[^1] : null;
                if (previous != null && previous.Name == function.Name && previous.Line == function.Line && function.StartLine <= previous.EndLine)
                {
                    previous.EndLine = Math.Max(previous.EndLine, function.EndLine);
                    previous.Complexity = Math.Max(previous.Complexity, function.Complexity);
                }
                else
                {
                    _result.Functions.Add(function);
                }
            }
        }

        void ScanLine(string line, int index)
        {
            if (_continuedDirective)
            {
                _hasCode[index] = _inactiveDepth == 0 && line.Length > 0;
                _continuedDirective = line.EndsWith('\\');
                return;
            }
            if (_inactiveDepth > 0)
            {
                ScanInactiveLine(line, index);
                return;
            }

            // Only a trailing backslash continues these onto the next line, and an empty line cannot end in one.
            if (line.Length == 0 && _text is Text.LineComment or Text.String or Text.Character)
                _text = Text.Code;

            var i = 0;
            while (i < line.Length)
            {
                if (_text != Text.Code)
                {
                    i = ScanText(line, i, index);
                    continue;
                }

                var c = line[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    if (line.EndsWith('\\'))
                        _text = Text.LineComment;
                    return;
                }
                if (c == '/' && next == '*')
                {
                    _text = Text.BlockComment;
                    i += 2;
                    continue;
                }

                // A directive must be the first token on its line; %: is the digraph for #.
                var isDirective = c == '#' || (c == '%' && next == ':');
                if (isDirective && !_hasCode[index])
                {
                    _hasCode[index] = true;
                    Directive(line, c == '#' ? i + 1 : i + 2);
                    return;
                }
                _hasCode[index] = true;

                if (c == '"' || c == '\'')
                {
                    Track("\"", index + 1);
                    _text = c == '"' ? Text.String : Text.Character;
                    i++;
                }
                else if (IsWordStart(c))
                {
                    var start = i;
                    while (i < line.Length && IsWordPart(line[i]))
                        i++;
                    var word = line[start..i];
                    if (i < line.Length && line[i] == '"' && word is "R" or "LR" or "uR" or "UR" or "u8R")
                    {
                        Track("\"", index + 1);
                        i = StartRawString(line, i);
                    }
                    else
                    {
                        Word(word, index + 1);
                    }
                }
                else if (char.IsAsciiDigit(c))
                {
                    Track("0", index + 1);

                    // Consumes 1'000 and 0x1F as a whole, so a digit separator is not mistaken for a character literal.
                    while (i < line.Length && (IsWordPart(line[i]) || line[i] is '.' or '\''))
                        i++;
                    if (_mode == Mode.Scope && _h.Line != 0)
                        SetLast("0");
                }
                else if (Digraph(c, next, i + 2 < line.Length ? line[i + 2] : '\0') is { } digraph)
                {
                    Symbol(digraph, index + 1);
                    i += 2;
                }
                else if ((c, next) is (':', ':') or ('-', '>') or ('&', '&') or ('|', '|'))
                {
                    Symbol(line.Substring(i, 2), index + 1);
                    i += 2;
                }
                else
                {
                    Symbol(c.ToString(), index + 1);
                    i++;
                }
            }
        }

        static string? Digraph(char c, char next, char afterNext) => (c, next) switch
        {
            ('<', '%') => "{",
            ('%', '>') => "}",
            ('<', ':') when afterNext != ':' => "[",  // <:: is < followed by ::
            (':', '>') => "]",
            _ => null,
        };

        static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_' || c > 127;

        static bool IsWordPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c > 127;

        int ScanText(string line, int i, int index)
        {
            switch (_text)
            {
                case Text.BlockComment:
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                        return line.Length;
                    _text = Text.Code;
                    return end + 2;
                }
                case Text.LineComment:
                    if (!line.EndsWith('\\'))
                        _text = Text.Code;
                    return line.Length;
                case Text.RawString:
                {
                    _hasCode[index] = true;
                    var end = line.IndexOf(_rawEnd, i, StringComparison.Ordinal);
                    if (end < 0)
                        return line.Length;
                    _text = Text.Code;
                    return end + _rawEnd.Length;
                }
            }

            _hasCode[index] = true;
            var after = SkipLiteral(line, i, _text == Text.String ? '"' : '\'');
            if (after >= 0)
            {
                _text = Text.Code;
                return after;
            }

            // An unterminated literal continues on the next line only after a trailing backslash.
            if (!line.EndsWith('\\'))
                _text = Text.Code;
            return line.Length;
        }

        // Returns the position after the closing quote, or -1 if the line ends first.
        static int SkipLiteral(string line, int i, char quote)
        {
            while (i < line.Length)
            {
                var c = line[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                i++;
                if (c == quote)
                    return i;
            }
            return -1;
        }

        int StartRawString(string line, int quote)
        {
            // The search is bounded: the standard allows at most 16 delimiter characters, and an unbounded search
            // would rescan the line for every R" on it.
            var open = line.IndexOf('(', quote, Math.Min(MaxRawStringPrefix, line.Length - quote));
            if (open < 0)
                return quote + 1;  // not a valid raw string, so the rest of the line is read as code
            _rawEnd = ")" + line[(quote + 1)..open] + "\"";
            _text = Text.RawString;
            return open + 1;
        }

        void Directive(string line, int i)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            var start = i;
            while (i < line.Length && char.IsAsciiLetter(line[i]))
                i++;

            // Both sides of an #if are read, each starting from the state saved at the #if, so that
            // "#if A / void f(int) { / #else / void f(long) { / #endif" still opens only one body.
            switch (line[start..i])
            {
                case "if" or "ifdef" or "ifndef":
                    if (_conditionals.Count < MaxConditionalDepth)
                        _conditionals.Push(new Conditional(_mode, _depth, _scope, _h.Copy(), _tracker?.Save()));
                    if (line[start..i] == "if" && IsAlwaysFalse(line, i))
                        _inactiveDepth = 1;
                    break;
                case "else" or "elif" or "elifdef" or "elifndef":
                    if (_conditionals.TryPeek(out var saved))
                        Restore(saved);
                    break;
                case "endif":
                    EndConditional();
                    break;
            }
            ScanDirectiveTail(line, i);
        }

        void EndConditional()
        {
            if (_conditionals.TryPop(out var ended) && ended.Deepest is { } deepest && deepest.Depth > _scope.Depth && _mode == Mode.Scope)
                _scope = deepest;
        }

        // Scans the rest of a directive line. Only a genuine block comment may stay open past the line end, so a /*
        // inside a string ("src/*.c") or behind // must not swallow the rest of the file.
        void ScanDirectiveTail(string line, int i)
        {
            while (i < line.Length)
            {
                var c = line[i];
                if (c is '"' or '\'')
                {
                    var after = SkipLiteral(line, i + 1, c);
                    if (after < 0)
                        break;
                    i = after;
                }
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        _text = Text.BlockComment;
                        return;
                    }
                    i = end + 2;
                }
                else
                {
                    i++;
                }
            }
            _continuedDirective = line.EndsWith('\\');
        }

        // Nothing inside #if 0 counts. Only the directives that end the block matter, and comments are tracked so that
        // a directive inside one is not taken for real.
        void ScanInactiveLine(string line, int index)
        {
            var i = 0;
            if (_text == Text.BlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                    return;
                i = end + 2;
                _text = Text.Code;
            }

            // Whitespace and complete comments may precede the #.
            while (i < line.Length)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        _text = Text.BlockComment;
                        return;
                    }
                    i = end + 2;
                }
                else
                {
                    break;
                }
            }

            var isDirective = i < line.Length && (line[i] == '#' || (line[i] == '%' && i + 1 < line.Length && line[i + 1] == ':'));
            if (!isDirective)
            {
                ScanDirectiveTail(line, i);
                return;
            }

            i += line[i] == '#' ? 1 : 2;
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            var start = i;
            while (i < line.Length && char.IsAsciiLetter(line[i]))
                i++;

            switch (line[start..i])
            {
                case "if" or "ifdef" or "ifndef":
                    _inactiveDepth++;
                    break;
                case "else" or "elif" or "elifdef" or "elifndef" when _inactiveDepth == 1:
                    _inactiveDepth = 0;
                    _hasCode[index] = true;
                    break;
                case "endif":
                    if (--_inactiveDepth == 0)
                    {
                        EndConditional();
                        _hasCode[index] = true;
                    }
                    break;
            }
            ScanDirectiveTail(line, i);
        }

        void Restore(Conditional saved)
        {
            // A head that began on the side being left still began there, and coverage tools may report that side.
            var earliest = _mode == Mode.Function && saved.Mode != Mode.Function ? _functionStart : _h.Line;
            _h = saved.Head.Copy();
            if (_h.Line == 0)
                _h.Line = earliest;
            _mode = saved.Mode;
            _depth = saved.Depth;
            _tracker?.Restore(saved.Decisions);

            if (_scope.Depth > saved.Scope.Depth && _scope.Depth > (saved.Deepest?.Depth ?? 0))
                saved.Deepest = _scope;
            _scope = saved.Scope;
        }

        void SetLast(string token)
        {
            _h.BeforeLast = _h.Last;
            _h.Last = token;
        }

        // Counts every token for the decision map and hands those of function bodies to the decision tracker.
        void Track(string token, int line)
        {
            if (_tracker == null)
                return;
            _decisions!.Touch(line);
            if (_mode == Mode.Function)
                _tracker.Feed(token, line);
        }

        void Word(string word, int line)
        {
            Track(word, line);
            if (_mode == Mode.Function)
            {
                if (word is "if" or "for" or "while" or "case" or "catch" or "and" or "or")
                    _complexity++;
                return;
            }
            if (_mode == Mode.Skip)
                return;

            // Function-try block, "void f() try { } catch (int) { }": the handlers belong to the function just ended.
            if (_tryBodyJustEnded && word == "catch" && _h.Line == 0)
            {
                _result.Functions.RemoveAt(_result.Functions.Count - 1);
                _lastEndedBody = 0;
                _mode = Mode.Function;
                _depth = 0;
                _complexity++;
                _tracker?.Begin(line);
                _tracker?.Feed(word, line);
                return;
            }
            _tryBodyJustEnded = false;
            _macroBodyJustEnded = false;

            var h = _h;
            if (h.Line == 0)
                h.Line = line;
            var last = h.Last;
            SetLast(word);

            // Words inside template arguments, attributes and parameter lists say nothing about the head itself.
            if (h.Parens > 0 || h.Angles > 0 || h.Brackets > 0)
                return;

            if (h.InOperator)
            {
                Append(last == "::" ? word : " " + word);  // operator bool, operator std::string
                return;
            }

            switch (word)
            {
                case "class" or "struct" or "union":
                    h.SawClassKey = true;
                    h.ReadingScopeName = true;
                    return;
                case "namespace":
                    h.SawNamespace = true;
                    h.ReadingScopeName = true;
                    return;
                case "enum":
                    h.SawEnum = true;
                    return;
                case "extern":
                    h.SawExtern = true;
                    return;
                case "try":
                    h.SawTry = true;
                    return;
                case "operator":
                    if (last == "::")
                        Append(word);
                    else
                        h.Candidate = word;
                    h.NameLine = last == "::" ? h.NameLine : line;
                    h.InOperator = true;
                    return;
                case "template" or "static" or "inline" or "constexpr" or "consteval" or "explicit" or "virtual" or "friend":
                    return;  // specifiers, not a return type
            }

            var qualified = last is "::" or "~";
            if (!qualified)
                h.Words++;
            if (h.SawParen && word is not ("const" or "volatile" or "noexcept" or "override" or "final"))
                h.WordsAfterParen++;

            // Names are capped, so that hostile input cannot make every step copy an ever longer string.
            if (h.ReadingScopeName && word != "final")
            {
                if (!qualified)
                    h.ScopeNameBefore = h.ScopeName;
                h.ScopeName = !qualified ? Capped(word) : h.ScopeName.Length < MaxNameLength ? h.ScopeName + "::" + Capped(word) : h.ScopeName;
            }

            if (qualified)
            {
                Append(word);
            }
            else
            {
                h.Candidate = Capped(word);
                h.NameLine = line;  // coverage tools report the line where a qualified name starts, not its last part
            }
        }

        static string Capped(string text) => text.Length > MaxNameLength ? text[..MaxNameLength] : text;

        void Append(string text)
        {
            if (_h.Candidate.Length < MaxNameLength)
                _h.Candidate = Capped(_h.Candidate + text);
        }

        void Symbol(string symbol, int line)
        {
            Track(symbol, line);
            switch (_mode)
            {
                case Mode.Function:
                    if (symbol is "?" or "&&" or "||")
                    {
                        _complexity++;
                    }
                    else if (symbol == "{")
                    {
                        _depth++;
                    }
                    else if (symbol == "}" && --_depth == 0)
                    {
                        EndFunction(line);
                    }
                    return;
                case Mode.Skip:
                    if (symbol == "{")
                    {
                        _depth++;
                    }
                    else if (symbol == "}" && --_depth == 0)
                    {
                        _mode = Mode.Scope;
                        ResetHead();
                    }
                    return;
            }

            _tryBodyJustEnded = false;

            // "DECLARE_INTERFACE_(I, B) { ... };" and "MACRO(x) Foo{1};" turn out to be declarations, not functions.
            if (_macroBodyJustEnded && symbol == ";" && _h.Line == 0)
            {
                _result.Functions.RemoveAt(_result.Functions.Count - 1);
                _lastEndedBody = 0;
            }
            _macroBodyJustEnded = false;

            var h = _h;
            if (h.Line == 0)
                h.Line = line;

            // Braced member initializers, as in "Foo() : a{1}, b{2} {", precede the actual body.
            if (h.InitializerBraces > 0)
            {
                if (symbol == "{")
                {
                    h.InitializerBraces++;
                }
                else if (symbol == "}" && --h.InitializerBraces == 0)
                {
                    SetLast("}");
                }
                return;
            }

            if (h.Parens > 0)
            {
                if (symbol == "(")
                {
                    h.Parens++;
                }
                else if (symbol == ")" && --h.Parens == 0)
                {
                    h.WordsAfterParen = 0;
                    SetLast(")");
                }
                return;  // parameter lists may contain anything, including braces in default arguments
            }

            // These always end the head, so an unclosed < or [ was not a bracket after all.
            if (symbol is ";" or "{" or "}")
                h.Angles = h.Brackets = 0;

            if (h.Brackets > 0)
            {
                if (symbol == "[")
                    h.Brackets++;
                else if (symbol == "]")
                    h.Brackets--;
                SetLast(symbol);
                return;
            }

            if (h.Angles > 0)
            {
                if (symbol == "<")
                    h.Angles++;
                else if (symbol == ">")
                    h.Angles--;
                SetLast(symbol);
                return;
            }

            if (h.InOperator && symbol is not ("(" or ";" or "{" or "}"))
            {
                if (h.OperatorParen && symbol == ")")
                {
                    Append("()");
                    h.OperatorParen = false;
                }
                else if (symbol.All(c => "+-*/%^&|~!=<>[],:".Contains(c)))
                {
                    Append(symbol);
                }
                SetLast(symbol);
                return;
            }

            switch (symbol)
            {
                case ";":
                    ResetHead();
                    return;
                case "}":
                    if (_scope.Parent is { } parent)
                        _scope = parent;
                    else
                        _result.Incomplete = true;  // a brace that closes nothing: the braces do not balance
                    ResetHead();
                    return;
                case "{":
                    OpenBrace(line);
                    return;
                case "(":
                    OpenParen(line);
                    return;
                case "[":
                    h.Brackets = 1;
                    break;
                case "<":
                    h.Angles = 1;
                    break;
                case "=":
                    h.SawEquals = true;
                    break;
                case "->":
                    h.SawArrow = h.SawParen;
                    break;
                case ":":
                    // An access specifier ends whatever preceded it, such as a macro call: "Q_DISABLE_COPY(A) public:".
                    if (h.Last is "public" or "private" or "protected")
                    {
                        ResetHead();
                        return;
                    }

                    // After a parameter list, a colon can only start member initializers, even behind a macro:
                    // "Foo() SOME_NOEXCEPT : a(1) {}".
                    if (h.SawParen && !h.SawClassKey)
                        h.InInitializers = true;
                    h.ReadingScopeName = false;
                    break;
                case ",":
                    if (h.SawParen && !h.InInitializers)
                        h.SawComma = true;  // a declaration list such as "Foo a(1), b{2};"
                    break;
                case "::":
                    Append("::");
                    break;
                case "~":
                    if (h.Last == "::")
                        Append("~");
                    else
                        h.Candidate = "~";
                    break;
            }
            SetLast(symbol);
        }

        void OpenParen(int line)
        {
            var h = _h;

            // In "namespace std SOME_MACRO(default) {", the word right before the parentheses is not the scope name.
            if (h.ReadingScopeName)
                h.ScopeName = h.ScopeNameBefore;

            if (h.InOperator && h.Candidate.EndsWith("operator", StringComparison.Ordinal))
            {
                h.OperatorParen = true;  // in operator(), the first pair of parentheses is part of the name
                SetLast("(");
                return;
            }

            if (h.Candidate.Length > 0 && !h.InInitializers && !IsSpecifier(h.Last) && MayRename())
            {
                h.Name = h.Candidate;
                h.FunctionLine = h.NameLine != 0 ? h.NameLine : line;
                h.NameHadType = h.Words > 1;
                h.NameAfterEnum = h.SawEnum;
            }
            h.InOperator = false;
            h.SawParen = true;
            h.Parens = 1;
            SetLast("(");
        }

        // Decides whether a later "name(" replaces the name found so far. A name that followed a return type is kept
        // ("void f() __nonnull(1) {") unless a complete new type and name follow ("static MACRO(x) inline int f(int a) {").
        // Without a return type, "EXPORT(int) f(int a) {" is named f, while "C::C() MACRO(x) {" stays C::C, because an
        // all-caps word after a regular name is taken to be a macro.
        bool MayRename()
        {
            var h = _h;
            if (h.Name.Length == 0)
                return true;
            if (h.NameHadType)
                return h.WordsAfterParen >= 2;
            return !IsAllCaps(h.Candidate) || IsAllCaps(h.Name);
        }

        static bool IsAllCaps(string word) => word.Length > 0 && word.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

        static bool IsSpecifier(string word) =>
            word is "noexcept" or "decltype" or "throw" or "requires" or "__attribute__" or "__declspec" or "alignas" or "sizeof";

        void OpenBrace(int line)
        {
            var h = _h;
            if (h.InInitializers && h.Last is not (")" or "}"))
            {
                h.InitializerBraces = 1;  // a braced initializer such as a{1}; the body is still to come
                return;
            }

            // "enum color f(int a) {" is a function: only then does a name with parentheses follow the enum.
            if (h.SawEquals || h.SawComma || (h.SawEnum && !h.NameAfterEnum))
            {
                Skip();
                return;
            }

            if (h.SawParen && !h.SawNamespace && h.Name.Length > 0 && EndsLikeFunction())
            {
                _mode = Mode.Function;
                _depth = 1;
                _complexity = 0;
                _functionName = h.Name;
                _functionLine = h.FunctionLine != 0 ? h.FunctionLine : line;
                _functionStart = h.Line;
                _functionHasTry = h.SawTry;
                _functionIsMacroLike = IsAllCaps(h.Name) && !h.NameHadType;
                _currentBody++;
                _tracker?.Begin(line);
                return;
            }

            // A lone all-caps word before a brace, such as "NS_BEGIN {", is most likely a namespace macro.
            if (h.SawClassKey || h.SawNamespace || h.SawExtern || (!h.SawParen && h.Words == 1 && IsAllCaps(h.Last)))
            {
                var parent = _scope.Prefix;
                var name = h.ScopeName;
                _scope = new Scope(_scope, name.Length > 0 && parent.Length + name.Length < MaxNameLength ? parent + name + "::" : parent);
                ResetHead();
                return;
            }

            Skip();  // any other block, such as an initializer list, contributes nothing
        }

        void Skip()
        {
            _mode = Mode.Skip;
            _depth = 1;
        }

        // Whether the head before a { can end a function head, judged by its last tokens.
        bool EndsLikeFunction()
        {
            var h = _h;
            if (h.InInitializers || h.SawArrow || h.Last is ")" or "try" or "const" or "noexcept" or "override" or "volatile" or "&" or "&&")
                return true;
            if (h.SawClassKey)
                return false;  // "class API(x) C final {"
            if (h.Last is "final" or ">")
                return true;   // "requires Foo<T> {"
            if (h.Last == "]")
                return h.BeforeLast == "]";  // an attribute: "void f() [[gnu::cold]] {"

            // A trailing macro, as in "void f() const SOME_MACRO {", but not a braced declaration: "MACRO(x) Foo foo{1};".
            return h.Last.Length > 0 && IsWordStart(h.Last[0]) && (h.WordsAfterParen == 1 || IsAllCaps(h.Last));
        }

        void EndFunction(int line)
        {
            _mode = Mode.Scope;
            _depth = 0;
            _tryBodyJustEnded = _functionHasTry;
            _macroBodyJustEnded = _functionIsMacroLike;
            ResetHead();

            var name = PathUtil.StripControl(_scope.Prefix + _functionName);
            var functions = _result.Functions;

            // Both sides of an #if closed the same body: update the entry instead of adding a second one.
            if (functions.Count > 0 && _lastEndedBody == _currentBody)
            {
                functions[^1].EndLine = line;
                functions[^1].Complexity = 1 + _complexity;
                return;
            }

            if (functions.Count >= MaxFunctionsPerFile)
            {
                _result.Incomplete = true;
                _tryBodyJustEnded = _macroBodyJustEnded = false;  // nothing was added, so there is nothing to take back
                return;
            }

            functions.Add(new FunctionMetrics
            {
                Name = name.Length == 0 ? "(unnamed)" : Capped(name),
                Line = _functionLine,
                StartLine = _functionStart,
                EndLine = line,
                Complexity = 1 + _complexity,
            });
            _lastEndedBody = _currentBody;
        }

        void ResetHead() => _h = new Head();
    }
}
