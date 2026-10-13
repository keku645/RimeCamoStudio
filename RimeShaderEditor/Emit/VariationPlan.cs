using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RimeShaderEditor.Emit;

/// <summary>
/// Everything a bake needs to deliver ONE brand-new object variation: the sibling names (the game lists a
/// variation by the directory it shares with its object, so every generated asset is named as a sibling),
/// the deterministic guids, and the two authored partitions (the shader stub and the variation asset) written
/// in the partition-JSON form the build commands consume.
///
/// The traps this encodes, each paid for with a wasted game boot:
///   - the variation lookup key is FNV(lowercased asset name); a name hashed any other way spawns invisible;
///   - authored partition types must be in the fidelity map or the engine reads their fields as garbage
///     (verified: a stub written without it carries secondary offsets of 0 on every field);
///   - the entry's referenced partitions must ride in the same bundle, closed transitively (resolvers).
/// </summary>
internal sealed class VariationSpec
{
    public string VariationAsset = "";

    /// <summary>
    /// Whether the mod should take over the appearance of the level's own copies of <see cref="Mesh"/>.
    /// Per-variation, not per-mod: one bake can carry a variation meant to replace what is in the map and
    /// another meant to be placed by hand.
    /// </summary>
    public bool ApplyToLevel = true;

    /// <summary>
    /// A stand-in for the mesh's partition, carrying its instances (the mesh asset and its MeshMaterials)
    /// under their REAL instance guids but under a partition guid and a partition NAME of its own.
    ///
    /// ⛔ It exists to WIN THE KEY, not to draw. A variation is keyed by (mesh nameHash, variation nameHash)
    /// and the FIRST entry to reach a key fills it — later ones are dropped in silence — so a database that
    /// arrives after the level's can never take over an entry. Arriving earlier is impossible while the entry
    /// still points into the level's own bundle: the reference does not resolve and the registrar dereferences
    /// it anyway (the engine's own "meshAssetNameHash != 0" sits on an already-null read). This partition lets
    /// the mod's bundle load FIRST with the entry's references resolved from inside itself.
    ///
    /// Why a NAME of its own: partitions are keyed by name, so a stand-in named after the real mesh dedupes
    /// against it and one of the two loses its instances. Nothing reads the name — the key is built from the
    /// mesh asset's NameHash FIELD — so the stand-in is free to be a sibling.
    /// </summary>
    public string MeshStubPartGuid = "";

    public string MeshStubName = "";
    public string MeshStubJsonPath = "";

    /// <summary>Partition guid of the REAL mesh, so the copied entry's refs can be repointed off it.</summary>
    public string MeshRealPartGuid = "";

    /// <summary>
    /// Variation hashes the level's own copies ask for, when they ask for anything but the base. Empty is
    /// the case a base take-over can serve; anything here means the authored key would never be consulted.
    /// </summary>
    public List<uint> LevelVariationHashes = new();

    /// <summary>
    /// Whether the level's copies are members of its static model group.
    ///
    /// ⚠ THIS WAS ONCE WRITTEN DOWN AS "the take-over cannot reach those copies", and that is NOT what was
    /// measured. What was measured: the base variation key IS won (an object placed by hand comes up wearing
    /// the authored shader) while the map's own copies looked unchanged. In that same build the pixel flavour
    /// a lightmapped copy draws with was still being handed vanilla bytecode, so "unchanged" was its expected
    /// look whether the variation reached it or not — the observation cannot separate the two, and the
    /// conclusion drawn from it was not earned. Every flavour is compiled against its own contract now, so
    /// the question is open again and the take-over ships for grouped copies.
    /// </summary>
    public bool GroupedInLevel;

    public int GroupedInstances;

    /// <summary>
    /// Texture partitions the level's own database entry binds PER MATERIAL, which the copied entry keeps
    /// pointing at.
    ///
    /// ⛔ THEY HAVE TO RIDE WITH THE ENTRY. A take-over bundle loads BEFORE the one that owns the mesh, so
    /// anything the entry still reaches for outside it is simply not loaded yet. A prop whose art is
    /// streamable binds none of these (a concrete barrier: zero) and the question never came up; a prop whose
    /// shader takes its art as MATERIAL PARAMETERS binds one per slot, and shipping the entry without them
    /// is a reference into a bundle that has not arrived.
    /// </summary>
    public List<string> EntryTextures = new();

