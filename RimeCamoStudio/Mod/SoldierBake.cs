using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeCamoStudio.Mod;

/// <summary>A picture a soldier part's graph carries on a texture node, by the material parameter it is bound under ("CamoTile", "Mask"…).</summary>
public sealed record SoldierPicture(string Parameter, string File, bool Srgb);

/// <summary>
/// A graph of a soldier part with nodes or wires of its own, saved for the bake:
/// the package ships a COPY of <paramref name="Shader"/> (the shader the part's materials wear, as the document names it) compiled from it,
/// and the part's materials that wear that shader draw with the copy. <paramref name="Declared"/>: the parameters the copy declares for the
/// pictures at registers the shader has none at ("Picture9" at t9). <paramref name="Declarations"/>: the vertex declarations the part's meshes
/// draw that shader with — every deferred GBuffer solution of the copy for them must carry the graph, or the copy does not ship.
/// </summary>
public sealed record SoldierShader(string Shader, string GraphPath, IReadOnlyList<(string Parameter, int Register)> Declared,
    IReadOnlyList<string> Declarations);

/// <summary>One part of a soldier skin in a bake: its pictures, whether its camo covers all the cloth, and its numbers.</summary>
public sealed class SoldierBakePart
{
    /// <summary>"head", "upper" or "lower".</summary>
    public string Part { get; init; } = "";

    /// <summary>Its third-person meshes.</summary>
    public List<string> Meshes { get; init; } = new();

    /// <summary>The pictures on its graph's texture nodes, by parameter — the pattern is the one on CamoTile.</summary>
    public List<SoldierPicture> Pictures { get; init; } = new();

    /// <summary>
    /// The camo on ALL the cloth (the mask is a choice per part): the package ships a white mask and binds it as the part's
    /// Mask. Ignored when the part's graph carries a picture on Mask — that picture is the mask.
    /// </summary>
    public bool WhiteMask { get; init; }

    /// <summary>The numbers typed on its graph that differ from the game's own ("x,y,z,w" each).</summary>
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Its graphs with a logic of their own, one per shader they are drawn on (the torso's and the first-person arms' may differ).</summary>
    public List<SoldierShader> Shaders { get; init; } = new();

    /// <summary>The user's picture for its OWN cell (its tab of the soldier's window), or null to cut it out of its own pattern.</summary>
    public string? Thumbnail { get; init; }

    /// <summary>The family its own cell is filed under, or "" (as the skin's).</summary>
    public string Family { get; init; } = "";

    /// <summary>What its own cell reads on its tab, or "" (the skin's name).</summary>
    public string Name { get; init; } = "";

    /// <summary>What its own cell shows in the INFO box, or "" (the skin's text).</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// The game's appearance whose look this part STARTS from ("preview native camo" on a soldier, per part — a vanilla camo
    /// as the template): the framework copies this part's entries from that look's variations. Null: the skin's appearance.
    /// </summary>
    public string? Look { get; init; }

    /// <summary>That look's own pattern, cached — what its cell's picture is cut out of when the part brings none. Null without a look.</summary>
    public string? LookPicture { get; init; }

    /// <summary>
    /// ⭐ The OTHER soldiers this part goes to too (this model only, US only, RU, all…, ticked per part), by catalogue
    /// key. Only a SIMPLE part (its pattern, its mask as the game's or all the cloth, its numbers): a picture painted on this model's layout
    /// or a graph of its own does not travel. Each starts from the look of the same name, else his own; the tiling is PROPORTIONAL to each
    /// mesh's own (the game tunes it per mesh: Desert torsos 11 / 3.6 / 5.5). All the cloth chosen here as a FIX (this soldier's own mask
    /// puts the camo nowhere) does not travel as such: each other keeps his own mask and tiling (CamoBaker.PlanOtherSoldiers).
    /// </summary>
    public List<string> Others { get; init; } = new();
}

/// <summary>
/// Everything a soldier SKIN package is built from (the soldier on screen, the parts that were changed on him, a mask
/// chosen per part). The package itself carries textures only — the game's soldiers are dressed by the framework in Lua (variations,
/// entries, whitelists, pieces and appearances: the recipe proven in game on 2026-09-28), from the description written beside it.
/// </summary>
public sealed class SoldierBakePlan
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Description { get; init; } = "";
    public string Family { get; init; } = "";
    public uint Identifier { get; init; }

    /// <summary>The user's picture for the row, or null to cut it out of the skin's pattern.</summary>
    public string? ThumbnailImage { get; init; }

    /// <summary>A cached picture of the soldier's own camo, the row's picture of a skin that ships no pattern; null when none is cached.</summary>
    public string? FallbackThumbnail { get; init; }

    /// <summary>The soldier, as the package describes him (its parts filled by the bake).</summary>
    public PackageSoldier Soldier { get; init; } = new();

    /// <summary>
    /// The level his meshes are drawn in ("xp2_skybar"; Aftermath's "xp4_quake"): its shader database is the TEMPLATE the copies of his parts'
    /// shaders are cut from — only a level that fields him has solutions for his meshes' vertex declarations. The package's own database
    /// loads on every level.
    /// </summary>
    public string ShaderLevel { get; init; } = "";

    public List<SoldierBakePart> Parts { get; init; } = new();
}

public static partial class CamoBaker
{
    /// <summary>The unlock name a SOLDIER SKIN takes — another than a camo's, so a skin and a camo of the same name never meet.</summary>
    public static string SkinUnlockNameOf(string p_Key) => $"Characters/Custom/U_SKIN_{p_Key}";

    /// <summary>The identifier of the skin whose display name is given: its key, its unlock name, its hash (as a camo's, CASE SENSITIVE).</summary>
    public static uint IdentifierOfSkin(string p_Name) => UnlockIdentifierOf(SkinUnlockNameOf(KeyOf(p_Name)));

    /// <summary>
    /// The skin's key for ANOTHER soldier it goes to (per part, the soldiers ticked): "&lt;key&gt;_&lt;SOLDIER&gt;" — his variations,
    /// pieces and partitions are named after it (two soldiers can wear the same mesh: one name each), and his identifier comes from it.
    /// </summary>
    public static string SkinKeyFor(string p_SkinKey, string p_SoldierKey) => $"{p_SkinKey}_{p_SoldierKey.ToUpperInvariant()}";

    /// <summary>The identifier of a skin key (<see cref="IdentifierOfSkin"/> of an already made key).</summary>
    public static uint IdentifierOfSkinKey(string p_Key) => UnlockIdentifierOf(SkinUnlockNameOf(p_Key));

    /// <summary>
    /// The package folder of a skin: "skin_" and its key, lowercase. ⛔ Never the camo's own folder rule: a weapon camo and a soldier skin
    /// both called "Snow" would share sb/Win32/camos/snow, and the second bake clears the folder it writes to.
    /// </summary>
    public static string SkinFolderOf(string p_Name) => "skin_" + FolderOf(p_Name);

    /// <summary>The n-th texture (1-based) a skin ships.</summary>
    public static string SkinTextureName(string p_Key, int p_Index) => $"Characters/Custom/Skin_{p_Key}_{p_Index}";

    /// <summary>The white mask a skin ships when a part's camo goes on all the cloth.</summary>
    public static string SkinWhiteMaskName(string p_Key) => $"Characters/Custom/Skin_{p_Key}_White";

    /// <summary>The row's picture of a skin — not a camo's (PremiumCamo_&lt;key&gt;), for the same reason as the folder.</summary>
    public static string SkinThumbnailName(string p_Key) => $"{ThumbFolder}SoldierSkin_{p_Key}";

    /// <summary>The picture of a skin part's OWN cell (its tab of the soldier's window): "…/SoldierSkin_SNOW_upper".</summary>
    public static string SkinPartThumbnailName(string p_Key, string p_Part) => $"{ThumbFolder}SoldierSkin_{p_Key}_{p_Part}";

    /// <summary>The copy of a shader a skin ships for one part's graph: "Characters/Custom/Skin_SNOW_upper_characterroot".</summary>
    public static string SkinShaderName(string p_Key, string p_Part, string p_Shader) =>
        $"Characters/Custom/Skin_{p_Key}_{p_Part}_{p_Shader.Split('/')[^1].ToLowerInvariant()}";

    /// <summary>
    /// Builds ONE soldier skin package and registers it in the framework:
    /// <code>
    ///   packnc  (NONCAS)  packncv: the skin's textures (the patterns, the white mask, the other pictures), resident, in the cloth camo's
    ///                     pool group; packncui: the row's picture (UI group, on-demand, header only)
    ///   chunks  (NONCAS)  the picture's pixels
    ///   pack    (CAS)     the textures' partitions (TextureAsset stubs), so the framework finds them by name
    /// </code>
    /// A pattern (a picture on CamoTile) ships the way a camo's pattern does — DXT1 512×512, sRGB — which is the texture the in-game tests
    /// bound as CamoTile and as Mask (2026-09-28). Everything else is the soldier module's job, from camo.json's "soldier".
    /// ⛔ Every guid is derived from the skin's key with a seed of its own ("skin:" + key): a camo of the same name derives its guids from the
    /// bare key, and a chunk is global by guid.
    /// </summary>
    public static BakeResult BuildSoldier(SoldierBakePlan p_Plan, BakeContext p_Context) =>
        p_Plan.Folder.Trim().Length == 0
            ? BuildSoldierInPlace(p_Plan, p_Context)
            : KeepingThePackageThatWorked(FrameworkMod.PackageDir(p_Context.ModFolder, p_Plan.Folder), p_Context,
                () => BuildSoldierInPlace(p_Plan, p_Context));

