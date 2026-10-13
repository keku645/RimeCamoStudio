using System;
using System.Collections.Generic;
using System.Linq;

namespace RimeShaderEditor.Translate;

/// <summary>
/// Where a pin's value lives once the frame instructions are removed: a temp register and the components to read.
/// </summary>
public sealed class PinSource
{
    public int Register { get; init; } = -1;
    public int[] Components { get; init; } = Array.Empty<int>();

    /// <summary>An output register the frame writes directly, redirected into the pin instead.</summary>
    public int RedirectedOutput { get; init; } = -1;

    /// <summary>
    /// Which component of the redirected OUTPUT the pin wants. A dedicated writer (`mad o0.w, ...`) commits
    /// the scalar itself and this is moot; a shared writer (`mov o0.xyzw, rN.xyzw` - the terrain family)
    /// delivers the whole vector, and the pin must extract its component POSITIONALLY or it silently reads
    /// lane x through the adapter - smoothness came out as the normal's red channel that way.
    /// </summary>
    public int RedirectedComponent { get; init; }

    /// <summary>An INTERPOLATOR the frame consumes directly - vN, no temp involved at all.</summary>
    /// <remarks>
    /// The no-normal-map variant: `mad o0.xyz, v2.xyzx, 0.5, 0.5` packs the vertex shader's world normal with
    /// no tangent basis and no normalize in sight, so there is no register write anywhere to capture.
    /// </remarks>
    public int InputRegister { get; init; } = -1;

    /// <summary>A literal the frame consumes directly - `mul r0.x, specScale, l(0.072)` has no producer.</summary>
    public float? LiteralValue { get; init; }

    /// <summary>
    /// An operand the frame consumes directly - a cbuffer field, read through the translator's own operand
    /// path so it lands on the same ExternalConstant node a literal translation would have produced.
    /// </summary>
    public AsmOperand? Operand { get; init; }

    /// <summary>
    /// The instruction that CONSUMED this value. Pins must be snapshotted there, not at the end of the shader:
    /// a temp is reused constantly, and on the oil drum the specular write lands in r0.y right on top of the
    /// normal's y that the frame read twenty instructions earlier.
    /// </summary>
    public int ReadAt { get; init; } = int.MaxValue;

    public bool IsSet => Register >= 0 || RedirectedOutput >= 0 || InputRegister >= 0 || LiteralValue.HasValue ||
                         Operand != null;
}

/// <summary>What a matched standard g-buffer frame yields.</summary>
public sealed class FrameMatch
{
    public HashSet<int> FrameInstructions { get; } = new();
    public PinSource Normal { get; set; } = new();
    public PinSource Diffuse { get; set; } = new();
    public PinSource Smoothness { get; set; } = new();
    public PinSource Specular { get; set; } = new();
    public float FresnelBias { get; set; } = 0.6f;
    public float FresnelExponent { get; set; } = 4f;

    /// <summary>What the shader writes into RT2.xyz - a constant, but not always zero.</summary>
    public float[] Rt2Xyz { get; set; } = { 0f, 0f, 0f };

    /// <summary>
    /// The interpolator holding the WORLD POSITION, proven by the bytecode itself: it is the register the
    /// fresnel's view vector subtracts from cameraPos (cb2[20]). -1 when the chain never showed it.
    ///
    /// This matters on contracts whose interpolator meanings are NOT measured: their fields are named
    /// Interp0..N, and a root that hardcodes `i.WorldPos` fails to compile there - while the shader itself just
    /// demonstrated which input is the world position, no semantics assumed.
    /// </summary>
    public int WorldPosInput { get; set; } = -1;

    /// <summary>The Normal pin already IS the world normal: skip the tangent transform and the normalize.</summary>
    public bool NormalWorldSpace { get; set; }

    /// <summary>
    /// Where the frame's own tangent-basis normalize LANDS (instruction index, register, lanes), when the
    /// tangent path matched. The builder republishes that value into the register file as a TangentToWorld
    /// node - authored maths is allowed to read the world normal (a raw-specular fresnel does), and the frame
    /// having consumed the instructions must not leave those registers holding the stale tangent-space value.
    /// </summary>
    public int WorldNormalAt { get; set; } = -1;

    public int WorldNormalRegister { get; set; } = -1;
    public int[] WorldNormalComponents { get; set; } = Array.Empty<int>();

    /// <summary>o1.w written as a bare literal - no fresnel anywhere. The root emits it verbatim.</summary>
    public float? SpecularPacked { get; set; }

    /// <summary>
    /// o1.w = sqrt(expression) with no recognisable fresnel product: the Specular pin carries the WHOLE
    /// expression and the root only applies the sqrt. This is what makes the weapons' cubic fresnel and the
    /// parameter-scaled speculars match the frame - their chain becomes authored subgraph instead of a decline.
    /// </summary>
    public bool SpecularNoFresnel { get; set; }

    /// <summary>
    /// The fresnel bias as a MATERIAL PARAMETER instead of a baked number: `add S, 1, -cb; mad f, S, cb` is how
    /// external_FresnelBias ships (658 uses in the corpus). Unset means FresnelBias holds the literal.
    /// </summary>
    public PinSource FresnelBiasPin { get; set; } = new();

    /// <summary>Which root variant the RT2 lighting-model byte selects.</summary>
    public string Root { get; set; } = "StandardRoot";

    // ── the terrain-2d frame's own pins (post-processed values; its root only assembles lanes) ───────────
    public PinSource NormalPacked { get; set; } = new();
    public PinSource AlbedoRooted { get; set; } = new();
    public PinSource Rt2Pin { get; set; } = new();
}

