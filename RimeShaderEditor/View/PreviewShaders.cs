namespace RimeShaderEditor.View;

/// <summary>
/// The fixed shaders around the authored one. The vertex shader reproduces the interpolator contract measured
/// on the vanilla BF3 mesh shader so the generated pixel shader runs unmodified; the resolve pass reproduces
/// the lighting maths measured in Dx11/DeferredOutdoorLight, but lit by a neutral studio rig rather than a
/// level's own sky — this is a working preview, not a prediction of the final in-game look.
/// </summary>
public static class PreviewShaders
{
    /// <summary>
    /// Builds a vertex shader whose TEXCOORDs carry what the TARGET SHADER actually treats them as.
    ///
    /// ⛔ The static rigid layout below fed EVERY shader worldpos/tangent-rows/uv - and a shader whose own
    /// bytecode proves TEXCOORD1 is its world NORMAL and TEXCOORD2 its packed UVs then previewed as a flat grey
    /// cube, while the differential test stayed green because BOTH sides ate the same wrong inputs. EQUIVALENT
    /// is not "looks right": the preview needs each interpolator fed per its USAGE, which the disassembly
    /// names (sample coordinate = uv, camera subtract = world position, packed to RT0 = world normal).
    /// A null/empty map emits the measured rigid layout, byte-for-byte the shader the guardians are pinned to.
    /// </summary>
    public static string VertexShaderFor(System.Collections.Generic.IReadOnlyDictionary<int, string>? p_Meanings)
    {
        if (p_Meanings == null || p_Meanings.Count == 0)
            return c_VertexShader;

        var s_Lines = new System.Text.StringBuilder();
        for (var i = 0; i <= 9; i++)
        {
            var s_Value = p_Meanings.TryGetValue(i, out var s_Meaning)
                ? s_Meaning switch
                {
                    "WorldPos" => "float4(s_World, 1.0)",
                    "Normal" => "float4(s_N, 1.0)",
                    // A shader classified UvPair reads BOTH halves, so both have to be real: the second one
                    // used to be fed `i.TexCoord * 2.0`, an invented set that costs nothing while every
                    // subject is a weapon (one UV) and lands the whole detail of a vehicle body elsewhere.
                    //
                    // ⛔ WHICH SET GOES IN WHICH HALF IS NOT DECIDED HERE, and that is the point: ONE vertex
                    // shader serves the target AND every foreign section of the same object, and they do not
                    // agree — measured by disassembling the game's own vertex shaders for the LAV-25's
                    // declaration: `vehiclepreset_mud` does `mov o8.xyzw, v3.zwxy` (it SWAPS the pair, so its
                    // .xy is the second set) while the lights, the slat cage and the kits do
                    // `mov o.xy, v3.xyxx` (no swap). Feeding the swap from here fixed the hull and broke
                    // those three. The swap therefore travels in the DATA, per section, at dump time.
                    "UvPair" => "float4(i.TexCoord, i.TexCoord1)",

                    // A SECOND uv interpolator, where the shader reads one: the jets' decal preset samples
                    // its normal map with the set its vertex shader hands over on its own (set 0 of three,
                    // measured 2026-09-22). The dump writes that set in the third slot, so the order it
                    // travels in is the shader's own, exactly as the pair's.
                    "UvExtra" => "float4(i.TexCoord2, 0.0, 0.0)",
                    "Uv" => "float4(i.TexCoord, i.TexCoord * 2.0)",

                    // The tangent-frame component rows, exactly as the rigid default feeds them.
                    "TangentRow0" => "float4(s_T.x, s_B.x, s_N.x, 0.75)",
                    "TangentRow1" => "float4(s_T.y, s_B.y, s_N.y, 0.60)",
                    "TangentRow2" => "float4(s_T.z, s_B.z, s_N.z, 0.85)",

                    // The light-probe SH rows for the interpolator-transport twins. Two properties are
                    // load-bearing: |xyz| stays well under w, so dp4((N,1), row) is positive for every
                    // normal AND still depends on it (a clamped-to-zero term deadens a differential test
                    // on both sides); and the rows are STRONGLY distinct — each leans on a different axis
                    // with well-separated w — so a swapped or misindexed row moves the output by far more
                    // than the test's 1-LSB resolution. The occlusion row's dp4 stays inside (0,1): it is
                    // written raw into an 8-bit target, and a saturated channel is a dead channel.
                    "ProbeShR" => "float4(0.20, 0.03, 0.05, 0.42)",
                    "ProbeShG" => "float4(0.04, 0.22, 0.03, 0.58)",
                    "ProbeShB" => "float4(0.05, 0.02, 0.24, 0.74)",
                    "ProbeShO" => "float4(0.12, 0.10, 0.08, 0.55)",
                    // ⛔ NOT a constant, and not 1.0. Plain white sent nine previously-verified shaders mute:
                    // the emitters compute saturate((texture - v.y) * v.x), and v.y = 1 kills every pixel -
                    // a constant is always somebody's lethal value, the same law as the zero and the saturated
                    // placeholder. Mid-range and spatially varying keeps gates, fades and vertex colours all
                    // alive and keeps the differential test able to see.
                    "Neutral" => "float4(0.75, 0.55 + 0.25 * i.TexCoord.y, " +
                                 "0.65 + 0.25 * i.TexCoord.x, 0.45 + 0.35 * i.TexCoord.y)",
                    _ => DefaultInterpolator(i),
                }
                : DefaultInterpolator(i);

            s_Lines.AppendLine($"    o.T{i} = {s_Value};");
        }

        return c_VertexShaderTemplate.Replace("__ASSIGN__", s_Lines.ToString());
    }

