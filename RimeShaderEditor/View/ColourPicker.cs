using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RimeShaderEditor.View;

/// <summary>
/// An in-house colour picker, built because the Win32 <c>ColorDialog</c> is the wrong tool for a shader value in
/// two ways. The reported one: seeded with a colour that happens to be in its basic grid (white, which is the
/// default of every Color node), moving the crosshair and pressing OK returns the still-selected swatch rather
/// than the crosshair, so picking on a fresh node looked like it did nothing. The structural one: it has no alpha
/// and clamps to 0..1, while an emissive colour above 1 is legitimate — see the Intensity field below.
/// </summary>
public sealed class ColourPicker : Window
{
    private const double c_Square = 208;
    private const double c_Bar = 22;

    private static readonly Brush s_Panel = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
    private static readonly Brush s_Field = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush s_Text = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush s_Edge = new SolidColorBrush(Color.FromRgb(0x54, 0x54, 0x5A));

    private double m_Hue;
    private double m_Saturation;
    private double m_Value = 1;
    private double m_Alpha = 1;
    private double m_Intensity = 1;

    private readonly Rectangle m_HueBase;
    private readonly Canvas m_SquareMarks;
    private readonly Canvas m_BarMarks;
    private readonly Rectangle m_Swatch;
    private readonly TextBox m_IntensityBox;
    private readonly TextBox m_AlphaBox;
    private readonly TextBlock m_Readout;

    private ColourPicker(float[] p_Seed)
    {
        Title = "Pick a colour";
        Background = s_Panel;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // A value above 1 is carried in Intensity so the square stays a 0..1 hue/sat/val picker and the HDR part
        // survives the round trip instead of being clamped away.
        var s_Peak = Math.Max(p_Seed[0], Math.Max(p_Seed[1], p_Seed[2]));
        m_Intensity = s_Peak > 1.0 ? s_Peak : 1.0;
        m_Alpha = p_Seed[3];

        var s_Normalised = new[]
        {
            (float) (p_Seed[0] / m_Intensity), (float) (p_Seed[1] / m_Intensity), (float) (p_Seed[2] / m_Intensity),
        };

        (m_Hue, m_Saturation, m_Value) = RgbToHsv(s_Normalised[0], s_Normalised[1], s_Normalised[2]);

        m_HueBase = new Rectangle { Width = c_Square, Height = c_Square };
        m_SquareMarks = new Canvas { Width = c_Square, Height = c_Square, Background = Brushes.Transparent };
        m_BarMarks = new Canvas { Width = c_Bar, Height = c_Square, Background = Brushes.Transparent };
        m_Swatch = new Rectangle { Height = 26, Stroke = Brushes.Black, StrokeThickness = 1 };
        m_IntensityBox = Field(m_Intensity.ToString("0.0###", CultureInfo.InvariantCulture));
        m_AlphaBox = Field(m_Alpha.ToString("0.0###", CultureInfo.InvariantCulture));
        m_Readout = new TextBlock
        {
            Foreground = s_Text, FontFamily = new FontFamily("Consolas"), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };

        Content = BuildLayout();
        Repaint();
    }

    /// <summary>The chosen value as four floats; only meaningful when <see cref="Pick"/> returned non-null.</summary>
    private float[] Chosen()
    {
        var s_Rgb = HsvToRgb(m_Hue, m_Saturation, m_Value);
        return new[]
        {
            (float) (s_Rgb[0] * m_Intensity), (float) (s_Rgb[1] * m_Intensity), (float) (s_Rgb[2] * m_Intensity),
            (float) m_Alpha,
        };
    }

    /// <summary>Opens the picker modally. Returns null when cancelled, so "no change" is distinguishable.</summary>
    public static float[]? Pick(Window? p_Owner, float[] p_Seed)
    {
        var s_Picker = new ColourPicker(p_Seed);
        if (p_Owner != null)
            s_Picker.Owner = p_Owner;

        return s_Picker.ShowDialog() == true ? s_Picker.Chosen() : null;
    }

