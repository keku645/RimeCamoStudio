using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// One vehicle that can carry a camo, as read out of the game's own data: the customization asset its
/// blueprint declares, the category that asset belongs to (Land / Air — the two tabs the game shows),
/// the body mesh a camo is painted on and what hangs under its kitunlock folder.
///
/// ⛔ MEASURED, NEVER NAMED BY HAND. The link vehicle → category is the customization asset the
/// BLUEPRINT names (the T-90 declares gameplay/vehicles/mbtcustomization); the body mesh is the one
/// whose materials wear a vehicle preset. Both were read off the whole-game dump — a folder rule
/// merges the LAV-25 with the LAV-AD, which share a folder and are two different vehicles.
/// </summary>
public sealed class VehicleEntry
{
    /// <summary>Where the game stores it: "t90", "xp3/m1128-stryker".</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";

    /// <summary>What the user reads: "T-90A", "M1128 Stryker".</summary>
    [JsonPropertyName("display")] public string Display { get; set; } = "";

    /// <summary>e.g. "gameplay/vehicles/mbtcustomization" — the table that holds its gadget rows.</summary>
    [JsonPropertyName("customization")] public string Customization { get; set; } = "";

    /// <summary>"VehicleCategory_Land" or "VehicleCategory_Air": the game's own two tabs.</summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";

    /// <summary>The row the game offers it under: MBT, IFV, Jet, Attack helicopter…</summary>
    [JsonPropertyName("categoryDisplay")] public string CategoryDisplay { get; set; } = "";

    /// <summary>The game's own string id for the category, for the day the UI shows it translated.</summary>
    [JsonPropertyName("nameSid")] public string NameSid { get; set; } = "";

    [JsonPropertyName("blueprints")] public List<string> Blueprints { get; set; } = new();

    /// <summary>The body meshes of the vehicle itself — what a camo is put on.</summary>
    [JsonPropertyName("meshes")] public List<string> Meshes { get; set; } = new();

    /// <summary>
    /// The same vehicle built for another mode (coop, AI, the air-drop version). Not offered as its own
    /// entry — the list would double in length with duplicates — but a bake still has to cover them or
    /// the camo stops at the mode boundary.
    /// </summary>
    [JsonPropertyName("variantMeshes")] public List<string> VariantMeshes { get; set; } = new();

    /// <summary>
    /// The INTERIOR cockpit — the model the game draws around the pilot, a mesh of its own, apart from the
    /// cockpit seen from outside that is part of the body (aircraft have two distinct meshes). Read off the
    /// blueprint's `CockpitMesh` (an ObjectBlueprint whose MeshProxyEntityData names the mesh), resolved by
    /// GUID over the game's dump — never by name. Empty for a land vehicle; all eight aircraft have one.
    /// </summary>
    [JsonPropertyName("cockpitMesh")] public string CockpitMesh { get; set; } = "";

    /// <summary>
    /// The composite parts the game draws only while a rotor SPINS: every `RotorComponentData` of the
    /// blueprints names a `LowRpmModel.PartIndex` (the still blades) and a `HighRpmModel.PartIndex` (the
    /// blur disc and a high-speed hub, swapped in above `ChangeModelRpm`); these are the high ones whose low
    /// part differs. The body, variant and cockpit meshes share the numbering (measured on the four
    /// helicopters, 2026-09-22). Written by the catalogue patcher from the game's dump; empty for anything
    /// without a rotor (a jet's fan has low == high).
    /// </summary>
    [JsonPropertyName("rotorSpinParts")] public List<int> RotorSpinParts { get; set; } = new();

    /// <summary>
    /// The body's camo PIECES (option B): each a group of composite parts that move together, cut out of the
    /// body as a rigid mesh the camo package ships, hung from the component that moves those parts and switched on by the
    /// driver's camo unlock while that component's stock part is hidden. MEASURED BY LOOKING, like the reactive armour's parts:
    /// a part's number only means something once its picture says what it is (on the M1A2 the boxes of the dump named a wheel
    /// "hull" and the machine-gun mount "turret"; part 59 photographed alone is the whole turret).
    /// </summary>
    [JsonPropertyName("pieces")] public List<VehiclePiece> Pieces { get; set; } = new();