    private static string DefaultInterpolator(int p_Index) => p_Index switch
    {
        0 => "float4(s_World, 1.0)",
        1 => "float4(s_T.x, s_B.x, s_N.x, 0.75)",
        2 => "float4(s_T.y, s_B.y, s_N.y, 0.60)",
        3 => "float4(s_T.z, s_B.z, s_N.z, 0.85)",
        4 => "float4(i.TexCoord, i.TexCoord * 2.0)",
        5 => "float4(i.TexCoord * 0.5, 0.35, 1.0)",
        6 => "float4(0.45, 0.55, 0.65, 1.0)",
        _ => "float4(s_World * 0.05, 1.0)",
    };

    // The meanings template reaches TEXCOORD9: the largest measured layout (the character probe twin)
    // interpolates ten rows, and a pixel shader reading a row the host never writes reads garbage.

    /// <summary>
    /// The stand-in for mesh sections worn by OTHER shaders: flat neutral grey into the standard GBuffer
    /// (an up-facing normal and vanilla RT2/RT3 defaults), so the object keeps its silhouette without
    /// pretending to be lit correctly. Constant on purpose — reading any interpolator would read a
    /// DIFFERENT quantity per contract family.
    /// </summary>
    public const string c_NeutralGBufferPixelShader = @"
struct PsOut
{
    float4 Target0 : SV_Target0;
    float4 Target1 : SV_Target1;
    float4 Target2 : SV_Target2;
    float4 Target3 : SV_Target3;
};

PsOut main(float4 p_Position : SV_Position)
{
    PsOut o;
    o.Target0 = float4(0.5, 0.5, 1.0, 0.12);
    o.Target1 = float4(0.55, 0.55, 0.55, 0.10);
    o.Target2 = float4(0, 0, 0, 0);
    o.Target3 = float4(0, 0, 0, 1.0 / 255.0);
    return o;
}";

    /// <summary>The forward-pass twin of the neutral stand-in: one opaque grey target.</summary>
    public const string c_NeutralForwardPixelShader = @"
float4 main(float4 p_Position : SV_Position) : SV_Target0
{
    return float4(0.35, 0.35, 0.37, 1.0);
}";

    /// <summary>
    /// The gizmo overlay: coloured lines in the same space the mesh is drawn in, through the same
    /// world/view-projection block, composited over the finished frame. Colour is premultiplied by the
    /// vertex shader so the forward blend state (One, InvSrcAlpha) draws it right.
    /// </summary>
    public const string c_OverlayVertexShader = @"
cbuffer PreviewVS : register(b0)
{
    float4x4 g_world;
    float4x4 g_viewProj;
};

struct VsIn
{
    float3 Position : POSITION;
    float4 Colour   : COLOR;
};

struct VsOut
{
    float4 Position : SV_Position;
    float4 Colour   : COLOR;
};

VsOut main(VsIn i)
{
    VsOut o;
    float3 s_World = mul(float4(i.Position, 1.0), g_world).xyz;
    o.Position = mul(float4(s_World, 1.0), g_viewProj);
    o.Colour = float4(i.Colour.rgb * i.Colour.a, i.Colour.a);
    return o;
}";

