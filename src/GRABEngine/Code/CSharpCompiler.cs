using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.Code;

/// <summary>One piece of a text expression: literal characters or a live number.</summary>
public sealed class TextPart
{
    public string? Text { get; set; }
    public CValue? Value { get; set; }
}

public sealed class CValue
{
    public CsType Type { get; set; } = CsType.Error;
    public bool IsConst { get; set; }
    public float Const { get; set; }
    public int Reg { get; set; } = -1;
    public GasmOperandKind RegKind { get; set; } = GasmOperandKind.Working;
    public CValue[]? Parts { get; set; }
    /// <summary>Element registers when Type is an array.</summary>
    public int[]? ArrayRegs { get; set; }
    public CsType? ElementType { get; set; }
    /// <summary>Text with live numbers, e.g. "Time: " + Time.Time.</summary>
    public List<TextPart>? TextParts { get; set; }
    public string Str { get; set; } = "";
    public LevelNode? Node { get; set; }
    public CInstance? Inst { get; set; }
    public CCollection? Collection { get; set; }
    public bool IsTemp { get; set; }
    public bool IsNull { get; set; }
    public bool IsNumeric => Type.Kind is CsTypeKind.Float or CsTypeKind.Bool;

    public static CValue F(float f) => new() { Type = CsType.Float, IsConst = true, Const = f };
    public static CValue B(bool b) => new() { Type = CsType.Bool, IsConst = true, Const = b ? 1 : 0 };
    public static CValue S(string s) => new() { Type = CsType.String, Str = s, IsConst = true };
    public static CValue A(CsType element, int[] regs) => new() { Type = CsType.ArrayOf(element), ElementType = element, ArrayRegs = regs };
    public static CValue V(CValue x, CValue y, CValue z) => new() { Type = CsType.Vector3, Parts = new[] { x, y, z } };
    public static CValue C(CValue r, CValue g, CValue b, CValue a) => new() { Type = CsType.Color, Parts = new[] { r, g, b, a } };
    public static CValue O(LevelNode n, CsType t) => new() { Type = t, Node = n };

    public GasmOperand Operand() => IsConst
        ? GasmOperand.Const(Const)
        : new GasmOperand { Kind = RegKind, Index = Reg };

    public CValue Clone() => new()
    {
        Type = Type, IsConst = IsConst, Const = Const, Reg = Reg, RegKind = RegKind,
        Parts = Parts?.Select(p => p.Clone()).ToArray(), ArrayRegs = ArrayRegs, ElementType = ElementType,
        TextParts = TextParts?.Select(p => new TextPart { Text = p.Text, Value = p.Value?.Clone() }).ToList(),
        Str = Str, Node = Node, Inst = Inst, Collection = Collection, IsTemp = IsTemp, IsNull = IsNull
    };
}

public sealed class CVar
{
    public string Name = "";
    public CsType Type = CsType.Float;
    public int[] Regs = Array.Empty<int>();
    public bool IsConst;
    public CValue? ConstValue;
    /// <summary>Compile-time value for string variables (GASM has no string registers).</summary>
    public string? StrValue;
    /// <summary>Element registers for fixed-size array variables.</summary>
    public int[] ArrayRegs = Array.Empty<int>();
    public CsType? ElementType;
    public LevelNode? ObjectNode;
    public bool IsObject;
    public bool IsField;
    public bool IsParameter;
    public bool IsReadonly;
    public bool IsStatic;
    public CInstance? Instance;
    public CCollection? Collection;
    public string TypeKey = "";
}

public sealed class MethodBinding
{
    public MethodDecl Decl { get; set; } = null!;
    public TypeDef Owner { get; set; } = null!;
}

/// <summary>A monomorphised class / struct / interface definition.</summary>
public sealed class TypeDef
{
    public string Name { get; set; } = "";
    public TypeDeclKind Kind { get; set; }
    public ClassDecl Decl { get; set; } = null!;
    public List<string> GenericParams { get; set; } = new();
    public Dictionary<string, CsType> Bindings { get; set; } = new(StringComparer.Ordinal);
    public TypeDef? Base { get; set; }
    public List<TypeDef> Interfaces { get; set; } = new();
    public List<(FieldDecl Decl, CsType Type)> Fields { get; set; } = new();
    public List<MethodBinding> Methods { get; set; } = new();
    public List<MethodBinding> Constructors { get; set; } = new();
    public bool IsAbstract { get; set; }
    public CsType Type { get; set; } = CsType.Float;
    public string Key { get; set; } = "";

    public MethodBinding? FindMethod(string name, int argc) =>
        Methods.FirstOrDefault(m => m.Decl.Name == name && CSharpCompiler.AcceptsArgs(m.Decl, argc));

    public bool IsSubclassOf(TypeDef other)
    {
        for (var d = this; d != null; d = d.Base)
            if (ReferenceEquals(d, other) || d.Key == other.Key) return true;
        return false;
    }
}

/// <summary>One allocated object or struct value.</summary>
public sealed class CInstance
{
    public TypeDef? Def;
    public CsType Type = CsType.Float;
    public Dictionary<string, CVar> Fields = new(StringComparer.Ordinal);
    public GasmEmitter? Emitter;

    /// <summary>Struct value copy: every field is copied into this instance.</summary>
    public void CopyFrom(CInstance source)
    {
        foreach (var kv in Fields)
        {
            if (!source.Fields.TryGetValue(kv.Key, out var src)) continue;
            var dst = kv.Value;
            if (dst.Instance != null && src.Instance != null) { dst.Instance.CopyFrom(src.Instance); continue; }
            dst.Instance = src.Instance;
            dst.Collection = src.Collection;
            dst.ObjectNode = src.ObjectNode;
            dst.StrValue = src.StrValue;
            dst.ConstValue = src.ConstValue?.Clone();
            if (Emitter != null)
            {
                for (int i = 0; i < Math.Min(dst.Regs.Length, src.Regs.Length); i++)
                    Emitter.Emit(GasmOp.Set, GasmEmitter.Work(dst.Regs[i]), GasmEmitter.Work(src.Regs[i]));
                for (int i = 0; i < Math.Min(dst.ArrayRegs.Length, src.ArrayRegs.Length); i++)
                    Emitter.Emit(GasmOp.Set, GasmEmitter.Work(dst.ArrayRegs[i]), GasmEmitter.Work(src.ArrayRegs[i]));
            }
        }
    }
}

/// <summary>A fixed-capacity List or Dictionary living in working registers.</summary>
public sealed class CCollection
{
    public CsType Type = CsType.Float;
    public bool IsDictionary;
    public CsType ElementType = CsType.Float;
    public CsType? KeyType;
    public int Capacity;
    public int Stride = 1;
    public int[] Slots = Array.Empty<int>();
    public int[] KeySlots = Array.Empty<int>();
    public int CountReg = -1;
}

internal abstract class CLValue
{
    public CsType Type = CsType.Float;
    public bool ReadOnly;
    public abstract CValue Read();
    public abstract void Write(CValue value);
}

internal sealed class ScalarSlotLValue : CLValue
{
    private readonly GasmEmitter _e;
    public int Index;
    public GasmOperandKind Kind;
    public ScalarSlotLValue(GasmEmitter e, int index, GasmOperandKind kind, CsType type, bool readOnly = false)
    { _e = e; Index = index; Kind = kind; Type = type; ReadOnly = readOnly; }

    public override CValue Read() => new() { Type = Type, Reg = Index, RegKind = Kind };
    public override void Write(CValue value)
    {
        if (ReadOnly) { _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "This property is read-only", File = "Script.cs" }); return; }
        _e.Emit(GasmOp.Set, new GasmOperand { Kind = Kind, Index = Index }, value.Operand());
    }
}

internal sealed class VectorSlotLValue : CLValue
{
    public CLValue[] Parts;
    public VectorSlotLValue(CLValue[] parts, CsType type) { Parts = parts; Type = type; }
    public override CValue Read() => new() { Type = Type, Parts = Parts.Select(p => p.Read()).ToArray() };
    public override void Write(CValue value)
    {
        if (value.Parts == null) return;
        for (int i = 0; i < Math.Min(Parts.Length, value.Parts.Length); i++) Parts[i].Write(value.Parts[i]);
    }
}

internal sealed class ScaledLValue : CLValue
{
    private readonly CLValue _inner;
    private readonly float _scale;
    private readonly GasmEmitter _e;
    public ScaledLValue(GasmEmitter e, CLValue inner, float scale) { _e = e; _inner = inner; _scale = scale; Type = CsType.Float; }
    public override CValue Read()
    {
        var raw = _inner.Read();
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Mul, GasmEmitter.Work(t), raw.Operand(), GasmEmitter.Const(_scale));
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }
    public override void Write(CValue value)
    {
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Mul, GasmEmitter.Work(t), value.Operand(), GasmEmitter.Const(1f / _scale));
        _inner.Write(new CValue { Type = CsType.Float, Reg = t, IsTemp = true });
        _e.FreeTemp(t);
    }
}

internal sealed class VarLValue : CLValue
{
    private readonly CVar _v;
    private readonly GasmEmitter _e;
    public VarLValue(CVar v, GasmEmitter e) { _v = v; _e = e; Type = v.Type; ReadOnly = v.IsConst || v.IsReadonly; }
    public override CValue Read()
    {
        if (_v.IsConst && _v.ConstValue != null) return _v.ConstValue.Clone();
        if (_v.IsObject) return CValue.O(_v.ObjectNode!, _v.Type);
        if (_v.Type.Kind == CsTypeKind.String) return CValue.S(_v.StrValue ?? "");
        if (_v.Type.Kind == CsTypeKind.Array)
            return CValue.A(_v.ElementType ?? CsType.Float, _v.ArrayRegs);
        if (_v.Collection != null) return new CValue { Type = _v.Type, Collection = _v.Collection };
        if (_v.Instance != null) return new CValue { Type = _v.Type, Inst = _v.Instance };
        if (_v.Type.Kind is CsTypeKind.Vector3)
            return new CValue { Type = _v.Type, Parts = _v.Regs.Select(r => (CValue)new CValue { Type = CsType.Float, Reg = r }).ToArray() };
        if (_v.Type.Kind is CsTypeKind.Color)
            return new CValue { Type = _v.Type, Parts = _v.Regs.Select(r => (CValue)new CValue { Type = CsType.Float, Reg = r }).ToArray() };
        return new CValue { Type = _v.Type, Reg = _v.Regs.Length > 0 ? _v.Regs[0] : -1 };
    }
    public override void Write(CValue value)
    {
        if (_v.IsConst) { _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Cannot assign to a const", File = "Script.cs" }); return; }
        if (_v.IsReadonly) { _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"'{_v.Name}' is readonly", File = "Script.cs" }); return; }
        if (_v.IsObject)
        {
            if (value.Node != null) _v.ObjectNode = value.Node;
            return;
        }
        if (value.IsNull) { _v.Instance = null; return; }
        if (_v.Type.Kind == CsTypeKind.String)
        {
            if (value.Type.Kind == CsTypeKind.String) _v.StrValue = value.Str;
            else _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"Cannot assign {value.Type} to a string", File = "Script.cs" });
            return;
        }
        if (_v.Type.IsUserType && value.Inst != null)
        {
            if (_v.Type.Kind == CsTypeKind.Struct && _v.Instance != null) _v.Instance.CopyFrom(value.Inst);
            else _v.Instance = value.Inst;
            return;
        }
        if (_v.Type.IsCollection && value.Collection != null)
        {
            _v.Collection = value.Collection;
            return;
        }
        if (_v.Type.Kind == CsTypeKind.Array)
        {
            var src = value.ArrayRegs;
            if (src != null && src.Length == _v.ArrayRegs.Length)
            {
                for (int i = 0; i < src.Length; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(_v.ArrayRegs[i]), GasmEmitter.Work(src[i]));
            }
            else _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Array assignment needs an array of the same length", File = "Script.cs" });
            return;
        }
        if (_v.Type.Kind is CsTypeKind.Vector3 or CsTypeKind.Color)
        {
            int n = _v.Type.Kind == CsTypeKind.Color ? 4 : 3;
            var parts = value.Parts;
            if (parts == null)
            {
                for (int i = 0; i < n; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(_v.Regs[i]), value.Operand());
            }
            else
            {
                for (int i = 0; i < n; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(_v.Regs[i]), parts[Math.Min(i, parts.Length - 1)].Operand());
            }
            return;
        }
        if (_v.Regs.Length > 0)
        {
            _e.Emit(GasmOp.Set, GasmEmitter.Work(_v.Regs[0]), value.Operand());
            _v.IsConst = false;
        }
    }
}

/// <summary>
/// One slot of a fixed-size array. A constant index compiles straight to the
/// element register; a runtime index becomes a short compare-and-branch chain,
/// because GASM has no indirect register addressing.
/// </summary>
internal sealed class ArrayElementLValue : CLValue
{
    private readonly CVar _array;
    private readonly CValue _index;
    private readonly GasmEmitter _e;

    public ArrayElementLValue(GasmEmitter e, CVar array, CValue index, bool readOnly)
    {
        _e = e;
        _array = array;
        _index = index;
        Type = array.ElementType ?? CsType.Float;
        ReadOnly = readOnly;
    }

    private bool TryConstIndex(out int index)
    {
        index = 0;
        if (!_index.IsConst) return false;
        index = (int)MathF.Round(_index.Const);
        return true;
    }

    private void BoundsError(int index) => _e.Diagnostics.Add(new Diagnostic
    {
        Severity = DiagnosticSeverity.Error,
        Message = $"Array index {index} is outside 0..{_array.ArrayRegs.Length - 1}",
        File = "Script.cs"
    });

    public override CValue Read()
    {
        if (TryConstIndex(out int i))
        {
            if (i < 0 || i >= _array.ArrayRegs.Length) { BoundsError(i); return CValue.F(0); }
            return new CValue { Type = Type, Reg = _array.ArrayRegs[i] };
        }

        int end = _e.NewLabel("arr_end");
        int result = _e.AllocTemp();
        var labels = new int[_array.ArrayRegs.Length];
        for (int k = 0; k < labels.Length; k++)
        {
            labels[k] = _e.NewLabel("arr_" + k);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), _index.Operand(), GasmEmitter.Const(k));
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(labels[k]));
            _e.FreeTemp(cmp);
        }
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(0));
        _e.Goto(end);
        for (int k = 0; k < labels.Length; k++)
        {
            _e.Mark(labels[k]);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Work(_array.ArrayRegs[k]));
            _e.Goto(end);
        }
        _e.Mark(end);
        return new CValue { Type = Type, Reg = result, IsTemp = true };
    }

    public override void Write(CValue value)
    {
        if (ReadOnly)
        {
            _e.Diagnostics.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "This array element is read-only", File = "Script.cs" });
            return;
        }
        if (TryConstIndex(out int i))
        {
            if (i < 0 || i >= _array.ArrayRegs.Length) { BoundsError(i); return; }
            _e.Emit(GasmOp.Set, GasmEmitter.Work(_array.ArrayRegs[i]), value.Operand());
            return;
        }

        int end = _e.NewLabel("arrw_end");
        var labels = new int[_array.ArrayRegs.Length];
        for (int k = 0; k < labels.Length; k++)
        {
            labels[k] = _e.NewLabel("arrw_" + k);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), _index.Operand(), GasmEmitter.Const(k));
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(labels[k]));
            _e.FreeTemp(cmp);
        }
        _e.Goto(end);
        for (int k = 0; k < labels.Length; k++)
        {
            _e.Mark(labels[k]);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(_array.ArrayRegs[k]), value.Operand());
            _e.Goto(end);
        }
        _e.Mark(end);
    }
}

/// <summary>
/// The Write/Text property. A string is baked into the level as the object's text
/// (GRAB signs display it); a number keeps GRAB's original ASCII / number-mode
/// register write, so board.Write = 72; still appends 'H'.
/// </summary>
internal sealed class WriteLValue : CLValue
{
    private readonly LevelNode _node;
    private readonly GasmEmitter _e;
    private readonly CLValue? _numeric;

    public WriteLValue(GasmEmitter e, LevelNode node, CLValue? numeric)
    {
        _e = e;
        _node = node;
        _numeric = numeric;
        Type = numeric != null ? CsType.Float : CsType.String;
    }

    public override CValue Read() => _numeric?.Read() ?? CValue.S(_node.Text);

    public override void Write(CValue value)
    {
        if (value.TextParts != null && value.TextParts.Count > 0)
        {
            if (_node.Kind == NodeKind.Sign)
            {
                EmitSignText(value.TextParts);
                _node.Text = CSharpCompiler.PreviewText(value.TextParts);
            }
            else _node.EditorLabel = CSharpCompiler.PreviewText(value.TextParts);
            return;
        }
        if (value.Type.Kind != CsTypeKind.String)
        {
            if (_numeric != null) { _numeric.Write(value); return; }
            _e.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = "Numbers can only be written to Signs (ASCII / number mode). Use a string, e.g. obj.Write = \"Hello\"",
                File = "Script.cs"
            });
            return;
        }
        if (!value.IsConst)
        {
            _e.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = "GRAB text has to be known when the level is compiled, so Write needs a constant string (literals, +, and string methods of literals).",
                File = "Script.cs"
            });
            return;
        }
        if (value.Str.Length > GrabLimits.MaxSignCharacters)
        {
            _e.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"The text is {value.Str.Length} characters; GRAB signs stop at {GrabLimits.MaxSignCharacters}.",
                File = "Script.cs"
            });
        }
        if (_node.Kind == NodeKind.Sign) _node.Text = value.Str;
        else _node.EditorLabel = value.Str;
    }

    private void EmitSignText(List<TextPart> parts)
        => EmitSignText(_e, _node, parts, clearFirst: false, newline: false);

    /// <summary>
    /// Rewrites a live text line: optionally clear the sign, type the literal
    /// characters in ASCII mode, switch to number mode for each live value and
    /// optionally end the line. This is exactly what a hand-written GASM Code
    /// Block does, so the same text appears in GRAB ENGINE and in the retail game.
    /// </summary>
    internal static void EmitSignText(GasmEmitter e, LevelNode node, List<TextPart> parts, bool clearFirst, bool newline)
    {
        var c = e.NodeConnection(node);
        var prop = e.EnsureProperty(c, GasmPropertyKind.Sign, GasmEmitter.SignSpecs);
        int writeReg = e.GetRegister(prop, 0, RegKind.Output, c.Name + ".Sign.Write");
        int selectReg = e.GetRegister(prop, 1, RegKind.Output, c.Name + ".Sign.Select");
        int controlReg = e.GetRegister(prop, 2, RegKind.Output, c.Name + ".Sign.Control");
        static GasmOperand Out(int i) => new() { Kind = GasmOperandKind.Output, Index = i };

        if (clearFirst)
            e.Emit(GasmOp.Set, Out(controlReg), GasmEmitter.Const(1));
        // Select persists between frames; force ASCII mode before typing the prefix.
        e.Emit(GasmOp.Set, Out(selectReg), GasmEmitter.Const(0));
        bool asciiMode = true;
        int characters = 0;
        foreach (var part in parts)
        {
            if (part.Value != null)
            {
                if (asciiMode)
                {
                    e.Emit(GasmOp.Set, Out(selectReg), GasmEmitter.Const(1));
                    asciiMode = false;
                }
                e.Emit(GasmOp.Set, Out(writeReg), part.Value.Operand());
                continue;
            }
            if (!asciiMode)
            {
                e.Emit(GasmOp.Set, Out(selectReg), GasmEmitter.Const(0));
                asciiMode = true;
            }
            foreach (char ch in part.Text ?? "")
            {
                e.Emit(GasmOp.Set, Out(writeReg), GasmEmitter.Const(ch));
                characters++;
            }
        }
        if (newline)
        {
            if (!asciiMode)
            {
                e.Emit(GasmOp.Set, Out(selectReg), GasmEmitter.Const(0));
                asciiMode = true;
            }
            e.Emit(GasmOp.Set, Out(writeReg), GasmEmitter.Const(10));
        }
        if (characters > 60)
        {
            e.Diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Typing {characters} characters every frame costs {characters} GASM instructions; GRAB allows {GrabLimits.MaxInstructions} per Code Block.",
                File = "Script.cs"
            });
        }
    }
}

public sealed class CSharpCompiler
{
    private readonly List<Diagnostic> _diags = new();
    private GrabLevel _level = null!;
    private LevelNode? _codeBlock;
    private GasmEmitter _e = null!;
    private CompilationUnit _unit = null!;
    private ClassDecl? _class;
    private readonly Dictionary<string, ClassDecl> _typeDecls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeDef> _typeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CVar> _statics = new(StringComparer.Ordinal);
    private CInstance? _thisInstance;
    private TypeDef? _scriptDef;
    private readonly List<Dictionary<string, CsType>> _typeBindings = new();
    private int _instanceCounter;
    private readonly Dictionary<object, int[]> _coroutineState = new();
    private readonly List<int> _coroutineInitRegs = new();
    private readonly Dictionary<string, CVar> _fields = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, CVar>> _scopes = new();
    private int _returnLabel = -1;
    private CsType _returnType = CsType.Void;
    private int[] _returnRegs = Array.Empty<int>();
    private readonly Stack<int> _breakLabels = new();
    private readonly Stack<int> _continueLabels = new();
    private readonly Stack<string> _methodStack = new();
    private int _timeReg = -1, _frameReg = -1;
    private bool _timeUsed, _frameUsed;
    private int _arrayCounter;
    private readonly Stack<int> _catchHandlers = new();
    private readonly Stack<BlockStmt> _finallyBlocks = new();
    private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
    private int _errorReg = -1;
    private int _coroutineCounter;
    private bool _allowSplit;
    private int _pageIndex;
    private LevelNode? _pageNode;
    private readonly List<LevelNode> _busNodes = new();
    private Dictionary<LevelNode, ulong>? _objectIds;
    private readonly List<SpillSlot> _spillSlotDefs = new();
    private readonly Dictionary<object, int[]> _spillMap = new();
    private readonly Dictionary<string, int[]> _auxSpill = new(StringComparer.Ordinal);
    private int _busSlotCursor = 1;
    private int _pageGateLabel = -1;
    private int _pageReturnLabel = -1;
    private int _pageBodyStart;
    private GasmCompileResult? _firstPage;
    private readonly List<GasmCompileResult> _continuations = new();
    private readonly Dictionary<int, List<int>> _pageCoroutineInits = new();
    private GasmProgram? _pageZeroProgram;
    private GasmEmitter? _pageZeroEmitter;
    private int _pageZeroBodyStart;
    private int _pageZeroTimeReg = -1;
    private int _pageZeroFrameReg = -1;
    private int _pageRename;
    private LevelNode? _consoleNode;
    private bool _consoleLineOpen;
    private readonly HashSet<string> _scriptObjectKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loopCreationWarnings = new(StringComparer.Ordinal);
    private int _loopDepth;
    private int _sceneObjectSerial;

    private sealed class SpillSlot
    {
        public int Bus;
        public GasmPropertyKind Kind;
        public int Component;
        public string Suffix = "";
    }

    /// <summary>
    /// Compiles a script. If the result exceeds GRAB's per-Code-Block limits the
    /// compiler retries in continuation mode: the Update body is split across
    /// generated Code Blocks that hand a page counter through a hidden bus object.
    /// </summary>
    public GasmCompileResult Compile(GrabLevel level, LevelNode codeBlock, string source)
        => Compile(level, codeBlock, source, prunePass: 0);

    private GasmCompileResult Compile(GrabLevel level, LevelNode codeBlock, string source, int prunePass)
    {
        var single = CompileCore(level, codeBlock, source, allowSplit: false);
        GasmCompileResult result;
        if (single.Program.Instructions.Count <= GrabLimits.MaxInstructions
            && single.Connections.Count <= GrabLimits.MaxConnections
            && single.Program.WorkingRegisters.Count <= 480)
        {
            DeactivateContinuations(level, codeBlock);
            result = single;
        }
        else
        {
            EnsureBusNodes(level, codeBlock);
            var split = CompileCore(level, codeBlock, source, allowSplit: true);
            if (!split.Success || split.Continuations.Count == 0)
            {
                DeactivateContinuations(level, codeBlock);
                result = single;
            }
            else
            {
                // The script may have needed more pages on an earlier compile; pages past the
                // new end must not keep running their old program.
                foreach (var page in level.AllNodes().Where(n => n.Generated && n.Kind == NodeKind.GASM &&
                             n.GeneratedFor == codeBlock.EditorId && n.PageIndex > split.Continuations.Count))
                {
                    page.Compiled = null;
                    page.StartActive = false;
                }
                result = split;
            }
        }

        // Objects from removed `new` expressions would otherwise stay in the level.
        // Removing them shifts object ids, so rebuild the program once afterwards;
        // otherwise a retained object's GASM connection could point at the wrong node.
        int pruned = PruneScriptObjects(level, codeBlock, result.Success);
        if (pruned > 0 && prunePass < 2)
            return Compile(level, codeBlock, source, prunePass + 1);
        return result;
    }

    private int PruneScriptObjects(GrabLevel level, LevelNode codeBlock, bool success)
    {
        if (!success) return 0;
        var stale = level.AllNodes().Where(n => n.ScriptCreated &&
            n.ScriptSourceBlock == codeBlock.EditorId && !_scriptObjectKeys.Contains(n.ScriptSourceKey)).ToList();
        foreach (var node in stale) level.RemoveNode(node);
        if (stale.Count > 0) level.IsDirty = true;
        return stale.Count;
    }

    private GasmCompileResult CompileCore(GrabLevel level, LevelNode codeBlock, string source, bool allowSplit)
    {
        _allowSplit = allowSplit;
        _pageIndex = 0;
        _pageNode = codeBlock;
        _consoleNode = null;
        _consoleLineOpen = false;
        _scriptObjectKeys.Clear();
        _loopCreationWarnings.Clear();
        _loopDepth = 0;
        _sceneObjectSerial = 0;
        _firstPage = null;
        _continuations.Clear();
        _level = level;
        _codeBlock = codeBlock;
        _diags.Clear();
        _fields.Clear();
        _scopes.Clear();
        _breakLabels.Clear();
        _continueLabels.Clear();
        _methodStack.Clear();
        _timeReg = _frameReg = -1;
        _timeUsed = _frameUsed = false;
        _typeDecls.Clear();
        _typeCache.Clear();
        _statics.Clear();
        _typeBindings.Clear();
        _coroutineState.Clear();
        _coroutineInitRegs.Clear();
        _pageCoroutineInits.Clear();
        _spillMap.Clear();
        _auxSpill.Clear();
        _spillSlotDefs.Clear();
        _spillSlotDefs.Add(new SpillSlot { Bus = 0, Kind = GasmPropertyKind.Position, Component = 0, Suffix = ".Pos.X" });
        _busSlotCursor = 1;
        _pageZeroProgram = null;
        _pageZeroEmitter = null;
        _thisInstance = null;
        _instanceCounter = 0;
        level.EnsureNames();
        var parser = new CsParser(source ?? "", _diags);
        try { _unit = parser.Parse(); }
        catch (Exception ex) { _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Parser crashed: " + ex.Message, File = "Script.cs" }); _unit = new CompilationUnit(); }
        foreach (var c in _unit.Classes)
            if (!string.IsNullOrEmpty(c.Name)) _typeDecls[c.Name] = c;
        var ids = level.BuildObjectIds();
        _objectIds = ids;
        _e = new GasmEmitter(ids, _diags);
        _class = _unit.Classes.FirstOrDefault(c => c.Methods.Any(m => m.Name is "Update" or "Start"))
                 ?? _unit.Classes.FirstOrDefault();
        _scriptDef = _class != null
            ? new TypeDef { Name = _class.Name, Kind = _class.Kind, Decl = _class, Key = _class.Name + "`script" }
            : null;
        if (_unit.Classes.Count > 1)
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = "Only the script class with Start/Update is compiled as the entry point; other classes are still available as types", File = "Script.cs" });

        try
        {
            BuildFields();
            EmitProgram();
            InitializeSyntheticRegisters();
            ValidateGrabLimits();
        }
        catch (Exception ex)
        {
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Compiler error: " + ex.Message, File = "Script.cs" });
        }

