namespace Pennyworth.Platform.Linux;

/// <summary>
/// The four things the helper does, with the validation each applies to what it was asked. The one-shot subcommands
/// (<see cref="HelperApp"/>) and the requests of a <c>serve</c> session (<see cref="HelperServe"/>) both call these and nothing
/// else, so a session can do exactly what a subcommand can: no more, and with the same refusals.
/// </summary>
internal static class HelperOperations
{
    internal static FirmwareTablesResponse ReadFirmwareTables(HelperSeams seams, CancellationToken cancellationToken) =>
        seams.ReadFirmwareTables(cancellationToken);

    /// <param name="mountPoint">Validated here against the mount table (<see cref="HelperApp.ValidateMountPoint"/>).</param>
    internal static SystemSpaceResponse MeasureSystemSpace(HelperSeams seams, string mountPoint, CancellationToken cancellationToken) =>
        seams.MeasureSystemSpace(HelperApp.ValidateMountPoint(mountPoint, seams.ReadMountInfo()), cancellationToken);

    internal static FoldersResponse MeasureFolders(HelperSeams seams, FoldersRequest request, CancellationToken cancellationToken)
    {
        RequireVersion(request.Version);
        if (request.Paths is null || request.Paths.Any(path => path is null))
        {
            throw HelperApp.Usage("the request is incomplete");
        }

        if (request.Paths.Count > HelperProtocol.MaxFolders)
        {
            throw new HelperException(HelperExit.TooLarge, "the request names too many folders");
        }

        return seams.MeasureFolders(request, cancellationToken);
    }

    /// <summary>Whether this caller may delete and <paramref name="ruleId"/> is one of the compiled-in rules; call before reading a request so a refused caller is not listened to.</summary>
    internal static void RequireCleaning(Caller caller, string ruleId)
    {
        if (!caller.IsRoot)
        {
            throw new HelperException(HelperExit.Refused, "cleaning system folders needs administrator rights");
        }

        if (SystemCleanupRules.FoldersOf(ruleId) is null)
        {
            throw new HelperException(HelperExit.Refused, "unknown cleanup rule");
        }
    }

    internal static CleanResponse CleanSystem(HelperSeams seams, Caller caller, string ruleId, CleanRequest request, CancellationToken cancellationToken)
    {
        RequireCleaning(caller, ruleId);
        RequireVersion(request.Version);
        if (request.Files is null || request.Files.Any(file => file?.Root is null || file.Path is null))
        {
            throw HelperApp.Usage("the request is incomplete");
        }

        if (request.Files.Count > HelperProtocol.MaxCleanFiles)
        {
            throw new HelperException(HelperExit.TooLarge, "the request lists too many files");
        }

        return seams.CleanSystem(ruleId, request, cancellationToken);
    }

    internal static void RequireVersion(int version)
    {
        if (version != HelperProtocol.Version)
        {
            throw HelperApp.Usage("unsupported protocol version");
        }
    }
}
