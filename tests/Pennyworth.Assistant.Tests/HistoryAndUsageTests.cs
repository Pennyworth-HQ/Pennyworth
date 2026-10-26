namespace Pennyworth.Assistant.Tests;

public sealed class HistoryAndUsageTests : IDisposable
{
    private static readonly DateTimeOffset Oct25 = new(2026, 10, 25, 21, 0, 0, TimeSpan.FromHours(-3));

    private readonly string _dir = Directory.CreateTempSubdirectory("pw-assistant-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Keeps_conversations_with_feedback_and_the_context_sent()
    {
        var store = new ConversationStore(Path.Combine(_dir, "conversations"));
        var info = store.Start("Why is C: full?", "local", Oct25);

        store.AppendTurn(info.Id, new ConversationTurn("user", "Why is C: full?", Oct25, SentContext: "FACTS…"));
        int answer = store.AppendTurn(info.Id, new ConversationTurn("assistant", "It is 92% full.", Oct25.AddSeconds(3), Blocked: 1));
        store.SetFeedback(info.Id, answer, helpful: true, Oct25.AddMinutes(1));

        var loaded = store.Load(info.Id)!;
        Assert.Equal("Why is C: full?", loaded.Info.Title);
        Assert.Equal(2, loaded.Info.TurnCount);
        Assert.Equal(Oct25.AddMinutes(1), loaded.Info.LastActivity);
        Assert.Equal("FACTS…", loaded.Turns[0].SentContext);
        Assert.True(loaded.Turns[1].Helpful);
        Assert.Equal(1, loaded.Turns[1].Blocked);
    }

    [Fact]
    public void Lists_newest_first_skips_damaged_lines_and_deletes_everything()
    {
        string folder = Path.Combine(_dir, "conversations");
        var store = new ConversationStore(folder);
        var old = store.Start("old", "p", Oct25);
        var recent = store.Start("recent", "p", Oct25.AddDays(1));
        File.AppendAllText(Path.Combine(folder, old.Id + ".jsonl"), "{not json\n");
        store.AppendTurn(old.Id, new ConversationTurn("user", "still here", Oct25.AddHours(1)));

        Assert.Equal([recent.Id, old.Id], store.List().Select(c => c.Id));
        Assert.Equal("still here", store.Load(old.Id)!.Turns.Single().Text);

        store.DeleteAll();
        Assert.Empty(store.List());
    }

    [Fact]
    public void Rejects_ids_that_are_paths()
    {
        var store = new ConversationStore(_dir);

        Assert.Throws<ArgumentException>(() => store.Load(@"..\settings"));
    }

    [Fact]
    public void Counts_tokens_per_profile_and_month()
    {
        var meter = new UsageMeter(Path.Combine(_dir, "usage.json"));

        meter.Add("cloud", 1000, 200, Oct25);
        meter.Add("cloud", 500, 100, Oct25.AddDays(2));
        meter.Add("cloud", 9, 9, Oct25.AddDays(10)); // November
        meter.Add("other", 1, 1, Oct25);

        Assert.Equal(1800, meter.Month("cloud", Oct25).Total);
        Assert.Equal(18, meter.Month("cloud", Oct25.AddDays(10)).Total);
    }

    [Theory]
    [InlineData(null, 5000, UsageState.Ok)]
    [InlineData(10_000L, 7_999, UsageState.Ok)]
    [InlineData(10_000L, 8_000, UsageState.Near)]
    [InlineData(10_000L, 10_000, UsageState.Reached)]
    public void Warns_near_the_cap_and_stops_at_it(long? cap, long used, UsageState expected)
    {
        var meter = new UsageMeter(Path.Combine(_dir, "usage.json"));
        meter.Add("cloud", used, 0, Oct25);

        Assert.Equal(expected, meter.State(new AssistantProfile("cloud", AssistantBackend.Anthropic, "Claude", "model", MonthlyTokenCap: cap), Oct25));
    }

    [Fact]
    public void A_damaged_usage_file_starts_again()
    {
        string path = Path.Combine(_dir, "usage.json");
        File.WriteAllText(path, "garbage");
        var meter = new UsageMeter(path);

        meter.Add("cloud", 5, 5, Oct25);

        Assert.Equal(10, meter.Month("cloud", Oct25).Total);
    }
}
