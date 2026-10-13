using System;
using System.Collections.Generic;
using System.Linq;
using RimeShaderEditor.Emit;

namespace RimeShaderEditor.Graph;

/// <summary>
/// The UDK-style half of the catalog: nodes wearing the names, ports and PROPERTIES of the original
/// UE3-era material expressions, so an author who knows that editor can lay a graph out the way they
/// already think — while the bake still produces this engine's shader, because each of these emits
/// exactly what its native twin emits.
///
/// ⛔ THE NAMES AND DEFAULTS ARE NOT INVENTED. They are read from the material expression classes of a
/// UDK source tree (MaterialExpression*.uc), which is the only authority for this vocabulary: the
/// property names, their defaults and their documented meaning come from there, comments included.
///
/// ⛔ A property with no counterpart here is NOT silently accepted: it is declared with the reason it
/// does nothing (UnMirrorU/V depend on a material being mirrored, a concept this engine has no notion
/// of). Quietly ignoring an author's setting is how a graph lies about what it will bake.
/// </summary>
public static partial class Palette
{
    private static void AddUdkNodes()
    {
        // ── Coordinates ──────────────────────────────────────────────────────────────────────────────
        // MaterialExpressionTextureCoordinate: CoordinateIndex + UTiling/VTiling (both default 1.0),
        // documented there as "scaling the U/V component of the vertex UVs by the specified amount" —
        // which is this engine's own tiling multiply, so the twin emits the same expression.
        Add(new NodeDef
        {
            Kind = "UdkTextureCoordinate",
            Title = "TextureCoordinate",
            Category = "UDK · Coordinates",
            Style = NodeStyle.Udk,
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Params =
            {
                new ParamDef
                {
                    Name = "CoordinateIndex", Kind = ParamKind.Choice, Default = "auto",
                    Choices = { "auto", "0", "1", "2", "3", "4", "5", "6", "7" },
                },
                new ParamDef { Name = "UTiling", Kind = ParamKind.Scalar, Default = "1.0" },
                new ParamDef { Name = "VTiling", Kind = ParamKind.Scalar, Default = "1.0" },

                // Present so the property is not silently missing, and refused for a measured reason.
                new ParamDef { Name = "UnMirrorU", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "UnMirrorV", Kind = ParamKind.Bool, Default = "false" },
            },
            Description = "Vertex UVs, scaled by UTiling/VTiling. CoordinateIndex picks the interpolator " +
                          "('auto' keeps the target's measured UV field). UnMirrorU/V do NOTHING here: they " +
                          "undo a mirrored material, and this engine has no mirrored-material flag to undo.",
            CanvasBadge = p_N => p_N.GetParam("CoordinateIndex") is var s_Index && s_Index != "auto"
                ? $"TC{s_Index}"
                : null,
            Emit = (p_S, p_N) =>
            {
                var s_Index = p_N.GetParam("CoordinateIndex");
                var s_Field = s_Index != "auto" && int.TryParse(s_Index, out var s_Number)
                    ? p_S.Contract.FieldNameForGraphIndex(s_Number)
                    : "TexCoord";

                p_S.Out("Out", TiledBy(p_S.ParamScalar("UTiling"), p_S.ParamScalar("VTiling"),
                    $"i.{s_Field}.xy"));
            },
        });

        // MaterialExpressionPanner: scrolls the coordinate over time by SpeedX/SpeedY.
        Add(new NodeDef
        {
            Kind = "UdkPanner",
            Title = "Panner",
            Category = "UDK · Coordinates",
            Style = NodeStyle.Udk,
            Inputs =
            {
                new PortDef
                {
                    Name = "Coordinate", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy",
                    DefaultIsExpression = true,
                },
                new PortDef { Name = "Time", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Params =
            {
                new ParamDef { Name = "SpeedX", Kind = ParamKind.Scalar, Default = "0.0" },
                new ParamDef { Name = "SpeedY", Kind = ParamKind.Scalar, Default = "0.0" },
            },
            Description = "Scrolls a coordinate over time: Coordinate + Time * (SpeedX, SpeedY).",
            CanvasBadge = p_N => $"{p_N.GetParam("SpeedX")}, {p_N.GetParam("SpeedY")}",
            Emit = (p_S, _) =>
            {
                var s_Speed = Literal2(p_S.ParamScalar("SpeedX"), p_S.ParamScalar("SpeedY"));
                p_S.Out("Out", $"{p_S.In("Coordinate")} + {s_Speed} * {p_S.In("Time")}");
            },
        });

        AddUdkArithmetic();
        AddUdkContextual();

        // MaterialExpressionComponentMask: an Input plus four bool properties R/G/B/A — NOT a pin per
        // channel. Its caption reads "Mask ( G B )", which is why the ticked channels are the badge.
        Add(new NodeDef
        {
            Kind = "UdkComponentMask",
            Title = "Mask",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "R", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "G", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "B", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "A", Kind = ParamKind.Bool, Default = "false" },
            },
            Description = "Keeps the ticked channels, in RGBA order. Over there the result's TYPE shrinks to " +
                          "the number of channels; here every value is carried as a float4 with the kept " +
                          "channels packed from x and the rest zero, so a two-channel mask feeding a " +
                          "two-component input behaves identically.",
            CanvasBadge = p_N => "( " + string.Join(" ", MaskChannels(p_N).Select(p_C => p_C.Label)) + " )",
            Emit = (p_S, p_N) =>
            {
                var s_Channels = MaskChannels(p_N);
                if (s_Channels.Count == 0)
                {
                    // Its own default is nothing ticked, and that is a mask of no channels — said out loud
                    // rather than quietly passing the value through.
                    p_S.Out("Out", "float4(0, 0, 0, 0) /* Mask with no channel ticked */");
                    return;
                }

                var s_Input = p_S.Declare("maskIn", ShaderPortType.SptVec4, p_S.In("Input"));
                var s_Parts = s_Channels.Select(p_C => $"{s_Input}.{p_C.Swizzle}").ToList();
                while (s_Parts.Count < 4)
                    s_Parts.Add("0");

                p_S.Out("Out", $"float4({string.Join(", ", s_Parts)})");
            },
        });

        // ── Output ───────────────────────────────────────────────────────────────────────────────────
        AddUdkMaterial();

        // ── Constants ────────────────────────────────────────────────────────────────────────────────
        // MaterialExpressionConstant / Constant2Vector / Constant3Vector / Constant4Vector: R, R+G,
        // R+G+B and R+G+B+A respectively, each a separate class over there.
        Add(UdkConstant("UdkConstant", "Constant", ShaderPortType.SptScalar, "R"));
        Add(UdkConstant("UdkConstant2Vector", "Constant2Vector", ShaderPortType.SptVec2, "R", "G"));
        Add(UdkConstant("UdkConstant3Vector", "Constant3Vector", ShaderPortType.SptVec3, "R", "G", "B"));
        Add(UdkConstant("UdkConstant4Vector", "Constant4Vector", ShaderPortType.SptVec4, "R", "G", "B", "A"));

        // ── Math ─────────────────────────────────────────────────────────────────────────────────────
        // MaterialExpressionLinearInterpolate: the same lerp, under the name that vocabulary uses.
        Add(new NodeDef
        {
            Kind = "UdkLinearInterpolate",
            Title = "LinearInterpolate",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Alpha", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Description = "Blends A and B by Alpha — the same operation this engine's Lerp performs.",
            Emit = (p_S, _) =>
                p_S.Out("Out", $"lerp({p_S.In("A")}, {p_S.In("B")}, {p_S.In("Alpha")})"),
        });

        // ── Textures ─────────────────────────────────────────────────────────────────────────────────
        // MaterialExpressionTextureSample declares FIVE outputs by mask — RGB, R, G, B, A — which is why
        // that vocabulary needs no mask node behind a texture. Register is ours and has to stay: over
        // there the node points at a texture ASSET, here it points at the slot the material binds.
        Add(new NodeDef
        {
            Kind = "UdkTextureSample",
            Title = "TextureSample",
            Category = "UDK · Textures",
            Style = NodeStyle.Udk,
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef
                {
                    Name = "Coordinates", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy",
                    DefaultIsExpression = true,
                },
            },
            Outputs =
            {
                new PortDef { Name = "RGB", Type = ShaderPortType.SptVec3 },
                new PortDef { Name = "R", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "G", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "B", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "A", Type = ShaderPortType.SptScalar },
            },
            Params =
            {
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "1",
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8" },
                },
                new ParamDef { Name = "Unpack", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "TransformNormal", Kind = ParamKind.Bool, Default = "false" },
            },
            Description = "Samples the texture bound to Register at Coordinates, with the per-channel " +
                          "outputs of that vocabulary. Unpack/TransformNormal are this engine's normal-map " +
                          "handling, kept because a normal map here is decoded by the shader, not the asset.",
            CanvasBadge = p_N => $"t{p_N.GetParam("Register")}",
            Emit = (p_S, p_N) =>
            {
                // The native texture node's own decode, reproduced step for step rather than called: that
                // one reads a "Coord" port and a "Tiling" string this node does not have. Any drift here is
                // caught by the twin test, which diffs this emission against the native one's.
                var s_Texture = HlslNames.TextureVar(p_N.GetParam("Register"));
                var s_Sample = p_S.Declare("texSample", ShaderPortType.SptVec4,
                    $"{s_Texture}.Sample(sampler0, {p_S.In("Coordinates")})");

                if (p_N.GetParam("Unpack") == "true")
                {
                    var s_Xy = p_S.Declare("texXy", ShaderPortType.SptVec2,
                        $"float2({s_Sample}.x * {s_Sample}.w, {s_Sample}.y) * 2.0 - 1.0");
                    var s_Normal = p_S.Declare("texNormal", ShaderPortType.SptVec3,
                        $"float3({s_Xy}, sqrt(max(0.0, 1.0 - dot({s_Xy}, {s_Xy}))))");

                    if (p_N.GetParam("TransformNormal") == "true")
                        s_Normal = p_S.Declare("texWorldN", ShaderPortType.SptVec3,
                            $"normalize(float3(dot({s_Normal}, i.TangentRow0.xyz), " +
                            $"dot({s_Normal}, i.TangentRow1.xyz), dot({s_Normal}, i.TangentRow2.xyz)))");

                    s_Sample = p_S.Declare("texUnpacked", ShaderPortType.SptVec4, $"float4({s_Normal}, 1.0)");
                }

                p_S.Out("RGB", $"{s_Sample}.xyz");
                p_S.Out("R", $"{s_Sample}.x");
                p_S.Out("G", $"{s_Sample}.y");
                p_S.Out("B", $"{s_Sample}.z");
                p_S.Out("A", $"{s_Sample}.w");
            },
        });

        // ⛔ LAST, once every node above exists. It ran from the middle of this method and silently dropped
        // every entry naming a node registered further down — the table is applied by NAME, so a node that is
        // not there yet is indistinguishable from a typo, and the miss only reached a Debug line nobody sees
        // in a release build.
        AddUdkAliases();
    }

    /// <summary>
    /// Every UDK name that is answered by a node this palette ALREADY has — one table, so what the search
    /// box knows can be audited at a glance instead of hunting for scattered declarations.
    ///
    /// An alias is only correct when the node does the SAME WORK; a name that merely sounds similar would
    /// send an author to a node that bakes something else. Where the work differs (ComponentMask returns a
    /// type that varies with the channels ticked, which this palette's fixed port types cannot express) the
    /// alias points at the node that does the job — the per-channel mask — and nothing pretends otherwise.
    /// </summary>
    /// <summary>
    /// Expressions that vocabulary and this one call the SAME thing (measured against its
    /// MaterialExpression classes), so the node is native but the name is already UDK's. They stay in the
    /// UDK palette: hiding "Multiply" from someone authoring in UDK would be absurd.
    /// </summary>
    private static readonly HashSet<string> s_SharedWithUdk = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Abs", "Add", "Ceil", "Clamp", "Distance", "Divide", "Floor", "Fmod", "Frac", "Fresnel",
        "Multiply", "Normalize", "OneMinus", "Power", "Rotator", "ScreenPosition", "ScreenSize",
        "Subtract", "Time",
    };

    /// <summary>
    /// The UDK-named node that REPLACES a native one while authoring in that style — so a keyboard
    /// shortcut bound to the texture node drops the UDK texture, not this engine's. Without this, the
    /// palette says one vocabulary and the shortcuts speak the other.
    /// </summary>
    private static readonly Dictionary<string, string> s_UdkTwins = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Texture"] = "UdkTextureSample",
        ["TexCoord"] = "UdkTextureCoordinate",
        ["Lerp"] = "UdkLinearInterpolate",
        ["Scalar"] = "UdkConstant",
        ["Color"] = "UdkConstant4Vector",
        ["Panner"] = "UdkPanner",
        ["StandardRoot"] = "UdkMaterial",
    };

    /// <summary>The ticked channels of a mask node, in RGBA order.</summary>
    private static List<(string Label, string Swizzle)> MaskChannels(GraphNode p_Node)
    {
        var s_All = new[] { ("R", "x"), ("G", "y"), ("B", "z"), ("A", "w") };
        return s_All.Where(p_C => p_Node.GetParam(p_C.Item1) == "true")
            .Select(p_C => (Label: p_C.Item1, Swizzle: p_C.Item2))
            .ToList();
    }

    /// <summary>
    /// Entries of the alias table that named a node the palette does not have. Empty is the only healthy
    /// value; the surface sweep fails on anything in here.
    /// </summary>
    private static readonly List<string> s_AliasMisses = new();

    public static IReadOnlyList<string> UdkAliasMisses => s_AliasMisses;

    /// <summary>Whether both catalogs call this node the same thing.</summary>
    public static bool IsSharedWithUdk(string p_Kind) => s_SharedWithUdk.Contains(p_Kind);

    /// <summary>The UDK node that stands in for this one, or null when there is none.</summary>
    public static string? UdkTwinOf(string p_Kind) =>
        s_UdkTwins.TryGetValue(p_Kind, out var s_Twin) ? s_Twin : null;

    /// <summary>
    /// What a shortcut bound to this kind actually drops, named in the vocabulary being authored in. ⛔ Every
    /// surface that ADVERTISES a shortcut (the settings list, the startup log) resolves it through here, and
    /// through the same twin lookup the canvas uses to place it — two of them naming the node separately is
    /// how a list ends up promising one node while the key hands over another.
    /// </summary>
    public static string PlacedTitle(string p_Kind, bool p_UdkStyle) =>
        p_UdkStyle
            ? Get(UdkTwinOf(p_Kind) ?? p_Kind).TitleFor(true)
            : Get(p_Kind).Title;

    /// <summary>Whether a node belongs in the UDK palette at all.</summary>
    public static bool IsUdkVocabulary(NodeDef p_Def) =>
        p_Def.Style == NodeStyle.Udk || p_Def.Aliases.Count > 0 || s_SharedWithUdk.Contains(p_Def.Kind);

    /// <summary>
    /// Nodes with no counterpart in that vocabulary which a CONVERSION can still leave sitting in the graph —
    /// listed in the UDK palette under their own category so a converted graph never holds a node the author
    /// cannot place again without switching styles.
    ///
    /// ⛔ It is this list and not "everything else" (68 nodes) on purpose: the point is to reach what a
    /// conversion produces, not to put the other catalog back on screen. Measured, not guessed — these are
    /// what actually survived converting the 26 translated presets on disk.
    /// ⚠ Some of these have no equivalent because that catalog simply HAS none: there is no Min, Max,
    /// Saturate or Negate expression over there (only Abs and Clamp), so they are not a gap to be closed.
    /// </summary>
    private static readonly HashSet<string> s_UdkReachable = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Masks and the raw interpolators a translated shader is full of.
        "MixOut", "Swizzle", "Interpolator", "TexCoord", "Texture", "TangentToWorld",
        // Maths that catalog does not have.
        "Saturate", "Negate", "Min", "Less", "Exp2", "Log2Raw",
        // Alpha-test nodes and the outputs other than the standard one.
        "Clip", "AlphaCoverage",
        "StandardRoot", "MetallicRoot", "SkinRoot", "ForwardRoot", "RawRoot",
        // Sub-graph pins: that catalog calls these FunctionInput/FunctionOutput.
        "InstanceInput", "InstanceOutput",
    };

    /// <summary>Whether the UDK palette lists this node under "Frostbite only".</summary>
    public static bool IsUdkReachable(NodeDef p_Def) =>
        !IsUdkVocabulary(p_Def) && s_UdkReachable.Contains(p_Def.Kind);

    /// <summary>Entries of the reachable list that name a node the palette does not have.</summary>
    public static IEnumerable<string> UdkReachableMisses =>
        s_UdkReachable.Where(p_K => !s_Defs.ContainsKey(p_K));

    /// <summary>The category heading a node appears under while authoring in one vocabulary.</summary>
    public const string c_ForeignCategory = "Frostbite only";

    private static void AddUdkAliases()
    {
        var s_Aliases = new Dictionary<string, string[]>
        {
            // Same operation, different word.
            ["Cos"] = new[] { "Cosine" },
            ["Sin"] = new[] { "Sine" },
            ["Sqrt"] = new[] { "SquareRoot" },
            ["Dot"] = new[] { "DotProduct" },
            ["Cross"] = new[] { "CrossProduct" },
            ["Append"] = new[] { "AppendVector" },
            ["Reflect"] = new[] { "ReflectionVector" },
            ["ParallaxOffset"] = new[] { "BumpOffset" },
            ["Script"] = new[] { "Custom" },

            // Inputs the two vocabularies name differently.
            ["WorldPos"] = new[] { "WorldPosition" },
            ["GeometricNormal"] = new[] { "WorldNormal" },
            ["CameraPos"] = new[] { "CameraWorldPosition" },
            ["ViewDirection"] = new[] { "CameraVector" },

            // A "parameter" there is an external constant here — same thing: a value the material supplies.
            // ⚠ VectorParameter FIRST because the first alias is also the CAPTION: this node publishes a
            // four-component output, so drawing it as "ScalarParameter" would name it after the one-float
            // node of that catalog. The scalar name stays for the search box.
            ["ExternalConstant"] = new[] { "VectorParameter", "ScalarParameter", "Parameter" },

            // Every static switch of that vocabulary is this node with a constant condition.
            ["Select"] = new[] { "If", "StaticSwitch", "StaticBool", "StaticBoolParameter", "StaticSwitchParameter" },

            // Texture sampling: over there the asset (and whether it is a parameter) makes a different class
            // per case; here it is one node whose Register says which slot, so they all land on it.
            //
            // ⛔ OUR OWN `Texture` CARRIES NONE OF THEM, and that is the fix for a caption that lied: the
            // first alias is what the node is DRAWN as, and it read "TextureObject" — which in that catalog
            // is a different node that does NOT sample (it hands a texture reference to something else).
            // Sampling a 2D texture there is TextureSample, which exists here as its own node, so the
            // sampling names belong on THAT one and this catalog's `Texture` is simply not part of the UDK
            // palette — exactly what was decided for MixOut when ComponentMask became a node of its own.
            // TextureObject/TextureObjectParameter are dropped outright: nothing here matches them, and an
            // alias that names a node we do not have teaches the wrong thing (as Clip→OpacityMask did).
            ["UdkTextureSample"] = new[] { "TextureSampleParameter", "TextureSampleParameter2D" },
            ["NormalMap"] = new[] { "TextureSampleParameterNormal" },
            ["TextureCube"] = new[] { "TextureSampleParameterCube" },

            // ⛔ ComponentMask is NOT an alias of MixOut any more: that vocabulary's mask is one output
            // with tick-boxes, ours is a pin per channel. They are different nodes, so the UDK one exists
            // in its own right and MixOut simply is not part of that palette.
        };

        foreach (var (s_Kind, s_Names) in s_Aliases)
        {
            if (!s_Defs.TryGetValue(s_Kind, out var s_Def))
            {
                // A table naming a node that does not exist teaches the search box nothing — and says so
                // where a test can read it, because a Debug line is invisible in the build that ships.
                s_AliasMisses.Add(s_Kind);
                continue;
            }

            foreach (var s_Name in s_Names)
                if (!s_Def.Aliases.Contains(s_Name))
                    s_Def.Aliases.Add(s_Name);
        }
    }

    /// <summary>
    /// The material OUTPUT node of that vocabulary, wired to this engine's measured GBuffer packing.
    ///
    /// ⛔ It does not reimplement the packing — it renames its pins and runs the native root's own emission,
    /// so the two cannot drift apart. Pins that vocabulary has and this pass cannot serve are DECLARED with
    /// the reason they are dead rather than dropped: an author who knows that editor looks for them, and a
    /// missing pin reads as "not supported yet" while a silent one reads as "it worked".
    /// </summary>
    private static void AddUdkMaterial()
    {
        // Defaults are that editor's own (Material.uc): DiffuseColor/SpecularColor 128 (=0.5), SpecularPower
        // 15, Opacity/OpacityMask 1.
        const string c_NoDeferredPin = "the deferred GBuffer pass has no channel for this";

        Add(new NodeDef
        {
            Kind = "UdkMaterial",
            Title = "Material",
            Category = "UDK · Output",
            Style = NodeStyle.Udk,
            IsRoot = true,
            Description = "The material output in the UDK-era vocabulary, packed into this engine's GBuffer. " +
                          "SpecularPower is an EXPONENT there and a 0..1 smoothness here, so it is converted " +
                          "(s = (log2(power) - 1) / 10) rather than passed through — connecting an exponent " +
                          "straight to smoothness would come out mirror-shiny.",
            Inputs =
            {
                new PortDef { Name = "DiffuseColor", Type = ShaderPortType.SptVec3, Default = "float3(0.5, 0.5, 0.5)" },

                // Present and refused: an exponent on the diffuse term is a pre-PBR trick this pass does not
                // carry, and there is nowhere in the packing to put it.
                new PortDef
                {
                    Name = "DiffusePower", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = _ => "this pass has no diffuse exponent; bake it into the texture",
                },
                new PortDef { Name = "EmissiveColor", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },

                // A COLOUR there, a single channel here (the GBuffer stores one specular value), so the red
                // channel is taken and the node says so rather than quietly averaging.
                new PortDef { Name = "SpecularColor", Type = ShaderPortType.SptVec3, Default = "float3(0.5, 0.5, 0.5)" },
                new PortDef { Name = "SpecularPower", Type = ShaderPortType.SptScalar, Default = "15.0" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                // ⛔ Availability follows THIS node's own property. It used to ask the native
                // SurfaceShaderType — a param this node does not have — so the pin read "Opaque" for ever
                // and stayed grey no matter which blend mode was picked.
                new PortDef
                {
                    Name = "OpacityMask", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = p_N => p_N.GetParam("BlendMode") is "BLEND_Masked" or "BLEND_SoftMasked"
                        ? null
                        : $"only cuts out while BlendMode is BLEND_Masked or BLEND_SoftMasked " +
                          $"(currently {p_N.GetParam("BlendMode")})",
                },
                new PortDef
                {
                    Name = "Opacity", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = p_N => p_N.GetParam("BlendMode") is "BLEND_Translucent" or "BLEND_Additive"
                        or "BLEND_Modulate" or "BLEND_ModulateAndAdd" or "BLEND_AlphaComposite"
                        ? "this node writes the OPAQUE GBuffer; blended transparency is a separate pass " +
                          "(the forward root), which this output cannot reach"
                        : "blended transparency only; for a cutout use OpacityMask with BLEND_Masked",
                },
                new PortDef
                {
                    Name = "Distortion", Type = ShaderPortType.SptVec2, Default = "float2(0, 0)",
                    UnavailableReason = _ => c_NoDeferredPin,
                },
                new PortDef
                {
                    Name = "CustomLighting", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)",
                    UnavailableReason = _ => "lighting is not chosen per-pin here: it is the ROOT you pick",
                },
                new PortDef
                {
                    Name = "TwoSidedLightingMask", Type = ShaderPortType.SptScalar, Default = "0.0",
                    UnavailableReason = _ => c_NoDeferredPin,
                },
                new PortDef
                {
                    Name = "TwoSidedLightingColor", Type = ShaderPortType.SptVec3, Default = "float3(1, 1, 1)",
                    UnavailableReason = _ => c_NoDeferredPin,
                },

                // No counterpart over there, and no way to author the vanilla look without them: this
                // engine's rim term is a pin, not a node chain.
                new PortDef { Name = "FresnelBias", Type = ShaderPortType.SptScalar, Default = "0.6" },
                new PortDef { Name = "FresnelExponent", Type = ShaderPortType.SptScalar, Default = "4.0" },
            },
            Params =
            {
                // ── The MATERIAL panel of that editor, in its own words ───────────────────────────────
                // BlendMode and LightingModel are PROPERTIES of the material there; here the first is the
                // surface type and the second is WHICH ROOT you are — so the node translates both instead
                // of making the author know that.
                new ParamDef
                {
                    Name = "BlendMode", Kind = ParamKind.Choice, Default = "BLEND_Opaque",
                    Choices =
                    {
                        "BLEND_Opaque", "BLEND_Masked", "BLEND_SoftMasked", "BLEND_Translucent",
                        "BLEND_Additive", "BLEND_Modulate",
                    },
                },
                new ParamDef
                {
                    Name = "LightingModel", Kind = ParamKind.Choice, Default = "MLM_Phong",
                    Choices = { "MLM_Phong", "MLM_Unlit", "MLM_NonDirectional", "MLM_SHPRT", "MLM_Custom", "MLM_Anisotropic" },
                },

                // Its own default is 0.3333; this pass clips in the SHADER, so the value is honoured
                // instead of being pinned to the engine's 0.5.
                new ParamDef { Name = "OpacityMaskClipValue", Kind = ParamKind.Scalar, Default = "0.333333" },

                // Called TwoSided in that editor; same flag.
                new ParamDef { Name = "TwoSided", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "GeometryPassEnable", Kind = ParamKind.Bool, Default = "true" },
                new ParamDef
                {
                    Name = "NormalSpace", Kind = ParamKind.Choice, Default = "Tangent",
                    Choices = { "Tangent", "World" },
                },
                new ParamDef { Name = "Rt2Xyz", Kind = ParamKind.Text, Default = "0,0,0" },
                new ParamDef { Name = "WorldPosInterp", Kind = ParamKind.Text, Default = "" },
                new ParamDef { Name = "PackedSpecular", Kind = ParamKind.Text, Default = "" },
                new ParamDef
                {
                    Name = "SpecularMode", Kind = ParamKind.Choice, Default = "Fresnel",
                    Choices = { "Fresnel", "Raw" },
                },
            },
            Emit = (p_S, p_N) =>
            {
                var s_Renames = new Dictionary<string, string>
                {
                    ["Diffuse"] = "DiffuseColor",
                    ["Emissive"] = "EmissiveColor",
                    ["Normal"] = "Normal",
                    ["FresnelBias"] = "FresnelBias",
                    ["FresnelExponent"] = "FresnelExponent",
                };

                // The two conversions, both stated in the node's own description so nothing is silent:
                // an exponent becomes this engine's 0..1 smoothness, and a specular COLOUR becomes the one
                // channel the GBuffer keeps.
                //
                // A CONSTANT exponent is converted here and emitted as the plain number it works out to.
                // Not an optimisation — fxc would fold it anyway: it is what the generated HLSL READS like,
                // and `0.4` says what the shader does where `(log2(max(1.0, 32.0)) - 1.0) / 10.0` makes the
                // reader do the arithmetic.
                var s_Power = ConstantInput(p_S, p_N, "SpecularPower");
                var s_Clip = p_S.ParamScalar("OpacityMaskClipValue");
                var s_Overrides = new Dictionary<string, string>
                {
                    // The packing clips at 0.5; shifting the value by (0.5 - clip) makes the cut land on the
                    // material's own OpacityMaskClipValue without touching the native root.
                    ["Opacity"] = Math.Abs(s_Clip - 0.5f) < 1e-6f
                        ? p_S.In("OpacityMask")
                        : $"(({p_S.In("OpacityMask")}) + {Scalar(0.5f - s_Clip)})",
                    ["Smoothness"] = s_Power is { } s_Value
                        ? Scalar((float) ((Math.Log2(Math.Max(1.0, s_Value)) - 1.0) / 10.0))
                        : $"(log2(max(1.0, {p_S.In("SpecularPower")})) - 1.0) / 10.0",
                    ["Specular"] = FirstComponent(p_S.In("SpecularColor")),
                };

                // ⛔ The packing reads its SETTINGS off the node itself (GetParam), not off the scope, so the
                // material panel's properties are translated into a stand-in node wearing the native names.
                // Anything with no counterpart says so IN THE EMITTED HLSL — a blend mode this pass cannot
                // do is the kind of thing that must not be discovered in-game.
                var s_Proxy = new GraphNode { Kind = "StandardRoot" };
                var s_Blend = p_N.GetParam("BlendMode");
                s_Proxy.Params["SurfaceShaderType"] = s_Blend switch
                {
                    "BLEND_Masked" => nameof(SurfaceShaderType.SurfaceShaderType_OpaqueAlphaTest),
                    "BLEND_SoftMasked" => nameof(SurfaceShaderType.SurfaceShaderType_OpaqueAlphaTestSimple),
                    _ => nameof(SurfaceShaderType.SurfaceShaderType_Opaque),
                };

                foreach (var s_Carried in new[] { "NormalSpace", "Rt2Xyz", "WorldPosInterp", "PackedSpecular", "SpecularMode" })
                    s_Proxy.Params[s_Carried] = p_N.GetParam(s_Carried);

                s_Proxy.Params["DoubleSided"] = p_N.GetParam("TwoSided");

                if (s_Blend is "BLEND_Translucent" or "BLEND_Additive" or "BLEND_Modulate")
                    p_S.Line($"// {s_Blend}: blended transparency is a different pass here — this node " +
                             "writes the opaque GBuffer, so the mode was ignored.");

                var s_Model = p_N.GetParam("LightingModel");
                if (s_Model != "MLM_Phong")
                    p_S.Line($"// {s_Model}: this pass has no such lighting model; shaded as MLM_Phong " +
                             "(the model here is WHICH ROOT you use, not a material property).");

                EmitGBufferPacking(new UdkScope(p_S, s_Renames, s_Overrides), s_Proxy,
                    ShaderLightingModel.ShaderLightingModel_Standard);
            },
        });
    }

    /// <summary>
    /// The expressions that vocabulary has and this palette did not: pure arithmetic, so they need no data
    /// the shader does not already have.
    ///
    /// ⚠ PROPERTY NAMES AND DEFAULTS ARE FROM THE SOURCE; THE FORMULAS ARE NOT. Each expression's Compile()
    /// lives in C++, which this tree does not carry, so the maths below is the conventional form of each
    /// operation — the same footing the Blend modes and Curve shapes are already on, and marked the same
    /// way. If one is ever measured against the real thing, this is the place to correct.
    /// </summary>
    private static void AddUdkArithmetic()
    {
        // Desaturation: LuminanceFactors default (0.3, 0.59, 0.11) — the classic luma weights.
        Add(new NodeDef
        {
            Kind = "UdkDesaturation",
            Title = "Desaturation",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptVec3, Default = "float3(1, 1, 1)" },
                new PortDef { Name = "Percent", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Params = { new ParamDef { Name = "LuminanceFactors", Kind = ParamKind.Color, Default = "0.3,0.59,0.11,0" } },
            Description = "Blends the colour towards its greyscale luminance. Percent 1 is fully desaturated. " +
                          "⚠ formula conventional (lerp between colour and dot(colour, LuminanceFactors)).",
            Emit = (p_S, p_N) =>
            {
                var s_Factors = PortTypeUtils.FormatLiteral(p_N.GetParam("LuminanceFactors"), ShaderPortType.SptVec3)
                                ?? "float3(0.3, 0.59, 0.11)";
                var s_Input = p_S.Declare("desatIn", ShaderPortType.SptVec3, p_S.In("Input"));
                p_S.Out("Out", $"lerp({s_Input}, dot({s_Input}, {s_Factors}).xxx, saturate({p_S.In("Percent")}))");
            },
        });

        // ConstantBiasScale: (x + Bias) * Scale, defaults 1 and 0.5 — the -1..1 to 0..1 remap.
        Add(new NodeDef
        {
            Kind = "UdkConstantBiasScale",
            Title = "ConstantBiasScale",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "Bias", Kind = ParamKind.Scalar, Default = "1.0" },
                new ParamDef { Name = "Scale", Kind = ParamKind.Scalar, Default = "0.5" },
            },
            Description = "(Input + Bias) * Scale. The defaults turn a -1..1 range into 0..1.",
            CanvasBadge = p_N => $"+{p_N.GetParam("Bias")} x{p_N.GetParam("Scale")}",
            Emit = (p_S, _) => p_S.Out("Out",
                $"({p_S.In("Input")} + {Scalar(p_S.ParamScalar("Bias"))}) * {Scalar(p_S.ParamScalar("Scale"))}"),
        });

        // ConstantClamp: like the clamp we have, but the bounds are PROPERTIES rather than pins.
        Add(new NodeDef
        {
            Kind = "UdkConstantClamp",
            Title = "ConstantClamp",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "Min", Kind = ParamKind.Scalar, Default = "0.0" },
                new ParamDef { Name = "Max", Kind = ParamKind.Scalar, Default = "1.0" },
            },
            Description = "clamp(Input, Min, Max), with the bounds as properties.",
            CanvasBadge = p_N => $"{p_N.GetParam("Min")}..{p_N.GetParam("Max")}",
            Emit = (p_S, _) => p_S.Out("Out",
                $"clamp({p_S.In("Input")}, {Scalar(p_S.ParamScalar("Min"))}, {Scalar(p_S.ParamScalar("Max"))})"),
        });

        // DeriveNormalZ: rebuilds Z from a tangent-space XY — the same step the texture node's unpack does.
        Add(new NodeDef
        {
            Kind = "UdkDeriveNormalZ",
            Title = "DeriveNormalZ",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs = { new PortDef { Name = "InXY", Type = ShaderPortType.SptVec2, Default = "float2(0, 0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Description = "Rebuilds a unit normal's Z from its XY: float3(xy, sqrt(1 - dot(xy, xy))). Same " +
                          "reconstruction the texture node performs when Unpack is on.",
            Emit = (p_S, _) =>
            {
                var s_Xy = p_S.Declare("normXy", ShaderPortType.SptVec2, p_S.In("InXY"));
                p_S.Out("Out", $"float3({s_Xy}, sqrt(max(0.0, 1.0 - dot({s_Xy}, {s_Xy}))))");
            },
        });

        // SphereMask: A/B/Radius/Hardness pins, with AttenuationRadius 256 and HardnessPercent 100 as the
        // fallbacks its own defaults declare.
        Add(new NodeDef
        {
            Kind = "UdkSphereMask",
            Title = "SphereMask",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "Radius", Type = ShaderPortType.SptScalar, Default = "256.0" },
                new PortDef { Name = "Hardness", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Description = "1 inside the sphere around B, falling to 0 at Radius; Hardness 1 is a hard edge. " +
                          "⚠ formula conventional (smoothstep over the distance).",
            Emit = (p_S, _) =>
            {
                var s_Distance = p_S.Declare("maskD", ShaderPortType.SptScalar,
                    $"distance({p_S.In("A")}, {p_S.In("B")})");
                var s_Radius = p_S.Declare("maskR", ShaderPortType.SptScalar, p_S.In("Radius"));
                var s_Inner = p_S.Declare("maskI", ShaderPortType.SptScalar,
                    $"{s_Radius} * (1.0 - saturate({p_S.In("Hardness")}))");

                p_S.Out("Out", $"1.0 - smoothstep({s_Inner}, max({s_Inner} + 1e-6, {s_Radius}), {s_Distance})");
            },
        });

        // RotateAboutAxis: axis in xyz, angle in w — Rodrigues' rotation of a point about that axis.
        Add(new NodeDef
        {
            Kind = "UdkRotateAboutAxis",
            Title = "RotateAboutAxis",
            Category = "UDK · Math",
            Style = NodeStyle.Udk,
            Inputs =
            {
                new PortDef
                {
                    Name = "NormalizedRotationAxisAndAngle", Type = ShaderPortType.SptVec4,
                    Default = "float4(0, 0, 1, 0)",
                },
                new PortDef { Name = "PositionOnAxis", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "Position", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Description = "Rotates Position about the axis in xyz (angle in w, turns) through PositionOnAxis, " +
                          "and returns the OFFSET. ⚠ formula conventional (Rodrigues).",
            Emit = (p_S, _) =>
            {
                var s_Axis = p_S.Declare("rotA", ShaderPortType.SptVec4, p_S.In("NormalizedRotationAxisAndAngle"));
                var s_Closest = p_S.Declare("rotC", ShaderPortType.SptVec3,
                    $"{p_S.In("PositionOnAxis")} + {s_Axis}.xyz * dot({s_Axis}.xyz, " +
                    $"{p_S.In("Position")} - {p_S.In("PositionOnAxis")})");
                var s_Arm = p_S.Declare("rotU", ShaderPortType.SptVec3, $"{p_S.In("Position")} - {s_Closest}");
                var s_Angle = p_S.Declare("rotT", ShaderPortType.SptScalar, $"{s_Axis}.w * 6.28318530718");

                p_S.Out("Out", $"({s_Closest} + {s_Arm} * cos({s_Angle}) + " +
                               $"cross({s_Axis}.xyz, {s_Arm}) * sin({s_Angle})) - {p_S.In("Position")}");
            },
        });

        // AntialiasedTextureMask: a cutout whose edge is softened by the alpha's own screen-space slope, so
        // it does not crawl. Threshold 0.5 and channel Alpha are its declared defaults.
        Add(new NodeDef
        {
            Kind = "UdkAntialiasedTextureMask",
            Title = "AntialiasedTextureMask",
            Category = "UDK · Textures",
            Style = NodeStyle.Udk,
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef
                {
                    Name = "Coordinates", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy",
                    DefaultIsExpression = true,
                },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params =
            {
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "1",
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8" },
                },
                new ParamDef { Name = "Threshold", Kind = ParamKind.Scalar, Default = "0.5" },
                new ParamDef
                {
                    Name = "Channel", Kind = ParamKind.Choice, Default = "Alpha",
                    Choices = { "Red", "Green", "Blue", "Alpha" },
                },
            },
            Description = "A mask whose edge is antialiased with the channel's own derivatives, instead of a " +
                          "hard step that crawls when the camera moves. ⚠ formula conventional " +
                          "(smoothstep across one pixel of slope).",
            CanvasBadge = p_N => $"t{p_N.GetParam("Register")} {p_N.GetParam("Channel")}",
            Emit = (p_S, p_N) =>
            {
                var s_Swizzle = p_N.GetParam("Channel") switch
                {
                    "Red" => "x",
                    "Green" => "y",
                    "Blue" => "z",
                    _ => "w",
                };

                var s_Texture = HlslNames.TextureVar(p_N.GetParam("Register"));
                var s_Value = p_S.Declare("maskV", ShaderPortType.SptScalar,
                    $"{s_Texture}.Sample(sampler0, {p_S.In("Coordinates")}).{s_Swizzle}");

                // One pixel of the value's own gradient is the width to blend over: wider where the mask is
                // stretched on screen, tighter where it is dense, which is what stops the edge from crawling.
                var s_Width = p_S.Declare("maskW", ShaderPortType.SptScalar,
                    $"max(1e-5, abs(ddx({s_Value})) + abs(ddy({s_Value})))");
                var s_Threshold = Scalar(p_S.ParamScalar("Threshold"));

                p_S.Out("Out", $"smoothstep({s_Threshold} - {s_Width}, {s_Threshold} + {s_Width}, {s_Value})");
            },
        });
    }

    /// <summary>
    /// The three expressions whose answer DEPENDS ON THE TARGET, not on arithmetic: they exist here, but
    /// only where the shader being edited actually carries the data.
    ///
    /// ⛔ They refuse instead of guessing. A vertex colour read from a family whose layout puts something
    /// else in that interpolator does not fail — it silently shades with a tangent row, which is the exact
    /// class of bug that cost this project a measured cycle.
    /// </summary>
    private static void AddUdkContextual()
    {
        // MaterialExpressionVertexColor: no properties. Here the vertex colour is an INTERPOLATOR whose
        // index differs per family, so the contract is asked where it is — and when the family has none,
        // the node says so rather than reading whatever sits at index 0.
        Add(new NodeDef
        {
            Kind = "UdkVertexColor",
            Title = "VertexColor",
            Category = "UDK · Inputs",
            Style = NodeStyle.Udk,
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Description = "The mesh's vertex colour. Only families whose measured layout carries one can " +
                          "answer this — on the rest the node emits white and says why, because reading the " +
                          "wrong interpolator shades with a tangent row instead of failing.",
            Emit = (p_S, _) =>
            {
                var s_Field = VertexColourField(p_S.Contract);
                p_S.Out("Out", s_Field != null
                    ? $"i.{s_Field}"
                    : "float4(1, 1, 1, 1) /* this target carries no vertex colour interpolator */");
            },
        });

        // MaterialExpressionTransform / TransformPosition: an enum of source and destination spaces. Only
        // tangent -> world exists here as measured maths (the interpolated tangent rows); the others need
        // matrices this pixel shader is never handed.
        foreach (var (s_Kind, s_Title, s_Vector) in new[]
                 {
                     ("UdkTransform", "Transform", true),
                     ("UdkTransformPosition", "TransformPosition", false),
                 })
        {
            var s_IsVector = s_Vector;
            Add(new NodeDef
            {
                Kind = s_Kind,
                Title = s_Title,
                Category = "UDK · Math",
                Style = NodeStyle.Udk,
                Inputs =
                {
                    new PortDef
                    {
                        Name = "Input", Type = ShaderPortType.SptVec3,
                        Default = s_IsVector ? "float3(0, 0, 1)" : "float3(0, 0, 0)",
                    },
                },
                Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
                Params =
                {
                    new ParamDef
                    {
                        Name = "TransformType", Kind = ParamKind.Choice, Default = "TangentToWorld",
                        Choices = { "TangentToWorld", "LocalToWorld", "WorldToLocal", "WorldToView" },
                    },
                },
                Description = "Moves a " + (s_IsVector ? "direction" : "position") + " between spaces. ONLY " +
                              "TangentToWorld is available: it is the interpolated tangent frame this shader " +
                              "already receives. The others need object or view matrices that the pixel " +
                              "shader is never given here, so they are refused rather than approximated.",
                CanvasBadge = p_N => p_N.GetParam("TransformType"),
                Emit = (p_S, p_N) =>
                {
                    if (p_N.GetParam("TransformType") != "TangentToWorld")
                    {
                        // Passing the value through would look like it worked. Emitting the input unchanged
                        // WITH the reason in the shader is the honest failure: visible in the generated HLSL.
                        p_S.Out("Out", $"{p_S.In("Input")} /* {p_N.GetParam("TransformType")} needs a matrix " +
                                       "this pass does not receive; value passed through untransformed */");
                        return;
                    }

                    var s_Value = p_S.Declare("xfIn", ShaderPortType.SptVec3, p_S.In("Input"));
                    p_S.Out("Out", $"float3(dot({s_Value}, i.TangentRow0.xyz), " +
                                   $"dot({s_Value}, i.TangentRow1.xyz), dot({s_Value}, i.TangentRow2.xyz))");
                },
            });
        }

        // MaterialExpressionTexelSize: 1/dimensions of a bound texture, straight from the resource.
        Add(new NodeDef
        {
            Kind = "UdkTexelSize",
            Title = "TexelSize",
            Category = "UDK · Textures",
            Style = NodeStyle.Udk,
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Params =
            {
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "1",
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8" },
                },
            },
            Description = "The size of one texel of the texture in Register, read from the resource itself.",
            CanvasBadge = p_N => $"t{p_N.GetParam("Register")}",
            Emit = (p_S, p_N) =>
            {
                // GetDimensions writes through out parameters, so it needs statements rather than an
                // expression — the scope's Line/Declare exist for exactly this.
                var s_Size = p_S.Declare("texelDim", ShaderPortType.SptVec2, "float2(1.0, 1.0)");
                p_S.Line($"{HlslNames.TextureVar(p_N.GetParam("Register"))}.GetDimensions({s_Size}.x, {s_Size}.y);");
                p_S.Out("Out", $"1.0 / max(float2(1.0, 1.0), {s_Size})");
            },
        });
    }

    /// <summary>
    /// The interpolator field carrying the vertex colour on the target being edited, or null when its
    /// measured layout has none. Asked of the CONTRACT, never assumed: only the character families carry
    /// one, and on the others that interpolator is a tangent row or a world position.
    /// </summary>
    private static string? VertexColourField(ShaderContract p_Contract)
    {
        for (var s_Index = 0; s_Index < 8; s_Index++)
            if (p_Contract.FieldNameFor(s_Index) == "VertexColor")
                return "VertexColor";

        return null;
    }

    /// <summary>One of the ConstantNVector family: N named channel properties, emitted as one literal.</summary>
    private static NodeDef UdkConstant(string p_Kind, string p_Title, ShaderPortType p_Type,
        params string[] p_Channels)
    {
        var s_Def = new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "UDK · Constants",
            Style = NodeStyle.Udk,
            Outputs = { new PortDef { Name = "Out", Type = p_Type } },
            Description = $"A literal {p_Channels.Length}-channel constant.",
            CanvasBadge = p_N => string.Join(", ", System.Array.ConvertAll(p_Channels, p_C => p_N.GetParam(p_C))),
            Emit = (p_S, _) =>
            {
                var s_Values = System.Array.ConvertAll(p_Channels, p_C => Scalar(p_S.ParamScalar(p_C)));
                p_S.Out("Out", p_Channels.Length == 1
                    ? s_Values[0]
                    : $"float{p_Channels.Length}({string.Join(", ", s_Values)})");
            },
        };

        foreach (var s_Channel in p_Channels)
            s_Def.Params.Add(new ParamDef { Name = s_Channel, Kind = ParamKind.Scalar, Default = "0.0" });

        return s_Def;
    }

    /// <summary>
    /// The tiling multiply, from two separate scalars instead of one "u,v" string. Skipped entirely at
    /// 1,1 so the emitted expression is IDENTICAL to the untiled twin's — the whole point of the style
    /// switch is that it changes nothing downstream.
    /// </summary>
    /// <summary>
    /// The red channel of a specular colour. A literal `floatN(a, b, c)` yields just `a`, so the generated
    /// HLSL reads `0.5` instead of `(float3(0.5, 0.5, 0.5)).x` — same shader, and one of them can be read.
    /// Anything computed keeps the explicit swizzle.
    /// </summary>
    private static string FirstComponent(string p_Expression)
    {
        var s_Match = System.Text.RegularExpressions.Regex.Match(p_Expression.Trim(),
            @"^float[234]\s*\(\s*([^,()]+?)\s*,");

        return s_Match.Success ? s_Match.Groups[1].Value : $"({p_Expression}).x";
    }

    /// <summary>
    /// The literal an input carries when nothing is wired into it — the typed override, or the port's own
    /// default. Null once a wire is attached, because then the value only exists at runtime.
    /// </summary>
    private static float? ConstantInput(IEmitScope p_Scope, GraphNode p_Node, string p_Port)
    {
        if (p_Scope.IsConnected(p_Port))
            return null;

        var s_Text = p_Node.GetInputOverride(p_Port) ?? p_Node.Def.FindInput(p_Port)?.Default;
        return float.TryParse(s_Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s_Value)
            ? s_Value
            : null;
    }

    private static string TiledBy(float p_U, float p_V, string p_Coord) =>
        p_U == 1f && p_V == 1f ? p_Coord : $"({p_Coord}) * {Literal2(p_U, p_V)}";

    private static string Literal2(float p_X, float p_Y) => $"float2({Scalar(p_X)}, {Scalar(p_Y)})";

    private static string Scalar(float p_Value) =>
        p_Value.ToString("0.0###########", System.Globalization.CultureInfo.InvariantCulture);
}
