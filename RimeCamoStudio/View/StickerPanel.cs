using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RimeShaderEditor.Graph;

namespace RimeCamoStudio.View;

/// <summary>
/// The side panel of sticker mode: the user's sticker library (pictures they bring in — users must be able to
/// import their own stickers), the placements on the weapon
/// being looked at, and the numbers of the selected one. The 3D view does the placing and the handles;
/// this panel only names pictures and shows what is placed. Every change goes through the editor, which
/// owns the placements (they live on the graph) and redraws.
/// </summary>
public sealed class StickerPanel : Grid
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush s_Armed = new SolidColorBrush(Color.FromRgb(0x3D, 0x5A, 0x80));

    private readonly RimeShaderEditor.MainWindow m_Editor;
    private readonly string m_Library;
    private readonly WrapPanel m_Palette = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly ListBox m_List = new() { Background = s_Field, Foreground = s_Text, BorderBrush = s_Edge, MinHeight = 90, MaxHeight = 220 };
    private readonly Slider m_Size = new() { Minimum = 0.01, Maximum = 0.8, Value = 0.12, TickFrequency = 0.01, IsSnapToTickEnabled = false };
    private readonly Slider m_Height = new() { Minimum = 0.01, Maximum = 0.8, Value = 0.12, TickFrequency = 0.01, IsSnapToTickEnabled = false };
    private readonly Slider m_Rotation = new() { Minimum = -180, Maximum = 180, Value = 0 };
    private readonly Slider m_Reach = new() { Minimum = 0.1, Maximum = 2.0, Value = StickerLayer.DefaultReach, TickFrequency = 0.05, IsSnapToTickEnabled = false };
    private readonly CheckBox m_Mirror = new() { Content = "Mirror", Foreground = s_Text, Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox m_IgnoreAlpha = new() { Content = "Ignore alpha channel", Foreground = s_Text, Margin = new Thickness(0, 4, 0, 0), IsEnabled = false };
    private readonly TextBlock m_SizeValue = Caption("0.12", 11, 0.8);
    private readonly TextBlock m_HeightValue = Caption("0.12", 11, 0.8);
    private readonly TextBlock m_RotationValue = Caption("0°", 11, 0.8);
    private readonly TextBlock m_ReachValue = Caption("0.50", 11, 0.8);
    private readonly ComboBox m_LayerSize = new() { MinWidth = 90 };
    private readonly Button m_FitHeight = new() { Content = "Picture's shape", Padding = new Thickness(8, 1, 8, 1), FontSize = 10, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock m_Status = Caption("", 11, 0.85);
    private readonly Button m_Remove = new() { Content = "Remove", Padding = new Thickness(8, 1, 8, 1), IsEnabled = false };
    private readonly Button m_Forget = new() { Content = "Remove picture", Padding = new Thickness(8, 1, 8, 1), IsEnabled = false };
    private readonly Button m_SlotEdit = new() { Content = "Edit slot", Padding = new Thickness(8, 1, 8, 1) };
    private readonly Button m_SlotSave = new() { Content = "Save slot", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
    private readonly Button m_SlotReset = new() { Content = "Automatic", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock m_SlotStatus = Caption("", 11, 0.85);
    private readonly Button m_FirstPerson = new() { Content = "1st person", Padding = new Thickness(8, 1, 8, 1),
        ToolTip = "The weapon as the soldier sees it in first person, with his arms (the game's camera bone, posed by this weapon's idle pose). " +
                  "The FOV slider is on the bar over the view. Right-drag or the wheel goes back to orbiting." };
    private readonly TextBlock m_ViewStatus = Caption("", 11, 0.85);
    private readonly Func<string, string?>? m_WeaponOfMesh;

    // ⭐ on a VEHICLE the panel only places the emblem's squares (the emblem goes on it as a decal; the studio only says
    // where — not to make the shader): the parts that make a sticker or ship it are hidden there, and the sizes are metres
    private readonly TextBlock m_Intro = Caption("", 11, 0.7);
    private readonly StackPanel m_OnThisLabel = Label("", "");
    private readonly StackPanel m_SlotLabel = Label("", "");
    private readonly StackPanel m_WidthLabel = Label("", "");
    private readonly StackPanel m_HeightLabel = Label("", "");
    private readonly List<UIElement> m_WeaponOnly = new();
    private bool? m_SlotOnlyShown;

    /// <summary>Marks a part of the panel that a slot-only subject (a vehicle) does not show.</summary>
    private T WeaponOnly<T>(T p_Element) where T : UIElement
    {
        m_WeaponOnly.Add(p_Element);
        return p_Element;
    }

    /// <summary>Whether the panel shows the slot-only form (a vehicle on screen) — for a driver.</summary>
    public bool ShowsSlotOnly => m_SlotOnlyShown == true;

    /// <summary>The panel follows the subject on screen: a weapon's sticker form, or a vehicle's emblem squares alone (in metres).</summary>
    private void ShowFormFor(bool p_SlotOnly)
    {
        if (m_SlotOnlyShown == p_SlotOnly)
            return;

        m_SlotOnlyShown = p_SlotOnly;
        foreach (var s_Element in m_WeaponOnly)
            s_Element.Visibility = p_SlotOnly ? Visibility.Collapsed : Visibility.Visible;

        m_Intro.Text = p_SlotOnly
            ? "A vehicle takes the players' emblems as decals: here you only say where. Edit slot, then click on the vehicle where an emblem " +
              "goes; drag a square to move it, the ring turns it, the corners size it (the arrow is the emblem's top). Right-drag orbits, wheel zooms."
            : "Pick a picture below, then click on the weapon's body to place it. Drag it to move it; the ring " +
              "turns it, the corners size it (or G, R, S with the mouse). Right-drag orbits, wheel zooms.";
        SetLabel(m_OnThisLabel, p_SlotOnly ? "On this vehicle" : "On this weapon", p_SlotOnly
            ? "The emblem's squares being placed on the vehicle being looked at."
            : "The stickers placed on the weapon being looked at. Each weapon has its own — a spot on one is nowhere in particular on another.");
        SetLabel(m_SlotLabel, "Emblem slot", p_SlotOnly
            ? "Where each player's emblem goes on this vehicle. Edit slot puts its square(s) on it — BF4's, on the vehicles the two games share " +
              "— and a click on bare body adds another: move, turn and size them with the handles, Remove drops one, then Save slot keeps them " +
              "in a file for this vehicle. Forget mine drops your file (BF4's comes back where there is one). The vehicle's shader is not touched."
            : "Where each player's emblem goes on this weapon, for every camo. Edit slot puts its square(s) " +
              "on the weapon (the test emblem drawn in them): move, turn and size them with the handles like a sticker, Remove drops one, " +
              "then Save slot keeps them in a file for this weapon. Automatic forgets the saved one and uses the spot the studio finds.");
        SetLabel(m_WidthLabel, p_SlotOnly ? "Width (metres)" : "Width", p_SlotOnly
            ? "The square's side across, in metres (BF4 puts 0.3–0.5 m on the ground vehicles, up to 1 m on the jets). A corner handle sizes width and height on their own; hold Shift to keep the shape."
            : "As a share of the weapon's texture: 0.1 is a tenth of the unwrap across. A corner handle on the weapon sizes width and height on their own; hold Shift to keep the shape.");
        SetLabel(m_HeightLabel, p_SlotOnly ? "Height (metres)" : "Height", p_SlotOnly
            ? "The square's side up and down, in metres."
            : "The sticker's own height, to counter a stretched unwrap. 'Picture's shape' lets it follow the picture again.");
        m_SlotReset.Content = p_SlotOnly ? "Forget mine" : "Automatic";
        m_Size.Minimum = m_Height.Minimum = p_SlotOnly ? 0.05 : 0.01;
        m_Size.Maximum = m_Height.Maximum = p_SlotOnly ? 1.5 : 0.8;
    }

    private static void SetLabel(StackPanel p_Label, string p_Title, string p_Help)
    {
        ((TextBlock) p_Label.Children[0]).Text = p_Title;
        ((TextBlock) p_Label.Children[1]).Text = p_Help;
    }

    private bool m_Filling;
    private ScrollViewer? m_Scroll;

    /// <summary>Scrolls the panel to its end — for a driver photographing the controls below the fold.</summary>
    public void ScrollToBottom() => m_Scroll?.ScrollToEnd();

    /// <summary>The library picture a click on the weapon places, or null.</summary>
    public string? Armed { get; private set; }

    /// <summary>Raised when the user presses Done — the studio leaves the mode.</summary>
    public event EventHandler? Done;

    public StickerPanel(RimeShaderEditor.MainWindow p_Editor, string p_Library, Func<string, string?>? p_WeaponOfMesh = null)
    {
        m_Editor = p_Editor;
        m_Library = p_Library;
        m_WeaponOfMesh = p_WeaponOfMesh;
        Directory.CreateDirectory(m_Library);
        Background = s_Panel;

        var s_Scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var s_Root = new StackPanel { Margin = new Thickness(10, 10, 10, 10) };
        s_Scroll.Content = s_Root;
        m_Scroll = s_Scroll;
        Children.Add(s_Scroll);

        var s_Head = new DockPanel();
        var s_Done = new Button { Content = "Done", Padding = new Thickness(10, 2, 10, 2) };
        s_Done.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);
        DockPanel.SetDock(s_Done, Dock.Right);
        s_Head.Children.Add(s_Done);
        s_Head.Children.Add(Caption("Stickers", 13, 1.0, FontWeights.Bold));
        s_Root.Children.Add(s_Head);
        s_Root.Children.Add(m_Intro);

        // ── the library ──────────────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(WeaponOnly(Label("Pictures", "Your sticker library: PNG with transparency works best. Each picture is copied into the library folder, so a camo keeps working if the original moves.")));
        s_Root.Children.Add(WeaponOnly(m_Palette));

        var s_LibraryRow = new StackPanel { Orientation = Orientation.Horizontal };
        var s_Add = new Button { Content = "Add picture…", Padding = new Thickness(8, 1, 8, 1) };
        s_Add.Click += (_, _) => AddPictures();
        s_LibraryRow.Children.Add(s_Add);
        m_Forget.Margin = new Thickness(6, 0, 0, 0);
        m_Forget.Click += (_, _) => ForgetArmed();
        s_LibraryRow.Children.Add(m_Forget);
        s_Root.Children.Add(WeaponOnly(s_LibraryRow));

        // ── the placements on this weapon ─────────────────────────────────────────────────────────────
        s_Root.Children.Add(m_OnThisLabel);
        m_List.SelectionChanged += (_, _) =>
        {
            if (m_Filling)
                return;

            m_Editor.SelectSticker(m_List.SelectedItem is Row s_Row ? s_Row.Index : -1);
        };
        s_Root.Children.Add(m_List);

        var s_ListRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        m_Remove.Click += (_, _) =>
        {
            if (m_List.SelectedItem is Row s_Row)
                m_Editor.RemoveSticker(s_Row.Index);
        };
        s_ListRow.Children.Add(m_Remove);
        s_Root.Children.Add(s_ListRow);

        // ── the emblem slot of this weapon ────────────────────────────────────────────────────────────
        // ⭐ the slot placed by hand, weapon by weapon, and saved to a file per weapon that every camo's bake reads
        s_Root.Children.Add(m_SlotLabel);
        var s_SlotRow = new StackPanel { Orientation = Orientation.Horizontal };
        m_SlotEdit.Click += (_, _) => EditSlot();
        m_SlotSave.Click += (_, _) => SaveSlot();
        m_SlotReset.Click += (_, _) => ResetSlot();
        s_SlotRow.Children.Add(m_SlotEdit);
        s_SlotRow.Children.Add(m_SlotSave);
        s_SlotRow.Children.Add(m_SlotReset);
        s_Root.Children.Add(s_SlotRow);
        s_Root.Children.Add(m_SlotStatus);

        // ⭐ simulate how the soldier would see it in first person with the weapon in his hands — the view from the soldier's eye
        // (its field of view is the slider on the bar over the 3D view: one knob, beside the picture it changes)
        var s_ViewRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        m_FirstPerson.Click += (_, _) => ToggleFirstPerson();
        s_ViewRow.Children.Add(m_FirstPerson);
        s_Root.Children.Add(WeaponOnly(s_ViewRow));
        s_Root.Children.Add(WeaponOnly(m_ViewStatus));

        // ── the selected sticker ──────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(m_WidthLabel);
        s_Root.Children.Add(SliderRow(m_Size, m_SizeValue));
        s_Root.Children.Add(m_HeightLabel);
        var s_HeightRow = SliderRow(m_Height, m_HeightValue);
        s_Root.Children.Add(s_HeightRow);
        m_FitHeight.HorizontalAlignment = HorizontalAlignment.Left;
        m_FitHeight.Margin = new Thickness(0, 2, 0, 0);
        m_FitHeight.Click += (_, _) =>
        {
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Height: 0);
        };
        s_Root.Children.Add(WeaponOnly(m_FitHeight));
        s_Root.Children.Add(Label("Rotation", "Degrees about the sticker's centre. The ring handle on the weapon does the same."));
        s_Root.Children.Add(SliderRow(m_Rotation, m_RotationValue));
        s_Root.Children.Add(WeaponOnly(m_Mirror));
        s_Root.Children.Add(WeaponOnly(Label("Reach", "A sticker is projected onto the body from where it sits, so it runs over the parts standing proud next to it. This is how far it reaches above and below its surface, as a share of its size: raise it to climb onto a taller part, lower it to keep off a part behind.")));
        s_Root.Children.Add(WeaponOnly(SliderRow(m_Reach, m_ReachValue)));

        m_Size.ValueChanged += (_, _) =>
        {
            m_SizeValue.Text = m_Size.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Width: m_Size.Value);
        };
        m_Height.ValueChanged += (_, _) =>
        {
            m_HeightValue.Text = m_Height.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Height: m_Height.Value);
        };
        m_Rotation.ValueChanged += (_, _) =>
        {
            m_RotationValue.Text = $"{m_Rotation.Value:0}°";
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Rotation: m_Rotation.Value);
        };
        m_Reach.ValueChanged += (_, _) =>
        {
            m_ReachValue.Text = m_Reach.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Reach: m_Reach.Value);
        };
        m_Mirror.Click += (_, _) =>
        {
            if (!m_Filling && m_List.SelectedItem is Row s_Row)
                m_Editor.UpdateSticker(s_Row.Index, p_Mirror: m_Mirror.IsChecked == true);
        };

        // ── the animated sticker ──────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(WeaponOnly(Label("Animated GIF", "A placed GIF plays with its own frames and timing. Ignore alpha channel draws the GIF's transparent pixels as opaque black, so the sticker is the whole frame rectangle; the frame sheet is rebuilt at once, here and in the package.")));
        m_IgnoreAlpha.Click += (_, _) =>
        {
            if (!m_Filling)
                m_Editor.SetStickerAnimationOpaque(m_IgnoreAlpha.IsChecked == true);
        };
        s_Root.Children.Add(WeaponOnly(m_IgnoreAlpha));

        // ── how it ships ──────────────────────────────────────────────────────────────────────────────
        s_Root.Children.Add(WeaponOnly(Label("Layer size", "Each weapon that has stickers ships one square texture with them composed in its own unwrap. 1024 is sharp enough for a logo (a quarter of a weapon's own texture, 1.4 MB per weapon); 2048 matches the weapon's own texture and weighs 5.6 MB per weapon in the package.")));
        // The editor's dark combo style: the default WPF popup is white on white against this panel
        // (the layer size settings could not be read).
        if (m_Editor.TryFindResource("DarkCombo") is Style s_DarkCombo)
            m_LayerSize.Style = s_DarkCombo;
        foreach (var s_Side in new[] { 512, 1024, 2048 })
            if (s_Side <= ShaderGraph.MaxStickerLayerSide)
                m_LayerSize.Items.Add(s_Side);
        m_LayerSize.SelectedItem = 1024;
        m_LayerSize.SelectionChanged += (_, _) =>
        {
            if (!m_Filling && m_LayerSize.SelectedItem is int s_Side)
                m_Editor.SetStickerLayerSize(s_Side);
        };
        s_Root.Children.Add(WeaponOnly(m_LayerSize));
        s_Root.Children.Add(m_Status);
        m_Status.Margin = new Thickness(0, 10, 0, 0);

        m_Editor.StickersChanged += ShowPlacements;
        m_Editor.StickerSelectionChanged += SyncSelection;

        RefreshLibrary();
        ShowPlacements();
    }

    /// <summary>The library folder — the pictures a camo's placements name.</summary>
    public string Library => m_Library;

    /// <summary>The pictures in the library, sorted by name.</summary>
    public IReadOnlyList<string> Pictures => Directory.Exists(m_Library)
        ? Directory.GetFiles(m_Library)
            .Where(p_F => IsPicture(p_F))
            .OrderBy(p_F => Path.GetFileName(p_F), StringComparer.OrdinalIgnoreCase)
            .ToList()
        : new List<string>();

    private static bool IsPicture(string p_Path) =>
        Path.GetExtension(p_Path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tga" or ".dds";

    /// <summary>
    /// Copies a picture into the library (as a PNG, transparency kept) and returns its library path, or
    /// null with a status line when it cannot be read. A name already in the library is replaced.
    /// </summary>
    public string? Import(string p_Source)
    {
        try
        {
            var s_Image = m_Editor.StickerImage(p_Source);
            if (s_Image == null)
            {
                m_Status.Text = $"'{Path.GetFileName(p_Source)}' could not be read as a picture.";
                return null;
            }

            var s_Name = Path.GetFileNameWithoutExtension(p_Source);

            // An animated GIF is kept as the GIF itself: placed, it plays as a flipbook with the file's own
            // frames and timing, so it looks the same as the original GIF.
            if (RimeShaderEditor.Graph.GifClip.Decode(p_Source) is { } s_Clip && s_Clip.Frames.Count > 1)
            {
                var s_GifTarget = Path.Combine(m_Library, s_Name + ".gif");
                if (!string.Equals(Path.GetFullPath(p_Source), Path.GetFullPath(s_GifTarget), StringComparison.OrdinalIgnoreCase))
                    File.Copy(p_Source, s_GifTarget, true);
                m_Editor.ForgetStickerImage(s_GifTarget);
                m_Editor.ForgetStickerAtlas(s_GifTarget);
                m_Status.Text = $"Added '{s_Name}' — animated: {s_Clip.Width}×{s_Clip.Height}, {s_Clip.Frames.Count} frame(s) " +
                                $"({s_Clip.SourceFrames} in the file), {s_Clip.TotalSeconds:0.##} s per loop.";
                return s_GifTarget;
            }

            var s_Target = Path.Combine(m_Library, s_Name + ".png");
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(s_Image));
            using (var s_Stream = File.Create(s_Target))
                s_Encoder.Save(s_Stream);

            m_Editor.ForgetStickerImage(s_Target);
            m_Status.Text = $"Added '{s_Name}' ({s_Image.PixelWidth}×{s_Image.PixelHeight}).";
            return s_Target;
        }
        catch (Exception s_Exception)
        {
            m_Status.Text = $"Could not add '{Path.GetFileName(p_Source)}': {s_Exception.Message}";
            return null;
        }
    }

    /// <summary>Makes a library picture the one the next click places (null disarms).</summary>
    public void Arm(string? p_Path)
    {
        Armed = p_Path != null && File.Exists(p_Path) ? p_Path : null;
        m_Editor.StickerToPlace = Armed;
        m_Forget.IsEnabled = Armed != null;
        foreach (var s_Tile in m_Palette.Children.OfType<ToggleButton>())
            s_Tile.IsChecked = Armed != null && string.Equals(s_Tile.Tag as string, Armed, StringComparison.OrdinalIgnoreCase);

        if (Armed != null)
            m_Status.Text = $"'{Path.GetFileNameWithoutExtension(Armed)}' armed — click on the weapon to place it.";
    }

    private void AddPictures()
    {
        var s_Dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add sticker pictures",
            Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.dds|All files (*.*)|*.*",
            Multiselect = true,
        };

        if (s_Dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        string? s_Last = null;
        foreach (var s_File in s_Dialog.FileNames)
            s_Last = Import(s_File) ?? s_Last;

        RefreshLibrary();
        if (s_Last != null)
            Arm(s_Last);
    }

    /// <summary>The weapon on screen as the catalogue names it (its folder), or its mesh's last segment.</summary>
    private string CurrentWeapon => m_Editor.CurrentPreviewMesh is { Length: > 0 } s_Mesh
        ? m_WeaponOfMesh?.Invoke(s_Mesh) ?? s_Mesh.Split('/')[^1]
        : "";

    /// <summary>Puts the weapon's slot on it as squares to move, turn and size (what Edit slot does).</summary>
    public void EditSlot()
    {
        // (a library picture armed would place itself instead of the square: disarmed first, and the editor arms the square)
        Arm(null);
        var s_Count = m_Editor.EditEmblemSlot();
        var s_Subject = m_Editor.StickerSlotOnly ? "vehicle" : "weapon";
        m_SlotStatus.Text = m_Editor.StickerToPlace != EmblemSlot.Image
            ? "No emblem slot on this camo's graph, or no weapon on screen."
            : s_Count > 0
                ? $"{CurrentWeapon}: {s_Count} square(s) on the {s_Subject} — move, turn and size them (a click on bare body adds another), then Save slot."
                : m_Editor.StickerSlotOnly
                    ? $"{CurrentWeapon}: no square on this vehicle yet — click on it where an emblem goes, then Save slot."
                    : $"{CurrentWeapon}: no automatic spot on this weapon — click on it where the emblem goes, then Save slot.";
        ShowSlotState();
    }

    /// <summary>Saves the squares on the weapon as its slot (what Save slot does); the file, or null.</summary>
    public string? SaveSlot()
    {
        var s_Path = m_Editor.SaveEmblemSlot(CurrentWeapon, out var s_Why);
        m_SlotStatus.Text = s_Path != null ? $"{CurrentWeapon}: slot saved — {s_Path}" : s_Why;
        ShowSlotState();
        return s_Path;
    }

    /// <summary>Forgets the weapon's saved slot: the automatic spot again (what Automatic does).</summary>
    public void ResetSlot()
    {
        var s_Had = m_Editor.ResetEmblemSlot();
        m_SlotStatus.Text = m_Editor.StickerSlotOnly
            ? s_Had ? $"{CurrentWeapon}: your slot forgotten." : $"{CurrentWeapon}: no slot of yours to forget."
            : s_Had ? $"{CurrentWeapon}: back to the automatic spot." : $"{CurrentWeapon}: already on the automatic spot.";
        ShowSlotState();
    }

    /// <summary>The slot buttons follow what the weapon on screen has: squares being placed, a saved slot, or the automatic one.</summary>
    private void ShowSlotState()
    {
        var s_Squares = m_Editor.StickersOnCurrentWeapon().Where(p_S => EmblemSlot.IsEmblem(p_S.Sticker)).Select(p_S => p_S.Sticker).ToList();
        var s_Editing = s_Squares.Count;
        var s_Armed = m_Editor.StickerToPlace == EmblemSlot.Image;
        m_SlotSave.IsEnabled = s_Editing > 0;
        m_SlotEdit.IsEnabled = s_Editing == 0 && !s_Armed;

        // a vehicle: its squares lie in space — no unwrap tile to fall outside of, no first-person side to miss
        if (m_Editor.StickerSlotOnly)
        {
            if (m_SlotStatus.Text.Length == 0)
                m_SlotStatus.Text = s_Editing > 0 || s_Armed
                    ? $"{CurrentWeapon}: {s_Editing} square(s) being placed — Save slot keeps them."
                    : m_Editor.SavedEmblemSlotFile() is { } s_Saved
                        ? $"{CurrentWeapon}: slot saved ({Path.GetFileName(Path.GetDirectoryName(s_Saved))}\\{Path.GetFileName(s_Saved)})."
                        : $"{CurrentWeapon}: no emblem slot yet — Edit slot, then click on the vehicle where an emblem goes.";
            return;
        }

        if (m_SlotStatus.Text.Length == 0)
            m_SlotStatus.Text = s_Editing > 0 || s_Armed
                ? $"{CurrentWeapon}: {s_Editing} square(s) being placed — Save slot keeps them for every camo."
                : m_Editor.SavedEmblemSlotFile() is { } s_File
                    ? $"{CurrentWeapon}: slot placed by hand ({Path.GetFileName(s_File)})."
                    : $"{CurrentWeapon}: automatic slot.";

        // ⛔ The slot's map covers the texture's first tile only (u and v 0..1): a square whose centre lies outside it draws nothing
        // (measured 2026-09-29: the L85A2's −x side, its right, at u 1.09). Said, so a square is not left there believing it works.
        // (a PROJECTED slot reads no unwrap: there it draws wherever it lies)
        var s_Outside = StickerGraph.IsProjected(m_Editor.DocumentGraph)
            ? 0
            : s_Squares.Count(p_S => p_S.U < 0 || p_S.U > 1 || p_S.V < 0 || p_S.V > 1);
        if (s_Outside > 0 && !m_SlotStatus.Text.Contains('⚠'))
            m_SlotStatus.Text += $"  ⚠ {s_Outside} square(s) sit on a part whose texture lies outside the weapon's first tile — the emblem " +
                                 "cannot draw there yet: move them onto another part of the body.";

        // ⭐ The first-person view shows the weapon's LEFT side (squares on the right would never be seen): a slot with no
        // square there is never seen by its owner. The squares being placed, or else the slot the bake will use.
        var s_Slot = s_Squares.Count > 0 || m_Editor.CurrentPreviewMesh is not { } s_Mesh ? s_Squares : m_Editor.EmblemPlacementsFor(s_Mesh);
        if (s_Slot.Count > 0 && !s_Slot.Any(p_S => p_S.Anchor is { Length: 3 } s_A && EmblemSlot.IsLeft(s_A)) && !m_SlotStatus.Text.Contains("left side", StringComparison.Ordinal))
            m_SlotStatus.Text += "  ⚠ No square on the weapon's left side: the first-person view shows the left, so its owner would not see the emblem.";
    }

    /// <summary>The slot line under its buttons, for a driver.</summary>
    public string SlotStatusText => m_SlotStatus.Text;

    /// <summary>The first-person line under its button, for a driver.</summary>
    public string ViewStatusText => m_ViewStatus.Text;

    /// <summary>What the "1st person" button does: the soldier's eye on the weapon on screen, or back to the orbit.</summary>
    public void ToggleFirstPerson()
    {
        if (m_Editor.FirstPersonView)
        {
            m_Editor.SetFirstPersonCamera(null);
            m_Editor.ShowFirstPersonArms(null);
            m_ViewStatus.Text = "";
            return;
        }

        // the eye is on screen at once when the cache has it, and the arms follow; a weapon whose eye was never measured is read from
        // the game first (its eye and its arms, one mount)
        if (m_Editor.CurrentPreviewMesh is { } s_Mesh && Catalog.FirstPersonCameras.Of(s_Mesh) == null)
            LastArms = MeasureThenShowAsync(s_Mesh);
        else if (ShowFirstPerson() && m_Editor.CurrentPreviewMesh is { } s_Shown)
            LastArms = ShowArmsAsync(s_Shown);
    }

    /// <summary>The last request of the "1st person" button (a mount the first time for a weapon), for a driver to wait on.</summary>
    public System.Threading.Tasks.Task? LastArms { get; private set; }

    /// <summary>
    /// Reads what the cache lacks for the first-person view of a weapon out of the game (its eye, the arms around it: one mount, about
    /// half a minute), saying so on the first-person line meanwhile. False when the game or RimeREPL cannot be found.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ReadFirstPersonAsync(string p_Mesh, string p_Doing)
    {
        if (!RimeCamoStudio.Settings.LooksLikeGame(RimeCamoStudio.Settings.GamePath) || RimeCamoStudio.Settings.FindRimeRepl() is not { } s_Repl)
        {
            m_ViewStatus.Text += " The game or RimeREPL is not found.";
            return false;
        }

        var s_Said = m_ViewStatus.Text;
        m_ViewStatus.Text = (s_Said + $" {p_Doing} (the game is mounted once for this weapon, about half a minute)…").TrimStart();
        m_FirstPerson.IsEnabled = false;
        try
        {
            var s_Game = RimeCamoStudio.Settings.GamePath;
            await System.Threading.Tasks.Task.Run(() => Cache.GameCache.DumpFirstPerson(new[] { p_Mesh }, s_Game, s_Repl));
        }
        finally
        {
            m_FirstPerson.IsEnabled = true;
            m_ViewStatus.Text = s_Said;
        }

        return true;
    }

    /// <summary>The first press on a weapon whose eye the cache does not have: measured out of the game with the arms, then shown.</summary>
    private async System.Threading.Tasks.Task MeasureThenShowAsync(string p_Mesh)
    {
        m_ViewStatus.Text = "";
        if (!await ReadFirstPersonAsync(p_Mesh, "Measuring the soldier's eye on this weapon and posing the arms around it"))
            return;

        // the user may have left the weapon while the game was mounted
        if (!string.Equals(m_Editor.CurrentPreviewMesh, p_Mesh, StringComparison.OrdinalIgnoreCase))
            return;

        if (ShowFirstPerson())
            await ShowArmsAsync(p_Mesh);
    }

    /// <summary>
    /// The soldier's first-person arms around the weapon on screen: from the mesh cache, or posed there first (one mount, the first time
    /// for each weapon). Said on the first-person line.
    /// </summary>
    private async System.Threading.Tasks.Task ShowArmsAsync(string p_Mesh)
    {
        var s_File = Cache.GameCache.ArmsFile(p_Mesh);
        if (!File.Exists(s_File))
        {
            if (!await ReadFirstPersonAsync(p_Mesh, "Posing the soldier's arms around this weapon"))
                return;

            // the user may have left the view or the weapon while the game was mounted
            if (!m_Editor.FirstPersonView || !string.Equals(m_Editor.CurrentPreviewMesh, p_Mesh, StringComparison.OrdinalIgnoreCase))
                return;

            if (!File.Exists(s_File))
            {
                m_ViewStatus.Text += " The arms could not be posed (see cache_firstperson.log in the cache folder).";
                return;
            }
        }

        if (m_Editor.ShowFirstPersonArms(s_File, Cache.GameCache.FirstPersonArmsMesh) is { } s_Error)
            m_ViewStatus.Text += $" No arms: {s_Error}.";
    }

    /// <summary>Puts the view on the soldier's eye (the FOV box's value); says why not when it cannot.</summary>
    public bool ShowFirstPerson()
    {
        var s_Mesh = m_Editor.CurrentPreviewMesh;
        if (Catalog.FirstPersonCameras.Of(s_Mesh) is not { } s_Eye)
        {
            m_ViewStatus.Text = $"No first-person eye could be measured for {s_Mesh?.Split('/')[^1] ?? "this view"} (see cache_firstperson.log in the cache folder).";
            return false;
        }

        if (!m_Editor.SetFirstPersonCamera(s_Eye.Position, s_Eye.Look, s_Eye.Up))
        {
            m_ViewStatus.Text = "The weapon is not loaded yet.";
            return false;
        }

        m_ViewStatus.Text = $"{CurrentWeapon}: first person (FOV on the bar over the view). Right-drag or the wheel goes back to orbiting.";
        return true;
    }

    private void ForgetArmed()
    {
        if (Armed == null)
            return;

        var s_Used = (m_Editor.DocumentGraph.Stickers ?? new List<StickerPlacement>())
            .Count(p_S => p_S.Image.Equals(Armed, StringComparison.OrdinalIgnoreCase));
        if (s_Used > 0)
        {
            m_Status.Text = $"'{Path.GetFileNameWithoutExtension(Armed)}' is placed {s_Used} time(s) on this camo — remove those first.";
            return;
        }

        try
        {
            File.Delete(Armed);
            m_Status.Text = $"Removed '{Path.GetFileNameWithoutExtension(Armed)}' from the library.";
        }
        catch (Exception s_Exception)
        {
            m_Status.Text = $"Could not remove it: {s_Exception.Message}";
        }

        Arm(null);
        RefreshLibrary();
    }

    /// <summary>Rebuilds the tiles from the library folder.</summary>
    public void RefreshLibrary()
    {
        m_Palette.Children.Clear();
        foreach (var s_File in Pictures)
        {
            var s_Tile = new ToggleButton
            {
                Width = 64, Height = 64, Margin = new Thickness(0, 0, 4, 4), Tag = s_File,
                ToolTip = Path.GetFileName(s_File), Background = s_Field, BorderBrush = s_Edge,
                Content = new Image { Source = Thumbnail(s_File), Stretch = Stretch.Uniform, Margin = new Thickness(3) },
            };
            s_Tile.Checked += (_, _) => Arm(s_File);
            s_Tile.Unchecked += (_, _) =>
            {
                if (string.Equals(Armed, s_File, StringComparison.OrdinalIgnoreCase))
                    Arm(null);
            };
            m_Palette.Children.Add(s_Tile);
        }

        if (m_Palette.Children.Count == 0)
            m_Palette.Children.Add(Caption("No pictures yet — Add picture… brings your own (PNG with transparency).", 11, 0.6));

        if (Armed != null && !File.Exists(Armed))
            Arm(null);
        else
            Arm(Armed);
    }

    private BitmapSource? Thumbnail(string p_File)
    {
        try
        {
            var s_Image = new BitmapImage();
            s_Image.BeginInit();
            s_Image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            s_Image.CacheOption = BitmapCacheOption.OnLoad;
            s_Image.DecodePixelWidth = 128;
            s_Image.UriSource = new Uri(p_File);
            s_Image.EndInit();
            s_Image.Freeze();
            return s_Image;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Row
    {
        public int Index { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    /// <summary>Lists the placements of the weapon on screen, keeping the editor's selection.</summary>
    public void ShowPlacements()
    {
        ShowFormFor(m_Editor.StickerSlotOnly);
        m_Filling = true;
        try
        {
            m_List.Items.Clear();
            // each square of the slot numbered in its order (the slot file's: BF4's on a vehicle both games share), so one can be named
            // (otherwise they would all be called the same)
            var s_Square = 0;
            foreach (var (s_Index, s_Sticker) in m_Editor.StickersOnCurrentWeapon())
                m_List.Items.Add(new Row
                {
                    Index = s_Index,
                    Text = $"{(EmblemSlot.IsEmblem(s_Sticker) ? $"#{s_Square++} Emblem slot" : Path.GetFileNameWithoutExtension(s_Sticker.Image))} — " +
                           $"{s_Sticker.Width:0.00}{(s_Sticker.IsSpatial ? " m" : "")} wide, {s_Sticker.Rotation:0}°{(s_Sticker.Mirror ? ", mirrored" : "")}",
                });

            m_LayerSize.SelectedItem = m_Editor.DocumentGraph.StickerLayerSide;
            m_IgnoreAlpha.IsEnabled = m_Editor.DocumentGraph.StickerAnimation != null;
            m_IgnoreAlpha.IsChecked = m_Editor.DocumentGraph.StickerAnimationOpaque;
            var s_Count = m_Editor.DocumentGraph.Stickers?.Count ?? 0;
            var s_Weapons = (m_Editor.DocumentGraph.Stickers ?? new List<StickerPlacement>())
                .Select(p_S => p_S.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (s_Count > 0)
                m_Status.Text = $"{s_Count} sticker(s) on {s_Weapons} weapon(s); each of those weapons ships one {m_Editor.DocumentGraph.StickerLayerSide}² layer.";
        }
        finally
        {
            m_Filling = false;
        }

        // the slot line speaks of the weapon on screen: a new weapon (or a change) re-reads it
        m_SlotStatus.Text = "";
        ShowSlotState();
        // (a new weapon loads in the orbit: the first-person line goes with the eye)
        if (!m_Editor.FirstPersonView)
            m_ViewStatus.Text = "";
        SyncSelection();
    }

    private void SyncSelection()
    {
        m_Filling = true;
        try
        {
            var s_Selected = m_Editor.SelectedSticker;
            m_List.SelectedItem = m_List.Items.OfType<Row>().FirstOrDefault(p_R => p_R.Index == s_Selected);
            var s_Sticker = m_Editor.StickerAt(s_Selected);
            m_Remove.IsEnabled = s_Sticker != null;
            m_Size.IsEnabled = m_Height.IsEnabled = m_Rotation.IsEnabled = m_Mirror.IsEnabled = m_Reach.IsEnabled = s_Sticker != null;
            m_FitHeight.IsEnabled = s_Sticker is { Height: > 0 };
            if (s_Sticker != null)
            {
                var s_Aspect = StickerLayer.AspectOf(m_Editor.StickerImage(s_Sticker.Image));
                m_Size.Value = Math.Clamp(s_Sticker.Width, m_Size.Minimum, m_Size.Maximum);
                m_Height.Value = Math.Clamp(StickerLayer.HeightOf(s_Sticker, s_Aspect), m_Height.Minimum, m_Height.Maximum);
                m_Rotation.Value = Math.Clamp(s_Sticker.Rotation, m_Rotation.Minimum, m_Rotation.Maximum);
                m_Mirror.IsChecked = s_Sticker.Mirror;
                m_Reach.Value = Math.Clamp(StickerLayer.ReachOf(s_Sticker), m_Reach.Minimum, m_Reach.Maximum);
            }
        }
        finally
        {
            m_Filling = false;
        }
    }

    /// <summary>Selects a placement row, as a click on the list does (for a driver).</summary>
    public void SelectRow(int p_Index) =>
        m_List.SelectedItem = m_List.Items.OfType<Row>().FirstOrDefault(p_R => p_R.Index == p_Index);

    public int RowCount => m_List.Items.Count;

    /// <summary>What each row of the list says, in order — for a driver.</summary>
    public IReadOnlyList<string> RowTexts => m_List.Items.OfType<Row>().Select(p_R => p_R.Text).ToList();
    public string StatusText => m_Status.Text;

    /// <summary>The layer sides the combo offers, in order.</summary>
    public IReadOnlyList<int> LayerSizes => m_LayerSize.Items.OfType<int>().ToList();

    /// <summary>Picks a layer side the way the user does: through the combo, so its handler runs.</summary>
    public void SelectLayerSize(int p_Side) => m_LayerSize.SelectedItem = p_Side;

    // --- widgets -------------------------------------------------------------------------------------------

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity, FontWeight? p_Weight = null) => new()
    {
        Text = p_Text, FontSize = p_Size, Opacity = p_Opacity, Foreground = s_Text, TextWrapping = TextWrapping.Wrap,
        FontWeight = p_Weight ?? FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2),
    };

    private static StackPanel Label(string p_Title, string p_Help)
    {
        var s_Panel = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };
        s_Panel.Children.Add(Caption(p_Title, 12, 1.0, FontWeights.Bold));
        s_Panel.Children.Add(Caption(p_Help, 10, 0.65));
        return s_Panel;
    }

    private static DockPanel SliderRow(Slider p_Slider, TextBlock p_Value)
    {
        var s_Row = new DockPanel();
        p_Value.Width = 44;
        p_Value.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(p_Value, Dock.Right);
        s_Row.Children.Add(p_Value);
        s_Row.Children.Add(p_Slider);
        return s_Row;
    }
}
