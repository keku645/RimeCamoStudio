using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor.Emit;

public class EmitResult
{
    public string Hlsl { get; set; } = "";
    public List<string> Errors { get; } = new();

    /// <summary>Non-fatal findings (an instance output nobody wired, a consumer falling back to a pin
    /// default). Shown, never failed on — a dropped detail the user should hear about is not a broken emit.</summary>
    public List<string> Warnings { get; } = new();

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Turns a graph into a BF3 GBufferLayout0 pixel shader.
///
/// The shell (cbuffer register/offsets, texture registers, interpolator semantics and the four render
/// targets) is not a guess: it was read off the vanilla shader with fxc /dumpbin, because
/// replace_shader_bytecode swaps only the bytecode and leaves the shaderdb binding table untouched — so a
/// generated shader has to consume exactly the slots the target already declares.
/// </summary>
public class HlslEmitter
{
    private sealed class Scope : IEmitScope
    {
        public required HlslEmitter Emitter { get; init; }
        public required GraphNode Node { get; init; }
        public required StringBuilder Body { get; init; }

        /// <summary>The target's contract, so an input node can name the right PsIn field for this family.</summary>
        public ShaderContract Contract => Emitter.Contract ?? ShaderContract.RigidMeshDefault();

        public string In(string p_Port)
        {
            var s_PortDef = Node.Def.FindInput(p_Port);
            if (s_PortDef == null)
                return "0";

            var s_Connection = Emitter.m_Graph.ConnectionInto(Node.Id, p_Port);
            if (s_Connection == null)
            {
                // An unconnected input takes the value typed in the properties panel, falling back to the
                // node's own default. A value that does not parse is reported rather than silently ignored.
                var s_Override = Node.GetInputOverride(p_Port);
                if (s_Override != null && s_PortDef.IsEditable)
                {
                    var s_Literal = PortTypeUtils.FormatLiteral(s_Override, s_PortDef.Type);
                    if (s_Literal != null)
                        return s_Literal;

                    Emitter.m_Result.Errors.Add(
                        $"'{Node.Def.Title}' input '{p_Port}': '{s_Override}' is not a valid " +
                        $"{s_PortDef.Type.ComponentCount()}-component value.");
                    return s_PortDef.Default ?? "0";
                }

                if (s_PortDef.Default != null)
                    return s_PortDef.Default;

                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' has nothing connected to its required input '{p_Port}'.");
                return "0";
            }

            if (!Emitter.m_Outputs.TryGetValue((s_Connection.FromNode, s_Connection.FromPort), out var s_Source))
            {
                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' input '{p_Port}' reads an output that was never produced.");
                return "0";
            }

            var s_Adapted = PortTypeUtils.Adapt(s_Source.Expression, s_Source.Type, s_PortDef.Type);
            if (s_Adapted == null)
            {
                Emitter.m_Result.Errors.Add(
                    $"'{Node.Def.Title}' input '{p_Port}' expects {s_PortDef.Type} but receives {s_Source.Type}.");
                return "0";
            }

            return s_Adapted;
        }

        public bool IsConnected(string p_Port) =>
            Emitter.m_Graph.ConnectionInto(Node.Id, p_Port) != null;

        public bool HasExplicitValue(string p_Port) =>
            IsConnected(p_Port) || Node.GetInputOverride(p_Port) != null;

        public void Out(string p_Port, string p_Expression)
        {
            var s_PortDef = Node.Def.FindOutput(p_Port);
            var s_Type = s_PortDef?.Type ?? ShaderPortType.SptVec4;
            var s_Name = Declare($"{Sanitize(Node.Def.Kind)}_{Sanitize(p_Port)}", s_Type, p_Expression);
            Emitter.m_Outputs[(Node.Id, p_Port)] = (s_Name, s_Type);
        }

        public void Line(string p_Code) => Body.AppendLine($"    {p_Code}");

        public string Declare(string p_Hint, ShaderPortType p_Type, string p_Expression)
        {
            var s_Name = $"{p_Hint}_{Emitter.m_TempCounter++}";
            Body.AppendLine($"    {p_Type.HlslType()} {s_Name} = {p_Expression};");
            return s_Name;
        }

        public string Param(string p_Name) => Node.GetParam(p_Name);

