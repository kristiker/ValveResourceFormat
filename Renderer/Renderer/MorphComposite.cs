using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Per-mesh morph target state. The gather tables are shared with every other mesh using the same morph
    /// resource; this holds only the blend weights and the ranges the scene reserved for this mesh, which
    /// <see cref="Scene.DispatchMorphComposites"/> composites into per-vertex offsets.
    /// </summary>
    public class MorphComposite
    {
        /// <summary>Gets the gather tables shared by every mesh using the same morph resource.</summary>
        public GPUMeshBufferCache.MorphTables Tables { get; }

        /// <summary>Gets or sets the start of this mesh's range in the scene morph offsets buffer,
        /// assigned when the scene instance buffers are (re)built. <see langword="null"/> until then.</summary>
        public uint? OffsetsBase { get; set; }

        /// <summary>Gets or sets the start of this mesh's blend weights in the scene morph weights buffer.</summary>
        public uint WeightsBase { get; set; }

        /// <summary>Gets whether this mesh has anything to gather, and so ranges worth reserving.</summary>
        public bool HasMorphData => Tables.HasMorphData;

        private readonly float[] morphWeights;
        private bool weightsDirty = true;

        /// <summary>Initializes morph state for the given morph data, taking a reference to its shared gather tables.</summary>
        /// <param name="renderContext">Renderer context owning the shared morph table cache.</param>
        /// <param name="morph">Morph data describing the morph targets and atlas layout.</param>
        public MorphComposite(RendererContext renderContext, Morph morph)
        {
            Tables = renderContext.MeshBufferCache.GetMorphTables(morph);
            morphWeights = new float[Tables.WeightCount];
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

        /// <summary>Marks the weights as needing another upload, after the scene buffers were rebuilt.</summary>
        public void InvalidateWeights()
        {
            weightsDirty = true;
        }

        /// <summary>Uploads this mesh's blend weights into its range of the scene morph weights buffer, if they changed.</summary>
        /// <returns><see langword="true"/> if an upload happened, and so this mesh needs compositing again.</returns>
        internal bool UploadWeights(StorageBuffer weightBuffer)
        {
            if (!weightsDirty)
            {
                return false;
            }

            weightBuffer.Update(morphWeights, (int)WeightsBase * sizeof(float), morphWeights.Length * sizeof(float));
            weightsDirty = false;

            return true;
        }
    }
}
