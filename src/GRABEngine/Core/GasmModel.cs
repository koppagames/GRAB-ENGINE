using System;
using System.Collections.Generic;
using System.Linq;

namespace GrabEngine.Core;

public enum GasmOp
{
    Noop = 0, Set = 1, Swap = 2, Add = 3, Sub = 4, Mul = 5, Div = 6,
    Equal = 7, Less = 8, Greater = 9, And = 10, Or = 11, Not = 12,
    Label = 13, Goto = 14, If = 15, Sleep = 16, End = 17, Rand = 18,
    Floor = 19, Mod = 20, Sin = 21, Cos = 22, Sqrt = 23, Atan2 = 24,
    Pow = 25, Abs = 26, VecSet = 27, VecAdd = 28, VecSub = 29,
    VecMul = 30, VecScl = 31, VecLen = 32, VecDot = 33
}

public enum GasmOperandKind
{
    Input = 0, Output = 1, Working = 2, Special = 3, Constant = 4,
    Label = 5, JumpAddress = 6, InOut = 7
}

public enum GasmConnectionType { Node = 0, Player = 1, Lobby = 2 }

public enum GasmPropertyKind
{
    Position = 3, TriggerActive = 4, Rotation = 5, Player = 6, Sign = 7,
    Color = 8, Scale = 9, Physics = 10, Light = 11, PlayerVariables = 12, LobbyVariables = 13
}

public class GasmOperand
{
    public GasmOperandKind Kind { get; set; }
    public int Index { get; set; }
    public float Value { get; set; }
    public static GasmOperand Work(int i) => new() { Kind = GasmOperandKind.Working, Index = i };
    public static GasmOperand Const(float v) => new() { Kind = GasmOperandKind.Constant, Value = v };
    public static GasmOperand Label(int i) => new() { Kind = GasmOperandKind.Label, Index = i };
    public static GasmOperand Special(int i) => new() { Kind = GasmOperandKind.Special, Index = i };
    public static GasmOperand In(int i) => new() { Kind = GasmOperandKind.Input, Index = i };
    public static GasmOperand Out(int i) => new() { Kind = GasmOperandKind.Output, Index = i };
    public static GasmOperand InOut(int i) => new() { Kind = GasmOperandKind.InOut, Index = i };
}

public class GasmInstruction
{
    public GasmOp Op { get; set; }
    /// <summary>Editor-only C# source line. Never written to a GRAB .level; the real
    /// game does not carry debug line info.</summary>
    public int SourceLine { get; set; }
    public List<GasmOperand> Operands { get; set; } = new();
    public GasmInstruction() { }
    public GasmInstruction(GasmOp op, params GasmOperand[] ops) { Op = op; Operands = ops.ToList(); }
}

public class GasmRegister
{
    public string Name { get; set; } = "";
}

public struct GasmComponent
{
    public int Input { get; set; }
    public int Output { get; set; }
    public int InOut { get; set; }
    public static GasmComponent None => new() { Input = -1, Output = -1, InOut = -1 };
    public GasmComponent(int input = -1, int output = -1, int inout = -1) { Input = input; Output = output; InOut = inout; }
}

public class GasmProperty
{
    public ulong ObjectId { get; set; }
    public GasmPropertyKind Kind { get; set; }
    public List<GasmComponent> Components { get; set; } = new();
}

public class GasmConnection
{
    public GasmConnectionType Type { get; set; }
    public ulong ObjectId { get; set; }
    public string Name { get; set; } = "";
    public List<GasmProperty> Properties { get; set; } = new();
}

public class GasmProgram
{
    public List<GasmRegister> InputRegisters { get; set; } = new();
    public List<GasmRegister> OutputRegisters { get; set; } = new();
    public List<GasmRegister> WorkingRegisters { get; set; } = new();
    public List<GasmRegister> InOutRegisters { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    public List<GasmInstruction> Instructions { get; set; } = new();

    public static GasmProgram CreateDefault()
    {
        var p = new GasmProgram();
        for (int i = 0; i < 8; i++) p.WorkingRegisters.Add(new GasmRegister { Name = "R" + i });
        return p;
    }

    public int FindWorking(string name) => WorkingRegisters.FindIndex(r => r.Name == name);
    public int AddWorking(string name)
    {
        int i = FindWorking(name);
        if (i >= 0) return i;
        WorkingRegisters.Add(new GasmRegister { Name = name });
        return WorkingRegisters.Count - 1;
    }
}

public class GasmCompileResult
{
    public GasmProgram Program { get; set; } = GasmProgram.CreateDefault();
    public List<GasmConnection> Connections { get; set; } = new();
    public List<string> GasmText { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public LevelNode? BlockNode { get; set; }
    /// <summary>True when this program intentionally exceeds the retail editor's
    /// 150-instruction / 10-connection UI caps. The GASM runtime stores programs
    /// and registers as dynamic arrays, so a loaded level can execute it.</summary>
    public bool AllowExtendedLimits { get; set; }
    /// <summary>Extra Code Blocks generated because the program exceeded GRAB's limits.</summary>
    public List<GasmCompileResult> Continuations { get; set; } = new();
    public bool Success => Diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
}

public enum DiagnosticSeverity { Info, Warning, Error }

public class Diagnostic
{
    public DiagnosticSeverity Severity { get; set; }
    public string Message { get; set; } = "";
    public int Line { get; set; } = 1;
    public int Column { get; set; } = 1;
    public int Length { get; set; } = 1;
    public string Code { get; set; } = "";
    public string File { get; set; } = "Script.cs";
    public override string ToString() => $"{File}({Line},{Column}): {Severity.ToString().ToLower()} {Code}: {Message}";
}