    /// <summary>
    /// How many texture PARAMETERS the entry binds, which is not how many partitions carry them: a shader
    /// that reads the same art in two slots (a diffuse doubling as specular) binds two parameters into one
    /// partition. The two numbers are kept apart because the check that the delivery is complete compares
    /// against the engine's own count, and that one counts parameters.
    /// </summary>
    public int EntryTextureBindings;

    /// <summary>The vanilla shader this variation clones — also the name of its EBX partition.</summary>
    public string TargetShader = "";

    public string CloneShader = "";
    public string Mesh = "";
    public uint Hash;
    public string MvdbName = "";
    public string StubJsonPath = "";
    public string VariationJsonPath = "";
    public string StubPartGuid = "";
    public string VarPartGuid = "";
    public string MmvInstGuid = "";
    public string MvdbPartGuid = "";
    public string MvdbInstGuid = "";

    /// <summary>Compiled pixel shader for the clone, or null to keep the game's own bytecode (the graph's
    /// emitted logic fingerprints identical to the translation, so only textures/params changed).</summary>
    public string? DxbcPath;

    /// <summary>Per-variant patch manifest ("vanilla.dxbc|authored.dxbc" per line): every pixel flavour of
    /// the mode receives bytecode compiled against ITS OWN contract. Overrides DxbcPath when present.</summary>
    public string? ManifestPath;
}

internal static class VariationPlan
{
    /// <summary>fb's FNV variant over the LOWERCASED name — the hash the variation index is keyed by.</summary>
    internal static uint HashQuickLowerCase(string p_Name)
    {
        var s_Hash = 0x1505u;
        foreach (var s_Char in p_Name.ToLowerInvariant())
            s_Hash = s_Hash * 0x21u ^ s_Char;
        return s_Hash;
    }

    /// <summary>
    /// Deterministic guid from a label: the same variation name always produces the same partition layout, so
    /// a rebake overwrites its previous self instead of leaking a second copy under fresh guids.
    /// </summary>
    internal static string GuidOf(string p_Label)
    {
        var s_Bytes = MD5.HashData(Encoding.UTF8.GetBytes("rse-variation::" + p_Label));
        return new Guid(s_Bytes).ToString();
    }

    /// <summary>The clone's sibling name: the target shader plus the variation's suffix (SS_Object -> SS_Object_RED).</summary>
    internal static string CloneShaderName(string p_Target, string p_VariationAsset)
    {
        var s_Suffix = SuffixOf(p_VariationAsset);
        var s_Name = s_Suffix.Length > 0
            ? $"{p_Target}_{s_Suffix}"
            : $"{p_Target}_VAR{HashQuickLowerCase(p_VariationAsset):X8}";

        // The clone and the variation asset each become a PARTITION, and partitions are keyed by name:
        // share one name and only one of the two survives in the bundle, leaving the other's importers
        // dangling — which the engine's EBX reader answers with a load-thread access violation. The names
        // stayed apart by luck while every target carried an SS_ prefix for the suggested variation name
        // to strip; the first prefix-less target (the glass preset) collided.
        if (s_Name.Equals(p_VariationAsset, StringComparison.OrdinalIgnoreCase))
            s_Name += "_SHADER";

        return s_Name;
    }

    /// <summary>
    /// A custom texture's sibling name: the vanilla texture's own suffix moves onto the variation asset
    /// (vanilla Object_D + variation .../Object_RED -> .../Object_RED_D). When the vanilla name shares no
    /// prefix with the object, the whole last segment is appended instead — still unique, still a sibling.
    /// </summary>
    internal static string SiblingTextureName(string p_VariationAsset, string p_VanillaTexture)
    {
        var s_VanillaLast = p_VanillaTexture.Split('/')[^1];
        var s_VariationLast = p_VariationAsset.Split('/')[^1];

        // The suffix must start at an underscore so the join reads Object_RED_D, never Object_REDD: the
        // shared stem is backed off to the last '_' boundary inside the common prefix.
        var s_Common = CommonPrefixTrimmed(s_VariationLast, s_VanillaLast).Length;
        while (s_Common > 0 && s_VanillaLast[s_Common - 1] != '_')
            s_Common--;
        while (s_Common > 0 && s_VanillaLast[s_Common - 1] == '_')
            s_Common--;

        if (s_Common > 0 && s_VanillaLast.Length > s_Common)
            return $"{p_VariationAsset}{s_VanillaLast[s_Common..]}";

        return $"{p_VariationAsset}_{s_VanillaLast}";
    }

