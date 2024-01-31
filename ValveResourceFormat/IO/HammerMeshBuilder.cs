using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using ValveResourceFormat.IO.ContentFormats.ValveMap;
using static ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;
using static ValveResourceFormat.IO.HammerMeshBuilder;
using System.ComponentModel;
using System.Linq;
using SharpGLTF.Geometry.VertexTypes;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Intrinsics;

namespace ValveResourceFormat.IO
{
    internal class HammerMeshBuilder
    {
        [Flags]
        public enum EdgeFlag
        {
            None = 0x0,
            SoftNormals = 0x1,
            HardNormals = 0x2,
        }

        public class Vertex
        {
            public Vector3 pos;
            public int outGoingHalfEdge = -1;
            public List<int> outgoingEdges = new();
            public List<int> relatedEdges = new();

            public Vertex(Vector3 pos)
            {
                this.pos = pos;
            }
        }

        public class HalfEdge
        {
            public int face = -1;
            public int twin = -1;
            public int next = -1;
            public int prev = -1;
            public int destVert = -1;
            public int origVert = -1;
        }

        public class Face
        {
            public int halfEdge = -1;
            public List<int> indices = new();
            public string mat = "unassigned";
        }

        public List<Vertex> Vertices = new();
        public List<HalfEdge> HalfEdges = new();
        public List<Face> Faces = new();

        public Dictionary<Tuple<int, int>, int> VertsToEdgeDict = new();

        public List<int> facesToRemove = new();

