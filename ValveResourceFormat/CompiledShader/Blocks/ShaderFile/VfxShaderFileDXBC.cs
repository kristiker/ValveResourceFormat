using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// DirectX Bytecode (DXBC) shader file.
/// The DXBC sources only have one header, the offset (which happens to be equal to their source size).
/// </summary>
public class VfxShaderFileDXBC : VfxShaderFile
{
    /// <inheritdoc/>
    public override string BlockName => "DXBC";

    /// <summary>
    /// Initializes a new instance from a binary reader.
    /// </summary>
    public VfxShaderFileDXBC(BinaryReader datareader, int sourceId, VfxStaticComboData parent)
        : base(datareader, sourceId, parent)
    {
        if (Size > 0)
        {
            Bytecode = datareader.ReadBytes(Size);
        }

        HashMD5 = new Guid(datareader.ReadBytes(16));
    }

    /// <summary>
    /// Initializes a new instance with explicit size and hash.
    /// </summary>
    public VfxShaderFileDXBC(BinaryReader datareader, int sourceId, int size, Guid hash, VfxStaticComboData parent)
        : base(sourceId, parent)
    {
        Size = size;
        Bytecode = datareader.ReadBytes(size);
        HashMD5 = hash;
    }

    /// <summary>
    /// Reads the reflection data out of this blob's <c>RDEF</c> chunk, if it still has one.
    /// </summary>
    /// <remarks>
    /// This is the only place the original HLSL constant buffer member names survive, so it is what makes a
    /// Vulkan decompile's <c>_m0</c> members nameable. See <see cref="DxbcReflection"/>.
    /// </remarks>
    public bool TryGetReflection([NotNullWhen(true)] out DxbcReflection? reflection)
        => DxbcReflection.TryParse(Bytecode, out reflection);

    /// <inheritdoc/>
    /// <remarks>
    /// DXBC decompilation is not supported. The hash is useful for finding the shader in renderdoc, and the
    /// reflection listing recovers the real resource and constant buffer names when the blob kept them.
    /// </remarks>
    public override string GetDecompiledFile()
    {
        var header = $"Shader hash: {new Guid(Bytecode.AsSpan(4, 16))}\n\nDXBC decompilation is currently not supported.\n";

        if (!TryGetReflection(out var reflection))
        {
            return header + "\nThis blob carries no RDEF chunk, so it has no reflection data to show.\n";
        }

        return header + "\n" + reflection.ToStringListing();
    }
}
