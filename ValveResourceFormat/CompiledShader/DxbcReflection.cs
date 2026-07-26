using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// Reflection data recovered from the <c>RDEF</c> chunk of a DXBC shader blob.
/// </summary>
/// <remarks>
/// VCS 71 does not store constant buffer member names, and SPIRV-Cross cannot invent them, so a Vulkan
/// decompile names members <c>_m0</c>, <c>_m1</c> and so on. The DirectX build of the same shader keeps the
/// original HLSL names in its reflection chunk, and because both builds compile the same source the register
/// offsets agree. Reading the <c>_pc_</c> variant of a shader therefore recovers the real names for its
/// <c>_vulkan_</c> counterpart.
///
/// Two caveats. Reflection is stripped per program, not per shader: a shader's vertex program may keep it
/// while its pixel program does not, in which case the missing names can often still be read from whichever
/// program declares the same buffer. And bind points are not transferable, because the DirectX and Vulkan
/// backends assign registers independently; only names and register offsets carry over.
/// </remarks>
public sealed class DxbcReflection
{
    private const int ContainerHeaderSize = 32;
    private const int ConstantBufferDescriptorSize = 24;
    private const int ResourceBindingDescriptorSize = 32;
    private const int ShaderModel4VariableDescriptorSize = 24;
    private const int ShaderModel5VariableDescriptorSize = 40;

    /// <summary>
    /// Gets the shader model major version the chunk declares.
    /// </summary>
    public int ShaderModelMajor { get; private init; }

    /// <summary>
    /// Gets the shader model minor version the chunk declares.
    /// </summary>
    public int ShaderModelMinor { get; private init; }

    /// <summary>
    /// Gets the compiler that produced the blob.
    /// </summary>
    public string Creator { get; private init; } = string.Empty;

    /// <summary>
    /// Gets every constant buffer the shader declares, including members it never reads.
    /// </summary>
    public IReadOnlyList<DxbcConstantBuffer> ConstantBuffers { get; private init; } = [];

    /// <summary>
    /// Gets every texture, sampler and buffer the shader binds.
    /// </summary>
    public IReadOnlyList<DxbcResourceBinding> ResourceBindings { get; private init; } = [];

    /// <summary>
    /// Reads the reflection chunk of a DXBC blob.
    /// </summary>
    /// <param name="bytecode">A complete DXBC container.</param>
    /// <param name="reflection">The parsed reflection data, or <c>null</c> when the blob carries none.</param>
    /// <returns>
    /// <c>true</c> if the blob is DXBC and retains an <c>RDEF</c> chunk. Most shipped blobs have the chunk
    /// stripped, so a <c>false</c> return is expected rather than exceptional.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<byte> bytecode, [NotNullWhen(true)] out DxbcReflection? reflection)
    {
        reflection = null;

        if (!TryFindChunk(bytecode, "RDEF", out var chunk))
        {
            return false;
        }

        try
        {
            reflection = Parse(chunk);
            return true;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            // A truncated or unexpected chunk layout is not worth propagating: the caller only ever wants to
            // know whether names are available.
            reflection = null;
            return false;
        }
    }

    private static bool TryFindChunk(ReadOnlySpan<byte> bytecode, string fourCc, out ReadOnlySpan<byte> chunk)
    {
        chunk = default;

        if (bytecode.Length < ContainerHeaderSize || !bytecode[..4].SequenceEqual("DXBC"u8))
        {
            return false;
        }

        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bytecode[28..]);
        var tableEnd = ContainerHeaderSize + (chunkCount * 4);

        if (chunkCount < 0 || tableEnd > bytecode.Length)
        {
            return false;
        }

        var wanted = Encoding.ASCII.GetBytes(fourCc);

        for (var i = 0; i < chunkCount; i++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(bytecode[(ContainerHeaderSize + (i * 4))..]);

            if (offset < 0 || offset + 8 > bytecode.Length)
            {
                continue;
            }

            if (!bytecode.Slice(offset, 4).SequenceEqual(wanted))
            {
                continue;
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(bytecode[(offset + 4)..]);

            if (size < 0 || offset + 8 + size > bytecode.Length)
            {
                return false;
            }

            // Offsets inside the chunk are relative to the start of its payload.
            chunk = bytecode.Slice(offset + 8, size);
            return true;
        }

        return false;
    }

