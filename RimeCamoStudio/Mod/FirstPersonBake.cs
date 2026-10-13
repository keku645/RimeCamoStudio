using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimeCamoStudio.Cache;
using RimeCamoStudio.Catalog;

namespace RimeCamoStudio.Mod;

/// <summary>One bundle of the framework's first-person split: one arms mesh of one pack, the copies of its variations the looks wear.</summary>
public sealed class FirstPersonGroup
{
    /// <summary>The bundle in the package ("fp0"…), loaded behind <see cref="Levels"/>.</summary>
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";

    /// <summary>"base" or "xp4": the kits whose looks wear these arms.</summary>
    [JsonPropertyName("pack")] public string Pack { get; set; } = "";

    [JsonPropertyName("mesh")] public string Mesh { get; set; } = "";

    /// <summary>The variation database the copies were read from (the catalogue's for that pack).</summary>
    [JsonPropertyName("database")] public string Database { get; set; } = "";

    /// <summary>The material ids of the mesh that are its trousers (hidden in the Torso copy, the only ones shown in the Legs copy).</summary>
    [JsonPropertyName("trousers")] public List<int> Trousers { get; set; } = new();

    /// <summary>The NameHashes of the variations copied (0 = the mesh linked bare, its entry 0): each has its Torso and its Legs copy.</summary>
    [JsonPropertyName("sources")] public List<uint> Sources { get; set; } = new();

    /// <summary>The level bundles that carry the mesh (of that pack's levels): the bundle goes right behind them, as a vehicle's veh0.</summary>
    [JsonPropertyName("levels")] public List<string> Levels { get; set; } = new();
}

/// <summary>The framework's first-person split package, as its description (firstperson.json) and the Lua index say it.</summary>
public sealed class FirstPersonPackage
{
    [JsonPropertyName("superbundle")] public string Superbundle { get; set; } = "";
    [JsonPropertyName("built")] public string Built { get; set; } = "";
    [JsonPropertyName("groups")] public List<FirstPersonGroup> Groups { get; set; } = new();
}

public static partial class CamoBaker
{
    /// <summary>The name of an arms variation's copy — the soldier module's ArmsCopyName, letter for letter ("Torso" / "Legs").</summary>
    public static string ArmsCopyName(uint p_Source, string p_Which) => $"Characters/Soldiers/Skin/Arms1P_{p_Source}_{p_Which}";

    /// <summary>
    /// THE FIRST-PERSON TROUSERS FOLLOW THE LEGS, BAKED (phase 2 of baking the soldiers the way the vehicles are): what is seen of the legs in
    /// first person is a material of the ARMS mesh, and the arms come with the TORSO piece. The soldier module splits each look's arms in
    /// two variations of its own — "Torso" (the arms without the trousers) and "Legs" (only the trousers) — named after the source
    /// variation's NameHash; until now it cloned their entries into every variation database as it loaded, which killed the MP_007 server
    /// (references not loaded yet) and could not be moved to the build (the client indexes the databases before it). Here they are baked
    /// once for the whole framework: per pack and arms mesh, a bundle with a copy of each variation the catalogue's looks wear (entry 0 for
    /// the arms linked bare), under both copy names, the other half given the hidden material variation (a shader with no solution: not
    /// drawn — in first person a hidden material no other copy paints is a black hole, so the two always go together). Each partition owns
    /// ONLY its entry (one key, one partition). The loader puts each bundle right behind the level bundles that carry its mesh; the soldier
    /// module splits only the looks whose arms have copies here, in a bundle that came in on the level.
    /// </summary>
    public static BakeResult BuildFirstPerson(BakeContext p_Context) =>
        KeepingThePackageThatWorked(FrameworkMod.FirstPersonDir(p_Context.ModFolder), p_Context, () => BuildFirstPersonInPlace(p_Context));

