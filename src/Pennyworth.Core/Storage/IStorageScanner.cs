namespace Pennyworth.Core.Storage;

public interface IStorageScanner
{
    Task<StorageTree> ScanAsync(
        string rootPath,
        IPathClassifier classifier,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}
