using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// An asset a socket object points at: a mesh (rigid or skinned) or, for lights, beams and the scope glint,
/// a prefab blueprint. The TYPE is the game's primary-instance type — it is what decides whether there is
/// anything to paint, never the name.
/// </summary>
public sealed class SocketAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    [JsonIgnore]
    public bool IsMesh => Type is "RigidMeshAsset" or "SkinnedMeshAsset" or "CompositeMeshAsset";
}

/// <summary>
/// One WeaponSocketObjectData: the three meshes the engine instances FROM THESE FIELDS (first person, first
/// person zoomed, third person), always under variation 0. The referenced hashes are the name hashes of
/// those meshes and are the chunk filter of the weapon's 1P bundle — read, never rewritten.
/// </summary>
public sealed class SocketObject
{
    /// <summary>"regular" (rigid, placed on a bone) or "skinned" (skinned to the weapon skeleton: foregrips, bipods).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("asset1p")] public SocketAsset? Asset1p { get; set; }
    [JsonPropertyName("asset1pZoom")] public SocketAsset? Asset1pZoom { get; set; }
    [JsonPropertyName("asset3p")] public SocketAsset? Asset3p { get; set; }
    [JsonPropertyName("hashes")] public List<uint> Hashes { get; set; } = new();

    [JsonIgnore] public bool IsSkinned => Kind == "skinned";

    /// <summary>The three fields, in the order the engine names them ("1pAttachment", "1pAttachmentZoom", "3pAttachment").</summary>
    [JsonIgnore]
    public IEnumerable<(string Field, SocketAsset Asset)> Assets
    {
        get
        {
            if (Asset1p != null) yield return ("asset1p", Asset1p);
            if (Asset1pZoom != null) yield return ("asset1pZoom", Asset1pZoom);
            if (Asset3p != null) yield return ("asset3p", Asset3p);
        }
    }
}

/// <summary>One SocketData of the weapon: which unlock switches it on, which bone it hangs from, and its objects.</summary>
public sealed class AccessorySocket
{
    /// <summary>The SoldierWeaponData partition the socket was read from (a weapon can have several).</summary>
    [JsonPropertyName("partition")] public string Partition { get; set; } = "";
    [JsonPropertyName("bone")] public string Bone { get; set; } = "";
    [JsonPropertyName("objects")] public List<SocketObject> Objects { get; set; } = new();
}

/// <summary>
/// One entry of an accessory row: the unlock the player picks with the arrows, and everything it shows.
/// </summary>
public sealed class AccessoryUnlock
{
    /// <summary>e.g. "Weapons/XP1_L85A2/U_L85A2_Acog" — also the partition name of the UnlockAsset.</summary>
    [JsonPropertyName("unlock")] public string Unlock { get; set; } = "";
    [JsonPropertyName("identifier")] public uint Identifier { get; set; }

    /// <summary>The UI category SID of its description (ID_P_CAT_OPTICS, …). Informative: the row is positional.</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";

    [JsonPropertyName("sockets")] public List<AccessorySocket> Sockets { get; set; } = new();

    /// <summary>Every MESH the unlock switches on, 1p/zoom/3p, distinct, lower-case (the game's mesh path).</summary>
    [JsonPropertyName("meshes")] public List<string> Meshes { get; set; } = new();

    /// <summary>
    /// Each of those meshes -> its EBX Name as the partition spells it (mixed case). The bake's clone must be
    /// EXACTLY as long as that string (both the MeshSet header and the EBX store it in place), so it is read
    /// from the game's own data rather than derived from the lower-case resource name.
    /// </summary>
    [JsonPropertyName("meshEbx")] public Dictionary<string, string> MeshEbx { get; set; } = new();

    /// <summary>The prefabs it switches on (laser, flashlight, scope glint) — not paintable, kept so nothing is silently lost.</summary>
    [JsonPropertyName("prefabs")] public List<string> Prefabs { get; set; } = new();

    /// <summary>The last path segment without the "U_" prefix: what the user recognises ("L85A2_Acog").</summary>
    [JsonIgnore]
    public string ShortName
    {
        get
        {
            var s_Leaf = Unlock[(Unlock.LastIndexOf('/') + 1)..];
            return s_Leaf.StartsWith("U_", StringComparison.OrdinalIgnoreCase) ? s_Leaf[2..] : s_Leaf;
        }
    }

    /// <summary>
    /// An unlock that changes nothing visible (heavy barrel, extended magazine, the "No…" placeholders):
    /// the studio has nothing to load for it and must SAY so rather than pretend it is not there.
    /// </summary>
    [JsonIgnore] public bool HasVisual => Meshes.Count > 0;

