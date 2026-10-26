using Microsoft.Extensions.AI;
using Pennyworth.Assistant;
using Pennyworth.Assistant.Local;
using Pennyworth.Assistant.Providers;

namespace Pennyworth.Platform;

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
    Func<string, long> FreeSpace);
