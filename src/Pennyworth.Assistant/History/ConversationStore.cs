using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pennyworth.Assistant;

/// <param name="Role">"user" or "assistant".</param>
/// <param name="SentContext">For questions: the context text sent with it, so an answer can be checked later against what the model saw.</param>
/// <param name="Helpful">For answers: the person's feedback, when given.</param>
public sealed record ConversationTurn(string Role, string Text, DateTimeOffset When, string? SentContext = null, bool? Helpful = null, int Blocked = 0);

public sealed record ConversationInfo(string Id, string Title, string ProfileId, DateTimeOffset Started, DateTimeOffset LastActivity, int TurnCount);

public sealed record Conversation(ConversationInfo Info, IReadOnlyList<ConversationTurn> Turns);

/// <summary>One line of a conversation file: "start", "turn" or "feedback".</summary>
internal sealed record ConversationEvent(
    string Kind,
    DateTimeOffset When,
    string? Title = null,
    string? ProfileId = null,
    string? Role = null,
    string? Text = null,
    string? SentContext = null,
    int? Turn = null,
    bool? Helpful = null,
    int Blocked = 0);

[JsonSerializable(typeof(ConversationEvent))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;

/// <summary>
/// Conversations kept only on this PC, one JSON Lines file each under
/// <c>%LocalAppData%\Pennyworth\assistant\conversations</c>. Appends never rewrite a file; a
/// damaged line is skipped.
/// </summary>
public sealed class ConversationStore(string folder)
{
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "assistant", "conversations");

    public ConversationInfo Start(string title, string profileId, DateTimeOffset when)
    {
        string id = $"{when.UtcDateTime:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        Append(id, new ConversationEvent("start", when, Title: title, ProfileId: profileId));
        return new ConversationInfo(id, title, profileId, when, when, 0);
    }

    /// <returns>The index of the new turn in the conversation.</returns>
    public int AppendTurn(string id, ConversationTurn turn)
    {
        int index = Load(id)?.Turns.Count ?? 0;
        Append(id, new ConversationEvent("turn", turn.When, Role: turn.Role, Text: turn.Text, SentContext: turn.SentContext, Blocked: turn.Blocked));
        return index;
    }

    public void SetFeedback(string id, int turn, bool helpful, DateTimeOffset when) =>
        Append(id, new ConversationEvent("feedback", when, Turn: turn, Helpful: helpful));

    /// <summary>Every conversation, most recent activity first.</summary>
    public IReadOnlyList<ConversationInfo> List()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(folder, "*.jsonl")
                .Select(path => Load(Path.GetFileNameWithoutExtension(path))?.Info)
                .OfType<ConversationInfo>()
                .OrderByDescending(info => info.LastActivity),
        ];
    }

    public Conversation? Load(string id)
    {
        string path = PathOf(id);
        if (!File.Exists(path))
        {
            return null;
        }

        ConversationEvent? start = null;
        var turns = new List<ConversationTurn>();
        DateTimeOffset last = default;
        foreach (string line in ReadLines(path))
        {
            ConversationEvent? e;
            try
            {
                e = JsonSerializer.Deserialize(line, ConversationJsonContext.Default.ConversationEvent);
            }
            catch (JsonException)
            {
                continue;
            }

            if (e is null)
            {
                continue;
            }

            last = e.When > last ? e.When : last;
            switch (e.Kind)
            {
                case "start":
                    start ??= e;
                    break;
                case "turn" when e.Role is not null && e.Text is not null:
                    turns.Add(new ConversationTurn(e.Role, e.Text, e.When, e.SentContext, null, e.Blocked));
                    break;
                case "feedback" when e.Turn is int index && index >= 0 && index < turns.Count:
                    turns[index] = turns[index] with { Helpful = e.Helpful };
                    break;
            }
        }

        return start is null
            ? null
            : new Conversation(new ConversationInfo(id, start.Title ?? "", start.ProfileId ?? "", start.When, last, turns.Count), turns);
    }

    public void Delete(string id)
    {
        if (File.Exists(PathOf(id)))
        {
            File.Delete(PathOf(id));
        }
    }

    /// <summary>"Delete all conversations" in Settings.</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(folder))
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.jsonl"))
            {
                File.Delete(path);
            }
        }
    }

    private void Append(string id, ConversationEvent e)
    {
        Directory.CreateDirectory(folder);
        File.AppendAllText(PathOf(id), JsonSerializer.Serialize(e, ConversationJsonContext.Default.ConversationEvent) + "\n");
    }

    private string PathOf(string id)
    {
        if (id.Length == 0 || id.IndexOfAny([.. Path.GetInvalidFileNameChars(), '.']) >= 0)
        {
            throw new ArgumentException($"Not a conversation id: '{id}'.", nameof(id));
        }

        return Path.Combine(folder, id + ".jsonl");
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
    }
}