    /// <summary>The skin's build itself, straight into its package folder — <see cref="BuildSoldier"/> keeps the package that worked around it.</summary>
    private static BakeResult BuildSoldierInPlace(SoldierBakePlan p_Plan, BakeContext p_Context)
    {
        var s_Result = new BakeResult();
        var s_Log = p_Context.Log;

        if (p_Plan.Key.Length == 0 || p_Plan.Identifier == 0 || p_Plan.Soldier.Key.Length == 0 || p_Plan.Parts.Count == 0)
        {
            s_Result.Problems.Add("the skin has no key, no identifier, no soldier or no part to dress — nothing to build.");
            return s_Result;
        }

        var s_PackageDir = FrameworkMod.PackageDir(p_Context.ModFolder, p_Plan.Folder);
        s_Result.PackageDir = s_PackageDir;
        Directory.CreateDirectory(s_PackageDir);
        Directory.CreateDirectory(p_Context.WorkDir);

        // ⛔ nothing of an older build of this skin survives into the new one (see Build)
        foreach (var s_Stale in Directory.GetFiles(s_PackageDir))
            File.Delete(s_Stale);

        var s_Seed = Fnv1A("skin:" + p_Plan.Key);
        string GuidFor(int p_Group, int p_Slot) => $"{s_Seed:x8}-{p_Group:x4}-4000-a000-{p_Slot:x12}";

        // --- the parts' own shaders FIRST: a copy of each shader a part's graph with a logic of its own is drawn on — and only the ones whose
        // copy would really draw the graph on his meshes (what a copy cannot cover is left out, said, and so are the pictures only it read) ----
        var s_Json = new JsonSerializerOptions { WriteIndented = true };
        var s_Lanes = p_Plan.Parts.SelectMany(p_P => p_P.Shaders.Select(p_S => (p_P.Part, Shader: p_S))).ToList();
        string? s_ShaderDb = null;
        var s_Stubs = new List<ShaderStub>();
        var s_Dropped = new HashSet<(string Part, string Shader)>();
        if (s_Lanes.Count > 0 &&
            PrepareSoldierShaders(p_Plan, p_Context, s_Lanes, GuidFor, s_Json, out s_ShaderDb, out s_Stubs, out s_Dropped) is { } s_ShaderError)
        {
            s_Result.Problems.Add(s_ShaderError);
            s_Log("BAKE FAILED — " + s_ShaderError);
            return s_Result;
        }

        // --- the textures: one per distinct picture (the same file on two parts is ONE texture), and one white mask if a part asks it ---
        var s_Textures = new List<(string Name, string Dds, string Partition, string Instance, bool Srgb, string What)>();
        var s_TextureOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var s_Parts = new List<PackageSoldierPart>();
        string? s_White = null;

        foreach (var s_Part in p_Plan.Parts)
        {
            // the copies of this part that ship, and the picture parameters only a copy left out would have read
            var s_Shipped = s_Part.Shaders.Where(p_S => !s_Dropped.Contains((s_Part.Part, p_S.Shader))).ToList();
            var s_Unread = s_Part.Shaders.Where(p_S => s_Dropped.Contains((s_Part.Part, p_S.Shader)))
                .SelectMany(p_S => p_S.Declared.Select(p_D => p_D.Parameter))
                .Where(p_P => !s_Shipped.Any(p_S => p_S.Declared.Any(p_D => p_D.Parameter == p_P)))
                .ToHashSet(StringComparer.Ordinal);

            var s_Mine = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s_Picture in s_Part.Pictures.Where(p_P => !s_Unread.Contains(p_P.Parameter)))
            {
                // a picture on CamoTile is a PATTERN: the camo lane (DXT1 512); any other picture keeps its own size (the graphs' pictures' lane)
                var s_Pattern = s_Picture.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase);
                // …and one on Mask keeps its ALPHA — where the camo goes (DXT5, as the game's masks: ConvertMaskPicture)
                var s_MaskPicture = s_Picture.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase);
                var s_Id = $"{(s_Pattern ? "pattern" : s_MaskPicture ? "mask" : "picture")}|{s_Picture.Srgb}|{Path.GetFullPath(s_Picture.File)}";
                if (!s_TextureOf.TryGetValue(s_Id, out var s_Name))
                {
                    // numbered among the pictures only (the white mask is named apart)
                    var s_Index = s_TextureOf.Count + 1;
                    s_Name = SkinTextureName(p_Plan.Key, s_Index);
                    var s_Dds = Path.Combine(p_Context.WorkDir, $"skin{s_Index}.dds");
                    var s_Error = s_Pattern
                        ? ConvertPattern(p_Context.Texconv, s_Picture.File, s_Dds, s_Log)
                        : s_MaskPicture
                            ? ConvertMaskPicture(p_Context.Texconv, s_Picture.File, s_Dds, s_Log)
                            : ConvertPicture(p_Context.Texconv, s_Picture.File, s_Dds, s_Log);
                    if (s_Error != null)
                    {
                        s_Result.Problems.Add($"{s_Part.Part} {s_Picture.Parameter}: {s_Error}");
                        return s_Result;
                    }

                    // a pattern is sRGB like the game's own cloth camos; a picture follows the preview's rule (sRGB unless a normal map)
                    s_Textures.Add((s_Name, s_Dds, GuidFor(0x5000 + s_Index, 1), GuidFor(0x5000 + s_Index, 2), s_Pattern || s_Picture.Srgb,
                        $"{Path.GetFileName(s_Picture.File)} ({(s_Pattern ? "pattern" : "picture")})"));
                    s_TextureOf[s_Id] = s_Name;
                }

