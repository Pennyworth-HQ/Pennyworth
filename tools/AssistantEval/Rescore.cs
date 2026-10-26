using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AssistantEval;

/// <summary>Re-runs the current checks on the answers stored in a report, keeping its header and timings.</summary>
public static partial class Rescore
{
    public static void Run(string reportPath, List<Scenario> scenarios)
    {
        string[] lines = File.ReadAllLines(reportPath);
        var header = new StringBuilder();
        int i = 0;
        for (; i < lines.Length && !lines[i].StartsWith("- en:", StringComparison.Ordinal) && !lines[i].StartsWith("- pt-BR:", StringComparison.Ordinal); i++)
        {
            header.AppendLine(lines[i]);
        }

        var runs = new List<Run>();
        for (; i < lines.Length; i++)
        {
            Match section = SectionPattern().Match(lines[i]);
            if (!section.Success)
            {
                continue;
            }

            Scenario scenario = scenarios.Single(s => s.Id == section.Groups[1].Value);
            Question question = scenario.Questions.Single(q => q.Lang == section.Groups[2].Value);
            var answer = new StringBuilder();
            Match metrics = Match.Empty;
            for (i++; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
            {
                if (lines[i].StartsWith("> ", StringComparison.Ordinal) || lines[i] == ">")
                {
                    answer.AppendLine(lines[i].Length > 2 ? lines[i][2..] : "");
                }
                else if (MetricsPattern().Match(lines[i]) is { Success: true } m)
                {
                    metrics = m;
                }
            }

            i--;
            double Seconds(int group) => double.Parse(metrics.Groups[group].Value, CultureInfo.InvariantCulture);
            string text = answer.ToString().Trim();
            TimeSpan total = TimeSpan.FromSeconds(Seconds(1));
            TimeSpan first = TimeSpan.FromSeconds(Seconds(2));
            long tokens = (long)Math.Round(Seconds(3) * (total - first).TotalSeconds);
            runs.Add(new Run(scenario, question, text, Checks.Run(scenario, question, text), first, total, tokens));
        }

        File.WriteAllText(reportPath, header + Report.Body(runs));
        Console.WriteLine($"Rescored {runs.Count} answers: {runs.Count(r => r.Passed)} pass. {reportPath}");
    }

    [GeneratedRegex(@"^## (\S+) · (\S+) · (PASS|FAIL)$")]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"^([\d.]+) s · first token ([\d.]+) s · ([\d.]+) tok/s")]
    private static partial Regex MetricsPattern();
}
