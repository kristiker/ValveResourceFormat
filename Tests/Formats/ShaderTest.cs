using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TUnit.Assertions.Enums;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.Utils;
using Vortice.SpirvCross;
using static ValveResourceFormat.CompiledShader.ShaderUtilHelpers;

namespace Tests.Formats
{
    public class ShaderTest
    {
        public static string ShadersDir => TestFixtures.Path("Shaders");

        public static IEnumerable<string> ShaderFiles() => TestFixtures.FilesIn("Shaders", "*.vcs");

        [Test]
        [MethodDataSource(nameof(ShaderFiles))]
        public async Task ParseShaders(string shaderFile)
        {
            using var shader = new VfxProgramData();
            shader.Read(Path.Combine(ShadersDir, shaderFile));

            using var sw = new IndentedTextWriter();

            shader.PrintSummary(sw);
            await Assert.That(sw.ToString().Length).IsGreaterThanOrEqualTo(100);

            foreach (var staticComboEntry in shader.StaticComboEntries)
            {
                var value = staticComboEntry.Value.Unserialize();
                await Assert.That(value).IsNotNull();
                _ = new PrintStaticComboSummary(value, sw);
            }
        }

        [Test]
        public async Task ShaderResourceDataMatchesBinary()
        {
            using var shader1 = new VfxProgramData();
            using var shader2 = new VfxProgramData();

            shader1.Read(Path.Combine(ShadersDir, "vcs69_bloom_vulkan_40_ps.vcs"));
            shader2.Read(Path.Combine(ShadersDir, "vcs70_resource_bloom_vulkan_40_ps.vcs"));

            using (Assert.Multiple())
            {
                await Assert.That(shader2.VcsProgramType).IsEqualTo(shader1.VcsProgramType);
                await Assert.That(shader2.VcsPlatformType).IsEqualTo(shader1.VcsPlatformType);
                await Assert.That(shader2.VcsShaderModelType).IsEqualTo(shader1.VcsShaderModelType);
                await Assert.That(shader2.VariableDescriptionVersionHash).IsEqualTo(shader1.VariableDescriptionVersionHash);
                await Assert.That(shader2.VariableSourceMax).IsEqualTo(shader1.VariableSourceMax);

                // Binary stores one hash, KV3 stores all hashes
                // Assert.That(shader1.ProgramHashes, Is.EqualTo(shader2.ProgramHashes));
            }

            using (Assert.Multiple())
            {
                await Assert.That(shader2.DynamicComboArray).Count().IsEqualTo(shader1.DynamicComboArray.Length);
                for (var i = 0; i < shader1.DynamicComboArray.Length; i++)
                {
                    var combo1 = shader1.DynamicComboArray[i];
                    var combo2 = shader2.DynamicComboArray[i];

                    await Assert.That(combo2.Name).IsEqualTo(combo1.Name);
                    await Assert.That(combo2.ComboIndexValue).IsEqualTo(combo1.ComboIndexValue);
                    await Assert.That(combo2.AliasName).IsEqualTo(combo1.AliasName);
                    await Assert.That(combo2.ComboType).IsEqualTo(combo1.ComboType);
                    await Assert.That(combo2.ComboSourceType).IsEqualTo(combo1.ComboSourceType);
                    await Assert.That(combo2.FeatureComparisonValue).IsEqualTo(combo1.FeatureComparisonValue);
                    await Assert.That(combo2.RangeMin).IsEqualTo(combo1.RangeMin);
                    await Assert.That(combo2.RangeMax).IsEqualTo(combo1.RangeMax);
                    await Assert.That(combo2.StateNames).IsEquivalentTo(combo1.StateNames);
                }
            }

            using (Assert.Multiple())
            {
                await Assert.That(shader2.DynamicComboRules).Count().IsEqualTo(shader1.DynamicComboRules.Length);
                for (var i = 0; i < shader1.DynamicComboRules.Length; i++)
                {
                    var rule1 = shader1.DynamicComboRules[i];
                    var rule2 = shader2.DynamicComboRules[i];

                    await Assert.That(rule2.RuleMethod).IsEqualTo(rule1.RuleMethod);
                    await Assert.That(rule2.RuleType).IsEqualTo(rule1.RuleType);
                    await Assert.That(rule2.ArgTypes).IsEquivalentTo(rule1.ArgTypes);
                    await Assert.That(rule2.ArgIndices).IsEquivalentTo(rule1.ArgIndices);
                    await Assert.That(rule2.ArgValues).IsEquivalentTo(rule1.ArgValues);
                    await Assert.That(rule2.ExtraRuleData).IsEquivalentTo(rule1.ExtraRuleData);
                    await Assert.That(rule2.ErrorString).IsEqualTo(rule1.ErrorString);
                }
                await Assert.That(shader2.VariableDescriptions).Count().IsEqualTo(shader1.VariableDescriptions.Length);
                for (var i = 0; i < shader1.VariableDescriptions.Length; i++)
                {
                    var var1 = shader1.VariableDescriptions[i];
                    var var2 = shader2.VariableDescriptions[i];

                    await Assert.That(var2.Name).IsEqualTo(var1.Name);
                    await Assert.That(var2.UiGroup).IsEqualTo(var1.UiGroup);
                    await Assert.That(var2.SourceString).IsEqualTo(var1.SourceString);
                    await Assert.That(var2.UiType).IsEqualTo(var1.UiType);
                    await Assert.That(var2.UiStep).IsEqualTo(var1.UiStep);
                    await Assert.That(var2.VariableSource).IsEqualTo(var1.VariableSource);
                    await Assert.That(var2.CompiledExpression).IsEquivalentTo(var1.CompiledExpression);
                    await Assert.That(var2.UiVisibilityExpression).IsEquivalentTo(var1.UiVisibilityExpression);
                    await Assert.That(var2.SourceIndex).IsEqualTo(var1.SourceIndex);
                    await Assert.That(var2.VfxType).IsEqualTo(var1.VfxType);
                    await Assert.That(var2.RegisterType).IsEqualTo(var1.RegisterType);
                    await Assert.That(var2.ContextStateAffectedByVariable).IsEqualTo(var1.ContextStateAffectedByVariable);
                    await Assert.That(var2.RegisterElements).IsEqualTo(var1.RegisterElements);
                    await Assert.That(var2.TypeSpecificBits).IsEqualTo(var1.TypeSpecificBits);
                    await Assert.That(var2.DefaultInputTexture).IsEqualTo(var1.DefaultInputTexture);
                    await Assert.That(var2.IntDefs).IsEquivalentTo(var1.IntDefs).Because(var2.Name);
                    await Assert.That(var2.IntMins).IsEquivalentTo(var1.IntMins).Because(var2.Name);
                    await Assert.That(var2.IntMaxs).IsEquivalentTo(var1.IntMaxs).Because(var2.Name);
                    await Assert.That(var2.FloatDefs).IsEquivalentTo(var1.FloatDefs).Because(var2.Name);
                    await Assert.That(var2.FloatMins).IsEquivalentTo(var1.FloatMins).Because(var2.Name);
                    await Assert.That(var2.FloatMaxs).IsEquivalentTo(var1.FloatMaxs).Because(var2.Name);
                    await Assert.That(var2.OutputTextureFormat).IsEqualTo(var1.OutputTextureFormat);
                    await Assert.That(var2.ChannelCount).IsEqualTo(var1.ChannelCount);
                    await Assert.That(var2.ChannelInfoIndices).IsEquivalentTo(var1.ChannelInfoIndices);
                    await Assert.That(var2.InputColorSpace).IsEqualTo(var1.InputColorSpace);
                    await Assert.That(var2.TextureFileEnding).IsEqualTo(var1.TextureFileEnding);
                    await Assert.That(var2.InputProcessingCommand).IsEqualTo(var1.InputProcessingCommand);

                    await Assert.That(var2.MinPrecisionBits).IsEqualTo(var1.MinPrecisionBits);
                    await Assert.That(var2.LayerId).IsEqualTo(var1.LayerId);
                    await Assert.That(var2.AllowLayerOverride).IsEqualTo(var1.AllowLayerOverride);
                    await Assert.That(var2.MaxRes).IsEqualTo(var1.MaxRes);
                    await Assert.That(var2.IsLayerConstant).IsEqualTo(var1.IsLayerConstant);
                }
            }

            using (Assert.Multiple())
            {
                await Assert.That(shader2.StaticComboEntries).Count().IsEqualTo(shader1.StaticComboEntries.Count);

                var combo1 = shader1.GetStaticCombo(0);
                var combo2 = shader2.GetStaticCombo(0);

                // KV3 has one less item in some arrays
                const int OneLessItemKV3 = 1;

                await Assert.That(combo2.StaticComboId).IsEqualTo(combo1.StaticComboId);
                await Assert.That(combo2.VsInputSignatureIndices).IsEquivalentTo(combo1.VsInputSignatureIndices, CollectionOrdering.Matching);
                await Assert.That(combo2.ConstantBufferBindingFlags).IsEquivalentTo(combo1.ConstantBufferBindingFlags[..^OneLessItemKV3], CollectionOrdering.Matching);
                await Assert.That(combo2.ConstantBufferBindingSlots).IsEquivalentTo(combo1.ConstantBufferBindingSlots[..^OneLessItemKV3], CollectionOrdering.Matching);
                await Assert.That(combo2.ConstantBufferSize).IsEqualTo(combo1.ConstantBufferSize);
                await Assert.That(combo2.StaticCB).IsEqualTo(combo1.StaticCB);
                await Assert.That(combo2.GlobalsBDA).IsEqualTo(combo1.GlobalsBDA);
                await Assert.That(combo2.UsesGlslSources).IsEqualTo(combo1.UsesGlslSources);

                static async Task TestVfxVariableIndexArray(VfxVariableIndexArray binary, VfxVariableIndexArray kv3)
                {
                    using var _ = Assert.Multiple();
                    await Assert.That(kv3.Index).IsEqualTo(binary.Index);
                    await Assert.That(kv3.FirstRenderStateElement).IsEqualTo(binary.FirstRenderStateElement);
                    await Assert.That(kv3.FirstConstantElement).IsEqualTo(binary.FirstConstantElement);
                    await Assert.That(kv3.Fields).IsEquivalentTo(binary.Fields, CollectionOrdering.Matching);
                }

                await TestVfxVariableIndexArray(combo1.AllVariables, combo2.AllVariables);
                // one less
                await Assert.That(combo2.DynamicComboVariables).Count().IsEqualTo(combo1.DynamicComboVariables.Length - OneLessItemKV3);
                for (var i = 0; i < combo2.DynamicComboVariables.Length; i++)
                {
                    await TestVfxVariableIndexArray(combo1.DynamicComboVariables[i], combo2.DynamicComboVariables[i]);
                }
                await Assert.That(combo2.DynamicComboRenderStates).Count().IsEqualTo(combo1.DynamicComboRenderStates.Length);
                for (var i = 0; i < combo1.DynamicComboRenderStates.Length; i++)
                {
                    var dyn1 = combo1.DynamicComboRenderStates[i];
                    var dyn2 = combo2.DynamicComboRenderStates[i];

                    await Assert.That(dyn2.ShaderFileId).IsEqualTo(dyn1.ShaderFileId);
                    await Assert.That(dyn2.DynamicComboId).IsEqualTo(dyn1.DynamicComboId);

                    // Source pointer is binary only
                    // Assert.That(dyn2.SourcePointer, Is.EqualTo(dyn1.SourcePointer));

                    var psRenderState1 = dyn1 as VfxRenderStateInfoPixelShader;
                    var psRenderState2 = dyn2 as VfxRenderStateInfoPixelShader;

                    var depth1 = psRenderState1!.DepthStencilStateDesc!.Value;
                    var depth2 = psRenderState2!.DepthStencilStateDesc!.Value;

                    await Assert.That(depth2.DepthWriteEnable).IsEqualTo(depth1.DepthWriteEnable);
                    await Assert.That(depth2.DepthFunc).IsEqualTo(depth1.DepthFunc);
                    await Assert.That(depth2.DepthTestEnable).IsEqualTo(depth1.DepthTestEnable);
                    await Assert.That(depth2.StencilEnable).IsEqualTo(depth1.StencilEnable);
                    await Assert.That(depth2.StencilReadMask).IsEqualTo(depth1.StencilReadMask);
                    await Assert.That(depth2.StencilWriteMask).IsEqualTo(depth1.StencilWriteMask);
                    await Assert.That(depth2.FrontStencilFunc).IsEqualTo(depth1.FrontStencilFunc);
                    await Assert.That(depth2.FrontStencilPassOp).IsEqualTo(depth1.FrontStencilPassOp);
                    await Assert.That(depth2.FrontStencilFailOp).IsEqualTo(depth1.FrontStencilFailOp);
                    await Assert.That(depth2.FrontStencilDepthFailOp).IsEqualTo(depth1.FrontStencilDepthFailOp);
                    await Assert.That(depth2.BackStencilFunc).IsEqualTo(depth1.BackStencilFunc);
                    await Assert.That(depth2.BackStencilPassOp).IsEqualTo(depth1.BackStencilPassOp);
                    await Assert.That(depth2.BackStencilFailOp).IsEqualTo(depth1.BackStencilFailOp);
                    await Assert.That(depth2.BackStencilDepthFailOp).IsEqualTo(depth1.BackStencilDepthFailOp);

                    var raster1 = psRenderState1.RasterizerStateDesc!.Value;
                    var raster2 = psRenderState2.RasterizerStateDesc!.Value;
                    await Assert.That(raster2.FillMode).IsEqualTo(raster1.FillMode);
                    await Assert.That(raster2.CullMode).IsEqualTo(raster1.CullMode);
                    await Assert.That(raster2.DepthClipEnable).IsEqualTo(raster1.DepthClipEnable);
                    await Assert.That(raster2.MultisampleEnable).IsEqualTo(raster1.MultisampleEnable);
                    await Assert.That(raster2.DepthBias).IsEqualTo(raster1.DepthBias);
                    await Assert.That(raster2.DepthBiasClamp).IsEqualTo(raster1.DepthBiasClamp);
                    await Assert.That(raster2.SlopeScaledDepthBias).IsEqualTo(raster1.SlopeScaledDepthBias);

                    var blend1 = psRenderState1.BlendStateDesc!.Value;
                    var blend2 = psRenderState2.BlendStateDesc!.Value;

                    await Assert.That(blend2.AlphaToCoverageEnable).IsEqualTo(blend1.AlphaToCoverageEnable);
                    await Assert.That(blend2.IndependentBlendEnable).IsEqualTo(blend1.IndependentBlendEnable);

                    for (var t = 0; t < RsBlendStateDesc.MaxRenderTargets; t++)
                    {
                        await Assert.That(blend2.BlendEnable[t]).IsEqualTo(blend1.BlendEnable[t]);
                        await Assert.That(blend2.SrcBlend[t]).IsEqualTo(blend1.SrcBlend[t]);
                        await Assert.That(blend2.DestBlend[t]).IsEqualTo(blend1.DestBlend[t]);
                        await Assert.That(blend2.BlendOp[t]).IsEqualTo(blend1.BlendOp[t]);
                        await Assert.That(blend2.SrcBlendAlpha[t]).IsEqualTo(blend1.SrcBlendAlpha[t]);
                        await Assert.That(blend2.DestBlendAlpha[t]).IsEqualTo(blend1.DestBlendAlpha[t]);
                        await Assert.That(blend2.BlendOpAlpha[t]).IsEqualTo(blend1.BlendOpAlpha[t]);
                        await Assert.That(blend2.RenderTargetWriteMask[t]).IsEqualTo(blend1.RenderTargetWriteMask[t]);
                        await Assert.That(blend2.SrgbWriteEnable[t]).IsEqualTo(blend1.SrgbWriteEnable[t]);
                    }
                }

                await Assert.That(combo2.Attributes).IsEquivalentTo(combo1.Attributes, CollectionOrdering.Matching);
                await Assert.That(combo2.ShaderFiles).Count().IsEqualTo(combo1.ShaderFiles.Length);
            }
        }