    /// <summary>The variation suffix: what its last segment adds beyond the shared object stem (RED for
    /// Object_RED next to SS_Object), empty when nothing is shared.</summary>
    private static string SuffixOf(string p_VariationAsset)
    {
        var s_Last = p_VariationAsset.Split('/')[^1];
        var s_Index = s_Last.LastIndexOf('_');
        return s_Index > 0 && s_Index < s_Last.Length - 1 ? s_Last[(s_Index + 1)..] : "";
    }

    private static string CommonPrefixTrimmed(string p_A, string p_B)
    {
        var s_Length = 0;
        while (s_Length < p_A.Length && s_Length < p_B.Length &&
               char.ToLowerInvariant(p_A[s_Length]) == char.ToLowerInvariant(p_B[s_Length]))
            s_Length++;
        return p_A[..s_Length];
    }

    /// <summary>
    /// Builds the whole spec and writes the two authored partitions into <paramref name="p_WorkDir"/>. The
    /// JSON shape is the one the partition dumper prints — that is the shape the build command parses, and a
    /// template obtained any other way is rejected at parse time.
    /// </summary>
    internal static VariationSpec Build(string p_Target, string p_VariationAsset, string p_Mesh,
        string p_WorkDir, string? p_DxbcPath, bool p_ApplyToLevel = true)
    {
        Directory.CreateDirectory(p_WorkDir);

        var s_Spec = new VariationSpec
        {
            ApplyToLevel = p_ApplyToLevel,
            VariationAsset = p_VariationAsset,
            CloneShader = CloneShaderName(p_Target, p_VariationAsset),
            Mesh = p_Mesh,
            Hash = HashQuickLowerCase(p_VariationAsset),
            MvdbName = $"{p_VariationAsset}_MVDB",
            StubPartGuid = GuidOf(p_VariationAsset + "::stub-part"),
            VarPartGuid = GuidOf(p_VariationAsset + "::var-part"),
            MmvInstGuid = GuidOf(p_VariationAsset + "::mmv-inst"),
            MvdbPartGuid = GuidOf(p_VariationAsset + "::mvdb-part"),
            MvdbInstGuid = GuidOf(p_VariationAsset + "::mvdb-inst"),
            DxbcPath = p_DxbcPath,
            StubJsonPath = Path.Combine(p_WorkDir, "variation_stub.json"),
            VariationJsonPath = Path.Combine(p_WorkDir, "variation_asset.json"),
            MeshStubPartGuid = GuidOf(p_VariationAsset + "::mesh-part"),
            MeshStubName = p_Mesh + "_RSEKEY",
            MeshStubJsonPath = Path.Combine(p_WorkDir, "mesh_stub.json"),
            TargetShader = p_Target,
        };

        var s_StubInst = GuidOf(p_VariationAsset + "::stub-inst");
        var s_VarInst = GuidOf(p_VariationAsset + "::var-inst");

        // ⛔⛔⛔ THIS FILE WAS NEVER WRITTEN, AND THE BAKE SHIPPED WHATEVER WAS LEFT IN THE WORK DIRECTORY.
        // The path was assigned here and consumed by the build script, and nothing in between ever produced
        // it — so a bake reusing this directory published the PREVIOUS bake's stub: a partition under this
        // variation's name carrying another shader's graph name, and the engine then looked up a key that
        // exists nowhere. Materials with no shader draw nothing at all: the level's copies went invisible,
        // and every check passed because the file did exist.
        //
        // The shape is the measured one — a shipped surface shader partition is a single ShaderGraph with a
        // name and two fields — and the guids are the ones the variation asset above points at, which is the
        // whole reason this partition exists.
        File.WriteAllText(s_Spec.StubJsonPath,
            "{\"PrimaryInstanceGuid\": \"" + s_StubInst + "\", \"Instances\": {" +
            "\"" + s_StubInst + "\": {\"$type\": \"ShaderGraph\", \"Name\": \"" + s_Spec.CloneShader + "\", " +
            "\"MaxSubMaterialCount\": 8, \"GammaCorrectionEnable\": true}}, " +
            "\"Name\": \"" + s_Spec.CloneShader + "\", \"PartitionGuid\": \"" + s_Spec.StubPartGuid + "\"}");

        // Same reasoning for the two files written LATER in the bake: left over from a previous run they
        // would ship under this variation's name with another object's contents. Their writers put them
        // back; what must not survive is a stale one.
        if (File.Exists(s_Spec.MeshStubJsonPath))
            File.Delete(s_Spec.MeshStubJsonPath);

        File.WriteAllText(s_Spec.VariationJsonPath,
            "{\"PrimaryInstanceGuid\": \"" + s_VarInst + "\", \"Instances\": {" +
            "\"" + s_Spec.MmvInstGuid + "\": {\"$type\": \"MeshMaterialVariation\", \"Shader\": {" +
            "\"Shader\": {\"PartitionGuid\": \"" + s_Spec.StubPartGuid + "\", \"InstanceGuid\": \"" + s_StubInst + "\"}, " +
            "\"BoolParameters\": [], \"VectorParameters\": [], \"VectorArrayParameters\": [], \"TextureParameters\": []}}, " +
            "\"" + s_VarInst + "\": {\"$type\": \"ObjectVariation\", \"Name\": \"" + p_VariationAsset + "\", " +
            "\"NameHash\": " + s_Spec.Hash + "}}, " +
            "\"Name\": \"" + p_VariationAsset + "\", \"PartitionGuid\": \"" + s_Spec.VarPartGuid + "\"}");

        return s_Spec;
    }