        public string HoistFunction(string p_Signature, string p_Body)
        {
            var s_Name = $"Script_{Emitter.m_TempCounter++}";
            Emitter.m_Functions.AppendLine($"float4 {s_Name}({p_Signature})");
            Emitter.m_Functions.AppendLine("{");
            foreach (var s_Line in p_Body.Replace("\r", "").Split('\n'))
                Emitter.m_Functions.AppendLine($"    {s_Line}");
            Emitter.m_Functions.AppendLine("}");
            Emitter.m_Functions.AppendLine();
            return s_Name;
        }

        public float ParamScalar(string p_Name) =>
            float.TryParse(Node.GetParam(p_Name), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Value)
                ? s_Value
                : 0.0f;

        public TEnum ParamEnum<TEnum>(string p_Name) where TEnum : struct, Enum =>
            Enum.TryParse<TEnum>(Node.GetParam(p_Name), out var s_Value) ? s_Value : default;

        private static string Sanitize(string p_Text)
        {
            var s_Builder = new StringBuilder();
            foreach (var s_Char in p_Text)
                s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

            return s_Builder.ToString();
        }
    }

    private ShaderGraph m_Graph = new();
    private EmitResult m_Result = new();
    private readonly Dictionary<(string Node, string Port), (string Expression, ShaderPortType Type)> m_Outputs = new();
    private readonly StringBuilder m_Functions = new();
    private int m_TempCounter;

    /// <summary>
    /// The target's measured contract, which decides the PsIn/PsOut shape. Null means the rigid-mesh default,
    /// and that path emits exactly what it always did so the bit-exact barrel result stays valid.
    /// </summary>
    public ShaderContract? Contract { get; set; }

    public EmitResult Emit(ShaderGraph p_Graph)
    {
        m_Result = new EmitResult();

        // Instance nodes expand into their fragments HERE, at the single choke point every consumer of the
        // emitter shares — preview, bake, fingerprinting and per-variant compilation all see the flat graph.
        // A graph without instance nodes passes through as the same object, so nothing changes for it.
        try
        {
            p_Graph = Graph.InstanceExpander.Expand(p_Graph, m_Result.Warnings.Add);
        }
        catch (InvalidOperationException s_Exception)
        {
            m_Result.Errors.Add(s_Exception.Message);
            return m_Result;
        }

        m_Graph = p_Graph;
        m_Outputs.Clear();
        m_Functions.Clear();
        m_TempCounter = 0;

        // Whether the graph's saved interpolator indices already speak THIS contract's numbering (it was
        // translated against this very family). If so, the probe-twin index shift must not apply — a graph
        // born from a probe solution stores the shifted indices verbatim, and shifting them again reads
        // past the layout. Null TranslatedFamily = base numbering, the historical invariant.
        if (Contract != null)
        {
            Contract.GraphSpeaksThisContract = p_Graph.TranslatedFamily == Contract.Family.ToString();
            Contract.GraphFamily = p_Graph.TranslatedFamily;
        }

        var s_Root = p_Graph.Root;
        if (s_Root == null)
        {
            m_Result.Errors.Add("The graph has no root node, so there is nothing to write.");
            return m_Result;
        }

        // More than one root is ambiguous: picking the first silently would emit whichever happened to be
        // added first, which is not something the author can see on the canvas.
        var s_RootCount = p_Graph.Nodes.Count(p_N => p_N.Def.IsRoot);
        if (s_RootCount > 1)
        {
            m_Result.Errors.Add(
                $"The graph has {s_RootCount} root nodes. Delete all but one — a shader writes one set of " +
                "render targets, so there is no way to honour both.");
            return m_Result;
        }

        var s_Ordered = p_Graph.TopologicalFromRoot();
        var s_Body = new StringBuilder();

        foreach (var s_Node in s_Ordered)
        {
            var s_Scope = new Scope { Emitter = this, Node = s_Node, Body = s_Body };
            s_Node.Def.Emit(s_Scope, s_Node);
        }

        m_Result.Hlsl = BuildShell(p_Graph, s_Body.ToString(), Contract, m_Functions.ToString());
        return m_Result;
    }

