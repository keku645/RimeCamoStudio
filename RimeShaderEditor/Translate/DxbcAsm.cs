using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RimeShaderEditor.Translate;

public enum OperandKind
{
    Temp,        // r0
    Input,       // v1
    Output,      // o0
    ConstBuffer, // cb2[20]
    IndexableTemp, // x0[3] - a SEPARATE register file from r0, not a variant of it
    Literal,     // l(1.0, 2.0, ...)
    Resource,    // t1
    Sampler,     // s0

    /// <summary>
    /// A system-value OUTPUT that is not a render target: `oMask` (SV_Coverage), `oDepth`.
    /// </summary>
    /// <remarks>
    /// Its own kind so the gap it causes is reported by its real name. Vegetation writes `oMask` to do
    /// alpha-to-coverage - four dithered samples of the alpha, one per coverage bit - and without this the
    /// translator blamed the arithmetic that feeds it (`bfi`, `iadd`) and hid the actual reason it cannot be
    /// expressed: a node graph has no per-sample output.
    /// </remarks>
    SystemOutput,

    /// <summary>The literal word `null`: a destination the instruction is told to throw away.</summary>
    /// <remarks>
    /// Its own kind rather than Unknown, because the two need OPPOSITE handling: an unknown destination means
    /// the translator did not understand something and must report a gap, while `null` means the shader itself
    /// does not want the value and there is nothing missing. `sincos r0.x, null, r0.x` is the common case.
    /// </remarks>
    Null,
    Unknown,
}

/// <summary>One operand of a disassembled instruction, with its swizzle and modifiers.</summary>
public sealed class AsmOperand
{
    public OperandKind Kind { get; init; }
    public int Index { get; init; }

    /// <summary>Second index for cbN[M]: the vector element.</summary>
    public int Element { get; init; } = -1;

    /// <summary>Per-component selection, 0..3, one entry per written/read component. Empty means "as declared".</summary>
    public int[] Swizzle { get; init; } = Array.Empty<int>();

    /// <summary>For a destination: which components it writes, as indices 0..3.</summary>
    public int[] WriteMask { get; init; } = Array.Empty<int>();

    public bool Negate { get; init; }
    public bool Absolute { get; init; }
    public float[] Literal { get; init; } = Array.Empty<float>();

    /// <summary>The verbatim text, kept for the operands whose identity IS their name (`oMask`, `oDepth`).</summary>
    public string Name { get; init; } = "";

    public override string ToString()
    {
        var s_Body = Kind switch
        {
            OperandKind.Literal => "l(" + string.Join(",", Literal.Select(p_L => p_L.ToString("0.###", CultureInfo.InvariantCulture))) + ")",
            OperandKind.ConstBuffer => $"cb{Index}[{Element}]",
            OperandKind.Temp => "r" + Index,
            OperandKind.IndexableTemp => $"x{Index}[{Element}]",
            OperandKind.Input => "v" + Index,
            OperandKind.Output => "o" + Index,
            OperandKind.Resource => "t" + Index,
            OperandKind.Sampler => "s" + Index,
            OperandKind.SystemOutput => Name,
            OperandKind.Null => "null",
            _ => "?",
        };

        var s_Suffix = WriteMask.Length > 0
            ? "." + new string(WriteMask.Select(p_C => "xyzw"[p_C]).ToArray())
            : Swizzle.Length > 0 ? "." + new string(Swizzle.Select(p_C => "xyzw"[p_C]).ToArray()) : "";

        return (Negate ? "-" : "") + (Absolute ? "|" : "") + s_Body + s_Suffix + (Absolute ? "|" : "");
    }
}

public sealed class AsmInstruction
{
    public string Opcode { get; init; } = "";

    /// <summary>
    /// The resource kind fxc prints after a sample opcode - "texture2d", "texturecube", "texture2darray". It is
    /// NOT decoration: a cube fetch takes a three-component direction and cannot be expressed by a 2D texture
    /// node, so translating one as if it were 2D produces a graph that is quietly wrong rather than one that is
    /// honestly incomplete.
    /// </summary>
    public string ResourceType { get; init; } = "";

    public bool Saturate { get; init; }
    public AsmOperand? Destination { get; init; }
    public List<AsmOperand> Sources { get; } = new();

    public override string ToString() =>
        $"{Opcode}{(Saturate ? "_sat" : "")} {Destination}" +
        (Sources.Count > 0 ? ", " + string.Join(", ", Sources) : "");
}

