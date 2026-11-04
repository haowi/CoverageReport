// SPDX-FileCopyrightText: 2026 Hanno Witt
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace CoverageReport;

/// <summary>
/// Demangles names in the Itanium C++ ABI scheme, as reported by GCC-based coverage tools. It follows libiberty's
/// substitution rules and c++filt's output format.
/// </summary>
/// <remarks>
/// A name that cannot be resolved with certainty keeps its mangled form, because a wrong name is worse than a mangled
/// one. Limitation: function pointers, arrays, member pointers and expressions are not resolved.
/// </remarks>
sealed class Demangler
{
    const int MaxInput = 2048;
    const int MaxOutput = 4096;   // bounds the exponential growth that nested substitutions can cause
    const int MaxDepth = 64;

    sealed class Fail : Exception;

    static readonly Dictionary<char, string> Builtins = new()
    {
        ['v'] = "void", ['w'] = "wchar_t", ['b'] = "bool", ['c'] = "char", ['a'] = "signed char",
        ['h'] = "unsigned char", ['s'] = "short", ['t'] = "unsigned short", ['i'] = "int",
        ['j'] = "unsigned int", ['l'] = "long", ['m'] = "unsigned long", ['x'] = "long long",
        ['y'] = "unsigned long long", ['n'] = "__int128", ['o'] = "unsigned __int128",
        ['f'] = "float", ['d'] = "double", ['e'] = "long double", ['g'] = "__float128", ['z'] = "...",
    };

    static readonly Dictionary<char, string> ExtendedBuiltins = new()
    {
        ['n'] = "decltype(nullptr)", ['a'] = "auto", ['c'] = "decltype(auto)",
        ['i'] = "char32_t", ['s'] = "char16_t", ['u'] = "char8_t",
    };

    static readonly Dictionary<char, string> StdSubstitutions = new()
    {
        ['a'] = "std::allocator", ['b'] = "std::basic_string", ['s'] = "std::string",
        ['i'] = "std::istream", ['o'] = "std::ostream", ['d'] = "std::iostream",
    };

    static readonly Dictionary<string, string> Operators = new()
    {
        ["nw"] = " new", ["na"] = " new[]", ["dl"] = " delete", ["da"] = " delete[]", ["ps"] = "+", ["ng"] = "-",
        ["ad"] = "&", ["de"] = "*", ["co"] = "~", ["pl"] = "+", ["mi"] = "-", ["ml"] = "*", ["dv"] = "/",
        ["rm"] = "%", ["an"] = "&", ["or"] = "|", ["eo"] = "^", ["aS"] = "=", ["pL"] = "+=", ["mI"] = "-=",
        ["mL"] = "*=", ["dV"] = "/=", ["rM"] = "%=", ["aN"] = "&=", ["oR"] = "|=", ["eO"] = "^=", ["ls"] = "<<",
        ["rs"] = ">>", ["lS"] = "<<=", ["rS"] = ">>=", ["eq"] = "==", ["ne"] = "!=", ["lt"] = "<", ["gt"] = ">",
        ["le"] = "<=", ["ge"] = ">=", ["ss"] = "<=>", ["nt"] = "!", ["aa"] = "&&", ["oo"] = "||", ["pp"] = "++",
        ["mm"] = "--", ["cm"] = ",", ["pm"] = "->*", ["pt"] = "->", ["cl"] = "()", ["ix"] = "[]", ["qu"] = "?",
    };

    readonly string _s;
    int _pos;
    int _depth;
    int _typeDepth;
    readonly List<string> _subs = [];
    List<string> _templateArgs = [];
    List<bool> _isPack = [];
    bool _closureSeen;

    // Properties of the name parsed last, to be read right after Name() returns.
    bool _endsWithTemplate;
    bool _noReturnType;
    string _qualifiers = "";

    Demangler(string mangled) => _s = mangled;