    private FrameworkElement BuildLayout()
    {
        // Hue base, then white-to-transparent across and transparent-to-black down: the standard saturation/value
        // square, with no bitmap to generate.
        var s_Square = new Grid { Width = c_Square, Height = c_Square };
        s_Square.Children.Add(m_HueBase);
        s_Square.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.White, Colors.Transparent, new Point(0, 0), new Point(1, 0)),
        });
        s_Square.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(Colors.Transparent, Colors.Black, new Point(0, 0), new Point(0, 1)),
        });
        s_Square.Children.Add(m_SquareMarks);

        var s_Bar = new Grid { Width = c_Bar, Height = c_Square, Margin = new Thickness(8, 0, 0, 0) };
        var s_HueStops = new GradientStopCollection();
        for (var i = 0; i <= 6; i++)
        {
            var s_Rgb = HsvToRgb(i * 60.0, 1, 1);
            s_HueStops.Add(new GradientStop(
                Color.FromRgb(Byte(s_Rgb[0]), Byte(s_Rgb[1]), Byte(s_Rgb[2])), i / 6.0));
        }

        s_Bar.Children.Add(new Rectangle
        {
            Fill = new LinearGradientBrush(s_HueStops, new Point(0, 0), new Point(0, 1)),
        });
        s_Bar.Children.Add(m_BarMarks);

        m_SquareMarks.MouseLeftButtonDown += (_, p_Args) => Grab(m_SquareMarks, p_Args, true);
        m_SquareMarks.MouseMove += (_, p_Args) => Track(m_SquareMarks, p_Args, true);
        m_SquareMarks.MouseLeftButtonUp += (_, _) => m_SquareMarks.ReleaseMouseCapture();
        m_BarMarks.MouseLeftButtonDown += (_, p_Args) => Grab(m_BarMarks, p_Args, false);
        m_BarMarks.MouseMove += (_, p_Args) => Track(m_BarMarks, p_Args, false);
        m_BarMarks.MouseLeftButtonUp += (_, _) => m_BarMarks.ReleaseMouseCapture();

        var s_Top = new StackPanel { Orientation = Orientation.Horizontal };
        s_Top.Children.Add(s_Square);
        s_Top.Children.Add(s_Bar);

        var s_Numbers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        s_Numbers.Children.Add(Caption("Intensity"));
        s_Numbers.Children.Add(m_IntensityBox);
        s_Numbers.Children.Add(Caption("Alpha"));
        s_Numbers.Children.Add(m_AlphaBox);

        m_IntensityBox.TextChanged += (_, _) => TakeNumbers();
        m_AlphaBox.TextChanged += (_, _) => TakeNumbers();

        var s_SwatchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        m_Swatch.Width = 84;
        s_SwatchRow.Children.Add(m_Swatch);
        s_SwatchRow.Children.Add(m_Readout);

        var s_Ok = new Button { Content = "OK", Width = 82, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var s_Cancel = new Button { Content = "Cancel", Width = 82, IsCancel = true };
        s_Ok.Click += (_, _) => { DialogResult = true; };

        var s_Buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        s_Buttons.Children.Add(s_Ok);
        s_Buttons.Children.Add(s_Cancel);

        // The panel carries the background as well as the window, so a snapshot of the content alone is faithful.
        var s_Root = new StackPanel { Margin = new Thickness(12), Background = s_Panel };
        s_Root.Children.Add(s_Top);
        s_Root.Children.Add(s_Numbers);
        s_Root.Children.Add(s_SwatchRow);
        s_Root.Children.Add(new TextBlock
        {
            Text = "Intensity scales the picked colour, so emissive values above 1 are reachable.",
            Foreground = s_Text, Opacity = 0.7, FontSize = 10, Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap, MaxWidth = c_Square + c_Bar + 8,
        });
        s_Root.Children.Add(s_Buttons);
        return s_Root;
    }

    private void Grab(IInputElement p_Target, MouseButtonEventArgs p_Args, bool p_IsSquare)
    {
        p_Target.CaptureMouse();
        Apply(p_Args.GetPosition(p_Target), p_IsSquare);
    }

    private void Track(IInputElement p_Target, MouseEventArgs p_Args, bool p_IsSquare)
    {
        if (p_Args.LeftButton == MouseButtonState.Pressed && Mouse.Captured == p_Target)
            Apply(p_Args.GetPosition(p_Target), p_IsSquare);
    }

    private void Apply(Point p_Point, bool p_IsSquare)
    {
        if (p_IsSquare)
        {
            m_Saturation = Math.Clamp(p_Point.X / c_Square, 0, 1);
            m_Value = Math.Clamp(1 - p_Point.Y / c_Square, 0, 1);
        }
        else
        {
            m_Hue = Math.Clamp(p_Point.Y / c_Square, 0, 1) * 360;
        }

        Repaint();
    }

    private void TakeNumbers()
    {
        if (double.TryParse(m_IntensityBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_I) &&
            s_I > 0)
            m_Intensity = s_I;

        if (double.TryParse(m_AlphaBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s_A))
            m_Alpha = s_A;

        Repaint();
    }

    private void Repaint()
    {
        var s_Pure = HsvToRgb(m_Hue, 1, 1);
        m_HueBase.Fill = new SolidColorBrush(Color.FromRgb(Byte(s_Pure[0]), Byte(s_Pure[1]), Byte(s_Pure[2])));

        m_SquareMarks.Children.Clear();
        var s_Ring = new Ellipse
        {
            Width = 11, Height = 11, Stroke = Brushes.White, StrokeThickness = 2,
        };
        Canvas.SetLeft(s_Ring, m_Saturation * c_Square - 5.5);
        Canvas.SetTop(s_Ring, (1 - m_Value) * c_Square - 5.5);
        m_SquareMarks.Children.Add(s_Ring);

        m_BarMarks.Children.Clear();
        var s_Notch = new Rectangle
        {
            Width = c_Bar, Height = 3, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1,
        };
        Canvas.SetTop(s_Notch, m_Hue / 360 * c_Square - 1.5);
        m_BarMarks.Children.Add(s_Notch);

        var s_Chosen = Chosen();
        m_Swatch.Fill = new SolidColorBrush(Color.FromRgb(
            Byte(s_Chosen[0]), Byte(s_Chosen[1]), Byte(s_Chosen[2])));

        m_Readout.Text = string.Join(", ",
            Array.ConvertAll(s_Chosen, p_V => p_V.ToString("0.0###", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Lays out and renders the picker with no window shown, so what it looks like can be looked at rather than
    /// assumed. A gradient pointing the wrong way or a marker landing off the square is invisible to a value test.
    /// </summary>
    internal static System.Windows.Media.Imaging.BitmapSource Snapshot(float[] p_Seed)
    {
        var s_Content = (FrameworkElement) new ColourPicker(p_Seed).Content;
        s_Content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        s_Content.Arrange(new Rect(new Point(0, 0), s_Content.DesiredSize));
        s_Content.UpdateLayout();

        // DesiredSize, not ActualWidth/Height: those exclude the outer Margin and the shot comes out clipped.
        var s_Target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int) Math.Ceiling(s_Content.DesiredSize.Width), (int) Math.Ceiling(s_Content.DesiredSize.Height),
            96, 96, PixelFormats.Pbgra32);

        s_Target.Render(s_Content);
        return s_Target;
    }

    /// <summary>
    /// Round-trips colours through the real seeding path (RGB to intensity + HSV in the constructor) and back out
    /// through <see cref="Chosen"/>. This replaced a dialog that was returning the wrong value, so its own
    /// conversion is checked rather than assumed — the HDR case in particular, since surviving values above 1 is
    /// the whole reason for the Intensity split.
    /// </summary>
    internal static bool RoundTripSelfTest(Action<string> p_Log)
    {
        float[][] s_Cases =
        {
            new[] { 1f, 1f, 1f, 1f },
            new[] { 0f, 0f, 0f, 1f },
            new[] { 1f, 0f, 0f, 1f },
            new[] { 0f, 1f, 0f, 0f },
            new[] { 0.25f, 0.5f, 0.75f, 0.5f },
            new[] { 2.5f, 0.5f, 0f, 1f },
            new[] { 8f, 8f, 8f, 1f },
        };

        var s_Ok = true;
        foreach (var s_Case in s_Cases)
        {
            var s_Back = new ColourPicker(s_Case).Chosen();
            var s_Worst = 0f;
            for (var i = 0; i < 4; i++)
                s_Worst = Math.Max(s_Worst, Math.Abs(s_Back[i] - s_Case[i]));

            var s_Pass = s_Worst < 1e-4f;
            s_Ok &= s_Pass;
            p_Log($"  {(s_Pass ? "ok  " : "FAIL")} {Show(s_Case)} -> {Show(s_Back)} (worst {s_Worst:0.000000})");
        }

        return s_Ok;
    }

    private static string Show(float[] p_Values) => string.Join(",",
        Array.ConvertAll(p_Values, p_V => p_V.ToString("0.0###", CultureInfo.InvariantCulture)));

    private static byte Byte(double p_V) => (byte) Math.Clamp(p_V * 255.0, 0, 255);

    private static TextBlock Caption(string p_Text) => new()
    {
        Text = p_Text, Foreground = s_Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 5, 0),
    };

    private static TextBox Field(string p_Value) => new()
    {
        Text = p_Value, Width = 60, Background = s_Field, Foreground = s_Text, BorderBrush = s_Edge,
        Margin = new Thickness(0, 0, 14, 0),
    };

    private static double[] HsvToRgb(double p_H, double p_S, double p_V)
    {
        var s_H = ((p_H % 360) + 360) % 360;
        var s_C = p_V * p_S;
        var s_X = s_C * (1 - Math.Abs(s_H / 60.0 % 2 - 1));
        var s_M = p_V - s_C;

        var s_Rgb = ((int) (s_H / 60)) switch
        {
            0 => new[] { s_C, s_X, 0.0 },
            1 => new[] { s_X, s_C, 0.0 },
            2 => new[] { 0.0, s_C, s_X },
            3 => new[] { 0.0, s_X, s_C },
            4 => new[] { s_X, 0.0, s_C },
            _ => new[] { s_C, 0.0, s_X },
        };

        return new[] { s_Rgb[0] + s_M, s_Rgb[1] + s_M, s_Rgb[2] + s_M };
    }

    private static (double Hue, double Saturation, double Value) RgbToHsv(float p_R, float p_G, float p_B)
    {
        double s_Max = Math.Max(p_R, Math.Max(p_G, p_B));
        double s_Min = Math.Min(p_R, Math.Min(p_G, p_B));
        var s_Delta = s_Max - s_Min;

        var s_Hue = 0.0;
        if (s_Delta > 1e-5)
        {
            if (Math.Abs(s_Max - p_R) < 1e-6)
                s_Hue = 60 * ((p_G - p_B) / s_Delta % 6);
            else if (Math.Abs(s_Max - p_G) < 1e-6)
                s_Hue = 60 * ((p_B - p_R) / s_Delta + 2);
            else
                s_Hue = 60 * ((p_R - p_G) / s_Delta + 4);
        }

        if (s_Hue < 0)
            s_Hue += 360;

        return (s_Hue, s_Max <= 0 ? 0 : s_Delta / s_Max, s_Max);
    }
}