        var final = CapturePage(_pageNode ?? codeBlock);
        if (_firstPage != null)
        {
            // The last page is captured here (SplitPage captured the earlier ones);
            // it is the same node, so only add it once.
            if (!_continuations.Any(p => ReferenceEquals(p.BlockNode, final.BlockNode))) _continuations.Add(final);
            _firstPage.Continuations = _continuations.ToList();
            foreach (var page in _continuations) page.BlockNode!.Compiled = page;
            return _firstPage;
        }
        return final;
    }

    private GasmCompileResult CapturePage(LevelNode node)
    {
        // Coroutine state registers are page-local. Initialise them once at program
        // entry (before the page gate) so a split script resumes its coroutines
        // exactly like an unsplit one.
        if (_pageCoroutineInits.TryGetValue(_pageIndex, out var initRegs))
        {
            foreach (var reg in initRegs.Distinct())
                _e.Program.Instructions.Insert(0, new GasmInstruction(GasmOp.Set, GasmEmitter.Work(reg), GasmEmitter.Const(0)));
            _pageCoroutineInits.Remove(_pageIndex);
        }
        var result = new GasmCompileResult
        {
            Program = _e.Program,
            Connections = _e.Connections,
            GasmText = _e.BuildText().ToList(),
            Diagnostics = _diags,
            BlockNode = node
        };
        if (result.Program.Instructions.Count > GrabLimits.MaxInstructions)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"Page {_pageIndex + 1} still needs {result.Program.Instructions.Count} instructions after splitting; reduce the code in one statement (loops and long sign text are the usual cause).",
                File = "Script.cs"
            });
        }
        if (result.Connections.Count > GrabLimits.MaxConnections)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"Page {_pageIndex + 1} needs {result.Connections.Count} connections; GRAB allows {GrabLimits.MaxConnections}. A split script can only reference {GrabLimits.MaxConnections - 1} scene objects because one connection is the bus.",
                File = "Script.cs"
            });
        }
        node.Compiled = result;
        node.StartActive = true;
        return result;
    }

    /// <summary>
    /// GASM registers are created/zeroed by a SET instruction in some game versions.
    /// Time.Time and Time.FrameCount need their hidden accumulators explicitly set to 0
    /// before the Start/Update body, otherwise adding DeltaTime to them is undefined
    /// and a user field called "time" used to collide with the internal accumulator.
    /// </summary>
    private void InitializeSyntheticRegisters()
    {
        if (_allowSplit) return;
        foreach (var reg in _coroutineInitRegs)
            _e.Program.Instructions.Insert(0, new GasmInstruction(GasmOp.Set, GasmEmitter.Work(reg), GasmEmitter.Const(0)));
        if (_timeUsed && _timeReg >= 0)
            _e.Program.Instructions.Insert(0, new GasmInstruction(GasmOp.Set, GasmEmitter.Work(_timeReg), GasmEmitter.Const(0)));
        if (_frameUsed && _frameReg >= 0)
            _e.Program.Instructions.Insert(0, new GasmInstruction(GasmOp.Set, GasmEmitter.Work(_frameReg), GasmEmitter.Const(0)));
    }

    private void ValidateGrabLimits()
    {
        if (_allowSplit) return;
        int instructions = _e.Program.Instructions.Count;
        if (instructions > GrabLimits.MaxInstructions)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"GRAB Code Blocks allow {GrabLimits.MaxInstructions} instructions; this program needs {instructions}. Simplify the script or split it across Code Blocks.",
                File = "Script.cs"
            });
        }
        else if (instructions >= GrabLimits.MaxInstructions - 10)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Close to GRAB's {GrabLimits.MaxInstructions}-instruction limit ({instructions} used).",
                File = "Script.cs"
            });
        }

        if (_e.Connections.Count > GrabLimits.MaxConnections)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"GRAB allows {GrabLimits.MaxConnections} connections per Code Block; this script uses {_e.Connections.Count}. Reuse objects or split the script.",
                File = "Script.cs"
            });
        }

        foreach (var c in _e.Connections)
        {
            if (c.Type == GasmConnectionType.Node && c.Name.Length > GrabLimits.MaxConnectionNameLength)
            {
                _diags.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = $"Connection name '{c.Name}' is longer than GRAB's {GrabLimits.MaxConnectionNameLength}-character limit.",
                    File = "Script.cs"
                });
            }
        }

        foreach (var group in _e.Connections.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"Duplicate Code Block connection name '{group.Key}'. GRAB requires unique names.",
                File = "Script.cs"
            });
        }
    }

    private void BuildFields()
    {
        if (_class == null) return;
        foreach (var f in _class.Fields)
        {
            var t = ResolveType(f.TypeName, f.Line, f.Col, f.IsArray);
            if (t == null) continue;
            var v = new CVar
            {
                Name = f.Name, Type = t, IsConst = f.IsConst, IsField = true,
                IsReadonly = f.IsReadonly, IsStatic = f.IsStatic, TypeKey = "f_" + f.Name
            };
            _fields[f.Name] = v;
            AllocateStorage(v);
        }
        foreach (var f in _class.Fields)
        {
            if (!_fields.TryGetValue(f.Name, out var v)) continue;
            if (v.IsObject)
            {
                if (f.Initializer != null)
                {
                    var val = Eval(f.Initializer);
                    if (val.Node != null)
                    {
                        v.ObjectNode = val.Node;
                        if (v.Type.Kind == CsTypeKind.GameObject) v.Type = val.Type;
                    }
                    else _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"'{f.Name}' must be initialized with GameObject.Find(...) or null", Line = f.Line, Column = f.Col, File = "Script.cs" });
                }
                continue;
            }
            if (v.IsConst)
            {
                if (f.Initializer == null) { _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"const '{f.Name}' needs an initializer", Line = f.Line, Column = f.Col, File = "Script.cs" }); continue; }
                var val = Eval(f.Initializer);
                if (!val.IsConst) { _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"const '{f.Name}' must be a compile-time constant", Line = f.Line, Column = f.Col, File = "Script.cs" }); continue; }
                v.ConstValue = val;
                continue;
            }
            if (v.Type.Kind == CsTypeKind.Array)
            {
                if (f.Initializer == null)
                {
                    _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"Array field '{f.Name}' needs a size, e.g. new float[4]", Line = f.Line, Column = f.Col, File = "Script.cs" });
                    continue;
                }
                var arr = Eval(f.Initializer);
                if (arr.ArrayRegs == null || arr.ArrayRegs.Length == 0)
                {
                    _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"Array field '{f.Name}' must be initialized with new {v.ElementType?.Name ?? "float"}[size]", Line = f.Line, Column = f.Col, File = "Script.cs" });
                    FreeValue(arr);
                    continue;
                }
                v.ElementType = arr.ElementType;
                v.ArrayRegs = new int[arr.ArrayRegs.Length];
                for (int i = 0; i < arr.ArrayRegs.Length; i++)
                {
                    v.ArrayRegs[i] = _e.AllocWorking("f_" + f.Name + "_" + i);
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(v.ArrayRegs[i]), GasmEmitter.Work(arr.ArrayRegs[i]));
                }
                FreeValue(arr);
                continue;
            }
            var initializer = FieldInitializer(f);
            if (initializer != null)
            {
                var val = Eval(initializer);
                bool ro = v.IsReadonly;
                v.IsReadonly = false;
                new VarLValue(v, _e).Write(val);
                v.IsReadonly = ro;
                FreeValue(val);
            }
            else InitDefault(v);
        }
    }

    /// <summary>
    /// Serialized fields let the Inspector override a starting value without editing
    /// the script. Constants, object references and arrays keep their source value;
    /// numeric/string/bool/Vector3/Color fields can be overridden.
    /// </summary>
    private Expr? FieldInitializer(FieldDecl f)
    {
        if (f.IsConst || f.IsStatic || f.IsArray) return f.Initializer;
        if (_codeBlock != null && _codeBlock.FieldOverrides.TryGetValue(f.Name, out var text) && !string.IsNullOrWhiteSpace(text))
        {
            var parsed = ScriptIntrospection.ParseValue(text, f.TypeName);
            if (parsed != null) return parsed;
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Inspector value '{text}' for '{f.Name}' is not a constant {f.TypeName}; using the script default.",
                Line = f.Line, Column = f.Col, File = "Script.cs"
            });
        }
        return f.Initializer;
    }

    private static bool IsObjectType(CsType t) =>
        t.Kind is CsTypeKind.GameObject or CsTypeKind.Trigger or CsTypeKind.Light or CsTypeKind.Sign or CsTypeKind.PhysicsBlock;

    private static int ComponentCount(CsType t) => t.Kind switch
    {
        CsTypeKind.Vector3 => 3,
        CsTypeKind.Color => 4,
        _ => 1
    };

    /// <summary>Allocates the storage for a variable based on its type.</summary>
    private void AllocateStorage(CVar v)
    {
        if (v.IsConst) return;
        if (IsObjectType(v.Type)) { v.IsObject = true; return; }
        if (v.Type.Kind is CsTypeKind.String or CsTypeKind.Array) return;
        if (v.Type.IsCollection) return;
        if (v.Type.IsUserType)
        {
            if (v.Type.Definition is TypeDef def && v.Type.Kind == CsTypeKind.Struct)
                v.Instance = AllocateInstance(def);
            return;
        }
        int count = ComponentCount(v.Type);
        v.Regs = new int[count];
        string prefix = string.IsNullOrEmpty(v.TypeKey) ? "v_" + v.Name : v.TypeKey;
        for (int i = 0; i < count; i++) v.Regs[i] = _e.AllocWorking(prefix + (count > 1 ? "_" + i : ""));
    }

    private void InitDefault(CVar v)
    {
        if (v.IsObject) return;
        if (v.Type.Kind == CsTypeKind.String) { v.StrValue = ""; return; }
        if (v.Type.IsUserType) return;
        if (v.Type.IsCollection) return;
        foreach (var r in v.Regs) _e.Emit(GasmOp.Set, GasmEmitter.Work(r), GasmEmitter.Const(0));
    }

    /// <summary>Allocates a class or struct instance and runs its field initializers.</summary>
    private CInstance AllocateInstance(TypeDef def)
    {
        int id = _instanceCounter++;
        var inst = new CInstance { Def = def, Type = def.Type, Emitter = _e };
        var saved = _thisInstance;
        var savedScopes = _scopes.ToList();
        _thisInstance = inst;
        _typeBindings.Add(def.Bindings);
        _scopes.Clear();
        _scopes.Add(inst.Fields);
        try
        {
            foreach (var (f, t) in def.Fields)
            {
                if (f.IsStatic) continue;
                var v = new CVar
                {
                    Name = f.Name, Type = t, IsField = true, IsReadonly = f.IsReadonly,
                    TypeKey = "o" + id + "_" + f.Name
                };
                inst.Fields[f.Name] = v;
                AllocateStorage(v);
            }
            foreach (var (f, t) in def.Fields)
            {
                if (f.IsStatic) continue;
                if (!inst.Fields.TryGetValue(f.Name, out var v)) continue;
                if (f.Initializer != null)
                {
                    var val = Eval(f.Initializer);
                    new VarLValue(v, _e).Write(val);
                    FreeValue(val);
                }
                else InitDefault(v);
            }
        }
        finally
        {
            _thisInstance = saved;
            _scopes.Clear();
            _scopes.AddRange(savedScopes);
            _typeBindings.RemoveAt(_typeBindings.Count - 1);
        }
        return inst;
    }

    /// <summary>Runs a constructor body with `this` bound to the new instance.</summary>
    private void RunConstructor(CInstance inst, MethodBinding? ctor, List<CValue> args, CsNode at)
    {
        if (ctor == null)
        {
            if (args.Count > 0) Error(at, $"'{inst.Type.Name}' has no constructor with {args.Count} parameter(s)");
            return;
        }
        var saved = _thisInstance;
        var savedScopes = _scopes.ToList();
        var savedReturn = _returnLabel;
        _thisInstance = inst;
        _typeBindings.Add(ctor.Owner.Bindings);
        _scopes.Clear();
        _scopes.Add(inst.Fields);
        var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
        _scopes.Add(scope);
        for (int i = 0; i < ctor.Decl.Params.Count && i < args.Count; i++)
        {
            var p = ctor.Decl.Params[i];
            var pt = ResolveType(p.TypeName, p.Line, p.Col) ?? CsType.Float;
            var v = new CVar { Name = p.Name, Type = pt, IsParameter = true, TypeKey = "p_" + p.Name + "_" + _instanceCounter };
            AllocateStorage(v);
            new VarLValue(v, _e).Write(args[i]);
            scope[p.Name] = v;
            inst.Fields[p.Name] = v;
        }
        int returnLabel = _e.NewLabel("ctor_return");
        _returnLabel = returnLabel;
        var readonlyFields = inst.Fields.Values.Where(v => v.IsReadonly).ToList();
        foreach (var v in readonlyFields) v.IsReadonly = false;
        if (ctor.Decl.Body != null)
            foreach (var s in ctor.Decl.Body.Statements) CompileStmt(s);
        foreach (var v in readonlyFields) v.IsReadonly = true;
        _e.Mark(returnLabel);
        _returnLabel = savedReturn;
        _thisInstance = saved;
        _scopes.Clear();
        _scopes.AddRange(savedScopes);
        _typeBindings.RemoveAt(_typeBindings.Count - 1);
    }

    private void EmitProgram()
    {
        var awake = Hook("Awake");
        var onEnable = Hook("OnEnable");
        var start = Hook("Start");
        var fixedUpdate = Hook("FixedUpdate");
        var update = Hook("Update");
        var lateUpdate = Hook("LateUpdate");
        var stay = Hook("OnTriggerStay");
        var signal = Hook("OnSignal");
        _scopes.Clear();
        _scopes.Add(_fields);
        foreach (var method in new[] { awake, onEnable, start, fixedUpdate, update, lateUpdate })
            if (method != null && method.ReturnTypeName != "void")
                Error(method, $"{method.Name} must return void");
        if (_unit.TopLevel.Count > 0 && _class != null)
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = "Top-level statements are ignored when a script class exists", File = "Script.cs" });

        if (_class != null)
        {
            if (Hook("OnDisable") != null || Hook("OnDestroy") != null)
                _diags.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = "GRAB has no unload callback, so OnDisable/OnDestroy are not emitted. Put cleanup in the trigger or condition that disables the object instead.",
                    File = "Script.cs"
                });
            if (Hook("OnGrab") != null || Hook("OnRelease") != null || Hook("OnPlayerJoin") != null || Hook("OnPlayerLeave") != null)
                _diags.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = "OnGrab/OnRelease/OnPlayerJoin/OnPlayerLeave have no GASM event; use an OnTriggerEnter/OnSignal trigger instead.",
                    File = "Script.cs"
                });
            if (fixedUpdate != null)
                _diags.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Message = "GASM has one program loop; FixedUpdate runs once per frame (60 Hz in GRAB) rather than on Unity's fixed clock.",
                    File = "Script.cs"
                });
        }

        // Awake and OnEnable run before Start, matching Unity's order closely enough
        // for setup code that has to happen before the first visible frame.
        if (awake != null) CompileEntryBody(awake, "Awake");
        if (onEnable != null) CompileEntryBody(onEnable, "OnEnable");

        if (start != null && _allowSplit && update == null && fixedUpdate == null && lateUpdate == null)
        {
            CompileSplitBody(start, true);
        }
        else if (start != null) CompileEntryBody(start, "Start");
        else if (_unit.TopLevel.Count > 0) CompileTopLevel(_unit.TopLevel);

        bool hasLoop = update != null || fixedUpdate != null || lateUpdate != null || HasTriggerHooks();
        if (hasLoop && _allowSplit)
        {
            // Event-only scripts (no Update) still go through the paged compiler; the
            // event dispatcher below emits the trigger hooks, so don't compile an
            // event method body a second time.
            CompileSplitBody(update ?? fixedUpdate ?? lateUpdate, false);
        }
        else if (hasLoop)
        {
            int loop = _e.NewLabel("loop");
            _e.Mark(loop);
            // Time.Time is lowered to a hidden accumulator that is advanced once per
            // Update. The body has to be compiled first: _timeUsed is only set when the
            // expression is actually seen. Emitting the ADD before the body meant a
            // script that only used Time.Time inside Update never advanced it.
            int loopBodyStart = _e.Program.Instructions.Count;
            if (fixedUpdate != null) CompileEntryBody(fixedUpdate, "FixedUpdate");
            if (update != null) CompileEntryBody(update, "Update");
            if (lateUpdate != null) CompileEntryBody(lateUpdate, "LateUpdate");
            EmitTriggerHooks();
            if (_unit.TopLevel.Count > 0 && _class == null) CompileTopLevel(_unit.TopLevel);
            if (_timeUsed)
            {
                EnsureTime();
                _e.Program.Instructions.Insert(loopBodyStart,
                    new GasmInstruction(GasmOp.Add, GasmEmitter.Work(_timeReg), GasmEmitter.Work(_timeReg), GasmEmitter.Special(4)));
                loopBodyStart++;
            }
            if (_frameUsed)
            {
                EnsureFrame();
                _e.Program.Instructions.Insert(loopBodyStart,
                    new GasmInstruction(GasmOp.Add, GasmEmitter.Work(_frameReg), GasmEmitter.Work(_frameReg), GasmEmitter.Const(1)));
            }
            _e.Emit(GasmOp.Sleep, GasmEmitter.Const(0));
            _e.Goto(loop);
        }
        else if (start == null && awake == null && onEnable == null)
        {
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = "Script has no Awake/Start/Update method, so it has no frame loop", File = "Script.cs" });
            _e.Emit(GasmOp.End);
        }
        else
        {
            _e.Emit(GasmOp.End);
        }
    }

    private MethodDecl? Hook(string name) =>
        _class?.Methods.FirstOrDefault(m => !m.IsConstructor && m.Name == name);

    /// <summary>True when a method can be called with this many arguments, accounting
    /// for a trailing `params` array.</summary>
    internal static bool AcceptsArgs(MethodDecl method, int argc)
    {
        if (method.Params.Count == 0) return argc == 0;
        var last = method.Params[^1];
        if (last.IsParams) return argc >= method.Params.Count - 1;
        return argc == method.Params.Count;
    }

    private static int FixedParamCount(MethodDecl method)
        => method.Params.Count > 0 && method.Params[^1].IsParams ? method.Params.Count - 1 : method.Params.Count;

    private bool HasTriggerHooks() => Hook("OnTriggerEnter") != null || Hook("OnTriggerExit") != null ||
                                      Hook("OnTriggerStay") != null || Hook("OnSignal") != null;

    /// <summary>
    /// Event callbacks are real GASM: every frame the program reads the Active register
    /// of each Trigger that targets this Code Block and calls the matching hook on a
    /// rising/falling edge. This works both in Play mode and in retail GRAB, without
    /// any extra connections beyond the trigger objects themselves.
    /// </summary>
    private void EmitTriggerHooks()
    {
        var enter = Hook("OnTriggerEnter");
        var exit = Hook("OnTriggerExit");
        var stay = Hook("OnTriggerStay");
        var signal = Hook("OnSignal");
        if (enter == null && exit == null && stay == null && signal == null) return;
        foreach (var hook in new[] { enter, exit, stay, signal })
        {
            if (hook == null || hook.Params.Count == 0) continue;
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Error,
                Message = $"{hook.Name} must be parameterless in GASM (there is no event object to pass).",
                File = "Script.cs"
            });
            return;
        }
        if (_codeBlock == null) return;
        var targets = _level.AllNodes().Where(n => n.Kind == NodeKind.Trigger &&
            n.TriggerTargets.Any(t => t.Kind == 7 &&
                (t.ObjectId == _objectIds![_codeBlock] || string.Equals(t.ObjectName, _codeBlock.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        if (targets.Count == 0)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = "A trigger event exists but no Trigger targets this Code Block (add a Program target to the trigger).",
                File = "Script.cs"
            });
            return;
        }
        int budget = GrabLimits.MaxConnections - _e.Connections.Count;
        if (targets.Count > budget)
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = $"Only {Math.Max(0, budget)} trigger connection(s) remain; event hooks use the first {Math.Max(0, budget)} trigger(s).",
                File = "Script.cs"
            });
        }
        int index = 0;
        foreach (var trigger in targets)
        {
            if (index >= Math.Max(0, budget)) break;
            var active = PropScalar(trigger, GasmPropertyKind.TriggerActive, GasmEmitter.TriggerSpecs, "Active", true)?.Read();
            if (active == null) continue;
            int last = _e.AllocWorking("evt_" + index + "_last");
            int value = Materialize(active);
            int same = _e.NewLabel("evt_same");
            if (stay != null || signal != null)
            {
                int hot = _e.AllocTemp();
                int quiet = _e.NewLabel("evt_quiet");
                _e.Emit(GasmOp.Greater, GasmEmitter.Work(hot), GasmEmitter.Work(value), GasmEmitter.Const(0));
                _e.Emit(GasmOp.If, GasmEmitter.Work(hot), GasmOperand.Label(quiet));
                if (stay != null) InvokeHook(stay);
                _e.Mark(quiet);
                _e.FreeTemp(hot);
            }
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(value), GasmEmitter.Work(last));
            _e.Emit(GasmOp.If, GasmEmitter.Work(eq), GasmOperand.Label(same));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(last), GasmEmitter.Work(value));
            int zero = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(zero), GasmEmitter.Work(value), GasmEmitter.Const(0));
            int leave = _e.NewLabel("evt_leave");
            _e.Emit(GasmOp.If, GasmEmitter.Work(zero), GasmOperand.Label(leave));
            InvokeHook(enter);
            InvokeHook(signal);
            _e.Goto(same);
            _e.Mark(leave);
            InvokeHook(exit);
            _e.Mark(same);
            _e.FreeTemp(zero);
            _e.FreeTemp(eq);
            _e.FreeTemp(value);
            FreeValue(active);
            index++;
        }
    }

    private void InvokeHook(MethodDecl? hook)
    {
        if (hook == null) return;
        var call = new CallExpr { Target = new NameExpr { Name = hook.Name }, Line = hook.Line, Col = hook.Col };
        var result = InvokeScript(hook, call);
        FreeValue(result);
    }

    private void CompileEntryBody(MethodDecl method, string name)
    {
        _returnLabel = _e.NewLabel(name + "_return");
        _labels.Clear();
        _catchHandlers.Clear();
        _finallyBlocks.Clear();
        _scopes.Clear();
        _scopes.Add(_fields);
        var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
        _scopes.Add(scope);
        for (int i = 0; i < method.Params.Count; i++)
        {
            var p = method.Params[i];
            var t = ResolveType(p.TypeName, p.Line, p.Col) ?? CsType.Float;
            var v = new CVar { Name = p.Name, Type = t, IsParameter = true };
            int count = t.Kind == CsTypeKind.Vector3 ? 3 : t.Kind == CsTypeKind.Color ? 4 : 1;
            v.Regs = new int[count];
            for (int k = 0; k < count; k++) v.Regs[k] = _e.AllocWorking("p_" + p.Name + (count > 1 ? "_" + k : ""));
            scope[p.Name] = v;
        }
        if (method.Body != null) CompileBlock(method.Body);
        else if (method.ExpressionBody != null)
        {
            var v = Eval(method.ExpressionBody);
            FreeValue(v);
        }
        _e.Mark(_returnLabel);
        _returnLabel = -1;
    }

    private void CompileTopLevel(List<Stmt> statements)
    {
        _returnLabel = _e.NewLabel("toplevel_return");
        _scopes.Clear();
        _scopes.Add(_fields);
        foreach (var s in statements) CompileStmt(s);
        _e.Mark(_returnLabel);
        _returnLabel = -1;
    }

    private void CompileStmt(Stmt s)
    {
        if (_e != null && s.Line > 0) _e.SourceLine = s.Line;
        switch (s)
        {
            case BlockStmt b: CompileBlock(b); break;
            case EmptyStmt: break;
            case LocalDeclStmt d: CompileLocal(d); break;
            case ExprStmt es:
            {
                var v = Eval(es.Expr);
                FreeValue(v);
                break;
            }
            case IfStmt iff: CompileIf(iff); break;
            case WhileStmt w:
            {
                int start = _e.NewLabel("while");
                int end = _e.NewLabel("endwhile");
                _breakLabels.Push(end);
                _continueLabels.Push(start);
                _e.Mark(start);
                EmitIfFalse(w.Cond, end);
                _loopDepth++;
                CompileStmt(w.Body);
                _loopDepth--;
                _e.Goto(start);
                _e.Mark(end);
                _continueLabels.Pop();
                _breakLabels.Pop();
                break;
            }
            case DoStmt doS:
            {
                int start = _e.NewLabel("do");
                int cont = _e.NewLabel("do_cond");
                int end = _e.NewLabel("enddo");
                _breakLabels.Push(end);
                _continueLabels.Push(cont);
                _e.Mark(start);
                _loopDepth++;
                CompileStmt(doS.Body);
                _loopDepth--;
                _e.Mark(cont);
                EmitIfTrue(doS.Cond, start);
                _e.Mark(end);
                _continueLabels.Pop();
                _breakLabels.Pop();
                break;
            }
            case ForStmt f:
            {
                if (f.Init != null) CompileStmt(f.Init);
                int start = _e.NewLabel("for");
                int cont = _e.NewLabel("for_inc");
                int end = _e.NewLabel("endfor");
                _breakLabels.Push(end);
                _continueLabels.Push(cont);
                _e.Mark(start);
                if (f.Cond != null) EmitIfFalse(f.Cond, end);
                _loopDepth++;
                CompileStmt(f.Body);
                _loopDepth--;
                _e.Mark(cont);
                foreach (var inc in f.Increment) { var v = Eval(inc); FreeValue(v); }
                _e.Goto(start);
                _e.Mark(end);
                _continueLabels.Pop();
                _breakLabels.Pop();
                break;
            }
            case ReturnStmt r:
            {
                if (_returnLabel < 0) { Error(r, "return outside a method"); break; }
                // finally blocks run before returning out of a try
                foreach (var fin in _finallyBlocks.ToArray()) CompileBlock(fin);
                if (r.Expr != null)
                {
                    var v = Eval(r.Expr);
                    if (_returnRegs.Length > 0)
                    {
                        if (_returnRegs.Length == 1) _e.Emit(GasmOp.Set, GasmEmitter.Work(_returnRegs[0]), v.Operand());
                        else if (v.Parts != null)
                            for (int i = 0; i < _returnRegs.Length; i++) _e.Emit(GasmOp.Set, GasmEmitter.Work(_returnRegs[i]), v.Parts[Math.Min(i, v.Parts.Length - 1)].Operand());
                    }
                    FreeValue(v);
                }
                _e.Goto(_returnLabel);
                break;
            }
            case BreakStmt br:
                if (_breakLabels.Count == 0) Error(br, "break outside a loop or switch");
                else _e.Goto(_breakLabels.Peek());
                break;
            case ContinueStmt cn:
                if (_continueLabels.Count == 0) Error(cn, "continue outside a loop");
                else _e.Goto(_continueLabels.Peek());
                break;
            case SwitchStmt sw: CompileSwitch(sw); break;
            case ForEachStmt fe: CompileForEach(fe); break;
            case TryStmt ts: CompileTry(ts); break;
            case ThrowStmt th: CompileThrow(th); break;
            case UsingStmt us: CompileUsing(us); break;
            case LockStmt lk:
            {
                var lockValue = Eval(lk.Lock);
                FreeValue(lockValue);
                _diags.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Info,
                    Message = "lock has no effect: GASM code blocks run on a single thread",
                    Line = lk.Line, Column = lk.Col, File = "Script.cs", Code = "CS0184"
                });
                CompileStmt(lk.Body);
                break;
            }
            case GotoStmt gt:
            {
                if (string.IsNullOrEmpty(gt.Label)) { Error(gt, "goto needs a label"); break; }
                int target = LabelFor(gt.Label, gt);
                _e.Goto(target);
                break;
            }
            case LabeledStmt ls:
            {
                int target = LabelFor(ls.Label, ls);
                _e.Mark(target);
                CompileStmt(ls.Inner);
                break;
            }
            case YieldStmt ys:
                Error(ys, "yield is only supported at the top level of an iterator method (IEnumerator) called from Update");
                break;
        }
    }

    private int LabelFor(string name, CsNode at)
    {
        if (_labels.TryGetValue(name, out var existing)) return existing;
        int label = _e.NewLabel("user_" + name);
        _labels[name] = label;
        return label;
    }

    private int EnsureErrorReg()
    {
        if (_errorReg < 0) _errorReg = _e.AllocWorking("__error");
        return _errorReg;
    }

    private void CompileForEach(ForEachStmt s)
    {
        var source = Eval(s.Collection);
        if (source.ArrayRegs != null)
        {
            var element = source.ElementType ?? CsType.Float;
            var declared = s.TypeName == "var" || string.IsNullOrEmpty(s.TypeName) ? element : ResolveType(s.TypeName, s.Line, s.Col) ?? element;
            var arrayVar = new CVar { Name = "__foreach_arr", Type = source.Type, ElementType = element, ArrayRegs = source.ArrayRegs };
            var item = new CVar { Name = s.Name, Type = declared, TypeKey = "fe_" + s.Name + "_" + _coroutineCounter++ };
            AllocateStorage(item);
            int index = _e.AllocWorking("fe_i_" + _coroutineCounter++);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(index), GasmEmitter.Const(0));
            int start = _e.NewLabel("fe_loop");
            int end = _e.NewLabel("fe_end");
            int cont = _e.NewLabel("fe_cont");
            _e.Mark(start);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Less, GasmEmitter.Work(cmp), GasmEmitter.Work(index), GasmEmitter.Const(source.ArrayRegs.Length));
            int done = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(done), GasmEmitter.Work(cmp));
            _e.Emit(GasmOp.If, GasmEmitter.Work(done), GasmOperand.Label(end));
            _e.FreeTemp(cmp); _e.FreeTemp(done);
            new VarLValue(item, _e).Write(new ArrayElementLValue(_e, arrayVar, new CValue { Type = CsType.Float, Reg = index }, false).Read());
            _scopes.Add(new Dictionary<string, CVar>(StringComparer.Ordinal) { [s.Name] = item });
            _breakLabels.Push(end); _continueLabels.Push(cont);
            _loopDepth++;
            CompileStmt(s.Body);
            _loopDepth--;
            _breakLabels.Pop(); _continueLabels.Pop();
            _scopes.RemoveAt(_scopes.Count - 1);
            _e.Mark(cont);
            _e.Emit(GasmOp.Add, GasmEmitter.Work(index), GasmEmitter.Work(index), GasmEmitter.Const(1));
            _e.Goto(start);
            _e.Mark(end);
            return;
        }
        if (source.Collection != null)
        {
            var coll = source.Collection;
            var itemType = coll.IsDictionary ? new CsType(CsTypeKind.Struct, "KeyValuePair") : coll.ElementType;
            var declared = s.TypeName == "var" || string.IsNullOrEmpty(s.TypeName) ? itemType : ResolveType(s.TypeName, s.Line, s.Col) ?? itemType;
            int index = _e.AllocWorking("fe_c_" + _coroutineCounter++);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(index), GasmEmitter.Const(0));
            int start = _e.NewLabel("fec_loop");
            int end = _e.NewLabel("fec_end");
            int cont = _e.NewLabel("fec_cont");
            _e.Mark(start);
            int limit = coll.IsDictionary ? coll.Capacity : -1;
            int cmp = _e.AllocTemp();
            if (coll.IsDictionary)
                _e.Emit(GasmOp.Less, GasmEmitter.Work(cmp), GasmEmitter.Work(index), GasmEmitter.Const(limit));
            else
                _e.Emit(GasmOp.Less, GasmEmitter.Work(cmp), GasmEmitter.Work(index), GasmEmitter.Work(coll.CountReg));
            int done = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(done), GasmEmitter.Work(cmp));
            _e.Emit(GasmOp.If, GasmEmitter.Work(done), GasmOperand.Label(end));
            _e.FreeTemp(cmp); _e.FreeTemp(done);

            CInstance? pair = null;
            CVar item;
            if (coll.IsDictionary)
            {
                pair = new CInstance { Def = null, Type = itemType, Emitter = _e };
                // The key of a string dictionary is its 24-bit stable hash, because GASM
                // has no string registers. Numeric keys are exact.
                var keyVar = new CVar { Name = "Key", Type = CsType.Float, TypeKey = "kv_k_" + _coroutineCounter++ };
                var valVar = new CVar { Name = "Value", Type = coll.ElementType, TypeKey = "kv_v_" + _coroutineCounter++ };
                AllocateStorage(keyVar);
                AllocateStorage(valVar);
                pair.Fields["Key"] = keyVar;
                pair.Fields["Value"] = valVar;
                CollectionSlotToVar(coll.KeySlots, index, keyVar);
                int emptyCmp = _e.AllocTemp();
                _e.Emit(GasmOp.Equal, GasmEmitter.Work(emptyCmp), GasmEmitter.Work(keyVar.Regs[0]), GasmEmitter.Const(DictionaryEmpty));
                _e.Emit(GasmOp.If, GasmEmitter.Work(emptyCmp), GasmOperand.Label(cont));
                _e.FreeTemp(emptyCmp);
                CollectionSlotToVar(coll.Slots, index, valVar);
                item = new CVar { Name = s.Name, Type = itemType, Instance = pair, TypeKey = "kv_" + s.Name + _coroutineCounter++ };
            }
            else
            {
                item = new CVar { Name = s.Name, Type = declared, TypeKey = "fe_" + s.Name + "_" + _coroutineCounter++ };
                AllocateStorage(item);
                new VarLValue(item, _e).Write(CollectionElementRead(coll, new CValue { Type = CsType.Float, Reg = index }, s));
            }
            _scopes.Add(new Dictionary<string, CVar>(StringComparer.Ordinal) { [s.Name] = item });
            _breakLabels.Push(end); _continueLabels.Push(cont);
            _loopDepth++;
            CompileStmt(s.Body);
            _loopDepth--;
            _breakLabels.Pop(); _continueLabels.Pop();
            _scopes.RemoveAt(_scopes.Count - 1);
            _e.Mark(cont);
            _e.Emit(GasmOp.Add, GasmEmitter.Work(index), GasmEmitter.Work(index), GasmEmitter.Const(1));
            _e.Goto(start);
            _e.Mark(end);
            return;
        }
        Error(s, "foreach needs an array, List<T> or Dictionary<K,V>");
    }

    /// <summary>Copies one of several slot registers selected by a runtime index into a variable.</summary>
    private void CollectionSlotToVar(int[] slots, int indexReg, CVar destination)
    {
        var temp = new CCollection { Capacity = slots.Length, Slots = slots, CountReg = -1, ElementType = destination.Type };
        var value = CollectionElementRead(temp, new CValue { Type = CsType.Float, Reg = indexReg }, new LiteralExpr());
        new VarLValue(destination, _e).Write(value);
        FreeValue(value);
    }

    private void CompileTry(TryStmt t)
    {
        int end = _e.NewLabel("try_end");
        int handler = _e.NewLabel("catch");
        int err = EnsureErrorReg();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(err), GasmEmitter.Const(0));
        _catchHandlers.Push(handler);
        _finallyBlocks.Push(t.Finally ?? new BlockStmt());
        CompileStmt(t.Body);
        _catchHandlers.Pop();
        _finallyBlocks.Pop();
        if (t.Finally != null) CompileBlock(t.Finally);
        _e.Goto(end);
        _e.Mark(handler);
        if (t.Catches.Count > 0)
        {
            var clause = t.Catches[0];
            var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(clause.Name))
            {
                var v = new CVar { Name = clause.Name, Type = CsType.Float, TypeKey = "catch_" + clause.Name + "_" + _coroutineCounter++ };
                AllocateStorage(v);
                new VarLValue(v, _e).Write(new CValue { Type = CsType.Float, Reg = err });
                scope[clause.Name] = v;
            }
            _scopes.Add(scope);
            CompileBlock(clause.Body);
            _scopes.RemoveAt(_scopes.Count - 1);
        }
        if (t.Finally != null) CompileBlock(t.Finally);
        _e.Mark(end);
    }

    private void CompileThrow(ThrowStmt t)
    {
        int err = EnsureErrorReg();
        if (t.Expr != null)
        {
            int code = 1;
            if (t.Expr is NewExpr { TypeName: "Exception" or "System.Exception" } ne && ne.Args.Count > 0)
            {
                var message = Eval(ne.Args[0]);
                if (message.Type.Kind == CsTypeKind.String) code = Math.Abs(HashKey(message.Str));
                else if (message.IsConst) code = (int)message.Const;
                else
                {
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(err), message.Operand());
                    code = -1;
                }
                FreeValue(message);
            }
            else
            {
                var v = Eval(t.Expr);
                if (v.IsConst) code = (int)v.Const;
                else { _e.Emit(GasmOp.Set, GasmEmitter.Work(err), v.Operand()); code = -1; }
                FreeValue(v);
            }
            if (code >= 0) _e.Emit(GasmOp.Set, GasmEmitter.Work(err), GasmEmitter.Const(code));
        }
        if (_catchHandlers.Count > 0) _e.Goto(_catchHandlers.Peek());
        else
        {
            _diags.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Message = "throw outside try/catch ends the code block; GASM has no exception unwinding",
                Line = t.Line, Column = t.Col, File = "Script.cs", Code = "CS0155"
            });
            if (_returnLabel >= 0) _e.Goto(_returnLabel);
        }
    }

    private void CompileUsing(UsingStmt u)
    {
        var savedScopes = _scopes.Count;
        if (u.Declaration != null) CompileLocal(u.Declaration);
        var resource = u.Resource != null ? Eval(u.Resource) : null;
        CompileStmt(u.Body);
        if (resource?.Inst != null)
        {
            var dispose = resource.Inst.Def?.FindMethod("Dispose", 0);
            if (dispose != null) InvokeMethod(dispose, new CallExpr { Target = new NameExpr { Name = "Dispose" }, Line = u.Line, Col = u.Col }, resource.Inst, true);
        }
        FreeValue(resource);
        while (_scopes.Count > savedScopes) _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void CompileBlock(BlockStmt b)
    {
        _scopes.Add(new Dictionary<string, CVar>(StringComparer.Ordinal));
        foreach (var s in b.Statements) CompileStmt(s);
        _scopes.RemoveAt(_scopes.Count - 1);
    }

    private void CompileLocal(LocalDeclStmt d)
    {
        CsType? declared = d.TypeName == "var" ? null : ResolveType(d.TypeName, d.Line, d.Col, d.IsArray);
        foreach (var (name, init, line, col) in d.Declarators)
        {
            CValue? iv = init != null ? Eval(init) : null;
            var t = declared ?? iv?.Type ?? CsType.Float;
            if (t.Kind == CsTypeKind.Error) { FreeValue(iv); continue; }
            if (t.Kind == CsTypeKind.Array)
            {
                var v2 = new CVar { Name = name, Type = t, ElementType = t.Element };
                if (iv?.ArrayRegs == null || iv.ArrayRegs.Length == 0)
                {
                    ErrorAt(line, col, $"Array '{name}' needs a fixed size, e.g. new {t.Element?.Name ?? "float"}[4]");
                }
                else
                {
                    v2.ElementType = iv.ElementType ?? t.Element;
                    v2.ArrayRegs = new int[iv.ArrayRegs.Length];
                    for (int i = 0; i < iv.ArrayRegs.Length; i++)
                    {
                        v2.ArrayRegs[i] = _e.AllocWorking("v_" + name + "_" + i);
                        _e.Emit(GasmOp.Set, GasmEmitter.Work(v2.ArrayRegs[i]), GasmEmitter.Work(iv.ArrayRegs[i]));
                    }
                }
                FreeValue(iv);
                _scopes[^1][name] = v2;
                continue;
            }
            var v = new CVar { Name = name, Type = t, TypeKey = "v_" + name + "_" + _instanceCounter++ };
            if (IsObjectType(t))
            {
                v.IsObject = true;
                if (iv?.Node == null) ErrorAt(line, col, $"'{name}' must be initialized with GameObject.Find(...)");
                else v.ObjectNode = iv.Node;
                if (iv != null && iv.Type.Kind != CsTypeKind.GameObject && t.Kind == CsTypeKind.GameObject) v.Type = iv.Type;
            }
            else if (t.Kind == CsTypeKind.String)
            {
                if (iv != null && iv.Type.Kind != CsTypeKind.String) ErrorAt(line, col, $"Cannot initialize a string with {iv.Type}");
                v.StrValue = iv?.Str ?? "";
            }
            else
            {
                AllocateStorage(v);
                if (t.IsUserType)
                {
                    if (iv == null)
                    {
                        // A class variable defaults to null; a struct already has its fields zeroed.
                    }
                    else if (iv.Inst != null) new VarLValue(v, _e).Write(new CValue { Type = t, Inst = iv.Inst });
                    else ErrorAt(line, col, $"Cannot initialize '{name}' of type {t.Name} with {iv.Type.Name}");
                }
                else if (t.IsCollection)
                {
                    if (iv?.Collection == null) ErrorAt(line, col, $"'{name}' needs a new {t.Name}(), e.g. new List<float>()");
                    else v.Collection = iv.Collection;
                }
                else new VarLValue(v, _e).Write(iv ?? CValue.F(0));
            }
            FreeValue(iv);
            _scopes[^1][name] = v;
        }
    }

    private void CompileIf(IfStmt s)
    {
        int elseL = _e.NewLabel("else");
        int endL = _e.NewLabel("endif");
        int cond = Materialize(Eval(s.Cond));
        int not = _e.AllocTemp();
        _e.Emit(GasmOp.Not, GasmEmitter.Work(not), GasmEmitter.Work(cond));
        _e.Emit(GasmOp.If, GasmEmitter.Work(not), GasmOperand.Label(elseL));
        _e.FreeTemp(not);
        _e.FreeTemp(cond);
        CompileStmt(s.Then);
        if (s.Else != null) { _e.Goto(endL); _e.Mark(elseL); CompileStmt(s.Else); _e.Mark(endL); }
        else { _e.Mark(elseL); }
    }

    private void CompileSwitch(SwitchStmt s)
    {
        var val = Eval(s.Expr);
        int reg = Materialize(val);
        FreeValue(val);
        int end = _e.NewLabel("endswitch");
        var labels = new Dictionary<SwitchCase, int>();
        int defaultLabel = -1;
        foreach (var c in s.Cases)
        {
            if (c.IsDefault) { defaultLabel = _e.NewLabel("default"); labels[c] = defaultLabel; continue; }
            int l = _e.NewLabel("case");
            labels[c] = l;
            var cv = Eval(c.Label!);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), GasmEmitter.Work(reg), cv.Operand());
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(l));
            _e.FreeTemp(cmp);
            FreeValue(cv);
        }
        if (defaultLabel >= 0) _e.Goto(defaultLabel); else _e.Goto(end);
        _breakLabels.Push(end);
        foreach (var c in s.Cases)
        {
            _e.Mark(labels[c]);
            foreach (var st in c.Statements) CompileStmt(st);
            _e.Goto(end);
        }
        _breakLabels.Pop();
        _e.Mark(end);
        _e.FreeTemp(reg);
    }

    private void EmitIfFalse(Expr cond, int label)
    {
        var v = Eval(cond);
        int r = Materialize(v);
        int n = _e.AllocTemp();
        _e.Emit(GasmOp.Not, GasmEmitter.Work(n), GasmEmitter.Work(r));
        _e.Emit(GasmOp.If, GasmEmitter.Work(n), GasmOperand.Label(label));
        _e.FreeTemp(n);
        _e.FreeTemp(r);
        FreeValue(v);
    }

    private void EmitIfTrue(Expr cond, int label)
    {
        var v = Eval(cond);
        int r = Materialize(v);
        _e.Emit(GasmOp.If, GasmEmitter.Work(r), GasmOperand.Label(label));
        _e.FreeTemp(r);
        FreeValue(v);
    }

    private int Materialize(CValue v)
    {
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(t), v.Operand());
        return t;
    }

    private void FreeValue(CValue? v)
    {
        if (v == null) return;
        if (v.Parts != null) { foreach (var p in v.Parts) FreeValue(p); return; }
        if (v.TextParts != null) { foreach (var p in v.TextParts) FreeValue(p.Value); return; }
        if (v.IsTemp && v.Reg >= 0) _e.FreeTemp(v.Reg);
        v.IsTemp = false;
    }

    private CsType? ResolveType(string name, int line, int col, bool isArray = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return CsType.Error;
        string n = name.Trim();
        if (n.StartsWith("global::")) n = n[8..];
        if (n.StartsWith("Grab.")) n = n[5..];
        while (n.EndsWith("[]")) { n = n[..^2]; isArray = true; }
        var baseType = ResolveNamedType(n, line, col);
        if (baseType == null) return null;
        if (!isArray) return baseType;
        if (baseType.Kind is not (CsTypeKind.Float or CsTypeKind.Bool or CsTypeKind.Vector3 or CsTypeKind.Color or CsTypeKind.Struct))
        {
            ErrorAt(line, col, $"Arrays of {baseType.Name} are not supported. Use float[], int[], bool[] or an array of a struct.");
            return CsType.Error;
        }
        return CsType.ArrayOf(baseType);
    }

    private CsType? ResolveNamedType(string n, int line, int col)
    {
        for (int i = _typeBindings.Count - 1; i >= 0; i--)
            if (_typeBindings[i].TryGetValue(n, out var bound)) return bound;

        // These names are also useful static helper classes, but a script's own
        // class must win (a user class called Shape is perfectly valid C#).
        if ((n is "Shape" or "Material" or "LightType" or "Random" or "Debug" or "Voxel" or "Animator") &&
            _typeDecls.TryGetValue(n, out var userDecl))
            return GetTypeDef(userDecl, new List<string>(), line, col)?.Type;

        switch (n)
        {
            case "float": case "double": case "int": case "uint": case "long": case "ulong":
            case "short": case "ushort": case "byte": case "sbyte": case "decimal": case "char":
                return CsType.Float;
            case "bool": return CsType.Bool;
            case "string": return CsType.String;
            case "void": return CsType.Void;
            case "Vector3": return CsType.Vector3;
            case "Vector2":
                ErrorAt(line, col, "Vector2 is not part of GRAB; use Vector3 with Z = 0");
                return CsType.Error;
            case "Color": return CsType.Color;
            case "GameObject": case "object": case "Object": return CsType.GameObject;
            case "Trigger": return new CsType(CsTypeKind.Trigger, "Trigger");
            case "Light": return new CsType(CsTypeKind.Light, "Light");
            case "Sign": return new CsType(CsTypeKind.Sign, "Sign");
            case "PhysicsBlock": case "Block": return new CsType(CsTypeKind.PhysicsBlock, "PhysicsBlock");
            case "Cube": case "Sphere": case "Cylinder": case "Pyramid": case "Prism":
            case "Cone": case "PyramidSquare": case "CrumblingBlock": case "Crumbling":
                return new CsType(CsTypeKind.PhysicsBlock, "PhysicsBlock");
            case "ParticleEmitter": case "Particles": case "SoundEmitter": case "Sound":
            case "GravityBlock": case "LobbyTerminal": case "StartPoint": case "FinishPoint":
                return CsType.GameObject;
            case "Shape": case "Material": case "LightType": case "Random": case "Debug": case "Voxel": case "Animator":
                return CsType.Float;
            case "Player": return new CsType(CsTypeKind.PlayerType, "Player");
            case "Level": return new CsType(CsTypeKind.LevelType, "Level");
            case "Exception": case "System.Exception": return CsType.Float;
            case "Task": case "ValueTask": case "IEnumerator": case "IEnumerable": case "IDisposable":
                return CsType.Task;
            case "var": return null;
        }

        var (baseName, argNames) = SplitGeneric(n);
        if (baseName is "Task" or "ValueTask" or "IEnumerator" or "IEnumerable" or "IDisposable" or "IList" or "ICollection")
            return CsType.Task;
        if (baseName == "List")
        {
            if (argNames.Count != 1) { ErrorAt(line, col, "List<T> needs one type argument"); return CsType.Error; }
            var element = ResolveType(argNames[0], line, col) ?? CsType.Float;
            if (!element.IsNumeric)
            {
                ErrorAt(line, col, $"List<{element.Name}> is not supported; lists store numbers (float, int, bool)");
                return CsType.Error;
            }
            return new CsType(CsTypeKind.List, n) { Element = element, Capacity = 4 };
        }
        if (baseName == "Dictionary")
        {
            if (argNames.Count != 2) { ErrorAt(line, col, "Dictionary<TKey, TValue> needs two type arguments"); return CsType.Error; }
            var key = ResolveType(argNames[0], line, col) ?? CsType.Float;
            var value = ResolveType(argNames[1], line, col) ?? CsType.Float;
            if (key.Kind is not (CsTypeKind.Float or CsTypeKind.Bool or CsTypeKind.String))
            {
                ErrorAt(line, col, $"Dictionary keys must be numbers or strings (got {key.Name})");
                return CsType.Error;
            }
            if (!value.IsNumeric)
            {
                ErrorAt(line, col, $"Dictionary values must be numbers (got {value.Name})");
                return CsType.Error;
            }
            return new CsType(CsTypeKind.Dictionary, n) { Element = value, KeyType = key, Capacity = 4 };
        }
        if (!_typeDecls.TryGetValue(baseName, out var decl))
            return UnknownType(n, line, col);
        return GetTypeDef(decl, argNames, line, col)?.Type;
    }

    private static (string Name, List<string> Args) SplitGeneric(string n)
    {
        int lt = n.IndexOf('<');
        if (lt < 0 || !n.EndsWith('>')) return (n, new List<string>());
        string name = n[..lt];
        string inner = n[(lt + 1)..^1];
        var args = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0) { args.Add(inner[start..i].Trim()); start = i + 1; }
        }
        args.Add(inner[start..].Trim());
        return (name, args);
    }

    private TypeDef? GetTypeDef(ClassDecl decl, List<string> argNames, int line, int col)
    {
        if (decl.GenericParams.Count != argNames.Count)
        {
            ErrorAt(line, col, $"'{decl.Name}' expects {decl.GenericParams.Count} type argument(s), got {argNames.Count}");
            return null;
        }
        var args = argNames.Select(a => ResolveType(a, line, col) ?? CsType.Error).ToList();
        string key = decl.Name + "`" + string.Join(",", args.Select(a => a.Name));
        if (_typeCache.TryGetValue(key, out var cached)) return cached;
        var def = new TypeDef
        {
            Name = decl.Name,
            Kind = decl.Kind,
            Decl = decl,
            Key = key,
            GenericParams = decl.GenericParams.ToList(),
            IsAbstract = decl.IsAbstract
        };
        for (int i = 0; i < args.Count; i++) def.Bindings[decl.GenericParams[i]] = args[i];
        var kind = decl.Kind == TypeDeclKind.Interface ? CsTypeKind.Interface
                 : decl.Kind == TypeDeclKind.Struct ? CsTypeKind.Struct : CsTypeKind.Object;
        def.Type = new CsType(kind, decl.Name + (args.Count > 0 ? "<" + string.Join(",", args.Select(a => a.Name)) + ">" : ""))
        {
            Definition = def,
            TypeArgs = args
        };
        _typeCache[key] = def;
        _typeBindings.Add(def.Bindings);
        try
        {
            if (decl.Kind != TypeDeclKind.Interface && !string.IsNullOrWhiteSpace(decl.BaseName) && decl.BaseName != "object")
            {
                var baseType = ResolveType(decl.BaseName, decl.Line, decl.Col);
                if (baseType?.Definition is TypeDef bd)
                {
                    if (bd.Kind == TypeDeclKind.Interface) def.Interfaces.Add(bd);
                    else if (ReferenceEquals(bd, def)) ErrorAt(decl.Line, decl.Col, $"'{decl.Name}' cannot inherit from itself");
                    else def.Base = bd;
                }
            }
            foreach (var iName in decl.Interfaces)
            {
                if (iName == decl.BaseName) continue;
                var it = ResolveType(iName, decl.Line, decl.Col);
                if (it?.Definition is TypeDef id && id.Kind == TypeDeclKind.Interface) def.Interfaces.Add(id);
            }
            if (def.Base != null)
            {
                def.Fields.AddRange(def.Base.Fields);
                def.Methods.AddRange(def.Base.Methods);
            }
            foreach (var f in decl.Fields)
            {
                var t = ResolveType(f.TypeName, f.Line, f.Col, f.IsArray) ?? CsType.Error;
                def.Fields.Add((f, t));
            }
            foreach (var m in decl.Methods)
            {
                var binding = new MethodBinding { Decl = m, Owner = def };
                if (m.IsConstructor) def.Constructors.Add(binding);
                else def.Methods.Insert(0, binding);
            }
        }
        finally
        {
            _typeBindings.RemoveAt(_typeBindings.Count - 1);
        }
        return def;
    }

    private CsType? UnknownType(string n, int line, int col)
    {
        ErrorAt(line, col, $"Unknown type '{n}'. Supported: float, int, bool, string, Vector3, Color, GameObject, Trigger, Light, Sign, PhysicsBlock, List<T>, Dictionary<K,V>, or a class/struct you defined");
        return CsType.Error;
    }

    private void Error(CsNode n, string msg) => ErrorAt(n.Line, n.Col, msg);
    private void ErrorAt(int line, int col, string msg) => _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = msg, Line = line, Column = col, File = "Script.cs", Code = "CS0103" });
    private void Warn(CsNode n, string msg) => _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = msg, Line = n.Line, Column = n.Col, File = "Script.cs" });

    private CVar? Lookup(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var v)) return v;
        if (_thisInstance != null && _thisInstance.Fields.TryGetValue(name, out var iv)) return iv;
        if (_fields.TryGetValue(name, out var f)) return f;
        return null;
    }

    private CValue Eval(Expr e)
    {
        // Expression nodes carry the exact source line, which gives the debugger
        // statement-level breakpoints even inside multiline assignments.
        if (e.Line > 0) _e.SourceLine = e.Line;
        switch (e)
        {
            case LiteralExpr lit:
                if (lit.Type.Kind == CsTypeKind.String) return CValue.S(lit.Value);
                return new CValue { Type = lit.Type, IsConst = true, Const = (float)lit.Number };
            case InterpolatedStringExpr interpolated:
                return EvalInterpolated(interpolated);
            case NameExpr ne:
            {
                var v = Lookup(ne.Name);
                if (v != null) return new VarLValue(v, _e).Read();
                Error(ne, $"Unknown name '{ne.Name}'");
                return CValue.F(0);
            }
            case ThisExpr:
                if (_thisInstance != null) return new CValue { Type = _thisInstance.Type, Inst = _thisInstance };
                Error(e, "'this' is only usable inside a class or struct");
                return CValue.F(0);
            case BaseExpr:
                if (_thisInstance != null) return new CValue { Type = _thisInstance.Type, Inst = _thisInstance };
                Error(e, "'base' is only usable inside a class");
                return CValue.F(0);
            case MemberExpr me: return EvalMember(me);
            case CallExpr ce: return EvalCall(ce);
            case BinaryExpr be: return EvalBinary(be);
            case UnaryExpr ue: return EvalUnary(ue);
            case AssignmentExpr ae: return EvalAssignment(ae);
            case ConditionalExpr cex: return EvalConditional(cex);
            case CastExpr cast: return EvalCast(cast);
            case NewExpr ne: return EvalNew(ne);
            case ParenthesizedExpr p: return Eval(p.Inner);
            case IndexExpr ix: return EvalIndex(ix);
            case AwaitExpr aw:
                Error(aw, "await is only supported as a top-level statement of an async method (async void Fade() { ... await Task.Delay(1000); ... })");
                return Eval(aw.Operand);
            default:
                Error(e, "Unsupported expression");
                return CValue.F(0);
        }
    }

    private CValue EvalInterpolated(InterpolatedStringExpr e)
    {
        var parts = new List<TextPart>();
        foreach (var part in e.Parts)
        {
            if (part.Text != null)
            {
                parts.Add(new TextPart { Text = part.Text });
                continue;
            }
            if (part.Value == null) continue;
            var value = Eval(part.Value);
            AppendConsoleValue(part.Value, value, parts);
        }
        var merged = MergeTextParts(parts);
        if (merged.All(p => p.Value == null))
            return CValue.S(string.Concat(merged.Select(p => p.Text)));
        foreach (var p in merged) if (p.Value != null) p.Value.IsTemp = false;
        return new CValue { Type = CsType.String, IsConst = false, TextParts = merged };
    }

    private CVar? ArrayVarOf(Expr target)
    {
        if (target is NameExpr n) return Lookup(n.Name) is { } v && v.Type.Kind == CsTypeKind.Array ? v : null;
        if (target is MemberExpr m && m.Target is ThisExpr && _fields.TryGetValue(m.Name, out var f) && f.Type.Kind == CsTypeKind.Array) return f;
        return null;
    }

    private CValue EvalIndex(IndexExpr ix)
    {
        var array = ArrayVarOf(ix.Target);
        if (array != null)
        {
            var index = Eval(ix.Index);
            return new ArrayElementLValue(_e, array, index, false).Read();
        }
        var target = Eval(ix.Target);
        if (target.Collection != null)
        {
            var index = Eval(ix.Index);
            var result = target.Collection.IsDictionary
                ? DictionaryElementRead(target.Collection, index, ix)
                : CollectionElementRead(target.Collection, index, ix);
            FreeValue(index);
            return result;
        }
        Error(ix, "Only fixed-size arrays, List<T> and Dictionary<K,V> can be indexed");
        return CValue.F(0);
    }

    private CValue DictionaryElementRead(CCollection coll, CValue key, CsNode at)
    {
        var k = KeyValue(key, coll.KeyType!, at);
        if (k == null) return CValue.F(0);
        int kr = Materialize(k);
        int result = _e.AllocTemp();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(0));
        int end = _e.NewLabel("dict_get_end");
        for (int i = 0; i < coll.Capacity; i++)
        {
            int next = _e.NewLabel("dict_get_next" + i);
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(kr));
            int diff = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(diff), GasmEmitter.Work(eq));
            _e.Emit(GasmOp.If, GasmEmitter.Work(diff), GasmOperand.Label(next));
            _e.FreeTemp(eq); _e.FreeTemp(diff);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Work(coll.Slots[i]));
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        _e.FreeTemp(kr);
        return new CValue { Type = coll.ElementType, Reg = result, IsTemp = true };
    }

    private void DictionaryElementWrite(CCollection coll, CValue key, CValue value, CsNode at)
    {
        var k = KeyValue(key, coll.KeyType!, at);
        if (k == null) return;
        DictionarySet(coll, k, value, at);
    }

    private bool TryPlayerPath(Expr e, out string path)
    {
        if (e is NameExpr n && n.Name == "Player") { path = ""; return true; }
        if (e is MemberExpr m && TryPlayerPath(m.Target, out var p)) { path = p.Length == 0 ? m.Name : p + "." + m.Name; return true; }
        path = ""; return false;
    }

    private CValue EvalMember(MemberExpr m)
    {
        if (m.Target is ThisExpr && _thisInstance != null && _thisInstance.Fields.TryGetValue(m.Name, out var thisField))
            return new VarLValue(thisField, _e).Read();
        if (m.Name == "Length" && ArrayVarOf(m.Target) is { } lengthArray)
            return CValue.F(lengthArray.ArrayRegs.Length);
        if (m.Target is NameExpr staticName && _typeDecls.ContainsKey(staticName.Name) && Lookup(staticName.Name) == null)
            return ReadStatic(staticName.Name, m.Name, m);
        if (m.Target is MemberExpr && TryPlayerPath(m.Target, out var playerPath))
        {
            var plv = PlayerPathLValue(playerPath + "." + m.Name);
            if (plv != null) return plv.Read();
        }
        if (m.Target is MemberExpr pm && pm.Target is NameExpr vn && Lookup(vn.Name) is CVar ov && ov.IsObject)
        {
            if (pm.Name == "Physics") return ObjectProperty(ov.ObjectNode, m, m.Name)?.Read() ?? CValue.F(0);
            if (pm.Name == "Light" && m.Name == "Color") return ObjectProperty(ov.ObjectNode, m, "Color")?.Read() ?? CValue.F(0);
        }
        if (m.Target is NameExpr tn)
        {
            if (tn.Name == "Time" || tn.Name == "time")
                return TimeMember(m);
            if (tn.Name == "Shape")
                return ShapeConstant(m);
            if (tn.Name == "Material")
                return MaterialConstant(m);
            if (tn.Name == "LightType")
                return LightTypeConstant(m);
            if (tn.Name is "Random" or "random")
                return RandomMember(m);
            if (tn.Name == "Mathf" || tn.Name == "Math" || tn.Name == "MathF")
                return MathConstant(m);
            if (tn.Name == "Vector3")
                return VectorConstant(m);
            if (tn.Name == "Color")
                return ColorConstant(m);
            if (tn.Name is "string" or "String")
            {
                if (m.Name == "Empty") return CValue.S("");
                Error(m, $"Unknown string member '{m.Name}'");
                return CValue.S("");
            }
            if (tn.Name == "float" || tn.Name == "double" || tn.Name == "int")
                return NumericConstant(m);
            if (tn.Name == "Player")
                return PlayerMember(m);
            if (tn.Name == "Level" || tn.Name == "World" || tn.Name == "Scene")
                return LevelMember(m);
            var v = Lookup(tn.Name);
            if (v != null)
            {
                if (v.IsObject)
                {
                    if (m.Name == "Name") return CValue.S(v.ObjectNode?.DisplayName ?? "");
                    var lv = ObjectProperty(v.ObjectNode, m, m.Name);
                    return lv?.Read() ?? CValue.F(0);
                }
                if (v.Instance != null)
                {
                    if (v.Instance.Fields.TryGetValue(m.Name, out var fieldVar)) return new VarLValue(fieldVar, _e).Read();
                    Error(m, $"'{v.Type.Name}' has no field '{m.Name}'");
                    return CValue.F(0);
                }
                if (v.Collection != null)
                {
                    if (m.Name is "Count" or "Length") return new CValue { Type = CsType.Float, Reg = v.Collection.CountReg };
                    if (m.Name == "Capacity") return CValue.F(v.Collection.Capacity);
                    Error(m, $"'{m.Name}' is not a member of {v.Type.Name}");
                    return CValue.F(0);
                }
                if (v.Type.Kind == CsTypeKind.String && m.Name == "Length")
                    return CValue.F(v.StrValue?.Length ?? 0);
                if (v.Type.Kind is CsTypeKind.Vector3 or CsTypeKind.Color || m.Name is "X" or "x" or "Y" or "y" or "Z" or "z" or "R" or "r" or "G" or "g" or "B" or "b" or "A" or "a")
                {
                    var obj = new VarLValue(v, _e).Read();
                    return Component(obj, m);
                }
                Error(m, $"'{tn.Name}' has no member '{m.Name}'");
                return CValue.F(0);
            }
            if (tn.Name == "GameObject" || tn.Name == "World" || tn.Name == "Scene")
            {
                Error(m, $"Use GameObject.Find(\"name\") to get a scene object");
                return CValue.F(0);
            }
        }
        if (m.Target is ThisExpr)
        {
            if (_thisInstance != null && _thisInstance.Fields.TryGetValue(m.Name, out var instField)) return new VarLValue(instField, _e).Read();
            if (_fields.TryGetValue(m.Name, out var f)) return new VarLValue(f, _e).Read();
            Error(m, $"Unknown field '{m.Name}'");
            return CValue.F(0);
        }
        var target = Eval(m.Target);
        if (target.Collection != null)
        {
            if (m.Name is "Count" or "Length") return new CValue { Type = CsType.Float, Reg = target.Collection.CountReg };
            if (m.Name == "Capacity") return CValue.F(target.Collection.Capacity);
            if (target.Collection.IsDictionary && m.Name is "Keys" or "Values")
            {
                Error(m, $"dict.{m.Name} cannot be stored at runtime; use foreach (var kv in dict) and read kv.Key / kv.Value");
                return CValue.F(0);
            }
            Error(m, $"'{m.Name}' is not a member of {target.Type.Name}");
            return CValue.F(0);
        }
        if (target.Inst != null) return ReadField(target.Inst, m.Name, m);
        if (target.Type.Kind == CsTypeKind.String && m.Name == "Length") return CValue.F(target.Str.Length);
        if (target.Type.Kind is CsTypeKind.Vector3) return Component(target, m);
        if (target.Type.Kind is CsTypeKind.Color) return Component(target, m);
        if (target.Node != null)
        {
            if (m.Name == "Name") return CValue.S(target.Node.DisplayName);
            var lv = ObjectProperty(target.Node, m, m.Name);
            return lv?.Read() ?? CValue.F(0);
        }
        Error(m, $"Cannot access member '{m.Name}'");
        return CValue.F(0);
    }

    private CValue Component(CValue v, MemberExpr m)
    {
        int idx = m.Name switch { "X" or "x" or "R" or "r" => 0, "Y" or "y" or "G" or "g" => 1, "Z" or "z" or "B" or "b" => 2, "W" or "w" or "A" or "a" => 3, _ => -1 };
        if (idx < 0)
        {
            if (m.Name is "Length" or "Magnitude" or "length" or "magnitude")
            {
                var p = v.Parts!;
                return VectorLength(p[0], p[1], p[2]);
            }
            if (m.Name is "SqrMagnitude" or "sqrMagnitude")
            {
                var p = v.Parts!;
                var xx = ScalarOp("*", p[0], p[0], m);
                var yy = ScalarOp("*", p[1], p[1], m);
                var zz = ScalarOp("*", p[2], p[2], m);
                return ScalarOp("+", ScalarOp("+", xx, yy, m), zz, m);
            }
            if (m.Name is "Normalized" or "normalized")
            {
                var p = v.Parts!;
                return VectorNormalize(p[0], p[1], p[2]);
            }
            Error(m, $"Unknown component '{m.Name}'");
            return CValue.F(0);
        }
        if (v.Parts == null || idx >= v.Parts.Length) { Error(m, "Missing vector component"); return CValue.F(0); }
        return v.Parts[idx];
    }

    private CValue TimeMember(MemberExpr m)
    {
        switch (m.Name)
        {
            case "DeltaTime":
            case "deltaTime":
                return new CValue { Type = CsType.Float, Reg = 4, RegKind = GasmOperandKind.Special };
            case "Time":
            case "time":
                _timeUsed = true;
                EnsureTime();
                return new CValue { Type = CsType.Float, Reg = _timeReg };
            case "FrameCount":
            case "frameCount":
                _frameUsed = true;
                EnsureFrame();
                return new CValue { Type = CsType.Float, Reg = _frameReg };
            default:
                Error(m, $"Unknown Time member '{m.Name}'");
                return CValue.F(0);
        }
    }

    private void EnsureTime() { if (_timeReg < 0) _timeReg = _e.AllocWorking("__time"); }
    private void EnsureFrame() { if (_frameReg < 0) _frameReg = _e.AllocWorking("__frame"); }

    // ================= continuation Code Blocks =================

    private void DeactivateContinuations(GrabLevel level, LevelNode block)
    {
        foreach (var page in level.AllNodes().Where(n => n.Generated && n.Kind == NodeKind.GASM && n.GeneratedFor == block.EditorId && n.PageIndex > 0))
        {
            page.Compiled = null;
            page.StartActive = false;
        }
    }

    private void EnsureBusNodes(GrabLevel level, LevelNode block)
    {
        _busNodes.Clear();
        _busNodes.AddRange(level.AllNodes().Where(n => n.Generated && n.GeneratedFor == block.EditorId && n.Kind == NodeKind.Static && n.HideModel));
        while (_busNodes.Count < 1) CreateBus(level, block);
    }

    private LevelNode CreateBus(GrabLevel level, LevelNode block)
    {
        var bus = new LevelNode
        {
            Name = "GASM Bus " + (_busNodes.Count + 1),
            Kind = NodeKind.Static,
            Shape = ShapeInfo.Cube,
            Material = 8,
            Position = new Vec3(0, -2000 - _busNodes.Count * 2, 0),
            Scale = new Vec3(0.05f, 0.05f, 0.05f),
            Color1 = new Col(0, 0, 0, 1),
            HideModel = true,
            Generated = true,
            GeneratedFor = block.EditorId
        };
        level.AddNode(bus);
        // Buses created mid-compile must also be resolvable by the emitter. Object ids
        // are assigned in node order, so a node appended now takes the next id.
        if (_objectIds != null)
        {
            // GRAB ids are 1-based hierarchy indices. A node appended now is the last
            // node, so its id is exactly the node count.
            _objectIds[bus] = (ulong)_level.AllNodes().Count();
        }
        _busNodes.Add(bus);
        return bus;
    }

    private LevelNode EnsurePageNode(int page)
    {
        var existing = _level.AllNodes().FirstOrDefault(n => n.Generated && n.Kind == NodeKind.GASM && n.GeneratedFor == _codeBlock!.EditorId && n.PageIndex == page);
        if (existing != null) return existing;
        var node = new LevelNode
        {
            Name = _codeBlock!.Name + " " + (page + 1),
            Kind = NodeKind.GASM,
            Position = _codeBlock.Position + new Vec3(0, 0.4f * page, 0),
            Generated = true,
            GeneratedFor = _codeBlock.EditorId,
            PageIndex = page
        };
        _level.AddNode(node);
        return node;
    }

    private int AllocateSlot()
    {
        int index = _busSlotCursor++;
        // Only Position components are used: they are ordinary floats in GASM, while
        // color channels clamp to 0..255 and rotation round-trips through a quaternion.
        int busIndex = index / 3;
        while (_busNodes.Count <= busIndex) CreateBus(_level, _codeBlock!);
        if (busIndex >= 40)
        {
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "The script needs more than 120 shared variables; split it into smaller Code Blocks.", File = "Script.cs" });
            return 0;
        }
        int within = index % 3;
        SpillSlot slot = within switch
        {
            0 => new SpillSlot { Bus = busIndex, Kind = GasmPropertyKind.Position, Component = 0, Suffix = ".Pos.X" },
            1 => new SpillSlot { Bus = busIndex, Kind = GasmPropertyKind.Position, Component = 1, Suffix = ".Pos.Y" },
            _ => new SpillSlot { Bus = busIndex, Kind = GasmPropertyKind.Position, Component = 2, Suffix = ".Pos.Z" }
        };
        _spillSlotDefs.Add(slot);
        return _spillSlotDefs.Count - 1;
    }

    private int[] SlotsFor(object owner, int count)
    {
        if (_spillMap.TryGetValue(owner, out var existing)) return existing;
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = AllocateSlot();
        _spillMap[owner] = slots;
        return slots;
    }

    /// <summary>A bus slot operand. Slots are InOut connection registers, not working registers.</summary>
    private GasmOperand Slot(int slotIndex) => GasmOperand.InOut(SlotRegister(slotIndex));

    private int SlotRegister(int slotIndex)
    {
        var slot = _spillSlotDefs[slotIndex];
        var bus = _busNodes[slot.Bus];
        var connection = _e.NodeConnection(bus);
        var specs = slot.Kind switch
        {
            GasmPropertyKind.Position => GasmEmitter.PositionSpecs,
            GasmPropertyKind.Rotation => GasmEmitter.RotationSpecs,
            _ => GasmEmitter.ColorSpecs
        };
        var prop = _e.EnsureProperty(connection, slot.Kind, specs);
        return _e.GetRegister(prop, slot.Component, RegKind.InOut, connection.Name + slot.Suffix);
    }

    private int AuxSlot(string name)
    {
        if (_auxSpill.TryGetValue(name, out var slots)) return slots[0];
        slots = new[] { AllocateSlot() };
        _auxSpill[name] = slots;
        return slots[0];
    }

    private void SpillVar(CVar v)
    {
        if (v.IsConst || v.IsObject || v.Type.Kind == CsTypeKind.String) return;
        if (v.Collection != null) { SpillCollection(v.Collection); return; }
        if (v.Instance != null) { foreach (var f in v.Instance.Fields.Values) SpillVar(f); return; }
        if (v.Regs.Length > 0)
        {
            var slots = SlotsFor(v, v.Regs.Length);
            for (int i = 0; i < v.Regs.Length; i++)
                _e.Emit(GasmOp.Set, Slot(slots[i]), GasmEmitter.Work(v.Regs[i]));
        }
        if (v.ArrayRegs.Length > 0)
        {
            var slots = SlotsFor(v, v.ArrayRegs.Length);
            for (int i = 0; i < v.ArrayRegs.Length; i++)
                _e.Emit(GasmOp.Set, Slot(slots[i]), GasmEmitter.Work(v.ArrayRegs[i]));
        }
    }

    private void LoadVar(CVar v)
    {
        if (v.IsConst || v.IsObject || v.Type.Kind == CsTypeKind.String) return;
        if (v.Collection != null) { LoadCollection(v.Collection); return; }
        if (v.Instance != null) { foreach (var f in v.Instance.Fields.Values) LoadVar(f); return; }
        if (v.Regs.Length > 0)
        {
            var slots = SlotsFor(v, v.Regs.Length);
            v.Regs = new int[v.Regs.Length];
            for (int i = 0; i < v.Regs.Length; i++)
            {
                v.Regs[i] = _e.AllocWorking("pg" + _pageIndex + "_" + v.Name + "_" + i + "_" + _pageRename++);
                _e.Emit(GasmOp.Set, GasmEmitter.Work(v.Regs[i]), Slot(slots[i]));
            }
        }
        if (v.ArrayRegs.Length > 0)
        {
            var slots = SlotsFor(v, v.ArrayRegs.Length);
            v.ArrayRegs = new int[v.ArrayRegs.Length];
            for (int i = 0; i < v.ArrayRegs.Length; i++)
            {
                v.ArrayRegs[i] = _e.AllocWorking("pg" + _pageIndex + "_" + v.Name + "_a" + i + "_" + _pageRename++);
                _e.Emit(GasmOp.Set, GasmEmitter.Work(v.ArrayRegs[i]), Slot(slots[i]));
            }
        }
    }

    private void SpillCollection(CCollection coll)
    {
        int count = 1 + coll.Slots.Length + coll.KeySlots.Length;
        var slots = SlotsFor(coll, count);
        int n = 0;
        _e.Emit(GasmOp.Set, Slot(slots[n++]), GasmEmitter.Work(coll.CountReg));
        foreach (var reg in coll.Slots)
            _e.Emit(GasmOp.Set, Slot(slots[n++]), GasmEmitter.Work(reg));
        foreach (var reg in coll.KeySlots)
            _e.Emit(GasmOp.Set, Slot(slots[n++]), GasmEmitter.Work(reg));
    }

    private void LoadCollection(CCollection coll)
    {
        int count = 1 + coll.Slots.Length + coll.KeySlots.Length;
        var slots = SlotsFor(coll, count);
        int n = 0;
        coll.CountReg = _e.AllocWorking("pg" + _pageIndex + "_cc" + (_pageRename++));
        _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.CountReg), Slot(slots[n++]));
        coll.Slots = new int[coll.Slots.Length];
        for (int i = 0; i < coll.Slots.Length; i++)
        {
            coll.Slots[i] = _e.AllocWorking("pg" + _pageIndex + "_cv" + (_pageRename++));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[i]), Slot(slots[n++]));
        }
        if (coll.KeySlots.Length > 0)
        {
            coll.KeySlots = new int[coll.KeySlots.Length];
            for (int i = 0; i < coll.KeySlots.Length; i++)
            {
                coll.KeySlots[i] = _e.AllocWorking("pg" + _pageIndex + "_ck" + (_pageRename++));
                _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.KeySlots[i]), Slot(slots[n++]));
            }
        }
    }

    private IEnumerable<CVar> LiveVars()
    {
        var seen = new HashSet<CVar>();
        foreach (var v in _fields.Values) if (seen.Add(v)) yield return v;
        foreach (var scope in _scopes)
            foreach (var v in scope.Values)
                if (seen.Add(v)) yield return v;
    }

    private int StoredRegCount(CVar v)
    {
        if (v.IsConst || v.IsObject || v.Type.Kind == CsTypeKind.String) return 0;
        if (v.Collection != null) return 1 + v.Collection.Slots.Length + v.Collection.KeySlots.Length;
        if (v.Instance != null) return v.Instance.Fields.Values.Sum(StoredRegCount);
        return v.Regs.Length + v.ArrayRegs.Length;
    }

    private int SpillCost() => LiveVars().Sum(StoredRegCount) * 2 + 34;

    private void SpillLive()
    {
        foreach (var v in LiveVars().ToList()) SpillVar(v);
        if (_allowSplit && _timeReg >= 0) _e.Emit(GasmOp.Set, Slot(AuxSlot("__time")), GasmEmitter.Work(_timeReg));
        if (_allowSplit && _frameReg >= 0) _e.Emit(GasmOp.Set, Slot(AuxSlot("__frame")), GasmEmitter.Work(_frameReg));
    }

    private void LoadLive()
    {
        foreach (var v in LiveVars().ToList()) LoadVar(v);
    }

    private void StartPage(int page)
    {
        _pageGateLabel = _e.NewLabel("page_gate");
        _pageReturnLabel = _e.NewLabel("page_ret");
        int run = _e.NewLabel("page_run");
        _e.Mark(_pageGateLabel);
        int cmp = _e.AllocTemp();
        _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), Slot(0), GasmEmitter.Const(page + 1));
        _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(run));
        _e.FreeTemp(cmp);
        _e.Emit(GasmOp.Sleep, GasmEmitter.Const(0));
        _e.Goto(_pageGateLabel);
        _e.Mark(run);
        LoadLive();
        // Time is shared through the bus so every page sees the same value.
        _timeReg = _e.AllocWorking("pg" + page + "_time");
        _e.Emit(GasmOp.Set, GasmEmitter.Work(_timeReg), Slot(AuxSlot("__time")));
        _frameReg = _e.AllocWorking("pg" + page + "_frame");
        _e.Emit(GasmOp.Set, GasmEmitter.Work(_frameReg), Slot(AuxSlot("__frame")));
        _pageBodyStart = _e.Program.Instructions.Count;
        if (page == 0)
        {
            _pageZeroProgram = _e.Program;
            _pageZeroEmitter = _e;
            _pageZeroBodyStart = _pageBodyStart;
            _pageZeroTimeReg = _timeReg;
            _pageZeroFrameReg = _frameReg;
        }
    }

    private void FinishPage(int nextGate)
    {
        _e.Mark(_pageReturnLabel);
        SpillLive();
        _e.Emit(GasmOp.Set, Slot(0), GasmEmitter.Const(nextGate));
        _e.Emit(GasmOp.Sleep, GasmEmitter.Const(0));
        _e.Goto(_pageGateLabel);
    }

    private void SplitPage()
    {
        FinishPage(_pageIndex + 2);
        var page = CapturePage(_pageNode!);
        if (_pageIndex == 0) _firstPage = page;
        else _continuations.Add(page);
        _pageIndex++;
        _pageNode = EnsurePageNode(_pageIndex);
        _e = new GasmEmitter(_e.ObjectIds, _diags);
        StartPage(_pageIndex);
    }

    private void CompileSplitBody(MethodDecl? method, bool startOnly)
    {
        _returnLabel = _e.NewLabel("unused_return");
        _scopes.Clear();
        _scopes.Add(_fields);
        var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
        _scopes.Add(scope);
        foreach (var p in method?.Params ?? new List<ParamDecl>())
        {
            var pt = ResolveType(p.TypeName, p.Line, p.Col) ?? CsType.Float;
            var v = new CVar { Name = p.Name, Type = pt, IsParameter = true };
            AllocateStorage(v);
            scope[p.Name] = v;
        }
        _e.Emit(GasmOp.Set, Slot(0), GasmEmitter.Const(1));
        _e.Emit(GasmOp.Set, Slot(AuxSlot("__time")), GasmEmitter.Const(0));
        _e.Emit(GasmOp.Set, Slot(AuxSlot("__frame")), GasmEmitter.Const(0));
        // Page 0 loads live variables from the bus as soon as it runs, so the initial
        // field values must be in the bus first (otherwise the first frame reads
        // uninitialised slot data such as a bus node's original Y = -2000).
        SpillLive();
        _pageIndex = 0;
        _pageNode = _codeBlock;
        StartPage(0);
        _returnLabel = _pageReturnLabel;
        bool any = false;
        foreach (var statement in method?.Body?.Statements ?? new List<Stmt>())
        {
            int spillCost = SpillCost();
            if (any && _e.Program.Instructions.Count + spillCost > GrabLimits.MaxInstructions - 34 && spillCost < 112)
                SplitPage();
            CompileStmt(statement);
            any = true;
            if (_e.Program.Instructions.Count + spillCost > GrabLimits.MaxInstructions - 6 && spillCost < 112)
                SplitPage();
        }
        if (!startOnly) EmitTriggerHooks();
        FinishPage(startOnly ? 0 : 1);
        var last = CapturePage(_pageNode!);
        if (_pageIndex == 0) _firstPage = last;
        else _continuations.Add(last);
        if (_timeUsed && _pageZeroProgram != null && _pageZeroTimeReg >= 0)
        {
            _pageZeroProgram.Instructions.Insert(Math.Min(_pageZeroBodyStart, _pageZeroProgram.Instructions.Count),
                new GasmInstruction(GasmOp.Add, GasmEmitter.Work(_pageZeroTimeReg), GasmEmitter.Work(_pageZeroTimeReg), GasmEmitter.Special(4)));
        }
        if (_frameUsed && _pageZeroProgram != null && _pageZeroTimeReg >= 0)
        {
            _pageZeroProgram.Instructions.Insert(Math.Min(_pageZeroBodyStart, _pageZeroProgram.Instructions.Count),
                new GasmInstruction(GasmOp.Add, GasmEmitter.Work(_pageZeroFrameReg), GasmEmitter.Work(_pageZeroFrameReg), GasmEmitter.Const(1)));
        }

    }

    private CValue MathConstant(MemberExpr m)
    {
        return m.Name switch
        {
            "PI" => CValue.F(MathF.PI),
            "Pi" => CValue.F(MathF.PI),
            "Deg2Rad" => CValue.F(MathF.PI / 180f),
            "Rad2Deg" => CValue.F(180f / MathF.PI),
            "Infinity" or "PositiveInfinity" => CValue.F(float.PositiveInfinity),
            "NegativeInfinity" => CValue.F(float.NegativeInfinity),
            "E" => CValue.F(MathF.E),
            _ => ErrorVal(m, $"Unknown constant '{m.Name}'")
        };
    }

    private CValue NumericConstant(MemberExpr m)
    {
        return m.Name switch
        {
            "MaxValue" => CValue.F(float.MaxValue),
            "MinValue" => CValue.F(float.MinValue),
            "Epsilon" => CValue.F(float.Epsilon),
            "PositiveInfinity" => CValue.F(float.PositiveInfinity),
            "NegativeInfinity" => CValue.F(float.NegativeInfinity),
            _ => ErrorVal(m, $"Unknown constant '{m.Name}'")
        };
    }

    private CValue VectorConstant(MemberExpr m)
    {
        return m.Name switch
        {
            "zero" or "Zero" => CValue.V(CValue.F(0), CValue.F(0), CValue.F(0)),
            "one" or "One" => CValue.V(CValue.F(1), CValue.F(1), CValue.F(1)),
            "up" or "Up" => CValue.V(CValue.F(0), CValue.F(1), CValue.F(0)),
            "down" or "Down" => CValue.V(CValue.F(0), CValue.F(-1), CValue.F(0)),
            "left" or "Left" => CValue.V(CValue.F(-1), CValue.F(0), CValue.F(0)),
            "right" or "Right" => CValue.V(CValue.F(1), CValue.F(0), CValue.F(0)),
            "forward" or "Forward" => CValue.V(CValue.F(0), CValue.F(0), CValue.F(1)),
            "back" or "Back" => CValue.V(CValue.F(0), CValue.F(0), CValue.F(-1)),
            _ => ErrorVal(m, $"Unknown Vector3 member '{m.Name}'")
        };
    }

    private CValue ColorConstant(MemberExpr m)
    {
        static CValue C(float r, float g, float b, float a = 1) => CValue.C(CValue.F(r), CValue.F(g), CValue.F(b), CValue.F(a));
        return m.Name switch
        {
            "white" or "White" => C(1, 1, 1),
            "black" or "Black" => C(0, 0, 0),
            "red" or "Red" => C(1, 0, 0),
            "green" or "Green" => C(0, 1, 0),
            "blue" or "Blue" => C(0, 0, 1),
            "yellow" or "Yellow" => C(1, 1, 0),
            "cyan" or "Cyan" => C(0, 1, 1),
            "magenta" or "Magenta" => C(1, 0, 1),
            "gray" or "grey" or "Gray" or "Grey" => C(0.5f, 0.5f, 0.5f),
            "clear" or "Clear" => C(0, 0, 0, 0),
            _ => ErrorVal(m, $"Unknown Color member '{m.Name}'")
        };
    }

    private CValue ShapeConstant(MemberExpr m)
    {
        int id = m.Name switch
        {
            "Cube" or "cube" => ShapeInfo.Cube,
            "Sphere" or "sphere" => ShapeInfo.Sphere,
            "Cylinder" or "cylinder" => ShapeInfo.Cylinder,
            "Pyramid" or "pyramid" => ShapeInfo.Pyramid,
            "Prism" or "prism" => ShapeInfo.Prism,
            "Cone" or "cone" => ShapeInfo.Cone,
            "PyramidSquare" or "Pyramid4" or "pyramid4" => ShapeInfo.PyramidSquare,
            _ => -1
        };
        if (id < 0) return ErrorVal(m, $"Unknown Shape constant '{m.Name}'. Use Shape.Cube, Shape.Sphere, Shape.Cylinder, Shape.Pyramid, Shape.Prism, Shape.Cone or Shape.PyramidSquare.");
        return CValue.F(id);
    }

    private CValue MaterialConstant(MemberExpr m)
    {
        int id = m.Name switch
        {
            "Default" or "default" => 0,
            "Grabbable" or "grabbable" => 1,
            "Ice" or "ice" => 2,
            "Lava" or "lava" => 3,
            "Wood" or "wood" => 4,
            "Grapplable" or "grapplable" => 5,
            "GrapplableLava" or "Grapplable_Lava" => 6,
            "GrabbableCrumbling" => 7,
            "DefaultColored" or "Colored" or "defaultColored" => 8,
            "Bouncing" or "bouncing" => 9,
            "Snow" or "snow" => 10,
            "Trigger" or "trigger" => 11,
            _ => -1
        };
        if (id < 0) return ErrorVal(m, $"Unknown Material constant '{m.Name}'. Use Material.Default, Grabbable, Ice, Lava, Wood, Grapplable, GrapplableLava, GrabbableCrumbling, DefaultColored, Bouncing, Snow or Trigger.");
        return CValue.F(id);
    }

    private CValue LightTypeConstant(MemberExpr m)
    {
        return m.Name switch
        {
            "Point" or "point" => CValue.F(0),
            "Spot" or "spot" => CValue.F(1),
            _ => ErrorVal(m, $"Unknown LightType constant '{m.Name}'. Use LightType.Point or LightType.Spot.")
        };
    }

    private CValue RandomMember(MemberExpr m)
    {
        if (m.Name is "Value" or "value") return RandomUnit(m);
        return ErrorVal(m, $"Unknown Random member '{m.Name}'. Use Random.Value, Random.Range, Random.Next or Random.Chance.");
    }

    private CValue RandomUnit(CsNode at)
    {
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Rand, GasmEmitter.Work(t), GasmEmitter.Const(0), GasmEmitter.Const(1));
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue RandomCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue Finish(CValue result)
        {
            foreach (var a in args) FreeValue(a);
            return result;
        }
        CValue A(int i) => i < args.Count ? args[i] : CValue.F(0);
        switch (name)
        {
            case "Value":
                return Finish(RandomUnit(c));
            case "Range":
            {
                if (args.Count != 2) { Error(c, "Random.Range needs (min, max)"); return Finish(CValue.F(0)); }
                var span = ScalarOp("-", A(1), A(0), c);
                int t = _e.AllocTemp();
                _e.Emit(GasmOp.Rand, GasmEmitter.Work(t), GasmEmitter.Const(0), span.Operand());
                FreeValue(span);
                var rangeUnit = new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
                var result = ScalarOp("+", A(0), rangeUnit, c);
                FreeValue(rangeUnit);
                return Finish(result);
            }
            case "Next":
            {
                if (args.Count == 0)
                {
                    var nextUnit = RandomUnit(c);
                    var result = ScalarMath1(GasmOp.Floor, nextUnit, c);
                    FreeValue(nextUnit);
                    return Finish(result);
                }
                if (args.Count is not (1 or 2)) { Error(c, "Random.Next needs (), (max) or (min, max)"); return Finish(CValue.F(0)); }
                var low = args.Count == 2 ? A(0) : CValue.F(0);
                var high = args.Count == 2 ? A(1) : A(0);
                var span = ScalarOp("-", high, low, c);
                int t = _e.AllocTemp();
                _e.Emit(GasmOp.Rand, GasmEmitter.Work(t), GasmEmitter.Const(0), span.Operand());
                FreeValue(span);
                var rangedUnit = new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
                var shifted = ScalarOp("+", low, rangedUnit, c);
                FreeValue(rangedUnit);
                var floored = ScalarMath1(GasmOp.Floor, shifted, c);
                FreeValue(shifted);
                return Finish(floored);
            }
            case "Chance":
            {
                if (args.Count != 1) { Error(c, "Random.Chance needs a probability from 0 to 1"); return Finish(CValue.F(0)); }
                var chanceUnit = RandomUnit(c);
                var result = ApplyBinary("<", chanceUnit, A(0), c);
                FreeValue(chanceUnit);
                return Finish(result);
            }
            default:
                Error(c, $"Unknown Random function '{name}'. Supported: Value, Range, Next, Chance");
                return Finish(CValue.F(0));
        }
    }

    private CValue DebugCall(string name, CallExpr c)
    {
        switch (name)
        {
            case "Log":
            case "LogWarning":
            case "LogError":
            case "LogFormat":
            case "Print":
                return ConsoleCall("WriteLine", c);
            case "Assert":
                Error(c, "Debug.Assert is not supported; write `if (!condition) Debug.Log(\"message\");` instead.");
                return new CValue { Type = CsType.Void };
            default:
                Error(c, $"Unknown Debug function '{name}'. Supported: Log, LogWarning, LogError, LogFormat");
                return new CValue { Type = CsType.Void };
        }
    }

    private CValue EvalCall(CallExpr c)
    {
        string? methodName = null;
        string? owner = null;
        if (c.Target is NameExpr n) methodName = n.Name;
        else if (c.Target is MemberExpr m && m.Target is NameExpr on) { owner = on.Name; methodName = m.Name; }
        else if (c.Target is MemberExpr m2 && m2.Target is ThisExpr) methodName = m2.Name;

        if (c.Target is MemberExpr consoleCall && IsConsoleTarget(consoleCall.Target))
            return ConsoleCall(consoleCall.Name, c);
        if (owner is "Random" or "random")
            return RandomCall(methodName!, c);
        if (owner is "Debug" or "debug")
            return DebugCall(methodName!, c);
        if (owner is "Voxel" or "voxel")
            return VoxelCall(methodName!, c);
        if (owner is "Animator" or "animator")
            return AnimatorCall(methodName!, c);
        if (owner is "Mathf" or "Math" or "MathF" or "MathF" or "mathf")
            return MathCall(methodName!, c);
        if (owner == "Vector3") return VectorCall(methodName!, c);
        if (owner == "Color") return ColorCall(methodName!, c);
        if (owner == "Array") return ArrayCall(methodName!, c);
        if (owner is "char" or "Char") return CharCall(methodName!, c);
        if (owner == "Convert") return ConvertCall(methodName!, c);
        if (owner is "string" or "String") return StaticStringCall(methodName!, c);
        if (owner is "int" or "Int32" or "float" or "Single" or "double" or "bool" or "Boolean")
            return NumericStaticCall(methodName!, c);
        if (owner is "GameObject" or "World" or "Scene")
        {
            if (methodName == "Find") return FindCall(c);
            if (methodName is "Log" or "Print") return ConsoleCall("WriteLine", c);
        }
        if (methodName == "Find" && owner == null) return FindCall(c);
        if (methodName is "Log" or "Print" && owner == null)
        {
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = "Log calls have no effect in GASM", Line = c.Line, Column = c.Col, File = "Script.cs" });
            return CValue.F(0);
        }
        if (methodName == "StartCoroutine" && c.Args.Count == 1)
            return StartCoroutineCall(c);

        if (c.Target is MemberExpr thisM && thisM.Target is ThisExpr)
        {
            if (_thisInstance != null) return InvokeInstance(_thisInstance, thisM.Name, c);
            var method = _class?.Methods.FirstOrDefault(x => !x.IsConstructor && x.Name == thisM.Name && AcceptsArgs(x, c.Args.Count) && !x.IsStatic);
            if (method != null) return IsCoroutine(method) ? StartCoroutine(method, c, c) : InvokeScript(method, c);
            Error(c, $"Unknown method '{thisM.Name}'");
            return CValue.F(0);
        }
        if (c.Target is MemberExpr baseM && baseM.Target is BaseExpr)
        {
            var bd = _thisInstance?.Def?.Base;
            if (bd?.FindMethod(baseM.Name, c.Args.Count) is { } bm) return InvokeMethod(bm, c, _thisInstance, false);
            Error(c, $"'{baseM.Name}' was not found in the base class");
            return CValue.F(0);
        }
        if (c.Target is MemberExpr sm && sm.Target is NameExpr typeName && _typeDecls.ContainsKey(typeName.Name) && Lookup(typeName.Name) == null)
        {
            var def = GetTypeDef(_typeDecls[typeName.Name], new List<string>(), sm.Line, sm.Col);
            var binding = def?.Methods.FirstOrDefault(x => x.Decl.Name == sm.Name && AcceptsArgs(x.Decl, c.Args.Count));
            if (binding != null)
            {
                if (!binding.Decl.IsStatic) Error(c, $"'{sm.Name}' is an instance method; call it on an object");
                return InvokeMethod(binding, c, null, false);
            }
            Error(c, $"'{typeName.Name}' has no method '{sm.Name}'");
            return CValue.F(0);
        }
        // Instance, collection, string and numeric methods.
        if (c.Target is MemberExpr mm)
        {
            var receiver = Eval(mm.Target);
            if (receiver.Node != null)
            {
                var r = ObjectCall(receiver.Node, mm.Name, c);
                FreeValue(receiver);
                return r;
            }
            if (receiver.Collection != null)
            {
                var r = CollectionCall(receiver.Collection, mm.Name, c);
                FreeValue(receiver);
                return r;
            }
            if (receiver.Inst != null)
            {
                var r = InvokeInstance(receiver.Inst, mm.Name, c);
                FreeValue(receiver);
                return r;
            }
            if (receiver.Type.Kind == CsTypeKind.String)
            {
                var r = StringCall(receiver, mm.Name, c);
                FreeValue(receiver);
                return r;
            }
            if (receiver.IsNumeric && mm.Name == "ToString")
            {
                if (!receiver.IsConst) Error(c, "ToString() on a runtime number is not supported; store the number in a sign field instead");
                var r = receiver.IsConst ? CValue.S(FormatNumber(receiver.Const)) : CValue.S("");
                FreeValue(receiver);
                return r;
            }
            FreeValue(receiver);
        }
        if (methodName != null && _class != null)
        {
            var method = _class.Methods.FirstOrDefault(x => !x.IsConstructor && x.Name == methodName && AcceptsArgs(x, c.Args.Count) && !x.IsStatic);
            if (method != null) return IsCoroutine(method) ? StartCoroutine(method, c, c) : InvokeScript(method, c);
            if (methodName is "Start" or "Update") { Error(c, $"{methodName}() is called by the engine and cannot be called manually"); return CValue.F(0); }
        }
        Error(c, $"Unknown method '{methodName}'");
        return CValue.F(0);
    }

    private static bool IsConsoleTarget(Expr e) => e switch
    {
        NameExpr n => n.Name == "Console",
        MemberExpr { Name: "Console" } m => m.Target is NameExpr { Name: "System" },
        _ => false
    };

    /// <summary>
    /// Console.Write / WriteLine. GASM has no print instruction, so output is typed
    /// into a small generated Sign next to the Code Block. The engine's Play mode
    /// also mirrors every completed line into its Console tab.
    /// </summary>
    private CValue ConsoleCall(string name, CallExpr c)
    {
        switch (name)
        {
            case "Write":
            case "WriteLine":
            {
                bool newline = name == "WriteLine";
                var node = EnsureConsoleNode();
                var parts = ConsoleText(c);
                // WriteLine starts a fresh line unless Write just began one; this keeps
                // Write("Score: "); WriteLine(score); on one line like a real console.
                bool clear = newline && !_consoleLineOpen;
                WriteLValue.EmitSignText(_e, node, parts, clearFirst: clear, newline: newline);
                string preview = (clear ? "" : node.Text) + PreviewText(parts) + (newline ? "\n" : "");
                node.Text = preview.Length > GrabLimits.MaxSignCharacters
                    ? preview[..GrabLimits.MaxSignCharacters]
                    : preview;
                if (newline) _consoleLineOpen = false;
                else if (parts.Count > 0) _consoleLineOpen = true;
                int characters = parts.Where(p => p.Value == null).Sum(p => (p.Text ?? "").Length);
                if (characters > GrabLimits.MaxSignCharacters)
                    Warn(c, $"This console line is {characters} characters; GRAB signs stop at {GrabLimits.MaxSignCharacters}.");
                return new CValue { Type = CsType.Void };
            }
            case "Clear":
            {
                if (c.Args.Count > 0) Error(c, "Console.Clear() takes no arguments");
                var node = EnsureConsoleNode();
                var connection = _e.NodeConnection(node);
                var prop = _e.EnsureProperty(connection, GasmPropertyKind.Sign, GasmEmitter.SignSpecs);
                int control = _e.GetRegister(prop, 2, RegKind.Output, connection.Name + ".Sign.Control");
                _e.Emit(GasmOp.Set, new GasmOperand { Kind = GasmOperandKind.Output, Index = control }, GasmEmitter.Const(1));
                node.Text = "";
                _consoleLineOpen = false;
                return new CValue { Type = CsType.Void };
            }
            case "ReadLine":
            case "ReadKey":
                Error(c, $"Console.{name}() cannot work in GRAB: GASM Code Blocks have no keyboard input. Read Player or Trigger properties instead.");
                return CValue.S("");
            case "Beep":
            case "ResetColor":
            case "SetCursorPosition":
            case "SetWindowSize":
                Warn(c, $"Console.{name}() has no effect; GRAB Code Blocks cannot control a console window.");
                return new CValue { Type = CsType.Void };
            default:
                Error(c, $"Console.{name}() is not supported. Use Console.Write, Console.WriteLine or Console.Clear.");
                return new CValue { Type = CsType.Void };
        }
    }

    /// <summary>Builds the text a Console call prints, keeping live numbers as parts.</summary>
    private List<TextPart> ConsoleText(CallExpr c)
    {
        var parts = new List<TextPart>();
        if (c.Args.Count > 1 && c.Args[0] is LiteralExpr { Type.Kind: CsTypeKind.String } format)
        {
            if (format.Value.Contains('{')) AppendCompositeFormat(format.Value, c, parts);
            else
            {
                parts.Add(new TextPart { Text = format.Value });
                Warn(c, "The first argument is a plain string, so the extra arguments are ignored (matches real C#). Use {0} placeholders or + to join them.");
            }
        }
        else
        {
            foreach (var arg in c.Args)
                AppendConsoleValue(arg, Eval(arg), parts);
        }
        var merged = MergeTextParts(parts);
        foreach (var p in merged) if (p.Value != null) p.Value.IsTemp = false;
        return merged;
    }

    private void AppendCompositeFormat(string format, CallExpr c, List<TextPart> parts)
    {
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add(new TextPart { Text = literal.ToString() });
            literal.Clear();
        }

        for (int i = 0; i < format.Length; i++)
        {
            char ch = format[i];
            if (ch == '{')
            {
                if (i + 1 < format.Length && format[i + 1] == '{') { literal.Append('{'); i++; continue; }
                int close = format.IndexOf('}', i + 1);
                if (close < 0)
                {
                    Error(c, "The Console format string has an unclosed '{'.");
                    literal.Append(format[(i + 1)..]);
                    break;
                }
                string spec = format[(i + 1)..close];
                int colon = spec.IndexOf(':');
                string indexPart = colon >= 0 ? spec[..colon] : spec;
                if (colon >= 0)
                    Warn(c, $"Format specifier '{{{spec}}}' is ignored; Console prints the plain value.");
                if (int.TryParse(indexPart.Trim(), out int argIndex) && argIndex >= 0 && argIndex < c.Args.Count - 1)
                {
                    Flush();
                    var arg = c.Args[argIndex + 1];
                    AppendConsoleValue(arg, Eval(arg), parts);
                }
                else
                    Error(c, $"The Console format placeholder '{{{spec}}}' has no matching argument.");
                i = close;
            }
            else if (ch == '}')
            {
                if (i + 1 < format.Length && format[i + 1] == '}') { literal.Append('}'); i++; }
                else { Error(c, "The Console format string has a single '}'; write '}}' for a literal brace."); literal.Append('}'); }
            }
            else literal.Append(ch);
        }
        Flush();
    }

    private void AppendConsoleValue(Expr arg, CValue v, List<TextPart> parts)
    {
        if (arg is LiteralExpr { Char: char ch })
        {
            parts.Add(new TextPart { Text = ch.ToString() });
            return;
        }
        if (v.TextParts != null) { parts.AddRange(v.TextParts); return; }
        if (v.IsNull) return;
        switch (v.Type.Kind)
        {
            case CsTypeKind.String:
                parts.Add(new TextPart { Text = v.Str });
                return;
            case CsTypeKind.Bool:
                if (v.IsConst) parts.Add(new TextPart { Text = v.Const != 0 ? "True" : "False" });
                else
                {
                    AppendConsoleNumber(v, parts);
                    Warn(arg, "A bool known only at runtime prints as 0/1; use a constant bool or separate WriteLines for true and false.");
                }
                return;
            case CsTypeKind.Vector3 when v.Parts is { Length: >= 3 } xyz:
                parts.Add(new TextPart { Text = "(" });
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) parts.Add(new TextPart { Text = ", " });
                    AppendConsoleNumber(xyz[i], parts);
                }
                parts.Add(new TextPart { Text = ")" });
                return;
            case CsTypeKind.Color when v.Parts is { Length: >= 4 } rgba:
                parts.Add(new TextPart { Text = "RGBA(" });
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) parts.Add(new TextPart { Text = ", " });
                    AppendConsoleNumber(rgba[i], parts);
                }
                parts.Add(new TextPart { Text = ")" });
                return;
            default:
                if (v.IsNumeric) { AppendConsoleNumber(v, parts); return; }
                Warn(arg, $"Console cannot print a value of type {v.Type.Name}; convert it to text first.");
                return;
        }
    }

    private static void AppendConsoleNumber(CValue v, List<TextPart> parts)
    {
        if (v.IsConst) parts.Add(new TextPart { Text = FormatNumber(v.Const) });
        else parts.Add(new TextPart { Value = v });
    }

    private LevelNode EnsureConsoleNode()
    {
        if (_consoleNode != null) return _consoleNode;
        var block = _codeBlock!;
        var existing = _level.AllNodes().FirstOrDefault(n =>
            n.Kind == NodeKind.Sign && n.Generated && n.GeneratedFor == block.EditorId &&
            n.Name.StartsWith("GASM Console", StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var sign = new LevelNode
            {
                Name = "GASM Console",
                Kind = NodeKind.Sign,
                Text = "",
                Position = block.WorldPosition + new Vec3(0, 2.2f, 0),
                Rotation = Quat.Identity,
                SignScale = 1f,
                SignColor = Col.White,
                Generated = true,
                GeneratedFor = block.EditorId
            };
            _level.AddNode(sign);
            // Like buses, a node appended during compilation is beyond the ids that
            // were captured at the start, so register it with the emitter now.
            if (_objectIds != null)
                _objectIds[sign] = (ulong)_level.AllNodes().Count();
            existing = sign;
        }
        existing.Text = "";
        _consoleNode = existing;
        return existing;
    }

    private sealed class CoroutineSegment
    {
        public List<Stmt> Statements { get; } = new();
        public Expr? Yield { get; set; }
        public Expr? Await { get; set; }
    }

    private static bool IsCoroutine(MethodDecl m) =>
        m.IsAsync || m.ReturnTypeName.Contains("IEnumerator", StringComparison.Ordinal) ||
        m.ReturnTypeName.Contains("IEnumerable", StringComparison.Ordinal) ||
        (m.Body != null && m.Body.Statements.Any(s => s is YieldStmt));

    private CValue StartCoroutineCall(CallExpr c)
    {
        if (c.Args.Count != 1)
        {
            Error(c, "StartCoroutine needs one coroutine call, e.g. StartCoroutine(Fade())");
            return CValue.F(0);
        }
        if (c.Args[0] is CallExpr inner && inner.Target is NameExpr n)
        {
            var method = _class?.Methods.FirstOrDefault(x => !x.IsConstructor && x.Name == n.Name && x.Params.Count == inner.Args.Count);
            if (method != null) return StartCoroutine(method, inner, c);
            Error(c, $"'{n.Name}' is not a method of {_class?.Name}");
            return CValue.F(0);
        }
        Error(c, "StartCoroutine needs a method call, e.g. StartCoroutine(Fade())");
        return CValue.F(0);
    }

    /// <summary>
    /// Compiles an iterator / async method into a resumable state machine at the call
    /// site. Each top-level yield (or await) is a state; the next segment runs on the
    /// next Update, or after the requested delay.
    /// </summary>
    private CValue StartCoroutine(MethodDecl method, CallExpr call, CsNode at)
    {
        var body = method.Body;
        if (body == null)
        {
            Error(at, $"'{method.Name}' has no body to run as a coroutine");
            return CValue.F(0);
        }
        var segments = new List<CoroutineSegment>();
        var current = new CoroutineSegment();
        foreach (var stmt in body.Statements)
        {
            if (stmt is YieldStmt ys)
            {
                if (!ys.IsBreak) current.Yield = ys.Value;
                segments.Add(current);
                current = new CoroutineSegment();
                if (ys.IsBreak) break;
                continue;
            }
            if (stmt is ExprStmt { Expr: AwaitExpr aw })
            {
                current.Await = aw.Operand;
                segments.Add(current);
                current = new CoroutineSegment();
                continue;
            }
            current.Statements.Add(stmt);
        }
        if (current.Statements.Count > 0 || segments.Count == 0) segments.Add(current);
        if (segments.Count == 1 && segments[0].Yield == null && segments[0].Await == null)
            return InvokeScript(method, call);

        if (!_coroutineState.TryGetValue(call, out var regs))
        {
            int id = _coroutineCounter++;
            regs = new[] { _e.AllocWorking("co" + id + "_state"), _e.AllocWorking("co" + id + "_wait") };
            _coroutineState[call] = regs;
            _coroutineInitRegs.Add(regs[0]);
            _coroutineInitRegs.Add(regs[1]);
            if (_allowSplit)
            {
                if (!_pageCoroutineInits.TryGetValue(_pageIndex, out var pageInit))
                {
                    pageInit = new List<int>();
                    _pageCoroutineInits[_pageIndex] = pageInit;
                }
                pageInit.Add(regs[0]);
                pageInit.Add(regs[1]);
            }
        }
        int state = regs[0];
        int wait = regs[1];
        int end = _e.NewLabel("co_end");
        var labels = new int[segments.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = _e.NewLabel("co_step" + i);

        // Dispatcher: jump to the segment the state machine is currently on.
        for (int i = 0; i < labels.Length; i++)
        {
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), GasmEmitter.Work(state), GasmEmitter.Const(i));
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(labels[i]));
            _e.FreeTemp(cmp);
        }
        _e.Goto(end);

        var savedScopes = _scopes.ToList();
        var savedReturn = _returnLabel;
        var savedType = _returnType;
        var savedRegs = _returnRegs;
        var savedThis = _thisInstance;
        _scopes.Clear();
        _scopes.Add(_fields);
        var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
        _scopes.Add(scope);
        var args = call.Args.Select(Eval).ToList();
        for (int i = 0; i < method.Params.Count && i < args.Count; i++)
        {
            var p = method.Params[i];
            var pt = ResolveType(p.TypeName, p.Line, p.Col) ?? CsType.Float;
            var v = new CVar { Name = p.Name, Type = pt, IsParameter = true, TypeKey = "co_p_" + p.Name + "_" + _coroutineCounter++ };
            AllocateStorage(v);
            new VarLValue(v, _e).Write(args[i]);
            scope[p.Name] = v;
        }
        _returnLabel = _e.NewLabel("co_return");
        _returnType = CsType.Void;
        _returnRegs = Array.Empty<int>();

        for (int i = 0; i < segments.Count; i++)
        {
            _e.Mark(labels[i]);
            if (i > 0 && segments[i - 1].Await != null)
            {
                // already handled by the previous segment's wait register
            }
            if (i > 0 && HasWait(segments[i - 1]))
            {
                _e.Emit(GasmOp.Sub, GasmEmitter.Work(wait), GasmEmitter.Work(wait), GasmEmitter.Special(4));
                int still = _e.AllocTemp();
                _e.Emit(GasmOp.Greater, GasmEmitter.Work(still), GasmEmitter.Work(wait), GasmEmitter.Const(0));
                _e.Emit(GasmOp.If, GasmEmitter.Work(still), GasmOperand.Label(end));
                _e.FreeTemp(still);
            }
            foreach (var stmt in segments[i].Statements) CompileStmt(stmt);
            int next = i + 1 < segments.Count ? i + 1 : -1;
            var waitValue = WaitExpression(segments[i].Yield ?? segments[i].Await, segments[i].Yield == null && segments[i].Await == null ? at : at);
            if (waitValue != null)
            {
                _e.Emit(GasmOp.Set, GasmEmitter.Work(wait), waitValue.Operand());
                FreeValue(waitValue);
            }
            else _e.Emit(GasmOp.Set, GasmEmitter.Work(wait), GasmEmitter.Const(0));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(state), GasmEmitter.Const(next < 0 ? 0 : next));
            _e.Goto(end);
        }
        _e.Mark(_returnLabel);
        _e.Emit(GasmOp.Set, GasmEmitter.Work(state), GasmEmitter.Const(0));
        _e.Goto(end);
        _e.Mark(end);

        _scopes.Clear();
        _scopes.AddRange(savedScopes);
        _returnLabel = savedReturn;
        _returnType = savedType;
        _returnRegs = savedRegs;
        _thisInstance = savedThis;
        foreach (var a in args) FreeValue(a);
        return new CValue { Type = CsType.Void };
    }

    private bool HasWait(CoroutineSegment segment)
    {
        var expr = segment.Yield ?? segment.Await;
        if (expr == null) return false;
        if (expr is LiteralExpr { IsNull: true }) return false;
        return true;
    }

    /// <summary>wait time in seconds, or null for "next frame".</summary>
    private CValue? WaitExpression(Expr? e, CsNode at)
    {
        if (e == null) return null;
        if (e is LiteralExpr { IsNull: true }) return null;
        if (e is NewExpr ne)
        {
            if (ne.TypeName is "WaitForSeconds" or "WaitForSecondsRealtime")
                return ne.Args.Count > 0 ? Eval(ne.Args[0]) : CValue.F(0);
            Error(at, $"yield return {ne.TypeName} is not supported; use null or new WaitForSeconds(seconds)");
            return null;
        }
        if (e is CallExpr call && call.Target is MemberExpr m && m.Target is NameExpr t && t.Name == "Task")
        {
            if (m.Name is "Delay" or "DelayMilliseconds")
            {
                var ms = call.Args.Count > 0 ? Eval(call.Args[0]) : CValue.F(0);
                var seconds = ScalarOp("/", ms, CValue.F(1000f), at);
                return seconds;
            }
            if (m.Name is "Yield" or "CompletedTask") return null;
        }
        if (e is CallExpr inner && inner.Target is NameExpr n2 && 
            _class?.Methods.FirstOrDefault(x => x.Name == n2.Name && x.Params.Count == inner.Args.Count) is { } nested && IsCoroutine(nested))
        {
            Error(at, "awaiting another coroutine is not supported; call StartCoroutine instead");
            return null;
        }
        Error(at, "await supports Task.Delay(ms), Task.Yield() or null");
        return null;
    }

    private static readonly float DictionaryEmpty = -2147483000f;

    private CCollection AllocateCollection(CsType type, int capacity, CsNode at)
    {
        capacity = Math.Clamp(capacity, 1, 32);
        var coll = new CCollection
        {
            Type = type,
            IsDictionary = type.Kind == CsTypeKind.Dictionary,
            ElementType = type.Element ?? CsType.Float,
            KeyType = type.KeyType,
            Capacity = capacity,
            CountReg = _e.AllocWorking("coll" + _instanceCounter++ + "_count")
        };
        coll.Slots = new int[capacity];
        for (int i = 0; i < capacity; i++) coll.Slots[i] = _e.AllocWorking("coll" + _instanceCounter++ + "_v" + i);
        if (coll.IsDictionary)
        {
            coll.KeySlots = new int[capacity];
            for (int i = 0; i < capacity; i++)
            {
                coll.KeySlots[i] = _e.AllocWorking("coll" + _instanceCounter++ + "_k" + i);
                _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Const(DictionaryEmpty));
            }
        }
        _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(0));
        return coll;
    }

    private static int HashKey(string text)
    {
        unchecked
        {
            int hash = 216613626;
            foreach (char c in text) hash = (hash ^ c) * 16777619;
            return hash & 0xFFFFFF;
        }
    }

    private CValue? KeyValue(CValue key, CsType keyType, CsNode at)
    {
        if (keyType.Kind == CsTypeKind.String)
        {
            if (key.Type.Kind != CsTypeKind.String || !key.IsConst)
            {
                Error(at, "Dictionary string keys must be compile-time constants (GASM has no string registers). Use a number key for runtime lookups.");
                return null;
            }
            return CValue.F(HashKey(key.Str));
        }
        return key;
    }

    /// <summary>Reads element[index] with a runtime index using a compare-and-branch chain.</summary>
    private CValue CollectionElementRead(CCollection coll, CValue index, CsNode at)
    {
        int idx = Materialize(index);
        int result = _e.AllocTemp();
        int end = _e.NewLabel("coll_end");
        var labels = new int[coll.Capacity];
        for (int k = 0; k < coll.Capacity; k++)
        {
            labels[k] = _e.NewLabel("coll_" + k);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), GasmEmitter.Work(idx), GasmEmitter.Const(k));
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(labels[k]));
            _e.FreeTemp(cmp);
        }
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(0));
        _e.Goto(end);
        for (int k = 0; k < coll.Capacity; k++)
        {
            _e.Mark(labels[k]);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Work(coll.Slots[k]));
            _e.Goto(end);
        }
        _e.Mark(end);
        _e.FreeTemp(idx);
        return new CValue { Type = coll.ElementType, Reg = result, IsTemp = true };
    }

    private void CollectionElementWrite(CCollection coll, CValue index, CValue value, CsNode at)
    {
        int idx = Materialize(index);
        int end = _e.NewLabel("collw_end");
        var labels = new int[coll.Capacity];
        for (int k = 0; k < coll.Capacity; k++)
        {
            labels[k] = _e.NewLabel("collw_" + k);
            int cmp = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), GasmEmitter.Work(idx), GasmEmitter.Const(k));
            _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(labels[k]));
            _e.FreeTemp(cmp);
        }
        _e.Goto(end);
        for (int k = 0; k < coll.Capacity; k++)
        {
            _e.Mark(labels[k]);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[k]), value.Operand());
            _e.Goto(end);
        }
        _e.Mark(end);
        _e.FreeTemp(idx);
    }

    private void CollectionAdd(CCollection coll, CValue value, CsNode at)
    {
        int cmp = _e.AllocTemp();
        int skip = _e.NewLabel("coll_full");
        int ok = _e.NewLabel("coll_add");
        _e.Emit(GasmOp.Less, GasmEmitter.Work(cmp), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(coll.Capacity));
        _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(ok));
        _e.Goto(skip);
        _e.Mark(ok);
        CollectionElementWrite(coll, new CValue { Type = CsType.Float, Reg = coll.CountReg }, value, at);
        _e.Emit(GasmOp.Add, GasmEmitter.Work(coll.CountReg), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(1));
        _e.Mark(skip);
        _e.FreeTemp(cmp);
    }

    private CValue CollectionCall(CCollection coll, string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue result = new() { Type = CsType.Void };
        switch (name)
        {
            case "Add":
                if (coll.IsDictionary)
                {
                    if (args.Count < 2) { Error(c, "Dictionary.Add needs a key and a value"); break; }
                    var key = KeyValue(args[0], coll.KeyType!, c);
                    if (key != null) DictionarySet(coll, key, args[1], c, addOnly: true);
                }
                else if (args.Count > 0) CollectionAdd(coll, args[0], c);
                break;
            case "AddRange":
                if (coll.IsDictionary) { Error(c, "Dictionary has no AddRange"); break; }
                if (args.Count != 1 || args[0].ArrayRegs == null)
                {
                    Error(c, "AddRange needs a fixed-size array; call Add in a loop for other sources");
                    break;
                }
                foreach (int reg in args[0].ArrayRegs)
                    CollectionAdd(coll, new CValue { Type = coll.ElementType, Reg = reg }, c);
                break;
            case "Clear":
                _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(0));
                if (coll.IsDictionary)
                    foreach (var k in coll.KeySlots) _e.Emit(GasmOp.Set, GasmEmitter.Work(k), GasmEmitter.Const(DictionaryEmpty));
                break;
            case "RemoveAt":
                if (coll.IsDictionary) { Error(c, "Dictionary has no RemoveAt"); break; }
                if (args.Count > 0) CollectionRemoveAt(coll, args[0], c);
                break;
            case "Remove":
                if (coll.IsDictionary)
                {
                    if (args.Count > 0)
                    {
                        var key = KeyValue(args[0], coll.KeyType!, c);
                        if (key != null) DictionaryRemove(coll, key, c);
                    }
                }
                else if (args.Count > 0) CollectionRemoveValue(coll, args[0], c);
                break;
            case "Contains":
            case "ContainsKey":
                if (coll.IsDictionary)
                {
                    result = DictionaryContains(coll, KeyValue(args[0], coll.KeyType!, c) ?? CValue.F(0), c);
                }
                else
                {
                    var idx = CollectionIndexOf(coll, args[0], c);
                    int below = _e.AllocTemp();
                    _e.Emit(GasmOp.Less, GasmEmitter.Work(below), GasmEmitter.Work(idx.Reg), GasmEmitter.Const(0));
                    int found = _e.AllocTemp();
                    _e.Emit(GasmOp.Not, GasmEmitter.Work(found), GasmEmitter.Work(below));
                    _e.FreeTemp(below);
                    FreeValue(idx);
                    result = new CValue { Type = CsType.Bool, Reg = found, IsTemp = true };
                }
                break;
            case "IndexOf":
                if (coll.IsDictionary) { Error(c, "Dictionary has no IndexOf"); break; }
                result = CollectionIndexOf(coll, args[0], c);
                break;
            case "TryGetValue":
                if (!coll.IsDictionary || args.Count < 1) { Error(c, "TryGetValue is a Dictionary method"); break; }
                result = DictionaryTryGet(coll, args[0], c);
                break;
            case "Insert":
                if (coll.IsDictionary) { Error(c, "Dictionary has no Insert"); break; }
                if (args.Count >= 2) CollectionInsert(coll, args[0], args[1], c);
                break;
            case "Sort":
            case "Reverse":
                Error(c, $"List.{name} is not supported by the GASM backend yet");
                break;
            default:
                Error(c, $"Unknown collection method '{name}'");
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    private void CollectionInsert(CCollection coll, CValue index, CValue value, CsNode at)
    {
        int idx = Materialize(index);
        int full = _e.AllocTemp();
        int done = _e.NewLabel("ins_done");
        int hasRoom = _e.NewLabel("ins_room");
        _e.Emit(GasmOp.Less, GasmEmitter.Work(full), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(coll.Capacity));
        _e.Emit(GasmOp.If, GasmEmitter.Work(full), GasmOperand.Label(hasRoom));
        _e.Goto(done);
        _e.Mark(hasRoom);
        for (int k = coll.Capacity - 1; k >= 1; k--)
        {
            int before = _e.AllocTemp();
            int skip = _e.NewLabel("ins_skip" + k);
            _e.Emit(GasmOp.Greater, GasmEmitter.Work(before), GasmEmitter.Work(idx), GasmEmitter.Const(k - 1));
            _e.Emit(GasmOp.If, GasmEmitter.Work(before), GasmOperand.Label(skip));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[k]), GasmEmitter.Work(coll.Slots[k - 1]));
            _e.Mark(skip);
            _e.FreeTemp(before);
        }
        CollectionElementWrite(coll, new CValue { Type = CsType.Float, Reg = idx }, value, at);
        _e.Emit(GasmOp.Add, GasmEmitter.Work(coll.CountReg), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(1));
        _e.Mark(done);
        _e.FreeTemp(full);
        _e.FreeTemp(idx);
    }

    private void CollectionRemoveAt(CCollection coll, CValue index, CsNode at)
    {
        int idx = Materialize(index);
        for (int k = 0; k < coll.Capacity - 1; k++)
        {
            int before = _e.AllocTemp();
            _e.Emit(GasmOp.Less, GasmEmitter.Work(before), GasmEmitter.Work(idx), GasmEmitter.Const(k + 1));
            int skip = _e.NewLabel("rm_skip");
            int neg = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(neg), GasmEmitter.Work(before));
            _e.Emit(GasmOp.If, GasmEmitter.Work(neg), GasmOperand.Label(skip));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[k]), GasmEmitter.Work(coll.Slots[k + 1]));
            _e.Mark(skip);
            _e.FreeTemp(before); _e.FreeTemp(neg);
        }
        _e.Emit(GasmOp.Sub, GasmEmitter.Work(coll.CountReg), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(1));
        int low = _e.AllocTemp();
        int done = _e.NewLabel("rm_done");
        _e.Emit(GasmOp.Less, GasmEmitter.Work(low), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(0));
        _e.Emit(GasmOp.If, GasmEmitter.Work(low), GasmOperand.Label(done));
        _e.FreeTemp(low);
        _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(0));
        _e.Mark(done);
        _e.FreeTemp(idx);
    }

    private void CollectionRemoveValue(CCollection coll, CValue value, CsNode at)
    {
        var found = CollectionIndexOf(coll, value, at);
        int skip = _e.NewLabel("rmv_skip");
        int neg = _e.AllocTemp();
        _e.Emit(GasmOp.Less, GasmEmitter.Work(neg), GasmEmitter.Work(found.Reg), GasmEmitter.Const(0));
        _e.Emit(GasmOp.If, GasmEmitter.Work(neg), GasmOperand.Label(skip));
        CollectionRemoveAt(coll, found, at);
        _e.Mark(skip);
        _e.FreeTemp(neg);
    }

    private CValue CollectionIndexOf(CCollection coll, CValue value, CsNode at)
    {
        int result = _e.AllocTemp();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(-1));
        int end = _e.NewLabel("idx_end");
        for (int k = 0; k < coll.Capacity; k++)
        {
            int next = _e.NewLabel("idx_next" + k);
            int inRange = _e.AllocTemp();
            _e.Emit(GasmOp.Less, GasmEmitter.Work(inRange), GasmEmitter.Const(k), GasmEmitter.Work(coll.CountReg));
            int outOfRange = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(outOfRange), GasmEmitter.Work(inRange));
            _e.Emit(GasmOp.If, GasmEmitter.Work(outOfRange), GasmOperand.Label(next));
            _e.FreeTemp(inRange); _e.FreeTemp(outOfRange);
            int match = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(match), GasmEmitter.Work(coll.Slots[k]), value.Operand());
            int noMatch = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(noMatch), GasmEmitter.Work(match));
            _e.Emit(GasmOp.If, GasmEmitter.Work(noMatch), GasmOperand.Label(next));
            _e.FreeTemp(match); _e.FreeTemp(noMatch);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(k));
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        return new CValue { Type = CsType.Float, Reg = result, IsTemp = true };
    }

    private void DictionarySet(CCollection coll, CValue key, CValue value, CsNode at, bool addOnly = false)
    {
        int k = Materialize(key);
        int end = _e.NewLabel("dict_end");
        for (int i = 0; i < coll.Capacity; i++)
        {
            int found = _e.NewLabel("dict_found" + i);
            int empty = _e.NewLabel("dict_empty" + i);
            int next = _e.NewLabel("dict_next" + i);
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(k));
            _e.Emit(GasmOp.If, GasmEmitter.Work(eq), GasmOperand.Label(found));
            _e.FreeTemp(eq);
            int isFree = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(isFree), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Const(DictionaryEmpty));
            _e.Emit(GasmOp.If, GasmEmitter.Work(isFree), GasmOperand.Label(empty));
            _e.FreeTemp(isFree);
            _e.Goto(next);
            _e.Mark(found);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[i]), value.Operand());
            _e.Goto(end);
            _e.Mark(empty);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(k));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[i]), value.Operand());
            _e.Emit(GasmOp.Add, GasmEmitter.Work(coll.CountReg), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(1));
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        _e.FreeTemp(k);
    }

    private void DictionaryRemove(CCollection coll, CValue key, CsNode at)
    {
        int k = Materialize(key);
        int end = _e.NewLabel("dict_rm_end");
        for (int i = 0; i < coll.Capacity; i++)
        {
            int next = _e.NewLabel("dict_rm_next" + i);
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(k));
            int diff = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(diff), GasmEmitter.Work(eq));
            _e.Emit(GasmOp.If, GasmEmitter.Work(diff), GasmOperand.Label(next));
            _e.FreeTemp(eq); _e.FreeTemp(diff);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Const(DictionaryEmpty));
            _e.Emit(GasmOp.Set, GasmEmitter.Work(coll.Slots[i]), GasmEmitter.Const(0));
            _e.Emit(GasmOp.Sub, GasmEmitter.Work(coll.CountReg), GasmEmitter.Work(coll.CountReg), GasmEmitter.Const(1));
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        _e.FreeTemp(k);
    }

    private CValue DictionaryContains(CCollection coll, CValue key, CsNode at)
    {
        int k = Materialize(key);
        int result = _e.AllocTemp();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(0));
        int end = _e.NewLabel("dict_has_end");
        for (int i = 0; i < coll.Capacity; i++)
        {
            int next = _e.NewLabel("dict_has_next" + i);
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(k));
            int diff = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(diff), GasmEmitter.Work(eq));
            _e.Emit(GasmOp.If, GasmEmitter.Work(diff), GasmOperand.Label(next));
            _e.FreeTemp(eq); _e.FreeTemp(diff);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(1));
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        _e.FreeTemp(k);
        return new CValue { Type = CsType.Bool, Reg = result, IsTemp = true };
    }

    private CValue DictionaryTryGet(CCollection coll, CValue key, CallExpr c)
    {
        var target = c.Args.Count > 1 ? ResolveLValue(c.Args[1]) : null;
        int k = Materialize(key);
        int result = _e.AllocTemp();
        _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(0));
        int end = _e.NewLabel("dict_try_end");
        for (int i = 0; i < coll.Capacity; i++)
        {
            int next = _e.NewLabel("dict_try_next" + i);
            int eq = _e.AllocTemp();
            _e.Emit(GasmOp.Equal, GasmEmitter.Work(eq), GasmEmitter.Work(coll.KeySlots[i]), GasmEmitter.Work(k));
            int diff = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(diff), GasmEmitter.Work(eq));
            _e.Emit(GasmOp.If, GasmEmitter.Work(diff), GasmOperand.Label(next));
            _e.FreeTemp(eq); _e.FreeTemp(diff);
            _e.Emit(GasmOp.Set, GasmEmitter.Work(result), GasmEmitter.Const(1));
            target?.Write(new CValue { Type = coll.ElementType, Reg = coll.Slots[i] });
            _e.Goto(end);
            _e.Mark(next);
        }
        _e.Mark(end);
        _e.FreeTemp(k);
        return new CValue { Type = CsType.Bool, Reg = result, IsTemp = true };
    }

    private CValue ReadField(CInstance inst, string name, CsNode at)
    {
        if (inst.Fields.TryGetValue(name, out var field)) return new VarLValue(field, _e).Read();
        Error(at, $"'{inst.Type.Name}' has no member '{name}'");
        return CValue.F(0);
    }

    private CValue ReadStatic(string typeName, string field, CsNode at)
    {
        var def = GetTypeDef(_typeDecls[typeName], new List<string>(), at.Line, at.Col);
        if (def == null) return CValue.F(0);
        var v = GetStaticVar(def, field, at);
        return v == null ? CValue.F(0) : new VarLValue(v, _e).Read();
    }

    private CVar? GetStaticVar(TypeDef def, string field, CsNode at)
    {
        string key = def.Key + "." + field;
        if (_statics.TryGetValue(key, out var existing)) return existing;
        var fd = def.Fields.FirstOrDefault(x => x.Decl.Name == field && x.Decl.IsStatic);
        if (fd.Decl == null) { Error(at, $"'{def.Name}' has no static field '{field}'"); return null; }
        var v = new CVar
        {
            Name = field, Type = fd.Type, IsStatic = true, IsReadonly = fd.Decl.IsReadonly,
            IsConst = fd.Decl.IsConst, TypeKey = "st_" + def.Name + "_" + field
        };
        _statics[key] = v;
        AllocateStorage(v);
        var saved = _thisInstance;
        _thisInstance = null;
        _typeBindings.Add(def.Bindings);
        if (fd.Decl.Initializer != null)
        {
            var val = Eval(fd.Decl.Initializer);
            if (fd.Decl.IsConst) { v.ConstValue = val; }
            else new VarLValue(v, _e).Write(val);
            FreeValue(val);
        }
        else InitDefault(v);
        _typeBindings.RemoveAt(_typeBindings.Count - 1);
        _thisInstance = saved;
        return v;
    }

    private CsType? InferArgType(Expr e)
    {
        if (e is LiteralExpr lit) return lit.Type;
        if (e is NameExpr n && Lookup(n.Name) is { } v) return v.Type;
        if (e is NewExpr) return null;
        return null;
    }

    private CValue InvokeScript(MethodDecl method, CallExpr call) =>
        InvokeMethod(new MethodBinding { Decl = method, Owner = _scriptDef! }, call, null, false);

    private CValue InvokeInstance(CInstance inst, string name, CallExpr call)
    {
        if (name == "ToString" && call.Args.Count == 0)
            return CValue.S(inst.Type.Name);
        var def = inst.Def;
        MethodBinding? binding = def?.FindMethod(name, call.Args.Count);
        if (binding == null)
        {
            // An interface-typed variable holds a concrete instance: search its type.
            if (inst.Type.Definition is TypeDef td) binding = td.FindMethod(name, call.Args.Count);
        }
        if (binding == null)
        {
            Error(call, $"'{inst.Type.Name}' has no method '{name}'");
            return CValue.F(0);
        }
        return InvokeMethod(binding, call, inst, false);
    }

    /// <summary>Compiles one method call inline, binding parameters and this.</summary>
    private CValue InvokeMethod(MethodBinding binding, CallExpr call, CInstance? instance, bool discard)
    {
        var method = binding.Decl;
        if (_methodStack.Contains(method.Name))
        {
            Error(call, $"Recursive call to '{method.Name}' is not supported (GASM has no call stack)");
            return CValue.F(0);
        }
        _methodStack.Push(method.Name);
        var bindings = new Dictionary<string, CsType>(binding.Owner.Bindings, StringComparer.Ordinal);
        if (method.GenericParams.Count > 0)
        {
            for (int i = 0; i < method.GenericParams.Count; i++)
            {
                CsType? t = null;
                if (i < call.GenericArgs.Count) t = ResolveType(call.GenericArgs[i], call.Line, call.Col);
                else if (i < call.Args.Count) t = InferArgType(call.Args[i]);
                bindings[method.GenericParams[i]] = t ?? CsType.Error;
            }
        }
        _typeBindings.Add(bindings);

        var args = new List<CValue>();
        var outArgs = new List<(ParamDecl Param, CLValue Target)>();
        int fixedCount = FixedParamCount(method);
        if (!AcceptsArgs(method, call.Args.Count))
        {
            Error(call, method.Params.Count > 0 && method.Params[^1].IsParams
                ? $"'{method.Name}' needs at least {fixedCount} argument(s), got {call.Args.Count}"
                : $"'{method.Name}' expects {method.Params.Count} arguments, got {call.Args.Count}");
            _typeBindings.RemoveAt(_typeBindings.Count - 1);
            _methodStack.Pop();
            return CValue.F(0);
        }
        for (int i = 0; i < fixedCount; i++)
        {
            bool isOut = call.OutArgs.Contains(i) || method.Params[i].IsOut || method.Params[i].IsRef;
            if (isOut)
            {
                var lv = ResolveLValue(call.Args[i]);
                if (lv == null) { Error(call, "out/ref needs a variable"); args.Add(CValue.F(0)); continue; }
                outArgs.Add((method.Params[i], lv));
                args.Add(lv.Read());
            }
            else args.Add(Eval(call.Args[i]));
        }
        if (method.Params.Count > 0 && method.Params[^1].IsParams)
        {
            var p = method.Params[^1];
            var arrayType = ResolveType(p.TypeName, p.Line, p.Col, true) ?? CsType.Error;
            if (arrayType.Kind != CsTypeKind.Array)
            {
                Error(call, $"params parameter '{p.Name}' must be an array, e.g. params float[] values");
                _typeBindings.RemoveAt(_typeBindings.Count - 1);
                _methodStack.Pop();
                return CValue.F(0);
            }
            int rest = call.Args.Count - fixedCount;
            var regs = new int[Math.Max(1, rest)];
            for (int i = 0; i < regs.Length; i++)
            {
                regs[i] = _e.AllocWorking("params_" + p.Name + "_" + _instanceCounter++);
                if (i < rest)
                {
                    var value = Eval(call.Args[fixedCount + i]);
                    var m = Materialize(value);
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), GasmEmitter.Work(m));
                    _e.FreeTemp(m);
                    FreeValue(value);
                }
                else _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), GasmEmitter.Const(0));
            }
            args.Add(CValue.A(arrayType.Element ?? CsType.Float, regs));
        }
        var returnType = ResolveType(method.ReturnTypeName, method.Line, method.Col) ?? CsType.Void;
        var savedScopes = _scopes.ToList();
        var savedReturn = _returnLabel;
        var savedType = _returnType;
        var savedRegs = _returnRegs;
        var savedThis = _thisInstance;
        var savedBreak = new Stack<int>(_breakLabels.Reverse());
        var savedContinue = new Stack<int>(_continueLabels.Reverse());
        _breakLabels.Clear(); _continueLabels.Clear();
        _scopes.Clear();
        _scopes.Add(_fields);
        if (instance != null) _scopes.Add(instance.Fields);
        var scope = new Dictionary<string, CVar>(StringComparer.Ordinal);
        _scopes.Add(scope);
        _thisInstance = instance;
        for (int i = 0; i < method.Params.Count; i++)
        {
            var p = method.Params[i];
            var pt = ResolveType(p.TypeName, p.Line, p.Col, p.IsArray) ?? CsType.Float;
            var v = new CVar { Name = p.Name, Type = pt, IsParameter = true, TypeKey = "p_" + p.Name + "_" + _instanceCounter++ };
            AllocateStorage(v);
            if (pt.Kind == CsTypeKind.Array)
            {
                v.ElementType = args[i].ElementType ?? pt.Element;
                v.ArrayRegs = new int[args[i].ArrayRegs?.Length ?? 0];
                for (int slot = 0; slot < v.ArrayRegs.Length; slot++)
                {
                    v.ArrayRegs[slot] = _e.AllocWorking("p_" + p.Name + "_" + slot + "_" + _instanceCounter++);
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(v.ArrayRegs[slot]), GasmEmitter.Work(args[i].ArrayRegs![slot]));
                }
            }
            else new VarLValue(v, _e).Write(args[i]);
            scope[p.Name] = v;
        }
        int resultLabel = _e.NewLabel(method.Name + "_ret");
        _returnLabel = resultLabel;
        _returnType = returnType;
        int rc = returnType.Kind is CsTypeKind.Vector3 ? 3 : returnType.Kind is CsTypeKind.Color ? 4 : returnType.Kind == CsTypeKind.Void ? 0 : 1;
        _returnRegs = new int[rc];
        for (int i = 0; i < rc; i++) _returnRegs[i] = _e.AllocTemp();
        if (method.Body != null)
        {
            foreach (var s in method.Body.Statements) CompileStmt(s);
        }
        else if (method.ExpressionBody != null)
        {
            var v = Eval(method.ExpressionBody);
            if (rc > 0) new VarLValue(new CVar { Type = returnType, Regs = _returnRegs, TypeKey = "ret" + _instanceCounter++ }, _e).Write(v);
            FreeValue(v);
        }
        _e.Mark(resultLabel);
        CValue result;
        if (returnType.Kind == CsTypeKind.Vector3)
            result = new CValue { Type = returnType, Parts = _returnRegs.Select(r => (CValue)new CValue { Type = CsType.Float, Reg = r, IsTemp = true }).ToArray() };
        else if (returnType.Kind == CsTypeKind.Color)
            result = new CValue { Type = returnType, Parts = _returnRegs.Select(r => (CValue)new CValue { Type = CsType.Float, Reg = r, IsTemp = true }).ToArray() };
        else if (returnType.Kind == CsTypeKind.Void) result = new CValue { Type = CsType.Void };
        else result = new CValue { Type = returnType, Reg = _returnRegs.Length > 0 ? _returnRegs[0] : -1, IsTemp = true };

        // Copy out/ref parameters back into the caller's variables.
        foreach (var (param, target) in outArgs)
            if (scope.TryGetValue(param.Name, out var pv)) target.Write(pv.Instance != null ? new CValue { Type = pv.Type, Inst = pv.Instance } : new VarLValue(pv, _e).Read());

        _scopes.Clear();
        _scopes.AddRange(savedScopes);
        _returnLabel = savedReturn;
        _returnType = savedType;
        _returnRegs = savedRegs;
        _thisInstance = savedThis;
        _breakLabels.Clear(); foreach (var x in savedBreak.Reverse()) _breakLabels.Push(x);
        _continueLabels.Clear(); foreach (var x in savedContinue.Reverse()) _continueLabels.Push(x);
        _typeBindings.RemoveAt(_typeBindings.Count - 1);
        _methodStack.Pop();
        foreach (var a in args) FreeValue(a);
        return result;
    }

    /// <summary>
    /// Builds a text value out of literal pieces and live numbers. When every piece
    /// is constant the result is a plain string; otherwise the pieces are kept so a
    /// sign write can emit a live ASCII + number sequence.
    /// </summary>
    private CValue ConcatText(params CValue[] values)
    {
        var parts = new List<TextPart>();
        foreach (var v in values) AddTextParts(v, parts);
        var merged = MergeTextParts(parts);
        if (merged.All(p => p.Value == null))
            return CValue.S(string.Concat(merged.Select(p => p.Text)));
        // The pieces outlive this expression until the sign write consumes them.
        foreach (var p in merged) if (p.Value != null) p.Value.IsTemp = false;
        return new CValue { Type = CsType.String, IsConst = false, TextParts = merged };
    }

    private void AddTextParts(CValue v, List<TextPart> parts)
    {
        if (v.TextParts != null) { parts.AddRange(v.TextParts); return; }
        if (v.Type.Kind == CsTypeKind.String)
        {
            parts.Add(new TextPart { Text = v.IsConst ? v.Str : v.Str });
            return;
        }
        if (v.IsNumeric)
        {
            if (v.IsConst) parts.Add(new TextPart { Text = FormatNumber(v.Const) });
            else parts.Add(new TextPart { Value = v });
            return;
        }
        parts.Add(new TextPart { Text = "" });
    }

    private static List<TextPart> MergeTextParts(List<TextPart> parts)
    {
        var merged = new List<TextPart>();
        foreach (var p in parts)
        {
            if (p.Value != null) { merged.Add(p); continue; }
            if (p.Text == null || p.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Value == null)
                merged[^1] = new TextPart { Text = merged[^1].Text + p.Text };
            else merged.Add(new TextPart { Text = p.Text });
        }
        return merged;
    }

    internal static string PreviewText(List<TextPart> parts)
        => string.Concat(parts.Select(p => p.Value != null ? "0" : p.Text));

    private static string FormatNumber(float v) =>
        v == MathF.Floor(v) && MathF.Abs(v) < 1e7f
            ? ((int)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// String members. All of them run while compiling: GASM has no string
    /// registers, so text has to be known before the program is emitted.
    /// </summary>
    private CValue StringCall(CValue receiver, string name, CallExpr c)
    {
        if (receiver.Type.Kind != CsTypeKind.String)
        {
            Error(c, $"{name}() is only available on strings");
            return CValue.F(0);
        }
        if (!receiver.IsConst)
        {
            Error(c, $"{name}() needs compile-time text. GASM has no string registers, so strings are evaluated while compiling.");
            return CValue.S("");
        }

        string text = receiver.Str;
        var args = c.Args.Select(Eval).ToList();
        string Str(int i) => i < args.Count && args[i].Type.Kind == CsTypeKind.String ? args[i].Str : "";
        bool Const(int i) => i < args.Count && args[i].IsConst;
        int Num(int i) => Const(i) ? (int)MathF.Round(args[i].Const) : 0;

        CValue result;
        switch (name)
        {
            case "ToUpper":
            case "ToUpperInvariant":
                result = CValue.S(text.ToUpperInvariant());
                break;
            case "ToLower":
            case "ToLowerInvariant":
                result = CValue.S(text.ToLowerInvariant());
                break;
            case "Trim": result = CValue.S(args.Count > 0 && args[0].IsConst ? text.Trim((char)Num(0)) : text.Trim()); break;
            case "TrimStart": result = CValue.S(args.Count > 0 && args[0].IsConst ? text.TrimStart((char)Num(0)) : text.TrimStart()); break;
            case "TrimEnd": result = CValue.S(args.Count > 0 && args[0].IsConst ? text.TrimEnd((char)Num(0)) : text.TrimEnd()); break;
            case "ToString": result = CValue.S(text); break;
            case "Substring":
            {
                int start = Num(0);
                if (!Const(0) || start < 0 || start > text.Length)
                {
                    Error(c, $"Substring start {start} is outside \"{text}\"");
                    result = CValue.S("");
                    break;
                }
                if (args.Count > 1)
                {
                    int len = Num(1);
                    if (!Const(1) || len < 0 || start + len > text.Length)
                    {
                        Error(c, $"Substring length {len} is outside \"{text}\"");
                        result = CValue.S("");
                        break;
                    }
                    result = CValue.S(text.Substring(start, len));
                }
                else result = CValue.S(text[start..]);
                break;
            }
            case "Contains":
                result = CValue.B(args.Count > 0 && args[0].IsConst &&
                    text.Contains(args[0].Type.Kind == CsTypeKind.String ? Str(0) : ((char)Num(0)).ToString(), StringComparison.Ordinal));
                break;
            case "StartsWith":
                result = CValue.B(args.Count > 0 && args[0].IsConst && text.StartsWith(Str(0), StringComparison.Ordinal));
                break;
            case "EndsWith":
                result = CValue.B(args.Count > 0 && args[0].IsConst && text.EndsWith(Str(0), StringComparison.Ordinal));
                break;
            case "IndexOf":
            case "LastIndexOf":
            {
                if (args.Count == 0 || !args[0].IsConst) { Error(c, $"{name} needs a constant needle"); result = CValue.F(-1); break; }
                bool last = name == "LastIndexOf";
                bool hasStart = args.Count > 1;
                int start = 0;
                if (hasStart)
                {
                    if (!Const(1)) { Error(c, $"{name} start index must be a compile-time constant"); result = CValue.F(-1); break; }
                    start = Math.Clamp(Num(1), 0, Math.Max(0, text.Length - 1));
                }
                if (args[0].Type.Kind == CsTypeKind.String)
                    result = CValue.F(last
                        ? (hasStart ? text.LastIndexOf(Str(0), start, StringComparison.Ordinal) : text.LastIndexOf(Str(0), StringComparison.Ordinal))
                        : (hasStart ? text.IndexOf(Str(0), start, StringComparison.Ordinal) : text.IndexOf(Str(0), StringComparison.Ordinal)));
                else
                    result = CValue.F(last
                        ? (hasStart ? text.LastIndexOf((char)Num(0), start) : text.LastIndexOf((char)Num(0)))
                        : (hasStart ? text.IndexOf((char)Num(0), start) : text.IndexOf((char)Num(0))));
                break;
            }
            case "CompareTo":
                result = CValue.F(string.CompareOrdinal(text, Str(0)));
                break;
            case "Replace":
                if (args.Count < 2 || args[0].Type.Kind != CsTypeKind.String || args[1].Type.Kind != CsTypeKind.String)
                {
                    Error(c, "Replace needs two constant strings, e.g. \"a-b\".Replace(\"-\", \"+\")");
                    result = CValue.S(text);
                    break;
                }
                result = CValue.S(text.Replace(Str(0), Str(1), StringComparison.Ordinal));
                break;
            case "Remove":
            {
                int start = Num(0);
                if (!Const(0) || start < 0 || start > text.Length) { Error(c, "Remove start is outside the string"); result = CValue.S(text); break; }
                if (args.Count > 1) { int count = Num(1); result = count < 0 || start + count > text.Length ? CValue.S(text) : CValue.S(text.Remove(start, count)); }
                else result = CValue.S(text.Remove(start));
                break;
            }
            case "Insert":
            {
                int at = Num(0);
                if (!Const(0) || at < 0 || at > text.Length) { Error(c, "Insert index is outside the string"); result = CValue.S(text); break; }
                result = CValue.S(text.Insert(at, Str(1)));
                break;
            }
            case "PadLeft":
                result = CValue.S(args.Count > 1 && Const(1) ? text.PadLeft(Num(0), (char)Num(1)) : text.PadLeft(Num(0)));
                break;
            case "PadRight":
                result = CValue.S(args.Count > 1 && Const(1) ? text.PadRight(Num(0), (char)Num(1)) : text.PadRight(Num(0)));
                break;
            case "Equals":
                result = CValue.B(Str(0) == text);
                break;
            case "Split":
            case "ToCharArray":
                Error(c, $"string.{name}() is not supported; GASM cannot allocate collections at runtime");
                result = CValue.S("");
                break;
            default:
                Error(c, $"Unknown string method '{name}'. Supported: Length, ToUpper, ToLower, Trim, Substring, Contains, StartsWith, EndsWith, IndexOf, Replace, Remove, Insert, PadLeft, PadRight, Equals, ToString");
                result = CValue.S("");
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    private CValue FormatStringCall(CallExpr c)
    {
        if (c.Args.Count == 0 || c.Args[0] is not LiteralExpr { Type.Kind: CsTypeKind.String } format)
        {
            Error(c, "string.Format needs a literal format string, e.g. string.Format(\"Score: {0}\", score)");
            return CValue.S("");
        }
        var parts = ConsoleText(c);
        if (parts.All(p => p.Value == null))
            return CValue.S(string.Concat(parts.Select(p => p.Text)));
        foreach (var p in parts) if (p.Value != null) p.Value.IsTemp = false;
        return new CValue { Type = CsType.String, IsConst = false, TextParts = parts };
    }

    private CValue StaticStringCall(string name, CallExpr c)
    {
        if (name == "Format") return FormatStringCall(c);
        var args = c.Args.Select(Eval).ToList();
        CValue result;
        switch (name)
        {
            case "Concat":
                result = ConcatText(args.ToArray());
                break;
            case "Join":
            {
                string sep = args.Count > 0 ? args[0].Str : "";
                var parts = new List<TextPart>();
                for (int i = 1; i < args.Count; i++)
                {
                    if (i > 1) parts.Add(new TextPart { Text = sep });
                    AddTextParts(args[i], parts);
                }
                var merged = MergeTextParts(parts);
                result = merged.All(p => p.Value == null)
                    ? CValue.S(string.Concat(merged.Select(p => p.Text)))
                    : new CValue { Type = CsType.String, IsConst = false, TextParts = merged };
                break;
            }
            case "IsNullOrEmpty":
                result = CValue.B(args.Count == 0 || string.IsNullOrEmpty(args[0].Str));
                break;
            case "IsNullOrWhiteSpace":
                result = CValue.B(args.Count == 0 || string.IsNullOrWhiteSpace(args[0].Str));
                break;
            case "Compare":
            case "CompareOrdinal":
                result = CValue.F(args.Count >= 2 ? string.CompareOrdinal(args[0].Str, args[1].Str) : 0);
                break;
            case "Copy":
                result = CValue.S(args.Count > 0 ? args[0].Str : "");
                break;
            default:
                Error(c, $"Unknown string function '{name}'. Supported: Concat, Join, Format, IsNullOrEmpty, IsNullOrWhiteSpace, Copy");
                result = CValue.S("");
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    /// <summary>Array helpers that only shuffle existing element registers, so no heap is needed.</summary>
    private CValue ArrayCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        var result = new CValue { Type = CsType.Void };
        int[]? Regs(int i) => i < args.Count ? args[i].ArrayRegs : null;
        if (name is not ("Reverse" or "IndexOf" or "Copy" or "Fill" or "Clear"))
        {
            Error(c, $"Unknown Array function '{name}'. Supported: Reverse, IndexOf, Copy, Fill, Clear");
            foreach (var a in args) FreeValue(a);
            return result;
        }
        var array = Regs(0);
        if (array == null)
        {
            Error(c, $"Array.{name} needs a fixed-size array as its first argument");
            foreach (var a in args) FreeValue(a);
            return result;
        }
        switch (name)
        {
            case "Reverse":
                for (int i = 0; i < array.Length / 2; i++)
                    _e.Emit(GasmOp.Swap, GasmEmitter.Work(array[i]), GasmEmitter.Work(array[array.Length - 1 - i]));
                break;
            case "IndexOf":
            {
                if (args.Count < 2) { Error(c, "Array.IndexOf needs (array, value)"); break; }
                int r = _e.AllocTemp();
                int end = _e.NewLabel("arr_idx_end");
                _e.Emit(GasmOp.Set, GasmEmitter.Work(r), GasmEmitter.Const(-1));
                var found = new int[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    int cmp = _e.AllocTemp();
                    found[i] = _e.NewLabel("arr_idx_" + i);
                    _e.Emit(GasmOp.Equal, GasmEmitter.Work(cmp), GasmEmitter.Work(array[i]), args[1].Operand());
                    _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(found[i]));
                    _e.FreeTemp(cmp);
                }
                _e.Goto(end);
                for (int i = 0; i < array.Length; i++)
                {
                    _e.Mark(found[i]);
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(r), GasmEmitter.Const(i));
                    _e.Goto(end);
                }
                _e.Mark(end);
                result = new CValue { Type = CsType.Float, Reg = r, IsTemp = true };
                break;
            }
            case "Copy":
            {
                if (args.Count < 2 || args[1].ArrayRegs == null) { Error(c, "Array.Copy needs (source, destination[, length])"); break; }
                int length = Math.Min(array.Length, args[1].ArrayRegs.Length);
                if (args.Count > 2)
                {
                    if (!args[2].IsConst) { Error(c, "Array.Copy length must be a compile-time constant"); break; }
                    length = Math.Clamp((int)MathF.Round(args[2].Const), 0, length);
                }
                for (int i = 0; i < length; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(args[1].ArrayRegs[i]), GasmEmitter.Work(array[i]));
                break;
            }
            case "Fill":
            {
                if (args.Count < 2) { Error(c, "Array.Fill needs (array, value)"); break; }
                for (int i = 0; i < array.Length; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(array[i]), args[1].Operand());
                break;
            }
            case "Clear":
                for (int i = 0; i < array.Length; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(array[i]), GasmEmitter.Const(0));
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    /// <summary>char.* helpers. Characters are compile-time values, like strings.</summary>
    private CValue CharCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue result;
        if (args.Count == 0 || !args[0].IsConst)
        {
            Error(c, $"{name} needs a compile-time character; GASM has no string/char registers");
            foreach (var a in args) FreeValue(a);
            return CValue.F(0);
        }
        char Ch(int i) => i < args.Count && args[i].IsConst ? (char)(int)MathF.Round(args[i].Const) : '\0';
        bool Has(int i) => i < args.Count && args[i].IsConst;
        switch (name)
        {
            case "IsDigit": result = CValue.B(Has(0) && char.IsDigit(Ch(0))); break;
            case "IsLetter": result = CValue.B(Has(0) && char.IsLetter(Ch(0))); break;
            case "IsLetterOrDigit": result = CValue.B(Has(0) && char.IsLetterOrDigit(Ch(0))); break;
            case "IsNumber": result = CValue.B(Has(0) && char.IsNumber(Ch(0))); break;
            case "IsWhiteSpace": result = CValue.B(Has(0) && char.IsWhiteSpace(Ch(0))); break;
            case "IsUpper": result = CValue.B(Has(0) && char.IsUpper(Ch(0))); break;
            case "IsLower": result = CValue.B(Has(0) && char.IsLower(Ch(0))); break;
            case "IsPunctuation": result = CValue.B(Has(0) && char.IsPunctuation(Ch(0))); break;
            case "IsSymbol": result = CValue.B(Has(0) && char.IsSymbol(Ch(0))); break;
            case "IsControl": result = CValue.B(Has(0) && char.IsControl(Ch(0))); break;
            case "ToUpper":
            case "ToUpperInvariant":
                result = CValue.F(char.ToUpperInvariant(Ch(0)));
                break;
            case "ToLower":
            case "ToLowerInvariant":
                result = CValue.F(char.ToLowerInvariant(Ch(0)));
                break;
            case "ToString": result = CValue.S(Ch(0).ToString()); break;
            case "Parse":
                result = args.Count > 0 && args[0].Type.Kind == CsTypeKind.String && args[0].Str.Length == 1
                    ? CValue.F(args[0].Str[0]) : CValue.F(0);
                if (args.Count == 0 || args[0].Type.Kind != CsTypeKind.String || args[0].Str.Length != 1)
                    Error(c, "char.Parse needs a constant string with exactly one character");
                break;
            default:
                Error(c, $"Unknown char function '{name}'. Supported: IsDigit, IsLetter, IsLetterOrDigit, IsNumber, IsWhiteSpace, IsUpper, IsLower, IsPunctuation, IsSymbol, IsControl, ToUpper, ToLower, ToString, Parse");
                result = CValue.F(0);
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    /// <summary>Convert.* value conversions. There is no string runtime, so text conversions are compile-time.</summary>
    private CValue ConvertCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue result = CValue.F(0);
        var v = args.Count > 0 ? args[0] : CValue.F(0);
        switch (name)
        {
            case "ToInt32":
            case "ToInt64":
            case "ToUInt32":
                if (v.Type.Kind == CsTypeKind.String && v.IsConst && float.TryParse(v.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedInt))
                    result = CValue.F(MathF.Round(parsedInt));
                else
                    result = v;
                break;
            case "ToSingle":
            case "ToDouble":
            case "ToDecimal":
                result = v.Type.Kind == CsTypeKind.String && v.IsConst && float.TryParse(v.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedFloat)
                    ? CValue.F(parsedFloat) : v;
                break;
            case "ToBoolean":
                if (v.Type.Kind == CsTypeKind.String)
                    result = CValue.B(v.IsConst && (v.Str.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Str == "1"));
                else result = ApplyBinary("!=", v, CValue.F(0), c);
                break;
            case "ToChar":
                if (v.Type.Kind == CsTypeKind.String && v.IsConst && v.Str.Length == 1) result = CValue.F(v.Str[0]);
                else { Error(c, "Convert.ToChar needs a constant one-character string"); result = CValue.F(0); }
                break;
            case "ToString":
                if (v.Type.Kind == CsTypeKind.String && v.IsConst) result = CValue.S(v.Str);
                else if (v.Type.Kind == CsTypeKind.Bool && v.IsConst) result = CValue.S(v.Const != 0 ? "True" : "False");
                else if (v.IsConst) result = CValue.S(FormatNumber(v.Const));
                else { Error(c, "Convert.ToString needs a compile-time value; use a Sign to display a runtime number"); result = CValue.S(""); }
                break;
            default:
                Error(c, $"Unknown Convert function '{name}'. Supported: ToInt32, ToInt64, ToUInt32, ToSingle, ToDouble, ToDecimal, ToBoolean, ToChar, ToString");
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    private CValue NumericStaticCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue result;
        switch (name)
        {
            case "Parse":
            {
                var v = args.Count > 0 ? args[0] : CValue.S("");
                if (v.Type.Kind == CsTypeKind.String && v.IsConst &&
                    float.TryParse(v.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    result = CValue.F(parsed);
                else
                {
                    Error(c, "Parse needs a constant number written as text, e.g. int.Parse(\"42\")");
                    result = CValue.F(0);
                }
                break;
            }
            case "ToString":
                result = CValue.S(args.Count > 0 ? FormatNumber(args[0].Const) : "0");
                break;
            default:
                Error(c, $"Unknown function '{name}'");
                result = CValue.F(0);
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    private CValue FindCall(CallExpr c)
    {
        if (c.Args.Count < 1) { Error(c, "Find needs an object name"); return CValue.F(0); }
        var nameVal = Eval(c.Args[0]);
        if (nameVal.Type.Kind != CsTypeKind.String)
        {
            Error(c, "Find needs a string literal name, e.g. GameObject.Find(\"Cube\")");
            return CValue.F(0);
        }
        string name = nameVal.Str;
        var node = _level.AllNodes().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                   ?? _level.AllNodes().FirstOrDefault(n => string.Equals(n.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        if (node == null)
        {
            Error(c, $"No object named '{name}' was found. Check the Hierarchy.");
            return CValue.F(0);
        }
        if (node.Kind is NodeKind.Start or NodeKind.Finish)
        {
            Error(c, $"GRAB does not allow Code Blocks to connect to {node.Kind} objects. Use a normal block as the anchor.");
            return CValue.F(0);
        }
        CsType type = CsType.GameObject;
        string? generic = c.GenericArgs.FirstOrDefault();
        if (generic != null) type = ResolveFindType(generic, node, c) ?? CsType.GameObject;
        return CValue.O(node, type);
    }

    private CsType? ResolveFindType(string name, LevelNode node, CsNode at)
    {
        var t = ResolveType(name, at.Line, at.Col);
        if (t == null) return CsType.GameObject;
        if (!IsObjectType(t)) { Error(at, $"'{name}' is not a scene object type"); return CsType.GameObject; }
        return CheckNodeType(node, t, at) ? t : CsType.GameObject;
    }

    private bool CheckNodeType(LevelNode node, CsType t, CsNode at)
    {
        bool ok = t.Kind switch
        {
            CsTypeKind.GameObject => true,
            CsTypeKind.Trigger => node.Kind == NodeKind.Trigger,
            CsTypeKind.Light => node.Kind == NodeKind.Light,
            CsTypeKind.Sign => node.Kind == NodeKind.Sign,
            CsTypeKind.PhysicsBlock => node.Kind is NodeKind.Static or NodeKind.Crumbling,
            _ => true
        };
        if (!ok) Error(at, $"'{node.DisplayName}' is a {node.Kind}, not a {t.Name}");
        return ok;
    }

    private CValue EvalAssignment(AssignmentExpr a)
    {
        var lv = ResolveLValue(a.Target);
        if (lv == null) { var v = Eval(a.Value); FreeValue(v); return CValue.F(0); }
        if (a.Op == "=")
        {
            var val = Eval(a.Value);
            // Write accepts both text (compile-time) and GRAB's numeric sign writes.
            if (lv is not WriteLValue && !Assignable(val.Type, lv.Type)) Error(a, $"Cannot assign {val.Type} to {lv.Type}");
            lv.Write(val);
            FreeValue(val);
            return lv.Read();
        }
        string op = a.Op[..^1];
        var left = lv.Read();
        var right = Eval(a.Value);
        var result = ApplyBinary(op, left, right, a);
        lv.Write(result);
        FreeValue(left); FreeValue(right);
        return result;
    }

    private CLValue? ResolveLValue(Expr e)
    {
        if (e is IndexExpr ix)
        {
            var array = ArrayVarOf(ix.Target);
            if (array != null) return new ArrayElementLValue(_e, array, Eval(ix.Index), false);
            var target = Eval(ix.Target);
            if (target.Collection != null) return new CollectionLValue(this, target.Collection, Eval(ix.Index));
            Error(ix, "Only fixed-size arrays, List<T> and Dictionary<K,V> can be indexed");
            return null;
        }
        if (e is NameExpr n)
        {
            var v = Lookup(n.Name);
            if (v == null) { Error(n, $"Unknown variable '{n.Name}'"); return null; }
            return new VarLValue(v, _e);
        }
        if (e is ThisExpr)
        {
            return null;
        }
        if (e is MemberExpr m)
        {
            if (m.Target is ThisExpr)
            {
                if (_thisInstance != null && _thisInstance.Fields.TryGetValue(m.Name, out var currentField)) return new VarLValue(currentField, _e);
                if (_fields.TryGetValue(m.Name, out var f)) return new VarLValue(f, _e);
                Error(m, $"Unknown field '{m.Name}'");
                return null;
            }
            if (m.Target is NameExpr typeName && _typeDecls.ContainsKey(typeName.Name) && Lookup(typeName.Name) == null)
            {
                var def = GetTypeDef(_typeDecls[typeName.Name], new List<string>(), m.Line, m.Col);
                if (def == null) return null;
                var staticVar = GetStaticVar(def, m.Name, m);
                return staticVar == null ? null : new VarLValue(staticVar, _e);
            }
            if (m.Target is not NameExpr && TryPlayerPath(m.Target, out var playerPath))
            {
                return PlayerPathLValue(playerPath + "." + m.Name);
            }
            if (m.Target is MemberExpr pm && pm.Target is NameExpr vn && Lookup(vn.Name) is CVar ov && ov.IsObject)
            {
                if (pm.Name == "Physics") return ObjectProperty(ov.ObjectNode, m, m.Name, forWrite: true);
                if (pm.Name == "Light" && m.Name == "Color") return ObjectProperty(ov.ObjectNode, m, "Color", forWrite: true);
            }
            if (m.Target is NameExpr tn)
            {
                if (tn.Name == "Player")
                {
                    var pl = PlayerMemberLValue(m);
                    return pl;
                }
                if (tn.Name == "Level" || tn.Name == "World" || tn.Name == "Scene")
                    return LevelMemberLValue(m);
                var v = Lookup(tn.Name);
                if (v != null)
                {
                    if (v.IsObject) return ObjectProperty(v.ObjectNode, m, m.Name, forWrite: true);
                    if (v.Instance != null)
                    {
                        if (v.Instance.Fields.TryGetValue(m.Name, out var fieldVar)) return new VarLValue(fieldVar, _e);
                        Error(m, $"'{v.Type.Name}' has no field '{m.Name}'");
                        return null;
                    }
                    if (v.Type.Kind is CsTypeKind.Vector3 or CsTypeKind.Color)
                    {
                        var parts = new VarLValue(v, _e);
                        return ComponentLValue(parts, m);
                    }
                    Error(m, $"'{tn.Name}' has no assignable member '{m.Name}'");
                    return null;
                }
            }
            var targetLv = ResolveLValue(m.Target);
            if (targetLv != null) return ComponentLValue(targetLv, m);
            var targetVal = Eval(m.Target);
            if (targetVal.Inst != null)
            {
                if (targetVal.Inst.Fields.TryGetValue(m.Name, out var instField)) return new VarLValue(instField, _e);
                Error(m, $"'{targetVal.Inst.Type.Name}' has no field '{m.Name}'");
                return null;
            }
            if (targetVal.Node != null) return ObjectProperty(targetVal.Node, m, m.Name, forWrite: true);
            Error(m, $"Cannot assign to '{m.Name}'");
            return null;
        }
        Error(e, "Expression is not assignable");
        return null;
    }

    private CLValue? ComponentLValue(CLValue parent, MemberExpr m)
    {
        int idx = m.Name switch { "X" or "R" => 0, "Y" or "G" => 1, "Z" or "B" => 2, "W" or "A" => 3, _ => -1 };
        if (idx < 0)
        {
            Error(m, $"Unknown component '{m.Name}'");
            return null;
        }
        if (parent is VectorSlotLValue vs && idx < vs.Parts.Length) return vs.Parts[idx];
        if (parent is ColorSlotLValue cs && idx < cs.Parts.Length) return cs.Parts[idx];
        Error(m, "Member is not a vector/color component");
        return null;
    }

    private sealed class CollectionLValue : CLValue
    {
        private readonly CSharpCompiler _c;
        private readonly CCollection _coll;
        private readonly CValue _key;
        public CollectionLValue(CSharpCompiler c, CCollection coll, CValue key)
        {
            _c = c; _coll = coll; _key = key; Type = coll.ElementType;
        }
        public override CValue Read() => _coll.IsDictionary
            ? _c.DictionaryElementRead(_coll, _key, new LiteralExpr())
            : _c.CollectionElementRead(_coll, _key, new LiteralExpr());
        public override void Write(CValue value)
        {
            if (_coll.IsDictionary) _c.DictionaryElementWrite(_coll, _key, value, new LiteralExpr());
            else _c.CollectionElementWrite(_coll, _key, value, new LiteralExpr());
        }
    }

    private sealed class ColorSlotLValue : CLValue
    {
        public CLValue[] Parts;
        public ColorSlotLValue(CLValue[] parts) { Parts = parts; Type = CsType.Color; }
        public override CValue Read() => new() { Type = CsType.Color, Parts = Parts.Select(p => p.Read()).ToArray() };
        public override void Write(CValue value)
        {
            if (value.Parts == null) return;
            for (int i = 0; i < Math.Min(Parts.Length, value.Parts.Length); i++) Parts[i].Write(value.Parts[i]);
        }
    }

    private CLValue? ObjectProperty(LevelNode? node, CsNode at, string name, bool forWrite = false)
    {
        if (node == null) { Error(at, "Object reference is null"); return null; }
        switch (name)
        {
            case "Position":
                if (node.Kind == NodeKind.Finish) { Error(at, "Finish points have no Position property"); return null; }
                return VectorProp(node, GasmPropertyKind.Position, GasmEmitter.PositionSpecs, new[] { "X", "Y", "Z" }, false);
            case "Rotation":
                return VectorProp(node, GasmPropertyKind.Rotation, GasmEmitter.RotationSpecs, new[] { "X", "Y", "Z" }, false);
            case "Scale":
                return VectorProp(node, GasmPropertyKind.Scale, GasmEmitter.ScaleSpecs, new[] { "X", "Y", "Z" }, true);
            case "Color":
                if (node.Kind is NodeKind.Light)
                    return LightColorProp(node);
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling))
                {
                    Error(at, "Color is only available on solid blocks and Light objects.");
                    return null;
                }
                return ColorProp(node, GasmPropertyKind.Color, GasmEmitter.ColorSpecs, forWrite);
            case "Active":
                if (node.Kind != NodeKind.Trigger) { Error(at, "Active is only available on Trigger objects"); return null; }
                return PropScalar(node, GasmPropertyKind.TriggerActive, GasmEmitter.TriggerSpecs, "Active", false);
            case "PlayerId":
                if (node.Kind != NodeKind.Trigger) { Error(at, "PlayerId is only available on Trigger objects"); return null; }
                return PropScalar(node, GasmPropertyKind.TriggerActive, GasmEmitter.TriggerSpecs, "PlayerId", true);
            case "ForceActive":
                if (node.Kind != NodeKind.Trigger) { Error(at, "ForceActive is only available on Trigger objects"); return null; }
                return PropScalar(node, GasmPropertyKind.TriggerActive, GasmEmitter.TriggerSpecs, "Force", false);
            case "Part":
                if (node.Kind != NodeKind.Trigger) { Error(at, "Part is only available on Trigger objects"); return null; }
                return PropScalar(node, GasmPropertyKind.TriggerActive, GasmEmitter.TriggerSpecs, "Part", true);
            case "Enabled":
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) { Error(at, "Physics.Enabled is only available on solid blocks"); return null; }
                return PropScalar(node, GasmPropertyKind.Physics, GasmEmitter.PhysicsSpecs, "Enabled", false);
            case "State":
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) { Error(at, "Physics.State is only available on solid blocks"); return null; }
                return PropScalar(node, GasmPropertyKind.Physics, GasmEmitter.PhysicsSpecs, "State", true);
            // Write/Text works on every object. Signs display it in game; on other
            // objects the text is stored in the level and shown by the editor.
            case "Write":
            {
                CLValue? numeric = null;
                if (node.Kind == NodeKind.Sign)
                    numeric = PropScalar(node, GasmPropertyKind.Sign, GasmEmitter.SignSpecs, "Write", false);
                return new WriteLValue(_e, node, numeric);
            }
            case "Text":
            case "Label":
                return new WriteLValue(_e, node, null);
            case "Select":
                if (node.Kind != NodeKind.Sign) { Error(at, "Select is only available on Sign objects"); return null; }
                return PropScalar(node, GasmPropertyKind.Sign, GasmEmitter.SignSpecs, "Select", false);
            case "Control":
                if (node.Kind != NodeKind.Sign) { Error(at, "Control is only available on Sign objects"); return null; }
                return PropScalar(node, GasmPropertyKind.Sign, GasmEmitter.SignSpecs, "Control", false);
            case "Number":
                if (node.Kind != NodeKind.Sign) { Error(at, "Number is only available on Sign objects"); return null; }
                return PropScalar(node, GasmPropertyKind.Sign, GasmEmitter.SignSpecs, "Number", false);
            case "Brightness":
                if (node.Kind != NodeKind.Light) { Error(at, "Brightness is only available on Light objects"); return null; }
                return PropScalar(node, GasmPropertyKind.Light, GasmEmitter.LightSpecs, "Brightness", false);
            case "Range":
                if (node.Kind != NodeKind.Light) { Error(at, "Range is only available on Light objects"); return null; }
                return PropScalar(node, GasmPropertyKind.Light, GasmEmitter.LightSpecs, "Range", false);
            default:
                Error(at, $"'{name}' is not a programmable property of {node.Kind}");
                return null;
        }
    }

    /// <summary>Convenience methods on a scene object, in the spirit of Unity's Transform API.</summary>
    private CValue ObjectCall(LevelNode node, string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        var result = new CValue { Type = CsType.Void };
        switch (name)
        {
            case "SetPosition":
            case "SetRotation":
            {
                if (args.Count != 1 || args[0].Parts == null)
                {
                    Error(c, $"{name} needs one Vector3 argument");
                    break;
                }
                var lv = ObjectProperty(node, c, name[3..], true);
                lv?.Write(args[0]);
                break;
            }
            case "Translate":
            case "Rotate":
            {
                if (args.Count != 1 || args[0].Parts == null)
                {
                    Error(c, $"{name} needs one Vector3 argument");
                    break;
                }
                string propName = name == "Translate" ? "Position" : "Rotation";
                var target = ObjectProperty(node, c, propName, true);
                if (target == null) break;
                var current = target.Read();
                if (current.Parts == null) { Error(c, $"This object has no {propName}"); break; }
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                    parts[i] = ScalarOp("+", current.Parts[i], args[0].Parts[i], c);
                target.Write(new CValue { Type = CsType.Vector3, Parts = parts });
                foreach (var p in parts) FreeValue(p);
                break;
            }
            case "SetColor":
                if (args.Count != 1 || args[0].Parts == null)
                {
                    Error(c, "SetColor needs one Color argument");
                    break;
                }
                ObjectProperty(node, c, "Color", true)?.Write(args[0]);
                break;
            default:
                Error(c, $"Unknown object method '{name}'. Use SetPosition, SetRotation, Translate, Rotate or SetColor.");
                break;
        }
        foreach (var a in args) FreeValue(a);
        return result;
    }

    private CLValue? ColorProp(LevelNode node, GasmPropertyKind kind, PropSpec[] specs, bool forWrite = false)
    {
        var c = _e.NodeConnection(node);
        var prop = _e.EnsureProperty(c, kind, specs);
        var parts = new CLValue[3];
        for (int i = 0; i < 3; i++)
        {
            int reg = _e.GetRegister(prop, i, RegKind.InOut, c.Name + specs[i].Suffix);
            parts[i] = new ScaledLValue(_e, new ScalarSlotLValue(_e, reg, GasmOperandKind.InOut, CsType.Float), 1f / 255f);
        }
        return new ColorSlotLValue(parts);
    }

    private CLValue? LightColorProp(LevelNode node)
    {
        var c = _e.NodeConnection(node);
        var prop = _e.EnsureProperty(c, GasmPropertyKind.Light, GasmEmitter.LightSpecs);
        var parts = new CLValue[3];
        for (int i = 0; i < 3; i++)
        {
            int reg = _e.GetRegister(prop, i, RegKind.InOut, c.Name + GasmEmitter.LightSpecs[i].Suffix);
            parts[i] = new ScaledLValue(_e, new ScalarSlotLValue(_e, reg, GasmOperandKind.InOut, CsType.Float), 1f / 255f);
        }
        return new ColorSlotLValue(parts);
    }

    private CLValue? VectorProp(LevelNode node, GasmPropertyKind kind, PropSpec[] specs, string[] keys, bool readOnly)
    {
        var c = _e.NodeConnection(node);
        var prop = _e.EnsureProperty(c, kind, specs);
        var parts = new CLValue[3];
        for (int i = 0; i < 3; i++)
        {
            int reg = _e.GetRegister(prop, i, specs[i].Kind ?? RegKind.InOut, c.Name + specs[i].Suffix);
            parts[i] = new ScalarSlotLValue(_e, reg, specs[i].Kind == RegKind.Input ? GasmOperandKind.Input : GasmOperandKind.InOut, CsType.Float, readOnly || specs[i].Kind == RegKind.Input);
        }
        return new VectorSlotLValue(parts, CsType.Vector3);
    }

    private CLValue? PropScalar(LevelNode node, GasmPropertyKind kind, PropSpec[] specs, string key, bool readOnly)
    {
        int idx = Array.FindIndex(specs, s => s.Key == key);
        if (idx < 0) return null;
        var c = _e.NodeConnection(node);
        var prop = _e.EnsureProperty(c, kind, specs);
        var spec = specs[idx];
        if (spec.Kind == null) { ErrorAt(1, 1, "This property component is not programmable"); return null; }
        int reg = _e.GetRegister(prop, idx, spec.Kind.Value, c.Name + spec.Suffix);
        var ok = spec.Kind switch { RegKind.Input => GasmOperandKind.Input, RegKind.Output => GasmOperandKind.Output, _ => GasmOperandKind.InOut };
        return new ScalarSlotLValue(_e, reg, ok, CsType.Float, readOnly || spec.Kind == RegKind.Input);
    }

    private CValue PlayerMember(MemberExpr m)
    {
        var lv = PlayerMemberLValue(m);
        return lv?.Read() ?? CValue.F(0);
    }

    private CLValue? PlayerMemberLValue(MemberExpr m)
    {
        var c = _e.PlayerConnection();
        var prop = c.Properties.First(p => p.Kind == GasmPropertyKind.Player);
        switch (m.Name)
        {
            case "Pos": return PlayerVector(prop, new[] { "Pos.X", "Pos.Y", "Pos.Z" }, true);
            case "Rot": return PlayerVector(prop, new[] { "Rot.X", "Rot.Y", "Rot.Z" }, true);
            case "Vel": return PlayerVector(prop, new[] { "Vel.X", "Vel.Y", "Vel.Z" }, false);
            case "TelPos": return PlayerVector(prop, new[] { "Tel.Pos.X", "Tel.Pos.Y", "Tel.Pos.Z" }, false);
            case "Tel":
                Error(m, "Use Player.Tel.Pos, Player.Tel.RotY or Player.Tel.Apply");
                return null;
            case "Col":
                Error(m, "Use Player.Col.R, Player.Col.G, Player.Col.B or Player.Col.Select");
                return null;
        }
        int idx = Array.FindIndex(GasmEmitter.PlayerSpecs, s => s.Key == m.Name);
        if (idx < 0)
        {
            Error(m, $"Unknown Player field '{m.Name}'");
            return null;
        }
        var spec = GasmEmitter.PlayerSpecs[idx];
        if (spec.Kind == null) return null;
        int reg = _e.GetRegister(prop, idx, spec.Kind.Value, "Player" + spec.Suffix);
        var ok = spec.Kind switch { RegKind.Input => GasmOperandKind.Input, RegKind.Output => GasmOperandKind.Output, _ => GasmOperandKind.InOut };
        return new ScalarSlotLValue(_e, reg, ok, CsType.Float, spec.Kind == RegKind.Input);
    }

    private CLValue? PlayerPathLValue(string path)
    {
        var c = _e.PlayerConnection();
        var prop = c.Properties.First(p => p.Kind == GasmPropertyKind.Player);
        string[] parts = path.Split('.');
        if (path is "Pos" or "Rot" or "Vel") return PlayerMemberLValue(new MemberExpr { Name = path, Target = new NameExpr { Name = "Player" } });
        if (path is "Tel.Pos" or "Tel.Rot")
        {
            if (parts[1] == "Rot") return PlayerVector(prop, new[] { "Tel.Rot.Y", "Tel.Rot.Y", "Tel.Rot.Y" }, false);
            return PlayerVector(prop, new[] { "Tel.Pos.X", "Tel.Pos.Y", "Tel.Pos.Z" }, false);
        }
        string key = path;
        if (parts.Length == 2 && parts[0] == "Col" && parts[1] is "R" or "G" or "B") key = "Col." + parts[1];
        if (parts.Length == 2 && parts[0] == "Col" && parts[1] == "Select") key = "Col.Select";
        if (parts.Length == 2 && parts[0] == "Tel" && parts[1] is "Pos" or "Rot") key = parts[1] == "Pos" ? "Tel.Pos" : "Tel.Rot";
        if (parts.Length == 2 && parts[0] == "Tel" && parts[1] == "Apply") key = "Tel.Apply";
        if (parts.Length == 2 && parts[0] == "Pos" && parts[1] is "X" or "Y" or "Z") key = "Pos." + parts[1];
        if (parts.Length == 2 && parts[0] == "Rot" && parts[1] is "X" or "Y" or "Z") key = "Rot." + parts[1];
        if (parts.Length == 2 && parts[0] == "Vel" && parts[1] is "X" or "Y" or "Z") key = "Vel." + parts[1];
        if (parts.Length == 3 && parts[0] == "Tel" && parts[1] == "Pos" && parts[2] is "X" or "Y" or "Z") key = "Tel.Pos." + parts[2];
        if (parts.Length == 3 && parts[0] == "Tel" && parts[1] == "Rot" && parts[2] == "Y") key = "Tel.Rot.Y";
        int idx = Array.FindIndex(GasmEmitter.PlayerSpecs, s => s.Key == key);
        if (idx < 0)
        {
            ErrorAt(1, 1, $"Unknown Player field '{path}'");
            return null;
        }
        var spec = GasmEmitter.PlayerSpecs[idx];
        if (spec.Kind == null) return null;
        int reg = _e.GetRegister(prop, idx, spec.Kind.Value, "Player" + spec.Suffix);
        var ok = spec.Kind switch { RegKind.Input => GasmOperandKind.Input, RegKind.Output => GasmOperandKind.Output, _ => GasmOperandKind.InOut };
        return new ScalarSlotLValue(_e, reg, ok, CsType.Float, spec.Kind == RegKind.Input);
    }

    private CLValue PlayerVector(GasmProperty prop, string[] keys, bool readOnly)
    {
        var parts = new CLValue[3];
        for (int i = 0; i < 3; i++)
        {
            int idx = Array.FindIndex(GasmEmitter.PlayerSpecs, s => s.Key == keys[i]);
            var spec = GasmEmitter.PlayerSpecs[idx];
            int reg = _e.GetRegister(prop, idx, spec.Kind ?? RegKind.InOut, "Player" + spec.Suffix);
            var ok = spec.Kind switch { RegKind.Input => GasmOperandKind.Input, RegKind.Output => GasmOperandKind.Output, _ => GasmOperandKind.InOut };
            parts[i] = new ScalarSlotLValue(_e, reg, ok, CsType.Float, readOnly || spec.Kind == RegKind.Input);
        }
        return new VectorSlotLValue(parts, CsType.Vector3);
    }

    private CValue LevelMember(MemberExpr m)
    {
        var lv = LevelMemberLValue(m);
        return lv?.Read() ?? CValue.F(0);
    }

    private CLValue? LevelMemberLValue(MemberExpr m)
    {
        if (m.Name == "Var" || m.Name == "Variables") { Error(m, "Use Level.Var1 through Level.Var5"); return null; }
        int idx = Array.FindIndex(GasmEmitter.LobbySpecs, s => s.Key == m.Name);
        if (idx < 0) { Error(m, $"Unknown Level field '{m.Name}'"); return null; }
        var c = _e.LobbyConnection();
        var prop = c.Properties.First(p => p.Kind == GasmPropertyKind.LobbyVariables);
        int reg = _e.GetRegister(prop, idx, RegKind.InOut, "Lobby" + GasmEmitter.LobbySpecs[idx].Suffix);
        return new ScalarSlotLValue(_e, reg, GasmOperandKind.InOut, CsType.Float);
    }

    private CValue EvalBinary(BinaryExpr b)
    {
        var l = Eval(b.Left);
        var r = Eval(b.Right);
        var v = ApplyBinary(b.Op, l, r, b);
        FreeValue(l);
        FreeValue(r);
        return v;
    }

    private CValue ApplyBinary(string op, CValue l, CValue r, CsNode at)
    {
        if (op is "+" or "-" or "*" or "/")
        {
            if (l.Type.Kind == CsTypeKind.Vector3 && r.Type.Kind == CsTypeKind.Vector3 && op is "+" or "-")
            {
                var p = new CValue[3];
                for (int i = 0; i < 3; i++) p[i] = ScalarOp(op, l.Parts![i], r.Parts![i], at);
                return new CValue { Type = CsType.Vector3, Parts = p };
            }
            if (l.Type.Kind == CsTypeKind.Vector3 && r.IsNumeric && op is "*" or "/")
            {
                var p = new CValue[3];
                for (int i = 0; i < 3; i++) p[i] = ScalarOp(op, l.Parts![i], r, at);
                return new CValue { Type = CsType.Vector3, Parts = p };
            }
            if (l.IsNumeric && r.Type.Kind == CsTypeKind.Vector3 && op == "*")
            {
                var p = new CValue[3];
                for (int i = 0; i < 3; i++) p[i] = ScalarOp(op, l, r.Parts![i], at);
                return new CValue { Type = CsType.Vector3, Parts = p };
            }
            if (l.Type.Kind == CsTypeKind.Color && r.IsNumeric && op is "*" or "/")
            {
                var p = new CValue[4];
                for (int i = 0; i < 4; i++) p[i] = ScalarOp(op, l.Parts![i], r, at);
                return new CValue { Type = CsType.Color, Parts = p };
            }
            if (op == "+" && (l.Type.Kind == CsTypeKind.String || r.Type.Kind == CsTypeKind.String))
                return ConcatText(l, r);
        }
        if (op is "==" or "!=" && l.Type.Kind == CsTypeKind.String && r.Type.Kind == CsTypeKind.String)
        {
            if (!l.IsConst || !r.IsConst)
            {
                Error(at, "String comparison needs compile-time text (GASM has no string registers)");
                return CValue.B(false);
            }
            return CValue.B(op == "==" ? l.Str == r.Str : l.Str != r.Str);
        }
        if (op is "==" or "!=" or "<" or ">" or "<=" or ">=")
        {
            if (!l.IsNumeric || !r.IsNumeric) { Error(at, $"Operator '{op}' needs numeric operands"); return CValue.B(false); }
            if (l.IsConst && r.IsConst)
            {
                bool res = op switch
                {
                    "==" => Math.Abs(l.Const - r.Const) < 1e-6f, "!=" => Math.Abs(l.Const - r.Const) >= 1e-6f,
                    "<" => l.Const < r.Const, ">" => l.Const > r.Const, "<=" => l.Const <= r.Const, _ => l.Const >= r.Const
                };
                return CValue.B(res);
            }
            var (a, bb) = (Materialize(l), Materialize(r));
            int t = _e.AllocTemp();
            var lop = op switch
            {
                "==" => GasmOp.Equal, "!=" => GasmOp.Equal, "<" => GasmOp.Less, ">" => GasmOp.Greater,
                "<=" => GasmOp.Greater, _ => GasmOp.Less
            };
            _e.Emit(lop, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(bb));
            if (op is "!=" or "<=" or ">=")
            {
                int n = _e.AllocTemp();
                _e.Emit(GasmOp.Not, GasmEmitter.Work(n), GasmEmitter.Work(t));
                _e.FreeTemp(t);
                t = n;
            }
            _e.FreeTemp(a); _e.FreeTemp(bb);
            return new CValue { Type = CsType.Bool, Reg = t, IsTemp = true };
        }
        if (op is "&&" or "&")
        {
            var a = Materialize(l); var bb = Materialize(r);
            int t = _e.AllocTemp();
            _e.Emit(GasmOp.And, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(bb));
            _e.FreeTemp(a); _e.FreeTemp(bb);
            return new CValue { Type = CsType.Bool, Reg = t, IsTemp = true };
        }
        if (op is "||" or "|")
        {
            var a = Materialize(l); var bb = Materialize(r);
            int t = _e.AllocTemp();
            _e.Emit(GasmOp.Or, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(bb));
            _e.FreeTemp(a); _e.FreeTemp(bb);
            return new CValue { Type = CsType.Bool, Reg = t, IsTemp = true };
        }
        if (op is "%")
            return ScalarOp(op, l, r, at);
        if (l.IsNumeric && r.IsNumeric)
            return ScalarOp(op, l, r, at);
        Error(at, $"Unsupported operator '{op}'");
        return CValue.F(0);
    }

    private CValue ScalarOp(string op, CValue l, CValue r, CsNode at)
    {
        if (l.IsConst && r.IsConst)
        {
            float res = op switch
            {
                "+" => l.Const + r.Const, "-" => l.Const - r.Const, "*" => l.Const * r.Const,
                "/" => Math.Abs(r.Const) < 1e-9f ? 0 : l.Const / r.Const,
                "%" => Math.Abs(r.Const) < 1e-9f ? 0 : l.Const % r.Const, _ => 0
            };
            return CValue.F(res);
        }
        var a = Materialize(l);
        var b = Materialize(r);
        int t = _e.AllocTemp();
        var gop = op switch
        {
            "+" => GasmOp.Add, "-" => GasmOp.Sub, "*" => GasmOp.Mul, "/" => GasmOp.Div, _ => GasmOp.Mod
        };
        if ((op is "/" or "%") && r.IsConst && Math.Abs(r.Const) < 1e-9f)
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = "Division by zero will stop the GASM program", Line = at.Line, Column = at.Col, File = "Script.cs" });
        _e.Emit(gop, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(b));
        _e.FreeTemp(a); _e.FreeTemp(b);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue EvalUnary(UnaryExpr u)
    {
        if (u.Op is "++" or "--")
        {
            var lv = ResolveLValue(u.Operand);
            if (lv == null) return CValue.F(0);
            var old = lv.Read();
            var one = CValue.F(1);
            var nv = ApplyBinary(u.Op == "++" ? "+" : "-", old, one, u);
            lv.Write(nv);
            if (u.Prefix) { FreeValue(old); return nv; }
            var oldCopy = new CValue { Type = old.Type, Reg = Materialize(old), IsTemp = true };
            FreeValue(old);
            return oldCopy;
        }
        var v = Eval(u.Operand);
        if (u.Op == "+") return v;
        if (u.Op == "-")
        {
            CValue res;
            if (v.Type.Kind == CsTypeKind.Vector3)
                res = new CValue { Type = CsType.Vector3, Parts = v.Parts!.Select(p => ScalarOp("-", CValue.F(0), p, u)).ToArray() };
            else res = ScalarOp("-", CValue.F(0), v, u);
            FreeValue(v);
            return res;
        }
        if (u.Op == "!")
        {
            var a = Materialize(v);
            int t = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(t), GasmEmitter.Work(a));
            _e.FreeTemp(a);
            FreeValue(v);
            return new CValue { Type = CsType.Bool, Reg = t, IsTemp = true };
        }
        Error(u, $"Unsupported unary '{u.Op}'");
        return CValue.F(0);
    }

    private CValue EvalConditional(ConditionalExpr c)
    {
        var cond = Materialize(Eval(c.Cond));
        var tv = Eval(c.True);
        var fv = Eval(c.False);
        var type = tv.Type.Kind == CsTypeKind.Error ? fv.Type : tv.Type;
        int elseL = _e.NewLabel("cond_else");
        int endL = _e.NewLabel("cond_end");
        int n = _e.AllocTemp();
        _e.Emit(GasmOp.Not, GasmEmitter.Work(n), GasmEmitter.Work(cond));
        _e.Emit(GasmOp.If, GasmEmitter.Work(n), GasmOperand.Label(elseL));
        _e.FreeTemp(n); _e.FreeTemp(cond);
        int count = type.Kind == CsTypeKind.Vector3 ? 3 : type.Kind == CsTypeKind.Color ? 4 : 1;
        var regs = new int[count];
        for (int i = 0; i < count; i++) regs[i] = _e.AllocTemp();
        if (count == 1) _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[0]), tv.Operand());
        else for (int i = 0; i < count; i++) _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), tv.Parts![Math.Min(i, tv.Parts.Length - 1)].Operand());
        _e.Goto(endL);
        _e.Mark(elseL);
        if (count == 1) _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[0]), fv.Operand());
        else for (int i = 0; i < count; i++) _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), fv.Parts![Math.Min(i, fv.Parts.Length - 1)].Operand());
        _e.Mark(endL);
        FreeValue(tv); FreeValue(fv);
        if (count == 1) return new CValue { Type = type, Reg = regs[0], IsTemp = true };
        return new CValue { Type = type, Parts = regs.Select(r => (CValue)new CValue { Type = CsType.Float, Reg = r, IsTemp = true }).ToArray() };
    }

    private CValue EvalCast(CastExpr c)
    {
        var target = ResolveType(c.TypeName, c.Line, c.Col);
        var v = Eval(c.Operand);
        if (target == null) return CValue.F(0);
        if (target.Kind is CsTypeKind.GameObject or CsTypeKind.Trigger or CsTypeKind.Light or CsTypeKind.Sign or CsTypeKind.PhysicsBlock)
        {
            if (v.Node != null)
            {
                CheckNodeType(v.Node, target, c);
                return CValue.O(v.Node, target);
            }
            Error(c, "Only scene object references can be cast");
            return CValue.F(0);
        }
        if (target.Kind == CsTypeKind.Float) return v.IsNumeric ? v : CValue.F(0);
        if (target.Kind == CsTypeKind.Bool)
        {
            if (v.IsConst) return CValue.B(v.Const != 0);
            var a = Materialize(v);
            int t = _e.AllocTemp();
            _e.Emit(GasmOp.Not, GasmEmitter.Work(t), GasmEmitter.Work(a));
            _e.Emit(GasmOp.Not, GasmEmitter.Work(t), GasmEmitter.Work(t));
            _e.FreeTemp(a);
            return new CValue { Type = CsType.Bool, Reg = t, IsTemp = true };
        }
        Error(c, $"Cannot cast to {target}");
        return CValue.F(0);
    }

    private CValue EvalNew(NewExpr n)
    {
        string type = n.TypeName;
        if (n.IsArray || n.Initializers.Count > 0)
        {
            var element = ResolveType(type, n.Line, n.Col);
            if (element == null || element.Kind is not (CsTypeKind.Float or CsTypeKind.Bool))
            {
                Error(n, $"Arrays of '{type}' are not supported. GRAB ENGINE supports float[], int[] and bool[].");
                return CValue.F(0);
            }
            int size;
            if (n.Initializers.Count > 0) size = n.Initializers.Count;
            else if (n.Size != null)
            {
                var sizeValue = Eval(n.Size);
                if (!sizeValue.IsConst)
                {
                    Error(n.Size, "Array size must be a compile-time constant (GASM has no heap)");
                    FreeValue(sizeValue);
                    return CValue.F(0);
                }
                size = (int)MathF.Round(sizeValue.Const);
                FreeValue(sizeValue);
            }
            else
            {
                Error(n, "Array creation needs a size, e.g. new float[4]");
                return CValue.F(0);
            }
            if (size <= 0 || size > 32)
            {
                Error(n, $"Array size must be 1..32 (this program asked for {size}); GASM has no heap and only 150 instructions.");
                return CValue.F(0);
            }

            int id = _arrayCounter++;
            var regs = new int[size];
            for (int i = 0; i < size; i++) regs[i] = _e.AllocWorking("arr" + id + "_" + i);
            if (n.Initializers.Count > 0)
            {
                var values = n.Initializers.Select(Eval).ToList();
                for (int i = 0; i < size; i++)
                    _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), values[Math.Min(i, values.Count - 1)].Operand());
                foreach (var v in values) FreeValue(v);
            }
            else
            {
                for (int i = 0; i < size; i++) _e.Emit(GasmOp.Set, GasmEmitter.Work(regs[i]), GasmEmitter.Const(0));
            }
            return CValue.A(element, regs);
        }
        if (type is "Vector3" or "Grab.Vector3")
        {
            var args = n.Args.Select(Eval).ToList();
            if (args.Count == 0) return CValue.V(CValue.F(0), CValue.F(0), CValue.F(0));
            if (args.Count == 1) return CValue.V(args[0], args[0], args[0]);
            if (args.Count != 3) { Error(n, "Vector3 needs 3 components"); return CValue.V(CValue.F(0), CValue.F(0), CValue.F(0)); }
            return CValue.V(args[0], args[1], args[2]);
        }
        if (type is "Color" or "Grab.Color")
        {
            var args = n.Args.Select(Eval).ToList();
            if (args.Count == 3) return CValue.C(args[0], args[1], args[2], CValue.F(1));
            if (args.Count != 4) { Error(n, "Color needs 3 or 4 components"); return CValue.C(CValue.F(0), CValue.F(0), CValue.F(0), CValue.F(1)); }
            return CValue.C(args[0], args[1], args[2], args[3]);
        }
        var created = ResolveType(n.TypeName, n.Line, n.Col);
        if (created == null || created.Kind == CsTypeKind.Error) return CValue.F(0);
        if (IsSceneObjectTypeName(n.TypeName))
            return EvalNewSceneObject(n);
        if (created.Kind is CsTypeKind.List or CsTypeKind.Dictionary)
        {
            int capacity = created.Capacity <= 0 ? 4 : created.Capacity;
            if (n.Args.Count > 0)
            {
                var size = Eval(n.Args[0]);
                if (!size.IsConst) { Error(n, "Collection capacity must be a compile-time constant"); FreeValue(size); return CValue.F(0); }
                capacity = (int)MathF.Round(size.Const);
                FreeValue(size);
            }
            return new CValue { Type = created, Collection = AllocateCollection(created, capacity, n) };
        }
        if (created.IsUserType && created.Definition is TypeDef def)
        {
            var inst = AllocateInstance(def);
            var args = n.Args.Select(Eval).ToList();
            var ctor = def.Constructors.FirstOrDefault(x => x.Decl.Params.Count == args.Count);
            RunConstructor(inst, ctor, args, n);
            foreach (var a in args) FreeValue(a);
            foreach (var (name, valueExpr) in n.MemberInit)
            {
                if (!inst.Fields.TryGetValue(name, out var field))
                {
                    Error(n, $"'{created.Name}' has no field '{name}'");
                    continue;
                }
                var val = Eval(valueExpr);
                new VarLValue(field, _e).Write(val);
                FreeValue(val);
            }
            return new CValue { Type = created, Inst = inst };
        }
        Error(n, $"'{type}' cannot be constructed. Use GameObject.Find(\"name\") for scene objects.");
        return CValue.F(0);
    }

    private static string NormalizeTypeName(string name)
    {
        string n = (name ?? "").Trim();
        if (n.StartsWith("global::", StringComparison.Ordinal)) n = n[8..];
        if (n.StartsWith("Grab.", StringComparison.Ordinal)) n = n[5..];
        int lt = n.IndexOf('<');
        if (lt >= 0) n = n[..lt];
        return n.Trim();
    }

    private static bool IsSceneObjectTypeName(string name)
    {
        string n = NormalizeTypeName(name);
        return n is "Sign" or "Light" or "Trigger" or "PhysicsBlock" or "Block"
            or "Cube" or "Sphere" or "Cylinder" or "Pyramid" or "Prism" or "Cone"
            or "PyramidSquare" or "CrumblingBlock" or "Crumbling" or "ParticleEmitter"
            or "Particles" or "SoundEmitter" or "Sound" or "GravityBlock" or "LobbyTerminal"
            or "StartPoint" or "FinishPoint";
    }

    private static bool IsShapeObjectType(string name)
    {
        string n = NormalizeTypeName(name);
        return n is "Cube" or "Sphere" or "Cylinder" or "Pyramid" or "Prism" or "Cone" or "PyramidSquare";
    }

    private static bool TryGetSceneKind(string name, out NodeKind kind, out int shape, out int material)
    {
        string n = NormalizeTypeName(name);
        kind = NodeKind.Static;
        shape = ShapeInfo.Cube;
        material = 8;
        switch (n)
        {
            case "Sign": kind = NodeKind.Sign; shape = ShapeInfo.Sign; return true;
            case "Light": kind = NodeKind.Light; shape = ShapeInfo.Light; return true;
            case "Trigger": kind = NodeKind.Trigger; shape = ShapeInfo.Cube; return true;
            case "ParticleEmitter": case "Particles": kind = NodeKind.ParticleEmitter; shape = ShapeInfo.ParticleEmitter; return true;
            case "Sound": case "SoundEmitter": kind = NodeKind.Sound; shape = ShapeInfo.Sound; return true;
            case "GravityBlock": kind = NodeKind.Gravity; shape = ShapeInfo.Gravity; return true;
            case "LobbyTerminal": kind = NodeKind.LobbyTerminal; shape = ShapeInfo.LobbyTerminal; return true;
            case "StartPoint": kind = NodeKind.Start; shape = ShapeInfo.Start; return true;
            case "FinishPoint": kind = NodeKind.Finish; shape = ShapeInfo.Finish; return true;
            case "CrumblingBlock": case "Crumbling": kind = NodeKind.Crumbling; shape = ShapeInfo.Cube; material = 7; return true;
            case "PhysicsBlock": case "Block": return true;
            case "Cube": shape = ShapeInfo.Cube; return true;
            case "Sphere": shape = ShapeInfo.Sphere; return true;
            case "Cylinder": shape = ShapeInfo.Cylinder; return true;
            case "Pyramid": shape = ShapeInfo.Pyramid; return true;
            case "Prism": shape = ShapeInfo.Prism; return true;
            case "Cone": shape = ShapeInfo.Cone; return true;
            case "PyramidSquare": shape = ShapeInfo.PyramidSquare; return true;
            default: return false;
        }
    }

    private static CsType SceneObjectTypeFor(NodeKind kind) => kind switch
    {
        NodeKind.Sign => new CsType(CsTypeKind.Sign, "Sign"),
        NodeKind.Light => new CsType(CsTypeKind.Light, "Light"),
        NodeKind.Trigger => new CsType(CsTypeKind.Trigger, "Trigger"),
        NodeKind.Static or NodeKind.Crumbling => new CsType(CsTypeKind.PhysicsBlock, "PhysicsBlock"),
        _ => CsType.GameObject
    };

    private static string SceneObjectKey(string typeName, CsNode at) =>
        "new:" + NormalizeTypeName(typeName) + "@" + at.Line + ":" + at.Col;

    private LevelNode? FindScriptObject(string key) =>
        _level.AllNodes().FirstOrDefault(n => n.ScriptCreated &&
            n.ScriptSourceBlock == _codeBlock?.EditorId && n.ScriptSourceKey == key);

    private Vec3 DefaultScenePosition()
    {
        var p = _codeBlock?.WorldPosition ?? Vec3.Zero;
        int i = _sceneObjectSerial++;
        return new Vec3(p.X + 2f + (i % 4) * 1.25f, p.Y + 0.5f + (i / 4) * 1.25f, p.Z);
    }

    private void AddScriptObject(LevelNode node, bool ensureNames = true)
    {
        _level.Nodes.Add(node);
        node.RebuildParents(null);
        _level.IsDirty = true;
        if (_objectIds != null)
            _objectIds[node] = (ulong)_level.AllNodes().Count();
        if (ensureNames) _level.EnsureNames();
    }

    private void ResetSceneDefaults(LevelNode node, string typeName, bool isNew)
    {
        if (!TryGetSceneKind(typeName, out var kind, out int shape, out int material)) return;
        node.Kind = kind;
        node.Shape = shape;
        node.Material = material;
        if (isNew)
        {
            node.Position = DefaultScenePosition();
            node.Name = "";
        }
        node.Rotation = Quat.Identity;
        node.Scale = kind switch
        {
            NodeKind.Trigger => new Vec3(2, 2, 2),
            NodeKind.Gravity => new Vec3(5, 5, 5),
            _ => Vec3.One
        };
        node.Color1 = new Col(0.72f, 0.72f, 0.72f, 1);
        node.Color2 = new Col(0.15f, 0.15f, 0.15f, 1);
        node.GradientDirection = new Vec3(0, 1, 0);
        node.SpecularBrightness = 3f;
        node.PhysicsObject = false;
        node.LocalPhysicsObject = false;
        node.Mass = 1f;
        node.IsNeon = false;
        node.IsTransparent = false;
        node.IsGradient = false;
        node.IsAdditive = false;
        node.IsGrabbable = false;
        node.IsGrapplable = false;
        node.IsPassable = false;
        node.StableTime = 1f;
        node.RespawnTime = 3f;
        node.IsLocal = false;
        node.Radius = kind == NodeKind.Finish ? 1f : 0.5f;
        node.IsHidden = false;
        node.Text = kind == NodeKind.Sign ? "Text" : "";
        node.EditorLabel = "";
        node.SignScale = 1f;
        node.SignColor = Col.White;
        node.SignWeight = 0;
        node.HideModel = false;
        node.SignNeon = false;
        node.GravityMode = 0;
        node.GravityDirection = new Vec3(0, -1, 0);
        node.HideParticles = false;
        node.ParticlesPerSecond = 20;
        node.LifeSpanMin = 1f;
        node.LifeSpanMax = 2f;
        node.StartColor = Col.White;
        node.EndColor = Col.Clear;
        node.StartSizeMin = 0.2f;
        node.StartSizeMax = 0.3f;
        node.EndSizeMin = 0f;
        node.EndSizeMax = 0f;
        node.Velocity = new Vec3(0, 1, 0);
        node.VelocityMin = Vec3.Zero;
        node.VelocityMax = Vec3.Zero;
        node.AccelerationMin = Vec3.Zero;
        node.AccelerationMax = Vec3.Zero;
        node.SoundName = "sound";
        node.SoundRepeat = false;
        node.SoundVolume = 1f;
        node.SoundStartActive = true;
        node.SoundMaxRange = 1000f;
        node.SoundWaveType = 0f;
        node.SoundFrequency = 440f;
        node.SoundAttack = 0.01f;
        node.SoundSustain = 0.3f;
        node.SoundRelease = 0.3f;
        node.LightType = 0;
        node.LightColor = Col.White;
        node.LightIntensity = 2.3f;
        node.LightRange = 4.3f;
        node.LightAngle = 45f;
        node.DistanceFalloffShape = 3f;
        node.ConeFalloffShape = 0f;
        node.IsSharedTrigger = false;
        node.StartActive = true;
    }

    private CValue EvalNewSceneObject(NewExpr n)
    {
        string type = NormalizeTypeName(n.TypeName);
        string key = SceneObjectKey(type, n);
        var args = n.Args.Select(Eval).ToList();
        if (!args.All(IsCompileTimeValue))
        {
            Error(n, "Scene objects are created while compiling, so constructor arguments must be compile-time constants. Use a literal or a constant field.");
            foreach (var a in args) FreeValue(a);
            return CValue.F(0);
        }

        var existing = FindScriptObject(key);
        bool isNew = existing == null;
        var node = existing ?? new LevelNode
        {
            ScriptCreated = true,
            ScriptSourceBlock = _codeBlock?.EditorId ?? "",
            ScriptSourceKey = key
        };
        ResetSceneDefaults(node, type, isNew);
        ApplySceneConstructor(node, type, args, n);
        ApplySceneInitializer(node, n.MemberInit);
        if (isNew) AddScriptObject(node);
        _scriptObjectKeys.Add(key);

        if (_loopDepth > 0 && _loopCreationWarnings.Add(key))
            Warn(n, "GRAB cannot create objects at runtime, so this `new` inside a loop creates one object and reuses it. Use Voxel.Grid / Voxel.Line for many blocks.");

        foreach (var a in args) FreeValue(a);
        return CValue.O(node, SceneObjectTypeFor(node.Kind));
    }

    private void ApplySceneConstructor(LevelNode node, string typeName, List<CValue> args, CsNode at)
    {
        string type = NormalizeTypeName(typeName);
        CValue Arg(int i) => i < args.Count ? args[i] : CValue.F(0);
        void TooMany(int max)
        {
            if (args.Count > max) Error(at, $"new {type} takes at most {max} constructor argument(s)");
        }

        switch (type)
        {
            case "Sign":
                TooMany(1);
                if (args.Count >= 1) node.Text = ConstText(Arg(0), at, "Sign text");
                break;
            case "PhysicsBlock": case "Block": case "CrumblingBlock": case "Crumbling":
                TooMany(2);
                if (args.Count >= 1) node.Shape = ConstInt(Arg(0), at, "Block shape", 1, 10000);
                if (args.Count >= 2) node.Material = ConstInt(Arg(1), at, "Block material", 0, 11);
                break;
            case "Cube": case "Sphere": case "Cylinder": case "Pyramid": case "Prism": case "Cone": case "PyramidSquare":
                TooMany(1);
                if (args.Count >= 1) node.Material = ConstInt(Arg(0), at, "Block material", 0, 11);
                break;
            case "Light":
                TooMany(3);
                if (args.Count >= 1) node.LightColor = ConstColor(Arg(0), at);
                if (args.Count >= 2) node.LightIntensity = ConstFloat(Arg(1), at, "Light intensity");
                if (args.Count >= 3) node.LightRange = ConstFloat(Arg(2), at, "Light range");
                break;
            case "Trigger":
                TooMany(1);
                if (args.Count >= 1)
                {
                    if (Arg(0).Parts is { Length: >= 3 })
                    {
                        var size = ConstVector(Arg(0), at);
                        node.Scale = new Vec3(MathF.Max(0.01f, size.X), MathF.Max(0.01f, size.Y), MathF.Max(0.01f, size.Z));
                    }
                    else
                    {
                        float size = MathF.Max(0.01f, ConstFloat(Arg(0), at, "Trigger size"));
                        node.Scale = new Vec3(size, size, size);
                    }
                }
                break;
            case "Sound": case "SoundEmitter":
                TooMany(1);
                if (args.Count >= 1) node.SoundName = ConstText(Arg(0), at, "Sound name");
                break;
            case "GravityBlock":
                TooMany(1);
                if (args.Count >= 1) node.GravityDirection = ConstVector(Arg(0), at);
                break;
            case "StartPoint": case "FinishPoint":
                TooMany(1);
                if (args.Count >= 1) node.Radius = MathF.Max(0.05f, ConstFloat(Arg(0), at, "Radius"));
                break;
            case "ParticleEmitter": case "Particles": case "LobbyTerminal":
                TooMany(0);
                break;
        }

        if (node.Kind is NodeKind.Static or NodeKind.Crumbling)
        {
            if (!ShapeInfo.All.Any(x => x.Id == node.Shape))
            {
                Error(at, $"Unknown block shape {node.Shape}. Use Shape.Cube, Shape.Sphere, Shape.Cylinder, Shape.Pyramid, Shape.Prism, Shape.Cone or Shape.PyramidSquare.");
                node.Shape = ShapeInfo.Cube;
            }
            if (node.Material is < 0 or > 11)
            {
                Error(at, $"Unknown block material {node.Material}. Use Material.Default through Material.Trigger.");
                node.Material = 8;
            }
        }
    }

    private void ApplySceneInitializer(LevelNode node, List<(string Name, Expr Value)> init)
    {
        foreach (var (name, expr) in init)
        {
            var value = Eval(expr);
            if (!IsCompileTimeValue(value))
            {
                Error(expr, $"'{name}' must be a compile-time constant when creating a scene object.");
                FreeValue(value);
                continue;
            }
            ApplySceneMember(node, name, value, expr);
            FreeValue(value);
        }
    }

    private void ApplySceneMember(LevelNode node, string name, CValue value, CsNode at)
    {
        switch (name)
        {
            case "Name": node.Name = ConstText(value, at, "Name"); return;
            case "Position":
                if (node.Kind == NodeKind.Finish) { Error(at, "Finish points have no Position property"); return; }
                node.Position = ConstVector(value, at); return;
            case "Rotation": node.Rotation = Quat.FromEulerDegrees(ConstVector(value, at)); return;
            case "Scale": node.Scale = ConstVector(value, at); return;
            case "Text": case "Write": case "Label":
                if (node.Kind == NodeKind.Sign) node.Text = ConstText(value, at, name);
                else node.EditorLabel = ConstText(value, at, name);
                return;
            case "Color":
                var color = ConstColor(value, at);
                if (node.Kind == NodeKind.Sign) node.SignColor = color;
                else if (node.Kind == NodeKind.Light) node.LightColor = color;
                else if (node.Kind is NodeKind.Static or NodeKind.Crumbling) node.Color1 = color;
                else Error(at, "Color can only be set on blocks, signs, lights and crumbling blocks");
                return;
            case "Shape":
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) { Error(at, "Shape is only available on solid blocks"); return; }
                node.Shape = ConstInt(value, at, "Shape", 1, 10000); return;
            case "Material":
                if (node.Kind is not (NodeKind.Static or NodeKind.Crumbling)) { Error(at, "Material is only available on solid blocks"); return; }
                node.Material = ConstInt(value, at, "Material", 0, 11); return;
            case "Physics": case "Enabled": node.PhysicsObject = value.Const != 0; return;
            case "LocalPhysics": node.LocalPhysicsObject = value.Const != 0; return;
            case "Mass": node.Mass = ConstFloat(value, at, "Mass"); return;
            case "Grabbable": node.IsGrabbable = value.Const != 0; return;
            case "Grapplable": node.IsGrapplable = value.Const != 0; return;
            case "Passable": node.IsPassable = value.Const != 0; return;
            case "Neon": node.IsNeon = value.Const != 0; return;
            case "Transparent": node.IsTransparent = value.Const != 0; return;
            case "Additive": node.IsAdditive = value.Const != 0; return;
            case "Gradient": node.IsGradient = value.Const != 0; return;
            case "GradientDirection": node.GradientDirection = ConstVector(value, at); return;
            case "Specular": case "SpecularBrightness": node.SpecularBrightness = ConstFloat(value, at, name); return;
            case "StableTime": node.StableTime = ConstFloat(value, at, "StableTime"); return;
            case "RespawnTime": node.RespawnTime = ConstFloat(value, at, "RespawnTime"); return;
            case "Local": node.IsLocal = value.Const != 0; return;
            case "Size": node.Scale = ConstVector(value, at); return;
            case "SignScale": node.SignScale = ConstFloat(value, at, "SignScale"); node.Scale = new Vec3(node.SignScale, node.SignScale, node.SignScale); return;
            case "SignWeight": node.SignWeight = ConstInt(value, at, "SignWeight", 0, 100); return;
            case "HideModel": node.HideModel = value.Const != 0; return;
            case "SignNeon": node.SignNeon = value.Const != 0; return;
            case "Brightness": case "Intensity": node.LightIntensity = ConstFloat(value, at, "Brightness"); return;
            case "Range": node.LightRange = ConstFloat(value, at, "Range"); return;
            case "Angle": node.LightAngle = ConstFloat(value, at, "Angle"); return;
            case "LightType": node.LightType = ConstInt(value, at, "LightType", 0, 1); return;
            case "DistanceFalloff": node.DistanceFalloffShape = ConstFloat(value, at, "DistanceFalloff"); return;
            case "ConeFalloff": node.ConeFalloffShape = ConstFloat(value, at, "ConeFalloff"); return;
            case "Shared": node.IsSharedTrigger = value.Const != 0; return;
            case "ParticlesPerSecond": case "Rate": node.ParticlesPerSecond = (uint)Math.Max(0, ConstInt(value, at, name, 0, 100000)); return;
            case "LifeSpanMin": node.LifeSpanMin = ConstFloat(value, at, "LifeSpanMin"); return;
            case "LifeSpanMax": node.LifeSpanMax = ConstFloat(value, at, "LifeSpanMax"); return;
            case "StartColor": node.StartColor = ConstColor(value, at); return;
            case "EndColor": node.EndColor = ConstColor(value, at); return;
            case "StartSizeMin": node.StartSizeMin = ConstFloat(value, at, "StartSizeMin"); return;
            case "StartSizeMax": node.StartSizeMax = ConstFloat(value, at, "StartSizeMax"); return;
            case "EndSizeMin": node.EndSizeMin = ConstFloat(value, at, "EndSizeMin"); return;
            case "EndSizeMax": node.EndSizeMax = ConstFloat(value, at, "EndSizeMax"); return;
            case "Velocity": node.Velocity = ConstVector(value, at); return;
            case "VelocityMin": node.VelocityMin = ConstVector(value, at); return;
            case "VelocityMax": node.VelocityMax = ConstVector(value, at); return;
            case "AccelerationMin": node.AccelerationMin = ConstVector(value, at); return;
            case "AccelerationMax": node.AccelerationMax = ConstVector(value, at); return;
            case "SoundName": node.SoundName = ConstText(value, at, "SoundName"); return;
            case "Volume": node.SoundVolume = ConstFloat(value, at, "Volume"); return;
            case "Repeat": node.SoundRepeat = value.Const != 0; return;
            case "StartActive":
                if (node.Kind == NodeKind.Sound) node.SoundStartActive = value.Const != 0;
                else node.StartActive = value.Const != 0;
                return;
            case "MaxRange": node.SoundMaxRange = ConstFloat(value, at, "MaxRange"); return;
            case "Frequency": node.SoundFrequency = ConstFloat(value, at, "Frequency"); return;
            case "WaveType": node.SoundWaveType = ConstFloat(value, at, "WaveType"); return;
            case "Attack": node.SoundAttack = ConstFloat(value, at, "Attack"); return;
            case "Sustain": node.SoundSustain = ConstFloat(value, at, "Sustain"); return;
            case "Release": node.SoundRelease = ConstFloat(value, at, "Release"); return;
            case "GravityMode": node.GravityMode = ConstInt(value, at, "GravityMode", 0, 2); return;
            case "GravityDirection": node.GravityDirection = ConstVector(value, at); return;
            case "HideParticles": node.HideParticles = value.Const != 0; return;
            case "Radius": node.Radius = ConstFloat(value, at, "Radius"); return;
            case "Hidden": node.IsHidden = value.Const != 0; return;
            default:
                Error(at, $"'{name}' is not an object-initializer property. Try Position, Rotation, Scale, Color, Name, Text, Physics, Material, Shape, Brightness, Range, Rate, Velocity or SoundName.");
                return;
        }
    }

    private static bool IsCompileTimeValue(CValue v)
    {
        if (v.IsConst || v.IsNull) return true;
        if (v.TextParts != null) return v.TextParts.All(p => p.Value == null);
        if (v.Parts != null) return v.Parts.All(IsCompileTimeValue);
        return false;
    }

    private float ConstFloat(CValue v, CsNode at, string what)
    {
        if (v.IsConst) return v.Const;
        Error(at, $"{what} must be a compile-time number");
        return 0;
    }

    private int ConstInt(CValue v, CsNode at, string what, int min, int max)
    {
        int value = (int)MathF.Round(ConstFloat(v, at, what));
        if (value < min || value > max)
        {
            Error(at, $"{what} must be in {min}..{max} (got {value})");
            return Math.Clamp(value, min, max);
        }
        return value;
    }

    private string ConstText(CValue v, CsNode at, string what)
    {
        if (v.Type.Kind == CsTypeKind.String && v.IsConst) return v.Str;
        if (v.TextParts != null && v.TextParts.All(p => p.Value == null))
            return string.Concat(v.TextParts.Select(p => p.Text));
        Error(at, $"{what} must be compile-time text");
        return "";
    }

    private Vec3 ConstVector(CValue v, CsNode at)
    {
        if (v.Parts is not { Length: >= 3 })
        {
            Error(at, "Expected a Vector3 value");
            return Vec3.Zero;
        }
        return new Vec3(ConstFloat(v.Parts[0], at, "Vector3.X"), ConstFloat(v.Parts[1], at, "Vector3.Y"), ConstFloat(v.Parts[2], at, "Vector3.Z"));
    }

    private Col ConstColor(CValue v, CsNode at)
    {
        if (v.Parts is not { Length: >= 3 })
        {
            Error(at, "Expected a Color value");
            return Col.White;
        }
        float a = v.Parts.Length >= 4 ? ConstFloat(v.Parts[3], at, "Color.A") : 1f;
        return new Col(ConstFloat(v.Parts[0], at, "Color.R"), ConstFloat(v.Parts[1], at, "Color.G"), ConstFloat(v.Parts[2], at, "Color.B"), a);
    }

    private LevelNode CreateVoxelCell(string key, Vec3 position, int shape, int material)
    {
        var existing = FindScriptObject(key);
        bool isNew = existing == null;
        var node = existing ?? new LevelNode
        {
            ScriptCreated = true,
            ScriptSourceBlock = _codeBlock?.EditorId ?? "",
            ScriptSourceKey = key
        };
        ResetSceneDefaults(node, "Block", isNew);
        node.Position = position;
        node.Shape = shape;
        node.Material = material;
        node.Scale = Vec3.One;
        if (isNew) AddScriptObject(node, ensureNames: false);
        _scriptObjectKeys.Add(key);
        return node;
    }

    private CValue VoxelCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        if (!args.All(IsCompileTimeValue))
        {
            Error(c, "Voxel helpers run while compiling, so their sizes, positions and spacing must be compile-time constants.");
            foreach (var a in args) FreeValue(a);
            return CValue.F(0);
        }
        float F(int i, float fallback) => i < args.Count ? ConstFloat(args[i], c, "Voxel argument") : fallback;
        Vec3 V(int i) => i < args.Count ? ConstVector(args[i], c) : Vec3.Zero;
        int I(int i, int fallback) => i < args.Count ? ConstInt(args[i], c, "Voxel argument", 0, 100000) : fallback;
        int shape = I(5, ShapeInfo.Cube);
        int material = I(6, 8);
        if (name is not ("Grid" or "Line" or "Ring"))
        {
            Error(c, $"Unknown Voxel function '{name}'. Supported: Grid, Line, Ring");
            foreach (var a in args) FreeValue(a);
            return CValue.F(0);
        }
        int count = 0;
        if (name == "Grid")
        {
            if (args.Count < 4) { Error(c, "Voxel.Grid needs (origin, countX, countY, countZ[, spacing, shape, material])"); foreach (var a in args) FreeValue(a); return CValue.F(0); }
            var origin = V(0);
            int cx = Math.Clamp(I(1, 1), 1, 64);
            int cy = Math.Clamp(I(2, 1), 1, 64);
            int cz = Math.Clamp(I(3, 1), 1, 64);
            float spacing = F(4, 1f);
            shape = I(5, ShapeInfo.Cube);
            material = I(6, 8);
            long total = (long)cx * cy * cz;
            if (total > 4096) { Error(c, $"Voxel.Grid would create {total} blocks; the safe maximum is 4096."); foreach (var a in args) FreeValue(a); return CValue.F(0); }
            if (!ShapeInfo.All.Any(x => x.Id == shape) || material is < 0 or > 11)
            { Error(c, "Voxel.Grid needs a valid Shape.* and Material.* constant"); foreach (var a in args) FreeValue(a); return CValue.F(0); }
            for (int z = 0; z < cz; z++)
            for (int y = 0; y < cy; y++)
            for (int x = 0; x < cx; x++)
            {
                var pos = new Vec3(origin.X + x * spacing, origin.Y + y * spacing, origin.Z + z * spacing);
                CreateVoxelCell($"voxel:grid@{c.Line}:{c.Col}:{x}:{y}:{z}", pos, shape, material);
                count++;
            }
        }
        else if (name == "Line")
        {
            if (args.Count < 2) { Error(c, "Voxel.Line needs (start, end[, spacing, shape, material])"); foreach (var a in args) FreeValue(a); return CValue.F(0); }
            var start = V(0); var end = V(1);
            float spacing = MathF.Max(0.01f, F(2, 1f));
            shape = I(3, ShapeInfo.Cube);
            material = I(4, 8);
            var delta = end - start;
            float length = delta.Length;
            int cells = Math.Clamp((int)MathF.Floor(length / spacing) + 1, 1, 4096);
            var step = length < 1e-5f ? Vec3.Zero : delta / length * spacing;
            for (int i = 0; i < cells; i++)
            {
                CreateVoxelCell($"voxel:line@{c.Line}:{c.Col}:{i}", start + step * i, shape, material);
                count++;
            }
        }
        else
        {
            if (args.Count < 3) { Error(c, "Voxel.Ring needs (center, radius, count[, shape, material])"); foreach (var a in args) FreeValue(a); return CValue.F(0); }
            var center = V(0);
            float radius = F(1, 1f);
            int cells = Math.Clamp(I(2, 1), 1, 4096);
            shape = I(3, ShapeInfo.Cube);
            material = I(4, 8);
            for (int i = 0; i < cells; i++)
            {
                float a = MathF.Tau * i / cells;
                var pos = new Vec3(center.X + MathF.Cos(a) * radius, center.Y, center.Z + MathF.Sin(a) * radius);
                CreateVoxelCell($"voxel:ring@{c.Line}:{c.Col}:{i}", pos, shape, material);
                count++;
            }
        }
        _level.EnsureNames();
        foreach (var a in args) FreeValue(a);
        return CValue.F(count);
    }

    // ================= Animator =================

    /// <summary>
    /// Animator is compiled to GRAB's native animation trigger: a real Trigger object
    /// with an Animation target connected to the scene object. Play/Stop/Pause/Resume
    /// write that trigger's Force register, so the same clip plays in the exported game.
    /// The trigger is generated, but the Hierarchy shows it so it can be inspected.
    /// </summary>
    private CValue AnimatorCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        try
        {
            if (args.Count < 1 || args[0].Node == null)
            {
                Error(c, "Animator needs a scene object as its first argument, e.g. Animator.Play(target, \"spin\").");
                return CValue.F(0);
            }
            var target = args[0].Node;
            string clipName = args.Count > 1 ? ConstText(args[1], c, "Animation clip name") : "";

            bool Flag(int index, bool fallback)
            {
                if (args.Count <= index) return fallback;
                if (!args[index].IsConst)
                {
                    Error(c, "Animator loop/reverse flags must be compile-time true or false so the trigger can be generated.");
                    return fallback;
                }
                return args[index].Const != 0;
            }

            switch (name)
            {
                case "Play":
                case "Start":
                    if (args.Count < 2) { Error(c, "Animator.Play needs (GameObject, clip[, loop[, reverse]])."); return CValue.F(0); }
                    if (!HasAnimationClip(target, clipName, c)) return CValue.F(0);
                    var playTrigger = EnsureAnimationTrigger(target, clipName, Flag(2, true), Flag(3, false), AnimationTargetMode.Start, c);
                    if (playTrigger != null)
                    {
                        ReleaseAnimationTriggers(target, playTrigger, c);
                        WriteTriggerForce(playTrigger, 1, c);
                    }
                    return new CValue { Type = CsType.Void };
                case "Stop":
                case "Pause":
                {
                    // STOP is a target mode in GRAB, not just Force=0. A dedicated stop
                    // trigger with mode STOP is what actually halts the clip.
                    var stopTriggers = EnsureAnimationActionTriggers(target, args, c, AnimationTargetMode.Stop, loop: false);
                    if (stopTriggers.Count == 0) return new CValue { Type = CsType.Void };
                    var keep = stopTriggers.ToHashSet();
                    ReleaseAnimationTriggers(target, keep, c);
                    foreach (var trigger in stopTriggers)
                    {
                        _scriptObjectKeys.Add(trigger.ScriptSourceKey);
                        WriteTriggerForce(trigger, 1, c);
                    }
                    return new CValue { Type = CsType.Void };
                }
                case "Resume":
                {
                    var resumeTriggers = EnsureAnimationActionTriggers(target, args, c, AnimationTargetMode.Start, loop: true);
                    if (resumeTriggers.Count == 0) return new CValue { Type = CsType.Void };
                    var keep = resumeTriggers.ToHashSet();
                    ReleaseAnimationTriggers(target, keep, c);
                    foreach (var trigger in resumeTriggers)
                    {
                        _scriptObjectKeys.Add(trigger.ScriptSourceKey);
                        WriteTriggerForce(trigger, 1, c);
                    }
                    return new CValue { Type = CsType.Void };
                }
                case "Restart":
                {
                    var restartTriggers = EnsureAnimationActionTriggers(target, args, c, AnimationTargetMode.Restart, loop: true);
                    if (restartTriggers.Count == 0) return new CValue { Type = CsType.Void };
                    var keep = restartTriggers.ToHashSet();
                    ReleaseAnimationTriggers(target, keep, c);
                    foreach (var trigger in restartTriggers)
                    {
                        _scriptObjectKeys.Add(trigger.ScriptSourceKey);
                        WriteTriggerForce(trigger, 1, c);
                    }
                    return new CValue { Type = CsType.Void };
                }
                case "Reverse":
                    if (args.Count < 2) { Error(c, "Animator.Reverse needs (GameObject, clip)."); return CValue.F(0); }
                    if (!HasAnimationClip(target, clipName, c)) return CValue.F(0);
                    var reverseTrigger = EnsureAnimationTrigger(target, clipName, Flag(2, true), true, AnimationTargetMode.ToggleReverse, c);
                    if (reverseTrigger != null)
                    {
                        ReleaseAnimationTriggers(target, reverseTrigger, c);
                        WriteTriggerForce(reverseTrigger, 1, c);
                    }
                    return new CValue { Type = CsType.Void };
                case "IsPlaying":
                    // Read the START trigger, never the STOP trigger left armed by a
                    // previous Animator.Stop call.
                    var first = AnimationTriggersFor(target).FirstOrDefault(n =>
                        n.TriggerTargets.Any(t => t.Kind == 1 && t.TargetMode == AnimationTargetMode.Start));
                    if (first == null)
                    {
                        first = EnsureAnimationActionTriggers(target, args, c, AnimationTargetMode.Start, loop: true).FirstOrDefault();
                        if (first == null) return CValue.B(false);
                    }
                    _scriptObjectKeys.Add(first.ScriptSourceKey);
                    var lv = ObjectProperty(first, c, "ForceActive");
                    if (lv == null) return CValue.B(false);
                    var read = lv.Read();
                    var playing = ApplyBinary(">", read, CValue.F(0.5f), c);
                    FreeValue(read);
                    return playing;
                default:
                    Error(c, $"Unknown Animator function '{name}'. Supported: Play, Stop, Pause, Resume, Restart, Reverse, IsPlaying.");
                    return CValue.F(0);
            }
        }
        finally
        {
            foreach (var a in args) FreeValue(a);
        }
    }

    private bool HasAnimationClip(LevelNode target, string clipName, CsNode at)
    {
        if (target.Animations.Count == 0)
        {
            Error(at, $"'{target.DisplayName}' has no animations. Open the Animation tab (Window → Animation) to author a clip first.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(clipName))
        {
            Error(at, "Animator needs the animation clip name as a constant string.");
            return false;
        }
        if (target.Animations.Any(a => string.Equals(a.Name, clipName, StringComparison.OrdinalIgnoreCase))) return true;
        Error(at, $"'{target.DisplayName}' has no animation named '{clipName}'. Clips: " + string.Join(", ", target.Animations.Select(a => a.Name)));
        return false;
    }

    /// <summary>
    /// Creates the trigger(s) for an animation control call. With an explicit clip
    /// there is one trigger; without a clip, Stop/Restart affect every clip on the
    /// object, matching GRAB's per-clip animation targets.
    /// </summary>
    private List<LevelNode> EnsureAnimationActionTriggers(LevelNode target, List<CValue> args, CsNode at, int mode, bool loop)
    {
        var result = new List<LevelNode>();
        if (target.Animations.Count == 0) return result;
        var clips = new List<string>();
        if (args.Count > 1)
        {
            string requested = ConstText(args[1], at, "Animation clip name");
            if (!HasAnimationClip(target, requested, at)) return result;
            clips.Add(requested);
        }
        else
        {
            clips.AddRange(target.Animations.Select(a => a.Name));
        }
        foreach (var clip in clips.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool reverse = mode == AnimationTargetMode.ToggleReverse;
            var trigger = EnsureAnimationTrigger(target, clip, loop, reverse, mode, at);
            if (trigger != null) result.Add(trigger);
        }
        return result;
    }

    /// <summary>Resets every other generated trigger so its next Force write is a 0→1 edge.</summary>
    private void ReleaseAnimationTriggers(LevelNode target, LevelNode except, CsNode at) =>
        ReleaseAnimationTriggers(target, new HashSet<LevelNode> { except }, at);

    private void ReleaseAnimationTriggers(LevelNode target, HashSet<LevelNode> keep, CsNode at)
    {
        foreach (var trigger in AnimationTriggersFor(target))
        {
            if (keep.Contains(trigger)) continue;
            _scriptObjectKeys.Add(trigger.ScriptSourceKey);
            WriteTriggerForce(trigger, 0, at);
        }
    }

    private IEnumerable<LevelNode> AnimationTriggersFor(LevelNode target) =>
        _level.AllNodes().Where(n => n.Kind == NodeKind.Trigger && n.ScriptCreated && n.Generated &&
            n.ScriptSourceBlock == _codeBlock?.EditorId &&
            n.ScriptSourceKey.StartsWith("anim:" + target.EditorId + ":", StringComparison.Ordinal));

    private string UniqueAnimationTriggerName()
    {
        // GRAB connection names are at most four characters. A plain "an" + hex
        // suffix fits exactly and avoids the old five-character names on big levels.
        for (int i = 0; i < 256; i++)
        {
            string name = "an" + i.ToString("x2", CultureInfo.InvariantCulture);
            if (_level.AllNodes().All(n => !string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
        return "an" + (_level.AllNodes().Count() % 256).ToString("x2", CultureInfo.InvariantCulture);
    }

    private LevelNode? EnsureAnimationTrigger(LevelNode target, string clipName, bool loop, bool reverse, int mode, CsNode at)
    {
        string key = "anim:" + target.EditorId + ":" + clipName + ":" + (loop ? "1" : "0") + ":" + (reverse ? "1" : "0") + ":" + mode;
        var existing = FindScriptObject(key);
        if (existing != null)
        {
            _scriptObjectKeys.Add(key);
            return existing;
        }

        var trigger = new LevelNode
        {
            Kind = NodeKind.Trigger,
            Generated = true,
            ScriptCreated = true,
            ScriptSourceBlock = _codeBlock?.EditorId ?? "",
            ScriptSourceKey = key,
            // Far below the level and tiny: the trigger can only ever fire through Force.
            Position = new Vec3(0, -9999, 0),
            Scale = new Vec3(0.1f, 0.1f, 0.1f),
            Name = UniqueAnimationTriggerName(),
            StartActive = true
        };
        trigger.TriggerSources.Add(new TriggerSource { BasicType = 0 });
        trigger.TriggerTargets.Add(new TriggerTarget
        {
            Mode = 0,
            Kind = 1,
            ObjectId = _objectIds != null && _objectIds.TryGetValue(target, out var id) ? id : 0,
            ObjectName = target.DisplayName,
            AnimationName = clipName,
            Loop = loop,
            Reverse = reverse,
            // GRAB's TriggerTargetAnimation.Mode: STOP=0, START=1,
            // TOGGLE_REVERSE=3, RESTART=4, RESET=5.
            TargetMode = mode
        });
        AddScriptObject(trigger, ensureNames: false);
        _level.EnsureNames();
        _scriptObjectKeys.Add(key);
        return trigger;
    }

    private void WriteTriggerForce(LevelNode trigger, float value, CsNode at)
    {
        var lv = ObjectProperty(trigger, at, "ForceActive", forWrite: true);
        lv?.Write(CValue.F(value));
    }

    private CValue ScalarMath1(GasmOp op, CValue a, CsNode at)
    {
        if (a.IsConst)
        {
            float r = op switch { GasmOp.Sin => MathF.Sin(a.Const * MathF.PI / 180), GasmOp.Cos => MathF.Cos(a.Const * MathF.PI / 180), GasmOp.Sqrt => MathF.Sqrt(Math.Max(0, a.Const)), GasmOp.Abs => MathF.Abs(a.Const), GasmOp.Floor => MathF.Floor(a.Const), _ => a.Const };
            return CValue.F(r);
        }
        int src = Materialize(a);
        int t = _e.AllocTemp();
        _e.Emit(op, GasmEmitter.Work(t), GasmEmitter.Work(src));
        _e.FreeTemp(src);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue DegreesToRadians(CValue v)
    {
        if (v.IsConst) return CValue.F(v.Const * MathF.PI / 180f);
        var r = Materialize(v);
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Mul, GasmEmitter.Work(t), GasmEmitter.Work(r), GasmEmitter.Const(MathF.PI / 180f));
        _e.FreeTemp(r);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue RadiansToDegrees(CValue v)
    {
        if (v.IsConst) return CValue.F(v.Const * 180f / MathF.PI);
        var r = Materialize(v);
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Mul, GasmEmitter.Work(t), GasmEmitter.Work(r), GasmEmitter.Const(180f / MathF.PI));
        _e.FreeTemp(r);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue MathCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue A(int i) => i < args.Count ? args[i] : CValue.F(0);
        void FreeArgs() { foreach (var a in args) FreeValue(a); }
        CValue Result(CValue v) { foreach (var a in args) if (!ReferenceEquals(a, v)) FreeValue(a); return v; }
        switch (name)
        {
            case "Sin": return Result(ScalarMath1(GasmOp.Sin, RadiansToDegrees(A(0)), c));
            case "Cos": return Result(ScalarMath1(GasmOp.Cos, RadiansToDegrees(A(0)), c));
            case "Tan":
            {
                var s = ScalarMath1(GasmOp.Sin, RadiansToDegrees(A(0)), c);
                var co = ScalarMath1(GasmOp.Cos, RadiansToDegrees(A(0)), c);
                var r = ScalarOp("/", s, co, c);
                FreeArgs();
                return r;
            }
            case "Atan2":
            {
                var a = Materialize(A(0)); var b = Materialize(A(1));
                int t = _e.AllocTemp();
                _e.Emit(GasmOp.Atan2, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(b));
                _e.FreeTemp(a); _e.FreeTemp(b);
                FreeArgs();
                return DegreesToRadians(new CValue { Type = CsType.Float, Reg = t, IsTemp = true });
            }
            case "Sqrt": return Result(ScalarMath1(GasmOp.Sqrt, A(0), c));
            case "Abs": return Result(ScalarMath1(GasmOp.Abs, A(0), c));
            case "Floor":
            case "FloorToInt":
                return Result(ScalarMath1(GasmOp.Floor, A(0), c));
            case "Ceil":
            case "CeilToInt":
            {
                var n = ScalarOp("-", CValue.F(0), A(0), c);
                var f = ScalarMath1(GasmOp.Floor, n, c);
                var r = ScalarOp("-", CValue.F(0), f, c);
                FreeArgs();
                return r;
            }
            case "Round":
            case "RoundToInt":
            {
                var plus = ScalarOp("+", A(0), CValue.F(0.5f), c);
                var r = ScalarMath1(GasmOp.Floor, plus, c);
                FreeArgs();
                return r;
            }
            case "Pow":
            {
                var a = Materialize(A(0)); var b = Materialize(A(1));
                int t = _e.AllocTemp();
                _e.Emit(GasmOp.Pow, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(b));
                _e.FreeTemp(a); _e.FreeTemp(b);
                FreeArgs();
                return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
            }
            case "Min": { var r = MinMax(A(0), A(1), false, c); FreeArgs(); return r; }
            case "Max": { var r = MinMax(A(0), A(1), true, c); FreeArgs(); return r; }
            case "Clamp":
            {
                var v = A(0); var min = A(1); var max = A(2);
                var m1 = MinMax(v, max, false, c);
                var m2 = MinMax(m1, min, true, c);
                FreeArgs();
                return m2;
            }
            case "Lerp":
            {
                var a = A(0); var b = A(1); var t = A(2);
                var diff = ScalarOp("-", b, a, c);
                var mul = ScalarOp("*", diff, t, c);
                var r = ScalarOp("+", a, mul, c);
                FreeArgs();
                return r;
            }
            case "Sign":
            {
                var v = Materialize(A(0));
                int less = _e.AllocTemp(); int greater = _e.AllocTemp();
                _e.Emit(GasmOp.Less, GasmEmitter.Work(less), GasmEmitter.Work(v), GasmEmitter.Const(0));
                _e.Emit(GasmOp.Greater, GasmEmitter.Work(greater), GasmEmitter.Work(v), GasmEmitter.Const(0));
                int t = _e.AllocTemp();
                int neg = _e.NewLabel("sign_neg"), pos = _e.NewLabel("sign_pos"), end = _e.NewLabel("sign_end");
                _e.Emit(GasmOp.If, GasmEmitter.Work(less), GasmOperand.Label(neg));
                _e.Emit(GasmOp.If, GasmEmitter.Work(greater), GasmOperand.Label(pos));
                _e.Emit(GasmOp.Set, GasmEmitter.Work(t), GasmEmitter.Const(0));
                _e.Goto(end);
                _e.Mark(neg); _e.Emit(GasmOp.Set, GasmEmitter.Work(t), GasmEmitter.Const(-1)); _e.Goto(end);
                _e.Mark(pos); _e.Emit(GasmOp.Set, GasmEmitter.Work(t), GasmEmitter.Const(1));
                _e.Mark(end);
                _e.FreeTemp(v); _e.FreeTemp(less); _e.FreeTemp(greater);
                FreeArgs();
                return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
            }
            case "Repeat":
            {
                var a = Materialize(A(0)); var b = Materialize(A(1));
                int t = _e.AllocTemp();
                _e.Emit(GasmOp.Mod, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(b));
                _e.FreeTemp(a); _e.FreeTemp(b);
                FreeArgs();
                return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
            }
            case "PingPong":
            {
                var a = A(0); var b = A(1);
                var doubled = ScalarOp("*", a, CValue.F(2), c);
                var m = Materialize(doubled); var len = Materialize(b);
                int rep = _e.AllocTemp();
                _e.Emit(GasmOp.Mod, GasmEmitter.Work(rep), GasmEmitter.Work(m), GasmEmitter.Work(len));
                _e.FreeTemp(m); _e.FreeTemp(len);
                var diff = ScalarOp("-", b, new CValue { Type = CsType.Float, Reg = rep, IsTemp = true }, c);
                var r = ScalarMath1(GasmOp.Abs, diff, c);
                FreeArgs();
                return r;
            }
            case "Rad2Deg": return Result(RadiansToDegrees(A(0)));
            case "Deg2Rad": return Result(DegreesToRadians(A(0)));
            case "IsPowerOfTwo":
            case "NextPowerOfTwo":
            {
                var v = A(0);
                if (!v.IsConst)
                {
                    Error(c, $"{name} needs a compile-time constant; GASM has no bitwise instructions");
                    Result(CValue.F(0));
                    return CValue.F(0);
                }
                int n = Math.Max(0, (int)MathF.Round(v.Const));
                if (name == "IsPowerOfTwo")
                {
                    Result(CValue.B(n > 0 && (n & (n - 1)) == 0));
                    return CValue.B(n > 0 && (n & (n - 1)) == 0);
                }
                int next = 1;
                while (next < n) next <<= 1;
                Result(CValue.F(next));
                return CValue.F(next);
            }
            case "Clamp01":
            {
                var upper = MinMax(A(0), CValue.F(1), false, c);
                var lower = MinMax(upper, CValue.F(0), true, c);
                FreeArgs();
                return lower;
            }
            case "Approximately":
            {
                var d = ScalarOp("-", A(0), A(1), c);
                var abs = ScalarMath1(GasmOp.Abs, d, c);
                var r = ApplyBinary("<", abs, CValue.F(0.00001f), c);
                FreeArgs();
                return r;
            }
            case "InverseLerp":
            {
                var denom = ScalarOp("-", A(1), A(0), c);
                var numer = ScalarOp("-", A(2), A(0), c);
                var t = ScalarOp("/", numer, denom, c);
                var upper = MinMax(t, CValue.F(1), false, c);
                var r = MinMax(upper, CValue.F(0), true, c);
                FreeArgs();
                return r;
            }
            case "SmoothStep":
            {
                var t = MinMax(MinMax(A(2), CValue.F(1), false, c), CValue.F(0), true, c);
                var t2 = ScalarOp("*", t, t, c);
                var inner = ScalarOp("-", CValue.F(3), ScalarOp("*", CValue.F(2), t, c), c);
                var weight = ScalarOp("*", t2, inner, c);
                var diff = ScalarOp("-", A(1), A(0), c);
                var r = ScalarOp("+", A(0), ScalarOp("*", diff, weight, c), c);
                FreeArgs();
                return r;
            }
            case "MoveTowards":
            {
                var cur = A(0); var target = A(1); var maxDelta = A(2);
                var diff = ScalarOp("-", target, cur, c);
                var dist = ScalarMath1(GasmOp.Abs, diff, c);
                var within = ApplyBinary("<=", dist, maxDelta, c);
                var positive = ApplyBinary(">", diff, CValue.F(0), c);
                var step = SelectValue(positive, ScalarOp("+", cur, maxDelta, c), ScalarOp("-", cur, maxDelta, c), c);
                var r = SelectValue(within, target, step, c);
                FreeArgs();
                return r;
            }
            case "Atan":
            {
                var r = Atan2Radians(A(0), CValue.F(1), c);
                FreeArgs();
                return r;
            }
            case "Asin":
            {
                var x = A(0);
                var square = ScalarOp("*", x, x, c);
                var oneMinus = ScalarOp("-", CValue.F(1), square, c);
                var root = ScalarMath1(GasmOp.Sqrt, oneMinus, c);
                var r = Atan2Radians(x, root, c);
                FreeArgs();
                return r;
            }
            case "Acos":
            {
                var x = A(0);
                var square = ScalarOp("*", x, x, c);
                var oneMinus = ScalarOp("-", CValue.F(1), square, c);
                var root = ScalarMath1(GasmOp.Sqrt, oneMinus, c);
                var r = Atan2Radians(root, x, c);
                FreeArgs();
                return r;
            }
            case "DeltaAngle":
            {
                var delta = ScalarOp("-", A(1), A(0), c);
                var r = DeltaAngleValue(delta, c);
                FreeArgs();
                return r;
            }
            case "LerpAngle":
            {
                var delta = DeltaAngleValue(ScalarOp("-", A(1), A(0), c), c);
                var scaled = ScalarOp("*", delta, A(2), c);
                var r = ScalarOp("+", A(0), scaled, c);
                FreeArgs();
                return r;
            }
            case "MoveTowardsAngle":
            {
                var delta = DeltaAngleValue(ScalarOp("-", A(1), A(0), c), c);
                var distance = ScalarMath1(GasmOp.Abs, delta, c);
                var within = ApplyBinary("<=", distance, A(2), c);
                var positive = ApplyBinary(">", delta, CValue.F(0), c);
                var limit = SelectValue(positive, A(2), ScalarOp("-", CValue.F(0), A(2), c), c);
                var step = SelectValue(within, delta, limit, c);
                var r = ScalarOp("+", A(0), step, c);
                FreeArgs();
                return r;
            }
            default:
                Error(c, $"Unknown Mathf function '{name}'");
                FreeArgs();
                return CValue.F(0);
        }
    }

    private CValue Atan2Radians(CValue y, CValue x, CsNode at)
    {
        var a = Materialize(y);
        var b = Materialize(x);
        int t = _e.AllocTemp();
        _e.Emit(GasmOp.Atan2, GasmEmitter.Work(t), GasmEmitter.Work(a), GasmEmitter.Work(b));
        _e.FreeTemp(a); _e.FreeTemp(b);
        var degrees = new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
        var radians = DegreesToRadians(degrees);
        if (radians.Reg != t) _e.FreeTemp(t);
        return radians;
    }

    private CValue DeltaAngleValue(CValue delta, CsNode at)
    {
        var mod = ScalarOp("%", delta, CValue.F(360), at);
        var low = ApplyBinary("<", mod, CValue.F(-180), at);
        var plus = ScalarOp("+", mod, CValue.F(360), at);
        var wrapped = SelectValue(low, plus, mod, at);
        var high = ApplyBinary(">", wrapped, CValue.F(180), at);
        var minus = ScalarOp("-", wrapped, CValue.F(360), at);
        var result = SelectValue(high, minus, wrapped, at);
        FreeValue(mod); FreeValue(low); FreeValue(plus); FreeValue(high); FreeValue(minus); FreeValue(wrapped);
        return result;
    }

    /// <summary>Branching select: cond != 0 ? whenTrue : whenFalse.</summary>
    private CValue SelectValue(CValue cond, CValue whenTrue, CValue whenFalse, CsNode at)
    {
        int c = Materialize(cond);
        int t = _e.AllocTemp();
        int trueLabel = _e.NewLabel("sel_true");
        int endLabel = _e.NewLabel("sel_end");
        _e.Emit(GasmOp.If, GasmEmitter.Work(c), GasmOperand.Label(trueLabel));
        _e.Emit(GasmOp.Set, GasmEmitter.Work(t), whenFalse.Operand());
        _e.Goto(endLabel);
        _e.Mark(trueLabel);
        _e.Emit(GasmOp.Set, GasmEmitter.Work(t), whenTrue.Operand());
        _e.Mark(endLabel);
        _e.FreeTemp(c);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue MinMax(CValue a, CValue b, bool max, CsNode at)
    {
        var ra = Materialize(a); var rb = Materialize(b);
        int t = _e.AllocTemp();
        int cmp = _e.AllocTemp();
        int trueL = _e.NewLabel(max ? "max" : "min");
        int endL = _e.NewLabel("mm_end");
        _e.Emit(max ? GasmOp.Greater : GasmOp.Less, GasmEmitter.Work(cmp), GasmEmitter.Work(ra), GasmEmitter.Work(rb));
        _e.Emit(GasmOp.If, GasmEmitter.Work(cmp), GasmOperand.Label(trueL));
        _e.Emit(GasmOp.Set, GasmEmitter.Work(t), GasmEmitter.Work(rb));
        _e.Goto(endL);
        _e.Mark(trueL);
        _e.Emit(GasmOp.Set, GasmEmitter.Work(t), GasmEmitter.Work(ra));
        _e.Mark(endL);
        _e.FreeTemp(ra); _e.FreeTemp(rb); _e.FreeTemp(cmp);
        return new CValue { Type = CsType.Float, Reg = t, IsTemp = true };
    }

    private CValue VectorCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        CValue A(int i) => i < args.Count ? args[i] : CValue.V(CValue.F(0), CValue.F(0), CValue.F(0));
        switch (name)
        {
            case "Distance":
            {
                var a = A(0); var b = A(1);
                var dx = ScalarOp("-", a.Parts![0], b.Parts![0], c);
                var dy = ScalarOp("-", a.Parts[1], b.Parts[1], c);
                var dz = ScalarOp("-", a.Parts[2], b.Parts[2], c);
                var r = VectorLength(dx, dy, dz);
                FreeArgs(args); return r;
            }
            case "SqrMagnitude":
            {
                var a = A(0); var p = a.Parts!;
                var r = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", p[0], p[0], c),
                    ScalarOp("*", p[1], p[1], c), c),
                    ScalarOp("*", p[2], p[2], c), c);
                FreeArgs(args); return r;
            }
            case "ClampMagnitude":
            {
                var a = A(0); var max = A(1); var p = a.Parts!;
                var length = VectorLength(p[0], p[1], p[2]);
                var within = ApplyBinary("<=", length, max, c);
                var direction = VectorNormalize(p[0], p[1], p[2]);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                {
                    var scaled = ScalarOp("*", direction.Parts![i], max, c);
                    parts[i] = SelectValue(within, p[i], scaled, c);
                }
                FreeValue(length); FreeValue(within); FreeValue(direction);
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "Project":
            {
                var a = A(0); var n = A(1); var ap = a.Parts!; var np = n.Parts!;
                var dot = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", ap[0], np[0], c),
                    ScalarOp("*", ap[1], np[1], c), c),
                    ScalarOp("*", ap[2], np[2], c), c);
                var nn = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", np[0], np[0], c),
                    ScalarOp("*", np[1], np[1], c), c),
                    ScalarOp("*", np[2], np[2], c), c);
                var scale = ScalarOp("/", dot, nn, c);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++) parts[i] = ScalarOp("*", np[i], scale, c);
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "ProjectOnPlane":
            {
                var a = A(0); var n = A(1); var ap = a.Parts!; var np = n.Parts!;
                var dot = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", ap[0], np[0], c),
                    ScalarOp("*", ap[1], np[1], c), c),
                    ScalarOp("*", ap[2], np[2], c), c);
                var nn = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", np[0], np[0], c),
                    ScalarOp("*", np[1], np[1], c), c),
                    ScalarOp("*", np[2], np[2], c), c);
                var scale = ScalarOp("/", dot, nn, c);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                {
                    var projection = ScalarOp("*", np[i], scale, c);
                    parts[i] = ScalarOp("-", ap[i], projection, c);
                }
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "Angle":
            {
                var a = A(0); var b = A(1); var ap = a.Parts!; var bp = b.Parts!;
                var na = VectorNormalize(ap[0], ap[1], ap[2]);
                var nb = VectorNormalize(bp[0], bp[1], bp[2]);
                var dot = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", na.Parts![0], nb.Parts![0], c),
                    ScalarOp("*", na.Parts[1], nb.Parts[1], c), c),
                    ScalarOp("*", na.Parts[2], nb.Parts[2], c), c);
                var upper = MinMax(dot, CValue.F(1), false, c);
                var lower = MinMax(upper, CValue.F(-1), true, c);
                var oneMinus = ScalarOp("-", CValue.F(1), ScalarOp("*", lower, lower, c), c);
                var root = ScalarMath1(GasmOp.Sqrt, oneMinus, c);
                var radians = Atan2Radians(root, lower, c);
                var degrees = RadiansToDegrees(radians);
                FreeValue(na); FreeValue(nb); FreeValue(dot); FreeValue(upper); FreeValue(lower);
                FreeArgs(args);
                return degrees;
            }
            case "Dot":
            {
                var a = A(0); var b = A(1);
                var x = ScalarOp("*", a.Parts![0], b.Parts![0], c);
                var y = ScalarOp("*", a.Parts[1], b.Parts[1], c);
                var z = ScalarOp("*", a.Parts[2], b.Parts[2], c);
                var r = ScalarOp("+", ScalarOp("+", x, y, c), z, c);
                FreeArgs(args); return r;
            }
            case "Cross":
            {
                var a = A(0); var b = A(1);
                var ap = a.Parts!; var bp = b.Parts!;
                var x = ScalarOp("-", ScalarOp("*", ap[1], bp[2], c), ScalarOp("*", ap[2], bp[1], c), c);
                var y = ScalarOp("-", ScalarOp("*", ap[2], bp[0], c), ScalarOp("*", ap[0], bp[2], c), c);
                var z = ScalarOp("-", ScalarOp("*", ap[0], bp[1], c), ScalarOp("*", ap[1], bp[0], c), c);
                var r = CValue.V(x, y, z);
                FreeArgs(args); return r;
            }
            case "Normalize":
            {
                var a = A(0);
                var r = VectorNormalize(a.Parts![0], a.Parts[1], a.Parts[2]);
                FreeArgs(args); return r;
            }
            case "Lerp":
            case "LerpUnclamped":
            {
                var a = A(0); var b = A(1); var t = A(2);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                {
                    var diff = ScalarOp("-", b.Parts![i], a.Parts![i], c);
                    var mul = ScalarOp("*", diff, t, c);
                    parts[i] = ScalarOp("+", a.Parts![i], mul, c);
                }
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "Scale":
            {
                var a = A(0); var b = A(1);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++) parts[i] = ScalarOp("*", a.Parts![i], b.Parts![i], c);
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "Min":
            case "Max":
            {
                var a = A(0); var b = A(1);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++) parts[i] = MinMax(a.Parts![i], b.Parts![i], name == "Max", c);
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "MoveTowards":
            {
                var a = A(0); var b = A(1); var max = A(2);
                var dx = ScalarOp("-", b.Parts![0], a.Parts![0], c);
                var dy = ScalarOp("-", b.Parts[1], a.Parts[1], c);
                var dz = ScalarOp("-", b.Parts[2], a.Parts[2], c);
                var distance = VectorLength(dx, dy, dz);
                var within = ApplyBinary("<=", distance, max, c);
                var direction = VectorNormalize(dx, dy, dz);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                {
                    var step = ScalarOp("*", direction.Parts![i], max, c);
                    var moved = ScalarOp("+", a.Parts![i], step, c);
                    parts[i] = SelectValue(within, b.Parts![i], moved, c);
                }
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            case "Reflect":
            {
                var d = A(0); var n = A(1);
                var dot = ScalarOp("+", ScalarOp("+",
                    ScalarOp("*", d.Parts![0], n.Parts![0], c),
                    ScalarOp("*", d.Parts[1], n.Parts[1], c), c),
                    ScalarOp("*", d.Parts[2], n.Parts[2], c), c);
                var twoDot = ScalarOp("*", dot, CValue.F(2), c);
                var parts = new CValue[3];
                for (int i = 0; i < 3; i++)
                    parts[i] = ScalarOp("-", d.Parts![i], ScalarOp("*", twoDot, n.Parts![i], c), c);
                FreeArgs(args);
                return new CValue { Type = CsType.Vector3, Parts = parts };
            }
            default:
                Error(c, $"Unknown Vector3 function '{name}'");
                FreeArgs(args);
                return CValue.F(0);
        }
    }

    private CValue ColorCall(string name, CallExpr c)
    {
        var args = c.Args.Select(Eval).ToList();
        if ((name is "Lerp" or "LerpUnclamped") && args.Count == 3)
        {
            var a = args[0]; var b = args[1]; var t = args[2];
            if (a.Parts == null || b.Parts == null) { FreeArgs(args); return args[0]; }
            var parts = new CValue[4];
            for (int i = 0; i < 4; i++)
            {
                var diff = ScalarOp("-", b.Parts[i], a.Parts[i], c);
                var mul = ScalarOp("*", diff, t, c);
                parts[i] = ScalarOp("+", a.Parts[i], mul, c);
            }
            FreeArgs(args);
            return new CValue { Type = CsType.Color, Parts = parts };
        }
        Error(c, $"Unknown Color function '{name}'");
        FreeArgs(args);
        return CValue.F(0);
    }

    private CValue VectorLength(CValue x, CValue y, CValue z)
    {
        var xx = ScalarOp("*", x, x, new LiteralExpr());
        var yy = ScalarOp("*", y, y, new LiteralExpr());
        var zz = ScalarOp("*", z, z, new LiteralExpr());
        var sum = ScalarOp("+", ScalarOp("+", xx, yy, new LiteralExpr()), zz, new LiteralExpr());
        return ScalarMath1(GasmOp.Sqrt, sum, new LiteralExpr());
    }

    private CValue VectorNormalize(CValue x, CValue y, CValue z)
    {
        var len = VectorLength(x, y, z);
        var lr = Materialize(len);
        int zero = _e.AllocTemp();
        _e.Emit(GasmOp.Equal, GasmEmitter.Work(zero), GasmEmitter.Work(lr), GasmEmitter.Const(0));
        int zeroL = _e.NewLabel("vn_zero"), endL = _e.NewLabel("vn_end");
        int rx = _e.AllocTemp(), ry = _e.AllocTemp(), rz = _e.AllocTemp();
        _e.Emit(GasmOp.If, GasmEmitter.Work(zero), GasmOperand.Label(zeroL));
        _e.Emit(GasmOp.Div, GasmEmitter.Work(rx), x.Operand(), GasmEmitter.Work(lr));
        _e.Emit(GasmOp.Div, GasmEmitter.Work(ry), y.Operand(), GasmEmitter.Work(lr));
        _e.Emit(GasmOp.Div, GasmEmitter.Work(rz), z.Operand(), GasmEmitter.Work(lr));
        _e.Goto(endL);
        _e.Mark(zeroL);
        _e.Emit(GasmOp.Set, GasmEmitter.Work(rx), GasmEmitter.Const(0));
        _e.Emit(GasmOp.Set, GasmEmitter.Work(ry), GasmEmitter.Const(0));
        _e.Emit(GasmOp.Set, GasmEmitter.Work(rz), GasmEmitter.Const(0));
        _e.Mark(endL);
        _e.FreeTemp(lr); _e.FreeTemp(zero);
        return new CValue
        {
            Type = CsType.Vector3,
            Parts = new[]
            {
                new CValue { Type = CsType.Float, Reg = rx, IsTemp = true },
                new CValue { Type = CsType.Float, Reg = ry, IsTemp = true },
                new CValue { Type = CsType.Float, Reg = rz, IsTemp = true }
            }
        };
    }

    private void FreeArgs(List<CValue> args) { foreach (var a in args) FreeValue(a); }

    private static bool Assignable(CsType from, CsType to)
    {
        if (to.Kind == CsTypeKind.Error || from.Kind == CsTypeKind.Error) return true;
        if (from.Kind == to.Kind && from.Definition == null && to.Definition == null) return true;
        if (to.Kind == CsTypeKind.Float && from.IsNumeric) return true;
        if (to.Kind == CsTypeKind.Bool && from.IsNumeric) return true;
        if (to.Kind == CsTypeKind.GameObject && IsObjectType(from)) return true;
        if (to.Definition is TypeDef targetDef && from.Definition is TypeDef sourceDef)
        {
            if (to.Kind == CsTypeKind.Interface)
            {
                for (var cur = sourceDef; cur != null; cur = cur.Base)
                    if (cur.Key == targetDef.Key || cur.Interfaces.Any(i => i.Key == targetDef.Key)) return true;
                return false;
            }
            if (targetDef.Kind == TypeDeclKind.Class) return sourceDef.IsSubclassOf(targetDef);
            return sourceDef.Key == targetDef.Key;
        }
        return false;
    }

    private CValue ErrorVal(CsNode at, string msg) { _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = msg, Line = at.Line, Column = at.Col, File = "Script.cs" }); return CValue.F(0); }
}
