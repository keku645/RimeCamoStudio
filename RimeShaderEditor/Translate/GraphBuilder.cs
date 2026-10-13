using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.Translate;

/// <summary>
/// Turns a parsed disassembly into a node graph that computes the same thing.
///
/// It does NOT recover the graph DICE authored - that is destroyed at bake time and many graphs share one
/// bytecode. It builds ONE equivalent graph, mechanically, so that `--shaderdiff` can prove the equivalence
/// against the game's own shader instead of anyone having to trust it.
///
/// Values are tracked PER SCALAR. DXBC writes partial destinations (r0.xy) and reads arbitrary swizzles
/// (r0.xyxx); tracking whole vectors would need a merge for every partial write, which is exactly where a
/// translator gets subtly wrong. Per-scalar has no special cases, at the price of a much bigger graph - which is
/// the trade that was asked for: fidelity over prettiness.
///
/// One economy keeps the node count sane: the palette's arithmetic nodes are float4 and component-wise, so a
/// scalar fed into one is replicated across all four lanes and the result stays uniform. A uniform value can
/// therefore be used as a scalar directly, with no extraction node between operations. Only genuinely
/// non-uniform sources - a texture sample, an interpolator - need a MixOut to pick a channel out.
/// </summary>
public sealed class GraphBuilder
{
    /// <summary>
    /// One scalar, located as a LANE of some node's output port. Lane -1 means the port is uniform across all
    /// four channels (a constant, or a MixOut that already picked one), so it can be read as a scalar directly.
    ///
    /// The lane is what makes vector emission possible. Tracking stays per-scalar - that is what made the
    /// translation provably correct - but knowing WHICH CHANNEL of which node a scalar lives in lets four
    /// consecutive scalars be recognised as one vector again, so `mul r0.xyz, a, b` emits ONE Multiply instead
    /// of three. On the oil drum that is the difference between 126 nodes and a graph a person can read.
    /// </summary>
    private readonly record struct Value(string NodeId, string Port, int Lane = -1);

    private readonly ShaderGraph m_Graph = new();
    private readonly Dictionary<string, Value?[]> m_Registers = new();
    private readonly Dictionary<string, Value> m_Constants = new();
    private readonly Dictionary<(string Source, int Component), Value> m_Extracted = new();
    private readonly ShaderContract m_Contract;
    private int m_Column;
    private int m_Row;

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Opcodes this build had no node for. Kept apart from the warnings because they are the one failure that
    /// makes the graph WRONG rather than merely ugly: the destination silently keeps its previous value, so the
    /// graph still compiles and still renders. The caller has to be able to say so out loud.
    /// </summary>
    public HashSet<string> UntranslatedOpcodes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Constant-buffer fields read by the shader that this editor has no node for, so they came through as 0.
    /// They break equivalence exactly as thoroughly as a missing opcode does, and just as quietly — the graph
    /// still compiles and still renders, it just shades with a zero where the game has a real value. Tracked
    /// separately so the caller can never report "every instruction had a node" while one of these is set.
    /// </summary>
    public HashSet<string> UnmodelledConstants { get; } = new(StringComparer.Ordinal);

    public GraphBuilder(ShaderContract? p_Contract = null) =>
        m_Contract = p_Contract ?? ShaderContract.RigidMeshDefault();

    /// <summary>Instructions already accounted for by a recognised idiom, so Translate must skip them.</summary>
    private readonly HashSet<int> m_Consumed = new();

    public ShaderGraph Build(List<AsmInstruction> p_Instructions, string p_Target, string p_Name)
    {
        m_Graph.Name = p_Name;
        m_Graph.TargetShader = p_Target;

        // The numbering this graph's explicit interpolator indices speak: the contract it is translated
        // against. A graph translated from a probe solution stores the SHIFTED indices verbatim, and the
        // emitter must not shift them again when it compiles back into that same family.
        m_Graph.TranslatedFamily = m_Contract?.Family.ToString();

        // A shader that wears the standard g-buffer frame gets the artist's graph - a few material nodes into a
        // StandardRoot - instead of one node per instruction. Anything else falls back to the faithful literal
        // translation, which is why declining to match costs nothing.
        m_Frame = FrameMatcher.TryMatch(p_Instructions);
        FrameRejection = m_Frame == null ? FrameMatcher.LastRejection : "";

        // The alpha-to-coverage idiom (vegetation alpha test): recognised BEFORE the literal walk, because
        // its terminal write lands in `oMask` — a per-sample system output no per-instruction node can
        // express, so without the fold the whole coverage path is dropped with a warning and an alpha-tested
        // surface turns into solid squares. Raw-root graphs only: the standard frame has no coverage pin.
        if (m_Frame == null)
            TryMatchAlphaCoverage(p_Instructions);

        for (var i = 0; i < p_Instructions.Count; i++)
        {
            // ⛔ Snapshot BEFORE the instruction runs. A pin's value is whatever its consumer would have read at
            // that point, and temps are reused: on the oil drum the specular write lands in r0.y on top of the
            // normal's y, twenty instructions after the frame consumed it. Reading pins at the end gave a graph
            // that was 245/255 wrong while looking perfectly reasonable.
            if (m_Frame != null)
                SnapshotPins(i, p_Instructions);

            if (m_Consumed.Contains(i) || m_Frame != null && m_Frame.FrameInstructions.Contains(i))
            {
                // ⛔ The frame consuming its tangent-basis normalize must not leave the register file holding
                // the STALE tangent-space normal. Authored maths is allowed to read the world normal - the
                // weapons' own fresnel does - and without this it silently read the pre-basis value: RT1.w came
                // out 89/255 wrong on a shader that had just gained the raw-specular frame. Republished lazily,
                // only when some non-frame instruction actually reads those lanes.
                if (m_Frame != null && i == m_Frame.WorldNormalAt &&
                    AuthoredReadOf(p_Instructions, i + 1, m_Frame.WorldNormalRegister,
                        m_Frame.WorldNormalComponents) &&
                    m_Pins.TryGetValue("Normal", out var s_TangentNormal))
                {
                    var s_ToWorld = NewNode("TangentToWorld");
                    m_Graph.Connect(s_TangentNormal.NodeId, s_TangentNormal.Port, s_ToWorld.Id, "Input");

                    var s_WorldFile = RegisterFile("r" + m_Frame.WorldNormalRegister);
                    for (var s_Lane = 0; s_Lane < 3; s_Lane++)
                        s_WorldFile[m_Frame.WorldNormalComponents[s_Lane]] = new Value(s_ToWorld.Id, "Out", s_Lane);
                }

                continue;
            }

            if (TryNormalMap(p_Instructions, i))
                continue;

            if (TryTangentBasis(p_Instructions, i))
                continue;

            Translate(p_Instructions[i]);
        }

        if (m_Frame != null)
        {
            SnapshotPins(p_Instructions.Count, p_Instructions);
            AttachStandardRoot();
        }
        else
        {
            AttachRoot();
        }

        // Fold hand-expanded idioms (normalize, lerp, 1-x, UV interpolators) back into the nodes an author
        // would draw. Runs before layout so the fused nodes are placed by their real dataflow.
        GraphPeepholes.Run(m_Graph, m_Contract);

        // Placed by dataflow rather than by the running grid the nodes were created on: a texture node is three
        // times taller than a Multiply, so a fixed row height stacked them on top of each other.
        View.GraphCanvas.AutoLayout(m_Graph);
        return m_Graph;
    }

    private readonly Dictionary<string, (string NodeId, string Port)> m_Pins = new(StringComparer.Ordinal);

    private void SnapshotPins(int p_At, List<AsmInstruction> p_Instructions)
    {
        Capture("Normal", m_Frame!.Normal, p_At, p_Instructions);
        Capture("Diffuse", m_Frame.Diffuse, p_At, p_Instructions);
        Capture("Specular", m_Frame.Specular, p_At, p_Instructions);
        Capture("Smoothness", m_Frame.Smoothness, p_At, p_Instructions);
        Capture("FresnelBias", m_Frame.FresnelBiasPin, p_At, p_Instructions);

        // The terrain-2d frame's own pins; unset on every other frame, where Capture is a no-op.
        Capture("NormalPacked", m_Frame.NormalPacked, p_At, p_Instructions);
        Capture("AlbedoRooted", m_Frame.AlbedoRooted, p_At, p_Instructions);
        Capture("Rt2", m_Frame.Rt2Pin, p_At, p_Instructions);
    }

    private void Capture(string p_Port, PinSource p_Pin, int p_At, List<AsmInstruction> p_Instructions)
    {
        if (!p_Pin.IsSet || p_Pin.ReadAt != p_At || m_Pins.ContainsKey(p_Port))
            return;

        // A specular that IS its number: the pin gets the constant, there is nothing else to trace.
        if (p_Pin.LiteralValue is { } s_Literal)
        {
            var s_Constant = Constant(s_Literal);
            m_Pins[p_Port] = (s_Constant.NodeId, s_Constant.Port);
            return;
        }

        // A cbuffer field consumed by the frame itself - external_FresnelBias. Read through the translator's
        // own operand path so it lands on the same ExternalConstant node a literal translation would produce.
        if (p_Pin.Operand is { } s_PinOperand)
        {
            var s_Value = Scalar(ReadRaw(s_PinOperand, 0));
            m_Pins[p_Port] = (s_Value.NodeId, s_Value.Port);
            return;
        }

        // A normal consumed straight from an interpolator: read it through the same path a translated `vN`
        // read takes, so the node and its ISGN-derived field name come out identical to the literal translation.
        if (p_Pin.InputRegister >= 0)
        {
            var s_Operand = new AsmOperand
            {
                Kind = OperandKind.Input, Index = p_Pin.InputRegister, Swizzle = new[] { 0, 1, 2 },
            };

            var s_Read = ReadVector(s_Operand, new[] { 0, 1, 2 });
            m_Pins[p_Port] = (s_Read.NodeId, s_Read.Port);
            return;
        }

        // An expression the frame wrote straight to an output register is re-run here so its value reaches the
        // pin instead of a render target the root now owns.
        if (p_Pin.RedirectedOutput >= 0)
        {
            m_Redirect = ("pin", 0);
            Translate(p_Instructions[p_Pin.RedirectedOutput]);
            m_Redirect = null;

            if (RegisterFile("pin0")[0] is { } s_Written)
            {
                var s_Value = s_Written;

                // A dedicated writer (`mad o0.w, ...`) commits the scalar itself. A SHARED writer
                // (`mov o0.xyzw, rN.xyzw` - the terrain family) delivers the whole vector, and the pin's
                // component must be extracted POSITIONALLY: without this the scalar pin read lane x through
                // the adapter, and smoothness rendered as the normal's red channel.
                var s_Mask = p_Instructions[p_Pin.RedirectedOutput].Destination?.WriteMask
                             ?? Array.Empty<int>();
                if (s_Mask.Length > 1 && s_Written.Lane >= 0)
                {
                    var s_Slot = Array.IndexOf(s_Mask, p_Pin.RedirectedComponent);
                    if (s_Slot < 0)
                        return;

                    s_Value = Scalar(new Value(s_Written.NodeId, s_Written.Port, s_Slot));
                }

                m_Pins[p_Port] = (s_Value.NodeId, s_Value.Port);
            }

            return;
        }

        var s_Slots = RegisterFile("r" + p_Pin.Register);
        var s_Values = p_Pin.Components.Select(p_C => s_Slots[p_C]).ToList();
        if (s_Values.Any(p_V => p_V == null))
            return;

        var s_Assembled = Assemble(s_Values.Select(p_V => p_V!.Value).ToList());
        m_Pins[p_Port] = (s_Assembled.NodeId, s_Assembled.Port);
    }

    private FrameMatch? m_Frame;

    /// <summary>True when the shader was recognised as a standard surface and got the compact graph.</summary>
    public bool FrameRecognised => m_Frame != null;

    /// <summary>Why the standard frame was declined, empty when it matched.</summary>
    public string FrameRejection { get; private set; } = "";

