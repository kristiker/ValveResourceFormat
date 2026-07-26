using System.Buffers;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.ModelAnimation;
using ValveResourceFormat.ResourceTypes.ModelAnimation2;
using ValveResourceFormat.ResourceTypes.ModelData.Attachments;
using ValveResourceFormat.Serialization.KeyValues;
using ValveResourceFormat.Utils;

namespace ValveResourceFormat.Renderer.SceneNodes
{
    /// <summary>
    /// Scene node for rendering animated models with skeletal animation and morph targets.
    /// </summary>
    public class ModelSceneNode : MeshCollectionNode
    {
        /// <inheritdoc/>
        public override Vector4 Tint
        {
            get
            {
                if (meshRenderers.Count > 0)
                {
                    return meshRenderers[0].Tint;
                }

                return Vector4.One;
            }
            set
            {
                foreach (var renderer in meshRenderers)
                {
                    renderer.Tint = value;
                }
            }
        }

        /// <summary>Gets the animation controller managing skeletal pose and flex data for this model.</summary>
        public AnimationController AnimationController { get; }

        /// <summary>
        /// A collection of animations available for playback on this model.
        /// </summary>
        public Dictionary<string, Animation> Animations { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Attachment points from model data.
        /// </summary>
        public Dictionary<string, Attachment> Attachments { get; }

        /// <summary>Gets the list of nodes attached to this model and the attachment points used.</summary>
        public List<(SceneNode Node, string AttachmentName, Vector3 Offset, Quaternion Rotation)> AttachedNodes { get; } = [];

        /// <summary>Gets the name of the currently active material group (skin).</summary>
        public string ActiveMaterialGroup => activeMaterialGroup.Name;

        /// <summary>Gets whether this model has at least one mesh renderer loaded.</summary>
        public bool HasMeshes => meshRenderers.Count > 0;

        private readonly List<RenderableMesh> meshRenderers = [];

        /// <summary>Gets whether this model has animations loaded and a skeleton to animate.</summary>
        public bool IsAnimated { get; private set; }

        /// <summary>Distance from an object's transform to its first skinning matrix, the slot in between holding
        /// its Transform. Kept in sync with <c>GetObjectBoneOffset</c> in instancing.slang.</summary>
        internal const uint BoneTransformStart = 2;

        /// <summary>Smallest basis a skinning matrix is uploaded with. Invisible at any camera distance, and far
        /// enough from zero that squaring it in the shader's normalize cannot underflow.</summary>
        private const float MinimumBoneScale = 1e-5f;

        /// <summary>Gets the number of skinning matrix slots this model reserves in the scene transform buffer (the mesh-bone remapping table length).</summary>
        internal int BoneTransformCount => remappingTable.Length;

        /// <summary>Gets or sets this model's object transform slot in the scene transform buffer, assigned when the
        /// scene instance buffers are (re)built. The scale slot and the skinning matrices follow it. 0 = not assigned.</summary>
        internal uint TransformSlot { get; set; }

        /// <inheritdoc/>
        public override Matrix4x4 Transform
        {
            get => base.Transform;
            set
            {
                base.Transform = value;
                transformDirty = true;
            }
        }

        private readonly int boneCount;
        private readonly int[] remappingTable;
        private bool transformDirty;


        /// <summary>Stand-in for the scene mirror while this model has no transform slot, so the bounding
        /// box a skinning write refreshes stays correct until it gets one.</summary>
        private OpenTK.Mathematics.Matrix3x4[]? skinningScratch;
        private bool skinningTransformsDirty;

        /// <summary>Set once <see cref="UpdateParallel"/> has run for this frame, so <see cref="Update"/>
        /// knows whether it still has to evaluate the pose itself.</summary>
        private bool poseEvaluated;

        /// <summary>Flex states whose morph weights changed and still need their composite rendered.</summary>
        private readonly List<FlexStateManager> pendingMorphComposites = [];

        private HashSet<string> activeMeshGroups = [];
        private (string Name, string[] Materials) activeMaterialGroup;
        private Dictionary<string, string>? materialTable;

        private readonly (string Name, string[] Materials)[] materialGroups;
        private readonly string[] meshGroups;
        private readonly long[]? meshGroupMasks;
        private readonly ModelLodInfo lodInfo;
        private readonly List<(int MeshIndex, string MeshName, long LoDMask)> referenceMeshes;
        private int? lodOverride;
        private int resolvedLod;

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelSceneNode"/> class and loads its meshes and animations.
        /// </summary>
        /// <param name="scene">The scene this node belongs to.</param>
        /// <param name="model">The model resource to render.</param>
        /// <param name="skin">The material group (skin) name to activate, or <see langword="null"/> for the default.</param>
        /// <param name="isWorldPreview">When <see langword="true"/>, only embedded animations are loaded.</param>
        public ModelSceneNode(Scene scene, Model model, string? skin = null, bool isWorldPreview = false)
            : base(scene)
        {
            materialGroups = model.GetMaterialGroups().ToArray();
            meshGroups = model.GetMeshGroups().ToArray();

            if (meshGroups.Length > 1)
            {
                meshGroupMasks = model.Data.GetIntegerArray("m_refMeshGroupMasks");
            }

            lodInfo = model.LodInfo;
            referenceMeshes = model.GetReferenceMeshNamesAndLoD().ToList();
            resolvedLod = lodInfo.LowestLevel;

            AnimationController = new(model.Skeleton, model.FlexControllers);
            boneCount = model.Skeleton.Bones.Length;
            remappingTable = model.Data.GetIntegerArray("m_remappingTable").Select(i => (int)i).ToArray();

            if (model.Data.GetArray<string>("m_vecNmSkeletonRefs") is { Length: > 0 } nmSkelRefs)
            {
                foreach (var skeletonName in nmSkelRefs)
                {
                    var resource = Scene.RendererContext.FileLoader.LoadFileCompiled(skeletonName);
                    if (resource?.DataBlock is not BinaryKV3 skeletonData)
                    {
                        continue;
                    }

                    var skeleton = Skeleton.FromSkeletonData(skeletonData.Data);
                    AnimationController.RegisterExternalSkeleton(skeletonName, skeleton);
                }

                foreach (var clipName in AnimationGraphLoader.GetClipNames(model, Scene.RendererContext.FileLoader))
                {
                    LoadAnimationClip(clipName);
                }
            }

            if (skin != null)
            {
                SetMaterialGroup(skin);
            }

            Name = model.Name;
            Attachments = model.Attachments;

            LoadMeshes(model);
            UpdateBoundingBox();

            LoadAnimations(model, embeddedAnimationsOnly: isWorldPreview);

            SetCharacterEyeRenderParams();
            Attachments = model.Attachments;
            AnimationController.TwistConstraints = ParseTwistConstraints(model);
        }

        readonly struct CharacterEyeParameters
        {
            public int LeftEyeBoneIndex { get; }
            public Vector3 LeftEyePosition { get; }
            public Vector3 LeftEyeForwardVector { get; } = Vector3.UnitX;
            public Vector3 LeftEyeUpVector { get; } = Vector3.UnitZ;

            public int RightEyeBoneIndex { get; }
            public Vector3 RightEyePosition { get; }
            public Vector3 RightEyeForwardVector { get; } = Vector3.UnitX;
            public Vector3 RightEyeUpVector { get; } = Vector3.UnitZ;

            public int TargetBoneIndex { get; }
            public Vector3 TargetPosition { get; }

            public bool AreValid => LeftEyeBoneIndex != -1 && RightEyeBoneIndex != -1 && TargetBoneIndex != -1;

            public CharacterEyeParameters(AnimationController animationController)
            {
                var skeleton = animationController.FrameCache.Skeleton;

                LeftEyeBoneIndex = skeleton.Bones.FirstOrDefault(b => b.Name == "eyeball_l")?.Index ?? -1;
                RightEyeBoneIndex = skeleton.Bones.FirstOrDefault(b => b.Name == "eyeball_r")?.Index ?? -1;
                TargetBoneIndex = skeleton.Bones.FirstOrDefault(b => b.Name == "eye_target")?.Index ?? -1;

                if (!AreValid)
                {
                    return;
                }

                LeftEyePosition = animationController.BindPose[LeftEyeBoneIndex].Translation;
                RightEyePosition = animationController.BindPose[RightEyeBoneIndex].Translation;
                TargetPosition = animationController.BindPose[TargetBoneIndex].Translation;
            }
        }

        /// <summary>
        /// Detects eye materials on this model and injects bone index and bind-pose uniforms for eyeball rendering.
        /// </summary>
        public void SetCharacterEyeRenderParams()
        {
            var eyeEnablingMaterials = meshRenderers
                .SelectMany(Mesh => Mesh.DrawCallsOpaque.Select(Draw => (Mesh, Draw)))
                .Where(meshDraw => meshDraw.Draw.Material.Material.IntParams.GetValueOrDefault("F_EYEBALLS") == 1)
                .Select(meshDraw => (meshDraw.Mesh, meshDraw.Draw.Material.Material))
                .ToList();

            if (eyeEnablingMaterials.Count == 0)
            {
                return;
            }

            var eyes = new CharacterEyeParameters(AnimationController);

            if (!eyes.AreValid)
            {
                return;
            }

            foreach (var (mesh, materialData) in eyeEnablingMaterials)
            {
                materialData.IntParams["g_nEyeLBindIdx"] = GetMeshBoneIndex(eyes.LeftEyeBoneIndex, mesh);
                materialData.IntParams["g_nEyeRBindIdx"] = GetMeshBoneIndex(eyes.RightEyeBoneIndex, mesh);
                materialData.IntParams["g_nEyeTargetBindIdx"] = GetMeshBoneIndex(eyes.TargetBoneIndex, mesh);

                materialData.VectorParams["g_vEyeLBindPos"] = new Vector4(eyes.LeftEyePosition, 0);
                materialData.VectorParams["g_vEyeLBindFwd"] = new Vector4(eyes.LeftEyeForwardVector, 0);
                materialData.VectorParams["g_vEyeLBindUp"] = new Vector4(eyes.LeftEyeUpVector, 0);

                materialData.VectorParams["g_vEyeRBindPos"] = new Vector4(eyes.RightEyePosition, 0);
                materialData.VectorParams["g_vEyeRBindFwd"] = new Vector4(eyes.RightEyeForwardVector, 0);
                materialData.VectorParams["g_vEyeRBindUp"] = new Vector4(eyes.RightEyeUpVector, 0);

                materialData.VectorParams["g_vEyeTargetBindPos"] = new Vector4(eyes.TargetPosition, 0);
            }
        }

        /// <summary>
        /// Returns the mesh-local bone index for the given model-level bone index within the specified mesh's remapping table slice.
        /// </summary>
        public int GetMeshBoneIndex(int modelBoneIndex, RenderableMesh mesh)
            => remappingTable.AsSpan(mesh.MeshBoneOffset, mesh.MeshBoneCount).IndexOf(modelBoneIndex);

        /// <inheritdoc/>
        public override bool SupportsParallelUpdate => true;

        /// <inheritdoc/>
        /// <remarks>
        /// Advances the animation and rebuilds this model's skinning matrices and morph weights, all of
        /// which only touch state this node owns. The GPU side of it is left to <see cref="Update"/>.
        /// </remarks>
        public override void UpdateParallel(Scene.UpdateContext context)
        {
            UpdateAutoLod(context.Camera);
            var animationUpdated = AnimationController.Update(context.Timestep);

            if (IsAnimated && (animationUpdated || transformDirty))
            {
                ComputeSkinningTransforms();
            }

            transformDirty = false;
            poseEvaluated = true;

            if (!animationUpdated || AnimationController.AnimationFrame == null)
            {
                return;
            }

            var datas = AnimationController.AnimationFrame.Datas;
            foreach (var renderableMesh in RenderableMeshes)
            {
                if (renderableMesh.FlexStateManager is not { } flexState)
                {
                    continue;
                }

                if (flexState.SetControllerValues(datas))
                {
                    flexState.UpdateComposite();
                    pendingMorphComposites.Add(flexState);
                }
            }
        }

        /// <inheritdoc/>
        public override void Update(Scene.UpdateContext context)
        {
            if (!poseEvaluated)
            {
                // Attached to another model, or added after the scene built its threaded work list
                UpdateParallel(context);
            }
            else if (IsAnimated && transformDirty)
            {
                // Moved after the threaded pass ran; the skinning matrices fold in the object transform
                ComputeSkinningTransforms();
                transformDirty = false;
            }

            poseEvaluated = false;

            UpdateAttachments(context);

            foreach (var flexState in pendingMorphComposites)
            {
                flexState.MorphComposite.Render();
            }

            pendingMorphComposites.Clear();
        }

        private void UpdateAttachments(Scene.UpdateContext context)
        {
            foreach (var attachment in AttachedNodes)
            {
                var child = attachment.Node;

                // keep the child's own scale; the parent drives the rest of its transform
                var localTransform = Matrix4x4.CreateScale(GetScale(child.Transform)) * Matrix4x4.CreateFromQuaternion(attachment.Rotation) * Matrix4x4.CreateTranslation(attachment.Offset);
                child.Transform = localTransform * GetAttachmentOrSelfTransform(attachment.AttachmentName);
                child.Update(context);
            }
        }

        // The parent anchor for an attached child: the attachment point's world transform, the world
        // transform of a bone with that name when no attachment matches, or the model's own transform when
        // no name is given or it matches neither. Rigid (no scale) because Source 2 does not propagate the
        // parent's scale to attachment-parented children.
        private Matrix4x4 GetAttachmentOrSelfTransform(string attachmentName)
        {
            if (!string.IsNullOrEmpty(attachmentName))
            {
                if (Attachments.ContainsKey(attachmentName))
                {
                    return GetRigidTransform(GetAttachmentTransform(attachmentName));
                }

                var boneIndex = AnimationController.Skeleton.GetBoneIndex(attachmentName);
                if (boneIndex != -1)
                {
                    return GetRigidTransform(AnimationController.Pose[boneIndex] * Transform);
                }
            }

            return GetRigidTransform(Transform);
        }

        /// <summary>
        /// Whether the given name resolves to an anchor on this model: an attachment point,
        /// or a bone when no attachment has that name.
        /// </summary>
        public bool HasAttachmentOrBone(string name)
            => Attachments.ContainsKey(name) || AnimationController.Skeleton.GetBoneIndex(name) != -1;

        // Rotation and translation only, with scale removed.
        private static Matrix4x4 GetRigidTransform(Matrix4x4 transform)
        {
            Matrix4x4.Decompose(transform, out _, out var rotation, out var translation);
            return Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        }

        private static Vector3 GetScale(Matrix4x4 transform)
        {
            Matrix4x4.Decompose(transform, out var scale, out _, out _);
            return scale;
        }

        /// <inheritdoc/>
        public override IEnumerable<string> GetSupportedRenderModes()
            => meshRenderers.SelectMany(static renderer => renderer.GetSupportedRenderModes());

        /// <summary>
        /// Activates the named material group (skin), remapping all mesh materials accordingly.
        /// </summary>
        public void SetMaterialGroup(string name)
        {
            if (materialGroups.Length == 0)
            {
                return;
            }

            if (materialTable is null)
            {
                var @default = materialGroups[0];
                activeMaterialGroup = @default;
                materialTable = new(materialGroups[0].Materials.Length);

                if (name == @default.Name)
                {
                    return;
                }
            }

            foreach (var materialGroup in materialGroups)
            {
                if (name == materialGroup.Name)
                {
                    materialTable.Clear();

                    foreach (var (Active, Replacement) in activeMaterialGroup.Materials.Zip(materialGroup.Materials))
                    {
                        materialTable[Active] = Replacement;
                    }

                    activeMaterialGroup = materialGroup;

                    foreach (var mesh in meshRenderers)
                    {
                        mesh.ReplaceMaterials(materialTable);
                    }
                }
            }
        }

        private void LoadAnimations(Model model, bool embeddedAnimationsOnly)
        {
            var animations = (embeddedAnimationsOnly
                ? model.GetEmbeddedAnimations()
                : model.GetAllAnimations(Scene.RendererContext.FileLoader)).ToList();

            AddAnimations(animations);

            if (Animations.Count != 0)
            {
                SetupBoneMatrixBuffers();
            }
        }

        /// <summary>
        /// Adds the given animations to the collection of available animations for this model.
        /// </summary>
        public void AddAnimations(List<Animation> animations)
        {
            Animations.EnsureCapacity(animations.Count);
            foreach (var anim in animations)
            {
                Animations[anim.Name] = anim;
            }
        }

        /// <summary>
        /// Loads an animgraph2 clip from the given <see cref="AnimationClip"/> instance and makes it available for playback on this model.
        /// </summary>
        public void LoadAnimationClip(AnimationClip clip)
        {
            var anim = new Animation(clip);
            Animations[anim.Name] = anim;
        }

        /// <summary>
        /// Loads an animgraph2 clip from the file system and makes it available for playback on this model.
        /// </summary>
        /// <param name="clipName">Clip resource name.</param>
        /// <returns><see langword="true"/> if the clip was found and loaded; otherwise <see langword="false"/>.</returns>
        public bool LoadAnimationClip(string clipName)
        {
            if (!clipName.EndsWith(".vnmclip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Clip must be a {ResourceType.NmClip} resource.", nameof(clipName));
            }

            var clipResource = Scene.RendererContext.FileLoader.LoadFileCompiled(clipName);
            if (clipResource?.DataBlock is not AnimationClip clip)
            {
                return false;
            }

            LoadAnimationClip(clip);
            return true;
        }

        private void LoadMeshes(Model model)
        {
            // All LoD levels are loaded; the active one is picked at render time.
            foreach (var embeddedMesh in model.GetEmbeddedMeshesAndLoD())
            {
                embeddedMesh.Mesh.LoadExternalMorphData(Scene.RendererContext.FileLoader);
                model.SetExternalMorphData(embeddedMesh.Mesh.MorphData);

                meshRenderers.Add(new RenderableMesh(embeddedMesh.Mesh, embeddedMesh.MeshIndex, Scene, model, materialTable, embeddedMesh.Mesh.MorphData));
            }

            foreach (var refMesh in referenceMeshes)
            {
                var newResource = Scene.RendererContext.FileLoader.LoadFileCompiled(refMesh.MeshName);
                if (newResource?.DataBlock is not Mesh mesh)
                {
                    continue;
                }

                mesh.LoadExternalMorphData(Scene.RendererContext.FileLoader);
                model.SetExternalMeshData(mesh);

                meshRenderers.Add(new RenderableMesh(mesh, refMesh.MeshIndex, Scene, model, materialTable));
            }

            // Set active meshes to default
            SetActiveMeshGroups(model.GetDefaultMeshGroups());
        }

        private void SetupBoneMatrixBuffers()
        {
            if (boneCount == 0)
            {
                return;
            }

            IsAnimated = true;
        }

        /// <summary>
        /// Fills this model's slice of the scene transform mirror. Slices never overlap, so this is safe
        /// off the render thread; the scene sends the changed ranges together once the frame's updates are
        /// done. A model with no slot yet still runs, for the bounding box the write refreshes.
        /// </summary>
        private void ComputeSkinningTransforms()
        {
            var slotCount = remappingTable.Length + (int)BoneTransformStart;
            var slice = Scene.GetTransformSlice(TransformSlot, slotCount);

            if (slice.IsEmpty)
            {
                skinningScratch ??= new OpenTK.Mathematics.Matrix3x4[slotCount];
                slice = skinningScratch;
            }

            WriteSkinningTransforms(slice);
            skinningTransformsDirty = true;
        }

        /// <summary>
        /// Reads and clears the flag saying this model reposed and its buffer range still needs sending.
        /// </summary>
        internal bool ConsumeSkinningTransformsDirty()
        {
            var dirty = skinningTransformsDirty;
            skinningTransformsDirty = false;
            return dirty;
        }

        /// <summary>
        /// Writes this model's whole transform buffer range and refreshes the local bounding box to cover the
        /// animated pose: the object transform as a matrix, the same transform as a Transform, then one
        /// skinning matrix per mesh bone. The object transform is folded into the matrices so the vertex
        /// shader needs no second one, its uniform scale left out of them and applied per vertex instead.
        /// Called every frame while animating, when the model moves, and when the scene instance buffers are
        /// (re)built so paused animations survive buffer recreation.
        /// </summary>
        internal void WriteSkinningTransforms(Span<OpenTK.Mathematics.Matrix3x4> dest)
        {
            Debug.Assert(dest.Length == remappingTable.Length + (int)BoneTransformStart);

            var objectToWorld = Transform;

            if (!Matrix4x4.Decompose(objectToWorld, out var scalePerAxis, out var rotation, out var translation))
            {
                // A collapsed transform is how content hides a model, so keep the zero: the vertices fold
                // onto a point and the triangles come out zero area. Degenerate for any other reason does not.
                scalePerAxis = objectToWorld.GetDeterminant() == 0f ? Vector3.Zero : Vector3.One;
                rotation = Quaternion.Identity;
                translation = objectToWorld.Translation;
            }

            // The shader broadcasts one component and ignores any non uniformity, the way Source 2 does
            var scale = scalePerAxis.Z;

            dest[0] = objectToWorld.To3x4();

            // A Transform rather than a matrix, so the shader reads the scale as one float instead of
            // measuring a basis. Three vec4s puts it at the same byte offset Source 2 reads it from
            dest[1] = new OpenTK.Mathematics.Matrix3x4(
                scalePerAxis.X, scalePerAxis.Y, scalePerAxis.Z, 0f,
                rotation.X, rotation.Y, rotation.Z, rotation.W,
                translation.X, translation.Y, translation.Z, 0f);

            // Rotation and translation only, the scale above rides on the vertex position instead
            objectToWorld = Matrix4x4.CreateFromQuaternion(rotation);
            objectToWorld.Translation = translation;

            var boneTransforms = dest[(int)BoneTransformStart..];
            var unskinned = objectToWorld.To3x4();

            if (!IsAnimated)
            {
                boneTransforms.Fill(unskinned);
                return;
            }

            var floatBufferSize = boneCount * 16;
            var floatBuffer = ArrayPool<float>.Shared.Rent(floatBufferSize);

            try
            {
                var modelBones = MemoryMarshal.Cast<float, Matrix4x4>(floatBuffer.AsSpan(0, floatBufferSize));
                AnimationController.GetSkinningMatrices(modelBones);

                for (var i = 0; i < remappingTable.Length; i++)
                {
                    var modelBoneIndex = remappingTable[i];
                    var modelBoneExists = modelBoneIndex < boneCount && modelBoneIndex != -1;

                    if (!modelBoneExists)
                    {
                        boneTransforms[i] = unskinned;
                        continue;
                    }

                    var bone = modelBones[modelBoneIndex];

                    // A bone animated to zero scale zeroes its matrix, but only the vertices weighted to it
                    // collapse, so the seam triangles still rasterize and would carry a normalized zero
                    // length normal, a NaN, into the lighting. Substituted here rather than in the pose, so
                    // attachments keep the real transform and nothing compounds down a bone chain.
                    if (((bone.M11 * bone.M11) + (bone.M12 * bone.M12) + (bone.M13 * bone.M13)) < MinimumBoneScale * MinimumBoneScale)
                    {
                        bone.M11 = MinimumBoneScale;
                        bone.M22 = MinimumBoneScale;
                        bone.M33 = MinimumBoneScale;
                    }

                    // Conjugating by the scale leaves it on the translation, the position multiply does the rest
                    bone.M41 *= scale;
                    bone.M42 *= scale;
                    bone.M43 *= scale;

                    boneTransforms[i] = (bone * objectToWorld).To3x4();
                }

                UpdateAnimatedBoundingBox();
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floatBuffer);
            }
        }