/// <summary>
/// Parses the TEXT disassembly that `fxc /dumpbin` produces.
///
/// Text rather than the SHEX bytecode on purpose: the text form is stable, human-checkable and already a
/// dependency here, while a bit-level SM5 decoder would be several times the work for the same information. The
/// point of this parser is to feed a translator that turns a compiled shader back into a node graph — not to
/// recover DICE's authored graph, which is gone, but to build ONE graph that computes the same thing, which
/// --shaderdiff can then prove equivalent.
/// </summary>
public static class DxbcAsm
{
    private static readonly Regex s_Literal = new(@"^l\(([^)]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// One component of an `l(...)`, decimal or RAW BIT PATTERN.
    ///
    /// ⛔ The disassembler prints a literal in hex whenever the instruction is an integer one, and the bitwise
    /// ops are exactly where that happens: `and r0.x, r0.x, l(0x3f800000)` is masking with the bits of 1.0f, and
    /// `l(0xc0490fdb)` is -pi. `float.TryParse` rejects both and the old code fell back to 0f WITHOUT SAYING SO,
    /// which turns "select 1.0" into "select nothing" - the same silent-zero family as the write-mask bug.
    /// A hex token in DXBC is always the 32-bit encoding, so it is reinterpreted, never converted.
    /// </summary>
    private static float ParseLiteral(string p_Text)
    {
        var s_Negative = p_Text.StartsWith('-');
        var s_Body = s_Negative ? p_Text[1..] : p_Text;

        if (s_Body.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(s_Body[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var s_Bits))
        {
            var s_Value = BitConverter.Int32BitsToSingle(unchecked((int) s_Bits));
            return s_Negative ? -s_Value : s_Value;
        }

        return float.TryParse(p_Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Float)
            ? s_Float
            : 0f;
    }
    private static readonly Regex s_ConstBuffer = new(@"^cb(\d+)\[(\d+)\]", RegexOptions.Compiled);

    // ⛔ Indexable temps are their OWN register file. Before this they fell through to the generic register
    // regex, failed it, and became Kind.Unknown with index 0 - which the builder then wrote to r0, silently
    // destroying whatever r0 held. Three `mov x0[0].x, l(0)` at the top of a terrain mask pass wiped the mask.
    private static readonly Regex s_IndexableTemp = new(@"^x(\d+)\[(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex s_Register = new(@"^([rvots])(\d+)", RegexOptions.Compiled);

    /// <summary>Opcodes whose operands are all READ - a branch tests a value, it does not produce one.</summary>
    private static readonly HashSet<string> s_NoDestination = new(StringComparer.Ordinal)
    {
        "if_nz", "if_z", "discard_nz", "discard_z", "breakc_nz", "breakc_z",
        "continuec_nz", "continuec_z", "retc_nz", "retc_z", "callc", "switch",
    };

    public static List<AsmInstruction> Parse(IEnumerable<string> p_Lines)
    {
        var s_Result = new List<AsmInstruction>();
        foreach (var s_Raw in p_Lines)
        {
            var s_Line = s_Raw.Trim();
            if (s_Line.Length == 0 || s_Line.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Declarations and the shader model line carry no computation.
            if (s_Line.StartsWith("dcl_", StringComparison.Ordinal) ||
                s_Line.StartsWith("ps_", StringComparison.Ordinal) ||
                s_Line.StartsWith("vs_", StringComparison.Ordinal))
                continue;

            var s_Instruction = ParseLine(s_Line);
            if (s_Instruction != null)
                s_Result.Add(s_Instruction);
        }

        return s_Result;
    }

    private static AsmInstruction? ParseLine(string p_Line)
    {
        // "sample_indexable(texture2d)(float,float,float,float) r0.xyz, v5.xyxx, t2.xywz, s0"
        var s_Space = p_Line.IndexOf(' ');
        var s_Head = s_Space < 0 ? p_Line : p_Line[..s_Space];
        var s_Rest = s_Space < 0 ? "" : p_Line[(s_Space + 1)..];

        // Strip the parenthesised type decorations fxc prints after some opcodes, keeping the FIRST group: for a
        // sample it names the resource kind, which decides whether the fetch is representable at all.
        var s_ResourceType = "";
        var s_Paren = s_Head.IndexOf('(');
        if (s_Paren >= 0)
        {
            var s_Close = s_Head.IndexOf(')', s_Paren);
            if (s_Close > s_Paren)
                s_ResourceType = s_Head[(s_Paren + 1)..s_Close];

            s_Head = s_Head[..s_Paren];
        }

        var s_Saturate = s_Head.EndsWith("_sat", StringComparison.Ordinal);
        if (s_Saturate)
            s_Head = s_Head[..^4];

        if (s_Head is "ret" or "")
            return null;

        var s_Operands = SplitOperands(s_Rest).Select(ParseOperand).Where(p_O => p_O != null).Cast<AsmOperand>().ToList();
        if (s_Operands.Count == 0)
            return new AsmInstruction { Opcode = s_Head, Saturate = s_Saturate, ResourceType = s_ResourceType };

        // ⛔ CONTROL FLOW HAS NO DESTINATION. `if_nz r1.z` carries one operand and it is what the branch TESTS,
        // but the generic rule "first operand is the destination" swallowed it, leaving Sources empty - so every
        // predicate fell back to a constant 1 and every branch was taken unconditionally, while `discard` lost
        // its condition entirely. It read as a terrain bug for a long time because those shaders are where the
        // branches live.
        if (s_NoDestination.Contains(s_Head))
        {
            var s_Test = new AsmInstruction { Opcode = s_Head, Saturate = s_Saturate, ResourceType = s_ResourceType };
            s_Test.Sources.AddRange(s_Operands);
            return s_Test;
        }

        var s_Instruction = new AsmInstruction
        {
            Opcode = s_Head,
            Saturate = s_Saturate,
            ResourceType = s_ResourceType,
            Destination = s_Operands[0],
        };

        s_Instruction.Sources.AddRange(s_Operands.Skip(1));
        return s_Instruction;
    }

    /// <summary>Splits on commas that are not inside l(...) or a swizzle.</summary>
    private static List<string> SplitOperands(string p_Text)
    {
        var s_Result = new List<string>();
        var s_Depth = 0;
        var s_Start = 0;

        for (var i = 0; i < p_Text.Length; i++)
        {
            if (p_Text[i] == '(')
                s_Depth++;
            else if (p_Text[i] == ')')
                s_Depth--;
            else if (p_Text[i] == ',' && s_Depth == 0)
            {
                s_Result.Add(p_Text[s_Start..i].Trim());
                s_Start = i + 1;
            }
        }

        if (s_Start < p_Text.Length)
            s_Result.Add(p_Text[s_Start..].Trim());

        return s_Result.Where(p_S => p_S.Length > 0).ToList();
    }

    private static AsmOperand? ParseOperand(string p_Text)
    {
        var s_Text = p_Text.Trim();
        if (s_Text.Length == 0)
            return null;

        var s_Negate = s_Text.StartsWith("-", StringComparison.Ordinal);
        if (s_Negate)
            s_Text = s_Text[1..];

        var s_Absolute = s_Text.StartsWith("|", StringComparison.Ordinal);
        if (s_Absolute)
            s_Text = s_Text.Trim('|');

        if (string.Equals(s_Text, "null", StringComparison.Ordinal))
            return new AsmOperand { Kind = OperandKind.Null };

        if (s_Text.StartsWith("oMask", StringComparison.Ordinal) ||
            s_Text.StartsWith("oDepth", StringComparison.Ordinal))
            return new AsmOperand { Kind = OperandKind.SystemOutput, Name = s_Text };

        var s_LiteralMatch = s_Literal.Match(s_Text);
        if (s_LiteralMatch.Success)
        {
            var s_Values = s_LiteralMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p_V => ParseLiteral(p_V.Trim()))
                .ToArray();

            return new AsmOperand { Kind = OperandKind.Literal, Literal = s_Values, Negate = s_Negate };
        }

        var s_TempMatch = s_IndexableTemp.Match(s_Text);
        if (s_TempMatch.Success)
            return new AsmOperand
            {
                Kind = OperandKind.IndexableTemp,
                Index = int.Parse(s_TempMatch.Groups[1].Value),
                Element = int.Parse(s_TempMatch.Groups[2].Value),
                Swizzle = ParseComponents(s_Text[s_TempMatch.Length..]),
                WriteMask = ParseComponents(s_Text[s_TempMatch.Length..]),
                Negate = s_Negate,
                Absolute = s_Absolute,
            };

        var s_CbMatch = s_ConstBuffer.Match(s_Text);
        if (s_CbMatch.Success)
            return new AsmOperand
            {
                Kind = OperandKind.ConstBuffer,
                Index = int.Parse(s_CbMatch.Groups[1].Value),
                Element = int.Parse(s_CbMatch.Groups[2].Value),
                Swizzle = ParseComponents(s_Text[s_CbMatch.Length..]),
                Negate = s_Negate,
                Absolute = s_Absolute,
            };

        var s_RegMatch = s_Register.Match(s_Text);
        if (!s_RegMatch.Success)
            return new AsmOperand { Kind = OperandKind.Unknown };

        var s_Kind = s_RegMatch.Groups[1].Value switch
        {
            "r" => OperandKind.Temp,
            "v" => OperandKind.Input,
            "o" => OperandKind.Output,
            "t" => OperandKind.Resource,
            "s" => OperandKind.Sampler,
            _ => OperandKind.Unknown,
        };

        var s_Components = ParseComponents(s_Text[s_RegMatch.Length..]);
        return new AsmOperand
        {
            Kind = s_Kind,
            Index = int.Parse(s_RegMatch.Groups[2].Value),
            Swizzle = s_Components,
            WriteMask = s_Components,
            Negate = s_Negate,
            Absolute = s_Absolute,
        };
    }

    private static int[] ParseComponents(string p_Suffix)
    {
        var s_Text = p_Suffix.Trim();
        if (!s_Text.StartsWith(".", StringComparison.Ordinal))
            return Array.Empty<int>();

        return s_Text[1..]
            .TakeWhile(p_C => "xyzw".Contains(p_C))
            .Select(p_C => "xyzw".IndexOf(p_C))
            .ToArray();
    }
}