                s_Mine[s_Picture.Parameter] = s_Name;
            }

            var s_Mask = s_Mine.ContainsKey("Mask") ? "picture" : "stock";
            // ⭐ a part that goes to other soldiers may need the white mask on one of them even where it takes the game's
            // here (a soldier whose own mask puts the camo nowhere) — or a picture of this model's (the US support's legs): the package makes
            // it whenever a part goes to others, and drops it when nobody binds it
            if (s_White == null && (s_Part.Others.Count > 0 || (s_Part.WhiteMask && !s_Mine.ContainsKey("Mask"))))
            {
                var s_Dds = Path.Combine(p_Context.WorkDir, "skin_white.dds");
                if (WhiteMask(p_Context.Texconv, s_Dds, s_Log) is { } s_Error)
                {
                    s_Result.Problems.Add("white mask: " + s_Error);
                    return s_Result;
                }

                s_White = SkinWhiteMaskName(p_Plan.Key);
                // a mask is data, not colour — and white is white either way
                s_Textures.Add((s_White, s_Dds, GuidFor(0x5F00, 1), GuidFor(0x5F00, 2), false, "white mask (all the cloth)"));
            }

            if (s_Part.WhiteMask && !s_Mine.ContainsKey("Mask"))
            {
                if (s_White == null)
                {
                    var s_Dds = Path.Combine(p_Context.WorkDir, "skin_white.dds");
                    if (WhiteMask(p_Context.Texconv, s_Dds, s_Log) is { } s_Error)
                    {
                        s_Result.Problems.Add("white mask: " + s_Error);
                        return s_Result;
                    }

                    s_White = SkinWhiteMaskName(p_Plan.Key);
                    // a mask is data, not colour — and white is white either way
                    s_Textures.Add((s_White, s_Dds, GuidFor(0x5F00, 1), GuidFor(0x5F00, 2), false, "white mask (all the cloth)"));
                }

                s_Mine["Mask"] = s_White;
                s_Mask = "white";
            }

            // a part left with nothing that reaches the game (its only change was a copy that cannot draw on his meshes) is not in the skin —
            // one that starts from another of the game's looks reaches it (that look's entries)
            if (s_Mine.Count == 0 && s_Part.Values.Count == 0 && s_Shipped.Count == 0 && s_Part.Look == null)
            {
                s_Log($"  {s_Part.Part}: nothing of it can reach the game (see the NOTE above) — it stays as the game ships it.");
                continue;
            }

            s_Parts.Add(new PackageSoldierPart
            {
                Part = s_Part.Part,
                Meshes = new List<string>(s_Part.Meshes),
                Appearance = s_Part.Look,
                Mask = s_Mask,
                Textures = s_Mine,
                Values = new Dictionary<string, string>(s_Part.Values, StringComparer.Ordinal),
                Shaders = s_Shipped.Select(p_S => new PackageSoldierShader
                {
                    Shader = p_S.Shader,
                    Clone = SkinShaderName(p_Plan.Key, s_Part.Part, p_S.Shader),
                    Declares = p_S.Declared.Select(p_D => p_D.Parameter).Distinct().ToList(),
                }).ToList(),
            });

            s_Log($"  {s_Part.Part}: " +
                  (s_Part.Look != null ? $"from the game's look {s_Part.Look.Split('/')[^1]}; " : "") +
                  (s_Mine.Count > 0 ? string.Join(", ", s_Mine.Select(p_T => $"{p_T.Key} = {p_T.Value.Split('/')[^1]}")) : "no texture of its own") +
                  $"; camo {(s_Mask == "white" ? "on ALL the cloth" : s_Mask == "picture" ? "where the picture on Mask puts it" : "where the game's mask puts it")}" +
                  (s_Part.Values.Count > 0 ? $"; numbers {string.Join(", ", s_Part.Values.Select(p_V => $"{p_V.Key}={p_V.Value}"))}" : "") +
                  (s_Shipped.Count > 0
                      ? $"; its OWN shader on {string.Join(", ", s_Shipped.Select(p_S => $"'{p_S.Shader.Split('/')[^1]}'"))} (the graph's nodes and wires)"
                      : "") + ".");
        }

        if (s_Parts.Count == 0)
        {
            s_Result.Problems.Add("nothing of the skin can reach the game: every part's change was a shader copy that cannot draw on his meshes (see the NOTEs).");
            s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
            return s_Result;
        }

        // --- the row's picture: the user's, else the skin's pattern, else any picture of it, else the soldier's own camo ------------------
        var s_ThumbName = SkinThumbnailName(p_Plan.Key);
        var s_ThumbPartition = GuidFor(0, 7);
        var s_ThumbInstance = GuidFor(0, 8);
        var s_ThumbChunk = GuidFor(0, 0x0a); // last byte even: a raw chunk, no compression flag
        var s_ThumbDds = Path.Combine(p_Context.WorkDir, "thumb.dds");
        var s_ThumbBin = Path.Combine(p_Context.WorkDir, "thumb.bin");
        // ⭐ the UPPER BODY's first (the skin-wide picture box is gone — the whole soldier's cell, OUTFIT, wears the upper
        // body's), then the other parts in order
        var s_ByUpper = p_Plan.Parts.OrderBy(p_P => p_P.Part == "upper" ? 0 : 1).ToList();
        // ⛔ never a Mask picture (review 2026-09-29): it is where the camo goes, not what it looks like — the US support's legs with only their
        // tiling changed showed a crop of their mask on the OUTFIT and LEGS cells
        var s_ThumbSource = p_Plan.ThumbnailImage ??
                            s_ByUpper.SelectMany(p_P => p_P.Pictures).FirstOrDefault(p_P => p_P.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase))?.File ??
                            s_ByUpper.SelectMany(p_P => p_P.Pictures).FirstOrDefault(p_P => !p_P.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase))?.File ??
                            p_Plan.FallbackThumbnail;
        if (p_Plan.ThumbnailImage != null)
            s_Log($"  thumbnail from the user's picture: {Path.GetFileName(p_Plan.ThumbnailImage)}");
        else if (s_ThumbSource == null)
            s_Log("  thumbnail: no picture chosen, none on the skin and no cached picture of the soldier's own camo — the row has no picture of its own.");
        else
            s_Log($"  thumbnail cut from {Path.GetFileName(s_ThumbSource)}.");
        var s_HasThumb = s_ThumbSource != null && MakeThumbnail(p_Context.Texconv, s_ThumbSource, p_Context.WorkDir, s_ThumbDds, s_ThumbBin, s_Log);

        // --- each part's OWN cell (each part of the body can be a different thing): the skin is listed on
        // each part's tab of the soldier's window too, and there it shows THAT part — the user's picture for it, else cut out of its own
        // pattern, else out of any picture of it — filed under its own category when one was chosen. A part whose picture would be the row's
        // (the same file) or that carries none has no picture of its own: the row's shows on its tab. The same file on two parts ships once.
        var s_Thumbs = new List<(string Name, string Dds, string Bin, string Partition, string Instance, string Chunk)>();
        if (s_HasThumb)
            s_Thumbs.Add((s_ThumbName, s_ThumbDds, s_ThumbBin, s_ThumbPartition, s_ThumbInstance, s_ThumbChunk));
        var s_ThumbOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // the picture's full path -> its thumbnail's name
        if (s_HasThumb)
            s_ThumbOf[Path.GetFullPath(s_ThumbSource!)] = s_ThumbName;

        foreach (var s_Part in s_Parts)
        {
            var s_From = p_Plan.Parts.First(p_P => p_P.Part == s_Part.Part);
            s_Part.Family = s_From.Family.Length > 0 ? s_From.Family : null;
            s_Part.Name = s_From.Name.Trim().Length > 0 ? s_From.Name.Trim() : null;
            s_Part.Description = s_From.Description.Trim().Length > 0 ? s_From.Description.Trim() : null;
            var s_Source = s_From.Thumbnail ??
                           s_From.Pictures.FirstOrDefault(p_P => p_P.Parameter.Equals("CamoTile", StringComparison.OrdinalIgnoreCase))?.File ??
                           s_From.Pictures.FirstOrDefault(p_P => !p_P.Parameter.Equals("Mask", StringComparison.OrdinalIgnoreCase))?.File ??
                           // (a part that starts from another of the game's looks and brings no picture: that look's pattern)
                           s_From.LookPicture;
            var s_Filed = (s_Part.Name != null ? $", named '{s_Part.Name}'" : "") + (s_Part.Family != null ? $", filed under {s_Part.Family}" : "");
            if (s_Source == null)
            {
                s_Log($"  {s_Part.Part}'s own cell: no picture of its own — the row's shows on its tab{s_Filed}.");
                continue;
            }

            if (s_ThumbOf.TryGetValue(Path.GetFullPath(s_Source), out var s_Same))
            {
                s_Part.Thumbnail = s_Same == s_ThumbName ? null : s_Same;
                s_Log($"  {s_Part.Part}'s own cell: {Path.GetFileName(s_Source)}, " +
                      (s_Part.Thumbnail == null ? "the row's own picture" : $"the picture of another part ({s_Same.Split('/')[^1]})") + $"{s_Filed}.");
                continue;
            }

            var s_Name = SkinPartThumbnailName(p_Plan.Key, s_Part.Part);
            var s_Dds = Path.Combine(p_Context.WorkDir, $"thumb_{s_Part.Part}.dds");
            var s_Bin = Path.Combine(p_Context.WorkDir, $"thumb_{s_Part.Part}.bin");
            s_Log($"  {s_Part.Part}'s own cell: {(s_From.Thumbnail != null ? "the user's picture" : "cut from")} {Path.GetFileName(s_Source)}{s_Filed}.");
            if (!MakeThumbnail(p_Context.Texconv, s_Source, p_Context.WorkDir, s_Dds, s_Bin, s_Log))
            {
                s_Log($"  {s_Part.Part}'s own cell: its picture could not be made — the row's shows on its tab.");
                continue;
            }

            var s_Group = 0x10 + s_Thumbs.Count;
            s_Thumbs.Add((s_Name, s_Dds, s_Bin, GuidFor(s_Group, 7), GuidFor(s_Group, 8), GuidFor(s_Group, 0x0a)));
            s_ThumbOf[Path.GetFullPath(s_Source)] = s_Name;
            s_Part.Thumbnail = s_Name;
        }

        // --- ⭐ THE ENTRIES, BAKED (the way the vehicles' are): the soldier module used to clone them into the level's
        // variation databases from Lua — as they loaded it killed the MP_007 server (their references not loaded yet), at the build it came
        // too late for the client's index (crash drawing the mannequin). They go in a bundle of the package's own, behind the level bundles
        // that carry every mesh they point at, and the engine indexes them as it indexes a vehicle's veh0 --------------------------------
        var s_EntryVariations = new List<Dictionary<string, object>>();
        List<SkinEntryJob> s_EntryJobs;
        string s_EntryDb;
        List<string> s_EntryLevels;
        // ⭐ the other soldiers its parts go to (ticked per part): each with his own entries, in a bundle of his own
        List<OtherSoldierSkin> s_OtherSkins;
        {
            var s_Catalog = Catalog.SoldierCatalog.Load();
            var s_CatalogSoldier = s_Catalog.Soldiers.FirstOrDefault(p_S => p_S.Key.Equals(p_Plan.Soldier.Key, StringComparison.OrdinalIgnoreCase));
            if (s_CatalogSoldier == null || s_CatalogSoldier.Database.Length == 0)
            {
                s_Result.Problems.Add($"{p_Plan.Soldier.Display} is not in the Soldiers catalogue (or names no variation database) — nothing to copy his entries from.");
                s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
                return s_Result;
            }

            s_EntryDb = s_CatalogSoldier.Database;
            var s_Scan = Cache.GameCache.DiscoverSoldierBindings(s_Catalog, p_Context.GamePath, p_Context.RimeRepl, s_Log);
            var s_Numbers = SkinScanNumbers(Path.Combine(Settings.CacheFolder, Cache.GameCache.SoldierScanFile));
            var s_Planned = PlanSkinEntries(p_Plan, s_Parts, s_CatalogSoldier, s_Scan, s_Numbers, s_Stubs, GuidFor, s_EntryVariations, s_Result.Problems, s_Log);
            if (s_Planned == null || s_Planned.Count == 0)
            {
                if (s_Result.Problems.Count == 0)
                    s_Result.Problems.Add("no entry to bake: no part lists a mesh of its look.");
                s_Log("BAKE FAILED — " + string.Join(" | ", s_Result.Problems));
                return s_Result;
            }

            s_EntryJobs = s_Planned;

            // the level bundles that carry EVERY mesh the entries point at (Program --soldierbundles, measured once)
            Dictionary<string, List<string>>? s_Bundles = null;
            if (File.Exists(Program.SoldierBundlesPath))
                s_Bundles = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(Program.SoldierBundlesPath));
            var s_Sets = s_EntryJobs.Select(p_J => p_J.Mesh).Distinct()
                .Select(p_M => new HashSet<string>(s_Bundles?.GetValueOrDefault(p_M) ?? new List<string>(), StringComparer.OrdinalIgnoreCase)).ToList();
            var s_Common = s_Sets.Aggregate((p_A, p_B) => { p_A.IntersectWith(p_B); return p_A; });
            s_EntryLevels = s_Common.OrderBy(p_B => p_B, StringComparer.Ordinal).ToList();
            if (s_EntryLevels.Count == 0)
            {
                s_Result.Problems.Add(s_Bundles == null
                    ? $"{Program.SoldierBundlesPath} is missing: where the soldier meshes are loaded is unknown (RimeCamoStudio --soldierbundles)."
                    : $"no level bundle carries all of {string.Join(", ", s_EntryJobs.Select(p_J => p_J.Mesh.Split('/')[^1]).Distinct())} — the entries could not load anywhere.");
                s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
                return s_Result;
            }

            foreach (var s_Job in s_EntryJobs)
                s_Log($"  entry {s_Job.Name.Split('/', 5)[^1]} ({s_Job.Hash}): {s_Job.Mesh.Split('/')[^1]} from variation {s_Job.Source} — {s_Job.Said}.");
            s_Log($"  entries: {s_EntryJobs.Count} from {s_EntryDb}, loaded behind {s_EntryLevels.Count} level bundle(s) that carry all their meshes.");

            // (the meshes' own numbers too: a tiling no variation sets is the material's — the ratio's fallback)
            var s_MaterialNumbers = SkinScanNumbers(Path.Combine(Settings.CacheFolder, Cache.GameCache.SoldierScanFile), "material");
            s_OtherSkins = PlanOtherSoldiers(p_Plan, s_Parts, s_CatalogSoldier, s_EntryJobs, s_Catalog, s_Scan, s_Numbers, s_MaterialNumbers, s_Bundles,
                s_White, s_Result.Problems, s_Log);
            if (s_Result.Problems.Count > 0)
            {
                s_Log("BAKE FAILED — " + string.Join(" | ", s_Result.Problems));
                return s_Result;
            }

            // the white mask made in case another soldier needed it ships only when some part of someone binds it
            if (s_White != null && !s_Parts.Concat(s_OtherSkins.SelectMany(p_O => p_O.Soldier.Parts)).Any(p_P => p_P.Textures.ContainsValue(s_White)))
            {
                s_Textures.RemoveAll(p_T => p_T.Name == s_White);
                s_Log("  the white mask: no part of anyone binds it — not shipped.");
            }
        }

        string StubOf(string p_Dds) => Path.Combine(p_Context.WorkDir, $"tex_{Path.GetFileNameWithoutExtension(p_Dds)}.json");
        foreach (var s_Texture in s_Textures)
            WriteTextureStub(Path.ChangeExtension(s_Texture.Dds, ".json"), s_Texture.Name, s_Texture.Partition, s_Texture.Instance, s_Json);
        foreach (var s_Thumb in s_Thumbs)
            WriteTextureStub(StubOf(s_Thumb.Dds), s_Thumb.Name, s_Thumb.Partition, s_Thumb.Instance, s_Json);

        // the entries' two partitions of our own: the hidden shader's stub and the skin's material variations
        var s_HiddenStub = new ShaderStub(SkinHiddenShaderName(p_Plan.Key), GuidFor(0x7001, 1), GuidFor(0x7001, 2));
        var s_HiddenStubPath = Path.Combine(p_Context.WorkDir, "skin_hidden.json");
        var s_MaterialsPath = Path.Combine(p_Context.WorkDir, "skin_materials.json");
        WriteShaderStub(s_HiddenStubPath, s_HiddenStub, s_Json);
        WriteSkinMaterials(s_MaterialsPath, SkinMaterialsName(p_Plan.Key), GuidFor(0x7000, 1), GuidFor(0x7000, 2), s_HiddenStub, s_EntryVariations, s_Json);
        // …and each other soldier's own two (named after his key, guids of his seed: none shared between bundles loaded on one level)
        var s_OtherFiles = new List<(OtherSoldierSkin Other, string Stub, string Materials)>();
        foreach (var s_Other in s_OtherSkins)
        {
            var s_OtherStub = new ShaderStub(SkinHiddenShaderName(s_Other.Key), s_Other.GuidFor(0x7001, 1), s_Other.GuidFor(0x7001, 2));
            var s_StubPath = Path.Combine(p_Context.WorkDir, $"skin_hidden_{s_Other.Bundle}.json");
            var s_MatPath = Path.Combine(p_Context.WorkDir, $"skin_materials_{s_Other.Bundle}.json");
            WriteShaderStub(s_StubPath, s_OtherStub, s_Json);
            WriteSkinMaterials(s_MatPath, SkinMaterialsName(s_Other.Key), s_Other.GuidFor(0x7000, 1), s_Other.GuidFor(0x7000, 2), s_OtherStub, s_Other.Variations, s_Json);
            s_OtherFiles.Add((s_Other, s_StubPath, s_MatPath));
        }

        // --- the Rime script (the camo package's lanes: textures, pictures, shaders, and the entries) ------------------------------------
        var s_Stem = FrameworkMod.StemOf(p_Plan.Folder, false);
        var s_Log2 = "";
        if (s_Textures.Count > 0 || s_Thumbs.Count > 0 || s_ShaderDb != null || s_EntryJobs.Count > 0)
        {
            var s_Sb = Path.Combine(p_Context.ModFolder, "sb");
            var s_Script = new StringBuilder();
            s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");
            // (an empty superbundle declared in mod.json is the same endless load as a missing one: packnc only with something in it)
            if (s_Textures.Count > 0 || s_Thumbs.Count > 0)
                s_Script.AppendLine($"build_sb Win32/{s_Stem}/packnc Frostbite2_0 \"{s_Sb}\" false");

            if (s_Textures.Count > 0)
            {
                s_Script.AppendLine($"build_bundle win32/{s_Stem}/packncv");
                // resident (a mod does not fill the streaming pool), in the pool group of the game's cloth camos — the soldiers' own patterns
                foreach (var s_Texture in s_Textures)
                    s_Script.AppendLine($"add_dds_texture \"{s_Texture.Name}\" \"{s_Texture.Dds}\" {(s_Texture.Srgb ? "true" : "false")} false false \"\" \"{PatternGroupDonor}\" false");
                s_Script.AppendLine("check_textures");
                s_Script.AppendLine("build");
            }

            if (s_Thumbs.Count > 0)
            {
                // the row's picture and the parts' own, one bundle (the framework loads it whole into the UI's compartment)
                s_Script.AppendLine($"build_bundle win32/{s_Stem}/packncui");
                foreach (var s_Thumb in s_Thumbs)
                {
                    s_Script.AppendLine($"add_dds_texture \"{s_Thumb.Name}\" \"{s_Thumb.Dds}\" false true false \"UI\" \"{ThumbGroupDonor}\" true \"{s_Thumb.Chunk}\"");
                    s_Script.AppendLine($"add_json_partition \"{s_Thumb.Name}\" \"{StubOf(s_Thumb.Dds)}\"");
                    s_Script.AppendLine($"remove_chunk {s_Thumb.Chunk}");
                }
                s_Script.AppendLine("build");
            }

            if (s_Textures.Count > 0 || s_Thumbs.Count > 0)
                s_Script.AppendLine("build");

            if (s_Thumbs.Count > 0)
            {
                s_Script.AppendLine($"build_sb Win32/{s_Stem}/chunks Frostbite2_0 \"{s_Sb}\" false");
                foreach (var s_Thumb in s_Thumbs)
                    s_Script.AppendLine($"add_chunk {s_Thumb.Chunk} \"{s_Thumb.Bin}\" \"{s_Thumb.Name.ToLowerInvariant()}\"");
                s_Script.AppendLine("build");
            }

            if (s_Textures.Count > 0 || s_EntryJobs.Count > 0)
            {
                s_Script.AppendLine($"build_sb Win32/{s_Stem}/pack Frostbite2_0 \"{s_Sb}\" true");
                if (s_Textures.Count > 0)
                {
                    // CAS: the textures' partitions — what finds them by name (SearchForDataContainer) once packncv has brought their pixels
                    s_Script.AppendLine($"build_bundle win32/{s_Stem}/packv");
                    foreach (var s_Texture in s_Textures)
                        s_Script.AppendLine($"add_json_partition \"{s_Texture.Name}\" \"{Path.ChangeExtension(s_Texture.Dds, ".json")}\"");
                    s_Script.AppendLine("resolve_resource_dependencies 1");
                    s_Script.AppendLine("resolve_missing_chunks 1");
                    s_Script.AppendLine("build");
                }

                // ⭐ the entries (see above): the hidden shader's stub, the skin's material variations, and per job a copy of its stock entry
                // under the skin's variation hash — every entry of the mesh copied first ('all', the weapons' way: the vanilla ones keep the
                // list head), ours appended from the source variation, the part's textures only on the materials it dresses ('@' scope)
                s_Script.AppendLine($"build_bundle win32/{s_Stem}/{SkinEntriesBundle}");
                s_Script.AppendLine($"add_json_partition \"{SkinHiddenShaderName(p_Plan.Key)}\" \"{s_HiddenStubPath}\"");
                s_Script.AppendLine($"add_json_partition \"{SkinMaterialsName(p_Plan.Key)}\" \"{s_MaterialsPath}\"");
                var s_TextureRef = s_Textures.ToDictionary(p_T => p_T.Name, p_T => $"{p_T.Partition}:{p_T.Instance}", StringComparer.OrdinalIgnoreCase);
                for (var j = 0; j < s_EntryJobs.Count; j++)
                {
                    var s_Job = s_EntryJobs[j];
                    var s_Spec = s_Parts.First(p_P => p_P.Part == s_Job.Part);
                    var s_Refs = s_Job.Refs.Count == 0 ? "" : $"{GuidFor(0x7000, 1)}:{string.Join(",", s_Job.Refs.Select(p_R => $"{p_R.Instance}@*#{p_R.Id}"))}";
                    // ⛔ a picture on the part's Mask is painted on its THIRD-PERSON mesh's layout (the US support's legs' starting
                    // document): the first-person arms' copies keep their own mask — the tiling pattern goes everywhere, a layout does not
                    var s_Bindings = string.Join(",", s_Job.Scopes.SelectMany(p_Scope => s_Spec.Textures
                        .Where(p_T => s_TextureRef.ContainsKey(p_T.Value))
                        .Where(p_T => !(s_Job.Side != null && s_Spec.Mask == "picture" && p_T.Key.Equals("Mask", StringComparison.OrdinalIgnoreCase)))
                        .Select(p_T => $"{p_T.Key}>{s_TextureRef[p_T.Value]}@{p_Scope}")));
                    // ⛔ ONE KEY, ONE PARTITION (OnlyVariation 1): 'all' reads every entry of the mesh (the source variation is one of
                    // them) but the partition keeps ONLY ours — the copies would race the level's own entries for the stock keys, and the
                    // three arms copies (torso, whole, legs) of the same mesh would race each other for them
                    s_Script.AppendLine($"mvdb_add_entry {s_EntryDb} {SkinEntryDbName(p_Plan.Key, j)} 1 {s_Job.Mesh} all \"\" \"\" \"\" \"\" " +
                                        $"{GuidFor(0x7100 + j, 1)} {GuidFor(0x7100 + j, 2)} \"\" {s_Job.Hash} \"{s_Refs}\" \"\" \"\" \"\" \"\" " +
                                        $"\"{s_Bindings}\" 1 1 {s_Job.Source}");
                }

                s_Script.AppendLine("resolve_resource_dependencies 1");
                s_Script.AppendLine("resolve_missing_chunks 1");
                s_Script.AppendLine("build");

                // ⭐ each other soldier's own bundle: his stub, his material variations, his entries — copied from HIS
                // database (base or Aftermath), binding the skin's own textures
                foreach (var (s_Other, s_OtherStubPath, s_OtherMatPath) in s_OtherFiles)
                {
                    s_Script.AppendLine($"build_bundle win32/{s_Stem}/{s_Other.Bundle}");
                    s_Script.AppendLine($"add_json_partition \"{SkinHiddenShaderName(s_Other.Key)}\" \"{s_OtherStubPath}\"");
                    s_Script.AppendLine($"add_json_partition \"{SkinMaterialsName(s_Other.Key)}\" \"{s_OtherMatPath}\"");
                    for (var j = 0; j < s_Other.Jobs.Count; j++)
                    {
                        var s_Job = s_Other.Jobs[j];
                        var s_Spec = s_Other.Soldier.Parts.First(p_P => p_P.Part == s_Job.Part);
                        var s_Refs = s_Job.Refs.Count == 0 ? "" : $"{s_Other.GuidFor(0x7000, 1)}:{string.Join(",", s_Job.Refs.Select(p_R => $"{p_R.Instance}@*#{p_R.Id}"))}";
                        var s_Bindings = string.Join(",", s_Job.Scopes.SelectMany(p_Scope => s_Spec.Textures
                            .Where(p_T => s_TextureRef.ContainsKey(p_T.Value))
                            .Select(p_T => $"{p_T.Key}>{s_TextureRef[p_T.Value]}@{p_Scope}")));
                        s_Script.AppendLine($"mvdb_add_entry {s_Other.Database} {SkinEntryDbName(s_Other.Key, j)} 1 {s_Job.Mesh} all \"\" \"\" \"\" \"\" " +
                                            $"{s_Other.GuidFor(0x7100 + j, 1)} {s_Other.GuidFor(0x7100 + j, 2)} \"\" {s_Job.Hash} \"{s_Refs}\" \"\" \"\" \"\" \"\" " +
                                            $"\"{s_Bindings}\" 1 1 {s_Job.Source}");
                    }

                    s_Script.AppendLine("resolve_resource_dependencies 1");
                    s_Script.AppendLine("resolve_missing_chunks 1");
                    s_Script.AppendLine("build");
                }

                s_Script.AppendLine("build");
            }

            // The parts' own shaders, the way a camo ships its own (Build): their database under a FRESH resource name plus the stubs the
            // soldier's material variations will point at, in a bundle the loader puts BEFORE the level's (a fresh-named database only adds
            // its keys; the level's own is never touched).
            if (s_ShaderDb != null)
            {
                s_Script.AppendLine($"build_sb Win32/{s_Stem}/shaders Frostbite2_0 \"{s_Sb}\" true");
                s_Script.AppendLine($"build_bundle win32/{s_Stem}/shadersv");
                for (var s = 0; s < s_Stubs.Count; s++)
                    s_Script.AppendLine($"add_json_partition \"{s_Stubs[s].Name}\" \"{Path.Combine(p_Context.WorkDir, "shader", $"stub_skin{s}.json")}\"");
                s_Script.AppendLine($"replace_resource_as {SkinShaderDbOf(p_Plan.ShaderLevel)} levels/{s_Stem}/shaderdb 1 \"{s_ShaderDb}\"");
                s_Script.AppendLine("resolve_resource_dependencies 1");
                s_Script.AppendLine("resolve_missing_chunks 1");
                s_Script.AppendLine("build");
                s_Script.AppendLine("build");
            }

            s_Script.AppendLine("exit");

            var s_ScriptPath = Path.Combine(p_Context.WorkDir, "build.rime");
            File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));
            s_Log($"Building skin '{p_Plan.Name}' ({p_Plan.Key}) for {p_Plan.Soldier.Display}: {s_Textures.Count} texture(s), " +
                  $"{(s_HasThumb ? "a" : "no")} row picture{(s_Thumbs.Count > (s_HasThumb ? 1 : 0) ? $" and {s_Thumbs.Count - (s_HasThumb ? 1 : 0)} part picture(s)" : "")} " +
                  "— this mounts the game.");

            s_Log2 = Cache.GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
            s_Result.LogPath = Path.Combine(p_Context.WorkDir, "build.log");
            File.WriteAllText(s_Result.LogPath, s_Log2, new UTF8Encoding(false));

            AuditSoldier(s_Result, s_Log2, s_PackageDir, s_Textures.Select(p_T => p_T.Name).ToList(), s_Thumbs.Select(p_T => p_T.Name).ToList(),
                s_ShaderDb != null ? $"levels/{s_Stem}/shaderdb" : null, s_Stubs.Select(p_S => p_S.Name).ToList(),
                Enumerable.Range(0, s_EntryJobs.Count).Select(j => SkinEntryDbName(p_Plan.Key, j))
                    .Append(SkinMaterialsName(p_Plan.Key)).Append(SkinHiddenShaderName(p_Plan.Key))
                    .Concat(s_OtherSkins.SelectMany(p_O => Enumerable.Range(0, p_O.Jobs.Count).Select(j => SkinEntryDbName(p_O.Key, j))
                        .Append(SkinMaterialsName(p_O.Key)).Append(SkinHiddenShaderName(p_O.Key)))).ToList());

            // the entries: each copied from its source variation under its hash, none failing, no material id the mesh does not have (the
            // other soldiers' as the skin's own)
            foreach (var s_Job in s_EntryJobs.Concat(s_OtherSkins.SelectMany(p_O => p_O.Jobs)))
                if (!s_Log2.Contains($"MVDB-SOURCE-ENTRY: {s_Job.Mesh} hash {s_Job.Source} ", StringComparison.OrdinalIgnoreCase) ||
                    !s_Log2.Contains($"-> new hash {s_Job.Hash}", StringComparison.Ordinal))
                    s_Result.Problems.Add($"entry {s_Job.Name}: no copy of {s_Job.Mesh.Split('/')[^1]} variation {s_Job.Source} under hash {s_Job.Hash} in the build log.");
            // ... and each partition owns ONLY its entry (none of the game's keys rides along)
            foreach (var s_Job in s_EntryJobs.Concat(s_OtherSkins.SelectMany(p_O => p_O.Jobs)))
                if (!s_Log2.Contains($"the partition owns only hash {s_Job.Hash}.", StringComparison.Ordinal) ||
                    !s_Log2.Contains($"with 1 '{s_Job.Mesh}' entr(ies) (variationAssetNameHash={s_Job.Hash},", StringComparison.OrdinalIgnoreCase))
                    s_Result.Problems.Add($"entry {s_Job.Name}: its partition does not hold ONLY hash {s_Job.Hash} (the game's entries of " +
                                          $"{s_Job.Mesh.Split('/')[^1]} would ride along and race the level's for their keys).");
            foreach (var s_Bad in s_Log2.Split('\n').Where(p_L => p_L.Contains("VARIATION FAILED") || p_L.Contains("VARIATION WARNING") ||
                                                               p_L.Contains("No entry of ") || p_L.Contains("No MVDB entry found") ||
                                                               p_L.Contains("OnlyVariation: expected") || p_L.Contains("OnlyVariation needs")))
                s_Result.Problems.Add("entries: " + s_Bad.Trim());
        }
        else
            s_Log($"  skin '{p_Plan.Name}' carries NUMBERS ONLY — no texture and no row picture: nothing to build, its description is the package.");

        if (!s_Result.Ok)
        {
            s_Log($"BAKE FAILED — {s_Result.Problems.Count} problem(s); the skin was NOT registered:");
            foreach (var s_Problem in s_Result.Problems)
                s_Log("  " + s_Problem);

            var s_Failed = Path.Combine(p_Context.WorkDir, "failed_package");
            Directory.CreateDirectory(s_Failed);
            foreach (var s_File in Directory.GetFiles(s_PackageDir))
                File.Move(s_File, Path.Combine(s_Failed, Path.GetFileName(s_File)), true);

            Directory.Delete(s_PackageDir, false);
            s_Log($"  (what was built is kept under {s_Failed})");
            return s_Result;
        }

        var s_Soldier = p_Plan.Soldier;
        s_Soldier.Parts = s_Parts;
        s_Soldier.Bundle = SkinEntriesBundle;
        s_Soldier.Levels = s_EntryLevels;
        s_Soldier.Entries = s_EntryJobs.Select(p_J => p_J.Name).ToList();
        var s_Info = new PackageInfo
        {
            Key = p_Plan.Key,
            Folder = p_Plan.Folder,
            Name = p_Plan.Name,
            Description = p_Plan.Description,
            Family = p_Plan.Family,
            Identifier = p_Plan.Identifier,
            Superbundle = s_Stem,
            Thumbnail = s_HasThumb ? s_ThumbName : null,
            // ⛔ what makes a loader PREPEND <stem>/packncv (the generated textures' annex) — the same flag, the same meaning (see PackageInfo)
            Stickers = s_Textures.Count > 0,
            // …and <stem>/shaders, the parts' own shaders, prepended too
            Shader = s_ShaderDb != null,
            // a skin paints no weapon body and offers no camo row
            Body = false,
            Soldier = s_Soldier,
            // ⭐ the other soldiers it goes to, each a skin of his in the index
            Also = s_OtherSkins.Count > 0 ? s_OtherSkins.Select(p_O => p_O.Soldier).ToList() : null,
            Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        };

        var s_InfoJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Path.Combine(s_PackageDir, "camo.json"), JsonSerializer.Serialize(s_Info, s_InfoJson), new UTF8Encoding(false));
        s_Result.Package = s_Info;

        var s_Sizes = Directory.GetFiles(s_PackageDir, "*.sb").Select(p_F => $"{Path.GetFileName(p_F)} {new FileInfo(p_F).Length / 1024} KB").ToList();
        s_Result.Summary = $"Skin '{p_Plan.Folder}' for {s_Soldier.Display}: {s_Parts.Count} part(s) ({string.Join(", ", s_Parts.Select(p_P => p_P.Part))}), " +
                           $"{s_Textures.Count} texture(s)" + (s_Stubs.Count > 0 ? $", {s_Stubs.Count} shader cop(ies) of its own" : "") +
                           (s_OtherSkins.Count > 0
                               ? $", and {s_OtherSkins.Count} other soldier(s): {string.Join(", ", s_OtherSkins.Select(p_O => $"{p_O.Soldier.Display} ({string.Join("+", p_O.Soldier.Parts.Select(p_P => p_P.Part))})"))}"
                               : "") +
                           (s_Sizes.Count > 0 ? $" — {string.Join(", ", s_Sizes)}." : ", no superbundle.");
        s_Log(s_Result.Summary);

        s_Result.Summary += " " + FrameworkMod.Register(p_Context.ModFolder, s_Log);
        return s_Result;
    }

    /// <summary>A white 512×512 mask, DXT1 with its mips — the pattern's own lane, so it lands where the tested textures landed.</summary>
    private static string? WhiteMask(string p_Texconv, string p_Out, Action<string> p_Log)
    {
        const int c_Side = 512;
        var s_Pixels = new byte[c_Side * c_Side * 4];
        Array.Fill(s_Pixels, (byte) 255);
        var s_Image = BitmapSource.Create(c_Side, c_Side, 96, 96, PixelFormats.Bgra32, null, s_Pixels, c_Side * 4);

        var s_Folder = Path.GetDirectoryName(p_Out)!;
        var s_Bmp = Path.Combine(s_Folder, "skin_white.bmp");
        WriteBmp(s_Image, s_Bmp);

        var s_Dir = Path.Combine(s_Folder, "conv_white");
        Directory.CreateDirectory(s_Dir);
        var s_Error = RunTexconv(p_Texconv, $"-nologo -y -w {c_Side} -h {c_Side} -m 0 -f DXT1 -o \"{s_Dir}\" \"{s_Bmp}\"",
            Path.Combine(s_Dir, "skin_white.dds"), p_Out);
        if (s_Error != null)
            return s_Error;

        var s_Header = ReadHeader(p_Out);
        if (s_Header.FourCc != "DXT1" || s_Header.Width != c_Side || s_Header.Height != c_Side)
            return $"it came out as {s_Header.FourCc} {s_Header.Width}x{s_Header.Height}, expected DXT1 {c_Side}x{c_Side}.";

        p_Log($"  white mask: DXT1 {c_Side}x{c_Side}, {s_Header.Mips} mip(s).");
        return null;
    }

    /// <summary>
    /// The skin's package read the way the game will (Audit's rules for what a skin carries): every superbundle built, no Rime error, every
    /// texture listed by check_textures with its pixels in packnc and its partition in pack, the row's pixels in chunks.
    /// </summary>
    private static void AuditSoldier(BakeResult p_Result, string p_Log, string p_PackageDir, IReadOnlyList<string> p_Textures,
        IReadOnlyList<string> p_Thumbs, string? p_ShaderDb, IReadOnlyList<string> p_Stubs, IReadOnlyList<string>? p_EntryPartitions = null)
    {
        var p_Thumb = p_Thumbs.Count > 0;
        var s_Entries = p_EntryPartitions is { Count: > 0 };
        // packnc with textures or a picture; chunks with a picture; pack with textures or entries; shaders with the parts' own shaders
        var s_Expected = (p_Textures.Count > 0 || p_Thumb ? 1 : 0) + (p_Thumb ? 1 : 0) + (p_Textures.Count > 0 || s_Entries ? 1 : 0) + (p_ShaderDb != null ? 1 : 0);
        var s_Built = Regex.Matches(p_Log, "Superbundle successfully built").Count;
        if (s_Built != s_Expected)
            p_Result.Problems.Add($"{s_Built} superbundle(s) built, expected {s_Expected} — read build.log.");

        foreach (var s_Line in p_Log.Split('\n'))
        {
            var s_Trim = s_Line.Trim();
            if (s_Trim.Contains("Command not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Could not find partition", StringComparison.OrdinalIgnoreCase))
                p_Result.Problems.Add("Rime: " + s_Trim);
        }

        if (Regex.IsMatch(p_Log, @"TEX-BAD"))
            p_Result.Problems.Add("check_textures marked a texture TEX-BAD (see build.log).");

        static bool Named(byte[] p_Bytes, string p_Name) =>
            Contains(p_Bytes, Encoding.ASCII.GetBytes(p_Name.ToLowerInvariant())) || Contains(p_Bytes, Encoding.ASCII.GetBytes(p_Name));

        var s_Nc = Path.Combine(p_PackageDir, "packnc.sb");
        var s_NcBytes = File.Exists(s_Nc) ? File.ReadAllBytes(s_Nc) : Array.Empty<byte>();
        var s_Pack = Path.Combine(p_PackageDir, "pack.sb");
        var s_PackBytes = File.Exists(s_Pack) ? File.ReadAllBytes(s_Pack) : Array.Empty<byte>();
        foreach (var s_Texture in p_Textures)
        {
            if (!Regex.IsMatch(p_Log, @"TEX\s+" + Regex.Escape(s_Texture.ToLowerInvariant()) + @"\s+\d+x\d+"))
                p_Result.Problems.Add($"check_textures did not list '{s_Texture}' — the texture has a name but no resource.");
            if (!Named(s_NcBytes, s_Texture))
                p_Result.Problems.Add($"'{s_Texture}' is not in packnc.sb.");
            if (!Named(s_PackBytes, s_Texture))
                p_Result.Problems.Add($"'{s_Texture}' has no partition in pack.sb — nothing could find it by name.");
        }

        // the entries' partitions (their databases, the material variations, the hidden shader's stub), each by name in pack.sb
        foreach (var s_Partition in p_EntryPartitions ?? Array.Empty<string>())
            if (!Named(s_PackBytes, s_Partition))
                p_Result.Problems.Add($"'{s_Partition}' is not in pack.sb — the skin's entries would not load.");

        if (p_Thumb)
        {
            // every picture's pixels (256×64 DXT5 = 16,384 bytes each, raw) in chunks, and every picture BY ITS WHOLE NAME in packnc: the row's
            // name is the start of each part's ("…_KEY" / "…_KEY_upper"), so a substring would find the row's in a part's
            var s_Chunks = Path.Combine(p_PackageDir, "chunks.sb");
            if (!File.Exists(s_Chunks) || new FileInfo(s_Chunks).Length < 16384L * p_Thumbs.Count)
                p_Result.Problems.Add($"chunks.sb is missing or holds less than {p_Thumbs.Count} picture(s)' pixels — a cell would have no picture.");

            var s_Lower = Encoding.ASCII.GetString(s_NcBytes).ToLowerInvariant();
            foreach (var s_Thumb in p_Thumbs)
                if (!Regex.IsMatch(s_Lower, Regex.Escape(s_Thumb.ToLowerInvariant()) + "(?![a-z0-9_])"))
                    p_Result.Problems.Add($"the picture '{s_Thumb}' is not in packnc.sb — its cell would show nothing.");
        }

        if (p_ShaderDb != null)
        {
            // the fresh-named database has to be IN the bundle: a template the command cannot use leaves the stubs only (Audit's rule)
            if (!Regex.IsMatch(p_Log, @"Adding '" + Regex.Escape(p_ShaderDb) + "'", RegexOptions.IgnoreCase))
                p_Result.Problems.Add($"the skin's shader database '{p_ShaderDb}' was not added to the shaders bundle.");

            var s_Shaders = Path.Combine(p_PackageDir, "shaders.sb");
            var s_Bytes = File.Exists(s_Shaders) ? File.ReadAllBytes(s_Shaders) : Array.Empty<byte>();
            if (s_Bytes.Length < 1024)
                p_Result.Problems.Add("shaders.sb is missing or empty — the parts' own shaders would not exist in the game.");
            foreach (var s_Stub in p_Stubs.Where(p_S => !Named(s_Bytes, p_S)))
                p_Result.Problems.Add($"the shader stub '{s_Stub}' is not in shaders.sb — nothing could point a material at it.");
        }
    }

    /// <summary>
    /// What of a copy would NOT carry the graph on his meshes, or null when every deferred GBuffer solution of every declaration the part's
    /// meshes draw the shader with takes the graph's bytecode. Read off the bake's own extraction of the shader (solutions.txt: each solution's
    /// declaration; its pixel shader) against the manifest's patched game bytes — the same pairing the clone command patches by. A solution of
    /// another pass (shadows, depth: fewer than three render targets) keeps the game's bytes by design and is not counted.
    /// </summary>
    private static string? CoverageGap(string p_Extract, SoldierShader p_Lane, RimeShaderEditor.Emit.ShaderBaker.BakeShader p_Compiled)
    {
        if (p_Lane.Declarations.Count == 0)
            return "no vertex declaration of his meshes that wear it is known (the soldiers' cache log names none) — the copy cannot be checked.";

        // one blob and no manifest: the command patches every GBuffer permutation of the mode
        if (p_Compiled.ManifestPath == null)
            return null;

        // the extraction of THAT shader: the extractor writes numbered folders and an index when the name matched several
        var s_Folder = p_Extract;
        var s_Index = Path.Combine(p_Extract, "index.txt");
        if (File.Exists(s_Index) &&
            File.ReadAllLines(s_Index).Select(p_L => p_L.Split('\t')).FirstOrDefault(p_P => p_P.Length >= 2 &&
                p_P[1].Equals(p_Lane.Shader, StringComparison.OrdinalIgnoreCase)) is { } s_Row)
            s_Folder = Path.Combine(p_Extract, s_Row[0]);
        var s_Solutions = Path.Combine(s_Folder, "solutions.txt");
        if (!File.Exists(s_Solutions))
            return $"its extraction has no solutions.txt ('{s_Folder}') — the copy cannot be checked.";

        static string Hash(byte[] p_Bytes) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(p_Bytes));
        var s_Patched = File.ReadAllLines(p_Compiled.ManifestPath)
            .Select(p_L => p_L.Split('|'))
            .Where(p_P => p_P.Length == 2 && File.Exists(p_P[0]))
            .Select(p_P => Hash(File.ReadAllBytes(p_P[0])))
            .ToHashSet(StringComparer.Ordinal);

        var s_Gaps = new List<string>();
        foreach (var s_Decl in p_Lane.Declarations)
        {
            var s_GBuffer = new List<(string Hash, int Inputs)>();
            foreach (var s_Line in File.ReadLines(s_Solutions))
            {
                var s_Match = Regex.Match(s_Line, @"decl=(0x[0-9A-Fa-f]+)");
                if (!s_Match.Success || !s_Match.Groups[1].Value.Equals(s_Decl, StringComparison.OrdinalIgnoreCase))
                    continue;

                var s_Ps = Path.Combine(s_Folder, s_Line.Split(' ')[0] + "_ps.dxbc");
                if (!File.Exists(s_Ps))
                    continue;

                var s_Bytes = File.ReadAllBytes(s_Ps);
                var s_Contract = RimeShaderEditor.Emit.ShaderContract.Detect(s_Bytes);
                if (s_Contract.RenderTargets < 3)
                    continue;

                s_GBuffer.Add((Hash(s_Bytes), s_Contract.Inputs.Count(p_I => p_I.Semantic.Equals("TEXCOORD", StringComparison.OrdinalIgnoreCase))));
            }

            if (s_GBuffer.Count == 0)
            {
                s_Gaps.Add($"{s_Decl}, which the template level's database has no GBuffer solution of");
                continue;
            }

            var s_Missing = s_GBuffer.Where(p_G => !s_Patched.Contains(p_G.Hash)).ToList();
            if (s_Missing.Count > 0)
                s_Gaps.Add($"{s_Decl}, {s_Missing.Count} of whose {s_GBuffer.Count} GBuffer solution(s) keep the game's bytes " +
                           $"({s_Missing.Select(p_G => p_G.Hash).Distinct().Count()} flavour(s) with {string.Join("/", s_Missing.Select(p_G => p_G.Inputs).Distinct())} " +
                           "interpolators, a layout the studio cannot compile a graph into yet)");
        }

        return s_Gaps.Count == 0 ? null : $"the game draws his meshes with it under declaration {string.Join("; ", s_Gaps)}.";
    }

    /// <summary>A level's shader database resource: "levels/xp2_skybar/xp2_skybar/shaderdb".</summary>
    private static string SkinShaderDbOf(string p_Level) => $"levels/{p_Level.ToLowerInvariant()}/{p_Level.ToLowerInvariant()}/shaderdb";

    /// <summary>
    /// The copies of the shaders a skin's parts draw with their OWN graphs (a graph with nodes or wires of its own) — the
    /// camo's own-shader recipe (PrepareShader) on a soldier: each graph compiled against every flavour of the shader it is drawn on, read out of
    /// the database of a level that fields him (only it has solutions for his meshes' declarations); the shader's entry cloned under the copy's
    /// name with that bytecode patched into its deferred GBuffer solutions (every other pass — shadows, depth — is cloned and keeps the game's
    /// own bytes, so nothing is left without a solution); the parameters of the pictures at registers the shader has none at declared on it;
    /// the copies cut into a small database of the package's own. Returns the failure, or null.
    /// </summary>
    /// <param name="p_Dropped">The (part, shader) copies left out because they would not draw the graph on his meshes (said in the log).</param>
    private static string? PrepareSoldierShaders(SoldierBakePlan p_Plan, BakeContext p_Context, List<(string Part, SoldierShader Shader)> p_Lanes,
        Func<int, int, string> p_GuidFor, JsonSerializerOptions p_Json, out string? p_ShaderDb, out List<ShaderStub> p_Stubs,
        out HashSet<(string Part, string Shader)> p_Dropped)
    {
        p_ShaderDb = null;
        p_Stubs = new List<ShaderStub>();
        p_Dropped = new HashSet<(string Part, string Shader)>();
        var s_Log = p_Context.Log;

        if (p_Plan.ShaderLevel.Length == 0)
            return "shader: no level is known for this soldier — nothing to read his shaders' flavours from.";
        if (!RimeShaderEditor.Emit.ShaderCompiler.IsAvailable(p_Context.Fxc))
            return "shader: fxc.exe (the Windows SDK shader compiler) was not found — a skin that ships its own shader needs it.";
        foreach (var (s_Part, s_Lane) in p_Lanes)
            if (!File.Exists(s_Lane.GraphPath))
                return $"shader: the {s_Part} graph '{s_Lane.GraphPath}' does not exist.";

        var s_Work = Path.Combine(p_Context.WorkDir, "shader");
        Directory.CreateDirectory(s_Work);
        var s_Db = SkinShaderDbOf(p_Plan.ShaderLevel);
        s_Log($"  the skin ships its OWN shader for {p_Lanes.Count} graph(s): compiling against the shaders' flavours out of {p_Plan.ShaderLevel} " +
              "(a TEMPLATE only: the skin's shader database is its own and loads on every level) — this mounts the game.");

        var (s_Compiled, _, s_PrepError) = RimeShaderEditor.MainWindow.PrepareAdditionalGraphs(
            p_Lanes.Select(p_L => p_L.Shader.GraphPath).ToList(), p_Context.RimeRepl, p_Context.Fxc, p_Context.GamePath,
            p_Plan.ShaderLevel, Settings.TextureCache, s_Work, s_Log);
        if (s_PrepError != null)
            return "shader: " + s_PrepError;
        if (s_Compiled.Count < p_Lanes.Count)
            return $"shader: {p_Lanes.Count} graph(s) were compiled and {s_Compiled.Count} produced bytecode.";

        // ⛔⛔ A COPY SHIPS ONLY WHERE IT DRAWS THE GRAPH ON HIS MESHES (measured 2026-09-28: Aftermath's torso copy "6 of 20 pixel permutations
        // patched" — and not one of them on the declaration the torso draws with, 0x56576EBB: 0 of its 8 GBuffer solutions; the bake said OK).
        // Each flavour the compiler cannot honour keeps the game's bytes (MainWindow.PrepareAdditionalGraphs: an interpolator layout no family
        // explains — Aftermath's characters carry the vertex colour at TC0 and the world position one row down), so what counts is every
        // GBuffer solution of the declarations his meshes use.
        var s_Covered = new List<int>();
        for (var i = 0; i < p_Lanes.Count; i++)
        {
            var (s_Part, s_Lane) = p_Lanes[i];
            if (CoverageGap(Path.Combine(s_Work, $"extract_{i}"), s_Lane, s_Compiled[i]) is { } s_Gap)
            {
                p_Dropped.Add((s_Part, s_Lane.Shader));
                s_Log($"NOTE: {s_Part}'s graph on '{s_Lane.Shader.Split('/')[^1]}' does NOT ship: {s_Gap} In the game this part keeps the game's " +
                      "shader; its pictures and numbers ship.");
                continue;
            }

            s_Covered.Add(i);
            s_Log($"  shader: {s_Part}'s graph on '{s_Lane.Shader.Split('/')[^1]}' covers every GBuffer solution of the declaration(s) his meshes draw it " +
                  $"with ({string.Join(", ", s_Lane.Declarations)}).");
        }

        if (s_Covered.Count == 0)
            return null;

        var s_Chain = Path.Combine(s_Work, "clones.bin");
        var s_Sliced = Path.Combine(s_Work, $"shaderdb_{p_Plan.Folder}.bin");
        foreach (var s_Stale in new[] { s_Chain, s_Sliced })
            if (File.Exists(s_Stale))
                File.Delete(s_Stale);

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        var s_Names = new List<string>();
        var s_Declared = new List<(string Parameter, int Register)>();
        foreach (var i in s_Covered)
        {
            var (s_Part, s_Lane) = p_Lanes[i];
            var s_Patch = s_Compiled[i].ManifestPath ?? s_Compiled[i].DxbcPath;
            if (!File.Exists(s_Patch))
                return $"shader: the bytecode of the {s_Part} graph ('{s_Patch}') was not written.";

            // ⛔ the FIRST clone of the chain writes the file; only the ones after it read it back (PrepareShader's rule). The mode filter
            // is the deferred GBuffer family, every layout: the manifest patches only the flavours whose game bytes it names, and every
            // other pass of the entry is cloned with its own bytes (ShaderDatabase.CloneShaderEntry)
            var s_Clone = SkinShaderName(p_Plan.Key, s_Part, s_Lane.Shader);
            var s_Tail = s_Names.Count > 0 ? $" \"-\" \"{s_Chain}\"" : "";
            s_Script.AppendLine($"shader_db_clone_entry {s_Db} {s_Lane.Shader} {s_Clone} \"{s_Chain}\" \"{s_Patch}\" DeferredShadingGBuffer{s_Tail}");
            s_Names.Add(s_Clone);

            foreach (var (s_Parameter, s_Register) in s_Lane.Declared.Distinct())
            {
                s_Script.AppendLine($"shader_db_add_external_texture {s_Db} {s_Clone} {s_Parameter} {s_Register} \"{s_Chain}\" \"{s_Chain}\"");
                s_Declared.Add((s_Parameter, s_Register));
            }
        }

        s_Script.AppendLine($"shader_db_slice \"{s_Chain}\" \"{s_Sliced}\" \"{string.Join(",", s_Names)}\"");
        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(s_Work, "clones.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));
        var s_Output = Cache.GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
        File.WriteAllText(Path.Combine(s_Work, "clones.log"), s_Output, new UTF8Encoding(false));

        foreach (var s_Line in s_Output.Split('\n'))
        {
            var s_Trim = s_Line.Trim();
            // (a "[<render path>] ERROR: no shader named …" line is not one: a path may lack the shader — the command's own rule)
            if (s_Trim.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Command not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("CLONE FAILED", StringComparison.OrdinalIgnoreCase) ||
                s_Trim.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
                return "shader: " + s_Trim;
        }

        // ⛔ EVERY copy carries the graph: a copy whose GBuffer solutions took none of the authored bytes (a mode the filter missed, a
        // manifest naming bytes the entry does not hold) draws the game's own logic under the skin's name — it looks like a bake that worked
        foreach (var s_Clone in s_Names)
        {
            // one report per render path that holds the shader
            var s_Reports = Regex.Matches(s_Output, @"cloned '[^']*' -> '" + Regex.Escape(s_Clone) +
                                                    @"'[^\n]*?(\d+) solution\(s\) deep-cloned \((\d+) pixel permutation\(s\), (\d+) patched",
                    RegexOptions.IgnoreCase)
                .Select(p_M => (Solutions: int.Parse(p_M.Groups[1].Value), Pixels: int.Parse(p_M.Groups[2].Value), Patched: int.Parse(p_M.Groups[3].Value)))
                .ToList();
            if (s_Reports.Count == 0)
                return $"shader: no clone report for '{s_Clone}' — read shader\\clones.log.";
            if (s_Reports.All(p_R => p_R.Patched == 0))
                return $"shader: '{s_Clone}' took none of the graph's bytecode (" +
                       string.Join("; ", s_Reports.Select(p_R => $"{p_R.Solutions} solution(s), {p_R.Pixels} pixel permutation(s), 0 patched")) +
                       ") — it would draw the game's own shader; read shader\\clones.log.";
            s_Log($"  shader: '{s_Clone.Split('/')[^1]}' — " +
                  string.Join("; ", s_Reports.Select(p_R => $"{p_R.Solutions} solution(s), {p_R.Patched} of {p_R.Pixels} pixel permutation(s) with the graph's bytecode")) + ".");
        }

        // each picture parameter declared on its copy: one report per declaration naming it at its register (PrepareShader's check)
        foreach (var s_Declaration in s_Declared.Distinct())
        {
            var s_Count = Regex.Matches(s_Output, @"Patched (\d+) pixel ShaderConstant\(s\) and (\d+) shader entr\(ies\) with external '" +
                                                  Regex.Escape(s_Declaration.Parameter) + @"' at t(\d+)")
                .Count(p_M => p_M.Groups[3].Value == s_Declaration.Register.ToString() && int.Parse(p_M.Groups[1].Value) > 0 && int.Parse(p_M.Groups[2].Value) > 0);
            var s_Copies = s_Declared.Count(p_D => p_D == s_Declaration);
            if (s_Count != s_Copies)
                return $"shader: the picture parameter '{s_Declaration.Parameter}' was declared on {s_Count} cop(ies) at t{s_Declaration.Register}, expected " +
                       $"{s_Copies} — read shader\\clones.log.";
        }

        if (!File.Exists(s_Sliced) || new FileInfo(s_Sliced).Length == 0)
            return "shader: the skin's shader database was not written — read shader\\clones.log.";

        var s_Kept = Regex.Matches(s_Output, @"SLICE: (\d+) shader\(s\)").Select(p_M => int.Parse(p_M.Groups[1].Value)).ToList();
        if (s_Kept.Count == 0 || s_Kept.Max() != s_Names.Count)
            return $"shader: the slice kept {string.Join("/", s_Kept)} shader(s) per render path, expected {s_Names.Count} — read shader\\clones.log.";

        p_ShaderDb = s_Sliced;
        for (var s = 0; s < s_Names.Count; s++)
        {
            // their own guid space (0x6000), apart from the textures' (0x5000…) and the row's picture's (0)
            var s_Stub = new ShaderStub(s_Names[s], p_GuidFor(0x6000 + s, 1), p_GuidFor(0x6000 + s, 2));
            WriteShaderStub(Path.Combine(s_Work, $"stub_skin{s}.json"), s_Stub, p_Json);
            p_Stubs.Add(s_Stub);
        }

        s_Log($"  shader: {s_Names.Count} cop(ies) cut into {new FileInfo(s_Sliced).Length / 1024} KB.");
        return null;
    }
}