        /// <summary>Activates the animation with the given name, or stops animation if not found.</summary>
        public void SetAnimationByName(string animationName, float blendTime = 0f)
        {
            Animations.TryGetValue(animationName, out var activeAnimation);
            SetAnimation(activeAnimation, blendTime);
        }

        /// <summary>
        /// Activates the named animation for world preview mode.
        /// </summary>
        /// <returns><see langword="true"/> if the animation was found and activated; otherwise <see langword="false"/>.</returns>
        public bool SetAnimationForWorldPreview(string animationName)
        {
            Animation? activeAnimation = null;

            if (animationName != null)
            {
                Animations.TryGetValue(animationName, out activeAnimation);
            }

            // TODO: CS2 falls back to the first animation, but other games seemingly do not.
            //activeAnimation ??= animations.FirstOrDefault(); // Fallback to the first animation

            if (activeAnimation != null)
            {
                SetAnimation(activeAnimation);
                return true;
            }

            return false;
        }

        /// <summary>Activates the given animation instance with a blend-in time, or clears the active animation when <see langword="null"/>.</summary>
        /// <param name="activeAnimation">The animation to activate, or <see langword="null"/> to clear.</param>
        /// <param name="blendTime">The time in seconds to blend from the current animation to the new one.</param>
        public void SetAnimation(Animation? activeAnimation, float blendTime = 0f)
        {
            AnimationController.SetAnimation(activeAnimation, blendTime);
            UpdateBoundingBox();

            var animated = activeAnimation != default && IsAnimated;

            foreach (var renderer in meshRenderers)
            {
                renderer.SetAnimated(animated);
            }
        }

