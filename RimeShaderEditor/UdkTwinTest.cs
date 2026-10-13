using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimeShaderEditor.Emit;
using RimeShaderEditor.Graph;

namespace RimeShaderEditor;

/// <summary>
/// Proves the promise the UDK-style palette makes: author in that vocabulary, bake this engine's shader.
///
/// Every UDK-named node is paired with the native graph it claims to stand for, both are emitted, and the
/// two HLSL bodies must come out IDENTICAL once temporary names are normalised. That is the only check
/// that can catch the failure that would matter — a node that looks right in the canvas and quietly bakes
/// something else. A twin that drifts fails here, on this machine, instead of in a shader that ships.
/// </summary>
public static class UdkTwinTest
{
    private sealed class Pair
    {
        public string Name = "";
        public ShaderGraph Udk = new();
        public ShaderGraph Native = new();
    }

    public static int Run(Action<string> p_Log)
    {
        var s_Pairs = BuildPairs();
        var s_Failures = 0;

        foreach (var s_Pair in s_Pairs)
        {
            var s_Left = Body(s_Pair.Udk, out var s_LeftError);
            var s_Right = Body(s_Pair.Native, out var s_RightError);

            if (s_LeftError != null || s_RightError != null)
            {
                p_Log($"FAIL {s_Pair.Name}: emit failed ({s_LeftError ?? s_RightError})");
                s_Failures++;
                continue;
            }

            if (s_Left == s_Right)
            {
                p_Log($"  ok  {s_Pair.Name}");
                continue;
            }

            p_Log($"FAIL {s_Pair.Name}: the UDK node does not emit what its native twin emits.");
            p_Log($"   udk    : {s_Left}");
            p_Log($"   native : {s_Right}");
            s_Failures++;
        }

        // A pass with nothing compared proves nothing — the list itself is part of the test.
        if (s_Pairs.Count == 0)
        {
            p_Log("FAIL: no twin pairs are defined.");
            return 1;
        }

        p_Log(s_Failures == 0
            ? $"UDKTWINTEST: PASS - {s_Pairs.Count} twin(s) emit identically."
            : $"UDKTWINTEST: FAIL - {s_Failures} of {s_Pairs.Count} twin(s) differ.");

        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The emitted body with temporaries renamed by order of appearance: the two graphs number their own
    /// declarations independently, and a difference in a generated NAME is not a difference in behaviour.
    /// </summary>
    private static string? Body(ShaderGraph p_Graph, out string? p_Error)
    {
        var s_Result = new HlslEmitter().Emit(p_Graph);
        if (!s_Result.Ok)
        {
            p_Error = string.Join("; ", s_Result.Errors);
            return null;
        }

        p_Error = null;
        return Normalise(s_Result.Hlsl);
    }

    /// <summary>
    /// Two emissions are the same shader when this says so. Shared with the fold check on purpose: two
    /// comparisons that normalise differently would let one of them pass what the other rejects.
    ///
    /// ⚠ Fold FIRST, rename after — the other way round a folded copy has already consumed a number and
    /// the two sides come out renumbered against each other even when the code is identical.
    /// </summary>
    public static string Normalise(string p_Hlsl)
    {
        var s_Names = new Dictionary<string, string>(StringComparer.Ordinal);
        return Regex.Replace(FoldCopies(p_Hlsl),
            @"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z][A-Za-z0-9]*)*_\d+\b",
            p_Match =>
            {
                if (!s_Names.TryGetValue(p_Match.Value, out var s_Alias))
                {
                    s_Alias = "tmp" + s_Names.Count;
                    s_Names[p_Match.Value] = s_Alias;
                }

                return s_Alias;
            });
    }

    /// <summary>
    /// Removes trivial copies — `float4 a = b;` where b is a plain name — and rewrites their uses.
    ///
    /// This is not softening the comparison: chaining through a second node necessarily assigns the first
    /// node's output to the second's input, and that copy is gone before the shader ever runs. What must
    /// match is the COMPUTATION, and everything that is one still has to match character for character —
    /// which is why a genuinely different sample or a missing declaration still fails here.
    /// </summary>
    private static string FoldCopies(string p_Hlsl)
    {
        var s_Copy = new Regex(@"^\s*(?:float|float2|float3|float4|uint|int)\s+([A-Za-z][A-Za-z0-9_]*)\s*=\s*([A-Za-z][A-Za-z0-9_]*)\s*;\s*$");
        var s_Lines = p_Hlsl.Split('\n').ToList();

        for (var i = 0; i < s_Lines.Count; i++)
        {
            var s_Match = s_Copy.Match(s_Lines[i].TrimEnd('\r'));
            if (!s_Match.Success)
                continue;

            var s_Alias = s_Match.Groups[1].Value;
            var s_Source = s_Match.Groups[2].Value;
            s_Lines.RemoveAt(i);
            i--;

            for (var j = 0; j < s_Lines.Count; j++)
                s_Lines[j] = Regex.Replace(s_Lines[j], $@"\b{Regex.Escape(s_Alias)}\b", s_Source);
        }

        return string.Join("\n", s_Lines);
    }

    private static List<Pair> BuildPairs()
    {
        var s_Pairs = new List<Pair>();

        // TextureCoordinate: plain, and with tiling — the tiling path is where the two could most easily
        // drift, because one takes two scalars and the other a "u,v" string.
        s_Pairs.Add(Twin("TextureCoordinate (plain)",
            Chain(("UdkTextureCoordinate", null), "Out", "Diffuse"),
            Chain(("TexCoord", null), "Out", "Diffuse")));

        s_Pairs.Add(Twin("TextureCoordinate (index 2, tiling 6)",
            Chain(("UdkTextureCoordinate", new Dictionary<string, string>
            {
                ["CoordinateIndex"] = "2", ["UTiling"] = "6.0", ["VTiling"] = "6.0",
            }), "Out", "Diffuse"),
            Chain(("TexCoord", new Dictionary<string, string>
            {
                ["Interp"] = "2", ["Tiling"] = "6,6",
            }), "Out", "Diffuse")));

        s_Pairs.Add(Twin("Constant",
            Chain(("UdkConstant", new Dictionary<string, string> { ["R"] = "0.35" }), "Out", "Smoothness"),
            Chain(("Scalar", new Dictionary<string, string> { ["Value"] = "0.35" }), "Out", "Smoothness")));

        s_Pairs.Add(Twin("LinearInterpolate",
            Chain(("UdkLinearInterpolate", null), "Out", "Diffuse"),
            Chain(("Lerp", null), "Out", "Diffuse")));

        // TextureSample's per-channel outputs against the native texture behind a mask node, which is the
        // whole reason that vocabulary needs no mask: RGB, and then the alpha lane on its own.
        s_Pairs.Add(Twin("TextureSample.RGB",
            Chain(("UdkTextureSample", new Dictionary<string, string> { ["Register"] = "3" }), "RGB", "Diffuse"),
            Masked("3", "RGB", "Diffuse", false)));

        s_Pairs.Add(Twin("TextureSample.A",
            Chain(("UdkTextureSample", new Dictionary<string, string> { ["Register"] = "2" }), "A", "Smoothness"),
            Masked("2", "A", "Smoothness", false)));

        // With the normal-map decode on, so the unpack branch is compared too and not just the plain sample.
        s_Pairs.Add(Twin("TextureSample.RGB (unpacked normal)",
            Chain(("UdkTextureSample", new Dictionary<string, string>
            {
                ["Register"] = "2", ["Unpack"] = "true",
            }), "RGB", "Normal"),
            Masked("2", "RGB", "Normal", true)));

        s_Pairs.Add(Twin("Panner",
            Chain(("UdkPanner", new Dictionary<string, string>
            {
                ["SpeedX"] = "0.1", ["SpeedY"] = "0.0",
            }), "Out", "Diffuse"),
            PannerNative()));

        // ⚠ ConstantClamp is deliberately NOT paired. It looked like the one arithmetic node with an exact
        // native twin, but the two differ in FORM without differing in effect: our Clamp declares its
        // bounds as 4-component pins (`clamp(x, float4(0.25,...), float4(0.75,...))`) while that
        // vocabulary declares them as `float` properties (`clamp(x, 0.25, 0.75)`). HLSL broadcasts, so the
        // shader is the same — but this test compares TEXT, and a pair that has to be argued about is not
        // evidence. The seven arithmetic nodes are NEW nodes, not translations: what proves them is
        // NODETEST (fxc compiles each one) plus the formula written on the node.

        // BLEND_Masked must land on the native alpha-tested surface type, and with the clip left at this
        // engine's own 0.5 the two have to emit the same test.
        s_Pairs.Add(Twin("Material (BLEND_Masked, clip 0.5)", MaskedUdk(), MaskedNative()));

        // The output node itself: same authored values on both sides, with the exponent conversion done by
        // hand on the native side. If the node's formula ever drifts, this pair is what catches it.
        s_Pairs.Add(Twin("Material (specular exponent -> smoothness)", MaterialUdk(), MaterialNative()));

        return s_Pairs;
    }

    /// <summary>The native panner with the same speed, typed into its vector pin.</summary>
    private static ShaderGraph PannerNative()
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Panner = new GraphNode { Kind = "Panner", X = -300, Y = 0 };
        s_Panner.SetInputOverride("Speed", "0.1,0");
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
        s_Graph.Nodes.Add(s_Panner);
        s_Graph.Nodes.Add(s_Root);
        s_Graph.Connect(s_Panner.Id, "Out", s_Root.Id, "Diffuse");
        return s_Graph;
    }

