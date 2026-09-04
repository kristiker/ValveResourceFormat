using System.Reflection;
using System.Runtime.InteropServices;

// glslang is loaded by absolute path through a DllImportResolver, so the search path the runtime
// would otherwise use never comes into play. Declaring the safe set keeps CA5392 satisfied.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Compiles the renderer's GLSL shader strings to Vulkan-flavored SPIR-V through glslang's C API,
/// the one native dependency this backend adds: shaders are still authored and preprocessed exactly
/// as the OpenGL backend does, this is purely the offline compile step OpenGL never needed because
/// its driver takes GLSL text directly.
/// </summary>
public static partial class VulkanGlslang
{
    private const string Library = "glslang";
    private const string ResourceLibrary = "glslang-default-resource-limits";

    private const int SourceGlsl = 1;

    private const int ClientVulkan = 1;

    // glslang_target_client_version_t: (1 << 22) | (minor << 12), mirroring VK_MAKE_API_VERSION.
    private const int ClientVersionVulkan13 = (1 << 22) | (3 << 12);

    private const int TargetLanguageSpv = 1;

    // glslang_target_language_version_t: (major << 16) | (minor << 8).
    private const int TargetLanguageVersionSpv15 = (1 << 16) | (5 << 8);

    private const int ProfileCore = 1 << 1;

    // GLSLANG_MSG_SPV_RULES_BIT | GLSLANG_MSG_VULKAN_RULES_BIT: emit SPIR-V under Vulkan's validation
    // rules, not OpenGL's - the one flag that actually changes what comes out, versus the GL-flavored
    // wrapper this was ported from.
    private const int MessagesSpvAndVulkanRules = (1 << 3) | (1 << 4);

    /// <summary>Values match glslang's own <c>glslang_stage_t</c>, not <see cref="ShaderProgramType"/>'s ordering.</summary>
    public enum Stage
    {
        /// <summary>Vertex stage.</summary>
        Vertex = 0,

        /// <summary>Fragment (pixel) stage.</summary>
        Fragment = 4,

        /// <summary>Compute stage.</summary>
        Compute = 5,
    }