    public const string c_OverlayPixelShader = @"
float4 main(float4 p_Position : SV_Position, float4 p_Colour : COLOR) : SV_Target
{
    return p_Colour;
}";

    /// <summary>A picture laid over the view (a vehicle's emblem slot shows the sample emblem): the mesh's space, a texture coordinate.</summary>
    public const string c_PictureVertexShader = @"
cbuffer PreviewVS : register(b0)
{
    float4x4 g_world;
    float4x4 g_viewProj;
};

struct VsIn
{
    float3 Position : POSITION;
    float2 Uv       : TEXCOORD;
};

struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv       : TEXCOORD;
};

VsOut main(VsIn i)
{
    VsOut o;
    float3 s_World = mul(float4(i.Position, 1.0), g_world).xyz;
    o.Position = mul(float4(s_World, 1.0), g_viewProj);
    o.Uv = i.Uv;
    return o;
}";

    /// <summary>The picture as it is, premultiplied (the overlay's blend).</summary>
    public const string c_PicturePixelShader = @"
Texture2D g_picture : register(t0);
SamplerState g_sampler : register(s0);

float4 main(float4 p_Position : SV_Position, float2 p_Uv : TEXCOORD) : SV_Target
{
    // the picture is projected onto the body's own triangles, which run past the square: outside it, nothing
    if (any(p_Uv < 0.0) || any(p_Uv > 1.0))
        discard;
    return g_picture.Sample(g_sampler, p_Uv);
}";

    private const string c_VertexShaderTemplate = @"
cbuffer PreviewVS : register(b0)
{
    float4x4 g_world;
    float4x4 g_viewProj;
};

struct VsIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float3 Tangent  : TANGENT;
    float  Handedness : TANGENT1;
    float2 TexCoord : TEXCOORD;

    /// The SECOND texture coordinate set. A mesh that has one carries it; a mesh that does not repeats the
    /// first, so nothing that only ever read TexCoord changes.
    float2 TexCoord1 : TEXCOORD1;

    /// The THIRD slot of the dump — the set a second uv interpolator carries (the jets' declaration adds a
    /// TexCoord2 after the bone indices). A dump without one repeats the first, as the second does.
    float2 TexCoord2 : TEXCOORD2;
};

struct VsOut
{
    float4 Position : SV_Position;
    float4 T0 : TEXCOORD0;
    float4 T1 : TEXCOORD1;
    float4 T2 : TEXCOORD2;
    float4 T3 : TEXCOORD3;
    float4 T4 : TEXCOORD4;
    float4 T5 : TEXCOORD5;
    float4 T6 : TEXCOORD6;
    float4 T7 : TEXCOORD7;
    float4 T8 : TEXCOORD8;
    float4 T9 : TEXCOORD9;
};

VsOut main(VsIn i)
{
    VsOut o;

    float3 s_World = mul(float4(i.Position, 1.0), g_world).xyz;
    o.Position = mul(float4(s_World, 1.0), g_viewProj);

    float3 s_N = normalize(mul(float4(i.Normal,  0.0), g_world).xyz);
    float3 s_T = normalize(mul(float4(i.Tangent, 0.0), g_world).xyz);
    // The frame keeps its handedness under a mirrored world (the game's meshes are right-handed and the
    // preview draws them through a mirror): a reflection flips a cross product, the sign puts it back.
    // ... and a mirrored island of the unwrap flips it, as the game's per-vertex BinormalSign does (0 = +1).
    float3 s_B = normalize(cross(s_N, s_T)) * sign(determinant((float3x3) g_world)) * (i.Handedness < 0.0 ? -1.0 : 1.0);

__ASSIGN__
    return o;
}";

    /// <summary>
    /// TEXCOORD0 = world position, TEXCOORD1..3 = the tangent frame, TEXCOORD4 = uv.
    ///
    /// The three frame rows are the COLUMNS of [T B N], not its rows: the pixel shader reconstructs the world
    /// normal as dot(n, row0/row1/row2), and n.x*T + n.y*B + n.z*N gives worldNormal.x = dot(n, (T.x,B.x,N.x)).
    /// Transposing this by mistake flips normal maps in a way that looks like a bad texture.
    /// </summary>
    public const string c_VertexShader = @"