    /// <summary>The split's build itself, straight into its folder — <see cref="BuildFirstPerson"/> keeps the one that worked around it.</summary>
    private static BakeResult BuildFirstPersonInPlace(BakeContext p_Context)
    {
        var s_Result = new BakeResult();
        var s_Log = p_Context.Log;
        var s_Json = new JsonSerializerOptions { WriteIndented = true };

        var s_Dir = FrameworkMod.FirstPersonDir(p_Context.ModFolder);
        s_Result.PackageDir = s_Dir;
        Directory.CreateDirectory(s_Dir);
        Directory.CreateDirectory(p_Context.WorkDir);
        // ⛔ nothing of an older build survives into the new one
        foreach (var s_Stale in Directory.GetFiles(s_Dir))
            File.Delete(s_Stale);

        var s_Seed = Fnv1A("firstperson:framework");
        string GuidFor(int p_Group, int p_Slot) => $"{s_Seed:x8}-{p_Group:x4}-4000-a000-{p_Slot:x12}";

        // --- what the looks wear: per pack and arms mesh, the variations of the catalogue's looks (0 = the mesh linked bare) ---------------
        var s_Catalog = SoldierCatalog.Load();
        var s_Scan = GameCache.DiscoverSoldierBindings(s_Catalog, p_Context.GamePath, p_Context.RimeRepl, s_Log);
        Dictionary<string, List<string>>? s_Bundles = null;
        if (File.Exists(Program.SoldierBundlesPath))
            s_Bundles = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(Program.SoldierBundlesPath));
        if (s_Bundles == null)
        {
            s_Result.Problems.Add($"{Program.SoldierBundlesPath} is missing: where the arms meshes are loaded is unknown (RimeCamoStudio --soldierbundles).");
            s_Log("BAKE FAILED — " + s_Result.Problems[^1]);
            return s_Result;
        }

        var s_Wanted = new SortedDictionary<(string Pack, string Mesh), (string Database, SortedSet<uint> Sources)>();
        foreach (var s_Soldier in s_Catalog.Soldiers)
        foreach (var s_Item in s_Soldier.Looks.SelectMany(p_L => p_L.Items).Where(p_I => p_I.Role == "arms1p"))
        {
            var s_Key = (s_Soldier.Expansion.ToLowerInvariant(), s_Item.Mesh.ToLowerInvariant());
            if (!s_Wanted.TryGetValue(s_Key, out var s_Had))
                s_Wanted[s_Key] = s_Had = (s_Soldier.Database, new SortedSet<uint>());
            else if (!s_Had.Database.Equals(s_Soldier.Database, StringComparison.OrdinalIgnoreCase))
                s_Log($"  note: {s_Item.Mesh.Split('/')[^1]} ({s_Key.Item1}) is read from {s_Had.Database}, not {s_Soldier.Database} as {s_Soldier.Display} names.");
            s_Had.Sources.Add(s_Item.Variation.Length > 0 ? s_Item.NameHash : 0);
        }

