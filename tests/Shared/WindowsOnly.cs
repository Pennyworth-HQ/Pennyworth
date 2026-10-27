using System.Runtime.CompilerServices;

namespace Pennyworth.Tests;

/// <summary>
/// A test that needs Windows itself (Win32 natives, the registry, <c>cmd.exe</c>, ACLs). On any other
/// OS it is reported as skipped, with the reason, instead of failing: the platform's own tests cover
/// the same ground there.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="WindowsFactAttribute"/>.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows.";
        }
    }
}
