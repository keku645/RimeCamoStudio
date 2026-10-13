using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RimeCamoStudio.Cache;
using RimeCamoStudio.Catalog;

namespace RimeCamoStudio.Mod;

/// <summary>
/// One variation database entry a soldier skin BAKES (done the way the vehicles are): a copy of the entry of
/// <see cref="Mesh"/> under the stock variation <see cref="Source"/> (0: the mesh linked bare, its entry 0), under a variation of the skin's
/// own (<see cref="Name"/> / <see cref="Hash"/> — the soldier module creates the variation of that same name in Lua and hangs it on his
/// pieces), with the part's textures on its camo cloth and, on the first-person arms' copies, the other half hidden.
/// </summary>
public sealed class SkinEntryJob
{
    public string Name { get; init; } = "";
    public uint Hash { get; init; }
    public string Mesh { get; init; } = "";
    public uint Source { get; init; }

    /// <summary>The studio's part whose textures and numbers it wears ("head", "upper", "lower").</summary>
    public string Part { get; init; } = "";

    /// <summary>Null for a third-person mesh; "torso", "whole" or "legs" for the first-person arms' copies.</summary>
    public string? Side { get; init; }

    /// <summary>(material id, instance): the materials whose material variation changes — the all-zero instance keeps the copied one.</summary>
    public List<(int Id, string Instance)> Refs { get; } = new();

    /// <summary>The instances whose materials take the part's textures (the dressed ones).</summary>
    public List<string> Scopes { get; } = new();

    /// <summary>What the bake says about it: "2 dressed, 1 hidden".</summary>
    public string Said { get; set; } = "";
}

public static partial class CamoBaker
{
    /// <summary>The all-zero instance: a material marked as the skin's (it takes its textures) keeping its own material variation.</summary>
    private const string c_KeepVariation = "00000000-0000-0000-0000-000000000000";

    /// <summary>The name a skin's variation takes — the soldier module's SkinVariationName, letter for letter ("torso" / "legs" for the body parts).</summary>
    public static string SkinVariationName(string p_Key, string p_WindowPart, string p_Leaf) =>
        $"Characters/Soldiers/Skin/{p_Key}/{p_WindowPart}/{p_Leaf}";

    /// <summary>A variation's NameHash as the game (and the soldier module's IdentifierOf) computes it: of the name in lower case.</summary>
    public static uint VariationHashOf(string p_Name) => UnlockIdentifierOf(p_Name.ToLowerInvariant());

    /// <summary>The window's part of a studio part (the name the soldier module's variations carry).</summary>
    private static string WindowPartOf(string p_Part) => p_Part switch { "upper" => "torso", "lower" => "legs", _ => "head" };

    /// <summary>The skin's material variations (its numbers, its shader copies, the hidden one) and the hidden shader's stub.</summary>
    public static string SkinMaterialsName(string p_Key) => $"Characters/Custom/Skin_{p_Key}_Materials";
    public static string SkinHiddenShaderName(string p_Key) => $"Characters/Custom/Skin_{p_Key}_Hidden";
    public static string SkinEntryDbName(string p_Key, int p_Index) => $"MV_SKIN_{p_Key}_{p_Index}";
    public const string SkinEntriesBundle = "skinv";

