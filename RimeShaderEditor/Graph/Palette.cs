using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using RimeShaderEditor.Emit;

namespace RimeShaderEditor.Graph;

/// <summary>
/// The node catalog. Node names, port types and the enum-valued parameters come from DICE's own vocabulary,
/// recovered from the shipped BF3 engine (fb::ShaderPortType, fb::BlendShaderMode, fb::CurveShaderType) —
/// the authoring-time graph instances were stripped at bake time but these enums survive in the binary.
/// The per-mode blend FORMULAS are conventional, reconstructed from observed results, not from any reference.
/// </summary>
public static partial class Palette
{
    private static readonly Dictionary<string, NodeDef> s_Defs = new();
    public static IReadOnlyCollection<NodeDef> All => s_Defs.Values;

    /// <summary>
    /// The nodes offered while authoring in one vocabulary: the shared ones plus that style's own twins.
    /// The other style's twins are hidden rather than removed — a graph that holds them still opens, still
    /// draws and still bakes, because the style is a palette filter and nothing else.
    ///
    /// In the UDK style it also carries the handful of nodes with no counterpart there that a CONVERSION can
    /// leave behind (see <see cref="IsUdkReachable"/>); those list under their own heading so a converted
    /// graph never contains something the author cannot place again.
    /// </summary>
    public static IEnumerable<NodeDef> VisibleFor(bool p_UdkStyle) => p_UdkStyle
        ? s_Defs.Values.Where(p_D => IsUdkVocabulary(p_D) || IsUdkReachable(p_D))
        : s_Defs.Values.Where(p_D => p_D.Style != NodeStyle.Udk);

    public static NodeDef Get(string p_Kind) =>
        s_Defs.TryGetValue(p_Kind, out var s_Def) ? s_Def : s_Defs["Scalar"];

    public static bool Has(string p_Kind) => s_Defs.ContainsKey(p_Kind);

    /// <summary>
    /// The node kinds that sample a texture register. The register IS the sharing rule: nodes on the same
    /// register sample the same texture (that is the engine's "parameter"); a node meant to be independent
    /// needs its own register — which is why a freshly placed one gets the first free number.
    /// </summary>
    public static readonly string[] TextureKinds =
        {
            "Texture", "NormalMap", "TextureCube", "Texture3D", "TextureArray",
            "UdkTextureSample", "UdkAntialiasedTextureMask", "StickerCoordinates", "EmblemLayers",
        };

    private static void Add(NodeDef p_Def) => s_Defs[p_Def.Kind] = p_Def;

    private static string Lit(float p_Value) => p_Value.ToString("0.0######", CultureInfo.InvariantCulture);

    static Palette()
    {
        AddInputs();
        AddTextures();
        AddConstants();
        AddMath();
        AddMoreMath();
        AddVector();
        AddUv();
        AddRoot();
        AddRawRoot();
        AddInstanceBoundary();
        AddUdkNodes();
    }

    /// <summary>Kinds of instance nodes are "Instance:&lt;library name&gt;" — the name survives in the saved
    /// graph, so a file opened on a machine whose library lacks it fails LOUDLY at emit instead of silently
    /// emitting something else.</summary>
    public const string InstanceKindPrefix = "Instance:";

    /// <summary>
    /// A library value: "r, g, b, a" (or fewer components) becomes a float4 literal shaped by the pin's
    /// TYPE — a scalar BROADCASTS (2 → (2,2,2,2), so a generic component-wise consumer scales instead of
    /// zeroing y/z; measured: the padded form turned blue × 2 into black), a vec4 given three components
    /// completes the colour with w=1, everything else pads zeros. Anything that does not parse as numbers
    /// passes through VERBATIM as an HLSL expression — which is what lets a host's typed pin override ride
    /// the same channel as the fragment's own numeric default.
    /// </summary>
    internal static string InstanceLiteral(string p_Value, string p_Type)
    {
        var s_Parts = (p_Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var s_Floats = new List<float>();
        foreach (var s_Part in s_Parts)
            if (float.TryParse(s_Part, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Float))
                s_Floats.Add(s_Float);
            else
                return p_Value!.Trim().Length > 0 ? p_Value.Trim() : "float4(0, 0, 0, 0)";

        if (s_Floats.Count == 0)
            s_Floats.Add(0f);

        if (p_Type == "scalar" || s_Floats.Count == 1)
            return $"float4({Lit(s_Floats[0])}, {Lit(s_Floats[0])}, {Lit(s_Floats[0])}, {Lit(s_Floats[0])})";

        while (s_Floats.Count < 4)
            s_Floats.Add(s_Floats.Count == 3 && p_Type == "vec4" ? 1f : 0f);

        return $"float4({Lit(s_Floats[0])}, {Lit(s_Floats[1])}, {Lit(s_Floats[2])}, {Lit(s_Floats[3])})";
    }

    /// <summary>
    /// The two BOUNDARY nodes an instanceable fragment is authored with: what they mark becomes the pins of
    /// the fragment's node in a host graph. A fragment stays previewable on its own — an Instance Input
    /// emits its Value when nothing replaces it, and a preview root (plus anything only feeding it) is
    /// simply not part of what expands into hosts.
    /// </summary>
    private static void AddInstanceBoundary()
    {
        Add(new NodeDef
        {
            Kind = "InstanceInput",
            Title = "Instance Input",
            Category = "Instance",
            Description = "Marks an exposed INPUT pin of an instanceable graph: hosts placing this graph as " +
                          "a node get a pin with this Name, and whatever they wire in replaces this node. " +
                          "Standalone (editing the fragment) it emits its Value, so the fragment previews by " +
                          "itself.",
            Params =
            {
                new ParamDef { Name = "Name", Kind = ParamKind.Text, Default = "Input" },
                new ParamDef
                {
                    Name = "Type", Kind = ParamKind.Choice, Default = "vec4",
                    Choices = { "vec4", "vec3", "vec2", "scalar" },
                },
                new ParamDef { Name = "Value", Kind = ParamKind.Text, Default = "0, 0, 0, 0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            CanvasBadge = p_N => p_N.GetParam("Name"),
            Emit = (p_S, p_N) => p_S.Out("Out", InstanceLiteral(p_N.GetParam("Value"), p_N.GetParam("Type"))),
        });

        Add(new NodeDef
        {
            Kind = "InstanceOutput",
            Title = "Instance Output",
            Category = "Instance",
            Description = "Marks an exposed OUTPUT pin of an instanceable graph: what feeds this node is " +
                          "what the host receives from the pin of this Name. Anything not feeding an " +
                          "Instance Output — including a preview root — is dropped when the fragment " +
                          "expands inside a host.",
            Params = { new ParamDef { Name = "Name", Kind = ParamKind.Text, Default = "Out" } },
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" } },
            CanvasBadge = p_N => p_N.GetParam("Name"),
            Emit = (_, _) => { },
        });
    }

    /// <summary>
    /// (Re)registers one palette entry per library fragment. The def's pins COME FROM the fragment's
    /// boundary nodes, so the palette always mirrors what expansion will actually bind. The safety Emit
    /// should never run — expansion replaces the node before the emitter walks — but if it ever does, it
    /// emits neutral zeros instead of the silent Scalar fallback an unknown kind would get.
    /// </summary>
    public static void RegisterInstanceGraphs(IReadOnlyDictionary<string, ShaderGraph> p_Fragments)
    {
        foreach (var s_Kind in s_Defs.Keys.Where(p_K => p_K.StartsWith(InstanceKindPrefix, StringComparison.Ordinal)).ToList())
            s_Defs.Remove(s_Kind);

        foreach (var (s_Name, s_Fragment) in p_Fragments)
        {
            // Emit is init-only, so the output list is gathered BEFORE the def is constructed.
            var s_OutputNames = new List<string>();
            foreach (var s_Output in s_Fragment.Nodes.Where(p_N => p_N.Kind == "InstanceOutput"))
                if (!s_OutputNames.Contains(s_Output.GetParam("Name")))
                    s_OutputNames.Add(s_Output.GetParam("Name"));

            var s_Def = new NodeDef
            {
                Kind = InstanceKindPrefix + s_Name,
                Title = s_Name,
                Category = "Instances",
                Description = "An instanceable graph from the library (Documents\\RimeShaderEditor\\" +
                              "instances). Expands into its full node network at emit time; edit the " +
                              "library file to change every use at once.",
                Emit = (p_S, _) =>
                {
                    foreach (var s_OutputName in s_OutputNames)
                        p_S.Out(s_OutputName, "float4(0, 0, 0, 0)");
                },
            };

            var s_SeenPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Input in s_Fragment.Nodes.Where(p_N => p_N.Kind == "InstanceInput"))
            {
                var s_Pin = s_Input.GetParam("Name");
                if (!s_SeenPins.Add(s_Pin))
                    continue;

                s_Def.Inputs.Add(new PortDef
                {
                    Name = s_Pin,
                    Type = s_Input.GetParam("Type") switch
                    {
                        "scalar" => ShaderPortType.SptScalar,
                        "vec2" => ShaderPortType.SptVec2,
                        "vec3" => ShaderPortType.SptVec3,
                        _ => ShaderPortType.SptVec4,
                    },
                    Default = InstanceLiteral(s_Input.GetParam("Value"), s_Input.GetParam("Type")),
                });
            }

            foreach (var s_OutputName in s_OutputNames)
                s_Def.Outputs.Add(new PortDef { Name = s_OutputName, Type = ShaderPortType.SptVec4 });

            Add(s_Def);
        }
    }

    private static void AddInputs()
    {
        // ── Family-agnostic access ────────────────────────────────────────────────────────────────────────
        // Terrain, skinned characters and vegetation each hand the pixel stage a DIFFERENT set of interpolators,
        // and only the rigid-mesh meanings are measured (verified bit-exact against the game's own shader). These
        // two nodes expose whatever the detected target actually declares, WITHOUT claiming to know what it
        // means — which is what makes the other families usable today instead of after a full RE of each.
        Add(new NodeDef
        {
            Kind = "Interpolator",
            Title = "Interpolator (raw)",
            Category = "Inputs",
            Description = "One of the target shader's TEXCOORD interpolators, as float4. Use when the family's " +
                          "per-interpolator meaning has not been measured - the tree shows the detected shape.",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef
                {
                    Name = "Index", Kind = ParamKind.Choice, Default = "0",
                    Choices = { "0", "1", "2", "3", "4", "5", "6", "7" },
                },
            },
            CanvasBadge = p_N => $"TC{p_N.GetParam("Index")}",
            Emit = (p_S, p_N) =>
            {
                var s_Index = (int) p_S.ParamScalar("Index");
                p_S.Out("Out", $"i.{p_S.Contract.FieldNameForGraphIndex(s_Index)}");
            },
        });

        Add(new NodeDef
        {
            Kind = "SubMaterialIndex",
            Title = "SubMaterial Index",
            Category = "Inputs",
            Description = "The flat-interpolated integer that selects the submaterial (MaxSubMaterialCount is 8). " +
                          "Only shaders whose contract declares an integer interpolator have one.",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) =>
            {
                var s_Selector = p_S.Contract.SubMaterialSelector;
                if (s_Selector == null)
                {
                    p_S.Out("Out", "0.0");
                    return;
                }

                p_S.Out("Out", $"((float) i.{p_S.Contract.FieldNameFor(s_Selector.Index)})");
            },
        });

        Add(new NodeDef
        {
            Kind = "TexCoord",
            Style = NodeStyle.Frostbite,
            Title = "TexCoord",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Params =
            {
                new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" },

                // Which interpolator carries the UV, and which half of it: a packed layout ships two UV sets
                // in ONE float4 (xy = the material UV, zw = the second set). "auto" keeps the measured
                // rigid-mesh field, which is what hand-authored graphs have always used.
                new ParamDef
                {
                    Name = "Interp", Kind = ParamKind.Choice, Default = "auto",
                    Choices = { "auto", "0", "1", "2", "3", "4", "5", "6", "7" },
                },
                new ParamDef { Name = "Half", Kind = ParamKind.Choice, Default = "xy", Choices = { "xy", "zw" } },
            },
            CanvasBadge = p_N => p_N.GetParam("Interp") is var s_Interp && s_Interp != "auto"
                ? $"TC{s_Interp}.{p_N.GetParam("Half")}"
                : null,
            // No coordinate-set selector: every StreamableTexture measured across the drum, the parachute and
            // the storm drain binds VertexElementUsage_TexCoord0, and this contract carries a single UV
            // interpolator, so a set index would offer exactly one valid value. Multi-set support needs a
            // target whose vertex shader actually outputs more than one.
            Emit = (p_S, p_N) =>
            {
                var s_Interp = p_N.GetParam("Interp");
                var s_Field = s_Interp != "auto" && int.TryParse(s_Interp, out var s_Index)
                    ? p_S.Contract.FieldNameForGraphIndex(s_Index)
                    : "TexCoord";
                var s_Half = p_N.GetParam("Half") == "zw" ? "zw" : "xy";
                p_S.Out("Out", Tiled(p_S, p_N, $"i.{s_Field}.{s_Half}"));
            },
        });

        Add(new NodeDef
        {
            Kind = "WorldPos",
            Title = "World Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Params =
            {
                // "auto" reads the measured rigid-mesh field; an index reads the interpolator the usage scan
                // classified as the world position in families whose layout is not measured.
                new ParamDef
                {
                    Name = "Interp", Kind = ParamKind.Choice, Default = "auto",
                    Choices = { "auto", "0", "1", "2", "3", "4", "5", "6", "7" },
                },
            },
            CanvasBadge = p_N => p_N.GetParam("Interp") is var s_Interp && s_Interp != "auto"
                ? $"TC{s_Interp}"
                : null,
            Emit = (p_S, p_N) =>
            {
                var s_Interp = p_N.GetParam("Interp");
                var s_Field = s_Interp != "auto" && int.TryParse(s_Interp, out var s_Index)
                    ? p_S.Contract.FieldNameForGraphIndex(s_Index)
                    : "WorldPos";
                p_S.Out("Out", $"i.{s_Field}.xyz");
            },
        });

        Add(new NodeDef
        {
            Kind = "TangentHandedness",
            Title = "Tangent Handedness",
            Category = "Inputs",
            Params =
            {
                // "derivatives" reads the hand off the drawn geometry; "rows" off the determinant of the
                // interpolated tangent frame (the game's per-vertex binormal sign) — kept for measuring
                // which field the game carries where (an instrumented package paints the body by it).
                new ParamDef { Name = "Formula", Kind = ParamKind.Choice, Default = "derivatives", Choices = { "derivatives", "rows" } },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Description = "+1 where the texture is laid on the surface the way it faces, −1 where it is laid in mirror. " +
                          "Read from the geometry itself: the orientation of the UV mapping across the pixel's screen " +
                          "neighbourhood against the orientation of the surface (screen derivatives of the world " +
                          "position and the UV, and the surface normal), so it owes nothing to the vertex data the " +
                          "game feeds its tangent frame with. A weapon whose two sides share one patch of texture " +
                          "differs in exactly this between the sides — what keeps a sticker on the side it was placed on.",
            Emit = (p_S, p_N) =>
            {
                // The sign of the tangent frame's determinant was the first reading, and in the game it gated
                // the sticker off BOTH sides (2026-09-12, F2000, still PNG): whatever the interpolated frame
                // carries, its determinant is not the mapping's handedness there. The derivatives are: the
                // UV Jacobian over screen space times the orientation of the position Jacobian against the
                // normal is det[dP/du, dP/dv, N] — the screen's own orientation cancels out of the product.
                bool Has(string p_Field) =>
                    p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == p_Field);

                if (p_N.GetParam("Formula") == "rows" && Has("TangentRow0"))
                {
                    p_S.Out("Out", "sign(determinant(float3x3(i.TangentRow0.xyz, i.TangentRow1.xyz, i.TangentRow2.xyz)))");
                    return;
                }

                // The same expression serves the preview and the game: the preview draws the mesh through a
                // lateral mirror precisely to show the game's geometry, so what it reads here IS the game's
                // handedness — and the side map stores that handedness (StickerSurface.Handedness).
                const string s_Flip = "";
                var s_UvJacobian = p_S.Declare("uvJacobian", ShaderPortType.SptScalar,
                    "(ddx(i.TexCoord.x) * ddy(i.TexCoord.y) - ddy(i.TexCoord.x) * ddx(i.TexCoord.y))");
                var s_Normal = Has("TangentRow0")
                    ? "float3(i.TangentRow0.z, i.TangentRow1.z, i.TangentRow2.z)"
                    : Has("WorldNormal") ? "i.WorldNormal.xyz" : null;
                if (s_Normal != null && Has("WorldPos"))
                {
                    var s_Facing = p_S.Declare("uvFacing", ShaderPortType.SptScalar,
                        $"dot(cross(ddx(i.WorldPos.xyz), ddy(i.WorldPos.xyz)), {s_Normal})");
                    p_S.Out("Out", $"{s_Flip}sign({s_UvJacobian} * {s_Facing})");
                }
                else
                {
                    // No world position to orient against: the screen orientation of a front face is one
                    // constant for the whole draw, so the UV Jacobian alone tells the sides apart, up to that
                    // constant — the gate's Flip is the knob for it.
                    p_S.Out("Out", $"{s_Flip}sign({s_UvJacobian})");
                }
            },
        });

        Add(new NodeDef
        {
            Kind = "StickerSideGate",
            Title = "Sticker Side Gate",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Side", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Hand", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Params =
            {
                new ParamDef { Name = "Flip", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "Enabled", Kind = ParamKind.Bool, Default = "true" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Description = "1 where a sticker may draw, 0 where it is the mirrored copy of one placed on the other " +
                          "side: Side is the side map's grey (0 = nothing painted here, ungated; 1/3 = laid out as it " +
                          "faces; 2/3 = laid out in mirror), Hand the Tangent Handedness. Flip swaps the two " +
                          "readings, should the handedness read in-game turn out with the opposite polarity; " +
                          "Enabled off makes the gate always 1 (a sticker on both halves).",
            Emit = (p_S, p_N) =>
            {
                if (p_N.GetParam("Enabled") == "false")
                {
                    p_S.Out("Out", "1.0");
                    return;
                }

                var s_Side = p_S.Declare("side", ShaderPortType.SptScalar, p_S.In("Side"));
                var s_Hand = p_S.Declare("hand", ShaderPortType.SptScalar, p_S.In("Hand"));
                var s_Positive = p_N.GetParam("Flip") == "true" ? "<" : ">";
                p_S.Out("Out", $"({s_Side} < 0.16) ? 1.0 : ((({s_Side} < 0.5) == ({s_Hand} {s_Positive} 0.0)) ? 1.0 : 0.0)");
            },
        });

        Add(new NodeDef
        {
            Kind = "CameraPos",
            Title = "Camera Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", "cameraPos"),
        });

        Add(new NodeDef
        {
            Kind = "Time",
            Title = "Time",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", "time"),
        });

        Add(new NodeDef
        {
            Kind = "ScreenSize",
            Title = "Screen Size",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out", "screenSize"),
        });

        Add(new NodeDef
        {
            Kind = "GeometricNormal",
            Title = "Geometric Normal",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Params =
            {
                // "auto" derives the normal from the measured tangent rows AND normalizes, the authoring-safe
                // form. An index reads the interpolator the usage scan classified as the world normal - RAW,
                // no normalize, because a translated shader must match its own bytecode, which normalizes
                // only if the original did.
                new ParamDef
                {
                    Name = "Interp", Kind = ParamKind.Choice, Default = "auto",
                    Choices = { "auto", "0", "1", "2", "3", "4", "5", "6", "7" },
                },
            },
            CanvasBadge = p_N => p_N.GetParam("Interp") is var s_Interp && s_Interp != "auto"
                ? $"TC{s_Interp}"
                : null,
            // The tangent frame arrives as three interpolated rows; its third column is the surface normal.
            Emit = (p_S, p_N) =>
            {
                var s_Interp = p_N.GetParam("Interp");
                if (s_Interp != "auto" && int.TryParse(s_Interp, out var s_Index))
                    p_S.Out("Out", $"i.{p_S.Contract.FieldNameForGraphIndex(s_Index)}.xyz");
                else if (p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == "TangentRow0"))
                    p_S.Out("Out", "normalize(float3(i.TangentRow0.z, i.TangentRow1.z, i.TangentRow2.z))");
                else if (p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == "WorldNormal"))
                    // The simple families interpolate the world normal itself - no frame to take a column of.
                    p_S.Out("Out", "normalize(i.WorldNormal.xyz)");
                else
                    p_S.Out("Out", "float3(0, 0, 1)");
            },
        });

