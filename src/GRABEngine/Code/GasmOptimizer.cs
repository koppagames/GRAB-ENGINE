using System;
using System.Collections.Generic;
using System.Linq;
using GrabEngine.Core;

namespace GrabEngine.Code;

/// <summary>
/// Safe GASM peephole optimiser used by Release exports. Every pass either removes
/// an instruction that can never change a register, or replaces an operation with a
/// constant result that is identical to what the VM would compute. Labels are never
/// renumbered, so control flow is preserved exactly.
/// </summary>
public static class GasmOptimizer
{
    public sealed class Stats
    {
        public int Nops;
        public int SelfSets;
        public int Constants;
        public int IdentityOps;
        public int Unreachable;
        public int TotalRemoved => Nops + SelfSets + Constants + IdentityOps + Unreachable;
        public override string ToString() =>
            $"{TotalRemoved} instruction(s) removed ({Constants} folded, {SelfSets} self-sets, {IdentityOps} identity ops, {Nops} no-ops, {Unreachable} unreachable)";
    }

    public static Stats Optimize(GasmProgram program)
    {
        var stats = new Stats();
        var instructions = program.Instructions;

        // 1. Unreachable code after GOTO / END until the next label.
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Op is not (GasmOp.Goto or GasmOp.End)) continue;
            int j = i + 1;
            while (j < instructions.Count && instructions[j].Op != GasmOp.Label)
            {
                instructions.RemoveAt(j);
                stats.Unreachable++;
            }
        }

        // 2. Fold constant arithmetic and remove identity operations.
        for (int i = 0; i < instructions.Count; i++)
        {
            var ins = instructions[i];
            if (ins.Operands.Count == 0) continue;
            var dst = ins.Operands[0];
            if (dst.Kind is not (GasmOperandKind.Working or GasmOperandKind.InOut or GasmOperandKind.Output or GasmOperandKind.Input)) continue;

            if (ins.Op == GasmOp.Set && ins.Operands.Count >= 2 && Same(ins.Operands[0], ins.Operands[1]))
            {
                instructions[i] = new GasmInstruction(GasmOp.Noop);
                stats.SelfSets++;
                continue;
            }

            if (ins.Op is GasmOp.Add or GasmOp.Sub or GasmOp.Mul or GasmOp.Div or GasmOp.Mod
                && ins.Operands.Count >= 3
                && ins.Operands[1].Kind == GasmOperandKind.Constant
                && ins.Operands[2].Kind == GasmOperandKind.Constant)
            {
                float a = ins.Operands[1].Value, b = ins.Operands[2].Value;
                float? result = ins.Op switch
                {
                    GasmOp.Add => a + b,
                    GasmOp.Sub => a - b,
                    GasmOp.Mul => a * b,
                    GasmOp.Div => MathF.Abs(b) < 1e-9f ? null : a / b,
                    GasmOp.Mod => MathF.Abs(b) < 1e-9f ? null : a % b,
                    _ => null
                };
                if (result is float value)
                {
                    instructions[i] = new GasmInstruction(GasmOp.Set, ins.Operands[0], GasmOperand.Const(value));
                    stats.Constants++;
                    continue;
                }
            }

            if (ins.Op is GasmOp.Add or GasmOp.Sub or GasmOp.Mul && ins.Operands.Count >= 3
                && ins.Operands[2].Kind == GasmOperandKind.Constant
                && Same(ins.Operands[0], ins.Operands[1]))
            {
                float c = ins.Operands[2].Value;
                bool identity = ins.Op switch
                {
                    GasmOp.Add or GasmOp.Sub => MathF.Abs(c) < 1e-9f,
                    GasmOp.Mul => MathF.Abs(c - 1f) < 1e-9f,
                    _ => false
                };
                if (identity)
                {
                    instructions[i] = new GasmInstruction(GasmOp.Noop);
                    stats.IdentityOps++;
                    continue;
                }
                if (ins.Op == GasmOp.Mul && MathF.Abs(c) < 1e-9f)
                {
                    instructions[i] = new GasmInstruction(GasmOp.Set, ins.Operands[0], GasmOperand.Const(0));
                    stats.Constants++;
                }
            }
        }

        // 3. Drop the markers created above and any NOOPs written by the compiler.
        for (int i = instructions.Count - 1; i >= 0; i--)
        {
            if (instructions[i].Op == GasmOp.Noop && instructions[i].Operands.Count == 0)
            {
                instructions.RemoveAt(i);
                stats.Nops++;
            }
        }

        // 4. Keep register tables compact: trailing unused temporaries only waste space.
        while (program.WorkingRegisters.Count > 8 &&
               !instructions.Any(op => op.Operands.Any(x => x.Kind == GasmOperandKind.Working && x.Index == program.WorkingRegisters.Count - 1)))
        {
            program.WorkingRegisters.RemoveAt(program.WorkingRegisters.Count - 1);
        }

        return stats;
    }

    private static bool Same(GasmOperand a, GasmOperand b) => a.Kind == b.Kind && a.Index == b.Index;

    /// <summary>Rebuilds the human-readable listing after optimisation.</summary>
    public static IEnumerable<string> ToText(GasmProgram program)
    {
        foreach (var ins in program.Instructions)
        {
            if (ins.Op == GasmOp.Label && ins.Operands.Count > 0)
            {
                yield return "LABEL " + (program.Labels.ElementAtOrDefault(ins.Operands[0].Index) ?? "L" + ins.Operands[0].Index);
                continue;
            }
            var line = new System.Text.StringBuilder(GasmEmitter.OpName(ins.Op));
            foreach (var op in ins.Operands)
            {
                line.Append(' ');
                line.Append(op.Kind switch
                {
                    GasmOperandKind.Working => program.WorkingRegisters.ElementAtOrDefault(op.Index)?.Name ?? "R" + op.Index,
                    GasmOperandKind.Input => program.InputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "in" + op.Index,
                    GasmOperandKind.Output => program.OutputRegisters.ElementAtOrDefault(op.Index)?.Name ?? "out" + op.Index,
                    GasmOperandKind.InOut => program.InOutRegisters.ElementAtOrDefault(op.Index)?.Name ?? "io" + op.Index,
                    GasmOperandKind.Constant => op.Value.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture),
                    GasmOperandKind.Label => program.Labels.ElementAtOrDefault(op.Index) ?? "L" + op.Index,
                    _ => op.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            yield return line.ToString();
        }
    }
}