    /// <summary>
    /// The entries of a skin, as the soldier module's ResolveSkin / AddSkinCopy dressed them in Lua until 2026-09-28, from the catalogue (each
    /// part's look → its pieces' meshes and variations) and the soldier material scan (per mesh, variation and material id: its shader, its
    /// textures — CamoTile marks the cloth —, its material variation's numbers):
    ///   · per part, each of its look's meshes the part lists: its cloth takes the part's textures; a material drawn with one of the part's
    ///     shader copies too;
    ///   · the first-person arms (the upper body's look's): "torso" = the sleeves dressed with the upper body's, the trousers hidden; "whole" =
    ///     the sleeves dressed, the trousers as they come; "legs" (the lower body's look's arms when the same mesh) = the trousers dressed with
    ///     the lower body's, the rest hidden.
    /// A dressed material keeps its own material variation (its numbers) unless the part brings numbers or draws it with a copy: then a
    /// material variation of the skin's carries the stock numbers, the part's over them, and the copy.
    /// </summary>
    private static List<SkinEntryJob>? PlanSkinEntries(SoldierBakePlan p_Plan, IReadOnlyList<PackageSoldierPart> p_Parts, SoldierEntry p_Soldier,
        IReadOnlyList<MaterialBinding> p_Scan, IReadOnlyDictionary<(string Mesh, uint Variation, int Id), List<(string Param, string Value)>> p_Numbers,
        IReadOnlyList<ShaderStub> p_Stubs, Func<int, int, string> p_GuidFor, List<Dictionary<string, object>> p_Variations,
        List<string> p_Problems, Action<string> p_Log)
    {
        var s_Jobs = new List<SkinEntryJob>();
        var s_Hidden = p_GuidFor(0x7000, 2);
        SoldierLook? LookOf(PackageSoldierPart? p_Part)
        {
            var s_Appearance = p_Part?.Appearance is { Length: > 0 } s_Own ? s_Own : p_Plan.Soldier.Appearance;
            return p_Soldier.Looks.FirstOrDefault(p_L => p_L.Appearance.Equals(s_Appearance, StringComparison.OrdinalIgnoreCase));
        }

        // the materials of one stock entry: id → (shader, cloth)
        Dictionary<int, (string Shader, bool Cloth)>? MaterialsOf(string p_Mesh, uint p_Variation)
        {
            var s_Found = p_Scan.Where(p_B => p_B.Mesh.Equals(p_Mesh, StringComparison.OrdinalIgnoreCase) && p_B.Variation == p_Variation &&
                                              p_B.MaterialId >= 0).ToList();
            if (s_Found.Count == 0)
                return null;

            var s_Out = new Dictionary<int, (string Shader, bool Cloth)>();
            foreach (var s_Binding in s_Found)
            {
                var s_Cloth = s_Binding.Textures.Any(p_T => p_T.Param.Equals("CamoTile", StringComparison.OrdinalIgnoreCase));
                s_Out[s_Binding.MaterialId] = s_Out.TryGetValue(s_Binding.MaterialId, out var s_Had)
                    ? (s_Had.Shader, s_Had.Cloth || s_Cloth)
                    : (s_Binding.Shader, s_Cloth);
            }

            return s_Out;
        }

        // one job: its materials decided as AddSkinCopy decided them
        void Plan(SkinEntryJob p_Job, PackageSoldierPart p_Spec)
        {
            if (s_Jobs.Any(p_J => p_J.Name.Equals(p_Job.Name, StringComparison.OrdinalIgnoreCase)))
                return;

            var s_Materials = MaterialsOf(p_Job.Mesh, p_Job.Source);
            if (s_Materials == null)
            {
                p_Problems.Add($"the soldier material scan has no entry of {p_Job.Mesh.Split('/')[^1]} under variation {p_Job.Source} — refresh the " +
                               "soldier scan (Reload from game) and bake again.");
                return;
            }

            var s_Copies = p_Spec.Shaders.ToDictionary(p_S => p_S.Shader.ToLowerInvariant(),
                p_S => p_Stubs.FirstOrDefault(p_T => p_T.Name.Equals(p_S.Clone, StringComparison.OrdinalIgnoreCase)));
            var s_Arms = p_Job.Side != null;
            int s_Dressed = 0, s_HiddenCount = 0, s_Own = 0;
            foreach (var (s_Id, (s_Shader, s_Cloth)) in s_Materials.OrderBy(p_M => p_M.Key))
            {
                var s_IsTrousers = s_Arms && p_Plan.Soldier.Trousers.Contains(s_Id);
                bool s_Hide = false, s_Dress = false, s_Kept = false;
                if (s_Arms)
                {
                    switch (p_Job.Side)
                    {
                        case "torso":
                            s_Hide = s_IsTrousers;
                            s_Dress = !s_IsTrousers && s_Cloth;
                            break;
                        case "whole":
                            s_Kept = s_IsTrousers;
                            s_Dress = !s_IsTrousers && s_Cloth;
                            break;
                        default:
                            s_Hide = !s_IsTrousers;
                            s_Dress = s_IsTrousers && s_Cloth;
                            break;
                    }
                }

                var s_Copy = s_Copies.GetValueOrDefault(s_Shader.ToLowerInvariant());
                if (!s_Arms)
                    s_Dress = s_Cloth || s_Copy != null;
                else if (s_Copy != null && !s_Hide && !s_Kept)
                    s_Dress = true;

                if (s_Hide)
                {
                    p_Job.Refs.Add((s_Id, s_Hidden));
                    s_HiddenCount++;
                    continue;
                }

                if (!s_Dress)
                    continue;

                s_Dressed++;
                if (p_Spec.Values.Count == 0 && s_Copy == null)
                {
                    // the cloth keeps its own material variation (its look's numbers): only its pictures change
                    p_Job.Refs.Add((s_Id, c_KeepVariation));
                    if (!p_Job.Scopes.Contains(c_KeepVariation))
                        p_Job.Scopes.Add(c_KeepVariation);
                    continue;
                }

                // a material variation of the skin's: the stock numbers, the part's over them, and the part's shader copy
                var s_Instance = p_GuidFor(0x7000, 0x10 + p_Variations.Count);
                var s_Values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (s_Param, s_Value) in p_Numbers.GetValueOrDefault((p_Job.Mesh.ToLowerInvariant(), p_Job.Source, s_Id)) ?? new())
                    s_Values[s_Param] = s_Value;
                foreach (var (s_Param, s_Value) in p_Spec.Values)
                    s_Values[s_Param] = s_Value;

                p_Variations.Add(new Dictionary<string, object>
                {
                    ["$instance"] = s_Instance,
                    ["$type"] = "MeshMaterialVariation",
                    ["Shader"] = new Dictionary<string, object?>
                    {
                        ["Shader"] = s_Copy == null ? null : new Dictionary<string, object> { ["PartitionGuid"] = s_Copy.Partition, ["InstanceGuid"] = s_Copy.Instance },
                        ["BoolParameters"] = Array.Empty<object>(),
                        ["VectorParameters"] = s_Values.Select(p_V => SkinVector(p_V.Key, p_V.Value)).ToList(),
                        ["VectorArrayParameters"] = Array.Empty<object>(),
                        ["TextureParameters"] = Array.Empty<object>(),
                    },
                });
                p_Job.Refs.Add((s_Id, s_Instance));
                p_Job.Scopes.Add(s_Instance);
                s_Own++;
            }

            p_Job.Said = $"{s_Dressed} dressed{(s_Own > 0 ? $" ({s_Own} with a material variation of its own)" : "")}, {s_HiddenCount} hidden";
            s_Jobs.Add(p_Job);
        }

