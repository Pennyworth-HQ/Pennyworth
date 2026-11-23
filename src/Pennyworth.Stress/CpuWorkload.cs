using System.Numerics;

namespace Pennyworth.Stress;

/// <summary>
/// Loads every core with arithmetic whose answer is known. Each unit is computed from the same
/// inputs every time, so an unstable processor (too hot, overclocked, undervolted) shows up as a
/// result that differs from the first one: the check Prime95 and OCCT rely on.
/// </summary>
public sealed class CpuWorkload : Workload
{
    /// <summary>Independent chains, enough to keep the vector units' pipelines full.</summary>
    private const int Chains = 8;

    private readonly int _threads;
    private readonly int _floatIterations;
    private readonly int _integerIterations;

    /// <param name="threads">0 means every logical processor.</param>
    /// <param name="unitScale">Shrinks each unit; tests use small units.</param>
    public CpuWorkload(CpuMode mode, int threads = 0, double unitScale = 1)
    {
        Mode = mode;
        _threads = threads > 0 ? threads : Environment.ProcessorCount;
        _floatIterations = Math.Max(16, (int)((1 << 18) * unitScale));
        _integerIterations = Math.Max(16, (int)((1 << 19) * unitScale));
    }

    public CpuMode Mode { get; }

    public override StressKind Kind => StressKind.Cpu;

    public override int Threads => _threads;

    /// <summary>Lets tests simulate a processor that gets one result wrong.</summary>
    internal Func<int, ulong, ulong>? Tamper { get; set; }

    /// <summary>Floating-point operations in one unit: a multiply and an add per lane.</summary>
    internal long FloatOperations => (long)_floatIterations * Chains * Vector<double>.Count * 2;

    /// <summary>Integer operations in one unit: multiply, add, two xors and a shift per chain.</summary>
    internal long IntegerOperations => (long)_integerIterations * Chains * 5;

    protected override void Work(int index, CancellationToken token)
    {
        // The first result of each kind is the reference; later ones must match it bit for bit.
        ulong? floatReference = null;
        ulong? integerReference = null;
        long unit = 0;
        while (!token.IsCancellationRequested)
        {
            bool useFloat = Mode switch
            {
                CpuMode.FloatingPoint => true,
                CpuMode.Integer => false,
                _ => (unit & 1) == 0,
            };

            ulong result = useFloat ? FloatUnit(_floatIterations) : IntegerUnit(_integerIterations);
            if (Tamper is { } tamper)
            {
                result = tamper(index, result);
            }

            ref ulong? reference = ref useFloat ? ref floatReference : ref integerReference;
            if (reference is null)
            {
                reference = result;
            }
            else if (reference != result)
            {
                Error();
            }

            Count(useFloat ? FloatOperations : IntegerOperations);
            unit++;
        }
    }

    /// <summary>
    /// <c>x = x * m + c</c> on eight vector registers, with fused multiply-add (one rounding,
    /// so the result is exact and repeatable). m is just under 1, so values stay bounded.
    /// </summary>
    internal static ulong FloatUnit(int iterations)
    {
        var m = new Vector<double>(0.999_999_9);
        var c = new Vector<double>(1e-7);
        Vector<double> a = new(1.0), b = new(1.1), d = new(1.2), e = new(1.3);
        Vector<double> f = new(1.4), g = new(1.5), h = new(1.6), k = new(1.7);
        for (int i = 0; i < iterations; i++)
        {
            a = Vector.FusedMultiplyAdd(a, m, c);
            b = Vector.FusedMultiplyAdd(b, m, c);
            d = Vector.FusedMultiplyAdd(d, m, c);
            e = Vector.FusedMultiplyAdd(e, m, c);
            f = Vector.FusedMultiplyAdd(f, m, c);
            g = Vector.FusedMultiplyAdd(g, m, c);
            h = Vector.FusedMultiplyAdd(h, m, c);
            k = Vector.FusedMultiplyAdd(k, m, c);
        }

        Vector<ulong> bits = Vector.AsVectorUInt64(a) ^ Vector.AsVectorUInt64(b) ^ Vector.AsVectorUInt64(d) ^ Vector.AsVectorUInt64(e)
            ^ Vector.AsVectorUInt64(f) ^ Vector.AsVectorUInt64(g) ^ Vector.AsVectorUInt64(h) ^ Vector.AsVectorUInt64(k);
        ulong checksum = 0;
        for (int i = 0; i < Vector<ulong>.Count; i++)
        {
            checksum = (checksum * 31) ^ bits[i];
        }

        return checksum;
    }

    /// <summary>Eight multiply-xorshift chains (a 64-bit mixing function), folded into one checksum.</summary>
    internal static ulong IntegerUnit(int iterations)
    {
        const ulong mul = 0x9E37_79B9_7F4A_7C15;
        ulong a = 1, b = 2, d = 3, e = 4, f = 5, g = 6, h = 7, k = 8;
        for (int i = 0; i < iterations; i++)
        {
            a = ((a * mul) + 1) ^ (a >> 29) ^ 0x5555;
            b = ((b * mul) + 3) ^ (b >> 29) ^ 0x5555;
            d = ((d * mul) + 5) ^ (d >> 29) ^ 0x5555;
            e = ((e * mul) + 7) ^ (e >> 29) ^ 0x5555;
            f = ((f * mul) + 9) ^ (f >> 29) ^ 0x5555;
            g = ((g * mul) + 11) ^ (g >> 29) ^ 0x5555;
            h = ((h * mul) + 13) ^ (h >> 29) ^ 0x5555;
            k = ((k * mul) + 15) ^ (k >> 29) ^ 0x5555;
        }

        return a ^ b ^ d ^ e ^ f ^ g ^ h ^ k;
    }
}
