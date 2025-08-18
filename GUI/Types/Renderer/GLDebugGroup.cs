using System.Runtime.CompilerServices;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer;

ref struct GLDebugGroup
{
    private Tracy.PInvoke.TracyCZoneCtx context;

    public GLDebugGroup(string name,
        [CallerLineNumber] uint lineNumber = 0,
        [CallerFilePath] string? filePath = null,
        [CallerMemberName] string? memberName = null
    )
    {
        using var filestr = Profiler.Profiler.GetCString(filePath, out var fileln);
        using var memberstr = Profiler.Profiler.GetCString(memberName, out var memberln);
        using var namestr = Profiler.Profiler.GetCString(name, out var nameln);
        var sourceLocationString = Tracy.PInvoke.TracyAllocSrclocName(lineNumber, filestr, fileln, memberstr, memberln, namestr, nameln, 0);
        context = Tracy.PInvoke.TracyEmitZoneBeginAlloc(sourceLocationString, 1);
#if DEBUG
        GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 0, name.Length, name);
#endif
    }

#pragma warning disable CA1822 // Mark members as static
    public readonly void Dispose()
#pragma warning restore CA1822
    {
        Tracy.PInvoke.TracyEmitZoneEnd(context);
#if DEBUG
        GL.PopDebugGroup();
#endif
    }
}
