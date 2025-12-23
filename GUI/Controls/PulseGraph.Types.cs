using System.Linq;
using ValveResourceFormat.Serialization.KeyValues;

namespace GUI.Types.Viewers
{
    internal partial class PulseGraph
    {
        #region Pulse Data Structures

        /// <summary>
        /// Root structure representing a complete Pulse graph definition.
        /// </summary>
        internal readonly struct PulseGraphData
        {
            public PulseChunk[] Chunks { get; init; }
            public PulseConstant[] Constants { get; init; }
            public PulseDomainValue[] DomainValues { get; init; }
            public PulseVariable[] Variables { get; init; }
            public PulseInvokeBinding[] InvokeBindings { get; init; }
            public PulseCell[] Cells { get; init; }
            public PulseCallInfo[] CallInfos { get; init; }
            public string DomainIdentifier { get; init; }
            public string DomainSubType { get; init; }
            public string ParentMapName { get; init; }
            public string ParentXmlName { get; init; }

            public static PulseGraphData FromKV(KVObject kv)
            {
                var chunksKV = kv.GetProperty<KVObject>("m_Chunks");
                var constantsKV = kv.GetProperty<KVObject>("m_Constants");
                var domainValuesKV = kv.GetProperty<KVObject>("m_DomainValues");
                var variablesKV = kv.GetProperty<KVObject>("m_Vars");
                var invokeBindingsKV = kv.GetProperty<KVObject>("m_InvokeBindings");
                var cellsKV = kv.GetProperty<KVObject>("m_Cells");
                var callInfosKV = kv.GetProperty<KVObject>("m_CallInfos");

                return new()
                {
                    Chunks = chunksKV?.Select(c => c.Value is KVObject kvObj ? PulseChunk.FromKV(kvObj) : default).ToArray() ?? [],
                    Constants = constantsKV?.Select(c => c.Value is KVObject kvObj ? PulseConstant.FromKV(kvObj) : default).ToArray() ?? [],
                    DomainValues = domainValuesKV?.Select(d => d.Value is KVObject kvObj ? PulseDomainValue.FromKV(kvObj) : default).ToArray() ?? [],
                    Variables = variablesKV?.Select(v => v.Value is KVObject kvObj ? PulseVariable.FromKV(kvObj) : default).ToArray() ?? [],
                    InvokeBindings = invokeBindingsKV?.Select(i => i.Value is KVObject kvObj ? PulseInvokeBinding.FromKV(kvObj) : default).ToArray() ?? [],
                    Cells = cellsKV?.Select(c => c.Value is KVObject kvObj ? PulseCell.FromKV(kvObj) : default).ToArray() ?? [],
                    CallInfos = callInfosKV?.Select(c => c.Value is KVObject kvObj ? PulseCallInfo.FromKV(kvObj) : default).ToArray() ?? [],
                    DomainIdentifier = kv.GetProperty<string>("m_DomainIdentifier") ?? "",
                    DomainSubType = kv.GetProperty<string>("m_DomainSubType") ?? "",
                    ParentMapName = kv.GetProperty<string>("m_ParentMapName") ?? "",
                    ParentXmlName = kv.GetProperty<string>("m_ParentXmlName") ?? "",
                };
            }
        }

        /// <summary>
        /// Represents a bytecode instruction in a Pulse chunk.
        /// </summary>
        internal readonly struct PulseInstruction
        {
            public string OpCode { get; init; }
            public int Var { get; init; }
            public int Reg0 { get; init; }
            public int Reg1 { get; init; }
            public int Reg2 { get; init; }
            public int InvokeBindingIndex { get; init; }
            public int Chunk { get; init; }
            public int DestInstruction { get; init; }
            public int CallInfoIndex { get; init; }
            public int ConstIdx { get; init; }
            public int DomainValueIdx { get; init; }
            public int BlackboardReferenceIdx { get; init; }

            public static PulseInstruction FromKV(KVObject kv) => new()
            {
                OpCode = kv.GetProperty<string>("m_nCode") ?? "",
                Var = kv.GetInt32Property("m_nVar"),
                Reg0 = kv.GetInt32Property("m_nReg0"),
                Reg1 = kv.GetInt32Property("m_nReg1"),
                Reg2 = kv.GetInt32Property("m_nReg2"),
                InvokeBindingIndex = kv.GetInt32Property("m_nInvokeBindingIndex"),
                Chunk = kv.GetInt32Property("m_nChunk"),
                DestInstruction = kv.GetInt32Property("m_nDestInstruction"),
                CallInfoIndex = kv.GetInt32Property("m_nCallInfoIndex"),
                ConstIdx = kv.GetInt32Property("m_nConstIdx"),
                DomainValueIdx = kv.GetInt32Property("m_nDomainValueIdx"),
                BlackboardReferenceIdx = kv.GetInt32Property("m_nBlackboardReferenceIdx"),
            };
        }

        /// <summary>
        /// Represents a register in a Pulse chunk for tracking data flow.
        /// </summary>
        internal readonly struct PulseRegister
        {
            public int Reg { get; init; }
            public string Type { get; init; }
            public string OriginName { get; init; }
            public int WrittenByInstruction { get; init; }
            public int LastReadByInstruction { get; init; }

            public static PulseRegister FromKV(KVObject kv) => new()
            {
                Reg = kv.GetInt32Property("m_nReg"),
                Type = kv.GetProperty<string>("m_Type") ?? "",
                OriginName = kv.GetProperty<string>("m_OriginName") ?? "",
                WrittenByInstruction = kv.GetInt32Property("m_nWrittenByInstruction"),
                LastReadByInstruction = kv.GetInt32Property("m_nLastReadByInstruction"),
            };
        }

