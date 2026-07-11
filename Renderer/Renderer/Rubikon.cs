using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;
using ValveResourceFormat.Serialization.KeyValues;
using static ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Mesh;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Ray tracing against Rubikon physics collision shapes including meshes and hulls.
/// </summary>
public class Rubikon
{
    private const int STACK_SIZE = 64;
    private const float Epsilon = 1e-6f;

    /// <summary>
    /// Triangle mesh collision data for ray tracing.
    /// </summary>
    public record PhysicsMeshData(
        string[] InteractAs,
        string[] InteractExclude,
        Vector3[] VertexPositions,
        Triangle[] Triangles,
        Node[] PhysicsTree
    );

    /// <summary>
    /// Convex hull collision data with vertices, edges, and planes.
    /// </summary>
    public record PhysicsHullData(
        Vector3 Min,
        Vector3 Max,
        Vector3[] VertexPositions,
        Hull.HalfEdge[] HalfEdges,
        byte[] FaceEdgeIndices,
        Hull.Plane[] Planes
    );

    /// <summary>Gets the triangle mesh collision shapes available for tracing.</summary>
    public PhysicsMeshData[] Meshes { get; }

    /// <summary>Gets the convex hull collision shapes available for tracing.</summary>
    public PhysicsHullData[] Hulls { get; }

    /// <summary>Gets the BVH acceleration structure built over <see cref="Hulls"/>.</summary>
    public Node[] HullTree { get; }

    private int[] HullIndices { get; }

    /// <summary>Initializes Rubikon by parsing all mesh and hull shapes from the physics aggregate data.</summary>
    /// <param name="physicsData">Source physics aggregate containing shapes and collision attributes.</param>
    public Rubikon(PhysAggregateData physicsData)
    {
        var worldMeshes = physicsData.Parts[0].Shape.Meshes
            .ToArray();

        Meshes = new PhysicsMeshData[worldMeshes.Length];
        var meshIndex = 0;

        foreach (var mesh in worldMeshes)
        {
            var vertexPositions = mesh.Shape.GetVertices();
            var triangles = mesh.Shape.GetTriangles();
            var physicsTree = mesh.Shape.ParseNodes();

            var collisionAttributes = physicsData.CollisionAttributes[mesh.CollisionAttributeIndex];
            var collisionGroup = collisionAttributes.GetStringProperty("m_CollisionGroupString");

            var interactAs = collisionAttributes.GetArray<string>("m_InteractAsStrings");
            var interactExclude = collisionAttributes.GetArray<string>("m_InteractExcludeStrings");

            Meshes[meshIndex++] = new PhysicsMeshData(interactAs, interactExclude, [.. vertexPositions], [.. triangles], [.. physicsTree]);
        }

        // we want to run player clip traces first because the mesh is much simpler
        Meshes = [.. Meshes.OrderByDescending(m => m.InteractAs.Contains("playerclip"))];

        Hulls = new PhysicsHullData[physicsData.Parts[0].Shape.Hulls.Length];
        var hullIndex = 0;
        foreach (var hullDesc in physicsData.Parts[0].Shape.Hulls)
        {
            var hull = hullDesc.Shape;
            var vertexPositions = hull.GetVertexPositions();
            var halfEdges = hull.GetEdges();
            var faceEdgeIndices = hull.GetFaces();
            var planes = hull.GetPlanes();


            Hulls[hullIndex++] = new PhysicsHullData(
                hull.Min, hull.Max,
                [.. vertexPositions],
                [.. halfEdges],
                [.. MemoryMarshal.Cast<Hull.Face, byte>(faceEdgeIndices)],
                [.. planes]
            );
        }

        // Build BVH for hulls
        HullIndices = [.. Enumerable.Range(0, Hulls.Length)];
        HullTree = BuildHullBVH();
    }

