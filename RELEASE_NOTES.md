# Release notes

## 2.2.0

New:

- Branch and method coverage are estimated from line coverage and source code when no input has them, as with lcov
  without branch coverage, gcov without `-b` or gcovr's Cobertura without methods. Branches are the ways of `if`, `for`,
  `while` and `switch`; a function counts as called if one of its measured lines ran. Estimates are made only when every
  input lacks that kind of data, so a total never mixes measured and estimated numbers. The README describes the rules.
- Every estimated value is marked with ≈ and a note that it was not measured: in the key figures, the file and function
  tables, `coverage.md` and the console. Branch badges in the source view read `≈1/2`, with a dashed border.
- Decisions whose branches line coverage cannot tell apart, such as `if (x) return;` on one line, `?:` or `do`-`while`,
  are not counted but reported: a `?` badge at their line gives the reason, and the key figures say how many there are.
- The history keeps estimated values with a mark. The chart draws them as hollow points, and the data table and the
  labels mark them with ≈. History files written before stay readable.
- `--no-estimates` turns the estimate off, so that branch and method coverage show n/a as before.
- Source text is untrusted input, also when a gcov file carries it along. The branch estimate therefore follows
  statements nested up to 256 levels deep, keeps up to 100,000 decisions per file and evaluates a `switch` with up to
  4,096 sections. Beyond that, the analysis is marked incomplete or the `switch` not evaluable, and the run goes on.

## 2.1.0

New:

- The report footer names the license next to the version and the copyright notice: "Licensed under the Apache License
  2.0", as in `--help` and `--version`. It is plain text, not a link, so the report never points to the internet.

Changed:

- A file page without source code shows the key figures and the function table, and in the Source section only the
  reason why the code is missing. The long list of colored rows with bare line numbers is gone. Function names on such
  a page are no longer links, as there are no lines to jump to.

Fixed:

- A gcov count that cannot be read, such as `NaNk` or `-Infinityk`, is now reported as malformed and gives the line
  no count. Before, `-Infinityk` and `Infinityk` counted as covered and `NaNk` as not covered, without a warning, and
  other unreadable counts made the whole file fail or dropped its last line. The line keeps its source text.

## 2.0.1

New:

- A CycloneDX 1.6 SBOM (software bill of materials). `build.ps1` writes it next to the exe as
  `dist\coveragereport.cdx.json` and as `sbom\coveragereport.cdx.json` in the repository. It lists the exe with its
  SHA-256 and SHA-512 hashes, and the .NET runtime and single-file host built into it, with versions, licenses and hashes
  taken from the build itself. The SBOM is generated offline, without additional tools.

## 2.0.0

The tool is now called **Coverage Report**, and the executable is `coveragereport.exe`. Scripts that call
`hw-coverage.exe` need the new name; all parameters stay the same.

New:

- CRAP score (Change Risk Anti-Patterns, `CCM² × (1 − line coverage)³ + CCM`) wherever LOC and CCM are shown: per function in the function tables, Riskiest functions and `coverage.md`; as the highest score per file, folder and
  project, with the number of functions above 30, in the key figures, the file table and the console. Scores above 30 are
  marked, and each place has a legend. The README explains the score.
- `-t`, `--title <text>` sets a project title. It is the heading of the summary page and of `coverage.md`, and appears
  in the header bar of every file page and in the browser tab.
- The report footer, `coverage.md`, `--help` and `--version` show the copyright notice; the file properties of the exe
  name the product, the author and the copyright.
- The project is licensed under the Apache License 2.0. `LICENSE` holds the license text, and every source file carries
  an SPDX header.
- The README describes how to build the tool and what is needed to run it.

Changed:

- A function's line coverage, and with it its CRAP score, now also uses the end line the coverage tool reports, as the
  function table already did.
- Pages, temporary folders and history files written under the former name hw-coverage are still recognized: old pages
  are cleaned up, and old runs stay in the history chart.
- Comments and documentation in the source code were revised throughout. Behavior is unchanged except as listed here.

## 1.2.0

New:

- Source code is read from the path the coverage file names, so `--source-dir` is no longer
  needed when the files are where the coverage file says. WSL paths (`/mnt/d/...`) become `D:\...`, and relative paths
  start in the folder of the coverage file. Network paths, mapped network drives and missing drives are never read
  this way. A file on disk wins over the text stored in a gcov file.
- Files stored online only (cloud placeholders, offline files) are never read, so reading source cannot make Windows
  download anything.
- `--source-dir` now only helps with files that are not at their path. With it, the summary page and `coverage.md` also
  list the C/C++ and assembler files in those folders that no coverage file lists, with their LOC. They do not count in
  any total.
- `--source-dir-only` restores the old behavior: no path from a coverage file is read, only `--source-dir` folders. Use
  it with coverage files from others.
- Assembler files (`.s`, `.S`, `.asm`) are found and shown with their code. LOC counts their code lines; CCM is n/a.
- The function table of each file shows branch coverage next to line coverage, also without source code: a function's
  lines run to the end line the coverage file gives, else to the end the source analysis found, else up to the next
  function. `coverage.md` lists branch coverage per function too.
