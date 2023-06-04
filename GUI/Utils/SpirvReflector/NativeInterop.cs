using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GUI.Utils.SpirvReflector;

public static class SpirvReflector
{
#pragma warning disable CA5392 // Use DefaultDllImportSearchPaths attribute for P/Invokes
    [DllImport("SpirvReflector.dll")]
    private static extern IntPtr CreateSpirvReflector();

    [DllImport("SpirvReflector.dll")]
    private static extern int PushUInt32(IntPtr decompiler, uint val);

    [DllImport("SpirvReflector.dll")]
    private static extern char Parse(IntPtr decompiler);

    [DllImport("SpirvReflector.dll")]
    private static extern int GetDataLength(IntPtr decompiler);

    [DllImport("SpirvReflector.dll")]
    private static extern char GetChar(IntPtr decompiler, int i);
#pragma warning restore CA5392

#pragma warning disable CA1806 // Ignore HRESULT error code
    public static string DecompileSpirv(byte[] databytes)
    {
        var decompiler = CreateSpirvReflector(); // TODO: destroy
        for (var i = 0; i < databytes.Length; i += 4)
        {
            var b0 = (uint)databytes[i + 0];
            var b1 = (uint)databytes[i + 1];
            var b2 = (uint)databytes[i + 2];
            var b3 = (uint)databytes[i + 3];
            var nextUInt32 = b3 + (b2 << 8) + (b1 << 16) + (b0 << 24);
            PushUInt32(decompiler, nextUInt32);
        }
        Parse(decompiler);
        var len = GetDataLength(decompiler);
        var sb = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            var c = GetChar(decompiler, i);
            sb.Append(c);
        }
        return sb.ToString();
    }
#pragma warning restore CA1806
}
