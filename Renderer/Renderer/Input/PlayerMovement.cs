
using System.Diagnostics;

namespace ValveResourceFormat.Renderer.Input;

/// <summary>
/// Source engine-style FPS player movement controller with collision detection, gravity, and crouch support.
/// </summary>
public class PlayerMovement
{
    // Player collision hull, stored as half-extents around the hull center (TracePosition).
    // Standing hull: 32x32x72, ducked hull: 32x32x48.
    private static readonly Vector3 StandingHullHalfExtents = new(16, 16, 36);
    private static readonly Vector3 DuckedHullHalfExtents = new(16, 16, 24);

    // Movement constants from Source engine (movevars_shared.cpp)
    private const float GravityValue = 800f;              // sv_gravity
    private const float FrictionValue = 5.2f;             // sv_friction
    private const float StopSpeedValue = 80f;             // sv_stopspeed
    private const float AccelerateValue = 5.5f;           // sv_accelerate
    private const float AirAccelerateValue = 150f;         // sv_airaccelerate
    //While sv_maxspeed is 320 in GO, the actual max speed is set by CS_PLAYER_SPEED_RUN, which is 260.
    private const float MaxSpeedValue = 260f;             // sv_maxspeed
    private const float JumpImpulseValue = 301.993377f;   // sv_jump_impulse = sqrt(2*800*57)
    private const float MaxVelocityValue = 3500f;         // sv_maxvelocity

    private const float WalkSpeedModifier = 0.52f;        // CS_PLAYER_SPEED_WALK_MODIFIER
    private const float DuckSpeedModifier = 0.34f;        // CS_PLAYER_SPEED_DUCK_MODIFIER

    // Moving up faster than this means airborne (NON_JUMP_VELOCITY). Keeps a fresh jump from
    // being re-grounded by the 2-unit ground probe on the very tick it leaves the floor.
    private const float NonJumpVelocity = 140f;
    private const float AirMaxWishSpeed = 30f;            // Air-control wishspeed cap (AirAccelerate)
    private const float WalkSpeedGraceMargin = 25f;       // Walk cap only applies when near walk speed, allowing natural deceleration

    private const float ViewHeightOffset = 8f;
    private static readonly float ViewHeightStanding = StandingHullHalfExtents.Z * 2f - ViewHeightOffset;
    private static readonly float ViewHeightDucked = DuckedHullHalfExtents.Z * 2f - ViewHeightOffset;

    // Bunnyhopping prevention (CS:GO)
    private const float BunnyjumpMaxSpeedFactor = 1.1f;   // Only allow bunny jumping up to 1.1x max speed

    // Collision constants
    private const float SurfaceEpsilon = 0.03125f;        // Keep-away margin (1/32 unit) applied inside TraceBBox; hits come back already backed off this far from surfaces
    private const float StepSize = 18f;                   // Maximum height of steps/obstacles player can climb
    private const float GroundProbeDistance = 2f;         // How far below the hull to look for ground contact
    private const float StepDownTolerance = 2f;           // A step may end at most this far below where it started

    // Crouch blend constants
    private const float CrouchBlendTime = 0.2f;           // Time to complete crouch/uncrouch animation (seconds)

    // Movement state
    /// <summary>Gets the current player velocity in world units per second.</summary>
    public Vector3 Velocity { get; private set; }
    private Vector3 TracePositionPrevious;
    private Vector3 TracePosition;
    private Vector3 TracePositionSmooth;
    private float AccumulatedTime;

    /// <summary>
    /// The desired update rate.
    /// </summary>
    public int TickRate { get; set; } = 64;

    /// <summary>
    /// Gets the current player position at feet level (where the AABB touches the ground).
    /// </summary>
    public Vector3 Position => TracePositionSmooth - new Vector3(0, 0, HullHalfExtents.Z); // Convert from hull center to feet position

