using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// Turns compiled entity keyvalues into the runtime classes that simulate them. Classes with no
/// movement-relevant behaviour are skipped rather than spawned inert, so the world only holds
/// entities that do something or are named by something that does.
/// </summary>
public static class EntityFactory
{
    /// <summary>
    /// Creates the runtime entity for a compiled one.
    /// </summary>
    /// <param name="entity">The compiled entity.</param>
    /// <param name="classname">The entity's classname, already read by the caller.</param>
    /// <param name="parentTransform">Transform imposed by a containing <c>point_template</c>.</param>
    /// <returns>The runtime entity, or <see langword="null"/> when the class is not simulated.</returns>
    public static EntityInstance? Create(EntityLump.Entity entity, string classname, Matrix4x4 parentTransform)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var instance = CreateForClass(classname);

        instance?.Initialize(entity, classname, parentTransform);

        return instance;
    }

    /// <summary>
    /// Creates the entity standing in for the player, backed by a movement controller.
    /// </summary>
    /// <param name="controller">The movement state the entity exposes to triggers.</param>
    /// <returns>The player entity.</returns>
    public static PlayerEntity CreatePlayer(PlayerEntity.IPlayerController controller)
    {
        var player = new PlayerEntity { Controller = controller };

        player.Initialize(data: null, "player", Matrix4x4.Identity);

        return player;
    }

    private static EntityInstance? CreateForClass(string classname) => classname switch
    {
        "func_brush" or "func_breakable" or "func_wall" or "func_wall_toggle" or "func_illusionary"
            => new FuncBrush(),

        "func_door" => new LinearMover { Kind = LinearMover.MoverKind.Door },
        "func_movelinear" or "func_water_analog" => new LinearMover { Kind = LinearMover.MoverKind.MoveLinear },
        "func_button" or "func_physical_button" => new LinearMover { Kind = LinearMover.MoverKind.Button },

        "func_door_rotating" or "momentary_rot_button" or "func_rot_button"
            => new RotatingMover(),

        "func_rotating" => new FuncRotating(),

        "trigger_teleport" => new TriggerTeleport(),
        "trigger_push" => new TriggerPush(),
        "trigger_multiple" => new TriggerMultiple(),
        "trigger_once" => new TriggerMultiple { FireOnce = true },
        "trigger_hurt" => new TriggerHurt(),

        "logic_auto" => new LogicAuto(),
        "logic_relay" => new LogicRelay(),
        "logic_timer" => new LogicTimer(),
        "logic_case" => new LogicCase(),

        "filter_activator_name" or "filter_damage_type" or "filter_multi" or "filter_activator_class"
            => new FilterEntity(),

        // Named but inert: teleport destinations, path nodes and I/O aliases
        "info_teleport_destination" or "info_target" or "path_track" or "info_landmark"
        or "info_player_start" or "info_player_terrorist" or "info_player_counterterrorist"
            => new PointEntity(),

        _ => null,
    };
}