    /// <summary>
    /// Every external constant the graph reads, as (buffer, register, element, safe name): the external-constant nodes, and the
    /// layer constants of an emblem node (Palette.EmblemFields — the same list the bake adds to the shader's table), and the frame
    /// constants of a projected slot (Palette.EmblemFrameFields).
    /// </summary>
    private static List<(string Buffer, int Register, int Element, string Name)> ExternalNodes(ShaderGraph p_Graph) =>
        p_Graph.Nodes
            .Where(p_N => p_N.Kind == "ExternalConstant")
            .Select(p_N => (
                Buffer: p_N.GetParam("Buffer") is { Length: > 0 } s_B ? s_B : "externalConstants",
                Register: int.TryParse(p_N.GetParam("Register"), out var s_R) ? s_R : 1,
                Element: int.TryParse(p_N.GetParam("Element"), out var s_E) ? s_E : 0,
                Name: Palette.SafeIdentifier(p_N.GetParam("Name"))))
            .Concat(p_Graph.Nodes
                .Where(p_N => p_N.Kind == "EmblemLayers")
                .SelectMany(Palette.EmblemFields)
                .Select(p_F => (Buffer: "externalConstants", Register: p_F.Buffer, Element: p_F.Element, Name: p_F.Name)))
            .Concat(p_Graph.Nodes
                .Where(p_N => p_N.Kind == "EmblemProjection")
                .SelectMany(Palette.EmblemFrameFields)
                .Select(p_F => (Buffer: "externalConstants", Register: p_F.Buffer, Element: p_F.Element, Name: p_F.Name)))
            .ToList();