    /// <summary>
    /// Gets a value indicating whether the player is currently on the ground.
    /// </summary>
    public bool OnGround { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the player was touching the ground in the previous frame.
    /// </summary>
    public bool WasOnGroundLastFrame { get; private set; }

    private bool RequestedJump;

    // Last tick-end position where the hull did not overlap geometry; used to recover
    // from stuck states instead of letting the player remain embedded
    private Vector3 LastValidPosition;
    private bool HasValidPosition;

    private bool HoldingCtrl => Input.Holding(TrackedKeys.Control);
    private bool HoldingShift => Input.Holding(TrackedKeys.Shift);

    private UserInput Input { get; }
    private Rubikon? Physics => Input.PhysicsWorld;

    /// <summary>
    /// Linear value from 0 to 1 representing how much the player is crouched.  0 = standing, 1 = fully crouched.
    /// </summary>
    public float CrouchBlend { get; private set; }

    /// <summary>
    /// Gets the current eye height blended between standing and crouched positions.
    /// </summary>
    public float BlendedEyeHeight { get; private set; }


    /// <summary>The current eye position</summary>
    public Vector3 EyePosition { get; private set; }

    private float DuckSpeedModifierSmooth => float.Lerp(1f, DuckSpeedModifier, CrouchBlend);
    private Vector3 SnappedHullHalfExtents => HoldingCtrl ? DuckedHullHalfExtents : StandingHullHalfExtents;

    private Vector3 HullHalfExtents => new(
        StandingHullHalfExtents.X,
        StandingHullHalfExtents.Y,
        float.Lerp(StandingHullHalfExtents.Z, DuckedHullHalfExtents.Z, CrouchBlend));

    private const float SurfaceFriction = 1.0f;
    private const float WalkableSlope = 0.7f; // ~45 degrees

    // options
    /// <summary>Gets or sets a value indicating whether the controller should reinitialize its position from the camera on the next tick.</summary>
    public bool Initialize { get; set; }
    /// <summary>Gets or sets a value indicating whether bunny-hopping is allowed by holding the jump key.</summary>
    public bool AutoBunnyHop { get; set; } = true;
    /// <summary>Gets or sets the base run speed in world units per second.</summary>
    public float RunSpeed { get; set; } = 250f;

    /// <summary>
    /// Initializes a new <see cref="PlayerMovement"/> bound to the given user input source.
    /// </summary>
    /// <param name="input">The user input instance used to read key state.</param>
    public PlayerMovement(UserInput input)
    {
        Input = input;
        Velocity = Vector3.Zero;
        OnGround = false;
        Initialize = false;
    }

    /// <summary>
    /// Reinitialize the character position from the current camera location.
    /// Call this when switching from noclip to FPS movement mode.
    /// </summary>
    public void ResetPosition(Camera camera)
    {
        TracePosition = camera.Location - Vector3.UnitZ * ViewHeightStanding + new Vector3(0, 0, StandingHullHalfExtents.Z);
        TracePositionPrevious = TracePosition;
        Velocity = Vector3.Zero;
        HasValidPosition = false; // Do not restore positions from before the reset
    }

    /// <summary>
    /// Main movement tick - processes input and updates position/velocity
    /// </summary>
    public void ProcessMovement(UserInput input, Camera camera, float deltaTime)
    {
        // Initialize character position from camera on first tick
        // We need to convert from eye height to feet position
        if (Initialize)
        {
            ResetPosition(camera);
            TryUnstuck(ref TracePosition, SnappedHullHalfExtents);
            Velocity = input.Velocity;
            Initialize = false;
        }

        RequestedJump = RequestedJump || input.Pressed(TrackedKeys.Space) || input.Holding(TrackedKeys.MouseWheelDown) || input.Holding(TrackedKeys.MouseWheelUp);

        var position = TracePosition;
        var yaw = camera.Yaw;

        AccumulatedTime += deltaTime;
        deltaTime = 1f / TickRate;

        AccumulatedTime = Math.Min(AccumulatedTime, deltaTime * 3f);

        int ticks = 0;
        for (; ticks + 1 < AccumulatedTime * TickRate; ticks++)
        {
            TickMovement(input, yaw, deltaTime, ref position);

            // Store the updated position and make SourcePosition the previous DestinationPosition
            TracePositionPrevious = TracePosition;
            TracePosition = position;
        }
        //Clear buffered jumps
        if (ticks != 0)
            RequestedJump = false;

        AccumulatedTime -= (float)ticks / TickRate;

        //Get interpolated position
        TracePositionSmooth = TracePositionPrevious + (TracePosition - TracePositionPrevious) * AccumulatedTime * TickRate;

        // Set camera at eye height with smooth crouch blend
        BlendedEyeHeight = ViewHeightStanding + (ViewHeightDucked - ViewHeightStanding) * CrouchBlend;
        EyePosition = Position + Vector3.UnitZ * BlendedEyeHeight;
        camera.Location = EyePosition;

        // Draw player AABB for debugging
        /*
        if (Physics?.SelectedNodeRenderer != null)
        {
            var worldAABB = playerHull.Translate(groundPos);
            var color = OnGround ? new Color32(0f, 1f, 0f, 1f) : new Color32(1f, 1f, 0f, 1f); // Green when grounded, yellow in air
            //ShapeSceneNode.AddBox(Physics.SelectedNodeRenderer.Vertices, worldAABB, color);
        }*/
    }

    /// <summary>
    /// Runs one fixed movement tick, mirroring Source's frame order:
    /// duck blend → categorize → start gravity → jump → wish velocity →
    /// friction/move → stay on ground → recategorize → finish gravity.
    /// </summary>
    private void TickMovement(UserInput input, float yaw, float deltaTime, ref Vector3 position)
    {
        // Track input state for acceleration modifiers and collision hull
        var isDucking = HoldingCtrl;
        var isWalking = !HoldingCtrl && HoldingShift;

        BlendDuckedHull(deltaTime, ref position, isDucking);

        var playerHull = HullHalfExtents;

        WasOnGroundLastFrame = OnGround;

        // Categorize position (check if on ground) - use lerped hull for collision
        CategorizePosition(ref position, playerHull);

        // Check if we just landed this frame
        var justLanded = !WasOnGroundLastFrame && OnGround;

        // StartGravity - add gravity at start of frame (like Source does)
        if (!OnGround)
        {
            ApplyHalfGravity(deltaTime);
            CheckVelocity(ref position); // StartGravity calls CheckVelocity in Source
        }

        // Check for jump (auto bunny hop if enabled and holding jump)
        var wantsToJump = AutoBunnyHop ? input.Holding(TrackedKeys.Space) : false;
        wantsToJump = wantsToJump || RequestedJump;

        // For auto bhop, also jump immediately when landing while holding jump
        if (wantsToJump && (OnGround || (AutoBunnyHop && justLanded)))
        {
            // Prevent bunnyhopping - cap speed before jumping (only if auto bhop is disabled)
            if (!AutoBunnyHop)
            {
                PreventBunnyJumping();
            }
            CheckJump(deltaTime);
        }

        // Calculate wish velocity from input (with speed modifiers for duck/crouch)
        var (wishdir, wishspeed) = CalculateWishVelocity(input, yaw);

        // Apply walk speed modifier only when near walk speed (CS:GO behavior)
        // This allows natural deceleration instead of instant capping
        if (isWalking)
        {
            var currentSpeed = Velocity.Length();
            var walkSpeed = MaxSpeedValue * WalkSpeedModifier;
            if (currentSpeed < walkSpeed + WalkSpeedGraceMargin)
            {
                wishspeed = MathF.Min(wishspeed, walkSpeed);
            }
        }

        // Ground or air movement - use isDucking (input) for speed modifiers
        if (OnGround)
        {
            // Apply friction before movement
            ZeroVerticalVelocity();
            Friction(deltaTime);
            CheckVelocity(ref position);
            WalkMove(wishdir, wishspeed, deltaTime, isDucking, isWalking);
            CheckVelocity(ref position);
        }
        else
        {
            AirMove(wishdir, wishspeed, deltaTime);
        }

        // Check velocity for NaN/bounds
        CheckVelocity(ref position);

        // Update position based on velocity - use lerped hull for collision.
        // Ground movement gets step support; in the air there is nothing to step off.
        position = OnGround
            ? StepMove(position, Velocity * deltaTime, playerHull)
            : TryPlayerMove(position, Velocity * deltaTime, playerHull);

        // StayOnGround - keep player stuck to ground when going down slopes/stairs
        if (OnGround)
        {
            StayOnGround(ref position, playerHull);
        }

        // Recategorize position after movement (now that position is updated)
        CategorizePosition(ref position, playerHull);

        // Check velocity again for NaN/bounds
        CheckVelocity(ref position);

        // FinishGravity - add remaining gravity at end of frame
        if (!OnGround)
        {
            ApplyHalfGravity(deltaTime);
            CheckVelocity(ref position); // FinishGravity calls CheckVelocity in Source
        }

        CheckStuck(ref position, playerHull);
    }

    /// <summary>
    /// Stuck prevention, like Source's CheckStuck: float error, blind clamps, and hull resizes
    /// can all leave the hull embedded in geometry despite trace-validated movement. Rather than
    /// closing every hole individually, remember each tick-end position that is clear and
    /// restore it the moment a tick ends inside something.
    /// </summary>
    private void CheckStuck(ref Vector3 position, Vector3 halfExtents)
    {
        if (Physics == null)
        {
            return;
        }

        if (!IsStuck(position, halfExtents))
        {
            LastValidPosition = position;
            HasValidPosition = true;
        }
        else if (HasValidPosition)
        {
            position = LastValidPosition;
            Velocity = Vector3.Zero;
        }
    }

    /// <summary>
    /// Applies half a tick of gravity. Source integrates gravity in two half-steps
    /// (StartGravity/FinishGravity) around the move for a more accurate leapfrog integration.
    /// </summary>
    private void ApplyHalfGravity(float deltaTime)
    {
        Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z - GravityValue * deltaTime * 0.5f);
    }