    /// <summary>
    /// The DRIVER seat's own part (the PartComponentData under the PlayerEntryComponentData whose EntryOrderNumber is 0): the only
    /// place a switch is asked about the driver alone. MEASURED (re16/re20, 2026-09-24): a seat, when it gets its player, asks the
    /// switches that hang from ITS part and turns them on if that player carries the unlock — on the M1A2 the turret part cf32ba20 is
    /// SEAT 2's (its entry hangs there), which is why a switch hung from the turret followed seat 2 (the camo must switch on only
    /// for seat 1, the one that drives the tank, no matter what). The camo's decision lives here; the pieces on
    /// the moving parts are switched on and off by events (Activate / DeActivate, which turn a switch on or off without asking anyone).
    /// </summary>
    [JsonPropertyName("driverPart")] public string DriverPart { get; set; } = "";

    /// <summary>
    /// The presets its pieces must wear from the camo package's own rigid-capable copy: a piece is drawn with a rigid declaration,
    /// and a level only compiles a preset for it when it fields something rigid that wears it (the multiplayer levels never do for
    /// the decals preset; xp1_001, xp5_001, mp_001 not even for the plain one). Measured per vehicle by the piece planner over the
    /// levels it plays in (--vehiclepieces); the presets not listed are drawn with the level's own.
    /// </summary>
    [JsonPropertyName("rigidTwins")] public List<string> RigidTwins { get; set; } = new();

    /// <summary>
    /// Materials its pieces must NOT carry, whatever the planner finds — set by hand, measured in game, and kept by --vehiclepieces
    /// (it drops them like a material the game draws with nothing). The Su-35BM's 3P cockpit (#1): from the pilot's seat the piece's
    /// copy of it rose above the 1P cockpit and flickered on its panel; without it, clean (verified in game, 2026-09-25).
    /// The F/A-18F's, the same structure, never showed it.
    /// </summary>
    [JsonPropertyName("pieceSkipMaterials")] public List<int> PieceSkipMaterials { get; set; } = new();

    /// <summary>
    /// Materials that take the camo although no database of the game binds a camo texture to them — set by hand, kept by
    /// --vehiclepieces (CamoSession.CamoMaterialsOf) and bound by the bake. The RHIB's hull (#0 vehiclepreset_mud, #2 its far LOD):
    /// its variations bind Diffuse/Normal/Specular only, so the preset draws its own default camo (camodeserttan_01, the slot map)
    /// and the scan found no camo material at all — 0 pieces, left out of every bake (the request: force the camo onto the hull and
    /// test how it looks). A forced material must wear a body preset; it is bound under the camo parameter that preset
    /// binds where the game does bind one.
    /// </summary>
    [JsonPropertyName("forceCamoMaterials")] public List<int> ForceCamoMaterials { get; set; } = new();

    /// <summary>
    /// Shaders of its OWN that take the camo although they are no preset — set by hand. The Rhino's van body (#0 vanbody_shader: five fixed
    /// textures and no camo at all; its only camo material in the game is the remote gun — painting that gun instead of the vehicle itself was
    /// a mistake, so the mask is built by the studio): its camo document adds the camo layer, sampling the pattern at
    /// <see cref="OwnCamoPreset.CamoRegister"/> — the register the studio puts the pattern on and the bake binds as the donor's "Camo".
    /// Such a shader counts as a body preset everywhere (VehicleCatalog.IsVehicleBodyPreset) once a catalogue naming it is loaded.
    /// </summary>
    [JsonPropertyName("ownCamoPresets")] public List<OwnCamoPreset> OwnCamoPresets { get; set; } = new();

    /// <summary>
    /// Materials the camo is kept OFF although the game binds one to them — set by hand, kept by --vehiclepieces (no piece carries them: the
    /// game draws its own part) and by the studio's camo materials. The Rhino's remote gun (#2: painting it was a mistake).
    /// </summary>
    [JsonPropertyName("camoOffMaterials")] public List<int> CamoOffMaterials { get; set; } = new();

    /// <summary>
    /// The blueprint the pieces (driverPart, pieces' holders and hides) were planned on: the one whose body IS the preview mesh —
    /// matched by the mesh's partition guid, never by the list's order (the Su-25TM's first blueprint was its AI one, whose guids the
    /// players' jet does not have: no camo on it, 2026-09-25). Empty in a catalogue planned before it existed = Blueprints[0].
    /// </summary>
    [JsonPropertyName("pieceBlueprint")] public string PieceBlueprint { get; set; } = "";

