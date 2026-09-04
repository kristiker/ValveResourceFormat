namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Defers <c>vkDestroy*</c>/<c>vmaDestroy*</c> calls so a caller can dispose a GPU resource without
/// knowing whether the command buffer that last touched it has finished on the GPU - the one thing
/// OpenGL gives every caller for free and Vulkan does not.
/// <para>
/// Correct because the renderer is fully CPU/GPU-serialized right now (<see cref="VulkanFrame"/>
/// waits its fence before recording): nothing is ever in flight except between a
/// <c>vkQueueSubmit2</c> and the following frame's wait, so anything queued for deletion by the time
/// that wait succeeds is provably safe to actually destroy right then, and <see cref="Flush"/> is
/// called from exactly that point. Multiple frames in flight will need one queue per frame-in-flight
/// slot instead of this single one, flushed after that slot's own fence wait; this already is that
/// pattern with one slot.
/// </para>
/// </summary>
public sealed class VulkanDeleteQueue
{
    private List<Action> pending = [];

    /// <summary>Defers <paramref name="destroy"/> until the next point it is provably safe to run.</summary>
    public void Queue(Action destroy) => pending.Add(destroy);

    /// <summary>
    /// Runs and clears every deferred action queued since the last flush. Call only where nothing
    /// queued since is still referenced by in-flight GPU work; see the type-level remarks.
    /// </summary>
    public void Flush()
    {
        if (pending.Count == 0)
        {
            return;
        }

        // Swapped rather than cleared in place, in case a destroy callback queues another deletion.
        var toRun = pending;
        pending = [];

        foreach (var destroy in toRun)
        {
            destroy();
        }
    }
}
