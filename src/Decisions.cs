// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

namespace CoverageReport;

/// <summary>The kinds of decision the source analysis records for the coverage estimate.</summary>
enum DecisionKind { If, Loop, DoWhile, Switch, Ternary }

/// <summary>A contiguous run of tokens, such as the body of an if, with the lines on which it starts and ends.</summary>
readonly record struct Region(int StartToken, int StartLine, int EndToken, int EndLine);

/// <summary>A section of a switch: its labels and the statements up to the next label.</summary>
/// <param name="FallsInto">
/// Whether the section before it can fall through into it, so that its lines also run when none of its own labels
/// was chosen.
/// </param>
readonly record struct CaseSection(Region Region, bool FallsInto);

/// <summary>A place in a function where control flow continues one of several ways, with the regions of its branches.</summary>
sealed class Decision
{
    public DecisionKind Kind { get; init; }

    /// <summary>The line of the keyword, or of the <c>?</c>. Coverage tools count the decision on this line.</summary>
    public int Line { get; init; }

    /// <summary>if: the then branch. for and while: the body.</summary>
    public Region? First { get; init; }

    /// <summary>if: the else branch, unless it is another if.</summary>
    public Region? Second { get; init; }

    /// <summary>if: the line of the if that forms the else branch, as in <c>else if</c>; 0 if there is none.</summary>
    public int ElseIfLine { get; init; }

    /// <summary>for and while: whether the head ends on the keyword's line, so that its count is the number of tests.</summary>
    public bool HeadOnOneLine { get; init; }

    /// <summary>switch: the sections in order; <c>null</c> if the body is not a block or has too many sections.</summary>
    public List<CaseSection>? Sections { get; init; }

    /// <summary>switch: set if the body has more than <see cref="DecisionTracker.MaxSections"/> sections.</summary>
    public bool TooManySections { get; init; }
}

/// <summary>The decisions found in one file, and the first and last token of every line.</summary>
/// <remarks>
/// The token positions tell which lines belong to a branch alone. A line that also holds tokens from outside the
/// branch, as in <c>if (x) return;</c>, cannot show whether the branch ran. The map is only kept until the estimate
/// is made, as it holds two numbers per line.
/// </remarks>
sealed class DecisionMap(int lines)
{
    public const int MaxDecisions = 100_000;

    readonly int[] _first = new int[lines + 2];
    readonly int[] _last = new int[lines + 2];

    public List<Decision> Decisions { get; } = [];

    /// <summary>Lines with <c>for</c>, <c>while</c> or <c>do</c>. Their count is the number of tests, not of entries.</summary>
    public HashSet<int> LoopLines { get; } = [];

    /// <summary>
    /// Set if decisions were left out: more than <see cref="MaxDecisions"/>, or statements nested deeper than
    /// <see cref="DecisionTracker.MaxNesting"/>.
    /// </summary>
    public bool Truncated { get; private set; }

    public void GiveUp() => Truncated = true;

    /// <summary>The number of the token seen last. Tokens are numbered from 1 in the order of the source.</summary>
    public int Token { get; private set; }

    /// <summary>Counts a token on <paramref name="line"/> (1-based).</summary>
    public void Touch(int line)
    {
        Token++;
        if ((uint)line >= (uint)_first.Length)
            return;
        if (_first[line] == 0)
            _first[line] = Token;
        _last[line] = Token;
    }

    /// <summary>Whether <paramref name="line"/> has tokens, and all of them lie within <paramref name="region"/>.</summary>
    public bool Owns(Region region, int line) =>
        (uint)line < (uint)_first.Length && _first[line] != 0 && _first[line] >= region.StartToken && _last[line] <= region.EndToken;

    public void Add(Decision decision)
    {
        if (Decisions.Count < MaxDecisions)
            Decisions.Add(decision);
        else
            Truncated = true;
    }
}

/// <summary>
/// Follows the statements of function bodies token by token and records their decisions in a <see cref="DecisionMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// A small statement grammar suffices: blocks, if/else, for, while, do-while, switch with its labels, try/catch, and
/// anything else as a statement up to its semicolon. A brace inside a statement opens a block, so the bodies of
/// lambdas are followed too. Input that does not fit is given up frame by frame until it fits again.
/// </para>
/// <para>
/// The state is an immutable chain of frames. The scanner saves it at an <c>#if</c> and restores it at the <c>#else</c>
/// for the price of a reference. Nothing recurses, so nesting cannot exhaust the stack. Each open statement holds a
/// frame, so the nesting depth and the sections of a switch are capped: a hostile file of nothing but opening braces
/// would otherwise fill the memory with frames.
/// </para>
/// </remarks>
sealed class DecisionTracker(DecisionMap map)
{
    /// <summary>The deepest nesting followed. Deeper code ends the tracking of its function.</summary>
    public const int MaxNesting = 256;

