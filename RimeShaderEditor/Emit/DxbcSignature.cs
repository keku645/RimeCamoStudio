using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RimeShaderEditor.Emit;

/// <summary>One entry of a shader's input or output signature.</summary>
public sealed class SignatureElement
{
    public string Semantic { get; init; } = "";
    public int Index { get; init; }
    public int Register { get; init; }

    /// <summary>Which components exist (bit 0 = x .. bit 3 = w).</summary>
    public byte Mask { get; init; }

    /// <summary>Which components the shader actually reads; 0 means declared but unused.</summary>
    public byte UsedMask { get; init; }

    public int ComponentType { get; init; }
    public int SystemValue { get; init; }

    public int Components => (Mask & 1) + ((Mask >> 1) & 1) + ((Mask >> 2) & 1) + ((Mask >> 3) & 1);
    public int UsedComponents => (UsedMask & 1) + ((UsedMask >> 1) & 1) + ((UsedMask >> 2) & 1) + ((UsedMask >> 3) & 1);

    /// <summary>Integer semantics are flat-interpolated selectors (a submaterial index), not values to blend.</summary>
    public bool IsInteger => ComponentType == 1 || ComponentType == 2;

    public string MaskText => Text(Mask);

    /// <summary>What the shader actually consumes. Interpolators are declared xyzw regardless, so this is the
    /// only field that describes the real layout.</summary>
    public string UsedMaskText => Text(UsedMask);

    private static string Text(byte p_Mask) => new(new[]
    {
        (p_Mask & 1) != 0 ? 'x' : '_', (p_Mask & 2) != 0 ? 'y' : '_',
        (p_Mask & 4) != 0 ? 'z' : '_', (p_Mask & 8) != 0 ? 'w' : '_',
    });

    public override string ToString() =>
        $"{Semantic}{Index} r{Register} {MaskText}{(IsInteger ? " int" : "")}";
}

/// <summary>
/// Reads the input/output signature straight out of a compiled shader's bytes.
///
/// Deliberately not via fxc: the container is a chunk table and the signature chunks are a flat record array, so
/// parsing them is instant and works on thousands of shaders, whereas spawning fxc per shader would take minutes
/// per level. It also means the editor can classify a target's contract with no game mount and no SDK installed.
/// </summary>
/// <summary>A texture or sampler the shader binds, with the register it actually sits in.</summary>
public sealed class ResourceBinding
{
    public string Name { get; init; } = "";
    public int Register { get; init; }
    public int InputType { get; init; }

    public bool IsTexture => InputType == 2;
    public bool IsSampler => InputType == 3;

    /// <summary>
    /// Names the ENGINE binds itself, observed across mp_017's families. Listed rather than pattern-matched
    /// because a first attempt ("material textures are the ones called texture_TextureN") wrongly threw out
    /// vegetation's texture_Diffuse and texture_Normal, which are the material's own.
    /// </summary>
    private static readonly string[] s_EngineNames =
    {
        "lightMap", "virtualTexture", "heightfieldAtlas", "globalColorMap", "dynamicMask", "maskMap",
        "irradiance", "depthBuffer", "skyEnvmap", "dynamicEnvmap", "gbufferTexture",
    };

    /// <summary>
    /// True when the engine supplies this texture, not the material. It matters because engine textures take the
    /// LOW registers on the shaders that use them: a lightmapped shader has the engine in t1..t4 and its own
    /// material starting at t5, which is exactly why "streamable order maps to t1, t2, ..." is false.
    /// </summary>
    public bool IsEngineTexture => IsTexture && s_EngineNames.Any(
        p_N => Name.Contains(p_N, StringComparison.OrdinalIgnoreCase));

    public bool IsMaterialTexture => IsTexture && !IsEngineTexture;

    /// <summary>
    /// The 1-based slot the name encodes for the texture_TextureN convention (texture_Texture = 1,
    /// texture_Texture3 = 3). That number is an IDENTITY, not a register — the parachute has texture_Texture7 in
    /// t1 and texture_Texture in t3 — and it is what lines up with the shaderdb's StreamableTextures order.
    /// Returns 0 for descriptively named material textures (vegetation's texture_Diffuse), which have no slot
    /// number to go on; those fall back to register order.
    /// </summary>
    public int MaterialSlot
    {
        get
        {
            if (!IsMaterialTexture ||
                !Name.StartsWith("texture_Texture", StringComparison.OrdinalIgnoreCase))
                return 0;

            var s_Digits = Name["texture_Texture".Length..];
            return s_Digits.Length == 0 ? 1 : int.TryParse(s_Digits, out var s_N) ? s_N : 0;
        }
    }