    /// <summary>
    /// Ray trace hit result with position, normal, and distance.
    /// </summary>
    public record struct TraceResult(bool Hit, Vector3 HitPosition, Vector3 HitNormal, float Distance, int TriangleIndex)
    {
        /// <summary>Initializes a default <see cref="TraceResult"/> representing a miss at maximum distance.</summary>
        public TraceResult() : this(false, Vector3.Zero, Vector3.UnitZ, float.MaxValue, -1) { }

        /// <summary>
        /// Gets or sets a value indicating whether the swept shape already overlapped geometry
        /// at the start position. When set, <see cref="Distance"/> is 0 and the reported normal
        /// belongs to one arbitrary overlapping triangle.
        /// </summary>
        public bool StartSolid { get; set; }

        /// <summary>
        /// Did we hit something very close to the starting position?
        /// </summary>
        public readonly bool IsMinimalDistance => Distance < 0.00001f;

        /// <summary>
        /// Updates this TraceResult if the other is closer. Returns true if updated.
        /// </summary>
        public bool MinimizeWith(TraceResult other)
        {
            if (other.Hit && other.Distance < Distance)
            {
                this = other;
                return true;
            }

            return false;
        }

        /// <summary>Updates this result if <paramref name="other"/> is closer, and returns <see langword="true"/> if the new hit is within the minimal-distance threshold.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MinimizeWith_EarlyExit(TraceResult other)
        {
            return MinimizeWith(other) && IsMinimalDistance;
        }
    }

    /// <summary>
    /// Precomputed ray direction data for accelerated ray tracing.
    /// </summary>
    public readonly record struct RayTraceContext
    {
        /// <summary>Gets the ray start position.</summary>
        public Vector3 Origin { get; init; }

        /// <summary>Gets the normalized ray direction.</summary>
        public Vector3 Direction { get; init; }

        /// <summary>Gets the component-wise reciprocal of <see cref="Direction"/> for slab-method AABB tests.</summary>
        public Vector3 InvDirection { get; init; }

        /// <summary>Gets the ray length.</summary>
        public float Length { get; init; }

        /// <summary>Gets the ray end position.</summary>
        public readonly Vector3 EndPosition => Origin + Direction * Length;

        /// <summary>Initializes a new ray trace context from start and end positions.</summary>
        /// <param name="start">Ray start position.</param>
        /// <param name="end">Ray end position.</param>
        public RayTraceContext(Vector3 start, Vector3 end)
        {
            Origin = start;
            Direction = Vector3.Normalize(end - start);
            InvDirection = Vector3.One / Direction;
            Length = Vector3.Distance(start, end);
        }
    }

    private static bool IsInvalidRay(Vector3 from, Vector3 to)
    {
        return Vector3.DistanceSquared(from, to) < Epsilon * Epsilon;
    }

    /// <summary>Traces a ray against all physics shapes and returns the closest hit.</summary>
    /// <param name="from">Ray start position.</param>
    /// <param name="to">Ray end position.</param>
    /// <returns>The closest <see cref="TraceResult"/>, or an empty result if nothing was hit.</returns>
    public TraceResult TraceRay(Vector3 from, Vector3 to)
    {
        TraceResult closestHit = new();

        if (IsInvalidRay(from, to))
        {
            return closestHit;
        }

        RayTraceContext ray = new(from, to);

        foreach (var mesh in Meshes)
        {
            if (mesh.InteractAs.Length > 0 && !mesh.InteractAs.Contains("passbullets"))
            {
                continue;
            }

            RayIntersectsWithMesh(ray, mesh, ref closestHit);
        }

        foreach (var hull in Hulls)
        {
            RayIntersectsWithHull(ray, hull, ref closestHit);
        }

        return closestHit;
    }