/// <summary>
/// Recognises the boilerplate that every standard BF3 surface shader shares, so a translated graph can be the
/// handful of nodes an artist would have drawn instead of one node per instruction.
///
/// The oil drum is 31 instructions and only THREE of them carry authored information - the colour map fetch, the
/// `* 4` that makes smoothness, and the `+ itself` that makes specular. The other 28 are the tangent-basis
/// transform, the normalize, the *0.5+0.5 pack, the view vector, the (1.001-N·V)^4 fresnel, the bias/scale, the
/// sqrt encodes and the RT2/RT3 constants - all of which the `StandardRoot` node emits by itself, and emits
/// bit-exactly, which is why the hand-authored 8-node graph diffed to zero against DICE's shader.
///
/// ⚠ DELIBERATELY STRICT. It matches the exact shape or it declines, and declining costs nothing but nodes,
/// whereas a loose match produces a graph that renders plausibly and is wrong. Every literal is checked.
/// </summary>
public static class FrameMatcher
{
    /// <summary>Which check turned a shader away, so widening the matcher is driven by counts and not by hunch.</summary>
    public static string LastRejection { get; private set; } = "";

    public static FrameMatch? TryMatch(List<AsmInstruction> p_Instructions)
    {
        var s_Match = new FrameMatch();
        var s_Definitions = BuildDefinitions(p_Instructions);
        LastRejection = "";

        // ⛔ RT2.w is the LIGHTING MODEL byte, not padding: 0 Standard, 1/255 Metallic, 2/255 Skin,
        // 3/255 DynamicEnvmap - measured on both sides, in the writer (a character glove writes 0.007843) and in
        // the outdoor light pass that compares against 1.5/255, 2.5/255 and 3.5/255. Demanding a plain zero here
        // turned away 126 of 230 shaders that wear this exact frame and merely shade as skin or metal, so the
        // byte is READ and picks the matching root variant instead of being required to be one value.
        //
        // ⛔⛔ EVERY check runs, and the rejection names the whole failing SET - not the first failure. The
        // first-failure histogram is the frequency trap all over again: "RT2 not constant x284" says how many
        // shaders TRIP on that check, not how many a relaxation would UNLOCK, because a shader that also fails
        // the normal check is not helped by fixing RT2 alone. The set is what set-cover needs; the movc lesson
        // (121 occurrences, 2 unlocked) is the precedent.
        var s_Failed = new List<string>();

        if (!MatchLightingModel(p_Instructions, s_Definitions, s_Match))
            s_Failed.Add("RT2 not a constant model byte");

        if (!MatchesConstant(p_Instructions, s_Definitions, 3, 0f, 0f, 0f, 1f / 255f, s_Match))
            s_Failed.Add("RT3 not vanilla");

        if (!MatchNormal(p_Instructions, s_Definitions, s_Match))
            s_Failed.Add("normal not tangent-basis");

        if (!MatchSmoothness(p_Instructions, s_Definitions, s_Match))
            s_Failed.Add("nothing writes o0.w");

        if (!MatchDiffuse(p_Instructions, s_Definitions, s_Match))
            s_Failed.Add("o1.xyz not sqrt(diffuse)");

        if (!MatchSpecular(p_Instructions, s_Definitions, s_Match))
            s_Failed.Add("o1.w not sqrt(fresnel*spec)");

        if (s_Failed.Count > 0)
        {
            // Not wearing the STANDARD frame is not the end: the distant-terrain family wears its own exact
            // shape, matched just as strictly - never as a loosening of this one.
            if (TryMatchTerrain2d(p_Instructions, s_Definitions) is { } s_Terrain)
                return s_Terrain;

            LastRejection = string.Join(" + ", s_Failed);
            return null;
        }

        KeepWhatAuthoredCodeReads(p_Instructions, s_Definitions, s_Match);
        return s_Match;
    }

    /// <summary>
    /// ⛔ AN INSTRUCTION IS FRAME ONLY IF NOTHING BUT THE FRAME READS IT. The root re-emits the frame's maths,
    /// so a frame instruction is dropped from the translation — and an AUTHORED instruction that reads its value
    /// is then left reading a register nobody wrote ("read before it was written; using 0") or, worse, a STALE
    /// one, silently. Both on the F-35B's `f35b_main` (2026-09-22): its view vector feeds the frame's N·V AND an
    /// authored rim term (`dp3 v2, V` × Scalar → the specular), and one `mul r0.xz, r0.xxyx, r0.xxyx` squares
    /// the fresnel in x (frame) and the rim in z (authored) — the rim read the normal's z left in r0.z. Such an
    /// instruction goes back to the authored side, and so, in turn, does whatever it reads.
    /// ⛔ EXCEPT THE WORLD NORMAL: GraphBuilder republishes it through a TangentToWorld node when authored code
    /// reads it (the weapons' own fresnel does). Un-framing it too rewrote six approved weapon graphs — the same
    /// value, spelled out as nodes — for nothing.
    /// </summary>
    private static void KeepWhatAuthoredCodeReads(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        for (var s_Changed = true; s_Changed;)
        {
            s_Changed = false;
            for (var i = 0; i < p_Instructions.Count; i++)
            {
                if (p_Match.FrameInstructions.Contains(i))
                    continue;

                foreach (var s_Source in p_Instructions[i].Sources)
                {
                    if (s_Source.Kind != OperandKind.Temp)
                        continue;

                    foreach (var s_Component in ComponentsRead(p_Instructions[i], s_Source))
                    {
                        var s_At = p_Definitions.Before(i, "r", s_Source.Index, s_Component);
                        if (s_At >= 0 && s_At != p_Match.WorldNormalAt && p_Match.FrameInstructions.Remove(s_At))
                            s_Changed = true;
                    }
                }
            }
        }
    }

