using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeShaderEditor.Graph;

/// <summary>
/// One sticker on one weapon, in the weapon's own texture space: the centre of the sticker at (U, V) with
/// V=0 the top row of the texture, its width as a fraction of the texture's width (the height follows the
/// picture's own aspect), and a rotation in degrees about its centre. Placement is per MESH because each
/// weapon has its own unwrap — a spot on the M416's receiver is a different (U, V) on the AK's. The
/// first- and third-person models of a weapon share the diffuse, so one placement dresses both.
/// </summary>
public class StickerPlacement
{
    /// <summary>The weapon mesh the sticker sits on (the first-person body mesh the studio previews).</summary>
    public string Mesh { get; set; } = "";

    /// <summary>The picture: a file in the sticker library (PNG with alpha, or any image WPF decodes).</summary>
    public string Image { get; set; } = "";

    public double U { get; set; }
    public double V { get; set; }

    /// <summary>Width as a fraction of the texture width (0.1 = a tenth of the unwrap across).</summary>
    public double Width { get; set; } = 0.1;

    /// <summary>
    /// Height as a fraction of the texture height, or 0 to follow the picture's own aspect. Set by a corner
    /// drag (a corner sizes the two sides on their own; Shift keeps the shape) — a sticker can be
    /// stretched to counter a weapon's own stretched unwrap.
    /// </summary>
    public double Height { get; set; }

    /// <summary>Degrees about the sticker's centre, counter-clockwise as seen from outside the surface.</summary>
    public double Rotation { get; set; }

    /// <summary>Horizontally flipped.</summary>
    public bool Mirror { get; set; }

    /// <summary>
    /// How far the sticker projects above and below the surface it sits on, as a fraction of its larger
    /// side; 0 = the default (a half). Raised, it climbs onto a part standing further proud beside it;
    /// lowered, it stays off a part behind the one it sits on.
    /// </summary>
    public double Reach { get; set; }

    /// <summary>
    /// Where on the body the centre sits, in the mesh's own units (x, y, z) — set when placed or dragged.
    /// Tells apart the halves of a weapon whose unwrap lays two parts over the same patch of texture; null
    /// (an older placement) = the first part of the body that covers the coordinate.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Anchor { get; set; }

    /// <summary>
    /// A placement laid out IN SPACE rather than on the unwrap (a vehicle's emblem slot: the emblem goes on the vehicle
    /// as a decal, the studio only says where): the outward normal at <see cref="Anchor"/>, in the mesh's own axes. With it the square is
    /// read from the anchor and this normal alone — Width and Height are METRES, Rotation turns it about the normal from upright (its top
    /// toward the mesh's +y; on a face looking up or down, toward −z) — and U, V are only where it was last clicked. A vehicle's unwrap
    /// lays many parts over one patch of texture, so a spot named by it is no spot at all. Null = a placement on the unwrap, as always.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Normal { get; set; }

    /// <summary>Whether the placement is laid out in space (<see cref="Normal"/> set) rather than on the unwrap.</summary>
    [JsonIgnore]
    public bool IsSpatial => Normal is { Length: 3 } && Anchor is { Length: 3 };
}

public class GraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Free-text note rendered as a bubble above the node. Null (the common case) is not serialized.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }

    public Dictionary<string, string> Params { get; set; } = new();

    [JsonIgnore]
    public NodeDef Def => Palette.Get(Kind);

    public string GetParam(string p_Name)
    {
        if (Params.TryGetValue(p_Name, out var s_Value))
            return s_Value;

        var s_Def = Def.Params.Find(p_P => p_P.Name == p_Name);
        return s_Def?.Default ?? "0";
    }

    /// <summary>
    /// Per-pin constant for an unconnected input, stored alongside the node parameters under an "in:" prefix so
    /// it travels with the saved graph without a second dictionary.
    /// </summary>
    private static string InputKey(string p_Port) => $"in:{p_Port}";

    public string? GetInputOverride(string p_Port) =>
        Params.TryGetValue(InputKey(p_Port), out var s_Value) && s_Value.Trim().Length > 0 ? s_Value : null;

    public void SetInputOverride(string p_Port, string? p_Value)
    {
        if (p_Value == null || p_Value.Trim().Length == 0)
            Params.Remove(InputKey(p_Port));
        else
            Params[InputKey(p_Port)] = p_Value;
    }
}

