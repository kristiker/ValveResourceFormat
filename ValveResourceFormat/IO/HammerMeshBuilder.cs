using System;
using System.Collections.Generic;
using System.Numerics;
using ValveResourceFormat.IO.ContentFormats.ValveMap;
using static ValveResourceFormat.IO.HammerMeshBuilder;
using ValveResourceFormat.ResourceTypes.RubikonPhysics;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization;
using ValveResourceFormat.IO.ContentFormats.DmxModel;


namespace ValveResourceFormat.IO
{
    internal class HalfEdgeMeshModifier
    {
        private List<HalfEdgeModification> HalfEdgeModifications = new();
        private List<VertsToEdgeDictModification> VertsToEdgeDictModifications = new();
        private List<HalfEdgesModification> HalfEdgesModifications = new();
        private List<VertexModification> VertexModifications = new();

        public enum HalfEdgePropertyNames
        {
            face,
            prev,
            next
        }

        public enum VertexPropertyNames
        {
            outGoingHalfEdge,
            relatedEdge
        }

        public HammerMeshBuilder.Face Face;

        private HammerMeshBuilder builderRef;

        public HalfEdgeMeshModifier(HammerMeshBuilder c_builderRef)
        {
            builderRef = c_builderRef;
        }

        // remember half edge states
        internal class HalfEdgeModification
        {
            public HammerMeshBuilder.HalfEdge he;
            public HalfEdgePropertyNames propertyName;
            public int property;

            public HalfEdgeModification(HammerMeshBuilder.HalfEdge c_he, HalfEdgePropertyNames c_propertyName, int c_property)
            {
                he = c_he;
                propertyName = c_propertyName;
                property = c_property;
            }
        }
        internal void RememberHalfEdgeModification(HammerMeshBuilder.HalfEdge he, HalfEdgePropertyNames propertyName, int property)
        {
            HalfEdgeModifications.Add(new HalfEdgeModification(he, propertyName, property));
        }

        public void ChangeHalfEdgeFace(HammerMeshBuilder.HalfEdge he, int face)
        {
            if (he.face == face)
            {
                return;
            }

            RememberHalfEdgeModification(he, HalfEdgePropertyNames.face, he.face);
            he.face = face;
        }

        public void ChangeHalfEdgePrev(HammerMeshBuilder.HalfEdge he, int prev)
        {
            if (he.prev == prev)
            {
                return;
            }

            RememberHalfEdgeModification(he, HalfEdgePropertyNames.prev, he.prev);
            he.prev = prev;
        }

        public void ChangeHalfEdgeNext(HammerMeshBuilder.HalfEdge he, int next)
        {
            if (he.next == next)
            {
                return;
            }

            RememberHalfEdgeModification(he, HalfEdgePropertyNames.next, he.next);
            he.next = next;
        }

        // remember vertxtoedgedict states
        internal class VertsToEdgeDictModification
        {
            public Tuple<int, int> heKey;

            public VertsToEdgeDictModification(Tuple<int, int> c_heKey)
            {
                heKey = c_heKey;
            }
        }

        internal void RememberVertsToEdgeDictModification(Tuple<int, int> heKey)
        {
            VertsToEdgeDictModifications.Add(new VertsToEdgeDictModification(heKey));
        }

        public bool TryAddVertsToEdgeDict(Tuple<int, int> heKey, int heIndex)
        {
            if (builderRef.VertsToEdgeDict.TryAdd(heKey, heIndex))
            {
                RememberVertsToEdgeDictModification(heKey);
                return true;
            }
            else
            {
                return false;
            }
        }

        // remember HalfEdges states
        internal class HalfEdgesModification
        {
            public HammerMeshBuilder.HalfEdge he;

            public HalfEdgesModification(HammerMeshBuilder.HalfEdge c_he)
            {
                he = c_he;
            }
        }

        internal void RememberHalfEdgesModification(HammerMeshBuilder.HalfEdge he)
        {
            HalfEdgesModifications.Add(new HalfEdgesModification(he));
        }

