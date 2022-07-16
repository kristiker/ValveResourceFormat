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
                    if (i == RED2Struct.ChildResourceList)
                        continue;
                    var value = kv3.AsKeyValueCollection().GetArray(keyName);
                    var block = ConstructStruct(keyName, value);

                    Structs.Add((REDIStruct)i, (REDIStructs.REDIBlock)block);
                }
                else
                {
                    Console.WriteLine("Construct RED2 struct: " + keyName);
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

        private static RED2Structs.IRED2Struct ConstructStruct(string name, IKeyValueCollection[] value)
        {
            return name switch
            {
                "m_InputDependencies" => new RED2Structs.InputDependencies(value),
                "m_AdditionalInputDependencies" => new RED2Structs.AdditionalInputDependencies(value),
                "m_ArgumentDependencies" => new RED2Structs.ArgumentDependencies(value),
                "m_SpecialDependencies" => new RED2Structs.SpecialDependencies(value),
                // CustomDependencies is gone
                "m_AdditionalRelatedFiles" => new RED2Structs.AdditionalRelatedFiles(value),
                "m_ChildResourceList" => null, // new RED2Structs.ChildResourceList((IKeyValueCollection)value)
                // ExtraIntData is gone
                // ExtraFloatData is gone
                // ExtraStringData is gone
                "m_WeakReferenceList" => null, // is new
                "m_SearchableUserData" => null, // is new
                "m_SubassetReferences" => null, // is new
                "m_SubassetDefinitions" => null, // is new
                _ => throw new InvalidDataException($"Unknown struct in RED2 block: '{name}'"),
            };
        }
    }
}
