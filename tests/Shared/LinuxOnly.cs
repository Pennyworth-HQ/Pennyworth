using System.Runtime.CompilerServices;

namespace Pennyworth.Tests;

/// <summary>
/// A test that reads the machine it runs on (<c>/proc</c>, <c>/sys</c>, mount tables, signals). On any
/// other OS it is reported as skipped with the reason. Tests that parse a checked-in fixture need no
/// attribute: they run everywhere.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux.";
        }
    }
}

/// <summary>The <see cref="TheoryAttribute"/> counterpart of <see cref="LinuxFactAttribute"/>.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Needs Linux.";
        }
    }
}
