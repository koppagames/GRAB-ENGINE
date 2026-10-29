using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GrabEngine.Core;

namespace GrabEngine.Code;

public enum CsTokKind { Eof, Ident, Keyword, Number, String, Char, Punct }

public sealed class CsToken
{
    public CsTokKind Kind { get; init; }
    public string Text { get; init; } = "";
    public string Value { get; init; } = "";
    public double Number { get; init; }
    public bool IsInterpolated { get; init; }
    public bool IsVerbatim { get; init; }
    public int Line { get; init; }
    public int Col { get; init; }
    public int Pos { get; init; }
    public override string ToString() => Text;
}

public enum CsTypeKind { Error, Void, Float, Bool, String, Vector3, Color, GameObject, Trigger, Light, Sign, PhysicsBlock, PlayerType, LevelType, Object, Array, Struct, Interface, List, Dictionary, Task }

public sealed class CsType
{
    public CsTypeKind Kind { get; set; }
    public string Name { get; set; } = "";
    public CsType(CsTypeKind k, string? name = null) { Kind = k; Name = name ?? k.ToString(); }
    public static readonly CsType Error = new(CsTypeKind.Error, "<error>");
    public static readonly CsType Void = new(CsTypeKind.Void, "void");
    public static readonly CsType Float = new(CsTypeKind.Float, "float");
    public static readonly CsType Bool = new(CsTypeKind.Bool, "bool");
    public static readonly CsType String = new(CsTypeKind.String, "string");
    public static readonly CsType Vector3 = new(CsTypeKind.Vector3, "Vector3");
    public static readonly CsType Color = new(CsTypeKind.Color, "Color");
    public static readonly CsType GameObject = new(CsTypeKind.GameObject, "GameObject");
    public static readonly CsType Task = new(CsTypeKind.Task, "Task");
    public override string ToString() => Name;
    public bool IsNumeric => Kind is CsTypeKind.Float or CsTypeKind.Bool;

    /// <summary>Element type when Kind is Array.</summary>
    public CsType? Element { get; set; }
    public bool IsArray => Kind == CsTypeKind.Array;

    /// <summary>Class / struct / interface definition for user types.</summary>
    public object? Definition { get; set; }
    public List<CsType>? TypeArgs { get; set; }
    public CsType? KeyType { get; set; }
    public int Capacity { get; set; }

    public bool IsUserType => Definition != null;
    public bool IsReferenceType => Kind is CsTypeKind.Object or CsTypeKind.Interface;
    public bool IsValueType => Kind == CsTypeKind.Struct;
    public bool IsCollection => Kind is CsTypeKind.List or CsTypeKind.Dictionary;

    public static CsType ArrayOf(CsType element) => new(CsTypeKind.Array, element.Name + "[]") { Element = element };
}

public abstract class CsNode
{
    public int Line { get; set; } = 1;
    public int Col { get; set; } = 1;
}

public sealed class CompilationUnit : CsNode
{
    public List<ClassDecl> Classes { get; } = new();
    public List<Stmt> TopLevel { get; } = new();
    public List<string> Usings { get; } = new();
}

public enum TypeDeclKind { Class, Struct, Interface }

public sealed class ClassDecl : CsNode
{
    public string Name { get; set; } = "";
    public TypeDeclKind Kind { get; set; } = TypeDeclKind.Class;
    public string BaseName { get; set; } = "";
    public List<string> Interfaces { get; } = new();
    public List<string> GenericParams { get; } = new();
    public bool IsAbstract { get; set; }
    public List<string> Attributes { get; } = new();
    public List<FieldDecl> Fields { get; } = new();
    public List<MethodDecl> Methods { get; } = new();
}

public sealed class FieldDecl : CsNode
{
    public string TypeName { get; set; } = "float";
    public bool IsArray { get; set; }
    public string Name { get; set; } = "";
    public Expr? Initializer { get; set; }
    public bool IsConst { get; set; }
    public bool IsStatic { get; set; }
    public bool IsReadonly { get; set; }
    public string Access { get; set; } = "private";
    public bool IsPublic => Access == "public";
    public List<string> Attributes { get; } = new();
}