    /// <summary>
    /// Zeroes the vertical velocity component, as ground movement operates purely horizontally.
    /// </summary>
    private void ZeroVerticalVelocity()
    {
        Velocity = new Vector3(Velocity.X, Velocity.Y, 0);
    }

    static readonly float crouchHeightDifference = (StandingHullHalfExtents.Z - DuckedHullHalfExtents.Z) * 2f;

    private void BlendDuckedHull(float deltaTime, ref Vector3 position, bool isDucking)
    {
        var crouchDelta = isDucking ? 1f : -1f;
        crouchDelta = crouchDelta / CrouchBlendTime * deltaTime;

        var goalCrouch = MathUtils.Saturate(CrouchBlend + crouchDelta);
        crouchDelta = goalCrouch - CrouchBlend;

        // Which end of the hull stays put as it resizes:
        // On ground, keep feet at same level (the hull grows upward).
        // In air, keep head at same level (the hull grows downward).
        var feetAnchored = OnGround;

        // Handle crouch/uncrouch transitions
        if (crouchDelta != 0f)
        {
            // uncrouching
            if (crouchDelta < 0f && Physics != null)
            {
                // The hull expands away from the anchored end, so that is the direction that needs
                // to be clear. This used to always check upward, so an airborne uncrouch grew the
                // hull downward unchecked and dug the player into whatever they were riding.
                if (UncrouchBlocked(position, feetAnchored))
                {
                    // Head-anchored uncrouch is blocked (e.g. surfing a ramp). If there is room
                    // above, anchor the feet instead so the player can still stand up.
                    if (!feetAnchored && !UncrouchBlocked(position, feetAnchored: true))
                    {
                        feetAnchored = true;
                    }
                    else
                    {
                        crouchDelta = 0f;
                    }
                }
            }

            // Move the hull center so the anchored end stays in place
            var bboxPositionDelta = crouchHeightDifference * crouchDelta / 2f;
            bboxPositionDelta *= feetAnchored ? -1f : 1f;
            position += new Vector3(0, 0, bboxPositionDelta);
        }

        CrouchBlend = MathUtils.Saturate(CrouchBlend + crouchDelta);
    }

