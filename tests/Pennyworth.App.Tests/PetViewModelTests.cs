using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Tests;

public sealed class PetViewModelTests
{
    private DateTimeOffset _now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    private PetViewModel Create(PetKind kind = PetKind.Cat) => new(kind, () => _now);

    [Fact]
    public void No_pet_is_hidden_and_says_nothing()
    {
        var pet = Create(PetKind.None);

        pet.Say("Processor at 99%", "Busy");

        Assert.False(pet.IsVisible);
        Assert.False(pet.IsSpeaking);
        Assert.Equal("", pet.Art);
    }

    [Fact]
    public void A_bubble_leaves_on_its_own_unless_the_pointer_is_on_it()
    {
        var pet = Create();
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
        var pet = Create();
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
        var pet = Create();
        int opened = 0;
        pet.Say("Memory at 95%", "Full", () => opened++);

        Assert.True(pet.Speech!.CanOpen);
        pet.OpenSpeechCommand.Execute(null);

        Assert.Equal(1, opened);
        Assert.False(pet.IsSpeaking);
    }

    [Fact]
    public void The_pet_moves_its_mouth_while_talking_and_sits_still_with_reduced_motion()
    {
        var pet = Create();
        string idle = pet.Art;

        pet.Say("Hi", "");
        var talking = new HashSet<string>();
        for (int i = 0; i < 4; i++)
        {
            pet.Tick();
            talking.Add(pet.Art);
        }

        Assert.Equal(2, talking.Count);

        pet.DismissCommand.Execute(null);
        pet.ReduceMotion = true;
        for (int i = 0; i < 24; i++)
        {
            pet.Tick();
            Assert.Equal(idle, pet.Art);
        }
    }

    [Fact]
    public void Every_frame_of_a_pet_has_the_same_shape()
    {
        foreach (PetKind kind in PetArt.All.Where(k => k != PetKind.None))
        {
            string[] open = PetArt.Frame(kind, talking: false, 0).Split('\n');
            string[] blink = PetArt.Frame(kind, talking: false, 11).Split('\n');
            string[] talk = PetArt.Frame(kind, talking: true, 0).Split('\n');

            Assert.NotEqual(open, blink);
            Assert.NotEqual(open, talk);
            Assert.Equal(open.Length, blink.Length);
            Assert.Equal(open.Length, talk.Length);
            for (int line = 0; line < open.Length; line++)
            {
                Assert.Equal(open[line].Length, blink[line].Length);
                Assert.Equal(open[line].Length, talk[line].Length);
            }
        }
    }

    [Fact]
    public void Removing_the_pet_drops_what_it_was_about_to_say()
    {
        var pet = Create();
        pet.Say("One", "");
        pet.Say("Two", "");

        pet.Kind = PetKind.None;
        pet.Kind = PetKind.Dog;

        Assert.False(pet.IsSpeaking);
    }
}
