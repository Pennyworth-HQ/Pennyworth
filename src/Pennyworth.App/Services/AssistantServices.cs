using Microsoft.Extensions.AI;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;

namespace Pennyworth.App.Services;

/// <summary>Everything the Ask screen needs; tests pass fakes.</summary>
/// <param name="CreateClient">A chat client for a profile; the key is null for local profiles.</param>
/// <param name="Download">Downloads and verifies a catalogued model into a folder.</param>
/// <param name="AvailableMemory">Physical memory free right now, in bytes.</param>
/// <param name="FreeSpace">Free bytes on the drive holding a folder.</param>
public sealed record AssistantServices(
    ICredentialStore Keys,
    ConversationStore Conversations,
    UsageMeter Usage,
    ModelCatalog Catalog,
    LocalModelStore Models,
    Func<AssistantProfile, string?, IChatClient> CreateClient,
    Func<LocalModel, string, IProgress<DownloadProgress>, CancellationToken, Task> Download,
    Anonymizer Anonymizer,
    Func<long> AvailableMemory,
    Func<string, long> FreeSpace)
{
    /// <summary>The real services: Credential Manager, files under %LocalAppData%\Pennyworth, Hugging Face downloads.</summary>
    public static AssistantServices CreateDefault(Func<long> availableMemory)
    {
        var catalog = ModelCatalog.Load();
        var models = new LocalModelStore(LocalModelStore.DefaultRoot);
        var local = new Dictionary<string, LocalChatClient>();
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var downloader = new ModelDownloader(http);

        IChatClient Create(AssistantProfile profile, string? key)
        {
            if (!profile.IsLocal)
            {
                return ChatClientFactory.Create(profile, key ?? "");
            }

            // One client per model, kept so it can stay loaded between questions and unload when idle.
            if (!local.TryGetValue(profile.ModelId, out LocalChatClient? client))
            {
                LocalModel model = catalog.Get(profile.ModelId);
                client = new LocalChatClient(models.FolderOf(model), model);
                local[profile.ModelId] = client;
            }

            return new NonDisposingClient(client);
        }

        return new AssistantServices(
            new WindowsCredentialStore(),
            new ConversationStore(ConversationStore.DefaultFolder),
            new UsageMeter(UsageMeter.DefaultPath),
            catalog,
            models,
            Create,
            (model, folder, progress, ct) => downloader.DownloadAsync(model, folder, progress, ct),
            Anonymizer.ForCurrentUser(),
            availableMemory,
            folder => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace);
    }

    /// <summary>Lets the screen dispose what it was given without unloading a shared local model.</summary>
    private sealed class NonDisposingClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
