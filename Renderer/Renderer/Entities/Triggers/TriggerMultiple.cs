namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_multiple</c>. Fires <c>OnTrigger</c> on entry and then goes quiet for <c>wait</c>
/// seconds before it can fire again. <c>trigger_once</c> uses the same class and disables itself
/// after the first fire.
/// </summary>
public sealed class TriggerMultiple : TriggerEntity
{
    private float nextTriggerTime;

    /// <summary>Gets a value indicating whether this trigger disables itself after firing once.</summary>
    public bool FireOnce { get; init; }

    /// <inheritdoc/>
    protected override void OnStartTouch(EntityInstance? toucher)
    {
        base.OnStartTouch(toucher);

        if (World.Time < nextTriggerTime)
        {
            return;
        }

        SendOutput("OnTrigger", toucher);

        if (FireOnce)
        {
            Enabled = false;
            return;
        }

        // A negative wait means "never re-fire"
        var wait = GetFloat("wait", 1f);
        nextTriggerTime = wait < 0f ? float.MaxValue : World.Time + wait;
    }
}
