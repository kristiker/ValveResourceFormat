using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.Serialization.VfxEval;

namespace ValveResourceFormat.IO;

public sealed class ShaderExtract
{
    public ShaderFile Features { get; init; }
    public ShaderFile GeometryShader { get; init; }
    public ShaderFile VertexShader { get; init; }
    public ShaderFile PixelShader { get; init; }
    public ShaderFile ComputeShader { get; init; }

    private List<string> FeatureNames { get; set; }
    private string[] Globals { get; set; }


    public ShaderExtract(Resource resource)
        : this((SboxShader)resource.DataBlock)
    { }

    public ShaderExtract(SboxShader sboxShaderCollection)
    {
        Features = sboxShaderCollection.Features;
        GeometryShader = sboxShaderCollection.Geometry;
        VertexShader = sboxShaderCollection.Vertex;
        PixelShader = sboxShaderCollection.Pixel;
        ComputeShader = sboxShaderCollection.Compute;
    }

    public ShaderExtract(SortedDictionary<(VcsProgramType, string), ShaderFile> shaderCollection)
        : this(shaderCollection.Values)
    { }

    public ShaderExtract(IEnumerable<ShaderFile> shaderCollection)
    {
        foreach (var shader in shaderCollection)
        {
            if (shader.VcsProgramType == VcsProgramType.Features)
            {
                Features = shader;
            }

            if (shader.VcsProgramType == VcsProgramType.GeometryShader)
            {
                GeometryShader = shader;
            }

            if (shader.VcsProgramType == VcsProgramType.VertexShader)
            {
                VertexShader = shader;
            }

            if (shader.VcsProgramType == VcsProgramType.PixelShader)
            {
                PixelShader = shader;
            }

            if (shader.VcsProgramType == VcsProgramType.ComputeShader)
            {
                ComputeShader = shader;
            }
        }
    }

    public ContentFile ToContentFile()
    {
        var vfx = new ContentFile
        {
            Data = Encoding.UTF8.GetBytes(ToVFX())
        };

        return vfx;
    }

    public string ToVFX()
    {
        // TODO: IndentedTextWriter
        FeatureNames = Features.SfBlocks.Select(f => f.Name).ToList();
        Globals = Features.ParamBlocks.Select(p => p.Name).ToArray();

        return HEADER()
            + MODES()
            + FEATURES()
            + COMMON()
            + GS()
            + VS()
            + PS()
            + CS()
            ;
    }

    private string HEADER()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine("HEADER");
        stringBuilder.AppendLine("{");

        stringBuilder.AppendLine($"\tDescription = \"{Features.FeaturesHeader.FileDescription}\";");
        stringBuilder.AppendLine($"\tDevShader = {(Features.FeaturesHeader.DevShader == 0 ? "false" : "true")};");
        stringBuilder.AppendLine($"\tVersion = {Features.FeaturesHeader.Version};");

        stringBuilder.AppendLine("}");

