namespace ValveResourceFormat.Renderer.Particles
{
    /// <summary>
    /// Whether a renderer fades its cards out where they approach opaque scene geometry, so that an
    /// intersection reads as a soft edge rather than a hard cut line.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/particles/ParticleDepthFeatheringMode_t">ParticleDepthFeatheringMode_t</seealso>
    public enum ParticleDepthFeatheringMode
    {
        /// <summary>No feathering; cards intersect geometry with a hard edge.</summary>
        PARTICLE_DEPTH_FEATHERING_OFF = 0,
        /// <summary>Feather where a depth copy is available, but do not force one to be produced.</summary>
        PARTICLE_DEPTH_FEATHERING_ON_OPTIONAL = 1,
        /// <summary>Feather, requiring a depth copy.</summary>
        PARTICLE_DEPTH_FEATHERING_ON_REQUIRED = 2,
    }
}
