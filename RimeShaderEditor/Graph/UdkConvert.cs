using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RimeShaderEditor.Graph;

/// <summary>
/// Rewrites a graph into the UDK vocabulary: the nodes a shader translated out of the game comes back as are
/// this engine's own, so authoring in that other vocabulary means staring at a graph made of the wrong words.
///
/// ⛔ THE RULE THAT MAKES THIS SAFE: a node is only rewritten when the twin provably emits the SAME shader.
/// Everything the twin cannot carry — a UV half-selector that has no property over there, a panner whose
/// speed arrives on a WIRE where that catalog only has a number, a root pin this output node does not own —
/// LEAVES THE NODE ALONE and says why. Dropping a value quietly would change the user's shader while telling
/// them it was only a rename. The seam emits before and after and demands identical HLSL, which is what
/// turns "should be the same" into "is the same".
/// </summary>
public static class UdkConvert
{
    public sealed class Report
    {
        /// <summary>Nodes rewritten into the other vocabulary.</summary>
        public int Converted;

        /// <summary>Texture+mask pairs folded into a single sampling node.</summary>
        public int Folded;

        /// <summary>Nodes deliberately left as they were, each with a line in <see cref="Notes"/>.</summary>
        public int Left;

        public List<string> Notes { get; } = new();
    }

    public static Report Run(ShaderGraph p_Graph)
    {
        var s_Report = new Report();

        // Pairs first: the folded node IS this vocabulary's sampler, and renaming the halves first would
        // leave nothing to fold.
        var s_Fold = UdkCollapse.Collapse(p_Graph);
        s_Report.Folded = s_Fold.Folded;
        s_Report.Left += s_Fold.Skipped;
        s_Report.Notes.AddRange(s_Fold.Reasons);

        foreach (var s_Node in p_Graph.Nodes.ToList())
        {
            var s_Refusal = Rewrite(p_Graph, s_Node);
            if (s_Refusal == null)
            {
                s_Report.Converted++;
                continue;
            }

            if (s_Refusal.Length == 0)
                continue;

            s_Report.Left++;
            s_Report.Notes.Add(s_Refusal);
        }

        // Say what is left that vocabulary does not have. These keep working — the style only filters the
        // PALETTE — and the palette lists them under their own heading, so they can still be placed; saying
        // so beats letting someone discover a node they cannot find. (A leftover mask is the common case:
        // without a texture in front of it there is no pair to fold, and it is NOT the same node as that
        // catalog's mask, whose single output packs the ticked channels from x instead of publishing one pin
        // each. Others — Min, Saturate, Negate — have no counterpart because that catalog has no such node.)
        var s_Foreign = p_Graph.Nodes
            .Where(p_N => !Palette.IsUdkVocabulary(p_N.Def))
            .GroupBy(p_N => p_N.Def.Title)
            .Select(p_G => $"{p_G.Count()}x {p_G.Key}")
            .ToList();

        if (s_Foreign.Count > 0)
            s_Report.Notes.Add($"no counterpart in that vocabulary, kept as they are (the palette lists them " +
                               $"under \"{Palette.c_ForeignCategory}\"): {string.Join(", ", s_Foreign)}");

        return s_Report;
    }

    /// <summary>
    /// Null when the node was rewritten, an empty string when it has no twin to rewrite into (already in that
    /// vocabulary, or shared by both), and otherwise the reason it was left alone.
    /// </summary>
    private static string? Rewrite(ShaderGraph p_Graph, GraphNode p_Node) => p_Node.Kind switch
    {
        "Texture" => $"{Name(p_Node)} keeps its own node: nothing masks its output, and a sampler there " +
                     "publishes one output per channel rather than the whole sample",
        "TexCoord" => Coordinates(p_Graph, p_Node),
        "Lerp" => Interpolate(p_Graph, p_Node),
        "Scalar" => Constant(p_Node),
        "Color" => Constant4(p_Node),
        "Panner" => Panner(p_Graph, p_Node),
        "StandardRoot" => Material(p_Graph, p_Node),
        _ => "",
    };

    private static string? Coordinates(ShaderGraph p_Graph, GraphNode p_Node)
    {
        // The second UV set lives in the zw half of one interpolator here; over there a coordinate node only
        // ever reads xy, so converting one that reads zw would silently move it to the other UV set.
        if (p_Node.GetParam("Half") != "xy")
            return $"{Name(p_Node)} keeps its own node: it reads the zw half of its interpolator, which has " +
                   "no property on the other coordinate node";

        var (s_U, s_V) = SplitPair(p_Node.GetParam("Tiling"), 1.0f);
        var s_Index = p_Node.GetParam("Interp");

        p_Node.Params.Clear();
        p_Node.Params["CoordinateIndex"] = s_Index;
        p_Node.Params["UTiling"] = Num(s_U);
        p_Node.Params["VTiling"] = Num(s_V);

        Retarget(p_Graph, p_Node, "UdkTextureCoordinate", new Dictionary<string, string>());
        return null;
    }

