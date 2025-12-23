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
    internal partial class PulseGraph : NodeGraphControl.NodeGraphControl
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
            // Parse the entire Pulse graph data
            var pulseData = PulseGraphData.FromKV(graphDefinition);

            if (pulseData.Chunks.Length == 0)
            {
                return;
            }

            // Build each chunk as a separate execution graph
            float startY = 200;
            float chunkVerticalSpacing = -200;

            for (int chunkIndex = 0; chunkIndex < pulseData.Chunks.Length; chunkIndex++)
            {
                BuildChunkGraph(chunkIndex, pulseData.Chunks[chunkIndex], startY + (chunkIndex * chunkVerticalSpacing),
                    pulseData.Constants, pulseData.DomainValues, pulseData.Variables, 
                    pulseData.InvokeBindings, pulseData.Cells, pulseData.CallInfos);
            }

            LayoutNodes(20f);
        }

        private void BuildChunkGraph(int chunkIndex, PulseChunk chunk, float startY,
            PulseConstant[] constants, PulseDomainValue[] domainValues, PulseVariable[] variables,
            PulseInvokeBinding[] invokeBindings, PulseCell[] cells, PulseCallInfo[] callInfos)
        {
            // Create node for chunk entry (NOP instruction)
            var chunkNode = new PulseNode(new KVObject("ChunkEntry"))
            {
                Name = $"Chunk {chunkIndex}",
                NodeType = "Entry",
                Location = new SKPoint(100, startY),
                HeaderColor = new SKColor(70, 100, 70) // Green for entry
            };

            var output = new SocketOut(typeof(ExecutionFlow), "Execute", chunkNode);
            chunkNode.Sockets.Add(output);
            chunkNode.AddText($"Chunk {chunkIndex} Start");

            // Find if this chunk has an entry point cell
            foreach (var cell in cells)
            {
                if (cell.EntryChunk == chunkIndex)
                {
                    if (!string.IsNullOrEmpty(cell.MethodName))
                    {
                        chunkNode.Name = cell.MethodName;
                        chunkNode.AddSpace();
                        chunkNode.AddText($"Method Entry");
                    }
                    else if (!string.IsNullOrEmpty(cell.EventName))
                    {
                        chunkNode.Name = cell.EventName.Split("::").LastOrDefault() ?? cell.EventName;
                        chunkNode.AddSpace();
                        chunkNode.AddText($"Event Handler");
                    }
                    break;
                }
            }

            chunkNode.Calculate();
            AddNode(chunkNode);

            // Process instructions and create nodes
            var instructionNodes = new Dictionary<int, PulseNode>();
            float currentX = 300;
            float instructionSpacing = 300;

            for (int i = 0; i < chunk.Instructions.Length; i++)
            {
                var instr = chunk.Instructions[i];

                // Skip NOP (handled by chunk entry)
                if (instr.OpCode == "NOP")
                {
                    continue;
                }

                var node = CreateInstructionNode(instr, i, chunkIndex, currentX, startY,
                    constants, domainValues, variables, invokeBindings, cells, callInfos, chunk.Registers);

                if (node != null)
                {
                    AddNode(node);
                    instructionNodes[i] = node;
                    currentX += instructionSpacing;
                }
            }

            // Connect chunk entry to first instruction node
            if (instructionNodes.Count > 0)
            {
                var firstNode = instructionNodes.Values.First();
                var chunkOutSocket = chunkNode.Sockets.OfType<SocketOut>().FirstOrDefault();
                var firstInSocket = firstNode.Sockets.OfType<SocketIn>().FirstOrDefault(s => s.ValueType == typeof(ExecutionFlow));

                if (chunkOutSocket != null && firstInSocket != null)
                {
                    try { Connect(chunkOutSocket, firstInSocket); } catch { }
                }
            }

            // Connect sequential instructions
            for (int i = 0; i < instructionNodes.Count - 1; i++)
            {
                var keys = instructionNodes.Keys.OrderBy(k => k).ToList();
                if (i < keys.Count - 1 && instructionNodes.TryGetValue(keys[i], out var current) &&
                    instructionNodes.TryGetValue(keys[i + 1], out var next))
                {
                    ConnectNodes(current, next);
                }
            }
        }

        private static PulseNode CreateInstructionNode(PulseInstruction instr, int instrIndex, int chunkIndex,
            float x, float y, PulseConstant[] constants, PulseDomainValue[] domainValues, PulseVariable[] variables,
            PulseInvokeBinding[] invokeBindings, PulseCell[] cells, PulseCallInfo[] callInfos, PulseRegister[] registers)
        {
            var node = new PulseNode(new KVObject("Instruction"))
            {
                Name = instr.OpCode,
                NodeType = "Instruction",
                Location = new SKPoint(x, y)
            };

            // Determine node color and configuration based on opcode
            switch (instr.OpCode)
            {
                case "GET_CONST":
                    node.HeaderColor = new SKColor(100, 100, 150); // Blue for constants
                    if (instr.ConstIdx >= 0 && instr.ConstIdx < constants.Length)
                    {
                        var constant = constants[instr.ConstIdx];
                        node.AddText($"Value: {constant.Value}");
                    }
                    if (instr.Reg0 >= 0)
                    {
                        node.AddText($"→ Register {instr.Reg0}");
                    }
                    var output = new SocketOut(typeof(object), "Value", node);
                    node.Sockets.Add(output);
                    break;

                case "GET_VAR":
                    node.HeaderColor = new SKColor(150, 100, 150); // Purple for variables
                    if (instr.Var >= 0 && instr.Var < variables.Length)
                    {
                        var variable = variables[instr.Var];
                        node.AddText($"Variable: {variable.Name}");
                    }
                    if (instr.Reg0 >= 0)
                    {
                        node.AddText($"→ Register {instr.Reg0}");
                    }
                    var varOutput = new SocketOut(typeof(object), "Value", node);
                    node.Sockets.Add(varOutput);
                    break;

                case "GET_DOMAIN_VALUE":
                    node.HeaderColor = new SKColor(100, 150, 100); // Green for domain values
                    if (instr.DomainValueIdx >= 0 && instr.DomainValueIdx < domainValues.Length)
                    {
                        var domainValue = domainValues[instr.DomainValueIdx];
                        node.AddText($"Entity: {domainValue.Value}");
                    }
                    if (instr.Reg0 >= 0)
                    {
                        node.AddText($"→ Register {instr.Reg0}");
                    }
                    var domainOutput = new SocketOut(typeof(Entity), "Entity", node);
                    node.Sockets.Add(domainOutput);
                    break;

                case "CELL_INVOKE":
                    node.HeaderColor = new SKColor(70, 70, 100); // Blue for cell invokes
                    node.Name = "Cell Invoke";
                    if (instr.InvokeBindingIndex >= 0 && instr.InvokeBindingIndex < invokeBindings.Length)
                    {
                        var binding = invokeBindings[instr.InvokeBindingIndex];
                        if (binding.CellIndex >= 0 && binding.CellIndex < cells.Length)
                        {
                            var cell = cells[binding.CellIndex];

                            if (!string.IsNullOrEmpty(cell.FuncName))
                            {
                                node.AddText($"Function: {cell.FuncName.Split("::").LastOrDefault()}");
                            }
                            else if (!string.IsNullOrEmpty(cell.Input))
                            {
                                node.AddText($"Action: {cell.Input}");
                            }
                            else
                            {
                                node.AddText($"Cell {binding.CellIndex}: {cell.ClassName}");
                            }
                        }
                    }
                    var cellInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    var cellOutput = new SocketOut(typeof(ExecutionFlow), "Out", node);
                    node.Sockets.Add(cellInput);
                    node.Sockets.Add(cellOutput);
                    break;

                case "LIBRARY_INVOKE":
                    node.HeaderColor = new SKColor(100, 70, 100); // Purple for library calls
                    node.Name = "Library Invoke";
                    if (instr.InvokeBindingIndex >= 0 && instr.InvokeBindingIndex < invokeBindings.Length)
                    {
                        var binding = invokeBindings[instr.InvokeBindingIndex];
                        node.AddText($"Func: {binding.FuncName.Split("::").LastOrDefault()}");
                    }
                    var libInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    var libOutput = new SocketOut(typeof(ExecutionFlow), "Out", node);
                    node.Sockets.Add(libInput);
                    node.Sockets.Add(libOutput);
                    break;

                case "PULSE_CALL_SYNC":
                case "PULSE_CALL_ASYNC_FIRE":
                    node.HeaderColor = new SKColor(150, 100, 70); // Orange for calls
                    node.Name = instr.OpCode == "PULSE_CALL_SYNC" ? "Call (Sync)" : "Call (Async)";

                    if (instr.CallInfoIndex >= 0 && instr.CallInfoIndex < callInfos.Length)
                    {
                        var callInfo = callInfos[instr.CallInfoIndex];

                        foreach (var cell in cells)
                        {
                            if (cell.EditorNodeID == callInfo.CallMethodID)
                            {
                                if (!string.IsNullOrEmpty(cell.MethodName))
                                {
                                    node.AddText($"Call: {cell.MethodName}");
                                }
                                break;
                            }
                        }
                    }

                    node.AddText($"→ Chunk {instr.Chunk}");
                    var callInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    var callOutput = new SocketOut(typeof(ExecutionFlow), "Out", node);
                    node.Sockets.Add(callInput);
                    node.Sockets.Add(callOutput);
                    break;

                case "JUMP":
                    node.HeaderColor = new SKColor(100, 100, 70); // Yellow for jumps
                    node.AddText($"Jump to instruction {instr.DestInstruction}");
                    var jumpInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    var jumpOutput = new SocketOut(typeof(ExecutionFlow), "Out", node);
                    node.Sockets.Add(jumpInput);
                    node.Sockets.Add(jumpOutput);
                    break;

                case "SET_VAR":
                    node.HeaderColor = new SKColor(150, 100, 150); // Purple for variables
                    if (instr.Var >= 0 && instr.Var < variables.Length)
                    {
                        var variable = variables[instr.Var];
                        node.AddText($"Set: {variable.Name}");
                    }
                    if (instr.Reg0 >= 0)
                    {
                        node.AddText($"From Register {instr.Reg0}");
                    }
                    var setInput = new SocketIn(typeof(object), "Value", node, false);
                    node.Sockets.Add(setInput);
                    break;

                case "CONVERT_VALUE":
                    node.HeaderColor = new SKColor(120, 120, 120); // Gray for conversions
                    node.AddText($"Reg {instr.Reg1} → Reg {instr.Reg0}");

                    // Get type info from registers if available
                    if (instr.Reg0 >= 0 && instr.Reg0 < registers.Length)
                    {
                        var register = registers[instr.Reg0];
                        if (!string.IsNullOrEmpty(register.Type))
                        {
                            node.AddText($"Type: {register.Type}");
                        }
                    }

                    var convertInput = new SocketIn(typeof(object), "In", node, false);
                    var convertOutput = new SocketOut(typeof(object), "Out", node);
                    node.Sockets.Add(convertInput);
                    node.Sockets.Add(convertOutput);
                    break;

                case "RETURN_VOID":
                    node.HeaderColor = new SKColor(100, 70, 70); // Red for return
                    node.AddText("End of execution");
                    var returnInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    node.Sockets.Add(returnInput);
                    break;

                default:
                    // Generic instruction node
                    node.HeaderColor = new SKColor(80, 80, 80); // Gray
                    node.AddText($"OpCode: {instr.OpCode}");
                    var genInput = new SocketIn(typeof(ExecutionFlow), "In", node, false);
                    var genOutput = new SocketOut(typeof(ExecutionFlow), "Out", node);
                    node.Sockets.Add(genInput);
                    node.Sockets.Add(genOutput);
                    break;
            }

            node.Calculate();
            return node;
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