    /// <summary>
    /// The mesh to preview: the first-person one of the FIRST socket object, zoom left aside (it is the
    /// same model seen through the sight), third person as the fallback for the few 3p-only objects.
    /// </summary>
    [JsonIgnore]
    public string? PreviewMesh
    {
        get
        {
            foreach (var s_Socket in Sockets)
                foreach (var s_Object in s_Socket.Objects)
                {
                    if (s_Object.Asset1p is { IsMesh: true } s_1p) return s_1p.Name;
                    if (s_Object.Asset3p is { IsMesh: true } s_3p) return s_3p.Name;
                }

            return null;
        }
    }
}

/// <summary>One of the three accessory rows of the customization screen, in the game's slot order.</summary>
public sealed class AccessoryRow
{
    /// <summary>0, 1, 2 = the first three groups of the CustomizationTable (the fourth is the camo group).</summary>
    [JsonPropertyName("slot")] public int Slot { get; set; }

    /// <summary>The studio's key for the row: OPTIC / UNDERBARREL / ACCESSORY.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("unlocks")] public List<AccessoryUnlock> Unlocks { get; set; } = new();
}

/// <summary>The accessory rows of one weapon of the weapon catalogue.</summary>
public sealed class WeaponAccessories
{
    /// <summary>Joins <see cref="WeaponEntry.Folder"/>.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("customization")] public string Customization { get; set; } = "";

    /// <summary>The SoldierWeaponData partitions the sockets were read from.</summary>
    [JsonPropertyName("weaponData")] public List<string> WeaponData { get; set; } = new();
    [JsonPropertyName("rows")] public List<AccessoryRow> Rows { get; set; } = new();

    [JsonIgnore]
    public IEnumerable<AccessoryUnlock> Unlocks => Rows.SelectMany(p_R => p_R.Unlocks);
}

/// <summary>
/// The accessories every weapon can mount, read out of the game's sockets (make_accessories.py, the same
/// offline source as weapons.json). Loaded once from the catalogue beside the executable.
///
/// Measured over the 60 weapons (2026-09-18): 180 rows, 1251 unlocks (1034 with a mesh, 217 without),
/// 179 distinct meshes, 6 prefabs left out. The row order is the game's: group 0 is always the optics.
/// </summary>
public sealed class AccessoryCatalog
{
    private readonly Dictionary<string, WeaponAccessories> m_ByFolder;

    private AccessoryCatalog(List<WeaponAccessories> p_Weapons)
    {
        Weapons = p_Weapons;
        m_ByFolder = p_Weapons.ToDictionary(p_W => p_W.Folder, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<WeaponAccessories> Weapons { get; }

    public WeaponAccessories? Of(string p_Folder) =>
        m_ByFolder.TryGetValue(p_Folder, out var s_Weapon) ? s_Weapon : null;

    /// <summary>Every distinct mesh any accessory shows, across all weapons — what a cache run has to pull.</summary>
    public List<string> AllMeshes => Weapons
        .SelectMany(p_W => p_W.Unlocks)
        .SelectMany(p_U => p_U.Meshes)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Whether a mesh path belongs to an accessory of ANY weapon (the bake rules differ for those).</summary>
    public bool IsAccessoryMesh(string p_Mesh) =>
        Weapons.Any(p_W => p_W.Unlocks.Any(p_U =>
            p_U.Meshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The mesh's EBX Name as the game spells it, or null when the catalogue does not carry the mesh.</summary>
    public string? EbxNameOf(string p_Mesh)
    {
        foreach (var s_Weapon in Weapons)
        foreach (var s_Unlock in s_Weapon.Unlocks)
            if (s_Unlock.MeshEbx.TryGetValue(p_Mesh, out var s_Name))
                return s_Name;

        return null;
    }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "accessories.json");

    /// <summary>No rows at all — a studio without the accessory table still opens, with no accessory picker.</summary>
    public static AccessoryCatalog Empty => new(new List<WeaponAccessories>());

    public static AccessoryCatalog Load(string? p_Path = null)
    {
        var s_Path = p_Path ?? DefaultPath;
        var s_Weapons = JsonSerializer.Deserialize<List<WeaponAccessories>>(File.ReadAllText(s_Path))
                        ?? throw new InvalidDataException($"'{s_Path}' is not an accessory catalogue.");

        return new AccessoryCatalog(s_Weapons.OrderBy(p_W => p_W.Folder, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
