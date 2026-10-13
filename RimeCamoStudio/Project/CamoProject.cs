using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimeCamoStudio.Project;

/// <summary>One camo the user is authoring: an image, what it is called, and which weapons offer it.</summary>
public sealed class Camo
{
    public string Name { get; set; } = "";

    /// <summary>The blurb the menu shows beside the row. Literal text — no localisation id is involved.</summary>
    public string Description { get; set; } = "";

    /// <summary>The user's own picture for the row, or null to cut it out of the pattern when baking.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>The pattern the user brought in, tiled over the weapon. The thumbnail is derived from it.</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>
    /// What a camo's tiling is until anything says otherwise. A weapon that declares no CamoTiling of its
    /// own — the ones wearing a no-camo preset, the M416 among them — starts here, and this number is then
    /// what the preview runs and what the package ships, so it is named once and read from here.
    /// </summary>
    public const double DefaultTiling = 3.0;

    /// <summary>
    /// How many times the pattern repeats across the weapon — the shader's CamoTiling.
    ///
    /// ⛔ REFUTED 2026-09-20 (measured over the 59 weapons with --basevalues, do not restore the old claim):
    /// this used to say "3, which is what the game's own camos use". It is NOT. The game's own camos run
    /// tiling 4 on 36 of the 59 weapons and 3 on 15 (8 of them on all their camos); on 44 weapons the number
    /// 3 never appears at all, and the spread is wide (F2000 0.3, AK74M 2, values from -3.5 to 5). So 3 is
    /// OUR default, not the game's, and a camo authored without touching the slider ships a tiling the
    /// weapon's own camos do not use. Whether the form should instead start at the weapon's own number is
    /// a deliberate decision, not a silent change: it moves how every camo baked from the form looks.
    /// </summary>
    public double Tiling { get; set; } = DefaultTiling;

    /// <summary>How much the camo is rubbed away at the edges — WearAmount. The game's own camos use 10.</summary>
    public double Wear { get; set; } = 10.0;

    /// <summary>
    /// Whether the simple form ever wrote Tiling and Wear. Until it has, they are defaults, and a default
    /// must not override the weapon's own material in the preview — a camo authored with the graph alone
    /// would otherwise change look the moment it was previewed.
    /// </summary>
    public bool SimpleEdited { get; set; }

    /// <summary>
    /// The form's controls the user MOVED since it was last shown ("tiling", "wear") — only those are written into the document (review
    /// 2026-09-29: a pattern chosen, or the wear moved, wrote the tiling too — the number the slider happened to show, clamped to its range
    /// or read off another subject — and shared it across the tab's subjects). Emptied each time the form is shown again. Not saved.
    /// </summary>
    [JsonIgnore]
    public HashSet<string> MovedControls { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders of the weapons that offer this camo. Empty means every weapon in the catalogue.</summary>
    public List<string> Weapons { get; set; } = new();

    /// <summary>
    /// The vehicles the bake dialog ticked last time (its keys, "folder@class"), so reopening it — or opening the camo saved ready to bake —
    /// asks with that answer. Empty = none remembered: the vehicle on screen, as always.
    /// </summary>
    public List<string> Vehicles { get; set; } = new();

    /// <summary>
    /// The menu family this camo is filed under -- one of the eight buttons -- or "" to let the screens
    /// guess it from the name, which is what every camo did before 2026-09-20. It rides in the table.
    /// </summary>
    public string Family { get; set; } = "";

    /// <summary>
    /// Whether the package also paints the attachments of its weapons (an entry per part, picked in the
    /// part's own window). Remembered so reopening the bake dialog asks with the answer given last time --
    /// a choice saved but not restored is, from the outside, the same as one never saved.
    /// </summary>
    public bool Accessories { get; set; }

    /// <summary>
    /// Whether the package is the look its weapons wear with NO camo picked (the bake dialog's box). Remembered like
    /// the attachments' choice, so reopening the dialog asks with the answer given last time.
    /// </summary>
    public bool DefaultLook { get; set; }

    /// <summary>
    /// A soldier skin's parts' OWN cells (each part of the soldier can be a different thing): per part ("head", "upper",
    /// "lower") the picture chosen for its tab's cell and the family it is filed under there. Remembered like the rest of the dialog.
    /// </summary>
    public Dictionary<string, string> PartThumbnails { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PartFamilies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>…and the name each part's own cell reads there (each part can have a name of its own).</summary>
    public Dictionary<string, string> PartNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>…and the text each part's own cell shows in the INFO box.</summary>
    public Dictionary<string, string> PartDescriptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The borrowed description this camo's row wears. Written down rather than recomputed: a player's
    /// loadout stores the camo BY IDENTIFIER, so this number is part of what has already shipped.
    /// </summary>
    public uint Identifier { get; set; }

    [JsonIgnore] public bool EveryWeapon => Weapons.Count == 0;
}

/// <summary>The user's work: a set of camos and the mod they bake into.</summary>
public sealed class CamoProject
{
    public string ModName { get; set; } = "MyCamos";
    public List<Camo> Camos { get; set; } = new();

    /// <summary>Maps the mod builds into. One superbundle each, because the database a camo entry patches
    /// is a per-level resource.</summary>
    public List<string> Levels { get; set; } = new();

    public static CamoProject Load(string p_Path) =>
        JsonSerializer.Deserialize<CamoProject>(File.ReadAllText(p_Path))
        ?? throw new InvalidDataException($"'{p_Path}' is not a camo project.");

    public void Save(string p_Path) =>
        File.WriteAllText(p_Path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));

    public IEnumerable<(string Camo, uint Identifier)> Assignments()
    {
        foreach (var s_Camo in Camos)
            if (s_Camo.Identifier != 0)
                yield return (s_Camo.Name, s_Camo.Identifier);
    }
}