    /// <summary>The most sections a switch may have and still be evaluated.</summary>
    public const int MaxSections = 4096;

    enum Kind { Block, Statement, If, Loop, Do, Switch, Catch, Try, Label }

    // Head: before the opening parenthesis. Condition: inside the parentheses. Body: the branch is being read.
    // Done: the branch is complete, and an else, while or catch may follow. Else: the else branch is being read.
    // Semicolon: do-while, after its condition.
    enum Step { Head, Condition, Body, Done, Else, Semicolon }

    sealed record Frame(Kind Kind, Frame? Parent, int Start, int StartLine, int Line)
    {
        public int Depth { get; } = Parent == null ? 1 : Parent.Depth + 1;
        public Step Step { get; init; }
        public int Parens { get; init; }
        public bool NotADecision { get; init; }   // if constexpr, if consteval, or a constant condition
        public bool IsFor { get; init; }
        public int Semicolons { get; init; }      // for: semicolons in the head so far
        public int ConditionTokens { get; init; } // tokens of the condition; of a for loop, of its middle part
        public string ConditionToken { get; init; } = "";
        public bool HeadOnOneLine { get; init; }
        public Region? First { get; init; }
        public bool FirstJumps { get; init; }
        public int ElseIfLine { get; init; }
        public int WhileLine { get; init; }
        public string FirstWord { get; init; } = "";
        public bool LastJumps { get; init; }      // block: whether its last statement leaves it
        public bool IsSwitchBody { get; init; }
        public SectionList? Sections { get; init; }  // switch body: the closed sections, newest first
        public int SectionCount { get; init; }
        public bool TooManySections { get; init; }
        public int SectionStart { get; init; }       // switch body: the first token of the open section, 0 if none
        public int SectionStartLine { get; init; }
        public bool SectionHasStatements { get; init; }
        public bool SectionFallsInto { get; init; }
    }

    sealed record SectionList(CaseSection Value, SectionList? Next);

    Frame? _top;
    int _previous, _previousLine;

    /// <summary>The state to restore at an <c>#else</c>.</summary>
    public object? Save() => _top;

    public void Restore(object? state) => _top = state as Frame;

    /// <summary>Starts a function body, whose opening brace was the token seen last.</summary>
    public void Begin(int line)
    {
        _top = new Frame(Kind.Block, null, map.Token, line, line);
        _previous = map.Token;
        _previousLine = line;
    }

    /// <summary>Reads the token seen last, as counted by <see cref="DecisionMap.Touch"/>.</summary>
    /// <param name="token">The word or symbol; "0" for a number and "\"" for a string or character literal.</param>
    public void Feed(string token, int line)
    {
        var at = map.Token;
        if (token == "?")
            map.Add(new Decision { Kind = DecisionKind.Ternary, Line = line });

        // A frame that cannot take the token is finished or given up, and the token goes to the frame below.
        while (_top is { } top && !Take(top, token, at, line))
        {
        }

        // A token opens at most one frame, so the depth grows by one at a time. Nesting this deep is not real code;
        // the rest of the function is not followed, and the analysis is marked as incomplete.
        if (_top is { Depth: > MaxNesting })
        {
            _top = null;
            map.GiveUp();
        }
        _previous = at;
        _previousLine = line;
    }