    /// <summary>
    /// Wires the material values into a StandardRoot, which re-emits the whole packing frame by itself - and
    /// emits it bit-exactly, which is the property the hand-authored oil drum graph proved.
    /// </summary>
    private void AttachStandardRoot()
    {
        var s_Root = NewNode(m_Frame!.Root);

        // The frame variants the root has to reproduce bit-exactly: a normal that never went through the
        // tangent basis, an RT2.xyz that is constant but not zero, and a specular that is a packed literal.
        // All root PARAMS, not new roots - the packing is otherwise identical.
        if (m_Frame.NormalWorldSpace)
            s_Root.Params["NormalSpace"] = "World";

        if (m_Frame.SpecularPacked is { } s_Packed)
            s_Root.Params["PackedSpecular"] = s_Packed.ToString("0.0######", CultureInfo.InvariantCulture);

        if (m_Frame.SpecularNoFresnel)
            s_Root.Params["SpecularMode"] = "Raw";

        if (m_Frame.Rt2Xyz.Any(p_V => p_V != 0f))
            s_Root.Params["Rt2Xyz"] = string.Join(",",
                m_Frame.Rt2Xyz.Select(p_V => p_V.ToString("0.0######", CultureInfo.InvariantCulture)));

        // Where the view vector's world position lives, as the TEXCOORD index the contract's field naming uses.
        // Only needed on contracts whose interpolators are unnamed - on rigid meshes FieldNameFor gives the
        // measured `WorldPos` back and the emission is unchanged.
        if (m_Frame.WorldPosInput >= 0 &&
            m_Contract.Inputs.FirstOrDefault(p_I => p_I.Register == m_Frame.WorldPosInput) is { } s_WorldPos &&
            !s_WorldPos.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
            s_Root.Params["WorldPosInterp"] = s_WorldPos.Index.ToString();

        // The fresnel constants are INPUT PINS on the root, so they arrive as scalar nodes rather than params.
        // The bias only as a constant when the shader did not carry it as a parameter - a captured
        // external_FresnelBias pin takes the wire instead, in the m_Pins loop below.
        foreach (var (s_Port, s_Value) in new[]
                 {
                     ("FresnelBias", m_Frame.FresnelBias), ("FresnelExponent", m_Frame.FresnelExponent),
                 })
        {
            // Root variants only get the pins they declare - the terrain root carries no fresnel.
            if (m_Pins.ContainsKey(s_Port) || s_Root.Def.FindInput(s_Port) == null)
                continue;

            var s_Constant = Constant(s_Value);
            m_Graph.Connect(s_Constant.NodeId, s_Constant.Port, s_Root.Id, s_Port);
        }

        foreach (var (s_Port, s_Source) in m_Pins)
        {
            if (s_Root.Def.FindInput(s_Port) == null)
                continue;

            var s_Routed = s_Port == "Diffuse" ? WithDiscards(s_Source) : s_Source;
            m_Graph.Connect(s_Routed.NodeId, s_Routed.Port, s_Root.Id, s_Port);
        }
    }

    /// <summary>Set while re-running an instruction whose destination must land in a pin, not in its register.</summary>
    private (string Key, int Index)? m_Redirect;

    /// <summary>
    /// Collapses the DXT5nm normal-map decode - the single most repeated block in the game's shaders, measured in
    /// 265 of the 417 that have a g-buffer pass - into the one palette node that already does exactly it.
    ///
    ///     sample rN.xyz, uv, tM.xywz          (X in ALPHA: the BF3 DXT5nm layout)
    ///     mul    rN.x, rN.z, rN.x             (optional: A * R)
    ///     mad    rN.xy, rN.xyxx, l(2,2), l(-1,-1)
    ///     dp2    rN.w, rN.xyxx, rN.xyxx
    ///     add    rN.w, -rN.w, l(1.0)
    ///     max    rN.w, rN.w, l(0.0)           (optional)
    ///     sqrt   rN.z, rN.w
    ///
    /// Matched only when the instructions are CONSECUTIVE and the scratch .w is dead afterwards. Both conditions
    /// are conservative on purpose: a missed idiom costs nodes, a wrongly matched one costs correctness, and the
    /// asymmetry is the whole reason to be strict here.
    /// </summary>
    private bool TryNormalMap(List<AsmInstruction> p_Instructions, int p_Start)
    {
        var s_Sample = p_Instructions[p_Start];
        if (!s_Sample.Opcode.StartsWith("sample", StringComparison.Ordinal) ||
            s_Sample.Destination is not { Kind: OperandKind.Temp } s_Target ||
            s_Target.WriteMask.Length != 3)
            return false;

        // ⛔ The NormalMap node is a FLAT 2D fetch. A helicopter cockpit stores its normal map in a 2D ARRAY, and
        // collapsing that into this node emitted `Texture2DArray.Sample(s, float2)`, which does not compile at
        // all - the one build break in the corpus. An idiom recogniser has to check the resource type, not just
        // the arithmetic shape.
        if (s_Sample.ResourceType.Length > 0 &&
            !s_Sample.ResourceType.Equals("texture2d", StringComparison.OrdinalIgnoreCase))
            return false;

        var s_Register = s_Target.Index;
        var s_Matched = new List<int> { p_Start };
        var s_Scratches = new List<(int Register, int Component)>();
        var s_At = p_Start + 1;

        // The compiler is free to interleave unrelated work (the PowerStation lands its OTHER texture fetch
        // between the sample and the decode) and to keep the dot's intermediates in a DIFFERENT register, so
        // the chain is matched by SEEKING each step. Anything stepped over must not touch the normal register
        // (a write would change what the decode sees; a read would want the RAW sample this node no longer
        // publishes) nor any live intermediate.
        AsmInstruction? Seek(Func<AsmInstruction, bool> p_Is)
        {
            while (s_At < p_Instructions.Count)
            {
                var s_Candidate = p_Instructions[s_At];
                if (p_Is(s_Candidate))
                    return s_Candidate;

                if (TouchesRegister(s_Candidate, s_Register) ||
                    s_Scratches.Any(p_S => TouchesComponent(s_Candidate, p_S.Register, p_S.Component)))
                    return null;

                s_At++;
            }

            return null;
        }

        bool DestIsNormal(AsmInstruction p_I, int p_Lanes) =>
            p_I.Destination is { Kind: OperandKind.Temp } s_D && s_D.Index == s_Register &&
            s_D.WriteMask.Length == p_Lanes;

        bool ReadsScratchNegated(AsmInstruction p_I, (int Register, int Component) p_Scratch)
        {
            if (p_I.Sources.Count != 2 || p_I.Destination?.WriteMask is not { Length: 1 } s_Mask)
                return false;

            for (var i = 0; i < 2; i++)
            {
                var s_Value = p_I.Sources[i];
                if (s_Value.Kind == OperandKind.Temp && s_Value.Index == p_Scratch.Register &&
                    s_Value.Negate && Component(s_Value, s_Mask[0]) == p_Scratch.Component &&
                    IsLiteral(p_I.Sources[1 - i], 1f))
                    return true;
            }

            return false;
        }

        bool ReadsScratch(AsmInstruction p_I, (int Register, int Component) p_Scratch) =>
            p_I.Destination?.WriteMask is { Length: 1 } s_Mask && p_I.Sources.Any(p_S =>
                p_S.Kind == OperandKind.Temp && p_S.Index == p_Scratch.Register &&
                Component(p_S, s_Mask[0]) == p_Scratch.Component);

        // 1. Optional A*R unswizzle, then the *2-1 decode.
        var s_Step = Seek(p_I => (p_I.Opcode == "mul" && DestIsNormal(p_I, 1)) ||
                                 (p_I.Opcode == "mad" && DestIsNormal(p_I, 2)));
        if (s_Step == null)
            return false;

        if (s_Step.Opcode == "mul")
        {
            s_Matched.Add(s_At++);
            s_Step = Seek(p_I => p_I.Opcode == "mad" && DestIsNormal(p_I, 2));
            if (s_Step == null)
                return false;
        }

        if (!IsLiteral(s_Step.Sources.ElementAtOrDefault(1), 2f) ||
            !IsLiteral(s_Step.Sources.ElementAtOrDefault(2), -1f))
            return false;

        s_Matched.Add(s_At++);

        // 2. dot(xy, xy) into a single-lane scratch, wherever the compiler put it.
        var s_Dot = Seek(p_I => p_I.Opcode == "dp2" &&
                                p_I.Sources.All(p_S => p_S.Kind == OperandKind.Temp &&
                                                       p_S.Index == s_Register) &&
                                p_I.Destination is { Kind: OperandKind.Temp, WriteMask.Length: 1 });
        if (s_Dot == null)
            return false;

        var s_Scratch = (s_Dot.Destination!.Index, s_Dot.Destination!.WriteMask[0]);
        s_Scratches.Add(s_Scratch);
        s_Matched.Add(s_At++);

        // 3. 1 - dot.
        var s_Complement = Seek(p_I => p_I.Opcode == "add" && ReadsScratchNegated(p_I, s_Scratch) &&
                                       p_I.Destination is { Kind: OperandKind.Temp });
        if (s_Complement == null)
            return false;

        s_Scratch = (s_Complement.Destination!.Index, s_Complement.Destination!.WriteMask[0]);
        s_Scratches.Add(s_Scratch);
        s_Matched.Add(s_At++);

        // 4. Optional clamp to zero before the root.
        var s_Clamp = Seek(p_I => p_I.Opcode == "max" && ReadsScratch(p_I, s_Scratch) &&
                                  p_I.Sources.Any(p_S => IsLiteral(p_S, 0f)) &&
                                  p_I.Destination is { Kind: OperandKind.Temp });
        if (s_Clamp != null)
        {
            s_Scratch = (s_Clamp.Destination!.Index, s_Clamp.Destination!.WriteMask[0]);
            s_Scratches.Add(s_Scratch);
            s_Matched.Add(s_At++);
        }

        // 5. The z reconstruction back into the normal register.
        var s_Reconstruct = Seek(p_I => p_I.Opcode == "sqrt" && DestIsNormal(p_I, 1) &&
                                        ReadsScratch(p_I, s_Scratch));
        if (s_Reconstruct == null)
            return false;

        s_Matched.Add(s_At);

        // ⚠ The intermediates (dot, 1-dot, the clamp) are consumed by this node and never published: anything
        // that still reads one downstream would silently get a STALE value, so those shaders keep the literal
        // translation.
        foreach (var (s_ScratchRegister, s_ScratchComponent) in s_Scratches)
            if (ReadsComponent(p_Instructions, s_At + 1, s_ScratchRegister, s_ScratchComponent))
                return false;

        var s_Resource = s_Sample.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Resource);
        var s_Node = NewNode("NormalMap");
        s_Node.Params["Register"] = (s_Resource?.Index ?? 2).ToString();

        var s_Coord = ReadVector(s_Sample.Sources[0], new[] { 0, 1 });
        m_Graph.Connect(s_Coord.NodeId, s_Coord.Port, s_Node.Id, "Coord");

        // x,y,z of the sample register now ARE the decoded normal; the intermediates are dead, checked above.
        var s_File = RegisterFile("r" + s_Register);
        for (var i = 0; i < 3; i++)
            s_File[i] = new Value(s_Node.Id, "Out", i);

        foreach (var s_Index in s_Matched)
            m_Consumed.Add(s_Index);

        return true;
    }

    private static bool TouchesRegister(AsmInstruction p_Instruction, int p_Register) =>
        (p_Instruction.Destination is { Kind: OperandKind.Temp } s_Destination &&
         s_Destination.Index == p_Register) ||
        p_Instruction.Sources.Any(p_S => p_S.Kind == OperandKind.Temp && p_S.Index == p_Register);

    private static bool TouchesComponent(AsmInstruction p_Instruction, int p_Register, int p_Component)
    {
        if (p_Instruction.Destination is { Kind: OperandKind.Temp } s_Destination &&
            s_Destination.Index == p_Register && s_Destination.WriteMask.Contains(p_Component))
            return true;

        var s_Slots = p_Instruction.Destination?.WriteMask is { Length: > 0 } s_Mask ? s_Mask : new[] { 0 };
        foreach (var s_Source in p_Instruction.Sources)
        {
            if (s_Source.Kind != OperandKind.Temp || s_Source.Index != p_Register)
                continue;

            foreach (var s_Slot in s_Slots)
                if (Component(s_Source, s_Slot) == p_Component)
                    return true;

            if (p_Instruction.Opcode.StartsWith("dp", StringComparison.Ordinal))
                for (var s_Position = 0; s_Position < 4; s_Position++)
                    if (Component(s_Source, s_Position) == p_Component)
                        return true;
        }

        return false;
    }