    /// <summary>
    /// Writes the stand-in partition from a DUMP of the real mesh partition: every instance is kept, under its
    /// own instance guid, so each of the entry's references (the mesh AND the MeshMaterials that live in the
    /// same partition) resolves inside the mod's bundle once the entry is repointed onto this one.
    ///
    /// Two edits are what make it a stand-in rather than a copy:
    ///   - the partition takes a guid and a NAME of its own, so it does not dedupe against the real one;
    ///   - references that leave the partition are cut. The stand-in is never drawn — the registrar reads the
    ///     mesh asset's NameHash and nothing else — so its geometry (LodGroup) and its materials' surface
    ///     shaders are dead weight, and keeping them would make the mod's bundle import from the level's,
    ///     which is exactly what loading first cannot do. Cutting them is safe because the authored variation
    ///     supplies the shader: the engine only falls back to the material's own when the variation has none,
    ///     and reads the material's parameter arrays only to merge under the variation's.
    /// Self-references (the mesh asset's own Materials list) are repointed onto this partition instead.
    /// </summary>
    /// <returns>false with a reason when the dump is not a usable mesh partition.</returns>
    internal static bool WriteMeshStub(VariationSpec p_Spec, string p_DumpPath, out string p_Error)
    {
        p_Error = "";

        JsonObject? s_Root;
        try
        {
            s_Root = JsonNode.Parse(File.ReadAllText(p_DumpPath)) as JsonObject;
        }
        catch (Exception s_Ex)
        {
            p_Error = $"the dump did not parse ({s_Ex.Message})";
            return false;
        }

        if (s_Root?["Instances"] is not JsonObject s_Instances || s_Instances.Count == 0)
        {
            p_Error = "the dump has no instances";
            return false;
        }

        // The PARTITION's guid is the root's, NOT the first "PartitionGuid" the text happens to contain —
        // that one belongs to the mesh's LodGroup reference, and a repoint aimed at it moves nothing (the
        // build reported "Repointed 0 ref(s)" and the mod shipped with its entry still pointing into the
        // level's bundle, which is a null mesh the moment that bundle is no longer the one loaded first).
        if ((string?) s_Root["PartitionGuid"] is not { Length: > 0 } s_RealPart)
        {
            p_Error = "the dump has no partition guid";
            return false;
        }

        // Nothing to win without a NameHash: it IS the key.
        if (!s_Instances.Any(p_I => p_I.Value?["NameHash"] != null))
        {
            p_Error = "no instance in the dump carries a NameHash";
            return false;
        }

        p_Spec.MeshRealPartGuid = s_RealPart;
        s_Root["PartitionGuid"] = p_Spec.MeshStubPartGuid;
        s_Root["Name"] = p_Spec.MeshStubName;
        ScrubRefs(s_Root, s_RealPart, p_Spec.MeshStubPartGuid);

        File.WriteAllText(p_Spec.MeshStubJsonPath, s_Root.ToJsonString());
        return true;
    }

