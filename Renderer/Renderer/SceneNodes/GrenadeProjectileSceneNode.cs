using ValveResourceFormat.ResourceTypes;

namespace ValveResourceFormat.Renderer.SceneNodes;

/// <summary>
/// A thrown grenade, simulated the way CS:GO's <c>CBaseCSGrenadeProjectile</c> is: a 4 unit cube
/// swept through the world under <c>MOVETYPE_FLYGRAVITY</c>, bouncing off whatever it hits with
/// <c>MOVECOLLIDE_FLY_CUSTOM</c> resolution, then detonating into a particle effect.
/// </summary>
public sealed class GrenadeProjectileSceneNode : ModelSceneNode
{
    /// <summary>The kind of grenade, which decides the fuse and what detonating spawns.</summary>
    public enum GrenadeKind
    {
        /// <summary>A smoke grenade: pops once its fuse ran out and it came to rest.</summary>
        Smoke,

        /// <summary>A high explosive grenade: detonates when its fuse runs out, wherever it is.</summary>
        Explosive,
    }

    /// <summary>Layer shared by every projectile and detonation effect. The "Internal - " prefix keeps
    /// the world layer list in the UI from switching them off (see <see cref="Scene.SetEnabledLayers"/>).</summary>
    public const string ProjectileLayerName = "Internal - Grenade Projectile";

    // Source runs entity physics on a fixed tick, and the bounce resolution carries the remainder
    // of one tick's movement into the push that follows it, so the simulation is stepped at the
    // game's tick rate rather than at the render framerate.
    private const float TickInterval = 1f / 64f;
    private const int MaxTicksPerFrame = 8;

    private const float SvGravity = 800f;

    /// <summary>Half extents of the collision hull. CBaseCSGrenadeProjectile::Spawn gives a grenade a
    /// "smaller, cube bounding box so we rest on the ground" rather than the shape of its model.</summary>
    public static readonly Vector3 HullHalfExtents = new(2f, 2f, 2f);

    // basegrenade_shared: a grenade falls at 40% of world gravity and keeps 45% of its speed
    // through a bounce.
    private const float GrenadeGravity = 0.4f;
    private const float GrenadeElasticity = 0.45f;

    /// <summary>Below this speed a grenade that hit something stops dead (CS:GO uses 30 u/s).</summary>
    private const float StopSpeed = 30f;

    /// <summary>Source's PhysicsClipVelocity snaps velocity components smaller than this to zero.</summary>
    private const float StopEpsilon = 0.1f;

    /// <summary>A bounce softer than this impact speed stays silent. The game plays a sound on every
    /// collision, but a grenade rolling along the floor collides on every tick, and one tick of
    /// gravity is only a few units per second of it.</summary>
    private const float BounceSoundMinSpeed = 30f;

    /// <summary>Bounces play under the event's authored volume, which is mixed for the game's own balance.</summary>
    private const float BounceSoundVolume = 0.6f;

    /// <summary>Fuse length, CS:GO's <c>GRENADE_TIMER</c>. A smoke's only arms it (see
    /// <see cref="ShouldDetonate"/>); an HE grenade goes off the moment it runs out.</summary>
    private const float GrenadeTimer = 1.5f;

    /// <summary>Gap kept between the hull and whatever it lands on. The swept-box test resolves 13
    /// separating axes and reports the one it entered on: a box that starts out exactly touching, or
    /// a hair inside, can enter on a side axis and hand back a sideways "surface" normal, which turns
    /// a bounce into a random deflection. Staying clear of the surface keeps the entry axis honest.</summary>
    private const float SurfaceEpsilon = 0.03125f;

    /// <summary>How long the detonation effect plays before the projectile can be handed out again.
    /// A smoke cloud is spent after about 17 seconds; the canister lies there venting until then.</summary>
    private const float SmokeEffectDuration = 17f;
    private const float ExplosionEffectDuration = 6f;

    /// <summary>A grenade that never settles (thrown out of the world, say) gives up after this long.</summary>
    private const float MaxFlightTime = 20f;

    // The tumble CS:GO gives a thrown grenade: AngularImpulse(600, ...) degrees per second.
    private const float TumbleRate = 600f;

    /// <summary>Sound events grenades can play, for warming the sound cache.</summary>
    public static readonly string[] Sounds = [
        "SmokeGrenade.Bounce",
        "BaseSmokeEffect.Sound",
        "HEGrenade.Bounce",
        "BaseGrenade.Explode",
    ];

