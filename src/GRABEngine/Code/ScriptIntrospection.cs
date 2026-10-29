using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Code;
using GrabEngine.Core;

namespace GrabEngine.Code;

public sealed class ScriptFieldInfo
{
    public string Name { get; init; } = "";
    public string TypeName { get; init; } = "float";
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public string Header { get; init; } = "";
    public double Min { get; init; } = double.NaN;
    public double Max { get; init; } = double.NaN;
    public bool IsColor { get; init; }
    public bool IsMultiline { get; init; }
    public bool IsBool => TypeName is "bool";
    public bool IsString => TypeName is "string" or "String";
    public bool IsVector => TypeName is "Vector3";
    public bool IsEnum => TypeName is not ("float" or "int" or "double" or "bool" or "string" or "Vector3" or "Color" or "GameObject" or "Trigger" or "Light" or "Sign" or "PhysicsBlock");
    public string DefaultText { get; init; } = "";
    public int Line { get; init; }
}

public sealed class ScriptButtonInfo
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
    public int Line { get; init; }
}

public sealed class ScriptClassInfo
{
    public string Name { get; init; } = "MyScript";
    public List<ScriptFieldInfo> Fields { get; } = new();
    public List<ScriptButtonInfo> Buttons { get; } = new();
    public List<string> Lifecycle { get; } = new();
    public List<string> Events { get; } = new();
    public int Line { get; init; }
}

/// <summary>
/// Reads a C# code block without compiling it: class name, inspector-exposed fields,
/// [Button] methods and the lifecycle hooks it implements. The editor uses this to
/// build a real component inspector and to warn about unsupported callbacks.
/// </summary>
public static class ScriptIntrospection
{
    public static readonly string[] LifecycleNames =
    {
        "Awake", "OnEnable", "Start", "FixedUpdate", "Update", "LateUpdate", "OnDisable", "OnDestroy"
    };

    public static readonly string[] EventNames =
    {
        "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnSignal", "OnGrab", "OnRelease", "OnPlayerJoin", "OnPlayerLeave"
    };

