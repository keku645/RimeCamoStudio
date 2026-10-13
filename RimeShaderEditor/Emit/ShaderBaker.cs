using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimeShaderEditor.View;

namespace RimeShaderEditor.Emit;

/// <summary>
/// Turns a compiled pixel shader into an installable VU mod: patches each chosen level's shader database, builds
/// one superbundle per level, and writes the mod.json plus the Lua that mounts and prepends them.
///
/// Every trap this pipeline has cost a wasted game boot is checked HERE rather than written in a readme, because
/// a readme is where instructions go to be got wrong:
///   - the superbundle name needs the Win32/ prefix or the level hangs in "creating level" with no error;
///   - build_sb defaults to NONCAS and the engine will not read it, so cas must be passed;
///   - select_game is mandatory after mount_game or every Game command answers "Command not found" with exit 0;
///   - the two phases must be separate processes, with the intermediate database verified between them;
///   - "Superbundle successfully built!" and exit 0 prove nothing - only the artifacts do.
/// </summary>
public static class ShaderBaker
{
    private const string c_Mode = "ShaderRenderMode_DeferredShadingGBufferLayout0";
    private const string c_ForwardMode = "ShaderRenderMode_Default";

    /// <summary>
    /// The pass the surgery should patch, read from the authored bytecode itself. ⛔ NOT by target count:
    /// "one or two targets = forward" misfiled the vegetation-alpha preset, whose GBuffer solutions write
    /// a REDUCED two-target layout yet live in the GBuffer pass. The measured discriminator is the
    /// outdoor-light constant block — the forward (Default) solutions compile it in by name, and no
    /// GBuffer solution in the 24-preset census carries it. No bytecode (a textures-only variation)
    /// keeps the GBuffer default; the mode filter only decides which permutations receive bytecode,
    /// and there is none.
    /// </summary>
    private static string ModeFor(string? p_DxbcPath)
    {
        try
        {
            if (p_DxbcPath != null && File.Exists(p_DxbcPath) &&
                ShaderContract.Detect(File.ReadAllBytes(p_DxbcPath)).ConstantFields
                    .Any(p_F => p_F.Name.Equals("outdoorLightHemisphereDir", StringComparison.OrdinalIgnoreCase)))
                return c_ForwardMode;
        }
        catch
        {
            // An unreadable dxbc fails loudly later, in the surgery itself; the mode guess stays GBuffer.
        }

        return c_Mode;
    }

    public sealed class LevelOutcome
    {
        public string Level { get; init; } = "";
        public int Permutations { get; set; }
        public long ShaderDbBytes { get; set; }
        public long SuperBundleBytes { get; set; }
        public long TocBytes { get; set; }
        public string? Failure { get; set; }
        public bool Ok => Failure == null;
    }

    public sealed class Result
    {
        public List<LevelOutcome> Levels { get; } = new();
        public string ModFolder { get; set; } = "";
        public bool Ok => Levels.Any(p_L => p_L.Ok) && Levels.All(p_L => p_L.Failure == null);
        public bool Partial => Levels.Any(p_L => p_L.Ok) && Levels.Any(p_L => !p_L.Ok);
    }

    /// <summary>
    /// Runs the whole bake. <paramref name="p_Log"/> is called from the calling thread's point of view only -
    /// the caller is responsible for marshalling it to the UI.
    /// </summary>
    /// <summary>One authored shader going into the mod: the game shader it replaces (or, with a variation
    /// spec, the shader whose entry is CLONED under the variation's sibling name) and its compiled DXBC.</summary>
    /// <summary>
    /// One graph on its way into a bake. <paramref name="ManifestPath"/> is the per-variant patch list, and
    /// a replacement needs it as much as a variation does: a shader name owns several pixel flavours with
    /// different contracts, and one compilation written into all of them renders the object BLACK.
    /// </summary>
    internal sealed record BakeShader(string Target, string DxbcPath, VariationSpec? Variation = null,
        string? ManifestPath = null);

    internal static Result Bake(BakeRequest p_Request, IReadOnlyList<BakeShader> p_Shaders,
        string p_GamePath, string p_RimeRepl, string p_WorkDir, Action<string> p_Log,
        IReadOnlyList<MainWindow.CustomBakeTexture>? p_CustomTextures = null)
    {
        var s_Result = new Result();
        var s_ModFolder = Path.Combine(p_Request.OutputFolder, p_Request.ModName);
        var s_SbRoot = Path.Combine(s_ModFolder, "sb");
        s_Result.ModFolder = s_ModFolder;

        Directory.CreateDirectory(s_SbRoot);
        Directory.CreateDirectory(p_WorkDir);

        foreach (var s_Level in p_Request.Levels)
            s_Result.Levels.Add(new LevelOutcome { Level = s_Level });

        // ⛔ A TAKE-OVER AND AN ORDINARY VARIATION CANNOT SHARE A MOD, AND THIS ONE IS A CRASH. They ride in
        // the SAME variation bundle, and a take-over puts that bundle before the level's own. A take-over's
        // entry survives being that early because it was repointed onto the stand-in mesh partition travelling
        // with it; an ordinary variation has no stand-in — its entry references the level's own mesh, which at
        // that moment is not loaded. The registrar dereferences it anyway: null mesh, client dead on the load
        // thread, no minidump (measured). Refused here rather than shipped.
        //
        // (A replacement mixes fine: it has no entry at all, only a database, and databases have their own
        // bundle on each side of the level's.)
        if (p_Shaders.Any(p_S => p_S.Variation is { ApplyToLevel: true }) &&
            p_Shaders.Any(p_S => p_S.Variation is { ApplyToLevel: false }))
        {
            var s_Clash = "this bake mixes a variation that takes over the level's objects with one meant to " +
                          "be placed by hand: " +
                          string.Join(", ", p_Shaders.Where(p_S => p_S.Variation is { ApplyToLevel: true })
                              .Select(p_S => $"'{p_S.Target}' (take-over)")) + " and " +
                          string.Join(", ", p_Shaders.Where(p_S => p_S.Variation is { ApplyToLevel: false })
                              .Select(p_S => $"'{p_S.Target}' (placed by hand)")) +
                          ". They share one bundle, and a take-over makes it load BEFORE the level's own — " +
                          "where the hand-placed one's entry points at a mesh that is not loaded yet, which " +
                          "kills the client as the level starts. Bake them as two mods, or tick \"use this on " +
                          "the objects already in the level\" on both.";

            foreach (var s_Outcome in s_Result.Levels)
                s_Outcome.Failure = s_Clash;

            p_Log($"REFUSED - {s_Clash}");
            return s_Result;
        }

        // ── Phase 1: one mount, one patched database per level (all shaders CHAINED into it) ──────────
        // The first replacement reads the mounted database; every further one reads the intermediate .bin it
        // is about to overwrite — that is what stacks several shaders into ONE database per level.
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");

        // Brand-new texture slots ride the same chained bin: after the bytecode replacements, each adds a
        // (register <- name) TextureConstant to its target shader's constants — the engine then loads and
        // binds the texture natively, which is what makes a texture the material never had deliverable.
        var s_NewSlots = (p_CustomTextures ?? Array.Empty<MainWindow.CustomBakeTexture>())
            .Where(p_T => p_T.SlotTarget != null)
            .ToList();

        // The keys this bake carries in its own database: the clone for a variation, the vanilla name itself
        // for a replacement.
        //
        // ⛔⛔ A REPLACEMENT *IS* DELIVERABLE ADDITIVELY, AND FOR A LONG TIME THIS SAID OTHERWISE. Measured
        // in the engine: databases are appended (addDatabase pushes to the end of the vector) and a shader
        // lookup walks them BACKWARDS — from the last registered to the first — taking the first hit and
        // keeping it (`if (v18 == 0) { ...copy...; v18 = 1; }`), while the per-state solutions go into a
        // unique-keyed map, so the last database registered wins those too. So the ORIGINAL DOES NOT WIN:
        // whoever registered LAST does. The belief that it did is what forced every take-over down the
        // variation route, and a variation cannot reach a copy that belongs to the level's static model
        // group — those never consult a mesh-variation database, and renaming their shader graph does not
        // move them either (both measured in game).
        //
        // What makes it work is delivery ORDER — and the two kinds of key need OPPOSITE ones, which is why
        // this mod ships up to TWO databases instead of one:
        //
        //   EARLY (clone names, from variations): a name nothing else defines, so it has nobody to outrank.
        //     What it does have is a variation database that resolves each material's shader BY NAME the
        //     moment it is ingested, and that happens as the variation bundle loads — so this one has to be
        //     registered BEFORE it. Registered late, the name does not exist yet and the objects are not
        //     drawn at all (measured: 24 grouped copies went invisible).
        //   LATE (vanilla names, from replacements): the level's own database answers for these too, and the
        //     LAST registered wins, so this one goes AFTER the level's.
        //
        // One database cannot be in both places, which is why a mod carrying both used to be refused. Split
        // in two, each rides where its own kind needs to be and a mod can hold both.
        var s_EarlyKeys = p_Shaders.Where(p_S => p_S.Variation != null)
            .Select(p_S => p_S.Variation!.CloneShader)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_LateKeys = p_Shaders.Where(p_S => p_S.Variation == null)
            .Select(p_S => p_S.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var s_Outcome in s_Result.Levels)
        {
            var s_Db = LevelScanner.ShaderDbFor(s_Outcome.Level);
            var s_Bin = DbPath(p_WorkDir, s_Outcome.Level);
            if (File.Exists(s_Bin))
                File.Delete(s_Bin);

            for (var i = 0; i < p_Shaders.Count; i++)
            {
                var s_Chain = i == 0 ? "" : $" \"{s_Bin}\"";

                // A variation clones the target's entry under the sibling name instead of replacing it —
                // the vanilla entry stays untouched. "-" keeps the game's own bytecode (textures/params-only
                // variation); a path patches the authored pixel shader into the cloned permutations.
                // The clone command takes an OPTIONAL reference dxbc BEFORE the chained input file: the "-"
                // placeholder is load-bearing, because a bin path landing in the reference slot makes the
                // command silently re-read the MOUNTED database and every earlier clone in the chain is lost.
                if (p_Shaders[i].Variation is { } s_Spec)
                    s_Script.AppendLine(
                        $"shader_db_clone_entry {s_Db} {p_Shaders[i].Target} {s_Spec.CloneShader} " +
                        $"\"{s_Bin}\" \"{s_Spec.ManifestPath ?? s_Spec.DxbcPath ?? "-"}\" " +
                        ModeFor(s_Spec.DxbcPath ?? p_Shaders[i].DxbcPath) +
                        (i == 0 ? "" : $" \"-\" \"{s_Bin}\""));
                else
                    // The manifest when there is one: every pixel flavour takes bytecode compiled against
                    // ITS OWN contract. Handing the same blob to all of them is what turned the map's
                    // barriers black — correct delivery, wrong bytes in every flavour but one.
                    s_Script.AppendLine(
                        $"replace_shader_bytecode {s_Db} {p_Shaders[i].Target} " +
                        $"\"{p_Shaders[i].ManifestPath ?? p_Shaders[i].DxbcPath}\" " +
                        $"\"{s_Bin}\" {ModeFor(p_Shaders[i].DxbcPath)}" + s_Chain);
            }

            foreach (var s_Slot in s_NewSlots)
                s_Script.AppendLine(
                    $"shader_db_add_texture {s_Db} {s_Slot.SlotTarget} \"{s_Slot.Name}\" {s_Slot.SlotRegister} " +
                    $"\"{s_Bin}\" \"{s_Bin}\"");

            // Additive delivery: cut the mod's OWN keys out of the patched database, so what ships is a few
            // hundred KB of our own entries instead of a 27,9 MB copy of the level's. Done HERE, in phase 1,
            // because the slice is a GAME-context command and phase 2 has already entered bundle building.
            // ⛔ TWO CUTS, NOT ONE. The clone names and the vanilla names have to register on opposite sides
            // of the level's own database, so they cannot share a resource: each kind is cut into its own.
            if (p_Request.AdditiveDb && s_EarlyKeys.Count > 0)
                s_Script.AppendLine($"shader_db_slice \"{s_Bin}\" " +
                                    $"\"{SlicedDbPath(p_WorkDir, s_Outcome.Level, true)}\" " +
                                    $"\"{string.Join(",", s_EarlyKeys)}\"");

            if (p_Request.AdditiveDb && s_LateKeys.Count > 0)
                s_Script.AppendLine($"shader_db_slice \"{s_Bin}\" " +
                                    $"\"{SlicedDbPath(p_WorkDir, s_Outcome.Level, false)}\" " +
                                    $"\"{string.Join(",", s_LateKeys)}\"");
        }

        // A variation that takes over the level's own copies needs a stand-in for the mesh partition, and that
        // can only be built from a dump of the real one — so phase 1, which already has the game mounted,
        // dumps it and the stand-in is written from it before phase 2 builds the bundle.
        var s_MeshDumps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Spec in p_Shaders.Select(p_S => p_S.Variation).OfType<VariationSpec>()
                     .Where(p_V => p_V.ApplyToLevel && p_V.Mesh.Length > 0))
        {
            if (s_MeshDumps.ContainsKey(s_Spec.Mesh))
                continue;

            var s_Dump = Path.Combine(p_WorkDir, $"mesh_real_{s_MeshDumps.Count}.json");
            s_MeshDumps[s_Spec.Mesh] = s_Dump;
            if (File.Exists(s_Dump))
                File.Delete(s_Dump);

            s_Script.AppendLine($"dump_partition_json \"{s_Spec.Mesh}\" \"{s_Dump}\"");
        }

