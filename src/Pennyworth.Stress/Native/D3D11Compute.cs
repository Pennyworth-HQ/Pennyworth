using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Pennyworth.Stress.Native;

/// <summary>
/// A Direct3D 11 compute shader on the strongest graphics card, called through the COM vtables
/// directly so the app needs no graphics package. The shader runs eight fused multiply-add
/// chains and an integer hash per thread and writes one answer per thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class D3D11Compute : IGpuCompute
{
    private const int GroupSize = 64;
    private const int Groups = 1024;
    private const int Chains = 8;
    private const int CalibrationIterations = 64;
    private const int MaxIterations = 1 << 20;

    // https://learn.microsoft.com/windows/win32/direct3ddxgi/dxgi-error
    private const int DeviceRemoved = unchecked((int)0x887A0005);
    private const int DeviceHung = unchecked((int)0x887A0006);
    private const int DeviceReset = unchecked((int)0x887A0007);
    private const int DriverInternalError = unchecked((int)0x887A0020);

    private const string Shader = """
        RWStructuredBuffer<uint> results : register(u0);

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            const float m = 0.9999999f;
            const float c = 1e-7f;
            float a = 1.0f + id.x * 1e-7f;
            float b = a + 0.1f, d = a + 0.2f, e = a + 0.3f;
            float f = a + 0.4f, g = a + 0.5f, h = a + 0.6f, k = a + 0.7f;
            uint n = id.x;
            [loop]
            for (uint i = 0; i < ITERATIONS; i++)
            {
                a = mad(a, m, c); b = mad(b, m, c); d = mad(d, m, c); e = mad(e, m, c);
                f = mad(f, m, c); g = mad(g, m, c); h = mad(h, m, c); k = mad(k, m, c);
                n = (n * 0x9E3779B1u + 1u) ^ (n >> 15);
            }
            results[id.x] = asuint(a) ^ asuint(b) ^ asuint(d) ^ asuint(e)
                ^ asuint(f) ^ asuint(g) ^ asuint(h) ^ asuint(k) ^ n;
        }
        """;

    private nint _device;
    private nint _context;
    private nint _buffer;
    private nint _staging;
    private nint _view;
    private nint _shader;

    private D3D11Compute(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public int Width => GroupSize * Groups;

    public long OperationsPerUnit { get; private set; }

    /// <summary>
    /// Opens the card with the most video memory and sizes a unit to about <paramref name="unitTime"/>.
    /// Null when there is no card with Direct3D 11 compute shaders.
    /// </summary>
    public static D3D11Compute? TryCreate(TimeSpan unitTime)
    {
        D3D11Compute? gpu = null;
        try
        {
            gpu = Open();
            if (gpu is null)
            {
                return null;
            }

            gpu.Calibrate(unitTime);
            return gpu;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or GpuLostException)
        {
            Debug.WriteLine(ex);
            gpu?.Dispose();
            return null;
        }
    }

    public void Run(Span<uint> results)
    {
        nint context = _context;
        nint view = _view;
        ((delegate* unmanaged<nint, nint, nint*, uint, void>)Slot(context, 69))(context, _shader, null, 0); // CSSetShader
        ((delegate* unmanaged<nint, uint, uint, nint*, uint*, void>)Slot(context, 68))(context, 0, 1, &view, null); // CSSetUnorderedAccessViews
        ((delegate* unmanaged<nint, uint, uint, uint, void>)Slot(context, 41))(context, Groups, 1, 1); // Dispatch
        ((delegate* unmanaged<nint, nint, nint, void>)Slot(context, 47))(context, _staging, _buffer); // CopyResource

        // Map waits for the card to finish the unit and the copy.
        MappedSubresource mapped;
        int hr = ((delegate* unmanaged<nint, nint, uint, int, uint, MappedSubresource*, int>)Slot(context, 14))(context, _staging, 0, MapRead, 0, &mapped);
        if (hr is DeviceRemoved or DeviceHung or DeviceReset or DriverInternalError)
        {
            throw new GpuLostException(hr);
        }

        Check(hr);
        try
        {
            new ReadOnlySpan<uint>(mapped.Data, Width).CopyTo(results);
        }
        finally
        {
            ((delegate* unmanaged<nint, nint, uint, void>)Slot(context, 15))(context, _staging, 0); // Unmap
        }
    }

    public void Dispose()
    {
        ReleaseCom(ref _shader);
        ReleaseCom(ref _view);
        ReleaseCom(ref _staging);
        ReleaseCom(ref _buffer);
        ReleaseCom(ref _context);
        ReleaseCom(ref _device);
    }

    private static D3D11Compute? Open()
    {
        nint adapter = StrongestAdapter(out string name);
        nint device = 0;
        nint context = 0;
        int level = FeatureLevel11;
        int created;
        try
        {
            // An adapter needs the "unknown" driver type; without one, the default hardware card.
            created = D3D11CreateDevice(adapter, adapter != 0 ? DriverTypeUnknown : DriverTypeHardware, 0, 0, &level, 1, SdkVersion, &device, null, &context);
        }
        finally
        {
            ReleaseCom(ref adapter);
        }

        if (created < 0)
        {
            return null;
        }

        var gpu = new D3D11Compute(name.Length > 0 ? name : "Direct3D 11") { _device = device, _context = context };
        try
        {
            gpu.CreateBuffers();
            return gpu;
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    /// <summary>The hardware adapter with the most dedicated video memory: the discrete card on a laptop that has two.</summary>
    private static nint StrongestAdapter(out string name)
    {
        name = "";
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        nint factory;
        if (CreateDXGIFactory1(&iid, &factory) < 0)
        {
            return 0;
        }

        nint best = 0;
        nuint bestMemory = 0;
        try
        {
            for (uint i = 0; ; i++)
            {
                nint adapter;
                if (((delegate* unmanaged<nint, uint, nint*, int>)Slot(factory, 12))(factory, i, &adapter) < 0) // EnumAdapters1
                {
                    break;
                }

                AdapterDesc1 desc;
                bool usable = ((delegate* unmanaged<nint, AdapterDesc1*, int>)Slot(adapter, 10))(adapter, &desc) >= 0 // GetDesc1
                    && (desc.Flags & AdapterFlagSoftware) == 0;
                if (usable && (best == 0 || desc.DedicatedVideoMemory > bestMemory))
                {
                    ReleaseCom(ref best);
                    best = adapter;
                    bestMemory = desc.DedicatedVideoMemory;
                    name = new string(desc.Description).Trim();
                }
                else
                {
                    ReleaseCom(ref adapter);
                }
            }
        }
        finally
        {
            ReleaseCom(ref factory);
        }

        return best;
    }

    private void CreateBuffers()
    {
        var desc = new BufferDesc
        {
            ByteWidth = (uint)(Width * sizeof(uint)),
            BindFlags = BindUnorderedAccess,
            MiscFlags = ResourceMiscBufferStructured,
            StructureByteStride = sizeof(uint),
        };
        nint buffer;
        Check(((delegate* unmanaged<nint, BufferDesc*, void*, nint*, int>)Slot(_device, 3))(_device, &desc, null, &buffer)); // CreateBuffer
        _buffer = buffer;

        var staging = new BufferDesc
        {
            ByteWidth = desc.ByteWidth,
            Usage = UsageStaging,
            CpuAccessFlags = CpuAccessRead,
        };
        Check(((delegate* unmanaged<nint, BufferDesc*, void*, nint*, int>)Slot(_device, 3))(_device, &staging, null, &buffer));
        _staging = buffer;

        var viewDesc = new UavDesc { ViewDimension = UavDimensionBuffer, NumElements = (uint)Width };
        nint view;
        Check(((delegate* unmanaged<nint, nint, UavDesc*, nint*, int>)Slot(_device, 8))(_device, _buffer, &viewDesc, &view)); // CreateUnorderedAccessView
        _view = view;
    }

    /// <summary>
    /// Times a short shader and grows the loop until a unit takes about <paramref name="unitTime"/>.
    /// Launch overhead makes short runs look slow, so each step can only undershoot, never
    /// overshoot into a run long enough for Windows to reset the card.
    /// </summary>
    private void Calibrate(TimeSpan unitTime)
    {
        var results = new uint[Width];
        int iterations = CalibrationIterations;
        for (int round = 0; ; round++)
        {
            Compile(iterations);
            Run(results); // The first run includes the driver's own compilation.
            long start = Stopwatch.GetTimestamp();
            Run(results);
            TimeSpan took = Stopwatch.GetElapsedTime(start);

            double scale = unitTime / took;
            if (round == 3 || scale < 2 || iterations >= MaxIterations)
            {
                break;
            }

            iterations = (int)Math.Clamp(iterations * Math.Min(scale, 64), CalibrationIterations, MaxIterations);
        }

        OperationsPerUnit = (long)Width * iterations * Chains * 2;
    }

    private void Compile(int iterations)
    {
        byte[] source = Encoding.ASCII.GetBytes($"#define ITERATIONS {iterations.ToString(CultureInfo.InvariantCulture)}u\n{Shader}");
        nint code = 0;
        nint errors = 0;
        int hr;
        fixed (byte* text = source)
        fixed (byte* entry = "main"u8)
        fixed (byte* target = "cs_5_0"u8)
        {
            hr = D3DCompile(text, (nuint)source.Length, null, null, null, entry, target, CompileOptimizationLevel3, 0, &code, &errors);
        }

        if (errors != 0)
        {
            Debug.WriteLine(Marshal.PtrToStringAnsi((nint)((delegate* unmanaged<nint, void*>)Slot(errors, 3))(errors)));
            ReleaseCom(ref errors);
        }

        Check(hr);
        try
        {
            void* bytes = ((delegate* unmanaged<nint, void*>)Slot(code, 3))(code); // GetBufferPointer
            nuint size = ((delegate* unmanaged<nint, nuint>)Slot(code, 4))(code); // GetBufferSize
            nint shader;
            Check(((delegate* unmanaged<nint, void*, nuint, nint, nint*, int>)Slot(_device, 18))(_device, bytes, size, 0, &shader)); // CreateComputeShader
            ReleaseCom(ref _shader);
            _shader = shader;
        }
        finally
        {
            ReleaseCom(ref code);
        }
    }

    private static void* Slot(nint com, int index) => (*(void***)com)[index];

    private static void ReleaseCom(ref nint com)
    {
        if (com != 0)
        {
            ((delegate* unmanaged<nint, uint>)Slot(com, 2))(com);
            com = 0;
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            throw new COMException(null, hr);
        }
    }

    private const int DriverTypeUnknown = 0;
    private const int DriverTypeHardware = 1;
    private const int FeatureLevel11 = 0xb000;
    private const uint SdkVersion = 7;
    private const uint AdapterFlagSoftware = 2;
    private const uint UsageStaging = 3;
    private const uint BindUnorderedAccess = 0x80;
    private const uint CpuAccessRead = 0x20000;
    private const uint ResourceMiscBufferStructured = 0x40;
    private const uint UavDimensionBuffer = 1;
    private const int MapRead = 1;
    private const uint CompileOptimizationLevel3 = 1 << 15;

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, int* featureLevels, uint featureLevelCount,
        uint sdkVersion, nint* device, int* featureLevel, nint* context);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* iid, nint* factory);

    [LibraryImport("d3dcompiler_47.dll")]
    private static partial int D3DCompile(byte* source, nuint sourceSize, byte* sourceName, void* defines, void* include, byte* entryPoint,
        byte* target, uint flags1, uint flags2, nint* code, nint* errors);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    /// <summary>D3D11_UNORDERED_ACCESS_VIEW_DESC with the buffer member of its union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UavDesc
    {
        public uint Format;
        public uint ViewDimension;
        public uint FirstElement;
        public uint NumElements;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
