using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using Backend = Vortice.SpirvCross.Backend;

namespace CLI
{
    public partial class Decompiler
    {
        private string? ShaderCombo;
        private bool ShaderListCombos;
        private bool ShaderDumpAll;
        private bool ShaderClean;
        private Backend? ShaderBackend;
        private bool HasShaderOptions => ShaderCombo != null || ShaderListCombos || ShaderDumpAll;
        private string ShaderSourceExtension => ShaderBackend == Backend.GLSL ? "glsl" : "hlsl";

        // Materials in the same package share a loader, so their shaders are only loaded once
        private readonly Dictionary<string, GameFileLoader> ShaderFileLoaders = [];

        private void ProcessShaderOptions(VfxProgramData shader, string path, int[]? staticState = null)
        {
            if (ShaderDumpAll)
            {
                DumpAllShaderCombos(shader, path);
                return;
            }

            if (ShaderListCombos)
            {
                ListShaderCombos(shader, staticState);
            }

            if (ShaderCombo != null)
            {
                DecompileShaderCombo(shader, ShaderCombo, staticState);
            }
        }

        /// <summary>
        /// Applies the shader options to the variants of the material's shader that the material selects.
        /// </summary>
        private void ProcessMaterialShaderOptions(Material material, string path)
        {
            GameFileLoader? fileLoader;

            lock (ShaderFileLoaders)
            {
                if (!ShaderFileLoaders.TryGetValue(path, out fileLoader))
                {
                    fileLoader = CreateGameFileLoader(null, path);
                    ShaderFileLoaders.Add(path, fileLoader);
                }
            }

            if (ShaderDumpAll)
            {
                ReportError("--shader_dump_all only applies to shader files, not materials.");
                return;
            }

            var shader = fileLoader.LoadShader(material.ShaderName);

            if (shader.Features == null)
            {
                ReportError($"Failed to find shader \"{material.ShaderName}\", the material needs to be inside of a game folder or use --game.");
                return;
            }

            var featureState = ShaderDataProvider.GetMaterialFeatureState(material);
            Stdout.WriteLine($"// Shader \"{material.ShaderName}\" with features: {string.Join(", ", featureState.Select(f => $"{f.Key}={f.Value}"))}");

            foreach (var program in shader)
            {
                if (program.VcsProgramType is VcsProgramType.Features or VcsProgramType.PixelShaderRenderState || program.StaticComboEntries.Count == 0)
                {
                    continue;
                }

                var staticState = ShaderDataProvider.ResolveStaticConfiguration(shader.Features, program, featureState).StaticConfig;

                Stdout.WriteLine($"// {program.VcsProgramType} program");
                ProcessShaderOptions(program, path, staticState);
            }
        }

        /// <summary>
        /// Prints every compiled variant of a shader with its combo values. Variants sharing a hash contain identical bytecode.
        /// </summary>
        private void ListShaderCombos(VfxProgramData shader, int[]? onlyStaticState)
        {
            var staticMapping = new ComboConfigMapping(shader);
            var onlyStaticComboId = onlyStaticState == null ? (long?)null : staticMapping.CalcComboIdFromValues(onlyStaticState);
            var uniqueHashes = new HashSet<Guid>();
            var count = 0;

            Stdout.WriteLine("static_combo\tdynamic_combo\tshader_file\thash\tstatic_values\tdynamic_values");

            foreach (var staticComboId in shader.StaticComboEntries.Keys)
            {
                if (onlyStaticComboId != null && staticComboId != onlyStaticComboId)
                {
                    continue;
                }

                var staticCombo = shader.GetStaticCombo(staticComboId);
                var staticValues = ShaderUtilHelpers.FormatComboState(shader.StaticComboArray, staticMapping.GetConfigState(staticComboId));

                foreach (var renderState in staticCombo.DynamicComboRenderStates)
                {
                    var shaderFile = GetShaderFile(staticCombo, renderState);

                    if (shaderFile == null)
                    {
                        continue;
                    }

                    var dynamicValues = shader.DynamicComboArray.Length > 0
                        ? ShaderUtilHelpers.FormatComboState(shader.DynamicComboArray, shader.GetDynamicComboConfig(renderState.DynamicComboId))
                        : string.Empty;

                    uniqueHashes.Add(shaderFile.HashMD5);
                    count++;

                    Stdout.WriteLine($"0x{staticComboId:x08}\t0x{renderState.DynamicComboId:x04}\t{shaderFile.ShaderFileId}\t{shaderFile.HashMD5}\t{staticValues}\t{dynamicValues}");
                }
            }

            Stdout.WriteLine($"--- {count} variants across {shader.StaticComboEntries.Count} static combos, {uniqueHashes.Count} of them unique");
        }

