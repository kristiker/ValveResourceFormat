using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveKeyValue;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Combines morph target deformations into a GPU buffer of per-vertex offsets for facial animation rendering.
    /// </summary>
    public class MorphComposite
    {
        private const int ComputeGroupSize = 64;

        /// <summary>Gets or sets the start of this composite's range in the scene morph offsets buffer,
        /// assigned when the scene instance buffers are (re)built. <see langword="null"/> until then.</summary>
        public uint? BaseOffset { get; set; }

        /// <summary>Gets the number of slots this composite occupies in the scene morph offsets buffer.</summary>
        public int SlotCount => slotCount;

        /// <summary>Gets whether this composite has anything to gather, and so a range worth reserving.</summary>
        public bool HasMorphData => slotCount > 0 && rectCount > 0;

        /// <summary>A single morph rect. The first two vectors mirror <c>MorphRect</c> in morph_composite.comp.slang.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MorphRect
        {
            public uint DestX;
            public uint DestY;
            public uint SrcX;
            public uint SrcY;

            public uint MorphId;
            /// <summary>Rect width in texels. Only used to enumerate covered slots; the shader derives its own position within the rect.</summary>
            public uint Width;
            /// <summary>Rect height in texels. Only used to enumerate covered slots.</summary>
            public uint Height;
            public uint Padding;

            public Vector4 Offsets;
            public Vector4 Ranges;
        }

        private readonly Shader shader;
        private readonly RenderTexture morphAtlas;
        private readonly StorageBuffer rectBuffer;
        private readonly StorageBuffer rectStartBuffer;
        private readonly StorageBuffer rectEntryBuffer;
        private readonly StorageBuffer weightBuffer;
        private readonly float[] morphWeights;
        private readonly int compositeWidth;
        private readonly int compositeHeight;
        private readonly int slotCount;
        private readonly int rectCount;
        private bool weightsDirty = true;

        /// <summary>Initializes the morph composite for the given morph data, uploading the atlas and building the rect tables.</summary>
        /// <param name="renderContext">Renderer context for loading shaders and textures.</param>
        /// <param name="morph">Morph data describing the morph targets and atlas layout.</param>
        public MorphComposite(RendererContext renderContext, Morph morph)
        {
            ArgumentNullException.ThrowIfNull(morph.TextureResource);
            morphAtlas = renderContext.MaterialLoader.LoadTexture(morph.TextureResource);
            shader = renderContext.ShaderLoader.LoadShader("vrf.morph_composite");

            compositeWidth = morph.Data.GetInt32Property("m_nWidth");
            compositeHeight = morph.Data.GetInt32Property("m_nHeight");
            slotCount = compositeWidth * compositeHeight;

            var morphDatas = morph.GetMorphDatas();
            morphWeights = new float[Math.Max(morph.GetMorphCount(), morphDatas.Count)];

            var rects = BuildRects(morphDatas);
            rectCount = rects.Length;

            rectBuffer = new StorageBuffer(ReservedBufferSlots.MorphRects);
            rectStartBuffer = new StorageBuffer(ReservedBufferSlots.MorphRectStarts);
            rectEntryBuffer = new StorageBuffer(ReservedBufferSlots.MorphRectEntries);
            weightBuffer = new StorageBuffer(ReservedBufferSlots.MorphWeights);

            if (!HasMorphData)
            {
                return;
            }

            BuildRectTable(rects, out var rectStarts, out var rectEntries);

            rectBuffer.Create<MorphRect>(rects, BufferUsageHint.StaticDraw);
            rectStartBuffer.Create<uint>(rectStarts, BufferUsageHint.StaticDraw);
            rectEntryBuffer.Create<uint>(rectEntries, BufferUsageHint.StaticDraw);
            weightBuffer.Create<float>(morphWeights, BufferUsageHint.DynamicDraw);
        }

        /// <summary>Flattens the rects of every morph target into a single array of gather sources.</summary>
        private MorphRect[] BuildRects(IReadOnlyList<KVObject> morphDatas)
        {
            var rects = new List<MorphRect>();

            for (var morphId = 0; morphId < morphDatas.Count; morphId++)
            {
                var morphData = morphDatas[morphId];

                if (morphData.ValueType != KVValueType.Collection)
                {
                    continue;
                }

                foreach (var rect in morphData.GetArray("m_morphRectDatas") ?? [])
                {
                    //TODO: Implement normal/wrinkle bundle type (second bundle data usually, if exists)
                    var bundleData = (rect.GetArray("m_bundleDatas") ?? [])[0];

                    rects.Add(new MorphRect
                    {
                        DestX = (uint)rect.GetInt32Property("m_nXLeftDst"),
                        DestY = (uint)rect.GetInt32Property("m_nYTopDst"),
                        SrcX = (uint)MathF.Round(bundleData.GetFloatProperty("m_flULeftSrc") * morphAtlas.Width),
                        SrcY = (uint)MathF.Round(bundleData.GetFloatProperty("m_flVTopSrc") * morphAtlas.Height),

                        MorphId = (uint)morphId,
                        Width = (uint)MathF.Round(rect.GetFloatProperty("m_flUWidthSrc") * morphAtlas.Width),
                        Height = (uint)MathF.Round(rect.GetFloatProperty("m_flVHeightSrc") * morphAtlas.Height),

                        Offsets = new Vector4(bundleData.GetFloatArray("m_offsets")),
                        Ranges = new Vector4(bundleData.GetFloatArray("m_ranges")),
                    });
                }
            }

            return [.. rects];
        }

        /// <summary>
        /// Builds the per-slot rect lists in compressed sparse row form: <paramref name="rectStarts"/> holds where
        /// each slot's list begins in <paramref name="rectEntries"/> (plus a terminator), and <paramref name="rectEntries"/>
        /// holds the indices of the rects contributing to that slot.
        /// </summary>
        private void BuildRectTable(MorphRect[] rects, out uint[] rectStarts, out uint[] rectEntries)
        {
            var counts = new int[slotCount];

            foreach (var rect in rects)
            {
                foreach (var slot in CoveredSlots(rect))
                {
                    counts[slot]++;
                }
            }

            rectStarts = new uint[slotCount + 1];
            var total = 0u;

            for (var slot = 0; slot < slotCount; slot++)
            {
                rectStarts[slot] = total;
                total += (uint)counts[slot];
            }

            rectStarts[slotCount] = total;
            rectEntries = new uint[total];

            var cursors = new uint[slotCount];
            Array.Copy(rectStarts, cursors, slotCount);

            for (var i = 0; i < rects.Length; i++)
            {
                foreach (var slot in CoveredSlots(rects[i]))
                {
                    rectEntries[cursors[slot]++] = (uint)i;
                }
            }
        }

        /// <summary>Enumerates the composite slots covered by the given rect.</summary>
        private IEnumerable<int> CoveredSlots(MorphRect rect)
        {
            for (var y = 0; y < rect.Height; y++)
            {
                var destY = (int)rect.DestY + y;

                // Content can author rects running past the composite; do not let them wrap into other rows
                if (destY >= compositeHeight)
                {
                    break;
                }

                for (var x = 0; x < rect.Width; x++)
                {
                    var destX = (int)rect.DestX + x;

                    if (destX >= compositeWidth)
                    {
                        break;
                    }

                    yield return destY * compositeWidth + destX;
                }
            }
        }

        /// <summary>Dispatches the compute pass that gathers all active morph targets into this composite's
        /// range of the scene morph offsets buffer.</summary>
        /// <param name="offsetBuffer">The scene morph offsets buffer, or <see langword="null"/> if it has not been built yet.</param>
        public void Dispatch(StorageBuffer? offsetBuffer)
        {
            if (!HasMorphData || offsetBuffer == null || BaseOffset is not uint baseOffset)
            {
                return;
            }

            if (weightsDirty)
            {
                weightBuffer.Update(morphWeights, 0, morphWeights.Length * sizeof(float));
                weightsDirty = false;
            }

            shader.Use();
            shader.SetTexture(0, "morphAtlas", morphAtlas);
            shader.SetUniform1("g_nCompositeWidth", (uint)compositeWidth);
            shader.SetUniform1("g_nCompositeSlotCount", (uint)slotCount);
            shader.SetUniform1("g_nCompositeBaseOffset", baseOffset);

            rectBuffer.BindBufferBase();
            rectStartBuffer.BindBufferBase();
            rectEntryBuffer.BindBufferBase();
            weightBuffer.BindBufferBase();
            offsetBuffer.BindBufferBase();

            GL.DispatchCompute((slotCount + ComputeGroupSize - 1) / ComputeGroupSize, 1, 1);

            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
        }

        /// <summary>Sets the blend weight for the specified morph target.</summary>
        /// <param name="morphId">Morph target identifier.</param>
        /// <param name="value">Blend weight to apply.</param>
        public void SetMorphValue(int morphId, float value)
        {
            if (morphWeights[morphId] == value)
            {
                return;
            }

            morphWeights[morphId] = value;
            weightsDirty = true;
        }

        /// <summary>Deletes the GPU buffers owned by this composite. The scene owns the offsets buffer it gathers into.</summary>
        public void Delete()
        {
            rectBuffer.Delete();
            rectStartBuffer.Delete();
            rectEntryBuffer.Delete();
            weightBuffer.Delete();
        }
    }
}