    /// <summary>
    /// Collapses the tangent-basis transform + normalize into ONE TangentToWorld node when it feeds AUTHORED
    /// maths instead of the frame's pack - a shader that blends two normal maps runs this whole chain twice:
    ///
    ///     dp3 R.x, n, v2
    ///     dp3 R.y, n, v3
    ///     dp3 R.z, n, v4
    ///     dp3 L.c, R.xyz, R.xyz
    ///     rsq Q.c, L.c
    ///     ... consumer: mul/mad reading R and Q together ...
    ///
    /// The five recognised instructions are consumed; the consumer is NOT - instead R's lanes become the node's
    /// output (already normalized) and Q becomes a constant 1, so `R * Q` translates to the same value with the
    /// multiply folding away. That trick is what lets the fused form (`mad out, R, Q, -x`, how a normal-map
    /// BLEND compiles) work without a recogniser of its own.
    ///
    /// ⛔ Every reader of R or Q until their next write must read them TOGETHER: R alone would now see the
    /// normalized vector where the game saw the raw one, and Q alone a 1 where the game had 1/length. One reader
    /// that fails this declines the whole collapse - a missed idiom costs nodes, a wrong one costs correctness.
    /// Rigid-mesh contracts only: v2..v4 are the measured tangent rows, and that is what the node emits.
    /// </summary>
    private bool TryTangentBasis(List<AsmInstruction> p_Instructions, int p_Start)
    {
        if (!m_Contract.SemanticsVerified || p_Start + 4 >= p_Instructions.Count)
            return false;

        // The three rows, strictly consecutive, one lane each, same tangent normal against v2, v3, v4.
        AsmOperand? s_Normal = null;
        var s_Rows = -1;
        for (var i = 0; i < 3; i++)
        {
            var s_Row = p_Instructions[p_Start + i];
            if (s_Row.Opcode != "dp3" ||
                s_Row.Destination is not { Kind: OperandKind.Temp, WriteMask.Length: 1 } s_Destination ||
                s_Destination.WriteMask[0] != i)
                return false;

            if (s_Rows < 0)
                s_Rows = s_Destination.Index;
            else if (s_Destination.Index != s_Rows)
                return false;

            var s_Temp = s_Row.Sources.FirstOrDefault(p_S => p_S is { Kind: OperandKind.Temp, Negate: false });
            var s_Input = s_Row.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Input);
            if (s_Temp == null || s_Input == null || s_Input.Index != 2 + i)
                return false;

            for (var s_Lane = 0; s_Lane < 3; s_Lane++)
                if (Component(s_Temp, s_Lane) != s_Lane)
                    return false;

            if (s_Normal == null)
                s_Normal = s_Temp;
            else if (s_Normal.Index != s_Temp.Index)
                return false;
        }

        // The squared length and the reciprocal square root.
        var s_Length = p_Instructions[p_Start + 3];
        if (s_Length.Opcode != "dp3" ||
            s_Length.Destination is not { Kind: OperandKind.Temp, WriteMask.Length: 1 } s_LengthDst ||
            s_Length.Sources.Count < 2 ||
            s_Length.Sources.Any(p_S => p_S.Kind != OperandKind.Temp || p_S.Index != s_Rows))
            return false;

        var s_Rsq = p_Instructions[p_Start + 4];
        if (s_Rsq.Opcode != "rsq" ||
            s_Rsq.Destination is not { Kind: OperandKind.Temp, WriteMask.Length: 1 } s_RsqDst ||
            s_Rsq.Sources.ElementAtOrDefault(0) is not { Kind: OperandKind.Temp } s_RsqSrc ||
            s_RsqSrc.Index != s_LengthDst.Index || Component(s_RsqSrc, 0) != s_LengthDst.WriteMask[0])
            return false;

        // Nothing else may consume the squared length - unless the rsq overwrote it IN PLACE (`rsq r0.w, r0.w`,
        // the common spelling), in which case the squared length no longer exists and any later read of that
        // component is a read of the reciprocal, checked by the together-guard below.
        var s_InPlace = s_RsqDst.Index == s_LengthDst.Index && s_RsqDst.WriteMask[0] == s_LengthDst.WriteMask[0];
        if (!s_InPlace && ReadsComponent(p_Instructions, p_Start + 5, s_LengthDst.Index, s_LengthDst.WriteMask[0]))
            return false;

        // Every reader of the rows or of the rsq, until each is rewritten, must read them TOGETHER.
        for (var i = p_Start + 5; i < p_Instructions.Count; i++)
        {
            var s_Instruction = p_Instructions[i];
            var s_ReadsRows = ReadsAnyLane(s_Instruction, s_Rows, new[] { 0, 1, 2 });
            var s_ReadsRsq = ReadsAnyLane(s_Instruction, s_RsqDst.Index, new[] { s_RsqDst.WriteMask[0] });
            if (s_ReadsRows != s_ReadsRsq)
                return false;

            var s_RowsDone = WritesAllLanes(s_Instruction, s_Rows, new[] { 0, 1, 2 });
            var s_RsqDone = WritesAllLanes(s_Instruction, s_RsqDst.Index, new[] { s_RsqDst.WriteMask[0] });
            if (s_RowsDone && s_RsqDone)
                break;

            // One dead before the other: from here a lone read of the survivor is legitimate again.
            if (s_RowsDone || s_RsqDone)
                break;
        }

        var s_Node = NewNode("TangentToWorld");
        var s_Vector = ReadVector(s_Normal!, new[] { 0, 1, 2 });
        m_Graph.Connect(s_Vector.NodeId, s_Vector.Port, s_Node.Id, "Input");

        var s_File = RegisterFile("r" + s_Rows);
        for (var i = 0; i < 3; i++)
            s_File[i] = new Value(s_Node.Id, "Out", i);

        RegisterFile("r" + s_RsqDst.Index)[s_RsqDst.WriteMask[0]] = Constant(1f);

        for (var i = p_Start; i < p_Start + 5; i++)
            m_Consumed.Add(i);

