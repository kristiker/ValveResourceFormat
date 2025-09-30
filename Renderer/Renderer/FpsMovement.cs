
namespace ValveResourceFormat.Renderer;

/// <summary>
/// FPS-style movement physics ported from CS:GO source (cs_gamemovement.cpp + gamemovement.cpp)
/// Implements MOVETYPE_WALK behavior with CS:GO-accurate movement mechanics
///
/// IMPLEMENTED FROM CS:GO:
/// - Split gravity (StartGravity/FinishGravity) for proper integration
/// - Ground friction and acceleration with CS:GO scaling (MAX(250, wishspeed))
/// - Air acceleration with 30 unit/sec cap for air control
/// - Bunnyhopping prevention (1.1x max speed cap before jumping)
/// - Jumping with proper impulse (sqrt(2*gravity*jumpheight))
/// - Walk speed modifier with CS:GO conditional application (only when near walk speed)
/// - Duck/crouch speed modifier (34% speed, CS:GO)
/// - Velocity checking (NaN protection) and clamping
/// - WalkMove speed clamping to prevent turning acceleration
/// - Swept AABB collision detection using Rubikon physics
///
/// NOT IMPLEMENTED (intentionally simplified):
/// - Water movement/swimming
/// - Weapon speed modifiers (AWP scoped speed, etc.)
/// - Stamina system (jump height/speed penalties when tired)
/// - Duck spam penalties and duck speed tracking
/// - Full duck mechanics (view height changes, collision hull shrinking)
/// - StayOnGround() for slopes/stairs
/// - Trailing velocity tracking for accuracy fishtailing
/// </summary>
public class FpsMovement
{
    // Player collision hull (adjusted from CS:GO values for better scale)
    // Standing hull: 32x32x64 (16 units radius, 64 units tall - about 5'4" at 1 unit = 1 inch)
    // Ducked hull: 32x32x48 (16 units radius, 48 units tall - 4 feet crouched)
    // Note: Hull is centered horizontally but extends from feet (Z=0) upward
    private static readonly AABB PlayerHullStanding = new(new Vector3(-8, -8, 0), new Vector3(16, 16, 72));
    private static readonly AABB PlayerHullDucked = new(new Vector3(-16, -16, 0), new Vector3(16, 16, 48));

    // Movement constants from Source engine (movevars_shared.cpp)
    private const float GravityValue = 800f;              // sv_gravity
    private const float FrictionValue = 5.2f;             // sv_friction
    private const float StopSpeedValue = 80f;             // sv_stopspeed
    private const float AccelerateValue = 5.5f;           // sv_accelerate
    private const float AirAccelerateValue = 12f;         // sv_airaccelerate
    private const float MaxSpeedValue = 320f;             // sv_maxspeed
    private const float JumpImpulseValue = 301.993377f;   // sv_jump_impulse = sqrt(2*800*57)
    private const float MaxVelocityValue = 3500f;         // sv_maxvelocity

    // Speed modifiers from CS:GO (cs_shareddefs.cpp)
    private const float WalkSpeedModifier = 0.52f;        // CS_PLAYER_SPEED_WALK_MODIFIER
    private const float DuckSpeedModifier = 0.34f;        // CS_PLAYER_SPEED_DUCK_MODIFIER

    private const float ViewHeightOffset = 8f;
    private static readonly float ViewHeightStanding = PlayerHullStanding.Size.Z - ViewHeightOffset;
    private static readonly float ViewHeightDucked = PlayerHullDucked.Size.Z - ViewHeightOffset;

    // Bunnyhopping prevention (CS:GO)
    private const float BunnyjumpMaxSpeedFactor = 1.1f;   // Only allow bunny jumping up to 1.1x max speed

    // Collision constants
    private const float SurfaceEpsilon = 0.03125f;        // Minimum distance from surfaces (1/32 unit) to prevent getting stuck

    // Movement state
    public Vector3 Velocity { get; private set; }
    private Vector3 AABBCenteredPosition;
    private bool OnGround;
    private bool WasOnGroundLastFrame;
    private bool WasDuckingLastFrame;
    private Rubikon? Physics;
    private bool IsInitialized;

    // Surface properties (simplified - always 1.0 for now)
    private const float SurfaceFriction = 1.0f;

