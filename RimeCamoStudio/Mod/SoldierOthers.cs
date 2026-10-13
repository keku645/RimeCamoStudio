using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimeCamoStudio.Cache;
using RimeCamoStudio.Catalog;

namespace RimeCamoStudio.Mod;

/// <summary>
/// ANOTHER soldier a skin goes to (per part, the soldiers ticked in the bake dialog — this model only, US, RU, all…): his
/// description in the package (with his own key and identifier), the entries baked for him and the bundle they go in.
/// </summary>
public sealed class OtherSoldierSkin
{
    public PackageSoldier Soldier { get; init; } = new();

    /// <summary>The skin's key for him (CamoBaker.SkinKeyFor): what his variations, pieces and partitions are named after.</summary>
    public string Key { get; init; } = "";

    /// <summary>His variation database (base or Aftermath): what his entries are copied from.</summary>
    public string Database { get; init; } = "";

    /// <summary>His guids: seeded with HIS key, so none meets the skin's own or another soldier's.</summary>
    public Func<int, int, string> GuidFor { get; init; } = (_, _) => "";

    public List<SkinEntryJob> Jobs { get; init; } = new();
    public List<Dictionary<string, object>> Variations { get; init; } = new();

    /// <summary>His bundle of entries in the package ("skinv1"…).</summary>
    public string Bundle { get; init; } = "";
}

