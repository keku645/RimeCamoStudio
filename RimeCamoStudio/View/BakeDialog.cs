using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RimeCamoStudio.Catalog;

namespace RimeCamoStudio.View;

/// <summary>What the user chose when baking.</summary>
public sealed class BakeRequest
{
    /// <summary>The camo's name: the row's title, and what the package and its files are called.</summary>
    public string Name { get; init; } = "";

    /// <summary>Folders of the weapons the camo is offered on. Empty means every weapon.</summary>
    public List<string> Weapons { get; init; } = new();

    /// <summary>The text the menu shows in the info box beside the row.</summary>
    public string Description { get; init; } = "";

    /// <summary>The user's picture for the row, or null to cut it out of the pattern.</summary>
    public string? Thumbnail { get; init; }

    /// <summary>
    /// The menu family this camo is filed under -- one of the eight buttons -- or "" for the guess by name.
    /// It travels in the table's package header, where the screens' script prefers it to its own keywords.
    /// </summary>
    public string Family { get; init; } = "";

    /// <summary>
    /// Whether the package also paints the attachments of the weapons it ships for: every optic, grip, laser
    /// and silencer they offer gets its own painted copy and its own entry, so a player picks this camo on
    /// each part. Off, the package is the weapon bodies alone, which is what the button did until today.
    /// </summary>
    public bool Accessories { get; init; }

    /// <summary>
    /// The package is the look its weapons wear with NO camo picked (BakePlan.DefaultLook; e.g. the pistols as shipped with
    /// the emblem slot): dressed by the framework, listed in no camo window.
    /// </summary>
    public bool DefaultLook { get; init; }

    /// <summary>
    /// The game camo (a CamoSession.NativeCamos key: abu, atacs, …) this package IS, or null: the bake ships it as that one of the framework's
    /// DEFAULT camos — defaultcamos/, the game's own row for the pattern, no picture of its own — instead of a camo of its own name
    /// (the eight native camos remade to support emblems — the pattern with the package's own shader and the
    /// emblem slot, which PlanForNative's values-only plan cannot carry).
    /// </summary>
    public string? NativeIdentity { get; init; }

    /// <summary>
    /// The pieces this camo paints, as "&lt;weapon&gt;/&lt;tag&gt;" — empty for a weapon's camo, which is the
    /// usual case. When it has entries the camo is OF A PIECE: no weapon body is painted and no weapon's menu
    /// gets a row, so the only thing the bake needs is which piece and on which weapons.
    /// </summary>
    public List<string> Pieces { get; init; } = new();

    /// <summary>
    /// Catalogue folders of the VEHICLES the camo paints: each body gets a clone wearing the camo, and the
    /// camo is offered in that vehicle's class window. Empty = no vehicle. With vehicles and no weapon chosen, Weapons is
    /// ["none"] -- an empty weapon list means every weapon to the plan.
    /// </summary>
    public List<string> Vehicles { get; init; } = new();

    /// <summary>
    /// The soldier a SKIN dresses (the Soldiers tab: the soldier on screen, the whole of him) — his catalogue key — or null
    /// for a camo of weapons or vehicles. With one, Weapons is ["none"] and Vehicles empty.
    /// </summary>
    public string? Soldier { get; init; }

    /// <summary>The parts ("head", "upper", "lower") whose camo goes on ALL the cloth — a white mask of the package's; the rest keep the game's.</summary>
    public List<string> WhiteMask { get; init; } = new();

