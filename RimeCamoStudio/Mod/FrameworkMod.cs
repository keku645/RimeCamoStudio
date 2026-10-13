using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Mod;

/// <summary>One weapon a package offers its camo on: the catalogue folder and the variation partition it ships.</summary>
public sealed class PackageWeapon
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("variation")] public string Variation { get; set; } = "";
}

/// <summary>
/// One mesh of an accessory the package repaints, as the loader needs it: the game's own mesh (the name a
/// socket object's field holds) and the clone the package ships for it. The clone is what the loader points
/// the socket at — the engine builds socket objects from those fields under variation 0, so a second
/// IDENTITY is the only way to give one of them a look of its own (closed in-game, boot 26).
/// </summary>
public sealed class PackageAccessoryMesh
{
    /// <summary>The game's mesh asset, spelled as the game spells it (what SearchForDataContainer takes).</summary>
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";

    /// <summary>The clone's partition name — the same string as the clone asset's Name, by convention.</summary>
    [JsonPropertyName("clone")] public string Clone { get; set; } = "";
}

/// <summary>One accessory a package repaints: which weapon it hangs from, which unlock it is, and its meshes.</summary>
public sealed class PackageAccessory
{
    /// <summary>Catalogue folder of the host weapon ("XP1_L85A2").</summary>
    [JsonPropertyName("weapon")] public string Weapon { get; set; } = "";

    /// <summary>The unlock's short name ("L85A2_Acog") — what the studio and the menu call it.</summary>
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";

    [JsonPropertyName("meshes")] public List<PackageAccessoryMesh> Meshes { get; set; } = new();
}

/// <summary>
/// One vehicle a package paints (vehicle camos, done as with the accessories): the body mesh gets a clone
/// under a name of its own whose hash-0 entry wears the camo, and the client's vehicle view points the vehicle's data at
/// that clone for the player who chose it (each player sees his own). The class is the customization row it is picked
/// in (the SID the camo window's name binding carries, e.g. ID_EOR_SCORINGBUCKET_VEHICLEMBT).
/// </summary>
public sealed class PackageVehicle
{
    /// <summary>The vehicle's blueprint (catalogue spelling), e.g. vehicles/m1a2/m1abrams.</summary>
    [JsonPropertyName("blueprint")] public string Blueprint { get; set; } = "";

    /// <summary>The class SID of its customization row.</summary>
    [JsonPropertyName("class")] public string Class { get; set; } = "";

    /// <summary>The game's body mesh.</summary>
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";

    /// <summary>The clone the package ships for it (its EBX Name).</summary>
    [JsonPropertyName("clone")] public string Clone { get; set; } = "";

    /// <summary>The body's camo pieces the package ships (option B); empty for a package baked before them.</summary>
    [JsonPropertyName("pieces")] public List<PackageVehiclePiece> Pieces { get; set; } = new();

    /// <summary>The driver seat's own part, where the loader hangs the switch that decides the camo (seat 1 only).</summary>
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";

    /// <summary>
    /// The bundle of the package's pack superbundle that carries this vehicle ("veh0"), or "" for a package baked before vehicles
    /// had bundles of their own (their part rides in packv).
    /// </summary>
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";

    /// <summary>The level bundles that carry the vehicle (lower case, "levels/xp3_desert/conquestlarge0"): the loader puts the
    /// vehicle's bundle right behind whichever of them a level loads, and loads nothing of it on a level that has none.</summary>
    [JsonPropertyName("levelBundles")] public List<string> LevelBundles { get; set; } = new();
}

/// <summary>
/// One camo piece of a vehicle body in a package: the rigid mesh the package ships, the blueprint component it hangs from (at
/// identity, behind the driver's camo switch) and the component whose stock part it replaces (hidden for that driver).
/// </summary>
public sealed class PackageVehiclePiece
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The piece's mesh (its EBX Name).</summary>
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";

    [JsonPropertyName("holder")] public string Holder { get; set; } = "";

    [JsonPropertyName("hide")] public string Hide { get; set; } = "";

    /// <summary>The game shows and hides its stock parts by itself: its switch follows that (see VehiclePiece.Mirror). Not written
    /// when false, as in a package baked before it.</summary>
    [JsonPropertyName("mirror")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Mirror { get; set; }
}

/// <summary>
/// The soldier a SKIN package dresses (one package = the soldier on screen, part by part). What the framework's soldier
/// module needs to dress him in the game the way the studio showed him: the appearance of his that his entries are copied from (the one
/// the studio previews him in), his first-person arms and which of their materials are his trousers (the lower body's), and per part the
/// textures and numbers the package brings. Nothing of the game's is in the package: only textures, and this description.
/// </summary>
public sealed class PackageSoldier
{
    /// <summary>The studio's soldier key: "US_Assault", "RU_Recon_XP4".</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>What the user reads: "US · Assault".</summary>
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    /// <summary>"US" or "RU".</summary>
    [JsonPropertyName("team")] public string Team { get; set; } = "";

    /// <summary>"Assault", "Engineer", "Support", "Recon".</summary>
    [JsonPropertyName("kit")] public string Kit { get; set; } = "";

    /// <summary>"base" or "xp4" (Aftermath: other meshes, other shaders, entries only in its levels).</summary>
    [JsonPropertyName("expansion")] public string Expansion { get; set; } = "";

    /// <summary>His kit asset: "Gameplay/Kits/USAssault".</summary>
    [JsonPropertyName("kitAsset")] public string KitAsset { get; set; } = "";

    /// <summary>The game's appearance he was previewed in: the one whose entries the skin's own are copied from.</summary>
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "";

    /// <summary>His first-person arms mesh ("" when his appearance names none).</summary>
    [JsonPropertyName("arms")] public string Arms { get; set; } = "";

    /// <summary>The material ids of the arms mesh that are his TROUSERS: the lower body's in first person, the rest the upper body's.</summary>
    [JsonPropertyName("trousers")] public List<int> Trousers { get; set; } = new();

    /// <summary>The parts the skin changes; a part not listed stays as the game ships it.</summary>
    [JsonPropertyName("parts")] public List<PackageSoldierPart> Parts { get; set; } = new();

