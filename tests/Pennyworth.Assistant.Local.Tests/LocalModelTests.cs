using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Assistant.Local.Tests;

public sealed class LocalModelTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private readonly string _dir = Directory.CreateTempSubdirectory("pw-models-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Catalogue_pins_every_file()
    {
        var catalog = ModelCatalog.Load();

        Assert.Contains(catalog.Models, m => m.Device == ModelDevice.Gpu);
        Assert.Contains(catalog.Models, m => m.Device == ModelDevice.Cpu);
        Assert.All(catalog.Models, m =>
        {
            Assert.Equal(40, m.Revision.Length);
            Assert.Equal("MIT", m.Licence);
            Assert.Contains(m.Files, f => f.Path == "genai_config.json");
            Assert.All(m.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
            Assert.DoesNotContain(m.Files, f => f.Path.EndsWith(".py", StringComparison.Ordinal));
        });
        Assert.Equal(
            "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/fc04c8f93df696602fd9f300a30d1bf2e3081347/gpu/gpu-int4-rtn-block-32/genai_config.json",
            catalog.Get("phi-4-mini-gpu").UrlOf(catalog.Get("phi-4-mini-gpu").Files.Single(f => f.Path == "genai_config.json"), ModelDownloader.HuggingFace).ToString());
    }

    [Theory]
    [InlineData(8L, "phi-4-mini-gpu")]
    [InlineData(6L, "phi-4-mini-gpu")]
    [InlineData(4L, "phi-3.5-mini-cpu")]
    [InlineData(0L, "phi-3.5-mini-cpu")]
    public void Recommends_the_graphics_card_model_only_with_enough_video_memory(long videoGb, string expected)
    {
        Assert.Equal(expected, HardwareCheck.Recommend(ModelCatalog.Load(), Specs(16, videoGb)).Id);
    }

    [Fact]
    public void Says_how_the_model_will_run_without_blocking()
    {
        var catalog = ModelCatalog.Load();
        var gpu = catalog.Get("phi-4-mini-gpu");
        var cpu = catalog.Get("phi-3.5-mini-cpu");

        Assert.Equal(HardwareFit.Good, HardwareCheck.Evaluate(gpu, Specs(16, 8), 10 * Gb, 100 * Gb, En).Fit);
        Assert.Equal(HardwareFit.Slow, HardwareCheck.Evaluate(cpu, Specs(16, 0), 10 * Gb, 100 * Gb, En).Fit);
        Assert.Equal(HardwareFit.Slow, HardwareCheck.Evaluate(gpu, Specs(16, 8), 2 * Gb, 100 * Gb, En).Fit);
        Assert.Equal(HardwareFit.NotEnoughMemory, HardwareCheck.Evaluate(cpu, Specs(4, 0), 2 * Gb, 100 * Gb, En).Fit);

        var full = HardwareCheck.Evaluate(gpu, Specs(16, 8), 10 * Gb, 3 * Gb, Pt);
        Assert.Equal(HardwareFit.NotEnoughSpace, full.Fit);
        Assert.StartsWith("O download precisa de", full.Reasons[0]);
    }

    [Fact]
    public async Task Downloads_verifies_and_marks_the_model_installed()
    {
        var (model, files) = Fake(("genai_config.json", 100), ("model.onnx.data", 300_000));
        var server = new FileServer(files);
        var store = new LocalModelStore(_dir);
        var reports = new List<DownloadProgress>();

        await new ModelDownloader(server.Client()).DownloadAsync(model, store.FolderOf(model), new SyncProgress(reports), TestContext.Current.CancellationToken);

        Assert.True(store.IsInstalled(model));
        Assert.Equal(files["model.onnx.data"], File.ReadAllBytes(Path.Combine(store.FolderOf(model), "model.onnx.data")));
        Assert.Equal(1, reports[^1].Fraction);
        Assert.Empty(Directory.EnumerateFiles(store.FolderOf(model), "*.partial"));
    }

    [Fact]
    public async Task Resumes_a_partial_file_with_a_range_request()
    {
        var (model, files) = Fake(("model.onnx.data", 200_000));
        var server = new FileServer(files);
        var store = new LocalModelStore(_dir);
        string folder = store.FolderOf(model);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx.data.partial"), files["model.onnx.data"][..50_000]);

        await new ModelDownloader(server.Client()).DownloadAsync(model, folder, null, TestContext.Current.CancellationToken);

        Assert.Equal(50_000, Assert.Single(server.Ranges));
        Assert.True(store.IsInstalled(model));
    }

    [Fact]
    public async Task Deletes_a_file_that_does_not_match_its_checksum()
    {
        var (model, files) = Fake(("model.onnx.data", 1_000));
        files["model.onnx.data"][10] ^= 0xFF; // tampered on the server
        var store = new LocalModelStore(_dir);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModelDownloader(new FileServer(files).Client()).DownloadAsync(model, store.FolderOf(model), null, TestContext.Current.CancellationToken));

        Assert.False(store.IsInstalled(model));
        Assert.Empty(Directory.EnumerateFiles(store.FolderOf(model)));
    }

    [Fact]
    public async Task Skips_files_already_verified_and_removes_everything()
    {
        var (model, files) = Fake(("a.json", 10), ("b.data", 5_000));
        var server = new FileServer(files);
        var store = new LocalModelStore(_dir);
        await new ModelDownloader(server.Client()).DownloadAsync(model, store.FolderOf(model), null, TestContext.Current.CancellationToken);

        await new ModelDownloader(server.Client()).DownloadAsync(model, store.FolderOf(model), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, server.Requests); // nothing fetched the second time
        Assert.True(store.SizeOnDisk(model) >= 5_010);
        store.Remove(model);
        Assert.False(store.IsInstalled(model));
        Assert.Equal(0, store.SizeOnDisk(model));
    }

    /// <summary>Runs only with PENNYWORTH_TEST_MODEL=&lt;folder of phi-3.5-mini-cpu or phi-4-mini-gpu&gt;.</summary>
    [Fact]
    public async Task Answers_with_a_real_model_and_unloads()
    {
        string? folder = Environment.GetEnvironmentVariable("PENNYWORTH_TEST_MODEL");
        if (folder is null)
        {
            Assert.Skip("Set PENNYWORTH_TEST_MODEL to a downloaded model folder to run this test.");
        }

        var catalog = ModelCatalog.Load();
        var model = catalog.Get(Environment.GetEnvironmentVariable("PENNYWORTH_TEST_MODEL_ID") ?? "phi-3.5-mini-cpu");
        using var client = new LocalChatClient(folder, model, TimeSpan.FromMilliseconds(300));

        string text = "";
        long? output = null;
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Say hello in three words.")], new ChatOptions { MaxOutputTokens = 20 }, TestContext.Current.CancellationToken))
        {
            text += update.Text;
            output = update.Contents.OfType<UsageContent>().FirstOrDefault()?.Details.OutputTokenCount ?? output;
        }

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("<|end|>", text);
        Assert.InRange(output ?? 0, 1, 20);
        Assert.True(client.IsLoaded);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.False(client.IsLoaded); // unloaded after the idle time

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Write a long story.")], new ChatOptions { MaxOutputTokens = 400 }, cts.Token))
            {
                if (++seen == 5)
                {
                    cts.Cancel();
                }
            }
        });
        Assert.InRange(seen, 5, 8);
    }

    private static PcSpecs Specs(long ramGb, long videoGb) => new(
        new SystemSpecs("PC", "ana", null, null),
        new OsSpecs("Windows 11", null, "26200", "X64", null, TimeSpan.Zero),
        new CpuSpecs("CPU", null, 6, 12, 1, null, 0, 0, 0, "X64", null),
        new MemorySpecs(ramGb * Gb, 2, []),
        videoGb == 0 ? [new GpuSpecs("Microsoft Basic Display Adapter", null, null, null)] : [new GpuSpecs("NVIDIA", videoGb * Gb, null, null)],
        [], [], new BoardSpecs(null, null, null, null, null), [], null);

    private static (LocalModel Model, Dictionary<string, byte[]> Files) Fake(params (string Name, int Size)[] spec)
    {
        var files = spec.ToDictionary(s => s.Name, s => RandomNumberGenerator.GetBytes(s.Size));
        var model = new LocalModel("fake", "Fake", "org/fake", new string('a', 40), "gpu", "MIT", ModelDevice.Cpu, 8, 0, [],
            new LocalizedText("", ""),
            [.. files.Select(f => new ModelFile(f.Key, f.Value.Length, Convert.ToHexStringLower(SHA256.HashData(f.Value))))]);
        // The server serves the original bytes; the catalogue was made before any tampering.
        return (model, files);
    }

    private sealed class FileServer(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public List<long> Ranges { get; } = [];

        public HttpClient Client() => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            byte[] data = files[request.RequestUri!.Segments[^1]];
            if (request.Headers.Range?.Ranges.Single().From is long from)
            {
                Ranges.Add(from);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from..]) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    private sealed class SyncProgress(List<DownloadProgress> values) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => values.Add(value);
    }
}