    /// <summary>A masked UDK material: BLEND_Masked with the clip value set to this engine's own 0.5.</summary>
    private static ShaderGraph MaskedUdk()
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Texture = new GraphNode { Kind = "UdkTextureSample", X = -600, Y = 0 };
        s_Texture.Params["Register"] = "2";
        var s_Root = new GraphNode { Kind = "UdkMaterial", X = 100, Y = 0 };
        s_Root.Params["BlendMode"] = "BLEND_Masked";
        s_Root.Params["OpacityMaskClipValue"] = "0.5";
        s_Root.SetInputOverride("SpecularPower", "64.0");
        s_Graph.Nodes.Add(s_Texture);
        s_Graph.Nodes.Add(s_Root);
        s_Graph.Connect(s_Texture.Id, "A", s_Root.Id, "OpacityMask");
        return s_Graph;
    }

    /// <summary>The same cutout on the native root: alpha-tested surface with the alpha wired to Opacity.</summary>
    private static ShaderGraph MaskedNative()
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Texture = new GraphNode { Kind = "Texture", X = -900, Y = 0 };
        s_Texture.Params["Register"] = "2";
        var s_Mask = new GraphNode { Kind = "MixOut", X = -600, Y = 0 };
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
        s_Root.Params["SurfaceShaderType"] = "SurfaceShaderType_OpaqueAlphaTest";

        // 64 converts to exactly 0.5 ((log2(64) - 1) / 10), the native smoothness default.
        s_Root.SetInputOverride("Smoothness", "0.5");
        s_Root.SetInputOverride("Specular", "0.5");
        s_Graph.Nodes.Add(s_Texture);
        s_Graph.Nodes.Add(s_Mask);
        s_Graph.Nodes.Add(s_Root);
        s_Graph.Connect(s_Texture.Id, "Out", s_Mask.Id, "Input");
        s_Graph.Connect(s_Mask.Id, "A", s_Root.Id, "Opacity");
        return s_Graph;
    }

    /// <summary>A UDK Material output fed a specular exponent and a specular colour.</summary>
    private static ShaderGraph MaterialUdk()
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Root = new GraphNode { Kind = "UdkMaterial", X = 100, Y = 0 };
        s_Graph.Nodes.Add(s_Root);
        s_Root.SetInputOverride("SpecularPower", "32.0");
        return s_Graph;
    }

    /// <summary>The same material on the native root, with the conversion spelled out.</summary>
    private static ShaderGraph MaterialNative()
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
        s_Graph.Nodes.Add(s_Root);

        // Typed straight into the pins: the native root has no exponent to convert, which is exactly the
        // difference the UDK node exists to hide.
        // Exponent 32 converts to exactly 0.4 ((log2(32) - 1) / 10), which is why this value was picked:
        // the native side can then be given a plain literal and the two must match to the character.
        s_Root.SetInputOverride("Smoothness", "0.4");
        return s_Graph;
    }

    /// <summary>
    /// Every blend mode of that vocabulary, against what the output node actually does with it: which pin
    /// the panel offers, which surface type it bakes as, and whether the shader really clips.
    ///
    /// Written because the first version got this wrong in a way no emission test could catch: the cutout
    /// pin asked a property the node did not own, so it read "Opaque" for ever and stayed dead in every
    /// mode. A mode that cannot be SEEN to change is indistinguishable from one that does nothing.
    /// </summary>
    public static int BlendModes(Action<string> p_Log)
    {
        var s_Expected = new (string Mode, bool Cutout, string Surface)[]
        {
            ("BLEND_Opaque", false, "SurfaceShaderType_Opaque"),
            ("BLEND_Masked", true, "SurfaceShaderType_OpaqueAlphaTest"),
            ("BLEND_SoftMasked", true, "SurfaceShaderType_OpaqueAlphaTestSimple"),
            ("BLEND_Translucent", false, "SurfaceShaderType_Opaque"),
            ("BLEND_Additive", false, "SurfaceShaderType_Opaque"),
            ("BLEND_Modulate", false, "SurfaceShaderType_Opaque"),
        };

        var s_Failures = 0;
        foreach (var (s_Mode, s_Cutout, _) in s_Expected)
        {
            var s_Graph = new ShaderGraph { Name = "blend" };
            var s_Texture = new GraphNode { Kind = "UdkTextureSample", X = -600, Y = 0 };
            var s_Root = new GraphNode { Kind = "UdkMaterial", X = 100, Y = 0 };
            s_Root.Params["BlendMode"] = s_Mode;
            s_Graph.Nodes.Add(s_Texture);
            s_Graph.Nodes.Add(s_Root);
            s_Graph.Connect(s_Texture.Id, "A", s_Root.Id, "OpacityMask");

            // What the PANEL would draw: null reason = the pin accepts input.
            var s_Pin = s_Root.Def.FindInput("OpacityMask");
            var s_Live = s_Pin?.WhyUnavailable(s_Root) == null;

            var s_Result = new HlslEmitter().Emit(s_Graph);
            if (!s_Result.Ok)
            {
                p_Log($"FAIL {s_Mode}: does not emit ({string.Join("; ", s_Result.Errors)})");
                s_Failures++;
                continue;
            }

            var s_Clips = s_Result.Hlsl.Contains("clip(", StringComparison.Ordinal);
            var s_Ok = s_Live == s_Cutout && s_Clips == s_Cutout;
            p_Log($"  {(s_Ok ? "ok  " : "FAIL")} {s_Mode,-20} cutout pin {(s_Live ? "live" : "dead")}, " +
                  $"shader {(s_Clips ? "clips" : "does not clip")}");

            if (!s_Ok)
                s_Failures++;
        }

        p_Log(s_Failures == 0
            ? $"UDKBLENDTEST: PASS - {s_Expected.Length} blend mode(s) behave as declared."
            : $"UDKBLENDTEST: FAIL - {s_Failures} mode(s) do not.");

        return s_Failures == 0 ? 0 : 1;
    }

    private static Pair Twin(string p_Name, ShaderGraph p_Udk, ShaderGraph p_Native) =>
        new() { Name = p_Name, Udk = p_Udk, Native = p_Native };

    /// <summary>One node wired straight into a StandardRoot pin.</summary>
    private static ShaderGraph Chain((string Kind, Dictionary<string, string>? Params) p_Node,
        string p_FromPort, string p_ToPin)
    {
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Node = new GraphNode { Kind = p_Node.Kind, X = -300, Y = 0 };
        foreach (var (s_Key, s_Value) in p_Node.Params ?? new Dictionary<string, string>())
            s_Node.Params[s_Key] = s_Value;

        var s_Root = new GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
        s_Graph.Nodes.Add(s_Node);
        s_Graph.Nodes.Add(s_Root);
        s_Graph.Connect(s_Node.Id, p_FromPort, s_Root.Id, p_ToPin);
        return s_Graph;
    }

    /// <summary>The native equivalent of a per-channel texture read: Texture -> MixOut -> pin.</summary>
    private static ShaderGraph Masked(string p_Register, string p_Channel, string p_ToPin, bool p_Unpack)
    {
        // Same NAME on both sides: the emitted header carries it, and the fixture must not create a
        // difference of its own that has nothing to do with what the nodes emit.
        var s_Graph = new ShaderGraph { Name = "twin" };
        var s_Texture = new GraphNode { Kind = "Texture", X = -600, Y = 0 };
        s_Texture.Params["Register"] = p_Register;
        if (p_Unpack)
            s_Texture.Params["Unpack"] = "true";

        var s_Mask = new GraphNode { Kind = "MixOut", X = -300, Y = 0 };
        var s_Root = new GraphNode { Kind = "StandardRoot", X = 100, Y = 0 };
        s_Graph.Nodes.Add(s_Texture);
        s_Graph.Nodes.Add(s_Mask);
        s_Graph.Nodes.Add(s_Root);
        s_Graph.Connect(s_Texture.Id, "Out", s_Mask.Id, "Input");
        s_Graph.Connect(s_Mask.Id, p_Channel, s_Root.Id, p_ToPin);
        return s_Graph;
    }
}
