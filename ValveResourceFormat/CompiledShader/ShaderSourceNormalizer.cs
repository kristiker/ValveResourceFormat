using System.Globalization;
using System.Text.RegularExpressions;

namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// Rewrites decompiled shader source so that combos of the same shader can be compared to each other.
/// </summary>
/// <remarks>
/// All passes are purely textual and conservative: when a rewrite would collide with a name that already
/// exists in the source, the rewrite is skipped rather than producing source that no longer compiles.
/// </remarks>
internal static partial class ShaderSourceNormalizer
{
    // struct _730 { ... }
    [GeneratedRegex(@"\bstruct\s+(_\d+)\b")]
    private static partial Regex StructDeclaration();

    // _25207, _1ident
    [GeneratedRegex(@"\b_\d+(?:ident)?\b")]
    private static partial Regex GeneratedIdentifier();

    // PerViewConstantBuffer_t_1_g_matWorldToProjection
    [GeneratedRegex(@"\b([A-Za-z_]\w*?)_\d+_(\w+)\b")]
    private static partial Regex PrefixedBufferMember();

    // mat4(vec4(m[0].x, m[1].x, m[2].x, m[3].x), vec4(m[0].y, ...), ...) is a transpose of m.
    [GeneratedRegex("""
        \b(mat4|float4x4)\(\s*
        (?:vec4|float4)\(\s*([A-Za-z_][\w.]*)\[0\]\.x,\s*\2\[1\]\.x,\s*\2\[2\]\.x,\s*\2\[3\]\.x\s*\),\s*
        (?:vec4|float4)\(\s*\2\[0\]\.y,\s*\2\[1\]\.y,\s*\2\[2\]\.y,\s*\2\[3\]\.y\s*\),\s*
        (?:vec4|float4)\(\s*\2\[0\]\.z,\s*\2\[1\]\.z,\s*\2\[2\]\.z,\s*\2\[3\]\.z\s*\),\s*
        (?:vec4|float4)\(\s*\2\[0\]\.w,\s*\2\[1\]\.w,\s*\2\[2\]\.w,\s*\2\[3\]\.w\s*\)\s*\)
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex ComponentWiseTranspose();

    /// <summary>
    /// Applies the enabled rewriting passes to decompiled shader source.
    /// </summary>
    /// <param name="code">The decompiled source.</param>
    /// <param name="bufferNames">Constant buffer names that member prefixes may be built from.</param>
    /// <param name="options">The passes to run.</param>
    public static string Apply(string code, IReadOnlyCollection<string> bufferNames, SpirvReflectionOptions options)
    {
        if (options.StripBufferPrefixes)
        {
            code = StripBufferPrefixes(code, bufferNames);
        }

        if (options.SimplifyMatrixConstruction)
        {
            code = ComponentWiseTranspose().Replace(code, static m => $"transpose({m.Groups[1].Value}({m.Groups[2].Value}[0], {m.Groups[2].Value}[1], {m.Groups[2].Value}[2], {m.Groups[2].Value}[3]))");
        }

        if (options.NormalizeIdentifiers)
        {
            code = NormalizeIdentifiers(code);
        }

        return code;
    }

    /// <summary>
    /// Renames SPIR-V id derived identifiers to names assigned in order of first appearance.
    /// </summary>
    private static string NormalizeIdentifiers(string code)
    {
        // Structs are collected first so that a type keeps one name across its declaration and every use of it.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var structPrefix = FindFreePrefix(code, "S");
        var tempPrefix = FindFreePrefix(code, "_t");
        var loopPrefix = FindFreePrefix(code, "i");

        foreach (Match declaration in StructDeclaration().Matches(code))
        {
            var name = declaration.Groups[1].Value;

            if (!names.ContainsKey(name))
            {
                names.Add(name, structPrefix + names.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        var tempCount = 0;
        var loopCount = 0;

        return GeneratedIdentifier().Replace(code, match =>
        {
            var name = match.Value;

            if (names.TryGetValue(name, out var replacement))
            {
                return replacement;
            }

            // _1ident is a loop counter synthesized by SPIRV-Cross, not a SPIR-V id.
            replacement = name.EndsWith("ident", StringComparison.Ordinal)
                ? loopPrefix + (loopCount++).ToString(CultureInfo.InvariantCulture)
                : tempPrefix + (tempCount++).ToString(CultureInfo.InvariantCulture);

            names.Add(name, replacement);
            return replacement;
        });

        // Renaming to a name that the shader already uses would change what the code means, so pick a prefix
        // that does not occur in the source at all. Valve's names are g_/v/n prefixed, so this rarely loops.
        string FindFreePrefix(string source, string preferred)
        {
            var prefix = preferred;

            while (new Regex($@"\b{Regex.Escape(prefix)}\d+\b").IsMatch(source))
            {
                prefix += preferred[^1];
            }

            return prefix;
        }
    }

    /// <summary>
    /// Removes the buffer name and instance id that SPIRV-Cross prepends to flattened constant buffer members.
    /// </summary>
    private static string StripBufferPrefixes(string code, IReadOnlyCollection<string> bufferNames)
    {
        if (bufferNames.Count == 0)
        {
            return code;
        }

        var buffers = new HashSet<string>(bufferNames, StringComparer.Ordinal);

        // SPIRV-Cross collapses the double underscore of names such as _Globals_ against the instance id,
        // so _Globals__1_x is emitted as _Globals_1_x. Accept the collapsed spelling too.
        foreach (var name in bufferNames)
        {
            buffers.Add(name.TrimEnd('_'));
        }

        // A member is only stripped when its bare name is not already taken by something else in the source,
        // otherwise two different variables would end up sharing one name.
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PrefixedBufferMember().Matches(code))
        {
            if (buffers.Contains(match.Groups[1].Value))
            {
                candidates.Add(match.Groups[2].Value);
            }
        }

        foreach (var candidate in candidates)
        {
            if (new Regex($@"(?<![\w.]){Regex.Escape(candidate)}\b").IsMatch(code))
            {
                rejected.Add(candidate);
            }
        }

        return PrefixedBufferMember().Replace(code, match =>
        {
            var member = match.Groups[2].Value;

            if (!buffers.Contains(match.Groups[1].Value) || rejected.Contains(member))
            {
                return match.Value;
            }

            return member;
        });
    }
}