public class GraphConnection
{
    public string FromNode { get; set; } = "";
    public string FromPort { get; set; } = "";
    public string ToNode { get; set; } = "";
    public string ToPort { get; set; } = "";
}

/// <summary>
/// A comment box on the canvas: a titled, coloured region that moves the nodes inside it when dragged by its
/// title bar. Purely organisational - groups never affect emission, translation or baking; they only exist so
/// a hundred-node graph can be read the way its author thinks about it.
/// </summary>
public class GraphGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Comment";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 200;

    /// <summary>Index into the canvas's preset palette, so saved files stay stable across theme tweaks.</summary>
    public int Colour { get; set; }
}

public class ShaderGraph
{
    public string Name { get; set; } = "Untitled";

    /// <summary>Shader this graph is authored against; its binding table defines the available input nodes.</summary>
    public string TargetShader { get; set; } = "";

    /// <summary>
    /// True for the SCAFFOLD a failed translation leaves on the canvas (a root plus the slot map's textures,
    /// SampleGraphs.ScaffoldFor): never a translation, so no caching path may keep it — twice a stub reached
    /// the graph cache on a size/pin guess while a real 15-node translation was refused by the same guess
    /// (2026-09-22). Not saved: a graph loaded from disk is what it is.
    /// </summary>
    [JsonIgnore]
    public bool IsScaffold { get; set; }

    /// <summary>
    /// Fingerprint (FNV-1a of the emitted HLSL) of this graph AS TRANSLATED from the game, stamped by the
    /// translate step. It is what lets a variation bake decide honestly whether the user changed the shader's
    /// LOGIC (current emission hashes differently -> the variation needs its own cloned shader) or only its
    /// textures (same hash -> the variation rides the vanilla shader). Absent in older files / hand-made graphs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranslatedHlslHash { get; set; }

    /// <summary>
    /// The pixel permutation this graph was translated from ("ShaderRenderPath_Dx11_sol19_ps.dxbc"), stamped by the translate step: what
    /// tells a cached translation made before the permutation rule changed for its shader (MainWindow.RetireStaleTranslation —
    /// characterroot_xp4, 2026-09-28) from one made after. Absent in older files / hand-made graphs.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranslatedFrom { get; set; }

    /// <summary>
    /// When set, the bake creates a brand-new object VARIATION under this asset name (full path, sibling of
    /// the object like the game's own variations) instead of replacing the target shader: the target's
    /// database entry is cloned under a fresh sibling name, custom textures ride under sibling names, and a
    /// mesh-variation entry keyed by FNV(name) makes the variation spawnable. Null = plain replacement bake.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BakeVariation { get; set; }

    /// <summary>
    /// Whether the baked mod should point the level's OWN copies of the object at this variation as the level
    /// loads. Without it a variation only shows on objects placed with it, so the map looks unchanged — which
    /// is almost never what someone authoring a variation wants, hence the default.
    ///
    /// It lives on the GRAPH and not on the bake request because it is a property of THIS shader: a mod can
    /// carry several, and one of them may be meant for hand placement while another replaces what is already
    /// in the map. Meaningless without <see cref="BakeVariation"/>.
    /// </summary>
    public bool ApplyVariationToLevel { get; set; } = true;

    /// <summary>
    /// The mesh the baked variation's database entry rides on. Stamped from the preview's (mesh, variation)
    /// selection when the open document bakes; empty means the bake picks one — and a shared shader can be
    /// used by meshes that are NOT resident when the level bundle loads (a destruction mesh was the first
    /// found for the glass preset), whose entry then crashes the load registering against a null mesh. The
    /// user's pick is the only reliable answer to "which object is this variation for".
    /// </summary>
    public string? BakeMesh { get; set; }

    /// <summary>
    /// For a camo document: the game's own camo this one starts from (a key such as "atacs"), or null for
    /// the weapon as shipped. It is the document's, so it comes back with its tab; the studio maps it to the
    /// texture and the material values it stands for. Never a replacement of that camo in the game — a base.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeCamo { get; set; }

