using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.Code;

public enum RegKind { Input, Output, Working, InOut }

public sealed class PropSpec
{
    public string Key { get; }
    public RegKind? Kind { get; }
    public string Suffix { get; }
    public PropSpec(string key, RegKind? kind, string suffix) { Key = key; Kind = kind; Suffix = suffix; }
}

public sealed class GasmEmitter
{
    public GasmProgram Program { get; } = new();
    public List<GasmConnection> Connections { get; } = new();
    public List<Diagnostic> Diagnostics { get; }
    public IReadOnlyDictionary<LevelNode, ulong> ObjectIds { get; }

    private readonly Dictionary<LevelNode, GasmConnection> _nodeConnections = new();
    private readonly Dictionary<(GasmConnection, GasmPropertyKind), GasmProperty> _properties = new();
    private readonly HashSet<string> _connectionNames = new(StringComparer.OrdinalIgnoreCase);
    private GasmConnection? _player;
    private GasmConnection? _lobby;
    private int _tempCounter;
    private int _labelCounter;
    private readonly Stack<int> _freeTemps = new();

    /// <summary>Current C# source line, written onto every emitted instruction.
    /// Play mode uses it for breakpoints and the execution-line marker.</summary>
    public int SourceLine { get; set; }

    public GasmEmitter(IReadOnlyDictionary<LevelNode, ulong> objectIds, List<Diagnostic> diags)
    {
        ObjectIds = objectIds;
        Diagnostics = diags;
        for (int i = 0; i < 8; i++) Program.WorkingRegisters.Add(new GasmRegister { Name = "R" + i });
    }

    public int AllocWorking(string name)
    {
        var existing = Program.WorkingRegisters.FindIndex(r => r.Name == name);
        if (existing >= 0) return existing;
        Program.WorkingRegisters.Add(new GasmRegister { Name = name });
        return Program.WorkingRegisters.Count - 1;
    }

    public int AllocTemp()
    {
        if (_freeTemps.Count > 0) return _freeTemps.Pop();
        Program.WorkingRegisters.Add(new GasmRegister { Name = "__t" + _tempCounter++ });
        if (Program.WorkingRegisters.Count > 480)
        {
            Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Program needs more than 480 registers. Simplify the script.", File = "Script.cs" });
        }
        return Program.WorkingRegisters.Count - 1;
    }

    public void FreeTemp(int reg) { if (reg >= 0 && reg < Program.WorkingRegisters.Count) _freeTemps.Push(reg); }

    public void Emit(GasmOp op, params GasmOperand[] operands)
        => Program.Instructions.Add(new GasmInstruction(op, operands) { SourceLine = SourceLine });

    public int NewLabel(string hint)
    {
        Program.Labels.Add(string.IsNullOrWhiteSpace(hint) ? "L" + _labelCounter : hint + "_" + _labelCounter);
        _labelCounter++;
        return Program.Labels.Count - 1;
    }

    public void Mark(int label) => Emit(GasmOp.Label, GasmOperand.Label(label));
    public void Goto(int label) => Emit(GasmOp.Goto, GasmOperand.Label(label));

    public static GasmOperand Const(float v) => GasmOperand.Const(v);
    public static GasmOperand Work(int i) => GasmOperand.Work(i);
    public static GasmOperand Special(int i) => GasmOperand.Special(i);

