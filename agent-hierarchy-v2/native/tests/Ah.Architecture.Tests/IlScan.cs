using System.Reflection;
using System.Reflection.Emit;

namespace Ah.Architecture.Tests;

internal static class IlScan
{
    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => (ushort)o.Value);

    /// <summary>The metadata token of every method operand (call, callvirt, newobj, ldftn, ldvirtftn) in a method body.</summary>
    internal static IEnumerable<int> CalledTokens(byte[] il) =>
        Instructions(il).Where(static i => i.Op.OperandType == OperandType.InlineMethod).Select(static i => i.Operand);

    /// <summary>One instruction of a method body: its opcode and, for an operand of four bytes, that operand (a token or an immediate); otherwise 0.</summary>
    internal readonly record struct Instruction(OpCode Op, int Operand);

    /// <summary>The instructions of a method body, in order.</summary>
    internal static IEnumerable<Instruction> Instructions(byte[] il)
    {
        var at = 0;
        while (at < il.Length)
        {
            ushort value = il[at++];
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il[at++]);
            }

            var op = OpCodesByValue[value];
            yield return new Instruction(op, op.OperandType is OperandType.InlineNone or OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.InlineVar or OperandType.InlineI8 or OperandType.InlineR or OperandType.InlineSwitch ? 0 : BitConverter.ToInt32(il, at));

            at += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, at)),
                _ => 4,
            };
        }
    }
}
