using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimeShaderEditor.View;

/// <summary>What the user chose in <see cref="BakeDialog"/>.</summary>
public sealed class BakeRequest
{
    public List<string> Levels { get; init; } = new();
    public string ModName { get; init; } = "";
    public string BundleName { get; init; } = "";
    public string OutputFolder { get; init; } = "";

    /// <summary>Paths of SAVED graph .json files baked alongside the open one — one mod, several shaders.</summary>
    public List<string> ExtraGraphs { get; init; } = new();

    /// <summary>
    /// Ship the mod's OWN small database of just its new keys, published under a fresh resource name,
    /// instead of a regenerated copy of the level's whole one (27,9 MB). Two mods built this way can then
    /// coexist without being merged, because neither claims the level's database name.
    ///
    /// ⛔ IT WAS ONCE WRITTEN HERE THAT ONLY VARIATIONS COULD SHIP THIS WAY, "because for an existing key
    /// the original always wins". That is FALSE and was measured in the binary: databases are appended and a
    /// lookup walks them BACKWARDS, so the one registered LAST answers for a name. A replacement ships
    /// additively too — its database just has to register AFTER the level's, which is why a mod carrying both
    /// kinds now ships TWO of them, one on each side of the level's own.
    /// ✅ Delivery CONFIRMED in-game, and since 2026-09-04 the MECHANISM is measured in the binary too:
    /// every shader database registers itself with the shader system as it loads, and the lookup walks ALL
    /// of them for the key — the resource NAME never enters the lookup, it only decides which one the level
    /// asks for. That is why a database under a fresh name serves its keys with the level's own untouched.
    /// ⚠ Two mods defining the SAME key still do not merge: the last one registered wins, silently.
    /// </summary>
    public bool AdditiveDb { get; init; }

}