        Add(new NodeDef
        {
            Kind = "ViewDirection",
            Title = "View Direction",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", "normalize(cameraPos - i.WorldPos.xyz)"),
        });

        Add(new NodeDef
        {
            Kind = "ScreenPosition",
            Title = "Screen Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            // Rasteriser position over the render-target size gives 0..1 screen UVs.
            Emit = (p_S, _) => p_S.Out("Out", "i.Position.xy * screenSize.zw"),
        });

        // ⚠ The RAW SV_Position, in pixels. Distinct from `ScreenPosition`, which already divides by the screen
        // size: a translated shader that does that division itself would otherwise do it twice, and the result
        // still looks like a picture, just the wrong one.
        Add(new NodeDef
        {
            Kind = "PixelPosition",
            Title = "Pixel Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Description = "SV_Position as the rasteriser delivers it, in pixels. ScreenPosition is this divided " +
                          "by the screen size.",
            Emit = (p_S, _) => p_S.Out("Out", "i.Position"),
        });

        Add(new NodeDef
        {
            Kind = "ViewportZParams",
            Title = "Viewport Z Params",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out", "viewportZMinMaxKzKw"),
        });

        Add(new NodeDef
        {
            Kind = "Fresnel",
            Title = "Fresnel",
            Category = "Inputs",
            Inputs =
            {
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                new PortDef { Name = "Exponent", Type = ShaderPortType.SptScalar, Default = "4.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            // Same form the vanilla GBuffer shader uses: saturate(1.001 - N.V) raised to a power. The input is
            // a TANGENT-space normal, matched to how the NormalMap node hands one over.
            Emit = (p_S, _) =>
            {
                var s_Normal = p_S.Declare("fresnelN", ShaderPortType.SptVec3, p_S.In("Normal"));
                var s_World = p_S.Declare("fresnelWs", ShaderPortType.SptVec3,
                    $"normalize(float3(dot({s_Normal}, i.TangentRow0.xyz), " +
                    $"dot({s_Normal}, i.TangentRow1.xyz), dot({s_Normal}, i.TangentRow2.xyz)))");
                var s_View = p_S.Declare("fresnelV", ShaderPortType.SptVec3,
                    "normalize(cameraPos - i.WorldPos.xyz)");
                p_S.Out("Out",
                    $"pow(saturate(1.001 - dot({s_World}, {s_View})), {p_S.In("Exponent")})");
            },
        });
    }

    /// <summary>
    /// The supersampled alpha-test tap tables, MEASURED from the vegetation-alpha pixel shaders (2026-08-23):
    /// each coverage bit is the texture's alpha sampled at uv + a·ddx(uv) + b·ddy(uv), compared against the
    /// threshold. The 2-tap form uses ±(0.25, 0.25); the 4-tap form is the standard rotated grid. The bit
    /// ORDER maps taps to MSAA samples and is kept exactly as the game emits it.
    /// </summary>
    private static readonly (float A, float B)[] s_CoverageTaps2 = { (0.25f, 0.25f), (-0.25f, -0.25f) };

    private static readonly (float A, float B)[] s_CoverageTaps4 =
        { (-0.125f, -0.375f), (0.375f, -0.125f), (-0.375f, 0.125f), (0.125f, 0.375f) };

    internal static (float A, float B)[] CoverageTaps(string p_Taps) =>
        p_Taps == "2" ? s_CoverageTaps2 : s_CoverageTaps4;

    /// <summary>
    /// Sample or SampleLevel depending on whether the Lod pin is wired — an explicit mip has to bypass the
    /// hardware's derivative-based selection, so it cannot be folded into Sample.
    /// </summary>
    private static string SampleCall(IEmitScope p_Scope, GraphNode p_Node, string p_Register)
    {
        var s_Texture = HlslNames.TextureVar(p_Register);
        var s_Coord = Tiled(p_Scope, p_Node, p_Scope.In("Coord"));
        var s_Sampler = PinnedSampler(p_Node) ?? p_Scope.SamplerFor(p_Register);

        return p_Scope.HasExplicitValue("Lod")
            ? $"{s_Texture}.SampleLevel({s_Sampler}, {s_Coord}, {p_Scope.In("Lod")})"
            : $"{s_Texture}.Sample({s_Sampler}, {s_Coord})";
    }

    /// <summary>
    /// The sampler a texture node is PINNED to ("sampler1"), or null when it follows the flavour it is emitted for.
    /// A graph moved onto another shader's solutions (a vehicle preset drawn on a donor's rigid flavours) keeps
    /// the samplers of the shader it was translated from: the donor may read everything through s0 while the
    /// original read its decals through s1 = Clamp/Clamp/Wrap, and addressing is not cosmetic. Stored only when a
    /// caller sets it (the bake's retargeting) — ⛔ read from Params directly: GetParam answers "0" for a
    /// parameter the node does not declare, which would pin every texture to s0.
    /// </summary>
    internal static string? PinnedSampler(GraphNode p_Node) =>
        p_Node.Params.TryGetValue("Sampler", out var s_Value) && int.TryParse(s_Value, out var s_Index) && s_Index >= 0
            ? "sampler" + s_Index
            : null;

    /// <summary>
    /// Applies the node's Tiling to a UV. This is not a borrowed convenience: DICE bakes a per-texture UV scale
    /// straight into the compiled shader — the Parachute pixel shader multiplies its texcoord by 20.0 and 0.5,
    /// which are exactly 1/Factor for the 0.05 and 2 recorded on those StreamableTextures in the shaderdb.
    /// Exposed here as the tiling itself (the reciprocal of Factor) because that is the intuitive direction.
    /// </summary>
    private static string Tiled(IEmitScope p_Scope, GraphNode p_Node, string p_Coord)
    {
        var s_Tiling = p_Node.GetParam("Tiling");
        var s_Literal = PortTypeUtils.FormatLiteral(s_Tiling, ShaderPortType.SptVec2);
        if (s_Literal == null || s_Tiling.Trim() is "1" or "1,1")
            return p_Coord;

        return $"({p_Coord}) * {s_Literal}";
    }

    private static void AddTextures()
    {
        Add(new NodeDef
        {
            Kind = "Texture",
            Style = NodeStyle.Frostbite,
            Title = "Texture",
            Category = "Textures",
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Lod", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                // Closed list: the emitted shell declares only t1 and t2, so a free-text register would
                // reference a texture variable that does not exist and the shader would fail to compile.
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "1",
                    // Up to eight: the drum has two but a character or a parachute declares far more, and a
                    // register the dropdown cannot offer is a slot the author simply cannot reach.
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" },
                },
                new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" },

                // Normal-map handling as flags on the texture node itself, mirroring how the shipped shaders
                // treat it: Unpack decodes the game's DXT5nm layout (X in ALPHA); TransformNormal rotates the
                // result through the interpolated tangent frame - rigid-mesh contracts only, the rows live in
                // TEXCOORD1..3.
                new ParamDef { Name = "Unpack", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "TransformNormal", Kind = ParamKind.Bool, Default = "false" },
            },
            Description = "Tiling multiplies the UV, matching the 1/Factor scaling the game bakes from the " +
                          "shaderdb's StreamableTexture into the compiled shader. Register picks which tN slot " +
                          "to read. Unpack decodes DXT5nm; TransformNormal takes the result to world space.",
            Emit = (p_S, p_N) =>
            {
                var s_Sampled = SampleCall(p_S, p_N, p_N.GetParam("Register"));
                if (p_N.GetParam("Unpack") != "true")
                {
                    p_S.Out("Out", s_Sampled);
                    return;
                }

                // Same decode the NormalMap node performs - one formula, two doors to it.
                var s_Sample = p_S.Declare("texSample", ShaderPortType.SptVec4, s_Sampled);
                var s_Xy = p_S.Declare("texXy", ShaderPortType.SptVec2,
                    $"float2({s_Sample}.x * {s_Sample}.w, {s_Sample}.y) * 2.0 - 1.0");
                var s_Normal = p_S.Declare("texNormal", ShaderPortType.SptVec3,
                    $"float3({s_Xy}, sqrt(max(0.0, 1.0 - dot({s_Xy}, {s_Xy}))))");

                if (p_N.GetParam("TransformNormal") == "true")
                    s_Normal = p_S.Declare("texWorldN", ShaderPortType.SptVec3,
                        $"normalize(float3(dot({s_Normal}, i.TangentRow0.xyz), " +
                        $"dot({s_Normal}, i.TangentRow1.xyz), dot({s_Normal}, i.TangentRow2.xyz)))");

                p_S.Out("Out", $"float4({s_Normal}, 1.0)");
            },
        });