        /// <summary>
        /// Decompiles the single variant selected by combo values such as "S_ALPHA_TEST=1,D_BLEND_WEIGHT_COUNT=4".
        /// </summary>
        private void DecompileShaderCombo(VfxProgramData shader, string comboValues, int[]? initialStaticState)
        {
            var staticState = initialStaticState != null ? [.. initialStaticState] : shader.StaticComboArray.Select(c => c.RangeMin).ToArray();
            var dynamicState = shader.DynamicComboArray.Select(c => c.RangeMin).ToArray();

            foreach (var part in comboValues.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var name = separator < 0 ? part : part[..separator].Trim();
                var value = 1;

                if (separator >= 0 && !int.TryParse(part[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    ReportError($"Invalid combo value in \"{part}\".");
                    return;
                }

                var combos = shader.StaticComboArray;
                var state = staticState;
                var index = FindCombo(combos, name);

                if (index < 0)
                {
                    combos = shader.DynamicComboArray;
                    state = dynamicState;
                    index = FindCombo(combos, name);
                }

                if (index < 0)
                {
                    // With a material, combos apply to all programs of the shader, and not every program has every combo
                    if (initialStaticState != null)
                    {
                        continue;
                    }

                    ReportError($"Combo \"{name}\" does not exist in this shader. Available combos: {string.Join(", ", shader.StaticComboArray.Concat(shader.DynamicComboArray).Select(c => c.Name))}");
                    return;
                }

                var combo = combos[index];

                if (value < combo.RangeMin || value > combo.RangeMax)
                {
                    ReportError($"Combo \"{combo.Name}\" must be between {combo.RangeMin} and {combo.RangeMax}.");
                    return;
                }

                state[index] = value;
            }

            var staticComboId = new ComboConfigMapping(shader).CalcComboIdFromValues(staticState);
            var dynamicComboId = new ComboConfigMapping(shader, isDynamic: true).CalcComboIdFromValues(dynamicState);

            if (!shader.StaticComboEntries.ContainsKey(staticComboId))
            {
                ReportError($"Static combo 0x{staticComboId:x08} was not compiled, it is likely excluded by a combo rule. Use --shader_list_combos to see the compiled variants.");
                return;
            }

            var staticCombo = shader.GetStaticCombo(staticComboId);
            var renderState = Array.Find(staticCombo.DynamicComboRenderStates, r => r.DynamicComboId == dynamicComboId);
            var shaderFile = renderState == null ? null : GetShaderFile(staticCombo, renderState);

            if (shaderFile == null)
            {
                ReportError($"Dynamic combo 0x{dynamicComboId:x04} of static combo 0x{staticComboId:x08} was not compiled, it is likely excluded by a combo rule. Use --shader_list_combos to see the compiled variants.");
                return;
            }

            Stdout.WriteLine($"// Static combo 0x{staticComboId:x08}, dynamic combo 0x{dynamicComboId:x04}");
            Stdout.WriteLine(DecompileShaderFile(shaderFile));
        }

        /// <summary>
        /// Writes every unique variant of a shader to the output folder. Variants that compiled to identical
        /// bytecode share one file, the manifest says which combos map to it.
        /// </summary>
        private void DumpAllShaderCombos(VfxProgramData shader, string path)
        {
            Debug.Assert(OutputFile != null);

            var baseName = Path.GetFileNameWithoutExtension(path);
            var directory = Path.Combine(OutputFile, baseName);

            // Two levels of deduplication: combos often share bytecode outright, and bytecode that only differs
            // in SPIR-V id numbering decompiles to the same source once identifiers have been normalized.
            var fileNamesByBytecode = new Dictionary<Guid, string>();
            var fileNamesBySource = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifest = new StringBuilder();
            var count = 0;

            manifest.AppendLine("static_combo\tdynamic_combo\tfile\tstatic_values\tdynamic_values");

            foreach (var variant in VfxComboResolver.EnumerateVariants(shader))
            {
                count++;

                if (!fileNamesByBytecode.TryGetValue(variant.ShaderFile.HashMD5, out var fileName))
                {
                    var decompiled = DecompileShaderFile(variant.ShaderFile);
                    var body = StripLeadingComments(decompiled);

                    if (!fileNamesBySource.TryGetValue(body, out fileName))
                    {
                        fileName = $"{baseName}_{variant.StaticComboId:x08}_{variant.DynamicComboId:x04}.{ShaderSourceExtension}";
                        fileNamesBySource.Add(body, fileName);

                        DumpFile(Path.Combine(directory, fileName), Encoding.UTF8.GetBytes(decompiled));
                    }

                    fileNamesByBytecode.Add(variant.ShaderFile.HashMD5, fileName);
                }

                manifest.Append(CultureInfo.InvariantCulture, $"0x{variant.StaticComboId:x08}\t0x{variant.DynamicComboId:x04}\t{fileName}\t");
                manifest.Append(variant.StaticCombos).Append('\t').AppendLine(variant.DynamicCombos);
            }

            DumpFile(Path.Combine(directory, "manifest.tsv"), Encoding.UTF8.GetBytes(manifest.ToString()));

            Console.WriteLine($"--- {count} variants, {fileNamesByBytecode.Count} unique bytecodes, written as {fileNamesBySource.Count} unique files");
        }

        /// <summary>
        /// Drops the header comments, which name the combo this variant belongs to and therefore differ
        /// between variants that are otherwise identical.
        /// </summary>
        private static string StripLeadingComments(string decompiled)
        {
            var span = decompiled.AsSpan();
            var offset = 0;

            while (offset < span.Length)
            {
                var lineEnd = span[offset..].IndexOf('\n');
                var line = (lineEnd == -1 ? span[offset..] : span.Slice(offset, lineEnd)).Trim();

                if (line.Length > 0 && !line.StartsWith("//"))
                {
                    break;
                }

                if (lineEnd == -1)
                {
                    return string.Empty;
                }

                offset += lineEnd + 1;
            }

            return decompiled[offset..];
        }

        private string DecompileShaderFile(VfxShaderFile shaderFile)
        {
            if (shaderFile is VfxShaderFileVulkan vulkan)
            {
                return vulkan.GetDecompiledFile(ShaderBackend, ShaderClean ? SpirvReflectionOptions.Clean : SpirvReflectionOptions.Default);
            }

            return shaderFile.GetDecompiledFile();
        }

        private static VfxShaderFile? GetShaderFile(VfxStaticComboData staticCombo, VfxRenderStateInfo renderState)
        {
            return renderState.ShaderFileId >= 0 && renderState.ShaderFileId < staticCombo.ShaderFiles.Length
                ? staticCombo.ShaderFiles[renderState.ShaderFileId]
                : null;
        }

        private static int FindCombo(VfxCombo[] combos, string name)
        {
            return Array.FindIndex(combos, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