    // Auto bunny hop setting
    public bool AutoBunnyHop { get; set; } = true;

    public FpsMovement()
    {
        Velocity = Vector3.Zero;
        OnGround = false;
        IsInitialized = false;
    }

    public void SetPhysics(Rubikon physics)
    {
        Physics = physics;
    }

    /// <summary>
    /// Reinitialize the character position from the current camera location.
    /// Call this when switching from noclip to FPS movement mode.
    /// </summary>
    public void ResetPosition(Camera camera, bool isDucking)
    {
        var hull = isDucking ? PlayerHullDucked : PlayerHullStanding;
        AABBCenteredPosition = camera.Location - new Vector3(0, 0, hull.Size.Z / 2);
        Velocity = Vector3.Zero;
        IsInitialized = true;
    }

    /// <summary>
    /// Main movement tick - processes input and updates position/velocity
    /// </summary>
    public void ProcessMovement(UserInput input, Camera camera, float deltaTime)
    {
        // Initialize character position from camera on first tick
        // We need to convert from eye height to feet position
        if (!IsInitialized)
        {
            ResetPosition(camera, input.Holding(TrackedKeys.Control));
        }

        var position = AABBCenteredPosition; // Use character's feet position for physics
        var pitch = camera.Pitch;
        var yaw = camera.Yaw;

        // Track input state for acceleration modifiers and collision hull
        var isDucking = input.Holding(TrackedKeys.Control);
        var isWalking = !isDucking && input.Holding(TrackedKeys.Shift);

        // Handle crouch/uncrouch transitions
        if (WasDuckingLastFrame && !isDucking)
        {
            // Trying to uncrouch - check if there's space above us
            var standingHull = PlayerHullStanding;
            var duckedHull = PlayerHullDucked;
            var heightDifference = standingHull.Size.Z - duckedHull.Size.Z; // 72 - 48 = 24 units

            // Trace upward using a small vertical hull to check clearance above the ducked hull
            // We trace from the current ducked position upward by the height difference
            var traceStart = AABBCenteredPosition;
            var traceEnd = AABBCenteredPosition + new Vector3(0, 0, heightDifference);

            // Use the ducked hull's horizontal dimensions but check vertical clearance
            var checkHull = new AABB(duckedHull.Min, duckedHull.Max);

            bool canUncrouch = true;
            if (Physics != null)
            {
                var trace = Physics.TraceAABB(traceStart, traceEnd, checkHull);
                canUncrouch = !trace.Hit;
            }

            if (canUncrouch)
            {
                // We have space to stand up - adjust position upward to keep feet at same level
                // The AABB center needs to move up by half the height difference
                AABBCenteredPosition += new Vector3(0, 0, heightDifference / 2);
                position = AABBCenteredPosition;
            }
            else
            {
                // Can't uncrouch - blocked by geometry above, stay ducked
                isDucking = true;
            }
        }

        WasDuckingLastFrame = isDucking;

        // Store previous ground state
        WasOnGroundLastFrame = OnGround;

        // Categorize position (check if on ground)
        CategorizePosition(ref position, isDucking);

        // Check if we just landed this frame
        bool justLanded = !WasOnGroundLastFrame && OnGround;

        // StartGravity - add gravity at start of frame (like Source does)
        if (!OnGround)
        {
            Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z - GravityValue * deltaTime * 0.5f);
            CheckVelocity(ref position); // StartGravity calls CheckVelocity in Source
        }

        // Check for jump (auto bunny hop if enabled and holding jump)
        bool wantsToJump = AutoBunnyHop ? input.Holding(TrackedKeys.Jump) : input.Pressed(TrackedKeys.Jump);

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
        var (wishdir, wishspeed) = CalculateWishVelocity(input, pitch, yaw);

        // Apply walk speed modifier only when near walk speed (CS:GO behavior)
        // This allows natural deceleration instead of instant capping
        if (isWalking)
        {
            var currentSpeed = Velocity.Length();
            var walkSpeed = MaxSpeedValue * WalkSpeedModifier;
            if (currentSpeed < walkSpeed + 25.0f)
            {
                wishspeed = MathF.Min(wishspeed, walkSpeed);
            }
        }

