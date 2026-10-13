using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeCamoStudio.Project;

namespace RimeCamoStudio.Mod;

/// <summary>One weapon in a bake: its body meshes, the look values its variation carries, and what its entries bind.</summary>
public sealed class BakeWeapon
{
    public string Folder { get; init; } = "";

    /// <summary>The body meshes, first and third person. One database entry each.</summary>
    public List<string> Meshes { get; init; } = new();

    /// <summary>The one weapon whose vertex declaration no camo preset covers: its variation uses the cloned presets.</summary>
    public bool Aug { get; init; }

    /// <summary>The constants the variation carries, "x,y,z,w" each — the weapon's own numbers under the camo's overrides.</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The constants the camo itself changed (typed on a node or moved on a slider). Only these ride in the
    /// variation on top of the proven baseline set: a constant the camo did not touch keeps the material's own
    /// value in-game exactly as it did in the preview, and is not written where the game's variations do not
    /// write it either.
    /// </summary>
    public HashSet<string> Overridden { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every texture the meshes' database entries bind, base and shipped variations alike. Copying an entry
    /// copies its bindings, and a binding whose texture did not travel draws the OTHER skins white — so all of
    /// them are referenced (cas-ref, zero bytes of the game's) rather than only the diffuse and specular.
    /// </summary>
    public List<string> Textures { get; init; } = new();

    /// <summary>The mesh the studio previews (the first-person body), which the placements name.</summary>
    public string PreviewMesh { get; init; } = "";

    /// <summary>
    /// Per mesh, the MATERIAL IDs (the mesh's own material index) the camo is kept off: those materials keep
    /// their shipped shader and bindings while the rest of the family takes the variation — the user's
    /// choice in the studio's material picker. Empty = every preset material takes it.
    /// </summary>
    public Dictionary<string, List<int>> MaterialsOff { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The weapon's sticker layers BY MESH — its placements composed into each body mesh's own texture
    /// space, as 32-bit TGAs (premultiplied). A mesh with no entry is bound the package's EMPTY layer. Only
    /// read when the plan has a <see cref="BakePlan.StickerRegister"/>.
    /// </summary>
    public Dictionary<string, string> StickerLayers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The stickers' MAPS BY MESH — red: 0 nothing, 85 laid out as it faces, 170 laid out in mirror (which
    /// half of a mirrored unwrap each sticker was painted on); green: the animated sticker's y; alpha: its x
    /// (255 without one) — 32-bit TGAs. A mesh with no entry is bound the package's EMPTY one (ungated).
    /// Only read when the plan has a <see cref="BakePlan.StickerRegister"/>.
    /// </summary>
    public Dictionary<string, string> StickerSides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The emblem slot as frames in the previewed body's own units (EmblemSlot.ProjectionSlots: "ox,oy,oz,ax,…,reach" per square, ";"
    /// between), for a package whose shader PROJECTS the slot from the mesh position its clones' patched vertex shaders hand over — the
    /// Emblem Projection node's numbers. Empty = no slot resolved on this weapon.
    /// </summary>
    public string EmblemProjection { get; init; } = "";

    /// <summary>
    /// The pictures on the texture nodes of the weapon's OWN camo document (GraphPicture; the camo register's aside — that one is the
    /// package's pattern): bound on its first-person materials, and declared on the package's own shader where the preset has no
    /// parameter at their register (the requirement: a custom texture on a texture node must work on any weapon, accessory or vehicle).
    /// </summary>
    public List<GraphPicture> Pictures { get; init; } = new();

    /// <summary>As shipped: the pictures on its edited as-shipped graph (ShaderGraph.ShippedGraph), the same way, on the copy of the preset it wears.</summary>
    public List<GraphPicture> ShippedPictures { get; init; } = new();

    /// <summary>The previewed mesh's layer, for a seam that holds it against the window's.</summary>
    public string? StickerLayer => StickerLayers.GetValueOrDefault(PreviewMesh);

    /// <summary>The previewed mesh's map, for a seam.</summary>
    public string? StickerMap => StickerSides.GetValueOrDefault(PreviewMesh);

    /// <summary>
    /// Whether the weapon's OWN entries bind a Camo texture (measured from the material scan: the M4A1's do,
    /// desertcamo03 under the camo preset; the M416's do not, it ships a NoCamo preset). A package that
    /// brings no pattern and borrows no game camo but ships a shader that samples Camo (stickers) has to
    /// bind one on the weapons that lack it — a parameter no material binds is a question the engine was
    /// never asked.
    /// </summary>
    public bool HasCamoBinding { get; init; } = true;

    /// <summary>
    /// Whether the weapon's first-person body wears the game's NoCamo preset as shipped (27 of 59). A
    /// sticker package over the weapon as shipped binds such a weapon the package's NO-CAMO clone, so the
    /// stickers land on the weapon exactly as the game draws it, camo preset and default pattern absent.
    /// </summary>
    public bool WearsNoCamo { get; init; }

    /// <summary>
    /// Whether this weapon wears the package's OWN first-person shader (BakePlan.ShaderGraphPath) — true unless its own camo document
    /// was left with the preset's logic while another weapon's was edited (one document per subject): such a weapon
    /// keeps the game's camo preset with the pattern and the numbers.
    /// </summary>
    public bool OwnShader { get; init; } = true;

    /// <summary>
    /// Whether the game gives this weapon NO camo row (the pistols and the crossbow: no camo group in their customization, measured in
    /// the EBX). Its body is baked as every weapon's (the variation 'all' + hash on its own meshes), with the ACCESSORIES' material (done
    /// exactly as the accessories are): the third-person instance on both shader families, because
    /// the first-person camo preset zones the camo by UV tile and a pistol's body is not laid out for it (run 349: body dark). Where the
    /// package draws the emblem or stickers on the body, both meshes take what a primary's take instead — the first-person instance, from a
    /// copy of the first-person preset whose tile gate is opened (CamoBaker.AllTilesGraphFor): the emblem lives there, in both views. The
    /// framework offers the variation through a camo unlock of the pistol's own blueprint, picked in the pistol's own camo window
    /// (SidearmCamos.lua).
    /// </summary>
    public bool NoCamoRow { get; init; }
}

/// <summary>Everything a package is built from, and nothing the window holds.</summary>
public sealed class BakePlan
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The menu button this camo is filed under, or "" to let the screens guess it from the name.</summary>
    public string Family { get; set; } = "";

    /// <summary>
    /// Whether this package paints the WEAPON BODIES of its weapons, or only the attachments listed in
    /// <see cref="Accessories"/>. False is a camo OF A PIECE (the iron sights, a suppressor): the weapons are
    /// there to say whose piece it is, the gun keeps whatever skin it has, and the package adds no camo row
    /// to any weapon's menu -- the piece's own window is where it is picked.
    /// </summary>
    public bool PaintsBody { get; init; } = true;

    /// <summary>
    /// ⭐ THE LOOK ITS WEAPONS WEAR WITH NO CAMO PICKED (the request: every pistol carries the emblem slot by default, on its own shader
    /// as shipped): the framework puts this package on a weapon of it whose player chose no camo, and lists
    /// it in no camo window. Set by whoever asks the bake for it (BakeRequest.DefaultLook) after the plan is made.
    /// </summary>
    public bool DefaultLook { get; set; }

    public uint Identifier { get; set; }

    /// <summary>The user's pattern image, shipped as the camo's own texture — or null to use a game texture.</summary>
    public string? PatternImage { get; init; }

    /// <summary>A game camo texture (lowercase asset name) the camo borrows instead of shipping one.</summary>
    public string? NativeTexture { get; init; }

    /// <summary>
    /// The user's own picture for the menu row, or null to cut the row's picture out of the pattern. Any size:
    /// it is scaled to cover 256×64 and centre-cropped to that band, the shape of the game's own thumbnails.
    /// </summary>
    public string? ThumbnailImage { get; set; }

    /// <summary>
    /// No pattern of its own and no game camo borrowed: the camo is the weapon's stock look with other
    /// material values (it must bake even with no pattern). Shipped the way
    /// the game ships such variations itself (the Panther's: a material variation with no shader and only
    /// vector parameters) — the material keeps its shader and its textures, and only the constants the user
    /// changed are written over it.
    /// </summary>
    public bool ValuesOnly => PatternImage == null && NativeTexture == null && StickerRegister == null;

    /// <summary>
    /// The register the package's shader samples its sticker layer from (the graph's StickerRegister), or
    /// null when the graph has no sticker layer. With one, the package binds a layer texture on EVERY
    /// weapon's variation (the weapon's own or the empty one) under the material parameter "Sticker", and
    /// its cloned preset declares that parameter — a shader sampling a parameter no material binds is a
    /// question the engine was never asked. Needs <see cref="ShaderGraphPath"/>: the layer only exists
    /// in the package's own shader.
    /// </summary>
    public int? StickerRegister { get; init; }

    /// <summary>
    /// The frame sheet of the camo's animated sticker (a 32-bit TGA, straight alpha), or null when the
    /// camo plays none. With one, the package ships it once under "StickerSheet" (sampled at
    /// StickerSheetRegister); the coordinates ride in the per-mesh map under "StickerMap" (StickerRegister + 1,
    /// green y, alpha x); the graph's Flipbook node carries the GIF's timeline.
    /// </summary>
    public string? StickerSheet { get; init; }

    /// <summary>
    /// The register the frame sheet is sampled at: StickerRegister + 3 beside the emblem slot's atlas (StickerGraph.GifKeepsEmblem: the
    /// slot stays whatever is placed), StickerRegister + 2 in a graph made before (null = that).
    /// </summary>
    public int? StickerSheetRegister { get; init; }

    /// <summary>
    /// The emblem slot's shape atlas (EmblemSlot.AtlasTga), or null when the package's shader draws no emblem slot. With one, the package
    /// ships it once under "EmblemAtlas" (sampled at StickerRegister + 2 — an animated sticker's sheet goes after it,
    /// StickerSheetRegister); the slot's square rides in the per-mesh map like an animated sticker's frame (a projected slot reads its frames
    /// instead), and its layers are the shader's EmblemL0.. constants, which the engine fills per weapon.
    /// </summary>
    public string? EmblemAtlas { get; init; }

    /// <summary>
    /// One of the framework's DEFAULT camos: a game pattern on every weapon, shipped under defaultcamos/ on the
    /// game's own row for that pattern (its identifier), with no picture of its own — the row already has one.
    /// See FrameworkMod and CamoSession.PlanForNative.
    /// </summary>
    public bool Native { get; set; }

    /// <summary>
    /// A saved graph (the first-person preset, edited beyond values) whose LOGIC the package ships as its own
    /// preset: the preset's entry is cloned under the package's name with the graph's bytecode patched into
    /// its flavours, cut into a small shader database of the package's own and registered on every level
    /// (the mechanism proven by the AUG's bundle). Null = the game's presets, pattern and values only.
    /// The third-person model keeps the game's preset either way — a first-person graph does not describe it.
    /// </summary>
    public string? ShaderGraphPath { get; init; }

    /// <summary>
    /// The VEHICLE preset the edited graph (ShaderGraphPath) was authored on — the document's target, e.g.
    /// vehicles/shaders/vehiclepreset_mud — or null. With it, the vehicle pieces whose materials wear that preset (or a sibling whose
    /// texture registers line up with it, as the studio's preview draws them) are drawn with copies of their presets compiled from
    /// THIS graph (whatever is edited by hand in the graph must bake, as it does for accessories and weapons). The graph file itself
    /// names the weapons' preset as its target (ShaderGraphForBake), so the vehicle's is carried here.
    /// </summary>
    public string? VehicleGraphTarget { get; init; }

    /// <summary>
    /// The no-camo twin of the package's graph — the game's NoCamo preset graph sampling the same sticker
    /// layer — for a sticker package over the weapon as shipped (no pattern, no native camo). Null otherwise.
    /// Weapons whose body wears the NoCamo preset are bound its clone; the rest the camo one.
    /// </summary>
    public string? NoCamoShaderGraphPath { get; init; }

    /// <summary>
    /// The values typed on a subject's document that the package does not carry — constants the studio's measured table does not know
    /// (a vehicle's FLIR numbers, the F-35B's Scalar): "Name (subject)". The preview shows them; the bake says it leaves them out
    /// (review 2026-09-29, C7: they went without a word).
    /// </summary>
    public List<string> ValuesNotBaked { get; init; } = new();

    /// <summary>
    /// ⭐ AS SHIPPED IS A CAMO LIKE ANY OTHER (the as-shipped graphs must bake too): the as-shipped graphs of
    /// weapons whose body wears ANOTHER preset than the camo's or the no-camo one (the M249's and the L85A2's weaponpresetfp, the XP2
    /// family's weaponpresetshadowfp_xp2), edited — one copy of that preset per entry, compiled from its graph, bound by those weapons
    /// alone. (The no-camo preset's goes through <see cref="NoCamoShaderGraphPath"/>, the camo's own through <see cref="ShaderGraphPath"/>.)
    /// </summary>
    public List<ShippedShaderRequest> ShippedShaders { get; init; } = new();

    /// <summary>
    /// The preset the package's own shader (<see cref="ShaderGraphPath"/>) is cloned from and compiled against: the first-person camo preset
    /// — every weapon's variation runs on it — unless the camo is AS SHIPPED on a tab aimed at another preset (a session opened on the
    /// JNG90 aims at weaponpresetshadowfp_xp2): its document IS that preset's as-shipped graph, and its copy has to be of THAT preset.
    /// </summary>
    public string OwnShaderPreset { get; init; } = CamoBaker.FirstPersonPresetName;

    /// <summary>
    /// The camo's edited graphs for the objects' OTHER materials (see <see cref="MaterialShaderRequest"/>),
    /// already written to disk. Empty = the package touches no material but the ones the camo paints.
    /// </summary>
    public List<MaterialShaderRequest> MaterialShaders { get; init; } = new();

    /// <summary>The pictures on the objects' materials' own graphs (see <see cref="MaterialPictureRequest"/>), each for that material alone.</summary>
    public List<MaterialPictureRequest> MaterialPictures { get; init; } = new();

    /// <summary>
    /// The camo's edited graphs for the materials of its VEHICLES that open a graph of their own (a material on another preset
    /// than the camo's — the CROWS on vehiclepreset_nomud under a vehiclepreset_mud camo —, a glass, a light), already written to
    /// disk. The vehicle pieces draw each with a copy of its preset compiled from ITS graph, handed to that material alone (two materials
    /// edited at once must both bake — until then only the preview showed them). Kept apart
    /// from <see cref="MaterialShaders"/>: a weapon's edited material is a clone of the level's own entry, a vehicle piece's a copy
    /// drawn on rigid solutions.
    /// </summary>
    public List<MaterialShaderRequest> VehicleMaterialShaders { get; init; } = new();

    /// <summary>
    /// The accessories this camo is offered on (phase F, A.3). Empty = a camo of weapon bodies only, exactly
    /// as every package built so far.
    /// </summary>
    public List<BakeAccessory> Accessories { get; init; } = new();

    /// <summary>
    /// The vehicles this camo paints. Empty = a camo of weapons (and their pieces) only, as every package
    /// baked before. A package may paint vehicles only: then it names no weapons and adds no weapon row.
    /// </summary>
    public List<BakeVehicle> Vehicles { get; init; } = new();

    public List<BakeWeapon> Weapons { get; init; } = new();
}

/// <summary>
/// One vehicle body a package paints: the accessories' recipe on the vehicle's composite mesh, measured in game (the camo
/// showed up directly and worked, the M1A2 wearing Berkut) — a renamed clone of the body mesh, the
/// clone's hash-0 entry copied from the vehicle's own base entry with its Camo binding repointed at the package's texture
/// and a material variation with no shader carrying the camo's values (the material keeps its own vehicle preset). The
/// client's vehicle view points the vehicle's data at the clone for the player who chose the camo.
/// </summary>
public sealed class BakeVehicle
{
    /// <summary>Every blueprint that wears this body (a jet spawned in the air is the same body), catalogue spelling.</summary>
    public List<string> Blueprints { get; init; } = new();

    /// <summary>The SID of its customization row, e.g. ID_EOR_SCORINGBUCKET_VEHICLEMBT — the camo window it is picked in.</summary>
    public string Class { get; init; } = "";

    /// <summary>The body mesh (resource and EBX partition), e.g. vehicles/m1a2/m1abrams_mesh.</summary>
    public string Mesh { get; init; } = "";

    /// <summary>The mesh's EBX Name as the game spells it — what the clone's name has to be as long as.</summary>
    public string EbxName { get; init; } = "";

    /// <summary>A level mesh-variation database that carries the body's base entry (no level has every vehicle).</summary>
    public string Database { get; init; } = "";

    /// <summary>The shaders of the body's materials that bind Camo: the ones the variation and the camo texture go to.</summary>
    public List<string> CamoShaders { get; init; } = new();

    /// <summary>The blueprint Pieces/DriverPart were planned on (VehicleEntry.PieceBlueprint; empty = Blueprints[0]).</summary>
    public string PieceBlueprint { get; init; } = "";

    /// <summary>Its other blueprints the same pieces hang on, with their own components (VehicleEntry.Variants).</summary>
    public List<Catalog.VehicleVariant> Variants { get; init; } = new();

    /// <summary>
    /// The camo texture PARAMETERS those materials bind, as the game binds them: "Camo" on the ground presets, "CamoA" and "CamoB" on
    /// the jet family (the colour uv's V ≥ 0 and V &lt; 0 halves — one alone paints half an aircraft). The pattern is bound on each.
    /// Empty = "Camo".
    /// </summary>
    public List<string> CamoParameters { get; init; } = new();

    /// <summary>The constants its material variation carries ("x,y,z,w" each), the camo's own; empty = the material's.</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The numbers the studio's preview draws each material of the body with, by the mesh's own material id (CamoSession.ValuesForMesh: the
    /// material's own over its preset's defaults). What a copy compiled on a DONOR whose constant block has no slot for one of its graph's
    /// constants bakes in that constant's place (CamoBaker.RelocateConstants) — the engine writes nothing there for the copy.
    /// </summary>
    public Dictionary<int, Dictionary<string, string>> MaterialValues { get; init; } = new();

    /// <summary>The same for the vehicle's own camo document: the numbers the preview dresses it with on the document's preset.</summary>
    public Dictionary<string, string> DocumentValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The saved graph of THIS vehicle's own camo document when its logic (nodes, wires) was edited beyond values, or null (one
    /// document per subject): its pieces' materials on the camo's preset (or a sibling lined up with it,
    /// BakePlan.VehicleGraphTarget) are drawn with copies compiled from it — this vehicle's own, never another's.
    /// </summary>
    public string? ShaderGraphPath { get; init; }

    /// <summary>
    /// The preset <see cref="ShaderGraphPath"/> was authored on when it is NOT the tab's (BakePlan.VehicleGraphTarget), or null: a vehicle
    /// whose own starting document is drawn on a shader of its own (the Rhino's van body, vanbody_shader with the camo layer its catalogue
    /// entry adds — VehicleEntry.OwnCamoPresets) ticked on a tab of the mud (one test camo per batch: "Markaz Test" carries
    /// the Phoenix's mud document and the Rhino's). Its pieces' copies are compiled against THIS preset; every other vehicle keeps the tab's.
    /// </summary>
    public string? GraphTarget { get; init; }

    /// <summary>The body's own diffuse (the camo material's Diffuse binding), for the row picture of a camo with no pattern; "" if unknown.</summary>
    public string DiffuseTexture { get; init; } = "";

    /// <summary>The pictures on the texture nodes of THIS vehicle's own camo document (GraphPicture; its camo registers' aside — the
    /// pattern), bound by parameter name on its camo materials, and declared on the copies compiled from its graph where needed.</summary>
    public List<GraphPicture> Pictures { get; init; } = new();

    /// <summary>
    /// The body's camo pieces (option B): each cut out of the body as a rigid mesh of the package
    /// (`vehicle_part_clone`) with its own (piece, 0) entry wearing the camo, like the body clone's.
    /// </summary>
    public List<Catalog.VehiclePiece> Pieces { get; init; } = new();

    /// <summary>The driver seat's own part: where the camo's decision (the driver's switch) hangs — see VehicleEntry.DriverPart.</summary>
    public string DriverPart { get; init; } = "";

    /// <summary>The preset each material of the body wears, by the mesh's own material ID (from the scan): what a piece's
    /// materials are drawn with, and so whether one needs a rigid-capable copy of its preset.</summary>
    public Dictionary<int, string> MaterialShaders { get; init; } = new();

    /// <summary>
    /// The vertex declarations the body's preset sections declare (CamoSession.DeclarationsOf; the RHIB's composite 0xFC3E2403): a body
    /// clone drawn with a copy of the vehicle's own graph (everything moved to option A) needs that copy's source entry to hold
    /// solutions for every one of them. Empty = unknown (no such body clone).
    /// </summary>
    public List<string> BodyDeclarations { get; init; } = new();

    /// <summary>The same for each OTHER mesh a piece is cut from (VehiclePiece.Mesh: a helicopter's first-person cockpit), by its own ids.</summary>
    public Dictionary<string, Dictionary<int, string>> PieceMeshShaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The mesh a piece is cut from: its own (VehiclePiece.Mesh) or the body.</summary>
    public string MeshOf(Catalog.VehiclePiece p_Piece) => p_Piece.Mesh is { Length: > 0 } s_Mesh ? s_Mesh : Mesh;

    /// <summary>The preset each material of a piece wears, by the ids of the mesh it is cut from.</summary>
    public IReadOnlyDictionary<int, string> ShadersOf(Catalog.VehiclePiece p_Piece) =>
        p_Piece.Mesh is { Length: > 0 } s_Mesh
            ? PieceMeshShaders.GetValueOrDefault(s_Mesh) ?? new Dictionary<int, string>()
            : MaterialShaders;

    /// <summary>Whether a piece is cut from the body — the one whose material ids the body's own edited materials and pictures name.</summary>
    public static bool FromBody(Catalog.VehiclePiece p_Piece) => string.IsNullOrEmpty(p_Piece.Mesh);

    /// <summary>The presets its pieces wear from the package's rigid-capable copy (see VehicleEntry.RigidTwins); the others are drawn
    /// with the level's own.</summary>
    public List<string> RigidTwins { get; init; } = new();

    /// <summary>The level bundles that carry the body (see VehicleEntry.LevelBundles): its part of the package goes right behind them.</summary>
    public List<string> LevelBundles { get; init; } = new();

    /// <summary>
    /// False when the body's resident LOD data lives only inside its levels (the expansion vehicles): its body clone is the EBX alone
    /// with the game's Name kept (accessory_camo_clone … vanilla) — the engine binds the MeshSet by that name, so the level's own
    /// resident LOD data answers — where a renamed header waited for data no bundle binds to it (run 149, the client frozen).
    /// </summary>
    public bool BodyInCatalog { get; init; } = true;
}

/// <summary>A preset the package ships: its clone's name and the EBX stub the variations point at.</summary>
public sealed record ShaderStub(string Name, string Partition, string Instance);

/// <summary>
/// One of the object's OTHER materials whose graph the camo edited (a glass, a glow, a reticle HUD, a preset
/// material kept off the camo): the package clones THAT material's own shader with the edited bytecode and
/// the variation points only that material at the clone (the bake must take every material
/// into account). <paramref name="MaterialId"/> is the mesh's own material index, as the studio shows it.
/// </summary>
public sealed record MaterialShaderRequest(string Mesh, int MaterialId, string Shader, string GraphPath);

/// <summary>
/// The pictures on the texture nodes of one material's OWN graph (a glass, a reticle, a vehicle's CROWS), bound on THAT material alone —
/// whether or not its logic was edited (a picture changes nothing the shader emits: with no <see cref="MaterialShaderRequest"/> for it, the
/// material keeps its shader and only the binding moves). Declared on its clone where its shader has no parameter at their register.
/// </summary>
public sealed record MaterialPictureRequest(string Mesh, int MaterialId, string Shader, IReadOnlyList<GraphPicture> Pictures);

/// <summary>
/// An as-shipped graph edited on weapons whose body wears <paramref name="Preset"/> (the preset as the game names it): the package clones
/// that preset with the graph's bytecode and those weapons' first-person material points at the clone (see BakePlan.ShippedShaders).
/// </summary>
public sealed record ShippedShaderRequest(string Preset, string GraphPath, IReadOnlyList<string> Weapons);

/// <summary>
/// A picture the user put on a texture node of an edited graph (the node's "Custom texture"), which the preview samples at
/// <paramref name="Register"/> (a snow picture on the ACOG's as-shipped graph looked right in the preview but wrong in game — the bake
/// dropped it and the copy read an unbound register). It ships as a texture of the package's own, bound on the
/// entries under <paramref name="Parameter"/> — the preset's own parameter at that register, or, on a register the preset has none at,
/// a parameter the copy declares there (<paramref name="Declare"/>: the stickers' recipe). Never a game texture replaced by name.
/// </summary>
/// <param name="From">When the node's own register holds something the preset reads that no material binds (a FIXED texture of the shader
/// — the glass's t2, "0 from this mesh's materials"): the node's register in the graph, moved to <paramref name="Register"/> (a free one) in
/// the copy that samples the picture, so a parameter can be declared there without fighting the fixed texture. Null = not moved.</param>
public sealed record GraphPicture(int Register, string Parameter, bool Declare, string File, bool Srgb, int? From = null);

/// <summary>One mesh of an accessory the package gives a camo of its own.</summary>
/// <param name="Mesh">The vanilla mesh resource (also its EBX partition name), e.g. weapons/accessories/acog/acog_scope_3p_mesh.</param>
/// <param name="EbxName">The mesh's EBX Name as the partition stores it — what the clone's name has to be as long as.</param>
/// <param name="Declaration">The vertex declaration its preset section declares ("0x882E8A4C"): it decides whether the
/// third-person preset can draw it or the package must ship a re-labelled twin.</param>
/// <param name="PresetShaders">The preset shaders its sections wear — the filter that hands the variation to its body.</param>
public sealed record BakeAccessoryMesh(string Mesh, string EbxName, string Declaration, IReadOnlyList<string> PresetShaders);

/// <summary>
/// One accessory of one weapon in a bake: its meshes (first person, zoom, third person) and the numbers its
/// material variation carries. The recipe is the one closed in-game over 26 boots (see the phase-E memory):
/// a renamed clone of each mesh, the (clone, 0) entry every engine socket reads, and the THIRD-person preset
/// family on its body — re-labelled to the accessory's declaration where the preset has no solution for it.
/// </summary>
public sealed class BakeAccessory
{
    /// <summary>The weapon it hangs from (catalogue folder) — its wear and tiling are what the accessory takes.</summary>
    public string Weapon { get; init; } = "";

    /// <summary>The unlock's short name, e.g. "L85A2_Acog" — what the package calls it.</summary>
    public string Tag { get; init; } = "";

    public List<BakeAccessoryMesh> Meshes { get; init; } = new();

    /// <summary>The constants its variation carries ("x,y,z,w" each): the host weapon's wear under the camo's own.</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The constants the user set for it — typed on the camo's document or the piece's own graph, or in its boxes under the accessory list —
    /// that its material variation writes beside the host's wear and tiling (CamoBaker.AccessoryParametersOf). Before, only those three
    /// were written, and whatever else the preview drew it with stayed at the game's (review 2026-09-29, C4).
    /// </summary>
    public HashSet<string> Overridden { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The saved graph of this accessory's painted body when its LOGIC was edited (the graph the studio opens for it, authored on the
    /// third-person preset, AccessoryPresetName), or null: the package then ships a copy of that preset compiled from it — re-labelled to
    /// the accessory's declaration where it needs a twin — and the accessory's material variation points at it (the same as the weapons
    /// get — until then only the graph's VALUES reached the bake).
    /// </summary>
    public string? GraphPath { get; init; }

    /// <summary>
    /// ⭐ AS SHIPPED, EDITED (as shipped is a camo like ABU or any other, only it is the original — and the accessories get it too): the
    /// preset the piece's own material wears as the game ships it (the ACOG's
    /// weaponpresetfp, spelled as the game's database spells it) when its as-shipped graph was edited, with that graph saved at
    /// <see cref="ShippedGraphPath"/>; null otherwise. The package ships a copy of THAT preset compiled from the graph, and the piece's
    /// entries hand it to the materials that wear that preset, with no constants — they keep their own numbers and textures. Nothing
    /// else of the piece changes: no camo, no host wear, no declaration twin (the copy has the preset's own solutions).
    /// </summary>
    public string? ShippedPreset { get; init; }

    /// <summary>The saved as-shipped graph of <see cref="ShippedPreset"/>, edited on this piece; null when it was not.</summary>
    public string? ShippedGraphPath { get; init; }

    /// <summary>The pictures on that graph's texture nodes (see <see cref="GraphPicture"/>): bound on the piece's entries, declared on
    /// its copy where the preset has no parameter at their register.</summary>
    public List<GraphPicture> ShippedPictures { get; init; } = new();

    /// <summary>Under a camo: the pictures on its painted body's own graph (the third-person preset's, <see cref="GraphPath"/> or values
    /// only), bound on the materials its variation goes to, declared on its copy where needed.</summary>
    public List<GraphPicture> Pictures { get; init; } = new();

    /// <summary>True for a piece of an as-shipped package: only what was edited on it rides (<see cref="ShippedPreset"/>, its
    /// materials' own graphs), never the camo's variation on its weapon presets.</summary>
    public bool AsShipped { get; init; }

    /// <summary>Every texture its entries bind, referenced (never copied) so the other skins do not draw white.</summary>
    public List<string> Textures { get; init; } = new();
}

public sealed class BakeContext
{
    public string ModFolder { get; init; } = "";
    public string WorkDir { get; init; } = "";
    public string RimeRepl { get; init; } = "";
    public string Texconv { get; init; } = "";

    /// <summary>The shader compiler (fxc.exe); only needed by a camo that ships its own shader.</summary>
    public string Fxc { get; init; } = "";
    public string GamePath { get; init; } = "";
    public string MvdbSource { get; init; } = Cache.GameCache.WeaponMvdb;
    public Action<string> Log { get; init; } = _ => { };
}

public sealed class BakeResult
{
    public bool Ok => Problems.Count == 0;
    public List<string> Problems { get; } = new();
    public string PackageDir { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string Summary { get; set; } = "";
    public PackageInfo? Package { get; set; }

    /// <summary>
    /// The WEAPON-side meshes (an accessory's, by their game name) whose clone left data to the level with no catalog copy — one problem
    /// each in <see cref="Problems"/>. The caller may bake again without them (the Rifle Scope's third-person LOD 4: that mesh keeps the
    /// game's look, the rest of the package ships).
    /// </summary>
    public List<string> LevelOnlyMeshes { get; } = new();
}

/// <summary>
/// Builds ONE camo package — the drop-in unit of the framework — and registers it in the mod.
///
/// The recipe is the one proven in-game on the bench (twelve camos on the M416, four with textures of their
/// own) and in the framework's base (eight camos on 63 weapons), written out per camo instead of per level:
/// <code>
///   pack    (CAS)     ObjectVariation per weapon + a minimal database per body mesh, copied from the level's
///                     database with the new variation appended, + REFERENCES to every texture those copies bind
///   packnc  (NONCAS)  the camo's own pattern texture (a generated resource only works from NONCAS) and the
///                     row's thumbnail resource (UI group, on-demand, header only)
///   chunks  (NONCAS)  the thumbnail's pixels — a chunk inside a bundle is discarded unless something is
///                     waiting for it, so they travel the way the expansions ship theirs: a chunk store
/// </code>
/// Nothing of the game's is copied: what the entries bind is referenced by its catalogue hash.
///
/// ⛔ EVERY GUID IS DERIVED, NEVER DRAWN. A player's loadout keeps the camo by identifier and the game keeps
/// the variation by the hash of its name; the partition guids only have to be unique and STABLE — the same
/// camo rebuilt tomorrow must produce the same guids, or a reference handed to the engine points somewhere
/// else each time. They come from the camo's key, the weapon's index and a slot number.
/// </summary>
public static partial class CamoBaker
{
    // The two presets that sample camo, by partition and primary instance — the same pair every camo of the
    // framework's base uses on 62 of 63 weapons. Read out of the game's EBX; not derived.
    private const string FpPartition = "1155325d-8138-4d3c-a3d1-ab5a1d520289";
    private const string FpInstance = "1afd6691-9cb6-4195-a4d1-6c925c0c3c2b";
    private const string ThreePPartition = "dab17334-583e-4acf-b7b2-efb1a93c0d5a";
    private const string ThreePInstance = "8d89edad-d2b1-4bb9-b36c-1498f76c8c8b";

    // The AUG A3's clones: the same presets with a vertex declaration only that weapon has, compiled into the
    // level's shader database by the framework's per-map base bundle. The guids are the base's EBX stubs.
    private const string AugFpPartition = "c0a0a0a0-0001-4000-a000-000000000001";
    private const string AugFpInstance = "c0a0a0a0-0002-4000-a000-000000000002";
    private const string Aug3PPartition = "c0a0a0a0-0003-4000-a000-000000000003";
    private const string Aug3PInstance = "c0a0a0a0-0004-4000-a000-000000000004";

    /// <summary>The first-person preset every variation runs on — and the one a package's own shader is cloned from.</summary>
    private const string FpPresetName = "Weapons/Shaders/WeaponPresetShadowFP";

    /// <summary>The same, for a plan that names its own shader's source (BakePlan.OwnShaderPreset).</summary>
    public const string FirstPersonPresetName = FpPresetName;

    /// <summary>
    /// The level database the clones are cut from (every level carries the weapon presets; this is the one
    /// the framework was measured on) and the level name the compiler extracts the preset's flavours from.
    /// </summary>
    private const string CloneSourceDb = "levels/xp2_skybar/xp2_skybar/shaderdb";
    private const string CloneSourceLevel = "xp2_skybar";

    /// <summary>
    /// The vehicle presets a RIGID camo piece may have to be drawn with from a copy, and the level database whose copy can draw it:
    /// a piece is drawn with the rigid declaration 0xC83353E0, and the levels compile a preset for it only if they field something
    /// rigid that wears it. MEASURED over 46 level databases (dump_shader_solutions, 2026-09-24): VehiclePreset_Mud has it in 16
    /// (not in xp1_001, xp5_001, mp_001…), VehiclePreset_noMud in 12, VehiclePreset_Mud_Decals only in sp_bank, sp_tank and sp_valley;
    /// the 1uvset family in NONE. sp_valley holds all three, and a copy chain reads ONE database, so it is the source of all three.
    /// Which of them a vehicle's pieces take from the copy is decided per vehicle (VehicleEntry.RigidTwins: those missing from one of
    /// its levels), so the rest keep the level's own. The copy goes under a fresh name into the package's own shader database and
    /// the (piece, 0) entry points that material at it (the M1A2 turret's front plates are drawn with the decals copy, run 135).
    ///
    /// ⭐ A preset NO level compiles for the rigid declaration is drawn with the rigid solutions of ANOTHER preset that has them (the
    /// donor), with its own logic compiled in from its graph (OwnLogic: the 1uvset family, the tank destroyers'). The donor is the
    /// one whose rigid flavours hand the pixel stage what the preset's graph reads (a tangent frame and the uv pair): for the decals
    /// preset that is VehiclePreset_Mud, NOT VehiclePreset_Mud_Decals — whose names line up better, but whose rigid flavours in
    /// sp_valley carry no tangent rows (measured). That choice is the one thing here no cache can answer, so it stays measured.
    /// ⛔ EVERYTHING ELSE about such a copy is DERIVED, never written here (see DeriveTwinRetarget): the texture registers by NAME in
    /// the two pixel stages, the textures the donor lacks, each texture's sampler as the preset reads it, the sampler STATES the copy
    /// carries (copied from the preset's own entry) and whether the piece's uv sets go exchanged (the studio's UV order census). A
    /// hand table of those (2026-09-24, v26) re-measured what the studio knew and left the decals reading through the donor's s0.
    /// A preset only a DIFFERENT level can copy (VehiclePreset_Lights: sp_tank and sp_bank alone) makes a shader database of its
    /// own: a copy chain reads one level database.
    /// Key = the preset (lowercase, as the scan names it).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, VehicleTwinSource> s_RigidTwinSources =
        new Dictionary<string, VehicleTwinSource>(StringComparer.OrdinalIgnoreCase)
        {
            // ⭐ sp_valley compiles the mud for the AIRCRAFT's piece layout too (0x1D397D6A: GBuffer with and without the light probe and
            // ZOnly, Dx10Plus and Dx11 — the same set as the ground one's), and its vertex stage hands the uv pair over the same way on
            // both (`mov o4.xyzw, v3.zwxy`, measured 2026-09-26): the XP4 ground vehicles whose bodies carry THREE uv sets (the Barsuk's
            // 0x45440377, the Rhino's 0x4544818E) cut to that layout, and without it their mud parts were dropped from every piece
            ["vehicles/shaders/vehiclepreset_mud"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "VehiclePreset_Mud",
                Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
            ["vehicles/shaders/vehiclepreset_nomud"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_noMud", "VehiclePreset_noMud"),
            ["vehicles/shaders/vehiclepreset_mud_decals"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud_Decals", "VehiclePreset_Mud_Decals"),
            // (the 1uvset family draws on that same mud entry: both layouts too)
            ["vehicles/shaders/vehiclepreset1uvset_mud"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "VehiclePreset1UvSet_Mud", OwnLogic: true,
                Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
            ["vehicles/shaders/vehiclepreset1uvset_mud_allcamo"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "VehiclePreset1UvSet_Mud_AllCamo", OwnLogic: true,
                Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
            ["vehicles/shaders/vehiclepreset1uvset_mud_decals"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "VehiclePreset1UvSet_Mud_Decals", OwnLogic: true,
                Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
            // the materials of a piece's part that take no camo: drawn as the game draws them, from the copy (no camo values)
            ["vehicles/shaders/vehiclepreset_glassopaque"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_GlassOpaque", "VehiclePreset_GlassOpaque", Camo: false),
            // the LOD one has the same pixel stages byte for byte (every flavour, measured) and no rigid solution in any level
            ["vehicles/shaders/vehiclepreset_glassopaque_lod"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_GlassOpaque", "VehiclePreset_GlassOpaque_LOD", Camo: false),
            // (coop_007 compiles the glass for BOTH rigid layouts — the ground family's and the aircraft's 0x1D397D6A, census
            // 2026-09-25 —; sp_valley only for the ground one)
            ["vehicles/shaders/vehiclepreset_glass"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Glass", "VehiclePreset_Glass", Camo: false,
                Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
            // ⭐ the AIRCRAFT (three uv sets: rigid 0x1D397D6A, stride 48): the jet presets are compiled for it in coop_007 alone
            // (…and for the ground one too, in both render paths — a piece cut from a helicopter's first-person cockpit mesh, whose
            // declaration is the ground family's 0xFC3E2403: measured 2026-09-25)
            ["vehicles/shaders/vehiclepreset_jet"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "VehiclePreset_Jet",
                Rigids: AircraftPieceDeclaration + "," + RigidPieceDeclaration),
            ["vehicles/shaders/vehiclepreset_jet_decals"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet_Decals", "VehiclePreset_Jet_Decals", Rigids: AircraftPieceDeclaration),
            // …and the aircraft's own shaders, which no level compiles for a rigid piece: their own logic compiled onto the jet's rigid
            // solutions (OwnLogic, as the 1uvset family on the mud). The cockpits and the Su-25's plane construct ride the hull's parts
            // (a part comes whole), so without them the F/A-18F's, Su-35BM's and Mi-28's hulls had no piece at all (2026-09-25)
            ["vehicles/su-25tm/vehiclepreset_jet_su-25"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "VehiclePreset_Jet_SU-25", OwnLogic: true, Rigids: AircraftPieceDeclaration),
            // (the decal one reads a THIRD uv set, 1:2:0 — the jet's decals preset hands it over, the plain jet only 1:0)
            ["vehicles/su-25tm/vehiclepreset_jet_su-25_decal"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet_Decals", "VehiclePreset_Jet_SU-25_Decal", OwnLogic: true, Rigids: AircraftPieceDeclaration),
            ["vehicles/su-25tm/planeconstruct"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "SU-25_PlaneConstruct", OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            ["vehicles/su-25tm/su-25_cockpit"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "SU-25_Cockpit", OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            ["vehicles/f18-f/f18-f_cockpit"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "F18-F_Cockpit", OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            ["vehicles/su-35bm-e/su-35bm-e_cockpit"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "SU-35BM-E_Cockpit", OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            ["vehicles/mi28/mi28_cockpit3p"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "Mi28_Cockpit3P", OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            // the AH-6's first-person cabin (its cockpit mesh, declaration 0xFC3E2403): compiled for no rigid layout in any of its 17 levels
            // (census 2026-09-25) ⇒ its logic on the jet's ground-layout solutions
            ["vehicles/ah6/ah6_cockpitbase"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "AH6_CockpitBase", OwnLogic: true, Camo: false, Rigids: RigidPieceDeclaration),
            // ⭐ "preset@rigid": the SAME preset for the other piece layout, from another source (see RigidTwinKey) — the Z-11W's hull
            // wears the ground family's noMud, which no level compiles for an aircraft's piece: its logic on the jet's
            ["vehicles/shaders/vehiclepreset_nomud@" + "0x1d397d6a"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Jet", "VehiclePreset_noMud_Air", OwnLogic: true, Rigids: AircraftPieceDeclaration),
            ["vehicles/shaders/vehiclepreset_lightson"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_LightsOn", "VehiclePreset_LightsOn", Camo: false),
            ["vehicles/shaders/vehiclepreset_lights"] = new("sp_tank", "Vehicles/Shaders/VehiclePreset_Lights", "VehiclePreset_Lights", Camo: false),
            // the BTR-90's preset (XPack01's own mud, ONE uv set — not the base mud's exchanged pair): rigid only in the four xp3
            // levels (census 2026-09-24), none of the BTR's own (xp1_001-003) ⇒ a database of its own, like the lights'
            ["vehicles/xpack01/shaders/vehiclepreset_mud"] = new("xp3_desert", "Vehicles/XPack01/Shaders/VehiclePreset_Mud", "XPack01_VehiclePreset_Mud"),
            // the Quad's preset (XPack01's MudScratches, ONE uv set): compiled for NO rigid layout in any of the 48 level databases
            // (census 2026-09-26: only its own composite 0xA839D484 and the depth-only 0x0F23D5FA) ⇒ its own logic on the base mud's
            // rigid solutions, as the 1uvset family (its uv set reaches the donor's pair through the cutter's copy in TexCoord1)
            ["vehicles/xpack01/shaders/vehiclepreset_mudscratches"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "XPack01_VehiclePreset_MudScratches", OwnLogic: true),
            // ⭐ the Barsuk's (XP4, three uv sets ⇒ the aircraft's piece layout 0x1D397D6A): XPack01's mud and the HIMARS glass its hull
            // wears are compiled for that layout in NO level (census 2026-09-26: the mud only for 0x45440377/0xC83353E0…, the glass only for
            // the composite ones) ⇒ each its own logic on a donor that has it — the base mud's sp_valley solutions, the vehicle glass's
            // coop_007 ones — as the aircraft's cockpits ride the jet's (without the glass, the hull part it sits on had no piece)
            ["vehicles/xpack01/shaders/vehiclepreset_mud@" + "0x1d397d6a"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "XPack01_VehiclePreset_Mud_Air",
                OwnLogic: true, Rigids: AircraftPieceDeclaration),
            ["vehicles/xp3/himars/himars_glass@" + "0x1d397d6a"] = new("coop_007", "Vehicles/Shaders/VehiclePreset_Glass", "HIMARS_Glass_Air",
                OwnLogic: true, Camo: false, Rigids: AircraftPieceDeclaration),
            // ⭐ the Rhino's van body (XP4, three uv sets ⇒ 0x1D397D6A): a shader of its OWN, compiled for no rigid layout (its levels hold
            // only its composite 0x4544818E and 0xFC3EE40E) and with no camo at all — its catalogue entry adds one (VehicleEntry.OwnCamoPresets,
            // with a mask the studio builds itself). Its document's logic on the base mud's rigid solutions, as the 1uvset family: one uv
            // set (TexCoord0, `mov o8.xy, v3.xyxx`), its five fixed textures in the copy's own list, its camo on the mud's Camo
            ["vehicles/xp4/vanmodified/vanbody_shader@" + "0x1d397d6a"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "VanBody_Air",
                OwnLogic: true, Rigids: AircraftPieceDeclaration),
            // ⭐ the F-35B's body (XPack01; the ground family's 0xFC3E2403, two uv sets): a shader of its OWN (f35b_main, compiled only in
            // xp1_002 / xp1_004) with no camo at all — its catalogue entry adds one (VehicleEntry.OwnCamoPresets; the F-35 was the test bake
            // of the new method). Its document's logic on the base mud's solutions, as the Rhino's van body:
            // its five fixed textures in the copy's own list, its camo on the mud's Camo. No pieces: worn as the whole body alone
            ["vehicles/xpack01/f35/f35b_main"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "F35B_Main", OwnLogic: true),
            // …and the lights its roof part carries (part 20: van body + LightsOff): sp_tank compiles them for the ground layout alone, sp_bank
            // for both (census 2026-09-26) — a database of its own, as the lights' other copy
            ["vehicles/shaders/vehiclepreset_lights@" + "0x1d397d6a"] = new("sp_bank", "Vehicles/Shaders/VehiclePreset_Lights", "VehiclePreset_Lights_Air",
                Camo: false, Rigids: AircraftPieceDeclaration),
            // ⭐ the Vodnik family's mirror glass and cabin interior (the Vodnik AA, XP5): compiled for no rigid layout in any level (census
            // 2026-09-26: the mirror only for the composite 0x4544818E / 0xFC3EE40E; the interior is a shader of its own) and sharing parts with
            // the hull's camo — the doors, the AA system's turret (part 22, 5.5 m²) —, so without them those parts stayed as shipped. Each its
            // own logic on an OPAQUE donor that has the ground piece layout: the mirror on the opaque glass (the same two textures and ColorTint,
            // measured), the interior on the mud (its two fixed textures go into the copy's own list, as the Rhino's van body)
            ["vehicles/shaders/vehiclepreset_glassmirror"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_GlassOpaque", "VehiclePreset_GlassMirror",
                OwnLogic: true, Camo: false),
            ["vehicles/gaz-3937_vodnik/gaz-3937_vodnikinterior_shader"] = new("sp_valley", "Vehicles/Shaders/VehiclePreset_Mud", "GAZ-3937_VodnikInterior",
                OwnLogic: true, Camo: false, Rigids: RigidPieceDeclaration + "," + AircraftPieceDeclaration),
        };

    /// <summary>
    /// The preset whose rigid solutions draw a vehicle's OWN camo shader (VehicleEntry.OwnCamoPresets) on its pieces — the donor its copy runs
    /// as (s_RigidTwinSources), lower case — or null. The engine fills the copy's constants from that donor's entry: a camo number the
    /// package does not set (CamoTiling, WearAmount… the van body's own entry sets none) is THAT preset's default, so the studio's preview
    /// takes the same (CamoSession.ValuesForMesh).
    /// </summary>
    public static string? OwnCamoDonorOf(string? p_Shader)
    {
        if (p_Shader == null || Catalog.VehicleCatalog.OwnCamoRegisterOf(p_Shader) == null)
            return null;

        return s_RigidTwinSources
            .Where(p_S => p_S.Value.OwnLogic && TwinShaderOf(p_S.Key).Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
            .Select(p_S => p_S.Value.Shader.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SingleOrDefault();
    }

    /// <summary>
    /// Where a rigid-capable copy of a vehicle preset comes from (a census, see s_RigidTwinSources): the level whose database
    /// compiles <paramref name="Shader"/> for the rigid declaration, and the copy's name stem. <paramref name="OwnLogic"/>: the copy
    /// draws the preset's own graph on <paramref name="Shader"/>'s solutions (see DeriveTwinRetarget). <paramref name="Camo"/>: the
    /// copy's material variation carries the camo's values (false for glass and lights).
    /// </summary>
    private sealed record VehicleTwinSource(string Level, string Shader, string Stem, bool OwnLogic = false, bool Camo = true,
        string Rigids = RigidPieceDeclaration)
    {
        public string Database => $"levels/{Level}/{Level}/shaderdb";

        /// <summary>Whether the copy has solutions for a piece cut to <paramref name="p_Rigid"/> (the source level compiles it for that).</summary>
        public bool Draws(string p_Rigid) => Rigids.Split(',').Any(p_R => p_R.Equals(p_Rigid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The vertex declaration a camo piece is cut to (see vehicle_part_clone): the rigid one the copies are made for.</summary>
    private const string RigidPieceDeclaration = "0xC83353E0";

    /// <summary>
    /// The same for an AIRCRAFT's piece: three uv sets (TexCoord0-2) after the tangent frame, stride 48 — the game's own rigid layout
    /// of those elements, measured on the AH-1Z's census once the cutter rounds the stride to 16 as the game does (2026-09-25).
    /// </summary>
    private const string AircraftPieceDeclaration = "0x1D397D6A";

    /// <summary>
    /// How a copy that draws a preset on another preset's solutions is retargeted, all DERIVED (see DeriveTwinRetarget):
    /// <paramref name="Moves"/> = the graph's texture register → the donor's, <paramref name="Adds"/> = the external textures the
    /// donor lacks and the register each is declared at, <paramref name="Samplers"/> = the graph's texture register → the sampler
    /// the preset reads it through, <paramref name="SwapUv"/> = the piece's sections carry their uv sets exchanged.
    /// </summary>
    private sealed record TwinRetarget(IReadOnlyDictionary<int, int> Moves, IReadOnlyList<(string Parameter, int Register)> Adds,
        IReadOnlyDictionary<int, int> Samplers, bool SwapUv, string Evidence)
    {
        public static readonly TwinRetarget None = new(new Dictionary<int, int>(), Array.Empty<(string, int)>(),
            new Dictionary<int, int>(), false, "the preset's own solutions");

        /// <summary>The preset's FIXED textures the donor lacks: asset name and the register the copy's own list takes it at.</summary>
        public IReadOnlyList<(string Asset, int Register)> Fixed { get; init; } = Array.Empty<(string, int)>();
    }

    /// <summary>
    /// Everything a copy that draws <paramref name="p_Preset"/> on its donor's rigid solutions needs, read off what the studio has
    /// measured — none of it written by hand:
    ///  · the texture REGISTERS by name in the two pixel stages' RDEF (texture_Diffuse… — the rule RegistersLineUp draws with in
    ///    the studio): a texture on another register moves; an EXTERNAL one the donor lacks is declared at its next free register
    ///    (a fixed one cannot be — refused);
    ///  · each texture's SAMPLER as the preset's own instructions read it (SamplerPairs: the 1uvset decals read t3 through s1);
    ///  · the uv sets: the preset's vertex stages and the donor's for the rigid declaration (the UV order census, --swaplist) — the
    ///    piece's sections go exchanged when the two hand the pair over in different orders.
    /// A preset drawn from its own solutions needs none of it.
    /// <paramref name="p_GraphOwner"/>: the preset an EDITED graph was authored on, when the copy draws that graph instead of
    /// <paramref name="p_Preset"/>'s own (BakePlan.VehicleGraphTarget) — then the registers and samplers are the graph's (its
    /// owner's RDEF), and the uv exchange is still decided by <paramref name="p_Preset"/>: the section's vertices were written for
    /// the preset it wears. <paramref name="p_MaterialGraph"/>: the graph is a material's OWN (its owner is the preset itself), only
    /// for what the evidence says.
    /// </summary>
    private static string? DeriveTwinRetarget(string p_Preset, VehicleTwinSource p_Source, out TwinRetarget p_Retarget,
        string? p_GraphOwner = null, bool p_MaterialGraph = false, IEnumerable<(string Parameter, int Register)>? p_Pictures = null)
    {
        p_Retarget = TwinRetarget.None;
        if (!p_Source.OwnLogic && p_GraphOwner == null)
            return null;

        var s_Owner = p_GraphOwner ?? p_Preset;
        if (CachedContract(s_Owner, out var s_PresetPath) is not { } s_Mine)
            return $"shader: the vehicle pieces' copy of '{p_Preset}' is retargeted by the bytecode of '{s_Owner}', and '{s_PresetPath}' is not cached (the cache step).";
        if (CachedContract(p_Source.Shader, out var s_DonorPath) is not { } s_Donor)
            return $"shader: the vehicle pieces' copy of '{p_Preset}' is retargeted onto '{p_Source.Shader}', and '{s_DonorPath}' is not cached (the cache step).";

        var s_Moves = new Dictionary<int, int>();
        var s_Adds = new List<(string, int)>();
        var s_Fixed = new List<(string, int)>();
        var s_Samplers = new Dictionary<int, int>();
        var s_Next = s_Donor.Resources.Where(p_R => p_R.IsTexture).Select(p_R => p_R.Register).DefaultIfEmpty(0).Max() + 1;
        foreach (var s_Texture in s_Mine.Resources.Where(p_R => p_R.IsTexture && p_R.Name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p_R => p_R.Register))
        {
            var s_Name = s_Texture.Name["texture_".Length..];
            var s_At = s_Donor.RegisterForExternalTexture(s_Name);
            // ⛔ a FIXED texture's name (TextureN) is the entry's N-th streamable, not an identity: the AH-6 cabin's Texture5 is its normal
            // map, the jet's Texture5 the scratches (2026-09-25) — paired by name, the cabin read the jet's scratches as its normal. The
            // donor's is taken only when both texture maps name the SAME asset there (the noMud's and the decals' Texture5 are the jet's)
            if (s_At >= 0 && Regex.IsMatch(s_Name, @"^Texture\d*$", RegexOptions.IgnoreCase) &&
                Cache.SlotMap.Load(s_Owner)?.Slots?.GetValueOrDefault(s_Texture.Register.ToString()) is { Length: > 0 } s_MineAsset &&
                Cache.SlotMap.Load(p_Source.Shader.ToLowerInvariant())?.Slots?.GetValueOrDefault(s_At.ToString()) is { Length: > 0 } s_DonorAsset &&
                !s_MineAsset.Equals(s_DonorAsset, StringComparison.OrdinalIgnoreCase))
                s_At = -1;
            if (s_At < 0)
            {
                // ⭐ a FIXED texture (texture_TextureN: the entry's own streamable list, bound by no material) the donor lacks goes
                // into the copy's own list at a free register (shader_db_add_texture), BY ITS ASSET NAME as the preset's own entry
                // lists it — the aircraft's cockpits read nothing else (F18-F_interior_D, Scratches_D…, 2026-09-25)
                if (Regex.IsMatch(s_Name, @"^Texture\d*$", RegexOptions.IgnoreCase))
                {
                    var s_Asset = Cache.SlotMap.Load(s_Owner)?.Slots?.GetValueOrDefault(s_Texture.Register.ToString());
                    if (string.IsNullOrWhiteSpace(s_Asset))
                        return $"shader: '{p_Preset}' reads its fixed texture '{s_Name}' at t{s_Texture.Register}, '{p_Source.Shader}' has none, " +
                               "and the preset's texture map (the cache step) does not name the asset — it cannot be its copy's donor.";

                    s_At = s_Next++;
                    s_Fixed.Add((s_Asset, s_At));
                }
                else
                {
                    s_At = s_Next++;
                    s_Adds.Add((s_Name, s_At));
                }
            }

            if (s_At != s_Texture.Register)
                s_Moves[s_Texture.Register] = s_At;
            s_Samplers[s_Texture.Register] = s_Mine.SamplerPairs.TryGetValue(s_Texture.Register, out var s_Sampler) ? s_Sampler : s_Mine.MaterialSamplerIndex;
        }

        // ⭐ a shader of the vehicle's OWN that takes the camo (VehicleEntry.OwnCamoPresets — the Rhino's van body): its bytecode reads no camo,
        // its document samples the pattern at the register the catalogue names, and the copy reads it where the donor binds "Camo" (the
        // entry's Camo binding lands there), through the material's sampler — the one the preset reads its own art with (Wrap: the tiling).
        // Only for a camo DOCUMENT (a graph authored on it): the shader's own translation, drawn as shipped, samples nothing there
        if (p_GraphOwner != null && Catalog.VehicleCatalog.OwnCamoRegisterOf(s_Owner) is { } s_OwnCamo && !s_Samplers.ContainsKey(s_OwnCamo))
        {
            var s_At = s_Donor.RegisterForExternalTexture("Camo");
            // ⭐ …on its OWN entry (a BODY COPY, TwinLane.BodySource — the Rhino's van body, 2026-09-27): the game's shader binds no camo at all,
            // so the copy declares it, an external texture named Camo like the pictures' (the entry's Camo binding lands on it by that name) —
            // at the register the document samples it at, or the next free one if the shader's own art sits there
            if (s_At < 0 && p_Source.Shader.Equals(s_Owner, StringComparison.OrdinalIgnoreCase))
            {
                s_At = s_Donor.Resources.Any(p_R => p_R.IsTexture && p_R.Register == s_OwnCamo) ? s_Next++ : s_OwnCamo;
                // (and nothing declared after it lands there: the Rhino's picture took t6 beside Camo@t6 — the check came before the pictures)
                s_Next = System.Math.Max(s_Next, s_At + 1);
                s_Adds.Add(("Camo", s_At));
            }
            if (s_At < 0)
                return $"shader: '{s_Owner}' takes its camo on '{p_Source.Shader}', whose bytecode binds no Camo texture — it cannot be its copy's donor.";

            if (s_At != s_OwnCamo)
                s_Moves[s_OwnCamo] = s_At;
            s_Samplers[s_OwnCamo] = s_Mine.MaterialSamplerIndex;
        }

        // ⛔ two of the graph's registers on ONE of the copy's would read the same texture — refused, named (every register the graph samples
        // lands where the table above put it: moved, or left where it is)
        var s_Landed = s_Samplers.Keys.GroupBy(p_R => s_Moves.TryGetValue(p_R, out var s_To) ? s_To : p_R).Where(p_G => p_G.Count() > 1).ToList();
        if (s_Landed.Count > 0)
            return $"shader: the copy of '{p_Preset}' on '{p_Source.Shader}' would read " +
                   string.Join("; ", s_Landed.Select(p_G => $"{string.Join(" and ", p_G.Select(p_R => $"t{p_R}"))} at t{p_G.Key}")) + " — refused rather than drawn wrong.";

        // ⭐ and the graph's PICTURES on registers its preset has no parameter at (any custom texture on a texture node must work):
        // external textures the donor lacks like any other — declared at its next free register, the node moved there, read
        // through the material's sampler. (Until then such a graph was refused as "a stale graph".)
        foreach (var (s_Parameter, s_Register) in (p_Pictures ?? Enumerable.Empty<(string, int)>()).Distinct().OrderBy(p_P => p_P.Item2))
        {
            if (s_Samplers.ContainsKey(s_Register))
                continue;

            var s_At = s_Next++;
            s_Adds.Add((s_Parameter, s_At));
            if (s_At != s_Register)
                s_Moves[s_Register] = s_At;
            s_Samplers[s_Register] = s_Mine.MaterialSamplerIndex;
        }

        // (…and once the pictures are placed as well — the Rhino's body copy put its picture on its declared Camo's register, 2026-09-27)
        var s_Doubled = s_Samplers.Keys.GroupBy(p_R => s_Moves.TryGetValue(p_R, out var s_To) ? s_To : p_R).Where(p_G => p_G.Count() > 1).ToList();
        if (s_Doubled.Count > 0 || s_Adds.GroupBy(p_A => p_A.Item2).Any(p_G => p_G.Count() > 1))
            return $"shader: the copy of '{p_Preset}' on '{p_Source.Shader}' would read " +
                   string.Join("; ", s_Doubled.Select(p_G => $"{string.Join(" and ", p_G.Select(p_R => $"t{p_R}"))} at t{p_G.Key}")
                       .Concat(s_Adds.GroupBy(p_A => p_A.Item2).Where(p_G => p_G.Count() > 1)
                           .Select(p_G => $"{string.Join(" and ", p_G.Select(p_A => p_A.Item1))} declared at t{p_G.Key}"))) + " — refused rather than drawn wrong.";

        var s_MyOrder = Cache.GameCache.UvOrderOf(p_Preset, null, out var s_MyEvidence);
        // (the donor's order for the rigid layout its copy serves: the aircraft's jet copies are 0x1D397D6A, not the ground family's)
        var s_DonorOrder = Cache.GameCache.UvOrderOf(p_Source.Shader, p_Source.Rigids.Split(',')[0], out var s_DonorEvidence);
        // a copy of the preset's OWN entry (another level's rigid flavours of it — a vehicle material's own edited graph on its own
        // preset), or of the entry the census says draws it AS IT IS (not OwnLogic: the LOD glass on the glass, pixel stages byte for
        // byte), hands the pair over as the plain copy of it does — which is how those have always shipped: no census needed
        var s_OwnEntry = p_Source.Shader.Equals(p_Preset, StringComparison.OrdinalIgnoreCase) || !p_Source.OwnLogic;
        if ((s_MyOrder == null || s_DonorOrder == null) && s_OwnEntry)
            s_MyOrder = s_DonorOrder = "0:1 (its own entry's)";
        if (s_MyOrder == null || s_DonorOrder == null)
            return $"shader: the uv order of '{(s_MyOrder == null ? p_Preset : p_Source.Shader)}' is not known — it comes from its translated vertex " +
                   "shaders (--vehiclegraphs for a vehicle that wears it), and a guess is how a hull goes dark.";
        // (three uv sets — the aircraft — only matter when the two hand them over differently: the same order exchanges nothing)
        if ((s_MyOrder.Split(':').Length > 2 || s_DonorOrder.Split(':').Length > 2) && !s_MyOrder.Equals(s_DonorOrder, StringComparison.Ordinal))
            return $"shader: '{p_Preset}' ({s_MyOrder}) on '{p_Source.Shader}' ({s_DonorOrder}): a third uv set cannot be exchanged on a piece.";

        var s_Swap = !s_MyOrder.Equals(s_DonorOrder, StringComparison.Ordinal) &&
                     Cache.GameCache.IsStraightOrder(s_MyOrder) != Cache.GameCache.IsStraightOrder(s_DonorOrder);
        p_Retarget = new TwinRetarget(s_Moves, s_Adds, s_Samplers, s_Swap,
            (p_GraphOwner != null && !p_MaterialGraph ? $"the camo's own graph (authored on {p_GraphOwner.Split('/')[^1]}): " : "") +
            $"registers {(s_Moves.Count == 0 ? "as they are" : string.Join(" ", s_Moves.OrderBy(p_M => p_M.Key).Select(p_M => $"t{p_M.Key}->t{p_M.Value}")))}" +
            $"{(s_Adds.Count == 0 ? "" : $", adds {string.Join(" ", s_Adds.Select(p_A => $"{p_A.Item1}@t{p_A.Item2}"))}")}" +
            $"{(s_Fixed.Count == 0 ? "" : $", fixed {string.Join(" ", s_Fixed.Select(p_F => $"{p_F.Item1}@t{p_F.Item2}"))}")}, samplers " +
            $"{string.Join(" ", s_Samplers.OrderBy(p_S => p_S.Key).Select(p_S => $"t{p_S.Key}:s{p_S.Value}"))}, uv {s_MyOrder} on {s_DonorOrder}" +
            $"{(s_Swap ? " (exchanged)" : "")}") { Fixed = s_Fixed };
        return null;
    }

    /// <summary>Whether a bake ships a rigid-capable copy of this preset for the pieces that wear it (see s_RigidTwinSources).</summary>
    public static bool HasRigidTwin(string p_Shader) => s_RigidTwinSources.ContainsKey(p_Shader);

    /// <summary>…for a piece cut to <paramref name="p_Rigid"/> (the ground family's 0xC83353E0, the aircraft's 0x1D397D6A).</summary>
    public static bool HasRigidTwin(string p_Shader, string p_Rigid) => RigidTwinKey(p_Shader, p_Rigid) != null;

    /// <summary>
    /// The copy a piece cut to <paramref name="p_Rigid"/> takes for <paramref name="p_Shader"/>: the preset's own entry when its source
    /// draws that layout, else a "preset@rigid" entry — the SAME preset from another source for the other layout (the Z-11W's hull wears
    /// VehiclePreset_noMud: the tanks' copy is sp_valley's as it is, the aircraft's its logic compiled on the jet, 2026-09-25). Null = none.
    /// What the planner writes into VehicleEntry.RigidTwins, and the key of that copy's lane.
    /// </summary>
    public static string? RigidTwinKey(string p_Shader, string p_Rigid)
    {
        if (s_RigidTwinSources.TryGetValue(p_Shader, out var s_Own) && s_Own.Draws(p_Rigid))
            return p_Shader.ToLowerInvariant();

        var s_Other = $"{p_Shader}@{p_Rigid}".ToLowerInvariant();
        return s_RigidTwinSources.TryGetValue(s_Other, out var s_Source) && s_Source.Draws(p_Rigid) ? s_Other : null;
    }

    /// <summary>The preset a copy's key names ("preset" or "preset@rigid").</summary>
    private static string TwinShaderOf(string p_Key) => p_Key.Split('@')[0];

    /// <summary>The copy (RigidTwinKey) a vehicle's pieces take for a preset they wear, or null when its levels draw it as it is.</summary>
    private static string? TwinOf(BakeVehicle p_Vehicle, string p_Shader) =>
        p_Vehicle.RigidTwins.FirstOrDefault(p_T => p_T.Equals(p_Shader, StringComparison.OrdinalIgnoreCase) ||
                                                   p_T.StartsWith(p_Shader + "@", StringComparison.OrdinalIgnoreCase));

    /// <summary>The package's rigid-capable copy of a vehicle preset, e.g. Vehicles/Custom/Camo_BERKUT_TANK_VehiclePreset_Mud_Decals.</summary>
    public static string VehicleTwinName(string p_Key, string p_TwinKey) => $"Vehicles/Custom/Camo_{p_Key}_{s_RigidTwinSources[p_TwinKey].Stem}";

    /// <summary>
    /// ONE COPY THE VEHICLE PIECES ARE DRAWN WITH (a "lane"): a preset out of its rigid-capable source (s_RigidTwinSources), cloned
    /// under <paramref name="Name"/> into the package's shader database. Three kinds, told apart by what they carry:
    ///  · a PRESET as it is (<paramref name="GraphPath"/> null) or with its own logic compiled in (OwnLogic: its cached translation) —
    ///    Key = the preset, shared by every vehicle of the package that wears it from a copy (VehicleEntry.RigidTwins);
    ///  · a preset drawing ONE VEHICLE's edited camo graph (BakeVehicle.ShaderGraphPath; one document per subject) —
    ///    Key = "mesh#preset", <paramref name="Vehicle"/> = that vehicle, <paramref name="GraphOwner"/> = the preset the graph was
    ///    authored on (BakePlan.VehicleGraphTarget);
    ///  · ONE MATERIAL's own edited graph (BakePlan.VehicleMaterialShaders) — Key = "mesh|id", handed to that material id alone ('#id',
    ///    tried before the shader families), <paramref name="GraphOwner"/> = its own preset.
    /// </summary>
    private sealed record TwinLane(string Key, string Shader, string Name, string? GraphPath, string? GraphOwner, BakeVehicle? Vehicle,
        int? MaterialId)
    {
        /// <summary>The source entry (RigidTwinKey) when it is not the preset's own — an edited lane of an aircraft's noMud.</summary>
        public string? SourceKey { get; init; }

        /// <summary>
        /// ⭐ A BODY COPY (so every vehicle can be baked): the same graph compiled from the preset's OWN entry in a level that
        /// fields the vehicle — its body's declarations and uv pair as the game draws that body —, for the body clone alone, where the
        /// pieces' copy cannot draw the body (its rigid source lacks the body's declaration: the SkidLoader's XPack01 mud of xp3_desert; or
        /// hands the uv pair over the other way round: the Rhino's van body on the mud). Null for every other lane. Key "…@body".
        /// </summary>
        public VehicleTwinSource? BodySource { get; init; }

        public VehicleTwinSource Source => BodySource ?? s_RigidTwinSources[SourceKey ?? (s_RigidTwinSources.ContainsKey(Key) ? Key : Shader)];
        public bool Compiled => GraphPath != null;

        /// <summary>The preset whose pixel logic the copy draws — whose samplers (and states) it carries.</summary>
        public string LogicShader => GraphOwner ?? Shader;
    }

    /// <summary>
    /// The lane a material of a vehicle's piece is drawn with, by the preset it wears: the vehicle's own edited-graph copy of it
    /// (<paramref name="p_Edited"/>), else the package's shared copy when the vehicle's levels lack a rigid solution of it
    /// (RigidTwins), else none — the level's own preset.
    /// </summary>
    private static string? PresetLaneOf(BakeVehicle p_Vehicle, string p_Shader, IReadOnlySet<string> p_Edited) =>
        p_Edited.Contains(p_Shader) ? $"{p_Vehicle.Mesh}#{p_Shader}"
        : TwinOf(p_Vehicle, p_Shader);

    /// <summary>
    /// The piece's materials whose sections carry their uv sets exchanged: those drawn with a copy whose retarget asks for it — a
    /// material's OWN copy (<paramref name="p_OwnLanes"/>: material id → its lane) where it has one, its preset's lane otherwise.
    /// </summary>
    private static List<int> PieceSwapUvOf(BakeVehicle p_Vehicle, Catalog.VehiclePiece p_Piece, IReadOnlyDictionary<string, TwinRetarget> p_Retargets,
        IReadOnlySet<string> p_Edited, IReadOnlyDictionary<int, string>? p_OwnLanes = null) =>
        p_Piece.Materials.Where(p_M => p_OwnLanes != null && p_OwnLanes.TryGetValue(p_M, out var s_Lane)
                ? p_Retargets.TryGetValue(s_Lane, out var s_Own) && s_Own.SwapUv
                : p_Vehicle.ShadersOf(p_Piece).TryGetValue(p_M, out var s_Shader) &&
                  PresetLaneOf(p_Vehicle, s_Shader, p_Edited) is { } s_PresetLane &&
                  p_Retargets.TryGetValue(s_PresetLane, out var s_Retarget) && s_Retarget.SwapUv)
            .ToList();

    /// <summary>
    /// The plan's edited vehicle materials the pieces can draw, in the plan's order (see TwinLane): each on a camo piece of its vehicle
    /// and wearing a preset some level compiles for a piece. The others are SAID, with why — nothing of the camo reaches them, whatever
    /// the preview shows.
    /// </summary>
    private static List<TwinLane> VehicleMaterialLanes(BakePlan p_Plan, Action<string> p_Log)
    {
        var s_Out = new List<TwinLane>();
        foreach (var s_Request in p_Plan.VehicleMaterialShaders)
        {
            var s_Where = $"{s_Request.Mesh.Split('/')[^1]} material #{s_Request.MaterialId}";
            var s_Vehicle = p_Plan.Vehicles.FirstOrDefault(p_V => p_V.Mesh.Equals(s_Request.Mesh, StringComparison.OrdinalIgnoreCase) &&
                                                                  p_V.Pieces.Count > 0) ??
                            p_Plan.Vehicles.FirstOrDefault(p_V => p_V.Mesh.Equals(s_Request.Mesh, StringComparison.OrdinalIgnoreCase));
            if (s_Vehicle == null)
                continue;

            if (!s_Vehicle.MaterialShaders.TryGetValue(s_Request.MaterialId, out var s_Shader))
            {
                p_Log($"  NOTE: {s_Where} was edited, and the mesh has no such material in the scan — its edit does not ship.");
                continue;
            }

            if (!s_Shader.Equals(s_Request.Shader, StringComparison.OrdinalIgnoreCase))
            {
                p_Log($"  NOTE: {s_Where} was edited on '{s_Request.Shader.Split('/')[^1]}', and it wears '{s_Shader.Split('/')[^1]}' — its edit " +
                      "does not ship (its registers are another shader's).");
                continue;
            }

            // (the body's pieces: a piece cut from another mesh numbers that mesh's materials)
            if (!s_Vehicle.Pieces.Any(p_P => BakeVehicle.FromBody(p_P) && p_P.Materials.Contains(s_Request.MaterialId)))
            {
                p_Log($"  NOTE: {s_Where} ({s_Shader.Split('/')[^1]}) was edited, and it is on none of the vehicle's camo pieces — a match " +
                      "draws it with the game's own part, so its edit does not ship.");
                continue;
            }

            // (the source the vehicle's pieces take for that preset — an aircraft's noMud is not the tanks' —, else the preset's own)
            var s_SourceKey = TwinOf(s_Vehicle, s_Shader) ?? s_Shader;
            if (!s_RigidTwinSources.TryGetValue(s_SourceKey, out var s_Source))
            {
                p_Log($"  NOTE: {s_Where} was edited, and no level database is known to compile '{s_Shader.Split('/')[^1]}' for a piece — its " +
                      "edit does not ship.");
                continue;
            }

            s_Out.Add(new TwinLane(RimeShaderEditor.Graph.ShaderGraph.MaterialKey(s_Vehicle.Mesh, s_Request.MaterialId), s_Shader,
                $"Vehicles/Custom/Camo_{p_Plan.Key}_{s_Source.Stem}_M{s_Out.Count}", s_Request.GraphPath, s_Shader, s_Vehicle,
                s_Request.MaterialId) { SourceKey = s_SourceKey });
        }

        return s_Out;
    }

    /// <summary>
    /// Every copy the plan's vehicle pieces are drawn with (see TwinLane), in a fixed order: the shared preset copies (by name — a
    /// camo that edits nothing builds exactly what it built before), then each vehicle's edited-graph copies, then the materials'
    /// own. <paramref name="p_EditedOf"/>: per vehicle, the presets its own edited graph draws (EditedPresetsOf).
    /// </summary>
    private static List<TwinLane> VehicleTwinLanes(BakePlan p_Plan, IReadOnlyDictionary<BakeVehicle, HashSet<string>> p_EditedOf,
        IReadOnlyList<TwinLane> p_MaterialLanes, IReadOnlySet<BakeVehicle>? p_BodyLanes = null)
    {
        // (keyed by the COPY each vehicle takes — RigidTwinKey: "preset", or "preset@rigid" for the aircraft's own copy of a ground
        // preset —, so a tank and a helicopter wearing the same preset get a copy each)
        var s_Presets = p_Plan.Vehicles
            .SelectMany(p_V => p_V.Pieces.SelectMany(p_P => p_P.Materials.Select(p_M => p_V.ShadersOf(p_P).GetValueOrDefault(p_M, "")))
                .Where(p_S => p_S.Length > 0 && !p_EditedOf[p_V].Contains(p_S))
                .Select(p_S => TwinOf(p_V, p_S)))
            // ⭐ …and the preset of a material whose OWN graph only carries a picture, edited preset or not: that material draws with its
            // own graph — the game's logic of its preset —, as the preview draws it, and its pieces need that preset's rigid copy
            .Concat(p_Plan.Vehicles.SelectMany(p_V => p_Plan.MaterialPictures
                .Where(p_P => p_P.Mesh.Equals(p_V.Mesh, StringComparison.OrdinalIgnoreCase) &&
                              !p_MaterialLanes.Any(p_L => ReferenceEquals(p_L.Vehicle, p_V) && p_L.MaterialId == p_P.MaterialId))
                .Select(p_P => p_V.MaterialShaders.GetValueOrDefault(p_P.MaterialId, ""))
                .Where(p_S => p_S.Length > 0)
                .Select(p_S => TwinOf(p_V, p_S))))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_K => p_K, StringComparer.OrdinalIgnoreCase)
            .Select(p_K => new TwinLane(p_K, TwinShaderOf(p_K), s_RigidTwinSources.ContainsKey(p_K) ? VehicleTwinName(p_Plan.Key, p_K) : "",
                s_RigidTwinSources.TryGetValue(p_K, out var s_Source) && s_Source.OwnLogic
                    ? Path.Combine(Settings.CacheFolder, "camographs", Cache.GameCache.SanitizeName(TwinShaderOf(p_K)) + ".json")
                    : null,
                null, null, null));

        var s_Edited = p_Plan.Vehicles
            .SelectMany((p_V, v) => p_EditedOf[p_V].OrderBy(p_S => p_S, StringComparer.OrdinalIgnoreCase)
                // a preset no level compiles for a piece gets no name here: the caller refuses the bake on it, by name
                .Select(p_S => (Shader: p_S, SourceKey: TwinOf(p_V, p_S) ?? p_S))
                .Select(p_E => p_V.Pieces.Count == 0 && BodyLevelOf(p_V) is { } s_BodyLevel
                    // ⭐ a vehicle with NO pieces (EditedPresetsOf) needs no rigid copy — a rigid copy exists to draw pieces —, only its
                    // BODY COPY: its graph compiled on the preset's own entry in its level (TwinLane.BodySource), from the start. Measured on the
                    // F-35B (2026-09-27): its f35b_main reads the world position and its tangent rows as its own vertex stage hands them, which
                    // the base mud's rigid solutions never do ("invalid subscript 'Interp4'" from fxc)
                    ? new TwinLane($"{p_V.Mesh}#{p_E.Shader}", p_E.Shader,
                        $"Vehicles/Custom/Camo_{p_Plan.Key}_{p_E.Shader.Split('/')[^1]}_Body_V{v}", p_V.ShaderGraphPath, GraphTargetOf(p_Plan, p_V), p_V,
                        null) { BodySource = new VehicleTwinSource(s_BodyLevel, p_E.Shader, $"{p_E.Shader.Split('/')[^1]}_Body") }
                    : new TwinLane($"{p_V.Mesh}#{p_E.Shader}", p_E.Shader,
                        s_RigidTwinSources.TryGetValue(p_E.SourceKey, out var s_Source) ? $"Vehicles/Custom/Camo_{p_Plan.Key}_{s_Source.Stem}_V{v}" : "",
                        p_V.ShaderGraphPath, GraphTargetOf(p_Plan, p_V), p_V, null) { SourceKey = s_RigidTwinSources.ContainsKey(p_E.SourceKey) ? p_E.SourceKey : null }));

        var s_All = s_Presets.Concat(s_Edited).Concat(p_MaterialLanes).ToList();

        // ⭐ …and the BODY COPIES of the vehicles whose own copies cannot draw their body (see TwinLane.BodySource; decided by Build after a
        // first pass, so every other vehicle's lanes stay exactly what they were): one per own lane, after all the others
        for (var v = 0; p_BodyLanes != null && v < p_Plan.Vehicles.Count; v++)
        {
            var s_Vehicle = p_Plan.Vehicles[v];
            if (!p_BodyLanes.Contains(s_Vehicle) || BodyLevelOf(s_Vehicle) is not { } s_Level)
                continue;

            foreach (var s_Lane in s_All.Where(p_L => ReferenceEquals(p_L.Vehicle, s_Vehicle) && p_L.BodySource == null).ToList())
            {
                var s_Stem = $"{s_Lane.Shader.Split('/')[^1]}_Body{(s_Lane.MaterialId is { } s_Id ? $"_M{s_Id}" : "")}";
                s_All.Add(new TwinLane($"{s_Lane.Key}@body", s_Lane.Shader, $"Vehicles/Custom/Camo_{p_Plan.Key}_{s_Stem}_V{v}", s_Lane.GraphPath,
                    s_Lane.GraphOwner, s_Vehicle, s_Lane.MaterialId) { BodySource = new VehicleTwinSource(s_Level, s_Lane.Shader, s_Stem) });
            }
        }

        return s_All;
    }

    /// <summary>
    /// The level a vehicle's BODY COPIES are compiled from: the one whose variation database the bake copies its entries from
    /// (BakeVehicle.Database, "levels/&lt;level&gt;/…") — a level that fields the vehicle, so its shader database compiles the body's presets for
    /// the body's own declarations (the dump after the copies checks it). Null when the database names no level.
    /// </summary>
    private static string? BodyLevelOf(BakeVehicle p_Vehicle) =>
        p_Vehicle.Database.Split('/') is { Length: > 2 } s_Parts && s_Parts[0].Equals("levels", StringComparison.OrdinalIgnoreCase) ? s_Parts[1].ToLowerInvariant() : null;

    /// <summary>A preset's pixel stage as the studio cached it, read for its RDEF (registers, samplers), or null when not cached.</summary>
    internal static RimeShaderEditor.Emit.ShaderContract? CachedContract(string p_Shader, out string p_Path)
    {
        p_Path = Path.Combine(Settings.MeshCache, "shaders", Cache.GameCache.SanitizeName(p_Shader.ToLowerInvariant()) + ".dxbc");
        return File.Exists(p_Path) ? RimeShaderEditor.Emit.ShaderContract.Detect(File.ReadAllBytes(p_Path)) : null;
    }

    /// <summary>
    /// ⭐ A GRAPH'S CONSTANTS BY NAME ON ANOTHER PRESET'S BLOCK (the Quad on "Alborz Test": every trace of roughness was gone, it shone far
    /// too much). An external constant node names DICE's parameter and keeps the slot (cN) it had in the preset the graph
    /// was translated from, and the emitter declares that slot as it is (packoffset) — while the engine fills the block of the entry the
    /// shader RUNS AS by NAME, laid out as THAT entry lays it out. On a donor's copy laid out otherwise the graph reads its neighbours: the
    /// Quad's MudScratches (8 constants) on the mud's rigid solutions (13) read SmoothnessMin/Max at the mud's WearPower (5) and FLIRData (0) —
    /// a mirror; the Z-11W's interior (noMud on the jet) its smoothness at the jet's FLIRScale (1) and SpecularBrightness (3). The same law as
    /// the textures' (DeriveTwinRetarget): pair by the NAME, never by the position.
    /// Each node of the material's block (externalConstants — the engine's own blocks are the same everywhere) moves to the slot
    /// <paramref name="p_Target"/> gives its name; a name the target has NO slot for gets nothing from the engine, so it is baked in as a
    /// literal (a Color node) with <paramref name="p_ValueOf"/>(name, the value typed on the node) — or refused when that knows none.
    /// Nothing is touched where every name already sits where the target keeps it (the 1uvset family on the mud: byte for byte as before).
    /// </summary>
    public static string? RelocateConstants(System.Text.Json.Nodes.JsonNode p_Graph, RimeShaderEditor.Emit.ShaderContract p_Target,
        Func<string, string?, (string? Value, string Source)> p_ValueOf, out List<string> p_Moved, out List<string> p_Baked)
    {
        p_Moved = new List<string>();
        p_Baked = new List<string>();
        foreach (var s_Node in p_Graph["Nodes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
        {
            if (s_Node?["Kind"]?.GetValue<string>() != "ExternalConstant" || s_Node["Params"] is not System.Text.Json.Nodes.JsonObject s_Params)
                continue;

            string? Param(string p_Name) => s_Params[p_Name]?.GetValue<string>();
            var s_Buffer = Param("Buffer") is { Length: > 0 } s_B ? s_B : "externalConstants";
            if (!s_Buffer.Equals("externalConstants", StringComparison.OrdinalIgnoreCase))
                continue;

            var s_Name = Param("Name") ?? "";
            var s_Bare = s_Name.StartsWith("external_", StringComparison.OrdinalIgnoreCase) ? s_Name["external_".Length..] : s_Name;
            var s_Register = int.TryParse(Param("Register"), out var s_R) ? s_R : 1;
            var s_Element = int.TryParse(Param("Element"), out var s_E) ? s_E : 0;
            var (s_ToRegister, s_ToElement) = p_Target.ExternalFieldOf(s_Bare);
            if (s_ToRegister >= 0)
            {
                if (s_ToRegister == s_Register && s_ToElement == s_Element)
                    continue;

                s_Params["Register"] = s_ToRegister.ToString(CultureInfo.InvariantCulture);
                s_Params["Element"] = s_ToElement.ToString(CultureInfo.InvariantCulture);
                p_Moved.Add($"{s_Bare} c{s_Element}->c{s_ToElement}");
                continue;
            }

            var (s_Value, s_Source) = p_ValueOf(s_Bare, Param("PreviewValue"));
            if (s_Value == null)
                return $"'{s_Bare}' has no slot in the target's constants (the engine writes nothing there) and {s_Source}.";

            s_Node["Kind"] = "Color";
            s_Node["Params"] = new System.Text.Json.Nodes.JsonObject { ["Value"] = s_Value };
            p_Baked.Add($"{s_Bare}={s_Value} ({s_Source})");
        }

        return null;
    }

    /// <summary>
    /// Whether the vehicle pieces' compiled copies read their constants where the donor keeps them (RelocateConstants). The way back —
    /// CAMO_CONSTANTS_BY_NAME_OLD=1 emits each graph's own slots as before, which is also the A/B a control bakes against.
    /// </summary>
    public static bool ConstantsByName { get; set; } = Environment.GetEnvironmentVariable("CAMO_CONSTANTS_BY_NAME_OLD") != "1";

    /// <summary>
    /// A constant's value as the preview feeds it, as the four components a literal needs: one number typed stands for the vector it
    /// means (a tiling "8" is 8,8 — CamoSession.ExpandValue, the preview's own expansion), missing components are 0 (the preview's block).
    /// </summary>
    public static string ConstantVector(string p_Name, string p_Text)
    {
        var s_Text = p_Text.Trim();
        if (!s_Text.Contains(','))
            s_Text = CamoSession.ExpandValue(p_Name, s_Text);

        var s_Parts = s_Text.Split(',').Select(p_P => p_P.Trim()).Where(p_P => p_P.Length > 0).Take(4).ToList();
        while (s_Parts.Count < 4)
            s_Parts.Add("0");
        return string.Join(",", s_Parts);
    }

    /// <summary>
    /// Whether a section wearing <paramref name="p_Other"/> is drawn with a graph authored on <paramref name="p_Target"/>: every
    /// external texture p_Other's own bytecode reads sits on the SAME register in p_Target's — the rule the studio's preview draws
    /// the object's other camo materials with (MainWindow.RegistersLineUp), so what ships is what was looked at. The mud, noMud and
    /// 1uvset presets line up; the decals preset (Decal at t3, the rest one register on) does not and keeps its own logic.
    /// </summary>
    internal static bool RegistersLineUp(string p_Target, string p_Other)
    {
        if (p_Target.Equals(p_Other, StringComparison.OrdinalIgnoreCase))
            return true;
        if (CachedContract(p_Target, out _) is not { } s_Target || CachedContract(p_Other, out _) is not { } s_Other)
            return false;

        return s_Other.Resources
            .Where(p_R => p_R.IsTexture && p_R.Name.StartsWith("texture_", StringComparison.OrdinalIgnoreCase))
            .All(p_R => s_Target.RegisterForExternalTexture(p_R.Name["texture_".Length..]) == p_R.Register);
    }

    /// <summary>
    /// The presets of ONE vehicle's pieces that ITS edited graph draws (BakeVehicle.ShaderGraphPath, authored on
    /// BakePlan.VehicleGraphTarget): each camo material's preset that is the graph's own or lines up with it. Empty when that
    /// vehicle's document was not edited beyond values — one document per subject: another vehicle's edit never
    /// reaches this one.
    /// </summary>
    private static HashSet<string> EditedPresetsOf(BakePlan p_Plan, BakeVehicle p_Vehicle)
    {
        var s_Out = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // (the preset ITS graph was authored on: the tab's, or its own starting document's — BakeVehicle.GraphTarget)
        if (p_Vehicle.ShaderGraphPath == null || GraphTargetOf(p_Plan, p_Vehicle) is not { Length: > 0 } s_Target)
            return s_Out;

        foreach (var s_Piece in p_Vehicle.Pieces)
        foreach (var s_Material in s_Piece.Materials)
            if (p_Vehicle.ShadersOf(s_Piece).TryGetValue(s_Material, out var s_Shader) &&
                p_Vehicle.CamoShaders.Contains(s_Shader, StringComparer.OrdinalIgnoreCase) && RegistersLineUp(s_Target, s_Shader))
                s_Out.Add(s_Shader);

        // ⭐ …and a vehicle with NO pieces, worn only as the whole body (VehicleSwap; the F-35B, the test bake of the new method): its BODY's
        // camo materials, which its body clone draws with these copies (Build, s_ShipsBodyClone).
        // Measured: without it the F-35B's own document shipped no copy at all and its clone would wear the game's grey (0 database entries)
        if (p_Vehicle.Pieces.Count == 0)
            foreach (var s_Shader in p_Vehicle.MaterialShaders.Values)
                if (p_Vehicle.CamoShaders.Contains(s_Shader, StringComparer.OrdinalIgnoreCase) && RegistersLineUp(s_Target, s_Shader))
                    s_Out.Add(s_Shader);

        return s_Out;
    }

    /// <summary>The preset a vehicle's edited graph was authored on: its own (BakeVehicle.GraphTarget) or the tab's (BakePlan.VehicleGraphTarget).</summary>
    private static string? GraphTargetOf(BakePlan p_Plan, BakeVehicle p_Vehicle) => p_Vehicle.GraphTarget ?? p_Plan.VehicleGraphTarget;

    /// <summary>The AUG A3's vertex declaration and the one it is re-labelled from — the framework's AUG recipe.</summary>
    private const string AugDecl = "0xB555855D";
    private const string AugFromDecl = "0xCE4574FD";

    /// <summary>The package's own first-person preset, and its AUG-layout twin.</summary>
    public static string FpCloneName(string p_Key) => $"Weapons/Shaders/Camo_{p_Key}_FP";
    public static string AugFpCloneName(string p_Key) => $"Weapons/Shaders/Camo_{p_Key}_AUGFP";

    /// <summary>The package's clone of the game's first-person NoCamo preset (stickers over the weapon as shipped).</summary>
    public static string NoCamoFpCloneName(string p_Key) => $"Weapons/Shaders/Camo_{p_Key}_NCFP";

    /// <summary>The package's first-person preset with its UV-tile gate opened: the first-person body of a weapon the game gives no camo row (AllTilesGraphFor).</summary>
    public static string AllTilesFpCloneName(string p_Key) => $"Weapons/Shaders/Camo_{p_Key}_ATFP";

    /// <summary>The clone name of one edited material's own shader, numbered in the plan's order.</summary>
    public static string MaterialCloneName(string p_Key, int p_Index) => $"Weapons/Shaders/Camo_{p_Key}_M{p_Index}";

    /// <summary>The clone name of one edited as-shipped graph of another preset (BakePlan.ShippedShaders), numbered in the plan's order.</summary>
    public static string ShippedCloneName(string p_Key, int p_Index) => $"Weapons/Shaders/Camo_{p_Key}_SHP{p_Index}";

    /// <summary>The clone name of one edited as-shipped graph of an ATTACHMENT's own preset (BakeAccessory.ShippedPreset), numbered in the plan's order.</summary>
    public static string ShippedPieceCloneName(string p_Key, int p_Index) => $"Weapons/Shaders/Camo_{p_Key}_SHPA{p_Index}";

    /// <summary>
    /// The third-person preset an accessory's body draws with — the one the sockets' meshes take a camo with
    /// in BOTH views (the first-person family multiplies by the specular as occlusion and blacks an accessory
    /// out; closed in-game, boot 26).
    /// </summary>
    public const string AccessoryPresetName = "Weapons/Shaders/WeaponPreset3P";

    /// <summary>
    /// The declarations <see cref="AccessoryPresetName"/> already has compiled solutions for — MEASURED with
    /// dump_shader_solutions (2026-09-18), not assumed. A mesh declaring one of these draws with the vanilla
    /// preset; anything else needs a twin of it re-labelled to that declaration.
    /// </summary>
    private static readonly HashSet<string> s_PresetDeclarations = new(StringComparer.OrdinalIgnoreCase)
    {
        "0xCE4574FD", "0x882E8A4C", "0x26517B0E", "0x164BFA7C", "0x212F0E96", "0x03184F6C",
    };

    /// <summary>
    /// The declaration a twin is re-labelled FROM: the static accessory layout (Pos, BinormalSign, Normal,
    /// Tangent, TexCoord0). The two declarations the preset lacks — BC3A4CC5 (+Color0) and C83353E0
    /// (+TexCoord1) — are supersets of it, which is the condition for re-labelling a solution.
    /// </summary>
    private const string AccessoryFromDecl = "0x882E8A4C";

    /// <summary>Whether a mesh's declaration needs a twin of the third-person preset.</summary>
    public static bool NeedsDeclarationTwin(string p_Declaration) =>
        p_Declaration.Length > 0 && !s_PresetDeclarations.Contains(p_Declaration);

    /// <summary>The twin's clone name for one declaration, e.g. Weapons/Shaders/Camo_ABU_ACCBC3A4CC5.</summary>
    public static string AccessoryTwinName(string p_Key, string p_Declaration) =>
        $"Weapons/Shaders/Camo_{p_Key}_ACC{p_Declaration.Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant()}";

    /// <summary>
    /// The clone's name for an accessory mesh: a token that says which camo it belongs to, plus the TAIL of
    /// the vanilla name so the leaf still says which mesh it is. Both the MeshSet header and the EBX store
    /// the string in place, so it must be EXACTLY as long as the vanilla name.
    /// </summary>
    public static string AccessoryCloneName(string p_Key, string p_EbxName)
    {
        var s_Token = $"C/{p_Key}/";
        return s_Token.Length >= p_EbxName.Length
            ? p_EbxName
            : s_Token + p_EbxName[(p_EbxName.Length - (p_EbxName.Length - s_Token.Length))..];
    }

    /// <summary>The first-person NoCamo preset that clone is cut from — what 27 of the 59 bodies wear as shipped.</summary>
    private const string NoCamoFpPresetName = "Weapons/Shaders/WeaponPresetShadowNoCamoFP";

    /// <summary>A shipped camo pattern the new texture copies its pool group from (Character_skipN).</summary>
    private const string PatternGroupDonor = "Characters/Shared/XP2_ClothCamo/ABU";

    /// <summary>A shipped row thumbnail the new one copies its pool group from (UI).</summary>
    private const string ThumbGroupDonor = "UI/Art/Persistence/Specializations/Camo/PremiumCamo_ABU";

    private const string ThumbFolder = "UI/Art/Persistence/Specializations/Camo/";

    /// <summary>The eight game camo patterns, by lowercase asset name: exact name, partition, primary instance.</summary>
    private static readonly IReadOnlyDictionary<string, (string Name, string Partition, string Instance)> s_NativeTextures =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["characters/shared/xp2_clothcamo/abu"] = ("Characters/Shared/XP2_ClothCamo/ABU", "47851ad7-6c3f-11e1-bebe-d52ef0eb99a1", "99539f3e-b4e3-1f7f-f1e5-a8fa5729b376"),
            ["characters/shared/xp2_clothcamo/atacs"] = ("Characters/Shared/XP2_ClothCamo/ATACS", "369d1b28-683a-11e1-8c04-c6342b7ccc62", "18701a38-a854-4ddf-ff9c-3dde0e63dc9e"),
            ["characters/shared/xp2_clothcamo/berkut"] = ("Characters/Shared/XP2_ClothCamo/Berkut", "47fac30a-6c3f-11e1-bebe-d52ef0eb99a1", "4cb02529-1960-e7d7-532e-d4ea5a212a51"),
            ["characters/shared/xp2_clothcamo/digiflora"] = ("Characters/Shared/XP2_ClothCamo/DigiFlora", "486f80d7-6c3f-11e1-bebe-d52ef0eb99a1", "7787de34-7948-521c-8067-319b0312fef0"),
            ["characters/shared/xp2_clothcamo/dsrttiger"] = ("Characters/Shared/XP2_ClothCamo/DsrtTiger", "48e501f9-6c3f-11e1-bebe-d52ef0eb99a1", "6d99449d-7fda-a003-0512-863b55cc0291"),
            ["characters/shared/xp2_clothcamo/kamysh"] = ("Characters/Shared/XP2_ClothCamo/Kamysh", "49c6670e-6c3f-11e1-bebe-d52ef0eb99a1", "61a20cf9-6c99-0236-5745-d872e559d6d6"),
            ["characters/shared/xp2_clothcamo/nwu"] = ("Characters/Shared/XP2_ClothCamo/NWU", "37f6244d-683a-11e1-8c04-c6342b7ccc62", "80fc2b88-f354-7b1a-f6f0-2f58f2881896"),
            ["characters/shared/xp2_clothcamo/partizan"] = ("Characters/Shared/XP2_ClothCamo/Partizan", "4b62bc7c-6c3f-11e1-bebe-d52ef0eb99a1", "0c293452-b0fa-24ff-5312-401f2cbc1856"),
        };

    /// <summary>
    /// The camo texture the first-person camo preset shows by default (its own slot set, what the studio
    /// previews a NoCamo weapon with) — bound on weapons whose entries bind no Camo when a package with no
    /// pattern ships a shader that samples it. Measured with dump_partition_json.
    /// </summary>
    private static readonly (string Name, string Partition, string Instance) DefaultCamoTexture =
        ("Weapons/Textures/DesertCamo03_D", "975ba5ca-ecb6-11df-bd6e-c1427e3d6cab", "62a83816-6779-ecd2-5509-5d338e279385");

    /// <summary>The constants a first-person variation carries besides whatever the camo overrides — the framework base's set.</summary>
    private static readonly string[] s_FpBaseline =
        { "SmoothnessMasked", "SmoothnessRegular", "SmoothnessWear", "WearAmount", "WearPower", "CamoTiling" };

    /// <summary>
    /// The constants a third-person variation carries besides the overrides. The game's own variations give
    /// the third-person material the same wear numbers as the first-person one (the M240's DsrtTiger:
    /// WearAmount 20, WearPower 10 on both), and the third-person preset reads both — a variation that left
    /// WearPower out gave the two models different wear (in third person the camo showed no wear at all, while
    /// in first person it did).
    /// </summary>
    private static readonly string[] s_ThreePBaseline = { "CamoTiling", "WearAmount", "WearPower" };

    /// <summary>
    /// The surface shaders whose materials take the FIRST-person material variation, and those that take the
    /// THIRD-person one — the way the game pairs its own variations (measured on the M240's DsrtTiger entry:
    /// the ShadowFP material and the 3P material get one each; the bullet belt and the tape get nothing).
    /// ⛔ Handing the instances out by POSITION instead put the camo preset on the M240's bullet belt (drawn
    /// white) and left its body without the variation's values. The NoCamo twins are the presets a camo
    /// replaces; the _xp2 and plain FP presets are what a few bodies (and the M249's second material) wear,
    /// and the studio previews them under the same camo preset.
    /// </summary>
    private const string FpShaderFamily =
        "weaponpresetshadowfp|weaponpresetshadownocamofp|weaponpresetshadowfp_xp2|weaponpresetfp";

    private const string ThreePShaderFamily = "weaponpreset3p|weaponpresetnocamo3p";

    /// <summary>The camo's short id: the name in capitals, anything but letters and digits as one underscore.</summary>
    public static string KeyOf(string p_Name)
    {
        var s_Key = Regex.Replace(p_Name.Trim().ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        return s_Key.Length > 0 ? s_Key : "CAMO";
    }

    /// <summary>The package folder: the key, lowercase.</summary>
    public static string FolderOf(string p_Name) => KeyOf(p_Name).ToLowerInvariant();

    /// <summary>The variation partition a camo ships for a weapon — the framework's naming, so the Lua can rebuild it.</summary>
    public static string VariationNameFor(string p_WeaponFolder, string p_Key)
    {
        var s_Folder = p_WeaponFolder.ToUpperInvariant();
        return $"Weapons/{s_Folder}/{s_Folder}_CAMO_{p_Key}";
    }

    /// <summary>The unlock name a USER camo takes: the framework builds exactly this from the index key.</summary>
    public static string UnlockNameOf(string p_Key) => $"Weapons/Custom/U_PKG_{p_Key}";

    /// <summary>
    /// The identifier the UI matches a description to an unlock by: djb2-XOR of the name AS WRITTEN.
    ///
    /// CASE SENSITIVE, unlike NameHash, which lowercases because it keys mesh variations: two
    /// normalisations of one algorithm, and the wrong one yields a plausible number nothing ever finds.
    /// 2026-09-20: a user camo used to be given a SEAT of the game's instead (HookPool), which is what the
    /// mod spent two days unpicking -- a borrowed identifier resolves to the game's description, of whatever
    /// family that description belongs to, and the UI reads it with the layout of the family it expected.
    /// </summary>
    public static uint UnlockIdentifierOf(string p_Name)
    {
        var s_Hash = 0x1505u;
        foreach (var s_Char in p_Name)
            s_Hash = unchecked((s_Hash * 0x21u) ^ s_Char);

        return s_Hash;
    }

    /// <summary>The identifier of the camo whose display name is given: its key, its unlock name, its hash.</summary>
    public static uint IdentifierOfCamo(string p_Name) => UnlockIdentifierOf(UnlockNameOf(KeyOf(p_Name)));

    /// <summary>The name the game keys a variation by: djb2-XOR of the name in lowercase.</summary>
    public static uint NameHash(string p_Name)
    {
        var s_Hash = 0x1505u;
        foreach (var s_Char in p_Name.ToLowerInvariant())
            s_Hash = unchecked((s_Hash * 0x21u) ^ s_Char);

        return s_Hash;
    }

    /// <summary>The pattern texture a package ships, by its key.</summary>
    public static string PatternTextureName(string p_Key) => $"Weapons/Custom/Camo_{p_Key}";

    /// <summary>The thumbnail texture a package ships, by its key.</summary>
    public static string ThumbnailTextureName(string p_Key) => $"{ThumbFolder}PremiumCamo_{p_Key}";

    /// <summary>
    /// ⛔ A RE-BAKE THAT FAILS KEEPS THE PACKAGE THAT WORKED (review 2026-09-29). Every build deletes its package's files first and writes the
    /// new ones as it goes, so a build that failed half-way — a picture texconv refused, a shader copy, an audit, an exception — left the
    /// folder empty or half-written while mod.json and the Lua index still declared it: the camo that worked was gone, and a declared
    /// superbundle that is missing is the endless "loading terrain" (WriteModJson). Now the files there are copied aside first, into the work
    /// folder (outside the mod: never scanned as a package), and put back unless the build comes out Ok, the files it wrote removed; after
    /// an exception the framework is registered again from what is on disk (registering is every build's last step, and only on success).
    /// CAMO_BAKE_INPLACE_OLD=1 = as before.
    /// </summary>
    internal static BakeResult KeepingThePackageThatWorked(string p_PackageDir, BakeContext p_Context, Func<BakeResult> p_Build)
    {
        if (Environment.GetEnvironmentVariable("CAMO_BAKE_INPLACE_OLD") == "1")
            return p_Build();

        var s_Aside = Path.Combine(p_Context.WorkDir, "previous_package_" + Path.GetFileName(p_PackageDir.TrimEnd('\\', '/')));
        var s_Before = new List<string>();
        try
        {
            if (Directory.Exists(s_Aside))
                Directory.Delete(s_Aside, true);
            if (Directory.Exists(p_PackageDir))
            {
                Directory.CreateDirectory(s_Aside);
                foreach (var s_File in Directory.GetFiles(p_PackageDir))
                {
                    File.Copy(s_File, Path.Combine(s_Aside, Path.GetFileName(s_File)), true);
                    s_Before.Add(Path.GetFileName(s_File));
                }
            }
        }
        catch (Exception s_Exception)
        {
            // no copy, no build: a failure after this could not be undone
            var s_Refused = new BakeResult { PackageDir = p_PackageDir };
            s_Refused.Problems.Add($"the package already in the mod could not be set aside before the build ({s_Exception.Message}) — nothing was touched.");
            p_Context.Log("BAKE FAILED — " + s_Refused.Problems[^1]);
            return s_Refused;
        }

        BakeResult? s_Result = null;
        var s_Threw = true;
        try
        {
            s_Result = p_Build();
            s_Threw = false;
            return s_Result;
        }
        finally
        {
            if (s_Threw || s_Result is not { Ok: true })
                PutThePackageBack(p_PackageDir, s_Aside, s_Before, s_Threw, p_Context);
            else
                try
                {
                    Directory.Delete(s_Aside, true);
                }
                catch (Exception)
                {
                    // a copy left in the work folder harms nothing
                }
        }
    }

    /// <summary>After a build that did not come out Ok: the package folder as it was before it (see <see cref="KeepingThePackageThatWorked"/>).</summary>
    private static void PutThePackageBack(string p_PackageDir, string p_Aside, IReadOnlyList<string> p_Before, bool p_Threw, BakeContext p_Context)
    {
        try
        {
            if (Directory.Exists(p_PackageDir))
                foreach (var s_File in Directory.GetFiles(p_PackageDir))
                    File.Delete(s_File);
            foreach (var s_Name in p_Before)
                File.Copy(Path.Combine(p_Aside, s_Name), Path.Combine(p_PackageDir, s_Name), true);
            if (p_Before.Count == 0 && Directory.Exists(p_PackageDir) && !Directory.EnumerateFileSystemEntries(p_PackageDir).Any())
                Directory.Delete(p_PackageDir);

            p_Context.Log(p_Before.Count > 0
                ? $"  The package that was in the mod before this build is back in place ({p_Before.Count} file(s)): what was baked last time still works."
                : "  Nothing of the failed build is left in the mod.");
            if (Directory.Exists(p_Aside))
                Directory.Delete(p_Aside, true);
        }
        catch (Exception s_Exception)
        {
            p_Context.Log($"⚠ The package that was in the mod before this build could NOT be put back ({s_Exception.Message}) — a copy of it is in {p_Aside}.");
            return;
        }

        if (!p_Threw)
            return;

        try
        {
            p_Context.Log("  " + FrameworkMod.Register(p_Context.ModFolder, p_Context.Log));
        }
        catch (Exception s_Exception)
        {
            p_Context.Log($"⚠ The framework could not be registered again after the failed build ({s_Exception.Message}) — bake again to register it.");
        }
    }

    /// <param name="p_BodyLanes">The vehicles that get BODY COPIES (TwinLane.BodySource) — set only by Build itself, on its second pass.</param>
    public static BakeResult Build(BakePlan p_Plan, BakeContext p_Context, IReadOnlySet<BakeVehicle>? p_BodyLanes = null) =>
        p_Plan.Folder.Trim().Length == 0
            ? BuildInPlace(p_Plan, p_Context, p_BodyLanes)
            : KeepingThePackageThatWorked(FrameworkMod.PackageDir(p_Context.ModFolder, p_Plan.Folder, p_Plan.Native), p_Context,
                () => BuildInPlace(p_Plan, p_Context, p_BodyLanes));

    /// <summary>The build itself, straight into the package folder — <see cref="Build"/> keeps the package that worked around it.</summary>
    private static BakeResult BuildInPlace(BakePlan p_Plan, BakeContext p_Context, IReadOnlySet<BakeVehicle>? p_BodyLanes)
    {
        var s_Result = new BakeResult();
        var s_Log = p_Context.Log;

        // A package of vehicles only names no weapons: what it needs is SOMETHING to paint.
        if (p_Plan.Key.Length == 0 || (p_Plan.Weapons.Count == 0 && p_Plan.Vehicles.Count == 0) || p_Plan.Identifier == 0)
        {
            s_Result.Problems.Add("the plan has no key, nothing to paint (no weapons, no vehicles) or no identifier — nothing to build.");
            return s_Result;
        }

        // a vehicle's bundle goes right behind the level bundles that carry it: without them there is nowhere it can go
        var s_Unplaced = p_Plan.Vehicles.Where(p_V => p_V.LevelBundles.Count == 0).Select(p_V => p_V.Mesh).ToList();
        if (s_Unplaced.Count > 0)
        {
            s_Result.Problems.Add($"no level bundle known for {string.Join(", ", s_Unplaced)} — measure them first (--vehiclebundles … --write).");
            return s_Result;
        }

        if (p_Plan.ValuesOnly)
        {
            // said for what the package holds: a camo of vehicles only has no weapon to keep anything
            var s_Changed = p_Plan.Weapons.Any(p_W => p_W.Overridden.Count > 0) || p_Plan.Vehicles.Any(p_V => p_V.Values.Count > 0);
            var s_Subjects = p_Plan.Weapons.Count > 0 && p_Plan.Vehicles.Count > 0 ? "weapon and vehicle"
                : p_Plan.Vehicles.Count > 0 ? "vehicle" : "weapon";
            // (the weapons whose own logic was edited — as shipped or on the basic camo — wear the package's copy of their shader: below)
            var s_OwnLogic = (p_Plan.ShaderGraphPath != null || p_Plan.NoCamoShaderGraphPath != null) && p_Plan.Weapons.Any(p_W => p_W.OwnShader);
            s_Log($"  no pattern and no native camo: the package carries VALUES ONLY — each {s_Subjects} keeps its stock " +
                  "textures (its own camo texture included)" +
                  (s_OwnLogic ? ", the weapons whose graph was edited wear the package's copy of their shader," : " and shader,") +
                  " and only the constants changed in the graph are written over them" +
                  (s_Changed || s_OwnLogic ? "." : " (none were changed: this package changes nothing visible)."));

            foreach (var s_Vehicle in p_Plan.Vehicles.Where(p_V => p_V.Values.Count > 0))
                s_Log($"    {s_Vehicle.Mesh.Split('/')[^1]}: {string.Join(", ", s_Vehicle.Values.Select(p_V => $"{p_V.Key}={p_V.Value}"))}");
        }

        // A borrowed game pattern, when the plan names one. ⛔ Not "whenever there is no pattern and the
        // package is not values-only": a sticker package with no pattern is neither (it ships a shader) and
        // names no native texture — the first sticker bake died here on a null key.
        (string Name, string Partition, string Instance)? s_Native = null;
        if (p_Plan.PatternImage == null && p_Plan.NativeTexture != null)
        {
            if (!s_NativeTextures.TryGetValue(p_Plan.NativeTexture, out var s_Known))
            {
                s_Result.Problems.Add($"'{p_Plan.NativeTexture}' is not one of the eight game camo textures.");
                return s_Result;
            }

            s_Native = s_Known;
        }

        if (p_Plan.PatternImage == null && s_Native == null && !p_Plan.ValuesOnly)
            s_Log("  no pattern and no native camo: the weapons keep the camo binding they ship with under the package's shader" +
                  (p_Plan.StickerRegister != null ? "; the stickers draw over it." : "."));

        if (p_Plan.Native && (s_Native == null || !FrameworkMod.NativeIdentifiers.Contains(p_Plan.Identifier)))
        {
            s_Result.Problems.Add("a default camo must borrow one of the eight game patterns and wear that pattern's own row.");
            return s_Result;
        }

        if (p_Plan.Native)
            s_Log($"  default camo: the game's {s_Native!.Value.Name} on the game's own row {p_Plan.Identifier} — " +
                  "no picture of its own, nothing rewritten for it in the menu.");

        var s_PackageDir = FrameworkMod.PackageDir(p_Context.ModFolder, p_Plan.Folder, p_Plan.Native);
        s_Result.PackageDir = s_PackageDir;
        Directory.CreateDirectory(s_PackageDir);
        Directory.CreateDirectory(p_Context.WorkDir);

        // ⛔ A build that fails half-way leaves the previous package behind, and a stale package ships in
        // silence with every check green. Nothing of the old build survives into the new one.
        foreach (var s_Stale in Directory.GetFiles(s_PackageDir))
            File.Delete(s_Stale);

        var s_Seed = Fnv1A(p_Plan.Key);
        string GuidFor(int p_Weapon, int p_Slot) => $"{s_Seed:x8}-{p_Weapon:x4}-4000-a000-{p_Slot:x12}";

        // --- textures -------------------------------------------------------------------------------------
        string? s_PatternDds = null;
        var s_PatternName = PatternTextureName(p_Plan.Key);
        var s_PatternPartition = GuidFor(0, 5);
        var s_PatternInstance = GuidFor(0, 6);

        if (p_Plan.PatternImage != null)
        {
            s_PatternDds = Path.Combine(p_Context.WorkDir, "pattern.dds");
            if (ConvertPattern(p_Context.Texconv, p_Plan.PatternImage, s_PatternDds, s_Log) is { } s_Error)
            {
                s_Result.Problems.Add(s_Error);
                return s_Result;
            }
        }

        var s_ThumbName = ThumbnailTextureName(p_Plan.Key);
        var s_ThumbPartition = GuidFor(0, 7);
        var s_ThumbInstance = GuidFor(0, 8);
        var s_ThumbChunk = GuidFor(0, 0x0a); // last byte even: a raw chunk, no compression flag
        var s_ThumbDds = Path.Combine(p_Context.WorkDir, "thumb.dds");
        var s_ThumbBin = Path.Combine(p_Context.WorkDir, "thumb.bin");
        // The user's picture first; then the pattern itself; then the game texture the camo borrows; and for a
        // values-only camo the first weapon's own diffuse (or, in a camo of vehicles only, the first vehicle's), so the row
        // shows the skin it changes. A default camo ships none: it sits on the game's own row for its pattern, picture included.
        // ⛔ The first values-only vehicle bake ("Metallic") died here reading the first WEAPON of a package with none.
        var s_ThumbSource = p_Plan.Native
            ? null
            : p_Plan.ThumbnailImage ?? p_Plan.PatternImage ??
              (p_Plan.NativeTexture != null
                  ? Path.Combine(Settings.TextureCache, Sanitize(p_Plan.NativeTexture) + ".png")
                  : p_Plan.Weapons.Count > 0
                      ? WeaponDiffuseOf(p_Plan.Weapons[0])
                      : p_Plan.Vehicles.Count > 0 ? CachedPngOf(p_Plan.Vehicles[0].DiffuseTexture) : null);
        var s_ThumbOwner = p_Plan.Weapons.Count > 0 ? "the first weapon" : "the first vehicle";
        if (p_Plan.ThumbnailImage != null && !p_Plan.Native)
            s_Log($"  thumbnail from the user's picture: {Path.GetFileName(p_Plan.ThumbnailImage)}");
        else if (p_Plan.ValuesOnly)
            s_Log(s_ThumbSource != null
                ? $"  thumbnail from {s_ThumbOwner}'s own diffuse: {Path.GetFileName(s_ThumbSource)}"
                : $"  thumbnail: no picture chosen and no cached diffuse for {s_ThumbOwner} — the row has no picture of its own.");
        var s_HasThumb = s_ThumbSource != null &&
                         MakeThumbnail(p_Context.Texconv, s_ThumbSource, p_Context.WorkDir, s_ThumbDds, s_ThumbBin, s_Log);

        // --- the sticker layers: one texture per weapon that has stickers, one empty one for the rest -------
        // Bound under the material parameter "Sticker" on every entry, so the package's shader (which
        // declares that parameter) always finds a texture: a weapon with nothing placed gets a 4×4 of
        // transparent texels rather than whatever the engine binds to a parameter no material names.
        var s_StickerTextures = new List<(string Name, string Dds, string Partition, string Instance, string Donor, bool Srgb)>();
        var s_StickerOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (p_Plan.StickerRegister != null)
        {
            if (p_Plan.ShaderGraphPath == null)
            {
                s_Result.Problems.Add("stickers need the package's own shader, and this plan ships none (the graph was not marked as edited).");
                return s_Result;
            }

            var s_StickerDir = Path.Combine(p_Context.WorkDir, "stickers");
            Directory.CreateDirectory(s_StickerDir);
            string? s_EmptyBinding = null;
            var s_Animated = p_Plan.StickerSheet != null;

            // The GIF's frame sheet, once: every weapon's map points into it.
            string? s_SheetBinding = null;
            if (s_Animated)
            {
                var s_SheetDds = Path.Combine(s_StickerDir, "sheet.dds");
                if (ConvertLayer(p_Context.Texconv, p_Plan.StickerSheet!, s_SheetDds, s_Log, "frame sheet") is { } s_SheetError)
                {
                    s_Result.Problems.Add("animated sticker sheet: " + s_SheetError);
                    return s_Result;
                }

                var s_SheetPartition = GuidFor(0, 0x46);
                var s_SheetInstance = GuidFor(0, 0x47);
                // Pool group of a WEAPON diffuse, like the layers. MEASURED in-game (2026-09-12, F2000, GIF with a
                // black background): in the 512² cloth-camo pattern group (Character_skipN) the sheet's DXT5
                // alpha read as 1 everywhere (a transparent stroke still drew; the black background vanished
                // into the body); in the weapon diffuse's group the same bytes drew with their alpha.
                var s_SheetDonor = p_Plan.Weapons.SelectMany(p_W => p_W.Textures)
                    .FirstOrDefault(p_T => p_T.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) ?? PatternGroupDonor;
                s_StickerTextures.Add((StickerSheetTextureName(p_Plan.Key), s_SheetDds, s_SheetPartition, s_SheetInstance, s_SheetDonor, true));
                s_SheetBinding = $"{StickerSheetParameter}>{s_SheetPartition}:{s_SheetInstance}";
            }

            // (and the emblem slot's atlas — beside the GIF's sheet too, at a register of its own: StickerGraph.GifKeepsEmblem)
            string? s_AtlasBinding = null;
            if (p_Plan.EmblemAtlas != null)
            {
                // The emblem slot's shape atlas, once: every weapon's map points into its square. Coverage in the ALPHA, so the pool
                // group of a weapon diffuse (the frame sheet's lesson: in the cloth-camo group a DXT5's alpha read as 1).
                var s_AtlasDds = Path.Combine(s_StickerDir, "emblem_atlas.dds");
                if (ConvertLayer(p_Context.Texconv, p_Plan.EmblemAtlas, s_AtlasDds, s_Log, "emblem atlas") is { } s_AtlasError)
                {
                    s_Result.Problems.Add("emblem slot atlas: " + s_AtlasError);
                    return s_Result;
                }

                var s_AtlasPartition = GuidFor(0, 0x4E);
                var s_AtlasInstance = GuidFor(0, 0x4F);
                var s_AtlasDonor = p_Plan.Weapons.SelectMany(p_W => p_W.Textures)
                    .FirstOrDefault(p_T => p_T.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) ?? PatternGroupDonor;
                s_StickerTextures.Add((EmblemAtlasTextureName(p_Plan.Key), s_AtlasDds, s_AtlasPartition, s_AtlasInstance, s_AtlasDonor, false));
                s_AtlasBinding = $"{EmblemAtlasParameter}>{s_AtlasPartition}:{s_AtlasInstance}";
            }

            // The shared empties: a mesh with nothing composed for it binds these, so the package's shader
            // (which samples every parameter) never reads a parameter no material binds.
            string? s_EmptyMapBinding = null;
            string? Empty(string p_Stem, string p_Parameter, string p_Name, int p_Guid, bool p_IsMap, bool p_Srgb, ref string? p_Binding, string p_Donor)
            {
                if (p_Binding != null)
                    return null;

                var s_Tga = Path.Combine(s_StickerDir, p_Stem + ".tga");
                var s_Dds = Path.Combine(s_StickerDir, p_Stem + ".dds");
                WriteEmptyLayerTga(s_Tga);
                var s_Error = p_IsMap ? ConvertMap(p_Context.Texconv, s_Tga, s_Dds, s_Log) : ConvertLayer(p_Context.Texconv, s_Tga, s_Dds, s_Log);
                if (s_Error != null)
                    return s_Error;

                var s_Partition = GuidFor(0, p_Guid);
                var s_Instance = GuidFor(0, p_Guid + 1);
                s_StickerTextures.Add((p_Name, s_Dds, s_Partition, s_Instance, p_Donor, p_Srgb));
                p_Binding = $"{p_Parameter}>{s_Partition}:{s_Instance}";
                return null;
            }

            var s_LayerCount = 0;
            var s_MapCount = 0;
            for (var w = 0; w < p_Plan.Weapons.Count; w++)
            {
                var s_Weapon = p_Plan.Weapons[w];
                // The pool group of the weapon's own diffuse: sized for a weapon texture, which a layer is.
                var s_Donor = s_Weapon.Textures.FirstOrDefault(p_T => p_T.EndsWith("_d", StringComparison.OrdinalIgnoreCase))
                              ?? PatternGroupDonor;

                // Per body mesh: each has its own unwrap, so each gets the layer and maps composed for it.
                for (var m = 0; m < s_Weapon.Meshes.Count; m++)
                {
                    var s_Mesh = s_Weapon.Meshes[m];
                    var s_Tag = MeshTag(s_Mesh);
                    var s_FileStem = $"{FolderOf(s_Weapon.Folder)}_{s_Tag}";
                    var s_Bindings = new List<string>();

                    if (s_Weapon.StickerLayers.TryGetValue(s_Mesh, out var s_LayerTga))
                    {
                        var s_Name = StickerTextureName(p_Plan.Key, KeyOf(s_Weapon.Folder), s_Tag);
                        var s_Dds = Path.Combine(s_StickerDir, $"{s_FileStem}.dds");
                        if (ConvertLayer(p_Context.Texconv, s_LayerTga, s_Dds, s_Log) is { } s_LayerError)
                        {
                            s_Result.Problems.Add($"sticker layer of {s_Weapon.Folder} ({s_Tag}): {s_LayerError}");
                            return s_Result;
                        }

                        var s_Partition = GuidFor(w + 1, 0x40 + m * 0x10);
                        var s_Instance = GuidFor(w + 1, 0x41 + m * 0x10);
                        s_StickerTextures.Add((s_Name, s_Dds, s_Partition, s_Instance, s_Donor, true));
                        s_Bindings.Add($"{StickerParameter}>{s_Partition}:{s_Instance}");
                        s_LayerCount++;
                    }
                    else
                    {
                        if (Empty("empty", StickerParameter, StickerTextureName(p_Plan.Key, "NONE"), 0x42, false, true, ref s_EmptyBinding, s_Donor) is { } s_EmptyError)
                        {
                            s_Result.Problems.Add("empty sticker layer: " + s_EmptyError);
                            return s_Result;
                        }

                        s_Bindings.Add(s_EmptyBinding!);
                    }

                    // This mesh's map (or the empty one): the side each sticker was painted on, and the
                    // animated sticker's coordinates (green y, alpha x).
                    if (s_Weapon.StickerSides.TryGetValue(s_Mesh, out var s_MapTga))
                    {
                        var s_MapName = StickerMapTextureName(p_Plan.Key, KeyOf(s_Weapon.Folder), s_Tag);
                        var s_MapDds = Path.Combine(s_StickerDir, $"{s_FileStem}_map.dds");
                        if (ConvertMap(p_Context.Texconv, s_MapTga, s_MapDds, s_Log) is { } s_MapError)
                        {
                            s_Result.Problems.Add($"sticker map of {s_Weapon.Folder} ({s_Tag}): {s_MapError}");
                            return s_Result;
                        }

                        var s_MapPartition = GuidFor(w + 1, 0x44 + m * 0x10);
                        var s_MapInstance = GuidFor(w + 1, 0x45 + m * 0x10);
                        s_StickerTextures.Add((s_MapName, s_MapDds, s_MapPartition, s_MapInstance, s_Donor, false));
                        s_Bindings.Add($"{StickerMapParameter}>{s_MapPartition}:{s_MapInstance}");
                        s_MapCount++;
                    }
                    else
                    {
                        if (Empty("empty_map", StickerMapParameter, StickerMapTextureName(p_Plan.Key, "NONE", null), 0x4A, true, false, ref s_EmptyMapBinding, s_Donor) is { } s_EmptyMap)
                        {
                            s_Result.Problems.Add("empty sticker map: " + s_EmptyMap);
                            return s_Result;
                        }

                        s_Bindings.Add(s_EmptyMapBinding!);
                    }

                    // The animated sticker's shared sheet, and the emblem slot's atlas.
                    if (s_SheetBinding != null)
                        s_Bindings.Add(s_SheetBinding);
                    if (s_AtlasBinding != null)
                        s_Bindings.Add(s_AtlasBinding);

                    s_StickerOf[$"{s_Weapon.Folder}|{s_Mesh}"] = string.Join(",", s_Bindings);
                }
            }

            s_Log($"  stickers: {s_LayerCount} mesh layer(s)" +
                  $"{(s_EmptyBinding != null ? " + the empty layer for the rest" : "")}, sampled at t{p_Plan.StickerRegister} as '{StickerParameter}', " +
                  $"{s_MapCount} mesh map(s) (side, coordinates) at t{p_Plan.StickerRegister + 1} as '{StickerMapParameter}'" +
                  (s_Animated ? $"; animated: the frame sheet at t{p_Plan.StickerSheetRegister ?? p_Plan.StickerRegister + 2} as '{StickerSheetParameter}'" : "") +
                  (p_Plan.EmblemAtlas != null
                      ? $"; the emblem slot: the shape atlas at t{p_Plan.StickerRegister + 2} as '{EmblemAtlasParameter}', its layers the shader's " +
                        $"{RimeShaderEditor.Graph.Palette.EmblemConstantPrefix}0..{RimeShaderEditor.Graph.Palette.EmblemConstantPrefix}{RimeShaderEditor.Graph.EmblemSlot.Layers - 1} constants" +
                        (s_Animated ? " (beside the animated sticker)." : ".")
                      : s_Animated ? " — NO emblem slot beside it (an older graph whose GIF took its register)." : "."));
        }

        // --- the pictures of the pieces' edited as-shipped graphs (the snow ACOG: right in the preview, wrong in game): one texture of the
        // package's own per distinct picture, bound on the piece's entries under the parameter its node
        // reads — never a game texture replaced by name ---------------------------------------------------------------------------------
        // ⭐ a custom texture on a texture node must work on any weapon, accessory or vehicle — every source of a picture, each with the
        // pool group of its owner's own diffuse (sized for an object's texture)
        static string DonorOf(IEnumerable<string> p_Textures) =>
            p_Textures.FirstOrDefault(p_T => p_T.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) ?? PatternGroupDonor;
        string MeshDonor(string p_Mesh) =>
            p_Plan.Weapons.FirstOrDefault(p_W => p_W.Meshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase)) is { } s_OnWeapon ? DonorOf(s_OnWeapon.Textures)
            : p_Plan.Accessories.FirstOrDefault(p_A => p_A.Meshes.Any(p_M => p_M.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))) is { } s_OnPiece ? DonorOf(s_OnPiece.Textures)
            : p_Plan.Vehicles.FirstOrDefault(p_V => p_V.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)) is { } s_OnVehicle && s_OnVehicle.DiffuseTexture.Length > 0 ? s_OnVehicle.DiffuseTexture
            : PatternGroupDonor;
        var s_AllPictures = new List<(GraphPicture Picture, string Owner, string Donor)>();
        foreach (var s_Weapon in p_Plan.Weapons)
            s_AllPictures.AddRange(s_Weapon.Pictures.Concat(s_Weapon.ShippedPictures).Select(p_P => (p_P, s_Weapon.Folder, DonorOf(s_Weapon.Textures))));
        foreach (var s_Piece in p_Plan.Accessories)
            s_AllPictures.AddRange(s_Piece.Pictures.Concat(s_Piece.ShippedPictures).Select(p_P => (p_P, $"{s_Piece.Weapon}/{s_Piece.Tag}", DonorOf(s_Piece.Textures))));
        foreach (var s_Vehicle in p_Plan.Vehicles)
            s_AllPictures.AddRange(s_Vehicle.Pictures.Select(p_P => (p_P, s_Vehicle.Mesh.Split('/')[^1], s_Vehicle.DiffuseTexture.Length > 0 ? s_Vehicle.DiffuseTexture : PatternGroupDonor)));
        foreach (var s_Material in p_Plan.MaterialPictures)
            s_AllPictures.AddRange(s_Material.Pictures.Select(p_P => (p_P, $"{s_Material.Mesh.Split('/')[^1]} #{s_Material.MaterialId}", MeshDonor(s_Material.Mesh))));

        var s_PictureTextures = new List<(string Name, string Dds, string Partition, string Instance, string Donor, bool Srgb)>();
        var s_PictureAt = new Dictionary<(string File, bool Srgb), (string Partition, string Instance)>();
        foreach (var (s_Picture, s_Owner, s_Donor) in s_AllPictures)
        {
            if (!s_PictureAt.ContainsKey((s_Picture.File, s_Picture.Srgb)))
            {
                var s_At = s_PictureAt.Count;
                var s_Dds = Path.Combine(p_Context.WorkDir, "pictures", $"picture{s_At}.dds");
                if (ConvertPicture(p_Context.Texconv, s_Picture.File, s_Dds, s_Log) is { } s_PictureError)
                {
                    s_Result.Problems.Add($"the picture on t{s_Picture.Register} of {s_Owner}'s graph: {s_PictureError}");
                    s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
                    return s_Result;
                }

                // their own guid space (0xF500)
                var s_Texture = (GuidFor(0xF500, 0x10 + s_At * 2), GuidFor(0xF500, 0x11 + s_At * 2));
                s_PictureTextures.Add((PictureTextureName(p_Plan.Key, s_At), s_Dds, s_Texture.Item1, s_Texture.Item2, s_Donor, s_Picture.Srgb));
                s_PictureAt[(s_Picture.File, s_Picture.Srgb)] = s_Texture;
            }

            s_Log($"  {s_Owner}: the picture '{Path.GetFileName(s_Picture.File)}' of its graph ships as the package's texture, bound as " +
                  $"'{s_Picture.Parameter}' at t{s_Picture.Register}" +
                  (s_Picture.From is { } s_From
                      ? $" (a parameter its copy declares there — moved from t{s_From}, where its shader reads a fixed texture no material binds)."
                      : s_Picture.Declare ? " (a parameter its copy declares there: the preset has none at that register)." : " (the preset's own parameter)."));
        }

        // A parameter a copy DECLARES is bound on every material that copy draws: a subject that shares the copy's logic but has no
        // picture of its own at that register gets a 4×4 black one ("a parameter no material binds is a question the engine was never
        // asked" — the stickers' empty layer, same rule).
        (string Partition, string Instance)? s_EmptyPicture = null;
        (string Partition, string Instance) EmptyPicture()
        {
            if (s_EmptyPicture is { } s_Made)
                return s_Made;

            var s_Png = Path.Combine(p_Context.WorkDir, "pictures", "empty.png");
            Directory.CreateDirectory(Path.GetDirectoryName(s_Png)!);
            var s_Black = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte) 0, 4 * 4 * 4).Select((p_B, i) => i % 4 == 3 ? (byte) 255 : p_B).ToArray(), 16);
            using (var s_Stream = File.Create(s_Png))
            {
                var s_Encoder = new PngBitmapEncoder();
                s_Encoder.Frames.Add(BitmapFrame.Create(s_Black));
                s_Encoder.Save(s_Stream);
            }

            var s_Dds = Path.Combine(p_Context.WorkDir, "pictures", "empty.dds");
            if (ConvertPicture(p_Context.Texconv, s_Png, s_Dds, s_Log) is { } s_Error)
                throw new InvalidOperationException("the empty picture: " + s_Error);

            s_EmptyPicture = (GuidFor(0xF500, 0x0E), GuidFor(0xF500, 0x0F));
            s_PictureTextures.Add((PictureTextureName(p_Plan.Key, 999), s_Dds, s_EmptyPicture.Value.Partition, s_EmptyPicture.Value.Instance, PatternGroupDonor, true));
            return s_EmptyPicture.Value;
        }

        // the bindings of some pictures on the materials given ONE variation instance (mvdb_add_entry's '@' scope), plus an empty picture
        // for every parameter the copy those materials draw declares and none of these pictures fills
        string PictureBindings(IEnumerable<GraphPicture> p_Pictures, string p_Scope, IEnumerable<(string Parameter, int Register)>? p_Declared = null)
        {
            var s_Pairs = new List<string>();
            var s_Filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Picture in p_Pictures.DistinctBy(p_P => p_P.Parameter, StringComparer.OrdinalIgnoreCase))
            {
                var (s_Part, s_Inst) = s_PictureAt[(s_Picture.File, s_Picture.Srgb)];
                s_Pairs.Add($"{s_Picture.Parameter}>{s_Part}:{s_Inst}@{p_Scope}");
                s_Filled.Add(s_Picture.Parameter);
            }

            foreach (var (s_Parameter, _) in (p_Declared ?? Enumerable.Empty<(string, int)>()).Where(p_D => !s_Filled.Contains(p_D.Parameter)).Distinct())
            {
                var (s_Part, s_Inst) = EmptyPicture();
                s_Pairs.Add($"{s_Parameter}>{s_Part}:{s_Inst}@{p_Scope}");
            }

            return string.Join(",", s_Pairs);
        }

        // (a picture at a register its game preset has no parameter at needs a copy to declare it there: with no copy it cannot be bound —
        // said, never dropped in silence)
        List<GraphPicture> Bindable(IEnumerable<GraphPicture> p_Pictures, bool p_HasCopy, string p_Owner)
        {
            var s_Out = new List<GraphPicture>();
            foreach (var s_Picture in p_Pictures)
                if (p_HasCopy || !s_Picture.Declare)
                    s_Out.Add(s_Picture);
                else
                    s_Log($"  NOTE: {p_Owner}: the picture '{Path.GetFileName(s_Picture.File)}' at t{s_Picture.Register} has no copy of the shader to declare " +
                          "its parameter on (its graph's logic is the game's) — not carried.");
            return s_Out;
        }

        // the weapons' own pictures on their first-person instance (GuidFor(i, 3)), decided HERE — before the texture annex is written —
        // because a copy's declared parameter with no picture on some weapon brings the empty one along
        var s_WeaponPictureBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < p_Plan.Weapons.Count; i++)
        {
            var s_Weapon = p_Plan.Weapons[i];
            var (s_Lane, s_Pictures) = PictureLaneOf(p_Plan, s_Weapon);
            var s_Declared = s_Lane == null ? null : DeclaredOnLane(p_Plan, s_Lane);
            var s_Bindings = PictureBindings(Bindable(s_Pictures, s_Lane != null, s_Weapon.Folder), GuidFor(i + 1, 3), s_Declared);
            if (s_Bindings.Length > 0)
                s_WeaponPictureBindings[s_Weapon.Folder] = s_Bindings;
        }

        // the materials' own pictures, each on its material's variation instance: its clone's when its logic was edited too (the '#id'
        // instance the entry already hands out), else a BARE one of its own — no shader, no constants: the material keeps all it has and
        // only the binding moves
        var s_MaterialPictureOn = new Dictionary<MaterialPictureRequest, (string Instance, bool Bare)>();
        var s_PieceMeshes = p_Plan.Accessories.SelectMany(p_A => p_A.Meshes).Select(p_M => p_M.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var s_PieceMaterialShaders = p_Plan.MaterialShaders.Where(p_M => s_PieceMeshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase)).ToList();
        var s_BareOfWeapon = new int[p_Plan.Weapons.Count];
        var s_BareOfPieces = 0;
        foreach (var s_Request in p_Plan.MaterialPictures)
        {
            var w = p_Plan.Weapons.FindIndex(p_W => p_W.Meshes.Contains(s_Request.Mesh, StringComparer.OrdinalIgnoreCase));
            if (w >= 0)
            {
                var s_Own = p_Plan.MaterialShaders.Where(p_M => p_Plan.Weapons[w].Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase)).ToList();
                var s_At = s_Own.FindIndex(p_M => p_M.Mesh.Equals(s_Request.Mesh, StringComparison.OrdinalIgnoreCase) && p_M.MaterialId == s_Request.MaterialId);
                s_MaterialPictureOn[s_Request] = s_At >= 0 ? (GuidFor(w + 1, 0x40 + s_At), false) : (GuidFor(w + 1, 0xA0 + s_BareOfWeapon[w]++), true);
            }
            else if (s_PieceMeshes.Contains(s_Request.Mesh, StringComparer.OrdinalIgnoreCase))
            {
                var s_At = s_PieceMaterialShaders.FindIndex(p_M => p_M.Mesh.Equals(s_Request.Mesh, StringComparison.OrdinalIgnoreCase) && p_M.MaterialId == s_Request.MaterialId);
                s_MaterialPictureOn[s_Request] = s_At >= 0 ? (GuidFor(0xF200, 0x100 + s_At), false) : (GuidFor(0xF200, 0x200 + s_BareOfPieces++), true);
            }
            // (a vehicle's material: the vehicles' entries hand it out)
        }

        // the '#id' specs of the bare instances of one mesh, and the bindings of every material picture on it
        IEnumerable<string> BareSpecs(string p_Mesh) => s_MaterialPictureOn
            .Where(p_P => p_P.Value.Bare && p_P.Key.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
            .Select(p_P => $"{p_P.Value.Instance}@{p_P.Key.Shader.Split('/')[^1]}#{p_P.Key.MaterialId}");
        string BareSpecsOf(string p_Mesh) => string.Concat(BareSpecs(p_Mesh).Select(p_S => "," + p_S));
        string MaterialPictureBindingsOf(string p_Mesh) => string.Join(",", s_MaterialPictureOn
            .Where(p_P => p_P.Key.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
            .Select(p_P => PictureBindings(Bindable(p_P.Key.Pictures, !p_P.Value.Bare, $"{p_Mesh.Split('/')[^1]} #{p_P.Key.MaterialId}"), p_P.Value.Instance))
            .Where(p_B => p_B.Length > 0));

        // --- partitions -----------------------------------------------------------------------------------
        var s_Json = new JsonSerializerOptions { WriteIndented = true };

        // --- the package's own shader, when the graph was edited beyond values ------------------------------
        string? s_ShaderDb = null;
        ShaderStub? s_FpClone = null;
        ShaderStub? s_AugFpClone = null;
        ShaderStub? s_NoCamoFpClone = null;
        // (the first-person preset with its UV-tile gate opened, for the body of the weapons the game gives no camo row where the package
        // draws the emblem / stickers)
        ShaderStub? s_AllTilesFpClone = null;
        // (the AUG twin of the package's shader only when the AUG wears that shader — one document per subject)
        var s_HasAug = p_Plan.Weapons.Any(p_W => p_W.Aug && p_W.OwnShader);
        var s_MaterialClones = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);
        var s_AccessoryTwins = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);
        // (a piece of an as-shipped package keeps its own preset — or a copy of it —, never the third-person one: no twin for it)
        var s_NeedsTwins = p_Plan.Accessories.Where(p_A => !p_A.AsShipped).SelectMany(p_A => p_A.Meshes).Any(p_M => NeedsDeclarationTwin(p_M.Declaration));
        // the weapons' own preset clone is for WEAPONS (and their attachments): a camo of vehicles only takes its edited graph through
        // the vehicle pieces' copies below, never through the weapons' first-person chain
        var s_WeaponShader = p_Plan.Weapons.Count > 0 || p_Plan.Accessories.Count > 0;
        var s_AccessoryClonesOf = new Dictionary<BakeAccessory, ShaderStub>();
        var s_ShippedClones = new List<ShaderStub>();
        if ((p_Plan.ShaderGraphPath != null && s_WeaponShader) || (p_Plan.NoCamoShaderGraphPath != null && s_WeaponShader) ||
            p_Plan.ShippedShaders.Count > 0 || p_Plan.MaterialShaders.Count > 0 || s_NeedsTwins ||
            p_Plan.Accessories.Any(p_A => p_A.GraphPath != null || p_A.ShippedGraphPath != null) || NeedsAllTiles(p_Plan))
        {
            var s_ShaderError = PrepareShader(p_Plan, p_Context, s_HasAug, GuidFor, s_Json,
                out s_ShaderDb, out s_FpClone, out s_AugFpClone, out s_NoCamoFpClone, out s_AllTilesFpClone, out s_MaterialClones,
                out s_AccessoryTwins, out s_AccessoryClonesOf, out s_ShippedClones, p_Plan.StickerRegister);
            if (s_ShaderError != null)
            {
                s_Result.Problems.Add(s_ShaderError);
                s_Log("BAKE FAILED — " + s_ShaderError);
                return s_Result;
            }
        }

        // --- the vehicle pieces' rigid-capable presets: a piece material whose preset the multiplayer levels compile for composite
        // meshes only (the M1A2 turret's front plates, drawn with the decals preset) gets the preset as another level compiles it
        var s_VehicleTwins = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);

        // ⭐ EACH VEHICLE'S OWN EDITED GRAPH ON ITS PIECES (whatever is edited in the graph must bake; one document per subject): the
        // presets of a vehicle's pieces' camo materials that ITS graph draws (the graph's own and the siblings whose registers line up,
        // as the studio's preview draws them) get a copy compiled from THAT graph — that vehicle's own copies, the same way the 1uvset
        // copies are compiled from theirs. Another vehicle of the package keeps its own logic.
        var s_EditedOf = p_Plan.Vehicles.ToDictionary(p_V => p_V, p_V => EditedPresetsOf(p_Plan, p_V));
        foreach (var s_Vehicle in p_Plan.Vehicles.Where(p_V => p_V.ShaderGraphPath != null && p_V.Pieces.Count > 0 && GraphTargetOf(p_Plan, p_V) != null))
            s_Log(s_EditedOf[s_Vehicle].Count > 0
                ? $"  shader: {s_Vehicle.Mesh.Split('/')[^1]}'s own graph (authored on {GraphTargetOf(p_Plan, s_Vehicle)!.Split('/')[^1]}" +
                  $"{(s_Vehicle.GraphTarget != null ? ", its own starting document's — not the tab's" : "")}) draws its pieces' " +
                  $"{string.Join(", ", s_EditedOf[s_Vehicle].Select(p_S => p_S.Split('/')[^1]))} materials; their other materials keep their presets' own logic."
                : $"  shader: {s_Vehicle.Mesh.Split('/')[^1]}'s own graph (authored on {GraphTargetOf(p_Plan, s_Vehicle)!.Split('/')[^1]}) matches none of its " +
                  "pieces' camo materials — they keep their presets' own logic.");
        // (a vehicle with no pieces: its own graph draws its body's camo materials, see EditedPresetsOf)
        foreach (var s_Vehicle in p_Plan.Vehicles.Where(p_V => p_V.ShaderGraphPath != null && p_V.Pieces.Count == 0 && GraphTargetOf(p_Plan, p_V) != null))
            s_Log(s_EditedOf[s_Vehicle].Count > 0
                ? $"  shader: {s_Vehicle.Mesh.Split('/')[^1]}'s own graph (authored on {GraphTargetOf(p_Plan, s_Vehicle)!.Split('/')[^1]}) draws its BODY's " +
                  $"{string.Join(", ", s_EditedOf[s_Vehicle].Select(p_S => p_S.Split('/')[^1]))} materials (no pieces: worn as the whole body alone)."
                : $"  shader: {s_Vehicle.Mesh.Split('/')[^1]}'s own graph (authored on {GraphTargetOf(p_Plan, s_Vehicle)!.Split('/')[^1]}) matches none of its " +
                  "body's camo materials — they keep their presets' own logic.");

        // ⭐ and THE VEHICLE MATERIALS EDITED ON THEIR OWN GRAPHS: one more copy each, keyed by "mesh|id" and handed to
        // that material alone. The lanes come in a fixed order: the shared preset copies first, so a camo that edits nothing builds
        // exactly what it built before.
        var s_Lanes = VehicleTwinLanes(p_Plan, s_EditedOf, VehicleMaterialLanes(p_Plan, s_Log), p_BodyLanes);
        var s_LaneOf = s_Lanes.ToDictionary(p_L => p_L.Key, StringComparer.OrdinalIgnoreCase);

        // ⭐ THE BODY CLONE (the camo window's preview of the whole vehicle) draws every material with the GAME's preset: a vehicle whose own
        // graph or one of whose materials' graphs only a copy can draw (its lanes — the M1A2 "Snow": right in game, but picked in the camo
        // selection window it still showed the default) ships none, and the window previews it
        // by its pieces — the path the vehicles without a catalog body take, confirmed in game (2026-09-24), which draw exactly what a match does
        // ⭐⭐ …UNLESS its own copies are compiled for its body too (everything moved to option A — the camo worn as the whole body, the
        // vehicle made again with it, VehicleSwap.lua; the RHIB uses a custom shader because it needed a custom mask):
        // a copy is its source entry cloned WHOLE, every declaration that level compiled, so where those include every one of the body's
        // (BakeVehicle.BodyDeclarations — the RHIB's composite 0xFC3E2403, which sp_valley's mud has) the body clone is drawn with the
        // vehicle's own graph, as its pieces are (BodyLanesOf). Decided once the copies are made (their sources' declarations are dumped
        // in the same mount): see below, after PrepareVehicleTwins. Confirmed in game on the RHIB (everything works).
        // The AIRCRAFT too, since 2026-09-27 (step 2 of moving everything to option A): they were left out after the Mi-28's body
        // clone showed no camo in the window (2026-09-25) — measured the same day, before the fix that takes the pattern from CamoA at
        // t3 as well: that bake was very likely "values only" (the F-18's was), never isolated. The clone binds every camo parameter the materials bind (CamoA/CamoB,
        // VehicleCamoTexture), and worn as a whole body it hides no part: the helicopters' first-person cockpit, drawn in place of the body
        // and following its part mask, stays the game's.
        var s_ShipsBodyClone = new HashSet<BakeVehicle>();
        var s_SourceDeclarations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (s_Lanes.FirstOrDefault(p_L => p_L.BodySource == null && !s_RigidTwinSources.ContainsKey(p_L.SourceKey ?? p_L.Key) &&
                                          !s_RigidTwinSources.ContainsKey(p_L.Shader)) is { } s_NoSource)
        {
            s_Result.Problems.Add($"shader: the vehicle pieces need a rigid-capable copy of '{s_NoSource.Shader}', and no level database is known to hold one.");
            s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
            return s_Result;
        }

        // how each copy that draws a preset on another's solutions is retargeted: derived from the caches, logged
        var s_Retargets = new Dictionary<string, TwinRetarget>(StringComparer.OrdinalIgnoreCase);
        // (the pictures a copy compiled from a graph declares: its vehicle's own graph's, or that material's own graph's)
        IEnumerable<(string Parameter, int Register)> LanePictures(TwinLane p_Lane) =>
            (p_Lane.MaterialId is { } s_Id
                ? p_Plan.MaterialPictures
                    .Where(p_P => p_P.Mesh.Equals(p_Lane.Vehicle!.Mesh, StringComparison.OrdinalIgnoreCase) && p_P.MaterialId == s_Id)
                    .SelectMany(p_P => p_P.Pictures)
                : p_Lane.Vehicle != null && p_Lane.Compiled ? p_Lane.Vehicle.Pictures : Enumerable.Empty<GraphPicture>())
            .Where(p_P => p_P.Declare)
            .Select(p_P => (p_P.Parameter, p_P.Register));
        foreach (var s_Lane in s_Lanes)
        {
            if (DeriveTwinRetarget(s_Lane.Shader, s_Lane.Source, out var s_Retarget, s_Lane.GraphOwner, s_Lane.MaterialId != null,
                    LanePictures(s_Lane)) is { } s_RetargetError)
            {
                s_Result.Problems.Add(s_RetargetError);
                s_Log("BAKE FAILED — " + s_RetargetError);
                return s_Result;
            }

            // (a body copy draws the body with the preset's own entry, as the game draws that body: nothing to exchange)
            if (s_Lane.BodySource != null)
                s_Retarget = s_Retarget with { SwapUv = false, Evidence = s_Retarget.Evidence + " — FOR THE BODY CLONE, from its own entry in " + s_Lane.Source.Level };
            s_Retargets[s_Lane.Key] = s_Retarget;
            var s_On = $"'{s_Lane.Shader.Split('/')[^1]}' on {s_Lane.Source.Shader.Split('/')[^1]}'s {(s_Lane.BodySource != null ? "own" : "rigid")} solutions: {s_Retarget.Evidence}.";
            if (s_Lane.MaterialId is { } s_MaterialId)
                s_Log($"  shader: {s_Lane.Vehicle!.Mesh.Split('/')[^1]} material #{s_MaterialId} draws ITS OWN edited graph of {s_On}");
            else if (s_Lane.Vehicle != null)
                s_Log($"  shader: {s_Lane.Vehicle.Mesh.Split('/')[^1]}'s own camo graph as {s_On}");
            else if (s_Lane.Source.OwnLogic)
                s_Log($"  shader: {s_On}");
        }

        // the copies' databases after the first (one per further source level): each its own resource in the shaders bundle
        var s_ExtraShaderDbs = new List<string>();
        if (s_Lanes.Count > 0)
        {
            var s_VehicleDbs = new List<string>();
            var s_TwinError = s_ShaderDb != null
                ? "shader: a package with its own weapon shader AND vehicle pieces that need a preset copy is not supported yet (two databases)."
                : PrepareVehicleTwins(p_Plan, p_Context, s_Lanes, s_Retargets, GuidFor, s_Json, out s_VehicleDbs, out s_VehicleTwins,
                    out s_SourceDeclarations);
            if (s_TwinError != null)
            {
                s_Result.Problems.Add(s_TwinError);
                s_Log("BAKE FAILED — " + s_TwinError);
                return s_Result;
            }

            s_ShaderDb = s_VehicleDbs[0];
            s_ExtraShaderDbs = s_VehicleDbs.Skip(1).ToList();
        }

        // the body clones (see above, "UNLESS"): each of the vehicle's OWN copies (its graph's, its materials') compiled for every
        // declaration of its body — or no own copy at all (the game's presets draw it, as always). An expansion vehicle's too, since
        // 2026-09-27 (when every vehicle was baked): its clone is the EBX alone on the game's MeshSet (BodyInCatalog).
        // ⭐ …and a vehicle whose own copies CANNOT draw its body gets BODY COPIES (TwinLane.BodySource): Build runs again once with them
        // for those vehicles alone — every other one's lanes and output stay as they were (the RHIB's, confirmed in game)
        var s_NeedBodyCopies = new HashSet<BakeVehicle>();
        foreach (var s_Vehicle in p_Plan.Vehicles)
        {
            // (with body copies, those alone decide — the pieces' copies stay the pieces')
            var s_WithBody = p_BodyLanes?.Contains(s_Vehicle) == true;
            var s_Own = s_Lanes.Where(p_L => ReferenceEquals(p_L.Vehicle, s_Vehicle) && (p_L.BodySource != null) == s_WithBody).ToList();
            var s_Missing = s_Own
                .Select(p_L => (Lane: p_L, Decls: s_SourceDeclarations.GetValueOrDefault($"{p_L.Source.Database}|{p_L.Source.Shader}".ToLowerInvariant())))
                .Select(p_X => (p_X.Lane, Missing: s_Vehicle.BodyDeclarations.Count == 0
                    ? new List<string> { "(the body's declarations are unknown — not in a cache log)" }
                    : s_Vehicle.BodyDeclarations.Where(p_D => p_X.Decls == null || !p_X.Decls.Contains(p_D)).ToList()))
                .Where(p_X => p_X.Missing.Count > 0)
                .ToList();
            // (a copy whose rigid solutions read the uv pair the other way round has it exchanged in the pieces' own geometry — the body
            // clone keeps the body's, so it is refused rather than guessed)
            var s_Swapped = s_Own.Where(p_L => s_Retargets.TryGetValue(p_L.Key, out var s_R) && s_R.SwapUv).ToList();

            if ((s_Swapped.Count > 0 || s_Missing.Count > 0) && p_BodyLanes == null && s_Own.Count > 0 && BodyLevelOf(s_Vehicle) is { } s_BodyLevel)
            {
                s_NeedBodyCopies.Add(s_Vehicle);
                s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]}: its own copies cannot draw its body (" +
                      string.Join("; ", s_Swapped.Select(p_L => $"'{p_L.Name.Split('/')[^1]}' reads the uv pair the other way round")
                          .Concat(s_Missing.Select(p_X => $"'{p_X.Lane.Name.Split('/')[^1]}' of {p_X.Lane.Source.Level} has no solution for {string.Join(", ", p_X.Missing)}"))) +
                      $") — body copies of them are compiled from its own entries in {s_BodyLevel}.");
            }
            else if (s_Swapped.Count > 0)
                s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]}: no body clone — its copy " +
                      $"{string.Join(", ", s_Swapped.Select(p_L => $"'{p_L.Name.Split('/')[^1]}'"))} reads the uv pair the other way round, which its pieces " +
                      "carry exchanged in their own geometry and a body clone cannot — so its camo is worn by pieces and the window previews it by them.");
            else if (s_Missing.Count == 0)
            {
                s_ShipsBodyClone.Add(s_Vehicle);
                if (s_Own.Count > 0)
                    s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]}: body clone drawn with its OWN copies ({string.Join(", ", s_Own.Select(p_L => p_L.Name.Split('/')[^1]))}) — " +
                          $"their sources are compiled for its body's {string.Join(", ", s_Vehicle.BodyDeclarations)}.");
            }
            else
                s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]}: no body clone — " + string.Join("; ", s_Missing.Select(p_X =>
                          $"its copy '{p_X.Lane.Name.Split('/')[^1]}' ({p_X.Lane.Source.Shader.Split('/')[^1]} of {p_X.Lane.Source.Level}) has no solution for " +
                          string.Join(", ", p_X.Missing))) + " — so its camo is worn by pieces and the window previews it by them.");
        }

        if (s_NeedBodyCopies.Count > 0)
        {
            s_Log($"  vehicles: again with body copies for {string.Join(", ", s_NeedBodyCopies.Select(p_V => p_V.Mesh.Split('/')[^1]))} " +
                  "(the first pass's shader work is redone; nothing was written to the framework).");
            // (the build itself again, inside the one Build that keeps the package that worked)
            return BuildInPlace(p_Plan, p_Context, s_NeedBodyCopies);
        }

        if (s_PatternDds != null)
            WriteTextureStub(Path.Combine(p_Context.WorkDir, "tex_pattern.json"), s_PatternName, s_PatternPartition, s_PatternInstance, s_Json);

        if (s_HasThumb)
            WriteTextureStub(Path.Combine(p_Context.WorkDir, "tex_thumb.json"), s_ThumbName, s_ThumbPartition, s_ThumbInstance, s_Json);

        foreach (var s_Layer in s_StickerTextures.Concat(s_PictureTextures))
            WriteTextureStub(Path.ChangeExtension(s_Layer.Dds, ".json"), s_Layer.Name, s_Layer.Partition, s_Layer.Instance, s_Json);

        // The Camo binding the entries get — none for a values-only camo, whose entries keep what they bind.
        var s_CamoTexture = s_PatternDds != null
            ? $"Camo>{s_PatternPartition}:{s_PatternInstance}"
            : s_Native != null
                ? $"Camo>{s_Native.Value.Partition}:{s_Native.Value.Instance}"
                : "";

        // …on a vehicle, on each camo parameter its materials bind (the jet family: CamoA and CamoB, no Camo — "Camo>" alone added a
        // parameter its shader never reads and left the stock pattern on)
        string VehicleCamoTexture(BakeVehicle p_Vehicle) =>
            s_CamoTexture.Length == 0 || p_Vehicle.CamoParameters.Count == 0
                ? s_CamoTexture
                : string.Join(",", p_Vehicle.CamoParameters.Select(p_P => p_P + s_CamoTexture["Camo".Length..]));

        var s_Entries = new List<(string Weapon, string Mesh, string Db, uint Hash)>();
        var s_Variations = new List<(string Weapon, string Name, uint Hash, string Json)>();
        var s_Referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        // ⛔ A PIECE-ONLY PACKAGE BUILDS NO BODY VARIATION AT ALL. Not an empty one, not one nobody points
        // at: none. What it ships is the attachment's clones below, and the weapons are only here to say whose
        // attachments they are.
        for (var i = 0; p_Plan.PaintsBody && i < p_Plan.Weapons.Count; i++)
        {
            var s_Weapon = p_Plan.Weapons[i];
            var s_Index = i + 1;
            var s_Name = VariationNameFor(s_Weapon.Folder, p_Plan.Key);
            var s_Hash = NameHash(s_Name);
            var s_Path = Path.Combine(p_Context.WorkDir, $"var_{s_Weapon.Folder.ToLowerInvariant()}.json");

            // A weapon whose body wears the NoCamo preset binds the no-camo clone when the package has one
            // (stickers over the weapon as shipped); the AUG keeps its own twin (its declaration).
            // (and one whose body wears another preset, its as-shipped graph edited, binds ITS copy of that preset: ShippedShaders)
            var s_ShippedLane = p_Plan.ShippedShaders.FindIndex(p_S => p_S.Weapons.Contains(s_Weapon.Folder, StringComparer.OrdinalIgnoreCase));
            // (and one the game gives no camo row binds the first-person preset with its UV-tile gate opened, the package's values on it —
            // what its first-person body wears; AllTilesGraphFor. Derived from the package's own graph when it has one: a pistol whose own
            // document kept the preset's logic still takes it — the gate is the one thing its body cannot do without)
            var s_OwnClone = s_ShippedLane >= 0 && s_ShippedLane < s_ShippedClones.Count
                ? s_ShippedClones[s_ShippedLane]
                : s_Weapon.NoCamoRow && s_AllTilesFpClone != null
                    ? s_AllTilesFpClone
                    : !s_Weapon.OwnShader
                        ? null // its own document kept the preset's logic: the game's camo preset, with the pattern and the numbers
                        : s_NoCamoFpClone != null && s_Weapon.WearsNoCamo && !s_Weapon.Aug ? s_NoCamoFpClone : s_FpClone;

            // The edited materials of THIS weapon's meshes, each with its own instance in the variation.
            var s_Extras = new List<(string Instance, ShaderStub? Stub)>();
            foreach (var s_Material in p_Plan.MaterialShaders.Where(p_M =>
                         s_Weapon.Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase)))
            {
                var s_Key = RimeShaderEditor.Graph.ShaderGraph.MaterialKey(s_Material.Mesh, s_Material.MaterialId);
                if (s_MaterialClones.TryGetValue(s_Key, out var s_Stub))
                    s_Extras.Add((GuidFor(s_Index, 0x40 + s_Extras.Count), s_Stub));
            }

            // …and a bare one per material of this weapon whose own graph only carries a picture
            foreach (var (s_Picture, s_On) in s_MaterialPictureOn.Where(p_P => p_P.Value.Bare && s_Weapon.Meshes.Contains(p_P.Key.Mesh, StringComparer.OrdinalIgnoreCase)))
                s_Extras.Add((s_On.Instance, null));

            WriteVariation(s_Path, s_Name, s_Hash, GuidFor(s_Index, 1), GuidFor(s_Index, 2), GuidFor(s_Index, 3),
                GuidFor(s_Index, 4), s_Weapon, p_Plan.ValuesOnly, s_Json, s_OwnClone, s_Weapon.OwnShader ? s_AugFpClone : null, s_Extras);
            s_Variations.Add((s_Weapon.Folder, s_Name, s_Hash, s_Path));

            foreach (var s_Texture in s_Weapon.Textures)
                s_Referenced.Add(s_Texture);

            for (var m = 0; m < s_Weapon.Meshes.Count; m++)
                s_Entries.Add((s_Weapon.Folder, s_Weapon.Meshes[m],
                    $"MV_{p_Plan.Key}_{KeyOf(s_Weapon.Folder)}_{(s_Weapon.Meshes[m].Contains("_3p_", StringComparison.OrdinalIgnoreCase) ? "3P" : "1P")}{(m > 1 ? m.ToString() : "")}",
                    s_Hash));
        }

        // ⭐ the projected slot's frames of every weapon (EmblemSlot.FramesOf: 12 float4 each), for the Lua that writes them into each
        // weapon's own component (the camo's shared variation gave only the first copy its square)
        var s_EmblemFrames = p_Plan.Weapons.Where(p_W => p_W.EmblemProjection.Length > 0)
            .ToDictionary(p_W => p_W.Folder, p_W => RimeShaderEditor.Graph.EmblemSlot.FramesOf(p_W.EmblemProjection));
        if (s_EmblemFrames.Count > 0)
        {
            File.WriteAllText(Path.Combine(p_Context.WorkDir, "emblem_frames.json"), JsonSerializer.Serialize(s_EmblemFrames, s_Json), new UTF8Encoding(false));
            s_Log($"  emblem slot: the frames of {s_EmblemFrames.Count} weapon(s) written for the weapons' own components (emblem_frames.json){(Environment.GetEnvironmentVariable("CAMO_EMBLEM_FRAMES_IN_VARIATION") == "1" ? " — and, CAMO_EMBLEM_FRAMES_IN_VARIATION=1, into their variations" : "")}.");
        }

        if (s_Native != null)
            s_Referenced.Add(s_Native.Value.Name);

        if (!p_Plan.PaintsBody)
            s_Log($"  this camo is OF A PIECE: no weapon body is painted and no camo row is added — " +
                  $"{p_Plan.Accessories.Count} attachment(s) on {p_Plan.Weapons.Count} weapon(s).");

        // --- the accessories (phase F, A.3) -------------------------------------------------------------
        // One clone per mesh, one (clone, 0) entry per clone — the key EVERY engine socket reads — and ONE
        // material variation per accessory, on the third-person preset (or its declaration twin), carrying
        // the host weapon's wear. The accessories' material variations live in a partition of their own.
        var s_AccessoryMaterials = new List<(string Instance, BakeAccessory Accessory, ShaderStub? Twin)>();
        var s_AccessoryPartition = GuidFor(0, 0x60);
        for (var a = 0; a < p_Plan.Accessories.Count; a++)
        {
            var s_Accessory = p_Plan.Accessories[a];
            // its OWN copy when its graph's logic was edited (already re-labelled to its declaration), else the plain twin or the preset
            // (as shipped: the copy of the preset it wears, or nothing — its entries then carry only its edited materials)
            var s_Twin = s_AccessoryClonesOf.GetValueOrDefault(s_Accessory) ?? (s_Accessory.AsShipped ? null : s_Accessory.Meshes
                .Select(p_M => p_M.Declaration)
                .Where(NeedsDeclarationTwin)
                .Select(p_D => s_AccessoryTwins.GetValueOrDefault(p_D))
                .FirstOrDefault(p_S => p_S != null));

            s_AccessoryMaterials.Add((GuidFor(0, 0x61 + a), s_Accessory, s_Twin));
            foreach (var s_Texture in s_Accessory.Textures)
                s_Referenced.Add(s_Texture);
        }

        // The meshes to clone: one per distinct mesh across the package's accessories (a shared rail is one
        // clone), each carrying the material variation of the first accessory that switches it on.
        var s_AccessoryClones = new List<(BakeAccessoryMesh Mesh, string Instance, BakeAccessory Accessory)>();
        for (var a = 0; a < p_Plan.Accessories.Count; a++)
        foreach (var s_Mesh in p_Plan.Accessories[a].Meshes)
            if (!s_AccessoryClones.Any(p_C => p_C.Mesh.Mesh.Equals(s_Mesh.Mesh, StringComparison.OrdinalIgnoreCase)))
                s_AccessoryClones.Add((s_Mesh, s_AccessoryMaterials[a].Instance, p_Plan.Accessories[a]));

        // ⭐ the vehicles' materials whose OWN graph carries a picture (any custom texture must work): an instance each
        // on the body clone (no shader — the body clone draws every material with the game's preset) and, unless a copy of their own
        // graph draws them on the pieces, one on the pieces (the copy of its preset they are drawn with there, or none) — both with the
        // camo's numbers on a camo material, so nothing changes but where the picture's binding can land: that material alone
        var s_VehiclePictureOn = new List<(MaterialPictureRequest Request, int Vehicle, string Body, string Piece, ShaderStub? PieceTwin, bool Camo, string? OwnLane)>();
        for (var v = 0; v < p_Plan.Vehicles.Count; v++)
        {
            var s_Vehicle = p_Plan.Vehicles[v];
            var s_At = 0;
            foreach (var s_Request in p_Plan.MaterialPictures.Where(p_P => p_P.Mesh.Equals(s_Vehicle.Mesh, StringComparison.OrdinalIgnoreCase)))
            {
                var s_OwnLane = s_Lanes.FirstOrDefault(p_L => p_L.MaterialId == s_Request.MaterialId && ReferenceEquals(p_L.Vehicle, s_Vehicle) &&
                                                              p_L.BodySource == null)?.Key;
                var s_Preset = s_Vehicle.MaterialShaders.GetValueOrDefault(s_Request.MaterialId, s_Request.Shader);
                // (its OWN graph draws it — the game's logic of its preset, as the preview shows it —, never the vehicle's edited graph: the
                // preset's shared rigid copy when its levels need one, else none)
                var s_PresetLane = PresetLaneOf(s_Vehicle, s_Preset, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                s_VehiclePictureOn.Add((s_Request, v, GuidFor(0xF000 + v, 0x300 + s_At), GuidFor(0xF000 + v, 0x380 + s_At),
                    s_PresetLane != null && s_VehicleTwins.TryGetValue(s_PresetLane, out var s_Twin) ? s_Twin : null,
                    s_Vehicle.CamoShaders.Contains(s_Preset, StringComparer.OrdinalIgnoreCase), s_OwnLane));
                s_At++;
            }
        }

        // The vehicles' material variations: one per vehicle, no shader, the camo's values -- in a partition of their own.
        var s_VehiclePartition = GuidFor(0, 0xC0);
        var s_VehicleMaterialsPath = Path.Combine(p_Context.WorkDir, "vehicle_materials.json");
        if (p_Plan.Vehicles.Count > 0)
            WriteVehicleMaterials(s_VehicleMaterialsPath, VehicleMaterialsName(p_Plan.Key), s_VehiclePartition,
                p_Plan.Vehicles.Select((p_V, i) => (GuidFor(0, 0xC1 + i), p_V)).ToList(), s_Json,
                // one more per (vehicle, copy): the same numbers, drawn with the copy (a vehicle's own copies — its edited graph's, its
                // materials' — that vehicle's only)
                p_Plan.Vehicles.SelectMany((p_V, v) => s_Lanes
                    .Select((p_L, t) => (Lane: p_L, At: t))
                    .Where(p_T => p_T.Lane.Vehicle == null || ReferenceEquals(p_T.Lane.Vehicle, p_V))
                    .Select(p_T => (GuidFor(0xF000 + v, 0x40 + p_T.At), p_V, (ShaderStub?) s_VehicleTwins[p_T.Lane.Key], p_T.Lane.Source.Camo)))
                    // (and the materials' own-picture instances, above)
                    .Concat(s_VehiclePictureOn.SelectMany(p_P => new[]
                    {
                        (p_P.Body, p_Plan.Vehicles[p_P.Vehicle], (ShaderStub?) null, p_P.Camo),
                        (p_P.Piece, p_Plan.Vehicles[p_P.Vehicle], p_P.PieceTwin, p_P.Camo),
                    }))
                    .ToList());

        // the edited materials of the accessories' meshes (a glass, a reticle — not the painted body, which rides in the accessory's own
        // copy): one instance each in the accessories' partition, handed to that material alone on its mesh's entry ('#id')
        var s_AccessoryMaterialShaders = new List<(string Instance, ShaderStub Stub, MaterialShaderRequest Material)>();
        foreach (var s_Material in p_Plan.MaterialShaders.Where(p_M =>
                     s_AccessoryClones.Any(p_C => p_C.Mesh.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase))))
            if (s_MaterialClones.TryGetValue(RimeShaderEditor.Graph.ShaderGraph.MaterialKey(s_Material.Mesh, s_Material.MaterialId), out var s_Stub))
                s_AccessoryMaterialShaders.Add((GuidFor(0xF200, 0x100 + s_AccessoryMaterialShaders.Count), s_Stub, s_Material));

        var s_AccessoryMaterialsPath = Path.Combine(p_Context.WorkDir, "accessory_materials.json");
        if (s_AccessoryMaterials.Count > 0)
        {
            WriteAccessoryMaterials(s_AccessoryMaterialsPath, AccessoryMaterialsName(p_Plan.Key), s_AccessoryPartition,
                s_AccessoryMaterials, s_Json, s_AccessoryMaterialShaders.Select(p_M => (p_M.Instance, (ShaderStub?) p_M.Stub))
                    // (and the bare instances of the pieces' materials whose own graph only carries a picture)
                    .Concat(s_MaterialPictureOn.Where(p_P => p_P.Value.Bare && s_PieceMeshes.Contains(p_P.Key.Mesh, StringComparer.OrdinalIgnoreCase))
                        .Select(p_P => (p_P.Value.Instance, (ShaderStub?) null)))
                    .ToList());
            s_Log($"  accessories: {p_Plan.Accessories.Count} ({string.Join(", ", p_Plan.Accessories.Select(p_A => $"{p_A.Weapon}/{p_A.Tag}"))}) — " +
                  $"{s_AccessoryClones.Count} mesh clone(s) " +
                  $"({p_Plan.Accessories.Sum(p_A => p_A.Meshes.Count) - s_AccessoryClones.Count} shared), " +
                  $"{s_AccessoryTwins.Count} declaration twin(s).");
        }

        // The Camo binding PER WEAPON. A package with no pattern and no borrowed camo but a shader of its own
        // (stickers) still SAMPLES Camo on every weapon: the ones whose own entries bind none (the NoCamo
        // presets' weapons, the M416 among them) get the preset's default pattern — what the preview showed
        // them with — referenced like any game texture. Decided here, before the script: the references
        // are written ahead of the entries.
        var s_CamoOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Weapon in p_Plan.Weapons)
        {
            var s_Binding = s_CamoTexture;
            // A weapon on the no-camo clone samples no camo at all: nothing to bind, nothing to fall back to.
            var s_OnNoCamoClone = s_NoCamoFpClone != null && s_Weapon.WearsNoCamo && !s_Weapon.Aug && s_Weapon.OwnShader;
            // (nor one on a copy of the preset it wears as shipped: its own bindings are that preset's — ShippedShaders)
            var s_OnShippedClone = p_Plan.ShippedShaders.Any(p_S => p_S.Weapons.Contains(s_Weapon.Folder, StringComparer.OrdinalIgnoreCase));
            if (s_Binding.Length == 0 && p_Plan.ShaderGraphPath != null && s_Weapon.OwnShader && !s_Weapon.HasCamoBinding && !s_OnNoCamoClone &&
                !s_OnShippedClone)
            {
                s_Binding = $"Camo>{DefaultCamoTexture.Partition}:{DefaultCamoTexture.Instance}";
                s_Referenced.Add(DefaultCamoTexture.Name);
                s_Log($"  {s_Weapon.Folder}: its entries bind no camo texture and the package's shader samples one — " +
                      $"bound to the preset's default '{DefaultCamoTexture.Name}', as previewed.");
            }

            s_CamoOf[s_Weapon.Folder] = s_Binding;
        }

        // --- the Rime script ------------------------------------------------------------------------------
        var s_Sb = Path.Combine(p_Context.ModFolder, "sb");
        var s_Stem = FrameworkMod.StemOf(p_Plan.Folder, p_Plan.Native);
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");

        // NONCAS: the generated resources. A generated texture shipped from a CAS bundle renders black.
        // Only when there is something generated to carry: an empty superbundle declared in mod.json is
        // the same endless load as a missing one.
        var s_HasNc = s_PatternDds != null || s_HasThumb || s_StickerTextures.Count > 0 || s_PictureTextures.Count > 0;
        if (s_HasNc)
            s_Script.AppendLine($"build_sb Win32/{s_Stem}/packnc Frostbite2_0 \"{s_Sb}\" false");

        if (s_PatternDds != null || s_StickerTextures.Count > 0 || s_PictureTextures.Count > 0)
        {
            s_Script.AppendLine($"build_bundle win32/{s_Stem}/packncv");
            // Fully resident (Streaming=false): a mod does not fill the game's streaming pool. sRGB, like the
            // game's own patterns — the shader's wear mask depends on the textures being read that way.
            if (s_PatternDds != null)
                s_Script.AppendLine($"add_dds_texture \"{s_PatternName}\" \"{s_PatternDds}\" true false false \"\" \"{PatternGroupDonor}\" false");

            // The sticker layers: sRGB art like the pattern, resident, in the pool group of the weapon's own
            // diffuse (a layer is a weapon-sized texture, not a 512² pattern).
            foreach (var s_Layer in s_StickerTextures)
                s_Script.AppendLine($"add_dds_texture \"{s_Layer.Name}\" \"{s_Layer.Dds}\" {(s_Layer.Srgb ? "true" : "false")} false false \"\" \"{s_Layer.Donor}\" false");

            // the edited graphs' pictures: resident like the layers, sRGB unless the node reads a normal map (the preview's rule)
            foreach (var s_Picture in s_PictureTextures)
                s_Script.AppendLine($"add_dds_texture \"{s_Picture.Name}\" \"{s_Picture.Dds}\" {(s_Picture.Srgb ? "true" : "false")} false false \"\" \"{s_Picture.Donor}\" false");

            s_Script.AppendLine("check_textures");
            s_Script.AppendLine("build");
        }

        if (s_HasThumb)
        {
            s_Script.AppendLine($"build_bundle win32/{s_Stem}/packncui");
            // Flags 9 (Streaming | OnDemandLoaded), group UI, header only — the pixels go to the chunk store.
            s_Script.AppendLine($"add_dds_texture \"{s_ThumbName}\" \"{s_ThumbDds}\" false true false \"UI\" \"{ThumbGroupDonor}\" true \"{s_ThumbChunk}\"");
            s_Script.AppendLine($"add_json_partition \"{s_ThumbName}\" \"{Path.Combine(p_Context.WorkDir, "tex_thumb.json")}\"");
            s_Script.AppendLine($"remove_chunk {s_ThumbChunk}");
            s_Script.AppendLine("build");
        }

        if (s_HasNc)
            s_Script.AppendLine("build");

        if (s_HasThumb)
        {
            s_Script.AppendLine($"build_sb Win32/{s_Stem}/chunks Frostbite2_0 \"{s_Sb}\" false");
            s_Script.AppendLine($"add_chunk {s_ThumbChunk} \"{s_ThumbBin}\" \"{s_ThumbName.ToLowerInvariant()}\"");
            s_Script.AppendLine("build");
        }

        // The package's own shader: its two-entry database under a FRESH resource name plus the stubs the
        // variations point at, in a bundle the loader puts BEFORE the level's (a fresh-named database only
        // adds its keys; the level's own is never touched — measured, and the way the AUG's bundle ships).
        // (the no-camo copy counts on its own: a package whose only own logic is an as-shipped graph edited on a no-camo weapon)
        if (s_ShaderDb != null && (s_FpClone != null || s_NoCamoFpClone != null || s_AllTilesFpClone != null || s_ShippedClones.Count > 0 ||
                                   s_MaterialClones.Count > 0 || s_AccessoryTwins.Count > 0 || s_VehicleTwins.Count > 0 || s_AccessoryClonesOf.Count > 0))
        {
            s_Script.AppendLine($"build_sb Win32/{s_Stem}/shaders Frostbite2_0 \"{s_Sb}\" true");
            s_Script.AppendLine($"build_bundle win32/{s_Stem}/shadersv");

            // The edited materials' own clones, each a stub the variation points that material at.
            for (var m = 0; m < p_Plan.MaterialShaders.Count; m++)
            {
                var s_Key = RimeShaderEditor.Graph.ShaderGraph.MaterialKey(p_Plan.MaterialShaders[m].Mesh, p_Plan.MaterialShaders[m].MaterialId);
                if (s_MaterialClones.TryGetValue(s_Key, out var s_MatStub))
                    s_Script.AppendLine($"add_json_partition \"{s_MatStub.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", $"stub_mat{m}.json")}\"");
            }

            // The copies of the accessories edited on their own graphs, each its stub (one per copy, however many accessories share it).
            foreach (var s_AceStub in s_AccessoryClonesOf.Values.Distinct())
                s_Script.AppendLine($"add_json_partition \"{s_AceStub.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", $"stub_{s_AceStub.Name.Split('/')[^1]}.json")}\"");

            // The accessories' declaration twins, each a stub their material variation points at.
            var s_TwinAt = 0;
            foreach (var s_Twin in s_AccessoryTwins.OrderBy(p_T => p_T.Key, StringComparer.OrdinalIgnoreCase))
                s_Script.AppendLine($"add_json_partition \"{s_Twin.Value.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", $"stub_acc{s_TwinAt++}.json")}\"");

            // The vehicle pieces' preset copies, each a stub their material variation points at.
            for (var t = 0; t < s_Lanes.Count; t++)
                s_Script.AppendLine($"add_json_partition \"{s_VehicleTwins[s_Lanes[t].Key].Name}\" " +
                                    $"\"{Path.Combine(p_Context.WorkDir, "shader", $"stub_veh{t}.json")}\"");

            if (s_FpClone != null)
                s_Script.AppendLine($"add_json_partition \"{s_FpClone.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", "stub_fp.json")}\"");
            if (s_AugFpClone != null)
                s_Script.AppendLine($"add_json_partition \"{s_AugFpClone.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", "stub_augfp.json")}\"");
            if (s_NoCamoFpClone != null)
                s_Script.AppendLine($"add_json_partition \"{s_NoCamoFpClone.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", "stub_ncfp.json")}\"");
            if (s_AllTilesFpClone != null)
                s_Script.AppendLine($"add_json_partition \"{s_AllTilesFpClone.Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", "stub_atfp.json")}\"");
            for (var k = 0; k < s_ShippedClones.Count; k++)
                s_Script.AppendLine($"add_json_partition \"{s_ShippedClones[k].Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", $"stub_shipped{k}.json")}\"");
            s_Script.AppendLine($"replace_resource_as {CloneSourceDb} levels/{s_Stem}/shaderdb 1 \"{s_ShaderDb}\"");
            for (var d = 0; d < s_ExtraShaderDbs.Count; d++)
                s_Script.AppendLine($"replace_resource_as {CloneSourceDb} {ExtraShaderDbName(s_Stem, d)} 1 \"{s_ExtraShaderDbs[d]}\"");
            s_Script.AppendLine("resolve_resource_dependencies 1");
            s_Script.AppendLine("resolve_missing_chunks 1"); // references only: nothing the catalog cannot point at is copied in
            s_Script.AppendLine("build");
            s_Script.AppendLine("build");
        }

        // CAS: the variations, the database entries, and REFERENCES to the textures they bind.
        s_Script.AppendLine($"build_sb Win32/{s_Stem}/pack Frostbite2_0 \"{s_Sb}\" true");
        s_Script.AppendLine($"build_bundle win32/{s_Stem}/packv");

        foreach (var s_Variation in s_Variations)
            s_Script.AppendLine($"add_json_partition \"{s_Variation.Name}\" \"{s_Variation.Json}\"");

        if (s_PatternDds != null)
            s_Script.AppendLine($"add_json_partition \"{s_PatternName}\" \"{Path.Combine(p_Context.WorkDir, "tex_pattern.json")}\"");

        foreach (var s_Layer in s_StickerTextures.Concat(s_PictureTextures))
            s_Script.AppendLine($"add_json_partition \"{s_Layer.Name}\" \"{Path.ChangeExtension(s_Layer.Dds, ".json")}\"");

        foreach (var s_Texture in s_Referenced)
            s_Script.AppendLine($"add_existing_partition \"{s_Texture}\" 1");

        // the entries that bind any texture (the camo's, a sticker layer, a picture): what a values-only package's audit expects bound
        var s_BoundEntries = 0;
        for (var e = 0; e < s_Entries.Count; e++)
        {
            var (s_Weapon, s_Mesh, s_Db, s_Hash) = s_Entries[e];
            var s_Index = p_Plan.Weapons.FindIndex(p_W => p_W.Folder == s_Weapon) + 1;
            var s_MeshIndex = p_Plan.Weapons[s_Index - 1].Meshes.IndexOf(s_Mesh);
            // The two material variations, each handed to the materials wearing its shader family (see
            // FpShaderFamily): never by position. The material IDs the user kept the camo off this mesh ride
            // after '!' on both families (an ID is one material; it matches one family at most).
            var s_KeptOff = p_Plan.Weapons[s_Index - 1].MaterialsOff.TryGetValue(s_Mesh, out var s_OffIds) && s_OffIds.Count > 0
                ? string.Concat(s_OffIds.OrderBy(p_I => p_I).Select(p_I => $"!{p_I}"))
                : "";
            // ⛔ A WEAPON THE GAME GIVES NO CAMO ROW TAKES THE ACCESSORIES' MATERIAL, NOT A CAMO WEAPON'S (run 349, a capture from the
            // game: the M9 body dark, only its grips camouflaged; in game, the stock look). The first-person camo preset ZONES the camo
            // by UV tile (Scratches_D: R in (0,0), B where v > 1 — bf3-weapon-shaders) and only weapons that ship camos lay their UVs
            // out for it: a pistol's body (WeaponPresetFP) under ShadowFP stays camo-less. The accessories met the same wall and paint
            // with the THIRD-person preset on every weapon-preset material (phase E, boot 26): so both meshes' materials, both families,
            // take the variation's third-person instance — one material, the accessories' spec (confirmed in game on run 362; run 369,
            // after the first-person copy with a primary's numbers was taken out: colour correct). The entry itself is the one every
            // weapon gets (below: the variation, 'all' + hash, on the pistol's own meshes); the framework hangs it on a camo unlock of the
            // pistol's blueprint, picked in the pistol's own window (SidearmCamos.lua).
            var s_NoCamoRow = p_Plan.Weapons[s_Index - 1].NoCamoRow;
            // ⭐ …EXCEPT where the package's own shader draws something on the body — the emblem slot, stickers (the emblem must show in
            // third person too, as it already does on the primary weapons): a primary shows them in BOTH views
            // because its third-person body draws with the first-person family's material (bf3-customize-menu-revamp, batch 7), so both of
            // the pistol's meshes take the first-person instance, whose shader is the package's first-person preset with its tile gate
            // opened (AllTilesGraphFor — built only for such a package). Measured: the sidearms' third-person bodies wear the 1P decls
            // (CE4574FD; the Glock 164BFA7C), both with solutions in the preset the copy is cloned from, and their bind pose is the 1P's
            // (byte-identical dumps, the M9) — the squares, moved into it (EmblemSlot.InBindPose), hold in both views.
            // (…and so does one AS SHIPPED with the stickers / the slot on its own preset's copy — ShippedShaders: its body is that copy,
            // in both views)
            var s_Is3P = s_Mesh.Contains("_3p_", StringComparison.OrdinalIgnoreCase);
            var s_OnShippedCopy = p_Plan.ShippedShaders.Any(p_S => p_S.Weapons.Contains(s_Weapon, StringComparer.OrdinalIgnoreCase));
            var s_OpenedGate = s_NoCamoRow && (s_AllTilesFpClone != null || s_OnShippedCopy);
            var s_Refs = s_NoCamoRow && !s_OpenedGate
                ? $"{GuidFor(s_Index, 1)}:{GuidFor(s_Index, 4)}@{ThreePShaderFamily}|{FpShaderFamily}{s_KeptOff}"
                : $"{GuidFor(s_Index, 1)}:{GuidFor(s_Index, 3)}@{FpShaderFamily}{s_KeptOff},{GuidFor(s_Index, 4)}@{ThreePShaderFamily}{s_KeptOff}";
            if (s_KeptOff.Length > 0)
                s_Log($"  {s_Weapon} {s_Mesh.Split('/')[^1]}: material id(s) {string.Join(", ", s_OffIds!.OrderBy(p_I => p_I).Select(p_I => $"#{p_I}"))} kept as shipped.");

            // ⭐ The edited materials of THIS mesh: each instance answers for ITS material id only ('#id'),
            // which is why those specs come before the families in the command's own matching.
            var s_ExtraAt = 0;
            foreach (var s_Material in p_Plan.MaterialShaders.Where(p_M =>
                         p_M.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)))
            {
                var s_Key = RimeShaderEditor.Graph.ShaderGraph.MaterialKey(s_Material.Mesh, s_Material.MaterialId);
                if (!s_MaterialClones.ContainsKey(s_Key))
                    continue;

                // The instance guids were handed out per WEAPON in mesh-independent order, so the index is
                // recomputed the same way here: the weapon's edited materials, in the plan's order.
                var s_WeaponMaterials = p_Plan.MaterialShaders
                    .Where(p_M => p_Plan.Weapons[s_Index - 1].Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                s_ExtraAt = s_WeaponMaterials.FindIndex(p_M =>
                    p_M.Mesh.Equals(s_Material.Mesh, StringComparison.OrdinalIgnoreCase) && p_M.MaterialId == s_Material.MaterialId);
                if (s_ExtraAt < 0)
                    continue;

                s_Refs += $",{GuidFor(s_Index, 0x40 + s_ExtraAt)}@{s_Material.Shader.Split('/')[^1]}#{s_Material.MaterialId}";
                s_Log($"  {s_Weapon} {s_Mesh.Split('/')[^1]}: material #{s_Material.MaterialId} ships the camo's edited " +
                      $"'{s_Material.Shader.Split('/')[^1]}'.");
            }

            // …and the bare ones of this mesh's materials whose own graph only carries a picture ('#id' too)
            s_Refs += BareSpecsOf(s_Mesh);

            // The camo binding plus, on a sticker package, this weapon's layer under "Sticker". A package
            // with no pattern and no borrowed camo but a shader of its own (stickers) still SAMPLES Camo on
            // every weapon: the ones whose own entries bind none (the NoCamo presets' weapons, the M416
            // among them) get the preset's default pattern, which is what the preview showed them with.
            // ⭐ …and the pictures: the weapon's own graph's on its first-person materials, each material's own graph's on that material
            var s_Bindings = string.Join(",", new[]
                {
                    s_CamoOf.GetValueOrDefault(s_Weapon) ?? s_CamoTexture, s_StickerOf.GetValueOrDefault($"{s_Weapon}|{s_Mesh}") ?? "",
                    s_WeaponPictureBindings.GetValueOrDefault(s_Weapon) ?? "", MaterialPictureBindingsOf(s_Mesh),
                }
                .Where(p_B => p_B.Length > 0));
            if (s_Bindings.Length > 0)
                s_BoundEntries++;

            if (s_OpenedGate && s_OnShippedCopy)
                s_Log($"  {s_Weapon} {s_Mesh.Split('/')[^1]}: no camo row in the game — as shipped, its {(s_Is3P ? "third-person" : "first-person")} body takes " +
                      "the first-person instance: the copy of the preset it wears with the stickers / the emblem slot laid on it (ShippedShaders).");
            else if (s_OpenedGate)
                s_Log($"  {s_Weapon} {s_Mesh.Split('/')[^1]}: no camo row in the game — its {(s_Is3P ? "third-person body (the package draws stickers / the emblem slot, as on a primary)" : "first-person body")} takes the first-person instance, " +
                      $"'{s_AllTilesFpClone!.Name.Split('/')[^1]}' (the first-person preset with its UV-tile gate opened) with the package's values.");
            else if (s_NoCamoRow)
                s_Log($"  {s_Weapon} {s_Mesh.Split('/')[^1]}: no camo row in the game — its variation entry hands the third-person material " +
                      "instance to both shader families (the accessories' material); the framework offers it in the pistol's own window.");

            // Every existing entry of the mesh copied ('all') and OURS appended with the new hash and fresh
            // guids (the source is a played map's own database: inherited guids collide and the entry is
            // never ingested). SetTextureParams binds the camo (on those same materials only) whether the
            // base had a Camo slot or not, and the trailing 1 makes the command reference every texture the
            // copies bind — the scan's list is a head start, this is the guarantee.
            s_Script.AppendLine($"mvdb_add_entry {p_Context.MvdbSource} {s_Db} 1 {s_Mesh} all \"\" \"\" \"\" \"\" " +
                                $"{GuidFor(s_Index, 0x10 + s_MeshIndex * 2)} {GuidFor(s_Index, 0x11 + s_MeshIndex * 2)} \"\" " +
                                $"{s_Hash} \"{s_Refs}\" \"\" \"\" \"\" \"\" \"{s_Bindings}\" 1");
        }

        // --- the accessories' clones and their (clone, 0) entries ---------------------------------------
        // ⛔ THE SOCKETS READ HASH 0. The engine instances an accessory from the socket object's own
        // asset1p/asset1pZoom/asset3p fields with variation 0 (measured in the binary, boot 19), so what the
        // camo has to own is the BASE entry of a mesh of its own — never a variation of the vanilla mesh.
        // what a piece's clone entry binds: the camo's texture; the pictures of the piece's own graph (its as-shipped one, or under a camo
        // its painted body's) on the materials ITS instance goes to — when the entry hands that instance out —; each material's own
        // graph's pictures on that material
        string AccessoryBindingsOf((BakeAccessoryMesh Mesh, string Instance, BakeAccessory Accessory) p_Clone, bool p_OwnInstance)
        {
            var s_Piece = p_Clone.Accessory;
            var s_Own = !p_OwnInstance
                ? ""
                : PictureBindings(Bindable(s_Piece.AsShipped ? s_Piece.ShippedPictures : s_Piece.Pictures, s_AccessoryClonesOf.ContainsKey(s_Piece),
                    $"{s_Piece.Weapon}/{s_Piece.Tag}"), p_Clone.Instance);
            return string.Join(",", new[] { s_CamoTexture, s_Own, MaterialPictureBindingsOf(p_Clone.Mesh.Mesh) }.Where(p_B => p_B.Length > 0));
        }

        if (p_Plan.Accessories.Count > 0)
        {
            s_Script.AppendLine($"add_json_partition \"{AccessoryMaterialsName(p_Plan.Key)}\" \"{s_AccessoryMaterialsPath}\"");

            // ⛔ ONE CLONE PER MESH, not per accessory. Two accessories of the same weapon share meshes — the
            // ACOG and the foregrip of the L85A2 both switch on its sight rail — and cloning it twice ships
            // the same geometry under two names, of which only one can ever be pointed at.
            var s_CloneAt = 0;
            foreach (var s_Clone in s_AccessoryClones)
            {
                var s_CloneName = AccessoryCloneName(p_Plan.Key, s_Clone.Mesh.EbxName);
                var s_ClonePartition = GuidFor(0, 0x70 + s_CloneAt);
                var s_Work = Path.Combine(p_Context.WorkDir, "accessories");
                // the camo's variation on every material that wears a weapon preset — or, on a piece of an as-shipped package, the copy of
                // the preset it wears on the materials that wear THAT preset only (its as-shipped graph edited), and nothing when it was not
                var s_Specs = new List<string>();
                if (!s_Clone.Accessory.AsShipped)
                    s_Specs.Add($"{s_Clone.Instance}@{ThreePShaderFamily}|weaponpresetfp|weaponpresetshadowfp|weaponpresetshadownocamofp");
                else if (s_Clone.Accessory.ShippedPreset is { } s_Worn && s_AccessoryClonesOf.ContainsKey(s_Clone.Accessory))
                {
                    s_Specs.Add($"{s_Clone.Instance}@{s_Worn.Split('/')[^1].ToLowerInvariant()}");
                    s_Log($"  {s_Clone.Mesh.Mesh.Split('/')[^1]}: its '{s_Worn.Split('/')[^1]}' materials ship the edited as-shipped graph " +
                          $"('{s_AccessoryClonesOf[s_Clone.Accessory].Name.Split('/')[^1]}'), with their own numbers and textures.");
                }

                var s_OwnInstance = s_Specs.Count > 0;

                // …and the bare instances of this mesh's materials whose own graph only carries a picture
                s_Specs.AddRange(BareSpecs(s_Clone.Mesh.Mesh));

                // ⭐ this mesh's materials edited on their own graphs, each ONLY that material ('#id': tried before the families)
                foreach (var (s_Instance, _, s_Material) in s_AccessoryMaterialShaders.Where(p_M =>
                             p_M.Material.Mesh.Equals(s_Clone.Mesh.Mesh, StringComparison.OrdinalIgnoreCase)))
                {
                    s_Specs.Add($"{s_Instance}@{s_Material.Shader.Split('/')[^1]}#{s_Material.MaterialId}");
                    s_Log($"  {s_Clone.Mesh.Mesh.Split('/')[^1]}: material #{s_Material.MaterialId} ships the camo's edited " +
                          $"'{s_Material.Shader.Split('/')[^1]}'.");
                }

                // ⛔ a clone with nothing to hand out would ship the piece's own look under a name of the package's: never planned (see
                // AccessoriesForBake), refused if it ever is
                if (s_Specs.Count == 0)
                {
                    s_Result.Problems.Add($"accessories: {s_Clone.Mesh.Mesh} rides in the package with nothing of it edited — no variation to hand its materials.");
                    s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
                    return s_Result;
                }

                var s_MaterialRef = $"{s_AccessoryPartition}:{string.Join(",", s_Specs)}";
                var s_CloneBindings = AccessoryBindingsOf(s_Clone, s_OwnInstance);

                s_Script.AppendLine($"accessory_camo_clone {s_Clone.Mesh.Mesh} 1 \"{s_CloneName}\" \"{s_Work}\" {s_ClonePartition}");

                // 'auto' = the vanilla mesh's own partition guid, resolved by the command (it has the mesh
                // open anyway), so nothing here has to have read a dump to write this line.
                s_Script.AppendLine($"mvdb_add_entry {p_Context.MvdbSource} MV_{p_Plan.Key}_ACC{s_CloneAt} 1 " +
                                    $"{s_Clone.Mesh.Mesh} \"\" auto {s_ClonePartition} \"\" \"\" " +
                                    $"{GuidFor(0, 0x80 + s_CloneAt * 2)} {GuidFor(0, 0x81 + s_CloneAt * 2)} \"\" 0 " +
                                    $"\"{s_MaterialRef}\" \"\" \"\" \"\" \"\" \"{s_CloneBindings}\" 1");
                if (s_CloneBindings.Length > 0)
                    s_BoundEntries++;
                s_CloneAt++;
            }
        }

        // --- the vehicles' clones and their (clone, 0) entries ----------------------------------------------
        // The accessories' recipe on a vehicle body, measured in game: a clone of the body mesh under a name of its own, its
        // hash-0 entry copied from the vehicle's own base entry (a database of a level that fields the vehicle: no level has
        // them all), the camo's texture on the materials that bind Camo and a material variation with NO shader carrying
        // the camo's values there (the material keeps its own vehicle preset, as the game's own values-only variations do).
        // The client points the vehicle's data at the clone for the player who picked the camo.
        // The material variations the vehicles share stay in packv (behind the level bundle, before any vehicle's own bundle).
        if (p_Plan.Vehicles.Count > 0)
            s_Script.AppendLine($"add_json_partition \"{VehicleMaterialsName(p_Plan.Key)}\" \"{s_VehicleMaterialsPath}\"");

        s_Script.AppendLine("resolve_resource_dependencies 1");
        s_Script.AppendLine("resolve_missing_chunks 1"); // references only: nothing the catalog cannot point at is copied in
        s_Script.AppendLine("build");

        // ⭐ Each vehicle in a bundle of its OWN, which the framework loads right behind the level bundles that carry the vehicle
        // (VehicleEntry.LevelBundles). Measured 2026-09-24 (run 147, the client dead at the Tornado): packv goes in behind the
        // level's own bundle, and a vehicle — mesh, textures, LOD group — lives in the level's MODE bundles, which come in later
        // calls; its clone and pieces pointed at things not loaded yet. Behind its own level bundle everything it points at is
        // there, and on a level without the vehicle its bundle is not loaded at all.
        if (p_Plan.Vehicles.Count > 0)
        {
            for (var v = 0; v < p_Plan.Vehicles.Count; v++)
            {
                var s_Vehicle = p_Plan.Vehicles[v];
                s_Script.AppendLine($"build_bundle win32/{s_Stem}/{VehicleBundleName(v)}");
                var s_CloneName = AccessoryCloneName(p_Plan.Key, s_Vehicle.EbxName);
                var s_ClonePartition = GuidFor(0, 0xB0 + v);
                var s_Work = Path.Combine(p_Context.WorkDir, "vehicles");
                // (a copy's instance on this vehicle: GuidFor(0xF000 + v, 0x40 + its place among the lanes) — the body clone's and the pieces')
                int LaneAt(string p_Key) => s_Lanes.FindIndex(p_L => p_L.Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase));

                // ⭐ the pictures (any custom texture must work): its own graph's on its camo materials — by parameter
                // name, as the preview dresses every section —, each material's own graph's on that material; a picture at a register the
                // game's preset has no parameter at only where a copy compiled from that graph declares it (the pieces)
                var s_MyMaterialPictures = s_VehiclePictureOn.Where(p_P => p_P.Vehicle == v).ToList();
                string PresetOf(MaterialPictureRequest p_Request) =>
                    s_Vehicle.MaterialShaders.GetValueOrDefault(p_Request.MaterialId, p_Request.Shader).Split('/')[^1];
                static IEnumerable<GraphPicture> Plain(IEnumerable<GraphPicture> p_Pictures) => p_Pictures.Where(p_P => !p_P.Declare);

                // the body clone (worn as a vehicle of its own, and the camo window's preview): a renamed MeshSet header where the body's
                // resident LOD data has a catalog copy; where it lives only inside its levels, the EBX ALONE with the game's Name kept —
                // measured in the client (IDA, 2026-09-27): the MeshModel binds its MeshSet by the asset's Name string
                // (ResourceProxyBase::bind_0 → ResourceManager_bind(compartment, type, const char*)) and the variation database keys an
                // entry by its mesh's NameHash (MeshAsset+0x18, sub_17402D0 / sub_17398C0), so a new NameHash under the game's Name gets
                // an entry of its own on the game's MeshSet, whose resident LODs the level binds — a renamed header waited for data no
                // bundle binds to it and the client froze on the loading screen (run 149)
                if (s_ShipsBodyClone.Contains(s_Vehicle))
                {
                    // ⭐ its OWN copies draw the body too (everything moved to option A — decided above, after PrepareVehicleTwins: their
                    // sources compile every declaration of its body): the pieces' rule below on the whole body — each camo preset its own graph
                    // draws on that copy's instance, each material edited on its own graph on its own copy ('#id'); every other camo preset on
                    // the game's (the body's own instance, no shader), as always. A vehicle with no own copy writes exactly what it wrote before.
                    // (a copy's BODY COPY where it has one: TwinLane.BodySource)
                    string? BodyLaneOf(string? p_Key) => p_Key != null && s_LaneOf.ContainsKey(p_Key + "@body") ? p_Key + "@body" : p_Key;
                    var s_BodyTwins = s_Vehicle.CamoShaders
                        .Select(p_S => (Shader: p_S, Lane: BodyLaneOf(PresetLaneOf(s_Vehicle, p_S, s_EditedOf[s_Vehicle]))))
                        .Where(p_T => p_T.Lane != null && s_VehicleTwins.ContainsKey(p_T.Lane) && ReferenceEquals(s_LaneOf[p_T.Lane].Vehicle, s_Vehicle))
                        .ToList();
                    var s_BodyOwnLanes = s_Lanes
                        .Where(p_L => p_L.MaterialId is { } s_Id && ReferenceEquals(p_L.Vehicle, s_Vehicle) && s_Vehicle.MaterialShaders.ContainsKey(s_Id) &&
                                      p_L.BodySource == null)
                        .ToDictionary(p_L => p_L.MaterialId!.Value, p_L => BodyLaneOf(p_L.Key)!);
                    // (a material with its own copy takes its pictures there; the others with pictures keep their own instance, no shader)
                    var s_BodyPictured = s_MyMaterialPictures.Where(p_P => !s_BodyOwnLanes.ContainsKey(p_P.Request.MaterialId)).ToList();
                    var s_BodyShaders = s_Vehicle.CamoShaders.Where(p_S => !s_BodyTwins.Any(p_T => p_T.Shader.Equals(p_S, StringComparison.OrdinalIgnoreCase))).ToList();

                    var s_BodySpecs = new List<string>();
                    if (s_BodyShaders.Count > 0)
                        s_BodySpecs.Add($"{GuidFor(0, 0xC1 + v)}@{string.Join("|", s_BodyShaders)}");
                    foreach (var (s_Shader, s_PresetLane) in s_BodyTwins)
                        s_BodySpecs.Add($"{GuidFor(0xF000 + v, 0x40 + LaneAt(s_PresetLane!))}@{s_Shader}");
                    foreach (var (s_Id, s_Lane) in s_BodyOwnLanes.OrderBy(p_O => p_O.Key))
                        s_BodySpecs.Add($"{GuidFor(0xF000 + v, 0x40 + LaneAt(s_Lane))}@{s_LaneOf[s_Lane].Shader}#{s_Id}");
                    s_BodySpecs.AddRange(s_BodyPictured.Select(p_P => $"{p_P.Body}@{PresetOf(p_P.Request)}#{p_P.Request.MaterialId}"));
                    var s_BodyRef = $"{s_VehiclePartition}:{string.Join(",", s_BodySpecs)}";

                    // (each instance bound only where a material is left to it — the '#id' ones took theirs, the command's rule —; its own graph's
                    // copy with all its pictures (the declared ones too: Picture7 at t7), the game's presets with the plain ones, a material's own
                    // graph's pictures first, then the vehicle's by parameter name: what the preview lays on it)
                    bool BodyKeeps(IEnumerable<string> p_Shaders) => s_Vehicle.MaterialShaders.Any(p_M =>
                        !s_BodyOwnLanes.ContainsKey(p_M.Key) && s_BodyPictured.All(p_P => p_P.Request.MaterialId != p_M.Key) &&
                        p_Shaders.Contains(p_M.Value, StringComparer.OrdinalIgnoreCase));
                    var s_BodyBound = new List<string> { VehicleCamoTexture(s_Vehicle) };
                    if (s_BodyShaders.Count > 0 && BodyKeeps(s_BodyShaders))
                        s_BodyBound.Add(PictureBindings(Plain(s_Vehicle.Pictures), GuidFor(0, 0xC1 + v)));
                    foreach (var (s_TwinShader, s_PresetLane) in s_BodyTwins.Where(p_T => BodyKeeps(new[] { p_T.Shader })))
                        s_BodyBound.Add(PictureBindings(s_LaneOf[s_PresetLane!] is { Compiled: true, MaterialId: null } ? s_Vehicle.Pictures : Plain(s_Vehicle.Pictures),
                            GuidFor(0xF000 + v, 0x40 + LaneAt(s_PresetLane!))));
                    foreach (var (s_Id, s_Lane) in s_BodyOwnLanes)
                        s_BodyBound.Add(PictureBindings(p_Plan.MaterialPictures
                            .Where(p_P => p_P.Mesh.Equals(s_Vehicle.Mesh, StringComparison.OrdinalIgnoreCase) && p_P.MaterialId == s_Id)
                            .SelectMany(p_P => p_P.Pictures)
                            .Concat(Plain(s_Vehicle.Pictures)), GuidFor(0xF000 + v, 0x40 + LaneAt(s_Lane))));
                    s_BodyBound.AddRange(s_BodyPictured.Select(p_P => PictureBindings(Plain(p_P.Request.Pictures).Concat(Plain(s_Vehicle.Pictures)), p_P.Body)));
                    var s_BodyBindings = string.Join(",", s_BodyBound.Where(p_B => p_B.Length > 0));
                    if (s_BodyTwins.Count + s_BodyOwnLanes.Count > 0)
                        s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]} body clone: " + string.Join(", ", s_BodyTwins.Select(p_T => $"{p_T.Shader.Split('/')[^1]} on '{s_LaneOf[p_T.Lane!].Name.Split('/')[^1]}'")
                                  .Concat(s_BodyOwnLanes.OrderBy(p_O => p_O.Key).Select(p_O => $"#{p_O.Key} on '{s_LaneOf[p_O.Value].Name.Split('/')[^1]}'"))) +
                              (s_BodyShaders.Count > 0 ? $"; {string.Join(", ", s_BodyShaders.Select(p_S => p_S.Split('/')[^1]))} on the game's" : "") + ".");
                    if (s_BodyBindings.Length > 0)
                        s_BoundEntries++;
                    s_Script.AppendLine($"accessory_camo_clone {s_Vehicle.Mesh} 1 \"{s_CloneName}\" \"{s_Work}\" {s_ClonePartition}" +
                                        (s_Vehicle.BodyInCatalog ? "" : " vanilla"));
                    if (!s_Vehicle.BodyInCatalog)
                        s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]} body clone: the EBX alone on the game's MeshSet (its resident LOD data lives " +
                              "only inside its levels, which bind it to the game's name).");
                    s_Script.AppendLine($"mvdb_add_entry {s_Vehicle.Database} {VehicleEntryName(p_Plan.Key, v)} 1 " +
                                        $"{s_Vehicle.Mesh} \"\" auto {s_ClonePartition} \"\" \"\" " +
                                        $"{GuidFor(0, 0xD0 + v * 2)} {GuidFor(0, 0xD1 + v * 2)} \"\" 0 " +
                                        $"\"{s_BodyRef}\" \"\" \"\" \"\" \"\" \"{s_BodyBindings}\" 1");
                }

                // The body's camo pieces (option B): each cut out of the body as a rigid mesh of the package, with its own
                // (piece, 0) entry copied from the same base entry and wearing the camo like the body clone's — the piece keeps
                // the body's material instances, so the same material variation and the same camo shaders apply.
                for (var p = 0; p < s_Vehicle.Pieces.Count; p++)
                {
                    var s_Piece = s_Vehicle.Pieces[p];
                    // each kind of guid in a range of its own: a vehicle has two dozen pieces (the M1A2: hull, turret, guns, 18 wheels),
                    // and ranges packed side by side (0x10 + p·4 against 0x20 + p·2) met from the 5th piece on
                    var s_PiecePartition = GuidFor(0xF000 + v, 0x1000 + p * 4);
                    var s_PieceChunk = GuidFor(0xF000 + v, 0x1002 + p * 4); // last byte even: a raw chunk, no compression flag

                    // the mesh it is cut from, and what its materials wear there (a helicopter's first-person cockpit numbers its own)
                    var s_PieceMesh = s_Vehicle.MeshOf(s_Piece);
                    var s_PieceShaders = s_Vehicle.ShadersOf(s_Piece);
                    var s_FromBody = BakeVehicle.FromBody(s_Piece);

                    // this piece's materials edited on their own graphs: each takes its own copy (material id → its lane) — the body's
                    // materials, so only on a piece cut from the body
                    var s_OwnLanes = s_Lanes
                        .Where(p_L => s_FromBody && p_L.MaterialId is { } s_Id && ReferenceEquals(p_L.Vehicle, s_Vehicle) && s_Piece.Materials.Contains(s_Id) &&
                                      p_L.BodySource == null)
                        .ToDictionary(p_L => p_L.MaterialId!.Value, p_L => p_L.Key);

                    // the sections drawn with a copy whose vertex stage reads the uv pair the other way round carry theirs exchanged
                    var s_SwapUv = PieceSwapUvOf(s_Vehicle, s_Piece, s_Retargets, s_EditedOf[s_Vehicle], s_OwnLanes);
                    s_Script.AppendLine($"vehicle_part_clone {s_PieceMesh} 1 \"{PieceCloneName(p_Plan.Key, s_Vehicle.EbxName, s_Piece)}\" " +
                                        $"{string.Join(",", s_Piece.Parts)} \"{s_Work}\" {s_PiecePartition} {s_PieceChunk} " +
                                        $"{(s_Piece.Materials.Count > 0 ? string.Join(",", s_Piece.Materials) : "-")} " +
                                        $"{(s_SwapUv.Count > 0 ? string.Join(",", s_SwapUv) : "-")}" +
                                        // (the body's simple cabin culled where the detailed one rides in another piece)
                                        (s_Piece.Cull is { Length: > 0 } s_CullSpec ? $" \"{s_CullSpec}\"" : ""));
                    // the piece's materials whose preset has a copy in the package take the copy's variation (drawn with the rigid
                    // solutions); every other camo material the body's, as the body clone
                    // (this vehicle's own lanes: another vehicle of the package needing a copy — or drawing its own edited graph — does not
                    // move this one off its level's preset)
                    var s_PieceTwins = s_Piece.Materials
                        .Select(p_M => s_PieceShaders.GetValueOrDefault(p_M, ""))
                        .Where(p_S => p_S.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(p_S => (Shader: p_S, Lane: PresetLaneOf(s_Vehicle, p_S, s_EditedOf[s_Vehicle])))
                        .Where(p_T => p_T.Lane != null && s_VehicleTwins.ContainsKey(p_T.Lane))
                        .ToList();
                    var s_PieceSpecs = new List<string>();
                    // (a piece cut from another mesh: only the camo presets that mesh wears — the AH-6's cockpit has no decals and no LOD
                    // preset, and an instance matching no material fails the entry)
                    var s_BodyShaders = s_Vehicle.CamoShaders.Where(p_S => !s_PieceTwins.Any(p_T => p_T.Shader.Equals(p_S, StringComparison.OrdinalIgnoreCase)) &&
                                                                        (s_FromBody || s_PieceShaders.Values.Contains(p_S, StringComparer.OrdinalIgnoreCase))).ToList();
                    if (s_BodyShaders.Count > 0)
                        s_PieceSpecs.Add($"{GuidFor(0, 0xC1 + v)}@{string.Join("|", s_BodyShaders)}");
                    foreach (var (s_Shader, s_PresetLane) in s_PieceTwins)
                        s_PieceSpecs.Add($"{GuidFor(0xF000 + v, 0x40 + LaneAt(s_PresetLane!))}@{s_Shader}");

                    // ⭐ and each material edited on its own graph ONLY that material ('#id': the command tries those before the families)
                    foreach (var (s_Id, s_Lane) in s_OwnLanes.OrderBy(p_O => p_O.Key))
                    {
                        s_PieceSpecs.Add($"{GuidFor(0xF000 + v, 0x40 + LaneAt(s_Lane))}@{s_LaneOf[s_Lane].Shader}#{s_Id}");
                        s_Log($"  {s_Vehicle.Mesh.Split('/')[^1]} {s_Piece.Name}: material #{s_Id} ships its own edited " +
                              $"'{s_LaneOf[s_Lane].Shader.Split('/')[^1]}' ({s_LaneOf[s_Lane].Name.Split('/')[^1]}).");
                    }

                    // ⭐ the pictures on this piece: the vehicle's own graph's on its camo materials (every one at a register a copy of
                    // THAT graph declares, the rest on the preset's own parameters), each material's own graph's on that material alone —
                    // its own copy's instance, or one of its own that draws it exactly as it would be drawn here
                    var s_PieceBindings = new List<string> { VehicleCamoTexture(s_Vehicle) };
                    var s_OwnPictured = s_MyMaterialPictures.Where(p_P => s_FromBody && p_P.OwnLane == null && s_Piece.Materials.Contains(p_P.Request.MaterialId)).ToList();
                    // the materials each instance is left with: the '#id' ones (own copies, own pictures) take theirs first — the command's rule —,
                    // and an instance with none left gets no binding (a scoped one would find nothing: VARIATION FAILED)
                    bool Keeps(IEnumerable<string> p_Shaders) => s_Piece.Materials.Any(p_M =>
                        !s_OwnLanes.ContainsKey(p_M) && s_OwnPictured.All(p_P => p_P.Request.MaterialId != p_M) &&
                        p_Shaders.Contains(s_PieceShaders.GetValueOrDefault(p_M, ""), StringComparer.OrdinalIgnoreCase));
                    if (s_BodyShaders.Count > 0 && Keeps(s_BodyShaders))
                        s_PieceBindings.Add(PictureBindings(Plain(s_Vehicle.Pictures), GuidFor(0, 0xC1 + v)));
                    foreach (var (s_TwinShader, s_PresetLane) in s_PieceTwins.Where(p_T => Keeps(new[] { p_T.Shader })))
                    {
                        var s_GraphLane = s_LaneOf[s_PresetLane!] is { Vehicle: not null, Compiled: true, MaterialId: null };
                        s_PieceBindings.Add(PictureBindings(s_GraphLane ? s_Vehicle.Pictures : Plain(s_Vehicle.Pictures),
                            GuidFor(0xF000 + v, 0x40 + LaneAt(s_PresetLane!))));
                    }

                    // a material with its own graph: its pictures first, then the vehicle's by parameter name (what the preview lays on it)
                    foreach (var (s_Id, s_Lane) in s_OwnLanes)
                        s_PieceBindings.Add(PictureBindings(p_Plan.MaterialPictures
                            .Where(p_P => p_P.Mesh.Equals(s_Vehicle.Mesh, StringComparison.OrdinalIgnoreCase) && p_P.MaterialId == s_Id)
                            .SelectMany(p_P => p_P.Pictures)
                            .Concat(Plain(s_Vehicle.Pictures)), GuidFor(0xF000 + v, 0x40 + LaneAt(s_Lane))));

                    foreach (var s_Own in s_OwnPictured)
                    {
                        s_PieceSpecs.Add($"{s_Own.Piece}@{PresetOf(s_Own.Request)}#{s_Own.Request.MaterialId}");
                        s_PieceBindings.Add(PictureBindings(Plain(s_Own.Request.Pictures).Concat(Plain(s_Vehicle.Pictures)), s_Own.Piece));
                    }

                    var s_PieceBound = string.Join(",", s_PieceBindings.Where(p_B => p_B.Length > 0));
                    if (s_PieceBound.Length > 0)
                        s_BoundEntries++;

                    s_Script.AppendLine($"mvdb_add_entry {s_Vehicle.Database} {VehiclePieceEntryName(p_Plan.Key, v, p)} 1 " +
                                        $"{s_PieceMesh} \"\" auto {s_PiecePartition} \"\" \"\" " +
                                        $"{GuidFor(0xF000 + v, 0x2000 + p * 2)} {GuidFor(0xF000 + v, 0x2001 + p * 2)} \"\" 0 " +
                                        $"\"{s_VehiclePartition}:{string.Join(",", s_PieceSpecs)}\" \"\" \"\" \"\" \"\" \"{s_PieceBound}\" 1");
                }

                // the vehicle's own textures: a catalog reference when the game has one, otherwise the level bundle loaded right
                // before this one brings them (MVDB-TEX-LEVEL / "Left to the level")
                s_Script.AppendLine("resolve_resource_dependencies 1");
                s_Script.AppendLine("resolve_missing_chunks 1");
                s_Script.AppendLine("build");
            }

            s_Log($"  vehicles: {p_Plan.Vehicles.Count} body clone(s) ({string.Join(", ", p_Plan.Vehicles.Select(p_V => p_V.Mesh))}), " +
                  $"{p_Plan.Vehicles.Sum(p_V => p_V.Pieces.Count)} piece(s) " +
                  $"({string.Join(", ", p_Plan.Vehicles.SelectMany(p_V => p_V.Pieces.Select(p_P => $"{p_V.Mesh.Split('/')[^1]} {p_P.Name}")))}), " +
                  $"each in its own bundle behind its level bundles.");
        }

        s_Script.AppendLine("build");
        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(p_Context.WorkDir, "build.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));

        s_Log($"Building '{p_Plan.Name}' ({p_Plan.Key}) for {p_Plan.Weapons.Count} weapon(s), " +
              $"{s_Entries.Count} database entries, {s_Referenced.Count} referenced texture(s) — this mounts the game.");

        var s_Output = Cache.GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
        s_Result.LogPath = Path.Combine(p_Context.WorkDir, "build.log");
        File.WriteAllText(s_Result.LogPath, s_Output, new UTF8Encoding(false));

        // --- audit: read the bundle the way the game will, not the log the way it flatters ----------------
        var s_PieceEntries = p_Plan.Vehicles.SelectMany((p_V, v) => p_V.Pieces.Select((_, p) => VehiclePieceEntryName(p_Plan.Key, v, p))).ToList();
        Audit(s_Result, s_Output, s_PackageDir, s_Variations.Select(p_V => (p_V.Name, p_V.Hash)).ToList(),
            s_Entries.Select(p_E => p_E.Db)
                .Concat(p_Plan.Vehicles.Select((p_V, v) => (p_V, v)).Where(p_X => s_ShipsBodyClone.Contains(p_X.p_V)).Select(p_X => VehicleEntryName(p_Plan.Key, p_X.v)))
                .Concat(s_PieceEntries).ToList(),
            s_Referenced, s_PatternDds != null ? s_PatternName : null,
            s_HasThumb ? s_ThumbName : null,
            s_Entries.Count + s_AccessoryClones.Count + p_Plan.Vehicles.Count(s_ShipsBodyClone.Contains) + s_PieceEntries.Count, p_Plan.ValuesOnly,
            s_ShaderDb != null ? new[] { $"levels/{s_Stem}/shaderdb" }.Concat(s_ExtraShaderDbs.Select((_, d) => ExtraShaderDbName(s_Stem, d))).ToList() : null,
            s_StickerTextures.Select(p_T => p_T.Name).ToList(),
            // a values-only package binds a camo texture only where a weapon wears a copy of the package's that samples one and binds
            // none of its own (the preset's default pattern, as previewed: the M416 edited on the basic camo) — and the graphs' pictures
            // where they go: counted as each entry's line was written
            s_BoundEntries,
            s_PictureTextures.Select(p_T => p_T.Name).ToList());

        // a piece the command refused (its reasons are plain lines of the log, which the audit's patterns do not catch) would ship
        // a package whose index names a mesh that is not in it
        var s_PiecesBuilt = Regex.Matches(s_Output, @"^PIECECLONE:", RegexOptions.Multiline).Count;
        if (s_PiecesBuilt != s_PieceEntries.Count)
            s_Result.Problems.Add($"{s_PiecesBuilt} vehicle piece(s) cut, expected {s_PieceEntries.Count} — read build.log (vehicle_part_clone).");

        if (!s_Result.Ok)
        {
            s_Log($"BAKE FAILED — {s_Result.Problems.Count} problem(s); the package was NOT registered:");
            foreach (var s_Problem in s_Result.Problems)
                s_Log("  " + s_Problem);

            // A broken package must not be offered, so nothing of it stays where Register looks — but it is
            // kept beside the build log, because the bytes are what a diagnosis reads.
            var s_Failed = Path.Combine(p_Context.WorkDir, "failed_package");
            Directory.CreateDirectory(s_Failed);
            foreach (var s_File in Directory.GetFiles(s_PackageDir))
                File.Move(s_File, Path.Combine(s_Failed, Path.GetFileName(s_File)), true);

            Directory.Delete(s_PackageDir, false);
            s_Log($"  (what was built is kept under {s_Failed})");
            return s_Result;
        }

        // (said, never silent: a blueprint that is neither the planned one nor a variant gets no row — a vehicle without pieces too,
        // since its body clone is worn as a vehicle of its own: the UH-1Y's coop blueprint has a body with other parts, 2026-09-27)
        foreach (var s_Vehicle in p_Plan.Vehicles)
            foreach (var s_Blueprint in s_Vehicle.Blueprints.Where(p_B =>
                         !p_B.Equals(s_Vehicle.PieceBlueprint.Length > 0 ? s_Vehicle.PieceBlueprint : s_Vehicle.Blueprints[0], StringComparison.OrdinalIgnoreCase) &&
                         !s_Vehicle.Variants.Any(p_X => p_X.Blueprint.Equals(p_B, StringComparison.OrdinalIgnoreCase))))
                s_Log($"  NOTE: {s_Blueprint} gets no camo — its body is not the one the body clone and the pieces were made from (an AI or campaign variant).");

        // --- the package's own record, then the mod is told --------------------------------------------
        var s_Info = new PackageInfo
        {
            Key = p_Plan.Key,
            Folder = p_Plan.Folder,
            Name = p_Plan.Name,
            Description = p_Plan.Description,
            Family = p_Plan.Family,
            Identifier = p_Plan.Identifier,
            Superbundle = s_Stem,
            PatternTexture = s_PatternDds != null ? s_PatternName : null,
            NativeTexture = s_Native?.Name,
            Thumbnail = s_HasThumb ? s_ThumbName : null,
            Native = p_Plan.Native,
            DefaultLook = p_Plan.DefaultLook,
            Shader = s_ShaderDb != null,
            // ⛔ "stickers" is what makes the loader PREPEND <stem>/packncv — the generated textures' annex, where the graphs' pictures
            // ride too ("Snow": the ACOG's picture with no pattern and no stickers was never registered and the client
            // died silently at the end of the load — the Trollface failure of 2026-09-11, on a lane that forgot it)
            Stickers = s_StickerTextures.Count > 0 || s_PictureTextures.Count > 0,
            // ⛔ A PIECE-ONLY PACKAGE STILL NAMES ITS WEAPONS, with no variation: the framework needs them to
            // know whose attachments these are, and refuses a package with none at all. What it must NOT do
            // is add a camo row for them, and that is what `Body` says.
            Body = p_Plan.PaintsBody,
            Weapons = p_Plan.PaintsBody
                ? s_Variations.Select(p_V => new PackageWeapon { Folder = p_V.Weapon, Variation = p_V.Name }).ToList()
                : p_Plan.Weapons.Select(p_W => new PackageWeapon { Folder = p_W.Folder, Variation = "" }).ToList(),
            // What the loader points the sockets at. The clone name is derived the same way as the line that
            // built it, so a shared mesh (the L85A2's sight rail, switched on by both the ACOG and the
            // foregrip) names the SAME clone under both accessories — one clone, two accessories that use it.
            // What the vehicle camo windows list and what the client points each vehicle at: one row per blueprint (a jet
            // spawned in the air wears the same body, so the same clone), under the class it is picked in.
            // ⭐ each row with ITS blueprint's components: the planned one's, or a variant's (the same pieces, its own holders, hides and
            // driver). A blueprint with pieces elsewhere and neither is left out — the planned guids are not its own (2026-09-25: the
            // Su-25TM's players' jet, the jets spawned in the air…)
            Vehicles = p_Plan.Vehicles.SelectMany((p_V, p_Index) => p_V.Blueprints
                .Select(p_B => (Blueprint: p_B, Variant: p_V.Variants.FirstOrDefault(p_X => p_X.Blueprint.Equals(p_B, StringComparison.OrdinalIgnoreCase))))
                .Where(p_R => p_R.Variant != null ||
                              p_R.Blueprint.Equals(p_V.PieceBlueprint.Length > 0 ? p_V.PieceBlueprint : p_V.Blueprints[0], StringComparison.OrdinalIgnoreCase))
                .Select(p_R => new PackageVehicle
            {
                Blueprint = p_R.Blueprint,
                Class = p_V.Class,
                Mesh = p_V.EbxName,
                Clone = s_ShipsBodyClone.Contains(p_V) ? AccessoryCloneName(p_Plan.Key, p_V.EbxName) : "",
                Pieces = p_V.Pieces.Select(p_P => new PackageVehiclePiece
                {
                    Name = p_P.Name,
                    Mesh = PieceCloneName(p_Plan.Key, p_V.EbxName, p_P),
                    Holder = p_R.Variant?.Pieces.First(p_X => p_X.Name == p_P.Name).Holder ?? p_P.Holder,
                    Hide = p_R.Variant?.Pieces.First(p_X => p_X.Name == p_P.Name).Hide ?? p_P.Hide,
                    Mirror = p_P.Mirror,
                }).ToList(),
                Driver = p_R.Variant?.DriverPart ?? p_V.DriverPart,
                Bundle = VehicleBundleName(p_Index),
                LevelBundles = new List<string>(p_V.LevelBundles),
            })).ToList(),
            Accessories = p_Plan.Accessories.Select(p_A => new PackageAccessory
            {
                Weapon = p_A.Weapon,
                Tag = p_A.Tag,
                Meshes = p_A.Meshes.Select(p_M => new PackageAccessoryMesh
                {
                    Mesh = p_M.EbxName,
                    Clone = AccessoryCloneName(p_Plan.Key, p_M.EbxName),
                }).ToList(),
            }).ToList(),
            // The kit lists the row's identifier means this camo in — the seat's slots the studio took.
            Kits = Catalog.Kits.Load().KitsOfWeapons(s_Variations.Select(p_V => p_V.Weapon).Distinct().ToList()).ToList(),
            Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            MvdbSource = p_Context.MvdbSource,
        };

        // camo.json is a file people open (and may edit before Register re-reads it): apostrophes and accents
        // stay as typed instead of the default \u escapes. The partition stubs above keep the plain encoder.
        var s_InfoJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(s_PackageDir, "camo.json"), JsonSerializer.Serialize(s_Info, s_InfoJson), new UTF8Encoding(false));
        s_Result.Package = s_Info;

        var s_Sizes = Directory.GetFiles(s_PackageDir, "*.sb")
            .Select(p_F => $"{Path.GetFileName(p_F)} {new FileInfo(p_F).Length / 1024} KB");
        s_Result.Summary = $"Package '{p_Plan.Folder}': {string.Join(", ", s_Sizes)}.";
        s_Log(s_Result.Summary);

        s_Result.Summary += " " + FrameworkMod.Register(p_Context.ModFolder, s_Log);
        return s_Result;
    }

    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The package's own first-person preset: the graph compiled against every flavour of the preset it was
    /// authored on (the editor's own bake machinery, one mount), the preset's entry cloned under the
    /// package's name with that bytecode patched in, the AUG-layout twin when an AUG variation is in the
    /// package (the framework's AUG recipe: the same clone with its declaration re-labelled), and the two
    /// cut into a small database of the package's own. Returns the failure, or null.
    /// </summary>
    /// <summary>
    /// The rigid-capable copies of the vehicle presets the plan's pieces wear (see s_RigidTwinSources): each preset cloned under a
    /// fresh name out of the level database that compiles it for the rigid declaration — its own bytecode, or a graph's compiled
    /// against that preset's flavours — cut into a shader database of the package's own, with the stub the piece's material
    /// variation points at. A chain of clones is ONE source database with the clones in it, so the copies make one database per
    /// source level (p_ShaderDbs, in the order of the levels' first copies).
    /// </summary>
    private static string? PrepareVehicleTwins(BakePlan p_Plan, BakeContext p_Context, List<TwinLane> p_Lanes,
        IReadOnlyDictionary<string, TwinRetarget> p_Retargets,
        Func<int, int, string> p_GuidFor, JsonSerializerOptions p_Json, out List<string> p_ShaderDbs, out Dictionary<string, ShaderStub> p_Twins,
        out Dictionary<string, HashSet<string>> p_SourceDeclarations)
    {
        p_ShaderDbs = new List<string>();
        p_Twins = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);
        // "<source database>|<source shader>" (lower case) -> the declarations that entry is compiled for (see the dumps below)
        p_SourceDeclarations = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var s_Log = p_Context.Log;

        var s_Work = Path.Combine(p_Context.WorkDir, "shader");
        Directory.CreateDirectory(s_Work);
        var s_Levels = p_Lanes.Select(p_L => p_L.Source.Level).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // --- the copies that draw a GRAPH (see TwinLane.Compiled): its preset's own translation (OwnLogic, the 1uvset family), a
        // vehicle's edited camo graph or a material's own edited graph — aimed at the source preset, compiled against that preset's
        // flavours in its own level (the editor's bake machinery; one mount per level)
        var s_Patches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var s_Compiles = p_Lanes.Where(p_L => p_L.Compiled).ToList();
        // the callers only look the compiler up for a camo that edits a shader; a vehicle's copies need it on their own
        var s_Fxc = RimeShaderEditor.Emit.ShaderCompiler.IsAvailable(p_Context.Fxc) ? p_Context.Fxc : RimeShaderEditor.MainWindow.FindFxc() ?? "";
        if (s_Compiles.Count > 0 && !RimeShaderEditor.Emit.ShaderCompiler.IsAvailable(s_Fxc))
            return "shader: fxc.exe (the Windows SDK shader compiler) was not found — the vehicle pieces' copies of " +
                   $"{string.Join(", ", s_Compiles.Select(p_L => p_L.Shader.Split('/')[^1]))} are compiled from their graphs.";

        // the materials a copy draws, each with the numbers the preview draws it with (BakeVehicle.MaterialValues / DocumentValues) and the
        // ones typed on its vehicle's document: what a constant the donor has no slot for is baked as (RelocateConstants)
        List<(string Who, IReadOnlyDictionary<string, string>? Values, IReadOnlyDictionary<string, string> Typed)> UsersOf(TwinLane p_Lane)
        {
            var s_Users = new List<(string, IReadOnlyDictionary<string, string>?, IReadOnlyDictionary<string, string>)>();
            if (p_Lane.Vehicle is { } s_Own)
            {
                // a material's own graph: that material's numbers; a vehicle's own graph: the ones its document is dressed with
                s_Users.Add(p_Lane.MaterialId is { } s_Id
                    ? ($"{s_Own.Mesh.Split('/')[^1]} #{s_Id}", s_Own.MaterialValues.GetValueOrDefault(s_Id), s_Own.Values)
                    : ($"{s_Own.Mesh.Split('/')[^1]}'s document", s_Own.DocumentValues, s_Own.Values));
                return s_Users;
            }

            // a shared copy: every piece material it is handed to (the rule the entries below follow — neither its vehicle's own graph's
            // copy nor its own material's)
            foreach (var s_Vehicle in p_Plan.Vehicles)
            foreach (var s_Piece in s_Vehicle.Pieces)
            foreach (var s_Material in s_Piece.Materials)
            {
                if (!s_Vehicle.ShadersOf(s_Piece).TryGetValue(s_Material, out var s_Wears) ||
                    TwinOf(s_Vehicle, s_Wears) is not { } s_Key || !s_Key.Equals(p_Lane.Key, StringComparison.OrdinalIgnoreCase) ||
                    p_Lanes.Any(p_L => ReferenceEquals(p_L.Vehicle, s_Vehicle) &&
                                       (p_L.Key.Equals($"{s_Vehicle.Mesh}#{s_Wears}", StringComparison.OrdinalIgnoreCase) ||
                                        BakeVehicle.FromBody(s_Piece) && p_L.MaterialId == s_Material)))
                    continue;

                s_Users.Add(($"{s_Vehicle.MeshOf(s_Piece).Split('/')[^1]} #{s_Material}",
                    BakeVehicle.FromBody(s_Piece) ? s_Vehicle.MaterialValues.GetValueOrDefault(s_Material) : null, s_Vehicle.Values));
            }

            return s_Users;
        }

        // the value a constant with no slot on the donor is baked as: typed on the node, typed on the document, the material's own (or its
        // preset's default as the preview fed it), the graph's preset's default — one value, or refused when a shared copy's users differ
        (string? Value, string Source) BakedValueOf(TwinLane p_Lane, IReadOnlyList<(string Who, IReadOnlyDictionary<string, string>? Values,
            IReadOnlyDictionary<string, string> Typed)> p_Users, string p_Name, string? p_OnNode)
        {
            if (!string.IsNullOrWhiteSpace(p_OnNode))
                return (ConstantVector(p_Name, p_OnNode), "typed on its node");

            var s_Defaults = Cache.SlotMap.Load(p_Lane.LogicShader.ToLowerInvariant())?.ExternalDefaults;
            var s_Default = s_Defaults?.GetValueOrDefault(p_Name) is { Length: > 0 } s_D ? ConstantVector(p_Name, s_D) : null;
            var s_Found = new List<(string Value, string Source)>();
            foreach (var (s_Who, s_Values, s_Typed) in p_Users)
                if (s_Typed.GetValueOrDefault(p_Name) is { Length: > 0 } s_OnDocument)
                    s_Found.Add((ConstantVector(p_Name, s_OnDocument), $"typed on {s_Who}"));
                else if (s_Values?.GetValueOrDefault(p_Name) is { Length: > 0 } s_Own)
                    s_Found.Add((ConstantVector(p_Name, s_Own), ConstantVector(p_Name, s_Own) == s_Default ? $"{s_Who}: its preset's default" : $"{s_Who}'s own"));

            if (s_Found.Count == 0)
                return s_Default != null
                    ? (s_Default, $"{p_Lane.LogicShader}'s default — no material of the plan says")
                    : (null, $"no value is known for it ({p_Lane.LogicShader} has no default and no material of the plan names it)");

            if (s_Found.Select(p_F => p_F.Value).Distinct().Count() > 1)
                return (null, "the copy is shared by materials drawn with different values of it (" +
                              string.Join("; ", s_Found.Distinct().Select(p_F => $"{p_F.Value} {p_F.Source}")) + ") — one copy bakes one value");

            return (s_Found[0].Value, string.Join(", ", s_Found.Select(p_F => p_F.Source).Distinct()));
        }

        foreach (var s_Level in s_Levels)
        {
            var s_Lanes = s_Compiles.Where(p_L => p_L.Source.Level.Equals(s_Level, StringComparison.OrdinalIgnoreCase)).ToList();
            if (s_Lanes.Count == 0)
                continue;

            var s_GraphPaths = new List<string>();
            // per lane, the slot each external constant of its graph was given (checked below against the donor's own flavours)
            var s_Relocated = new List<List<(string Name, int Element)>>();
            foreach (var s_Lane in s_Lanes)
            {
                var s_Shader = s_Lane.Key;
                var s_Source = s_Lane.Source;
                var s_Retarget = p_Retargets[s_Lane.Key];
                // a material's OWN edited graph, a vehicle's EDITED camo graph, or the preset's own translation (TwinLane.GraphPath)
                var s_Cached = s_Lane.GraphPath!;
                if (!File.Exists(s_Cached))
                    return $"shader: the vehicle pieces' copy of '{s_Shader}' is compiled from its graph, and '{s_Cached}' is not there.";

                // the graph as the donor's: its target, its texture registers moved to the donor's table, and each texture PINNED to
                // the sampler the preset itself reads it through — emitted against the donor's flavours it would otherwise take the
                // donor's (the mud reads everything through s0; the 1uvset decals read their decals through s1 = Clamp/Clamp/Wrap)
                var s_Graph = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_Cached))!;
                s_Graph["TargetShader"] = s_Source.Shader.ToLowerInvariant();

                // ⭐ the INTERPOLATOR the uv pair arrives in is the donor's vertex stage's, not the graph's own: the F/A-18F's cockpit
                // reads it from TEXCOORD4 and the jet's rigid solutions hand it over in TEXCOORD3 (fxc: no 'Interp4' on the donor's
                // input, 2026-09-25). Read off the donor's own translation (its TexCoord nodes), never written here
                var s_DonorGraph = Path.Combine(Settings.CacheFolder, "camographs", Cache.GameCache.SanitizeName(s_Source.Shader.ToLowerInvariant()) + ".json");
                string[] UvInterpsOf(System.Text.Json.Nodes.JsonNode? p_Graph) => (p_Graph?["Nodes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                    .Where(p_N => p_N?["Kind"]?.GetValue<string>() == "TexCoord")
                    .Select(p_N => p_N!["Params"]?["Interp"]?.GetValue<string>() ?? "")
                    .Where(p_I => p_I.Length > 0).Distinct().ToArray();
                var s_Mine = UvInterpsOf(s_Graph);
                var s_Theirs = File.Exists(s_DonorGraph) ? UvInterpsOf(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_DonorGraph))) : Array.Empty<string>();
                if (s_Mine.Length == 1 && s_Theirs.Length == 1 && s_Mine[0] != s_Theirs[0])
                {
                    foreach (var s_Node in s_Graph["Nodes"]!.AsArray().Where(p_N => p_N?["Kind"]?.GetValue<string>() == "TexCoord"))
                        s_Node!["Params"]!["Interp"] = s_Theirs[0];
                    s_Log($"  shader: '{s_Shader.Split('/')[^1]}': its uv pair read from TEXCOORD{s_Theirs[0]}, where '{s_Source.Shader.Split('/')[^1]}' hands it over (its own: TEXCOORD{s_Mine[0]}).");
                }
                else if (s_Mine.Length > 1 || (s_Mine.Length == 1 && s_Theirs.Length != 1))
                    return $"shader: the graph of '{s_Shader}' reads its uv from TEXCOORD{string.Join("/", s_Mine)} and where '{s_Source.Shader}' hands the pair " +
                           $"over is {(s_Theirs.Length == 0 ? "not known (its translation is not cached)" : $"TEXCOORD{string.Join("/", s_Theirs)}")} — refused rather than guessed.";

                // ⭐ …and its CONSTANTS where the donor keeps them, by name (RelocateConstants): the engine fills the copy's block as the
                // donor's entry lays it out — the Quad's MudScratches read its smoothness at the mud's WearPower/FLIRData ("brilla demasiado")
                if (CachedContract(s_Source.Shader, out var s_DonorDxbc) is not { } s_DonorContract)
                    return $"shader: the copy of '{s_Shader}' reads its constants where '{s_Source.Shader}' keeps them, and '{s_DonorDxbc}' is not cached (the cache step).";
                var s_Users = UsersOf(s_Lane);
                var s_ConstantsMoved = new List<string>();
                var s_ConstantsBaked = new List<string>();
                if (ConstantsByName &&
                    RelocateConstants(s_Graph, s_DonorContract, (p_Name, p_OnNode) => BakedValueOf(s_Lane, s_Users, p_Name, p_OnNode),
                        out s_ConstantsMoved, out s_ConstantsBaked) is { } s_ConstantError)
                    return $"shader: the graph of '{s_Shader}' on '{s_Source.Shader}': {s_ConstantError}";
                // (full names: the base mud and XPack01's share their last segment)
                if (s_ConstantsMoved.Count + s_ConstantsBaked.Count > 0)
                    s_Log($"  shader: '{s_Shader}' on '{s_Source.Shader}': its constants where the donor keeps them — " +
                          (s_ConstantsMoved.Count > 0 ? $"moved {string.Join(", ", s_ConstantsMoved)}" : "none moved") +
                          (s_ConstantsBaked.Count > 0
                              ? $"; baked in (the donor has no slot for them, the engine writes nothing there): {string.Join(", ", s_ConstantsBaked)}"
                              : "") + ".");
                s_Relocated.Add((s_Graph["Nodes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                    .Where(p_N => p_N?["Kind"]?.GetValue<string>() == "ExternalConstant" &&
                                  (p_N["Params"]?["Buffer"]?.GetValue<string>() is not { Length: > 0 } s_Buffer ||
                                   s_Buffer.Equals("externalConstants", StringComparison.OrdinalIgnoreCase)))
                    .Select(p_N => (Name: p_N!["Params"]?["Name"]?.GetValue<string>() ?? "",
                                    Element: int.TryParse(p_N["Params"]?["Element"]?.GetValue<string>(), out var s_At) ? s_At : 0))
                    .ToList());

                var s_MovedFrom = new HashSet<int>();
                var s_Pinned = 0;
                foreach (var s_Node in s_Graph["Nodes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                {
                    if (s_Node?["Kind"]?.GetValue<string>() is not ("Texture" or "NormalMap") ||
                        s_Node["Params"]?["Register"]?.GetValue<string>() is not { } s_Register || !int.TryParse(s_Register, out var s_From))
                        continue;

                    if (!s_Retarget.Samplers.TryGetValue(s_From, out var s_Sampler))
                        return $"shader: the graph of '{s_Shader}' samples t{s_From}, which its own bytecode does not declare — a stale graph?";

                    s_Node["Params"]!["Sampler"] = s_Sampler.ToString();
                    s_Pinned++;
                    if (s_Retarget.Moves.TryGetValue(s_From, out var s_To))
                    {
                        s_Node["Params"]!["Register"] = s_To.ToString();
                        s_MovedFrom.Add(s_From);
                    }
                }

                // (a document on a vehicle's own camo shader that dropped its camo node samples nothing at the camo's register: no stale graph)
                var s_OwnCamoRegister = Catalog.VehicleCatalog.OwnCamoRegisterOf(s_Lane.LogicShader);
                var s_Unmoved = s_Retarget.Moves.Keys.Where(p_R => !s_MovedFrom.Contains(p_R) && p_R != s_OwnCamoRegister).OrderBy(p_R => p_R).ToList();
                if (s_Unmoved.Count > 0)
                    return $"shader: the graph of '{s_Shader}' never samples {string.Join(", ", s_Unmoved.Select(p_R => $"t{p_R}"))}, which its copy moves — a stale graph?";
                if (s_Pinned == 0)
                    return $"shader: the graph of '{s_Shader}' samples no texture — not the preset's translation.";

                var s_Path = Path.Combine(s_Work, $"twin_{Cache.GameCache.SanitizeName(s_Shader)}.json");
                File.WriteAllText(s_Path, s_Graph.ToJsonString(), new UTF8Encoding(false));
                s_GraphPaths.Add(s_Path);
            }

            s_Log($"  shader: compiling {s_Lanes.Count} vehicle preset graph(s) against {s_Level}'s rigid-capable presets — this mounts the game.");
            var (s_Compiled, _, s_Error) = RimeShaderEditor.MainWindow.PrepareAdditionalGraphs(s_GraphPaths, p_Context.RimeRepl, s_Fxc,
                p_Context.GamePath, s_Level, Settings.TextureCache, Path.Combine(s_Work, $"twins_{s_Level}"), s_Log);
            if (s_Error != null)
                return "shader (vehicle pieces): " + s_Error;
            if (s_Compiled.Count != s_Lanes.Count)
                return $"shader: {s_Lanes.Count} vehicle preset graph(s) compiled and {s_Compiled.Count} produced bytecode.";

            // ⛔ the donor's block as ITS FLAVOURS lay it out, not only as the studio's cached pixel stage does: every flavour of the donor
            // this level extracted (the ones the copy patches among them) must keep each constant the graph reads where it was put above
            for (var i = 0; i < s_Lanes.Count && ConstantsByName; i++)
            {
                var s_Extract = Path.Combine(s_Work, $"twins_{s_Level}", $"extract_{i}");
                var s_Folder = s_Extract;
                var s_Index = Path.Combine(s_Extract, "index.txt");
                if (File.Exists(s_Index) && File.ReadAllLines(s_Index).Select(p_L => p_L.Split('\t'))
                        .FirstOrDefault(p_P => p_P.Length >= 2 && p_P[1].Equals(s_Lanes[i].Source.Shader, StringComparison.OrdinalIgnoreCase)) is { } s_Row)
                    s_Folder = Path.Combine(s_Extract, s_Row[0]);

                var s_Flavours = Directory.Exists(s_Folder) ? Directory.GetFiles(s_Folder, "*_ps.dxbc") : Array.Empty<string>();
                if (s_Flavours.Length == 0)
                    return $"shader: the flavours of '{s_Lanes[i].Source.Shader}' extracted for the copy of '{s_Lanes[i].Key}' are not under '{s_Folder}' — its constants cannot be checked.";

                foreach (var s_Flavour in s_Flavours)
                {
                    var s_Fields = RimeShaderEditor.Emit.ShaderContract.Detect(File.ReadAllBytes(s_Flavour));
                    foreach (var (s_Name, s_Element) in s_Relocated[i])
                        if (s_Fields.ExternalFieldOf(s_Name) is var (s_Register, s_At) && s_Register >= 0 && s_At != s_Element)
                            return $"shader: '{s_Lanes[i].Source.Shader}' flavour {Path.GetFileName(s_Flavour)} keeps '{s_Name}' at c{s_At}, and the copy of " +
                                   $"'{s_Lanes[i].Key}' reads it at c{s_Element} (the studio's cached pixel stage) — refused rather than read from a neighbour.";
                }
            }

            for (var i = 0; i < s_Lanes.Count; i++)
            {
                var s_Patch = s_Compiled[i].ManifestPath ?? s_Compiled[i].DxbcPath;
                if (s_Patch == null || !File.Exists(s_Patch))
                    return $"shader: the compiled '{s_Lanes[i].Key}' ('{s_Patch}') was not written.";
                s_Patches[s_Lanes[i].Key] = s_Patch;
            }
        }

        // --- one chain per source level: the clones (every render mode, the depth passes too), the textures a copy declares on
        // top, then the slice
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");

        var s_Slices = new List<(string Level, string File, List<string> Names)>();
        for (var l = 0; l < s_Levels.Count; l++)
        {
            var s_Chain = Path.Combine(s_Work, $"vehicle_twins{(l > 0 ? l.ToString() : "")}.bin");
            var s_Sliced = Path.Combine(s_Work, $"shaderdb_{p_Plan.Folder}_vehicles{(l > 0 ? l.ToString() : "")}.bin");
            foreach (var s_Stale in new[] { s_Chain, s_Sliced })
                if (File.Exists(s_Stale))
                    File.Delete(s_Stale);

            var s_Here = new List<string>();
            foreach (var s_Lane in p_Lanes.Where(p_L => p_L.Source.Level.Equals(s_Levels[l], StringComparison.OrdinalIgnoreCase)))
            {
                // chained on the first clone's file; the patch is a per-flavour manifest (see PrepareAdditionalGraphs) or none
                var s_Patch = s_Patches.TryGetValue(s_Lane.Key, out var s_File) ? $"\"{s_File}\"" : "\"-\"";
                var s_Tail = s_Here.Count > 0 ? $" \"-\" \"{s_Chain}\"" : "";
                s_Script.AppendLine($"shader_db_clone_entry {s_Lane.Source.Database} {s_Lane.Source.Shader} {s_Lane.Name} \"{s_Chain}\" {s_Patch} ShaderRenderMode_{s_Tail}");
                s_Here.Add(s_Lane.Name);
            }

            foreach (var s_Lane in p_Lanes.Where(p_L => p_L.Compiled && p_L.Source.Level.Equals(s_Levels[l], StringComparison.OrdinalIgnoreCase)))
            {
                // the textures the donor does not declare, where the retarget put them
                foreach (var (s_Parameter, s_Register) in p_Retargets[s_Lane.Key].Adds)
                    s_Script.AppendLine($"shader_db_add_external_texture {s_Lane.Source.Database} {s_Lane.Name} {s_Parameter} {s_Register} \"{s_Chain}\" \"{s_Chain}\"");
                // …and the fixed ones the preset's own entry lists (the engine loads and binds them itself, by name)
                foreach (var (s_Asset, s_Register) in p_Retargets[s_Lane.Key].Fixed)
                    s_Script.AppendLine($"shader_db_add_texture {s_Lane.Source.Database} {s_Lane.Name} {s_Asset} {s_Register} \"{s_Chain}\" \"{s_Chain}\"");

                // and the sampler STATES of the preset whose pixel logic the copy draws (a vehicle's edited camo graph: the preset it
                // was authored on): the copy is the donor's entry, and the donor's table is what the engine binds
                var s_Logic = s_Lane.LogicShader;
                var s_Home = Cache.GameCache.ShaderDbLevelOf(s_Logic, Cache.GameCache.VehicleShaderDbLevel);
                s_Script.AppendLine($"shader_db_copy_samplers levels/{s_Home}/{s_Home}/shaderdb {s_Logic} {s_Lane.Source.Database} {s_Lane.Name} " +
                                    $"\"{s_Chain}\" \"{s_Chain}\"");
            }

            s_Script.AppendLine($"shader_db_slice \"{s_Chain}\" \"{s_Sliced}\" \"{string.Join(",", s_Here)}\"");
            s_Slices.Add((s_Levels[l], s_Sliced, s_Here));
        }

        // ⭐ the declarations each source entry of a vehicle's OWN copy holds solutions for (everything moved to option A): a copy is
        // the whole entry cloned (every declaration that level compiled — sp_valley's mud has the RHIB body's composite 0xFC3E2403 13
        // times, measured), so where they include the body's the body clone can be drawn with the vehicle's own graph (SourceDeclarationsOf)
        var s_OwnSources = p_Lanes.Where(p_L => p_L.Vehicle != null)
            .Select(p_L => (p_L.Source.Database, p_L.Source.Shader))
            .Distinct()
            .ToList();
        foreach (var (s_Database, s_Shader) in s_OwnSources)
            s_Script.AppendLine($"dump_shader_solutions {s_Database} {s_Shader}");

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(s_Work, "vehicle_twins.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));
        var s_Output = Cache.GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
        File.WriteAllText(Path.Combine(s_Work, "vehicle_twins.log"), s_Output, new UTF8Encoding(false));

        foreach (var s_Line in s_Output.Split('\n'))
        {
            var s_Trim = s_Line.Trim();
            if (s_Trim.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Command not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Could not find", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("No shader matched", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("SAMPLER-CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("CLONE FAILED", StringComparison.OrdinalIgnoreCase))
                return "shader (vehicle pieces): " + s_Trim;
        }

        // (the source entries' declarations: a block of SHSOL lines per render path, closed by "SHSOL-DONE: <db> '<shader>'"; the search is
        // by substring, so only the header naming the shader EXACTLY counts — its _LOD / _Decals siblings come in the same block)
        var s_Declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s_Exact = false;
        foreach (var s_Line in s_Output.Split('\n'))
        {
            var s_Header = Regex.Match(s_Line, @"SHSOL-SHADER: \[\w+\] (\S+)");
            if (s_Header.Success)
            {
                s_Exact = s_OwnSources.Any(p_S => p_S.Shader.Equals(s_Header.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var s_Decl = Regex.Match(s_Line, @"SHSOL: .*decl=(0x[0-9A-Fa-f]{8})");
            if (s_Decl.Success && s_Exact)
                s_Declared.Add(s_Decl.Groups[1].Value);

            var s_Done = Regex.Match(s_Line, @"SHSOL-DONE: (\S+) '([^']+)'");
            if (s_Done.Success)
            {
                p_SourceDeclarations[$"{s_Done.Groups[1].Value}|{s_Done.Groups[2].Value}".ToLowerInvariant()] =
                    new HashSet<string>(s_Declared.Select(p_D => "0x" + p_D[2..].ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
                s_Log($"  shader: {s_Done.Groups[2].Value.Split('/')[^1]} in {s_Done.Groups[1].Value} is compiled for " +
                      $"{string.Join(", ", s_Declared.OrderBy(p_D => p_D))} — what a body clone drawn with a copy of it can wear.");
                s_Declared.Clear();
                s_Exact = false;
            }
        }

        // every copy that draws a graph on a donor carries that graph's samplers — said by the copy, not assumed
        foreach (var s_Lane in p_Lanes.Where(p_L => p_L.Compiled))
            if (!s_Output.Contains($"SAMPLERS: '{s_Lane.Name}'", StringComparison.OrdinalIgnoreCase))
                return $"shader (vehicle pieces): the copy of '{s_Lane.Key}' did not report its samplers — read shader\\vehicle_twins.log.";

        // each slice's own lines: the SLICE counts it printed before its "Wrote sliced shaderdb -> <file>"
        var s_Kept = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var s_Pending = new List<int>();
        foreach (var s_Line in s_Output.Split('\n'))
        {
            var s_Count = Regex.Match(s_Line, @"SLICE: (\d+) shader\(s\)");
            if (s_Count.Success)
                s_Pending.Add(int.Parse(s_Count.Groups[1].Value));

            var s_Wrote = Regex.Match(s_Line, @"Wrote sliced shaderdb -> (.+?) \(");
            if (s_Wrote.Success)
            {
                s_Kept[s_Wrote.Groups[1].Value.Trim()] = s_Pending;
                s_Pending = new List<int>();
            }
        }

        foreach (var (s_Level, s_File, s_Here) in s_Slices)
        {
            if (!File.Exists(s_File) || new FileInfo(s_File).Length == 0)
                return $"shader: the vehicle pieces' shader database from {s_Level} was not written — read shader\\vehicle_twins.log.";

            var s_Counts = s_Kept.GetValueOrDefault(s_File) ?? new List<int>();
            if (s_Counts.Count == 0 || s_Counts.Max() != s_Here.Count)
                return $"shader: the vehicle pieces' slice from {s_Level} kept {string.Join("/", s_Counts)} shader(s) per render path, " +
                       $"expected {s_Here.Count} — read shader\\vehicle_twins.log.";

            p_ShaderDbs.Add(s_File);
            s_Log($"  shader: the vehicle pieces' copies from {s_Level} cut into {new FileInfo(s_File).Length / 1024} KB.");
        }

        for (var t = 0; t < p_Lanes.Count; t++)
        {
            // their own guid space (0xF100): the weapon lanes' stubs use (0, 0x20..0x5F), the vehicles' pieces (0xF000 + v, …)
            var s_Lane = p_Lanes[t];
            var s_Source = s_Lane.Source;
            var s_Stub = new ShaderStub(s_Lane.Name, p_GuidFor(0xF100, 0x10 + t * 2), p_GuidFor(0xF100, 0x11 + t * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_veh{t}.json"), s_Stub, p_Json);
            p_Twins[s_Lane.Key] = s_Stub;
            s_Log($"  shader: '{s_Stub.Name}' — the game's '{s_Source.Shader}' as {s_Source.Level} compiles it (with the rigid declaration " +
                  $"the multiplayer levels lack)" +
                  (s_Lane.MaterialId is { } s_Id
                      ? $", drawing material #{s_Id}'s OWN edited graph with its samplers"
                      : s_Lane.Vehicle != null
                          ? $", drawing THE CAMO'S OWN GRAPH of {s_Lane.Vehicle.Mesh.Split('/')[^1]} (its own document) with its samplers"
                          : s_Source.OwnLogic ? $", drawing '{s_Lane.Shader.Split('/')[^1]}' from its graph with its own samplers" : "") +
                  $"{(p_Retargets[s_Lane.Key].SwapUv ? ", the pieces' uv sets exchanged" : "")}" +
                  (s_Lane.MaterialId is { } s_Alone
                      ? $", for material #{s_Alone} of {s_Lane.Vehicle!.Mesh.Split('/')[^1]} alone."
                      : s_Lane.Vehicle != null
                          ? $", for {s_Lane.Vehicle.Mesh.Split('/')[^1]}'s pieces' materials that wear it."
                          : ", for the vehicle pieces' materials that wear it."));
        }

        return null;
    }

    private static string? PrepareShader(BakePlan p_Plan, BakeContext p_Context, bool p_HasAug,
        Func<int, int, string> p_GuidFor, JsonSerializerOptions p_Json,
        out string? p_ShaderDb, out ShaderStub? p_FpClone, out ShaderStub? p_AugFpClone, out ShaderStub? p_NoCamoFpClone,
        out ShaderStub? p_AllTilesFpClone, out Dictionary<string, ShaderStub> p_MaterialClones,
        out Dictionary<string, ShaderStub> p_AccessoryTwins,
        out Dictionary<BakeAccessory, ShaderStub> p_AccessoryClones,
        out List<ShaderStub> p_ShippedClones,
        int? p_StickerRegister = null)
    {
        p_ShaderDb = null;
        p_FpClone = null;
        p_AugFpClone = null;
        p_NoCamoFpClone = null;
        p_AllTilesFpClone = null;
        p_MaterialClones = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);
        p_AccessoryTwins = new Dictionary<string, ShaderStub>(StringComparer.OrdinalIgnoreCase);
        p_AccessoryClones = new Dictionary<BakeAccessory, ShaderStub>();
        p_ShippedClones = new List<ShaderStub>();

        // the accessories whose painted body's LOGIC was edited (BakeAccessory.GraphPath): a copy of the third-person preset each
        var s_EditedAccessories = p_Plan.Accessories.Where(p_A => p_A.GraphPath != null).ToList();
        foreach (var s_Accessory in s_EditedAccessories)
            if (!File.Exists(s_Accessory.GraphPath))
                return $"shader: the edited graph of {s_Accessory.Weapon}/{s_Accessory.Tag} ('{s_Accessory.GraphPath}') does not exist.";

        // and the as-shipped graphs edited on weapons that wear another preset (BakePlan.ShippedShaders): a copy of THAT preset each
        foreach (var s_Shipped in p_Plan.ShippedShaders)
            if (!File.Exists(s_Shipped.GraphPath))
                return $"shader: the edited as-shipped graph of {string.Join(", ", s_Shipped.Weapons)} ('{s_Shipped.GraphPath}') does not exist.";

        // and the pieces whose own as-shipped graph was edited (BakeAccessory.ShippedPreset): a copy of the preset each wears
        var s_ShippedPieces = p_Plan.Accessories.Where(p_A => p_A.ShippedPreset != null && p_A.ShippedGraphPath != null).ToList();
        foreach (var s_Piece in s_ShippedPieces)
            if (!File.Exists(s_Piece.ShippedGraphPath))
                return $"shader: the edited as-shipped graph of {s_Piece.Weapon}/{s_Piece.Tag} ('{s_Piece.ShippedGraphPath}') does not exist.";
        var s_Log = p_Context.Log;
        var s_HasCamoGraph = p_Plan.ShaderGraphPath != null;
        var s_HasNoCamo = p_Plan.NoCamoShaderGraphPath != null;
        if (s_HasNoCamo && !File.Exists(p_Plan.NoCamoShaderGraphPath))
            return $"shader: the no-camo twin graph '{p_Plan.NoCamoShaderGraphPath}' does not exist.";

        if (s_HasCamoGraph && !File.Exists(p_Plan.ShaderGraphPath))
            return $"shader: the camo's graph '{p_Plan.ShaderGraphPath}' does not exist.";

        foreach (var s_Material in p_Plan.MaterialShaders)
            if (!File.Exists(s_Material.GraphPath))
                return $"shader: the edited graph of material #{s_Material.MaterialId} ('{s_Material.GraphPath}') does not exist.";

        var s_Work = Path.Combine(p_Context.WorkDir, "shader");
        Directory.CreateDirectory(s_Work);

        // ⭐ the first-person preset with its UV-tile gate opened, for the body of the weapons the game gives no camo row where the package
        // draws the emblem / stickers (AllTilesGraphFor): from the package's own first-person graph
        var s_AllTilesGraph = AllTilesGraphFor(p_Plan, s_Work, s_Log);
        var s_AllTilesFromOwn = s_AllTilesGraph != null && s_HasCamoGraph;

        // ⛔ Only a GRAPH needs the compiler. A declaration twin keeps the preset's own bytecode — nothing is
        // compiled for it — so a package that only gives accessories a camo must not ask for fxc.
        var s_Compiles = s_HasCamoGraph || s_HasNoCamo || s_AllTilesGraph != null || p_Plan.MaterialShaders.Count > 0 || s_EditedAccessories.Count > 0 ||
                         p_Plan.ShippedShaders.Count > 0 || s_ShippedPieces.Count > 0;
        // (nothing to compile and nothing to re-label: the opened gate was the only reason to be here and there was nothing to open — no
        // database of its own, the weapons take what they took; AllTilesGraphFor said why)
        if (!s_Compiles && !p_Plan.Accessories.Where(p_A => !p_A.AsShipped).SelectMany(p_A => p_A.Meshes).Any(p_M => NeedsDeclarationTwin(p_M.Declaration)))
            return null;
        if (s_Compiles && !RimeShaderEditor.Emit.ShaderCompiler.IsAvailable(p_Context.Fxc))
            return "shader: fxc.exe (the Windows SDK shader compiler) was not found — a camo that ships its own shader needs it.";

        if (s_Compiles)
            s_Log("  this camo ships its OWN first-person shader: compiling the graph against the preset's flavours — this mounts the game.");
        else
            s_Log("  this camo ships a shader database for its accessories only: the preset's own bytecode, re-labelled " +
                  "to their vertex declarations — nothing is compiled.");
        // The next line the compiler prints names a level: that is only where the preset's compiled shader is
        // READ from (every level carries the same weapon presets; this is the one the framework was measured
        // on). The package's own database is registered on every level (are they universal for any map? —
        // they are).
        if (s_Compiles)
            s_Log($"  (the preset is read out of the {CloneSourceLevel} database as a TEMPLATE only: the package's shader database is its own and loads on every level.)");

        // The lanes, in the order the compiler gets them: the camo's graph, its no-camo twin, then one per
        // edited material. Each graph names its OWN target shader, so a material's lane is compiled against
        // the flavours of the shader that material wears (glass, glow, the reticle HUD), not the preset's.
        var s_GraphList = new List<string>();
        if (s_HasCamoGraph)
            s_GraphList.Add(p_Plan.ShaderGraphPath!);
        if (s_HasNoCamo)
            s_GraphList.Add(p_Plan.NoCamoShaderGraphPath!);

        // …then the first-person preset with its tile gate opened (its own target: the preset it was derived from)
        var s_AllTilesAt = s_GraphList.Count;
        if (s_AllTilesGraph != null)
            s_GraphList.Add(s_AllTilesGraph);

        var s_MaterialAt = s_GraphList.Count;
        s_GraphList.AddRange(p_Plan.MaterialShaders.Select(p_M => p_M.GraphPath));

        // …and one per accessory GRAPH edited on its own (authored on the third-person preset, compiled against its flavours): the same
        // mesh on two weapons (the ACOG of the M416 and of the M240) is one subject with one graph, compiled once
        var s_AccessoryAt = s_GraphList.Count;
        var s_AccessoryGraphs = s_EditedAccessories.Select(p_A => p_A.GraphPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s_GraphList.AddRange(s_AccessoryGraphs);

        // …and one per as-shipped graph of another preset (compiled against THAT preset's flavours: each graph names its target)
        var s_ShippedAt = s_GraphList.Count;
        s_GraphList.AddRange(p_Plan.ShippedShaders.Select(p_S => p_S.GraphPath));

        // …and one per piece's as-shipped graph (compiled against the flavours of the preset it wears: the graph names it), one per
        // (preset, graph) — the ACOG of the M416 and of the M240 is one mesh with one graph, compiled once
        var s_ShippedPieceAt = s_GraphList.Count;
        var s_ShippedPieceCopies = s_ShippedPieces
            .Select(p_A => (Preset: p_A.ShippedPreset!, Graph: p_A.ShippedGraphPath!))
            .Distinct()
            .ToList();
        s_GraphList.AddRange(s_ShippedPieceCopies.Select(p_C => p_C.Graph));

        var s_GraphPaths = s_GraphList.ToArray();
        var s_Shaders = new List<RimeShaderEditor.Emit.ShaderBaker.BakeShader>();
        if (s_GraphPaths.Length > 0)
        {
            var (s_Compiled, _, s_PrepError) = RimeShaderEditor.MainWindow.PrepareAdditionalGraphs(
                s_GraphPaths, p_Context.RimeRepl, p_Context.Fxc, p_Context.GamePath,
                CloneSourceLevel, Settings.TextureCache, s_Work, s_Log);

            if (s_PrepError != null)
                return "shader: " + s_PrepError;

            if (s_Compiled.Count < s_GraphPaths.Length)
                return $"shader: {s_GraphPaths.Length} graph(s) were compiled and {s_Compiled.Count} produced bytecode.";

            s_Shaders = s_Compiled;
        }

        // Per-flavour manifest when the preset has several pixel flavours; one blob when it has one.
        var s_Patch = s_HasCamoGraph ? s_Shaders[0].ManifestPath ?? s_Shaders[0].DxbcPath : null;
        if (s_Patch != null && !File.Exists(s_Patch))
            return $"shader: '{s_Patch}' was not written.";

        var s_FpName = FpCloneName(p_Plan.Key);
        var s_AugName = AugFpCloneName(p_Plan.Key);
        var s_NoCamoName = NoCamoFpCloneName(p_Plan.Key);
        var s_NoCamoPatch = s_HasNoCamo ? s_Shaders[s_HasCamoGraph ? 1 : 0].ManifestPath ?? s_Shaders[s_HasCamoGraph ? 1 : 0].DxbcPath : null;
        if (s_NoCamoPatch != null && !File.Exists(s_NoCamoPatch))
            return $"shader: '{s_NoCamoPatch}' (the no-camo twin) was not written.";
        var s_AllTilesName = AllTilesFpCloneName(p_Plan.Key);
        var s_AllTilesPatch = s_AllTilesGraph != null ? s_Shaders[s_AllTilesAt].ManifestPath ?? s_Shaders[s_AllTilesAt].DxbcPath : null;
        if (s_AllTilesPatch != null && !File.Exists(s_AllTilesPatch))
            return $"shader: '{s_AllTilesPatch}' (the first-person preset with its UV-tile gate opened) was not written.";
        var s_Chain = Path.Combine(s_Work, "clones.bin");
        var s_Sliced = Path.Combine(s_Work, $"shaderdb_{p_Plan.Folder}.bin");
        foreach (var s_Stale in new[] { s_Chain, s_Sliced })
            if (File.Exists(s_Stale))
                File.Delete(s_Stale);

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");

        // ⭐ THE PICTURES' PARAMETERS each copy declares (a custom texture on a texture node must work on any weapon, accessory or
        // vehicle): a register its graph samples a picture at and its preset has no
        // parameter at gets one there — an ExternalTextureConstant, declared as the sticker layer is — so the entries can bind it
        var s_PictureParameters = new List<(string Parameter, int Register)>();
        void DeclarePictures(string p_Clone, IEnumerable<(string Parameter, int Register)> p_Declared)
        {
            foreach (var (s_Parameter, s_Register) in p_Declared.Distinct())
            {
                s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {p_Clone} {s_Parameter} {s_Register} \"{s_Chain}\" \"{s_Chain}\"");
                s_PictureParameters.Add((s_Parameter, s_Register));
            }
        }

        static IEnumerable<(string Parameter, int Register)> Declared(IEnumerable<GraphPicture> p_Pictures) =>
            p_Pictures.Where(p_P => p_P.Declare).Select(p_P => (p_P.Parameter, p_P.Register));

        // ⭐ THE PROJECTED EMBLEM SLOT's vertex half: a copy whose graph reads the mesh position (a Mesh Position
        // node) gets vertex shaders of its own that hand it over (DxbcMeshPosition) — a clone SHARES the preset's otherwise, and the game's
        // write 0 there. Chained on the same file right after the copy is made; each one's reply is checked after the run.
        var s_MeshPositionClones = new List<string>();
        void HandMeshPosition(string p_Clone, string? p_Graph)
        {
            if (p_Graph == null || !File.Exists(p_Graph) ||
                !RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(p_Graph)).Nodes.Any(p_N => p_N.Kind == "MeshPosition"))
                return;

            s_Script.AppendLine($"shader_db_vertex_mesh_pos {CloneSourceDb} {p_Clone} \"{s_Chain}\" \"{s_Chain}\"");
            s_MeshPositionClones.Add(p_Clone);
        }

        var s_Names = "";
        if (s_Patch != null)
        {
            // (from the preset the plan's own shader is authored on: the camo preset, or — as shipped — the one the document aims at)
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {p_Plan.OwnShaderPreset} {s_FpName} \"{s_Chain}\" \"{s_Patch}\" " +
                                "ShaderRenderMode_DeferredShadingGBufferLayout0");
            s_Names = s_FpName;
            DeclarePictures(s_FpName, DeclaredOnLane(p_Plan, "fp"));
            HandMeshPosition(s_FpName, p_Plan.ShaderGraphPath);
        }

        if (s_Patch != null && p_HasAug)
        {
            // Chained on the first clone ("-" = no reference dxbc, then the chain file), with the AUG's
            // declaration re-labelled from the one the preset was compiled for.
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {FpPresetName} {s_AugName} \"{s_Chain}\" \"{s_Patch}\" " +
                                $"ShaderRenderMode_DeferredShadingGBufferLayout0 \"-\" \"{s_Chain}\" {AugDecl} {AugFromDecl}");
            s_Names += "," + s_AugName;
            DeclarePictures(s_AugName, DeclaredOnLane(p_Plan, "fp"));
            HandMeshPosition(s_AugName, p_Plan.ShaderGraphPath);
        }

        // The no-camo clone, for a sticker package over the weapon as shipped: the game's NoCamo preset with
        // the no-camo twin graph's bytecode, chained on the same file; the weapons whose body wears that
        // preset bind it, and see their stickers on the weapon exactly as the game draws it.
        if (s_NoCamoPatch != null)
        {
            // ⛔ chained on the first clone only when there IS one: a package whose only own logic is an as-shipped graph edited on a
            // no-camo weapon has no camo clone before this one — the materials' rule, below
            var s_Tail = s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {NoCamoFpPresetName} {s_NoCamoName} \"{s_Chain}\" \"{s_NoCamoPatch}\" " +
                                $"ShaderRenderMode_DeferredShadingGBufferLayout0{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_NoCamoName;
            DeclarePictures(s_NoCamoName, DeclaredOnLane(p_Plan, "nc"));
            HandMeshPosition(s_NoCamoName, p_Plan.NoCamoShaderGraphPath);
        }

        // ⭐ THE FIRST-PERSON PRESET WITH ITS UV-TILE GATE OPENED (AllTilesGraphFor): the first-person body of the weapons the game gives
        // no camo row wears it with the package's values — a primary's wear on a body whose UVs are not laid out in tiles. A copy of the
        // preset the graph is derived from: the package's own first-person preset (its pictures and stickers along, as the AUG twin's)
        // or the game's. Chained on the same file; the first clone of the chain when the package has no graph of its own.
        if (s_AllTilesPatch != null)
        {
            var s_Tail = s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {(s_AllTilesFromOwn ? p_Plan.OwnShaderPreset : FpPresetName)} {s_AllTilesName} \"{s_Chain}\" " +
                                $"\"{s_AllTilesPatch}\" ShaderRenderMode_DeferredShadingGBufferLayout0{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_AllTilesName;
            if (s_AllTilesFromOwn)
            {
                DeclarePictures(s_AllTilesName, DeclaredOnLane(p_Plan, "fp"));
                HandMeshPosition(s_AllTilesName, s_AllTilesGraph);
            }
        }

        // ⭐ ONE CLONE PER EDITED MATERIAL, of the shader THAT MATERIAL wears. The mode
        // filter is the whole family ("ShaderRenderMode_" matches every mode as a substring): a glass or a
        // glow is a FORWARD shader, and cloning only the GBuffer mode would clone nothing of it. The patch is
        // the per-flavour manifest, so each variant keeps its own contract. Chained on the same file.
        var s_MaterialNames = new List<string>();
        for (var m = 0; m < p_Plan.MaterialShaders.Count; m++)
        {
            var s_Material = p_Plan.MaterialShaders[m];
            var s_Compiled = s_Shaders[s_MaterialAt + m];
            var s_MaterialPatch = s_Compiled.ManifestPath ?? s_Compiled.DxbcPath;
            if (!File.Exists(s_MaterialPatch))
                return $"shader: the bytecode of material #{s_Material.MaterialId} ('{s_MaterialPatch}') was not written.";

            // ⛔ The FIRST clone of the chain writes the file; only the ones after it read it back. A package
            // that edits a material but NOT the camo has no clone before this one, and chaining on a file
            // nobody wrote yet failed the whole bake ("Input file not found: clones.bin", measured).
            var s_CloneName = MaterialCloneName(p_Plan.Key, m);
            var s_Tail = s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {s_Material.Shader} {s_CloneName} \"{s_Chain}\" " +
                                $"\"{s_MaterialPatch}\" ShaderRenderMode_{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_CloneName;
            s_MaterialNames.Add(s_CloneName);
            DeclarePictures(s_CloneName, Declared(p_Plan.MaterialPictures
                .Where(p_P => p_P.Mesh.Equals(s_Material.Mesh, StringComparison.OrdinalIgnoreCase) && p_P.MaterialId == s_Material.MaterialId)
                .SelectMany(p_P => p_P.Pictures)));
        }

        // ⭐ ONE COPY PER ACCESSORY EDITED ON ITS OWN GRAPH: the third-person preset with THAT graph's bytecode, re-labelled to the
        // accessory's declaration where the preset has no solution for it (the declaration twins' rule, below) — the AUG's recipe of
        // a patched clone re-labelled in one go. Its material variation points at it instead of the preset or the plain twin.
        var s_AccessoryNames = new List<string>();
        // one copy per (graph, declaration it is re-labelled to): shared by every accessory that has both
        var s_AccessoryCopies = s_EditedAccessories
            .Select(p_A => (Graph: p_A.GraphPath!, Decl: p_A.Meshes.Select(p_M => p_M.Declaration).FirstOrDefault(NeedsDeclarationTwin)))
            .Distinct()
            .ToList();
        for (var e = 0; e < s_AccessoryCopies.Count; e++)
        {
            var (s_GraphOfCopy, s_Decl) = s_AccessoryCopies[e];
            var s_Compiled = s_Shaders[s_AccessoryAt + s_AccessoryGraphs.FindIndex(p_G => p_G.Equals(s_GraphOfCopy, StringComparison.OrdinalIgnoreCase))];
            var s_AccessoryPatch = s_Compiled.ManifestPath ?? s_Compiled.DxbcPath;
            if (!File.Exists(s_AccessoryPatch))
                return $"shader: the bytecode of the accessory graph '{s_GraphOfCopy}' ('{s_AccessoryPatch}') was not written.";

            var s_CloneName = $"Weapons/Shaders/Camo_{p_Plan.Key}_ACE{e}";
            var s_Input = s_Names.Length > 0 ? $"\"{s_Chain}\"" : "\"-\"";
            var s_Tail = s_Decl != null
                ? $" \"-\" {s_Input} {s_Decl} {AccessoryFromDecl}"
                : s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {AccessoryPresetName} {s_CloneName} \"{s_Chain}\" " +
                                $"\"{s_AccessoryPatch}\" ShaderRenderMode_DeferredShadingGBufferLayout0{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_CloneName;
            s_AccessoryNames.Add(s_CloneName);
            DeclarePictures(s_CloneName, Declared(s_EditedAccessories
                .Where(p_A => p_A.GraphPath == s_GraphOfCopy && p_A.Meshes.Select(p_M => p_M.Declaration).FirstOrDefault(NeedsDeclarationTwin) == s_Decl)
                .SelectMany(p_A => p_A.Pictures)));
        }

        // ⭐ ONE COPY PER AS-SHIPPED GRAPH OF ANOTHER PRESET (as shipped bakes too): the preset those weapons' bodies wear
        // (weaponpresetfp, weaponpresetshadowfp_xp2 — every weapon preset is in the source database, censused) with the graph's bytecode.
        var s_ShippedNames = new List<string>();
        for (var k = 0; k < p_Plan.ShippedShaders.Count; k++)
        {
            var s_Lane = p_Plan.ShippedShaders[k];
            var s_Compiled = s_Shaders[s_ShippedAt + k];
            var s_LanePatch = s_Compiled.ManifestPath ?? s_Compiled.DxbcPath;
            if (!File.Exists(s_LanePatch))
                return $"shader: the bytecode of the as-shipped graph of {string.Join(", ", s_Lane.Weapons)} ('{s_LanePatch}') was not written.";

            var s_CloneName = ShippedCloneName(p_Plan.Key, k);
            var s_Tail = s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {s_Lane.Preset} {s_CloneName} \"{s_Chain}\" \"{s_LanePatch}\" " +
                                $"ShaderRenderMode_DeferredShadingGBufferLayout0{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_CloneName;
            s_ShippedNames.Add(s_CloneName);
            DeclarePictures(s_CloneName, DeclaredOnLane(p_Plan, $"shp{k}"));
            HandMeshPosition(s_CloneName, s_Lane.GraphPath);
        }

        // ⭐ ONE COPY PER PIECE'S AS-SHIPPED GRAPH (the accessories get it too): the preset the piece's own
        // material wears (the ACOG's weaponpresetfp: the level's database carries its solutions for the piece's declarations — 0xBC3A4CC5 and
        // 0x882E8A4C measured on xp2_skybar, 8 GBuffer solutions each —, so nothing is re-labelled) with the graph's bytecode.
        var s_ShippedPieceNames = new List<string>();
        for (var p = 0; p < s_ShippedPieceCopies.Count; p++)
        {
            var s_Compiled = s_Shaders[s_ShippedPieceAt + p];
            var s_PiecePatch = s_Compiled.ManifestPath ?? s_Compiled.DxbcPath;
            if (!File.Exists(s_PiecePatch))
                return $"shader: the bytecode of the piece's as-shipped graph '{s_ShippedPieceCopies[p].Graph}' ('{s_PiecePatch}') was not written.";

            var s_CloneName = ShippedPieceCloneName(p_Plan.Key, p);
            var s_Tail = s_Names.Length > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {s_ShippedPieceCopies[p].Preset} {s_CloneName} \"{s_Chain}\" \"{s_PiecePatch}\" " +
                                $"ShaderRenderMode_DeferredShadingGBufferLayout0{s_Tail}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_CloneName;
            s_ShippedPieceNames.Add(s_CloneName);

            DeclarePictures(s_CloneName, PicturesToDeclare(s_ShippedPieces, s_ShippedPieceCopies[p]).Select(p_P => (p_P.Parameter, p_P.Register)));
        }

        // ⭐ THE ACCESSORIES' DECLARATION TWINS. The third-person preset draws most accessory meshes as it
        // ships (it has solutions for 882E8A4C and CE4574FD, measured); the two declarations it lacks —
        // BC3A4CC5 (+Color0) and C83353E0 (+TexCoord1), both supersets of 882E8A4C — get a clone of the
        // preset with ITS OWN bytecode kept and only the solutions' declaration label moved. One per
        // declaration, shared by every accessory of the package that declares it.
        var s_TwinDecls = p_Plan.Accessories
            .Where(p_A => !p_A.AsShipped)
            .SelectMany(p_A => p_A.Meshes)
            .Select(p_M => p_M.Declaration)
            .Where(NeedsDeclarationTwin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_D => p_D, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_TwinNames = new List<string>();
        foreach (var s_Decl in s_TwinDecls)
        {
            // ⛔ The reference dxbc and the input chain are POSITIONAL: both must be given ("-" for none) or
            // the parser reads the declarations as file names ("Input file not found: 0x882E8A4C", measured).
            var s_TwinName = AccessoryTwinName(p_Plan.Key, s_Decl);
            var s_Input = s_Names.Length > 0 ? $"\"{s_Chain}\"" : "\"-\"";
            s_Script.AppendLine($"shader_db_clone_entry {CloneSourceDb} {AccessoryPresetName} {s_TwinName} \"{s_Chain}\" " +
                                $"\"-\" ShaderRenderMode_DeferredShadingGBufferLayout0 \"-\" {s_Input} {s_Decl} {AccessoryFromDecl}");
            s_Names += (s_Names.Length > 0 ? "," : "") + s_TwinName;
            s_TwinNames.Add(s_TwinName);
        }

        // The sticker layer's material parameter, declared on the clones (both: the AUG twin samples it
        // too) — an ExternalTextureConstant at the register the graph samples plus the entry's own list,
        // the way Diffuse/Camo/Specular are declared. Chained on the same file, before the slice.
        // (…and the as-shipped copies whose graph samples the layer: a body that wears another preset, as shipped, with the stickers / the
        // emblem slot laid on its preset's graph — the pistols' weaponpresetfp. Same register: Ensure laid it at the plan's.)
        var s_StickerShipped = Enumerable.Range(0, s_ShippedNames.Count)
            .Where(k => p_Plan.ShippedShaders[k].GraphPath is { } s_LaneGraph && File.Exists(s_LaneGraph) &&
                        RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_LaneGraph)).StickerRegister != null)
            .Select(k => (Name: s_ShippedNames[k], Graph: p_Plan.ShippedShaders[k].GraphPath))
            .ToList();
        if (p_StickerRegister is { } s_StickerRegister && s_Patch != null)
        {
            s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_FpName} {StickerParameter} {s_StickerRegister} \"{s_Chain}\" \"{s_Chain}\"");
            foreach (var (s_ShippedName, _) in s_StickerShipped)
                s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_ShippedName} {StickerParameter} {s_StickerRegister} \"{s_Chain}\" \"{s_Chain}\"");
            if (p_HasAug)
                s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_AugName} {StickerParameter} {s_StickerRegister} \"{s_Chain}\" \"{s_Chain}\"");
            if (s_NoCamoPatch != null)
                s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_NoCamoName} {StickerParameter} {s_StickerRegister} \"{s_Chain}\" \"{s_Chain}\"");
            if (s_AllTilesFromOwn)
                s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_AllTilesName} {StickerParameter} {s_StickerRegister} \"{s_Chain}\" \"{s_Chain}\"");

            // The map on every clone (register + 1), the emblem slot's atlas after it, and an animated sticker's frame sheet — after the atlas
            // when both are there (StickerGraph.GifKeepsEmblem).
            var s_More = new List<(string, int)> { (StickerMapParameter, s_StickerRegister + 1) };
            if (p_Plan.StickerSheet != null)
                s_More.Add((StickerSheetParameter, p_Plan.StickerSheetRegister ?? s_StickerRegister + 2));
            if (p_Plan.EmblemAtlas != null)
                s_More.Add((EmblemAtlasParameter, s_StickerRegister + 2));
            foreach (var (s_Parameter, s_At) in s_More)
                {
                    s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_FpName} {s_Parameter} {s_At} \"{s_Chain}\" \"{s_Chain}\"");
                    if (p_HasAug)
                        s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_AugName} {s_Parameter} {s_At} \"{s_Chain}\" \"{s_Chain}\"");
                    if (s_NoCamoPatch != null)
                        s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_NoCamoName} {s_Parameter} {s_At} \"{s_Chain}\" \"{s_Chain}\"");
                    if (s_AllTilesFromOwn)
                        s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_AllTilesName} {s_Parameter} {s_At} \"{s_Chain}\" \"{s_Chain}\"");
                    foreach (var (s_ShippedName, _) in s_StickerShipped)
                        s_Script.AppendLine($"shader_db_add_external_texture {CloneSourceDb} {s_ShippedName} {s_Parameter} {s_At} \"{s_Chain}\" \"{s_Chain}\"");
                }

            // ⭐ The emblem slot's layers: EmblemL0..79 added to each clone's constant table, right after the constants its graph reads —
            // placed RELATIVE to one of them in each record (a record's numbering of the block moves with the blocks before it: a
            // $Globals before the constants shifts every Index by its size, measured 2026-09-29), so every flavour gets the element
            // its bytecode reads. The engine fills them by NAME from the weapon's shader parameter component; default zero = no layer.
            if (p_Plan.EmblemAtlas != null)
                foreach (var (s_Clone, s_Graph) in new[] { (s_FpName, p_Plan.ShaderGraphPath), (p_HasAug ? s_AugName : null, p_Plan.ShaderGraphPath),
                             (s_NoCamoPatch != null ? s_NoCamoName : null, p_Plan.NoCamoShaderGraphPath),
                             (s_AllTilesFromOwn ? s_AllTilesName : null, s_AllTilesGraph) }
                             .Concat(s_StickerShipped.Select(p_S => ((string?) p_S.Name, (string?) p_S.Graph))))
                {
                    if (s_Clone == null || s_Graph == null)
                        continue;
                    if (EmblemConstantsOf(s_Graph) is not { } s_Emblem)
                        return $"shader: the graph '{Path.GetFileName(s_Graph)}' of clone {s_Clone.Split('/')[^1]} draws no emblem slot, or has no external " +
                               "constant to place its layers after.";
                    s_Script.AppendLine($"shader_db_set_external_value {CloneSourceDb} {s_Clone} {RimeShaderEditor.Graph.Palette.EmblemConstantPrefix}{{0..{s_Emblem.Count - 1}}} " +
                                        $"rel:{s_Emblem.Reference}:{s_Emblem.Delta} \"{s_Chain}\" - \"{s_Chain}\"");

                    // ⭐ a PROJECTED slot's frames right after the layers (EmblemF0..): each weapon's variation fills them, zero = no square
                    if (EmblemFrameConstantsOf(s_Graph) is { } s_Frames)
                        s_Script.AppendLine($"shader_db_set_external_value {CloneSourceDb} {s_Clone} {RimeShaderEditor.Graph.EmblemSlot.FramePrefix}{{0..{s_Frames.Count - 1}}} " +
                                            $"rel:{s_Frames.Reference}:{s_Frames.Delta} \"{s_Chain}\" - \"{s_Chain}\"");
                }
        }

        s_Script.AppendLine($"shader_db_slice \"{s_Chain}\" \"{s_Sliced}\" \"{s_Names}\"");
        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(s_Work, "clones.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));
        var s_Output = Cache.GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
        File.WriteAllText(Path.Combine(s_Work, "clones.log"), s_Output, new UTF8Encoding(false));

        foreach (var s_Line in s_Output.Split('\n'))
        {
            var s_Trim = s_Line.Trim();
            if (s_Trim.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Command not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
                return "shader: " + s_Trim;
        }

        // ⛔ a copy that reads the mesh position on the game's vertex shaders reads (0,0,0) everywhere and draws no emblem, looking like any
        // camo that works: every one must say it re-pointed solutions to vertex shaders of its own (VERTEXMESHPOS, one line per render path)
        foreach (var s_Clone in s_MeshPositionClones)
        {
            var s_Replies = s_Output.Split('\n')
                .Select(p_L => p_L.Trim())
                .Where(p_L => p_L.StartsWith("VERTEXMESHPOS", StringComparison.Ordinal) && p_L.Contains($"'{s_Clone}'", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var s_Reply in s_Replies)
                s_Log($"  {s_Reply}");
            var s_Taken = s_Replies.Count(p_L => System.Text.RegularExpressions.Regex.Match(p_L, @", (\d+) solution\(s\) re-pointed") is { Success: true } s_M &&
                                                 int.Parse(s_M.Groups[1].Value) > 0);
            if (s_Taken == 0)
                return $"shader: '{s_Clone.Split('/')[^1]}' reads the mesh position (the projected emblem slot) and none of its vertex shaders " +
                       "took the patch that hands it over — read shader\\clones.log.";
        }

        if (!File.Exists(s_Sliced) || new FileInfo(s_Sliced).Length == 0)
            return "shader: the package's shader database was not written — read shader\\clones.log.";

        if (p_StickerRegister is { } s_Register)
        {
            // One report per clone, each naming the parameter at the register the graph samples — a
            // surgery that silently patched nothing would ship a shader reading a texture nobody binds.
            var s_Declared = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) and (\d+) shader entr\(ies\) with external '" +
                                                     Regex.Escape(StickerParameter) + @"' at t(\d+)")
                .Where(p_M => p_M.Groups[3].Value == s_Register.ToString() && int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0)
                .Count();
            var s_ExpectedClones = (p_HasAug ? 2 : 1) + (s_NoCamoPatch != null ? 1 : 0) + (s_AllTilesFromOwn ? 1 : 0) +
                                   s_StickerShipped.Count;   // (…and the as-shipped copies that sample the layer)
            if (s_Declared != s_ExpectedClones)
                return $"shader: the sticker parameter '{StickerParameter}' was declared on {s_Declared} clone(s) at t{s_Register}, expected {s_ExpectedClones} — read shader\\clones.log.";

            var s_MoreChecks = new List<(string, int)> { (StickerMapParameter, s_Register + 1) };
            if (p_Plan.StickerSheet != null)
                s_MoreChecks.Add((StickerSheetParameter, p_Plan.StickerSheetRegister ?? s_Register + 2));
            if (p_Plan.EmblemAtlas != null)
            {
                s_MoreChecks.Add((EmblemAtlasParameter, s_Register + 2));

                // and the layer constants on every clone: a clone without them draws no emblem, silently
                var s_Layered = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) in (\d+) shader entr\(ies\) with external value '" +
                                                        Regex.Escape(RimeShaderEditor.Graph.Palette.EmblemConstantPrefix) + @"\{")
                    .Count(p_M => int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0);
                if (s_Layered != s_ExpectedClones)
                    return $"shader: the emblem slot's layer constants went into {s_Layered} clone(s), expected {s_ExpectedClones} — read shader\\clones.log.";

                // …and a projected slot's frame constants on every clone whose graph projects: without them it holds no square, silently
                var s_Projecting = new[] { p_Plan.ShaderGraphPath, p_HasAug ? p_Plan.ShaderGraphPath : null, s_NoCamoPatch != null ? p_Plan.NoCamoShaderGraphPath : null,
                                           s_AllTilesFromOwn ? s_AllTilesGraph : null }
                    .Concat(s_StickerShipped.Select(p_S => p_S.Graph))
                    .Count(p_G => p_G != null && EmblemFrameConstantsOf(p_G) != null);
                var s_Framed = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) in (\d+) shader entr\(ies\) with external value '" +
                                                       Regex.Escape(RimeShaderEditor.Graph.EmblemSlot.FramePrefix) + @"\{")
                    .Count(p_M => int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0);
                if (s_Framed != s_Projecting)
                    return $"shader: the projected emblem slot's frame constants went into {s_Framed} clone(s), expected {s_Projecting} — read shader\\clones.log.";
            }
            foreach (var (s_Parameter, s_At) in s_MoreChecks)
                {
                    var s_Count = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) and (\d+) shader entr\(ies\) with external '" +
                                                          Regex.Escape(s_Parameter) + @"' at t(\d+)")
                        .Count(p_M => p_M.Groups[3].Value == s_At.ToString() && int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0);
                    if (s_Count != s_ExpectedClones)
                        return $"shader: the animated sticker parameter '{s_Parameter}' was declared on {s_Count} clone(s) at t{s_At}, expected {s_ExpectedClones} — read shader\\clones.log.";
                }
        }

        // Each picture parameter declared on its copy: one report per declaration naming it at its register — a declaration that patched
        // nothing ships a copy sampling a register no entry can bind (what showed in game on the ACOG: darker, glitching with the view)
        foreach (var s_Declaration in s_PictureParameters.Distinct())
        {
            var s_Count = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) and (\d+) shader entr\(ies\) with external '" +
                                                  Regex.Escape(s_Declaration.Parameter) + @"' at t(\d+)")
                .Count(p_M => p_M.Groups[3].Value == s_Declaration.Register.ToString() && int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0);
            var s_Copies = s_PictureParameters.Count(p_D => p_D == s_Declaration);
            if (s_Count != s_Copies)
                return $"shader: the picture parameter '{s_Declaration.Parameter}' was declared on {s_Count} cop(ies) at t{s_Declaration.Register}, expected " +
                       $"{s_Copies} — read shader\\clones.log.";
        }

        // The slice names how many shaders each render path kept: the clones, and only the clones. A render
        // path that carries FEWER is not a failure once material clones are in play — a forward-only shader
        // (a glass) has no entry in a deferred-only path — so the count is checked against what CAN be there.
        var s_Kept = Regex.Matches(s_Output, @"SLICE: (\d+) shader\(s\)").Select(p_M => int.Parse(p_M.Groups[1].Value)).ToList();
        var s_Expected = (s_Patch != null ? p_HasAug ? 2 : 1 : 0) + (s_NoCamoPatch != null ? 1 : 0) + (s_AllTilesPatch != null ? 1 : 0) +
                         s_MaterialNames.Count + s_AccessoryNames.Count + s_TwinNames.Count + s_ShippedNames.Count + s_ShippedPieceNames.Count;
        if (s_Kept.Count == 0 || s_Kept.Max() != s_Expected)
            return $"shader: the slice kept {string.Join("/", s_Kept)} shader(s) per render path, expected {s_Expected} — read shader\\clones.log.";

        p_ShaderDb = s_Sliced;
        if (s_Patch != null)
        {
            p_FpClone = new ShaderStub(s_FpName, p_GuidFor(0, 0x20), p_GuidFor(0, 0x21));
            WriteShaderStub(Path.Combine(s_Work, "stub_fp.json"), p_FpClone, p_Json);
        }

        if (s_Patch != null && p_HasAug)
        {
            p_AugFpClone = new ShaderStub(s_AugName, p_GuidFor(0, 0x22), p_GuidFor(0, 0x23));
            WriteShaderStub(Path.Combine(s_Work, "stub_augfp.json"), p_AugFpClone, p_Json);
        }

        if (s_NoCamoPatch != null)
        {
            p_NoCamoFpClone = new ShaderStub(s_NoCamoName, p_GuidFor(0, 0x24), p_GuidFor(0, 0x25));
            WriteShaderStub(Path.Combine(s_Work, "stub_ncfp.json"), p_NoCamoFpClone, p_Json);
            s_Log($"  shader: '{s_NoCamoName}' too — the no-camo preset {(p_StickerRegister != null ? "with the sticker layer" : "from its edited as-shipped graph")}, " +
                  "for the weapons that wear it as shipped.");
        }

        if (s_AllTilesPatch != null)
        {
            p_AllTilesFpClone = new ShaderStub(s_AllTilesName, p_GuidFor(0, 0x26), p_GuidFor(0, 0x27));
            WriteShaderStub(Path.Combine(s_Work, "stub_atfp.json"), p_AllTilesFpClone, p_Json);
            s_Log($"  shader: '{s_AllTilesName}' too — the first-person preset with its UV-tile gate opened, for the body (both views) of " +
                  $"{string.Join(", ", p_Plan.Weapons.Where(p_W => p_W.NoCamoRow).Select(p_W => p_W.Folder))} (no camo row in the game; the package draws the emblem / stickers).");
        }

        for (var k = 0; k < s_ShippedNames.Count; k++)
        {
            // their own guid space (0xF300): the weapon lanes use (0, 0x20..0x5F), the vehicles' copies 0xF100, the accessories' 0xF200
            var s_Stub = new ShaderStub(s_ShippedNames[k], p_GuidFor(0xF300, 0x10 + k * 2), p_GuidFor(0xF300, 0x11 + k * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_shipped{k}.json"), s_Stub, p_Json);
            p_ShippedClones.Add(s_Stub);
            s_Log($"  shader: '{s_Stub.Name}' — '{p_Plan.ShippedShaders[k].Preset.Split('/')[^1]}' from the edited as-shipped graph of " +
                  $"{string.Join(", ", p_Plan.ShippedShaders[k].Weapons)}, for those weapons alone.");
        }

        for (var p = 0; p < s_ShippedPieceNames.Count; p++)
        {
            // their own guid space (0xF400): the weapon lanes use (0, 0x20..0x5F), the vehicles' copies 0xF100, the accessories' 0xF200, the
            // weapons' as-shipped copies 0xF300
            var s_Stub = new ShaderStub(s_ShippedPieceNames[p], p_GuidFor(0xF400, 0x10 + p * 2), p_GuidFor(0xF400, 0x11 + p * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_{s_Stub.Name.Split('/')[^1]}.json"), s_Stub, p_Json);
            var s_Wearing = s_ShippedPieces.Where(p_A => (p_A.ShippedPreset!, p_A.ShippedGraphPath!) == s_ShippedPieceCopies[p]).ToList();
            foreach (var s_Piece in s_Wearing)
                p_AccessoryClones[s_Piece] = s_Stub;
            s_Log($"  shader: '{s_Stub.Name}' — '{s_ShippedPieceCopies[p].Preset.Split('/')[^1]}' from the edited as-shipped graph of " +
                  $"{string.Join(", ", s_Wearing.Select(p_A => $"{p_A.Weapon}/{p_A.Tag}"))}, for that piece alone (the same mesh wherever it hangs).");
        }

        for (var m = 0; m < s_MaterialNames.Count; m++)
        {
            var s_Material = p_Plan.MaterialShaders[m];
            var s_Stub = new ShaderStub(s_MaterialNames[m], p_GuidFor(0, 0x30 + m * 2), p_GuidFor(0, 0x31 + m * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_mat{m}.json"), s_Stub, p_Json);
            p_MaterialClones[RimeShaderEditor.Graph.ShaderGraph.MaterialKey(s_Material.Mesh, s_Material.MaterialId)] = s_Stub;
            s_Log($"  shader: '{s_Stub.Name}' — the edited graph of material #{s_Material.MaterialId} of " +
                  $"'{s_Material.Mesh.Split('/')[^1]}' (it wears '{s_Material.Shader.Split('/')[^1]}').");
        }

        for (var e = 0; e < s_AccessoryNames.Count; e++)
        {
            // their own guid space (0xF200): the weapon lanes' stubs use (0, 0x20..0x5F), the vehicles' copies 0xF100
            var s_Stub = new ShaderStub(s_AccessoryNames[e], p_GuidFor(0xF200, 0x10 + e * 2), p_GuidFor(0xF200, 0x11 + e * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_{s_Stub.Name.Split('/')[^1]}.json"), s_Stub, p_Json);
            var s_Wearing = s_EditedAccessories
                .Where(p_A => (p_A.GraphPath!, p_A.Meshes.Select(p_M => p_M.Declaration).FirstOrDefault(NeedsDeclarationTwin)) == s_AccessoryCopies[e])
                .ToList();
            foreach (var s_Accessory in s_Wearing)
                p_AccessoryClones[s_Accessory] = s_Stub;
            s_Log($"  shader: '{s_Stub.Name}' — the third-person preset with the OWN edited graph of " +
                  $"{string.Join(", ", s_Wearing.Select(p_A => $"{p_A.Weapon}/{p_A.Tag}"))}" +
                  (s_AccessoryCopies[e].Decl != null ? $", re-labelled to {s_AccessoryCopies[e].Decl}" : "") +
                  ", for that accessory alone (the same mesh wherever it hangs).");
        }

        for (var t = 0; t < s_TwinNames.Count; t++)
        {
            var s_Stub = new ShaderStub(s_TwinNames[t], p_GuidFor(0, 0x50 + t * 2), p_GuidFor(0, 0x51 + t * 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_acc{t}.json"), s_Stub, p_Json);
            p_AccessoryTwins[s_TwinDecls[t]] = s_Stub;
            s_Log($"  shader: '{s_Stub.Name}' — the third-person preset re-labelled to {s_TwinDecls[t]}, for the " +
                  "accessories that declare it (the preset has no solution of its own for it).");
        }

        s_Log($"  shader: {(s_Patch != null ? $"'{s_FpName}'{(p_HasAug ? $" and '{s_AugName}'" : "")}" : $"the package's {s_Names.Split(',').Length} cop(ies)")} " +
              $"cut into {new FileInfo(s_Sliced).Length / 1024} KB " +
              $"({(s_Shaders.Count > 0 && s_Shaders[0].ManifestPath != null ? "per-flavour manifest" : "no compilation")}).");
        return null;
    }

    /// <summary>
    /// Which of the package's copies a weapon's first-person material draws with, and so which of its pictures go there: the copy of the
    /// preset it wears as shipped ("shp{k}", its as-shipped graph's pictures), the no-camo copy ("nc", the same), the package's own shader
    /// ("fp", its camo document's pictures) — or none (null): the game's preset, which only its document's pictures on the preset's own
    /// parameters can reach. The same rule the variation's shader follows (Build, s_OwnClone).
    /// </summary>
    private static (string? Lane, IReadOnlyList<GraphPicture> Pictures) PictureLaneOf(BakePlan p_Plan, BakeWeapon p_Weapon)
    {
        var s_Shipped = p_Plan.ShippedShaders.FindIndex(p_S => p_S.Weapons.Contains(p_Weapon.Folder, StringComparer.OrdinalIgnoreCase));
        if (s_Shipped >= 0)
            return ($"shp{s_Shipped}", p_Weapon.ShippedPictures);
        if (!p_Weapon.OwnShader)
            return (null, p_Weapon.Pictures);
        if (p_Plan.NoCamoShaderGraphPath != null && p_Weapon.WearsNoCamo && !p_Weapon.Aug)
            return ("nc", p_Weapon.ShippedPictures);
        return ("fp", p_Weapon.Pictures);
    }

    /// <summary>The parameters one copy of the weapons' declares for their pictures: every weapon that draws with it, together.</summary>
    private static List<(string Parameter, int Register)> DeclaredOnLane(BakePlan p_Plan, string p_Lane) => p_Plan.Weapons
        .Select(p_W => PictureLaneOf(p_Plan, p_W))
        .Where(p_L => p_L.Lane == p_Lane)
        .SelectMany(p_L => p_L.Pictures)
        .Where(p_P => p_P.Declare)
        .Select(p_P => (p_P.Parameter, p_P.Register))
        .Distinct()
        .ToList();

    /// <summary>
    /// Whether the plan has a body to open the tile gate for (<see cref="AllTilesGraphFor"/>): a weapon the game gives no camo row, painted,
    /// in a package whose own shader DRAWS SOMETHING ON THE BODY — stickers or the emblem slot (the emblem on the pistols, first and third
    /// person, as on the primaries). A plain camo keeps the accessories' material on both of a pistol's meshes — the look confirmed in
    /// game (run 362, and run 369 after the run-368 revert: colour correct; the first-person copy of the preset with the
    /// primaries' material numbers made them darker and was taken out).
    /// (Public for the callers that look the compiler up only when they know a graph compiles: this lane is decided here, by the plan.)
    /// </summary>
    public static bool NeedsAllTiles(BakePlan p_Plan) =>
        p_Plan.PaintsBody && p_Plan.ShaderGraphPath != null && p_Plan.StickerRegister != null &&
        // (a pistol AS SHIPPED with the slot wears a copy of its own preset instead — ShippedShaders)
        p_Plan.Weapons.Any(p_W => p_W.NoCamoRow &&
                                  !p_Plan.ShippedShaders.Any(p_S => p_S.Weapons.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase)));

    /// <summary>The tile gate's node in the first-person camo preset's translation (camographs\weapons_shaders_weaponpresetshadowfp.json): the
    /// `Less` whose first input is the Scalar 1 and whose second is the uv's second half — phase E's "all tiles" twin opened this one.</summary>
    private const string TileGateNodeId = "7e1a53e852224810ab3a2f13db6745c9";

    /// <summary>
    /// ⛔ A WEAPON THE GAME GIVES NO CAMO ROW WEARS, WHERE THE PACKAGE DRAWS THE EMBLEM OR STICKERS ON ITS BODY, THE FIRST-PERSON CAMO
    /// PRESET WITH ITS UV-TILE GATE OPENED — in BOTH views, as a primary's body does (its third-person mesh draws the body with the
    /// first-person family's material: bf3-customize-menu-revamp, batch 7). The emblem lives in that preset; the accessories' material a
    /// pistol wears otherwise (the third-person instance on both families, run 349/362) has none. The first-person preset ZONES the camo by
    /// UV tile — `1 &lt; v` on the uv's second half picks the scratch channel, and only weapons that ship camos lay their UVs out for it
    /// (bf3-weapon-shaders); a pistol's body is not: run 349, body dark. So the copy's gate is always true: a Scalar of -1000 on the gate's
    /// first input (what phase E's "all tiles" twin did, measured on the ACOG). The graph is the package's own first-person graph (its
    /// stickers, pictures, emblem and edits ride along). Written beside the bake's shaders; its path, or null when nothing needs it (no such
    /// weapon or nothing drawn on the body: <see cref="NeedsAllTiles"/>) or there is no gate to open in the graph (another preset's logic) —
    /// then the pistol keeps the accessories' material, said in the log. ⛔ No material numbers of a primary's are added (run 368: the
    /// pistols came out darker, and that was taken out).
    /// </summary>
    private static string? AllTilesGraphFor(BakePlan p_Plan, string p_Work, Action<string> p_Log)
    {
        if (!NeedsAllTiles(p_Plan))
            return null;

        var s_Pistols = string.Join(", ", p_Plan.Weapons.Where(p_W => p_W.NoCamoRow).Select(p_W => p_W.Folder));
        var s_Source = p_Plan.ShaderGraphPath!;
        const string s_From = "the package's own first-person graph";

        RimeShaderEditor.Graph.ShaderGraph s_Graph;
        try { s_Graph = RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_Source)); }
        catch (Exception s_Exception)
        {
            p_Log($"  NOTE: '{Path.GetFileName(s_Source)}' is not a graph ({s_Exception.Message}), so {s_Pistols} keep the accessories' material (no emblem on them).");
            return null;
        }

        // the gate, by shape — Less(Scalar 1, the uv's second half out of a MixOut) — and, failing that, by the translation's own node id
        RimeShaderEditor.Graph.GraphConnection? Into(string p_Node, string p_Port) =>
            s_Graph.Connections.FirstOrDefault(p_C => p_C.ToNode == p_Node && p_C.ToPort == p_Port);
        RimeShaderEditor.Graph.GraphNode? NodeOf(string p_Id) => s_Graph.Nodes.FirstOrDefault(p_N => p_N.Id == p_Id);
        static bool IsOne(RimeShaderEditor.Graph.GraphNode? p_Node) =>
            p_Node is { Kind: "Scalar" } &&
            float.TryParse(p_Node.GetParam("Value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_Value) && Math.Abs(s_Value - 1f) < 1e-6f;
        var s_Gates = s_Graph.Nodes
            .Where(p_N => p_N.Kind == "Less" &&
                          Into(p_N.Id, "Input1") is { } s_First && IsOne(NodeOf(s_First.FromNode)) &&
                          Into(p_N.Id, "Input2") is { FromPort: "G" } s_Second && NodeOf(s_Second.FromNode) is { Kind: "MixOut" })
            .ToList();
        if (s_Gates.Count == 0 && NodeOf(TileGateNodeId) is { Kind: "Less" } s_ById && Into(s_ById.Id, "Input1") != null)
            s_Gates.Add(s_ById);
        if (s_Gates.Count != 1)
        {
            p_Log($"  NOTE: {s_From} ('{Path.GetFileName(s_Source)}') has {(s_Gates.Count == 0 ? "no" : s_Gates.Count.ToString())} UV-tile gate to open, so " +
                  $"{s_Pistols} keep the accessories' material (no emblem on them).");
            return null;
        }

        var s_Gate = s_Gates[0];
        var s_Open = new RimeShaderEditor.Graph.GraphNode
        {
            Kind = "Scalar",
            X = s_Gate.X - 262,
            Y = s_Gate.Y + 68,
            Comment = "The tile gate opened: -1000 < v on every tile, so the whole body takes the camo channel (a pistol's UVs are not laid out in tiles).",
            Params = { ["Value"] = "-1000" },
        };
        s_Graph.Nodes.Add(s_Open);
        var s_Wire = Into(s_Gate.Id, "Input1")!;
        s_Wire.FromNode = s_Open.Id;
        s_Wire.FromPort = "Out";

        // ⛔ SCOPEOCC FIXED AT 0 (Gold on the pistols: switching to the primary turned the pistols black, only during the transition).
        // The preset colours the body by lerp(spec.R, spec.B, ScopeOcc). A pistol's own shader-parameter
        // component carries no ScopeOcc (Emblem.lua AddSidearmComponents: the primaries' 1 would draw it black), so it draws with 0 = spec.R
        // -- but the soldier's render copies to the weapon the block of the CURRENT weapon's component (getCurrentWeaponShaderParameterComponent,
        // bf3-weapon-camo-frontend), and while the pistol is put away the current one is the primary, whose ScopeOcc is 1: the pistol took
        // spec.B, ~0 over most of its body (measured: B < 0.05 on 81 % of m9_s, 38 % of glock_s) -- black until it left the screen. A
        // pistol's copy reads 0 there whatever block it is handed.
        var s_ScopeOcc = s_Graph.Nodes.Where(p_N => p_N.Kind == "ExternalConstant" &&
                                                    (p_N.GetParam("Name") ?? "").Equals("external_ScopeOcc", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var s_Fixed = 0;
        foreach (var s_Constant in s_ScopeOcc)
        {
            var s_Zero = new RimeShaderEditor.Graph.GraphNode
            {
                Kind = "Color",
                X = s_Constant.X,
                Y = s_Constant.Y + 60,
                Comment = "ScopeOcc fixed at 0 on a pistol's copy: the body is coloured by spec.R whatever weapon's parameter block the soldier hands it.",
                Params = { ["Value"] = "0,0,0,0" },
            };
            s_Graph.Nodes.Add(s_Zero);
            foreach (var s_Out in s_Graph.Connections.Where(p_C => p_C.FromNode == s_Constant.Id).ToList())
            {
                s_Out.FromNode = s_Zero.Id;
                s_Out.FromPort = "Out";
                s_Fixed++;
            }
        }

        var s_Path = Path.Combine(p_Work, "alltiles_fp.json");
        File.WriteAllText(s_Path, s_Graph.ToJson(), new UTF8Encoding(false));
        p_Log($"  shader: {s_Pistols} (no camo row in the game) wear the first-person preset with its UV-tile gate opened in both views (the package " +
              $"draws the emblem / stickers on the body) — '{Path.GetFileName(s_Path)}' from {s_From}, the gate at node {s_Gate.Id[..6]}; " +
              $"ScopeOcc fixed at 0 on {s_Fixed} wire(s) (the body never darkens with the primary's parameter block while it is put away).");
        return s_Path;
    }

    /// <summary>The pictures of one piece's copy (a (preset, graph) pair) that sit on a register the preset has no parameter at: each
    /// declared once on the copy.</summary>
    private static IEnumerable<GraphPicture> PicturesToDeclare(IEnumerable<BakeAccessory> p_Pieces, (string Preset, string Graph) p_Copy) => p_Pieces
        .Where(p_A => p_A.ShippedPreset == p_Copy.Preset && p_A.ShippedGraphPath == p_Copy.Graph)
        .SelectMany(p_A => p_A.ShippedPictures)
        .Where(p_P => p_P.Declare)
        .DistinctBy(p_P => (p_P.Parameter, p_P.Register));

    /// <summary>The texture a package ships for the n-th distinct picture of its edited graphs (see GraphPicture).</summary>
    public static string PictureTextureName(string p_Key, int p_Index) => $"Weapons/Custom/Camo_{p_Key}_Picture{p_Index}";

    /// <summary>The partition the accessories' material variations live in.</summary>
    public static string AccessoryMaterialsName(string p_Key) => $"Weapons/Custom/Camo_{p_Key}_AccMaterials";

    /// <summary>The partition of a package's vehicle material variations.</summary>
    public static string VehicleMaterialsName(string p_Key) => $"Vehicles/Custom/Camo_{p_Key}_VehMaterials";

    /// <summary>The bundle, in the package's pack superbundle, that carries the plan's v-th vehicle (loaded behind its level bundles).</summary>
    public static string VehicleBundleName(int p_Vehicle) => $"veh{p_Vehicle}";

    /// <summary>The d-th further shader database of the package (the vehicle pieces' copies from a second source level, …), in the
    /// shaders bundle next to levels/{stem}/shaderdb.</summary>
    private static string ExtraShaderDbName(string p_Stem, int p_Index) => $"levels/{p_Stem}/v{p_Index + 1}/shaderdb";

    /// <summary>The mini database of a package's v-th vehicle body (its clone's hash-0 entry).</summary>
    public static string VehicleEntryName(string p_Key, int p_Index) => $"MV_{p_Key}_VEH{p_Index}";

    /// <summary>The (piece, 0) entry of a vehicle's camo piece.</summary>
    public static string VehiclePieceEntryName(string p_Key, int p_Vehicle, int p_Piece) => $"MV_{p_Key}_VEH{p_Vehicle}P{p_Piece}";

    /// <summary>
    /// A body piece's name: the body clone's with its last letters replaced by "p" and the first part's number (the M1A2's turret
    /// under Berkut Tank: C/BERKUT_TANK/m1abrams_mesh -> C/BERKUT_TANK/m1abrams_p059). As long as the body's, which the command needs
    /// (the header and the EBX store the string in place).
    /// </summary>
    public static string PieceCloneName(string p_Key, string p_EbxName, Catalog.VehiclePiece p_Piece)
    {
        // (as long as the name of the mesh it is cut FROM: the cockpit mesh's is its own)
        var s_Body = AccessoryCloneName(p_Key, BakeVehicle.FromBody(p_Piece) ? p_EbxName : p_Piece.Mesh!);
        // ("c" for a piece cut from the cockpit mesh: it can take the same first part as the body's own — the AH-6's hull and cabin, 8)
        var s_Tag = $"{(BakeVehicle.FromBody(p_Piece) ? 'p' : 'c')}{(p_Piece.Parts.Count > 0 ? p_Piece.Parts[0] : 0):000}";
        return s_Body.Length > s_Tag.Length ? s_Body[..^s_Tag.Length] + s_Tag : s_Body;
    }

    /// <summary>
    /// The material variations of the package's vehicles: ONE MeshMaterialVariation per vehicle with NO shader -- the
    /// material keeps its own vehicle preset, the way the game ships its own values-only variations -- carrying the camo's
    /// values. No ObjectVariation: they ride in the clones' hash-0 entries, the body's base look.
    /// </summary>
    private static void WriteVehicleMaterials(string p_Path, string p_Name, string p_Partition,
        IReadOnlyList<(string Instance, BakeVehicle Vehicle)> p_Materials, JsonSerializerOptions p_Json,
        IReadOnlyList<(string Instance, BakeVehicle Vehicle, ShaderStub? Twin, bool Camo)>? p_TwinMaterials = null)
    {
        var s_Instances = new Dictionary<string, object>();
        foreach (var (s_Instance, s_Vehicle) in p_Materials)
            s_Instances[s_Instance] = new Dictionary<string, object?>
            {
                ["$type"] = "MeshMaterialVariation",
                ["Shader"] = new Dictionary<string, object?>
                {
                    ["Shader"] = null,
                    ["BoolParameters"] = Array.Empty<object>(),
                    ["VectorParameters"] = s_Vehicle.Values.Select(p_V => Parameter(p_V.Key, p_V.Value)).ToList(),
                    ["VectorArrayParameters"] = Array.Empty<object>(),
                    ["TextureParameters"] = Array.Empty<object>(),
                },
            };

        // the pieces' materials whose preset has no rigid solution in the multiplayer levels: the same numbers, drawn with the
        // package's copy of that preset (the stub names it; the package's shader database carries its solutions); a copy for a
        // material that takes no camo (glass, lights) carries no numbers, only the copy
        // (and one per material whose own graph only carries a picture: the shader it would draw with there — a copy, or null: its own —
        // and the camo's numbers when it is a camo material, so the picture's binding can be scoped to that material alone)
        foreach (var (s_Instance, s_Vehicle, s_Twin, s_Camo) in p_TwinMaterials ?? Array.Empty<(string, BakeVehicle, ShaderStub?, bool)>())
            s_Instances[s_Instance] = new Dictionary<string, object?>
            {
                ["$type"] = "MeshMaterialVariation",
                ["Shader"] = new Dictionary<string, object?>
                {
                    ["Shader"] = s_Twin == null
                        ? null
                        : new Dictionary<string, object> { ["PartitionGuid"] = s_Twin.Partition, ["InstanceGuid"] = s_Twin.Instance },
                    ["BoolParameters"] = Array.Empty<object>(),
                    ["VectorParameters"] = s_Camo
                        ? s_Vehicle.Values.Select(p_V => Parameter(p_V.Key, p_V.Value)).ToList()
                        : new List<Dictionary<string, object>>(),
                    ["VectorArrayParameters"] = Array.Empty<object>(),
                    ["TextureParameters"] = Array.Empty<object>(),
                },
            };

        var s_Partition = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_Materials[0].Instance,
            ["Instances"] = s_Instances,
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };

        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Partition, p_Json), new UTF8Encoding(false));
    }

    /// <summary>
    /// The material variations of the package's accessories: ONE MeshMaterialVariation per accessory, naming
    /// the third-person preset (or its declaration twin) and the numbers that accessory carries. No
    /// ObjectVariation: these ride in the (clone, 0) entries — the key the engine's sockets read — which is
    /// the mesh's BASE entry, not a variation of it.
    /// </summary>
    /// <summary>
    /// The constants an accessory's material variation writes: the host weapon's wear and tiling (the third-person baseline) and every other
    /// one the user set for it (BakeAccessory.Overridden) — engine-fed ones never, and only the ones it has a value for. CAMO_ACCESSORY_OVERRIDES_OLD=1
    /// = the baseline alone, as before (review 2026-09-29, C4).
    /// </summary>
    internal static IReadOnlyList<string> AccessoryParametersOf(BakeAccessory p_Accessory) => s_ThreePBaseline
        .Concat(Environment.GetEnvironmentVariable("CAMO_ACCESSORY_OVERRIDES_OLD") == "1"
            ? Enumerable.Empty<string>()
            : p_Accessory.Overridden.Where(p_K => WeaponConstants.Find(p_K) is { Kind: not ConstantKind.Engine }).OrderBy(p_K => p_K, StringComparer.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(p_K => p_Accessory.Values.ContainsKey(p_K))
        .ToList();

    private static void WriteAccessoryMaterials(string p_Path, string p_Name, string p_Partition,
        IReadOnlyList<(string Instance, BakeAccessory Accessory, ShaderStub? Twin)> p_Materials,
        JsonSerializerOptions p_Json,
        IReadOnlyList<(string Instance, ShaderStub? Stub)>? p_MaterialShaders = null)
    {
        var s_Instances = new Dictionary<string, object>();

        // one instance per edited material of an accessory mesh (a glass, a reticle): its own clone and NO constants — what changed is
        // the logic, the material keeps its numbers and textures (the weapons' rule, WriteVariation)
        // (a material whose own graph only carries a picture: a BARE one, no shader either — its binding is what moves)
        foreach (var (s_MaterialInstance, s_Stub) in p_MaterialShaders ?? Array.Empty<(string, ShaderStub?)>())
            s_Instances[s_MaterialInstance] = new Dictionary<string, object?>
            {
                ["$type"] = "MeshMaterialVariation",
                ["Shader"] = new Dictionary<string, object?>
                {
                    ["Shader"] = s_Stub == null
                        ? null
                        : new Dictionary<string, object> { ["PartitionGuid"] = s_Stub.Partition, ["InstanceGuid"] = s_Stub.Instance },
                    ["BoolParameters"] = Array.Empty<object>(),
                    ["VectorParameters"] = Array.Empty<object>(),
                    ["VectorArrayParameters"] = Array.Empty<object>(),
                    ["TextureParameters"] = Array.Empty<object>(),
                },
            };
        foreach (var (s_Instance, s_Accessory, s_Twin) in p_Materials)
        {
            var (s_Part, s_Inst) = s_Twin != null
                ? (s_Twin.Partition, s_Twin.Instance)
                : (ThreePPartition, ThreePInstance);

            s_Instances[s_Instance] = new Dictionary<string, object?>
            {
                ["$type"] = "MeshMaterialVariation",
                ["Shader"] = new Dictionary<string, object?>
                {
                    ["Shader"] = new Dictionary<string, object> { ["PartitionGuid"] = s_Part, ["InstanceGuid"] = s_Inst },
                    ["BoolParameters"] = Array.Empty<object>(),
                    ["VectorParameters"] = AccessoryParametersOf(s_Accessory)
                        .Select(p_K => Parameter(p_K, s_Accessory.Values[p_K]))
                        .ToList(),
                    ["VectorArrayParameters"] = Array.Empty<object>(),
                    ["TextureParameters"] = Array.Empty<object>(),
                },
            };
        }

        var s_Partition = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_Materials[0].Instance,
            ["Instances"] = s_Instances,
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };

        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Partition, p_Json), new UTF8Encoding(false));
    }

    /// <summary>The EBX stub a variation's material points at: a shader graph asset that only has a name.</summary>
    private static void WriteShaderStub(string p_Path, ShaderStub p_Stub, JsonSerializerOptions p_Json)
    {
        var s_Stub = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_Stub.Instance,
            ["Instances"] = new Dictionary<string, object>
            {
                [p_Stub.Instance] = new Dictionary<string, object>
                {
                    ["$type"] = "ShaderGraph", ["MaxSubMaterialCount"] = 8, ["GammaCorrectionEnable"] = true, ["Name"] = p_Stub.Name,
                },
            },
            ["Name"] = p_Stub.Name,
            ["PartitionGuid"] = p_Stub.Partition,
        };

        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Stub, p_Json), new UTF8Encoding(false));
    }

    /// <summary>A game texture's cached picture (the studio's texture cache), or null when it is not cached or none is named.</summary>
    internal static string? CachedPngOf(string p_Texture)
    {
        if (string.IsNullOrWhiteSpace(p_Texture))
            return null;

        var s_Png = Path.Combine(Settings.TextureCache, Sanitize(p_Texture) + ".png");
        return File.Exists(s_Png) ? s_Png : null;
    }

    /// <summary>The cached diffuse of a weapon's body, for a values-only camo's row picture; null if none is cached.</summary>
    private static string? WeaponDiffuseOf(BakeWeapon p_Weapon)
    {
        foreach (var s_Texture in p_Weapon.Textures.Where(p_T => p_T.EndsWith("_d", StringComparison.OrdinalIgnoreCase)))
        {
            var s_Png = Path.Combine(Settings.TextureCache, Sanitize(s_Texture) + ".png");
            if (File.Exists(s_Png))
                return s_Png;
        }

        return null;
    }

    /// <summary>The material parameter the sticker layer is bound under — declared on the clones, bound on the variations.</summary>
    public const string StickerParameter = "Sticker";

    /// <summary>The material parameter of the stickers' per-mesh map: side in red, animated y in green, x in alpha (StickerRegister + 1).</summary>
    public const string StickerMapParameter = "StickerMap";

    /// <summary>The material parameter of the animated sticker's frame sheet, one per package (StickerRegister + 2).</summary>
    public const string StickerSheetParameter = "StickerSheet";

    /// <summary>The material parameter of the emblem slot's shape atlas, one per package (StickerRegister + 2, where a GIF's sheet would be).</summary>
    public const string EmblemAtlasParameter = "EmblemAtlas";

    /// <summary>The emblem slot's shape atlas a package ships.</summary>
    public static string EmblemAtlasTextureName(string p_Key) => $"Weapons/Custom/EmblemAtlas_{p_Key}";

    /// <summary>
    /// Where a compiled graph reads the emblem slot's layers, as the shader table needs to be told: how many constants, and their first
    /// one's distance from an external constant of the same block the table already holds (Reference, bare name) — so the layers are
    /// added relative to it in every record (shader_db_set_external_value rel:). Null when the graph draws no emblem slot or reads no
    /// other constant of that block.
    /// </summary>
    internal static (int Count, string Reference, int Delta)? EmblemConstantsOf(string p_GraphPath) =>
        ConstantsOf(p_GraphPath, "EmblemLayers", RimeShaderEditor.Graph.Palette.EmblemFields);

    /// <summary>The same for a PROJECTED slot's frame constants (EmblemF0..), which sit right after the layers: null when the graph does not project.</summary>
    internal static (int Count, string Reference, int Delta)? EmblemFrameConstantsOf(string p_GraphPath) =>
        ConstantsOf(p_GraphPath, "EmblemProjection", RimeShaderEditor.Graph.Palette.EmblemFrameFields);

    private static (int Count, string Reference, int Delta)? ConstantsOf(string p_GraphPath, string p_Kind,
        Func<RimeShaderEditor.Graph.GraphNode, List<(string Name, int Buffer, int Element)>> p_Fields)
    {
        try
        {
            var s_Graph = RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(p_GraphPath));
            if (s_Graph.Nodes.FirstOrDefault(p_N => p_N.Kind == p_Kind) is not { } s_Emblem)
                return null;

            var s_Fields = p_Fields(s_Emblem);
            if (s_Fields.Count == 0)
                return null;

            var s_Buffer = s_Fields[0].Buffer;
            var s_Reference = s_Graph.Nodes
                .Where(p_N => p_N.Kind == "ExternalConstant" &&
                              (p_N.GetParam("Buffer") is not { Length: > 0 } s_B || s_B.Equals("externalConstants", StringComparison.OrdinalIgnoreCase)) &&
                              int.TryParse(p_N.GetParam("Register"), out var s_R) && s_R == s_Buffer &&
                              int.TryParse(p_N.GetParam("Element"), out _))
                .OrderBy(p_N => int.Parse(p_N.GetParam("Element")))
                .FirstOrDefault();
            if (s_Reference == null)
                return null;

            var s_Name = s_Reference.GetParam("Name");
            var s_Bare = s_Name.StartsWith("external_", StringComparison.OrdinalIgnoreCase) ? s_Name["external_".Length..] : s_Name;
            return (s_Fields.Count, s_Bare, s_Fields[0].Element - int.Parse(s_Reference.GetParam("Element")));
        }
        catch (Exception)
        {
            return null;
        }
    }


    /// <summary>"1P" or "3P" from a body mesh's name, the suffix that tells the two apart in a texture name.</summary>
    public static string MeshTag(string p_Mesh) =>
        p_Mesh.EndsWith("_3p_mesh", StringComparison.OrdinalIgnoreCase) ? "3P" : "1P";

    /// <summary>The sticker layer texture a package ships for one weapon mesh (NONE, no tag, for the shared empty one).</summary>
    public static string StickerTextureName(string p_Key, string p_WeaponKey, string? p_MeshTag = null) =>
        $"Weapons/Custom/Sticker_{p_Key}_{p_WeaponKey}" + (p_MeshTag != null ? $"_{p_MeshTag}" : "");

    /// <summary>The sticker map a package ships for one weapon mesh (NONE, no tag, for the shared empty one).</summary>
    public static string StickerMapTextureName(string p_Key, string p_WeaponKey, string? p_MeshTag) =>
        $"Weapons/Custom/StickerMap_{p_Key}_{p_WeaponKey}" + (p_MeshTag != null ? $"_{p_MeshTag}" : "");

    /// <summary>The animated sticker's frame sheet a package ships, once.</summary>
    public static string StickerSheetTextureName(string p_Key) => $"Weapons/Custom/StickerSheet_{p_Key}";

    /// <summary>
    /// A sticker layer as the game reads it: DXT5 (the alpha is the sticker), a full mip chain, its own
    /// size, classic header. From a TGA — the one picture format that carries alpha and no colour-space
    /// metadata for the converter to honour. Returns the failure, or null.
    /// </summary>
    private static string? ConvertLayer(string p_Texconv, string p_Source, string p_Out, Action<string> p_Log, string p_What = "sticker layer")
    {
        if (!File.Exists(p_Source))
            return $"the {p_What} '{p_Source}' does not exist.";

        var s_Dir = Path.Combine(Path.GetDirectoryName(p_Out)!, "conv_" + Path.GetFileNameWithoutExtension(p_Out));
        Directory.CreateDirectory(s_Dir);

        // -tgazeroalpha: the converter takes a TGA whose alpha is zero EVERYWHERE for a picture without an
        // alpha channel and ships it opaque — the empty layer (nothing placed) came out as opaque black and
        // painted every weapon body black (2026-09-12, F2000 with an animated sticker: dark in 1P, black in 3P).
        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -m 0 -f DXT5 -tgazeroalpha -o \"{s_Dir}\" \"{p_Source}\"",
            Path.Combine(s_Dir, Path.GetFileNameWithoutExtension(p_Source) + ".dds"), p_Out);
        if (s_Error != null)
            return s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT5")
            return $"the {p_What} came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT5.";

        p_Log($"  {p_What}: {Path.GetFileName(p_Source)} -> DXT5 {s_Header.Width}x{s_Header.Height}, {s_Header.Mips} mip(s).");
        return null;
    }

    /// <summary>
    /// The sticker map (side in red, animated y in green, x in alpha) as the game reads it: DXT5, ONE mip —
    /// a coordinate blended across mips or texels is not a coordinate, the shader reads it texel by texel at
    /// level 0. Returns the failure, or null.
    /// </summary>
    private static string? ConvertMap(string p_Texconv, string p_Source, string p_Out, Action<string> p_Log)
    {
        if (!File.Exists(p_Source))
            return $"the map '{p_Source}' does not exist.";

        var s_Dir = Path.Combine(Path.GetDirectoryName(p_Out)!, "conv_" + Path.GetFileNameWithoutExtension(p_Out));
        Directory.CreateDirectory(s_Dir);

        // DXT5: its ALPHA is 8 bits a texel (the colour 5/6/5) and it is the one lane the engine reads for
        // a material — the two-channel BC5 lane came out as garbage in-game and the one-channel BC4 lane as
        // a black weapon (2026-09-12, F2000).
        // -tgazeroalpha: see ConvertLayer — an all-zero alpha (the empty map, x = 0 everywhere) must stay zero.
        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -m 1 -f DXT5 -tgazeroalpha -o \"{s_Dir}\" \"{p_Source}\"",
            Path.Combine(s_Dir, Path.GetFileNameWithoutExtension(p_Source) + ".dds"), p_Out);
        if (s_Error != null)
            return s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT5")
            return $"the map came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT5.";

        p_Log($"  sticker map: {Path.GetFileName(p_Source)} -> DXT5 {s_Header.Width}x{s_Header.Height}, {s_Header.Mips} mip(s).");
        return null;
    }

    /// <summary>A 4×4 fully transparent 32-bit TGA — the layer of a weapon with nothing placed on it.</summary>
    private static void WriteEmptyLayerTga(string p_Path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
        using var s_Writer = new BinaryWriter(File.Create(p_Path));
        s_Writer.Write((byte) 0);
        s_Writer.Write((byte) 0);
        s_Writer.Write((byte) 2);
        s_Writer.Write(new byte[5]);
        s_Writer.Write((ushort) 0);
        s_Writer.Write((ushort) 0);
        s_Writer.Write((ushort) 4);
        s_Writer.Write((ushort) 4);
        s_Writer.Write((byte) 32);
        s_Writer.Write((byte) 0x28);
        s_Writer.Write(new byte[4 * 4 * 4]);
    }

    /// <summary>The pattern as the game's own camo textures ship: 512×512, DXT1, a full mip chain, classic header.</summary>
    private static string? ConvertPattern(string p_Texconv, string p_Source, string p_Out, Action<string> p_Log)
    {
        if (!File.Exists(p_Source))
            return $"the pattern image '{p_Source}' does not exist.";

        var s_Dir = Path.Combine(Path.GetDirectoryName(p_Out)!, "conv_pattern");
        Directory.CreateDirectory(s_Dir);

        // Through a BMP, never the user's file itself: see WriteBmp — a PNG carrying colour-space chunks
        // would come out of texconv darkened and more saturated, and the game then decodes it as sRGB again.
        var s_Image = LoadImage(p_Source);
        if (s_Image == null)
            return $"pattern: '{Path.GetFileName(p_Source)}' could not be decoded.";

        var s_Bmp = Path.Combine(Path.GetDirectoryName(p_Out)!, "pattern.bmp");
        WriteBmp(s_Image, s_Bmp);

        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -w 512 -h 512 -m 0 -f DXT1 -o \"{s_Dir}\" \"{s_Bmp}\"",
            Path.Combine(s_Dir, "pattern.dds"), p_Out);
        if (s_Error != null)
            return "pattern: " + s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT1" || s_Header.Width != 512 || s_Header.Height != 512)
            return $"pattern came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT1 512x512.";

        p_Log($"  pattern: {Path.GetFileName(p_Source)} -> DXT1 512x512, {s_Header.Mips} mip(s).");
        return null;
    }

    /// <summary>
    /// A picture of an edited graph (GraphPicture) as the game reads it: DXT1, a full mip chain, classic header, its own size rounded to
    /// powers of two and kept at 2048 at most (it is resident: a mod does not fill the streaming pool). Through a BMP, as the pattern: a
    /// PNG's colour-space chunks would darken it. Returns the failure, or null.
    /// </summary>
    private static string? ConvertPicture(string p_Texconv, string p_Source, string p_Out, Action<string> p_Log)
    {
        if (!File.Exists(p_Source))
            return $"'{p_Source}' does not exist.";

        var s_Image = LoadImage(p_Source);
        if (s_Image == null)
            return $"'{Path.GetFileName(p_Source)}' could not be decoded.";

        var s_Stem = Path.GetFileNameWithoutExtension(p_Out);
        var s_Folder = Path.GetDirectoryName(p_Out)!;
        var s_Dir = Path.Combine(s_Folder, "conv_" + s_Stem);
        Directory.CreateDirectory(s_Dir);
        var s_Bmp = Path.Combine(s_Folder, s_Stem + ".bmp");
        WriteBmp(s_Image, s_Bmp);

        static int Side(int p_Pixels) => Math.Min(2048, (int) System.Numerics.BitOperations.RoundUpToPowerOf2((uint) Math.Max(4, p_Pixels)));
        var (s_Width, s_Height) = (Side(s_Image.PixelWidth), Side(s_Image.PixelHeight));
        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -w {s_Width} -h {s_Height} -m 0 -f DXT1 -o \"{s_Dir}\" \"{s_Bmp}\"",
            Path.Combine(s_Dir, s_Stem + ".dds"), p_Out);
        if (s_Error != null)
            return s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT1")
            return $"it came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT1.";

        p_Log($"  picture: {Path.GetFileName(p_Source)} ({s_Image.PixelWidth}x{s_Image.PixelHeight}) -> DXT1 {s_Header.Width}x{s_Header.Height}, {s_Header.Mips} mip(s).");
        return null;
    }

    /// <summary>
    /// A picture on a soldier part's MASK as the game reads one: DXT5 WITH ITS ALPHA — the mask's alpha is where the camo goes (the
    /// character shaders' lerp(Mask.rgb, CamoTile, Mask.a), measured 2026-09-28) and the game's own masks are DXT5 (us_lowerbody04_m, _m3).
    /// ⛔ ConvertPicture (a BMP into DXT1) drops it: the US support's legs' mask (boots, knee pads and belt out) came out
    /// with an alpha of 1 on EVERY texel — the camo back on the boots in the game while the studio's preview, reading the PNG, showed it
    /// right. Through a PNG written from the decoded pixels (no colour-space chunks), its own size rounded to powers of two, 2048 at most;
    /// the alpha that comes out is measured against the picture's own and a mismatch is a failure. Returns it, or null.
    /// </summary>
    private static string? ConvertMaskPicture(string p_Texconv, string p_Source, string p_Out, Action<string> p_Log)
    {
        if (!File.Exists(p_Source))
            return $"'{p_Source}' does not exist.";

        if (LoadImage(p_Source) is not { } s_Loaded)
            return $"'{Path.GetFileName(p_Source)}' could not be decoded.";

        var s_Image = new FormatConvertedBitmap(s_Loaded, PixelFormats.Bgra32, null, 0);
        var s_Pixels = new byte[s_Image.PixelWidth * s_Image.PixelHeight * 4];
        s_Image.CopyPixels(s_Pixels, s_Image.PixelWidth * 4, 0);
        var s_Share = ShareOver(s_Pixels, 3);

        var s_Stem = Path.GetFileNameWithoutExtension(p_Out);
        var s_Folder = Path.GetDirectoryName(p_Out)!;
        var s_Dir = Path.Combine(s_Folder, "conv_" + s_Stem);
        Directory.CreateDirectory(s_Dir);
        // ⛔ Through a 32-bit TGA: a BMP drops the alpha, and a PNG (even one written from bare pixels) is read by texconv as sRGB and
        // written LINEAR — the colour under the camo came out darker (the mask's mean 196 → 142, measured 2026-09-28). A TGA carries no
        // colour space: the bytes go in as they are.
        var s_Tga = Path.Combine(s_Folder, s_Stem + ".tga");
        WriteTga(s_Pixels, s_Image.PixelWidth, s_Image.PixelHeight, s_Tga);

        static int Side(int p_Pixels) => Math.Min(2048, (int) System.Numerics.BitOperations.RoundUpToPowerOf2((uint) Math.Max(4, p_Pixels)));
        var (s_Width, s_Height) = (Side(s_Image.PixelWidth), Side(s_Image.PixelHeight));
        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -w {s_Width} -h {s_Height} -m 0 -f DXT5 -o \"{s_Dir}\" \"{s_Tga}\"",
            Path.Combine(s_Dir, s_Stem + ".dds"), p_Out);
        if (s_Error != null)
            return s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT5")
            return $"it came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT5 (a mask's alpha is where the camo goes).";

        // ⛔ the alpha AND the colour that came out, BY THEIR BYTES (a trivial artifact is audited at the converter's output: texconv once made
        // an empty layer opaque, and read a PNG mask as sRGB)
        var s_Shipped = RimeShaderEditor.View.DdsImage.Load(p_Out) is { } s_Dds ? new FormatConvertedBitmap(s_Dds, PixelFormats.Bgra32, null, 0) : null;
        var s_ShippedShare = -1.0;
        var s_ShippedMean = Array.Empty<double>();
        if (s_Shipped != null)
        {
            var s_Out = new byte[s_Shipped.PixelWidth * s_Shipped.PixelHeight * 4];
            s_Shipped.CopyPixels(s_Out, s_Shipped.PixelWidth * 4, 0);
            s_ShippedShare = ShareOver(s_Out, 3);
            s_ShippedMean = MeanOf(s_Out);
        }

        var s_Mean = MeanOf(s_Pixels);
        if (s_ShippedShare < 0 || Math.Abs(s_ShippedShare - s_Share) > 0.02)
            return $"its alpha did not survive the conversion: {s_Share:P1} of the picture over half, {(s_ShippedShare < 0 ? "unreadable" : $"{s_ShippedShare:P1}")} " +
                   "of the DDS.";
        if (s_ShippedMean.Length < 3 || Enumerable.Range(0, 3).Any(c => Math.Abs(s_ShippedMean[c] - s_Mean[c]) > 6))
            return $"its colour did not survive the conversion: the picture's mean {string.Join("/", s_Mean.Take(3).Select(p_V => $"{p_V:0}"))}, the DDS's " +
                   $"{string.Join("/", s_ShippedMean.Take(3).Select(p_V => $"{p_V:0}"))} (a colour space applied on the way?).";

        p_Log($"  mask picture: {Path.GetFileName(p_Source)} ({s_Image.PixelWidth}x{s_Image.PixelHeight}) -> DXT5 {s_Header.Width}x{s_Header.Height}, " +
              $"{s_Header.Mips} mip(s); its alpha (where the camo goes) over half on {s_ShippedShare:P1} of the texels (the picture: {s_Share:P1}), " +
              $"its colour {string.Join("/", s_ShippedMean.Take(3).Select(p_V => $"{p_V:0}"))} (the picture: {string.Join("/", s_Mean.Take(3).Select(p_V => $"{p_V:0}"))}).");
        return null;
    }

    /// <summary>The mean of each channel of a BGRA block, in R, G, B, A order.</summary>
    private static double[] MeanOf(byte[] p_Bgra)
    {
        var s_Sum = new double[4];
        for (var i = 0; i + 3 < p_Bgra.Length; i += 4)
        {
            s_Sum[0] += p_Bgra[i + 2];
            s_Sum[1] += p_Bgra[i + 1];
            s_Sum[2] += p_Bgra[i];
            s_Sum[3] += p_Bgra[i + 3];
        }

        var s_Count = Math.Max(1, p_Bgra.Length / 4);
        return s_Sum.Select(p_S => p_S / s_Count).ToArray();
    }

    /// <summary>An uncompressed 32-bit TGA of a BGRA block (top-left origin, 8 alpha bits): no colour space for a converter to apply.</summary>
    private static void WriteTga(byte[] p_Bgra, int p_Width, int p_Height, string p_Path)
    {
        var s_Header = new byte[18];
        s_Header[2] = 2; // uncompressed true colour
        BitConverter.GetBytes((ushort) p_Width).CopyTo(s_Header, 12);
        BitConverter.GetBytes((ushort) p_Height).CopyTo(s_Header, 14);
        s_Header[16] = 32;
        s_Header[17] = 0x28; // top-left origin, 8 bits of alpha
        using var s_Stream = File.Create(p_Path);
        s_Stream.Write(s_Header);
        s_Stream.Write(p_Bgra);
    }

    /// <summary>The share of a BGRA block whose channel <paramref name="p_Channel"/> is over half.</summary>
    private static double ShareOver(byte[] p_Bgra, int p_Channel)
    {
        var s_On = 0;
        for (var i = p_Channel; i < p_Bgra.Length; i += 4)
            if (p_Bgra[i] > 128)
                s_On++;
        return p_Bgra.Length == 0 ? 0 : s_On / (p_Bgra.Length / 4.0);
    }

    /// <summary>
    /// The row's picture: a 256×64 band cut from the pattern, DXT5, one mip, classic header — the shape of
    /// the game's own thumbnails. False (with a log line) when there is nothing to cut it from; the package
    /// then ships without a picture rather than not at all.
    /// </summary>
    private static bool MakeThumbnail(string p_Texconv, string p_Source, string p_WorkDir, string p_Dds, string p_Bin, Action<string> p_Log)
    {
        try
        {
            if (!File.Exists(p_Source))
            {
                p_Log($"  thumbnail: no image at '{p_Source}' — the row will have no picture.");
                return false;
            }

            var s_Bmp = Path.Combine(p_WorkDir, "thumb.bmp");
            var s_Image = LoadImage(p_Source);
            if (s_Image == null)
            {
                p_Log($"  thumbnail: '{Path.GetFileName(p_Source)}' could not be decoded — the row will have no picture.");
                return false;
            }

            // Scale so the band is covered, then cut the middle: a tileable pattern shows a couple of repeats.
            var s_Scale = Math.Max(256.0 / s_Image.PixelWidth, 64.0 / s_Image.PixelHeight);
            var s_Scaled = new TransformedBitmap(s_Image, new ScaleTransform(s_Scale, s_Scale));
            var s_X = Math.Max(0, (s_Scaled.PixelWidth - 256) / 2);
            var s_Y = Math.Max(0, (s_Scaled.PixelHeight - 64) / 2);
            var s_Band = new CroppedBitmap(s_Scaled, new Int32Rect(s_X, s_Y,
                Math.Min(256, s_Scaled.PixelWidth), Math.Min(64, s_Scaled.PixelHeight)));

            WriteBmp(s_Band, s_Bmp);

            var s_Dir = Path.Combine(p_WorkDir, "conv_thumb");
            Directory.CreateDirectory(s_Dir);
            var s_Error = RunTexconv(p_Texconv, $"-nologo -y -w 256 -h 64 -m 1 -f DXT5 -o \"{s_Dir}\" \"{s_Bmp}\"",
                Path.Combine(s_Dir, "thumb.dds"), p_Dds);
            if (s_Error != null)
            {
                p_Log("  thumbnail: " + s_Error + " — the row will have no picture.");
                return false;
            }

            var s_Header = ReadHeader(p_Dds);
            if (s_Header.FourCc != "DXT5" || s_Header.Width != 256 || s_Header.Height != 64)
            {
                p_Log($"  thumbnail came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT5 256x64 — no picture.");
                return false;
            }

            // The chunk is the DDS without its 128-byte header: raw pixels, as the game's own thumbnails ship.
            var s_Bytes = File.ReadAllBytes(p_Dds);
            File.WriteAllBytes(p_Bin, s_Bytes.Skip(128).ToArray());
            p_Log($"  thumbnail: 256x64 DXT5, {s_Bytes.Length - 128} bytes of pixels.");
            return true;
        }
        catch (Exception s_Exception)
        {
            p_Log($"  thumbnail failed: {s_Exception.Message} — the row will have no picture.");
            return false;
        }
    }

    /// <summary>
    /// The image texconv is handed, as a BMP: pixels only, no colour-space metadata.
    ///
    /// ⛔ MEASURED 2026-09-11 (the thumbnail came out oversaturated): a PNG that carries sRGB/gAMA chunks —
    /// which WPF's PNG encoder writes, and Photoshop's — makes texconv convert the pixels from sRGB to linear
    /// while compressing to a non-sRGB DXT format (a 256×64 band went from mean 170 to 115, saturation 179 to
    /// 232), and the game's own thumbnails carry NO such conversion (their pixels match their patterns').
    /// For the pattern it would be worse still: the game samples it as sRGB, so it would be decoded twice.
    /// </summary>
    private static void WriteBmp(BitmapSource p_Image, string p_Path)
    {
        var s_Encoder = new BmpBitmapEncoder();
        s_Encoder.Frames.Add(BitmapFrame.Create(p_Image));
        using var s_Stream = File.Create(p_Path);
        s_Encoder.Save(s_Stream);
    }

    private static BitmapSource? LoadImage(string p_Path)
    {
        if (p_Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return RimeShaderEditor.View.DdsImage.Load(p_Path);

        var s_Bitmap = new BitmapImage();
        s_Bitmap.BeginInit();
        s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
        s_Bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        s_Bitmap.UriSource = new Uri(p_Path);
        s_Bitmap.EndInit();
        s_Bitmap.Freeze();
        return s_Bitmap;
    }

    /// <summary>Runs texconv and claims its output (it names the file after the input). Null on success.</summary>
    private static string? RunTexconv(string p_Texconv, string p_Arguments, string p_Produced, string p_Claim)
    {
        if (!File.Exists(p_Texconv))
            return "texconv.exe not found (expected beside RimeREPL.exe).";

        if (File.Exists(p_Produced))
            File.Delete(p_Produced);

        var s_Process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p_Texconv)
        {
            Arguments = p_Arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var s_Output = s_Process == null ? "" : s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
        s_Process?.WaitForExit();

        if (!File.Exists(p_Produced))
            return $"texconv produced nothing: {s_Output.Trim()}";

        if (File.Exists(p_Claim))
            File.Delete(p_Claim);

        File.Move(p_Produced, p_Claim);
        return null;
    }

    private static (string FourCc, int Width, int Height, int Mips) ReadHeader(string p_Dds)
    {
        var s_Header = new byte[128];
        using (var s_Stream = File.OpenRead(p_Dds))
            _ = s_Stream.Read(s_Header, 0, s_Header.Length);

        return (Encoding.ASCII.GetString(s_Header, 84, 4), BitConverter.ToInt32(s_Header, 16),
            BitConverter.ToInt32(s_Header, 12), BitConverter.ToInt32(s_Header, 28));
    }

    private static void WriteTextureStub(string p_Path, string p_Name, string p_Partition, string p_Instance, JsonSerializerOptions p_Json)
    {
        var s_Stub = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_Instance,
            ["Instances"] = new Dictionary<string, object>
            {
                [p_Instance] = new Dictionary<string, object> { ["$type"] = "TextureAsset", ["Name"] = p_Name },
            },
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };

        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Stub, p_Json), new UTF8Encoding(false));
    }

    /// <summary>
    /// The weapon's variation: the ObjectVariation the unlock points at, and the two material variations
    /// that put the camo-sampling presets on the weapon's materials — shaped exactly like the game's own
    /// (Weapons/M416/M416_CAMO1_ABU3), texture parameters EMPTY because the engine reads bindings from the
    /// database entry, never from here.
    /// </summary>
    /// <param name="p_ValuesOnly">No pattern: the material variations name NO shader (the material keeps its
    /// own, textures included) and carry only the constants the user changed — the shape of the game's own
    /// values-only variations.</param>
    /// <param name="p_FpShader">The package's own first-person preset, when it ships one; null = the game's.</param>
    /// <param name="p_AugFpShader">Its AUG-layout twin, for the AUG's variation; null = the framework's AUG clone.</param>
    private static void WriteVariation(string p_Path, string p_Name, uint p_Hash, string p_Partition, string p_Instance,
        string p_MmvFp, string p_Mmv3P, BakeWeapon p_Weapon, bool p_ValuesOnly, JsonSerializerOptions p_Json,
        ShaderStub? p_FpShader = null, ShaderStub? p_AugFpShader = null,
        IReadOnlyList<(string Instance, ShaderStub? Stub)>? p_MaterialShaders = null)
    {
        var (s_FpPart, s_FpInst) = p_Weapon.Aug
            ? (p_AugFpShader?.Partition ?? AugFpPartition, p_AugFpShader?.Instance ?? AugFpInstance)
            : (p_FpShader?.Partition ?? FpPartition, p_FpShader?.Instance ?? FpInstance);
        var (s_3PPart, s_3PInst) = p_Weapon.Aug ? (Aug3PPartition, Aug3PInstance) : (ThreePPartition, ThreePInstance);

        var s_Overrides = p_Weapon.Overridden
            .Where(p_K => WeaponConstants.Find(p_K) is { Kind: not ConstantKind.Engine })
            .ToList();

        // ⭐ A WEAPON WITH A SHADER OF ITS OWN IN THE PACKAGE WEARS IT, values-only or not (the as-shipped graphs must bake too): a camo
        // with no pattern and no game camo — as shipped, or the basic camo — used to write NO shader on the body,
        // so a logic edited there never left the studio. The first-person material names its clone; its numbers stay as they were
        // (only the ones changed are written, the rest the material keeps — the edited materials' own rule, below).
        var s_OwnFp = p_Weapon.Aug ? p_AugFpShader != null : p_FpShader != null;

        // ⭐ a PROJECTED emblem slot: this weapon's squares as the frame constants its copy reads (EmblemF0.., raw float4s).
        // ⛔ NOT in the variation by default (two players with the same weapon and camo: only the first to spawn got the emblem): the
        // variation is shared by every copy of the weapon with this camo and the second copy drew no square — the
        // frames ride in each weapon's own component, like the layers (the Lua writes them; emblem_frames.json below is their source).
        // CAMO_EMBLEM_FRAMES_IN_VARIATION=1 = the way back.
        var s_Frames = p_Weapon.EmblemProjection.Length == 0 || Environment.GetEnvironmentVariable("CAMO_EMBLEM_FRAMES_IN_VARIATION") != "1"
            ? new List<Dictionary<string, object>>()
            : RimeShaderEditor.Graph.EmblemSlot.FramesOf(p_Weapon.EmblemProjection)
                .Select((p_V, i) => VectorParameter($"{RimeShaderEditor.Graph.EmblemSlot.FramePrefix}{i}", p_V))
                .ToList();

        Dictionary<string, object?> Material(string p_Part, string p_Inst, IEnumerable<string> p_Baseline, bool p_Bind) => new()
        {
            ["$type"] = "MeshMaterialVariation",
            ["Shader"] = new Dictionary<string, object?>
            {
                ["Shader"] = !p_Bind
                    ? null
                    : new Dictionary<string, object> { ["PartitionGuid"] = p_Part, ["InstanceGuid"] = p_Inst },
                ["BoolParameters"] = Array.Empty<object>(),
                ["VectorParameters"] = p_Baseline
                    .Where(p_K => p_Weapon.Values.ContainsKey(p_K))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(p_K => Parameter(p_K, p_Weapon.Values[p_K]))
                    .Concat(s_Frames)
                    .ToList(),
                ["VectorArrayParameters"] = Array.Empty<object>(),
                ["TextureParameters"] = Array.Empty<object>(),
            },
        };

        // Values only: nothing but what the user changed, on both material variations — a constant the
        // material's shader does not declare is simply never bound.
        var s_FpNames = p_ValuesOnly ? s_Overrides : s_FpBaseline.Concat(s_Overrides);
        var s_3PNames = p_ValuesOnly ? s_Overrides : s_ThreePBaseline.Concat(s_Overrides);

        var s_Instances = new Dictionary<string, object>
        {
            [p_MmvFp] = Material(s_FpPart, s_FpInst, s_FpNames, !p_ValuesOnly || s_OwnFp),
            [p_Mmv3P] = Material(s_3PPart, s_3PInst, s_3PNames, !p_ValuesOnly),
            [p_Instance] = new Dictionary<string, object>
            {
                ["$type"] = "ObjectVariation", ["Name"] = p_Name, ["NameHash"] = p_Hash,
            },
        };

        // One instance per edited material: it names that material's own clone and NO constants — what
        // changed is the shader's logic, and the material keeps every number and texture it ships with.
        // (a material whose own graph only carries a picture gets a BARE one: no shader either — the binding is what moves)
        foreach (var (s_MaterialInstance, s_Stub) in p_MaterialShaders ?? Array.Empty<(string, ShaderStub?)>())
            s_Instances[s_MaterialInstance] = Material(s_Stub?.Partition ?? "", s_Stub?.Instance ?? "", Array.Empty<string>(), s_Stub != null);

        var s_Partition = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_Instance,
            ["Instances"] = s_Instances,
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };

        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Partition, p_Json), new UTF8Encoding(false));
    }

    /// <summary>A raw four-component vector parameter (ShaderParameterType_Vec4, 295 uses in the game's data): a projected slot's frame.</summary>
    private static Dictionary<string, object> VectorParameter(string p_Name, float[] p_Value) => new()
    {
        ["Value"] = new Dictionary<string, object> { ["x"] = p_Value[0], ["y"] = p_Value[1], ["z"] = p_Value[2], ["w"] = p_Value[3] },
        ["ParameterType"] = "ShaderParameterType_Vec4",
        ["ParameterName"] = p_Name,
    };

    /// <summary>
    /// One vector parameter, typed the way the game types it (measured on its own variations: tilings are
    /// Vec2, tints are Color, the rest Scalar) with the components that type reads.
    /// </summary>
    private static Dictionary<string, object> Parameter(string p_Name, string p_Vector)
    {
        var s_Parts = p_Vector.Split(',')
            .Select(p_C => float.TryParse(p_C.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V) ? s_V : 0f)
            .Concat(new[] { 0f, 0f, 0f, 0f })
            .Take(4)
            .ToArray();

        var s_Kind = WeaponConstants.Find(p_Name)?.Kind ?? ConstantKind.Scalar;
        var (s_Type, s_Keep) = s_Kind switch
        {
            ConstantKind.Tiling => ("ShaderParameterType_Vec2", 2),
            ConstantKind.Color => ("ShaderParameterType_Color", 4),
            _ => ("ShaderParameterType_Scalar", 1),
        };

        return new Dictionary<string, object>
        {
            ["Value"] = new Dictionary<string, object>
            {
                ["x"] = s_Parts[0],
                ["y"] = s_Keep > 1 ? s_Parts[1] : 0f,
                ["z"] = s_Keep > 2 ? s_Parts[2] : 0f,
                ["w"] = s_Keep > 3 ? s_Parts[3] : 0f,
            },
            ["ParameterType"] = s_Type,
            ["ParameterName"] = p_Name,
        };
    }

    /// <summary>
    /// Reads the built bundles the way the game will. The log flatters: a build ends in "successfully built"
    /// with a partition missing, a texture without its resource, an entry without its hash. What has to be
    /// true is checked in the bytes and in the counters, and every failure names what it saw.
    /// </summary>
    private static void Audit(BakeResult p_Result, string p_Log, string p_PackageDir,
        IReadOnlyList<(string Name, uint Hash)> p_Variations, IReadOnlyList<string> p_Databases,
        IReadOnlySet<string> p_Referenced, string? p_PatternName, string? p_ThumbName, int p_EntryCount,
        bool p_ValuesOnly = false, IReadOnlyList<string>? p_ShaderDbNames = null, IReadOnlyList<string>? p_StickerNames = null,
        int p_ValuesOnlyBound = 0, IReadOnlyList<string>? p_PictureNames = null)
    {
        var s_HasShaderDb = p_ShaderDbNames is { Count: > 0 };
        p_StickerNames ??= Array.Empty<string>();

        // the edited graphs' pictures: each a resource check_textures lists, in packnc — or the entry binds a parameter to nothing
        if (p_PictureNames is { Count: > 0 })
        {
            var s_NcFile = Path.Combine(p_PackageDir, "packnc.sb");
            var s_NcData = File.Exists(s_NcFile) ? File.ReadAllBytes(s_NcFile) : Array.Empty<byte>();
            foreach (var s_Picture in p_PictureNames)
            {
                if (!Regex.IsMatch(p_Log, @"TEX\s+" + Regex.Escape(s_Picture.ToLowerInvariant()) + @"\s+\d+x\d+"))
                    p_Result.Problems.Add($"check_textures did not list the graph's picture '{s_Picture}'.");
                if (s_NcData.Length == 0 || !(Contains(s_NcData, Encoding.ASCII.GetBytes(s_Picture.ToLowerInvariant())) || Contains(s_NcData, Encoding.ASCII.GetBytes(s_Picture))))
                    p_Result.Problems.Add($"the graph's picture '{s_Picture}' is not in packnc.sb.");
            }
        }
        if (p_StickerNames.Count > 0)
        {
            // Every layer has a resource (check_textures lists it) and lives in packnc, or a weapon draws
            // its body through a parameter bound to nothing.
            var s_Nc = Path.Combine(p_PackageDir, "packnc.sb");
            var s_NcBytes = File.Exists(s_Nc) ? File.ReadAllBytes(s_Nc) : Array.Empty<byte>();
            foreach (var s_Layer in p_StickerNames)
            {
                if (!Regex.IsMatch(p_Log, @"TEX\s+" + Regex.Escape(s_Layer.ToLowerInvariant()) + @"\s+\d+x\d+"))
                    p_Result.Problems.Add($"check_textures did not list the sticker layer '{s_Layer}'.");
                if (s_NcBytes.Length == 0 || !(Contains(s_NcBytes, Encoding.ASCII.GetBytes(s_Layer.ToLowerInvariant())) || Contains(s_NcBytes, Encoding.ASCII.GetBytes(s_Layer))))
                    p_Result.Problems.Add($"the sticker layer '{s_Layer}' is not in packnc.sb.");
            }

            if (Regex.IsMatch(p_Log, @"TEX-BAD"))
                p_Result.Problems.Add("check_textures marked a texture TEX-BAD (see build.log).");

            // And the parameter itself: bound on every entry (the "Set N texture parameter(s)" count includes it).
            var s_SetCounts = Regex.Matches(p_Log, @"Set (\d+) texture parameter").Select(p_M => int.Parse(p_M.Groups[1].Value)).ToList();
            if (s_SetCounts.Count != p_EntryCount || s_SetCounts.Any(p_N => p_N < 1))
                p_Result.Problems.Add($"the sticker layer was bound on {s_SetCounts.Count(p_N => p_N >= 1)} of {p_EntryCount} entries.");
        }

        // pack always; packnc when the package carries a pattern or a thumbnail; chunks with a thumbnail;
        // shaders when the camo ships its own preset.
        // (packnc also carries the sticker layers and the graphs' pictures when there is no pattern and no picture of the row)
        var s_Expected = 1 + (p_PatternName != null || p_ThumbName != null || p_StickerNames.Count > 0 || p_PictureNames is { Count: > 0 } ? 1 : 0) +
                         (p_ThumbName != null ? 1 : 0) +
                         (s_HasShaderDb ? 1 : 0);

        if (s_HasShaderDb)
        {
            // The fresh-named database has to be IN the bundle: a template the command cannot use leaves a
            // bundle with the stubs only and a log that still says "successfully built" (measured on the AUG's).
            foreach (var s_DbName in p_ShaderDbNames!)
                if (!Regex.IsMatch(p_Log, @"Adding '" + Regex.Escape(s_DbName) + "'", RegexOptions.IgnoreCase))
                    p_Result.Problems.Add($"the package's shader database '{s_DbName}' was not added to the shaders bundle.");

            var s_ShadersSb = Path.Combine(p_PackageDir, "shaders.sb");
            if (!File.Exists(s_ShadersSb) || new FileInfo(s_ShadersSb).Length < 1024)
                p_Result.Problems.Add("shaders.sb is missing or empty — the camo's own preset would not exist in game.");
        }
        var s_Built = Regex.Matches(p_Log, "Superbundle successfully built").Count;
        if (s_Built != s_Expected)
            p_Result.Problems.Add($"{s_Built} superbundle(s) built, expected {s_Expected} — read build.log.");

        foreach (var s_Line in p_Log.Split('\n'))
        {
            var s_Trim = s_Line.Trim();
            if (s_Trim.Contains("Command not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.StartsWith("VARIATION FAILED", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Could not find partition", StringComparison.OrdinalIgnoreCase))
                p_Result.Problems.Add("Rime: " + s_Trim);
        }

        // Every entry must have handed its FIRST-person variation to at least one material — the body. An
        // entry whose materials matched no shader would ship the variation nowhere and the audit below would
        // still count it as authored.
        var s_ByShader = Regex.Matches(p_Log, @"by shader \(inst#1=(\d+)").Select(p_M => int.Parse(p_M.Groups[1].Value)).ToList();
        if (s_ByShader.Count != p_EntryCount)
            p_Result.Problems.Add($"{s_ByShader.Count} entries handed their variations out by shader, expected {p_EntryCount}.");
        else if (s_ByShader.Any(p_N => p_N == 0))
            p_Result.Problems.Add($"{s_ByShader.Count(p_N => p_N == 0)} entr(ies) found no material for the first-person variation.");

        var s_Pack = Path.Combine(p_PackageDir, "pack.sb");
        if (!File.Exists(s_Pack) || new FileInfo(s_Pack).Length == 0)
        {
            p_Result.Problems.Add("pack.sb was not written.");
            return;
        }

        // ⛔ NAMES ARE STORED IN LOWERCASE inside a bundle (measured on the bench's own bundles: mv_rojo1p,
        // weapons/custom/camo_rojo), whatever case the script gave them. Searched both ways.
        static bool Named(byte[] p_Bytes, string p_Name) =>
            Contains(p_Bytes, Encoding.ASCII.GetBytes(p_Name.ToLowerInvariant())) ||
            Contains(p_Bytes, Encoding.ASCII.GetBytes(p_Name));

        var s_PackBytes = File.ReadAllBytes(s_Pack);
        foreach (var (s_Name, s_Hash) in p_Variations)
        {
            if (!Named(s_PackBytes, s_Name))
                p_Result.Problems.Add($"variation '{s_Name}' is not in pack.sb (its partition was not added).");

            if (!Contains(s_PackBytes, BitConverter.GetBytes(s_Hash)))
                p_Result.Problems.Add($"variation hash {s_Hash} ('{s_Name}') is not in pack.sb — the entry would not be reachable.");
        }

        foreach (var s_Db in p_Databases)
            if (!Named(s_PackBytes, s_Db))
                p_Result.Problems.Add($"database '{s_Db}' is not in pack.sb.");

        var s_Authored = Regex.Matches(p_Log, @"Variation: hash=(\d+)").Select(p_M => uint.Parse(p_M.Groups[1].Value)).ToList();
        if (s_Authored.Count != p_EntryCount)
            p_Result.Problems.Add($"{s_Authored.Count} entries authored, expected {p_EntryCount}.");

        var s_Set = Regex.Matches(p_Log, @"Set (\d+) texture parameter").Select(p_M => int.Parse(p_M.Groups[1].Value)).ToList();
        if (p_ValuesOnly)
        {
            // Nothing is bound on purpose: the entries keep what they bind — except the ones of a weapon that wears a copy of the
            // package's own shader and binds no camo of its own (p_ValuesOnlyBound: the preset's default pattern, as previewed).
            var s_BoundCount = s_Set.Count(p_N => p_N > 0);
            if (s_BoundCount != p_ValuesOnlyBound)
                p_Result.Problems.Add($"a values-only package bound a camo texture on {s_BoundCount} entries, expected {p_ValuesOnlyBound}.");
        }
        else if (s_Set.Count != p_EntryCount || s_Set.Any(p_N => p_N == 0))
            p_Result.Problems.Add($"the camo texture was bound on {s_Set.Count(p_N => p_N > 0)} of {p_EntryCount} entries.");

        // Every texture the copied entries bind must travel, or the weapon's other skins draw white. The
        // build prints one MVDB-TEX line per binding; each named texture has to be one we referenced or ours.
        var s_Bound = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match s_Match in Regex.Matches(p_Log, @"MVDB-TEX:[^\n]*"))
            foreach (Match s_Path in Regex.Matches(s_Match.Value, @"[A-Za-z0-9_][A-Za-z0-9_ .\-]*(?:/[A-Za-z0-9_ .\-]+)+"))
                s_Bound.Add(s_Path.Value.Trim());

        var s_Shipped = new HashSet<string>(p_Referenced, StringComparer.OrdinalIgnoreCase);
        if (p_PatternName != null)
            s_Shipped.Add(p_PatternName);
        foreach (var s_Layer in p_StickerNames)
            s_Shipped.Add(s_Layer);

        // What the command referenced on its own ("MVDB-TEX-REF: <name>") travels too.
        foreach (Match s_Match in Regex.Matches(p_Log, @"MVDB-TEX-REF:\s*([^\r\n]+)"))
            s_Shipped.Add(s_Match.Groups[1].Value.Trim());

        // And what it left to the levels ("MVDB-TEX-LEVEL: <name> (…)"): a texture with no catalog copy lives only inside the
        // levels that use it — those levels bring it, and a package never carries the game's own bytes.
        foreach (Match s_Match in Regex.Matches(p_Log, @"MVDB-TEX-LEVEL:\s*([^(\r\n]+)"))
            s_Shipped.Add(s_Match.Groups[1].Value.Trim());

        // ⛔ …but only a level bundle loaded BEFORE can bring it. A VEHICLE's own bundle loads right behind the level bundles that carry
        // it (its mesh, textures and LOD group are there: measured 2026-09-24), so what a vehicle leaves to the level is found; packv
        // (weapons, accessories) loads right behind the level's own bundle, where a mode's art is not yet (run 147: the client died on
        // a piece whose LOD group was left to a mode bundle). So anything else left to the level is refused, by name.
        var s_CloneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match s_Match in Regex.Matches(p_Log, @"Renamed MeshSet '([^']+)' \([^)]*\) -> '([^']+)'"))
            s_CloneOf[s_Match.Groups[2].Value] = s_Match.Groups[1].Value;
        var s_LeftOutside = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match s_Match in Regex.Matches(p_Log, @"MVDB-TEX-LEVEL:\s*(\S+)"))
            if (!s_Match.Groups[1].Value.StartsWith("vehicles/", StringComparison.OrdinalIgnoreCase))
                s_LeftOutside.Add(s_Match.Groups[1].Value);
        foreach (Match s_Match in Regex.Matches(p_Log, @"Left to the level[^\n]*for resource (\S+) \("))
            if (!(s_CloneOf.TryGetValue(s_Match.Groups[1].Value, out var s_Source) && s_Source.StartsWith("vehicles/", StringComparison.OrdinalIgnoreCase)))
                s_LeftOutside.Add(s_Match.Groups[1].Value);
        foreach (var s_Left in s_LeftOutside)
        {
            p_Result.Problems.Add($"'{s_Left}' has no catalog copy and is not a vehicle's (whose own bundle loads behind the level bundle " +
                                  "that carries it) — nothing loaded before the package would bring it.");

            // the game mesh a clone was cut from ("Renamed MeshSet '<game>' … -> '<clone>'"): what a second bake leaves out
            if (s_CloneOf.TryGetValue(s_Left, out var s_Game) && s_Game.StartsWith("weapons/", StringComparison.OrdinalIgnoreCase))
                p_Result.LevelOnlyMeshes.Add(s_Game);
        }

        foreach (var s_Texture in s_Bound.Where(p_T => !s_Shipped.Contains(p_T) && !p_T.EndsWith("meshvariationdb_win32", StringComparison.OrdinalIgnoreCase) && !p_T.Contains("_mesh", StringComparison.OrdinalIgnoreCase)))
            p_Result.Problems.Add($"the entries bind '{s_Texture}', which the package neither ships nor references — it would draw white.");

        if (p_PatternName != null)
        {
            var s_Nc = Path.Combine(p_PackageDir, "packnc.sb");
            if (!File.Exists(s_Nc) || !Named(File.ReadAllBytes(s_Nc), p_PatternName))
                p_Result.Problems.Add($"the pattern texture '{p_PatternName}' is not in packnc.sb.");

            // A generated texture announces itself through check_textures ("TEX <name> WxH … fmt=N mips=M"),
            // not through an "Added resource" line. fmt 0 is DXT1, the lane the game's own patterns use.
            var s_Tex = Regex.Match(p_Log, @"TEX\s+" + Regex.Escape(p_PatternName.ToLowerInvariant()) + @"\s+(\d+)x(\d+)[^\n]*fmt=(\d+)[^\n]*mips=(\d+)");
            if (!s_Tex.Success)
                p_Result.Problems.Add($"check_textures did not list '{p_PatternName}' — the texture has a name but no resource.");
            else if (s_Tex.Groups[3].Value != "0" || s_Tex.Groups[1].Value != "512")
                p_Result.Problems.Add($"'{p_PatternName}' came out as fmt={s_Tex.Groups[3].Value} {s_Tex.Groups[1].Value}x{s_Tex.Groups[2].Value}, expected DXT1 512x512.");

            if (Regex.IsMatch(p_Log, @"TEX-BAD"))
                p_Result.Problems.Add("check_textures marked a texture TEX-BAD (see build.log).");
        }

        if (p_ThumbName != null)
        {
            var s_Chunks = Path.Combine(p_PackageDir, "chunks.sb");
            if (!File.Exists(s_Chunks) || new FileInfo(s_Chunks).Length < 1024)
                p_Result.Problems.Add("chunks.sb is missing or empty — the thumbnail would have no pixels.");
        }
    }

    private static bool Contains(byte[] p_Haystack, byte[] p_Needle)
    {
        if (p_Needle.Length == 0 || p_Haystack.Length < p_Needle.Length)
            return false;

        var s_First = p_Needle[0];
        for (var i = 0; i <= p_Haystack.Length - p_Needle.Length; i++)
        {
            if (p_Haystack[i] != s_First)
                continue;

            var s_Match = true;
            for (var j = 1; j < p_Needle.Length && s_Match; j++)
                s_Match = p_Haystack[i + j] == p_Needle[j];

            if (s_Match)
                return true;
        }

        return false;
    }

    private static uint Fnv1A(string p_Text)
    {
        var s_Hash = 2166136261u;
        foreach (var s_Char in p_Text.ToLowerInvariant())
            s_Hash = unchecked((s_Hash ^ s_Char) * 16777619u);

        return s_Hash;
    }

    /// <summary>The cache's own file naming, so the native camo's thumbnail source is found where the preview keeps it.</summary>
    private static string Sanitize(string p_Text)
    {
        var s_Builder = new StringBuilder();
        foreach (var s_Char in p_Text)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

        return s_Builder.ToString().Trim('_');
    }
}
