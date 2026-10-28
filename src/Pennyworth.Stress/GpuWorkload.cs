using Pennyworth.Stress.Native;

namespace Pennyworth.Stress;

/// <summary>What the graphics card test needs from the card: run one unit and read back its answers.</summary>
internal interface IGpuCompute : IDisposable
{
    /// <summary>The card's name, as the driver reports it.</summary>
    string Name { get; }

    /// <summary>Answers per unit: one per shader thread.</summary>
    int Width { get; }

    /// <summary>Floating-point operations in one unit.</summary>
    long OperationsPerUnit { get; }

    /// <summary>Runs one unit and copies its answers into <paramref name="results"/>; blocks until the card finishes.</summary>
    /// <exception cref="GpuLostException">The driver reset the card or the card stopped answering.</exception>
    void Run(Span<uint> results);
}

/// <summary>The card stopped answering: Windows reset its driver (a TDR) or the card was removed.</summary>
internal sealed class GpuLostException(int hresult) : Exception($"The graphics card was lost (0x{hresult:X8}).");

/// <summary>
/// Loads the graphics card with a compute shader whose answers are known, like OCCT's 3D test.
/// Every unit runs the same shader on the same inputs, so an unstable card (too hot,
/// overclocked, failing memory) shows up as an answer that differs from the first one. A driver
/// reset under load counts as an error too: it is how Windows recovers from a card that hung.
/// </summary>
public sealed class GpuWorkload : Workload
{
    /// <summary>
    /// How long one unit keeps the card busy. Short enough that Windows never mistakes it for a
    /// hang (it resets cards stuck for 2 s) and that the desktop still gets frames in between.
    /// </summary>
    private static readonly TimeSpan UnitTime = TimeSpan.FromMilliseconds(30);

    private readonly Func<IGpuCompute?> _create;
    private IGpuCompute? _gpu;
    private string? _device;

    public GpuWorkload()
        : this(() => OperatingSystem.IsWindows() ? D3D11Compute.TryCreate(UnitTime) : null)
    {
    }

    /// <param name="create">Opens the card; null when there is none that can run the test.</param>
    internal GpuWorkload(Func<IGpuCompute?> create)
    {
        _create = create;
    }

    public override StressKind Kind => StressKind.Gpu;

    /// <summary>One thread feeds the card; the work itself runs on the card.</summary>
    public override int Threads => 1;

    public override string? Device => Volatile.Read(ref _device);

    /// <summary>Lets tests simulate a card that gets an answer wrong.</summary>
    internal Action<uint[]>? Tamper { get; set; }

    protected override bool Prepare()
    {
        _gpu = _create();
        if (_gpu is null)
        {
            Fail(WorkloadFailure.NoGpu, null);
            return false;
        }

        Volatile.Write(ref _device, _gpu.Name);
        return true;
    }

    protected override void Work(int index, CancellationToken token)
    {
        IGpuCompute gpu = _gpu!;
        var results = new uint[gpu.Width];
        uint[]? reference = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                gpu.Run(results);
            }
            catch (GpuLostException)
            {
                Error();
                return;
            }

            Tamper?.Invoke(results);

            // The first answers are the reference; later ones must match them bit for bit.
            if (reference is null)
            {
                reference = (uint[])results.Clone();
            }
            else if (!results.AsSpan().SequenceEqual(reference))
            {
                Error(Mismatches(reference, results));
            }

            Count(gpu.OperationsPerUnit);
        }
    }

    protected override void Release()
    {
        _gpu?.Dispose();
        _gpu = null;
    }

    private static int Mismatches(uint[] expected, uint[] actual)
    {
        int wrong = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                wrong++;
            }
        }

        return wrong;
    }
}
