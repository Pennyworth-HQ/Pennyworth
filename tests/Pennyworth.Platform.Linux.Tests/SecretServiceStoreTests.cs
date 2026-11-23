using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public sealed class SecretServiceStoreTests : IDisposable
{
    private readonly string _profile = "test-" + Guid.NewGuid().ToString("N");
    private SecretServiceStore? _real;

    public void Dispose()
    {
        if (_real is not null)
        {
            _real.Delete(_profile);
        }
    }

    [LinuxFact]
    public void Save_without_libsecret_throws_plain_words_and_stores_nothing()
    {
        var backend = new FakeBackend { Fail = new DllNotFoundException("libsecret-1.so.0") };
        var store = new SecretServiceStore(backend);

        var ex = Assert.Throws<KeyringException>(() => store.Save("cloud-1", "sk-super-secret"));

        Assert.Equal(SecretServiceStore.NoKeyringMessage, ex.Message);
        Assert.DoesNotContain("sk-super-secret", ex.ToString());
        Assert.Empty(backend.Items);
    }

    [LinuxFact]
    public void A_library_without_the_needed_functions_is_the_same_as_no_library()
    {
        var store = new SecretServiceStore(new FakeBackend { Fail = new EntryPointNotFoundException("secret_password_storev_sync") });

        var ex = Assert.Throws<KeyringException>(() => store.Save("cloud-1", "k"));

        Assert.Equal(SecretServiceStore.NoKeyringMessage, ex.Message);
    }

    [LinuxFact]
    public void Save_keeps_the_keyring_own_explanation_when_the_service_refuses()
    {
        var store = new SecretServiceStore(new FakeBackend { Fail = new KeyringException("The keyring would not store the key: Cannot create an item in a locked collection") });

        var ex = Assert.Throws<KeyringException>(() => store.Save("cloud-1", "k"));

        Assert.Contains("locked collection", ex.Message);
    }

    [LinuxFact]
    public void Read_and_Delete_without_a_keyring_find_nothing_and_do_not_throw()
    {
        foreach (Exception failure in new Exception[] { new DllNotFoundException(), new EntryPointNotFoundException(), new KeyringException("no service", serviceMissing: true) })
        {
            var store = new SecretServiceStore(new FakeBackend { Fail = failure });

            Assert.Null(store.Read("cloud-1"));
            store.Delete("cloud-1");
        }
    }

    [LinuxFact]
    public void Delete_does_not_hide_a_keyring_that_answered_and_refused()
    {
        var backend = new FakeBackend { Fail = new KeyringException("The keyring would not delete the key: locked collection") };
        var store = new SecretServiceStore(backend);

        var ex = Assert.Throws<KeyringException>(() => store.Delete("cloud-1"));

        Assert.Contains("locked collection", ex.Message);
    }

    [LinuxTheory]
    [InlineData("The name org.freedesktop.secrets was not provided by any .service files", true)]
    [InlineData("GDBus.Error:org.freedesktop.DBus.Error.ServiceUnknown: The name is not activatable", true)]
    [InlineData("Cannot autolaunch D-Bus without X11 $DISPLAY", true)]
    [InlineData("Cannot create an item in a locked collection", false)]
    [InlineData("Object does not exist at path", false)]
    [InlineData("", false)]
    public void Tells_a_missing_service_from_one_that_refused(string detail, bool missing) =>
        Assert.Equal(missing, Libsecret.IsServiceMissing(detail));

    [LinuxFact]
    public void Other_failures_are_not_swallowed()
    {
        var store = new SecretServiceStore(new FakeBackend { Fail = new IOException("disk on fire") });

        Assert.Throws<IOException>(() => store.Save("cloud-1", "k"));
        Assert.Throws<IOException>(() => store.Read("cloud-1"));
        Assert.Throws<IOException>(() => store.Delete("cloud-1"));
    }

    [LinuxFact]
    public void Save_replaces_and_Delete_removes_only_that_profile()
    {
        var backend = new FakeBackend();
        var store = new SecretServiceStore(backend);

        store.Save("a", "one");
        store.Save("b", "other");
        store.Save("a", "two");

        Assert.Equal("two", store.Read("a"));
        store.Delete("a");
        Assert.Null(store.Read("a"));
        Assert.Equal("other", store.Read("b"));
    }

    [LinuxFact]
    public void The_label_tells_a_person_what_the_item_is()
    {
        var backend = new FakeBackend();

        new SecretServiceStore(backend).Save("cloud-1", "k");

        Assert.Equal("Pennyworth assistant key (cloud-1)", backend.Labels["cloud-1"]);
    }

    [LinuxFact]
    public void A_NUL_in_a_key_is_refused_instead_of_cut_off_silently()
    {
        var backend = new FakeBackend();
        var store = new SecretServiceStore(backend);

        var ex = Assert.Throws<ArgumentException>(() => store.Save("cloud-1", "abc\0def"));

        Assert.DoesNotContain("abc", ex.Message);
        Assert.Empty(backend.Items);
        Assert.Null(store.Read("a\0b"));
    }

    [LinuxFact]
    public void An_empty_profile_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SecretServiceStore(new FakeBackend()).Save("", "k"));
    }

    [LinuxFact]
    public void Availability_follows_whether_the_service_answers_and_never_throws()
    {
        Assert.True(SecretServiceStore.IsAvailable(new FakeBackend()));
        Assert.False(SecretServiceStore.IsAvailable(new FakeBackend { Fail = new DllNotFoundException() }));
        Assert.False(SecretServiceStore.IsAvailable(new FakeBackend { Fail = new KeyringException("no service", serviceMissing: true) }));
    }

    [LinuxFact]
    public void Round_trips_through_the_real_keyring()
    {
        if (!SecretServiceStore.IsAvailable())
        {
            Assert.Skip("no secret service");
        }

        _real = new SecretServiceStore();

        Assert.Null(_real.Read(_profile));
        _real.Save(_profile, "first-key-ñ-✓");
        Assert.Equal("first-key-ñ-✓", _real.Read(_profile));
        _real.Save(_profile, "second-key");
        Assert.Equal("second-key", _real.Read(_profile));
        _real.Delete(_profile);
        Assert.Null(_real.Read(_profile));
        _real.Delete(_profile);
    }

    private sealed class FakeBackend : ISecretBackend
    {
        public Exception? Fail { get; init; }

        public Dictionary<string, string> Items { get; } = [];

        public Dictionary<string, string> Labels { get; } = [];

        public void Store(string profileId, string label, string secret)
        {
            Check();
            Items[profileId] = secret;
            Labels[profileId] = label;
        }

        public string? Lookup(string profileId)
        {
            Check();
            return Items.GetValueOrDefault(profileId);
        }

        public void Clear(string profileId)
        {
            Check();
            Items.Remove(profileId);
        }

        private void Check()
        {
            if (Fail is not null)
            {
                throw Fail;
            }
        }
    }
}