    /// <summary>Precomputed sweep data for an axis-aligned box trace.</summary>
    public readonly struct AABBTraceContext
    {
        /// <summary>Gets the start position of the sweep.</summary>
        public Vector3 Origin { get; }

        /// <summary>Gets the end position of the sweep.</summary>
        public Vector3 End { get; }

        /// <summary>Gets the normalized sweep direction.</summary>
        public Vector3 Direction { get; }

        /// <summary>Gets the half-extents of the swept AABB.</summary>
        public Vector3 HalfExtents { get; }

        /// <summary>Gets the total sweep length.</summary>
        public float Length { get; }

        /// <summary>Gets the sweep center line as a precomputed ray.</summary>
        public RayTraceContext Ray { get; }

        /// <summary>Gets a value indicating whether triangles are tested for overlap at the
        /// start position (reported as <see cref="TraceResult.StartSolid"/>).</summary>
        public bool DetectStartSolid { get; }

        /// <summary>Initializes a new AABB trace context from start/end positions and box half-extents.</summary>
        /// <param name="start">Sweep start position.</param>
        /// <param name="end">Sweep end position.</param>
        /// <param name="halfExtents">Half-extents of the swept box.</param>
        /// <param name="detectStartSolid">Whether to test triangles for overlap at the start position.</param>
        public AABBTraceContext(Vector3 start, Vector3 end, Vector3 halfExtents, bool detectStartSolid = false)
        {
            Origin = start;
            End = end;
            Ray = new RayTraceContext(start, end);
            Direction = Ray.Direction;
            HalfExtents = halfExtents;
            Length = Ray.Length;
            DetectStartSolid = detectStartSolid;
        }
    }

    /// <summary>Sweeps an axis-aligned bounding box through the physics world and returns the closest hit.</summary>
    /// <param name="from">Sweep start position (center of the AABB).</param>
    /// <param name="to">Sweep end position.</param>
    /// <param name="aabb">Box whose size determines the half-extents of the swept volume.</param>
    /// <param name="collisionName">Collision group name used to filter shapes (e.g. "player").</param>
    /// <param name="detectStartSolid">Whether to also test for overlap at the start position (see <see cref="TraceResult.StartSolid"/>).</param>
    /// <returns>The closest <see cref="TraceResult"/>, or an empty result if nothing was hit.</returns>
    public TraceResult TraceAABB(Vector3 from, Vector3 to, AABB aabb, string collisionName, bool detectStartSolid = false)
    {
        TraceResult closestHit = new();

        if (IsInvalidRay(from, to))
        {
            return closestHit;
        }

        var halfExtents = aabb.Size * 0.5f;
        var trace = new AABBTraceContext(from, to, halfExtents, detectStartSolid);

        // Check against all meshes
        foreach (var mesh in Meshes)
        {
            // player collision rules
            if (collisionName == "player")
            {
                if (mesh.InteractExclude.Contains("player"))
                {
                    continue;
                }

                if (mesh.InteractAs.Length > 0 && !mesh.InteractAs.Contains("playerclip"))
                {
                    continue;
                }
            }

            AABBTraceMesh(trace, mesh, ref closestHit);
            if (closestHit.IsMinimalDistance)
            {
                break;
            }
        }

        if (HullTree.Length > 0)
        {
            AABBTraceHullBVH(trace, ref closestHit);
        }

        return closestHit;
    }

    private static void RayIntersectsWithHull(RayTraceContext ray, PhysicsHullData hull, ref TraceResult closestHit)
    {
        // Skip hulls that cannot contain a hit closer than the best one found so far
        if (!RayIntersectsAABB(ray, hull.Min, hull.Max, out var entryDistance) || entryDistance > closestHit.Distance)
        {
            return;
        }

        foreach (var firstEdgeCcw in hull.FaceEdgeIndices)
        {
            var edge0 = hull.HalfEdges[firstEdgeCcw];
            Hull.HalfEdge edge3 = default;

            var edgeIndex = edge0.Next;
            var v0 = hull.VertexPositions[edge0.Origin];

            do
            {
                var edge1 = hull.HalfEdges[edgeIndex];
                var edge2 = hull.HalfEdges[edge1.Next];

                // Just do triangle intersection?
                var v1 = hull.VertexPositions[edge1.Origin];
                var v2 = hull.VertexPositions[edge2.Origin];

                if (RayIntersectsTriangle(ray, v0, v1, v2, out var intersection))
                {
                    // Update if this is the closest hit
                    if (intersection.Distance < closestHit.Distance)
                    {
                        closestHit = new(true, ray.Origin + ray.Direction * intersection.Distance, intersection.Normal, intersection.Distance, -1);
                    }
                }

                edgeIndex = edge1.Next;
                edge3 = hull.HalfEdges[edge2.Next];
            } while (edge3.Origin != edge0.Origin);
        }
    }