    /// <summary>Gets the kind of grenade this projectile is.</summary>
    public GrenadeKind Kind { get; }

    /// <summary>Gets whether this projectile is in flight, or its detonation effect is still playing.
    /// A projectile that is not live can be thrown again by <see cref="Launch"/>.</summary>
    public bool Live { get; private set; }

    /// <summary>Gets whether the grenade is still on its way - thrown, and not yet gone off.</summary>
    public bool InFlight => Live && !detonated;

    /// <summary>Gets the grenade's world position as drawn - interpolated between simulation ticks,
    /// so a camera reading it agrees with the model exactly. After it detonates this is where it
    /// went off.</summary>
    public Vector3 Position => renderPosition;

    private readonly ParticleSceneNode? detonationEffect;
    private readonly string bounceSound;
    private readonly string detonateSound;
    private readonly float effectDuration;

    private Vector3 position;
    private Vector3 previousTickPosition;
    private Vector3 renderPosition;
    private Vector3 velocity;
    private Vector3 tumbleAxis = Vector3.UnitY;
    private Quaternion orientation = Quaternion.Identity;
    private float spinAngle;
    private bool onGround;

    private float fuse;
    private float flightTime;
    private bool detonated;
    private float effectTimeLeft;
    private float tickAccumulator;

    /// <summary>
    /// Creates a projectile and its detonation effect. Both are built up front and then reused,
    /// because standing up a particle renderer mid-throw would stall the frame.
    /// </summary>
    /// <param name="scene">The scene to add the projectile and its effect to.</param>
    /// <param name="model">The grenade world model.</param>
    /// <param name="kind">Which grenade this is.</param>
    /// <param name="detonationEffect">The effect its detonation plays, if it could be loaded.</param>
    public GrenadeProjectileSceneNode(Scene scene, Model model, GrenadeKind kind, ParticleSystem? detonationEffect)
        : base(scene, model)
    {
        Kind = kind;
        LayerName = ProjectileLayerName;
        LayerEnabled = false;

        (bounceSound, detonateSound, effectDuration) = kind switch
        {
            GrenadeKind.Smoke => ("SmokeGrenade.Bounce", "BaseSmokeEffect.Sound", SmokeEffectDuration),
            _ => ("HEGrenade.Bounce", "BaseGrenade.Explode", ExplosionEffectDuration),
        };

        if (detonationEffect != null)
        {
            // The effect is its own entity in the world rather than a child of the grenade: it
            // outlives the projectile, and a smoke fills a far larger volume than the model does.
            this.detonationEffect = new ParticleSceneNode(scene, detonationEffect)
            {
                LayerName = ProjectileLayerName,
                LayerEnabled = false,
            };

            scene.Add(this.detonationEffect, true);
        }
    }

    /// <summary>
    /// Throws this projectile from <paramref name="origin"/> at <paramref name="velocity"/> and
    /// starts its fuse.
    /// </summary>
    public void Launch(Vector3 origin, Vector3 velocity)
    {
        position = origin;
        previousTickPosition = origin;
        renderPosition = origin;
        spinAngle = 0f;
        this.velocity = velocity;
        onGround = false;
        detonated = false;
        Live = true;
        fuse = GrenadeTimer;
        flightTime = 0f;
        effectTimeLeft = 0f;
        tickAccumulator = 0f;
        orientation = Quaternion.Identity;

        // Tumble end over end, around the axis lying across the throw.
        var across = Vector3.Cross(velocity, Vector3.UnitZ);
        tumbleAxis = across.LengthSquared() > 1e-6f ? Vector3.Normalize(across) : Vector3.UnitY;

        LayerEnabled = true;
        ApplyTransform();
    }

    /// <summary>
    /// The camera-dependent half of the frame; <see cref="Simulate"/> does the flight.
    /// </summary>
    public override void Update(Scene.UpdateContext context)
    {
        if (Live && LayerEnabled)
        {
            base.Update(context);
        }
    }

