using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RimeShaderEditor.View;

/// <summary>
/// The two folders the editor cannot work without, asked once, the first time it runs.
///
/// ⛔ IT ASKS EVEN THOUGH BOTH HAVE DEFAULTS, and that is the point. The game folder is guessed from the
/// installer's registry key and the cache lands in Documents — both silently. Someone with the game
/// elsewhere, or who would rather not have gigabytes of dumped textures under Documents, only found out by
/// noticing it afterwards. Shown once, prefilled, one click to accept: the guess becomes an answer.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(45, 45, 48));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private static readonly Brush s_Dim = new SolidColorBrush(Color.FromRgb(150, 150, 150));
    private static readonly Brush s_Border = new SolidColorBrush(Color.FromRgb(84, 84, 90));
    private static readonly Brush s_Warn = new SolidColorBrush(Color.FromRgb(255, 192, 96));

    private readonly TextBox m_Game;
    private readonly TextBox m_Cache;
    private readonly TextBlock m_GameNote;

    public FirstRunWindow(EditorSettings p_Settings)
    {
        Title = "Welcome";
        Background = s_Panel;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        var s_Root = new StackPanel { Margin = new Thickness(16) };

        s_Root.Children.Add(new TextBlock
        {
            Text = "Where things live",
            Foreground = s_Text, FontWeight = FontWeights.Bold, FontSize = 15,
            Margin = new Thickness(0, 0, 0, 4),
        });

        s_Root.Children.Add(new TextBlock
        {
            Text = "Both are filled in with a guess. Change them if the guess is wrong — they can be " +
                   "changed later in Settings.",
            Foreground = s_Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        m_Game = Field(p_Settings.GamePath);
        m_GameNote = new TextBlock
        {
            Foreground = s_Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 10), Visibility = Visibility.Collapsed,
        };

        s_Root.Children.Add(Row("Game folder",
            "The install the editor reads shaders, textures and meshes out of. Nothing in it is ever " +
            "written to.", m_Game, "Choose the game folder"));
        s_Root.Children.Add(m_GameNote);

        m_Cache = Field(p_Settings.OutputFolder);
        s_Root.Children.Add(Row("Cache and output folder",
            "Everything the editor dumps out of the game and everything it builds: texture thumbnails, mesh " +
            "dumps, shader lists, saved graphs and the mods it bakes. It grows to gigabytes.",
            m_Cache, "Choose the cache folder"));

        var s_Buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var s_Ok = new Button { Content = "Continue", Padding = new Thickness(18, 3, 18, 3), IsDefault = true };
        s_Ok.Click += (_, _) =>
        {
            // Created here rather than on first use: the editor writes into it from several places, and a
            // path that does not exist yet reads as "the cache is empty" everywhere it is checked.
            try { Directory.CreateDirectory(m_Cache.Text.Trim()); }
            catch (Exception) { /* an unwritable choice is reported by the first thing that needs it */ }

            DialogResult = true;
        };

        s_Buttons.Children.Add(s_Ok);
        s_Root.Children.Add(s_Buttons);

        m_Game.TextChanged += (_, _) => Revalidate();
        Revalidate();

        Content = s_Root;
    }

    public string GamePath => m_Game.Text.Trim();
    public string OutputFolder => m_Cache.Text.Trim();

    /// <summary>
    /// Says what is wrong without blocking. A folder that does not look like the game is USUALLY a mistake,
    /// but the editor already survives an empty one (it just cannot read from the game), so refusing to let
    /// the user past a warning would be the dialog claiming to know his machine better than he does.
    /// </summary>
    private void Revalidate()
    {
        var s_Path = m_Game.Text.Trim();
        var s_Bad = s_Path.Length > 0 && !EditorSettings.LooksLikeGame(s_Path);

        m_GameNote.Text = s_Path.Length == 0
            ? "Empty: shader lists, textures and mesh previews stay unavailable until this is set."
            : "That folder has no bf3.exe and no Data folder — check it is the install root.";

        m_GameNote.Visibility = s_Bad || s_Path.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private TextBox Field(string p_Value) => new()
    {
        Text = p_Value, Background = s_Field, Foreground = s_Text, BorderBrush = s_Border,
        BorderThickness = new Thickness(1), Padding = new Thickness(4, 3, 4, 3),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private UIElement Row(string p_Label, string p_Help, TextBox p_Box, string p_BrowseTitle)
    {
        var s_Panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        s_Panel.Children.Add(new TextBlock
        {
            Text = p_Label, Foreground = s_Text, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        s_Panel.Children.Add(new TextBlock
        {
            Text = p_Help, Foreground = s_Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        var s_Line = new DockPanel();
        var s_Browse = new Button
        {
            Content = "Browse…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0),
        };

        s_Browse.Click += (_, _) =>
        {
            var s_Dialog = new Microsoft.Win32.OpenFolderDialog { Title = p_BrowseTitle };
            if (Directory.Exists(p_Box.Text.Trim()))
                s_Dialog.InitialDirectory = p_Box.Text.Trim();

            if (s_Dialog.ShowDialog(this) == true)
                p_Box.Text = s_Dialog.FolderName;
        };

        DockPanel.SetDock(s_Browse, Dock.Right);
        s_Line.Children.Add(s_Browse);
        s_Line.Children.Add(p_Box);
        s_Panel.Children.Add(s_Line);

        return s_Panel;
    }
}
