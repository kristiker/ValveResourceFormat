namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// One compiled shader variant: the bytecode a single static and dynamic combo pair resolves to.
/// </summary>
/// <param name="StaticComboId">The static combo ID.</param>
/// <param name="DynamicComboId">The dynamic combo ID.</param>
/// <param name="StaticCombos">The non-default static combo values, formatted for display.</param>
/// <param name="DynamicCombos">The non-default dynamic combo values, formatted for display.</param>
/// <param name="ShaderFile">The bytecode this combo pair selects.</param>
public readonly record struct VfxShaderVariant(
    long StaticComboId,
    long DynamicComboId,
    string StaticCombos,
    string DynamicCombos,
    VfxShaderFile ShaderFile);

/// <summary>
/// Walks the compiled variants of a shader program.
/// </summary>
public static class VfxComboResolver
{
    /// <summary>
    /// Enumerates every compiled variant in the program, in static then dynamic combo order.
    /// </summary>
    /// <remarks>
    /// Static combos are read lazily through <see cref="VfxProgramData.StaticComboCache"/>, which evicts on a
    /// size limit. Consume each variant before advancing the enumerator, or raise the cache capacity first.
    /// </remarks>
    public static IEnumerable<VfxShaderVariant> EnumerateVariants(VfxProgramData program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var staticMapping = new ComboConfigMapping(program);

        foreach (var entry in program.StaticComboEntries)
        {
            var staticCombo = program.StaticComboCache.Get(entry.Key);
            var statics = ShaderUtilHelpers.FormatComboState(program.StaticComboArray, staticMapping.GetConfigState(entry.Key));

            foreach (var renderState in staticCombo.DynamicComboRenderStates)
            {
                if (renderState.ShaderFileId < 0 || renderState.ShaderFileId >= staticCombo.ShaderFiles.Length)
                {
                    continue;
                }

                var shaderFile = staticCombo.ShaderFiles[renderState.ShaderFileId];

                if (shaderFile == null)
                {
                    continue;
                }

                var dynamics = program.DynamicComboArray.Length > 0
                    ? ShaderUtilHelpers.FormatComboState(program.DynamicComboArray, program.GetDynamicComboConfig(renderState.DynamicComboId))
                    : string.Empty;

                yield return new VfxShaderVariant(entry.Key, renderState.DynamicComboId, statics, dynamics, shaderFile);
            }
        }
    }
}
