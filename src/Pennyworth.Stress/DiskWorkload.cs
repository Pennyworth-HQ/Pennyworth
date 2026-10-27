using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pennyworth.Stress;

/// <summary>
/// Writes a temporary file in large chunks, reads it back and checks every chunk, over and over
/// (HeavyLoad's "write test file", with OCCT-style verification). On Windows the file bypasses
/// the cache so the drive itself is measured. The file is deleted when the test stops, even if
/// the app is killed (delete-on-close).
/// </summary>
public sealed unsafe class DiskWorkload : Workload
{
    /// <summary>Kept free on the drive, so the test never fills it.</summary>
    public const long Reserve = 2L * 1024 * 1024 * 1024;

    /// <summary>Skip the Windows file cache (FILE_FLAG_NO_BUFFERING).</summary>
    private const FileOptions NoBuffering = (FileOptions)0x2000_0000;

    private const ulong Golden = 0x9E37_79B9_7F4A_7C15;

    private readonly string _folder;
    private readonly long _bytes;
    private readonly int _chunkBytes;
    private SafeFileHandle? _file;

    public DiskWorkload(string folder, long bytes, int chunkBytes = 4 * 1024 * 1024)
    {
        _folder = folder;
        _chunkBytes = chunkBytes;
        _bytes = Math.Max(1, bytes / chunkBytes) * chunkBytes;
    }

    public override StressKind Kind => StressKind.Disk;

    public override int Threads => 1;

    public override long Bytes => _bytes;

    /// <summary>The temporary file, while the test runs.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Lets tests corrupt a chunk as it is read back, as a failing drive would.</summary>
    internal ChunkTamper? Tamper { get; set; }

    protected override bool Prepare()
    {
        Directory.CreateDirectory(_folder);
        long free = new DriveInfo(Path.GetFullPath(_folder)).AvailableFreeSpace;
        if (free - _bytes < Reserve)
        {
            Fail(WorkloadFailure.NotEnoughDiskSpace, null);
            return false;
        }

        FilePath = Path.Combine(_folder, $"pennyworth-stress-{Guid.NewGuid():N}.tmp");
        const FileOptions options = FileOptions.WriteThrough | FileOptions.DeleteOnClose;
        try
        {
            _file = File.OpenHandle(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                OperatingSystem.IsWindows() ? options | NoBuffering : options, _bytes);
        }
        catch (ArgumentException)
        {
            // A runtime that refuses the unbuffered flag: measure through the cache instead.
            _file = File.OpenHandle(FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, options, _bytes);
        }

        return true;
    }

    protected override void Work(int index, CancellationToken token)
    {
        SafeFileHandle file = _file!;
        // Unbuffered I/O needs sector-aligned memory; 4 KB covers every drive.
        byte* written = (byte*)NativeMemory.AlignedAlloc((nuint)_chunkBytes, 4096);
        byte* read = (byte*)NativeMemory.AlignedAlloc((nuint)_chunkBytes, 4096);
        try
        {
            var expected = new Span<byte>(written, _chunkBytes);
            var actual = new Span<byte>(read, _chunkBytes);
            long chunks = _bytes / _chunkBytes;
            for (ulong pass = 0; !token.IsCancellationRequested; pass++)
            {
                Phase = WorkPhase.Writing;
                for (long c = 0; c < chunks && !token.IsCancellationRequested; c++)
                {
                    Fill(expected, pass, c);
                    RandomAccess.Write(file, expected, c * _chunkBytes);
                    Count(_chunkBytes);
                }

                Phase = WorkPhase.Reading;
                for (long c = 0; c < chunks && !token.IsCancellationRequested; c++)
                {
                    int got = RandomAccess.Read(file, actual, c * _chunkBytes);
                    Tamper?.Invoke(actual);
                    Fill(expected, pass, c);
                    if (got != _chunkBytes || !actual.SequenceEqual(expected))
                    {
                        Error();
                    }

                    Count(_chunkBytes);
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(written);
            NativeMemory.AlignedFree(read);
        }
    }

    protected override void Release()
    {
        _file?.Dispose();
        _file = null;
        if (FilePath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>A pattern unique to each pass and chunk, so stale data never passes the check.</summary>
    internal static void Fill(Span<byte> chunk, ulong pass, long index)
    {
        Span<ulong> words = MemoryMarshal.Cast<byte, ulong>(chunk);
        ulong seed = (pass * Golden) ^ ((ulong)index << 32) ^ ((pass & 1) == 0 ? 0 : ulong.MaxValue);
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = ((ulong)i * Golden) ^ seed;
        }
    }
}

internal delegate void ChunkTamper(Span<byte> chunk);