    /// <summary>
    /// Per part ("head", "upper", "lower"), the user's picture for that part's OWN cell — the one on its tab of the soldier's window (each
    /// part of the body may be something different). A part not listed cuts its picture out of its own pattern.
    /// </summary>
    public Dictionary<string, string> PartThumbnails { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per part, the family its own cell is filed under on its tab; a part not listed is filed as the skin is.</summary>
    public Dictionary<string, string> PartFamilies { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per part, what its own cell READS on its tab (each part may have a different name); a part not
    /// listed reads the skin's name. Only the cell's words: the skin is one, and its identity is its own name.
    /// </summary>
    public Dictionary<string, string> PartNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per part, the text its own cell shows in the INFO box on its tab; a part not listed shows the skin's.</summary>
    public Dictionary<string, string> PartDescriptions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ⭐ Per part, the OTHER soldiers it goes to too (this model only, US only, RU only, everyone…, ticked per part), by catalogue
    /// key; a part not listed is this soldier's alone.
    /// </summary>
    public Dictionary<string, List<string>> PartSoldiers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A soldier the skin's parts can go to, as the dialog lists them (the catalogue's sixteen).</summary>
public sealed class BakeSoldierChoice
{
    public string Key { get; init; } = "";

    /// <summary>"US" or "RU".</summary>
    public string Team { get; init; } = "";

    /// <summary>"Assault", "Engineer", "Support", "Recon".</summary>
    public string Kit { get; init; } = "";

    public bool Aftermath { get; init; }
}

/// <summary>
/// The soldier on screen as the Soldiers tab's bake sees him, measured by the session before the dialog opens: each of his three parts, what
/// was changed on it and what of that a skin can carry. The dialog asks only the mask of each part (chosen per part).
/// </summary>
public sealed class BakeSoldier
{
    public string Key { get; init; } = "";

    /// <summary>"US · Assault".</summary>
    public string Display { get; init; } = "";

    public IReadOnlyList<BakeSoldierPart> Parts { get; init; } = Array.Empty<BakeSoldierPart>();

    /// <summary>Every soldier of the catalogue (him included): the ones a part can go to too.</summary>
    public IReadOnlyList<BakeSoldierChoice> Everyone { get; init; } = Array.Empty<BakeSoldierChoice>();

    /// <summary>
    /// A skin name -> the soldier ANOTHER skin of that name is already baked for (its display), or null (review 2026-09-29: a skin is known by
    /// its name alone — its folder and the identifier a player's pick is kept under —, so baking "Arctic" on RU · Assault replaced the
    /// "Arctic" of US · Assault without a word).
    /// </summary>
    public Func<string, string?>? BakedForAnother { get; init; }
}

/// <summary>One part of the soldier in the bake dialog.</summary>
public sealed class BakeSoldierPart
{
    /// <summary>"head", "upper", "lower".</summary>
    public string Part { get; init; } = "";

    /// <summary>"Head", "Upper body", "Lower body".</summary>
    public string Display { get; init; } = "";

    /// <summary>What it carries into the skin, in the user's words ("pattern snow.png, 2 number(s)"), or "" when nothing was changed on it.</summary>
    public string Carries { get; init; } = "";

    /// <summary>Whether something of it was changed that the skin carries (then it is in the skin whatever its mask).</summary>
    public bool Changed { get; init; }

    /// <summary>Whether it has camo cloth at all (a material binding CamoTile): without it a mask means nothing and nothing can go on it.</summary>
    public bool HasCloth { get; init; } = true;

    /// <summary>A picture on its Mask node: that picture is the mask, and the choice is not asked.</summary>
    public bool MaskPicture { get; init; }

    /// <summary>What was changed on it and cannot ship, said beside it; "" when nothing.</summary>
    public string Note { get; init; } = "";

    /// <summary>The file name of the pattern on its camo node (its CamoTile picture) — what its own cell's picture is cut from —, or "".</summary>
    public string Pattern { get; init; } = "";

    /// <summary>The picture chosen for its own cell at this skin's last bake, or "".</summary>
    public string Thumbnail { get; init; } = "";

    /// <summary>The family chosen for its own cell at this skin's last bake, or "" (as the skin's).</summary>
    public string Family { get; init; } = "";

    /// <summary>The name typed for its own cell at this skin's last bake, or "" (the skin's).</summary>
    public string Name { get; init; } = "";

    /// <summary>The description typed for its own cell at this skin's last bake, or "".</summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Why this part cannot go to other soldiers, or null when it can: only a SIMPLE part travels (its pattern, its mask as the game's own
    /// or all the cloth, its numbers) — a picture painted on this model's layout, or a graph of its own, does not.
    /// </summary>
    public string? NotForOthers { get; init; }

    /// <summary>Its camo on all the cloth, as the Soldiers panel's box has it (one state — the dialog opens on it).</summary>
    public bool AllCloth { get; init; }

    /// <summary>⚠ Its meshes where the game's mask of the look it starts from puts the camo nowhere ("" when none): said under "Where the
    /// game puts it".</summary>
    public string StockMaskHidden { get; init; } = "";
}

/// <summary>
/// The piece the camo is being authored on, measured by the session before the dialog opens: what it is,
/// where it was picked, and every weapon that carries THE SAME piece.
///
/// The dialog does not look any of this up. It is the answer to "what can the camo tell us itself?", which is
/// the question that decides what this dialog asks (the request for the bake menu: options that appear or
/// disappear depending on what exactly is being baked, and no choice of weapons when baking the iron sights,
/// since every weapon has iron sights of its own).
/// </summary>
public sealed class BakePiece
{
    /// <summary>The weapon it is picked under — the one on screen.</summary>
    public string Weapon { get; init; } = "";

    /// <summary>What the piece is called where the user picks it ("iron sights", "Sound_Suppressor"...).</summary>
    public string Display { get; init; } = "";

    /// <summary>"&lt;weapon&gt;/&lt;tag&gt;" for the weapon on screen: the piece as the bake names it.</summary>
    public string Host { get; init; } = "";

    /// <summary>
    /// Every weapon that carries this same piece, as "&lt;weapon&gt;/&lt;tag&gt;", the host first. The tag is
    /// read per weapon and NOT copied from the host: the same suppressor is "Silencer" on one gun and
    /// "Sound_Suppressor" on the next (measured: 33 weapons carry sound_supressor under two names).
    /// </summary>
    public IReadOnlyList<string> Carriers { get; init; } = Array.Empty<string>();

    /// <summary>How many of its meshes a camo can go on (first and third person are two meshes of one piece).</summary>
    public int Meshes { get; init; }
}

/// <summary>
/// Asks what the camo is called, which weapons it goes on, what its row says and what picture it shows, and
/// shows where the package will land.
///
/// The weapon list lives HERE and not beside the camo's name (the weapon list comes at the end, when Bake
/// is pressed): choosing what a camo LOOKS like and choosing where it is
/// OFFERED are different jobs, and only the second one belongs to shipping. The name, the text and the
/// picture are asked here too: they are what the MENU shows, decided when shipping. There
/// is no mod name to type: the package is dropped into the camo framework and registered there by the studio.
/// </summary>
public sealed class BakeDialog : Window
{
    /// <summary>The shape of the game's own camo thumbnails, and what every picture is scaled and cut to.</summary>
    public const int ThumbnailWidth = 256;
    public const int ThumbnailHeight = 64;

    /// <summary>
    /// The menu's eight family buttons, in the order they are drawn (make_camomenu_doc.py FAMILIES). The
    /// first entry is not a family: it is the old behaviour, where the family is guessed from the camo's
    /// name, and it stays the default so a camo baked before today keeps filing itself the same way.
    /// </summary>
    public static readonly string[] Families =
        { "", "MISC", "ADAPTIVE", "AUTUMN", "DESERT", "NAVAL", "SNOW", "URBAN", "WOODLAND" };

    private const string c_AutoFamily = "(from the camo's name)";

    /// <summary>
    /// How many painted attachments the biggest package actually RUN in the game carried (the L85A2 and the
    /// M240 complete, 2026-09-20). It is not a limit — no ceiling has been found — but a package far past it
    /// is untried ground, and the summary says so rather than letting the user find out in a server.
    /// </summary>
    private const int PartsTriedInGame = 39;

    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));
    private static readonly Brush s_Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x60));

    private readonly List<CheckBox> m_Weapons = new();

    /// <summary>The vehicle boxes (one per catalogue vehicle that takes a camo), empty when the dialog has no catalogue.</summary>
    private readonly List<CheckBox> m_Vehicles = new();
    private readonly VehicleCatalog? m_VehicleCatalog;
    private readonly IReadOnlyCollection<string> m_VehiclesChecked;

    /// <summary>
    /// The dialog of the VEHICLES tab (what shows depends on the tab: on the vehicles the whole weapons section goes away, and
    /// when baking weapons the vehicles' area does not show): the vehicle list takes the weapon list's place and neither the
    /// weapons nor their attachments are asked. On the Weapons tab the vehicle list is not built at all.
    /// </summary>
    private readonly bool m_VehicleMode;
    private readonly TextBox m_Name;
    private readonly TextBox m_Description;
    private readonly ComboBox m_Family;
    private readonly CheckBox m_Accessories;

    /// <summary>
    /// ⭐ THE LOOK WITH NO CAMO PICKED (a box in the Bake dialog): ticked, the package is what the ticked weapons wear when the
    /// player picks no camo — the framework takes the game's no-camo row of each weapon for it, keeps its name and picture, and
    /// lists the package in no camo window (BakeRequest.DefaultLook, BakePlan.DefaultLook, the index's defaultLook). Remembered per camo.
    /// </summary>
    private readonly CheckBox m_DefaultLook;
    private readonly TextBox m_Thumbnail;
    private readonly TextBlock m_Summary;
    private readonly Button m_Ok;
    private readonly WeaponCatalog m_Catalog;
    private readonly string m_Target;

    /// <summary>The piece being authored, or null when the camo is a weapon's (the usual case).</summary>
    private readonly BakePiece? m_Piece;

    /// <summary>
    /// The soldier on screen, on the Soldiers tab: the dialog asks no subjects at all — the skin is his, all of him — only
    /// where the camo lies on each part. Null on every other tab.
    /// </summary>
    private readonly BakeSoldier? m_Soldier;

    /// <summary>Per part, its "all the cloth" choice (the other radio of the pair is "where the game puts it").</summary>
    private readonly Dictionary<string, RadioButton> m_AllCloth = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RadioButton> m_StockMask = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per part that can be in the skin, its OWN cell on its tab of the soldier's window (each part of the body may be something different,
    /// so the thumbnail and category are chosen per section): the picture (empty =
    /// cut out of the part's own pattern) and the category (the first row = as the skin's), and the block that holds them.
    /// </summary>
    private readonly Dictionary<string, TextBox> m_PartThumbnail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ComboBox> m_PartFamily = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> m_PartName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBox> m_PartDescription = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> m_PartCell = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The soldier's parts the form shows — the ones changed, or with all the cloth ticked on the Soldiers panel.</summary>
    private readonly List<BakeSoldierPart> m_ShownParts;

    /// <summary>The part whose cell the whole soldier's (OUTFIT) wears: the upper body when shown, else the first part shown.</summary>
    private readonly string m_LeadPart;

    /// <summary>A part as a sentence names it: "the upper body", "the head".</summary>
    private static string PartWord(string p_Part) => p_Part switch
    {
        "upper" => "upper body",
        "lower" => "lower body",
        _ => p_Part,
    };

    /// <summary>⭐ Per part, the other soldiers' boxes (ticked per part) and the block that holds them.</summary>
    private readonly Dictionary<string, Dictionary<string, CheckBox>> m_PartOthers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> m_PartOthersBlock = new(StringComparer.OrdinalIgnoreCase);

    private const string c_SkinFamily = "(as the upper body's)";

    /// <summary>The tab of the soldier's window a part's cell sits on, as the game's English window names it.</summary>
    private static string TabOf(string p_Part) => p_Part switch
    {
        "head" => "HEAD",
        "upper" => "TORSO",
        "lower" => "LEGS",
        _ => p_Part.ToUpperInvariant(),
    };

    /// <summary>Piece mode: whether it is painted on every weapon that carries it, or only on this one.</summary>
    private readonly RadioButton? m_PieceHere;
    private readonly RadioButton? m_PieceEverywhere;

    /// <summary>
    /// How many attachment rows the chosen weapons offer. It comes from the session, which counts them with
    /// the SAME call the plan expands ("all"), so the number in front of the user is the number that ships —
    /// minus the parts with nothing to paint, which the bake drops and says so.
    /// </summary>
    private readonly Func<IReadOnlyCollection<string>, int>? m_CountAccessories;

    /// <param name="p_Owner">The editor window, which owns the dark chrome the dialog dresses itself with.</param>
    private BakeDialog(Window? p_Owner, WeaponCatalog p_Catalog, string p_Name, string p_Description,
        string? p_Thumbnail, string p_Target, string p_Family = "", bool p_Accessories = false,
        Func<IReadOnlyCollection<string>, int>? p_CountAccessories = null, BakePiece? p_Piece = null,
        VehicleCatalog? p_Vehicles = null, IReadOnlyCollection<string>? p_VehiclesChecked = null, bool p_VehicleMode = false,
        BakeSoldier? p_Soldier = null, bool p_DefaultLook = false)
    {
        m_Catalog = p_Catalog;
        // a soldier's skin is neither a piece's nor a vehicle's: the Soldiers tab asks it alone
        m_Soldier = p_Piece == null ? p_Soldier : null;
        // a vehicle camo is never a piece's (the pieces are weapon attachments), and it needs the catalogue to list them
        m_VehicleMode = p_VehicleMode && p_Piece == null && m_Soldier == null && p_Vehicles != null;
        m_VehicleCatalog = m_VehicleMode ? p_Vehicles : null;
        m_VehiclesChecked = p_VehiclesChecked ?? Array.Empty<string>();
        m_Target = p_Target;
        m_CountAccessories = p_CountAccessories;
        m_Piece = p_Piece;

        Title = p_Piece != null ? $"Bake camo — {p_Piece.Display}"
            : m_Soldier != null ? $"Bake soldier skin — {m_Soldier.Display}"
            : m_VehicleMode ? "Bake vehicle camo" : "Bake camo";
        Background = s_Panel;
        Width = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        if (p_Piece != null)
        {
            m_PieceHere = PieceChoice($"Only on {p_Piece.Weapon}",
                $"The package carries this piece for {p_Piece.Weapon} alone.");

            m_PieceEverywhere = PieceChoice(
                p_Piece.Carriers.Count > 1
                    ? $"On the {p_Piece.Carriers.Count} weapons that carry this piece"
                    : "On every weapon that carries this piece (only this one does)",
                p_Piece.Carriers.Count > 1
                    ? "The same piece hangs off other guns, and it is one object: painted here, painted there. " +
                      "The painted meshes are shared, so this costs the package almost nothing — what it adds " +
                      $"is one entry per weapon while a level is loaded ({p_Piece.Carriers.Count} of them)."
                    : "No other weapon carries it, so this is the same as the choice above.");

            m_PieceEverywhere.IsEnabled = p_Piece.Carriers.Count > 1;
            // The piece is ONE object: a camo on it belongs wherever it hangs, and the cheap answer is also
            // the one the user means. Where it hangs on one gun only, both choices are the same thing.
            if (p_Piece.Carriers.Count > 1)
                m_PieceEverywhere.IsChecked = true;
            else
                m_PieceHere.IsChecked = true;

            m_PieceHere.Checked += (_, _) => Refresh();
            m_PieceEverywhere.Checked += (_, _) => Refresh();
        }

        // ⭐ ONLY THE PARTS THAT WERE CHANGED (the head, upper and lower body sections show only when modified: a camo made for the head
        // alone showing the lower and upper body too was confusing): a part
        // changed on him, or with the Soldiers panel's "camo on all the cloth" ticked — what goes into the skin. The rest are said in one line
        // and stay as the game ships them; all the cloth on an untouched part is the panel's box, which then shows it here.
        // CAMO_BAKE_ALLPARTS_OLD=1 = every part, as before.
        // (…and a part with something changed on it that cannot ship — stickers, a material's own graph, a picture where it has no camo cloth:
        // its note says so here, before the bake; hiding it said "nothing was changed on it" — review 2026-09-29)
        m_ShownParts = m_Soldier?.Parts
            .Where(p_P => p_P.Changed || p_P.AllCloth || p_P.Note.Length > 0 || Environment.GetEnvironmentVariable("CAMO_BAKE_ALLPARTS_OLD") == "1")
            .ToList() ?? new List<BakeSoldierPart>();
        // …and the whole soldier's cell (OUTFIT) wears the upper body's when it is one of them, else the first one's (a skin of his head alone
        // is filed and pictured as his head's cell says — "(as the upper body's)" named a part the form no longer shows)
        m_LeadPart = m_ShownParts.Any(p_P => p_P.Part == "upper") ? "upper" : m_ShownParts.FirstOrDefault()?.Part ?? "upper";

        // ⭐ THE MASK, PER PART (chosen per part): where the game's own mask puts the camo, or all the cloth — a white
        // mask of the skin's (Aftermath's levels load no white texture of the game's). Not asked where a picture on the Mask node already is
        // the mask, nor on a part with no camo cloth.
        foreach (var s_Part in m_ShownParts.Where(p_P => p_P.HasCloth && !p_P.MaskPicture))
        {
            var s_Stock = PieceChoice("Where the game puts it", "The game's own mask of this part: the camo on the cloth it covers, the rest as it ships.");
            var s_All = PieceChoice("All the cloth", "A white mask of the skin's own: the camo on every bit of this part's cloth (the vest, the pockets…).");
            s_Stock.GroupName = s_All.GroupName = "mask_" + s_Part.Part;
            // (as the Soldiers panel's box has it: what the preview showed is what is asked)
            s_Stock.IsChecked = !s_Part.AllCloth;
            s_All.IsChecked = s_Part.AllCloth;
            s_Stock.Checked += (_, _) => Refresh();
            s_All.Checked += (_, _) => Refresh();
            m_StockMask[s_Part.Part] = s_Stock;
            m_AllCloth[s_Part.Part] = s_All;
        }

        m_Name = Field(p_Name, false);
        m_Description = Field(p_Description, true);
        // The editor's dark combo style, the same one the sticker panel takes. The system chrome paints a
        // ComboBox light or dark on its own and NO fixed Background/Foreground survives it — a box dressed
        // by hand comes out white text on white (the category was unreadable, white letters on a white
        // background, unlike the rest of the program's UI), which is the same
        // bite the layer-size box took. Only a template that owns every colour holds, and that template is
        // DarkCombo, in the editor's resources: every other combo of this program wears it.
        m_Family = new ComboBox { MinHeight = 24 };
        if (p_Owner?.TryFindResource("DarkCombo") is Style s_DarkCombo)
            m_Family.Style = s_DarkCombo;

        foreach (var s_Name in Families)
            m_Family.Items.Add(s_Name.Length == 0 ? c_AutoFamily : s_Name);

        var s_Chosen = Array.IndexOf(Families, (p_Family ?? "").Trim().ToUpperInvariant());
        m_Family.SelectedIndex = s_Chosen >= 0 ? s_Chosen : 0;

        // each part that can be in the skin (changed, or with a mask to choose) gets its own cell's picture and category, as last baked
        foreach (var s_Part in m_ShownParts.Where(p_P => p_P.Changed || m_AllCloth.ContainsKey(p_P.Part)))
        {
            var s_Box = Field(s_Part.Thumbnail, false);
            s_Box.IsReadOnly = true;
            s_Box.TextChanged += (_, _) => Refresh();

            var s_Combo = new ComboBox { MinHeight = 24, Width = 150, Margin = new Thickness(6, 0, 0, 0) };
            if (p_Owner?.TryFindResource("DarkCombo") is Style s_PartCombo)
                s_Combo.Style = s_PartCombo;
            // (the lead part's category is the skin's — the OUTFIT cell's —; the other parts follow it unless they choose one)
            foreach (var s_Name in Families)
                s_Combo.Items.Add(s_Name.Length == 0
                    ? s_Part.Part == m_LeadPart ? c_AutoFamily.Replace("camo's", "skin's") : m_LeadPart == "upper" ? c_SkinFamily : $"(as the {PartWord(m_LeadPart)}'s)"
                    : s_Name);
            var s_Was = Array.IndexOf(Families, s_Part.Family.Trim().ToUpperInvariant());
            s_Combo.SelectedIndex = s_Was > 0 ? s_Was : 0;

            m_PartThumbnail[s_Part.Part] = s_Box;
            m_PartFamily[s_Part.Part] = s_Combo;
            m_PartName[s_Part.Part] = Field(s_Part.Name, false);
            m_PartDescription[s_Part.Part] = Field(s_Part.Description, true);
        }

        m_Accessories = new CheckBox
        {
            Content = new TextBlock { Text = "Paint the weapons' attachments too", Foreground = s_Text },
            IsChecked = p_Accessories, Margin = new Thickness(0, 2, 0, 0),
        };

        m_Accessories.Checked += (_, _) => Refresh();
        m_Accessories.Unchecked += (_, _) => Refresh();

        // (the look with no camo picked paints the weapon bodies alone: ticked, the attachments' box is cleared and greyed — a box greyed
        // and ticked reads "it happens anyway" — and given back as it was when this one is cleared)
        m_DefaultLook = new CheckBox
        {
            Content = new TextBlock { Text = "Use as these weapons' look with no camo picked (the menu's no-camouflage entry)", Foreground = s_Text },
            IsChecked = p_DefaultLook, Margin = new Thickness(0, 2, 0, 0),
        };
        var s_AttachmentsBefore = p_Accessories;
        void FollowDefaultLook()
        {
            if (m_DefaultLook.IsChecked == true)
            {
                s_AttachmentsBefore = m_Accessories.IsChecked == true;
                m_Accessories.IsChecked = false;
                m_Accessories.IsEnabled = false;
            }
            else
            {
                m_Accessories.IsEnabled = true;
                m_Accessories.IsChecked = s_AttachmentsBefore;
            }

            Refresh();
        }

        m_DefaultLook.Checked += (_, _) => FollowDefaultLook();
        m_DefaultLook.Unchecked += (_, _) => FollowDefaultLook();
        if (p_DefaultLook)
        {
            m_Accessories.IsChecked = false;
            m_Accessories.IsEnabled = false;
        }
        m_Thumbnail = Field(p_Thumbnail ?? "", false);
        m_Thumbnail.IsReadOnly = true;

        m_Summary = new TextBlock
        {
            Foreground = s_Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        m_Ok = new Button { Content = "Bake", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        m_Ok.Click += (_, _) => DialogResult = true;

        Content = BuildLayout();
        m_Name.TextChanged += (_, _) => Refresh();
        Refresh();
        SizeToFit();
    }

    /// <summary>
    /// Makes the window as tall as the form it holds, capped at the screen.
    ///
    /// ⛔ The bake window must open tall enough by default: on the piece form it hid the Bake button unless
    /// the user stretched the window by hand. The height used to be a
    /// NUMBER chosen per mode, and a number cannot know how tall the text it is holding wrapped to — the piece
    /// form's own explanations pushed the Bake button off the bottom. So it is measured, not chosen.
    /// ⛔ And measuring is not enough on its own: on a short screen the cap would put the button out of reach
    /// again, so the buttons live OUTSIDE the scrolling part (see BuildLayout) and cannot be scrolled away.
    /// Measured with DesiredSize, which INCLUDES the margins — ActualWidth/Height do not, and that is what once
    /// cropped a snapshot (the law is in the seam notes).
    /// </summary>
    private void SizeToFit()
    {
        var s_Content = Content as FrameworkElement;
        if (s_Content == null)
            return;

        s_Content.Measure(new Size(Width, double.PositiveInfinity));

        // the title bar and the window's own borders, which the content knows nothing about
        var s_Chrome = SystemParameters.WindowCaptionHeight + SystemParameters.ResizeFrameHorizontalBorderHeight * 2 + 8;
        var s_Room = SystemParameters.WorkArea.Height;
        if (double.IsNaN(s_Room) || s_Room < 300)
            s_Room = 900;

        Height = Math.Min(Math.Max(s_Content.DesiredSize.Height + s_Chrome, 360), s_Room * 0.95);
    }

    /// <param name="p_Name">The name to start from — the graph's name, or empty when the graph still carries
    /// the preset's name or none, so the user has to type one.</param>
    /// <param name="p_Piece">The piece on screen, measured by the session — null for a weapon's camo.</param>
    public static BakeRequest? Ask(Window? p_Owner, WeaponCatalog p_Catalog, string p_Name,
        string p_Description, string? p_Thumbnail, string p_Target, string p_Family = "",
        bool p_Accessories = false, Func<IReadOnlyCollection<string>, int>? p_CountAccessories = null,
        BakePiece? p_Piece = null, VehicleCatalog? p_Vehicles = null, IReadOnlyCollection<string>? p_VehiclesChecked = null,
        bool p_VehicleMode = false)
    {
        return Build(p_Owner, p_Catalog, p_Name, p_Description, p_Thumbnail, p_Target, p_Family, p_Accessories,
            p_CountAccessories, p_Piece, p_Vehicles, p_VehiclesChecked, p_VehicleMode).ShowAndAnswer(p_Owner);
    }

    /// <summary>
    /// The dialog the bake button asks with, built and NOT shown: the button shows it (<see cref="ShowAndAnswer"/>), a seam
    /// reads it (<see cref="Result"/>, <see cref="CanBake"/>, <see cref="SummaryText"/>) — one constructor for both, so what a
    /// seam reads is what the user gets.
    /// </summary>
    internal static BakeDialog Build(Window? p_Owner, WeaponCatalog p_Catalog, string p_Name, string p_Description,
        string? p_Thumbnail, string p_Target, string p_Family = "", bool p_Accessories = false,
        Func<IReadOnlyCollection<string>, int>? p_CountAccessories = null, BakePiece? p_Piece = null,
        VehicleCatalog? p_Vehicles = null, IReadOnlyCollection<string>? p_VehiclesChecked = null, bool p_VehicleMode = false,
        BakeSoldier? p_Soldier = null, bool p_DefaultLook = false) =>
        new(p_Owner, p_Catalog, p_Name, p_Description, p_Thumbnail, p_Target, p_Family, p_Accessories, p_CountAccessories,
            p_Piece, p_Vehicles, p_VehiclesChecked, p_VehicleMode, p_Soldier, p_DefaultLook);

    /// <summary>What the dialog is made of, for a seam: which sections were built.</summary>
    internal int WeaponBoxCount => m_Weapons.Count;
    internal int VehicleBoxCount => m_Vehicles.Count;
    internal bool AsksAttachments => m_Piece == null && !m_VehicleMode && m_Soldier == null;

    /// <summary>Whether the form asks "the look with no camo" (a weapons' camo: not a piece's, a vehicle's or a skin's) — for a seam.</summary>
    internal bool AsksDefaultLook => AsksAttachments;

    /// <summary>Whether the attachments' box can be clicked (the look with no camo greys it) — for a seam.</summary>
    internal bool AttachmentsEnabled => m_Accessories.IsEnabled;

    /// <summary>The Soldiers tab's form: the soldier's key, or null.</summary>
    internal string? SoldierKey => m_Soldier?.Key;

    /// <summary>The parts whose mask is asked (a pair of radios each).</summary>
    internal IReadOnlyCollection<string> MaskAsked => m_AllCloth.Keys;

    /// <summary>The soldier's parts the form shows (changed, or with all the cloth ticked on the panel), in his order — what a seam checks.</summary>
    internal IReadOnlyList<string> PartsShown => m_ShownParts.Select(p_P => p_P.Part).ToList();

    /// <summary>Picks one part's mask — the user's click, for a driver; false when that part's mask is not asked.</summary>
    internal bool SetAllCloth(string p_Part, bool p_AllCloth)
    {
        if (!m_AllCloth.TryGetValue(p_Part, out var s_All))
            return false;

        if (p_AllCloth)
            s_All.IsChecked = true;
        else
            m_StockMask[p_Part].IsChecked = true;
        return true;
    }

    /// <summary>The parts the skin carries as the form stands: the ones changed, and the ones whose camo goes on all the cloth.</summary>
    internal IReadOnlyList<string> PartsInSkin => m_Soldier == null
        ? Array.Empty<string>()
        : m_Soldier.Parts.Where(p_P => p_P.Changed || AllCloth(p_P.Part)).Select(p_P => p_P.Part).ToList();

    private bool AllCloth(string p_Part) => m_AllCloth.TryGetValue(p_Part, out var s_All) && s_All.IsChecked == true;

    /// <summary>Whether a part's mask choice stands on "All the cloth" — what a seam reads of the dialog it opened.</summary>
    internal bool AllClothChosen(string p_Part) => AllCloth(p_Part);

    /// <summary>The parts whose own cell (picture and category) is asked.</summary>
    internal IReadOnlyCollection<string> PartCellsAsked => m_PartThumbnail.Keys;

    /// <summary>Picks a part's own cell picture — the user's Browse…, for a driver; false when that part's cell is not asked.</summary>
    internal bool SetPartThumbnail(string p_Part, string p_File)
    {
        if (!m_PartThumbnail.TryGetValue(p_Part, out var s_Box))
            return false;

        s_Box.Text = p_File;
        return true;
    }

    /// <summary>Picks a part's own category by its family name ("" = as the skin's) — the user's pick, for a driver.</summary>
    internal bool SetPartFamily(string p_Part, string p_Family)
    {
        var s_At = Array.IndexOf(Families, p_Family.Trim().ToUpperInvariant());
        if (!m_PartFamily.TryGetValue(p_Part, out var s_Combo) || s_At < 0)
            return false;

        s_Combo.SelectedIndex = s_At;
        return true;
    }

    /// <summary>Types a part's own cell's description — the user's typing, for a driver; false when that part's cell is not asked.</summary>
    internal bool SetPartDescription(string p_Part, string p_Text)
    {
        if (!m_PartDescription.TryGetValue(p_Part, out var s_Box))
            return false;

        s_Box.Text = p_Text;
        return true;
    }

    /// <summary>Whether the skin-wide description, category and picture boxes are in the form (the soldier's has none of them).</summary>
    internal bool AsksSkinCellBoxes => m_Soldier == null;

    /// <summary>Types a part's own cell's name — the user's typing, for a driver; false when that part's cell is not asked.</summary>
    internal bool SetPartName(string p_Part, string p_Name)
    {
        if (!m_PartName.TryGetValue(p_Part, out var s_Box))
            return false;

        s_Box.Text = p_Name;
        return true;
    }

    /// <summary>What a part's category box shows, read off the box (for a seam: the row the user sees).</summary>
    internal string PartFamilyShown(string p_Part) =>
        m_PartFamily.TryGetValue(p_Part, out var s_Combo) ? s_Combo.SelectedItem?.ToString() ?? "(none)" : "(not asked)";

    /// <summary>The "attachments too" box, ticked or not — the user's click, for a driver.</summary>
    internal bool AttachmentsTicked
    {
        get => m_Accessories.IsChecked == true;
        set => m_Accessories.IsChecked = value;
    }
    internal bool VehicleMode => m_VehicleMode;

    /// <summary>Shows the dialog over the editor and answers with the boxes as they stand, or null when cancelled.</summary>
    internal BakeRequest? ShowAndAnswer(Window? p_Owner)
    {
        if (p_Owner is { IsVisible: true })
            Owner = p_Owner;

        return ShowDialog() != true ? null : Result();
    }

    /// <summary>Whether the Bake button is enabled — the question a seam asks before pressing it.</summary>
    internal bool CanBake => m_Ok.IsEnabled;

    /// <summary>The line under the form that says what the bake will do (or what is missing).</summary>
    internal string SummaryText => m_Summary.Text;

    /// <summary>The camo name box, typed into the way the user types it (its TextChanged refreshes the form).</summary>
    internal string CamoName
    {
        get => m_Name.Text;
        set => m_Name.Text = value;
    }

    /// <summary>The description box (the text beside the row in the menu), typed into the way the user types it.</summary>
    internal string DescriptionText
    {
        get => m_Description.Text;
        set => m_Description.Text = value;
    }

    /// <summary>The weapons ticked and the vehicles ticked, as the boxes stand.</summary>
    internal IReadOnlyList<string> WeaponsTicked => m_Weapons.Where(p_C => p_C.IsChecked == true).Select(p_C => (string) p_C.Tag).ToList();
    internal IReadOnlyList<string> VehiclesTicked => m_Vehicles.Where(p_C => p_C.IsChecked == true).Select(p_C => (string) p_C.Tag).ToList();

    /// <summary>Leaves ticked exactly these weapons' boxes (catalogue folders) — the user's clicks, for a driver; the number of boxes ticked.</summary>
    internal int TickOnlyWeapons(IEnumerable<string> p_Folders)
    {
        var s_Wanted = new HashSet<string>(p_Folders, StringComparer.OrdinalIgnoreCase);
        foreach (var s_Box in m_Weapons)
            s_Box.IsChecked = s_Wanted.Contains((string) s_Box.Tag);

        return m_Weapons.Count(p_C => p_C.IsChecked == true);
    }

    /// <summary>Ticks (or unticks) one vehicle's box, by its key ("folder@class") — the user's click, for a driver; false when there is no such box.</summary>
    internal bool TickVehicle(string p_Key, bool p_On = true)
    {
        var s_Box = m_Vehicles.FirstOrDefault(p_C => string.Equals((string) p_C.Tag, p_Key, StringComparison.OrdinalIgnoreCase));
        if (s_Box == null)
            return false;

        s_Box.IsChecked = p_On;
        return true;
    }

    /// <summary>The dialog's form rendered without showing it — a picture of THIS dialog, boxes as they stand.</summary>
    internal System.Windows.Media.Imaging.BitmapSource Render()
    {
        var s_Content = (FrameworkElement) Content;
        s_Content.Measure(new Size(Width, Height));
        s_Content.Arrange(new Rect(0, 0, Width, Height));
        s_Content.UpdateLayout();

        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(Width), (int) Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);

        s_Target.Render(s_Content);
        return s_Target;
    }

    /// <summary>
    /// Whether the package is the look its weapons wear with NO camo picked (BakeRequest.DefaultLook) — set by a driver today (the
    /// --defaultemblemtest seam); the dialog shows no box for it yet (where the choice lives is still undecided).
    /// </summary>
    internal bool DefaultLookTicked
    {
        get => m_DefaultLook.IsChecked == true;
        set => m_DefaultLook.IsChecked = value;
    }

    /// <summary>The game camo the package is baked as (BakeRequest.NativeIdentity) — set by a driver (the --nativeemblem seam); no box.</summary>
    internal string? NativeIdentityKey { get; set; }

    /// <summary>
    /// The answer the boxes as they stand come to. Separate from <see cref="Ask"/> so a seam can read it
    /// without a modal window: what the reach choice DOES is the thing worth proving, and a photograph shows
    /// the radio, not the answer.
    /// </summary>
    internal BakeRequest Result()
    {
        var s_Chosen = m_Weapons.Where(p_C => p_C.IsChecked == true)
            .Select(p_C => (string) p_C.Tag).ToList();

        var s_Picture = m_Thumbnail.Text.Trim();

        // Piece mode: the pieces the camo paints, and the weapons are simply the ones they hang off. They are
        // listed OUTRIGHT and never left empty — an empty weapon list means "every weapon" to the plan.
        var s_Pieces = PickedPieces();

        var s_Vehicles = m_Vehicles.Where(p_C => p_C.IsChecked == true).Select(p_C => (string) p_C.Tag).ToList();

        // each part's own cell (only the parts the skin carries: a part left as the game ships it has no cell of the skin's)
        var s_In = PartsInSkin;
        var s_PartThumbnails = m_PartThumbnail.Where(p_T => s_In.Contains(p_T.Key) && p_T.Value.Text.Trim().Length > 0)
            .ToDictionary(p_T => p_T.Key, p_T => p_T.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);
        var s_PartFamilies = m_PartFamily.Where(p_F => s_In.Contains(p_F.Key) && p_F.Value.SelectedIndex > 0)
            .ToDictionary(p_F => p_F.Key, p_F => Families[p_F.Value.SelectedIndex], StringComparer.OrdinalIgnoreCase);
        var s_PartNames = m_PartName.Where(p_N => s_In.Contains(p_N.Key) && p_N.Value.Text.Trim().Length > 0)
            .ToDictionary(p_N => p_N.Key, p_N => p_N.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);
        var s_PartDescriptions = m_PartDescription.Where(p_D => s_In.Contains(p_D.Key) && p_D.Value.Text.Trim().Length > 0)
            .ToDictionary(p_D => p_D.Key, p_D => p_D.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);

        // ⭐ the soldier's form has no skin-wide text, category or picture: the whole soldier's cell (OUTFIT) wears the
        // LEAD PART's — the upper body's, or the first part shown when the upper body is not (2026-09-29) — its picture (else cut out of the
        // skin's pattern at the bake), its category (else guessed from the name), its text (else the first part's that has one)
        // (the lead AS THE FORM STANDS: a part shown only for its all the cloth may have been switched out of the skin since it was built)
        var s_Lead = s_In.Contains("upper") ? "upper" : s_In.FirstOrDefault() ?? m_LeadPart;
        string? SoldierDescription() => s_PartDescriptions.GetValueOrDefault(s_Lead) ??
                                        new[] { "upper", "head", "lower" }.Select(p_P => s_PartDescriptions.GetValueOrDefault(p_P)).FirstOrDefault(p_D => p_D != null);

        return new BakeRequest
        {
            Name = m_Name.Text.Trim(),
            // ⛔ The vehicle form has NO weapon boxes, so "every box ticked" (0 of 0) must not be read: an empty list means
            // every weapon to the plan. A vehicle camo paints none, said outright.
            Weapons = m_VehicleMode || m_Soldier != null
                ? new List<string> { "none" }
                : s_Pieces.Count > 0
                    ? s_Pieces.Select(p_P => p_P.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    : s_Chosen.Count == m_Weapons.Count
                        ? new List<string>()
                        : s_Chosen,
            Vehicles = m_VehicleMode ? s_Vehicles : new List<string>(),
            Description = m_Soldier != null ? SoldierDescription() ?? "" : m_Description.Text.Trim(),
            Thumbnail = m_Soldier != null ? s_PartThumbnails.GetValueOrDefault(s_Lead) : s_Picture.Length > 0 ? s_Picture : null,
            Family = m_Soldier != null ? s_PartFamilies.GetValueOrDefault(s_Lead) ?? ""
                : m_Family.SelectedIndex > 0 ? Families[m_Family.SelectedIndex] : "",
            Accessories = !m_VehicleMode && m_Soldier == null && m_Accessories.IsChecked == true,
            DefaultLook = !m_VehicleMode && m_Soldier == null && DefaultLookTicked,
            NativeIdentity = !m_VehicleMode && m_Soldier == null ? NativeIdentityKey : null,
            Pieces = s_Pieces,
            Soldier = m_Soldier?.Key,
            WhiteMask = m_AllCloth.Where(p_A => p_A.Value.IsChecked == true).Select(p_A => p_A.Key).ToList(),
            PartThumbnails = s_PartThumbnails,
            PartFamilies = s_PartFamilies,
            PartNames = s_PartNames,
            PartDescriptions = s_PartDescriptions,
            // (only the parts the skin carries, and only the ones with another soldier ticked)
            PartSoldiers = m_PartOthers.Keys.Where(p_P => s_In.Contains(p_P) && PartOthers(p_P).Count > 0)
                .ToDictionary(p_P => p_P, PartOthers, StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Builds the dialog a seam wants to question — never shown — with the reach choice made. The dialog is
    /// the real one, built by the same constructor the button uses, so what it answers is what the user gets.
    /// </summary>
    internal static BakeRequest AnswerFor(Window? p_Owner, WeaponCatalog p_Catalog, string p_Name, string p_Target,
        BakePiece? p_Piece, bool p_Everywhere, int p_FamilyIndex = -1)
    {
        var s_Dialog = new BakeDialog(p_Owner, p_Catalog, p_Name, "A camo of my own.", null, p_Target, "", false,
            null, p_Piece);

        if (p_Piece != null)
        {
            s_Dialog.m_PieceEverywhere!.IsChecked = p_Everywhere;
            s_Dialog.m_PieceHere!.IsChecked = !p_Everywhere;
        }

        if (p_FamilyIndex >= 0 && p_FamilyIndex < s_Dialog.m_Family.Items.Count)
            s_Dialog.m_Family.SelectedIndex = p_FamilyIndex;

        return s_Dialog.Result();
    }

    /// <summary>
    /// What the category box offers, READ FROM A BUILT DIALOG — the rows as the user sees them, not the array
    /// they were made from.
    /// ⛔ The first version of this returned the array, so a seam using it compared the source with itself and
    /// could never catch the one thing worth catching: a box whose rows do not line up with the names behind
    /// them (ADAPTIVE was picked and the package came out AUTUMN).
    /// </summary>
    internal static IReadOnlyList<string> FamilyLabels(Window? p_Owner, WeaponCatalog p_Catalog)
    {
        var s_Dialog = new BakeDialog(p_Owner, p_Catalog, "Tricolor", "", null, "…");
        var s_Labels = new List<string>();

        foreach (var s_Item in s_Dialog.m_Family.Items)
            s_Labels.Add(s_Item?.ToString() ?? "(null)");

        return s_Labels;
    }

    /// <summary>The "&lt;weapon&gt;/&lt;tag&gt;" names the reach choice comes to, or none in weapon mode.</summary>
    private List<string> PickedPieces() => m_Piece == null
        ? new List<string>()
        : m_PieceEverywhere?.IsChecked == true
            ? m_Piece.Carriers.ToList()
            : new List<string> { m_Piece.Host };

    /// <summary>
    /// Renders the dialog without showing it, so its layout can be looked at rather than assumed. The owner
    /// is the editor window the user opens this from: parts of the dialog take their look from it, so a
    /// picture shot without one is a picture of a dialog nobody gets.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(WeaponCatalog p_Catalog,
        string p_Name, string p_Target, string? p_Thumbnail = null, Window? p_Owner = null,
        bool p_Accessories = false, Func<IReadOnlyCollection<string>, int>? p_CountAccessories = null,
        BakePiece? p_Piece = null)
    {
        var s_Dialog = new BakeDialog(p_Owner, p_Catalog, p_Name, "A camo of my own.", p_Thumbnail, p_Target,
            "", p_Accessories, p_CountAccessories, p_Piece);
        var s_Content = (FrameworkElement) s_Dialog.Content;
        s_Content.Measure(new Size(s_Dialog.Width, s_Dialog.Height));
        s_Content.Arrange(new Rect(0, 0, s_Dialog.Width, s_Dialog.Height));
        s_Content.UpdateLayout();

        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Dialog.Width), (int) Math.Ceiling(s_Dialog.Height), 96, 96,
            PixelFormats.Pbgra32);

        s_Target.Render(s_Content);
        return s_Target;
    }

    /// <summary>One of the two reach choices: a radio with the answer on it and what it means underneath.</summary>
    private static RadioButton PieceChoice(string p_Label, string p_Explanation) => new()
    {
        GroupName = "reach",
        Margin = new Thickness(0, 4, 0, 0),
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = p_Label, Foreground = s_Text, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = p_Explanation, Foreground = s_Text, FontSize = 10, Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0),
                },
            },
        },
    };

    private static TextBox Field(string p_Text, bool p_Multiline) => new()
    {
        Text = p_Text, Background = s_Field, Foreground = s_Text, BorderBrush = s_Edge,
        BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3),
        TextWrapping = p_Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = false,
        MinHeight = p_Multiline ? 44 : 24, VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// The weapon question: which of them offer this camo, with the list of sixty under it. It fills the
    /// dialog's stretching row, which is why it takes the grid as well as the head.
    /// </summary>
    private void BuildWeaponBlock(StackPanel p_Head, Grid p_Root)
    {
        p_Head.Children.Add(Caption("Weapons", 12, 1.0, FontWeights.Bold, 10));
        p_Head.Children.Add(Caption(
            "Which weapons offer this camo. The weapon's own default skin is never touched — the mod only " +
            "ever adds new ones.", 10, 0.7));

        var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var s_All = new Button { Content = "all", Padding = new Thickness(7, 0, 7, 0), FontSize = 10 };
        var s_None = new Button
        {
            Content = "none", Padding = new Thickness(7, 0, 7, 0), FontSize = 10,
            Margin = new Thickness(4, 0, 0, 0),
        };
        s_All.Click += (_, _) => Set(true);
        s_None.Click += (_, _) => Set(false);
        s_Buttons.Children.Add(s_All);
        s_Buttons.Children.Add(s_None);
        p_Head.Children.Add(s_Buttons);

        var s_List = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        foreach (var s_Weapon in m_Catalog.Weapons)
        {
            var s_Box = new CheckBox
            {
                Content = new TextBlock { Text = s_Weapon.Folder, Foreground = s_Text },
                Tag = s_Weapon.Folder, Margin = new Thickness(4, 2, 4, 2), Width = 150, IsChecked = true,
                ToolTip = $"{s_Weapon.Blueprint}\n{string.Join("\n", s_Weapon.BodyMeshes)}",
            };

            s_Box.Checked += (_, _) => Refresh();
            s_Box.Unchecked += (_, _) => Refresh();
            m_Weapons.Add(s_Box);
            s_List.Children.Add(s_Box);
        }

        var s_Scroll = new ScrollViewer
        {
            Content = s_List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = s_Field, BorderBrush = s_Edge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 120,
        };
        Grid.SetRow(s_Scroll, 1);
        p_Root.Children.Add(s_Scroll);
    }

    /// <summary>
    /// The vehicle question: which vehicles this camo paints -- every catalogue vehicle whose body takes a
    /// camo, the one on screen ticked. A vehicle's camo is picked in its class window (LAND / AIR, CAMOUFLAGE), and in a
    /// match the vehicle wears its DRIVER's camo, seen by every player. The Vehicles tab's form: the list
    /// takes the weapon list's place (the stretching row), the way the weapon form lays its own out.
    /// </summary>
    private void BuildVehicleBlock(StackPanel p_Head, Grid p_Root)
    {
        if (m_VehicleCatalog == null || m_VehicleCatalog.Vehicles.Count == 0)
            return;

        p_Head.Children.Add(Caption("Vehicles", 12, 1.0, FontWeights.Bold, 10));
        p_Head.Children.Add(Caption(
            "The vehicles this camo paints. It is offered in each one's class window (land / air, camouflage), and in a " +
            "match a vehicle wears its DRIVER's camo, which every player sees. The vehicle on screen comes ticked.", 10, 0.7));

        var s_List = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        foreach (var s_Vehicle in m_VehicleCatalog.Vehicles.Where(p_V => p_V.TakesCamo))
        {
            // ⛔ keyed by folder AND class (VehicleCatalog.KeyOf): vehicles/lav25 holds two vehicles, the LAV-25 and the LAV-AD,
            // and a box keyed by folder alone ticked and baked both
            var s_Box = new CheckBox
            {
                Content = new TextBlock { Text = s_Vehicle.Display, Foreground = s_Text },
                Tag = VehicleCatalog.KeyOf(s_Vehicle), Margin = new Thickness(4, 2, 4, 2), Width = 150,
                IsChecked = m_VehiclesChecked.Contains(VehicleCatalog.KeyOf(s_Vehicle), StringComparer.OrdinalIgnoreCase),
                ToolTip = s_Vehicle.CategoryDisplay + "\n" + string.Join("\n", s_Vehicle.Meshes),
            };

            s_Box.Checked += (_, _) => Refresh();
            s_Box.Unchecked += (_, _) => Refresh();
            m_Vehicles.Add(s_Box);
            s_List.Children.Add(s_Box);
        }

        var s_Scroll = new ScrollViewer
        {
            Content = s_List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = s_Field, BorderBrush = s_Edge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 120,
        };
        Grid.SetRow(s_Scroll, 1);
        p_Root.Children.Add(s_Scroll);
    }

    /// <summary>
    /// The piece question, which is a different one: not WHICH weapons, but how far this piece's camo reaches.
    /// The piece itself is not asked — it is the one on screen — and neither is the attachments box: this camo
    /// IS an attachment. What is worth saying out loud is what the package will NOT do, because it is the
    /// opposite of every other bake: no weapon is painted and no weapon's menu gains a row.
    /// </summary>
    private void BuildPieceBlock(StackPanel p_Head)
    {
        var s_Piece = m_Piece!;

        p_Head.Children.Add(Caption($"This camo is of a piece: {s_Piece.Display}", 12, 1.0, FontWeights.Bold, 10));
        p_Head.Children.Add(Caption(
            $"It paints the {s_Piece.Display} ({s_Piece.Meshes} mesh(es)) and nothing else: no weapon body is " +
            "painted and no weapon's camo list gains a row. It is picked in the piece's own window, on the " +
            "part — which is where a camo for a sight or a suppressor belongs.", 10, 0.7));

        p_Head.Children.Add(Caption("Where it reaches", 12, 1.0, FontWeights.Bold, 10));
        p_Head.Children.Add(m_PieceHere!);
        p_Head.Children.Add(m_PieceEverywhere!);
    }

    /// <summary>
    /// The soldier's form: no subject is asked — the skin is the soldier on screen, and ALL of him (the studio finds every
    /// part changed on him and says it bakes the whole soldier) —, only where its camo lies on each part. Each part says what it carries, or
    /// that it stays as the game ships it, and what was changed on it that a skin cannot carry.
    /// </summary>
    private void BuildSoldierBlock(StackPanel p_Head)
    {
        var s_Soldier = m_Soldier!;

        p_Head.Children.Add(Caption($"This skin is of a soldier: {s_Soldier.Display}", 12, 1.0, FontWeights.Bold, 10));
        p_Head.Children.Add(Caption(
            "It bakes the WHOLE soldier: every part changed on him goes into this one skin, and the parts left alone stay as the game " +
            "ships them. It dresses no weapon and no vehicle; in the game it is picked in the soldier's own window, as a whole or part " +
            "by part.", 10, 0.7));

        // (only the parts changed are asked about; the others are named, so nothing seems forgotten)
        var s_Left = s_Soldier.Parts.Where(p_P => !m_ShownParts.Contains(p_P)).Select(p_P => p_P.Display).ToList();
        if (m_ShownParts.Count == 0)
            p_Head.Children.Add(Caption(
                "Nothing was changed on him yet: change a part (a picture on its camo node, a number), or tick 'Camo on all the cloth' for " +
                "one on the Soldiers panel, and press Bake again.", 11, 0.9, null, 8));
        else if (s_Left.Count > 0)
            p_Head.Children.Add(Caption(
                $"Not in this skin, as the game ships {(s_Left.Count == 1 ? "it" : "them")}: {string.Join(", ", s_Left)} — nothing was changed on " +
                $"{(s_Left.Count == 1 ? "it" : "them")}.", 10, 0.85, null, 6));

        foreach (var s_Part in m_ShownParts)
        {
            p_Head.Children.Add(Caption(s_Part.Display, 12, 1.0, FontWeights.Bold, 8));
            p_Head.Children.Add(Caption(s_Part.Changed
                    ? "Carries: " + s_Part.Carries + (s_Part.HasCloth ? "" : " — no camo cloth here: its own shader draws its whole material.")
                    : s_Part.Note.Length > 0
                        ? "Nothing of what was changed on it can go into a skin:"
                        : s_Part.HasCloth
                            ? "Nothing changed on it."
                            : "No camo cloth on this part as this soldier wears it — only a graph of its own (a node or a wire changed) can go on it.",
                10, 0.85));
            if (s_Part.Note.Length > 0)
                p_Head.Children.Add(new TextBlock
                {
                    Text = s_Part.Note, Foreground = s_Warn, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 3),
                });

            if (s_Part.MaskPicture)
                p_Head.Children.Add(Caption("Mask: the picture on its Mask node is where the camo goes.", 10, 0.7));
            else if (m_StockMask.TryGetValue(s_Part.Part, out var s_Stock))
            {
                p_Head.Children.Add(s_Stock);
                // ⚠ (the US support's default legs) the game's mask of this look lets no camo through
                if (s_Part.StockMaskHidden.Length > 0)
                    p_Head.Children.Add(new TextBlock
                    {
                        Text = $"⚠ The game's mask of this look puts the camo NOWHERE on {s_Part.StockMaskHidden} (its alpha is empty): with " +
                               "this choice the skin's pattern does not show there.",
                        Foreground = s_Warn, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 0, 0, 3),
                    });
                p_Head.Children.Add(m_AllCloth[s_Part.Part]);
            }

            if (m_PartThumbnail.TryGetValue(s_Part.Part, out var s_Box))
            {
                p_Head.Children.Add(PartCellBlock(s_Part, m_PartName[s_Part.Part], s_Box, m_PartFamily[s_Part.Part], m_PartDescription[s_Part.Part]));
                if (s_Soldier.Everyone.Count > 1)
                    p_Head.Children.Add(s_Part.NotForOthers == null
                        ? OthersBlock(s_Part)
                        : Caption($"On this soldier only: {s_Part.NotForOthers} — that does not travel to another model.", 10, 0.7, null, 4));
            }
        }
    }

    /// <summary>
    /// ⭐ THE OTHER SOLDIERS A PART GOES TO (the user chooses: this model only, US only, RU only, everyone…, per part):
    /// a box per soldier of the catalogue — him ticked and fixed — and the shortcuts. Each one wears the part's pattern and numbers, starting
    /// from his look of the same name (else his own), the tiling proportional to his own mesh's; the mask is checked on each (all the
    /// cloth chosen as a fix for this soldier's mask put the pattern on the others' boots) — CamoBaker.PlanOtherSoldiers.
    /// </summary>
    private FrameworkElement OthersBlock(BakeSoldierPart p_Part)
    {
        var s_Soldier = m_Soldier!;
        var s_Block = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        s_Block.Children.Add(Caption($"The soldiers its {TabOf(p_Part.Part)} goes to", 11, 1.0, FontWeights.SemiBold));
        s_Block.Children.Add(Caption(
            "The same pattern and numbers on each ticked soldier's part: each starts from his look of the same name as this part's " +
            "(else his own), and the tiling is proportional to his own mesh's (the game tunes it per mesh). Each one's mask is checked: " +
            "'All the cloth' chosen here because this soldier's own mask hides the camo is a fix for him alone — the others keep their own " +
            "mask and tiling; a soldier whose own mask hides it gets all the cloth. A part with no camo cloth on him stays as the game has " +
            "it; the Output says so. In the game each one lists the skin in his own window.", 10, 0.7));

        var s_Boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var s_Shortcuts = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
        foreach (var (s_Label, s_Which) in new[] { ("Only this one", "none"), ("US", "us"), ("RU", "ru"), ("Base game", "base"), ("Aftermath", "xp4"), ("All", "all") })
        {
            var s_Button = new Button { Content = s_Label, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0) };
            s_Button.Click += (_, _) => SetPartOthers(p_Part.Part, s_Which);
            s_Shortcuts.Children.Add(s_Button);
        }

        s_Block.Children.Add(s_Shortcuts);

        // four rows (US, RU; base, Aftermath) of four kits
        var s_Grid = new Grid();
        s_Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        for (var c = 0; c < 4; c++)
            s_Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var s_Kits = new[] { "Assault", "Engineer", "Support", "Recon" };
        var s_Row = 0;
        foreach (var (s_Team, s_Aftermath) in new[] { ("US", false), ("RU", false), ("US", true), ("RU", true) })
        {
            var s_Here = s_Soldier.Everyone.Where(p_E => p_E.Team == s_Team && p_E.Aftermath == s_Aftermath).ToList();
            if (s_Here.Count == 0)
                continue;

            s_Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var s_Name = Caption($"{s_Team}{(s_Aftermath ? " · Aftermath" : "")}", 10, 0.85);
            Grid.SetRow(s_Name, s_Row);
            s_Grid.Children.Add(s_Name);
            foreach (var s_Choice in s_Here)
            {
                var s_Column = Array.IndexOf(s_Kits, s_Choice.Kit);
                var s_Him = s_Choice.Key.Equals(s_Soldier.Key, StringComparison.OrdinalIgnoreCase);
                var s_CheckBox = new CheckBox
                {
                    Content = new TextBlock { Text = s_Choice.Kit, Foreground = s_Text, FontSize = 11 },
                    Tag = s_Choice.Key, IsChecked = s_Him, IsEnabled = !s_Him, Margin = new Thickness(0, 1, 6, 1),
                };
                s_CheckBox.Checked += (_, _) => Refresh();
                s_CheckBox.Unchecked += (_, _) => Refresh();
                Grid.SetRow(s_CheckBox, s_Row);
                Grid.SetColumn(s_CheckBox, s_Column < 0 ? 1 : s_Column + 1);
                s_Grid.Children.Add(s_CheckBox);
                if (!s_Him)
                    s_Boxes[s_Choice.Key] = s_CheckBox;
            }

            s_Row++;
        }

        s_Block.Children.Add(s_Grid);
        m_PartOthers[p_Part.Part] = s_Boxes;
        m_PartOthersBlock[p_Part.Part] = s_Block;
        return s_Block;
    }

    /// <summary>
    /// A part's other soldiers, set the way its shortcuts do (the button and a seam alike): "none" (this one only), "us", "ru", "base",
    /// "xp4", "all", or a comma list of catalogue keys. False when the part asks none.
    /// </summary>
    internal bool SetPartOthers(string p_Part, string p_Which)
    {
        if (!m_PartOthers.TryGetValue(p_Part, out var s_Boxes))
            return false;

        var s_Keys = p_Which.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (s_Key, s_Box) in s_Boxes)
        {
            var s_Choice = m_Soldier!.Everyone.First(p_E => p_E.Key.Equals(s_Key, StringComparison.OrdinalIgnoreCase));
            s_Box.IsChecked = p_Which.ToLowerInvariant() switch
            {
                "none" => false,
                "all" => true,
                "us" or "ru" => s_Choice.Team.Equals(p_Which, StringComparison.OrdinalIgnoreCase),
                "base" => !s_Choice.Aftermath,
                "xp4" => s_Choice.Aftermath,
                _ => s_Keys.Contains(s_Key, StringComparer.OrdinalIgnoreCase),
            };
        }

        Refresh();
        return true;
    }

    /// <summary>The other soldiers ticked on a part (an empty list for a part that asks none).</summary>
    internal List<string> PartOthers(string p_Part) => m_PartOthers.TryGetValue(p_Part, out var s_Boxes)
        ? s_Boxes.Where(p_B => p_B.Value.IsChecked == true).Select(p_B => p_B.Key).ToList()
        : new List<string>();

    /// <summary>
    /// A part's OWN cell in the soldier's window: the skin is listed on the part's tab too (HEAD, TORSO, LEGS), and a skin whose parts are
    /// different things shows each one there — its picture cut out of the part's own pattern unless one is chosen, filed where the skin is
    /// unless a category is chosen.
    /// </summary>
    private FrameworkElement PartCellBlock(BakeSoldierPart p_Part, TextBox p_Name, TextBox p_Box, ComboBox p_Combo, TextBox p_Description)
    {
        var s_Block = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        s_Block.Children.Add(Caption($"Its cell on the {TabOf(p_Part.Part)} tab", 11, 1.0, FontWeights.SemiBold));
        // (the whole soldier's cell, OUTFIT, is the lead part's — the skin's own picture, category and text are gone; the
        // upper body's, or the first part shown when it is not)
        var s_Outfit = p_Part.Part == m_LeadPart ? " The OUTFIT cell (the whole soldier) wears this part's picture, category and text." : "";
        s_Block.Children.Add(Caption(
            "The name: what the cell reads there — left empty, the skin's name. Only the cell's words: the skin stays one, known by its own name. " +
            (p_Part.Pattern.Length > 0
                ? $"The picture: leave it empty to cut it out of this part's own pattern ({p_Part.Pattern})."
                : "The picture: this part carries no pattern of its own — left empty, its cell shows the skin's picture.") +
            (p_Part.Part == m_LeadPart
                ? " The category: left as it comes, guessed from the skin's name."
                : $" The category: the {PartWord(m_LeadPart)}'s unless one is chosen here.") +
            " The text: what the INFO box shows when its cell is selected." + s_Outfit, 10, 0.7));
        p_Name.Margin = new Thickness(0, 0, 0, 3);
        s_Block.Children.Add(p_Name);

        var s_Row = new DockPanel { LastChildFill = true };
        var s_Browse = new Button { Content = "Browse…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        var s_Clear = new Button { Content = "none", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
        s_Browse.Click += (_, _) => PickPicture(p_Box);
        s_Clear.Click += (_, _) => p_Box.Text = "";
        DockPanel.SetDock(p_Combo, Dock.Right);
        DockPanel.SetDock(s_Clear, Dock.Right);
        DockPanel.SetDock(s_Browse, Dock.Right);
        s_Row.Children.Add(p_Combo);
        s_Row.Children.Add(s_Clear);
        s_Row.Children.Add(s_Browse);
        s_Row.Children.Add(p_Box);
        s_Block.Children.Add(s_Row);
        p_Description.Margin = new Thickness(0, 3, 0, 0);
        s_Block.Children.Add(p_Description);

        m_PartCell[p_Part.Part] = s_Block;
        return s_Block;
    }

    private FrameworkElement BuildLayout()
    {
        // Opaque, so a snapshot of the content alone is faithful instead of compositing onto nothing.
        var s_Root = new Grid { Margin = new Thickness(12), Background = s_Panel };
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // The middle row stretches for the WEAPON list (or the vehicle list on the Vehicles tab) and for nothing else: on
        // the piece form there is no list, and a star row with nothing in it is just a hole between two questions.
        s_Root.RowDefinitions.Add(new RowDefinition
        {
            Height = m_Piece == null && m_Soldier == null ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
        });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var s_Head = new StackPanel();
        s_Head.Children.Add(Caption(m_Soldier != null ? "Skin name" : "Camo name", 12, 1.0, FontWeights.Bold));
        s_Head.Children.Add(Caption(m_Soldier != null
            // (is the skin name still needed now that each part has a name? — yes: a part's name is only its
            // cell's words, this one is the skin itself)
            ? "What the skin IS to the game: a player's pick is kept under this name — rename it later and whoever wore it finds the " +
              "soldier as the game ships him until they pick it again —, and the package and its files are named after it. It is also " +
              "what its cell reads on the OUTFIT tab (the whole soldier), and on each part's tab unless that part has a name of its own below."
            // ⛔ The name no longer picks a BORROWED menu slot (that mechanism is gone): it derives the camo's
            // OWN identifier, and the loadout stores that — so the consequence of renaming is different.
            : "The title of the camo's row in the customization menu. It also names the package and its files, " +
              "and the name is what the camo IS to the game — rename it later and a player who had it equipped " +
              $"will find no camo on that {(m_VehicleMode ? "vehicle" : "weapon")} until they pick it once more.", 10, 0.7));
        s_Head.Children.Add(m_Name);

        // ⭐ THE DIALOG ASKS ONLY WHAT THE CAMO CANNOT ANSWER ITSELF. Authoring a PIECE, the weapon list is
        // not a question: the piece is the one on screen and the weapons are whichever carry it. Authoring a
        // weapon, it is the whole point. Authoring a VEHICLE (its tab), the vehicles are, and nothing of the weapons
        // is asked. So the three live in the same place and only one of them is built.
        if (m_Piece != null)
            BuildPieceBlock(s_Head);
        else if (m_Soldier != null)
            BuildSoldierBlock(s_Head);
        else if (m_VehicleMode)
            BuildVehicleBlock(s_Head, s_Root);
        else
            BuildWeaponBlock(s_Head, s_Root);

        Grid.SetRow(s_Head, 0);
        s_Root.Children.Add(s_Head);

        var s_Tail = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        // Beside the weapons, because it is the same question — what this camo is painted on — and not a
        // property of the camo itself. Off by default: it multiplies the package and the work the framework
        // does on every level, and the number of that is in the summary, where the choice is made.
        // A camo of a piece never asks it: it IS the attachment, and painting the rest of them is a
        // different camo. Nor does a vehicle's: it has no weapons.
        if (AsksAttachments)
        {
            s_Tail.Children.Add(Caption("Attachments", 12, 1.0, FontWeights.Bold));
            s_Tail.Children.Add(Caption(
                "Paint the optics, grips, lasers and silencers the chosen weapons offer, each with its own entry " +
                "in the menu, so a player puts this camo on a scope without putting it on the gun. Left off, the " +
                "package is the weapon bodies alone. It is not free: a painted copy of every part travels in the " +
                "package and the mod builds one entry per weapon and part while a level is loaded.", 10, 0.7));
            s_Tail.Children.Add(m_Accessories);

            s_Tail.Children.Add(Caption("Look with no camo", 12, 1.0, FontWeights.Bold, 10));
            s_Tail.Children.Add(Caption(
                "Ticked, this package is what the chosen weapons wear when the player picks the menu's no-camouflage entry: it takes the place of the " +
                "game's no-camo row on each of them (same cell, same name and picture in the menu) and is offered in no camo window. " +
                "Bake it \"as shipped\" for the weapons' own look with the emblem slot. Weapons the game gives no camo row (the pistols, " +
                "the crossbow) wear it from the server. One look per weapon: a weapon another package already dresses this way takes the " +
                "one registered last. The weapon bodies alone (no attachments).", 10, 0.7));
            s_Tail.Children.Add(m_DefaultLook);
        }

        // ⭐ THE SOLDIER'S FORM ASKS THESE PER PART (the skin-wide category, thumbnail and description are redundant now that each body
        // section has its own): the whole soldier's cell (OUTFIT) wears the upper body's — so the skin-wide boxes are the weapons'
        // and the vehicles' only
        if (AsksSkinCellBoxes)
            BuildCellBlock(s_Tail);

        s_Tail.Children.Add(Caption($"The package is dropped into {m_Target} and registered there. Restart the " +
                                    "server afterwards for it to be offered.", 10, 0.7, null, 8));
        s_Tail.Children.Add(m_Summary);

        Grid.SetRow(s_Tail, 2);
        s_Root.Children.Add(s_Tail);

        return FinishLayout(s_Root);
    }

    /// <summary>The row's text, category and picture — one set for the whole camo (the weapons' and vehicles' form).</summary>
    private void BuildCellBlock(StackPanel s_Tail)
    {
        s_Tail.Children.Add(Caption("Description", 12, 1.0, FontWeights.Bold, AsksAttachments ? 10 : 0));
        s_Tail.Children.Add(Caption("The text the menu shows in the INFO box when the row is selected.", 10, 0.7));
        s_Tail.Children.Add(m_Description);

        s_Tail.Children.Add(Caption("Category", 12, 1.0, FontWeights.Bold, 10));
        s_Tail.Children.Add(Caption(m_Soldier != null
            ? "Which of the menu's eight buttons files the skin's cell on the OUTFIT tab (the whole soldier), and its cell on each part's " +
              "tab unless that part has a category of its own above. Left as it comes, the family is guessed from the skin's NAME (a name " +
              "with 'desert' in it lands under DESERT, one with nothing recognisable under MISC) -- which is a guess, and this is the answer."
            : "Which of the menu's eight buttons files this camo. Left as it comes, the family is guessed from " +
              "the camo's NAME (a name with 'desert' in it lands under DESERT, one with nothing recognisable " +
              "under MISC) -- which is a guess, and this is the answer.", 10, 0.7));
        s_Tail.Children.Add(m_Family);

        s_Tail.Children.Add(Caption("Thumbnail", 12, 1.0, FontWeights.Bold, 10));
        s_Tail.Children.Add(Caption(m_Soldier != null
            ? $"The picture on the skin's cell on the OUTFIT tab (the whole soldier): {ThumbnailWidth}×{ThumbnailHeight} pixels (a 4:1 band), " +
              "PNG or JPG. Another size is scaled to cover the band and cut down the middle. Leave it empty to cut the band out of the " +
              "skin's first pattern. Each part's tab shows the part's own picture (above)."
            : $"The picture on the row: {ThumbnailWidth}×{ThumbnailHeight} pixels (a 4:1 band), PNG or JPG. Another " +
              "size is scaled to cover the band and cut down the middle. Leave it empty to cut the band out of " +
              "the camo's own pattern.", 10, 0.7));

        var s_Picture = new DockPanel { LastChildFill = true };
        var s_Browse = new Button { Content = "Browse…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        var s_Clear = new Button { Content = "none", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0) };
        s_Browse.Click += (_, _) => PickThumbnail();
        s_Clear.Click += (_, _) => m_Thumbnail.Text = "";
        DockPanel.SetDock(s_Clear, Dock.Right);
        DockPanel.SetDock(s_Browse, Dock.Right);
        s_Picture.Children.Add(s_Clear);
        s_Picture.Children.Add(s_Browse);
        s_Picture.Children.Add(m_Thumbnail);
        s_Tail.Children.Add(s_Picture);
    }

    /// <summary>The form in its shell: the buttons docked to the bottom, the form scrolling above them where it can outgrow the screen.</summary>
    private FrameworkElement FinishLayout(Grid s_Root)
    {
        // ⛔ THE BUTTONS LIVE OUTSIDE EVERYTHING THAT CAN SCROLL OR OVERFLOW (the Bake button was below the
        // bottom edge until the user stretched the window by hand). They are docked to the bottom of the
        // window, so no amount of text above them can push them out of reach, on any screen.
        var s_Cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var s_Row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 8, 12, 12), Background = s_Panel,
        };
        s_Row.Children.Add(m_Ok);
        s_Row.Children.Add(s_Cancel);

        var s_Shell = new DockPanel { LastChildFill = true, Background = s_Panel };
        DockPanel.SetDock(s_Row, Dock.Bottom);
        s_Shell.Children.Add(s_Row);

        // And when the form is taller than the screen it scrolls INSIDE, never under the buttons. The weapon form
        // carries its own scrolling list, so wrapping it again would nest two scrollbars for nothing: it is the
        // piece form (all text, no list) that can outgrow a short screen.
        if (m_Piece != null || m_Soldier != null)
        {
            s_Shell.Children.Add(new ScrollViewer
            {
                Content = s_Root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Background = s_Panel,
            });
        }
        else
        {
            s_Shell.Children.Add(s_Root);
        }

        return s_Shell;
    }

    private void PickThumbnail() => PickPicture(m_Thumbnail);

    /// <summary>The row's picture, or a part's own cell's, into its box.</summary>
    private void PickPicture(TextBox p_Box)
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the row's picture ({ThumbnailWidth}×{ThumbnailHeight})",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.dds|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (s_Dialog.ShowDialog(this) == true)
            p_Box.Text = s_Dialog.FileName;

        Refresh();
    }

    private void Set(bool p_On)
    {
        foreach (var s_Box in m_Weapons)
            s_Box.IsChecked = p_On;
    }

    /// <summary>
    /// Says what the choice COSTS, in the unit that actually decides it: database entries. A camo needs one
    /// per weapon mesh, and both meshes of a weapon, so the number is not the weapon count. And it keeps the
    /// Bake button off until there is a name and at least one weapon.
    /// </summary>
    private void Refresh()
    {
        if (m_Piece != null)
        {
            RefreshPiece();
            return;
        }

        if (m_Soldier != null)
        {
            RefreshSoldier();
            return;
        }

        var s_Chosen = m_Weapons.Where(p_C => p_C.IsChecked == true)
            .Select(p_C => (string) p_C.Tag).ToList();

        var s_Meshes = m_Catalog.Weapons
            .Where(p_W => s_Chosen.Contains(p_W.Folder, StringComparer.OrdinalIgnoreCase))
            .Sum(p_W => p_W.BodyMeshes.Count);

        var s_Picture = m_Thumbnail.Text.Trim();
        var s_PictureNote = s_Picture.Length > 0 && !File.Exists(s_Picture) ? " The thumbnail file does not exist." : "";

        // What the attachments add, counted the same way the bake expands them — and said in the unit that
        // decides it, which is not "19 attachments" but the entries the mod builds on every level.
        var s_Parts = m_Accessories.IsChecked == true && s_Chosen.Count > 0
            ? m_CountAccessories?.Invoke(s_Chosen) ?? 0
            : 0;

        var s_PartsNote = m_Accessories.IsChecked != true
            ? ""
            : s_Parts > 0
                ? $" Plus {s_Parts} attachment(s): a painted copy of each in the package and {s_Parts} entr(ies) " +
                  "the mod builds on every level; the parts with nothing to paint are dropped at bake." +
                  (s_Parts > PartsTriedInGame
                      ? $" ⚠ The largest package run in the game so far carried {PartsTriedInGame}: this one is " +
                        $"{s_Parts / PartsTriedInGame}× that and has never been tried."
                      : "")
                : " No attachment of the chosen weapons can be painted — the package will be the bodies alone.";

        var s_Vehicles = m_VehicleCatalog == null
            ? new List<VehicleEntry>()
            : m_VehicleCatalog.Vehicles.Where(p_V => VehiclesTicked.Contains(VehicleCatalog.KeyOf(p_V), StringComparer.OrdinalIgnoreCase)).ToList();

        // each tab's form asks its own subjects and counts only them (the other kind is not in the dialog at all)
        var s_Picked = m_VehicleMode ? s_Vehicles.Count : s_Chosen.Count;

        if (m_Name.Text.Trim().Length == 0)
            m_Summary.Text = "Name the camo." + s_PictureNote;
        else if (s_Picked == 0)
            m_Summary.Text = (m_VehicleMode ? "Pick at least one vehicle." : "Pick at least one weapon.") + s_PictureNote;
        else if (m_VehicleMode)
            m_Summary.Text = VehicleNote(s_Vehicles).TrimStart() + s_PictureNote;
        else
            m_Summary.Text = $"{s_Chosen.Count} weapon(s) = {s_Meshes} database entries in the package." + s_PartsNote +
                             s_PictureNote;

        m_Ok.IsEnabled = m_Name.Text.Trim().Length > 0 && s_Picked > 0 && s_PictureNote.Length == 0;
    }

    /// <summary>
    /// What each ticked vehicle gets, in the terms the player sees. Since everything moved to option A, a vehicle camo is
    /// worn as the WHOLE VEHICLE — made again wearing it when its driver gets in (VehicleSwap.lua), with or without pieces in the
    /// catalogue; pieces are only the bake's fallback for a body it cannot clone, which its log names. ⛔ The old note ("No camo
    /// pieces … never worn in a match") said the F-35B and the Venom would never be worn — false since then, so the bake warning
    /// was fixed.
    /// </summary>
    private static string VehicleNote(IReadOnlyList<VehicleEntry> p_Vehicles)
    {
        if (p_Vehicles.Count == 0)
            return "";

        return $" {p_Vehicles.Count} vehicle(s): {string.Join(", ", p_Vehicles.Select(p_V => p_V.Display))} — each worn as the whole vehicle, " +
               "made again wearing the camo when its driver gets in (the bake's log names any it could only dress with pieces).";
    }

    /// <summary>
    /// The same job for a piece, in the unit that decides it there: the painted meshes travel ONCE (the piece
    /// is one object, and two weapons that carry it share its mesh), and what the reach multiplies is the
    /// entries the mod builds while a level is loaded. There are no database entries to count: a camo of a
    /// piece builds no weapon variation at all.
    /// </summary>
    private void RefreshPiece()
    {
        var s_Piece = m_Piece!;
        var s_Reach = PickedPieces().Count;
        var s_Picture = m_Thumbnail.Text.Trim();
        var s_PictureNote = s_Picture.Length > 0 && !File.Exists(s_Picture) ? " The thumbnail file does not exist." : "";

        m_Summary.Text = m_Name.Text.Trim().Length == 0
            ? "Name the camo." + s_PictureNote
            : $"{s_Piece.Meshes} painted mesh(es) of the {s_Piece.Display} in the package, and {s_Reach} entr(ies) " +
              "the mod builds on every level. No weapon body is painted and no weapon's menu gains a row — this " +
              "camo is picked on the piece, in its own window." + s_PictureNote;

        m_Ok.IsEnabled = m_Name.Text.Trim().Length > 0 && s_PictureNote.Length == 0;
    }

    /// <summary>
    /// The soldier's summary: the WHOLE soldier is baked (and the summary warns of it) — which parts go into the skin and
    /// with what, and which stay as the game ships them. Bake stays off until there is a name and at least one part to carry.
    /// </summary>
    private void RefreshSoldier()
    {
        var s_Soldier = m_Soldier!;
        var s_In = PartsInSkin;
        // ⛔ NOT the skin-wide thumbnail box (review 2026-09-29): the soldier's form does not show it, yet it came seeded with the last bake's
        // picture — moved or deleted since, it said "The thumbnail file does not exist." and kept Bake off with nothing on screen to fix it.
        // The pictures that count are the parts' own cells, checked below.
        var s_PictureNote = "";

        string Of(string p_Part) => s_Soldier.Parts.First(p_P => p_P.Part == p_Part).Display;

        // a part's own cell only means something while the part is in the skin (a part left as the game ships it has no cell of the skin's)
        foreach (var (s_Part, s_Cell) in m_PartCell)
            s_Cell.IsEnabled = s_In.Contains(s_Part);
        foreach (var (s_Part, s_Block) in m_PartOthersBlock)
            s_Block.IsEnabled = s_In.Contains(s_Part);
        // ⭐ the other soldiers the parts go to, said with the rest
        var s_Others = s_In.Where(p_P => PartOthers(p_P).Count > 0).Select(p_P => $"{Of(p_P)} on {PartOthers(p_P).Count} other soldier(s)").ToList();
        foreach (var (s_Part, s_Box) in m_PartThumbnail)
            if (s_In.Contains(s_Part) && s_Box.Text.Trim().Length > 0 && !File.Exists(s_Box.Text.Trim()))
                s_PictureNote += $" The picture of the {TabOf(s_Part)} cell does not exist.";
        var s_Left = s_Soldier.Parts.Where(p_P => !s_In.Contains(p_P.Part)).Select(p_P => p_P.Display).ToList();

        // ⛔ the name another soldier's skin already has: baking it here would replace that one (one folder, one identifier per name)
        var s_Taken = m_Name.Text.Trim().Length > 0 ? s_Soldier.BakedForAnother?.Invoke(m_Name.Text.Trim()) : null;
        if (s_Taken != null)
            s_PictureNote += $" ⚠ A skin named '{m_Name.Text.Trim()}' is already baked for {s_Taken}: baking this one would replace it there. " +
                             "Give this skin another name, or delete that one first (Delete camo…).";

        m_Summary.Text = m_Name.Text.Trim().Length == 0
            ? "Name the skin." + s_PictureNote
            : s_In.Count == 0
                ? $"Nothing of {s_Soldier.Display} to bake yet: change a part (a picture on its camo node, a number), or tick 'Camo on all " +
                  "the cloth' for one on the Soldiers panel." + s_PictureNote
                : $"This bakes the WHOLE soldier, {s_Soldier.Display}: {string.Join(", ", s_In.Select(p_P => Of(p_P) + (AllCloth(p_P) ? " (all the cloth)" : "")))} " +
                  $"in one skin{(s_Left.Count > 0 ? $"; {string.Join(", ", s_Left)} as the game ships {(s_Left.Count == 1 ? "it" : "them")}" : "")}." +
                  (s_Others.Count > 0 ? $" Also: {string.Join(", ", s_Others)}." : "") +
                  s_PictureNote;

        m_Ok.IsEnabled = m_Name.Text.Trim().Length > 0 && s_In.Count > 0 && s_PictureNote.Length == 0;
    }

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0,
        FontWeight? p_Weight = null, double p_Top = 0) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity,
        FontWeight = p_Weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, p_Top, 0, 3),
    };
}