        // The vanilla surface shader's own partition, copied into the clone's so the clone carries that
        // shader's own fields rather than the shape's defaults.
        var s_ShaderDumps = new Dictionary<VariationSpec, string>();
        foreach (var s_Spec in p_Shaders.Select(p_S => p_S.Variation).OfType<VariationSpec>()
                     .Where(p_V => p_V.ApplyToLevel && p_V.TargetShader.Length > 0))
        {
            var s_Dump = Path.Combine(p_WorkDir, $"shader_vanilla_{s_ShaderDumps.Count}.json");
            s_ShaderDumps[s_Spec] = s_Dump;
            if (File.Exists(s_Dump))
                File.Delete(s_Dump);

            s_Script.AppendLine($"dump_partition_json \"{s_Spec.TargetShader}\" \"{s_Dump}\"");
        }

        // What the level's own entry BINDS per material. Those texture references travel with the copied
        // entry, and a take-over bundle loads before the one they live in — so the bake has to know their
        // names to carry them along. Scoped to the variation's own mesh, which turns a whole-level scan into
        // one partition read.
        foreach (var s_Outcome in s_Result.Levels)
        {
            var s_LevelMvdb = LevelScanner.ShaderDbFor(s_Outcome.Level)
                .Replace("/shaderdb", "/meshvariationdb_win32", StringComparison.OrdinalIgnoreCase);

            foreach (var s_Spec in p_Shaders.Select(p_S => p_S.Variation).OfType<VariationSpec>()
                         .Where(p_V => p_V.ApplyToLevel && p_V.Mesh.Length > 0 && p_V.TargetShader.Length > 0))
                s_Script.AppendLine($"dump_shader_material_textures {s_LevelMvdb} {s_Spec.TargetShader} " +
                                    s_Spec.Mesh.Split('/')[^1]);

            // ⛔ AND HOW WIDE A REPLACEMENT REACHES, WHICH IS THE ONE THING THE AUTHOR CANNOT SEE. Replacing a
            // shader answers for its NAME, so every mesh wearing that name changes — one object for an
            // exclusive shader, half the map for a shared preset (measured: ~150 meshes wear one). Unfiltered
            // on purpose: the count IS the question. It costs seconds on an already-mounted game and it is
            // the difference between a bake that does what was meant and one that repaints a level.
            // ⚠ AND OVER THE LEVEL ROOT, NOT ONE DATABASE. A level keeps one per sublevel and a mesh listed in
            // a gamemode's own database renders just the same, so counting in the base one alone under-reports
            // — and it reported a DIFFERENT number from the one the editor shows next to the checkbox, which
            // is worse than either: two answers to the same question.
            var s_LevelRoot = s_LevelMvdb[..s_LevelMvdb.IndexOf('/', s_LevelMvdb.IndexOf('/') + 1)];

            foreach (var s_Target in p_Shaders.Where(p_S => p_S.Variation == null).Select(p_S => p_S.Target)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                s_Script.AppendLine($"dump_shader_material_textures {s_LevelRoot} {s_Target}");
        }

        // The level's own partition, to read how it PLACES the mesh (see VariationPlan.InspectLevelUse).
        var s_LevelDumps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s_MeshDumps.Count > 0)
            foreach (var s_Outcome in s_Result.Levels)
            {
                var s_Partition = LevelScanner.ShaderDbFor(s_Outcome.Level)
                    .Replace("/shaderdb", "", StringComparison.OrdinalIgnoreCase);
                var s_LevelDump = Path.Combine(p_WorkDir, $"level_{s_Outcome.Level.ToLowerInvariant()}.json");

                s_LevelDumps[s_Outcome.Level] = s_LevelDump;
                if (File.Exists(s_LevelDump))
                    File.Delete(s_LevelDump);

                s_Script.AppendLine($"dump_partition_json \"{s_Partition}\" \"{s_LevelDump}\"");
            }

        s_Script.AppendLine("exit");

        p_Log($"Phase 1: patching {p_Shaders.Count} shader(s) into {s_Result.Levels.Count} database(s). " +
              "Mounting the game, this takes a few minutes...");
        var s_Phase1 = RunScript(p_RimeRepl, p_WorkDir, "bake_patch.rime", s_Script.ToString());

        foreach (var s_Spec in p_Shaders.Select(p_S => p_S.Variation).OfType<VariationSpec>()
                     .Where(p_V => p_V.ApplyToLevel && p_V.Mesh.Length > 0))
        {
            // Degrading to an ordinary variation beats shipping a bundle that loads FIRST without the
            // stand-in that makes loading first survivable.
            if (!s_MeshDumps.TryGetValue(s_Spec.Mesh, out var s_Dump) || !File.Exists(s_Dump))
            {
                p_Log($"  WARNING: '{s_Spec.Mesh}' could not be dumped; this variation cannot take over the " +
                      "level's own copies and will ship as an ordinary variation.");
                s_Spec.ApplyToLevel = false;
                continue;
            }

            if (!VariationPlan.WriteMeshStub(s_Spec, s_Dump, out var s_StubError))
            {
                p_Log($"  WARNING: no stand-in for '{s_Spec.Mesh}' ({s_StubError}); shipping as an ordinary " +
                      "variation.");
                s_Spec.ApplyToLevel = false;
                continue;
            }

            p_Log($"  stand-in for '{s_Spec.Mesh}': partition {s_Spec.MeshStubPartGuid} " +
                  $"as '{s_Spec.MeshStubName}' (real partition {s_Spec.MeshRealPartGuid})");

            // The clone's own shader partition, rewritten from the vanilla one so its fields are that
            // shader's rather than the shape's defaults. A failure here is not fatal: Build already wrote a
            // correct partition for this variation, and the guard below checks the only field that decides
            // whether anything draws.
            if (s_ShaderDumps.TryGetValue(s_Spec, out var s_StubSource) && File.Exists(s_StubSource) &&
                !VariationPlan.WriteShaderStubFrom(s_Spec, s_StubSource, out var s_CopyError))
                p_Log($"  note: '{s_Spec.TargetShader}' could not be copied into the clone's partition " +
                      $"({s_CopyError}); the authored one is used instead.");

            // ⛔ RENAMING THE VANILLA SHADER ASSET WAS TRIED HERE AND IS GONE. It travelled with the SHADER,
            // so every mesh wearing it followed — measured on a shipped map, one shared preset is worn by
            // ~150 meshes — and it was only ever introduced because copies inside a level's static model
            // group were believed unreachable per mesh. They are not: the per-mesh variation reaches them
            // (confirmed in game), and it is the only route that can be AIMED at one object.
            p_Log($"  '{s_Spec.TargetShader}' keeps its own name — this bake aims at the mesh, so nothing " +
                  "else that shares this shader is touched.");

            // The entry's own texture bindings, read off the level's database rather than assumed. "source=
            // variation" is the entry's list — the one the copy inherits — as opposed to the material's own.
            // One database is read per level, and they list the same bindings, so a binding is counted by
            // WHICH SLOT it fills rather than by how many lines mention it.
            var s_Slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match s_Texture in Regex.Matches(s_Phase1,
                         @"SHMATTEX: shader=\S+ mesh=(\S+) variation=0 \S+ source=variation param=(\S+) " +
                         @"texture=(\S+)"))
            {
                if (!string.Equals(s_Texture.Groups[1].Value, s_Spec.Mesh, StringComparison.OrdinalIgnoreCase) ||
                    s_Texture.Groups[3].Value == "(unresolved)" ||
                    !s_Slots.Add($"{s_Texture.Groups[2].Value}|{s_Texture.Groups[3].Value}"))
                    continue;

                // Every binding counts; the partition behind it only once. A texture read in two slots is
                // two bindings and one partition, and conflating the two is what made a complete delivery
                // look short by one.
                s_Spec.EntryTextureBindings++;

                if (!s_Spec.EntryTextures.Contains(s_Texture.Groups[3].Value, StringComparer.OrdinalIgnoreCase))
                    s_Spec.EntryTextures.Add(s_Texture.Groups[3].Value);
            }