cbuffer PreviewVS : register(b0)
{
    float4x4 g_world;
    float4x4 g_viewProj;
    // the mesh position's way back to the dump's own units: raw = position * w + xyz; w = 0 = off (ShaderPreview.MeshPositionInW)
    float4 g_meshRaw;
};

struct VsIn
{
    float3 Position : POSITION;
    float3 Normal   : NORMAL;
    float3 Tangent  : TANGENT;
    float  Handedness : TANGENT1;
    float2 TexCoord : TEXCOORD;

    /// The SECOND texture coordinate set. A mesh that has one carries it; a mesh that does not repeats the
    /// first, so nothing that only ever read TexCoord changes.
    float2 TexCoord1 : TEXCOORD1;

    /// The THIRD slot of the dump — the set a second uv interpolator carries (the jets' declaration adds a
    /// TexCoord2 after the bone indices). A dump without one repeats the first, as the second does.
    float2 TexCoord2 : TEXCOORD2;
};

struct VsOut
{
    float4 Position    : SV_Position;
    float4 WorldPos    : TEXCOORD0;
    float4 TangentRow0 : TEXCOORD1;
    float4 TangentRow1 : TEXCOORD2;
    float4 TangentRow2 : TEXCOORD3;
    float4 TexCoord    : TEXCOORD4;
    float4 Extra5      : TEXCOORD5;
    float4 Extra6      : TEXCOORD6;
    float4 Extra7      : TEXCOORD7;
};

VsOut main(VsIn i)
{
    VsOut o;

    float3 s_World = mul(float4(i.Position, 1.0), g_world).xyz;
    o.Position = mul(float4(s_World, 1.0), g_viewProj);
    // the mesh-space position in three .w, as an emblem clone's patched vertex shader hands it over in the game (off: the old values)
    float3 s_Raw = i.Position * g_meshRaw.w + g_meshRaw.xyz;
    bool s_MeshPos = g_meshRaw.w > 0.0;
    o.WorldPos = float4(s_World, s_MeshPos ? s_Raw.x : 1.0);

    float3 s_N = normalize(mul(float4(i.Normal,  0.0), g_world).xyz);
    float3 s_T = normalize(mul(float4(i.Tangent, 0.0), g_world).xyz);
    // The frame keeps its handedness under a mirrored world (the game's meshes are right-handed and the
    // preview draws them through a mirror): a reflection flips a cross product, the sign puts it back.
    // ... and a mirrored island of the unwrap flips it, as the game's per-vertex BinormalSign does (0 = +1).
    float3 s_B = normalize(cross(s_N, s_T)) * sign(determinant((float3x3) g_world)) * (i.Handedness < 0.0 ? -1.0 : 1.0);

    // ⛔ The fourth component is NOT spare padding. Left at 0 it silently degenerates every shader that reads it
    // - a particle fade, a vertex alpha, a blend weight - into a multiply by zero, which renders as a blank
    // object and makes the differential test vacuous rather than failed. Filled with something distinctive
    // and spatially varying, those shaders produce a picture that can actually be compared.
    o.TangentRow0 = float4(s_T.x, s_B.x, s_N.x, s_MeshPos ? s_Raw.y : 0.75);
    o.TangentRow1 = float4(s_T.y, s_B.y, s_N.y, s_MeshPos ? s_Raw.z : 0.60);
    // (with the mesh position: the orientation of mesh -> drawn, −1 through the preview's lateral mirror; the game's writes 0 = +1)
    o.TangentRow2 = float4(s_T.z, s_B.z, s_N.z, s_MeshPos ? sign(determinant((float3x3) g_world)) : 0.85);

    // zw is a SECOND UV SET, which is what the game puts there - the Barrack packs two of them in TEXCOORD4 and
    // tiles them differently.
    o.TexCoord = float4(i.TexCoord, i.TexCoord * 2.0);

    // Interpolators the rig used to stop short of. A pixel shader whose input signature asks for TEXCOORD5 and
    // does not get it fails the DRAW, not the shader creation - so it wrote nothing and looked like a shader
    // that simply outputs nothing. Lightmapped meshes and the parachute's submaterial index live up here.
    o.Extra5 = float4(i.TexCoord * 0.5, 0.35, 1.0);
    o.Extra6 = float4(0.45, 0.55, 0.65, 1.0);
    o.Extra7 = float4(s_World * 0.05, 1.0);
    return o;
}";

    /// <summary>Fullscreen triangle for the resolve pass; no vertex buffer needed.</summary>
    public const string c_ResolveVertexShader = @"
