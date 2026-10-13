namespace RimeShaderEditor.Graph;

/// <summary>
/// Port value types. Mirrors fb::ShaderPortType from the shipped BF3 engine (the shader-graph node types
/// themselves were stripped at bake time, but this enum survives in the binary, so the authoring
/// vocabulary is DICE's rather than invented).
/// </summary>
public enum ShaderPortType
{
    SptBool = 0,
    SptInteger = 1,
    SptScalar = 2,
    SptVec2 = 3,
    SptVec3 = 4,
    SptVec4 = 5,
    SptColor = 6,
}

/// <summary>Blend node mode. Mirrors fb::BlendShaderMode.</summary>
public enum BlendShaderMode
{
    BsmLerp = 0,
    BsmAdd = 1,
    BsmSubtract = 2,
    BsmMultiply = 3,
    BsmMultiply2x = 4,
    BsmScreen = 5,
    BsmDifference = 6,
    BsmLighten = 7,
    BsmDarken = 8,
    BsmOverlay = 9,
}

/// <summary>
/// How the surface is composited — BF3's equivalent of Unreal's material Blend Mode. Mirrors
/// fb::SurfaceShaderType. Opaque/OpaqueAlphaTest/Transparent line up with Unreal's Opaque/Masked/Translucent.
/// Stored per shader in the shaderdb's SurfaceShaderInfo, NOT in the pixel-shader bytecode.
/// </summary>
public enum SurfaceShaderType
{
    SurfaceShaderType_Opaque = 0,
    SurfaceShaderType_OpaqueAlphaTest = 1,
    SurfaceShaderType_OpaqueAlphaTestSimple = 2,
    SurfaceShaderType_Transparent = 3,
    SurfaceShaderType_TransparentDecal = 4,
}

/// <summary>
/// Which lighting maths the surface feeds — BF3's equivalent of Unreal's Shading Model. Mirrors
/// fb::ShaderLightingModel, and at authoring time it also picked the ROOT NODE variant (Standard -> StandardRoot).
/// </summary>
public enum ShaderLightingModel
{
    ShaderLightingModel_Standard = 0,
    ShaderLightingModel_Metallic = 1,
    ShaderLightingModel_Skin = 2,
    ShaderLightingModel_DynamicEnvmap = 3,
    ShaderLightingModel_Translucent = 4,
}

/// <summary>Per-shader booleans. Mirrors fb::SurfaceShaderFlags (a bitfield in SurfaceShaderInfo.Flags).</summary>
[System.Flags]
public enum SurfaceShaderFlags
{
    None = 0,
    GeometryPassEnable = 0x1,
    DeferredShadingEmissiveEnable = 0x2,
    DoubleSided = 0x4,
}

/// <summary>Curve node waveform. Mirrors fb::CurveShaderType.</summary>
public enum CurveShaderType
{
    CstSine = 0,
    CstSineNormalized = 1,
    CstSawtooth = 2,
    CstTriangle = 3,
    CstSquare = 4,
}

public static class PortTypeUtils
{
    /// <summary>
    /// Turns what the user typed for an unconnected input ("0.5", "1,0,0,1") into an HLSL literal of that
    /// pin's type. Returns null when the text is not a usable number list, so the caller can fall back to the
    /// node's own default instead of emitting something that would not compile.
    /// </summary>
    public static string? FormatLiteral(string p_Text, ShaderPortType p_Type)
    {
        var s_Parts = p_Text.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (s_Parts.Length == 0)
            return null;

        var s_Values = new float[s_Parts.Length];
        for (var i = 0; i < s_Parts.Length; i++)
        {
            if (!float.TryParse(s_Parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out s_Values[i]))
                return null;
        }

        var s_Wanted = p_Type.ComponentCount();

        // One number fills every component, which is what people expect when typing a single value.
        if (s_Values.Length == 1 && s_Wanted > 1)
        {
            var s_Single = Format(s_Values[0]);
            return $"{p_Type.HlslType()}({string.Join(", ", System.Linq.Enumerable.Repeat(s_Single, s_Wanted))})";
        }

        if (s_Values.Length < s_Wanted)
            return null;

        if (s_Wanted == 1)
            return Format(s_Values[0]);

        var s_Components = new string[s_Wanted];
        for (var i = 0; i < s_Wanted; i++)
            s_Components[i] = Format(s_Values[i]);

        return $"{p_Type.HlslType()}({string.Join(", ", s_Components)})";
    }

    private static string Format(float p_Value) =>
        p_Value.ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text to show in the properties panel for an unconnected input.</summary>
    public static string DefaultAsText(this PortDef p_Port)
    {
        var s_Default = p_Port.Default ?? "";
        var s_Open = s_Default.IndexOf('(');
        if (s_Open < 0)
            return s_Default;

        var s_Close = s_Default.LastIndexOf(')');
        return s_Close > s_Open
            ? s_Default.Substring(s_Open + 1, s_Close - s_Open - 1).Replace(" ", "")
            : s_Default;
    }

    public static int ComponentCount(this ShaderPortType p_Type) => p_Type switch
    {
        ShaderPortType.SptBool => 1,
        ShaderPortType.SptInteger => 1,
        ShaderPortType.SptScalar => 1,
        ShaderPortType.SptVec2 => 2,
        ShaderPortType.SptVec3 => 3,
        ShaderPortType.SptVec4 => 4,
        ShaderPortType.SptColor => 4,
        _ => 1,
    };

    public static string HlslType(this ShaderPortType p_Type) => p_Type switch
    {
        ShaderPortType.SptBool => "bool",
        ShaderPortType.SptInteger => "int",
        ShaderPortType.SptScalar => "float",
        ShaderPortType.SptVec2 => "float2",
        ShaderPortType.SptVec3 => "float3",
        ShaderPortType.SptVec4 => "float4",
        ShaderPortType.SptColor => "float4",
        _ => "float",
    };

    /// <summary>
    /// Adapts an expression of one port type to another by swizzle/splat, the way a graph editor must when a
    /// vec3 feeds a scalar input or vice versa. Returns null when the conversion is not allowed.
    /// </summary>
    public static string? Adapt(string p_Expression, ShaderPortType p_From, ShaderPortType p_To)
    {
        var s_FromCount = p_From.ComponentCount();
        var s_ToCount = p_To.ComponentCount();

        if (s_FromCount == s_ToCount)
            return p_Expression;

        // Splat a scalar out to any width. HLSL has no single-argument vector constructor, so this must be a
        // cast; float4(x) is a compile error where (float4)x replicates.
        if (s_FromCount == 1)
            return $"(({p_To.HlslType()})({p_Expression}))";

        // Narrow by taking the leading components.
        if (s_FromCount > s_ToCount)
        {
            var s_Swizzle = "xyzw".Substring(0, s_ToCount);
            return $"({p_Expression}).{s_Swizzle}";
        }

        // Widen: pad with 0 for position-like data and 1 for the alpha slot of a colour.
        var s_Pad = p_To == ShaderPortType.SptColor || p_To == ShaderPortType.SptVec4 ? "1" : "0";
        var s_Missing = string.Join(", ", System.Linq.Enumerable.Repeat(s_Pad, s_ToCount - s_FromCount));
        return $"{p_To.HlslType()}({p_Expression}, {s_Missing})";
    }
}
