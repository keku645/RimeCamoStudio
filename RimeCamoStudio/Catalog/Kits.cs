using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Catalog;

/// <summary>
/// The primary weapons each kit offers — the list the customize screen's ARMA PRINCIPAL arrows cycle
/// through — measured from the game's own kit tables (Data/kits.json). Fifteen weapons, the shotguns and
/// the PDWs, are in every kit; every other primary belongs to exactly one.
///
/// Why the studio cares: a borrowed menu row (a hook) can mean a DIFFERENT camo in each kit's list. The
/// label of a row is written when the accessories screen opens and holds for the whole arrow cycle, and
/// the arrows never leave a kit's list — measured 2026-09-11 on the bench: the same identifier read "ROJO"
/// on the M416 (assault) and "AZUL SCAR-H" on the SCAR-H (engineer), by the arrows too, each painting its
/// own. So a hook has four slots, one per kit, and a camo takes only the slots of the kits its weapons
/// belong to. A camo on an all-kit weapon (or on every weapon) takes all four.
/// </summary>
public sealed class Kits
{
    /// <summary>The four kit lists, in the order the slots are reported.</summary>
    public static readonly IReadOnlyList<string> Names = new[] { "assault", "engineer", "support", "recon" };

    private sealed class Table
    {
        [JsonPropertyName("measured")] public string Measured { get; set; } = "";
        [JsonPropertyName("exclusive")] public Dictionary<string, List<string>> Exclusive { get; set; } = new();
        [JsonPropertyName("allKit")] public List<string> AllKit { get; set; } = new();
    }

    private readonly Table m_Table;
    private readonly Dictionary<string, string> m_KitOfFolder = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> m_AllKitFolders = new(StringComparer.OrdinalIgnoreCase);

    private Kits(Table p_Table)
    {
        m_Table = p_Table;
        foreach (var (s_Kit, s_Unlocks) in p_Table.Exclusive)
        foreach (var s_Unlock in s_Unlocks)
            m_KitOfFolder[FolderOfUnlock(s_Unlock)] = s_Kit;

        foreach (var s_Unlock in p_Table.AllKit)
            m_AllKitFolders.Add(FolderOfUnlock(s_Unlock));
    }

    public static Kits Load(string? p_Path = null)
    {
        var s_Path = p_Path ?? Path.Combine(AppContext.BaseDirectory, "Data", "kits.json");
        var s_Table = JsonSerializer.Deserialize<Table>(File.ReadAllText(s_Path))
                      ?? throw new InvalidDataException($"'{s_Path}' is not a kit table.");
        return new Kits(s_Table);
    }

    /// <summary>Where the table came from, for whoever prints it.</summary>
    public string Measured => m_Table.Measured;

    /// <summary>Unlock names of the weapons exclusive to a kit, in the arrows' order.</summary>
    public IReadOnlyList<string> ExclusiveOf(string p_Kit) =>
        m_Table.Exclusive.TryGetValue(p_Kit, out var s_List) ? s_List : Array.Empty<string>();

    /// <summary>Unlock names of the weapons every kit offers.</summary>
    public IReadOnlyList<string> AllKit => m_Table.AllKit;

    /// <summary>
    /// The catalogue folder of a weapon unlock: the second segment of its name ("Weapons/M416/U_M416" →
    /// "M416"), which is how the catalogue, the packages and the framework's variation patterns all name a
    /// weapon.
    /// </summary>
    public static string FolderOfUnlock(string p_Unlock)
    {
        var s_Parts = p_Unlock.Split('/');
        return s_Parts.Length >= 2 ? s_Parts[1] : p_Unlock;
    }

    /// <summary>Whether a weapon (catalogue folder) is offered by every kit — a shotgun or a PDW.</summary>
    public bool IsAllKit(string p_Folder) => m_AllKitFolders.Contains(p_Folder);

    /// <summary>The one kit a weapon (catalogue folder) is exclusive to, or null for an all-kit or unknown weapon.</summary>
    public string? ExclusiveKitOf(string p_Folder) => m_KitOfFolder.GetValueOrDefault(p_Folder);

    /// <summary>
    /// The kit lists a camo offered on these weapons occupies, in <see cref="Names"/> order: the kit of each
    /// exclusive weapon; all four for an all-kit weapon, for a weapon the table does not know (a sidearm,
    /// which every kit's sidearm list shows), and for an empty list (= every weapon).
    /// </summary>
    public IReadOnlyList<string> KitsOfWeapons(IReadOnlyCollection<string> p_Folders)
    {
        if (p_Folders.Count == 0)
            return Names;

        var s_Kits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s_Folder in p_Folders)
        {
            var s_Kit = ExclusiveKitOf(s_Folder);
            if (s_Kit == null)
                return Names;

            s_Kits.Add(s_Kit);
        }