public sealed class MethodDecl : CsNode
{
    public string ReturnTypeName { get; set; } = "void";
    public string Name { get; set; } = "";
    public List<ParamDecl> Params { get; } = new();
    public List<string> GenericParams { get; } = new();
    public BlockStmt? Body { get; set; }
    public Expr? ExpressionBody { get; set; }
    public bool IsStatic { get; set; }
    public bool IsConstructor { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsAsync { get; set; }
    public bool IsReadonly { get; set; }
    public string Access { get; set; } = "private";
    public List<string> Attributes { get; } = new();
}

public sealed class ParamDecl : CsNode
{
    public string TypeName { get; set; } = "float";
    public string Name { get; set; } = "";
    public bool IsOut { get; set; }
    public bool IsRef { get; set; }
    public bool IsParams { get; set; }
    public bool IsArray { get; set; }
}

public abstract class Stmt : CsNode { }
public sealed class BlockStmt : Stmt { public List<Stmt> Statements { get; } = new(); }
public sealed class EmptyStmt : Stmt { }
public sealed class LocalDeclStmt : Stmt
{
    public string TypeName { get; set; } = "var";
    public bool IsArray { get; set; }
    public List<(string Name, Expr? Init, int Line, int Col)> Declarators { get; } = new();
}
public sealed class ExprStmt : Stmt { public Expr Expr { get; set; } = null!; }
public sealed class IfStmt : Stmt { public Expr Cond { get; set; } = null!; public Stmt Then { get; set; } = null!; public Stmt? Else { get; set; } }
public sealed class WhileStmt : Stmt { public Expr Cond { get; set; } = null!; public Stmt Body { get; set; } = null!; }
public sealed class DoStmt : Stmt { public Stmt Body { get; set; } = null!; public Expr Cond { get; set; } = null!; }
public sealed class ForStmt : Stmt
{
    public Stmt? Init { get; set; }
    public Expr? Cond { get; set; }
    public List<Expr> Increment { get; } = new();
    public Stmt Body { get; set; } = null!;
}
public sealed class ReturnStmt : Stmt { public Expr? Expr { get; set; } }
public sealed class BreakStmt : Stmt { }
public sealed class ContinueStmt : Stmt { }
public sealed class SwitchStmt : Stmt
{
    public Expr Expr { get; set; } = null!;
    public List<SwitchCase> Cases { get; } = new();
}
public sealed class SwitchCase : CsNode
{
    public Expr? Label { get; set; }
    public bool IsDefault { get; set; }
    public List<Stmt> Statements { get; } = new();
}
public sealed class ForEachStmt : Stmt
{
    public string TypeName { get; set; } = "var";
    public string Name { get; set; } = "";
    public Expr Collection { get; set; } = null!;
    public Stmt Body { get; set; } = null!;
}
public sealed class TryStmt : Stmt
{
    public BlockStmt Body { get; set; } = null!;
    public List<CatchClause> Catches { get; } = new();
    public BlockStmt? Finally { get; set; }
}
public sealed class CatchClause : CsNode
{
    public string TypeName { get; set; } = "Exception";
    public string Name { get; set; } = "";
    public BlockStmt Body { get; set; } = null!;
}
public sealed class ThrowStmt : Stmt { public Expr? Expr { get; set; } }
public sealed class UsingStmt : Stmt
{
    public LocalDeclStmt? Declaration { get; set; }
    public Expr? Resource { get; set; }
    public Stmt Body { get; set; } = null!;
}
public sealed class LockStmt : Stmt { public Expr Lock { get; set; } = null!; public Stmt Body { get; set; } = null!; }
public sealed class GotoStmt : Stmt { public string Label { get; set; } = ""; }
public sealed class LabeledStmt : Stmt { public string Label { get; set; } = ""; public Stmt Inner { get; set; } = null!; }
public sealed class YieldStmt : Stmt { public bool IsBreak { get; set; } public Expr? Value { get; set; } }

public abstract class Expr : CsNode { }
public sealed class LiteralExpr : Expr { public CsType Type { get; set; } = CsType.Float; public double Number { get; set; } public string Value { get; set; } = ""; public bool IsNull { get; set; } public char? Char { get; set; } }
public sealed class InterpolatedStringExpr : Expr { public List<InterpolatedPart> Parts { get; } = new(); }
public sealed class InterpolatedPart
{
    public string? Text { get; set; }
    public Expr? Value { get; set; }
    public string? Format { get; set; }
}
public sealed class NameExpr : Expr { public string Name { get; set; } = ""; }
public sealed class MemberExpr : Expr { public Expr Target { get; set; } = null!; public string Name { get; set; } = ""; }
public sealed class CallExpr : Expr
{
    public Expr Target { get; set; } = null!;
    public List<Expr> Args { get; } = new();
    public List<string> GenericArgs { get; } = new();
    public HashSet<int> OutArgs { get; } = new();
}
public sealed class BinaryExpr : Expr { public string Op { get; set; } = ""; public Expr Left { get; set; } = null!; public Expr Right { get; set; } = null!; }
public sealed class UnaryExpr : Expr { public string Op { get; set; } = ""; public Expr Operand { get; set; } = null!; public bool Prefix { get; set; } = true; }
public sealed class AssignmentExpr : Expr { public string Op { get; set; } = "="; public Expr Target { get; set; } = null!; public Expr Value { get; set; } = null!; }
public sealed class ConditionalExpr : Expr { public Expr Cond { get; set; } = null!; public Expr True { get; set; } = null!; public Expr False { get; set; } = null!; }
public sealed class CastExpr : Expr { public string TypeName { get; set; } = "float"; public Expr Operand { get; set; } = null!; }
public sealed class NewExpr : Expr
{
    public string TypeName { get; set; } = "";
    public bool IsArray { get; set; }
    public Expr? Size { get; set; }
    public List<Expr> Args { get; } = new();
    public List<Expr> Initializers { get; } = new();
    public List<(string Name, Expr Value)> MemberInit { get; } = new();
}
public sealed class ThisExpr : Expr { }
public sealed class BaseExpr : Expr { }
public sealed class AwaitExpr : Expr { public Expr Operand { get; set; } = null!; }
public sealed class ParenthesizedExpr : Expr { public Expr Inner { get; set; } = null!; }
public sealed class IndexExpr : Expr { public Expr Target { get; set; } = null!; public Expr Index { get; set; } = null!; }

public sealed class CsParser
{
    private readonly List<CsToken> _tokens;
    private int _i;
    private readonly List<Diagnostic> _diags;

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public","private","protected","internal","static","readonly","const","sealed","partial","virtual","override","abstract","async","unsafe","extern","new"
    };

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal)
    {
        "float","double","int","uint","long","ulong","short","ushort","byte","sbyte","bool","string","char","object","void","var"
    };

    public CsParser(string source, List<Diagnostic> diags)
    {
        _tokens = new CsLexer(source, diags).Lex();
        _diags = diags;
    }

    private CsToken Cur => _tokens[Math.Min(_i, _tokens.Count - 1)];
    public bool AtEnd => Cur.Kind == CsTokKind.Eof;
    private CsToken Peek(int n = 1) => _tokens[Math.Min(_i + n, _tokens.Count - 1)];
    private bool Is(string text) => Cur.Text == text;
    private bool IsIdent => Cur.Kind == CsTokKind.Ident;
    private CsToken Take() => _tokens[Math.Min(_i++, _tokens.Count - 1)];
    private bool Accept(string text) { if (Is(text)) { _i++; return true; } return false; }
    private void Expect(string text)
    {
        if (!Accept(text)) Error($"Expected '{text}' but found '{Cur.Text}'");
    }
    private void Error(string msg) => _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = msg, Line = Cur.Line, Column = Cur.Col, Code = "CS1002" });
    private void Warn(string msg) => _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Warning, Message = msg, Line = Cur.Line, Column = Cur.Col, Code = "CS0168" });

    public CompilationUnit Parse()
    {
        var unit = new CompilationUnit();
        while (Cur.Kind != CsTokKind.Eof)
        {
            if (Is("using"))
            {
                Take();
                var sb = new StringBuilder();
                while (Cur.Kind != CsTokKind.Eof && !Is(";")) sb.Append(Take().Text);
                Accept(";");
                unit.Usings.Add(sb.ToString());
            }
            else if (Is("namespace"))
            {
                Take();
                while (Cur.Kind != CsTokKind.Eof && !Is("{") && !Is(";")) Take();
                if (Accept("{"))
                {
                    while (Cur.Kind != CsTokKind.Eof && !Is("}"))
                    {
                        if (Is("class") || Is("struct") || Is("record") || Is("interface") || Is("enum")) unit.Classes.Add(ParseClass());
                        else if (TrySkipAttribute()) { }
                        else if (Modifiers.Contains(Cur.Text)) Take();
                        else Error("Only class declarations are supported inside a namespace"); Take();
                    }
                    Accept("}");
                }
            }
            else if (Is("class") || Is("struct") || Is("record") || Is("interface") || Is("enum"))
            {
                unit.Classes.Add(ParseClass());
            }
            else if (Is("["))
            {
                ParseAttributes();
            }
            else if (Modifiers.Contains(Cur.Text))
            {
                Take();
            }
            else
            {
                unit.TopLevel.Add(ParseStatement());
            }
        }
        return unit;
    }

    private bool TrySkipAttribute()
    {
        if (!Is("[")) return false;
        ParseAttributes();
        return true;
    }

    /// <summary>
    /// Reads one or more [Attribute] clauses and returns their contents without the
    /// brackets. Attributes are metadata for the inspector; the compiler itself still
    /// treats them as no-ops, exactly like the real C# runtime would for these markers.
    /// </summary>
    private List<string> ParseAttributes()
    {
        var result = new List<string>();
        while (Is("["))
        {
            Take();
            int brackets = 1, parens = 0;
            var current = new StringBuilder();
            while (Cur.Kind != CsTokKind.Eof && brackets > 0)
            {
                if (Is("[")) { brackets++; current.Append('['); Take(); continue; }
                if (Is("]"))
                {
                    brackets--;
                    if (brackets == 0) { Take(); break; }
                    current.Append(']'); Take(); continue;
                }
                if (Is("(")) parens++;
                if (Is(")")) parens = Math.Max(0, parens - 1);
                if (Is(",") && parens == 0)
                {
                    if (current.Length > 0) result.Add(current.ToString().Trim());
                    current.Clear();
                    Take();
                    continue;
                }
                current.Append(Cur.Text);
                Take();
            }
            if (current.Length > 0) result.Add(current.ToString().Trim());
        }
        return result;
    }

    private ClassDecl ParseClass()
    {
        var cls = new ClassDecl { Line = Cur.Line, Col = Cur.Col };
        bool isEnum = Is("enum");
        if (Is("interface")) cls.Kind = TypeDeclKind.Interface;
        else if (Is("struct")) cls.Kind = TypeDeclKind.Struct;
        else if (Is("record")) cls.Kind = TypeDeclKind.Class;
        Accept("class"); Accept("struct"); Accept("record"); Accept("interface"); Accept("enum");
        if (!IsIdent) { Error("Expected class name"); return cls; }
        cls.Name = Take().Text;
        if (isEnum)
        {
            if (Accept(":")) ParseTypeName();
            Expect("{");
            int value = 0;
            while (Cur.Kind != CsTokKind.Eof && !Is("}"))
            {
                if (!IsIdent) { Take(); continue; }
                var member = Take();
                Expr? init = null;
                if (Accept("=")) { init = ParseExpression(); if (init is LiteralExpr lit) value = (int)lit.Number; }
                cls.Fields.Add(new FieldDecl { TypeName = "int", Name = member.Text, IsStatic = true, IsConst = true, Line = member.Line, Col = member.Col, Initializer = init ?? new LiteralExpr { Type = CsType.Float, Number = value } });
                value++;
                Accept(",");
            }
            Expect("}");
            Accept(";");
            return cls;
        }
        if (Is("<"))
        {
            Take();
            while (!Is(">") && Cur.Kind != CsTokKind.Eof)
            {
                if (IsIdent) cls.GenericParams.Add(Take().Text);
                else Take();
                if (!Accept(",")) break;
            }
            Accept(">");
        }
        if (Accept(":"))
        {
            while (Cur.Kind != CsTokKind.Eof)
            {
                var name = ParseTypeName();
                if (cls.BaseName.Length == 0) cls.BaseName = name;
                cls.Interfaces.Add(name);
                if (!Accept(",")) break;
            }
        }
        while (Is("where"))
        {
            while (Cur.Kind != CsTokKind.Eof && !Is("{") && !Is(";")) Take();
        }
        Expect("{");
        while (Cur.Kind != CsTokKind.Eof && !Is("}"))
        {
            var attributes = ParseAttributes();
            bool isStatic = false, isConst = false, isVirtual = false, isOverride = false, isAbstract = false, isAsync = false, isReadonly = false;
            string access = "private";
            while (Modifiers.Contains(Cur.Text))
            {
                if (Is("static")) isStatic = true;
                if (Is("const")) isConst = true;
                if (Is("readonly")) isReadonly = true;
                if (Is("virtual")) isVirtual = true;
                if (Is("override")) isOverride = true;
                if (Is("abstract")) isAbstract = true;
                if (Is("async")) isAsync = true;
                if (Is("public") || Is("private") || Is("protected") || Is("internal")) access = Cur.Text;
                Take();
            }
            if (Is("}")) break;
            if (Is("delegate") || Is("event")) { SkipToMemberEnd(); continue; }
            if (Is("~")) { SkipToMemberEnd(); continue; }
            if (Is("operator")) { SkipToMemberEnd(); continue; }
            if (Is("this") && Peek().Text == "[") { SkipToMemberEnd(); continue; }
            if (isAbstract && (Is("class") || Is("struct"))) { cls.IsAbstract = true; SkipToMemberEnd(); continue; }
            // constructor: ClassName(...)
            if (IsIdent && Cur.Text == cls.Name && Peek().Text == "(")
            {
                var ctorTok = Take();
                var ctor = new MethodDecl { Name = cls.Name, ReturnTypeName = cls.Name, IsStatic = isStatic, IsConstructor = true, Access = access, Line = ctorTok.Line, Col = ctorTok.Col };
                ctor.Attributes.AddRange(attributes);
                ParseParams(ctor);
                if (Is(":")) { while (!Is("{") && !Is(";") && Cur.Kind != CsTokKind.Eof) Take(); }
                if (Is("{")) ctor.Body = ParseBlock(); else Accept(";");
                cls.Methods.Add(ctor);
                continue;
            }
            var typeRef = ParseType();
            string type = typeRef?.Name ?? "";
            bool isArray = typeRef?.IsArray ?? false;
            if (string.IsNullOrEmpty(type)) { SkipToMemberEnd(); continue; }
            if (!IsIdent) { Error("Expected member name"); SkipToMemberEnd(); continue; }
            var nameTok = Take();
            if (Is("<"))
            {
                // generic method: parse type parameters, then the parameter list
                var generic = new MethodDecl { ReturnTypeName = type, Name = nameTok.Text, Line = nameTok.Line, Col = nameTok.Col, IsStatic = isStatic, IsVirtual = isVirtual, IsOverride = isOverride, IsAbstract = isAbstract, IsAsync = isAsync, Access = access };
                generic.Attributes.AddRange(attributes);
                Take();
                while (!Is(">") && Cur.Kind != CsTokKind.Eof)
                {
                    if (IsIdent) generic.GenericParams.Add(Take().Text);
                    else Take();
                    if (!Accept(",")) break;
                }
                Accept(">");
                ParseParams(generic);
                while (Is("where")) { while (Cur.Kind != CsTokKind.Eof && !Is("{") && !Is(";") && !Is("=>")) Take(); }
                if (Accept("=>")) { generic.ExpressionBody = ParseExpression(); Expect(";"); }
                else if (Is("{")) generic.Body = ParseBlock();
                else if (Is(";")) Take();
                else Error("Expected method body");
                cls.Methods.Add(generic);
                continue;
            }
            if (Is("("))
            {
                var m = new MethodDecl { ReturnTypeName = type, Name = nameTok.Text, Line = nameTok.Line, Col = nameTok.Col, IsStatic = isStatic, IsVirtual = isVirtual, IsOverride = isOverride, IsAbstract = isAbstract, IsAsync = isAsync, Access = access };
                m.Attributes.AddRange(attributes);
                ParseParams(m);
                while (Is("where")) { while (Cur.Kind != CsTokKind.Eof && !Is("{") && !Is(";") && !Is("=>")) Take(); }
                if (Accept("=>"))
                {
                    m.ExpressionBody = ParseExpression();
                    Expect(";");
                }
                else if (Is("{")) m.Body = ParseBlock();
                else if (Is(";")) Take();
                else Error("Expected method body");
                if (cls.Kind == TypeDeclKind.Interface) m.IsAbstract = true;
                cls.Methods.Add(m);
            }
            else if (Is("{"))
            {
                // Auto-property or property with accessors. Treat auto-properties as fields.
                int brace = 0;
                bool accessorBody = false;
                Expr? init = null;
                do
                {
                    if (Is("{")) { brace++; if (brace > 1) accessorBody = true; }
                    if (Is("}")) brace--;
                    Take();
                } while (Cur.Kind != CsTokKind.Eof && brace > 0);
                if (accessorBody)
                {
                    _diags.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Message = $"Property '{nameTok.Text}' has an accessor body; GRAB ENGINE treats properties as plain fields, so the body is ignored.",
                        Line = nameTok.Line, Column = nameTok.Col, File = "Script.cs"
                    });
                }
                if (Accept("=")) { init = ParseExpression(); Expect(";"); }
                else Accept(";");
                var accessorField = new FieldDecl { TypeName = type, IsArray = isArray, Name = nameTok.Text, Initializer = init, IsStatic = isStatic, IsReadonly = isReadonly, Access = access, Line = nameTok.Line, Col = nameTok.Col };
                accessorField.Attributes.AddRange(attributes);
                cls.Fields.Add(accessorField);
            }
            else
            {
                Expr? init = null;
                if (Accept("=")) init = ParseExpression();
                var field = new FieldDecl { TypeName = type, IsArray = isArray, Name = nameTok.Text, Initializer = init, IsConst = isConst, IsStatic = isStatic, IsReadonly = isReadonly, Access = access, Line = nameTok.Line, Col = nameTok.Col };
                field.Attributes.AddRange(attributes);
                cls.Fields.Add(field);
                while (Accept(","))
                {
                    if (!IsIdent) { Error("Expected field name"); break; }
                    var nt = Take();
                    Expr? init2 = null;
                    if (Accept("=")) init2 = ParseExpression();
                    var extra = new FieldDecl { TypeName = type, IsArray = isArray, Name = nt.Text, Initializer = init2, IsConst = isConst, IsStatic = isStatic, IsReadonly = isReadonly, Access = access, Line = nt.Line, Col = nt.Col };
                    extra.Attributes.AddRange(attributes);
                    cls.Fields.Add(extra);
                }
                Expect(";");
            }
        }
        Accept("}");
        return cls;
    }

    private void SkipToMemberEnd()
    {
        int depth = 0;
        while (Cur.Kind != CsTokKind.Eof)
        {
            if (Is("{")) depth++;
            else if (Is("}")) { if (depth == 0) break; depth--; }
            else if (Is(";") && depth == 0) { Take(); break; }
            Take();
        }
    }

    private void ParseParams(MethodDecl m)
    {
        Expect("(");
        while (Cur.Kind != CsTokKind.Eof && !Is(")"))
        {
            bool isOut = false, isRef = false, isParams = false;
            while (Is("ref") || Is("out") || Is("in") || Is("params") || Is("this"))
            {
                if (Is("out")) isOut = true;
                if (Is("ref")) isRef = true;
                if (Is("params")) isParams = true;
                Take();
            }
            var typeRef = ParseType();
            var type = typeRef?.Name ?? "";
            if (!IsIdent) { Error("Expected parameter name"); break; }
            var nameTok = Take();
            m.Params.Add(new ParamDecl
            {
                TypeName = type, Name = nameTok.Text, IsOut = isOut, IsRef = isRef,
                IsParams = isParams, IsArray = typeRef?.IsArray ?? false,
                Line = nameTok.Line, Col = nameTok.Col
            });
            if (!Accept(",")) break;
        }
        Expect(")");
    }

    private string ParseTypeName()
    {
        var t = ParseType();
        return t?.Name ?? "";
    }

    private TypeRef? ParseType()
    {
        if (Cur.Kind == CsTokKind.Eof) return null;
        if (Cur.Kind != CsTokKind.Ident && Cur.Kind != CsTokKind.Keyword) return null;
        var tr = new TypeRef { Name = Take().Text, Line = Cur.Line, Col = Cur.Col };
        while (Accept("."))
        {
            if (!IsIdent) break;
            tr.Name += "." + Take().Text;
        }
        if (Is("<"))
        {
            int save = _i;
            Take();
            var args = new List<TypeRef>();
            bool ok = true;
            while (!Is(">") && Cur.Kind != CsTokKind.Eof)
            {
                var a = ParseType();
                if (a == null) { ok = false; break; }
                args.Add(a);
                if (!Accept(",")) break;
            }
            if (ok && Accept(">"))
            {
                tr.Args = args;
                tr.Name += "<" + string.Join(",", args.Select(a => a.Name)) + ">";
            }
            else { _i = save; }
        }
        // Only consume "[]" here; "new float[4]" keeps the size for ParsePrimary.
        if (Is("[") && Peek(1).Text == "]") { Take(); Take(); tr.IsArray = true; }
        return tr;
    }

    private BlockStmt ParseBlock()
    {
        var b = new BlockStmt { Line = Cur.Line, Col = Cur.Col };
        Expect("{");
        while (Cur.Kind != CsTokKind.Eof && !Is("}")) b.Statements.Add(ParseStatement());
        Expect("}");
        return b;
    }

    public Stmt ParseStatement()
    {
        var line = Cur.Line;
        var col = Cur.Col;
        if (Is("{")) return ParseBlock();
        if (Accept(";")) return new EmptyStmt { Line = line, Col = col };
        if (Accept("if"))
        {
            Expect("(");
            var cond = ParseExpression();
            Expect(")");
            var then = ParseStatement();
            Stmt? els = null;
            if (Accept("else")) els = ParseStatement();
            return new IfStmt { Cond = cond, Then = then, Else = els, Line = line, Col = col };
        }
        if (Accept("while"))
        {
            Expect("(");
            var cond = ParseExpression();
            Expect(")");
            return new WhileStmt { Cond = cond, Body = ParseStatement(), Line = line, Col = col };
        }
        if (Accept("do"))
        {
            var body = ParseStatement();
            Expect("while");
            Expect("(");
            var cond = ParseExpression();
            Expect(")");
            Expect(";");
            return new DoStmt { Body = body, Cond = cond, Line = line, Col = col };
        }
        if (Accept("for"))
        {
            Expect("(");
            var f = new ForStmt { Line = line, Col = col };
            if (!Is(";"))
            {
                int save = _i;
                if (TryParseLocalDecl(out var decl, consumeSemicolon: false)) f.Init = decl;
                else
                {
                    _i = save;
                    var e = ParseExpression();
                    f.Init = new ExprStmt { Expr = e, Line = e.Line, Col = e.Col };
                    while (Accept(",")) f.Increment.Add(ParseExpression());
                }
            }
            Expect(";");
            if (!Is(";")) f.Cond = ParseExpression();
            Expect(";");
            if (!Is(")"))
            {
                f.Increment.Add(ParseExpression());
                while (Accept(",")) f.Increment.Add(ParseExpression());
            }
            Expect(")");
            f.Body = ParseStatement();
            return f;
        }
        if (Accept("switch"))
        {
            Expect("(");
            var e = ParseExpression();
            Expect(")");
            Expect("{");
            var sw = new SwitchStmt { Expr = e, Line = line, Col = col };
            while (Cur.Kind != CsTokKind.Eof && !Is("}"))
            {
                if (Accept("case"))
                {
                    var c = new SwitchCase { Line = Cur.Line, Col = Cur.Col };
                    c.Label = ParseExpression();
                    Expect(":");
                    while (!Is("case") && !Is("default") && !Is("}") && Cur.Kind != CsTokKind.Eof) c.Statements.Add(ParseStatement());
                    sw.Cases.Add(c);
                }
                else if (Accept("default"))
                {
                    Expect(":");
                    var c = new SwitchCase { IsDefault = true, Line = Cur.Line, Col = Cur.Col };
                    while (!Is("case") && !Is("default") && !Is("}") && Cur.Kind != CsTokKind.Eof) c.Statements.Add(ParseStatement());
                    sw.Cases.Add(c);
                }
                else { Error("Expected case or default"); Take(); }
            }
            Expect("}");
            return sw;
        }
        if (Accept("return"))
        {
            Expr? e = null;
            if (!Is(";")) e = ParseExpression();
            Expect(";");
            return new ReturnStmt { Expr = e, Line = line, Col = col };
        }
        if (Accept("break")) { Expect(";"); return new BreakStmt { Line = line, Col = col }; }
        if (Accept("continue")) { Expect(";"); return new ContinueStmt { Line = line, Col = col }; }
        if ((Cur.Kind == CsTokKind.Keyword && TypeKeywords.Contains(Cur.Text)) ||
            Is("Vector3") || Is("Color") || Is("GameObject") || Is("Trigger") || Is("Light") ||
            Is("Sign") || Is("PhysicsBlock") || Is("ParticleEmitter") || Is("Sound"))
        {
            if (TryParseLocalDecl(out var d)) return d;
        }
        else if (Cur.Kind == CsTokKind.Ident && !Is("goto") && !Is("throw") && !Is("try") && !Is("foreach") && !Is("lock") && !Is("using"))
        {
            int save = _i;
            var type = ParseType();
            if (type != null && IsIdent && (Peek().Text == "=" || Peek().Text == ";" || Peek().Text == ","))
            {
                _i = save;
                if (TryParseLocalDecl(out var d)) return d;
            }
            _i = save;
        }
        if (Accept("foreach"))
        {
            Expect("(");
            var fe = new ForEachStmt { Line = line, Col = col };
            if (Is("var")) { Take(); }
            else fe.TypeName = ParseTypeName();
            if (!IsIdent) Error("Expected foreach variable name");
            else fe.Name = Take().Text;
            Expect("in");
            fe.Collection = ParseExpression();
            Expect(")");
            fe.Body = ParseStatement();
            return fe;
        }
        if (Accept("try"))
        {
            var ts = new TryStmt { Line = line, Col = col };
            if (!Is("{")) { Error("Expected a block after try"); return ts; }
            ts.Body = ParseBlock();
            while (Is("catch"))
            {
                var ct = Take();
                var clause = new CatchClause { Line = ct.Line, Col = ct.Col };
                if (Accept("("))
                {
                    while (Is("ref") || Is("out")) Take();
                    clause.TypeName = ParseTypeName();
                    if (IsIdent) clause.Name = Take().Text;
                    while (!Is(")") && Cur.Kind != CsTokKind.Eof) Take();
                    Expect(")");
                }
                if (Accept("when"))
                {
                    if (Accept("(")) { while (!Is(")") && Cur.Kind != CsTokKind.Eof) Take(); Expect(")"); }
                    else Take();
                }
                if (Is("{")) clause.Body = ParseBlock(); else { Error("Expected a catch block"); }
                ts.Catches.Add(clause);
            }
            if (Accept("finally"))
            {
                if (Is("{")) ts.Finally = ParseBlock(); else Error("Expected a finally block");
            }
            if (ts.Catches.Count == 0 && ts.Finally == null) Error("try needs a catch or finally block");
            return ts;
        }
        if (Accept("throw"))
        {
            Expr? value = null;
            if (!Is(";")) value = ParseExpression();
            Expect(";");
            return new ThrowStmt { Expr = value, Line = line, Col = col };
        }
        if (Accept("using"))
        {
            if (Accept("("))
            {
                UsingStmt us;
                int save = _i;
                if (TryParseLocalDecl(out var decl, consumeSemicolon: false))
                {
                    us = new UsingStmt { Declaration = decl, Line = line, Col = col };
                }
                else
                {
                    _i = save;
                    us = new UsingStmt { Resource = ParseExpression(), Line = line, Col = col };
                }
                Expect(")");
                us.Body = ParseStatement();
                return us;
            }
            if (TryParseLocalDecl(out var usingDecl))
                return new UsingStmt { Declaration = usingDecl, Body = new EmptyStmt { Line = line, Col = col }, Line = line, Col = col };
            Error("Expected '(' or a local declaration after using");
            SkipStatement();
            return new EmptyStmt { Line = line, Col = col };
        }
        if (Accept("lock"))
        {
            Expect("(");
            var target = ParseExpression();
            Expect(")");
            return new LockStmt { Lock = target, Body = ParseStatement(), Line = line, Col = col };
        }
        if (Accept("goto"))
        {
            if (Is("case") || Is("default")) { SkipStatement(); return new EmptyStmt { Line = line, Col = col }; }
            string label = IsIdent ? Take().Text : "";
            Expect(";");
            return new GotoStmt { Label = label, Line = line, Col = col };
        }
        if (Accept("yield"))
        {
            if (Accept("break")) { Expect(";"); return new YieldStmt { IsBreak = true, Line = line, Col = col }; }
            Expect("return");
            Expr? value = null;
            if (!Is(";")) value = ParseExpression();
            Expect(";");
            return new YieldStmt { Value = value, Line = line, Col = col };
        }
        if (Cur.Kind == CsTokKind.Ident && Peek().Text == ":")
        {
            var labelTok = Take();
            Take();
            return new LabeledStmt { Label = labelTok.Text, Inner = ParseStatement(), Line = labelTok.Line, Col = labelTok.Col };
        }
        if (Is("checked") || Is("unchecked"))
        {
            Take();
            if (Accept("(")) { var inner = ParseExpression(); Expect(")"); Expect(";"); return new ExprStmt { Expr = inner, Line = line, Col = col }; }
            return ParseStatement();
        }
        if (Is("try") || Is("foreach") || Is("throw"))
        {
            // Unreachable, but keeps the parser from looping on a malformed file.
            Error($"'{Cur.Text}' is not supported here");
            SkipStatement();
            return new EmptyStmt { Line = line, Col = col };
        }
        var expr = ParseExpression();
        Expect(";");
        return new ExprStmt { Expr = expr, Line = line, Col = col };
    }

    private void SkipStatement()
    {
        int depth = 0;
        while (Cur.Kind != CsTokKind.Eof)
        {
            if (Is("{")) depth++;
            else if (Is("}")) { if (depth == 0) return; depth--; }
            else if (Is(";") && depth == 0) { Take(); return; }
            Take();
        }
    }

    private bool TryParseLocalDecl(out LocalDeclStmt decl, bool consumeSemicolon = true)
    {
        decl = null!;
        int save = _i;
        var type = ParseType();
        if (type == null || !IsIdent)
        {
            _i = save;
            return false;
        }
        var d = new LocalDeclStmt { TypeName = type.Name, IsArray = type.IsArray, Line = Cur.Line, Col = Cur.Col };
        while (true)
        {
            if (!IsIdent) { _i = save; return false; }
            var nt = Take();
            Expr? init = null;
            if (Accept("=")) init = ParseExpression();
            d.Declarators.Add((nt.Text, init, nt.Line, nt.Col));
            if (!Accept(",")) break;
        }
        if (!Is(";")) { _i = save; return false; }
        if (consumeSemicolon) Take();
        decl = d;
        return true;
    }

    public Expr ParseExpression() => ParseAssignment();

    private Expr ParseAssignment()
    {
        var left = ParseConditional();
        if (Is("=") || Is("+=") || Is("-=") || Is("*=") || Is("/=") || Is("%=") || Is("&=") || Is("|="))
        {
            var op = Take();
            var right = ParseAssignment();
            return new AssignmentExpr { Op = op.Text, Target = left, Value = right, Line = left.Line, Col = left.Col };
        }
        return left;
    }

    private Expr ParseConditional()
    {
        var cond = ParseBinary(0);
        if (Accept("?"))
        {
            var t = ParseExpression();
            Expect(":");
            var f = ParseExpression();
            return new ConditionalExpr { Cond = cond, True = t, False = f, Line = cond.Line, Col = cond.Col };
        }
        return cond;
    }

    private static readonly string[][] Levels =
    {
        new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "&" },
        new[] { "==", "!=" }, new[] { "<", ">", "<=", ">=" },
        new[] { "+", "-" }, new[] { "*", "/", "%" }
    };

    private Expr ParseBinary(int level)
    {
        if (level >= Levels.Length) return ParseUnary();
        var left = ParseBinary(level + 1);
        while (Levels[level].Contains(Cur.Text))
        {
            var op = Take();
            var right = ParseBinary(level + 1);
            left = new BinaryExpr { Op = op.Text, Left = left, Right = right, Line = left.Line, Col = left.Col };
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Is("await"))
        {
            var aw = Take();
            return new AwaitExpr { Operand = ParseUnary(), Line = aw.Line, Col = aw.Col };
        }
        if (Is("!") || Is("-") || Is("+") || Is("~") || Is("++") || Is("--"))
        {
            var op = Take();
            var operand = ParseUnary();
            if (op.Text == "~") Error("Bitwise complement is not supported");
            return new UnaryExpr { Op = op.Text, Operand = operand, Prefix = true, Line = op.Line, Col = op.Col };
        }
        if (Is("(") && IsCastAhead())
        {
            var lp = Take();
            var type = ParseTypeName();
            Expect(")");
            var operand = ParseUnary();
            return new CastExpr { TypeName = type, Operand = operand, Line = lp.Line, Col = lp.Col };
        }
        if (Is("(") && Peek().Text == "(")
        {
            // lambda - not supported
            Error("Lambdas are not supported by GRAB ENGINE's GASM compiler");
            Take(); Take();
            return new LiteralExpr { Type = CsType.Float, Number = 0, Line = Cur.Line, Col = Cur.Col };
        }
        return ParsePostfix();
    }

    private bool IsCastAhead()
    {
        int save = _i;
        Take(); // (
        var type = ParseType();
        bool ok = false;
        if (type != null && Is(")"))
        {
            Take();
            if (TypeKeywords.Contains(type.Name) || type.Name is "Vector3" or "Color" or "GameObject" or "Trigger" or "Light" or "Sign" or "PhysicsBlock" or "Player" or "Level" || char.IsUpper(type.Name.FirstOrDefault()))
            {
                var next = Cur.Text;
                ok = Cur.Kind is CsTokKind.Ident or CsTokKind.Number or CsTokKind.String or CsTokKind.Char || next is "(" or "!" or "-";
            }
        }
        _i = save;
        return ok;
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (Accept("."))
            {
                if (!IsIdent) { Error("Expected member name"); break; }
                e = new MemberExpr { Target = e, Name = Take().Text, Line = e.Line, Col = e.Col };
            }
            else if (Is("("))
            {
                Take();
                var call = new CallExpr { Target = e, Line = e.Line, Col = e.Col };
                while (Cur.Kind != CsTokKind.Eof && !Is(")"))
                {
                    if (Accept("out") || Accept("ref")) call.OutArgs.Add(call.Args.Count);
                    if (IsIdent && Peek().Text == ":") { Take(); Take(); }
                    call.Args.Add(ParseExpression());
                    if (!Accept(",")) break;
                }
                Expect(")");
                e = call;
            }
            else if (Is("<") && e is NameExpr or MemberExpr)
            {
                int save = _i;
                Take();
                var args = new List<string>();
                bool ok = true;
                while (!Is(">") && Cur.Kind != CsTokKind.Eof)
                {
                    var t = ParseType();
                    if (t == null) { ok = false; break; }
                    args.Add(t.Name);
                    if (!Accept(",")) break;
                }
                if (ok && Accept(">") && Is("("))
                {
                    Take();
                    var call = new CallExpr { Target = e, Line = e.Line, Col = e.Col };
                    call.GenericArgs.AddRange(args);
                    while (Cur.Kind != CsTokKind.Eof && !Is(")"))
                    {
                        if (Accept("out") || Accept("ref")) call.OutArgs.Add(call.Args.Count);
                        if (IsIdent && Peek().Text == ":") { Take(); Take(); }
                        call.Args.Add(ParseExpression());
                        if (!Accept(",")) break;
                    }
                    Expect(")");
                    e = call;
                }
                else { _i = save; break; }
            }
            else if (Accept("["))
            {
                var idx = ParseExpression();
                Expect("]");
                e = new IndexExpr { Target = e, Index = idx, Line = e.Line, Col = e.Col };
            }
            else if (Is("++") || Is("--"))
            {
                var op = Take();
                e = new UnaryExpr { Op = op.Text, Operand = e, Prefix = false, Line = e.Line, Col = e.Col };
            }
            else break;
        }
        return e;
    }

    private Expr ParsePrimary()
    {
        var t = Cur;
        if (t.Kind == CsTokKind.Number) { Take(); return new LiteralExpr { Type = CsType.Float, Number = t.Number, Value = t.Text, Line = t.Line, Col = t.Col }; }
        if (t.Kind == CsTokKind.String)
        {
            Take();
            return t.IsInterpolated ? ParseInterpolatedString(t) : new LiteralExpr { Type = CsType.String, Value = t.Value, Line = t.Line, Col = t.Col };
        }
        if (t.Kind == CsTokKind.Char) { Take(); return new LiteralExpr { Type = CsType.Float, Number = t.Value.Length > 0 ? t.Value[0] : 0, Char = t.Value.Length > 0 ? t.Value[0] : '\0', Line = t.Line, Col = t.Col }; }
        if (t.Text == "true") { Take(); return new LiteralExpr { Type = CsType.Bool, Number = 1, Line = t.Line, Col = t.Col }; }
        if (t.Text == "false") { Take(); return new LiteralExpr { Type = CsType.Bool, Number = 0, Line = t.Line, Col = t.Col }; }
        if (t.Text == "null") { Take(); return new LiteralExpr { Type = CsType.GameObject, IsNull = true, Line = t.Line, Col = t.Col }; }
        if (t.Text == "this") { Take(); return new ThisExpr { Line = t.Line, Col = t.Col }; }
        if (t.Text == "new")
        {
            Take();
            TypeRef? typeRef = null;
            if (Is("["))
            {
                Take();
                Expect("]");
                var neArr = new NewExpr { IsArray = true, Line = t.Line, Col = t.Col };
                if (Is("{"))
                {
                    Take();
                    while (Cur.Kind != CsTokKind.Eof && !Is("}"))
                    {
                        neArr.Initializers.Add(ParseExpression());
                        if (!Accept(",")) break;
                    }
                    Expect("}");
                }
                else if (Accept("[")) { neArr.Size = ParseExpression(); Expect("]"); }
                return neArr;
            }
            typeRef = ParseType();
            var ne = new NewExpr { TypeName = typeRef?.Name ?? "", IsArray = typeRef?.IsArray ?? false, Line = t.Line, Col = t.Col };
            // new float[4] — the size is a normal expression so constants work.
            if (Accept("["))
            {
                ne.IsArray = true;
                if (!Is("]")) ne.Size = ParseExpression();
                Expect("]");
            }
            if (ne.IsArray && Accept("{"))
            {
                // new float[] { 1, 2, 3 }
                while (Cur.Kind != CsTokKind.Eof && !Is("}"))
                {
                    ne.Initializers.Add(ParseExpression());
                    if (!Accept(",")) break;
                }
                Expect("}");
            }
            else if (Accept("("))
            {
                while (Cur.Kind != CsTokKind.Eof && !Is(")"))
                {
                    if (Accept("out") || Accept("ref")) { }
                    if (IsIdent && Peek().Text == ":") { Take(); Take(); }
                    ne.Args.Add(ParseExpression());
                    if (!Accept(",")) break;
                }
                Expect(")");
                if (Is("{")) ParseMemberInitializer(ne);
            }
            else if (Is("{")) ParseMemberInitializer(ne);
            return ne;
        }
        if (t.Text == "(")
        {
            Take();
            var inner = ParseExpression();
            Expect(")");
            return new ParenthesizedExpr { Inner = inner, Line = t.Line, Col = t.Col };
        }
        if (t.Text == "nameof")
        {
            Take();
            Expect("(");
            var inner = ParseExpression();
            Expect(")");
            string name = inner switch
            {
                NameExpr n => n.Name,
                MemberExpr m => m.Name,
                _ => ""
            };
            if (name.Length == 0) Error("nameof needs a name, e.g. nameof(MyScript) or nameof(transform)");
            return new LiteralExpr { Type = CsType.String, Value = name, Line = t.Line, Col = t.Col };
        }
        if (t.Kind == CsTokKind.Ident || t.Kind == CsTokKind.Keyword)
        {
            Take();
            return new NameExpr { Name = t.Text, Line = t.Line, Col = t.Col };
        }
        Error($"Unexpected token '{t.Text}'");
        Take();
        return new LiteralExpr { Type = CsType.Float, Number = 0, Line = t.Line, Col = t.Col };
    }

    private Expr ParseInterpolatedString(CsToken token)
    {
        var result = new InterpolatedStringExpr { Line = token.Line, Col = token.Col };
        string raw = token.Value;
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0) return;
            result.Parts.Add(new InterpolatedPart { Text = DecodeInterpolated(literal.ToString(), token.IsVerbatim) });
            literal.Clear();
        }

        int i = 0;
        while (i < raw.Length)
        {
            char ch = raw[i];
            if (ch == '{' && i + 1 < raw.Length && raw[i + 1] == '{') { literal.Append('{'); i += 2; continue; }
            if (ch == '}' && i + 1 < raw.Length && raw[i + 1] == '}') { literal.Append('}'); i += 2; continue; }
            if (ch == '{')
            {
                Flush();
                int end = FindInterpolationEnd(raw, i + 1);
                if (end < 0)
                {
                    Error("The interpolated string has an unclosed '{'");
                    break;
                }
                string content = raw[(i + 1)..end];
                int colon = FindTopLevel(content, ':');
                string expressionText = (colon >= 0 ? content[..colon] : content).Trim();
                string? format = colon >= 0 ? content[(colon + 1)..].Trim() : null;
                int comma = FindTopLevel(expressionText, ',');
                if (comma >= 0) expressionText = expressionText[..comma].Trim();
                if (expressionText.Length == 0)
                    Error("An interpolation hole cannot be empty");
                else
                {
                    string padded = new string('\n', Math.Max(0, token.Line - 1)) + new string(' ', Math.Max(0, token.Col - 1)) + expressionText;
                    var parser = new CsParser(padded, _diags);
                    var value = parser.ParseExpression();
                    if (!parser.AtEnd) Error($"Unexpected text after interpolated expression '{expressionText}'");
                    result.Parts.Add(new InterpolatedPart { Value = value, Format = format });
                    if (format != null)
                        Warn($"Format specifier '{{{expressionText}:{format}}}' is ignored; interpolation prints the plain value");
                }
                i = end + 1;
                continue;
            }
            if (ch == '}')
            {
                Error("A single '}' in an interpolated string must be written as '}}'");
                literal.Append('}');
                i++;
                continue;
            }
            literal.Append(ch);
            i++;
        }
        Flush();
        return result;
    }

    private static int FindInterpolationEnd(string text, int start)
    {
        int depth = 1;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' || c == '\'') { i = SkipQuoted(text, i, c) - 1; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int SkipQuoted(string text, int start, char quote)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == quote) return i + 1;
        }
        return text.Length;
    }

    private static int FindTopLevel(string text, char target)
    {
        int paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' || c == '\'') { i = SkipQuoted(text, i, c) - 1; continue; }
            if (c == '(') paren++;
            else if (c == ')') paren--;
            else if (c == '[') bracket++;
            else if (c == ']') bracket--;
            else if (c == '{') brace++;
            else if (c == '}') brace--;
            else if (c == target && paren == 0 && bracket == 0 && brace == 0) return i;
        }
        return -1;
    }

    private static string DecodeInterpolated(string text, bool verbatim)
    {
        if (verbatim) return text.Replace("\"\"", "\"");
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { sb.Append(c); continue; }
            char e = text[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'u':
                case 'x':
                {
                    int value = 0, digits = 0;
                    while (digits < 4 && i + 1 < text.Length && Uri.IsHexDigit(text[i + 1]))
                    {
                        value = value * 16 + Convert.ToInt32(text[i + 1].ToString(), 16);
                        i++; digits++;
                    }
                    sb.Append((char)value);
                    break;
                }
                default: sb.Append(e); break;
            }
        }
        return sb.ToString();
    }

    private void ParseMemberInitializer(NewExpr ne)
    {
        Expect("{");
        while (Cur.Kind != CsTokKind.Eof && !Is("}"))
        {
            if (!IsIdent) { Take(); continue; }
            string name = Take().Text;
            Expect("=");
            ne.MemberInit.Add((name, ParseExpression()));
            if (!Accept(",")) break;
        }
        Expect("}");
    }

    private void SkipBrace()
    {
        int d = 0;
        do
        {
            if (Is("{")) d++;
            if (Is("}")) d--;
            Take();
        } while (Cur.Kind != CsTokKind.Eof && d > 0);
    }
}