        public void ComputeHalfEdgeStructure()
        {
            //loops trough all the faces and generates only the inner half edges of the face
            for (int i = 0; i < Faces.Count; i++)
            {
                var face = Faces[i];
                int prevHalfEdge = -1;
                int firstHalfEdge = -1;
                int initHalfEdgeCount = HalfEdges.Count;

                //generate inner half edge loop
                for (int j = 0; j < face.indices.Count; j++)
                {
                    var v1idx = face.indices[j];
                    var v2idx = face.indices[(j + 1) % face.indices.Count];

                    var v1 = Vertices[v1idx];
                    var v2 = Vertices[v2idx];

                    var heIndex = initHalfEdgeCount + j;

                    //if the current internal half edge were trying to add already exists something is wrong
                    //break out of the loop (discard face)
                    try
                    {
                        VertsToEdgeDict.Add(new Tuple<int, int>(v1idx, v2idx), heIndex);
                    }
                    catch (ArgumentException)
                    {
                        Faces.RemoveAt(i);
                        for (int k = j - 1; k >= 0; k--)
                        {
                            var heToRemoveIndex = initHalfEdgeCount + k;
                            var heToRemove = HalfEdges[heToRemoveIndex];
                            Vertices[heToRemove.origVert].relatedEdges.Remove(heToRemoveIndex);
                            Vertices[heToRemove.destVert].relatedEdges.Remove(heToRemoveIndex);
                            HalfEdges.RemoveAt(heToRemoveIndex);

                            if (Vertices[heToRemove.origVert].relatedEdges.Count == 0)
                            {
                                Vertices[heToRemove.origVert].outGoingHalfEdge = -1;
                                Vertices[heToRemove.origVert].pos = new Vector3(-1);
                            }

                            if (Vertices[heToRemove.destVert].relatedEdges.Count == 0)
                            {
                                Vertices[heToRemove.destVert].outGoingHalfEdge = -1;
                                Vertices[heToRemove.destVert].pos = new Vector3(-1);
                            }
                        }

                        i--;

                        break;
                    }

                    var he = new HalfEdge { origVert = v1idx, destVert = v2idx, face = i };

                    //if the previous half edge is -1 it means this is the start of the new face so remember the first half edge added
                    //if the previous half edge is not -1, then connect the current half edge with its previous one
                    if (prevHalfEdge == -1)
                    {
                        firstHalfEdge = heIndex;
                    }
                    else
                    {
                        he.prev = prevHalfEdge;
                        HalfEdges[prevHalfEdge].next = heIndex;
                    }

                    prevHalfEdge = heIndex;

                    //if we are on the last inner half edge of the face, connect it with the first
                    if (j == face.indices.Count - 1)
                    {
                        he.next = firstHalfEdge;
                        HalfEdges[firstHalfEdge].prev = heIndex;
                    }

                    HalfEdges.Add(he);
                    v1.outGoingHalfEdge = heIndex;
                    face.halfEdge = heIndex;

                    //build vertex-halfedge relationships for later use
                    if (!v1.relatedEdges.Contains(heIndex))
                    {
                        v1.relatedEdges.Add(heIndex);
                    }

                    if (!v2.relatedEdges.Contains(heIndex))
                    {
                        v2.relatedEdges.Add(heIndex);
                    }

                }
            }

            for (var i = 0; i < facesToRemove.Count; i++)
            {
                Faces.RemoveAt(facesToRemove[i]);
            }

            //link twins and generate outer edges
            var outerEdges = new List<HalfEdge>();

            for (var i = 0; i < HalfEdges.Count; i++)
            {
                var he = HalfEdges[i];
                var vert = Vertices[he.destVert];

                //loop trough all the related edges of destination vert of this half edge
                foreach (var potentialTwinId in vert.relatedEdges)
                {
                    var potentialTwin = HalfEdges[potentialTwinId];

                    //if the destination vertex of our edge is the same as the origin vertex of the potential twin and
                    //if the origin vertex of our edge is the same as the destination vertex of the potential twin and
                    //if the potential twin doesn't already have a twin, we can be sure this is the correct twin
                    if (he.destVert == potentialTwin.origVert && he.origVert == potentialTwin.destVert && potentialTwin.twin == -1)
                    {
                        he.twin = potentialTwinId;
                        potentialTwin.twin = i;
                    }
                }

                //if the half edge hasnt found a twin, then it means it's twin has to be a boundary half edge
                //generate a bondary half edge, and set twin relationships
                if (he.twin == -1)
                {
                    var outerHalfEdge = new HalfEdge();
                    he.twin = HalfEdges.Count + outerEdges.Count;
                    outerHalfEdge.twin = i;
                    outerHalfEdge.destVert = he.origVert;
                    outerHalfEdge.origVert = he.destVert;
                    outerEdges.Add(outerHalfEdge);
                }
            }
            //adding the outers to the main half edge list here because if we were to add them in the loop above as they are generated
            //HalfEdges would change size as were looping over it, and thats bad
            HalfEdges.AddRange(outerEdges);

            //loop trough all outer half edges, and find the correct next/prev outer halfedges then link them
            foreach (var halfEdge in outerEdges)
            {
                var potentialNextOuters = new List<HalfEdge>();

                for (var i = 0; i < outerEdges.Count; i++)
                {
                    var potentialOuterNext = outerEdges[i];

                    var potentialOuterNextId = (HalfEdges.Count - outerEdges.Count) + i;

                    //if the potential outer next origin vert is our destination vert
                    //we found a valid next, add it to the list
                    if (potentialOuterNext.origVert == halfEdge.destVert)
                    {
                        potentialNextOuters.Add(potentialOuterNext);
                    }

                    var potentialOuterPrev = outerEdges[i];

                    //if the potential outer prev doesnt have a next halfedge, and its destination vert is our origin vert, and we don't already have a prev
                    //we found a valid prev, connect them
                    if (potentialOuterPrev.next == -1 && potentialOuterPrev.destVert == halfEdge.origVert && halfEdge.prev == -1)
                    {
                        halfEdge.prev = potentialOuterNextId;
                        potentialOuterNext.next = HalfEdges.IndexOf(halfEdge);
                    }

                }

                //if the list is bigger than 1, go trough the list of potential next edges, this is where it gets funky
                if (potentialNextOuters.Count == 1)
                {
                    halfEdge.next = HalfEdges.IndexOf(potentialNextOuters[0]);
                    potentialNextOuters[0].prev = HalfEdges.IndexOf(halfEdge);
                }
                //find the oppsite edge of the current potential next (edge that points directly into it)
                //then circle around its destination vertex until a suitable next edge is found
                else if (potentialNextOuters.Count > 1)
                {
                    HalfEdge oppositeEdge = null;
                    for (var i = 0; i < outerEdges.Count; i++)
                    {
                        if (outerEdges[i].destVert == halfEdge.destVert && outerEdges[i].face == -1 && halfEdge != outerEdges[i])
                        {
                            oppositeEdge = outerEdges[i];
                        }
                    }

                    var outerCandidate = HalfEdges[oppositeEdge.twin];
                    do
                    {
                        outerCandidate = HalfEdges[HalfEdges[outerCandidate.prev].twin];

                    } while(outerCandidate.face != -1);


                    if (outerCandidate.face == -1 && outerCandidate.origVert == halfEdge.destVert)
                    {
                        halfEdge.next = HalfEdges.IndexOf(outerCandidate);
                        outerCandidate.prev = HalfEdges.IndexOf(halfEdge);
                    }
                }
            }
        }

