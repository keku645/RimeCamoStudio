using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimeCamoStudio.View;

/// <summary>A soldier with something a skin would take, over every tab of the session: his parts changed and the tabs they are in.</summary>
public sealed record SoldierToBake(string Key, string Display, IReadOnlyList<string> Parts, IReadOnlyList<string> Tabs, bool OnScreen);

/// <summary>
/// Asks WHICH soldier a skin is baked for, before the bake dialog (with several soldiers' tabs open there was no telling who a bake was
/// for: so with several different soldiers open, clicking bake first asks which soldier to bake, then the normal bake dialog opens).
/// A skin is of ONE soldier; each soldier is listed with the parts changed on him and the tabs they are in, the one on screen chosen to
/// start with. Built apart from being shown, so a driver chooses and reads the answer of the SAME dialog.
/// </summary>
public sealed class BakeSoldierChooser : Window
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));

    private readonly List<RadioButton> m_Choices = new();

    private BakeSoldierChooser(IReadOnlyList<SoldierToBake> p_Soldiers)
    {
        Title = "Bake — which soldier?";
        Background = s_Panel;
        Width = 600;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = BuildLayout(p_Soldiers);
    }

    /// <summary>The dialog for these soldiers, built and not shown (the one on screen chosen, else the first).</summary>
    internal static BakeSoldierChooser Build(IReadOnlyList<SoldierToBake> p_Soldiers) => new(p_Soldiers);

    /// <summary>Shows it; the key of the soldier chosen, or null when cancelled.</summary>
    public static string? Ask(Window? p_Owner, IReadOnlyList<SoldierToBake> p_Soldiers)
    {
        var s_Dialog = Build(p_Soldiers);
        if (p_Owner is { IsVisible: true })
            s_Dialog.Owner = p_Owner;

        return s_Dialog.ShowDialog() == true ? s_Dialog.Result() : null;
    }

    /// <summary>The soldier chosen, as the Continue button would answer.</summary>
    internal string? Result() => m_Choices.FirstOrDefault(p_C => p_C.IsChecked == true)?.Tag as string;

    /// <summary>Chooses a soldier the way a click does. False when he is not listed.</summary>
    internal bool Choose(string p_Key)
    {
        var s_Choice = m_Choices.FirstOrDefault(p_C => string.Equals(p_C.Tag as string, p_Key, StringComparison.OrdinalIgnoreCase));
        if (s_Choice == null)
            return false;

        s_Choice.IsChecked = true;
        return true;
    }

    /// <summary>Renders the dialog without showing it, so its layout can be looked at rather than assumed.</summary>
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

    private FrameworkElement BuildLayout(IReadOnlyList<SoldierToBake> p_Soldiers)
    {
        // Opaque, so a snapshot of the content alone is faithful instead of compositing onto nothing.
        var s_Root = new Grid { Margin = new Thickness(12), Background = s_Panel };
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var s_Head = new StackPanel();
        s_Head.Children.Add(Caption("Which soldier's skin do you bake?", 12, 1.0, FontWeights.Bold));
        s_Head.Children.Add(Caption(
            "Your tabs hold changes on more than one soldier, and a skin is of ONE soldier. Choose him: he comes on screen and the bake " +
            "menu opens for him, with every part changed on him. The others stay in their tabs, to be baked on their own.", 10, 0.7));
        Grid.SetRow(s_Head, 0);
        s_Root.Children.Add(s_Head);

        var s_List = new StackPanel { Margin = new Thickness(4) };
        var s_First = p_Soldiers.FirstOrDefault(p_S => p_S.OnScreen) ?? p_Soldiers.FirstOrDefault();
        foreach (var s_Soldier in p_Soldiers)
        {
            var s_Lines = new StackPanel();
            s_Lines.Children.Add(new TextBlock
            {
                Text = $"{s_Soldier.Display}{(s_Soldier.OnScreen ? "   (on screen)" : "")}   —   {string.Join(", ", s_Soldier.Parts)}",
                Foreground = s_Text, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            s_Lines.Children.Add(new TextBlock
            {
                Text = $"in the tab{(s_Soldier.Tabs.Count == 1 ? "" : "s")}: {string.Join("  ·  ", s_Soldier.Tabs.Select(p_T => $"'{p_T}'"))}",
                Foreground = s_Text, Opacity = 0.65, FontSize = 10, TextWrapping = TextWrapping.Wrap,
            });

            var s_Choice = new RadioButton
            {
                Content = s_Lines, Tag = s_Soldier.Key, GroupName = "soldier", Margin = new Thickness(4, 5, 4, 5),
                IsChecked = ReferenceEquals(s_Soldier, s_First),
            };
            m_Choices.Add(s_Choice);
            s_List.Children.Add(s_Choice);
        }

        var s_Scroll = new ScrollViewer
        {
            Content = s_List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = s_Field, BorderBrush = s_Edge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 6, 0, 0), MinHeight = 120,
        };
        Grid.SetRow(s_Scroll, 1);
        s_Root.Children.Add(s_Scroll);

        var s_Continue = new Button { Content = "Continue", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        s_Continue.Click += (_, _) => DialogResult = true;
        var s_Cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var s_Row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        s_Row.Children.Add(s_Continue);
        s_Row.Children.Add(s_Cancel);
        Grid.SetRow(s_Row, 2);
        s_Root.Children.Add(s_Row);
        return s_Root;
    }

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0,
        FontWeight? p_Weight = null, double p_Top = 0) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity,
        FontWeight = p_Weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, p_Top, 0, 3),
    };
}
