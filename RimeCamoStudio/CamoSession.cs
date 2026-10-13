using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using RimeCamoStudio.Catalog;
using RimeCamoStudio.Mod;
using RimeCamoStudio.Project;

namespace RimeCamoStudio;

/// <summary>
/// Everything the studio adds on top of the shader editor: the camos being authored, the borrowed UI rows
/// they will wear, and the bake that turns them into a mod.
///
/// ⛔ IT IS NOT A WINDOW, and that is the point (the requirement: no new window, everything happens inside
/// Camo Studio). The studio IS the shader graph with the shader-only parts removed, so
/// there is one window; this holds the state that window does not know about.
/// </summary>
public sealed class CamoSession
{
    private readonly WeaponCatalog m_Weapons;
    private readonly HookPool m_Hooks;
    private readonly AccessoryCatalog m_Accessories;
    private readonly VehicleCatalog m_Vehicles;
    private readonly SoldierCatalog m_Soldiers;
    private readonly CamoProject m_Project = new();

    public CamoSession(WeaponCatalog p_Weapons, HookPool p_Hooks, AccessoryCatalog? p_Accessories = null,
        VehicleCatalog? p_Vehicles = null, SoldierCatalog? p_Soldiers = null)
    {
        m_Weapons = p_Weapons;
        m_Hooks = p_Hooks;

        // The accessory rows ship beside the weapons; a build without them (an older Data folder) still
        // opens the studio, only with no accessory picker — and says so once, in the Output.
        m_Accessories = p_Accessories ?? (File.Exists(AccessoryCatalog.DefaultPath)
            ? AccessoryCatalog.Load()
            : AccessoryCatalog.Empty);

        // Same rule for the vehicles: no catalogue, no Vehicles tab, and the rest of the studio unchanged.
        m_Vehicles = p_Vehicles ?? (File.Exists(VehicleCatalog.DefaultPath)
            ? VehicleCatalog.Load()
            : VehicleCatalog.Empty);

        // …and for the soldiers: no catalogue, the Soldiers tab stays disabled and says why.
        m_Soldiers = p_Soldiers ?? SoldierCatalog.Load();
    }

    /// <summary>The vehicles the Vehicles tab lists, and what their gadget parts are.</summary>
    public VehicleCatalog Vehicles => m_Vehicles;

    /// <summary>The sixteen soldiers the Soldiers tab lists (2 teams × 4 classes × base / Aftermath).</summary>
    public SoldierCatalog Soldiers => m_Soldiers;

    /// <summary>
    /// The soldier on screen: the one whose torso was picked last. A mesh two soldiers wear (the US and RU assault share their legs)
    /// is drawn in THIS one's variation — US_LB03_Desert around the US torso, RU_LB03_Desert around the RU one.
    /// </summary>
    private SoldierEntry? m_ActiveSoldier;

    /// <summary>
    /// The variation a mesh is previewed in: 0 — the mesh's own entry — for a weapon or a vehicle; for a soldier's mesh the variation
    /// his default appearance puts on it (the torso of US · Assault is US_Upperbody04_Desert). ⛔ A soldier's torso has NO entry 0 at
    /// all (measured 2026-09-28: us_upperbody04 is bound only per variation, 19 of them), so the weapons' "variation 0" read found
    /// nothing and would have drawn the character without a single texture.
    /// </summary>
    public uint PreviewVariationOf(string p_Mesh, string? p_NativeCamo = null)
    {
        if (m_Soldiers.Soldiers.Count == 0)
            return 0;

        static SoldierItem? Worn(SoldierEntry? p_Soldier, string p_Mesh) =>
            p_Soldier?.Items.FirstOrDefault(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

        var s_Soldier = Worn(m_ActiveSoldier, p_Mesh) != null
            ? m_ActiveSoldier
            : m_Soldiers.Soldiers.FirstOrDefault(p_S => Worn(p_S, p_Mesh) != null);
        var s_Item = Worn(s_Soldier, p_Mesh);

        // ⭐ one of his STOCK LOOKS as the part's starting point ("preview native camo" on a soldier, chosen per part: the
        // document's): the variation that look puts on this mesh — when the look puts the same meshes on the part (LookMisfit), else his own
        if (s_Soldier != null && s_Item != null && LookNameOf(p_NativeCamo) is { } s_Name && s_Soldier.LookNamed(s_Name) is { } s_Look &&
            s_Soldier.LookMisfit(s_Look, s_Item.Part) == null &&
            s_Look.Items.FirstOrDefault(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)) is { } s_Its)
            return s_Its.NameHash;

        return s_Item?.NameHash ?? 0;
    }

    /// <summary>A soldier's stock look as a key of the native-camo picker: "look:Ninja".</summary>
    public const string LookPrefix = "look:";

    /// <summary>The look a native-camo key names, or null for a weapon camo, the basic one, as shipped.</summary>
    public static string? LookNameOf(string? p_NativeCamo) =>
        p_NativeCamo != null && p_NativeCamo.StartsWith(LookPrefix, StringComparison.OrdinalIgnoreCase) ? p_NativeCamo[LookPrefix.Length..] : null;

    /// <summary>
    /// The looks the picker offers on each side (US / RU): "Default basic camo" and every look that is NOT the default of some soldier of
    /// that side — in the game's order. (The two sides name their XP2 looks apart: ABU / Partizan…; an Aftermath soldier's default is Wood01,
    /// which a base one offers.)
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<(string Key, string Display)>> SoldierNatives()
    {
        var s_Out = new Dictionary<string, IReadOnlyList<(string Key, string Display)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Team in m_Soldiers.Soldiers.Select(p_S => p_S.Team).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s_Looks = new List<SoldierLook>();
            foreach (var s_Soldier in m_Soldiers.Soldiers.Where(p_S => p_S.Team.Equals(s_Team, StringComparison.OrdinalIgnoreCase)))
            foreach (var s_Look in s_Soldier.Looks)
                if (!s_Look.Appearance.Equals(s_Soldier.Appearance, StringComparison.OrdinalIgnoreCase) &&
                    s_Looks.All(p_L => !p_L.Name.Equals(s_Look.Name, StringComparison.OrdinalIgnoreCase)))
                    s_Looks.Add(s_Look);

            s_Out[s_Team] = new[] { (BasicCamoKey, BasicCamoDisplay) }
                .Concat(s_Looks.Select(p_L => (LookPrefix + p_L.Name, p_L.Display))).ToList();
        }

        return s_Out;
    }

    /// <summary>
    /// What picking a native camo on a soldier's part CANNOT do, said when it is picked: the soldier on screen has no such look, or the look
    /// puts other meshes on the part (the part then stays on his own look, in the preview and in the bake). Null when it can, or not a look.
    /// </summary>
    public string? LookNoteFor(string p_Mesh, string? p_NativeCamo)
    {
        if (LookNameOf(p_NativeCamo) is not { } s_Name || m_Soldiers.Soldiers.Count == 0)
            return null;

        var s_Soldier = m_ActiveSoldier?.Items.Any(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)) == true
            ? m_ActiveSoldier
            : m_Soldiers.Soldiers.FirstOrDefault(p_S => p_S.Items.Any(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)));
        var s_Item = s_Soldier?.Items.FirstOrDefault(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));
        if (s_Soldier == null || s_Item == null)
            return null;

        if (s_Soldier.LookNamed(s_Name) is not { } s_Look)
            return $"{s_Soldier.Display} has no look '{s_Name.Replace('_', ' ')}' (it is another side's or another kit's) — his {PartDisplay(s_Item.Part)} " +
                   "stays on his own look.";

        return s_Soldier.LookMisfit(s_Look, s_Item.Part) is { } s_Why
            ? $"{s_Soldier.Display}, {PartDisplay(s_Item.Part)}: {s_Why} — it cannot be previewed or baked on this part, which stays on his own look."
            : null;
    }

    /// <summary>What a part button reads, by the catalogue's part: three plain words.</summary>
    public static string PartDisplay(string p_Part) => p_Part switch
    {
        "head" => "Head",
        "upper" => "Upper body",
        "lower" => "Lower body",
        _ => p_Part,
    };

    /// <summary>
    /// The picker entries under a soldier: his UPPER BODY first (the torso — what picking him shows, so "back to him" is the first entry
    /// the way "(weapon itself)" is), then his HEAD (the headgear: the helmet or cap, or on every RU and Aftermath soldier the one mesh
    /// that is face and headgear together) and his LOWER BODY, and his FIRST-PERSON arms under the Cockpit tag, so the window's Cockpit box
    /// ticks them (a first-person button, like the Cockpit box). Each is a SUBJECT of its own (its own document, its own
    /// materials), drawn with the rest of him around it — not an attachment: nothing of the attachments' own-preset rules applies to it.
    /// </summary>
    private IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> PartsOf(SoldierEntry p_Soldier)
    {
        m_ActiveSoldier = p_Soldier;
        var s_Entries = new List<RimeShaderEditor.MainWindow.CamoWeapon>();

        void Add(SoldierItem? p_Item, string p_Tag)
        {
            if (p_Item == null)
                return;

            var s_Word = p_Tag == RimeShaderEditor.MainWindow.CockpitTag ? "First person" : PartDisplay(p_Item.Part);
            s_Entries.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = $"{s_Word} · {p_Item.Mesh[(p_Item.Mesh.LastIndexOf('/') + 1)..]}",
                Mesh = p_Item.Mesh,
                Tag = p_Tag,
                // the button it stands under: the first-person arms belong to the upper body (the torso and the arms go
                // together)
                Part = PartDisplay(p_Item.Part),
            });
        }

        Add(p_Soldier.Items.FirstOrDefault(p_I => p_I.Role == "torso"), "SoldierUpper");
        Add(p_Soldier.Items.FirstOrDefault(p_I => p_I.Role == "headgear"), "SoldierHead");
        Add(p_Soldier.Items.FirstOrDefault(p_I => p_I.Role == "legs"), "SoldierLower");

        // ⭐ THE FIRST-PERSON MODEL IS TWO PARTS (the first-person body split as well): its sleeves, vest and
        // gloves are the upper body's, its TROUSERS the lower body's — in the game they already follow the legs (a second copy of the
        // arms mesh through the legs socket, tests 3/4). So it is offered under both buttons: the same mesh, each entry editing only its
        // own materials, the other ones drawn around them as they ship.
        if (p_Soldier.Items.FirstOrDefault(p_I => p_I.View == "1p") is { } s_Arms)
        {
            var s_Leaf = s_Arms.Mesh[(s_Arms.Mesh.LastIndexOf('/') + 1)..];
            var s_Trousers = TrousersMaterialsOf(s_Arms.Mesh);
            var s_Rest = SectionMaterialsOf(s_Arms.Mesh).Select(p_S => p_S.MaterialId)
                .Concat(MaterialShadersOf(s_Arms.Mesh).Keys)
                .Where(p_Id => p_Id >= 0 && !s_Trousers.Contains(p_Id))
                .Distinct()
                .OrderBy(p_Id => p_Id)
                .ToList();

            s_Entries.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = $"First person · {s_Leaf}",
                Mesh = s_Arms.Mesh,
                Tag = RimeShaderEditor.MainWindow.CockpitTag,
                Part = PartDisplay("upper"),
                DocumentKey = $"soldiers/{p_Soldier.Key}/upper",
                ContextMaterials = s_Trousers.OrderBy(p_Id => p_Id).ToList(),
            });

            // only when the trousers were measured: without them there is nothing of the lower body in the first-person model to edit
            if (s_Trousers.Count > 0)
                s_Entries.Add(new RimeShaderEditor.MainWindow.CamoWeapon
                {
                    Display = $"First person · trousers ({s_Leaf})",
                    Mesh = s_Arms.Mesh,
                    Tag = RimeShaderEditor.MainWindow.CockpitTag,
                    Part = PartDisplay("lower"),
                    DocumentKey = $"soldiers/{p_Soldier.Key}/lower",
                    ContextMaterials = s_Rest,
                });
        }

        return s_Entries;
    }

    /// <summary>
    /// The materials of a first-person arms mesh that are its TROUSERS: the ones that, in any of its variations, bind a diffuse of the
    /// LOWER BODY's (characters/lowerbody/…). Measured, not listed (2026-09-28): #1 on the US arms (ru_lowerbody04_c), #3 on both RU ones
    /// (us_lb03_d) — ⛔ by MESH, not by variation: the US arms' Aftermath variations bind "arms1plb_us_d" there, under characters/arms/,
    /// and the material list of a mesh is the same in all of them.
    /// </summary>
    public IReadOnlyCollection<int> TrousersMaterialsOf(string p_ArmsMesh) => Scan
        .Where(p_B => p_B.Mesh.Equals(p_ArmsMesh, StringComparison.OrdinalIgnoreCase) && p_B.MaterialId >= 0 &&
                      p_B.Textures.Any(p_T => p_T.Param.Equals("Diffuse", StringComparison.OrdinalIgnoreCase) &&
                                              p_T.Texture.Contains("/lowerbody/", StringComparison.OrdinalIgnoreCase)))
        .Select(p_B => p_B.MaterialId)
        .ToHashSet();

    /// <summary>The soldier on screen when he wears this mesh, else the first who does; null for a mesh no soldier wears.</summary>
    private SoldierEntry? SoldierWearing(string p_Mesh) =>
        m_ActiveSoldier != null && m_ActiveSoldier.AllMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase)
            ? m_ActiveSoldier
            : m_Soldiers.Soldiers.FirstOrDefault(p_S => p_S.AllMeshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The key a subject's DOCUMENT is kept under in a tab (MainWindow's "one document per subject"): the mesh for a weapon, an attachment or
    /// a vehicle — as it always was —, and for a soldier his PART: "soldiers/US_Assault/upper". ⛔ Not the mesh: the US and RU assault
    /// soldiers wear the SAME legs (us_lb03), and a skin is baked per soldier — editing one soldier's legs must not edit the other's; and
    /// the upper body is the torso AND the first-person arms together, one document for both meshes.
    /// </summary>
    public string DocumentKeyFor(string p_Mesh)
    {
        if (SoldierWearing(p_Mesh) is not { } s_Soldier ||
            s_Soldier.Items.FirstOrDefault(p_I => p_I.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)) is not { } s_Item)
            return p_Mesh;

        return $"soldiers/{s_Soldier.Key}/{s_Item.Part}";
    }

    /// <summary>
    /// The document one MATERIAL of a mesh is edited under — <see cref="DocumentKeyFor"/>, except the first-person model's trousers, which
    /// are the lower body's although the mesh stands under the upper body. What the rest of a soldier is drawn with, part by part.
    /// </summary>
    /// <summary>
    /// The cached picture of a stock look's pattern on one part — the CamoTile its variation binds on the part's first mesh (the torso for
    /// the upper body) — or null when the scan names none or it is not cached.
    /// </summary>
    internal string? LookPictureOf(SoldierEntry p_Soldier, SoldierLook p_Look, string p_Part)
    {
        foreach (var s_Item in p_Look.Items.Where(p_I => p_I.Part.Equals(p_Part, StringComparison.OrdinalIgnoreCase) && p_I.View != "1p"))
        {
            var s_Texture = Scan.FirstOrDefault(p_B => p_B.Mesh.Equals(s_Item.Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == s_Item.NameHash &&
                                                       p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
                ?.Textures.First(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)).Texture;
            if (s_Texture != null && CamoBaker.CachedPngOf(s_Texture) is { } s_Png && File.Exists(s_Png))
                return s_Png;
        }

        return null;
    }

    /// <summary>
    /// Whether a document key is a soldier's part — whose camo of the picker is its OWN (a stock look chosen per part: the
    /// torso on Ninja, the legs on Urban), not the tab's as a weapon's and its attachments' is.
    /// </summary>
    public static bool IsSoldierPartKey(string p_Key) => p_Key.StartsWith("soldiers/", StringComparison.OrdinalIgnoreCase);

    public string DocumentKeyForMaterial(string p_Mesh, int p_MaterialId)
    {
        if (SoldierWearing(p_Mesh) is { } s_Soldier && s_Soldier.ArmsMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) &&
            TrousersMaterialsOf(p_Mesh).Contains(p_MaterialId))
            return $"soldiers/{s_Soldier.Key}/lower";

        return DocumentKeyFor(p_Mesh);
    }

    // ---- ⭐ THE CAMO ON ALL THE CLOTH, PER PART, PREVIEWED (both: the warning and the preview) ---------------------------------------------
    /// <summary>
    /// The soldier parts ("soldiers/US_Support/lower") whose camo goes on ALL their cloth — the skin's white mask — in the preview AND at the
    /// bake: ONE state for the Soldiers panel's box and the bake dialog's choice (the dialog opens on it and writes its answer back).
    /// </summary>
    private readonly HashSet<string> m_AllCloth = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A texture name of the studio's own: the preview's white mask, its picture written into the texture cache (opaque white — the
    /// mask's alpha is where the camo goes).</summary>
    public const string WhiteMaskTexture = "RimeCamoStudio/WhiteMask";

    public bool AllClothOf(string p_PartKey) => m_AllCloth.Contains(p_PartKey);

    /// <summary>The Soldiers panel's box (and the bake dialog's answer): the part's camo on all its cloth, or where the game's mask puts it.</summary>
    public void SetAllCloth(string p_PartKey, bool p_On)
    {
        if (p_On && EnsureWhiteMaskPicture())
            m_AllCloth.Add(p_PartKey);
        else
            m_AllCloth.Remove(p_PartKey);
    }

    private static bool EnsureWhiteMaskPicture()
    {
        var s_Png = Path.Combine(Settings.TextureCache, Cache.GameCache.SanitizeName(WhiteMaskTexture) + ".png");
        if (File.Exists(s_Png))
            return true;

        try
        {
            Directory.CreateDirectory(Settings.TextureCache);
            const int c_Side = 64;
            var s_Pixels = new byte[c_Side * c_Side * 4];
            Array.Fill(s_Pixels, (byte) 255);
            var s_Image = System.Windows.Media.Imaging.BitmapSource.Create(c_Side, c_Side, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                s_Pixels, c_Side * 4);
            var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Image));
            using var s_File = File.Create(s_Png);
            s_Encoder.Save(s_File);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A soldier part's camo cloth as the preview wears it: its meshes' materials that bind CamoTile in the variation of the look it starts
    /// from (<paramref name="p_NativeCamo"/>), each with the Mask bound on it — the first-person arms' with the upper body, their trousers
    /// with the lower.
    /// </summary>
    private List<(string Mesh, int Material, string? Mask)> PartCloth(string p_PartKey, string? p_NativeCamo)
    {
        var s_Key = p_PartKey.Split('/');
        if (s_Key.Length != 3 || !IsSoldierPartKey(p_PartKey) ||
            m_Soldiers.Soldiers.FirstOrDefault(p_S => p_S.Key.Equals(s_Key[1], StringComparison.OrdinalIgnoreCase)) is not { } s_Soldier)
            return new List<(string, int, string?)>();

        var s_Part = s_Key[2];
        var s_Cloth = new List<(string Mesh, int Material, string? Mask)>();
        var s_Meshes = s_Soldier.MeshesOf(s_Part).Concat(s_Part != "head" && s_Soldier.ArmsMesh.Length > 0 ? new[] { s_Soldier.ArmsMesh } : Array.Empty<string>());
        foreach (var s_Mesh in s_Meshes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s_Arms = s_Mesh.Equals(s_Soldier.ArmsMesh, StringComparison.OrdinalIgnoreCase);
            var s_Trousers = s_Arms ? TrousersMaterialsOf(s_Mesh) : null;
            var s_Variation = PreviewVariationOf(s_Mesh, p_NativeCamo);
            foreach (var s_Binding in Scan.Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == s_Variation &&
                                                        p_B.MaterialId >= 0 && p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase))))
            {
                if (s_Arms && s_Trousers!.Contains(s_Binding.MaterialId) != (s_Part == "lower"))
                    continue;

                s_Cloth.Add((s_Mesh, s_Binding.MaterialId,
                    s_Binding.Textures.FirstOrDefault(p_T => p_T.Param.Equals("Mask", StringComparison.OrdinalIgnoreCase)).Texture));
            }
        }

        return s_Cloth.Distinct().ToList();
    }

    /// <summary>
    /// ⚠ Whether the game's mask of the look a part starts from puts the camo NOWHERE on its cloth (the case: the US support's default
    /// legs — `us_lowerbody04_m3`, an alpha of 0: "where the game puts it" shows the skin's pattern nowhere). False when any mask is
    /// unknown or lets some camo through.
    /// </summary>
    internal bool PartMaskHidesCamo(string p_PartKey, string? p_NativeCamo) => PartMeshesHidingCamo(p_PartKey, p_NativeCamo).Count > 0;

    /// <summary>
    /// The meshes of a part (by name — "us_support_lb_mesh", "the first-person trousers") whose cloth the game's masks of that look cover with
    /// NO camo at all. ⛔ PER MESH, not per part (the first seam, 2026-09-28): the US support's legs hide it everywhere while the lower body's
    /// first-person trousers show it — "the part" as a whole looked fine and no warning came.
    /// </summary>
    internal List<string> PartMeshesHidingCamo(string p_PartKey, string? p_NativeCamo)
    {
        var s_Arms = p_PartKey.Split('/') is { Length: 3 } s_Key
            ? m_Soldiers.Soldiers.FirstOrDefault(p_S => p_S.Key.Equals(s_Key[1], StringComparison.OrdinalIgnoreCase))?.ArmsMesh ?? ""
            : "";
        return PartCloth(p_PartKey, p_NativeCamo)
            .GroupBy(p_C => p_C.Mesh, StringComparer.OrdinalIgnoreCase)
            .Where(p_G => p_G.All(p_C => p_C.Mask != null) && CamoBaker.MasksHideCamo(p_G.Select(p_C => p_C.Mask!)))
            .Select(p_G => p_G.Key.Equals(s_Arms, StringComparison.OrdinalIgnoreCase)
                ? (p_PartKey.EndsWith("/lower", StringComparison.OrdinalIgnoreCase) ? "the first-person trousers" : "the first-person sleeves")
                : p_G.Key.Split('/')[^1])
            .ToList();
    }

    /// <summary>
    /// The Soldiers panel's "camo on all the cloth" box for the part on screen (MainWindow.SetCamoPartOption): ticked or not — null where
    /// the part has no camo cloth as he wears it in that look — and what is said under it.
    /// </summary>
    public (bool? On, string? Note) PartOption(string p_PartKey, string? p_NativeCamo)
    {
        if (!IsSoldierPartKey(p_PartKey) || PartCloth(p_PartKey, p_NativeCamo).Count == 0)
            return (null, null);

        var s_On = m_AllCloth.Contains(p_PartKey);
        var s_Hidden = PartMeshesHidingCamo(p_PartKey, p_NativeCamo);
        var s_Where = string.Join(", ", s_Hidden);
        return (s_On, s_Hidden.Count == 0 ? null
            : s_On ? $"The game's mask of this look puts the camo nowhere on {s_Where} — here it goes on all the cloth, as it will be baked."
            : $"⚠ The game's mask of this look puts the camo NOWHERE on {s_Where} (its alpha is empty): the skin's pattern will not show " +
              "there — tick this box, or put a picture on the Mask node.");
    }

    /// <summary>The picker entries of the Soldiers tab: each soldier by name, standing on his torso, under his team.</summary>
    internal IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> PickerSoldiers => m_Soldiers.Soldiers
        .Where(p_S => p_S.PreviewMesh.Length > 0)
        .Select(p_S => new RimeShaderEditor.MainWindow.CamoWeapon
        {
            Display = p_S.Display,
            Mesh = p_S.PreviewMesh,
            Group = p_S.Team,
        })
        .ToList();

    /// <summary>The accessory rows of every weapon — what the second picker lists under the weapon.</summary>
    public AccessoryCatalog Accessories => m_Accessories;

    /// <summary>
    /// The "no optic" placeholder of the OPTIC row, shared by all 60 weapons (Weapons/Common/NoOptics).
    ///
    /// ⛔ IT IS NOT AN ATTACHMENT AND IT IS NOT OFFERED AS ONE (the request: drop NoOptics from every
    /// weapon's OPTIC row, and bake it along with the weapon's camo, since in the end it is part of the
    /// same piece). What hangs from it are the weapon's IRON SIGHTS — measured: 29 of the 60 weapons carry a sight mesh here
    /// (the M416's own m416_sight, the M4A1 and M16A4's m16_ironsight, the F2000's f2000_sight…) and the
    /// other 31 have no mesh on it at all. They are part of the gun, so they take the gun's camo and ride in
    /// its package always, not as something to pick and paint on its own.
    /// </summary>
    public const string IronSightsTag = "NoOptics";

    /// <summary>
    /// The iron-sight meshes of a weapon — what hangs from that placeholder — or nothing when it has none
    /// (measured: 29 of the 60 weapons carry one or two, the other 31 none). Used to paint them WITH the
    /// weapon and to say so where the user picks what to paint.
    /// </summary>
    public IReadOnlyList<string> IronSightMeshesOf(string p_Folder) =>
        (IReadOnlyList<string>?) m_Accessories.Of(p_Folder)?.Unlocks
            .FirstOrDefault(p_U => p_U.ShortName.Equals(IronSightsTag, StringComparison.OrdinalIgnoreCase))
            ?.Meshes ?? Array.Empty<string>();

    /// <summary>
    /// The picker entries under a weapon: the weapon itself first, then every unlock of its three rows in
    /// the game's order, labelled "ROW · name". An unlock that changes nothing visible (heavy barrel, the
    /// "No…" placeholders) is listed with an empty mesh and said so in its label: the user must see that it
    /// is there and that there is nothing to paint on it, not wonder why it is missing.
    ///
    /// ⭐ THE IRON SIGHTS ARE PICKED LIKE ANY OTHER PIECE (once their window opened in the game, the other
    /// half was done in Camo Studio so camos can be baked for the iron sights). They are listed under their
    /// own name and not as "NoOptics", which is what the game calls the ROW when no scope is on it, not what
    /// the player sees on the gun. They still ship with every bake of the weapon whether or not they are
    /// picked here: picking them is for AUTHORING their look.
    /// </summary>
    internal IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> AccessoriesOf(string p_WeaponMesh)
    {
        var s_Items = new List<RimeShaderEditor.MainWindow.CamoWeapon>();

        // ⭐ THE SAME CALLBACK ANSWERS FOR BOTH FAMILIES, and that is deliberate: the window asks "what
        // hangs off the thing on screen" and the studio is the side that knows whether that thing is a
        // rifle or a tank. A second callback per family is how the two halves drift.
        if (m_Vehicles.VehicleOfBody(p_WeaponMesh) is { } s_Vehicle)
            return GadgetsOf(s_Vehicle, p_WeaponMesh);

        // A soldier: his PARTS (head, upper body, lower body — each one ties the editing to that part alone).
        if (m_Soldiers.OfPreviewMesh(p_WeaponMesh) is { } s_Soldier)
            return PartsOf(s_Soldier);

        var s_Weapon = m_Weapons.Weapons.FirstOrDefault(p_W =>
            p_W.PreviewMesh != null && p_W.PreviewMesh.Equals(p_WeaponMesh, StringComparison.OrdinalIgnoreCase));
        if (s_Weapon == null)
            return s_Items;

        var s_Rows = m_Accessories.Of(s_Weapon.Folder);
        s_Items.Add(new RimeShaderEditor.MainWindow.CamoWeapon
        {
            Display = $"({s_Weapon.Folder} itself)",
            Mesh = p_WeaponMesh,
        });

        if (s_Rows == null)
            return s_Items;

        foreach (var s_Row in s_Rows.Rows)
        foreach (var s_Unlock in s_Row.Unlocks)
        {
            var s_Mesh = PreviewMeshOf(s_Unlock);

            // "NoOptics" is what the game calls the ROW with no scope on it; what hangs there is the weapon's
            // own iron sights, and that is the name to pick. Where the weapon has none, the entry says so the
            // way every other empty one does.
            var s_Name = s_Unlock.ShortName.Equals(IronSightsTag, StringComparison.OrdinalIgnoreCase)
                ? "iron sights"
                : s_Unlock.ShortName;

            s_Items.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = s_Mesh == null
                    ? $"{s_Row.Name} · {s_Name}  (no visible model)"
                    : $"{s_Row.Name} · {s_Name}",
                Mesh = s_Mesh ?? "",
                Weapon = s_Weapon.Folder,
                Tag = s_Unlock.ShortName,
            });
        }

        return s_Items;
    }

    /// <summary>
    /// The picker entries under a VEHICLE: the vehicle itself first, then the parts its kit unlocks put on
    /// it — which is the same shape as a weapon and its attachments (two parts: the whole vehicle, then
    /// its gadgets — basically the same approach as weapons and attachments).
    ///
    /// ⛔ MEASURED, AND IT IS A SHORT LIST: of the 21 vehicles only THREE have a kitunlock folder with a
    /// mesh in it (the T-90's antennas, the BMP-2's, the Sprut-SD's). The other gadgets are numbers —
    /// reload rates, optics, stealth — with nothing to paint. That is not a gap in the catalogue, it is
    /// what the game ships, and it is the same rule the in-game screen follows: no mesh, no window.
    /// </summary>
    private IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> GadgetsOf(VehicleEntry p_Vehicle,
        string p_BodyMesh)
    {
        var s_Items = new List<RimeShaderEditor.MainWindow.CamoWeapon>
        {
            new()
            {
                Display = $"({p_Vehicle.Display} itself)",
                Mesh = p_BodyMesh,
            },
        };

        // ⭐ THE REACTIVE ARMOUR IS A PIECE, NOT PART OF THE HULL (the reactive armour is an attachment:
        // its mesh split off and offered as an attachment on every vehicle that has it). It has no mesh of
        // its own — it lives inside the hull's dump as whole sections and/or parts of the kit section — so
        // its entry carries the BODY mesh and is told apart by its tag; the window isolates it instead of
        // loading something else, and leaves it off the body otherwise.
        if (ReactiveArmourOf(p_BodyMesh) is { } s_Armour)
            s_Items.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = $"{ReactiveArmour.Display}  ({s_Armour.Triangles} tri)",
                Mesh = p_BodyMesh,
                Weapon = p_Vehicle.Folder,
                Tag = ReactiveArmour.Tag,
            });

        // ⭐ THE PILOT'S COCKPIT (air vehicles have two different cockpit meshes: one while flying, another
        // when a player sees it from outside): the model the
        // game draws around the pilot is a mesh of its own, and it is picked here like any piece — with ITS
        // materials on the panel and ITS shaders on the canvas. The window's Cockpit box ticks this entry.
        if (p_Vehicle.CockpitMesh.Length > 0)
            s_Items.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = $"cockpit · {p_Vehicle.CockpitMesh[(p_Vehicle.CockpitMesh.LastIndexOf('/') + 1)..]}",
                Mesh = p_Vehicle.CockpitMesh,
                Weapon = p_Vehicle.Folder,
                Tag = RimeShaderEditor.MainWindow.CockpitTag,
            });

        foreach (var s_Gadget in p_Vehicle.GadgetMeshes)
        {
            // A gadget takes the camo only where one of its materials wears a preset — the same test the
            // attachments get, and the one the in-game window will make before it opens.
            var s_Paints = SectionMaterialsOf(s_Gadget).Any(p_S => IsCamoPreset(p_S.Shader)) ||
                           Scan.Any(p_B => p_B.Mesh.Equals(s_Gadget, StringComparison.OrdinalIgnoreCase) &&
                                           IsCamoPreset(p_B.Shader));

            var s_Leaf = s_Gadget[(s_Gadget.LastIndexOf('/') + 1)..];
            s_Items.Add(new RimeShaderEditor.MainWindow.CamoWeapon
            {
                Display = s_Paints ? $"gadget · {s_Leaf}" : $"gadget · {s_Leaf}  (no visible model)",
                Mesh = s_Paints ? s_Gadget : "",
                Weapon = p_Vehicle.Folder,
                Tag = s_Leaf,
            });
        }

        return s_Items;
    }

    /// <summary>
    /// The tabs above the subject picker — what a camo can be looked at on.
    ///
    /// Weapons is the studio as it was. Vehicles is the same window with the vehicle catalogue behind it
    /// and NO native-camo picker, because BF3 ships no vehicle camo to start from (so for now they keep
    /// 'as shipped'; templates may come later). Soldiers is on the row and disabled: the button was asked
    /// for before the work, so it has to look like what it is — next, not broken.
    /// </summary>
    internal IReadOnlyList<RimeShaderEditor.MainWindow.CamoFamily> Families => new[]
    {
        new RimeShaderEditor.MainWindow.CamoFamily
        {
            Key = "weapons",
            Display = "Weapons",
            Tooltip = "Put the camo on a weapon and its attachments.",
            SubjectTitle = "Preview weapon",
            SubjectWord = "weapon",
            SubjectHelp = "The weapon the camo is looked at on. Every weapon is cached — picking one is instant.",
            AccessoryTitle = "Preview accessory",
            AccessoryHelp = "The weapon itself, or one of the attachments its customization rows offer. " +
                            "An attachment is shown alone; an entry marked '(no visible model)' has nothing to paint.",
            Subjects = PickerWeapons,
            NativeCamos = new[] { (BasicCamoKey, BasicCamoDisplay) }
                .Concat(NativeCamos.Select(p_C => (p_C.Key, p_C.Display))).ToList(),
        },
        // ⭐ THE SOLDIERS (Soldiers → US / RU buttons → the 8 models): each of the sixteen stands on his TORSO
        // (the subject a skin is authored on) and the rest of him — helmet, head, legs — is drawn around it as it ships (ExtraMeshesFor),
        // so the preview is the whole character with his head.
        new RimeShaderEditor.MainWindow.CamoFamily
        {
            Key = "soldiers",
            Display = "Soldiers",
            Tooltip = "Put a skin on a soldier: his head gear, upper body and lower body.",
            SubjectTitle = "Preview soldier",
            SubjectWord = "soldier",
            SubjectHelp = "Each class is its own model; Aftermath's are others again.",
            AccessoryTitle = "Preview part",
            AccessoryHelp = "The part the skin is edited on — each keeps its own graph.",
            PartOrder = new[] { PartDisplay("head"), PartDisplay("upper"), PartDisplay("lower") },
            // a character's foreign materials (skin, eyes, teeth) often bind no AO or dirt layer: neutral there, never the camo's art
            NeutralForeignSlots = true,
            CockpitLabel = "First person (the arms and trousers the player sees of himself)",
            CockpitTooltip = "Show the first-person model the game draws for the player himself — it is part of the upper body. " +
                             "Untick to go back to the soldier seen from outside.",
            Subjects = PickerSoldiers,
            Groups = new[] { ("US", "US"), ("RU", "RU") },
            NativeCamos = new[] { (BasicCamoKey, BasicCamoDisplay) },
            // ⭐ his STOCK LOOKS as the part's starting point (as with the weapons, "preview native camo" on soldiers too) — per
            // side: each side names its own
            GroupNativeCamos = SoldierNatives(),
            Unavailable = m_Soldiers.Soldiers.Count > 0
                ? null
                : "No soldier catalogue beside the executable (Data/soldiers.json).",
        },
        new RimeShaderEditor.MainWindow.CamoFamily
        {
            Key = "vehicles",
            Display = "Vehicles",
            Tooltip = "Put the camo on a vehicle and the parts its kit unlocks add to it.",
            SubjectTitle = "Preview vehicle",
            SubjectWord = "vehicle",
            SubjectHelp = "The vehicle the camo is looked at on, under the row the game offers it in — a camo " +
                          "is painted on a MODEL, and one row fields a different one per side (MBT: M1A2 and T-90). " +
                          "An entry marked '(no camo-bearing material)' wears shaders of its own and takes none.",
            AccessoryTitle = "Preview accessory",
            AccessoryHelp = "The vehicle itself, or one of the parts its kit unlocks put on it. Measured: only " +
                            "three vehicles in the game have one (the T-90's antennas, the BMP-2's, the " +
                            "Sprut-SD's) — the rest of the gadgets change numbers, not geometry.",
            Subjects = PickerVehicles,

            // ⛔⛔ THE PICKER IS NOT "THE LIST OF GAME CAMOS": IT IS THE DOOR OUT OF "AS SHIPPED", and
            // leaving it out locked the whole family in a read-only preview. The note that used to stand here
            // said BF3 ships no vehicle camo so the list "would hold as shipped and nothing else, a control
            // that cannot be used" — true about the camos, WRONG about the control. While a subject is shown
            // as shipped the 3D draws the GAME'S bytecode per section, so nothing the user authors appears;
            // on a weapon the user steps out of it by picking a camo here, and a vehicle had no way at all
            // (the report: every pin disconnected and still no change). Measured: breaking the six
            // root wires left the frame BYTE-IDENTICAL as shipped and changed it the moment a camo was on.
            // Two entries — as shipped, and the basic camo to author from — are exactly what a weapon has
            // minus the game's eight. Vehicle templates, once made, land in this same list.
            NativeCamos = new[] { (BasicCamoKey, BasicCamoDisplay) },
            Unavailable = m_Vehicles.Vehicles.Count > 0
                ? null
                : "No vehicle catalogue beside the executable (Data/vehicles.json).",
        },
    };

    /// <summary>The vehicles the Vehicles tab lists, labelled the way a player names them, with their row.</summary>
    internal IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> PickerVehicles => m_Vehicles.Vehicles
        .Where(p_V => p_V.PreviewMesh.Length > 0)
        .Select(p_V => new RimeShaderEditor.MainWindow.CamoWeapon
        {
            // The category is part of the name here, not a separate control: a camo is authored on a
            // MODEL (the MBT row is an M1A2 on one side and a T-90 on the other), and the row it belongs
            // to is what tells the user which of the two they are looking at.
            // A vehicle whose body wears only shaders of its own (the F-35B) opens as shipped; the label says why
            // there is no camo slot to fill until a preset is made for it.
            Display = p_V.TakesCamo
                ? $"{p_V.CategoryDisplay} · {p_V.Display}"
                : $"{p_V.CategoryDisplay} · {p_V.Display}  (own shaders - as shipped)",
            Mesh = p_V.PreviewMesh,
        })
        .ToList();

    /// <summary>
    /// The mesh an accessory unlock is previewed on: the first of its meshes, in socket order, that the
    /// material scan covers — i.e. that wears a weapon preset and can take a camo. A reticle or a beam is
    /// a mesh too, and for some optics it is the FIRST object of the socket; previewing that would show a
    /// quad. Null when none of its meshes wears a preset (nothing to paint).
    /// </summary>
    public string? PreviewMeshOf(AccessoryUnlock p_Unlock)
    {
        foreach (var s_Socket in p_Unlock.Sockets)
        foreach (var s_Object in s_Socket.Objects)
        foreach (var (_, s_Asset) in s_Object.Assets)
            if (s_Asset.IsMesh && Scan.Any(p_B => p_B.Mesh.Equals(s_Asset.Name, StringComparison.OrdinalIgnoreCase)))
                return s_Asset.Name;

        return null;
    }

    /// <summary>The weapon (its preview mesh) an accessory mesh belongs to, or null for a weapon body / unknown mesh.</summary>
    public string? WeaponOfAccessory(string p_Mesh)
    {
        // A gadget belongs to its vehicle exactly as an attachment belongs to its weapon: the window uses
        // this to know what the piece on screen hangs from.
        if (m_Vehicles.VehicleOfGadget(p_Mesh) is { } s_Vehicle)
            return s_Vehicle.PreviewMesh;

        // The interior cockpit hangs from its aircraft the same way.
        if (m_Vehicles.VehicleOfCockpit(p_Mesh) is { } s_Aircraft)
            return s_Aircraft.PreviewMesh;

        foreach (var s_Weapon in m_Weapons.Weapons)
        {
            if (s_Weapon.PreviewMesh == null || m_Accessories.Of(s_Weapon.Folder) is not { } s_Rows)
                continue;

            if (s_Rows.Unlocks.Any(p_U => p_U.Meshes.Contains(p_Mesh, StringComparer.OrdinalIgnoreCase)))
                return s_Weapon.PreviewMesh;
        }

        return null;
    }

    /// <summary>Whether a mesh is an accessory of some weapon — authored and baked with the accessory preset.</summary>
    public bool IsAccessoryMesh(string p_Mesh) => m_Accessories.IsAccessoryMesh(p_Mesh);

    /// <summary>
    /// The piece the bake dialog would be asked about for an attachment picked under a weapon: what it is, how
    /// many of its meshes a camo can go on, and EVERY weapon that carries the same piece. Null when the pair
    /// names no attachment, or when nothing on it can be painted (a beam, a reticle, an unlock with no model)
    /// — the camo is then a weapon's, and the dialog asks the long form.
    ///
    /// ⛔ The carriers are found by MESH and their tags are read one by one, never copied from the host: the
    /// same suppressor is "Silencer" on one gun and "Sound_Suppressor" on the next (33 weapons, measured
    /// 2026-09-20 with --attachmentpreset), and a twin is created under the tag ITS weapon uses.
    /// </summary>
    public View.BakePiece? PieceForBake(string p_Folder, string p_Tag)
    {
        if (p_Folder.Trim().Length == 0 || p_Tag.Trim().Length == 0)
            return null;

        var s_Unlock = m_Accessories.Of(p_Folder)?.Unlocks.FirstOrDefault(p_U =>
            p_U.ShortName.Equals(p_Tag, StringComparison.OrdinalIgnoreCase));
        if (s_Unlock == null)
            return null;

        var s_Meshes = s_Unlock.Meshes
            .Where(p_M => SectionMaterialsOf(p_M).Any(p_S => IsCamoPreset(p_S.Shader)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (s_Meshes.Count == 0)
            return null;

        var s_Host = $"{p_Folder}/{s_Unlock.ShortName}";
        var s_Carriers = new List<string> { s_Host };
        foreach (var s_Weapon in m_Weapons.Weapons)
        {
            if (s_Weapon.Folder.Equals(p_Folder, StringComparison.OrdinalIgnoreCase))
                continue;

            var s_Theirs = m_Accessories.Of(s_Weapon.Folder)?.Unlocks.FirstOrDefault(p_U =>
                p_U.Meshes.Any(p_M => s_Meshes.Contains(p_M, StringComparer.OrdinalIgnoreCase)));

            if (s_Theirs != null)
                s_Carriers.Add($"{s_Weapon.Folder}/{s_Theirs.ShortName}");
        }

        return new View.BakePiece
        {
            Weapon = p_Folder,
            // "NoOptics" is the name of the ROW with no scope on it; what hangs there is the weapon's own
            // iron sights, which is what the user picked and what the dialog must say.
            Display = s_Unlock.ShortName.Equals(IronSightsTag, StringComparison.OrdinalIgnoreCase)
                ? "iron sights"
                : s_Unlock.ShortName,
            Host = s_Host,
            Carriers = s_Carriers,
            Meshes = s_Meshes.Count,
        };
    }

    /// <summary>The meshes the editor offers under "Preview mesh" — one per weapon, first person.</summary>
    public IReadOnlyList<string> PreviewMeshes => m_Weapons.Weapons
        .Select(p_W => p_W.PreviewMesh)
        .Where(p_M => p_M != null)
        .Select(p_M => p_M!)
        .ToList();

    /// <summary>
    /// The same weapons, labelled the way a player names them.
    ///
    /// ⛔ NOT THE FILE NAMES. "acr_1p_mesh" is how the asset is stored, not what the weapon is called, and a
    /// list of those asks the user to translate before choosing. The catalogue's folder is the weapon's own
    /// name (not the literal file names, but the name each one should have).
    /// </summary>
    internal IReadOnlyList<RimeShaderEditor.MainWindow.CamoWeapon> PickerWeapons => m_Weapons.Weapons
        .Where(p_W => p_W.PreviewMesh != null)
        .Select(p_W => new RimeShaderEditor.MainWindow.CamoWeapon
        {
            Display = p_W.Folder,
            Mesh = p_W.PreviewMesh!,
        })
        .ToList();

    /// <summary>
    /// The camo the window is currently editing, created on demand from the graph's own name. The editor
    /// already has a name for the document being worked on, so asking for a second one would be asking the
    /// user to name the same thing twice.
    /// </summary>
    public Camo Current(string p_GraphName)
    {
        var s_Name = p_GraphName.Trim().Length > 0 ? p_GraphName.Trim() : "Untitled";
        var s_Camo = m_Project.Camos.FirstOrDefault(p_C =>
            string.Equals(p_C.Name, s_Name, StringComparison.OrdinalIgnoreCase));

        if (s_Camo == null)
        {
            s_Camo = new Camo { Name = s_Name };
            m_Project.Camos.Add(s_Camo);
        }

        // Derived from the NAME and written down, so a saved loadout keeps pointing at the same camo.
        // 2026-09-20: this used to take a SEAT from the pool -- an identifier of the game's. Nothing of ours
        // borrows anything any more: the number is the camo's own name, hashed the way the UI hashes it.
        s_Camo.Identifier = CamoBaker.IdentifierOfCamo(s_Camo.Name);

        return s_Camo;
    }

    /// <summary>
    /// The shader a camo is authored on for a given weapon mesh.
    ///
    /// ⛔ NOT SIMPLY "the shader the weapon wears" (the shader needed is the one that already has the basic
    /// camo built in). Most weapons wear a **NoCamo** preset, because
    /// their shipped state has no camo at all: the census measured `Camo` bound on 449 materials out of 796,
    /// and 41 of the 60 weapons carry no vanilla camo whatsoever. Opening that preset would hand the user a
    /// graph with NO camo slot to work with — which is also what the delivery recipe says, since it
    /// SUBSTITUTES the NoCamo preset for the one that samples Camo.
    ///
    /// So: read what the weapon wears from the cached scan (never guessed — there are six presets, and the
    /// bench script's two hard-coded guids only fit the M416), then map it onto its camo-capable twin.
    /// </summary>
    public string? ShaderForMesh(string p_Mesh)
    {
        // ⛔ AN ACCESSORY IS NOT AUTHORED ON WHAT IT WEARS. Its body wears weaponpresetfp (which has no camo
        // path at all — its graph reads no Camo texture) and nocamo3p on the far LODs; the twin rule below
        // would open the FP graph, with nothing to paint. The preset an accessory takes a camo with, in BOTH
        // views, is the third-person one (see AccessoryPreset) — so that is what opens, for every accessory.
        if (m_Accessories.IsAccessoryMesh(p_Mesh))
            return AccessoryPreset;

        // ⭐ A SOLDIER'S PIECE, like a vehicle, has no twin: what its sections wear IS the graph to open — the shader that draws the most
        // of it, read off the dump (the torso's CharacterRoot atlas, never its radio cable), the scan's before it is dumped.
        if (m_Soldiers.IsSoldierMesh(p_Mesh))
        {
            if (m_Soldiers.OfPreviewMesh(p_Mesh) is { } s_Soldier && SoldierWearing(p_Mesh) != s_Soldier)
                m_ActiveSoldier = s_Soldier;

            // ⛔ THE SHADER THAT WEARS THE CAMO FIRST, then the one that draws the most (2026-09-28): the first-person arms draw most of
            // their triangles with characterroot_1p (the gloves) while their sleeves and trousers — the camo, CamoTile — wear characterroot,
            // the torso's own; by triangles alone the upper body's two meshes opened two graphs in two tabs.
            var s_Shown = PreviewVariationOf(p_Mesh);
            var s_Camo = Scan
                .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == s_Shown &&
                              p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
                .Select(p_B => p_B.Shader)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Cache.GameCache.SectionTrianglesOf(p_Mesh)
                       .Where(p_S => p_S.Shader.Length > 0)
                       .GroupBy(p_S => p_S.Shader, StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(p_G => s_Camo.Contains(p_G.Key))
                       .ThenByDescending(p_G => p_G.Sum(p_S => p_S.Triangles))
                       .Select(p_G => p_G.Key)
                       .FirstOrDefault()
                   ?? Scan.Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
                       .Select(p_B => p_B.Shader)
                       .FirstOrDefault();
        }

        // ⛔ A VEHICLE HAS NO TWIN TO MAP ONTO, and that is the difference from a weapon. Weapons ship a
        // camo/no-camo PAIR per view, so the studio opens the camo-sampling twin of what the body wears;
        // the vehicle presets are one family, and their materials already feed CamoTiling, CamoBrightness
        // and the wear values (measured: vehiclepreset_mud takes CamoTiling on 32 of its 70 materials).
        // So what the section wears IS the graph to open — and the catalogue answers even before the mesh
        // has ever been dumped, which is what keeps the first pick from mounting the game.
        if (m_Vehicles.Of(p_Mesh) is { } s_Vehicle)
        {
            var s_Body = Cache.GameCache.SectionTrianglesOf(p_Mesh)
                .Where(p_S => VehicleCatalog.IsVehicleBodyPreset(p_S.Shader))
                .GroupBy(p_S => p_S.Shader, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p_G => p_G.Count())
                // ⭐ a tie of sections goes to the preset that draws MORE of the object (2026-09-26): the DPV has one section of the
                // base mud (its CROWS turret, 4,863 triangles) and one of XPack01's (its body, 15,039) — section order gave it the
                // turret's document, and the body came out of the preview without the pattern while the bake painted it. Every other
                // catalogued vehicle keeps its choice (measured on the 29: the DPV is the only tie)
                .ThenByDescending(p_G => p_G.Sum(p_S => p_S.Triangles))
                .Select(p_G => p_G.Key)
                .FirstOrDefault();

            return s_Body
                   ?? Scan.Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) &&
                                        VehicleCatalog.IsVehicleBodyPreset(p_B.Shader))
                       .Select(p_B => p_B.Shader)
                       .FirstOrDefault()
                   ?? s_Vehicle.Presets.FirstOrDefault(VehicleCatalog.IsVehicleBodyPreset)
                   // ⭐ A BODY THAT WEARS SHADERS OF ITS OWN STILL OPENS — AS SHIPPED (the F-35B: with a unique
                   // material/shader it stays as shipped until camo presets are made for it).
                   // Its document is the shader that draws the most of it, read off the dump — never a name.
                   // ⛔ Of the BODY, for every mesh of the vehicle: the cockpit shares the body's document (as the
                   // F-18's does). Measured per mesh, the F-35B's cockpit got `f35b_cockpit_parts` while the tab
                   // kept `f35b_main`, and its seat and consoles were neither the document's nor registered as
                   // the game's — drawn flat white.
                   ?? Cache.GameCache.SectionTrianglesOf(s_Vehicle.PreviewMesh)
                       .Where(p_S => p_S.Shader.Length > 0)
                       .GroupBy(p_S => p_S.Shader, StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(p_G => p_G.Sum(p_S => p_S.Triangles))
                       .Select(p_G => p_G.Key)
                       .FirstOrDefault();
        }

        // ⛔⛔ THE DUMP SAYS WHAT THE SECTIONS WEAR; THE SCAN SAYS WHAT THE DATABASE BINDS TO THE NAME — and
        // those differ. The ACR's 1p mesh is bound to weaponpreset3p AND weaponpresetshadowfp in the
        // database, and the first material to bind Camo was the 3p one; but not one section of the 1p dump
        // wears weaponpreset3p. Authoring on it made the whole body a FOREIGN section — drawn with the
        // game's own bytecode, untouched by the graph, so the camo would have shown nowhere. The preset a
        // section actually wears wins; the scan is the fallback for a mesh that is not cached yet.
        var s_Sections = Cache.GameCache.SectionShadersOf(p_Mesh)
            .Where(p_S => p_S.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p_S => p_S, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p_G => p_G.Count())
            .Select(p_G => p_G.Key)
            .ToList();

        if (s_Sections.Count > 0)
            return CamoTwinOf(s_Sections[0]);

        var s_Worn = Scan.Where(p_B =>
                p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) &&
                p_B.Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase))
            .Select(p_B => p_B.Shader)
            .ToList();

        // A material that ALREADY binds Camo names the right preset outright.
        var s_WithCamo = Scan.FirstOrDefault(p_B =>
            p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) &&
            p_B.Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase) &&
            p_B.Textures.Any(p_T => p_T.Param.Equals("Camo", StringComparison.OrdinalIgnoreCase)));

        // ⛔ The twin mapping runs on BOTH answers, not only on the fallback. Two weapons bind a Camo
        // parameter through a NoCamo preset, and returning that shader unchanged handed back exactly the
        // graph without a camo slot that this method exists to avoid. It is idempotent on a preset that
        // already samples camo, so applying it always is both simpler and correct.
        return CamoTwinOf(s_WithCamo?.Shader ?? s_Worn.FirstOrDefault());
    }

    /// <summary>
    /// The camo-sampling twin of a preset: "…NoCamo…" drops out of the name. Measured pairs, from the census
    /// of what the weapons actually wear — nocamofp → fp, nocamo3p → 3p. A preset that already samples camo
    /// is returned unchanged.
    ///
    /// ⛔ AND WeaponPresetFP → WeaponPresetShadowFP (2026-10-06, the pistols): its name carries no "nocamo" but
    /// it has NO camo path — its colour pass samples Diffuse and Specular only (its graph reads no Camo
    /// texture) — so the name rule handed it back unchanged and a pistol, whose whole body wears it, opened a
    /// canvas with nothing to paint. The bake already gives an FP material the ShadowFP camo shader (its 1P
    /// family), which has solutions for the bodies' declarations (CE4574FD, the Glock's 164BFA7C); this is
    /// the same pairing for the canvas. As shipped still shows FP: that is the preset the body wears.
    /// </summary>
    internal static string? CamoTwinOf(string? p_Shader) =>
        p_Shader != null && p_Shader.Replace('\\', '/').EndsWith("/weaponpresetfp", StringComparison.OrdinalIgnoreCase)
            ? p_Shader[..^"weaponpresetfp".Length] + "weaponpresetshadowfp"
            : p_Shader?.Replace("nocamo", "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's NoCamo preset of a view (census of the game's weapon presets: 3P, FP, NoCamo3P, ShadowFP,
    /// ShadowFP_xp2, ShadowNoCamoFP — one per view): the GRAPH the canvas shows while a weapon is as shipped
    /// — the weapon before any camo, never the camo's graph (settled after three rounds). ⛔ NOT what
    /// the 3D view draws: 28 bodies wear the camo preset with a texture in the Camo slot BY DEFAULT and that
    /// texture is their colour (the F2000's olive is `dust_d` under CamoDarkening (0.31, 0.31, 0.16), not its
    /// diffuse — an in-game picture beside the editor's grey one), so the view draws the preset the body
    /// wears with the game's variation-0 bindings and bytecode.
    /// </summary>
    public static string NoCamoPresetOf(string p_Shader)
    {
        // ⛔ A VEHICLE HAS NO NO-CAMO TWIN, and the weapons' rule INVENTS ONE. Left alone, this answered
        // "vehicles/shaders/weaponpresetshadownocamofp" for a tank — a shader that does not exist anywhere
        // in the game — and that name went to the canvas as the graph to show while the vehicle is as
        // shipped, and to the window as its no-camo preset. Nothing would have said so out loud: a missing
        // shader shows as a canvas that stays empty. For a vehicle, AS SHIPPED IS THE PRESET ITSELF.
        if (VehicleCatalog.IsVehicleBodyPreset(p_Shader))
            return p_Shader;

        // ⛔ And the pair exists only for the WEAPON presets: a vehicle's own shader (the F-35B's f35b_main,
        // not a vehiclepreset) got "vehicles/xpack01/f35/weaponpresetshadownocamofp" out of the rule below.
        if (!p_Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase))
            return p_Shader;

        var s_Slash = p_Shader.LastIndexOf('/');
        var s_Folder = s_Slash < 0 ? "weapons/shaders/" : p_Shader[..(s_Slash + 1)];
        return p_Shader.Contains("3p", StringComparison.OrdinalIgnoreCase)
            ? s_Folder + "weaponpresetnocamo3p"
            : s_Folder + "weaponpresetshadownocamofp";
    }

    /// <summary>
    /// The sampler register a preset reads its camo pattern from (t2 on every weapon preset, but measured,
    /// not assumed — see RegistersFor). Null when the preset's map is not cached yet.
    /// </summary>
    public string? CamoRegisterFor(string p_Shader)
    {
        if (p_Shader.Length == 0)
            return null;

        // A weapon preset has ONE camo texture ("Camo"); the jet family of vehicle presets has two, CamoA
        // where the colour uv's V >= 0 and CamoB where V < 0 (measured in vehiclepreset_jet's bytecode). The
        // one named here is where a single-register caller puts the pattern; CamoRegistersFor lists them all.
        var s_Registers = RegistersFor(p_Shader, Cache.SlotMap.Load(p_Shader));
        return s_Registers.GetValueOrDefault("Camo") ?? s_Registers.GetValueOrDefault("CamoA") ??
               s_Registers.GetValueOrDefault("CamoB");
    }

    /// <summary>
    /// EVERY sampler register a preset reads a camo pattern from, lowest first — "Camo" on a weapon preset,
    /// "CamoA" and "CamoB" on the jet family. A pattern goes on all of them: the F/A-18F's hull reads CamoA
    /// on 54 % of its vertices and CamoB on the rest, and a pattern on one register alone painted half a jet.
    /// </summary>
    public IReadOnlyList<string> CamoRegistersFor(string p_Shader) =>
        p_Shader.Length == 0
            ? Array.Empty<string>()
            : RegistersFor(p_Shader, Cache.SlotMap.Load(p_Shader))
                .Where(p_R => IsCamoTextureParameter(p_R.Key))
                .Select(p_R => p_R.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_R => int.TryParse(p_R, out var s_Number) ? s_Number : int.MaxValue)
                .ToList();

    /// <summary>
    /// The camo's pattern out of the document's pictures: the one on the preset's main camo register, else the one on any of its camo
    /// registers (a picture on the jet's CamoA node is the pattern too); <paramref name="p_OnCamo"/> lists every camo register that carries one.
    /// </summary>
    internal static string? PatternOf(string? p_Register, IReadOnlyList<string> p_CamoRegisters, IReadOnlyDictionary<string, string> p_Custom,
        out List<(string Register, string File)> p_OnCamo)
    {
        p_OnCamo = p_CamoRegisters.Where(p_Custom.ContainsKey).Select(p_R => (p_R, p_Custom[p_R])).ToList();
        return p_Register != null && p_Custom.TryGetValue(p_Register, out var s_Chosen)
            ? s_Chosen
            : p_OnCamo.Select(p_C => p_C.File).FirstOrDefault();
    }

    /// <summary>
    /// Every catalogued vehicle's camo documents checked against their own bytecode (offline): the presets its camo materials wear and
    /// the one its tab opens, the camo textures each preset's pixel shader names (texture_Camo* = tN), whether the bake takes a picture on
    /// each of those registers as the pattern (PatternOf over CamoRegistersFor), and whether the cached as-shipped graph has a node there
    /// to put it on. One line per preset; FAIL lines are the ones where a picture on the original camo node would not ship as the pattern.
    /// </summary>
    internal int VehicleCamoRegisterCensus(Func<string, string> p_GraphPath)
    {
        var s_Failures = 0;
        var s_OldRuleLost = 0;
        foreach (var s_Vehicle in m_Vehicles.Vehicles)
        {
            var s_Document = ShaderForMesh(s_Vehicle.PreviewMesh) ?? "";
            var s_Meshes = s_Vehicle.Meshes.Append(s_Vehicle.PreviewMesh).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var s_CamoPresets = s_Meshes
                .SelectMany(p_M => CamoMaterialsOf(p_M).Select(p_Id => MaterialShadersOf(p_M).GetValueOrDefault(p_Id, "")))
                .Where(p_S => p_S.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Console.WriteLine($"{s_Vehicle.Display} — document {s_Document.Split('/')[^1]}; camo materials wear " +
                              (s_CamoPresets.Count == 0 ? "(none)" : string.Join(", ", s_CamoPresets.Select(p_S => p_S.Split('/')[^1]))));

            foreach (var s_Preset in s_CamoPresets.Prepend(s_Document).Where(p_S => p_S.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s_IsDocument = s_Preset.Equals(s_Document, StringComparison.OrdinalIgnoreCase);
                var s_Contract = ContractOf(s_Preset);
                var s_Named = s_Contract?.Resources
                    .Where(p_R => p_R.IsTexture && p_R.Name.StartsWith("texture_Camo", StringComparison.OrdinalIgnoreCase))
                    .Select(p_R => (Param: p_R.Name["texture_".Length..], Register: p_R.Register.ToString()))
                    .ToList() ?? new List<(string Param, string Register)>();
                var s_Taken = CamoRegistersFor(s_Preset);
                var s_GraphFile = p_GraphPath(s_Preset);
                var s_Nodes = File.Exists(s_GraphFile)
                    ? RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_GraphFile)).Nodes
                        .Where(p_N => p_N.Kind is "Texture" or "NormalMap")
                        .Select(p_N => p_N.GetParam("Register"))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;

                // a far-LOD preset the studio never opens (no bytecode, no graph cached): nothing to put a picture on — the bake dresses it by
                // parameter name
                if (s_Contract == null && s_Nodes == null && !s_IsDocument)
                {
                    Console.WriteLine($"  n/a  {s_Preset}: never opened in the studio (no bytecode or graph cached) — dressed by parameter name");
                    continue;
                }

                // the F-35B: its own shaders, as shipped by design until camo presets are made for it
                if (s_IsDocument && s_CamoPresets.Count == 0)
                {
                    Console.WriteLine($"  n/a  [document] {s_Preset}: reads no camo texture and no material takes one — as shipped");
                    continue;
                }

                var s_Problems = new List<string>();
                var s_Notes = new List<string>();
                if (s_Contract == null)
                    s_Problems.Add("no cached bytecode");
                if (s_Nodes == null)
                    s_Problems.Add("no cached as-shipped graph");
                foreach (var (s_Param, s_Register) in s_Named)
                {
                    if (!IsCamoTextureParameter(s_Param))
                    {
                        // the Su-25's CamoMask: a mask, not a pattern — a picture there ships as that texture of that material
                        s_Notes.Add($"{s_Param}@t{s_Register} is a mask, not the pattern");
                        continue;
                    }

                    // ⛔ the negative control: the rule before 2026-09-25 (only CamoRegisterFor's one register) must lose the jet's other one
                    if (PatternOf(CamoRegisterFor(s_Preset), new[] { CamoRegisterFor(s_Preset)! },
                            new Dictionary<string, string> { [s_Register] = "old.png" }, out _) == null)
                        s_OldRuleLost++;

                    // the product's own rule, fed a picture on this register alone, as the Bake reads the document
                    var s_Picture = $"picture_t{s_Register}.png";
                    var s_Pattern = PatternOf(CamoRegisterFor(s_Preset), s_Taken,
                        new Dictionary<string, string> { [s_Register] = s_Picture }, out _);
                    if (s_Pattern != s_Picture)
                        s_Problems.Add($"a picture on {s_Param}@t{s_Register} is NOT the pattern");
                    if (s_Nodes != null && !s_Nodes.Contains(s_Register))
                        s_Problems.Add($"the as-shipped graph has no node at t{s_Register} ({s_Param})");
                }

                if (s_IsDocument && s_Named.Count == 0 && s_Contract != null)
                    s_Problems.Add("the document's shader reads no camo texture");
                // ⛔ the control: a register that is NOT a camo register must not become the pattern
                var s_Other = s_Contract?.Resources.Where(p_R => p_R.IsTexture && !s_Taken.Contains(p_R.Register.ToString()))
                    .Select(p_R => p_R.Register.ToString()).FirstOrDefault();
                if (s_Other != null && PatternOf(CamoRegisterFor(s_Preset), s_Taken,
                        new Dictionary<string, string> { [s_Other] = "control.png" }, out _) != null)
                    s_Problems.Add($"CONTROL: a picture on t{s_Other} (not a camo texture) was taken as the pattern");

                if (s_Problems.Count > 0)
                    s_Failures++;
                Console.WriteLine($"  {(s_Problems.Count == 0 ? "ok  " : "FAIL")} {(s_IsDocument ? "[document] " : "")}{s_Preset}: " +
                                  $"camo textures {(s_Named.Count == 0 ? "(none)" : string.Join(", ", s_Named.Select(p_N => $"{p_N.Param}@t{p_N.Register}")))}" +
                                  $"; bake takes t[{string.Join(",", s_Taken)}]" +
                                  (s_Problems.Count > 0 ? " — " + string.Join("; ", s_Problems) : "") +
                                  (s_Notes.Count > 0 ? " (" + string.Join("; ", s_Notes) + ")" : ""));
            }
        }

        Console.WriteLine($"control: the old one-register rule loses the picture on {s_OldRuleLost} camo node(s) above (must be > 0)");
        if (s_OldRuleLost == 0)
            s_Failures++;
        Console.WriteLine(s_Failures == 0 ? "VEHICLECAMOREGISTERS: PASS" : $"VEHICLECAMOREGISTERS: FAIL ({s_Failures} preset line(s))");
        return s_Failures == 0 ? 0 : 1;
    }

    /// <summary>"Camo", "CamoA", "CamoB"… — a camo TEXTURE parameter; "CamoTiling"/"CamoBrightness" are numbers, not this.</summary>
    public static bool IsCamoTextureParameter(string p_Name) =>
        p_Name.StartsWith("Camo", StringComparison.OrdinalIgnoreCase) &&
        (p_Name.Length == 4 || (p_Name.Length == 5 && char.IsLetter(p_Name[4])));

    /// <summary>The register a preset samples the weapon's diffuse from (t1 on the presets, measured) — the UV a sticker follows.</summary>
    public int? DiffuseRegisterFor(string p_Shader) =>
        p_Shader.Length > 0 &&
        int.TryParse(RegistersFor(p_Shader, Cache.SlotMap.Load(p_Shader)).GetValueOrDefault("Diffuse"), out var s_Register)
            ? s_Register
            : null;

    /// <summary>The folder the user's sticker pictures are kept in, beside the caches.</summary>
    public static string StickerLibrary => StickerLibraryOverride ?? Path.Combine(Settings.CacheFolder, "stickers");

    /// <summary>A seam's library, so a headless run never puts a test picture among the user's.</summary>
    public static string? StickerLibraryOverride { get; set; }

    private View.StickerPanel? m_StickerPanel;

    /// <summary>The panel sticker mode shows, built once per window (a driver may reach its controls).</summary>
    public View.StickerPanel StickerPanelFor(RimeShaderEditor.MainWindow p_Editor)
    {
        if (m_StickerPanel == null)
        {
            // ⭐ a VEHICLE takes the players' emblems as decals — sticker mode on one only places the emblem's squares (in space,
            // metres) and leaves its graph alone: here Camo Studio is only used to give the emblems' position
            p_Editor.SlotOnlySubject = p_Mesh => VehicleOfMesh(p_Mesh) != null;
            m_StickerPanel = new View.StickerPanel(p_Editor, StickerLibrary,
                p_Mesh => m_Weapons.Weapons.FirstOrDefault(p_W => string.Equals(p_W.PreviewMesh, p_Mesh, StringComparison.OrdinalIgnoreCase))?.Folder ??
                          VehicleOfMesh(p_Mesh)?.Display);
            m_StickerPanel.Done += (_, _) => p_Editor.SetStickerMode(false);
        }

        return m_StickerPanel;
    }

    /// <summary>The catalogue's vehicle whose body a mesh is (its preview mesh), or null for any other mesh.</summary>
    public Catalog.VehicleEntry? VehicleOfMesh(string p_Mesh) =>
        Vehicles.Vehicles.FirstOrDefault(p_V => p_V.PreviewMesh.Length > 0 && p_V.PreviewMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Opens sticker mode on the window: the studio's panel goes in, the window fills itself with the
    /// weapon. Needs a picked weapon — a sticker is placed on a body, not on a cube.
    /// </summary>
    public void StartStickers(RimeShaderEditor.MainWindow p_Editor)
    {
        if (p_Editor.CamoSnapshot().Picked == null)
        {
            p_Editor.LogCamo("Stickers: pick a weapon under Preview weapon first — a sticker is placed on its body.");
            return;
        }

        p_Editor.SetStickerMode(true, StickerPanelFor(p_Editor));
        m_StickerPanel!.ShowPlacements();
    }

    /// <summary>
    /// The material values the simple form's sliders stand for, in the shape the preview's constant-buffer
    /// path takes: Tiling -> CamoTiling (x and y), Wear -> WearAmount. Null until the form has written them,
    /// so an untouched camo leaves the weapon's own numbers alone.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? LiveValuesOf(Camo p_Camo)
    {
        if (!p_Camo.SimpleEdited)
            return null;

        var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CamoTiling"] = ExpandValue("CamoTiling", p_Camo.Tiling.ToString(s_Culture)),
            ["WearAmount"] = ExpandValue("WearAmount", p_Camo.Wear.ToString(s_Culture)),
        };
    }

    /// <summary>
    /// What a user action on a WEAPON's or a VEHICLE's form writes (review 2026-09-29). The rule — the tiling is the weapon's own, and a
    /// weapon without that camo gets 3: the numbers the form starts from are the ones that run and
    /// ship — so both are written, each with the value it truly holds (Camo.Tiling / Camo.Wear: the weapon's own or the default, never the
    /// slider's clamp of it), EXCEPT a number the user did not move that the document already has typed: that one stays as typed (a "4,2"
    /// is not flattened to "4,4" by choosing a picture). Null until the form has written (SimpleEdited). A soldier's part writes only what
    /// moved (<see cref="SoldierMovedValuesOf"/>). CAMO_FORM_WRITES_OLD=1 = both, typed or not, as before (<see cref="LiveValuesOf"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? FormValuesOf(Camo p_Camo, bool p_TilingTyped, bool p_WearTyped)
    {
        if (Environment.GetEnvironmentVariable("CAMO_FORM_WRITES_OLD") == "1")
            return LiveValuesOf(p_Camo);
        if (!p_Camo.SimpleEdited)
            return null;

        var s_Culture = System.Globalization.CultureInfo.InvariantCulture;
        var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (p_Camo.MovedControls.Contains("tiling") || !p_TilingTyped)
            s_Values["CamoTiling"] = ExpandValue("CamoTiling", p_Camo.Tiling.ToString(s_Culture));
        if (p_Camo.MovedControls.Contains("wear") || !p_WearTyped)
            s_Values["WearAmount"] = ExpandValue("WearAmount", p_Camo.Wear.ToString(s_Culture));
        return s_Values.Count > 0 ? s_Values : null;
    }

    /// <summary>
    /// The soldier's form's number, only when its tiling was MOVED: a factor typed on a part flattens the tiling each of its meshes and
    /// materials runs on its own (the 1P arms' included), so a picture chosen writes none (review 2026-09-29).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? SoldierMovedValuesOf(Camo p_Camo) =>
        Environment.GetEnvironmentVariable("CAMO_FORM_WRITES_OLD") == "1" || p_Camo.MovedControls.Contains("tiling")
            ? SoldierLiveValuesOf(p_Camo)
            : null;

    /// <summary>
    /// The simple form's number on a SOLDIER's part (on soldiers only the tiling stays, the wear is removed): ONE
    /// tiling, CamoTileFactor — every soldier shader with camo reads it as a single float on both uv directions (measured 2026-09-29) and
    /// none has a wear. Null until the form has written it, as <see cref="LiveValuesOf"/>. Rounded to hundredths: the soldiers' slider
    /// steps in tenths, and a tenth is not exact in binary (a snapped 6.9 can be 6.900000000000001, which the node would then show).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? SoldierLiveValuesOf(Camo p_Camo) =>
        !p_Camo.SimpleEdited
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CamoTileFactor"] = ExpandValue("CamoTileFactor",
                    Math.Round(p_Camo.Tiling, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };

    /// <summary>
    /// The four-component vector a single number stands for on a weapon-material constant — what the user
    /// types on the node or moves on the slider is ONE number (without having to type x,x,x,x).
    /// The kind of each constant (tiling in x and y, colour in RGBA, scalar in x) is the measured table.
    /// </summary>
    public static string ExpandValue(string p_Name, string p_Number) => WeaponConstants.Expand(p_Name, p_Number);

    /// <summary>
    /// The note a node of a camo graph should carry, or null. The three things the simple form edits —
    /// the pattern, its tiling, its wear — are nodes in the graph, and nothing on a node says which is
    /// which (the request: a comment on those nodes, to know which ones they are and edit them). Same
    /// words as the form's own labels, so the two views name one thing one way.
    /// </summary>
    public string? NoteForNode(RimeShaderEditor.Graph.GraphNode p_Node, string p_Preset)
    {
        if (p_Node.Kind == "ExternalConstant")
        {
            // The bubble on the canvas stays SHORT and only on the two knobs the simple form also has, so
            // they can be found among the twelve; the full account of every constant is the panel's
            // (PanelNoteForNode) — twelve long bubbles buried the graph.
            if (WeaponConstants.Find(p_Node.GetParam("Name")) is not { } s_Constant)
                return null;

            return s_Constant.Name switch
            {
                "CamoTiling" => $"Tiling — {s_Constant.What.Split('.')[0]}. (Simple mode: the Tiling slider.)",
                "WearAmount" => $"Wear — {s_Constant.What.Split(':')[0]}. (Simple mode: the Wear slider.)",
                _ => null,
            };
        }

        if (p_Node.Kind == "Texture" && p_Node.GetParam("Register") is { Length: > 0 } s_NodeRegister &&
            CamoRegistersFor(p_Preset).Contains(s_NodeRegister, StringComparer.OrdinalIgnoreCase))
            return "Pattern — " + View.SimpleCamoPanel.PatternHelp + " (Browse… below picks it; Simple mode: Choose image.)";

        return null;
    }

    /// <summary>
    /// What the properties panel says about a constant, beside its value box: what it is, what one number
    /// means on it, and the range the game itself uses (a brief explanation shown right there, like the
    /// one for external). Null for anything the measured table does not cover.
    /// </summary>
    public string? PanelNoteForNode(RimeShaderEditor.Graph.GraphNode p_Node, string p_Preset) =>
        p_Node.Kind == "ExternalConstant" && WeaponConstants.Find(p_Node.GetParam("Name")) is { } s_Constant
            ? WeaponConstants.Note(s_Constant, VehicleCatalog.IsVehicleBodyPreset(p_Preset))
            : null;

    /// <summary>
    /// Whether a section shader is one a camo is authored on: a weapon preset, or a vehicle's BODYWORK
    /// preset.
    ///
    /// ⛔ IT WAS CALLED IsCamoPreset AND THE NAME WAS THE WHOLE RULE. With vehicles on the same window
    /// the question every caller is really asking is "does this material take the camo", and a vehicle's
    /// hull answers yes through a preset whose name has no "weapon" in it. The two families cannot collide:
    /// measured, no weapon mesh wears a vehicle preset, and the 12 materials under vehicles/ that wear
    /// weaponpresetfp are the guns bolted to them — which do take a camo.
    /// </summary>
    public static bool IsCamoPreset(string p_Shader) =>
        p_Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase) ||
        VehicleCatalog.IsVehicleBodyPreset(p_Shader);

    /// <summary>
    /// The section shaders of a mesh that the camo being authored OWNS besides its target: every weapon
    /// preset the body wears. To a preview those are the edited shader's own sections, not foreign ones —
    /// in-game the mod substitutes the preset on exactly those materials, so a camo must show on them here
    /// too, whichever preset's tab happens to be on the canvas. Bullets, sights and tape stay foreign.
    /// </summary>
    public static IReadOnlyList<string> ReplacedBy(string p_Shader, IEnumerable<string> p_SectionShaders) =>
        p_SectionShaders
            .Where(p_S => p_S.Length > 0 &&
                          !p_S.Equals(p_Shader, StringComparison.OrdinalIgnoreCase) &&
                          IsCamoPreset(p_S))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A warning to show when a weapon is picked, or null when there is nothing to say.
    ///
    /// ⛔⛔ ONE WEAPON IN THE GAME CANNOT WEAR A CAMO WITH VANILLA SHADERS, and it is not a guess: the AUG A3
    /// is the only mesh whose vertex-declaration uses FOUR TexCoords, and no camo-sampling preset has a
    /// compiled solution for it (only the NoCamo ones do). With no solution the engine does not draw the
    /// material at all — the weapon renders BLACK. That cost a saga's worth of in-game launches to find, so
    /// the studio says it BEFORE the user authors a camo and discovers it in a match.
    ///
    /// Detected by MEASURE, not by name: the count of TexCoords comes from the declarations the mesh dump
    /// recorded. Four is the broken case; the four weapons with two are covered and work.
    /// </summary>
    public string? NoteForMesh(string p_Mesh)
    {
        if (!Declarations.TryGetValue(p_Mesh, out var s_TexCoords) || s_TexCoords < 4)
            return null;

        return $"⚠ This weapon's mesh declares {s_TexCoords} texture coordinates — the only one in the game " +
               "that does. No camo-sampling preset has a compiled solution for that declaration, so a camo " +
               "on it renders BLACK unless the mod also compiles the missing solution into the shader " +
               "database. Authoring works; delivery needs that extra step.";
    }

    /// <summary>
    /// What is said when an aircraft's COCKPIT (the model the game draws around the pilot, the window's Cockpit box) is picked: the preview
    /// dresses it with the camo, and no bake paints it — none of the 8 cockpits is a mesh or a piece of its vehicle's package (measured on
    /// the catalogue, review 2026-09-29: it went unsaid). Null for any other mesh. Said at the bake too (BakeRequested).
    /// </summary>
    public string? CockpitNoteFor(string p_Mesh) =>
        m_Vehicles.VehicleOfCockpit(p_Mesh) is { } s_Aircraft
            ? $"⚠ {s_Aircraft.Display}'s cockpit — the model around the pilot — is shown here with the camo, but a bake does not paint it " +
              "yet: in the game it stays as the game ships it. The body and its pieces take the camo."
            : null;

    /// <summary>Mesh -> how many TexCoords its camo-bearing material declares, read from the dump log.</summary>
    private Dictionary<string, int> Declarations => m_Declarations ??= LoadDeclarations();

    private Dictionary<string, int>? m_Declarations;

    /// <summary>
    /// The dumped sections of a mesh in DUMP ORDER — the order the .rsm holds them — each with the game's
    /// own material ID (its index in the mesh's material list) and the shader it wears. Read from the
    /// MESHDECL lines of the cache logs, which the dump writes in the same loop that writes the sections.
    /// Empty for a mesh no cache log covers (a section then only has its order as an id).
    /// </summary>
    /// <summary>
    /// The reactive armour a vehicle body carries, if any — cached per mesh because the picker, the dressing
    /// and the seams all ask for it and it reads a dump each time.
    /// </summary>
    public ReactiveArmour.Piece? ReactiveArmourOf(string p_Mesh)
    {
        if (m_Armour.TryGetValue(p_Mesh, out var s_Known))
            return s_Known;

        var s_Piece = ReactiveArmour.Of(p_Mesh, Scan, SectionMaterialsOf(p_Mesh));
        m_Armour[p_Mesh] = s_Piece;
        return s_Piece;
    }

    private readonly Dictionary<string, ReactiveArmour.Piece?> m_Armour =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The material indices of a vehicle body that take the camo — what its camo pieces keep: a body preset that binds Camo, on the
    /// mesh's own variation, the same test the bake's camo shaders pass. Empty when the scan does not have the mesh.
    /// ⛔ ANY camo texture parameter (IsCamoTextureParameter): the jet family binds CamoA/CamoB and no "Camo" — with "Camo" alone every
    /// aircraft read "camo materials []" and got no piece (2026-09-25).
    /// </summary>
    public IReadOnlyCollection<int> CamoMaterialsOf(string p_Mesh)
    {
        var s_Ids = Scan
            .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == 0 && p_B.MaterialId >= 0 &&
                          VehicleCatalog.IsVehicleBodyPreset(p_B.Shader) &&
                          p_B.Textures.Any(p_T => IsCamoTextureParameter(p_T.Param)))
            .Select(p_B => p_B.MaterialId)
            .ToHashSet();

        // …and the ones the catalogue forces on this body (VehicleEntry.ForceCamoMaterials: the RHIB's hull, whose camo no database
        // binds), as long as they wear a body preset
        var s_Shaders = MaterialShadersOf(p_Mesh);
        foreach (var s_Vehicle in m_Vehicles.Vehicles.Where(p_V => p_V.PreviewMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var s_Id in s_Vehicle.ForceCamoMaterials)
                if (VehicleCatalog.IsVehicleBodyPreset(s_Shaders.GetValueOrDefault(s_Id)))
                    s_Ids.Add(s_Id);

            // …and without the ones it keeps the camo OFF (VehicleEntry.CamoOffMaterials: the Rhino's remote gun)
            s_Ids.ExceptWith(s_Vehicle.CamoOffMaterials);
        }

        return s_Ids;
    }

    /// <summary>
    /// A body's own starting document in the studio (the RHIB's: the graph with its hand-painted mask — MainWindow.SubjectPresetIn), from
    /// Data/vehiclepresets (VehicleCatalog.PresetPathOf): the changes kept there applied to the user's own translation of the shader
    /// (Project.GraphPatch), a FRESH graph per call, its pictures' paths (relative to that folder in the file) made whole. Null when the mesh
    /// has none; one that cannot be made (no translation cached yet, a file that cannot be read) is said once and treated as none.
    /// </summary>
    public RimeShaderEditor.Graph.ShaderGraph? SubjectPresetOf(string p_Mesh)
    {
        var s_Path = VehicleCatalog.PresetPathOf(p_Mesh);
        if (!File.Exists(s_Path))
            return null;

        try
        {
            if (Project.GraphPatch.GraphOf(s_Path, out var s_Why) is not { } s_Graph)
            {
                if (m_PresetUnreadable.Add(p_Mesh))
                    Console.Error.WriteLine($"PRESET: {Path.GetFileName(s_Path)} cannot be made ({s_Why}) — {p_Mesh} starts from its camo's common document.");
                return null;
            }

            // its pictures: the shipped file, or the one its recipe makes in the cache out of the game's own textures (Project.PresetPicture)
            foreach (var s_Node in s_Graph.Nodes)
                if (s_Node.Params.TryGetValue("CustomTexture", out var s_Picture) && s_Picture.Length > 0 && !Path.IsPathRooted(s_Picture))
                {
                    if (Project.PresetPicture.FileOf(VehicleCatalog.PresetFolder, s_Picture, out var s_NoPicture) is not { } s_File)
                    {
                        if (m_PresetUnreadable.Add(p_Mesh))
                            Console.Error.WriteLine($"PRESET: {Path.GetFileName(s_Path)}'s picture cannot be had ({s_NoPicture}) — {p_Mesh} starts from its camo's common document.");
                        return null;
                    }

                    s_Node.Params["CustomTexture"] = s_File;
                }

            return s_Graph;
        }
        catch (Exception s_Exception)
        {
            if (m_PresetUnreadable.Add(p_Mesh))
                Console.Error.WriteLine($"PRESET: {s_Path} could not be read ({s_Exception.Message}) — {p_Mesh} starts from its camo's common document.");
            return null;
        }
    }

    private readonly HashSet<string> m_PresetUnreadable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pieces' starting graphs the studio ships: Data/piecepresets, one patch per piece naming the meshes it is for.</summary>
    public static string PiecePresetFolder => Path.Combine(AppContext.BaseDirectory, "Data", "piecepresets");

    private Dictionary<string, string>? m_PiecePresets;

    /// <summary>
    /// The starting graph the studio ships for a piece of equipment the user has saved none of his own for (MainWindow.AttachmentPresetOf):
    /// the sound suppressor's, whose camo shows only through a graph of its own — the stock preset's wear chain cancels it on that piece.
    /// Kept as the changes made to the translation of its shader (Project.GraphPatch), applied to the user's own; a fresh graph per call,
    /// null when the piece has none or it cannot be made (said once).
    /// </summary>
    public RimeShaderEditor.Graph.ShaderGraph? PiecePresetOf(string p_Mesh)
    {
        if (m_PiecePresets == null)
        {
            m_PiecePresets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(PiecePresetFolder))
                foreach (var s_File in Directory.EnumerateFiles(PiecePresetFolder, "*.patch.json"))
                {
                    try
                    {
                        foreach (var s_Mesh in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(s_File))?["Meshes"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                            m_PiecePresets[(string) s_Mesh!] = s_File;
                    }
                    catch (Exception s_Exception)
                    {
                        Console.Error.WriteLine($"PRESET: {s_File} could not be read ({s_Exception.Message}).");
                    }
                }
        }

        if (!m_PiecePresets.TryGetValue(p_Mesh, out var s_Path))
            return null;

        try
        {
            if (Project.GraphPatch.GraphOf(s_Path, out var s_Why) is { } s_Graph)
                return s_Graph;

            if (m_PresetUnreadable.Add(p_Mesh))
                Console.Error.WriteLine($"PRESET: {Path.GetFileName(s_Path)} cannot be made ({s_Why}) — {p_Mesh} starts from its stock preset.");
        }
        catch (Exception s_Exception)
        {
            if (m_PresetUnreadable.Add(p_Mesh))
                Console.Error.WriteLine($"PRESET: {s_Path} could not be read ({s_Exception.Message}) — {p_Mesh} starts from its stock preset.");
        }

        return null;
    }

    /// <summary>A mesh's material index -> the shader it wears (its own variation), as the scan read it.</summary>
    public IReadOnlyDictionary<int, string> MaterialShadersOf(string p_Mesh)
    {
        var s_Variation = PreviewVariationOf(p_Mesh);
        return Scan
            .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == s_Variation && p_B.MaterialId >= 0)
            .GroupBy(p_B => p_B.MaterialId)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Shader);
    }

    public IReadOnlyList<(int MaterialId, string Shader)> SectionMaterialsOf(string p_Mesh)
    {
        _ = Declarations;
        return m_SectionMaterials != null && m_SectionMaterials.TryGetValue(p_Mesh, out var s_List)
            ? s_List
            : Array.Empty<(int, string)>();
    }

    private Dictionary<string, List<(int MaterialId, string Shader)>>? m_SectionMaterials;

    /// <summary>
    /// Mesh -> the vertex declarations its PRESET sections declare ("0x882E8A4C"), from the same cache logs.
    /// What decides whether the third-person preset can draw an accessory or the bake must ship a twin
    /// re-labelled to that declaration — read, never inferred from the mesh's name or kind.
    /// </summary>
    private Dictionary<string, List<string>> SectionDeclarations
    {
        get
        {
            _ = Declarations;
            return m_SectionDeclarations ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, List<string>>? m_SectionDeclarations;

    /// <summary>Every section's (shader, declaration, material id) per mesh, from the cache logs' MESHDECL lines — see <see cref="DeclarationsOf(string, string)"/>.</summary>
    private Dictionary<string, List<(string Shader, string Decl, int MaterialId)>>? m_SectionShaderDeclarations;

    /// <summary>
    /// The vertex declarations a mesh's sections that wear ONE shader draw with ("0x…", distinct) — what a copy of that shader must have
    /// solutions carrying the graph for, or the mesh draws the game's own logic under the copy's name. Empty when the mesh is not in a cache log.
    /// </summary>
    public IReadOnlyList<string> DeclarationsOf(string p_Mesh, string p_Shader)
    {
        _ = Declarations;
        return m_SectionShaderDeclarations != null && m_SectionShaderDeclarations.TryGetValue(p_Mesh, out var s_List)
            ? s_List.Where(p_S => p_S.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase)).Select(p_S => p_S.Decl)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();
    }

    /// <summary>
    /// The vertex declarations the soldiers' CAMO CLOTH draws with under ONE shader — the sections of the Soldiers catalogue's meshes (its
    /// cache log's MESHDECL lines) whose material binds CamoTile in the soldier scan — the one most sections draw with first: what a character
    /// shader's translation takes its reference permutation from (ShaderIndex.SubjectDeclarations: `characterroot_xp4`'s plainest is the
    /// faces', its cloth's is 0x56576EBB, 2026-09-28). Empty for a shader no soldier's cloth wears: a weapon's, a vehicle's, a face's or a
    /// head's is translated as it always was (counting every cached mesh moved 14 vehicle and prop shaders; every soldier section, the
    /// heads' skin shaders).
    /// </summary>
    public IReadOnlyList<string> DeclarationsOfShader(string p_Shader)
    {
        _ = Declarations;
        var s_Meshes = Soldiers.Soldiers.SelectMany(p_S => p_S.AllMeshes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var s_Cloth = Scan
            .Where(p_B => p_B.MaterialId >= 0 && p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
            .Select(p_B => (p_B.Mesh.ToLowerInvariant(), p_B.MaterialId)).ToHashSet();
        return m_SectionShaderDeclarations == null
            ? Array.Empty<string>()
            : m_SectionShaderDeclarations.Where(p_M => s_Meshes.Contains(p_M.Key))
                .SelectMany(p_M => p_M.Value.Where(p_S => s_Cloth.Contains((p_M.Key.ToLowerInvariant(), p_S.MaterialId))))
                .Where(p_S => p_S.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p_S => p_S.Decl, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(p_G => p_G.Count()).ThenBy(p_G => p_G.Key, StringComparer.Ordinal)
                .Select(p_G => p_G.Key).ToList();
    }

    /// <summary>Every shader a cached mesh's section wears (the cache logs' MESHDECL lines), distinct.</summary>
    public IReadOnlyList<string> ShadersOfSections()
    {
        _ = Declarations;
        return m_SectionShaderDeclarations == null
            ? Array.Empty<string>()
            : m_SectionShaderDeclarations.Values.SelectMany(p_L => p_L).Select(p_S => p_S.Shader)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p_S => p_S, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private Dictionary<string, int> LoadDeclarations()
    {
        var s_Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var s_Sections = new Dictionary<string, List<(int MaterialId, string Shader)>>(StringComparer.OrdinalIgnoreCase);
        var s_Decls = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // ⛔ ONE LOG PER CATALOGUE, AND THE LIST IS THE WHOLE RULE. The vehicles' dump writes its own
        // (cache_vehicles.log, 213 MESHDECL lines for 33 meshes) and leaving it out of this list did NOT
        // fail: every vehicle simply had no material IDs, so the picker fell back to the section ORDER and
        // the game's own IDs — the thing the panel exists for — were never shown. It showed the first
        // time a vehicle was opened (the material IDs were missing on vehicles). A catalogue
        // added without its log is a feature that quietly does not apply to it. (The soldiers' is the fourth, 2026-09-28.)
        var s_Lines = new[] { "cache_meshes.log", "cache_accessories.log", "cache_vehicles.log", Cache.GameCache.SoldierDumpLog + ".log" }
            .Select(p_Name => System.IO.Path.Combine(Settings.CacheFolder, p_Name))
            .Where(System.IO.File.Exists)
            .SelectMany(System.IO.File.ReadLines);

        // The log prints every section's declaration and THEN names the mesh they belonged to, so the
        // pending lines are attributed when that name arrives.
        var s_Pending = new List<string>();
        var s_PendingParts = new List<string>();
        var s_Tracks = new Dictionary<string, (float Left, float Right)>(StringComparer.OrdinalIgnoreCase);
        var s_ShaderDecls = new Dictionary<string, List<(string Shader, string Decl, int MaterialId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Line in s_Lines)
        {
            if (s_Line.StartsWith("MESHPART:", StringComparison.Ordinal))
            {
                s_PendingParts.Add(s_Line);
                continue;
            }

            if (s_Line.StartsWith("MESHDECL:", StringComparison.Ordinal))
            {
                s_Pending.Add(s_Line);
                continue;
            }

            var s_Match = System.Text.RegularExpressions.Regex.Match(s_Line,
                @"^Wrote \d+ section\(s\) for (\S+) to");

            if (!s_Match.Success)
                continue;

            var s_Worst = s_Pending
                .Where(p_L => ShaderOfDecl(p_L) is { } s_Shader && IsCamoPreset(s_Shader))
                .Select(p_L => System.Text.RegularExpressions.Regex
                    .Matches(p_L, @"TexCoord(\d)").Select(p_M => p_M.Groups[1].Value).Distinct().Count())
                .DefaultIfEmpty(0)
                .Max();

            s_Map[s_Match.Groups[1].Value] = s_Worst;

            // "MESHDECL: subset=1 material=1 stride=32 shader=weapons/shaders/weaponpresetfp decl=…"
            s_Sections[s_Match.Groups[1].Value] = s_Pending
                .Select(p_L => System.Text.RegularExpressions.Regex.Match(p_L, @"material=(\d+)\s+stride=\d+\s+shader=(\S*)"))
                .Where(p_M => p_M.Success)
                .Select(p_M => (int.Parse(p_M.Groups[1].Value), p_M.Groups[2].Value))
                .ToList();

            // The declarations of the PRESET sections only: a reticle's or a beam's says nothing about
            // whether the accessory's body can be drawn by the third-person preset. Read through the same
            // test the rest of the studio uses, so a vehicle's hull counts as a preset section too.
            s_Decls[s_Match.Groups[1].Value] = s_Pending
                .Where(p_L => ShaderOfDecl(p_L) is { } s_Shader && IsCamoPreset(s_Shader))
                .Select(p_L => System.Text.RegularExpressions.Regex.Match(p_L, @"decl=(0x[0-9A-Fa-f]+)"))
                .Where(p_M => p_M.Success)
                .Select(p_M => p_M.Groups[1].Value.ToUpperInvariant().Replace("0X", "0x"))
                .ToList();

            if (AnchorsOf(s_PendingParts) is { } s_Anchors)
                s_Tracks[s_Match.Groups[1].Value] = s_Anchors;

            // …and every section's, by the shader it wears (a soldier's: characterroot is no camo preset, and a copy of it must cover the
            // declarations his meshes draw with — CamoBaker.BuildSoldier)
            s_ShaderDecls[s_Match.Groups[1].Value] = s_Pending
                .Select(p_L => (Shader: ShaderOfDecl(p_L), Decl: System.Text.RegularExpressions.Regex.Match(p_L, @"decl=(0x[0-9A-Fa-f]+)"),
                    Material: System.Text.RegularExpressions.Regex.Match(p_L, @"material=(\d+)")))
                .Where(p_S => p_S.Shader != null && p_S.Decl.Success)
                .Select(p_S => (p_S.Shader!, p_S.Decl.Groups[1].Value.ToUpperInvariant().Replace("0X", "0x"),
                    p_S.Material.Success ? int.Parse(p_S.Material.Groups[1].Value) : -1))
                .ToList();

            s_Pending.Clear();
            s_PendingParts.Clear();
        }

        m_SectionMaterials = s_Sections;
        m_SectionDeclarations = s_Decls;
        m_SectionShaderDeclarations = s_ShaderDecls;
        m_TrackAnchors = s_Tracks;
        return s_Map;
    }

    /// <summary>
    /// Where a tracked vehicle's two belts go, DERIVED from the body's own part anchors — never a constant
    /// per vehicle.
    ///
    /// ⛔ THE PROBLEM IT SOLVES (the tank was missing its tracks): the running belt is NOT in the
    /// body mesh. It is its own skinned mesh whose bind pose is already the belt wrapped round the road
    /// wheels — but in ITS OWN space, centred on x, because where each of the two goes belongs to the
    /// vehicle, not to the mesh. The body says it: a tracked hull carries two symmetric ROWS of part
    /// anchors at road-wheel height (the M1A2: parts 40..48 at x=-1.434 and 29..37 at x=+1.423, both at
    /// y=0.331, spanning z +2.94..-3.43 — as many anchors as the belt's skeleton has wheel bones).
    ///
    /// Null when the hull has no such pair, which is every wheeled vehicle and the answer for them: their
    /// wheels live inside the body mesh and there is nothing to add.
    /// </summary>
    public (float Left, float Right)? TrackAnchorsOf(string p_Mesh)
    {
        _ = Declarations;
        return m_TrackAnchors != null && m_TrackAnchors.TryGetValue(p_Mesh, out var s_Found) ? s_Found : null;
    }

    /// <summary>
    /// What the object on screen carries that its own mesh does not hold, already placed: a tracked
    /// vehicle's TWO belts, one per row of anchors, the far one mirrored so its outer face still points out.
    /// Null for a weapon, an accessory and every wheeled vehicle.
    /// </summary>
    public IReadOnlyList<(string Path, float OffsetX, bool MirrorX, string Context)>? ExtraMeshesFor(string p_Mesh)
    {
        // ⭐ THE REST OF THE SOLDIER around his torso (the preview is the whole character, head included): every
        // other third-person mesh his appearance puts on him, where it is — a soldier's meshes are skinned on one skeleton and dumped
        // as stored, so they meet as one character with no offset. Each goes as CONTEXT: drawn as it ships, with its own art.
        // …and around ANY of his parts (the head, the legs picked on their buttons), the rest of the soldier ON SCREEN: the legs two
        // soldiers share are drawn under the one whose torso was picked. His first-person arms are shown alone, as an aircraft's cockpit is.
        if (SoldierWearing(p_Mesh) is { } s_Soldier)
        {
            if (s_Soldier.PreviewMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
                m_ActiveSoldier = s_Soldier;
            if (s_Soldier.ArmsMesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
                return null;

            var s_Around = s_Soldier.ThirdPersonMeshes
                .Where(p_M => !p_M.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase))
                .Where(p_M => System.IO.File.Exists(Cache.GameCache.MeshFile(p_M)))
                .Select(p_M => (Cache.GameCache.MeshFile(p_M), 0f, false, p_M))
                .ToList();
            return s_Around.Count > 0 ? s_Around : null;
        }

        if (Vehicles.TrackMeshOfBody(p_Mesh) is not { Length: > 0 } s_Track)
            return null;

        var s_File = Cache.GameCache.MeshFile(s_Track);
        if (!System.IO.File.Exists(s_File) || TrackAnchorsOf(p_Mesh) is not { } s_Anchors)
            return null;

        // A belt is a piece of the vehicle itself, not context: it behaves exactly as it always did.
        return new[]
        {
            (s_File, s_Anchors.Right, false, ""),
            (s_File, s_Anchors.Left, true, ""),
        };
    }

    private Dictionary<string, (float Left, float Right)>? m_TrackAnchors;

    /// <summary>
    /// The two rows of anchors out on the flanks and down at wheel height, averaged. ⛔ The log writes its
    /// numbers in the machine's culture, so `trans=(0,292,2,334,1,187)` is THREE values with COMMA decimal
    /// separators: splitting on commas yields six tokens and silently reads y as 799.
    /// </summary>
    private static (float Left, float Right)? AnchorsOf(IReadOnlyList<string> p_Parts)
    {
        var s_Xs = new List<float>();
        foreach (var s_Line in p_Parts)
        {
            var s_At = s_Line.IndexOf("trans=(", StringComparison.Ordinal);
            if (s_At < 0)
                continue;

            var s_End = s_Line.IndexOf(')', s_At);
            if (s_End < 0)
                continue;

            var s_Numbers = System.Text.RegularExpressions.Regex
                .Matches(s_Line[(s_At + 7)..s_End], @"-?\d+(?:[.,]\d+)?")
                .Select(p_M => float.Parse(p_M.Value.Replace(',', '.'),
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

            // Out on a flank and BELOW the deck: the skirt's anchors sit at y≈1.4 and the sprocket at y≈0.93,
            // and including them would drag the average off the belt's centre line.
            if (s_Numbers.Count >= 3 && System.Math.Abs(s_Numbers[0]) > 0.3f && s_Numbers[1] < 0.8f)
                s_Xs.Add(s_Numbers[0]);
        }

        var s_Left = s_Xs.Where(p_X => p_X < 0).ToList();
        var s_Right = s_Xs.Where(p_X => p_X > 0).ToList();

        // Four a side, or it is not a row of road wheels: a couple of odd anchors on both flanks would
        // otherwise be read as a track and hang a belt off a wheeled vehicle.
        if (s_Left.Count < 4 || s_Right.Count < 4)
            return null;

        var s_L = s_Left.Average();
        var s_R = s_Right.Average();
        return System.Math.Abs(s_L + s_R) < 0.15f * System.Math.Abs(s_R) ? (s_L, s_R) : null;
    }

    /// <summary>
    /// The shader named by one MESHDECL line, or null. Read as a FIELD rather than by looking for a word in
    /// the whole line: the line also carries the mesh's own path and its vertex elements, so a substring
    /// test answers about the wrong part of it as soon as a second family arrives.
    /// </summary>
    private static string? ShaderOfDecl(string p_Line)
    {
        var s_Match = System.Text.RegularExpressions.Regex.Match(p_Line, @"\bshader=(\S*)");
        return s_Match.Success ? s_Match.Groups[1].Value : null;
    }

    /// <summary>
    /// The scan's bindings of a mesh that belong to the material wearing a preset — by IDENTITY of the shader,
    /// never every material of the mesh in scan order.
    ///
    /// ⛔ MEASURED ON THE L85A2 (2026-09-18): its 1p mesh carries THREE preset materials — the body on
    /// ShadowFP (l85a2_d, CamoTiling 3, WearAmount 30), the side rail on FP and the rail's LOD on 3P
    /// (sightrail_d, CamoTiling 0, WearAmount 0, WearPower 0). Taking every binding of the mesh, last one
    /// wins, dressed the BODY in the rail's textures and collapsed its camo to tiling 0 — the studio showed a
    /// streaked, greyed L85A2 under every camo. The same rule the bake learnt on the M240: pair by the shader.
    /// The nearest material wins: the preset itself, then its camo/no-camo twin, then a preset of the same
    /// view family (3P / first person), then any weapon preset, then whatever the mesh has.
    /// </summary>
    private List<Cache.MaterialBinding> BindingsOf(string p_Mesh, string? p_Shader,
        Func<Cache.MaterialBinding, bool> p_Where, int? p_MaterialId = null)
    {
        var s_All = Scan.Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_Where(p_B)).ToList();

        // ⛔⛔ THE MATERIAL ID WINS OVER EVERY SHADER RULE BELOW, and it is the only thing that can answer
        // here: the rules under it pick the NEAREST SHADER, which cannot separate two materials wearing the
        // SAME one. Measured on the LAV-25 (2026-09-21): #2 main_M and #6 M_US_ATGM_Launchers are both
        // vehiclepreset_mud, the hull took the launchers' textures and the vehicle drew flat grey. On a
        // weapon that case is rare; on a vehicle it is the normal shape.
        if (p_MaterialId is { } s_Wanted)
        {
            var s_Exact = s_All.Where(p_B => p_B.MaterialId == s_Wanted).ToList();
            if (s_Exact.Count > 0)
                return s_Exact;
        }

        if (p_Shader == null || s_All.Count <= 1)
            return s_All;

        var s_Twin = CamoTwinOf(p_Shader);
        var s_Is3P = p_Shader.Contains("3p", StringComparison.OrdinalIgnoreCase);
        foreach (var s_Rule in new Func<Cache.MaterialBinding, bool>[]
                 {
                     p_B => p_B.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase),
                     p_B => string.Equals(CamoTwinOf(p_B.Shader), s_Twin, StringComparison.OrdinalIgnoreCase),
                     p_B => IsCamoPreset(p_B.Shader) && p_B.Shader.Contains("3p", StringComparison.OrdinalIgnoreCase) == s_Is3P,
                     p_B => IsCamoPreset(p_B.Shader),
                 })
        {
            var s_Nearest = s_All.Where(s_Rule).ToList();
            if (s_Nearest.Count > 0)
                return s_Nearest;
        }

        return s_All;
    }

    /// <summary>The material scan, loaded once and only if it is on disk — no mount happens here.</summary>
    private List<Cache.MaterialBinding> Scan => m_Scan ??= LoadScan();

    private List<Cache.MaterialBinding>? m_Scan;

    /// <summary>
    /// Both halves of the scan, parsed together: the weapons' and the vehicles'.
    ///
    /// ⛔ TWO FILES, ONE LIST. They are refreshed by different errands (a weapon cache run must not have
    /// to re-scan the vehicles, and the other way round), but every lookup downstream asks the same
    /// question — "what does the database bind to this mesh" — so a caller must never have to know which
    /// file the answer came from. Parsing them in one pass also collapses identical rows, the way the
    /// sibling databases of one level already do.
    /// </summary>
    private static List<Cache.MaterialBinding> LoadScan()
    {
        var s_Lines = new List<string>();
        foreach (var s_Name in new[] { "material_scan.txt", Cache.GameCache.VehicleScanFile, Cache.GameCache.SoldierScanFile })
        {
            var s_Path = System.IO.Path.Combine(Settings.CacheFolder, s_Name);
            if (System.IO.File.Exists(s_Path))
                s_Lines.AddRange(System.IO.File.ReadLines(s_Path));
        }

        return s_Lines.Count == 0
            ? new List<Cache.MaterialBinding>()
            : Cache.MaterialScan.Parse(s_Lines);
    }

    /// <summary>
    /// The textures a weapon should be previewed with, by sampler register.
    ///
    /// ⛔ THE SHADER'S OWN SLOT MAP IS NOT ENOUGH. It was built by scanning a LEVEL and holds only the 70
    /// meshes that scan happened to see — the M416 and 26 other weapons are simply not in it, so they fell
    /// back to the map's BASE set, which belongs to whichever weapon came first (the M4A1). That is both
    /// reported symptoms at once: another gun's art, stretched over these UVs.
    ///
    /// The material scan HAS all 348 meshes, so the sets are built from it. What the scan does not say is
    /// which REGISTER a parameter feeds — so that mapping is DERIVED by cross-referencing the meshes present
    /// in both, rather than assumed. Registers the weapon does not name (scratches, detail normal) keep the
    /// map's own values: they are shared by every weapon.
    /// </summary>
    /// <param name="p_MaterialId">
    /// Which material of the mesh to dress, when the caller knows: the game's own id. Two materials of one
    /// mesh can wear the SAME shader (a vehicle's hull and its launchers), and then nothing else separates
    /// their texture sets.
    /// </param>
    public Dictionary<string, string>? SlotsForMesh(string p_Mesh, string p_Shader, string? p_NativeCamo = null,
        int? p_MaterialId = null)
    {
        var s_Map = Cache.SlotMap.Load(p_Shader);

        // The material wearing THIS preset (see BindingsOf) — the L85A2's body, not its side rail; and when
        // the caller names a material id, THAT material, which is the only thing that separates two of them
        // wearing the same preset.
        // (the variation it is previewed in: 0 for a weapon or a vehicle, the default appearance's for a soldier — PreviewVariationOf)
        var s_Variation = PreviewVariationOf(p_Mesh, p_NativeCamo);
        var s_Own = BindingsOf(p_Mesh, p_Shader, p_B => p_B.Variation == s_Variation, p_MaterialId);

        // ⭐ NO TEXTURE BOUND TO THIS SHADER AND A MAP NOBODY WEARS: the shader's own art IS the subject's (the
        // F-35B — its databases bind textures to `proppreset_metal` only, never to `f35b_main`, and "Default
        // basic camo" drew it pink with no texture at all, 2026-09-22). ⛔ "No texture of THIS shader", not "no
        // row": the scan lists the mesh's other materials, and BindingsOf falls back to them (the wheels'
        // `us_f35b_parts_*` on the fuselage), and a textureless row of the material itself says nothing.
        // Taken AS THE MAP HAS IT: the map was built from the shader itself, and re-homing "unbound" assets
        // would move every one of them, since nothing binds any. Any other empty answer stays null.
        if (Cache.SlotMap.OwnArtOf(p_Shader) is { } s_OwnArt &&
            !s_Own.Any(p_B => p_B.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase) && p_B.Textures.Any()))
        {
            var s_Art = new Dictionary<string, string>(s_OwnArt.Slots!);
            OwnCamoSlot(p_Shader, s_Art, p_NativeCamo);
            return s_Art;
        }

        if (s_Own.Count == 0)
            return null;

        var s_Registers = RegistersFor(p_Shader, s_Map);

        // ⭐ …and when what the material binds is nothing this shader READS, its art is the shader's own too (2026-09-28, the eye of
        // head06: its material binds "Diffuse", characterroot_eye's bytecode samples only its two fixed eyeball textures — no register
        // for any bound parameter, so this used to answer nothing and the eye went unregistered as a material).
        if (s_Registers.Count == 0 && Cache.SlotMap.OwnArtOf(p_Shader) is { } s_OnlyOwn)
        {
            var s_Art = new Dictionary<string, string>(s_OnlyOwn.Slots!);
            OwnCamoSlot(p_Shader, s_Art, p_NativeCamo);
            return s_Art;
        }

        if (s_Registers.Count == 0)
            return null;

        var s_Slots = new Dictionary<string, string>(s_Map?.Slots ?? new Dictionary<string, string>());
        RehomeFixedSlots(p_Shader, s_Slots, s_Registers);

        foreach (var (s_Param, s_Texture) in s_Own.SelectMany(p_B => p_B.Textures))
            if (s_Registers.TryGetValue(s_Param, out var s_Register))
                s_Slots[s_Register] = s_Texture;

        // ⭐ ONE OF THE GAME'S OWN CAMOS AS THE STARTING POINT: its texture goes to the camo register, over
        // whatever the weapon ships with. The texture is shared by every weapon that wears it (the game
        // keeps the eight in one library), so this is the same art the game would draw.
        if (NativeCamoTexture(p_NativeCamo) is { } s_Native && s_Registers.TryGetValue("Camo", out var s_CamoRegister))
            s_Slots[s_CamoRegister] = s_Native;

        // ⭐ a soldier part previewed with its camo on ALL its cloth: the mask of its cloth is the skin's white one — what
        // the bake ships (the part's materials that bind CamoTile; the first-person trousers are the lower body's, by material)
        if (m_AllCloth.Count > 0 && s_Registers.TryGetValue("Mask", out var s_MaskRegister) &&
            s_Own.Any(p_B => p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase))) &&
            m_AllCloth.Contains(p_MaterialId is { } s_Id ? DocumentKeyForMaterial(p_Mesh, s_Id) : DocumentKeyFor(p_Mesh)) &&
            EnsureWhiteMaskPicture())
            s_Slots[s_MaskRegister] = WhiteMaskTexture;

        OwnCamoSlot(p_Shader, s_Slots, p_NativeCamo);
        return s_Slots;
    }

    /// <summary>
    /// ⭐ A vehicle's OWN camo shader (VehicleEntry.OwnCamoPresets — the Rhino's van body): its own art has nothing at the register its
    /// document samples the camo at, and in the game its copy reads there what its DONOR binds as "Camo" — the package's pattern, a native
    /// camo, or with neither the donor's own default (the mud's camodeserttan_01, CamoBaker.OwnCamoDonorOf). The preview puts the same there
    /// (a pattern chosen goes on top, as on every camo register). No-op for any other shader.
    /// </summary>
    private void OwnCamoSlot(string p_Shader, Dictionary<string, string> p_Slots, string? p_NativeCamo)
    {
        if (VehicleCatalog.OwnCamoRegisterOf(p_Shader) is not { } s_Register)
            return;

        var s_At = s_Register.ToString();
        if (NativeCamoTexture(p_NativeCamo) is { } s_Native)
            p_Slots[s_At] = s_Native;
        else if (!p_Slots.ContainsKey(s_At) && CamoBaker.OwnCamoDonorOf(p_Shader) is { } s_Donor &&
                 RegistersFor(s_Donor, Cache.SlotMap.Load(s_Donor)).GetValueOrDefault("Camo") is { } s_DonorCamo &&
                 Cache.SlotMap.Load(s_Donor)?.Slots?.GetValueOrDefault(s_DonorCamo) is { Length: > 0 } s_Default)
            p_Slots[s_At] = s_Default;
    }

    /// <summary>
    /// The eight camos the game itself has, MEASURED from the material scan: exactly these eight textures
    /// are bound by the weapons' own camo variations (19 premium weapons, two each), all from the one shared
    /// library. Key = the texture's own name; the display name is the game's, as far as it is known.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Display, string Texture)> NativeCamos = new[]
    {
        ("abu", "ABU", "characters/shared/xp2_clothcamo/abu"),
        ("atacs", "A-TACS", "characters/shared/xp2_clothcamo/atacs"),
        ("berkut", "Berkut", "characters/shared/xp2_clothcamo/berkut"),
        ("digiflora", "Digital Flora", "characters/shared/xp2_clothcamo/digiflora"),
        ("dsrttiger", "Desert Tiger", "characters/shared/xp2_clothcamo/dsrttiger"),
        ("kamysh", "Kamysh", "characters/shared/xp2_clothcamo/kamysh"),
        ("nwu", "NWU", "characters/shared/xp2_clothcamo/nwu"),
        ("partizan", "Partizan", "characters/shared/xp2_clothcamo/partizan"),
    };

    /// <summary>
    /// The picker's entry between "as shipped" and the eight: the camo preset with the weapon's OWN camo
    /// texture where it ships one and the preset's default pattern where it does not — what every weapon
    /// used to preview as by default, and what a package with no pattern and no native camo puts in the
    /// game (since 2026-09-11 the default is the weapon with NO camo, and this look moved to the
    /// picker as "Default basic camo"). It stands for no texture of its own, so everything keyed by
    /// texture (slots, values, the bake) treats it exactly as "no native camo".
    /// </summary>
    public const string BasicCamoKey = "basic";

    public const string BasicCamoDisplay = "Default basic camo";

    /// <summary>The texture a native camo key stands for; null for no key, the basic key or an unknown one.</summary>
    public static string? NativeCamoTexture(string? p_Key) =>
        string.IsNullOrWhiteSpace(p_Key)
            ? null
            : NativeCamos.FirstOrDefault(p_C => p_C.Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase)).Texture;

    /// <summary>
    /// Whether a weapon has the game's own variation of a native camo — the 19 premium weapons have two
    /// each — in which case the values it was tuned with (its own tiling and wear) exist to be used.
    /// </summary>
    public bool HasNativeVariation(string p_Mesh, string p_NativeCamo) =>
        NativeCamoTexture(p_NativeCamo) is { } s_Texture &&
        Scan.Any(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation != 0 &&
                        p_B.Textures.Any(p_T => p_T.Param.Equals("Camo", StringComparison.OrdinalIgnoreCase) &&
                                                p_T.Texture.Equals(s_Texture, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The material's own numbers for a weapon — CamoTiling, DiffuseDarkening, the smoothness set.
    ///
    /// ⛔ NOT DECORATION. The weapon presets build their albedo FROM these before sampling any texture, so a
    /// weapon previewed without them draws flat green no matter how much art is loaded. Formatted the way
    /// the editor's constant-buffer path expects: "x,y,z,w".
    /// </summary>
    public Dictionary<string, string>? ValuesForMesh(string p_Mesh, string? p_Shader = null,
        string? p_NativeCamo = null, int? p_MaterialId = null)
    {
        var s_Shader = p_Shader ?? ShaderForMesh(p_Mesh);

        // The material wearing THIS preset (see BindingsOf): the L85A2's rail sets CamoTiling 0 / WearAmount 0
        // / WearPower 0 on its own materials, and those must never land on the body. With a material id, that
        // material's own numbers — two materials on one preset have different ones.
        var s_Shown = PreviewVariationOf(p_Mesh, p_NativeCamo);
        var s_Bindings = BindingsOf(p_Mesh, s_Shader, p_B => p_B.Variation == s_Shown, p_MaterialId);

        // ⭐ a vehicle's OWN camo shader (VehicleEntry.OwnCamoPresets — the Rhino's van body): the camo numbers its document adds (CamoTiling,
        // WearAmount…) are not its own, and in the game its copy runs as its donor's entry, which fills a number the package does not set with
        // ITS default (CamoBaker.OwnCamoDonorOf) — so the preview starts from the same, under the shader's own
        Dictionary<string, string>? DonorDefaults() =>
            CamoBaker.OwnCamoDonorOf(s_Shader) is { } s_Donor && Cache.SlotMap.Load(s_Donor)?.ExternalDefaults is { } s_Theirs
                ? new Dictionary<string, string>(s_Theirs, StringComparer.OrdinalIgnoreCase)
                : null;

        // The same shader-owned case as SlotsForMesh: nothing binds it, so its numbers are the shader's own.
        if (s_Bindings.Count == 0)
        {
            if (Cache.SlotMap.OwnArtOf(s_Shader)?.ExternalDefaults is not { } s_OwnDefaults)
                return null;

            var s_Own = DonorDefaults() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Name, s_Default) in s_OwnDefaults)
                s_Own[s_Name] = s_Default;
            return s_Own;
        }

        // ⛔ START FROM THE SHADER'S DEFAULTS. A material lists only what it OVERRIDES, so a weapon that
        // never mentions DiffuseDarkening (the Jackhammer) would leave that constant unwritten — and the
        // albedo is built from it before any texture is read, so the weapon drew flat green while its
        // neighbours, which happen to override it, looked right.
        var s_Values = DonorDefaults() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s_Shader != null && Cache.SlotMap.Load(s_Shader)?.ExternalDefaults is { } s_Defaults)
            foreach (var (s_Name, s_Default) in s_Defaults)
                s_Values[s_Name] = s_Default;

        static string Text(float[] p_Value) => string.Join(",", p_Value.Select(p_C =>
            p_C.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        foreach (var (s_Param, s_Value) in s_Bindings.SelectMany(p_B => p_B.Vectors))
            s_Values[s_Param] = Text(s_Value);

        // ⭐ A NATIVE CAMO BRINGS THE NUMBERS THE GAME TUNED IT WITH — when this weapon has that camo as one
        // of its own variations (the scan lists the variation's values after the material's, so the later
        // ones win, exactly as the game applies them). A weapon without that variation keeps its own
        // numbers under the borrowed pattern: there is nothing measured to replace them with.
        if (NativeCamoTexture(p_NativeCamo) is { } s_Texture)
            foreach (var s_Variation in BindingsOf(p_Mesh, s_Shader, p_B =>
                         p_B.Variation != 0 &&
                         p_B.Textures.Any(p_T => p_T.Param.Equals("Camo", StringComparison.OrdinalIgnoreCase) &&
                                                 p_T.Texture.Equals(s_Texture, StringComparison.OrdinalIgnoreCase))))
            foreach (var (s_Param, s_Value) in s_Variation.Vectors)
                s_Values[s_Param] = Text(s_Value);

        return s_Values;
    }

    /// <summary>
    /// A constant as THIS WEAPON declares it — its own material, with the tuning of the native camo in play
    /// on top when the weapon really has that camo — or null when the weapon declares none.
    ///
    /// ⛔ THE SHADER'S DEFAULTS ARE DELIBERATELY LEFT OUT, and that is the whole point of this next to
    /// <see cref="ValuesForMesh"/>: a default is shared by every weapon wearing the preset, so handing it
    /// back as "the weapon's own" is how a shared 4 passes for a measurement. Measured 2026-09-20 over the
    /// 59 weapons: the game's own camos run tiling 4 on 36 of them and 3 on 15, with the spread going from
    /// -3.5 to 5 (F2000 0.3, AK74M 2) — numbers like those are the weapon's, a flat 4 usually is not.
    /// The rule: the tiling is the weapon's own, and a weapon without that camo gets 3 — so null here
    /// means the caller uses <see cref="Project.Camo.DefaultTiling"/>.
    /// </summary>
    public string? WeaponOwnValue(string p_Mesh, string p_Name, string? p_Shader = null, string? p_NativeCamo = null)
    {
        var s_Shader = p_Shader ?? ShaderForMesh(p_Mesh);

        static string Text(float[] p_Value) => string.Join(",", p_Value.Select(p_C =>
            p_C.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        string? s_Found = null;
        var s_Shown = PreviewVariationOf(p_Mesh, p_NativeCamo);
        foreach (var (s_Param, s_Value) in BindingsOf(p_Mesh, s_Shader, p_B => p_B.Variation == s_Shown)
                     .SelectMany(p_B => p_B.Vectors))
            if (s_Param.Equals(p_Name, StringComparison.OrdinalIgnoreCase))
                s_Found = Text(s_Value);

        // The game's tuning for THIS weapon under THIS camo wins over its plain material, the same order the
        // engine applies them; a weapon that does not carry that camo has nothing here and keeps its own.
        if (NativeCamoTexture(p_NativeCamo) is { } s_Texture)
            foreach (var s_Variation in BindingsOf(p_Mesh, s_Shader, p_B =>
                         p_B.Variation != 0 &&
                         p_B.Textures.Any(p_T => p_T.Param.Equals("Camo", StringComparison.OrdinalIgnoreCase) &&
                                                 p_T.Texture.Equals(s_Texture, StringComparison.OrdinalIgnoreCase))))
            foreach (var (s_Param, s_Value) in s_Variation.Vectors)
                if (s_Param.Equals(p_Name, StringComparison.OrdinalIgnoreCase))
                    s_Found = Text(s_Value);

        // One number when the vector says the same thing twice, which is how the form and the boxes read it.
        if (s_Found == null)
            return null;

        var s_First = s_Found.Split(',')[0].Trim();
        return string.Equals(ExpandValue(p_Name, s_First), s_Found, StringComparison.Ordinal) ? s_First : s_Found;
    }

    /// <summary>Parameter name -> sampler register, measured from the meshes both sources describe.</summary>
    /// <summary>The register ("2") a shader samples one of its material parameters ("Mask") at, as the preview binds it; null when unknown.</summary>
    public string? RegisterOfParameter(string p_Shader, string p_Parameter) =>
        RegistersFor(p_Shader, Cache.SlotMap.Load(p_Shader)).GetValueOrDefault(p_Parameter);

    private Dictionary<string, string> RegistersFor(string p_Shader, Cache.SlotMap? p_Map)
    {
        if (m_Registers.TryGetValue(p_Shader, out var s_Cached))
            return s_Cached;

        var s_Votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Variation in p_Map?.Variations?.Values ?? Enumerable.Empty<Cache.SlotVariation>())
        {
            if (!string.Equals(s_Variation.AssetName, "(base)", StringComparison.OrdinalIgnoreCase) ||
                s_Variation.Slots == null)
                continue;

            var s_ByTexture = s_Variation.Slots
                .GroupBy(p_S => p_S.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Key, StringComparer.OrdinalIgnoreCase);

            foreach (var s_Binding in Scan.Where(p_B =>
                         p_B.Mesh.Equals(s_Variation.Mesh, StringComparison.OrdinalIgnoreCase) &&
                         p_B.Variation == 0))
            foreach (var (s_Param, s_Texture) in s_Binding.Textures)
            {
                if (!s_ByTexture.TryGetValue(s_Texture, out var s_Register))
                    continue;

                if (!s_Votes.TryGetValue(s_Param, out var s_Counts))
                    s_Votes[s_Param] = s_Counts = new Dictionary<string, int>();

                s_Counts[s_Register] = s_Counts.GetValueOrDefault(s_Register) + 1;
            }
        }

        // The winner per parameter. A tie would mean the two sources disagree, which has not happened —
        // Diffuse/Camo/Specular each landed on one register with dozens of votes and no runner-up.
        var s_Result = s_Votes.ToDictionary(
            p_V => p_V.Key,
            p_V => p_V.Value.OrderByDescending(p_C => p_C.Value).First().Key,
            StringComparer.OrdinalIgnoreCase);

        // ⛔ NO VOTES IS NOT "NO REGISTER". A slot map built where nobody wears the shader has no variations
        // to vote with (the Stryker's `vehiclepreset1uvset_mud_allcamo` and `_decals`: MeshUsers 0), and a
        // parameter without a register silently dropped the material's own art — "the studio names no
        // texture set for it", the armour drawn with the slot map's one scratch texture (2026-09-22). The
        // bytecode NAMES the register of every external texture (texture_Diffuse is t1…), so it is read
        // there, with zero inference, for EVERY parameter it names; the vote covers what it does not.
        // ⛔ THE BYTECODE GOES FIRST, NOT ONLY WHERE THE VOTE IS EMPTY (2026-09-22, F/A-18F): the vote pairs a
        // parameter with the register that holds ITS TEXTURE, so two parameters bound to the SAME texture are
        // indistinguishable to it — the jet family binds camomarinegrey_01 to CamoA (t3) and CamoB (t2)
        // alike, and the vote put both on t2. The pattern then only ever reached CamoB, which the jet shader
        // reads where V < 0 (46 % of the F/A-18F's hull by vertex count); the other half kept the game's grey.
        var s_Asked = Scan.Where(p_B => p_B.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p_B => p_B.Textures)
            .Select(p_T => p_T.Param)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (s_Asked.Count > 0 && ContractOf(p_Shader) is { } s_Contract)
            foreach (var s_Param in s_Asked)
            {
                var s_Register = s_Contract.RegisterForExternalTexture(s_Param);
                if (s_Register > 0)
                    s_Result[s_Param] = s_Register.ToString();
            }

        // ⭐ a shader of a vehicle's OWN that takes the camo although its bytecode reads none (VehicleEntry.OwnCamoPresets — the Rhino's van
        // body): its camo document samples the pattern at the register the catalogue names — where the studio puts the
        // pattern, and what the bake binds as the donor's Camo (CamoBaker.DeriveTwinRetarget)
        if (VehicleCatalog.OwnCamoRegisterOf(p_Shader) is { } s_OwnCamo && !s_Result.ContainsKey("Camo"))
            s_Result["Camo"] = s_OwnCamo.ToString();

        m_Registers[p_Shader] = s_Result;
        return s_Result;
    }

    private readonly Dictionary<string, Dictionary<string, string>> m_Registers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cached pixel shader's contract (its RDEF names every texture register), or null without a dump.</summary>
    /// <summary>Every texture register a preset's pixel stage reads (its parameters, its fixed and its engine textures) — what a register
    /// "the preset has nothing at" is measured against.</summary>
    internal IReadOnlyCollection<int> TextureRegistersOf(string p_Shader) =>
        (ContractOf(p_Shader)?.Resources.Where(p_R => p_R.IsTexture).Select(p_R => p_R.Register) ?? Enumerable.Empty<int>())
        .Concat(RegistersFor(p_Shader, Cache.SlotMap.Load(p_Shader)).Values.Select(p_V => int.TryParse(p_V, out var s_R) ? s_R : 0))
        .Where(p_R => p_R > 0)
        .ToHashSet();

    private RimeShaderEditor.Emit.ShaderContract? ContractOf(string p_Shader)
    {
        if (m_Contracts.TryGetValue(p_Shader, out var s_Known))
            return s_Known;

        RimeShaderEditor.Emit.ShaderContract? s_Contract = null;
        var s_Path = Cache.GameCache.ShaderFile(p_Shader);
        if (System.IO.File.Exists(s_Path))
            try { s_Contract = RimeShaderEditor.Emit.ShaderContract.Detect(System.IO.File.ReadAllBytes(s_Path)); }
            catch (Exception) { /* a bytecode that does not parse answers nothing */ }

        m_Contracts[p_Shader] = s_Contract;
        return s_Contract;
    }

    private readonly Dictionary<string, RimeShaderEditor.Emit.ShaderContract?> m_Contracts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the slot map's FIXED textures (the scratch mask, a detail normal: assets no material instance ever
    /// binds) on the registers the bytecode names for them.
    ///
    /// ⛔ A slot map built where nobody wears the shader (MeshUsers 0 — the Stryker's `_allcamo` and `_decals`)
    /// lists its fixed texture at register 1, which the bytecode names `texture_Diffuse`: the diffuse then
    /// overwrote it and the scratch mask was bound nowhere. A healthy map keeps such an asset on the register
    /// the RDEF calls `texture_TextureN` (VehicleScratches_m at t3 under `1uvset_mud`), so that invariant is
    /// restored here — derived from the bytecode, never from a list of asset names (2026-09-22).
    /// </summary>
    private void RehomeFixedSlots(string p_Shader, Dictionary<string, string> p_Slots, Dictionary<string, string> p_Registers)
    {
        if (p_Slots.Count == 0 || ContractOf(p_Shader) is not { } s_Contract)
            return;

        var s_External = new HashSet<string>(p_Registers.Values, StringComparer.OrdinalIgnoreCase);
        var s_Bound = new HashSet<string>(
            Scan.Where(p_B => p_B.Shader.Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
                .SelectMany(p_B => p_B.Textures)
                .Select(p_T => p_T.Texture),
            StringComparer.OrdinalIgnoreCase);
        var s_Generic = s_Contract.Resources
            .Where(p_R => p_R.IsTexture && p_R.Name.StartsWith("texture_Texture", StringComparison.OrdinalIgnoreCase))
            .Select(p_R => p_R.Register.ToString())
            .OrderBy(p_R => int.Parse(p_R))
            .ToList();

        foreach (var (s_Register, s_Asset) in p_Slots.OrderBy(p_S => int.TryParse(p_S.Key, out var s_N) ? s_N : 0).ToList())
        {
            // A fixed asset is one no instance binds; misplaced when it sits where the bytecode expects a parameter.
            if (s_Bound.Contains(s_Asset) || !s_External.Contains(s_Register))
                continue;

            var s_Home = s_Generic.FirstOrDefault(p_G => !p_Slots.ContainsKey(p_G));
            if (s_Home == null)
                continue;

            p_Slots.Remove(s_Register);
            p_Slots[s_Home] = s_Asset;
        }
    }

    public Hook? HookOf(Camo p_Camo) => m_Hooks.Find(p_Camo.Identifier);

    public int FreeHooks => m_Hooks.Free;

    /// <summary>
    /// The camo on the canvas, turned into a package plan — or null with the reason in <paramref name="p_Error"/>.
    /// Pure: reads the catalogue, the scan and the caches; nothing is written.
    /// </summary>
    /// <param name="p_Name">The camo's name (the graph name): what the package, its variations and its row are called.</param>
    /// <param name="p_PatternImage">The user's image on the camo texture node, or null.</param>
    /// <param name="p_NativeKey">The native camo the graph starts from, or null; used when there is no image.</param>
    /// <param name="p_ThumbnailImage">The user's picture for the menu row, or null to cut it from the pattern.</param>
    /// <param name="p_Overrides">The graph's preview values, bare constant name -> "x,y,z,w".</param>
    /// <param name="p_WeaponFolders">Catalogue folders to offer the camo on; empty = every weapon.</param>
    /// <param name="p_ShaderGraphPath">A saved graph whose logic the package ships as its own first-person
    /// preset (see BakePlan.ShaderGraphPath), or null for the game's presets.</param>
    public BakePlan? PlanFor(string p_Name, string p_Description, string? p_PatternImage, string? p_NativeKey,
        string? p_ThumbnailImage, IReadOnlyDictionary<string, string> p_Overrides,
        IReadOnlyCollection<string> p_WeaponFolders, out string? p_Error, string? p_ShaderGraphPath = null,
        StickerRequest? p_Stickers = null, string? p_NoCamoShaderGraphPath = null,
        IReadOnlyDictionary<string, List<int>>? p_MaterialsOff = null,
        IReadOnlyList<MaterialShaderRequest>? p_MaterialShaders = null,
        IReadOnlyCollection<string>? p_Accessories = null,
        RimeShaderEditor.Graph.ShaderGraph? p_Document = null,
        bool p_PaintBody = true,
        IReadOnlyCollection<string>? p_Vehicles = null,
        string? p_VehicleGraphTarget = null,
        IReadOnlyDictionary<string, string>? p_VehicleGraphs = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? p_VehicleValues = null,
        SubjectDocuments? p_Subjects = null,
        IReadOnlyList<ShippedShaderRequest>? p_ShippedShaders = null,
        string? p_OwnShaderPreset = null,
        IReadOnlyList<MaterialPictureRequest>? p_MaterialPictures = null,
        IReadOnlyDictionary<string, string>? p_VehicleTargets = null)
    {
        p_Error = null;
        var s_Name = p_Name.Trim();

        // Stickers only exist in the package's own shader: a plan without one ships no layer.
        var s_Stickers = p_ShaderGraphPath != null && p_Stickers?.Register != null ? p_Stickers : null;
        if (p_Stickers?.Register != null && p_ShaderGraphPath == null)
        {
            p_Error = "the graph samples a sticker layer but ships no shader of its own — the stickers would have nothing to draw them.";
            return null;
        }

        // ⭐ a PROJECTED emblem slot in any graph the package compiles: each weapon's frames ride in its own variation (BakeWeapon.EmblemProjection)
        var s_ProjectsSlot = GraphsProjectingSlot(new[] { p_ShaderGraphPath, p_NoCamoShaderGraphPath }
            .Concat((p_ShippedShaders ?? Array.Empty<ShippedShaderRequest>()).Select(p_S => p_S.GraphPath))).Count > 0;

        if (!IsCamoName(s_Name))
        {
            p_Error = "name the camo first — the name is what the package, its variations and its row are called.";
            return null;
        }

        // No pattern and no native camo is allowed: the package then carries values only (the user
        // may use no textures at all, or only change values) — see BakePlan.ValuesOnly.
        var s_Native = NativeCamoTexture(p_NativeKey);

        if (p_PatternImage != null && !File.Exists(p_PatternImage))
        {
            p_Error = $"the pattern image '{p_PatternImage}' no longer exists.";
            return null;
        }

        if (p_ThumbnailImage != null && !File.Exists(p_ThumbnailImage))
        {
            p_Error = $"the thumbnail image '{p_ThumbnailImage}' no longer exists.";
            return null;
        }

        var s_Camo = Current(s_Name);
        s_Camo.Description = p_Description;
        s_Camo.Weapons = new List<string>(p_WeaponFolders);

        // A camo OF A PIECE has no row of its own in any weapon's menu -- it is picked in the piece's window --
        // so the row pool has nothing to say about it.
        if (s_Camo.Identifier == 0 && p_PaintBody)
        {
            p_Error = $"no free menu row for '{s_Name}' in the kits these weapons belong to — every borrowable description is spent there " +
                      $"({m_Hooks.FreeSlots} kit slot(s) left on {m_Hooks.Total} rows; a camo on a shotgun, a PDW or every weapon needs a row free in all four kits).";
            return null;
        }

        var s_Chosen = m_Weapons.Weapons
            .Where(p_W => p_WeaponFolders.Count == 0 || p_WeaponFolders.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var s_Weapons = new List<BakeWeapon>();
        var s_AnyAnimated = false;
        // (the values typed that the package cannot carry, said by the bake — BakePlan.ValuesNotBaked)
        var s_NotBaked = new List<string>();
        foreach (var s_Weapon in s_Chosen)
        {
            var s_Meshes = s_Weapon.BodyMeshes;
            var s_Preview = s_Weapon.PreviewMesh;
            if (s_Meshes.Count == 0 || s_Preview == null)
                continue;

            // The weapon's own numbers under the camo's overrides — the same merge the preview shows, so what
            // was looked at is what ships. Engine-fed constants are never written into a variation.
            var s_Values = ValuesForMesh(s_Preview, ShaderForMesh(s_Preview), p_NativeKey)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ⭐ this weapon's OWN document (one document per subject): the values typed on it, its materials kept off
            var s_Own = p_Subjects?.DocumentOf(s_Preview);
            var s_Typed = s_Own != null ? p_Subjects!.ValuesOf(s_Own) : p_Overrides;

            var s_Overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Constant, s_Vector) in s_Typed)
                if (WeaponConstants.Find(s_Constant) is { Kind: not ConstantKind.Engine } s_Known &&
                    !string.IsNullOrWhiteSpace(s_Vector))
                {
                    // A graph's value is ONE number when the user typed one; it ships as the vector it
                    // stands for (a tiling in x AND y), the same expansion the preview fed — never as
                    // (n,0,0,0), which on a tiling is stripes in-game.
                    var s_Text = s_Vector.Trim();
                    s_Values[s_Known.Name] = s_Text.Contains(',') ? s_Text : ExpandValue(s_Known.Name, s_Text);
                    s_Overridden.Add(s_Known.Name);
                }
                // (a constant the measured table does not know is left out — said: the preview shows it; review 2026-09-29, C7)
                else if (WeaponConstants.Find(s_Constant) == null && !string.IsNullOrWhiteSpace(s_Vector))
                    s_NotBaked.Add($"{s_Constant} ({s_Weapon.Folder})");

            // Every texture the meshes' entries bind, base and shipped variations alike (the copies bring
            // them along, and one left behind draws the weapon's other skins white).
            var s_Textures = Scan
                .Where(p_B => s_Meshes.Contains(p_B.Mesh, StringComparer.OrdinalIgnoreCase))
                .SelectMany(p_B => p_B.Textures.Select(p_T => p_T.Texture))
                .Where(p_T => !string.IsNullOrWhiteSpace(p_T))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_T => p_T, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The weapon's sticker layer: its placements (keyed by the mesh the studio previews) composed
            // into its texture space, written as the file the baker converts. None placed = no layer; the
            // baker binds the package's empty one.
            var s_Layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var s_Sides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var s_Projection = "";
            if (s_Stickers != null)
            {
                // (an emblem slot still being placed by hand rides in the list as "#emblem" placements: it is the slot, not a sticker)
                var s_EditedSlot = s_Stickers.Placements
                    .Where(p_S => p_S.Mesh.Equals(s_Preview, StringComparison.OrdinalIgnoreCase) && RimeShaderEditor.Graph.EmblemSlot.IsEmblem(p_S))
                    .ToList();
                var s_Placed = s_Stickers.Placements
                    .Where(p_S => p_S.Mesh.Equals(s_Preview, StringComparison.OrdinalIgnoreCase) && !RimeShaderEditor.Graph.EmblemSlot.IsEmblem(p_S))
                    .ToList();
                var s_Still = s_Placed.Where(p_S => !RimeShaderEditor.Graph.StickerGraph.IsAnimated(p_S.Image)).ToList();
                var s_Animated = s_Stickers.Atlas == null
                    ? new List<RimeShaderEditor.Graph.StickerPlacement>()
                    : s_Placed.Where(p_S => p_S.Image.Equals(s_Stickers.Animation, StringComparison.OrdinalIgnoreCase)).ToList();

                // The placements name a spot on the body the studio previews (the 1P): resolved there
                // into a decal in the weapon's own space, then painted onto EACH body mesh with that
                // mesh's unwrap — the 3P has its own (the F2000's sides mapped in mirror, measured
                // in-game 2026-09-12). No dump = drawn flat in texture space, and said so.
                var s_FrameBody = s_Still.Count > 0 || s_Animated.Count > 0 || s_Stickers.Emblem
                    ? RimeShaderEditor.Graph.StickerSurface.LoadRsm3(Cache.GameCache.MeshFile(s_Preview), IsCamoPreset)
                    : null;

                // ⭐ the emblem slot (EmblemSlot): found on this weapon's own body, both sides of the receiver, and composed into the
                // side and coordinate maps like an animated sticker — beside a GIF on this weapon only when PROJECTED (it reads no map then;
                // the GIF keeps the coordinates: StickerGraph.GifKeepsEmblem — whatever happens, the emblem slot stays)
                // (placed by hand: the "#emblem" placements still in the list, else the weapon's saved slot; the automatic spot otherwise)
                var s_Emblem = s_Stickers.Emblem && (s_Animated.Count == 0 || s_ProjectsSlot) && s_FrameBody != null
                    ? s_EditedSlot.Count > 0
                        ? s_EditedSlot
                        : RimeShaderEditor.Graph.EmblemSlot.PlacementsFor(s_FrameBody, s_Preview, RimeShaderEditor.Graph.EmblemSlot.DefaultSizeMetres, s_Stickers.Log)
                    : new List<RimeShaderEditor.Graph.StickerPlacement>();
                if (s_Stickers.Emblem && s_FrameBody == null)
                    s_Stickers.Log?.Invoke($"emblem slot: no cached mesh for {s_Weapon.Folder} — no emblem slot on it.");

                // ⭐ the same slot as frames in the body's own space, for a package whose shader PROJECTS it from the mesh position
                // (EmblemSlot.Projected): the weapon's own frame constants, from the frames the coordinate map below is composed with
                if (s_ProjectsSlot && s_Emblem.Count > 0 && s_FrameBody != null)
                {
                    s_Projection = RimeShaderEditor.Graph.EmblemSlot.ProjectionSlots(s_Emblem, s_FrameBody);

                    // ⛔ …placed on the weapon ASSEMBLED, read by the game in the BIND pose: each square moved with its part (EmblemSlot.InBindPose;
                    // a pistol's slide sits 26-30 cm away there — the M9's squares drew nothing, first or third person, run 368)
                    var s_BindBody = RimeShaderEditor.Graph.StickerSurface.LoadRsm3(Cache.GameCache.BindMeshFile(s_Preview), IsCamoPreset);
                    if (s_BindBody != null)
                        s_Projection = RimeShaderEditor.Graph.EmblemSlot.InBindPose(s_Projection, s_FrameBody, s_BindBody, s_Weapon.Folder, s_Stickers.Log);
                    else
                        s_Stickers.Log?.Invoke($"emblem slot: NOTE no bind-pose dump of {s_Preview.Split('/')[^1]} ('{Cache.GameCache.BindMeshFile(s_Preview)}'): " +
                                               $"{s_Weapon.Folder}'s squares go as placed — right on a part the idle pose leaves alone (a rifle's receiver), " +
                                               "nowhere on one it moves (a pistol's slide). --cache dumps it.");
                    var s_Squares = s_Projection.Length == 0 ? 0 : s_Projection.Split(';').Length;
                    if (s_Squares > RimeShaderEditor.Graph.EmblemSlot.ProjectedSquares)
                        s_Stickers.Log?.Invoke($"emblem slot: {s_Weapon.Folder} has {s_Squares} squares and a projected slot holds " +
                                               $"{RimeShaderEditor.Graph.EmblemSlot.ProjectedSquares} — the first {RimeShaderEditor.Graph.EmblemSlot.ProjectedSquares} ship.");
                }

                if (s_Still.Count > 0 || s_Animated.Count > 0 || s_Emblem.Count > 0)
                {
                    if (s_FrameBody == null)
                        s_Stickers.Log?.Invoke($"stickers: no cached mesh for {s_Weapon.Folder} — its stickers are composed flat, as rectangles in texture space.");

                    var s_Side = s_Stickers.Side;
                    foreach (var s_Mesh in s_Meshes)
                    {
                        var s_IsPreview = s_Mesh.Equals(s_Preview, StringComparison.OrdinalIgnoreCase);
                        var s_Body = s_IsPreview ? s_FrameBody : RimeShaderEditor.Graph.StickerSurface.LoadRsm3(Cache.GameCache.MeshFile(s_Mesh), IsCamoPreset);
                        if (!s_IsPreview && s_Body == null)
                        {
                            s_Stickers.Log?.Invoke($"stickers: {s_Weapon.Folder}'s '{s_Mesh.Split('/')[^1]}' is not cached — it wears the 1P's layer (run the studio's cache step to compose it on its own unwrap).");
                            continue;
                        }

                        var s_Tag = CamoBaker.MeshTag(s_Mesh);

                        // a projected slot is ONE set of frames, in the previewed body's space, drawn on every body mesh: a third-person
                        // model laid out elsewhere would get the square somewhere else (the M416's is the 1P's, vertex for vertex) — measured
                        if (!s_IsPreview && s_Projection.Length > 0 && s_Body != null && s_FrameBody != null)
                        {
                            var (s_FrameMin, s_FrameMax) = s_FrameBody.Bounds();
                            var (s_BodyMin, s_BodyMax) = s_Body.Bounds();
                            var s_Apart = Math.Max((s_BodyMin - s_FrameMin).Length(), (s_BodyMax - s_FrameMax).Length()) / s_Body.RawScale;
                            s_Stickers.Log?.Invoke($"emblem slot: {s_Weapon.Folder}'s '{s_Mesh.Split('/')[^1]}' is projected with the 1P's frames — " +
                                                   $"its bounds lie {s_Apart * 100f:0.##} cm from the 1P's" +
                                                   (s_Apart > 0.005f ? " ⚠ a different layout: the square may land elsewhere on it." : "."));
                        }

                        if (s_Still.Count > 0)
                        {
                            var s_Composed = RimeShaderEditor.Graph.StickerLayer.Compose(s_Still, s_Stickers.ImageOf, s_Side, s_Body, s_FrameBody);
                            var s_Layer = Path.Combine(s_Stickers.WorkDir, $"{CamoBaker.FolderOf(s_Weapon.Folder)}_{s_Tag}.tga");
                            RimeShaderEditor.Graph.StickerLayer.SaveTga(s_Composed, s_Layer);
                            s_Layers[s_Mesh] = s_Layer;
                        }

                        // The side map of every placement on this mesh (which half of a mirrored unwrap
                        // each was painted on — the F2000's sides share one patch, measured in-game),
                        // packed with the animated sticker's x coordinate in its alpha; y in its own
                        // texture's alpha. The same projection as the layer; the shader reads the current
                        // frame of the sheet where the coordinates say.
                        // The shipped maps store the geometry's own hand, which the game reads in first and
                        // third person alike (measured 2026-09-12, F2000, "VU P2"). CAMO_SIDE_FLIP names mesh
                        // tags whose map stores the opposite hand — a bisection knob, empty by default.
                        var s_FlipTags = (Environment.GetEnvironmentVariable("CAMO_SIDE_FLIP") ?? "")
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var s_MirrorHand = s_FlipTags.Contains(s_Tag, StringComparer.OrdinalIgnoreCase);
                        var s_SideMap = RimeShaderEditor.Graph.StickerLayer.ComposeSideMap(s_Placed.Concat(s_Emblem),
                            RimeShaderEditor.Graph.EmblemSlot.ImageOf(s_Stickers.ImageOf), s_Side, s_Body, s_FrameBody, s_MirrorHand);
                        if (s_MirrorHand)
                            s_Stickers.Log?.Invoke($"stickers: {s_Weapon.Folder}'s {s_Tag} side map stores the opposite hand (bisection).");
                        var s_Coordinates = s_Animated.Count > 0
                            ? RimeShaderEditor.Graph.StickerLayer.ComposeMap(s_Animated, s_Stickers.Atlas!, s_Side, s_Body, s_FrameBody)
                            : s_Emblem.Count > 0
                                ? RimeShaderEditor.Graph.StickerLayer.ComposeMap(s_Emblem, RimeShaderEditor.Graph.EmblemSlot.Picture, s_Side, s_Body, s_FrameBody)
                                : null;
                        var s_Packed = RimeShaderEditor.Graph.StickerLayer.PackMaps(s_SideMap, s_Coordinates);
                        var s_MapPath = Path.Combine(s_Stickers.WorkDir, $"{CamoBaker.FolderOf(s_Weapon.Folder)}_{s_Tag}_map.tga");
                        RimeShaderEditor.Graph.StickerLayer.SaveTga(s_Packed, s_MapPath);
                        s_Sides[s_Mesh] = s_MapPath;
                        if (s_Coordinates != null)
                            s_AnyAnimated = true;
                    }

                    // A body mesh without a dump wears the 1P's textures — the look every package had before.
                    foreach (var s_Mesh in s_Meshes.Where(p_M => !p_M.Equals(s_Preview, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!s_Layers.ContainsKey(s_Mesh) && s_Layers.TryGetValue(s_Preview, out var s_L))
                            s_Layers[s_Mesh] = s_L;
                        if (!s_Sides.ContainsKey(s_Mesh) && s_Sides.TryGetValue(s_Preview, out var s_S))
                            s_Sides[s_Mesh] = s_S;

                    }
                }
            }

            // Whether the weapon's own entries bind Camo at all (a NoCamo preset binds none): the baker
            // needs to know when the package brings no pattern but a shader that samples it.
            var s_HasCamo = Scan.Any(p_B => s_Meshes.Contains(p_B.Mesh, StringComparer.OrdinalIgnoreCase) &&
                                            p_B.Variation == 0 &&
                                            p_B.Textures.Any(p_T => p_T.Param.Equals("Camo", StringComparison.OrdinalIgnoreCase)));

            // The material IDs the camo keeps off, per body mesh (the document's choice, made on the mesh
            // the studio shows; a body mesh the user never looked at keeps every preset material painted).
            var s_MaterialsOff = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var s_OffChoices = s_Own != null ? s_Own.MaterialsOff : p_MaterialsOff;
            if (s_OffChoices != null)
                foreach (var s_Mesh in s_Meshes)
                    if (s_OffChoices.TryGetValue(s_Mesh, out var s_Ids) && s_Ids.Count > 0)
                        s_MaterialsOff[s_Mesh] = new List<int>(s_Ids);

            // ⭐ the pictures on its own document's texture nodes (the pattern's register aside) and, as shipped, on its edited as-shipped
            // graph (weapon, attachment or vehicle alike: a custom texture put on a texture node must just work)
            var s_PictureDocument = s_Own ?? p_Document;
            var s_Pictures = s_PictureDocument != null ? PicturesOf(s_PictureDocument, out _, true) : new List<GraphPicture>();
            var s_ShippedPictures = p_NativeKey == null && s_PictureDocument?.ShippedGraph is { } s_ShippedGraph
                ? PicturesOf(s_ShippedGraph, out _)
                : new List<GraphPicture>();

            s_Weapons.Add(new BakeWeapon
            {
                Folder = s_Weapon.Folder,
                Meshes = new List<string>(s_Meshes),
                Pictures = s_Pictures,
                ShippedPictures = s_ShippedPictures,
                Aug = NoteForMesh(s_Preview) != null,
                Values = s_Values,
                Overridden = s_Overridden,
                Textures = s_Textures,
                PreviewMesh = s_Preview,
                StickerLayers = s_Layers,
                StickerSides = s_Sides,
                EmblemProjection = s_Projection,
                MaterialsOff = s_MaterialsOff,
                HasCamoBinding = s_HasCamo,
                // ⛔ the FIRST-person no-camo preset (weaponpresetshadownocamofp), not any "nocamo" in the name (2026-10-07, the crossbow
                // invisible with "Emblemtest", run 368): the crossbow's T-UGS piece wears weaponpresetnocamo3p, a THIRD-person shader, and the
                // old match sent its body — ShadowFP, decl 164BFA7C — to the no-camo clone, whose preset compiles no solution for that decl
                // (measured in xp2_skybar: 6 decls, 164BFA7C not among them) — nothing drawn. Census of the catalogue: the crossbow is the one
                // weapon the two rules tell apart.
                WearsNoCamo = Cache.GameCache.SectionShadersOf(s_Preview)
                    .Any(p_S => p_S.Contains("nocamo", StringComparison.OrdinalIgnoreCase) && p_S.EndsWith("fp", StringComparison.OrdinalIgnoreCase)),
                // the package's own shader only where this weapon's document carries the edited logic (null set = every weapon)
                OwnShader = p_Subjects?.OwnShaderWeapons == null || p_Subjects.OwnShaderWeapons.Contains(s_Weapon.Folder),
                // the game offers no camo for it AND its body wears the plain first-person preset (the pistols): it takes the accessories'
                // material. ⛔ "no camo row" is TWO things and only the first is the catalogue's (2026-10-06, the crossbow): no camo group
                // in the customization table — the seven pistols AND the crossbow, the framework's window covers them all — and a BODY that
                // wears weaponpresetfp (no camo path, no UV tiles). The crossbow's body wears WeaponPresetShadowFP as shipped, woodlandcamo_d
                // bound and its own CamoDarkening/CamoTiling/ScratchTiling/WearAmount on the material (material_scan.txt): it bakes as a
                // primary does — the look "crossbow test" showed right in game (run 368).
                NoCamoRow = Catalog.WeaponCatalog.HasNoCamoRow(s_Weapon.Folder) &&
                            Cache.GameCache.SectionShadersOf(s_Preview).Any(p_S => p_S.EndsWith("weaponpresetfp", StringComparison.OrdinalIgnoreCase)),
            });
        }

        if (s_Weapons.Count == 0 && (p_Vehicles == null || p_Vehicles.Count == 0))
        {
            p_Error = p_PaintBody
                ? "none of the chosen weapons has a body mesh in the catalogue."
                : "none of the chosen weapons is in the catalogue, so there is nothing to hang a piece on.";
            return null;
        }

        // The GIF's frame sheet, once per package: the texture every weapon's map points into.
        string? s_Sheet = null;
        if (s_Stickers?.Atlas is { } s_Atlas && s_AnyAnimated)
        {
            s_Sheet = Path.Combine(s_Stickers.WorkDir, "sheet.tga");
            RimeShaderEditor.Graph.StickerLayer.SaveTga(s_Atlas.Sheet, s_Sheet);
            s_Stickers.Log?.Invoke($"stickers: '{Path.GetFileName(s_Atlas.Path)}' plays as a flipbook — {s_Atlas.Frames} frame(s), " +
                                   $"{s_Atlas.TotalSeconds:0.##} s per loop, {s_Atlas.Dimensions} sheet of {s_Atlas.Cells} cell(s) of {s_Atlas.CellWidth}×{s_Atlas.CellHeight}" +
                                   $"{(s_Atlas.Opaque ? ", alpha channel ignored" : "")}.");
        }

        var s_Vehicles = VehiclesForBake(p_Vehicles, p_Overrides, p_VehicleGraphs, p_VehicleValues, p_ShaderGraphPath, p_Subjects, p_VehicleGraphTarget,
            p_VehicleTargets, s_NotBaked);
        var s_Accessories = AccessoriesForBake(p_Accessories, s_Weapons, p_NativeKey, p_Overrides, p_Document, p_PaintBody, p_Subjects,
            p_MaterialShaders);

        // an accessory's painted body edited on its own graph ships through the accessory (BakeAccessory.GraphPath); its other edited
        // materials (a glass, a reticle) as material clones handed to that material alone, like a weapon's
        bool PaintedBodyOf(MaterialShaderRequest p_M) => p_M.Shader.Equals(AccessoryPreset, StringComparison.OrdinalIgnoreCase) &&
                                                         s_Accessories.Any(p_A => p_A.Meshes.Any(p_Am => p_Am.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)));
        return new BakePlan
        {
            StickerSheet = s_Sheet,
            // (at the register the graph's Flipbook reads: after the slot's atlas when the slot stays beside the GIF)
            StickerSheetRegister = s_Sheet != null ? s_Stickers?.SheetRegister ?? s_Stickers?.Register + 2 : null,
            // the emblem slot's shapes, when the package's shader draws the slot — beside a GIF too, whose sheet then has a register of its own
            // (StickerGraph.GifKeepsEmblem); an older graph whose GIF sits at the atlas's register has no slot
            EmblemAtlas = s_Stickers is { Emblem: true } &&
                          (s_Sheet == null || (s_Stickers.SheetRegister ?? s_Stickers.Register + 2) != s_Stickers.Register + 2) &&
                          File.Exists(RimeShaderEditor.Graph.EmblemSlot.AtlasTga)
                ? RimeShaderEditor.Graph.EmblemSlot.AtlasTga
                : null,
            Name = s_Name,
            Key = CamoBaker.KeyOf(s_Name),
            Folder = CamoBaker.FolderOf(s_Name),
            Description = p_Description,
            Family = s_Camo.Family,
            Identifier = s_Camo.Identifier,
            PatternImage = p_PatternImage,
            NativeTexture = p_PatternImage == null ? s_Native : null,
            ThumbnailImage = p_ThumbnailImage,
            ShaderGraphPath = p_ShaderGraphPath,
            // the vehicle preset the edited graph was authored on (the graph file itself names the weapons' preset)
            VehicleGraphTarget = (p_ShaderGraphPath != null || p_VehicleGraphs is { Count: > 0 }) && p_Vehicles is { Count: > 0 }
                ? p_VehicleGraphTarget
                : null,
            NoCamoShaderGraphPath = p_NoCamoShaderGraphPath,
            ValuesNotBaked = s_NotBaked.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StickerRegister = s_Stickers?.Register,
            Weapons = s_Weapons,

            OwnShaderPreset = p_OwnShaderPreset ?? CamoBaker.FirstPersonPresetName,

            // the as-shipped graphs edited on weapons that wear another preset: only for the weapons this package paints
            ShippedShaders = (p_ShippedShaders ?? Array.Empty<ShippedShaderRequest>())
                .Select(p_S => p_S with { Weapons = p_S.Weapons.Where(p_W => s_Weapons.Any(p_B => p_B.Folder.Equals(p_W, StringComparison.OrdinalIgnoreCase))).ToList() })
                .Where(p_S => p_S.Weapons.Count > 0)
                .ToList(),

            // Only the edited materials of the meshes this package actually ships — its weapons' and its accessories' (their painted
            // bodies aside: those ride in the accessory, above)
            MaterialShaders = (p_MaterialShaders ?? Array.Empty<MaterialShaderRequest>())
                .Where(p_M => s_Weapons.Any(p_W => p_W.Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase)) ||
                              (!PaintedBodyOf(p_M) &&
                               s_Accessories.Any(p_A => p_A.Meshes.Any(p_Am => p_Am.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))))
                .ToList(),

            // ⭐ the pictures on the materials' own graphs of the meshes this package ships (any custom texture must just
            // work) — an attachment's painted body aside: its pictures ride on the attachment's own instance (BakeAccessory.Pictures)
            MaterialPictures = (p_MaterialPictures ?? Array.Empty<MaterialPictureRequest>())
                .Where(p_M => p_M.Pictures.Count > 0 &&
                              (s_Weapons.Any(p_W => p_W.Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase)) ||
                               (!p_M.Shader.Equals(AccessoryPreset, StringComparison.OrdinalIgnoreCase) &&
                                s_Accessories.Any(p_A => p_A.Meshes.Any(p_Am => p_Am.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))) ||
                               s_Vehicles.Any(p_V => p_V.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase))))
                .ToList(),

            // and those of the vehicle bodies it paints: drawn on the vehicle pieces by copies of their own (CamoBaker)
            VehicleMaterialShaders = (p_MaterialShaders ?? Array.Empty<MaterialShaderRequest>())
                .Where(p_M => s_Vehicles.Any(p_V => p_V.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))
                .ToList(),

            Accessories = s_Accessories,
            Vehicles = s_Vehicles,
            PaintsBody = p_PaintBody,
        };
    }

    /// <summary>"Every vehicle that takes a camo" — the shorthand for the vehicles of a bake.</summary>
    public static readonly IReadOnlyCollection<string> AllVehicles = new[] { "all" };

    /// <summary>
    /// The vehicle bodies a package paints, from catalogue folders ("m1a2", "t90"; "all" = every one that
    /// takes a camo): per body mesh, the shaders of its materials that bind Camo (read from the scan -- what the camo texture
    /// and the variation go to, never the glass, lights or tracks), the level database its base entry is copied from and the
    /// class it is picked in (the catalogue's nameSid: the camo window's name binding carries the same SID). A body whose
    /// materials bind no Camo (the F-35B: shaders of its own) is left out and said.
    /// ⛔ The camo's NUMBERS ride along (2026-09-24): the values typed in the panel (CamoTiling, WearAmount…) reach the
    /// vehicles' material variation the way they reach a weapon's -- only the ones the user set, each material keeping its
    /// own for the rest. Until today the variation went out empty and a vehicle wore its stock numbers under the camo,
    /// whatever the preview showed.
    /// </summary>
    /// <param name="p_VehicleGraphs">Per vehicle (its preview mesh): the saved graph of ITS document when that graph was edited beyond
    /// values (one document per subject). Null = the older callers' single graph, <paramref name="p_ShaderGraphPath"/>,
    /// for every vehicle.</param>
    /// <param name="p_VehicleValues">Per vehicle (its preview mesh): the values typed on its own document; missing = <paramref name="p_Overrides"/>.</param>
    /// <param name="p_DocumentTarget">The preset the vehicles' documents are drawn with (the tab's — BakePlan.VehicleGraphTarget); null = each
    /// vehicle's own (ShaderForMesh). Only for the numbers its document is dressed with (BakeVehicle.DocumentValues).</param>
    /// <param name="p_VehicleTargets">Per vehicle (its preview mesh): the preset its graph (<paramref name="p_VehicleGraphs"/>) was authored on when
    /// it is not <paramref name="p_DocumentTarget"/> — its own starting document on a shader of its own (BakeVehicle.GraphTarget).</param>
    private List<BakeVehicle> VehiclesForBake(IReadOnlyCollection<string>? p_Folders,
        IReadOnlyDictionary<string, string>? p_Overrides = null,
        IReadOnlyDictionary<string, string>? p_VehicleGraphs = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? p_VehicleValues = null,
        string? p_ShaderGraphPath = null, SubjectDocuments? p_Subjects = null, string? p_DocumentTarget = null,
        IReadOnlyDictionary<string, string>? p_VehicleTargets = null, List<string>? p_NotBaked = null)
    {
        var s_Out = new List<BakeVehicle>();
        if (p_Folders == null || p_Folders.Count == 0)
            return s_Out;

        // the same filter and expansion a weapon's overrides get (PlanFor): engine-fed constants never ship, and one typed
        // number ships as the vector it stands for — and one the measured table does not know is left out, and said (C7)
        Dictionary<string, string> Shipped(IReadOnlyDictionary<string, string>? p_Typed, string p_Vehicle)
        {
            var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s_Constant, s_Vector) in p_Typed ?? new Dictionary<string, string>())
                if (WeaponConstants.Find(s_Constant) is { Kind: not ConstantKind.Engine } s_Known && !string.IsNullOrWhiteSpace(s_Vector))
                {
                    var s_Text = s_Vector.Trim();
                    s_Values[s_Known.Name] = s_Text.Contains(',') ? s_Text : ExpandValue(s_Known.Name, s_Text);
                }
                else if (WeaponConstants.Find(s_Constant) == null && !string.IsNullOrWhiteSpace(s_Vector))
                    p_NotBaked?.Add($"{s_Constant} ({p_Vehicle})");

            return s_Values;
        }

        foreach (var s_Vehicle in m_Vehicles.Vehicles)
        {
            // a bare folder, "all", or the dialog's "<folder>@<class>" (the folder alone holds two vehicles in lav25)
            if (!p_Folders.Any(p_F => VehicleCatalog.Asks(p_F, s_Vehicle)))
                continue;

            // this vehicle's own document: its typed values and, when edited beyond values, its graph
            var s_Values = Shipped(p_VehicleValues != null && p_VehicleValues.TryGetValue(s_Vehicle.PreviewMesh, out var s_Own) ? s_Own : p_Overrides,
                s_Vehicle.Display);
            var s_Graph = p_VehicleGraphs != null ? p_VehicleGraphs.GetValueOrDefault(s_Vehicle.PreviewMesh) : p_ShaderGraphPath;
            // (the preset that graph was authored on, when it is its own starting document's and not the tab's — the Rhino's van body)
            var s_GraphTarget = s_Graph != null ? p_VehicleTargets?.GetValueOrDefault(s_Vehicle.PreviewMesh) : null;
            // ⭐ the pictures on its own document's texture nodes, its camo registers' aside (the pattern) — any custom texture
            var s_Pictures = p_Subjects?.DocumentOf(s_Vehicle.PreviewMesh) is { } s_Document
                ? PicturesOf(s_Document, out _, true)
                : new List<GraphPicture>();

            foreach (var s_Mesh in s_Vehicle.Meshes)
            {
                // (the body's materials the catalogue keeps the camo OFF — VehicleEntry.CamoOffMaterials, the Rhino's remote gun — take no
                // part: a preset only they wear is no camo shader of this body)
                var s_Off = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase)
                    ? s_Vehicle.CamoOffMaterials.ToHashSet()
                    : new HashSet<int>();
                var s_Shaders = Scan
                    .Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == 0 &&
                                  !s_Off.Contains(p_B.MaterialId) &&
                                  VehicleCatalog.IsVehicleBodyPreset(p_B.Shader) &&
                                  // (Camo, or the jet family's CamoA/CamoB — see CamoMaterialsOf)
                                  p_B.Textures.Any(p_T => IsCamoTextureParameter(p_T.Param)))
                    .Select(p_B => p_B.Shader)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var s_Id in s_Off)
                    Console.WriteLine($"VEHICLE-CAMO-OFF {s_Vehicle.Folder} {s_Mesh}: material #{s_Id} " +
                                      $"({MaterialShadersOf(s_Mesh).GetValueOrDefault(s_Id, "no shader in the scan").Split('/')[^1]}) keeps its own look - " +
                                      "the catalogue keeps the camo off it");
                var s_CamoParameters = Scan
                    .Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == 0 &&
                                  s_Shaders.Contains(p_B.Shader, StringComparer.OrdinalIgnoreCase))
                    .SelectMany(p_B => p_B.Textures)
                    .Select(p_T => p_T.Param)
                    .Where(IsCamoTextureParameter)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p_P => p_P, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // ⭐ the materials the catalogue FORCES (VehicleEntry.ForceCamoMaterials — the RHIB's hull, whose camo no database binds):
                // their presets join the camo shaders, bound under the camo parameter that preset binds where the game does bind one (the
                // scan, any mesh); the entry then gets the parameter added (mvdb_add_entry's SetTextureParams adds what the base lacks)
                if (s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase))
                {
                    var s_OwnShaders = MaterialShadersOf(s_Mesh);
                    foreach (var s_Id in s_Vehicle.ForceCamoMaterials)
                    {
                        var s_Preset = s_OwnShaders.GetValueOrDefault(s_Id, "");
                        var s_Params = Scan
                            .Where(p_B => p_B.Variation == 0 && p_B.Shader.Equals(s_Preset, StringComparison.OrdinalIgnoreCase))
                            .SelectMany(p_B => p_B.Textures)
                            .Select(p_T => p_T.Param)
                            .Where(IsCamoTextureParameter)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        // ⭐ a shader of its OWN that takes the camo (VehicleEntry.OwnCamoPresets — the Rhino's van body): no vehicle of the game
                        // binds one there, and its copy reads the pattern where its donor binds "Camo" (CamoBaker.DeriveTwinRetarget)
                        var s_OwnCamo = s_Params.Count == 0 && VehicleCatalog.OwnCamoRegisterOf(s_Preset) != null;
                        if (s_OwnCamo)
                            s_Params.Add("Camo");
                        if (!VehicleCatalog.IsVehicleBodyPreset(s_Preset) || s_Params.Count == 0)
                        {
                            Console.WriteLine($"VEHICLE-FORCED {s_Vehicle.Folder} {s_Mesh}: material #{s_Id} " +
                                              $"({(s_Preset.Length > 0 ? s_Preset : "no shader in the scan")}) NOT forced - " +
                                              (VehicleCatalog.IsVehicleBodyPreset(s_Preset)
                                                  ? "no vehicle of the game binds a camo texture on that preset"
                                                  : "it wears no body preset"));
                            continue;
                        }

                        if (!s_Shaders.Contains(s_Preset, StringComparer.OrdinalIgnoreCase))
                            s_Shaders.Add(s_Preset);
                        foreach (var s_Param in s_Params.Where(p_P => !s_CamoParameters.Contains(p_P, StringComparer.OrdinalIgnoreCase)))
                            s_CamoParameters.Add(s_Param);
                        Console.WriteLine($"VEHICLE-FORCED {s_Vehicle.Folder} {s_Mesh}: material #{s_Id} ({s_Preset.Split('/')[^1]}) binds no camo " +
                                          $"in the game - forced by the catalogue, bound as {string.Join("/", s_Params)}" +
                                          (s_OwnCamo ? $" (a shader of its own: its document samples the pattern at t{VehicleCatalog.OwnCamoRegisterOf(s_Preset)})" : ""));
                    }

                    s_CamoParameters.Sort(StringComparer.OrdinalIgnoreCase);
                }

                if (s_Shaders.Count == 0 || s_Vehicle.Databases.Count == 0)
                {
                    Console.WriteLine($"VEHICLE-SKIP {s_Vehicle.Folder} {s_Mesh}: " +
                                      (s_Shaders.Count == 0 ? "no material binds Camo" : "no level database carries it"));
                    continue;
                }

                s_Out.Add(new BakeVehicle
                {
                    Blueprints = new List<string>(s_Vehicle.Blueprints),
                    Class = s_Vehicle.NameSid,
                    Mesh = s_Mesh,
                    // the clone's name has to be as long as the game's; the catalogue's spelling is the game's, in lowercase
                    EbxName = s_Mesh,
                    Database = s_Vehicle.Databases[0],
                    CamoShaders = s_Shaders,
                    CamoParameters = s_CamoParameters,
                    PieceBlueprint = s_Vehicle.PieceBlueprint,
                    Variants = new List<Catalog.VehicleVariant>(s_Vehicle.Variants),
                    Values = new Dictionary<string, string>(s_Values, StringComparer.OrdinalIgnoreCase),
                    // the numbers the preview draws each material of the body with, and its document with: what a copy compiled on a donor
                    // that has no slot for one of its graph's constants bakes in its place (CamoBaker.RelocateConstants)
                    MaterialValues = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase)
                        ? MaterialShadersOf(s_Mesh).ToDictionary(p_M => p_M.Key, p_M =>
                            new Dictionary<string, string>(ValuesForMesh(s_Mesh, p_M.Value, null, p_M.Key) ?? new Dictionary<string, string>(),
                                StringComparer.OrdinalIgnoreCase))
                        : new Dictionary<int, Dictionary<string, string>>(),
                    DocumentValues = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase) &&
                                     ValuesForMesh(s_Mesh, s_GraphTarget ?? p_DocumentTarget ?? ShaderForMesh(s_Mesh)) is { } s_DocumentValues
                        ? new Dictionary<string, string>(s_DocumentValues, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    // the graph its pieces' copies are compiled from, when its document's logic was edited (only the body with pieces uses it)
                    ShaderGraphPath = s_Graph,
                    GraphTarget = s_GraphTarget,
                    // (its pictures: on the body the studio previews, where the pieces are cut)
                    Pictures = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase) ? s_Pictures : new List<GraphPicture>(),
                    // the camo material's own diffuse: the row picture of a camo that brings no pattern
                    DiffuseTexture = Scan
                        .Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == 0 &&
                                      s_Shaders.Contains(p_B.Shader, StringComparer.OrdinalIgnoreCase))
                        .SelectMany(p_B => p_B.Textures)
                        .FirstOrDefault(p_T => p_T.Param.Equals("Diffuse", StringComparison.OrdinalIgnoreCase)).Texture ?? "",
                    // the pieces are cut from the body, the mesh the studio previews
                    Pieces = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase)
                        ? new List<Catalog.VehiclePiece>(s_Vehicle.Pieces)
                        : new List<Catalog.VehiclePiece>(),
                    DriverPart = s_Vehicle.DriverPart,
                    RigidTwins = new List<string>(s_Vehicle.RigidTwins),
                    LevelBundles = new List<string>(s_Vehicle.LevelBundles),
                    BodyInCatalog = s_Vehicle.BodyInCatalog,
                    // (the body's own vertex declarations: a body clone drawn with a copy of its own graph needs that copy compiled for them)
                    BodyDeclarations = new List<string>(DeclarationsOf(s_Mesh)),
                    MaterialShaders = Scan
                        .Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == 0 && p_B.MaterialId >= 0)
                        .GroupBy(p_B => p_B.MaterialId)
                        .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Shader),
                    // …and of every other mesh a piece is cut from (a helicopter's first-person cockpit): the scan, else its own sections
                    // (a shader of its own that no variation binds, the AH-6's cabin)
                    PieceMeshShaders = s_Mesh.Equals(s_Vehicle.PreviewMesh, StringComparison.OrdinalIgnoreCase)
                        ? s_Vehicle.Pieces.Select(p_P => p_P.Mesh).OfType<string>().Where(p_M => p_M.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(p_M => p_M, p_M =>
                            {
                                var s_Of = new Dictionary<int, string>(MaterialShadersOf(p_M));
                                foreach (var (s_Id, s_Shader) in SectionMaterialsOf(p_M))
                                    if (s_Shader.Length > 0)
                                        s_Of.TryAdd(s_Id, s_Shader);
                                return s_Of;
                            }, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase),
                });
            }
        }

        return s_Out;
    }

    /// <summary>
    /// The accessories a package carries, from their names ("XP1_L85A2/L85A2_Acog"): every paintable mesh of
    /// the unlock with the declaration its preset section wears (read from the cache log, never guessed) and
    /// the numbers its variation ships — the accessory's own material under the HOST WEAPON's wear and tiling
    /// (measured: 83 of 259 accessory materials set WearPower to 0, which kills the camo outright) under the
    /// camo's own. An accessory whose weapon is not in the package, or with no paintable mesh, is left out.
    /// </summary>
    /// <summary>
    /// The accessories a bake was asked for, with the shorthands expanded: "all" (or "*") means every accessory of every
    /// weapon in the package, "&lt;weapon&gt;/*" every accessory of that one. What cannot be painted is dropped later, by the
    /// same rule as always (a mesh whose sections wear a weapon preset), so "all" never means "and a laser beam too".
    /// </summary>
    /// <summary>"Every attachment of every weapon in the package" — the shorthand the bake button hands the
    /// plan, so the window asks for exactly what `--baketest --accessories all` asks for.</summary>
    public static readonly IReadOnlyCollection<string> AllAccessories = new[] { "all" };

    public List<string> ExpandAccessoryNames(IReadOnlyCollection<string>? p_Names, IEnumerable<string> p_Folders)
    {
        var s_Wanted = new List<string>();
        var s_Seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s_All = p_Folders.ToList();

        void Add(string p_Folder, string p_Tag)
        {
            var s_Name = p_Folder + "/" + p_Tag;
            if (s_Seen.Add(s_Name))
                s_Wanted.Add(s_Name);
        }

        void AddWeapon(string p_Folder)
        {
            foreach (var s_Unlock in m_Accessories.Of(p_Folder)?.Unlocks ?? Enumerable.Empty<Catalog.AccessoryUnlock>())
                // The iron sights are not one of the attachments this asks for: they belong to the weapon and
                // the bake adds them on its own, so counting them here would count the gun twice. Naming the
                // tag outright still works, for a bake that wants only them.
                if (s_Unlock.HasVisual && !s_Unlock.ShortName.Equals(IronSightsTag, StringComparison.OrdinalIgnoreCase))
                    Add(p_Folder, s_Unlock.ShortName);
        }

        foreach (var s_Name in p_Names ?? Array.Empty<string>())
        {
            var s_Trimmed = s_Name.Trim();
            if (s_Trimmed.Length == 0)
                continue;

            if (s_Trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) || s_Trimmed == "*")
            {
                foreach (var s_Folder in s_All)
                    AddWeapon(s_Folder);

                continue;
            }

            var s_Slash = s_Trimmed.IndexOf('/');
            if (s_Slash > 0 && s_Trimmed[(s_Slash + 1)..].Trim() is "*" or "all")
            {
                AddWeapon(s_Trimmed[..s_Slash].Trim());
                continue;
            }

            if (s_Slash > 0)
                Add(s_Trimmed[..s_Slash].Trim(), s_Trimmed[(s_Slash + 1)..].Trim());
        }

        return s_Wanted;
    }

    /// <summary>
    /// The typed values of the attachment's OWN graphs — the ones the canvas holds while a piece is authored,
    /// kept in the document under "&lt;mesh&gt;|&lt;material id&gt;". Read the same way the editor reads them
    /// (an ExternalConstant node with a PreviewValue, by its bare name, expanded to its vector), so the number
    /// that ships is the number the preview ran.
    /// </summary>
    private Dictionary<string, string> AccessoryGraphValues(RimeShaderEditor.Graph.ShaderGraph? p_Document,
        List<BakeAccessoryMesh> p_Meshes)
    {
        var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (p_Document?.MaterialGraphs is not { Count: > 0 } s_Graphs)
            return s_Values;

        foreach (var s_Mesh in p_Meshes)
        foreach (var (s_Key, s_Graph) in s_Graphs.OrderBy(p_G => p_G.Key, StringComparer.OrdinalIgnoreCase))
        {
            // the key is "<mesh>|<id>": this attachment's graphs and no one else's
            var s_Bar = s_Key.LastIndexOf('|');
            if (s_Bar <= 0 || !s_Key[..s_Bar].Equals(s_Mesh.Mesh, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var s_Node in s_Graph.Nodes)
            {
                if (s_Node.Kind != "ExternalConstant" ||
                    !s_Node.Params.TryGetValue("PreviewValue", out var s_Text) || string.IsNullOrWhiteSpace(s_Text))
                    continue;

                var s_Name = s_Node.GetParam("Name");
                var s_Constant = s_Name.StartsWith("external_", StringComparison.Ordinal)
                    ? s_Name["external_".Length..]
                    : s_Name;

                s_Values[s_Constant] = ExpandValue(s_Constant, s_Text.Trim());
            }
        }

        return s_Values;
    }

    /// <summary>
    /// The attachment models whose data lives only inside the levels (a LOD with no catalog copy: the Rifle Scope's third-person
    /// model, measured 2026-09-25) — a package cannot bring them, so a bake leaves them as the game ships them. Learned from a bake's
    /// audit (BakeResult.LevelOnlyMeshes) and kept beside the studio's caches, so the next bake leaves them out from the start. A seam
    /// (Headless) learns for its own process and writes nothing.
    /// </summary>
    private static HashSet<string>? s_LevelOnly;

    private static string LevelOnlyFile => Path.Combine(Settings.CacheFolder, "attachment_models_level_only.txt");

    private static HashSet<string> LevelOnly()
    {
        if (s_LevelOnly != null)
            return s_LevelOnly;

        s_LevelOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(LevelOnlyFile))
                foreach (var s_Line in File.ReadAllLines(LevelOnlyFile))
                    if (s_Line.Trim() is { Length: > 0 } s_Mesh && !s_Mesh.StartsWith('#'))
                        s_LevelOnly.Add(s_Mesh);
        }
        catch (Exception)
        {
            // no list: the bake finds them again
        }

        return s_LevelOnly;
    }

    private static void RememberLevelOnly(IEnumerable<string> p_Meshes)
    {
        var s_Set = LevelOnly();
        var s_New = p_Meshes.Where(s_Set.Add).ToList();
        if (s_New.Count == 0 || RimeShaderEditor.MainWindow.Headless)
            return;

        try
        {
            File.WriteAllLines(LevelOnlyFile,
                new[] { "# attachment models whose data lives only inside the levels (no catalog copy): a camo package leaves them as the game ships them" }
                    .Concat(s_Set.OrderBy(p_M => p_M, StringComparer.OrdinalIgnoreCase)),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // kept for this session either way
        }
    }

    /// <summary>The attachment models the last plan left out because their data lives only inside the levels (see LevelOnly).</summary>
    private readonly List<string> m_LevelOnlySkipped = new();

    /// <summary>How many attachments (iron sights included) the last plan left as the game ships them because the camo is "as shipped".</summary>
    private int m_ShippedPiecesLeft;

    private List<BakeAccessory> AccessoriesForBake(IReadOnlyCollection<string>? p_Names, List<BakeWeapon> p_Weapons,
        string? p_NativeKey, IReadOnlyDictionary<string, string> p_Overrides,
        RimeShaderEditor.Graph.ShaderGraph? p_Document, bool p_PaintBody = true, SubjectDocuments? p_Subjects = null,
        IReadOnlyList<MaterialShaderRequest>? p_MaterialShaders = null)
    {
        var s_Result = new List<BakeAccessory>();
        m_LevelOnlySkipped.Clear();

        // ⭐ THE IRON SIGHTS RIDE WITH THE WEAPON, ALWAYS (when the user bakes a weapon's camo, NoOptics is
        // baked too, since in the end it is part of the same piece). They are not something the user picks: a
        // gun whose body wears the camo and whose sights stay factory is a gun painted wrong, so every weapon
        // in the package brings its own — whether or not attachments were asked for. A weapon without sights
        // of its own (31 of the 60) adds nothing.
        // ⛔ THEY RIDE WITH THE BODY, SO A PACKAGE THAT PAINTS NO BODY DOES NOT CARRY THEM: a camo of the
        // suppressor reaches 33 weapons, and adding each one's sights would ship 33 guns' worth of sights
        // nobody asked for — under a camo whose whole point is that it paints ONE piece.
        var s_Wanted = ExpandAccessoryNames(p_Names, p_Weapons.Select(p_W => p_W.Folder));
        foreach (var s_Weapon in p_Weapons)
            if (p_PaintBody && IronSightMeshesOf(s_Weapon.Folder).Count > 0)
                s_Wanted.Insert(0, $"{s_Weapon.Folder}/{IronSightsTag}");

        // ⛔ AS SHIPPED, NOTHING IS PAINTED (as shipped is a camo like any other, the difference being that it is the original):
        // every piece here takes a camo — the third-person preset with the host's wear —, which on a camo that IS the weapon as it ships
        // repainted the iron sights of every weapon baked (measured: F2000/NoOptics, M416/NoOptics rode along). They stay as the game ships
        // them — ⭐ except what was EDITED on a piece (the follow-up work on the attachments): its as-shipped graph (a
        // copy of the preset its own material wears, on the materials that wear it in each of its models) and its materials' own graphs.
        m_ShippedPiecesLeft = 0;
        if (p_NativeKey == null)
        {
            foreach (var s_Name in s_Wanted.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var s_Slash = s_Name.IndexOf('/');
                var s_Weapon = s_Slash > 0 ? p_Weapons.FirstOrDefault(p_W => p_W.Folder.Equals(s_Name[..s_Slash].Trim(), StringComparison.OrdinalIgnoreCase)) : null;
                var s_Unlock = s_Weapon == null ? null : m_Accessories.Of(s_Weapon.Folder)?.Unlocks.FirstOrDefault(p_U =>
                    p_U.ShortName.Equals(s_Name[(s_Slash + 1)..].Trim(), StringComparison.OrdinalIgnoreCase));
                if (s_Weapon == null || s_Unlock == null)
                    continue;

                // its as-shipped graph lives in the document of the mesh the studio shows it with
                (string Preset, string GraphPath, IReadOnlyList<GraphPicture> Pictures)? s_Shipped =
                    PreviewMeshOf(s_Unlock) is { } s_Shown && p_Subjects?.ShippedPieces?.TryGetValue(s_Shown, out var s_Found) == true
                        ? s_Found
                        : null;
                // (its materials edited on their own graphs; not a graph of the third-person preset a camo paints it with — the plan
                // ships those through the accessory's camo copy, which a piece as shipped has none of)
                var s_Edited = (p_MaterialShaders ?? Array.Empty<MaterialShaderRequest>())
                    .Where(p_M => s_Unlock.Meshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase) &&
                                  !p_M.Shader.Equals(AccessoryPreset, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var s_Meshes = new List<BakeAccessoryMesh>();
                foreach (var s_Mesh in s_Unlock.Meshes.Where(p_M => !LevelOnly().Contains(p_M)))
                {
                    var s_Sections = SectionMaterialsOf(s_Mesh);
                    // the models with a material that wears the preset the graph is of (by name, as the entry hands it out), or one
                    // edited on its own graph
                    var s_Wears = s_Shipped is { } s_Lane && s_Sections.Any(p_S =>
                        p_S.Shader.Split('/')[^1].Equals(s_Lane.Preset.Split('/')[^1], StringComparison.OrdinalIgnoreCase));
                    if (!s_Wears && !s_Edited.Any(p_M => p_M.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    s_Meshes.Add(new BakeAccessoryMesh(s_Mesh, EbxNameOf(s_Mesh), DeclarationOf(s_Mesh),
                        s_Sections.Select(p_S => p_S.Shader).Where(IsCamoPreset).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
                }

                if (s_Meshes.Count == 0)
                {
                    m_ShippedPiecesLeft++;
                    continue;
                }

                s_Result.Add(new BakeAccessory
                {
                    Weapon = s_Weapon.Folder,
                    Tag = s_Unlock.ShortName,
                    Meshes = s_Meshes,
                    // no camo, no host wear: its materials keep their own numbers
                    Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Textures = s_Meshes
                        .SelectMany(p_M => Scan.Where(p_B => p_B.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))
                        .SelectMany(p_B => p_B.Textures.Select(p_T => p_T.Texture))
                        .Where(p_T => !string.IsNullOrWhiteSpace(p_T))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p_T => p_T, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ShippedPreset = s_Shipped?.Preset,
                    ShippedGraphPath = s_Shipped?.GraphPath,
                    ShippedPictures = s_Shipped?.Pictures.ToList() ?? new List<GraphPicture>(),
                    AsShipped = true,
                });
            }

            // (said by BakeRequested, as the level-only models are)
            return s_Result;
        }

        foreach (var s_Name in s_Wanted.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s_Slash = s_Name.IndexOf('/');
            if (s_Slash <= 0)
                continue;

            var s_Folder = s_Name[..s_Slash].Trim();
            var s_Tag = s_Name[(s_Slash + 1)..].Trim();
            var s_Weapon = p_Weapons.FirstOrDefault(p_W => p_W.Folder.Equals(s_Folder, StringComparison.OrdinalIgnoreCase));
            var s_Unlock = m_Accessories.Of(s_Folder)?.Unlocks.FirstOrDefault(p_U =>
                p_U.ShortName.Equals(s_Tag, StringComparison.OrdinalIgnoreCase));

            if (s_Weapon == null || s_Unlock == null)
                continue;

            var s_Meshes = new List<BakeAccessoryMesh>();
            foreach (var s_Mesh in s_Unlock.Meshes)
            {
                // a model whose data lives only inside the levels stays as the game ships it (see LevelOnly)
                if (LevelOnly().Contains(s_Mesh))
                {
                    if (!m_LevelOnlySkipped.Contains(s_Mesh, StringComparer.OrdinalIgnoreCase))
                        m_LevelOnlySkipped.Add(s_Mesh);
                    continue;
                }

                // Only what a camo can go on: a mesh whose sections wear a weapon preset.
                var s_Sections = SectionMaterialsOf(s_Mesh).Where(p_S => IsCamoPreset(p_S.Shader)).ToList();
                if (s_Sections.Count == 0)
                    continue;

                var s_Declaration = DeclarationOf(s_Mesh);
                if (s_Declaration.Length == 0)
                    continue;

                s_Meshes.Add(new BakeAccessoryMesh(s_Mesh, EbxNameOf(s_Mesh), s_Declaration,
                    s_Sections.Select(p_S => p_S.Shader).Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }

            if (s_Meshes.Count == 0)
                continue;

            // The accessory's own numbers, then the host weapon's wear, then the camo's — the layering the
            // preview shows it with (AccessoryHostConstants in the editor).
            var s_Preview = s_Meshes[0].Mesh;
            var s_Values = ValuesForMesh(s_Preview, AccessoryPreset, p_NativeKey)
                           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s_Constant in new[] { "CamoTiling", "WearAmount", "WearPower" })
                if (s_Weapon.Values.TryGetValue(s_Constant, out var s_HostValue))
                    s_Values[s_Constant] = s_HostValue;

            // ⭐ THIS ATTACHMENT's own document (one document per subject): the values typed on it, its own numbers
            // and its graph's — kept where they were typed, which is the attachment's document while it was on screen
            var s_Document = p_Subjects?.DocumentOf(PreviewMeshOf(s_Unlock) ?? s_Preview) ?? p_Document;
            var s_Typed = s_Document != null && p_Subjects != null ? p_Subjects.ValuesOf(s_Document) : p_Overrides;
            // (what the user set, the three layers below — written beside the host's wear and tiling: BakeAccessory.Overridden, C4)
            var s_Overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // (a vector typed apart on its axes — "4,2" — ships as typed: its first number alone was expanded to "4,4", while the preview drew
            // 4,2; review 2026-09-29, C4. CAMO_ACCESSORY_VECTOR_OLD=1 = the first number, as before)
            foreach (var (s_Constant, s_Text) in s_Typed)
            {
                s_Values[s_Constant] = ExpandValue(s_Constant, Environment.GetEnvironmentVariable("CAMO_ACCESSORY_VECTOR_OLD") == "1"
                    ? s_Text.Split(',')[0].Trim()
                    : s_Text.Trim());
                s_Overridden.Add(s_Constant);
            }

            // …and what the user set for THIS ATTACHMENT in the studio, which beats the weapon's and the camo's
            // (the boxes under the accessory list; kept in the document, so a bake from a saved camo carries
            // them). It is why they exist: a scope's specular is not a rifle body's, and the weapon's wear
            // rubs the camo off it -- 83 of the 259 attachment materials ship WearPower 0.
            foreach (var (s_Constant, s_Text) in s_Document?.AccessoryValuesOf(s_Weapon.Folder, s_Unlock.ShortName)
                                                 ?? new Dictionary<string, string>())
            {
                s_Values[s_Constant] = ExpandValue(s_Constant, s_Text);
                s_Overridden.Add(s_Constant);
            }

            // ⛔⛔ …AND THE PIECE'S OWN GRAPH LAST, WHICH IS WHERE THE NUMBERS ARE TYPED WHILE A PIECE IS BEING
            // AUTHORED (the report: a camo baked with the camo tiling's x,y at 1 looked right in the preview, but
            // in game the baked one came out at the default 0,3 -- 0.3 is the F2000's OWN tiling).
            // THE PREVIEW APPLIES FIVE LAYERS AND THIS APPLIED FOUR. The missing one is this: picking an
            // attachment opens ITS graph on the canvas, and its external constants are typed there -- while
            // `p_Overrides` is `CamoSnapshot().PreviewValues`, which is built from `DocumentGraph.Nodes` (the
            // CAMO's), never from the canvas. So the host weapon's value, applied above, survived into the
            // package and the gun drew a number the studio never showed.
            // ⇒ Rule: when the preview composes in layers, the bake walks THE SAME LIST of layers in the same
            // order; a layer that exists in only one of the two is a divergence nobody sees until the match.
            foreach (var (s_Constant, s_Text) in AccessoryGraphValues(s_Document, s_Meshes))
            {
                s_Values[s_Constant] = s_Text;
                s_Overridden.Add(s_Constant);
            }

            var s_Textures = s_Meshes
                .SelectMany(p_M => Scan.Where(p_B => p_B.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(p_B => p_B.Textures.Select(p_T => p_T.Texture))
                .Where(p_T => !string.IsNullOrWhiteSpace(p_T))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p_T => p_T, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // its painted body's own graph, when its LOGIC was edited (the request the studio makes for it names the third-person preset,
            // the graph it is authored on): the accessory ships a copy of that preset compiled from it
            var s_Graph = (p_MaterialShaders ?? Array.Empty<MaterialShaderRequest>())
                .FirstOrDefault(p_M => p_M.Shader.Equals(AccessoryPreset, StringComparison.OrdinalIgnoreCase) &&
                                       s_Meshes.Any(p_Am => p_Am.Mesh.Equals(p_M.Mesh, StringComparison.OrdinalIgnoreCase)))?.GraphPath;

            // ⭐ …and the pictures on that painted body's graph (any custom texture on a texture node must just work) — kept in
            // the attachment's document per material, whether or not its logic changed
            var s_BodyGraph = s_Document?.MaterialGraphs?
                .Where(p_G => RimeShaderEditor.Graph.ShaderGraph.TryParseMaterialKey(p_G.Key, out var s_Of, out _) &&
                              s_Meshes.Any(p_Am => p_Am.Mesh.Equals(s_Of, StringComparison.OrdinalIgnoreCase)) &&
                              p_G.Value.TargetShader.Equals(AccessoryPreset, StringComparison.OrdinalIgnoreCase))
                .Select(p_G => p_G.Value)
                .FirstOrDefault();

            s_Result.Add(new BakeAccessory
            {
                Weapon = s_Weapon.Folder,
                Tag = s_Unlock.ShortName,
                Meshes = s_Meshes,
                Values = s_Values,
                Overridden = s_Overridden,
                Textures = s_Textures,
                GraphPath = s_Graph,
                Pictures = s_BodyGraph != null ? PicturesOf(s_BodyGraph, out _) : new List<GraphPicture>(),
            });
        }

        return s_Result;
    }

    /// <summary>
    /// The vertex declaration a mesh's PRESET sections declare, from the cache log's MESHDECL lines ("0x…").
    /// It decides whether the third-person preset can draw the accessory or the package must ship a twin
    /// re-labelled to it. Empty when the mesh is not in a cache log, or when its preset sections disagree —
    /// no accessory in the game does (measured over the 179), and a guess here ships a mesh that cannot draw.
    /// </summary>
    /// <summary>
    /// Every vertex declaration a mesh's PRESET sections declare (distinct, "0x…"), from the cache log's MESHDECL lines — a vehicle
    /// body's composite ones (the RHIB's 0xFC3E2403): what a copy of its camo preset must have solutions for to draw the whole body.
    /// Empty when the mesh is not in a cache log.
    /// </summary>
    public IReadOnlyList<string> DeclarationsOf(string p_Mesh) =>
        SectionDeclarations.TryGetValue(p_Mesh, out var s_List)
            ? s_List.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    public string DeclarationOf(string p_Mesh)
    {
        var s_Decls = SectionDeclarations.TryGetValue(p_Mesh, out var s_List) ? s_List : null;
        if (s_Decls == null)
            return "";

        var s_Distinct = s_Decls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return s_Distinct.Count == 1 ? s_Distinct[0] : "";
    }

    /// <summary>
    /// The mesh's EBX Name as its partition stores it (mixed case): the clone's name must be exactly as long.
    /// The catalogue keeps the game's own spelling; a mesh outside it falls back to the resource name.
    /// </summary>
    public string EbxNameOf(string p_Mesh) =>
        m_Accessories.EbxNameOf(p_Mesh) ?? p_Mesh;

    /// <summary>
    /// What a plan needs to compose the sticker layers: the placements (the graph's), the register the
    /// graph samples, the layer side, how a picture is read, and where the composed files go.
    /// </summary>
    public sealed class StickerRequest
    {
        public IReadOnlyList<RimeShaderEditor.Graph.StickerPlacement> Placements { get; init; } = Array.Empty<RimeShaderEditor.Graph.StickerPlacement>();
        public int? Register { get; init; }
        public int Side { get; init; } = 1024;
        public Func<string, System.Windows.Media.Imaging.BitmapSource?> ImageOf { get; init; } = _ => null;
        public string WorkDir { get; init; } = "";
        public Action<string>? Log { get; init; }

        /// <summary>The animated GIF the camo plays (the graph's), and its decoded frame sheet; null without one.</summary>
        public string? Animation { get; init; }
        public RimeShaderEditor.Graph.GifAtlas? Atlas { get; init; }

        /// <summary>Whether the graph draws the emblem slot (EmblemSlot): its placements are found on each weapon's body and composed into the maps.</summary>
        public bool Emblem { get; init; }

        /// <summary>
        /// The register the graph samples the GIF's frame sheet at (its Flipbook's texture), or null without one: after the emblem slot's
        /// atlas when the slot stays beside the GIF (StickerGraph.GifKeepsEmblem), else the atlas's own register.
        /// </summary>
        public int? SheetRegister { get; init; }

        /// <summary>The request a graph makes: its placements and register, pictures read by the window (cached), files under the bake's work folder.</summary>
        public static StickerRequest? Of(RimeShaderEditor.Graph.ShaderGraph p_Graph, Func<string, System.Windows.Media.Imaging.BitmapSource?> p_ImageOf,
            string p_WorkDir, Action<string>? p_Log = null, Func<string, RimeShaderEditor.Graph.GifAtlas?>? p_AtlasOf = null)
        {
            if (p_Graph.StickerRegister == null)
                return null;

            var s_Atlas = p_Graph.StickerAnimation != null && p_AtlasOf != null ? p_AtlasOf(p_Graph.StickerAnimation) : null;
            return new StickerRequest
            {
                Placements = p_Graph.Stickers ?? new List<RimeShaderEditor.Graph.StickerPlacement>(),
                Register = p_Graph.StickerRegister,
                Side = p_Graph.StickerLayerSide,
                ImageOf = p_ImageOf,
                WorkDir = Path.Combine(p_WorkDir, "stickers"),
                Log = p_Log,
                Animation = s_Atlas != null ? p_Graph.StickerAnimation : null,
                Atlas = s_Atlas,
                Emblem = RimeShaderEditor.Graph.EmblemSlot.IsOn(p_Graph),
                SheetRegister = RimeShaderEditor.Graph.StickerGraph.SheetRegisterIn(p_Graph),
            };
        }
    }

    /// <summary>The first-person preset a package's own shader is cloned from and compiled against.</summary>
    public const string FpPresetName = "Weapons/Shaders/WeaponPresetShadowFP";

    /// <summary>The first-person NoCamo preset the no-camo twin of a sticker package is compiled against.</summary>
    public const string NoCamoFpPresetName = "Weapons/Shaders/WeaponPresetShadowNoCamoFP";

    /// <summary>
    /// The pictures on a graph's texture nodes (their "Custom texture"), each with the parameter it ships under (GraphPicture): the
    /// preset's own at that register, or a fresh one its copy declares there. 2D texture nodes only; the rest are named in
    /// <paramref name="p_Skipped"/>, with any picture whose file is gone.
    /// </summary>
    internal List<GraphPicture> PicturesOf(RimeShaderEditor.Graph.ShaderGraph p_Graph, out List<string> p_Skipped, bool p_OwnDocument = false) =>
        PictureNodesOf(p_Graph, out p_Skipped, p_OwnDocument).Select(p_P => p_P.Picture).ToList();

    /// <summary>
    /// The graph as the bake compiles it where a picture sits on a register its preset reads a FIXED texture at (GraphPicture.From): that
    /// node moved to the free register the picture ships at. The graph itself when nothing moves.
    /// </summary>
    internal RimeShaderEditor.Graph.ShaderGraph WithPictureMoves(RimeShaderEditor.Graph.ShaderGraph p_Graph, bool p_OwnDocument = false)
    {
        var s_Moves = PictureNodesOf(p_Graph, out _, p_OwnDocument).Where(p_P => p_P.Picture.From != null).ToList();
        if (s_Moves.Count == 0)
            return p_Graph;

        var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(p_Graph.ToJson());
        foreach (var (s_Node, s_Picture) in s_Moves)
            if (s_Copy.Nodes.FirstOrDefault(p_N => p_N.Id == s_Node.Id) is { } s_Moved)
                s_Moved.Params["Register"] = s_Picture.Register.ToString();

        return s_Copy;
    }

    private List<(RimeShaderEditor.Graph.GraphNode Node, GraphPicture Picture)> PictureNodesOf(RimeShaderEditor.Graph.ShaderGraph p_Graph,
        out List<string> p_Skipped, bool p_OwnDocument)
    {
        p_Skipped = new List<string>();
        var s_Result = new List<(RimeShaderEditor.Graph.GraphNode, GraphPicture)>();
        // what the preset itself reads (parameters, fixed and engine textures) and what the graph's nodes read: a picture on a register the
        // preset reads a FIXED texture at moves past all of them in its copy
        var s_Preset = TextureRegistersOf(p_Graph.TargetShader);
        var s_Free = s_Preset
            .Concat(p_Graph.Nodes.Where(p_N => RimeShaderEditor.Graph.Palette.TextureKinds.Contains(p_N.Kind))
                .Select(p_N => int.TryParse(p_N.GetParam("Register"), out var s_R) ? s_R : 0))
            .DefaultIfEmpty(0)
            .Max() + 1;
        // the preset's own parameters, by register, as its bytecode names them (texture_Diffuse is t1…)
        var s_Registers = RegistersFor(p_Graph.TargetShader, Cache.SlotMap.Load(p_Graph.TargetShader));
        // a subject's own camo document: its camo register's picture is the PACKAGE'S PATTERN (the tab's, shipped as such) and the sticker
        // registers are the sticker layer's — neither is one of these
        var s_Except = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (p_OwnDocument)
        {
            s_Except.UnionWith(CamoRegistersFor(p_Graph.TargetShader));
            if (p_Graph.StickerRegister is { } s_Sticker)
                s_Except.UnionWith(new[] { s_Sticker, s_Sticker + 1, s_Sticker + 2 }.Select(p_R => p_R.ToString()));
        }

        foreach (var s_Node in p_Graph.Nodes)
        {
            if (!RimeShaderEditor.Graph.Palette.TextureKinds.Contains(s_Node.Kind) ||
                !s_Node.Params.TryGetValue("CustomTexture", out var s_File) || s_File.Length == 0 ||
                s_Except.Contains(s_Node.GetParam("Register")))
                continue;

            if (s_Node.Kind is not ("Texture" or "NormalMap") || !int.TryParse(s_Node.GetParam("Register"), out var s_Register))
            {
                p_Skipped.Add($"{s_Node.Kind} '{Path.GetFileName(s_File)}'");
                continue;
            }

            if (!File.Exists(s_File))
            {
                p_Skipped.Add($"'{s_File}' (the file is gone)");
                continue;
            }

            var s_Own = s_Registers.FirstOrDefault(p_R => p_R.Value == s_Register.ToString()).Key;
            // sRGB unless the node reads a normal map: the preview's rule (CustomTextureIsSrgb), so the game draws what it showed — or what the
            // node declares (CustomTextureSrgb: the RU engineer's legs mask, the game's LINEAR us_lowerbody_m1)
            var s_Srgb = s_Node.Params.TryGetValue("CustomTextureSrgb", out var s_Declared) && bool.TryParse(s_Declared, out var s_Declares)
                ? s_Declares
                : s_Node.Kind != "NormalMap";
            if (s_Own != null)
                s_Result.Add((s_Node, new GraphPicture(s_Register, s_Own, false, s_File, s_Srgb)));
            else if (s_Preset.Contains(s_Register))
            {
                // the preset reads a FIXED texture here (no material binds it: the glass's t2): the copy reads the picture at a free register
                var s_To = s_Free++;
                s_Result.Add((s_Node, new GraphPicture(s_To, $"Picture{s_To}", true, s_File, s_Srgb, s_Register)));
            }
            else
                s_Result.Add((s_Node, new GraphPicture(s_Register, $"Picture{s_Register}", true, s_File, s_Srgb)));
        }

        return s_Result;
    }

    /// <summary>
    /// A weapon preset as the game's shader database spells it (censused on levels/xp2_skybar/xp2_skybar/shaderdb, 2026-09-25: the six
    /// weapon presets, 3P and FP, camo, no-camo and the XP2 shadow one), from the lower-case name a document carries; the name as given
    /// when it is not one of them (the database is keyed by the lower-cased name's hash, so the spelling is for the logs).
    /// </summary>
    internal static string GamePresetNameOf(string p_Preset) => p_Preset.ToLowerInvariant() switch
    {
        "weapons/shaders/weaponpresetfp" => "Weapons/Shaders/WeaponPresetFP",
        "weapons/shaders/weaponpreset3p" => "Weapons/Shaders/WeaponPreset3P",
        "weapons/shaders/weaponpresetshadowfp" => FpPresetName,
        "weapons/shaders/weaponpresetshadowfp_xp2" => "Weapons/Shaders/WeaponPresetShadowFP_xp2",
        "weapons/shaders/weaponpresetshadownocamofp" => NoCamoFpPresetName,
        "weapons/shaders/weaponpresetnocamo3p" => "Weapons/Shaders/WeaponPresetNoCamo3P",
        _ => p_Preset,
    };

    /// <summary>
    /// The preset an ACCESSORY is authored on and baked with, in BOTH views — as a section shader name, the
    /// way the dumps spell it. Measured in-game (fase E, 26 launches): the first-person preset zones the camo
    /// by UV tile AND multiplies the final colour by the specular as if it were occlusion, and an accessory's
    /// specular (dark plastic, bright screws) blacks its body out; the third-person preset does neither and
    /// paints the whole part. Meshes whose declaration the preset has no solution for (the ACOG family, with
    /// a colour stream) get a twin of THIS preset relabelled to their declaration at bake time.
    /// </summary>
    public const string AccessoryPreset = "weapons/shaders/weaponpreset3p";

    /// <summary>
    /// Writes the no-camo twin of a sticker package's graph — the game's NoCamo preset graph (the target's
    /// twin, as cached by the translation step) sampling the sticker layer at the package's register — to
    /// camo_graph_nocamo.json in the work folder. The weapons whose body wears that preset as shipped get a
    /// clone of it, so their stickers land on the weapon exactly as the game draws it. Null with the reason
    /// when the twin is not translated or has no material root.
    /// </summary>
    internal string? NoCamoShaderGraphForBake(RimeShaderEditor.MainWindow p_Editor, string p_Target, int? p_Register, string p_Work, out string p_Why,
        RimeShaderEditor.Graph.ShaderGraph? p_Document = null)
    {
        p_Why = "";
        // the document whose stickers these are (one document per subject: the edited weapons' own, not necessarily the one on screen)
        var s_Document = p_Document ?? p_Editor.DocumentGraph;
        var s_NoCamoPreset = NoCamoPresetOf(p_Target);
        var s_NoCamoCache = p_Editor.CamoGraphCachePath(s_NoCamoPreset);
        if (!File.Exists(s_NoCamoCache))
        {
            p_Why = $"stickers over the weapon as shipped need the graph of '{s_NoCamoPreset}', which is not translated yet — " +
                    "run the studio's pretranslate step.";
            return null;
        }

        var s_Graph = RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_NoCamoCache));
        s_Graph.TargetShader = NoCamoFpPresetName;
        s_Graph.BakeVariation = null;
        if (RimeShaderEditor.Graph.StickerGraph.Ensure(s_Graph, DiffuseRegisterFor(s_NoCamoPreset), p_Register, out _, s_Document.StickerLayerSide) == null)
        {
            p_Why = "the no-camo preset's graph has no material root to blend the sticker layer into.";
            return null;
        }

        // The animated sticker plays on the no-camo twin too: same map, same sheet, same timeline.
        if (s_Document.StickerAnimation is { } s_Gif && p_Editor.StickerAtlasOf(s_Gif) is { } s_Atlas)
            RimeShaderEditor.Graph.StickerGraph.EnsureAnimated(s_Graph, s_Atlas, s_Document.StickerLayerSide, out _);

        var s_Path = Path.Combine(p_Work, "camo_graph_nocamo.json");
        File.WriteAllText(s_Path, s_Graph.ToJson(), new System.Text.UTF8Encoding(false));
        return s_Path;
    }

    /// <summary>
    /// The graph as the bake compiles it: a copy aimed at the first-person preset (whatever preset the tab
    /// was opened on — the variations of every weapon run on this one), with no bake-time variation of the
    /// editor's own and no custom textures (the pattern travels as the package's texture, bound through the
    /// database entry, never as a texture override).
    /// </summary>
    internal static RimeShaderEditor.Graph.ShaderGraph ShaderGraphForBake(RimeShaderEditor.Graph.ShaderGraph p_Graph, string? p_Preset = null)
    {
        var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(p_Graph.ToJson());
        // (the preset the package's own shader is cloned from: BakePlan.OwnShaderPreset — the camo preset unless an as-shipped document
        // aims at another one)
        s_Copy.TargetShader = p_Preset ?? FpPresetName;
        s_Copy.BakeVariation = null;
        s_Copy.BakeMesh = null;
        s_Copy.Variations = null;
        foreach (var s_Node in s_Copy.Nodes)
            s_Node.Params.Remove("CustomTexture");

        return s_Copy;
    }

    /// <summary>The graphs among these files that PROJECT the emblem slot (an Emblem Projection node).</summary>
    internal static List<string> GraphsProjectingSlot(IEnumerable<string?> p_GraphPaths)
    {
        var s_Projecting = new List<string>();
        foreach (var s_Path in p_GraphPaths.Where(p_P => p_P != null && File.Exists(p_P)).Distinct(StringComparer.OrdinalIgnoreCase))
            try
            {
                if (RimeShaderEditor.Graph.StickerGraph.IsProjected(RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_Path!))))
                    s_Projecting.Add(s_Path!);
            }
            catch (Exception)
            {
                // not a graph the bake compiles: nothing to say about it
            }

        return s_Projecting;
    }

    /// <summary>
    /// ⭐ THE PROJECTED EMBLEM SLOT (EmblemSlot.Projected; confirmed working in game): the shader reads its squares from frame
    /// constants and each weapon's variation carries its own (BakeWeapon.EmblemProjection) — one package, every weapon with its slot. Said
    /// when a graph the package compiles projects: which weapons carry squares, and which have no slot (no emblem there).
    /// </summary>
    internal static void NoteProjectedSlots(BakePlan p_Plan, IEnumerable<string?> p_GraphPaths, Action<string> p_Log)
    {
        var s_Graphs = GraphsProjectingSlot(p_GraphPaths);
        if (s_Graphs.Count == 0)
            return;

        var s_With = p_Plan.Weapons.Where(p_W => p_W.EmblemProjection.Length > 0).ToList();
        var s_Without = p_Plan.Weapons.Where(p_W => p_W.EmblemProjection.Length == 0).Select(p_W => p_W.Folder).ToList();
        p_Log($"NOTE: the emblem slot is PROJECTED from the mesh position ({s_Graphs.Count} graph(s); the package's copies get vertex shaders " +
              $"that hand it over) — {s_With.Count} weapon(s) carry their own squares in their variation" +
              (s_With.Count > 0
                  ? $" ({string.Join(", ", s_With.Take(8).Select(p_W => $"{p_W.Folder} {p_W.EmblemProjection.Split(';').Length}"))}{(s_With.Count > 8 ? ", …" : "")})"
                  : "") +
              (s_Without.Count > 0 ? $"; no slot on {string.Join(", ", s_Without.Take(8))}{(s_Without.Count > 8 ? ", …" : "")} — no emblem there." : "."));
    }

    /// <summary>
    /// The plan for one of the framework's DEFAULT camos: a game pattern on every weapon, built like any
    /// package (the preview's values for each weapon, the game texture referenced) but shipped under
    /// defaultcamos/ on the game's own row for that pattern, with no picture and no text of its own.
    ///
    /// Weapons that ship the pattern as a variation of their own are left out: the game's row already offers
    /// it there, and a second row of the same pattern is what showed on the F2000 (its two unique skins
    /// came out duplicated). Those folders come back in <paramref name="p_Shipped"/>.
    /// </summary>
    /// <param name="p_Key">The native key (CamoSession.NativeCamos): abu, atacs, berkut, digiflora, dsrttiger, kamysh, nwu, partizan.</param>
    public BakePlan? PlanForNative(string p_Key, out string? p_Error, out List<string> p_Shipped)
    {
        p_Shipped = new List<string>();
        var s_Native = NativeCamos.FirstOrDefault(p_C => p_C.Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase));
        if (s_Native.Key == null)
        {
            p_Error = $"'{p_Key}' is not one of the eight game camos ({string.Join(", ", NativeCamos.Select(p_C => p_C.Key))}).";
            return null;
        }

        if (!FrameworkMod.NativeIdentifierOf.TryGetValue(s_Native.Key, out var s_Identifier))
        {
            p_Error = $"no game row is known for '{s_Native.Key}'.";
            return null;
        }

        var s_Folders = new List<string>();
        foreach (var s_Weapon in m_Weapons.Weapons)
        {
            if (s_Weapon.BodyMeshes.Any(p_M => HasNativeVariation(p_M, s_Native.Key)))
                p_Shipped.Add(s_Weapon.Folder);
            else
                s_Folders.Add(s_Weapon.Folder);
        }

        var s_Description = $"The game's {s_Native.Display} pattern, offered on every weapon by the camo framework.";
        var s_Base = PlanFor(s_Native.Display, s_Description, null, s_Native.Key, null,
            new Dictionary<string, string>(), s_Folders, out p_Error);
        if (s_Base == null)
            return null;

        // The key and folder come from the native key, not the display name ("A-TACS" would key as A_TACS):
        // the variations are named <Weapon>_CAMO_ABU … exactly as the framework's per-level base named them,
        // so a loadout that pointed at one of those keeps pointing at the same variation hash.
        return new BakePlan
        {
            Name = s_Native.Display,
            Key = CamoBaker.KeyOf(s_Native.Key),
            Folder = s_Native.Key.ToLowerInvariant(),
            Description = s_Description,
            Identifier = s_Identifier,
            NativeTexture = s_Base.NativeTexture,
            Native = true,
            Weapons = s_Base.Weapons,
        };
    }

    /// <summary>
    /// Whether a graph name can name a camo: something the user typed, not the empty/"Untitled" default and
    /// not the name of the weapon preset a fresh tab is called after until it is renamed (a package called
    /// "weaponpresetshadowfp" is not what anyone meant).
    /// </summary>
    public static bool IsCamoName(string? p_Name)
    {
        var s_Name = (p_Name ?? "").Trim();
        return s_Name.Length > 0 &&
               !s_Name.Equals("Untitled", StringComparison.OrdinalIgnoreCase) &&
               !IsCamoPreset(s_Name) &&
               // ⛔ nor a soldier's shader, which a fresh Soldiers tab is called after (the bake dialog read "characterroot" twice, 2026-09-28:
               // the skin — its identity, its package, its OUTFIT cell — would have been named after the shader)
               !System.Text.RegularExpressions.Regex.IsMatch(s_Name, @"^characterroot(_\w+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The framework folder: a setting, asked for the first time it is needed and validated by what is in it
    /// — a mod.json and the framework's own Lua — never by its name. Null when the user cancels or points at
    /// something else (said in the log). Shared by baking and deleting: one folder, asked once, and asked
    /// again only when the saved one no longer holds the framework (moved, renamed, drive gone — ask again
    /// only once that link is lost).
    ///
    /// ⛔ WRITTEN INTO THE WINDOW'S OWN SETTINGS INSTANCE (<paramref name="p_Settings"/> = MainWindow.Settings),
    /// never into a fresh Load(): the window saves its instance on Closing and after every Settings change,
    /// and that save overwrote the folder saved here from another instance — so it had to be picked again
    /// on every start.
    /// </summary>
    /// <param name="p_Ask">How the folder is asked for when it is unknown or no longer valid; the folder
    /// picker by default, a canned answer in a seam. Null from it = cancelled.</param>
    internal static string? ResolveFrameworkFolder(RimeShaderEditor.MainWindow p_Editor,
        RimeShaderEditor.EditorSettings p_Settings, Action<string> p_Log, Func<string?>? p_Ask = null)
    {
        var s_Mod = p_Settings.CamoFrameworkFolder;
        if (FrameworkMod.Validate(s_Mod) is not { } s_Why)
            return s_Mod;

        if (s_Mod.Length > 0)
            p_Log($"The camo framework folder in Settings ('{s_Mod}') does not hold the framework any more: {s_Why}. Asking again.");

        p_Ask ??= () =>
        {
            var s_Dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the CamoFramework mod folder (the one holding mod.json)",
            };

            return s_Dialog.ShowDialog(p_Editor) == true ? s_Dialog.FolderName : null;
        };

        s_Mod = p_Ask();
        if (s_Mod == null)
            return null;

        if (FrameworkMod.Validate(s_Mod) is { } s_StillWhy)
        {
            p_Log($"'{s_Mod}' is not the camo framework: {s_StillWhy}.");
            return null;
        }

        p_Settings.CamoFrameworkFolder = s_Mod;
        p_Settings.Save();
        p_Log($"Camo framework folder: {s_Mod} (kept in Settings; asked again only if it stops holding the framework).");
        return s_Mod;
    }

    /// <summary>
    /// The "Delete camo…" button, the bake's counterpart: lists the camos baked into the
    /// framework — the user's, never the eight defaults — and removes the ticked ones (folder to the Recycle
    /// Bin, framework re-registered). Every step in the editor's Output.
    /// </summary>
    public void StartDelete(RimeShaderEditor.MainWindow p_Editor)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        var s_Mod = ResolveFrameworkFolder(p_Editor, p_Editor.Settings, Log);
        if (s_Mod == null)
        {
            Log("Delete cancelled: no framework folder chosen.");
            return;
        }

        // (neither the game's default camos nor the default look -- what the dialog lists)
        var s_Packages = FrameworkMod.Scan(s_Mod, Log).Where(p_P => !p_P.Native && !p_P.DefaultLook).ToList();
        if (s_Packages.Count == 0)
        {
            Log($"No baked camos in {s_Mod} — the default camos and the default look are not removed one by one.");
            return;
        }

        var s_Chosen = View.DeleteCamoDialog.Ask(p_Editor, s_Packages, s_Mod);
        if (s_Chosen == null || s_Chosen.Count == 0)
        {
            Log("Delete cancelled.");
            return;
        }

        var s_Removed = FrameworkMod.DeletePackages(s_Mod, s_Chosen.Select(p_P => p_P.Folder).ToList(), Log);
        Log(s_Removed.Count > 0
            ? $"Removed {s_Removed.Count} camo(s): {string.Join(", ", s_Removed)}. Restart the server for the change to take."
            : "Nothing was removed.");
    }

    // ---- ⭐ THE CAMO SAVED READY TO BAKE (the request: save the camo ready to bake, nodes and all, so the user picks a camo, puts the sticker
    // on it and Bake bakes the sticker together with that camo — not a reader of camos already baked) ----------------------------------------
    // One file, <name>.camo.json: the tab on screen whole (MainWindow.CamoTabsSnapshot — every weapon's own document, its nodes, its stickers,
    // its materials' graphs) and the bake dialog's answers (the camo's: name, text, category, attachments, the weapons ticked…). The pictures it
    // uses are copied into <name>_files beside it and named relative to the file — nothing in it is a path of this machine (a picture shipped
    // with the studio is named relative to the studio). Open camo… puts the tab back and the answers with it; Bake bakes it.

    /// <summary>What a camo file says it is, and the version of its layout.</summary>
    internal const string CamoFileFormat = "RimeCamoStudio camo";
    internal const int CamoFileVersion = 1;
    internal const string CamoFileExtension = ".camo.json";

    /// <summary>A picture copied beside the camo file (relative to the file's folder), and one shipped with the studio (relative to it).</summary>
    private const string CamoFileRef = "camofile:";
    private const string StudioFileRef = "camostudio:";

    /// <summary>A path as a camo file: "*.camo.json" (a ".json" chosen in the dialog becomes ".camo.json").</summary>
    internal static string CamoFilePath(string p_Path) =>
        p_Path.EndsWith(CamoFileExtension, StringComparison.OrdinalIgnoreCase) ? p_Path
        : p_Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p_Path[..^".json".Length] + CamoFileExtension
        : p_Path + CamoFileExtension;

    /// <summary>The file's name without ".camo.json" — what its pictures' folder is called after.</summary>
    private static string CamoFileStem(string p_Path)
    {
        var s_Name = Path.GetFileName(p_Path);
        return s_Name.EndsWith(CamoFileExtension, StringComparison.OrdinalIgnoreCase) ? s_Name[..^CamoFileExtension.Length] : Path.GetFileNameWithoutExtension(s_Name);
    }

    /// <summary>Every text value of a JSON tree, replaced where <paramref name="p_Map"/> answers one (null = left as it is).</summary>
    private static void RewriteTexts(JsonNode? p_Node, Func<string, string?> p_Map)
    {
        static string? TextOf(JsonNode? p_Value) => p_Value is JsonValue s_Value && s_Value.TryGetValue<string>(out var s_Text) ? s_Text : null;

        switch (p_Node)
        {
            case JsonObject s_Object:
                foreach (var s_Key in s_Object.Select(p_P => p_P.Key).ToList())
                    if (TextOf(s_Object[s_Key]) is { } s_Text)
                    {
                        if (p_Map(s_Text) is { } s_New)
                            s_Object[s_Key] = s_New;
                    }
                    else
                        RewriteTexts(s_Object[s_Key], p_Map);
                break;
            case JsonArray s_Array:
                for (var i = 0; i < s_Array.Count; i++)
                    if (TextOf(s_Array[i]) is { } s_Text)
                    {
                        if (p_Map(s_Text) is { } s_New)
                            s_Array[i] = s_New;
                    }
                    else
                        RewriteTexts(s_Array[i], p_Map);
                break;
        }
    }

    /// <summary>The "Save camo…" button: asks where, then <see cref="SaveCamoTo"/>.</summary>
    public void StartSaveCamo(RimeShaderEditor.MainWindow p_Editor)
    {
        var s_Name = p_Editor.GraphNameBox.Text.Trim();
        var s_Dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save camo (ready to bake)",
            Filter = "Camo ready to bake (*.camo.json)|*.camo.json",
            FileName = (IsCamoName(s_Name) ? s_Name : "Camo") + CamoFileExtension,
        };

        if (s_Dialog.ShowDialog(p_Editor) != true)
        {
            p_Editor.LogCamo("Save camo cancelled.");
            return;
        }

        SaveCamoTo(p_Editor, s_Dialog.FileName);
    }

    /// <summary>
    /// Writes the camo on screen, ready to bake, to <paramref name="p_Path"/> (made a "*.camo.json"): the tab and the bake dialog's answers,
    /// its pictures copied into "&lt;name&gt;_files" beside it. The camo is named <paramref name="p_Name"/>, else the tab's graph name, else
    /// the file's — and the tab follows the name, as a bake does. Returns the file written, or null (said in the Output).
    /// </summary>
    internal string? SaveCamoTo(RimeShaderEditor.MainWindow p_Editor, string p_Path, string? p_Name = null)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        var s_Path = Path.GetFullPath(CamoFilePath(p_Path));
        var s_Stem = CamoFileStem(s_Path);
        var s_Typed = p_Editor.GraphNameBox.Text.Trim();
        var s_Name = (p_Name ?? (IsCamoName(s_Typed) ? s_Typed : s_Stem)).Trim();
        if (!IsCamoName(s_Name))
        {
            Log($"Save camo: '{s_Name}' cannot name a camo — type its name under Graph name (or name the file after it).");
            return null;
        }

        if (!string.Equals(s_Typed, s_Name, StringComparison.Ordinal))
        {
            p_Editor.GraphNameBox.Text = s_Name;
            Log($"Camo named '{s_Name}' (the tab's graph name follows it).");
        }

        var (s_Tabs, s_Active) = p_Editor.CamoTabsSnapshot();
        if (s_Tabs.Count == 0)
        {
            Log("Save camo: there is no camo tab to save.");
            return null;
        }

        // the name rides on the document on screen (the bake dialog starts from it, and finds these answers by it)
        if (s_Tabs[s_Active]?["Graph"] is JsonObject s_OnScreen)
            s_OnScreen["Name"] = s_Name;

        var s_Camo = Current(s_Name);
        var s_Root = new JsonObject
        {
            ["Format"] = CamoFileFormat,
            ["Version"] = CamoFileVersion,
            ["Name"] = s_Name,
            ["Family"] = p_Editor.CamoFamilyKey,
            ["Camo"] = JsonSerializer.SerializeToNode(s_Camo),
            // (a soldier's "camo on all the cloth" parts: the skin's answer kept in the session, not in a document)
            ["AllCloth"] = new JsonArray(m_AllCloth.Select(p_P => (JsonNode?) p_P).ToArray()),
            ["Active"] = s_Active,
            ["Tabs"] = s_Tabs,
        };

        // ⛔ NOTHING OF THIS MACHINE IN THE FILE (no machine-locked artifacts): every text that names an existing file is a picture the
        // camo uses — the pattern on a node, a sticker, the animated sticker's GIF, the row's picture… Each is copied into <name>_files and
        // named relative to the file; one shipped with the studio is named relative to the studio. Two pictures of one name from two folders
        // are told apart (name_2); a picture already in that folder (a camo opened from here and saved again) keeps its name and is not copied.
        var s_Folder = Path.GetDirectoryName(s_Path)!;
        var s_FilesName = s_Stem + "_files";
        var s_Files = Path.Combine(s_Folder, s_FilesName);
        var s_Studio = Path.GetFullPath(AppContext.BaseDirectory);
        var s_Pictures = new List<string>();
        var s_Missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Existing(string p_Text)
        {
            if (p_Text.Length < 4 || !Path.IsPathFullyQualified(p_Text))
                return null;
            try
            {
                var s_Full = Path.GetFullPath(p_Text);
                if (File.Exists(s_Full))
                    return s_Full;
            }
            catch (Exception)
            {
                return null;
            }

            s_Missing.Add(p_Text);
            return null;
        }

        RewriteTexts(s_Root, p_Text =>
        {
            if (Existing(p_Text) is { } s_Full && !s_Pictures.Contains(s_Full, StringComparer.OrdinalIgnoreCase))
                s_Pictures.Add(s_Full);
            return null;
        });

        var s_References = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var s_Taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s_Copied = 0;
        bool Inside(string p_File, string p_Folder) =>
            p_File.StartsWith(p_Folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // (the ones already in the pictures' folder first: their names are theirs)
        foreach (var s_Full in s_Pictures.Where(p_P => Inside(p_P, s_Files) && Path.GetDirectoryName(p_P)!.Equals(s_Files, StringComparison.OrdinalIgnoreCase)))
            if (s_Taken.Add(Path.GetFileName(s_Full)))
                s_References[s_Full] = $"{CamoFileRef}{s_FilesName}/{Path.GetFileName(s_Full)}";

        try
        {
            foreach (var s_Full in s_Pictures.Where(p_P => !s_References.ContainsKey(p_P)))
            {
                if (Inside(s_Full, s_Studio))
                {
                    s_References[s_Full] = StudioFileRef + Path.GetRelativePath(s_Studio, s_Full).Replace('\\', '/');
                    continue;
                }

                var s_FileName = Path.GetFileName(s_Full);
                for (var i = 2; !s_Taken.Add(s_FileName); i++)
                    s_FileName = $"{Path.GetFileNameWithoutExtension(s_Full)}_{i}{Path.GetExtension(s_Full)}";

                Directory.CreateDirectory(s_Files);
                File.Copy(s_Full, Path.Combine(s_Files, s_FileName), true);
                s_Copied++;
                s_References[s_Full] = $"{CamoFileRef}{s_FilesName}/{s_FileName}";
            }

            RewriteTexts(s_Root, p_Text => Existing(p_Text) is { } s_Full && s_References.TryGetValue(s_Full, out var s_Reference) ? s_Reference : null);
            Directory.CreateDirectory(s_Folder);
            File.WriteAllText(s_Path, s_Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new System.Text.UTF8Encoding(false));
        }
        catch (Exception s_Exception)
        {
            Log($"Save camo: ERROR writing {s_Path}: {s_Exception.Message}");
            return null;
        }

        var s_Documents = s_Tabs.OfType<JsonObject>().Sum(p_T =>
            (p_T["DocumentSubject"] != null ? 1 : 0) +
            ((p_T["Natives"] as JsonObject)?.Sum(p_N => (p_N.Value?["Subjects"] as JsonObject)?.Count ?? 0) ?? 0));
        Log($"Camo '{s_Name}' saved ready to bake: {s_Path} — {s_Tabs.Count} tab(s), {s_Documents} subject document(s), " +
            $"{s_References.Count} picture(s) ({s_Copied} copied into {s_FilesName}), the bake's answers" +
            (s_Camo.Weapons.Count > 0 && !s_Camo.Weapons.Contains("none", StringComparer.OrdinalIgnoreCase) ? $" ({s_Camo.Weapons.Count} weapon(s) ticked)" : "") +
            ". Open camo… brings it back; Bake bakes it." +
            (s_Missing.Count > 0 ? $" NOTE: {s_Missing.Count} picture path(s) of it point at files that are gone and stay as they were: " +
                                   string.Join(", ", s_Missing.Take(4).Select(Path.GetFileName)) + (s_Missing.Count > 4 ? ", …" : "") + "." : ""));
        return s_Path;
    }

    /// <summary>The "Open camo…" button: asks which file, then <see cref="OpenCamoFrom"/>.</summary>
    public void StartOpenCamo(RimeShaderEditor.MainWindow p_Editor)
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open camo (ready to bake)",
            Filter = "Camo ready to bake (*.camo.json)|*.camo.json|All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog(p_Editor) != true)
            return;

        OpenCamoFrom(p_Editor, s_Dialog.FileName);
    }

    /// <summary>
    /// Opens a camo saved by <see cref="SaveCamoTo"/>: its pictures found beside the file (wherever it was moved with its folder), the bake
    /// dialog's answers back on the camo of its name, and its tab(s) opened — the one that was on screen taking the window, its subject
    /// picked again (the pick in flight is the editor's LastMeshPick). False when the file is not a camo (said in the Output).
    /// </summary>
    internal bool OpenCamoFrom(RimeShaderEditor.MainWindow p_Editor, string p_Path)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        JsonObject? s_Root;
        try
        {
            s_Root = JsonNode.Parse(File.ReadAllText(p_Path)) as JsonObject;
        }
        catch (Exception s_Exception)
        {
            Log($"Open camo: ERROR reading {p_Path}: {s_Exception.Message}");
            return false;
        }

        static string? Text(JsonNode? p_Node) => p_Node is JsonValue s_Value && s_Value.TryGetValue<string>(out var s_Text) ? s_Text : null;

        if (Text(s_Root?["Format"]) != CamoFileFormat || s_Root!["Tabs"] is not JsonArray s_Tabs)
        {
            Log($"Open camo: {Path.GetFileName(p_Path)} is not a camo saved with Save camo… (a document saved with Save… opens with Open…).");
            return false;
        }

        var s_Folder = Path.GetDirectoryName(Path.GetFullPath(p_Path))!;
        var s_Missing = new List<string>();
        RewriteTexts(s_Root, p_Text =>
        {
            var s_Full = p_Text.StartsWith(CamoFileRef, StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(s_Folder, p_Text[CamoFileRef.Length..]))
                : p_Text.StartsWith(StudioFileRef, StringComparison.Ordinal)
                    ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p_Text[StudioFileRef.Length..]))
                    : null;
            if (s_Full != null && !File.Exists(s_Full) && !s_Missing.Contains(s_Full, StringComparer.OrdinalIgnoreCase))
                s_Missing.Add(s_Full);
            return s_Full;
        });

        var s_Name = Text(s_Root["Name"]) ?? "";
        if (IsCamoName(s_Name) && s_Root["Camo"]?.Deserialize<Camo>() is { } s_Saved)
        {
            // the answers the dialog asks with, on the camo of that name (Current: the one the bake dialog finds by the tab's name)
            var s_Camo = Current(s_Name);
            s_Camo.Description = s_Saved.Description;
            s_Camo.Thumbnail = s_Saved.Thumbnail;
            s_Camo.ImagePath = s_Saved.ImagePath;
            s_Camo.Tiling = s_Saved.Tiling;
            s_Camo.Wear = s_Saved.Wear;
            s_Camo.SimpleEdited = s_Saved.SimpleEdited;
            s_Camo.Weapons = new List<string>(s_Saved.Weapons);
            s_Camo.Vehicles = new List<string>(s_Saved.Vehicles);
            s_Camo.Family = s_Saved.Family;
            s_Camo.Accessories = s_Saved.Accessories;
            s_Camo.DefaultLook = s_Saved.DefaultLook;
            s_Camo.PartThumbnails = new Dictionary<string, string>(s_Saved.PartThumbnails, StringComparer.OrdinalIgnoreCase);
            s_Camo.PartFamilies = new Dictionary<string, string>(s_Saved.PartFamilies, StringComparer.OrdinalIgnoreCase);
            s_Camo.PartNames = new Dictionary<string, string>(s_Saved.PartNames, StringComparer.OrdinalIgnoreCase);
            s_Camo.PartDescriptions = new Dictionary<string, string>(s_Saved.PartDescriptions, StringComparer.OrdinalIgnoreCase);
        }

        if (s_Root["AllCloth"] is JsonArray s_AllCloth)
            foreach (var s_Part in s_AllCloth.Select(Text).OfType<string>())
                SetAllCloth(s_Part, true);

        var s_Opened = p_Editor.OpenCamoTabs(s_Tabs,
            s_Root["Active"] is JsonValue s_At && s_At.TryGetValue<int>(out var s_Active) ? s_Active : 0);
        if (s_Opened == 0)
        {
            Log($"Open camo: {Path.GetFileName(p_Path)} holds no tab to open.");
            return false;
        }

        var s_Weapons = Current(s_Name).Weapons;
        Log($"Opened camo '{s_Name}' from {p_Path} in {(s_Opened == 1 ? "a tab of its own" : $"{s_Opened} tabs of its own")}, ready to bake" +
            (s_Weapons.Count > 0 && !s_Weapons.Contains("none", StringComparer.OrdinalIgnoreCase) ? $" — the bake dialog comes with its {s_Weapons.Count} weapon(s) ticked" : "") +
            "." + (s_Missing.Count > 0 ? $" NOTE: {s_Missing.Count} of its picture(s) are not beside it any more ({string.Join(", ", s_Missing.Take(4).Select(Path.GetFileName))}) — " +
                                         "move the camo file together with its _files folder." : ""));
        return true;
    }

    /// <summary>
    /// A skin name -> the display of the soldier ANOTHER skin of that name is baked for in the framework (a skin is known by its name alone:
    /// its folder and its identifier), or null — read once from the framework's packages; nothing when the folder is not the framework.
    /// </summary>
    private static Func<string, string?> SkinsBakedForOthers(string p_Mod, string p_Soldier)
    {
        var s_Baked = new Dictionary<string, PackageSoldier>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (FrameworkMod.Validate(p_Mod) == null)
                foreach (var s_Package in FrameworkMod.Scan(p_Mod))
                    if (s_Package.Soldier != null)
                        s_Baked[s_Package.Folder] = s_Package.Soldier;
        }
        catch (Exception)
        {
            // a framework that cannot be read has nothing to warn about here; the bake says what it finds
        }

        return p_Name => s_Baked.TryGetValue(CamoBaker.SkinFolderOf(p_Name), out var s_Other) &&
                         !s_Other.Key.Equals(p_Soldier, StringComparison.OrdinalIgnoreCase)
            ? s_Other.Display
            : null;
    }

    /// <summary>
    /// Every soldier with something a skin would take, over EVERY tab of the session (several soldiers open, a tab per part — which one
    /// does the bake target?): the parts with a changed document — ChangedDocumentsUnder, the bake's own reading — and the tabs
    /// they are in; in the catalogue's order, the one on screen said.
    /// </summary>
    internal List<View.SoldierToBake> SoldiersToBake(RimeShaderEditor.MainWindow p_Editor)
    {
        // (and the parts with the Soldiers panel's "camo on all the cloth" ticked: a skin can be that alone — review 2026-09-29, such a soldier
        // was never listed, and the button went to another one)
        var s_Keys = p_Editor.SubjectKeysInSession().Concat(m_AllCloth).ToList();
        var s_OnScreen = SoldierOnScreen(p_Editor)?.Key;
        var s_Result = new List<View.SoldierToBake>();
        foreach (var s_Soldier in m_Soldiers.Soldiers)
        {
            var s_Prefix = $"soldiers/{s_Soldier.Key}/";
            if (!s_Keys.Any(p_K => p_K.StartsWith(s_Prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            var s_Parts = new List<string>();
            var s_Tabs = new List<string>();
            foreach (var s_Part in new[] { "head", "upper", "lower" })
            {
                var s_Changed = p_Editor.ChangedDocumentsUnder(s_Prefix + s_Part);
                var s_AllCloth = m_AllCloth.Contains(s_Prefix + s_Part);
                if (s_Changed.Count == 0 && !s_AllCloth)
                    continue;

                s_Parts.Add(PartDisplay(s_Part) + (s_Changed.Count == 0 ? " (all the cloth)" : ""));
                s_Tabs.AddRange(s_Changed.Select(p_C => p_C.Tab));
            }

            if (s_Parts.Count > 0)
                s_Result.Add(new View.SoldierToBake(s_Soldier.Key, s_Soldier.Display, s_Parts, s_Tabs.Distinct(StringComparer.Ordinal).ToList(),
                    string.Equals(s_Soldier.Key, s_OnScreen, StringComparison.OrdinalIgnoreCase)));
        }

        return s_Result;
    }

    /// <summary>
    /// Whom the Bake button bakes a skin for, before its dialog (with several different soldiers open, the first thing Bake does is ask
    /// which soldier): two or more soldiers with something to bake — ask; one — him, no
    /// question; none — the soldier on screen, as before (the dialog says there is nothing of him to bake yet).
    /// </summary>
    internal static (bool Ask, string? Soldier) WhoToBake(IReadOnlyList<View.SoldierToBake> p_Soldiers) =>
        p_Soldiers.Count switch
        {
            0 => (false, null),
            1 => (false, p_Soldiers[0].Key),
            _ => (true, null),
        };

    /// <summary>The soldier whose skin a bake measures now — SkinOnScreen's own choice — or null.</summary>
    private SoldierEntry? SoldierOnScreen(RimeShaderEditor.MainWindow p_Editor) =>
        p_Editor.CamoSnapshot().Picked is { } s_Picked ? SoldierWearing(s_Picked) : m_ActiveSoldier;

    /// <summary>
    /// Puts a soldier on screen for his skin's bake: a tab of his changes brought back (its own click, his part with it), else him picked in
    /// the Soldiers list — so the bake dialog, the preview behind it and the bake itself (which measures the soldier on screen, and refuses
    /// another) are all his. True when he is on screen.
    /// </summary>
    internal async Task<bool> BringSoldierOnScreenAsync(RimeShaderEditor.MainWindow p_Editor, string p_Key)
    {
        if (m_Soldiers.Soldiers.FirstOrDefault(p_S => p_S.Key.Equals(p_Key, StringComparison.OrdinalIgnoreCase)) is not { } s_Soldier)
            return false;

        if (string.Equals(SoldierOnScreen(p_Editor)?.Key, p_Key, StringComparison.OrdinalIgnoreCase))
            return true;

        var s_Before = p_Editor.LastMeshPick;
        if (!p_Editor.ShowChangesTabOf($"soldiers/{p_Key}/"))
        {
            // no tab of changes of his (a part only on one of his stock looks): him from the Soldiers list
            if (p_Editor.CamoSnapshot().Group != s_Soldier.Team)
                p_Editor.PressCamoGroup(s_Soldier.Team);
            if (!p_Editor.SelectCamoWeapon(s_Soldier.PreviewMesh))
                return false;
        }

        if (!ReferenceEquals(s_Before, p_Editor.LastMeshPick) && p_Editor.LastMeshPick is { } s_Pick)
            await s_Pick;

        return string.Equals(SoldierOnScreen(p_Editor)?.Key, p_Key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The "Bake camo…" button: the camo of the ACTIVE TAB — its graph, its pattern, its values — becomes a
    /// package inside the camo framework mod and is registered there. Asks for the framework folder once
    /// (kept in Settings), then the name, the weapons, the row's text and picture, then builds in the
    /// background with every step in the editor's Output. On the Soldiers tab it first settles WHICH soldier (WhoToBake).
    /// </summary>
    public async void StartBake(RimeShaderEditor.MainWindow p_Editor)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        // ⛔ ONE BAKE AT A TIME (review 2026-09-29): the build runs in the background and the button stayed live — a second press built a
        // second package into the same framework at the same time (mod.json, the indexes, and the package folder itself when the name is the
        // same), and a press while the first was still bringing its soldier on screen opened a second dialog. CAMO_BAKE_TWICE_OLD=1 = as before.
        if (Environment.GetEnvironmentVariable("CAMO_BAKE_TWICE_OLD") != "1" && (m_BakeStarting || m_BakeInFlight is { IsCompleted: false }))
        {
            Log(m_BakeStarting
                ? "Bake: the last press is still being set up — finish or cancel its dialog first."
                : "Bake: a bake is still building — wait for its 'BAKE OK' or 'Bake failed' line in the Output, then press Bake again.");
            return;
        }

        m_BakeStarting = true;
        try
        {
            m_BakeInFlight = await StartBakeCore(p_Editor);
        }
        finally
        {
            m_BakeStarting = false;
        }
    }

    /// <summary>The bake building now (the button's), or null: one at a time (StartBake).</summary>
    private Task<BakeResult?>? m_BakeInFlight;

    /// <summary>For a seam: a build in flight as the button would hold it (the press after it must be refused before any dialog).</summary>
    internal void HoldBakeInFlight(Task<BakeResult?>? p_Build) => m_BakeInFlight = p_Build;

    /// <summary>The button between its press and the build (the framework asked, the soldier brought on screen, the dialog open).</summary>
    private bool m_BakeStarting;

    /// <summary>What the button does up to the build: returns the build running in the background, or null when nothing was started.</summary>
    private async Task<Task<BakeResult?>?> StartBakeCore(RimeShaderEditor.MainWindow p_Editor)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        var s_Mod = ResolveFrameworkFolder(p_Editor, p_Editor.Settings, Log);
        if (s_Mod == null)
        {
            Log("Bake cancelled: no framework folder chosen.");
            return null;
        }

        // ⭐ WHICH SOLDIER (with several soldiers' tabs open there was no telling whom the bake was for: ask which soldier to bake, then the
        // normal bake menu opens): the bake measures the soldier ON SCREEN, so the one chosen
        // is brought on screen first — the dialog, the preview behind it and the bake are then his. CAMO_BAKE_WHO_OLD=1 = as before (the
        // soldier on screen, no question).
        if (string.Equals(p_Editor.CamoFamilyKey, "soldiers", StringComparison.OrdinalIgnoreCase) &&
            Environment.GetEnvironmentVariable("CAMO_BAKE_WHO_OLD") != "1")
        {
            var s_Soldiers = SoldiersToBake(p_Editor);
            var (s_Ask, s_Only) = WhoToBake(s_Soldiers);
            var s_Chosen = s_Ask ? View.BakeSoldierChooser.Ask(p_Editor, s_Soldiers) : s_Only;
            if (s_Ask && s_Chosen == null)
            {
                Log("Bake cancelled.");
                return null;
            }

            if (s_Chosen != null)
            {
                var s_Display = s_Soldiers.First(p_S => p_S.Key == s_Chosen).Display;
                var s_WasOnScreen = s_Soldiers.First(p_S => p_S.Key == s_Chosen).OnScreen;
                if (!await BringSoldierOnScreenAsync(p_Editor, s_Chosen))
                {
                    Log($"Bake: {s_Display} could not be brought on screen — pick him in the Soldiers list and press Bake again.");
                    return null;
                }

                if (!s_WasOnScreen)
                    Log(s_Ask
                        ? $"Bake: {s_Display} chosen — he is on screen, and the bake menu is his."
                        : $"Bake: {s_Display} is the only soldier with changes in your tabs — he is on screen, and the bake menu is his.");
            }
        }

        if (NewBakeDialog(p_Editor, s_Mod).ShowAndAnswer(p_Editor) is not { } s_Request)
        {
            Log("Bake cancelled.");
            return null;
        }

        return BakeRequested(p_Editor, s_Mod, s_Request);
    }

    /// <summary>
    /// The bake dialog for what is on screen, built and not shown: <see cref="StartBake"/> shows it, and a seam reads and
    /// answers the SAME dialog (`--vehiclebaketest`) and hands the answer to <see cref="BakeRequested"/>, the rest of the button.
    /// </summary>
    internal View.BakeDialog NewBakeDialog(RimeShaderEditor.MainWindow p_Editor, string p_Mod)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        // The tab's graph name starts the dialog off; the preset's own name (what a fresh tab is called
        // until the user renames it) and "Untitled" are not camo names and leave the box empty to be typed.
        var s_GraphName = p_Editor.GraphNameBox.Text.Trim();
        var s_Suggested = IsCamoName(s_GraphName) ? s_GraphName : "";

        // ⭐ THE DIALOG FOLLOWS WHAT IS ON SCREEN (the bake menu's options appear or disappear depending on
        // what exactly is being baked). With a PIECE
        // picked, the weapons are not a question — the piece answers them — so the dialog asks the short form
        // and the package paints that piece alone. The pair (weapon, tag) is what names it: its mesh cannot,
        // being the same object on dozens of guns.
        var s_OnScreen = p_Editor.CamoSnapshot();

        // ⭐ AND THE TAB DECIDES THE KIND (on vehicles the whole weapons section disappears, and the other way
        // round): on the Vehicles tab the dialog asks vehicles only, on the Weapons tab weapons only — the
        // tab, not the subject picked, so a Vehicles tab with nothing picked yet still asks vehicles.
        var s_VehicleTab = string.Equals(p_Editor.CamoFamilyKey, "vehicles", StringComparison.OrdinalIgnoreCase);
        // ⭐ …and the Soldiers tab bakes a SKIN of the soldier on screen: no weapons, no vehicles, no pieces
        var s_SoldierTab = string.Equals(p_Editor.CamoFamilyKey, "soldiers", StringComparison.OrdinalIgnoreCase);
        var s_Piece = s_VehicleTab || s_SoldierTab ? null : PieceForBake(s_OnScreen.SelectedAccessoryWeapon, s_OnScreen.SelectedAccessoryTag);
        if (s_Piece != null)
            Log($"Baking the piece on screen: {s_Piece.Display} of {s_Piece.Weapon} — the dialog asks how far it " +
                $"reaches, not which weapons ({s_Piece.Carriers.Count} carry this piece).");
        else if (!s_VehicleTab && !s_SoldierTab && s_OnScreen.SelectedAccessoryTag.Length > 0)
            Log($"NOTE: '{s_OnScreen.SelectedAccessoryTag}' has no mesh a camo can go on, so this bakes a camo for " +
                "the weapons, as usual.");

        var s_Known = s_Suggested.Length > 0 ? Current(s_Suggested) : null;

        View.BakeSoldier? s_Skin = null;
        if (s_SoldierTab)
        {
            var s_Measured = SkinOnScreen(p_Editor);
            s_Skin = s_Measured == null
                ? new View.BakeSoldier { Display = "(no soldier on screen)" }
                : new View.BakeSoldier
                {
                    Key = s_Measured.Soldier.Key,
                    Display = s_Measured.Soldier.Display,
                    Parts = s_Measured.Parts.Select(p_P => new View.BakeSoldierPart
                    {
                        Part = p_P.Part,
                        Display = PartDisplay(p_P.Part),
                        Carries = p_P.Carries,
                        Changed = p_P.Changed,
                        HasCloth = p_P.HasCloth,
                        MaskPicture = p_P.Pictures.Any(p_X => p_X.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase)),
                        Note = string.Join(" ", p_P.Notes),
                        // what its own cell's picture is cut out of, and what was chosen for that cell last time
                        Pattern = p_P.Pictures.FirstOrDefault(p_X => p_X.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)) is { } s_Pattern
                            ? Path.GetFileName(s_Pattern.File)
                            : p_P.Look != null ? $"the game's look {p_P.Look.Display}" : "",
                        Thumbnail = s_Known?.PartThumbnails.GetValueOrDefault(p_P.Part) ?? "",
                        Family = s_Known?.PartFamilies.GetValueOrDefault(p_P.Part) ?? "",
                        Name = s_Known?.PartNames.GetValueOrDefault(p_P.Part) ?? "",
                        Description = s_Known?.PartDescriptions.GetValueOrDefault(p_P.Part) ?? "",
                        NotForOthers = p_P.NotForOthers,
                        // ⭐ one state with the Soldiers panel's box, and the warning for a look whose mask puts the camo nowhere
                        AllCloth = AllClothOf($"soldiers/{s_Measured.Soldier.Key}/{p_P.Part}"),
                        StockMaskHidden = string.Join(", ", PartMeshesHidingCamo($"soldiers/{s_Measured.Soldier.Key}/{p_P.Part}",
                            p_P.Look != null ? LookPrefix + p_P.Look.Name : null)),
                    }).ToList(),
                    // ⭐ every soldier a part can go to too (ticked per part)
                    Everyone = Soldiers.Soldiers.Select(p_S => new View.BakeSoldierChoice
                    {
                        Key = p_S.Key, Team = p_S.Team, Kit = p_S.Kit, Aftermath = p_S.IsAftermath,
                    }).ToList(),
                    // ⛔ a name another soldier's skin already has (review 2026-09-29): the framework's skins, read once
                    BakedForAnother = SkinsBakedForOthers(p_Mod, s_Measured.Soldier.Key),
                };
            Log(s_Measured == null
                ? "Bake: no soldier on screen — pick one in the Soldiers list first."
                : $"Baking a skin of {s_Measured.Soldier.Display} — the WHOLE soldier: " +
                  string.Join("; ", s_Measured.Parts.Select(p_P => $"{PartDisplay(p_P.Part)} {(p_P.Changed ? p_P.Carries : "unchanged")}")) + ".");
        }

        // A vehicle on screen is ticked in the dialog's vehicle list: the camo being authored is that vehicle's.
        var s_OnVehicle = s_VehicleTab && s_OnScreen.Picked != null ? m_Vehicles.VehicleOfBody(s_OnScreen.Picked) : null;

        // ⭐ …AND THE WEAPONS AND VEHICLES TICKED LAST TIME (the camo saved ready to bake and opened again: the dialog comes back filled
        // in, with the same weapons ticked) — remembered like the name, the text, the category and the attachments always
        // were: the weapons exactly as ticked (none remembered = every weapon, as before), the vehicles with the one on screen added.
        // CAMO_BAKE_TICKS_OLD=1 = every weapon and the vehicle on screen alone, as before.
        var s_Remember = Environment.GetEnvironmentVariable("CAMO_BAKE_TICKS_OLD") != "1";
        var s_VehiclesTicked = (s_Remember && s_Known != null ? s_Known.Vehicles : new List<string>())
            .Concat(s_OnVehicle != null ? new[] { VehicleCatalog.KeyOf(s_OnVehicle) } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var s_Dialog = View.BakeDialog.Build(p_Editor, m_Weapons, s_Suggested, s_Known?.Description ?? "",
            s_Known?.Thumbnail, p_Mod, s_Known?.Family ?? "", s_Known?.Accessories ?? false,
            p_Folders => ExpandAccessoryNames(AllAccessories, p_Folders).Count, s_Piece,
            s_VehicleTab ? m_Vehicles : null, s_VehiclesTicked.Count > 0 ? s_VehiclesTicked : null,
            s_VehicleTab, s_Skin, s_Known?.DefaultLook ?? false);

        if (s_Remember && !s_VehicleTab && !s_SoldierTab && s_Piece == null && s_Known is { Weapons.Count: > 0 } &&
            !s_Known.Weapons.Contains("none", StringComparer.OrdinalIgnoreCase))
        {
            var s_Ticked = s_Dialog.TickOnlyWeapons(s_Known.Weapons);
            Log($"Bake: '{s_Known.Name}' comes with the {s_Ticked} weapon(s) ticked when it was last baked or saved.");
        }

        return s_Dialog;
    }

    /// <summary>One part of the soldier on screen as a skin's bake reads it (see <see cref="SkinOnScreen"/>).</summary>
    /// <param name="Meshes">Its third-person meshes.</param>
    /// <param name="HasCloth">Whether any material of it wears the camo cloth (its entry binds CamoTile): where a skin's textures go.</param>
    /// <param name="Pictures">The pictures on its changed documents' texture nodes, by the parameter they are bound under.</param>
    /// <param name="Values">The numbers typed on them that differ from the game's.</param>
    /// <param name="Notes">What was changed on it that a skin cannot carry, said.</param>
    /// <param name="Shaders">Its documents that need a shader of their own (see <see cref="SkinShader"/>).</param>
    /// <param name="Look">The game's look it starts from ("preview native camo" on a soldier, per part), or null: his own.</param>
    internal sealed record SkinPart(string Part, List<string> Meshes, bool HasCloth, List<SoldierPicture> Pictures,
        Dictionary<string, string> Values, List<string> Notes, List<SkinShader> Shaders, SoldierLook? Look = null)
    {
        /// <summary>Something of it that the skin carries was changed — one of the game's looks as its start included.</summary>
        public bool Changed => Pictures.Count > 0 || Values.Count > 0 || Shaders.Count > 0 || Look != null;

        /// <summary>
        /// Why it cannot go to other soldiers, or null when it can (only a SIMPLE part — its pattern, its mask, its numbers):
        /// a picture on another parameter is painted on THIS model's layout; a graph of its own is a copy of his meshes' shader (the same rule
        /// as the bake's, CamoBaker.WhyNotForOthers). A picture on Mask no longer holds the part back (the US support's legs start from
        /// one): it is this model's, and each other soldier keeps his own mask.
        /// </summary>
        public string? NotForOthers =>
            Shaders.Count > 0 ? "its graph has nodes or wires of its own"
            : Pictures.FirstOrDefault(p_P => !p_P.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase) &&
                                             !p_P.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase)) is { } s_Other
                ? $"a picture on {s_Other.Parameter} is painted on this model's layout"
            : null;

        /// <summary>What it carries, in the user's words.</summary>
        public string Carries => string.Join(", ", (Look != null ? new[] { $"the game's look {Look.Display}" } : Array.Empty<string>())
            .Concat(Shaders.Select(p_S => $"its own shader on {p_S.Shader.Split('/')[^1]} " +
                                          (p_S.LogicEdited ? "(the graph's nodes and wires)" : "(for its pictures)")))
            .Concat(Pictures.Select(p_P => (p_P.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)
                                               ? "pattern "
                                               : p_P.Parameter + " ") + Path.GetFileName(p_P.File)))
            .Concat(Values.Count > 0 ? new[] { $"{Values.Count} number(s) ({string.Join(", ", Values.Keys)})" } : Array.Empty<string>()));
    }

    /// <summary>
    /// A document of a soldier part that needs a SHADER OF ITS OWN in the skin (a graph with nodes or wires of its own):
    /// its logic was edited (nodes, wires), or it carries a picture at a register the shader has no parameter at — only a copy can declare
    /// one. The bake compiles it into a copy of <paramref name="Shader"/> that the part's materials wearing that shader draw with.
    /// </summary>
    internal sealed record SkinShader(string Shader, RimeShaderEditor.Graph.ShaderGraph Document, string Tab, bool LogicEdited,
        List<(string Parameter, int Register)> Declared);

    /// <summary>The soldier on screen and his three parts, measured for a skin's bake.</summary>
    internal sealed record SkinMeasure(SoldierEntry Soldier, IReadOnlyList<SkinPart> Parts);

    /// <summary>
    /// The soldier on screen as a SKIN's bake reads him (when baking, the studio detects ALL the modified parts of that soldier): each
    /// part's documents CHANGED from the game's translation, in every tab (MainWindow.ChangedDocumentsUnder — his Aftermath
    /// torso and his first-person arms draw on two shaders, two tabs), and what of them a skin carries: the pictures on its texture nodes (the
    /// pattern is the one on CamoTile), the numbers typed, and — for a graph whose nodes or wires changed, or with a picture at a register the
    /// shader has no parameter at — a shader of its own. What it cannot carry is said per part: the materials' own graphs,
    /// stickers, and a picture or a number alone on a part with no camo cloth (Aftermath's RU heads: the headgear is painted in the face's own
    /// texture). Null when no soldier is on screen.
    /// </summary>
    internal SkinMeasure? SkinOnScreen(RimeShaderEditor.MainWindow p_Editor)
    {
        var s_Picked = p_Editor.CamoSnapshot().Picked;
        var s_Soldier = s_Picked != null ? SoldierWearing(s_Picked) : m_ActiveSoldier;
        if (s_Soldier == null)
            return null;

        var s_Arms = s_Soldier.ArmsMesh;
        IReadOnlyCollection<int> s_Trousers = s_Arms.Length > 0 ? TrousersMaterialsOf(s_Arms) : new HashSet<int>();
        var s_Parts = new List<SkinPart>();
        foreach (var s_Part in new[] { "head", "upper", "lower" })
        {
            var s_Meshes = s_Soldier.MeshesOf(s_Part).ToList();

            var s_Pictures = new List<SoldierPicture>();
            var s_Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var s_Notes = new List<string>();
            var s_Shaders = new List<SkinShader>();
            // (what is said of its pictures and numbers)
            var s_PictureNotes = new List<string>();
            // what was put on a document with NO shader of its own, on a part with no camo cloth: it has nowhere to go
            var s_Dropped = 0;
            // the documents of the shader his part's first mesh opens first: the one its tab draws, then the others (his first-person arms')
            var s_Main = s_Meshes.Select(ShaderForMesh).FirstOrDefault(p_S => p_S != null) ?? "";
            var s_Documents = p_Editor.ChangedDocumentsUnder($"soldiers/{s_Soldier.Key}/{s_Part}")
                .OrderByDescending(p_D => p_D.Document.TargetShader.Equals(s_Main, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // ⭐ THE GAME'S LOOK THE PART STARTS FROM ("preview native camo" per part — a vanilla camo as the template): the
            // camo of the picker its documents are on, when it is one of his stock looks that puts the same meshes on the part (the skin's
            // entries are then copied from that look's, LookMisfit); the first document that names one decides, and a second is said
            SoldierLook? s_Look = null;
            foreach (var s_Name in s_Documents.Select(p_D => LookNameOf(p_D.Document.NativeCamo)).Where(p_N => p_N != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (s_Soldier.LookNamed(s_Name!) is not { } s_Found)
                    s_Notes.Add($"The look '{s_Name!.Replace('_', ' ')}' is not one of {s_Soldier.Display}'s: the part starts from his own.");
                else if (s_Soldier.LookMisfit(s_Found, s_Part) is { } s_Why)
                    s_Notes.Add($"{char.ToUpperInvariant(s_Why[0])}{s_Why[1..]}: the part starts from his own look.");
                else if (s_Look == null)
                    s_Look = s_Found;
                else if (s_Look != s_Found)
                    s_Notes.Add($"Its graphs are on two looks ({s_Look.Display}, {s_Found.Display}): one part starts from one, {s_Look.Display}.");
            }

            var s_LookKey = s_Look != null ? LookPrefix + s_Look.Name : null;

            // the camo cloth of the part, as he wears it: a material of its meshes whose entry (in the variation of the look it starts from)
            // binds CamoTile — and the first-person arms' own, split by the trousers
            bool Cloth(string p_Mesh, Func<int, bool> p_Material) => Scan.Any(p_B =>
                p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == PreviewVariationOf(p_Mesh, s_LookKey) &&
                p_Material(p_B.MaterialId) && p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)));
            var s_HasCloth = s_Meshes.Any(p_M => Cloth(p_M, _ => true)) ||
                             (s_Arms.Length > 0 && s_Part != "head" && Cloth(s_Arms, p_Id => s_Trousers.Contains(p_Id) == (s_Part == "lower")));
            foreach (var s_Changed in s_Documents)
            {
                var s_Shader = s_Changed.Document.TargetShader.Split('/')[^1];
                if (s_Changed.Document.MaterialGraphs is { Count: > 0 })
                    s_Notes.Add($"{s_Changed.Document.MaterialGraphs.Count} material(s) edited on their own graph ({s_Changed.Tab}) do not ship in a skin.");
                if (s_Changed.Document.Stickers is { Count: > 0 })
                    s_Notes.Add($"Its stickers ({s_Changed.Tab}) do not ship in a skin.");
                // (a change the bake now sees on its own — review 2026-09-29, B7 — and says, as the two above)
                if (s_Changed.Document.MaterialsOff is { Count: > 0 } s_Off)
                    s_Notes.Add($"{s_Off.Values.Sum(p_L => p_L.Count)} material(s) kept as shipped ({s_Changed.Tab}): a skin keeps no material apart — " +
                                "they take the skin like the rest of the part.");

                var s_Found = PicturesOf(s_Changed.Document, out var s_Skipped);

                // ⭐ A SHADER OF ITS OWN (a graph with nodes or wires of its own): its logic edited, or a picture at a
                // register the shader has no parameter at (only a copy can declare one) — one copy per shader a part's documents are drawn on
                var s_Declared = s_Found.Where(p_P => p_P.Declare).Select(p_P => (p_P.Parameter, p_P.Register)).ToList();
                var s_Own = s_Changed.LogicEdited || s_Declared.Count > 0;
                if (s_Own)
                {
                    if (s_Shaders.Any(p_S => p_S.Shader.Equals(s_Changed.Document.TargetShader, StringComparison.OrdinalIgnoreCase)))
                    {
                        s_PictureNotes.Add($"Two graphs of this part on '{s_Shader}' ({s_Changed.Tab} and another tab): one skin ships the first.");
                        continue;
                    }

                    s_Shaders.Add(new SkinShader(s_Changed.Document.TargetShader, s_Changed.Document, s_Changed.Tab, s_Changed.LogicEdited, s_Declared));
                }

                // on a part with no camo cloth, only what rides with a shader of its own reaches a material (the ones that wear that shader)
                if (!s_HasCloth && !s_Own)
                {
                    s_Dropped += s_Found.Count + s_Changed.Values.Count;
                    continue;
                }

                foreach (var s_Picture in s_Found)
                {
                    if (s_Pictures.FirstOrDefault(p_P => p_P.Parameter.Equals(s_Picture.Parameter, StringComparison.OrdinalIgnoreCase)) is { } s_Held)
                    {
                        if (!string.Equals(Path.GetFullPath(s_Held.File), Path.GetFullPath(s_Picture.File), StringComparison.OrdinalIgnoreCase))
                            s_PictureNotes.Add($"{s_Picture.Parameter} carries '{Path.GetFileName(s_Picture.File)}' on '{s_Shader}' ({s_Changed.Tab}) and " +
                                               $"'{Path.GetFileName(s_Held.File)}' on another graph of this part: one skin ships one, '{Path.GetFileName(s_Held.File)}'.");
                    }
                    else
                        s_Pictures.Add(new SoldierPicture(s_Picture.Parameter, s_Picture.File, s_Picture.Srgb));
                }

                s_PictureNotes.AddRange(s_Skipped.Select(p_S => $"{p_S} cannot ship."));
                foreach (var (s_Constant, s_Value) in s_Changed.Values)
                    if (!s_Values.TryGetValue(s_Constant, out var s_Held))
                        s_Values[s_Constant] = s_Value;
                    else if (s_Held != s_Value)
                        s_PictureNotes.Add($"{s_Constant} is {s_Value} on '{s_Shader}' ({s_Changed.Tab}) and {s_Held} on another graph of this part: one skin ships {s_Held}.");
            }

            // ⛔ a part with NO camo cloth (Aftermath's RU heads: the headgear is painted in the face's own texture, measured in game 2026-09-28):
            // a picture or a number alone has nowhere to go — one reason, the real one, never "t3 is not a texture of its shader", which is
            // true and points the user at the wrong thing. A graph of its own does ship: it draws the whole material, as the preview shows.
            if (s_Dropped > 0)
                s_Notes.Add("This part has no camo cloth as he wears it (its headgear is painted in the face's own texture): a picture or a number " +
                            "on it cannot ship alone — only with a graph of its own (a node or a wire changed), which draws its whole material, " +
                            "the face included, the way the preview shows it.");
            s_Notes.AddRange(s_PictureNotes);

            s_Parts.Add(new SkinPart(s_Part, s_Meshes, s_HasCloth, s_Pictures, s_Values, s_Notes, s_Shaders, s_Look));
        }

        return new SkinMeasure(s_Soldier, s_Parts);
    }

    /// <summary>
    /// The bake button on the Soldiers tab, past its dialog: the skin of the soldier on screen — the parts changed on him and the ones whose
    /// camo goes on all the cloth — becomes a package in the framework and is registered there (CamoBaker.BuildSoldier). Its own path: nothing
    /// of the weapons' or the vehicles' plan applies to a skin. Returns the build running in the background, or null (said in the Output).
    /// </summary>
    internal Task<BakeResult?>? BakeSkinRequested(RimeShaderEditor.MainWindow p_Editor, string p_Mod, View.BakeRequest p_Request, string? p_WorkRoot)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        var s_Name = p_Request.Name;
        if (!IsCamoName(s_Name))
        {
            Log($"Bake: '{s_Name}' cannot name a skin — it is empty or a preset's name.");
            return null;
        }

        var s_Measured = SkinOnScreen(p_Editor);
        if (s_Measured == null || !string.Equals(s_Measured.Soldier.Key, p_Request.Soldier, StringComparison.OrdinalIgnoreCase))
        {
            Log($"Bake: the dialog asked for a skin of '{p_Request.Soldier}', and {(s_Measured == null ? "no soldier" : s_Measured.Soldier.Display)} is on screen.");
            return null;
        }

        if (!string.Equals(p_Editor.GraphNameBox.Text.Trim(), s_Name, StringComparison.Ordinal))
        {
            p_Editor.GraphNameBox.Text = s_Name;
            Log($"Skin named '{s_Name}' (the tab's graph name follows it).");
        }

        var s_Camo = Current(s_Name);
        s_Camo.Description = p_Request.Description;
        s_Camo.Thumbnail = p_Request.Thumbnail;
        s_Camo.Family = p_Request.Family;
        // each part's own cell, remembered too: reopening the dialog asks with the answer given last time
        s_Camo.PartThumbnails = new Dictionary<string, string>(p_Request.PartThumbnails, StringComparer.OrdinalIgnoreCase);
        s_Camo.PartFamilies = new Dictionary<string, string>(p_Request.PartFamilies, StringComparer.OrdinalIgnoreCase);
        s_Camo.PartNames = new Dictionary<string, string>(p_Request.PartNames, StringComparer.OrdinalIgnoreCase);
        s_Camo.PartDescriptions = new Dictionary<string, string>(p_Request.PartDescriptions, StringComparer.OrdinalIgnoreCase);

        var s_Soldier = s_Measured.Soldier;
        var s_Parts = new List<SoldierBakePart>();
        foreach (var s_Part in s_Measured.Parts)
        {
            foreach (var s_Note in s_Part.Notes)
                Log($"NOTE: {s_Soldier.Display} {PartDisplay(s_Part.Part)}: {s_Note}");

            // ⭐ the dialog's answer is the panel's box from now on (one state: the preview shows what was baked) — where the dialog ASKED it:
            // a part with a picture on its Mask node is not asked (the picture is its mask), and its box was cleared by every bake, so the
            // user's "all the cloth" was gone once the picture came off (review 2026-09-29)
            if (s_Part.HasCloth && !s_Part.Pictures.Any(p_P => p_P.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase)))
                SetAllCloth($"soldiers/{s_Soldier.Key}/{s_Part.Part}", p_Request.WhiteMask.Contains(s_Part.Part, StringComparer.OrdinalIgnoreCase));

            var s_AllCloth = s_Part.HasCloth && p_Request.WhiteMask.Contains(s_Part.Part, StringComparer.OrdinalIgnoreCase) &&
                             !s_Part.Pictures.Any(p_P => p_P.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase));
            if (!s_Part.Changed && !s_AllCloth)
                continue;

            // ⭐ its graphs with a logic of their own, saved as the compiler reads them: the pictures moved off a register the shader reads
            // a fixed texture at (the move PicturesOf measured), and nothing of the window's own (variations, material graphs, pictures'
            // files — they ship as textures, bound by parameter)
            var s_Shaders = new List<SoldierShader>();
            foreach (var s_Own in s_Part.Shaders)
            {
                var s_Work = Path.Combine(p_WorkRoot ?? Path.Combine(Settings.CacheFolder, "camobake"), CamoBaker.SkinFolderOf(s_Name), "graphs");
                Directory.CreateDirectory(s_Work);
                var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(WithPictureMoves(s_Own.Document).ToJson());
                s_Copy.Variations = null;
                s_Copy.BakeVariation = null;
                s_Copy.BakeMesh = null;
                s_Copy.MaterialGraphs = null;
                s_Copy.MaterialsOff = null;
                s_Copy.ShippedGraph = null;
                foreach (var s_Node in s_Copy.Nodes)
                    s_Node.Params.Remove("CustomTexture");

                var s_Path = Path.Combine(s_Work, $"{s_Part.Part}_{Cache.GameCache.SanitizeName(s_Own.Shader)}.json");
                File.WriteAllText(s_Path, s_Copy.ToJson(), new System.Text.UTF8Encoding(false));
                // the declarations his meshes of this part draw that shader with (the first-person arms too: sleeves and trousers alike)
                var s_Decls = s_Part.Meshes.Concat(s_Part.Part != "head" && s_Soldier.ArmsMesh.Length > 0 ? new[] { s_Soldier.ArmsMesh } : Array.Empty<string>())
                    .SelectMany(p_M => DeclarationsOf(p_M, s_Own.Shader))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                s_Shaders.Add(new SoldierShader(s_Own.Shader, s_Path, s_Own.Declared, s_Decls));
            }

            s_Parts.Add(new SoldierBakePart
            {
                Part = s_Part.Part,
                Meshes = s_Part.Meshes,
                Pictures = s_Part.Pictures,
                WhiteMask = s_AllCloth,
                Values = s_Part.Values,
                Shaders = s_Shaders,
                Thumbnail = p_Request.PartThumbnails.GetValueOrDefault(s_Part.Part),
                Family = p_Request.PartFamilies.GetValueOrDefault(s_Part.Part) ?? "",
                Name = p_Request.PartNames.GetValueOrDefault(s_Part.Part) ?? "",
                Description = p_Request.PartDescriptions.GetValueOrDefault(s_Part.Part) ?? "",
                // the game's look it starts from, and that look's own pattern (what its cell's picture is cut out of when it has none)
                Look = s_Part.Look?.Appearance,
                LookPicture = s_Part.Look != null ? LookPictureOf(s_Soldier, s_Part.Look, s_Part.Part) : null,
                // ⭐ the other soldiers ticked for it — only a simple part travels; the dialog never offers the rest
                Others = s_Part.NotForOthers == null ? p_Request.PartSoldiers.GetValueOrDefault(s_Part.Part) ?? new List<string>() : new List<string>(),
            });
            if (s_Part.NotForOthers != null && p_Request.PartSoldiers.ContainsKey(s_Part.Part))
                Log($"NOTE: {s_Soldier.Display} {PartDisplay(s_Part.Part)} goes to him alone — {s_Part.NotForOthers}.");
        }

        // a graph with a logic of its own is compiled: the Windows SDK's shader compiler
        var s_Fxc = "";
        if (s_Parts.Any(p_P => p_P.Shaders.Count > 0))
        {
            s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
            if (s_Fxc.Length == 0)
            {
                Log("Bake: a part's graph changes the shader itself, and fxc.exe (the Windows SDK shader compiler) was not found to compile it. " +
                    "Install the Windows SDK or put fxc.exe on PATH.");
                return null;
            }
        }

        if (s_Parts.Count == 0)
        {
            Log($"Bake: nothing of {s_Soldier.Display} to bake — no part carries a change a skin can ship, and none has its camo on all the cloth.");
            return null;
        }

        var s_Repl = Settings.FindRimeRepl();
        var s_Texconv = RimeShaderEditor.MainWindow.FindTexconv();
        var s_Game = p_Editor.Settings.GamePath.Length > 0 ? p_Editor.Settings.GamePath : Settings.GamePath;
        if (s_Repl == null || s_Texconv == null || !Settings.LooksLikeGame(s_Game))
        {
            Log($"Bake: cannot build — RimeREPL {(s_Repl == null ? "NOT found" : "found")}, texconv " +
                $"{(s_Texconv == null ? "NOT found (expected beside RimeREPL.exe)" : "found")}, game folder " +
                $"{(Settings.LooksLikeGame(s_Game) ? "OK" : "NOT set (Settings)")}.");
            return null;
        }

        // the row's picture of a skin with no picture of its own: his own camo as the studio previews it (the torso's CamoTile, cached)
        var s_Torso = s_Soldier.PreviewMesh;
        var s_StockCamo = Scan.FirstOrDefault(p_B => p_B.Mesh.Equals(s_Torso, StringComparison.OrdinalIgnoreCase) && p_B.Variation == PreviewVariationOf(s_Torso) &&
                                                      p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
            ?.Textures.First(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)).Texture;

        var s_Key = CamoBaker.KeyOf(s_Name);
        var s_Plan = new SoldierBakePlan
        {
            Name = s_Name.Trim(),
            Key = s_Key,
            Folder = CamoBaker.SkinFolderOf(s_Name),
            Description = p_Request.Description,
            Family = p_Request.Family,
            Identifier = CamoBaker.IdentifierOfSkin(s_Name),
            ThumbnailImage = p_Request.Thumbnail,
            // (a skin that ships no pattern: the look its upper body starts from, else any part's, else his own camo)
            FallbackThumbnail = s_Parts.FirstOrDefault(p_P => p_P.Part == "upper")?.LookPicture ??
                                s_Parts.Select(p_P => p_P.LookPicture).FirstOrDefault(p_L => p_L != null) ??
                                (s_StockCamo != null ? CamoBaker.CachedPngOf(s_StockCamo) : null),
            Soldier = new PackageSoldier
            {
                Key = s_Soldier.Key,
                Display = s_Soldier.Display,
                Team = s_Soldier.Team,
                Kit = s_Soldier.Kit,
                Expansion = s_Soldier.Expansion,
                KitAsset = s_Soldier.KitAsset,
                Appearance = s_Soldier.Appearance,
                Arms = s_Soldier.ArmsMesh,
                Trousers = TrousersMaterialsOf(s_Soldier.ArmsMesh).OrderBy(p_Id => p_Id).ToList(),
            },
            Parts = s_Parts,
            // the level his meshes are drawn in: the template his parts' shader copies are cut from
            ShaderLevel = s_Soldier.ShaderDbLevel,
        };

        var s_Context = new BakeContext
        {
            ModFolder = p_Mod,
            WorkDir = Path.Combine(p_WorkRoot ?? Path.Combine(Settings.CacheFolder, "camobake"), s_Plan.Folder),
            RimeRepl = s_Repl,
            Texconv = s_Texconv,
            Fxc = s_Fxc,
            GamePath = s_Game,
            Log = p_Line => p_Editor.Dispatcher.BeginInvoke(new Action(() => p_Editor.LogCamo(p_Line))),
        };

        Log($"Baking skin '{s_Plan.Name}' ({s_Plan.Key}) of {s_Soldier.Display} into {p_Mod} — the WHOLE soldier: " +
            string.Join("; ", s_Parts.Select(p_P => $"{PartDisplay(p_P.Part)}: " +
                                                     string.Join(", ", p_P.Shaders.Select(p_S => $"its own shader on {p_S.Shader.Split('/')[^1]}")
                                                         .Concat(p_P.Pictures.Select(p_X => $"{p_X.Parameter} {Path.GetFileName(p_X.File)}"))
                                                         .Concat(p_P.WhiteMask ? new[] { "camo on all the cloth" } : Array.Empty<string>())
                                                         .Concat(p_P.Values.Select(p_V => $"{p_V.Key}={p_V.Value}"))
                                                         .DefaultIfEmpty("unchanged")))) +
            (s_Measured.Parts.Count > s_Parts.Count
                ? $"; {string.Join(", ", s_Measured.Parts.Where(p_M => s_Parts.All(p_P => p_P.Part != p_M.Part)).Select(p_M => PartDisplay(p_M.Part)))} as the game ships"
                : "") +
            $" — identifier {s_Plan.Identifier}, its own; package camos/{s_Plan.Folder}; the game is mounted, this takes a minute.");

        return Task.Run(() =>
        {
            try
            {
                var s_Result = CamoBaker.BuildSoldier(s_Plan, s_Context);
                s_Context.Log(s_Result.Ok ? $"BAKE OK — {s_Result.Summary}" : $"Bake failed — see {s_Result.LogPath}");
                return s_Result;
            }
            catch (Exception s_Exception)
            {
                var s_Where = string.Join(" <- ", new System.Diagnostics.StackTrace(s_Exception, false).GetFrames()
                    .Select(p_F => p_F.GetMethod())
                    .Where(p_M => p_M != null)
                    .Take(6)
                    .Select(p_M => $"{p_M!.DeclaringType?.Name}.{p_M.Name}"));
                s_Context.Log($"BAKE ERROR: {s_Exception.GetType().Name}: {s_Exception.Message} — at {s_Where}");
                return (BakeResult?) null;
            }
        });
    }

    /// <summary>
    /// The bake button past its dialog: the camo on screen, with what the dialog answered, becomes a package in the framework
    /// and is registered there. Returns the build running in the background (null when it stopped before building, said in
    /// the Output). <paramref name="p_WorkRoot"/> is where the bake's work goes: the studio's cache unless a seam gives its own.
    /// </summary>
    internal Task<BakeResult?>? BakeRequested(RimeShaderEditor.MainWindow p_Editor, string p_Mod, View.BakeRequest p_Request,
        string? p_WorkRoot = null)
    {
        void Log(string p_Line) => p_Editor.LogCamo(p_Line);

        // a soldier's skin has a path of its own: nothing of the weapons' or the vehicles' plan below applies to it
        if (p_Request.Soldier != null)
            return BakeSkinRequested(p_Editor, p_Mod, p_Request, p_WorkRoot);

        var s_Request = p_Request;
        var s_Mod = p_Mod;
        var s_Settings = p_Editor.Settings;
        var s_GraphName = p_Editor.GraphNameBox.Text.Trim();
        var s_WorkRoot = p_WorkRoot ?? Path.Combine(Settings.CacheFolder, "camobake");

        // What the reach choice came to. A camo of a piece paints no body: that is the whole point of it.
        var s_PieceOnly = s_Request.Pieces.Count > 0;

        var s_Name = s_Request.Name;
        if (!IsCamoName(s_Name))
        {
            Log($"Bake: '{s_Name}' cannot name a camo — it is empty or a weapon preset's name.");
            return null;
        }

        // The dialog's name IS the camo's name: the tab follows it, so the document being edited and the
        // package that ships stay one thing with one name.
        if (!string.Equals(s_GraphName, s_Name, StringComparison.Ordinal))
        {
            p_Editor.GraphNameBox.Text = s_Name;
            Log($"Camo named '{s_Name}' (the tab's graph name follows it).");
        }

        // ⛔ The seats the packages already in the framework hold — a package another studio dropped in
        // included — are taken BEFORE this camo's seat is chosen, so a new camo never lands on a row a
        // package already wears. What that cannot cover (two studios naming a camo alike, a package copied
        // in later) Register resolves from disk; see FrameworkMod. The default camos are not restored: their
        // rows are reserved in the pool already, and restoring one under its name would answer a camo named
        // like it with the game's own row.
        // (a soldier skin holds no weapon seat: its identifier is its own and lives in its own index)
        m_Hooks.Restore(FrameworkMod.Scan(s_Mod).Where(p_P => !p_P.Native && p_P.Soldier == null).Select(p_P =>
            (p_P.Name, p_P.Identifier, (IReadOnlyCollection<string>) p_P.Weapons.Select(p_W => p_W.Folder).ToList())));

        var s_Camo = Current(s_Name);
        s_Camo.Thumbnail = s_Request.Thumbnail;
        // what the user chose at the dialog is the camo's from now on, so reopening it asks with that answer
        s_Camo.Family = s_Request.Family;
        s_Camo.Accessories = s_Request.Accessories;
        s_Camo.DefaultLook = s_Request.DefaultLook;
        // (the weapons ticked are written down by PlanFor; the vehicles here — NewBakeDialog ticks them again)
        s_Camo.Vehicles = new List<string>(s_Request.Vehicles);

        var s_Snapshot = p_Editor.CamoSnapshot();
        // ⛔ the CAMO's preset, not the box's: Bake pressed while one of the object's other materials is on the canvas (a vehicle's
        // CROWS on its own preset, a weapon's glass) left the box naming THAT material's shader — the pattern's register, the
        // "edited beyond values" yardstick and the vehicles' graph target were all asked of the wrong preset
        var s_DocumentTarget = s_Snapshot.DocumentTarget.Length > 0 ? s_Snapshot.DocumentTarget : s_Snapshot.Target;
        var s_Register = CamoRegisterFor(s_DocumentTarget);
        // ⭐ the image on ANY of the preset's camo registers (2026-09-25, the F/A-18F: a picture on the CamoA node shipped NOTHING —
        // "packnc.sb 0 KB" — while one on a new node did): the jet family reads CamoA AND CamoB, and asking the one register
        // CamoRegisterFor names left a picture on the other neither the pattern nor a picture (camo registers are the pattern's)
        var s_Pattern = PatternOf(s_Register, CamoRegistersFor(s_DocumentTarget), s_Snapshot.CustomTextures, out var s_OnCamo);
        if (s_OnCamo.Select(p_C => p_C.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            Log($"NOTE: the camo registers carry different images ({string.Join(", ", s_OnCamo.Select(p_C => $"t{p_C.Register}: {Path.GetFileName(p_C.File)}"))}) — " +
                $"one camo ships one pattern, laid on every camo register: '{Path.GetFileName(s_Pattern)}'.");
        else if (s_Pattern != null && s_Register != null && !s_Snapshot.CustomTextures.ContainsKey(s_Register))
            Log($"NOTE: the pattern is the image on t{s_OnCamo[0].Register} (a camo register of '{s_DocumentTarget.Split('/')[^1]}'), laid on all of them.");

        // ⭐ EDITED LOGIC SHIPS (stickers, animated patterns — anything the graph does
        // beyond values): the graph becomes the package's own first-person preset. Saved beside the bake's
        // work, as the file the compiler reads. The third-person model keeps the game's preset — a
        // first-person graph does not describe it — with the pattern and the values.
        string? s_ShaderGraph = null;
        string? s_NoCamoShaderGraph = null;
        StickerRequest? s_Stickers = null;
        var s_Fxc = "";

        // ⭐ THE OTHER MATERIALS' EDITED GRAPHS (the bake must take every material into account, in case any
        // of them changed). Each ships as a clone of the shader THAT
        // material wears; only the ones that really changed are here (the editor measures the emission).
        var s_MaterialWork = Path.Combine(s_WorkRoot, CamoBaker.FolderOf(s_Name), "materials");
        var s_MaterialShaders = new List<MaterialShaderRequest>();
        // every subject's, each from its own document (one document per subject)
        var s_EditedMaterials = p_Editor.EditedMaterialGraphsOfSession().ToList();
        // ⭐ …and the ones whose logic is the game's but that carry a picture where their shader has no parameter (a fixed texture's register,
        // the glass's t2; any custom texture must just work): only a copy of their shader can declare one for it
        foreach (var s_Pictured in p_Editor.MaterialGraphsWithPicturesOfSession())
            if (!s_EditedMaterials.Any(p_E => p_E.Mesh.Equals(s_Pictured.Mesh, StringComparison.OrdinalIgnoreCase) && p_E.MaterialId == s_Pictured.MaterialId) &&
                PicturesOf(s_Pictured.Graph, out _).Any(p_P => p_P.Declare))
                s_EditedMaterials.Add(s_Pictured);

        foreach (var (s_Mesh, s_MaterialId, s_Shader, s_Graph) in s_EditedMaterials)
        {
            Directory.CreateDirectory(s_MaterialWork);
            var s_Path = Path.Combine(s_MaterialWork, $"{Cache.GameCache.SanitizeName(s_Mesh)}_{s_MaterialId}.json");
            // (a picture on a fixed texture's register reads from a free one in the copy: GraphPicture.From)
            var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(WithPictureMoves(s_Graph).ToJson());
            s_Copy.Variations = null;
            s_Copy.BakeVariation = null;
            s_Copy.BakeMesh = null;
            s_Copy.MaterialGraphs = null;
            s_Copy.MaterialsOff = null;
            foreach (var s_Node in s_Copy.Nodes)
                s_Node.Params.Remove("CustomTexture");

            File.WriteAllText(s_Path, s_Copy.ToJson(), new System.Text.UTF8Encoding(false));
            s_MaterialShaders.Add(new MaterialShaderRequest(s_Mesh, s_MaterialId, s_Shader, s_Path));
        }

        if (s_MaterialShaders.Count > 0)
        {
            s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
            if (s_Fxc.Length == 0)
            {
                Log("Bake: this camo edits a material's own shader, and fxc.exe (the Windows SDK shader compiler) was " +
                    "not found to compile it. Install the Windows SDK or put fxc.exe on PATH.");
                return null;
            }

            Log($"NOTE: {s_MaterialShaders.Count} material shader(s) edited in this camo ship as their own clones: " +
                string.Join(", ", s_MaterialShaders.Select(p_M => $"{p_M.Mesh.Split('/')[^1]} #{p_M.MaterialId} ({p_M.Shader.Split('/')[^1]})")) + ".");
        }

        // ⭐ An edited graph reaches VEHICLES too (any manual change to the graph must bake, as it does on attachments and
        // weapons): the vehicle pieces whose materials wear the preset it was authored on
        // (and the siblings whose registers line up, as the preview draws them) are drawn with copies compiled from it (CamoBaker,
        // BakePlan.VehicleGraphTarget). What stays behind is said: the stickers (their layer is laid out per weapon) and the camo
        // window's whole-vehicle preview, which keeps its preset's own logic.
        var s_VehiclesOnly = s_Request.Vehicles.Count > 0 && s_Request.Weapons.Count == 1 &&
                             s_Request.Weapons[0].Equals("none", StringComparison.OrdinalIgnoreCase);

        // ⭐ ONE DOCUMENT PER WEAPON (on weapons as well): the package's own first-person shader is the logic of the
        // weapons whose OWN documents were edited beyond values; the ticked weapons left with the preset's logic keep the game's camo
        // preset (with the tab's pattern and numbers). One edited logic per bake — told by the HLSL it emits, so the same sticker nodes
        // laid on two weapons are one logic —; two different ones are refused, named, to be baked apart. A camo OF A PIECE keeps the
        // document on screen, as before.
        var s_WeaponDocument = p_Editor.DocumentGraph;
        HashSet<string>? s_OwnShaderWeapons = null;
        bool s_GraphEdited;

        // the preset the package's own weapon shader is a copy of: the first-person camo preset — unless the camo is AS SHIPPED on a tab
        // aimed at another first-person preset (a session opened on the JNG90 aims at weaponpresetshadowfp_xp2): its document is then
        // that preset's as-shipped graph, and the copy has to be of THAT preset (measured 2026-09-25: it was cloned from shadowfp)
        var s_OwnPreset = s_Snapshot.NativeCamo == null && !s_PieceOnly &&
                          s_DocumentTarget.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase) &&
                          !s_DocumentTarget.Contains("3p", StringComparison.OrdinalIgnoreCase)
            ? GamePresetNameOf(s_DocumentTarget)
            : FpPresetName;
        if (s_VehiclesOnly)
            s_GraphEdited = false;
        else if (s_PieceOnly)
            s_GraphEdited = GraphEditedBeyondValues(p_Editor, s_DocumentTarget);
        else
        {
            // ⭐ THE EMBLEM SLOT IS PERMANENT (a permanent slot in Camo Studio, added at bake time): every weapon a camo bakes
            // carries the slot, whether or not the user ever opened sticker mode on it — laid by the BAKE on a copy of the weapon's own
            // document (the same StickerGraph.Ensure the mode makes, at the register the diffuse gives, the document's layer size), the
            // document itself left as it is (the canvas as shipped stays the game's graph, the rule since 09-12). With every weapon
            // carrying the slot the bake takes the road the default look and the eight default camos took, measured in game (10-07/08):
            // the package's own shader on every weapon, the no-camo twin, a copy per other preset worn as shipped. CAMO_EMBLEM_ALWAYS=0 =
            // the way back (the slot only where sticker mode laid it).
            var s_SlotAlways = RimeShaderEditor.Graph.EmblemSlot.Enabled && Environment.GetEnvironmentVariable("CAMO_EMBLEM_ALWAYS") != "0";
            RimeShaderEditor.Graph.ShaderGraph WithEmblemSlot(RimeShaderEditor.Graph.ShaderGraph p_Document)
            {
                if (!s_SlotAlways || p_Document.StickerRegister != null)
                    return p_Document;

                var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(p_Document.ToJson());
                return RimeShaderEditor.Graph.StickerGraph.Ensure(s_Copy, DiffuseRegisterFor(s_DocumentTarget), null, out _, p_Document.StickerLayerSide) != null
                    ? s_Copy
                    : p_Document;
            }

            var s_Groups = new Dictionary<string, (RimeShaderEditor.Graph.ShaderGraph Document, List<string> Weapons, bool Edited)>(StringComparer.Ordinal);
            foreach (var s_Weapon in m_Weapons.Weapons.Where(p_W => p_W.PreviewMesh != null &&
                                                                     (s_Request.Weapons.Count == 0 ||
                                                                      s_Request.Weapons.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))))
            {
                var s_Own = p_Editor.DocumentOfSubject(s_Weapon.PreviewMesh!);
                var s_Edited = GraphEditedBeyondValues(p_Editor, s_DocumentTarget, s_Own);
                if (!s_Edited && !s_SlotAlways)
                    continue;

                var s_Baked = WithEmblemSlot(s_Own);
                var s_Logic = p_Editor.LogicFingerprint(s_Baked) ?? s_Baked.ToJson();
                if (!s_Groups.TryGetValue(s_Logic, out var s_Group))
                    s_Groups[s_Logic] = s_Group = (s_Baked, new List<string>(), false);
                s_Group.Weapons.Add(s_Weapon.Folder);
                if (s_Edited)
                    s_Groups[s_Logic] = (s_Group.Document, s_Group.Weapons, true);
            }

            // (a camo whose graph was edited on SOME weapons: those carry their logic and the slot; the ones left with the preset's logic
            // would need a second shader of the package's own, which one camo cannot ship — they keep the game's preset, as before, said)
            if (s_Groups.Count > 1 && s_Groups.Count(p_G => p_G.Value.Edited) == 1)
            {
                var s_Plain = s_Groups.Where(p_G => !p_G.Value.Edited).SelectMany(p_G => p_G.Value.Weapons).ToList();
                foreach (var s_Key in s_Groups.Where(p_G => !p_G.Value.Edited).Select(p_G => p_G.Key).ToList())
                    s_Groups.Remove(s_Key);
                Log($"NOTE: {s_Plain.Count} weapon(s) of this camo keep the preset's own logic ({string.Join(", ", s_Plain.Take(8))}" +
                    $"{(s_Plain.Count > 8 ? ", …" : "")}) while the others carry an edited graph: one camo ships one shader of its own, so " +
                    "those keep the game's preset — WITHOUT the emblem slot. Bake them as a camo of their own to give them the slot.");
            }

            if (s_Groups.Count > 1)
            {
                Log($"Bake: the ticked weapons carry {s_Groups.Count} DIFFERENT graphs of their own (each weapon keeps its own camo document) — " +
                    string.Join("; ", s_Groups.Select((p_G, i) => $"graph {i + 1} ({p_G.Value.Document.Nodes.Count} node(s), {p_G.Value.Document.Connections.Count} wire(s), " +
                                                                  $"logic {(p_G.Key.Length > 12 ? "json:" + p_G.Key.Length : p_G.Key)}): " +
                                                                  $"{string.Join(", ", p_G.Value.Weapons.Take(6))}" +
                                                                  (p_G.Value.Weapons.Count > 6 ? $" (+{p_G.Value.Weapons.Count - 6})" : ""))) +
                    ". One camo ships one shader of its own: bake each group as a camo of its own (tick one group's weapons), or make their " +
                    "graphs the same.");
                return null;
            }

            s_GraphEdited = s_Groups.Count == 1;
            if (s_GraphEdited)
            {
                var s_Only = s_Groups.Values.First();
                s_WeaponDocument = s_Only.Document;
                s_OwnShaderWeapons = new HashSet<string>(s_Only.Weapons, StringComparer.OrdinalIgnoreCase);
                // (with no pattern and no game camo — as shipped, the basic camo — the others keep their OWN shader: a values-only package)
                var s_NoPattern = s_Pattern == null && (s_Snapshot.NativeCamo == null || s_Snapshot.NativeCamo == BasicCamoKey);
                Log($"NOTE: {s_Only.Weapons.Count} weapon(s) carry their own edited graph and wear the package's shader " +
                    $"({string.Join(", ", s_Only.Weapons.Take(8))}{(s_Only.Weapons.Count > 8 ? ", …" : "")}); every other weapon ticked keeps " +
                    (s_NoPattern ? "its own shader and textures." : "the game's camo preset with the pattern and the numbers."));
            }
        }

        // ⭐ ONE DOCUMENT PER VEHICLE (only that subject's): every vehicle ticked bakes with ITS OWN document — the
        // one on screen, the one changed on it earlier, or the tab's common one if nothing was — for its graph's logic (the pieces'
        // copies are compiled from it, per vehicle: CamoBaker) and the values typed on its nodes. The pattern, the native camo and the
        // simple form's numbers are the tab's, the same in every document.
        var s_VehicleGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var s_VehicleValues = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        // ⭐ …and the preset each one's document is drawn on when it is NOT the tab's: a vehicle whose own starting document is on a shader of its
        // own (the Rhino's van body, ticked on a tab of the mud — DocumentOfSubject hands that document with the tab's pattern and numbers on it)
        var s_VehicleTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Entry in m_Vehicles.Vehicles.Where(p_V => p_V.PreviewMesh.Length > 0 &&
                                                                s_Request.Vehicles.Any(p_K => VehicleCatalog.Asks(p_K, p_V))))
        {
            var s_Own = p_Editor.DocumentOfSubject(s_Entry.PreviewMesh);
            s_VehicleValues[s_Entry.PreviewMesh] = p_Editor.PreviewValuesOf(s_Own);
            var s_OwnTarget = s_Own.TargetShader is { Length: > 0 } s_Target && !s_Target.Equals(s_DocumentTarget, StringComparison.OrdinalIgnoreCase)
                ? s_Target
                : null;
            // (measured against ITS preset's translation: the tab's says nothing of a document drawn on another shader)
            if (!GraphEditedBeyondValues(p_Editor, s_OwnTarget ?? s_DocumentTarget, s_Own))
                continue;

            if (s_Own.StickerRegister != null)
            {
                Log($"Bake: {s_Entry.Display}'s camo carries stickers, and a vehicle's pieces have no sticker layer yet — take the " +
                    "stickers off it (or bake them on weapons).");
                return null;
            }

            var s_Work = Path.Combine(s_WorkRoot, CamoBaker.FolderOf(s_Name));
            Directory.CreateDirectory(s_Work);
            var s_Path = Path.Combine(s_Work, $"camo_graph_{Cache.GameCache.SanitizeName(s_Entry.PreviewMesh)}.json");
            File.WriteAllText(s_Path, ShaderGraphForBake(WithPictureMoves(s_Own, true)).ToJson(), new System.Text.UTF8Encoding(false));
            s_VehicleGraphs[s_Entry.PreviewMesh] = s_Path;
            if (s_OwnTarget != null)
            {
                s_VehicleTargets[s_Entry.PreviewMesh] = s_OwnTarget;
                Log($"NOTE: {s_Entry.Display} bakes with ITS OWN starting document, drawn on '{s_OwnTarget.Split('/')[^1]}' (not this tab's " +
                    $"'{s_DocumentTarget.Split('/')[^1]}'), with this tab's pattern and numbers on it — its pieces' copies are compiled against that shader.");
            }
            Log($"NOTE: {s_Entry.Display}'s graph (its own document) has nodes or wires that differ from the preset's — it is drawn with " +
                "copies of its presets compiled from THAT graph: on the whole body (the vehicle made again wearing it, and the camo window's " +
                "preview) where the copies' level compiles its body's declarations, otherwise on its pieces — the bake's log says which.");
        }

        if (s_VehicleGraphs.Count > 0)
        {
            s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
            if (s_Fxc.Length == 0)
            {
                Log("Bake: a vehicle's graph changes the shader itself, and fxc.exe (the Windows SDK shader compiler) was not found to " +
                    "compile it. Install the Windows SDK or put fxc.exe on PATH.");
                return null;
            }
        }

        // ⭐ and a vehicle's material edited on its own graph (two materials edited at once must both bake, plus the
        // rest): the pieces draw it with a copy compiled from THAT graph, on that material alone (CamoBaker says which)
        if (s_MaterialShaders.Count > 0 && s_Request.Vehicles.Count > 0 &&
            s_MaterialShaders.Any(p_M => m_Vehicles.VehicleOfBody(p_M.Mesh) != null))
            Log("NOTE: the vehicle's materials edited on their own graphs are each drawn with a copy of its preset compiled from its graph: " +
                "on the whole body (the vehicle made again wearing it, and the camo window's preview) where the copies' level compiles its " +
                "body's declarations, otherwise on the vehicle pieces — the bake's log says which.");

        if (s_GraphEdited)
        {
            s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
            if (s_Fxc.Length == 0)
            {
                Log("Bake: this graph changes the shader itself, and fxc.exe (the Windows SDK shader compiler) was not " +
                    "found to compile it. Install the Windows SDK or put fxc.exe on PATH.");
                return null;
            }

            var s_Work = Path.Combine(s_WorkRoot, CamoBaker.FolderOf(s_Name));
            Directory.CreateDirectory(s_Work);
            s_ShaderGraph = Path.Combine(s_Work, "camo_graph.json");

            // A document saved by an older studio gets today's sticker nodes (the side gate, the register
            // layout) before it ships — the same migration entering sticker mode makes.
            // (THE EDITED WEAPONS' document — one document per subject —, the one on screen for a camo of a piece)
            var s_Document = s_WeaponDocument;
            if (s_Document.StickerRegister is { } s_StickerRegister)
            {
                RimeShaderEditor.Graph.StickerGraph.Ensure(s_Document, DiffuseRegisterFor(s_DocumentTarget), s_StickerRegister, out _, s_Document.StickerLayerSide);
                if (s_Document.StickerAnimation is { } s_Gif && p_Editor.StickerAtlasOf(s_Gif) is { } s_Atlas)
                    RimeShaderEditor.Graph.StickerGraph.EnsureAnimated(s_Document, s_Atlas, s_Document.StickerLayerSide, out _);
            }

            File.WriteAllText(s_ShaderGraph, ShaderGraphForBake(WithPictureMoves(s_Document, true), s_OwnPreset).ToJson(), new System.Text.UTF8Encoding(false));
            if (!s_VehiclesOnly)
                Log("NOTE: this graph's nodes or wires differ from the preset's — the package ships its OWN first-person " +
                    "shader with them (a small shader database of its own); the third-person model draws with it too." +
                    (s_OwnPreset != FpPresetName
                        ? $" As shipped, the document is '{s_OwnPreset.Split('/')[^1]}' itself, so the copy is of THAT preset."
                        : ""));

            // The stickers ride with the shader: composed HERE, on the window's thread with its picture
            // cache, so the bake's own thread only converts files. (A camo of vehicles only carries none: refused above.)
            s_Stickers = s_VehiclesOnly ? null : StickerRequest.Of(s_Document, p_Editor.StickerImage, s_Work, Log, p_Editor.StickerAtlasOf);
            if (s_Stickers != null)
                Log($"NOTE: the graph samples a sticker layer at t{s_Stickers.Register}: " +
                    $"{s_Stickers.Placements.Count(p_S => !RimeShaderEditor.Graph.EmblemSlot.IsEmblem(p_S))} sticker(s) " +
                    $"composed at {s_Stickers.Side}² per weapon that has any; the rest get an empty layer.");

            // Stickers over the weapon AS SHIPPED (no pattern, no native camo): the weapons that wear the
            // NoCamo preset get the package's clone of THAT preset, so a second graph rides along — the
            // game's no-camo preset graph sampling the same sticker layer.
            if (s_Stickers != null && s_Pattern == null && s_Snapshot.NativeCamo == null)
            {
                s_NoCamoShaderGraph = NoCamoShaderGraphForBake(p_Editor, s_DocumentTarget, s_Stickers.Register, s_Work, out var s_Why, s_Document);
                if (s_NoCamoShaderGraph == null)
                {
                    Log("Bake: " + s_Why);
                    return null;
                }

                Log("NOTE: stickers over the weapon as shipped — the weapons that wear no camo get a clone of the game's " +
                    "no-camo preset with the sticker layer; the rest keep the camo preset with their shipped textures.");
            }
        }

        // ⭐ AS SHIPPED IS A CAMO LIKE ANY OTHER (even as shipped can be baked): a weapon whose body wears
        // the game's NO-CAMO preset (the M416) and whose as-shipped graph was edited (ShaderGraph.ShippedGraph) wears a copy of THAT preset
        // compiled from its graph — the package's no-camo clone, the lane stickers over the weapon as shipped already take. A weapon whose
        // body wears the camo's own preset (the F2000) keeps its as-shipped graph in its document: it is the package's own shader, above.
        var s_ShippedLanes = new List<ShippedShaderRequest>();

        // the graph as the bake compiles it: aimed at the preset as the game's database names it, nothing of the window's own
        // (a piece's carries no sticker layer: the stickers are laid out per weapon body)
        string WriteShippedCopy(RimeShaderEditor.Graph.ShaderGraph p_Graph, string p_Preset, string p_File, bool p_Stickers = true)
        {
            var s_Work = Path.Combine(s_WorkRoot, CamoBaker.FolderOf(s_Name));
            Directory.CreateDirectory(s_Work);
            var s_Copy = RimeShaderEditor.Graph.ShaderGraph.FromJson(WithPictureMoves(p_Graph).ToJson());
            s_Copy.TargetShader = p_Preset;
            s_Copy.BakeVariation = null;
            s_Copy.BakeMesh = null;
            s_Copy.Variations = null;
            s_Copy.MaterialGraphs = null;
            s_Copy.MaterialsOff = null;
            s_Copy.ShippedGraph = null;
            foreach (var s_Node in s_Copy.Nodes)
                s_Node.Params.Remove("CustomTexture");

            // the sticker layer rides on it as on the no-camo twin of a sticker package (the as-shipped graph samples it already
            // when the document has one — ShowFactoryGraph — and Ensure leaves a graph that does as it is)
            if (p_Stickers && s_Stickers != null)
                RimeShaderEditor.Graph.StickerGraph.Ensure(s_Copy, DiffuseRegisterFor(p_Graph.TargetShader), s_Stickers.Register, out _, s_WeaponDocument.StickerLayerSide);

            var s_Path = Path.Combine(s_Work, p_File);
            File.WriteAllText(s_Path, s_Copy.ToJson(), new System.Text.UTF8Encoding(false));
            return s_Path;
        }

        // ⭐ …AND A PIECE'S AS-SHIPPED GRAPH EDITED (the follow-up work on the attachments — until then refused by name): a
        // copy of the preset the piece's own material wears (the ACOG's weaponpresetfp), compiled from its graph, handed to the materials
        // that wear that preset on each of its models (AccessoriesForBake, CamoBaker). One graph per piece mesh: the ACOG of the M416 and of
        // the M240 is one subject. Pieces nothing was edited on stay as the game ships them.
        var s_ShippedPieces = new Dictionary<string, (string Preset, string GraphPath, IReadOnlyList<GraphPicture> Pictures)>(StringComparer.OrdinalIgnoreCase);
        if (s_Snapshot.NativeCamo == null && !s_VehiclesOnly)
            foreach (var s_Mesh in p_Editor.ShippedEditsOfSession()
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(p_M => m_Weapons.Weapons.All(p_W => !string.Equals(p_W.PreviewMesh, p_M, StringComparison.OrdinalIgnoreCase))))
            {
                if (p_Editor.DocumentOfSubject(s_Mesh).ShippedGraph is not { } s_Graph)
                    continue;

                // (the compiler is looked for here and demanded by the bake, which says so if it is missing)
                if (s_Fxc.Length == 0)
                    s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
                var s_GameName = GamePresetNameOf(s_Graph.TargetShader);
                // ⭐ its PICTURES ride too (a snow picture on the ACOG's graph at t4 looked right in the preview but wrong in game
                // — the copy's graph drops them, and it read t4, which the preset binds nothing at): read before the copy is written
                var s_Pictures = PicturesOf(s_Graph, out var s_Skipped);
                if (s_Skipped.Count > 0)
                    Log($"NOTE: {s_Mesh.Split('/')[^1]}'s graph has picture(s) on node kinds a package cannot carry yet ({string.Join(", ", s_Skipped)}) — " +
                        "those nodes read what the game binds there.");
                s_ShippedPieces[s_Mesh] = (s_GameName, WriteShippedCopy(s_Graph, s_GameName, $"piece_graph_shipped{s_ShippedPieces.Count}.json", false), s_Pictures);
            }

        if (s_Snapshot.NativeCamo == null && !s_VehiclesOnly && !s_PieceOnly)
        {
            var s_NoCamoPreset = NoCamoPresetOf(s_DocumentTarget);
            var s_Shipped = new Dictionary<string, (RimeShaderEditor.Graph.ShaderGraph Graph, List<string> Weapons)>(StringComparer.Ordinal);
            // …and the weapons whose body wears ANOTHER preset (the M249's weaponpresetfp, the XP2 family's shadowfp_xp2): one copy of
            // that preset per (preset, logic) — two logics on one preset are refused, named, like two camo graphs
            var s_OtherLanes = new Dictionary<string, Dictionary<string, (RimeShaderEditor.Graph.ShaderGraph Graph, List<string> Weapons)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var s_Weapon in m_Weapons.Weapons.Where(p_W => p_W.PreviewMesh != null &&
                                                                     (s_Request.Weapons.Count == 0 ||
                                                                      s_Request.Weapons.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))))
            {
                if (p_Editor.DocumentOfSubject(s_Weapon.PreviewMesh!).ShippedGraph is not { } s_Graph)
                    continue;

                var s_Logic = p_Editor.LogicFingerprint(s_Graph) ?? s_Graph.ToJson();
                var s_Lanes = s_Graph.TargetShader.Equals(s_NoCamoPreset, StringComparison.OrdinalIgnoreCase)
                    ? s_Shipped
                    : s_OtherLanes.TryGetValue(s_Graph.TargetShader, out var s_Known)
                        ? s_Known
                        : s_OtherLanes[s_Graph.TargetShader] = new Dictionary<string, (RimeShaderEditor.Graph.ShaderGraph, List<string>)>(StringComparer.Ordinal);
                if (!s_Lanes.TryGetValue(s_Logic, out var s_Group))
                    s_Lanes[s_Logic] = s_Group = (s_Graph, new List<string>());
                s_Group.Weapons.Add(s_Weapon.Folder);
            }

            // ⭐ STICKERS — THE EMBLEM SLOT — OVER THE WEAPON AS SHIPPED, ON A BODY THAT WEARS ANOTHER PRESET (every pistol gets the emblem
            // slot by default, but with its default shader, as shipped): what the no-camo twin does for
            // the weapons that wear the no-camo preset (above), for the ones whose body wears a third one — the pistols' weaponpresetfp —: a
            // copy of THAT preset's translated graph, the sticker layer and the slot laid on it by WriteShippedCopy, so the weapon keeps the
            // game's look with the slot on it. Measured before (--defaultemblemtest, the M9): without this the pistols fell to the camo
            // preset (its tile gate opened) — a camo look, not theirs. A weapon whose as-shipped graph was edited has its lane above already.
            if (s_Stickers != null && s_Pattern == null)
                foreach (var s_Weapon in m_Weapons.Weapons.Where(p_W => p_W.PreviewMesh != null &&
                                                                         (s_Request.Weapons.Count == 0 ||
                                                                          s_Request.Weapons.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))))
                {
                    if (p_Editor.DocumentOfSubject(s_Weapon.PreviewMesh!).ShippedGraph != null)
                        continue;

                    // the preset that draws the most of the body (MainWindow.WornPresetOf's rule, read from the cached dump)
                    var s_Worn = Cache.GameCache.SectionTrianglesOf(s_Weapon.PreviewMesh!)
                        .Where(p_S => p_S.Shader.Contains("weaponpreset", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(p_S => p_S.Shader.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(p_G => p_G.Sum(p_S => p_S.Triangles))
                        .Select(p_G => p_G.Key)
                        .FirstOrDefault();
                    if (s_Worn == null || s_Worn.Equals(s_DocumentTarget.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                        s_Worn.Equals(s_NoCamoPreset, StringComparison.OrdinalIgnoreCase) || s_Worn.EndsWith("3p", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var s_Cached = p_Editor.CamoGraphCachePath(s_Worn);
                    if (!File.Exists(s_Cached))
                    {
                        Log($"NOTE: {s_Weapon.Folder}'s body wears '{s_Worn.Split('/')[^1]}', whose graph is not translated yet — it keeps the camo " +
                            "preset under the stickers (run the studio's pretranslate step).");
                        continue;
                    }

                    var s_Translation = RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_Cached));
                    s_Translation.TargetShader = s_Worn;
                    var s_Lanes = s_OtherLanes.TryGetValue(s_Worn, out var s_Known)
                        ? s_Known
                        : s_OtherLanes[s_Worn] = new Dictionary<string, (RimeShaderEditor.Graph.ShaderGraph, List<string>)>(StringComparer.Ordinal);
                    const string s_AsTranslated = "(the preset's own graph, the stickers laid on it)";
                    if (!s_Lanes.TryGetValue(s_AsTranslated, out var s_Group))
                        s_Lanes[s_AsTranslated] = s_Group = (s_Translation, new List<string>());
                    s_Group.Weapons.Add(s_Weapon.Folder);
                }

            foreach (var (s_Preset, s_ByLogic) in s_OtherLanes.Where(p_L => p_L.Value.Count > 1))
            {
                Log($"Bake: the ticked weapons that wear '{s_Preset.Split('/')[^1]}' carry {s_ByLogic.Count} DIFFERENT as-shipped graphs — " +
                    string.Join("; ", s_ByLogic.Values.Select((p_G, i) => $"graph {i + 1}: {string.Join(", ", p_G.Weapons)}")) +
                    ". One camo ships one copy of a preset: bake each group as a camo of its own, or make their graphs the same.");
                return null;
            }

            if (s_Shipped.Count > 1)
            {
                Log($"Bake: the ticked weapons carry {s_Shipped.Count} DIFFERENT as-shipped graphs of their own — " +
                    string.Join("; ", s_Shipped.Values.Select((p_G, i) => $"graph {i + 1}: {string.Join(", ", p_G.Weapons.Take(6))}" +
                                                                         (p_G.Weapons.Count > 6 ? $" (+{p_G.Weapons.Count - 6})" : ""))) +
                    ". One camo ships one as-shipped copy of the no-camo preset: bake each group as a camo of its own, or make their graphs the same.");
                return null;
            }

            if (s_Shipped.Count > 0 || s_OtherLanes.Count > 0)
            {
                if (s_Fxc.Length == 0)
                    s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";
                if (s_Fxc.Length == 0)
                {
                    Log("Bake: an edited as-shipped graph changes the shader itself, and fxc.exe (the Windows SDK shader compiler) was not " +
                        "found to compile it. Install the Windows SDK or put fxc.exe on PATH.");
                    return null;
                }
            }

            if (s_Shipped.Count == 1)
            {
                var (s_Graph, s_Weapons) = s_Shipped.Values.First();
                s_NoCamoShaderGraph = WriteShippedCopy(s_Graph, NoCamoFpPresetName, "camo_graph_shipped.json");
                s_OwnShaderWeapons ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_OwnShaderWeapons.UnionWith(s_Weapons);
                Log($"NOTE: as shipped, {string.Join(", ", s_Weapons)} carr{(s_Weapons.Count == 1 ? "ies" : "y")} an edited graph of the no-camo preset its body " +
                    "wears — the package ships a copy of that preset compiled from it, with the weapon's own textures and numbers.");
            }

            foreach (var (s_Preset, s_ByLogic) in s_OtherLanes)
            {
                var (s_Graph, s_Weapons) = s_ByLogic.Values.First();
                var s_GameName = GamePresetNameOf(s_Preset);
                s_ShippedLanes.Add(new ShippedShaderRequest(s_GameName,
                    WriteShippedCopy(s_Graph, s_GameName, $"camo_graph_shipped{s_ShippedLanes.Count}.json"), s_Weapons));
                s_OwnShaderWeapons ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                s_OwnShaderWeapons.UnionWith(s_Weapons);
                Log($"NOTE: as shipped, {string.Join(", ", s_Weapons)} carr{(s_Weapons.Count == 1 ? "ies" : "y")} an edited graph of " +
                    $"'{s_Preset.Split('/')[^1]}', the preset its body wears — the package ships a copy of that preset compiled from it, with the " +
                    "weapon's own textures and numbers.");
            }
        }

        // The document's per-material choices (material IDs kept as shipped, per mesh) ride into the plan.
        var s_MaterialsOff = p_Editor.DocumentGraph.MaterialsOff;
        // The attachments the user asked for, as the SAME shorthand the command line takes, so the button and
        // `--baketest --accessories all` walk one path: what cannot be painted is dropped by the plan, named.
        var s_Plan = PlanFor(s_Name, s_Request.Description, s_Pattern, s_Snapshot.NativeCamo, s_Request.Thumbnail,
            s_Snapshot.PreviewValues, s_Request.Weapons, out var s_Error, s_ShaderGraph, s_Stickers, s_NoCamoShaderGraph,
            s_MaterialsOff, s_MaterialShaders,
            s_PieceOnly ? s_Request.Pieces : s_Request.Accessories ? AllAccessories : null,
            p_Editor.DocumentGraph, !s_PieceOnly, s_Request.Vehicles,
            // the vehicle preset the camo is authored on: an edited graph draws the vehicle pieces that wear it
            s_DocumentTarget,
            // and each vehicle's own document: its edited graph (if any) and its typed values
            s_Request.Vehicles.Count > 0 ? s_VehicleGraphs : null, s_Request.Vehicles.Count > 0 ? s_VehicleValues : null,
            // and each weapon's and attachment's: its typed values, its materials kept off, its numbers — and who wears the shader
            new SubjectDocuments
            {
                DocumentOf = p_Editor.DocumentOfSubject,
                ValuesOf = p_Document => p_Editor.PreviewValuesOf(p_Document),
                OwnShaderWeapons = s_OwnShaderWeapons,
                ShippedPieces = s_ShippedPieces,
            },
            // and the as-shipped graphs edited on weapons that wear another preset: a copy of that preset each
            s_ShippedLanes,
            // and the preset the package's own weapon shader is a copy of
            s_OwnPreset,
            // and the pictures on the materials' own graphs, each for its material (edited logic or not)
            p_Editor.MaterialGraphsWithPicturesOfSession()
                .Select(p_M => new MaterialPictureRequest(p_M.Mesh, p_M.MaterialId, p_M.Shader, PicturesOf(p_M.Graph, out _)))
                .ToList(),
            // and the preset each vehicle's graph was authored on where it is not the tab's
            s_Request.Vehicles.Count > 0 ? s_VehicleTargets : null);
        if (s_Plan == null)
        {
            Log("Bake: " + s_Error);
            return null;
        }

        // ⭐ ONE OF THE FRAMEWORK'S DEFAULT CAMOS, WITH THE PACKAGE'S OWN SHADER (the 8 native camos remade to support the
        // emblems): the plan the window made — the game pattern, the shader with the sticker layer and the emblem slot — shipped as that
        // game camo, the way PlanForNative names one: its key (the variations <Weapon>_CAMO_ABU, the hashes the framework has always used),
        // defaultcamos/<key>, the game's own row for the pattern (its identifier), no picture or text of its own
        if (s_Request.NativeIdentity is { } s_NativeKey)
        {
            var s_GameCamo = NativeCamos.FirstOrDefault(p_C => p_C.Key.Equals(s_NativeKey, StringComparison.OrdinalIgnoreCase));
            if (s_GameCamo.Key == null || !FrameworkMod.NativeIdentifierOf.TryGetValue(s_GameCamo.Key, out var s_Row))
            {
                Log($"Bake: '{s_NativeKey}' is not one of the eight game camos with a row of the game's ({string.Join(", ", NativeCamos.Select(p_C => p_C.Key))}).");
                return null;
            }

            // (the pattern on the weapons has to BE that camo's: a default camo on the ABU row wearing another pattern would lie in the menu)
            if (s_Plan.PatternImage != null || !string.Equals(s_Plan.NativeTexture, NativeCamoTexture(s_GameCamo.Key), StringComparison.OrdinalIgnoreCase))
            {
                Log($"Bake: baked as the game's {s_GameCamo.Display}, the camo must wear {s_GameCamo.Display} itself — the picker says " +
                    $"'{s_Snapshot.NativeCamo ?? "(no game camo)"}'{(s_Plan.PatternImage != null ? " with a picture of its own" : "")}.");
                return null;
            }

            s_Plan.Name = s_GameCamo.Display;
            s_Plan.Key = CamoBaker.KeyOf(s_GameCamo.Key);
            s_Plan.Folder = s_GameCamo.Key.ToLowerInvariant();
            s_Plan.Description = $"The game's {s_GameCamo.Display} pattern, offered on every weapon by the camo framework.";
            s_Plan.Family = "";
            s_Plan.Identifier = s_Row;
            s_Plan.ThumbnailImage = null;
            s_Plan.Native = true;
            // ⛔ NO IRON SIGHTS on a default camo (2026-10-07, after the client froze and died loading the level, run 375): the sights ride
            // with every camo's body (AccessoriesForBake), and eight default camos over every weapon made 528 sight rows the client could not
            // build — the default camos are the weapon bodies alone, as they always were
            if (s_Plan.Accessories.Count > 0)
            {
                Log($"NOTE: a default camo paints the weapon bodies alone — its {s_Plan.Accessories.Count} iron sight(s) stay as the game ships them.");
                s_Plan.Accessories.Clear();
            }
            Log($"NOTE: baked as the game's {s_GameCamo.Display} — one of the framework's default camos (defaultcamos/{s_Plan.Folder}, the game's row " +
                $"{s_Row}), with this camo's own shader: {string.Join(", ", s_Plan.Weapons.Select(p_W => p_W.Folder))}.");
        }

        // (the look its weapons wear with no camo picked, when the bake was asked for that: the framework dresses them with it)
        s_Plan.DefaultLook = s_Request.DefaultLook;
        if (s_Plan.DefaultLook)
        {
            Log($"NOTE: '{s_Name}' is the DEFAULT LOOK of {string.Join(", ", s_Plan.Weapons.Select(p_W => p_W.Folder))}: the framework puts it on " +
                "those weapons when their player picked no camo, and lists it in no camo window.");

            // (the look under the menu's no-camo entry is the weapon bodies alone: an attachment row of it would be a camo in the pieces' windows)
            if (s_Plan.Accessories.Count > 0)
            {
                Log($"NOTE: the look with no camo paints the weapon bodies alone — its {s_Plan.Accessories.Count} attachment(s) stay as the game ships them.");
                s_Plan.Accessories.Clear();
            }

            // (one look per weapon: another package of the mod already dressing some of these weapons this way is named — the framework
            // takes the one registered last)
            var s_Folders = new HashSet<string>(s_Plan.Weapons.Select(p_W => p_W.Folder), StringComparer.OrdinalIgnoreCase);
            foreach (var s_Other in FrameworkMod.Scan(s_Mod).Where(p_P => p_P.DefaultLook && !p_P.Folder.Equals(s_Plan.Folder, StringComparison.OrdinalIgnoreCase)))
            {
                var s_Shared = s_Other.Weapons.Select(p_W => p_W.Folder).Where(s_Folders.Contains).ToList();
                if (s_Shared.Count > 0)
                    Log($"NOTE: '{s_Other.Name}' is ALREADY the look with no camo of {s_Shared.Count} of these weapons ({string.Join(", ", s_Shared.Take(8))}" +
                        $"{(s_Shared.Count > 8 ? ", …" : "")}) — on each, the one registered last wins. Bake it under the name '{s_Other.Name}' to replace it, " +
                        "or delete one of the two.");
            }
        }

        // (a weapon the game gives no camo row takes, where the package draws the emblem / stickers, a copy of the first-person preset with
        // its UV-tile gate opened — compiled, so the compiler is looked for here too; the bake says so if it is missing: CamoBaker.NeedsAllTiles)
        if (s_Fxc.Length == 0 && CamoBaker.NeedsAllTiles(s_Plan))
            s_Fxc = RimeShaderEditor.MainWindow.FindFxc() ?? "";

        NoteProjectedSlots(s_Plan, new[] { s_ShaderGraph, s_NoCamoShaderGraph }.Concat(s_ShippedLanes.Select(p_L => p_L.GraphPath)), Log);

        // ⛔ WHAT DOES NOT SHIP IS SAID (weapon, attachment or vehicle alike, a custom texture put on a texture node must just
        // work). Every picture on a 2D texture node ships now, on whatever graph; the ones that cannot — a cube,
        // volume or array node, a file that is gone — are named here, never dropped in silence (a picture on a register nothing can declare
        // is named by the bake itself).
        var s_Dropped = new List<string>();
        void SkippedOf(string p_Whose, RimeShaderEditor.Graph.ShaderGraph? p_Graph)
        {
            if (p_Graph == null)
                return;

            PicturesOf(p_Graph, out var s_Skipped);
            s_Dropped.AddRange(s_Skipped.Select(p_S => $"{p_Whose}: {p_S}"));
        }

        void DocumentSkipped(string p_Whose, RimeShaderEditor.Graph.ShaderGraph p_Document)
        {
            SkippedOf(p_Whose, p_Document);
            SkippedOf($"{p_Whose} (as shipped)", p_Document.ShippedGraph);
            foreach (var (s_Key, s_Graph) in p_Document.MaterialGraphs ?? new Dictionary<string, RimeShaderEditor.Graph.ShaderGraph>())
                SkippedOf($"{p_Whose} material {s_Key.Split('|')[^1]}", s_Graph);
        }

        foreach (var s_Weapon in s_Plan.Weapons)
            if (m_Weapons.Weapons.FirstOrDefault(p_W => p_W.Folder.Equals(s_Weapon.Folder, StringComparison.OrdinalIgnoreCase))?.PreviewMesh is { } s_WeaponMesh)
                DocumentSkipped(s_Weapon.Folder, p_Editor.DocumentOfSubject(s_WeaponMesh));
        foreach (var s_Piece in s_Plan.Accessories.DistinctBy(p_A => p_A.Meshes[0].Mesh, StringComparer.OrdinalIgnoreCase))
            if (m_Accessories.Of(s_Piece.Weapon)?.Unlocks.FirstOrDefault(p_U => p_U.ShortName.Equals(s_Piece.Tag, StringComparison.OrdinalIgnoreCase)) is { } s_Unlock &&
                PreviewMeshOf(s_Unlock) is { } s_PieceMesh)
                DocumentSkipped($"{s_Piece.Weapon}/{s_Piece.Tag}", p_Editor.DocumentOfSubject(s_PieceMesh));
        foreach (var s_Entry in m_Vehicles.Vehicles.Where(p_V => p_V.PreviewMesh.Length > 0 && s_Request.Vehicles.Any(p_K => VehicleCatalog.Asks(p_K, p_V))))
            DocumentSkipped(s_Entry.Display, p_Editor.DocumentOfSubject(s_Entry.PreviewMesh));

        if (s_Dropped.Count > 0)
            Log($"NOTE: {s_Dropped.Distinct().Count()} picture(s) on texture nodes cannot ship — in the game those nodes read what the game binds " +
                $"there: {string.Join("; ", s_Dropped.Distinct().Take(8))}{(s_Dropped.Distinct().Count() > 8 ? "; …" : "")}.");

        if (s_PieceOnly)
            Log($"NOTE: a camo OF A PIECE — {s_Plan.Accessories.Count} of {s_Request.Pieces.Count} asked-for " +
                $"piece(s) ship painted ({s_Plan.Accessories.Sum(p_A => p_A.Meshes.Count)} mesh clone(s)) on " +
                $"{s_Plan.Weapons.Count} weapon(s). No weapon body is painted, no weapon's menu gains a row: it is " +
                "picked in the piece's own window in the game.");

        // ⛔ The zero is a reading, not a silence: "attachments asked for and none could be painted" and
        // "attachments not asked for" look the same in the package, and only one of them is a surprise.
        // (as shipped, what rides and what stays is said below: a piece rides for what was edited on it, never "painted")
        if (s_Request.Accessories && !s_PieceOnly && s_Snapshot.NativeCamo != null)
        {
            var s_Asked = ExpandAccessoryNames(AllAccessories, s_Plan.Weapons.Select(p_W => p_W.Folder)).Count;
            var s_Meshes = s_Plan.Accessories.Sum(p_A => p_A.Meshes.Count);
            Log(s_Plan.Accessories.Count > 0
                ? $"NOTE: {s_Plan.Accessories.Count} of {s_Asked} attachment(s) ship painted ({s_Meshes} mesh clone(s)); " +
                  $"the other {s_Asked - s_Plan.Accessories.Count} have nothing a camo can paint. The mod builds one " +
                  "entry per weapon and attachment on every level."
                : $"NOTE: attachments were asked for and NONE of the {s_Asked} can be painted — the package ships the " +
                  "weapon bodies alone. (A part is painted only when its mesh wears a weapon preset: beams, reticles " +
                  "and the like never do.)");
        }

        if (s_MaterialsOff is { Count: > 0 })
            Log($"NOTE: materials kept as shipped by ID: " + string.Join("; ", s_MaterialsOff.Select(p_M =>
                $"{p_M.Key.Split('/')[^1]} #{string.Join(" #", p_M.Value.OrderBy(p_I => p_I))}")) + ".");

        // (review 2026-09-29) an aircraft's cockpit is dressed with the camo in the preview and painted by no bake — said (CockpitNoteFor)
        foreach (var s_Aircraft in m_Vehicles.Vehicles.Where(p_V => p_V.CockpitMesh.Length > 0 &&
                                                                     s_Plan.Vehicles.Any(p_B => p_V.Meshes.Contains(p_B.Mesh, StringComparer.OrdinalIgnoreCase))))
            Log($"NOTE: {s_Aircraft.Display}'s cockpit (the model around the pilot) is not painted by this package — in the game it stays as " +
                "the game ships it; the body and its pieces take the camo.");

        // (review 2026-09-29, C3) a value typed on a material's OWN graph: the preview draws the material with it, and the package writes no
        // material's own numbers (its instance names its clone or its pictures) — said, it went without a word
        if (p_Editor.MaterialGraphValuesOfSession() is { Count: > 0 } s_MaterialValues)
            Log("NOTE: typed but NOT baked — " + string.Join("; ", s_MaterialValues.Select(p_M =>
                    $"{p_M.Mesh.Split('/')[^1]} #{p_M.MaterialId}: {string.Join(", ", p_M.Constants)}")) +
                ": the numbers typed on a material's own graph are drawn by the preview, but the package does not carry them yet — in the game " +
                "that material keeps its own. (Its graph's logic and pictures do ship.)");

        // (review 2026-09-29, C7) a value typed on a constant the studio's measured table does not know: the preview shows it, the package
        // does not carry it — said, it used to go without a word
        if (s_Plan.ValuesNotBaked.Count > 0)
            Log($"NOTE: typed but NOT baked — {string.Join(", ", s_Plan.ValuesNotBaked)}: the studio does not know what that constant is on " +
                "the game's materials (its type and range are not measured), so the package leaves it as the game ships it; the preview shows it.");

        if (s_Plan.ValuesOnly)
        {
            // said for what is being baked: on the Vehicles tab there is no weapon in the package (it must know which category is
            // on screen — the line said "each weapon" over a tank)
            var s_Subject = s_Plan.Vehicles.Count > 0 && s_Plan.Weapons.Count == 0 ? "vehicle" : "weapon";
            var s_Changed = s_Plan.Weapons.Any(p_W => p_W.Overridden.Count > 0) || s_Plan.Vehicles.Any(p_V => p_V.Values.Count > 0);
            // …except the weapons whose own logic was edited (as shipped, or on the basic camo): they wear the package's copy of their shader
            var s_OwnLogic = (s_Plan.ShaderGraphPath != null || s_Plan.NoCamoShaderGraphPath != null) && s_Plan.Weapons.Any(p_W => p_W.OwnShader);
            Log($"NOTE: no pattern image and no native camo — the package carries values only: each {s_Subject} keeps its " +
                $"stock textures (its own camo texture too)" +
                (s_OwnLogic ? ", the weapons whose graph was edited wear the package's copy of their shader," : " and shader,") +
                " and gets the constants changed in the graph" +
                (s_Changed || s_OwnLogic ? "." : " (none were changed: nothing visible will differ)."));
        }

        var s_Repl = Settings.FindRimeRepl();
        var s_Texconv = RimeShaderEditor.MainWindow.FindTexconv();
        var s_Game = s_Settings.GamePath.Length > 0 ? s_Settings.GamePath : Settings.GamePath;

        if (s_Repl == null || s_Texconv == null || !Settings.LooksLikeGame(s_Game))
        {
            Log($"Bake: cannot build — RimeREPL {(s_Repl == null ? "NOT found" : "found")}, texconv " +
                $"{(s_Texconv == null ? "NOT found (expected beside RimeREPL.exe)" : "found")}, game folder " +
                $"{(Settings.LooksLikeGame(s_Game) ? "OK" : "NOT set (Settings)")}.");
            return null;
        }

        var s_Context = new BakeContext
        {
            ModFolder = s_Mod,
            WorkDir = Path.Combine(s_WorkRoot, s_Plan.Folder),
            RimeRepl = s_Repl,
            Texconv = s_Texconv,
            Fxc = s_Fxc,
            GamePath = s_Game,
            Log = p_Line => p_Editor.Dispatcher.BeginInvoke(new Action(() => p_Editor.LogCamo(p_Line))),
        };

        Log($"Baking '{s_Name}' ({s_Plan.Key}) for {s_Plan.Weapons.Count} weapon(s)" +
            (s_Plan.Vehicles.Count > 0
                ? $" and {s_Plan.Vehicles.Count} vehicle bod(ies) ({string.Join(", ", s_Plan.Vehicles.Select(p_V => p_V.Mesh.Split('/')[^1]))})"
                : "") +
            $" into {s_Mod} — " +
            // It used to name the game row the camo borrowed; since the camo carries its OWN identifier that
            // lookup answers nothing, and the line printed a bare "?" where it used to print a fact.
            $"identifier {s_Plan.Identifier}, its own, derived from the name; the game is mounted, this takes a minute.");

        if (s_Snapshot.NativeCamo == null && s_Plan.Accessories.Count > 0)
            // (one line per distinct look: the ACOG of the M416 and of the M240 is one mesh, one graph)
            Log("NOTE: as shipped, the attachments ride with what was edited on them and nothing else — " + string.Join("; ", s_Plan.Accessories
                .GroupBy(p_A => (p_A.ShippedPreset != null ? $"a copy of '{p_A.ShippedPreset.Split('/')[^1]}' compiled from their edited as-shipped graph, on " +
                                                             $"their materials that wear it in {string.Join(", ", p_A.Meshes.Select(p_M => p_M.Mesh.Split('/')[^1]))}"
                                                           : "their edited materials only") +
                                 ", with their own textures and numbers")
                .Select(p_G => $"{string.Join(", ", p_G.Select(p_A => $"{p_A.Weapon}/{p_A.Tag}"))}: {p_G.Key}")) + ".");

        if (m_ShippedPiecesLeft > 0)
            Log($"NOTE: as shipped, the attachments nothing was edited on keep the look the game ships them with — {m_ShippedPiecesLeft} left out " +
                "of the package (the iron sights included).");
        m_ShippedPiecesLeft = 0;

        if (m_LevelOnlySkipped.Count > 0)
            Log($"NOTE: {m_LevelOnlySkipped.Count} attachment model(s) stay as the game ships them — their data lives only inside the levels " +
                $"(no catalog copy), so a package cannot bring it: {string.Join(", ", m_LevelOnlySkipped.Select(p_M => p_M.Split('/')[^1]))}. " +
                "The rest of each attachment takes the camo.");

        return Task.Run(() =>
        {
            try
            {
                var s_Result = CamoBaker.Build(s_Plan, s_Context);

                // ⭐ AN ATTACHMENT MODEL WHOSE DATA LIVES ONLY INSIDE THE LEVELS (the Rifle Scope's third-person model: its
                // LOD 4 has no catalog copy): the package cannot bring it, so that model keeps the game's look
                // and the rest bakes — once more, without it, and remembered so the next bake leaves it out from the start
                if (!s_Result.Ok && s_Result.LevelOnlyMeshes.Count > 0 && s_Result.Problems.Count == s_Result.LevelOnlyMeshes.Count)
                {
                    RememberLevelOnly(s_Result.LevelOnlyMeshes);
                    foreach (var s_Accessory in s_Plan.Accessories)
                        s_Accessory.Meshes.RemoveAll(p_M => s_Result.LevelOnlyMeshes.Contains(p_M.Mesh, StringComparer.OrdinalIgnoreCase));
                    s_Plan.Accessories.RemoveAll(p_A => p_A.Meshes.Count == 0);
                    s_Context.Log($"NOTE: {string.Join(", ", s_Result.LevelOnlyMeshes.Select(p_M => p_M.Split('/')[^1]))} — its data lives only inside " +
                                  "the levels (no catalog copy) and a package cannot bring it: that model stays as the game ships it. Baking again " +
                                  "without it (remembered: the next bake leaves it out from the start).");
                    s_Result = CamoBaker.Build(s_Plan, s_Context);
                }
                s_Context.Log(s_Result.Ok
                    ? $"BAKE OK — {s_Result.Summary}"
                    : $"Bake failed — see {s_Result.LogPath}");
                return s_Result;
            }
            catch (Exception s_Exception)
            {
                // The frames are the report: a message alone ("Value cannot be null") names nothing.
                var s_Where = string.Join(" <- ", new System.Diagnostics.StackTrace(s_Exception, false).GetFrames()
                    .Select(p_F => p_F.GetMethod())
                    .Where(p_M => p_M != null)
                    .Take(6)
                    .Select(p_M => $"{p_M!.DeclaringType?.Name}.{p_M.Name}"));
                s_Context.Log($"BAKE ERROR: {s_Exception.GetType().Name}: {s_Exception.Message} — at {s_Where}");
                return (BakeResult?) null;
            }
        });
    }

    /// <summary>
    /// Whether the document's LOGIC differs from the preset's cached translation — the shader it emits (MainWindow.LogicFingerprint), which a
    /// typed value or a picture leaves alone and a literal changed or a wire moved does not (measured 2026-09-29, --logicprobe over five
    /// presets). ⛔ It compared node and wire COUNTS (review 2026-09-29): a wire moved to another input, or a Scalar's value changed, kept both
    /// counts — the stock preset shipped, no NOTE, while the preview drew the edit. The counts stay as the answer only when no fingerprint can
    /// be made (no cached contract). CAMO_EDITED_BY_COUNT_OLD=1 = the counts, as before.
    /// </summary>
    internal static bool GraphEditedBeyondValues(RimeShaderEditor.MainWindow p_Editor, string p_Target,
        RimeShaderEditor.Graph.ShaderGraph? p_Document = null)
    {
        try
        {
            var s_Cached = Path.Combine(Settings.CacheFolder, "camographs",
                Cache.GameCache.SanitizeName(p_Target) + ".json");
            if (!File.Exists(s_Cached))
                return false;

            var s_Preset = RimeShaderEditor.Graph.ShaderGraph.FromJson(File.ReadAllText(s_Cached));
            // The camo DOCUMENT, never the weapon's own graph the canvas may be showing in its place — the one on screen, or the
            // document of another subject of the tab (one document per subject).
            var s_Document = p_Document ?? p_Editor.DocumentGraph;
            var s_Counts = s_Preset.Nodes.Count != s_Document.Nodes.Count || s_Preset.Connections.Count != s_Document.Connections.Count;
            if (Environment.GetEnvironmentVariable("CAMO_EDITED_BY_COUNT_OLD") == "1")
                return s_Counts;

            return p_Editor.LogicFingerprint(s_Document) is { } s_Logic && p_Editor.LogicFingerprint(s_Preset) is { } s_PresetLogic
                ? s_Logic != s_PresetLogic
                : s_Counts;
        }
        catch
        {
            return false;
        }
    }

    private List<string> TargetsOf(Camo p_Camo) => p_Camo.EveryWeapon
        ? m_Weapons.Weapons.Select(p_W => p_W.Folder).ToList()
        : p_Camo.Weapons;
}

/// <summary>
/// What a bake reads PER SUBJECT (one camo document per weapon, vehicle and attachment — the pattern, the native
/// camo and the simple form's numbers shared by all of a tab's): each subject's own document by its mesh (the window's
/// DocumentOfSubject), the values typed on a document, and which weapons wear the package's own first-person shader (the ones whose
/// document carries the edited logic; null = every weapon, the older callers').
/// </summary>
public sealed class SubjectDocuments
{
    public required Func<string, RimeShaderEditor.Graph.ShaderGraph> DocumentOf { get; init; }
    public required Func<RimeShaderEditor.Graph.ShaderGraph, IReadOnlyDictionary<string, string>> ValuesOf { get; init; }
    public IReadOnlySet<string>? OwnShaderWeapons { get; init; }

    /// <summary>
    /// The pieces whose AS-SHIPPED graph was edited, by the mesh their document belongs to (the piece's preview mesh): the preset their own
    /// material wears, as the game's database spells it, and the graph saved for the bake — see BakeAccessory.ShippedPreset. Empty or null
    /// = none; only read by a package that is as shipped.
    /// </summary>
    public IReadOnlyDictionary<string, (string Preset, string GraphPath, IReadOnlyList<GraphPicture> Pictures)>? ShippedPieces { get; init; }
}
