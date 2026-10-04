using System.Globalization;
using System.Text.RegularExpressions;

namespace Zen.Blazor.Services;

public sealed record ChatMessage(long Id, DateTimeOffset At, string Text);

public sealed partial class MessageBoard(ILogger<MessageBoard> logger)
{
    public const int MaxLength = 500;
    public const int Capacity = 50;

    private readonly Lock _gate = new();
    private ChatMessage[] _messages = [];
    private long _nextId;

    public event Action? Changed;

    public IReadOnlyList<ChatMessage> Messages => Volatile.Read(ref _messages);

    public bool TryPost(string? input)
    {
        var text = Normalize(input);
        if (text.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var current = _messages;
            var next = new ChatMessage[Math.Min(current.Length + 1, Capacity)];
            next[0] = new ChatMessage(++_nextId, DateTimeOffset.Now, text);
            Array.Copy(current, 0, next, 1, next.Length - 1);
            Volatile.Write(ref _messages, next);
        }

        Notify();
        return true;
    }

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "";
        }

        var text = string.Concat(input.ReplaceLineEndings("\n").Where(c => c == '\n' || !char.IsControl(c)));
        text = ExtraBlankLines().Replace(text, "\n\n").Trim();

        if (text.Length > MaxLength)
        {
            var cut = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            text = text[..cut].TrimEnd();
        }

        return text.Any(IsVisible) ? text : "";
    }

    private static bool IsVisible(char c) =>
        !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format;

    private void Notify()
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Message board subscriber failed");
            }
        }
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();
}
