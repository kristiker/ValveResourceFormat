using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer.UniformBuffers
{
    class StorageBuffer : Buffer
    {
        public StorageBuffer(int bindingPoint, string name)
            : base(BufferTarget.ShaderStorageBuffer, bindingPoint, name)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BindingPoint, Handle);
        }

        public void Create<T>(T[] data, int sizeOfData) where T : struct
        {
            GL.NamedBufferData(Handle, data.Length * sizeOfData, data, BufferUsageHint.StaticRead);
        }

        public void Update<T>(T[] data, int sizeOfData) where T : struct
        {
            GL.NamedBufferSubData(Handle, IntPtr.Zero, data.Length * sizeOfData, data);
        }
    }
}
