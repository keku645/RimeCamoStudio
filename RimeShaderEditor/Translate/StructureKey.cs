using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimeShaderEditor.Translate;

/// <summary>
/// Normalised STRUCTURE keys for a disassembled pixel shader.
///
/// The question these answer is not "are these two shaders the same bytecode" — they never are, because two props
/// authored from the same preset differ in their constants, their texture slots and their register
/// allocation. It is "are these two shaders the same GRAPH with different values plugged in", because that is the
/// thing a human can hand-author once and re-use.
///
/// Four keys of deliberately different tightness are produced so the coverage number can be sanity-checked
/// against a key that is known to be too loose. Read them together: a fraction that only appears under the loose
/// key is an artefact of the key, not a property of the corpus.
/// </summary>
public static class StructureKey
{
    /// <summary>
    /// LOOSE CONTROL — the opcode sequence alone. No masks, no operands, no dataflow. Deliberately wrong: it
    /// cannot tell "mul a,b" from "mul b,c", so anything it reports is an upper bound with no meaning.
    /// </summary>
    public static string OpcodeSequence(IEnumerable<AsmInstruction> p_Instructions) =>
        string.Join(";", p_Instructions.Select(p_I => p_I.Opcode));

    /// <summary>
    /// COARSE — the multiset of opcodes, order discarded. Answers "same instruction budget, same operation mix".
    /// </summary>
    public static string OpcodeMultiset(IEnumerable<AsmInstruction> p_Instructions) =>
        string.Join(",", p_Instructions
            .GroupBy(p_I => p_I.Opcode, StringComparer.Ordinal)
            .OrderBy(p_G => p_G.Key, StringComparer.Ordinal)
            .Select(p_G => $"{p_G.Key}*{p_G.Count()}"));

    /// <summary>
    /// THE ASKED-FOR KEY — ordered (opcode, saturate, destination write mask, operand KINDS and swizzles) with
    /// every literal VALUE, register INDEX, cbuffer element and texture slot erased.
    ///
    /// Erasing register indices outright is the loosest thing that can still be called a structure: it keeps the
    /// shape of every instruction but throws the DATAFLOW away, so two shaders that run the same instructions on
    /// differently-wired registers collide. That is why <see cref="Dataflow"/> exists next to it.
    /// </summary>
    public static string Shape(IEnumerable<AsmInstruction> p_Instructions)
    {
        var s_Builder = new StringBuilder();
        foreach (var s_Instruction in p_Instructions)
        {
            s_Builder.Append(s_Instruction.Opcode);
            if (s_Instruction.Saturate)
                s_Builder.Append("_sat");

            s_Builder.Append(' ').Append(Mask(s_Instruction.Destination));
            foreach (var s_Source in s_Instruction.Sources)
                s_Builder.Append(',').Append(ShapeOperand(s_Source));

            s_Builder.Append(';');
        }

        return s_Builder.ToString();
    }