    public GasmConnection NodeConnection(LevelNode node)
    {
        if (_nodeConnections.TryGetValue(node, out var c)) return c;
        if (!ObjectIds.TryGetValue(node, out var id))
        {
            Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"Object '{node.DisplayName}' is not part of the level", File = "Script.cs" });
            id = 0;
        }
        string name = ShortConnectionName(node);
        c = new GasmConnection { Type = GasmConnectionType.Node, ObjectId = id, Name = name };
        Connections.Add(c);
        _nodeConnections[node] = c;
        return c;
    }

    /// <summary>
    /// GRAB levels use short connection names (blk1, trg1, lght...). Some game
    /// versions truncate longer names and then the matching registers become
    /// disconnected. Generate a unique four-character name per object.
    /// </summary>
    private string ShortConnectionName(LevelNode node)
    {
        string prefix = node.Kind switch
        {
            NodeKind.Static or NodeKind.Crumbling => "bl",
            NodeKind.Trigger => "tr",
            NodeKind.Light => "lg",
            // Generated console displays keep a reserved prefix after .level export
            // so Play mode can mirror their text into the editor Console.
            NodeKind.Sign when node.Name.StartsWith("GASM Console", StringComparison.OrdinalIgnoreCase) => "cn",
            NodeKind.Sign => "sg",
            NodeKind.ParticleEmitter => "pt",
            NodeKind.Sound => "sn",
            NodeKind.Gravity => "gv",
            NodeKind.Group => "gp",
            NodeKind.Start => "st",
            NodeKind.Finish => "fi",
            NodeKind.GASM => "cd",
            NodeKind.LobbyTerminal => "tm",
            _ => "ob"
        };
        // 36^2 = 1296 objects per type with a readable "bl00" name. Beyond that,
        // collapse to one letter + three base-36 digits (still four characters).
        for (int i = 0; i < 36 * 36; i++)
        {
            string candidate = prefix + Base36(i, 2);
            if (_connectionNames.Add(candidate)) return candidate;
        }
        for (int i = 0; i < 36 * 36 * 36; i++)
        {
            string candidate = prefix[..1] + Base36(i, 3);
            if (_connectionNames.Add(candidate)) return candidate;
        }
        Diagnostics.Add(new Diagnostic
        {
            Severity = DiagnosticSeverity.Error,
            Message = "Too many object connections for one Code Block. GRAB allows 10.",
            File = "Script.cs"
        });
        return "ob00";
    }

    private static string Base36(int value, int width)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        Span<char> chars = stackalloc char[width];
        for (int i = width - 1; i >= 0; i--)
        {
            chars[i] = digits[value % 36];
            value /= 36;
        }
        return new string(chars);
    }

    public GasmProperty EnsureProperty(GasmConnection c, GasmPropertyKind kind, PropSpec[] specs)
    {
        if (_properties.TryGetValue((c, kind), out var existing)) return existing;
        var prop = new GasmProperty { ObjectId = c.ObjectId, Kind = kind };
        for (int i = 0; i < specs.Length; i++)
        {
            var spec = specs[i];
            if (spec.Kind == null) { prop.Components.Add(GasmComponent.None); continue; }
            string name = c.Name + spec.Suffix;
            int index = AddRegister(name, spec.Kind.Value);
            prop.Components.Add(new GasmComponent(
                spec.Kind == RegKind.Input ? index : -1,
                spec.Kind == RegKind.Output ? index : -1,
                spec.Kind == RegKind.InOut ? index : -1));
        }
        c.Properties.Add(prop);
        _properties[(c, kind)] = prop;
        return prop;
    }

    public int AddRegister(string name, RegKind kind)
    {
        var list = kind switch
        {
            RegKind.Input => Program.InputRegisters,
            RegKind.Output => Program.OutputRegisters,
            RegKind.InOut => Program.InOutRegisters,
            _ => Program.WorkingRegisters
        };
        int existing = list.FindIndex(r => r.Name == name);
        if (existing >= 0) return existing;
        list.Add(new GasmRegister { Name = name });
        return list.Count - 1;
    }

    public int GetRegister(GasmProperty prop, int component, RegKind kind, string name)
    {
        if (component < 0 || component >= prop.Components.Count)
        {
            Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "GASM property layout mismatch", File = "Script.cs" });
            return 0;
        }
        var c = prop.Components[component];
        int idx = kind switch { RegKind.Input => c.Input, RegKind.Output => c.Output, _ => c.InOut };
        if (idx >= 0) return idx;
        idx = AddRegister(name, kind);
        if (kind == RegKind.Input) c.Input = idx;
        else if (kind == RegKind.Output) c.Output = idx;
        else c.InOut = idx;
        prop.Components[component] = c;
        return idx;
    }

    public GasmConnection PlayerConnection()
    {
        if (_player != null) return _player;
        _player = new GasmConnection { Type = GasmConnectionType.Player, ObjectId = 0, Name = "Player" };
        Connections.Add(_player);
        EnsureProperty(_player, GasmPropertyKind.Player, PlayerSpecs);
        return _player;
    }

    public GasmConnection LobbyConnection()
    {
        if (_lobby != null) return _lobby;
        _lobby = new GasmConnection { Type = GasmConnectionType.Lobby, ObjectId = 0, Name = "Lobby" };
        Connections.Add(_lobby);
        EnsureProperty(_lobby, GasmPropertyKind.LobbyVariables, LobbySpecs);
        return _lobby;
    }

    public static readonly PropSpec[] PositionSpecs =
    {
        new("X", RegKind.InOut, ".Pos.X"), new("Y", RegKind.InOut, ".Pos.Y"), new("Z", RegKind.InOut, ".Pos.Z")
    };
    public static readonly PropSpec[] RotationSpecs =
    {
        new("X", RegKind.InOut, ".Rot.X"), new("Y", RegKind.InOut, ".Rot.Y"), new("Z", RegKind.InOut, ".Rot.Z")
    };
    public static readonly PropSpec[] ScaleSpecs =
    {
        new("X", RegKind.Input, ".Sca.X"), new("Y", RegKind.Input, ".Sca.Y"), new("Z", RegKind.Input, ".Sca.Z")
    };
    public static readonly PropSpec[] ColorSpecs =
    {
        new("R", RegKind.InOut, ".Col.R"), new("G", RegKind.InOut, ".Col.G"), new("B", RegKind.InOut, ".Col.B"), new("Select", RegKind.InOut, ".Col.Select")
    };
    public static readonly PropSpec[] TriggerSpecs =
    {
        new("Active", RegKind.Input, ".Trg.Active"), new("PlayerId", RegKind.Input, ".Trg.PlayerId"),
        new("Force", RegKind.InOut, ".Trg.Force"), new("Part", RegKind.Input, ".Trg.Part"),
        new("", null, ""), new("", null, "")
    };
    public static readonly PropSpec[] PhysicsSpecs =
    {
        new("Enabled", RegKind.Output, ".Phy.Ena"), new("State", RegKind.Input, ".Phy.Sta")
    };
    public static readonly PropSpec[] LightSpecs =
    {
        new("R", RegKind.InOut, ".Col.R"), new("G", RegKind.InOut, ".Col.G"), new("B", RegKind.InOut, ".Col.B"),
        new("Brightness", RegKind.InOut, ".Brt"), new("Range", RegKind.InOut, ".Range")
    };
    public static readonly PropSpec[] SignSpecs =
    {
        new("Write", RegKind.Output, ".Sign.Write"), new("Select", RegKind.Output, ".Sign.Sel"),
        new("Control", RegKind.Output, ".Sign.Control"), new("Number", RegKind.Output, ".Sign.Number")
    };
    public static readonly PropSpec[] LobbySpecs =
    {
        new("Var1", RegKind.InOut, ".Var1"), new("Var2", RegKind.InOut, ".Var2"), new("Var3", RegKind.InOut, ".Var3"),
        new("Var4", RegKind.InOut, ".Var4"), new("Var5", RegKind.InOut, ".Var5")
    };
    public static readonly PropSpec[] PlayerSpecs =
    {
        new("SelectIndex", RegKind.Output, ".SelIndex"), new("SelectId", RegKind.Output, ".SelId"),
        new("SelectPart", RegKind.Output, ".SelPart"), new("Control", RegKind.Output, ".Control"),
        new("Count", RegKind.Input, ".Count"), new("Valid", RegKind.Input, ".Valid"),
        new("Id", RegKind.Input, ".Id"), new("Pos.X", RegKind.Input, ".Pos.X"),
        new("Pos.Y", RegKind.Input, ".Pos.Y"), new("Pos.Z", RegKind.Input, ".Pos.Z"),
        new("Rot.X", RegKind.Input, ".Rot.X"), new("Rot.Y", RegKind.Input, ".Rot.Y"),
        new("Rot.Z", RegKind.Input, ".Rot.Z"), new("Name", RegKind.Input, ".Name"),
        new("Col.Select", RegKind.Output, ".Col.Sel"), new("Col.R", RegKind.Input, ".Col.R"),
        new("Col.G", RegKind.Input, ".Col.G"), new("Col.B", RegKind.Input, ".Col.B"),
        new("LocalId", RegKind.Input, ".LocalId"), new("Vel.X", RegKind.InOut, ".Vel.X"),
        new("Vel.Y", RegKind.InOut, ".Vel.Y"), new("Vel.Z", RegKind.InOut, ".Vel.Z"),
        new("Tel.Pos.X", RegKind.Output, ".Tel.Pos.X"), new("Tel.Pos.Y", RegKind.Output, ".Tel.Pos.Y"),
        new("Tel.Pos.Z", RegKind.Output, ".Tel.Pos.Z"), new("Tel.Rot.Y", RegKind.Output, ".Tel.Rot.Y"),
        new("Tel.Apply", RegKind.Output, ".Tel.Apply"), new("Respawn", RegKind.Output, ".Respawn")
    };

    public string[] BuildText()
    {
        var lines = new List<string>();
        foreach (var ins in Program.Instructions)
        {
            if (ins.Op == GasmOp.Label && ins.Operands.Count > 0)
            {
                lines.Add("LABEL " + LabelName(ins.Operands[0]));
                continue;
            }
            if (ins.Op == GasmOp.Noop && ins.Operands.Count == 0) { lines.Add("NOOP"); continue; }
            var sb = new StringBuilder(OpName(ins.Op));
            foreach (var op in ins.Operands) sb.Append(' ').Append(OperandName(op));
            lines.Add(sb.ToString());
        }
        return lines.ToArray();
    }

    private string OperandName(GasmOperand op) => op.Kind switch
    {
        GasmOperandKind.Input => Program.InputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "in" + op.Index,
        GasmOperandKind.Output => Program.OutputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "out" + op.Index,
        GasmOperandKind.Working => Program.WorkingRegisters.ElementAtOrDefault(op.Index)?.Name ?? "R" + op.Index,
        GasmOperandKind.InOut => Program.InOutRegisters.ElementAtOrDefault(op.Index)?.Name ?? "io" + op.Index,
        GasmOperandKind.Special => SpecialName(op.Index),
        GasmOperandKind.Constant => op.Value.ToString("0.#####", CultureInfo.InvariantCulture),
        GasmOperandKind.Label => LabelName(op),
        GasmOperandKind.JumpAddress => "@" + op.Index,
        _ => op.Index.ToString()
    };

    private string LabelName(GasmOperand op) => Program.Labels.ElementAtOrDefault(op.Index) ?? "L" + op.Index;
    private static string SpecialName(int i) => i switch { 0 => "ProgramCounter", 1 => "Halt", 2 => "HaltFrame", 3 => "SleepTimer", _ => "DeltaTime" };

    public static string OpName(GasmOp op) => op switch
    {
        GasmOp.Noop => "NOOP", GasmOp.Set => "SET", GasmOp.Swap => "SWAP", GasmOp.Add => "ADD",
        GasmOp.Sub => "SUB", GasmOp.Mul => "MUL", GasmOp.Div => "DIV", GasmOp.Equal => "EQUAL",
        GasmOp.Less => "LESS", GasmOp.Greater => "GREATER", GasmOp.And => "AND", GasmOp.Or => "OR",
        GasmOp.Not => "NOT", GasmOp.Label => "LABEL", GasmOp.Goto => "GOTO", GasmOp.If => "IF",
        GasmOp.Sleep => "SLEEP", GasmOp.End => "END", GasmOp.Rand => "RAND", GasmOp.Floor => "FLOOR",
        GasmOp.Mod => "MOD", GasmOp.Sin => "SIN", GasmOp.Cos => "COS", GasmOp.Sqrt => "SQRT",
        GasmOp.Atan2 => "ATAN2", GasmOp.Pow => "POW", GasmOp.Abs => "ABS",
        GasmOp.VecSet => "VEC_SET", GasmOp.VecAdd => "VEC_ADD", GasmOp.VecSub => "VEC_SUB",
        GasmOp.VecMul => "VEC_MUL", GasmOp.VecScl => "VEC_SCL", GasmOp.VecLen => "VEC_LEN",
        GasmOp.VecDot => "VEC_DOT", _ => "NOOP"
    };
}