    private static void AABBTraceHull(AABBTraceContext trace, PhysicsHullData hull, ref TraceResult closestHit)
    {
        // Expand hull AABB by trace half extents for conservative culling, and skip
        // hulls that cannot contain a hit closer than the best one found so far
        if (!RayIntersectsAABB(trace.Ray, hull.Min - trace.HalfExtents, hull.Max + trace.HalfExtents, out var entryDistance) || entryDistance > closestHit.Distance)
        {
            return;
        }

        foreach (var firstEdgeCcw in hull.FaceEdgeIndices)
        {
            var edge0 = hull.HalfEdges[firstEdgeCcw];
            Hull.HalfEdge edge3 = default;

            var edgeIndex = edge0.Next;
            var v0 = hull.VertexPositions[edge0.Origin];

            do
            {
                var edge1 = hull.HalfEdges[edgeIndex];
                var edge2 = hull.HalfEdges[edge1.Next];

                // Just do triangle intersection?
                var v1 = hull.VertexPositions[edge1.Origin];
                var v2 = hull.VertexPositions[edge2.Origin];

                AABBTraceTriangle13AxisSat(trace, v0, v1, v2, ref closestHit);

                if (closestHit.IsMinimalDistance)
                {
                    return;
                }

                edgeIndex = edge1.Next;
                edge3 = hull.HalfEdges[edge2.Next];
            } while (edge3.Origin != edge0.Origin);
        }
    }

    private void AABBTraceHullBVH(AABBTraceContext trace, ref TraceResult closestHit)
    {
        Span<(Node Node, int Index)> stack = stackalloc (Node Node, int Index)[STACK_SIZE];
        var stackCount = 0;
        stack[stackCount++] = (HullTree[0], 0);

        var ray = trace.Ray;

        while (stackCount > 0)
        {
            var nodeWithIndex = stack[--stackCount];
            var node = nodeWithIndex.Node;

            // Expand node AABB by trace half extents for conservative culling, and skip
            // nodes that cannot contain a hit closer than the best one found so far
            if (!RayIntersectsAABB(ray, node.Min - trace.HalfExtents, node.Max + trace.HalfExtents, out var entryDistance) || entryDistance > closestHit.Distance)
            {
                continue;
            }

            if (node.Type != NodeType.Leaf)
            {
                var leftChild = nodeWithIndex.Index + 1;
                var rightChild = nodeWithIndex.Index + (int)node.ChildOffset;

                var rayIsPositive = ray.Direction[(int)node.Type] >= 0;
                var (nearId, farId) = rayIsPositive
                    ? (leftChild, rightChild)
                    : (rightChild, leftChild);

                // Push far node first so near node is processed first (stack is LIFO)
                stack[stackCount++] = new(HullTree[farId], farId);
                stack[stackCount++] = new(HullTree[nearId], nearId);
                continue;
            }

            // Process hulls in leaf node
            var count = (int)node.ChildOffset;
            var startIndex = (int)node.TriangleOffset;

            for (var i = startIndex; i < startIndex + count; i++)
            {
                var hullIndex = HullIndices[i];
                var hull = Hulls[hullIndex];
                AABBTraceHull(trace, hull, ref closestHit);

                if (closestHit.IsMinimalDistance)
                {
                    return;
                }
            }
        }
    }

