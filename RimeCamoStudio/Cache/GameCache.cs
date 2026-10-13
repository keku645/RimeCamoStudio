using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using RimeCamoStudio.Catalog;

namespace RimeCamoStudio.Cache;

/// <summary>
/// Pulls the weapons out of the game once, so the studio can show them without the game afterwards.
///
/// ⛔ ONE MOUNT, EVERY DUMP. Mounting is the expensive part — minutes — and dumping a mesh once mounted is
/// cheap, so the whole catalogue goes into a single script rather than one process per weapon. A per-weapon
/// loop would turn a single wait into an hour of them.
/// </summary>
public static class GameCache
{
    /// <summary>
    /// The level whose shaderdb the mesh dumps are resolved against. Weapons live in every level, so this is
    /// only about having A database to name sections with — this one is where the whole camo saga was
    /// measured, so it is the one known to hold the weapon presets.
    /// </summary>
    public const string ShaderDbLevel = "xp2_skybar";

    public sealed class Result
    {
        public int Wanted { get; set; }
        public int Written { get; set; }
        public string ScriptPath { get; init; } = "";
        public string LogPath { get; init; } = "";
        public List<string> Missing { get; } = new();
    }

    /// <summary>
    /// ⛔ SANITISED, NOT "slashes to underscores". The editor names its cache files by turning EVERY
    /// non-alphanumeric character into '_', so a weapon with a hyphen — scar-l, dao-12, qbz-95b — was cached
    /// under a name the editor never looks for, and picking it mounted the game to dump a mesh that was
    /// already on disk. Eight weapons, all of them the ones with punctuation in their path.
    /// </summary>
    public static string MeshFile(string p_Mesh) =>
        Path.Combine(Settings.MeshCache, Sanitize(p_Mesh) + ".rsm");

    /// <summary>
    /// The dump format every consumer reads; its magic is the version stamp of a cached file. RSM7 adds a
    /// THIRD uv slot per vertex (the jets' TexCoord2), written in the order the section's shader hands the
    /// sets over (2026-09-22).
    /// </summary>
    public const string MeshMagic = "RSM7";

    /// <summary>
    /// The previous stamp, still accepted. RSM5 only ADDS the second texture coordinate set per vertex, which
    /// a one-UV subject does not have anyway — so every weapon and accessory already on disk stays valid and
    /// nobody pays a re-dump for a field their shaders never read.
    /// </summary>
    public const string MeshMagicLegacy = "RSM4";

    /// <summary>
    /// Every stamp a cached dump may carry and still be USABLE. RSM6 only ADDS the part index per vertex
    /// (what splits a composite section into the objects the game equips separately), so a weapon or an
    /// accessory dumped as RSM5 is as good as it ever was — and re-dumping the 59 weapons and 179
    /// accessories to gain a field their meshes do not even have is exactly the cost this list avoids. The
    /// vehicles are re-dumped on purpose, with `--cachevehicles --redump`.
    /// </summary>
    public static readonly string[] MeshMagicAccepted = { "RSM7", "RSM6", "RSM5", "RSM4" };

    /// <summary>
    /// The skeleton every hand-held weapon is skinned to (measured: the only weapon skeleton in the game; the
    /// blueprints name none of their own), which a weapon's idle pose is composed on.
    /// </summary>
    public const string WeaponSkeleton = "animations/skeletons/weapon/weaponske01";

    /// <summary>Where the per-weapon pose files land, beside the mesh dumps they assemble.</summary>
    public static string PoseFolder => Path.Combine(Settings.MeshCache, "poses");

    public static string PoseFile(string p_Mesh) => Path.Combine(PoseFolder, Sanitize(p_Mesh) + ".json");

    /// <summary>
    /// A weapon body dumped with NO pose — its parts where the mesh stores them, the bind pose — beside the assembled dump
    /// (meshcache/bind/&lt;mesh&gt;.rsm). Not for drawing: the emblem's projected slot is placed on the assembled weapon and READ by the game
    /// in the bind pose (the patched vertex shader hands over the input position), so the bake moves each square with its part from one
    /// to the other (EmblemSlot.InBindPose; a pistol's slide sits 26-30 cm away, measured 2026-10-07).
    /// </summary>
    public static string BindMeshFile(string p_Mesh) => Path.Combine(Settings.MeshCache, "bind", Sanitize(p_Mesh) + ".rsm");

