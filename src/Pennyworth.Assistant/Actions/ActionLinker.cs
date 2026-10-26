using System.Text;
using System.Text.RegularExpressions;

namespace Pennyworth.Assistant;

/// <summary>
/// Finds <c>[[action:…]]</c> codes in a streamed answer, turns the first allowed one into an
/// <see cref="ActionUpdate"/> and removes every code from the text. Text that might be the start
/// of a code is held back until it is clearly not one. Small models also wrote one bracket or
/// extra closing brackets in phase 0 (<c>[action:x]</c>, <c>]]]]</c>, <c>].</c>); both are handled.
/// </summary>
public sealed partial class ActionLinker(IReadOnlyList<AssistantAction> allowed)
{
    private const int MaxCodeLength = 100;
    private static readonly string[] Openers = ["[[action:", "[action:"];

    private readonly StringBuilder _buffer = new();
    private bool _afterCode;

    public AssistantAction? Linked { get; private set; }

    public IEnumerable<AssistantUpdate> Push(string chunk)
    {
        _buffer.Append(chunk);
        return Drain(final: false);
    }

    public IEnumerable<AssistantUpdate> Flush() => Drain(final: true);

    private List<AssistantUpdate> Drain(bool final)
    {
        var updates = new List<AssistantUpdate>();
        var text = new StringBuilder();
        string pending = _buffer.ToString();
        _buffer.Clear();

        while (pending.Length > 0)
        {
            if (_afterCode)
            {
                // Swallow stray "]" left after a code; hold "]." style leftovers until real text follows.
                if (StrayPattern().IsMatch(pending))
                {
                    if (final)
                    {
                        pending = "";
                    }

                    break;
                }

                pending = pending.TrimStart(']');
                _afterCode = false;
            }

            Match code = CodePattern().Match(pending);
            int open = pending.IndexOf('[', StringComparison.Ordinal);
            if (code.Success && code.Index == open)
            {
                text.Append(pending, 0, code.Index);
                if (AssistantAction.Parse(code.Groups[1].Value) is { } action && allowed.Contains(action) && Linked is null)
                {
                    Flush(text, updates);
                    Linked = action;
                    updates.Add(new ActionUpdate(action));
                }

                pending = pending[(code.Index + code.Length)..];
                _afterCode = true;
                continue;
            }

            if (open < 0)
            {
                text.Append(pending);
                pending = "";
                break;
            }

            string tail = pending[open..];
            bool maybeCode = Openers.Any(o => o.StartsWith(tail, StringComparison.OrdinalIgnoreCase) || tail.StartsWith(o, StringComparison.OrdinalIgnoreCase));
            if (maybeCode && !final && tail.Length < MaxCodeLength && !tail.Contains('\n'))
            {
                text.Append(pending, 0, open);
                pending = tail; // wait for more
                break;
            }

            text.Append(pending, 0, open + 1);
            pending = pending[(open + 1)..];
        }

        _buffer.Append(pending);
        Flush(text, updates);
        return updates;
    }

    private static void Flush(StringBuilder text, List<AssistantUpdate> updates)
    {
        if (text.Length > 0)
        {
            updates.Add(new TextUpdate(text.ToString()));
            text.Clear();
        }
    }

    [GeneratedRegex(@"\[\[?action:([^\]\r\n]{1,90})\]\]?", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^[\]\.\s]*$")]
    private static partial Regex StrayPattern();
}