    /// <summary>
    /// Advances the flight, the fuse and the detonation effect.
    /// </summary>
    /// <param name="timestep">Elapsed time in seconds since the last frame.</param>
    public void Simulate(float timestep)
    {
        if (!Live)
        {
            return;
        }

        if (detonated)
        {
            effectTimeLeft -= timestep;

            if (effectTimeLeft <= 0f)
            {
                Live = false;
                LayerEnabled = false;

                if (detonationEffect != null)
                {
                    detonationEffect.LayerEnabled = false;
                }
            }

            return;
        }

        flightTime += timestep;

        // Catch up on whole ticks only; the remainder carries into the next frame, so the
        // simulation advances at a steady rate whatever the framerate does.
        tickAccumulator += timestep;

        var ticks = 0;

        while (tickAccumulator >= TickInterval && ticks < MaxTicksPerFrame)
        {
            tickAccumulator -= TickInterval;
            ticks++;

            // Where the grenade was at the start of this tick, so the frames in between can be
            // drawn along the way rather than waiting on the next one.
            previousTickPosition = position;

            PhysicsToss();

            fuse -= TickInterval;

            if (ShouldDetonate())
            {
                Detonate();
                return;
            }
        }

        if (ticks == MaxTicksPerFrame)
        {
            // Too far behind to catch up: drop the backlog rather than spiralling.
            tickAccumulator = 0f;
        }

        if (flightTime > MaxFlightTime)
        {
            Detonate();
            return;
        }

        // Drawn between the two most recent tick positions rather than snapped to the newer one.
        // The flight steps at a fixed rate and the frames do not line up with it, so without this
        // the grenade lurches once every couple of frames - and a camera following it drags the
        // whole world along with the lurch.
        renderPosition = Vector3.Lerp(previousTickPosition, position, MathUtils.Saturate(tickAccumulator / TickInterval));

        // The tumble is decoration, so it runs off frame time and stays smooth on its own.
        if (!onGround || velocity != Vector3.Zero)
        {
            spinAngle += float.DegreesToRadians(TumbleRate * timestep);
        }

        orientation = Quaternion.CreateFromAxisAngle(tumbleAxis, spinAngle);

        ApplyTransform();
    }

    // A smoke grenade's fuse only arms it: CSmokeGrenadeProjectile::Think_Detonate holds off while
    // the grenade is still moving, which is why a smoke rolled down a corridor pops where it came
    // to rest. An HE grenade goes off wherever it happens to be when its fuse runs out.
    private bool ShouldDetonate()
    {
        if (fuse > 0f)
        {
            return false;
        }

        return Kind != GrenadeKind.Smoke || velocity.Length() <= 0.1f;
    }

    private void ApplyTransform()
    {
        var previousBounds = BoundingBox;

        Transform = Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(renderPosition);

        if (LayerEnabled && !previousBounds.Equals(BoundingBox))
        {
            Scene.DynamicOctree.Update(this, previousBounds);
        }
    }

    private void Detonate()
    {
        detonated = true;
        effectTimeLeft = effectDuration;

        // An HE grenade is consumed by its own blast. A smoke canister is not: it lies where it
        // stopped, venting, and only goes away once the cloud is spent.
        LayerEnabled = Kind == GrenadeKind.Smoke;

        Sound.Play(detonateSound, position);

        if (detonationEffect != null)
        {
            detonationEffect.Transform = Matrix4x4.CreateTranslation(position);
            detonationEffect.LayerEnabled = true;
            detonationEffect.Restart();
        }
    }

    /// <summary>
    /// One tick of Source's <c>CBaseEntity::PhysicsToss</c> for a <c>MOVETYPE_FLYGRAVITY</c> entity.
    /// </summary>
    private void PhysicsToss()
    {
        // Moving upward takes the grenade off the ground again.
        if (velocity.Z > 0f)
        {
            onGround = false;
        }

        if (onGround && velocity == Vector3.Zero)
        {
            return;
        }

        // PhysicsAddGravityMove: the vertical move uses the average of the old and the new
        // velocity, so a tick of free fall is exact rather than an Euler step.
        var move = new Vector3(velocity.X * TickInterval, velocity.Y * TickInterval, 0f);

        if (!onGround)
        {
            var newVelocityZ = velocity.Z - GrenadeGravity * SvGravity * TickInterval;
            move.Z = (velocity.Z + newVelocityZ) * 0.5f * TickInterval;
            velocity.Z = newVelocityZ;
        }

        var trace = PushEntity(move);

        if (trace is { Hit: true, IsValid: true })
        {
            var moveLength = move.Length();
            var fraction = moveLength > 0f ? MathUtils.Saturate(trace.Distance / moveLength) : 0f;

            ResolveFlyCollisionCustom(trace, fraction);
        }
    }