- Cobertura methods now carry their last line, so "fully covered" no longer looks past the method's end.

## 1.1.2

Fixed, found by the independent review of 1.1.1:

- False alarm: "analysis incomplete" appeared on correct code when one side of an `#if` opened a scope, as in
  `#ifdef __cplusplus` / `extern "C" {`, or when both sides closed one. Function names also lost their scope there.
- A block comment opened on an `#if 0` line stayed open past its `#endif`. Comments inside `#if 0` can no longer hide or fake its end, and a missing `#endif` is flagged.
- `public:`, `private:` and `protected:` no longer count into the next method's LOC.
- Two bodies of one function, with a shared head across `#ifdef` and `#else`, are now one entry, and the function list stays sorted.
- Declarations are no longer listed as functions: `DECLARE_INTERFACE_(I, B) { ... };`, `MACRO(x) Foo{1};`, `Foo a(1), b{2};`,
  and `DECLARE(x)` followed by an `enum`.
- Macros in front of a declaration no longer become its name, as in `_DEPRECATE(x) inline wchar_t* wcstok(...)`.
- `operator std::string` keeps its name.

## 1.1.1

Fixed, found by an independent review of 1.1.0:

- A function with `#else` or `#elif` in its body got line 0. Its coverage then took in the lines of earlier functions, its
  link led nowhere, and other functions' rows in the table were wrong. In Python's C headers this hit about one function in ten.
- `/*` on a directive line, as in `#define GLOB "src/*.c"`, swallowed the rest of the file. So did a `//` comment ending in a backslash.
- `#else` inside a function or class head lost or misnamed it.
- Functions no longer lost or misnamed: function-try blocks, `enum color f()`, `class __declspec(dllexport) A final`,
  `requires` clauses, `[[...]]` after the parameter list, `const` followed by a macro, a qualified name split over two lines,
  and macros such as `__nonnull(1)` or `API_EXPORT(int)`.
- Two patterns on one long line took quadratic time: `R"` without a `(`, and an upper-case word followed by `()()()`.
- A pipe in an operator name can no longer split a cell in `coverage.md`.

New:

- `#if 0` and `#if false` no longer count, in LOC or CCM.
- Digraphs (`<%`, `%>`, `%:`, `<:`, `:>`) are understood.
- When a file's braces do not add up, the report, `coverage.md` and the console say the analysis is incomplete and the
  numbers may be too low. A function still open at the end of the file is kept.
- Files matched to their source by file name only stay out of Riskiest functions, and the summary says how many there are.
- At most 100,000 functions per source file.

Corrected:

- The README claimed that 99.4 % of 1,125 functions reported by lcov were recognized. That figure was wrong: it counted table rows that lcov never reported, and not all of the misses had "no body of their own". It also claimed quadratic time was impossible. Both statements are replaced by what was measured.

## 1.1.0

New:

- **Lines of code (LOC)** for the project, each file and each function: lines holding code, without blank and
  comment-only lines.
- **Cyclomatic complexity after McCabe (CCM)** for the project, each file and each function: 1 + `if`, `for`, `while`,
  `case`, `catch`, `?:`, `&&` and `||` per function. A file's and the project's value is the sum of their functions.
- **Riskiest functions** on the summary page: the ten functions where high CCM meets little coverage, ranked by CRAP
  score. Fully covered functions up to CCM 10 are left out.
- The function table on file pages now shows LOC, CCM and line coverage, and also lists functions no coverage tool
  reports, such as templates that were never instantiated.
- `coverage.md` has the new metrics for the project, each file and each function.

Changed:

- New layout and color scheme: a band of key figures instead of cards, a white page, and the palette `#0A0F14`,
  `#0F3D3E`, `#2E6F73`, `#7FB3B8`, `#EADCC8`, `#C6601F`.
- The file table is slimmer: covered and total counts sit next to each percentage, and the uncovered and coverable
  columns are gone. The summary band still shows the project's counts.

Limits:

- LOC and CCM are computed from the source text, so they need `--source-dir` or gcov text, and otherwise show n/a.
- There is no preprocessor. Both sides of an `#if` count, code hidden in macros does not, K&R function headers are not
  recognized, and `&&` in a local rvalue reference counts as a decision. See 1.1.1 for what this release got wrong.

## 1.0.0

First version.

- Reads Cobertura XML, lcov and gcov text, detected from the content; files, folders and wildcards; merged into one report.
- Line, branch and method coverage in an HTML report, with filter, sorting and coverage history (`--history-dir`).
- Optional `coverage.md` (`--markdown`) and file exclusions (`--exclude`).
- Missing branch or method data shows as n/a, never as 0 %.
- Built-in demangler for C++ names.
- Source code is read only from `--source-dir`.
- `/` works like `-` for all parameters; `-h`, `--help`, `-?` and `/?` show the help.
- Runs offline, with no network access and no external processes. Limits on memory, output and file counts stop hostile input.
- Single `.exe` for Windows x64, with no installation and no .NET needed on the target machine.
