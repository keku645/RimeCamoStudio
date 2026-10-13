using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RimeShaderEditor.View;

namespace RimeShaderEditor;

/// <summary>
/// The editor's preferences dialog. Everything applies LIVE through the owner's callback (no OK/Cancel
/// ceremony — the effect of a zoom speed is judged by feel, so the user must see it while the dialog is
/// still open) and persists via the same guarded save path as the rest of the window state.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly EditorSettings m_Settings;
    private readonly Action m_Changed;
    private readonly Window m_OwnerWindow;

    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(45, 45, 48));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private static readonly Brush s_Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    private static readonly Brush s_Border = new SolidColorBrush(Color.FromRgb(84, 84, 90));
    private static readonly Brush s_Accent = new SolidColorBrush(Color.FromRgb(150, 210, 150));

    /// <summary>Keys the canvas already uses for something that is not node placement.</summary>
    private static readonly Key[] s_ReservedKeys = { Key.C, Key.Delete, Key.Escape, Key.F };

    private Button? m_Capturing;

    public SettingsWindow(Window p_Owner, EditorSettings p_Settings, Action p_Changed)
    {
        m_Settings = p_Settings;
        m_Changed = p_Changed;
        m_OwnerWindow = p_Owner;

        Title = "Editor Settings";

        // WPF refuses an owner that was never shown, which is exactly what the headless photograph drives.
        if (p_Owner.IsVisible)
            Owner = p_Owner;

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = s_Panel;
        ShowInTaskbar = false;

        var s_Root = new StackPanel { Margin = new Thickness(14), Width = 430 };

        // ⛔ THE SAME TWO FOLDERS THE FIRST-RUN WINDOW ASKS FOR. They were only editable in the two
        // boxes along the top of the main window, which is not where anyone looks for a setting — and a
        // machine where the guess was wrong had no obvious way back.
        s_Root.Children.Add(Section("Folders"));
        s_Root.Children.Add(FolderRow("Game folder",
            "Read-only: shaders, textures and meshes come out of here.",
            () => m_Settings.GamePath, p_Value => m_Settings.GamePath = p_Value, "Choose the game folder"));
        s_Root.Children.Add(FolderRow("Cache and output folder",
            "Thumbnails, mesh dumps, shader lists, saved graphs and baked mods.",
            () => m_Settings.OutputFolder, p_Value => m_Settings.OutputFolder = p_Value,
            "Choose the cache folder"));

        // Only where it means something: the shader editor has no simple mode to offer, and a setting that
        // does nothing where it is shown teaches people to distrust the whole dialog.
        if (p_Owner is MainWindow { CamoMode: true })
        {
            s_Root.Children.Add(Section("Camo"));

            var s_Simple = new CheckBox
            {
                Content = "Simple mode (no node graph)",
                Foreground = s_Text,
                Margin = new Thickness(0, 2, 0, 2),
                IsChecked = m_Settings.CamoSimpleMode,
            };

            s_Simple.Checked += (_, _) => { m_Settings.CamoSimpleMode = true; m_Changed(); };
            s_Simple.Unchecked += (_, _) => { m_Settings.CamoSimpleMode = false; m_Changed(); };
            s_Root.Children.Add(s_Simple);

            s_Root.Children.Add(new TextBlock
            {
                Text = "Author a camo by picking an image and setting tiling and wear, instead of wiring " +
                       "nodes. The camo it produces is the same either way — this only changes what you " +
                       "work with.",
                Foreground = s_Dim, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Margin = new Thickness(20, 0, 0, 4),
            });

            // Where a bake lands. The mod is the one place a camo package has to be for the game to see it,
            // and the studio registers the package there itself — so the folder is a setting, asked once.
            s_Root.Children.Add(FolderRow("Camo framework mod",
                "The CamoFramework mod folder (holds mod.json). Baked camos are dropped under its " +
                "sb/Win32/camos/ and registered automatically; restart the server afterwards.",
                () => m_Settings.CamoFrameworkFolder, p_Value => m_Settings.CamoFrameworkFolder = p_Value,
                "Choose the camo framework mod folder"));
        }

        s_Root.Children.Add(Section("Canvas"));
        s_Root.Children.Add(SpeedRow("Zoom speed", () => m_Settings.CanvasZoomSpeed,
            p_V => m_Settings.CanvasZoomSpeed = p_V));

        s_Root.Children.Add(Section("Preview"));
        s_Root.Children.Add(SpeedRow("Zoom speed", () => m_Settings.PreviewZoomSpeed,
            p_V => m_Settings.PreviewZoomSpeed = p_V));
        s_Root.Children.Add(SpeedRow("Orbit sensitivity", () => m_Settings.PreviewOrbitSpeed,
            p_V => m_Settings.PreviewOrbitSpeed = p_V));

        var s_Flir = new CheckBox
        {
            Content = "Thermal view (FLIR)",
            Foreground = s_Text,
            Margin = new Thickness(0, 6, 0, 2),
            IsChecked = m_Settings.FlirPreview,
        };
        s_Flir.Checked += (_, _) => { m_Settings.FlirPreview = true; m_Changed(); };
        s_Flir.Unchecked += (_, _) => { m_Settings.FlirPreview = false; m_Changed(); };
        s_Root.Children.Add(s_Flir);

        s_Root.Children.Add(new TextBlock
        {
            Text = "Preview only. Off shows the material the way the game renders it with thermal optics " +
                   "off (the thermal parameters read zero). On feeds them the probe pattern so the thermal " +
                   "tint path is visible. Baked shaders are identical either way — these values are fed by " +
                   "the game at runtime, never compiled into the shader.",
            Foreground = s_Dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(20, 0, 0, 4),
        });

        var s_OnlyEdited = new CheckBox
        {
            Content = "Mesh preview: only the edited shader",
            Foreground = s_Text,
            Margin = new Thickness(0, 6, 0, 2),
            IsChecked = m_Settings.PreviewOnlyEditedShader,
        };
        s_OnlyEdited.Checked += (_, _) => { m_Settings.PreviewOnlyEditedShader = true; m_Changed(); };
        s_OnlyEdited.Unchecked += (_, _) => { m_Settings.PreviewOnlyEditedShader = false; m_Changed(); };
        s_Root.Children.Add(s_OnlyEdited);

        s_Root.Children.Add(new TextBlock
        {
            Text = "When previewing a real game object, its other material sections are HIDDEN instead of " +
                   "drawn as neutral grey — you see exactly the surfaces your shader owns.",
            Foreground = s_Dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(20, 0, 0, 4),
        });

        s_Root.Children.Add(Section("Authoring"));

        var s_UdkStyle = new CheckBox
        {
            Content = "Use the UDK-style node palette",
            Foreground = s_Text,
            Margin = new Thickness(0, 6, 0, 2),
            IsChecked = m_Settings.UdkNodeStyle,
        };
        // Rebuild, not just notify: this box renames the shortcut list below it, and a list left showing the
        // other vocabulary's names is the same defect as a panel that keeps stale pins after a mode change.
        s_UdkStyle.Checked += (_, _) => { m_Settings.UdkNodeStyle = true; m_Changed(); RebuildContent(); };
        s_UdkStyle.Unchecked += (_, _) => { m_Settings.UdkNodeStyle = false; m_Changed(); RebuildContent(); };
        s_Root.Children.Add(s_UdkStyle);

        s_Root.Children.Add(new TextBlock
        {
            Text = "The palette, the search box, the keyboard shortcuts and the node captions all switch to " +
                   "the UDK-era material vocabulary — TextureSample with its per-channel outputs, " +
                   "TextureCoordinate with UTiling/VTiling, Material as the output node, Mask with its " +
                   "channel boxes — with the property names that vocabulary uses. The BAKE does not change: " +
                   "each of those emits exactly what its native twin emits, and graphs made either way stay " +
                   "compatible. The palette then lists only that vocabulary, so the output nodes with no " +
                   "counterpart there are hidden while this is ticked; a graph that already uses them still " +
                   "opens, draws and bakes, and unticking this brings them back.",
            Foreground = s_Dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(20, 0, 0, 4),
        });

        s_Root.Children.Add(Section("Export"));

        var s_ExportPng = new CheckBox
        {
            Content = "Export textures as PNG",
            Foreground = s_Text,
            Margin = new Thickness(0, 4, 0, 2),
            IsChecked = m_Settings.ExportTexturesAsPng,
        };
        s_ExportPng.Checked += (_, _) => { m_Settings.ExportTexturesAsPng = true; m_Changed(); };
        s_ExportPng.Unchecked += (_, _) => { m_Settings.ExportTexturesAsPng = false; m_Changed(); };
        s_Root.Children.Add(s_ExportPng);

        s_Root.Children.Add(new TextBlock
        {
            Text = "The Texture panel's Export button writes a PNG ready for an image editor. Off exports " +
                   "the file exactly as it is — for game textures that is the raw DDS dump.",
            Foreground = s_Dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(20, 0, 0, 4),
        });

        s_Root.Children.Add(Section("Node shortcuts"));
        s_Root.Children.Add(new TextBlock
        {
            Text = "Hold the key and left-click the canvas to place the node. Click a key to remap it; " +
                   "Escape cancels. C, F, Delete and the number aliases (1/4) are reserved.",
            Foreground = s_Dim,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var s_Grid = new UniformGrid { Columns = 2 };
        foreach (var (s_Kind, _) in GraphCanvas.DefaultShortcuts)
            s_Grid.Children.Add(ShortcutRow(s_Kind));
        s_Root.Children.Add(s_Grid);

        var s_Buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var s_Reset = MakeButton("Reset defaults");
        s_Reset.Click += (_, _) =>
        {
            m_Settings.CanvasZoomSpeed = 1.0;
            m_Settings.PreviewZoomSpeed = 1.0;
            m_Settings.PreviewOrbitSpeed = 1.0;
            m_Settings.FlirPreview = false;
            m_Settings.PreviewOnlyEditedShader = false;
            m_Settings.UdkNodeStyle = false;
            m_Settings.NodeShortcuts.Clear();
            m_Changed();
            RebuildContent();
        };
        s_Buttons.Children.Add(s_Reset);

        var s_Close = MakeButton("Close");
        s_Close.Margin = new Thickness(6, 0, 0, 0);
        s_Close.Click += (_, _) => Close();
        s_Buttons.Children.Add(s_Close);

        s_Root.Children.Add(s_Buttons);
        Content = s_Root;

        PreviewKeyDown += OnCaptureKey;
    }

    /// <summary>Reset rebuilds the whole dialog so every control shows the default it just got.</summary>
    private void RebuildContent()
    {
        var s_Rebuilt = new SettingsWindow(m_OwnerWindow, m_Settings, m_Changed);
        s_Rebuilt.Left = Left;
        s_Rebuilt.Top = Top;
        s_Rebuilt.WindowStartupLocation = WindowStartupLocation.Manual;
        s_Rebuilt.Show();
        Close();
    }

    /// <summary>
    /// A folder setting: type it or browse to it, applied LIVE like everything else in this dialog so the
    /// main window's own boxes follow immediately rather than after a restart.
    /// </summary>
    private UIElement FolderRow(string p_Label, string p_Help, Func<string> p_Get, Action<string> p_Set,
        string p_BrowseTitle)
    {
        var s_Panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        s_Panel.Children.Add(new TextBlock
        {
            Text = p_Label, Foreground = s_Text, Margin = new Thickness(0, 0, 0, 2),
        });

        s_Panel.Children.Add(new TextBlock
        {
            Text = p_Help, Foreground = s_Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        var s_Box = new TextBox
        {
            Text = p_Get(), Background = s_Field, Foreground = s_Text, BorderBrush = s_Border,
            BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        s_Box.TextChanged += (_, _) =>
        {
            p_Set(s_Box.Text.Trim());
            m_Changed();
        };

        var s_Browse = new Button
        {
            Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0),
        };

        s_Browse.Click += (_, _) =>
        {
            var s_Dialog = new Microsoft.Win32.OpenFolderDialog { Title = p_BrowseTitle };
            if (System.IO.Directory.Exists(s_Box.Text.Trim()))
                s_Dialog.InitialDirectory = s_Box.Text.Trim();

            if (s_Dialog.ShowDialog(this) == true)
                s_Box.Text = s_Dialog.FolderName;
        };

        var s_Line = new DockPanel();
        DockPanel.SetDock(s_Browse, Dock.Right);
        s_Line.Children.Add(s_Browse);
        s_Line.Children.Add(s_Box);
        s_Panel.Children.Add(s_Line);

        return s_Panel;
    }

    private static TextBlock Section(string p_Title) => new()
    {
        Text = p_Title,
        Foreground = s_Accent,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 10, 0, 4),
    };

    private UIElement SpeedRow(string p_Label, Func<double> p_Get, Action<double> p_Set)
    {
        var s_Row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

        s_Row.Children.Add(new TextBlock
        {
            Text = p_Label,
            Foreground = s_Text,
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var s_Value = new TextBlock
        {
            Text = "×" + p_Get().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Foreground = s_Dim,
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(s_Value, Dock.Right);
        s_Row.Children.Add(s_Value);

        var s_Slider = new Slider
        {
            Minimum = 0.25,
            Maximum = 4.0,
            Value = p_Get(),
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };
        s_Slider.ValueChanged += (_, s_Args) =>
        {
            p_Set(s_Args.NewValue);
            s_Value.Text = "×" + s_Args.NewValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            m_Changed();
        };
        s_Row.Children.Add(s_Slider);

        return s_Row;
    }

    private UIElement ShortcutRow(string p_Kind)
    {
        var s_Row = new DockPanel { Margin = new Thickness(0, 1, 8, 1) };

        s_Row.Children.Add(new TextBlock
        {
            // ⛔ The row must name the node the key actually PLACES, not our own label for it: while the UDK
            // palette is on, T drops a TextureSample and L a LinearInterpolate (GraphCanvas resolves the twin),
            // so a list reading "Texture"/"Lerp" promises one node and hands over another.
            Text = Graph.Palette.PlacedTitle(p_Kind, m_Settings.UdkNodeStyle),
            Foreground = s_Text,
            Width = 110,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var s_Button = MakeButton(CurrentKeyOf(p_Kind).ToString());
        s_Button.Tag = p_Kind;
        s_Button.MinWidth = 52;
        s_Button.Click += (_, _) =>
        {
            // Clicking a second row while one capture is pending moves the capture, it does not stack.
            if (m_Capturing != null)
                m_Capturing.Content = CurrentKeyOf((string) m_Capturing.Tag).ToString();

            m_Capturing = s_Button;
            s_Button.Content = "Press a key…";
        };
        s_Row.Children.Add(s_Button);

        return s_Row;
    }

    private Key CurrentKeyOf(string p_Kind) =>
        m_Settings.NodeShortcuts.TryGetValue(p_Kind, out var s_Name) &&
        Enum.TryParse<Key>(s_Name, true, out var s_Key)
            ? s_Key
            : GraphCanvas.DefaultShortcuts[p_Kind];

    private void OnCaptureKey(object p_Sender, KeyEventArgs p_Args)
    {
        if (m_Capturing == null)
            return;

        p_Args.Handled = true;
        var s_Kind = (string) m_Capturing.Tag;
        var s_Key = p_Args.Key == Key.System ? p_Args.SystemKey : p_Args.Key;

        if (s_Key == Key.Escape)
        {
            m_Capturing.Content = CurrentKeyOf(s_Kind).ToString();
            m_Capturing = null;
            return;
        }

        // Only plain letters and digits place nodes comfortably while the mouse aims; anything with a
        // modifier or a reserved role is refused rather than silently colliding with an editor command.
        var s_Letter = s_Key is >= Key.A and <= Key.Z || s_Key is >= Key.D0 and <= Key.D9;
        var s_Taken = GraphCanvas.DefaultShortcuts.Keys
            .Where(p_K => p_K != s_Kind)
            .Any(p_K => CurrentKeyOf(p_K) == s_Key);

        if (!s_Letter || s_ReservedKeys.Contains(s_Key) || s_Taken ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            m_Capturing.Content = "In use / reserved";
            return;
        }

        if (s_Key == GraphCanvas.DefaultShortcuts[s_Kind])
            m_Settings.NodeShortcuts.Remove(s_Kind);
        else
            m_Settings.NodeShortcuts[s_Kind] = s_Key.ToString();

        m_Capturing.Content = s_Key.ToString();
        m_Capturing = null;
        m_Changed();
    }

    private static Button MakeButton(string p_Content) => new()
    {
        Content = p_Content,
        Background = new SolidColorBrush(Color.FromRgb(63, 63, 70)),
        Foreground = s_Text,
        BorderBrush = s_Border,
        Padding = new Thickness(8, 3, 8, 3),
    };
}