    /// <summary>Returns the demangled name, or <paramref name="name"/> unchanged if it is not mangled or cannot be resolved.</summary>
    public static string Demangle(string name)
    {
        if (name.Length <= 2 || name.Length > MaxInput || !name.StartsWith("_Z", StringComparison.Ordinal))
            return name;
        try
        {
            var d = new Demangler(name) { _pos = 2 };
            var result = d.Encoding();
            return d._pos == name.Length ? result : name;
        }
        catch (Fail)
        {
            return name;
        }
    }

    bool AtEnd => _pos >= _s.Length;
    char Peek => AtEnd ? '\0' : _s[_pos];
    char PeekNext => _pos + 1 >= _s.Length ? '\0' : _s[_pos + 1];

    static string Check(string text) => text.Length > MaxOutput ? throw new Fail() : text;

    string Add(string text)
    {
        _subs.Add(Check(text));
        return text;
    }

    void Enter()
    {
        if (++_depth > MaxDepth)
            throw new Fail();
    }

    // Parses <encoding>. Inside a local name, the enclosing function is shown without its return type.
    string Encoding(bool showReturnType = true)
    {
        Enter();
        if (Peek == 'T' || (Peek == 'G' && PeekNext == 'V'))
            throw new Fail();  // vtables, guard variables and thunks are not functions a report lists

        var name = Name();
        var endsWithTemplate = _endsWithTemplate;
        var noReturnType = _noReturnType;
        var qualifiers = _qualifiers;

        string result;
        if (AtEnd || Peek == 'E')
        {
            result = name;
        }
        else
        {
            var returnType = endsWithTemplate && !noReturnType ? Type() + " " : "";
            result = (showReturnType ? returnType : "") + name + "(" + TypeList() + ")" + qualifiers;
        }
        _depth--;
        return Check(result);
    }

    string TypeList()
    {
        var types = new List<string>();
        while (!AtEnd && Peek != 'E')
            types.Add(Type());
        return types is ["void"] ? "" : string.Join(", ", types);
    }

    string Name()
    {
        Enter();
        string result;
        _qualifiers = "";
        var endsWithTemplate = false;
        var noReturnType = false;
        var qualifiers = "";

        switch (Peek)
        {
            case 'N':
                (result, endsWithTemplate, noReturnType, qualifiers) = Nested();
                break;
            case 'Z':
                (result, qualifiers) = Local();
                break;
            case 'S':
            {
                var isSubstitution = PeekNext != 't';
                if (isSubstitution)
                {
                    result = Substitution();
                }
                else
                {
                    _pos += 2;
                    result = "std::" + Unqualified(ref noReturnType);
                }
                if (Peek == 'I')
                {
                    if (!isSubstitution)
                        Add(result);
                    result = Check(result + NameTemplateArgs());
                    endsWithTemplate = true;
                }
                break;
            }
            default:
                result = Unqualified(ref noReturnType);
                if (Peek == 'I')
                {
                    Add(result);
                    result = Check(result + NameTemplateArgs());
                    endsWithTemplate = true;
                }
                break;
        }

        _endsWithTemplate = endsWithTemplate;
        _noReturnType = noReturnType;
        _qualifiers = qualifiers;
        _depth--;
        return result;
    }