            if (s_Spec.EntryTextureBindings > 0)
                p_Log($"  the level's entry binds {s_Spec.EntryTextureBindings} texture parameter(s) across " +
                      $"{s_Spec.EntryTextures.Count} partition(s); they ride in the mod's bundle: " +
                      string.Join(", ", s_Spec.EntryTextures));

            // ⛔ AUTHOR AGAINST WHAT THE MAP ACTUALLY ASKS FOR, NOT AGAINST THE ASSUMPTION THAT IT ASKS FOR
            // THE BASE. The copies of a repeated prop are instances of one member of the map's static model
            // group, and that member can hand each instance a variation of its own. This bake writes the
            // BASE key, so copies carrying a variation hash would never see it — and the mod would install,
            // report success and change nothing, which is the failure mode that costs a boot to notice.
            foreach (var s_Level in s_LevelDumps.Values.Where(File.Exists))
            {
                if (!VariationPlan.InspectLevelUse(s_Level, s_Spec.MeshRealPartGuid, out var s_Count,
                        out var s_Hashes, out var s_Grouped))
                    continue;

                p_Log($"  the level places {s_Count} instance(s) of '{s_Spec.Mesh}' in its static model " +
                      $"group; {(s_Hashes.Count == 0 ? "all on the base variation" : $"{s_Hashes.Count} carry a variation of their own")}");

                s_Spec.GroupedInLevel |= s_Grouped;
                s_Spec.GroupedInstances += s_Count;

                // ⛔ WHAT IS AND IS NOT MEASURED ABOUT GROUPED COPIES. They were once written off as unable to
                // read a per-mesh variation at all. That came from a build where the pixel flavour a
                // lightmapped copy draws with was still keeping its vanilla bytecode, so those copies had to
                // look unchanged whether or not the data reached them — the observation could not tell the
                // two apart. With every flavour now compiled against its own contract, "unchanged" finally
                // means something, so the take-over ships for them and the result is readable.
                if (s_Grouped)
                    p_Log("    those copies belong to the level's static model group; the take-over ships for " +
                          "them and this bake is what tells us whether it reaches them");

                if (s_Hashes.Count > 0)
                    s_Spec.LevelVariationHashes = s_Hashes.Distinct().ToList();
            }
        }

        // One outcome per queued command, in order: levels × (shaders, then new-slot surgeries). Every queued
        // command must report success — a surgery that silently failed would build a bundle whose shader
        // samples a register nothing binds.
        var s_Outcomes = Regex.Matches(s_Phase1,
                @"Replaced (\d+) pixel permutation\(s\)|No shader name matched|No pixel permutation was patched|" +
                @"Patched (\d+) pixel ShaderConstant\(s\)|No shader matched|" +
                @"Cloned into (\d+) render path\(s\)|CLONE FAILED")
            .Select(p_M => p_M.Groups[1].Success ? int.Parse(p_M.Groups[1].Value)
                : p_M.Groups[2].Success ? 10_000 + int.Parse(p_M.Groups[2].Value)
                : p_M.Groups[3].Success ? 20_000 + int.Parse(p_M.Groups[3].Value)
                : -1)
            .ToList();

        var s_PerLevel = p_Shaders.Count + s_NewSlots.Count;

        for (var i = 0; i < s_Result.Levels.Count; i++)
        {
            var s_Outcome = s_Result.Levels[i];
            var s_Total = 0;
            string? s_Failure = null;

            // A level that misses ANY of the shaders fails whole: shipping a mod with some of the chosen
            // shaders silently absent is the worst kind of working-looking bundle.
            for (var j = 0; j < p_Shaders.Count && s_Failure == null; j++)
            {
                var s_Index = i * s_PerLevel + j;
                var s_Code = s_Index < s_Outcomes.Count ? s_Outcomes[s_Index] : -2;

                if (p_Shaders[j].Variation != null)
                {
                    if (s_Code < 20_000)
                        s_Failure = $"cloning '{p_Shaders[j].Target}' -> " +
                                    $"'{p_Shaders[j].Variation!.CloneShader}' did not succeed (see bake_patch.log)";
                    else
                        s_Total += s_Code - 20_000;

                    continue;
                }

                if (s_Code <= 0 || s_Code >= 10_000)
                {
                    s_Failure = s_Code switch
                    {
                        -1 => $"'{p_Shaders[j].Target}' matched nothing (or patched nothing) in this level's database",
                        -2 => "the patch step reported nothing for this level (see bake_patch.log)",
                        _ => $"'{p_Shaders[j].Target}' matched, but no solution used the " +
                             $"{ModeFor(p_Shaders[j].DxbcPath)} pass",
                    };
                    break;
                }

                s_Total += s_Code;
            }

            for (var j = 0; j < s_NewSlots.Count && s_Failure == null; j++)
            {
                var s_Index = i * s_PerLevel + p_Shaders.Count + j;
                var s_Code = s_Index < s_Outcomes.Count ? s_Outcomes[s_Index] : -2;

                if (s_Code < 10_000)
                    s_Failure = $"adding the texture slot t{s_NewSlots[j].SlotRegister} <- " +
                                $"'{s_NewSlots[j].Name}' did not succeed (see bake_patch.log)";
            }

            s_Outcome.Permutations = s_Total;

            if (s_Failure != null)
            {
                s_Outcome.Failure = s_Failure;
                p_Log($"  {s_Outcome.Level}: SKIPPED - {s_Failure}");
                continue;
            }

            var s_Bin = DbPath(p_WorkDir, s_Outcome.Level);
            if (!File.Exists(s_Bin))
            {
                s_Outcome.Failure = "the patch step wrote no database (see bake_patch.log)";
                p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                continue;
            }

            s_Outcome.ShaderDbBytes = new FileInfo(s_Bin).Length;

            // A truncated intermediate is accepted by the next step without complaint, so it is caught here.
            if (s_Outcome.ShaderDbBytes < 1_000_000)
            {
                s_Outcome.Failure = $"the patched database is only {s_Outcome.ShaderDbBytes} bytes, which is truncated";
                p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                continue;
            }

            // The additive cut is what actually ships, so it gets its own existence check: without one, a
            // slice that never ran would fall through to a bundle publishing an empty resource name.
            if (p_Request.AdditiveDb)
            {
                // Each cut that was ASKED for must exist: a slice that never ran would otherwise fall through
                // to a bundle publishing an empty resource name.
                long s_Bytes = 0;
                foreach (var (s_Keys, s_Early) in new[] { (s_EarlyKeys, true), (s_LateKeys, false) })
                {
                    if (s_Keys.Count == 0)
                        continue;

                    var s_Sliced = SlicedDbPath(p_WorkDir, s_Outcome.Level, s_Early);
                    if (!File.Exists(s_Sliced) || new FileInfo(s_Sliced).Length < 1024)
                    {
                        s_Outcome.Failure = $"the {(s_Early ? "variation" : "replacement")} database was not " +
                                            "produced (see bake_patch.log for the slice step)";
                        break;
                    }

                    s_Bytes += new FileInfo(s_Sliced).Length;
                }

                if (s_Outcome.Failure != null)
                {
                    p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                    continue;
                }

                s_Outcome.ShaderDbBytes = s_Bytes;
                p_Log($"  {s_Outcome.Level}: {s_Outcome.Permutations} permutation(s) patched, additive " +
                      $"database {s_Outcome.ShaderDbBytes:N0} bytes (" +
                      $"{s_EarlyKeys.Count} variation key(s) before the level, " +
                      $"{s_LateKeys.Count} replacement key(s) after it)");
                continue;
            }

            p_Log($"  {s_Outcome.Level}: {s_Outcome.Permutations} permutation(s) patched across " +
                  $"{p_Shaders.Count} shader(s), {s_Outcome.ShaderDbBytes:N0} bytes");
        }

        // ── How far a replacement actually reaches, read off the level rather than assumed ───────────────
        // ⛔ A REPLACEMENT ANSWERS FOR A NAME, NOT FOR AN OBJECT. Aimed at a shader only one mesh wears it is
        // exactly what the author meant; aimed at a shared preset it repaints every object in the map wearing
        // it, and there is no way to tell the two apart from inside the editor. So the count is measured here
        // and a shared one is refused, with the aimed route named in the message: a variation is keyed by the
        // MESH, so it touches that object and nothing else.
        // ⛔ AND PER MAP, BECAUSE A SHADER DRESSES DIFFERENT OBJECTS IN EACH ONE. The same preset can be worn
        // by one mesh in one map and by eighty in the next, so a single merged number would name no map and
        // answer for none. Each scan closes with a line naming the scope it ran over, so the output is cut
        // into one block per scan and counted inside each.
        var s_Scans = new List<(string Scope, string Shader, string Body)>();
        var s_Cut = 0;

        foreach (Match s_Done in Regex.Matches(s_Phase1, @"SHMATTEX-DONE: (\S+) '([^']*)'[^\n]*"))
        {
            s_Scans.Add((s_Done.Groups[1].Value, s_Done.Groups[2].Value, s_Phase1[s_Cut..s_Done.Index]));
            s_Cut = s_Done.Index + s_Done.Length;
        }

        List<string> WearersIn(string p_Body, string p_Shader)
        {
            var s_Found = new List<string>();
            foreach (Match s_Wearer in Regex.Matches(p_Body, @"SHMATMESH: shader=(\S+) mesh=(\S+) "))
            {
                // The scan matches by substring, so a shader whose name merely CONTAINS the target would be
                // counted in; only the shader this bake replaces is.
                if (!string.Equals(s_Wearer.Groups[1].Value, p_Shader, StringComparison.OrdinalIgnoreCase) ||
                    s_Found.Contains(s_Wearer.Groups[2].Value, StringComparer.OrdinalIgnoreCase))
                    continue;

                s_Found.Add(s_Wearer.Groups[2].Value);
            }

            return s_Found;
        }

        foreach (var s_Target in p_Shaders.Where(p_S => p_S.Variation == null).Select(p_S => p_S.Target)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var s_ByMap in s_Scans
                     .Where(p_S => string.Equals(p_S.Shader, s_Target, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(p_S => p_S.Scope, StringComparer.OrdinalIgnoreCase))
        {
            var s_Map = s_ByMap.Key.Split('/').Last().ToUpperInvariant();

            // ⛔ THE WIDEST SCAN OF THAT MAP, NOT THE FIRST. The same map can be scanned twice in one bake —
            // once narrowed to a mesh folder for a variation, once whole for this count — and the narrowed one
            // is a SUBSET. Reading it would answer "only one object wears it" about a shader worn by eighty,
            // which is the reassuring direction and the one that costs a repainted map.
            var s_Wearers = s_ByMap.Select(p_S => WearersIn(p_S.Body, s_Target))
                .OrderByDescending(p_W => p_W.Count)
                .First();

            if (s_Wearers.Count <= 1)
            {
                p_Log($"  '{s_Target}' is worn by {s_Wearers.Count} mesh(es) in {s_Map}" +
                      (s_Wearers.Count == 1 ? $" ({s_Wearers[0]})" : "") + ", so replacing it is aimed there.");
                continue;
            }

            if (Environment.GetEnvironmentVariable("RSE_REPLACE_SHARED") == "1")
            {
                p_Log($"  '{s_Target}' is worn by {s_Wearers.Count} meshes in {s_Map} and " +
                      "RSE_REPLACE_SHARED=1 is set — every one of them will render with this shader.");
                continue;
            }

            var s_Failure =
                $"in {s_Map}, '{s_Target}' is worn by {s_Wearers.Count} different meshes " +
                $"({string.Join(", ", s_Wearers.Take(3))}{(s_Wearers.Count > 3 ? ", …" : "")}), and a " +
                "replacement answers for the shader's NAME — all of them would render with this shader. To " +
                "change ONE object, bake it as a variation instead: pick the object's set in the preview, " +
                "give the graph a variation name and tick \"use this on the objects already in the level\". " +
                "If repainting every one of them is what you meant, set RSE_REPLACE_SHARED=1 and bake again.";

            p_Log($"  REFUSED - {s_Failure}");
            foreach (var s_Outcome in s_Result.Levels)
                s_Outcome.Failure ??= s_Failure;
        }

        // ── Phase 2: one process per level, because each needs its own build sequence ─────────────────
        foreach (var s_Outcome in s_Result.Levels.Where(p_L => p_L.Failure == null))
        {
            var s_Map = s_Outcome.Level.ToLowerInvariant();
            var s_SbName = $"Win32/{p_Request.BundleName}/{s_Map}";
            var s_BundleName = $"{s_SbName}b";
            var s_Bin = DbPath(p_WorkDir, s_Outcome.Level);

            // ⛔ WHICH BUNDLE HOLDS WHAT IS THE DELIVERY, so each piece stays exactly where its own mode has
            // been proven to work:
            //   <sb>e  the EARLY database (clone keys) AND the custom textures — everything that has to be in
            //          place before the level's own bundle. Only exists when this mod carries a variation.
            //   <sb>b  the LATE database (vanilla keys), which must register after the level's own; and, in a
            //          mod with no variation, the textures too — that is where they have always ridden.
            //   <sb>v  the authored variation partitions.
            var s_EarlyBundle = $"{s_SbName}e";
            var s_HasEarly = p_Request.AdditiveDb && s_EarlyKeys.Count > 0;
            var s_HasLate = !p_Request.AdditiveDb || s_LateKeys.Count > 0;

            var s_Build = new StringBuilder();
            s_Build.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
            s_Build.AppendLine($"build_sb {s_SbName} Frostbite2_0 \"{s_SbRoot}\" true");

            // Custom textures override the game texture of the same NAME — the mechanism three shipped mods
            // already use. Resident (streaming=false), or only the small mips would load from a mod bundle.
            // The TextureGroup argument is omitted so Rime copies the mounted original's group, which a
            // same-name override always has.
            void CustomTextures()
            {
                foreach (var s_Texture in p_CustomTextures ?? Array.Empty<MainWindow.CustomBakeTexture>())
                    s_Build.AppendLine($"add_dds_texture \"{s_Texture.Name}\" \"{s_Texture.DdsPath}\" " +
                                       $"{(s_Texture.Srgb ? "true" : "false")} false " +
                                       $"{(s_Texture.Normal ? "true" : "false")}" +
                                       (s_Texture.GroupDonor != null
                                           ? $" Default \"{s_Texture.GroupDonor}\""
                                           : ""));
            }

            if (s_HasEarly)
            {
                s_Build.AppendLine($"build_bundle {s_EarlyBundle}");
                s_Build.AppendLine($"replace_resource_as {LevelScanner.ShaderDbFor(s_Outcome.Level)} " +
                                   $"{AdditiveDbName(p_Request.ModName, true)} 1 " +
                                   $"\"{SlicedDbPath(p_WorkDir, s_Outcome.Level, true)}\"");
                CustomTextures();
                s_Build.AppendLine("build");
            }

            if (s_HasLate)
            {
                s_Build.AppendLine($"build_bundle {s_BundleName}");

                // Additive: publish OUR OWN small database under a fresh resource name (the level's own
                // resource is left alone, so nothing collides with another mod). Otherwise: the level's whole
                // database, republished under the level's own name, which is what makes two such mods
                // mutually exclusive.
                if (p_Request.AdditiveDb)
                    s_Build.AppendLine($"replace_resource_as {LevelScanner.ShaderDbFor(s_Outcome.Level)} " +
                                       $"{AdditiveDbName(p_Request.ModName)} 1 " +
                                       $"\"{SlicedDbPath(p_WorkDir, s_Outcome.Level, false)}\"");
                else
                    s_Build.AppendLine($"replace_resource {LevelScanner.ShaderDbFor(s_Outcome.Level)} 1 " +
                                       $"\"{s_Bin}\"");

                if (!s_HasEarly)
                    CustomTextures();
            }

            // Variation delivery — a separate VARIATION BUNDLE holding only the authored partitions (shader
            // stub, variation asset, single-entry mesh-variation database keyed by FNV(variation name)). The
            // mesh is NOT carried: the entry references the level's own resident mesh by guid, so no copy
            // ever shadows it and no resolver closure (lod groups, mesh set, chunks) is needed. That
            // reference only resolves if the level bundle has already loaded — the generated loader inserts
            // this bundle right AFTER the level's own, which is what makes the reference-only delivery safe
            // (loading it early is the proven null-mesh registration crash).
            var s_Variations = p_Shaders
                .Where(p_S => p_S.Variation != null)
                .Select(p_S => (p_S.Target, Spec: p_S.Variation!))
                .ToList();

            if (s_Variations.Count > 0)
            {
                // Closes whichever bundle is open; with neither database bundle written (nothing to publish)
                // there is none, and an extra "build" would close the superbundle early.
                if (s_HasEarly || s_HasLate)
                    s_Build.AppendLine("build");

                s_Build.AppendLine($"build_bundle {s_SbName}v");

                foreach (var (s_FilterShader, s_Spec) in s_Variations)
                {
                    var s_Mvdb = LevelScanner.ShaderDbFor(s_Outcome.Level)
                        .Replace("/shaderdb", "/meshvariationdb_win32");

                    s_Build.AppendLine($"add_json_partition \"{s_Spec.CloneShader}\" \"{s_Spec.StubJsonPath}\"");
                    s_Build.AppendLine($"add_json_partition \"{s_Spec.VariationAsset}\" \"{s_Spec.VariationJsonPath}\"");

                    // ⛔ DIAGNOSTIC ONLY (RSE_DIAG_NO_MVDB=1): ship the shader-partition override WITHOUT the
                    // mesh-variation entry. Both routes are live at once and both end at the same authored
                    // shader, so a hand-placed object coming up authored proves nothing about WHICH one did
                    // it — and that is the question blocking the grouped copies. With the entry gone, the
                    // object either still changes (the override drives the render, and the group's problem
                    // is elsewhere) or goes vanilla (the override is cosmetic and this route is dead).
                    var s_NoMvdb = Environment.GetEnvironmentVariable("RSE_DIAG_NO_MVDB") == "1";
                    if (s_NoMvdb)
                        p_Log("  DIAGNOSTIC: RSE_DIAG_NO_MVDB=1 — shipping the shader override ALONE, with " +
                              "no mesh-variation entry. This build is for the A/B, not for use.");

                    if (s_Spec.ApplyToLevel)
                    {
                        // The stand-in rides in this bundle and the entry is repointed onto it, so every
                        // reference the entry makes resolves from here — which is what lets this bundle load
                        // before the level's and take the key.
                        s_Build.AppendLine($"add_json_partition \"{s_Spec.MeshStubName}\" " +
                                           $"\"{s_Spec.MeshStubJsonPath}\"");

                        // ⛔ AND THE ART THE ENTRY BINDS, CARRIED WITH IT. This bundle loads before the one
                        // the level's copies live in, so every reference the entry makes has to resolve from
                        // inside it — the mesh and its material do, through the stand-in, and these do not
                        // unless they ride along. Their own partitions, under their own names and guids, so
                        // the entry needs no repointing and nothing else in the game changes.
                        foreach (var s_Texture in s_Spec.EntryTextures)
                            s_Build.AppendLine($"add_existing_partition \"{s_Texture}\" 1");

                        // ⛔ TO CHANGE WHAT IS ALREADY IN THE MAP, DO NOT GIVE THE OBJECT A NEW VARIATION AND
                        // THEN TRY TO POINT EVERYTHING AT IT — measured: the copies do take the new hash (49 of
                        // them, read back), and the map still renders unchanged, because by then the engine has
                        // already resolved what each copy draws with.
                        //
                        // Instead, add an entry for the variation those copies ALREADY use — the BASE one,
                        // hash 0 — whose materials point at the authored shader. Nothing has to change at
                        // runtime: the object keeps asking for the variation it always asked for, and that
                        // variation now resolves to this mod's shader.
                        //
                        // ⛔ HASH 0, NOT EMPTY. Everything the command does — building the entry AND
                        // repointing its materials — sits inside `if (VariationHash is not blank)`. Passing
                        // blank skipped the whole block, so the mini-MVDB shipped a VERBATIM copy of the
                        // vanilla entry: a mod that installs, logs success and changes nothing (measured, one
                        // wasted boot). "0" enters the block and asks for the base entry, which is the intent.
                        //
                        // No 'all' either: a lone entry becomes the head of the mesh's runtime variation list,
                        // which is exactly the takeover wanted here — the same effect that was once a bug
                        // (a lone entry hijacking the default look of the red security booth).
                        if (!s_NoMvdb)
                            s_Build.AppendLine($"mvdb_add_entry {s_Mvdb} {s_Spec.MvdbName} 1 {s_Spec.Mesh} " +
                                               $"\"\" {s_Spec.MeshRealPartGuid} {s_Spec.MeshStubPartGuid} " +
                                               $"\"\" \"\" {s_Spec.MvdbPartGuid} {s_Spec.MvdbInstGuid} \"\" " +
                                               $"0 \"{s_Spec.VarPartGuid}:{s_Spec.MmvInstGuid}\" {s_FilterShader}");
                    }
                    else
                    {
                        // 'all': the mini-MVDB carries every vanilla entry of the mesh INTACT plus the new
                        // variation appended — a lone new entry became the head of the runtime variation list
                        // and hijacked the DEFAULT look of freshly spawned objects (the red security booth).
                        s_Build.AppendLine($"mvdb_add_entry {s_Mvdb} {s_Spec.MvdbName} 1 {s_Spec.Mesh} all " +
                                           $"\"\" \"\" \"\" \"\" {s_Spec.MvdbPartGuid} {s_Spec.MvdbInstGuid} \"\" " +
                                           $"{s_Spec.Hash} \"{s_Spec.VarPartGuid}:{s_Spec.MmvInstGuid}\" {s_FilterShader}");
                    }
                }

                // The carried texture partitions are EBX shells: the pixels live in a resource plus its
                // streaming chunk, and a partition added without them is a texture reference the streaming
                // side cannot answer. One pass closes them for everything added above.
                if (s_Variations.Any(p_V => p_V.Spec.EntryTextures.Count > 0))
                    s_Build.AppendLine("resolve_resource_dependencies 1");
            }

            s_Build.AppendLine("build");
            s_Build.AppendLine("build");
            s_Build.AppendLine("exit");

            p_Log($"Phase 2 [{s_Outcome.Level}]: building the superbundle...");
            var s_BuildLog = RunScript(p_RimeRepl, p_WorkDir, $"bake_build_{s_Map}.rime", s_Build.ToString());

            // ⛔ A variation whose materials were NOT repointed is a mod that installs, reports success and
            // changes nothing — it shipped once and cost a boot to discover. The repoint reports how many
            // materials it took ("N/M material(s) repointed"), so ZERO is caught here instead of in game.
            foreach (Match s_Repoint in Regex.Matches(s_BuildLog, @"Variation: hash=\d+, (\d+)/(\d+) material"))
                if (s_Repoint.Groups[1].Value == "0")
                    s_Outcome.Failure = "the variation's materials were not repointed at the authored shader " +
                                        $"(0/{s_Repoint.Groups[2].Value}) — the mod would install and change " +
                                        "nothing. The shader filter matched no material of this mesh.";

            // The diagnostic build deliberately ships no mesh-variation entry, so none of the entry's own
            // guards apply to it — they would all fire on work that was skipped on purpose.
            var s_Diagnostic = Environment.GetEnvironmentVariable("RSE_DIAG_NO_MVDB") == "1";

            // Same class of silence: the repoint line missing entirely means the command skipped that work.
            if (!s_Diagnostic && s_Variations.Any(p_V => p_V.Spec.ApplyToLevel) &&
                !s_BuildLog.Contains("material(s) repointed"))
                s_Outcome.Failure ??= "the mesh-variation entry was written without repointing any material " +
                                      "at the authored shader — the mod would install and change nothing.";

            // ⛔ AND THE SAME SILENCE ONE LEVEL DOWN. A take-over bundle loads BEFORE the level's, which is
            // only survivable because the entry's references were moved onto the stand-in partition riding
            // with it. A repoint that moved NOTHING leaves them pointing into a bundle that is not loaded
            // yet: the registrar reads a null mesh and the client dies on the load thread, without a
            // minidump, the instant the level starts. It shipped exactly once, from a guid read off the
            // wrong place in the dump, and the count was in the log all along.
            foreach (Match s_Moved in Regex.Matches(s_BuildLog, @"Repointed (\d+) ref\(s\)"))
                if (s_Moved.Groups[1].Value == "0")
                    s_Outcome.Failure ??= "the mesh-variation entry was not repointed onto the stand-in mesh " +
                                          "partition (0 refs moved) — shipping it would load the mod's bundle " +
                                          "first with references the engine cannot resolve yet, which crashes " +
                                          "the client as the level starts loading.";

            // The same rule for what the entry binds per material. A prop with streamable textures binds
            // none and is safe; an object whose entry carries its own texture bindings (a vehicle's camo)
            // would reach into a bundle that has not loaded yet.
            // ⛔ THIS USED TO FAIL THE BAKE OUTRIGHT FOR GROUPED COPIES, DEMANDING THE SHADER RENAME INSTEAD.
            // The rule it enforced ("a member of the level's static model group cannot be served per mesh")
            // was read off a build in which the flavour those copies draw with kept its VANILLA bytecode, so
            // they were bound to look unchanged either way — a delivery question answered with a picture that
            // could not answer it. The rename also cannot be aimed: it repaints every mesh sharing the
            // shader. The take-over ships instead, and what it does is now legible.
            // Read off the level itself, before the build: copies that ask for a variation of their own are
            // never served by the base key this bake writes.
            foreach (var s_Spec in s_Variations.Select(p_V => p_V.Spec)
                         .Where(p_S => p_S.ApplyToLevel && p_S.LevelVariationHashes.Count > 0))
                s_Outcome.Failure ??= $"the level's copies of '{s_Spec.Mesh}' already carry variation(s) of " +
                                      $"their own ({string.Join(", ", s_Spec.LevelVariationHashes)}), so the " +
                                      "base appearance this bake authors would never be the one they ask " +
                                      "for. Untick \"use this on the objects already in the level\" and " +
                                      "place the variation by hand.";

            // ⛔⛔⛔ THE ONE FIELD THE WHOLE VARIATION HANGS FROM: the name inside the clone's shader
            // partition. The variation's material reaches its shader through that graph, and the engine looks
            // that name up in a shader database — so it has to be the key this bake wrote. It shipped once
            // naming ANOTHER shader entirely (a file left in the work directory by an earlier bake, which
            // nothing rewrote), the lookup found nothing, and the level's copies rendered as nothing at all
            // while every other check passed.
            foreach (var s_Spec in s_Variations.Select(p_V => p_V.Spec))
            {
                var s_Named = VariationPlan.StubGraphName(s_Spec);
                if (string.Equals(s_Named, s_Spec.CloneShader, StringComparison.Ordinal))
                    continue;

                s_Outcome.Failure ??= "the clone's shader partition names " +
                                      (s_Named == null ? "nothing readable" : $"'{s_Named}'") +
                                      $" instead of '{s_Spec.CloneShader}', so the variation's materials " +
                                      "would look up a shader that does not exist and the objects would not " +
                                      "be drawn at all.";
            }

            // ⛔ WHAT THE ENTRY REACHES FOR MUST BE WHAT THE BUNDLE CARRIES, COUNTED — not assumed. The
            // command reports how many texture BINDINGS the copied entry still makes outside the stand-in, so
            // that is the unit this compares against: the partitions carried are fewer whenever one texture
            // fills two slots, and reading the reported number against the partition count failed a delivery
            // that was in fact complete. They come back in the order the entries were written.
            var s_Bound = Regex.Matches(s_BuildLog, @"binds (\d+) texture\(s\) outside");

            for (var j = 0; j < s_Bound.Count && s_Outcome.Failure == null; j++)
            {
                var s_Spec = j < s_Variations.Count ? s_Variations[j].Spec : null;
                var s_Identified = s_Spec?.EntryTextureBindings ?? 0;
                if (int.Parse(s_Bound[j].Groups[1].Value) <= s_Identified)
                    continue;

                s_Outcome.Failure = $"this object's entry binds {s_Bound[j].Groups[1].Value} texture(s) of " +
                                    $"its own and only {s_Identified} could be identified to ride with it. " +
                                    "Taking over the level's copies needs the bundle to load first, and a " +
                                    "binding left behind points into a bundle that is not there yet. Untick " +
                                    "\"use this on the objects already in the level\" for this shader and " +
                                    "place the variation by hand instead.";
            }

            // Carrying a texture partition without its pixels is the same reference failing one level down,
            // so the resolver's own count is read back rather than trusted.
            if (s_Variations.Any(p_V => p_V.Spec.EntryTextures.Count > 0) &&
                !s_BuildLog.Contains("Added resource:", StringComparison.Ordinal))
                s_Outcome.Failure ??= "the textures the entry binds were added to the bundle without their " +
                                      "image data (no resource was resolved), which is a texture reference " +
                                      "the game cannot answer once the mod's bundle loads first.";

            if (s_BuildLog.Contains("Could not find partition (", StringComparison.Ordinal))
                s_Outcome.Failure ??= "a partition this bake asked the bundle to carry was not found in the " +
                                      "game (see the build log) — the entry would ship pointing at nothing.";

            if (s_Outcome.Failure != null)
            {
                p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                continue;
            }

            var s_Sb = Path.Combine(s_SbRoot, "Win32", p_Request.BundleName, s_Map + ".sb");
            var s_Toc = Path.ChangeExtension(s_Sb, ".toc");

            if (!File.Exists(s_Sb))
            {
                s_Outcome.Failure = "no superbundle was produced";
                p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                continue;
            }

            s_Outcome.SuperBundleBytes = new FileInfo(s_Sb).Length;
            s_Outcome.TocBytes = File.Exists(s_Toc) ? new FileInfo(s_Toc).Length : 0;

            // A run can print "Bundle successfully built" and still leave a zero-byte toc behind.
            if (s_Outcome.TocBytes == 0)
            {
                s_Outcome.Failure = "the .toc is empty, so the build did not finish (retrying usually fixes it)";
                p_Log($"  {s_Outcome.Level}: FAILED - {s_Outcome.Failure}");
                continue;
            }

            // Every bundle this bake actually wrote, and only those: checking one that was never built would
            // fail a correct mod, and skipping one that was would ship the failure this check exists for.
            var s_HeaderProblem =
                (s_HasEarly ? CheckCasHeader(s_Sb, s_EarlyBundle) : null)
                ?? (s_HasLate ? CheckCasHeader(s_Sb, s_BundleName) : null)
                ?? (s_Variations.Count > 0 ? CheckCasHeader(s_Sb, $"{s_SbName}v") : null);
            if (s_HeaderProblem != null)
            {
                s_Outcome.Failure = s_HeaderProblem;
                p_Log($"  {s_Outcome.Level}: FAILED - {s_HeaderProblem}");
                continue;
            }

            p_Log($"  {s_Outcome.Level}: {s_Outcome.SuperBundleBytes:N0} bytes, toc {s_Outcome.TocBytes} bytes, CAS header OK");
        }

        WriteModFiles(s_ModFolder, p_Request,
            string.Join(", ", p_Shaders.Select(p_S => p_S.Target)), s_Result,
            p_Shaders.Select(p_S => p_S.Variation).OfType<VariationSpec>().ToList(),
            !p_Request.AdditiveDb || p_Shaders.Any(p_S => p_S.Variation == null));
        return s_Result;
    }

    /// <summary>
    /// A NONCAS superbundle is the failure that hangs the level forever with no error and no dump, and it is
    /// visible in the first bytes: a CAS one opens with a manifest naming the bundle path.
    /// </summary>
    private static string? CheckCasHeader(string p_SbPath, string p_BundleName)
    {
        // The manifest opens the file, but a bundle's own descriptor sits AFTER the payload of every bundle
        // before it — with two bundles the second name lives megabytes in, so the name scan reads the whole
        // file, not just the head.
        var s_Bytes = File.ReadAllBytes(p_SbPath);
        var s_Head = Encoding.ASCII.GetString(s_Bytes, 0, Math.Min(1024, s_Bytes.Length));
        if (!s_Head.Contains("bundles", StringComparison.Ordinal))
            return "the superbundle has no CAS manifest - it was built NONCAS and the engine will hang the level";

        var s_Needle = Encoding.ASCII.GetBytes(p_BundleName);
        if (((ReadOnlySpan<byte>) s_Bytes).IndexOf(s_Needle) < 0)
            return $"the superbundle does not name '{p_BundleName}' - the bundle id will not resolve";

        return null;
    }

    private static string DbPath(string p_WorkDir, string p_Level) =>
        Path.Combine(p_WorkDir, $"shaderdb_{p_Level.ToLowerInvariant()}.bin");

    /// <summary>
    /// An additive database: only this mod's own keys, cut from the patched one. There are two, because the
    /// clone names and the vanilla names have to be registered on opposite sides of the level's own.
    /// </summary>
    private static string SlicedDbPath(string p_WorkDir, string p_Level, bool p_Early) =>
        Path.Combine(p_WorkDir,
            $"shaderdb_{p_Level.ToLowerInvariant()}_additive{(p_Early ? "_early" : "")}.bin");

    /// <summary>
    /// The resource name an additive database is published under. It must NOT be the level's own name (that
    /// is the whole point: two mods claiming it collide, which is what the merger exists to fix), and it
    /// follows the shape the engine builds its own from — "&lt;levelName&gt;/ShaderDb" — so the name stays
    /// inside the family the loader knows, with the mod's own folder standing in for the level.
    /// </summary>
    /// <remarks>
    /// The replacement one keeps the original name so a mod already built this way publishes the same
    /// resource; the variation one gets a sibling, because a single mod can now ship both.
    /// </remarks>
    private static string AdditiveDbName(string p_ModName, bool p_Early = false) =>
        $"levels/{Sanitise(p_ModName)}/{(p_Early ? "sdbv" : "sdb")}/shaderdb";

    private static string Sanitise(string p_Name) =>
        new string(p_Name.ToLowerInvariant().Select(p_C => char.IsLetterOrDigit(p_C) ? p_C : '_').ToArray());

    private static string RunScript(string p_Repl, string p_WorkDir, string p_FileName, string p_Script)
    {
        var s_Path = Path.Combine(p_WorkDir, p_FileName);
        File.WriteAllText(s_Path, p_Script);

        var s_Info = new ProcessStartInfo(p_Repl)
        {
            Arguments = $"\"{s_Path}\"",
            WorkingDirectory = Path.GetDirectoryName(p_Repl) ?? p_WorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var s_Process = Process.Start(s_Info);
        if (s_Process == null)
            return "";

        var s_Output = s_Process.StandardOutput.ReadToEnd() + s_Process.StandardError.ReadToEnd();
        s_Process.WaitForExit();

        // Kept next to the mod so a failed bake can be read afterwards.
        File.WriteAllText(Path.ChangeExtension(s_Path, ".log"), s_Output);
        return s_Output;
    }

    private static void WriteModFiles(string p_ModFolder, BakeRequest p_Request, string p_TargetShader,
        Result p_Result, IReadOnlyList<VariationSpec> p_Variations, bool p_HasLateDb)
    {
        var s_Good = p_Result.Levels.Where(p_L => p_L.Ok).ToList();
        if (s_Good.Count == 0)
            return;

        var s_Superbundles = string.Join(",\n", s_Good.Select(p_L =>
            $"        \"Win32/{p_Request.BundleName}/{p_L.Level.ToLowerInvariant()}\""));

        var s_Maps = string.Join(", ", s_Good.Select(p_L => p_L.Level));
        var s_Json = new StringBuilder();
        s_Json.AppendLine("{");
        s_Json.AppendLine($"    \"Name\": \"{p_Request.ModName}\",");
        s_Json.AppendLine("    \"Authors\": [");
        s_Json.AppendLine("        \"Rime Shader Editor\"");
        s_Json.AppendLine("    ],");
        // ⛔ The description states what the mod ACTUALLY does in the mode it was built in. A mod.json that
        // describes a behaviour its files do not have is a documented time-waster of this project (a loader
        // claimed to hide an overlay it never touched), and the two delivery modes differ in exactly the
        // thing someone reads this line to find out: whether it takes over the level's own database.
        s_Json.AppendLine($"    \"Description\": \"Shader authored in the Rime node editor for {p_TargetShader}, " +
                          $"baked for: {s_Maps}. " +
                          (p_Request.AdditiveDb
                              ? "Ships its OWN small shader database of just the keys it adds, under its own " +
                                "resource name, so the level's database is left untouched and other shader " +
                                "mods can be installed alongside it."
                              : "One superbundle per level, each overriding that level's own database") +
                          "; only the GBuffer permutations are replaced, the ZOnly depth-pass ones are left " +
                          "untouched.\",");
        s_Json.AppendLine("    \"URL\": \"none\",");
        s_Json.AppendLine("    \"Version\": \"0.0.1\",");
        s_Json.AppendLine("    \"HasWebUI\": false,");
        s_Json.AppendLine("    \"HasVeniceEXT\": true,");
        s_Json.AppendLine("    \"Tags\": [");
        s_Json.AppendLine("        \"gameplay\"");
        s_Json.AppendLine("    ],");
        s_Json.AppendLine("    \"Superbundles\": [");
        s_Json.AppendLine(s_Superbundles);
        s_Json.AppendLine("    ],");
        s_Json.AppendLine("    \"Dependencies\": {");
        s_Json.AppendLine("        \"veniceext\": \"^1.0.0\"");
        s_Json.AppendLine("    }");
        s_Json.AppendLine("}");

        // No BOM: VU does not detect a mod whose mod.json starts with one.
        File.WriteAllText(Path.Combine(p_ModFolder, "mod.json"), s_Json.ToString(),
            new UTF8Encoding(false));

        var s_Ext = Path.Combine(p_ModFolder, "ext", "Shared");
        Directory.CreateDirectory(s_Ext);
        File.WriteAllText(Path.Combine(s_Ext, "__init__.lua"),
            p_Variations.Count > 0
                ? VariationLuaFor(p_Request, s_Good.Select(p_L => p_L.Level), p_Variations, p_HasLateDb)
                : LuaFor(p_Request, s_Good.Select(p_L => p_L.Level)),
            new UTF8Encoding(false));
    }

    /// <summary>
    /// The loader for a bake that delivers variations. Two bundles per level: the database bundle is
    /// PREPENDED (bundle order decides which database answers a by-name shader lookup), and the variation
    /// bundle goes where its references can resolve: right AFTER the level's own when the entries point into
    /// it, and BEFORE it when they were repointed onto a stand-in partition riding in the same bundle — which
    /// is the only way to reach a variation key before the level's own database fills it.
    /// </summary>
    private static string VariationLuaFor(BakeRequest p_Request, IEnumerable<string> p_Levels,
        IReadOnlyList<VariationSpec> p_Variations, bool p_HasLateDb)
    {
        var s_Rows = string.Join("\n", p_Levels.Select(p_L =>
            $"\t[\"{p_L.ToUpperInvariant()}\"] = \"{p_Request.BundleName}/{p_L.ToLowerInvariant()}\","));

        var s_Report = ArrivalLuaFor(p_Variations);

        // ⛔ WHERE THE VARIATION BUNDLE GOES IS DECIDED BY WHETHER IT CARRIES A STAND-IN MESH, and getting
        // this backwards is a crash, not a cosmetic difference.
        //
        // A variation is keyed by (mesh nameHash, variation nameHash) and the FIRST entry to reach a key
        // fills it — later ones are dropped in silence. So a variation meant to take over what is already in
        // the map has to register BEFORE the level's own database, and it can only survive being that early
        // because its entry was repointed onto a stand-in partition riding in the same bundle.
        //
        // Without a stand-in the entry still points into the level's bundle, which at that moment is not
        // loaded: the mesh reads back null and the client dies on the load thread. An ordinary variation —
        // one selected by hand in the map editor — has nothing to win by being early, so it goes right after
        // the level's own bundle, where its references resolve.
        // ⛔ AND *WHICH BATCH* IS PART OF THE SAME DECISION. Bundles arrive in several batches; waiting for
        // the one that carries the level's own bundle means every batch before it registered first. A
        // take-over has to be in the EARLIEST batch there is — which is only survivable, again, because the
        // stand-in makes this bundle self-contained. An ordinary variation still waits for the level's,
        // because its entry points into it. Measured: the level's copies are 49 instances of one member of
        // the map's StaticModelGroup, and their InstanceObjectVariation list is EMPTY — they ask for hash 0,
        // the key this mod authors, so what decides the outcome is purely who reaches that key first.
        var s_Gate = p_Variations.Any(p_V => p_V.ApplyToLevel)
            ? "\tif m_Prepended then\n\t\treturn\n\tend\n\tm_Prepended = true"
            : "\tif not s_HasLevel then\n\t\treturn\n\tend";

        // ⛔⛔ THE DATABASE BUNDLE GOES AFTER THE LEVEL'S OWN WHEN IT ANSWERS FOR A NAME THE LEVEL ALSO HAS.
        // A shader lookup walks the registered databases BACKWARDS and keeps the first hit, so the one
        // registered LAST answers for a name — the original does not win, the latest does. Loading this
        // bundle early (which this did, on the belief that order did not matter there) hands every vanilla
        // name back to the level's own database and makes a replacement silently inert.
        //
        // ⛔⛔⛔ A TAKE-OVER IS THE OPPOSITE CASE AND THE SAME RULE PUT IT LAST, WHICH BROKE IT. Its key is a
        // name nothing else defines, so there is nobody to outrank — and the variation database that names
        // it is ingested as soon as the variation bundle loads, which is where each material's shader is
        // resolved BY NAME. Registered after that, the name is not there yet: the materials come up with no
        // shader and the objects vanish (measured — 24 grouped copies went invisible, which is also what
        // proved they DO read the variation). So a take-over registers the database FIRST.
        //
        // The variation bundle is a different question: it goes early only when it carries a stand-in mesh
        // partition (a take-over), because its entries must reach a variation key before the level's do.
        // What this mod actually inserts, declared in the script itself so the log names THAT and cannot
        // claim a bundle the bake never built.
        var s_Own = "\tlocal s_Own = { m_DbEarly, m_VarBundle" + (p_HasLateDb ? ", m_DbBundle" : "") + " }";

        var s_Order = p_Variations.Any(p_V => p_V.ApplyToLevel)
            ? "\t-- Shader database FIRST, then the variation bundle, both before the level's own.\n" +
              "\t-- The variation bundle must be early to reach the variation key before the level's\n" +
              "\t-- database does, which is survivable only because its entries were repointed onto the\n" +
              "\t-- stand-in mesh partition shipped alongside them. The database goes before it because a\n" +
              "\t-- variation's materials resolve their shader BY NAME the moment that database is taken\n" +
              "\t-- up, and this bake's name is a new one that exists nowhere else: registered afterwards,\n" +
              "\t-- the materials come up with no shader and the objects are not drawn at all.\n" +
              "\tlocal s_New = { m_DbEarly, m_VarBundle }\n" +
              "\tfor _, l_Bundle in ipairs(p_Bundles) do\n" +
              "\t\ts_New[#s_New + 1] = l_Bundle\n" +
              (p_HasLateDb
                  ? "\t\tif l_Bundle == s_Level then\n" +
                    "\t\t\ts_New[#s_New + 1] = m_DbBundle\n" +
                    "\t\tend\n"
                  : "") +
              "\tend"
            : "\t-- Both AFTER the level's own bundle: the shader database because the latest one registered\n" +
              "\t-- answers for a name, and the variation bundle because its entries reference the level's\n" +
              "\t-- resident mesh and the materials that live in that mesh's partition.\n" +
              "\tlocal s_New = {}\n" +
              "\tfor _, l_Bundle in ipairs(p_Bundles) do\n" +
              "\t\ts_New[#s_New + 1] = l_Bundle\n" +
              "\t\tif l_Bundle == s_Level then\n" +
              "\t\t\ts_New[#s_New + 1] = m_DbEarly\n" +
              "\t\t\ts_New[#s_New + 1] = m_VarBundle\n" +
              (p_HasLateDb ? "\t\t\ts_New[#s_New + 1] = m_DbBundle\n" : "") +
              "\t\tend\n" +
              "\tend";

        return $@"-- {p_Request.ModName} - generated by the Rime shader editor. Do not hand-edit; rebake instead.
--
-- One superbundle per level, TWO bundles inside it:
--   <name>b  (database bundle)  - the shader database holding ONLY the authored entries, under its own name.
--            The engine does NOT resolve these by name: every database registers itself as it loads, and the
--            shader system walks ALL of them looking for the key, so a database under a fresh name serves its
--            keys with the level's own left untouched (measured in the binary, 2026-09-04). It is prepended
--            so it is registered and resident before anything asks for those keys.
--            NOTE: two mods defining the SAME key do not merge — the last one registered wins.
--   <name>v  (variation bundle) - ONLY the authored variation partitions (shader stub + variation asset +
--            single-entry mesh-variation database). Inserted right AFTER the level's own bundle: it does
--            NOT carry the mesh - its entries reference the level's own resident mesh, and that reference
--            only resolves if the level bundle has already loaded (loading it early crashes the variation
--            registration with a null mesh).

local SUPERS = {{
{s_Rows}
}}

local IS_SERVER = (ClientUtils == nil)
local m_Super, m_DbBundle, m_DbEarly, m_VarBundle = nil, nil, nil, nil
local m_Prepended = false

local function LOG(p_Text)
	print(""[{p_Request.ModName}]["" .. (IS_SERVER and ""SRV"" or ""CLI"") .. ""] "" .. tostring(p_Text))
end

-- Unconditional mark of life: without it, ""nothing happened"" cannot be told apart from ""the mod never loaded"".
LOG(""loaded, targets: "" .. table.concat((function()
	local t = {{}}
	for k in pairs(SUPERS) do t[#t + 1] = k end
	table.sort(t)
	return t
end)(), "", ""))

Events:Subscribe(""Level:LoadResources"", function(p_LevelName)
	m_Super, m_DbBundle, m_DbEarly, m_VarBundle = nil, nil, nil, nil
	m_Prepended = false

	local s_Key = string.upper(tostring(p_LevelName):match(""[^/]+$"") or """")
	local s_Super = SUPERS[s_Key]
	if s_Super == nil then
		return
	end

	m_Super, m_DbBundle, m_DbEarly, m_VarBundle =
		s_Super, s_Super .. ""b"", s_Super .. ""e"", s_Super .. ""v""

	-- Instrumented on purpose: a silently failed mount leaves the injected bundle ids unresolvable and the
	-- level then hangs in ""creating level"" forever instead of erroring out.
	local s_Ok, s_Err = pcall(function() ResourceManager:MountSuperBundle(m_Super) end)
	LOG(""mount '"" .. m_Super .. ""': "" .. (s_Ok and ""OK"" or (""FAILED "" .. tostring(s_Err))))
end)

-- Keyed on the level's own bundle being present rather than on list position, so another module prepending
-- first cannot silently stop this from firing.
Hooks:Install(""ResourceManager:LoadBundles"", 100, function(p_Hook, p_Bundles, p_Compartment)
	if m_Super == nil then
		return
	end

	local s_Level = SharedUtils:GetLevelName()
	local s_HasLevel, s_HasOurs = false, false
	for _, l_Bundle in ipairs(p_Bundles) do
		if l_Bundle == s_Level then
			s_HasLevel = true
		end
		if l_Bundle == m_DbBundle or l_Bundle == m_DbEarly then
			s_HasOurs = true
		end
	end

	if s_HasOurs then
		return
	end

{s_Gate}

{s_Order}

{s_Own}

	-- The bundle names of every batch, because which batch carries the gamemode's own database is the thing
	-- that decides whether going last is enough.
	LOG(""PREPENDED "" .. table.concat(s_Own, "", "")
		.. "" (comp="" .. tostring(p_Compartment) .. "", n="" .. tostring(#p_Bundles) .. ""): ""
		.. table.concat(p_Bundles, "", ""))
	p_Hook:Pass(s_New, p_Compartment)
end)
{s_Report}";
    }

    /// <summary>
    /// Renders the mod script exactly as a bake would, so the generated Lua can be read and checked without
    /// baking (which needs the game mounted). A script only runs on the user's machine: an unchecked one
    /// costs him a boot to discover a typo.
    /// </summary>
    internal static string PreviewVariationLua(BakeRequest p_Request, IEnumerable<string> p_Levels,
        IReadOnlyList<VariationSpec> p_Variations, bool p_HasLateDb = false) =>
        VariationLuaFor(p_Request, p_Levels, p_Variations, p_HasLateDb);

    /// <summary>
    /// The OTHER loader — the one a plain replacement ships — rendered for the same checks.
    ///
    /// ⛔ It was invisible to the battery while every test went through the variation loader, so a fix to
    /// bundle order landed in one and not the other: the replacement kept registering its database FIRST,
    /// which is precisely the order that loses. Two loaders means two things to assert.
    /// </summary>
    internal static string PreviewReplacementLua(BakeRequest p_Request, IEnumerable<string> p_Levels) =>
        LuaFor(p_Request, p_Levels);

    /// <summary>
    /// Reports which of the mod's OWN partitions actually reach the game, and every mesh-variation database
    /// the level loads.
    ///
    /// ⛔ It exists because four fixes in a row moved a different piece and none moved the symptom, all of
    /// them resting on an assumption nobody had checked: that the mod's mesh-variation database is ingested
    /// at all. A bundle that mounts proves the BUNDLE arrived, not that its partitions were taken up — and
    /// "the mod does nothing" reads identically whether the data never arrived or arrived and lost.
    /// </summary>
    private static string ArrivalLuaFor(IReadOnlyList<VariationSpec> p_Variations)
    {
        var s_Wanted = new List<string>();
        foreach (var s_Variation in p_Variations)
        {
            if (s_Variation.MvdbName.Length > 0)
                s_Wanted.Add($"\t[\"{s_Variation.MvdbName.ToLowerInvariant()}\"] = \"variation database\",");
            if (s_Variation.VariationAsset.Length > 0)
                s_Wanted.Add($"\t[\"{s_Variation.VariationAsset.ToLowerInvariant()}\"] = \"variation asset\",");
            if (s_Variation.CloneShader.Length > 0)
                s_Wanted.Add($"\t[\"{s_Variation.CloneShader.ToLowerInvariant()}\"] = \"shader stub\",");
        }

        if (s_Wanted.Count == 0)
            return "";

        return $@"
-- ── Did this mod's data actually arrive? ─────────────────────────────────────────────────────────────
local EXPECTED = {{
{string.Join("\n", s_Wanted.Distinct())}
}}

local m_Arrived = {{}}
local m_Databases = {{}}
local m_Contenders = {{}}

Events:Subscribe(""Partition:Loaded"", function(p_Partition)
	local s_Name = p_Partition.name ~= nil and string.lower(p_Partition.name) or """"

	if EXPECTED[s_Name] ~= nil and m_Arrived[s_Name] == nil then
		m_Arrived[s_Name] = true
		LOG(""arrived: "" .. s_Name .. "" ("" .. EXPECTED[s_Name] .. "")"")
	end

	-- ⛔ THE ORDER THESE ARRIVE IN IS THE WHOLE GAME. A variation key is filled by the FIRST database that
	-- reaches it and every later one is dropped in silence, so a mod taking over an object already in the
	-- map has to arrive before the database that owns it. Hundreds of these load (soldier gear, weapons,
	-- vehicles) and printing them all buries the answer, so only the level's own and this mod's are listed,
	-- each with the position it arrived in.
	for _, l_Instance in pairs(p_Partition.instances) do
		if l_Instance:Is(""MeshVariationDatabase"") then
			local s_Name = p_Partition.name ~= nil and string.lower(p_Partition.name) or ""<unnamed>""
			m_Databases[#m_Databases + 1] = s_Name

			if EXPECTED[s_Name] ~= nil or string.sub(s_Name, 1, 7) == ""levels/"" then
				m_Contenders[#m_Contenders + 1] = ""#"" .. tostring(#m_Databases) .. "" "" .. s_Name ..
					(EXPECTED[s_Name] ~= nil and ""   <-- OURS"" or """")
			end

			break
		end
	end
end)

Events:Subscribe(""Level:Loaded"", function()
	local s_Missing = {{}}
	for l_Name, l_What in pairs(EXPECTED) do
		if m_Arrived[l_Name] == nil then
			s_Missing[#s_Missing + 1] = l_What .. "" ('"" .. l_Name .. ""')""
		end
	end

	if #s_Missing == 0 then
		LOG(""all of this mod's partitions arrived"")
	else
		LOG(""NEVER ARRIVED: "" .. table.concat(s_Missing, "", ""))
	end

	LOG(tostring(#m_Databases) .. "" mesh-variation database(s) loaded; the level's own and ours, in "" ..
		""arrival order (the FIRST to reach a key keeps it):"")
	for _, l_Line in ipairs(m_Contenders) do
		LOG(""   "" .. l_Line)
	end
end)
";
    }

    /// <summary>
    /// The loader. A superbundle declared in mod.json is NOT mounted and NOT prepended automatically, and the
    /// prepend is what decides which database answers a shader lookup - ours has to come before the level's own.
    /// </summary>
    private static string LuaFor(BakeRequest p_Request, IEnumerable<string> p_Levels)
    {
        var s_Rows = string.Join("\n", p_Levels.Select(p_L =>
            $"\t[\"{p_L.ToUpperInvariant()}\"] = \"{p_Request.BundleName}/{p_L.ToLowerInvariant()}\","));

        return $@"-- {p_Request.ModName} - generated by the Rime shader editor. Do not hand-edit; rebake instead.
--
-- One superbundle per level, each carrying that level's shader database with the authored pixel shader in it.
-- A declared superbundle is NOT mounted or prepended automatically, so both are done here. The prepend is not
-- cosmetic: the shader system resolves solutions BY NAME and bundle order decides which database answers, so
-- ours must come BEFORE the level's own bundle.

local SUPERS = {{
{s_Rows}
}}

local IS_SERVER = (ClientUtils == nil)
local m_Super, m_Bundle = nil, nil

local function LOG(p_Text)
	print(""[{p_Request.ModName}]["" .. (IS_SERVER and ""SRV"" or ""CLI"") .. ""] "" .. tostring(p_Text))
end

-- Unconditional mark of life: without it, ""nothing happened"" cannot be told apart from ""the mod never loaded"".
LOG(""loaded, targets: "" .. table.concat((function()
	local t = {{}}
	for k in pairs(SUPERS) do t[#t + 1] = k end
	table.sort(t)
	return t
end)(), "", ""))

Events:Subscribe(""Level:LoadResources"", function(p_LevelName)
	m_Super, m_Bundle = nil, nil

	local s_Key = string.upper(tostring(p_LevelName):match(""[^/]+$"") or """")
	local s_Super = SUPERS[s_Key]
	if s_Super == nil then
		return
	end

	m_Super, m_Bundle = s_Super, s_Super .. ""b""

	-- Instrumented on purpose: a silently failed mount leaves the prepended bundle id unresolvable and the
	-- level then hangs in ""creating level"" forever instead of erroring out.
	local s_Ok, s_Err = pcall(function() ResourceManager:MountSuperBundle(m_Super) end)
	LOG(""mount '"" .. m_Super .. ""': "" .. (s_Ok and ""OK"" or (""FAILED "" .. tostring(s_Err))))
end)

-- Keyed on the level's own bundle being present rather than on list position, so another module prepending
-- first cannot silently stop this from firing.
Hooks:Install(""ResourceManager:LoadBundles"", 100, function(p_Hook, p_Bundles, p_Compartment)
	if m_Bundle == nil then
		return
	end

	local s_Level = SharedUtils:GetLevelName()
	local s_HasLevel, s_HasOurs = false, false
	for _, l_Bundle in ipairs(p_Bundles) do
		if l_Bundle == s_Level then
			s_HasLevel = true
		end
		if l_Bundle == m_Bundle then
			s_HasOurs = true
		end
	end

	if not s_HasLevel or s_HasOurs then
		return
	end

	-- ⛔ AFTER THE LEVEL'S OWN BUNDLE, AND THAT IS THE WHOLE DELIVERY. A shader name is answered by the
	-- database registered LAST: the engine appends each one and then walks them BACKWARDS, keeping the first
	-- hit. Going first (which this did) hands every vanilla name straight back to the level's own database,
	-- so the mod installs, reports success and changes nothing.
	local s_New = {{}}
	for _, l_Bundle in ipairs(p_Bundles) do
		s_New[#s_New + 1] = l_Bundle
		if l_Bundle == s_Level then
			s_New[#s_New + 1] = m_Bundle
		end
	end

	LOG(""INSERTED "" .. m_Bundle .. "" after the level's own (comp="" .. tostring(p_Compartment)
		.. "", n="" .. tostring(#p_Bundles) .. "")"")
	p_Hook:Pass(s_New, p_Compartment)
end)
";
    }
}