    /// <summary>
    /// Rewrites the clone's shader partition from a DUMP of the vanilla one, so its fields are the shader's
    /// own rather than the shape's defaults. Only the name and the guids are ours — the name because it is
    /// the key the clone's entry is stored under, the guids because the variation asset points at them.
    /// </summary>
    /// <returns>false with a reason when the dump is not a usable shader partition; the authored default
    /// written at Build time stays in place, which is correct for every shader measured so far.</returns>
    internal static bool WriteShaderStubFrom(VariationSpec p_Spec, string p_DumpPath, out string p_Error)
    {
        p_Error = "";

        JsonObject? s_Root;
        try
        {
            s_Root = JsonNode.Parse(File.ReadAllText(p_DumpPath)) as JsonObject;
        }
        catch (Exception s_Ex)
        {
            p_Error = $"the dump did not parse ({s_Ex.Message})";
            return false;
        }

        if (s_Root?["Instances"] is not JsonObject s_Instances || s_Instances.Count != 1)
        {
            p_Error = "the dump is not a single-instance shader partition";
            return false;
        }

        var (_, s_Node) = s_Instances.First();
        if (s_Node is not JsonObject s_Graph || (string?) s_Graph["$type"] is not { } s_Type ||
            !s_Type.Contains("Shader", StringComparison.OrdinalIgnoreCase))
        {
            p_Error = "the dump's instance is not a shader";
            return false;
        }

        var s_StubInst = GuidOf(p_Spec.VariationAsset + "::stub-inst");
        var s_Clone = s_Graph.DeepClone()!.AsObject();
        s_Clone["Name"] = p_Spec.CloneShader;

        var s_Out = new JsonObject
        {
            ["PrimaryInstanceGuid"] = s_StubInst,
            ["Instances"] = new JsonObject { [s_StubInst] = s_Clone },
            ["Name"] = p_Spec.CloneShader,
            ["PartitionGuid"] = p_Spec.StubPartGuid,
        };

        File.WriteAllText(p_Spec.StubJsonPath, s_Out.ToJsonString());
        return true;
    }

