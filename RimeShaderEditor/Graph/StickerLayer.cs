using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeShaderEditor.Graph;

/// <summary>
/// Composes the stickers placed on one weapon into the LAYER its shader samples: a square RGBA picture in the
/// weapon's texture space, transparent wherever no sticker is, each sticker projected onto the body as a
/// decal (<see cref="StickerSurface"/>) with its width, height, rotation, mirror and reach. The preview
/// pushes the result to the sticker register and the bake ships it as a texture, so one routine decides
/// what a placement looks like everywhere.
/// </summary>
public static class StickerLayer
{
    /// <summary>How far a sticker projects above and below its surface when its placement says nothing: half its larger side.</summary>
    public const double DefaultReach = 0.5;

    /// <summary>A placement's reach as a fraction of its larger side: its own, or the default.</summary>
    public static double ReachOf(StickerPlacement p_Sticker) => p_Sticker.Reach > 0 ? p_Sticker.Reach : DefaultReach;

    /// <summary>
    /// The layer, premultiplied BGRA, <paramref name="p_Side"/> texels square. Stickers whose picture cannot
    /// be read are skipped — a missing file leaves a hole, never a crash in the middle of a drag or a bake.
    /// Must run on a thread that owns the WPF objects it makes (the UI thread, or the thread a headless seam
    /// runs on).
    /// </summary>
    /// <param name="p_Surface">The weapon's body to project onto. Without one (a bake without the weapon's
    /// cached dump) a sticker is drawn flat: its rectangle in texture space, on every part of the weapon whose
    /// unwrap shares that patch — the fallback, not the look.</param>
    /// <param name="p_FrameSurface">The body the PLACEMENTS are resolved on (their (u, v) and anchor name a
    /// spot on the mesh the studio previews, the first-person body); the decal frame found there — a point,
    /// axes and size in the weapon's own space — is then painted onto <paramref name="p_Surface"/>, which may
    /// be another mesh of the same weapon (the third-person body: same space, its own unwrap, and on the
    /// F2000 its sides mapped in mirror — measured in-game, 2026-09-12). Null = the drawing surface itself.</param>
    public static BitmapSource Compose(IEnumerable<StickerPlacement> p_Stickers, Func<string, BitmapSource?> p_ImageOf,
        int p_Side, StickerSurface? p_Surface = null, StickerSurface? p_FrameSurface = null)
    {
        p_Side = Math.Clamp(p_Side, 16, 4096);
        p_FrameSurface ??= p_Surface;

        // PREMULTIPLIED on purpose, and kept so all the way to the game: the shader composes it as
        // diffuse × (1 − a) + rgb, which is exact for premultiplied art and stays exact through the mip
        // chain the texture converter builds (filtering straight-alpha texels drags the transparent
        // neighbours' colour into every smaller mip — a dark halo round the sticker at a distance).
        if (p_Surface != null)
        {
            // Projected: rasterised on the CPU, texel by texel (StickerSurface.DrawInto) — each triangle
            // grown by most of a texel so neighbours overlap and no hairline is left along shared edges.
            var s_Length = p_Side * p_Side * 4;
            var s_Pixels = s_Scratch.Rent(s_Length);
            Array.Clear(s_Pixels, 0, s_Length);
            var s_Painted = new HashSet<int>();
            foreach (var s_Sticker in p_Stickers)
            {
                if (p_ImageOf(s_Sticker.Image) is not { PixelWidth: > 0, PixelHeight: > 0 } s_Image)
                    continue;

                if (p_FrameSurface?.FrameOf(s_Sticker, AspectOf(s_Image)) is { } s_Frame)
                    s_Painted.UnionWith(p_Surface.DrawInto(s_Pixels, p_Side, s_Frame, StickerSurface.Picture.Of(s_Image), s_Sticker.Mirror));
            }

            // Padded across the seams of the unwrap, so the game's filtering never blends a sticker texel
            // with the transparent texel of the piece next to it in the atlas.
            if (s_Painted.Count > 0)
                p_Surface.PadAcrossSeams(s_Pixels, p_Side, s_Painted);
            var s_Layer = BitmapSource.Create(p_Side, p_Side, 96, 96, PixelFormats.Pbgra32, null, s_Pixels, p_Side * 4);
            s_Layer.Freeze();
            s_Scratch.Return(s_Pixels);
            return s_Layer;
        }

        // No body to project onto (a bake without the weapon's cached dump): flat, through the drawing system.
        var s_Visual = new DrawingVisual();
        using (var s_Context = s_Visual.RenderOpen())
        {
            foreach (var s_Sticker in p_Stickers)
                if (p_ImageOf(s_Sticker.Image) is { PixelWidth: > 0, PixelHeight: > 0 } s_Image)
                    DrawFlat(s_Context, s_Sticker, s_Image, p_Side);
        }

        var s_Target = new RenderTargetBitmap(p_Side, p_Side, 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Visual);
        s_Target.Freeze();
        return s_Target;
    }