        /// <summary>
        /// Attaches another <see cref="SceneNode"/> to this model with optional attachment point, offset and rotation.
        /// </summary>
        /// <param name="node">The child model to attach.</param>
        /// <param name="attachmentName">The attachment point name.</param>
        /// <param name="offset">The local offset from the attachment point.</param>
        /// <param name="rotation">The local rotation from the attachment point.</param>
        public void AttachNode(SceneNode node,
            string attachmentName = "",
            Vector3 offset = default,
            Quaternion rotation = default)
        {
            node.Parent = this;
            AttachedNodes.RemoveAll(entry => entry.Node == node);
            AttachedNodes.Add((node, attachmentName, offset, rotation));
        }

        /// <summary>
        /// Places <paramref name="child"/> once at the named attachment point or bone (or the model's own
        /// transform when no name is given), with <paramref name="offset"/> applied in that anchor's frame.
        /// Unlike <see cref="AttachNode"/>, the child does not track the model afterwards. Works for any scene node.
        /// </summary>
        public void PlaceNode(SceneNode child, string attachmentName, Vector3 offset)
        {
            child.Transform = Matrix4x4.CreateTranslation(offset) * GetAttachmentOrSelfTransform(attachmentName);
        }

        /// <summary>
        /// Attaches <paramref name="node"/> so it keeps its current world position relative to this model,
        /// following the model if it later moves. Used for plain <c>parentname</c> parenting (no attachment point),
        /// where the child stays where it was authored instead of snapping onto the parent.
        /// </summary>
        /// <param name="node">The child to attach.</param>
        public void AttachNodeKeepingTransform(SceneNode node)
        {
            Matrix4x4.Invert(GetRigidTransform(Transform), out var anchorInverse);
            var local = GetRigidTransform(node.Transform) * anchorInverse;
            Matrix4x4.Decompose(local, out _, out var rotation, out var offset);
            AttachNode(node, offset: offset, rotation: rotation);
        }

