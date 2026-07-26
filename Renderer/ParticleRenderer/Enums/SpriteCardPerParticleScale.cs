namespace ValveResourceFormat.Renderer.Particles
{
    /// <summary>
    /// Selects which per-particle scalar scales one of a texture layer's controls, letting a single
    /// authored value spread out across the particles in a system.
    /// </summary>
    /// <remarks>
    /// These are multipliers, so <see cref="SPRITECARD_TEXTURE_PP_SCALE_NONE"/> resolves to 1 and leaves
    /// the control it drives untouched. The age-based entries resolve to <c>age + 1</c> rather than to the
    /// age itself, for the same reason: a particle at birth scales by 1.
    /// </remarks>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particles/SpriteCardPerParticleScale_t">SpriteCardPerParticleScale_t</seealso>
    public enum SpriteCardPerParticleScale
    {
        /// <summary>Not driven; resolves to 1.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_NONE = 0,
        /// <summary>Particle age in seconds, plus 1.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_PARTICLE_AGE = 1,
        /// <summary>Current sprite sheet frame, plus 1.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_ANIMATION_FRAME = 2,
        /// <summary>The particle's first shader extra data field.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_SHADER_EXTRA_DATA1 = 3,
        /// <summary>The particle's second shader extra data field.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_SHADER_EXTRA_DATA2 = 4,
        /// <summary>The particle's alpha.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_PARTICLE_ALPHA = 5,
        /// <summary>The particle's radius.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_SHADER_RADIUS = 6,
        /// <summary>Roll angle in radians.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_ROLL = 7,
        /// <summary>Yaw angle in radians.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_YAW = 8,
        /// <summary>Pitch angle in radians.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_PITCH = 9,
        /// <summary>A stable per-particle random in [0, 1).</summary>
        SPRITECARD_TEXTURE_PP_SCALE_RANDOM = 10,
        /// <summary>That random remapped to [-1, 1).</summary>
        SPRITECARD_TEXTURE_PP_SCALE_NEG_RANDOM = 11,
        /// <summary>Age scaled by the random, plus 1.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_RANDOM_TIME = 12,
        /// <summary>Age scaled by the signed random, offset by its sign so the result stays away from 0.</summary>
        SPRITECARD_TEXTURE_PP_SCALE_NEG_RANDOM_TIME = 13,
    }
}
