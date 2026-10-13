using System;
using System.Collections.Generic;
using RimeShaderEditor.Emit;

namespace RimeShaderEditor.Graph;

/// <summary>
/// Lets a UDK-named node run the NATIVE node's emission unchanged, by answering its input lookups under
/// the names that node asks for.
///
/// This is why the Material output is equivalent by CONSTRUCTION rather than by a copy that can drift: it
/// does not reimplement the GBuffer packing, it runs the very same code the native root runs. A pin that
/// needs converting (a specular EXPONENT into this engine's smoothness) is supplied as a ready expression;
/// everything else is a rename.
/// </summary>
internal sealed class UdkScope : IEmitScope
{
    private readonly IEmitScope m_Inner;

    /// <summary>native pin name -> the UDK pin that stands for it.</summary>
    private readonly Dictionary<string, string> m_Renames;

    /// <summary>native pin name -> an expression to use instead of reading a pin at all.</summary>
    private readonly Dictionary<string, string> m_Overrides;

    public UdkScope(IEmitScope p_Inner, Dictionary<string, string> p_Renames,
        Dictionary<string, string> p_Overrides)
    {
        m_Inner = p_Inner;
        m_Renames = p_Renames;
        m_Overrides = p_Overrides;
    }

    private string Map(string p_Port) => m_Renames.TryGetValue(p_Port, out var s_Name) ? s_Name : p_Port;

    public string In(string p_Port) =>
        m_Overrides.TryGetValue(p_Port, out var s_Expression) ? s_Expression : m_Inner.In(Map(p_Port));

    // An overridden pin counts as GIVEN: the conversion behind it is exactly what the author asked for by
    // typing into the UDK pin, and reporting it as absent would make the native code take its default path.
    public bool IsConnected(string p_Port) =>
        m_Overrides.ContainsKey(p_Port) || m_Inner.IsConnected(Map(p_Port));

    public bool HasExplicitValue(string p_Port) =>
        m_Overrides.ContainsKey(p_Port) || m_Inner.HasExplicitValue(Map(p_Port));

    public void Out(string p_Port, string p_Expression) => m_Inner.Out(p_Port, p_Expression);
    public void Line(string p_Code) => m_Inner.Line(p_Code);

    public string Declare(string p_Hint, ShaderPortType p_Type, string p_Expression) =>
        m_Inner.Declare(p_Hint, p_Type, p_Expression);

    public string Param(string p_Name) => m_Inner.Param(p_Name);
    public float ParamScalar(string p_Name) => m_Inner.ParamScalar(p_Name);

    public TEnum ParamEnum<TEnum>(string p_Name) where TEnum : struct, Enum => m_Inner.ParamEnum<TEnum>(p_Name);

    public ShaderContract Contract => m_Inner.Contract;


    public string HoistFunction(string p_Signature, string p_Body) =>
        m_Inner.HoistFunction(p_Signature, p_Body);
}
