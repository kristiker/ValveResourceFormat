using System.Linq;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The live entity set for a map: name lookup, the delayed entity I/O queue, and the per-frame tick
/// that advances movers and tests trigger volumes against the player.
/// </summary>
public sealed class EntityWorld
{
    /// <summary>A queued input waiting for its delay to elapse.</summary>
    private readonly record struct QueuedInput(
        float FireTime,
        long Sequence,
        EntityInstance Target,
        string InputName,
        string Parameter,
        EntityInstance? Activator,
        EntityInstance? Caller);

    private readonly List<EntityInstance> entities = [];
    private readonly Dictionary<string, List<EntityInstance>> byTargetName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<EntityInstance>> byClassname = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EntityInstance> thinkers = [];
    private readonly List<QueuedInput> queue = [];
    private readonly List<QueuedInput> dueBuffer = [];
    private long sequenceCounter;
    private bool spawned;

    /// <summary>
    /// Guards against a self-retriggering output chain (a relay that fires itself with no delay)
    /// spinning forever inside a single tick.
    /// </summary>
    private const int MaxInputsPerTick = 4096;

    /// <summary>Gets all entities in the world.</summary>
    public IReadOnlyList<EntityInstance> Entities => entities;

    /// <summary>Gets the solid brush entities, in registration order. Traced by player movement.</summary>
    public List<BrushEntity> BrushEntities { get; } = [];

    /// <summary>Gets the trigger volumes, in registration order. Tested for overlap every tick.</summary>
    public List<TriggerEntity> Triggers { get; } = [];

    /// <summary>Gets the elapsed world time in seconds, advanced by <see cref="Tick"/>.</summary>
    public float Time { get; private set; }

    /// <summary>
    /// Gets or sets the entity standing in for the player in I/O chains. Triggers name it as the
    /// activator, and <c>!player</c> resolves to it.
    /// </summary>
    public EntityInstance? Player { get; set; }

    /// <summary>Gets or sets a value indicating whether entity simulation runs. Movers and triggers freeze when unset.</summary>
    public bool Simulating { get; set; } = true;

    /// <summary>
    /// Adds an entity. Registration order is the compiled order, which is also the order the engine
    /// resolves duplicate names in.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    public void Add(EntityInstance entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.World = this;
        entities.Add(entity);

        if (!string.IsNullOrEmpty(entity.TargetName))
        {
            Register(byTargetName, entity.TargetName, entity);
        }

        Register(byClassname, entity.Classname, entity);

        if (entity is BrushEntity brush)
        {
            BrushEntities.Add(brush);
        }

        if (entity is TriggerEntity trigger)
        {
            Triggers.Add(trigger);
        }

        if (entity.WantsThink)
        {
            thinkers.Add(entity);
        }

