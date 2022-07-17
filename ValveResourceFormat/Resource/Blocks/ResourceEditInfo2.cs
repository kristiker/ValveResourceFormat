using System;
using System.IO;
using System.Collections.Generic;
using REDIStructs = ValveResourceFormat.Blocks.ResourceEditInfoStructs;
using RED2Structs = ValveResourceFormat.Blocks.RED2Structs;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Blocks
{
    /// <summary>
    /// "RED2" block. CResourceEditInfo.
    /// </summary>
    public class ResourceEditInfo2 : ResourceEditInfo
    {
        public override BlockType Type => BlockType.RED2;

        private BinaryKV3 BackingData;

        /// <summary>
        /// New structs added with RED2 that are not present in REDI.
        /// </summary>
        public enum RED2NewStruct
        {
            WeakReferenceList,
            SearchableUserData,
            SubassetReferences,
            SubassetDefinitions,

            End,
        }

        // or
        public enum RED2Struct
        {
            InputDependencies = 0,
            AdditionalInputDependencies = 1,
            ArgumentDependencies = 2,
            SpecialDependencies = 3,
            //CustomDependencies = 4,
            AdditionalRelatedFiles = 5,
            ChildResourceList = 6,
            //ExtraIntData = 7,
            //ExtraFloatData = 8,
            //ExtraStringData = 9,
            WeakReferenceList = 10,
            SearchableUserData = 11,
            SubassetReferences = 12,
            SubassetDefinitions = 13,

            End,
        }

        public Dictionary<RED2NewStruct, RED2Structs.IRED2Struct> Structs2 { get; private set; }

        public ResourceEditInfo2()
            : base()
        {
            Structs2 = new Dictionary<RED2NewStruct, RED2Structs.IRED2Struct>(sizeof(RED2NewStruct)-1);
        }

        public override void Read(BinaryReader reader, Resource resource)
        {
            var kv3 = new BinaryKV3
            {
                Offset = Offset,
                Size = Size,
            };
            kv3.Read(reader, resource);
            BackingData = kv3;

            for (var i = RED2Struct.InputDependencies; i < RED2Struct.End; i++)
            {
                if (!Enum.IsDefined<RED2Struct>(i))
                {
                    continue;
                }

                var keyName = "m_"+Enum.GetName<RED2Struct>(i);

                if ((REDIStruct)i < REDIStruct.End)
                {
                    var block = ConstructStruct(keyName, kv3.AsKeyValueCollection());

                    Structs.Add((REDIStruct)i, (REDIStructs.REDIBlock)block);
                }
                else
                {
                    Console.WriteLine("Construct RED2 struct: " + keyName);
                    var block = ConstructStruct(keyName, kv3.AsKeyValueCollection());

                    if (block is not null)
                    {
                        if (block is RED2Structs.SearchableUserData)
                        {
                            var equivalentRediStructs = ((RED2Structs.SearchableUserData)block).GetEquivalentREDIBlocks();
                            Structs.Add(REDIStruct.ExtraIntData, equivalentRediStructs.Item1);
                            Structs.Add(REDIStruct.ExtraFloatData, equivalentRediStructs.Item2);
                            Structs.Add(REDIStruct.ExtraStringData, equivalentRediStructs.Item3);
                        }
                        Structs2.Add((RED2NewStruct)i, (RED2Structs.IRED2Struct)block);
                    }
                }
            }
        }

        public override void WriteText(IndentedTextWriter writer)
        {
            BackingData.WriteText(writer);
        }

        public ResourceEditInfo GetRedi()
        {
            var redi = new ResourceEditInfo();

            foreach( var kv in Structs )
            {
                redi.Structs.Add(kv.Key, kv.Value);
            }
            return redi;
        }

        private static RED2Structs.IRED2Struct ConstructStruct(string name, IKeyValueCollection data)
        {
            return name switch
            {
                "m_InputDependencies" => new RED2Structs.InputDependencies(data.GetArray(name)),
                "m_AdditionalInputDependencies" => new RED2Structs.AdditionalInputDependencies(data.GetArray(name)),
                "m_ArgumentDependencies" => new RED2Structs.ArgumentDependencies(data.GetArray(name)),
                "m_SpecialDependencies" => new RED2Structs.SpecialDependencies(data.GetArray(name)),
                // CustomDependencies is gone
                "m_AdditionalRelatedFiles" => new RED2Structs.AdditionalRelatedFiles(data.GetArray(name)),
                "m_ChildResourceList" => new RED2Structs.ChildResourceList(data.GetArray<string>(name)),
                // ExtraIntData is in SearchableUserData
                // ExtraFloatData is in SearchableUserData
                // ExtraStringData is in SearchableUserData
                "m_WeakReferenceList" => new RED2Structs.WeakReferenceList(data.GetArray<string>(name)), // is new
                "m_SearchableUserData" => new RED2Structs.SearchableUserData(data.GetSubCollection(name)), // is new
                "m_SubassetReferences" => null, // is new
                "m_SubassetDefinitions" => null, // is new
                _ => throw new InvalidDataException($"Unknown struct in RED2 block: '{name}'"),
            };
        }
    }
}
