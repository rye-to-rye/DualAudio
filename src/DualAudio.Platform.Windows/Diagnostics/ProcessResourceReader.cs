using System.Diagnostics;
using System.Runtime.InteropServices;
using DualAudio.Core.Diagnostics;

namespace DualAudio.Platform.Windows.Diagnostics;

/// <summary>Current-process resources: <see cref="Process"/> counters plus GDI/USER objects (GetGuiResources).</summary>
public sealed partial class ProcessResourceReader : IProcessResources
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    public ProcessResourceSnapshot Read()
    {
        using var process = Process.GetCurrentProcess(); // disposed: it holds a process handle
        process.Refresh();
        var pseudoHandle = GetCurrentProcess();
        return new ProcessResourceSnapshot(
            process.TotalProcessorTime,
            process.PrivateMemorySize64,
            process.HandleCount,
            (int)GetGuiResources(pseudoHandle, GrGdiObjects),
            (int)GetGuiResources(pseudoHandle, GrUserObjects),
            process.Threads.Count);
    }

    // Pseudo handle (-1): needs no CloseHandle.
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("user32.dll")]
    private static partial uint GetGuiResources(nint hProcess, uint uiFlags);
}