    /// <summary>
    /// STRICT — <see cref="Shape"/> plus the wiring. Temps, inputs, resources and samplers are alpha-renamed in
    /// order of first appearance, so "which value feeds which instruction" survives while the actual register
    /// numbers (a compiler allocation detail) do not. Constant-buffer INDEX is kept (cb0 and cb2 are different
    /// hardware slots with different meanings) but the element inside it is erased, because the element is where
    /// a preset's per-instance values live.
    ///
    /// This is the key that answers the design question. Two shaders that agree here compute the same expression
    /// tree with different numbers and different textures plugged into the leaves — exactly what one authored
    /// graph plus a parameter block covers.
    /// </summary>
    public static string Dataflow(IEnumerable<AsmInstruction> p_Instructions)
    {
        var s_Names = new Dictionary<string, int>(StringComparer.Ordinal);
        var s_Builder = new StringBuilder();

        string Canonical(AsmOperand p_Operand)
        {
            var s_Raw = p_Operand.Kind switch
            {
                OperandKind.Temp => "r" + p_Operand.Index,
                OperandKind.Input => "v" + p_Operand.Index,
                OperandKind.Output => "o" + p_Operand.Index,
                OperandKind.Resource => "t" + p_Operand.Index,
                OperandKind.Sampler => "s" + p_Operand.Index,
                _ => "",
            };

            if (s_Raw.Length == 0)
                return "";

            // Outputs keep their real number: o0..o3 ARE the g-buffer contract, not an allocation detail.
            if (p_Operand.Kind == OperandKind.Output)
                return "o" + p_Operand.Index;

            if (!s_Names.TryGetValue(s_Raw, out var s_Id))
            {
                s_Id = s_Names.Count(p_K => p_K.Key[0] == s_Raw[0]);
                s_Names[s_Raw] = s_Id;
            }

            return s_Raw[0].ToString() + s_Id;
        }

        foreach (var s_Instruction in p_Instructions)
        {
            s_Builder.Append(s_Instruction.Opcode);
            if (s_Instruction.Saturate)
                s_Builder.Append("_sat");

            s_Builder.Append(' ');
            if (s_Instruction.Destination != null)
                s_Builder.Append(Canonical(s_Instruction.Destination)).Append(Mask(s_Instruction.Destination));

            foreach (var s_Source in s_Instruction.Sources)
            {
                s_Builder.Append(',');
                if (s_Source.Negate)
                    s_Builder.Append('-');

                if (s_Source.Absolute)
                    s_Builder.Append('|');

                s_Builder.Append(s_Source.Kind switch
                {
                    OperandKind.Literal => "L" + s_Source.Literal.Length,
                    OperandKind.ConstBuffer => $"cb{s_Source.Index}[]",
                    OperandKind.Unknown => "?",
                    _ => Canonical(s_Source),
                });

                if (s_Source.Kind != OperandKind.Literal && s_Source.Swizzle.Length > 0)
                    s_Builder.Append('.').Append(new string(s_Source.Swizzle.Select(p_C => "xyzw"[p_C]).ToArray()));
            }

            s_Builder.Append(';');
        }

        return s_Builder.ToString();
    }

    /// <summary>
    /// PARAMETERISED FAMILY — <see cref="Shape"/> with adjacent repeated instruction blocks collapsed to one copy.
    ///
    /// The hypothesis it tests: a preset is not one graph but one graph TEMPLATE with a layer count, so a 3-layer
    /// and a 5-layer instance of the same preset differ only in how many times the layer body is unrolled. If that
    /// is what the corpus looks like, collapsing the repeats should merge them.
    ///
    /// This is a LOOSE key on purpose and its clusters are not directly authorable — a hit here means "same
    /// template, different layer count", which needs a graph with a repeat, not a fixed graph.
    /// </summary>
    public static string Collapsed(IEnumerable<AsmInstruction> p_Instructions)
    {
        var s_Tokens = Shape(p_Instructions).Split(';', StringSplitOptions.RemoveEmptyEntries);
        var s_Out = new List<string>();

        var i = 0;
        while (i < s_Tokens.Length)
        {
            var s_Block = 0;
            for (var s_Length = 1; s_Length <= 40 && i + 2 * s_Length <= s_Tokens.Length; s_Length++)
            {
                var s_Repeats = true;
                for (var s_K = 0; s_K < s_Length && s_Repeats; s_K++)
                    s_Repeats = string.Equals(s_Tokens[i + s_K], s_Tokens[i + s_Length + s_K], StringComparison.Ordinal);

                if (s_Repeats)
                {
                    s_Block = s_Length;
                    break;
                }
            }

            if (s_Block == 0)
            {
                s_Out.Add(s_Tokens[i]);
                i++;
                continue;
            }

            // Emit the block once, then skip every further consecutive copy of it.
            for (var s_K = 0; s_K < s_Block; s_K++)
                s_Out.Add(s_Tokens[i + s_K]);

            var s_Next = i + s_Block;
            while (s_Next + s_Block <= s_Tokens.Length)
            {
                var s_Same = true;
                for (var s_K = 0; s_K < s_Block && s_Same; s_K++)
                    s_Same = string.Equals(s_Tokens[i + s_K], s_Tokens[s_Next + s_K], StringComparison.Ordinal);

                if (!s_Same)
                    break;

                s_Next += s_Block;
            }

            i = s_Next;
        }

        return string.Join(";", s_Out);
    }