    public override string ToString() => $"{Name} @t{Register}";
}

public static class DxbcSignature
{
    private const uint c_Magic = 0x43425844; // "DXBC"

    /// <summary>
    /// Reads the resource bindings out of the RDEF chunk: which texture sits in which register, by name.
    ///
    /// This is the ONLY reliable source for a streamable texture's register. The shaderdb does not record it
    /// (streamables resolve by name at draw time), and "declaration order maps to t1, t2, ..." is FALSE in
    /// general — it happened to hold for the oil drum, but a shader that also uses lightmaps has the engine
    /// occupying t1..t4 and its own textures starting at t5.
    /// </summary>
    public static List<ResourceBinding> ReadResources(byte[] p_Dxbc)
    {
        var s_Result = new List<ResourceBinding>();
        var s_Chunk = FindChunk(p_Dxbc, "RDEF");
        if (s_Chunk < 0)
            return s_Result;

        var s_Data = s_Chunk + 8;
        if (s_Data + 16 > p_Dxbc.Length)
            return s_Result;

        var s_Count = BitConverter.ToInt32(p_Dxbc, s_Data + 8);
        var s_Offset = BitConverter.ToInt32(p_Dxbc, s_Data + 12);
        if (s_Count < 0 || s_Count > 128)
            return s_Result;

        for (var i = 0; i < s_Count; i++)
        {
            var s_Record = s_Data + s_Offset + i * 32;
            if (s_Record + 32 > p_Dxbc.Length)
                break;

            s_Result.Add(new ResourceBinding
            {
                Name = ReadAscii(p_Dxbc, s_Data + BitConverter.ToInt32(p_Dxbc, s_Record)),
                InputType = BitConverter.ToInt32(p_Dxbc, s_Record + 4),
                Register = BitConverter.ToInt32(p_Dxbc, s_Record + 20),
            });
        }

        return s_Result;
    }

    /// <summary>
    /// One named field of a constant buffer, as DICE's own compiler recorded it.
    ///
    /// This is the authoring vocabulary the shader graph was built from, surviving inside the bytecode: a
    /// `cbuffer externalConstants { float external_Specular; float external_Smoothness; }` is literally the
    /// artist's pin list. 192 distinct names across mp_017, and they match the ParameterName strings in EBX
    /// down to DICE's own typo (external_DiffuseBrighness).
    /// </summary>
    public sealed class ConstantField
    {
        public string Buffer { get; init; } = "";
        public string Name { get; init; } = "";

        /// <summary>Which cbN the buffer is bound to, joined from the resource table by name.</summary>
        public int Register { get; init; }

        /// <summary>Byte offset inside the buffer. Divided by 16 it is the cbN[element] the disassembly reads.</summary>
        public int Offset { get; init; }

        public int Size { get; init; }

        /// <summary>The element index a `cbN[M]` operand refers to.</summary>
        public int Element => Offset / 16;

        /// <summary>Which component of that element this field starts at, for fields packed inside one register.</summary>
        public int Component => Offset % 16 / 4;
    }