    /// <summary>The components of a source an instruction really reads: the dot's width, or the write mask's lanes.</summary>
    private static IEnumerable<int> ComponentsRead(AsmInstruction p_Instruction, AsmOperand p_Source)
    {
        IEnumerable<int> s_Slots = p_Instruction.Opcode switch
        {
            "dp2" => new[] { 0, 1 },
            "dp3" => new[] { 0, 1, 2 },
            "dp4" => new[] { 0, 1, 2, 3 },
            _ => p_Instruction.Destination?.WriteMask is { Length: > 0 } s_Mask ? s_Mask : new[] { 0, 1, 2, 3 },
        };

        return s_Slots.Select(p_S => Component(p_Source, p_S)).Distinct();
    }

    /// <summary>
    /// The distant-terrain (2d) packing, measured across the family's own bytecode (41 of its 46 share it
    /// exactly):
    ///     mov  o0.x,    Rp.[x]        Rp = the packed normal (its mad *0.5+0.5 stays authored and visible)
    ///     mov  o0.yz,   Rs.[a b]      Rs = the rooted albedo (its sqrt stays authored and visible)
    ///     mov  o0.w,    l(1)
    ///     mov  o1.xz,   Rp.[z y]
    ///     sqrt o1.y,    spec          - or covered by a literal-zero mov
    ///     mov  o1.w,    l(1)          - possibly the same instruction as the literal o1.y
    ///     mov  o2.xyzw, Rq.[swizzle]  RT2 varies per shader, so it is a RAW pass-through pin
    /// Strict and positional; the five family members with a split o2 stay literal.
    /// </summary>
    private static FrameMatch? TryMatchTerrain2d(List<AsmInstruction> p_Instructions,
        Definitions p_Definitions)
    {
        // Three render targets exactly.
        if (p_Definitions.Last("o", 3, 0) >= 0 || p_Definitions.Last("o", 3, 3) >= 0)
            return null;

        var s_Match = new FrameMatch { Root = "Terrain2dRoot" };

        bool MovFromTemp(int p_At, out AsmInstruction p_Mov, out AsmOperand p_Source)
        {
            p_Mov = null!;
            p_Source = null!;
            if (p_At < 0 || p_Instructions[p_At].Opcode != "mov" ||
                p_Instructions[p_At].Sources.ElementAtOrDefault(0) is not
                    { Kind: OperandKind.Temp, Negate: false, Absolute: false } s_Source)
                return false;

            p_Mov = p_Instructions[p_At];
            p_Source = s_Source;
            return true;
        }

        float? LiteralAt(AsmOperand? p_Operand, int p_Component) =>
            p_Operand is { Kind: OperandKind.Literal } && p_Operand.Literal.Length > 0
                ? p_Operand.Literal[Math.Min(p_Component, p_Operand.Literal.Length - 1)]
                : null;

        var s_O0xAt = p_Definitions.Last("o", 0, 0);
        if (!MovFromTemp(s_O0xAt, out var s_O0x, out var s_PackX) ||
            s_O0x.Destination!.WriteMask.Length != 1)
            return null;

        var s_O0yzAt = p_Definitions.Last("o", 0, 1);
        if (s_O0yzAt != p_Definitions.Last("o", 0, 2) ||
            !MovFromTemp(s_O0yzAt, out var s_O0yz, out var s_Albedo) ||
            s_O0yz.Destination!.WriteMask.Length != 2)
            return null;

        var s_O0wAt = p_Definitions.Last("o", 0, 3);
        if (s_O0wAt < 0 || p_Instructions[s_O0wAt].Opcode != "mov" ||
            LiteralAt(p_Instructions[s_O0wAt].Sources.ElementAtOrDefault(0), 3) != 1f)
            return null;

        var s_O1xzAt = p_Definitions.Last("o", 1, 0);
        if (s_O1xzAt != p_Definitions.Last("o", 1, 2) ||
            !MovFromTemp(s_O1xzAt, out _, out var s_PackZy) || s_PackZy.Index != s_PackX.Index)
            return null;

        var s_O1yAt = p_Definitions.Last("o", 1, 1);
        if (s_O1yAt < 0)
            return null;

        var s_O1y = p_Instructions[s_O1yAt];
        if (s_O1y.Opcode == "sqrt" && s_O1y.Sources.ElementAtOrDefault(0) is
                { Kind: OperandKind.Temp } s_SpecSource)
        {
            s_Match.Specular = new PinSource
            {
                Register = s_SpecSource.Index, ReadAt = s_O1yAt,
                Components = new[] { Component(s_SpecSource, 1) },
            };

            s_Match.FrameInstructions.Add(s_O1yAt);
        }
        else if (s_O1y.Opcode == "mov" && LiteralAt(s_O1y.Sources.ElementAtOrDefault(0), 1) == 0f)
        {
            // Specular pin left unwired: the root's sqrt(0) folds to the same zero the mov wrote.
            s_Match.FrameInstructions.Add(s_O1yAt);
        }
        else
        {
            return null;
        }

        var s_O1wAt = p_Definitions.Last("o", 1, 3);
        if (s_O1wAt < 0 || p_Instructions[s_O1wAt].Opcode != "mov" ||
            LiteralAt(p_Instructions[s_O1wAt].Sources.ElementAtOrDefault(0), 3) != 1f)
            return null;

        var s_O2At = p_Definitions.Last("o", 2, 0);
        if (s_O2At < 0 || Enumerable.Range(1, 3).Any(p_C => p_Definitions.Last("o", 2, p_C) != s_O2At) ||
            !MovFromTemp(s_O2At, out _, out var s_Rt2))
            return null;

        // Pin components are POSITIONAL throughout: the lane a source supplies is chosen by the destination
        // component's position, never by mask ordinal.
        s_Match.NormalPacked = new PinSource
        {
            Register = s_PackX.Index, ReadAt = s_O0xAt,
            Components = new[] { Component(s_PackX, 0), Component(s_PackZy, 2), Component(s_PackZy, 0) },
        };

        s_Match.AlbedoRooted = new PinSource
        {
            Register = s_Albedo.Index, ReadAt = s_O0yzAt,
            Components = new[] { Component(s_Albedo, 1), Component(s_Albedo, 2) },
        };

        s_Match.Rt2Pin = new PinSource
        {
            Register = s_Rt2.Index, ReadAt = s_O2At,
            Components = Enumerable.Range(0, 4).Select(p_C => Component(s_Rt2, p_C)).ToArray(),
        };

        foreach (var s_At in new[] { s_O0xAt, s_O0yzAt, s_O0wAt, s_O1xzAt, s_O1wAt, s_O2At })
            s_Match.FrameInstructions.Add(s_At);

        return s_Match;
    }