    private static DxbcReflection Parse(ReadOnlySpan<byte> chunk)
    {
        var constantBufferCount = BinaryPrimitives.ReadInt32LittleEndian(chunk);
        var constantBufferOffset = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
        var resourceCount = BinaryPrimitives.ReadInt32LittleEndian(chunk[8..]);
        var resourceOffset = BinaryPrimitives.ReadInt32LittleEndian(chunk[12..]);
        var minor = chunk[16];
        var major = chunk[17];
        var creatorOffset = BinaryPrimitives.ReadInt32LittleEndian(chunk[24..]);

        var variableDescriptorSize = major >= 5
            ? ShaderModel5VariableDescriptorSize
            : ShaderModel4VariableDescriptorSize;

        // Shader model 5 appends an "RD11" header whose fourth entry is the authoritative variable
        // descriptor size. Trust it over the constant above when it is present.
        if (major >= 5 && chunk.Length >= 60 && chunk.Slice(28, 4).SequenceEqual("RD11"u8))
        {
            var declared = BinaryPrimitives.ReadInt32LittleEndian(chunk[44..]);

            if (declared > 0)
            {
                variableDescriptorSize = declared;
            }
        }

        var constantBuffers = new List<DxbcConstantBuffer>(Math.Max(0, constantBufferCount));

        for (var i = 0; i < constantBufferCount; i++)
        {
            var descriptor = chunk[(constantBufferOffset + (i * ConstantBufferDescriptorSize))..];
            var name = ReadString(chunk, BinaryPrimitives.ReadInt32LittleEndian(descriptor));
            var memberCount = BinaryPrimitives.ReadInt32LittleEndian(descriptor[4..]);
            var memberOffset = BinaryPrimitives.ReadInt32LittleEndian(descriptor[8..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(descriptor[12..]);

            var members = new List<DxbcConstantBufferMember>(Math.Max(0, memberCount));

            for (var m = 0; m < memberCount; m++)
            {
                var member = chunk[(memberOffset + (m * variableDescriptorSize))..];
                var flags = BinaryPrimitives.ReadInt32LittleEndian(member[12..]);

                members.Add(new DxbcConstantBufferMember(
                    ReadString(chunk, BinaryPrimitives.ReadInt32LittleEndian(member)),
                    BinaryPrimitives.ReadInt32LittleEndian(member[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(member[8..]),
                    (flags & 2) != 0));
            }

            constantBuffers.Add(new DxbcConstantBuffer(name, size, members));
        }

        var resourceBindings = new List<DxbcResourceBinding>(Math.Max(0, resourceCount));

        for (var i = 0; i < resourceCount; i++)
        {
            var descriptor = chunk[(resourceOffset + (i * ResourceBindingDescriptorSize))..];

            resourceBindings.Add(new DxbcResourceBinding(
                ReadString(chunk, BinaryPrimitives.ReadInt32LittleEndian(descriptor)),
                (DxbcResourceType)BinaryPrimitives.ReadInt32LittleEndian(descriptor[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(descriptor[20..]),
                BinaryPrimitives.ReadInt32LittleEndian(descriptor[24..])));
        }

        return new DxbcReflection
        {
            ShaderModelMajor = major,
            ShaderModelMinor = minor,
            Creator = ReadString(chunk, creatorOffset),
            ConstantBuffers = constantBuffers,
            ResourceBindings = resourceBindings,
        };
    }

    private static string ReadString(ReadOnlySpan<byte> chunk, int offset)
    {
        if (offset < 0 || offset >= chunk.Length)
        {
            return string.Empty;
        }

        var rest = chunk[offset..];
        var end = rest.IndexOf((byte)0);

        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]);
    }

    /// <summary>
    /// Formats the reflection data as an annotated listing.
    /// </summary>
    /// <remarks>
    /// The constant buffer members are written in the same <c>cN.lane</c> form the HLSL backend emits as a
    /// <c>packoffset</c>, so the listing can be lined up against a decompile directly.
    /// </remarks>
    public string ToStringListing()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"// Reflection from the DXBC RDEF chunk: shader model {ShaderModelMajor}.{ShaderModelMinor}, {Creator}");
        sb.AppendLine("// Names and register offsets are the ones the original HLSL used. Bind points below are");
        sb.AppendLine("// DirectX registers and do not carry over to a Vulkan build.");

        if (ResourceBindings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Resource bindings:");

            foreach (var binding in ResourceBindings)
            {
                var count = binding.BindCount > 1 ? $"[{binding.BindCount}]" : string.Empty;
                sb.AppendLine($"    {binding.Register,-5} {binding.Type,-16} {binding.Name}{count}");
            }
        }

        foreach (var buffer in ConstantBuffers)
        {
            sb.AppendLine();
            sb.AppendLine($"cbuffer {buffer.Name}  // {buffer.Size} bytes, {buffer.Members.Count} members");

            foreach (var member in buffer.Members)
            {
                var used = member.IsUsed ? "     " : " (--)";
                sb.AppendLine($"    {member.PackOffset,-7}{used} {member.Size,4} bytes  {member.Name}");
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// A constant buffer declared by a DXBC shader.
/// </summary>
/// <param name="Name">The buffer's HLSL name.</param>
/// <param name="Size">The buffer's size in bytes.</param>
/// <param name="Members">Every member the source declared, whether or not the shader reads it.</param>
public sealed record DxbcConstantBuffer(string Name, int Size, IReadOnlyList<DxbcConstantBufferMember> Members);

/// <summary>
/// One member of a DXBC constant buffer.
/// </summary>
/// <param name="Name">The member's HLSL name.</param>
/// <param name="Offset">The member's byte offset into the buffer.</param>
/// <param name="Size">The member's size in bytes.</param>
/// <param name="IsUsed">Whether this particular shader reads the member.</param>
public sealed record DxbcConstantBufferMember(string Name, int Offset, int Size, bool IsUsed)
{
    /// <summary>
    /// Gets the member's location as a <c>packoffset</c>, for example <c>c11.z</c>.
    /// </summary>
    public string PackOffset => (Offset % 16) switch
    {
        0 => $"c{Offset / 16}",
        4 => $"c{Offset / 16}.y",
        8 => $"c{Offset / 16}.z",
        12 => $"c{Offset / 16}.w",
        _ => $"c{Offset / 16}+{Offset % 16}",
    };
}

/// <summary>
/// A texture, sampler or buffer bound by a DXBC shader.
/// </summary>
/// <param name="Name">The resource's HLSL name.</param>
/// <param name="Type">The resource's kind.</param>
/// <param name="BindPoint">The DirectX register index the resource binds to.</param>
/// <param name="BindCount">How many consecutive registers the resource occupies.</param>
public sealed record DxbcResourceBinding(string Name, DxbcResourceType Type, int BindPoint, int BindCount)
{
    /// <summary>
    /// Gets the resource's register in the usual HLSL notation, for example <c>t13</c>.
    /// </summary>
    public string Register => $"{Type.RegisterPrefix()}{BindPoint}";
}

/// <summary>
/// The kinds of resource a DXBC shader can bind, as encoded by <c>D3D_SHADER_INPUT_TYPE</c>.
/// </summary>
public enum DxbcResourceType
{
#pragma warning disable CS1591
    CBuffer = 0,
    TBuffer = 1,
    Texture = 2,
    Sampler = 3,
    UavRwTyped = 4,
    Structured = 5,
    UavRwStructured = 6,
    ByteAddress = 7,
    UavRwByteAddress = 8,
    UavAppendStructured = 9,
    UavConsumeStructured = 10,
    UavRwStructuredWithCounter = 11,
#pragma warning restore CS1591
}

/// <summary>
/// Extensions for <see cref="DxbcResourceType"/>.
/// </summary>
public static class DxbcResourceTypeExtensions
{
    /// <summary>
    /// Gets the HLSL register prefix a resource of this kind binds to.
    /// </summary>
    public static string RegisterPrefix(this DxbcResourceType type) => type switch
    {
        DxbcResourceType.CBuffer => "b",
        DxbcResourceType.Sampler => "s",
        DxbcResourceType.Texture or DxbcResourceType.TBuffer or DxbcResourceType.Structured
            or DxbcResourceType.ByteAddress => "t",
        _ => "u",
    };
}