    /// <summary>
    /// Reads the constant-buffer variable table out of RDEF. Without it every cb read had to become a literal 0,
    /// which is what blocked 364 of mp_017's 625 shaders - not a missing opcode, just a table nobody parsed.
    /// </summary>
    public static List<ConstantField> ReadConstantFields(byte[] p_Dxbc)
    {
        var s_Result = new List<ConstantField>();
        var s_Chunk = FindChunk(p_Dxbc, "RDEF");
        if (s_Chunk < 0)
            return s_Result;

        var s_Data = s_Chunk + 8;
        if (s_Data + 32 > p_Dxbc.Length)
            return s_Result;

        var s_BufferCount = BitConverter.ToInt32(p_Dxbc, s_Data);
        var s_BufferOffset = BitConverter.ToInt32(p_Dxbc, s_Data + 4);
        if (s_BufferCount <= 0 || s_BufferCount > 64)
            return s_Result;

        // ⚠ The variable record grew from 24 to 40 bytes at shader model 5. Guessing the stride does not fail
        // loudly - it walks off into neighbouring bytes and yields plausible garbage names - so it is read from
        // the version byte the chunk carries.
        var s_MajorVersion = p_Dxbc[s_Data + 17];
        var s_Stride = s_MajorVersion >= 5 ? 40 : 24;

        // The register lives in the bound-resource table, not in the buffer record; they join by name.
        var s_Registers = ReadResources(p_Dxbc)
            .GroupBy(p_R => p_R.Name, StringComparer.Ordinal)
            .ToDictionary(p_G => p_G.Key, p_G => p_G.First().Register, StringComparer.Ordinal);

        for (var i = 0; i < s_BufferCount; i++)
        {
            var s_Record = s_Data + s_BufferOffset + i * 24;
            if (s_Record + 24 > p_Dxbc.Length)
                break;

            var s_BufferName = ReadAscii(p_Dxbc, s_Data + BitConverter.ToInt32(p_Dxbc, s_Record));
            var s_VariableCount = BitConverter.ToInt32(p_Dxbc, s_Record + 4);
            var s_VariableOffset = BitConverter.ToInt32(p_Dxbc, s_Record + 8);
            if (s_VariableCount < 0 || s_VariableCount > 512)
                continue;

            for (var j = 0; j < s_VariableCount; j++)
            {
                var s_Variable = s_Data + s_VariableOffset + j * s_Stride;
                if (s_Variable + 24 > p_Dxbc.Length)
                    break;

                s_Result.Add(new ConstantField
                {
                    Buffer = s_BufferName,
                    Name = ReadAscii(p_Dxbc, s_Data + BitConverter.ToInt32(p_Dxbc, s_Variable)),
                    Register = s_Registers.TryGetValue(s_BufferName, out var s_Reg) ? s_Reg : -1,
                    Offset = BitConverter.ToInt32(p_Dxbc, s_Variable + 4),
                    Size = BitConverter.ToInt32(p_Dxbc, s_Variable + 8),
                });
            }
        }

        return s_Result;
    }

    private static int FindChunk(byte[] p_Dxbc, string p_FourCc)
    {
        if (p_Dxbc.Length < 32 || BitConverter.ToUInt32(p_Dxbc, 0) != c_Magic)
            return -1;

        var s_ChunkCount = BitConverter.ToInt32(p_Dxbc, 28);
        if (s_ChunkCount <= 0 || s_ChunkCount > 64)
            return -1;

        for (var i = 0; i < s_ChunkCount; i++)
        {
            var s_TableOffset = 32 + i * 4;
            if (s_TableOffset + 4 > p_Dxbc.Length)
                break;

            var s_Chunk = BitConverter.ToInt32(p_Dxbc, s_TableOffset);
            if (s_Chunk < 0 || s_Chunk + 8 > p_Dxbc.Length)
                continue;

            if (Encoding.ASCII.GetString(p_Dxbc, s_Chunk, 4) == p_FourCc)
                return s_Chunk;
        }

        return -1;
    }

    /// <summary>
    /// Which SAMPLER each texture register is read through, taken from the shader body's own sample
    /// instructions.
    ///
    /// ⛔ THE PAIRING IS NOT DERIVABLE FROM THE BINDINGS, AND IT DECIDES ADDRESSING. Sampler state is what
    /// makes a texture tile or clamp, and a flavour spreads its textures across several samplers: measured
    /// on one shader's own permutations, the plain flavour reads t1..t5 through s0,s1,s2,s2,s2 while the
    /// lightmapped one reads its engine textures through s0 and its material through s1,s2,s3,s3,s3. Reading
    /// a tiling detail texture through the engine's sampler stretches one texel over the whole surface,
    /// which looks like broad diagonal bands — not like a missing texture.
    ///
    /// Walks the SM4/5 instruction stream directly: the container gives no such table, and shelling out to
    /// the compiler for a disassembly would put a toolchain dependency in the middle of emitting.
    /// </summary>
    public static Dictionary<int, int> ReadSamplerPairs(byte[] p_Dxbc)
    {
        var s_Result = new Dictionary<int, int>();
        var s_Chunk = FindChunk(p_Dxbc, "SHEX");
        if (s_Chunk < 0)
            s_Chunk = FindChunk(p_Dxbc, "SHDR");

        if (s_Chunk < 0)
            return s_Result;

        var s_Size = BitConverter.ToInt32(p_Dxbc, s_Chunk + 4);
        var s_Body = s_Chunk + 8;
        if (s_Body + s_Size > p_Dxbc.Length || s_Size < 8)
            return s_Result;

        uint Dw(int p_Index) => BitConverter.ToUInt32(p_Dxbc, s_Body + p_Index * 4);

        var s_Total = s_Size / 4;

        // [0] version, [1] length in dwords, then instructions.
        for (var i = 2; i < s_Total;)
        {
            var s_Token = Dw(i);
            var s_Op = s_Token & 0x7FF;
            var s_Length = (int) ((s_Token >> 24) & 0x7F);
            if (s_Length == 0)
                break;

            // The sample family: every one of them names a resource and a sampler.
            if (s_Op is >= 69 and <= 76 or 78 or 79)
            {
                int? s_Texture = null, s_Sampler = null;

                for (var j = i + 1; j < i + s_Length && j < s_Total;)
                {
                    var s_Operand = Dw(j);
                    var s_Type = (s_Operand >> 12) & 0xFF;
                    var s_Dimension = (int) ((s_Operand >> 20) & 3);
                    j++;

                    if (s_Dimension >= 1 && j < s_Total)
                    {
                        var s_Value = (int) Dw(j);
                        if (s_Type == 7 && s_Texture == null)
                            s_Texture = s_Value;
                        else if (s_Type == 6 && s_Sampler == null)
                            s_Sampler = s_Value;
                    }

                    j += s_Dimension;
                }

                if (s_Texture is { } s_T && s_Sampler is { } s_S)
                    s_Result[s_T] = s_S;
            }

            i += s_Length;
        }

        return s_Result;
    }

