using System.Runtime.InteropServices;

namespace FortniteMatchCompiler;

internal static class NativeProcessErrorMode
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private const uint SemNoOpenFileErrorBox = 0x8000;

    private const uint ChildProcessDialogSuppression =
        SemFailCriticalErrors |
        SemNoGpFaultErrorBox |
        SemNoOpenFileErrorBox;

    internal static void SuppressChildProcessCrashDialogs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var previousMode = SetErrorMode(ChildProcessDialogSuppression);
        _ = SetErrorMode(previousMode | ChildProcessDialogSuppression);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint errorMode);
}
