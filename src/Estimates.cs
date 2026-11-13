// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

namespace CoverageReport;

/// <summary>
/// Estimates branch and method coverage from line coverage and the source code, for inputs that carry neither.
/// </summary>
/// <remarks>
/// <para>
/// A branch counts as taken if the first measured line that belongs to it alone ran. The false way of an if without
/// else and the exit of a loop have no lines of their own; they come from the hit counts: the condition line ran
/// more often than the branch was entered. This matches what gcov counts for if, loops and switch, but not its
/// branches for the parts of &amp;&amp; and ||, for ?: and for exception paths, which line coverage cannot show.
/// </para>
/// <para>
/// A decision whose branches cannot be told apart this way is not counted, but reported with the reason. A decision
/// on a line without coverage data was not compiled and is left out entirely, like a function without measured lines.
/// </para>
/// </remarks>
static class Estimator
{
    public const string Note = "Estimated from line coverage and source code, not measured.";

    /// <summary>Turns the decisions of a file into estimated branches.</summary>
    public static void EstimateBranches(FileCoverage file, DecisionMap map)
    {
        var measured = file.Lines.Keys.ToArray();  // sorted, as Lines is
        foreach (var decision in map.Decisions)
        {
            if (!file.Lines.TryGetValue(decision.Line, out var reached))
                continue;
            var (outcomes, reason) = Evaluate(decision, reached, file.Lines, measured, map);
            if (outcomes != null)
                file.AddEstimatedBranches(decision.Line, outcomes);
            else
                file.AddUnevaluable(decision.Line, reason!);
        }
        file.HasBranchData = true;
        file.BranchesEstimated = true;
        if (map.Truncated && file.Metrics != null)
            file.Metrics.Incomplete = true;
    }

    /// <summary>Turns the functions of a file into estimated methods.</summary>
    /// <remarks>
    /// A function counts as called if one of its measured lines ran. A function without measured lines was not
    /// compiled, such as a template never instantiated, and is left out, as compilers do not report it either.
    /// </remarks>
    public static void EstimateMethods(FileCoverage file, SourceMetrics metrics)
    {
        // All counts are read before the first method is added: adding one resets the line index, and rebuilding it
        // per function would make a file with many functions cost quadratic time.
        var counts = metrics.Functions.Select(f => file.LinesBetween(f.StartLine, f.EndLine)).ToList();
        for (var i = 0; i < metrics.Functions.Count; i++)
        {
            var function = metrics.Functions[i];
            if (counts[i].Total > 0)
                file.AddEstimatedMethod(i, function.Name, function.Line, function.EndLine, counts[i].Covered > 0 ? 1 : 0);
        }
        file.HasMethodData = true;
        file.MethodsEstimated = true;
    }

    // The taken counts of the decision's branches, or null with the reason why they cannot be told.
    static (long[]? Outcomes, string? Reason) Evaluate(Decision d, long reached, SortedDictionary<int, long> lines, int[] measured, DecisionMap map)
    {
        static (long[]?, string?) Fail(string why) => (null, why);
        static (long[]?, string?) Taken(params long[] counts) => (counts, null);

        switch (d.Kind)
        {
            case DecisionKind.Ternary:
                return Fail("?: decides within one line");
            case DecisionKind.DoWhile:
                return Fail("whether a do-while loop repeats does not show in line counts");
            case DecisionKind.If:
            {
                if (Entry(d.First, lines, measured, map) is not { } then)
                    return Fail("the branch has no measured line of its own");
                if (d.ElseIfLine > 0)
                    return lines.TryGetValue(d.ElseIfLine, out var other) ? Taken(then.Hits, other) : Fail("the else if has no measured line");
                if (d.Second != null)
                    return Entry(d.Second, lines, measured, map) is { } otherwise ? Taken(then.Hits, otherwise.Hits) : Fail("the else branch has no measured line of its own");
                if (!then.Exact)
                    return Fail("the branch starts with a loop, so how often it was entered is unknown");
                return Taken(then.Hits, Math.Max(0, reached - then.Hits));
            }
            case DecisionKind.Loop:
            {
                if (!d.HeadOnOneLine)
                    return Fail("the loop head spans several lines");
                if (Entry(d.First, lines, measured, map) is not { } body)
                    return Fail("the loop body has no measured line of its own");
                if (!body.Exact)
                    return Fail("the loop body starts with another loop, so how often it was entered is unknown");
                return Taken(body.Hits, Math.Max(0, reached - body.Hits));
            }
            case DecisionKind.Switch:
            {
                if (d.TooManySections)
                    return Fail($"the switch has more than {DecisionTracker.MaxSections} sections");
                if (d.Sections is not { Count: > 0 } sections)
                    return Fail("the switch has no case labels in a block");
                var taken = new long[sections.Count];
                for (var i = 0; i < sections.Count; i++)
                {
                    if (sections[i].FallsInto)
                        return Fail("a case falls through into the next one");
                    if (Entry(sections[i].Region, lines, measured, map) is not { } section)
                        return Fail("a case has no measured line of its own");
                    taken[i] = section.Hits;
                }
                return (taken, null);
            }
        }
        return Fail("unknown decision");
    }

    /// <summary>
    /// The first measured line that belongs to the region alone, with its hit count. <c>Exact</c> is false if the line
    /// holds a loop, whose count is the number of tests rather than the number of entries.
    /// </summary>
    /// <remarks>
    /// The region is a contiguous run of tokens, so only its first and last line can hold tokens from outside;
    /// every line in between belongs to it. That makes this two lookups and a binary search.
    /// </remarks>
    static (int Line, long Hits, bool Exact)? Entry(Region? region, SortedDictionary<int, long> lines, int[] measured, DecisionMap map)
    {
        if (region is not { } r)
            return null;
        int? found = null;
        if (map.Owns(r, r.StartLine) && lines.ContainsKey(r.StartLine))
        {
            found = r.StartLine;
        }
        else
        {
            var at = Array.BinarySearch(measured, r.StartLine + 1);
            at = at < 0 ? ~at : at;
            if (at < measured.Length && measured[at] < r.EndLine)
                found = measured[at];
            else if (r.EndLine > r.StartLine && map.Owns(r, r.EndLine) && lines.ContainsKey(r.EndLine))
                found = r.EndLine;
        }
        return found is { } line ? (line, lines[line], !map.LoopLines.Contains(line)) : null;
    }
}