        [Test]
        public async Task TestVcsFileName()
        {
            var testCases = new (string FileName, string ShaderName, VcsPlatformType Platform, VcsShaderModelType ShaderModel, VcsProgramType ProgramType)[]
            {
                ("/sourcedir/multiblend_pcgl_40_ps.vcs", "multiblend", VcsPlatformType.PCGL, VcsShaderModelType._40, VcsProgramType.PixelShader),
                ("/sourcedir/solid_sky_pcgl_30_features.vcs", "solid_sky", VcsPlatformType.PCGL, VcsShaderModelType._30, VcsProgramType.Features),
                ("/sourcedir/copytexture_pc_30_ps.vcs", "copytexture", VcsPlatformType.PC, VcsShaderModelType._30, VcsProgramType.PixelShader),
                ("/sourcedir/copytexture_pc_40_ps.vcs", "copytexture", VcsPlatformType.PC, VcsShaderModelType._40, VcsProgramType.PixelShader),
                ("/sourcedir/deferred_shading_pc_41_ps.vcs", "deferred_shading", VcsPlatformType.PC, VcsShaderModelType._41, VcsProgramType.PixelShader),
                ("/sourcedir/bloom_dota_mobile_gles_30_ps.vcs", "bloom_dota", VcsPlatformType.MOBILE_GLES, VcsShaderModelType._30, VcsProgramType.PixelShader),
                ("/sourcedir/cs_volumetric_fog_vulkan_50_cs.vcs", "cs_volumetric_fog", VcsPlatformType.VULKAN, VcsShaderModelType._50, VcsProgramType.ComputeShader),
                ("/sourcedir/bloom_dota_ios_vulkan_40_ps.vcs", "bloom_dota", VcsPlatformType.IOS_VULKAN, VcsShaderModelType._40, VcsProgramType.PixelShader),
                ("/sourcedir/flow_map_preview_android_vulkan_40_vs.vcs", "flow_map_preview", VcsPlatformType.ANDROID_VULKAN, VcsShaderModelType._40, VcsProgramType.VertexShader),
            };

            foreach (var testCase in testCases)
            {
                var result = ComputeVCSFileName(testCase.FileName);
                var opposite = ComputeVCSFileName(testCase.ShaderName, testCase.ProgramType, testCase.Platform, testCase.ShaderModel);

                using (Assert.Multiple())
                {
                    await Assert.That(result.ShaderName).IsEqualTo(testCase.ShaderName);
                    await Assert.That(result.PlatformType).IsEqualTo(testCase.Platform);
                    await Assert.That(result.ShaderModelType).IsEqualTo(testCase.ShaderModel);
                    await Assert.That(result.ProgramType).IsEqualTo(testCase.ProgramType);
                    await Assert.That(opposite).IsEqualTo(Path.GetFileName(testCase.FileName));
                }
            }
        }