        /// <summary>
        /// Gets the world transform for the specified attachment point.
        /// </summary>
        public Matrix4x4 GetAttachmentTransform(string attachmentName)
        {
            var transform = Matrix4x4.Identity;

            var attachment = Attachments.GetValueOrDefault(attachmentName);
            if (attachment != null)
            {
                for (var i = 0; i < attachment.Length; i++)
                {
                    var influence = attachment[i];
                    var boneIndex = AnimationController.FrameCache.Skeleton.GetBoneIndex(influence.Name);
                    if (boneIndex != -1)
                    {
                        var boneTransform = AnimationController.Pose[boneIndex];
                        var influenceTransform = Matrix4x4.CreateFromQuaternion(influence.Rotation) * Matrix4x4.CreateTranslation(influence.Offset);
                        transform *= Matrix4x4.Lerp(Matrix4x4.Identity, influenceTransform * boneTransform, influence.Weight);
                    }
                }

                if (attachment.IgnoreRotation)
                {
                    var scale = transform.M22;
                    var translation = transform.Translation;
                    transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(translation);
                }
            }

            return transform * Transform;
        }

#pragma warning disable CA1024 // Use properties where appropriate
        /// <summary>Returns every external reference mesh name and its LoD mask, across all levels.</summary>
        public IEnumerable<(int MeshIndex, string MeshName, long LoDMask)> GetReferenceMeshes()
            => referenceMeshes;