        return stringBuilder.ToString();
    }

    private string MODES()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("MODES");
        stringBuilder.AppendLine("{");

        foreach (var mode in Features.FeaturesHeader.Modes)
        {
            if (string.IsNullOrEmpty(mode.Shader))
            {
                stringBuilder.AppendLine($"\t{mode.Name}({mode.StaticConfig});");
            }
            else
            {
                stringBuilder.AppendLine($"\t{mode.Name}(\"{mode.Shader}\");");
            }
        }

        stringBuilder.AppendLine("}");
        return stringBuilder.ToString();
    }

    private string FEATURES()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("FEATURES");
        stringBuilder.AppendLine("{");

        HandleFeatures(Features.SfBlocks, Features.SfConstraintsBlocks, stringBuilder);

        stringBuilder.AppendLine("}");

        return stringBuilder.ToString();
    }

    private string COMMON()
    {
        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("COMMON");
        stringBuilder.AppendLine("{");

        if (VertexShader is not null && VertexShader.SymbolBlocks.Count > 0)
        {
            stringBuilder.AppendLine("\tstruct VS_INPUT");
            stringBuilder.AppendLine("\t{");

            foreach (var symbol in VertexShader.SymbolBlocks[0].SymbolsDefinition)
            {
                var attributeVfx = symbol.Option.Length > 0 ? $" < Semantic({symbol.Option}); >" : string.Empty;
                // TODO: type
                stringBuilder.AppendLine($"\t\tfloat4 {symbol.Name} : {symbol.Type}{symbol.SemanticIndex}{attributeVfx};");
            }

            stringBuilder.AppendLine("\t};");
        }

        stringBuilder.AppendLine("}");

        return stringBuilder.ToString();
    }

    private string GS()
    {
        if (GeometryShader is null)
        {
            return string.Empty;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("GS");
        stringBuilder.AppendLine("{");

        HandleStaticCombos(VertexShader.SfBlocks, VertexShader.SfConstraintsBlocks, FeatureNames, stringBuilder);
        HandleParameters(VertexShader.ParamBlocks, stringBuilder);

        stringBuilder.AppendLine("}");


        return stringBuilder.ToString();
    }

    private string VS()
    {
        if (VertexShader is null)
        {
            return string.Empty;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("VS");
        stringBuilder.AppendLine("{");

        HandleStaticCombos(VertexShader.SfBlocks, VertexShader.SfConstraintsBlocks, FeatureNames, stringBuilder);
        HandleParameters(VertexShader.ParamBlocks, stringBuilder);

        stringBuilder.AppendLine("}");


        return stringBuilder.ToString();
    }

    private string PS()
    {
        if (PixelShader is null)
        {
            return string.Empty;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("PS");
        stringBuilder.AppendLine("{");


        HandleStaticCombos(PixelShader.SfBlocks, PixelShader.SfConstraintsBlocks, FeatureNames, stringBuilder);
        HandleParameters(PixelShader.ParamBlocks, stringBuilder);

        foreach (var mipmap in Features.MipmapBlocks)
        {
            stringBuilder.AppendLine($"\t{GetChannelFromMipmap(mipmap)}");
        }

        stringBuilder.AppendLine("}");
        return stringBuilder.ToString();
    }

    private string CS()
    {
        if (ComputeShader is null)
        {
            return string.Empty;
        }

        var stringBuilder = new StringBuilder();
        stringBuilder.AppendLine();
        stringBuilder.AppendLine("CS");
        stringBuilder.AppendLine("{");


        HandleStaticCombos(ComputeShader.SfBlocks, ComputeShader.SfConstraintsBlocks, FeatureNames, stringBuilder);
        HandleParameters(ComputeShader.ParamBlocks, stringBuilder);

        stringBuilder.AppendLine("}");
        return stringBuilder.ToString();
    }


    private static void HandleFeatures(List<SfBlock> features, List<SfConstraintsBlock> constraints, StringBuilder sb)
    {
        foreach (var feature in features)
        {
            var checkboxNames = feature.CheckboxNames.Count > 0
                ? " (" + string.Join(", ", feature.CheckboxNames.Select((x, i) => $"{i}=\"{x}\"")) + ")"
                : string.Empty;

            // Verify RangeMin
            sb.AppendLine($"\tFeature( {feature.Name}, {feature.RangeMin}..{feature.RangeMax}{checkboxNames}, \"{feature.Category}\" );");
        }

        foreach (var rule in HandleConstraints(features, constraints))
        {
            sb.AppendLine($"\tFeatureRule( {rule.Constraint}, \"{rule.Description}\" );");
        }
    }
    private static void HandleStaticCombos(List<SfBlock> combos, List<SfConstraintsBlock> constraints, List<string> features, StringBuilder sb)
    {
        foreach (var staticCombo in combos)
        {
            if (staticCombo.FeatureIndex != -1)
            {
                sb.AppendLine($"\tStaticCombo( {staticCombo.Name}, {features[staticCombo.FeatureIndex]}, Sys( All ) );");
                continue;
            }
            else if (staticCombo.RangeMax != 0)
            {
                sb.AppendLine($"\tStaticCombo( {staticCombo.Name}, {staticCombo.RangeMin}..{staticCombo.RangeMax}, Sys( All ) );");
            }
            else
            {
                sb.AppendLine($"\tStaticCombo( {staticCombo.Name}, {staticCombo.RangeMax}, Sys( {staticCombo.Sys} ) );");
            }
        }

        foreach (var rule in HandleConstraints(combos, constraints))
        {
            sb.AppendLine($"\tStaticComboRule( {rule.Constraint} );");
        }
    }

    private static IEnumerable<(string Constraint, string Description)> HandleConstraints(List<SfBlock> sfBlocks, List<SfConstraintsBlock> constraints)
    {
        foreach (var constraint in constraints)
        {
            Console.WriteLine(string.Join(" ", constraint.Flags));
            var constrainedNames = string.Join(", ", constraint.Range0.Select(x => sfBlocks[x].Name));

            var rules = new List<string>
            {
                "<rule0>",
                "Requires1",
                "Requiress", // spritecard: FeatureRule(Requiress(F_NORMAL_MAP, F_TEXTURE_LAYERS, F_TEXTURE_LAYERS, F_TEXTURE_LAYERS), "Normal map requires Less than 3 Layers due to DX9");
                "Allow1",
            };

            yield return ($"{rules[constraint.RelRule]}( {constrainedNames} )", constraint.Description);
        }
    }

    private void HandleParameters(List<ParamBlock> paramBlocks, StringBuilder sb)
    {
        foreach (var param in paramBlocks.OrderBy(x => x.Id == 255))
        {
            // BoolAttribute
            // FloatAttribute
            var attributes = new List<string>();

            // Render State
            if (param.ParamType is ParameterType.RenderState)
            {
                if (Enum.TryParse<RenderState>(param.Name, false, out var result))
                {
                    if ((byte)result != param.Id)
                    {
                        Console.WriteLine($"{param.Name} = {param.Id},");
                    }
                }
                else
                {
                    Console.WriteLine($"{param.Name} = {param.Id},");
                }

                if (param.DynExp.Length > 0)
                {
                    var dynEx = new VfxEval(param.DynExp, Globals, omitReturnStatement: true, FeatureNames).DynamicExpressionResult;
                    sb.AppendLine($"\tRenderState({param.Name}, {dynEx});");
                }
                else
                {
                    sb.AppendLine($"\tRenderState({param.Name}, {param.IntDefs[0]});");
                }
            }

            // Sampler State
            else if (param.ParamType is ParameterType.SamplerState)
            {
                if (Enum.TryParse<SamplerState>(param.Name, false, out var result))
                {
                    if ((byte)result != param.Id)
                    {
                        Console.WriteLine($"{param.Name} = {param.Id},");
                    }
                }
                else
                {
                    Console.WriteLine($"{param.Name} = {param.Id},");
                }

                sb.AppendLine($"\t{param.Name}({param.IntDefs[0]}); // Sampler");
            }

            // User input
            else if (param.Id == 255)
            {
                // Texture Input (unpacked)
                if (param.VecSize == -1)
                {
                    if (param.UiType != UiType.Texture)
                    {
                        throw new Exception("Unexpected UiType: " + param.UiType.ToString());
                    }

                    if (param.ParamType != ParameterType.InputTexture)
                    {
                        throw new Exception("Unexpected ParamType: " + param.UiType.ToString());
                    }

                    var default4 = $"Default4({string.Join(", ", param.FloatDefs)})";
                    var mode = param.IntArgs1[2] == 0
                        ? "Linear"
                        : "Srgb";
                    sb.AppendLine($"\tCreateInputTexture2D({param.Name}, {mode}, {param.IntArgs1[3]}, \"{param.Command1}\", \"{param.Command0}\", \"{param.UiGroup}\", {default4});");
                    // param.FileRef materials/default/default_cube.png
                    continue;
                }


                var intDefsCutOff = 0;
                var floatDefsCutOff = 0;
                for (var i = 3; i >= 0; i--)
                {
                    if (param.IntDefs[i] == 0)
                    {
                        intDefsCutOff++;
                    }

                    if (param.FloatDefs[i] == 0f)
                    {
                        floatDefsCutOff++;
                    }
                }

                static string GetFuncName(string func, int cutOff)
                    => cutOff == 3 ? func : func + (4 - cutOff);

                if (intDefsCutOff <= 3)
                {
                    if (floatDefsCutOff <= 3)
                    {
                        attributes.Add($"{GetFuncName("Default", floatDefsCutOff)}({string.Join(", ", param.FloatDefs[..^floatDefsCutOff])})");
                    }
                    else
                    {
                        var funcName = intDefsCutOff == 3 ? "Default" : "Default" + (4 - intDefsCutOff);
                        attributes.Add($"{GetFuncName("Default", intDefsCutOff)}({string.Join(", ", param.IntDefs[..^intDefsCutOff])})");
                    }
                }

                var intRangeCutOff = 0;
                var floatRangeCutOff = 0;
                for (var i = 3; i >= 0; i--)
                {
                    if (param.IntMins[i] == 0 && param.IntMaxs[i] == 1)
                    {
                        intRangeCutOff++;
                    }

                    if (param.FloatMins[i] == 0f && param.FloatMaxs[i] == 1f)
                    {
                        floatRangeCutOff++;
                    }
                }

                if (intRangeCutOff <= 3 && param.IntMins[0] != -ParamBlock.IntInf)
                {
                    if (floatRangeCutOff <= 3 && param.FloatMins[0] != -ParamBlock.FloatInf)
                    {
                        attributes.Add($"{GetFuncName("Range", floatRangeCutOff)}({string.Join(", ", param.FloatMins[..^floatRangeCutOff])}, {string.Join(", ", param.FloatMaxs[..^floatRangeCutOff])})");
                    }
                    else
                    {
                        attributes.Add($"{GetFuncName("Range", intRangeCutOff)}({string.Join(", ", param.IntMins[..^intRangeCutOff])}, {string.Join(", ", param.IntMaxs[..^intRangeCutOff])});");
                    }
                }


                if (param.UiType != UiType.None)
                {
                    attributes.Add($"UiType({param.UiType});");
                }

                if (param.UiGroup.Length > 0)
                {
                    attributes.Add($"UiGroup(\"{param.UiGroup}\");");
                }

                if (param.DynExp.Length > 0)
                {
                    var globals = paramBlocks.Select(p => p.Name).ToArray();
                    var dynEx = new VfxEval(param.DynExp, globals, omitReturnStatement: true, FeatureNames).DynamicExpressionResult.Replace(param.Name, "this");
                    attributes.Add($"Expression({dynEx});");
                }

                var attributesVfx = attributes.Count > 0
                    ? " < " + string.Join(" ", attributes) + " > "
                    : string.Empty;

                sb.AppendLine($"\t{Vfx.Types.GetValueOrDefault(param.VfxType, $"unkntype{param.VfxType}")} {param.Name}{attributesVfx};");
            }
            else
            {

            }
        }
    }

    private string GetChannelFromMipmap(MipmapBlock mipmapBlock)
    {
        var mode = mipmapBlock.ColorMode == 0
            ? "Linear"
            : "Srgb";

        var cutoff = Array.IndexOf(mipmapBlock.InputTextureIndices, -1);
        var inputs = string.Join(", ", mipmapBlock.InputTextureIndices[..cutoff].Select(idx => Features.ParamBlocks[idx].Name));

        return $"Channel( {mipmapBlock.Channel}, {mipmapBlock.Name}( {inputs} ) );";
    }
}