        /// <summary>
        /// Represents a Pulse chunk - a container for instructions and registers.
        /// Each chunk is a separate execution graph starting with a NOP instruction.
        /// </summary>
        internal readonly struct PulseChunk
        {
            public PulseInstruction[] Instructions { get; init; }
            public PulseRegister[] Registers { get; init; }
            public int[] InstructionEditorIDs { get; init; }

            public static PulseChunk FromKV(KVObject kv)
            {
                var instructionsKV = kv.GetProperty<KVObject>("m_Instructions");
                var registersKV = kv.GetProperty<KVObject>("m_Registers");
                var editorIDsKV = kv.GetProperty<KVObject>("m_InstructionEditorIDs");

                return new()
                {
                    Instructions = instructionsKV?.Select(i => i.Value is KVObject kvInstr ? PulseInstruction.FromKV(kvInstr) : default).ToArray() ?? [],
                    Registers = registersKV?.Select(r => r.Value is KVObject kvReg ? PulseRegister.FromKV(kvReg) : default).ToArray() ?? [],
                    InstructionEditorIDs = editorIDsKV?.Select(e => e.Value as int? ?? -1).ToArray() ?? [],
                };
            }
        }

        /// <summary>
        /// Represents a constant value used in Pulse scripts.
        /// </summary>
        internal readonly struct PulseConstant
        {
            public string Type { get; init; }
            public object Value { get; init; }

            public static PulseConstant FromKV(KVObject kv) => new()
            {
                Type = kv.GetProperty<string>("m_Type") ?? "",
                Value = kv.GetProperty<object>("m_Value") ?? "",
            };
        }

        /// <summary>
        /// Represents a domain value (typically entity names) in Pulse scripts.
        /// </summary>
        internal readonly struct PulseDomainValue
        {
            public string Type { get; init; }
            public string Value { get; init; }
            public string RequiredRuntimeType { get; init; }

            public static PulseDomainValue FromKV(KVObject kv) => new()
            {
                Type = kv.GetProperty<string>("m_nType") ?? "",
                Value = kv.GetProperty<string>("m_Value") ?? "",
                RequiredRuntimeType = kv.GetProperty<string>("m_RequiredRuntimeType") ?? "",
            };
        }

        /// <summary>
        /// Represents a variable in a Pulse script.
        /// </summary>
        internal readonly struct PulseVariable
        {
            public string Name { get; init; }
            public string Description { get; init; }
            public string Type { get; init; }
            public object DefaultValue { get; init; }
            public int EditorNodeID { get; init; }

            public static PulseVariable FromKV(KVObject kv) => new()
            {
                Name = kv.GetProperty<string>("m_Name") ?? "",
                Description = kv.GetProperty<string>("m_Description") ?? "",
                Type = kv.GetProperty<string>("m_Type") ?? "",
                DefaultValue = kv.GetProperty<object>("m_DefaultValue") ?? "",
                EditorNodeID = kv.GetInt32Property("m_nEditorNodeID"),
            };
        }

        /// <summary>
        /// Represents a binding to a function or cell invocation.
        /// </summary>
        internal readonly struct PulseInvokeBinding
        {
            public string FuncName { get; init; }
            public int CellIndex { get; init; }
            public int SrcChunk { get; init; }
            public int SrcInstruction { get; init; }

            public static PulseInvokeBinding FromKV(KVObject kv) => new()
            {
                FuncName = kv.GetProperty<string>("m_FuncName") ?? "",
                CellIndex = kv.GetInt32Property("m_nCellIndex"),
                SrcChunk = kv.GetInt32Property("m_nSrcChunk"),
                SrcInstruction = kv.GetInt32Property("m_nSrcInstruction"),
            };
        }

        /// <summary>
        /// Represents a Pulse cell - the high-level definition of methods, events, and actions.
        /// </summary>
        internal readonly struct PulseCell
        {
            public string ClassName { get; init; }
            public int EditorNodeID { get; init; }
            public int EntryChunk { get; init; }
            public string MethodName { get; init; }
            public string EventName { get; init; }
            public string FuncName { get; init; }
            public string Input { get; init; }
            public string Description { get; init; }

            public static PulseCell FromKV(KVObject kv) => new()
            {
                ClassName = kv.GetProperty<string>("_class") ?? "",
                EditorNodeID = kv.GetInt32Property("m_nEditorNodeID"),
                EntryChunk = kv.GetInt32Property("m_EntryChunk"),
                MethodName = kv.GetProperty<string>("m_MethodName") ?? "",
                EventName = kv.GetProperty<string>("m_EventName") ?? "",
                FuncName = kv.GetProperty<string>("m_FuncName") ?? "",
                Input = kv.GetProperty<string>("m_Input") ?? "",
                Description = kv.GetProperty<string>("m_Description") ?? "",
            };
        }

        /// <summary>
        /// Represents metadata about a method call in Pulse scripts.
        /// </summary>
        internal readonly struct PulseCallInfo
        {
            public string PortName { get; init; }
            public int EditorNodeID { get; init; }
            public int CallMethodID { get; init; }
            public int SrcChunk { get; init; }
            public int SrcInstruction { get; init; }

            public static PulseCallInfo FromKV(KVObject kv) => new()
            {
                PortName = kv.GetProperty<string>("m_PortName") ?? "",
                EditorNodeID = kv.GetInt32Property("m_nEditorNodeID"),
                CallMethodID = kv.GetInt32Property("m_CallMethodID"),
                SrcChunk = kv.GetInt32Property("m_nSrcChunk"),
                SrcInstruction = kv.GetInt32Property("m_nSrcInstruction"),
            };
        }

        #endregion
    }
}