    /// <summary>
    /// For a camo document: WHAT IT WAS BEING LOOKED AT ON when it was saved — a weapon's mesh, or an
    /// ATTACHMENT's mesh, which is enough to bring both back (the studio knows which weapon an attachment
    /// hangs from). Opening the file picks it again, so the file reopens on the piece it was authored on
    /// instead of a graph floating with no art (opening it must bring back the graph plus whatever attachment
    /// it was on; it used to open only the graph, with no textures, and neither the weapon nor the accessory).
    /// Null for a document saved outside camo mode, or one saved before this existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviewMesh { get; set; }

    /// <summary>
    /// The WEAPON that was picked when this was saved, beside <see cref="PreviewMesh"/>.
    ///
    /// ⛔ IT CANNOT BE DERIVED FROM THE ATTACHMENT, and assuming so was wrong (measured 2026-09-20): a
    /// suppressor, a rail or a Kobra is the SAME mesh on dozens of weapons, so asking "whose is this mesh"
    /// answers with the first weapon that offers it — the file would reopen on someone else's gun. The pair
    /// is what identifies the view.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviewWeapon { get; set; }

    /// <summary>
    /// For a camo document: the stickers placed on the weapons, in each weapon's own texture space (see
    /// <see cref="StickerPlacement"/>). They are the document's — a tab carries its own, undo covers them —
    /// and the bake composes them, per weapon, into the layer the sticker texture node samples. Null = none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StickerPlacement>? Stickers { get; set; }

    /// <summary>
    /// For a camo document: per mesh (weapon or accessory), the MATERIAL IDs — the game's own index into the
    /// mesh's material list — that keep their shipped look instead of taking the camo (an object carries
    /// several materials and shaders; the user chooses by ID which ones the camo goes on).
    /// Only materials wearing a weapon preset can be listed here: the others (glass, glow, HUD, tape) never
    /// take the camo anyway. Null = every preset material of every mesh takes it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<int>>? MaterialsOff { get; set; }

    /// <summary>Whether a material ID of a mesh keeps its shipped look under this camo.</summary>
    public bool IsMaterialOff(string p_Mesh, int p_MaterialId) =>
        MaterialsOff != null && MaterialsOff.TryGetValue(p_Mesh, out var s_Ids) && s_Ids.Contains(p_MaterialId);

    /// <summary>
    /// For a camo document: the graphs of the OTHER materials of its objects the user edited (glass, glow,
    /// the HUD, a preset material kept off the camo…), keyed by <see cref="MaterialKey"/> ("mesh|id"). Each is
    /// a full graph aimed at the shader that material wears; it is saved with the camo, comes back with it,
    /// previews live on that material and ships at the bake as that material's own shader (the edits
    /// must persist across material IDs, be saved, and be baked). Null = none edited.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ShaderGraph>? MaterialGraphs { get; set; }

    /// <summary>
    /// For a camo document AS SHIPPED (<see cref="NativeCamo"/> null): the game's own graph of what the subject wears as shipped — the one
    /// the canvas shows in that look — as the user EDITED it (as shipped is really just like ABU or any other camo, only it is the
    /// original). Null while it is the game's, untouched.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ShaderGraph? ShippedGraph { get; set; }

    /// <summary>
    /// ⭐ WHAT ONE ACCESSORY WEARS, when the weapon's numbers are wrong for it (a per-accessory knob): the
    /// constants an accessory overrides for itself, keyed "&lt;weapon folder&gt;/&lt;attachment&gt;" in lower case.
    /// An accessory takes its host weapon's wear and tiling by default -- which is what the game's own
    /// third-person variations carry -- but a scope's specular is not a rifle body's: measured over the 259
    /// accessory materials, 83 set WearPower to 0, and the 3P preset's
    /// 0.6·(1−min((spec·WearAmount)^WearPower, 1)) then weighs the camo at exactly zero. Anything set here wins
    /// over the weapon's and over the camo's own numbers, for the preview and for the bake alike. Null = none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, string>>? AccessoryValues { get; set; }

