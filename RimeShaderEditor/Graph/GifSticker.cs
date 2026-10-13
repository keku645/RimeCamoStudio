using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeShaderEditor.Graph;

/// <summary>
/// An animated GIF as a sticker: its frames composited the way a viewer shows them (sub-rectangles,
/// disposal, transparency), each with the delay the file gives it, and the FLIPBOOK the shader plays them
/// from — a sheet of frames plus a timeline. The engine has no video texture (measured, 2026-09-12: a
/// MovieTextureAsset is not a TextureBaseAsset and no material can bind one), so a GIF ships as a sheet.
///
/// The timeline is the file's own: every frame's delay, in order, never resampled — the sticker plays for
/// exactly as long as the GIF does, at the same speed (it must look the same as in the original GIF).
/// Consecutive frames that are byte-identical are stored once with their delays added, which
/// changes nothing on screen and keeps the sheet small.
/// </summary>
public sealed class GifClip
{
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>The composited frames, Bgra32, straight alpha, all Width × Height.</summary>
    public List<BitmapSource> Frames { get; } = new();

    /// <summary>Each frame's delay in milliseconds, the viewer's reading of the file (a 0 or 1 hundredth = 100 ms).</summary>
    public List<int> DelaysMs { get; } = new();

    /// <summary>How many frames the file holds before identical neighbours are merged.</summary>
    public int SourceFrames { get; init; }

    public double TotalSeconds => DelaysMs.Sum() / 1000.0;