        /// <summary>Returns all mesh group names defined by this model.</summary>
        public IEnumerable<string> GetMeshGroups()
            => meshGroups;

        /// <summary>Returns the set of currently active mesh group names.</summary>
        public ICollection<string> GetActiveMeshGroups()
            => activeMeshGroups;
#pragma warning restore CA1024 // Use properties where appropriate

        /// <summary>
        /// Sets which mesh groups are active, rebuilding the renderable mesh list accordingly.
        /// </summary>
        public void SetActiveMeshGroups(IEnumerable<string> setMeshGroups)
        {
            activeMeshGroups = new HashSet<string>(meshGroups.Intersect(setMeshGroups));
            RebuildRenderableMeshes();
        }

        /// <summary>Gets the LoD level currently being rendered (auto-selected or forced).</summary>
        public int ActiveLod => resolvedLod;

        /// <summary>Gets whether the LoD level is being chosen automatically by distance, rather than forced.</summary>
        public bool IsAutoLod => lodOverride == null;

        /// <summary>
        /// Sets the LoD level to display, rebuilding the renderable mesh list accordingly.
        /// Pass <see langword="null"/> to enable automatic distance-based selection.
        /// </summary>
        public void SetActiveLod(int? lod)
        {
            lodOverride = lod;

            // A forced level stays put; Auto starts at the lowest populated level and UpdateAutoLod takes over.
            resolvedLod = lod ?? lodInfo.LowestLevel;
            RebuildRenderableMeshes();
        }