    /// <summary>
    /// Tests whether a full uncrouch is obstructed. The standing hull occupies exactly the ducked
    /// hull swept <see cref="crouchHeightDifference"/> away from the anchored end, so sweeping the
    /// ducked hull in that direction covers precisely the space the player needs to be clear.
    /// </summary>
    private bool UncrouchBlocked(Vector3 position, bool feetAnchored)
    {
        var growth = new Vector3(0, 0, feetAnchored ? crouchHeightDifference : -crouchHeightDifference);
        return TraceBBox(position, position + growth, DuckedHullHalfExtents).Hit;
    }

    /// <summary>
    /// Keep player stuck to ground when moving down slopes/stairs
    /// Prevents player from becoming airborne and losing friction
    /// Ported from gamemovement.cpp StayOnGround()
    /// </summary>
    private void StayOnGround(ref Vector3 position, Vector3 halfExtents)
    {
        if (Physics == null)
        {
            return;
        }

        // Trace down to find ground (trace extra distance to catch steep slopes)
        var trace = TraceBBox(position, position + new Vector3(0, 0, -StepSize), halfExtents);

        if (IsWalkableGroundHit(trace))
        {
            // Ground-following only ever snaps down, so it must never lift the player
            SnapToGround(ref position, trace, snapDownOnly: true);
        }
    }

    /// <summary>
    /// Whether a trace landed on a surface flat enough to stand on.
    /// </summary>
    private static bool IsWalkableGroundHit(Rubikon.TraceResult trace)
        => trace.Hit && trace.HitNormal.Z > WalkableSlope;

    /// <summary>
    /// Snaps the position's Z onto the ground found by <paramref name="trace"/>, keeping a
    /// SurfaceEpsilon perpendicular gap. XY is preserved so slopes do not induce sliding.
    /// </summary>
    /// <param name="position">The hull center position to adjust.</param>
    /// <param name="trace">The downward trace whose hit describes the ground.</param>
    /// <param name="snapDownOnly">
    /// When set, the snap may only lower the player. Use it for pure ground-following, which must
    /// never lift; leave it clear where the snap also has to push the hull out of a shallow
    /// penetration up onto the surface.
    /// </param>
    private static void SnapToGround(ref Vector3 position, Rubikon.TraceResult trace, bool snapDownOnly)
    {
        // A zero-distance hit means we are already at (or within the keep-away margin of) the
        // surface, so HitPosition is just traceStart echoed back and carries no ground
        // information - snapping to it would climb indefinitely. Being in margin contact also
        // means there is nothing to snap. Source guards the same way: StayOnGround requires
        // fraction > 0 && !startsolid.
        if (trace.IsMinimalDistance)
        {
            return;
        }

        // The trace's keep-away margin already leaves HitPosition clear of the surface
        var groundZ = trace.HitPosition.Z;

        if (snapDownOnly)
        {
            groundZ = MathF.Min(position.Z, groundZ);
        }

        position = new Vector3(position.X, position.Y, groundZ);
    }

