using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RimeShaderEditor.View;

/// <summary>
/// Minimal DDS reader for node thumbnails. WPF/WIC has no DDS codec and Rime's texture converter only emits
/// DDS, so the block formats BF3 actually ships (BC1 colour, BC3/BC5 normals) are decompressed here. Only the
/// top mip is decoded — this exists to draw a 136px preview, not to be a texture pipeline.
/// </summary>
public static class DdsImage
{
    private const uint c_Magic = 0x20534444; // "DDS "

    private enum Codec
    {
        Unsupported,
        Bc1,
        Bc2,
        Bc3,
        Bc4,
        Bc5,
        Bgra8,
        Rgba8,
    }

    public static BitmapSource? Load(string p_Path)
    {
        try
        {
            using var s_Stream = File.OpenRead(p_Path);
            using var s_Reader = new BinaryReader(s_Stream);

            if (s_Reader.ReadUInt32() != c_Magic)
                return null;

            var s_HeaderSize = s_Reader.ReadUInt32();
            if (s_HeaderSize != 124)
                return null;

            s_Reader.ReadUInt32(); // flags
            var s_Height = (int) s_Reader.ReadUInt32();
            var s_Width = (int) s_Reader.ReadUInt32();
            s_Reader.ReadUInt32(); // pitchOrLinearSize
            s_Reader.ReadUInt32(); // depth
            s_Reader.ReadUInt32(); // mipMapCount
            for (var i = 0; i < 11; i++)
                s_Reader.ReadUInt32(); // reserved1

            s_Reader.ReadUInt32(); // ddspf.size
            var s_PixelFlags = s_Reader.ReadUInt32();
            var s_FourCc = s_Reader.ReadUInt32();
            var s_BitCount = s_Reader.ReadUInt32();
            var s_RedMask = s_Reader.ReadUInt32();
            s_Reader.ReadUInt32(); // gMask
            s_Reader.ReadUInt32(); // bMask
            s_Reader.ReadUInt32(); // aMask

            for (var i = 0; i < 5; i++)
                s_Reader.ReadUInt32(); // caps1..4 + reserved2

            var s_Codec = Identify(s_PixelFlags, s_FourCc, s_BitCount, s_RedMask);

            if (s_FourCc == FourCc("DX10"))
            {
                var s_DxgiFormat = s_Reader.ReadUInt32();
                for (var i = 0; i < 4; i++)
                    s_Reader.ReadUInt32();

                s_Codec = FromDxgi(s_DxgiFormat);
            }

            if (s_Codec == Codec.Unsupported || s_Width <= 0 || s_Height <= 0)
                return null;

            var s_Payload = s_Reader.ReadBytes(RequiredBytes(s_Codec, s_Width, s_Height));
            var s_Pixels = Decode(s_Codec, s_Payload, s_Width, s_Height);
            if (s_Pixels == null)
                return null;

            var s_Bitmap = BitmapSource.Create(s_Width, s_Height, 96, 96, PixelFormats.Bgra32, null,
                s_Pixels, s_Width * 4);
            s_Bitmap.Freeze();
            return s_Bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The header flag bit the dumper sets from the game's own per-texture gamma flag. It is not part of the
    /// standard DDS flag set, which is why a plain "DXT1" file can still say how the game samples it.
    /// </summary>
    private const uint c_SrgbFlag = 0x40000000;

    /// <summary>
    /// Whether the game samples this texture through an sRGB view (stored values decoded to linear on read) or
    /// as stored. Null when the file is not a readable DDS.
    ///
    /// ⛔ THIS BIT IS AN INPUT OF EVERY MASK THE SHADERS BUILD FROM A TEXTURE CHANNEL. The weapon presets'
    /// wear mask is saturate((specular.g × WearAmount) ^ WearPower), and the weapons' specular maps carry the
    /// flag: the game reads a stored 0.12 as 0.013, so WearAmount 10–30 leaves most of the body under the camo.
    /// A view created without it reads the 0.12 as-is and the same numbers saturate the mask nearly everywhere —
    /// "every camo looks worn at the game's own values". Measured over the cache: 477 flagged, 174 not, and NO
    /// name rule separates them (a weapon's "_s" is flagged, a soldier's "_s" is not), so it is read per file.
    /// </summary>
    public static bool? IsSrgb(string p_Path)
    {
        try
        {
            using var s_Stream = File.OpenRead(p_Path);
            var s_Header = new byte[12];
            if (s_Stream.Read(s_Header, 0, s_Header.Length) != s_Header.Length ||
                BitConverter.ToUInt32(s_Header, 0) != c_Magic)
                return null;

            return (BitConverter.ToUInt32(s_Header, 8) & c_SrgbFlag) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How the preview must create the view of a cached texture: from the .dds beside its thumbnail when it is
    /// there, otherwise guessed from the name (normal maps linear, everything else sRGB) and reported as a
    /// guess so the caller can say so — the thumbnail PNG itself carries no flag.
    /// </summary>
    public static (bool Srgb, bool FromHeader) SrgbOf(string p_DdsPath, string p_AssetName)
    {
        if (IsSrgb(p_DdsPath) is { } s_Flag)
            return (s_Flag, true);

        return (!LooksLikeNormalMap(p_AssetName), false);
    }

    private static bool LooksLikeNormalMap(string p_Name)
    {
        var s_Stem = Path.GetFileNameWithoutExtension(p_Name).ToLowerInvariant();
        return s_Stem.EndsWith("_n") || s_Stem.EndsWith("_nm") || s_Stem.EndsWith("_nrm") ||
               s_Stem.Contains("normal");
    }

    private static uint FourCc(string p_Code) =>
        (uint) (p_Code[0] | (p_Code[1] << 8) | (p_Code[2] << 16) | (p_Code[3] << 24));

    private static Codec Identify(uint p_PixelFlags, uint p_FourCc, uint p_BitCount, uint p_RedMask)
    {
        const uint c_FourCcFlag = 0x4;
        const uint c_RgbFlag = 0x40;

        if ((p_PixelFlags & c_FourCcFlag) != 0)
        {
            if (p_FourCc == FourCc("DXT1")) return Codec.Bc1;
            if (p_FourCc == FourCc("DXT2") || p_FourCc == FourCc("DXT3")) return Codec.Bc2;
            if (p_FourCc == FourCc("DXT4") || p_FourCc == FourCc("DXT5")) return Codec.Bc3;
            if (p_FourCc == FourCc("ATI1") || p_FourCc == FourCc("BC4U")) return Codec.Bc4;
            if (p_FourCc == FourCc("ATI2") || p_FourCc == FourCc("BC5U")) return Codec.Bc5;
            return Codec.Unsupported;
        }

        if ((p_PixelFlags & c_RgbFlag) != 0 && p_BitCount == 32)
            return p_RedMask == 0x00ff0000 ? Codec.Bgra8 : Codec.Rgba8;

        return Codec.Unsupported;
    }

    private static Codec FromDxgi(uint p_Format) => p_Format switch
    {
        70 or 71 or 72 => Codec.Bc1,
        73 or 74 or 75 => Codec.Bc2,
        76 or 77 or 78 => Codec.Bc3,
        79 or 80 or 81 => Codec.Bc4,
        82 or 83 or 84 => Codec.Bc5,
        87 or 88 => Codec.Bgra8,
        28 or 29 => Codec.Rgba8,
        _ => Codec.Unsupported,
    };

    private static int RequiredBytes(Codec p_Codec, int p_Width, int p_Height)
    {
        var s_BlocksX = Math.Max(1, (p_Width + 3) / 4);
        var s_BlocksY = Math.Max(1, (p_Height + 3) / 4);

        return p_Codec switch
        {
            Codec.Bc1 or Codec.Bc4 => s_BlocksX * s_BlocksY * 8,
            Codec.Bc2 or Codec.Bc3 or Codec.Bc5 => s_BlocksX * s_BlocksY * 16,
            _ => p_Width * p_Height * 4,
        };
    }

    private static byte[]? Decode(Codec p_Codec, byte[] p_Data, int p_Width, int p_Height)
    {
        var s_Out = new byte[p_Width * p_Height * 4];

        switch (p_Codec)
        {
            case Codec.Bgra8:
                if (p_Data.Length < s_Out.Length) return null;
                Array.Copy(p_Data, s_Out, s_Out.Length);
                return s_Out;

            case Codec.Rgba8:
                if (p_Data.Length < s_Out.Length) return null;
                for (var i = 0; i < p_Width * p_Height; i++)
                {
                    s_Out[i * 4 + 0] = p_Data[i * 4 + 2];
                    s_Out[i * 4 + 1] = p_Data[i * 4 + 1];
                    s_Out[i * 4 + 2] = p_Data[i * 4 + 0];
                    s_Out[i * 4 + 3] = p_Data[i * 4 + 3];
                }

                return s_Out;
        }

        var s_BlocksX = Math.Max(1, (p_Width + 3) / 4);
        var s_BlocksY = Math.Max(1, (p_Height + 3) / 4);
        var s_BlockBytes = p_Codec is Codec.Bc1 or Codec.Bc4 ? 8 : 16;

        var s_Colors = new byte[16 * 4];
        var s_Offset = 0;

        for (var s_By = 0; s_By < s_BlocksY; s_By++)
        for (var s_Bx = 0; s_Bx < s_BlocksX; s_Bx++)
        {
            if (s_Offset + s_BlockBytes > p_Data.Length)
                return s_Out;

            Array.Clear(s_Colors);

            switch (p_Codec)
            {
                case Codec.Bc1:
                    DecodeColorBlock(p_Data, s_Offset, s_Colors, true);
                    break;

                case Codec.Bc2:
                    DecodeColorBlock(p_Data, s_Offset + 8, s_Colors, false);
                    for (var i = 0; i < 16; i++)
                    {
                        var s_Nibble = p_Data[s_Offset + i / 2];
                        var s_Value = i % 2 == 0 ? s_Nibble & 0x0F : s_Nibble >> 4;
                        s_Colors[i * 4 + 3] = (byte) (s_Value * 17);
                    }

                    break;

                case Codec.Bc3:
                    DecodeColorBlock(p_Data, s_Offset + 8, s_Colors, false);
                    DecodeAlphaBlock(p_Data, s_Offset, s_Colors, 3);
                    break;

                case Codec.Bc4:
                    DecodeAlphaBlock(p_Data, s_Offset, s_Colors, 0);
                    for (var i = 0; i < 16; i++)
                    {
                        s_Colors[i * 4 + 1] = s_Colors[i * 4 + 0];
                        s_Colors[i * 4 + 2] = s_Colors[i * 4 + 0];
                        s_Colors[i * 4 + 3] = 255;
                    }

                    break;

                case Codec.Bc5:
                    // Two BC4 blocks: red then green. Blue is reconstructed so a normal map reads as a normal
                    // map rather than as a yellow smear.
                    DecodeAlphaBlock(p_Data, s_Offset, s_Colors, 2);
                    DecodeAlphaBlock(p_Data, s_Offset + 8, s_Colors, 1);
                    for (var i = 0; i < 16; i++)
                    {
                        var s_Nx = s_Colors[i * 4 + 2] / 255.0 * 2.0 - 1.0;
                        var s_Ny = s_Colors[i * 4 + 1] / 255.0 * 2.0 - 1.0;
                        var s_Nz = Math.Sqrt(Math.Max(0.0, 1.0 - s_Nx * s_Nx - s_Ny * s_Ny));
                        s_Colors[i * 4 + 0] = (byte) Math.Clamp((s_Nz * 0.5 + 0.5) * 255.0, 0, 255);
                        s_Colors[i * 4 + 3] = 255;
                    }

                    break;
            }

            for (var s_Py = 0; s_Py < 4; s_Py++)
            for (var s_Px = 0; s_Px < 4; s_Px++)
            {
                var s_X = s_Bx * 4 + s_Px;
                var s_Y = s_By * 4 + s_Py;
                if (s_X >= p_Width || s_Y >= p_Height)
                    continue;

                var s_Src = (s_Py * 4 + s_Px) * 4;
                var s_Dst = (s_Y * p_Width + s_X) * 4;
                s_Out[s_Dst + 0] = s_Colors[s_Src + 0];
                s_Out[s_Dst + 1] = s_Colors[s_Src + 1];
                s_Out[s_Dst + 2] = s_Colors[s_Src + 2];
                s_Out[s_Dst + 3] = s_Colors[s_Src + 3];
            }

            s_Offset += s_BlockBytes;
        }

        return s_Out;
    }

    private static void DecodeColorBlock(byte[] p_Data, int p_Offset, byte[] p_Out, bool p_AllowAlpha)
    {
        var s_C0 = (ushort) (p_Data[p_Offset] | (p_Data[p_Offset + 1] << 8));
        var s_C1 = (ushort) (p_Data[p_Offset + 2] | (p_Data[p_Offset + 3] << 8));

        var s_R = new byte[4];
        var s_G = new byte[4];
        var s_B = new byte[4];
        var s_A = new byte[4] { 255, 255, 255, 255 };

        Unpack565(s_C0, out s_R[0], out s_G[0], out s_B[0]);
        Unpack565(s_C1, out s_R[1], out s_G[1], out s_B[1]);

        if (s_C0 > s_C1 || !p_AllowAlpha)
        {
            s_R[2] = (byte) ((2 * s_R[0] + s_R[1]) / 3);
            s_G[2] = (byte) ((2 * s_G[0] + s_G[1]) / 3);
            s_B[2] = (byte) ((2 * s_B[0] + s_B[1]) / 3);
            s_R[3] = (byte) ((s_R[0] + 2 * s_R[1]) / 3);
            s_G[3] = (byte) ((s_G[0] + 2 * s_G[1]) / 3);
            s_B[3] = (byte) ((s_B[0] + 2 * s_B[1]) / 3);
        }
        else
        {
            s_R[2] = (byte) ((s_R[0] + s_R[1]) / 2);
            s_G[2] = (byte) ((s_G[0] + s_G[1]) / 2);
            s_B[2] = (byte) ((s_B[0] + s_B[1]) / 2);
            s_A[3] = 0;
        }

        for (var i = 0; i < 16; i++)
        {
            var s_Index = (p_Data[p_Offset + 4 + i / 4] >> (2 * (i % 4))) & 0x3;
            p_Out[i * 4 + 0] = s_B[s_Index];
            p_Out[i * 4 + 1] = s_G[s_Index];
            p_Out[i * 4 + 2] = s_R[s_Index];
            if (p_Out[i * 4 + 3] == 0)
                p_Out[i * 4 + 3] = s_A[s_Index];
        }
    }

    private static void DecodeAlphaBlock(byte[] p_Data, int p_Offset, byte[] p_Out, int p_Channel)
    {
        var s_A0 = p_Data[p_Offset];
        var s_A1 = p_Data[p_Offset + 1];
        var s_Values = new byte[8];
        s_Values[0] = s_A0;
        s_Values[1] = s_A1;

        if (s_A0 > s_A1)
        {
            for (var i = 0; i < 6; i++)
                s_Values[2 + i] = (byte) (((6 - i) * s_A0 + (1 + i) * s_A1) / 7);
        }
        else
        {
            for (var i = 0; i < 4; i++)
                s_Values[2 + i] = (byte) (((4 - i) * s_A0 + (1 + i) * s_A1) / 5);

            s_Values[6] = 0;
            s_Values[7] = 255;
        }

        ulong s_Bits = 0;
        for (var i = 0; i < 6; i++)
            s_Bits |= (ulong) p_Data[p_Offset + 2 + i] << (8 * i);

        for (var i = 0; i < 16; i++)
        {
            var s_Index = (int) ((s_Bits >> (3 * i)) & 0x7);
            p_Out[i * 4 + p_Channel] = s_Values[s_Index];
        }
    }

    private static void Unpack565(ushort p_Value, out byte p_R, out byte p_G, out byte p_B)
    {
        var s_R5 = (p_Value >> 11) & 0x1F;
        var s_G6 = (p_Value >> 5) & 0x3F;
        var s_B5 = p_Value & 0x1F;

        p_R = (byte) ((s_R5 * 255 + 15) / 31);
        p_G = (byte) ((s_G6 * 255 + 31) / 63);
        p_B = (byte) ((s_B5 * 255 + 15) / 31);
    }
}
