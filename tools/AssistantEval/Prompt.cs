using System.Text;

namespace AssistantEval;

/// <summary>
/// Provisional persona prompt for phase 0. Phase 1 moves prompts into Pennyworth.Assistant and
/// this harness will use that composer instead.
/// </summary>
public static class Prompt
{
    public static readonly Dictionary<string, string> ActionHelp = new()
    {
        ["cleanup"] = "opens the guided cleanup screen",
        ["cleanup.item:temp"] = "shows every temporary file that cleanup would delete",
        ["cleanup.item:shader"] = "shows the game graphics cache files that cleanup would delete",
        ["cleanup.item:recycle-bin"] = "shows every item in the Recycle Bin before emptying it",
        ["memory"] = "opens the Memory & CPU screen",
        ["close-app:chrome"] = "asks to close Google Chrome",
        ["storage"] = "opens the storage summary",
        ["specs"] = "opens the full PC specs screen",
    };

    public static string System(Scenario scenario, string lang)
    {
        bool pt = lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        sb.AppendLine(pt
            ? "Você é o assistente do Pennyworth, um app gratuito que explica o PC em linguagem simples. Fale como um mordomo britânico discreto: educado, caloroso, levemente irônico, sempre claro. Responda em português do Brasil, tratando a pessoa por \"você\"."
            : "You are the assistant inside Pennyworth, a free app that explains a PC in plain language. Speak like a discreet British butler: polite, warm, a little dry, always clear. Answer in English.");
        sb.AppendLine();
        sb.AppendLine(pt ? "Regras:" : "Rules:");
        sb.AppendLine(pt
            ? """
              - Use apenas os FATOS abaixo sobre este PC. Nunca invente números, peças ou medições.
              - Se os fatos não respondem à pergunta, diga com franqueza que não consegue saber pelo que vê.
              - Explique termos técnicos em poucas palavras. No máximo 120 palavras.
              - Nunca sugira editar o registro, desativar o Windows Defender, o Firewall ou o Windows Update, overclock, mudar a BIOS, scripts, "otimizadores" ou limpadores de RAM.
              - Você pode sugerir no máximo UMA ação, escrevendo exatamente um destes códigos no fim da resposta:
              """
            : """
              - Use only the FACTS below about this PC. Never invent numbers, parts or measurements.
              - If the facts can't answer the question, say plainly that you can't tell from what you can see.
              - Explain technical terms in a few words. At most 120 words.
              - Never suggest editing the registry, disabling Windows Defender, the Firewall or Windows Update, overclocking, BIOS changes, scripts, "optimizers" or RAM cleaners.
              - You may suggest at most ONE action by writing exactly one of these codes at the end of the answer:
              """);
        foreach (string action in scenario.AllowedActions)
        {
            sb.AppendLine($"  [[action:{action}]] — {ActionHelp.GetValueOrDefault(action, action)}");
        }

        sb.AppendLine(pt ? "- Nunca mencione Alfred, Batman, Bruce Wayne ou Gotham." : "- Never mention Alfred, Batman, Bruce Wayne or Gotham.");
        sb.AppendLine();
        sb.AppendLine(pt ? "FATOS medidos pelo Pennyworth:" : "FACTS measured by Pennyworth:");
        foreach (Fact fact in scenario.Facts)
        {
            sb.AppendLine($"- {fact.Label}: {fact.Value}");
        }

        if (scenario.Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(pt ? "CONCLUSÕES das regras do Pennyworth:" : "FINDINGS from Pennyworth's rules:");
            foreach (string finding in scenario.Findings)
            {
                sb.AppendLine($"- {finding}");
            }
        }

        return sb.ToString();
    }
}
