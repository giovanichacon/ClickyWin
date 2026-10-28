using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

/// <summary>
/// Opt-in (KeepRecordings): each dictation's audio is saved with what live speech-to-text heard.
/// The report re-transcribes them with AssemblyAI's slower pre-recorded model (a second opinion, not ground truth)
/// and shows word-by-word where the two differ. Files stay in %APPDATA%\ClickyWindows\recordings.
/// </summary>
public static class AccuracyReport
{
    public static string Folder { get; } = Path.Combine(AppSettings.Folder, "recordings");
    private const int Keep = 100;
    private const string Api = "https://api.assemblyai.com/v2";

    /// <summary>Saves PCM16 16 kHz mono audio as a WAV plus a .heard.txt; keeps the newest <see cref="Keep"/>.</summary>
    public static void Save(byte[] pcm, string heard)
    {
        if (pcm.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var name = Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            using (var f = File.Create(name + ".wav"))
            using (var w = new BinaryWriter(f))
            {
                w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVEfmt "u8);
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000);
                w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
            }
            File.WriteAllText(name + ".heard.txt", heard);
            foreach (var old in Directory.GetFiles(Folder, "*.wav").OrderByDescending(p => p).Skip(Keep))
                foreach (var file in Directory.GetFiles(Folder, Path.GetFileNameWithoutExtension(old) + ".*"))
                    File.Delete(file);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Recordings] Save failed: {ex.Message}");
        }
    }

    /// <summary>Re-transcribes recordings that have no reference yet, writes report.html and returns its path.</summary>
    public static async Task<string> RunAsync(string apiKey, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var wavs = Directory.GetFiles(Folder, "*.wav").OrderBy(p => p).ToList();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("authorization", apiKey);

        var todo = wavs.Where(w => !File.Exists(Path.ChangeExtension(w, ".ref.txt"))).ToList();
        for (int i = 0; i < todo.Count; i++)
        {
            progress.Report($"{i + 1}/{todo.Count}");
            File.WriteAllText(Path.ChangeExtension(todo[i], ".ref.txt"), await TranscribeAsync(http, todo[i], ct));
        }

        var path = Path.Combine(Folder, "report.html");
        File.WriteAllText(path, BuildHtml(wavs));
        return path;
    }

    private static async Task<string> TranscribeAsync(HttpClient http, string wav, CancellationToken ct)
    {
        using var upload = await http.PostAsync($"{Api}/upload", new ByteArrayContent(await File.ReadAllBytesAsync(wav, ct)), ct);
        upload.EnsureSuccessStatusCode();
        var url = JsonDocument.Parse(await upload.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("upload_url").GetString();

        var body = JsonSerializer.Serialize(new { audio_url = url, language_detection = true });
        using var created = await http.PostAsync($"{Api}/transcript", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("id").GetString();

        while (true)
        {
            await Task.Delay(1500, ct);
            var doc = JsonDocument.Parse(await http.GetStringAsync($"{Api}/transcript/{id}", ct)).RootElement;
            switch (doc.GetProperty("status").GetString())
            {
                case "completed": return doc.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                case "error": throw new InvalidOperationException(doc.GetProperty("error").GetString());
            }
        }
    }

    internal static string[] Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value).ToArray();

    /// <summary>Word alignment (Levenshtein backtrace): (heard, reference) pairs, null on the missing side.</summary>
    internal static List<(string? heard, string? reference)> Align(string[] h, string[] r)
    {
        var d = new int[h.Length + 1, r.Length + 1];
        for (int i = 0; i <= h.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= r.Length; j++) d[0, j] = j;
        for (int i = 1; i <= h.Length; i++)
            for (int j = 1; j <= r.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (h[i - 1] == r[j - 1] ? 0 : 1));

        var ops = new List<(string?, string?)>();
        for (int i = h.Length, j = r.Length; i > 0 || j > 0;)
        {
            if (i > 0 && j > 0 && d[i, j] == d[i - 1, j - 1] + (h[i - 1] == r[j - 1] ? 0 : 1)) { ops.Add((h[--i], r[--j])); }
            else if (j > 0 && d[i, j] == d[i, j - 1] + 1) ops.Add((null, r[--j]));
            else ops.Add((h[--i], null));
        }
        ops.Reverse();
        return ops;
    }

    private static string BuildHtml(List<string> wavs)
    {
        var rows = new StringBuilder();
        var pairs = new Dictionary<string, int>();
        int errors = 0, refWords = 0;
        foreach (var wav in wavs.AsEnumerable().Reverse())
        {
            var refPath = Path.ChangeExtension(wav, ".ref.txt");
            if (!File.Exists(refPath)) continue;
            var heard = File.ReadAllText(Path.ChangeExtension(wav, ".heard.txt"));
            var reference = File.ReadAllText(refPath);
            var ops = Align(Words(heard), Words(reference));
            int rowErrors = ops.Count(o => o.heard != o.reference);
            int rowRef = ops.Count(o => o.reference != null);
            errors += rowErrors;
            refWords += rowRef;

            var diff = new StringBuilder();
            foreach (var (h, r) in ops)
            {
                if (h == r) { diff.Append(WebUtility.HtmlEncode(h)).Append(' '); continue; }
                if (h != null) diff.Append("<s>").Append(WebUtility.HtmlEncode(h)).Append("</s> ");
                if (r != null) diff.Append("<b>").Append(WebUtility.HtmlEncode(r)).Append("</b> ");
                var key = $"{h ?? "∅"} → {r ?? "∅"}";
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
            }
            var name = Path.GetFileName(wav);
            rows.Append($"<tr><td>{name[..15]}<br><audio controls preload=none src=\"{name}\"></audio></td>")
                .Append($"<td>{diff}</td><td>{(rowRef == 0 ? "–" : $"{100.0 * rowErrors / rowRef:F0}%")}</td></tr>\n");
        }

        var top = string.Join("", pairs.OrderByDescending(p => p.Value).Take(30)
            .Select(p => $"<li>{WebUtility.HtmlEncode(p.Key)} ×{p.Value}</li>"));
        var wer = refWords == 0 ? "–" : $"{100.0 * errors / refWords:F1}%";
        return $$"""
            <!doctype html><meta charset="utf-8"><title>Clicky dictation accuracy</title>
            <style>body{font:14px system-ui;margin:24px;background:#1c1c1e;color:#eee}td{padding:8px;border-top:1px solid #333;vertical-align:top}
            s{color:#ff6b6b}b{color:#6bdc8b;font-weight:600}</style>
            <h1>Dictation accuracy</h1>
            <p>Word difference vs the slower pre-recorded model: {{wer}} ({{errors}} of {{refWords}} words).
            <s>red</s> = what live dictation heard, <b>green</b> = what the second pass heard. Play the clip to see who is right.</p>
            <h2>Most common differences</h2><ul>{{top}}</ul>
            <table>{{rows}}</table>
            """;
    }

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
