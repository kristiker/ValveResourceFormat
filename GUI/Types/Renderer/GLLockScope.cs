using System.Runtime.CompilerServices;
using System.Threading;
using OpenTK.Windowing.Desktop;

namespace GUI.Types.Renderer;

readonly ref struct GLLockScope
{
#pragma warning disable CA2213 // Disposable fields should be disposed
    private readonly Lock.Scope lockScope;
#pragma warning restore CA2213 // Ref structs implicitly have Dispose method and do not implement the IDisposable interface
    private readonly IGLFWGraphicsContext context;

    private readonly Tracy.PInvoke.TracyCZoneCtx tracyContext;

    public GLLockScope(Lock glLock, IGLFWGraphicsContext context,
        [CallerLineNumber] uint lineNumber = 0,
        [CallerFilePath] string? filePath = null,
        [CallerMemberName] string? memberName = null)
    {
        lockScope = glLock.EnterScope();
        this.context = context;

        using var filestr = Profiler.Profiler.GetCString(filePath, out var fileln);
        using var memberstr = Profiler.Profiler.GetCString(memberName, out var memberln);
        using var namestr = Profiler.Profiler.GetCString("GLContextLock", out var nameln);
        var sourceLocationString = Tracy.PInvoke.TracyAllocSrclocName(lineNumber, filestr, fileln, memberstr, memberln, namestr, nameln, 0);
        tracyContext = Tracy.PInvoke.TracyEmitZoneBeginAlloc(sourceLocationString, 1);

        context.MakeCurrent();
    }

    public readonly void Dispose()
    {
        context.MakeNoneCurrent();
        lockScope.Dispose();
        Tracy.PInvoke.TracyEmitZoneEnd(tracyContext);
    }
}
