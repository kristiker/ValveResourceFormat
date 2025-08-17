using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Blocks;

#nullable disable

namespace GUI.Types.Renderer
{
    class DrawCall
    {
        public PrimitiveType PrimitiveType { get; set; }
        public int BaseVertex { get; set; }
        public uint VertexCount { get; set; }
        public nint StartIndex { get; set; } // pointer for GL call
        public int IndexCount { get; set; }
        //public float UvDensity { get; set; }     //TODO
        //public string Flags { get; set; }        //TODO
        public Vector4 TintColor { get; set; } = Vector4.One;

        public AABB? DrawBounds { get; set; }

        public int MeshId { get; set; }
        public int FirstMeshlet { get; set; }
        public int NumMeshlets { get; set; }
        public RenderMaterial Material { get; set; }

        public GPUMeshBufferCache MeshBuffers { get; set; }
        public string MeshName { get; set; } = string.Empty;
        public int VertexArrayObject { get; set; } = -1;

        public VertexDrawBuffer[] VertexBuffers { get; set; }
        public DrawElementsType IndexType { get; set; }
        public IndexDrawBuffer IndexBuffer { get; set; }
        public int VertexIdOffset { get; set; }


        public void SetNewMaterial(RenderMaterial newMaterial)
        {
            VertexArrayObject = -1;
            Material = newMaterial;

            if (newMaterial.Shader.IsLoaded)
            {
                UpdateVertexArrayObject();
            }
        }

        public void UpdateVertexArrayObject()
        {
            Debug.Assert(Material.Shader.IsLoaded, "Shader must be loaded (more specifically the attribute locations) before creating a VAO");

            VertexArrayObject = MeshBuffers.GetVertexArrayObject(
                   MeshName,
                   VertexBuffers,
                   Material,
                   IndexBuffer.Handle);

#if DEBUG
            var vaoName = $"{MeshName}+{Material.Material.Name}";
            vaoName = vaoName.Length > byte.MaxValue ? vaoName[..byte.MaxValue] : vaoName;
            GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, VertexArrayObject, vaoName.Length, vaoName);
#endif
        }
    }

    internal struct IndexDrawBuffer
    {
        public int Handle;
        public uint Offset;
    }

    internal struct VertexDrawBuffer
    {
        public int Handle;
        public uint Offset;
        public uint ElementSizeInBytes;
        public VBIB.RenderInputLayoutField[] InputLayoutFields;
    }
}
