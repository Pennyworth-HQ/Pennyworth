using System.Net;
using System.Text;
using System.Text.Json;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;
using Pennyworth.Cleanup;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.App.Tests;

/// <summary>
/// Phase 6 privacy review, automated: a whole cloud conversation through the real view model,
/// session, anonymiser and OpenAI client, with the HTTP request captured before it would leave.
/// </summary>
// The shell sets the process-wide language from settings, so these run apart from tests that switch it.
[Collection(nameof(LanguageSwitching))]
public sealed class AssistantPrivacyTests : IDisposable
{
    private const string Key = "sk-live-very-secret-123";
    private readonly string _dir = Directory.CreateTempSubdirectory("pw-privacy-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task What_leaves_the_PC_is_what_the_preview_showed_and_nothing_personal()
    {
        var wire = new CapturingHandler();
        var keys = new MemoryKeys();
        var services = new AssistantServices(
            keys,
            new ConversationStore(Path.Combine(_dir, "conversations")),
            new UsageMeter(Path.Combine(_dir, "usage.json")),
            ModelCatalog.Load(),
            new LocalModelStore(Path.Combine(_dir, "models")),
            (profile, key) => ChatClientFactory.Create(profile, key!, new HttpClient(wire)),
            (_, _, _, _) => Task.CompletedTask,
            new Anonymizer("ana.souza", @"C:\Users\Ana Souza", "DESKTOP-ANA"),
            () => 8L << 30,
            _ => 100L << 30);
        string settingsPath = Path.Combine(_dir, "settings.json");
        var shell = new MainWindowViewModel(TestApp.Services(cleanup: new BinWithPersonalFiles(), settings: new FileSettingsStore(settingsPath))
            with { Specs = new AnaPc(), Assistant = services });
        shell.Initialize();
        await shell.Cleanup.CurrentMeasure;
        var ask = shell.Ask!;

        ask.ApiKey = Key;
        ask.SaveCloudProfileCommand.Execute(null);
        ask.AskAbout(new AssistantTopic(TopicKind.CleanupItem, "recycle-bin"), @"Can I empty the bin? I'm ana.souza on DESKTOP-ANA, mail ana@example.com, files in C:\Users\Ana Souza\Documents\Contratos 2026.");
        await ask.SendCommand.ExecuteAsync(null);
        var preview = Assert.IsType<SendPreviewDialogViewModel>(shell.Dialog);
        Assert.Empty(wire.Bodies); // nothing sent while the preview is open

        preview.SendCommand.Execute(null);
        await ask.CurrentRun;

        string body = Assert.Single(wire.Bodies);
        using var json = JsonDocument.Parse(body);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(preview.Instructions, messages[0].GetProperty("content").GetString());
        Assert.Equal(preview.Question, messages[messages.GetArrayLength() - 1].GetProperty("content").GetString());

        foreach (string personal in new[] { "ana.souza", "Ana Souza", "DESKTOP-ANA", "ana@example.com", "Contratos", "contrato.pdf", "backup.zip", "Fotos da praia", "PICHAU-ANA" })
        {
            Assert.DoesNotContain(personal, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("<profile>", body);
        Assert.Equal("Bearer " + Key, wire.Authorization);

        // The answer shows the real names again, and the key is nowhere on disk.
        Assert.Contains(@"C:\Users\Ana Souza\Documents", ask.Messages[^1].Text);
        foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Key, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Authorization = request.Headers.Authorization?.ToString();
            const string answer = "Your bin holds old files from <profile>\\\\Documents. Have a look first.";
            string sse =
                "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"" + answer + "\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":20,\"total_tokens\":920}}\n\n" +
                "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private sealed class MemoryKeys : ICredentialStore
    {
        private readonly Dictionary<string, string> _keys = [];

        public void Save(string profileId, string secret) => _keys[profileId] = secret;

        public string? Read(string profileId) => _keys.GetValueOrDefault(profileId);

        public void Delete(string profileId) => _keys.Remove(profileId);
    }

    /// <summary>Specs naming the PC and the user, as Windows reports them.</summary>
    private sealed class AnaPc : ISpecsReader
    {
        public PcSpecs Read() => SpecsViewModelTests.SamplePc() with
        {
            System = new SystemSpecs("DESKTOP-ANA", "ana.souza", "PICHAU-ANA", "Model"),
        };
    }

    /// <summary>A Recycle Bin whose items come from the user's personal folders.</summary>
    private sealed class BinWithPersonalFiles : ICleanupService
    {
        private static readonly CleanupRule Bin = FakeCleanup.Rule("recycle-bin", SafetyLevel.UsuallySafe, kind: CleanupKind.RecycleBin);

        public IReadOnlyList<CleanupRule> Rules { get; } = [Bin];

        public Task<CleanupEstimate> MeasureAsync(CleanupRule rule, CancellationToken cancellationToken) =>
            Task.FromResult(new CleanupEstimate(rule, 3L << 30, 2, true, [],
            [
                new CleanupFile(@"C:\$Recycle.Bin\S-1\$Ra.zip", 2L << 30, DateTime.UtcNow, @"C:\Users\Ana Souza\Pictures\Fotos da praia\backup.zip"),
                new CleanupFile(@"C:\$Recycle.Bin\S-1\$Rb.pdf", 1L << 30, DateTime.UtcNow, @"C:\Users\Ana Souza\Documents\Contratos 2026\contrato.pdf"),
            ]));

        public Task<CleanupResult> CleanAsync(CleanupEstimate estimate, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The privacy review never cleans.");
    }
}