        /// <summary>
        /// In automatic mode, picks the LoD level from the screen-size metric: the model drops to LoD
        /// <c>n</c> once the metric passes <c>m_lodGroupSwitchDistances[n]</c>. Depends on FOV and
        /// resolution, not on how big the model is.
        /// </summary>
        private void UpdateAutoLod(Camera camera)
        {
            if (lodOverride != null || lodInfo.AvailableLevels.Count <= 1 || lodInfo.SwitchDistances.Count <= 1)
            {
                return;
            }

            var target = lodInfo.SelectLevel(ComputeLodMetric(camera));

            if (target != resolvedLod)
            {
                resolvedLod = target;
                RebuildRenderableMeshes();
            }
        }

        /// <summary>
        /// Computes the LoD metric: <c>100 / on-screen size of a unit sphere at the model origin</c>.
        /// It depends only on camera distance and FOV/viewport height, so where the model sits on
        /// screen doesn't matter and looking around won't flip LoDs.
        /// </summary>
        private float ComputeLodMetric(Camera camera)
        {
            var distance = MathF.Sqrt(GetCameraDistance(camera));

            // Size on screen of a unit sphere at this distance. M22 is the projection's
            // 1/tan(vFov/2) y-scale, so the pixel height is windowHeight * M22 / distance.
            var unitSphereSize = distance > 0f
                ? camera.WindowSize.Y * camera.ProjectionMatrix.M22 / distance
                : float.MaxValue;

            return unitSphereSize > 0f ? 100f / unitSphereSize : 0f;
        }

