using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// A <see cref="VkBuffer"/> and the VMA allocation backing it. Memory placement follows
/// <see cref="BufferUsage"/>, the same intent-based enum the OpenGL backend's <c>Buffer</c> already
/// uses, so callers do not need a Vulkan-specific vocabulary for "who writes, who reads".
/// Disposing queues the actual destruction; see <see cref="VulkanDeleteQueue"/>.
/// </summary>
public sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanDevice device;
    private readonly VmaAllocation allocation;

    /// <summary>The buffer handle.</summary>
    public VkBuffer Handle { get; }

    /// <summary>Size in bytes.</summary>
    public ulong Size { get; }

    /// <summary>
    /// Pointer to the persistently mapped allocation, or <see langword="null"/> for a
    /// <see cref="BufferUsage.Static"/>/<see cref="BufferUsage.GpuOnly"/> buffer, which is never
    /// host-visible.
    /// </summary>
    public void* MappedData { get; }

    private VulkanBuffer(VulkanDevice device, VkBuffer handle, VmaAllocation allocation, ulong size, void* mappedData)
    {
        this.device = device;
        Handle = handle;
        this.allocation = allocation;
        Size = size;
        MappedData = mappedData;
    }

    /// <summary>Creates an empty buffer of the given size, placed in memory appropriate to <paramref name="usage"/>.</summary>
    public static VulkanBuffer Create(VulkanDevice device, string name, ulong size, VkBufferUsageFlags bufferUsage, BufferUsage usage)
    {
        var bufferCreateInfo = new VkBufferCreateInfo
        {
            size = size,
            usage = bufferUsage,
            sharingMode = VkSharingMode.Exclusive,
        };

        var (allocationCreateInfo, persistentlyMapped) = ToAllocationCreateInfo(usage);

        VmaAllocationInfo allocationInfo;
        vmaCreateBuffer(device.VmaAllocator, in bufferCreateInfo, in allocationCreateInfo, out var buffer, out var allocation, &allocationInfo).CheckResult();

        SetDebugName(device, buffer, name);

        return new VulkanBuffer(device, buffer, allocation, size, persistentlyMapped ? allocationInfo.pMappedData : null);
    }

    /// <summary>
    /// Creates a device-local buffer already holding <paramref name="data"/>, uploaded through a
    /// throwaway staging buffer and <see cref="VulkanDevice.RunOneShotCommands"/>. For
    /// <see cref="BufferUsage.Static"/> data such as mesh vertex/index buffers, which are written
    /// once and read by the GPU every draw after.
    /// </summary>
    public static VulkanBuffer CreateWithData<T>(VulkanDevice device, string name, ReadOnlySpan<T> data, VkBufferUsageFlags bufferUsage) where T : unmanaged
    {
        var size = (ulong)(data.Length * sizeof(T));

        var staging = Create(device, $"{name} (staging)", size, VkBufferUsageFlags.TransferSrc, BufferUsage.Dynamic);

        try
        {
            staging.SetData(data);

            var final = Create(device, name, size, bufferUsage | VkBufferUsageFlags.TransferDst, BufferUsage.Static);

            device.RunOneShotCommands(commandBuffer =>
            {
                var region = new VkBufferCopy { size = size };
                device.Api.vkCmdCopyBuffer(commandBuffer, staging.Handle, final.Handle, 1, &region);
            });

            return final;
        }
        finally
        {
            staging.Dispose();
        }
    }

    /// <summary>
    /// Writes <paramref name="data"/> at <paramref name="offset"/> bytes into the mapped allocation.
    /// Only valid for a buffer created with a host-visible <see cref="BufferUsage"/>.
    /// </summary>
    public void SetData<T>(ReadOnlySpan<T> data, ulong offset = 0) where T : unmanaged
    {
        if (MappedData == null)
        {
            throw new InvalidOperationException($"Buffer is not host-visible; create it with {nameof(BufferUsage.Dynamic)} or {nameof(BufferUsage.Readback)} to write it directly, or use {nameof(CreateWithData)}.");
        }

        var destination = new Span<T>((byte*)MappedData + offset, data.Length);
        data.CopyTo(destination);

        // Coherent memory (HostAccessSequentialWrite without HOST_VISIBLE-but-not-COHERENT fallback)
        // makes this a no-op; harmless either way and correct if VMA ever picks non-coherent memory.
        vmaFlushAllocation(device.VmaAllocator, allocation, offset, (ulong)(data.Length * sizeof(T)));
    }

    private static (VmaAllocationCreateInfo, bool PersistentlyMapped) ToAllocationCreateInfo(BufferUsage usage) => usage switch
    {
        BufferUsage.Static or BufferUsage.GpuOnly => (new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice }, false),
        BufferUsage.Dynamic => (new VmaAllocationCreateInfo { usage = VmaMemoryUsage.Auto, flags = VmaAllocationCreateFlags.HostAccessSequentialWrite | VmaAllocationCreateFlags.Mapped }, true),
        BufferUsage.Readback => (new VmaAllocationCreateInfo { usage = VmaMemoryUsage.Auto, flags = VmaAllocationCreateFlags.HostAccessRandom | VmaAllocationCreateFlags.Mapped }, true),
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null),
    };

    private static void SetDebugName(VulkanDevice device, VkBuffer buffer, string name)
    {
#if DEBUG
        if (name.Length == 0 || !device.Instance.DebugUtilsEnabled)
        {
            return;
        }

        device.Instance.Api.vkSetDebugUtilsObjectNameEXT(device.Handle, VkObjectType.Buffer, buffer.Handle, name);
#endif
    }

    /// <summary>Queues this buffer's destruction; see <see cref="VulkanDeleteQueue"/>.</summary>
    public void Dispose()
    {
        var buffer = Handle;
        var bufferAllocation = allocation;
        var vmaAllocator = device.VmaAllocator;

        device.DeleteQueue.Queue(() => vmaDestroyBuffer(vmaAllocator, buffer, bufferAllocation));
    }
}
