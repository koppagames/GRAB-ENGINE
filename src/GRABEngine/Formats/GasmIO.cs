using System;
using System.Collections.Generic;
using System.Linq;
using GrabEngine.Core;

namespace GrabEngine.Formats;

public static class GasmIO
{
    public static void WriteProgram(PbWriter w, int field, GasmProgram p)
    {
        w.Message(field, m => WriteProgramBody(m, p));
    }

    public static void WriteProgramBody(PbWriter w, GasmProgram p)
    {
        foreach (var r in p.InputRegisters) w.Message(1, m => m.String(1, r.Name));
        foreach (var r in p.OutputRegisters) w.Message(2, m => m.String(1, r.Name));
        foreach (var r in p.WorkingRegisters) w.Message(3, m => m.String(1, r.Name));
        foreach (var l in p.Labels) w.Message(4, m => m.String(1, l));
        foreach (var ins in p.Instructions)
        {
            w.Message(5, m =>
            {
                m.Varint(1, (ulong)ins.Op);
                foreach (var op in ins.Operands)
                {
                    m.Message(2, o =>
                    {
                        o.Varint(1, (ulong)op.Kind);
                        if (op.Kind == GasmOperandKind.Constant) o.Float(3, op.Value);
                        else o.Int32(2, op.Index);
                    });
                }
            });
        }
        foreach (var r in p.InOutRegisters) w.Message(6, m => m.String(1, r.Name));
    }

    public static GasmProgram ReadProgram(byte[] data)
    {
        var p = new GasmProgram();
        var fields = PbReader.Read(data);
        foreach (var f in fields.Where(x => x.Number == 1))
            p.InputRegisters.Add(new GasmRegister { Name = PbReader.String(PbReader.Message(f), 1) });
        foreach (var f in fields.Where(x => x.Number == 2))
            p.OutputRegisters.Add(new GasmRegister { Name = PbReader.String(PbReader.Message(f), 1) });
        foreach (var f in fields.Where(x => x.Number == 3))
            p.WorkingRegisters.Add(new GasmRegister { Name = PbReader.String(PbReader.Message(f), 1) });
        foreach (var f in fields.Where(x => x.Number == 6))
            p.InOutRegisters.Add(new GasmRegister { Name = PbReader.String(PbReader.Message(f), 1) });
        foreach (var f in fields.Where(x => x.Number == 4))
            p.Labels.Add(PbReader.String(PbReader.Message(f), 1));
        foreach (var f in fields.Where(x => x.Number == 5))
        {
            var insFields = PbReader.Message(f);
            var ins = new GasmInstruction { Op = (GasmOp)PbReader.Varint(insFields, 1) };
            foreach (var of in insFields.Where(x => x.Number == 2))
            {
                var ofs = PbReader.Message(of);
                var kind = (GasmOperandKind)PbReader.Varint(ofs, 1);
                var operand = new GasmOperand { Kind = kind };
                if (kind == GasmOperandKind.Constant) operand.Value = PbReader.Float(ofs, 3);
                else operand.Index = PbReader.Int(ofs, 2);
                ins.Operands.Add(operand);
            }
            p.Instructions.Add(ins);
        }
        if (p.WorkingRegisters.Count == 0)
            for (int i = 0; i < 8; i++) p.WorkingRegisters.Add(new GasmRegister { Name = "R" + i });
        return p;
    }

    public static void WriteConnections(PbWriter w, IReadOnlyList<GasmConnection> connections)
    {
        foreach (var c in connections) w.Message(3, m => WriteConnection(m, c));
    }

    public static void WriteConnection(PbWriter w, GasmConnection c)
    {
        if (c.ObjectId != 0) w.Varint(1, c.ObjectId);
        w.String(2, c.Name);
        foreach (var p in c.Properties) w.Message(3, m => WriteProperty(m, p));
        if (c.Type != GasmConnectionType.Node) w.Varint(4, (ulong)c.Type);
    }

    private static void WriteProperty(PbWriter w, GasmProperty p)
    {
        if (p.ObjectId != 0) w.Varint(1, p.ObjectId);
        foreach (var c in p.Components)
        {
            w.Message(2, m =>
            {
                m.Int32(1, c.Input);
                m.Int32(2, c.Output);
                m.Int32(3, c.InOut);
            });
        }
        // The oneof type tag is represented by an empty message.
        w.Message((int)p.Kind, _ => { });
    }

    public static List<GasmConnection> ReadConnections(IEnumerable<PbField> nodeFields)
    {
        var result = new List<GasmConnection>();
        foreach (var f in nodeFields.Where(x => x.Number == 3))
        {
            var cf = PbReader.Message(f);
            var conn = new GasmConnection
            {
                ObjectId = PbReader.Varint(cf, 1),
                Name = PbReader.String(cf, 2),
                Type = (GasmConnectionType)PbReader.Int(cf, 4)
            };
            foreach (var pf in cf.Where(x => x.Number == 3))
            {
                var pfs = PbReader.Message(pf);
                var prop = new GasmProperty { ObjectId = PbReader.Varint(pfs, 1) };
                foreach (var cf2 in pfs.Where(x => x.Number == 2))
                {
                    var cfs = PbReader.Message(cf2);
                    prop.Components.Add(new GasmComponent(
                        PbReader.Int(cfs, 1),
                        PbReader.Int(cfs, 2),
                        PbReader.Int(cfs, 3)));
                }
                foreach (var tag in pfs)
                {
                    if (tag.Number >= 3 && tag.Number <= 13)
                    {
                        prop.Kind = (GasmPropertyKind)tag.Number;
                        break;
                    }
                }
                conn.Properties.Add(prop);
            }
            result.Add(conn);
        }
        return result;
    }
}
