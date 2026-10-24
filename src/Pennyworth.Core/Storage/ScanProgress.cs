namespace Pennyworth.Core.Storage;

/// <param name="CategoryBytes">Bytes per category, indexed by <c>(int)StorageCategory</c>.</param>
public sealed record ScanProgress(long FileCount, long TotalBytes, IReadOnlyList<long> CategoryBytes);
