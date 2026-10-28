using ClickyWindows.Helpers;
using NAudio.CoreAudioApi;

namespace ClickyWindows.Services;

/// <summary>
/// Hands-free session: turns other apps (music, videos) down so the mic hears the user over them,
/// and puts them back afterwards. Uses each app's own volume (the Volume Mixer), never the master volume.
/// shortcut: only the default output device; if the user switches devices mid-session the old one isn't restored.
/// </summary>
public static class AudioDucker
{
    private const float Level = 0.2f;
    private static readonly Dictionary<string, float> Saved = []; // audio session instance id → volume before ducking
    private static readonly object Gate = new();

    // Saved volumes also live on disk, so a crash mid-session can't leave other apps at 20%: the next start restores them
    private static readonly string SavedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClickyWindows", "ducked.json");

    /// <summary>Lowers every other app. Safe to call repeatedly: apps that started playing since are lowered too.</summary>
    public static void Duck()
    {
        ForEachSession((id, volume) =>
        {
            if (Saved.ContainsKey(id)) return;
            Saved[id] = volume.Volume;
            volume.Volume *= Level;
        });
        lock (Gate)
        {
            try { File.WriteAllText(SavedPath, System.Text.Json.JsonSerializer.Serialize(Saved)); }
            catch (Exception ex) { Logger.Log($"[Duck] Could not save volumes: {ex.Message}"); }
        }
    }

    /// <summary>Puts other apps back. Also called at startup: restores what a crashed run left lowered.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (Saved.Count == 0 && File.Exists(SavedPath))
            {
                try
                {
                    foreach (var (id, v) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, float>>(
                                 File.ReadAllText(SavedPath)) ?? [])
                        Saved[id] = v;
                    Logger.Log($"[Duck] Restoring {Saved.Count} app volume(s) left lowered by the last run");
                }
                catch (Exception ex) { Logger.Log($"[Duck] Could not read saved volumes: {ex.Message}"); }
            }
            if (Saved.Count == 0) return;
        }
        ForEachSession((id, volume) =>
        {
            if (Saved.TryGetValue(id, out var original)) volume.Volume = original;
        });
        lock (Gate)
        {
            Saved.Clear();
            try { File.Delete(SavedPath); } catch (IOException) { }
        }
    }

    private static void ForEachSession(Action<string, SimpleAudioVolume> action)
    {
        lock (Gate)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = device.AudioSessionManager.Sessions;
                uint self = (uint)Environment.ProcessId;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.GetProcessID == self || session.IsSystemSoundsSession) continue; // keep Clicky's voice
                    action(session.GetSessionInstanceIdentifier, session.SimpleAudioVolume);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Duck] {ex.Message}");
            }
        }
    }
}
