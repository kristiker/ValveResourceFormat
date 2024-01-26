using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer.UniformBuffers
{
    class UniformBuffer<T> : Buffer
        where T : new()
    {
        T data;
        public T Data { get => data; set { data = value; Update(); } }

        // A buffer where the structure is marshalled into, before being sent to the GPU
        readonly float[] cpuBuffer;
        readonly GCHandle cpuBufferHandle;

        public UniformBuffer(int bindingPoint) : base(BufferTarget.UniformBuffer, bindingPoint, typeof(T).Name)
        {
            Size = Marshal.SizeOf<T>();
            Debug.Assert(Size % 16 == 0);

            cpuBuffer = new float[Size / 4];
            cpuBufferHandle = GCHandle.Alloc(cpuBuffer, GCHandleType.Pinned);

            data = new T();
            Initialize();
        }

        private void WriteToCpuBuffer()
        {
            Debug.Assert(Size == Marshal.SizeOf(data));
            Marshal.StructureToPtr(data, cpuBufferHandle.AddrOfPinnedObject(), false);
        }

        private void Initialize()
        {
            Bind();
            WriteToCpuBuffer();
            GL.BufferData(Target, Size, cpuBuffer, BufferUsageHint.StaticDraw);
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, BindingPoint, Handle);
            Unbind();
        }

        public void Update()
        {
            Bind();
            WriteToCpuBuffer();
            GL.BufferSubData(Target, IntPtr.Zero, Size, cpuBuffer);
            Unbind();
        }

        public override void Dispose()
        {
            cpuBufferHandle.Free();
            base.Dispose();
        }
    }
}