    (string, bool, bool, string) Nested()
    {
        _pos++;  // N
        var qualifiers = "";
        while (Peek is 'r' or 'V' or 'K')
        {
            qualifiers = (_s[_pos++] switch { 'r' => " restrict", 'V' => " volatile", _ => " const" }) + qualifiers;
        }
        if (Peek is 'R' or 'O')
            qualifiers += _s[_pos++] == 'R' ? " &" : " &&";

        var current = "";
        var endsWithTemplate = false;
        var noReturnType = false;

        while (Peek != 'E')
        {
            if (AtEnd)
                throw new Fail();
            var wasSubstitution = false;
            endsWithTemplate = false;
            if (Peek != 'I')
                noReturnType = false;  // template arguments after a constructor name leave it a constructor

            if (Peek == 'S' && PeekNext == 't')
            {
                _pos += 2;
                current = Join(current, "std");
                wasSubstitution = true;  // "std" on its own is not a substitution candidate
            }
            else if (Peek == 'S')
            {
                current = Substitution();
                wasSubstitution = true;
            }
            else if (Peek == 'T')
            {
                current = TemplateParam();
            }
            else if (Peek == 'I')
            {
                if (current.Length == 0)
                    throw new Fail();
                current = Check(current + NameTemplateArgs());
                endsWithTemplate = true;
            }
            else if (Peek == 'C' && PeekNext is >= '1' and <= '5')
            {
                _pos += 2;
                current = Join(current, ConstructorName(current));
                noReturnType = true;
            }
            else if (Peek == 'D' && PeekNext is '0' or '1' or '2' or '4' or '5')
            {
                _pos += 2;
                current = Join(current, "~" + ConstructorName(current));
                noReturnType = true;
            }
            else
            {
                current = Join(current, Unqualified(ref noReturnType));
            }

            if (!wasSubstitution && Peek != 'E')
                Add(current);
        }
        if (current.Length == 0)
            throw new Fail();
        _pos++;  // E
        return (Check(current), endsWithTemplate, noReturnType, qualifiers);
    }

    static string Join(string prefix, string name) => prefix.Length == 0 ? name : Check(prefix + "::" + name);

    static string ConstructorName(string owner)
    {
        switch (owner)
        {
            case "std::string": return "basic_string";
            case "std::istream": return "basic_istream";
            case "std::ostream": return "basic_ostream";
            case "std::iostream": return "basic_iostream";
            case "":
                throw new Fail();
        }

        // The last name component, without its template arguments.
        var end = owner.Length;
        var angle = 0;
        for (var i = owner.Length - 1; i >= 0; i--)
        {
            if (owner[i] == '>')
            {
                angle++;
            }
            else if (owner[i] == '<')
            {
                if (--angle == 0)
                    end = i;
            }
            else if (owner[i] == ':' && angle == 0)
            {
                return owner[(i + 1)..end];
            }
        }
        return owner[..end];
    }

    (string, string) Local()
    {
        // Inside a type, the inner encoding's template arguments would be lost, so the name is not resolved.
        if (_typeDepth > 0)
            throw new Fail();
        _pos++;  // Z
        var function = Encoding(showReturnType: false);
        if (Peek != 'E')
            throw new Fail();
        _pos++;

        string entity;
        var qualifiers = "";
        if (Peek == 's')
        {
            _pos++;
            entity = "string literal";
        }
        else if (Peek == 'd')
        {
            throw new Fail();
        }
        else
        {
            entity = Name();
            qualifiers = _qualifiers;
        }
        SkipDiscriminator();

        // The caller parses the entity's parameters; the enclosing function is shown without a return type.
        return (Check(function + "::" + entity), qualifiers);
    }

    void SkipDiscriminator()
    {
        if (Peek != '_')
            return;
        var p = _pos + 1;
        if (p < _s.Length && _s[p] == '_')
        {
            p++;
            while (p < _s.Length && char.IsAsciiDigit(_s[p]))
                p++;
            if (p < _s.Length && _s[p] == '_')
                _pos = p + 1;
        }
        else if (p < _s.Length && char.IsAsciiDigit(_s[p]))
        {
            _pos = p + 1;
        }
    }

