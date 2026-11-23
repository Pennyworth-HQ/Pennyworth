using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class PetViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pennyworth-pets").FullName;
    private readonly FakePetHost _host = new();
    private DateTimeOffset _now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    public PetViewModelTests() => Localizer.Instance.SetLanguage(LanguagePreference.English);

    public void Dispose()
    {
        Localizer.Instance.SetLanguage(LanguagePreference.English);
        Directory.Delete(_dir, recursive: true);
    }

    private PetLibrary Library => new(Path.Combine(_dir, "library"));

    private string BobaZip() =>
        SamplePets.Zip(_dir, $"boba-{Guid.NewGuid():N}.zip", ("pet.json", SamplePets.Utf8(SamplePets.PetJson)), ("spritesheet.webp", SamplePets.WebpHeader));

    /// <summary>A pet already added, as after a restart.</summary>
    private PetViewModel WithBoba()
    {
        Library.Import(BobaZip());
        return new PetViewModel(Library, null, _host, () => _now, "boba");
    }

    private PetViewModel Empty(string? picked = null) => new(Library, new FakeFilePicker(picked), _host, () => _now);

    [Fact]
    public void No_pet_is_hidden_and_says_nothing()
    {
        PetViewModel pet = Empty();

        pet.Say("Processor at 99%", "Busy");

        Assert.False(pet.IsVisible);
        Assert.False(pet.IsSpeaking);
    }

    [Fact]
    public async Task Adding_a_pet_from_the_picker_puts_it_on_the_desktop_and_remembers_it()
    {
        PetViewModel pet = Empty(picked: BobaZip());

        await pet.AddCommand.ExecuteAsync(null);

        Assert.True(pet.IsVisible);
        Assert.Equal("Boba", pet.Name);
        Assert.Equal("boba", _host.SavedId);
        Assert.Equal("Hi! Boba here.", pet.Speech!.Title);
        Assert.Equal(PetAnimation.Jumping, pet.Animation);
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        PetViewModel pet = Empty(picked: null);

        await pet.AddCommand.ExecuteAsync(null);

        Assert.False(pet.IsVisible);
        Assert.Null(_host.Toast);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pet_says_so_and_keeps_the_current_pet()
    {
        PetViewModel pet = WithBoba();
        string photo = Path.Combine(_dir, "photo.png");
        File.WriteAllBytes(photo, SamplePets.Png(1920, 1080));

        await pet.ImportAsync(photo);

        Assert.Equal("Boba", pet.Name);
        Assert.Equal(Resources.Strings.PetBadSpritesheet, _host.Toast);
        Assert.Equal(PetAnimation.Failed, pet.Animation);
    }

    [Fact]
    public void The_saved_pet_is_back_on_the_next_start() => Assert.Equal("Boba", WithBoba().Name);

    [Fact]
    public void Removing_the_pet_forgets_it_and_drops_what_it_was_about_to_say()
    {
        PetViewModel pet = WithBoba();
        pet.Say("One", "");
        pet.Say("Two", "");

        pet.RemoveCommand.Execute(null);

        Assert.False(pet.IsVisible);
        Assert.False(pet.IsSpeaking);
        Assert.Null(_host.SavedId);
    }

    [Fact]
    public void A_bubble_leaves_on_its_own_unless_the_pointer_is_on_it()
    {
        PetViewModel pet = WithBoba();
        pet.Say("Processor at 99%", "Busy");
        Assert.Equal("Processor at 99%", pet.Speech!.Title);

        _now += PetViewModel.SpeechDuration;
        pet.IsHovered = true;
        pet.Tick();
        Assert.True(pet.IsSpeaking);

        pet.IsHovered = false;
        pet.Tick();
        Assert.False(pet.IsSpeaking);
    }

    [Fact]
    public void Bubbles_wait_their_turn_and_keep_only_the_latest_few()
    {
        PetViewModel pet = WithBoba();
        for (int i = 0; i <= PetViewModel.MaxQueued + 1; i++)
        {
            pet.Say($"Alert {i}", "");
        }

        Assert.Equal("Alert 0", pet.Speech!.Title);
        pet.DismissCommand.Execute(null);
        Assert.Equal("Alert 2", pet.Speech!.Title); // Alert 1 was pushed out.
        pet.DismissCommand.Execute(null);
        pet.DismissCommand.Execute(null);
        Assert.Equal($"Alert {PetViewModel.MaxQueued + 1}", pet.Speech!.Title);
        pet.DismissCommand.Execute(null);
        Assert.False(pet.IsSpeaking);
    }

    [Fact]
    public void Clicking_the_bubble_does_what_the_notification_offers()
    {
        PetViewModel pet = WithBoba();
        int opened = 0;
        pet.Say("Memory at 95%", "Full", () => opened++);

        Assert.True(pet.Speech!.CanOpen);
        pet.OpenSpeechCommand.Execute(null);

        Assert.Equal(1, opened);
        Assert.False(pet.IsSpeaking);
    }

    [Fact]
    public void The_pet_waves_while_talking_and_runs_while_the_app_works()
    {
        PetViewModel pet = WithBoba();

        pet.Update(PetActivity.Working);
        Assert.Equal(PetAnimation.Running, pet.Animation);

        pet.Say("Hi", "");
        Assert.Equal(PetAnimation.Waving, pet.Animation);

        pet.DismissCommand.Execute(null);
        pet.Update(PetActivity.Thinking);
        Assert.Equal(PetAnimation.Review, pet.Animation);

        pet.Update(PetActivity.Resting);
        Assert.Equal(PetAnimation.Idle, pet.Animation);
    }

    private sealed class FakePetHost : IPetHost
    {
        public string? Toast { get; private set; }

        public string? SavedId { get; private set; } = "unset";

        public void ShowToast(string message) => Toast = message;

        public void SavePetId(string? id) => SavedId = id;
    }

    private sealed class FakeFilePicker(string? path) : IFilePicker
    {
        public Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns) => Task.FromResult(path);
    }
}
