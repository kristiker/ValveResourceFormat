using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;
using ValveResourceFormat.Utils;
using Vortice.SpirvCross;
using static ValveResourceFormat.CompiledShader.ShaderUtilHelpers;

namespace Tests
{
    public class ShaderTest
    {
        public static string ShadersDir
            => Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "Shaders");

        [Test]
        public void ParseShaders()
        {
            var files = Directory.GetFiles(ShadersDir, "*.vcs");

            foreach (var file in files)
            {
                using var shader = new VfxProgramData();
                shader.Read(file);

                using var sw = new IndentedTextWriter();

                shader.PrintSummary(sw);
                Assert.That(sw.ToString(), Has.Length.AtLeast(100));

                foreach (var zframe in shader.StaticComboEntries)
                {
                    var value = zframe.Value.Unserialize();
                    Assert.That(value, Is.Not.Null);
                    var zframeSummary = new PrintZFrameSummary(value, sw);
                }
            }
        }

        [Test]
        public void ShaderResourceDataMatchesBinary()
        {
            using var shader1 = new VfxProgramData();
            using var shader2 = new VfxProgramData();

            shader1.Read(Path.Combine(ShadersDir, "vcs69_bloom_vulkan_40_ps.vcs"));
            shader2.Read(Path.Combine(ShadersDir, "vcs70_resource_bloom_vulkan_40_ps.vcs"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shader2.VcsProgramType, Is.EqualTo(shader1.VcsProgramType));
                Assert.That(shader2.VcsPlatformType, Is.EqualTo(shader1.VcsPlatformType));
                Assert.That(shader2.VcsShaderModelType, Is.EqualTo(shader1.VcsShaderModelType));
                Assert.That(shader2.FileHash, Is.EqualTo(shader1.FileHash));
                Assert.That(shader2.VariableSourceMax, Is.EqualTo(shader1.VariableSourceMax));

                // Binary stores one hash, KV3 stores all hashes
                // Assert.That(shader1.HashesMD5, Is.EqualTo(shader2.HashesMD5));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shader2.DynamicComboArray, Has.Length.EqualTo(shader1.DynamicComboArray.Length));
                for (var i = 0; i < shader1.DynamicComboArray.Length; i++)
                {
                    var combo1 = shader1.DynamicComboArray[i];
                    var combo2 = shader2.DynamicComboArray[i];

                    Assert.That(combo2.Name, Is.EqualTo(combo1.Name));
                    Assert.That(combo2.CalculatedComboId, Is.EqualTo(combo1.CalculatedComboId));
                    Assert.That(combo2.AliasName, Is.EqualTo(combo1.AliasName));
                    Assert.That(combo2.ComboType, Is.EqualTo(combo1.ComboType));
                    Assert.That(combo2.ComboSourceType, Is.EqualTo(combo1.ComboSourceType));
                    Assert.That(combo2.FeatureComparisonValue, Is.EqualTo(combo1.FeatureComparisonValue));
                    Assert.That(combo2.RangeMin, Is.EqualTo(combo1.RangeMin));
                    Assert.That(combo2.RangeMax, Is.EqualTo(combo1.RangeMax));
                    Assert.That(combo2.Strings, Is.EquivalentTo(combo1.Strings));
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shader2.DynamicComboRules, Has.Length.EqualTo(shader1.DynamicComboRules.Length));
                for (var i = 0; i < shader1.DynamicComboRules.Length; i++)
                {
                    var rule1 = shader1.DynamicComboRules[i];
                    var rule2 = shader2.DynamicComboRules[i];

                    Assert.That(rule2.Rule, Is.EqualTo(rule1.Rule));
                    Assert.That(rule2.RuleType, Is.EqualTo(rule1.RuleType));
                    Assert.That(rule2.ConditionalTypes, Is.EquivalentTo(rule1.ConditionalTypes));
                    Assert.That(rule2.Indices, Is.EquivalentTo(rule1.Indices));
                    Assert.That(rule2.Values, Is.EquivalentTo(rule1.Values));
                    Assert.That(rule2.ExtraRuleData, Is.EquivalentTo(rule1.ExtraRuleData));
                    Assert.That(rule2.Description, Is.EqualTo(rule1.Description));
                }

                Assert.That(shader2.VariableDescriptions, Has.Length.EqualTo(shader1.VariableDescriptions.Length));
                for (var i = 0; i < shader1.VariableDescriptions.Length; i++)
                {
                    var var1 = shader1.VariableDescriptions[i];
                    var var2 = shader2.VariableDescriptions[i];

                    Assert.That(var2.Name, Is.EqualTo(var1.Name));
                    Assert.That(var2.UiGroup, Is.EqualTo(var1.UiGroup));
                    Assert.That(var2.StringData, Is.EqualTo(var1.StringData));
                    Assert.That(var2.UiType, Is.EqualTo(var1.UiType));
                    Assert.That(var2.UiStep, Is.EqualTo(var1.UiStep));
                    Assert.That(var2.VariableSource, Is.EqualTo(var1.VariableSource));
                    Assert.That(var2.DynExp, Is.EquivalentTo(var1.DynExp));
                    Assert.That(var2.UiVisibilityExp, Is.EquivalentTo(var1.UiVisibilityExp));
                    Assert.That(var2.SourceIndex, Is.EqualTo(var1.SourceIndex));
                    Assert.That(var2.VfxType, Is.EqualTo(var1.VfxType));
                    Assert.That(var2.RegisterType, Is.EqualTo(var1.RegisterType));
                    Assert.That(var2.ContextStateAffectedByVariable, Is.EqualTo(var1.ContextStateAffectedByVariable));
                    Assert.That(var2.RegisterElements, Is.EqualTo(var1.RegisterElements));
                    Assert.That(var2.ExtConstantBufferId, Is.EqualTo(var1.ExtConstantBufferId));
                    Assert.That(var2.DefaultInputTexture, Is.EqualTo(var1.DefaultInputTexture));
                    Assert.That(var2.IntDefs, Is.EquivalentTo(var1.IntDefs), var2.Name);
                    Assert.That(var2.IntMins, Is.EquivalentTo(var1.IntMins), var2.Name);
                    Assert.That(var2.IntMaxs, Is.EquivalentTo(var1.IntMaxs), var2.Name);
                    Assert.That(var2.FloatDefs, Is.EquivalentTo(var1.FloatDefs), var2.Name);
                    Assert.That(var2.FloatMins, Is.EquivalentTo(var1.FloatMins), var2.Name);
                    Assert.That(var2.FloatMaxs, Is.EquivalentTo(var1.FloatMaxs), var2.Name);
                    Assert.That(var2.ImageFormat, Is.EqualTo(var1.ImageFormat));
                    Assert.That(var2.ChannelCount, Is.EqualTo(var1.ChannelCount));
                    Assert.That(var2.ChannelIndices, Is.EquivalentTo(var1.ChannelIndices));
                    Assert.That(var2.ColorMode, Is.EqualTo(var1.ColorMode));
                    Assert.That(var2.ImageSuffix, Is.EqualTo(var1.ImageSuffix));
                    Assert.That(var2.ImageProcessor, Is.EqualTo(var1.ImageProcessor));

                    Assert.That(var2.MinPrecisionBits, Is.EqualTo(var1.MinPrecisionBits));
                    Assert.That(var2.LayerId, Is.EqualTo(var1.LayerId));
                    Assert.That(var2.AllowLayerOverride, Is.EqualTo(var1.AllowLayerOverride));
                    Assert.That(var2.MaxRes, Is.EqualTo(var1.MaxRes));
                    Assert.That(var2.IsLayerConstant, Is.EqualTo(var1.IsLayerConstant));
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(shader2.StaticComboEntries, Has.Count.EqualTo(shader1.StaticComboEntries.Count));

                var combo1 = shader1.GetStaticCombo(0);
                var combo2 = shader2.GetStaticCombo(0);

                // KV3 has one less item in some arrays
                const int OneLessItemKV3 = 1;

                Assert.That(combo2.StaticComboId, Is.EqualTo(combo1.StaticComboId));
                Assert.That(combo2.VShaderInputs, Is.EqualTo(combo1.VShaderInputs));
                Assert.That(combo2.ConstantBufferBindInfoFlags, Is.EqualTo(combo1.ConstantBufferBindInfoFlags[..^OneLessItemKV3]));
                Assert.That(combo2.ConstantBufferBindInfoSlots, Is.EqualTo(combo1.ConstantBufferBindInfoSlots[..^OneLessItemKV3]));
                Assert.That(combo2.ConstantBufferSize, Is.EqualTo(combo1.ConstantBufferSize));
                Assert.That(combo2.Flagbyte0, Is.EqualTo(combo1.Flagbyte0));
                Assert.That(combo2.Flagbyte1, Is.EqualTo(combo1.Flagbyte1));
                Assert.That(combo2.Flagbyte2, Is.EqualTo(combo1.Flagbyte2));

                static void TestVfxVariableIndexArray(VfxVariableIndexArray binary, VfxVariableIndexArray kv3)
                {
                    using var _ = Assert.EnterMultipleScope();
                    Assert.That(kv3.BlockId, Is.EqualTo(binary.BlockId));
                    Assert.That(kv3.FirstRenderStateElement, Is.EqualTo(binary.FirstRenderStateElement));
                    Assert.That(kv3.FirstConstantElement, Is.EqualTo(binary.FirstConstantElement));
                    Assert.That(kv3.Fields, Is.EqualTo(binary.Fields));
                }

                TestVfxVariableIndexArray(combo1.VariablesFromStaticCombo, combo2.VariablesFromStaticCombo);

                // one less
                Assert.That(combo2.DynamicComboVariables, Has.Length.EqualTo(combo1.DynamicComboVariables.Length - OneLessItemKV3));
                for (var i = 0; i < combo2.DynamicComboVariables.Length; i++)
                {
                    TestVfxVariableIndexArray(combo1.DynamicComboVariables[i], combo2.DynamicComboVariables[i]);
                }

                Assert.That(combo2.DynamicCombos, Has.Length.EqualTo(combo1.DynamicCombos.Length));
                for (var i = 0; i < combo1.DynamicCombos.Length; i++)
                {
                    var dyn1 = combo1.DynamicCombos[i];
                    var dyn2 = combo2.DynamicCombos[i];

                    Assert.That(dyn2.ShaderFileId, Is.EqualTo(dyn1.ShaderFileId));
                    Assert.That(dyn2.DynamicComboId, Is.EqualTo(dyn1.DynamicComboId));

                    // Source pointer is binary only
                    // Assert.That(dyn2.SourcePointer, Is.EqualTo(dyn1.SourcePointer));

                    var psRenderState1 = dyn1 as VfxRenderStateInfoPixelShader;
                    var psRenderState2 = dyn2 as VfxRenderStateInfoPixelShader;

                    var depth1 = psRenderState1!.DepthStencilStateDesc!;
                    var depth2 = psRenderState2!.DepthStencilStateDesc!;


                    Assert.That(depth2.DepthWriteEnable, Is.EqualTo(depth1.DepthWriteEnable));
                    Assert.That(depth2.DepthFunc, Is.EqualTo(depth1.DepthFunc));
                    Assert.That(depth2.DepthTestEnable, Is.EqualTo(depth1.DepthTestEnable));
                    Assert.That(depth2.StencilEnable, Is.EqualTo(depth1.StencilEnable));
                    Assert.That(depth2.StencilReadMask, Is.EqualTo(depth1.StencilReadMask));
                    Assert.That(depth2.StencilWriteMask, Is.EqualTo(depth1.StencilWriteMask));
                    Assert.That(depth2.FrontStencilFunc, Is.EqualTo(depth1.FrontStencilFunc));
                    Assert.That(depth2.FrontStencilPassOp, Is.EqualTo(depth1.FrontStencilPassOp));
                    Assert.That(depth2.FrontStencilFailOp, Is.EqualTo(depth1.FrontStencilFailOp));
                    Assert.That(depth2.FrontStencilDepthFailOp, Is.EqualTo(depth1.FrontStencilDepthFailOp));
                    Assert.That(depth2.BackStencilFunc, Is.EqualTo(depth1.BackStencilFunc));
                    Assert.That(depth2.BackStencilPassOp, Is.EqualTo(depth1.BackStencilPassOp));
                    Assert.That(depth2.BackStencilFailOp, Is.EqualTo(depth1.BackStencilFailOp));
                    Assert.That(depth2.BackStencilDepthFailOp, Is.EqualTo(depth1.BackStencilDepthFailOp));

                    // These are no longer stored in KV3
                    Assert.That(depth2.HiZEnable360, Is.EqualTo(depth1.HiZEnable360));
                    Assert.That(depth2.HiZWriteEnable360, Is.EqualTo(depth1.HiZWriteEnable360));
                    Assert.That(depth2.HiStencilEnable360, Is.EqualTo(depth1.HiStencilEnable360));
                    Assert.That(depth2.HiStencilWriteEnable360, Is.EqualTo(depth1.HiStencilWriteEnable360));
                    Assert.That(depth2.HiStencilFunc360, Is.EqualTo(depth1.HiStencilFunc360));
                    Assert.That(depth2.HiStencilRef360, Is.EqualTo(depth1.HiStencilRef360));

                    var raster1 = psRenderState1.RasterizerStateDesc!;
                    var raster2 = psRenderState2.RasterizerStateDesc!;
                    Assert.That(raster2.FillMode, Is.EqualTo(raster1.FillMode));
                    Assert.That(raster2.CullMode, Is.EqualTo(raster1.CullMode));
                    Assert.That(raster2.DepthClipEnable, Is.EqualTo(raster1.DepthClipEnable));
                    Assert.That(raster2.MultisampleEnable, Is.EqualTo(raster1.MultisampleEnable));
                    Assert.That(raster2.DepthBias, Is.EqualTo(raster1.DepthBias));
                    Assert.That(raster2.DepthBiasClamp, Is.EqualTo(raster1.DepthBiasClamp));
                    Assert.That(raster2.SlopeScaledDepthBias, Is.EqualTo(raster1.SlopeScaledDepthBias));

                    var blend1 = psRenderState1.BlendStateDesc!;
                    var blend2 = psRenderState2.BlendStateDesc!;

                    Assert.That(blend2.AlphaToCoverageEnable, Is.EqualTo(blend1.AlphaToCoverageEnable));
                    Assert.That(blend2.IndependentBlendEnable, Is.EqualTo(blend1.IndependentBlendEnable));

                    for (var t = 0; t < VfxRenderStateInfoPixelShader.RsBlendStateDesc.MaxRenderTargets; t++)
                    {
                        Assert.That(blend2.BlendEnable[t], Is.EqualTo(blend1.BlendEnable[t]));
                        Assert.That(blend2.SrcBlend[t], Is.EqualTo(blend1.SrcBlend[t]));
                        Assert.That(blend2.DestBlend[t], Is.EqualTo(blend1.DestBlend[t]));
                        Assert.That(blend2.BlendOp[t], Is.EqualTo(blend1.BlendOp[t]));
                        Assert.That(blend2.SrcBlendAlpha[t], Is.EqualTo(blend1.SrcBlendAlpha[t]));
                        Assert.That(blend2.DestBlendAlpha[t], Is.EqualTo(blend1.DestBlendAlpha[t]));
                        Assert.That(blend2.BlendOpAlpha[t], Is.EqualTo(blend1.BlendOpAlpha[t]));
                        Assert.That(blend2.RenderTargetWriteMask[t], Is.EqualTo(blend1.RenderTargetWriteMask[t]));
                        Assert.That(blend2.SrgbWriteEnable[t], Is.EqualTo(blend1.SrgbWriteEnable[t]));
                    }
                }

                Assert.That(combo2.Attributes, Is.EqualTo(combo1.Attributes));
                Assert.That(combo2.ShaderFiles, Has.Length.EqualTo(combo1.ShaderFiles.Length));
            }
        }

        [Test]
        public void TestVcsFileName()
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

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.ShaderName, Is.EqualTo(testCase.ShaderName));
                    Assert.That(result.PlatformType, Is.EqualTo(testCase.Platform));
                    Assert.That(result.ShaderModelType, Is.EqualTo(testCase.ShaderModel));
                    Assert.That(result.ProgramType, Is.EqualTo(testCase.ProgramType));
                    Assert.That(opposite, Is.EqualTo(Path.GetFileName(testCase.FileName)));
                }
            }
        }

        [Test]
        public void CompiledShaderInResourceThrows()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var resource = new Resource();

            var ex = Assert.Throws<InvalidDataException>(() => resource.Read(path));
            Assert.That(ex, Is.Not.Null);
        }

        [Test]
        public void TestZFrameWriteSequences()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var zFrameFile = shader.GetStaticCombo(0);
            using var sw = new IndentedTextWriter();
            var zframeSummary = new PrintZFrameSummary(zFrameFile, sw);

            var wsCount = zframeSummary.GetUniqueWriteSequences().Count;
            Assert.That(wsCount, Is.EqualTo(1));

            var zBlockToWS = zframeSummary.GetBlockToUniqueSequenceMap();
            var expected = new Dictionary<int, int>
            {
                {-1, 0},
                {0, 0},
            };

            Assert.That(zBlockToWS, Is.EqualTo(expected));
        }

        [Test]
        public void TestChannelMapping()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ChannelMapping.R.PackedValue, Is.EqualTo(0xFFFFFF00));
                Assert.That(ChannelMapping.G.PackedValue, Is.EqualTo(0xFFFFFF01));
                Assert.That(ChannelMapping.B.PackedValue, Is.EqualTo(0xFFFFFF02));
                Assert.That(ChannelMapping.A.PackedValue, Is.EqualTo(0xFFFFFF03));
                Assert.That(ChannelMapping.RGB.PackedValue, Is.EqualTo(0xFF020100));
                Assert.That(ChannelMapping.RGBA.PackedValue, Is.EqualTo(0x03020100));

                Assert.That(ChannelMapping.RGBA.Channels[0], Is.EqualTo(ChannelMapping.Channel.R));
                Assert.That(ChannelMapping.RGBA.Channels[1], Is.EqualTo(ChannelMapping.Channel.G));
                Assert.That(ChannelMapping.AG.Channels[1], Is.EqualTo(ChannelMapping.Channel.G));

                Assert.That(ChannelMapping.RGBA.Count, Is.EqualTo(4));
                Assert.That(ChannelMapping.RGB.Count, Is.EqualTo(3));
                Assert.That(ChannelMapping.RG.Count, Is.EqualTo(2));
                Assert.That(ChannelMapping.G.Count, Is.EqualTo(1));
                Assert.That(ChannelMapping.NULL.Count, Is.Zero);
                Assert.That(ChannelMapping.RGBA.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.R, ChannelMapping.Channel.G, ChannelMapping.Channel.B, ChannelMapping.Channel.A }));
                Assert.That(ChannelMapping.RGB.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.R, ChannelMapping.Channel.G, ChannelMapping.Channel.B }));
                Assert.That(ChannelMapping.RG.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.R, ChannelMapping.Channel.G }));
                Assert.That(ChannelMapping.AG.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.A, ChannelMapping.Channel.G }));
                Assert.That(ChannelMapping.A.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.A }));
                Assert.That(ChannelMapping.R.ValidChannels, Is.EqualTo(new[] { ChannelMapping.Channel.R }));

                Assert.That((byte)ChannelMapping.R, Is.Zero);
                Assert.That((byte)ChannelMapping.G, Is.EqualTo(0x01));
                Assert.That((byte)ChannelMapping.B, Is.EqualTo(0x02));
                Assert.That((byte)ChannelMapping.A, Is.EqualTo(0x03));

                Assert.That(ChannelMapping.R, Is.EqualTo(ChannelMapping.FromUInt32(0xFFFFFF00)));
                Assert.That(ChannelMapping.G, Is.EqualTo(ChannelMapping.FromUInt32(0xFFFFFF01)));
                Assert.That(ChannelMapping.AG, Is.EqualTo(ChannelMapping.FromUInt32(0xFFFF0103)));

                // New in version 67
                Assert.That(ChannelMapping.RGBA, Is.EqualTo(ChannelMapping.FromUInt32(0x33221100)));
                Assert.That(ChannelMapping.AG, Is.EqualTo(ChannelMapping.FromUInt32(0xFFFF1130)));

                Assert.That(ChannelMapping.R.ToString(), Is.EqualTo("R"));
                Assert.That(ChannelMapping.G.ToString(), Is.EqualTo("G"));
                Assert.That(ChannelMapping.B.ToString(), Is.EqualTo("B"));
                Assert.That(ChannelMapping.A.ToString(), Is.EqualTo("A"));
                Assert.That(ChannelMapping.AG.ToString(), Is.EqualTo("AG"));
                Assert.That(ChannelMapping.RGB.ToString(), Is.EqualTo("RGB"));
                Assert.That(ChannelMapping.NULL.ToString(), Is.EqualTo("0xFFFFFFFF"));

                Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMapping.FromChannels(0x04));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChannelMapping.FromChannels(0x05));

                Assert.That(ChannelMapping.FromChannels(0xFF), Is.EqualTo(ChannelMapping.NULL));
            }
        }

        [Test]
        public void VfxShaderExtract_Invalid()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pcgl_40_ps.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var ex = Assert.Throws<InvalidOperationException>(() => new ShaderExtract(ShaderCollection.FromEnumerable([shader])));

            Debug.Assert(ex != null);
            Assert.That(ex, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("cannot continue without at least a features file"));
        }

        [Test]
        public void VfxShaderExtract_Minimal()
        {
            var path = Path.Combine(ShadersDir, "vcs64_error_pc_40_features.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var extract = new ShaderExtract(ShaderCollection.FromEnumerable([shader]));

            var vfx = extract.ToVFX(ShaderExtract.ShaderExtractParams.Inspect);
            vfx = extract.ToVFX(ShaderExtract.ShaderExtractParams.Export);

            Assert.That(vfx.VfxContent, Does.Contain("Description = \"Error shader\""));
            Assert.That(vfx.VfxContent, Does.Contain("DevShader = true"));
        }

        [Test]
        public void VfxShaderExtract_OptionsTest()
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
                Assert.That(vfx.VfxContent, Does.Contain("Description = \"Error shader\""));
                Assert.That(vfx.VfxContent, Does.Contain("DevShader = true"));
            }
        }

        private static IEnumerable<TestCaseData> SpirvReflectionTestCases()
        {
            yield return new TestCaseData("vcs65_compute_depthbin_cullbits_vulkan_50_cs.vcs", 0, 0);
            yield return new TestCaseData("vcs68_tower_force_field_vulkan_40_vs.vcs", 0, 9);
            yield return new TestCaseData("vcs68_tower_force_field_vulkan_40_ps.vcs", 1, 1);
            yield return new TestCaseData("vcs68_csgo_simple_2way_blend_vulkan_60_rtx.vcs", 0x6, 0);
            yield return new TestCaseData("vcs68_test_vulkan_60_ms.vcs", 0, 1);
            yield return new TestCaseData("vcs69_downsample_depth_cs_vulkan_50_cs.vcs", 0, 0x20);
            yield return new TestCaseData("vcs69_zstd5_npr_dummy_vulkan_50_vs.vcs", 0, 0);
            yield return new TestCaseData("vcs69_bloom_vulkan_40_ps.vcs", 0, 0);
            yield return new TestCaseData("vcs70_resource_bloom_vulkan_40_ps.vcs", 0, 0);
        }

        [Test, TestCaseSource(nameof(SpirvReflectionTestCases))]
        public void TestSpirvReflection(string shaderFile, int staticCombo, int dynamicCombo)
        {
            if (!IsSpirvCrossAvailable())
            {
                Assert.Ignore("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, shaderFile);
            using var shader = new VfxProgramData();
            shader.Read(path);

            var staticComboEntry = shader.GetStaticCombo(staticCombo);
            var dynamicComboEntry = staticComboEntry.DynamicCombos[dynamicCombo];
            var code = staticComboEntry.ShaderFiles[dynamicComboEntry.ShaderFileId].GetDecompiledFile();
            code = code.Replace(StringToken.VRF_GENERATOR, "VRF-TEST", StringComparison.Ordinal);

            var referencePath = Path.Combine(ShadersDir, "SpirvOutput", $"{shaderFile}.glsl");

            /*{
                var shadersDirRepo = Path.Combine(TestContext.CurrentContext.TestDirectory, "../../", "Files", "Shaders");
                var referencePathRepo = Path.Combine(shadersDirRepo, "SpirvOutput", $"{shaderFile}.glsl");
                File.WriteAllText(referencePathRepo, code);
                return;
            }*/

            var reference = File.ReadAllText(referencePath);

            Assert.That(code, Is.EqualTo(reference).IgnoreWhiteSpace, $"Spirv reflection output does not match reference.");
        }

        private static IEnumerable<TestCaseData> SpirvOpenGlTestCases()
        {
            yield return new TestCaseData("vcs68_tower_force_field_vulkan_40_vs.vcs", 0, 9);
            yield return new TestCaseData("vcs68_tower_force_field_vulkan_40_ps.vcs", 1, 1);
            yield return new TestCaseData("vcs66_vr_stencil_vulkan_50_vs.vcs", 0, 0);
            yield return new TestCaseData("vcs69_zstd5_npr_dummy_vulkan_50_vs.vcs", 0, 0);
            yield return new TestCaseData("vcs69_bloom_vulkan_40_ps.vcs", 0, 0);
        }

        [Test, TestCaseSource(nameof(SpirvOpenGlTestCases))]
        public void TestSpirvOpenGlEmission(string shaderFile, int staticCombo, int dynamicCombo)
        {
            if (!IsSpirvCrossAvailable())
            {
                Assert.Ignore("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, shaderFile);
            using var shader = new VfxProgramData();
            shader.Read(path);

            var staticComboEntry = shader.GetStaticCombo(staticCombo);
            var dynamicComboEntry = staticComboEntry.DynamicCombos[dynamicCombo];
            var vulkanSource = (VfxShaderFileVulkan)staticComboEntry.ShaderFiles[dynamicComboEntry.ShaderFileId];

            var success = ShaderSpirvReflection.ReflectSpirvOpenGl(vulkanSource, out var code, out var info);

            Assert.That(success, Is.True, code);
            Assert.That(code, Does.StartWith("#version 460"));

            // Nothing Vulkan may survive: no descriptor set/binding qualifiers, no separate images or samplers.
            Assert.That(code, Does.Not.Contain("layout(set"));
            Assert.That(code, Does.Not.Contain("layout(binding"));
            Assert.That(code, Does.Not.Contain("uniform texture"));

            // The saturate() rewrite is skipped, it is not a GLSL builtin.
            Assert.That(code, Does.Not.Contain("saturate("));

            var isVertexShader = shader.VcsProgramType == VcsProgramType.VertexShader;
            var stageSuffix = isVertexShader ? "_vs" : "_ps";

            if (isVertexShader)
            {
                // The FlipVertexY counter-negation of the baked Vulkan Y-flip.
                Assert.That(code, Does.Contain("gl_Position.y = -gl_Position.y"));
            }

            foreach (var block in info.UniformBlocks)
            {
                Assert.That(block.EmittedName, Does.EndWith(stageSuffix));
                Assert.That(code, Does.Contain(block.EmittedName));
            }

            foreach (var block in info.StorageBlocks)
            {
                Assert.That(block.EmittedName, Does.EndWith(stageSuffix));
                Assert.That(code, Does.Contain(block.EmittedName));
            }

            foreach (var combinedSampler in info.CombinedSamplers)
            {
                Assert.That(code, Does.Contain($" {combinedSampler.UniformName};"));
            }
        }

        [Test]
        public void TestSpirvOpenGlUnsupportedProgramType()
        {
            if (!IsSpirvCrossAvailable())
            {
                Assert.Ignore("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, "vcs68_csgo_simple_2way_blend_vulkan_60_rtx.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var staticComboEntry = shader.GetStaticCombo(0x6);
            var dynamicComboEntry = staticComboEntry.DynamicCombos[0];
            var vulkanSource = (VfxShaderFileVulkan)staticComboEntry.ShaderFiles[dynamicComboEntry.ShaderFileId];

            var success = ShaderSpirvReflection.ReflectSpirvOpenGl(vulkanSource, out var code, out _);

            Assert.That(success, Is.False);
            Assert.That(code, Does.Contain("not supported"));
        }

        [Test]
        public void TestDepthStencilStateBitLayouts()
        {
            // Value observed in CS2 (VCS 71) pixel shaders: depth test+write with LessEqual,
            // stencil disabled with Always funcs and full masks. The bit layout changed in version 71.
            var v71 = new VfxRenderStateInfoPixelShader.RsDepthStencilStateDesc(0xFFFF00000077000FUL, 71);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(v71.DepthTestEnable, Is.True);
                Assert.That(v71.DepthWriteEnable, Is.True);
                Assert.That(v71.DepthFunc, Is.EqualTo(RsComparison.LessEqual));
                Assert.That(v71.StencilEnable, Is.False);
                Assert.That(v71.FrontStencilFunc, Is.EqualTo(RsComparison.Always));
                Assert.That(v71.BackStencilFunc, Is.EqualTo(RsComparison.Always));
                Assert.That(v71.StencilReadMask, Is.EqualTo(0xFF));
                Assert.That(v71.StencilWriteMask, Is.EqualTo(0xFF));
            }

            // Value from the vcs70 resource bloom fixture: depth disabled, LessEqual, Always funcs.
            var v70 = new VfxRenderStateInfoPixelShader.RsDepthStencilStateDesc(0xFFFF01C01C000300UL, 70);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(v70.DepthTestEnable, Is.False);
                Assert.That(v70.DepthWriteEnable, Is.False);
                Assert.That(v70.DepthFunc, Is.EqualTo(RsComparison.LessEqual));
                Assert.That(v70.StencilEnable, Is.False);
                Assert.That(v70.FrontStencilFunc, Is.EqualTo(RsComparison.Always));
                Assert.That(v70.BackStencilFunc, Is.EqualTo(RsComparison.Always));
                Assert.That(v70.StencilReadMask, Is.EqualTo(0xFF));
                Assert.That(v70.StencilWriteMask, Is.EqualTo(0xFF));
            }
        }

        private static IEnumerable<TestCaseData> Clamp01TestCases()
        {
            // The clamped expression routinely contains calls and their commas.
            yield return new TestCaseData("clamp(x, 0.0, 1.0)", "saturate(x)");
            yield return new TestCaseData("clamp(-x, 0.0, 1.0)", "saturate(-x)");
            yield return new TestCaseData("clamp(dot(a, b), 0.0, 1.0)", "saturate(dot(a, b))");
            yield return new TestCaseData("clamp(1.0 - dot(a.xy, a.xy), 0.0, 1.0)", "saturate(1.0 - dot(a.xy, a.xy))");
            yield return new TestCaseData("clamp((f(a) - b) / (g(c, d) - b), 0.0, 1.0)", "saturate((f(a) - b) / (g(c, d) - b))");

            // Both backends' spelling of the bounds, including the hlsl float suffix.
            yield return new TestCaseData("clamp(x, 0.0f, 1.0f)", "saturate(x)");
            yield return new TestCaseData("clamp(x, 0, 1)", "saturate(x)");
            yield return new TestCaseData("clamp(x, vec3(0.0), vec3(1.0))", "saturate(x)");
            yield return new TestCaseData("clamp(x, float4(0.0f, 0.0f, 0.0f, 0.0f), float4(1.0f, 1.0f, 1.0f, 1.0f))", "saturate(x)");

            // Nested clamps need more than one pass, Replace resumes after the outer match.
            yield return new TestCaseData("clamp(min(m, pow(clamp(x, 0.0, 1.0), e)), 0.0, 1.0)", "saturate(min(m, pow(saturate(x), e)))");

            // Anything that is not a 0..1 clamp has to survive untouched.
            yield return new TestCaseData("clamp(x, 0.0, 0.5)", "clamp(x, 0.0, 0.5)");
            yield return new TestCaseData("clamp(x, 0.5, 1.0)", "clamp(x, 0.5, 1.0)");
            yield return new TestCaseData("clamp(x, 10.0, 1.0)", "clamp(x, 10.0, 1.0)");
            yield return new TestCaseData("clamp(x, 0.05, 1.0)", "clamp(x, 0.05, 1.0)");
            yield return new TestCaseData("clamp(x, y, 1.0)", "clamp(x, y, 1.0)");
            yield return new TestCaseData("unclamped(x, 0.0, 1.0)", "unclamped(x, 0.0, 1.0)");
        }

        [Test, TestCaseSource(nameof(Clamp01TestCases))]
        public void TestClamp01Rewrite(string input, string expected)
        {
            Assert.That(ShaderSpirvReflection.ReplaceCommonPatterns(input), Is.EqualTo(expected));
        }

        [Test]
        public void TestComboResolver()
        {
            var path = Path.Combine(ShadersDir, "vcs69_downsample_depth_cs_vulkan_50_cs.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            // D_OUTPUT_MIP0, D_REVERSE_DEPTH, D_RESAMPLE, D_MIP_COUNT (1..4), D_MSAA_DEPTH (0..3).
            // The ids are mixed radix, so D_REVERSE_DEPTH is worth 2 and D_MIP_COUNT is worth 8 per step above its minimum.
            const long ReverseDepthAndThreeMips = 2 + ((3 - 1) * 8);

            Assert.Multiple(() =>
            {
                Assert.That(VfxComboResolver.TryResolve(shader, "D_REVERSE_DEPTH=1,D_MIP_COUNT=3", out var staticId, out var dynamicId, out var error), Is.True);
                Assert.That(error, Is.Empty);
                Assert.That(staticId, Is.Zero);
                Assert.That(dynamicId, Is.EqualTo(ReverseDepthAndThreeMips));

                // The values must survive the round trip back out of the id.
                int[] expectedValues = [0, 1, 0, 3, 0];
                Assert.That(shader.GetDBlockConfig(dynamicId), Is.EqualTo(expectedValues));
                Assert.That(VfxComboResolver.FormatDynamicCombos(shader, dynamicId), Is.EqualTo("D_REVERSE_DEPTH, D_MIP_COUNT=3"));

                // A bare name means =1.
                Assert.That(VfxComboResolver.TryResolve(shader, "D_REVERSE_DEPTH,D_MIP_COUNT=3", out _, out var bareId, out _), Is.True);
                Assert.That(bareId, Is.EqualTo(ReverseDepthAndThreeMips));

                // An empty spec is every combo at its minimum, which is not zero for D_MIP_COUNT.
                Assert.That(VfxComboResolver.TryResolve(shader, null, out var defaultStatic, out var defaultDynamic, out _), Is.True);
                Assert.That(defaultStatic, Is.Zero);
                Assert.That(defaultDynamic, Is.Zero);
                Assert.That(shader.GetDBlockConfig(defaultDynamic)[3], Is.EqualTo(1));
            });

            Assert.Multiple(() =>
            {
                Assert.That(VfxComboResolver.TryResolve(shader, "D_NOT_A_COMBO=1", out _, out _, out var unknownError), Is.False);
                Assert.That(unknownError, Does.StartWith("Unknown combo 'D_NOT_A_COMBO'."));

                // Zero is below D_MIP_COUNT's minimum of one.
                Assert.That(VfxComboResolver.TryResolve(shader, "D_MIP_COUNT=0", out _, out _, out var rangeError), Is.False);
                Assert.That(rangeError, Does.StartWith("Value 0 is out of range for combo 'D_MIP_COUNT' (1..4)"));
            });
        }

        [Test]
        public void TestSpirvReflectionNormalization()
        {
            if (!IsSpirvCrossAvailable())
            {
                Assert.Ignore("There are no native binaries for SPIR-V on arm linux yet.");
                return;
            }

            var path = Path.Combine(ShadersDir, "vcs69_zstd5_npr_dummy_vulkan_50_vs.vcs");
            using var shader = new VfxProgramData();
            shader.Read(path);

            var shaderFile = (VfxShaderFileVulkan)shader.GetStaticCombo(0).ShaderFiles[0];
            var raw = shaderFile.GetDecompiledFile(Backend.HLSL, SpirvReflectionOptions.Default);
            var clean = shaderFile.GetDecompiledFile(Backend.HLSL, SpirvReflectionOptions.Clean);

            Assert.Multiple(() =>
            {
                // SPIR-V ids differ between combos even when the code is the same, so none may survive.
                Assert.That(raw, Does.Match(@"\b_\d+\b"), "Expected the raw output to contain SPIR-V id derived names.");
                Assert.That(clean, Does.Not.Match(@"\b_\d+(?:ident)?\b"));

                Assert.That(raw, Does.Contain("PerViewConstantBuffer_t_1_g_matWorldToProjection"));
                Assert.That(clean, Does.Contain("g_matWorldToProjection"));
                Assert.That(clean, Does.Not.Contain("PerViewConstantBuffer_t_1_g_matWorldToProjection"));

                // Rewriting must not drop or duplicate any statement.
                Assert.That(clean.Count(c => c == ';'), Is.EqualTo(raw.Count(c => c == ';')));
            });
        }

        [Test]
        public void TestUiGroup()
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
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(parsed.Heading, Is.EqualTo(expected.Heading));
                    Assert.That(parsed.HeadingOrder, Is.EqualTo(expected.HeadingOrder));
                    Assert.That(parsed.Group, Is.EqualTo(expected.Group));
                    Assert.That(parsed.GroupOrder, Is.EqualTo(expected.GroupOrder));
                    Assert.That(parsed.VariableOrder, Is.EqualTo(expected.VariableOrder));
                }
            }
        }

        [Test]
        public void TestDxbcReflectionShaderModel4()
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

            Assert.That(reflection, Is.Not.Null, "the fixture was expected to retain a populated RDEF chunk");

            string[] expectedBindings =
                ["g_tTransformTexture_sampler", "g_tTransformTexture", "PerViewConstantBuffer_t"];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reflection!.ShaderModelMajor, Is.EqualTo(4));
                Assert.That(reflection.Creator, Does.Contain("Shader Compiler"));
                Assert.That(reflection.ResourceBindings.Select(b => b.Name), Is.EquivalentTo(expectedBindings));
                Assert.That(reflection.ConstantBuffers, Has.Count.EqualTo(1));
            }

            var buffer = reflection!.ConstantBuffers[0];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(buffer.Name, Is.EqualTo("PerViewConstantBuffer_t"));
                Assert.That(buffer.Members, Has.Count.EqualTo(39));
                Assert.That(buffer.Members[0].Name, Is.EqualTo("g_matWorldToProjection"));
                Assert.That(buffer.Members[0].PackOffset, Is.EqualTo("c0"));
                Assert.That(buffer.Members[0].Size, Is.EqualTo(64));
                Assert.That(buffer.Members[0].IsUsed, Is.True);
            }

            // A wrong descriptor stride reads names from the middle of other records, so requiring every one to
            // be an identifier that sits inside the buffer is what actually pins the layout down.
            foreach (var member in buffer.Members)
            {
                Assert.That(member.Name, Does.Match("^[A-Za-z_$][A-Za-z0-9_$]*$"));
                Assert.That(member.Size, Is.GreaterThan(0));
                Assert.That(member.Offset + member.Size, Is.LessThanOrEqualTo(buffer.Size));
            }
        }

        [Test]
        public void TestDxbcReflectionShaderModel5Stride()
        {
            // Every CS2 shader is shader model 5, where an "RD11" header declares a 40 byte variable descriptor
            // instead of shader model 4's 24. No checked in fixture has a populated SM5 chunk -- the SM5 ones
            // keep the chunk but with emptied tables -- so this builds a minimal container to cover that path.
            var bytecode = BuildShaderModel5Rdef();

            Assert.That(DxbcReflection.TryParse(bytecode, out var parsed), Is.True);
            Assert.That(parsed, Is.Not.Null);

            var reflection = parsed!;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reflection.ShaderModelMajor, Is.EqualTo(5));
                Assert.That(reflection.ShaderModelMinor, Is.EqualTo(0));
                Assert.That(reflection.Creator, Is.EqualTo("test"));

                Assert.That(reflection.ResourceBindings, Has.Count.EqualTo(1));
                Assert.That(reflection.ResourceBindings[0].Name, Is.EqualTo("g_tTexture"));
                Assert.That(reflection.ResourceBindings[0].Type, Is.EqualTo(DxbcResourceType.Texture));
                Assert.That(reflection.ResourceBindings[0].Register, Is.EqualTo("t7"));

                Assert.That(reflection.ConstantBuffers, Has.Count.EqualTo(1));
            }

            var buffer = reflection.ConstantBuffers[0];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(buffer.Name, Is.EqualTo("MyControls_t"));
                Assert.That(buffer.Members, Has.Count.EqualTo(2));

                // Reading these at a 24 byte stride would land the second name inside the first record.
                Assert.That(buffer.Members[0].Name, Is.EqualTo("g_vFirst"));
                Assert.That(buffer.Members[0].PackOffset, Is.EqualTo("c0"));
                Assert.That(buffer.Members[0].IsUsed, Is.True);

                Assert.That(buffer.Members[1].Name, Is.EqualTo("g_flSecond"));
                Assert.That(buffer.Members[1].PackOffset, Is.EqualTo("c1.z"));
                Assert.That(buffer.Members[1].IsUsed, Is.False);
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
        public void TestDxbcReflectionAbsentFromNonDirectXShaders()
        {
            // A Vulkan build stores SPIR-V rather than DXBC, so there is nothing to find. The API has to say so
            // rather than throw, because a stripped or non-DirectX blob is the common case, not an error.
            using var shader = new VfxProgramData();
            shader.Read(Path.Combine(ShadersDir, "vcs64_error_vulkan_40_vs.vcs"));

            foreach (var variant in VfxComboResolver.EnumerateVariants(shader))
            {
                Assert.That(variant.ShaderFile, Is.Not.InstanceOf<VfxShaderFileDXBC>());
                Assert.That(DxbcReflection.TryParse(variant.ShaderFile.Bytecode, out var reflection), Is.False);
                Assert.That(reflection, Is.Null);
            }
        }

        [Test]
        public void TestDxbcPackOffsetFormatting()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(new DxbcConstantBufferMember("a", 0, 4, true).PackOffset, Is.EqualTo("c0"));
                Assert.That(new DxbcConstantBufferMember("a", 4, 4, true).PackOffset, Is.EqualTo("c0.y"));
                Assert.That(new DxbcConstantBufferMember("a", 8, 4, true).PackOffset, Is.EqualTo("c0.z"));
                Assert.That(new DxbcConstantBufferMember("a", 12, 4, true).PackOffset, Is.EqualTo("c0.w"));
                Assert.That(new DxbcConstantBufferMember("a", 184, 8, true).PackOffset, Is.EqualTo("c11.z"));
                Assert.That(new DxbcConstantBufferMember("a", 256, 16, true).PackOffset, Is.EqualTo("c16"));
            }
        }
    }
}
