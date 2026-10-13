using System;
using System.Collections.Generic;

namespace RimeShaderEditor.Graph;

public enum ParamKind
{
    Scalar,
    Color,
    Enum,
    Text,
    Bool,

    /// <summary>A closed list of valid strings — use where a free-text value could emit a broken shader.</summary>
    Choice,
}

/// <summary>
/// Which authoring vocabulary a node belongs to. The style is a PALETTE filter, never an emission
/// difference: a UDK-style node emits exactly what its Frostbite twin emits, which is what lets the author
/// work in one vocabulary and bake the other.
/// </summary>
public enum NodeStyle
{
    /// <summary>Offered in both palettes: the node is called the same thing in either vocabulary, or it is a
    /// Frostbite CONTRACT node with no counterpart at all — hiding those would make real shaders unauthorable.</summary>
    Both,

    /// <summary>The Frostbite-named twin of a node UDK calls something else; hidden while in UDK style.</summary>
    Frostbite,

    /// <summary>The UDK-named twin; hidden while in Frostbite style.</summary>
    Udk,
}

public class PortDef
{
    public string Name { get; init; } = "";
    public ShaderPortType Type { get; init; } = ShaderPortType.SptScalar;

    /// <summary>HLSL literal used when nothing is wired into this input. Null makes the input mandatory.</summary>
    public string? Default { get; init; }

    /// <summary>
    /// True when Default is a contextual EXPRESSION (i.TexCoord.xy, time) rather than a constant, so the
    /// properties panel must not offer it as a number to type over.
    /// </summary>
    public bool DefaultIsExpression { get; init; }

    /// <summary>
    /// Any input with a default can be overridden with a typed constant. An expression default (time,
    /// i.TexCoord.xy) is still overridable — it just must not be PREFILLED as a number, or the panel would
    /// immediately feed "time" back as a value. Empty box means "use the default".
    /// </summary>
    public bool IsEditable => Default != null;

    /// <summary>
    /// Why this port currently does nothing, or null when it is live. Ports that exist for parity with
    /// the original tooling but that the GBuffer pass does not consume are drawn dimmed with this as the reason, the way
    /// Unreal greys out material inputs its blend mode ignores — connecting one silently would be worse.
    /// </summary>
    public Func<GraphNode, string?>? UnavailableReason { get; init; }

    public string? WhyUnavailable(GraphNode p_Node) => UnavailableReason?.Invoke(p_Node);
}

public class ParamDef
{
    public string Name { get; init; } = "";
    public ParamKind Kind { get; init; } = ParamKind.Scalar;
    public string Default { get; init; } = "0";

    /// <summary>For ParamKind.Enum: the enum type whose names are offered in the properties panel.</summary>
    public Type? EnumType { get; init; }

    /// <summary>For ParamKind.Choice: the only accepted values.</summary>
    public List<string> Choices { get; init; } = new();
}

/// <summary>
/// Everything a node emits into the generated HLSL. The emitter resolves each input to an expression
/// (already adapted to the declared port type) before handing the scope to the node.
/// </summary>
public interface IEmitScope
{
    string In(string p_Port);
    bool IsConnected(string p_Port);

    /// <summary>
    /// True when the author gave this input a value at all — by wire OR by typing a constant. Branch on this,
    /// not on IsConnected, or a value typed in the properties panel is discarded without a word.
    /// </summary>
    bool HasExplicitValue(string p_Port);
    void Out(string p_Port, string p_Expression);
    void Line(string p_Code);
    string Declare(string p_Hint, ShaderPortType p_Type, string p_Expression);
    string Param(string p_Name);
    float ParamScalar(string p_Name);
    TEnum ParamEnum<TEnum>(string p_Name) where TEnum : struct, Enum;

    /// <summary>
    /// The target shader's measured contract. Input nodes need it because the PsIn field for a given interpolator
    /// depends on the family — the rigid mesh keeps DICE's measured names, everything else gets a neutral InterpN.
    /// </summary>
    Emit.ShaderContract Contract { get; }