        public void AddHalfEdgeToHalfEdgesList(HammerMeshBuilder.HalfEdge he)
        {
            RememberHalfEdgesModification(he);
            builderRef.HalfEdges.Add(he);
        }

        // remember Vertex states
        internal class VertexModification
        {
            public Vertex vertex;
            public int outGoingHalfEdge;
            public int relatedEdge;
            public VertexPropertyNames proprtyName;
            public int proprty;

            public VertexModification(Vertex c_vertex, VertexPropertyNames c_proprtyName, int c_proprty)
            {
                vertex = c_vertex;
                proprtyName = c_proprtyName;
                proprty = c_proprty;
            }
        }

        internal void RememberVertexModification(Vertex vertex, VertexPropertyNames proprtyName, int proprty)
        {
            VertexModifications.Add(new VertexModification(vertex, proprtyName, proprty));
        }

        public void SetVertexOutGoing(Vertex vertex, int outGoingHalfEdge)
        {
            RememberVertexModification(vertex, VertexPropertyNames.outGoingHalfEdge, vertex.outGoingHalfEdge);
            vertex.outGoingHalfEdge = outGoingHalfEdge;
        }

        public void AddVertexRelatedEdge(Vertex vertex, int he)
        {
            RememberVertexModification(vertex, VertexPropertyNames.relatedEdge, he);
            vertex.relatedEdges.Add(he);
        }

        // roll back
        public void RollBack(string error)
        {
            builderRef.FacesRemoved++;

            HalfEdgeModifications.Reverse();
            VertsToEdgeDictModifications.Reverse();
            HalfEdgesModifications.Reverse();
            VertexModifications.Reverse();

            foreach (var VertexModification in VertexModifications)
            {
                switch (VertexModification.proprtyName)
                {
                    case VertexPropertyNames.outGoingHalfEdge:
                        VertexModification.vertex.outGoingHalfEdge = VertexModification.proprty;
                        break;

                    case VertexPropertyNames.relatedEdge:
                        VertexModification.vertex.relatedEdges.Remove(VertexModification.proprty);
                        break;
                }
            }

            foreach (var HalfEdgesModification in HalfEdgesModifications)
            {
                builderRef.HalfEdges.RemoveAt(HalfEdgesModification.he.id);
                HalfEdgesModification.he.skipInRollback = true;
            }

            foreach (var VertsToEdgeDictModification in VertsToEdgeDictModifications)
            {
                builderRef.VertsToEdgeDict.Remove(VertsToEdgeDictModification.heKey);
            }

            foreach (var HalfEdgeModification in HalfEdgeModifications)
            {
                var he = HalfEdgeModification.he;

                if (he.skipInRollback)
                    continue;

                switch (HalfEdgeModification.propertyName)
                {
                    case HalfEdgePropertyNames.face:
                        he.face = HalfEdgeModification.property;
                        break;

                    case HalfEdgePropertyNames.prev:
                        he.prev = HalfEdgeModification.property;
                        break;

                    case HalfEdgePropertyNames.next:
                        he.next = HalfEdgeModification.property;
                        break;
                }
            }

            Console.WriteLine("HammerMeshBuilder error: " + error + " Extracting face...");

            foreach (var vertex in Face.indices)
            {
                builderRef.AddVertex(new Vertex(builderRef.Vertices[vertex].pos));
            }

            var vertCount = builderRef.Vertices.Count;

            var newIndices = new List<int>();

            for (var i = vertCount - Face.indices.Count; i < vertCount; i++)
            {
                newIndices.Add(i);
            }

            builderRef.AddFace(newIndices);
        }

        public void Reset()
        {
            HalfEdgeModifications.Clear();
            VertsToEdgeDictModifications.Clear();
            HalfEdgesModifications.Clear();
            VertexModifications.Clear();
            Face = null;
        }
    }

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
            public List<int> relatedEdges = new();
            public Vector2 uv;
            public bool hasExistingUvs;

            public Vertex(Vector3 pos, Vector2 uv = new Vector2())
            {
                this.pos = pos;
                this.uv = uv;
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
            public int id = -1;
            public bool skipInRollback;
            public bool overridOuter;
        }