    private static void RayIntersectsWithMesh(RayTraceContext ray, PhysicsMeshData mesh, ref TraceResult closestHit)
    {
        Span<(Node Node, int Index)> stack = stackalloc (Node Node, int Index)[STACK_SIZE];
        var stackCount = 0;
        stack[stackCount++] = (mesh.PhysicsTree[0], 0);

        while (stackCount > 0)
        {
            var nodeWithIndex = stack[--stackCount];
            var node = nodeWithIndex.Node;

            // Skip nodes that cannot contain a hit closer than the best one found so far
            if (!RayIntersectsAABB(ray, node.Min, node.Max, out var entryDistance) || entryDistance > closestHit.Distance)
            {
                continue;
            }

            if (node.Type != NodeType.Leaf)
            {
                var leftChild = nodeWithIndex.Index + 1;
                var rightChild = nodeWithIndex.Index + (int)node.ChildOffset;

                var rayIsPositive = ray.Direction[(int)node.Type] >= 0;
                var (nearId, farId) = rayIsPositive
                    ? (leftChild, rightChild)    // Ray going positive direction, traverse left first
                    : (rightChild, leftChild);   // Ray going negative direction, traverse right first

                // Push far node first so near node is processed first (stack is LIFO)
                stack[stackCount++] = new(mesh.PhysicsTree[farId], farId);
                stack[stackCount++] = new(mesh.PhysicsTree[nearId], nearId);
                continue;
            }

            // Check triangles in this leaf node
            var count = (int)node.ChildOffset;
            var startIndex = (int)node.TriangleOffset;

            for (var i = startIndex; i < startIndex + count; i++)
            {
                var triangle = mesh.Triangles[i];
                var v0 = mesh.VertexPositions[triangle.X];
                var v1 = mesh.VertexPositions[triangle.Y];
                var v2 = mesh.VertexPositions[triangle.Z];

                if (!RayIntersectsTriangle(ray, v0, v1, v2, out var intersection))
                {
                    continue;
                }

                // Update if this is the closest hit
                if (intersection.Distance < closestHit.Distance)
                {
                    closestHit = new(true, ray.Origin + ray.Direction * intersection.Distance, intersection.Normal, intersection.Distance, i);
                }
            }
        }
    }

    private static bool RayIntersectsAABB(RayTraceContext ray, Vector3 min, Vector3 max, out float entryDistance)
    {
        // Calculate intersection with AABB using slab method
        var t1 = (min - ray.Origin) * ray.InvDirection;
        var t2 = (max - ray.Origin) * ray.InvDirection;

        var tNear = Vector3.Min(t1, t2);
        var tFar = Vector3.Max(t1, t2);

        var tNearMax = MathF.Max(tNear.X, MathF.Max(tNear.Y, tNear.Z));
        var tFarMin = MathF.Min(tFar.X, MathF.Min(tFar.Y, tFar.Z));

        // Negative when the ray starts inside the box
        entryDistance = tNearMax;

        var intersects = tNearMax <= tFarMin && tFarMin >= 0 && tNearMax <= ray.Length;
        return intersects;
    }

    private static bool RayIntersectsTriangle(RayTraceContext ray, Vector3 v0, Vector3 v1, Vector3 v2, out (float Distance, Vector3 Normal) intersection)
    {
        // Möller–Trumbore ray-triangle intersection algorithm
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Vector3.Cross(ray.Direction, edge2);
        var a = Vector3.Dot(edge1, h);

        intersection = (-1, Vector3.Zero);

        // Ray is parallel to triangle
        if (Math.Abs(a) < Epsilon)
        {
            return false;
        }

        var f = 1.0f / a;
        var s = ray.Origin - v0;
        var u = f * Vector3.Dot(s, h);

        // Ray intersection is outside triangle
        if (u is < 0.0f or > 1.0f)
        {
            return false;
        }

        var q = Vector3.Cross(s, edge1);
        var v = f * Vector3.Dot(ray.Direction, q);

        // Ray intersection is outside triangle
        if (v < 0.0f || u + v > 1.0f)
        {
            return false;
        }

        var t = f * Vector3.Dot(edge2, q);

        // Ray intersection is behind ray origin or beyond ray end
        if (t < 0 || t > ray.Length)
        {
            return false;
        }

        intersection = (t, Vector3.Normalize(Vector3.Cross(edge1, edge2)));
        return true;
    }

