using System.Collections.Generic;
using ValveResourceFormat.Serialization;
using ValveResourceFormat.Serialization.KeyValues;
using REDIStructs = ValveResourceFormat.Blocks.ResourceEditInfoStructs;

namespace ValveResourceFormat.Blocks.RED2Structs
{
    public interface IRED2Struct
    {
        public BlockType Type => BlockType.RED2;
        // BackingData ?
    }

    public class InputDependencies : REDIStructs.InputDependencies, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public InputDependencies(IKeyValueCollection[] m_InputDependencies)
        {
            foreach (var inputDependency in m_InputDependencies)
            {
                var inputDependencyRedi = new InputDependencies.InputDependency
                {
                    ContentRelativeFilename = inputDependency.GetProperty<string>("m_RelativeFilename"),
                    ContentSearchPath = inputDependency.GetProperty<string>("m_SearchPath"),
                    FileCRC = (uint)inputDependency.GetUnsignedIntegerProperty("m_nFileCRC"),
                    // TODO: These get added to Flags
                    //inputDepenency.GetProperty<bool>("m_bOptional"),
                    //inputDepenency.GetProperty<bool>("m_bFileExists"),
                    //inputDepenency.GetProperty<bool>("m_bIsGameFile"),

                };

                List.Add(inputDependencyRedi);
            }
        }
    }

    public class AdditionalInputDependencies : RED2Structs.InputDependencies, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public AdditionalInputDependencies(IKeyValueCollection[] m_AdditionalInputDependencies)
            : base(m_AdditionalInputDependencies)
        {}
    }

    public class ArgumentDependencies : REDIStructs.ArgumentDependencies, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public ArgumentDependencies(IKeyValueCollection[] m_ArgumentDependencies)
        {
            foreach (var argumentDependency in m_ArgumentDependencies)
            {
                var argumentDependencyRedi = new ArgumentDependencies.ArgumentDependency
                {
                    ParameterName = argumentDependency.GetProperty<string>("m_ParameterName"),
                    ParameterType = argumentDependency.GetProperty<string>("m_ParameterType"),
                    Fingerprint = (uint)argumentDependency.GetUnsignedIntegerProperty("m_nFingerprint"),
                    FingerprintDefault = (uint)argumentDependency.GetUnsignedIntegerProperty("m_nFingerprintDefault"),
                };

                List.Add(argumentDependencyRedi);
            }
        }
    }

    public class SpecialDependencies : REDIStructs.SpecialDependencies, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public SpecialDependencies(IKeyValueCollection[] m_SpecialDependencies)
        {
            foreach (var specialDependency in m_SpecialDependencies)
            {
                var specialDependencyRedi = new SpecialDependencies.SpecialDependency
                {
                    String = specialDependency.GetProperty<string>("m_String"),
                    CompilerIdentifier = specialDependency.GetProperty<string>("m_CompilerIdentifier"),
                    Fingerprint = specialDependency.GetIntegerProperty("m_nFingerprint"),
                    UserData = specialDependency.GetIntegerProperty("m_nUserData"),
                };

                List.Add(specialDependencyRedi);
            }
        }
    }

    // CustomDependencies

    public class AdditionalRelatedFiles : REDIStructs.AdditionalRelatedFiles, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public AdditionalRelatedFiles(IKeyValueCollection[] m_AdditionalRelatedFiles)
        {
            foreach (var additionalRelatedFile in m_AdditionalRelatedFiles)
            {
                var additionalRelatedFileRedi = new AdditionalRelatedFiles.AdditionalRelatedFile
                {
                    ContentRelativeFilename = additionalRelatedFile.GetProperty<string>("m_RelativeFilename"),
                    ContentSearchPath = additionalRelatedFile.GetProperty<string>("m_SearchPath"),
                    // new field
                    //additionalRelatedFile.GetProperty<bool>("m_bIsGameFile"),
                };

                List.Add(additionalRelatedFileRedi);
            }
        }
    }

    public class ChildResourceList : REDIStructs.ChildResourceList, IRED2Struct
    {
        public override BlockType Type => BlockType.RED2;

        public ChildResourceList(IKeyValueCollection m_ChildResourceList)
        {
            foreach (var additionalRelatedFile in m_ChildResourceList)
            {
                var referenceInfoRedi = new ChildResourceList.ReferenceInfo
                {
                    // no Id
                    ResourceName = (string)additionalRelatedFile.Value
                };

                List.Add(referenceInfoRedi);
            }
        }
    }

    // ExtraIntData
    // ExtraFloatData
    // ExtraStringData

    public class WeakReferenceList : IRED2Struct
    {
        public static BlockType Type => BlockType.RED2;

        // dunno what objects
        public List<object> List { get; }

        public WeakReferenceList()
        {
            List = null;
        }

        public WeakReferenceList(KVValue m_WeakReferenceList)
        {
            List = new List<object>();
        }
    }

    public class SearchableUserData : IRED2Struct
    {
        public static BlockType Type => BlockType.RED2;

        public Dictionary<string, object> Data { get; }

        public SearchableUserData()
        {
            Data = null;
        }

        public SearchableUserData(KVValue m_SearchableUserData)
        {
            if (m_SearchableUserData is null)
            {
                Data = null;
            }
            else
            {
                Data = new Dictionary<string, object>();
                foreach (var (key, value) in (IKeyValueCollection)m_SearchableUserData.Value)
                {
                    Data.Add(key, value);
                }
            }
        }

    }

    public class SubassetReferences : IRED2Struct
    {
        public static BlockType Type => BlockType.RED2;

        public Dictionary<string, object> Data { get; }

        public SubassetReferences()
        {
            Data = null;
        }

        public SubassetReferences(KVValue m_SubassetReferences)
        {
            if (m_SubassetReferences is null)
            {
                Data = null;
            }
            else
            {
                Data = new Dictionary<string, object>();
                foreach (var (key, value) in (IKeyValueCollection)m_SubassetReferences.Value)
                {
                    Data.Add(key, value);
                }
            }
        }

    }

    public class SubassetDefinitions : IRED2Struct
    {
        public static BlockType Type => BlockType.RED2;

        public Dictionary<string, object> Data { get; }

        public SubassetDefinitions()
        {
            Data = null;
        }

        public SubassetDefinitions(IKeyValueCollection m_SubassetDefinitions)
        {
            if (m_SubassetDefinitions is null)
            {
                Data = null;
            }
            else
            {
                Data = new Dictionary<string, object>();
                foreach (var (key, value) in m_SubassetDefinitions)
                {
                    Data.Add(key, value);
                }
            }
        }

    }
}