        // Ground or air movement
        if (OnGround)
        {
            // Apply friction before movement
            Velocity = new Vector3(Velocity.X, Velocity.Y, 0);
            Friction(deltaTime);
            WalkMove(wishdir, wishspeed, deltaTime, isDucking, isWalking);
        }
        else
        {
            AirMove(wishdir, wishspeed, deltaTime);
        }

        // Check velocity for NaN/bounds
        CheckVelocity(ref position);
        Vector3 prevPosition = position;
        // Update position based on velocity (in Source this happens inside TryPlayerMove)
        position = TryPlayerMove2(position, Velocity * deltaTime, isDucking);

        // Recategorize position after movement (now that position is updated)
        CategorizePosition(ref position, isDucking);

        // Check velocity again for NaN/bounds
        CheckVelocity(ref position);

        // FinishGravity - add remaining gravity at end of frame
        if (!OnGround)
        {
            Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z - GravityValue * deltaTime * 0.5f);
            CheckVelocity(ref position); // FinishGravity calls CheckVelocity in Source
        }

        // Store the updated position
        AABBCenteredPosition = position;

        // Set camera at eye height (not feet position)
        var eyeHeight = isDucking ? ViewHeightDucked : ViewHeightStanding;
        var hull = isDucking ? PlayerHullDucked : PlayerHullStanding;
        var groundPos = AABBCenteredPosition - new Vector3(0, 0, hull.Size.Z / 2);
        camera.Location = groundPos + Vector3.UnitZ * eyeHeight;

        // Draw player AABB for debugging
        if (Physics?.SelectedNodeRenderer != null)
        {
            var aabb = isDucking ? PlayerHullDucked : PlayerHullStanding;
            var worldAABB = aabb.Translate(groundPos);
            var color = OnGround ? new Color32(0f, 1f, 0f, 1f) : new Color32(1f, 1f, 0f, 1f); // Green when grounded, yellow in air
            ShapeSceneNode.AddBox(Physics.SelectedNodeRenderer.Vertices, worldAABB, color);
        }

        return;
    }

    /// <summary>
    /// Check if player is on ground using swept AABB trace
    /// Traces down based on current downward velocity to detect ground contact
    /// </summary>
    private void CategorizePosition(ref Vector3 position, bool isDucking)
    {
        if (Physics == null)
        {
            // Fallback: simple ground plane at Z=0
            OnGround = position.Z <= 0.1f;
            return;
        }

        // Get player AABB based on ducking state
        var aabb = isDucking ? PlayerHullDucked : PlayerHullStanding;

        // Trace down from current position to check for ground
        // Use a small distance (2 units) to check if we're on or very close to ground
        // This distance should be enough to detect ground contact in Source engine scale
        var traceStart = position;
        var traceDistance = 2.0f; // Fixed small distance for ground check
        var traceEnd = position + new Vector3(0, 0, -traceDistance);

        var result = Physics.TraceAABB(traceStart, traceEnd, aabb);


        if (result.Hit && result.HitNormal.Z > 0.8f && Velocity.Z < 140.0f)
        {
            OnGround = true;
            // Snap to ground if very close, but maintain epsilon distance from surface
            position = result.HitPosition + result.HitNormal * SurfaceEpsilon;
        }
        else
        {
            OnGround = false;
        }
    }

    /// <summary>
    /// Perform swept AABB collision detection for player movement
    /// Returns the final position after collision resolution
    /// </summary>
    private Vector3 TryPlayerMove(Vector3 start, Vector3 delta, bool isDucking)
    {
        if (Physics == null || delta.LengthSquared() < 1e-6f)
        {
            // No physics or no movement - fallback to simple movement
            var newPos = start + delta;
            if (newPos.Z < 0)
            {
                newPos = new Vector3(newPos.X, newPos.Y, 0);
            }
            return newPos;
        }

        // Get player AABB based on ducking state
        var aabb = isDucking ? PlayerHullDucked : PlayerHullStanding;

        var end = start + delta;
        var result = Physics.TraceAABB(start, end, aabb);

        if (!result.Hit)
        {
            // No collision - move freely
            return end;
        }

        // Collision detected - move to hit position but maintain epsilon distance from surface
        var position = result.HitPosition + result.HitNormal * SurfaceEpsilon;

        // Calculate remaining movement after initial collision
        var remainingTime = 1.0f - result.Distance;
        if (remainingTime > 0)
        {
            // Project remaining velocity onto the collision plane (slide)
            var normal = result.HitNormal;
            var remainingDelta = delta * remainingTime;

            // Remove component of velocity along normal (don't move into surface)
            var projection = Vector3.Dot(remainingDelta, normal);
            if (projection < 0)
            {
                remainingDelta -= normal * projection;

                // Try to slide with remaining velocity
                if (remainingDelta.LengthSquared() > 1e-6f)
                {
                    var slideResult = Physics.TraceAABB(position, position + remainingDelta, aabb);
                    if (!slideResult.Hit)
                    {
                        position += remainingDelta;
                    }
                    else
                    {
                        position = slideResult.HitPosition + slideResult.HitNormal * SurfaceEpsilon;

                        // Update velocity - remove component along hit normal
                        var velProjection = Vector3.Dot(Velocity, slideResult.HitNormal);
                        if (velProjection < 0)
                        {
                            Velocity -= slideResult.HitNormal * velProjection;
                        }
                    }
                }
            }

            // Update velocity based on initial collision normal
            var velProj = Vector3.Dot(Velocity, normal);
            if (velProj < 0)
            {
                Velocity -= normal * velProj;
            }
        }

        return position;
    }

    private Vector3 TryPlayerMove2(Vector3 start, Vector3 delta, bool isDucking)
    {
        //H7per: should this be LengthSquared <= 1e-6f to the second power?
        if (Physics == null)
        {
            // No physics or no movement - fallback to simple movement
            var newPos = start + delta;
            if (newPos.Z < 0)
            {
                newPos = new Vector3(newPos.X, newPos.Y, 0);
            }
            return newPos;
        }

        if (delta.LengthSquared() < 1e-6f)
        {
            return start;
        }

        // Get player AABB based on ducking state
        var aabb = isDucking ? PlayerHullDucked : PlayerHullStanding;

        int numbumps = 4;
        int bumpcount = 0;

        //this is a bit scuffed, RollingDistance should not be needed but eh
        Vector3 RollingPosition = start;
        Vector3 RollingDelta = delta;
        float RollingDistance = delta.Length();

        float RemainingFraction = 1.0f;

        for (bumpcount = 0; bumpcount < numbumps; bumpcount++)
        {
            var result = Physics.TraceAABB(RollingPosition, RollingPosition + RollingDelta, aabb);

            if (!result.Hit)
            {
                RollingPosition += RollingDelta;
                return RollingPosition;
            }
            else
            {
                result.Distance = Math.Max(result.Distance + SurfaceEpsilon / Vector3.Dot(Vector3.Normalize(RollingDelta), result.HitNormal), 0.0f);


                float Fraction = result.Distance / RollingDistance;
                RollingPosition += RollingDelta * Fraction;
                RemainingFraction -= Fraction * RemainingFraction;

                RollingDelta -= result.HitNormal * Vector3.Dot(result.HitNormal, RollingDelta);
                //RollingDelta *= RemainingFraction;
                Velocity -= result.HitNormal * Vector3.Dot(result.HitNormal, Velocity);

                RollingDistance = RollingDelta.Length();
            }
            if (RemainingFraction <= 0)
            {
                return RollingPosition;
            }
        }

        return RollingPosition;

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
        // This subtracts 0.5 * gravity * dt
        Velocity = new Vector3(Velocity.X, Velocity.Y, Velocity.Z - GravityValue * deltaTime * 0.5f);
    }

    /// <summary>
    /// Calculate desired movement direction and speed from input
    /// </summary>
    private static (Vector3 wishdir, float wishspeed) CalculateWishVelocity(UserInput input, float pitch, float yaw)
    {
        // Calculate forward and right vectors from yaw (ignore pitch for horizontal movement)
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
        var right = new Vector3(MathF.Cos(yaw - MathF.PI / 2f), MathF.Sin(yaw - MathF.PI / 2f), 0);

        // Determine movement amounts
        float forwardMove = 0, sideMove = 0;

        if (input.Holding(TrackedKeys.Forward))
        {
            forwardMove += MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.Back))
        {
            forwardMove -= MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.Right))
        {
            sideMove += MaxSpeedValue;
        }

        if (input.Holding(TrackedKeys.Left))
        {
            sideMove -= MaxSpeedValue;
        }

        // Build wish velocity
        var wishvel = forward * forwardMove + right * sideMove;
        wishvel = new Vector3(wishvel.X, wishvel.Y, 0); // Zero out Z

        var wishspeed = wishvel.Length();
        var wishdir = wishspeed > 0 ? Vector3.Normalize(wishvel) : Vector3.Zero;

        // Clamp to max speed
        if (wishspeed > MaxSpeedValue)
        {
            wishvel *= MaxSpeedValue / wishspeed;
            wishspeed = MaxSpeedValue;
        }

        // Apply duck/crouch speed modifier (from CS:GO cs_gamemovement.cpp)
        if (input.Holding(TrackedKeys.Control)) // Duck/crouch
        {
            wishspeed *= DuckSpeedModifier;
        }

        return (wishdir, wishspeed);
    }

    /// <summary>
    /// Apply ground friction to slow down the player
    /// Ported from gamemovement.cpp Friction()
    /// </summary>
    private void Friction(float deltaTime)
    {
        // Calculate speed
        var speed = Velocity.Length();

        // If too slow, return
        if (speed < 0.1f)
        {
            return;
        }

        var drop = 0f;

        // Apply ground friction (only when on ground, but this is only called when on ground)
        var friction = FrictionValue * SurfaceFriction;

        // Bleed off some speed, but if we have less than the bleed
        // threshold, bleed the threshold amount.
        var control = (speed < StopSpeedValue) ? StopSpeedValue : speed;

        // Add the amount to the drop amount.
        drop += control * friction * deltaTime;

        // scale the velocity
        var newspeed = speed - drop;
        if (newspeed < 0)
        {
            newspeed = 0;
        }

        if (newspeed != speed)
        {
            // Determine proportion of old speed we are using.
            newspeed /= speed;
            // Adjust velocity according to proportion.
            Velocity *= newspeed;
        }
    }

    /// <summary>
    /// Accelerate in desired direction using CS:GO acceleration
    /// Ported from cs_gamemovement.cpp Accelerate()
    ///
    /// SKIPPED (not relevant without weapons):
    /// - Exponential acceleration curves (never executes since flZeroToMaxSpeedTime = 0)
    /// - Trailing velocity tracking
    /// </summary>
    private void Accelerate(Vector3 wishdir, float wishspeed, float accel, float deltaTime, bool isDucking, bool isWalking)
    {
        // See if we are changing direction a bit
        var currentspeed = Vector3.Dot(Velocity, wishdir);

        // Reduce wishspeed by the amount of veer.
        var addspeed = wishspeed - currentspeed;

        // If not going to add any speed, done.
        if (addspeed <= 0)
        {
            return;
        }

        if (currentspeed < 0)
        {
            currentspeed = 0;
        }

        // CS:GO acceleration scaling
        var flMaxSpeed = 250.0f;
        var fAccelerationScale = MathF.Max(flMaxSpeed, wishspeed);
        var flGoalSpeed = fAccelerationScale;

        // Apply duck/walk modifiers to acceleration scale and goal speed
        if (isDucking)
        {
            fAccelerationScale *= DuckSpeedModifier;
            flGoalSpeed *= DuckSpeedModifier;
        }

        if (isWalking)
        {
            fAccelerationScale *= WalkSpeedModifier;
            flGoalSpeed *= WalkSpeedModifier;
        }

        // Walk speed gradient clamping
        // When walking and near goal speed, gradually reduce acceleration to prevent overshooting
        // Formula: clamp(1.0 - ((currentspeed - (goalspeed-5)) / 5.0), 0.0, 1.0)
        // Note: Denominator simplifies to 5.0 since (goalspeed - (goalspeed - 5)) = 5
        var flStoredAccel = accel;
        if (isWalking && currentspeed > flGoalSpeed - 5)
        {
            var numerator = MathF.Max(0.0f, currentspeed - (flGoalSpeed - 5));
            var ratio = numerator / 5.0f;  // Simplified from flGoalSpeed - (flGoalSpeed - 5)
            flStoredAccel *= Math.Clamp(1.0f - ratio, 0.0f, 1.0f);
        }

        // Simple linear acceleration
        var accelspeed = flStoredAccel * deltaTime * fAccelerationScale * SurfaceFriction;

        // Cap at addspeed
        if (accelspeed > addspeed)
        {
            accelspeed = addspeed;
        }

        // Adjust velocity
        Velocity += accelspeed * wishdir;
    }

    /// <summary>
    /// Ground movement with friction and acceleration
    /// Ported from gamemovement.cpp WalkMove()
    /// Simplified - no traces, assumes flat ground
    /// </summary>
    private void WalkMove(Vector3 wishdir, float wishspeed, float deltaTime, bool isDucking, bool isWalking)
    {
        // Set pmove velocity (zero out Z component)
        Velocity = new Vector3(Velocity.X, Velocity.Y, 0);

        // Accelerate
        Accelerate(wishdir, wishspeed, AccelerateValue, deltaTime, isDucking, isWalking);

        Velocity = new Vector3(Velocity.X, Velocity.Y, 0);

        // Clamp to max speed to prevent going faster while turning
        // Important: Clamp to the duck/walk-modified max speed, not base max speed
        var effectiveMaxSpeed = MaxSpeedValue;
        if (isDucking)
        {
            effectiveMaxSpeed *= DuckSpeedModifier;
        }
        else if (isWalking)
        {
            effectiveMaxSpeed *= WalkSpeedModifier;
        }

        // Use LengthSquared for performance (avoids sqrt)
        if (Velocity.LengthSquared() > effectiveMaxSpeed * effectiveMaxSpeed)
        {
            var speed = Velocity.Length();
            Velocity *= effectiveMaxSpeed / speed;
        }
    }

    /// <summary>
    /// Air movement with reduced acceleration
    /// Ported from gamemovement.cpp AirMove()
    /// </summary>
    private void AirMove(Vector3 wishdir, float wishspeed, float deltaTime)
    {
        // Air accelerate uses different function!
        AirAccelerate(wishdir, wishspeed, AirAccelerateValue, deltaTime);
    }

    /// <summary>
    /// Air acceleration - different from ground acceleration
    /// Ported from gamemovement.cpp AirAccelerate()
    /// Note: CS:GO doesn't apply duck/walk modifiers in air (only ground acceleration)
    /// </summary>
    private void AirAccelerate(Vector3 wishdir, float wishspeed, float accel, float deltaTime)
    {
        var wishspd = wishspeed;

        // Cap speed at 30 for air control
        if (wishspd > 30)
        {
            wishspd = 30;
        }

        // Determine veer amount
        var currentspeed = Vector3.Dot(Velocity, wishdir);

        // See how much to add
        var addspeed = wishspd - currentspeed;

        // If not adding any, done.
        if (addspeed <= 0)
        {
            return;
        }

        // Determine acceleration speed after acceleration
        // Note: uses original wishspeed, NOT the capped wishspd
        var accelspeed = accel * wishspeed * deltaTime * SurfaceFriction;

        // Cap it
        if (accelspeed > addspeed)
        {
            accelspeed = addspeed;
        }

        // Adjust velocity
        Velocity += accelspeed * wishdir;
    }

    /// <summary>
    /// Check and clamp velocity - prevents NaN and enforces max velocity
    /// Ported from gamemovement.cpp CheckVelocity()
    /// </summary>
    private void CheckVelocity(ref Vector3 position)
    {
        // Check for NaN and fix
        if (float.IsNaN(Velocity.X) || float.IsNaN(Velocity.Y) || float.IsNaN(Velocity.Z))
        {
            Velocity = new Vector3(
                float.IsNaN(Velocity.X) ? 0 : Velocity.X,
                float.IsNaN(Velocity.Y) ? 0 : Velocity.Y,
                float.IsNaN(Velocity.Z) ? 0 : Velocity.Z
            );
        }

        // Check for NaN in position and fix
        if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsNaN(position.Z))
        {
            position = new Vector3(
                float.IsNaN(position.X) ? 0 : position.X,
                float.IsNaN(position.Y) ? 0 : position.Y,
                float.IsNaN(position.Z) ? 0 : position.Z
            );
        }

        // Clamp each component to max velocity
        Velocity = new Vector3(
            Math.Clamp(Velocity.X, -MaxVelocityValue, MaxVelocityValue),
            Math.Clamp(Velocity.Y, -MaxVelocityValue, MaxVelocityValue),
            Math.Clamp(Velocity.Z, -MaxVelocityValue, MaxVelocityValue)
        );
    }
}
