using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeCamoStudio.Project;

namespace RimeCamoStudio.View;

/// <summary>
/// Authoring a camo without the node graph: bring an image, set how it tiles and how worn it looks.
///
/// It is the SAME camo either way — this panel writes the values the graph would have produced through its
/// nodes. It exists because someone who wants their picture on a rifle should not have to learn a material
/// graph first, and it is opt-in from Settings so nobody is pushed into it.
///
/// Every change goes out through <see cref="Changed"/> the moment it is made, so the preview follows the
/// sliders live (simple mode must show its settings live in the preview too).
/// </summary>
public sealed class SimpleCamoPanel : Grid
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Dim = new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0x96));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    private readonly Image m_Thumbnail = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock m_ImageName = Caption("No image chosen", 11, 0.8);
    private readonly TextBlock m_Note = Caption("", 11, 0.9);
    // Ranges from the material scan: tilings 0.3–4 on the game's own camos (8 leaves room), wear 10–30
    // commonly and up to 150 — a slider capped at 20 could not even reach what most of the game's camos use.
    private readonly Slider m_Tiling = Knob(0.25, 8.0, 3.0, 0.25);
    private readonly Slider m_Wear = Knob(0.0, 100.0, 10.0, 1.0);
    private readonly TextBlock m_TilingValue = Caption("3.0", 11, 0.8);
    private readonly TextBlock m_WearValue = Caption("10.0", 11, 0.8);

    // what changes between a weapon or vehicle and a soldier's part (ShowFor)
    private readonly TextBlock m_Intro = Caption(WeaponIntro, 11, 0.7);
    private readonly TextBlock m_PatternHelp = Help(PatternHelp);
    private readonly TextBlock m_TilingHelp = Help(TilingHelp);
    private readonly Button m_Choose = new()
    {
        Content = "Choose image…", Padding = new Thickness(10, 2, 10, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private readonly StackPanel m_WearLabel;
    private readonly UIElement m_WearRow;

    /// <summary>The soldier's part the form is on, or null on a weapon or a vehicle.</summary>
    private SoldierPart? m_Soldier;

    private const string WeaponIntro =
        "Simple mode: no nodes. Bring the pattern and set how it sits on the weapon. Switch it off in " +
        "Settings to author with the graph instead — the camo it makes is the same.";

    /// <summary>
    /// A soldier's part the form is on (on soldiers only the tiling is kept — the wear is left out, it belonged to weapons and
    /// vehicles): its name on the Soldiers panel, the tiling the game gives it (one number, as its material has it — null when it has
    /// none), and why nothing can be set on it, or null when it can.
    /// </summary>
    public sealed record SoldierPart(string Part, string? GameTiling, string? Unavailable, bool TilingAvailable = true);

    /// <summary>
    /// The soldiers' tiling, as the game's own shaders read it — measured 2026-09-29 over every pixel shader of the soldier shaders
    /// that carry camo (characterroot, its Aftermath and LOD flavours): CamoTileFactor is ONE float, and the pattern's uv is multiplied
    /// by it in both directions; the soldier material scan has it from 0.8 to 18 (most often 7, 5, 9, 4 and 3), never a second number.
    /// </summary>
    public const string SoldierTilingHelp =
        "How many times the pattern repeats over this part — ONE number for both directions: the game's soldiers have no " +
        "separate X and Y. The game's own soldiers use 0.8–18 (most often 7, 5 and 9); each part's mesh has its own.";

    public const string SoldierPatternHelp =
        "The image tiled over this part's camo cloth, where the part's mask lets it show — shipped as a 512×512 pattern. " +
        "Each part (head, upper body, lower body) keeps its own.";

    private const string SoldierIntro =
        "Simple mode: no nodes. Bring the pattern and set how it tiles on the part on screen — the head, the upper body " +
        "and the lower body each keep their own. Switch it off in Settings to author with the graph instead — the skin it " +
        "makes is the same.";

    // The soldiers' range (the scan: 0.8–18) with room, in tenths: the game's own numbers (6.9, 7.8…) are shown as they are.
    private const double c_SoldierMin = 0.1, c_SoldierMax = 20.0, c_SoldierStep = 0.1;
    private const double c_WeaponMin = 0.25, c_WeaponMax = 8.0, c_WeaponStep = 0.25;

    /// <summary>
    /// What each setting IS, in one sentence — shown under the slider here, and written on the matching node
    /// of the graph in the other mode, so both views name the same three things in the same words. The two
    /// constants read the measured table the node notes read.
    /// </summary>
    public const string PatternHelp = "The image tiled over the weapon — 512×512. The 256×64 thumbnail the menu row shows is generated from it.";
    public static readonly string TilingHelp = HelpFor("CamoTiling");
    public static readonly string WearHelp = HelpFor("WearAmount");

    private static string HelpFor(string p_Constant)
    {
        var s_Constant = WeaponConstants.All[p_Constant];
        return $"{s_Constant.What} ({char.ToUpperInvariant(s_Constant.Range[0])}{s_Constant.Range[1..]}.)";
    }

    /// <summary>The chosen image's FULL path — the caption shows the file name, the camo needs the file.</summary>
    private string? m_ImagePath;

    private Camo? m_Camo;

    /// <summary>Raised after every change the user makes, with the camo as it now stands.</summary>
    public event EventHandler<Camo>? Changed;

    public SimpleCamoPanel()
    {
        Background = s_Panel;

        var s_Root = new StackPanel { Margin = new Thickness(18, 14, 18, 14), MaxWidth = 560 };
        s_Root.Children.Add(Caption("Camo", 13, 1.0, FontWeights.Bold));
        s_Root.Children.Add(m_Intro);

        // ── The pattern ──────────────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(Label("Pattern", m_PatternHelp));

        var s_Row = new StackPanel { Orientation = Orientation.Horizontal };
        s_Row.Children.Add(new Border
        {
            Width = 96, Height = 96, Background = s_Field, BorderBrush = s_Edge,
            BorderThickness = new Thickness(1), Child = m_Thumbnail,
        });

        var s_Side = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        m_Choose.Click += (_, _) => ChooseImage();
        s_Side.Children.Add(m_Choose);
        s_Side.Children.Add(m_ImageName);
        s_Row.Children.Add(s_Side);
        s_Root.Children.Add(s_Row);

        // ── How it sits ──────────────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(Label("Tiling", m_TilingHelp));
        s_Root.Children.Add(WithValue(m_Tiling, m_TilingValue));

        // (a soldier has no wear: it goes with its label — ShowFor)
        m_WearLabel = Label("Wear", Help(WearHelp));
        m_WearRow = WithValue(m_Wear, m_WearValue);
        s_Root.Children.Add(m_WearLabel);
        s_Root.Children.Add(m_WearRow);

        // (each control says it moved: only what the user moves is written — Camo.MovedControls)
        m_Tiling.ValueChanged += (_, _) => Commit("tiling");
        m_Wear.ValueChanged += (_, _) => Commit("wear");

        m_Note.Margin = new Thickness(0, 18, 0, 0);
        s_Root.Children.Add(m_Note);

        Children.Add(new ScrollViewer
        {
            Content = s_Root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    /// <summary>Points the panel at the camo being edited and shows its values. Fills, never writes back.</summary>
    public void Show(Camo p_Camo) => ShowFor(p_Camo, null);

    /// <summary>
    /// The same on the subject on screen: a weapon or a vehicle (null — pattern, tiling, wear), or a soldier's part (the pattern and ONE
    /// tiling number, no wear: the game's soldiers have neither a second tiling number nor a wear). Fills, never writes back.
    /// </summary>
    public void ShowFor(Camo p_Camo, SoldierPart? p_Soldier)
    {
        m_Camo = null; // so filling the controls does not write back
        m_Soldier = p_Soldier;

        // ⛔ the range BEFORE the value: a slider coerces its value into its range, so 9 set on the weapons' 0.25–8 would read 8
        var s_Soldier = p_Soldier != null;
        m_Tiling.Minimum = s_Soldier ? c_SoldierMin : c_WeaponMin;
        m_Tiling.Maximum = s_Soldier ? c_SoldierMax : c_WeaponMax;
        m_Tiling.TickFrequency = s_Soldier ? c_SoldierStep : c_WeaponStep;
        m_Intro.Text = s_Soldier ? SoldierIntro : WeaponIntro;
        m_PatternHelp.Text = s_Soldier ? SoldierPatternHelp : PatternHelp;
        m_TilingHelp.Text = s_Soldier ? SoldierTilingHelp : TilingHelp;
        m_WearLabel.Visibility = m_WearRow.Visibility = s_Soldier ? Visibility.Collapsed : Visibility.Visible;

        // a part that takes no pattern (its shader reads none) or has no tiling on its graph: said in the note, and the control goes grey
        m_Choose.IsEnabled = p_Soldier?.Unavailable == null;
        m_Tiling.IsEnabled = p_Soldier == null || (p_Soldier.Unavailable == null && p_Soldier.TilingAvailable);

        m_Tiling.Value = p_Camo.Tiling;
        m_Wear.Value = p_Camo.Wear;
        // the value as it IS, not as the slider can hold it: a game value outside its range (a tiling of -1.88, a wear of 300) is shown
        // true, and written only if the user moves that slider (review 2026-09-29)
        m_TilingValue.Text = p_Camo.Tiling.ToString("0.0");
        m_WearValue.Text = p_Camo.Wear.ToString("0.0");
        SetImage(p_Camo.ImagePath);
        // the form shown again: nothing moved yet
        p_Camo.MovedControls.Clear();
        m_Camo = p_Camo;
        Refresh();
    }

    /// <summary>The camo the panel is editing, if any.</summary>
    public Camo? Current => m_Camo;

    /// <summary>What a driver reads of the form as the user sees it: the wear shown, the tiling's range and state, the note.</summary>
    public bool WearShown => m_WearRow.Visibility == Visibility.Visible && m_WearLabel.Visibility == Visibility.Visible;

    public (double Minimum, double Maximum, double Step) TilingRange => (m_Tiling.Minimum, m_Tiling.Maximum, m_Tiling.TickFrequency);

    public bool PatternEnabled => m_Choose.IsEnabled;

    public bool TilingEnabled => m_Tiling.IsEnabled;

    public string Note => m_Note.Text;

    public string ImageShown => m_ImageName.Text;

    /// <summary>The tiling slider — a driver moves it the way a hand does.</summary>
    public double Tiling
    {
        get => m_Tiling.Value;
        set => m_Tiling.Value = value;
    }

    /// <summary>The wear slider.</summary>
    public double Wear
    {
        get => m_Wear.Value;
        set => m_Wear.Value = value;
    }

    private void ChooseImage()
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the camo pattern",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.dds|All files (*.*)|*.*",
        };

        if (s_Dialog.ShowDialog() != true)
            return;

        ChoosePattern(s_Dialog.FileName);
    }

    /// <summary>What choosing a file in the dialog does — the one entry, so a driver takes the same path.</summary>
    public void ChoosePattern(string p_Path)
    {
        SetImage(p_Path);
        Commit(null);
    }

    private void SetImage(string? p_Path)
    {
        if (string.IsNullOrWhiteSpace(p_Path) || !File.Exists(p_Path))
        {
            m_ImagePath = null;
            m_Thumbnail.Source = null;
            m_ImageName.Text = "No image chosen";
            return;
        }

        m_ImagePath = p_Path;
        m_ImageName.Text = Path.GetFileName(p_Path);

        try
        {
            // Loaded fully into memory: a BitmapImage bound to a path keeps the file locked, and the user
            // is likely to be editing that very file in another program while they iterate.
            var s_Bitmap = new BitmapImage();
            s_Bitmap.BeginInit();
            s_Bitmap.CacheOption = BitmapCacheOption.OnLoad;
            s_Bitmap.UriSource = new Uri(p_Path);
            s_Bitmap.EndInit();
            m_Thumbnail.Source = s_Bitmap;
        }
        catch (Exception)
        {
            // A DDS (or anything WPF cannot decode) still counts as chosen; only the preview is missing.
            m_Thumbnail.Source = null;
        }
    }

    /// <param name="p_Moved">The control the user moved ("tiling", "wear"), or null for the pattern.</param>
    private void Commit(string? p_Moved)
    {
        if (m_Camo == null)
        {
            // (filling the form: the labels follow ShowFor's values)
            return;
        }

        if (p_Moved == "tiling")
            m_TilingValue.Text = m_Tiling.Value.ToString("0.0");
        if (p_Moved == "wear")
            m_WearValue.Text = m_Wear.Value.ToString("0.0");

        // only what moved takes the slider's value: the other keeps what it holds (the document's, shown true)
        if (p_Moved == "tiling")
            m_Camo.Tiling = m_Tiling.Value;
        if (p_Moved == "wear")
            m_Camo.Wear = m_Wear.Value;
        if (p_Moved != null)
            m_Camo.MovedControls.Add(p_Moved);
        m_Camo.SimpleEdited = true;

        // ⛔ The full path, not the caption: the caption is the file NAME, and a camo that remembers only
        // the name cannot find its own pattern the next time it is shown.
        if (m_ImagePath != null)
            m_Camo.ImagePath = m_ImagePath;

        Refresh();
        Changed?.Invoke(this, m_Camo);
    }

    /// <summary>
    /// Says plainly what is and is not wired up. ⛔ The panel would otherwise LOOK finished: sliders move,
    /// an image shows, and nothing reaches the game — the kind of half-built control that costs a user an
    /// afternoon before they find out.
    /// </summary>
    private void Refresh() =>
        m_Note.Text = m_Camo == null
            ? ""
            : m_Soldier != null
                ? SoldierNote(m_Camo, m_Soldier)
                : $"'{m_Camo.Name}': tiling {m_Camo.Tiling:0.0}, wear {m_Camo.Wear:0.0}" +
                  (string.IsNullOrWhiteSpace(m_Camo.ImagePath) ? ", no pattern yet" : $", pattern {m_Camo.ImagePath}") +
                  ".\nShown live in the preview.\n⚠ These values are stored with the camo, but the generator that " +
                  "turns them into a mod is not built yet — baking still writes nothing.";

    /// <summary>
    /// What the form says on a soldier's part — true there: the Soldiers tab's Bake… makes the skin, and it takes each part's own
    /// pattern and tiling from the part's graph, which is where this form writes them (the node the graph mode shows).
    /// </summary>
    private static string SoldierNote(Camo p_Camo, SoldierPart p_Part)
    {
        if (p_Part.Unavailable != null)
            return $"'{p_Camo.Name}' · {p_Part.Part}: {p_Part.Unavailable}";

        var s_Game = p_Part.GameTiling != null ? $" (the game's own here: {p_Part.GameTiling})" : "";
        return $"'{p_Camo.Name}' · {p_Part.Part}: " +
               (string.IsNullOrWhiteSpace(p_Camo.ImagePath) ? "no pattern yet" : $"pattern {p_Camo.ImagePath}") +
               (p_Part.TilingAvailable
                   ? $", tiling {p_Camo.Tiling:0.0}{s_Game}."
                   : ". Its graph has no tiling to set (no CamoTileFactor node): the part keeps the game's.") +
               "\nShown live in the preview. Bake… makes the skin with each part's own pattern and tiling — " +
               "the other parts keep theirs.";
    }

    private static UIElement WithValue(Slider p_Slider, TextBlock p_Value)
    {
        var s_Row = new DockPanel { Margin = new Thickness(0, 2, 0, 0), MaxWidth = 420 };
        p_Value.Width = 44;
        p_Value.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(p_Value, Dock.Right);
        s_Row.Children.Add(p_Value);
        s_Row.Children.Add(p_Slider);
        return s_Row;
    }

    private static Slider Knob(double p_Min, double p_Max, double p_Value, double p_Tick) => new()
    {
        Minimum = p_Min, Maximum = p_Max, Value = p_Value, IsSnapToTickEnabled = true, TickFrequency = p_Tick,
    };

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0,
        FontWeight? p_Weight = null) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity,
        FontWeight = p_Weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
    };

    private static StackPanel Label(string p_Title, TextBlock p_Help)
    {
        var s_Stack = new StackPanel { Margin = new Thickness(0, 16, 0, 3) };
        s_Stack.Children.Add(Caption(p_Title, 11, 1.0, FontWeights.Bold));
        s_Stack.Children.Add(p_Help);
        return s_Stack;
    }

    /// <summary>The dim line under a setting's title — kept, so the soldiers' words can take its place (ShowFor).</summary>
    private static TextBlock Help(string p_Text) => new()
    {
        Text = p_Text, Foreground = s_Dim, FontSize = 10, TextWrapping = TextWrapping.Wrap,
    };
}