public static partial class CamoBaker
{
    private static readonly Dictionary<string, double?> s_MaskCoverage = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How much of a game MASK lets the camo through: the share of its pixels whose ALPHA is over half, from its cached picture; null when
    /// it is not cached. ⭐ MEASURED 2026-09-28 (why the US support's lower body took no texture at all): the character
    /// shaders draw `lerp(Mask.rgb, CamoTile.rgb, Mask.a)` — the mask's ALPHA is where the camo goes, its RGB the colour under it
    /// (characterroot_xp4's disassembly; the base shader agrees: the Ninja torso's mask, whose camo does not show, has an alpha of 0, the
    /// Desert one 7 %). A look whose mask has no alpha shows the skin's pattern nowhere "where the game puts it".
    /// </summary>
    public static double? MaskCoverageOf(string p_Texture)
    {
        lock (s_MaskCoverage)
            if (s_MaskCoverage.TryGetValue(p_Texture, out var s_Known))
                return s_Known;

        double? s_Share = null;
        try
        {
            if (CachedPngOf(p_Texture) is { } s_Png && System.IO.File.Exists(s_Png))
            {
                var s_Image = new System.Windows.Media.Imaging.BitmapImage();
                s_Image.BeginInit();
                s_Image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                s_Image.UriSource = new Uri(s_Png);
                s_Image.EndInit();
                var s_Converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(s_Image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var s_Pixels = new byte[s_Converted.PixelWidth * s_Converted.PixelHeight * 4];
                s_Converted.CopyPixels(s_Pixels, s_Converted.PixelWidth * 4, 0);
                var s_On = 0;
                for (var i = 3; i < s_Pixels.Length; i += 4)
                    if (s_Pixels[i] > 128)
                        s_On++;
                s_Share = s_Pixels.Length == 0 ? null : s_On / (s_Pixels.Length / 4.0);
            }
        }
        catch (Exception)
        {
            // a picture that will not decode tells nothing: no warning is better than a wrong one
        }

        lock (s_MaskCoverage)
            s_MaskCoverage[p_Texture] = s_Share;
        return s_Share;
    }

    /// <summary>
    /// Whether the game's masks of some cloth materials put the camo NOWHERE (every one known, each under 1 % of its alpha): the textures
    /// bound as Mask on them. False when any is unknown or lets some through.
    /// </summary>
    public static bool MasksHideCamo(IEnumerable<string> p_Masks)
    {
        var s_Masks = p_Masks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return s_Masks.Count > 0 && s_Masks.All(p_M => MaskCoverageOf(p_M) is { } s_Share && s_Share < 0.01);
    }

    /// <summary>The numbers that are a TILING: proportional to each mesh's own when the skin goes to another soldier.</summary>
    private static readonly HashSet<string> s_TilingNumbers = new(StringComparer.OrdinalIgnoreCase) { "CamoTileFactor" };

    /// <summary>
    /// Why a part of the skin cannot go to another soldier, or null when it can: only a SIMPLE part travels — its pattern (CamoTile), its mask,
    /// its numbers. A picture on any other parameter (a diffuse…) is painted on THIS model's layout and would land anywhere on another; a graph
    /// of its own is a copy of his meshes' shader. ⭐ A picture on Mask travels (the US support's legs start from one, the
    /// mask that keeps his boots, knee pads and belt out): it is this model's layout, so each other soldier keeps HIS own mask
    /// (PlanOtherSoldiers).
    /// </summary>
    public static string? WhyNotForOthers(PackageSoldierPart p_Part) =>
        p_Part.Shaders.Count > 0 ? "its graph has nodes or wires of its own"
        : p_Part.Textures.Keys.FirstOrDefault(p_K => !p_K.Equals("CamoTile", StringComparison.OrdinalIgnoreCase) && !p_K.Equals("Mask", StringComparison.OrdinalIgnoreCase))
            is { } s_Other ? $"a picture on {s_Other} is painted on this model's layout"
        : null;

    /// <summary>
    /// The skin for the OTHER soldiers its parts go to: per soldier, per part that goes to him, the look of the same name the part starts from
    /// (else his own), his meshes of that part that wear camo cloth there, the part's pattern, HIS mask and its tiling PROPORTIONAL to each
    /// mesh's own (the game tunes it per mesh — measured 2026-09-28, the Desert torsos: 11 / 3.6 / 5.5; the user's number is taken as a ratio
    /// of the tiling his own mesh had); then his entries, planned as the skin's own are.
    /// ⭐ THE MASK AND THE TILING PER SOLDIER (the US support's legs baked "on all the cloth" for everyone put the pattern on the others'
    /// BOOTS, which their own masks keep out — so every graph is checked, and each other soldier's original tiling kept):
    ///   · all the cloth here only because THIS soldier's own mask puts the camo nowhere on one of the part's meshes = a FIX for him: each other
    ///     keeps his own mask (all the cloth only if his hides it everywhere too) and his own tiling;
    ///   · all the cloth here on a mask that shows the camo = a look: all the cloth on everyone, the tiling proportional;
    ///   · the game's mask here: his own, unless it puts the camo nowhere on every cloth mesh of his part — then all the cloth, or the skin would
    ///     not show on him at all.
    /// A tiling this soldier's mesh carries no number of to scale by is left as each other's own.
    /// </summary>
    private static List<OtherSoldierSkin> PlanOtherSoldiers(SoldierBakePlan p_Plan, IReadOnlyList<PackageSoldierPart> p_Parts, SoldierEntry p_Primary,
        IReadOnlyList<SkinEntryJob> p_PrimaryJobs, SoldierCatalog p_Catalog, IReadOnlyList<MaterialBinding> p_Scan,
        IReadOnlyDictionary<(string Mesh, uint Variation, int Id), List<(string Param, string Value)>> p_Numbers,
        IReadOnlyDictionary<(string Mesh, uint Variation, int Id), List<(string Param, string Value)>> p_MaterialNumbers,
        Dictionary<string, List<string>>? p_Bundles, string? p_WhiteMask, List<string> p_Problems, Action<string> p_Log)
    {
        var s_Others = new List<OtherSoldierSkin>();
        var s_Keys = p_Plan.Parts.SelectMany(p_P => p_P.Others)
            .Where(p_K => !p_K.Equals(p_Plan.Soldier.Key, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (s_Keys.Count == 0)
            return s_Others;

        // the materials of an entry that wear the camo cloth (bind CamoTile), in id order
        List<int> ClothIds(string p_Mesh, uint p_Variation) => p_Scan
            .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == p_Variation && p_B.MaterialId >= 0 &&
                          p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
            .Select(p_B => p_B.MaterialId).Distinct().OrderBy(p_I => p_I).ToList();

        // the game's masks on an entry's cloth: the textures bound as Mask beside a CamoTile
        List<string> MasksOf(string p_Mesh, uint p_Variation) => p_Scan
            .Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == p_Variation &&
                          p_B.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(p_B => p_B.Textures.Where(p_T => p_T.Param.Equals("Mask", StringComparison.OrdinalIgnoreCase)).Select(p_T => p_T.Texture))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // ⭐ the parts on all the cloth as a FIX: this soldier's own mask puts the camo nowhere on one of their third-person meshes — per mesh,
        // as the studio warns (a part's other meshes may show it)
        var s_Fixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s_Part in p_Parts.Where(p_P => p_P.Mask == "white" && p_Plan.Parts.Any(p_F => p_F.Part == p_P.Part && p_F.Others.Count > 0)))
        {
            var s_Masks = p_PrimaryJobs.Where(p_J => p_J.Side == null && p_J.Part == s_Part.Part)
                .Select(p_J => (p_J.Mesh, Masks: MasksOf(p_J.Mesh, p_J.Source))).ToList();
            var s_Hiding = s_Masks.Where(p_M => MasksHideCamo(p_M.Masks)).Select(p_M => p_M.Mesh.Split('/')[^1]).Distinct().ToList();
            if (s_Hiding.Count > 0)
            {
                s_Fixes.Add(s_Part.Part);
                p_Log($"  {s_Part.Part}: on all the cloth as a FIX — {p_Plan.Soldier.Display}'s own mask puts the camo nowhere on {string.Join(", ", s_Hiding)}; " +
                      "the other soldiers keep their own masks and their own tiling.");
            }
            else if (s_Masks.SelectMany(p_M => p_M.Masks).Any(p_T => MaskCoverageOf(p_T) == null))
                p_Log($"  ⚠ {s_Part.Part}: {p_Plan.Soldier.Display}'s own masks are not in the cache — whether all the cloth is a fix for him is unknown; " +
                      "it goes to the others as chosen (preview his part once and bake again to know).");
        }

        // a number of an entry's first cloth material, as its stock material variation carries it ("x,y,z,w") — else as the mesh's own
        // material does (what an entry that sets none draws with: the US support's default legs, CamoTileFactor 9) — or null
        string? NumberOf(string p_Mesh, uint p_Variation, string p_Param)
        {
            foreach (var s_Numbers in new[] { p_Numbers, p_MaterialNumbers })
                foreach (var s_Id in ClothIds(p_Mesh, p_Variation))
                    if (s_Numbers.GetValueOrDefault((p_Mesh.ToLowerInvariant(), p_Variation, s_Id)) is { } s_List &&
                        s_List.FirstOrDefault(p_V => p_V.Param.Equals(p_Param, StringComparison.OrdinalIgnoreCase)) is { Value: { } s_Value })
                        return s_Value;
            return null;
        }

        static float[] Vec(string p_Value) => p_Value.Split(',')
            .Select(p_C => float.TryParse(p_C.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V) ? s_V : 0f)
            .Concat(new[] { 0f, 0f, 0f, 0f }).Take(4).ToArray();

        // the look (of the soldier's) a part starts from and the first third-person item of that part in it
        SoldierLook? LookOf(SoldierEntry p_Soldier, string? p_Appearance) =>
            p_Soldier.Looks.FirstOrDefault(p_L => p_L.Appearance.Equals(p_Appearance ?? p_Soldier.Appearance, StringComparison.OrdinalIgnoreCase));

        var s_Index = 0;
        foreach (var s_Key in s_Keys)
        {
            var s_Soldier = p_Catalog.Soldiers.FirstOrDefault(p_S => p_S.Key.Equals(s_Key, StringComparison.OrdinalIgnoreCase));
            if (s_Soldier == null || s_Soldier.Database.Length == 0)
            {
                p_Problems.Add($"'{s_Key}' (ticked for this skin) is not a soldier of the catalogue.");
                continue;
            }

            var s_SkinKey = SkinKeyFor(p_Plan.Key, s_Soldier.Key);
            var s_Seed = Fnv1A("skin:" + s_SkinKey);
            string GuidFor(int p_Group, int p_Slot) => $"{s_Seed:x8}-{p_Group:x4}-4000-a000-{p_Slot:x12}";
            var s_Parts = new List<PackageSoldierPart>();
            var s_Said = new List<string>();

            foreach (var s_Part in p_Parts)
            {
                var s_From = p_Plan.Parts.FirstOrDefault(p_P => p_P.Part == s_Part.Part);
                if (s_From == null || !s_From.Others.Contains(s_Soldier.Key, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (WhyNotForOthers(s_Part) is { } s_Why)
                {
                    s_Said.Add($"{s_Part.Part}: not his — {s_Why}");
                    continue;
                }

                // ⭐ the look of the same NAME as the one the part starts from here, else his own
                SoldierLook? s_Look = null;
                var s_LookNote = "";
                if (s_Part.Appearance != null)
                {
                    var s_Name = p_Primary.Looks.FirstOrDefault(p_L => p_L.Appearance.Equals(s_Part.Appearance, StringComparison.OrdinalIgnoreCase))?.Name;
                    s_Look = s_Name != null ? s_Soldier.LookNamed(s_Name) : null;
                    if (s_Look != null && s_Soldier.LookMisfit(s_Look, s_Part.Part) != null)
                        s_Look = null;
                    s_LookNote = s_Look != null ? $" from his look {s_Look.Display}" : $" from his own look (he has no '{s_Name?.Replace('_', ' ')}' that fits)";
                }

                var s_Items = (LookOf(s_Soldier, s_Look?.Appearance)?.Items ?? s_Soldier.Items)
                    .Where(p_I => p_I.Part.Equals(s_Part.Part, StringComparison.OrdinalIgnoreCase) && p_I.Role != "arms1p").ToList();
                var s_Cloth = s_Items.Where(p_I => ClothIds(p_I.Mesh, p_I.Variation.Length > 0 ? p_I.NameHash : 0).Count > 0)
                    .Select(p_I => p_I.Mesh.ToLowerInvariant()).Distinct().ToList();
                if (s_Cloth.Count == 0)
                {
                    s_Said.Add($"{s_Part.Part}: no camo cloth on his ({string.Join(", ", s_Items.Select(p_I => p_I.Mesh.Split('/')[^1]).Distinct())}) — as the game has it");
                    continue;
                }

                // ⭐ HIS mask: the part's all the cloth when it is a look; else his game's own, unless it puts the camo nowhere
                // on every cloth mesh of his part (then all the cloth, or the skin would not show on him)
                var s_Fix = s_Fixes.Contains(s_Part.Part);
                var s_HisMasks = s_Items.Where(p_I => s_Cloth.Contains(p_I.Mesh.ToLowerInvariant()))
                    .Select(p_I => (p_I.Mesh, Masks: MasksOf(p_I.Mesh, p_I.Variation.Length > 0 ? p_I.NameHash : 0u))).ToList();
                var s_HisHiding = s_HisMasks.Where(p_M => MasksHideCamo(p_M.Masks)).Select(p_M => p_M.Mesh.Split('/')[^1]).Distinct().ToList();
                var s_HeHides = s_HisMasks.Count > 0 && s_HisMasks.All(p_M => MasksHideCamo(p_M.Masks));
                var s_Mask = (s_Part.Mask == "white" && !s_Fix) || s_HeHides ? "white" : "stock";
                if (s_Mask == "white" && p_WhiteMask == null)
                {
                    p_Problems.Add($"also {s_Soldier.Display}: his {s_Part.Part} needs the camo on all the cloth and the package ships no white mask.");
                    continue;
                }

                var s_Textures = new Dictionary<string, string>(s_Part.Textures, StringComparer.Ordinal);
                if (s_Mask == "white")
                    s_Textures["Mask"] = p_WhiteMask!;
                else
                    s_Textures.Remove("Mask");

                if (s_Fix)
                    s_Said.Add(s_Mask == "white"
                        ? $"{s_Part.Part}: all the cloth for him too (his game's mask puts the camo nowhere on it either)"
                        : $"{s_Part.Part}: his own mask (all the cloth is a fix for {p_Plan.Soldier.Display}'s)");
                // (a picture on Mask is painted on this soldier's layout — the US support's legs' starting document: each keeps his own)
                else if (s_Part.Mask == "picture")
                    s_Said.Add(s_Mask == "white"
                        ? $"{s_Part.Part}: all the cloth for him (the picture on the mask is {p_Plan.Soldier.Display}'s layout, and his own mask puts the camo nowhere)"
                        : $"{s_Part.Part}: his own mask (the picture on the mask is {p_Plan.Soldier.Display}'s layout)");
                else if (s_Part.Mask == "white")
                    s_Said.Add($"{s_Part.Part}: all the cloth, as chosen");
                else if (s_Mask == "white")
                    s_Said.Add($"{s_Part.Part}: all the cloth for him (his game's mask puts the camo nowhere on it)");
                // (his own mask kept while it hides the camo on some of his part's meshes: said, per mesh)
                if (s_Mask == "stock" && s_HisHiding.Count > 0)
                    s_Said.Add($"⚠ {s_Part.Part}: his game's mask puts the camo NOWHERE on {string.Join(", ", s_HisHiding)} (its alpha is empty)");

                // ⭐ the tiling PROPORTIONAL: the user's number over what this soldier's mesh had, times what his mesh has — or HIS OWN
                // (each other soldier's original tiling) when the part's all the cloth is a fix for this soldier, or when this
                // soldier's mesh carries no number to scale by (the US support's legs: a mesh linked bare, entry 0)
                var s_Values = new Dictionary<string, string>(StringComparer.Ordinal);
                var s_Primary = p_PrimaryJobs.FirstOrDefault(p_J => p_J.Side == null && p_J.Part == s_Part.Part);
                var s_Theirs = s_Items.First(p_I => s_Cloth.Contains(p_I.Mesh.ToLowerInvariant()));
                foreach (var (s_Param, s_Typed) in s_Part.Values)
                {
                    if (!s_TilingNumbers.Contains(s_Param))
                    {
                        s_Values[s_Param] = s_Typed;
                        continue;
                    }

                    var s_Mine = s_Primary != null ? NumberOf(s_Primary.Mesh, s_Primary.Source, s_Param) : null;
                    var s_His = NumberOf(s_Theirs.Mesh, s_Theirs.Variation.Length > 0 ? s_Theirs.NameHash : 0, s_Param);
                    if (s_Fix || (s_Mine == null && s_His != null))
                    {
                        s_Said.Add($"{s_Part.Part} {s_Param}: his own ({(s_His != null ? Vec(s_His)[0].ToString("0.###", CultureInfo.InvariantCulture) : "the game's")}" +
                                   $"{(s_Fix ? "" : $", {p_Plan.Soldier.Display}'s mesh carries no number to scale {s_Typed.Split(',')[0]} by")})");
                        continue;
                    }

                    if (s_Mine == null || s_His == null)
                    {
                        // (his mesh carries no number: nothing to scale by, the typed one goes as it is)
                        s_Values[s_Param] = s_Typed;
                        s_Said.Add($"{s_Part.Part} {s_Param} {s_Typed.Split(',')[0]} as typed (his mesh carries no number of its own to scale by)");
                        continue;
                    }

                    var (s_T, s_M, s_H) = (Vec(s_Typed), Vec(s_Mine), Vec(s_His));
                    s_Values[s_Param] = string.Join(",", Enumerable.Range(0, 4).Select(i =>
                        (s_M[i] != 0 ? s_T[i] * s_H[i] / s_M[i] : s_T[i]).ToString("0.###", CultureInfo.InvariantCulture)));
                    s_Said.Add($"{s_Part.Part} {s_Param} {s_Typed.Split(',')[0]} x {s_H[0].ToString("0.###", CultureInfo.InvariantCulture)}/" +
                               $"{s_M[0].ToString("0.###", CultureInfo.InvariantCulture)} = {s_Values[s_Param].Split(',')[0]}");
                }

                s_Parts.Add(new PackageSoldierPart
                {
                    Part = s_Part.Part,
                    Meshes = s_Cloth,
                    Appearance = s_Look?.Appearance,
                    Mask = s_Mask,
                    Textures = s_Textures,
                    Values = s_Values,
                    Thumbnail = s_Part.Thumbnail,
                    Family = s_Part.Family,
                    Name = s_Part.Name,
                    Description = s_Part.Description,
                });
                s_Said.Add($"{s_Part.Part}{s_LookNote}: {string.Join(", ", s_Cloth.Select(p_M => p_M.Split('/')[^1]))}");
            }

            if (s_Parts.Count == 0)
            {
                p_Log($"  also {s_Soldier.Display}: nothing of the skin goes on him — {string.Join("; ", s_Said)}.");
                continue;
            }

            var s_Arms = s_Soldier.ArmsMesh;
            var s_Other = new OtherSoldierSkin
            {
                Key = s_SkinKey,
                Database = s_Soldier.Database,
                GuidFor = GuidFor,
                Bundle = $"{SkinEntriesBundle}{++s_Index}",
                Soldier = new PackageSoldier
                {
                    Key = s_Soldier.Key, Display = s_Soldier.Display, Team = s_Soldier.Team, Kit = s_Soldier.Kit, Expansion = s_Soldier.Expansion,
                    KitAsset = s_Soldier.KitAsset, Appearance = s_Soldier.Appearance, Arms = s_Arms,
                    // his trousers: the arms' materials that bind a lower body's diffuse in any variation (by MESH — CamoSession.TrousersMaterialsOf)
                    Trousers = p_Scan.Where(p_B => p_B.Mesh.Equals(s_Arms, StringComparison.OrdinalIgnoreCase) && p_B.MaterialId >= 0 &&
                                                   p_B.Textures.Any(p_T => p_T.Param.Equals("Diffuse", StringComparison.OrdinalIgnoreCase) &&
                                                                           p_T.Texture.Contains("/lowerbody/", StringComparison.OrdinalIgnoreCase)))
                        .Select(p_B => p_B.MaterialId).Distinct().OrderBy(p_I => p_I).ToList(),
                    Parts = s_Parts,
                    SkinKey = s_SkinKey,
                    Identifier = IdentifierOfSkinKey(s_SkinKey),
                },
            };

            var s_Plan = new SoldierBakePlan { Name = p_Plan.Name, Key = s_SkinKey, Folder = p_Plan.Folder, Identifier = s_Other.Soldier.Identifier!.Value, Soldier = s_Other.Soldier };
            var s_Jobs = PlanSkinEntries(s_Plan, s_Parts, s_Soldier, p_Scan, p_Numbers, Array.Empty<ShaderStub>(), GuidFor, s_Other.Variations, p_Problems, p_Log);
            if (s_Jobs == null || s_Jobs.Count == 0)
            {
                if (s_Jobs != null)
                    p_Log($"  also {s_Soldier.Display}: no entry to bake — as the game has him.");
                continue;
            }

            s_Other.Jobs.AddRange(s_Jobs);
            var s_Sets = s_Jobs.Select(p_J => p_J.Mesh).Distinct()
                .Select(p_M => new HashSet<string>(p_Bundles?.GetValueOrDefault(p_M) ?? new List<string>(), StringComparer.OrdinalIgnoreCase)).ToList();
            var s_Common = s_Sets.Aggregate((p_A, p_B) => { p_A.IntersectWith(p_B); return p_A; });
            if (s_Common.Count == 0)
            {
                p_Problems.Add($"also {s_Soldier.Display}: no level bundle carries all of {string.Join(", ", s_Jobs.Select(p_J => p_J.Mesh.Split('/')[^1]).Distinct())}.");
                continue;
            }

            s_Other.Soldier.Bundle = s_Other.Bundle;
            s_Other.Soldier.Levels = s_Common.OrderBy(p_B => p_B, StringComparer.Ordinal).ToList();
            s_Other.Soldier.Entries = s_Jobs.Select(p_J => p_J.Name).ToList();
            s_Others.Add(s_Other);
            p_Log($"  also {s_Soldier.Display} ({s_SkinKey}, identifier {s_Other.Soldier.Identifier}): {string.Join("; ", s_Said)} — " +
                  $"{s_Jobs.Count} entr(y/ies) from {s_Soldier.Database}, in {s_Other.Bundle} behind {s_Other.Soldier.Levels.Count} level bundle(s).");
            foreach (var s_Job in s_Jobs)
                p_Log($"    entry {s_Job.Name.Split('/', 5)[^1]} ({s_Job.Hash}): {s_Job.Mesh.Split('/')[^1]} from variation {s_Job.Source} — {s_Job.Said}.");
        }

        return s_Others;
    }
}