        public CDmePolygonMesh GenerateMesh()
        {
            ComputeHalfEdgeStructure();

            var mesh = new CDmePolygonMesh();

            var faceMaterialIndices = CreateStream<Datamodel.IntArray, int>(8, "materialindex:0");
            var faceFlags = CreateStream<Datamodel.IntArray, int>(3, "flags:0");
            mesh.FaceData.Streams.Add(faceMaterialIndices);
            mesh.FaceData.Streams.Add(faceFlags);

            var textureCoords = CreateStream<Datamodel.Vector2Array, Vector2>(1, "texcoord:0");
            var normals = CreateStream<Datamodel.Vector3Array, Vector3>(1, "normal:0");
            var tangent = CreateStream<Datamodel.Vector4Array, Vector4>(1, "tangent:0");
            mesh.FaceVertexData.Streams.Add(textureCoords);
            mesh.FaceVertexData.Streams.Add(normals);
            mesh.FaceVertexData.Streams.Add(tangent);

            var vertexPositions = CreateStream<Datamodel.Vector3Array, Vector3>(3, "position:0");
            mesh.VertexData.Streams.Add(vertexPositions);

            var edgeFlags = CreateStream<Datamodel.IntArray, int>(3, "flags:0");
            mesh.EdgeData.Streams.Add(edgeFlags);

            foreach (var Vertex in Vertices)
            {
                var vertexDataIndex = mesh.VertexData.Size;
                mesh.VertexEdgeIndices.Add(Vertex.outGoingHalfEdge);
                mesh.VertexDataIndices.Add(vertexDataIndex);
                mesh.VertexData.Size++;

                vertexPositions.Data.Add(Vertex.pos);
            }

            for (var i = 0; i < HalfEdges.Count / 2; i++)
            {
                mesh.EdgeData.Size++;
                edgeFlags.Data.Add((int)EdgeFlag.None);
            }

            int prevHalfEdge = -1;
            for (var i = 0; i < HalfEdges.Count; i++)
            {
                var halfEdge = HalfEdges[i];
                //EdgeData refers to a single edge, so its half of the total
                //of half edges and both halfs of the edge should have the same EdgeData Index
                if (halfEdge.twin == prevHalfEdge)
                {
                    mesh.EdgeDataIndices.Add(prevHalfEdge / 2);
                }
                else
                {
                    mesh.EdgeDataIndices.Add(i / 2);
                }

                mesh.EdgeVertexIndices.Add(halfEdge.destVert);
                mesh.EdgeOppositeIndices.Add(halfEdge.twin);
                mesh.EdgeNextIndices.Add(halfEdge.next);
                mesh.EdgeFaceIndices.Add(halfEdge.face);
                mesh.EdgeVertexDataIndices.Add(i);

                prevHalfEdge = i;

                mesh.FaceVertexData.Size += 1;

                var normal = new Vector3(0, 0, 1);
                if (halfEdge.face != -1)
                {
                    normal = CalculateNormal(halfEdge.next);
                    var tangents = CalculateTangentFromNormal(normal);
                    normals.Data.Add(normal);
                    tangent.Data.Add(tangents);

                }
                else
                {
                    normals.Data.Add(new Vector3(0, 0, 0));
                    tangent.Data.Add(new Vector4(0, 0, 0, 0));
                }

                var startVertex = Vertices[halfEdge.destVert];

                textureCoords.Data.Add(CalculateTriplanarUVs(startVertex.pos, normal));
            }

            foreach (var Face in Faces)
            {
                var faceDataIndex = mesh.FaceData.Size;
                mesh.FaceDataIndices.Add(faceDataIndex);
                mesh.FaceData.Size++;

                var mat = Face.mat;
                var materialIndex = mesh.Materials.IndexOf(mat);
                if (materialIndex == -1 && mat != null)
                {
                    materialIndex = mesh.Materials.Count;
                    faceMaterialIndices.Data.Add(materialIndex);
                    mesh.Materials.Add(mat);
                }

                faceFlags.Data.Add(0);

                mesh.FaceEdgeIndices.Add(Face.halfEdge);
            }


            AddSubdivisionStuff(mesh);

            return mesh;
        }

