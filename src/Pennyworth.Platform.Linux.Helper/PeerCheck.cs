using System.Runtime.InteropServices;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// What a <c>serve</c> session learns about the process on the other end of its pipes. The real set asks the kernel; a test
/// supplies its own, so nothing a test does can change what runs in production.
/// </summary>
/// <param name="ParentPid"><c>getppid()</c>. <c>pkexec</c> replaces itself with the helper (<c>execv</c>, no fork), so this is the process that ran <c>pkexec</c>: the app.</param>
/// <param name="OwnerOfProcess">The real user id of process <c>pid</c> (<c>/proc/&lt;pid&gt;/status</c>), null when the process is gone or unreadable.</param>
/// <param name="StdioArePipes">Whether standard input and output are pipes or sockets (what a parent that spawned the helper with redirected streams hands over), not files, terminals or devices.</param>
internal sealed record PeerSources(Func<int> ParentPid, Func<int, uint?> OwnerOfProcess, Func<bool> StdioArePipes)
{
    internal static PeerSources Real { get; } = new(
        static () => PeerCheckNative.GetParentPid(),
        static pid => PeerCheckNative.OwnerOfProcess(pid),
        static () => PeerCheckNative.IsPipe(0) && PeerCheckNative.IsPipe(1));
}

/// <summary>
/// The one check <c>serve</c> makes about who it talks to. A pipe has no peer credentials, so what can be verified is the
/// chain that created it: standard input and output are pipes (not a file or a terminal somebody redirected), and the process
/// that started the helper is owned by the very user <c>pkexec</c> authenticated (<c>PKEXEC_UID</c>). The helper serves that
/// process and nobody else: it opens no socket, file or FIFO another process could connect to, and it stops when its parent
/// changes (the app died and the helper was adopted). Like <see cref="CallerCheck"/>, nothing is checked without root rights:
/// then the helper has the caller's own rights and can do no more than the caller can.
/// </summary>
internal static class PeerCheck
{
    /// <summary>The pid of the parent to watch afterwards; null when nothing is enforced (not root).</summary>
    /// <exception cref="HelperException"><see cref="HelperExit.Refused"/> when the helper runs as root and its streams or parent are not the ones a spawning app would give it.</exception>
    internal static int? Verify(PeerSources sources, Caller caller)
    {
        if (!caller.IsRoot)
        {
            return null;
        }

        if (!sources.StdioArePipes())
        {
            throw Refuse("its input and output are not pipes from the process that started it");
        }

        int parent = sources.ParentPid();
        if (parent <= 1)
        {
            throw Refuse("it has no parent process to serve");
        }

        if (sources.OwnerOfProcess(parent) != caller.RequestingUid)
        {
            throw Refuse("its parent process does not belong to the user who authenticated");
        }

        return parent;
    }

    /// <summary>Whether the parent seen now is still the one <see cref="Verify"/> accepted.</summary>
    internal static bool ParentUnchanged(PeerSources sources, int parentAtStart) => sources.ParentPid() == parentAtStart;

    private static HelperException Refuse(string reason) => new(HelperExit.Refused, "refused: " + reason);
}

internal static unsafe partial class PeerCheckNative
{
    private const ushort FormatFifo = 0x1000;
    private const ushort FormatSocket = 0xC000;

    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int GetParentPidNative();

    internal static int GetParentPid() => GetParentPidNative();

    /// <summary>Whether descriptor <paramref name="fd"/> is a pipe or a socket.</summary>
    internal static bool IsPipe(int fd)
    {
        if (SafeFs.StatFd(fd, out StatxBuffer buffer) != 0)
        {
            return false;
        }

        ushort format = SafeFs.Format(in buffer);
        return format is FormatFifo or FormatSocket;
    }

    /// <summary>
    /// The real user id of process <paramref name="pid"/>, from the <c>Uid:</c> line of <c>/proc/&lt;pid&gt;/status</c> (the owner of
    /// the <c>/proc/&lt;pid&gt;</c> folder would be root for a process that turned off its dumpability); null when it cannot be read.
    /// </summary>
    internal static uint? OwnerOfProcess(int pid)
    {
        try
        {
            using var stream = new FileStream("/proc/" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/status", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] buffer = new byte[8192];
            int length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return ParseRealUid(System.Text.Encoding.UTF8.GetString(buffer, 0, length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The first number after <c>Uid:</c> in the text of a <c>status</c> file; null when there is none.</summary>
    internal static uint? ParseRealUid(string status)
    {
        foreach (string line in status.Split('\n'))
        {
            if (!line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[4..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 0 && uint.TryParse(fields[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint uid) ? uid : null;
        }

        return null;
    }
}
