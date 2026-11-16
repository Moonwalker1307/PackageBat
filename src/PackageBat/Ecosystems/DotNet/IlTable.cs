using System.Reflection;
using System.Reflection.Emit;

namespace PackageBat.Ecosystems.DotNet;

/// <summary>Operand types of every IL opcode, for walking method bodies without decoding them fully.</summary>
internal static class IlTable
{
    public static readonly OperandType[] OneByte = new OperandType[256];
    public static readonly OperandType[] TwoByte = new OperandType[256];

    static IlTable()
    {
        Array.Fill(OneByte, OperandType.InlineNone);
        Array.Fill(TwoByte, OperandType.InlineNone);
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is OpCode op)
            {
                var v = (ushort)op.Value;
                if (op.Size == 1) OneByte[v] = op.OperandType;
                else TwoByte[v & 0xFF] = op.OperandType;
            }
        }
    }

    public static int OperandSize(OperandType t) => t switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };
}