    private static void AABBTraceMesh(AABBTraceContext trace, PhysicsMeshData mesh, ref TraceResult closestHit)
    {
        Span<(Node Node, int Index)> stack = stackalloc (Node Node, int Index)[STACK_SIZE];
        var stackCount = 0;
        stack[stackCount++] = (mesh.PhysicsTree[0], 0);

        var ray = trace.Ray;

        while (stackCount > 0)
        {
            var nodeWithIndex = stack[--stackCount];
            var node = nodeWithIndex.Node;

            // Expand node AABB by trace half extents for conservative culling, and skip
            // nodes that cannot contain a hit closer than the best one found so far
            if (!RayIntersectsAABB(ray, node.Min - trace.HalfExtents, node.Max + trace.HalfExtents, out var entryDistance) || entryDistance > closestHit.Distance)
            {
                continue;
            }

            if (node.Type != NodeType.Leaf)
            {
                var leftChild = nodeWithIndex.Index + 1;
                var rightChild = nodeWithIndex.Index + (int)node.ChildOffset;

                var rayIsPositive = ray.Direction[(int)node.Type] >= 0;
                var (nearId, farId) = rayIsPositive
                    ? (leftChild, rightChild)
                    : (rightChild, leftChild);

                // Push far node first so near node is processed first (stack is LIFO)
                stack[stackCount++] = new(mesh.PhysicsTree[farId], farId);
                stack[stackCount++] = new(mesh.PhysicsTree[nearId], nearId);
                continue;
            }

            // Process triangles in leaf node
            var count = (int)node.ChildOffset;
            var startIndex = (int)node.TriangleOffset;

            for (var i = startIndex; i < startIndex + count; i++)
            {
                var triangle = mesh.Triangles[i];
                var v0 = mesh.VertexPositions[triangle.X];
                var v1 = mesh.VertexPositions[triangle.Y];
                var v2 = mesh.VertexPositions[triangle.Z];

                AABBTraceTriangle13AxisSat(trace, v0, v1, v2, ref closestHit);

                if (closestHit.IsMinimalDistance)
                {
                    return;
                }
            }
        }
    }