    /// <summary>
    /// Its OTHER blueprints whose body has the same parts as the preview mesh (measured: same census, part by part — the jets spawned
    /// in the air, the AH-1Z's coop one): the same pieces, hung with THAT blueprint's own components. A blueprint neither planned nor
    /// here gets no camo (its guids are not the planned ones: the framework would refuse it, by name).
    /// </summary>
    [JsonPropertyName("variants")] public List<VehicleVariant> Variants { get; set; } = new();

    /// <summary>
    /// The level bundles that carry the body ("levels/xp3_desert/conquestlarge0", measured by --vehiclebundles): the vehicle's part of
    /// a package is loaded right behind them, where its mesh, textures and LOD group already are — the level's MODE bundles come after
    /// the package's own place behind the level bundle, so nothing of the vehicle is loaded there (run 147).
    /// </summary>
    [JsonPropertyName("levelBundles")] public List<string> LevelBundles { get; set; } = new();

    /// <summary>
    /// Whether the body's resident LODs have their data in the game's catalog (--vehiclebundles). A package's clone of the body (the
    /// camo window's preview) has a name of its own, so only a catalog reference can give its resident LODs their data; a body whose
    /// LOD data lives only inside its levels gets no clone — its pieces are unaffected (run 149: the client froze on the loading
    /// screen with the expansion vehicles' clones in).
    /// </summary>
    [JsonPropertyName("bodyInCatalog")] public bool BodyInCatalog { get; set; } = true;

    /// <summary>The cockpit as a list: one mesh, or none.</summary>
    [JsonIgnore]
    public IEnumerable<string> CockpitMeshes => CockpitMesh.Length > 0 ? new[] { CockpitMesh } : Array.Empty<string>();

    /// <summary>
    /// What hangs under vehicles/&lt;x&gt;/kitunlock/ — the gadget parts that have something to paint.
    ///
    /// ⛔ MEASURED: only THREE vehicles in the game have one (the T-90's armour cage and antennas, the
    /// BMP-2's antennas, the Sprut-SD's). The other gadgets change numbers, not geometry, so there is
    /// nothing to open a camo window on — which is exactly the rule for the in-game screen.
    /// </summary>
    [JsonPropertyName("gadgetMeshes")] public List<string> GadgetMeshes { get; set; } = new();

    /// <summary>
    /// The mesh-variation databases that name this vehicle's mesh, best first.
    ///
    /// ⛔ NOT ONE LEVEL FOR ALL OF THEM, which is how the weapons work. Weapons live in every map;
    /// vehicles do not — a Close Quarters map has no tank entry at all. Measured over the game's 828
    /// databases: xp3_desert covers 15 of the 21, and the LAV-25, BTR-90 and the air-drop pair are only
    /// named by base-game, XP1 and XP5 levels.
    /// </summary>
    [JsonPropertyName("databases")] public List<string> Databases { get; set; } = new();

    /// <summary>The mesh the studio previews: the body.</summary>
    [JsonPropertyName("previewMesh")] public string PreviewMesh { get; set; } = "";

    [JsonPropertyName("materialCount")] public int MaterialCount { get; set; }

    /// <summary>The vehicle presets its materials wear, as the dump measured them.</summary>
    [JsonPropertyName("presets")] public List<string> Presets { get; set; } = new();

    /// <summary>
    /// Why this one has nothing to paint, when it has nothing to paint. Measured on the F-35B: its body
    /// wears shaders of its own (f35b_main, f35b_parts) and not one vehicle preset. Listed and said so
    /// rather than dropped — a customizable vehicle missing from the list reads as a bug.
    /// </summary>
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    /// <summary>Whether a camo can be put on it at all: it needs a body material wearing a vehicle preset.</summary>
    [JsonIgnore]
    public bool TakesCamo => Presets.Any(VehicleCatalog.IsVehicleBodyPreset);
}

/// <summary>A vehicle's own shader that takes the camo (see <see cref="VehicleEntry.OwnCamoPresets"/>) and where its document samples the pattern.</summary>
public sealed class OwnCamoPreset
{
    [JsonPropertyName("shader")] public string Shader { get; set; } = "";

