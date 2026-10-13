using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeCamoStudio.Project;

/// <summary>
/// ⭐ A PRESET'S PICTURE MADE OF THE GAME'S OWN TEXTURES IS KEPT AS ITS RECIPE. A soldier part's starting document carries a legs mask
/// that is the game's: the colours of one of its mask textures, the alpha of another, and a hand edit on top (texels painted white,
/// texels whose alpha was set) — so what ships is the recipe beside the preset (&lt;picture&gt;.recipe.json: which textures, and the edit
/// layer holding only the edited texels), and the picture is made in the user's cache, out of his own game's textures, the first time a
/// preset asks for it. A picture that is wholly ours (a hand-painted mask) ships as itself.
///
/// The recipe: { "Colour": "&lt;texture&gt;", "Alpha": "&lt;texture&gt;" (the colour's own alpha when absent), "Edit": "&lt;layer png, relative to the
/// presets folder&gt;" (optional), "EditColour": true|false }. The edit layer: R = 255 where the colour is painted white, G = 255 where
/// the alpha is set, A = the alpha set there; every other texel zero.
/// </summary>
internal static class PresetPicture
{
    /// <summary>Where the pictures made from recipes are kept: the cache, beside everything else read out of the game.</summary>
    public static string MadeFolder => Path.Combine(Settings.CacheFolder, "presetpictures");

    /// <summary>
    /// The file a preset's picture (named relative to <paramref name="p_Folder"/>) is read from: the shipped one when it is there, else
    /// the one its recipe makes in the cache (made now if it is not yet). Null, with the reason, when neither can be had.
    /// </summary>
    public static string? FileOf(string p_Folder, string p_Relative, out string? p_Why)
    {
        p_Why = null;
        var s_Shipped = Path.GetFullPath(Path.Combine(p_Folder, p_Relative));
        if (File.Exists(s_Shipped))
            return s_Shipped;

        var s_Recipe = Path.ChangeExtension(s_Shipped, ".recipe.json");
        if (!File.Exists(s_Recipe))
        {
            p_Why = $"'{p_Relative}' is neither shipped nor made from a recipe";
            return null;
        }

        var s_Made = Path.GetFullPath(Path.Combine(MadeFolder, p_Relative));
        if (File.Exists(s_Made))
            return s_Made;

        try
        {
            var s_Json = JsonNode.Parse(File.ReadAllText(s_Recipe))!.AsObject();
            var s_ColourOf = (string?) s_Json["Colour"] ?? "";
            var s_AlphaOf = (string?) s_Json["Alpha"];
            if (Pixels(s_ColourOf, out var s_Width, out var s_Height) is not { } s_Picture)
            {
                p_Why = $"the game texture '{s_ColourOf}' is not in the cache";
                return null;
            }

            if (s_AlphaOf != null)
            {
                if (Pixels(s_AlphaOf, out var s_W, out var s_H) is not { } s_Alpha || s_W != s_Width || s_H != s_Height)
                {
                    p_Why = $"the game texture '{s_AlphaOf}' is not in the cache, or not the size of '{s_ColourOf}'";
                    return null;
                }

                for (var i = 3; i < s_Picture.Length; i += 4)
                    s_Picture[i] = s_Alpha[i];
            }

            if ((string?) s_Json["Edit"] is { Length: > 0 } s_EditOf)
            {
                var s_Edit = Read(Path.Combine(p_Folder, s_EditOf), out var s_W, out var s_H);
                if (s_W != s_Width || s_H != s_Height)
                {
                    p_Why = $"the edit layer '{s_EditOf}' is not the size of '{s_ColourOf}'";
                    return null;
                }

                var s_EditColour = (bool?) s_Json["EditColour"] ?? false;
                for (var i = 0; i < s_Picture.Length; i += 4)
                {
                    // (Bgra32: blue, green, red, alpha)
                    if (s_EditColour && s_Edit[i + 2] == 255)
                        s_Picture[i] = s_Picture[i + 1] = s_Picture[i + 2] = 255;
                    if (s_Edit[i + 1] == 255)
                        s_Picture[i + 3] = s_Edit[i + 3];
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(s_Made)!);
            var s_Encoder = new PngBitmapEncoder();
            s_Encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null, s_Picture, s_Width * 4)));
            using (var s_Stream = File.Create(s_Made))
                s_Encoder.Save(s_Stream);
            return s_Made;
        }
        catch (Exception s_Exception)
        {
            p_Why = $"its recipe could not be followed ({s_Exception.Message})";
            return null;
        }
    }

    /// <summary>A game texture of the cache as straight Bgra32 pixels — decoded as the cache decodes it for the preview (DdsImage).</summary>
    private static byte[]? Pixels(string p_Texture, out int p_Width, out int p_Height)
    {
        p_Width = p_Height = 0;
        var s_Dds = Cache.GameCache.TextureFile(p_Texture);
        if (!File.Exists(s_Dds) || RimeShaderEditor.View.DdsImage.Load(s_Dds) is not { } s_Image)
            return null;

        return Bgra(s_Image, out p_Width, out p_Height);
    }

    private static byte[] Read(string p_Png, out int p_Width, out int p_Height) =>
        Bgra(BitmapDecoder.Create(new Uri(Path.GetFullPath(p_Png)), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0], out p_Width, out p_Height);

    private static byte[] Bgra(BitmapSource p_Image, out int p_Width, out int p_Height)
    {
        var s_Converted = p_Image.Format == PixelFormats.Bgra32 ? p_Image : new FormatConvertedBitmap(p_Image, PixelFormats.Bgra32, null, 0);
        p_Width = s_Converted.PixelWidth;
        p_Height = s_Converted.PixelHeight;
        var s_Pixels = new byte[p_Width * p_Height * 4];
        s_Converted.CopyPixels(s_Pixels, p_Width * 4, 0);
        return s_Pixels;
    }
}
