using System.Runtime.Versioning;
using Pennyworth.Tests;

namespace Pennyworth.Assistant.Providers.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialStoreTests : IDisposable
{
    // A test-only prefix so the real Windows store is used without touching the app's keys.
    private readonly WindowsCredentialStore _store = new($"Pennyworth/Tests/{Guid.NewGuid():N}/");

    public void Dispose() => _store.Delete("profile");

    [WindowsFact]
    public void Saves_reads_replaces_and_deletes_a_key()
    {
        Assert.Null(_store.Read("profile"));

        _store.Save("profile", "sk-ção-🔑");
        Assert.Equal("sk-ção-🔑", _store.Read("profile"));

        _store.Save("profile", "sk-new");
        Assert.Equal("sk-new", _store.Read("profile"));

        _store.Delete("profile");
        Assert.Null(_store.Read("profile"));
        _store.Delete("profile"); // deleting twice is fine
    }
}