    /// <summary>
    /// glslang_input_t. The field order has to match glslang's header exactly, including the three
    /// HLSL fields this wrapper never sets, or every field after <c>Code</c> lands on the wrong one.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Language;
        public int Stage;
        public int Client;
        public int ClientVersion;
        public int TargetLanguage;
        public int TargetLanguageVersion;
        public nint Code;
        public nint EntryPoint;
        public nint SourceEntryPoint;
        public int HlslFunctionality1;
        public int DefaultVersion;
        public int DefaultProfile;
        public int ForceDefaultVersionAndProfile;
        public int ForwardCompatible;
        public int Messages;
        public nint Resource;
        public nint IncludeLocal;
        public nint IncludeSystem;
        public nint FreeIncludeResult;
        public nint CallbacksContext;
    }

    /// <summary>The size glslang's header says <see cref="Input"/> has, checked once before use.</summary>
    private const int ExpectedInputSize = 112;

    [LibraryImport(Library)]
    private static partial int glslang_initialize_process();

    [LibraryImport(Library)]
    private static partial nint glslang_shader_create(in Input input);

    [LibraryImport(Library)]
    private static partial void glslang_shader_delete(nint shader);

    [LibraryImport(Library)]
    private static partial int glslang_shader_preprocess(nint shader, in Input input);

    [LibraryImport(Library)]
    private static partial int glslang_shader_parse(nint shader, in Input input);

    [LibraryImport(Library)]
    private static partial nint glslang_shader_get_info_log(nint shader);

    [LibraryImport(Library)]
    private static partial void glslang_shader_set_options(nint shader, int options);

    [LibraryImport(Library)]
    private static partial nint glslang_program_create();

    [LibraryImport(Library)]
    private static partial void glslang_program_delete(nint program);

    [LibraryImport(Library)]
    private static partial void glslang_program_add_shader(nint program, nint shader);

    [LibraryImport(Library)]
    private static partial int glslang_program_link(nint program, int messages);

    [LibraryImport(Library)]
    private static partial nint glslang_program_get_info_log(nint program);

    [LibraryImport(Library)]
    private static partial void glslang_program_SPIRV_generate(nint program, int stage);

    [LibraryImport(Library)]
    private static partial nuint glslang_program_SPIRV_get_size(nint program);

    [LibraryImport(Library)]
    private static partial void glslang_program_SPIRV_get(nint program, nint output);

    [LibraryImport(ResourceLibrary)]
    private static partial nint glslang_default_resource();

    // GLSLANG_SHADER_AUTO_MAP_LOCATIONS: numbers in/out interface variables that have no explicit
    // layout(location=), which the renderer's shaders rely on the driver to do for OpenGL.
    private const int OptionsAutoMapLocations = 1 << 1;

    private static bool initialized;
    private static Exception? initializeFailure;

    /// <summary>
    /// Loads glslang from the Glslang.NET package. Throws once, with a message identifying what is
    /// missing, if it cannot; every later call re-throws the same failure instead of trying again.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        if (initializeFailure != null)
        {
            throw initializeFailure;
        }

        try
        {
            if (!NativeLibrary.TryLoad(Library, Assembly.GetExecutingAssembly(), null, out var packageHandle))
            {
                throw new DllNotFoundException($"{Library} was not found next to the executable; it should come from the Glslang.NET package.");
            }

            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) => name switch
            {
                Library or ResourceLibrary => packageHandle,
                _ => nint.Zero,
            });

            if (Marshal.SizeOf<Input>() != ExpectedInputSize)
            {
                throw new InvalidOperationException($"glslang_input_t is {Marshal.SizeOf<Input>()} bytes here but should be {ExpectedInputSize}; the Glslang.NET package version likely changed its layout.");
            }

            if (glslang_initialize_process() == 0)
            {
                throw new InvalidOperationException("glslang failed to initialize.");
            }

            if (glslang_default_resource() == nint.Zero)
            {
                throw new InvalidOperationException("glslang has no default resource limits.");
            }
        }
        catch (Exception e)
        {
            initializeFailure = e;
            throw;
        }

        initialized = true;
    }

    /// <summary>
    /// Compiles one preprocessed GLSL stage to Vulkan SPIR-V 1.5, which every device this backend
    /// selects supports (it requires synchronization2 and dynamic rendering, both core since 1.3).
    /// </summary>
    public static byte[] Compile(string source, Stage stage)
    {
        EnsureInitialized();

        var shader = CreateShader(source, stage);

        try
        {
            var program = glslang_program_create();

            try
            {
                glslang_program_add_shader(program, shader);

                if (glslang_program_link(program, MessagesSpvAndVulkanRules) == 0)
                {
                    throw new ShaderCompilerException($"glslang failed to link the {stage} stage:\n{Text(glslang_program_get_info_log(program))}");
                }

                glslang_program_SPIRV_generate(program, (int)stage);

                var wordCount = (int)glslang_program_SPIRV_get_size(program);
                var spirv = new byte[wordCount * sizeof(uint)];

                unsafe
                {
                    fixed (byte* pSpirv = spirv)
                    {
                        glslang_program_SPIRV_get(program, (nint)pSpirv);
                    }
                }

                return spirv;
            }
            finally
            {
                glslang_program_delete(program);
            }
        }
        finally
        {
            glslang_shader_delete(shader);
        }
    }

    private static unsafe nint CreateShader(string source, Stage stage)
    {
        var codePtr = Marshal.StringToCoTaskMemUTF8(source);

        try
        {
            var input = new Input
            {
                Language = SourceGlsl,
                Stage = (int)stage,
                Client = ClientVulkan,
                ClientVersion = ClientVersionVulkan13,
                TargetLanguage = TargetLanguageSpv,
                TargetLanguageVersion = TargetLanguageVersionSpv15,
                Code = codePtr,
                DefaultVersion = 450,
                DefaultProfile = ProfileCore,
                Messages = MessagesSpvAndVulkanRules,
                Resource = glslang_default_resource(),
            };

            var shader = glslang_shader_create(in input);
            glslang_shader_set_options(shader, OptionsAutoMapLocations);

            if (glslang_shader_preprocess(shader, in input) == 0)
            {
                var message = $"glslang failed to preprocess the {stage} stage:\n{Text(glslang_shader_get_info_log(shader))}";
                glslang_shader_delete(shader);
                throw new ShaderCompilerException(message);
            }

            if (glslang_shader_parse(shader, in input) == 0)
            {
                var message = $"glslang failed to parse the {stage} stage:\n{Text(glslang_shader_get_info_log(shader))}";
                glslang_shader_delete(shader);
                throw new ShaderCompilerException(message);
            }

            return shader;
        }
        finally
        {
            Marshal.FreeCoTaskMem(codePtr);
        }
    }

    private static unsafe string Text(nint ptr) => ptr == nint.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;

    /// <summary>Thrown when glslang rejects a shader.</summary>
    public sealed class ShaderCompilerException(string message) : Exception(message);
}
