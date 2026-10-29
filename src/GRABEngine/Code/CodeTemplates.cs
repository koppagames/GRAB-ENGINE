using System;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.Code;

public static class CodeTemplates
{
    public static string NewScript(GrabLevel level, LevelNode codeNode)
    {
        var target = level.AllNodes().FirstOrDefault(n => n.Kind is NodeKind.Static or NodeKind.Crumbling && n != codeNode);
        string targetName = target?.DisplayName ?? "";
        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// GRAB ENGINE C# script. This is compiled to real GASM.");
        sb.AppendLine("// Use GameObject.Find(\"Name\") to bind a scene object at compile time.");
        sb.AppendLine("public class MyScript : CodeBlock");
        sb.AppendLine("{");
        if (target != null)
        {
            sb.AppendLine($"    GameObject target = GameObject.Find(\"{targetName.Replace("\"", "\\\"")}\");");
            sb.AppendLine();
        }
        sb.AppendLine("    public float speed = 2f;");
        sb.AppendLine();
        sb.AppendLine("    void Start()");
        sb.AppendLine("    {");
        sb.AppendLine("        // Runs once when the code block starts.");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        if (target != null)
        {
            sb.AppendLine("        // Example: bob the target up and down.");
            sb.AppendLine("        target.Position.Y += Mathf.Sin(Time.Time * speed * 30f) * speed * Time.DeltaTime;");
        }
        else
        {
            sb.AppendLine("        // Create a block and reference it: GameObject block = GameObject.Find(\"Cube\");");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string ImportedStub(GrabLevel level, LevelNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine("// This Code Block was imported with a hand-written GASM program.");
        sb.AppendLine("// The original program is preserved until you compile a new C# script.");
        if (node.ImportedConnections.Count > 0)
        {
            sb.AppendLine("// Connections: " + string.Join(", ", node.ImportedConnections.Select(c => c.Name + " (" + c.Type + ")")));
        }
        sb.AppendLine("public class ImportedScript : CodeBlock");
        sb.AppendLine("{");
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        sb.AppendLine("        // TODO: rewrite the imported GASM logic in C#.");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

public static class GasmDecompiler
{
    public static string ToCommentText(LevelNode node)
    {
        if (node.ImportedProgram == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("// Imported GASM (" + node.ImportedProgram.Instructions.Count + " instructions)");
        var names = node.ImportedProgram.WorkingRegisters.Select((r, i) => string.IsNullOrEmpty(r.Name) ? "R" + i : r.Name).ToArray();
        foreach (var ins in node.ImportedProgram.Instructions)
        {
            if (ins.Op == GasmOp.Label && ins.Operands.Count > 0)
            {
                sb.AppendLine("// " + (node.ImportedProgram.Labels.ElementAtOrDefault(ins.Operands[0].Index) ?? "label") + ":");
                continue;
            }
            sb.Append("//   " + GasmEmitter.OpName(ins.Op));
            foreach (var op in ins.Operands) sb.Append(' ' + Operand(op, node.ImportedProgram, names));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Operand(GasmOperand op, GasmProgram p, string[] working) => op.Kind switch
    {
        GasmOperandKind.Working => working.ElementAtOrDefault(op.Index) ?? ("R" + op.Index),
        GasmOperandKind.Input => p.InputRegisters.ElementAtOrDefault(op.Index)?.Name ?? ("in" + op.Index),
        GasmOperandKind.Output => p.OutputRegisters.ElementAtOrDefault(op.Index)?.Name ?? ("out" + op.Index),
        GasmOperandKind.InOut => p.InOutRegisters.ElementAtOrDefault(op.Index)?.Name ?? ("io" + op.Index),
        GasmOperandKind.Constant => op.Value.ToString("0.###"),
        GasmOperandKind.Label => p.Labels.ElementAtOrDefault(op.Index) ?? ("L" + op.Index),
        GasmOperandKind.Special => op.Index == 4 ? "DeltaTime" : "special" + op.Index,
        _ => op.Index.ToString()
    };

    /// <summary>
    /// Best-effort GASM → C#. Registers become local floats, labels/gotos are preserved
    /// (C# supports them), and each instruction becomes the matching arithmetic or
    /// assignment. The result is a readable starting point for editing an imported
    /// Code Block, not a byte-perfect round trip: GASM has no structured control flow.
    /// </summary>
    public static string ToCSharp(LevelNode node)
    {
        var p = node.ImportedProgram;
        if (p == null) return "// This Code Block has no imported GASM program.";
        var sb = new StringBuilder();
        sb.AppendLine("using Grab;");
        sb.AppendLine();
        sb.AppendLine($"// Decompiled from the imported GASM of '{node.DisplayName}'.");
        sb.AppendLine($"// {p.Instructions.Count} instructions · {p.InputRegisters.Count + p.OutputRegisters.Count + p.InOutRegisters.Count} connected registers.");
        sb.AppendLine("// Labels and gotos match the original program; use this as a starting point.");
        sb.AppendLine("public class DecompiledScript : CodeBlock");
        sb.AppendLine("{");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in p.WorkingRegisters)
        {
            string n = Identifier(string.IsNullOrWhiteSpace(r.Name) ? "r" + p.WorkingRegisters.IndexOf(r) : r.Name);
            while (!names.Add(n)) n += "_";
            sb.AppendLine($"    float {n};");
        }
        foreach (var r in p.InputRegisters.Concat(p.OutputRegisters).Concat(p.InOutRegisters))
        {
            string n = Identifier(string.IsNullOrWhiteSpace(r.Name) ? "io" + (p.InputRegisters.Count + p.OutputRegisters.Count) : r.Name);
            if (names.Add(n)) sb.AppendLine($"    // connected register: {r.Name}");
        }
        sb.AppendLine();
        sb.AppendLine("    void Update()");
        sb.AppendLine("    {");
        var working = p.WorkingRegisters.Select((r, i) => Identifier(string.IsNullOrWhiteSpace(r.Name) ? "r" + i : r.Name)).ToArray();
        string Name(GasmOperand op) => op.Kind switch
        {
            GasmOperandKind.Working => working.ElementAtOrDefault(op.Index) ?? "r" + op.Index,
            GasmOperandKind.Constant => op.Value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture) + "f",
            GasmOperandKind.Input => Identifier(p.InputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "input" + op.Index),
            GasmOperandKind.Output => Identifier(p.OutputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "output" + op.Index),
            GasmOperandKind.InOut => Identifier(p.InOutRegisters.ElementAtOrDefault(op.Index)?.Name ?? "inout" + op.Index),
            GasmOperandKind.Label => "L" + op.Index,
            GasmOperandKind.Special => op.Index == 4 ? "Time.DeltaTime" : "0f",
            _ => "0f"
        };
        foreach (var ins in p.Instructions)
        {
            var o = ins.Operands;
            string? line = ins.Op switch
            {
                GasmOp.Label when o.Count > 0 => "L" + o[0].Index + ": ;",
                GasmOp.Goto when o.Count > 0 => "goto L" + o[0].Index + ";",
                GasmOp.If when o.Count >= 2 => $"if ({Name(o[0])} != 0f) goto L{o[1].Index};",
                GasmOp.Set when o.Count >= 2 => $"{Name(o[0])} = {Name(o[1])};",
                GasmOp.Add when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} + {Name(o[2])};",
                GasmOp.Sub when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} - {Name(o[2])};",
                GasmOp.Mul when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} * {Name(o[2])};",
                GasmOp.Div when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} / {Name(o[2])};",
                GasmOp.Equal when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} == {Name(o[2])} ? 1f : 0f;",
                GasmOp.Less when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} < {Name(o[2])} ? 1f : 0f;",
                GasmOp.Greater when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} > {Name(o[2])} ? 1f : 0f;",
                GasmOp.And when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} != 0f && {Name(o[2])} != 0f ? 1f : 0f;",
                GasmOp.Or when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} != 0f || {Name(o[2])} != 0f ? 1f : 0f;",
                GasmOp.Not when o.Count >= 2 => $"{Name(o[0])} = {Name(o[1])} == 0f ? 1f : 0f;",
                GasmOp.Abs when o.Count >= 2 => $"{Name(o[0])} = Mathf.Abs({Name(o[1])});",
                GasmOp.Floor when o.Count >= 2 => $"{Name(o[0])} = Mathf.Floor({Name(o[1])});",
                GasmOp.Sin when o.Count >= 2 => $"{Name(o[0])} = Mathf.Sin({Name(o[1])});",
                GasmOp.Cos when o.Count >= 2 => $"{Name(o[0])} = Mathf.Cos({Name(o[1])});",
                GasmOp.Sqrt when o.Count >= 2 => $"{Name(o[0])} = Mathf.Sqrt({Name(o[1])});",
                GasmOp.Atan2 when o.Count >= 3 => $"{Name(o[0])} = Mathf.Atan2({Name(o[1])}, {Name(o[2])});",
                GasmOp.Pow when o.Count >= 3 => $"{Name(o[0])} = Mathf.Pow({Name(o[1])}, {Name(o[2])});",
                GasmOp.Mod when o.Count >= 3 => $"{Name(o[0])} = {Name(o[1])} % {Name(o[2])};",
                GasmOp.Rand when o.Count >= 1 => $"{Name(o[0])} = Random.Range(0f, 1f);",
                GasmOp.Sleep => $"// wait {o.ElementAtOrDefault(0)?.Value:0.###} s",
                GasmOp.End => "return;",
                _ => null
            };
            if (line != null) sb.AppendLine("        " + line);
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string Identifier(string raw)
    {
        var sb = new StringBuilder();
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, "r_");
        return sb.ToString();
    }
}