    private static void AABBTraceTriangle13AxisSat(AABBTraceContext trace, Vector3 v0, Vector3 v1, Vector3 v2, ref TraceResult closestHit)
    {
        // Already overlapping this triangle at the start position - nothing can be closer,
        // so report start-solid and let the callers early-exit
        if (trace.DetectStartSolid && TriangleOverlapsBox(trace.Origin, trace.HalfExtents, v0, v1, v2))
        {
            var startNormal = Vector3.Cross(v1 - v0, v2 - v0);
            var startNormalLength = startNormal.Length();
            startNormal = startNormalLength > Epsilon ? startNormal / startNormalLength : Vector3.UnitZ;

            if (Vector3.Dot(startNormal, trace.Direction) > 0)
            {
                startNormal = -startNormal;
            }

            closestHit = new TraceResult(true, trace.Origin, startNormal, 0f, -1) { StartSolid = true };
            return;
        }

        var hitNormal = Vector3.Zero;

        ReadOnlySpan<Vector3> triangle = [v0, v1, v2];

        float enter = float.NegativeInfinity, exit = float.PositiveInfinity;

        for (int axis = 0; axis < 13; axis++)
        {
            Vector3 axisVector;
            if (axis == 0)
            {
                axisVector = Vector3.Normalize(Vector3.Cross(v1 - v0, v2 - v0));
            }
            else if (axis > 0 && axis < 10)
            {
                var localAxisIndex = axis - 1;

                var triangleEdgeIndex = localAxisIndex / 3;
                var boxAxisIndex = localAxisIndex % 3;

                Vector3 edge = triangle[(triangleEdgeIndex + 1) % 3] - triangle[triangleEdgeIndex];

                axisVector = edge;
                axisVector[boxAxisIndex] = 0;
                axisVector[(boxAxisIndex + 1) % 3] = -edge[(boxAxisIndex + 2) % 3];
                axisVector[(boxAxisIndex + 2) % 3] = edge[(boxAxisIndex + 1) % 3];

                if (Math.Abs(axisVector[(boxAxisIndex + 1) % 3]) < Epsilon && Math.Abs(axisVector[(boxAxisIndex + 2) % 3]) < Epsilon)
                {
                    continue;
                }
            }
            else
            {
                var localAxisIndex = axis - 10;
                axisVector = Vector3.Zero;
                axisVector[localAxisIndex] = 1;
            }

            axisVector = Vector3.Normalize(axisVector);
            axisVector = Vector3.Dot(trace.Direction, axisVector) > 0 ? axisVector : -axisVector;

            //project the triangle onto the axis

            var boxExtent = Vector3.Dot(Vector3.Abs(axisVector), trace.HalfExtents);

            // cosTheta >= 0 because axisVector was flipped toward the ray above.
            // The sweep advances the box projection by cosTheta * Length over the trace.
            var cosTheta = Vector3.Dot(trace.Direction, axisVector);

            float minProj = float.PositiveInfinity, maxProj = float.NegativeInfinity;
            for (var vertexIdx = 0; vertexIdx < 3; vertexIdx++)
            {
                var projection = Vector3.Dot(triangle[vertexIdx] - trace.Origin, axisVector);
                minProj = MathF.Min(minProj, projection);
                maxProj = MathF.Max(maxProj, projection);
            }

            float min, max;
            if (cosTheta > Epsilon)
            {
                var denom = cosTheta * trace.Length;
                min = (minProj - boxExtent) / denom;
                max = (maxProj + boxExtent) / denom;
            }
            else
            {
                // Axis is (near) perpendicular to the sweep: the box's projection onto it
                // does not change over the trace. If the triangle's projection lies outside
                // the box slab [-boxExtent, boxExtent], this is a permanent separating axis.
                if (maxProj < -boxExtent || minProj > boxExtent)
                {
                    return;
                }

                // Otherwise this axis never separates and imposes no constraint on the sweep.
                min = float.NegativeInfinity;
                max = float.PositiveInfinity;
            }

            if (min > enter)
            {
                hitNormal = -axisVector;
                enter = min;
            }
            exit = MathF.Min(exit, max);

            if (enter > exit || exit <= 0)
                return;
        }
        if (enter > 1.0f)
            return;

        var hitDistance = Math.Max(enter * trace.Length, 0);
        var hitPoint = trace.Origin + trace.Direction * hitDistance;

        if (hitDistance < closestHit.Distance)
        {
            closestHit = new TraceResult(true, hitPoint, hitNormal, hitDistance, -1);
        }
    }