public sealed class TypeRef
{
    public string Name { get; set; } = "";
    public List<TypeRef>? Args { get; set; }
    public bool IsArray { get; set; }
    public int Line { get; set; }
    public int Col { get; set; }
}

public sealed class CsLexer
{
    private readonly string _s;
    private readonly List<Diagnostic> _diags;
    private int _i;
    private int _line = 1, _col = 1;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
        "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
        "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock",
        "long","namespace","new","null","object","operator","out","override","params","private","protected",
        "public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string",
        "struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort",
        "using","virtual","void","volatile","while","var"
    };

    public CsLexer(string s, List<Diagnostic> diags) { _s = s; _diags = diags; }

    private char C(int n = 0) => _i + n < _s.Length ? _s[_i + n] : '\0';
    private void Advance()
    {
        if (_i < _s.Length)
        {
            if (_s[_i] == '\n') { _line++; _col = 1; }
            else _col++;
            _i++;
        }
    }

    public List<CsToken> Lex()
    {
        var list = new List<CsToken>();
        while (_i < _s.Length)
        {
            char c = C();
            if (char.IsWhiteSpace(c)) { Advance(); continue; }
            if (c == '/' && C(1) == '/')
            {
                while (C() != '\n' && _i < _s.Length) Advance();
                continue;
            }
            if (c == '/' && C(1) == '*')
            {
                Advance(); Advance();
                while (_i < _s.Length && !(C() == '*' && C(1) == '/')) Advance();
                if (_i < _s.Length) { Advance(); Advance(); }
                continue;
            }
            if (c == '#')
            {
                while (C() != '\n' && _i < _s.Length) Advance();
                continue;
            }
            int line = _line, col = _col, pos = _i;
            if ((c == '$' && (C(1) == '"' || (C(1) == '@' && C(2) == '"'))) ||
                (c == '@' && C(1) == '$' && C(2) == '"'))
            {
                bool verbatim = C(1) == '@' || (c == '@' && C(1) == '$');
                if (c == '$')
                {
                    Advance();
                    if (C() == '@') Advance();
                }
                else
                {
                    Advance(); Advance();
                }
                Advance(); // opening quote
                int start = _i;
                int brace = 0;
                while (_i < _s.Length)
                {
                    char ch = C();
                    if (brace > 0)
                    {
                        if (ch == '"' || ch == '\'') { SkipQuotedInExpression(ch); continue; }
                        if (ch == '{') brace++;
                        else if (ch == '}') brace--;
                        else if (ch == '/' && C(1) == '/') { while (C() != '\n' && _i < _s.Length) Advance(); continue; }
                        Advance();
                        continue;
                    }
                    if (ch == '"') break;
                    if (ch == '{' && C(1) == '{') { Advance(); Advance(); continue; }
                    if (ch == '}' && C(1) == '}') { Advance(); Advance(); continue; }
                    if (ch == '{') { brace = 1; Advance(); continue; }
                    if (ch == '\\' && !verbatim) { Advance(); if (_i < _s.Length) Advance(); continue; }
                    Advance();
                }
                string raw = _s[start.._i];
                if (C() == '"') Advance();
                list.Add(new CsToken { Kind = CsTokKind.String, Text = raw, Value = raw, IsInterpolated = true, IsVerbatim = verbatim, Line = line, Col = col, Pos = pos });
                continue;
            }
            if (c == '@' && C(1) == '"')
            {
                Advance(); Advance();
                var sb = new StringBuilder();
                while (_i < _s.Length)
                {
                    if (C() == '"')
                    {
                        if (C(1) == '"') { sb.Append('"'); Advance(); Advance(); continue; }
                        Advance(); break;
                    }
                    sb.Append(C()); Advance();
                }
                list.Add(new CsToken { Kind = CsTokKind.String, Text = sb.ToString(), Value = sb.ToString(), Line = line, Col = col, Pos = pos });
                continue;
            }
            if (c == '$')
            {
                _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = "Interpolated strings are not supported", Line = line, Column = col, Code = "CS1056" });
                Advance();
                continue;
            }
            if (c == '"')
            {
                Advance();
                var sb = new StringBuilder();
                while (_i < _s.Length && C() != '"')
                {
                    if (C() == '\\') { Advance(); sb.Append(ReadEscape()); }
                    else { sb.Append(C()); Advance(); }
                }
                Advance();
                list.Add(new CsToken { Kind = CsTokKind.String, Text = sb.ToString(), Value = sb.ToString(), Line = line, Col = col, Pos = pos });
                continue;
            }
            if (c == '\'')
            {
                Advance();
                char ch = '\0';
                if (C() == '\\') { Advance(); ch = ReadEscape(); }
                else { ch = C(); Advance(); }
                if (C() == '\'') Advance();
                list.Add(new CsToken { Kind = CsTokKind.Char, Text = ch.ToString(), Value = ch.ToString(), Line = line, Col = col, Pos = pos });
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(C(1))))
            {
                int start = _i;
                if (c == '0' && (C(1) == 'x' || C(1) == 'X'))
                {
                    Advance(); Advance();
                    while (Uri.IsHexDigit(C())) Advance();
                }
                else
                {
                    while (char.IsDigit(C())) Advance();
                    if (C() == '.') { Advance(); while (char.IsDigit(C())) Advance(); }
                    if (C() is 'e' or 'E') { Advance(); if (C() is '+' or '-') Advance(); while (char.IsDigit(C())) Advance(); }
                }
                while (C() is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U' or 'l' or 'L') Advance();
                string raw = _s[start.._i];
                double num = 0;
                try
                {
                    if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) num = (double)Convert.ToInt64(raw[2..].TrimEnd('u', 'U', 'l', 'L'), 16);
                    else num = double.Parse(raw.TrimEnd('f', 'F', 'd', 'D', 'm', 'M', 'u', 'U', 'l', 'L'), CultureInfo.InvariantCulture);
                }
                catch { num = 0; }
                list.Add(new CsToken { Kind = CsTokKind.Number, Text = raw, Number = num, Line = line, Col = col, Pos = pos });
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                if (c == '@') Advance();
                int start = _i;
                while (char.IsLetterOrDigit(C()) || C() == '_') Advance();
                string id = _s[start.._i];
                list.Add(new CsToken { Kind = Keywords.Contains(id) ? CsTokKind.Keyword : CsTokKind.Ident, Text = id, Line = line, Col = col, Pos = pos });
                continue;
            }
            string op = MatchOperator();
            if (op.Length > 0)
            {
                for (int k = 0; k < op.Length; k++) Advance();
                list.Add(new CsToken { Kind = CsTokKind.Punct, Text = op, Line = line, Col = col, Pos = pos });
                continue;
            }
            _diags.Add(new Diagnostic { Severity = DiagnosticSeverity.Error, Message = $"Unexpected character '{c}'", Line = line, Column = col, Code = "CS1039" });
            Advance();
        }
        list.Add(new CsToken { Kind = CsTokKind.Eof, Text = "<eof>", Line = _line, Col = _col, Pos = _i });
        return list;
    }

    private void SkipQuotedInExpression(char quote)
    {
        Advance();
        while (_i < _s.Length)
        {
            if (C() == '\\') { Advance(); if (_i < _s.Length) Advance(); continue; }
            if (C() == quote) { Advance(); return; }
            Advance();
        }
    }

    private char ReadEscape()
    {
        char c = C();
        Advance();
        return c switch
        {
            'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', '\\' => '\\', '"' => '"', '\'' => '\'',
            'b' => '\b', 'f' => '\f', 'v' => '\v',
            'u' => ReadUnicode(4), 'x' => ReadUnicode(4),
            _ => c
        };
    }

    private char ReadUnicode(int count)
    {
        int v = 0;
        for (int i = 0; i < count && Uri.IsHexDigit(C()); i++) { v = v * 16 + Convert.ToInt32(C().ToString(), 16); Advance(); }
        return (char)v;
    }

    private string MatchOperator()
    {
        string[] ops3 = { "<<=", ">>=", "??=" };
        foreach (var op in ops3)
            if (C() == op[0] && C(1) == op[1] && C(2) == op[2]) return op;
        string[] ops = { "=>", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>", "??", "?.", "::" };
        foreach (var op in ops)
            if (C() == op[0] && C(1) == op[1]) return op;
        return "(){}[];,.<>+-*/%!&|^~?:=".Contains(C()) ? C().ToString() : "";
    }
}