    private static string? Interpolate(ShaderGraph p_Graph, GraphNode p_Node)
    {
        Retarget(p_Graph, p_Node, "UdkLinearInterpolate", new Dictionary<string, string>
        {
            ["Input1"] = "A",
            ["Input2"] = "B",
            ["Delta"] = "Alpha",
        });

        return null;
    }

    private static string? Constant(GraphNode p_Node)
    {
        var s_Value = p_Node.GetParam("Value");
        p_Node.Params.Clear();
        p_Node.Params["R"] = s_Value;
        p_Node.Kind = "UdkConstant";
        return null;
    }

    private static string? Constant4(GraphNode p_Node)
    {
        // The colour node's own emission fills a missing component with 1, so the split has to agree with it
        // or a three-component value would come out with a different alpha.
        var s_Parts = p_Node.GetParam("Value").Split(',');
        var s_Names = new[] { "R", "G", "B", "A" };

        var s_Values = new string[4];
        for (var i = 0; i < 4; i++)
            s_Values[i] = i < s_Parts.Length &&
                          float.TryParse(s_Parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V)
                ? Num(s_V)
                : "1";

        p_Node.Params.Clear();
        for (var i = 0; i < 4; i++)
            p_Node.Params[s_Names[i]] = s_Values[i];

        p_Node.Kind = "UdkConstant4Vector";
        return null;
    }

    private static string? Panner(ShaderGraph p_Graph, GraphNode p_Node)
    {
        // Speed is a PIN here and a pair of PROPERTIES there: a wired speed has nowhere to go.
        if (p_Graph.ConnectionInto(p_Node.Id, "Speed") != null)
            return $"{Name(p_Node)} keeps its own node: its speed arrives on a wire, and that catalog's " +
                   "panner takes speed as two numbers";

        var (s_X, s_Y) = SplitPair(p_Node.GetInputOverride("Speed") ?? "float2(0.1, 0)", 0.0f);

        p_Node.Params.Clear();
        p_Node.SetInputOverride("Speed", null);
        p_Node.Params["SpeedX"] = Num(s_X);
        p_Node.Params["SpeedY"] = Num(s_Y);

        Retarget(p_Graph, p_Node, "UdkPanner", new Dictionary<string, string> { ["Coord"] = "Coordinate" });
        return null;
    }

    /// <summary>
    /// The output node, which is the one that can actually LOSE something: that catalog's Material has no
    /// occlusion pins, takes a specular EXPONENT where this root takes smoothness, and takes a colour where
    /// this one takes a scalar. Each of those is checked before anything is touched.
    /// </summary>
    private static string? Material(ShaderGraph p_Graph, GraphNode p_Node)
    {
        var s_Blend = p_Node.GetParam("SurfaceShaderType") switch
        {
            "SurfaceShaderType_Opaque" => "BLEND_Opaque",
            "SurfaceShaderType_OpaqueAlphaTest" => "BLEND_Masked",
            "SurfaceShaderType_OpaqueAlphaTestSimple" => "BLEND_SoftMasked",
            _ => null,
        };

        if (s_Blend == null)
            return $"{Name(p_Node)} stays as it is: its surface type has no blend mode in that vocabulary";

        foreach (var s_Pin in new[] { "Occlusion", "SkyOcclusion" })
            if (Used(p_Graph, p_Node, s_Pin))
                return $"{Name(p_Node)} stays as it is: '{s_Pin}' is in use and that output node has no such pin";

        // Smoothness and specular are CONVERTED values (exponent, colour): with a constant the conversion is
        // done here and the folded number comes back identical, but a wire would need extra maths nodes —
        // which is a different graph, not a rename.
        foreach (var s_Pin in new[] { "Smoothness", "Specular" })
            if (p_Graph.ConnectionInto(p_Node.Id, s_Pin) != null)
                return $"{Name(p_Node)} stays as it is: '{s_Pin}' is driven by a wire, and over there it is " +
                       "a specular exponent/colour that would need conversion maths in between";

        var s_Smoothness = Scalar(p_Node, "Smoothness", 0.5f);
        var s_Specular = Scalar(p_Node, "Specular", 0.5f);

        // Inverse of the node's own smoothness = (log2(max(1, power)) - 1) / 10.
        var s_Power = (float) Math.Pow(2.0, s_Smoothness * 10.0 + 1.0);

        var s_Carried = new[] { "GeometryPassEnable", "NormalSpace", "Rt2Xyz", "WorldPosInterp" }
            .ToDictionary(p_P => p_P, p_Node.GetParam);
        var s_DoubleSided = p_Node.GetParam("DoubleSided");

        foreach (var s_Pin in new[] { "Smoothness", "Specular", "Occlusion", "SkyOcclusion" })
            p_Node.SetInputOverride(s_Pin, null);

        var s_Overrides = p_Node.Params
            .Where(p_P => p_P.Key.StartsWith("in:", StringComparison.Ordinal))
            .ToDictionary(p_P => p_P.Key, p_P => p_P.Value);

        p_Node.Params.Clear();
        foreach (var (s_Key, s_Value) in s_Overrides)
            p_Node.Params[s_Key] = s_Value;

        foreach (var (s_Key, s_Value) in s_Carried)
            p_Node.Params[s_Key] = s_Value;

        p_Node.Params["BlendMode"] = s_Blend;
        p_Node.Params["TwoSided"] = s_DoubleSided;

        // The packing cuts at 0.5, and the other node pre-shifts opacity by (0.5 - clip): the clip value that
        // leaves the cut exactly where it is now is 0.5.
        p_Node.Params["OpacityMaskClipValue"] = "0.5";
        p_Node.SetInputOverride("SpecularPower", Num(s_Power));
        // Pin overrides are VALUES, not expressions: "a,b,c", never "float3(a, b, c)".
        p_Node.SetInputOverride("SpecularColor", $"{Num(s_Specular)},{Num(s_Specular)},{Num(s_Specular)}");

        Retarget(p_Graph, p_Node, "UdkMaterial", new Dictionary<string, string>
        {
            ["Diffuse"] = "DiffuseColor",
            ["Emissive"] = "EmissiveColor",
            ["Opacity"] = "OpacityMask",
        });

        return null;
    }