    /// <summary>The texture register the camo document samples the pattern at — one the shader itself does not use.</summary>
    [JsonPropertyName("camoRegister")] public int CamoRegister { get; set; }
}

/// <summary>Another blueprint of the same body (see <see cref="VehicleEntry.Variants"/>): the planned pieces, hung with its own components.</summary>
public sealed class VehicleVariant
{
    [JsonPropertyName("blueprint")] public string Blueprint { get; set; } = "";

    /// <summary>Its driver seat's own part (or the seat itself, as VehicleEntry.DriverPart).</summary>
    [JsonPropertyName("driverPart")] public string DriverPart { get; set; } = "";

    /// <summary>Per planned piece (by name): the component of THIS blueprint that holds it and the ones whose parts it replaces.</summary>
    [JsonPropertyName("pieces")] public List<VehicleVariantPiece> Pieces { get; set; } = new();
}

public sealed class VehicleVariantPiece
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("holder")] public string Holder { get; set; } = "";
    [JsonPropertyName("hide")] public string Hide { get; set; } = "";
}

/// <summary>One camo piece of a vehicle body (see <see cref="VehicleEntry.Pieces"/>).</summary>
public sealed class VehiclePiece
{
    /// <summary>What it is, e.g. "turret" — the leaf of the piece's name and what the logs call it.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The body's composite parts it is made of; the first one's space is the piece's.</summary>
    [JsonPropertyName("parts")] public List<int> Parts { get; set; } = new();

    /// <summary>The body's material indices it keeps (the painted ones); empty = every material those parts wear.</summary>
    [JsonPropertyName("materials")] public List<int> Materials { get; set; } = new();

    /// <summary>
    /// The blueprint component that moves the piece's parts: the piece hangs under it at identity, behind a switch of the camo's
    /// own that no seat asks (a part's rest transform in the body IS that component's transform — the M1A2's turret part 59 and
    /// its turret component sit at the same point). Parts that never move against each other can share one piece (the hull
    /// and its skirts): every component added counts against the vehicle's one-byte component budget.
    /// </summary>
    [JsonPropertyName("holder")] public string Holder { get; set; } = "";

    /// <summary>The blueprint components whose stock parts the piece replaces, comma-separated: hidden (Show/Hide events) for
    /// the driver who carries the camo, shown again when he gets out.</summary>
    [JsonPropertyName("hide")] public string Hide { get; set; } = "";