    bool Take(Frame f, string t, int at, int line)
    {
        switch (f.Kind)
        {
            case Kind.Block:
                if (t == "}")
                {
                    var closed = CloseSection(f);
                    Complete(closed, at, line, closed.LastJumps);
                    return true;
                }
                if (f.IsSwitchBody && t is "case" or "default")
                {
                    _top = new Frame(Kind.Label, OpenSection(f, at, line), at, line, line);
                    return true;
                }
                return StartChild(f, t, at, line);

            case Kind.Statement:
                switch (t)
                {
                    case ";":
                        Complete(f, at, line, IsJump(f.FirstWord));
                        return true;
                    case "{":
                        _top = new Frame(Kind.Block, f, at, line, line);  // a lambda body or a braced initializer
                        return true;
                    case "}":
                        Complete(f, _previous, _previousLine, false);  // the block ends without a semicolon
                        return false;
                }
                return true;

            case Kind.Label:
                if (t is ";" or "}")
                {
                    _top = f.Parent;
                    return false;
                }
                if (t == ":" && f.Parens == 0)
                    _top = f.Parent;
                else if (t is "(" or ")")
                    _top = f with { Parens = Math.Max(0, f.Parens + (t == "(" ? 1 : -1)) };
                return true;
        }

        switch (f.Step)
        {
            case Step.Head:
                return Head(f, t, line);
            case Step.Condition:
                Condition(f, t, line);
                return true;
            case Step.Body:
                // A switch body that is a block holds the case labels.
                if (f.Kind == Kind.Switch && t == "{")
                {
                    _top = new Frame(Kind.Block, f, at, line, line) { IsSwitchBody = true };
                    return true;
                }
                return StartChild(f, t, at, line);
            case Step.Else:
                return StartChild(t == "if" ? f with { ElseIfLine = line } : f, t, at, line);
            case Step.Semicolon:
                map.Add(new Decision { Kind = DecisionKind.DoWhile, Line = f.WhileLine });
                if (t == ";")
                {
                    Complete(f, at, line, false);
                    return true;
                }
                Complete(f, _previous, _previousLine, false);
                return false;
        }

        // Step.Done: the branch is complete; see whether the statement continues.
        switch (f.Kind)
        {
            case Kind.If when t == "else":
                _top = f with { Step = Step.Else };
                return true;
            case Kind.If:
                if (!f.NotADecision)
                    map.Add(new Decision { Kind = DecisionKind.If, Line = f.Line, First = f.First });
                Complete(f, f.First!.Value.EndToken, f.First.Value.EndLine, false);
                return false;
            case Kind.Do when t == "while":
                _top = f with { Step = Step.Head, WhileLine = line };
                return true;
            case Kind.Try when t == "catch":
                _top = new Frame(Kind.Catch, f, at, line, line);
                return true;
            case Kind.Try:
                Complete(f, _previous, _previousLine, false);
                return false;
        }
        _top = f.Parent;  // a do without its while: given up
        return false;
    }

    bool Head(Frame f, string t, int line)
    {
        if (f.Kind == Kind.If && t is "constexpr" or "!")
        {
            _top = f with { NotADecision = true };
            return true;
        }
        if (f.Kind == Kind.If && t == "consteval")
        {
            _top = f with { NotADecision = true, Step = Step.Body };
            return true;
        }
        if (t == "(")
        {
            _top = f with { Step = Step.Condition, Parens = 1 };
            return true;
        }
        _top = f.Parent;  // not the statement it seemed to be, such as a macro named like a keyword
        return false;
    }

    // Follows the parentheses of a condition. A constant condition, as in "while (true)" or "for (;;)", compiles to no
    // branch at all, so it is not recorded. Numbers arrive as "0", whatever their value.
    void Condition(Frame f, string t, int line)
    {
        var parens = f.Parens + (t == "(" ? 1 : t == ")" ? -1 : 0);
        if (parens > 0)
        {
            if (f.IsFor && parens == 1 && t == ";")
                _top = f with { Parens = parens, Semicolons = f.Semicolons + 1 };
            else if (!f.IsFor || f.Semicolons == 1)
                _top = f with { Parens = parens, ConditionTokens = f.ConditionTokens + 1, ConditionToken = t };
            else
                _top = f with { Parens = parens };
            return;
        }

        var constant = f.ConditionTokens == 0 ? !f.IsFor || f.Semicolons >= 2 : f.ConditionTokens == 1 && f.ConditionToken is "true" or "false" or "0";
        if (f.IsFor && f.Semicolons < 2)
            constant = false;  // a range-based for
        _top = f with
        {
            Parens = 0,
            Step = f.Kind == Kind.Do ? Step.Semicolon : Step.Body,
            HeadOnOneLine = line == f.Line,
            NotADecision = f.NotADecision || (constant && f.Kind is Kind.If or Kind.Loop),
        };
    }

