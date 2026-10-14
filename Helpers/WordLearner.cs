using System.Text.RegularExpressions;

namespace ClickyWindows.Helpers;

/// <summary>
/// Finds words the user fixed right after dictating: a dictated word that disappeared and a
/// similar new word that appeared ("Jovani" → "Giovani", "github" → "GitHub"). Big rewrites
/// are ignored — only a few small fixes count as corrections.
/// </summary>
public static partial class WordLearner
{
    private const int MaxCorrections = 3;
    private const double MinSimilarity = 0.5;

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}'’\-]*")]
    private static partial Regex WordRegex();

    public static List<string> FindCorrections(string inserted, string finalText)
    {
        var dictated = Words(inserted);
        var final = Words(finalText);
        if (dictated.Count == 0 || final.Count == 0) return [];

        var finalSet = new HashSet<string>(final, StringComparer.Ordinal);
        var dictatedSet = new HashSet<string>(dictated, StringComparer.Ordinal);

        // Most of the dictation must still be there, or the box was cleared / rewritten / is another text
        int kept = dictated.Count(finalSet.Contains);
        if (kept < dictated.Count / 2.0) return [];

        var missing = dictated.Where(w => !finalSet.Contains(w)).Distinct().ToList();
        if (missing.Count == 0 || missing.Count > MaxCorrections) return [];

        var candidates = final.Where(w => !dictatedSet.Contains(w) && w.Length >= 3).Distinct().ToList();
        var learned = new List<string>();
        foreach (var gone in missing)
        {
            var best = candidates
                .Select(c => (word: c, score: Similarity(gone, c)))
                .Where(x => x.score >= MinSimilarity)
                .OrderByDescending(x => x.score)
                .FirstOrDefault();
            if (best.word != null && !learned.Contains(best.word)) learned.Add(best.word);
        }
        return learned;
    }

    private static List<string> Words(string text) =>
        WordRegex().Matches(text).Select(m => m.Value).ToList();

    /// <summary>1 − Levenshtein distance / longer length, ignoring case.</summary>
    public static double Similarity(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}
