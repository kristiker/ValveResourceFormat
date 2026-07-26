using System.Globalization;
using System.Linq;
using ValveResourceFormat.IO;

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
/// Resolves shader combos by name, so that a variant can be picked with values such as
/// <c>S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4</c> instead of raw combo IDs.
/// </summary>
public static class VfxComboResolver
{
    /// <summary>
    /// Formats the non-default static combo values of a static combo ID, for example <c>S_MODE_DEPTH, S_DETAIL=2</c>.
    /// </summary>
    /// <returns>The formatted values, or an empty string when every combo is at zero.</returns>
    public static string FormatStaticCombos(VfxProgramData program, long staticComboId)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.StaticComboArray.Length == 0)
        {
            return string.Empty;
        }

        var configGen = new ConfigMappingParams(program);
        return FormatCombos(program.StaticComboArray, configGen.GetConfigState(staticComboId));
    }

    /// <summary>
    /// Formats the non-default dynamic combo values of a dynamic combo ID, for example <c>D_BLEND_WEIGHT_COUNT=4</c>.
    /// </summary>
    /// <returns>The formatted values, or an empty string when every combo is at zero.</returns>
    public static string FormatDynamicCombos(VfxProgramData program, long dynamicComboId)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.DynamicComboArray.Length == 0 || dynamicComboId == 0)
        {
            return string.Empty;
        }

        return FormatCombos(program.DynamicComboArray, program.GetDBlockConfig(dynamicComboId));
    }

    private static string FormatCombos(VfxCombo[] combos, int[] state)
    {
        var parts = new List<string>();

        for (var i = 0; i < state.Length; i++)
        {
            if (state[i] == 0)
            {
                continue;
            }

            parts.Add(state[i] != 1
                ? $"{combos[i].Name}={state[i].ToString(CultureInfo.InvariantCulture)}"
                : combos[i].Name);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Resolves a combo specification such as <c>S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4</c> into combo IDs.
    /// </summary>
    /// <param name="program">The shader program to resolve against.</param>
    /// <param name="comboSpec">
    /// Comma separated <c>NAME=VALUE</c> pairs, mixing static and dynamic combos freely. A bare name means
    /// <c>=1</c>, and any combo left out stays at its minimum value.
    /// </param>
    /// <param name="staticComboId">The resolved static combo ID.</param>
    /// <param name="dynamicComboId">The resolved dynamic combo ID.</param>
    /// <param name="error">
    /// The reason resolution failed, or a warning describing how the requested static configuration had to be
    /// reduced to satisfy the shader's combo rules. Empty when the spec resolved exactly.
    /// </param>
    /// <returns>True when both IDs were resolved. The dynamic combo is not checked for existence, use
    /// <see cref="VfxStaticComboData.GetDynamicComboIndex"/> for that.</returns>
    public static bool TryResolve(VfxProgramData program, string? comboSpec,
        out long staticComboId, out long dynamicComboId, out string error)
    {
        ArgumentNullException.ThrowIfNull(program);

        staticComboId = 0;
        dynamicComboId = 0;
        error = string.Empty;

        var staticValues = GetDefaultValues(program.StaticComboArray);
        var dynamicValues = GetDefaultValues(program.DynamicComboArray);

        foreach (var entry in (comboSpec ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var name = separator == -1 ? entry : entry[..separator].TrimEnd();
            var valueText = separator == -1 ? "1" : entry[(separator + 1)..].TrimStart();

            if (!int.TryParse(valueText, CultureInfo.InvariantCulture, out var value))
            {
                error = $"'{valueText}' is not a valid value for combo '{name}'.";
                return false;
            }

            var combo = Array.Find(program.StaticComboArray, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var values = staticValues;

            if (combo == null)
            {
                combo = Array.Find(program.DynamicComboArray, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                values = dynamicValues;
            }

            if (combo == null)
            {
                var known = program.StaticComboArray.Concat(program.DynamicComboArray).Select(c => c.Name);
                error = $"Unknown combo '{name}'. This shader has: {string.Join(", ", known)}";
                return false;
            }

            if (value < combo.RangeMin || value > combo.RangeMax)
            {
                error = $"Value {value} is out of range for combo '{combo.Name}' ({combo.RangeMin}..{combo.RangeMax}).";
                return false;
            }

            values[combo.BlockIndex] = value;
        }

        var configGen = new ConfigMappingParams(program);
        staticComboId = configGen.CalcStaticComboIdFromValues(staticValues);

        // Combos that violate the shader's rules are not compiled, so there is no bytecode to point at.
        // Reduce the configuration the same way the material driven lookup does, and say so.
        if (!program.StaticComboEntries.ContainsKey(staticComboId)
            && ShaderDataProvider.TryReduceStaticConfiguration(program, staticValues, out var reduced))
        {
            var reducedComboId = configGen.CalcStaticComboIdFromValues(reduced);

            if (program.StaticComboEntries.ContainsKey(reducedComboId))
            {
                error = $"Requested static combo ({FormatCombos(program.StaticComboArray, staticValues)}) violates the shader's combo rules, "
                    + $"using ({FormatCombos(program.StaticComboArray, reduced)}) instead.";
                staticComboId = reducedComboId;
            }
        }

        if (!program.StaticComboEntries.ContainsKey(staticComboId))
        {
            error = $"This shader has no static combo 0x{staticComboId:x08} ({FormatCombos(program.StaticComboArray, staticValues)}).";
            return false;
        }

        dynamicComboId = program.CalcDynamicComboIdFromValues(dynamicValues);
        return true;

        static int[] GetDefaultValues(VfxCombo[] combos)
        {
            var values = new int[combos.Length];

            for (var i = 0; i < combos.Length; i++)
            {
                values[i] = combos[i].RangeMin;
            }

            return values;
        }
    }

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

        foreach (var entry in program.StaticComboEntries)
        {
            var staticCombo = program.StaticComboCache.Get(entry.Key);
            var statics = FormatStaticCombos(program, entry.Key);

            foreach (var dynamicCombo in staticCombo.DynamicCombos)
            {
                if (dynamicCombo.ShaderFileId < 0 || dynamicCombo.ShaderFileId >= staticCombo.ShaderFiles.Length)
                {
                    continue;
                }

                var shaderFile = staticCombo.ShaderFiles[dynamicCombo.ShaderFileId];

                if (shaderFile == null)
                {
                    continue;
                }

                yield return new VfxShaderVariant(
                    entry.Key,
                    dynamicCombo.DynamicComboId,
                    statics,
                    FormatDynamicCombos(program, dynamicCombo.DynamicComboId),
                    shaderFile);
            }
        }
    }
}
