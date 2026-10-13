using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// One mesh a soldier's appearance puts on him, as the game's data links it (the Soldiers tab: each class is a different model, and
/// the Aftermath DLC's are different models again). Read out of the dump by a separate catalogue-extraction script:
/// the appearance's pieces, each piece's items — a pair (mesh, variation) or the mesh itself — and the socket each lands in.
/// </summary>
public sealed class SoldierItem
{
    /// <summary>The appearance piece it comes from: "MP_US_Assault_UpperBody01".</summary>
    [JsonPropertyName("piece")] public string Piece { get; set; } = "";

    /// <summary>
    /// The PART it is authored under — the three buttons: "head" (headgear and head sockets; in Aftermath and on every RU
    /// soldier one mesh, face + headgear), "upper" (the torso and the first-person arms, which go together) and "lower"
    /// (the legs).
    /// </summary>
    [JsonPropertyName("part")] public string Part { get; set; } = "";

    /// <summary>"headgear", "head", "torso", "legs" or "arms1p".</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = "";

    /// <summary>The skinned visual socket of SoldierSocketList_MP it lands in (0-based).</summary>
    [JsonPropertyName("socket")] public int? Socket { get; set; }

    /// <summary>"3p" or "1p" (the first-person arms).</summary>
    [JsonPropertyName("view")] public string View { get; set; } = "";

    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";
    [JsonPropertyName("meshGuid")] public string MeshGuid { get; set; } = "";

    /// <summary>The ObjectVariation it wears; empty when the piece links the mesh itself (variation 0).</summary>
    [JsonPropertyName("variation")] public string Variation { get; set; } = "";
    [JsonPropertyName("variationGuid")] public string VariationGuid { get; set; } = "";
    [JsonPropertyName("nameHash")] public uint NameHash { get; set; }
}

/// <summary>
/// One of a soldier's STOCK LOOKS — a row of his kit's appearance list, the game's own (native camos previewed on soldiers too, so a
/// user can start from a vanilla camo as a template): its name as the appearance names it ("Ninja", "XP2_ABU",
/// "Appearance01" for the base default), the row, the appearance and its items. Measured over the 16 (with a separate census script):
/// 262 of the 280 looks put the SAME meshes on every part as his default one — only the variations change —; US · Support's default legs
/// and US · Engineer (Aftermath)'s Desert02 torso are other meshes.
/// </summary>
public sealed class SoldierLook
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("row")] public string Row { get; set; } = "";
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "";
    [JsonPropertyName("items")] public List<SoldierItem> Items { get; set; } = new();

    /// <summary>What the picker reads: the name with its underscores as spaces ("XP2 ABU").</summary>
    [JsonIgnore] public string Display => Name.Replace('_', ' ');
}

/// <summary>
/// One of the sixteen soldiers (2 teams × 4 classes × base / Aftermath), with the appearance the studio previews him in — the first
/// row of his kit's appearance list, the game's default — and the level his meshes are drawn against.
///
/// ⛔ TWO LEVELS, NOT ONE: Aftermath's soldiers wear shaders of their own (CharacterRoot_XP4…) and their database entries exist only
/// in Aftermath levels (measured 2026-09-28: 0 in xp2_skybar, all of them in xp4_quake / xp4_fd), so a base soldier is drawn
/// against xp2_skybar and an Aftermath one against xp4_quake.
/// </summary>
public sealed class SoldierEntry
{
    /// <summary>"US_Assault", "RU_Recon_XP4".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>"US" or "RU".</summary>
    [JsonPropertyName("team")] public string Team { get; set; } = "";

    /// <summary>"Assault", "Engineer", "Support", "Recon".</summary>
    [JsonPropertyName("kit")] public string Kit { get; set; } = "";

    /// <summary>"base" or "xp4".</summary>
    [JsonPropertyName("expansion")] public string Expansion { get; set; } = "";

    /// <summary>What the user reads: "US · Assault", "RU · Recon (Aftermath)".</summary>
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    /// <summary>"Gameplay/Kits/USAssault_XP4".</summary>
    [JsonPropertyName("kitAsset")] public string KitAsset { get; set; } = "";

    /// <summary>The appearance the preview wears.</summary>
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "";

    /// <summary>The kit's row that appearance hangs from (U_CAMO_DEFAULT, U_CAMO1_XP4).</summary>
    [JsonPropertyName("row")] public string Row { get; set; } = "";

    /// <summary>"levels/xp2_skybar/xp2_skybar/shaderdb".</summary>
    [JsonPropertyName("shaderDb")] public string ShaderDb { get; set; } = "";

    /// <summary>The variation database holding his entries: "levels/xp2_skybar/domination/meshvariationdb_win32".</summary>
    [JsonPropertyName("database")] public string Database { get; set; } = "";

    [JsonPropertyName("items")] public List<SoldierItem> Items { get; set; } = new();

    /// <summary>Every look of his kit, in the game's order (his default one among them).</summary>
    [JsonPropertyName("looks")] public List<SoldierLook> Looks { get; set; } = new();