    /// <summary>
    /// The sampler a MATERIAL texture is read through — not always s0. A flavour carrying engine textures
    /// (a lightmap) gives them s0 and starts the material at s1, and the engine's sampler does not tile:
    /// reading a repeating detail texture through it stretches one texel into broad bands.
    /// </summary>
    string MaterialSampler => Contract?.MaterialSampler ?? "sampler0";

    /// <summary>The sampler THIS flavour reads that texture register through — per texture, because a
    /// flavour spreads its material across several samplers with different addressing.</summary>
    string SamplerFor(string p_Register) =>
        Contract != null && int.TryParse(p_Register, out var s_Slot)
            ? Contract.SamplerFor(s_Slot)
            : MaterialSampler;

    /// <summary>
    /// Hoists an HLSL function above main and returns its generated name. The Script node needs it: a body
    /// with `return` statements cannot be inlined into main, and HLSL has no lambdas.
    /// </summary>
    string HoistFunction(string p_Signature, string p_Body);
}

public class NodeDef
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "Math";

    /// <summary>Which palette offers this node. See <see cref="NodeStyle"/>.</summary>
    public NodeStyle Style { get; init; } = NodeStyle.Both;

    /// <summary>
    /// Other names this node answers to in the search box — the UDK term for a node that already exists
    /// here under another name. Cheaper and safer than a duplicate node: same emission, same node, found
    /// by whichever word the author happens to know.
    /// </summary>
    public List<string> Aliases { get; init; } = new();

    /// <summary>Shown in the properties panel. Say what the engine does with this, not what the node is called.</summary>
    public string Description { get; init; } = "";
    public List<PortDef> Inputs { get; init; } = new();
    public List<PortDef> Outputs { get; init; } = new();
    public List<ParamDef> Params { get; init; } = new();

    /// <summary>True for the single terminal node of a graph; it consumes inputs and writes render targets.</summary>
    public bool IsRoot { get; init; }

    /// <summary>Node samples a texture, so the canvas reserves room for its thumbnail.</summary>
    public bool HasTexturePreview { get; init; }

    public Action<IEmitScope, GraphNode> Emit { get; init; } = (_, _) => { };

    /// <summary>
    /// Text shown ON the node in the canvas, next to the title. For nodes whose meaning lives in a parameter
    /// (a constant's value, a swizzle's channels, an external's name) - a graph where that only shows in the
    /// properties panel cannot be read at a glance, which defeats the graph.
    /// </summary>
    public Func<GraphNode, string?>? CanvasBadge { get; init; }

    /// <summary>
    /// What this node is CALLED in the vocabulary being used. A node kept because both catalogs have it
    /// still has to wear the right name: someone authoring in UDK looks for DotProduct, not Dot.
    /// </summary>
    public string TitleFor(bool p_UdkStyle)
    {
        if (!p_UdkStyle)
            return Title;

        // ⛔ A node that already BELONGS to that vocabulary keeps its own title: its aliases are extra words
        // for the search box (the class names of the parameter variants), and taking the first one as the
        // caption would rename TextureSample to "TextureSampleParameter".
        if (Style == NodeStyle.Udk)
            return Title;

        // The UDK name first; then, for a node both catalogs share, the KIND — which IS that name
        // ("Fmod", "OneMinus"), where our own Title is a prettified label ("Modulo", "One Minus").
        // ⚠ The FIRST alias is the caption, so it has to be the name of the node that does the SAME job —
        // the rest are search words. ("TextureObject" as the caption of a sampling node was exactly this
        // going wrong.)
        if (Aliases.Count > 0)
            return Aliases[0];

        return Palette.IsSharedWithUdk(Kind) ? Kind : Title;
    }

    /// <summary>
    /// The heading this node lists under in the vocabulary being used. A node the UDK palette only carries
    /// because a conversion can leave it behind is grouped apart, under its REAL name: filing it among that
    /// catalog's own nodes would say it belongs to a vocabulary that has never had it.
    /// </summary>
    public string CategoryFor(bool p_UdkStyle) =>
        p_UdkStyle && Palette.IsUdkReachable(this) ? Palette.c_ForeignCategory : Category;

    public PortDef? FindInput(string p_Name) => Inputs.Find(p_P => p_P.Name == p_Name);
    public PortDef? FindOutput(string p_Name) => Outputs.Find(p_P => p_P.Name == p_Name);
}
