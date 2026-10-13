using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RimeCamoStudio.Mod;

namespace RimeCamoStudio.View;

/// <summary>
/// Asks which baked camos to remove from the camo framework — the bake's counterpart (the same way custom
/// camos are baked, an option to delete camos already baked, except the default ones).
///
/// Only the USER packages (sb/Win32/camos/) are listed: the game's own eight default camos live under
/// defaultcamos/ and are rebuilt by the studio as a set, never removed one by one. Nothing is chosen until the
/// user ticks it, and the button stays off until something is.
/// </summary>
public sealed class DeleteCamoDialog : Window
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));
    private static readonly Brush s_Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x60));

    private readonly List<CheckBox> m_Boxes = new();
    private readonly TextBlock m_Summary;
    private readonly Button m_Ok;

    private DeleteCamoDialog(IReadOnlyList<PackageInfo> p_Packages, string p_Target)
    {
        Title = "Delete camo";
        Background = s_Panel;
        Width = 560;
        Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        m_Summary = new TextBlock
        {
            Foreground = s_Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        m_Ok = new Button { Content = "Delete", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        m_Ok.Click += (_, _) => DialogResult = true;

        Content = BuildLayout(p_Packages, p_Target);
        Refresh();
    }

    /// <summary>The vehicle catalogue, read once, for the vehicles' display names (null when it cannot be read: the blueprint's folder then).</summary>
    private static Catalog.VehicleCatalog? s_Vehicles;

    private static string VehicleName(string p_Blueprint)
    {
        try
        {
            s_Vehicles ??= Catalog.VehicleCatalog.Load();
        }
        catch (Exception)
        {
            // no catalogue: the blueprint's own folder names it
        }

        return s_Vehicles?.Vehicles.FirstOrDefault(p_V => p_V.Blueprints.Contains(p_Blueprint, StringComparer.OrdinalIgnoreCase))?.Display is { Length: > 0 } s_Display
            ? s_Display
            : p_Blueprint.Split('/').Reverse().Skip(1).FirstOrDefault()?.ToUpperInvariant() ?? p_Blueprint;
    }

    /// <summary>
    /// What a package paints, in words (which weapon / vehicle / accessory each entry refers to):
    /// its vehicles, the weapons whose body it paints, and its attachments (the iron sights aside, which ride with every body) — at most
    /// <paramref name="p_Most"/> names per kind.
    /// </summary>
    internal static string SubjectsOf(PackageInfo p_Package, int p_Most)
    {
        static string Few(IReadOnlyList<string> p_Names, int p_Most) =>
            string.Join(", ", p_Names.Take(p_Most)) + (p_Names.Count > p_Most ? $" (+{p_Names.Count - p_Most})" : "");

        var s_Parts = new List<string>();
        // (a vehicle dropped from the air is the same body: named once)
        var s_Vehicles = p_Package.Vehicles
            .Select(p_V => VehicleName(p_V.Blueprint).Replace(" (air drop)", "", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (s_Vehicles.Count > 0)
            s_Parts.Add($"vehicle{(s_Vehicles.Count == 1 ? "" : "s")}: {Few(s_Vehicles, p_Most)}");

        var s_Weapons = p_Package.Weapons.Select(p_W => p_W.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (p_Package.Body && s_Weapons.Count > 0)
            s_Parts.Add($"weapon{(s_Weapons.Count == 1 ? "" : "s")}: {Few(s_Weapons, p_Most)}");

        var s_Pieces = p_Package.Accessories
            .Where(p_A => !p_A.Tag.Equals("NoOptics", StringComparison.OrdinalIgnoreCase))
            .Select(p_A => p_A.Tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (s_Pieces.Count > 0)
            s_Parts.Add($"attachment{(s_Pieces.Count == 1 ? "" : "s")}: {Few(s_Pieces, p_Most)}" +
                        (!p_Package.Body && s_Weapons.Count > 0 ? $" (on {Few(s_Weapons, p_Most)})" : ""));

        return s_Parts.Count > 0 ? string.Join("; ", s_Parts) : "nothing listed";
    }

    /// <summary>The packages the user chose, or null when cancelled (or when there was nothing to choose from).</summary>
    public static List<PackageInfo>? Ask(Window? p_Owner, IReadOnlyList<PackageInfo> p_Packages, string p_Target)
    {
        var s_Dialog = new DeleteCamoDialog(p_Packages, p_Target);
        if (p_Owner is { IsVisible: true })
            s_Dialog.Owner = p_Owner;

        if (s_Dialog.ShowDialog() != true)
            return null;

        return s_Dialog.m_Boxes.Where(p_B => p_B.IsChecked == true).Select(p_B => (PackageInfo) p_B.Tag).ToList();
    }

    /// <summary>Renders the dialog without showing it, so its layout can be looked at rather than assumed.</summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(IReadOnlyList<PackageInfo> p_Packages, string p_Target)
    {
        var s_Dialog = new DeleteCamoDialog(p_Packages, p_Target);
        if (s_Dialog.m_Boxes.Count > 0)
            s_Dialog.m_Boxes[0].IsChecked = true;

        var s_Content = (FrameworkElement) s_Dialog.Content;
        s_Content.Measure(new Size(s_Dialog.Width, s_Dialog.Height));
        s_Content.Arrange(new Rect(0, 0, s_Dialog.Width, s_Dialog.Height));
        s_Content.UpdateLayout();

        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Dialog.Width), (int) Math.Ceiling(s_Dialog.Height), 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Content);
        return s_Target;
    }

    private FrameworkElement BuildLayout(IReadOnlyList<PackageInfo> p_Packages, string p_Target)
    {
        // Opaque, so a snapshot of the content alone is faithful instead of compositing onto nothing.
        var s_Root = new Grid { Margin = new Thickness(12), Background = s_Panel };
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var s_Head = new StackPanel();
        s_Head.Children.Add(Caption("Baked camos", 12, 1.0, FontWeights.Bold));
        s_Head.Children.Add(Caption(
            $"The camo packages found in {p_Target}. Tick the ones to remove. The game's own default camos " +
            "and the default look (what the weapons wear with no camo picked) are not listed: the studio rebuilds them.", 10, 0.7));
        Grid.SetRow(s_Head, 0);
        s_Root.Children.Add(s_Head);

        var s_List = new StackPanel { Margin = new Thickness(4) };
        // (the default emblem is kept off the list to delete, since it is literally the default -- the default look is what "no camo"
        // wears, the emblem on every weapon; it was once deleted by mistake, taken for a test)
        foreach (var s_Package in p_Packages.Where(p_P => !p_P.Native && !p_P.DefaultLook))
        {
            // ⭐ WHAT it paints (which weapon / vehicle / accessory it refers to): a camo of
            // vehicles or of a piece said "0 weapon(s)" and nothing else
            var s_Box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = $"{s_Package.Name}   —   {SubjectsOf(s_Package, 6)}   —   folder '{s_Package.Folder}', built {s_Package.Built}",
                    Foreground = s_Text, TextWrapping = TextWrapping.Wrap,
                },
                Tag = s_Package, Margin = new Thickness(4, 3, 4, 3), IsChecked = false,
                ToolTip = $"{s_Package.Description}\n{SubjectsOf(s_Package, int.MaxValue)}",
            };

            s_Box.Checked += (_, _) => Refresh();
            s_Box.Unchecked += (_, _) => Refresh();
            m_Boxes.Add(s_Box);
            s_List.Children.Add(s_Box);
        }

        if (m_Boxes.Count == 0)
            s_List.Children.Add(Caption("No baked camos in the framework.", 11, 0.8));

        var s_Scroll = new ScrollViewer
        {
            Content = s_List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = s_Field, BorderBrush = s_Edge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 120,
        };
        Grid.SetRow(s_Scroll, 1);
        s_Root.Children.Add(s_Scroll);

        var s_Tail = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        s_Tail.Children.Add(Caption(
            "What happens: the package folder goes to the Windows Recycle Bin (put it back from there to undo), " +
            "and the framework's index and mod.json are rewritten from what is left. Restart the server for the " +
            "change to take. A player who had the camo equipped sees the weapon's stock look until they pick " +
            "another one.", 10, 0.7));
        s_Tail.Children.Add(m_Summary);

        var s_Cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var s_Row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        s_Row.Children.Add(m_Ok);
        s_Row.Children.Add(s_Cancel);
        s_Tail.Children.Add(s_Row);

        Grid.SetRow(s_Tail, 2);
        s_Root.Children.Add(s_Tail);
        return s_Root;
    }

    private void Refresh()
    {
        var s_Chosen = m_Boxes.Count(p_B => p_B.IsChecked == true);
        m_Summary.Text = m_Boxes.Count == 0
            ? "Nothing to delete."
            : s_Chosen == 0
                ? "Tick at least one camo."
                : $"{s_Chosen} camo(s) will be removed from the framework.";
        m_Ok.IsEnabled = s_Chosen > 0;
    }

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0,
        FontWeight? p_Weight = null, double p_Top = 0) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity,
        FontWeight = p_Weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, p_Top, 0, 3),
    };
}