        return Names.Where(s_Kits.Contains).ToList();
    }

    /// <summary>
    /// Writes the framework's copy of the table as a Lua module (ext/Shared/Camos/kits.lua): the lists, and
    /// the two functions the loader uses — resolving each unlock to its weapon blueprint at level load and
    /// answering the kit of a blueprint name (what the menu's mannequin reports). Generated so the mod and
    /// the studio can never disagree about which weapon is in which kit.
    /// </summary>
    public void WriteLua(string p_Path)
    {
        var s_Text = new StringBuilder();
        s_Text.AppendLine("-- GENERATED by Rime Camo Studio from Data/kits.json — do not edit by hand. Regenerated by Register.");
        s_Text.AppendLine("-- The primary weapons each kit offers, measured from the game's kit tables (" + m_Table.Measured.Replace("\n", " ") + ").");
        s_Text.AppendLine("-- Fifteen weapons -- the shotguns and the PDWs -- are in every kit; every other primary belongs to exactly one,");
        s_Text.AppendLine("-- and two weapons of different kits never share an arrow cycle. A borrowed row can therefore mean a different");
        s_Text.AppendLine("-- camo in each kit (see index.lua's `kits`), and never on an all-kit weapon.");
        s_Text.AppendLine();
        s_Text.AppendLine("local Kits = {}");
        s_Text.AppendLine();
        s_Text.AppendLine("---Unlock names of the weapons exclusive to each kit, in the arrows' order.");
        s_Text.AppendLine("Kits.EXCLUSIVE = {");
        foreach (var s_Kit in Names)
        {
            s_Text.AppendLine($"\t{s_Kit} = {{");
            foreach (var s_Unlock in ExclusiveOf(s_Kit))
                s_Text.AppendLine($"\t\t{Mod.FrameworkMod.LuaQuote(s_Unlock)},");
            s_Text.AppendLine("\t},");
        }

        s_Text.AppendLine("}");
        s_Text.AppendLine();
        s_Text.AppendLine("---Unlock names of the weapons every kit offers (shotguns and PDWs).");
        s_Text.AppendLine("Kits.ALL_KIT = {");
        foreach (var s_Unlock in m_Table.AllKit)
            s_Text.AppendLine($"\t{Mod.FrameworkMod.LuaQuote(s_Unlock)},");
        s_Text.AppendLine("}");
        s_Text.AppendLine();
        s_Text.AppendLine("local m_KitOfBlueprint = nil   -- blueprint name -> kit, resolved from the unlocks at level load");
        s_Text.AppendLine();
        s_Text.AppendLine("---Resolves each exclusive unlock to its weapon blueprint name (the name the mannequin reports), once per");
        s_Text.AppendLine("---level. Reads the game's data, never a hand-written blueprint name (the AK74M's is \"Weapons/AK74M/AK74\").");
        s_Text.AppendLine("function Kits.Resolve(p_Log)");
        s_Text.AppendLine("\tm_KitOfBlueprint = {}");
        s_Text.AppendLine("\tlocal s_Counts = {}");
        s_Text.AppendLine();
        s_Text.AppendLine("\tfor l_Kit, l_Unlocks in pairs(Kits.EXCLUSIVE) do");
        s_Text.AppendLine("\t\ts_Counts[l_Kit] = 0");
        s_Text.AppendLine();
        s_Text.AppendLine("\t\tfor _, l_Unlock in ipairs(l_Unlocks) do");
        s_Text.AppendLine("\t\t\tpcall(function()");
        s_Text.AppendLine("\t\t\t\tlocal s_Asset = ResourceManager:SearchForDataContainer(l_Unlock)");
        s_Text.AppendLine();
        s_Text.AppendLine("\t\t\t\tif s_Asset ~= nil then");
        s_Text.AppendLine("\t\t\t\t\tlocal s_Blueprint = SoldierWeaponUnlockAsset(s_Asset).weapon");
        s_Text.AppendLine();
        s_Text.AppendLine("\t\t\t\t\tif s_Blueprint ~= nil then");
        s_Text.AppendLine("\t\t\t\t\t\tm_KitOfBlueprint[tostring(s_Blueprint.name)] = l_Kit");
        s_Text.AppendLine("\t\t\t\t\t\ts_Counts[l_Kit] = s_Counts[l_Kit] + 1");
        s_Text.AppendLine("\t\t\t\t\tend");
        s_Text.AppendLine("\t\t\t\tend");
        s_Text.AppendLine("\t\t\tend)");
        s_Text.AppendLine("\t\tend");
        s_Text.AppendLine("\tend");
        s_Text.AppendLine();
        s_Text.AppendLine("\tif p_Log ~= nil then");
        s_Text.AppendLine("\t\tp_Log(string.format(\"kits resolved: assault %d/%d, engineer %d/%d, support %d/%d, recon %d/%d exclusive weapons\",");
        s_Text.AppendLine("\t\t\ts_Counts.assault or 0, #Kits.EXCLUSIVE.assault, s_Counts.engineer or 0, #Kits.EXCLUSIVE.engineer,");
        s_Text.AppendLine("\t\t\ts_Counts.support or 0, #Kits.EXCLUSIVE.support, s_Counts.recon or 0, #Kits.EXCLUSIVE.recon))");
        s_Text.AppendLine("\tend");
        s_Text.AppendLine("end");
        s_Text.AppendLine();
        s_Text.AppendLine("---The kit a weapon (blueprint name) is exclusive to, or nil for an all-kit weapon (or an unresolved one).");
        s_Text.AppendLine("function Kits.KitOf(p_BlueprintName)");
        s_Text.AppendLine("\tif m_KitOfBlueprint == nil or p_BlueprintName == nil then");
        s_Text.AppendLine("\t\treturn nil");
        s_Text.AppendLine("\tend");
        s_Text.AppendLine();
        s_Text.AppendLine("\treturn m_KitOfBlueprint[p_BlueprintName]");
        s_Text.AppendLine("end");
        s_Text.AppendLine();
        s_Text.AppendLine("return Kits");

        Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
        File.WriteAllText(p_Path, s_Text.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
    }
}