    string Unqualified(ref bool noReturnType)
    {
        if (Peek == 'L')
            _pos++;  // internal linkage

        string result;
        if (char.IsAsciiDigit(Peek))
        {
            result = SourceName();
        }
        else if (Peek == 'U' && PeekNext == 'l')
        {
            result = Lambda();
        }
        else if (Peek == 'U' && PeekNext == 't')
        {
            _pos += 2;
            result = $"{{unnamed type#{SequenceNumber() + 1}}}";
            _closureSeen = true;
        }
        else if (char.IsAsciiLetterLower(Peek))
        {
            result = Operator(ref noReturnType);
        }
        else
        {
            throw new Fail();
        }

        while (Peek == 'B')
        {
            _pos++;
            result += "[abi:" + SourceName() + "]";
        }
        return Check(result);
    }

    string SourceName()
    {
        var length = 0;
        while (char.IsAsciiDigit(Peek))
        {
            length = length * 10 + (_s[_pos++] - '0');
            if (length > _s.Length)
                throw new Fail();
        }
        if (length == 0 || _pos + length > _s.Length)
            throw new Fail();
        var name = _s.Substring(_pos, length);
        _pos += length;
        return name.StartsWith("_GLOBAL__N_", StringComparison.Ordinal) ? "(anonymous namespace)" : name;
    }

    string Lambda()
    {
        _pos += 2;
        var parameters = TypeListInType();
        if (Peek != 'E')
            throw new Fail();
        _pos++;
        _closureSeen = true;
        return $"{{lambda({parameters})#{SequenceNumber() + 1}}}";
    }

    string TypeListInType()
    {
        _typeDepth++;
        var list = TypeList();
        _typeDepth--;
        return list;
    }

    // Parses <number>_, where "_" is 0, "0_" is 1, and so on.
    int SequenceNumber()
    {
        var value = 0;
        var any = false;
        while (char.IsAsciiDigit(Peek))
        {
            value = value * 10 + (_s[_pos++] - '0');
            any = true;
            if (value > _s.Length)
                throw new Fail();
        }
        if (Peek != '_')
            throw new Fail();
        _pos++;
        return any ? value + 1 : 0;
    }

    string Operator(ref bool noReturnType)
    {
        if (_pos + 2 > _s.Length)
            throw new Fail();
        var code = _s.Substring(_pos, 2);
        _pos += 2;
        if (code == "cv")
        {
            noReturnType = true;
            _typeDepth++;
            var type = Type();
            _typeDepth--;
            return "operator " + type;
        }
        if (code == "li")
            return "operator\"\" " + SourceName();
        return Operators.TryGetValue(code, out var op) ? "operator" + op : throw new Fail();
    }

    // The template arguments of the function's own name determine what T_ refers to in its parameter list.
    string NameTemplateArgs()
    {
        var keep = _typeDepth == 0;
        var (text, args, packs) = TemplateArgs();
        if (keep)
        {
            _templateArgs = args;
            _isPack = packs;
        }
        return text;
    }

    (string, List<string>, List<bool>) TemplateArgs()
    {
        Enter();
        _pos++;  // I
        _typeDepth++;
        var args = new List<string>();
        var packs = new List<bool>();
        while (Peek != 'E')
        {
            if (AtEnd)
                throw new Fail();
            var (text, unexpandable) = TemplateArg();
            args.Add(text);
            packs.Add(unexpandable);
        }
        _pos++;  // E
        _typeDepth--;
        _depth--;

        var joined = string.Join(", ", args.Where(a => a.Length > 0));
        var close = joined.EndsWith('>') ? " >" : ">";
        return (Check("<" + joined + close), args, packs);
    }

    (string Text, bool Unexpandable) TemplateArg()
    {
        switch (Peek)
        {
            case 'L':
                return (Literal(), false);
            case 'J':
            {
                Enter();
                _pos++;
                var items = new List<string>();
                while (Peek != 'E')
                {
                    if (AtEnd)
                        throw new Fail();
                    items.Add(TemplateArg().Text);
                }
                _pos++;
                _depth--;
                return (Check(string.Join(", ", items)), items.Count != 1);
            }
            case 'X':
                throw new Fail();
            default:
                return (Type(), false);
        }
    }

