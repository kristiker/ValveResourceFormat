using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GUI.Utils;
using NodeGraphControl.Elements;
using SkiaSharp;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Visual node graph representation of Pulse scripts (.vpulse_c).
    /// Pulse is Valve's visual scripting system used in Source 2.
    /// </summary>
    internal class PulseGraph : NodeGraphControl.NodeGraphControl
    {
        private readonly VrfGuiContext vrfGuiContext;
        private readonly KVObject graphDefinition;

        public PulseGraph(VrfGuiContext guiContext, KVObject data) : base()
        {
            vrfGuiContext = guiContext;
            graphDefinition = data;

            Dock = DockStyle.Fill;
            GridStyle = EGridStyle.Grid;

            CanvasBackgroundColor = new SKColor(40, 40, 40);
            NodeColor = new SKColor(60, 60, 60);
            NodeTextColor = new SKColor(230, 230, 230);
            GridColor = SKColors.White;

            ExecutionColor = new SKColor(255, 255, 255); // White for execution flow
            EntityColor = new SKColor(100, 200, 255);     // Blue for entities
            StringColor = new SKColor(255, 150, 200);     // Pink for strings
            BoolColor = new SKColor(200, 100, 100);       // Red for booleans

            if (Themer.CurrentTheme == Themer.AppTheme.Dark)
            {
                CanvasBackgroundColor = ToSKColor(Themer.CurrentThemeColors.AppMiddle);
                NodeColor = ToSKColor(Themer.CurrentThemeColors.AppSoft);
                GridColor = ToSKColor(Themer.CurrentThemeColors.ContrastSoft);
            }

            AddTypeColorPair<ExecutionFlow>(ExecutionColor);
            AddTypeColorPair<Entity>(EntityColor);
            AddTypeColorPair<string>(StringColor);
            AddTypeColorPair<bool>(BoolColor);

            CreateGraph();
        }

        // Type markers for different data types
        private struct ExecutionFlow;
        private struct Entity;

        private static SKColor ToSKColor(Color color) => new(color.R, color.G, color.B, color.A);

        private bool firstPaint = true;
        public static SKColor NodeColor { get; set; }
        public static SKColor NodeTextColor { get; set; }
        public static SKColor ExecutionColor { get; set; }
        public static SKColor EntityColor { get; set; }
        public static SKColor StringColor { get; set; }
        public static SKColor BoolColor { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (firstPaint)
            {
                firstPaint = false;
                FocusView(SKPoint.Empty);
            }

            base.OnPaint(e);
        }

        private void CreateGraph()
        {
            var cellsArray = graphDefinition.GetProperty<KVObject>("m_Cells");
            if (cellsArray == null)
            {
                return;
            }

            var nodeMap = new Dictionary<long, PulseNode>();
            var cellsByIndex = new Dictionary<int, PulseNode>();
            var libraryNodes = new Dictionary<int, PulseNode>(); // For library function calls
            var invokeBindings = graphDefinition.GetProperty<KVObject>("m_InvokeBindings");
            var chunks = graphDefinition.GetProperty<KVObject>("m_Chunks");
            var domainValues = graphDefinition.GetProperty<KVObject>("m_DomainValues");

            // First pass: Create all cell nodes
            int cellIndex = 0;
            foreach (var cell in cellsArray)
            {
                if (cell.Value is not KVObject cellData)
                {
                    continue;
                }

                var nodeId = cellData.GetInt32Property("m_nEditorNodeID");
                var className = cellData.GetProperty<string>("_class");

                var node = new PulseNode(cellData)
                {
                    Name = GetNodeName(cellData, nodeId, className),
                    NodeType = GetNodeType(className),
                    Location = new SKPoint(100, 100) // Will be positioned later
                };

                // Add input/output sockets based on node type
                ConfigureNodeSockets(node, cellData, className);

                AddNode(node);
                nodeMap[nodeId] = node;
                cellsByIndex[cellIndex] = node;
                cellIndex++;
            }

            // Second pass: Analyze bytecode and create logical connections
            if (chunks != null && invokeBindings != null)
            {
                CreateLogicalConnections(chunks, invokeBindings, cellsByIndex, domainValues, libraryNodes);
            }

            // Position nodes based on execution flow
            PositionNodesLogically(nodeMap, libraryNodes);
        }

        private static string GetNodeName(KVObject cellData, long nodeId, string className)
        {
            var methodName = cellData.GetProperty<string>("m_MethodName");
            if (!string.IsNullOrEmpty(methodName))
            {
                return $"{methodName}";
            }

            var input = cellData.GetProperty<string>("m_Input");
            if (!string.IsNullOrEmpty(input))
            {
                return $"EntFire: {input}";
            }

            var funcName = cellData.GetProperty<string>("m_FuncName");
            if (!string.IsNullOrEmpty(funcName))
            {
                var shortFunc = funcName.Split("::").LastOrDefault() ?? funcName;
                return shortFunc;
            }

            var shortName = className?.Split('_').LastOrDefault() ?? "Node";
            return shortName;
        }

        private static string GetNodeType(string className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return "Unknown";
            }

            // Extract meaningful part of class name
            // e.g., "CPulseCell_Inflow_Method" -> "Inflow Method"
            // e.g., "CPulseCell_Step_EntFire" -> "Step EntFire"
            const string prefix = "CPulseCell_";
            if (className.StartsWith(prefix))
            {
                var type = className[prefix.Length..];
                return type.Replace('_', ' ');
            }

            return className;
        }

        private static void ConfigureNodeSockets(PulseNode node, KVObject cellData, string className)
        {
            // Entry points have execution output
            if (className?.Contains("Inflow") == true)
            {
                node.HeaderColor = new SKColor(70, 100, 70); // Green
                var output = new SocketOut(typeof(ExecutionFlow), "Execute", node);
                node.Sockets.Add(output);

                // Add method details
                var methodName = cellData.GetProperty<string>("m_MethodName");
                if (!string.IsNullOrEmpty(methodName))
                {
                    node.AddText($"Entry Point");
                }
            }
            // Steps have execution flow in and out
            else if (className?.Contains("Step") == true)
            {
                node.HeaderColor = new SKColor(70, 70, 100); // Blue

                var input = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                node.Sockets.Add(input);

                var output = new SocketOut(typeof(ExecutionFlow), "Out", node);
                node.Sockets.Add(output);

                // Add entity target input for EntFire nodes
                if (className.Contains("EntFire"))
                {
                    var targetInput = cellData.GetProperty<string>("m_Input");
                    if (!string.IsNullOrEmpty(targetInput))
                    {
                        node.AddText($"Action: {targetInput}");
                    }

                    // Show target entity from m_OutputConnections context if available
                    node.AddSpace();
                    node.AddText("Fires entity event");
                }
            }
            // Library invokes (functions)
            else if (className?.Contains("Library") == true || className?.Contains("Invoke") == true)
            {
                node.HeaderColor = new SKColor(100, 70, 100); // Purple

                var input = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                node.Sockets.Add(input);

                var output = new SocketOut(typeof(ExecutionFlow), "True", node);
                node.Sockets.Add(output);

                var falseOutput = new SocketOut(typeof(ExecutionFlow), "False", node);
                node.Sockets.Add(falseOutput);

                // Add function details
                var funcName = cellData.GetProperty<string>("m_FuncName");
                if (!string.IsNullOrEmpty(funcName))
                {
                    node.AddSpace();
                    node.AddText($"Function Call");
                }

                // Add return value output
                var returnOutput = new SocketOut(typeof(bool), "Result", node);
                node.Sockets.Add(returnOutput);
            }
            // Outflow nodes
            else if (className?.Contains("Outflow") == true)
            {
                node.HeaderColor = new SKColor(100, 70, 70); // Red
                var input = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                node.Sockets.Add(input);
            }
            else
            {
                // Default node with basic execution flow
                var input = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                node.Sockets.Add(input);

                var output = new SocketOut(typeof(ExecutionFlow), "Out", node);
                node.Sockets.Add(output);
            }

            // Add description if present
            var description = cellData.GetProperty<string>("m_Description");
            if (!string.IsNullOrEmpty(description))
            {
                node.AddSpace();
                node.AddText($"Info: {description}");
            }

            node.Calculate();
        }

        private void CreateConnections(KVObject outputConnections, Dictionary<long, PulseNode> nodeMap)
        {
            foreach (var conn in outputConnections)
            {
                if (conn.Value is not KVObject connData)
                {
                    continue;
                }

                var sourceOutput = connData.GetProperty<string>("m_SourceOutput");
                var targetEntity = connData.GetProperty<string>("m_TargetEntity");
                var targetInput = connData.GetProperty<string>("m_TargetInput");

                // Parse source node ID from format "GameModeCheck => Step_EntFire:16216213"
                var parts = sourceOutput?.Split(':');
                if (parts?.Length != 2 || !long.TryParse(parts[1], out var sourceNodeId))
                {
                    continue;
                }

                if (!nodeMap.TryGetValue(sourceNodeId, out var sourceNode))
                {
                    continue;
                }

                // Find target node by matching input or similar characteristics
                var targetNode = FindTargetNode(nodeMap, targetEntity, targetInput);
                if (targetNode == null)
                {
                    continue;
                }

                // Connect execution flow sockets
                var sourceSocket = sourceNode.Sockets.OfType<SocketOut>()
                    .FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));
                var targetSocket = targetNode.Sockets.OfType<SocketIn>()
                    .FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));

                if (sourceSocket != null && targetSocket != null)
                {
                    try
                    {
                        Connect(sourceSocket, targetSocket);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to connect {sourceNode.Name} to {targetNode.Name}: {ex.Message}");
                    }
                }
            }
        }

        private void CreateLogicalConnections(KVObject chunks, KVObject invokeBindings, Dictionary<int, PulseNode> cellsByIndex, KVObject domainValues, Dictionary<int, PulseNode> libraryNodes)
        {
            // Parse invoke bindings to understand which cells are called from bytecode
            var bindingToCellMap = new Dictionary<int, int>(); // binding index -> cell index
            var bindingToTargetEntity = new Dictionary<int, string>(); // binding index -> entity name
            var bindingToFuncName = new Dictionary<int, string>(); // binding index -> function name

            int bindingIndex = 0;
            foreach (var binding in invokeBindings)
            {
                if (binding.Value is not KVObject bindingData)
                {
                    bindingIndex++;
                    continue;
                }

                var cellIndex = bindingData.GetInt32Property("m_nCellIndex");
                var funcName = bindingData.GetProperty<string>("m_FuncName");

                if (!string.IsNullOrEmpty(funcName))
                {
                    bindingToFuncName[bindingIndex] = funcName;
                }

                if (cellIndex >= 0)
                {
                    bindingToCellMap[bindingIndex] = cellIndex;

                    // Get target entity name from register map
                    var registerMap = bindingData.GetProperty<KVObject>("m_RegisterMap");
                    if (registerMap != null)
                    {
                        var inparams = registerMap.GetProperty<KVObject>("m_Inparams");
                        if (inparams != null)
                        {
                            foreach (var param in inparams)
                            {
                                if (param.Key == "TargetName")
                                {
                                    var domainIdx = param.Value as int?;
                                    if (domainIdx.HasValue && domainValues != null)
                                    {
                                        var domainValuesArray = domainValues.ToArray();
                                        if (domainIdx.Value < domainValuesArray.Length)
                                        {
                                            if (domainValuesArray[domainIdx.Value].Value is KVObject domainObj)
                                            {
                                                var entityName = domainObj.GetProperty<string>("m_Value");
                                                if (!string.IsNullOrEmpty(entityName))
                                                {
                                                    bindingToTargetEntity[bindingIndex] = entityName;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                bindingIndex++;
            }

            // Analyze the first chunk (entry point)
            if (chunks.Count == 0)
            {
                return;
            }

            var firstChunk = chunks.First().Value as KVObject;
            if (firstChunk == null)
            {
                return;
            }

            var instructions = firstChunk.GetProperty<KVObject>("m_Instructions");
            if (instructions == null)
            {
                return;
            }

            // Track execution flow through instructions
            var truePathNodes = new List<int>();  // Nodes executed when condition is true
            var falsePathNodes = new List<int>(); // Nodes executed when condition is false
            bool inTrueBranch = false;
            bool inFalseBranch = false;
            int? conditionBindingIdx = null;
            PulseNode? conditionNode = null; // The library function that returns the condition
            PulseNode? branchNode = null; // The conditional branch node

            var instructionsArray = instructions.ToArray();
            for (int i = 0; i < instructionsArray.Length; i++)
            {
                if (instructionsArray[i].Value is not KVObject instrData)
                {
                    continue;
                }

                var opCode = instrData.GetProperty<string>("m_nCode");
                var invokeBindingIdx = instrData.GetInt32Property("m_nInvokeBindingIndex");

                // LIBRARY_INVOKE is typically a condition check (GetIsWingman)
                if (opCode == "LIBRARY_INVOKE" && invokeBindingIdx >= 0)
                {
                    conditionBindingIdx = invokeBindingIdx;

                    // Create a virtual node for the library function call
                    if (bindingToFuncName.TryGetValue(invokeBindingIdx, out var funcName))
                    {
                        var funcShortName = funcName.Split("::").LastOrDefault() ?? funcName;
                        conditionNode = new PulseNode(new KVObject("VirtualLibraryNode"))
                        {
                            Name = funcShortName,
                            NodeType = "Library Function",
                            Location = new SKPoint(100, 100),
                            HeaderColor = new SKColor(100, 70, 100) // Purple
                        };

                        var input = new SocketIn(typeof(ExecutionFlow), "In", conditionNode, false);
                        conditionNode.Sockets.Add(input);

                        var output = new SocketOut(typeof(bool), "Result", conditionNode);
                        conditionNode.Sockets.Add(output);

                        conditionNode.AddSpace();
                        conditionNode.AddText($"Returns: bool");
                        conditionNode.Calculate();

                        AddNode(conditionNode);
                        libraryNodes[invokeBindingIdx] = conditionNode;
                    }
                }
                // JUMP_COND jumps to true branch if condition is true
                else if (opCode == "JUMP_COND")
                {
                    // Create a conditional branch node
                    branchNode = new PulseNode(new KVObject("VirtualBranchNode"))
                    {
                        Name = "Branch",
                        NodeType = "Conditional",
                        Location = new SKPoint(100, 100),
                        HeaderColor = new SKColor(100, 100, 70) // Yellow
                    };

                    var condInput = new SocketIn(typeof(bool), "Condition", branchNode, false);
                    branchNode.Sockets.Add(condInput);

                    var trueOutput = new SocketOut(typeof(ExecutionFlow), "True", branchNode);
                    branchNode.Sockets.Add(trueOutput);

                    var falseOutput = new SocketOut(typeof(ExecutionFlow), "False", branchNode);
                    branchNode.Sockets.Add(falseOutput);

                    branchNode.AddSpace();
                    branchNode.AddText("If true: take true path");
                    branchNode.AddText("If false: take false path");
                    branchNode.Calculate();

                    AddNode(branchNode);

                    // Connect condition node's result to branch node's condition input
                    if (conditionNode != null)
                    {
                        var condOutSocket = conditionNode.Sockets.OfType<SocketOut>().FirstOrDefault(s => s.ValueType == typeof(bool));
                        if (condOutSocket != null && condInput != null)
                        {
                            try
                            {
                                Connect(condOutSocket, condInput);
                            }
                            catch { }
                        }
                    }

                    inTrueBranch = true;
                    inFalseBranch = false;
                }
                // JUMP jumps to false branch
                else if (opCode == "JUMP" && inTrueBranch)
                {
                    inTrueBranch = false;
                    inFalseBranch = true;
                }
                // CELL_INVOKE executes a cell
                else if (opCode == "CELL_INVOKE" && invokeBindingIdx >= 0)
                {
                    if (bindingToCellMap.TryGetValue(invokeBindingIdx, out var cellIndex))
                    {
                        if (inTrueBranch)
                        {
                            truePathNodes.Add(cellIndex);
                        }
                        else if (inFalseBranch)
                        {
                            falsePathNodes.Add(cellIndex);
                        }

                        // Add entity label to node if we have it
                        if (bindingToTargetEntity.TryGetValue(invokeBindingIdx, out var entityName))
                        {
                            if (cellsByIndex.TryGetValue(cellIndex, out var node))
                            {
                                node.AddSpace();
                                node.AddText($"Target: {entityName}");
                                node.Calculate();
                            }
                        }
                    }
                }
                // RETURN_VOID ends execution
                else if (opCode == "RETURN_VOID")
                {
                    inTrueBranch = false;
                    inFalseBranch = false;
                }
            }

            // Connect entry point to condition check
            if (cellsByIndex.TryGetValue(0, out var entryNode))
            {
                if (conditionBindingIdx.HasValue && bindingToCellMap.TryGetValue(conditionBindingIdx.Value, out var conditionCellIdx))
                {
                    // Entry node doesn't exist as a cell, so we connect to first instruction
                    // Actually, the library invoke is not a cell, so skip this
                }

                // Connect entry to first node in true branch
                if (truePathNodes.Count > 0 && cellsByIndex.TryGetValue(truePathNodes[0], out var firstTrueNode))
                {
                    var entryOutSocket = entryNode.Sockets.OfType<SocketOut>().FirstOrDefault(s => s.SocketName == "Execute");
                    var firstTrueInSocket = firstTrueNode.Sockets.OfType<SocketIn>().FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));

                    if (entryOutSocket != null && firstTrueInSocket != null)
                    {
                        try
                        {
                            Connect(entryOutSocket, firstTrueInSocket);
                        }
                        catch { }
                    }
                }
            }

            // Connect nodes in true branch sequentially
            for (int i = 0; i < truePathNodes.Count - 1; i++)
            {
                if (cellsByIndex.TryGetValue(truePathNodes[i], out var currentNode) &&
                    cellsByIndex.TryGetValue(truePathNodes[i + 1], out var nextNode))
                {
                    ConnectNodes(currentNode, nextNode);
                }
            }

            // Connect nodes in false branch sequentially
            for (int i = 0; i < falsePathNodes.Count - 1; i++)
            {
                if (cellsByIndex.TryGetValue(falsePathNodes[i], out var currentNode) &&
                    cellsByIndex.TryGetValue(falsePathNodes[i + 1], out var nextNode))
                {
                    ConnectNodes(currentNode, nextNode);
                }
            }
        }

        private void ConnectNodes(PulseNode sourceNode, PulseNode targetNode)
        {
            var sourceSocket = sourceNode.Sockets.OfType<SocketOut>()
                .FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));
            var targetSocket = targetNode.Sockets.OfType<SocketIn>()
                .FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));

            if (sourceSocket != null && targetSocket != null)
            {
                try
                {
                    Connect(sourceSocket, targetSocket);
                }
                catch
                {
                    // Connection already exists or invalid
                }
            }
        }

        private void PositionNodesLogically(Dictionary<long, PulseNode> nodeMap, Dictionary<int, PulseNode> libraryNodes)
        {
            // Use a hierarchical layout based on execution flow
            var positioned = new HashSet<PulseNode>();
            var startX = 100;
            var startY = 100;
            var horizontalSpacing = 350;
            var verticalSpacing = 150;

            // Find entry nodes (Inflow nodes)
            var entryNodes = nodeMap.Values.Where(n => n.NodeType.Contains("Inflow")).ToList();

            int currentY = startY;
            foreach (var entryNode in entryNodes)
            {
                PositionNodeHierarchy(entryNode, startX, currentY, horizontalSpacing, verticalSpacing, positioned);
                currentY += verticalSpacing * 3; // Space between different entry point trees
            }

            // Position any library nodes
            foreach (var libNode in libraryNodes.Values)
            {
                if (!positioned.Contains(libNode))
                {
                    libNode.Location = new SKPoint(startX + horizontalSpacing, startY);
                    positioned.Add(libNode);
                }
            }

            // Position any unconnected nodes
            var unpositioned = nodeMap.Values.Where(n => !positioned.Contains(n)).ToList();
            int column = 0;
            foreach (var node in unpositioned)
            {
                node.Location = new SKPoint(
                    startX + (column % 3) * horizontalSpacing,
                    currentY + (column / 3) * verticalSpacing
                );
                column++;
            }

            LayoutNodes(20f);
        }

        private static void PositionNodeHierarchy(PulseNode node, float x, float y, float hSpacing, float vSpacing, HashSet<PulseNode> positioned)
        {
            if (positioned.Contains(node))
            {
                return;
            }

            node.Location = new SKPoint(x, y);
            positioned.Add(node);

            // Get all connected output nodes
            var outputSockets = node.Sockets.OfType<SocketOut>().ToList();
            int childIndex = 0;

            foreach (var socket in outputSockets)
            {
                // SocketOut doesn't expose connections directly, we'll use a different approach
                // The node graph control will handle connections through the Connect method
            }
        }

        private static PulseNode? FindTargetNode(Dictionary<long, PulseNode> nodeMap, string targetEntity, string targetInput)
        {
            // Try to find a node that matches the target entity/input
            // This is a heuristic approach since we don't have direct node ID references
            foreach (var node in nodeMap.Values)
            {
                var input = node.Data.GetProperty<string>("m_Input");
                if (!string.IsNullOrEmpty(input) && !string.IsNullOrEmpty(targetInput) &&
                    input.Equals(targetInput, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }

                // Also check if node name contains target info
                if (!string.IsNullOrEmpty(targetEntity) && node.Name.Contains(targetEntity, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }

            return null;
        }

        #region Node Definition

        private class PulseNode : AbstractNode
        {
            public KVObject Data { get; set; }

            public PulseNode(KVObject data)
            {
                Data = data;
                BaseColor = NodeColor;
                TextColor = NodeTextColor;
                HeaderColor = ToSKColor(ControlPaint.Light(Color.FromArgb(NodeColor.Red, NodeColor.Green, NodeColor.Blue)));
                HeaderTextColor = new SKColor(5, 5, 5);
                HeaderTypeColor = new SKColor(25, 25, 25);
            }

            public override bool IsReady()
            {
                // Pulse nodes are always ready (not async execution model)
                return true;
            }

            public override void Execute()
            {
                // Execution is for runtime - not needed for visualization
            }

            public void AddSpace() => CreateTextSocket<string>(string.Empty);
            public void AddText(string text) => CreateTextSocket<string>(text);

            private void CreateTextSocket<T>(string text)
            {
                var socket = new SocketIn(typeof(T), text, this, false)
                {
                    DisplayOnly = true
                };
                Sockets.Add(socket);
            }
        }

        #endregion
    }
}