struct VsOut
{
    float4 Position : SV_Position;
    float2 Uv       : TEXCOORD0;
};

VsOut main(uint p_Id : SV_VertexID)
{
    VsOut o;
    float2 s_Corner = float2((p_Id << 1) & 2, p_Id & 2);
    o.Uv = s_Corner;
    o.Position = float4(s_Corner * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}";

    /// <summary>
    /// Consumes the four targets the authored shader wrote, the same way the game's outdoor light pass does:
    ///   RT0.xyz normal (*2-1)   RT0.w smoothness -> specExp = 2^(w*10+1), envmap LOD = (1-w)*10
    ///   RT1     squared on read -> albedo and specular intensity
    ///   RT2.w   lighting model as an integer in 1/255 units
    ///   RT3     emissive, rgb * a * 8
    /// The sky is procedural rather than the level's cubemap, which is why this is approximate by design.
    /// </summary>
    public const string c_ResolvePixelShader = @"
cbuffer PreviewLight : register(b0)
{
    float4x4 g_invViewProj;
    float3 g_lightDir;      float g_pad0;
    float3 g_keyColor;      float g_pad1;
    float3 g_topColor;      float g_pad2;
    float3 g_bottomColor;   float g_pad3;
    float3 g_cameraPos;     float g_exposure;
};

Texture2D    g_gbuffer0 : register(t0);
Texture2D    g_gbuffer1 : register(t1);
Texture2D    g_gbuffer2 : register(t2);
Texture2D    g_gbuffer3 : register(t3);
Texture2D    g_depth    : register(t4);
SamplerState g_point    : register(s0);

float3 Sky(float3 p_Dir, float p_Blur)
{
    // Stand-in for the level's sky cubemap: a hemisphere gradient plus a soft sun, blurred by roughness so
    // smoothness still visibly sharpens or dulls the reflection.
    float s_Up = saturate(p_Dir.y * 0.5 + 0.5);
    float3 s_Base = lerp(g_bottomColor, g_topColor, s_Up);
    float s_Sun = pow(saturate(dot(p_Dir, g_lightDir)), lerp(256.0, 2.0, saturate(p_Blur)));
    return s_Base + g_keyColor * s_Sun * (1.0 - saturate(p_Blur)) * 2.0;
}

float4 main(float4 p_Position : SV_Position, float2 p_Uv : TEXCOORD0) : SV_Target
{
    float4 s_Rt0 = g_gbuffer0.Sample(g_point, p_Uv);
    float4 s_Rt1 = g_gbuffer1.Sample(g_point, p_Uv);
    float4 s_Rt2 = g_gbuffer2.Sample(g_point, p_Uv);
    float4 s_Rt3 = g_gbuffer3.Sample(g_point, p_Uv);

    // Depth at the far plane means the cube was not drawn here. Reconstructing the world position from depth
    // is what the game does too (its light pass carries g_invViewProjMatrix), and it avoids asking the
    // authored shader for a fifth target it does not declare.
    float s_Depth = g_depth.Sample(g_point, p_Uv).r;
    if (s_Depth >= 1.0)
        return float4(lerp(g_bottomColor, g_topColor, saturate(1.0 - p_Uv.y)) * 0.35, 1.0);

    float4 s_Clip = float4(p_Uv.x * 2.0 - 1.0, 1.0 - p_Uv.y * 2.0, s_Depth, 1.0);
    float4 s_World = mul(s_Clip, g_invViewProj);
    s_World /= s_World.w;

    float3 s_Normal = normalize(s_Rt0.xyz * 2.0 - 1.0);
    float s_Smoothness = s_Rt0.w;

    // The game squares the whole of RT1 on read, so the stored sqrt round-trips.
    float4 s_Squared = s_Rt1 * s_Rt1;
    float3 s_Albedo = s_Squared.xyz;
    float s_SpecIntensity = s_Squared.w;

    float s_SpecExp = exp2(s_Smoothness * 10.0 + 1.0);
    float3 s_View = normalize(g_cameraPos - s_World.xyz);
    float3 s_Half = normalize(s_View + g_lightDir);

    float s_NdotL = dot(s_Normal, g_lightDir);
    float s_Lambert = saturate(s_NdotL);
    float s_Spec = pow(saturate(dot(s_Normal, s_Half)), s_SpecExp) * s_Lambert
                 * (s_SpecExp + 8.0) * 0.125;

    // Hemisphere ambient, as the light pass builds it from the top and bottom colours.
    float s_Hemi = dot(s_Normal, float3(0.0, 1.0, 0.0)) * 0.5 + 0.5;
    float3 s_Ambient = lerp(g_bottomColor, g_topColor, s_Hemi);
    float3 s_Diffuse = s_Ambient + g_keyColor * s_Lambert;

    // RT2.w picks the model: 1/255 metallic tints the specular by albedo, 2/255 skin adds a wrap term.
    int s_Model = (int) round(s_Rt2.w * 255.0);
    float3 s_SpecColor = s_Model == 1 ? s_Albedo : float3(1.0, 1.0, 1.0);

    if (s_Model == 2)
    {
        float s_Wrap = saturate(s_NdotL * (s_NdotL * (s_NdotL * 1.14989 - 2.14564) + 0.841609) + 0.154141);
        s_Diffuse += s_Wrap * g_keyColor * 0.6;
    }

    float3 s_Reflected = reflect(-s_View, s_Normal);
    float3 s_Envmap = Sky(s_Reflected, 1.0 - s_Smoothness);

    float3 s_Colour = s_Albedo * s_Diffuse
                    + s_SpecColor * s_SpecIntensity * (s_Spec * g_keyColor + s_Envmap * 0.25)
                    + s_Rt3.xyz * s_Rt3.w * 8.0;

    // Plain gamma so the preview is not viewed in linear space; the game tonemaps instead.
    s_Colour = pow(saturate(s_Colour * g_exposure), 1.0 / 2.2);
    return float4(s_Colour, 1.0);
}";

    /// <summary>
    /// Resolve for a FORWARD (single-target) shader. Its output is already-lit premultiplied colour, not
    /// g-buffer attributes — running it through the lit resolve reads colour as a normal and shades
    /// garbage (the all-black glass cube). This one applies the game's own blend instead: source plus
    /// background times one-minus-alpha. At alpha 0 the pixel is exactly the background, so a transparent
    /// object leaves no silhouette seam.
    /// </summary>
    public const string c_ForwardResolvePixelShader = @"
cbuffer PreviewLight : register(b0)
{
    float4x4 g_invViewProj;
    float3 g_lightDir;      float g_pad0;
    float3 g_keyColor;      float g_pad1;
    float3 g_topColor;      float g_pad2;
    float3 g_bottomColor;   float g_pad3;
    float3 g_cameraPos;     float g_exposure;
};

Texture2D    g_gbuffer0 : register(t0);
Texture2D    g_depth    : register(t4);
SamplerState g_point    : register(s0);

float4 main(float4 p_Position : SV_Position, float2 p_Uv : TEXCOORD0) : SV_Target
{
    // The same background the lit resolve draws, so switching a tab between a g-buffer and a forward
    // shader keeps the scene looking like the same place.
    float3 s_Bg = lerp(g_bottomColor, g_topColor, saturate(1.0 - p_Uv.y)) * 0.35;

    float s_Depth = g_depth.Sample(g_point, p_Uv).r;
    if (s_Depth >= 1.0)
        return float4(s_Bg, 1.0);

    float4 s_Forward = g_gbuffer0.Sample(g_point, p_Uv);
    float3 s_Colour = pow(saturate(s_Forward.rgb * g_exposure), 1.0 / 2.2)
                    + s_Bg * (1.0 - saturate(s_Forward.a));
    return float4(saturate(s_Colour), 1.0);
}";
}