    /// <summary>Whether a pin carries anything at all — a wire or a value the author typed in.</summary>
    private static bool Used(ShaderGraph p_Graph, GraphNode p_Node, string p_Pin) =>
        p_Graph.ConnectionInto(p_Node.Id, p_Pin) != null || p_Node.GetInputOverride(p_Pin) != null;

    private static float Scalar(GraphNode p_Node, string p_Pin, float p_Fallback)
    {
        var s_Text = p_Node.GetInputOverride(p_Pin) ??
                     p_Node.Def.FindInput(p_Pin)?.Default ?? "";

        return float.TryParse(s_Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Value)
            ? s_Value
            : p_Fallback;
    }

    /// <summary>
    /// Points the node at its twin: the wires keep their ends but wear the twin's port names, and a value
    /// typed into a renamed pin moves with it. A port missing from the map keeps its name.
    /// </summary>
    private static void Retarget(ShaderGraph p_Graph, GraphNode p_Node, string p_Kind,
        IReadOnlyDictionary<string, string> p_Inputs)
    {
        foreach (var s_Connection in p_Graph.Connections)
            if (s_Connection.ToNode == p_Node.Id && p_Inputs.TryGetValue(s_Connection.ToPort, out var s_Renamed))
                s_Connection.ToPort = s_Renamed;

        var s_Moved = new List<(string Port, string Value)>();
        foreach (var (s_From, s_To) in p_Inputs)
            if (p_Node.GetInputOverride(s_From) is { } s_Value)
                s_Moved.Add((s_To, s_Value));

        foreach (var (s_From, _) in p_Inputs)
            p_Node.SetInputOverride(s_From, null);

        foreach (var (s_Port, s_Value) in s_Moved)
            p_Node.SetInputOverride(s_Port, s_Value);

        p_Node.Kind = p_Kind;
    }

    /// <summary>Reads "u,v" or "float2(u, v)" — the two shapes a pair is written in around here.</summary>
    private static (float U, float V) SplitPair(string p_Text, float p_Fallback)
    {
        var s_Body = p_Text.Trim();
        var s_Open = s_Body.IndexOf('(');
        if (s_Open >= 0 && s_Body.EndsWith(")", StringComparison.Ordinal))
            s_Body = s_Body[(s_Open + 1)..^1];

        var s_Parts = s_Body.Split(',');
        var s_U = Parse(s_Parts.Length > 0 ? s_Parts[0] : "", p_Fallback);

        // "2" means both axes here, the way the tiling field has always been read.
        var s_V = s_Parts.Length > 1 ? Parse(s_Parts[1], p_Fallback) : s_U;
        return (s_U, s_V);

        static float Parse(string p_Value, float p_Default) =>
            float.TryParse(p_Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p_Parsed)
                ? p_Parsed
                : p_Default;
    }

    private static string Num(float p_Value) => p_Value.ToString("0.0######", CultureInfo.InvariantCulture);

    private static string Name(GraphNode p_Node) => $"'{p_Node.Def.Title} {p_Node.Id[..6]}'";
}