        // --- the parts' own meshes ------------------------------------------------------------------------------------------------------
        foreach (var s_Part in p_Parts)
        {
            if (LookOf(s_Part) is not { } s_Look)
            {
                p_Problems.Add($"{s_Part.Part}: its look {s_Part.Appearance ?? p_Plan.Soldier.Appearance} is not one of {p_Soldier.Display}'s in the catalogue.");
                continue;
            }

            foreach (var s_Item in s_Look.Items.Where(p_I => p_I.Part.Equals(s_Part.Part, StringComparison.OrdinalIgnoreCase) && p_I.Role != "arms1p" &&
                                                            s_Part.Meshes.Contains(p_I.Mesh, StringComparer.OrdinalIgnoreCase)))
            {
                var s_Name = SkinVariationName(p_Plan.Key, WindowPartOf(s_Part.Part), s_Item.Mesh.ToLowerInvariant().Split('/')[^1]);
                Plan(new SkinEntryJob
                {
                    Name = s_Name, Hash = VariationHashOf(s_Name), Mesh = s_Item.Mesh.ToLowerInvariant(),
                    Source = s_Item.Variation.Length > 0 ? s_Item.NameHash : 0, Part = s_Part.Part,
                }, s_Part);
            }
        }

        // --- the first-person arms: the upper body's look's (a pair, or the mesh linked bare: entry 0) --------------------------------------
        var s_Upper = p_Parts.FirstOrDefault(p_P => p_P.Part == "upper");
        var s_Lower = p_Parts.FirstOrDefault(p_P => p_P.Part == "lower");
        var s_ArmsItem = LookOf(s_Upper)?.Items.FirstOrDefault(p_I => p_I.Role == "arms1p");
        if (s_ArmsItem != null)
        {
            var s_ArmsMesh = s_ArmsItem.Mesh.ToLowerInvariant();
            var s_ArmsSource = s_ArmsItem.Variation.Length > 0 ? s_ArmsItem.NameHash : 0;
            if (s_Upper != null)
            {
                foreach (var s_Side in new[] { "torso", "whole" })
                {
                    var s_Name = SkinVariationName(p_Plan.Key, "arms", s_Side);
                    Plan(new SkinEntryJob { Name = s_Name, Hash = VariationHashOf(s_Name), Mesh = s_ArmsMesh, Source = s_ArmsSource, Part = "upper", Side = s_Side },
                        s_Upper);
                }
            }

            if (s_Lower != null)
            {
                // the trousers from the arms of the LOOK THE LEGS START FROM, when they are the same mesh
                var s_LegsItem = LookOf(s_Lower)?.Items.FirstOrDefault(p_I => p_I.Role == "arms1p");
                var s_LegsSource = s_LegsItem != null && s_LegsItem.Mesh.Equals(s_ArmsItem.Mesh, StringComparison.OrdinalIgnoreCase)
                    ? (s_LegsItem.Variation.Length > 0 ? s_LegsItem.NameHash : 0)
                    : s_ArmsSource;
                var s_Name = SkinVariationName(p_Plan.Key, "arms", "legs");
                Plan(new SkinEntryJob { Name = s_Name, Hash = VariationHashOf(s_Name), Mesh = s_ArmsMesh, Source = s_LegsSource, Part = "lower", Side = "legs" },
                    s_Lower);
            }
        }
        else
            p_Log($"  first person: {p_Plan.Soldier.Display}'s upper body look links no first-person arms — the game's arms.");

