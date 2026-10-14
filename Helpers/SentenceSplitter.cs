using System.Text;
using ClickyWindows.Services;

namespace ClickyWindows.Helpers;

/// <summary>
/// Cuts a streamed reply into speakable sentences as it arrives, so speech can start before the
/// reply is complete. [POINT:…] tags are removed, and nothing is cut inside an unfinished tag.
/// Very short pieces ("Sí.") are joined with the next sentence so the voice doesn't sound choppy.
/// </summary>
public sealed class SentenceSplitter
{
    private const int MinLength = 24;
    private readonly StringBuilder _pending = new();

    public IEnumerable<string> Add(string chunk)
    {
        _pending.Append(chunk);
        var sentences = new List<string>();
        int searchFrom = 0;
        while (true)
        {
            var text = _pending.ToString();
            int end = FindEnd(text, searchFrom);
            if (end < 0) break;

            var piece = text[..end];
            if (OpenTag(piece))
            {
                searchFrom = end; // a period inside [POINT:…] is not a sentence end
                continue;
            }
            if (ClaudeService.StripPointTags(piece).Length < MinLength)
            {
                searchFrom = end; // too short: try to extend it to the next boundary
                continue;
            }

            Emit(piece, sentences);
            _pending.Remove(0, end);
            searchFrom = 0;
        }
        return sentences;
    }

    public IEnumerable<string> Flush()
    {
        var sentences = new List<string>();
        Emit(_pending.ToString(), sentences);
        _pending.Clear();
        return sentences;
    }

    private static void Emit(string piece, List<string> into)
    {
        var clean = ClaudeService.StripPointTags(piece).Trim();
        if (clean.Length > 0) into.Add(clean);
    }

    /// <summary>Index just past a sentence end (". ", "? ", "! ", "… " or a newline), or -1.</summary>
    private static int FindEnd(string text, int from)
    {
        for (int i = from; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') return i + 1;
            if (c is '.' or '!' or '?' or '…' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
                return i + 1;
        }
        return -1;
    }

    private static bool OpenTag(string text)
    {
        int open = text.LastIndexOf('[');
        return open >= 0 && text.IndexOf(']', open) < 0;
    }
}
