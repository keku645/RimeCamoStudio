using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// One weapon that can carry a camo, as read out of the game's customization tables.
/// </summary>
public sealed class WeaponEntry
{
    /// <summary>e.g. "Weapons/A91/A91_Customization" — the table that holds the camo unlock group.</summary>
    [JsonPropertyName("customization")] public string Customization { get; set; } = "";

    /// <summary>The folder name, which is what the user recognises: "A91", "M416", "SCAR-H".</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";

    /// <summary>e.g. "Weapons/A91/A91" — what the unlock's BlueprintAndVariationPair points at.</summary>
    [JsonPropertyName("blueprint")] public string Blueprint { get; set; } = "";

    /// <summary>
    /// Every mesh the weapon owns: the body plus, on 19 of them, swappable magazines, rails and bipods —
    /// between two and six, always in 1p/3p pairs.
    /// </summary>
    [JsonPropertyName("meshes")] public List<string> Meshes { get; set; } = new();

    /// <summary>
    /// The weapon's own model, 1p and 3p — what a camo is put on. The attachments are deliberately left
    /// out (no magazines or accessories are needed, only the weapon's own model), which
    /// takes the catalogue from 172 mesh entries to 118. ⚠ The cost is visible: a weapon fitted with an
    /// extended magazine wears the camo on the body and the stock skin on the magazine.
    ///
    /// Picked STRUCTURALLY — the shortest base name once the _1p_mesh/_3p_mesh suffix is off — and not by
    /// matching the folder: a quarter of the weapons are named differently from their folder (AK74M ships
    /// ak74_*, XP1_FAMAS ships famas_*), so a folder rule silently returned nothing for 24 of them.
    /// </summary>
    [JsonIgnore]
    public List<string> BodyMeshes
    {
        get
        {
            var s_Groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Mesh in Meshes)
            {
                var s_Leaf = s_Mesh[(s_Mesh.LastIndexOf('/') + 1)..];
                foreach (var s_Suffix in new[] { "_1p_mesh", "_3p_mesh" })
                {
                    if (!s_Leaf.EndsWith(s_Suffix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var s_Base = s_Leaf[..^s_Suffix.Length];
                    if (!s_Groups.TryGetValue(s_Base, out var s_Group))
                        s_Groups[s_Base] = s_Group = new List<string>();

                    s_Group.Add(s_Mesh);
                }
            }

            return s_Groups.Count == 0
                ? new List<string>()
                : s_Groups.OrderBy(p_G => p_G.Key.Length).ThenBy(p_G => p_G.Key, StringComparer.Ordinal)
                    .First().Value;
        }
    }

    /// <summary>
    /// The mesh the studio PREVIEWS: the first-person one (the 1p model is enough), which halves what has
    /// to be pulled out of the game.
    ///
    /// ⛔ PREVIEW ONLY — A BAKE STILL NEEDS BOTH: only when the bundle is baked does the camo have to go on
    /// both models. An MVDB entry is keyed by the MESH,
    /// so shipping one leaves the weapon camouflaged in the player's hands and stock-skinned to everyone else.
    /// </summary>
    [JsonIgnore]
    public string? PreviewMesh => BodyMeshes.FirstOrDefault(p_M =>
        p_M.EndsWith("_1p_mesh", StringComparison.OrdinalIgnoreCase));

    /// <summary>Camos this weapon ALREADY offers. A hook of one of these must never be used here: the row
    /// would share its identifier with a vanilla one and one of the two would lie.</summary>
    [JsonPropertyName("existingCamoIds")] public List<ExistingCamo> ExistingCamos { get; set; } = new();
}

/// <summary>A camo the weapon ships with, and the unlock that offers it.</summary>
public sealed class ExistingCamo
{
    [JsonPropertyName("identifier")] public uint Identifier { get; set; }
    [JsonPropertyName("unlock")] public string Unlock { get; set; } = "";
}

/// <summary>The weapons the tool can put a camo on, loaded once from the catalogue beside the executable.</summary>
public sealed class WeaponCatalog
{
    private WeaponCatalog(IReadOnlyList<WeaponEntry> p_Weapons) => Weapons = p_Weapons;

    public IReadOnlyList<WeaponEntry> Weapons { get; }

    /// <summary>Body meshes across every weapon — the multiplier that decides how big a bake is.</summary>
    public int MeshCount => Weapons.Sum(p_W => p_W.BodyMeshes.Count);

    /// <summary>
    /// Entries the catalogue carries that are not worth offering. Kept HERE rather than filtered out of
    /// the .json, so regenerating the catalogue from the game does not quietly bring them back.
    /// (Empty since 2026-10-06: the crossbow came back, with the pistols, as weapons a user can camo.)
    /// </summary>
    private static readonly string[] s_Excluded = Array.Empty<string>();

    /// <summary>
    /// The weapons the game gives NO camo row (measured in the EBX, 2026-10-06, with a separate census script: the kits'
    /// SECONDARY part and the crossbow, which is a GADGET; the M1911/M9/REX/MP443 have no customization table at all, the Glock 18,
    /// M93R, .44 and crossbow have one without a camo group). Their bodies bake as every weapon's, with the accessories' material
    /// (CamoBaker: the third-person instance on both families), and the framework offers the variation through a camo unlock of the
    /// pistol's own blueprint, picked in the pistol's own window (SidearmCamos.lua). ⛔ Not "no game camo listed": 48 of the 67 have none.
    /// </summary>
    private static readonly string[] s_NoCamoRow =
        { "Glock18", "M1911", "M9", "M93R", "MP412Rex", "MP443", "Taurus44", "XP4_Crossbow_Prototype" };

    public static bool HasNoCamoRow(string p_Folder) => s_NoCamoRow.Contains(p_Folder, StringComparer.OrdinalIgnoreCase);

    public static WeaponCatalog Load(string? p_Path = null)
    {
        var s_Path = p_Path ?? Path.Combine(AppContext.BaseDirectory, "Data", "weapons.json");
        var s_Weapons = JsonSerializer.Deserialize<List<WeaponEntry>>(File.ReadAllText(s_Path))
                        ?? throw new InvalidDataException($"'{s_Path}' is not a weapon catalogue.");

        // Sorted by the name the user sees, not by the order the dump happened to produce.
        return new WeaponCatalog(s_Weapons
            .Where(p_W => !s_Excluded.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p_W => p_W.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }
}