    /// <summary>
    /// The game shows and hides the piece's stock parts by itself (seen on Riverside: the ASRAD's launchers, hidden at spawn
    /// and shown by a sequence when a gunner takes his seat; the dirt bike's gauges, swapped by an event gate between first and third
    /// person): the piece gets a switch of its own that follows the game's Show / Hide of them (VehicleCamos.lua), instead of staying
    /// on while the camo is. Not written when false: a catalogue planned before it reads as it always did.
    /// </summary>
    [JsonPropertyName("mirror")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Mirror { get; set; }

    /// <summary>
    /// The mesh the piece is cut from when it is not the body: a helicopter's first-person cockpit (VehicleEntry.CockpitMesh), which
    /// carries the same parts as the body — its detailed cabin in the part the hull piece hides, so hiding that part put it out
    /// (the AH-6, 2026-09-25). Null = the body. Its material ids are that mesh's own.
    /// </summary>
    [JsonPropertyName("mesh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mesh { get; set; }

    /// <summary>
    /// The body's own simple cabin taken out of this piece where a piece cut from the cockpit mesh carries the detailed one:
    /// "&lt;cockpit mesh&gt;:&lt;its cabin materials&gt;:&lt;its exterior materials&gt;:&lt;this piece's materials to cull&gt;" (ids "|"-separated),
    /// vehicle_part_clone's Cull. Null = nothing culled. The AH-6's hull, 2026-09-25: its simple seats and consoles poked through.
    /// </summary>
    [JsonPropertyName("cull")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cull { get; set; }
}

/// <summary>The vehicles the tool can put a camo on, loaded once from the catalogue beside the executable.</summary>
public sealed class VehicleCatalog
{
    private VehicleCatalog(IReadOnlyList<VehicleEntry> p_Vehicles) => Vehicles = p_Vehicles;

    public IReadOnlyList<VehicleEntry> Vehicles { get; }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "vehicles.json");

    /// <summary>
    /// The subjects' own starting documents in the studio (a vehicle's or a soldier part's graph with its mask — see
    /// CamoSession.SubjectPresetOf): one per body mesh, named like the mesh cache, its pictures in a folder of the same name. Each is kept
    /// as the changes made to the translation of its shader (Project.GraphPatch), applied to the user's own translation when it is asked
    /// for. Installed with --vehiclepreset / --soldierpreset.
    /// </summary>
    public static string PresetFolder => Path.Combine(AppContext.BaseDirectory, "Data", "vehiclepresets");

    /// <summary>Where a body mesh's own starting document is (it may not exist).</summary>
    public static string PresetPathOf(string p_Mesh) => Path.Combine(PresetFolder, Cache.GameCache.SanitizeName(p_Mesh) + ".patch.json");

    /// <summary>An install without the catalogue still opens the studio, with no Vehicles tab.</summary>
    public static VehicleCatalog Empty { get; } = new(Array.Empty<VehicleEntry>());

    /// <summary>Every body, variant, gadget and cockpit mesh — the list a cache run has to dump.</summary>
    public IReadOnlyList<string> AllMeshes => Vehicles
        .SelectMany(p_V => p_V.Meshes.Concat(p_V.VariantMeshes).Concat(p_V.GadgetMeshes).Concat(p_V.CockpitMeshes))
        .Concat(TrackMeshes)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The running belt a tracked vehicle carries as its OWN mesh — it is NOT in the body.
    ///
    /// ⛔ MEASURED, not derived from a name (the tanks showed up without their tracks): the game ships NINE
    /// separate track meshes and their names follow no rule worth trusting — `m1a2_tracks_Mesh` but
    /// `bmp2_track_Mesh`, `tunguska_track_Mesh` under a folder called `9K22_Tunguska_M`, `2s25_track_Mesh`
    /// under `XP3/2S25-SPRUT-SD`. Kept HERE rather than in the .json so regenerating the catalogue from the
    /// game does not quietly drop them, the same reason the weapons' exclusion list lives in code.
    /// The other four (AAV-7A1 left/right, the two T-72s) belong to vehicles this catalogue does not carry.
    /// </summary>
    private static readonly Dictionary<string, string> s_TrackMeshes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m1a2"] = "vehicles/m1a2/m1a2_tracks_mesh",
        ["t90"] = "vehicles/t90/t90_tracks_mesh",
        ["bmp2"] = "vehicles/bmp2/bmp2_track_mesh",
        ["xp5/bmp2_paradrop"] = "vehicles/bmp2/bmp2_track_mesh",
        ["9k22_tunguska_m"] = "vehicles/9k22_tunguska_m/tunguska_track_mesh",
        ["xp3/2s25-sprut-sd"] = "vehicles/xp3/2s25-sprut-sd/2s25_track_mesh",
    };

    /// <summary>The belt of the vehicle whose BODY this mesh is, or null — which is every wheeled one.</summary>
    public string? TrackMeshOfBody(string p_Mesh) =>
        VehicleOfBody(p_Mesh) is { } s_Vehicle && s_TrackMeshes.TryGetValue(s_Vehicle.Folder, out var s_Track)
            ? s_Track
            : null;

    /// <summary>Every belt this catalogue's vehicles use — dumped alongside the bodies, in the same mount.</summary>
    public IReadOnlyList<string> TrackMeshes => Vehicles
        .Select(p_V => s_TrackMeshes.TryGetValue(p_V.Folder, out var s_Track) ? s_Track : null)
        .Where(p_T => p_T != null)
        .Select(p_T => p_T!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The vehicle a mesh belongs to — body, variant, gadget or cockpit — or null for anything else.</summary>
    public VehicleEntry? Of(string p_Mesh) => Vehicles.FirstOrDefault(p_V =>
        p_V.Meshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase) ||
        p_V.VariantMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase) ||
        p_V.GadgetMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase) ||
        p_V.CockpitMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>The aircraft whose INTERIOR COCKPIT this mesh is, or null when the mesh is not one.</summary>
    public VehicleEntry? VehicleOfCockpit(string p_Mesh) => Vehicles.FirstOrDefault(p_V =>
        p_V.CockpitMesh.Length > 0 && p_V.CockpitMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A vehicle's own key, "&lt;folder&gt;@&lt;class SID&gt;": the folder alone is not one — vehicles/lav25 holds the LAV-25 and the
    /// LAV-AD, which only their customization row tells apart. What the bake dialog's boxes carry; the bake takes it or a bare
    /// folder (every vehicle of that folder, as `--baketest --vehicles lav25` has always meant).
    /// </summary>
    public static string KeyOf(VehicleEntry p_Vehicle) => $"{p_Vehicle.Folder}@{p_Vehicle.NameSid}";

    /// <summary>Whether a name the bake was given (a bare folder, a key, "all" / "*") asks for this vehicle.</summary>
    public static bool Asks(string p_Name, VehicleEntry p_Vehicle) =>
        p_Name is "all" or "*" ||
        p_Name.Equals(p_Vehicle.Folder, StringComparison.OrdinalIgnoreCase) ||
        p_Name.Equals(KeyOf(p_Vehicle), StringComparison.OrdinalIgnoreCase);

    /// <summary>The vehicle whose BODY this is (a gadget belongs to its vehicle, and is not one).</summary>
    public VehicleEntry? VehicleOfBody(string p_Mesh) => Vehicles.FirstOrDefault(p_V =>
        p_V.Meshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase) ||
        p_V.VariantMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase));

