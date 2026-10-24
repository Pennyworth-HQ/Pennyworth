using System.Collections.Concurrent;
using Pennyworth.Core.Storage;
using Pennyworth.Scanning.Native;

namespace Pennyworth.Scanning;

/// <summary>
/// Scans any drive or folder without admin rights by listing directories in parallel.
/// Folders Windows refuses to list are recorded as locked and the scan continues.
/// </summary>
public sealed class DirectoryScanner : IStorageScanner
{
    private readonly int _workerCount;
    private readonly TimeSpan _progressInterval;

    public DirectoryScanner(int? workerCount = null, TimeSpan? progressInterval = null)
    {
        _workerCount = workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 16);
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(150);
    }

    public Task<StorageTree> ScanAsync(
        string rootPath,
        IPathClassifier classifier,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => new ScanRun(rootPath, classifier, _workerCount).Run(progress, _progressInterval, cancellationToken), cancellationToken);
    }

    private sealed class ScanRun
    {
        private readonly IPathClassifier _classifier;
        private readonly int _workerCount;
        private readonly long _clusterSize;
        private readonly StorageTreeBuilder _builder;
        private readonly object _builderLock = new();
        private readonly ConcurrentStack<WorkItem> _pending = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _outstanding;

        public ScanRun(string rootPath, IPathClassifier classifier, int workerCount)
        {
            _classifier = classifier;
            _workerCount = workerCount;
            _clusterSize = NativeDirectory.GetClusterSize(rootPath);
            StorageCategory rootCategory = classifier.Classify(rootPath, StorageCategory.Other);
            _builder = new StorageTreeBuilder(rootPath, rootCategory);
            Enqueue(new WorkItem(StorageTreeBuilder.RootIndex, rootPath, rootCategory, IsRoot: true));
        }

        public StorageTree Run(IProgress<ScanProgress>? progress, TimeSpan progressInterval, CancellationToken cancellationToken)
        {
            using var timer = progress is null ? null : new Timer(_ => Report(progress), null, progressInterval, progressInterval);
            Task[] workers = Enumerable.Range(0, _workerCount)
                .Select(_ => Task.Factory.StartNew(() => Work(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            try
            {
                Task.WaitAll(workers, cancellationToken);
            }
            catch (AggregateException) when (cancellationToken.IsCancellationRequested)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            timer?.Dispose();
            if (progress is not null)
            {
                Report(progress);
            }

            lock (_builderLock)
            {
                return _builder.Build();
            }
        }

        private void Work(CancellationToken cancellationToken)
        {
            var entries = new List<DirectoryEntry>();
            while (true)
            {
                _available.Wait(cancellationToken);
                if (!_pending.TryPop(out WorkItem item))
                {
                    return; // Woken up because the scan is finished.
                }

                try
                {
                    entries.Clear();
                    Process(item, entries);
                }
                finally
                {
                    if (Interlocked.Decrement(ref _outstanding) == 0)
                    {
                        _available.Release(_workerCount);
                    }
                }
            }
        }

        private void Process(WorkItem item, List<DirectoryEntry> entries)
        {
            if (!NativeDirectory.TryList(item.Path, entries))
            {
                lock (_builderLock)
                {
                    _builder.MarkLocked(item.Index);
                }

                return;
            }

            // Classify and size outside the lock; the lock only guards the builder.
            var directories = new List<(DirectoryEntry Entry, string Path, StorageCategory Category)>();
            var files = new List<(DirectoryEntry Entry, long Allocated, StorageCategory Category)>(entries.Count);
            foreach (DirectoryEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (SizeOnDisk.ShouldEnterDirectory(entry.Attributes, entry.ReparseTag))
                    {
                        string path = Path.Join(item.Path, entry.Name);
                        directories.Add((entry, path, _classifier.Classify(path, item.Category)));
                    }
                }
                else
                {
                    StorageCategory category = item.IsRoot
                        ? _classifier.Classify(Path.Join(item.Path, entry.Name), item.Category)
                        : item.Category;
                    long? stored = SizeOnDisk.NeedsStoredSize(entry.Attributes)
                        ? NativeDirectory.GetStoredSize(Path.Join(item.Path, entry.Name))
                        : null;
                    files.Add((entry, SizeOnDisk.Estimate(entry.Attributes, entry.Size, _clusterSize, stored), category));
                }
            }

            var children = new WorkItem[directories.Count];
            lock (_builderLock)
            {
                foreach (var (entry, allocated, category) in files)
                {
                    _builder.AddFile(item.Index, entry.Name, allocated, entry.Size, category);
                }

                for (int i = 0; i < directories.Count; i++)
                {
                    var (entry, path, category) = directories[i];
                    int index = _builder.AddDirectory(item.Index, entry.Name, category);
                    children[i] = new WorkItem(index, path, category, IsRoot: false);
                }
            }

            foreach (WorkItem child in children)
            {
                Enqueue(child);
            }
        }

        private void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref _outstanding);
            _pending.Push(item);
            _available.Release();
        }

        private void Report(IProgress<ScanProgress> progress)
        {
            ScanProgress snapshot;
            lock (_builderLock)
            {
                snapshot = new ScanProgress(_builder.FileCount, _builder.TotalBytes, _builder.SnapshotCategoryBytes());
            }

            progress.Report(snapshot);
        }
    }

    private readonly record struct WorkItem(int Index, string Path, StorageCategory Category, bool IsRoot);
}
