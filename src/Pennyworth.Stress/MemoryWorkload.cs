namespace Pennyworth.Stress;

/// <summary>
/// Fills a large share of memory with patterns that depend on each word's position and the pass,
/// then reads every word back and checks it, like OCCT's memory test and HeavyLoad's "allocate
/// memory". A word that reads back different from what was written is a memory error.
/// </summary>
public sealed class MemoryWorkload : Workload
{
    private const ulong Golden = 0x9E37_79B9_7F4A_7C15;

    private readonly long _bytes;
    private readonly int _blockWords;
    private readonly int _maxThreads;
    private ulong[][] _blocks = [];

    /// <param name="bytes">Memory to test; rounded down to whole blocks.</param>
    /// <param name="blockBytes">Size of each allocation; tests use small ones.</param>
    public MemoryWorkload(long bytes, int threads = 0, int blockBytes = 32 * 1024 * 1024)
    {
        _blockWords = blockBytes / sizeof(ulong);
        long blocks = Math.Max(1, bytes / blockBytes);
        _bytes = blocks * blockBytes;
        _maxThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2);
    }

    public override StressKind Kind => StressKind.Memory;

    public override int Threads => (int)Math.Min(_maxThreads, BlockCount);

    public override long Bytes => _bytes;

    private long BlockCount => _bytes / ((long)_blockWords * sizeof(ulong));

    /// <summary>Lets tests flip a bit after a block is written, as failing memory would.</summary>
    internal Action<ulong[]>? Tamper { get; set; }

    protected override bool Prepare()
    {
        if (_bytes > GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)
        {
            Fail(WorkloadFailure.NotEnoughMemory, null);
            return false;
        }

        var blocks = new ulong[BlockCount][];
        for (long i = 0; i < blocks.Length; i++)
        {
            blocks[i] = GC.AllocateUninitializedArray<ulong>(_blockWords);
        }

        _blocks = blocks;
        return true;
    }

    protected override void Work(int index, CancellationToken token)
    {
        for (ulong pass = 0; !token.IsCancellationRequested; pass++)
        {
            // Odd passes write the inverse, so every bit is checked as both 0 and 1.
            ulong invert = (pass & 1) == 0 ? 0 : ulong.MaxValue;
            ulong seed = (pass * Golden) ^ invert;
            for (int b = index; b < _blocks.Length && !token.IsCancellationRequested; b += Threads)
            {
                ulong[] block = _blocks[b];
                ulong blockSeed = seed ^ ((ulong)b << 40);
                Fill(block, blockSeed);
                Tamper?.Invoke(block);
                long wrong = Check(block, blockSeed);
                if (wrong > 0)
                {
                    Error(wrong);
                }

                Count(2L * block.Length * sizeof(ulong));
            }
        }
    }

    protected override void Release()
    {
        _blocks = [];
        // Hand the memory back to Windows now rather than at the next collection.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    internal static void Fill(ulong[] block, ulong seed)
    {
        for (int i = 0; i < block.Length; i++)
        {
            block[i] = ((ulong)i * Golden) ^ seed;
        }
    }

    /// <summary>Words that differ from what <see cref="Fill"/> wrote.</summary>
    internal static long Check(ulong[] block, ulong seed)
    {
        long wrong = 0;
        for (int i = 0; i < block.Length; i++)
        {
            if (block[i] != (((ulong)i * Golden) ^ seed))
            {
                wrong++;
            }
        }

        return wrong;
    }
}
