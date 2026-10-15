namespace ClickyWindows.Models;

public record ConversationTurn(string Role, string Content);

/// <summary>
/// Maintains a rolling window of the last N conversation turns for context.
/// </summary>
public class ConversationHistory
{
    private readonly int _maxTurns;
    private readonly List<ConversationTurn> _turns = new();

    public ConversationHistory(int maxTurns = 10)
    {
        _maxTurns = maxTurns;
    }

    public IReadOnlyList<ConversationTurn> Turns => _turns.AsReadOnly();

    public void AddUserMessage(string content) => Add("user", content);
    public void AddAssistantMessage(string content) => Add("assistant", content);

    /// <summary>Adds to the last assistant message (a task's result after the reply that started it).</summary>
    public void AppendToLastAssistant(string content)
    {
        if (_turns.Count > 0 && _turns[^1].Role == "assistant")
            _turns[^1] = _turns[^1] with { Content = _turns[^1].Content + "\n" + content };
        else
            AddAssistantMessage(content);
    }

    private void Add(string role, string content)
    {
        _turns.Add(new ConversationTurn(role, content));
        // Keep only the last _maxTurns entries
        while (_turns.Count > _maxTurns)
            _turns.RemoveAt(0);
    }

    public void Clear() => _turns.Clear();
}
