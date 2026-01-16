using System.Linq;
using System.Runtime.InteropServices;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes
{
    /// <summary>
    /// Represents a convex hull shape.
    /// </summary>
    public readonly struct Hull
    {
        /// <summary>
        /// Represents a plane in the hull.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Plane
        {
            /// <summary>
            /// The plane normal.
            /// </summary>
            public readonly Vector3 Normal;
            /// <summary>
            /// The plane offset such that P: n*x - d = 0
            /// </summary>
            public readonly float Offset;

            /// <summary>
            /// Initializes a new instance of the <see cref="Plane"/> struct.
            /// </summary>
            public Plane(KVObject data)
            {
                Normal = data.GetSubCollection("m_vNormal").ToVector3();
                Offset = data.GetFloatProperty("m_flOffset");
            }
        }

        /// <summary>
        /// Represents a half-edge in the hull mesh.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct HalfEdge
        {
            /// <summary>
            /// Next edge index in CCW circular list around face
            /// </summary>
            public readonly byte Next;
            /// <summary>
            /// The twin edge index.
            /// </summary>
            public readonly byte Twin;
            /// <summary>
            /// The origin vertex index.
            /// </summary>
            public readonly byte Origin;
            /// <summary>
            /// The face index.
            /// </summary>
            public readonly byte Face;

            /// <summary>
            /// Initializes a new instance of the <see cref="HalfEdge"/> struct.
            /// </summary>
            public HalfEdge(KVObject data)
            {
                Next = data.GetByteProperty("m_nNext");
                Twin = data.GetByteProperty("m_nTwin");
                Origin = data.GetByteProperty("m_nOrigin");
                Face = data.GetByteProperty("m_nFace");
            }
        }

        /// <summary>
        /// Represents a face in the hull mesh.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Face
        {
            /// <summary>
            /// Index of first edge in CCW circular list around face
            /// </summary>
            public readonly byte Edge;

            /// <summary>
            /// Initializes a new instance of the <see cref="Face"/> struct.
            /// </summary>
            public Face(KVObject data)
            {
                Edge = data.GetByteProperty("m_nEdge");
            }
        }

        /// <summary>
        /// Represents a node in the hull's Support Vector Machine (SVM) binary tree structure.
        /// The SVM tree is used for efficient spatial queries and collision detection by hierarchically
        /// subdividing the hull's volume using splitting planes. This allows for fast point-in-hull tests,
        /// closest point queries, and ray intersections by traversing the tree and quickly eliminating
        /// irrelevant regions of the hull without testing every face.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public readonly struct RegionNode
        {
            /// <summary>
            /// Flags for SVM tree node types and classification.
            /// </summary>
            [Flags]
            public enum SVMNodeFlags : byte
            {
                /// <summary>
                /// meaning to be determined
                /// </summary>
                LeafTypeB = 0x20,

                /// <summary>
                /// meaning to be determined
                /// </summary>
                LeafTypeA = 0x40,

                /// <summary>
                /// Flag indicating an internal node with child nodes for spatial subdivision.
                /// </summary>
                Internal = 0x80,
            }

            /// <summary>
            /// Splitting plane index for internal nodes. Support plane index for leaf nodes.
            /// </summary>
            public readonly byte PlaneIndex;
            /// <summary>
            /// Left child index. Always zero for internal nodes because the left child is implicitly the next node in the array (currentIndex + 1).
            /// Points to the negative half-space of the splitting plane.
            /// </summary>
            public readonly byte LeftChildIndex;
            /// <summary>
            /// Right child index. For internal nodes, this is an absolute index in the nodes array (not a relative offset).
            /// Points to the positive half-space of the splitting plane. Zero for leaf nodes.
            /// </summary>
            public readonly byte RightChildIndex;

            /// <summary>
            /// Node type and classification flags.
            /// See <see cref="SVMNodeFlags"/> for flag constants.
            /// </summary>
            public readonly SVMNodeFlags Flags;

            /// <summary>
            /// Gets a value indicating whether this node is an internal node (has child nodes for spatial subdivision).
            /// </summary>
            public readonly bool IsInternal => (Flags & SVMNodeFlags.Internal) != 0;
        }

        /// <summary>
        /// Represents a region in the hull with an optional Support Vector Machine (SVM) tree for spatial optimization.
        /// The SVM tree structure enables efficient collision detection and spatial queries by organizing hull geometry
        /// into a binary space partitioning tree.
        /// The tree works by recursively subdividing space with planes, allowing algorithms to quickly eliminate
        /// large portions of the hull without testing individual faces, reducing O(n) operations to O(log n).
        /// </summary>
        public class Region
        {
            /// <summary>
            /// Gets the SVM binary tree nodes that hierarchically subdivide the hull's volume.
            /// Each node represents either a splitting plane (internal node) or a terminal region (leaf node).
            /// Note: The array may contain more nodes than are reachable from the root node (index 0).
            /// Additional nodes may be used for auxiliary data or pre-computed leaf region information.
            /// </summary>
            public RegionNode[] Nodes { get; }

            /// <summary>
            /// Gets the splitting planes.
            /// </summary>
            public Plane[] Planes { get; }


            /// <summary>
            /// Initializes a new instance of the <see cref="Region"/> class.
            /// Parses the region data including SVM tree nodes if present.
            /// </summary>
            /// <param name="data">The KeyValues data containing region information.</param>
            public Region(KVObject data)
            {
                Nodes = MemoryMarshal.Cast<byte, RegionNode>(data.GetArray<byte>("m_Nodes")).ToArray();
                Planes = MemoryMarshal.Cast<byte, Plane>(data.GetArray<byte>("m_Planes")).ToArray();
            }
        }

        /// <summary>
        /// Gets the centroid of the hull.
        /// </summary>
        public Vector3 Centroid { get; }

        /// <summary>
        /// Gets the maximum angular radius for Continuous Collision Detection (CCD).
        /// This value represents the maximum distance from the centroid to any vertex,
        /// used to predict potential collisions during rotational movement.
        /// </summary>
        public float MaxAngularRadius { get; }

        /// <summary>
        /// Gets the region SVM data.
        /// </summary>
        public Region? RegionSVM { get; }

        /// <summary>
        /// Fraction 0..1 of coverage along YZ,ZX,XY sides of AABB
        /// </summary>
        public Vector3 OrthographicAreas { get; }

        /// <summary>
        /// Gets the volume of the hull.
        /// </summary>
        public float Volume { get; }

        //public AABB Bounds { get; set; }
        /// <summary>
        /// Gets the minimum bounds.
        /// </summary>
        public Vector3 Min { get; }
        /// <summary>
        /// Gets the maximum bounds.
        /// </summary>
        public Vector3 Max { get; }
        /// <summary>
        /// Gets the raw data.
        /// </summary>
        public KVObject Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Hull"/> struct.
        /// </summary>
        /// <param name="data">The KeyValues data containing hull information including geometry, bounds, and optional SVM tree.</param>
        public Hull(KVObject data)
        {
            Centroid = data.GetSubCollection("m_vCentroid").ToVector3();
            MaxAngularRadius = data.GetFloatProperty("m_flMaxAngularRadius");
            OrthographicAreas = data.GetSubCollection("m_vOrthographicAreas").ToVector3();
            Volume = data.GetFloatProperty("m_flVolume");

            var bounds = data.GetSubCollection("m_Bounds");
            Min = bounds.GetSubCollection("m_vMinBounds").ToVector3();
            Max = bounds.GetSubCollection("m_vMaxBounds").ToVector3();

            var regionSVM = data.GetSubCollection("m_pRegionSVM");
            RegionSVM = regionSVM == null ? null : new Region(regionSVM);
            Data = data;
        }

        // 2023-11-4: Explicit vertex indices
        private static bool HasExplicitVertexIndices(KVObject data)
            => data.ContainsKey("m_VertexPositions");

        /// <summary>
        /// Hull vertex indices. Hulls can have up to 255 vertices.
        /// </summary>
        /// <remarks>Empty for resources compiled before 2023-11-04.</remarks>
        public Span<byte> GetVertices()
        {
            if (!HasExplicitVertexIndices(Data))
            {
                return [];
            }

            return Data.GetArray<byte>("m_Vertices");
        }

        /// <summary>
        /// Hull vertex positions.
        /// </summary>
        public ReadOnlySpan<Vector3> GetVertexPositions() => ParseVertices(Data);

        /// <summary>
        /// Hull half edges order such that each edge e is followed by its twin e' (e1, e1', e2, e2', ...)
        /// </summary>
        public ReadOnlySpan<HalfEdge> GetEdges()
        {
            if (Data.IsNotBlobType("m_Edges"))
            {
                var edgesArr = Data.GetArray("m_Edges");
                return edgesArr.Select(e => new HalfEdge(e)).ToArray();
            }

            return MemoryMarshal.Cast<byte, HalfEdge>(Data.GetArray<byte>("m_Edges"));
        }

        /// <summary>
        /// Hull faces.
        /// </summary>
        public ReadOnlySpan<Face> GetFaces()
        {
            if (Data.IsNotBlobType("m_Faces"))
            {
                var edgesArr = Data.GetArray("m_Faces");
                return edgesArr.Select(e => new Face(e)).ToArray();
            }

            return MemoryMarshal.Cast<byte, Face>(Data.GetArray<byte>("m_Faces"));
        }

        /// <summary>
        /// Hull face planes with outward pointing normals (n1, -d1, n2, -d2, ...)
        /// </summary>
        public ReadOnlySpan<Plane> GetPlanes()
        {
            if (Data.IsNotBlobType("m_Planes"))
            {
                var planesArr = Data.GetArray("m_Planes");
                return planesArr.Select(p => new Plane(p)).ToArray();
            }

            return MemoryMarshal.Cast<byte, Plane>(Data.GetArray<byte>("m_Planes"));
        }

        internal static ReadOnlySpan<Vector3> ParseVertices(KVObject data)
        {
            if (data.IsNotBlobType("m_Vertices"))
            {
                var verticesArr = data.GetArray("m_Vertices");
                return verticesArr.Select(v => v.ToVector3()).ToArray();
            }

            var verticesName = HasExplicitVertexIndices(data) ? "m_VertexPositions" : "m_Vertices";

            return MemoryMarshal.Cast<byte, Vector3>(data.GetArray<byte>(verticesName));
        }
    }
}