    private static string ShapeOperand(AsmOperand p_Operand)
    {
        var s_Prefix = (p_Operand.Negate && p_Operand.Kind != OperandKind.Literal ? "-" : "") +
                       (p_Operand.Absolute ? "|" : "");

        var s_Body = p_Operand.Kind switch
        {
            OperandKind.Literal => "L" + p_Operand.Literal.Length,
            OperandKind.ConstBuffer => "cb[]",
            OperandKind.Temp => "r",
            OperandKind.Input => "v",
            OperandKind.Output => "o" + p_Operand.Index,
            OperandKind.Resource => "t",
            OperandKind.Sampler => "s",
            _ => "?",
        };

        var s_Swizzle = p_Operand.Kind == OperandKind.Literal || p_Operand.Swizzle.Length == 0
            ? ""
            : "." + new string(p_Operand.Swizzle.Select(p_C => "xyzw"[p_C]).ToArray());

        return s_Prefix + s_Body + s_Swizzle;
    }

    private static string Mask(AsmOperand? p_Destination)
    {
        if (p_Destination == null)
            return "";

        var s_Body = p_Destination.Kind == OperandKind.Output ? "o" + p_Destination.Index : "";
        return p_Destination.WriteMask.Length == 0
            ? s_Body
            : s_Body + "." + new string(p_Destination.WriteMask.Select(p_C => "xyzw"[p_C]).ToArray());
    }

    /// <summary>
    /// SEMANTIC — what the shader does, at the altitude an artist thinks in: how many textures it samples, how
    /// many render targets it writes, and whether it contains a fresnel-like (1 - N.V)^k chain.
    ///
    /// The fresnel test is a HEURISTIC, not a proof: a dot product (or a saturate of one) that is subtracted from
    /// a literal, whose result is then either squared by a self-multiply or run through log/exp. It will call a
    /// non-fresnel "1 - x" power chain a fresnel; it is reported as "fresnel-shaped chain", not as DICE's intent.
    /// </summary>
    public static (int Samples, bool Fresnel) Semantics(List<AsmInstruction> p_Instructions)
    {
        var s_Samples = p_Instructions.Count(p_I => p_I.Opcode.StartsWith("sample", StringComparison.Ordinal));

        // Registers holding a dot-product result, propagated through mov/saturate-only copies.
        var s_FromDot = new HashSet<int>();
        var s_OneMinus = new HashSet<int>();
        var s_Fresnel = false;

        foreach (var s_Instruction in p_Instructions)
        {
            var s_Dest = s_Instruction.Destination;
            var s_DestTemp = s_Dest is { Kind: OperandKind.Temp } ? s_Dest.Index : -1;

            var s_TempSources = s_Instruction.Sources.Where(p_S => p_S.Kind == OperandKind.Temp).ToList();

            if (s_Instruction.Opcode is "dp2" or "dp3" or "dp4")
            {
                if (s_DestTemp >= 0)
                    s_FromDot.Add(s_DestTemp);

                continue;
            }

            // "1 - x": an add/mad with a literal and a NEGATED source that traces back to a dot product.
            if (s_Instruction.Opcode is "add" or "mad" &&
                s_Instruction.Sources.Any(p_S => p_S.Kind == OperandKind.Literal) &&
                s_TempSources.Any(p_S => p_S.Negate && s_FromDot.Contains(p_S.Index)))
            {
                if (s_DestTemp >= 0)
                    s_OneMinus.Add(s_DestTemp);

                continue;
            }

            // ...raised to a power: squared by a self-multiply, or through the log/exp pair fxc emits for pow().
            if (s_Instruction.Opcode == "mul" && s_TempSources.Count == 2 &&
                s_TempSources[0].Index == s_TempSources[1].Index && s_OneMinus.Contains(s_TempSources[0].Index))
                s_Fresnel = true;

            if (s_Instruction.Opcode is "log" && s_TempSources.Any(p_S => s_OneMinus.Contains(p_S.Index)))
                s_Fresnel = true;

            if (s_Instruction.Opcode == "mov" && s_DestTemp >= 0 && s_TempSources.Count == 1)
            {
                if (s_FromDot.Contains(s_TempSources[0].Index))
                    s_FromDot.Add(s_DestTemp);

                if (s_OneMinus.Contains(s_TempSources[0].Index))
                    s_OneMinus.Add(s_DestTemp);
            }
        }

        return (s_Samples, s_Fresnel);
    }
}