        private bool IsMeshInActiveLod(int meshIndex)
            => lodInfo.IsMeshInLevel(meshIndex, resolvedLod);

        private bool IsMeshInActiveGroup(int meshIndex)
        {
            if (meshGroups.Length <= 1 || meshGroupMasks == null)
            {
                return true;
            }

            foreach (var group in activeMeshGroups)
            {
                var groupIndex = Array.IndexOf(meshGroups, group);
                if (groupIndex >= 0 && (meshGroupMasks[meshIndex] & 1L << groupIndex) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildRenderableMeshes()
        {
            RenderableMeshes.Clear();

            foreach (var meshRenderer in meshRenderers)
            {
                if (IsMeshInActiveLod(meshRenderer.MeshIndex) && IsMeshInActiveGroup(meshRenderer.MeshIndex))
                {
                    RenderableMeshes.Add(meshRenderer);
                }
            }
        }

        private void UpdateBoundingBox()
        {
            var first = true;
            foreach (var mesh in meshRenderers)
            {
                LocalBoundingBox = first ? mesh.BoundingBox : LocalBoundingBox.Union(mesh.BoundingBox);
                first = false;
            }
        }

        /// <summary>
        /// Fits the local bounding box to the current pose by placing each bone's authored sphere at that
        /// bone's posed origin. Replaces sweeping the whole mesh box through every bone's skinning matrix,
        /// which cost a matrix transform per bone and produced a box far larger than the model.
        /// </summary>
        private void UpdateAnimatedBoundingBox()
        {
            if (AnimationController.Skeleton.BoneSpheres.Length == 0)
            {
                return;
            }

            var spheres = AnimationController.Skeleton.BoneSpheres;
            var pose = AnimationController.Pose;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);

            for (var boneIndex = 0; boneIndex < spheres.Length; boneIndex++)
            {
                var radius = new Vector3(spheres[boneIndex]);
                var origin = pose[boneIndex].Translation;

                min = Vector3.Min(min, origin - radius);
                max = Vector3.Max(max, origin + radius);
            }

            LocalBoundingBox = new AABB(min, max);
        }

#if DEBUG
        /// <inheritdoc/>
        public override void UpdateVertexArrayObjects()
        {
            foreach (var renderer in meshRenderers)
            {
                renderer.UpdateVertexArrayObjects();
            }
        }
#endif

        /// <inheritdoc/>
        public override void Delete()
        {
        }

        /// <summary>
        /// Parses tilt-twist constraints from the model's keyvalues.
        /// </summary>
        protected static TiltTwistConstraint[] ParseTwistConstraints(Model model)
        {
            var keyvalues = model.KeyValues;
            if (!keyvalues.ContainsKey("BoneConstraintList"))
            {
                return [];
            }

            var boneConstraintList = keyvalues.GetArray("BoneConstraintList");
            var constraints = new List<TiltTwistConstraint>();

            foreach (var constraintData in boneConstraintList)
            {
                var className = constraintData.GetStringProperty("_class");
                if (className != "CTiltTwistConstraint")
                {
                    continue;
                }

                var upVec = constraintData.GetFloatArray("m_vUpVector");

                var constraint = new TiltTwistConstraint
                {
                    Name = constraintData.GetStringProperty("m_name"),
                    UpVector = new Vector3(upVec[0], upVec[1], upVec[2]),
                    TargetAxis = (int)constraintData.GetIntegerProperty("m_nTargetAxis"),
                    SlaveAxis = (int)constraintData.GetIntegerProperty("m_nSlaveAxis"),
                };

                // Parse slaves
                var slaves = constraintData.GetArray("m_slaves");
                constraint.Slaves = slaves.Select(s =>
                {
                    var quat = s.GetFloatArray("m_qBaseOrientation");
                    var pos = s.GetFloatArray("m_vBasePosition");

                    return new TiltTwistConstraintSlave
                    {
                        BaseOrientation = new Quaternion(quat[0], quat[1], quat[2], quat[3]),
                        BasePosition = new Vector3(pos[0], pos[1], pos[2]),
                        BoneHash = s.GetUInt32Property("m_nBoneHash"),
                        Weight = s.GetFloatProperty("m_flWeight"),
                        Name = s.GetStringProperty("m_sName"),
                    };
                }).ToArray();

                // Parse targets
                var targets = constraintData.GetArray("m_targets");
                constraint.Targets = targets.Select(t =>
                {
                    var quat = t.GetFloatArray("m_qOffset");
                    var pos = t.GetFloatArray("m_vOffset");

                    return new TiltTwistConstraintTarget
                    {
                        Offset = new Quaternion(quat[0], quat[1], quat[2], quat[3]),
                        PositionOffset = new Vector3(pos[0], pos[1], pos[2]),
                        BoneHash = t.GetUInt32Property("m_nBoneHash"),
                        Name = t.GetStringProperty("m_sName"),
                        Weight = t.GetFloatProperty("m_flWeight"),
                        IsAttachment = t.GetBooleanProperty("m_bIsAttachment"),
                    };
                }).ToArray();

                constraints.Add(constraint);
            }

            return [.. constraints];
        }
    }
}