        [Test]
        public async Task CompiledShaderInResourceThrows()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var resource = new Resource();

            var ex = Assert.ThrowsExactly<InvalidDataException>(() => resource.Read(path));
            await Assert.That(ex).IsNotNull();
        }

        [Test]
        public async Task TestWriteSequences()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var staticCombo = shader.GetStaticCombo(0);
            using var sw = new IndentedTextWriter();
            _ = new PrintStaticComboSummary(staticCombo, sw);

            var (uniqueSequences, indexToSequence) = staticCombo.GetWriteSequences();
            await Assert.That(uniqueSequences.Count).IsEqualTo(1);

            var expected = new Dictionary<int, int>
            {
                {-1, 0},
                {0, 0},
            };

            // Only the block to sequence mapping matters, not the order the entries come out in.
            await Assert.That(indexToSequence).IsEquivalentTo(expected);
        }

        [Test]
        public async Task TestChannelMapping()
        {
            using (Assert.Multiple())
            {
                await Assert.That(ChannelMapping.R.PackedValue).IsEqualTo((uint)0xFFFFFF00);
                await Assert.That(ChannelMapping.G.PackedValue).IsEqualTo((uint)0xFFFFFF01);
                await Assert.That(ChannelMapping.B.PackedValue).IsEqualTo((uint)0xFFFFFF02);
                await Assert.That(ChannelMapping.A.PackedValue).IsEqualTo((uint)0xFFFFFF03);
                await Assert.That(ChannelMapping.RGB.PackedValue).IsEqualTo((uint)0xFF020100);
                await Assert.That(ChannelMapping.RGBA.PackedValue).IsEqualTo((uint)0x03020100);

                await Assert.That(ChannelMapping.RGBA.Channels[0]).IsEqualTo(ChannelMapping.Channel.R);
                await Assert.That(ChannelMapping.RGBA.Channels[1]).IsEqualTo(ChannelMapping.Channel.G);
                await Assert.That(ChannelMapping.AG.Channels[1]).IsEqualTo(ChannelMapping.Channel.G);

                await Assert.That(ChannelMapping.RGBA.Count).IsEqualTo(4);
                await Assert.That(ChannelMapping.RGB.Count).IsEqualTo(3);
                await Assert.That(ChannelMapping.RG.Count).IsEqualTo(2);
                await Assert.That(ChannelMapping.G.Count).IsEqualTo(1);
                await Assert.That(ChannelMapping.NULL.Count).IsZero();
                await Assert.That(ChannelMapping.RGBA.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.R, ChannelMapping.Channel.G, ChannelMapping.Channel.B, ChannelMapping.Channel.A], CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.RGB.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.R, ChannelMapping.Channel.G, ChannelMapping.Channel.B], CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.RG.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.R, ChannelMapping.Channel.G], CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.AG.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.A, ChannelMapping.Channel.G], CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.A.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.A], CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.R.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.R], CollectionOrdering.Matching);

                await Assert.That((byte)ChannelMapping.R).IsZero();
                await Assert.That((byte)ChannelMapping.G).IsEqualTo((byte)0x01);
                await Assert.That((byte)ChannelMapping.B).IsEqualTo((byte)0x02);
                await Assert.That((byte)ChannelMapping.A).IsEqualTo((byte)0x03);

                await Assert.That(ChannelMapping.R).IsEqualTo(ChannelMapping.FromUInt32(0xFFFFFF00));
                await Assert.That(ChannelMapping.G).IsEqualTo(ChannelMapping.FromUInt32(0xFFFFFF01));
                await Assert.That(ChannelMapping.AG).IsEqualTo(ChannelMapping.FromUInt32(0xFFFF0103));

                // Version 67 and newer pack the destination channel into the low nibble of each byte
                await Assert.That(ChannelMapping.RGBA).IsEqualTo(ChannelMapping.FromUInt32(0x33221100, packedDestinations: true));
                await Assert.That(ChannelMapping.AG).IsEqualTo(ChannelMapping.FromUInt32(0xFFFF1130, packedDestinations: true));

                await Assert.That(ChannelMapping.RGBA.Destinations).IsEquivalentTo(new byte[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);
                await Assert.That(ChannelMapping.AG.Destinations).IsEquivalentTo(new byte[] { 0, 1 }, CollectionOrdering.Matching);

                var rotated = ChannelMapping.FromUInt32(0x23120130, packedDestinations: true);
                await Assert.That(rotated.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.A, ChannelMapping.Channel.R, ChannelMapping.Channel.G, ChannelMapping.Channel.B], CollectionOrdering.Matching);
                await Assert.That(rotated.Destinations).IsEquivalentTo(new byte[] { 0, 1, 2, 3 }, CollectionOrdering.Matching);

                var offset = ChannelMapping.FromUInt32(0xFFFF1201, packedDestinations: true);
                await Assert.That(offset.ValidChannels).IsEquivalentTo([ChannelMapping.Channel.R, ChannelMapping.Channel.G], CollectionOrdering.Matching);
                await Assert.That(offset.Destinations).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);

                await Assert.That(ChannelMapping.R.ToString()).IsEqualTo("R");
                await Assert.That(ChannelMapping.G.ToString()).IsEqualTo("G");
                await Assert.That(ChannelMapping.B.ToString()).IsEqualTo("B");
                await Assert.That(ChannelMapping.A.ToString()).IsEqualTo("A");
                await Assert.That(ChannelMapping.AG.ToString()).IsEqualTo("AG");
                await Assert.That(ChannelMapping.RGB.ToString()).IsEqualTo("RGB");
                await Assert.That(ChannelMapping.NULL.ToString()).IsEqualTo("0xFFFFFFFF");

                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChannelMapping.FromChannels(0x04));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ChannelMapping.FromChannels(0x05));

                await Assert.That(ChannelMapping.FromChannels(0xFF)).IsEqualTo(ChannelMapping.NULL);
            }
        }

        [Test]
        public async Task VfxShaderExtract_ReplacesWholeIdentifiersOnly()
        {
            using (Assert.Multiple())
            {
                await Assert.That(ShaderExtract.ReplaceIdentifier("g_flCubeMapBlur*g_flCubeMapBlurAmount", "g_flCubeMapBlur", "this")).IsEqualTo("this*g_flCubeMapBlurAmount");
                await Assert.That(ShaderExtract.ReplaceIdentifier("float2(g_vScale.x,g_vScale.y)", "g_vScale", "this")).IsEqualTo("float2(this.x,this.y)");
                await Assert.That(ShaderExtract.ReplaceIdentifier("g_flAmount", "g_flAmountExtra", "this")).IsEqualTo("g_flAmount");
            }
        }

        [Test]
        public async Task VfxShaderExtract_RenderStateEnumNames()
        {
            var colorWriteEnable = typeof(RsColorWriteEnableBits);
            var cullMode = typeof(RsCullMode);

            using (Assert.Multiple())
            {
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 3)).IsEqualTo("R|G");
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 5)).IsEqualTo("R|B");
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 7)).IsEqualTo("R|G|B");
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 14)).IsEqualTo("G|B|A");
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 15)).IsEqualTo("All");
                await Assert.That(ShaderUtilHelpers.GetEnumName(colorWriteEnable, 0)).IsEqualTo("None");
                await Assert.That(ShaderUtilHelpers.GetEnumName(cullMode, 42)).IsEqualTo("42");
            }
        }

        [Test]
        public async Task VfxShaderExtract_Invalid()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => _ = new ShaderExtract(ShaderCollection.FromEnumerable([shader])));

            Debug.Assert(ex != null);
            await Assert.That(ex).IsNotNull();
            await Assert.That(ex.Message).Contains("cannot continue without at least a features file");
        }

        [Test]
        public async Task VfxShaderExtract_Minimal()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pc_40_features.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var extract = new ShaderExtract(ShaderCollection.FromEnumerable([shader]));

            var vfx = extract.ToVFX(ShaderExtract.ShaderExtractParams.Inspect);
            vfx = extract.ToVFX(ShaderExtract.ShaderExtractParams.Export);

            await Assert.That(vfx.VfxContent).Contains("Description = \"Error shader\"");
            await Assert.That(vfx.VfxContent).Contains("DevShader = true");
        }

        [Test]
        public async Task VfxShaderExtract_OptionsTest()
        {
            using var collection = new ShaderCollection();
            foreach (var file in Directory.GetFiles(ShadersDir, "vcs64_error_pc_40_*.vcs"))
            {
                var shader = new VfxProgramData();

                try
                {
                    shader.Read(file);
                    collection.Add(shader);
                    shader = null;
                }
                finally
                {
                    shader?.Dispose();
                }
            }

            var extract = new ShaderExtract(collection);

            var optionsToTest = new[]
            {
                ShaderExtract.ShaderExtractParams.Inspect,
                ShaderExtract.ShaderExtractParams.Export,
                new ShaderExtract.ShaderExtractParams { },
                new ShaderExtract.ShaderExtractParams { CollapseBuffers_InInclude = true },
                new ShaderExtract.ShaderExtractParams { StaticComboReadingCap = -1 },
                new ShaderExtract.ShaderExtractParams { StaticComboReadingCap = 0 },
                new ShaderExtract.ShaderExtractParams { StaticComboReadingCap = 1 },
                new ShaderExtract.ShaderExtractParams { StaticComboAttributes_NoSeparateGlobals = true },
                new ShaderExtract.ShaderExtractParams { StaticComboAttributes_NoConditionalReduce = true },
            };

            foreach (var options in optionsToTest)
            {
                var vfx = extract.ToVFX(options);
                await Assert.That(vfx.VfxContent).Contains("Description = \"Error shader\"");
                await Assert.That(vfx.VfxContent).Contains("DevShader = true");
            }
        }

        public static IEnumerable<(string, int, int)> SpirvReflectionTestCases()
        {
            yield return ("vcs65_compute_depthbin_cullbits_vulkan_50_cs.vcs", 0, 0);
            yield return ("vcs68_tower_force_field_vulkan_40_vs.vcs", 0, 9);
            yield return ("vcs68_tower_force_field_vulkan_40_ps.vcs", 1, 1);
            yield return ("vcs68_csgo_simple_2way_blend_vulkan_60_rtx.vcs", 0x6, 0);
            yield return ("vcs68_test_vulkan_60_ms.vcs", 0, 1);
            yield return ("vcs69_downsample_depth_cs_vulkan_50_cs.vcs", 0, 0x20);
            yield return ("vcs69_zstd5_npr_dummy_vulkan_50_vs.vcs", 0, 0);
            yield return ("vcs69_bloom_vulkan_40_ps.vcs", 0, 0);
            yield return ("vcs70_resource_bloom_vulkan_40_ps.vcs", 0, 0);
            yield return ("vcs72_test_vulkan_60_ps.vcs", 0, 0);
        }

        [Test, MethodDataSource(nameof(SpirvReflectionTestCases))]
        public async Task TestSpirvReflection(string shaderFile, int staticCombo, int dynamicCombo)
        {
            if (!IsSpirvCrossAvailable())
            {
                Skip.Test("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, shaderFile);
            using var shader = new VfxProgramData();
            shader.Read(path);

            var staticComboEntry = shader.GetStaticCombo(staticCombo);
            var dynamicComboEntry = staticComboEntry.DynamicComboRenderStates[dynamicCombo];
            var code = staticComboEntry.ShaderFiles[dynamicComboEntry.ShaderFileId].GetDecompiledFile();
            code = code.Replace(StringToken.VRF_GENERATOR, "VRF-TEST", StringComparison.Ordinal);

            var referencePath = Path.Combine(ShadersDir, "SpirvOutput", $"{shaderFile}.glsl");

            /*{
                var shadersDirRepo = Path.Combine(TestContext.TestDirectory!, "../../", "Files", "Shaders");
                var referencePathRepo = Path.Combine(shadersDirRepo, "SpirvOutput", $"{shaderFile}.glsl");
                File.WriteAllText(referencePathRepo, code);
                return;
            }*/

            var reference = await File.ReadAllTextAsync(referencePath);
            await Assert.That(code).IsEqualTo(reference).IgnoringWhitespace().Because("Spirv reflection output does not match reference.");
        }

        public static IEnumerable<(string, string)> Clamp01TestCases()
        {
            // The clamped expression routinely contains calls and their commas.
            yield return ("clamp(x, 0.0, 1.0)", "saturate(x)");
            yield return ("clamp(-x, 0.0, 1.0)", "saturate(-x)");
            yield return ("clamp(dot(a, b), 0.0, 1.0)", "saturate(dot(a, b))");
            yield return ("clamp(1.0 - dot(a.xy, a.xy), 0.0, 1.0)", "saturate(1.0 - dot(a.xy, a.xy))");
            yield return ("clamp((f(a) - b) / (g(c, d) - b), 0.0, 1.0)", "saturate((f(a) - b) / (g(c, d) - b))");

            // Both backends' spelling of the bounds, including the hlsl float suffix.
            yield return ("clamp(x, 0.0f, 1.0f)", "saturate(x)");
            yield return ("clamp(x, 0, 1)", "saturate(x)");
            yield return ("clamp(x, vec3(0.0), vec3(1.0))", "saturate(x)");
            yield return ("clamp(x, float4(0.0f, 0.0f, 0.0f, 0.0f), float4(1.0f, 1.0f, 1.0f, 1.0f))", "saturate(x)");

            // Nested clamps need more than one pass, Replace resumes after the outer match.
            yield return ("clamp(min(m, pow(clamp(x, 0.0, 1.0), e)), 0.0, 1.0)", "saturate(min(m, pow(saturate(x), e)))");

            // Anything that is not a 0..1 clamp has to survive untouched.
            yield return ("clamp(x, 0.0, 0.5)", "clamp(x, 0.0, 0.5)");
            yield return ("clamp(x, 0.5, 1.0)", "clamp(x, 0.5, 1.0)");
            yield return ("clamp(x, 10.0, 1.0)", "clamp(x, 10.0, 1.0)");
            yield return ("clamp(x, 0.05, 1.0)", "clamp(x, 0.05, 1.0)");
            yield return ("clamp(x, y, 1.0)", "clamp(x, y, 1.0)");
            yield return ("unclamped(x, 0.0, 1.0)", "unclamped(x, 0.0, 1.0)");
        }

        [Test, MethodDataSource(nameof(Clamp01TestCases))]
        public async Task TestClamp01Rewrite(string input, string expected)
        {
            await Assert.That(ShaderSpirvReflection.ReplaceCommonPatterns(input)).IsEqualTo(expected);
        }

        [Test]
        public async Task TestSpirvReflectionNormalization()
        {
            if (!IsSpirvCrossAvailable())
            {
                Skip.Test("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, "vcs69_zstd5_npr_dummy_vulkan_50_vs.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var shaderFile = (VfxShaderFileVulkan)shader.GetStaticCombo(0).ShaderFiles[0];
            var raw = shaderFile.GetDecompiledFile(Backend.HLSL, SpirvReflectionOptions.Default);
            var clean = shaderFile.GetDecompiledFile(Backend.HLSL, SpirvReflectionOptions.Clean);

            using (Assert.Multiple())
            {
                // SPIR-V ids differ between combos even when the code is the same, so none may survive.
                await Assert.That(Regex.IsMatch(raw, @"\b_\d+\b")).IsTrue().Because("Expected the raw output to contain SPIR-V id derived names.");
                await Assert.That(Regex.IsMatch(clean, @"\b_\d+(?:ident)?\b")).IsFalse();

                await Assert.That(raw).Contains("PerViewConstantBuffer_t_1_g_matWorldToProjection");
                await Assert.That(clean).Contains("g_matWorldToProjection");
                await Assert.That(clean).DoesNotContain("PerViewConstantBuffer_t_1_g_matWorldToProjection");

                // Rewriting must not drop or duplicate any statement.
                await Assert.That(clean.Count(c => c == ';')).IsEqualTo(raw.Count(c => c == ';'));
            }
        }

        [Test]
        public async Task TestDepthStencilStateBitLayouts()
        {
            // Depth test+write with LessEqual, stencil disabled with Always funcs and full masks. The bit layout changed in version 71.
            var v71 = new RsDepthStencilStateDesc(0xFFFF00000077000FUL, 71);

            using (Assert.Multiple())
            {
                await Assert.That(v71.DepthTestEnable).IsTrue();
                await Assert.That(v71.DepthWriteEnable).IsTrue();
                await Assert.That(v71.DepthFunc).IsEqualTo(RsComparison.LessEqual);
                await Assert.That(v71.StencilEnable).IsFalse();
                await Assert.That(v71.FrontStencilFunc).IsEqualTo(RsComparison.Always);
                await Assert.That(v71.BackStencilFunc).IsEqualTo(RsComparison.Always);
                await Assert.That(v71.StencilReadMask).IsEqualTo((byte)0xFF);
                await Assert.That(v71.StencilWriteMask).IsEqualTo((byte)0xFF);
            }

            // Value from vcs70 and older: depth disabled, LessEqual, Always funcs.
            var v70 = new RsDepthStencilStateDesc(0xFFFF01C01C000300UL, 70);

            using (Assert.Multiple())
            {
                await Assert.That(v70.DepthTestEnable).IsFalse();
                await Assert.That(v70.DepthWriteEnable).IsFalse();
                await Assert.That(v70.DepthFunc).IsEqualTo(RsComparison.LessEqual);
                await Assert.That(v70.StencilEnable).IsFalse();
                await Assert.That(v70.FrontStencilFunc).IsEqualTo(RsComparison.Always);
                await Assert.That(v70.BackStencilFunc).IsEqualTo(RsComparison.Always);
                await Assert.That(v70.StencilReadMask).IsEqualTo((byte)0xFF);
                await Assert.That(v70.StencilWriteMask).IsEqualTo((byte)0xFF);
            }
        }

        [Test]
        public async Task TestUiGroup()
        {
            var testCases = new Dictionary<string, UiGroup>
            {
                ["heading,10/2"] = new("heading", 10, variableOrder: 2),
                ["heading,12/group,12/5"] = new("heading", 12, "group", 12, 5),
                ["Interaction Effects, 500,20"] = new("Interaction Effects", headingOrder: 500),

                [string.Empty] = new(),
                ["h,1/g,2"] = new("h", 1, variableOrder: 2),
                ["h,1/g"] = new("h", 1),
                ["h,1"] = new("h", 1),
                ["h"] = new("h"),

                ["//////"] = new(),
                ["z,z,z/z,z,z,z/z,z,z,z/,z,z,z"] = new(heading: "z,z,z", group: "z,z,z,z"),
            };

            foreach (var (compactString, expected) in testCases)
            {
                var parsed = UiGroup.FromCompactString(compactString);
                using (Assert.Multiple())
                {
                    await Assert.That(parsed.Heading).IsEqualTo(expected.Heading);
                    await Assert.That(parsed.HeadingOrder).IsEqualTo(expected.HeadingOrder);
                    await Assert.That(parsed.Group).IsEqualTo(expected.Group);
                    await Assert.That(parsed.GroupOrder).IsEqualTo(expected.GroupOrder);
                    await Assert.That(parsed.VariableOrder).IsEqualTo(expected.VariableOrder);
                }
            }
        }

        [Test]
        public async Task TestDxbcReflectionShaderModel4()
        {
            // Reflection is stripped from most shipped blobs. This fixture is one of the few checked in that
            // keeps a populated one, and being shader model 4 it exercises the 24 byte variable descriptor.
            using var shader = new VfxProgramData();
            shader.Read(Path.Combine(ShadersDir, "vcs64_error_pc_40_vs.vcs"));

            DxbcReflection? reflection = null;

            foreach (var variant in VfxComboResolver.EnumerateVariants(shader))
            {
                if (variant.ShaderFile is VfxShaderFileDXBC dxbc && dxbc.TryGetReflection(out reflection))
                {
                    break;
                }
            }

            await Assert.That(reflection).IsNotNull().Because("the fixture was expected to retain a populated RDEF chunk");

            string[] expectedBindings =
                ["g_tTransformTexture_sampler", "g_tTransformTexture", "PerViewConstantBuffer_t"];

            using (Assert.Multiple())
            {
                await Assert.That(reflection!.ShaderModelMajor).IsEqualTo(4);
                await Assert.That(reflection.Creator).Contains("Shader Compiler");
                await Assert.That(reflection.ResourceBindings.Select(b => b.Name)).IsEquivalentTo(expectedBindings);
                await Assert.That(reflection.ConstantBuffers.Count).IsEqualTo(1);
            }

            var buffer = reflection!.ConstantBuffers[0];

            using (Assert.Multiple())
            {
                await Assert.That(buffer.Name).IsEqualTo("PerViewConstantBuffer_t");
                await Assert.That(buffer.Members.Count).IsEqualTo(39);
                await Assert.That(buffer.Members[0].Name).IsEqualTo("g_matWorldToProjection");
                await Assert.That(buffer.Members[0].PackOffset).IsEqualTo("c0");
                await Assert.That(buffer.Members[0].Size).IsEqualTo(64);
                await Assert.That(buffer.Members[0].IsUsed).IsTrue();
            }

            // A wrong descriptor stride reads names from the middle of other records, so requiring every one to
            // be an identifier that sits inside the buffer is what actually pins the layout down.
            foreach (var member in buffer.Members)
            {
                await Assert.That(Regex.IsMatch(member.Name, "^[A-Za-z_$][A-Za-z0-9_$]*$")).IsTrue();
                await Assert.That(member.Size).IsGreaterThan(0);
                await Assert.That(member.Offset + member.Size).IsLessThanOrEqualTo(buffer.Size);
            }
        }

        [Test]
        public async Task TestDxbcReflectionShaderModel5Stride()
        {
            // Every CS2 shader is shader model 5, where an "RD11" header declares a 40 byte variable descriptor
            // instead of shader model 4's 24. No checked in fixture has a populated SM5 chunk -- the SM5 ones
            // keep the chunk but with emptied tables -- so this builds a minimal container to cover that path.
            var bytecode = BuildShaderModel5Rdef();

            await Assert.That(DxbcReflection.TryParse(bytecode, out var parsed)).IsTrue();
            await Assert.That(parsed).IsNotNull();

            var reflection = parsed!;

            using (Assert.Multiple())
            {
                await Assert.That(reflection.ShaderModelMajor).IsEqualTo(5);
                await Assert.That(reflection.ShaderModelMinor).IsEqualTo(0);
                await Assert.That(reflection.Creator).IsEqualTo("test");

                await Assert.That(reflection.ResourceBindings.Count).IsEqualTo(1);
                await Assert.That(reflection.ResourceBindings[0].Name).IsEqualTo("g_tTexture");
                await Assert.That(reflection.ResourceBindings[0].Type).IsEqualTo(DxbcResourceType.Texture);
                await Assert.That(reflection.ResourceBindings[0].Register).IsEqualTo("t7");

                await Assert.That(reflection.ConstantBuffers.Count).IsEqualTo(1);
            }

            var buffer = reflection.ConstantBuffers[0];

            using (Assert.Multiple())
            {
                await Assert.That(buffer.Name).IsEqualTo("MyControls_t");
                await Assert.That(buffer.Members.Count).IsEqualTo(2);

                // Reading these at a 24 byte stride would land the second name inside the first record.
                await Assert.That(buffer.Members[0].Name).IsEqualTo("g_vFirst");
                await Assert.That(buffer.Members[0].PackOffset).IsEqualTo("c0");
                await Assert.That(buffer.Members[0].IsUsed).IsTrue();

                await Assert.That(buffer.Members[1].Name).IsEqualTo("g_flSecond");
                await Assert.That(buffer.Members[1].PackOffset).IsEqualTo("c1.z");
                await Assert.That(buffer.Members[1].IsUsed).IsFalse();
            }
        }

        /// <summary>
        /// Builds the smallest DXBC container that carries a populated shader model 5 RDEF chunk: one constant
        /// buffer with two members and one texture binding.
        /// </summary>
        private static byte[] BuildShaderModel5Rdef()
        {
            const int ConstantBufferDescriptor = 24;
            const int ResourceBindingDescriptor = 32;
            const int VariableDescriptor = 40;

            const int HeaderSize = 60;                                  // 28 byte header plus the RD11 block
            const int CbufferTable = HeaderSize;
            const int VariableTable = CbufferTable + ConstantBufferDescriptor;
            const int ResourceTable = VariableTable + (2 * VariableDescriptor);
            const int StringTable = ResourceTable + ResourceBindingDescriptor;

            var strings = new List<byte>();

            int AddString(string value)
            {
                var at = StringTable + strings.Count;
                strings.AddRange(Encoding.ASCII.GetBytes(value));
                strings.Add(0);
                return at;
            }

            var creatorOffset = AddString("test");
            var bufferNameOffset = AddString("MyControls_t");
            var firstNameOffset = AddString("g_vFirst");
            var secondNameOffset = AddString("g_flSecond");
            var textureNameOffset = AddString("g_tTexture");

            var payload = new byte[StringTable + strings.Count];

            void Write(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(at), value);

            Write(0, 1);                                                // constant buffer count
            Write(4, CbufferTable);
            Write(8, 1);                                                // resource binding count
            Write(12, ResourceTable);
            payload[16] = 0;                                            // shader model minor
            payload[17] = 5;                                            // shader model major
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0xFFFE);
            Write(20, 0);                                               // flags
            Write(24, creatorOffset);
            "RD11"u8.CopyTo(payload.AsSpan(28));
            Write(32, HeaderSize);
            Write(36, ConstantBufferDescriptor);
            Write(40, ResourceBindingDescriptor);
            Write(44, VariableDescriptor);                              // the size this test is about
            Write(48, 36);
            Write(52, 12);
            Write(56, 0);

            Write(CbufferTable + 0, bufferNameOffset);
            Write(CbufferTable + 4, 2);                                 // member count
            Write(CbufferTable + 8, VariableTable);
            Write(CbufferTable + 12, 32);                               // buffer size

            Write(VariableTable + 0, firstNameOffset);
            Write(VariableTable + 4, 0);                                // c0
            Write(VariableTable + 8, 16);
            Write(VariableTable + 12, 2);                               // used

            Write(VariableTable + VariableDescriptor + 0, secondNameOffset);
            Write(VariableTable + VariableDescriptor + 4, 24);           // c1.z
            Write(VariableTable + VariableDescriptor + 8, 4);
            Write(VariableTable + VariableDescriptor + 12, 0);           // unused

            Write(ResourceTable + 0, textureNameOffset);
            Write(ResourceTable + 4, (int)DxbcResourceType.Texture);
            Write(ResourceTable + 20, 7);                               // bind point
            Write(ResourceTable + 24, 1);                               // bind count

            strings.CopyTo(payload, StringTable);

            // Wrap the payload in a container: magic, digest, version, size, chunk count, offset table.
            var container = new byte[36 + 8 + payload.Length];
            var outer = container.AsSpan();
            "DXBC"u8.CopyTo(outer);
            BinaryPrimitives.WriteInt32LittleEndian(outer[20..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(outer[24..], container.Length);
            BinaryPrimitives.WriteInt32LittleEndian(outer[28..], 1);
            BinaryPrimitives.WriteInt32LittleEndian(outer[32..], 36);
            "RDEF"u8.CopyTo(outer[36..]);
            BinaryPrimitives.WriteInt32LittleEndian(outer[40..], payload.Length);
            payload.CopyTo(container, 44);

            return container;
        }

        [Test]
        public async Task TestDxbcReflectionAbsentFromNonDirectXShaders()
        {
            // A Vulkan build stores SPIR-V rather than DXBC, so there is nothing to find. The API has to say so
            // rather than throw, because a stripped or non-DirectX blob is the common case, not an error.
            using var shader = new VfxProgramData();
            shader.Read(Path.Combine(ShadersDir, "vcs64_error_vulkan_40_vs.vcs"));

            foreach (var variant in VfxComboResolver.EnumerateVariants(shader))
            {
                await Assert.That(variant.ShaderFile is VfxShaderFileDXBC).IsFalse();
                await Assert.That(DxbcReflection.TryParse(variant.ShaderFile.Bytecode, out var reflection)).IsFalse();
                await Assert.That(reflection).IsNull();
            }
        }

        [Test]
        public async Task TestDxbcPackOffsetFormatting()
        {
            using (Assert.Multiple())
            {
                await Assert.That(new DxbcConstantBufferMember("a", 0, 4, true).PackOffset).IsEqualTo("c0");
                await Assert.That(new DxbcConstantBufferMember("a", 4, 4, true).PackOffset).IsEqualTo("c0.y");
                await Assert.That(new DxbcConstantBufferMember("a", 8, 4, true).PackOffset).IsEqualTo("c0.z");
                await Assert.That(new DxbcConstantBufferMember("a", 12, 4, true).PackOffset).IsEqualTo("c0.w");
                await Assert.That(new DxbcConstantBufferMember("a", 184, 8, true).PackOffset).IsEqualTo("c11.z");
                await Assert.That(new DxbcConstantBufferMember("a", 256, 16, true).PackOffset).IsEqualTo("c16");
            }
        }
    }
}