    /// <summary>
    /// Triangle vs. axis-aligned box overlap using the 13-axis separating axis test
    /// (Akenine-Möller): 3 box face axes, the triangle plane, and 9 edge cross products.
    /// </summary>
    private static bool TriangleOverlapsBox(Vector3 center, Vector3 halfExtents, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        // Translate so the box is centered at the origin
        v0 -= center;
        v1 -= center;
        v2 -= center;

        // Box face axes: the triangle's bounds must overlap the box extents
        var triMin = Vector3.Min(v0, Vector3.Min(v1, v2));
        var triMax = Vector3.Max(v0, Vector3.Max(v1, v2));

        if (triMin.X > halfExtents.X || triMax.X < -halfExtents.X
            || triMin.Y > halfExtents.Y || triMax.Y < -halfExtents.Y
            || triMin.Z > halfExtents.Z || triMax.Z < -halfExtents.Z)
        {
            return false;
        }

        // Triangle plane: the box must straddle the triangle's plane
        var normal = Vector3.Cross(v1 - v0, v2 - v0);
        var planeDistance = Vector3.Dot(normal, v0);
        var planeRadius = Vector3.Dot(Vector3.Abs(normal), halfExtents);

        if (Math.Abs(planeDistance) > planeRadius)
        {
            return false;
        }

        // Cross products of each triangle edge with each box axis
        Span<Vector3> edges = [v1 - v0, v2 - v1, v0 - v2];

        foreach (var edge in edges)
        {
            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                var axis = axisIndex switch
                {
                    0 => new Vector3(0, -edge.Z, edge.Y),
                    1 => new Vector3(edge.Z, 0, -edge.X),
                    _ => new Vector3(-edge.Y, edge.X, 0)
                };

                var p0 = Vector3.Dot(v0, axis);
                var p1 = Vector3.Dot(v1, axis);
                var p2 = Vector3.Dot(v2, axis);

                var projMin = MathF.Min(p0, MathF.Min(p1, p2));
                var projMax = MathF.Max(p0, MathF.Max(p1, p2));

                var radius = Vector3.Dot(Vector3.Abs(axis), halfExtents);

                if (projMin > radius || projMax < -radius)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private Node[] BuildHullBVH()
    {
        if (Hulls.Length == 0)
        {
            return [];
        }

        if (Hulls.Length == 1)
        {
            // Single hull - create a single leaf node
            return [new Node(Hulls[0].Min, Hulls[0].Max, NodeType.Leaf, 1, 0)];
        }

        // Build BVH recursively
        var nodes = new List<Node>();

        BuildHullBVHRecursive(nodes, HullIndices, 0, Hulls.Length, 0);

        return [.. nodes];
    }

    private void BuildHullBVHRecursive(List<Node> nodes, int[] hullIndices, int start, int count, int depth)
    {
        var nodeIndex = nodes.Count;
        nodes.Add(default); // Reserve space for this node

        // Calculate bounding box for this range
        var min = Hulls[hullIndices[start]].Min;
        var max = Hulls[hullIndices[start]].Max;

        for (var i = 1; i < count; i++)
        {
            var hull = Hulls[hullIndices[start + i]];
            min = Vector3.Min(min, hull.Min);
            max = Vector3.Max(max, hull.Max);
        }

        // If few enough hulls or max depth reached, make a leaf
        const int MaxHullsPerLeaf = 4;
        if (count <= MaxHullsPerLeaf || depth >= STACK_SIZE)
        {
            nodes[nodeIndex] = new Node(
                min,
                max,
                NodeType.Leaf,
                (uint)count,
                (uint)start // Starting index in hullIndices array
            );
            return;
        }

        // Choose split axis based on longest extent
        var extent = max - min;
        var splitAxis = extent.X > extent.Y
            ? (extent.X > extent.Z ? NodeType.SplitX : NodeType.SplitZ)
            : (extent.Y > extent.Z ? NodeType.SplitY : NodeType.SplitZ);

        // Sort hulls along split axis
        var axisIndex = (int)splitAxis;
        Array.Sort(hullIndices, start, count, Comparer<int>.Create((a, b) =>
        {
            var centerA = (Hulls[a].Min[axisIndex] + Hulls[a].Max[axisIndex]) * 0.5f;
            var centerB = (Hulls[b].Min[axisIndex] + Hulls[b].Max[axisIndex]) * 0.5f;
            return centerA.CompareTo(centerB);
        }));

        // Split in the middle
        var leftCount = count / 2;
        var rightCount = count - leftCount;

        // Build left child (immediately after parent)
        var leftChildIndex = nodes.Count;
        BuildHullBVHRecursive(nodes, hullIndices, start, leftCount, depth + 1);

        // Build right child
        var rightChildIndex = nodes.Count;
        BuildHullBVHRecursive(nodes, hullIndices, start + leftCount, rightCount, depth + 1);

        // Update parent node
        var childOffset = (uint)(rightChildIndex - nodeIndex);
        nodes[nodeIndex] = new Node(
            min,
            max,
            splitAxis,
            childOffset,
            0
        );
    }
}