        return p_Problems.Count == 0 ? s_Jobs : null;
    }

    /// <summary>A number of a skin's material variation: all four components, as the soldier module wrote them (Vec4).</summary>
    private static Dictionary<string, object> SkinVector(string p_Name, string p_Value)
    {
        var s_Parts = p_Value.Split(',')
            .Select(p_C => float.TryParse(p_C.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s_V) ? s_V : 0f)
            .Concat(new[] { 0f, 0f, 0f, 0f })
            .Take(4)
            .ToArray();
        return new Dictionary<string, object>
        {
            ["Value"] = new Dictionary<string, object> { ["x"] = s_Parts[0], ["y"] = s_Parts[1], ["z"] = s_Parts[2], ["w"] = s_Parts[3] },
            ["ParameterType"] = "ShaderParameterType_Vec4",
            ["ParameterName"] = p_Name,
        };
    }

    /// <summary>
    /// The numbers of each stock material variation the soldier scan read (its SHMATVEC lines with source=variationAsset), by (mesh, variation,
    /// material id) — what a material variation of the skin's starts from, as the Lua copy did. With <paramref name="p_Source"/> "material":
    /// the numbers the MESH's own material carries under that entry instead (what a variation that sets none draws with — the US support's
    /// default legs, a bare entry 0: CamoTileFactor 9, measured 2026-09-28).
    /// </summary>
    private static Dictionary<(string Mesh, uint Variation, int Id), List<(string Param, string Value)>> SkinScanNumbers(string p_ScanPath,
        string p_Source = "variationAsset")
    {
        var s_Out = new Dictionary<(string, uint, int), List<(string, string)>>();
        if (!File.Exists(p_ScanPath))
            return s_Out;

        foreach (var s_Line in File.ReadLines(p_ScanPath))
        {
            if (!s_Line.StartsWith("SHMATVEC:", StringComparison.Ordinal) || !s_Line.Contains($" source={p_Source} ", StringComparison.Ordinal))
                continue;

            string? Field(string p_Name)
            {
                var s_At = s_Line.IndexOf(" " + p_Name + "=", StringComparison.Ordinal);
                if (s_At < 0)
                    return null;
                var s_Start = s_At + p_Name.Length + 2;
                var s_End = s_Line.IndexOf(' ', s_Start);
                return s_End < 0 ? s_Line[s_Start..] : s_Line[s_Start..s_End];
            }

            if (Field("mesh") is not { } s_Mesh || !uint.TryParse(Field("variation"), out var s_Variation) ||
                !int.TryParse(Field("material"), out var s_Id) || Field("param") is not { } s_Param || Field("value") is not { } s_Value)
                continue;

            var s_Key = (s_Mesh.ToLowerInvariant(), s_Variation, s_Id);
            if (!s_Out.TryGetValue(s_Key, out var s_List))
                s_Out[s_Key] = s_List = new List<(string, string)>();
            if (!s_List.Any(p_P => p_P.Item1 == s_Param))
                s_List.Add((s_Param, s_Value));
        }

        return s_Out;
    }

    /// <summary>The skin's material variations and the hidden one, as one partition (the vehicles' VehMaterials JSON, plus the hidden stub's ref).</summary>
    private static void WriteSkinMaterials(string p_Path, string p_Name, string p_Partition, string p_HiddenInstance, ShaderStub p_HiddenStub,
        IReadOnlyList<Dictionary<string, object>> p_Variations, JsonSerializerOptions p_Json)
    {
        var s_Instances = new Dictionary<string, object>
        {
            // ⛔ the HIDDEN material variation: its shader has no solution in any database, so the material is not drawn (the soldier module's
            // NoSuchShader, proven in game 2026-09-28 — and in first person a hidden material no other copy paints is a black hole)
            [p_HiddenInstance] = new Dictionary<string, object>
            {
                ["$type"] = "MeshMaterialVariation",
                ["Shader"] = new Dictionary<string, object>
                {
                    ["Shader"] = new Dictionary<string, object> { ["PartitionGuid"] = p_HiddenStub.Partition, ["InstanceGuid"] = p_HiddenStub.Instance },
                    ["BoolParameters"] = Array.Empty<object>(),
                    ["VectorParameters"] = Array.Empty<object>(),
                    ["VectorArrayParameters"] = Array.Empty<object>(),
                    ["TextureParameters"] = Array.Empty<object>(),
                },
            },
        };

        foreach (var s_Variation in p_Variations)
            s_Instances[(string) s_Variation["$instance"]] = s_Variation.Where(p_P => p_P.Key != "$instance").ToDictionary(p_P => p_P.Key, p_P => p_P.Value);

        var s_Partition = new Dictionary<string, object>
        {
            ["PrimaryInstanceGuid"] = p_HiddenInstance,
            ["Instances"] = s_Instances,
            ["Name"] = p_Name,
            ["PartitionGuid"] = p_Partition,
        };
        File.WriteAllText(p_Path, JsonSerializer.Serialize(s_Partition, p_Json), new UTF8Encoding(false));
    }
}