/// <summary>
/// Asks for the maps to bake into, the mod and bundle names and where to write it. Built in code like the colour
/// picker so the whole editor keeps one style, and dark-themed explicitly because a Window does not inherit the
/// main window's resources.
/// </summary>
public sealed class BakeDialog : Window
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));

    private readonly List<CheckBox> m_Levels = new();
    private readonly TextBox m_ModName;
    private readonly TextBox m_BundleName;
    private readonly TextBox m_Output;
    private readonly CheckBox m_AdditiveDb;
    private readonly TextBlock m_Warning;

    // (path, target) of each saved graph added to this bake, plus the panel that lists them.
    private readonly List<(string Path, string Target, string? Variation)> m_ExtraGraphs = new();
    private StackPanel? m_ExtraList;
    private string m_PrimaryTarget = "";
    private string? m_PrimaryVariation;

    private BakeDialog(IEnumerable<string> p_Levels, string p_ModName, string p_Output, string p_TargetShader)
    {
        Title = "Bake shader";
        Background = s_Panel;
        Width = 460;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        m_ModName = Field(p_ModName);
        m_BundleName = Field(p_ModName.ToLowerInvariant());
        m_Output = Field(p_Output);
        m_AdditiveDb = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Additive database (ship only this mod's own keys)",
                TextWrapping = TextWrapping.Wrap,
            },
            Foreground = s_Text,
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = true,
            ToolTip = "Ships a few hundred KB of just the shaders this mod creates, under its own resource " +
                      "name, instead of a 27,9 MB copy of the level's database. Two mods built this way can " +
                      "be installed together without merging them. Replacements and variations can be " +
                      "mixed in one mod: each kind ships in its own database, registered on the side of the " +
                      "level's own that its keys need.",
        };

        m_Warning = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x60)), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };

        // The mod name drives the bundle name until the user takes it over, which is what people expect and
        // avoids a mismatch nobody notices until the mod does not load.
        var s_BundleTouched = false;
        m_BundleName.TextChanged += (_, _) => s_BundleTouched = true;
        m_ModName.TextChanged += (_, _) =>
        {
            if (!s_BundleTouched)
                m_BundleName.Text = Sanitize(m_ModName.Text).ToLowerInvariant();

            Revalidate();
        };

        Content = BuildLayout(p_Levels, p_TargetShader);
        Revalidate();
    }

    /// <summary>Shows the dialog. Returns null when cancelled.</summary>
    public static BakeRequest? Ask(Window? p_Owner, IEnumerable<string> p_Levels, string p_ModName,
        string p_Output, string p_TargetShader, string? p_TargetVariation = null)
    {
        var s_Dialog = new BakeDialog(p_Levels, p_ModName, p_Output, p_TargetShader);
        s_Dialog.m_PrimaryVariation = p_TargetVariation;

        // After the variation is known, never before: the knob's whole state depends on it.
        s_Dialog.RefreshAdditiveState();
        if (p_Owner != null)
            s_Dialog.Owner = p_Owner;

        if (s_Dialog.ShowDialog() != true)
            return null;

        return new BakeRequest
        {
            Levels = s_Dialog.m_Levels.Where(p_C => p_C.IsChecked == true)
                .Select(p_C => (string) p_C.Tag).ToList(),
            ModName = Sanitize(s_Dialog.m_ModName.Text),
            BundleName = Sanitize(s_Dialog.m_BundleName.Text).ToLowerInvariant(),
            OutputFolder = s_Dialog.m_Output.Text.Trim(),
            ExtraGraphs = s_Dialog.m_ExtraGraphs.Select(p_G => p_G.Path).ToList(),
            AdditiveDb = s_Dialog.m_AdditiveDb.IsChecked == true,
        };
    }

    /// <summary>
    /// Lays out and renders the dialog with no window shown, so what it looks like can be looked at rather than
    /// assumed. DesiredSize, not ActualWidth/Height: those exclude the Margin and the shot comes out clipped.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(IEnumerable<string> p_Levels,
        string p_ModName, string p_Output, string p_TargetShader)
    {
        var s_Dialog = new BakeDialog(p_Levels, p_ModName, p_Output, p_TargetShader);

        // The same call the real path makes: a screenshot that skips it shows a knob in a state the user
        // never sees, which is the harness drifting from the path it is supposed to photograph.
        s_Dialog.RefreshAdditiveState();
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

    private FrameworkElement BuildLayout(IEnumerable<string> p_Levels, string p_TargetShader)
    {
        // The root carries the background as well as the Window, so a snapshot of the content alone is faithful
        // (against a transparent surface the text antialiasing falls apart and looks like a styling bug).
        var s_Root = new Grid { Margin = new Thickness(12), Background = s_Panel };
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        m_PrimaryTarget = p_TargetShader;

        var s_Head = new StackPanel();
        s_Head.Children.Add(Caption("Shaders in this mod", 12, 1.0, FontWeights.Bold));
        s_Head.Children.Add(Caption($"•  {p_TargetShader}   (open in the editor)", 11, 0.8));

        m_ExtraList = new StackPanel();
        s_Head.Children.Add(m_ExtraList);

        var s_AddRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var s_Add = new Button { Content = "Add saved graphs…", Padding = new Thickness(6, 1, 6, 1), FontSize = 11 };
        s_Add.Click += (_, _) => AddSavedGraphs();
        s_AddRow.Children.Add(s_Add);
        // ⚠ The old caption said every added graph "replaces its own target shader", which stopped being true
        // when a graph could carry a variation instead — and the two are not interchangeable in one mod: a
        // take-over and a replacement need opposite bundle orders, so the bake refuses that mix rather than
        // shipping one of them inert.
        s_AddRow.Children.Add(Caption("each bakes into the same mod, keeping its own target and its own " +
                                      "variation — replacements and take-overs can be mixed",
            10, 0.6, null, new Thickness(8, 2, 0, 0)));
        s_Head.Children.Add(s_AddRow);

        s_Head.Children.Add(Caption("Maps to bake into", 12, 1.0, FontWeights.Bold, new Thickness(0, 10, 0, 4)));
        s_Head.Children.Add(Caption(
            "One superbundle per map. The shader has to exist in that map's database - a map that does not use " +
            "it is reported and skipped, not silently baked empty.", 10, 0.7));
        Grid.SetRow(s_Head, 0);
        s_Root.Children.Add(s_Head);

        var s_List = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var s_Any = false;

        // Grouped because a flat list of ~65 levels is unusable. Multiplayer first, then the expansions,
        // then singleplayer and co-op, which are targets like any other.
        var s_Groups = p_Levels
            .GroupBy(Emit.LevelScanner.GroupOf)
            .OrderBy(p_G => Emit.LevelScanner.GroupRank(p_G.Key));

        foreach (var s_Group in s_Groups)
        {
            var s_Members = s_Group.OrderBy(p_L => p_L, StringComparer.OrdinalIgnoreCase).ToList();
            var s_Header = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, s_Any ? 8 : 0, 0, 2),
            };
            s_Header.Children.Add(Caption($"{s_Group.Key}  ({s_Members.Count})", 11, 0.9, FontWeights.Bold));

            var s_All = new Button
            {
                Content = "all", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(8, 0, 0, 0),
                FontSize = 10,
            };
            var s_None = new Button
            {
                Content = "none", Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(3, 0, 0, 0),
                FontSize = 10,
            };
            s_Header.Children.Add(s_All);
            s_Header.Children.Add(s_None);
            s_List.Children.Add(s_Header);

            var s_Boxes = new List<CheckBox>();
            foreach (var s_Level in s_Members)
            {
                s_Any = true;
                // The label goes in a TextBlock, not straight into Content: a string Content is parsed for
                // access keys, which EATS the underscore and shows "MP001" for MP_017's neighbours.
                var s_Check = new CheckBox
                {
                    Content = new TextBlock { Text = s_Level, Foreground = s_Text },
                    Tag = s_Level, Foreground = s_Text, Margin = new Thickness(10, 2, 2, 2),
                };

                s_Check.Checked += (_, _) => Revalidate();
                s_Check.Unchecked += (_, _) => Revalidate();
                m_Levels.Add(s_Check);
                s_Boxes.Add(s_Check);
                s_List.Children.Add(s_Check);
            }

            s_All.Click += (_, _) => { foreach (var l_B in s_Boxes) l_B.IsChecked = true; };
            s_None.Click += (_, _) => { foreach (var l_B in s_Boxes) l_B.IsChecked = false; };
        }

        if (!s_Any)
            s_List.Children.Add(Caption(
                "No levels found. Check the game folder in Settings - the list is scanned from the install.",
                11, 0.9));

        var s_Scroll = new ScrollViewer
        {
            Content = s_List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = s_Field, BorderBrush = s_Edge, BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4),
        };
        Grid.SetRow(s_Scroll, 1);
        s_Root.Children.Add(s_Scroll);

        var s_Tail = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        s_Tail.Children.Add(Caption("Mod name", 11));
        s_Tail.Children.Add(m_ModName);
        s_Tail.Children.Add(Caption("Bundle name  (becomes Win32/<bundle>/<map>)", 11, 1.0, FontWeights.Normal,
            new Thickness(0, 8, 0, 0)));
        s_Tail.Children.Add(m_BundleName);
        s_Tail.Children.Add(Caption("Save the mod in", 11, 1.0, FontWeights.Normal, new Thickness(0, 8, 0, 0)));

        var s_Row = new Grid();
        s_Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        s_Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(m_Output, 0);
        s_Row.Children.Add(m_Output);

        var s_Browse = new Button { Content = "…", Width = 30, Margin = new Thickness(4, 0, 0, 0) };
        s_Browse.Click += (_, _) =>
        {
            using var s_Picker = new System.Windows.Forms.FolderBrowserDialog();
            if (System.IO.Directory.Exists(m_Output.Text.Trim()))
                s_Picker.SelectedPath = m_Output.Text.Trim();

            if (s_Picker.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                m_Output.Text = s_Picker.SelectedPath;
        };
        Grid.SetColumn(s_Browse, 1);
        s_Row.Children.Add(s_Browse);
        s_Tail.Children.Add(s_Row);
        s_Tail.Children.Add(m_AdditiveDb);
        s_Tail.Children.Add(m_Warning);

        var s_Ok = new Button { Content = "Bake", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var s_Cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        s_Ok.Click += (_, _) =>
        {
            if (Problem() == null)
                DialogResult = true;
        };

        var s_Buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        s_Buttons.Children.Add(s_Ok);
        s_Buttons.Children.Add(s_Cancel);
        s_Tail.Children.Add(s_Buttons);

        Grid.SetRow(s_Tail, 2);
        s_Root.Children.Add(s_Tail);
        return s_Root;
    }

    /// <summary>
    /// Multiselect picker for saved graph .json files. Each is opened HERE to read its target: a file that is
    /// not a graph, has no target, or duplicates one already in the list is refused with the reason — finding
    /// that out after minutes of mounting would be the expensive way.
    /// </summary>
    private void AddSavedGraphs()
    {
        var s_Picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Shader graph (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = true,
        };

        if (s_Picker.ShowDialog() != true)
            return;

        foreach (var s_File in s_Picker.FileNames)
        {
            string s_Target;
            string? s_Variation;
            try
            {
                var s_Graph = Graph.ShaderGraph.FromJson(System.IO.File.ReadAllText(s_File));
                s_Target = s_Graph.TargetShader;
                s_Variation = s_Graph.BakeVariation;
            }
            catch
            {
                MessageBox.Show(this, $"'{System.IO.Path.GetFileName(s_File)}' is not a shader graph.",
                    "Bake shader", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (string.IsNullOrWhiteSpace(s_Target))
            {
                MessageBox.Show(this, $"'{System.IO.Path.GetFileName(s_File)}' has no target shader — open " +
                                      "it in the editor and set one first.",
                    "Bake shader", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            // Two graphs may share a TARGET as long as each delivers a DIFFERENT variation of it — that is
            // exactly how several variations of one object ship in a single mod. What stays refused is two
            // graphs claiming the same (target, variation) pair: those would silently fight over one entry.
            bool SameLane(string p_OtherTarget, string? p_OtherVariation) =>
                p_OtherTarget.Equals(s_Target, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p_OtherVariation ?? "", s_Variation ?? "", StringComparison.OrdinalIgnoreCase);

            if (SameLane(m_PrimaryTarget, m_PrimaryVariation) ||
                m_ExtraGraphs.Any(p_G => SameLane(p_G.Target, p_G.Variation)))
            {
                MessageBox.Show(this, $"'{s_Target}'{(s_Variation != null ? $" ({s_Variation})" : "")} is " +
                                      "already in this bake — two graphs delivering the same shader (and same " +
                                      "variation) would silently fight over it.",
                    "Bake shader", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            m_ExtraGraphs.Add((s_File, s_Target, s_Variation));
            RefreshExtraList();
        }
    }

    /// <summary>
    /// The additive database carries this mod's own keys — a brand-new one for a variation, and the vanilla
    /// name itself for a replacement.
    ///
    /// ⛔⛔ THIS USED TO DISABLE ITSELF FOR REPLACEMENTS, ON A BELIEF THAT WAS NEVER MEASURED: that a name the
    /// level's database already holds always answers from the level's copy. Measured in the engine, the
    /// opposite is true — databases are appended and looked up BACKWARDS, so the one registered LAST answers.
    /// A replacement is therefore perfectly deliverable additively, provided its bundle is registered after
    /// the level's (the generated loader does that). The belief cost a long detour through variations,
    /// stand-in meshes and partition overrides, none of which can reach a copy inside the level's static
    /// model group — while a replacement reaches every object that uses the shader.
    /// </summary>
    private void RefreshAdditiveState()
    {
        var s_Replaces = m_PrimaryVariation == null && m_PrimaryTarget.Length > 0 ||
                         m_ExtraGraphs.Any(p_G => p_G.Variation == null);

        m_AdditiveDb.IsEnabled = true;
        m_AdditiveDb.Content = new TextBlock
        {
            Text = s_Replaces
                ? "Additive database — ships only this shader's own entry instead of a copy of the map's " +
                  "whole database, and takes over every object that uses it (the map's own included)"
                : "Additive database (ship only this mod's own keys)",
            TextWrapping = TextWrapping.Wrap,
        };
    }

    private void RefreshExtraList()
    {
        RefreshAdditiveState();

        if (m_ExtraList == null)
            return;

        m_ExtraList.Children.Clear();
        foreach (var s_Entry in m_ExtraGraphs)
        {
            var s_Row = new StackPanel { Orientation = Orientation.Horizontal };
            s_Row.Children.Add(Caption($"•  {s_Entry.Target}" + (s_Entry.Variation != null ? $"  → {s_Entry.Variation.Split('/')[^1]}" : ""), 11, 0.8));

            var s_Remove = new Button
            {
                Content = "✕", FontSize = 9, Padding = new Thickness(4, 0, 4, 0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = s_Entry.Path,
            };
            var s_Captured = s_Entry;
            s_Remove.Click += (_, _) =>
            {
                m_ExtraGraphs.Remove(s_Captured);
                RefreshExtraList();
            };
            s_Row.Children.Add(s_Remove);
            m_ExtraList.Children.Add(s_Row);
        }
    }

    private string? Problem()
    {
        if (m_Levels.All(p_C => p_C.IsChecked != true))
            return "Pick at least one map.";

        if (Sanitize(m_ModName.Text).Length == 0)
            return "The mod needs a name.";

        if (Sanitize(m_BundleName.Text).Length == 0)
            return "The bundle needs a name.";

        if (m_Output.Text.Trim().Length == 0)
            return "Pick a folder to save the mod in.";

        return null;
    }

    private void Revalidate()
    {
        var s_Problem = Problem();
        m_Warning.Text = s_Problem ?? "";

        var s_Count = m_Levels.Count(p_C => p_C.IsChecked == true);
        if (s_Problem == null && s_Count > 0)
        {
            // Said up front because the game has to be mounted once per map, and that is minutes each.
            m_Warning.Text = $"{s_Count} map(s): mounting the game takes a few minutes per map, so this is " +
                             $"roughly {1 + s_Count} long steps. The Output panel reports each one.";
        }
    }

    private static string Sanitize(string p_Text)
    {
        var s_Chars = p_Text.Trim().Where(p_C => char.IsLetterOrDigit(p_C) || p_C == '_').ToArray();
        return new string(s_Chars);
    }

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0,
        FontWeight? p_Weight = null, Thickness? p_Margin = null) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity,
        FontWeight = p_Weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
        Margin = p_Margin ?? new Thickness(0, 0, 0, 0),
    };

    private static TextBox Field(string p_Value) => new()
    {
        Text = p_Value, Background = s_Field, Foreground = s_Text, BorderBrush = s_Edge,
        Padding = new Thickness(3, 2, 3, 2),
    };
}