    /// <summary>The vehicle a GADGET mesh hangs from, or null when the mesh is not a gadget.</summary>
    public VehicleEntry? VehicleOfGadget(string p_Mesh) => Vehicles.FirstOrDefault(p_V =>
        p_V.GadgetMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a shader is a vehicle preset that carries the BODYWORK — what a camo goes on.
    ///
    /// ⛔ BY KIND, NOT BY A LIST OF NAMES. Four vehicles ship a preset of their own
    /// (vehicles/su-25tm/vehiclepreset_jet_su-25, vehicles/f18-f/vehiclepreset_jet_spjet, the xpack01
    /// and xp5 families), and a name list dropped all of them in silence. Glass, lights, wrecks and
    /// missile racks are the parts that never take it — measured over the 1282 materials of the
    /// vehicles subtree.
    /// </summary>
    public static bool IsVehicleBodyPreset(string? p_Shader)
    {
        if (string.IsNullOrWhiteSpace(p_Shader))
            return false;

        // …and a vehicle's own shader the catalogue says takes the camo (VehicleEntry.OwnCamoPresets), by its FULL name
        if (s_OwnCamoRegisters.ContainsKey(p_Shader))
            return true;

        var s_Leaf = p_Shader[(p_Shader.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (!s_Leaf.Contains("vehiclepreset"))
            return false;

        foreach (var s_Word in new[] { "glass", "light", "wreck", "missilerack", "soot" })
            if (s_Leaf.Contains(s_Word))
                return false;

        return true;
    }

    /// <summary>The own shaders the loaded catalogue says take the camo (VehicleEntry.OwnCamoPresets), with the register their pattern is sampled at.</summary>
    private static readonly Dictionary<string, int> s_OwnCamoRegisters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The register a vehicle's own camo shader samples the pattern at (VehicleEntry.OwnCamoPresets), or null for any other shader.</summary>
    public static int? OwnCamoRegisterOf(string? p_Shader) =>
        p_Shader != null && s_OwnCamoRegisters.TryGetValue(p_Shader, out var s_Register) ? s_Register : null;

    public static VehicleCatalog Load(string? p_Path = null)
    {
        var s_Path = p_Path ?? DefaultPath;
        var s_Vehicles = JsonSerializer.Deserialize<List<VehicleEntry>>(File.ReadAllText(s_Path))
                         ?? throw new InvalidDataException($"'{s_Path}' is not a vehicle catalogue.");

        foreach (var s_Own in s_Vehicles.SelectMany(p_V => p_V.OwnCamoPresets))
            s_OwnCamoRegisters[s_Own.Shader] = s_Own.CamoRegister;

        // Land before Air and then by name, which is the order the game's own tabs read in.
        return new VehicleCatalog(s_Vehicles
            .OrderBy(p_V => p_V.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p_V => p_V.CategoryDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p_V => p_V.Display, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }
}
