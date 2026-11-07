# Coverage Report

Command-line tool for Windows that renders C/C++ and assembler code coverage as an HTML report, optionally with a
Markdown summary. It shows line, branch and method coverage together with code metrics (LOC, cyclomatic complexity and CRAP score) for the project, each file and each function.

A single `coveragereport.exe`: no installation, no .NET runtime needed on the target machine. It works entirely
offline: it uses no network API, starts no process, and its reports load nothing from anywhere. (If a report is served
over http, a browser may on its own ask that server for `/favicon.ico`. No report data is in that request.)

## Requirements

To run `coveragereport.exe`:

- **Windows 10 or Windows 11, 64-bit (x64)**, or a Windows Server version supported by .NET 10.
- **Nothing to install.** The exe is self-contained: the .NET runtime is built in, and no .NET installation, Visual C++
  redistributable, ICU library or other runtime component is needed. Copy the file anywhere and run it; no
  administrator rights are required.
- **A web browser** to view the report, such as Edge, Chrome or Firefox. The report is a set of static HTML files and
  can be opened directly from disk.
- Write access to the output folder, and enough memory for the input: the tool limits itself to 4 GB of heap.

To build it from source, see [Building](#building).

## Usage

```
coveragereport -i <file|dir|wildcard> [-i ...] -o <dir> [options]
```

| Option | Meaning |
|---|---|
| `-i`, `--input <path>` | Coverage input: a file, a folder (searched recursively) or a wildcard like `reports\*.gcov`. Repeatable; all inputs are merged. |
| `-o`, `--output <dir>` | Output folder. Created if missing. |
| `-t`, `--title <text>` | Project title, at most 200 characters. It becomes the heading of the summary page and of `coverage.md`, and appears in the header bar of every file page and in the browser tab. |
| `-x`, `--exclude <pattern>` | Leave out files whose full path matches. Repeatable. |
| `-s`, `--source-dir <dir>` | Folder to look for source code in, for files that are not at the path the coverage file names. Repeatable. Its C/C++ and assembler files without coverage data are listed in the report. |
| `--source-dir-only` | Never read the paths the coverage files name, only `--source-dir` folders. Use it with coverage files from others. |
| `--history-dir <dir>` | Keep one result per run here and show coverage history in the report. |
| `--markdown` | Also write `coverage.md`: totals, code metrics and tables per file and per function, no source code. |
| `-h`, `--help`, `-?` | Show name, version, copyright and all parameters with a short explanation. |
| `--version` | Show name, version and copyright. |

All parameters also work with `/` instead of `-`: `/i` is `-i`, `/input` is `--input`, `/?` is `-?`.

Exit codes: `0` report written, `1` processing error, `2` invalid command line. Coverage percentages never affect the exit code.

```
coveragereport -i build\coverage.info -o report -t "Firmware 4.2" -x "/usr/*" --markdown --history-dir history
```

Changes per version are in [RELEASE_NOTES.md](RELEASE_NOTES.md).

## Code metrics

Coverage files carry no code metrics, so Coverage Report computes them from the C/C++ source text, for the project, each
file and each function. They need the source code (see [Source code](#source-code)) or gcov text, and otherwise show n/a.

| Metric | Meaning |
|---|---|
| LOC | Lines holding code, directives included. Blank and comment-only lines and code inside `#if 0` do not count. A function's LOC runs from the start of its head, such as a `template` line, to its closing brace; a macro call without a semicolon right before the head counts as part of it. |
| CCM | Cyclomatic complexity after McCabe: 1 + each `if`, `for`, `while`, `case`, `catch`, `?:`, `&&` and `\|\|`. `else` and `default` add nothing. A lambda counts towards its enclosing function. A file's and the project's CCM is the sum of their functions, so code outside functions adds nothing. CCM above 10, McCabe's recommended limit, is marked. |
| CRAP | Change Risk Anti-Patterns score of a function, from its CCM and its line coverage; see below. A score above 30 is marked. |

### CRAP score

The CRAP score (Change Risk Anti-Patterns) estimates how risky a function is to change. It combines complexity with
missing tests:

```
CRAP = CCM² × (1 − line coverage)³ + CCM
```

Line coverage is the share of the function's instrumented lines that ran, from 0 to 1. A fully covered function scores
its bare CCM; an untested one scores CCM² + CCM. Complexity therefore weighs little where tests exist and heavily where
they do not. A score above **30** is the usual threshold for a function that should get tests or be simplified before
it is changed:

| CCM | Line coverage | CRAP | |
|---:|---:|---:|---|
| 5 | 0 % | 30 | at the limit |
| 5 | 100 % | 5 | |
| 10 | 0 % | 110 | risky |
| 10 | 50 % | 22.5 | |
| 15 | 80 % | 16.8 | |
| 35 | 100 % | 35 | risky even when fully tested: too complex |

The score is shown wherever LOC and CCM are: in the key figures, the Riskiest functions list, the file table, the
function table of each file page, `coverage.md` and the console summary. Each place has a legend nearby. For a file, a
folder or the project, the table shows the highest score of any function in it, and the key figures add how many
functions are above 30. A function without any measured line, and every assembler file, shows n/a. Scores are shown with
one decimal, and the mark compares the shown value, so a score shown as 30 is never marked.

**Riskiest functions** on the summary page lists the ten functions with the highest CRAP score. It leaves out fully
covered functions up to CCM 10, functions without a measured line, and files matched to their source by file name
only. Rows from source text stored in gcov files carry a note.

### Function coverage

The function table of each file shows line and branch coverage per function. A function's lines run to the end line
the coverage file gives (lcov 2, Cobertura with method lines), else to the end the source analysis found, else up to
the next function. A function without branches shows n/a.

### Assembler

Assembler files (`.s`, `.S`, `.asm`) get LOC only; their CCM, CRAP and functions are n/a, since labels are not functions
whose control flow McCabe's metric could describe. LOC leaves out blank lines, `#if 0` blocks and comments: `/* */` and
`//`, lines that start with `;`, with `@` in `.s` and `.S` files, or with `#` followed by anything but a preprocessor
directive. Assembler conditionals such as `.if 0` count as code.

### Accuracy

The analysis reads the source as written, without a compiler or preprocessor. It is a heuristic, and the numbers can
differ from a tool that compiles the code:

- Both sides of an `#if` count. Only `#if 0` and `#if false` are left out.
- Code hidden in macros does not count, and an unusual macro near a function can lose or misname it, or make a declaration
  look like a function.
- K&R function headers are not recognized, and functions returning function pointers get a wrong name or are lost.
- `&&` in a local rvalue reference counts as a decision.
- In rare `#if` layouts, a body can be counted towards the wrong function.

If the braces of a file do not add up after both sides of each `#if` are read, the numbers may be too low, because a function
can swallow what follows it. The report, `coverage.md` and the console then say that the analysis is incomplete, without
naming the place. A function still open at the end of the file is kept. The flag is a hint in both directions: macros that
hide a brace can raise it on correct code, and a function lost with balanced braces does not raise it.

How far this was checked: the sample project's CCM values were counted by hand. On the system headers the sample project
uses, 115 of the 119 method rows lcov reports for C/C++ files found a function at their line, with template
instantiations counted separately; the other four are `= default` constructors, which have no body. That only shows a
function is found at that line, not that its LOC and CCM are right. Of 297 Python C headers one was flagged as incomplete,
rightly: it never closes its `extern "C" {`. The numbers were not compared with a compiler-based tool.

## Input formats

The format is detected from the file's content, not its extension.

| Format | How to produce it | Line | Branch | Method |
|---|---|---|---|---|
| lcov | `lcov --capture --rc branch_coverage=1 ...` | yes | if enabled | yes |
| gcov text | `gcov -b -c ...` | yes | with `-b` | with `-b` |
| Cobertura XML | `gcovr --cobertura ...` | yes | yes | if the file lists methods |

A metric the input does not contain is shown as **n/a**, never as 0 %, and is no error. If only some files have
branch or method data, totals count only those files and the summary says how many that is. A history curve leaves
a gap for such a run.

Known limits of the inputs:

- **gcov text** leaves out function and branch records for some template instantiations (gcov itself then reports
  "No branches" for that file). Use lcov when template code matters.
- **gcovr 7** writes no `<method>` entries, so its Cobertura files have no method coverage.
- **Cobertura** only states how many branches of a line were taken, not which ones.

## Excluding files

`*` and `?` are wildcards matched against the **full path** as the coverage file reports it. `*` also spans folders,
case is ignored, `/` and `\` are equal. So `test_*.c` alone matches nothing; write `*/test_*.c`.

A source file found on disk is also checked, by its Windows path and by its path below the source folder. So
`*/vendor/*` and `vendor/*` both keep `<source-dir>\vendor\x.h` out of the report, whatever path the coverage file names.
A Linux path such as `/build/vendor/*` only matches what the coverage file reports.

| Goal | Pattern |
|---|---|
| System and STL headers on Linux | `"/usr/*"` |
| A test folder anywhere | `"*/test/*"` |
| All headers | `"*.h"` |

System headers are **not** left out automatically: the numbers show exactly what the input contains.

## Merging

Hit counts for the same file and line add up. Use this for several test runs of one build. Branches merge by their
position on the line, so inputs should come from the same build. A relative path joins the one absolute path it is
the tail of (`src/a.c` joins `/build/src/a.c`); if several fit, it stays a separate entry.

Merging the *same* run in two formats doubles the hit counts. Line and method percentages stay right; branch
percentages can shift on template-heavy headers, because the tools count those branches differently.

## Source code

Source code is read from the path the coverage file names. Windows paths are used as they are, WSL paths such as
`/mnt/d/proj/src/a.c` become `D:\proj\src\a.c`, and a relative path starts in the folder of the coverage file that
names it (a `.gcov` file's `../src/a.c`). Linux paths such as `/home/...`, network paths (`\\server\share\...`, also
`\\wsl$\...`) and mapped network drives are never read this way; name such a folder with `--source-dir`.

What is not found there is looked up in the `--source-dir` folders by the longest matching path tail:
`/build/proj/src/a.c` finds `D:\proj\src\a.c`. If several files fit equally well, none is guessed; the report then says
why it shows no code. Such a page keeps the key figures and the function table, without a line listing. A match on the
file name alone may be a different file, so the page says so.

`.gcov` files also carry their own source text. It is used when the file is not found on disk, and the page then says
so. Such text can differ from the real file. A match on the file name alone never replaces this text.

**Files without coverage data.** With `--source-dir`, the summary page and `coverage.md` list the C/C++ and assembler
implementation files (`.c`, `.cc`, `.cpp`, `.cxx`, `.c++`, `.s`, `.S`, `.asm`) in those folders that no coverage file
lists, with their LOC. They were not built with coverage, or no test ran them. They do not count in any total: which of
their lines are executable is known only to the compiler, so a percentage would be a guess. To count them as 0 %, let
the coverage tool include them, for example with `lcov --initial`. Headers are not listed, as they only show up in
coverage data when they hold inline code. `--exclude` applies to the list as well, and a file that fits a coverage path
is never listed, even if the match was ambiguous.

The report contains your source code. Sharing the report shares the code. A coverage file decides which files are read:
a crafted one can name any C/C++ or assembler file on local drives, such as a header with credentials. With coverage
files from others, use `--source-dir-only` and name only a `--source-dir` that holds nothing secret.

## Security

Coverage files, history files and source text are treated as untrusted input.

- **No network, no processes.** The code uses no network or process API, and no native calls. A test inspects the
  compiled assembly for direct references to them.
- **Reports do not call out.** All input is escaped. As a second line of defense, a Content-Security-Policy allows
  only the report's own embedded style and script, by hash, and no connections. A CSP cannot stop a `<meta>` refresh,
  so it does not replace the escaping.
- **File access.** Only files with a C/C++ or assembler extension are read as source, so a crafted path cannot pull a
  key file into the report. A path from a coverage file is never read from a network path, a mapped network drive, a
  drive that is not there (Windows could reconnect a stored mapping), a device or an alternate data stream, since just
  opening a network path would contact another computer and offer it your Windows login. A file that is itself a link
  is skipped, while folder links on the way to it are followed. Files stored online only (cloud placeholders, offline
  files) are never read, as reading would make Windows fetch them. Symbolic links and junctions below a source folder
  are skipped; a source folder or input file you name yourself is followed, even on a network drive. Hard links
  cannot be told apart from ordinary files, so a source folder should not contain any that lead elsewhere.
- **XML.** The DTD that Cobertura files name on the internet is skipped unread; nothing is fetched, entities stay undefined.
- **Writes.** A report is built in a temporary folder inside the output folder and then moved. A failure while building,
  or a read-only or locked file noticed beforehand, leaves the old report as it was; a failure during the moves themselves
  can leave a mix, and then exits with 1. Links at the destination are replaced, not written through. `index.html` and
  `coverage.md` are overwritten even if they are not this tool's. Page names are hashes, never taken from input. Old
  pages, an old `coverage.md` and the temporary folder of a killed run are removed, since they could hold code that is
  now excluded; only what carries this tool's marker is touched, including pages written under the former name
  hw-coverage.
- **Spoofing.** Invisible and bidi characters are removed from paths, names and the title, and shown as `[U+202E]` in
  source lines. Paths in Markdown are code spans and the title is escaped, so neither can become a link or mention.
  History ignores runs dated more than 10 minutes ahead; forged files in the history folder can still push real runs out
  of the chart, which takes write access to that folder.
- **History** holds paths and counts, never source code. Runs saved under the former name hw-coverage are still read.
- **Source analysis.** One pass over the text, with no recursion and no regular expressions, so hostile source cannot
  overflow the stack. It is built to stay linear in the size of the text, and tests attack the patterns known to have
  slowed it down. Function names are escaped like every other input and capped at 256 characters.
- **Limits.** Input that exceeds a limit ends with a clean error and exit code 1. There is no limit on running time or on
  the total size of several inputs, and up to 4 GB of memory can be a heavy load on a small machine.

| Limit | Value |
|---|---|
| Memory (heap) | 4 GB; large inputs can reach it before the input file limit |
| Input file | 512 MB each |
| Source file | 32 MB; a larger one is shown without code |
| Line number, and source lines per file | 2,000,000 |
| Source files in the inputs | 100,000, counted before excludes |
| Report size | 4 GB |
| XML nesting | 64 levels |
| Title | 200 characters |

These end in a warning instead, and the run continues:

| Limit | Value |
|---|---|
| Branches per line (Cobertura) | 256; a line above it loses its branch data |
| Functions per source file | 100,000; the rest is left out and the analysis marked incomplete |
| Files without coverage data | 100,000; the rest is not listed |
| History read | newest 50 valid runs, at most 200 files and 256 MB |
| History file | 64 MB; a larger run is not saved |

## Building

Prerequisites:

- Windows x64 with PowerShell.
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) (developed with 10.0.403).
- Internet access for the first build only: publishing downloads .NET's runtime pack for `win-x64` from nuget.org
  once and caches it. The project itself has no NuGet dependencies.

Build the self-contained single-file exe:

```
.\build.ps1
```

The result is `dist\coveragereport.exe`, trimmed and compressed, ready to copy to any machine that meets the
[requirements](#requirements), together with its [SBOM](#sbom). The script turns off the .NET CLI telemetry for the build.

The build is reproducible: the same source and SDK produce a byte-identical exe, so its hash in the SBOM can be checked
by building again.

For development, the tool can also run straight from the source tree; this needs the .NET 10 runtime that comes with the
SDK:

```
dotnet run --project src -c Release -- -i build\coverage.info -o report
```

## SBOM

Every build writes a software bill of materials in the [CycloneDX](https://cyclonedx.org/) 1.6 JSON format, next to the
exe as `dist\coveragereport.cdx.json` and in the repository as `sbom\coveragereport.cdx.json`. It lists:

| Component | Content |
|---|---|
| Coverage Report | The exe itself: version, author, Apache-2.0 license, copyright, SHA-256 and SHA-512 hashes. |
| `Microsoft.NETCore.App.Runtime.win-x64` | The .NET runtime built into the exe, in trimmed form: version, MIT license, supplier, the source commit of the .NET release, and the hashes of the package. Its third-party notices are in the package's `THIRD-PARTY-NOTICES.TXT`. |
| `Microsoft.NETCore.App.Host.win-x64` | The single-file host that starts the runtime and forms the native part of the exe: version, MIT license and the hashes of `singlefilehost.exe` before bundling. |

The dependencies section states that the exe contains both .NET components. The project has no other dependencies.

`tools\New-Sbom.ps1` creates the SBOM from the build itself: the runtime version from the `deps.json` the publish
writes, package data from the local NuGet cache and the SDK, hashes from the files. It needs no extra tools and nothing
from the internet. Each run creates a new BOM with its own serial number and timestamp, as CycloneDX intends, so the
copy in the repository changes with every build; commit it together with a release. After a .NET patch update of the
SDK, the next build picks up the new runtime version automatically.

## Tests

```
dotnet run --project tests -c Release
```

No test framework, exit code 0 means all passed. `tests/data` holds real output of gcov, lcov 2.0 and gcovr 7.0 for the
C/C++ project in `sample/`. To regenerate it, run `sh sample/generate.sh` inside Linux or WSL with gcc, g++, gcov, lcov
and gcovr installed. `tests/data/demangle.tsv` holds mangled names with `c++filt`'s reading as the reference for the
built-in demangler.

## C++ names

Mangled names are demangled by a built-in Itanium ABI demangler. Function pointers, arrays, member pointers,
expressions and variadic templates with several arguments keep their mangled name: a wrong name would be worse.

## License

Copyright © 2026 Hanno Witt

Licensed under the Apache License, Version 2.0; see [LICENSE](LICENSE) for the full text. You may not use this project
except in compliance with the License. Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

Every source file starts with an [SPDX](https://spdx.dev/) header that states its copyright and license:

```
// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0
```