    /// <summary>
    /// ⛔ REACHING definitions, not last-in-program. A temp register is rewritten constantly - the oil drum
    /// writes r0.x eleven times - so "which instruction defined this value" only means anything relative to the
    /// instruction that READS it. A program-wide table answers a different question and quietly matched the
    /// wrong producer, which showed up as the matcher never firing at all.
    /// </summary>
    private sealed class Definitions
    {
        private readonly List<AsmInstruction> m_Instructions;

        public Definitions(List<AsmInstruction> p_Instructions) => m_Instructions = p_Instructions;

        public int Before(int p_Use, string p_Prefix, int p_Index, int p_Component)
        {
            for (var i = Math.Min(p_Use, m_Instructions.Count) - 1; i >= 0; i--)
            {
                var s_Destination = m_Instructions[i].Destination;
                if (s_Destination == null)
                    continue;

                var s_Prefix = s_Destination.Kind == OperandKind.Output ? "o" : "r";
                if (s_Prefix != p_Prefix || s_Destination.Index != p_Index)
                    continue;

                var s_Mask = s_Destination.WriteMask.Length > 0 ? s_Destination.WriteMask : new[] { 0 };
                if (s_Mask.Contains(p_Component))
                    return i;
            }

            return -1;
        }

        /// <summary>The only sensible reading for an OUTPUT: the last write in the program.</summary>
        public int Last(string p_Prefix, int p_Index, int p_Component) =>
            Before(m_Instructions.Count, p_Prefix, p_Index, p_Component);
    }

    private static Definitions BuildDefinitions(List<AsmInstruction> p_Instructions) => new(p_Instructions);

    /// <summary>
    /// RT2 must still be a constant - written whole, w one of the four model bytes - but WHICH byte selects the
    /// root variant, and xyz is CARRIED rather than required to be zero. Anything computed there is genuinely
    /// outside this frame and is declined.
    ///
    /// ⛔ xyz is not always zero and demanding zero was a measured -13: the carrier props write
    /// `mov o2, l(0,0,1/255,1/255)` and terrain-adjacent surfaces write x=1. The sun pass never reads xyz, so
    /// their meaning stays unidentified - which is exactly why the value is copied through verbatim instead of
    /// interpreted. Copying an unknown is faithful; interpreting it would be invention.
    /// </summary>
    private static bool MatchLightingModel(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        var s_At = p_Definitions.Last("o", 2, 0);
        if (s_At < 0)
            return false;

        var s_Instruction = p_Instructions[s_At];
        if (s_Instruction.Opcode != "mov" || s_Instruction.Sources.Count == 0 ||
            s_Instruction.Sources[0].Kind != OperandKind.Literal)
            return false;

        // The whole register in ONE write: a partial mask means the other components come from another
        // instruction this check never looked at, and Last() only found the x writer.
        if (s_Instruction.Destination is not { } s_Destination || s_Destination.WriteMask.Length != 4)
            return false;

        var s_Literal = s_Instruction.Sources[0].Literal;
        p_Match.Rt2Xyz = new[]
        {
            s_Literal.Length > 0 ? s_Literal[0] : 0f,
            s_Literal.Length > 1 ? s_Literal[1] : 0f,
            s_Literal.Length > 2 ? s_Literal[2] : 0f,
        };

        var s_Byte = (s_Literal.Length > 3 ? s_Literal[3] : 0f) * 255f;
        var s_Rounded = (int) Math.Round(s_Byte);
        if (Math.Abs(s_Byte - s_Rounded) > 0.02f || s_Rounded is < 0 or > 3)
            return false;

        p_Match.Root = s_Rounded switch
        {
            1 => "MetallicRoot", 2 => "SkinRoot", 3 => "DynamicEnvmapRoot", _ => "StandardRoot",
        };

        p_Match.FrameInstructions.Add(s_At);
        return true;
    }

    private static bool MatchesConstant(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        int p_Target, float p_X, float p_Y, float p_Z, float p_W, FrameMatch p_Match)
    {
        var s_At = p_Definitions.Last("o", p_Target, 0);
        if (s_At < 0)
            return false;

        var s_Instruction = p_Instructions[s_At];
        if (s_Instruction.Opcode != "mov" || s_Instruction.Sources.Count == 0 ||
            s_Instruction.Sources[0].Kind != OperandKind.Literal)
            return false;

        var s_Literal = s_Instruction.Sources[0].Literal;
        var s_Wanted = new[] { p_X, p_Y, p_Z, p_W };
        for (var i = 0; i < 4; i++)
            if (Math.Abs((s_Literal.Length > i ? s_Literal[i] : 0f) - s_Wanted[i]) > 1e-5f)
                return false;

        p_Match.FrameInstructions.Add(s_At);
        return true;
    }

