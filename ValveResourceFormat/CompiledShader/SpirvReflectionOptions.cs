namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// Controls how decompiled SPIR-V source is post-processed.
/// </summary>
/// <remarks>
/// <see cref="Default"/> leaves SPIRV-Cross output untouched. The clean-up passes are opt-in because they
/// rewrite identifiers, which is desirable when comparing many combos of the same shader against each other,
/// but changes output that callers may be diffing against a known reference.
/// </remarks>
public readonly record struct SpirvReflectionOptions
{
    /// <summary>
    /// Gets the options that reproduce raw SPIRV-Cross output.
    /// </summary>
    public static SpirvReflectionOptions Default => new();

    /// <summary>
    /// Gets the options that produce output suited for comparing combos of the same shader.
    /// </summary>
    public static SpirvReflectionOptions Clean => new()
    {
        NormalizeIdentifiers = true,
        StripBufferPrefixes = true,
        SimplifyMatrixConstruction = true,
    };

    /// <summary>
    /// Gets whether SPIR-V id derived names (<c>_25207</c>, <c>_1ident</c>, <c>struct _730</c>) are renamed to
    /// names assigned in order of first appearance.
    /// </summary>
    /// <remarks>
    /// SPIR-V ids differ between combos of the same shader even when the emitted code is identical, which makes
    /// two equivalent combos look entirely different. Renaming them makes equivalent output textually equal.
    /// </remarks>
    public bool NormalizeIdentifiers { get; init; }

    /// <summary>
    /// Gets whether constant buffer members are stripped of their <c>&lt;BufferName&gt;_&lt;id&gt;_</c> prefix,
    /// so that <c>PerViewConstantBuffer_t_1_g_matWorldToProjection</c> reads as <c>g_matWorldToProjection</c>.
    /// </summary>
    public bool StripBufferPrefixes { get; init; }

    /// <summary>
    /// Gets whether the component-wise 4x4 matrix transpose that SPIRV-Cross emits for row major matrix loads
    /// is folded back into a <c>transpose()</c> call.
    /// </summary>
    public bool SimplifyMatrixConstruction { get; init; }

    /// <summary>
    /// Gets whether the generator and bytecode size header comment is emitted. Defaults to true.
    /// </summary>
    /// <remarks>The static and dynamic combo comments are always emitted, they identify which variant this is.</remarks>
    public bool EmitGeneratorComment { get; init; } = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="SpirvReflectionOptions"/> struct.
    /// </summary>
    public SpirvReflectionOptions()
    {
    }

    /// <summary>
    /// Gets whether any source rewriting pass is enabled.
    /// </summary>
    public bool HasAnyRewrite => NormalizeIdentifiers || StripBufferPrefixes || SimplifyMatrixConstruction;
}