    /// <summary>
    /// Check if player is on ground using swept AABB trace
    /// Traces down a small fixed distance (2 units) to detect ground contact; only accepts the hit as ground when the surface is walkable and downward velocity is below a threshold
    /// </summary>
    private void CategorizePosition(ref Vector3 position, Vector3 halfExtents)
    {
        if (Physics == null)
        {
            // Fallback: simple ground plane at Z=0
            OnGround = position.Z <= 0.1f;
            return;
        }

        // Trace down from current position to check if we're on or very close to ground
        var result = TraceBBox(position, position + new Vector3(0, 0, -GroundProbeDistance), halfExtents);

        OnGround = IsWalkableGroundHit(result) && Velocity.Z < NonJumpVelocity;

        if (OnGround)
        {
            SnapToGround(ref position, result, snapDownOnly: false);
        }
    }

    /// <summary>
    /// Attempt to find a valid position if stuck inside geometry.
    /// Uses the trace's start-solid overlap test to detect being stuck and to
    /// validate candidate positions directly.
    /// </summary>
    private bool TryUnstuck(ref Vector3 position, Vector3 halfExtents)
    {
        if (Physics == null)
        {
            return false;
        }

        if (!IsStuck(position, halfExtents))
        {
            return true; // Not stuck
        }

        // No downward candidate: unstucking below the geometry we are wedged in
        // would leave the player falling forever
        Span<Vector3> directions = stackalloc[]
        {
            Vector3.UnitZ,        // Up
            Vector3.UnitX,        // Right
            -Vector3.UnitX,       // Left
            Vector3.UnitY,        // Forward
            -Vector3.UnitY,       // Back
            Vector3.Normalize(new Vector3(1, 1, 0)),   // Diagonal
            Vector3.Normalize(new Vector3(-1, 1, 0)),
            Vector3.Normalize(new Vector3(1, -1, 0)),
            Vector3.Normalize(new Vector3(-1, -1, 0)),
        };

        // Try increasingly larger offsets
        for (var distance = 1f; distance <= 100f; distance += 10f)
        {
            foreach (var dir in directions)
            {
                var testPos = position + dir * distance;

                if (!IsStuck(testPos, halfExtents))
                {
                    // Found a valid position
                    position = testPos;
                    Velocity = Vector3.Zero; // Reset velocity when unstucking
                    return true;
                }
            }
        }

        return false; // Couldn't find a valid position
    }

    /// <summary>
    /// Check whether the hull overlaps solid geometry at the given position.
    /// </summary>
    private bool IsStuck(Vector3 position, Vector3 halfExtents)
    {
        // Start-solid is evaluated at the start position; the probe direction is irrelevant
        var probe = TraceBBox(position, position + new Vector3(0, 0, -0.1f), halfExtents, detectStartSolid: true);
        return probe.StartSolid;
    }

    /// <summary>
    /// Perform swept AABB collision detection for player movement with multi-bounce sliding
    /// </summary>
    private Vector3 TryPlayerMove(Vector3 start, Vector3 delta, Vector3 halfExtents)
    {
        if (Physics == null || delta.LengthSquared() < 1e-6f)
        {
            return start + delta;
        }

        const int MaxBumps = 6;

        var position = start;
        var remainingDelta = delta;
        var remainingDistance = delta.Length();

        // Fraction of the original move not yet consumed; the loop ends once it is all used up
        var remainingFraction = 1.0f;

        // Velocity at entry; if clipping ever turns the velocity against it, we are ping-ponging
        // in a corner and should stop dead instead (Source TryPlayerMove does the same)
        var entryVelocity = Velocity;

        // Planes hit without making progress; movement must be clipped to parallel all of them
        Span<Vector3> planes = stackalloc Vector3[MaxBumps];
        var planeCount = 0;

        for (var bump = 0; bump < MaxBumps && remainingFraction > 0; bump++)
        {
            var result = TraceBBox(position, position + remainingDelta, halfExtents);

            if (!result.Hit)
            {
                return position + remainingDelta;
            }

            // Advance to the hit point (the trace already backs hits off by the keep-away margin)
            var fraction = result.Distance / remainingDistance;

            position += remainingDelta * fraction;
            remainingFraction *= 1f - fraction;

            if (fraction > 0)
            {
                // Made progress, so the accumulated planes no longer box us in
                planeCount = 0;
            }

            planes[planeCount++] = result.HitNormal;

            if (!ClipToPlanes(planes[..planeCount], ref remainingDelta, out var velocity)
                || Vector3.Dot(velocity, entryVelocity) <= 0)
            {
                // Trapped by three or more planes, or clipping reversed the move
                Velocity = Vector3.Zero;
                break;
            }

            Velocity = velocity;

            CheckVelocity(ref position);

            remainingDistance = remainingDelta.Length();
            if (remainingDistance <= SurfaceEpsilon)
            {
                // Too little movement left to matter
                break;
            }
        }

        return position;
    }