    /// <summary>
    /// o0.xyz = normalize(tangent-basis * n) * 0.5 + 0.5, and n is what the Normal pin gets.
    /// </summary>
    private static bool MatchNormal(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        var s_PackAt = p_Definitions.Last("o", 0, 0);
        if (s_PackAt < 0)
            return false;

        var s_Pack = p_Instructions[s_PackAt];

        // The terrain family packs into a TEMP and ships it with a final `mov o0.xyzw, rN.xyzw`. Same frame:
        // the mov is claimed as frame plumbing and the pack is the temp's own last writer before it.
        if (s_Pack.Opcode == "mov" &&
            s_Pack.Sources.ElementAtOrDefault(0) is { Kind: OperandKind.Temp, Negate: false, Absolute: false } s_Buffered &&
            Component(s_Buffered, 0) == 0 && Component(s_Buffered, 1) == 1 && Component(s_Buffered, 2) == 2)
        {
            var s_BufferedAt = p_Definitions.Before(s_PackAt, "r", s_Buffered.Index, 0);
            if (s_BufferedAt >= 0)
            {
                p_Match.FrameInstructions.Add(s_PackAt);
                s_PackAt = s_BufferedAt;
                s_Pack = p_Instructions[s_PackAt];
            }
        }

        if (s_Pack.Opcode != "mad" || !IsLiteral(s_Pack.Sources.ElementAtOrDefault(1), 0.5f) ||
            !IsLiteral(s_Pack.Sources.ElementAtOrDefault(2), 0.5f))
            return false;

        // The no-normal-map variant: `mad o0.xyz, vN.xyzx, 0.5, 0.5` packs the INTERPOLATED world normal as it
        // arrives - no tangent basis, no normalize (the vertex shader already emitted it unit-length, and the
        // fresnel downstream dots the same raw vN). 52 shaders wear the frame with exactly this normal path.
        if (s_Pack.Sources[0] is { Kind: OperandKind.Input, Negate: false, Absolute: false } s_Raw)
        {
            for (var i = 0; i < 3; i++)
                if (Component(s_Raw, i) != i)
                    return false;

            p_Match.FrameInstructions.Add(s_PackAt);
            p_Match.NormalWorldSpace = true;
            p_Match.Normal = new PinSource
            {
                InputRegister = s_Raw.Index, Components = new[] { 0, 1, 2 }, ReadAt = s_PackAt,
            };

            return true;
        }

        if (s_Pack.Sources[0].Kind != OperandKind.Temp)
            return false;

        // Tangent-basis first; when the temp's producer is NOT that pattern, the shader authored its world
        // normal in the open (the scattering rocks lerp two interpolated normals, facades blend detail maps).
        // That is not a failure to match - the authored maths becomes the subgraph feeding a WORLD-space pin,
        // and only the pack itself is claimed as frame. Nothing about the value is assumed; the sweep's diff
        // stays the referee.
        if (!MatchTangentNormal(p_Instructions, p_Definitions, p_Match, s_Pack, s_PackAt))
        {
            p_Match.NormalWorldSpace = true;
            p_Match.FrameInstructions.Add(s_PackAt);
            p_Match.Normal = new PinSource
            {
                Register = s_Pack.Sources[0].Index, ReadAt = s_PackAt,
                Components = new[]
                {
                    Component(s_Pack.Sources[0], 0), Component(s_Pack.Sources[0], 1),
                    Component(s_Pack.Sources[0], 2),
                },
            };
        }

        return true;
    }

    /// <summary>The measured rigid-mesh normal path: three basis dot products, a normalize, then the pack.</summary>
    private static bool MatchTangentNormal(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match, AsmInstruction s_Pack, int s_PackAt)
    {

        // The normalize: mul <normalized>, rsq(dp3(v, v)), v
        // ⛔ Component(source, 0), not 0 - the FOURTH bite of the positional law. The warehouse normalizes into
        // r0.YZW and packs r0.yzwy; asking who wrote component x found an unrelated sample, the tangent path
        // declined a textbook tangent-basis shader, and the world fallback then mangled it.
        var s_ScaleAt = p_Definitions.Before(s_PackAt, "r", s_Pack.Sources[0].Index,
            Component(s_Pack.Sources[0], 0));
        if (s_ScaleAt < 0 || p_Instructions[s_ScaleAt].Opcode != "mul")
            return false;

        var s_Scale = p_Instructions[s_ScaleAt];
        var s_Rsq = s_Scale.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Temp &&
            p_Definitions.Before(s_ScaleAt, "r", p_S.Index, p_S.Swizzle.FirstOrDefault()) is var s_At && s_At >= 0 &&
            p_Instructions[s_At].Opcode == "rsq");

        var s_Vector = s_Scale.Sources.FirstOrDefault(p_S => p_S != s_Rsq && p_S.Kind == OperandKind.Temp);
        if (s_Rsq == null || s_Vector == null)
            return false;

        var s_RsqAt = p_Definitions.Before(s_ScaleAt, "r", s_Rsq.Index, s_Rsq.Swizzle.FirstOrDefault());
        var s_LengthAt = p_Definitions.Before(s_RsqAt, "r", p_Instructions[s_RsqAt].Sources[0].Index,
            p_Instructions[s_RsqAt].Sources[0].Swizzle.FirstOrDefault());

        if (s_LengthAt < 0 || p_Instructions[s_LengthAt].Opcode != "dp3")
            return false;

        // The three basis dot products, one per component, each against a different interpolator. The
        // components the vector operand supplies are chosen by the SCALE's destination positions - the same
        // positional law again, because the normalize is free to live in .yzw.
        var s_ScalePositions = s_Scale.Destination is { WriteMask.Length: 3 }
            ? s_Scale.Destination.WriteMask
            : new[] { 0, 1, 2 };

        var s_Rows = new int[3];
        for (var i = 0; i < 3; i++)
        {
            s_Rows[i] = p_Definitions.Before(s_ScaleAt, "r", s_Vector.Index,
                Component(s_Vector, s_ScalePositions[i]));
            if (s_Rows[i] < 0 || p_Instructions[s_Rows[i]].Opcode != "dp3")
                return false;
        }

        var s_Source = p_Instructions[s_Rows[0]].Sources
            .FirstOrDefault(p_S => p_S.Kind == OperandKind.Temp);

