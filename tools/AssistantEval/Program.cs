// Phase 0 evaluation harness for the Pennyworth assistant.
//
//   dotnet run --project tools/AssistantEval -- --backend local --model <folder> --model-id phi-4-mini-gpu --name phi-4-mini-local
//   dotnet run --project tools/AssistantEval -- --backend onnx --model <folder> --ep dml --name phi-4-mini-dml
//   dotnet run --project tools/AssistantEval -- --backend openai --endpoint <url> --model <id> --key-env MY_KEY --name gpt
//
//   dotnet run --project tools/AssistantEval -- --rescore tools/AssistantEval/reports/<report>.md
//
// Writes tools/AssistantEval/reports/<date>-<name>.md. --rescore re-checks the answers in an
// existing report with the current checks, keeping its timings, and rewrites it in place.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AssistantEval;
using Microsoft.Extensions.AI;
using Pennyworth.Assistant;

string scenariosPath = Path.Combine(AppContext.BaseDirectory, "scenarios", "scenarios.json");
if (Array.IndexOf(args, "--rescore") is int r and >= 0)
{
    Rescore.Run(args[r + 1], Scenarios.Load(scenariosPath));
    return 0;
}

var options = Options.Parse(args);
var scenarios = Scenarios.Load(scenariosPath)
    .Where(s => options.Only is null || options.Only.Contains(s.Id))
    .ToList();

var load = Stopwatch.StartNew();
using IChatClient client = Backends.Create(options);
load.Stop();
long memoryAfterLoad = Process.GetCurrentProcess().WorkingSet64;
Console.WriteLine($"Loaded {options.Name} in {load.Elapsed.TotalSeconds:0.0} s, working set {memoryAfterLoad / 1e9:0.00} GB");

var results = new List<Run>();
foreach (Scenario scenario in scenarios)
{
    foreach (Question question in scenario.Questions)
    {
        List<ChatMessage> messages = [new(ChatRole.System, PersonaPrompt.Compose(Scenarios.Bundle(scenario, question))), new(ChatRole.User, question.Text)];
        var chat = new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 350 };
        var answer = new StringBuilder();
        var watch = Stopwatch.StartNew();
        TimeSpan? first = null;
        int chunks = 0;
        long? outputTokens = null;
        try
        {
            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(messages, chat))
            {
                if (update.Text.Length > 0)
                {
                    first ??= watch.Elapsed;
                    chunks++;
                    answer.Append(update.Text);
                }

                foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
                {
                    outputTokens = usage.Details.OutputTokenCount ?? outputTokens;
                }
            }
        }
        catch (Exception ex)
        {
            answer.Append($"[ERROR {ex.GetType().Name}: {ex.Message}]");
        }

        watch.Stop();
        string text = answer.ToString().Trim();
        long tokens = outputTokens ?? chunks; // local streaming yields about one token per update
        var run = new Run(scenario, question, text, Checks.Run(scenario, question, text), first ?? watch.Elapsed, watch.Elapsed, tokens);
        results.Add(run);
        Console.WriteLine($"{(run.Passed ? "PASS" : "FAIL")} {scenario.Id,-16} {question.Lang,-5} {run.Total.TotalSeconds,5:0.0}s {run.TokensPerSecond,5:0.0} tok/s  {string.Join(" ", run.Checks.Where(c => !c.Passed).Select(c => c.Name))}");
    }
}

long peak = Process.GetCurrentProcess().PeakWorkingSet64;
string report = Report.Write(options, results, load.Elapsed, memoryAfterLoad, peak);
string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "reports"));
Directory.CreateDirectory(dir);
string file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}-{options.Name}.md");
File.WriteAllText(file, report);
Console.WriteLine($"Report: {file}");
return 0;

public sealed record Run(Scenario Scenario, Question Question, string Answer, IReadOnlyList<CheckResult> Checks, TimeSpan FirstToken, TimeSpan Total, long OutputTokens)
{
    public bool Passed => Checks.All(c => c.Passed);

    public double TokensPerSecond => (Total - FirstToken).TotalSeconds > 0 ? OutputTokens / (Total - FirstToken).TotalSeconds : 0;
}