        public class Face
        {
            public int halfEdge = -1;
            public List<int> indices = [];
            public string mat = "unassigned";
            public bool toExtract;
        }

        public int FacesRemoved;
        public int OriginalFaceCount;

        public List<Vertex> Vertices = [];
        public List<HalfEdge> HalfEdges = [];
        public List<Face> Faces = [];

        public Dictionary<Tuple<int, int>, int> VertsToEdgeDict = [];

        public HalfEdgeMeshModifier halfEdgeModifier;

        public HammerMeshBuilder()
        {
            halfEdgeModifier = new HalfEdgeMeshModifier(this);
        }

        public CDmePolygonMesh GenerateMesh()
        {
            if (FacesRemoved > 0)
                Console.WriteLine($"HammerMeshBuilder: extracted '{FacesRemoved}' of '{OriginalFaceCount - FacesRemoved}' faces");

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

                var normal = Vector3.UnitZ;
                if (halfEdge.face != -1)
                {
                    normal = CalculateNormal(halfEdge.next);
                    var tangents = CalculateTangentFromNormal(normal);
                    normals.Data.Add(normal);
                    tangent.Data.Add(tangents);

                }
                else
                {
                    normals.Data.Add(normal);
                    tangent.Data.Add(Vector4.Zero);
                }

                var startVertex = Vertices[halfEdge.destVert];

                if (startVertex.hasExistingUvs)
                {
                    textureCoords.Data.Add(new Vector2(startVertex.uv[0], startVertex.uv[1]));
                }
                else
                {
                    textureCoords.Data.Add(CalculateTriplanarUVs(startVertex.pos, normal));
                }
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

        public IEnumerable<int> VertexCirculator(int heidx, string direction)
        {
            if (heidx < 0) { yield break; }
            int h = heidx;
            int count = 0;
            do
            {
                yield return h;

                if (direction == "into")
                {
                    h = HalfEdges[HalfEdges[h].twin].prev;
                }

                if (direction == "away")
                {
                    h = HalfEdges[HalfEdges[h].twin].next;
                }

                if (h < 0) { throw new InvalidOperationException("Vertex circulator returned an invalid half edge index"); }
                if (count++ > 999) { throw new InvalidOperationException("Runaway vertex circulator"); }
            }
            while (h != heidx);
        }


        public void AddFace(Face face)
        {
            halfEdgeModifier.Reset();
            halfEdgeModifier.Face = face;

            List<HalfEdge> newBoundaryList = new();

            OriginalFaceCount++;

            var IndexCount = face.indices.Count;

            // don't allow degenerate faces
            if (IndexCount < 3)
            {
                Console.WriteLine($"HammerMeshBuilder error: failed to add face '{Faces.Count}', face has less than 3 vertices.");
                FacesRemoved++;
                return;
            }

            var firstinnerFaceHeidx = -1;
            var lastinnerFaceHeidx = -1;

            for (int i = 0; i < face.indices.Count; i++)
            {
                var faceidx = Faces.Count;
                var v1idx = face.indices[i];
                var v2idx = face.indices[(i + 1) % face.indices.Count]; // will cause v2idx to wrap around
                var v1 = Vertices[v1idx];
                var v2 = Vertices[v2idx];

                var innerHeIndex = HalfEdges.Count; // since we haven't yet added this edge, its index is just the count of the list + newHalfEdges list
                var boundaryHeIndex = HalfEdges.Count + 1;
                var innerHeKey = new Tuple<int, int>(v1idx, v2idx);
                var boundaryHeKey = new Tuple<int, int>(v2idx, v1idx);
                HalfEdge innerHe = null;
                HalfEdge boundaryHe = null;

                if (v1.outGoingHalfEdge != -1)
                {
                    var hasBoundary = false;
                    foreach (var he in v1.relatedEdges)
                    {
                        if (HalfEdges[he].face == -1)
                        {
                            hasBoundary = true;
                        }
                    }
                    if (!hasBoundary)
                    {
                        halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, Face specified a vertex which had edges attached, but none that were open.");
                        return;
                    }
                }

                if (v2.outGoingHalfEdge != -1)
                {
                    var hasBoundary = false;
                    foreach (var he in v2.relatedEdges)
                    {
                        if (HalfEdges[he].face == -1)
                        {
                            hasBoundary = true;
                        }
                    }
                    if (!hasBoundary)
                    {
                        halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, Face specified a vertex which had edges attached, but none that were open.");
                        return;
                    }
                }

                // check if the inner already exists
                if (!halfEdgeModifier.TryAddVertsToEdgeDict(innerHeKey, innerHeIndex))
                {
                    // failed to add the key to the dict, this either means we hit a set of inner twins
                    // or that the face being added is wrong

                    VertsToEdgeDict.TryGetValue(innerHeKey, out innerHeIndex); // get the half edge that already exists and assign to innerHeIndex

                    innerHe = HalfEdges[innerHeIndex];

                    // already existing half edge doesn't have a face, which means this is a boundary, don't add a new half edge
                    // but instead just set its face to be the current face (turning it into an inner)
                    if (innerHe.face == -1)
                    {
                        if (lastinnerFaceHeidx != -1)
                        {
                            if (HalfEdges[lastinnerFaceHeidx].overridOuter)
                            {
                                if (innerHe.prev != lastinnerFaceHeidx && HalfEdges[lastinnerFaceHeidx].next != innerHeIndex)
                                {
                                    halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, Face specified two edges that are connected by a vertex but have or more existing edges separating them.");
                                    return;
                                }
                            }

                            if (i == face.indices.Count - 1)
                            {
                                if (HalfEdges[firstinnerFaceHeidx].overridOuter)
                                {
                                    if (innerHe.next != firstinnerFaceHeidx && HalfEdges[firstinnerFaceHeidx].prev != innerHeIndex)
                                    {
                                        halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, Face specified two edges that are connected by a vertex but have or more existing edges separating them.");
                                        return;
                                    }
                                }
                            }
                        }

                        halfEdgeModifier.ChangeHalfEdgeFace(innerHe, faceidx);
                        innerHe.overridOuter = true;
                    }
                    else
                    {
                        halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, Face specified an edge which already had two faces attached");
                        return;
                    }
                }
                else
                {
                    innerHe = new HalfEdge
                    {
                        face = faceidx,
                        origVert = v1idx,
                        destVert = v2idx,
                        twin = boundaryHeIndex,
                        id = innerHeIndex
                    };

                    boundaryHe = new HalfEdge
                    {
                        origVert = v2idx,
                        destVert = v1idx,
                        twin = innerHeIndex,
                        id = boundaryHeIndex
                    };

                    halfEdgeModifier.TryAddVertsToEdgeDict(boundaryHeKey, boundaryHeIndex);

                    halfEdgeModifier.AddHalfEdgeToHalfEdgesList(innerHe);
                    halfEdgeModifier.AddHalfEdgeToHalfEdgesList(boundaryHe);

                    halfEdgeModifier.AddVertexRelatedEdge(v1, innerHeIndex);
                    halfEdgeModifier.AddVertexRelatedEdge(v1, boundaryHeIndex);
                    halfEdgeModifier.AddVertexRelatedEdge(v2, innerHeIndex);
                    halfEdgeModifier.AddVertexRelatedEdge(v2, boundaryHeIndex);

                    halfEdgeModifier.SetVertexOutGoing(v2, boundaryHeIndex);

                    newBoundaryList.Add(boundaryHe);
                }

                //link inners
                halfEdgeModifier.ChangeHalfEdgePrev(innerHe, lastinnerFaceHeidx);
                halfEdgeModifier.ChangeHalfEdgeNext(innerHe, firstinnerFaceHeidx);

                // link current inner with the previous half edge
                if (lastinnerFaceHeidx != -1)
                {
                    var lastinnerFaceHe = HalfEdges[lastinnerFaceHeidx];
                    halfEdgeModifier.ChangeHalfEdgeNext(lastinnerFaceHe, innerHeIndex);
                }

                // if i is 0 this is the first inner of the face, remember it
                if (i == 0)
                {
                    firstinnerFaceHeidx = innerHeIndex;
                    face.halfEdge = firstinnerFaceHeidx;
                }

                // if i is max indices - 1 it means this is the end of the loop, link current inner with the first
                if (i == face.indices.Count - 1)
                {
                    var firstinnerFaceHe = HalfEdges[firstinnerFaceHeidx];
                    halfEdgeModifier.ChangeHalfEdgePrev(firstinnerFaceHe, innerHeIndex);
                    halfEdgeModifier.ChangeHalfEdgeNext(innerHe, firstinnerFaceHeidx);
                }

                lastinnerFaceHeidx = innerHeIndex;
            }

            // link boundary half edges
            // TODO: it would be nice to find a way to generalize this code so theres no code duplication for
            // linking prev/next boundaries, maybe even find a way to only have to link next with some smart logic
            // because right now if you have a triangle, processing 2/3 of its edges will also link the 3rd edge
            // but then the algorithm still goes over the 3rd edge linking it, couldn't figure out nice logic to avoid that
            // but i have obsessed over this too much for now
            foreach (var boundary in newBoundaryList)
            {
                var origVert = Vertices[boundary.origVert];
                var destVert = Vertices[boundary.destVert];
                var boundaryIdx = boundary.id;

                //
                // link prev boundary
                //
                var totalPotentialPrevBoundary = 0;
                var potentialBoundaryWithAnInnerAsNextIdx = -1;
                var oppositePrevBoundary = -1;

                foreach (var heIdx in origVert.relatedEdges)
                {
                    var he = HalfEdges[heIdx];

                    // dont loop over ourselves
                    if (he == boundary)
                    {
                        continue;
                    }

                    // only loop over boundaries
                    if (he.face != -1)
                    {
                        continue;
                    }

                    // TODO: im sure there has to be a smarter way of writing this code, since currently we loop over all boundaries associated with a vertex
                    // but we only end up actually using less than half, the issue is that vertex circulation doesn't work here
                    // because while building the data structure, some half edges won't have data filled out yet, causing it to fail

                    // store the edge that opposes our current edge (they point into eachother)
                    // this will be useful for some trickery later
                    if (he.origVert == boundary.origVert)
                    {
                        oppositePrevBoundary = heIdx;
                    }

                    // if the destvert of the half edge related with the vertex is the same as our origin vertex 
                    // and it doesn't have a next (valid prev)
                    // and it's next HAS A FACE (boundary that got overriden as inner)
                    // store the boundary with an inner as next for later
                    if (he.destVert == boundary.origVert)
                    {
                        totalPotentialPrevBoundary++;

                        if (he.next != -1)
                        {
                            if (HalfEdges[he.next].face != -1)
                            {
                                potentialBoundaryWithAnInnerAsNextIdx = heIdx;
                            }
                        }
                    }
                }

                // more than two prev boundaries to choose here means we got more than 4 half edges on one vertex, invalid
                if (totalPotentialPrevBoundary > 2)
                {
                    halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, a vertex specified by the face has multiple boundary edges but shares no existing edge");
                    return;
                }

                // TODO: im sure theres a way to unify this logic, but i havent found one

                if (totalPotentialPrevBoundary == 2)
                {
                    // if totalPotentialPrevBoundary == 2 and we got a boundary that has an inner as next
                    // it means at least one edge merge happened (two faces sharing an edge) while adding this face
                    // we can use this information to get our prev for this boundary, since the boundary that has a next that has a face
                    // will always be our prev
                    if (potentialBoundaryWithAnInnerAsNextIdx != -1)
                    {
                        halfEdgeModifier.ChangeHalfEdgePrev(boundary, potentialBoundaryWithAnInnerAsNextIdx);
                        halfEdgeModifier.ChangeHalfEdgeNext(HalfEdges[potentialBoundaryWithAnInnerAsNextIdx], boundaryIdx);
                    }
                    // if no face merge happened, we can still get prev, but its a bit trickier
                    // we have to circulate away from the opposite boundary, until we find another boundary
                    /// checking for twin here because of how the circulator works
                    else
                    {
                        foreach (var heidx in VertexCirculator(oppositePrevBoundary, "away"))
                        {
                            var possiblePrev = HalfEdges[heidx];
                            if (HalfEdges[possiblePrev.twin].face == -1)
                            {
                                halfEdgeModifier.ChangeHalfEdgePrev(boundary, possiblePrev.twin);
                                halfEdgeModifier.ChangeHalfEdgeNext(HalfEdges[possiblePrev.twin], boundaryIdx);
                                break;
                            }
                        }
                    }
                }

                // if there is only one potential prev, it means that this either a single separate face being added
                // or that this is the junction of two faces being merged 
                if (totalPotentialPrevBoundary == 1)
                {
                    // if this is just a single face, we can hard code the prev
                    if (potentialBoundaryWithAnInnerAsNextIdx == -1)
                    {
                        var prev = HalfEdges[HalfEdges[boundary.twin].next].twin;
                        halfEdgeModifier.ChangeHalfEdgePrev(boundary, prev);
                        halfEdgeModifier.ChangeHalfEdgeNext(HalfEdges[prev], boundaryIdx);
                    }
                    // else if its two faces joining, just use the boundary with inner as next as before
                    else
                    {
                        halfEdgeModifier.ChangeHalfEdgePrev(boundary, potentialBoundaryWithAnInnerAsNextIdx);
                        halfEdgeModifier.ChangeHalfEdgeNext(HalfEdges[potentialBoundaryWithAnInnerAsNextIdx], boundaryIdx);
                    }
                }

                //
                // link next boundary
                //

                // same awful logic as obove just flipped in order to connects nexts, but with the joy of code duplication

                var totalPotentialNextBoundary = 0;
                var potentialBoundaryWithAnInnerAsPrevIdx = -1;
                var oppositeNextBoundary = -1;

                foreach (var heIdx in destVert.relatedEdges)
                {
                    var he = HalfEdges[heIdx];

                    // dont loop over ourselves
                    if (he == boundary)
                    {
                        continue;
                    }

                    // only loop over boundaries
                    if (he.face != -1)
                    {
                        continue;
                    }

                    if (he.destVert == boundary.destVert)
                    {
                        oppositeNextBoundary = heIdx;
                    }

                    if (he.origVert == boundary.destVert)
                    {
                        totalPotentialNextBoundary++;

                        if (he.prev != -1)
                        {
                            if (HalfEdges[he.prev].face != -1)
                            {
                                potentialBoundaryWithAnInnerAsPrevIdx = heIdx;
                            }
                        }
                    }
                }

                // more than two prev boundaries to choose here means we got more than 4 half edges on one vertex, invalid
                if (totalPotentialNextBoundary > 2)
                {
                    halfEdgeModifier.RollBack($"Removed face `{OriginalFaceCount - FacesRemoved}`, a vertex specified by the face has multiple boundary edges but shares no existing edge");
                    return;
                }

                if (totalPotentialNextBoundary == 2)
                {
                    if (potentialBoundaryWithAnInnerAsPrevIdx != -1)
                    {
                        halfEdgeModifier.ChangeHalfEdgeNext(boundary, potentialBoundaryWithAnInnerAsPrevIdx);
                        halfEdgeModifier.ChangeHalfEdgePrev(HalfEdges[potentialBoundaryWithAnInnerAsPrevIdx], boundaryIdx);
                    }
                    else
                    {
                        foreach (var heidx in VertexCirculator(oppositeNextBoundary, "into"))
                        {
                            var possibleNext = HalfEdges[heidx];
                            if (HalfEdges[possibleNext.twin].face == -1)
                            {
                                halfEdgeModifier.ChangeHalfEdgeNext(boundary, possibleNext.twin);
                                halfEdgeModifier.ChangeHalfEdgePrev(HalfEdges[possibleNext.twin], boundaryIdx);
                                break;
                            }
                        }
                    }
                }

                if (totalPotentialNextBoundary == 1)
                {
                    if (potentialBoundaryWithAnInnerAsPrevIdx == -1)
                    {
                        var next = HalfEdges[HalfEdges[boundary.twin].prev].twin;
                        halfEdgeModifier.ChangeHalfEdgeNext(boundary, next);
                        halfEdgeModifier.ChangeHalfEdgePrev(HalfEdges[next], boundaryIdx);
                    }
                    else
                    {
                        halfEdgeModifier.ChangeHalfEdgeNext(boundary, potentialBoundaryWithAnInnerAsPrevIdx);
                        halfEdgeModifier.ChangeHalfEdgePrev(HalfEdges[potentialBoundaryWithAnInnerAsPrevIdx], boundaryIdx);
                    }
                }
            }

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

        public void AddPhysHull(HullDescriptor hull, PhysAggregateData phys, Vector3 vertexOffset = new Vector3(), string materialOverride = "")
        {
            var attributes = phys.CollisionAttributes[hull.CollisionAttributeIndex];
            var tags = attributes.GetArray<string>("m_InteractAsStrings") ?? attributes.GetArray<string>("m_PhysicsTagStrings");
            var group = attributes.GetStringProperty("m_CollisionGroupString");
            var material = MapExtract.GetToolTextureNameForCollisionTags(new ModelExtract.SurfaceTagCombo(group, tags));
            if (materialOverride.Length > 0)
            {
                material = materialOverride;
            }

            foreach (var v in hull.Shape.VertexPositions)
            {
                AddVertex(new HammerMeshBuilder.Vertex(v + vertexOffset));
            }

            foreach (var face in hull.Shape.Faces)
            {
                var Indices = new List<int>();

                var startHe = face.Edge;
                var he = startHe;

                do
                {
                    Indices.Add(hull.Shape.Edges[he].Origin);
                    he = hull.Shape.Edges[he].Next;
                }
                while (he != startHe);

                AddFace(Indices, material);
            }
        }

        public void AddPhysMesh(MeshDescriptor mesh, PhysAggregateData phys, Vector3 vertexOffset = new Vector3(), string materialOverride = "")
        {
            var attributes = phys.CollisionAttributes[mesh.CollisionAttributeIndex];
            var tags = attributes.GetArray<string>("m_InteractAsStrings") ?? attributes.GetArray<string>("m_PhysicsTagStrings");
            var group = attributes.GetStringProperty("m_CollisionGroupString");
            var material = MapExtract.GetToolTextureNameForCollisionTags(new ModelExtract.SurfaceTagCombo(group, tags));
            if (materialOverride.Length > 0)
            {
                material = materialOverride;
            }

            foreach (var Vertex in mesh.Shape.Vertices)
            {
                AddVertex(new HammerMeshBuilder.Vertex(Vertex + vertexOffset));
            }

            foreach (var Face in mesh.Shape.Triangles)
            {
                AddFace(Face.X, Face.Y, Face.Z, material);
            }
        }

        public void AddRenderMesh(Datamodel.Datamodel dmxMesh, Vector3 vertexOffset = new Vector3())
        {
            var mesh = (DmeModel)dmxMesh.Root["model"];
            var dag = (DmeDag)mesh.JointList[0];
            var shape = (DmeMesh)dag.Shape;
            var facesets = shape.FaceSets;

            var vertexdata = (DmeVertexData)shape.BaseStates[0];
            var vertices = vertexdata.Get<Vector3[]>("position$0");
            var uvs = vertexdata.Get<Vector2[]>("texcoord$0");

            for (int i = 0; i < vertices.Length; i++)
            {
                var vertex = vertices[i];
                var uv = uvs[i];

                var newvert = new HammerMeshBuilder.Vertex(vertex + vertexOffset, uv);
                newvert.hasExistingUvs = true;
                AddVertex(newvert);
            }

            foreach (DmeFaceSet faceset in facesets)
            {
                var faces = faceset.Faces;

                List<int> face = new();
                foreach (var index in faces)
                {
                    if (index == -1)
                    {
                        AddFace(face, faceset.Material.MaterialName);
                        face.Clear();
                        continue;
                    }
                    face.Add(index);
                }
            }
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
            Vector3 tangent1 = Vector3.Cross(normal, Vector3.UnitY);
            Vector3 tangent2 = Vector3.Cross(normal, Vector3.UnitZ);
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