    /// <summary>
    /// The key one accessory's numbers are kept under: the attachment's short name WITHOUT its weapon's prefix
    /// ("U_L85A2_Acog" and "U_M240_Acog" are both "acog"), because what makes a scope need its own wear is the
    /// specular of ITS mesh, and that mesh is the same one on every weapon that offers it. One setting, every
    /// weapon -- which is also the only way this survives a camo baked over the whole arsenal.
    /// </summary>
    public static string AccessoryKey(string p_Weapon, string p_Tag)
    {
        var s_Tag = p_Tag.ToLowerInvariant();
        var s_Weapon = p_Weapon.ToLowerInvariant();

        if (s_Weapon.Length > 0 && s_Tag.StartsWith(s_Weapon + "_", StringComparison.Ordinal))
            s_Tag = s_Tag[(s_Weapon.Length + 1)..];

        return s_Tag;
    }

    /// <summary>The numbers that accessory overrides, or an empty set.</summary>
    public IReadOnlyDictionary<string, string> AccessoryValuesOf(string p_Weapon, string p_Tag) =>
        AccessoryValues != null && AccessoryValues.TryGetValue(AccessoryKey(p_Weapon, p_Tag), out var s_Values)
            ? s_Values
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps (or, with an empty text, drops) one constant of one accessory; an emptied set is dropped.</summary>
    public void SetAccessoryValue(string p_Weapon, string p_Tag, string p_Constant, string? p_Text)
    {
        var s_Key = AccessoryKey(p_Weapon, p_Tag);
        AccessoryValues ??= new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (!AccessoryValues.TryGetValue(s_Key, out var s_Values))
        {
            s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AccessoryValues[s_Key] = s_Values;
        }

        if (string.IsNullOrWhiteSpace(p_Text))
            s_Values.Remove(p_Constant);
        else
            s_Values[p_Constant] = p_Text.Trim();

        if (s_Values.Count == 0)
            AccessoryValues.Remove(s_Key);

        if (AccessoryValues.Count == 0)
            AccessoryValues = null;
    }

    /// <summary>The key a material's graph is kept under: the mesh path (lower case) and the game's material ID.</summary>
    public static string MaterialKey(string p_Mesh, int p_MaterialId) => $"{p_Mesh.ToLowerInvariant()}|{p_MaterialId}";

    /// <summary>Splits a material key back into its mesh and ID; false for anything else.</summary>
    public static bool TryParseMaterialKey(string p_Key, out string p_Mesh, out int p_MaterialId)
    {
        p_Mesh = "";
        p_MaterialId = -1;
        var s_Bar = p_Key.LastIndexOf('|');
        if (s_Bar <= 0 || !int.TryParse(p_Key[(s_Bar + 1)..], out p_MaterialId))
            return false;

        p_Mesh = p_Key[..s_Bar];
        return true;
    }

    public ShaderGraph? MaterialGraphOf(string p_Mesh, int p_MaterialId) =>
        MaterialGraphs != null && MaterialGraphs.TryGetValue(MaterialKey(p_Mesh, p_MaterialId), out var s_Graph) ? s_Graph : null;

    /// <summary>Keeps (or, with null, drops) the graph of one material; an emptied dictionary is dropped.</summary>
    public void SetMaterialGraph(string p_Mesh, int p_MaterialId, ShaderGraph? p_Graph)
    {
        var s_Key = MaterialKey(p_Mesh, p_MaterialId);
        if (p_Graph != null)
        {
            MaterialGraphs ??= new Dictionary<string, ShaderGraph>(StringComparer.OrdinalIgnoreCase);
            MaterialGraphs[s_Key] = p_Graph;
            return;
        }

        if (MaterialGraphs == null)
            return;

        MaterialGraphs.Remove(s_Key);
        if (MaterialGraphs.Count == 0)
            MaterialGraphs = null;
    }

    /// <summary>Turns the camo on or off for one material ID of a mesh; an emptied list is dropped.</summary>
    public void SetMaterialOff(string p_Mesh, int p_MaterialId, bool p_Off)
    {
        if (p_Off)
        {
            MaterialsOff ??= new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            if (!MaterialsOff.TryGetValue(p_Mesh, out var s_Ids))
                MaterialsOff[p_Mesh] = s_Ids = new List<int>();

            if (!s_Ids.Contains(p_MaterialId))
                s_Ids.Add(p_MaterialId);

            return;
        }

        if (MaterialsOff == null || !MaterialsOff.TryGetValue(p_Mesh, out var s_Listed))
            return;

        s_Listed.Remove(p_MaterialId);
        if (s_Listed.Count == 0)
            MaterialsOff.Remove(p_Mesh);

        if (MaterialsOff.Count == 0)
            MaterialsOff = null;
    }