    public static ScriptClassInfo Inspect(string source)
    {
        var diags = new List<Diagnostic>();
        CompilationUnit unit;
        try
        {
            unit = new CsParser(source ?? "", diags).Parse();
        }
        catch
        {
            return new ScriptClassInfo();
        }
        var cls = unit.Classes.FirstOrDefault(c => c.Methods.Any(m => m.Name is "Update" or "Start"))
                  ?? unit.Classes.FirstOrDefault();
        if (cls == null) return new ScriptClassInfo();
        var info = new ScriptClassInfo { Name = cls.Name, Line = cls.Line };
        foreach (var f in cls.Fields)
        {
            if (f.IsConst || f.IsStatic) continue;
            bool serialize = f.IsPublic || HasAttribute(f.Attributes, "SerializeField") || HasAttribute(f.Attributes, "SerializeReference");
            if (!serialize) continue;
            if (HasAttribute(f.Attributes, "HideInInspector") || HasAttribute(f.Attributes, "NonSerialized")) continue;
            if (f.IsArray) continue;
            var range = Attribute(f.Attributes, "Range");
            var slider = Attribute(f.Attributes, "Slider");
            double min = double.NaN, max = double.NaN;
            if ((range ?? slider) is { } r)
            {
                var args = ParseArgs(r);
                if (args.Count >= 2 &&
                    double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) &&
                    double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hi))
                {
                    min = lo;
                    max = hi;
                }
            }
            info.Fields.Add(new ScriptFieldInfo
            {
                Name = f.Name,
                TypeName = f.TypeName,
                Label = StringArg(Attribute(f.Attributes, "Label")) ?? Pretty(f.Name),
                Tooltip = StringArg(Attribute(f.Attributes, "Tooltip")) ?? "",
                Header = StringArg(Attribute(f.Attributes, "Header")) ?? "",
                Min = min,
                Max = max,
                IsColor = f.TypeName == "Color" || HasAttribute(f.Attributes, "ColorUsage"),
                IsMultiline = HasAttribute(f.Attributes, "TextArea") || HasAttribute(f.Attributes, "Multiline"),
                DefaultText = f.Initializer != null ? ExprText(f.Initializer) : DefaultFor(f.TypeName),
                Line = f.Line
            });
        }
        foreach (var m in cls.Methods)
        {
            if (!HasAttribute(m.Attributes, "Button")) continue;
            info.Buttons.Add(new ScriptButtonInfo
            {
                Name = m.Name,
                Label = StringArg(Attribute(m.Attributes, "Button")) ?? Pretty(m.Name),
                Tooltip = StringArg(Attribute(m.Attributes, "Tooltip")) ?? "",
                Line = m.Line
            });
        }
        info.Lifecycle.AddRange(cls.Methods.Select(m => m.Name).Where(n => LifecycleNames.Contains(n)).Distinct());
        info.Events.AddRange(cls.Methods.Select(m => m.Name).Where(n => EventNames.Contains(n)).Distinct());
        return info;
    }

    public static string ClassName(string source)
    {
        var info = Inspect(source);
        return string.IsNullOrWhiteSpace(info.Name) ? "MyScript" : info.Name;
    }

    public static string DefaultFor(string type) => type switch
    {
        "bool" => "false",
        "string" or "String" => "\"\"",
        "Vector3" => "new Vector3(0, 0, 0)",
        "Color" => "new Color(1, 1, 1, 1)",
        _ => "0"
    };

    public static bool HasAttribute(IEnumerable<string> attributes, string name)
        => attributes.Any(a => a.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static string? Attribute(IEnumerable<string> attributes, string name)
        => attributes.FirstOrDefault(a => a.StartsWith(name + "(", StringComparison.OrdinalIgnoreCase)) ??
           attributes.FirstOrDefault(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static List<string> ParseArgs(string attribute)
    {
        int open = attribute.IndexOf('(');
        int close = attribute.LastIndexOf(')');
        if (open < 0 || close <= open) return new List<string>();
        return SplitArguments(attribute.Substring(open + 1, close - open - 1));
    }

    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) quoted = !quoted;
            if (!quoted && (c == '(' || c == '[')) depth++;
            if (!quoted && (c == ')' || c == ']')) depth--;
            if (!quoted && depth == 0 && c == ',')
            {
                result.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    private static string? StringArg(string? attribute)
    {
        if (attribute == null) return null;
        int open = attribute.IndexOf('(');
        int close = attribute.LastIndexOf(')');
        if (open < 0 || close <= open) return Pretty(attribute);
        var raw = attribute.Substring(open + 1, close - open - 1).Trim();
        if (raw.StartsWith("\"") && raw.EndsWith("\"")) raw = raw[1..^1];
        return raw.Replace("\\n", "\n");
    }

    public static string Pretty(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(name[i]);
        }
        return char.ToUpper(sb[0]) + sb.ToString()[1..];
    }

    public static string ExprText(Expr e) => e switch
    {
        LiteralExpr l when l.Type == CsType.String => "\"" + (l.Value ?? "") + "\"",
        LiteralExpr l when l.Type == CsType.Bool => l.Number != 0 ? "true" : "false",
        LiteralExpr l when l.IsNull => "null",
        LiteralExpr l => l.Number.ToString("0.####", CultureInfo.InvariantCulture),
        UnaryExpr { Op: "-" } u => "-" + ExprText(u.Operand),
        NewExpr n when n.Args.Count > 0 => $"new {n.TypeName}({string.Join(", ", n.Args.Select(ExprText))})",
        NameExpr name => name.Name,
        MemberExpr m => ExprText(m.Target) + "." + m.Name,
        _ => ""
    };

    /// <summary>
    /// Turns an inspector override into a compile-time expression. Only constants and
    /// the Vector3/Color constructors are accepted: a serialized field is a starting
    /// value, not executable code.
    /// </summary>
    public static Expr? ParseValue(string text, string typeName)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        if (typeName is "bool")
        {
            if (bool.TryParse(text, out var b)) return new LiteralExpr { Type = CsType.Bool, Number = b ? 1 : 0 };
            if (text == "1" || text == "0") return new LiteralExpr { Type = CsType.Bool, Number = text == "1" ? 1 : 0 };
            return null;
        }
        if (typeName is "string" or "String")
        {
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                return new LiteralExpr { Type = CsType.String, Value = text[1..^1] };
            return new LiteralExpr { Type = CsType.String, Value = text };
        }
        if (typeName is "Vector3")
        {
            var args = ExtractArgs(text, "Vector3");
            if (args.Count >= 3)
            {
                var n = new NewExpr { TypeName = "Vector3" };
                foreach (var a in args.Take(3))
                {
                    if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
                    n.Args.Add(new LiteralExpr { Type = CsType.Float, Number = v });
                }
                return n;
            }
            return null;
        }
        if (typeName is "Color")
        {
            var args = ExtractArgs(text, "Color");
            if (args.Count >= 3)
            {
                var n = new NewExpr { TypeName = "Color" };
                foreach (var a in args.Take(4))
                {
                    if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
                    n.Args.Add(new LiteralExpr { Type = CsType.Float, Number = v });
                }
                return n;
            }
            return null;
        }
        // Enum values are compile-time names.
        if (text.Contains('.') && !text.Contains('('))
        {
            var parts = text.Split('.');
            Expr expr = new NameExpr { Name = parts[0] };
            for (int i = 1; i < parts.Length; i++) expr = new MemberExpr { Target = expr, Name = parts[i] };
            return expr;
        }
        if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase)) text = text[..^1];
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return new LiteralExpr { Type = CsType.Float, Number = num };
        if (text == "true" || text == "false")
            return new LiteralExpr { Type = CsType.Bool, Number = text == "true" ? 1 : 0 };
        return null;
    }

    private static List<string> ExtractArgs(string text, string type)
    {
        int open = text.IndexOf('(');
        int close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return new List<string>();
        return SplitArguments(text.Substring(open + 1, close - open - 1));
    }
}
