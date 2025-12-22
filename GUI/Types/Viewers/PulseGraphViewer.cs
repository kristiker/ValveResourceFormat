using System;
using System.Drawing;
using System.Windows.Forms;
using GUI.Utils;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Types.Viewers
{
    /// <summary>
    /// Viewer for Pulse Graph files (.vpulse_c).
    /// Pulse is Valve's visual scripting system used in Source 2.
    /// </summary>
    internal class PulseGraphViewer : SplitContainer
    {
        private readonly VrfGuiContext vrfGuiContext;
        private readonly KVObject graphDefinition;

        private readonly PulseGraph graphPanel;
        private readonly TextBox detailsTextBox;

        public PulseGraphViewer(VrfGuiContext guiContext, KVObject data)
        {
            vrfGuiContext = guiContext;
            graphDefinition = data;

            Dock = DockStyle.Fill;
            Orientation = Orientation.Horizontal;
            SplitterDistance = 400;

            // Top: Graph visualization using NodeGraphControl
            graphPanel = new PulseGraph(guiContext, data)
            {
                Dock = DockStyle.Fill
            };
            Panel1.Controls.Add(graphPanel);

            // Bottom: Details view
            detailsTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(220, 220, 220)
            };
            Panel2.Controls.Add(detailsTextBox);

            // Generate summary
            detailsTextBox.Text = GenerateGraphSummary(data);
        }

        private static string GenerateGraphSummary(KVObject data)
        {
            var summary = new System.Text.StringBuilder();
            summary.AppendLine("=== Pulse Graph Summary ===\n");

            var parentMap = data.GetProperty<string>("m_ParentMapName");
            if (!string.IsNullOrEmpty(parentMap))
            {
                summary.AppendLine($"Parent Map: {parentMap}");
            }

            var domain = data.GetProperty<string>("m_DomainIdentifier");
            if (!string.IsNullOrEmpty(domain))
            {
                summary.AppendLine($"Domain: {domain}");
            }

            var cellsArray = data.GetProperty<KVObject>("m_Cells");
            if (cellsArray != null)
            {
                summary.AppendLine($"\nCells: {cellsArray.Count}");
                foreach (var cell in cellsArray)
                {
                    if (cell.Value is KVObject cellData)
                    {
                        var className = cellData.GetProperty<string>("_class");
                        var nodeId = cellData.GetInt32Property("m_nEditorNodeID");
                        summary.AppendLine($"  [{nodeId}] {className}");

                        var methodName = cellData.GetProperty<string>("m_MethodName");
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            summary.AppendLine($"    Method: {methodName}");
                        }

                        var input = cellData.GetProperty<string>("m_Input");
                        if (!string.IsNullOrEmpty(input))
                        {
                            summary.AppendLine($"    Input: {input}");
                        }
                    }
                }
            }

            var connectionsArray = data.GetProperty<KVObject>("m_OutputConnections");
            if (connectionsArray != null)
            {
                summary.AppendLine($"\nOutput Connections: {connectionsArray.Count}");
                foreach (var conn in connectionsArray)
                {
                    if (conn.Value is KVObject connData)
                    {
                        var source = connData.GetProperty<string>("m_SourceOutput");
                        var target = connData.GetProperty<string>("m_TargetEntity");
                        var targetInput = connData.GetProperty<string>("m_TargetInput");
                        summary.AppendLine($"  {source} -> {target}.{targetInput}");
                    }
                }
            }

            var domainValuesArray = data.GetProperty<KVObject>("m_DomainValues");
            if (domainValuesArray != null)
            {
                summary.AppendLine($"\nDomain Values: {domainValuesArray.Count}");
                foreach (var dv in domainValuesArray)
                {
                    if (dv.Value is KVObject dvData)
                    {
                        var type = dvData.GetProperty<string>("m_nType");
                        var value = dvData.GetProperty<string>("m_Value");
                        summary.AppendLine($"  {type}: {value}");
                    }
                }
            }

            var chunksArray = data.GetProperty<KVObject>("m_Chunks");
            if (chunksArray != null)
            {
                summary.AppendLine($"\nBytecode Chunks: {chunksArray.Count}");
                foreach (var chunk in chunksArray)
                {
                    if (chunk.Value is KVObject chunkData)
                    {
                        var instructions = chunkData.GetProperty<KVObject>("m_Instructions");
                        if (instructions != null)
                        {
                            summary.AppendLine($"  Instructions: {instructions.Count}");
                        }
                    }
                }
            }

            return summary.ToString();
        }
    }
}