    /// <summary>
    /// The texture register the sticker layer is sampled from — the register of the texture node the sticker
    /// mode added to this graph. Null until stickers were first used on the graph. The bake binds each
    /// weapon's composed layer to a material parameter at this register; the preview pushes the same layer
    /// there, so what is looked at is what ships.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StickerRegister { get; set; }

    /// <summary>
    /// Side, in texels, of the square layer the stickers of one weapon are composed into (the weapons'
    /// own textures are 2048²; a layer of half that keeps a package small and a sticker still sharp).
    /// Null = the default, 1024.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StickerLayerSize { get; set; }

    /// <summary>
    /// For a camo document: the animated GIF its animated sticker placements play (one GIF per camo — its
    /// frame sheet is a texture of the package, sampled at StickerRegister + 3; each weapon's coordinate
    /// maps, x and y, at StickerRegister + 1 and + 2). Null = no animated sticker. See <see cref="GifAtlas"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StickerAnimation { get; set; }

    /// <summary>
    /// Whether the animated sticker ignores the GIF's alpha channel: its transparent pixels are drawn as
    /// opaque black, so the sticker is the whole frame rectangle. The sheet is rebuilt
    /// from the GIF when this changes; false = the GIF's own transparency.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StickerAnimationOpaque { get; set; }

    /// <summary>
    /// The register of the stickers' MAP: red = which half of a mirrored unwrap each sticker was painted on,
    /// green = the animated sticker's y, alpha = its x (see StickerLayer.PackMaps). Null without a sticker register.
    /// </summary>
    [JsonIgnore] public int? StickerSideRegister => StickerRegister + 1;

    /// <summary>The register of the animated sticker's frame sheet, or null without a sticker register.</summary>
    [JsonIgnore] public int? StickerSheetRegister => StickerRegister is { } s_Register ? StickerGraph.SheetRegisterOf(s_Register) : null;

    /// <summary>
    /// The largest layer a package ships: the weapons' own texture size. A 2048² layer is 5.6 MB of DXT5
    /// per weapon in the package (1024² is 1.4 MB). ⛔ 2026-09-11: a client crash at the first draw of a
    /// 2048² layer (the material's Sticker parameter bound to NOTHING) was first pinned on the size — WRONG:
    /// the same package at 1024² crashed too, and a sister package with a 1024² layer loaded. The package
    /// had no pattern, and the mod's loader only registered the generated-texture bundle for packages WITH
    /// one; the loader now keys on the stickers flag as well. The size was never the cause.
    /// </summary>
    public const int MaxStickerLayerSide = 2048;

    /// <summary>The layer side to compose at: the document's choice or the default, never above the shipped maximum.</summary>
    [JsonIgnore] public int StickerLayerSide => StickerLayerSize is > 0 and <= MaxStickerLayerSide ? StickerLayerSize.Value : Math.Min(1024, MaxStickerLayerSide);