    private static string BuildShell(ShaderGraph p_Graph, string p_Body, ShaderContract? p_Contract,
        string p_Functions = "")
    {
        var s_Builder = new StringBuilder();
        s_Builder.AppendLine("// Generated by RimeShaderEditor. Do not edit by hand.");
        s_Builder.AppendLine($"// Graph:  {p_Graph.Name}");
        s_Builder.AppendLine($"// Target: {p_Graph.TargetShader}");
        s_Builder.AppendLine("//");
        s_Builder.AppendLine("// Slot layout is dictated by the target shader's existing shaderdb binding table:");
        s_Builder.AppendLine("//   cb2 viewConstants, s0 sampler0, t1/t2 textures, TEXCOORD0..4 interpolators,");
        s_Builder.AppendLine("//   SV_Target0..3 (DeferredShadingGBufferLayout0).");
        s_Builder.AppendLine();
        s_Builder.AppendLine("cbuffer viewConstants : register(b2)");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    float  time                : packoffset(c0.x);");
        s_Builder.AppendLine("    float4 screenSize          : packoffset(c1);");
        s_Builder.AppendLine("    float4 viewportZMinMaxKzKw : packoffset(c19);");
        s_Builder.AppendLine("    float3 cameraPos           : packoffset(c20);");

        // Any OTHER view constant the graph reads joins the same block at its measured register. It has to be
        // this block and not a second one, because a cbuffer is declared once per binding - and the four above
        // stay hardcoded because the drum's bit-exact output is emitted from them.
        foreach (var s_Field in ExternalNodes(p_Graph)
                     .Where(p_F => p_F.Buffer == "viewConstants")
                     .DistinctBy(p_F => p_F.Name)
                     .OrderBy(p_F => p_F.Element))
            s_Builder.AppendLine($"    float4 {s_Field.Name,-20} : packoffset(c{s_Field.Element});");

        s_Builder.AppendLine("};");
        s_Builder.AppendLine();
        // ⛔ Declared from the graph, never from a fixed list: these are the target shader's OWN parameters, read
        // out of its bytecode. Each one gets a whole 16-byte register because that is how DICE's compiler laid
        // them out - measured across mp_017, 25,246 of 25,246 externals sit alone in their slot, with no packing -
        // so packoffset(cN) reproduces their layout exactly and a graph can read cbN[M] and mean it.
        // viewConstants is excluded: its fields were merged into the block declared above, because a cbuffer can
        // only be declared once per binding.
        var s_Externals = ExternalNodes(p_Graph)
            .Where(p_N => p_N.Buffer != "viewConstants")
            .GroupBy(p_N => (p_N.Buffer, p_N.Register))
            .ToList();

        foreach (var s_Buffer in s_Externals.OrderBy(p_B => p_B.Key.Register))
        {
            // ⚠ Sanitised: DICE's unnamed global buffer is called `$Globals` in the bytecode, and `$` is not a
            // legal HLSL identifier - 18 shaders failed to compile on exactly that. The NAME of a cbuffer binds
            // nothing; the register does, so renaming it is free.
            s_Builder.AppendLine(
                $"cbuffer {Palette.SafeIdentifier(s_Buffer.Key.Buffer)} : register(b{s_Buffer.Key.Register})");
            s_Builder.AppendLine("{");

            foreach (var s_Field in s_Buffer.DistinctBy(p_F => p_F.Name).OrderBy(p_F => p_F.Element))
                s_Builder.AppendLine($"    float4 {s_Field.Name,-40} : packoffset(c{s_Field.Element});");

            s_Builder.AppendLine("};");
            s_Builder.AppendLine();
        }

        // As many samplers as the flavour uses. A lightmapped one reads the engine's textures through s0 and
        // its own material through the next ones, so declaring only s0 would force every material fetch
        // through the engine's sampler — whose addressing does not tile.
        var s_Samplers = Math.Max(1, (p_Contract?.Resources.Count(p_R => p_R.IsSampler) ?? 1));

        // ...and never fewer than a node PINNED to its own sampler reads through (see Palette.PinnedSampler).
        foreach (var s_Node in p_Graph.Nodes)
            if (Palette.PinnedSampler(s_Node) is { } s_Pinned && int.TryParse(s_Pinned["sampler".Length..], out var s_Index))
                s_Samplers = Math.Max(s_Samplers, s_Index + 1);
        for (var s_S = 0; s_S < s_Samplers; s_S++)
            s_Builder.AppendLine($"SamplerState sampler{s_S}          : register(s{s_S});");

        // Declare as many texture slots as the graph actually reads, never fewer: hardcoding t1/t2 (the drum's
        // two) meant every shader with more textures failed to compile with "undeclared identifier
        // texture_TextureN", which surfaced only as a paused preview. Two is the floor so the verified drum
        // output stays byte-for-byte what it was.
        var s_HighestSlot = 2;

        // ⚠ A slot's HLSL type comes from the node that reads it: a cube fetch takes a three-component direction
        // and a Texture2D declaration simply will not compile against it. Declared per slot rather than all as
        // Texture2D, which is what limited the translator to flat textures.
        var s_SlotTypes = new Dictionary<int, string>();

        foreach (var s_Node in p_Graph.Nodes)
        {
            var s_Type = s_Node.Kind switch
            {
                "Texture" or "NormalMap" or "AlphaCoverage" or "UdkTextureSample"
                    or "UdkAntialiasedTextureMask" or "StickerCoordinates" or "EmblemLayers" => "Texture2D",
                "TextureCube" => "TextureCube",
                "Texture3D" => "Texture3D",
                "TextureArray" => "Texture2DArray",
                _ => null,
            };

            if (s_Type == null || !int.TryParse(s_Node.GetParam("Register"), out var s_Slot))
                continue;

            s_HighestSlot = Math.Max(s_HighestSlot, s_Slot);
            s_SlotTypes[s_Slot] = s_Type;
        }

        // ⛔ REGISTERS THE ENGINE OWNS ARE NOT OURS TO NUMBER OVER. On a lightmapped flavour the engine holds
        // t1..t4 (the lightmap's irradiance/direction/sky-visibility) and the material starts at t5 — so
        // walking 1..n and declaring a material texture in every slot would have the shader SAMPLE THE
        // LIGHTMAP believing it is the albedo. Those registers are declared here under their real names, and
        // skipped below. Without a parsed contract there are none and the loop is exactly what it was.
        // ⛔ …UNLESS A NODE OF THE GRAPH READS THAT REGISTER. Shaders outside the weapon presets sample what
        // the contract calls an engine texture FROM THE GRAPH — the glow and the glass read the sky cubemap
        // at t1 — and the nodes name their slots by the register convention (HlslNames.TextureVar). Declaring
        // the engine name there and skipping the slot left the node's own name undeclared, so the material's
        // graph would not compile at all ("undeclared identifier 'texture_Texture'", measured on red_glow,
        // 2026-09-18) — and two names on one register is not legal either. The node wins: it is what the
        // emitted code references, and its declaration carries the right TYPE (a cube fetch needs TextureCube).
        var s_EngineTextures = (p_Contract?.EngineTextures ?? new List<ResourceBinding>())
            .Where(p_R => p_R.Register > 0 && !s_SlotTypes.ContainsKey(p_R.Register))
            .ToList();

        foreach (var s_Engine in s_EngineTextures)
            s_Builder.AppendLine($"{"Texture2D",-15} {s_Engine.Name,-40} : register(t{s_Engine.Register});");

        var s_Taken = s_EngineTextures.Select(p_R => p_R.Register).ToHashSet();

        for (var s_Slot = 1; s_Slot <= s_HighestSlot; s_Slot++)
        {
            if (s_Taken.Contains(s_Slot))
                continue;

            var s_Name = s_Slot == 1 ? "texture_Texture" : "texture_Texture" + s_Slot;
            var s_Declared = s_SlotTypes.TryGetValue(s_Slot, out var s_Type) ? s_Type : "Texture2D";
            s_Builder.AppendLine($"{s_Declared,-15} {s_Name,-17} : register(t{s_Slot});");
        }

        s_Builder.AppendLine();
        // Shape from the target's measured contract; without one, the rigid-mesh layout the editor has always
        // assumed, emitted identically so the verified barrel output does not move.
        var s_Contract = p_Contract ?? ShaderContract.RigidMeshDefault();

        // The cbuffer-transport probe variant reads its SH rows from a b0 block the engine fills per
        // draw; binding is by REGISTER, so the block name is free but the field ORDER is the layout.
        if (s_Contract.Probes == ProbeTransport.Cbuffer && !s_Builder.ToString().Contains("register(b0)"))
        {
            s_Builder.AppendLine("cbuffer lightProbes : register(b0)");
            s_Builder.AppendLine("{");
            s_Builder.AppendLine("    float4 lightProbeShR : packoffset(c0);");
            s_Builder.AppendLine("    float4 lightProbeShG : packoffset(c1);");
            s_Builder.AppendLine("    float4 lightProbeShB : packoffset(c2);");
            s_Builder.AppendLine("    float4 lightProbeShO : packoffset(c3);");
            s_Builder.AppendLine("};");
            s_Builder.AppendLine();
        }

        s_Builder.AppendLine("struct PsIn");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    float4 Position    : SV_Position;");
        foreach (var s_Interp in s_Contract.Interpolators)
        {
            var s_Type = s_Interp.IsInteger ? "uint  " : "float4";
            var s_Name = s_Contract.FieldNameFor(s_Interp.Index);
            s_Builder.AppendLine($"    {s_Type} {s_Name,-11}: TEXCOORD{s_Interp.Index};");
        }

        s_Builder.AppendLine("};");
        s_Builder.AppendLine();
        s_Builder.AppendLine("struct PsOut");
        s_Builder.AppendLine("{");

        // As many targets as the target shader's own signature declares: a forward (transparent) solution
        // has ONE, distant terrain has three, the GBuffer four. Declaring extras is not harmless — the
        // signature is part of what the diff harness and the engine-side permutation see. A contract with
        // no parsed signature (the default, the node tests) keeps the four-target GBuffer shape.
        var s_TargetCount = s_Contract.RenderTargets is >= 1 and <= 4 ? s_Contract.RenderTargets : 4;
        for (var s_T = 0; s_T < s_TargetCount; s_T++)
            s_Builder.AppendLine($"    float4 Target{s_T} : SV_Target{s_T};");

        // Declared only when the root actually writes it: the vegetation-alpha family dithers its alpha
        // test into SV_Coverage (one bit per MSAA sample), and an undeclared system output cannot be
        // written — while an unwritten declared one would zero the coverage and discard every pixel.
        if (p_Graph.Root is { } s_RootNode &&
            p_Graph.ConnectionInto(s_RootNode.Id, "Coverage") != null)
            s_Builder.AppendLine("    uint   Coverage : SV_Coverage;");

        s_Builder.AppendLine("};");
        s_Builder.AppendLine();

        // Script-node functions, hoisted here because a body with `return` cannot live inside main.
        if (p_Functions.Length > 0)
            s_Builder.Append(p_Functions);

        s_Builder.AppendLine("PsOut main(PsIn i)");
        s_Builder.AppendLine("{");
        s_Builder.AppendLine("    PsOut o;");
        s_Builder.Append(p_Body);
        s_Builder.AppendLine("    return o;");
        s_Builder.AppendLine("}");

        return s_Builder.ToString();
    }
}