    // Starts the statement that forms a branch, or the next statement of a block.
    bool StartChild(Frame parent, string t, int at, int line)
    {
        Frame Push(Kind kind) => new(kind, parent, at, line, line);
        switch (t)
        {
            case "{":
                _top = Push(Kind.Block);
                return true;
            case "if":
                _top = Push(Kind.If);
                return true;
            case "for" or "while":
                map.LoopLines.Add(line);
                _top = Push(Kind.Loop) with { IsFor = t == "for" };
                return true;
            case "do":
                map.LoopLines.Add(line);
                _top = Push(Kind.Do) with { Step = Step.Body };
                return true;
            case "switch":
                _top = Push(Kind.Switch);
                return true;
            case "try":
                _top = Push(Kind.Try) with { Step = Step.Body };
                return true;
            case "catch":
                _top = Push(Kind.Catch);
                return true;
            case ";":
                Complete(Push(Kind.Statement), at, line, false);
                return true;
            case "}":
                _top = parent.Parent;  // the branch is missing; the brace belongs further out
                return false;
            case "else":
                _top = parent;  // an else without an if
                return true;
            default:
                _top = Push(Kind.Statement) with { FirstWord = t };
                return true;
        }
    }

    // Finishes a statement and hands its region to the frame it belongs to. That frame may be finished by it as well,
    // as an if is by its else branch, so this continues outwards in a loop.
    void Complete(Frame f, int end, int endLine, bool jumps)
    {
        while (true)
        {
            var region = new Region(f.Start, f.StartLine, end, endLine);
            if (f.Parent is not { } parent)
            {
                _top = null;
                return;
            }
            if (!ChildDone(parent, f, region, jumps, out jumps))
                return;
            f = parent;
        }
    }

    // Returns whether the parent is finished too, and if so, whether it ends with a jump.
    bool ChildDone(Frame p, Frame child, Region region, bool jumps, out bool parentJumps)
    {
        parentJumps = false;
        switch (p.Kind)
        {
            case Kind.Block:
                _top = p with { LastJumps = jumps, SectionHasStatements = p.SectionStart != 0 };
                return false;
            case Kind.If when p.Step == Step.Body:
                _top = p with { Step = Step.Done, First = region, FirstJumps = jumps };
                return false;
            case Kind.If:
                if (!p.NotADecision)
                {
                    map.Add(new Decision
                    {
                        Kind = DecisionKind.If,
                        Line = p.Line,
                        First = p.First,
                        Second = p.ElseIfLine == 0 ? region : null,
                        ElseIfLine = p.ElseIfLine,
                    });
                }
                parentJumps = p.FirstJumps && jumps;
                return true;
            case Kind.Loop:
                if (!p.NotADecision)
                    map.Add(new Decision { Kind = DecisionKind.Loop, Line = p.Line, First = region, HeadOnOneLine = p.HeadOnOneLine });
                return true;
            case Kind.Do:
                _top = p with { Step = Step.Done, First = region };
                return false;
            case Kind.Switch:
                map.Add(new Decision
                {
                    Kind = DecisionKind.Switch,
                    Line = p.Line,
                    Sections = child.IsSwitchBody && !child.TooManySections ? InOrder(child.Sections) : null,
                    TooManySections = child.TooManySections,
                });
                return true;
            case Kind.Catch:
                return true;
            case Kind.Try:
                _top = p with { Step = Step.Done };
                return false;
        }
        _top = p;  // a statement around a lambda body or a braced initializer goes on
        return false;
    }

    // A case or default label in a switch body. Labels directly after one another form one section.
    Frame OpenSection(Frame f, int at, int line)
    {
        if (f.SectionStart != 0 && !f.SectionHasStatements)
            return f;
        var fallsInto = f.SectionStart != 0 && !f.LastJumps;
        return CloseSection(f) with
        {
            SectionStart = at,
            SectionStartLine = line,
            SectionHasStatements = false,
            SectionFallsInto = fallsInto,
            LastJumps = false,
        };
    }

    // Beyond MaxSections, the sections are dropped rather than kept, so a switch of millions of cases holds no memory.
    Frame CloseSection(Frame f) => f.SectionStart == 0 ? f
        : f.TooManySections || f.SectionCount >= MaxSections ? f with { Sections = null, TooManySections = true, SectionStart = 0 }
        : f with
        {
            Sections = new SectionList(new CaseSection(new Region(f.SectionStart, f.SectionStartLine, _previous, _previousLine), f.SectionFallsInto), f.Sections),
            SectionCount = f.SectionCount + 1,
            SectionStart = 0,
        };

    static List<CaseSection> InOrder(SectionList? sections)
    {
        var list = new List<CaseSection>();
        for (var s = sections; s != null; s = s.Next)
            list.Add(s.Value);
        list.Reverse();
        return list;
    }

    static bool IsJump(string word) => word is "break" or "return" or "continue" or "goto" or "throw" or "co_return";
}