    /// <summary>The stickers placed on one mesh, in placement order (later ones draw on top).</summary>
    public IEnumerable<StickerPlacement> StickersOn(string p_Mesh) =>
        (Stickers ?? new List<StickerPlacement>()).Where(p_S =>
            p_S.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The contract family this graph's explicit interpolator indices were TRANSLATED under, stamped by the
    /// translator. Emission shifts a graph's saved indices when compiling into a probe twin (the SH rows
    /// displace the base layout), which is right for a graph that speaks BASE numbering — but a graph
    /// translated straight from a probe solution already speaks the shifted numbering, and shifting it again
    /// reads past the layout. Null (every pre-existing graph, every hand-authored one) means base numbering.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranslatedFamily { get; set; }

    /// <summary>
    /// A MASTER document embeds every variation of its object as a full graph here — one file holds them
    /// all; the editor unpacks them into working drafts on open and re-embeds on save, and the bake ships
    /// every one without the user listing files by hand. Null on plain single-look documents.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ShaderGraph>? Variations { get; set; }

    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphConnection> Connections { get; set; } = new();

    /// <summary>Comment boxes. Absent in older files, which deserialize to an empty list.</summary>
    public List<GraphGroup> Groups { get; set; } = new();

    public GraphNode? FindNode(string p_Id) => Nodes.Find(p_N => p_N.Id == p_Id);

    public GraphConnection? ConnectionInto(string p_NodeId, string p_Port) =>
        Connections.Find(p_C => p_C.ToNode == p_NodeId && p_C.ToPort == p_Port);

    public IEnumerable<GraphConnection> ConnectionsFrom(string p_NodeId, string p_Port)
    {
        foreach (var s_Connection in Connections)
            if (s_Connection.FromNode == p_NodeId && s_Connection.FromPort == p_Port)
                yield return s_Connection;
    }

    public GraphNode? Root => Nodes.Find(p_N => p_N.Def.IsRoot);

    /// <summary>
    /// Connects an output to an input, replacing whatever fed that input. Inputs take exactly one wire;
    /// outputs may fan out. Rejects wires that would introduce a cycle.
    /// </summary>
    public bool Connect(string p_FromNode, string p_FromPort, string p_ToNode, string p_ToPort)
    {
        if (p_FromNode == p_ToNode)
            return false;

        if (WouldCycle(p_FromNode, p_ToNode))
            return false;

        Connections.RemoveAll(p_C => p_C.ToNode == p_ToNode && p_C.ToPort == p_ToPort);
        Connections.Add(new GraphConnection
        {
            FromNode = p_FromNode,
            FromPort = p_FromPort,
            ToNode = p_ToNode,
            ToPort = p_ToPort,
        });

        return true;
    }

    public void Disconnect(string p_ToNode, string p_ToPort) =>
        Connections.RemoveAll(p_C => p_C.ToNode == p_ToNode && p_C.ToPort == p_ToPort);

    public void RemoveNode(string p_Id)
    {
        Nodes.RemoveAll(p_N => p_N.Id == p_Id);
        Connections.RemoveAll(p_C => p_C.FromNode == p_Id || p_C.ToNode == p_Id);
    }

    private bool WouldCycle(string p_FromNode, string p_ToNode)
    {
        // The new wire makes p_ToNode depend on p_FromNode, so a cycle exists iff p_FromNode already
        // depends on p_ToNode.
        var s_Stack = new Stack<string>();
        var s_Seen = new HashSet<string>();
        s_Stack.Push(p_FromNode);

        while (s_Stack.Count > 0)
        {
            var s_Current = s_Stack.Pop();
            if (s_Current == p_ToNode)
                return true;

            if (!s_Seen.Add(s_Current))
                continue;

            foreach (var s_Connection in Connections)
                if (s_Connection.ToNode == s_Current)
                    s_Stack.Push(s_Connection.FromNode);
        }

        return false;
    }

    /// <summary>Dependency-first ordering of the nodes that actually feed the root.</summary>
    public List<GraphNode> TopologicalFromRoot()
    {
        var s_Result = new List<GraphNode>();
        var s_Root = Root;
        if (s_Root == null)
            return s_Result;

        var s_Visiting = new HashSet<string>();
        var s_Done = new HashSet<string>();

        void Visit(GraphNode p_Node)
        {
            if (s_Done.Contains(p_Node.Id) || !s_Visiting.Add(p_Node.Id))
                return;

            foreach (var s_Port in p_Node.Def.Inputs)
            {
                var s_Connection = ConnectionInto(p_Node.Id, s_Port.Name);
                var s_Source = s_Connection == null ? null : FindNode(s_Connection.FromNode);
                if (s_Source != null)
                    Visit(s_Source);
            }

            s_Visiting.Remove(p_Node.Id);
            s_Done.Add(p_Node.Id);
            s_Result.Add(p_Node);
        }

        Visit(s_Root);
        return s_Result;
    }

    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_JsonOptions);

    public static ShaderGraph FromJson(string p_Json) =>
        JsonSerializer.Deserialize<ShaderGraph>(p_Json) ?? new ShaderGraph();
}