    /// <summary>One of his looks by its name, or null.</summary>
    public SoldierLook? LookNamed(string p_Name) =>
        Looks.FirstOrDefault(p_L => p_L.Name.Equals(p_Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a look puts on a part the SAME meshes his default look does (first person included on the upper body): then the part
    /// only changes variations, and it can be previewed and baked from that look. Null when it can, else why not, in the user's words.
    /// </summary>
    public string? LookMisfit(SoldierLook p_Look, string p_Part)
    {
        static List<string> MeshesIn(IEnumerable<SoldierItem> p_Items, string p_Part) => p_Items
            .Where(p_I => p_I.Part.Equals(p_Part, StringComparison.OrdinalIgnoreCase))
            .Select(p_I => p_I.Mesh.ToLowerInvariant()).Distinct().OrderBy(p_M => p_M, StringComparer.Ordinal).ToList();

        var s_Own = MeshesIn(Items, p_Part);
        var s_Its = MeshesIn(p_Look.Items, p_Part);
        return s_Own.SequenceEqual(s_Its)
            ? null
            : $"the look '{p_Look.Display}' puts {(s_Its.Count == 0 ? "nothing" : string.Join(", ", s_Its.Select(p_M => p_M.Split('/')[^1])))} on " +
              $"this part, not {string.Join(", ", s_Own.Select(p_M => p_M.Split('/')[^1]))}";
    }

    /// <summary>The level his meshes are dumped against ("xp2_skybar"): the folder of <see cref="ShaderDb"/>.</summary>
    [JsonIgnore] public string ShaderDbLevel => ShaderDb.Split('/').ElementAtOrDefault(1) ?? "";

    [JsonIgnore] public bool IsAftermath => Expansion.Equals("xp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>His third-person meshes, the whole character the preview shows (the first-person arms apart).</summary>
    [JsonIgnore] public IEnumerable<string> ThirdPersonMeshes => Items.Where(p_I => p_I.View != "1p").Select(p_I => p_I.Mesh)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first-person arms mesh ("" when the appearance names none).</summary>
    [JsonIgnore] public string ArmsMesh => Items.FirstOrDefault(p_I => p_I.View == "1p")?.Mesh ?? "";

    /// <summary>
    /// The mesh that stands for the soldier in the picker: his TORSO — the subject the upper body is authored on, and the one the
    /// rest of the character is drawn around.
    /// </summary>
    [JsonIgnore] public string PreviewMesh => Items.FirstOrDefault(p_I => p_I.Role == "torso")?.Mesh ?? "";

    /// <summary>Every mesh he wears, first person included.</summary>
    [JsonIgnore] public IEnumerable<string> AllMeshes => Items.Select(p_I => p_I.Mesh).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The third-person meshes of one part ("head", "upper", "lower").</summary>
    public IEnumerable<string> MeshesOf(string p_Part) => Items
        .Where(p_I => p_I.View != "1p" && p_I.Part.Equals(p_Part, StringComparison.OrdinalIgnoreCase))
        .Select(p_I => p_I.Mesh).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The Soldiers catalogue: Data/soldiers.json next to the executable.</summary>
public sealed class SoldierCatalog
{
    public IReadOnlyList<SoldierEntry> Soldiers { get; }

    private SoldierCatalog(IReadOnlyList<SoldierEntry> p_Soldiers)
    {
        Soldiers = p_Soldiers;
    }

    public static SoldierCatalog Empty { get; } = new(Array.Empty<SoldierEntry>());

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "soldiers.json");

    /// <summary>Every mesh of every soldier.</summary>
    public IReadOnlyList<string> AllMeshes => Soldiers.SelectMany(p_S => p_S.AllMeshes)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The level a mesh is dumped against: the first soldier who wears it decides, base first (a mesh a base soldier wears — the
    /// US arms, the RU/US shared legs — is drawn with the base shaders the base levels hold).
    /// </summary>
    public string ShaderDbLevelOf(string p_Mesh) => Soldiers
        .OrderBy(p_S => p_S.IsAftermath)
        .FirstOrDefault(p_S => p_S.AllMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase))?.ShaderDbLevel ?? "";

    /// <summary>The soldier a picker entry stands for (by his preview mesh, the torso).</summary>
    public SoldierEntry? OfPreviewMesh(string p_Mesh) => Soldiers.FirstOrDefault(p_S =>
        p_S.PreviewMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a mesh is worn by any soldier.</summary>
    public bool IsSoldierMesh(string p_Mesh) => Soldiers.Any(p_S => p_S.AllMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase));

    public static SoldierCatalog Load(string? p_Path = null)
    {
        var s_Path = p_Path ?? DefaultPath;
        if (!File.Exists(s_Path))
            return Empty;

        var s_Soldiers = JsonSerializer.Deserialize<List<SoldierEntry>>(File.ReadAllText(s_Path)) ?? new List<SoldierEntry>();

        // Team, then base before Aftermath, then the game's class order (the order the kits come in the file).
        return new SoldierCatalog(s_Soldiers
            .OrderBy(p_S => p_S.Team.Equals("US", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p_S => p_S.IsAftermath)
            .ToList());
    }
}