    /// <summary>
    /// Sweeps the grenade hull along <paramref name="move"/>, leaves it where it stopped, and
    /// returns what it ran into.
    /// </summary>
    private Rubikon.TraceResult PushEntity(Vector3 move)
    {
        var physics = Scene.PhysicsWorld;

        if (physics == null)
        {
            position += move;
            return new Rubikon.TraceResult { IsValid = false };
        }

        var trace = SweepHull(physics, position, position + move);

        // A sweep shorter than the trace epsilon says nothing about the geometry. Moving anyway
        // would let a grenade creep through a surface it rests against a fraction at a time, so a
        // move too small to check is a move not made.
        if (!trace.IsValid)
        {
            return trace;
        }

        position = trace.Hit ? trace.HitPosition : position + move;

        return trace;
    }

    /// <summary>
    /// Sweeps the grenade hull, stopping it <see cref="SurfaceEpsilon"/> short of whatever it runs
    /// into, measured perpendicular to that surface. <see cref="Rubikon.TraceResult.HitPosition"/>
    /// comes back as the position the hull can sit at without touching anything.
    /// </summary>
    public static Rubikon.TraceResult SweepHull(Rubikon physics, Vector3 from, Vector3 to)
    {
        var trace = physics.TraceAABB(from, to, HullHalfExtents, Rubikon.GrenadeCollisionName);

        if (!trace.Hit || !trace.IsValid)
        {
            return trace;
        }

        var direction = Vector3.Normalize(to - from);
        var approach = -Vector3.Dot(direction, trace.HitNormal);

        // Only back off when the sweep actually closes on the surface. A glancing contact - the
        // surface all but parallel to the move - needs no margin, and dividing by an approach near
        // zero there would eat the whole move and pin the grenade against a wall it should skim.
        var margin = approach > 0.001f ? SurfaceEpsilon / approach : 0f;

        trace.Distance = MathF.Max(trace.Distance - margin, 0f);
        trace.HitPosition = from + direction * trace.Distance;

        return trace;
    }

    /// <summary>
    /// CS:GO's <c>CBaseCSGrenadeProjectile::ResolveFlyCollisionCustom</c>: reflect off the surface,
    /// scrub the reflection by the grenade's elasticity, and come to rest once that leaves it slow
    /// enough. Steep surfaces are not stood on, so a grenade hitting a wall keeps falling.
    /// </summary>
    private void ResolveFlyCollisionCustom(in Rubikon.TraceResult trace, float fraction)
    {
        if (trace.HitNormal.LengthSquared() < 0.5f)
        {
            return; // degenerate triangle: nothing sensible to reflect off
        }

        var impactSpeed = MathF.Abs(Vector3.Dot(velocity, trace.HitNormal));

        // A backoff of 2 is a reflection; the elasticity then takes speed back out of it.
        var elasticity = Math.Clamp(GrenadeElasticity, 0f, 0.9f);
        var bounced = ClipVelocity(velocity, trace.HitNormal, 2f) * elasticity;

        var slow = bounced.LengthSquared() < StopSpeed * StopSpeed;

        if (trace.HitNormal.Z > 0.7f) // don't slide on steep inclines
        {
            velocity = bounced;

            if (slow)
            {
                onGround = true;
                velocity = Vector3.Zero;
            }
            else
            {
                // Spend what was left of this tick's movement carrying on off the surface.
                // Source does not resolve a second collision out of this push, so neither do we.
                PushEntity(bounced * ((1f - fraction) * TickInterval));
            }
        }
        else if (slow)
        {
            // Too slow to get away from a wall: gravity would otherwise push it back in every tick.
            velocity = Vector3.Zero;
        }
        else
        {
            velocity = bounced;
        }

        if (impactSpeed >= BounceSoundMinSpeed)
        {
            Sound.Play(bounceSound, trace.HitPosition, volume: BounceSoundVolume);
        }
    }

    /// <summary>
    /// Source's <c>PhysicsClipVelocity</c>. An <paramref name="overbounce"/> of 1 slides along the
    /// plane, 2 reflects off it.
    /// </summary>
    private static Vector3 ClipVelocity(Vector3 velocity, Vector3 normal, float overbounce)
    {
        var backoff = Vector3.Dot(velocity, normal) * overbounce;
        var clipped = velocity - normal * backoff;

        return new Vector3(
            MathF.Abs(clipped.X) < StopEpsilon ? 0f : clipped.X,
            MathF.Abs(clipped.Y) < StopEpsilon ? 0f : clipped.Y,
            MathF.Abs(clipped.Z) < StopEpsilon ? 0f : clipped.Z
        );
    }
}