    /// <summary>
    /// The coordinate MAP of a weapon's animated sticker placements: the same projection as
    /// <see cref="Compose"/>, drawing the sheet's coordinate picture (red = x, green = y of the GIF frame)
    /// instead of a picture — so every layer texel a placement covers holds the frame coordinate it shows,
    /// and the shader reads the current frame of the sheet there. Bgra32, straight: red = x, green = y,
    /// alpha 255 where a placement is and 0 (with x = y = 0, the transparent gutter of a cell) elsewhere.
    /// Anti-aliased edge texels are un-premultiplied back to their coordinate, and edges are hard: a
    /// coordinate is not a colour, half of one means nothing.
    /// </summary>
    /// <summary>
    /// Scratch buffers for a compose: a 4 MB array six times a refresh went to the large-object heap and
    /// came back as collection pauses in the middle of a drag; rented and returned instead.
    /// </summary>
    private static readonly System.Buffers.ArrayPool<byte> s_Scratch = System.Buffers.ArrayPool<byte>.Shared;

    public static BitmapSource ComposeMap(IEnumerable<StickerPlacement> p_Stickers, GifAtlas p_Atlas, int p_Side, StickerSurface? p_Surface = null,
        StickerSurface? p_FrameSurface = null) =>
        ComposeMap(p_Stickers, p_Atlas.CoordinatePicture(), p_Side, p_Surface, p_FrameSurface);

    /// <summary>
    /// The coordinate map for placements that all show <paramref name="p_Picture"/>, a coordinate picture (red = x, green = y,
    /// 1..255): an animated sticker's frame (GifAtlas.CoordinatePicture) or the emblem's square (EmblemCoordinatePicture).
    /// </summary>
    public static BitmapSource ComposeMap(IEnumerable<StickerPlacement> p_Stickers, BitmapSource p_Picture, int p_Side, StickerSurface? p_Surface = null,
        StickerSurface? p_FrameSurface = null)
    {
        var s_Picture = p_Picture;
        var s_Premultiplied = Compose(p_Stickers, _ => s_Picture, p_Side, p_Surface, p_FrameSurface);
        var s_Length = s_Premultiplied.PixelWidth * s_Premultiplied.PixelHeight * 4;
        var s_Pixels = s_Scratch.Rent(s_Length);
        s_Premultiplied.CopyPixels(s_Pixels, s_Premultiplied.PixelWidth * 4, 0);
        for (var i = 0; i < s_Length; i += 4)
        {
            var s_Alpha = s_Pixels[i + 3];
            if (s_Alpha < 128)
            {
                s_Pixels[i] = 0;
                s_Pixels[i + 1] = 0;
                s_Pixels[i + 2] = 0;
                s_Pixels[i + 3] = 0;
                continue;
            }

            s_Pixels[i] = 0;
            s_Pixels[i + 1] = (byte) Math.Min(255, s_Pixels[i + 1] * 255 / s_Alpha);
            s_Pixels[i + 2] = (byte) Math.Min(255, s_Pixels[i + 2] * 255 / s_Alpha);
            s_Pixels[i + 3] = 255;
        }

        var s_Map = BitmapSource.Create(s_Premultiplied.PixelWidth, s_Premultiplied.PixelHeight, 96, 96, PixelFormats.Bgra32, null,
            s_Pixels, s_Premultiplied.PixelWidth * 4);
        s_Map.Freeze();
        s_Scratch.Return(s_Pixels);
        return s_Map;
    }