        var s_Package = new FirstPersonPackage { Superbundle = FrameworkMod.FirstPersonStem, Built = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        var s_Copies = new List<(FirstPersonGroup Group, string Db, string Mesh, uint Source, uint Hash, string Which, List<int> Hidden, int Materials)>();
        var s_Groups = new List<(FirstPersonGroup Group, string StubPath, string MaterialsPath, ShaderStub Stub, string MaterialsName, string MaterialsPartition, string Hidden)>();

        foreach (var ((s_Pack, s_Mesh), (s_Db, s_Sources)) in s_Wanted)
        {
            var s_Leaf = s_Mesh.Split('/')[^1];
            // the trousers: the materials that, in any variation of the mesh, bind a diffuse of the lower body's (by MESH: the material list is
            // the same in all of them — CamoSession.TrousersMaterialsOf)
            var s_Trousers = s_Scan.Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.MaterialId >= 0 &&
                                                 p_B.Textures.Any(p_T => p_T.Param.Equals("Diffuse", StringComparison.OrdinalIgnoreCase) &&
                                                                         p_T.Texture.Contains("/lowerbody/", StringComparison.OrdinalIgnoreCase)))
                .Select(p_B => p_B.MaterialId).ToHashSet();
            if (s_Trousers.Count == 0)
            {
                s_Result.Problems.Add($"{s_Leaf} ({s_Pack}): the soldier scan finds no trousers material on it — refresh the soldier scan (Reload from game).");
                continue;
            }

            var s_Levels = (s_Bundles.GetValueOrDefault(s_Mesh) ?? new List<string>())
                .Where(p_B => p_B.StartsWith("levels/xp4_", StringComparison.OrdinalIgnoreCase) == (s_Pack == "xp4"))
                .OrderBy(p_B => p_B, StringComparer.Ordinal).ToList();
            if (s_Levels.Count == 0)
            {
                s_Result.Problems.Add($"{s_Leaf} ({s_Pack}): no level bundle of that pack carries it — its copies could load nowhere.");
                continue;
            }

            var j = s_Groups.Count;
            var s_Group = new FirstPersonGroup
            {
                Bundle = $"fp{j}", Pack = s_Pack, Mesh = s_Mesh, Database = s_Db, Trousers = s_Trousers.OrderBy(p_I => p_I).ToList(), Levels = s_Levels,
            };

            var s_Skipped = new List<uint>();
            foreach (var s_Source in s_Sources)
            {
                var s_Ids = s_Scan.Where(p_B => p_B.Mesh.Equals(s_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == s_Source && p_B.MaterialId >= 0)
                    .Select(p_B => p_B.MaterialId).ToHashSet();
                if (s_Ids.Count == 0 || !s_Ids.Overlaps(s_Trousers) || s_Ids.All(s_Trousers.Contains))
                {
                    s_Skipped.Add(s_Source);
                    continue;
                }

                s_Group.Sources.Add(s_Source);
                foreach (var s_Which in new[] { "Torso", "Legs" })
                {
                    var s_Hidden = s_Ids.Where(p_Id => s_Trousers.Contains(p_Id) == (s_Which == "Torso")).OrderBy(p_Id => p_Id).ToList();
                    s_Copies.Add((s_Group, s_Db, s_Mesh, s_Source, VariationHashOf(ArmsCopyName(s_Source, s_Which)), s_Which, s_Hidden, s_Ids.Count));
                }
            }

            if (s_Skipped.Count > 0)
                s_Log($"  {s_Leaf} ({s_Pack}): {s_Skipped.Count} variation(s) the scan has no materials (or no trousers) for — not split: " +
                      string.Join(", ", s_Skipped));

            var s_Stub = new ShaderStub($"Characters/Custom/FirstPerson_Hidden_{j}", GuidFor(0x7010 + j, 1), GuidFor(0x7010 + j, 2));
            s_Groups.Add((s_Group, Path.Combine(p_Context.WorkDir, $"fp{j}_hidden.json"), Path.Combine(p_Context.WorkDir, $"fp{j}_materials.json"), s_Stub,
                $"Characters/Custom/FirstPerson_Materials_{j}", GuidFor(0x7020 + j, 1), GuidFor(0x7020 + j, 2)));
            s_Package.Groups.Add(s_Group);
            s_Log($"  {s_Group.Bundle}: {s_Leaf} ({s_Pack}) — {s_Group.Sources.Count} variation(s) -> {s_Group.Sources.Count * 2} copies, trousers " +
                  $"#{string.Join(",#", s_Group.Trousers)}, from {s_Db}, behind {s_Levels.Count} level bundle(s).");
        }

        if (s_Result.Problems.Count > 0 || s_Copies.Count == 0)
        {
            if (s_Result.Problems.Count == 0)
                s_Result.Problems.Add("no first-person arms in the catalogue's looks — nothing to split.");
            s_Log("BAKE FAILED — " + string.Join(" | ", s_Result.Problems));
            return s_Result;
        }

        // --- the Rime script: one CAS superbundle, a bundle per group (its hidden shader's stub, its hidden material variation, its copies) --
        foreach (var s_G in s_Groups)
        {
            WriteShaderStub(s_G.StubPath, s_G.Stub, s_Json);
            WriteSkinMaterials(s_G.MaterialsPath, s_G.MaterialsName, s_G.MaterialsPartition, s_G.Hidden, s_G.Stub, new List<Dictionary<string, object>>(), s_Json);
        }

        var s_Sb = Path.Combine(p_Context.ModFolder, "sb");
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_Context.GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine($"build_sb Win32/{FrameworkMod.FirstPersonStem}/pack Frostbite2_0 \"{s_Sb}\" true");
        var s_Names = new List<string>();
        for (var g = 0; g < s_Groups.Count; g++)
        {
            var s_G = s_Groups[g];
            s_Script.AppendLine($"build_bundle win32/{FrameworkMod.FirstPersonStem}/{s_G.Group.Bundle}");
            s_Script.AppendLine($"add_json_partition \"{s_G.Stub.Name}\" \"{s_G.StubPath}\"");
            s_Script.AppendLine($"add_json_partition \"{s_G.MaterialsName}\" \"{s_G.MaterialsPath}\"");
            s_Names.Add(s_G.Stub.Name);
            s_Names.Add(s_G.MaterialsName);

            var i = 0;
            foreach (var s_Copy in s_Copies.Where(p_C => p_C.Group == s_G.Group))
            {
                var s_Name = $"MV_FIRSTPERSON_{s_G.Group.Bundle.ToUpperInvariant()}_{i}";
                var s_Refs = $"{s_G.MaterialsPartition}:{string.Join(",", s_Copy.Hidden.Select(p_Id => $"{s_G.Hidden}@*#{p_Id}"))}";
                // 'all' reads every entry of the mesh (the source is one of them); OnlyVariation keeps ONLY ours in the partition; no texture
                // binding (the copy wears the stock ones), the textures referenced as the weapons' are
                s_Script.AppendLine($"mvdb_add_entry {s_Copy.Db} {s_Name} 1 {s_Copy.Mesh} all \"\" \"\" \"\" \"\" " +
                                    $"{GuidFor(0x7100 + g, 0x100 + i * 2)} {GuidFor(0x7100 + g, 0x101 + i * 2)} \"\" " +
                                    $"{s_Copy.Hash} \"{s_Refs}\" \"\" \"\" \"\" \"\" \"\" 1 1 {s_Copy.Source}");
                s_Names.Add(s_Name);
                i++;
            }

            s_Script.AppendLine("resolve_resource_dependencies 1");
            s_Script.AppendLine("resolve_missing_chunks 1");
            s_Script.AppendLine("build");
        }

        s_Script.AppendLine("build");
        var s_ScriptPath = Path.Combine(p_Context.WorkDir, "build.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString(), new UTF8Encoding(false));
        s_Log($"Building the first-person split: {s_Groups.Count} bundle(s), {s_Copies.Count} copies — this mounts the game.");

        var s_Out = GameCache.Run(p_Context.RimeRepl, s_ScriptPath);
        s_Result.LogPath = Path.Combine(p_Context.WorkDir, "build.log");
        File.WriteAllText(s_Result.LogPath, s_Out, new UTF8Encoding(false));

        // --- the audit: one superbundle; per copy its source read, its hash, ONLY its entry, the materials it hides; every name in pack.sb --
        var s_Built = Regex.Matches(s_Out, "Superbundle successfully built").Count;
        if (s_Built != 1)
            s_Result.Problems.Add($"{s_Built} superbundle(s) built, expected 1 — read build.log.");
        foreach (var s_Line in s_Out.Split('\n').Select(p_L => p_L.Trim()))
            if (s_Line.Contains("Command not found", StringComparison.OrdinalIgnoreCase) || s_Line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) ||
                s_Line.Contains("Could not find partition", StringComparison.OrdinalIgnoreCase) || s_Line.Contains("VARIATION FAILED") ||
                s_Line.Contains("VARIATION WARNING") || s_Line.Contains("No entry of ") || s_Line.Contains("No MVDB entry found") ||
                s_Line.StartsWith("OnlyVariation: expected", StringComparison.Ordinal) || s_Line.StartsWith("OnlyVariation needs", StringComparison.Ordinal))
                s_Result.Problems.Add("Rime: " + s_Line);

        foreach (var s_Copy in s_Copies)
        {
            var s_What = $"{s_Copy.Mesh.Split('/')[^1]} {s_Copy.Source} {s_Copy.Which}";
            if (!s_Out.Contains($"MVDB-SOURCE-ENTRY: {s_Copy.Mesh} hash {s_Copy.Source} ", StringComparison.OrdinalIgnoreCase) ||
                !s_Out.Contains($"-> new hash {s_Copy.Hash}", StringComparison.Ordinal))
                s_Result.Problems.Add($"{s_What}: no copy of that variation under hash {s_Copy.Hash} in the build log.");
            if (!s_Out.Contains($"the partition owns only hash {s_Copy.Hash}.", StringComparison.Ordinal) ||
                !s_Out.Contains($"with 1 '{s_Copy.Mesh}' entr(ies) (variationAssetNameHash={s_Copy.Hash},", StringComparison.OrdinalIgnoreCase))
                s_Result.Problems.Add($"{s_What}: its partition does not hold ONLY hash {s_Copy.Hash}.");
            if (!s_Out.Contains($"Variation: hash={s_Copy.Hash}, {s_Copy.Hidden.Count}/{s_Copy.Materials} material(s) repointed", StringComparison.Ordinal))
                s_Result.Problems.Add($"{s_What}: not {s_Copy.Hidden.Count} of its {s_Copy.Materials} materials hidden (#{string.Join(",#", s_Copy.Hidden)}).");
        }

        var s_PackFile = Path.Combine(s_Dir, "pack.sb");
        var s_PackText = File.Exists(s_PackFile) ? Encoding.ASCII.GetString(File.ReadAllBytes(s_PackFile)) : "";
        foreach (var s_Name in s_Names.Where(p_N => !s_PackText.Contains(p_N, StringComparison.OrdinalIgnoreCase)))
            s_Result.Problems.Add($"'{s_Name}' is not in pack.sb.");

        if (!s_Result.Ok)
        {
            s_Log($"BAKE FAILED — {s_Result.Problems.Count} problem(s); the first-person split was NOT registered:");
            foreach (var s_Problem in s_Result.Problems.Take(20))
                s_Log("  " + s_Problem);
            return s_Result;
        }

        // --- the description (what Register writes the Lua index from), then the registration --------------------------------------------
        File.WriteAllText(Path.Combine(s_Dir, FrameworkMod.FirstPersonDescription), JsonSerializer.Serialize(s_Package, s_Json), new UTF8Encoding(false));
        var s_Registered = FrameworkMod.Register(p_Context.ModFolder, s_Log);
        s_Result.Summary = $"First-person split: {s_Groups.Count} bundle(s), {s_Copies.Count} copies ({string.Join(", ", s_Groups.Select(p_G => $"{p_G.Group.Bundle} " +
            $"{p_G.Group.Mesh.Split('/')[^1]} {p_G.Group.Pack} {p_G.Group.Sources.Count}×2"))}), pack.sb {new FileInfo(s_PackFile).Length / 1024} KB. {s_Registered}";
        s_Log("BAKE OK — " + s_Result.Summary);
        return s_Result;
    }
}