    /// <summary>
    /// The name the clone's shader partition actually carries on disk, or null when it cannot be read. The
    /// bake compares it against the key it wrote into the database: they are the same string or the object
    /// asks for a shader nobody defines and is not drawn at all.
    /// </summary>
    internal static string? StubGraphName(VariationSpec p_Spec)
    {
        try
        {
            if ((JsonNode.Parse(File.ReadAllText(p_Spec.StubJsonPath)) as JsonObject)?["Instances"]
                is not JsonObject s_Instances || s_Instances.Count == 0)
                return null;

            return (string?) s_Instances.First().Value?["Name"];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks up how the level actually places this mesh, so a take-over is not authored against a guess.
    ///
    /// ⛔ THE COPIES IN A MAP ARE NOT LOOSE OBJECTS. Repeated props are members of the map's
    /// StaticModelGroupEntityData: one member entry holds an instance COUNT and a per-instance variation
    /// list. Since a variation is keyed by (mesh nameHash, variation nameHash), a mod authoring the base key
    /// only reaches those copies while that list is empty or all-zero — if the map gave them a variation of
    /// their own, an entry under hash 0 installs, reports success and changes nothing.
    ///
    /// ⚠ The member does NOT point at the mesh: it points at a mesh ENTITY DATA which points at the mesh.
    /// Searching the members for the mesh's own guid finds nothing and reads as "this mesh is not in the
    /// group" — it took a wrong turn on exactly that. The chain is followed here instead.
    /// </summary>
    internal static bool InspectLevelUse(string p_DumpPath, string p_MeshPartGuid, out int p_Instances,
        out List<uint> p_Variations, out bool p_Grouped)
    {
        p_Instances = 0;
        p_Variations = new List<uint>();
        p_Grouped = false;

        JsonObject? s_Instances;
        try
        {
            s_Instances = (JsonNode.Parse(File.ReadAllText(p_DumpPath)) as JsonObject)?["Instances"]
                as JsonObject;
        }
        catch (Exception)
        {
            return false;
        }

        if (s_Instances == null)
            return false;

        // The mesh entity datas that draw this mesh — the thing the group's members actually reference.
        var s_Drawers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s_Guid, s_Node) in s_Instances)
            if (s_Node?["Mesh"] is JsonObject s_Mesh &&
                string.Equals((string?) s_Mesh["PartitionGuid"], p_MeshPartGuid,
                    StringComparison.OrdinalIgnoreCase))
                s_Drawers.Add(s_Guid);

        if (s_Drawers.Count == 0)
            return false;

        foreach (var (_, s_Node) in s_Instances)
        {
            if ((string?) s_Node?["$type"] != "StaticModelGroupEntityData" ||
                s_Node["MemberDatas"] is not JsonArray s_Members)
                continue;

            foreach (var s_Member in s_Members)
            {
                if (s_Member?["MeshEntityType"] is not JsonObject s_Type ||
                    !s_Drawers.Contains((string?) s_Type["InstanceGuid"] ?? ""))
                    continue;

                p_Grouped = true;
                p_Instances += (int?) s_Member["InstanceCount"] ?? 0;

                if (s_Member["InstanceObjectVariation"] is not JsonArray s_Variations)
                    continue;

                foreach (var s_Variation in s_Variations)
                    if ((uint?) s_Variation is { } s_Hash && s_Hash != 0)
                        p_Variations.Add(s_Hash);
            }
        }

        return true;
    }

    /// <summary>Repoints every in-partition reference onto the stand-in and cuts every outgoing one.</summary>
    private static void ScrubRefs(JsonNode? p_Node, string p_RealPart, string p_StubPart)
    {
        switch (p_Node)
        {
            case JsonObject s_Object:
                foreach (var s_Key in s_Object.Select(p_P => p_P.Key).ToList())
                {
                    if (AsRef(s_Object[s_Key]) is { } s_Ref)
                    {
                        // Kept references are edited in place: re-assigning a node to the slot it already
                        // occupies is what the node model rejects ("the node already has a parent").
                        if (!Keep(s_Ref, p_RealPart, p_StubPart))
                            s_Object[s_Key] = null;
                    }
                    else
                    {
                        ScrubRefs(s_Object[s_Key], p_RealPart, p_StubPart);
                    }
                }

                break;

            case JsonArray s_Array:
                for (var i = 0; i < s_Array.Count; i++)
                {
                    if (AsRef(s_Array[i]) is { } s_Ref)
                    {
                        if (!Keep(s_Ref, p_RealPart, p_StubPart))
                            s_Array[i] = null;
                    }
                    else
                    {
                        ScrubRefs(s_Array[i], p_RealPart, p_StubPart);
                    }
                }

                break;
        }
    }

    /// <summary>Repoints a reference that stays inside the partition; false when it leaves and must be cut.</summary>
    private static bool Keep(JsonObject p_Ref, string p_RealPart, string p_StubPart)
    {
        if (!string.Equals((string?) p_Ref["PartitionGuid"], p_RealPart, StringComparison.OrdinalIgnoreCase))
            return false;

        p_Ref["PartitionGuid"] = p_StubPart;
        return true;
    }

    /// <summary>A reference is the two-guid pair the dumper prints for a link to an instance.</summary>
    private static JsonObject? AsRef(JsonNode? p_Node) =>
        p_Node is JsonObject s_Object && s_Object.ContainsKey("PartitionGuid") &&
        s_Object.ContainsKey("InstanceGuid")
            ? s_Object
            : null;
}