    /// <summary>
    /// The SIDE map of a weapon mesh's placements (still and animated alike): every texel a sticker was
    /// painted on holds the grey of the handedness of the triangle it was painted on (85 = as laid out,
    /// 170 = mirrored), 0 elsewhere. A weapon whose two sides share one patch of texture shows a sticker
    /// on BOTH sides otherwise, mirrored on the second (the F2000 in-game, 2026-09-12); the shader compares
    /// this with the handedness of the tangent frame it draws with and keeps the sticker on the side it was
    /// placed on. Grey Bgra32, alpha 255 everywhere; edge texels snapped to one of the three values.
    /// </summary>
    /// <param name="p_MirrorHand">Store each triangle's OPPOSITE hand: for a view that draws the mesh through
    /// a mirror (the studio's preview). The game reads the hand as composed, in first and third person alike
    /// (measured 2026-09-12, F2000, "VU P2").</param>
    public static BitmapSource ComposeSideMap(IEnumerable<StickerPlacement> p_Stickers, Func<string, BitmapSource?> p_ImageOf,
        int p_Side, StickerSurface? p_Surface, StickerSurface? p_FrameSurface = null, bool p_MirrorHand = false)
    {
        p_Side = Math.Clamp(p_Side, 16, 4096);
        p_FrameSurface ??= p_Surface;
        var s_Visual = new DrawingVisual();
        using (var s_Context = s_Visual.RenderOpen())
        {
            if (p_Surface != null)
                foreach (var s_Sticker in p_Stickers)
                {
                    var s_Aspect = AspectOf(p_ImageOf(s_Sticker.Image));
                    if (p_FrameSurface?.FrameOf(s_Sticker, s_Aspect) is { } s_Frame)
                        p_Surface.DrawSides(s_Context, s_Frame, p_Side);
                }
        }

        var s_Target = new RenderTargetBitmap(p_Side, p_Side, 96, 96, PixelFormats.Pbgra32);
        s_Target.Render(s_Visual);
        var s_Length = p_Side * p_Side * 4;
        var s_Pixels = s_Scratch.Rent(s_Length);
        s_Target.CopyPixels(s_Pixels, p_Side * 4, 0);
        for (var i = 0; i < s_Length; i += 4)
        {
            var s_Alpha = s_Pixels[i + 3];
            byte s_Grey = 0;
            if (s_Alpha >= 128)
            {
                var s_Value = s_Pixels[i] * 255 / s_Alpha;
                var s_Hand = s_Value < 128 ? 1 : -1;
                s_Grey = StickerSurface.SideValue(p_MirrorHand ? -s_Hand : s_Hand);
            }

            s_Pixels[i] = s_Pixels[i + 1] = s_Pixels[i + 2] = s_Grey;
            s_Pixels[i + 3] = 255;
        }

        var s_Map = BitmapSource.Create(p_Side, p_Side, 96, 96, PixelFormats.Bgra32, null, s_Pixels, p_Side * 4);
        s_Map.Freeze();
        s_Scratch.Return(s_Pixels);
        return s_Map;
    }

