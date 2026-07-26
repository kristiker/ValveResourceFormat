using System.Diagnostics;
using ValveResourceFormat.ResourceTypes.ModelFlex;

namespace ValveResourceFormat.ResourceTypes.ModelAnimation
{
    /// <summary>
    /// Caches animation frames to optimize frame retrieval and interpolation.
    /// </summary>
    public class AnimationFrameCache
    {
        private Frame PrevFrame;
        private Frame NextFrame;

        /// <summary>
        /// The output frame.
        /// </summary>
        public Frame InterpolatedFrame { get; }

        /// <summary>
        /// Gets the skeleton associated with this frame cache.
        /// </summary>
        public Skeleton Skeleton { get; }

        /// <summary>
        /// Gets the flex controllers associated with this frame cache.
        /// </summary>
        public FlexController[] FlexControllers { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="AnimationFrameCache"/> class.
        /// </summary>
        public AnimationFrameCache(Skeleton skeleton, FlexController[] flexControllers)
        {
            PrevFrame = new Frame(skeleton, flexControllers);
            NextFrame = new Frame(skeleton, flexControllers);
            InterpolatedFrame = new Frame(skeleton, flexControllers);
            Skeleton = skeleton;
            FlexControllers = flexControllers;
            Clear();
        }

        /// <summary>
        /// Clears the cached frames (previous and next) and resets the frame cache.
        /// Should be used on animation change.
        /// </summary>
        public void Clear()
        {
            PurgeCache();
            PrevFrame.Clear(Skeleton);
            NextFrame.Clear(Skeleton);
        }

        /// <summary>
        /// Get the animation frame at a time.
        /// </summary>
        /// <param name="anim">The animation to interpolate.</param>
        /// <param name="time">The time to get the frame for.</param>
        public Frame GetInterpolatedFrame(Animation anim, float time)
        {
            if (anim.FrameCount <= 1)
            {
                return GetFrame(anim, 0);
            }

            // Calculate the index of the current frame
            var frameIndex = (int)(time * anim.Fps) % (anim.FrameCount - 1);
            var nextFrameIndex = (frameIndex + 1) % anim.FrameCount;
            var t = ((time * anim.Fps) - frameIndex) % 1;

            // Get current and next frame
            var frame1 = GetFrame(anim, frameIndex);
            var frame2 = GetFrame(anim, nextFrameIndex);

            // Make sure second GetFrame call didn't return incorrect instance
            Debug.Assert(frame1.FrameIndex == frameIndex);
            Debug.Assert(frame2.FrameIndex == nextFrameIndex);

            // Interpolate bone positions, angles and scale.
            // Quaternion.Lerp normalizes and takes the shortest arc, differing from Slerp only in sweeping
            // at a non-constant angular rate. These two frames are adjacent within one animation, a
            // thirtieth of a second and a few degrees apart, where that costs under a tenth of a degree
            // and Slerp's acos and sines are most of what it takes to pose a model. Blending between
            // clips is the case that needs the real thing, and still uses it: see FrameBone.Blend.
            // Held in locals and written whole. FrameBone's members are auto properties behind an array
            // property, so assigning the three of them in place costs three getters and three bounds
            // checks a bone, which at this call rate is most of what the loop does.
            var sourceBones1 = frame1.Bones;
            var sourceBones2 = frame2.Bones;
            var destinationBones = InterpolatedFrame.Bones;

            for (var i = 0; i < sourceBones1.Length; i++)
            {
                var frame1Bone = sourceBones1[i];
                var frame2Bone = sourceBones2[i];

                destinationBones[i] = new FrameBone(
                    Vector3.Lerp(frame1Bone.Position, frame2Bone.Position, t),
                    float.Lerp(frame1Bone.Scale, frame2Bone.Scale, t),
                    Quaternion.Lerp(frame1Bone.Angle, frame2Bone.Angle, t));
            }

            var sourceDatas1 = frame1.Datas;
            var sourceDatas2 = frame2.Datas;
            var destinationDatas = InterpolatedFrame.Datas;

            for (var i = 0; i < sourceDatas1.Length; i++)
            {
                destinationDatas[i] = float.Lerp(sourceDatas1[i], sourceDatas2[i], t);
            }

            if (anim.HasMovementData())
            {
                InterpolatedFrame.Movement = new(
                    Vector3.Lerp(frame1.Movement.Position, frame2.Movement.Position, t),
                    float.Lerp(frame1.Movement.Angle, frame2.Movement.Angle, t)
                );
            }

            return InterpolatedFrame;
        }

        /// <summary>
        /// Get the animation frame at a given index.
        /// </summary>
        public Frame GetFrame(Animation anim, int frameIndex)
        {
            // Try to lookup cached (precomputed) frame - happens when GUI Autoplay runs faster than animation FPS
            if (frameIndex == PrevFrame.FrameIndex)
            {
                return PrevFrame;
            }

            var frame = NextFrame;
            NextFrame = PrevFrame;
            PrevFrame = frame;

            // Only two frames are cached at a time to minimize memory usage, especially with Autoplay enabled
            if (frameIndex == frame.FrameIndex)
            {
                return frame;
            }

            // We make an assumption that frames within one animation
            // contain identical bone sets, so we don't clear frame here
            frame.FrameIndex = frameIndex;
            anim.DecodeFrame(frame);

            frame.Movement = anim.GetMovementOffsetData(frameIndex);
            return frame;
        }

        /// <summary>
        /// Purges the frame cache, resetting both previous and next frames.
        /// </summary>
        public void PurgeCache()
        {
            PrevFrame.FrameIndex = -2;
            NextFrame.FrameIndex = -1;
        }
    }
}