    /// <summary>
    /// Clips the movement so it parallels every accumulated plane, as in Source's TryPlayerMove:
    /// prefer a single-plane clip that does not push into any other plane; against exactly two
    /// planes, slide along their crease. Returns false when no direction satisfies all planes
    /// (three or more planes trap the movement).
    /// </summary>
    private bool ClipToPlanes(ReadOnlySpan<Vector3> planes, ref Vector3 delta, out Vector3 velocity)
    {
        // Try clipping against each plane alone, accepting the first result that does not
        // move into any of the other planes
        for (var i = 0; i < planes.Length; i++)
        {
            var candidateVelocity = Velocity;
            var candidateDelta = delta;
            ClipVelocity(ref candidateDelta, ref candidateVelocity, planes[i], OnGround);

            var valid = true;
            for (var j = 0; j < planes.Length; j++)
            {
                if (j != i && Vector3.Dot(candidateVelocity, planes[j]) < 0)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                delta = candidateDelta;
                velocity = candidateVelocity;
                return true;
            }
        }

        // No single-plane clip satisfies all planes; two planes form a crease we can slide along
        if (planes.Length == 2)
        {
            velocity = Velocity;
            ClipToCrease(ref delta, ref velocity, planes[0], planes[1]);
            return true;
        }

        velocity = Vector3.Zero;
        delta = Vector3.Zero;
        return false;
    }

    /// <summary>
    /// Constrains movement to the crease line between two planes (their cross product),
    /// killing all velocity perpendicular to it. Used when the player is wedged into
    /// a corner formed by two opposing planes.
    /// </summary>
    private static void ClipToCrease(ref Vector3 delta, ref Vector3 velocity, Vector3 plane1, Vector3 plane2)
    {
        var crease = Vector3.Normalize(Vector3.Cross(plane1, plane2));
        velocity = Vector3.Dot(crease, velocity) * crease;
        delta = Vector3.Dot(crease, delta) * crease;
    }

    /// <summary>
    /// Ground move with step support, like Source's StepMove: run the slide normally and again
    /// from a stepped-up position, then keep whichever branch traveled farther laterally.
    /// </summary>
    private Vector3 StepMove(Vector3 start, Vector3 delta, Vector3 halfExtents)
    {
        if (Physics == null)
        {
            return TryPlayerMove(start, delta, halfExtents);
        }

        var entryVelocity = Velocity;

        // Consult the step whenever the direct path is obstructed at all. Source checks the
        // first trace the same way; testing the slide's net displacement instead misses grazing
        // hits that end within epsilon of the destination while still clipping the velocity,
        // which reads as sliding along low angled blocks instead of stepping onto them.
        if (!TraceBBox(start, start + delta, halfExtents).Hit)
        {
            return start + delta;
        }

        // Branch 1: plain slide along the ground
        var downPosition = TryPlayerMove(start, delta, halfExtents);
        var downVelocity = Velocity;

        // Branch 2: step up as far as headroom allows, slide at that height, then settle back down
        Velocity = entryVelocity;

        var stepUpEnd = start + new Vector3(0, 0, StepSize);
        var upTrace = TraceBBox(start, stepUpEnd, halfExtents);
        var steppedStart = upTrace.Hit ? upTrace.HitPosition : stepUpEnd;

        var steppedSlidePosition = TryPlayerMove(steppedStart, delta, halfExtents);

        var downEnd = steppedSlidePosition + new Vector3(0, 0, -(StepSize + GroundProbeDistance));
        var downTrace = TraceBBox(steppedSlidePosition, downEnd, halfExtents);

        // The stepped branch must settle on standable ground.
        // A zero-distance landing means the hull is already embedded at the stepped position
        // (e.g. wedged in a floor-ceiling gap, where the up-step was blocked at zero height):
        // HitPosition is just the input echoed back and carries no ground information.
        // The down tolerance keeps a low ceiling from turning a "step" into a ledge drop.
        var landingInvalid = !downTrace.Hit
            || downTrace.IsMinimalDistance
            || downTrace.HitNormal.Z < WalkableSlope
            || downTrace.HitPosition.Z - start.Z < -StepDownTolerance;

        if (landingInvalid)
        {
            Velocity = downVelocity;
            return downPosition;
        }

        var steppedPosition = downTrace.HitPosition;

        // Keep whichever branch went farther laterally - vertical gain is not progress toward
        // where the player is steering (Source compares fLateralDist the same way)
        var downLateral = new Vector2(downPosition.X - start.X, downPosition.Y - start.Y).LengthSquared();
        var steppedLateral = new Vector2(steppedPosition.X - start.X, steppedPosition.Y - start.Y).LengthSquared();

        if (downLateral > steppedLateral)
        {
            Velocity = downVelocity;
            return downPosition;
        }

        // The stepped branch keeps its own horizontal velocity, but the vertical velocity comes
        // from the ground branch so stepping does not manufacture upward speed (as in Source)
        Velocity = new Vector3(Velocity.X, Velocity.Y, downVelocity.Z);
        return steppedPosition;
    }