    public static bool IsGif(string p_Path) => p_Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a GIF into composited frames. Null when the file is not a GIF or cannot be read; a GIF with a
    /// single frame decodes fine (one frame, one delay) — the caller decides whether that is "animated".
    /// </summary>
    public static GifClip? Decode(string p_Path)
    {
        if (!IsGif(p_Path) || !File.Exists(p_Path))
            return null;

        try
        {
            GifBitmapDecoder s_Decoder;
            using (var s_Stream = new FileStream(p_Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                s_Decoder = new GifBitmapDecoder(s_Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            if (s_Decoder.Frames.Count == 0)
                return null;

            // The logical screen: the canvas every frame is placed on. Missing metadata = the first frame's size.
            var s_Global = s_Decoder.Metadata as BitmapMetadata;
            var s_Width = QueryInt(s_Global, "/logscrdesc/Width") ?? s_Decoder.Frames[0].PixelWidth;
            var s_Height = QueryInt(s_Global, "/logscrdesc/Height") ?? s_Decoder.Frames[0].PixelHeight;
            if (s_Width <= 0 || s_Height <= 0)
                return null;

            var s_Clip = new GifClip { Width = s_Width, Height = s_Height, SourceFrames = s_Decoder.Frames.Count };
            var s_Canvas = new byte[s_Width * s_Height * 4];
            byte[]? s_Previous = null;
            var s_LastRect = new Int32Rect(0, 0, 0, 0);
            var s_LastDisposal = 0;
            byte[]? s_LastPixels = null;
            var s_LastDelay = -1;

            foreach (var s_Frame in s_Decoder.Frames)
            {
                var s_Meta = s_Frame.Metadata as BitmapMetadata;
                var s_Left = QueryInt(s_Meta, "/imgdesc/Left") ?? 0;
                var s_Top = QueryInt(s_Meta, "/imgdesc/Top") ?? 0;
                var s_Delay = QueryInt(s_Meta, "/grctlext/Delay") ?? 0;    // hundredths of a second
                var s_Disposal = QueryInt(s_Meta, "/grctlext/Disposal") ?? 0;

                // What the previous frame asked to happen after it was shown.
                if (s_LastDisposal == 2)
                    Clear(s_Canvas, s_Width, s_LastRect);
                else if (s_LastDisposal == 3 && s_Previous != null)
                    Array.Copy(s_Previous, s_Canvas, s_Canvas.Length);

                if (s_Disposal == 3)
                    s_Previous = (byte[]) s_Canvas.Clone();

                // The frame's own pixels over the canvas: a GIF pixel is opaque or absent, so an opaque one
                // replaces and a transparent one leaves what was there.
                var s_Converted = new FormatConvertedBitmap(s_Frame, PixelFormats.Bgra32, null, 0);
                var s_FrameWidth = s_Converted.PixelWidth;
                var s_FrameHeight = s_Converted.PixelHeight;
                var s_Pixels = new byte[s_FrameWidth * s_FrameHeight * 4];
                s_Converted.CopyPixels(s_Pixels, s_FrameWidth * 4, 0);
                for (var y = 0; y < s_FrameHeight; y++)
                {
                    var s_CanvasY = s_Top + y;
                    if (s_CanvasY < 0 || s_CanvasY >= s_Height)
                        continue;

                    for (var x = 0; x < s_FrameWidth; x++)
                    {
                        var s_CanvasX = s_Left + x;
                        if (s_CanvasX < 0 || s_CanvasX >= s_Width)
                            continue;

                        var s_Source = (y * s_FrameWidth + x) * 4;
                        if (s_Pixels[s_Source + 3] == 0)
                            continue;

                        var s_Target = (s_CanvasY * s_Width + s_CanvasX) * 4;
                        s_Canvas[s_Target] = s_Pixels[s_Source];
                        s_Canvas[s_Target + 1] = s_Pixels[s_Source + 1];
                        s_Canvas[s_Target + 2] = s_Pixels[s_Source + 2];
                        s_Canvas[s_Target + 3] = 255;
                    }
                }

                // A delay of 0 or 1 hundredth is shown as 100 ms by every viewer (browsers included); the
                // file's own number otherwise.
                var s_Ms = s_Delay <= 1 ? 100 : s_Delay * 10;

                // Identical to the frame before: its time is added to that one, nothing else changes on screen.
                if (s_LastPixels != null && s_LastPixels.AsSpan().SequenceEqual(s_Canvas))
                {
                    s_Clip.DelaysMs[^1] += s_Ms;
                }
                else
                {
                    var s_Shot = (byte[]) s_Canvas.Clone();
                    var s_Bitmap = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_Shot, s_Width * 4);
                    s_Bitmap.Freeze();
                    s_Clip.Frames.Add(s_Bitmap);
                    s_Clip.DelaysMs.Add(s_Ms);
                    s_LastPixels = s_Shot;
                }

                s_LastRect = new Int32Rect(s_Left, s_Top, s_FrameWidth, s_FrameHeight);
                s_LastDisposal = s_Disposal;
                s_LastDelay = s_Ms;
            }

            return s_Clip.Frames.Count == 0 ? null : s_Clip;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? QueryInt(BitmapMetadata? p_Meta, string p_Query)
    {
        try
        {
            if (p_Meta == null || !p_Meta.ContainsQuery(p_Query))
                return null;

            return p_Meta.GetQuery(p_Query) switch
            {
                ushort s_U16 => s_U16,
                byte s_U8 => s_U8,
                int s_I32 => s_I32,
                uint s_U32 => (int) s_U32,
                short s_I16 => s_I16,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Clear(byte[] p_Canvas, int p_Width, Int32Rect p_Rect)
    {
        var s_Height = p_Canvas.Length / (p_Width * 4);
        for (var y = Math.Max(0, p_Rect.Y); y < Math.Min(s_Height, p_Rect.Y + p_Rect.Height); y++)
        for (var x = Math.Max(0, p_Rect.X); x < Math.Min(p_Width, p_Rect.X + p_Rect.Width); x++)
            Array.Clear(p_Canvas, (y * p_Width + x) * 4, 4);
    }
}

/// <summary>
/// The sheet the shader plays a GIF from: every distinct frame in a grid of equal cells (each inset by a
/// texel of transparent gutter, so the mips never bleed a neighbour in), and the timeline that says which
/// cell is on at a given second. The cell is as large as the frame (up to 512 across), or the largest that
/// lets every frame fit a sheet of up to 4096×2048 — 4096² as the last resort — never a frame dropped,
/// never a delay changed. Frames with identical pixels share one cell wherever they are in the loop.
/// </summary>
public sealed class GifAtlas
{
    public string Path { get; init; } = "";
    public GifClip Clip { get; init; } = null!;
    public BitmapSource Sheet { get; init; } = null!;
    public int Width { get; init; }
    public int Height { get; init; }
    public int CellWidth { get; init; }
    public int CellHeight { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }
    public int Frames => Clip.Frames.Count;

    /// <summary>The cell each frame plays from (frames with identical pixels share one).</summary>
    public int[] CellOf { get; init; } = Array.Empty<int>();

    /// <summary>How many cells the sheet holds — the distinct frames.</summary>
    public int Cells => CellOf.Length == 0 ? 0 : CellOf.Max() + 1;

    /// <summary>
    /// Whether the GIF's alpha channel was ignored: its transparent pixels drawn as opaque black, so the
    /// sticker is the whole frame rectangle (the "ignore alpha channel" option).
    /// </summary>
    public bool Opaque { get; init; }

    public const int Inset = 1;

    /// <summary>The sheet as it is stored: DXT5, 8 bits a texel with its mips.</summary>
    public double Megabytes => Width * (double) Height * 4 / 3 / (1024 * 1024);

    /// <summary>"4096×2048" — for the log and the node.</summary>
    public string Dimensions => $"{Width}×{Height}";

    /// <summary>Whether every frame lasts the same, in which case the shader divides instead of walking a table.</summary>
    public bool Uniform => Clip.DelaysMs.Distinct().Count() == 1;

    /// <summary>The second at which each frame ENDS (cumulative), the last one being the loop's length.</summary>
    public double[] EndTimes
    {
        get
        {
            var s_Ends = new double[Clip.DelaysMs.Count];
            var s_Sum = 0.0;
            for (var i = 0; i < s_Ends.Length; i++)
            {
                s_Sum += Clip.DelaysMs[i] / 1000.0;
                s_Ends[i] = s_Sum;
            }

            return s_Ends;
        }
    }

    public double TotalSeconds => Clip.TotalSeconds;

    /// <summary>The Flipbook node's parameters for this sheet — what the package's shader is compiled with.</summary>
    public Dictionary<string, string> NodeParams()
    {
        var s_Culture = CultureInfo.InvariantCulture;
        var s_Identity = CellOf.Select((p_C, p_I) => p_C == p_I).All(p_Same => p_Same);
        var s_Params = new Dictionary<string, string>
        {
            ["Side"] = Width.ToString(s_Culture),
            ["Height"] = Height.ToString(s_Culture),
            ["CellW"] = CellWidth.ToString(s_Culture),
            ["CellH"] = CellHeight.ToString(s_Culture),
            ["Cols"] = Columns.ToString(s_Culture),
            ["Frames"] = Frames.ToString(s_Culture),
            ["Inset"] = Inset.ToString(s_Culture),
            ["Delay"] = (Clip.DelaysMs[0] / 1000.0).ToString("0.####", s_Culture),
            ["Timeline"] = Uniform ? "" : string.Join(",", EndTimes.Select(p_T => p_T.ToString("0.####", s_Culture))),
            ["Cells"] = s_Identity ? "" : string.Join(",", CellOf.Select(p_C => p_C.ToString(s_Culture))),
        };
        return s_Params;
    }

    /// <summary>The sheets tried, smallest first: square up to 2048, then the wide 4096×2048, then 4096².</summary>
    private static readonly (int Width, int Height)[] s_Sheets = { (1024, 1024), (2048, 1024), (2048, 2048), (4096, 2048), (4096, 4096) };

    /// <summary>
    /// The largest sheet side a package ships: 4096 — a 4096×2048 sheet drew in-game exactly like a 2048²
    /// one (the "VU S4096" test). CAMO_SHEET_MAX lowers it for a bisection round.
    /// </summary>
    public static int MaxSide =>
        int.TryParse(Environment.GetEnvironmentVariable("CAMO_SHEET_MAX"), out var s_Max) && s_Max is 1024 or 2048 or 4096 ? s_Max : 4096;

    /// <summary>
    /// Lays a clip out as a sheet. Null when even 32-texel cells on a 4096² sheet cannot hold every distinct
    /// frame. With p_Opaque the GIF's transparent pixels are drawn as opaque black.
    /// </summary>
    public static GifAtlas? Build(string p_Path, GifClip p_Clip, bool p_Opaque = false)
    {
        // Frames with identical pixels play from one cell: the timeline still names every frame.
        var s_CellOf = new int[p_Clip.Frames.Count];
        var s_Distinct = new List<BitmapSource>();
        var s_ByHash = new Dictionary<string, int>();
        for (var i = 0; i < p_Clip.Frames.Count; i++)
        {
            var s_Frame = p_Clip.Frames[i];
            var s_Bytes = new byte[s_Frame.PixelWidth * s_Frame.PixelHeight * 4];
            s_Frame.CopyPixels(s_Bytes, s_Frame.PixelWidth * 4, 0);
            var s_Hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(s_Bytes));
            if (!s_ByHash.TryGetValue(s_Hash, out var s_Cell))
            {
                s_Cell = s_Distinct.Count;
                s_ByHash[s_Hash] = s_Cell;
                s_Distinct.Add(s_Frame);
            }

            s_CellOf[i] = s_Cell;
        }

        var s_Count = s_Distinct.Count;
        var s_Aspect = (double) p_Clip.Height / p_Clip.Width;

        // Cell widths from the frame's own size (512 at most) down, powers of two; for each, the smallest
        // sheet that holds every cell — the largest cell that fits any sheet wins.
        var s_Widths = new List<int>();
        for (var s_W = 512; s_W >= 32; s_W /= 2)
            if (s_W <= Math.Max(32, p_Clip.Width))
                s_Widths.Add(s_W);

        (int Width, int Height, int CellW, int CellH, int Cols, int Rows)? s_Best = null;
        foreach (var s_CellW in s_Widths)
        {
            var s_CellH = Math.Max(4, (int) Math.Round(s_CellW * s_Aspect));
            foreach (var (s_SheetW, s_SheetH) in s_Sheets)
            {
                if (s_SheetW > MaxSide || s_SheetH > MaxSide)
                    continue;

                var s_Cols = s_SheetW / s_CellW;
                var s_Rows = s_SheetH / s_CellH;
                if (s_Cols * s_Rows >= s_Count)
                {
                    s_Best = (s_SheetW, s_SheetH, s_CellW, s_CellH, s_Cols, (s_Count + s_Cols - 1) / s_Cols);
                    break;
                }
            }

            if (s_Best != null)
                break;
        }

        if (s_Best == null)
            return null;

        var (s_SheetWidth, s_SheetHeight, s_CW, s_CH, s_C, s_R) = s_Best.Value;
        var s_Visual = new DrawingVisual();
        using (var s_Context = s_Visual.RenderOpen())
        {
            for (var i = 0; i < s_Count; i++)
            {
                var s_X = (i % s_C) * s_CW + Inset;
                var s_Y = (i / s_C) * s_CH + Inset;
                // The shader maps coordinate 1/255 to the frame's first texel: the frame is drawn so that
                // 1..255 spans it and 0 falls on the gutter.
                var s_Rect = new Rect(s_X, s_Y, s_CW - 2 * Inset, s_CH - 2 * Inset);
                if (p_Opaque)
                    s_Context.DrawRectangle(Brushes.Black, null, s_Rect);
                s_Context.DrawImage(s_Distinct[i], s_Rect);
            }
        }

        var s_Target = new RenderTargetBitmap(s_SheetWidth, s_SheetHeight, 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Visual);

        // Straight alpha on the sheet: the shader premultiplies when it blends, and a straight-alpha DXT5
        // is what the converter expects from the TGA.
        var s_Straight = new FormatConvertedBitmap(s_Target, PixelFormats.Bgra32, null, 0);
        var s_Pixels = new byte[s_SheetWidth * s_SheetHeight * 4];
        s_Straight.CopyPixels(s_Pixels, s_SheetWidth * 4, 0);

        // Diagnostic (CAMO_SHEET_DEBUG_BG=1): every opaque near-black texel becomes magenta, so an in-game
        // round tells whether the sheet's dark colours reach the body or only its bright ones do.
        var s_Debug = Environment.GetEnvironmentVariable("CAMO_SHEET_DEBUG_BG");
        if (s_Debug == "1")
            for (var i = 0; i < s_Pixels.Length; i += 4)
                if (s_Pixels[i + 3] > 0 && s_Pixels[i] < 24 && s_Pixels[i + 1] < 24 && s_Pixels[i + 2] < 24)
                {
                    s_Pixels[i] = 255;
                    s_Pixels[i + 1] = 0;
                    s_Pixels[i + 2] = 255;
                }

        // Diagnostic (CAMO_SHEET_DEBUG_BG=2): every bright texel keeps its colour but loses its alpha, so an
        // in-game round tells whether the alpha channel or the colour decides what the sheet paints.
        if (s_Debug == "2")
            for (var i = 0; i < s_Pixels.Length; i += 4)
                if (s_Pixels[i] > 200 && s_Pixels[i + 1] > 200 && s_Pixels[i + 2] > 200)
                    s_Pixels[i + 3] = 0;

        // Diagnostic (CAMO_SHEET_DEBUG_BG=3): every opaque near-black texel becomes a dark blue (0,0,96) —
        // low luminance, saturated hue — so an in-game round separates "alpha reads as 1" (a blue square)
        // from "the colour's brightness decides" (nothing).
        if (s_Debug == "3")
            for (var i = 0; i < s_Pixels.Length; i += 4)
                if (s_Pixels[i + 3] > 0 && s_Pixels[i] < 24 && s_Pixels[i + 1] < 24 && s_Pixels[i + 2] < 24)
                {
                    s_Pixels[i] = 96;
                    s_Pixels[i + 1] = 0;
                    s_Pixels[i + 2] = 0;
                }

        var s_Sheet = BitmapSource.Create(s_SheetWidth, s_SheetHeight, 96, 96, PixelFormats.Bgra32, null, s_Pixels, s_SheetWidth * 4);
        s_Sheet.Freeze();

        return new GifAtlas
        {
            Path = p_Path, Clip = p_Clip, Sheet = s_Sheet, Width = s_SheetWidth, Height = s_SheetHeight,
            CellWidth = s_CW, CellHeight = s_CH, Columns = s_C, Rows = s_R, CellOf = s_CellOf, Opaque = p_Opaque,
        };
    }

    /// <summary>
    /// The picture a map is composed from: a gradient whose red is the frame's x and green its y, the size
    /// of a cell's inner rectangle. Drawn through the same decal projection as a sticker, every layer texel
    /// of the placement then holds the frame coordinate it shows — the shader reads that coordinate and
    /// looks the current frame up in the sheet. Coordinates run 1..255: 0 is reserved for "nothing here",
    /// the value of every texel outside a placement (an opaque frame's corner must never paint the body —
    /// measured in-game, 2026-09-12: the whole weapon went black).
    /// </summary>
    public BitmapSource CoordinatePicture() => m_CoordinatePicture ??= BuildCoordinatePicture();

    private BitmapSource? m_CoordinatePicture;

    private BitmapSource BuildCoordinatePicture()
    {
        var s_Width = Math.Max(2, CellWidth - 2 * Inset);
        var s_Height = Math.Max(2, CellHeight - 2 * Inset);
        var s_Pixels = new byte[s_Width * s_Height * 4];
        for (var y = 0; y < s_Height; y++)
        for (var x = 0; x < s_Width; x++)
        {
            var i = (y * s_Width + x) * 4;
            s_Pixels[i] = 0;                                                       // B
            s_Pixels[i + 1] = (byte) (1 + Math.Round(254.0 * y / (s_Height - 1))); // G = t
            s_Pixels[i + 2] = (byte) (1 + Math.Round(254.0 * x / (s_Width - 1)));  // R = s
            s_Pixels[i + 3] = 255;
        }

        var s_Picture = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_Pixels, s_Width * 4);
        s_Picture.Freeze();
        return s_Picture;
    }
}
