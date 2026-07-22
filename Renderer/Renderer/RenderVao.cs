using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer
{
    /// <summary>
    /// Vertex array state for geometry that is unique per pipeline.
    /// Bind sites probe this with the shader about to be used. Creation and storage of the VAOs is
    /// backed by <see cref="GPUMeshBufferCache"/>; this only memoizes the most recent lookups.
    /// </summary>
    /// <param name="meshBuffers">The cache that creates and owns the VAOs.</param>
    /// <param name="meshName">Name of the mesh, used as part of the cache key and for labeling.</param>
    /// <param name="vertexBuffers">Vertex buffer bindings describing the geometry layout.</param>
    /// <param name="indexBuffer">OpenGL handle of the index buffer, or 0 for non-indexed geometry.</param>
    /// <param name="inputSignature">Material input signature mapping buffer semantics to shader attribute names.</param>
    public class RenderVao(GPUMeshBufferCache meshBuffers, string meshName, VertexDrawBuffer[] vertexBuffers, int indexBuffer, Material.VsInputSignature inputSignature)
    {
        /// <summary>Initializes vertex array state for untracked geometry in a single vertex buffer.
        /// The vertex buffer handle is appended to the name so instances sharing a label stay distinct in the cache.</summary>
        /// <param name="meshBuffers">The cache that creates and owns the VAOs.</param>
        /// <param name="label">Label for the geometry, used as part of the cache key and for labeling.</param>
        /// <param name="vertexBuffer">OpenGL handle of the vertex buffer.</param>
        /// <param name="stride">Size in bytes of a single vertex.</param>
        /// <param name="inputLayoutFields">Input layout describing the vertex attributes.</param>
        /// <param name="indexBuffer">OpenGL handle of the index buffer, or 0 for non-indexed geometry.</param>
        /// <param name="inputSignature">Optional material input signature mapping buffer semantics to shader attribute names.</param>
        public RenderVao(GPUMeshBufferCache meshBuffers, string label, int vertexBuffer, int stride, VBIB.RenderInputLayoutField[] inputLayoutFields,
            int indexBuffer = 0, Material.VsInputSignature inputSignature = default)
            : this(meshBuffers, $"{label}#{vertexBuffer}",
            [
                new VertexDrawBuffer
                {
                    Handle = vertexBuffer,
                    ElementSizeInBytes = (uint)stride,
                    InputLayoutFields = inputLayoutFields,
                },
            ], indexBuffer, inputSignature)
        {
        }

        private int primaryProgram = -1;
        private int primaryVao = -1;
        private int replacementProgram = -1;
        private int replacementVao = -1;

        /// <summary>Returns the VAO matching the given shader, creating it through the cache on first use.</summary>
        /// <param name="shader">The shader the geometry is about to be rendered with.</param>
        /// <returns>The OpenGL VAO handle.</returns>
        public int Get(Shader shader)
        {
            if (shader.Program == primaryProgram)
            {
                return primaryVao;
            }

            if (shader.Program == replacementProgram)
            {
                return replacementVao;
            }

            shader.EnsureLoaded();
            var vao = meshBuffers.GetVertexArrayObject(meshName, vertexBuffers, shader, inputSignature, indexBuffer);

            if (primaryProgram == -1)
            {
                primaryProgram = shader.Program;
                primaryVao = vao;
            }
            else
            {
                replacementProgram = shader.Program;
                replacementVao = vao;
            }

            return vao;
        }

        /// <summary>Deletes the cached VAOs created under this state's name. Only for geometry with a
        /// name unique to this instance; VAOs of cache-tracked meshes are freed with their buffers.</summary>
        public void Delete()
        {
            meshBuffers.DeleteVertexArrayObjects(meshName);

            primaryProgram = -1;
            primaryVao = -1;
            replacementProgram = -1;
            replacementVao = -1;
        }
    }
}