        if (spawned)
        {
            entity.Spawn();
        }
    }

    private static void Register(Dictionary<string, List<EntityInstance>> index, string key, EntityInstance entity)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }

        list.Add(entity);
    }

    /// <summary>
    /// Runs every entity's spawn pass. Call once after the whole map has been added, so spawn code
    /// can resolve names regardless of compile order.
    /// </summary>
    public void SpawnAll()
    {
        if (spawned)
        {
            return;
        }

        spawned = true;

        // Snapshot: an entity's Spawn may add more (and those get spawned by Add)
        foreach (var entity in entities.ToArray())
        {
            entity.Spawn();
        }
    }

    /// <summary>
    /// Advances world time, delivers every input whose delay has elapsed, and runs thinkers.
    /// </summary>
    /// <param name="deltaTime">Seconds since the previous tick.</param>
    public void Tick(float deltaTime)
    {
        if (!Simulating)
        {
            return;
        }

        Time += deltaTime;

        // Latch each brush's pose before anything moves it, so a rider can be carried from where
        // the brush was to where it ends up
        foreach (var brush in BrushEntities)
        {
            brush.BeginTick();
        }

        DispatchDueInputs();

        // Indexed rather than foreach: an entity is free to spawn another while thinking
        for (var i = 0; i < thinkers.Count; i++)
        {
            thinkers[i].Think(deltaTime);
        }

        // Thinking can queue zero-delay inputs (a door reaching its end fires OnFullyOpen);
        // deliver them now so the chain resolves within the same tick, as the engine does.
        DispatchDueInputs();
    }

    private void DispatchDueInputs()
    {
        var delivered = 0;

        while (delivered < MaxInputsPerTick)
        {
            dueBuffer.Clear();

            for (var i = queue.Count - 1; i >= 0; i--)
            {
                if (queue[i].FireTime <= Time)
                {
                    dueBuffer.Add(queue[i]);
                    queue.RemoveAt(i);
                }
            }

            if (dueBuffer.Count == 0)
            {
                return;
            }

            // Same-frame inputs keep the order their outputs fired in
            dueBuffer.Sort(static (a, b) => a.FireTime != b.FireTime
                ? a.FireTime.CompareTo(b.FireTime)
                : a.Sequence.CompareTo(b.Sequence));

            foreach (var input in dueBuffer)
            {
                input.Target.AcceptInput(input.InputName, input.Parameter, input.Activator, input.Caller);
                delivered++;
            }
        }
    }

    /// <summary>
    /// Fires one of <paramref name="caller"/>'s outputs, queuing the matching connections.
    /// </summary>
    /// <param name="caller">The entity whose output fired.</param>
    /// <param name="output">The output name.</param>
    /// <param name="activator">The entity that started the chain, usually the player.</param>
    public void SendOutput(EntityInstance caller, string output, EntityInstance? activator)
    {
        ArgumentNullException.ThrowIfNull(caller);

        foreach (var connection in caller.Connections)
        {
            if (connection.Exhausted || !string.Equals(connection.OutputName, output, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            connection.TimesFired++;

            foreach (var target in ResolveTargets(connection.TargetName, activator, caller))
            {
                queue.Add(new QueuedInput(
                    Time + MathF.Max(0f, connection.Delay),
                    sequenceCounter++,
                    target,
                    connection.InputName,
                    connection.OverrideParam,
                    activator,
                    caller));
            }
        }
    }

    /// <summary>
    /// Queues an input directly, bypassing the output list. Used to drive the world from outside,
    /// for example a debug command.
    /// </summary>
    /// <param name="targetName">The target name, classname or special name.</param>
    /// <param name="input">The input to fire.</param>
    /// <param name="parameter">The input parameter.</param>
    /// <param name="delay">Delay in seconds.</param>
    /// <param name="activator">The activator to propagate.</param>
    public void QueueInput(string targetName, string input, string parameter = "", float delay = 0f, EntityInstance? activator = null)
    {
        foreach (var target in ResolveTargets(targetName, activator, null))
        {
            queue.Add(new QueuedInput(Time + MathF.Max(0f, delay), sequenceCounter++, target, input, parameter, activator, null));
        }
    }

    /// <summary>
    /// Queues an input on one known entity, for self-scheduled behaviour such as a door shutting
    /// itself after its wait expires.
    /// </summary>
    /// <param name="target">The entity receiving the input.</param>
    /// <param name="input">The input to fire.</param>
    /// <param name="parameter">The input parameter.</param>
    /// <param name="delay">Delay in seconds.</param>
    /// <param name="activator">The activator to propagate.</param>
    public void QueueInputOn(EntityInstance target, string input, string parameter = "", float delay = 0f, EntityInstance? activator = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        queue.Add(new QueuedInput(Time + MathF.Max(0f, delay), sequenceCounter++, target, input, parameter, activator, null));
    }

    /// <summary>
    /// Resolves an I/O target name to the entities it names. Handles the <c>!</c> special names, an
    /// exact targetname, a trailing <c>*</c> wildcard, and finally a classname.
    /// </summary>
    /// <param name="targetName">The name to resolve.</param>
    /// <param name="activator">The chain's activator, for <c>!activator</c> and <c>!player</c>.</param>
    /// <param name="caller">The firing entity, for <c>!self</c> and <c>!caller</c>.</param>
    /// <returns>The matching entities, possibly empty.</returns>
    public IEnumerable<EntityInstance> ResolveTargets(string? targetName, EntityInstance? activator, EntityInstance? caller)
    {
        if (string.IsNullOrEmpty(targetName))
        {
            return [];
        }

        if (targetName[0] == '!')
        {
            EntityInstance? special = null;

            if (string.Equals(targetName, "!self", StringComparison.OrdinalIgnoreCase) || string.Equals(targetName, "!caller", StringComparison.OrdinalIgnoreCase))
            {
                special = caller;
            }
            else if (string.Equals(targetName, "!activator", StringComparison.OrdinalIgnoreCase))
            {
                special = activator;
            }
            else if (string.Equals(targetName, "!player", StringComparison.OrdinalIgnoreCase))
            {
                special = Player;
            }

            return special == null ? [] : [special];
        }

        if (byTargetName.TryGetValue(targetName, out var named))
        {
            return named;
        }

        if (targetName.EndsWith('*'))
        {
            var prefix = targetName[..^1];
            return entities.Where(e => e.TargetName != null && e.TargetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return byClassname.TryGetValue(targetName, out var classed) ? classed : [];
    }

    /// <summary>
    /// Finds the first entity with the given targetname.
    /// </summary>
    /// <param name="targetName">The name to look up.</param>
    /// <returns>The entity, or <see langword="null"/> when nothing matches.</returns>
    public EntityInstance? FindByName(string? targetName)
        => targetName != null && byTargetName.TryGetValue(targetName, out var list) && list.Count > 0 ? list[0] : null;

    /// <summary>
    /// Tests every enabled trigger against the player hull, firing touch outputs on the transitions.
    /// Call after player movement has settled for the frame.
    /// </summary>
    /// <param name="center">Centre of the player's collision hull.</param>
    /// <param name="halfExtents">Half-extents of the player's collision hull.</param>
    public void UpdateTriggerTouch(Vector3 center, Vector3 halfExtents)
    {
        if (!Simulating)
        {
            return;
        }

        foreach (var trigger in Triggers)
        {
            trigger.UpdateTouch(center, halfExtents, Player);
        }
    }
}