    /// <summary>
    /// Whether a cached dump is of the CURRENT format. An older one (RSM3: the weapon's parts left where the
    /// mesh stores them, "disassembled") is treated as missing and dumped again — the editor does the same
    /// on a pick, so the two never disagree about what "cached" means.
    /// </summary>
    public static bool IsCurrentDump(string p_Path)
    {
        try
        {
            using var s_Stream = File.OpenRead(p_Path);
            var s_Magic = new byte[4];
            if (s_Stream.Read(s_Magic, 0, 4) != 4)
                return false;

            var s_Read = Encoding.ASCII.GetString(s_Magic);
            return MeshMagicAccepted.Contains(s_Read);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Dumps every weapon body mesh that is not cached yet, or cached in an older format. Already-cached
    /// meshes are skipped, so a run that was interrupted resumes instead of starting over.
    /// </summary>
    public static Result DumpMeshes(WeaponCatalog p_Weapons, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null)
    {
        Directory.CreateDirectory(Settings.MeshCache);

        // Both body meshes of every weapon: the first-person one feeds the PREVIEW, and the third-person one
        // is what the bake composes the stickers onto for that model — same weapon, its own unwrap (the
        // F2000's 3P maps its sides in mirror; a layer composed on the 1P landed on the wrong side in-game,
        // 2026-09-12). Posed through the same idle pose: the two share the weapon skeleton.
        var s_Meshes = p_Weapons.Weapons
            .SelectMany(p_W => p_W.BodyMeshes.Concat(p_W.PreviewMesh != null ? new[] { p_W.PreviewMesh } : Array.Empty<string>()))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return DumpMeshList(s_Meshes, true, "cache_meshes", p_GamePath, p_Repl, p_Log);
    }

    /// <summary>
    /// Dumps every ACCESSORY mesh of the catalogue that is not cached yet. Same cache, same file naming, same
    /// format as the weapon bodies — the preview and the bake read them the same way.
    ///
    /// ⛔ NO POSE. A weapon body is dumped assembled through its idle pose because its animated parts (magazine,
    /// bolt) are stored around their bones' rest positions; an accessory is shown ON ITS OWN, so it is dumped
    /// exactly as authored — for a skinned one (foregrip, bipod) that is its bind pose, which is the pose the
    /// artist modelled it in.
    /// </summary>
    public static Result DumpAccessoryMeshes(AccessoryCatalog p_Accessories, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null) =>
        DumpMeshList(p_Accessories.AllMeshes, false, "cache_accessories", p_GamePath, p_Repl, p_Log);

    /// <summary>
    /// Dumps every VEHICLE mesh of the catalogue — bodies, their mode variants and the gadget parts.
    ///
    /// ⛔ NO POSE, and there is no skeleton to pose it with: a vehicle body is a COMPOSITE mesh, and what
    /// assembles it are the PART TRANSFORMS its own mesh layout carries (measured on the LAV-25: 59 parts,
    /// each modelled around its own origin, each vertex naming its part). That is why this errand asks for
    /// the assembly (the last argument) — without it every hatch, wheel and weapon station is drawn at the
    /// centre of the hull, which is what "the vehicle comes up in pieces" looked like.
    ///
    /// ⛔ AND NOT AGAINST THE WEAPONS' SHADER DATABASE. Sections are named through the shaderdb of a level,
    /// and xp2_skybar is Close Quarters: it fields no vehicle at all, so every vehicle section would come
    /// back unnamed. See <see cref="VehicleShaderDbLevel"/>.
    /// </summary>
    /// <param name="p_Redump">
    /// Throws away the cached dumps first. Needed once, when what a dump CONTAINS changes without its stamp
    /// changing: a mesh already on disk is skipped by name, so an unassembled vehicle would stay unassembled
    /// forever. Bumping the format stamp instead would re-dump every weapon and accessory too.
    /// </param>
    /// <param name="p_Only">
    /// One vehicle's meshes instead of the whole catalogue. The rule for this front, and it is also the
    /// cheap way: re-dump only the one vehicle being verified, not all of them — a change is proved
    /// on ONE subject that can be looked at before it is spent on twenty that cannot.
    /// </param>
    public static Result DumpVehicleMeshes(VehicleCatalog p_Vehicles, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null, bool p_Redump = false, IEnumerable<string>? p_Only = null)
    {
        var s_Meshes = (p_Only ?? p_Vehicles.AllMeshes).ToList();
        if (p_Redump)
        {
            var s_Thrown = 0;
            foreach (var s_Mesh in s_Meshes)
                if (File.Exists(MeshFile(s_Mesh)))
                {
                    File.Delete(MeshFile(s_Mesh));
                    s_Thrown++;
                }

            p_Log?.Invoke($"Re-dumping: {s_Thrown} cached vehicle mesh(es) thrown away.");
        }

        return DumpMeshList(s_Meshes, false, "cache_vehicles", p_GamePath, p_Repl, p_Log,
            VehicleShaderDbLevel, true);
    }

    /// <summary>
    /// The one dump routine behind both catalogues: one mount, one script, every mesh that is missing or of
    /// an older format. Kept single on purpose — two routines producing the same cache file is how a format
    /// bump reaches one of them and not the other.
    /// </summary>
    private static Result DumpMeshList(IEnumerable<string> p_Meshes, bool p_Posed, string p_ScriptName,
        string p_GamePath, string p_Repl, Action<string>? p_Log, string? p_ShaderDbLevel = null,
        bool p_Parts = false)
    {
        // (a weapon body also wants its bind-pose dump: one already cached assembled but without it is dumped for that alone)
        bool Stale(string p_Mesh) => !File.Exists(MeshFile(p_Mesh)) || !IsCurrentDump(MeshFile(p_Mesh));
        var s_Meshes = p_Meshes
            .Where(p_M => Stale(p_M) || p_Posed && !File.Exists(BindMeshFile(p_M)))
            .ToList();

        var s_Result = new Result
        {
            Wanted = s_Meshes.Count,
            ScriptPath = Path.Combine(Settings.CacheFolder, p_ScriptName + ".rime"),
            LogPath = Path.Combine(Settings.CacheFolder, p_ScriptName + ".log"),
        };

        if (s_Meshes.Count == 0)
        {
            p_Log?.Invoke($"Every {(p_Posed ? "weapon" : "accessory")} mesh is already cached.");
            return s_Result;
        }

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");

        // ⛔ mount_game does NOT switch context on its own: without select_game every game command answers
        // "Command not found", which reads exactly like a missing command rather than a missing context.
        s_Script.AppendLine("select_game 1");

        // Each weapon first gets its POSE (the idle pose of its own animation package on the shared weapon
        // skeleton), then its mesh dumped assembled through it — without the pose every animated part sits
        // where the mesh stores it, around its bone's rest position, and the weapon comes out disassembled.
        var s_Level = p_ShaderDbLevel ?? ShaderDbLevel;
        var s_Db = $"levels/{s_Level}/{s_Level}/shaderdb";

        // Which shaders hand their two UV sets over SWAPPED — derived, never a list of names: a shader that
        // reads TWO HALVES of the same interpolator is one whose vertex shader does `mov o.xyzw, v.zwxy`
        // (checked against the game's own bytecode for `vehiclepreset_mud`, with the `mov o.xy, v.xyxx` of
        // the lights, the slat cage and the kits as the control). The preview cannot do this per section —
        // it compiles ONE vertex shader for the whole object — so the swap travels in the dump.
        var s_Swap = p_Parts ? ShadersThatSwapUv(p_Log) : new List<string>();
        var s_SwapArg = s_Swap.Count == 0 ? "-" : string.Join(",", s_Swap);
        if (s_Swap.Count > 0)
            p_Log?.Invoke($"UV sets handed over reordered by {s_Swap.Count} shader entr(ies): {string.Join(", ", s_Swap)}");
        Directory.CreateDirectory(PoseFolder);
        if (p_Posed)
            Directory.CreateDirectory(Path.GetDirectoryName(BindMeshFile("x"))!);
        foreach (var s_Mesh in s_Meshes)
        {
            if (p_Posed)
            {
                if (Stale(s_Mesh))
                {
                    s_Script.AppendLine($"dump_weapon_pose {s_Mesh} {WeaponSkeleton} \"{PoseFile(s_Mesh)}\"");
                    s_Script.AppendLine($"dump_mesh_sections {s_Mesh} \"{MeshFile(s_Mesh)}\" {s_Db} \"{PoseFile(s_Mesh)}\"");
                }

                // …and the same mesh with no pose file: the bind pose (BindMeshFile)
                if (!File.Exists(BindMeshFile(s_Mesh)))
                    s_Script.AppendLine($"dump_mesh_sections {s_Mesh} \"{BindMeshFile(s_Mesh)}\" {s_Db}");
            }
            else
            {
                // '-' is "no skeleton"; the flag after it assembles a COMPOSITE mesh with its own part
                // transforms (vehicles). An accessory is a single rigid piece and asks for neither.
                // '-' is "no skeleton", then the assembly of a COMPOSITE mesh (vehicles). The UV sets are
                // NOT reordered here: the dump carries both in the order the declaration names them, and
                // the preview's vertex shader feeds each one to the half the target shader reads it from.
                // (An earlier version swapped them at dump time; with the shader doing it too that was a
                // double swap — the fix belongs in ONE place, and it is the one that knows the shader.)
                s_Script.AppendLine(p_Parts
                    ? $"dump_mesh_sections {s_Mesh} \"{MeshFile(s_Mesh)}\" {s_Db} - true 0 \"{s_SwapArg}\""
                    : $"dump_mesh_sections {s_Mesh} \"{MeshFile(s_Mesh)}\" {s_Db}");
            }
        }

        s_Script.AppendLine("exit");
        File.WriteAllText(s_Result.ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Dumping {s_Meshes.Count} mesh(es) in one mount — this takes minutes.");
        var s_Output = Run(p_Repl, s_Result.ScriptPath);

        // APPENDED, never replaced: the log is also where the studio reads each mesh's vertex declaration
        // from (the AUG's four-TexCoord warning), and a run that re-dumps ONE stale mesh must not erase the
        // other fifty-eight — it did, and the warning vanished until the next full run.
        File.AppendAllText(s_Result.LogPath, $"{Environment.NewLine}=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {s_Meshes.Count} mesh(es) ==={Environment.NewLine}{s_Output}");

        // What landed on disk is the measure, not what the log claims: a dump that fails still prints a line.
        foreach (var s_Mesh in s_Meshes)
        {
            if (File.Exists(MeshFile(s_Mesh)) && (!p_Posed || File.Exists(BindMeshFile(s_Mesh))))
                s_Result.Written++;
            else
                s_Result.Missing.Add(s_Mesh);
        }

        return s_Result;
    }

    /// <summary>The soldier's first-person arms drawn around a weapon in the "1st person" view: the US soldiers' (the catalogue's first 1P arms).</summary>
    public const string FirstPersonArmsMesh = "characters/arms/arms1p_bareglove03/arms1p_bareglove03_mesh";

    /// <summary>The 1P skeleton the arms are skinned to, and whose camera bone is the first-person eye.</summary>
    public const string FirstPersonSkeleton = "animations/skeletons/venice1pske01";

    /// <summary>Where the arms posed around one weapon land: meshcache/firstperson/&lt;weapon mesh&gt;.arms.rsm (the pose file beside it).</summary>
    public static string ArmsFile(string p_WeaponMesh) => Path.Combine(Settings.MeshCache, "firstperson", Sanitize(p_WeaponMesh) + ".arms.rsm");

    /// <summary>
    /// The first-person eye of every weapon measured so far, one entry per first-person mesh (Rime's dump_weapon_camera merges each
    /// weapon into it): read by Catalog.FirstPersonCameras.
    /// </summary>
    public static string CameraFile => Path.Combine(Settings.MeshCache, "firstperson", "cameras.json");

    /// <summary>
    /// What the "1st person" view needs from the game for each weapon that lacks it in the cache, in ONE mount: its eye (dump_weapon_camera:
    /// the soldier's 1P skeleton posed by the weapon's idle pose, its camera bone taken into the weapon mesh's space) and, with
    /// <paramref name="p_Arms"/>, the soldier's arms posed around it (dump_arms_pose, then the arms mesh dumped through that pose — the
    /// hands land on the grip and the handguard, in the units the weapon's own dump is in, so the preview draws the two together).
    /// </summary>
    public static Result DumpFirstPerson(IEnumerable<string> p_WeaponMeshes, string p_GamePath, string p_Repl, Action<string>? p_Log = null,
        bool p_Arms = true)
    {
        bool NeedsEye(string p_Mesh) => Catalog.FirstPersonCameras.Of(p_Mesh) == null;
        bool NeedsArms(string p_Mesh) => p_Arms && !File.Exists(ArmsFile(p_Mesh));
        var s_Meshes = p_WeaponMeshes.Distinct(StringComparer.OrdinalIgnoreCase).Where(p_M => NeedsEye(p_M) || NeedsArms(p_M)).ToList();
        var s_Result = new Result
        {
            Wanted = s_Meshes.Count,
            ScriptPath = Path.Combine(Settings.CacheFolder, "cache_firstperson.rime"),
            LogPath = Path.Combine(Settings.CacheFolder, "cache_firstperson.log"),
        };

        if (s_Meshes.Count == 0)
        {
            p_Log?.Invoke($"The first-person {(p_Arms ? "eye and arms are" : "eye is")} cached for every weapon asked for.");
            return s_Result;
        }

        Directory.CreateDirectory(Path.Combine(Settings.MeshCache, "firstperson"));
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        var s_Db = $"levels/{ShaderDbLevel}/{ShaderDbLevel}/shaderdb";
        foreach (var s_Mesh in s_Meshes)
        {
            if (NeedsEye(s_Mesh))
                s_Script.AppendLine($"dump_weapon_camera {s_Mesh} {FirstPersonSkeleton} {WeaponSkeleton} \"{CameraFile}\"");

            if (NeedsArms(s_Mesh))
            {
                var s_Pose = Path.ChangeExtension(ArmsFile(s_Mesh), ".json");
                s_Script.AppendLine($"dump_arms_pose {s_Mesh} {FirstPersonSkeleton} \"{s_Pose}\"");
                s_Script.AppendLine($"dump_mesh_sections {FirstPersonArmsMesh} \"{ArmsFile(s_Mesh)}\" {s_Db} \"{s_Pose}\"");
            }
        }

        s_Script.AppendLine("exit");
        File.WriteAllText(s_Result.ScriptPath, s_Script.ToString());
        p_Log?.Invoke($"Reading the first-person {(p_Arms ? "eye and arms" : "eye")} of {s_Meshes.Count} weapon(s) in one mount — this takes a minute or more.");
        var s_Output = Run(p_Repl, s_Result.ScriptPath);
        File.AppendAllText(s_Result.LogPath, $"{Environment.NewLine}=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {s_Meshes.Count} weapon(s) ==={Environment.NewLine}{s_Output}");

        // what landed on disk is the measure, not what the log claims
        foreach (var s_Mesh in s_Meshes)
        {
            if (!NeedsEye(s_Mesh) && !NeedsArms(s_Mesh))
                s_Result.Written++;
            else
                s_Result.Missing.Add(s_Mesh);
        }

        return s_Result;
    }

    /// <summary>
    /// Asks the game which textures the weapons' database entries bind, and caches the answer.
    ///
    /// ⛔ THE ONLY HONEST SOURCE. The names cannot be derived from the weapon (Weapons/&lt;X&gt;/&lt;X&gt;_D):
    /// a quarter of the weapons are already named differently from their folder, and an entry also binds the
    /// camo patterns of every variation the weapon already ships.
    /// </summary>
    public static List<MaterialBinding> DiscoverBindings(string p_GamePath, string p_Repl,
        Action<string>? p_Log = null, bool p_Force = false)
    {
        Directory.CreateDirectory(Settings.CacheFolder);
        var s_ScanPath = Path.Combine(Settings.CacheFolder, "material_scan.txt");

        if (!p_Force && File.Exists(s_ScanPath))
        {
            p_Log?.Invoke("Using the cached material scan.");
            return MaterialScan.ParseFile(s_ScanPath);
        }

        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "scan_materials.rime");
        File.WriteAllText(s_ScriptPath,
            $"mount_game \"{p_GamePath}\" Frostbite2_0 true\nselect_game 1\n" +
            $"dump_shader_material_textures {WeaponMvdb} WeaponPreset weapons/\nexit\n");

        p_Log?.Invoke("Scanning the weapon materials — this mounts the game, so it takes minutes.");
        File.WriteAllText(s_ScanPath, Run(p_Repl, s_ScriptPath));

        return MaterialScan.ParseFile(s_ScanPath);
    }

    /// <summary>
    /// The scan above asks the database only for the WeaponPreset materials, but a weapon body also wears OTHER shaders, and
    /// a section drawn with one of them takes its art from what this mesh binds to that shader (the window's foreign path:
    /// the shader's slot map, then the scan on top). Without them the section has no art of its own — measured 2026-10-06 on
    /// the crossbow (reported as looking broken, with wrong uvs): its bow (`preset_metal_xp4`, 7,450 triangles) binds xp4_crossbow_bow_d /
    /// _rgb / _n and its bolt (`projectile_preset`) c4_explosives_d / _s in the weapons' own database, the scan had neither,
    /// and the bow drew with nothing in its colour, specular and normal slots. The same gap holds smaller on every weapon
    /// (bullets_base on 102 body meshes, aimingdots 54, the tapes 24).
    /// So: every shader a catalogue body wears that the scan holds no material of, asked once per shader in ONE mount
    /// (`dump_shader_material_textures {WeaponMvdb} &lt;shader leaf&gt; weapons/`), its lines APPENDED to the same scan file —
    /// the one the studio reads and the textures are dumped from. The shaders asked are recorded beside it, so a shader the
    /// database binds nowhere is not asked again on every errand. Returns the scan as it stands after.
    /// </summary>
    public static List<MaterialBinding> AppendSectionBindings(IEnumerable<string> p_BodyMeshes, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null)
    {
        var s_ScanPath = Path.Combine(Settings.CacheFolder, "material_scan.txt");
        var s_AskedPath = Path.Combine(Settings.CacheFolder, "material_scan_sections.txt");
        var s_Bindings = File.Exists(s_ScanPath) ? MaterialScan.ParseFile(s_ScanPath) : new List<MaterialBinding>();
        var s_Asked = File.Exists(s_AskedPath)
            ? File.ReadAllLines(s_AskedPath).Where(p_L => p_L.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var s_Scanned = s_Bindings.Select(p_B => p_B.Shader.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var s_Missing = p_BodyMeshes
            .SelectMany(SectionShadersOf)
            .Select(p_S => p_S.Replace('\\', '/'))
            .Where(p_S => p_S.Length > 0 && !s_Scanned.Contains(p_S) && !s_Asked.Contains(p_S))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p_S => p_S, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (s_Missing.Count == 0)
        {
            p_Log?.Invoke("Every shader the weapon bodies wear is in the material scan.");
            return s_Bindings;
        }

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        foreach (var s_Shader in s_Missing)
            s_Script.AppendLine($"dump_shader_material_textures {WeaponMvdb} {s_Shader[(s_Shader.LastIndexOf('/') + 1)..]} weapons/");
        s_Script.AppendLine("exit");
        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "scan_section_materials.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Scanning the materials of {s_Missing.Count} other shader(s) the weapon bodies wear " +
                      $"({string.Join(", ", s_Missing.Select(p_S => p_S[(p_S.LastIndexOf('/') + 1)..]))}) — this mounts the game.");
        var s_Output = Run(p_Repl, s_ScriptPath);
        var s_Lines = s_Output.Split('\n').Select(p_L => p_L.TrimEnd('\r')).Where(p_L => p_L.StartsWith("SHMAT", StringComparison.Ordinal)).ToList();
        File.AppendAllText(s_ScanPath, $"{Environment.NewLine}# section shaders, {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                                       string.Join(Environment.NewLine, s_Lines) + Environment.NewLine);
        File.AppendAllLines(s_AskedPath, s_Missing);

        var s_After = MaterialScan.ParseFile(s_ScanPath);
        p_Log?.Invoke($"Section scan: {s_After.Count - s_Bindings.Count} more material binding(s) from {s_Missing.Count} shader(s).");
        return s_After;
    }

    /// <summary>
    /// The database the scan reads and a bake patches. One per map, and the gamemode one rather than the
    /// level's own: that is where the weapon entries live, and it is what the working mod already uses.
    /// </summary>
    public const string WeaponMvdb = "levels/xp2_skybar/domination/meshvariationdb_win32";

    /// <summary>
    /// The level whose shaderdb names the VEHICLE sections, and whose databases bind their materials.
    ///
    /// ⛔ NOT THE WEAPONS' ONE. Measured over the game's 828 mesh-variation databases: xp2_skybar (Close
    /// Quarters) names not a single vehicle body, while xp3_desert names 15 of the 21 — every tank, both
    /// helicopter families and the jets. A dump against a level that does not field the vehicle comes back
    /// with its sections unnamed, which reads exactly like a mesh with no shader.
    /// </summary>
    public const string VehicleShaderDbLevel = "xp3_desert";

    /// <summary>
    /// Where a vehicle shader the vehicle level does not hold is looked for, in this order, in ONE mount.
    ///
    /// ⛔ MEASURED, 2026-09-22: the F-35B's own shaders (`vehicles/xpack01/f35/…`) are in none of xp3_desert,
    /// xp3_alborz, xp3_shield, xp3_valley, xp1_001, xp1_003 or xp5_001–004 — only xp1_002 (Gulf of Oman) and
    /// xp1_004 (Wake Island) hold them. Its mesh is in no mesh-variation database either (no material of it
    /// takes a camo), so its `databases` cannot name the map; the shaderdbs themselves are the only witness.
    /// A name here that is not a level just answers "could not find shaderdb" and costs nothing.
    /// </summary>
    public static readonly string[] VehicleShaderDbSweep =
    {
        "xp3_desert", "xp3_alborz", "xp3_shield", "xp3_valley", "xp1_001", "xp1_002", "xp1_003", "xp1_004",
        "xp5_001", "xp5_002", "xp5_003", "xp5_004", "mp_007", "mp_012", "mp_018", "mp_013", "mp_017", "mp_001",
        "mp_003", "mp_011", "mp_subway", "xp4_quake", "xp4_fd", "xp4_parl", "xp4_rubble",
    };

    private static string ShaderDbLevelsFile => Path.Combine(Settings.CacheFolder, "shaderdb_levels.txt");

    /// <summary>Every level a shaderdb sweep has recorded a shader in (see <see cref="SweepShaderDbLevel"/>).</summary>
    public static IEnumerable<string> SweptShaderDbLevels() =>
        File.Exists(ShaderDbLevelsFile)
            ? File.ReadAllLines(ShaderDbLevelsFile)
                .Select(p_L => p_L.Split('\t'))
                .Where(p_P => p_P.Length == 2 && p_P[1].Length > 0)
                .Select(p_P => p_P[1])
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

    /// <summary>
    /// The level whose shaderdb holds this shader: the one a sweep found and recorded, else the family's.
    /// Everything that reads a shader out of the game asks here — the fetch, the translation and the sampler
    /// read — so a shader found once in another map is not looked for in the wrong one by the next errand.
    /// </summary>
    public static string ShaderDbLevelOf(string p_Shader, string p_Default)
    {
        if (!File.Exists(ShaderDbLevelsFile))
            return p_Default;

        foreach (var s_Line in File.ReadAllLines(ShaderDbLevelsFile))
        {
            var s_Parts = s_Line.Split('\t');
            if (s_Parts.Length == 2 && s_Parts[0].Equals(p_Shader, StringComparison.OrdinalIgnoreCase))
                return s_Parts[1];
        }

        return p_Default;
    }

    /// <summary>
    /// Looks for a shader in every level of <see cref="VehicleShaderDbSweep"/> in one mount and records the
    /// first that holds it EXACTLY (the extract matches by substring: xp1_002 answers "f35b_main" with both
    /// F35B_Main_LOD and F35B_Main) with a pixel shader. Null when no level does.
    /// </summary>
    public static string? SweepShaderDbLevel(string p_GamePath, string p_Repl, string p_Shader, Action<string>? p_Log) =>
        SweepShaderDbLevels(p_GamePath, p_Repl, new[] { p_Shader }, p_Log).GetValueOrDefault(p_Shader);

    /// <summary>
    /// <see cref="SweepShaderDbLevel"/> for many shaders in the SAME one mount: every shader looked for in every level of the sweep,
    /// each recorded where it is found. Shader → the level found (null when none holds it).
    /// </summary>
    public static Dictionary<string, string?> SweepShaderDbLevels(string p_GamePath, string p_Repl, IReadOnlyList<string> p_Shaders,
        Action<string>? p_Log)
    {
        var s_Result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (p_Shaders.Count == 0)
            return s_Result;

        var s_Root = Path.Combine(Settings.CacheFolder, "shaderdb_sweep");
        string WorkOf(string p_Shader) => Path.Combine(s_Root, Sanitize(p_Shader));

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        foreach (var s_Shader in p_Shaders)
        {
            var s_Work = WorkOf(s_Shader);
            if (Directory.Exists(s_Work))
                try { Directory.Delete(s_Work, true); } catch { }
            Directory.CreateDirectory(s_Work);
            foreach (var s_Level in VehicleShaderDbSweep)
                s_Script.AppendLine($"extract_shader_dxbc levels/{s_Level}/{s_Level}/shaderdb \"{s_Shader}\" \"{Path.Combine(s_Work, s_Level)}\"");
        }

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(s_Root, "sweep.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());
        p_Log?.Invoke(p_Shaders.Count == 1
            ? $"  '{p_Shaders[0]}' is not in {VehicleShaderDbLevel}'s shaderdb - looking for it in {VehicleShaderDbSweep.Length} levels (one mount)…"
            : $"  {p_Shaders.Count} shader(s) are not in {VehicleShaderDbLevel}'s shaderdb - looking for them in {VehicleShaderDbSweep.Length} " +
              "levels (one mount)…");
        Run(p_Repl, s_ScriptPath);

        var s_Lines = File.Exists(ShaderDbLevelsFile) ? File.ReadAllLines(ShaderDbLevelsFile).ToList() : new List<string>();
        foreach (var s_Shader in p_Shaders)
        {
            var s_Work = WorkOf(s_Shader);
            string? s_Found = null;
            var s_Holders = new List<string>();
            foreach (var s_Level in VehicleShaderDbSweep)
            {
                var s_Folder = Path.Combine(s_Work, s_Level);
                var s_Index = Path.Combine(s_Folder, "index.txt");
                if (File.Exists(s_Index))
                {
                    var s_Row = File.ReadAllLines(s_Index)
                        .Select(p_L => p_L.Split('\t'))
                        .FirstOrDefault(p_P => p_P.Length >= 2 && p_P[1].Equals(s_Shader, StringComparison.OrdinalIgnoreCase));
                    if (s_Row == null)
                        continue;
                    s_Folder = Path.Combine(s_Folder, s_Row[0]);
                }

                if (!Directory.Exists(s_Folder) || Directory.GetFiles(s_Folder, "*_ps.dxbc").Length == 0)
                    continue;

                s_Holders.Add(s_Level);
                s_Found ??= s_Level;
            }

            try { Directory.Delete(s_Work, true); } catch { }

            s_Result[s_Shader] = s_Found;
            if (s_Found == null)
            {
                p_Log?.Invoke($"  '{s_Shader}': no level of the sweep holds it.");
                continue;
            }

            s_Lines.RemoveAll(p_L => p_L.Split('\t')[0].Equals(s_Shader, StringComparison.OrdinalIgnoreCase));
            s_Lines.Add($"{s_Shader}\t{s_Found}");
            p_Log?.Invoke($"  '{s_Shader}': held by {string.Join(", ", s_Holders)} - fetched from {s_Found} from now on.");
        }

        if (s_Result.Values.Any(p_L => p_L != null))
            File.WriteAllLines(ShaderDbLevelsFile, s_Lines);
        return s_Result;
    }

    /// <summary>
    /// The levels whose databases are scanned for vehicle materials, in one mount.
    ///
    /// ⛔ ONE LEVEL IS NOT ENOUGH, and that is the difference from weapons. Every map fields every weapon;
    /// no map fields every vehicle. Measured cover: xp3_desert 15, xp3_alborz adds the Stryker, and the
    /// LAV-25, the BTR-90 and the two air-drop variants are named only by base-game, XP1 and XP5 levels.
    /// Given as level PREFIXES: the scan expands each into every sublevel database under it, which is
    /// where the gamemode entries live.
    /// </summary>
    public static readonly string[] VehicleMvdbLevels =
    {
        "levels/xp3_desert", "levels/xp3_alborz", "levels/mp_001", "levels/xp1_001", "levels/xp5_001",
    };

    /// <summary>
    /// Asks the game which textures and values the VEHICLES' database entries bind, and caches the answer
    /// in its own scan file beside the weapons'.
    ///
    /// ⛔ A SEPARATE FILE, NOT APPENDED TO THE WEAPONS' SCAN. The two are refreshed by different errands
    /// (a weapon cache run must not have to re-scan the vehicles, and the other way round), and a single
    /// file that either run can rewrite is how half of it goes missing.
    /// </summary>
    public static List<MaterialBinding> DiscoverVehicleBindings(string p_GamePath, string p_Repl,
        Action<string>? p_Log = null, bool p_Force = false)
    {
        Directory.CreateDirectory(Settings.CacheFolder);
        var s_ScanPath = Path.Combine(Settings.CacheFolder, VehicleScanFile);

        if (!p_Force && File.Exists(s_ScanPath))
        {
            p_Log?.Invoke("Using the cached vehicle material scan.");
            return MaterialScan.ParseFile(s_ScanPath);
        }

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");

        // ⛔⛔ THE FILTER USED TO BE 'VehiclePreset', AND THAT IS WHY EVERY NON-CAMO SECTION WORE SOMEBODY
        // ELSE'S ART. It is a SUBSTRING of the shader's name, so it caught vehiclepreset_mud, _jet, _lights,
        // the 1uvset family — and MISSED every shader a vehicle wears that is not a preset: `kits_ru`,
        // `armorcage`, `tracks`, `*_infrared`, `*_decal`. Those sections then had NO binding of their own
        // ("0 from this mesh's materials" in the Output) and fell back to the slot map, which is keyed by
        // SHADER and carries the set of whichever object built it — the Sprut-SD's side panels drawn with
        // another vehicle's kit atlas, the lamps with the Humvee's (seen 2026-09-22, with the game beside
        // the studio). 'vehicles/' is a substring of EVERY vehicle shader's name, presets included — but NOT
        // of the WEAPON shader a vehicle's remote gun wears (`weapons/shaders/weaponpresetfp` on the BTR-90's
        // Remotegun_Base): that material had no scan row, so the studio dressed the gunner's station with the
        // missile launcher's textures (seen 2026-09-22, with the game open). The shader filter is a plain
        // substring match, and every resource name has a folder, so '/' means "any shader": what limits the
        // scan is the MESH filter, which still keeps it from opening the whole level.
        // ⛔ AND THE MAPS THE SHADERDB SWEEP FOUND A VEHICLE'S SHADERS IN: a map that holds them fields the
        // vehicle, so its databases bind its materials. The F-35B is fielded only by xp1_002/xp1_004 — none of
        // the five above — so its wheels and nozzle (`proppreset_metal`, bound to `us_f35b_parts_d/_n` there)
        // had no row and wore the slot map's art of another object (seen 2026-09-22, with the game beside it).
        var s_Levels = VehicleMvdbLevels
            .Concat(SweptShaderDbLevels().Select(p_L => $"levels/{p_L}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var s_Level in s_Levels)
            s_Script.AppendLine($"dump_shader_material_textures {s_Level} / vehicles/");

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "scan_vehicles.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Scanning the vehicle materials of {s_Levels.Count} level(s) — this mounts " +
                      "the game, so it takes minutes.");
        File.WriteAllText(s_ScanPath, Run(p_Repl, s_ScriptPath));

        return MaterialScan.ParseFile(s_ScanPath);
    }

    /// <summary>
    /// ⭐ THE VEHICLES NO SCAN HAS SEEN (2026-09-26, the Markaz batch: the Phoenix, the Rhino and the Barsuk are fielded only by XP4 levels,
    /// none of those the cached scan opened ⇒ no row: no art in the studio, no camo material, no piece). Their own rows, from the levels
    /// their databases live in, APPENDED to the scan file — each command scoped to the vehicle's own folder (<paramref name="p_Wanted"/>:
    /// level prefix, mesh name prefix), so no row of any other vehicle is added or moved: a full re-scan would re-read every vehicle from
    /// levels it was never read from.
    /// </summary>
    public static List<MaterialBinding> AppendVehicleBindings(string p_GamePath, string p_Repl,
        IReadOnlyList<(string Level, string MeshPrefix)> p_Wanted, Action<string>? p_Log = null)
    {
        var s_ScanPath = Path.Combine(Settings.CacheFolder, VehicleScanFile);
        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        foreach (var (s_Level, s_Prefix) in p_Wanted)
            s_Script.AppendLine($"dump_shader_material_textures {s_Level} / {s_Prefix}");
        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "scan_vehicles_added.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());
        p_Log?.Invoke($"Adding to the vehicle material scan: {string.Join(", ", p_Wanted.Select(p_W => $"{p_W.MeshPrefix} in {p_W.Level}"))} " +
                      "— this mounts the game.");

        var s_Output = Run(p_Repl, s_ScriptPath);
        var s_Separator = File.Exists(s_ScanPath) && !File.ReadAllText(s_ScanPath).EndsWith('\n') ? "\n" : "";
        File.AppendAllText(s_ScanPath, s_Separator + s_Output);
        return MaterialScan.ParseFile(s_ScanPath);
    }

    /// <summary>The vehicles' half of the material scan, beside the weapons' material_scan.txt.</summary>
    public const string VehicleScanFile = "material_scan_vehicles.txt";

    /// <summary>The soldiers' part of the material scan (the Soldiers tab), beside the weapons' and the vehicles'.</summary>
    public const string SoldierScanFile = "material_scan_soldiers.txt";

    /// <summary>The dump log of the soldier meshes: where their material ids (MESHDECL) are read from.</summary>
    public const string SoldierDumpLog = "cache_soldiers";

    /// <summary>
    /// Dumps every SOLDIER mesh of the catalogue — heads and headgear, torsos, legs, first-person arms.
    ///
    /// ⛔ NO POSE AND NO ASSEMBLY: a soldier's meshes are SKINNED, and a skinned mesh stores its vertices in model space around the
    /// skeleton's rest pose (what the weapons' foregrips and bipods already showed) — head, torso and legs share the soldier skeleton,
    /// so dumped as stored they meet as one character.
    /// ⛔ AND AGAINST TWO LEVELS, grouped by the level each mesh is drawn from (<see cref="SoldierCatalog.ShaderDbLevelOf"/>): the
    /// Aftermath soldiers' shaders (CharacterRoot_XP4…) exist only in Aftermath levels, so a single database would leave their
    /// sections unnamed, which reads exactly like a mesh with no shader.
    /// </summary>
    public static Result DumpSoldierMeshes(SoldierCatalog p_Soldiers, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null, bool p_Redump = false, IEnumerable<string>? p_Only = null)
    {
        var s_Meshes = (p_Only ?? p_Soldiers.AllMeshes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (p_Redump)
        {
            var s_Thrown = 0;
            foreach (var s_Mesh in s_Meshes)
                if (File.Exists(MeshFile(s_Mesh)))
                {
                    File.Delete(MeshFile(s_Mesh));
                    s_Thrown++;
                }

            p_Log?.Invoke($"Re-dumping: {s_Thrown} cached soldier mesh(es) thrown away.");
        }

        var s_Total = new Result
        {
            ScriptPath = Path.Combine(Settings.CacheFolder, SoldierDumpLog + ".rime"),
            LogPath = Path.Combine(Settings.CacheFolder, SoldierDumpLog + ".log"),
        };

        foreach (var s_Group in s_Meshes.GroupBy(p_M => p_Soldiers.ShaderDbLevelOf(p_M), StringComparer.OrdinalIgnoreCase))
        {
            p_Log?.Invoke($"Soldier meshes against {s_Group.Key}: {s_Group.Count()}.");
            var s_Part = DumpMeshList(s_Group, false, SoldierDumpLog, p_GamePath, p_Repl, p_Log, s_Group.Key);
            s_Total.Wanted += s_Part.Wanted;
            s_Total.Written += s_Part.Written;
            s_Total.Missing.AddRange(s_Part.Missing);
        }

        return s_Total;
    }

    /// <summary>
    /// Asks the game which textures and values the SOLDIERS' database entries bind (every mesh under characters/, any shader), from
    /// the databases the catalogue names — the base game's and Aftermath's — and caches the answer in its own scan file.
    ///
    /// ⛔ THE GAMEMODE DATABASES, NOT THE LEVEL'S OWN: soldier gear is recorded in the gamemode sublevels (CharacterRoot has zero
    /// materials in a level's base database — the note on dump_shader_material_textures), which is where the catalogue points.
    /// </summary>
    public static List<MaterialBinding> DiscoverSoldierBindings(SoldierCatalog p_Soldiers, string p_GamePath, string p_Repl,
        Action<string>? p_Log = null, bool p_Force = false)
    {
        Directory.CreateDirectory(Settings.CacheFolder);
        var s_ScanPath = Path.Combine(Settings.CacheFolder, SoldierScanFile);

        if (!p_Force && File.Exists(s_ScanPath))
        {
            p_Log?.Invoke("Using the cached soldier material scan.");
            return MaterialScan.ParseFile(s_ScanPath);
        }

        var s_Databases = p_Soldiers.Soldiers.Select(p_S => p_S.Database).Where(p_D => p_D.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        foreach (var s_Database in s_Databases)
            s_Script.AppendLine($"dump_shader_material_textures {s_Database} / characters/");
        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "scan_soldiers.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Scanning the soldier materials of {s_Databases.Count} database(s) — this mounts the game, so it takes minutes.");
        File.WriteAllText(s_ScanPath, Run(p_Repl, s_ScriptPath));

        return MaterialScan.ParseFile(s_ScanPath);
    }

    /// <summary>Same naming as everything else the caches hold — see <see cref="MeshFile"/>.</summary>
    public static string TextureFile(string p_Texture) =>
        Path.Combine(Settings.TextureCache, Sanitize(p_Texture) + ".dds");

    /// <summary>Dumps every texture the scan found that is not cached yet, in one mount.</summary>
    public static Result DumpTextures(IReadOnlyList<MaterialBinding> p_Bindings, string p_GamePath,
        string p_Repl, Action<string>? p_Log = null)
    {
        Directory.CreateDirectory(Settings.TextureCache);

        var s_Textures = MaterialScan.TexturesOf(p_Bindings)
            .Where(p_T => !File.Exists(TextureFile(p_T)))
            .ToList();

        var s_Result = new Result
        {
            Wanted = s_Textures.Count,
            ScriptPath = Path.Combine(Settings.CacheFolder, "cache_textures.rime"),
            LogPath = Path.Combine(Settings.CacheFolder, "cache_textures.log"),
        };

        if (s_Textures.Count == 0)
        {
            p_Log?.Invoke("Every texture is already cached.");
            return s_Result;
        }

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");
        foreach (var s_Texture in s_Textures)
            s_Script.AppendLine($"dump_texture \"{s_Texture}\" \"{TextureFile(s_Texture)}\"");

        s_Script.AppendLine("exit");
        File.WriteAllText(s_Result.ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Dumping {s_Textures.Count} texture(s) in one mount.");
        File.WriteAllText(s_Result.LogPath, Run(p_Repl, s_Result.ScriptPath));

        foreach (var s_Texture in s_Textures)
        {
            if (File.Exists(TextureFile(s_Texture)))
                s_Result.Written++;
            else
                s_Result.Missing.Add(s_Texture);
        }

        return s_Result;
    }

    /// <summary>
    /// The shader of each section of a cached mesh, in section order, straight from its dump — the ground
    /// truth for what the preview draws each section with. Empty when the mesh is not cached yet.
    ///
    /// Read with the dump's own layout (RSM4: per section a shader name, a material name, two ints, the
    /// vertices, the indices) and the geometry skipped over, so it costs a few kilobytes of reading per mesh.
    /// </summary>
    public static List<string> SectionShadersOf(string p_Mesh) =>
        SectionTrianglesOf(p_Mesh).Select(p_S => p_S.Shader).ToList();

    /// <summary>
    /// The shader and the triangle count of each section of a cached mesh, in section order — what draws how
    /// much of the object, for the one question a name cannot answer (which of a vehicle's own shaders is its
    /// body). Same read as <see cref="SectionShadersOf"/>, the index count kept instead of skipped over.
    /// </summary>
    public static List<(string Shader, int Triangles)> SectionTrianglesOf(string p_Mesh)
    {
        var s_Shaders = new List<(string Shader, int Triangles)>();
        var s_File = MeshFile(p_Mesh);
        if (!File.Exists(s_File))
            return s_Shaders;

        try
        {
            using var s_Reader = new BinaryReader(File.OpenRead(s_File));
            var s_Magic = new string(s_Reader.ReadChars(4));
            if (!MeshMagicAccepted.Contains(s_Magic))
                return s_Shaders;

            // Floats per vertex: position + one uv set (RSM4), + a second (RSM5/6), + a third slot (RSM7).
            var s_Floats = s_Magic switch { "RSM7" => 9L, "RSM5" or "RSM6" => 7L, _ => 5L };
            var s_HasParts = s_Magic is "RSM6" or "RSM7";

            var s_Count = s_Reader.ReadInt32();
            for (var i = 0; i < s_Count; i++)
            {
                var s_Shader = Encoding.UTF8.GetString(s_Reader.ReadBytes(s_Reader.ReadInt32())).Replace('\\', '/');
                s_Reader.ReadBytes(s_Reader.ReadInt32()); // material name
                s_Reader.ReadInt32();                     // category
                s_Reader.ReadInt32();                     // double-sided
                var s_Vertices = s_Reader.ReadInt32();
                s_Reader.BaseStream.Seek(s_Vertices * s_Floats * sizeof(float), SeekOrigin.Current);
                var s_Indices = s_Reader.ReadInt32();
                s_Reader.BaseStream.Seek(s_Indices * (long) sizeof(int), SeekOrigin.Current);
                s_Shaders.Add((s_Shader, s_Indices / 3));

                // …and the part index per vertex, or the next section header is read out of the middle of
                // this one's data — a reader that skips a field it does not use still has to skip it.
                if (s_HasParts)
                    s_Reader.BaseStream.Seek(s_Vertices * (long) sizeof(int), SeekOrigin.Current);
            }
        }
        catch (Exception)
        {
            // A dump of another format is not this method's to diagnose: the preview will say so.
        }

        return s_Shaders;
    }

    /// <summary>
    /// Every shader the cached weapon meshes wear, read back out of the dumps themselves.
    ///
    /// The names are stored as plain text inside each RSM section header, so they are recovered by scanning
    /// for printable runs containing "shaders/" rather than by decoding the geometry — the same read that
    /// showed the weapons wear ten different shaders, not two.
    /// </summary>
    public static List<string> ShadersInCache()
    {
        var s_Shaders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Settings.MeshCache))
            return s_Shaders.ToList();

        foreach (var s_File in Directory.GetFiles(Settings.MeshCache, "*.rsm"))
        {
            var s_Bytes = File.ReadAllBytes(s_File);
            var s_Run = new StringBuilder();

            foreach (var s_Byte in s_Bytes)
            {
                if (s_Byte >= 0x20 && s_Byte < 0x7F)
                {
                    s_Run.Append((char) s_Byte);
                    continue;
                }

                if (s_Run.Length >= 8 && s_Run.ToString().Contains("shaders/", StringComparison.Ordinal))
                    s_Shaders.Add(s_Run.ToString());

                s_Run.Clear();
            }
        }

        return s_Shaders.ToList();
    }

    /// <summary>
    /// Pulls every one of those shaders into the cache. One mount EACH — the editor's fetch builds its own
    /// script per shader — but there are only a handful of them, and paying it here means the user never
    /// waits mid-work.
    /// </summary>
    /// <param name="p_Also">
    /// Shaders that must be cached even though no cached mesh WEARS them.
    ///
    /// ⛔ THE TARGET IS NOT ALWAYS A SECTION SHADER. The studio opens the camo-capable TWIN of what a weapon
    /// wears, and for four weapons that twin (weaponpreset3p) appears in no dump — so a prefetch built only
    /// from the meshes left exactly those four mounting the game on the first click, which is the whole
    /// thing this prefetch exists to prevent.
    /// </param>
    /// <param name="p_Only">
    /// When given, the ONLY shaders fetched — used by the vehicle errand, which must not drag the weapon
    /// presets through a vehicle level's database (they are not in it, and the fetch would report them
    /// missing for the wrong reason).
    /// </param>
    /// <param name="p_ShaderDbLevel">The level whose shaderdb resolves them; the weapons' one by default.</param>
    public static Result DumpShaders(string p_GamePath, string p_Repl, Action<string>? p_Log = null,
        IEnumerable<string>? p_Also = null, IEnumerable<string>? p_Only = null,
        string? p_ShaderDbLevel = null)
    {
        // ⛔ A CALLER'S ORDER IS KEPT. Each shader costs a mount, so the order decides how soon the first
        // subjects become usable; sorting by name here silently threw away an order the caller had chosen
        // for exactly that reason (the vehicles' errand asks for the most shared presets first).
        var s_Given = (p_Only ?? ShadersInCache().OrderBy(p_S => p_S, StringComparer.OrdinalIgnoreCase))
            .Concat(p_Also ?? Enumerable.Empty<string>());

        var s_Shaders = s_Given.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var s_Folder = Path.Combine(Settings.MeshCache, "shaders");

        // ⛔ CACHED MEANS BOTH FILES. The editor fetches when EITHER the compiled shader or its slot map is
        // missing, so testing only the .dxbc marks as done a shader that still costs a mount — which is
        // exactly what happened: two presets left over from earlier editor sessions had a .dxbc and no
        // slot map, were skipped here, and mounted the game on the first click anyway.
        var s_Wanted = s_Shaders
            .Where(p_S => !File.Exists(ShaderFile(p_S)) || !File.Exists(SlotMapFile(p_S)))
            .ToList();

        var s_Result = new Result { Wanted = s_Wanted.Count };
        if (s_Wanted.Count == 0)
        {
            p_Log?.Invoke($"Every shader is already cached ({s_Shaders.Count} in use).");
            return s_Result;
        }

        p_Log?.Invoke($"Fetching {s_Wanted.Count} shader(s) — a few mounts for all of them, and only this once.");
        var s_DbLevel = p_ShaderDbLevel ?? ShaderDbLevel;

        // ⭐ ALL OF THEM IN A FEW MOUNTS (MainWindow.PrefetchSectionShaders): each shader is read the way it always was, but the reads of
        // every shader share one mount per round instead of three mounts each — 81 vehicle shaders took hours one by one
        var s_First = RimeShaderEditor.MainWindow.PrefetchSectionShaders(p_Repl, p_GamePath,
            s_Wanted.Select(p_S => ShaderDbLevelOf(p_S, s_DbLevel)).Select(p_L => $"levels/{p_L}/{p_L}/shaderdb").Zip(s_Wanted).ToList(),
            Settings.MeshCache, Settings.TextureCache, p_Log);

        // ⛔ NOT IN THIS MAP IS NOT "NOT IN THE GAME". A vehicle's own shaders live only in the shaderdb of a map that fields it, and the
        // vehicle level does not field every vehicle (the F-35B's six: "no pixel shader came out of the game" against xp3_desert,
        // 2026-09-22). Swept, recorded, and fetched again from where they really are — every one missed in ONE sweep and ONE batch
        // (one at a time it was four mounts each).
        var s_Misses = Enumerable.Range(0, s_Wanted.Count)
            .Where(i => s_First[i] != null && ShaderDbLevelOf(s_Wanted[i], s_DbLevel).Equals(VehicleShaderDbLevel, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (s_Misses.Count > 0)
        {
            var s_Found = SweepShaderDbLevels(p_GamePath, p_Repl, s_Misses.Select(i => s_Wanted[i]).ToList(), p_Log);
            var s_Again = s_Misses.Where(i => s_Found.GetValueOrDefault(s_Wanted[i]) != null).ToList();
            var s_Second = RimeShaderEditor.MainWindow.PrefetchSectionShaders(p_Repl, p_GamePath,
                s_Again.Select(i => ($"levels/{s_Found[s_Wanted[i]]}/{s_Found[s_Wanted[i]]}/shaderdb", s_Wanted[i])).ToList(),
                Settings.MeshCache, Settings.TextureCache, p_Log);
            for (var j = 0; j < s_Again.Count; j++)
                s_First[s_Again[j]] = s_Second[j];
        }

        for (var i = 0; i < s_Wanted.Count; i++)
        {
            var s_Shader = s_Wanted[i];
            var s_Error = s_First[i];

            if (s_Error == null && File.Exists(ShaderFile(s_Shader)) && File.Exists(SlotMapFile(s_Shader)))
            {
                s_Result.Written++;
                p_Log?.Invoke($"  {s_Shader}");
            }
            else
            {
                s_Result.Missing.Add($"{s_Shader} ({s_Error ?? "no dxbc came out"})");
            }
        }

        return s_Result;
    }

    /// <summary>
    /// Decodes the dumped DDS files into the PNG thumbnails the preview actually reads.
    ///
    /// ⛔ THE PREVIEW DOES NOT READ THE .dds. It looks for "&lt;sanitised asset name&gt;.png" in the texture
    /// cache, and a missing one is not an error anywhere — the mesh simply draws with no texture, which is
    /// what made every weapon come up GREEN. Dumping in the format the game hands over is only half the job;
    /// the cache has to hold the format the consumer opens.
    ///
    /// ⚠ Named from the ASSET, not from the .dds file: the two disagree on names containing spaces
    /// ("saiga 20k_d"), where the editor turns them into underscores and the dump kept them.
    /// </summary>
    public static Result ConvertTextures(IReadOnlyList<MaterialBinding> p_Bindings, Action<string>? p_Log = null)
    {
        var s_Textures = MaterialScan.TexturesOf(p_Bindings);
        var s_Result = new Result();

        foreach (var s_Texture in s_Textures)
        {
            var s_Png = Path.Combine(Settings.TextureCache, $"{Sanitize(s_Texture)}.png");
            if (File.Exists(s_Png))
                continue;

            s_Result.Wanted++;

            var s_Dds = TextureFile(s_Texture);
            if (!File.Exists(s_Dds))
            {
                s_Result.Missing.Add($"{s_Texture} (no dds)");
                continue;
            }

            try
            {
                var s_Image = RimeShaderEditor.View.DdsImage.Load(s_Dds);
                if (s_Image == null)
                {
                    s_Result.Missing.Add($"{s_Texture} (not decodable)");
                    continue;
                }

                var s_Encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                s_Encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(s_Image));
                using var s_Stream = File.Create(s_Png);
                s_Encoder.Save(s_Stream);
                s_Result.Written++;
            }
            catch (Exception s_Exception)
            {
                s_Result.Missing.Add($"{s_Texture} ({s_Exception.Message})");
            }
        }

        p_Log?.Invoke($"Thumbnails: {s_Result.Written} written, {s_Result.Missing.Count} failed, " +
                      $"{s_Textures.Count - s_Result.Wanted} already there.");

        return s_Result;
    }

    /// <summary>
    /// Fills in the SAMPLER ADDRESSING of every slot map already on disk — in ONE mount, whatever the count.
    ///
    /// ⛔ WHY IT IS A SEPARATE ERRAND: addressing arrived after the maps were built, and it is not worth a
    /// re-fetch of every shader (one mount EACH). The slot maps keep their version, so nothing is invalidated;
    /// a map without the field simply draws as it always did (wrapping) and SAYS so in the Output.
    ///
    /// The shaders are resolved against their own family's database — a vehicle's preset does not exist in the
    /// weapons' level, and asking the wrong one returns nothing at all (the same trap `--pretranslate` had).
    /// </summary>
    public static Result FillSamplers(string p_GamePath, string p_Repl, Action<string>? p_Log = null,
        IEnumerable<string>? p_Only = null)
    {
        // ⛔ NOT `ShadersInCache()` ALONE: that one keeps only strings containing "shaders/", which is a rule
        // about NAMES — and a vehicle's are `vehicles/common/kits/kits_ru`, `…/armorcage`, `…/tracks`,
        // `vehicles/lav25/lav25_decal`. Asked for "every cached shader" it silently left out exactly the
        // family this errand exists for (the kit atlas one), and the subject came back unfilled with a green
        // "34/35". The dumps' own SECTION list has no such rule.
        var s_Wanted = p_Only?.ToList();
        if (s_Wanted == null)
        {
            var s_FromSections = Directory.Exists(Settings.MeshCache)
                ? Directory.GetFiles(Settings.MeshCache, "*.rsm")
                    .SelectMany(p_F => SectionShadersOf(Path.GetFileNameWithoutExtension(p_F)))
                : Enumerable.Empty<string>();

            s_Wanted = ShadersInCache().Concat(s_FromSections).ToList();
        }

        var s_Shaders = s_Wanted
            .Where(p_S => p_S.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p_S => File.Exists(SlotMapFile(p_S)))
            .OrderBy(p_S => p_S, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var s_Result = new Result { Wanted = s_Shaders.Count };
        if (s_Shaders.Count == 0)
        {
            p_Log?.Invoke("No cached slot map to fill.");
            return s_Result;
        }

        var s_Script = new StringBuilder();
        s_Script.AppendLine($"mount_game \"{p_GamePath}\" Frostbite2_0 true");
        s_Script.AppendLine("select_game 1");

        foreach (var s_Shader in s_Shaders)
        {
            var s_Level = ShaderDbLevelOf(s_Shader, s_Shader.StartsWith("vehicles/", StringComparison.OrdinalIgnoreCase)
                ? VehicleShaderDbLevel
                : ShaderDbLevel);

            s_Script.AppendLine($"dump_shader_textures levels/{s_Level}/{s_Level}/shaderdb {s_Shader}");
        }

        s_Script.AppendLine("exit");

        var s_ScriptPath = Path.Combine(Settings.CacheFolder, "samplers.rime");
        File.WriteAllText(s_ScriptPath, s_Script.ToString());

        p_Log?.Invoke($"Reading the sampler states of {s_Shaders.Count} shader(s) — one mount for all of them.");
        var s_Output = Run(p_Repl, s_ScriptPath);

        foreach (var s_Shader in s_Shaders)
        {
            // Scoped to the block whose header names THIS shader: the dump matches by substring, so a sibling's
            // samplers would otherwise be written into this shader's map.
            var s_Scope = RimeShaderEditor.MainWindow.ScopeToShaderBlocks(s_Output, s_Shader);
            var s_Rows = System.Text.RegularExpressions.Regex
                .Matches(s_Scope, @"SHTEX-SAMPLER:\s*mode=(\S+)\s+register=s(\d+)\s+u=(\S+)\s+v=(\S+)\s+w=(\S+)")
                .Select(p_M => (Mode: p_M.Groups[1].Value, Register: p_M.Groups[2].Value,
                    Modes: $"{p_M.Groups[3].Value},{p_M.Groups[4].Value},{p_M.Groups[5].Value}"))
                .ToList();

            var s_Preferred = s_Rows.Where(p_R => p_R.Mode.Contains("GBuffer")).ToList();
            var s_Samplers = (s_Preferred.Count > 0 ? s_Preferred : s_Rows)
                .GroupBy(p_R => p_R.Register)
                .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Modes);

            if (s_Samplers.Count == 0)
            {
                s_Result.Missing.Add($"{s_Shader} (the database named no sampler state)");
                continue;
            }

            var s_MapPath = SlotMapFile(s_Shader);
            var s_Map = RimeShaderEditor.MainWindow.ReadSlotMapFull(s_MapPath);
            if (s_Map == null)
            {
                s_Result.Missing.Add($"{s_Shader} (its slot map no longer reads)");
                continue;
            }

            s_Map.Samplers = s_Samplers;
            File.WriteAllText(s_MapPath, System.Text.Json.JsonSerializer.Serialize(s_Map));
            s_Result.Written++;

            var s_Note = string.Join(" ", s_Samplers
                .OrderBy(p_S => p_S.Key, StringComparer.Ordinal)
                .Select(p_S => $"s{p_S.Key}={p_S.Value.Replace(",", "/")}"));
            p_Log?.Invoke($"  {s_Shader}: {s_Note}");
        }

        return s_Result;
    }

    /// <summary>Where a shader's compiled pixel shader lands once prefetched.</summary>
    public static string ShaderFile(string p_Shader) =>
        Path.Combine(Settings.MeshCache, "shaders", $"{Sanitize(p_Shader)}.dxbc");

    /// <summary>Its texture-slot map, the OTHER half of "this shader is cached".</summary>
    public static string SlotMapFile(string p_Shader) =>
        Path.Combine(Settings.TextureCache, RimeShaderEditor.MainWindow.SlotMapFileName(p_Shader));

    /// <summary>Both files present — the same test the editor makes before deciding to mount.</summary>
    public static bool IsShaderCached(string p_Shader) =>
        File.Exists(ShaderFile(p_Shader)) && File.Exists(SlotMapFile(p_Shader));

    /// <summary>The editor's own cache-file naming, for callers outside this class.</summary>
    public static string SanitizeName(string p_Text) => Sanitize(p_Text);

    /// <summary>The editor's own cache-file naming, so both tools find the same files.</summary>
    private static string Sanitize(string p_Text)
    {
        var s_Builder = new StringBuilder();
        foreach (var s_Char in p_Text)
            s_Builder.Append(char.IsLetterOrDigit(s_Char) ? s_Char : '_');

        return s_Builder.ToString().Trim('_');
    }

    /// <summary>
    /// The shaders whose vertex shader hands the two UV sets over SWAPPED, read off the TRANSLATED GRAPHS
    /// instead of named: a graph with two TexCoord nodes on the SAME interpolator (one .xy, one .zw) is a
    /// shader that uses both sets, and measured in the game's bytecode those are exactly the ones that do
    /// `mov o.xyzw, v.zwxy` — their .xy is the SECOND set. A one-UV shader takes .xy straight.
    ///
    /// ⛔ Derived, because a list of names is what left four vehicles out of a catalogue once already; a
    /// shader nobody has translated yet simply does not appear, which is exactly today's behaviour.
    /// ⛔ And the names are FULL resource names, matched by identity on the Rime side: two presets with the
    /// same last segment read the pair differently (see the BTR-90 note below).
    /// </summary>
    public static List<string> ShadersThatSwapUv() => ShadersThatSwapUv(null);

    /// <param name="p_Log">Where each shader's verdict and its evidence go (the dump log, or the console of --swaplist).</param>
    public static List<string> ShadersThatSwapUv(Action<string>? p_Log)
    {
        var s_Swap = new List<string>();
        var s_Folder = Path.Combine(Settings.CacheFolder, "camographs");
        if (!Directory.Exists(s_Folder))
            return s_Swap;

        var s_Translate = Path.Combine(Settings.CacheFolder, "translate");
        foreach (var s_File in Directory.EnumerateFiles(s_Folder, "*.json"))
            try
            {
                using var s_Document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(s_File));
                if (!s_Document.RootElement.TryGetProperty("TargetShader", out var s_Target) ||
                    !s_Document.RootElement.TryGetProperty("Nodes", out var s_Nodes) ||
                    s_Target.GetString() is not { Length: > 0 } s_Name)
                    continue;

                var s_Halves = new Dictionary<string, HashSet<string>>();
                foreach (var s_Node in s_Nodes.EnumerateArray())
                {
                    if (!s_Node.TryGetProperty("Kind", out var s_Kind) || s_Kind.GetString() != "TexCoord" ||
                        !s_Node.TryGetProperty("Params", out var s_Params) ||
                        !s_Params.TryGetProperty("Interp", out var s_Interp) ||
                        !s_Params.TryGetProperty("Half", out var s_Half))
                        continue;

                    var s_Key = s_Interp.GetString() ?? "";
                    if (!s_Halves.TryGetValue(s_Key, out var s_Set))
                        s_Halves[s_Key] = s_Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    s_Set.Add(s_Half.GetString() ?? "");
                }

                var s_TwoHalves = s_Halves.Values.Any(p_H => p_H.Contains("xy") && p_H.Contains("zw"));

                // ⛔⛔ THE VERTEX SHADER DECIDES, NOT THE GRAPH. "Reads two halves of the interpolator" was the
                // rule, checked against the bytecode of three shaders — and the fourth one that reads both
                // halves (`vehiclepreset1uvset_mud_decals`) hands them over STRAIGHT (`mov o4.xyzw, v3.xyzw`):
                // the rule would have swapped the Stryker's decals for nothing (2026-09-22). The translation
                // leaves every solution's vertex shader on disk, so the answer is read there: a shader swaps
                // when its VS puts the SECOND uv element first — `mov oN.xyzw, vK.zwxy` where one float4
                // carries both sets, or `mov oN.xy, v5` + `mov oN.zw, v3` where the bone indices sit between
                // the two Half2 (the BTR/Tunguska declaration). Measured on 8 vehicle shaders: the verdict is
                // the same in both declaration families, so one answer per shader holds.
                var s_Verdict = UvOrderOfVertexShaders(Path.Combine(s_Translate, Path.GetFileNameWithoutExtension(s_File)),
                    s_Name, out var s_Evidence, out var s_ByDeclaration);
                if (s_Verdict == null)
                {
                    // No vertex shader on disk: the graph decides, as it did before the bytecode was read.
                    p_Log?.Invoke($"  {s_Name}: no vertex shader on disk — the graph decides: {(s_TwoHalves ? "SWAP" : "straight")}");
                    if (s_TwoHalves)
                        s_Swap.Add(s_Name);
                    continue;
                }

                var s_Straight = IsStraightOrder(s_Verdict);
                p_Log?.Invoke($"  {s_Name}: {(s_Straight ? "straight" : $"order {s_Verdict.Replace(':', ',')}")} ({s_Evidence})" +
                              (s_TwoHalves == !s_Straight ? "" : $" — the graph {(s_TwoHalves ? "reads both halves" : "reads one half")}, the VS decides"));

                // ⛔ The FULL resource name, never the last segment: `vehicles/xpack01/shaders/vehiclepreset_mud`
                // (the BTR-90's preset, ONE uv set, `mov o4.xy, v3.xyxx`) shares its last segment with
                // `vehicles/shaders/vehiclepreset_mud`. Handed over as a short name, the swap landed on the
                // BTR's hull and missile launcher — whose second set is all zeros — and the whole vehicle
                // sampled one texel: dark, flat, no camo (2026-09-22).
                // The plain swap (1:0) travels as the bare name, as it always did; a three-slot order as
                // `name=a:b:c`.
                if (!s_Straight)
                    s_Swap.Add(s_Verdict == "1:0" ? s_Name : $"{s_Name}={s_Verdict}");

                // A declaration whose solutions hand the sets over in ANOTHER order than the shader's usual
                // one gets its own entry, which the dump matches by the section's declaration hash first.
                foreach (var (s_Declaration, s_Order) in s_ByDeclaration.Where(p_D => p_D.Value != s_Verdict).OrderBy(p_D => p_D.Key))
                {
                    p_Log?.Invoke($"      declaration {s_Declaration}: order {s_Order.Replace(':', ',')} — its own entry");
                    s_Swap.Add($"{s_Name}@{s_Declaration}={(IsStraightOrder(s_Order) ? "0:1" : s_Order)}");
                }
            }
            catch (Exception)
            {
                // A graph that cannot be read says nothing about swapping; the dump keeps the element order.
            }

        return s_Swap;
    }

    /// <summary>
    /// One shader's uv order as its translated vertex shaders show it ("1:0", "0", "1:2:0" — see
    /// <see cref="UvOrderOfVertexShaders"/>), for <paramref name="p_Declaration"/> ("0xC83353E0") when its solutions for that
    /// declaration hand the sets over in an order of their own. Null when the shader has no translation on disk
    /// (`--vehiclegraphs` makes one) — the caller must not guess straight: that is how the BTR-90 went dark.
    /// </summary>
    public static string? UvOrderOf(string p_Shader, string? p_Declaration, out string p_Evidence)
    {
        var s_Folder = Path.Combine(Settings.CacheFolder, "translate", Sanitize(p_Shader.ToLowerInvariant()));
        var s_Verdict = UvOrderOfVertexShaders(s_Folder, p_Shader, out p_Evidence, out var s_ByDeclaration);
        if (s_Verdict == null)
            return null;

        return p_Declaration != null && s_ByDeclaration.TryGetValue(p_Declaration, out var s_Own) ? s_Own : s_Verdict;
    }

    /// <summary>
    /// The order in which one translated shader's vertex shaders hand the uv sets to the pixel shader —
    /// "a:b" (the pair's .xy and .zw), "a:b:c" (plus the set a second uv interpolator carries) or "0" (one
    /// set, straight) — and, per vertex declaration, the orders that differ from it. Null when no vertex
    /// shader is on disk. <paramref name="p_Evidence"/> counts the solutions per order.
    ///
    /// ⭐ EXACT WHERE THE EXTRACTION LEFT ITS SIDECAR (solutions.txt beside the bytecode: per solution the
    /// declaration's elements and the vertex permutation's input layout, semantic:format@offset). A `vN` is
    /// then the declaration element at its byte offset — `v4 = R16G16B16A16@24` is TexCoord0+TexCoord1,
    /// `v5 = R16G16@36` TexCoord2 — whatever the register numbering does with the bone indices (in the jets'
    /// declaration they come LAST, after TexCoord2, so a positional guess is wrong there; measured
    /// 2026-09-22). A translate folder without the sidecar falls back to that guess: the uv inputs in
    /// ascending register order, a float4 input holding two sets — right on the LAV's and the BTR's
    /// declarations, where it was measured against the bytecode.
    ///
    /// The forms this reads, all measured in the game's vertex shaders:
    ///   `mov oN.xyzw, vK.zwxy`                                   one float4 pair, swapped      → 1:0
    ///   `mov oN.xy, v5.xyxx` + `mov oN.zw, v3.xxxy`              two Half2, bones between      → 1:0
    ///   `mov oN.xy, v4.zwzz` + `mov oN.zw, v5.xxxy` + `mov oM.xy, v4.xyxx`   three sets       → 1:2:0
    ///   `mov oN.xy, v3.xyxx`                                     one set                       → 0
    /// </summary>
    internal static string? UvOrderOfVertexShaders(string p_Folder, string p_Shader, out string p_Evidence,
        out Dictionary<string, string> p_ByDeclaration)
    {
        p_Evidence = "";
        p_ByDeclaration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(p_Folder))
            return null;

        // ⛔ THE EXTRACTION IS BY SUBSTRING AND PULLS THE SHADER'S SIBLINGS TOO — `vehiclepreset_jet` lands
        // with jet_decals, jet_lod and jet_wreckage, one numbered folder each and an index.txt naming them
        // (measured 2026-09-22). Only THIS shader's folder votes on its order, or its siblings do.
        var s_Folder = p_Folder;
        var s_Index = Path.Combine(p_Folder, "index.txt");
        if (File.Exists(s_Index))
        {
            var s_Row = File.ReadAllLines(s_Index)
                .Select(p_L => p_L.Split('\t'))
                .FirstOrDefault(p_P => p_P.Length >= 2 && p_P[1].Trim().Equals(p_Shader, StringComparison.OrdinalIgnoreCase));
            if (s_Row == null)
                return null;

            s_Folder = Path.Combine(p_Folder, s_Row[0].Trim());
            if (!Directory.Exists(s_Folder))
                return null;
        }

        var s_Files = Directory.EnumerateFiles(s_Folder, "*_vs.dxbc", SearchOption.AllDirectories).ToList();
        if (s_Files.Count == 0)
            return null;

        // Every direct hand-over of an input to an output. Position, normal and tangent go through dp3/dp4
        // into temporaries, so a direct move is a uv (or a colour) passthrough.
        var s_Move = new System.Text.RegularExpressions.Regex(@"mov o(\d+)\.([xyzw]+), v(\d+)\.([xyzw]+)");
        var s_Input = new System.Text.RegularExpressions.Regex(@"dcl_input v(\d+)\.([xyzw]+)");
        var s_Orders = new Dictionary<string, int>();
        var s_OrdersByDeclaration = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        int s_Silent = 0, s_Exact = 0;

        foreach (var s_File in s_Files)
        {
            string s_Text;
            byte[] s_Bytes;
            try
            {
                s_Bytes = File.ReadAllBytes(s_File);
                s_Text = new SharpDX.D3DCompiler.ShaderBytecode(s_Bytes).Disassemble();
            }
            catch (Exception) { s_Silent++; continue; }

            var s_Moves = s_Move.Matches(s_Text).Cast<System.Text.RegularExpressions.Match>().ToList();
            if (s_Moves.Count == 0) { s_Silent++; continue; }

            // Register -> the uv set its .xy carries (its .zw carries the next one).
            var s_SetOf = UvSetsFromSidecar(s_File, s_Bytes, out var s_Declaration);
            if (s_SetOf != null)
                s_Exact++;
            else
            {
                s_SetOf = new Dictionary<int, int>();
                var s_Masks = s_Input.Matches(s_Text).Cast<System.Text.RegularExpressions.Match>()
                    .ToDictionary(p_M => int.Parse(p_M.Groups[1].Value), p_M => p_M.Groups[2].Value);
                var s_Next = 0;
                foreach (var s_Register in s_Moves.Select(p_M => int.Parse(p_M.Groups[3].Value)).Distinct().OrderBy(p_R => p_R))
                {
                    s_SetOf[s_Register] = s_Next;
                    s_Next += s_Masks.GetValueOrDefault(s_Register, "xy").Length >= 4 ? 2 : 1;
                }
            }

            var s_Halves = new Dictionary<int, (int? Xy, int? Zw)>();
            foreach (var s_M in s_Moves)
            {
                var s_Output = int.Parse(s_M.Groups[1].Value);
                var s_Mask = s_M.Groups[2].Value;
                var s_Swizzle = s_M.Groups[4].Value;
                if (!s_SetOf.TryGetValue(int.Parse(s_M.Groups[3].Value), out var s_Base))
                    continue; // not a uv input (a colour, say)

                int Set(char p_Component) => s_Base + (p_Component is 'z' or 'w' ? 1 : 0);
                var s_Current = s_Halves.GetValueOrDefault(s_Output);
                switch (s_Mask)
                {
                    case "xyzw": s_Current = (Set(s_Swizzle[0]), Set(s_Swizzle[Math.Min(2, s_Swizzle.Length - 1)])); break;
                    case "xy": s_Current.Xy = Set(s_Swizzle[0]); break;
                    case "zw": s_Current.Zw = Set(s_Swizzle[0]); break;
                    default: continue;
                }

                s_Halves[s_Output] = s_Current;
            }

            if (s_Halves.Count == 0) { s_Silent++; continue; }

            // The pair is the output fed both halves (else the lowest one fed at all); a second output's
            // .xy is the extra interpolator's set.
            var s_Pair = s_Halves.OrderByDescending(p_H => p_H.Value.Xy != null && p_H.Value.Zw != null).ThenBy(p_H => p_H.Key).First();
            var s_Extra = s_Halves.Where(p_H => p_H.Key != s_Pair.Key && p_H.Value.Xy != null).OrderBy(p_H => p_H.Key)
                .Select(p_H => p_H.Value.Xy).FirstOrDefault();
            var s_Order = (s_Pair.Value.Xy ?? s_Pair.Value.Zw ?? 0).ToString();
            if (s_Pair.Value.Xy != null && s_Pair.Value.Zw != null)
                s_Order += ":" + s_Pair.Value.Zw;
            if (s_Extra != null)
                s_Order += ":" + s_Extra;

            s_Orders[s_Order] = s_Orders.GetValueOrDefault(s_Order) + 1;
            if (s_Declaration != null)
            {
                if (!s_OrdersByDeclaration.TryGetValue(s_Declaration, out var s_Per))
                    s_OrdersByDeclaration[s_Declaration] = s_Per = new Dictionary<string, int>();
                s_Per[s_Order] = s_Per.GetValueOrDefault(s_Order) + 1;
            }
        }

        p_Evidence = string.Join(", ", s_Orders.OrderByDescending(p_O => p_O.Value).Select(p_O => $"{p_O.Value} VS {p_O.Key.Replace(':', ',')}")) +
                     (s_Orders.Count > 0 ? ", " : "") + $"{s_Silent} without a uv move" +
                     (s_Exact > 0 ? $"; {s_Exact} placed by their input layout" : "; placed by register order");
        if (s_Orders.Count == 0)
            return "0";

        // The verdict: the order naming the MOST sets (a three-set declaration shows the whole intent; a
        // two-set one is its degenerate case, and the dump falls back the same way), then the most frequent.
        static string Verdict(Dictionary<string, int> p_Counts) => p_Counts.Keys
            .OrderByDescending(p_K => p_K.Split(':').Length)
            .ThenByDescending(p_K => p_Counts[p_K])
            .ThenBy(p_K => p_K, StringComparer.Ordinal)
            .First();

        foreach (var (s_Declaration, s_Per) in s_OrdersByDeclaration)
            p_ByDeclaration[s_Declaration] = Verdict(s_Per);

        return Verdict(s_Orders);
    }

    /// <summary>"0", "0:1", "0:1:2": the sets pass in declaration order, nothing to reorder.</summary>
    internal static bool IsStraightOrder(string p_Order) =>
        p_Order.Split(':').Select((p_Set, p_Slot) => p_Set == p_Slot.ToString()).All(p_Same => p_Same);

    /// <summary>
    /// From the extraction's sidecar (solutions.txt, one line per solution: `&lt;tag&gt; decl=0x… elements=…
    /// layout=…`), the uv set each input REGISTER of one vertex shader carries in its .xy — by the byte
    /// offset the permutation's input layout gives the semantic, looked up among the declaration's
    /// TexCoordN elements, and by the vertex shader's own INPUT SIGNATURE for which register carries
    /// that semantic. Null without a sidecar or without a line for this file.
    /// <paramref name="p_Declaration"/> is the solution's declaration hash, "0x…".
    ///
    /// ⛔⛔ THE REGISTER IS NOT THE SEMANTIC INDEX. The input layout names `TEXCOORD6@44`; which `vN` the
    /// shader reads that from is the compiler's choice, and it follows the order the HLSL declared its
    /// inputs in, not the semantic index. The LAV's, the BTR's and the jets' presets happened to declare
    /// them in order (v4 = TEXCOORD4), so "register = semantic index" held on every shader measured until
    /// the Mi-28's cockpit interior (`mi28_cockpit1p`, declaration 0xB149D3FC, 2026-09-22): its signature
    /// is `TEXCOORD6 → v0, TEXCOORD4 → v1, TEXCOORD0 → v2…`, so `mov o5.xy, v0` + `mov o5.zw, v1` — the
    /// diffuse read through TexCoord1, the plain 1:0 swap — matched no uv register at all, every solution
    /// was counted as "without a uv move", the verdict came out straight, the pilot's cockpit was written
    /// in declaration order and its uvs showed wrong from the seat in game. The signature is in the bytecode
    /// (ISGN), so it is read from there.
    /// </summary>
    private static Dictionary<int, int>? UvSetsFromSidecar(string p_VsFile, byte[] p_Bytecode, out string? p_Declaration)
    {
        p_Declaration = null;
        var s_Sidecar = Path.Combine(Path.GetDirectoryName(p_VsFile)!, "solutions.txt");
        if (!File.Exists(s_Sidecar))
            return null;

        var s_Tag = Path.GetFileName(p_VsFile);
        s_Tag = s_Tag[..^"_vs.dxbc".Length];
        var s_Line = File.ReadLines(s_Sidecar).FirstOrDefault(p_L => p_L.StartsWith(s_Tag + " ", StringComparison.Ordinal));
        if (s_Line == null)
            return null;

        string Field(string p_Name)
        {
            var s_At = s_Line.IndexOf(" " + p_Name + "=", StringComparison.Ordinal);
            if (s_At < 0)
                return "";

            var s_From = s_At + p_Name.Length + 2;
            var s_To = s_Line.IndexOf(' ', s_From);
            return s_To < 0 ? s_Line[s_From..] : s_Line[s_From..s_To];
        }

        p_Declaration = Field("decl");

        // Byte offset -> uv set, from the declaration's TexCoordN elements (VertexElementUsage_TexCoord1:…@28).
        var s_UvAtOffset = new Dictionary<int, int>();
        foreach (var s_Element in Field("elements").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var s_Colon = s_Element.IndexOf(':');
            var s_AtSign = s_Element.LastIndexOf('@');
            if (s_Colon < 0 || s_AtSign < 0)
                continue;

            var s_Usage = s_Element[..s_Colon];
            var s_TexCoord = s_Usage.IndexOf("TexCoord", StringComparison.OrdinalIgnoreCase);
            if (s_TexCoord < 0 || !int.TryParse(s_Usage[(s_TexCoord + 8)..], out var s_Set))
                continue;

            if (int.TryParse(s_Element[(s_AtSign + 1)..], out var s_Offset))
                s_UvAtOffset[s_Offset] = s_Set;
        }

        // Semantic index -> set, from the layout's TEXCOORDn:format@offset.
        var s_SetOfSemantic = new Dictionary<int, int>();
        foreach (var s_Item in Field("layout").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var s_Colon = s_Item.IndexOf(':');
            var s_AtSign = s_Item.LastIndexOf('@');
            if (s_Colon < 0 || s_AtSign < 0 || !s_Item.StartsWith("TEXCOORD", StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(s_Item[8..s_Colon], out var s_Semantic) &&
                int.TryParse(s_Item[(s_AtSign + 1)..], out var s_Offset) &&
                s_UvAtOffset.TryGetValue(s_Offset, out var s_Set))
                s_SetOfSemantic[s_Semantic] = s_Set;
        }

        // Register -> set, through the vertex shader's input signature (TEXCOORDn -> vN), never by number.
        var s_SetOf = new Dictionary<int, int>();
        try
        {
            using var s_Reflection = new SharpDX.D3DCompiler.ShaderReflection(p_Bytecode);
            for (var i = 0; i < s_Reflection.Description.InputParameters; ++i)
            {
                var s_Parameter = s_Reflection.GetInputParameterDescription(i);
                if (!string.Equals(s_Parameter.SemanticName, "TEXCOORD", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (s_SetOfSemantic.TryGetValue(s_Parameter.SemanticIndex, out var s_Set))
                    s_SetOf[s_Parameter.Register] = s_Set;
            }
        }
        catch (Exception)
        {
            // Bytecode whose signature cannot be read says nothing exact; the caller's register-order guess
            // is the published fallback.
            return null;
        }

        return s_SetOf;
    }

    /// <summary>Runs one Rime script to completion and returns everything it printed. Shared with the baker.</summary>
    internal static string Run(string p_Repl, string p_Script)
    {
        var s_Info = new ProcessStartInfo(p_Repl)
        {
            Arguments = $"\"{p_Script}\"",
            WorkingDirectory = Path.GetDirectoryName(p_Repl) ?? Settings.CacheFolder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var s_Process = Process.Start(s_Info);
        if (s_Process == null)
            return "could not start RimeREPL";

        var s_Output = s_Process.StandardOutput.ReadToEnd();
        var s_Error = s_Process.StandardError.ReadToEnd();
        s_Process.WaitForExit();

        return s_Output + s_Error;
    }
}
