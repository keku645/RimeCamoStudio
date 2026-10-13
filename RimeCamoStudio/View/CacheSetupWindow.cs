using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RimeCamoStudio.Cache;

namespace RimeCamoStudio.View;

/// <summary>
/// "Prepare from your game": the window that fills the cache out of the user's own Battlefield 3, step by step (CacheSetup) — shown
/// when the cache is not ready, before the studio opens. Each step's output scrolls by; a step can be cancelled and the preparation
/// resumed later (the steps already done stay done). Nothing of the game ships with the studio, so this is where it comes from.
/// </summary>
public sealed class CacheSetupWindow : Window
{
    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));
    private static readonly Brush s_Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x60));
    private static readonly Brush s_Good = new SolidColorBrush(Color.FromRgb(0x8C, 0xD9, 0x8C));

    private readonly string m_Game;
    private readonly string m_Cache;
    private readonly List<(CacheSetup.Step Step, TextBlock Mark)> m_Rows = new();
    private readonly TextBox m_Log;
    private readonly TextBlock m_Status;
    private readonly Button m_Start;
    private readonly Button m_Close;
    private readonly Queue<string> m_Lines = new();
    private CancellationTokenSource? m_Cancel;

    /// <summary>Whether every step is done — what the studio asks when the window closes.</summary>
    public bool Ready => CacheSetup.IsComplete(m_Cache);

    public CacheSetupWindow(string p_Game, string p_Cache)
    {
        m_Game = p_Game;
        m_Cache = p_Cache;

        Title = "Prepare Camo Studio from your game";
        Background = s_Panel;
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var s_Root = new Grid { Margin = new Thickness(14), Background = s_Panel };
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        s_Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var s_Head = new StackPanel();
        s_Head.Children.Add(Caption("Your game, read once", 14, 1.0, FontWeights.Bold));
        s_Head.Children.Add(Caption(
            "Camo Studio carries nothing of Battlefield 3. Before it can show a weapon, a vehicle or a soldier it reads them out of YOUR " +
            "copy of the game, once, into the cache folder: models, materials, textures and shaders. The game itself is only read, " +
            "never written to. The first time takes a while (count on an hour or more) and a few gigabytes of disk; you can stop and " +
            "come back to it later — what is done stays done.", 11, 0.8));

        var s_GameOk = Settings.LooksLikeGame(p_Game);
        s_Head.Children.Add(Caption($"Game folder:   {(p_Game.Length > 0 ? p_Game : "(not set)")}", 11, 1.0, FontWeights.Normal, 8));
        if (!s_GameOk)
            s_Head.Children.Add(new TextBlock
            {
                Text = "That folder does not look like Battlefield 3 (no bf3.exe and no Data folder). Close this window, set the game " +
                       "folder in Settings ▸ Folders, and open Camo Studio again.",
                Foreground = s_Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2),
            });
        s_Head.Children.Add(Caption($"Cache folder:  {p_Cache}", 11, 1.0, FontWeights.Normal));
        s_Head.Children.Add(Caption("(both are the ones chosen in Settings ▸ Folders)", 10, 0.6));

        var s_Steps = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        foreach (var s_Step in CacheSetup.Steps)
        {
            var s_Mark = new TextBlock { Width = 92, Foreground = s_Text, FontSize = 11 };
            var s_Line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            DockPanel.SetDock(s_Mark, Dock.Left);
            s_Line.Children.Add(s_Mark);
            s_Line.Children.Add(new TextBlock
            {
                Foreground = s_Text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Inlines = { new System.Windows.Documents.Run(s_Step.Title) { FontWeight = FontWeights.Bold },
                            new System.Windows.Documents.Run("  —  " + s_Step.Detail) { Foreground = Brushes.Gray } },
            });
            s_Steps.Children.Add(s_Line);
            m_Rows.Add((s_Step, s_Mark));
        }

        s_Head.Children.Add(s_Steps);
        Grid.SetRow(s_Head, 0);
        s_Root.Children.Add(s_Head);

        m_Log = new TextBox
        {
            IsReadOnly = true, Background = s_Field, Foreground = s_Text, BorderBrush = s_Edge, FontFamily = new FontFamily("Consolas"),
            FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };
        Grid.SetRow(m_Log, 1);
        s_Root.Children.Add(m_Log);

        var s_Tail = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        m_Start = new Button { Content = "Start", Width = 100, IsDefault = true, IsEnabled = s_GameOk, Margin = new Thickness(6, 0, 0, 0) };
        m_Close = new Button { Content = "Later", Width = 100, Margin = new Thickness(6, 0, 0, 0) };
        m_Start.Click += async (_, _) => await RunAllAsync();
        // (while a step runs the button stops it — the window stays; otherwise it closes, and the studio opens with what is cached)
        m_Close.Click += (_, _) =>
        {
            if (m_Cancel != null)
                m_Cancel.Cancel();
            else
                Close();
        };
        var s_Buttons = new StackPanel { Orientation = Orientation.Horizontal };
        s_Buttons.Children.Add(m_Start);
        s_Buttons.Children.Add(m_Close);
        DockPanel.SetDock(s_Buttons, Dock.Right);
        s_Tail.Children.Add(s_Buttons);
        m_Status = new TextBlock { Foreground = s_Warn, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        s_Tail.Children.Add(m_Status);
        Grid.SetRow(s_Tail, 2);
        s_Root.Children.Add(s_Tail);

        Content = s_Root;
        Closing += (_, p_Args) =>
        {
            // closing while a step runs stops it (what it finished stays done)
            if (m_Cancel != null)
                m_Cancel.Cancel();
        };

        Refresh(null);
    }

    /// <summary>Puts each step's state on its row and the summary under the log.</summary>
    private void Refresh(CacheSetup.Step? p_Running)
    {
        foreach (var (s_Step, s_Mark) in m_Rows)
        {
            var s_Done = CacheSetup.IsDone(m_Cache, s_Step);
            s_Mark.Text = s_Done ? "✓ done" : ReferenceEquals(s_Step, p_Running) ? "▶ running…" : "· to do";
            s_Mark.Foreground = s_Done ? s_Good : ReferenceEquals(s_Step, p_Running) ? s_Warn : s_Text;
        }

        var s_Left = m_Rows.Count(p_R => !CacheSetup.IsDone(m_Cache, p_R.Step));
        if (p_Running == null)
            m_Status.Text = s_Left == 0 ? "Ready: everything is in the cache." : $"{s_Left} step(s) to do.";
        m_Status.Foreground = s_Left == 0 ? s_Good : s_Warn;
        m_Close.Content = s_Left == 0 ? "Open Camo Studio" : m_Cancel != null ? "Stop" : "Later";
    }

    /// <summary>Each output line also goes here (a seam's console), as the window shows it.</summary>
    internal Action<string>? Echo { get; set; }

    /// <summary>The window's form rendered without showing it, for a seam to look at.</summary>
    internal System.Windows.Media.Imaging.BitmapSource Render()
    {
        var s_Content = (FrameworkElement) Content;
        s_Content.Measure(new Size(Width, Height));
        s_Content.Arrange(new Rect(0, 0, Width, Height));
        s_Content.UpdateLayout();
        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap((int) Width, (int) Height, 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Content);
        return s_Target;
    }

    private void Append(string p_Line)
    {
        Echo?.Invoke(p_Line);
        // the last lines only: a step prints thousands, and the window stays light
        m_Lines.Enqueue(p_Line);
        while (m_Lines.Count > 400)
            m_Lines.Dequeue();
        m_Log.Text = string.Join(Environment.NewLine, m_Lines);
        m_Log.ScrollToEnd();
    }

    /// <summary>Runs every step not done yet, in order; stops at the first that fails or is cancelled. True when the cache is complete.</summary>
    public async Task<bool> RunAllAsync()
    {
        if (m_Cancel != null)
            return false;

        m_Cancel = new CancellationTokenSource();
        m_Start.IsEnabled = false;
        try
        {
            foreach (var s_Step in CacheSetup.Steps.Where(p_S => !CacheSetup.IsDone(m_Cache, p_S)))
            {
                Refresh(s_Step);
                var s_Clock = Stopwatch.StartNew();
                m_Status.Text = $"{s_Step.Title}: running…";
                Append($"==== {s_Step.Title} ({string.Join(" ", s_Step.Arguments)})");
                var s_Ok = await CacheSetup.RunAsync(s_Step, m_Game, m_Cache,
                    p_Line => Dispatcher.BeginInvoke(new Action(() => Append(p_Line))), m_Cancel.Token);
                Append($"==== {s_Step.Title}: {(s_Ok ? "done" : m_Cancel.IsCancellationRequested ? "stopped" : "FAILED")} after {s_Clock.Elapsed:h\\:mm\\:ss}");
                if (!s_Ok)
                {
                    m_Status.Text = m_Cancel.IsCancellationRequested
                        ? $"Stopped during '{s_Step.Title}'. Start again to go on from there."
                        : $"'{s_Step.Title}' did not finish — read the lines above. Start again to retry it.";
                    return false;
                }
            }

            return CacheSetup.IsComplete(m_Cache);
        }
        finally
        {
            m_Cancel = null;
            m_Start.IsEnabled = Settings.LooksLikeGame(m_Game);
            Refresh(null);
        }
    }

    private static TextBlock Caption(string p_Text, double p_Size, double p_Opacity = 1.0, FontWeight? p_Weight = null, double p_Top = 0) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = p_Size, Opacity = p_Opacity, FontWeight = p_Weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, p_Top, 0, 3),
    };
}