    /// <summary>
    /// THE ONE texture the shader reads the side and the frame coordinates from, packed the way the game
    /// can carry it — DXT5, whose ALPHA is 8 bits a texel and whose colour is 5/6/5, the one lane every
    /// package already uses: red = the side grey (0, 85 or 170), green = the frame's y (6 bits), alpha = the
    /// frame's x (8 bits; 255 without an animated sticker). ONE texture: the packages with seven or eight
    /// external textures came out black in-game (2026-09-12, F2000), but those rounds also carried two bugs
    /// since fixed (the side gate, an opaque empty layer) — and a package with SEVEN (a GIF's sheet beside the
    /// emblem slot's atlas) drew right in game on 2026-10-13: no cap at seven. The one-channel BC4 and
    /// two-channel BC5 lanes WERE garbage there. Green is 6 bits: y has about 64 levels in-game; a second map
    /// with y in its alpha is the remedy if that shows.
    /// </summary>
    public static BitmapSource PackMaps(BitmapSource p_Side, BitmapSource? p_Map)
    {
        var s_Width = p_Side.PixelWidth;
        var s_Height = p_Side.PixelHeight;
        var s_Length = s_Width * s_Height * 4;
        var s_SidePixels = s_Scratch.Rent(s_Length);
        p_Side.CopyPixels(s_SidePixels, s_Width * 4, 0);
        byte[]? s_MapPixels = null;
        if (p_Map != null && p_Map.PixelWidth == s_Width && p_Map.PixelHeight == s_Height)
        {
            s_MapPixels = s_Scratch.Rent(s_Length);
            p_Map.CopyPixels(s_MapPixels, s_Width * 4, 0);
        }

        var s_Packed = s_Scratch.Rent(s_Length);
        for (var i = 0; i < s_Length; i += 4)
        {
            s_Packed[i] = 0;                                                     // B
            s_Packed[i + 1] = s_MapPixels == null ? (byte) 0 : s_MapPixels[i + 1]; // G = y (the map's green)
            s_Packed[i + 2] = s_SidePixels[i];                                   // R = side
            s_Packed[i + 3] = s_MapPixels == null ? (byte) 255 : s_MapPixels[i + 2]; // A = x (the map's red)
        }

        var s_Bitmap = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_Packed, s_Width * 4);
        s_Bitmap.Freeze();
        s_Scratch.Return(s_SidePixels);
        if (s_MapPixels != null)
            s_Scratch.Return(s_MapPixels);
        s_Scratch.Return(s_Packed);
        return s_Bitmap;
    }

    /// <summary>
    /// A coordinate map split into its two one-channel textures — x (the map's red) and y (its green), each
    /// as a grey picture with the value in every colour channel and alpha 255 — the form the shader reads
    /// them in (the red channel of a one-channel BC4 texture) and the preview pushes them as.
    /// </summary>
    public static (BitmapSource U, BitmapSource V) SplitMap(BitmapSource p_Map)
    {
        var s_Width = p_Map.PixelWidth;
        var s_Height = p_Map.PixelHeight;
        var s_Pixels = new byte[s_Width * s_Height * 4];
        p_Map.CopyPixels(s_Pixels, s_Width * 4, 0);
        var s_U = new byte[s_Pixels.Length];
        var s_V = new byte[s_Pixels.Length];
        for (var i = 0; i < s_Pixels.Length; i += 4)
        {
            var s_X = s_Pixels[i + 2];
            var s_Y = s_Pixels[i + 1];
            s_U[i] = s_U[i + 1] = s_U[i + 2] = s_X;
            s_V[i] = s_V[i + 1] = s_V[i + 2] = s_Y;
            s_U[i + 3] = 255;
            s_V[i + 3] = 255;
        }

        var s_MapU = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_U, s_Width * 4);
        var s_MapV = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_V, s_Width * 4);
        s_MapU.Freeze();
        s_MapV.Freeze();
        return (s_MapU, s_MapV);
    }

    /// <summary>The flat fallback: the picture as a rectangle in texture space, rotated and mirrored, unclipped.</summary>
    private static void DrawFlat(DrawingContext p_Context, StickerPlacement p_Sticker, BitmapSource p_Image, int p_Side)
    {
        var s_Rect = RectOf(p_Sticker, p_Image, p_Side);
        var s_Centre = new Point(p_Sticker.U * p_Side, p_Sticker.V * p_Side);

        // Rotation is counter-clockwise; WPF rotates clockwise in y-down space.
        var s_Transform = new TransformGroup();
        if (p_Sticker.Mirror)
            s_Transform.Children.Add(new ScaleTransform(-1, 1, s_Centre.X, s_Centre.Y));
        s_Transform.Children.Add(new RotateTransform(-p_Sticker.Rotation, s_Centre.X, s_Centre.Y));

        p_Context.PushTransform(s_Transform);
        p_Context.DrawImage(p_Image, s_Rect);
        p_Context.Pop();
    }

    /// <summary>
    /// Writes a layer as a 32-bit TGA — the file the bake hands the texture converter. TGA and not PNG or
    /// BMP: a PNG carries colour-space chunks the converter honours (it would linearise the pixels), and
    /// a BMP drops the alpha the whole layer is about.
    /// </summary>
    public static void SaveTga(BitmapSource p_Layer, string p_Path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
        var s_Pixels = new byte[p_Layer.PixelWidth * p_Layer.PixelHeight * 4];
        var s_Source = p_Layer.Format == PixelFormats.Pbgra32 || p_Layer.Format == PixelFormats.Bgra32
            ? p_Layer
            : new FormatConvertedBitmap(p_Layer, PixelFormats.Pbgra32, null, 0);
        s_Source.CopyPixels(s_Pixels, p_Layer.PixelWidth * 4, 0);

        using var s_Stream = File.Create(p_Path);
        using var s_Writer = new BinaryWriter(s_Stream);
        s_Writer.Write((byte) 0);              // id length
        s_Writer.Write((byte) 0);              // no colour map
        s_Writer.Write((byte) 2);              // uncompressed true-colour
        s_Writer.Write(new byte[5]);           // colour map spec
        s_Writer.Write((ushort) 0);            // x origin
        s_Writer.Write((ushort) 0);            // y origin
        s_Writer.Write((ushort) p_Layer.PixelWidth);
        s_Writer.Write((ushort) p_Layer.PixelHeight);
        s_Writer.Write((byte) 32);             // bits per pixel
        s_Writer.Write((byte) 0x28);           // 8 alpha bits, top-left origin: rows as WPF holds them
        s_Writer.Write(s_Pixels);              // BGRA, the TGA byte order
    }

    /// <summary>The sticker's height in texture units: its own when set, the picture's aspect otherwise.</summary>
    public static double HeightOf(StickerPlacement p_Sticker, double p_Aspect) =>
        p_Sticker.Height > 0 ? p_Sticker.Height : p_Sticker.Width * p_Aspect;

    /// <summary>The sticker's unrotated rectangle in layer texels: width from the placement, height its own or the picture's.</summary>
    public static Rect RectOf(StickerPlacement p_Sticker, BitmapSource p_Image, int p_Side)
    {
        var s_Width = p_Sticker.Width * p_Side;
        var s_Height = HeightOf(p_Sticker, AspectOf(p_Image)) * p_Side;
        return new Rect(p_Sticker.U * p_Side - s_Width / 2, p_Sticker.V * p_Side - s_Height / 2, s_Width, s_Height);
    }

    /// <summary>
    /// The emblem slot's coordinate picture: a SQUARE (BF4's emblem canvas is 320×320), red = x across it, green = y down it,
    /// 1..255 so that 0 keeps meaning "no slot here" — the picture an emblem placement is composed with into the coordinate map,
    /// exactly as an animated sticker's frame is (GifAtlas.CoordinatePicture), so the shader's EmblemLayers node reads the point of
    /// the emblem each texel shows.
    /// </summary>
    public static BitmapSource EmblemCoordinatePicture() => s_EmblemPicture ??= BuildEmblemPicture(256);

    private static BitmapSource? s_EmblemPicture;

    private static BitmapSource BuildEmblemPicture(int p_Side)
    {
        var s_Pixels = new byte[p_Side * p_Side * 4];
        for (var y = 0; y < p_Side; y++)
        for (var x = 0; x < p_Side; x++)
        {
            var i = (y * p_Side + x) * 4;
            s_Pixels[i] = 0;                                                     // B
            s_Pixels[i + 1] = (byte) (1 + Math.Round(254.0 * y / (p_Side - 1))); // G = y
            s_Pixels[i + 2] = (byte) (1 + Math.Round(254.0 * x / (p_Side - 1))); // R = x
            s_Pixels[i + 3] = 255;
        }

        var s_Picture = BitmapSource.Create(p_Side, p_Side, 96, 96, PixelFormats.Bgra32, null, s_Pixels, p_Side * 4);
        s_Picture.Freeze();
        return s_Picture;
    }

    /// <summary>Height over width of a picture, for the placement's height.</summary>
    public static double AspectOf(BitmapSource? p_Image) =>
        p_Image is { PixelWidth: > 0, PixelHeight: > 0 } ? (double) p_Image.PixelHeight / p_Image.PixelWidth : 1.0;

    /// <summary>Writes a composed layer as a PNG, for looking at.</summary>
    public static void SavePng(BitmapSource p_Layer, string p_Path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(p_Path)!);
        var s_Encoder = new PngBitmapEncoder();
        s_Encoder.Frames.Add(BitmapFrame.Create(p_Layer));
        using var s_Stream = File.Create(p_Path);
        s_Encoder.Save(s_Stream);
    }
}