    string Literal()
    {
        _pos++;  // L
        if (Peek == '_' || !Builtins.TryGetValue(Peek, out var type))
            throw new Fail();
        var code = _s[_pos++];
        var start = _pos;
        while (!AtEnd && Peek != 'E')
            _pos++;
        if (AtEnd)
            throw new Fail();
        var value = _s[start.._pos].Replace('n', '-');
        _pos++;  // E
        return code switch
        {
            'b' => value switch { "0" => "false", "1" => "true", _ => throw new Fail() },
            'i' => value,
            'j' => value + "u",
            'l' => value + "l",
            'm' => value + "ul",
            'x' => value + "ll",
            'y' => value + "ull",
            _ => $"({type}){value}",
        };
    }

    string TemplateParam()
    {
        _pos++;  // T
        var index = SequenceNumber();
        if (index >= _templateArgs.Count || _isPack[index])
            throw new Fail();
        return Add(_templateArgs[index]);
    }

    string Substitution()
    {
        _pos++;  // S
        if (StdSubstitutions.TryGetValue(Peek, out var std))
        {
            _pos++;
            return std;
        }

        // A base-36 sequence id, where "_" denotes the first entry.
        var index = 0;
        if (Peek != '_')
        {
            var value = 0;
            var any = false;
            while (char.IsAsciiDigit(Peek) || char.IsAsciiLetterUpper(Peek))
            {
                var c = _s[_pos++];
                value = value * 36 + (char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10);
                any = true;
                if (value > _s.Length)
                    throw new Fail();
            }
            if (!any)
                throw new Fail();
            index = value + 1;
        }
        if (Peek != '_')
            throw new Fail();
        _pos++;

        // libiberty and the compilers disagree on whether closure types count as candidates, so the index is not guessed.
        if (_closureSeen || index >= _subs.Count)
            throw new Fail();
        return _subs[index];
    }

    string Type()
    {
        Enter();
        _typeDepth++;
        var result = TypeInner();
        _typeDepth--;
        _depth--;
        return Check(result);
    }

    string TypeInner()
    {
        var c = Peek;
        if (Builtins.TryGetValue(c, out var builtin))
        {
            _pos++;
            return builtin;
        }

        switch (c)
        {
            case 'r' or 'V' or 'K':
            {
                var suffix = new StringBuilder();
                while (Peek is 'r' or 'V' or 'K')
                    suffix.Insert(0, _s[_pos++] switch { 'r' => " restrict", 'V' => " volatile", _ => " const" });
                return Add(Type() + suffix);
            }
            case 'P':
                _pos++;
                return Add(Type() + "*");
            case 'R':
            {
                _pos++;
                var inner = Type();
                // Reference collapsing: T& where T is already a reference.
                return Add(inner.EndsWith("&&", StringComparison.Ordinal) ? inner[..^1]
                    : inner.EndsWith('&') ? inner : inner + "&");
            }
            case 'O':
            {
                _pos++;
                var inner = Type();
                return Add(inner.EndsWith('&') ? inner : inner + "&&");
            }
            case 'D':
                _pos++;
                if (Peek == 'p')
                {
                    _pos++;
                    return Add(Type());
                }
                if (ExtendedBuiltins.TryGetValue(Peek, out var extended))
                {
                    _pos++;
                    return extended;
                }
                throw new Fail();
            case 'T':
            {
                var param = TemplateParam();
                if (Peek != 'I')
                    return param;
                return Add(param + TemplateArgs().Item1);
            }
            case 'S':
            {
                var next = PeekNext;
                if (next == 't')
                    return Add(Name());
                var result = Substitution();
                return Peek == 'I' ? Add(result + TemplateArgs().Item1) : result;
            }
            case 'N' or 'Z':
                return Add(Name());
            default:
                if (char.IsAsciiDigit(c))
                    return Add(Name());
                throw new Fail();
        }
    }
}