    /// <summary>
    /// Clips velocity against a surface normal. Special handling for walkable slopes.
    /// </summary>
    private static void ClipVelocity(ref Vector3 delta, ref Vector3 velocity, Vector3 normal, bool onGround)
    {
        // Special handling for walkable slopes when on ground - maintain horizontal speed
        if (onGround && normal.Z > WalkableSlope)
        {
            var horizontalVel = new Vector3(velocity.X, velocity.Y, 0);
            var horizontalSpeed = horizontalVel.Length();

            if (horizontalSpeed > 0.001f)
            {
                // Project horizontal direction onto slope while maintaining speed
                var horizontalDir = horizontalVel / horizontalSpeed;
                var projectedDir = horizontalDir - normal * Vector3.Dot(horizontalDir, normal);

                if (projectedDir.LengthSquared() > 0.001f)
                {
                    velocity = Vector3.Normalize(projectedDir) * horizontalSpeed;
                }
            }

            // Same for delta
            var horizontalDelta = new Vector3(delta.X, delta.Y, 0);
            var deltaLength = horizontalDelta.Length();

            if (deltaLength > 0.001f)
            {
                var deltaDir = horizontalDelta / deltaLength;
                var projectedDeltaDir = deltaDir - normal * Vector3.Dot(deltaDir, normal);

                if (projectedDeltaDir.LengthSquared() > 0.001f)
                {
                    delta = Vector3.Normalize(projectedDeltaDir) * deltaLength;
                }
            }
        }
        else
        {
            // Standard clipping for walls and ceilings
            delta -= normal * Vector3.Dot(delta, normal);
            velocity -= normal * Vector3.Dot(velocity, normal);
        }
    }

    /// <summary>
    /// Prevent excessive speed gain from bunnyhopping
    /// Ported from cs_gamemovement.cpp PreventBunnyJumping()
    /// </summary>
    private void PreventBunnyJumping()
    {
        // Speed at which bunny jumping is limited
        var maxscaledspeed = BunnyjumpMaxSpeedFactor * MaxSpeedValue;
        if (maxscaledspeed <= 0.0f)
        {
            return;
        }

        // Current player speed
        var spd = Velocity.Length();

        if (spd <= maxscaledspeed)
        {
            return;
        }

        // Apply this cropping fraction to velocity
        var fraction = maxscaledspeed / spd;

        Velocity *= fraction;
    }

    /// <summary>
    /// Handle jump input - applies upward impulse
    /// Ported from cs_gamemovement.cpp CheckJumpButton()
    /// </summary>
    private void CheckJump(float deltaTime)
    {
        // In the air now (SetGroundEntity NULL in Source)
        OnGround = false;

        // Accelerate upward
        // v = sqrt( g * 2.0 * jumpheight )
        Velocity = new Vector3(Velocity.X, Velocity.Y, JumpImpulseValue);

        // FinishGravity is called after jump in Source
        ApplyHalfGravity(deltaTime);
    }