public sealed record Options(string Backend, string Model, string Ep, string? Endpoint, string? KeyEnv, string Name, HashSet<string>? Only)
{
    /// <summary>"--provider-option key=value", e.g. enable_graph_capture=0 for models DirectML can't capture.</summary>
    public IReadOnlyList<(string Key, string Value)> ProviderOptions { get; init; } = [];

    /// <summary>"--model-id phi-4-mini-gpu" for the <c>local</c> backend.</summary>
    public string? ModelId { get; init; }

    /// <summary>"--repetition-penalty 1.0" for the <c>local</c> backend.</summary>
    public float? RepetitionPenalty { get; init; }

    public static Options Parse(string[] args)
    {
        string? Get(string name)
        {
            int i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string backend = Get("backend") ?? throw new ArgumentException("--backend onnx|openai is required");
        string model = Get("model") ?? throw new ArgumentException("--model is required");
        string? only = Get("only");
        string? providerOption = Get("provider-option");
        return new Options(backend, model, Get("ep") ?? "dml", Get("endpoint"), Get("key-env"),
            Get("name") ?? Path.GetFileName(model.TrimEnd('/', '\\')),
            only is null ? null : [.. only.Split(',')])
        {
            ProviderOptions = providerOption is null ? [] : [(providerOption.Split('=')[0], providerOption.Split('=')[1])],
            ModelId = Get("model-id"),
            RepetitionPenalty = Get("repetition-penalty") is { } penalty ? float.Parse(penalty, System.Globalization.CultureInfo.InvariantCulture) : null,
        };
    }
}

public static class Report
{
    public static string Write(Options options, List<Run> runs, TimeSpan load, long memoryAfterLoad, long peak)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine(inv, $"# Assistant evaluation — {options.Name}");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        string ep = options.Backend == "onnx" ? " · ep `" + options.Ep + "`" : "";
        sb.AppendLine(inv, $"- Backend: `{options.Backend}` · model `{options.Model}`{ep}");
        sb.AppendLine(inv, $"- Load: {load.TotalSeconds:0.0} s · working set after load {memoryAfterLoad / 1e9:0.00} GB · peak {peak / 1e9:0.00} GB");
        sb.Append(Body(runs));
        return sb.ToString();
    }

    /// <summary>Everything after the header: per-language summary, check table and each answer.</summary>
    public static string Body(List<Run> runs)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        foreach (string lang in runs.Select(r => r.Question.Lang).Distinct())
        {
            var set = runs.Where(r => r.Question.Lang == lang).ToList();
            sb.AppendLine(inv, $"- {lang}: {set.Count(r => r.Passed)}/{set.Count} passed all checks · median {Median(set.Select(r => r.Total.TotalSeconds)):0.0} s per answer · median first token {Median(set.Select(r => r.FirstToken.TotalSeconds)):0.0} s · median {Median(set.Select(r => r.TokensPerSecond)):0.0} tok/s");
        }

        sb.AppendLine();
        sb.AppendLine("| Check | Failures |");
        sb.AppendLine("|---|---|");
        foreach (var group in runs.SelectMany(r => r.Checks).GroupBy(c => c.Name))
        {
            sb.AppendLine(inv, $"| {group.Key} | {group.Count(c => !c.Passed)} / {group.Count()} |");
        }

        foreach (Run run in runs)
        {
            sb.AppendLine();
            sb.AppendLine(inv, $"## {run.Scenario.Id} · {run.Question.Lang} · {(run.Passed ? "PASS" : "FAIL")}");
            sb.AppendLine();
            sb.AppendLine(inv, $"**Q:** {run.Question.Text}");
            sb.AppendLine();
            foreach (string line in run.Answer.Split('\n'))
            {
                sb.AppendLine("> " + line);
            }

            sb.AppendLine();
            string checks = string.Join(" · ", run.Checks.Select(c => c.Name + " " + (c.Passed ? "✓" : "✗") + (c.Detail.Length > 0 ? " (" + c.Detail + ")" : "")));
            sb.AppendLine(inv, $"{run.Total.TotalSeconds:0.0} s · first token {run.FirstToken.TotalSeconds:0.0} s · {run.TokensPerSecond:0.0} tok/s · {checks}");
        }

        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