        public void AddVertex(Vertex Vertex)
        {
            Vertices.Add(Vertex);
        }

        public void AddFace(Face face)
        {
            Faces.Add(face);
        }

        public void AddFace(int[] indices, string material = "unassigned")
        {
            var face = new Face();
            face.indices.AddRange(indices);
            face.mat = material;
            AddFace(face);
        }

        public void AddFace(List<int> indices, string material = "unassigned")
        {
            AddFace(indices.ToArray(), material);
        }

        public void AddFace(int v1, int v2, int v3, string material = "unassigned")
        {
            AddFace(new int[3] { v1, v2, v3 }, material);
        }

        private bool VerifyIndicesWithinBounds(IEnumerable<int> indices)
        {
            foreach (var index in indices)
            {
                if (index < 0 || index >= Vertices.Count)
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddSubdivisionStuff(CDmePolygonMesh mesh, int count = 8)
        {
            for (int i = 0; i < count; i++)
            {
                mesh.SubdivisionData.SubdivisionLevels.Add(0);
            }
        }

        private Vector3 CalculateNormal(int i)
        {
            var normalHalfEdges = new int[] { i, HalfEdges[i].next, HalfEdges[i].prev };
            var v1 = Vertices[HalfEdges[normalHalfEdges[0]].origVert].pos;
            var v2 = Vertices[HalfEdges[normalHalfEdges[1]].origVert].pos;
            var v3 = Vertices[HalfEdges[normalHalfEdges[2]].origVert].pos;

            var normal = Vector3.Normalize(Vector3.Cross(v2 - v1, v3 - v1));

            return normal;
        }

        private static Vector4 CalculateTangentFromNormal(Vector3 normal)
        {
            Vector3 tangent1 = Vector3.Cross(normal, new Vector3(0, 1, 0));
            Vector3 tangent2 = Vector3.Cross(normal, new Vector3(0, 0, 1));
            return new Vector4(tangent1.Length() > tangent2.Length() ? tangent1 : tangent2, 1.0f);
        }

        private static Vector2 CalculateTriplanarUVs(Vector3 vertexPos, Vector3 normal, float textureScale = 0.03125f)
        {
            var weights = Vector3.Abs(normal);
            var top = new Vector2(vertexPos.Y, vertexPos.X) * weights.Z;
            var front = new Vector2(vertexPos.X, vertexPos.Z) * weights.Y;
            var side = new Vector2(vertexPos.Y, vertexPos.Z) * weights.X;

            var UV = (top + front + side);

            return UV * textureScale;
        }


        public static CDmePolygonMeshDataStream<T> CreateStream<TArray, T>(int dataStateFlags, string name, params T[] data)
            where TArray : Datamodel.Array<T>, new()
        {

            var dmArray = new TArray();
            foreach (var item in data)
            {
                dmArray.Add(item);
            }

            var stream = new CDmePolygonMeshDataStream<T>
            {
                Name = name,
                StandardAttributeName = name[..^2],
                SemanticName = name[..^2],
                SemanticIndex = 0,
                VertexBufferLocation = 0,
                DataStateFlags = dataStateFlags,
                SubdivisionBinding = null,
                Data = dmArray
            };

            return stream;
        }
    }
}