    /// <summary>
    /// Calculate desired movement direction and speed from input
    /// </summary>
    private (Vector3 wishdir, float wishspeed) CalculateWishVelocity(UserInput input, float yaw)
    {
        // Calculate forward and right vectors from yaw (ignore pitch for horizontal movement)
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
        var right = new Vector3(MathF.Cos(yaw - MathF.PI / 2f), MathF.Sin(yaw - MathF.PI / 2f), 0);

        // Determine movement amounts
        float forwardMove = 0, sideMove = 0;

        if (input.Holding(TrackedKeys.W))
        {
            forwardMove += MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.S))
        {
            forwardMove -= MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.D))
        {
            sideMove += MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.A))
        {
            sideMove -= MaxSpeedValue;
        }

        // Build wish velocity
        var wishvel = forward * forwardMove + right * sideMove;
        wishvel = new Vector3(wishvel.X, wishvel.Y, 0); // Zero out Z

        var wishspeed = wishvel.Length();
        var wishdir = wishspeed > 0 ? Vector3.Normalize(wishvel) : Vector3.Zero;

        // Clamp to max speed
        wishspeed = MathF.Min(wishspeed, MaxSpeedValue);

        // Apply duck/crouch speed modifier (from CS:GO cs_gamemovement.cpp)
        wishspeed *= DuckSpeedModifierSmooth;

        return (wishdir, wishspeed);
    }

    /// <summary>
    /// Apply ground friction to slow down the player
    /// </summary>
    private void Friction(float deltaTime)
    {
        var speed = Velocity.Length();
        if (speed < 0.1f)
        {
            return;
        }

        var control = speed < StopSpeedValue ? StopSpeedValue : speed;
        var drop = control * FrictionValue * SurfaceFriction * deltaTime;
        var newSpeed = Math.Max(0, speed - drop);

        if (newSpeed != speed)
        {
            Velocity *= newSpeed / speed;
        }
    }

    /// <summary>
    /// Accelerate in desired direction using CS:GO acceleration
    /// </summary>
    private void Accelerate(Vector3 wishdir, float wishspeed, float accel, float deltaTime, bool isDucking, bool isWalking)
    {
        var currentspeed = Vector3.Dot(Velocity, wishdir);
        var addspeed = wishspeed - currentspeed;

        if (addspeed <= 0)
        {
            return;
        }

        currentspeed = Math.Max(0, currentspeed);

        // CS:GO acceleration scaling: acceleration ramps against a goal speed of at least 250,
        // reduced by the duck/walk modifiers
        var goalSpeed = MathF.Max(250.0f, wishspeed);

        if (isDucking)
        {
            goalSpeed *= DuckSpeedModifierSmooth;
        }
        if (isWalking)
        {
            goalSpeed *= WalkSpeedModifier;
        }

        // Walk speed gradient clamping - gradually reduce acceleration near goal speed
        var finalAccel = accel;
        if (isWalking && currentspeed > goalSpeed - 5)
        {
            var ratio = Math.Max(0.0f, currentspeed - (goalSpeed - 5)) / 5.0f;
            finalAccel *= Math.Clamp(1.0f - ratio, 0.0f, 1.0f);
        }

        var accelspeed = Math.Min(finalAccel * deltaTime * goalSpeed * SurfaceFriction, addspeed);
        Velocity += accelspeed * wishdir;
    }

    /// <summary>
    /// Ground movement with friction and acceleration
    /// </summary>
    private void WalkMove(Vector3 wishdir, float wishspeed, float deltaTime, bool isDucking, bool isWalking)
    {
        ZeroVerticalVelocity();
        Accelerate(wishdir, wishspeed, AccelerateValue, deltaTime, isDucking, isWalking);
        ZeroVerticalVelocity();

        // Clamp to effective max speed
        var effectiveMaxSpeed = RunSpeed * DuckSpeedModifierSmooth;
        if (isWalking)
        {
            effectiveMaxSpeed *= WalkSpeedModifier;
        }

        if (Velocity.LengthSquared() > effectiveMaxSpeed * effectiveMaxSpeed)
        {
            Velocity *= effectiveMaxSpeed / Velocity.Length();
        }
    }

    /// <summary>
    /// Air movement - air acceleration works differently from ground acceleration
    /// </summary>
    private void AirMove(Vector3 wishdir, float wishspeed, float deltaTime)
    {
        var wishspd = Math.Min(wishspeed, AirMaxWishSpeed);
        var currentspeed = Vector3.Dot(Velocity, wishdir);
        var addspeed = wishspd - currentspeed;

        if (addspeed <= 0)
        {
            return;
        }

        // Note: uses original wishspeed, NOT the capped wishspd
        var accelspeed = Math.Min(AirAccelerateValue * wishspeed * deltaTime * SurfaceFriction, addspeed);
        Velocity += accelspeed * wishdir;
    }

    /// <summary>
    /// Check and clamp velocity - prevents NaN and enforces max velocity
    /// </summary>
    private void CheckVelocity(ref Vector3 position)
    {
        position.X = float.IsNaN(position.X) ? TracePosition.X : position.X;
        position.Y = float.IsNaN(position.Y) ? TracePosition.Y : position.Y;
        position.Z = float.IsNaN(position.Z) ? TracePosition.Z : position.Z;

        var velocityBounds = new AABB(Vector3.Zero, MaxVelocityValue);
        Velocity = Vector3.Clamp(Velocity, velocityBounds.Min, velocityBounds.Max);

        // max bounds
        var movementBounds = new AABB(Vector3.Zero, 16_000f);
        position = Vector3.Clamp(position, movementBounds.Min, movementBounds.Max);

        // sanity check, compare against last position
        movementBounds = new AABB(TracePosition, MathF.Max(StepSize * 2f, Velocity.Length()));
        position = Vector3.Clamp(position, movementBounds.Min, movementBounds.Max);
    }

    private Rubikon.TraceResult TraceBBox(Vector3 from, Vector3 to, Vector3 halfExtents, bool detectStartSolid = false)
    {
        Debug.Assert(Physics != null, "Physics world must be initialized");

        var result = Physics.TraceAABB(from, to, halfExtents, "player", detectStartSolid);

        // Keep-away margin: back the hit off along the sweep so the perpendicular gap to the
        // surface is SurfaceEpsilon. Centralized here so every caller can use HitPosition
        // directly and no per-call-site epsilon math can go wrong. A grazing (or back-facing)
        // hit gets a bounded pullback via the clamp to the traveled distance, never an
        // unbounded (or, via infinity * 0, NaN) offset.
        if (result.Hit && !result.StartSolid)
        {
            var direction = Vector3.Normalize(to - from);
            var approach = Vector3.Dot(-direction, result.HitNormal);
            var backoff = MathF.Max(SurfaceEpsilon / approach, 0f);
            backoff = MathF.Min(backoff, result.Distance);

            result.Distance -= backoff;
            result.HitPosition -= direction * backoff;
        }

        return result;
    }
}