    /// <summary>
    /// ⭐ The bundle of the package's variation database ENTRIES (baked the way the vehicles' are — the framework editing the
    /// game's databases from Lua killed the MP_007 server as they loaded, and at the build came too late for the client's index): one
    /// entry per mesh and part the skin dresses and the first-person arms' copies, baked from his database and loaded right behind the
    /// level bundle that carries his meshes, as a vehicle's veh0. Null in a package baked before it.
    /// </summary>
    [JsonPropertyName("bundle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bundle { get; set; }

    /// <summary>The level bundles ("levels/mp_007/conquest_large") that carry EVERY mesh those entries point at — the bundle goes behind them.</summary>
    [JsonPropertyName("levels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Levels { get; set; }

    /// <summary>The variation names the bundle has entries for ("Characters/Soldiers/Skin/SNOW/torso/us_upperbody04_mesh"…): the soldier module
    /// makes the same names and dresses only what has its entry here.</summary>
    [JsonPropertyName("entries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Entries { get; set; }

    /// <summary>
    /// ⭐ ANOTHER SOLDIER THE SAME SKIN GOES TO (per part, the soldiers the user ticks): the skin's key for HIM —
    /// "&lt;skin key&gt;_&lt;SOLDIER KEY&gt;", what his variations, pieces and partitions are named after (CamoBaker.SkinKeyFor) — and the identifier
    /// his pick is kept under (the window keys its table by identifier: one per soldier). Null on the soldier the skin was made on.
    /// </summary>
    [JsonPropertyName("skinKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkinKey { get; set; }

    [JsonPropertyName("identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Identifier { get; set; }
}

/// <summary>
/// One part of a soldier skin ("head", "upper", "lower"): the textures the package ships for it, bound BY PARAMETER NAME, and the numbers
/// written over its materials. Both go to the materials of the part's meshes that wear the camo cloth (their entry binds CamoTile) — the
/// ones the game's own appearances vary, and the ones the in-game tests dressed (2026-09-28: torso, trousers, helmet, headscarf).
/// </summary>
public sealed class PackageSoldierPart
{
    [JsonPropertyName("part")] public string Part { get; set; } = "";

    /// <summary>Its third-person meshes (the first-person arms are the soldier's, split by <see cref="PackageSoldier.Trousers"/>).</summary>
    [JsonPropertyName("meshes")] public List<string> Meshes { get; set; } = new();

    /// <summary>
    /// The game's appearance whose look this part STARTS from (the studio's "preview native camo" per part): its entries
    /// are copied from that look's variations — and on the lower body the first-person trousers from that look's arms. Null: the soldier's
    /// <see cref="PackageSoldier.Appearance"/>. Absent from a package baked before it.
    /// </summary>
    [JsonPropertyName("appearance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Appearance { get; set; }

    /// <summary>
    /// Where the camo lies on it (a choice per part): "stock" — where the game's own mask puts it —, "white" — all the
    /// cloth, a white mask of the package's —, or "picture" — the user's own picture on the Mask node.
    /// </summary>
    [JsonPropertyName("mask")] public string Mask { get; set; } = "stock";

    /// <summary>Texture parameter → the package's texture bound there ("CamoTile" → the pattern, "Mask" → the white mask…).</summary>
    [JsonPropertyName("textures")] public Dictionary<string, string> Textures { get; set; } = new();

    /// <summary>Constant → "x,y,z,w": the numbers typed on the part's graph, written over its materials' own.</summary>
    [JsonPropertyName("values")] public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>
    /// The part's own shaders (a graph with nodes or wires of its own): the part's materials that wear <see cref="PackageSoldierShader.Shader"/>
    /// draw with its copy. Empty = the game's shaders.
    /// </summary>
    [JsonPropertyName("shaders")] public List<PackageSoldierShader> Shaders { get; set; } = new();

    /// <summary>
    /// The UI texture of the part's OWN cell — the skin on the part's tab of the soldier's window (each part can be a different
    /// thing) —, or null: the skin's row picture shows there. Absent from a package baked before it.
    /// </summary>
    [JsonPropertyName("thumbnail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thumbnail { get; set; }

    /// <summary>The family the part's own cell is filed under on its tab, or null: the skin's.</summary>
    [JsonPropertyName("family")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Family { get; set; }

    /// <summary>What the part's own cell reads on its tab, or null: the skin's name (display only — the skin is known by its own).</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>What the part's own cell shows in the INFO box on its tab, or null: the skin's text.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

/// <summary>A copy of a shader a skin ships for one part: which shader it stands for, its name, the picture parameters it declares.</summary>
public sealed class PackageSoldierShader
{
    /// <summary>The game's shader the part's materials wear, as the studio names it ("shaders/root/characterroot").</summary>
    [JsonPropertyName("shader")] public string Shader { get; set; } = "";

    /// <summary>The copy's shader asset (a stub in &lt;stem&gt;/shaders, its entry in the skin's own database).</summary>
    [JsonPropertyName("clone")] public string Clone { get; set; } = "";

    /// <summary>The picture parameters only this copy reads ("Picture9"): bound only where it draws.</summary>
    [JsonPropertyName("declares")] public List<string> Declares { get; set; } = new();
}

/// <summary>
/// What a baked camo package says about itself — the camo.json inside its folder. Everything the
/// framework's loader needs to offer the camo (written into the generated Lua index) and everything the
/// studio needs to list it. Written by the baker; read back by <see cref="FrameworkMod.Register"/>.
/// </summary>
public sealed class PackageInfo
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>
    /// The menu family the camo is filed under (one of the eight buttons), or "" for the guess by name.
    /// The choice was added on 2026-09-20; it rides in the table's package header, which the screens'
    /// script prefers to its own keywords.
    /// </summary>
    [JsonPropertyName("family")] public string Family { get; set; } = "";

    /// <summary>
    /// Whether the package paints the weapon bodies (the default) or only its attachments. A piece-only
    /// package adds no camo row to any weapon: its camos are picked in the piece's own window.
    /// </summary>
    [JsonPropertyName("body")] public bool Body { get; set; } = true;

    /// <summary>The borrowed UI description the row wears. A loadout stores the camo by this number.</summary>
    [JsonPropertyName("identifier")] public uint Identifier { get; set; }

    /// <summary>Superbundle stem as the Lua mounts it: "camos/&lt;folder&gt;" (lowercase, no Win32/).</summary>
    [JsonPropertyName("superbundle")] public string Superbundle { get; set; } = "";

    /// <summary>The pattern texture the package ships, or null when it borrows a game texture.</summary>
    [JsonPropertyName("patternTexture")] public string? PatternTexture { get; set; }

    /// <summary>The game texture the camo uses when it ships none of its own.</summary>
    [JsonPropertyName("nativeTexture")] public string? NativeTexture { get; set; }

    /// <summary>The UI texture of the row's thumbnail, or null when the package carries none.</summary>
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }

    /// <summary>
    /// One of the framework's DEFAULT camos: the game's own pattern offered on every weapon, under
    /// sb/Win32/defaultcamos/. Its identifier is the game's own row for that pattern (the description already
    /// reads right: name, text, picture), so the loader never rewrites anything for it, and its seat is
    /// reserved in the studio's pool rather than assigned. Register keeps it on that seat; a user package
    /// found on the same identifier is the one that moves.
    /// </summary>
    [JsonPropertyName("native")] public bool Native { get; set; }

    /// <summary>
    /// The look its weapons wear when their player picked NO camo (the pistols as shipped with the emblem slot): the
    /// framework dresses such a weapon with it and lists it in no camo window. Written to the index only when true.
    /// </summary>
    [JsonPropertyName("defaultLook")] public bool DefaultLook { get; set; }

    /// <summary>
    /// The package ships its own first-person preset (the graph's logic) in a fourth superbundle,
    /// &lt;stem&gt;/shaders: a small shader database under a fresh name plus the stubs its variations point at,
    /// which the loader puts BEFORE the level's own bundles.
    /// </summary>
    [JsonPropertyName("shader")] public bool Shader { get; set; }

    /// <summary>
    /// Whether packnc carries sticker layers (one generated texture per weapon that has stickers) or the pictures of
    /// an edited graph (an attachment's as-shipped graph, 2026-09-25: same failure, same lane). Like the
    /// pattern, they live in packncv, which the loader has to load BEFORE the weapons' materials bind —
    /// measured 2026-09-11: a sticker package without a pattern shipped its layer in a bundle the loader
    /// never loaded (it only did so for a pattern), the material's Sticker parameter bound to nothing and
    /// the client died at the first draw.
    /// </summary>
    [JsonPropertyName("stickers")] public bool Stickers { get; set; }

    [JsonPropertyName("weapons")] public List<PackageWeapon> Weapons { get; set; } = new();

    /// <summary>
    /// The accessories the package repaints, with the clone it ships for each of their meshes. The loader
    /// points the weapons' socket objects at those clones; a package with none leaves every socket as the
    /// game shipped it.
    /// </summary>
    [JsonPropertyName("accessories")] public List<PackageAccessory> Accessories { get; set; } = new();

    /// <summary>
    /// The vehicles the package paints, each with the clone it ships for its body mesh. The framework lists them in the
    /// vehicle camo windows (per class) and the player's client points his vehicles at the clones he chose. Empty = a
    /// camo of weapons only, as every package baked before 2026-09-23.
    /// </summary>
    [JsonPropertyName("vehicles")] public List<PackageVehicle> Vehicles { get; set; } = new();

    /// <summary>
    /// The kit lists (assault, engineer, support, recon) the row's identifier means THIS camo in: the kits
    /// its weapons belong to, all four when any of them is an all-kit weapon. Written by the baker; a
    /// package without it (baked before 2026-09-11) is computed from its weapons by Register. The loader
    /// writes the row's label per kit, so one identifier can serve a different camo in each kit.
    /// </summary>
    [JsonPropertyName("kits")] public List<string>? Kits { get; set; }

    /// <summary>
    /// The soldier a SKIN package dresses, or null for a camo of weapons or vehicles (not written then: their camo.json reads as it always
    /// did). A skin paints no weapon and no vehicle, and goes into its own index (<see cref="FrameworkMod.SoldierIndexFile"/>), never into
    /// the camo index the weapon and vehicle loaders read.
    /// </summary>
    [JsonPropertyName("soldier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PackageSoldier? Soldier { get; set; }

    /// <summary>
    /// ⭐ The OTHER soldiers the same skin goes to (per part, the soldiers the user ticks — this model only, US, RU, all…):
    /// each with the parts it has on him, his own key and identifier, and his own bundle of entries. The package's textures and pictures
    /// are the skin's, shared; the index lists each one as a skin of that soldier. Null: the skin is its soldier's alone.
    /// </summary>
    [JsonPropertyName("also")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PackageSoldier>? Also { get; set; }

    [JsonPropertyName("built")] public string Built { get; set; } = "";

    /// <summary>The level database the entries were copied from — the map the package was templated on.</summary>
    [JsonPropertyName("mvdbSource")] public string MvdbSource { get; set; } = "";
}

/// <summary>
/// The camo framework mod as the studio sees it: where packages go, how they are found, and how the mod is
/// told about them. Two roots, one mechanism: sb/Win32/defaultcamos/ holds the eight DEFAULT camos (the
/// game's own patterns on every weapon, baked by the studio like any other package — no
/// per-level base bundle, nothing that touches a level's own shader database), sb/Win32/camos/ the camos
/// people author.
///
/// ⛔ A DROPPED FOLDER IS NOT ENOUGH, AND THE REASON IS THE HOST'S, NOT OURS. The game client only receives
/// the superbundles a mod DECLARES in mod.json — an undeclared one is never synced and the level waits for it
/// forever (six test cycles, once). And the mod's Lua cannot list a folder. So every package has to be written
/// into two files: the Superbundles list of mod.json and a generated Lua index. <see cref="Register"/> does
/// both from what is on disk, so a package copied in by hand is picked up the same way as a baked one.
/// </summary>
public static class FrameworkMod
{
    /// <summary>The first line of the framework's shared Lua — what says "this folder is the framework".</summary>
    public const string LuaMarker = "-- Weapon camo framework";

    /// <summary>Superbundle prefix of the user packages, as mod.json declares them.</summary>
    public const string SuperbundlePrefix = "Win32/camos/";

    /// <summary>Superbundle prefix of the default (native) camo packages.</summary>
    public const string NativePrefix = "Win32/defaultcamos/";

    /// <summary>
    /// The game's own row for each of its eight camo patterns, by the studio's native key (CamoSession
    /// .NativeCamos): the UIWeaponAccessoryDescription of one premium weapon's unlock for that pattern —
    /// ABU → L96, ATACS/Berkut → L85A2, DigiFlora → L96, DsrtTiger → SCAR-H, Kamysh → Pecheneg, NWU → F2000,
    /// Partizan → MTAR. The default packages wear these (the row already shows the right name and picture),
    /// and they are spent before any studio camo — see HookPool.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, uint> NativeIdentifierOf =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["abu"] = 3941658638,
            ["atacs"] = 3234898665,
            ["berkut"] = 3234898666,
            ["digiflora"] = 3266543565,
            ["dsrttiger"] = 566124738,
            ["kamysh"] = 2113243326,
            ["nwu"] = 821974674,
            ["partizan"] = 27546601,
        };

    /// <summary>The eight identifiers above, for whoever only needs to know a seat is a native row's.</summary>
    public static readonly IReadOnlyList<uint> NativeIdentifiers = NativeIdentifierOf.Values.ToList();

    public static string PackagesDir(string p_Mod) => Path.Combine(p_Mod, "sb", "Win32", "camos");
    public static string NativesDir(string p_Mod) => Path.Combine(p_Mod, "sb", "Win32", "defaultcamos");

    public static string PackageDir(string p_Mod, string p_Folder, bool p_Native = false) =>
        Path.Combine(p_Native ? NativesDir(p_Mod) : PackagesDir(p_Mod), p_Folder);

    /// <summary>The superbundle stem the Lua mounts for a package: no Win32/, lowercase.</summary>
    public static string StemOf(string p_Folder, bool p_Native) =>
        (p_Native ? "defaultcamos/" : "camos/") + p_Folder.ToLowerInvariant();
    public static string IndexFile(string p_Mod) => Path.Combine(p_Mod, "ext", "Shared", "Camos", "index.lua");

    /// <summary>
    /// The generated index of the soldier SKINS (PackageInfo.Soldier): a file of its own, because a skin paints no weapon and no vehicle and
    /// the four readers of the camo index (the loader, the vehicle swap, the vehicle camos, the vehicle view) have nothing to do with it.
    /// </summary>
    public static string SoldierIndexFile(string p_Mod) => Path.Combine(p_Mod, "ext", "Shared", "Camos", "soldiers.lua");

    /// <summary>
    /// The framework's own FIRST-PERSON SPLIT package (CamoBaker.BuildFirstPerson): the copies of the stock looks' arms
    /// (trousers apart), baked once for every soldier. A root of its own — neither a camo nor a default camo, and no camo.json: its
    /// description (firstperson.json) is what Register writes its Lua index from.
    /// </summary>
    public const string FirstPersonStem = "soldiers/firstperson";
    public const string FirstPersonDescription = "firstperson.json";
    public static string FirstPersonDir(string p_Mod) => Path.Combine(p_Mod, "sb", "Win32", "soldiers", "firstperson");
    public static string FirstPersonIndexFile(string p_Mod) => Path.Combine(p_Mod, "ext", "Shared", "Camos", "firstperson.lua");

    /// <summary>The generated kit table the loader reads (which weapon is in which kit's list).</summary>
    public static string KitsFile(string p_Mod) => Path.Combine(p_Mod, "ext", "Shared", "Camos", "kits.lua");

    /// <summary>A package's kit lists: what its camo.json says, or what its weapons imply.</summary>
    public static IReadOnlyList<string> KitsOf(PackageInfo p_Package, Catalog.Kits p_Kits) =>
        p_Package.Kits is { Count: > 0 } s_Kits
            ? Catalog.Kits.Names.Where(p_K => s_Kits.Contains(p_K, StringComparer.OrdinalIgnoreCase)).ToList()
            : p_Kits.KitsOfWeapons(p_Package.Weapons.Select(p_W => p_W.Folder).ToList());
    public static string ModJson(string p_Mod) => Path.Combine(p_Mod, "mod.json");
    public static string SharedInit(string p_Mod) => Path.Combine(p_Mod, "ext", "Shared", "__init__.lua");

    /// <summary>Why a folder is not the framework, or null when it is.</summary>
    public static string? Validate(string? p_Folder)
    {
        if (string.IsNullOrWhiteSpace(p_Folder))
            return "no folder given";

        if (!Directory.Exists(p_Folder))
            return "the folder does not exist";

        if (!File.Exists(ModJson(p_Folder)))
            return "no mod.json in it — not a mod folder";

        if (!File.Exists(SharedInit(p_Folder)))
            return "no ext/Shared/__init__.lua in it — not the camo framework";

        try
        {
            var s_First = File.ReadLines(SharedInit(p_Folder)).FirstOrDefault() ?? "";
            if (!s_First.StartsWith(LuaMarker, StringComparison.Ordinal))
                return "its Lua is not the camo framework's (first line differs)";
        }
        catch (Exception s_Exception)
        {
            return $"cannot read its Lua: {s_Exception.Message}";
        }

        return null;
    }

    /// <summary>
    /// Every package on disk, by its camo.json — the disk is the list, nothing else is. The default camos
    /// (sb/Win32/defaultcamos/) come first, then the user packages (sb/Win32/camos/), each root in folder order.
    /// </summary>
    public static List<PackageInfo> Scan(string p_Mod, Action<string>? p_Log = null)
    {
        var s_Packages = new List<PackageInfo>();

        foreach (var (s_Root, s_NativeRoot) in new[] { (NativesDir(p_Mod), true), (PackagesDir(p_Mod), false) })
        {
            if (!Directory.Exists(s_Root))
                continue;

            foreach (var s_Dir in Directory.GetDirectories(s_Root).OrderBy(p_D => p_D, StringComparer.OrdinalIgnoreCase))
            {
                var s_Json = Path.Combine(s_Dir, "camo.json");
                if (!File.Exists(s_Json))
                    continue;

                try
                {
                    var s_Info = JsonSerializer.Deserialize<PackageInfo>(File.ReadAllText(s_Json));
                    if (s_Info == null || s_Info.Key.Length == 0)
                        continue;

                    // The folder on disk is the identity the superbundle names are built from; a camo.json
                    // copied from another package must not point the loader at files that live somewhere else.
                    s_Info.Folder = Path.GetFileName(s_Dir);

                    // ⛔ A default camo is one BY WHERE IT LIVES AND BY WHAT IT SAYS, both: a user package
                    // dropped under defaultcamos/ would be offered without its own text (the loader never
                    // rewrites a default's row), and a default copied under camos/ would have its native
                    // identifier taken away by the collision pass. Neither is what the person dropping the
                    // folder meant, so both are reported and treated as user packages.
                    if (s_Info.Native != s_NativeRoot)
                    {
                        p_Log?.Invoke($"WARN: package {s_Info.Folder} is {(s_Info.Native ? "a default camo" : "a user camo")} " +
                                      $"but lives under {(s_NativeRoot ? "defaultcamos" : "camos")}/ — treated as a user package.");
                        s_Info.Native = false;
                    }

                    s_Info.Superbundle = StemOf(s_Info.Folder, s_NativeRoot);
                    s_Packages.Add(s_Info);
                }
                catch (Exception)
                {
                    // A package whose camo.json does not parse is not offered; Register says which.
                }
            }
        }

        return s_Packages;
    }

    /// <summary>Whether a package lives under the default camos root (its superbundle stem says so).</summary>
    public static bool IsUnderNatives(PackageInfo p_Package) =>
        p_Package.Superbundle.StartsWith("defaultcamos/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites the two files the game and the Lua read packages from, from what is on disk. Idempotent: run
    /// after every bake, or by hand after copying a package in. Returns a one-line summary for the log.
    /// </summary>
    public static string Register(string p_Mod, Action<string>? p_Log = null)
    {
        if (Validate(p_Mod) is { } s_Why)
            throw new InvalidOperationException($"'{p_Mod}' is not the camo framework: {s_Why}");

        var s_Scanned = Scan(p_Mod, p_Log);
        // ⭐ the soldier skins apart: their own identifiers, their own index — the camo index and its collision pass see
        // exactly the packages they saw before skins existed
        var s_Packages = s_Scanned.Where(p_P => p_P.Soldier == null).ToList();
        var s_Skins = s_Scanned.Where(p_P => p_P.Soldier != null).ToList();
        var s_Kits = Catalog.Kits.Load();
        ResolveIdentifierCollisions(p_Mod, s_Packages, s_Kits, p_Log);
        ResolveSkinIdentifiers(p_Mod, s_Skins, p_Log);

        WriteIndex(p_Mod, s_Packages, s_Kits);
        s_Kits.WriteLua(KitsFile(p_Mod));
        // (only where there is something to say: a framework that never held a skin gets no file of them)
        if (s_Skins.Count > 0 || File.Exists(SoldierIndexFile(p_Mod)))
            WriteSoldierIndex(p_Mod, s_Skins);
        var s_FirstPerson = WriteFirstPersonIndex(p_Mod, p_Log);
        // every survivor in the scan's order, skins included: the client only receives the superbundles mod.json declares
        var s_Declared = WriteModJson(p_Mod, s_Scanned.Where(p_P => s_Packages.Contains(p_P) || s_Skins.Contains(p_P)).ToList(), s_FirstPerson);

        var s_Natives = s_Packages.Count(p_P => p_P.Native);
        var s_Summary = $"Registered {s_Packages.Count - s_Natives} camo package(s)" +
                        (s_Skins.Count > 0 ? $", {s_Skins.Count} soldier skin(s)" : "") +
                        (s_FirstPerson ? ", the first-person split" : "") +
                        $" and {s_Natives} default camo(s) in " +
                        $"'{Path.GetFileName(p_Mod)}' ({s_Declared} superbundle(s) declared). Restart the server to load them.";
        p_Log?.Invoke(s_Summary);
        return s_Summary;
    }

    /// <summary>
    /// A soldier skin's identifier is its own name, hashed (CamoBaker.IdentifierOfSkin) — derived the way a camo's is, from another unlock
    /// name, so a skin and a weapon camo of the same name never meet. A package whose identifier was written by hand is put back on its own;
    /// two copies of one skin (the same name) cannot both be offered: the first in folder order is.
    /// </summary>
    private static void ResolveSkinIdentifiers(string p_Mod, List<PackageInfo> p_Skins, Action<string>? p_Log)
    {
        var s_Holders = new Dictionary<uint, PackageInfo>();
        foreach (var s_Skin in p_Skins)
        {
            var s_Own = CamoBaker.IdentifierOfSkin(s_Skin.Name);
            if (s_Skin.Identifier != s_Own)
            {
                p_Log?.Invoke($"soldier skin {s_Skin.Folder}: identifier {s_Skin.Identifier} is not its own — it is now {s_Own} (from '{s_Skin.Name}').");
                s_Skin.Identifier = s_Own;
                WriteIdentifier(p_Mod, s_Skin);
            }

            if (s_Holders.TryGetValue(s_Own, out var s_Holder))
            {
                p_Log?.Invoke($"WARN: soldier skin {s_Skin.Folder} is another copy of '{s_Holder.Name}' ({s_Holder.Folder}) — only the first is offered.");
                s_Skin.Identifier = 0;
                continue;
            }

            s_Holders[s_Own] = s_Skin;
        }

        p_Skins.RemoveAll(p_S => p_S.Identifier == 0);
    }

    /// <summary>
    /// Removes baked USER packages from the framework (the bake's counterpart) and
    /// registers what is left, once. Each package folder goes to the Windows Recycle Bin, so a deletion can be
    /// undone from there; only when the Recycle Bin refuses (a drive without one) is the folder deleted
    /// outright, and the log says which happened. A default camo (under defaultcamos/) is never removed here:
    /// it is refused by name and reported.
    /// </summary>
    /// <returns>The folders that were removed.</returns>
    public static List<string> DeletePackages(string p_Mod, IReadOnlyCollection<string> p_Folders, Action<string>? p_Log = null)
    {
        if (Validate(p_Mod) is { } s_Why)
            throw new InvalidOperationException($"'{p_Mod}' is not the camo framework: {s_Why}");

        var s_Removed = new List<string>();
        foreach (var s_Folder in p_Folders)
        {
            var s_Name = Path.GetFileName(s_Folder.Trim().TrimEnd('\\', '/'));
            if (s_Name.Length == 0 || s_Name == "." || s_Name == "..")
            {
                p_Log?.Invoke($"WARN: '{s_Folder}' is not a package folder name — skipped.");
                continue;
            }

            if (Directory.Exists(PackageDir(p_Mod, s_Name, true)) && !Directory.Exists(PackageDir(p_Mod, s_Name)))
            {
                p_Log?.Invoke($"WARN: '{s_Name}' is one of the default camos (sb/Win32/defaultcamos/) — not deleted; " +
                              "the defaults are rebuilt by the studio, not removed one by one.");
                continue;
            }

            var s_Dir = PackageDir(p_Mod, s_Name);
            if (!Directory.Exists(s_Dir))
            {
                p_Log?.Invoke($"WARN: no package folder '{s_Name}' under sb/Win32/camos/ — skipped.");
                continue;
            }

            if (!File.Exists(Path.Combine(s_Dir, "camo.json")))
            {
                p_Log?.Invoke($"WARN: '{s_Name}' has no camo.json — not a package the studio made; not deleted.");
                continue;
            }

            var s_How = RemoveFolder(s_Dir);
            s_Removed.Add(s_Name);
            p_Log?.Invoke($"Deleted package '{s_Name}' ({s_How}).");
        }

        if (s_Removed.Count > 0)
            Register(p_Mod, p_Log);

        return s_Removed;
    }

    /// <summary>To the Recycle Bin when there is one; outright otherwise. Returns which.</summary>
    private static string RemoveFolder(string p_Dir)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(p_Dir,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            if (!Directory.Exists(p_Dir))
                return "moved to the Recycle Bin";
        }
        catch (Exception)
        {
            // No Recycle Bin on this drive, or the shell refused: fall through to a plain delete.
        }

        Directory.Delete(p_Dir, true);
        return "deleted outright — this drive has no Recycle Bin";
    }

    /// <summary>
    /// Two packages on one identifier IN THE SAME KIT would give the menu two rows with one description, and
    /// the loader would drop the second. It happens when two studios name a camo alike (the seat is derived
    /// from the name, and one studio cannot know the other's packages), or when a package lands on a seat a
    /// native row wears. Two packages on one identifier in DIFFERENT kits are fine — that is the reuse the
    /// kit slots exist for.
    ///
    /// Resolved here, deterministically, from what is on disk: packages are taken in folder order, the first
    /// on a slot keeps it, a later one is moved to the seat its own name probes to among the free ones — the
    /// same probing the studio uses — and its camo.json is rewritten so the move sticks for good. A player
    /// who had the MOVED camo equipped sees another camo once; the alternative was not seeing it at all.
    /// Two copies of one camo (same name) cannot both be offered and are reported instead.
    /// </summary>
    private static void ResolveIdentifierCollisions(string p_Mod, List<PackageInfo> p_Packages, Catalog.Kits p_Kits, Action<string>? p_Log)
    {
        var s_Holders = new Dictionary<(uint Identifier, string Kit), PackageInfo>();
        var s_Placed = new List<PackageInfo>();
        var s_Moved = new List<PackageInfo>();

        foreach (var s_Package in p_Packages)
        {
            var s_Native = NativeIdentifiers.Contains(s_Package.Identifier);

            // A default camo on its own native row is exactly where it belongs (the scan lists the defaults
            // first, so it is the holder before any user package on that seat is looked at), in every kit.
            // One on any other identifier is not offered at all: the loader would rewrite nothing for it,
            // and the row it sat on would name another camo.
            if (s_Package.Native)
            {
                if (!s_Native)
                {
                    p_Log?.Invoke($"WARN: default camo {s_Package.Folder} carries identifier {s_Package.Identifier}, which is " +
                                  "not one of the game's own camo rows — it is not offered (rebake it with the studio).");
                    s_Package.Identifier = 0;
                    continue;
                }

                if (s_Holders.GetValueOrDefault((s_Package.Identifier, Catalog.Kits.Names[0])) is { } s_NativeHolder)
                {
                    p_Log?.Invoke($"WARN: default camo {s_Package.Folder} is another copy of '{s_NativeHolder.Name}' ({s_NativeHolder.Folder}) " +
                                  "— only the first is offered.");
                    s_Package.Identifier = 0;
                    continue;
                }

                foreach (var s_Kit in Catalog.Kits.Names)
                    s_Holders[(s_Package.Identifier, s_Kit)] = s_Package;
                s_Package.Kits = Catalog.Kits.Names.ToList();
                continue;
            }

            // 2026-09-20: the identifier IS the camo's name, hashed. A package baked before that carries a
            // borrowed seat in its camo.json, and the mod would compute a different number from the same
            // name -- so it is recomputed here and written back, once, and index/camo.json/mod all agree.
            var s_Own = CamoBaker.IdentifierOfCamo(s_Package.Name);

            if (s_Package.Identifier != s_Own)
            {
                p_Log?.Invoke($"package {s_Package.Folder}: identifier {s_Package.Identifier} was a borrowed seat — " +
                              $"it is now its own ({s_Own}, from '{s_Package.Name}').");
                s_Package.Identifier = s_Own;
                WriteIdentifier(p_Mod, s_Package);
            }

            // The kits this package occupies, written down so the index says the same thing the seat does.
            var s_PackageKits = KitsOf(s_Package, p_Kits);
            s_Package.Kits = s_PackageKits.ToList();

            var s_Holder = s_PackageKits
                .Select(p_K => s_Holders.GetValueOrDefault((s_Package.Identifier, p_K)))
                .FirstOrDefault(p_H => p_H != null);

            if (s_Holder == null && !s_Native)
            {
                foreach (var s_Kit in s_PackageKits)
                    s_Holders[(s_Package.Identifier, s_Kit)] = s_Package;
                s_Placed.Add(s_Package);
                continue;
            }

            if (s_Holder != null && !s_Holder.Native &&
                string.Equals(s_Holder.Name, s_Package.Name, StringComparison.OrdinalIgnoreCase))
            {
                p_Log?.Invoke($"WARN: package {s_Package.Folder} is another copy of '{s_Holder.Name}' ({s_Holder.Folder}) " +
                              "— only the first is offered.");
                continue;
            }

            // 2026-09-20: there is nothing to MOVE any more. An identifier is derived from the camo's own
            // name, so two DIFFERENT camos cannot collide; what reaches here either carries a hand-edited
            // number or is a package whose name hashes onto a native row. Both are refused rather than
            // quietly rehoused on something that belongs to the game.
            p_Log?.Invoke($"WARN: package {s_Package.Folder} carries identifier {s_Package.Identifier}, which is " +
                          $"{(s_Native ? "one of the game's own camo rows" : "already held by " + s_Holder!.Folder + " in the same kit")}" +
                          " — it is not offered (rebake it: the studio derives the identifier from the camo's name).");
            s_Package.Identifier = 0;
        }

        foreach (var s_Package in s_Moved)
            WriteIdentifier(p_Mod, s_Package);

        // What was refused above is not offered: the index and mod.json are written from the survivors.
        p_Packages.RemoveAll(p_P => p_P.Identifier == 0);
    }

    /// <summary>Rewrites one field of a package's camo.json, leaving the rest as the baker wrote it.</summary>
    private static void WriteIdentifier(string p_Mod, PackageInfo p_Package)
    {
        var s_Path = Path.Combine(PackageDir(p_Mod, p_Package.Folder, IsUnderNatives(p_Package)), "camo.json");
        var s_Root = JsonNode.Parse(File.ReadAllText(s_Path))?.AsObject()
                     ?? throw new InvalidDataException($"'{s_Path}' is not a package description.");

        s_Root["identifier"] = p_Package.Identifier;
        File.WriteAllText(s_Path, JsonSerializer.Serialize(s_Root, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }), new UTF8Encoding(false));
    }

    /// <summary>The Lua index: one table per package, only fields the loader reads.</summary>
    private static void WriteIndex(string p_Mod, IReadOnlyList<PackageInfo> p_Packages, Catalog.Kits p_Kits)
    {
        var s_Text = new StringBuilder();
        s_Text.AppendLine("-- GENERATED by Rime Camo Studio from the packages under sb/Win32/defaultcamos/ and sb/Win32/camos/ — do not edit by hand.");
        s_Text.AppendLine("-- One entry per package, the default camos first; regenerated on every bake and by Register. Fields:");
        s_Text.AppendLine("--   key         short id of the camo (the variation names end in _CAMO_<key>)");
        s_Text.AppendLine("--   identifier  the UI description the row wears (the loadout stores the camo by it): a default camo's");
        s_Text.AppendLine("--               is the game's own row for that pattern, a user camo's is a borrowed one the loader rewrites");
        s_Text.AppendLine("--   kits        the kit lists (assault/engineer/support/recon, see kits.lua) the identifier means THIS camo");
        s_Text.AppendLine("--               in: two packages may share an identifier when their kits differ (the row's label is");
        s_Text.AppendLine("--               written per kit and holds for the whole arrow cycle); never in the same kit");
        s_Text.AppendLine("--   superbundle stem of its superbundles: <stem>/pack (CAS, always), <stem>/packnc and <stem>/chunks (when carried),");
        s_Text.AppendLine("--               <stem>/shaders (when the camo ships its own preset: mounted and PREPENDED, like the AUG's)");
        s_Text.AppendLine("--   native      true for a default camo (the game's pattern on every weapon; never rewritten, never swapped)");
        s_Text.AppendLine("--   shader      true when <stem>/shaders exists (the camo's own first-person preset)");
        s_Text.AppendLine("--   pattern     true when packnc carries the camo's own texture (false = a game texture, referenced)");
        s_Text.AppendLine("--   stickers    true when packnc carries sticker layers (one texture per weapon that has stickers) or the");
        s_Text.AppendLine("--               pictures of an edited graph (an attachment's as-shipped graph);");
        s_Text.AppendLine("--               like pattern, it means <stem>/packncv must be loaded BEFORE the weapons' materials bind");
        s_Text.AppendLine("--   thumbnail   the UI texture of the row's picture, or nil");
        s_Text.AppendLine("--   weapons     catalogue folders the camo is offered on");
        s_Text.AppendLine("--   accessories the accessories the camo repaints: per accessory, the host weapon's folder, the unlock's");
        s_Text.AppendLine("--               short name and { mesh = the game's mesh, clone = the clone the package ships for it }.");
        s_Text.AppendLine("--               The loader points the weapon's socket objects at those clones (the engine builds a socket");
        s_Text.AppendLine("--               object from its own asset fields under variation 0); absent = every socket stays vanilla");
        s_Text.AppendLine("--   vehicles    the vehicles the camo paints: { blueprint = the vehicle, class = the SID of its customization");
        s_Text.AppendLine("--               row, clone = the clone of its body mesh the package ships }. The vehicle camo windows list the");
        s_Text.AppendLine("--               camo under that class, and the player's client points his vehicles at the clone he chose.");
        s_Text.AppendLine("--               pieces = { { name, mesh = the rigid piece the package ships, holder = the blueprint component that");
        s_Text.AppendLine("--               moves it, hide = the components (comma-separated) whose stock parts it replaces for the driver who");
        s_Text.AppendLine("--               wears the camo, mirror = true when the game shows and hides those stock parts by itself } },");
        s_Text.AppendLine("--               driver = the driver seat's own part, where the switch that decides the camo hangs (seat 1 only)");
        s_Text.AppendLine("--               bundle = the package bundle that carries the vehicle, levels = the level bundles that carry the");
        s_Text.AppendLine("--               vehicle: the loader puts its bundle right behind whichever of them the level loads");
        s_Text.AppendLine("return {");

        foreach (var s_Package in p_Packages)
        {
            s_Text.AppendLine("\t{");
            s_Text.AppendLine($"\t\tkey = {LuaQuote(s_Package.Key)},");
            s_Text.AppendLine($"\t\tname = {LuaQuote(s_Package.Name)},");
            s_Text.AppendLine($"\t\ttext = {LuaQuote(s_Package.Description)},");
            s_Text.AppendLine($"\t\tfamily = {LuaQuote(s_Package.Family)},");

            // Only when it is FALSE: an index of packages that all paint their weapons reads the same as before.
            if (!s_Package.Body)
                s_Text.AppendLine("\t\tbody = false,   -- a camo OF A PIECE: no weapon body, no camo row");
            s_Text.AppendLine($"\t\tidentifier = {s_Package.Identifier},");
            s_Text.AppendLine("\t\tkits = { " + string.Join(", ", KitsOf(s_Package, p_Kits).Select(LuaQuote)) + " },");
            s_Text.AppendLine($"\t\tsuperbundle = {LuaQuote(s_Package.Superbundle)},");
            s_Text.AppendLine($"\t\tnative = {(s_Package.Native ? "true" : "false")},");
            // (only when TRUE: the look its weapons wear with no camo picked — dressed by the framework, in no camo window)
            if (s_Package.DefaultLook)
                s_Text.AppendLine("\t\tdefaultLook = true,   -- the look these weapons wear with NO camo picked: in no camo window");
            s_Text.AppendLine($"\t\tshader = {(s_Package.Shader ? "true" : "false")},");
            s_Text.AppendLine($"\t\tpattern = {(s_Package.PatternTexture != null ? "true" : "false")},");
            s_Text.AppendLine($"\t\tstickers = {(s_Package.Stickers ? "true" : "false")},");
            s_Text.AppendLine($"\t\tthumbnail = {(s_Package.Thumbnail != null ? LuaQuote(s_Package.Thumbnail) : "nil")},");
            s_Text.AppendLine("\t\tweapons = { " +
                              string.Join(", ", s_Package.Weapons.Select(p_W => LuaQuote(p_W.Folder))) + " },");

            // Only written when the package has accessories: an empty table would say the same as none, and
            // the index is a file people read.
            if (s_Package.Accessories.Count > 0)
            {
                s_Text.AppendLine("\t\taccessories = {");
                foreach (var s_Accessory in s_Package.Accessories)
                {
                    s_Text.AppendLine($"\t\t\t{{ weapon = {LuaQuote(s_Accessory.Weapon)}, tag = {LuaQuote(s_Accessory.Tag)}, meshes = {{");
                    foreach (var s_Mesh in s_Accessory.Meshes)
                        s_Text.AppendLine($"\t\t\t\t{{ mesh = {LuaQuote(s_Mesh.Mesh)}, clone = {LuaQuote(s_Mesh.Clone)} }},");

                    s_Text.AppendLine("\t\t\t} },");
                }

                s_Text.AppendLine("\t\t},");
            }

            // Only when it has any, as above.
            if (s_Package.Vehicles.Count > 0)
            {
                s_Text.AppendLine("\t\tvehicles = {");
                foreach (var s_Vehicle in s_Package.Vehicles)
                {
                    // pieces only when it has any: a package baked before them reads as it always did
                    var s_Pieces = s_Vehicle.Pieces.Count == 0
                        ? ""
                        : (s_Vehicle.Driver.Length > 0 ? $", driver = {LuaQuote(s_Vehicle.Driver)}" : "") +
                          ", pieces = { " + string.Join(", ", s_Vehicle.Pieces.Select(p_P =>
                              $"{{ name = {LuaQuote(p_P.Name)}, mesh = {LuaQuote(p_P.Mesh)}, holder = {LuaQuote(p_P.Holder)}, " +
                              $"hide = {LuaQuote(p_P.Hide)}{(p_P.Mirror ? ", mirror = true" : "")} }}")) + " }";
                    // its own bundle and the level bundles it goes behind, when the package has them
                    var s_Bundle = s_Vehicle.Bundle.Length == 0
                        ? ""
                        : $", bundle = {LuaQuote(s_Vehicle.Bundle)}, levels = {{ {string.Join(", ", s_Vehicle.LevelBundles.Select(LuaQuote))} }}";
                    s_Text.AppendLine($"\t\t\t{{ blueprint = {LuaQuote(s_Vehicle.Blueprint)}, class = {LuaQuote(s_Vehicle.Class)}, " +
                                      $"clone = {LuaQuote(s_Vehicle.Clone)}{s_Pieces}{s_Bundle} }},");
                }

                s_Text.AppendLine("\t\t},");
            }

            s_Text.AppendLine("\t},");
        }

        s_Text.AppendLine("}");

        var s_Path = IndexFile(p_Mod);
        Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
        File.WriteAllText(s_Path, s_Text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>The soldier skins' Lua index: one table per skin, what the framework's soldier module reads (see PackageSoldier).</summary>
    private static void WriteSoldierIndex(string p_Mod, IReadOnlyList<PackageInfo> p_Skins)
    {
        var s_Text = new StringBuilder();
        s_Text.AppendLine("-- GENERATED by Rime Camo Studio from the soldier skin packages under sb/Win32/camos/ — do not edit by hand.");
        s_Text.AppendLine("-- One entry per skin; regenerated on every bake and by Register. A skin dresses ONE soldier, part by part. Fields:");
        s_Text.AppendLine("--   key         short id of the skin (its textures are Characters/Custom/Skin_<key>_…)");
        s_Text.AppendLine("--   identifier  the skin's own number, from its name (the unlock name Characters/Custom/U_SKIN_<key>)");
        s_Text.AppendLine("--   superbundle stem of its superbundles: <stem>/packnc (its textures in <stem>/packncv — load it BEFORE the soldiers'");
        s_Text.AppendLine("--               materials bind — and the row's picture in <stem>/packncui), <stem>/chunks (the picture's pixels),");
        s_Text.AppendLine("--               <stem>/pack (CAS: the textures' partitions, so they can be found by name)");
        s_Text.AppendLine("--   textures    true when packncv carries textures (a skin of numbers only carries none)");
        s_Text.AppendLine("--   shader      true when <stem>/shaders exists (the parts' own shaders: mounted and PREPENDED, like a camo's)");
        s_Text.AppendLine("--   thumbnail   the UI texture of the row's picture, or nil");
        s_Text.AppendLine("--   soldier     the studio's soldier key (US_Assault, RU_Recon_XP4); team, kit, expansion (base/xp4), kitAsset;");
        s_Text.AppendLine("--               appearance = the game's appearance he was previewed in, whose entries the skin's own are copied from;");
        s_Text.AppendLine("--               arms = his first-person arms mesh, trousers = the material ids of it that are the LOWER body's");
        s_Text.AppendLine("--   bundle      <stem>/<bundle>: the skin's variation database entries (baked), loaded right behind the first of");
        s_Text.AppendLine("--               levels = the level bundles that carry every mesh they point at (as a vehicle's veh0); entries = the");
        s_Text.AppendLine("--               variation names it has an entry for (nil: a package baked before — its soldier is not dressed)");
        s_Text.AppendLine("--   parts       head / upper / lower, only the ones the skin changes: meshes = the part's third-person meshes;");
        s_Text.AppendLine("--               appearance = the game's look the part starts from (its entries copied from that look's, the lower");
        s_Text.AppendLine("--               body's first-person trousers from that look's arms), \"\" = the soldier's appearance above;");
        s_Text.AppendLine("--               mask = stock (where the game's mask puts the camo) | white (all the cloth) | picture (the user's);");
        s_Text.AppendLine("--               textures = { parameter = texture } bound on the materials of the part that wear the camo cloth");
        s_Text.AppendLine("--               (their entry binds CamoTile) and on the ones that draw with a copy below; values = { constant =");
        s_Text.AppendLine("--               \"x,y,z,w\" } written over the same materials; shaders = { { shader = the game's shader, clone = its copy,");
        s_Text.AppendLine("--               declares = { picture parameters only the copy reads } } }: the part's materials that wear that shader");
        s_Text.AppendLine("--               draw with the copy (a graph with nodes or wires of its own); thumbnail = the UI texture of the part's");
        s_Text.AppendLine("--               own cell on its tab of the soldier's window, or nil (the skin's shows there); family = the family");
        s_Text.AppendLine("--               that cell is filed under, or \"\" (the skin's); name = what that cell reads, or \"\" (the skin's);");
        s_Text.AppendLine("--               text = what that cell shows in the INFO box, or \"\" (the skin's)");
        s_Text.AppendLine("-- ⭐ A skin that goes to several soldiers (per part, the ones ticked at its bake) is one package and one entry per soldier: the");
        s_Text.AppendLine("--   others with a key and an identifier of their own (<key>_<SOLDIER>: the window keys its table by identifier), the same");
        s_Text.AppendLine("--   superbundle, textures and pictures, and each its own bundle of entries.");
        s_Text.AppendLine("return {");

        // the soldier the skin was made on, then the others it goes to — each an entry of its own
        foreach (var (s_Skin, s_Soldier, s_Key, s_Identifier) in p_Skins.SelectMany(p_S =>
                     new[] { (p_S, p_S.Soldier!, p_S.Key, p_S.Identifier) }.Concat((p_S.Also ?? new List<PackageSoldier>())
                         .Where(p_O => p_O.SkinKey != null && p_O.Identifier != null)
                         .Select(p_O => (p_S, p_O, p_O.SkinKey!, p_O.Identifier!.Value)))))
        {
            s_Text.AppendLine("\t{");
            s_Text.AppendLine($"\t\tkey = {LuaQuote(s_Key)},");
            s_Text.AppendLine($"\t\tname = {LuaQuote(s_Skin.Name)},");
            s_Text.AppendLine($"\t\ttext = {LuaQuote(s_Skin.Description)},");
            s_Text.AppendLine($"\t\tfamily = {LuaQuote(s_Skin.Family)},");
            s_Text.AppendLine($"\t\tidentifier = {s_Identifier},");
            s_Text.AppendLine($"\t\tsuperbundle = {LuaQuote(s_Skin.Superbundle)},");
            s_Text.AppendLine($"\t\ttextures = {(s_Skin.Stickers ? "true" : "false")},");
            s_Text.AppendLine($"\t\tshader = {(s_Skin.Shader ? "true" : "false")},");
            s_Text.AppendLine($"\t\tthumbnail = {(s_Skin.Thumbnail != null ? LuaQuote(s_Skin.Thumbnail) : "nil")},");
            s_Text.AppendLine($"\t\tsoldier = {LuaQuote(s_Soldier.Key)}, team = {LuaQuote(s_Soldier.Team)}, kit = {LuaQuote(s_Soldier.Kit)}, " +
                              $"expansion = {LuaQuote(s_Soldier.Expansion)},");
            s_Text.AppendLine($"\t\tkitAsset = {LuaQuote(s_Soldier.KitAsset)},");
            s_Text.AppendLine($"\t\tappearance = {LuaQuote(s_Soldier.Appearance)},");
            s_Text.AppendLine($"\t\tarms = {LuaQuote(s_Soldier.Arms)}, trousers = {{ {string.Join(", ", s_Soldier.Trousers)} }},");
            if (s_Soldier.Bundle != null)
            {
                s_Text.AppendLine($"\t\tbundle = {LuaQuote(s_Soldier.Bundle)},");
                s_Text.AppendLine("\t\tlevels = { " + string.Join(", ", (s_Soldier.Levels ?? new List<string>()).Select(LuaQuote)) + " },");
                s_Text.AppendLine("\t\tentries = { " + string.Join(", ", (s_Soldier.Entries ?? new List<string>()).Select(LuaQuote)) + " },");
            }

            s_Text.AppendLine("\t\tparts = {");
            foreach (var s_Part in s_Soldier.Parts)
            {
                s_Text.AppendLine($"\t\t\t{s_Part.Part} = {{");
                s_Text.AppendLine($"\t\t\t\tmeshes = {{ {string.Join(", ", s_Part.Meshes.Select(LuaQuote))} }},");
                s_Text.AppendLine($"\t\t\t\tappearance = {LuaQuote(s_Part.Appearance ?? "")},");
                s_Text.AppendLine($"\t\t\t\tmask = {LuaQuote(s_Part.Mask)},");
                s_Text.AppendLine("\t\t\t\ttextures = { " + string.Join(", ", s_Part.Textures.OrderBy(p_T => p_T.Key, StringComparer.Ordinal)
                    .Select(p_T => $"{p_T.Key} = {LuaQuote(p_T.Value)}")) + " },");
                s_Text.AppendLine("\t\t\t\tvalues = { " + string.Join(", ", s_Part.Values.OrderBy(p_V => p_V.Key, StringComparer.Ordinal)
                    .Select(p_V => $"{p_V.Key} = {LuaQuote(p_V.Value)}")) + " },");
                s_Text.AppendLine("\t\t\t\tshaders = { " + string.Join(", ", s_Part.Shaders.Select(p_S =>
                    $"{{ shader = {LuaQuote(p_S.Shader)}, clone = {LuaQuote(p_S.Clone)}, declares = {{ {string.Join(", ", p_S.Declares.Select(LuaQuote))} }} }}")) + " },");
                s_Text.AppendLine($"\t\t\t\tthumbnail = {(s_Part.Thumbnail != null ? LuaQuote(s_Part.Thumbnail) : "nil")},");
                s_Text.AppendLine($"\t\t\t\tfamily = {LuaQuote(s_Part.Family ?? "")},");
                s_Text.AppendLine($"\t\t\t\tname = {LuaQuote(s_Part.Name ?? "")},");
                s_Text.AppendLine($"\t\t\t\ttext = {LuaQuote(s_Part.Description ?? "")},");
                s_Text.AppendLine("\t\t\t},");
            }

            s_Text.AppendLine("\t\t},");
            s_Text.AppendLine("\t},");
        }

        s_Text.AppendLine("}");

        var s_Path = SoldierIndexFile(p_Mod);
        Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
        File.WriteAllText(s_Path, s_Text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// The first-person split's Lua index, from its package's description: whether the package is there (pack.sb and firstperson.json). With
    /// none, a framework that had one gets an index saying so (nil: no look is split), never a stale list of bundles that are gone.
    /// </summary>
    private static bool WriteFirstPersonIndex(string p_Mod, Action<string>? p_Log)
    {
        var s_Dir = FirstPersonDir(p_Mod);
        var s_Description = Path.Combine(s_Dir, FirstPersonDescription);
        FirstPersonPackage? s_Package = null;
        if (File.Exists(s_Description) && File.Exists(Path.Combine(s_Dir, "pack.sb")))
        {
            try
            {
                s_Package = JsonSerializer.Deserialize<FirstPersonPackage>(File.ReadAllText(s_Description));
            }
            catch (Exception s_Exception)
            {
                p_Log?.Invoke($"WARN: {s_Description} does not parse ({s_Exception.Message}) — the first-person split is not offered.");
            }
        }

        var s_Path = FirstPersonIndexFile(p_Mod);
        if (s_Package == null && !File.Exists(s_Path))
            return false;

        var s_Text = new StringBuilder();
        s_Text.AppendLine("-- GENERATED by Rime Camo Studio from sb/Win32/soldiers/firstperson/firstperson.json — do not edit by hand.");
        s_Text.AppendLine("-- The framework's first-person split (the trousers of the arms mesh follow the legs): per pack and arms mesh, a bundle");
        s_Text.AppendLine("-- with two copies of each variation the looks wear, Characters/Soldiers/Skin/Arms1P_<source NameHash>_Torso (the arms");
        s_Text.AppendLine("-- without the trousers) and _Legs (only the trousers). Fields: superbundle = stem of <stem>/pack (CAS); per group:");
        s_Text.AppendLine("--   bundle   <stem>/<bundle>, loaded right behind the first of levels = the level bundles that carry the mesh");
        s_Text.AppendLine("--   pack     base | xp4;  mesh = the arms mesh;  trousers = its material ids that are the trousers");
        s_Text.AppendLine("--   sources  the NameHashes of the variations copied (0 = the mesh linked bare, its entry 0)");
        if (s_Package == null)
        {
            s_Text.AppendLine("-- (no package: no look is split)");
            s_Text.AppendLine("return nil");
        }
        else
        {
            s_Text.AppendLine("return {");
            s_Text.AppendLine($"\tsuperbundle = {LuaQuote(s_Package.Superbundle)},");
            s_Text.AppendLine("\tgroups = {");
            foreach (var s_Group in s_Package.Groups)
            {
                s_Text.AppendLine("\t\t{");
                s_Text.AppendLine($"\t\t\tbundle = {LuaQuote(s_Group.Bundle)}, pack = {LuaQuote(s_Group.Pack)},");
                s_Text.AppendLine($"\t\t\tmesh = {LuaQuote(s_Group.Mesh)}, trousers = {{ {string.Join(", ", s_Group.Trousers)} }},");
                s_Text.AppendLine($"\t\t\tsources = {{ {string.Join(", ", s_Group.Sources)} }},");
                s_Text.AppendLine("\t\t\tlevels = { " + string.Join(", ", s_Group.Levels.Select(LuaQuote)) + " },");
                s_Text.AppendLine("\t\t},");
            }

            s_Text.AppendLine("\t},");
            s_Text.AppendLine("}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(s_Path)!);
        File.WriteAllText(s_Path, s_Text.ToString(), new UTF8Encoding(false));
        return s_Package != null;
    }

    /// <summary>
    /// The Superbundles list of mod.json: the mod's own entries kept as they are, one block per package
    /// appended — only the files that exist, because a declared superbundle that is not there is the same
    /// endless "loading terrain" as an undeclared one. Backed up before the first change.
    /// </summary>
    private static int WriteModJson(string p_Mod, IReadOnlyList<PackageInfo> p_Packages, bool p_FirstPerson = false)
    {
        var s_Path = ModJson(p_Mod);
        var s_Original = File.ReadAllText(s_Path);
        var s_Root = JsonNode.Parse(s_Original) as JsonObject
                     ?? throw new InvalidDataException("mod.json is not a JSON object.");

        var s_Kept = new List<string>();
        if (s_Root["Superbundles"] is JsonArray s_Existing)
            foreach (var s_Entry in s_Existing)
                if (s_Entry?.GetValue<string>() is { } s_Name &&
                    !s_Name.StartsWith(SuperbundlePrefix, StringComparison.OrdinalIgnoreCase) &&
                    !s_Name.StartsWith(NativePrefix, StringComparison.OrdinalIgnoreCase) &&
                    !s_Name.StartsWith($"Win32/{FirstPersonStem}/", StringComparison.OrdinalIgnoreCase))
                    s_Kept.Add(s_Name);

        var s_Declared = 0;
        var s_List = new JsonArray();
        foreach (var s_Name in s_Kept)
            s_List.Add(s_Name);

        foreach (var s_Package in p_Packages)
        foreach (var s_Stem in new[] { "pack", "packnc", "chunks", "shaders" })
        {
            var s_Native = IsUnderNatives(s_Package);
            if (!File.Exists(Path.Combine(PackageDir(p_Mod, s_Package.Folder, s_Native), s_Stem + ".sb")))
                continue;

            s_List.Add($"Win32/{s_Package.Superbundle}/{s_Stem}");
            s_Declared++;
        }

        // the first-person split's one superbundle, when its package is there
        if (p_FirstPerson && File.Exists(Path.Combine(FirstPersonDir(p_Mod), "pack.sb")))
        {
            s_List.Add($"Win32/{FirstPersonStem}/pack");
            s_Declared++;
        }

        s_Root["Superbundles"] = s_List;

        // Through the serializer, not JsonNode.ToJsonString: on .NET 8 the latter marks fresh options
        // read-only before a type resolver is attached and throws ("must specify a TypeInfoResolver").
        var s_Text = JsonSerializer.Serialize(s_Root, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Apostrophes and accents stay as typed: the default encoder turns them into \u escapes, which
            // is valid JSON and unreadable in a file people open.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        if (s_Text == s_Original)
            return s_Declared;

        File.Copy(s_Path, s_Path + $".bak_{DateTime.Now:yyyyMMdd_HHmmss}", true);
        File.WriteAllText(s_Path, s_Text, new UTF8Encoding(false));
        return s_Declared;
    }

    /// <summary>A Lua string literal: backslashes, quotes and line breaks escaped, nothing else touched.</summary>
    internal static string LuaQuote(string p_Text) =>
        "\"" + p_Text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
}