    public static List<SignatureElement> ReadInput(byte[] p_Dxbc) => Read(p_Dxbc, "ISGN", "ISG1");

    public static List<SignatureElement> ReadOutput(byte[] p_Dxbc) => Read(p_Dxbc, "OSGN", "OSG1", "OSG5");

    private static List<SignatureElement> Read(byte[] p_Dxbc, params string[] p_FourCcs)
    {
        var s_Result = new List<SignatureElement>();
        if (p_Dxbc.Length < 32 || BitConverter.ToUInt32(p_Dxbc, 0) != c_Magic)
            return s_Result;

        var s_ChunkCount = BitConverter.ToInt32(p_Dxbc, 28);
        if (s_ChunkCount <= 0 || s_ChunkCount > 64)
            return s_Result;

        for (var i = 0; i < s_ChunkCount; i++)
        {
            var s_TableOffset = 32 + i * 4;
            if (s_TableOffset + 4 > p_Dxbc.Length)
                break;

            var s_Chunk = BitConverter.ToInt32(p_Dxbc, s_TableOffset);
            if (s_Chunk < 0 || s_Chunk + 8 > p_Dxbc.Length)
                continue;

            var s_FourCc = Encoding.ASCII.GetString(p_Dxbc, s_Chunk, 4);
            if (!p_FourCcs.Contains(s_FourCc))
                continue;

            var s_Data = s_Chunk + 8;
            if (s_Data + 8 > p_Dxbc.Length)
                continue;

            var s_Count = BitConverter.ToInt32(p_Dxbc, s_Data);
            if (s_Count < 0 || s_Count > 64)
                continue;

            // Records start after the count and a fixed field; name offsets are relative to the chunk data.
            for (var e = 0; e < s_Count; e++)
            {
                var s_Record = s_Data + 8 + e * 24;
                if (s_Record + 24 > p_Dxbc.Length)
                    break;

                var s_NameOffset = s_Data + BitConverter.ToInt32(p_Dxbc, s_Record);
                s_Result.Add(new SignatureElement
                {
                    Semantic = ReadAscii(p_Dxbc, s_NameOffset),
                    Index = BitConverter.ToInt32(p_Dxbc, s_Record + 4),
                    SystemValue = BitConverter.ToInt32(p_Dxbc, s_Record + 8),
                    ComponentType = BitConverter.ToInt32(p_Dxbc, s_Record + 12),
                    Register = BitConverter.ToInt32(p_Dxbc, s_Record + 16),
                    Mask = s_Record + 20 < p_Dxbc.Length ? p_Dxbc[s_Record + 20] : (byte) 0,
                    UsedMask = s_Record + 21 < p_Dxbc.Length ? p_Dxbc[s_Record + 21] : (byte) 0,
                });
            }

            break;
        }

        return s_Result;
    }

    private static string ReadAscii(byte[] p_Bytes, int p_Offset)
    {
        if (p_Offset < 0 || p_Offset >= p_Bytes.Length)
            return "";

        var s_End = p_Offset;
        while (s_End < p_Bytes.Length && p_Bytes[s_End] != 0)
            s_End++;

        return Encoding.ASCII.GetString(p_Bytes, p_Offset, s_End - p_Offset);
    }
}