        Add(new NodeDef
        {
            Kind = "AlphaCoverage",
            Title = "Alpha Coverage (supersampled alpha test)",
            Category = "Textures",
            Description = "The game's alpha-to-coverage idiom, measured from the vegetation shaders: samples " +
                          "the texture's ALPHA at Taps positions offset by fractions of the UV derivatives, " +
                          "compares each against Threshold, and packs one coverage bit per tap. Wire the " +
                          "output into a Raw Root's Coverage pin; it becomes SV_Coverage, so alpha-tested " +
                          "edges keep their per-sample dither instead of turning into solid squares.",
            Inputs =
            {
                new PortDef { Name = "Uv", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params =
            {
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "1",
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" },
                },
                new ParamDef { Name = "Taps", Kind = ParamKind.Choice, Default = "4", Choices = { "2", "4" } },
                new ParamDef { Name = "Threshold", Kind = ParamKind.Text, Default = "0.5" },
            },
            Emit = (p_S, p_N) =>
            {
                var s_Uv = p_S.Declare("covUv", ShaderPortType.SptVec2, p_S.In("Uv"));
                var s_Dx = p_S.Declare("covDx", ShaderPortType.SptVec2, $"ddx_coarse({s_Uv})");
                var s_Dy = p_S.Declare("covDy", ShaderPortType.SptVec2, $"ddy_coarse({s_Uv})");
                var s_Texture = HlslNames.TextureVar(p_N.GetParam("Register"));
                var s_Threshold = p_S.Declare("covThr", ShaderPortType.SptScalar,
                    PortTypeUtils.FormatLiteral(p_N.GetParam("Threshold"), ShaderPortType.SptScalar) ?? "0.5");

                var s_Bits = Palette.CoverageTaps(p_N.GetParam("Taps")).Select((p_Tap, p_Bit) =>
                    $"({s_Texture}.Sample({p_S.SamplerFor(p_N.GetParam("Register"))}, {s_Uv} + {Lit(p_Tap.A)} * {s_Dx} + {Lit(p_Tap.B)} * {s_Dy}).w" +
                    $" >= {s_Threshold} ? {1 << p_Bit}u : 0u)");
                p_S.Out("Out", $"(float) ({string.Join(" | ", s_Bits)})");
            },
        });

        // The fetches that are not flat 2D. BF3 uses all three: cube for the sky and reflection envmaps, 3D for
        // volume lookups, and 2D arrays for the parachute's per-submaterial texture sets. Each takes a THREE
        // component coordinate - a direction, a volume position, or uv plus a slice index - which is why they
        // cannot borrow the 2D node and why translating them as 2D produced a graph that rendered the wrong
        // thing without any error.
        foreach (var (s_Kind, s_Title, s_Meaning) in new[]
                 {
                     ("TextureCube", "Texture Cube", "a direction, not a UV"),
                     ("Texture3D", "Texture 3D", "a position inside the volume"),
                     ("TextureArray", "Texture 2D Array", "UV in xy, slice index in z"),
                 })
        {
            var s_Register = s_Kind;
            Add(new NodeDef
            {
                Kind = s_Kind,
                Title = s_Title,
                Category = "Textures",
                Inputs =
                {
                    new PortDef { Name = "Coord", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                    new PortDef { Name = "Lod", Type = ShaderPortType.SptScalar, Default = "0.0" },
                },
                Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
                Params =
                {
                    new ParamDef
                    {
                        Name = "Register", Kind = ParamKind.Choice, Default = "1",
                        Choices = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" },
                    },
                },
                Description = $"Samples the {s_Title.ToLowerInvariant()} bound to that register. Coord is {s_Meaning}.",
                Emit = (p_S, p_N) =>
                {
                    var s_Texture = HlslNames.TextureVar(p_N.GetParam("Register"));
                    var s_Coord = p_S.In("Coord");
                    p_S.Out("Out", p_S.HasExplicitValue("Lod")
                        ? $"{s_Texture}.SampleLevel({p_S.SamplerFor(p_N.GetParam("Register"))}, {s_Coord}, {p_S.In("Lod")})"
                        : $"{s_Texture}.Sample({p_S.SamplerFor(p_N.GetParam("Register"))}, {s_Coord})");
                },
            });

            _ = s_Register;
        }

        Add(new NodeDef
        {
            Kind = "NormalMap",
            Title = "Normal Map",
            Category = "Textures",
            HasTexturePreview = true,
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Lod", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Params =
            {
                new ParamDef
                {
                    Name = "Register", Kind = ParamKind.Choice, Default = "2",
                    // Up to eight: the drum has two but a character or a parachute declares far more, and a
                    // register the dropdown cannot offer is a slot the author simply cannot reach.
                    Choices = { "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" },
                },
                new ParamDef { Name = "Tiling", Kind = ParamKind.Text, Default = "1,1" },
            },
            Description = "Decodes a BF3 DXT5nm normal map (X lives in the ALPHA channel). Tiling multiplies " +
                          "the UV, as the game bakes 1/Factor into the compiled shader.",
            // BF3 normal maps are DXT5nm with X in the ALPHA channel: nx = (R*A)*2-1, ny = G*2-1,
            // nz reconstructed. Reading B instead produces a metallic-looking mess.
            Emit = (p_S, p_N) =>
            {
                var s_Sample = p_S.Declare("nmSample", ShaderPortType.SptVec4,
                    SampleCall(p_S, p_N, p_N.GetParam("Register")));
                var s_Xy = p_S.Declare("nmXy", ShaderPortType.SptVec2,
                    $"float2({s_Sample}.x * {s_Sample}.w, {s_Sample}.y) * 2.0 - 1.0");
                p_S.Out("Out",
                    $"float3({s_Xy}, sqrt(max(0.0, 1.0 - dot({s_Xy}, {s_Xy}))))");
            },
        });
    }

    private static void AddConstants()
    {
        Add(new NodeDef
        {
            Kind = "Color",
            Style = NodeStyle.Frostbite,
            Title = "Color",
            Category = "Constants",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptColor } },
            Params = { new ParamDef { Name = "Value", Kind = ParamKind.Color, Default = "1,1,1,1" } },
            CanvasBadge = p_N => p_N.GetParam("Value"),
            Emit = (p_S, p_N) =>
            {
                var s_Parts = p_N.GetParam("Value").Split(',');
                var s_Values = new float[4];
                for (var i = 0; i < 4; i++)
                    s_Values[i] = i < s_Parts.Length &&
                                  float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V)
                        ? s_V
                        : 1.0f;

                p_S.Out("Out",
                    $"float4({Lit(s_Values[0])}, {Lit(s_Values[1])}, {Lit(s_Values[2])}, {Lit(s_Values[3])})");
            },
        });

        Add(new NodeDef
        {
            Kind = "Scalar",
            Style = NodeStyle.Frostbite,
            Title = "Scalar",
            Category = "Constants",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params = { new ParamDef { Name = "Value", Kind = ParamKind.Scalar, Default = "1" } },
            CanvasBadge = p_N => p_N.GetParam("Value"),
            Emit = (p_S, p_N) => p_S.Out("Out", Lit(p_S.ParamScalar("Value"))),
        });
    }

    private static void AddMath()
    {
        AddBinary("Multiply", "Multiply", "{0} * {1}");
        AddBinary("Add", "Add", "{0} + {1}");
        AddBinary("Subtract", "Subtract", "{0} - {1}");

        Add(new NodeDef
        {
            Kind = "Lerp",
            Style = NodeStyle.Frostbite,
            Title = "Lerp",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input1", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Input2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Delta", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"lerp({p_S.In("Input1")}, {p_S.In("Input2")}, {p_S.In("Delta")})"),
        });

        Add(new NodeDef
        {
            Kind = "Blend",
            Title = "Blend",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Color1", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,1)" },
                new PortDef { Name = "Color2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Opacity", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef
                {
                    Name = "Mode", Kind = ParamKind.Enum, Default = nameof(BlendShaderMode.BsmLerp),
                    EnumType = typeof(BlendShaderMode),
                },
            },
            Emit = (p_S, _) =>
            {
                var s_A = p_S.In("Color1");
                var s_B = p_S.In("Color2");
                var s_T = p_S.In("Opacity");
                var s_Blended = p_S.ParamEnum<BlendShaderMode>("Mode") switch
                {
                    BlendShaderMode.BsmAdd => $"{s_A} + {s_B}",
                    BlendShaderMode.BsmSubtract => $"{s_A} - {s_B}",
                    BlendShaderMode.BsmMultiply => $"{s_A} * {s_B}",
                    BlendShaderMode.BsmMultiply2x => $"{s_A} * {s_B} * 2.0",
                    BlendShaderMode.BsmScreen => $"1.0 - (1.0 - {s_A}) * (1.0 - {s_B})",
                    BlendShaderMode.BsmDifference => $"abs({s_A} - {s_B})",
                    BlendShaderMode.BsmLighten => $"max({s_A}, {s_B})",
                    BlendShaderMode.BsmDarken => $"min({s_A}, {s_B})",
                    BlendShaderMode.BsmOverlay =>
                        $"lerp(2.0 * {s_A} * {s_B}, 1.0 - 2.0 * (1.0 - {s_A}) * (1.0 - {s_B}), step(0.5, {s_A}))",
                    _ => s_B,
                };

                p_S.Out("Out", $"lerp({s_A}, {s_Blended}, {s_T})");
            },
        });

        AddUnary("Normalize", "Normalize", ShaderPortType.SptVec3, "normalize({0})");
        AddUnary("Saturate", "Saturate", ShaderPortType.SptVec4, "saturate({0})");
        AddUnary("OneMinus", "One Minus", ShaderPortType.SptVec4, "1.0 - {0}");

        // The tangent-basis transform + normalize as ONE node - the same maths StandardRoot performs on its
        // Normal pin, exposed for shaders that need the world normal as a VALUE (to blend two normal maps, to
        // drive authored maths) rather than only for packing. Rigid-mesh contracts only: the three rows are the
        // measured TEXCOORD1..3.
        Add(new NodeDef
        {
            Kind = "TangentToWorld",
            Title = "Tangent To World",
            Category = "Vector",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Description = "Rotates a tangent-space normal into world space through the interpolated tangent " +
                          "frame and normalizes it - what a normal map needs before lighting maths can use it.",
            Emit = (p_S, _) =>
            {
                var s_Input = p_S.Declare("tsIn", ShaderPortType.SptVec3, p_S.In("Input"));
                p_S.Out("Out",
                    $"normalize(float3(dot({s_Input}, i.TangentRow0.xyz), " +
                    $"dot({s_Input}, i.TangentRow1.xyz), dot({s_Input}, i.TangentRow2.xyz)))");
            },
        });

        Add(new NodeDef
        {
            Kind = "Dot",
            Title = "Dot",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"dot({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Power",
            Title = "Power",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Base", Type = ShaderPortType.SptScalar, Default = "0.5" },
                new PortDef { Name = "Exponent", Type = ShaderPortType.SptScalar, Default = "2.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"pow(max(1e-6, {p_S.In("Base")}), {p_S.In("Exponent")})"),
        });

        Add(new NodeDef
        {
            Kind = "Curve",
            Title = "Curve",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Params =
            {
                new ParamDef
                {
                    Name = "Type", Kind = ParamKind.Enum, Default = nameof(CurveShaderType.CstSine),
                    EnumType = typeof(CurveShaderType),
                },
            },
            Emit = (p_S, _) =>
            {
                var s_X = p_S.In("Input");
                p_S.Out("Out", p_S.ParamEnum<CurveShaderType>("Type") switch
                {
                    CurveShaderType.CstSineNormalized => $"sin({s_X} * 6.2831853) * 0.5 + 0.5",
                    CurveShaderType.CstSawtooth => $"frac({s_X})",
                    CurveShaderType.CstTriangle => $"abs(frac({s_X}) * 2.0 - 1.0)",
                    CurveShaderType.CstSquare => $"step(0.5, frac({s_X})) * 2.0 - 1.0",
                    _ => $"sin({s_X} * 6.2831853)",
                });
            },
        });

        Add(new NodeDef
        {
            Kind = "MixOut",
            Title = "MixOut",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs =
            {
                new PortDef { Name = "RGB", Type = ShaderPortType.SptVec3 },
                new PortDef { Name = "R", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "G", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "B", Type = ShaderPortType.SptScalar },
                new PortDef { Name = "A", Type = ShaderPortType.SptScalar },
            },
            Emit = (p_S, _) =>
            {
                var s_In = p_S.Declare("mix", ShaderPortType.SptVec4, p_S.In("Input"));
                p_S.Out("RGB", $"{s_In}.xyz");
                p_S.Out("R", $"{s_In}.x");
                p_S.Out("G", $"{s_In}.y");
                p_S.Out("B", $"{s_In}.z");
                p_S.Out("A", $"{s_In}.w");
            },
        });
    }

    /// <summary>
    /// Wrappers over HLSL intrinsics. Unlike the Blend modes, the Curve shapes, the port types and the root
    /// pins — which come from DICE's own enums — these node NAMES are ours: BF3 ships no shader-graph node
    /// classes at all (verified against the retail client binary, its decompilation, and both server builds),
    /// so there is no authoritative list to mirror.
    /// </summary>
    private static void AddMoreMath()
    {
        AddBinary("Divide", "Divide", "{0} / {1}");
        AddBinary("Min", "Min", "min({0}, {1})");
        AddBinary("Max", "Max", "max({0}, {1})");
        AddBinary("Fmod", "Modulo", "fmod({0}, {1})");
        AddBinary("Step", "Step", "step({0}, {1})");

        // Comparisons as 0/1 MASKS, component-wise. DXBC's lt/ge/eq/ne write an all-ones bit pattern, which as a
        // float is a NaN - useless in a graph - so the value that travels here is a plain 0 or 1 and `movc`
        // becomes a lerp between the two sides. That is also what makes if-conversion possible at all: a branch
        // turns into "compute both, blend by the mask", which is the only shape a node graph can express.
        // ⛔⛔ WRITTEN AS THE COMPARISON ITSELF, never as the complement of the opposite one. `a < b` was
        // `1 - step(b, a)`, which is `1 - (a >= b)` - identical for ordinary numbers and WRONG for NaN: every
        // comparison against a NaN is false, so the real `a < b` yields 0 while the complement yields 1.
        // A particle shader reaches `log(0) * 0` = NaN by design, compares it, and expects the test to fail; the
        // complement form made it succeed and DISCARDED EVERY PIXEL. The cube simply vanished.
        AddBinary("Less", "Less Than", "((float4) (({0}) < ({1})))");
        AddBinary("GreaterEqual", "Greater or Equal", "((float4) (({0}) >= ({1})))");
        AddBinary("Equal", "Equal", "((float4) (({0}) == ({1})))");
        AddBinary("NotEqual", "Not Equal", "((float4) (({0}) != ({1})))");

        AddUnary("Abs", "Abs", ShaderPortType.SptVec4, "abs({0})");
        AddUnary("Floor", "Floor", ShaderPortType.SptVec4, "floor({0})");
        AddUnary("Ceil", "Ceil", ShaderPortType.SptVec4, "ceil({0})");
        AddUnary("Trunc", "Truncate", ShaderPortType.SptVec4, "trunc({0})");
        AddUnary("Frac", "Frac", ShaderPortType.SptVec4, "frac({0})");

        // Screen-space derivatives: how much a value changes between neighbouring pixels. The game uses them to
        // pick a mip level by hand and to fake a normal from a height field. `_coarse` is DICE's own choice of
        // precision and maps to the HLSL intrinsic of the same name - not to plain ddx, which is the fine
        // variant and would quietly compute something else.
        AddUnary("DdxCoarse", "Ddx (screen)", ShaderPortType.SptVec4, "ddx_coarse({0})");
        AddUnary("DdyCoarse", "Ddy (screen)", ShaderPortType.SptVec4, "ddy_coarse({0})");
        AddUnary("Round", "Round", ShaderPortType.SptVec4, "round({0})");
        AddUnary("Sign", "Sign", ShaderPortType.SptVec4, "sign({0})");
        AddUnary("Negate", "Negate", ShaderPortType.SptVec4, "-({0})");

        // ⛔ These are COMPONENT-WISE, and declaring them scalar was a real bug the moment the translator started
        // emitting one node per vector instruction: a scalar port takes `.x` of whatever is wired in and
        // broadcasts it, so `sqrt` of an RGB colour returned sqrt(R) in all three channels. The differential test
        // caught it as "red correct, green and blue wrong" - the same signature as any first-component-only bug.
        // HLSL's sqrt/exp2/log2/sin/cos are all component-wise, so vec4 is what they were always meant to be.
        AddUnary("Sqrt", "Sqrt", ShaderPortType.SptVec4, "sqrt(max(0.0, {0}))");
        AddUnary("Exp", "Exp", ShaderPortType.SptVec4, "exp({0})");

        // ⚠ Exp2 exists because the DXBC `exp` opcode is BASE 2, not base e. Measured, not assumed: exp2(x)
        // compiles to a bare `exp`, while exp(x) compiles to `mul by 1.442695` followed by `exp`. Translating a
        // game shader's `exp` into this node's e^x would have been wrong by that factor - and silently, since it
        // still compiles and still renders.
        AddUnary("Exp2", "Exp2", ShaderPortType.SptVec4, "exp2({0})");
        AddUnary("Log2", "Log2", ShaderPortType.SptVec4, "log2(max(1e-6, {0}))");

        // ⛔ UNGUARDED twins, for the translator only. `Log2`/`Sqrt` clamp their domain so a hand-authored graph
        // cannot produce NaN, and that guard is right for authoring - but it is NOT what the opcode does, and a
        // translated shader has to match the opcode. Measured on the spotlight cone: a `pow(uv.x, 0.25)` built
        // from log/exp reads uv.x = 0 at the edge, where the game gives exactly 0 and the clamped node gives
        // 0.032 - about 8/255 after the following one-minus. Fidelity and safety are different jobs, so they get
        // different nodes instead of one node that is wrong for one of them.
        AddUnary("Log2Raw", "Log2 (raw)", ShaderPortType.SptVec4, "log2({0})");
        AddUnary("SqrtRaw", "Sqrt (raw)", ShaderPortType.SptVec4, "sqrt({0})");
        AddUnary("Sin", "Sin", ShaderPortType.SptVec4, "sin({0})");
        AddUnary("Cos", "Cos", ShaderPortType.SptVec4, "cos({0})");

        // The rest of the original authoring catalog's trig/log set. Domain-clamped like Sqrt/Log2 above:
        // an authored graph must not be able to produce NaN from a knob value (the raw twins exist for the
        // translator when an opcode ever needs them).
        AddUnary("Tan", "Tan", ShaderPortType.SptVec4, "tan({0})");
        AddUnary("Asin", "Asin", ShaderPortType.SptVec4, "asin(clamp({0}, -1.0, 1.0))");
        AddUnary("Acos", "Acos", ShaderPortType.SptVec4, "acos(clamp({0}, -1.0, 1.0))");
        AddUnary("InverseSqrt", "Inverse Sqrt", ShaderPortType.SptVec4, "rsqrt(max(1e-6, {0}))");
        AddUnary("Log", "Log (natural)", ShaderPortType.SptVec4, "log(max(1e-6, {0}))");
        AddUnary("Log10", "Log10", ShaderPortType.SptVec4, "log10(max(1e-6, {0}))");

        // Boolean algebra over the 0/1 MASKS the comparisons above produce (never over raw values): And is
        // the mask product, Or saturates the sum — both stay exact for any mix of 0s and 1s.
        AddBinary("And", "And (masks)", "(({0}) * ({1}))");
        AddBinary("Or", "Or (masks)", "saturate(({0}) + ({1}))");

        // Any/All reduce a vector mask to ONE scalar mask (the intrinsics return bool; the graph keeps 0/1).
        Add(new NodeDef
        {
            Kind = "Any",
            Title = "Any (mask)",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "0.0" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"(any({p_S.In("Input")}) ? 1.0 : 0.0)"),
        });

        Add(new NodeDef
        {
            Kind = "All",
            Title = "All (mask)",
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "0.0" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"(all({p_S.In("Input")}) ? 1.0 : 0.0)"),
        });

        // ★★★ The artist-facing knob of a DICE surface shader, under DICE's own name.
        //
        // Their HLSL codegen emits these as `cbuffer externalConstants { float external_SpecularScale; ... }`, and
        // the names survive in the bytecode's RDEF chunk: 192 distinct ones across mp_017, matching the
        // ParameterName strings in EBX down to DICE's own typo (external_DiffuseBrighness). Before this node
        // existed every such read became a literal 0, which is what blocked 364 of that level's 625 shaders -
        // and silently, because a graph shading with zeros still compiles and still renders.
        //
        // ⚠ The NAME is DICE's; "External constant" as a node title is ours. It maps to their own
        // fb::ShaderValueParameterType_ExternalConstant, which is the "fed at runtime, not baked" case - so
        // changing this value in game does NOT need the shader recompiled.
        Add(new NodeDef
        {
            Kind = "ExternalConstant",
            Title = "External constant",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "Name", Kind = ParamKind.Text, Default = "external_Value" },
                new ParamDef { Name = "Buffer", Kind = ParamKind.Text, Default = "externalConstants" },
                new ParamDef { Name = "Register", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Element", Kind = ParamKind.Text, Default = "0" },
            },

            // The artist-facing name IS this node's meaning; without it on the canvas every external reads
            // as the same anonymous box.
            CanvasBadge = p_N => p_N.GetParam("Name").StartsWith("external_", StringComparison.Ordinal)
                ? p_N.GetParam("Name")["external_".Length..]
                : p_N.GetParam("Name"),
            Emit = (p_S, p_N) => p_S.Out("Out", SafeIdentifier(p_N.GetParam("Name"))),
        });

        // Parallax offset mapping: shifts a UV along the tangent-space view direction by a height sample, the
        // standard relief trick for depth on flat surfaces. ⚠ CONVENTIONAL formula - none of the level's own
        // shaders uses parallax, so there is no reference to verify against; marked as such rather than
        // presented as measured. Rigid-mesh contracts only (the tangent rows live in TEXCOORD1..3).
        Add(new NodeDef
        {
            Kind = "ParallaxOffset",
            Title = "Parallax Offset",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Height", Type = ShaderPortType.SptScalar, Default = "0.5" },
                new PortDef { Name = "Scale", Type = ShaderPortType.SptScalar, Default = "0.02" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Description = "Offsets the UV along the tangent-space view direction by Height * Scale - feed the " +
                          "result into a Texture's Coord. Conventional parallax formula (no vanilla reference " +
                          "exists in this level to verify against). Chain several for layered relief.",
            Emit = (p_S, _) =>
            {
                var s_View = p_S.Declare("pxView", ShaderPortType.SptVec3,
                    "normalize(cameraPos - i.WorldPos.xyz)");
                var s_ViewTs = p_S.Declare("pxViewTs", ShaderPortType.SptVec3,
                    $"float3(dot({s_View}, float3(i.TangentRow0.x, i.TangentRow1.x, i.TangentRow2.x)), " +
                    $"dot({s_View}, float3(i.TangentRow0.y, i.TangentRow1.y, i.TangentRow2.y)), " +
                    $"dot({s_View}, float3(i.TangentRow0.z, i.TangentRow1.z, i.TangentRow2.z)))");

                p_S.Out("Out",
                    $"({p_S.In("Coord")}) + {s_ViewTs}.xy * (({p_S.In("Height")}) - 0.5) * ({p_S.In("Scale")})");
            },
        });

        // Free-HLSL script node with named arguments that arrive as pins. The four fixed pins A..D map, in
        // order, onto the names declared in the Inputs parameter - `color:vec4, saturation:scalar` makes A
        // arrive as `color` and B as `saturation`. The body must `return` a float4.
        Add(new NodeDef
        {
            Kind = "Script",
            Title = "Script (HLSL)",
            Category = "Custom",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "C", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "D", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params =
            {
                new ParamDef { Name = "Inputs", Kind = ParamKind.Text, Default = "a:vec4" },
                new ParamDef { Name = "Body", Kind = ParamKind.Text, Default = "return a;" },
            },
            Description = "Custom HLSL for the maths no other node covers - a colour-saturation matrix, a " +
                          "bespoke falloff. Declare arguments as name:type (vec4/vec3/vec2/scalar) in Inputs; " +
                          "pins A-D feed them in order. The body runs in a hoisted function and must `return` " +
                          "a float4.",
            Emit = (p_S, p_N) =>
            {
                var s_Declared = (p_N.GetParam("Inputs") ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p_Spec => p_Spec.Split(':', StringSplitOptions.TrimEntries))
                    .Where(p_Parts => p_Parts.Length is 1 or 2 && p_Parts[0].Length > 0)
                    .Take(4)
                    .Select(p_Parts => (Name: SafeIdentifier(p_Parts[0]),
                        Type: (p_Parts.Length > 1 ? p_Parts[1] : "vec4") switch
                        {
                            "scalar" => "float", "vec2" => "float2", "vec3" => "float3", _ => "float4",
                        }))
                    .ToList();

                if (s_Declared.Count == 0)
                    s_Declared.Add((Name: "a", Type: "float4"));

                var s_Signature = string.Join(", ", s_Declared.Select(p_D => $"{p_D.Type} {p_D.Name}"));
                var s_Function = p_S.HoistFunction(s_Signature, p_N.GetParam("Body") is { Length: > 0 } s_Body
                    ? s_Body
                    : "return 0;");

                var s_Pins = new[] { "A", "B", "C", "D" };
                var s_Arguments = s_Declared.Select((p_D, p_I) => p_D.Type switch
                {
                    "float" => $"({p_S.In(s_Pins[p_I])}).x",
                    "float2" => $"({p_S.In(s_Pins[p_I])}).xy",
                    "float3" => $"({p_S.In(s_Pins[p_I])}).xyz",
                    _ => p_S.In(s_Pins[p_I]),
                });

                p_S.Out("Out", $"{s_Function}({string.Join(", ", s_Arguments)})");
            },
        });

        // The branchless conditional. `movc` is exactly this, and so is every `if` after if-conversion.
        Add(new NodeDef
        {
            Kind = "Select",
            Title = "Select",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Condition", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "WhenTrue", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "WhenFalse", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Description = "Condition is a 0/1 mask, as the comparison nodes produce. Picks WhenTrue where it is " +
                          "1 and WhenFalse where it is 0. Use Lerp to blend between two values instead.",

            // ⛔⛔⛔ A REAL SELECT, NOT A LERP - AND THE DIFFERENCE IS NaN.
            // This was `lerp(WhenFalse, WhenTrue, saturate(Condition))`, which is the same number for every
            // finite input and NOT the same for a NaN one: lerp expands to `a + t*(b - a)`, so with t = 0 a NaN
            // in `b` still arrives as `0 * NaN = NaN`. That matters because if-conversion evaluates BOTH sides
            // of every branch, and a branch the hardware would skip is often skipped PRECISELY because its body
            // is undefined there - `rsq` of a zero vector, a divide by zero, `log(0)`. The terrain normalises a
            // vector that is zero when its layer is absent, so the untaken branch handed a NaN to a select whose
            // condition was 0, the NaN survived the blend, and RT1 came out black on eleven shaders.
            // A ternary compiles to `movc`, which copies bits and cannot propagate anything from the side it
            // did not pick.
            Emit = (p_S, _) => p_S.Out("Out",
                $"((((float4)({p_S.In("Condition")})) != 0.0f) ? ((float4)({p_S.In("WhenTrue")})) : " +
                $"((float4)({p_S.In("WhenFalse")})))"),
        });

        // Alpha test. Not a value but a STATEMENT - the pixel stops existing - so it passes its input straight
        // through and does the discard as a side effect, which is what keeps it alive through the emitter's
        // dead-code pass.
        Add(new NodeDef
        {
            Kind = "Clip",
            Title = "Clip (alpha test)",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Discard", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Description = "Kills the pixel where Discard is non-zero, then passes Input through unchanged.",
            Emit = (p_S, _) =>
            {
                p_S.Line($"clip({p_S.In("Discard")} != 0.0 ? -1.0 : 1.0);");
                p_S.Out("Out", p_S.In("Input"));
            },
        });

        Add(new NodeDef
        {
            Kind = "Atan2",
            Title = "Atan2",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Y", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "X", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"atan2({p_S.In("Y")}, {p_S.In("X")})"),
        });

        Add(new NodeDef
        {
            Kind = "Clamp",
            Title = "Clamp",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Min", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" },
                new PortDef { Name = "Max", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"clamp({p_S.In("Input")}, {p_S.In("Min")}, {p_S.In("Max")})"),
        });

        Add(new NodeDef
        {
            Kind = "SmoothStep",
            Title = "SmoothStep",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Edge0", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Edge1", Type = ShaderPortType.SptScalar, Default = "1.0" },
                new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "0.5" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"smoothstep({p_S.In("Edge0")}, {p_S.In("Edge1")}, {p_S.In("Input")})"),
        });

        Add(new NodeDef
        {
            Kind = "Remap",
            Title = "Remap",
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "FromMin", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "FromMax", Type = ShaderPortType.SptScalar, Default = "1.0" },
                new PortDef { Name = "ToMin", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "ToMax", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) =>
            {
                var s_From = p_S.Declare("remapFrom", ShaderPortType.SptScalar,
                    $"{p_S.In("FromMax")} - {p_S.In("FromMin")}");
                p_S.Out("Out",
                    $"{p_S.In("ToMin")} + ({p_S.In("Input")} - {p_S.In("FromMin")}) / " +
                    $"(abs({s_From}) < 1e-6 ? 1e-6 : {s_From}) * ({p_S.In("ToMax")} - {p_S.In("ToMin")})");
            },
        });
    }

    private static void AddVector()
    {
        Add(new NodeDef
        {
            Kind = "Append",
            Title = "Append",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "X", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Y", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Z", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "W", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            // Always builds a float4; wiring it into a narrower input takes the leading components.
            Emit = (p_S, _) => p_S.Out("Out",
                $"float4({p_S.In("X")}, {p_S.In("Y")}, {p_S.In("Z")}, {p_S.In("W")})"),
        });

        Add(new NodeDef
        {
            Kind = "Swizzle",
            Title = "Swizzle",
            Category = "Vector",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec4, Default = "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Params = { new ParamDef { Name = "Channels", Kind = ParamKind.Text, Default = "xyzw" } },
            CanvasBadge = p_N => $".{p_N.GetParam("Channels")}",
            Emit = (p_S, p_N) =>
            {
                var s_Channels = new string(p_N.GetParam("Channels")
                    .Where(p_C => "xyzwrgba".Contains(p_C)).Take(4).ToArray());

                if (s_Channels.Length == 0)
                    s_Channels = "xyzw";

                var s_Input = p_S.Declare("swz", ShaderPortType.SptVec4, p_S.In("Input"));
                var s_Padding = string.Join("", Enumerable.Repeat(", 0", 4 - s_Channels.Length));
                p_S.Out("Out", $"float4({s_Input}.{s_Channels}{s_Padding})");
            },
        });

        Add(new NodeDef
        {
            Kind = "Cross",
            Title = "Cross",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(1,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,1,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", $"cross({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Length",
            Title = "Length",
            Category = "Vector",
            Inputs = { new PortDef { Name = "Input", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"length({p_S.In("Input")})"),
        });

        Add(new NodeDef
        {
            Kind = "Distance",
            Title = "Distance",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "A", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" },
                new PortDef { Name = "B", Type = ShaderPortType.SptVec3, Default = "float3(0,0,0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptScalar } },
            Emit = (p_S, _) => p_S.Out("Out", $"distance({p_S.In("A")}, {p_S.In("B")})"),
        });

        Add(new NodeDef
        {
            Kind = "Reflect",
            Title = "Reflect",
            Category = "Vector",
            Inputs =
            {
                new PortDef { Name = "Incident", Type = ShaderPortType.SptVec3, Default = "float3(0,0,-1)" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out", $"reflect({p_S.In("Incident")}, {p_S.In("Normal")})"),
        });

        Add(new NodeDef
        {
            Kind = "Refract",
            Title = "Refract",
            Category = "Vector",
            Description = "HLSL refract: bends the incident direction through a surface with the given " +
                          "index-of-refraction ratio (entering/exiting medium). Returns zero on total " +
                          "internal reflection, exactly like the intrinsic.",
            Inputs =
            {
                new PortDef { Name = "Incident", Type = ShaderPortType.SptVec3, Default = "float3(0,0,-1)" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0,0,1)" },
                new PortDef { Name = "Eta", Type = ShaderPortType.SptScalar, Default = "0.66" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"refract(normalize({p_S.In("Incident")}), normalize({p_S.In("Normal")}), {p_S.In("Eta")})"),
        });
    }

    private static void AddUv()
    {
        Add(new NodeDef
        {
            Kind = "UvTransform",
            Title = "UV Transform",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Scale", Type = ShaderPortType.SptVec2, Default = "float2(1, 1)" },
                new PortDef { Name = "Offset", Type = ShaderPortType.SptVec2, Default = "float2(0, 0)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"{p_S.In("Coord")} * {p_S.In("Scale")} + {p_S.In("Offset")}"),
        });

        Add(new NodeDef
        {
            Kind = "Panner",
            Style = NodeStyle.Frostbite,
            Title = "Panner",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Speed", Type = ShaderPortType.SptVec2, Default = "float2(0.1, 0)" },
                new PortDef { Name = "Time", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) => p_S.Out("Out",
                $"{p_S.In("Coord")} + {p_S.In("Speed")} * {p_S.In("Time")}"),
        });

        Add(new NodeDef
        {
            Kind = "TexelSnap",
            Title = "Texel Snap",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
            },
            Params = { new ParamDef { Name = "Side", Kind = ParamKind.Text, Default = "1024" } },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Description = "Moves a UV to the centre of the texel it falls in, on a texture Side texels square — a " +
                          "lookup table (the sticker coordinate map) is read one texel at a time, never blended.",
            Emit = (p_S, p_N) =>
            {
                var s_Side = HlslNames.Number(p_N.GetParam("Side"), 1024);
                p_S.Out("Out", $"(floor({p_S.In("Coord")} * {s_Side}) + 0.5) / {s_Side}");
            },
        });

        Add(new NodeDef
        {
            Kind = "StickerCoordinates",
            Title = "Sticker Coordinates",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
            },
            Params =
            {
                new ParamDef { Name = "Register", Kind = ParamKind.Text, Default = "7" },
                new ParamDef { Name = "Side", Kind = ParamKind.Text, Default = "1024" },
                new ParamDef { Name = "XChannel", Kind = ParamKind.Text, Default = "w" },
                new ParamDef { Name = "YChannel", Kind = ParamKind.Text, Default = "y" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Description = "Reads the animated sticker's frame coordinate (x from XChannel, y from YChannel, 0 = nothing " +
                          "here) out of the coordinate map at Register, Side texels square, at level 0. Between texels the " +
                          "coordinate is INTERPOLATED — the field is linear across a sticker, so the frame is sampled " +
                          "smoothly however coarse the map is (read one texel at a time, a sticker had the map's " +
                          "resolution, not the frame's) — except where the four texels disagree by a lot or one says " +
                          "'nothing': the sticker's edge and the seams of the unwrap, where the nearest texel is taken as it is.",
            Emit = (p_S, p_N) =>
            {
                var s_Register = p_N.GetParam("Register");
                var s_Side = HlslNames.Number(p_N.GetParam("Side"), 1024);
                var s_Channels = (p_N.GetParam("XChannel") is { Length: 1 } s_X ? s_X : "w") + (p_N.GetParam("YChannel") is { Length: 1 } s_Y ? s_Y : "y");
                var s_Texture = HlslNames.TextureVar(s_Register);
                var s_Sampler = p_S.SamplerFor(s_Register);
                var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
                var s_SideText = s_Side.ToString("0.####", s_Culture);
                var s_Uv = p_S.Declare("sc_uv", ShaderPortType.SptVec2, p_S.In("Coord"));
                var s_T = p_S.Declare("sc_t", ShaderPortType.SptVec2, $"{s_Uv} * {s_SideText} - 0.5");
                var s_T0 = p_S.Declare("sc_t0", ShaderPortType.SptVec2, $"floor({s_T})");
                var s_F = p_S.Declare("sc_f", ShaderPortType.SptVec2, $"{s_T} - {s_T0}");
                string Tap(string p_Name, string p_Offset) =>
                    p_S.Declare(p_Name, ShaderPortType.SptVec2, $"{s_Texture}.SampleLevel({s_Sampler}, ({s_T0} + {p_Offset}) / {s_SideText}, 0.0).{s_Channels}");
                var s_C00 = Tap("sc_c00", "float2(0.5, 0.5)");
                var s_C10 = Tap("sc_c10", "float2(1.5, 0.5)");
                var s_C01 = Tap("sc_c01", "float2(0.5, 1.5)");
                var s_C11 = Tap("sc_c11", "float2(1.5, 1.5)");
                var s_Near = p_S.Declare("sc_near", ShaderPortType.SptVec2,
                    $"{s_Texture}.SampleLevel({s_Sampler}, (floor({s_Uv} * {s_SideText}) + 0.5) / {s_SideText}, 0.0).{s_Channels}");
                var s_Lo = p_S.Declare("sc_lo", ShaderPortType.SptVec2, $"min(min({s_C00}, {s_C10}), min({s_C01}, {s_C11}))");
                var s_Hi = p_S.Declare("sc_hi", ShaderPortType.SptVec2, $"max(max({s_C00}, {s_C10}), max({s_C01}, {s_C11}))");
                var s_Smooth = p_S.Declare("sc_bilinear", ShaderPortType.SptVec2,
                    $"lerp(lerp({s_C00}, {s_C10}, {s_F}.x), lerp({s_C01}, {s_C11}, {s_F}.x), {s_F}.y)");
                // Continuous where the four taps agree (a smooth field steps a few 255ths a texel) and none
                // says "nothing"; the nearest texel across a seam or at the edge.
                p_S.Out("Out",
                    $"(min({s_Lo}.x, {s_Lo}.y) > 0.002 && max({s_Hi}.x - {s_Lo}.x, {s_Hi}.y - {s_Lo}.y) < 0.2) ? {s_Smooth} : {s_Near}");
            },
        });

        Add(new NodeDef
        {
            Kind = "Flipbook",
            Title = "Flipbook",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Time", Type = ShaderPortType.SptScalar, Default = "time", DefaultIsExpression = true },
            },
            Params =
            {
                new ParamDef { Name = "Side", Kind = ParamKind.Text, Default = "1024" },
                new ParamDef { Name = "Height", Kind = ParamKind.Text, Default = "" },
                new ParamDef { Name = "CellW", Kind = ParamKind.Text, Default = "128" },
                new ParamDef { Name = "CellH", Kind = ParamKind.Text, Default = "128" },
                new ParamDef { Name = "Cols", Kind = ParamKind.Text, Default = "8" },
                new ParamDef { Name = "Frames", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Inset", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Delay", Kind = ParamKind.Text, Default = "0.1" },
                new ParamDef { Name = "Timeline", Kind = ParamKind.Text, Default = "" },
                new ParamDef { Name = "Cells", Kind = ParamKind.Text, Default = "" },
            },
            Outputs =
            {
                new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 },
                new PortDef { Name = "Inside", Type = ShaderPortType.SptScalar },
            },
            Description = "Plays a sheet of frames: Coord is the point inside one frame (0..1), Out the UV of that point " +
                          "in the frame that is on at Time. Cells of CellW×CellH on a sheet Side wide and Height tall " +
                          "(Height empty = square), Cols per row, Frames in all, each drawn Inset texels inside its cell. " +
                          "Delay is the seconds every frame lasts; a Timeline (the second each frame ENDS, comma-separated) " +
                          "overrides it for frames of unequal length — the GIF's own timing, never resampled. The loop is as " +
                          "long as the last end time. Cells (comma-separated, one per frame) names the cell each frame plays " +
                          "from when identical frames share one; empty = frame i in cell i. Inside is 1 where Coord names a " +
                          "point (both parts above zero) and 0 where a coordinate map says 'nothing here' — an opaque " +
                          "frame's corner must never paint the rest of the object.",
            Emit = (p_S, p_N) =>
            {
                var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
                var s_Side = HlslNames.Number(p_N.GetParam("Side"), 1024);
                var s_Height = HlslNames.Number(p_N.GetParam("Height"), s_Side);
                var s_CellW = HlslNames.Number(p_N.GetParam("CellW"), 128);
                var s_CellH = HlslNames.Number(p_N.GetParam("CellH"), 128);
                var s_Cols = Math.Max(1, (int) HlslNames.Number(p_N.GetParam("Cols"), 8));
                var s_Frames = Math.Max(1, (int) HlslNames.Number(p_N.GetParam("Frames"), 1));
                var s_Inset = HlslNames.Number(p_N.GetParam("Inset"), 1);
                var s_Delay = Math.Max(0.001, HlslNames.Number(p_N.GetParam("Delay"), 0.1));
                var s_Timeline = p_N.GetParam("Timeline").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p_T => double.TryParse(p_T.Trim(), System.Globalization.NumberStyles.Float, s_Culture, out var s_V) ? s_V : -1)
                    .Where(p_T => p_T >= 0)
                    .ToList();

                string F(double p_V) => p_V.ToString("0.####", s_Culture);
                string s_Frame;
                if (s_Timeline.Count == s_Frames && s_Frames > 1)
                {
                    // Frames of unequal length: the second each one ends, walked in a hoisted function.
                    var s_Body = $"float ends[{s_Frames}] = {{ {string.Join(", ", s_Timeline.Select(F))} }};\n" +
                                 "float f = 0.0;\n" +
                                 $"[unroll] for (int i = 0; i < {s_Frames - 1}; i++)\n" +
                                 "    f += (t >= ends[i]) ? 1.0 : 0.0;\n" +
                                 "return float4(f, 0, 0, 0);";
                    var s_Function = p_S.HoistFunction("float t", s_Body);
                    s_Frame = p_S.Declare("fb_frame", ShaderPortType.SptScalar,
                        $"{s_Function}(fmod({p_S.In("Time")}, {F(s_Timeline[^1])})).x");
                }
                else
                {
                    var s_Total = s_Frames * s_Delay;
                    s_Frame = p_S.Declare("fb_frame", ShaderPortType.SptScalar,
                        $"min({s_Frames - 1}.0, floor(fmod({p_S.In("Time")}, {F(s_Total)}) / {F(s_Delay)}))");
                }

                // Identical frames share a cell: a table from frame to cell, walked in a hoisted function.
                var s_Cells = p_N.GetParam("Cells").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p_C => int.TryParse(p_C.Trim(), System.Globalization.NumberStyles.Integer, s_Culture, out var s_V) ? s_V : -1)
                    .Where(p_C => p_C >= 0)
                    .ToList();
                var s_Slot = s_Frame;
                if (s_Cells.Count == s_Frames && s_Frames > 1 && s_Cells.Select((p_C, p_I) => p_C != p_I).Any(p_Differs => p_Differs))
                {
                    var s_Table = $"float cells[{s_Frames}] = {{ {string.Join(", ", s_Cells.Select(p_C => F(p_C)))} }};\n" +
                                  $"return float4(cells[clamp((int) t, 0, {s_Frames - 1})], 0, 0, 0);";
                    var s_Lookup = p_S.HoistFunction("float t", s_Table);
                    s_Slot = p_S.Declare("fb_slot", ShaderPortType.SptScalar, $"{s_Lookup}({s_Frame}).x");
                }

                var s_Cell = p_S.Declare("fb_cell", ShaderPortType.SptVec2,
                    $"float2(fmod({s_Slot}, {s_Cols}.0), floor({s_Slot} / {s_Cols}.0))");
                var s_Coord = p_S.Declare("fb_coord", ShaderPortType.SptVec2, $"saturate({p_S.In("Coord")})");
                // Inside: a coordinate map stores 1..255 for a point of the frame and 0 for "nothing here".
                p_S.Out("Inside", $"(min({s_Coord}.x, {s_Coord}.y) > 0.001) ? 1.0 : 0.0");
                p_S.Out("Out",
                    $"({s_Cell} * float2({F(s_CellW)}, {F(s_CellH)}) + {F(s_Inset)} + {s_Coord} * " +
                    $"float2({F(s_CellW - 2 * s_Inset)}, {F(s_CellH - 2 * s_Inset)})) / float2({F(s_Side)}, {F(s_Height)})");
            },
        });

        // ⭐ A BF4-format emblem (Battlelog's layer list: shape, colour, opacity, angle, flips, centre, size), drawn by the
        // weapon's own shader over the emblem slot. Coord is the point of the emblem's
        // square (the slot's coordinate map, as the animated sticker reads it: x right, y down, 0 = no slot here). Every
        // layer is ONE external constant the ENGINE fills per weapon (the weapon's shader parameter component copies its
        // vectors into its own block when the weapon is created — measured in game 2026-09-29), so one compiled shader
        // shows each player's own emblem, and a slot whose layers are all zero draws nothing. One, not two: the engine packs
        // a shader's external values into a buffer of 64 registers (fb::DxShaderDispatcher, measured 2026-09-29 — eighty
        // layer constants beside the preset's twelve overflowed it and killed the client), so each layer is four integers
        // below 2^24, exact in a float (EmblemSlot.Encode):
        //   x = centre x (12 bits) · 4096 + centre y (12 bits)        — on the square from −0.5 to 1.5;
        //   y = half width (10) · 16384 + half height (10) · 16 + opacity (4) — half sizes 0..2, opacity in fifteenths;
        //   z = angle (12) · 4096 + flips (2: x = 1, y = 2) · 1024 + atlas cell (10) — the angle clockwise, a turn in 4096;
        //   w = the colour 0xRRGGBB.
        // Layer 0 is the bottom one, as in Battlelog. The shapes come from the atlas at Register: GRID×GRID cells of CELL
        // texels, each shape stretched to its INNER box in the middle of its cell (Battlelog stretches its SVG the same way),
        // the coverage in alpha — the same atlas and the same lookup as the emblem decal (a mip cap and an analytic box mask:
        // far away a thin layer would otherwise smear its whole cell; seen in a game capture).
        Add(new NodeDef
        {
            Kind = "EmblemLayers",
            Title = "Emblem Layers",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "float2(0.0, 0.0)" },
            },
            Params =
            {
                new ParamDef { Name = "Register", Kind = ParamKind.Text, Default = "8" },
                new ParamDef { Name = "Buffer", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Base", Kind = ParamKind.Text, Default = "12" },
                new ParamDef { Name = "Layers", Kind = ParamKind.Text, Default = "40" },
                new ParamDef { Name = "Grid", Kind = ParamKind.Text, Default = "8" },
                new ParamDef { Name = "Cell", Kind = ParamKind.Text, Default = "128" },
                new ParamDef { Name = "Inner", Kind = ParamKind.Text, Default = "112" },
                new ParamDef { Name = "Side", Kind = ParamKind.Text, Default = "1024" },
                new ParamDef { Name = "MaxLod", Kind = ParamKind.Text, Default = "3" },
            },
            Outputs =
            {
                new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 },
                new PortDef { Name = "Inside", Type = ShaderPortType.SptScalar },
            },
            Description = "Draws a BF4 emblem (up to Layers layers) at the point Coord of its square: Out is the colour " +
                          "premultiplied by the coverage, and the coverage in w. Each layer is one packed external constant " +
                          "(external_EmblemL0.. from element Base of the block at Buffer, right after the shader's own), fed per weapon by the engine; " +
                          "all zero = nothing drawn. Shapes from the atlas at Register (Grid×Grid cells of Cell texels, the " +
                          "shape in the middle Inner, coverage in alpha). Inside is 1 where Coord names a point of the square.",
            Emit = (p_S, p_N) =>
            {
                var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
                string F(double p_V) => p_V.ToString("0.0####", s_Culture);
                var s_Register = p_N.GetParam("Register");
                var s_Texture = HlslNames.TextureVar(s_Register);
                var s_Sampler = p_S.SamplerFor(s_Register);
                var s_Grid = Math.Max(1.0, HlslNames.Number(p_N.GetParam("Grid"), 8));
                var s_Cell = HlslNames.Number(p_N.GetParam("Cell"), 128);
                var s_Inner = HlslNames.Number(p_N.GetParam("Inner"), 112);
                var s_Side = HlslNames.Number(p_N.GetParam("Side"), 1024);
                var s_MaxLod = HlslNames.Number(p_N.GetParam("MaxLod"), 3);

                // One layer over what is below it. The gradients come in from outside (the caller's control flow is
                // not uniform, so no ddx/fwidth in here): the box's own gradients give both the lookup's footprint and
                // the width of its anti-aliased edge.
                var s_Layer = p_S.HoistFunction(
                    "float2 p, float2 dpx, float2 dpy, float4 L, float4 below",
                    // unpacking: every division is by a power of two, exact on these integers
                    "float qcx = floor(L.x / 4096.0);\n" +
                    "float qcy = L.x - qcx * 4096.0;\n" +
                    "float qhw = floor(L.y / 16384.0);\n" +
                    "float ry = L.y - qhw * 16384.0;\n" +
                    "float qhh = floor(ry / 16.0);\n" +
                    "float qop = ry - qhh * 16.0;\n" +
                    "if (qop <= 0.0 || qhw <= 0.0 || qhh <= 0.0)\n" +
                    "    return below;\n" +
                    "float qa = floor(L.z / 4096.0);\n" +
                    "float rz = L.z - qa * 4096.0;\n" +
                    "float flips = floor(rz / 1024.0);\n" +
                    "float cellIndex = rz - flips * 1024.0;\n" +
                    "float2 centre = float2(qcx, qcy) / 4095.0 * 2.0 - 0.5;\n" +
                    "float2 hs = float2(qhw, qhh) / 1023.0 * 2.0;\n" +
                    "hs.x = fmod(flips, 2.0) >= 1.0 ? -hs.x : hs.x;\n" +
                    "hs.y = flips >= 2.0 ? -hs.y : hs.y;\n" +
                    "float angle = qa / 4096.0 * 6.28318531;\n" +
                    "float opacity = qop / 15.0;\n" +
                    "float s = sin(angle);\n" +
                    "float c = cos(angle);\n" +
                    "float2 d = p - centre;\n" +
                    "float2 size = 2.0 * hs;\n" +
                    "float2 inBox = float2(d.x * c + d.y * s, -d.x * s + d.y * c) / size + 0.5;\n" +
                    "float2 boxDx = float2(dpx.x * c + dpx.y * s, -dpx.x * s + dpx.y * c) / size;\n" +
                    "float2 boxDy = float2(dpy.x * c + dpy.y * s, -dpy.x * s + dpy.y * c) / size;\n" +
                    $"float reach = {F((s_Cell - s_Inner) * 0.5 / s_Inner)};\n" +
                    "float2 inCell = clamp(inBox, -reach, 1.0 + reach);\n" +
                    $"float2 cell = float2(fmod(cellIndex, {F(s_Grid)}), floor(cellIndex / {F(s_Grid)}));\n" +
                    $"float2 atlasUv = (cell * {F(s_Cell)} + {F((s_Cell - s_Inner) * 0.5)} + inCell * {F(s_Inner)}) / {F(s_Side)};\n" +
                    $"float2 gx = boxDx * {F(s_Inner / s_Side)};\n" +
                    $"float2 gy = boxDy * {F(s_Inner / s_Side)};\n" +
                    $"float footprint = max(max(length(gx), length(gy)) * {F(s_Side)}, 1e-6);\n" +
                    $"float shrink = footprint > {F(Math.Pow(2, s_MaxLod))} ? {F(Math.Pow(2, s_MaxLod))} / footprint : 1.0;\n" +
                    $"float shape = {s_Texture}.SampleGrad({s_Sampler}, atlasUv, gx * shrink, gy * shrink).w;\n" +
                    "float2 edgeWidth = max(abs(boxDx) + abs(boxDy), 1e-5);\n" +
                    "float2 edge = saturate(min(inBox, 1.0 - inBox) / edgeWidth + 0.5);\n" +
                    "float alpha = shape * edge.x * edge.y * saturate(opacity);\n" +
                    "float red = floor(L.w / 65536.0);\n" +
                    "float green = floor((L.w - red * 65536.0) / 256.0);\n" +
                    "float blue = L.w - red * 65536.0 - green * 256.0;\n" +
                    "float3 colour = pow(saturate(float3(red, green, blue) / 255.0), 2.2);\n" +
                    "return float4(colour * alpha, alpha) + below * (1.0 - alpha);");

                // Every layer, bottom first, and only where the slot is: outside it the lookups are skipped.
                var s_Fields = EmblemFields(p_N);
                var s_Calls = new System.Text.StringBuilder();
                s_Calls.Append("if (min(p.x, p.y) <= 0.001)\n    return float4(0.0, 0.0, 0.0, 0.0);\n");
                s_Calls.Append("float4 acc = float4(0.0, 0.0, 0.0, 0.0);\n");
                foreach (var s_Field in s_Fields)
                    s_Calls.Append($"acc = {s_Layer}(p, dpx, dpy, {s_Field.Name}, acc);\n");
                s_Calls.Append("return acc;");
                var s_All = p_S.HoistFunction("float2 p, float2 dpx, float2 dpy", s_Calls.ToString());

                var s_Point = p_S.Declare("em_p", ShaderPortType.SptVec2, p_S.In("Coord"));
                var s_Dx = p_S.Declare("em_dpx", ShaderPortType.SptVec2, $"ddx({s_Point})");
                var s_Dy = p_S.Declare("em_dpy", ShaderPortType.SptVec2, $"ddy({s_Point})");
                p_S.Out("Inside", $"(min({s_Point}.x, {s_Point}.y) > 0.001) ? 1.0 : 0.0");
                p_S.Out("Out", $"{s_All}({s_Point}, {s_Dx}, {s_Dy})");
            },
        });

        // ⭐ THE POSITION IN THE WEAPON'S OWN SPACE (the emblem anywhere on the weapon, as BF4's weapons carry a second, unique
        // unwrap): the vertex shader of an emblem clone hands the mesh-space position of each vertex over in components the game's
        // writes 0 (DxbcMeshPosition: the .w of the world position and of the first two tangent rows); the studio's preview feeds the
        // same components the same way. On a shader whose vertex stage was not patched it reads 0.
        Add(new NodeDef
        {
            Kind = "MeshPosition",
            Title = "Mesh Position",
            Category = "Inputs",
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec3 } },
            Description = "The point's position in the weapon mesh's own space (the space its dump and the emblem slot's anchors are " +
                          "in), handed over by an emblem clone's patched vertex shader in the .w of the world position and the first " +
                          "two tangent rows. Zero where the vertex stage was not patched.",
            Emit = (p_S, _) =>
            {
                bool Has(string p_Field) =>
                    p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == p_Field);

                p_S.Out("Out", Has("WorldPos") && Has("TangentRow0") && Has("TangentRow1")
                    ? "float3(i.WorldPos.w, i.TangentRow0.w, i.TangentRow1.w)"
                    : "float3(0.0, 0.0, 0.0)");
            },
        });

        // The emblem's square PROJECTED from the mesh position (so the sticker can be put anywhere): a slot is a
        // frame in the mesh's own space — its centre, and its two axes scaled so a point's coordinate across the square is
        // 0.5 + dot(p − centre, axis) — and a point takes the square's coordinate when it lies within the square and within reach of its
        // plane, on a surface facing along it. Nothing in it reads the texture's unwrap: a slot runs across the seams, the mirrored
        // halves and the tiles of the unwrap as it lies on the body. The frames are external constants (external_EmblemF0.., three per
        // square, EmblemSlot.FramesOf) that each weapon's own variation fills — one shader serves every weapon of a camo, each with its
        // own slot (it worked in game with the frames as literals: this is the next step). The first square that holds the
        // point wins; a square left zero holds none.
        Add(new NodeDef
        {
            Kind = "EmblemProjection",
            Title = "Emblem Projection",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "MeshPos", Type = ShaderPortType.SptVec3, Default = "float3(0.0, 0.0, 0.0)" },
            },
            Params =
            {
                new ParamDef { Name = "Buffer", Kind = ParamKind.Text, Default = "1" },
                new ParamDef { Name = "Base", Kind = ParamKind.Text, Default = "52" },
                new ParamDef { Name = "Squares", Kind = ParamKind.Text, Default = "4" },
                new ParamDef { Name = "Facing", Kind = ParamKind.Text, Default = "0.25" },
            },
            Outputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2 },
                new PortDef { Name = "Inside", Type = ShaderPortType.SptScalar },
            },
            Description = "The point of the emblem's square the surface shows here, from the mesh position (x right, y down, 0 = outside " +
                          "every slot) — the slots are frames in the mesh's own space, so they run across the unwrap's seams and mirrored " +
                          "halves. Up to Squares squares, three external constants each (external_EmblemF0.. from element Base of the block " +
                          "at Buffer: centre and reach, x axis and the normal's sign, y axis), filled per weapon by its variation. Inside " +
                          "is 1 where a square holds the point.",
            Emit = (p_S, p_N) =>
            {
                var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
                string F(double p_V) => p_V.ToString("0.0########", s_Culture);
                var s_Frames = EmblemFrameFields(p_N);
                var s_Facing = HlslNames.Number(p_N.GetParam("Facing"), 0.25);

                var s_Point = p_S.Declare("ep_p", ShaderPortType.SptVec3, p_S.In("MeshPos"));
                // The surface's normal in the mesh's space, from the derivatives of the position (outside every branch) — SIGNED, or a
                // slot on the left would draw on the right side too (it lies within reach through a thin receiver): the screen
                // derivatives' orientation is measured in world space against the interpolated normal (as TangentHandedness does), and
                // the mesh → world map's own orientation is the preview's mirror (its vertex shader writes −1 in TangentRow2.w; the
                // game's writes 0 there, a rotation: +1).
                bool Has(string p_Field) =>
                    p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == p_Field);
                var s_Orientation = Has("WorldPos") && Has("TangentRow2")
                    ? p_S.Declare("ep_o", ShaderPortType.SptScalar,
                        "sign(dot(cross(ddx(i.WorldPos.xyz), ddy(i.WorldPos.xyz)), float3(i.TangentRow0.z, i.TangentRow1.z, i.TangentRow2.z))) * " +
                        "(i.TangentRow2.w < 0.0 ? -1.0 : 1.0)")
                    : "1.0";
                var s_Normal = p_S.Declare("ep_n", ShaderPortType.SptVec3,
                    $"normalize(cross(ddx({s_Point}), ddy({s_Point})) + float3(1e-12, 0.0, 0.0)) * {s_Orientation}");
                var s_Coord = p_S.Declare("ep_c", ShaderPortType.SptVec2, "float2(0.0, 0.0)");
                for (var k = 0; k + EmblemSlot.FrameRegisters <= s_Frames.Count; k += EmblemSlot.FrameRegisters)
                {
                    var (s_Origin, s_AxisX, s_AxisY) = (s_Frames[k].Name, s_Frames[k + 1].Name, s_Frames[k + 2].Name);
                    var s_Local = p_S.Declare("ep_l", ShaderPortType.SptVec3, $"{s_Point} - {s_Origin}.xyz");
                    // the surface's outward normal at the square: with y down, cross(x axis, y axis) points into the body — the frame
                    // carries the sign that turns it out
                    var s_Plane = p_S.Declare("ep_pl", ShaderPortType.SptVec3,
                        $"normalize(cross({s_AxisX}.xyz, {s_AxisY}.xyz) + float3(1e-12, 0.0, 0.0)) * {s_AxisX}.w");
                    var s_Here = p_S.Declare("ep_s", ShaderPortType.SptVec2, $"float2(0.5 + dot({s_Local}, {s_AxisX}.xyz), 0.5 + dot({s_Local}, {s_AxisY}.xyz))");
                    var s_Holds = $"(all({s_Here} > 0.0) && all({s_Here} < 1.0) && abs(dot({s_Local}, {s_Plane})) < {s_Origin}.w && " +
                                  $"dot({s_Normal}, {s_Plane}) > {F(s_Facing)} && {s_Coord}.x <= 0.0)";
                    s_Coord = p_S.Declare("ep_c", ShaderPortType.SptVec2, $"{s_Holds} ? max({s_Here}, float2(0.002, 0.002)) : {s_Coord}");
                }

                p_S.Out("Coord", s_Coord);
                p_S.Out("Inside", $"({s_Coord}.x > 0.0) ? 1.0 : 0.0");
            },
        });

        Add(new NodeDef
        {
            Kind = "Rotator",
            Title = "Rotator",
            Category = "UV",
            Inputs =
            {
                new PortDef { Name = "Coord", Type = ShaderPortType.SptVec2, Default = "i.TexCoord.xy", DefaultIsExpression = true },
                new PortDef { Name = "Centre", Type = ShaderPortType.SptVec2, Default = "float2(0.5, 0.5)" },
                new PortDef { Name = "Angle", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec2 } },
            Emit = (p_S, _) =>
            {
                var s_Angle = p_S.Declare("rotAngle", ShaderPortType.SptScalar, p_S.In("Angle"));
                var s_Centre = p_S.Declare("rotCentre", ShaderPortType.SptVec2, p_S.In("Centre"));
                var s_Local = p_S.Declare("rotLocal", ShaderPortType.SptVec2,
                    $"{p_S.In("Coord")} - {s_Centre}");
                var s_Sin = p_S.Declare("rotSin", ShaderPortType.SptScalar, $"sin({s_Angle})");
                var s_Cos = p_S.Declare("rotCos", ShaderPortType.SptScalar, $"cos({s_Angle})");
                p_S.Out("Out",
                    $"float2({s_Local}.x * {s_Cos} - {s_Local}.y * {s_Sin}, " +
                    $"{s_Local}.x * {s_Sin} + {s_Local}.y * {s_Cos}) + {s_Centre}");
            },
        });
    }

    /// <summary>
    /// Writes the four render targets verbatim, with no packing of its own.
    ///
    /// Needed because StandardRoot imposes the measured GBuffer convention (normal*0.5+0.5, sqrt(albedo),
    /// the lighting-model byte), which is right for a graph authored by hand and wrong for one TRANSLATED from a
    /// compiled shader: that shader already did its own packing, and re-applying ours would change the result.
    /// It is also what a family with a different layout (terrain 2d) needs.
    /// </summary>
    private static void AddRawRoot()
    {
        Add(new NodeDef
        {
            Kind = "Terrain2dRoot",
            Title = "Terrain 2D Root",
            Category = "Output",
            Description = "The packing the level's distant-terrain (2d) shaders share, measured from their own " +
                          "bytecode: RT0 = (packed normal x, rooted albedo xy, 1), RT1 = (packed normal z, " +
                          "rooted specular, packed normal y, 1), RT2 passed through exactly as wired.",
            IsRoot = true,
            Inputs =
            {
                new PortDef { Name = "NormalPacked", Type = ShaderPortType.SptVec3, Default = "float3(0.5, 0.5, 1)" },
                new PortDef { Name = "AlbedoRooted", Type = ShaderPortType.SptVec2, Default = "float2(0.5, 0.5)" },
                new PortDef { Name = "Specular", Type = ShaderPortType.SptScalar, Default = "0.0" },
                new PortDef { Name = "Rt2", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" },
            },
            Emit = (p_S, _) =>
            {
                var s_Normal = p_S.Declare("t2dNormal", ShaderPortType.SptVec3, p_S.In("NormalPacked"));
                var s_Albedo = p_S.Declare("t2dAlbedo", ShaderPortType.SptVec2, p_S.In("AlbedoRooted"));
                p_S.Line($"o.Target0 = float4({s_Normal}.x, {s_Albedo}.x, {s_Albedo}.y, 1.0);");
                p_S.Line($"o.Target1 = float4({s_Normal}.z, sqrt({p_S.In("Specular")}), {s_Normal}.y, 1.0);");
                p_S.Line($"o.Target2 = {p_S.In("Rt2")};");
            },
        });

        Add(new NodeDef
        {
            Kind = "RawRoot",
            Title = "Raw Root (verbatim targets)",
            Category = "Output",
            Description = "Writes SV_Target0..3 exactly as given, applying no GBuffer packing. Use for graphs " +
                          "translated from a compiled shader, or for a target whose layout is not the standard one. " +
                          "Coverage, when wired, writes SV_Coverage (per-sample alpha-to-coverage, one bit per " +
                          "MSAA sample) — feed it from an AlphaCoverage node.",
            IsRoot = true,
            Inputs =
            {
                new PortDef { Name = "Target0", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" },
                new PortDef { Name = "Target1", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" },
                new PortDef { Name = "Target2", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" },
                new PortDef { Name = "Target3", Type = ShaderPortType.SptVec4, Default = "float4(0, 0, 0, 0)" },
                new PortDef { Name = "Coverage", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Emit = (p_S, _) =>
            {
                // Only the targets the contract's signature declares exist in PsOut; writing beyond them
                // would not compile. A forward solution has one, distant terrain three, the GBuffer four.
                var s_Declared = p_S.Contract.RenderTargets is >= 1 and <= 4 ? p_S.Contract.RenderTargets : 4;
                for (var s_T = 0; s_T < s_Declared; s_T++)
                    p_S.Line($"o.Target{s_T} = {p_S.In($"Target{s_T}")};");

                // Only when wired: an unconnected pin would write coverage 0 and discard every pixel. The
                // shell declares SV_Coverage in PsOut under the same condition. Rounded, not truncated:
                // the AlphaCoverage node feeds whole bit patterns (round is identity there), but a raw
                // 0..1 alpha wired straight in would TRUNCATE to zero and delete the object — rounding
                // turns that mistake into a plain 0.5 alpha test instead.
                if (p_S.HasExplicitValue("Coverage"))
                    p_S.Line($"o.Coverage = (uint) round(max(0.0, {p_S.In("Coverage")}));");
            },
        });

        Add(new NodeDef
        {
            Kind = "ForwardRoot",
            Title = "Forward Root (transparent)",
            Category = "Output",
            Description = "Single-target output for the forward (transparent) solutions: writes " +
                          "Target0 = (Color × Alpha, Alpha), the premultiplied-alpha convention the " +
                          "transparent pass blends with. The vanilla shaders multiply the per-vertex " +
                          "distance fade (WorldPos.w on this family) into Alpha — wire it the same way " +
                          "to keep the fade-out with distance.",
            IsRoot = true,
            Inputs =
            {
                new PortDef { Name = "Color", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "Alpha", Type = ShaderPortType.SptScalar, Default = "1.0" },
            },
            Emit = (p_S, _) =>
            {
                var s_Alpha = p_S.Declare("fwdAlpha", ShaderPortType.SptScalar, p_S.In("Alpha"));
                p_S.Line($"o.Target0 = float4(({p_S.In("Color")}) * {s_Alpha}, {s_Alpha});");

                // On a wider contract (previewing a forward graph against the 4-target default) the
                // remaining declared targets still need a defined value.
                var s_Declared = p_S.Contract.RenderTargets is >= 1 and <= 4 ? p_S.Contract.RenderTargets : 4;
                for (var s_T = 1; s_T < s_Declared; s_T++)
                    p_S.Line($"o.Target{s_T} = float4(0, 0, 0, 0);");
            },
        });
    }

    /// <summary>
    /// Keeps a parameter name usable as an HLSL identifier. The names come from the game's bytecode rather than
    /// from us, so a stray character would produce a shader that fails to compile with a message pointing at the
    /// generated file instead of at the shader it came from.
    /// </summary>
    internal static string SafeIdentifier(string p_Name)
    {
        var s_Builder = new System.Text.StringBuilder();
        foreach (var s_Char in p_Name)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) || s_Char == '_' ? s_Char : '_');

        var s_Result = s_Builder.ToString();
        if (s_Result.Length == 0 || char.IsDigit(s_Result[0]))
            s_Result = "_" + s_Result;

        return s_Result;
    }

    /// <summary>The name every emblem layer constant carries, without the compiler's external_ prefix: EmblemL0, EmblemL1…</summary>
    public const string EmblemConstantPrefix = "EmblemL";

    /// <summary>
    /// The external constants an EmblemLayers node reads, one per layer — external_EmblemL0, external_EmblemL1… at consecutive
    /// elements from its Base, in the block at its Buffer register. The emitter declares them from here and the bake adds them
    /// to the shader's constant table from here, so the two can never disagree on a name or a slot.
    /// </summary>
    public static List<(string Name, int Buffer, int Element)> EmblemFields(GraphNode p_Node)
    {
        var s_Layers = Math.Clamp((int) HlslNames.Number(p_Node.GetParam("Layers"), 40), 1, 48);
        var s_Buffer = int.TryParse(p_Node.GetParam("Buffer"), out var s_B) ? s_B : 1;
        var s_Base = int.TryParse(p_Node.GetParam("Base"), out var s_E) ? s_E : 12;
        return Enumerable.Range(0, s_Layers)
            .Select(i => ($"external_{EmblemConstantPrefix}{i}", s_Buffer, s_Base + i))
            .ToList();
    }

    /// <summary>
    /// The external constants an EmblemProjection node reads, three per square — external_EmblemF0, external_EmblemF1… at consecutive
    /// elements from its Base, in the block at its Buffer register (EmblemSlot.FramesOf fills them, the bake adds them to the shader's
    /// table from here and writes each weapon's into its variation).
    /// </summary>
    public static List<(string Name, int Buffer, int Element)> EmblemFrameFields(GraphNode p_Node)
    {
        var s_Squares = Math.Clamp((int) HlslNames.Number(p_Node.GetParam("Squares"), EmblemSlot.ProjectedSquares), 0, EmblemSlot.ProjectedSquares);
        var s_Buffer = int.TryParse(p_Node.GetParam("Buffer"), out var s_B) ? s_B : 1;
        var s_Base = int.TryParse(p_Node.GetParam("Base"), out var s_E) ? s_E : 52;
        return Enumerable.Range(0, s_Squares * EmblemSlot.FrameRegisters)
            .Select(i => ($"external_{EmblemSlot.FramePrefix}{i}", s_Buffer, s_Base + i))
            .ToList();
    }

    private static void AddBinary(string p_Kind, string p_Title, string p_Format)
    {
        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Math",
            Inputs =
            {
                new PortDef { Name = "Input1", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
                new PortDef { Name = "Input2", Type = ShaderPortType.SptVec4, Default = "float4(1,1,1,1)" },
            },
            Outputs = { new PortDef { Name = "Out", Type = ShaderPortType.SptVec4 } },
            Emit = (p_S, _) => p_S.Out("Out",
                string.Format(p_Format, $"({p_S.In("Input1")})", $"({p_S.In("Input2")})")),
        });
    }

    private static void AddUnary(string p_Kind, string p_Title, ShaderPortType p_Type, string p_Format)
    {
        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Math",
            Inputs = { new PortDef { Name = "Input", Type = p_Type, Default = p_Type == ShaderPortType.SptVec3 ? "float3(0,0,1)" : "float4(0,0,0,0)" } },
            Outputs = { new PortDef { Name = "Out", Type = p_Type } },
            Emit = (p_S, _) => p_S.Out("Out", string.Format(p_Format, $"({p_S.In("Input")})")),
        });
    }

    /// <summary>
    /// Opacity only means anything once the surface is alpha-tested or transparent, and that is a property of
    /// the shaderdb's SurfaceShaderInfo rather than of the pixel shader we generate.
    /// </summary>
    private static string? OpacityReason(GraphNode p_Node)
    {
        var s_Type = Enum.TryParse<SurfaceShaderType>(p_Node.GetParam("SurfaceShaderType"), out var s_Parsed)
            ? s_Parsed
            : SurfaceShaderType.SurfaceShaderType_Opaque;

        return s_Type == SurfaceShaderType.SurfaceShaderType_Opaque
            ? "ignored while SurfaceShaderType is Opaque"
            : null;
    }

    /// <summary>
    /// Every root writes the SAME four render targets; the only difference between the lighting models is the
    /// integer in RT2.w, which is what Dx11/DeferredOutdoorLight branches on when it reads the GBuffer. They
    /// exist as separate palette entries for discoverability, but share this one implementation so the packing
    /// cannot drift between them.
    /// </summary>
    private static void AddRootVariant(string p_Kind, string p_Title, ShaderLightingModel p_Model,
        string p_Description)
    {
        const string c_NotIdentified =
            "RT2.xyz is not read by the outdoor light pass; channel still unidentified";

        Add(new NodeDef
        {
            Kind = p_Kind,
            Title = p_Title,
            Category = "Output",
            Description = p_Description,
            IsRoot = true,
            Inputs =
            {
                new PortDef { Name = "Diffuse", Type = ShaderPortType.SptVec3, Default = "float3(0.5, 0.5, 0.5)" },
                new PortDef { Name = "Emissive", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 0)" },
                new PortDef { Name = "FresnelBias", Type = ShaderPortType.SptScalar, Default = "0.6" },
                new PortDef { Name = "FresnelExponent", Type = ShaderPortType.SptScalar, Default = "4.0" },
                new PortDef { Name = "Normal", Type = ShaderPortType.SptVec3, Default = "float3(0, 0, 1)" },
                new PortDef { Name = "Occlusion", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = _ => c_NotIdentified },
                new PortDef { Name = "Opacity", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = OpacityReason },
                // This pin is often called Roughness elsewhere, but the game treats RT0.w as SMOOTHNESS: the outdoor
                // light pass derives specExp = 2^(w*10+1) and envmap LOD = (1-w)*10 from it, so 1 is a mirror
                // and 0 is matte. Named for what it does, or every graph authored here would come out inverted.
                new PortDef { Name = "Smoothness", Type = ShaderPortType.SptScalar, Default = "0.5" },
                new PortDef { Name = "SkyOcclusion", Type = ShaderPortType.SptScalar, Default = "1.0",
                    UnavailableReason = _ => c_NotIdentified },
                new PortDef { Name = "Specular", Type = ShaderPortType.SptScalar, Default = "0.5" },
                // A per-pixel metal mask (the gold camo, meant to really look shiny gold): where it is over
                // 0.5 the pixel is written as the METALLIC model (RT2.w = 1/255 — the light pass tints its specular and its sky
                // reflection by the albedo), elsewhere as this root's own model. Unwired, nothing changes: the root's model as before.
                new PortDef { Name = "Metallic", Type = ShaderPortType.SptScalar, Default = "0.0" },
            },
            Params =
            {
                new ParamDef
                {
                    Name = "SurfaceShaderType", Kind = ParamKind.Enum,
                    Default = nameof(SurfaceShaderType.SurfaceShaderType_Opaque),
                    EnumType = typeof(SurfaceShaderType),
                },
                new ParamDef { Name = "DoubleSided", Kind = ParamKind.Bool, Default = "false" },
                new ParamDef { Name = "GeometryPassEnable", Kind = ParamKind.Bool, Default = "true" },

                // Which space the Normal pin is in. Tangent is the authored default (a normal map's value);
                // World means the pin already IS the world normal and the tangent transform plus normalize are
                // skipped - the no-normal-map shaders pack their interpolated vertex normal exactly like that.
                new ParamDef
                {
                    Name = "NormalSpace", Kind = ParamKind.Choice, Default = "Tangent",
                    Choices = { "Tangent", "World" },
                },

                // What RT2.xyz gets. The sun pass never reads those channels and their meaning is unidentified,
                // but the game's shaders write non-zero constants there (0,0,1/255 on the carrier props), so
                // faithful translation has to carry the value through verbatim.
                new ParamDef { Name = "Rt2Xyz", Kind = ParamKind.Text, Default = "0,0,0" },

                // TEXCOORD index of the interpolator holding the world position, for contracts whose fields are
                // the unnamed Interp0..N. Empty uses the rigid-mesh `WorldPos`. The translator fills this from
                // the shader's own view-vector subtract; hand-authored graphs never need to touch it.
                new ParamDef { Name = "WorldPosInterp", Kind = ParamKind.Text, Default = "" },

                // Set to a number to write RT1.w AS that value: no fresnel, no view vector, the Specular pin
                // ignored. This is how shaders with a fixed specular ship (sqrt(0.05) baked as 0.223607), and
                // skipping the view maths entirely is what lets them target contracts that never interpolate a
                // world position. Empty = the normal fresnel-scaled path.
                new ParamDef { Name = "PackedSpecular", Kind = ParamKind.Text, Default = "" },

                // Fresnel: RT1.w = sqrt(lerp(bias,1,fresnel) * Specular), the measured vanilla path.
                // Raw: RT1.w = sqrt(Specular) with no view maths at all - the pin carries the WHOLE authored
                // expression, which is how parameter-scaled speculars and the weapons' own fresnel variants
                // wear this frame.
                new ParamDef
                {
                    Name = "SpecularMode", Kind = ParamKind.Choice, Default = "Fresnel",
                    Choices = { "Fresnel", "Raw" },
                },
            },
            Emit = (p_S, p_N) => EmitGBufferPacking(p_S, p_N, p_Model),
        });
    }

    /// <summary>
    /// Packing measured on both sides: the vanilla OilDrumBarrel GBuffer pixel shader that writes it, and
    /// Dx11/DeferredOutdoorLight (from Systems/ShaderProgramDb) that reads it back.
    /// </summary>
    private static void EmitGBufferPacking(IEmitScope p_S, GraphNode p_Node, ShaderLightingModel p_Model)
    {
        // The authored alpha test: an OpaqueAlphaTest* surface with a wired Opacity discards below 0.5 —
        // the same clip the game's own alpha-tested shaders compile in. Gated on BOTH the type and the
        // wire (the UI greys the pin while the type is Opaque), so every existing graph emits unchanged.
        var s_SurfaceType = Enum.TryParse<SurfaceShaderType>(p_Node.GetParam("SurfaceShaderType"),
            out var s_ParsedType)
            ? s_ParsedType
            : SurfaceShaderType.SurfaceShaderType_Opaque;
        if (s_SurfaceType is SurfaceShaderType.SurfaceShaderType_OpaqueAlphaTest
                or SurfaceShaderType.SurfaceShaderType_OpaqueAlphaTestSimple &&
            p_S.HasExplicitValue("Opacity"))
            p_S.Line($"clip({p_S.In("Opacity")} - 0.5);");

        // World mode takes the pin AS the world normal - verbatim, not even normalized, because the shaders
        // that wear this variant pack their interpolated vertex normal with `mad o0.xyz, vN, 0.5, 0.5` and dot
        // the same raw vN into the fresnel. Adding a normalize here would be "more correct" and not equivalent.
        var s_WorldSpace = p_Node.GetParam("NormalSpace") == "World";
        var s_PinNormal = p_S.Declare(s_WorldSpace ? "wsIn" : "tsNormal", ShaderPortType.SptVec3, p_S.In("Normal"));

        // Whether a field of this MEANING exists in the detected contract - the families measured from the
        // preset vertex shaders differ exactly here: rows-and-UV layouts carry no world position, the
        // simple layouts carry the world NORMAL itself and no tangent frame at all.
        bool HasField(string p_Field) =>
            p_S.Contract.Interpolators.Any(p_I => p_S.Contract.FieldNameFor(p_I.Index) == p_Field);

        var s_WorldNormal = s_WorldSpace
            ? s_PinNormal
            : HasField("TangentRow0")
                ? p_S.Declare("wsNormal", ShaderPortType.SptVec3,
                    $"normalize(float3(dot({s_PinNormal}, i.TangentRow0.xyz), " +
                    $"dot({s_PinNormal}, i.TangentRow1.xyz), " +
                    $"dot({s_PinNormal}, i.TangentRow2.xyz)))")
                // No tangent frame in this family: the interpolated world normal IS the surface normal (a
                // tangent-space pin cannot be transformed without a frame, and these layouts never carry
                // normal maps in the game either).
                : p_S.Declare("wsNormal", ShaderPortType.SptVec3,
                    HasField("WorldNormal") ? "normalize(i.WorldNormal.xyz)" : "float3(0, 0, 1)");

        // ⛔ THE STORED INDEX IS FOR NAMELESS FAMILIES ONLY, AND IT IS AN INDEX INTO THE FLAVOUR THE GRAPH
        // WAS TRANSLATED FROM. A family that NAMES its world position wins over it: on the lightmapped
        // layout everything sits one slot up, so index 0 there is the atlas transform — and the fresnel came
        // out reading `cameraPos - i.LightMapUvTransform.xyz`, a view vector built from a UV matrix. Caught
        // by emitting the graph against that contract and reading the HLSL, not in game.
        string WorldPosField() =>
            HasField("WorldPos") || p_S.Contract == null
                ? "WorldPos"
                : int.TryParse(p_Node.GetParam("WorldPosInterp"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var s_Index)
                    ? p_S.Contract.FieldNameForGraphIndex(s_Index)
                    : "WorldPos";

        p_S.Line($"o.Target0.xyz = {s_WorldNormal} * 0.5 + 0.5;");
        p_S.Line($"o.Target0.w = {p_S.In("Smoothness")};");
        p_S.Line($"o.Target1.xyz = sqrt(max(0.0, {p_S.In("Diffuse")}));");

        // A packed specular skips the whole view path - no world position touched, which is also what lets
        // fixed-specular shaders match on contracts that never interpolate one.
        if (float.TryParse(p_Node.GetParam("PackedSpecular"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var s_PackedSpecular))
        {
            p_S.Line($"o.Target1.w = {Lit(s_PackedSpecular)};");
        }
        else if (p_Node.GetParam("SpecularMode") == "Raw")
        {
            // The pin already IS the finished specular expression; only the gbuffer's sqrt encode is applied.
            p_S.Line($"o.Target1.w = sqrt(max(0.0, {p_S.In("Specular")}));");
        }
        else if (!HasField(WorldPosField()))
        {
            // The fresnel path needs a world position this family never interpolates. The RAW encode is
            // what those families' own shaders do (measured: the vehicle body writes sqrt(spec) with no
            // view maths), so degrading to it is faithful, not a fallback.
            p_S.Line($"o.Target1.w = sqrt(max(0.0, {p_S.In("Specular")}));");
        }
        else
        {
            var s_View = p_S.Declare("viewDir", ShaderPortType.SptVec3,
                $"normalize(cameraPos - i.{WorldPosField()}.xyz)");
            var s_Fresnel = p_S.Declare("fresnel", ShaderPortType.SptScalar,
                $"pow(saturate(1.001 - dot({s_WorldNormal}, {s_View})), {p_S.In("FresnelExponent")})");

            // lerp(bias, 1, f) reproduces vanilla's 0.6 + 0.4*f exactly at bias 0.6.
            var s_SpecScale = p_S.Declare("specScale", ShaderPortType.SptScalar,
                $"lerp({p_S.In("FresnelBias")}, 1.0, {s_Fresnel})");

            p_S.Line($"o.Target1.w = sqrt(max(0.0, {s_SpecScale} * {p_S.In("Specular")}));");
        }

        // RT2.w selects the lighting model: the reader compares it against 1.5/255, 2.5/255 and 3.5/255.
        // xyz is whatever the Rt2Xyz param says - unidentified channels, carried verbatim.
        var s_Xyz = (p_Node.GetParam("Rt2Xyz") ?? "").Split(',');
        var s_Rt2 = new string[3];
        for (var i = 0; i < 3; i++)
            s_Rt2[i] = s_Xyz.Length > i && float.TryParse(s_Xyz[i].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var s_Value)
                ? Lit(s_Value)
                : "0";

        // A probe-lit variant carries the object's ambient: the SH rows dotted with the world normal,
        // RGBM-encoded into RT3 (the light pass decodes rgb*w*8, hence the 1/8 here) plus the occlusion
        // row's dot in RT2.x. Reproduced instruction-for-instruction from the game's own probe variant.
        // A user-driven Emissive keeps the classic path — vanilla probe variants carry no authored
        // emissive, so combining the two has no reference to be faithful to.
        // ⛔ THE FLAVOUR THE MAP'S OWN COPIES DRAW WITH OWES THE GBUFFER A LIGHTMAP TERM. A static model
        // group's instances are lit by a precomputed atlas, and the shader is what writes that light into
        // the channels the light pass reads: RT2.x is the sky visibility and RT3 the irradiance, RGBM. A
        // graph has no way to produce either, so an emitted variant that skips this ships an object with
        // the map's baked light MISSING — which is not "flat", it is wrong in the channels the engine
        // reads. Reproduced instruction-for-instruction from the flavour's own bytecode (measured
        // 2026-09-05); the atlas transform rides TC0 because it is per-instance.
        if (p_S.Contract is { NeedsLightmapTerm: true } s_LmContract && !p_S.HasExplicitValue("Emissive"))
        {
            string Lm(string p_Suffix) =>
                s_LmContract.EngineTextures.FirstOrDefault(
                    p_R => p_R.Name.Contains(p_Suffix, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

            var s_Chroma = Lm("IrradianceChroma");
            var s_Luma = Lm("IrradianceLuma");
            var s_Dir = Lm("Direction");
            var s_Sky = Lm("SkyVisibility");

            if (s_Chroma.Length > 0 && s_Luma.Length > 0 && s_Dir.Length > 0 && s_Sky.Length > 0)
            {
                // uv = transform(TC0) applied to the lightmap UV, plus the second UV set as atlas offset.
                var s_LmUv = p_S.Declare("lmUv", ShaderPortType.SptVec2,
                    "float2(dot(i.LightMapUvTransform.xy, i.LightMapUv.xy), " +
                    "dot(i.LightMapUvTransform.zw, i.LightMapUv.xy)) + i.TexCoord.zw");

                var s_LmC = p_S.Declare("lmChroma", ShaderPortType.SptVec4,
                    $"{s_Chroma}.Sample(sampler0, {s_LmUv})");
                var s_LmL = p_S.Declare("lmLuma", ShaderPortType.SptScalar,
                    $"{s_Luma}.Sample(sampler0, {s_LmUv}).x");
                var s_LmD = p_S.Declare("lmDir", ShaderPortType.SptVec4,
                    $"{s_Dir}.Sample(sampler0, {s_LmUv})");

                // RT2.x is the sky visibility and the rest is zero — this flavour writes no model selector.
                p_S.Line($"o.Target2 = float4({s_Sky}.Sample(sampler0, {s_LmUv}).x, 0, 0, 0);");

                var s_LmRgb = p_S.Declare("lmRgb", ShaderPortType.SptVec3,
                    $"{s_LmL} * float3({s_LmC}.y, 1.0 - {s_LmC}.y - {s_LmC}.x, {s_LmC}.x) * 16.0");
                // ⛔ HALF-LAMBERT: the normal enters the dot at HALF scale and the bias is added on top —
                // `mul r1.xyz, r1.xyzx, l(0.5,0.5,0.5,0)` sits right before the dp4, whose r1.w is 0.5.
                // Written as dot(N,dir)+0.5 the term reaches 1.5, and the RGBM encode below clamps and
                // quantises that into visible BANDS across the lit face (seen in game, fixed here).
                var s_LmNdl = p_S.Declare("lmNdl", ShaderPortType.SptScalar,
                    $"dot({s_WorldNormal}, {s_LmD}.zyx * 2.0 - 1.0) * 0.5 + 0.5");
                var s_LmLit = p_S.Declare("lmLit", ShaderPortType.SptVec3,
                    $"{s_LmRgb} * {s_LmNdl} / max({s_LmD}.w, 0.0001) * 0.125");

                // RGBM, the same encode the probe path uses (round_pi is ceil).
                var s_LmW = p_S.Declare("lmW", ShaderPortType.SptScalar,
                    $"ceil(min(max(max({s_LmLit}.r, {s_LmLit}.g), max({s_LmLit}.b, 0.000001)), 1.0) " +
                    "* 255.0) / 255.0");

                p_S.Line($"o.Target3 = float4({s_LmLit} / {s_LmW}, {s_LmW});");
                return;
            }
        }

        // RT2.w, the lighting model selector: the root's own, or -- the Metallic pin wired -- the metallic model where the mask is over
        // half (the reader's thresholds sit at 0.5/255 steps, so the written value is exactly 1/255 or the root's).
        var s_ModelOut = p_S.HasExplicitValue("Metallic")
            ? $"(({p_S.In("Metallic")}) > 0.5 ? {Lit((int) ShaderLightingModel.ShaderLightingModel_Metallic)} : {Lit((int) p_Model)}) / 255.0"
            : $"{Lit((int) p_Model)} / 255.0";

        var s_Probes = p_S.Contract?.Probes ?? ProbeTransport.None;
        if (s_Probes != ProbeTransport.None && !p_S.HasExplicitValue("Emissive"))
        {
            // By FIELD NAME, not by table index: the SH rows sit at TC0-3 in most probe twins but ride
            // BEHIND the vertex stream in the character one — the field name is the same in every table.
            string Sh(int p_Row, string p_CbName) => s_Probes == ProbeTransport.Interpolators
                ? "i." + new[] { "ProbeShR", "ProbeShG", "ProbeShB", "ProbeShO" }[p_Row]
                : p_CbName;

            var s_ProbeN = p_S.Declare("probeN", ShaderPortType.SptVec4,
                $"float4({s_WorldNormal}, 1.0)");
            p_S.Line($"o.Target2 = float4(dot({s_ProbeN}, {Sh(3, "lightProbeShO")}), " +
                     $"{s_Rt2[1]}, {s_Rt2[2]}, {s_ModelOut});");
            var s_ProbeRgb = p_S.Declare("probeRgb", ShaderPortType.SptVec3,
                $"max(float3(dot({s_ProbeN}, {Sh(0, "lightProbeShR")}), " +
                $"dot({s_ProbeN}, {Sh(1, "lightProbeShG")}), " +
                $"dot({s_ProbeN}, {Sh(2, "lightProbeShB")})), 0.0) * 0.125");
            var s_ProbeW = p_S.Declare("probeW", ShaderPortType.SptScalar,
                $"ceil(min(max(max({s_ProbeRgb}.r, {s_ProbeRgb}.g), max({s_ProbeRgb}.b, 0.000001)), 1.0) " +
                "* 255.0) / 255.0");
            p_S.Line($"o.Target3 = float4({s_ProbeRgb} / {s_ProbeW}, {s_ProbeW});");
            return;
        }

        p_S.Line($"o.Target2 = float4({s_Rt2[0]}, {s_Rt2[1]}, {s_Rt2[2]}, {s_ModelOut});");

        // RT3 is emissive, read back as RT3.xyz * RT3.w * 8. Unconnected it reproduces vanilla's
        // (0,0,0,1/255) exactly so the correctness harness stays byte-faithful.
        if (p_S.HasExplicitValue("Emissive"))
            p_S.Line($"o.Target3 = float4({p_S.In("Emissive")}, 0.125);");
        else
            p_S.Line("o.Target3 = float4(0, 0, 0, 1.0 / 255.0);");
    }

    private static void AddRoot()
    {
        // Pin names follow the vocabulary that survives in the shipped game's own enums and parameter names. The four models are separate entries only for
        // discoverability: they emit identical packing and differ solely in the RT2.w selector, so there is no
        // hidden per-model magic on the WRITE side — the difference is entirely in how the light pass reads it.
        // ShaderLightingModel_Translucent is deliberately absent: the deferred outdoor light pass has no branch
        // for it (translucency goes through the forward Default pass), and that contract is not measured yet.
        AddRootVariant("StandardRoot", "StandardRoot", ShaderLightingModel.ShaderLightingModel_Standard,
            "Deferred default. The light pass gives it WHITE specular of intensity RT1.w.");

        AddRootVariant("MetallicRoot", "MetallicRoot", ShaderLightingModel.ShaderLightingModel_Metallic,
            "Writes RT2.w = 1/255. The light pass TINTS the specular by the albedo (specular = RT1.rgb * RT1.w) " +
            "instead of using white, which is what makes a surface read as metal.");

        AddRootVariant("SkinRoot", "SkinRoot", ShaderLightingModel.ShaderLightingModel_Skin,
            "Writes RT2.w = 2/255. The light pass adds a wrap-lighting term: a cubic in N.L " +
            "(1.14989, -2.14564, 0.841609, 0.154141) times a GLOBAL subsurface colour from the light constants " +
            "-- that colour is not per-material, so no pin controls it.");

        AddRootVariant("DynamicEnvmapRoot", "DynamicEnvmapRoot",
            ShaderLightingModel.ShaderLightingModel_DynamicEnvmap,
            "Writes RT2.w = 3/255. The light pass samples the DYNAMIC envmap instead of the sky cubemap. " +
            "Note the dynamic envmap capture is not wired up in BF3 retail, so expect no reflection to appear.");
    }
}

public static class HlslNames
{
    /// <summary>Maps a texture register index to the variable name the target shader's binding table uses.</summary>
    public static string TextureVar(string p_Register) =>
        p_Register == "1" ? "texture_Texture" : $"texture_Texture{p_Register}";

    /// <summary>A node parameter as a number, or the default when it does not parse.</summary>
    public static double Number(string p_Text, double p_Default) =>
        double.TryParse(p_Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s_Value)
            ? s_Value
            : p_Default;
}
