using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Assistant;

/// <summary>A drive as the app shows it.</summary>
/// <param name="Root">"C:\".</param>
/// <param name="IsSystem">The drive Windows runs from.</param>
public sealed record DriveFact(string Root, string? Label, long TotalBytes, long FreeBytes, bool IsSystem)
{
    public long UsedBytes => Math.Max(TotalBytes - FreeBytes, 0);

    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
}

/// <summary>
/// Everything the app has measured, handed to the assistant. The assistant never reads Windows
/// itself; missing parts are null or empty and simply produce fewer facts.
/// </summary>
/// <param name="RecentSamples">Live monitor samples, oldest first, about one per second.</param>
/// <param name="SystemDriveCategories">Bytes per category from the last scan of the system drive.</param>
/// <param name="IsUnlocked">Whether the full-scan permission was granted.</param>
public sealed record PcSnapshot(
    PcSpecs? Specs,
    IReadOnlyList<MonitorSample> RecentSamples,
    IReadOnlyList<DriveFact> Drives,
    IReadOnlyDictionary<StorageCategory, long>? SystemDriveCategories,
    IReadOnlyList<CleanupEstimate> Cleanup,
    bool IsUnlocked)
{
    public static PcSnapshot Empty { get; } = new(null, [], [], null, [], false);

    public MonitorSample? Latest => RecentSamples.Count == 0 ? null : RecentSamples[^1];
}
