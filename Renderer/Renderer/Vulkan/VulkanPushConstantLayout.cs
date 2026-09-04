using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Packs a shader's <see cref="ShaderLoader.ParsedShaderData.PushConstantDeclarations"/> - the loose,
/// non-<c>g_</c>/<c>F_</c> uniforms OpenGL sets directly per draw with <c>GL.ProgramUniform*</c> - into
/// a single push-constant block, using the same bucket-by-component-count packing
/// <see cref="GlobalsLayout"/> uses for the <c>Globals</c> uniform buffer (matrices, then vec4s, then
/// vec3s each paired with a filler scalar, then vec2s, then remaining scalars). Vulkan-only: OpenGL's
/// preprocessed source is never touched by this, only a separate copy of it is; see
/// <see cref="ApplyToSource"/>.
/// </summary>
public sealed class VulkanPushConstantLayout
{
    /// <summary>Name of the generated GLSL push-constant block.</summary>
    public const string BlockName = "PushConstants";

    /// <summary>Name the block instance is declared under, so a member reads as <c>pc.name</c>.</summary>
    public const string InstanceName = "pc";

    /// <summary>Gets the layout used by shaders that declare no push-constant candidates.</summary>
    public static VulkanPushConstantLayout Empty { get; } = new([]);

    private readonly Dictionary<string, GlobalsMember> members = [];

    /// <summary>Gets the packed members by uniform name.</summary>
    public IReadOnlyDictionary<string, GlobalsMember> Members => members;

    /// <summary>Gets the size of the push-constant block in bytes, zero when there is nothing to pack.</summary>
    public int Size { get; }

    /// <summary>Gets the GLSL declaration of the push-constant block, prepended to every stage of the shader.</summary>
    public string BlockSource { get; } = string.Empty;

    /// <summary>
    /// Merges the declarations collected from every stage of one shader and computes the packed layout.
    /// </summary>
    /// <param name="declarations">Declarations in source order; duplicates across stages are merged.</param>
    /// <exception cref="ShaderLoader.ShaderCompilerException">A uniform is declared with conflicting types.</exception>
    public static VulkanPushConstantLayout Build(IEnumerable<GlobalsDeclaration> declarations)
    {
        var merged = new Dictionary<string, GlobalsDeclaration>(StringComparer.Ordinal);

        foreach (var declaration in declarations)
        {
            if (!merged.TryGetValue(declaration.Name, out var existing))
            {
                merged.Add(declaration.Name, declaration);
                continue;
            }

            if (existing.Type != declaration.Type)
            {
                throw new ShaderLoader.ShaderCompilerException(
                    $"Uniform '{declaration.Name}' is declared as both '{GlobalsLayout.GetGlslName(existing.Type)}' and '{GlobalsLayout.GetGlslName(declaration.Type)}'");
            }
        }

        return merged.Count == 0 ? Empty : new VulkanPushConstantLayout([.. merged.Values]);
    }

    private VulkanPushConstantLayout(List<GlobalsDeclaration> declarations)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        // Deterministic, so the same shader always packs the same way regardless of stage compile order.
        declarations.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        List<GlobalsDeclaration> matrices = [], quads = [], triples = [], pairs = [], scalars = [];

        foreach (var declaration in declarations)
        {
            var bucket = GlobalsLayout.GetComponentCount(declaration.Type) switch
            {
                16 => matrices,
                4 => quads,
                3 => triples,
                2 => pairs,
                _ => scalars,
            };

            bucket.Add(declaration);
        }

        var builder = new StringBuilder(declarations.Count * 32);
        builder.Append(CultureInfo.InvariantCulture, $"layout(push_constant, std430) uniform {BlockName}\n{{\n");

        var offset = 0;

        void Place(GlobalsDeclaration declaration)
        {
            var alignment = GlobalsLayout.GetComponentCount(declaration.Type) switch { 1 => 4, 2 => 8, _ => 16 };
            offset = (offset + alignment - 1) & ~(alignment - 1);

            members.Add(declaration.Name, new GlobalsMember(declaration.Name, declaration.Type, offset));

            offset += GlobalsLayout.GetComponentCount(declaration.Type) * sizeof(float);

            builder.Append("    ");
            builder.Append(GlobalsLayout.GetGlslName(declaration.Type));
            builder.Append(' ');
            builder.Append(declaration.Name);
            builder.Append(";\n");
        }

        foreach (var declaration in matrices)
        {
            Place(declaration);
        }

        foreach (var declaration in quads)
        {
            Place(declaration);
        }

        var scalarIndex = 0;

        foreach (var declaration in triples)
        {
            Place(declaration);

            if (scalarIndex < scalars.Count)
            {
                Place(scalars[scalarIndex++]);
            }
        }

        foreach (var declaration in pairs)
        {
            Place(declaration);
        }

        for (; scalarIndex < scalars.Count; scalarIndex++)
        {
            Place(scalars[scalarIndex]);
        }

        builder.Append("} ").Append(InstanceName).Append(";\n");

        foreach (var member in members.Values)
        {
            builder.Append("#define ").Append(member.Name).Append(' ').Append(InstanceName).Append('.').Append(member.Name).Append('\n');
        }

        BlockSource = builder.ToString();
        Size = offset;
    }

    /// <summary>
    /// Removes each packed member's loose declaration from already-preprocessed GLSL and prepends the
    /// push-constant block plus a <c>#define name pc.name</c> alias per member, so the shader body
    /// needs no edits - the same trick <see cref="GlobalsLayout"/> uses for the <c>Globals</c> block.
    /// The source this is called on is a copy already handed to OpenGL as-is; OpenGL never sees this
    /// transformation or the block it produces.
    /// </summary>
    public string ApplyToSource(string preprocessedSource)
    {
        if (members.Count == 0)
        {
            return preprocessedSource;
        }

        var result = preprocessedSource;

        foreach (var member in members.Values)
        {
            var pattern = $@"^[ \t]*(?:layout\s*\([^)]*\)\s*)?uniform\s+(?:layout\s*\([^)]*\)\s*)?{Regex.Escape(GlobalsLayout.GetGlslName(member.Type))}\s+{Regex.Escape(member.Name)}\s*;.*$";
            result = Regex.Replace(result, pattern, $"// :VrfPushConstant {member.Name}", RegexOptions.Multiline);
        }

        return BlockSource + result;
    }
}