        if (s_Source == null)
            return false;

        // Every row must read the SAME tangent-space normal, against interpolators 1, 2 and 3 in order.
        // ⛔ Nothing is added to FrameInstructions until EVERY check has passed: this path now has a fallback
        // (the authored-world-normal variant), and a half-committed set would mark authored dp3s as frame and
        // silently drop them from the translation.
        for (var i = 0; i < 3; i++)
        {
            var s_Row = p_Instructions[s_Rows[i]];
            if (s_Row.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Temp) is not { } s_Normal ||
                s_Normal.Index != s_Source.Index)
                return false;

            if (s_Row.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Input) is not { } s_Interpolator ||
                s_Interpolator.Index != 2 + i)
                return false;
        }

        foreach (var s_Row in s_Rows)
            p_Match.FrameInstructions.Add(s_Row);

        p_Match.FrameInstructions.Add(s_PackAt);
        p_Match.FrameInstructions.Add(s_ScaleAt);
        p_Match.FrameInstructions.Add(s_RsqAt);
        p_Match.FrameInstructions.Add(s_LengthAt);
        p_Match.Normal = new PinSource
        {
            Register = s_Source.Index, ReadAt = s_Rows.Min(),
            Components = new[] { Component(s_Source, 0), Component(s_Source, 1), Component(s_Source, 2) },
        };

        // Where the normalized world normal lands, so the builder can republish it for authored readers.
        p_Match.WorldNormalAt = s_ScaleAt;
        p_Match.WorldNormalRegister = s_Scale.Destination!.Index;
        p_Match.WorldNormalComponents = s_Scale.Destination.WriteMask.Length == 3
            ? s_Scale.Destination.WriteMask
            : new[] { 0, 1, 2 };

        return true;
    }

    /// <summary>o0.w is the smoothness, taken as whatever expression writes it.</summary>
    private static bool MatchSmoothness(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        var s_At = p_Definitions.Last("o", 0, 3);
        if (s_At < 0)
            return false;

        p_Match.Smoothness = new PinSource { RedirectedOutput = s_At, ReadAt = s_At, RedirectedComponent = 3 };
        return true;
    }

    /// <summary>o1.xyz = sqrt(diffuse): the pin is the sqrt's source.</summary>
    private static bool MatchDiffuse(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        var s_At = p_Definitions.Last("o", 1, 0);
        if (s_At < 0 || p_Instructions[s_At].Opcode != "sqrt")
            return false;

        var s_Source = p_Instructions[s_At].Sources[0];
        if (s_Source.Kind != OperandKind.Temp)
            return false;

        p_Match.FrameInstructions.Add(s_At);
        p_Match.Diffuse = new PinSource
        {
            Register = s_Source.Index, ReadAt = s_At,
            Components = new[] { Component(s_Source, 0), Component(s_Source, 1), Component(s_Source, 2) },
        };

        return true;
    }

    /// <summary>
    /// o1.w = sqrt(specScale * specular), specScale = mad(fresnel, scale, bias),
    /// fresnel = saturate(1.001 - dot(N, V)) raised by repeated squaring.
    /// </summary>
    private static bool MatchSpecular(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        FrameMatch p_Match)
    {
        var s_SqrtAt = p_Definitions.Last("o", 1, 3);
        if (s_SqrtAt < 0)
            return false;

        // `mov o1.w, l(K)` - a packed constant, no fresnel anywhere in the shader (sqrt(0.05) ships as
        // 0.223607, and plenty of shaders write a plain 0). Carried verbatim; the root then emits no view
        // vector at all, which is also what lets these match on contracts that never interpolate a world
        // position.
        if (p_Instructions[s_SqrtAt] is { Opcode: "mov" } s_Packed &&
            s_Packed.Sources.ElementAtOrDefault(0) is { Kind: OperandKind.Literal } s_Constant &&
            s_Constant.Literal.Length > 0)
        {
            // Position 3 for a multi-component literal - the destination is component w. l(0) broadcasts.
            var s_Value = s_Constant.Literal.Length == 1
                ? s_Constant.Literal[0]
                : s_Constant.Literal[Math.Min(3, s_Constant.Literal.Length - 1)];

            p_Match.FrameInstructions.Add(s_SqrtAt);
            p_Match.SpecularPacked = s_Constant.Negate ? -s_Value : s_Value;
            return true;
        }

        if (p_Instructions[s_SqrtAt].Opcode != "sqrt")
            return false;

        // The whole expression as the pin, when the standard fresnel product is not there to collapse. The
        // sqrt is the only instruction claimed; everything feeding it translates as authored nodes.
        bool TakeRaw()
        {
            var s_Source = p_Instructions[s_SqrtAt].Sources[0];
            if (s_Source.Kind != OperandKind.Temp)
                return false;

            p_Match.FrameInstructions.Add(s_SqrtAt);
            p_Match.SpecularNoFresnel = true;
            p_Match.Specular = new PinSource
            {
                Register = s_Source.Index, Components = new[] { Component(s_Source, 3) }, ReadAt = s_SqrtAt,
            };

            return true;
        }

        var s_Product = p_Instructions[s_SqrtAt].Sources[0];

        // ⛔⛔ POSITION 3, NOT 0 - the third bite of the same law: the component a source supplies is chosen by
        // the DESTINATION component's xyzw position. The drum writes its specular with a scalar `sqrt o1.w, r.x`
        // where the two readings coincide, so this matched; the metal barrier fuses all four channels into ONE
        // `sqrt o1.xyzw, r0.yzwx`, and reading position 0 walked the ALBEDO lane, found its sample where a mul
        // was required, and declined a shader wearing the exact frame - 54 nodes instead of 10, visibly so.
        var s_ProductAt = p_Definitions.Before(s_SqrtAt, "r", s_Product.Index, Component(s_Product, 3));
        if (s_ProductAt < 0 || p_Instructions[s_ProductAt].Opcode != "mul")
            return TakeRaw();

        // One side is the fresnel scale chain, the other is the specular value.
        var s_Multiply = p_Instructions[s_ProductAt];
        var s_ScaleIndex = -1;
        for (var i = 0; i < 2; i++)
        {
            var s_Operand = s_Multiply.Sources[i];
            if (s_Operand.Kind != OperandKind.Temp)
                continue;

            var s_At = p_Definitions.Before(s_ProductAt, "r", s_Operand.Index, Component(s_Operand, 0));
            if (s_At >= 0 && p_Instructions[s_At].Opcode == "mad" &&
                p_Instructions[s_At].Sources.ElementAtOrDefault(1) is { Kind: OperandKind.Literal } s_Scale &&
                p_Instructions[s_At].Sources.ElementAtOrDefault(2) is { Kind: OperandKind.Literal } s_Bias)
            {
                s_ScaleIndex = i;
                p_Match.FresnelBias = s_Bias.Literal.FirstOrDefault();

                // A scale/bias pair that does not sum to 1, or a chain that is not the plain fresnel (the
                // weapons preset cubes it and adds an external bias afterwards): the pin takes the whole
                // expression instead. CollectFresnel is transactional, so a failed walk marks nothing.
                if (Math.Abs(s_Scale.Literal.FirstOrDefault() + s_Bias.Literal.FirstOrDefault() - 1f) > 1e-4f)
                    return TakeRaw();

                if (!CollectFresnel(p_Instructions, p_Definitions, s_At, p_Match))
                    return TakeRaw();

                break;
            }

            // The bias as a PARAMETER: `mad f, S, cb` with S produced by `add S, 1, -cb` over the SAME cbuffer
            // field - which is exactly lerp(bias, 1, f) with bias = external_FresnelBias. The pin gets the
            // field, through the same operand path a literal translation reads cbuffers with.
            if (s_At >= 0 && p_Instructions[s_At].Opcode == "mad" &&
                p_Instructions[s_At].Sources.ElementAtOrDefault(1) is { Kind: OperandKind.Temp } s_ScaleTemp &&
                p_Instructions[s_At].Sources.ElementAtOrDefault(2) is { Kind: OperandKind.ConstBuffer, Negate: false } s_BiasField)
            {
                var s_OneMinusAt = p_Definitions.Before(s_At, "r", s_ScaleTemp.Index, Component(s_ScaleTemp, 0));
                if (s_OneMinusAt < 0 || p_Instructions[s_OneMinusAt] is not { Opcode: "add" } s_OneMinus)
                    continue;

                // Either order: add S, l(1), -cb  or  add S, -cb, l(1). The cb must be the SAME field.
                var s_One = s_OneMinus.Sources.FirstOrDefault(p_O => IsLiteral(p_O, 1f) && !p_O.Negate);
                var s_Subtracted = s_OneMinus.Sources.FirstOrDefault(p_O =>
                    p_O is { Kind: OperandKind.ConstBuffer, Negate: true } &&
                    p_O.Index == s_BiasField.Index && p_O.Element == s_BiasField.Element &&
                    Component(p_O, 0) == Component(s_BiasField, 0));

                if (s_One == null || s_Subtracted == null)
                    continue;

                s_ScaleIndex = i;

                if (!CollectFresnel(p_Instructions, p_Definitions, s_At, p_Match))
                    return TakeRaw();

                // Committed only after the walk succeeded, for the same reason the walk is transactional.
                p_Match.FresnelBiasPin = new PinSource { Operand = s_BiasField, ReadAt = s_At };
                p_Match.FrameInstructions.Add(s_OneMinusAt);
                break;
            }
        }

        if (s_ScaleIndex < 0)
            return TakeRaw();

        var s_Specular = s_Multiply.Sources[1 - s_ScaleIndex];

        // `mul r0.x, specScale, l(0.072)` - a specular that IS its number. There is no producer to capture, so
        // the pin carries the value itself; 45 shaders wear the frame with exactly this.
        if (s_Specular is { Kind: OperandKind.Literal } && s_Specular.Literal.Length > 0)
        {
            p_Match.FrameInstructions.Add(s_SqrtAt);
            p_Match.FrameInstructions.Add(s_ProductAt);
            p_Match.Specular = new PinSource
            {
                LiteralValue = s_Specular.Negate ? -s_Specular.Literal[0] : s_Specular.Literal[0],
                ReadAt = s_ProductAt,
            };

            return true;
        }

        if (s_Specular.Kind != OperandKind.Temp)
            return false;

        p_Match.FrameInstructions.Add(s_SqrtAt);
        p_Match.FrameInstructions.Add(s_ProductAt);
        p_Match.Specular = new PinSource
        {
            Register = s_Specular.Index, Components = new[] { Component(s_Specular, 0) }, ReadAt = s_ProductAt,
        };

        return true;
    }

    /// <summary>
    /// Walks the fresnel chain back to its saturate, marking it as frame and reading the exponent.
    /// TRANSACTIONAL: nothing lands in FrameInstructions unless the whole walk succeeds, because a failed walk
    /// now falls back to the raw-specular pin - and that pin NEEDS the very instructions a half-committed walk
    /// would have dropped as frame.
    /// </summary>
    private static bool CollectFresnel(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        int p_MadAt, FrameMatch p_Match)
    {
        var s_Marked = new List<int> { p_MadAt };
        var s_Operand = p_Instructions[p_MadAt].Sources[0];
        var s_Exponent = 1f;
        var s_Cursor = p_MadAt;
        var s_Clamped = false;

        for (var s_Guard = 0; s_Guard < 8; s_Guard++)
        {
            if (s_Operand.Kind != OperandKind.Temp)
                return false;

            var s_At = p_Definitions.Before(s_Cursor, "r", s_Operand.Index, Component(s_Operand, 0));
            if (s_At < 0)
                return false;

            var s_Instruction = p_Instructions[s_At];

            // Repeated squaring is how fxc builds an even power.
            if (s_Instruction.Opcode == "mul" && s_Instruction.Sources.Count == 2 &&
                s_Instruction.Sources[0].Kind == OperandKind.Temp &&
                s_Instruction.Sources[1].Kind == OperandKind.Temp &&
                s_Instruction.Sources[0].Index == s_Instruction.Sources[1].Index &&
                Component(s_Instruction.Sources[0], 0) == Component(s_Instruction.Sources[1], 0))
            {
                s_Exponent *= 2f;
                s_Marked.Add(s_At);
                s_Operand = s_Instruction.Sources[0];
                s_Cursor = s_At;
                continue;
            }

            // A clamp AFTER the powering: `min(f^n, 1)`. Some compiles spell the fresnel that way instead of
            // saturating the base.
            if (s_Instruction.Opcode == "min" && IsLiteral(s_Instruction.Sources.ElementAtOrDefault(1), 1f))
            {
                s_Clamped = true;
                s_Marked.Add(s_At);
                s_Operand = s_Instruction.Sources[0];
                s_Cursor = s_At;
                continue;
            }

            // The base: (1.001 - N·V), saturated - OR bare, when the min(,1) above did the clamping. The two
            // spellings are value-identical everywhere reachable: the input is 1.001-dot of unit-ish vectors,
            // so it lives in [0.001, 2.001] - never negative, which is the only region where they differ - and
            // above 1 both roads end at exactly 1. The root keeps emitting the saturate form.
            if (s_Instruction.Opcode == "add" && (s_Instruction.Saturate || s_Clamped) &&
                IsLiteral(s_Instruction.Sources.ElementAtOrDefault(1), 1.001f))
            {
                foreach (var s_Index in s_Marked)
                    p_Match.FrameInstructions.Add(s_Index);

                p_Match.FrameInstructions.Add(s_At);
                MarkViewChain(p_Instructions, p_Definitions, s_Instruction.Sources[0], s_At, p_Match);
                p_Match.FresnelExponent = s_Exponent;
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>Marks dot(N, V) and the normalize of V that feeds it - all of it is frame.</summary>
    private static void MarkViewChain(List<AsmInstruction> p_Instructions, Definitions p_Definitions,
        AsmOperand p_Operand, int p_UsedAt, FrameMatch p_Match)
    {
        // ⛔ Each operand carries the instruction that READS it, so the walk uses reaching definitions. Walking
        // with end-of-program definitions marked `add r0.y, r1.z, r1.z` - the authored specular - as frame,
        // because by the end r0.y had been reused; the pin then picked up a stale register and the graph came
        // out 245/255 wrong while looking entirely sensible.
        var s_Pending = new Stack<(AsmOperand Operand, int ReadBy)>();
        s_Pending.Push((p_Operand, p_UsedAt));

        for (var s_Guard = 0; s_Guard < 32 && s_Pending.Count > 0; s_Guard++)
        {
            var (s_Current, s_ReadBy) = s_Pending.Pop();
            if (s_Current.Kind != OperandKind.Temp)
                continue;

            // ⛔ The N side of the N·V dot is the NORMAL PIN'S value, and its producers are AUTHORED, not
            // frame. Without this stop, a world-space normal's maths - the basis dot products, a scattering
            // lerp - got marked as frame, dropped from the translation, and the pin then captured registers
            // nobody had written: nine shaders came out 150-235/255 wrong in one sweep.
            // Only reads BY THE DOT are guarded: the dot's own result often lands in a component the pin also
            // names, and guarding every read of it left the whole view chain unmarked - ten dead nodes per
            // shader, recomputing a fresnel nobody consumes.
            if (p_Instructions[s_ReadBy].Opcode == "dp3" &&
                p_Match.Normal.Register == s_Current.Index &&
                p_Match.Normal.Components.Contains(Component(s_Current, 0)))
                continue;

            var s_At = p_Definitions.Before(s_ReadBy, "r", s_Current.Index, Component(s_Current, 0));
            if (s_At < 0 || !p_Match.FrameInstructions.Add(s_At))
                continue;

            // Stop at anything that is not part of the view-vector maths: the camera-relative subtract, its
            // normalize, and the dot with the world normal.
            var s_Instruction = p_Instructions[s_At];
            if (s_Instruction.Opcode is not ("dp3" or "rsq" or "mul" or "add"))
            {
                p_Match.FrameInstructions.Remove(s_At);
                continue;
            }

            // The camera-relative subtract names the world position: `add r, -vN, cb2[20]` reads cameraPos, so
            // vN IS the world position in THIS shader - recorded as evidence, not assumed from a family.
            if (s_Instruction.Opcode == "add" &&
                s_Instruction.Sources.Any(p_S => p_S is { Kind: OperandKind.ConstBuffer, Index: 2, Element: 20 }) &&
                s_Instruction.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Input) is { } s_WorldPos)
                p_Match.WorldPosInput = s_WorldPos.Index;

            foreach (var s_Source in s_Instruction.Sources)
                s_Pending.Push((s_Source, s_At));
        }
    }

    private static int Component(AsmOperand p_Operand, int p_Slot) =>
        p_Operand.Swizzle.Length == 0
            ? Math.Min(p_Slot, 3)
            : p_Operand.Swizzle[Math.Min(p_Slot, p_Operand.Swizzle.Length - 1)];

    private static bool IsLiteral(AsmOperand? p_Operand, float p_Value) =>
        p_Operand is { Kind: OperandKind.Literal } && p_Operand.Literal.Length > 0 &&
        Math.Abs(p_Operand.Literal[0] - p_Value) < 1e-5f;
}