        return true;
    }

    /// <summary>
    /// Whether any NON-frame, non-consumed instruction reads one of the given lanes while it still holds this
    /// value. Liveness is per lane: a rewritten lane stops counting, the others stay live - without that, a
    /// read of a lane the shader had already recycled created a dangling node on the drum.
    /// </summary>
    private bool AuthoredReadOf(List<AsmInstruction> p_Instructions, int p_From, int p_Register, int[] p_Lanes)
    {
        var s_Live = new HashSet<int>(p_Lanes);
        for (var i = p_From; i < p_Instructions.Count && s_Live.Count > 0; i++)
        {
            var s_Skip = m_Consumed.Contains(i) || m_Frame != null && m_Frame.FrameInstructions.Contains(i);
            if (!s_Skip && ReadsAnyLane(p_Instructions[i], p_Register, s_Live.ToArray()))
                return true;

            if (p_Instructions[i].Destination is { Kind: OperandKind.Temp } s_Destination &&
                s_Destination.Index == p_Register)
                foreach (var s_Lane in s_Destination.WriteMask is { Length: > 0 } s_Mask ? s_Mask : new[] { 0 })
                    s_Live.Remove(s_Lane);
        }

        return false;
    }

    private static bool ReadsAnyLane(AsmInstruction p_Instruction, int p_Register, int[] p_Lanes)
    {
        var s_Slots = p_Instruction.Destination?.WriteMask is { Length: > 0 } s_Mask ? s_Mask : new[] { 0 };
        foreach (var s_Source in p_Instruction.Sources)
        {
            if (s_Source.Kind != OperandKind.Temp || s_Source.Index != p_Register)
                continue;

            foreach (var s_Slot in s_Slots)
                if (p_Lanes.Contains(Component(s_Source, s_Slot)))
                    return true;
        }

        return false;
    }

    private static bool WritesAllLanes(AsmInstruction p_Instruction, int p_Register, int[] p_Lanes)
    {
        if (p_Instruction.Destination is not { Kind: OperandKind.Temp } s_Destination ||
            s_Destination.Index != p_Register)
            return false;

        var s_Mask = s_Destination.WriteMask is { Length: > 0 } s_Written ? s_Written : new[] { 0 };
        return p_Lanes.All(s_Mask.Contains);
    }

    private static bool IsLiteral(AsmOperand? p_Operand, float p_Value) =>
        p_Operand is { Kind: OperandKind.Literal } && p_Operand.Literal.Length > 0 &&
        Math.Abs(Math.Abs(p_Operand.Literal[0]) - Math.Abs(p_Value)) < 1e-6f &&
        Math.Sign(p_Operand.Literal[0]) == Math.Sign(p_Value);

    /// <summary>Whether any later instruction reads the given component of a temp register.</summary>
    private static bool ReadsComponent(List<AsmInstruction> p_Instructions, int p_From, int p_Register, int p_Component)
    {
        for (var i = p_From; i < p_Instructions.Count; i++)
        {
            var s_Instruction = p_Instructions[i];
            var s_Slots = s_Instruction.Destination?.WriteMask is { Length: > 0 } s_Mask ? s_Mask : new[] { 0 };

            foreach (var s_Source in s_Instruction.Sources)
            {
                if (s_Source.Kind != OperandKind.Temp || s_Source.Index != p_Register)
                    continue;

                foreach (var s_Slot in s_Slots)
                    if (Component(s_Source, s_Slot) == p_Component)
                        return true;

                // A dot product reads positions 0..3 whatever the mask says.
                if (s_Instruction.Opcode.StartsWith("dp", StringComparison.Ordinal))
                    for (var s_Position = 0; s_Position < 4; s_Position++)
                        if (Component(s_Source, s_Position) == p_Component)
                            return true;
            }

            // Once the register is fully overwritten there is nothing of ours left to read.
            if (s_Instruction.Destination is { Kind: OperandKind.Temp } s_Dest && s_Dest.Index == p_Register &&
                s_Dest.WriteMask.Contains(p_Component))
                return false;
        }

        return false;
    }

    // ── node plumbing ────────────────────────────────────────────────────────────────────────────────────

    private GraphNode NewNode(string p_Kind)
    {
        var s_Node = new GraphNode { Kind = p_Kind, X = m_Column * 240, Y = m_Row * 130 };
        m_Row++;
        if (m_Row > 14)
        {
            m_Row = 0;
            m_Column++;
        }

        m_Graph.Nodes.Add(s_Node);
        return s_Node;
    }

    private Value Constant(float p_Value)
    {
        var s_Key = p_Value.ToString("R", CultureInfo.InvariantCulture);
        if (m_Constants.TryGetValue(s_Key, out var s_Existing))
            return s_Existing;

        var s_Node = NewNode("Scalar");
        s_Node.Params["Value"] = p_Value.ToString("0.0######", CultureInfo.InvariantCulture);
        var s_Value = new Value(s_Node.Id, "Out");
        m_Constants[s_Key] = s_Value;
        return s_Value;
    }

    /// <summary>
    /// The register file an operand names. ⛔ Returns null for an operand this parser does not understand, and
    /// callers must then write NOTHING: the old code fell back to "r" + Index, so an unrecognised destination
    /// quietly overwrote r0 - which is how three `mov x0[0].x` in a terrain mask pass destroyed the sampled mask
    /// and turned 38 shaders into silent mistranslations.
    /// </summary>
    private static string? FileKey(AsmOperand p_Operand) => p_Operand.Kind switch
    {
        OperandKind.Temp => "r" + p_Operand.Index,
        OperandKind.Output => "o" + p_Operand.Index,
        OperandKind.IndexableTemp => $"x{p_Operand.Index}_{p_Operand.Element}",
        _ => null,
    };

    private Value?[] RegisterFile(string p_Key)
    {
        if (!m_Registers.TryGetValue(p_Key, out var s_Slots))
        {
            s_Slots = new Value?[4];
            m_Registers[p_Key] = s_Slots;
        }

        return s_Slots;
    }

    private readonly Dictionary<string, string> m_MixNodes = new(StringComparer.Ordinal);

    /// <summary>
    /// The ONE MixOut splitter in front of a vector-valued port. Every channel read of the same port goes
    /// through the same splitter and just uses a different pin - the shape an author draws (one MixOut behind
    /// a texture, R/G/B/A fanned out), instead of the one-splitter-per-channel thicket this used to build.
    /// </summary>
    private string MixNodeFor(string p_NodeId, string p_Port)
    {
        var s_Key = p_NodeId + "|" + p_Port;
        if (m_MixNodes.TryGetValue(s_Key, out var s_Existing))
            return s_Existing;

        var s_Mix = NewNode("MixOut");
        m_Graph.Connect(p_NodeId, p_Port, s_Mix.Id, "Input");
        m_MixNodes[s_Key] = s_Mix.Id;
        return s_Mix.Id;
    }

    /// <summary>Picks one channel out of a genuinely vector-valued node, caching so it is done once.</summary>
    private Value Extract(string p_NodeId, string p_Port, int p_Component)
    {
        var s_Key = (p_NodeId + "|" + p_Port, p_Component);
        if (m_Extracted.TryGetValue(s_Key, out var s_Cached))
            return s_Cached;

        var s_Value = new Value(MixNodeFor(p_NodeId, p_Port), "RGBA"[p_Component].ToString());
        m_Extracted[s_Key] = s_Value;
        return s_Value;
    }

    // ── operand reads ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The scalar an operand supplies for one destination component, as a genuine scalar port. Only the dot
    /// products still need this: everything else reads whole vectors now.
    /// </summary>
    private Value Read(AsmOperand p_Operand, int p_Slot)
    {
        var s_Value = Scalar(ReadRaw(p_Operand, p_Slot));

        if (p_Operand.Absolute)
            s_Value = Unary("Abs", s_Value);

        if (p_Operand.Negate)
            s_Value = Unary("Negate", s_Value);

        return s_Value;
    }

    /// <summary>Forces a value to a scalar port, inserting the MixOut only when the value is a lane.</summary>
    private Value Scalar(Value p_Value) =>
        p_Value.Lane < 0 ? p_Value : Extract(p_Value.NodeId, p_Value.Port, p_Value.Lane);

    private readonly Dictionary<string, (string NodeId, string Port)> m_Assembled = new(StringComparer.Ordinal);

    /// <summary>
    /// Turns the per-component values an operand supplies back into ONE vector port.
    ///
    /// This is where the node count is won or lost. Consecutive scalars that already live in the same node's
    /// output are recognised and wired straight through, or routed by a single Swizzle - which is exactly what
    /// the DXBC source swizzle was in the first place. Only a genuine mix of different producers costs an
    /// Append, and that is cached too.
    /// </summary>
    private (string NodeId, string Port, bool Uniform) Assemble(List<Value> p_Values)
    {
        if (p_Values.Count == 0)
            return (Constant(0f).NodeId, "Out", true);

        // One producing port for all of them: the whole operand is a swizzle of a single node.
        if (p_Values.All(p_V => p_V.NodeId == p_Values[0].NodeId && p_V.Port == p_Values[0].Port))
        {
            // Uniform port (a constant, or an already-extracted channel): every lane holds it, so it needs
            // nothing at all - the palette's float4 nodes broadcast a scalar across all four channels.
            if (p_Values[0].Lane < 0)
                return (p_Values[0].NodeId, p_Values[0].Port, true);

            // Two lanes in pair order straight off a UV interpolator ARE a UV set: the TexCoord node an
            // author draws, never a raw vec4 wire (the xy pair) or a Swizzle (the zw pair of a packed layout).
            if (p_Values.Count == 2 &&
                ((p_Values[0].Lane == 0 && p_Values[1].Lane == 1) ||
                 (p_Values[0].Lane == 2 && p_Values[1].Lane == 3)) &&
                TexCoordFor(p_Values[0].NodeId, p_Values[0].Lane == 0 ? "xy" : "zw") is { } s_TexCoord)
                return (s_TexCoord, "Out", false);

            // Three lanes in order off an interpolator classified as the world position or the world normal:
            // the named input node, same principle as the UV pairs.
            if (p_Values.Count == 3 && p_Values.Select((p_V, p_I) => p_V.Lane == p_I).All(p_B => p_B) &&
                NamedInterpolatorFor(p_Values[0].NodeId) is { } s_Named)
                return (s_Named, "Out", false);

            // Every lane the same channel: that is ONE channel of the source, so it reads as a MixOut pin -
            // visible on the canvas and shared with every other read of the port - never as a Swizzle whose
            // single letter hides in the properties panel. The scalar pin broadcasts on its own.
            if (p_Values.All(p_V => p_V.Lane == p_Values[0].Lane))
            {
                var s_Channel = Extract(p_Values[0].NodeId, p_Values[0].Port, p_Values[0].Lane);
                return (s_Channel.NodeId, s_Channel.Port, true);
            }

            var s_Identity = p_Values.Select((p_V, p_I) => p_V.Lane == p_I).All(p_B => p_B);
            if (s_Identity)
            {
                // .rgb straight out of a texture is a thing the author needs to SEE (the alpha may carry
                // something else entirely), so it goes through the splitter's RGB pin instead of an invisible
                // truncation. Everything else keeps the direct wire - splitting every temp would bury the
                // graph in MixOuts.
                if (p_Values.Count == 3 && m_Graph.FindNode(p_Values[0].NodeId)?.Kind == "Texture")
                    return (MixNodeFor(p_Values[0].NodeId, p_Values[0].Port), "RGB", false);

                return (p_Values[0].NodeId, p_Values[0].Port, false);
            }

            var s_Channels = new string(p_Values.Select(p_V => "xyzw"[p_V.Lane]).ToArray());
            var s_SwizzleKey = $"swz|{p_Values[0].NodeId}|{p_Values[0].Port}|{s_Channels}";
            if (m_Assembled.TryGetValue(s_SwizzleKey, out var s_CachedSwizzle))
                return (s_CachedSwizzle.NodeId, s_CachedSwizzle.Port, false);

            var s_Swizzle = NewNode("Swizzle");
            s_Swizzle.Params["Channels"] = s_Channels;
            m_Graph.Connect(p_Values[0].NodeId, p_Values[0].Port, s_Swizzle.Id, "Input");
            m_Assembled[s_SwizzleKey] = (s_Swizzle.Id, "Out");
            return (s_Swizzle.Id, "Out", false);
        }

        var s_Scalars = p_Values.Select(Scalar).ToList();
        var s_AppendKey = "app|" + string.Join(";", s_Scalars.Select(p_V => p_V.NodeId + "." + p_V.Port));
        if (m_Assembled.TryGetValue(s_AppendKey, out var s_CachedAppend))
            return (s_CachedAppend.NodeId, s_CachedAppend.Port, false);

        var s_Append = NewNode("Append");
        var s_Ports = new[] { "X", "Y", "Z", "W" };
        for (var i = 0; i < s_Scalars.Count && i < 4; i++)
            m_Graph.Connect(s_Scalars[i].NodeId, s_Scalars[i].Port, s_Append.Id, s_Ports[i]);

        m_Assembled[s_AppendKey] = (s_Append.Id, "Out");
        return (s_Append.Id, "Out", false);
    }

    /// <summary>
    /// ReadVector with the operand's negate SUPPRESSED - for the subtraction shapes, where the Subtract node
    /// carries the sign and a Negate node would say it twice.
    /// </summary>
    private (string NodeId, string Port, bool Uniform) ReadVectorAbsOnly(AsmOperand p_Operand, int[] p_Slots)
    {
        var s_Assembled = Assemble(p_Slots.Select(p_S => ReadRaw(p_Operand, p_S)).ToList());

        if (p_Operand.Absolute)
            s_Assembled = ApplyVector("Abs", s_Assembled);

        return s_Assembled;
    }

    private readonly Dictionary<string, string> m_TexCoords = new(StringComparer.Ordinal);

    /// <summary>
    /// The TexCoord node for one UV set of an interpolator the contract classified as a UV - cached, so every
    /// read of the same set shares the node. Null when the source is not a UV interpolator, which keeps the
    /// raw wire for everything the classification could not name.
    /// </summary>
    private string? TexCoordFor(string p_NodeId, string p_Half)
    {
        if (m_Graph.FindNode(p_NodeId) is not { Kind: "Interpolator" } s_Interpolator ||
            !int.TryParse(s_Interpolator.GetParam("Index"), out var s_Index))
            return null;

        var s_IsUv = m_Contract.SemanticsVerified
            ? m_Contract.FieldNameFor(s_Index) == "TexCoord"
            : m_Contract.InterpolatorMeanings.TryGetValue(s_Index, out var s_Meaning) &&
              s_Meaning == "UvPair";

        if (!s_IsUv)
            return null;

        var s_Key = s_Index + "|" + p_Half;
        if (m_TexCoords.TryGetValue(s_Key, out var s_Existing))
            return s_Existing;

        var s_Node = NewNode("TexCoord");
        s_Node.Params["Interp"] = s_Index.ToString();
        s_Node.Params["Half"] = p_Half;
        m_TexCoords[s_Key] = s_Node.Id;
        return s_Node.Id;
    }

    /// <summary>
    /// The named node (World Position / Geometric Normal) for an interpolator the contract classified as one,
    /// cached. Null keeps the raw wire for everything the classification could not name. UVs have their own
    /// pair-aware path in <see cref="TexCoordFor"/>.
    /// </summary>
    private string? NamedInterpolatorFor(string p_NodeId)
    {
        if (m_Graph.FindNode(p_NodeId) is not { Kind: "Interpolator" } s_Interpolator ||
            !int.TryParse(s_Interpolator.GetParam("Index"), out var s_Index))
            return null;

        var s_Kind = m_Contract.SemanticsVerified
            ? m_Contract.FieldNameFor(s_Index) == "WorldPos" ? "WorldPos" : null
            : m_Contract.InterpolatorMeanings.GetValueOrDefault(s_Index) switch
            {
                "WorldPos" => "WorldPos",
                "Normal" => "GeometricNormal",
                _ => null,
            };

        if (s_Kind == null)
            return null;

        var s_Key = s_Index + "|" + s_Kind;
        if (m_TexCoords.TryGetValue(s_Key, out var s_Existing))
            return s_Existing;

        var s_Node = NewNode(s_Kind);
        s_Node.Params["Interp"] = s_Index.ToString();
        m_TexCoords[s_Key] = s_Node.Id;
        return s_Node.Id;
    }

    /// <summary>One operand of a vector instruction, modifiers applied once to the whole vector.</summary>
    private (string NodeId, string Port, bool Uniform) ReadVector(AsmOperand p_Operand, int[] p_Slots)
    {
        var s_Assembled = Assemble(p_Slots.Select(p_S => ReadRaw(p_Operand, p_S)).ToList());

        if (p_Operand.Absolute)
            s_Assembled = ApplyVector("Abs", s_Assembled);

        if (p_Operand.Negate)
            s_Assembled = ApplyVector("Negate", s_Assembled);

        return s_Assembled;
    }

    private (string NodeId, string Port, bool Uniform) ApplyVector(
        string p_Kind, (string NodeId, string Port, bool Uniform) p_Input)
    {
        var s_Node = NewNode(p_Kind);
        m_Graph.Connect(p_Input.NodeId, p_Input.Port, s_Node.Id, "Input");
        return (s_Node.Id, "Out", p_Input.Uniform);
    }

    private Value ReadRaw(AsmOperand p_Operand, int p_Slot)
    {
        switch (p_Operand.Kind)
        {
            case OperandKind.Literal:
            {
                // l(4.0) is a single value used for every component; l(a,b,c,d) is per component.
                var s_Index = p_Operand.Literal.Length == 1 ? 0 : Math.Min(p_Slot, p_Operand.Literal.Length - 1);
                return Constant(p_Operand.Literal.Length == 0 ? 0f : p_Operand.Literal[s_Index]);
            }

            case OperandKind.Temp:
            case OperandKind.IndexableTemp:
            {
                var s_Component = Component(p_Operand, p_Slot);
                var s_Slots = RegisterFile(FileKey(p_Operand)!);
                if (s_Slots[s_Component] is { } s_Known)
                    return s_Known;

                Warnings.Add($"{FileKey(p_Operand)}.{"xyzw"[s_Component]} read before it was written; using 0.");
                return Constant(0f);
            }

            case OperandKind.Input:
            {
                // Returned as a LANE, not as an extracted scalar: an interpolator read as .xyz then becomes one
                // wire instead of three MixOut nodes.
                var s_Component = Component(p_Operand, p_Slot);
                return new Value(InterpolatorNode(p_Operand.Index), "Out", s_Component);
            }

            case OperandKind.ConstBuffer:
                return ConstBufferValue(p_Operand, Component(p_Operand, p_Slot));

            default:
                Warnings.Add($"unsupported operand {p_Operand}; using 0.");
                return Constant(0f);
        }
    }

    private static int Component(AsmOperand p_Operand, int p_Slot)
    {
        if (p_Operand.Swizzle.Length == 0)
            return Math.Min(p_Slot, 3);

        return p_Operand.Swizzle[Math.Min(p_Slot, p_Operand.Swizzle.Length - 1)];
    }

    private readonly Dictionary<int, string> m_Interpolators = new();

    /// <summary>
    /// Which input a `vN` operand names, taken from the shader's OWN input signature.
    ///
    /// ⛔ NOT "v(N) is TEXCOORD(N-1)". That held for the oil drum only because SV_Position sat in v0 and nothing
    /// read it, so the off-by-one happened to line up. A lens flare reads v0 - which IS SV_Position there - and
    /// the shortcut silently handed it TEXCOORD0 instead: the graph sampled a texture at the wrong coordinate
    /// and still rendered something plausible. The ISGN records the register of every element; use it.
    /// </summary>
    private string InterpolatorNode(int p_Register)
    {
        if (m_Interpolators.TryGetValue(p_Register, out var s_Existing))
            return s_Existing;

        var s_Element = m_Contract.Inputs.FirstOrDefault(p_I => p_I.Register == p_Register);
        string s_Id;

        if (s_Element != null && s_Element.Semantic.StartsWith("SV_Position", StringComparison.OrdinalIgnoreCase))
        {
            s_Id = NewNode("PixelPosition").Id;
        }
        else if (s_Element != null && s_Element.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
        {
            // ⛔ A system value - SV_IsFrontFace and friends - is NOT an interpolator. Handing one the nearest
            // TEXCOORD produced a shader that read a UV where the game reads a face flag, and rendered something
            // plausible. Reported as a gap instead: a stated hole beats a quiet substitution.
            UntranslatedOpcodes.Add(s_Element.Semantic);
            Warnings.Add($"{s_Element.Semantic} has no node; reads of v{p_Register} are 0.");
            s_Id = Constant(0f).NodeId;
        }
        else
        {
            var s_Node = NewNode("Interpolator");
            s_Node.Params["Index"] = (s_Element?.Index ?? Math.Max(0, p_Register - 1)).ToString();
            if (s_Element == null)
                Warnings.Add($"v{p_Register} is not in the input signature; assuming TEXCOORD{Math.Max(0, p_Register - 1)}.");

            s_Id = s_Node.Id;
        }

        m_Interpolators[p_Register] = s_Id;
        return s_Id;
    }

    private readonly Dictionary<string, string> m_CbNodes = new();

    /// <summary>The view constants that already have a named node, keyed by DICE's own field name from RDEF.</summary>
    private static readonly Dictionary<string, string> s_KnownViewConstants = new(StringComparer.Ordinal)
    {
        ["cameraPos"] = "CameraPos",
        ["time"] = "Time",
        ["screenSize"] = "ScreenSize",
        ["viewportZMinMaxKzKw"] = "ViewportZParams",
    };

    /// <summary>
    /// Only the cbuffer fields the editor already models are reachable. cb2[20] is cameraPos, measured off the
    /// vanilla shader; anything else becomes a constant 0 WITH a warning rather than a silent wrong value.
    /// </summary>
    private static int p_Element16(AsmOperand p_Operand) => p_Operand.Element * 16;

    private Value ConstBufferValue(AsmOperand p_Operand, int p_Component)
    {
        if (p_Operand.Index == 2 && p_Operand.Element == 20)
        {
            if (!m_CbNodes.TryGetValue("cameraPos", out var s_Node))
            {
                s_Node = NewNode("CameraPos").Id;
                m_CbNodes["cameraPos"] = s_Node;
            }

            return new Value(s_Node, "Out", Math.Min(p_Component, 2));
        }

        // ★★★ The shader names its own parameters. Anything the target declares in a constant buffer becomes a
        // node carrying DICE's name for it, instead of the literal 0 that used to stand in - which is what made
        //364 of mp_017's shaders untranslatable, and made them untranslatable SILENTLY.
        // ⚠ Only fields that fit in ONE register are taken. A wider one (a matrix) is read row by row and this
        // node has no way to say which row, so it falls through to the reported gap rather than quietly handing
        // back the first row - the measurement says every external owns its own 16-byte slot anyway.
        var s_Field = m_Contract.FieldAt(p_Operand.Index, p_Operand.Element, p_Component);

        // The four view constants the palette already models by name get their dedicated node, so a translated
        // graph reads `Time` or `Screen Size` rather than an anonymous slot.
        if (s_Field != null && s_KnownViewConstants.TryGetValue(s_Field.Name, out var s_Kind))
        {
            if (!m_CbNodes.TryGetValue(s_Field.Name, out var s_Known))
            {
                s_Known = NewNode(s_Kind).Id;
                m_CbNodes[s_Field.Name] = s_Known;
            }

            return new Value(s_Known, "Out",
                Math.Clamp((p_Element16(p_Operand) + p_Component * 4 - s_Field.Offset) / 4, 0, 3));
        }

        if (s_Field != null && s_Field.Size <= 16)
        {
            var s_Key = $"{s_Field.Register}:{s_Field.Name}";
            if (!m_CbNodes.TryGetValue(s_Key, out var s_Parameter))
            {
                var s_Node = NewNode("ExternalConstant");
                s_Node.Params["Name"] = s_Field.Name;
                s_Node.Params["Buffer"] = s_Field.Buffer;
                s_Node.Params["Register"] = s_Field.Register.ToString();
                s_Node.Params["Element"] = s_Field.Element.ToString();
                s_Parameter = s_Node.Id;
                m_CbNodes[s_Key] = s_Parameter;
            }

            // The node holds the field, so the lane is the offset WITHIN the field, not within the register.
            var s_Lane = (p_Element16(p_Operand) + p_Component * 4 - s_Field.Offset) / 4;
            return new Value(s_Parameter, "Out", Math.Clamp(s_Lane, 0, 3));
        }

        UnmodelledConstants.Add($"cb{p_Operand.Index}[{p_Operand.Element}]");
        Warnings.Add($"cb{p_Operand.Index}[{p_Operand.Element}] is not modelled; using 0.");
        return Constant(0f);
    }

    // ── operations ───────────────────────────────────────────────────────────────────────────────────────

    private Value Binary(string p_Kind, Value p_A, Value p_B)
    {
        var s_Node = NewNode(p_Kind);
        m_Graph.Connect(p_A.NodeId, p_A.Port, s_Node.Id, "Input1");
        m_Graph.Connect(p_B.NodeId, p_B.Port, s_Node.Id, "Input2");
        return new Value(s_Node.Id, "Out");
    }

    private Value Unary(string p_Kind, Value p_A)
    {
        var s_Node = NewNode(p_Kind);
        m_Graph.Connect(p_A.NodeId, p_A.Port, s_Node.Id, "Input");
        return new Value(s_Node.Id, "Out");
    }

    private void Write(AsmOperand p_Destination, int p_Slot, Value p_Value, bool p_Saturate)
    {
        var s_Value = p_Saturate ? Unary("Saturate", p_Value) : p_Value;

        // ⛔ The redirect has to work here too, not only on the vector path. A shader whose smoothness is a
        // `dp2_sat` - the facade preset does exactly that - went through this scalar path, ignored the redirect,
        // and left the pin unconnected: the diff read 127/255, which is the pin's own 0.5 default rendered out.
        // A default that plausible is why the number had to be traced rather than eyeballed.
        if (m_Redirect is { } s_Pin)
        {
            RegisterFile(s_Pin.Key + s_Pin.Index)[0] = s_Value;
            return;
        }

        var s_Component = p_Destination.WriteMask.Length > 0
            ? p_Destination.WriteMask[Math.Min(p_Slot, p_Destination.WriteMask.Length - 1)]
            : p_Slot;

        var s_Key = FileKey(p_Destination);
        if (s_Key == null)
        {
            UntranslatedOpcodes.Add($"write to {p_Destination.Kind}");
            return;
        }

        RegisterFile(s_Key)[s_Component] = s_Value;
    }

    /// <summary>
    /// ⛔ A DXBC instruction reads ALL of its sources before it writes ANY of its destination. Writing component
    /// by component inside the loop meant `mul r0.xyz, r0.xxxx, r1.xyzx` clobbered r0.x and then read the NEW
    /// value for y and z — the first component came out right and the rest silently wrong. Every case therefore
    /// computes the whole result first and commits it afterwards.
    /// </summary>
    private void Commit(AsmOperand p_Destination, List<Value> p_Results, bool p_Saturate)
    {
        for (var i = 0; i < p_Results.Count; i++)
            Write(p_Destination, i, p_Results[i], p_Saturate);
    }

    /// <summary>
    /// Commits ONE vector-valued node across the destination's write mask: lane i of the result becomes the i-th
    /// masked component. Same read-before-write guarantee as the scalar path, because nothing is stored until
    /// every source has already been read into the node's inputs.
    /// </summary>
    private void CommitVector(AsmOperand p_Destination, (string NodeId, string Port, bool Uniform) p_Result,
        int[] p_Slots, bool p_Saturate)
    {
        var s_Result = p_Saturate ? ApplyVector("Saturate", p_Result) : p_Result;
        s_Result = Predicated(s_Result, p_Destination, p_Slots);

        var s_Key = m_Redirect is { } s_Pin ? s_Pin.Key + s_Pin.Index : FileKey(p_Destination);
        if (s_Key == null)
        {
            UntranslatedOpcodes.Add($"write to {p_Destination.Kind}");
            Warnings.Add($"destination {p_Destination} is not a register this translator knows; write dropped.");
            return;
        }

        var s_File = RegisterFile(s_Key);

        // A redirected write always lands in component 0: the pin reads a value, not a register channel.
        if (m_Redirect != null)
        {
            s_File[0] = new Value(s_Result.NodeId, s_Result.Port, s_Result.Uniform ? -1 : 0);
            return;
        }

        for (var i = 0; i < p_Slots.Length; i++)
        {
            // A single-component result built only from uniform sources is itself uniform, so it can be recorded
            // without a lane and read later as a plain scalar - which is what keeps chains of scalar maths from
            // growing a MixOut between every step.
            // A uniform result has the same value in every lane, so it is recorded without one and can be read
            // later as a plain scalar - which is what stops chains of scalar maths from growing a MixOut between
            // every step. A dot product is the common case: one number broadcast across the write mask.
            var s_Lane = s_Result.Uniform ? -1 : i;
            s_File[p_Slots[i]] = new Value(s_Result.NodeId, s_Result.Port, s_Lane);
        }
    }

    /// <summary>
    /// Blends a result with what the destination held BEFORE, under the open `if` predicates. Outside any `if`
    /// this returns the result untouched, so the proven path does not move.
    /// </summary>
    private (string NodeId, string Port, bool Uniform) Predicated(
        (string NodeId, string Port, bool Uniform) p_Result, AsmOperand p_Destination, int[] p_Slots)
    {
        if (m_Predicates.Count == 0 || m_Redirect != null)
            return p_Result;

        var s_Key = FileKey(p_Destination);
        if (s_Key == null)
            return p_Result;

        var s_File = RegisterFile(s_Key);

        // The value each written component had on the way in. A component never written before is 0, which is
        // what an undefined register reads as here - flagged, because it means the branch depends on something
        // this translation cannot see.
        var s_Previous = new List<Value>();
        foreach (var s_Slot in p_Slots)
        {
            if (s_File[s_Slot] is { } s_Known)
            {
                s_Previous.Add(s_Known);
                continue;
            }

            Warnings.Add($"{s_Key}.{"xyzw"[s_Slot]} is written inside a branch without a prior value; using 0.");
            s_Previous.Add(Constant(0f));
        }

        var s_Old = Assemble(s_Previous);
        var s_Condition = CombinedPredicate();

        var s_Select = NewNode("Select");
        m_Graph.Connect(s_Condition.NodeId, s_Condition.Port, s_Select.Id, "Condition");
        m_Graph.Connect(p_Result.NodeId, p_Result.Port, s_Select.Id, "WhenTrue");
        m_Graph.Connect(s_Old.NodeId, s_Old.Port, s_Select.Id, "WhenFalse");
        return (s_Select.Id, "Out", false);
    }

    /// <summary>All open predicates ANDed - as 0/1 masks, that is simply their product.</summary>
    private (string NodeId, string Port) CombinedPredicate()
    {
        var s_Combined = m_Predicates[0];
        for (var i = 1; i < m_Predicates.Count; i++)
        {
            var s_And = NewNode("Multiply");
            m_Graph.Connect(s_Combined.NodeId, s_Combined.Port, s_And.Id, "Input1");
            m_Graph.Connect(m_Predicates[i].NodeId, m_Predicates[i].Port, s_And.Id, "Input2");
            s_Combined = (s_And.Id, "Out");
        }

        return s_Combined;
    }

    /// <summary>Discard conditions, ALREADY evaluated at the point each `discard` appeared.</summary>
    private readonly List<(string NodeId, string Port)> m_PendingDiscards = new();

    /// <summary>
    /// Routes a value through the alpha test, so the pixel is killed before that value reaches a render target.
    /// Returns the input untouched when the shader never discards.
    /// </summary>
    private (string NodeId, string Port) WithDiscards((string NodeId, string Port) p_Value)
    {
        var s_Value = p_Value;
        foreach (var s_Discard in m_PendingDiscards)
        {
            var s_Clip = NewNode("Clip");
            m_Graph.Connect(s_Value.NodeId, s_Value.Port, s_Clip.Id, "Input");
            m_Graph.Connect(s_Discard.NodeId, s_Discard.Port, s_Clip.Id, "Discard");
            s_Value = (s_Clip.Id, "Out");
        }

        return s_Value;
    }

    /// <summary>
    /// Node outputs that hold a COMPARISON RESULT and nothing else - `nodeId|port`.
    ///
    /// Provenance, not a type: it is what makes `and` translatable. In DXBC a comparison writes all-ones or
    /// zero, so ANDing that with a value is how the compiler spells "this value, or nothing" - and in a graph,
    /// where the same comparison is a 0/1 float, that is exactly a multiply. But ONLY then: `and` on two
    /// genuine integers is bit twiddling and a multiply would be a mistranslation, which is worse than the
    /// stated hole. So the multiply is emitted when one side is known to be a mask, and the gap is reported
    /// otherwise.
    /// </summary>
    private readonly HashSet<string> m_MaskPorts = new(StringComparer.Ordinal);

    /// <summary>True when every component this operand supplies came straight out of a comparison.</summary>
    private bool OperandIsMask(AsmOperand? p_Operand, int[] p_Slots)
    {
        if (p_Operand == null || p_Operand.Kind is not (OperandKind.Temp or OperandKind.IndexableTemp))
            return false;

        var s_Key = FileKey(p_Operand);
        if (s_Key == null)
            return false;

        var s_File = RegisterFile(s_Key);
        foreach (var s_Slot in p_Slots)
        {
            if (s_File[Component(p_Operand, s_Slot)] is not { } s_Value ||
                !m_MaskPorts.Contains(s_Value.NodeId + "|" + s_Value.Port))
                return false;
        }

        return true;
    }

    /// <summary>Emits one node for a component-wise instruction, whatever its width.</summary>
    private string VectorOp(string p_Kind, AsmInstruction p_Instruction, AsmOperand p_Destination, int[] p_Slots)
    {
        var s_Node = NewNode(p_Kind);
        var s_Uniform = true;
        var s_Ports = p_Instruction.Sources.Count == 1
            ? new[] { "Input" }
            : new[] { "Input1", "Input2", "Input3" };

        for (var i = 0; i < p_Instruction.Sources.Count && i < s_Ports.Length; i++)
        {
            var s_Source = ReadVector(p_Instruction.Sources[i], p_Slots);
            s_Uniform &= s_Source.Uniform;
            m_Graph.Connect(s_Source.NodeId, s_Source.Port, s_Node.Id, s_Ports[i]);
        }

        CommitVector(p_Destination, (s_Node.Id, "Out", s_Uniform), p_Slots, p_Instruction.Saturate);
        return s_Node.Id;
    }

    private static readonly int[] s_XOnly = { 0 };

    /// <summary>
    /// The predicate stack of if-conversion: one 0/1 mask per open `if`, ANDed together.
    ///
    /// A node graph has no branches, so a branch becomes "compute it anyway and blend by the condition": every
    /// write inside an `if` turns into `dst = lerp(dst_before, value, predicate)`. That is exactly what the
    /// hardware does for a short branch, and it is the only shape this editor can express - which is why `if_nz`
    /// was the single biggest gap (119 shaders) and not just another opcode.
    ///
    /// ⚠ Sound only for branches WITHOUT side effects. A discard or a loop inside one is not covered, and those
    /// are declined rather than approximated.
    /// </summary>
    private readonly List<(string NodeId, string Port)> m_Predicates = new();

    /// <summary>
    /// The ONE component a branch tests, as a genuine scalar port that broadcasts across all four channels.
    ///
    /// ⛔⛔⛔ A BRANCH CONDITION IS A SCALAR, AND FEEDING IT AS A VECTOR MAKES EVERY CHANNEL TAKE ITS OWN BRANCH.
    /// `if_nz r1.x` tests exactly `r1.x`, but ReadVector over a single slot hands back the WHOLE producing port
    /// whenever that component happens to sit in lane 0 (it is already the right lane, so nothing is extracted).
    /// The predicate then reaches `Select` as a float4, `lerp` applies it component-wise, and channel y of the
    /// result silently follows `cond.y` - a completely different comparison. `lt r1.x, l(0.01), r2.z` over the
    /// terrain mask came out true in x and false in y, so `o2.y` took the untaken branch and wrote 0 where the
    /// game writes 1: the 255/255 on RT2.G that stood open across the whole 2d terrain family.
    ///
    /// Extracting the lane costs one MixOut and makes the value uniform, which is what a predicate must be.
    /// </summary>
    private (string NodeId, string Port, bool Uniform) ScalarCondition(AsmOperand p_Operand)
    {
        var s_Value = Read(p_Operand, 0);
        return (s_Value.NodeId, s_Value.Port, true);
    }

    private void Translate(AsmInstruction p_Instruction)
    {
        switch (p_Instruction.Opcode)
        {
            case "if_nz":
            case "if_z":
            {
                (string NodeId, string Port, bool Uniform) s_Condition = p_Instruction.Sources.Count > 0
                    ? ScalarCondition(p_Instruction.Sources[0])
                    : (Constant(1f).NodeId, "Out", true);

                // `if_z` runs the body when the value is ZERO, so its predicate is the complement.
                if (p_Instruction.Opcode == "if_z")
                {
                    var s_Not = NewNode("OneMinus");
                    m_Graph.Connect(s_Condition.NodeId, s_Condition.Port, s_Not.Id, "Input");
                    s_Condition = (s_Not.Id, "Out", s_Condition.Uniform);
                }

                m_Predicates.Add((s_Condition.NodeId, s_Condition.Port));
                return;
            }

            case "else":
            {
                if (m_Predicates.Count == 0)
                    return;

                var s_Current = m_Predicates[^1];
                var s_Complement = NewNode("OneMinus");
                m_Graph.Connect(s_Current.NodeId, s_Current.Port, s_Complement.Id, "Input");
                m_Predicates[^1] = (s_Complement.Id, "Out");
                return;
            }

            case "endif":
                if (m_Predicates.Count > 0)
                    m_Predicates.RemoveAt(m_Predicates.Count - 1);

                return;

            case "discard_nz":
            case "discard_z":
            {
                if (p_Instruction.Sources.Count == 0)
                    return;

                // ⛔ Evaluated HERE, not when the root is attached. The condition register is reused later in
                // most of these shaders, so reading it at the end would pick up a different value entirely -
                // the same reaching-definition mistake that cost three bugs in the frame matcher.
                var s_Condition = ScalarCondition(p_Instruction.Sources[0]);

                if (p_Instruction.Opcode == "discard_z")
                {
                    var s_Not = NewNode("OneMinus");
                    m_Graph.Connect(s_Condition.NodeId, s_Condition.Port, s_Not.Id, "Input");
                    s_Condition = (s_Not.Id, "Out", s_Condition.Uniform);
                }

                // A discard inside an `if` only fires when that branch is taken.
                if (m_Predicates.Count > 0)
                {
                    var s_Gate = NewNode("Multiply");
                    var s_Predicate = CombinedPredicate();
                    m_Graph.Connect(s_Condition.NodeId, s_Condition.Port, s_Gate.Id, "Input1");
                    m_Graph.Connect(s_Predicate.NodeId, s_Predicate.Port, s_Gate.Id, "Input2");
                    s_Condition = (s_Gate.Id, "Out", false);
                }

                m_PendingDiscards.Add((s_Condition.NodeId, s_Condition.Port));
                return;
            }
        }

        var s_Destination = p_Instruction.Destination;
        if (s_Destination == null)
            return;

        // ⛔ Name the REAL blocker. Vegetation dithers its alpha into `oMask` (SV_Coverage, one bit per MSAA
        // sample) and a node graph has no per-sample output at all, so the shader is out of reach whatever we do
        // with the arithmetic feeding it. Reported as `bfi`/`iadd` before, which sent me looking at integer
        // opcodes for a hole that is not in the opcodes.
        if (s_Destination.Kind == OperandKind.SystemOutput)
        {
            UntranslatedOpcodes.Add("writes " + s_Destination.Name);
            Warnings.Add($"'{s_Destination.Name}' is a per-sample system output; a node graph cannot express it.");
            return;
        }

        // ⛔⛔ THE COMPONENT A SOURCE SUPPLIES IS CHOSEN BY THE DESTINATION COMPONENT'S xyzw POSITION, NOT BY ITS
        // place in the write mask. `mul r0.zw, r0.yyyx, l(0,0,5,4)` writes z and w, so it reads swizzle/literal
        // positions 2 and 3 - giving 5 and 4. Walking the mask ordinally read positions 0 and 1 instead, so that
        // multiply silently became "times zero".
        //
        // This is invisible on any instruction whose mask starts at x and is contiguous, because there the two
        // readings coincide - which is every single instruction in the oil drum, the shader this translator was
        // proven bit-exact against. It took a probe written to use masks like .zw and .xz to expose it.
        var s_Slots = s_Destination.WriteMask.Length == 0 ? s_XOnly : s_Destination.WriteMask;
        var s_Results = new List<Value>();

        switch (p_Instruction.Opcode)
        {
            case "mov":
            {
                var s_Moved = ReadVector(p_Instruction.Sources[0], s_Slots);
                CommitVector(s_Destination, s_Moved, s_Slots, p_Instruction.Saturate);
                return;
            }

            // a + (-b) is a subtraction and the graph should SAY so: an author draws Subtract, never an Add
            // with a Negate hanging off one leg - the corpus had 2779 Negates and zero Subtracts, the same
            // literalism sqrt-as-Power once was.
            case "add" when p_Instruction.Sources.Count == 2 &&
                            p_Instruction.Sources[0].Negate != p_Instruction.Sources[1].Negate:
            {
                var s_NegatedIndex = p_Instruction.Sources[0].Negate ? 0 : 1;
                var s_Right = ReadVectorAbsOnly(p_Instruction.Sources[s_NegatedIndex], s_Slots);
                var s_Left = ReadVector(p_Instruction.Sources[1 - s_NegatedIndex], s_Slots);

                var s_Difference = NewNode("Subtract");
                m_Graph.Connect(s_Left.NodeId, s_Left.Port, s_Difference.Id, "Input1");
                m_Graph.Connect(s_Right.NodeId, s_Right.Port, s_Difference.Id, "Input2");

                CommitVector(s_Destination, (s_Difference.Id, "Out", s_Left.Uniform && s_Right.Uniform),
                    s_Slots, p_Instruction.Saturate);

                return;
            }

            case "mul":
            case "add":
            case "max":
            case "min":
            case "div":
                VectorOp(p_Instruction.Opcode switch
                {
                    "mul" => "Multiply", "add" => "Add", "max" => "Max", "min" => "Min", _ => "Divide",
                }, p_Instruction, s_Destination, s_Slots);

                return;

            case "lt":
            case "ge":
            case "eq":
            case "ne":
                m_MaskPorts.Add(VectorOp(p_Instruction.Opcode switch
                {
                    "lt" => "Less", "ge" => "GreaterEqual", "eq" => "Equal", _ => "NotEqual",
                }, p_Instruction, s_Destination, s_Slots) + "|Out");

                return;

            // `and` is never bit twiddling in these shaders: it is a comparison result being turned into a
            // value. `and r0.x, <mask>, l(0x3f800000)` is "1.0 where the test passed", `and r0.z, <mask>, r1.y`
            // is "r1.y or nothing", and two masks ANDed is a logical AND - all three are the SAME multiply once
            // the mask is a 0/1 float. Measured over every use in mp_017 (27 shaders, 6 read by hand): every
            // one has a comparison on at least one side.
            case "and":
            {
                if (p_Instruction.Sources.Count < 2 ||
                    (!OperandIsMask(p_Instruction.Sources[0], s_Slots) &&
                     !OperandIsMask(p_Instruction.Sources[1], s_Slots)))
                {
                    UntranslatedOpcodes.Add("and (bitwise)");
                    Warnings.Add("'and' on two values that are not comparison masks is genuine bit " +
                                 "manipulation; its destination keeps its old value.");
                    return;
                }

                var s_And = VectorOp("Multiply", p_Instruction, s_Destination, s_Slots);

                // Mask AND mask is still a mask, so a chain of them keeps translating.
                if (OperandIsMask(p_Instruction.Sources[0], s_Slots) &&
                    OperandIsMask(p_Instruction.Sources[1], s_Slots))
                    m_MaskPorts.Add(s_And + "|Out");

                return;
            }

            // `ftoi` is a float-to-int CONVERSION, and in the float domain that conversion is `trunc`: both
            // round toward zero and both give the same number for anything an int can hold. The decal shader
            // uses it as `movc(ftoi(v1.w), red, texture)`, i.e. purely as a test, and `trunc` answers that
            // identically - including at -0.5, where both give a zero that reads as false.
            // ⚠ What this does NOT survive is being fed to genuine bit manipulation. Those opcodes are gaps
            // already, and `and` only translates when a comparison mask is on one side, so a truncated float
            // cannot silently leak into one.
            case "ftoi":
            case "round_z_i":
                VectorOp("Trunc", p_Instruction, s_Destination, s_Slots);
                return;

            // `ieq <mask>, l(0)` is how the compiler spells NOT: a comparison wrote all-ones or zero and this
            // asks whether it was zero. With the mask as a 0/1 float that is exactly 1 - mask. The barbed wire
            // uses it to invert an alpha test before `discard_nz`.
            // Anything else under `ieq` is integer equality on real integers, which this translator does not
            // model, so it stays a stated hole rather than an approximation.
            case "ieq":
            case "ine":
            {
                var s_MaskFirst = OperandIsMask(p_Instruction.Sources.ElementAtOrDefault(0), s_Slots);
                var s_MaskSecond = OperandIsMask(p_Instruction.Sources.ElementAtOrDefault(1), s_Slots);
                var s_Mask = s_MaskFirst ? p_Instruction.Sources[0] : p_Instruction.Sources.ElementAtOrDefault(1);
                var s_Other = s_MaskFirst ? p_Instruction.Sources.ElementAtOrDefault(1) : p_Instruction.Sources[0];

                if (!(s_MaskFirst ^ s_MaskSecond) || !IsLiteral(s_Other, 0f))
                {
                    UntranslatedOpcodes.Add(p_Instruction.Opcode + " (integer)");
                    Warnings.Add($"'{p_Instruction.Opcode}' on values that are not a comparison mask against " +
                                 "zero is integer equality; its destination keeps its old value.");
                    return;
                }

                var s_Read = ReadVector(s_Mask!, s_Slots);
                var s_Node = NewNode(p_Instruction.Opcode == "ieq" ? "OneMinus" : "Saturate");
                m_Graph.Connect(s_Read.NodeId, s_Read.Port, s_Node.Id, "Input");
                CommitVector(s_Destination, (s_Node.Id, "Out", s_Read.Uniform), s_Slots, p_Instruction.Saturate);

                // Still a 0/1 mask, so it can feed another `and` or another `ieq`.
                m_MaskPorts.Add(s_Node.Id + "|Out");
                return;
            }

            // `sincos dstSin, dstCos, src` is the one instruction with TWO destinations, and either may be the
            // word `null` when the shader only wants one of them.
            case "sincos":
            {
                if (p_Instruction.Sources.Count < 2)
                {
                    UntranslatedOpcodes.Add("sincos");
                    Warnings.Add("'sincos' without both a cosine destination and a source is not translated.");
                    return;
                }

                var s_Angle = p_Instruction.Sources[^1];
                var s_CosDestination = p_Instruction.Sources[0];
                var s_CosSlots = s_CosDestination.WriteMask.Length == 0 ? s_XOnly : s_CosDestination.WriteMask;

                // ⛔ BOTH reads happen before EITHER write. `sincos r0.x, r1.x, r0.x` takes its angle from the
                // register it is about to overwrite, so committing the sine first would leave the cosine
                // reading sin(x) instead of x - the read-before-write law, now with two destinations.
                var s_SinInput = s_Destination.Kind == OperandKind.Null
                    ? default((string NodeId, string Port, bool Uniform)?)
                    : ReadVector(s_Angle, s_Slots);

                var s_CosInput = s_CosDestination.Kind == OperandKind.Null
                    ? default((string NodeId, string Port, bool Uniform)?)
                    : ReadVector(s_Angle, s_CosSlots);

                if (s_SinInput is { } s_Sine)
                {
                    var s_SinNode = NewNode("Sin");
                    m_Graph.Connect(s_Sine.NodeId, s_Sine.Port, s_SinNode.Id, "Input");
                    CommitVector(s_Destination, (s_SinNode.Id, "Out", s_Sine.Uniform), s_Slots,
                        p_Instruction.Saturate);
                }

                if (s_CosInput is { } s_Cosine)
                {
                    var s_CosNode = NewNode("Cos");
                    m_Graph.Connect(s_Cosine.NodeId, s_Cosine.Port, s_CosNode.Id, "Input");
                    CommitVector(s_CosDestination, (s_CosNode.Id, "Out", s_Cosine.Uniform), s_CosSlots,
                        p_Instruction.Saturate);
                }

                return;
            }

            case "movc":
            {
                // c ? a : b, with c the 0/1 mask the comparison nodes produce.
                var s_Node = NewNode("Select");
                var s_Ports = new[] { "Condition", "WhenTrue", "WhenFalse" };
                var s_Uniform = true;

                for (var i = 0; i < 3 && i < p_Instruction.Sources.Count; i++)
                {
                    var s_Source = ReadVector(p_Instruction.Sources[i], s_Slots);
                    s_Uniform &= s_Source.Uniform;
                    m_Graph.Connect(s_Source.NodeId, s_Source.Port, s_Node.Id, s_Ports[i]);
                }

                CommitVector(s_Destination, (s_Node.Id, "Out", s_Uniform), s_Slots, p_Instruction.Saturate);
                return;
            }

            case "mad":
            {
                // Two nodes for the whole instruction whatever its width, instead of two per component. `mad` is
                // the compiler's own fusion of a multiply and an add, so undoing it into exactly those two nodes
                // is the most faithful shape available without a dedicated node.
                var s_Multiply = NewNode("Multiply");
                var s_A = ReadVector(p_Instruction.Sources[0], s_Slots);
                var s_B = ReadVector(p_Instruction.Sources[1], s_Slots);
                m_Graph.Connect(s_A.NodeId, s_A.Port, s_Multiply.Id, "Input1");
                m_Graph.Connect(s_B.NodeId, s_B.Port, s_Multiply.Id, "Input2");

                // mad(a, b, -c) reads as product-minus-c, so it gets the Subtract shape too.
                var s_AddendNegated = p_Instruction.Sources[2].Negate;
                var s_C = s_AddendNegated
                    ? ReadVectorAbsOnly(p_Instruction.Sources[2], s_Slots)
                    : ReadVector(p_Instruction.Sources[2], s_Slots);

                var s_Sum = NewNode(s_AddendNegated ? "Subtract" : "Add");
                m_Graph.Connect(s_Multiply.Id, "Out", s_Sum.Id, "Input1");
                m_Graph.Connect(s_C.NodeId, s_C.Port, s_Sum.Id, "Input2");

                CommitVector(s_Destination, (s_Sum.Id, "Out", s_A.Uniform && s_B.Uniform && s_C.Uniform),
                    s_Slots, p_Instruction.Saturate);

                return;
            }

            case "sqrt":
            case "rsq":
            case "rcp":
            case "exp":
            case "log":
            case "frc":
            case "round_ne":
            case "round_pi":
            case "round_ni":
            case "round_z":
            case "deriv_rtx_coarse":
            case "deriv_rty_coarse":
            case "deriv_rtx":
            case "deriv_rty":
            {
                NoteClampDeviation(p_Instruction.Opcode);

                // The RAW variants: a translated shader must match the opcode, not the authoring-safe guard.
                var s_Kind = p_Instruction.Opcode switch
                {
                    "sqrt" or "rsq" => "SqrtRaw",
                    // ⛔ NOT the Exp node: the DXBC `exp` opcode is 2^x, measured with a known-answer probe
                    // (exp2(x) compiles to a bare `exp`; exp(x) compiles to mul-by-1.442695 then `exp`).
                    "exp" => "Exp2",
                    "log" => "Log2Raw",
                    "frc" => "Frac",
                    "round_pi" => "Ceil",
                    "round_ni" => "Floor",
                    "round_z" => "Trunc",
                    "deriv_rtx_coarse" or "deriv_rtx" => "DdxCoarse",
                    "deriv_rty_coarse" or "deriv_rty" => "DdyCoarse",
                    "rcp" => "",
                    _ => "Round",
                };

                var s_Input = ReadVector(p_Instruction.Sources[0], s_Slots);
                var s_Value = s_Kind.Length == 0 ? s_Input : ApplyVector(s_Kind, s_Input);

                // No reciprocal or reciprocal-square-root node: 1/x and 1/sqrt(x) say the same thing with nodes
                // that already exist, and keep the palette honest about what it really has.
                if (p_Instruction.Opcode is "rsq" or "rcp")
                {
                    var s_Divide = NewNode("Divide");
                    var s_One = Constant(1f);
                    m_Graph.Connect(s_One.NodeId, s_One.Port, s_Divide.Id, "Input1");
                    m_Graph.Connect(s_Value.NodeId, s_Value.Port, s_Divide.Id, "Input2");
                    s_Value = (s_Divide.Id, "Out", s_Value.Uniform);
                }

                CommitVector(s_Destination, s_Value, s_Slots, p_Instruction.Saturate);
                return;
            }

            case "dp3":
            {
                // ONE node. This is the single biggest saving in the whole translator: the tangent basis alone is
                // three dp3s, and expanding each into 3 multiplies + 2 adds plus its channel extractions was
                // most of the oil drum's node count.
                var s_A = ReadVector(p_Instruction.Sources[0], new[] { 0, 1, 2 });
                var s_B = ReadVector(p_Instruction.Sources[1], new[] { 0, 1, 2 });
                var s_Dot = NewNode("Dot");
                m_Graph.Connect(s_A.NodeId, s_A.Port, s_Dot.Id, "A");
                m_Graph.Connect(s_B.NodeId, s_B.Port, s_Dot.Id, "B");

                // Its output is a scalar, and a dot broadcasts that scalar to every written component.
                CommitVector(s_Destination, (s_Dot.Id, "Out", true), s_Slots, p_Instruction.Saturate);
                return;
            }

            case "dp2":
            case "dp4":
            {
                // Still expanded: Dot is a three-component node, so a two- or four-component dot would need
                // padding or a second node kind. Both are rare next to dp3.
                var s_Count = p_Instruction.Opcode == "dp2" ? 2 : 4;
                Value? s_Sum = null;

                for (var i = 0; i < s_Count; i++)
                {
                    var s_Term = Binary("Multiply", Read(p_Instruction.Sources[0], i),
                        Read(p_Instruction.Sources[1], i));

                    s_Sum = s_Sum == null ? s_Term : Binary("Add", s_Sum.Value, s_Term);
                }

                // A dot product reads swizzle positions 0..n-1 whatever the destination mask is, and broadcasts
                // the one scalar to every written component - so this loop counts, it does not index.
                foreach (var _ in s_Slots)
                    s_Results.Add(s_Sum!.Value);

                Commit(s_Destination, s_Results, p_Instruction.Saturate);
                return;
            }

            // Every form that ends in a plain texture read at a coordinate. The extra operands the variants carry
            // (an explicit LOD, a bias, a gradient pair) steer MIP SELECTION, not which texel maths happens, so
            // the node is the same one - and the sweep is what confirms that rather than this comment.
            case "sample":
            case "sample_indexable":
            case "sample_l":
            case "sample_l_indexable":
            case "sample_b":
            case "sample_b_indexable":
            case "sample_d":
            case "sample_d_indexable":
            {
                // ⛔ A cube or array fetch is NOT a 2D fetch with the third coordinate dropped: each has its own
                // node and takes a three-component coordinate. Anything still unrecognised is reported as a gap
                // rather than approximated - an approximation reads as a mistranslation, which is worse than a
                // stated hole.
                var s_Kind = p_Instruction.ResourceType.ToLowerInvariant() switch
                {
                    "" or "texture2d" => "Texture",
                    "texturecube" => "TextureCube",
                    "texture3d" => "Texture3D",
                    "texture2darray" => "TextureArray",
                    _ => null,
                };

                if (s_Kind == null)
                {
                    UntranslatedOpcodes.Add($"{p_Instruction.Opcode}({p_Instruction.ResourceType})");
                    Warnings.Add($"'{p_Instruction.ResourceType}' fetches have no node; that sample was skipped.");
                    return;
                }

                var s_Resource = p_Instruction.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Resource);
                var s_Texture = NewNode(s_Kind);
                s_Texture.Params["Register"] = (s_Resource?.Index ?? 1).ToString();

                // Two components for a flat fetch, three for the others - the coordinate's width is part of what
                // makes them different resources.
                var s_Axes = s_Kind == "Texture" ? new[] { 0, 1 } : new[] { 0, 1, 2 };
                var s_Coord = ReadVector(p_Instruction.Sources[0], s_Axes);
                m_Graph.Connect(s_Coord.NodeId, s_Coord.Port, s_Texture.Id, "Coord");

                // ⛔ `sample_l` carries an EXPLICIT mip level and it is not decoration: the terrain reads its
                // heightfield at level 0 on purpose. Dropping it left the mip to the pixel derivatives, which on
                // a mipped texture is a different texel and showed up as a full 255/255 difference - the last
                // thing standing between the terrain surface pass and an equivalent graph.
                if (p_Instruction.Opcode.StartsWith("sample_l", StringComparison.Ordinal) &&
                    p_Instruction.Sources.Count >= 4)
                {
                    var s_Lod = Read(p_Instruction.Sources[^1], 0);
                    m_Graph.Connect(s_Lod.NodeId, s_Lod.Port, s_Texture.Id, "Lod");
                }

                // The sampled vector is swizzled by the resource operand (t2.ywxz), and that swizzle is read by
                // destination POSITION like every other source - see the note at the top of this method. Kept as
                // LANES of the texture node, so `r0.xyz = sample.xyz` is one wire rather than three MixOuts.
                var s_Fetched = s_Slots.Select(p_Position => new Value(s_Texture.Id, "Out",
                    s_Resource != null && s_Resource.Swizzle.Length > p_Position
                        ? s_Resource.Swizzle[p_Position]
                        : p_Position)).ToList();

                var s_Sampled = Assemble(s_Fetched);
                CommitVector(s_Destination, s_Sampled, s_Slots, p_Instruction.Saturate);
                return;
            }

            default:
                UntranslatedOpcodes.Add(p_Instruction.Opcode);
                Warnings.Add($"opcode '{p_Instruction.Opcode}' is not translated; its destination keeps its old value.");
                return;
        }
    }

    private readonly HashSet<string> m_ClampsNoted = new(StringComparer.Ordinal);

    /// <summary>
    /// Two palette nodes guard their domain (Sqrt clamps at 0, Log2 at 1e-6) so a hand-authored graph cannot
    /// produce NaN. The game's opcodes do not: `sqrt` of a negative is NaN and `log` of zero is -INF. For inputs
    /// that stay in range - which is every shader measured so far - the translation is exact, but the difference
    /// is real and belongs in the log rather than in a comment nobody reads.
    /// </summary>
    private void NoteClampDeviation(string p_Opcode)
    {
        // Nothing to report any more: the translator uses the unguarded nodes, so it matches the opcode exactly
        // rather than "exactly for in-range inputs". Kept as the hook for the next node whose palette form and
        // opcode form diverge - that divergence is silent by nature and belongs in the log the moment it exists.
        _ = p_Opcode;
        _ = m_ClampsNoted;
    }

    private GraphNode? m_CoverageNode;

    /// <summary>
    /// Recognises the supersampled alpha-test idiom and folds it into an AlphaCoverage node: K samples of one
    /// texture's ALPHA at uv + a·ddx + b·ddy offsets, thresholded, one coverage bit per tap, written to
    /// `oMask` (SV_Coverage). Measured on the vegetation-alpha pixel shaders: 2 taps at ±(0.25, 0.25) on the
    /// plain solutions, the standard 4-tap rotated grid on the instanced ones.
    ///
    /// The match is by bounded dataflow, not by sequence: backward reachability from the oMask write down to
    /// the first derivative, then an audit — only idiom opcodes in the cluster, every sample reading .w of the
    /// SAME texture, every threshold equal, and the mul/mad literals matching the measured tap magnitudes
    /// exactly. The audit stops a LOOK-ALIKE with different offsets from silently becoming the standard
    /// pattern (the node emits the measured tables, so a variant arrangement must decline, not approximate).
    /// Anything that fails keeps the existing behaviour: the coverage path is dropped with the loud warning.
    /// </summary>
    private void TryMatchAlphaCoverage(List<AsmInstruction> p_Instructions)
    {
        var s_MaskIndex = p_Instructions.FindIndex(p_I =>
            p_I.Destination is { Kind: OperandKind.SystemOutput } s_D &&
            s_D.Name.StartsWith("oMask", StringComparison.Ordinal));
        if (s_MaskIndex < 0 || m_Contract == null)
            return;

        // The derivatives name the UV: both coarse derivs must read the same INPUT register, and the
        // contract must call that interpolator TexCoord — the node's coordinate default is the contract's
        // UV field, so a coverage idiom running on any other interpolator must decline.
        var s_DerivIndices = Enumerable.Range(0, s_MaskIndex)
            .Where(p_I => p_Instructions[p_I].Opcode is "deriv_rtx_coarse" or "deriv_rty_coarse" &&
                          p_Instructions[p_I].Sources.FirstOrDefault()?.Kind == OperandKind.Input)
            .ToList();
        if (s_DerivIndices.Count < 2)
            return;

        var s_UvRegisters = s_DerivIndices.Select(p_I => p_Instructions[p_I].Sources[0].Index).Distinct().ToList();
        if (s_UvRegisters.Count != 1)
            return;

        var s_UvElement = m_Contract.Inputs.FirstOrDefault(p_I => p_I.Register == s_UvRegisters[0] &&
            p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase));
        if (s_UvElement == null || m_Contract.FieldNameFor(s_UvElement.Index) != "TexCoord")
            return;

        // Backward reachability over temp registers, bounded to [first deriv .. oMask write].
        var s_First = s_DerivIndices.Min();
        var s_Needed = p_Instructions[s_MaskIndex].Sources
            .Where(p_S => p_S.Kind == OperandKind.Temp).Select(p_S => p_S.Index).ToHashSet();
        var s_Cluster = new HashSet<int> { s_MaskIndex };
        for (var i = s_MaskIndex - 1; i >= s_First; i--)
        {
            if (p_Instructions[i].Destination is not { Kind: OperandKind.Temp } s_Dest ||
                !s_Needed.Contains(s_Dest.Index))
                continue;

            s_Cluster.Add(i);
            foreach (var s_Source in p_Instructions[i].Sources.Where(p_S => p_S.Kind == OperandKind.Temp))
                s_Needed.Add(s_Source.Index);
        }

        // Audit the cluster's shape.
        var s_Samples = new List<AsmInstruction>();
        var s_TapLiterals = new List<float>();
        float? s_Threshold = null;
        foreach (var s_Index in s_Cluster.OrderBy(p_I => p_I))
        {
            var s_Instruction = p_Instructions[s_Index];
            switch (s_Instruction.Opcode)
            {
                case "deriv_rtx_coarse" or "deriv_rty_coarse" or "add" or "and" or "bfi" or "iadd":
                    break;

                case "mul" or "mad":
                    var s_Literal = s_Instruction.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Literal);
                    if (s_Literal == null)
                        return;
                    s_TapLiterals.AddRange(s_Literal.Literal);
                    break;

                case "ge":
                    var s_Compare = s_Instruction.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Literal)?
                        .Literal?.FirstOrDefault(p_V => p_V != 0f);
                    if (s_Compare is not { } s_Value || (s_Threshold ??= s_Value) != s_Value)
                        return;
                    break;

                case var s_Opcode when s_Opcode.StartsWith("sample", StringComparison.Ordinal):
                    s_Samples.Add(s_Instruction);
                    break;

                default:
                    return;
            }
        }

        if (s_Threshold == null || (s_Samples.Count != 2 && s_Samples.Count != 4))
            return;

        // Every tap must read the ALPHA of one texture: the resource swizzle position at the written lane
        // is the channel the sample hands over, and it must be w on all of them.
        var s_TextureRegisters = new HashSet<int>();
        foreach (var s_Sample in s_Samples)
        {
            var s_Resource = s_Sample.Sources.FirstOrDefault(p_S => p_S.Kind == OperandKind.Resource);
            var s_Lane = s_Sample.Destination?.WriteMask.FirstOrDefault() ?? 0;
            if (s_Resource == null || s_Sample.Destination?.WriteMask.Length != 1 ||
                s_Resource.Swizzle.ElementAtOrDefault(s_Lane) != 3)
                return;

            s_TextureRegisters.Add(s_Resource.Index);
        }

        if (s_TextureRegisters.Count != 1)
            return;

        // The offsets must be EXACTLY the measured tap magnitudes for this tap count.
        var s_Magnitudes = s_TapLiterals.Select(Math.Abs).OrderBy(p_V => p_V).ToList();
        var s_Expected = s_Samples.Count == 2
            ? Enumerable.Repeat(0.25f, 8)
            : Enumerable.Repeat(0.125f, 8).Concat(Enumerable.Repeat(0.375f, 8));
        if (!s_Magnitudes.SequenceEqual(s_Expected.OrderBy(p_V => p_V)))
            return;

        // The cluster must be self-contained: nothing later may read a lane it wrote before overwriting it,
        // or consuming the cluster would leave that reader with a stale register.
        var s_Owned = new HashSet<(int Register, int Component)>();
        foreach (var s_Index in s_Cluster)
            if (p_Instructions[s_Index].Destination is { Kind: OperandKind.Temp } s_Dest)
                foreach (var s_Component in s_Dest.WriteMask.Length > 0 ? s_Dest.WriteMask : new[] { 0, 1, 2, 3 })
                    s_Owned.Add((s_Dest.Index, s_Component));

        for (var i = s_First; i < p_Instructions.Count; i++)
        {
            if (s_Cluster.Contains(i))
                continue;

            foreach (var s_Source in p_Instructions[i].Sources.Where(p_S => p_S.Kind == OperandKind.Temp))
            {
                var s_Read = s_Source.Swizzle.Length > 0 ? s_Source.Swizzle : new[] { 0, 1, 2, 3 };
                if (s_Read.Any(p_C => s_Owned.Contains((s_Source.Index, p_C))))
                    return;
            }

            if (p_Instructions[i].Destination is { Kind: OperandKind.Temp } s_Overwrite)
                foreach (var s_Component in s_Overwrite.WriteMask.Length > 0 ? s_Overwrite.WriteMask : new[] { 0, 1, 2, 3 })
                    s_Owned.Remove((s_Overwrite.Index, s_Component));
        }

        foreach (var s_Index in s_Cluster)
            m_Consumed.Add(s_Index);

        m_CoverageNode = NewNode("AlphaCoverage");
        m_CoverageNode.Params["Register"] = s_TextureRegisters.First().ToString();
        m_CoverageNode.Params["Taps"] = s_Samples.Count.ToString();
        m_CoverageNode.Params["Threshold"] = s_Threshold.Value.ToString(CultureInfo.InvariantCulture);
        m_CoverageNode.Comment = $"Folded from the shader's own alpha-to-coverage: {s_Samples.Count} alpha " +
                                 $"taps at derivative offsets, one coverage bit each, threshold {s_Threshold}.";
    }

    /// <summary>Wires whatever ended up in o0..o3 into a raw root, rebuilding each target as a float4.</summary>
    private void AttachRoot()
    {
        var s_Root = NewNode("RawRoot");

        if (m_CoverageNode != null)
            m_Graph.Connect(m_CoverageNode.Id, "Out", s_Root.Id, "Coverage");

        for (var s_Target = 0; s_Target < 4; s_Target++)
        {
            if (!m_Registers.TryGetValue("o" + s_Target, out var s_Slots) || s_Slots.All(p_S => p_S == null))
                continue;

            // Assembled rather than always appended: a target whose four components already sit in one node -
            // which is the normal case now that whole vectors survive - wires straight through.
            var s_Target4 = Assemble(Enumerable.Range(0, 4)
                .Select(p_I => s_Slots[p_I] ?? Constant(0f)).ToList());

            // The alpha test rides on the first target: it has to run before anything is written, and this is
            // the only place the emitter guarantees will be reached.
            var s_Routed = s_Target == 0
                ? WithDiscards((s_Target4.NodeId, s_Target4.Port))
                : (s_Target4.NodeId, s_Target4.Port);

            m_Graph.Connect(s_Routed.NodeId, s_Routed.Port, s_Root.Id, "Target" + s_Target);
        }
    }
}
