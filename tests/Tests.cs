// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace CoverageReport.Tests;

/// <summary>
/// A minimal test runner: every static method whose name starts with "Test" is run, and each check is counted.
/// It needs no test framework, which keeps the solution free of NuGet packages. The exit code is 0 if all checks pass.
/// </summary>
static class TestRunner
{
    static int _failures;
    static int _checks;
    static string _temp = "";
    static string _data = "";

    static int Main()
    {
        _data = FindData();
        _temp = Path.Combine(Path.GetTempPath(), "coveragereport-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
        try
        {
            foreach (var test in typeof(TestRunner).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal)))
            {
                var before = _failures;
                try
                {
                    test.Invoke(null, null);
                }
                catch (TargetInvocationException e)
                {
                    Fail($"threw {e.InnerException}");
                }
                Console.WriteLine($"{(_failures == before ? "ok  " : "FAIL")} {test.Name}");
            }
        }
        finally
        {
            Directory.Delete(_temp, true);
        }
        Console.WriteLine($"{_checks} checks, {_failures} failures");
        return _failures == 0 ? 0 : 1;
    }

    static string FindData()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "tests", "data")))
            dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new DirectoryNotFoundException("tests/data"), "tests", "data");
    }

    static void Fail(string message)
    {
        _failures++;
        Console.WriteLine("  " + message);
    }

    static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition)
            Fail(message);
    }

    static void Equal<T>(T expected, T actual, string message)
    {
        _checks++;
        if (!Equals(expected, actual))
            Fail($"{message}: expected <{expected}>, got <{actual}>");
    }

    static string TempDir()
    {
        var dir = Path.Combine(_temp, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static FileCoverage WithLines(SortedDictionary<int, long> lines)
    {
        var file = new FileCoverage("/p/a.c");
        foreach (var (line, hits) in lines)
            file.AddLine(line, hits);
        return file;
    }

    static FileCoverage Single(List<FileCoverage> files)
    {
        Equal(1, files.Count, "file count");
        return files[0];
    }

    // Runs the command line in-process with its output discarded and returns the exit code.
    static int Cli(params string[] args)
    {
        var (o, e) = (Console.Out, Console.Error);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            return Program.Main(args);
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
            Console.OutputEncoding = Encoding.UTF8;
        }
    }

    // ---- demangler ----

    // tests/data/demangle.tsv holds c++filt's output as the reference; see Aliased for the one intended difference.
    static void TestDemanglerMatchesCppFilt()
    {
        string[] mayStayMangled = ["_ZTV1A", "_Z1gPFviE", "_Z1fRA3_i", "_ZZ4mainENKUlvE_clEv", "_Z3bazIJicEEvDpT_"];
        var resolved = 0;
        var rows = File.ReadAllLines(Path.Combine(_data, "demangle.tsv"));
        foreach (var row in rows)
        {
            var parts = row.Split('\t');
            var expected = Aliased(parts[1]);
            var actual = Aliased(Demangler.Demangle(parts[0]));
            if (actual == parts[0] && parts[0] != parts[1])
            {
                Check(mayStayMangled.Contains(parts[0]) || !IsSimple(parts[0]), $"not resolved: {parts[0]}");
                continue;
            }
            resolved++;
            Equal(expected, actual, parts[0]);
        }
        Check(resolved >= rows.Length * 95 / 100, $"resolved only {resolved} of {rows.Length}");
    }

    // c++filt spells out the std:: aliases, while the demangler keeps them short. That also moves where "> >" appears.
    static string Aliased(string name) => name
        .Replace("std::basic_string<char, std::char_traits<char>, std::allocator<char> >", "std::string")
        .Replace("std::basic_ostream<char, std::char_traits<char> >", "std::ostream")
        .Replace(" >", ">");

    // Function types, arrays, member pointers and expressions are the documented limitations of the demangler.
    static bool IsSimple(string mangled) => !mangled.Contains('F') && !mangled.Contains('A') && !mangled.Contains('M') && !mangled.Contains('X');

    static void TestDemanglerKnownNames()
    {
        Equal("demo::Tokenizer::split(char) const", Demangler.Demangle("_ZNK4demo9Tokenizer5splitEc"), "const member");
        Equal("demo::Stack<int>::push(int const&)", Demangler.Demangle("_ZN4demo5StackIiE4pushERKi"), "template member");
        Equal("void A<int>::f<char>(char)", Demangler.Demangle("_ZN1AIiE1fIcEEvT_"), "T_ means the innermost arguments");
        Equal("clamp", Demangler.Demangle("clamp"), "C names stay");
        Equal("__cxx_global_var_init", Demangler.Demangle("__cxx_global_var_init"), "other names stay");
    }

    static void TestDemanglerFailsSafely()
    {
        string[] broken = ["_Z", "_ZN", "_ZN1", "_Z99999999999f", "_ZNE", "_ZS_", "_Z1fS5000_", "_Z1fT_", "_Z1fI", "_ZN1AE.cold", "_Z<script>"];
        foreach (var name in broken)
            Equal(name, Demangler.Demangle(name), "malformed name stays as it is");

        // Each 1B<X, X> doubles the text, since substitution indexes 1, 3, 5, ... each refer to the previous step.
        var bomb = new StringBuilder("_Z2f01AIiE");
        for (var i = 0; i < 40; i++)
            bomb.Append($"1BIS{ToBase36(2 * i + 1)}_S{ToBase36(2 * i + 1)}_E");
        Equal(bomb.ToString(), Demangler.Demangle(bomb.ToString()), "doubling stops at the output limit");
        var small = new StringBuilder("_Z2f01AIiE");
        for (var i = 0; i < 3; i++)
            small.Append($"1BIS{ToBase36(2 * i + 1)}_S{ToBase36(2 * i + 1)}_E");
        Check(Demangler.Demangle(small.ToString()).Contains("B<B<B<A<int>", StringComparison.Ordinal), "the same form resolves when small: the limit is what stopped it");

        string[] tooDeep =
        [
            "_Z1f" + new string('P', 1000) + "i",
            "_Z1f" + string.Concat(Enumerable.Repeat("1BI", 300)) + "i" + new string('E', 300),
            "_Z1fI" + new string('J', 900) + "i" + new string('E', 900) + "Ev",
        ];
        var timer = Stopwatch.StartNew();
        foreach (var name in tooDeep)
            Equal(name, Demangler.Demangle(name), "too deep stays mangled");
        var longName = new string('x', 1_000_000);
        Equal(longName, Demangler.Demangle(longName), "too long stays as it is");
        Check(timer.ElapsedMilliseconds < 2000, $"hostile names took {timer.ElapsedMilliseconds} ms");
    }

    // Substitution ids count in base 36, with "_" first: index 0 is S_, index 1 is S0_.
    static string ToBase36(int index)
    {
        const string digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        if (index == 0)
            return "";
        var value = index - 1;
        return value < 36 ? digits[value].ToString() : digits[value / 36].ToString() + digits[value % 36];
    }

    // ---- code metrics: expected values are worked out by hand from McCabe's rule ----

    static SourceMetrics Metrics(string source) => CodeAnalyzer.Analyze(source.Split('\n'));

    static FunctionMetrics OnlyFunction(string source)
    {
        var functions = Metrics(source).Functions;
        Equal(1, functions.Count, $"one function in: {source}");
        return functions[0];
    }

    static void TestComplexityCountsEachDecision()
    {
        (string Body, int Expected)[] cases =
        [
            ("", 1),
            ("if (a) x();", 2),
            ("if (a) x(); else y();", 2),
            ("if (a) x(); else if (b) y(); else z();", 3),
            ("for (;;) x();", 2),
            ("for (int v : list) x();", 2),
            ("while (a) x();", 2),
            ("do { x(); } while (a);", 2),
            ("switch (a) { case 1: x(); break; case 2: case 3: y(); break; default: z(); }", 4),
            ("try { x(); } catch (int) { y(); } catch (...) { z(); }", 3),
            ("int v = a ? 1 : 2;", 2),
            ("if (a && b) x();", 3),
            ("if (a || b && c) x();", 4),
            ("if (a and b or c) x();", 4),
            ("auto f = [](int v) { if (v) x(); };", 2),
            ("goto end; end: return;", 1),
        ];
        foreach (var (body, expected) in cases)
            Equal(expected, OnlyFunction($"void f() {{ {body} }}").Complexity, body);
    }

    static void TestComplexityIgnoresWhatIsNotCode()
    {
        (string Body, int Expected)[] cases =
        [
            ("// if (a) && b\n", 1),
            ("/* if (a) \n for (;;) */", 1),
            ("const char* s = \"if (a) && { } ? || \";", 1),
            ("const char* s = \"\\\" if (a)\";", 1),
            ("char c = '{'; char d = '\\''; char e = '?';", 1),
            ("auto s = R\"(if (a) \" { } ?)\";", 1),
            ("auto s = R\"x(\n if (a) )\" {\n)x\";", 1),
            ("int n = 1'000'000; if (n) x();", 2),
            ("x_if(); forever(); whilst();", 1),
            ("foo::bar(); a->b();", 1),
        ];
        foreach (var (body, expected) in cases)
            Equal(expected, OnlyFunction("void f() {\n" + body + "\n}").Complexity, body);
    }

    static void TestLinesOfCode()
    {
        var m = Metrics("""
            // comment

            /* block
               comment */
            #include <a.h>
            int x; // trailing comment

            /* leading */ int y;
            void f()
            {
                // inside

                g();
            }
            """);
        Equal(7, m.Loc, "#include, x, y, void f(), {, g(), }");
        Equal(4, m.Functions[0].Loc, "signature to closing brace, comment and blank line left out");
        Equal(9, m.Functions[0].Line, "line of the name");
        Equal(14, m.Functions[0].EndLine, "line of the closing brace");

        Equal(0, Metrics("").Loc, "empty");
        Equal(0, Metrics("// only\n\n/* a comment */").Loc, "only comments");
        Equal(0, Metrics("").Functions.Count, "no functions");
    }

    static void TestAssemblerLinesOfCode()
    {
        static SourceMetrics Asm(string text, string extension = ".S") => SourceResolver.Analyze("x" + extension, text.Split('\n'));

        var m = Asm("""
            /* startup code
               for the demo */
            #include "regs.h"
            #define STACK 0x1000
            @ ARM comment
            // C++ comment
            # x86 comment
            ; empty statement or comment

                .syntax unified
                .global Reset_Handler
            Reset_Handler:            @ trailing comment
                ldr r0, =STACK        /* trailing */
                mov r1, #1
            #if 0
                b .
            #else
                nop
            #endif
                .ascii "/* no comment"
                bx lr
            """);
        Equal(13, m.Loc, "directives, statements, label, #if 0 and #else/#endif lines; no comments, blanks or #if 0 body");
        Check(m.IsAssembly && m.Functions.Count == 0 && !m.Incomplete, "LOC only");

        Equal(2, Asm("mov r0, r1 @ see /* here\nnop").Loc, "a /* behind @ opens no comment");
        Equal(2, Asm("mov r0, r1 ; see /* here\nnop", ".asm").Loc, "nor behind ;");
        Equal(2, Asm("mov r0, r1 /* a\n b */ nop\n/* c */").Loc, "code behind the end of a comment counts");
        Equal(2, Asm("@@:\n  jmp @B", ".asm").Loc, "@ is no comment in .asm (MASM labels)");
        Equal(0, Asm("@@:", ".s").Loc, "but in .s");
        Equal(1, Asm("; comment\nnop", ".asm").Loc, "; comments in .asm");
        Equal(0, Asm("# 1 \"start.S\"").Loc, "line markers are comments");
        Equal(1, Asm("#if 0\n#if X\nnop\n#endif\nnop\n").Loc, "nested #if inside #if 0, which is left open");
        Check(Asm("#if 0\nnop").Incomplete, "an open #if 0 is flagged");
        Equal(3, Asm("#if 0 /* open\nnop\n#endif\nnop").Loc, "a comment opened on #if 0 ends with it");
        Check(Asm("/* open").Incomplete, "an open comment is flagged");
        Equal(0, Asm("").Loc, "empty");
        Check(!SourceResolver.Analyze("x.c", ["int a;"]).IsAssembly, "C stays C");
    }

    // An assembler file shows its code and LOC, while CCM, CRAP and the analyzed functions stay n/a.
    static void TestCliAssemblerFiles()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "start.S");
        File.WriteAllText(file, "@ vector table\n.global f\nf:\n  bx lr\n");
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, $"SF:{file}\nFN:3,f\nFNDA:1,f\nFNF:1\nFNH:1\nDA:3,1\nDA:4,1\nend_of_record\n");

        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "--markdown"), "run");
        var page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("bx lr", StringComparison.Ordinal), "the source is shown");
        Check(page.Contains("for assembler, neither CCM nor CRAP is computed", StringComparison.Ordinal), "the function table says why CCM is missing");
        Check(File.ReadAllText(Path.Combine(o, "index.html")).Contains("Not computed for assembler", StringComparison.Ordinal), "and so does the summary");
        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("| 3 | n/a | n/a |", StringComparison.Ordinal), "LOC 3, CCM and CRAP n/a in the file table");
        Check(md.Contains("| Lines of code | 3 |", StringComparison.Ordinal), "LOC in the totals");
        Check(md.Contains("| Cyclomatic complexity after McCabe (CCM) | n/a |", StringComparison.Ordinal), "CCM n/a in the totals");
    }

    // Without source, a function's lines run to the end line the coverage file reports, or else up to the next function.
    static void TestFunctionTableCoverage()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nFN:1,3,exact\nFN:5,next\nFN:9,last\nFNDA:1,exact\nFNDA:1,next\nFNDA:0,last\n"
            + "DA:1,1\nDA:2,0\nDA:3,1\nDA:5,1\nDA:6,1\nDA:7,0\nDA:9,0\nDA:10,0\n"
            + "BRDA:2,0,0,1\nBRDA:2,0,1,0\nBRDA:4,0,0,1\nBRDA:6,0,0,1\nBRDA:6,0,1,1\nend_of_record\n");
        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o), "run");
        var page = File.ReadAllText(Path.Combine(o, HtmlReport.PageName("/p/a.c")));
        string Row(string name) => Regex.Match(page, $"<tr><td class=\"left fn\">(<a[^>]*>)?{name}<.*?</tr>").Value;

        Check(page.Contains("<th>Line coverage</th><th>Branch coverage</th>", StringComparison.Ordinal), "both columns");
        Check(Row("exact").Contains("<td>66.6%</td><td>50%</td>", StringComparison.Ordinal), $"lines 1 to 3, as lcov says: {Row("exact")}");
        Check(Row("next").Contains("<td>66.6%</td><td>100%</td>", StringComparison.Ordinal), $"lines 5 to 8, up to the next function: {Row("next")}");
        Check(Row("last").Contains("<td>0%</td><td class=\"na\">n/a</td>", StringComparison.Ordinal), $"no branches: {Row("last")}");
    }

    static void TestCoberturaMethodEndLine()
    {
        var xml = "<?xml version=\"1.0\"?><coverage><packages><package><classes><class filename=\"a.c\"><methods>"
            + "<method name=\"f\" signature=\"\"><lines><line number=\"3\" hits=\"1\"/><line number=\"7\" hits=\"0\"/></lines></method>"
            + "</methods><lines><line number=\"3\" hits=\"1\"/><line number=\"7\" hits=\"0\"/></lines></class></classes></package></packages></coverage>";
        var method = Single(Parsers.ParseCobertura(xml, [])).Methods["f"];
        Equal(3, method.Line, "first line of the method");
        Equal(7, method.EndLine, "last line of the method");
    }

    static void TestFunctionLocStartsAtTheHead()
    {
        var f = OnlyFunction("template <typename T>\nstatic inline T\nname(T a,\n     T b)\n{\n  return a;\n}");
        Equal(7, f.Loc, "the whole head counts");
        Equal(3, f.Line, "coverage tools report the name's line");
        Equal("name", f.Name, "name");
    }

    static void TestFunctionRecognition()
    {
        (string Source, string[] Names)[] cases =
        [
            ("int a() { return 1; } int b() { return 2; }", ["a", "b"]),
            ("namespace n { void f() {} }", ["n::f"]),
            ("namespace { void f() {} }", ["f"]),
            ("namespace a::b { void f() {} }", ["a::b::f"]),
            ("extern \"C\" { void f() {} }", ["f"]),
            ("extern \"C\" void f() {}", ["f"]),
            ("class C { public: void f() {} int g() const { return 1; } };", ["C::f", "C::g"]),
            ("struct S : Base<int> { S() {} ~S() {} };", ["S::S", "S::~S"]),
            ("template <class T> class C final : public B { void f() {} };", ["C::f"]),
            ("template <class T> void f(T a) {}", ["f"]),
            ("template <> void f<int>(int a) {}", ["f"]),
            ("void C::f() {}", ["C::f"]),
            ("C::C() : a(1), b{2} {}", ["C::C"]),
            ("C::C() : a{1} , b{2} { x(); }", ["C::C"]),
            ("C::~C() noexcept {}", ["C::~C"]),
            ("C() SOME_NOEXCEPT : a(1) { x(); }", ["C"]),
            ("C() const SOME_MACRO : a{1}, b(2) {}", ["C"]),
            ("namespace std SOME_MACRO(default) { void f() {} }", ["std::f"]),
            ("class API(x) C { void f() {} };", ["C::f"]),
            ("class EXPORT C { void f() {} };", ["C::f"]),
            ("SOME_MACRO(x) public: void f() {}", ["f"]),
            ("int f() noexcept(true) { return 1; }", ["f"]),
            ("auto f() -> int { return 1; }", ["f"]),
            ("auto f() -> std::vector<int> { return {}; }", ["f"]),
            ("__attribute__((used)) void f() {}", ["f"]),
            ("void f(int a = {}) {}", ["f"]),
            ("void f(void (*cb)(int)) {}", ["f"]),
            ("void f() SOME_MACRO {}", ["f"]),
            ("void f() const SOME_MACRO {}", ["f"]),
            ("void f() const FIRST_MACRO SECOND_MACRO {}", ["f"]),
            ("T& f() const override some_macro {}", ["f"]),
            ("TEST(Suite, Case) { x(); }", ["TEST"]),
            ("struct S f() { struct S s; return s; }", ["f"]),
            ("bool C::operator==(const C& o) const { return true; }", ["C::operator=="]),
            ("C& operator=(const C& o) { return *this; }", ["operator="]),
            ("void operator()(int a) {}", ["operator()"]),
            ("operator bool() const { return true; }", ["operator bool"]),
            ("int a[] = { 1, 2 };", []),
            ("int a[] { 1, 2 };", []),
            ("enum E { A, B };", []),
            ("enum class E { A, B };", []),
            ("struct S { int a; };", []),
            ("void f();", []),
            ("void f() = delete;", []),
            ("auto x = [] { if (a) b(); };", []),
            ("S s = { .a = 1 };", []),
            ("int a < 3; void f() {}", ["f"]),
            ("void f() try { x(); } catch (int) { y(); }\nvoid g() {}", ["f", "g"]),
            ("C::C() try : a(1) { x(); } catch (...) { y(); } catch (int) { z(); }", ["C::C"]),
            ("enum color f(int a) { return RED; }", ["f"]),
            ("class __declspec(dllexport) A final { void f() {} };", ["A::f"]),
            ("struct alignas(8) A final { void f() {} };", ["A::f"]),
            ("MACRO(x)\nclass A final { void f() {} };", ["A::f"]),
            ("template <class T> void f(T t) requires Foo<T> { x(); }", ["f"]),
            ("void foo() [[gnu::cold]] { x(); }", ["foo"]),
            ("[[nodiscard]] int f() { return 1; }", ["f"]),
            ("[[deprecated(\"x\")]] int FOO() { return 1; }", ["FOO"]),
            ("template <class T> requires requires(T t) { t.x(); } void f(T t) {}", ["f"]),
            ("void foo(int *p) __nonnull(1) { x(); }", ["foo"]),
            ("API_EXPORT(int) FOO(int a) { return a; }", ["FOO"]),
            ("EXPORT(int) foo(int a) { return a; }", ["foo"]),
            ("C::C() MACRO(x) { y(); }", ["C::C"]),
            ("MACRO(x)\nFoo foo{1};\nvoid g() {}", ["g"]),
            ("NS_BEGIN { void f() {} }", ["f"]),
            ("void f() <% x(); %>\nvoid g() {}", ["f", "g"]),
            ("bool operator|(int a) { return a; }", ["operator|"]),
            ("void ​‌() {}", ["(unnamed)"]),
            ("_Check_return_ _DEPRECATE(x) inline wchar_t* wcstok(int a) { return 0; }", ["wcstok"]),
            ("class A { Q_OBJECT Q_DISABLE_COPY(A) public: A() {} };", ["A::A"]),
            ("operator std::string() const { return s; }", ["operator std::string"]),
            ("MACRO(x) FOO{1};\nvoid g() {}", ["g"]),
            ("MACRO(x) foo{1};\nvoid g() {}", ["g"]),
            ("Foo a(1), b{2};\nvoid g() {}", ["g"]),
            ("DECLARE(x)\nenum foo { A };\nvoid g() {}", ["g"]),
            ("MACRO(x) Foo<int>{1};\nvoid g() {}", ["g"]),
            ("DECLARE_INTERFACE_(I, B) { void x(); };\nvoid g() {}", ["g"]),
            ("TEST(a, b) { x(); }\nvoid g() {}", ["TEST", "g"]),
            ("void f() {};\nvoid g() {};", ["f", "g"]),
        ];
        foreach (var (source, names) in cases)
            Equal(string.Join(", ", names), string.Join(", ", Metrics(source).Functions.Select(f => f.Name)), source);
    }

    static void TestPreprocessorDoesNotConfuseBraces()
    {
        var bothHeads = Metrics("#ifdef A\nvoid f(int a) {\n#else\nvoid f(long a) {\n#endif\n  if (a) x();\n}\nvoid g() {}\n");
        Equal("f, g", string.Join(", ", bothHeads.Functions.Select(f => f.Name)), "two heads open one body");
        Equal(2, bothHeads.Functions[0].Complexity, "body counted once");

        var bothBodies = OnlyFunction("void f() {\n#if A\n  if (a) x();\n#else\n  if (b) y();\n#endif\n}\n");
        Equal(3, bothBodies.Complexity, "both sides of an #if count");
        Equal(1, bothBodies.Line, "an #else in the body leaves the function's line alone");
        Equal("f", bothBodies.Name, "and its name");

        // Regression: these functions used to end up at line 0, absorb the lines of earlier functions and break the lookup.
        var several = Metrics("void a0() {\n x();\n}\nvoid b0() {\n#ifdef Q\n y();\n#else\n z();\n#endif\n}\nvoid c0() {\n}\n");
        Equal("a0@1, b0@4, c0@11", string.Join(", ", several.Functions.Select(f => $"{f.Name}@{f.Line}")), "lines stay right and sorted");
        Equal("b0", several.FunctionAt(5)?.Name, "lookup after an #else");
        several.AttachCoverage(WithLines(new() { [1] = 0, [2] = 0, [4] = 1, [6] = 1, [8] = 1 }));
        Equal(new Counts(3, 3), several.Functions[1].Coverage, "b0 does not take in the lines of a0");

        // GCC reports the side it compiled (here the first), while the scanner reads through to the last side.
        var twoHeads = Metrics("#ifdef A\nvoid f(int a)\n#else\nvoid f(long a)\n#endif\n{\n x();\n}\nvoid g() {}\n");
        Equal("f", twoHeads.FunctionAt(2)?.Name, "the first side's name line finds the function");
        Equal("f", twoHeads.FunctionAt(4)?.Name, "and so does the second's");
        Equal("g", twoHeads.FunctionAt(9)?.Name, "the next function is unaffected");
        var openBodies = Metrics("#ifdef A\nvoid f(int a) {\n#else\nvoid f(long a) {\n#endif\n x();\n}\n");
        Equal("f", openBodies.FunctionAt(2)?.Name, "also when each side opens the body");

        var inHead = OnlyFunction("void f(int a\n#ifdef X\n, int b\n#else\n, long b\n#endif\n) {\n}\n");
        Equal("f", inHead.Name, "an #else inside a parameter list");
        Equal("C::f", Metrics("class C\n#ifdef X\n: public B\n#else\n: public D\n#endif\n{\n void f() {}\n};\n").Functions[0].Name, "an #else inside a class head");
        Equal("C::C", OnlyFunction("C::C()\n#ifdef X\n : a(1)\n#else\n : a(2)\n#endif\n{\n}\n").Name, "an #else inside initializers");
        Equal(1, OnlyFunction("C::C()\n#ifdef X\n : a(1)\n#else\n : a(2)\n#endif\n{\n}\n").Line, "and the line stays");

        var closedTwice = Metrics("void f() {\n#if A\n}\n#else\n}\n#endif\nvoid g() {}\n");
        Equal("f, g", string.Join(", ", closedTwice.Functions.Select(f => f.Name)), "both sides closing one body is one function");

        // #if 0 is the one condition whose value is known without a preprocessor.
        var dead = Metrics("#if 0\nvoid old() {\n if (a) x();\n#endif\nvoid g() {}\n");
        Equal("g", string.Join(", ", dead.Functions.Select(f => f.Name)), "an open brace in #if 0 harms nothing");
        Equal(3, dead.Loc, "#if 0, #endif and g: dead code is no code");
        Check(!dead.Incomplete, "nothing is wrong with this file");
        Equal("b", string.Join(", ", Metrics("#if 0\nvoid a() {}\n#else\nvoid b() {}\n#endif\n").Functions.Select(f => f.Name)), "#else of an #if 0 is live");
        Equal("g", string.Join(", ", Metrics("#if 0\n#if X\nvoid a() {}\n#else\nvoid b() {}\n#endif\n#endif\nvoid g() {}\n").Functions.Select(f => f.Name)), "nested inside #if 0");

        // An access specifier does not belong to the method after it.
        Equal(1, Metrics("class A {\npublic:\n  void f() {}\n};").Functions[0].Loc, "public: is not part of f");

        // The two sides yield two bodies of one function: they become one entry, and the list stays sorted.
        var twoBodies = Metrics("void f(int a\n#ifdef X\n, int b) {\n}\n#else\n) {\n}\n#endif\nvoid g() {}\n");
        Equal("f, g", string.Join(", ", twoBodies.Functions.Select(f => f.Name)), "merged");
        Equal(1, twoBodies.Functions[0].StartLine, "from the head");
        Equal(7, twoBodies.Functions[0].EndLine, "to the last body");

        // A scope opened on one side of an #if stays open. This is correct code, not a brace error.
        foreach (var correct in new[]
        {
            "#ifdef __cplusplus\nextern \"C\" {\n#else\nint a;\n#endif\nvoid f() {}\n#ifdef __cplusplus\n}\n#endif\n",
            "#if A\nnamespace n {\n#elif B\nnamespace m {\n#else\n#error no\n#endif\nvoid f() {}\n}\n",
            "namespace n {\n#if A\n}\n#else\n}\n#endif\nvoid f() {}\n",
        })
        {
            var m = Metrics(correct);
            Check(!m.Incomplete, $"no false alarm on: {correct}");
            Equal(1, m.Functions.Count, $"function kept in: {correct}");
        }
        Equal("n::f", Metrics("#if A\nnamespace n {\n#else\n#error no\n#endif\nvoid f() {}\n}\n").Functions[0].Name, "and names keep their scope");

        // #if 0 combined with comments: the dead code must end exactly where the preprocessor ends it.
        foreach (var dead2 in new[]
        {
            "#if 0 /* old\nstill */\nvoid dead() {}\n#endif\nvoid g() {}\n",
            "#if 0\n/*\n#if FOO\n*/\nvoid dead() {}\n#endif\nvoid g() {}\n",
            "#if 0\n/* #endif */\nvoid dead() {}\n#endif\nvoid g() {}\n",
            "#if 0\n/* x */ void dead() {}\n/* y */ #endif\nvoid g() {}\n",
            "#if 0\nvoid dead() {}\n%:endif\nvoid g() {}\n",
            "#if 0 // x \\\nvoid dead() {}\n#endif\nvoid g() {}\n",
            "#if 0\n// #endif\nvoid dead() {}\n#endif\nvoid g() {}\n",
        })
        {
            var m = Metrics(dead2);
            Equal("g", string.Join(", ", m.Functions.Select(f => f.Name)), dead2);
            Check(!m.Incomplete, $"no false alarm on: {dead2}");
        }
        Equal("g", string.Join(", ", Metrics("#if 0 /* off */ || X\nvoid g() {}\n#endif\n").Functions.Select(f => f.Name)), "not always false");
        Check(Metrics("#if 0\nvoid dead() {}\n").Incomplete, "a missing #endif is flagged");

        // Regression: each of these once swallowed the rest of the file without any warning.
        foreach (var derail in new[]
        {
            "#define GLOB \"src/*.c\"",
            "#include \"a/*b.h\"",
            "#define X 1 // see /* below",
            "#error don't /* stop",
            "// C:\\tmp\\\nif (a) {",
            "%:define X {",
        })
        {
            var after = Metrics(derail + "\nvoid g() {}\n");
            Equal("g", string.Join(", ", after.Functions.Select(f => f.Name)), derail);
            Check(!after.Incomplete, $"no false alarm: {derail}");
        }
        Equal("g", string.Join(", ", Metrics("#define C /* open\n still */\nvoid g() {}\n").Functions.Select(f => f.Name)), "a real comment in a directive is honoured");

        Equal(1, OnlyFunction("#define M(x) if (x) { y(); }\nvoid f() {}").Complexity, "a macro definition is no code of the function");
        Equal(1, OnlyFunction("#define M \\\n  if (x) { \\\n y(); }\nvoid f() {}").Complexity, "nor is a continued one");
        Equal(3, Metrics("#define A 1\n#define B \\\n 2\n").Loc, "directive lines count as code");
    }

    static void TestMalformedSourceStaysHarmless()
    {
        string[] broken =
        [
            "void f() {", "}", "}}}}", "{{{{", "void f() { /* never closed", "void f() { \" never closed", "void f() { ' never closed",
            "auto s = R\"x(never closed", "auto s = R\"", "#", "#if", "#else", "#endif", "#endif\n#endif", "(((((", ")))))", "<<<<", "operator", "operator(", "operator()",
            "::", "~", "class", "class {", "namespace", "extern", "\\", "/*", "*/", "void f() : {", "a{1}", "= {", "\0",
        ];
        foreach (var source in broken)
        {
            var m = Metrics(source);
            Check(m.Loc >= 0, $"no crash on: {source}");
            var previous = 0;
            foreach (var f in m.Functions)
            {
                Check(f.Line >= 1 && f.Loc >= 0 && f.EndLine >= f.Line, $"sane numbers on: {source}");
                Check(f.Line >= previous, $"sorted on: {source}");
                previous = f.Line;
            }
        }
    }

    // Numbers that look plausible but may be too low must be flagged as such.
    static void TestIncompleteAnalysisIsFlagged()
    {
        foreach (var source in new[] { "void f() {", "void f() { /* open", "namespace a {", "int a[] = {", "}", "void f() {}\n}", "/* never closed", "auto s = R\"(open" })
            Check(Metrics(source).Incomplete, $"flagged: {source}");
        foreach (var source in new[] { "void f() {}", "namespace a { void f() {} }", "int a[] = { 1 };", "", "// only" })
            Check(!Metrics(source).Incomplete, $"not flagged: {source}");

        var open = Metrics("void f() {\n if (a) x();\n");
        Equal("f", open.Functions[0].Name, "a function still open at the end is kept");
        Equal(2, open.Functions[0].Complexity, "with what was read");

        var flood = Metrics(string.Concat(Enumerable.Repeat("void f(){}\n", CodeAnalyzer.MaxFunctionsPerFile + 5)));
        Equal(CodeAnalyzer.MaxFunctionsPerFile, flood.Functions.Count, "functions per file are capped");
        Check(flood.Incomplete, "and the cap is flagged");

        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "a.c"), "void f() {\n x();\n");
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/q/a.c\nDA:1,1\nend_of_record\n");
        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-s", dir, "--markdown"), "run");
        Check(File.ReadAllText(Path.Combine(o, "index.html")).Contains("Analysis incomplete in 1 of 1 files", StringComparison.Ordinal), "shown in the report");
        Check(File.ReadAllText(Path.Combine(o, "coverage.md")).Contains("Files with incomplete analysis, numbers may be too low | 1 |", StringComparison.Ordinal), "and in Markdown");
    }

    static void TestNameOnlyMatchesStayOutOfRiskiest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.c"), "void unrelated() {\n if (a) x();\n}\n");
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/build/lib/other/config.c\nDA:2,0\nend_of_record\n");
        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-s", dir, "--markdown"), "run");
        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(!index.Contains("Riskiest functions", StringComparison.Ordinal), "a file that may be another one is not ranked");
        Check(index.Contains("matched by file name only", StringComparison.Ordinal), "and the summary says so");
    }

    // Source text can also arrive embedded in a gcov file, so a single hostile line must not cost more than one pass.
    static void TestRepeatedShapesStayLinear()
    {
        const int n = 1_000_000;
        string[] hostile =
        [
            string.Concat(Enumerable.Repeat("R\"", n)),
            "AAAA" + string.Concat(Enumerable.Repeat("()", n)),
            new string('A', n) + string.Concat(Enumerable.Repeat("()", 100_000)),
            string.Concat(Enumerable.Repeat("/**/", n)),
            string.Concat(Enumerable.Repeat("\"\"", n)),
            string.Concat(Enumerable.Repeat("#else\n", n)),
            "#if 1\n" + string.Concat(Enumerable.Repeat("#else\n", n)),
            string.Concat(Enumerable.Repeat("[[", n)),
            string.Concat(Enumerable.Repeat("namespace a{", 100_000)) + string.Concat(Enumerable.Repeat("\n#if A\n#else\n#endif", 100_000)),
            "#if 0\n" + string.Concat(Enumerable.Repeat("/**/", n)) + "\n#endif",
            string.Concat(Enumerable.Repeat("a b ", n)) + "(){}",
        ];
        foreach (var source in hostile)
        {
            var timer = Stopwatch.StartNew();
            CodeAnalyzer.Analyze(source.Split('\n'));
            Check(timer.ElapsedMilliseconds < 3000, $"{source[..Math.Min(16, source.Length)]} took {timer.ElapsedMilliseconds} ms");
        }
    }

    // The analysis is a single pass without recursion, so hostile source must stay linear and cannot overflow the stack.
    static void TestHostileSourceStaysFast()
    {
        const int n = 2_000_000;
        string[] hostile =
        [
            new string('{', n),
            new string('}', n),
            new string('(', n),
            new string('<', n),
            string.Concat(Enumerable.Repeat("namespace a{", n / 12)),
            string.Concat(Enumerable.Repeat("class a{", n / 8)),
            string.Concat(Enumerable.Repeat("void f(){}", n / 10)),
            "void f(){" + string.Concat(Enumerable.Repeat("if(a)", n / 5)) + "}",
            "operator" + new string('+', n),
            new string('a', n) + "(){}",
            string.Concat(Enumerable.Repeat("#if 1\n", n / 6)),
            string.Concat(Enumerable.Repeat("a::", n / 3)) + "f(){}",
        ];
        foreach (var source in hostile)
        {
            var timer = Stopwatch.StartNew();
            var m = CodeAnalyzer.Analyze(source.Split('\n'));
            Check(timer.ElapsedMilliseconds < 5000, $"{source[..Math.Min(20, source.Length)]} took {timer.ElapsedMilliseconds} ms");
            foreach (var f in m.Functions.Take(100))
                Check(f.Name.Length <= 256, "names are capped");
        }
    }

    static void TestFunctionLookupAndCoverage()
    {
        var m = Metrics("void a() {\n  x();\n}\n\nvoid b() {\n  y();\n}\n");
        Equal("a", m.FunctionAt(1)?.Name, "first line");
        Equal("a", m.FunctionAt(3)?.Name, "last line");
        Equal(null, m.FunctionAt(4)?.Name, "between functions");
        Equal("b", m.FunctionAt(6)?.Name, "second function");
        Equal(null, m.FunctionAt(99)?.Name, "behind all");

        var file = WithLines(new() { [1] = 1, [2] = 0, [5] = 3, [6] = 3 });
        file.AddBranches(2, [1, 0]);
        file.AddBranches(4, [1, 1]);  // between the functions, so it belongs to neither
        file.AddBranches(6, [1, 1, 0]);
        m.AttachCoverage(file);
        Equal(new Counts(1, 2), m.Functions[0].Coverage, "a");
        Equal(new Counts(2, 2), m.Functions[1].Coverage, "b");
        Equal(new Counts(1, 2), m.Functions[0].BranchCoverage, "branches of a");
        Equal(new Counts(2, 3), m.Functions[1].BranchCoverage, "branches of b");
        Check(m.Functions[0].Crap > m.Functions[1].Crap, "less coverage means more risk");
        Equal<double?>(1.0, m.Functions[1].Crap, "full coverage leaves the bare complexity");

        // A reported end line wins over the analyzed end: here the tool says a() ends at line 1.
        file.AddMethod("a", 1, 1, 1);
        m.AttachCoverage(file);
        Equal(new Counts(1, 1), m.Functions[0].Coverage, "a, up to the reported end");

        // GCC reports the line where a qualified name starts: "char_traits<_CharT>::" on one line, "length()" on the next.
        var split = OnlyFunction("template <class T>\nint\nchar_traits<T>::\nlength(int a)\n{\n return a;\n}\n");
        Equal(3, split.Line, "the qualifier's line");
        Equal("char_traits::length", split.Name, "qualified name");
    }

    static void TestRiskiestFunctions()
    {
        var f = new FileCoverage("/p/a.c");
        f.Metrics = Metrics("void simple_full() {\n x();\n}\nvoid missed() {\n x();\n}\nvoid unknown() {\n x();\n}\nvoid complex_full() {\n"
            + string.Concat(Enumerable.Repeat("if (a) x();\n", 11)) + "}\n");
        foreach (var line in new[] { 1, 2, 3 })
            f.AddLine(line, 1);
        f.AddLine(4, 0);
        f.AddLine(5, 0);
        foreach (var line in Enumerable.Range(10, 13))
            f.AddLine(line, 1);
        f.Metrics.AttachCoverage(f);

        var names = ReportOf(f).RiskiestFunctions(10).Select(e => e.Function.Name).ToList();
        Equal("complex_full, missed", string.Join(", ", names), "complex first, simple and fully covered left out, unmeasured left out");
    }

    // CRAP = CCM² × (1 − coverage)³ + CCM, worked out by hand.
    static void TestCrapScore()
    {
        Equal<double?>(null, FunctionMetrics.CrapOf(5, default), "no measured line: n/a");
        Equal<double?>(30.0, FunctionMetrics.CrapOf(5, new Counts(0, 4)), "untested: 25 + 5");
        Equal<double?>(5.0, FunctionMetrics.CrapOf(5, new Counts(4, 4)), "fully tested: the bare complexity");
        Equal<double?>(2.5, FunctionMetrics.CrapOf(2, new Counts(1, 2)), "half tested: 4 × 0.125 + 2");

        Check(!Report.IsAboveCrapLimit(30.0), "30 is the limit itself");
        Check(!Report.IsAboveCrapLimit(30.04), "a score shown as 30 is not marked");
        Check(Report.IsAboveCrapLimit(30.05), "a score shown as 30.1 is");
        Equal("30.1", Report.Score(30.05), "rounded to one decimal");
        Equal("n/a", Report.Score(null), "no score");

        var report = ReportOf(CrapSample());
        var t = report.Totals;
        Equal<double?>(132.0, t.MaxCrap, "highest: CCM 11 without any test, 121 + 11");
        Equal(1L, t.FunctionsAboveCrapLimit, "one function above 30");
        Equal(2L, t.FunctionsWithCrap, "the function without measured lines has no score");
    }

    // Three functions: a simple tested one, a complex untested one (CCM 11), and one without any measured line.
    static FileCoverage CrapSample()
    {
        var f = new FileCoverage("/p/crap.c");
        f.Metrics = Metrics("void simple() {\n x();\n}\nvoid risky() {\n" + string.Concat(Enumerable.Repeat("if (a) x();\n", 10)) + "}\nvoid unknown() {\n x();\n}\n");
        f.AddLine(2, 1);
        foreach (var line in Enumerable.Range(5, 10))
            f.AddLine(line, 0);
        f.Metrics.AttachCoverage(f);
        return f;
    }

    // Expected values counted by hand from sample/src.
    static void TestSampleProjectMetrics()
    {
        var src = Path.Combine(Path.GetDirectoryName(_data)!, "..", "sample", "src");
        SourceMetrics Of(string file) => CodeAnalyzer.Analyze(File.ReadAllLines(Path.Combine(src, file)));
        string Summary(SourceMetrics m) => string.Join(", ", m.Functions.Select(f => $"{f.Name}={f.Complexity}"));

        Equal("clamp=3, gcd=2, unused_add=1", Summary(Of("mathutil.c")), "mathutil.c");
        Equal("demo::Tokenizer::Tokenizer=1, demo::Tokenizer::split=4, demo::Tokenizer::count=3, demo::Tokenizer::isBlank=1", Summary(Of("tokenizer.cpp")), "tokenizer.cpp");
        Equal("demo::Stack::push=1, demo::Stack::pop=2, demo::Stack::empty=1, demo::Stack::clear=1", Summary(Of("stack.hpp")), "stack.hpp");
        Equal("main=3", Summary(Of("main.cpp")), "main.cpp");
        Equal(0, Of("mathutil.h").Functions.Count, "a header with declarations only");

        var clamp = Of("mathutil.c").Functions[0];
        Equal(10, clamp.Loc, "clamp, lines 6 to 15");
        Equal(6, clamp.Line, "clamp starts at line 6, below the SPDX header");
    }

    // The coverage tools and the analyzer must agree on where functions are.
    static void TestRealMethodsMatchAnalyzedFunctions()
    {
        var src = Path.Combine(Path.GetDirectoryName(_data)!, "..", "sample", "src");
        var files = Parsers.ParseLcov(Parsers.ReadText(Path.Combine(_data, "lcov", "sample.info")), [])
            .Where(f => f.Path.Contains("/sample/src/", StringComparison.Ordinal));
        var methods = 0;
        foreach (var file in files)
        {
            var m = CodeAnalyzer.Analyze(File.ReadAllLines(Path.Combine(src, Path.GetFileName(file.Path))));
            foreach (var method in file.Methods.Values)
            {
                methods++;
                var function = m.FunctionAt(method.Line);
                Check(function != null, $"{method.Name} at line {method.Line} of {file.Path}");

                // It must be the right function, not just any: the source name is the last part of the demangled name.
                var plain = method.Name.Split('(')[0].Split("::")[^1].Split('<')[0].Split('[')[0];
                var analyzed = function?.Name.Split("::")[^1] ?? "";
                Check(plain == analyzed, $"{method.Name} matched {function?.Name}");
            }
        }
        Check(methods >= 12, $"found {methods} methods");
    }

    static void TestMetricsReachAllReports()
    {
        var o = TempDir();
        Equal(0, Cli("-i", Path.Combine(_data, "lcov", "sample.info"), "-o", o, "-s", Path.Combine(Path.GetDirectoryName(_data)!, "..", "sample", "src"), "-x", "/usr/*", "--markdown"), "run");

        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("| Cyclomatic complexity after McCabe (CCM) | 23 |", StringComparison.Ordinal), "project complexity: 6 + 9 + 5 + 3");
        Check(md.Contains("| Functions | 12 |", StringComparison.Ordinal), "project functions");
        Check(md.Contains("| Highest CCM in a function | 4 |", StringComparison.Ordinal), "highest");
        Check(md.Contains("| Files analyzed | 4 of 4 |", StringComparison.Ordinal), "files analyzed");
        Check(md.Contains("| `demo::Tokenizer::split` | `tokenizer.cpp` | 10 |", StringComparison.Ordinal), "function row");
        Check(md.Contains("## Functions", StringComparison.Ordinal), "function table");

        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(index.Contains("Riskiest functions", StringComparison.Ordinal), "riskiest functions");
        Check(index.Contains("Lines of code</dt>", StringComparison.Ordinal), "project LOC");
        Check(index.Contains("Complexity (CCM)</dt><dd class=\"kpi-value\">23</dd>", StringComparison.Ordinal), "project complexity");

        var page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("Complexity (CCM)</dt>", StringComparison.Ordinal), "file level");
        Check(page.Contains("demo::Stack::clear", StringComparison.Ordinal), "a template never instantiated is listed");
        Check(page.Contains("No method data", StringComparison.Ordinal), "and marked as unknown to coverage");

        // CRAP appears wherever LOC and CCM do, each time with its legend nearby.
        Check(md.Contains("| Highest CRAP score in a function |", StringComparison.Ordinal), "CRAP in the Markdown totals");
        Check(md.Contains("| LOC | CCM | CRAP |", StringComparison.Ordinal), "in the Markdown file table");
        Check(md.Contains("| LOC | CCM | CRAP | Line coverage | Branch coverage |", StringComparison.Ordinal), "in the Markdown function table");
        Check(md.Split(MarkdownReport.CrapLegend).Length - 1 >= 2, "with the legend after the tables");
        Check(index.Contains("Highest CRAP score</dt>", StringComparison.Ordinal), "in the summary's key figures");
        Check(index.Contains("<th>CRAP</th><th>CCM</th>", StringComparison.Ordinal), "in Riskiest functions");
        Check(index.Contains("<button type=\"button\">CRAP</button>", StringComparison.Ordinal), "in the file table");
        Check(page.Contains("<th>CCM</th><th>CRAP</th>", StringComparison.Ordinal), "in the function table of a file page");
        foreach (var html in Directory.GetFiles(o, "*.html").Select(File.ReadAllText))
            Check(html.Contains("CRAP = CCM² × (1 − line coverage)³ + CCM", StringComparison.Ordinal), "every page explains CRAP");
    }

    static void TestMetricsWithoutSourceAreNa()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nFN:1,f\nFNDA:1,f\nDA:1,1\nend_of_record\n");
        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "--markdown"), "run");

        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("| Lines of code | n/a |", StringComparison.Ordinal), "LOC n/a, not 0");
        Check(md.Contains("| Cyclomatic complexity after McCabe (CCM) | n/a |", StringComparison.Ordinal), "complexity n/a, not 0");
        Check(md.Contains("| Highest CRAP score in a function | n/a |", StringComparison.Ordinal), "CRAP n/a, not 0");
        Check(md.Contains("| Files analyzed | 0 of 1 |", StringComparison.Ordinal), "files analyzed");
        Check(!md.Contains("## Functions", StringComparison.Ordinal), "no empty table");

        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(index.Contains("Complexity (CCM)</dt><dd class=\"kpi-value na\">n/a</dd>", StringComparison.Ordinal), "n/a in the report");
        Check(index.Contains("Needs source code", StringComparison.Ordinal), "and it says why");
        Check(!index.Contains("Riskiest functions", StringComparison.Ordinal), "no empty list");
    }

    static void TestFunctionNamesAreEscaped()
    {
        // Operator names are the one way markup characters reach a function name. The functions are uncovered, so
        // they appear in Riskiest functions as well.
        var dir = Directory.CreateDirectory(Path.Combine(TempDir(), "src")).FullName;
        File.WriteAllText(Path.Combine(dir, "a.c"), "bool operator<!--(int a) {\n return a;\n}\nbool operator|(int a) {\n return a;\n}\n");
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/q/src/a.c\nDA:2,0\nDA:5,0\nend_of_record\n");
        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-s", dir, "--markdown"), "run");

        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(index.Contains("Riskiest functions", StringComparison.Ordinal), "the names reach the summary");
        Check(index.Contains("operator&lt;!--", StringComparison.Ordinal), "escaped in the report");
        foreach (var page in Directory.GetFiles(o, "*.html"))
            Check(!File.ReadAllText(page).Contains("operator<", StringComparison.Ordinal), "no raw markup from a name");

        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("`operator\\|`", StringComparison.Ordinal), "a pipe in a name cannot split a Markdown cell");
        Check(!md.Contains("\\\\", StringComparison.Ordinal), "and no backslash can undo the escape");
    }

    // ---- lcov ----

    const string Lcov = """
        TN:
        SF:/build/src/a.c
        FN:3,clamp
        FN:10,_Z3barv
        FNDA:2,clamp
        FNDA:0,_Z3barv
        FNF:2
        FNH:1
        DA:3,2
        DA:4,1
        DA:10,0
        BRDA:4,0,0,1
        BRDA:4,0,1,0
        BRDA:10,0,0,-
        BRF:3
        BRH:1
        LF:3
        LH:2
        end_of_record
        """;

    static void TestLcov()
    {
        var f = Single(Parsers.ParseLcov(Lcov, []));
        Equal("/build/src/a.c", f.Path, "path");
        Equal(new Counts(2, 3), f.LineCounts, "lines");
        Equal(new Counts(1, 3), f.BranchCounts, "branches, '-' counts as not taken");
        Equal(new Counts(1, 2), f.MethodCounts, "methods");
        Check(f.Methods.ContainsKey("bar()"), "mangled name is demangled");
        Check(f.HasBranchData && f.HasMethodData, "data flags");
    }

    static void TestLcovWithoutBranchAndMethodData()
    {
        // lcov writes BRF:0 and FNF:0 when the data is disabled. That means "no data", not "0 %".
        var f = Single(Parsers.ParseLcov("SF:a.c\nFNF:0\nFNH:0\nDA:1,1\nBRF:0\nBRH:0\nend_of_record\n", []));
        Check(!f.HasBranchData, "no branch data");
        Check(!f.HasMethodData, "no method data");
        Equal(null, f.BranchCounts.Percent, "branch percent");
    }

    static void TestLcovNewerFunctionRecords()
    {
        var f = Single(Parsers.ParseLcov("SF:a.c\nFNL:0,5,9\nFNA:0,3,foo\nFN:20,25,bar, baz\nFNDA:1,bar, baz\nDA:5,3\nDA:9,0\nend_of_record\n", []));
        Equal(5, f.Methods["foo"].Line, "FNL start");
        Equal(9, f.Methods["foo"].EndLine, "FNL end");
        Equal(3L, f.Methods["foo"].Hits, "FNA hits");
        Equal(25, f.Methods["bar, baz"].EndLine, "comma in name");
        Check(!f.IsFullyCovered(f.Methods["foo"]), "line 9 is inside foo and uncovered");
    }

    static void TestLcovMalformedLines()
    {
        var warnings = new List<string>();
        var f = Single(Parsers.ParseLcov("SF:a.c\nDA:abc,1\nDA:-1,1\nDA:99999999999,1\nDA:1,-5\nBRDA:1\nFN:x\nDA:2,1\nend_of_record\nDA:5,5\n", warnings));
        Equal(1, f.Lines.Count, "only the valid line remains");
        Equal(1, warnings.Count, "one warning");
    }

    static void TestLcovWarnsAboutIncompleteInput()
    {
        var warnings = new List<string>();
        Parsers.ParseLcov("SF:a.c\nDA:1,1\n", warnings);
        Check(warnings.Any(w => w.Contains("end_of_record", StringComparison.Ordinal)), "cut-off file");

        warnings.Clear();
        Parsers.ParseLcov("SF:a.c\nDA:1,1\nSF:b.c\nDA:1,1\nend_of_record\n", warnings);
        Check(warnings.Any(w => w.Contains("end_of_record", StringComparison.Ordinal)), "record without an end");

        warnings.Clear();
        Parsers.ParseLcov("SF:a.c\nDA:1,1\nLF:100\nLH:1\nend_of_record\n", warnings);
        Check(warnings.Any(w => w.Contains("line count", StringComparison.Ordinal)), "LF:100 with one DA line would otherwise read as 100 %");

        warnings.Clear();
        Parsers.ParseLcov("SF:a.c\nDA:1,1\nLF:1\nLH:1\nend_of_record\n", warnings);
        Equal(0, warnings.Count, "complete record");
    }

    // ---- gcov ----

    const string Gcov = """
                -:    0:Source:src/stack.hpp
                -:    0:Runs:1
                -:    1:#pragma once
                3:    2:void push() {}
        ------------------
        _Z1aIiEvv:
        function _Z1aIiEvv called 1 returned 100% blocks executed 100%
                1:    2:void push() {}
        branch  0 taken 1 (fallthrough)
        branch  1 taken 0
        ------------------
        _Z1aIcEvv:
        function _Z1aIcEvv called 2 returned 100% blocks executed 100%
                2:    2:void push() {}
        ------------------
        function foo called 0 returned 0% blocks executed 0%
            #####:    3:int foo() {
        branch  0 never executed
        branch  1 taken 50%
            =====:    4:  throw 1;
               4*:    5:  x();
             1.2k:    6:  y();
                -:    7:}
        """;

    static void TestGcov()
    {
        var f = Single(Parsers.ParseGcov(Gcov, []));
        Equal("src/stack.hpp", f.Path, "path");
        Equal(3L, f.Lines[2], "template lines count once: the aggregate, not the sections");
        Equal(0L, f.Lines[3], "#####");
        Equal(0L, f.Lines[4], "=====");
        Equal(4L, f.Lines[5], "count with *");
        Equal(1200L, f.Lines[6], "count with k");
        Check(!f.Lines.ContainsKey(1), "'-' is not coverable");
        Equal(new Counts(2, 4), f.BranchCounts, "branches, including section and percent forms");
        Equal(3, f.Methods.Count, "instantiations are separate methods");
        Equal(2, f.Methods["void a<int>()"].Line, "function line comes from the next line");
        Equal(3, f.Methods["foo"].Line, "function line");
        Equal("void push() {}", f.Source?[1], "source comes from the aggregate");
        Equal("  y();", f.Source?[5], "source");
    }

    static void TestGcovWithoutBranchOption()
    {
        var f = Single(Parsers.ParseGcov("        -:    0:Source:a.c\n        1:    1:int x;\n", []));
        Check(!f.HasBranchData, "no branch data");
        Check(!f.HasMethodData, "no method data");
        Equal(new Counts(1, 1), f.LineCounts, "lines");
    }

    static void TestGcovBranchesKnownToBeAbsent()
    {
        // gcov ran with -b, and the file genuinely has no branches.
        var f = Single(Parsers.ParseGcov("        -:    0:Source:a.c\nfunction f called 1 returned 100% blocks executed 100%\n        1:    1:void f() {}\n", []));
        Check(f.HasBranchData, "function records prove -b");
        Equal(null, f.BranchCounts.Percent, "percent stays undefined");
    }

    // Regression: 52 bytes once requested ten million report rows, and a single gap once dropped the rest of a file,
    // which then read as 100 %.
    static void TestGcovLineJumpIsAnError()
    {
        foreach (var text in new[]
        {
            "        -:    0:Source:a.c\n        1:    1:ok\n        1: 2000000:x\n",
            "        -:    0:Source:a.c\n        1: 10000000:above the limit\n",
            "        -:    0:Source:a.c\n        1:    1:ok\n    #####:    3:gap\n    #####:    4:missed\n",
        })
        {
            var threw = false;
            try
            {
                Parsers.ParseGcov(text, []);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            Check(threw, "a jump in the line numbers is refused");
        }
    }

    static void TestGcovStreamWithSeveralFiles()
    {
        var files = Parsers.ParseGcov("        -:    0:Source:a.c\n        1:    1:in_a\n        -:    0:Source:b.c\n        0:    1:in_b\n        1:    2:in_b2\n", []);
        Equal(2, files.Count, "each Source: line starts a file");
        Equal("in_a", files[0].Source?[0], "first file");
        Equal("in_b2", files[1].Source?[1], "second file");
        Equal(new Counts(1, 2), files[1].LineCounts, "counts stay with their file");
    }

    // gcov -H abbreviations go through double parsing, which also knows NaN and infinity. "-Infinityk" once became a
    // negative count that overflowed into a covered line, without a warning.
    static void TestGcovUnreadableCountsAreReported()
    {
        var warnings = new List<string>();
        var f = Single(Parsers.ParseGcov("        -:    0:Source:/p/a.c\n-Infinityk:    1:int a;\n     NaNk:    2:int b;\nInfinityk:    3:int c;\n"
            + "      x1:    4:int d;\n     1.5k:    5:int e;\nbranch  0 taken NaN%\nbranch  1 taken 1\n", warnings));
        Check(!f.Lines.ContainsKey(1) && !f.Lines.ContainsKey(2) && !f.Lines.ContainsKey(3) && !f.Lines.ContainsKey(4), "an unreadable count is no count");
        Equal(1500L, f.Lines[5], "an abbreviated count still works");
        Equal(5, f.Source?.Length, "every line keeps its source text, so nothing shifts");
        Equal(new Counts(1, 1), f.BranchCounts, "a branch with an unreadable count is left out");
        Equal("Skipped 5 malformed gcov line(s).", string.Join("; ", warnings), "and each one is reported");

        var g = Single(Parsers.ParseGcov("        -:    0:Source:/p/a.c\n        1:    1:ok\n     NaNk:    2:last line\n", warnings));
        Equal(2, g.Source?.Length, "an unreadable count on the last line no longer drops the line");
        Equal(InputFormat.Unknown, Parsers.Detect("     NaNk:    0:Source:/p/a.c\n"), "the header needs a readable count to count as gcov");
    }

    // ---- Cobertura ----

    const string Cobertura = """
        <?xml version="1.0"?>
        <coverage line-rate="0.5" branch-rate="0.5">
          <sources><source>/build/proj</source></sources>
          <packages><package name="p"><classes>
            <class name="a" filename="src/a.cpp" line-rate="0.5">
              <methods>
                <method name="_Z3foov" signature="" line-rate="1"><lines><line number="1" hits="4"/></lines></method>
                <method name="dead" signature="" line-rate="0"><lines><line number="9" hits="0"/></lines></method>
                <method name="noLines" signature="" line-rate="1"/>
              </methods>
              <lines>
                <line number="1" hits="4" branch="false"/>
                <line number="2" hits="1" branch="true" condition-coverage="50% (1/2)"/>
                <line number="9" hits="0" branch="false"/>
              </lines>
            </class>
          </classes></package></packages>
        </coverage>
        """;

    static void TestCobertura()
    {
        Equal(InputFormat.Cobertura, Parsers.Detect(Cobertura), "detected");
        var f = Single(Parsers.ParseCobertura(Cobertura, []));
        Equal("/build/proj/src/a.cpp", f.Path, "relative filename joined with source");
        Equal(new Counts(2, 3), f.LineCounts, "method lines are not counted twice");
        Equal(4L, f.Lines[1], "hits");
        Equal(new Counts(1, 2), f.BranchCounts, "branches from condition-coverage");
        Equal(new Counts(2, 3), f.MethodCounts, "methods");
        Equal(1, f.Methods["foo()"].Line, "method start");
        Equal(0, f.Methods["noLines"].Line, "method without lines");
    }

    static void TestCoberturaWithoutBranchAndMethodData()
    {
        const string xml = "<coverage><packages><package><classes><class filename=\"a.c\"><methods/><lines><line number=\"1\" hits=\"1\" branch=\"false\"/></lines></class></classes></package></packages></coverage>";
        var f = Single(Parsers.ParseCobertura(xml, []));
        Check(!f.HasBranchData, "no branch data");
        Check(!f.HasMethodData, "no method data");
        Equal(new Counts(1, 1), f.LineCounts, "lines");
    }

    static void TestCoberturaRejectsDtdAndExternalEntities()
    {
        var secret = Path.Combine(TempDir(), "secret.txt");
        File.WriteAllText(secret, "TOP-SECRET");
        var xxe = $"<?xml version=\"1.0\"?><!DOCTYPE coverage [<!ENTITY x SYSTEM \"file:///{secret.Replace('\\', '/')}\">]><coverage><packages><package><classes><class filename=\"&x;\"/></classes></package></packages></coverage>";
        var bomb = "<?xml version=\"1.0\"?><!DOCTYPE c [<!ENTITY a \"aaaa\"><!ENTITY b \"&a;&a;&a;&a;\">]><coverage>&b;</coverage>";

        foreach (var attack in new[] { xxe, bomb })
        {
            var threw = false;
            try
            {
                Parsers.ParseCobertura(attack, []);
            }
            catch (InvalidDataException e)
            {
                threw = true;
                Check(!e.Message.Contains("TOP-SECRET", StringComparison.Ordinal), "secret not in the message");
            }
            Check(threw, "entities from a DTD stay undefined");
        }
    }

    // gcovr references a DTD on the internet in every file; it must be skipped, never fetched.
    static void TestCoberturaNeverFetchesTheDtd()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var brokenDtd = Path.Combine(TempDir(), "broken.dtd");
        File.WriteAllText(brokenDtd, "<!ENTITY this is not a valid DTD and would throw if it were read");

        foreach (var system in new[] { $"http://127.0.0.1:{port}/coverage-04.dtd", "file:///" + brokenDtd.Replace('\\', '/') })
        {
            var xml = $"<?xml version=\"1.0\"?><!DOCTYPE coverage SYSTEM '{system}'><coverage><packages><package><classes><class filename=\"a.c\"><lines><line number=\"1\" hits=\"1\"/></lines></class></classes></package></packages></coverage>";
            Equal(InputFormat.Cobertura, Parsers.Detect(xml), "file with a DOCTYPE is recognized");
            Equal(new Counts(1, 1), Single(Parsers.ParseCobertura(xml, [])).LineCounts, "and parsed");
        }
        Check(!listener.Pending(), "no connection to the DTD's address");
    }

    static void TestCoberturaLimits()
    {
        var deep = "<coverage>" + string.Concat(Enumerable.Repeat("<a>", 1000)) + string.Concat(Enumerable.Repeat("</a>", 1000)) + "</coverage>";
        var threw = false;
        try
        {
            Parsers.ParseCobertura(deep, []);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }
        Check(threw, "nesting depth is limited");

        const string huge = "<coverage><packages><package><classes><class filename=\"a.c\"><lines><line number=\"1\" hits=\"1\" condition-coverage=\"1% (1/4000)\"/></lines></class></classes></package></packages></coverage>";
        var warnings = new List<string>();
        var f = Single(Parsers.ParseCobertura(huge, warnings));
        Equal(0L, f.BranchCounts.Total, "62 bytes must not allocate thousands of branches");
        Equal(1, warnings.Count, "and the dropped data is reported");
    }

    static void TestFormatDetection()
    {
        Equal(InputFormat.Lcov, Parsers.Detect("TN:\nSF:a.c\n"), "lcov with TN");
        Equal(InputFormat.Lcov, Parsers.Detect("\nSF:a.c\n"), "lcov with SF");
        Equal(InputFormat.Gcov, Parsers.Detect("        -:    0:Source:a.c\n"), "gcov");
        Equal(InputFormat.Unknown, Parsers.Detect("<project/>"), "other XML");
        Equal(InputFormat.Unknown, Parsers.Detect("hello"), "text");
        Equal(InputFormat.Unknown, Parsers.Detect(""), "empty");
    }

    // ---- merge and totals ----

    static void TestMerge()
    {
        var a = Single(Parsers.ParseLcov("SF:a.c\nFN:1,f\nFNDA:0,f\nDA:1,0\nDA:2,1\nBRDA:2,0,0,0\nBRDA:2,0,1,1\nend_of_record\n", []));
        var b = Single(Parsers.ParseLcov("SF:a.c\nFN:1,f\nFNDA:2,f\nDA:1,3\nBRDA:2,0,0,4\nBRDA:2,0,1,0\nend_of_record\n", []));
        a.Merge(b);
        Equal(3L, a.Lines[1], "hits add up");
        Equal(new Counts(2, 2), a.LineCounts, "lines");
        Equal(new Counts(2, 2), a.BranchCounts, "branches merge by position");
        Equal(2L, a.Methods["f"].Hits, "method hits add up");

        var big = new FileCoverage("x.c");
        big.AddLine(1, long.MaxValue);
        big.AddLine(1, long.MaxValue);
        Equal(long.MaxValue, big.Lines[1], "no overflow");
    }

    static void TestRelativePathsJoinAbsoluteOnes()
    {
        Equal("mnt/d/a.c", PathUtil.Normalize("../../mnt/d/a.c"), "leading .. has no meaning on another machine");
        Equal("/a/c.c", PathUtil.Normalize(@"\a\.\b\..\c.c"), "lexical cleanup");

        FileCoverage Make(string path)
        {
            var f = new FileCoverage(path);
            f.AddLine(1, 1);
            return f;
        }

        var files = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { Make("/x/src/a.c"), Make("src/a.c"), Make("/x/dup.c"), Make("/y/dup.c"), Make("dup.c"), Make("other.c") })
            files[f.Path] = f;
        PathUtil.MergeRelativeIntoAbsolute(files);

        Check(!files.ContainsKey("src/a.c"), "relative path joined");
        Equal(2L, files["/x/src/a.c"].Lines[1], "hits added");
        Check(files.ContainsKey("dup.c"), "several candidates: not guessed");
        Check(files.ContainsKey("other.c"), "no candidate: kept");
    }

    static void TestPartialDataDoesNotPullTotalsDown()
    {
        var withData = Single(Parsers.ParseLcov("SF:a.c\nFN:1,f\nFNDA:1,f\nDA:1,1\nBRDA:1,0,0,1\nBRDA:1,0,1,1\nend_of_record\n", []));
        var without = Single(Parsers.ParseLcov("SF:b.c\nDA:1,0\nend_of_record\n", []));
        var t = Report.TotalsOf([withData, without]);
        Equal(100.0, t.Branch.Percent, "branch quota only counts files with data");
        Equal(100.0, t.Method.Percent, "method quota only counts files with data");
        Equal(1, t.FilesWithBranchData, "files with branch data");
        Equal(new Counts(1, 2), t.Line, "line data still counts everywhere");

        var none = Report.TotalsOf([without]);
        Check(none.ToSnapshot().Branch == null, "no data at all is null, not 0");
    }

    static void TestPercentFormat()
    {
        Equal("n/a", Report.Percent(null), "null");
        Equal("0%", Report.Percent(0), "zero");
        Equal("99.9%", Report.Percent(99.96), "rounds down, never up to 100");
        Equal("100%", Report.Percent(100), "hundred");
        Equal("66.6%", Report.Percent(200.0 / 3), "invariant culture");
    }

    static void TestFullMethodCoverage()
    {
        var f = Single(Parsers.ParseLcov("SF:a.c\nFN:1,a\nFN:5,b\nFN:9,c\nFNDA:1,a\nFNDA:1,b\nFNDA:0,c\nDA:1,1\nDA:2,1\nDA:5,1\nDA:6,0\nDA:9,0\nend_of_record\n", []));
        Check(f.IsFullyCovered(f.Methods["a"]), "a: all lines hit");
        Check(!f.IsFullyCovered(f.Methods["b"]), "b: line 6 missed");
        Check(!f.IsFullyCovered(f.Methods["c"]), "c: never called");
        Equal(new Counts(1, 3), f.FullMethodCounts, "full methods");
    }

    // Regression: this used to be quadratic, and 6 MB of input would have taken twenty minutes.
    static void TestHostileCountsStayFast()
    {
        var f = new FileCoverage("/p/a.c");
        for (var i = 1; i <= 200_000; i++)
        {
            f.AddLine(i, i % 2);
            f.AddMethod("m" + i, i, 0, 1);
        }
        var timer = Stopwatch.StartNew();
        Equal(new Counts(100_000, 200_000), f.FullMethodCounts, "every second method is fully covered");
        Check(timer.ElapsedMilliseconds < 2000, $"full method coverage took {timer.ElapsedMilliseconds} ms");

        var files = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 50_000; i++)
        {
            var a = new FileCoverage($"/root/d{i}/x.c");
            var r = new FileCoverage($"d{i}/x.c");
            files[a.Path] = a;
            files[r.Path] = r;
        }
        timer.Restart();
        PathUtil.MergeRelativeIntoAbsolute(files);
        Equal(50_000, files.Count, "all relative paths joined");
        Check(timer.ElapsedMilliseconds < 3000, $"merging paths took {timer.ElapsedMilliseconds} ms");

        // One path spelled in 100,000 ways, each of which would be another place to look for the source.
        var target = new FileCoverage("/p/a.c");
        timer.Restart();
        for (var i = 0; i < 100_000; i++)
        {
            var spelling = new FileCoverage($"/p/x{i}/../a.c");
            spelling.Origins.Add((spelling.RawPath, @"C:\cov"));
            target.Merge(spelling);
        }
        Check(timer.ElapsedMilliseconds < 2000, $"merging spellings took {timer.ElapsedMilliseconds} ms");
        Equal(FileCoverage.MaxOrigins, target.Origins.Count, "only the first spellings are kept");
    }

    static void TestInvisibleCharacters()
    {
        const string rlo = "‮";
        Equal("/p/ab.c", new FileCoverage($"/p/a{rlo}b.c").Path, "bidi override cannot fake a path");
        var f = new FileCoverage("/p/a.c");
        f.AddMethod($"f{rlo}oo", 1, 0, 1);
        Check(f.Methods.ContainsKey("foo"), "nor a method name");

        f.AddLine(1, 1);
        f.Source = [$"int a; {rlo}// hidden"];
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        var page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));
        Check(page.Contains("[U+202E]", StringComparison.Ordinal), "source shows it");
        Check(!page.Contains(rlo, StringComparison.Ordinal), "and does not apply it");

        // Tag characters lie above U+FFFF, where a per-char check would only see surrogates.
        const string tag = "\U000E0041";
        Equal("/p/ab.c", new FileCoverage($"/p/a{tag}b.c").Path, "astral format character in a path");
        Equal("x[U+E0041]y", PathUtil.RevealInvisible($"x{tag}y"), "and shown in source");
        Equal("\U0001F600 ü", PathUtil.StripControl("\U0001F600 ü"), "ordinary text stays");
    }

    static void TestPageNamesFollowFileIdentity()
    {
        Equal(HtmlReport.PageName("/p/A.c"), HtmlReport.PageName("/p/a.c"), "case variants are one file");
        Check(HtmlReport.PageName("/p/k.c") != HtmlReport.PageName("/p/K.c"), "the Kelvin sign is another file");
    }

    // ---- exclude ----

    static void TestExcludePatterns()
    {
        Check(Program.IsExcluded("/usr/include/stdio.h", ["/usr/*"]), "prefix");
        Check(Program.IsExcluded("/a/b/test/c/d.c", ["*/test/*"]), "* spans folders");
        Check(Program.IsExcluded("C:/Src/Foo.H", ["*.h"]), "case is ignored");
        Check(Program.IsExcluded("C:/src/a.c", [@"C:\src\*"]), "backslash equals slash");
        Check(Program.IsExcluded("/a/x1.c", ["*/x?.c"]), "? is one character");
        Check(!Program.IsExcluded("/a/x12.c", ["*/x?.c"]), "? is exactly one character");
        Check(!Program.IsExcluded("/a/test_a.c", ["test_*.c"]), "patterns match the full path");
        Check(!Program.IsExcluded("/a/b.c", []), "no patterns");
    }

    // ---- source lookup ----

    static void TestSourceResolver()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "src", "x"));
        Directory.CreateDirectory(Path.Combine(root, "src", "y"));
        File.WriteAllText(Path.Combine(root, "src", "x", "a.c"), "int x;\r\nint y;\r\n");
        File.WriteAllText(Path.Combine(root, "src", "x", "dup.c"), "x");
        File.WriteAllText(Path.Combine(root, "src", "y", "dup.c"), "y");
        File.WriteAllText(Path.Combine(root, "secret.txt"), "TOP-SECRET");
        File.WriteAllText(Path.Combine(root, "id_rsa"), "TOP-SECRET");

        var resolver = new SourceResolver([Path.Combine(root, "src")]);
        var found = resolver.Find("/mnt/d/other/place/src/x/a.c", out var ambiguous);
        Equal(Path.Combine(root, "src", "x", "a.c"), found, "tail match across machines");
        Check(!ambiguous, "unique");

        Check(resolver.Find("/q/dup.c", out ambiguous) == null && ambiguous, "ties are not guessed");
        Equal(Path.Combine(root, "src", "y", "dup.c"), resolver.Find("/q/y/dup.c", out _), "longer tail decides");

        // Attacks: files outside the roots, or files that are not C/C++ source.
        Check(resolver.Find(Path.Combine(root, "secret.txt").Replace('\\', '/'), out _) == null, "file outside the root");
        Check(new SourceResolver([root]).Find("secret.txt", out _) == null, "non C/C++ file inside the root");
        Check(new SourceResolver([root]).Find("/root/.ssh/id_rsa", out _) == null, "extensionless file");
        Check(resolver.Find("../../secret.txt", out _) == null, "traversal");

        Equal("int y;", SourceResolver.Load(found!)?[1], "CRLF removed");
        Equal(2, SourceResolver.Load(found!)?.Length, "no phantom last line");
    }

    static void TestSourceEncodingFallback()
    {
        var file = Path.Combine(TempDir(), "latin.c");
        File.WriteAllBytes(file, [0x2F, 0x2F, 0xFC]);
        Equal("//\u00FC", SourceResolver.Load(file)?[0], "Latin-1 fallback");
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, 0x2F, 0xC3, 0xBC]);
        Equal("/\u00FC", SourceResolver.Load(file)?[0], "UTF-8 with BOM");
    }

    // ---- reports ----

    static Report ReportOf(params FileCoverage[] files) => new()
    {
        Files = files.ToList(),
        GeneratedAt = DateTimeOffset.Now,
        Inputs = ["x"],
    };

    const string Xss = "<script>alert(1)</script>\"'><img src=x onerror=alert(2)>";

    static void TestHtmlEscapesEverything()
    {
        var f = new FileCoverage($"/p/{Xss}.c") { HasMethodData = true, HasBranchData = true };
        f.AddLine(1, 1);
        f.AddMethod(Xss, 1, 0, 1);
        f.Source = [Xss];
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);

        foreach (var page in Directory.GetFiles(out_, "*.html"))
        {
            var html = File.ReadAllText(page);
            Check(!html.Contains("<img src=x", StringComparison.Ordinal), $"raw markup in {Path.GetFileName(page)}");
            Check(!html.Contains("<script>alert", StringComparison.Ordinal), $"raw script in {Path.GetFileName(page)}");
            Check(!html.Contains("onerror=alert(2)>", StringComparison.Ordinal), $"attribute breakout in {Path.GetFileName(page)}");
        }

        // No < or > here: only escaping the quote keeps this from opening an event handler.
        const string quotes = "/p/a\" onmouseover=\"alert(3).c";
        var g = new FileCoverage(quotes);
        g.AddLine(1, 1);
        var out2 = TempDir();
        HtmlReport.Write(ReportOf(g), out2);
        foreach (var page in Directory.GetFiles(out2, "*.html"))
            Check(!File.ReadAllText(page).Contains("\" onmouseover=\"", StringComparison.Ordinal), "quote in a path breaks out of an attribute");
    }

    static void TestCspMatchesWhatIsEmbedded()
    {
        var f = new FileCoverage("/p/a.c");
        f.AddLine(1, 1);
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        var index = File.ReadAllText(Path.Combine(out_, "index.html"));
        var page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));

        static string Hash(string content) => Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        foreach (var html in new[] { index, page })
        {
            var style = Regex.Match(html, "<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;
            Check(html.Contains($"style-src 'sha256-{Hash(style)}'", StringComparison.Ordinal), "style hash matches");
            Check(!style.Contains('\r') , "no CR, browsers would hash it differently");

            foreach (Match href in Regex.Matches(html, "href=\"([^\"]*)\""))
                Check(Regex.IsMatch(href.Groups[1].Value, @"^(index\.html|f_[0-9a-f]{16}\.html(#L\d+)?|#L\d+)$"), $"link leaves the report: {href.Groups[1].Value}");
        }

        var script = Regex.Match(index, "<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        Check(index.Contains($"script-src 'sha256-{Hash(script)}'", StringComparison.Ordinal), "script hash matches");
        Check(!page.Contains("<script", StringComparison.Ordinal), "file pages need no script");
    }

    static void TestHtmlHasNoExternalReferences()
    {
        var f = new FileCoverage("/p/a.c");
        f.AddLine(1, 1);
        f.Source = ["int a;"];
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);

        foreach (var page in Directory.GetFiles(out_, "*.html"))
        {
            var html = File.ReadAllText(page);
            Check(!html.Contains("http://", StringComparison.OrdinalIgnoreCase), "no http:// URL");
            Check(!html.Contains("https://", StringComparison.OrdinalIgnoreCase), "no https:// URL");
            Check(!html.Contains("//cdn", StringComparison.OrdinalIgnoreCase), "no CDN");
            Check(!html.Contains("url(", StringComparison.OrdinalIgnoreCase), "CSS loads nothing");
            Check(!html.Contains("@import", StringComparison.OrdinalIgnoreCase), "CSS imports nothing");
            Check(!html.Contains("@font-face", StringComparison.OrdinalIgnoreCase), "only system fonts");
            Check(!html.Contains("src=\"", StringComparison.OrdinalIgnoreCase), "nothing loaded from a file or URL");
            Check(!html.Contains("style=\"", StringComparison.OrdinalIgnoreCase), "inline style attributes would break the CSP");
            Check(html.Contains("default-src 'none'", StringComparison.Ordinal), "CSP present");
            Check(html.Contains("form-action 'none'", StringComparison.Ordinal), "forms locked");
            Check(!html.Contains("unsafe-inline", StringComparison.Ordinal), "no unsafe-inline");
        }
        var js = File.ReadAllText(Path.Combine(out_, "index.html"));
        foreach (var forbidden in new[] { "fetch(", "XMLHttpRequest", "WebSocket", "sendBeacon", "eval(", "innerHTML", "document.write", "localStorage", "EventSource", "location" })
            Check(!js.Contains(forbidden, StringComparison.Ordinal), $"report.js must not use {forbidden}");
    }

    static void TestPageNamesCannotEscapeOutputFolder()
    {
        var f = new FileCoverage("../../../../evil.c");
        f.AddLine(1, 1);
        var out_ = Path.Combine(TempDir(), "a", "b");
        HtmlReport.Write(ReportOf(f), out_);
        var name = HtmlReport.PageName(f.Path);
        Check(name.StartsWith("f_", StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.'), "page name is a plain hash");
        Check(File.Exists(Path.Combine(out_, name)), "page inside the output folder");
        Check(!File.Exists(Path.Combine(out_, "..", "evil.c.html")), "nothing outside");
    }

    static void TestStalePagesAreRemovedForeignFilesKept()
    {
        var out_ = TempDir();
        var old = new FileCoverage("/p/old.c");
        old.AddLine(1, 1);
        old.Source = ["int secret;"];
        HtmlReport.Write(ReportOf(old), out_);
        var oldPage = Path.Combine(out_, HtmlReport.PageName(old.Path));
        Check(File.Exists(oldPage), "first run wrote the page");

        var foreign = Path.Combine(out_, "f_mine.html");
        var notes = Path.Combine(out_, "notes.txt");
        var legacy = Path.Combine(out_, "f_0123456789abcdef.html");
        File.WriteAllText(foreign, "<html>my own page</html>");
        File.WriteAllText(notes, "hello");
        File.WriteAllText(legacy, "<!doctype html><!-- hw-coverage report -->\n<p>int secret;</p>");

        var current = new FileCoverage("/p/new.c");
        current.AddLine(1, 1);
        HtmlReport.Write(ReportOf(current), out_);

        Check(!File.Exists(oldPage), "stale page with old source code removed");
        Check(!File.Exists(legacy), "a page written under the former name hw-coverage is recognized too");
        Check(File.Exists(foreign), "foreign page kept");
        Check(File.Exists(notes), "foreign file kept");
        Check(File.Exists(Path.Combine(out_, HtmlReport.PageName(current.Path))), "new page written");
    }

    static void TestHtmlShowsMissingDataAsNa()
    {
        var f = new FileCoverage("/p/a.c");
        f.AddLine(1, 1);
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        var index = File.ReadAllText(Path.Combine(out_, "index.html"));
        Check(index.Contains("Branch coverage</dt><dd class=\"kpi-value na\">n/a</dd>", StringComparison.Ordinal), "summary shows n/a");
        Check(index.Contains("Method coverage</dt><dd class=\"kpi-value na\">n/a</dd>", StringComparison.Ordinal), "summary shows n/a");
        Check(index.Contains("data-v=\"-1\" class=\"na\">n/a", StringComparison.Ordinal), "table shows n/a");

        var page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));
        Check(page.Contains("No method data", StringComparison.Ordinal), "method section explains itself");
        Check(page.Contains("Source not shown", StringComparison.Ordinal), "missing source explains itself");
    }

    // Without source code, a file page shows the key figures and the function table, but no rows of bare line numbers.
    static void TestFilePageWithoutSourceHasNoListing()
    {
        var f = Single(Parsers.ParseLcov("SF:/p/a.c\nFN:2,f\nFNDA:1,f\nDA:2,1\nDA:3,0\nBRDA:3,0,0,1\nBRDA:3,0,1,0\nend_of_record\n", []));
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        var page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));
        Check(page.Contains("<p class=\"warn\">Source not shown: ", StringComparison.Ordinal), "the reason is shown");
        Check(!page.Contains("class=\"source\"", StringComparison.Ordinal), "no source table");
        Check(!page.Contains("id=\"L", StringComparison.Ordinal), "no line rows");
        Check(page.Contains("Line coverage</dt><dd class=\"kpi-value\">50%</dd>", StringComparison.Ordinal), "key figures stay");
        Check(page.Contains("<td class=\"left fn\">f</td>", StringComparison.Ordinal), "the function is listed, without a link to a missing line");
        Check(!page.Contains("href=\"#L", StringComparison.Ordinal), "no link points into the page");

        f.Source = ["", "void f() {", "  if (x) {}", "}"];
        HtmlReport.Write(ReportOf(f), out_);
        page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));
        Check(page.Contains("class=\"source\"", StringComparison.Ordinal) && page.Contains("<a href=\"#L2\">f</a>", StringComparison.Ordinal), "with source, the listing and the link are back");
    }

    static void TestPartialDataNote()
    {
        var a = Single(Parsers.ParseLcov("SF:/p/a.c\nDA:1,1\nBRDA:1,0,0,1\nend_of_record\n", []));
        var b = Single(Parsers.ParseLcov("SF:/p/b.c\nDA:1,1\nend_of_record\n", []));
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(a, b), out_);
        var index = File.ReadAllText(Path.Combine(out_, "index.html"));
        Check(index.Contains("Branch data: 1 of 2 files", StringComparison.Ordinal), "note about partial data");
    }

    static void TestSourceRowsMarkState()
    {
        var f = Single(Parsers.ParseLcov("SF:/p/a.c\nDA:1,1\nDA:2,0\nDA:3,1\nBRDA:3,0,0,1\nBRDA:3,0,1,0\nDA:99,1\nend_of_record\n", []));
        f.Source = ["one", "two", "three", "four"];
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        var page = File.ReadAllText(Path.Combine(out_, HtmlReport.PageName(f.Path)));
        Check(page.Contains("<tr class=\"hit\" id=\"L1\">", StringComparison.Ordinal), "covered");
        Check(page.Contains("<tr class=\"miss\" id=\"L2\">", StringComparison.Ordinal), "uncovered");
        Check(page.Contains("<tr class=\"part\" id=\"L3\">", StringComparison.Ordinal), "partly covered");
        Check(page.Contains("<tr class=\"none\" id=\"L4\">", StringComparison.Ordinal), "not coverable");
        Check(page.Contains("1/2", StringComparison.Ordinal), "branch marker as text");
        Check(page.Contains("id=\"L99\"", StringComparison.Ordinal) && page.Contains("may have changed", StringComparison.Ordinal), "line beyond the source");
    }

    static void TestMarkdown()
    {
        var a = Single(Parsers.ParseLcov("SF:/p/[x](http:evil)|<b>@someone.c\nDA:1,1\nend_of_record\n", []));
        var md = MarkdownReport.Render(ReportOf(a));
        Check(md.Contains("| Line coverage | 1 | 1 | 100% |", StringComparison.Ordinal), "summary");
        Check(md.Contains("| Branch coverage | n/a | n/a | n/a |", StringComparison.Ordinal), "missing data");
        Check(md.Contains("`[x](http:evil)\\|<b>@someone.c`", StringComparison.Ordinal), "links, HTML and mentions stay inert in a code span, pipes are escaped");

        Equal("``a`b``", MarkdownReport.CodeSpan("a`b"), "fence longer than the backticks inside");
        Equal("`` `a ``", MarkdownReport.CodeSpan("`a"), "leading backtick is padded");

        Check(md.StartsWith(HtmlReport.Marker + "\n\n# Coverage Report\n", StringComparison.Ordinal), "the product name is the heading without a title");
        Check(md.Contains($"Generated by Coverage Report {Program.Version} on ", StringComparison.Ordinal), "name and version");
        Check(md.Contains("· Copyright © 2026 Hanno Witt.", StringComparison.Ordinal), "and the copyright notice");

        var titled = new Report { Files = [a], GeneratedAt = DateTimeOffset.Now, Inputs = ["x"], Title = "Firmware <b>[v2]</b> | *main*" };
        Check(MarkdownReport.Render(titled).Contains("\n# Firmware \\<b\\>\\[v2\\]\\<\\/b\\> \\| \\*main\\*\n", StringComparison.Ordinal), "a title is the heading, rendered literally");
    }

    // ---- history ----

    static void TestHistoryRoundTrip()
    {
        var dir = TempDir();
        var a = Single(Parsers.ParseLcov("SF:/p/a.c\nDA:1,1\nDA:2,0\nend_of_record\n", []));
        History.Save(dir, ReportOf(a));
        var b = Single(Parsers.ParseLcov("SF:/p/a.c\nFN:1,f\nFNDA:1,f\nDA:1,1\nDA:2,1\nBRDA:1,0,0,1\nend_of_record\n", []));
        History.Save(dir, ReportOf(b));
        File.WriteAllText(Path.Combine(dir, "hw-coverage-broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "hw-coverage-wrong.json"), "{\"version\":1,\"timestamp\":\"2020-01-01T00:00:00Z\",\"line\":{\"covered\":9,\"total\":1},\"files\":[]}");
        File.WriteAllText(Path.Combine(dir, "unrelated.json"), "{ not json");

        var warnings = new List<string>();
        var runs = History.Load(dir, warnings);
        Equal(2, runs.Count, "both good runs");
        Equal(2, warnings.Count, "both broken files are reported");
        Equal(50.0, runs[0].Totals.Line?.Percent, "first run");
        Check(runs[0].Totals.Branch == null, "no branch data stays null");
        Equal(100.0, runs[1].Totals.Branch?.Percent, "second run");
        Check(runs[1].Files.ContainsKey("/p/a.c"), "per-file data");

        var saved = Directory.GetFiles(dir, "coveragereport-2*.json");
        Equal(2, saved.Length, "runs are saved under the new name");
        Check(!string.Concat(saved.Select(File.ReadAllText)).Contains("int ", StringComparison.Ordinal), "no source code in history");
    }

    // Runs saved under the former name hw-coverage stay in the chart, ordered by time with the new ones.
    static void TestHistoryReadsRunsOfTheFormerName()
    {
        var dir = TempDir();
        static string Run(DateTimeOffset time) => $"{{\"version\":1,\"timestamp\":\"{time:O}\",\"line\":{{\"covered\":1,\"total\":2}},\"branch\":null,\"method\":null,\"files\":[]}}";
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < History.MaxRuns; i++)
            File.WriteAllText(Path.Combine(dir, $"coveragereport-{start.AddDays(i):yyyyMMdd}T000000000Z-0000000{i % 10}.json"), Run(start.AddDays(i)));
        var newest = start.AddDays(History.MaxRuns);
        File.WriteAllText(Path.Combine(dir, $"hw-coverage-{newest:yyyyMMdd}T000000000Z-00000000.json"), Run(newest));

        var runs = History.Load(dir, []);
        Equal(History.MaxRuns, runs.Count, "cap");
        Equal(newest, runs[^1].Time, "the newest run counts, whatever its name");
        Equal(start.AddDays(1), runs[0].Time, "and the oldest new-style run is the one left out");
    }

    static void TestHistoryKeepsLastRuns()
    {
        var dir = TempDir();
        for (var i = 0; i < History.MaxRuns + 5; i++)
        {
            var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i);
            File.WriteAllText(Path.Combine(dir, $"hw-coverage-{time:yyyyMMdd}.json"), $"{{\"version\":1,\"timestamp\":\"{time:O}\",\"line\":{{\"covered\":1,\"total\":1}},\"branch\":null,\"method\":null,\"files\":[]}}");
        }
        var runs = History.Load(dir, []);
        Equal(History.MaxRuns, runs.Count, "cap");
        Check(runs[0].Time < runs[^1].Time, "oldest first");
        Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(History.MaxRuns + 4), runs[^1].Time, "latest kept");
        Equal(History.MaxRuns + 5, Directory.GetFiles(dir).Length, "nothing deleted");
    }

    static void TestHistoryRefusesForgedFiles()
    {
        var dir = TempDir();
        static string Run(int year) => $"{{\"version\":1,\"timestamp\":\"{year}-01-01T00:00:00Z\",\"line\":{{\"covered\":1,\"total\":1}},\"branch\":null,\"method\":null,\"files\":[]}}";
        File.WriteAllText(Path.Combine(dir, "hw-coverage-2020.json"), Run(2020));
        for (var i = 0; i < 50; i++)
            File.WriteAllText(Path.Combine(dir, $"hw-coverage-0-{i}.json"), Run(9999));

        var warnings = new List<string>();
        var runs = History.Load(dir, warnings);
        Equal(1, runs.Count, "runs dated in the future cannot push out real ones");
        Equal(50, warnings.Count, "and each is reported");

        // Reading stops once the chart is full, so the number of files cannot exhaust memory.
        var many = TempDir();
        for (var i = 0; i < 250; i++)
            File.WriteAllText(Path.Combine(many, $"hw-coverage-{i:D4}.json"), Run(2020));
        File.WriteAllText(Path.Combine(many, "hw-coverage-0000.json"), "{ would be reported if it were read");
        warnings.Clear();
        Equal(History.MaxRuns, History.Load(many, warnings).Count, "cap");
        Equal(0, warnings.Count, "older files are not read at all");

        var broken = TempDir();
        for (var i = 0; i < 250; i++)
            File.WriteAllText(Path.Combine(broken, $"hw-coverage-{i:D4}.json"), "{ not json");
        warnings.Clear();
        Equal(0, History.Load(broken, warnings).Count, "nothing valid");
        Check(warnings.Any(w => w.Contains("too large to read", StringComparison.Ordinal)), "says that it gave up");

        // A run too large to be read back is not kept.
        var tiny = TempDir();
        warnings.Clear();
        var f = new FileCoverage("/p/a.c");
        f.AddLine(1, 1);
        History.Save(tiny, ReportOf(f), warnings, maxBytes: 10);
        Equal(0, Directory.GetFiles(tiny).Length, "not saved");
        Equal(1, warnings.Count, "and reported");
    }

    static void TestHistoryChartLeavesGaps()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var withData = new Snapshot(new Counts(1, 2), null, new Counts(1, 1));
        var runs = new List<(DateTimeOffset, Snapshot?)>
        {
            (t, withData), (t.AddDays(1), withData), (t.AddDays(2), new Snapshot(null, null, null)), (t.AddDays(3), withData), (t.AddDays(4), withData),
        };
        var svg = HtmlReport.HistoryChart(runs);
        Equal(4, svg.Split("<polyline").Length - 1 - 0, "line and method split into two pieces each");
        Check(!svg.Contains("Branch n/a", StringComparison.Ordinal), "series without data has no label");
        Check(svg.Contains("Line 50%", StringComparison.Ordinal), "direct label");
        Check(HtmlReport.HistoryChart([(t, withData)]).Contains("<circle", StringComparison.Ordinal), "single run still draws a point");
    }

    // ---- command line ----

    static void TestCliExitCodes()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var o = Path.Combine(dir, "out");

        Equal(0, Cli("--help"), "--help");
        Equal(0, Cli("--version"), "--version");
        Equal(2, Cli(), "no arguments");
        Equal(2, Cli("-i", lcov), "missing -o");
        Equal(2, Cli("-o", o), "missing -i");
        Equal(2, Cli("-i"), "missing value");
        Equal(2, Cli("--nope"), "unknown option");
        Equal(2, Cli("-i", lcov, "-o", o, "-s", Path.Combine(dir, "nope")), "missing source folder");
        Equal(2, Cli("-i", Path.Combine(dir, "*", "x.gcov"), "-o", o), "wildcard in a folder segment");
        Equal(1, Cli("-i", Path.Combine(dir, "nope.info"), "-o", o), "missing input");
        Equal(1, Cli("-i", Path.Combine(dir, "*.nope"), "-o", o), "wildcard without match");

        var other = Path.Combine(dir, "other.txt");
        File.WriteAllText(other, "hello");
        Equal(1, Cli("-i", other, "-o", o), "unknown format");

        var badXml = Path.Combine(dir, "bad.xml");
        File.WriteAllText(badXml, "<coverage><packages>");
        Equal(1, Cli("-i", badXml, "-o", o), "broken XML");

        var empty = Path.Combine(dir, "empty.info");
        File.WriteAllText(empty, "TN:\n");
        Equal(1, Cli("-i", empty, "-o", o), "no coverage data");

        Equal(1, Cli("-i", lcov, "-o", o, "-x", "*"), "everything excluded");

        Check(!File.Exists(Path.Combine(o, "index.html")), "errors write no report");
        Equal(0, Cli("-i", lcov, "-o", o), "success");
        Check(File.Exists(Path.Combine(o, "index.html")), "report written");
        Check(!File.Exists(Path.Combine(o, "coverage.md")), "Markdown is optional");
        Equal(0, Cli("-i", lcov, "-o", o, "--markdown"), "with --markdown");
        Check(File.Exists(Path.Combine(o, "coverage.md")), "Markdown written");
    }

    static void TestCliHelpNamesEveryParameter()
    {
        var (o, e) = (Console.Out, Console.Error);
        try
        {
            foreach (var flag in new[] { "-h", "--help", "-?", "/h", "/help", "/?" })
            {
                var output = new StringWriter();
                Console.SetOut(output);
                Equal(0, Program.Main([flag]), flag);
                Check(output.ToString().StartsWith($"Coverage Report {Program.Version}", StringComparison.Ordinal), $"{flag} starts with name and version");
                Check(output.ToString().Contains("Copyright © 2026 Hanno Witt. Licensed under the Apache License 2.0.", StringComparison.Ordinal), $"{flag} shows the copyright notice");
                foreach (var parameter in new[] { "-i, --input", "-o, --output", "-t, --title", "-x, --exclude", "-s, --source-dir", "--source-dir-only", "--history-dir", "--markdown", "-h, --help", "--version" })
                    Check(output.ToString().Contains(parameter, StringComparison.Ordinal), $"{flag} lists {parameter}");
            }
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
            Console.OutputEncoding = Encoding.UTF8;
        }
    }

    // The file properties of the exe and the texts the program shows come from two places, which must agree.
    static void TestProductInformationMatches()
    {
        var assembly = typeof(Program).Assembly;
        Equal(Program.Product, assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product, "product name");
        Equal(Program.Product, assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title, "file description");
        Equal(Program.Copyright, assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright, "copyright");
        Equal("Hanno Witt", assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company, "author");
        Equal("coveragereport", assembly.GetName().Name, "the exe is coveragereport.exe");
    }

    static void TestCliTitle()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");

        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-t", " Firmware <b>v2</b>‮ ", "--markdown"), "run");
        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        var page = File.ReadAllText(Path.Combine(o, HtmlReport.PageName("/p/a.c")));
        Check(index.Contains("<h1>Firmware &lt;b&gt;v2&lt;/b&gt;</h1>", StringComparison.Ordinal), "the title is the heading, escaped, trimmed, without invisible characters");
        Check(index.Contains("<p class=\"meta\">Coverage summary &middot; Files: 1", StringComparison.Ordinal), "the former heading moves into the line below");
        Check(index.Contains("<title>Firmware &lt;b&gt;v2&lt;/b&gt; - Coverage summary</title>", StringComparison.Ordinal), "browser tab");
        Check(page.Contains("<span class=\"brand\">Coverage Report</span><span class=\"project\">Firmware &lt;b&gt;v2&lt;/b&gt;</span>", StringComparison.Ordinal), "header bar of a file page");
        Check(page.Contains("<title>a.c - Firmware &lt;b&gt;v2&lt;/b&gt;</title>", StringComparison.Ordinal), "browser tab of a file page");
        Check(File.ReadAllText(Path.Combine(o, "coverage.md")).Contains("\n# Firmware \\<b\\>v2\\<\\/b\\>\n", StringComparison.Ordinal), "Markdown heading");

        o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "/title", "Mine"), "/title works like --title");
        Check(File.ReadAllText(Path.Combine(o, "index.html")).Contains("<h1>Mine</h1>", StringComparison.Ordinal), "and is shown");

        o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o), "without a title");
        index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(index.Contains("<h1>Coverage summary</h1>", StringComparison.Ordinal) && !index.Contains("class=\"project\"", StringComparison.Ordinal), "everything stays as before");

        Equal(2, Cli("-i", lcov, "-o", o, "-t", "  "), "an empty title is an error");
        Equal(2, Cli("-i", lcov, "-o", o, "-t", new string('x', Program.MaxTitleLength + 1)), "so is one that is too long");
    }

    static void TestReportShowsNameAndCopyright()
    {
        var f = new FileCoverage("/p/a.c");
        f.AddLine(1, 1);
        var out_ = TempDir();
        HtmlReport.Write(ReportOf(f), out_);
        foreach (var page in Directory.GetFiles(out_, "*.html").Select(File.ReadAllText))
        {
            Check(page.Contains("<span class=\"brand\">Coverage Report</span>", StringComparison.Ordinal), "name in the header bar");
            Check(page.Contains($"<footer>Generated by Coverage Report {Program.Version} &middot; ", StringComparison.Ordinal), "name and version in the footer");
            Check(page.Contains("&middot; Copyright © 2026 Hanno Witt &middot; Licensed under the Apache License 2.0</footer>", StringComparison.Ordinal), "copyright and license notice in the footer");
            Check(!Regex.IsMatch(page, "href=\"https?:", RegexOptions.IgnoreCase), "the license is named, not linked: the report stays offline");
            Check(!page.Contains("hw-coverage", StringComparison.OrdinalIgnoreCase), "the former name is gone");
        }
    }

    static void TestCliSlashWorksLikeDash()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\nSF:/usr/b.c\nDA:1,1\nend_of_record\n");
        File.WriteAllText(Path.Combine(dir, "a.c"), "from_source_dir");

        foreach (var (input, output, exclude, source) in new[] { ("/i", "/o", "/x", "/s"), ("/input", "/output", "/exclude", "/source-dir") })
        {
            var o = TempDir();
            var history = Path.Combine(dir, "history" + input[1..]);
            Equal(0, Cli(input, lcov, output, o, exclude, "/usr/*", source, dir, "/history-dir", history, "/markdown"), $"{input} form");
            Check(File.Exists(Path.Combine(o, "coverage.md")), "/markdown");
            Equal(1, Directory.GetFiles(history).Length, "/history-dir");
            var pages = Directory.GetFiles(o, "f_*.html");
            Equal(1, pages.Length, "/x with a value that starts with a slash");
            Check(File.ReadAllText(pages[0]).Contains("from_source_dir", StringComparison.Ordinal), "/s");
        }

        Equal(0, Cli("/version"), "/version");
        Equal(2, Cli("/nope"), "unknown");
        Equal(2, Cli("/usr/include"), "a path without an option is no option");
        Equal(2, Cli("/i"), "missing value");

        // Values are never read as options, in either form.
        var report = TempDir();
        Equal(0, Cli("-i", lcov, "-o", report, "-x", "/h"), "a value /h is no help request");
        Check(File.Exists(Path.Combine(report, "index.html")), "the report was written");
        Equal(1, Cli("-i", "/help", "-o", report), "a value /help is a path that does not exist");
    }

    static void TestCliInputForms()
    {
        var dir = TempDir();
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub", "deep")).FullName;
        File.WriteAllText(Path.Combine(dir, "a.info"), "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        File.WriteAllText(Path.Combine(dir, "b.info"), "SF:/p/b.c\nDA:1,1\nend_of_record\n");
        File.WriteAllText(Path.Combine(sub, "c.gcov"), "        -:    0:Source:/p/c.c\n        1:    1:int c;\n");
        File.WriteAllText(Path.Combine(sub, "unrelated.xml"), "<project/>");

        int PagesFor(params string[] inputs)
        {
            var o = TempDir();
            var args = inputs.SelectMany(i => new[] { "-i", i }).Concat(["-o", o]).ToArray();
            Equal(0, Cli(args), "run");
            return Directory.GetFiles(o, "f_*.html").Length;
        }

        Equal(1, PagesFor(Path.Combine(dir, "a.info")), "single file");
        Equal(2, PagesFor(Path.Combine(dir, "*.info")), "wildcard");
        Equal(3, PagesFor(dir), "folder, recursive, unrelated XML ignored");
        Equal(2, PagesFor(Path.Combine(dir, "a.info"), Path.Combine(dir, "b.info")), "repeated -i");
        Equal(2, PagesFor(Path.Combine(dir, "a.info"), Path.Combine(dir, "*.info")), "same file only once");
    }

    static void TestCliReadsOnlyFromSourceDirs()
    {
        var dir = TempDir();
        var outside = Path.Combine(dir, "outside");
        var allowed = Path.Combine(dir, "allowed");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(allowed);
        File.WriteAllText(Path.Combine(outside, "secret.c"), "TOP-SECRET-OUTSIDE");
        File.WriteAllText(Path.Combine(outside, "id_rsa"), "TOP-SECRET-KEY");
        File.WriteAllText(Path.Combine(allowed, "ok.c"), "visible_code");

        var attack = Path.Combine(dir, "attack.info");
        File.WriteAllText(attack,
            $"SF:{Path.Combine(outside, "secret.c").Replace('\\', '/')}\nDA:1,1\nend_of_record\n"
            + $"SF:{Path.Combine(outside, "id_rsa").Replace('\\', '/')}\nDA:1,1\nend_of_record\n"
            + $"SF:{Path.Combine(allowed, "ok.c").Replace('\\', '/')}\nDA:1,1\nend_of_record\n");

        var o = TempDir();
        Equal(0, Cli("-i", attack, "-o", o, "-s", allowed, "--source-dir-only"), "run");
        var all = string.Concat(Directory.GetFiles(o).Select(File.ReadAllText));
        Check(!all.Contains("TOP-SECRET", StringComparison.Ordinal), "with --source-dir-only nothing outside --source-dir reaches the report");
        Check(all.Contains("visible_code", StringComparison.Ordinal), "files inside --source-dir do");

        o = TempDir();
        Equal(0, Cli("-i", attack, "-o", o, "--source-dir-only"), "--source-dir-only alone");
        all = string.Concat(Directory.GetFiles(o).Select(File.ReadAllText));
        Check(!all.Contains("TOP-SECRET", StringComparison.Ordinal) && !all.Contains("visible_code", StringComparison.Ordinal), "reads no source at all");
        Check(all.Contains("--source-dir-only was given without --source-dir", StringComparison.Ordinal), "and the page says why");

        // Without it, the C file at the named path is read by default; a file of any other kind never is.
        o = TempDir();
        Equal(0, Cli("-i", attack, "-o", o), "default");
        all = string.Concat(Directory.GetFiles(o).Select(File.ReadAllText));
        Check(all.Contains("TOP-SECRET-OUTSIDE", StringComparison.Ordinal), "the C file at the named path is read");
        Check(!all.Contains("TOP-SECRET-KEY", StringComparison.Ordinal), "a file without a C/C++ or assembler extension is not");
    }

    static void TestCliReadsNoSourceWithoutSourceDir()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "creds.h"), "TOP-SECRET-CWD");
        var attack = Path.Combine(dir, "attack.info");
        File.WriteAllText(attack, "SF:/anything/creds.h\nDA:1,1\nend_of_record\n");

        var o = TempDir();
        var cwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            Equal(0, Cli("-i", attack, "-o", o), "run");
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
        var all = string.Concat(Directory.GetFiles(o).Select(File.ReadAllText));
        Check(!all.Contains("TOP-SECRET", StringComparison.Ordinal), "a Linux path is no path on this drive, and the current folder is not searched");
        Check(all.Contains("not found at the path in the coverage file", StringComparison.Ordinal), "and the page says why there is no code");

        o = TempDir();
        Equal(0, Cli("-i", attack, "-o", o, "-s", dir), "with --source-dir");
        Check(string.Concat(Directory.GetFiles(o).Select(File.ReadAllText)).Contains("TOP-SECRET", StringComparison.Ordinal), "a named folder is searched");
    }

    // Paths as coverage files write them, and where they are looked for on this computer.
    static void TestLocalPaths()
    {
        var d = char.ToUpperInvariant(Path.GetTempPath()[0]);  // a local drive on any test machine
        var folder = $@"{d}:\build\out";
        Equal($@"{d}:\proj\src\a.c", SourceResolver.LocalPath($"{d}:/proj/src/a.c", folder), "Windows path");
        Equal($@"{d}:\proj\src\a.c", SourceResolver.LocalPath($@"{d}:\proj\x\..\src\a.c", folder), "backslashes and ..");
        Equal($@"{d}:\proj\src\a.c", SourceResolver.LocalPath($"/mnt/{char.ToLowerInvariant(d)}/proj/src/a.c", folder), "WSL path");
        Equal($@"{d}:\build\src\a.c", SourceResolver.LocalPath("../src/a.c", folder), "relative to the coverage file");
        Equal($@"{d}:\build\out\a.S", SourceResolver.LocalPath("a.S", folder), "assembler");
        Equal($@"{d}:\build\out\a.asm", SourceResolver.LocalPath("a.asm", folder), "assembler, .asm");

        Equal(null, SourceResolver.LocalPath("/home/u/proj/a.c", folder), "Linux path");
        Equal(null, SourceResolver.LocalPath($"/mnt/{d}", folder), "no file");
        Equal(null, SourceResolver.LocalPath("//server/share/a.c", folder), "network path");
        Equal(null, SourceResolver.LocalPath(@"\\server\share\a.c", folder), "network path with backslashes");
        Equal(null, SourceResolver.LocalPath(@"/\server\share\a.c", folder), "network path, mixed slashes");
        Equal(null, SourceResolver.LocalPath($@"\\?\{d}:\proj\a.c", folder), "device path");
        Equal(null, SourceResolver.LocalPath(@"\\.\pipe\a.c", folder), "pipe");
        Equal(null, SourceResolver.LocalPath(@"\??\UNC\server\share\a.c", folder), "NT path");
        Equal(null, SourceResolver.LocalPath("a.c", @"\\server\share\cov"), "relative to a network folder");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/notes.txt:x.c", folder), "alternate data stream");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/CON.c", folder), "device name");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/nul .h", folder), "device name with a space");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/com1.x.c", folder), "device name with two extensions");
        Equal(null, SourceResolver.LocalPath("C:a.c", folder), "relative to a drive's current folder");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/a.o", folder), "object file");
        Equal(null, SourceResolver.LocalPath($"{d}:/proj/lib.a", folder), "library");
        Equal(null, SourceResolver.LocalPath("<built-in>", folder), "no file");

        // A drive that does not exist could be a stored network mapping that Windows would reconnect.
        var present = DriveInfo.GetDrives().Select(x => char.ToUpperInvariant(x.Name[0])).ToHashSet();
        var missing = Enumerable.Range('D', 23).Select(c => (char)c).FirstOrDefault(c => !present.Contains(c));
        if (missing != default)
        {
            Equal(null, SourceResolver.LocalPath($"{missing}:/proj/a.c", folder), "missing drive");
            Equal(null, SourceResolver.LocalPath($"/mnt/{char.ToLowerInvariant(missing)}/proj/a.c", folder), "missing drive, WSL path");
        }
    }

    // Reading a file that is stored online only would make Windows fetch it.
    static void TestOfflineFilesAreNotRead()
    {
        var file = Path.Combine(TempDir(), "a.c");
        File.WriteAllText(file, "int a;\n");
        Check(SourceResolver.Load(file) != null, "a normal file is read");
        File.SetAttributes(file, FileAttributes.Offline);
        Check((File.GetAttributes(file) & FileAttributes.Offline) != 0, "the test file is marked offline");
        Equal(null, SourceResolver.Load(file), "an offline file is not");
        File.SetAttributes(file, FileAttributes.Normal);
    }

    // A relative path starts in the folder of its coverage file, not in the current folder.
    static void TestCliRelativePathsStartAtTheCoverageFile()
    {
        var dir = TempDir();
        var build = Directory.CreateDirectory(Path.Combine(dir, "build")).FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "a.c"), "int relative_code;\n");
        File.WriteAllText(Path.Combine(build, "a.gcov"), "        -:    0:Source:../src/a.c\n        1:    1:int gcov_text;\n");

        var o = TempDir();
        Equal(0, Cli("-i", Path.Combine(build, "a.gcov"), "-o", o), "run");
        var page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("relative_code", StringComparison.Ordinal), "the file on disk is found");
        Check(!page.Contains("gcov_text", StringComparison.Ordinal), "and wins over the text in the gcov file");

        o = TempDir();
        Equal(0, Cli("-i", Path.Combine(build, "a.gcov"), "-o", o, "--source-dir-only"), "--source-dir-only");
        page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("gcov_text", StringComparison.Ordinal), "keeps the text in the gcov file");
    }

    static void TestCliListsFilesWithoutCoverageData()
    {
        var dir = TempDir();
        var src = Directory.CreateDirectory(Path.Combine(dir, "src")).FullName;
        Directory.CreateDirectory(Path.Combine(src, "vendor"));
        File.WriteAllText(Path.Combine(src, "used.c"), "int used;\n");
        File.WriteAllText(Path.Combine(src, "unused.c"), "int a;\n\n// c\nint b;\n");
        File.WriteAllText(Path.Combine(src, "boot.S"), "@ c\nnop\n");
        File.WriteAllText(Path.Combine(src, "only.h"), "int h;\n");
        File.WriteAllText(Path.Combine(src, "vendor", "lib.c"), "int v;\n");
        File.WriteAllText(Path.Combine(src, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(src, "evil‮c.S"), "nop\n");  // reads as "evilS.c"
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/build/src/used.c\nDA:1,1\nend_of_record\n");

        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-s", src, "-x", "vendor/*", "--markdown"), "run");
        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        var at = index.IndexOf("<h2>Files without coverage data</h2>", StringComparison.Ordinal);
        Check(at > 0, "the list is there");
        var section = at > 0 ? index[at..] : "";
        Check(section.Contains(">unused.c</td><td>2</td>", StringComparison.Ordinal), "with its LOC, below the common folder");
        Check(section.Contains(">boot.S</td><td>1</td>", StringComparison.Ordinal), "assembler too");
        Check(section.Contains($"Paths below <span class=\"path\">{HtmlReport.H(src)}</span>", StringComparison.Ordinal), "the folder is named once");
        Check(!section.Contains(">used.c<", StringComparison.Ordinal), "not the file with coverage data");
        Check(!section.Contains("only.h", StringComparison.Ordinal), "no headers");
        Check(!section.Contains("lib.c", StringComparison.Ordinal), "nothing excluded");
        Check(!section.Contains("notes.txt", StringComparison.Ordinal), "no other files");
        Check(index.Contains("Without coverage data: 3", StringComparison.Ordinal), "counted in the title");
        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("| Line coverage | 1 | 1 | 100% |", StringComparison.Ordinal), "the totals stay as measured");
        Check(md.Contains("## Files without coverage data", StringComparison.Ordinal), "the Markdown lists them too");
        Check(md.Contains("evilc.S", StringComparison.Ordinal) && section.Contains("evilc.S", StringComparison.Ordinal), "a bidi override in a file name is removed");
        Check(!(md + index).Contains('‮'), "everywhere");

        o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o), "without --source-dir");
        Check(!File.ReadAllText(Path.Combine(o, "index.html")).Contains("Files without coverage data", StringComparison.Ordinal), "no list");
    }

    // The path in the coverage file wins over a match by path tail; --source-dir finds what is not there.
    static void TestCliCoveragePathBeforeSourceDir()
    {
        var dir = TempDir();
        var real = Directory.CreateDirectory(Path.Combine(dir, "real", "src")).FullName;
        var copy = Directory.CreateDirectory(Path.Combine(dir, "copy", "src")).FullName;
        File.WriteAllText(Path.Combine(real, "a.c"), "int real_a;\n");
        File.WriteAllText(Path.Combine(copy, "a.c"), "int copy_a;\n");
        File.WriteAllText(Path.Combine(copy, "b.c"), "int copy_b;\n");
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, $"SF:{Path.Combine(real, "a.c")}\nDA:1,1\nend_of_record\nSF:/elsewhere/src/b.c\nDA:1,1\nend_of_record\n");

        var o = TempDir();
        Equal(0, Cli("-i", lcov, "-o", o, "-s", Path.Combine(dir, "copy")), "run");
        var pages = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(pages.Contains("real_a", StringComparison.Ordinal) && !pages.Contains("copy_a", StringComparison.Ordinal), "the named path wins");
        Check(pages.Contains("copy_b", StringComparison.Ordinal), "--source-dir finds a file that is not at its path");
        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(!index.Contains("Files without coverage data", StringComparison.Ordinal), "a copy of a measured file is not unmeasured");
    }

    static void TestCliExcludeAlsoAppliesToTheFileOnDisk()
    {
        var dir = TempDir();
        var vendor = Directory.CreateDirectory(Path.Combine(dir, "vendor")).FullName;
        File.WriteAllText(Path.Combine(vendor, "creds.h"), "TOP-SECRET-CRED");

        // The coverage file does not mention "vendor", yet its tail matches vendor\creds.h.
        var attack = Path.Combine(dir, "attack.info");
        File.WriteAllText(attack, "SF:/anything/creds.h\nDA:1,1\nend_of_record\n");

        foreach (var pattern in new[] { "*/vendor/*", "vendor/*" })
        {
            var o = TempDir();
            Equal(0, Cli("-i", attack, "-o", o, "-s", dir, "-x", pattern), "run");
            var all = string.Concat(Directory.GetFiles(o).Select(File.ReadAllText));
            Check(!all.Contains("TOP-SECRET", StringComparison.Ordinal), $"{pattern}: an excluded file on disk stays out");
            Check(all.Contains("excluded by --exclude", StringComparison.Ordinal), $"{pattern}: and the report says why");
        }
    }

    static void TestCliNameOnlyMatches()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "config.h"), "project_config");

        var o = TempDir();
        var gcov = Path.Combine(dir, "other.gcov");
        File.WriteAllText(gcov, "        -:    0:Source:/usr/lib/other/config.h\n        1:    1:system_config\n");
        Equal(0, Cli("-i", gcov, "-o", o, "-s", dir), "gcov");
        var page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("system_config", StringComparison.Ordinal) && !page.Contains("project_config", StringComparison.Ordinal), "a same-named file does not replace gcov's text");

        o = TempDir();
        var lcov = Path.Combine(dir, "other.info");
        File.WriteAllText(lcov, "SF:/usr/lib/other/config.h\nDA:1,1\nend_of_record\n");
        Equal(0, Cli("-i", lcov, "-o", o, "-s", dir), "lcov");
        page = string.Concat(Directory.GetFiles(o, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("project_config", StringComparison.Ordinal), "without other text the match is used");
        Check(page.Contains("by file name only", StringComparison.Ordinal), "and marked as uncertain");
    }

    static void TestCliOutputFolderProblems()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var o = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        Equal(0, Cli("-i", lcov, "-o", o), "first run");
        var index = Path.Combine(o, "index.html");
        var page = Path.Combine(o, HtmlReport.PageName("/p/a.c"));
        var pageBefore = File.ReadAllText(page);

        var second = Path.Combine(dir, "b.info");
        File.WriteAllText(second, "SF:/p/a.c\nDA:1,0\nend_of_record\nSF:/p/new.c\nDA:1,1\nend_of_record\n");

        // A read-only index.html is detected before anything is moved.
        File.SetAttributes(index, FileAttributes.ReadOnly);
        Equal(1, Cli("-i", second, "-o", o), "read-only index.html");
        File.SetAttributes(index, FileAttributes.Normal);
        Equal(pageBefore, File.ReadAllText(page), "no page replaced");
        Check(!File.Exists(Path.Combine(o, HtmlReport.PageName("/p/new.c"))), "no page added");
        Equal(0, Directory.GetDirectories(o).Length, "no temp folder left");

        // So is a folder in the way.
        var blocked = Directory.CreateDirectory(Path.Combine(dir, "blocked")).FullName;
        Directory.CreateDirectory(Path.Combine(blocked, "index.html"));
        Equal(1, Cli("-i", lcov, "-o", blocked), "folder named index.html");
        Equal(0, Directory.GetFiles(blocked).Length, "nothing moved");

        // A stale page that cannot be removed must not fail a report that is already complete.
        Equal(0, Cli("-i", second, "-o", o), "second run");
        var stale = Path.Combine(o, HtmlReport.PageName("/p/new.c"));
        var (stdout, stderr) = (Console.Out, Console.Error);
        var error = new StringWriter();
        int code;
        using (new FileStream(stale, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(error);
            try
            {
                code = Program.Main(["-i", lcov, "-o", o]);
            }
            finally
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        Equal(0, code, "locked stale page");
        Check(error.ToString().Contains("Could not remove", StringComparison.Ordinal), "is a warning");
    }

    static void TestCliCleansUpAfterKilledRuns()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var o = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;

        var killed = Directory.CreateDirectory(Path.Combine(o, ".coveragereport-deadbeef")).FullName;
        File.WriteAllText(Path.Combine(killed, "f_0.html"), $"<!doctype html>{HtmlReport.Marker}TOP-SECRET-SOURCE");
        var killedLegacy = Directory.CreateDirectory(Path.Combine(o, ".hw-coverage-deadbeef")).FullName;
        File.WriteAllText(Path.Combine(killedLegacy, "f_0.html"), "<!doctype html><!-- hw-coverage report -->TOP-SECRET-SOURCE");
        var foreign = Directory.CreateDirectory(Path.Combine(o, ".hw-coverage-cafecafe")).FullName;
        File.WriteAllText(Path.Combine(foreign, "mine.txt"), "someone else's");
        var mixed = Directory.CreateDirectory(Path.Combine(o, ".hw-coverage-12345678")).FullName;
        File.WriteAllText(Path.Combine(mixed, "f_0.html"), HtmlReport.Marker);
        File.WriteAllText(Path.Combine(mixed, "mine.txt"), "someone else's");
        var otherName = Directory.CreateDirectory(Path.Combine(o, ".hw-coverage-keep-me")).FullName;
        File.WriteAllText(Path.Combine(otherName, "f_0.html"), HtmlReport.Marker);

        Equal(0, Cli("-i", lcov, "-o", o), "run");
        Check(!Directory.Exists(killed), "a killed run's folder is removed");
        Check(!Directory.Exists(killedLegacy), "also one left by hw-coverage, the former name");
        Check(File.Exists(Path.Combine(foreign, "mine.txt")), "a folder with foreign files stays");
        Check(File.Exists(Path.Combine(mixed, "mine.txt")), "a folder with some foreign files stays");
        Check(File.Exists(Path.Combine(otherName, "f_0.html")), "another name stays");
    }

    static void TestCliStaleMarkdownIsRemoved()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var o = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        var md = Path.Combine(o, "coverage.md");

        Equal(0, Cli("-i", lcov, "-o", o, "--markdown"), "with --markdown");
        Check(File.Exists(md), "written");
        Equal(0, Cli("-i", lcov, "-o", o), "without");
        Check(!File.Exists(md), "an old one no longer lists files that are gone");

        File.WriteAllText(md, "# my own notes");
        Equal(0, Cli("-i", lcov, "-o", o), "without");
        Check(File.Exists(md), "somebody else's coverage.md stays");
    }

    static void TestCliSkipsLinksInSourceFolders()
    {
        var dir = TempDir();
        var outside = Directory.CreateDirectory(Path.Combine(dir, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.c"), "TOP-SECRET-JUNCTION");
        var root = Directory.CreateDirectory(Path.Combine(dir, "root")).FullName;
        var link = Path.Combine(root, "link");
        Equal(0, Run("cmd", $"/c mklink /J \"{link}\" \"{outside}\""), "junction created");
        try
        {
            Check(File.Exists(Path.Combine(link, "secret.c")), "the junction works");
            var attack = Path.Combine(dir, "attack.info");
            File.WriteAllText(attack, "SF:/x/link/secret.c\nDA:1,1\nend_of_record\n");
            var o = TempDir();
            Equal(0, Cli("-i", attack, "-o", o, "-s", root), "run");
            Check(!string.Concat(Directory.GetFiles(o).Select(File.ReadAllText)).Contains("TOP-SECRET", StringComparison.Ordinal), "a junction does not lead out of the source folder");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    static void TestCliSourceDirWinsOverGcovText()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "a.c"), "real_code\n");
        var gcov = Path.Combine(dir, "a.c.gcov");
        File.WriteAllText(gcov, "        -:    0:Source:/x/src/a.c\n        1:    1:forged_code\n");

        var withDir = TempDir();
        Equal(0, Cli("-i", gcov, "-o", withDir, "-s", dir), "with --source-dir");
        var page = string.Concat(Directory.GetFiles(withDir, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("real_code", StringComparison.Ordinal) && !page.Contains("forged_code", StringComparison.Ordinal), "the file on disk is shown");
        Check(!page.Contains("as stored in the gcov file", StringComparison.Ordinal), "without the note");

        var cwd = Directory.GetCurrentDirectory();
        var empty = TempDir();
        var without = TempDir();
        Directory.SetCurrentDirectory(empty);
        try
        {
            Equal(0, Cli("-i", gcov, "-o", without), "without --source-dir");
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
        page = string.Concat(Directory.GetFiles(without, "f_*.html").Select(File.ReadAllText));
        Check(page.Contains("forged_code", StringComparison.Ordinal), "the stored text is used");
        Check(page.Contains("as stored in the gcov file", StringComparison.Ordinal), "and labeled as such");
    }

    static void TestCliWritesThroughNoLinks()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var victim = Path.Combine(dir, "victim.txt");
        File.WriteAllText(victim, "untouched");
        var o = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        Equal(0, Run("cmd", $"/c mklink /H \"{Path.Combine(o, "index.html")}\" \"{victim}\""), "hard link created");

        Equal(0, Cli("-i", lcov, "-o", o), "run");
        Equal("untouched", File.ReadAllText(victim), "the link's target is not overwritten");
        Check(File.ReadAllText(Path.Combine(o, "index.html")).Contains("Coverage summary", StringComparison.Ordinal), "the report is in place");
    }

    static void TestCliFailedRunChangesNothing()
    {
        var dir = TempDir();
        var o = Directory.CreateDirectory(Path.Combine(dir, "out")).FullName;
        var history = Path.Combine(dir, "history");
        var good = Path.Combine(dir, "good.info");
        File.WriteAllText(good, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        Equal(0, Cli("-i", good, "-o", o, "--history-dir", history), "first run");
        var before = File.ReadAllText(Path.Combine(o, "index.html"));
        var runs = Directory.GetFiles(history).Length;

        // Above the file limit, the input is refused before anything is written.
        var tooMany = Path.Combine(dir, "many.info");
        File.WriteAllText(tooMany, string.Concat(Enumerable.Range(0, HtmlReport.MaxFiles + 1).Select(i => $"SF:n{i}\nend_of_record\n")));
        Equal(1, Cli("-i", tooMany, "-o", o, "--history-dir", history), "too many files");
        Equal(before, File.ReadAllText(Path.Combine(o, "index.html")), "report unchanged");
        Equal(runs, Directory.GetFiles(history).Length, "no history entry");
        Check(Directory.GetDirectories(o).Length == 0, "no temp folder left");
        Equal(2, Directory.GetFiles(o).Length, "index.html and the one page");
    }

    static void TestCliKeepsEscapeSequencesOutOfTheConsole()
    {
        var error = new StringWriter();
        var (o, e) = (Console.Out, Console.Error);
        Console.SetError(error);
        try
        {
            Equal(1, Program.Main(["-i", "nope\u001b[31m\u0007.info", "-o", TempDir()]), "missing input");
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
            Console.OutputEncoding = Encoding.UTF8;
        }
        Check(error.ToString().Contains("nope", StringComparison.Ordinal), "message is written");
        Check(!error.ToString().Contains('\u001b') && !error.ToString().Contains('\u0007'), "without control characters");
    }

    static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    static void TestCliHistoryOnlyWhenRequested()
    {
        var dir = TempDir();
        var lcov = Path.Combine(dir, "a.info");
        File.WriteAllText(lcov, "SF:/p/a.c\nDA:1,1\nend_of_record\n");
        var o = Path.Combine(dir, "out");

        Equal(0, Cli("-i", lcov, "-o", o), "without");
        Check(!File.ReadAllText(Path.Combine(o, "index.html")).Contains("Coverage history", StringComparison.Ordinal), "no history section");
        Equal(1, Directory.GetDirectories(dir).Length, "nothing else created");

        var history = Path.Combine(dir, "history");
        Equal(0, Cli("-i", lcov, "-o", o, "--history-dir", history), "first run");
        Equal(0, Cli("-i", lcov, "-o", o, "--history-dir", history), "second run");
        Equal(2, Directory.GetFiles(history).Length, "one file per run");
        Check(File.ReadAllText(Path.Combine(o, "index.html")).Contains("Coverage history", StringComparison.Ordinal), "history section");
    }

    // ---- real tool output (sample/generate.sh) ----

    static void TestRealGcovOutput()
    {
        var o = TempDir();
        Equal(0, Cli("-i", Path.Combine(_data, "gcov"), "-o", o, "-x", "/usr/*", "--markdown"), "run");
        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("`mathutil.c`", StringComparison.Ordinal), "project file listed");
        Check(!md.Contains("stl_vector", StringComparison.Ordinal), "system headers excluded");
        var index = File.ReadAllText(Path.Combine(o, "index.html"));
        Check(!index.Contains("kpi-value na", StringComparison.Ordinal), "all metrics present");
    }

    static void TestRealGcovWithoutBranchData()
    {
        var o = TempDir();
        Equal(0, Cli("-i", Path.Combine(_data, "gcov-plain"), "-o", o, "-x", "/usr/*", "--markdown"), "run");
        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        Check(md.Contains("| Branch coverage | n/a | n/a | n/a |", StringComparison.Ordinal), "branch n/a");
        Check(md.Contains("| Method coverage | n/a | n/a | n/a |", StringComparison.Ordinal), "method n/a");
    }

    static void TestRealFormatsAgreeOnLineCoverage()
    {
        Dictionary<string, FileCoverage> Load(string file, InputFormat format)
        {
            var warnings = new List<string>();
            var files = Parsers.Parse(format, Parsers.ReadText(file), warnings);
            Equal(0, warnings.Count, $"real output raises no warnings: {string.Join("; ", warnings)}");
            return files.Where(f => f.Path.Contains("/sample/src/", StringComparison.Ordinal))
                .ToDictionary(f => f.Path[(f.Path.LastIndexOf('/') + 1)..]);
        }

        var lcov = Load(Path.Combine(_data, "lcov", "sample.info"), InputFormat.Lcov);
        var cobertura = Load(Path.Combine(_data, "cobertura", "sample.xml"), InputFormat.Cobertura);
        Check(lcov.Count >= 4, $"lcov lists the project files, got {lcov.Count}");
        Equal(lcov.Count, cobertura.Count, "same file count");
        foreach (var (name, file) in lcov)
        {
            Check(cobertura.ContainsKey(name), $"{name} in Cobertura");
            if (cobertura.TryGetValue(name, out var other))
                Equal(file.LineCounts, other.LineCounts, $"{name} line counts");
        }
        Equal(new Counts(2, 3), lcov["mathutil.c"].MethodCounts, "clamp and gcd called, unused_add not");
        Check(lcov["tokenizer.cpp"].Methods.Keys.Any(k => k == "demo::Tokenizer::split[abi:cxx11](char) const"), "readable C++ names");
    }

    static void TestRealMergeAcrossFormats()
    {
        var o = TempDir();
        Equal(0, Cli("-i", Path.Combine(_data, "lcov", "sample.info"), "-i", Path.Combine(_data, "cobertura", "sample.xml"), "-o", o, "--markdown", "-x", "/usr/*"), "run");
        var md = File.ReadAllText(Path.Combine(o, "coverage.md"));
        var files = md[md.IndexOf("\n## Files\n", StringComparison.Ordinal)..];
        files = files[..(files.IndexOf("\n## ", 1, StringComparison.Ordinal) is var end and > 0 ? end : files.Length)];
        Equal(1, files.Split("mathutil.c").Length - 1, "the same file from two formats is one entry");
    }

    // ---- no data may leave the machine ----

    // The tool's own code must not reference anything that can reach a network or start a process. This inspects
    // direct references only; that nothing is fetched at run time is covered separately (the DTD test, the browser check).
    static void TestAssemblyHasNoNetworkOrProcessAccess()
    {
        string[] forbiddenTypes = ["Process", "ProcessStartInfo", "XmlUrlResolver", "XmlPreloadedResolver", "Marshal", "NativeLibrary", "AssemblyLoadContext"];
        string[] forbiddenMembers = ["Load", "LoadFrom", "LoadFile", "InvokeMember", "CreateInstance"];
        var assembly = typeof(Program).Assembly.Location;
        using var pe = new PEReader(File.OpenRead(assembly));
        var md = pe.GetMetadataReader();

        foreach (var handle in md.AssemblyReferences)
        {
            var name = md.GetString(md.GetAssemblyReference(handle).Name);
            Check(!name.StartsWith("System.Net", StringComparison.Ordinal), $"references {name}");
            Check(name != "System.Diagnostics.Process", $"references {name}");
            Check(!name.Contains("Http", StringComparison.Ordinal), $"references {name}");
        }

        foreach (var handle in md.TypeReferences)
        {
            var type = md.GetTypeReference(handle);
            var ns = md.GetString(type.Namespace);
            var name = md.GetString(type.Name);
            Check(!ns.StartsWith("System.Net", StringComparison.Ordinal), $"uses {ns}.{name}");
            Check(!forbiddenTypes.Contains(name), $"uses {ns}.{name}");
        }

        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            var name = md.GetString(member.Name);
            if (member.Parent.Kind == HandleKind.TypeReference && forbiddenMembers.Contains(name))
            {
                var owner = md.GetTypeReference((TypeReferenceHandle)member.Parent);
                Check(md.GetString(owner.Name) is not ("Assembly" or "Type" or "Activator"), $"uses {md.GetString(owner.Name)}.{name}");
            }
        }

        // P/Invoke leaves no type reference; it shows up on the method definition itself.
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            Check(method.GetImport().Name.IsNil, $"native call: {md.GetString(method.Name)}");
        }
    }
}
