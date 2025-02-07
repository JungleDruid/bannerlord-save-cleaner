using System.Diagnostics;

namespace SaveCleaner.Utils;

public static class SafeDebugger
{
    [Conditional("DEBUG")]
    public static void Break() => Debugger.Break();
}