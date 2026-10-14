using System.Globalization;
using System.Text.RegularExpressions;

namespace ClickyWindows.Helpers;

/// <summary>
/// Spanish/English UI text. "Auto" follows the language you last spoke to Clicky,
/// falling back to the Windows display language.
/// </summary>
public static partial class L
{
    /// <summary>"Auto", "es" or "en" — set from settings at startup and when saved.</summary>
    public static string Setting { get; set; } = "Auto";

    private static volatile string? _lastSpoken; // "es" / "en" from the last transcript

    public static bool Es => Setting switch
    {
        "es" => true,
        "en" => false,
        _ => (_lastSpoken ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "es",
    };

    /// <summary>Records the language of what the user just said (for "Auto").</summary>
    public static void NoteSpoken(string text) => _lastSpoken = LooksSpanish(text) ? "es" : "en";

    // Accents/ñ/¿¡ or common Spanish function words outnumbering common English ones
    [GeneratedRegex(@"[áéíóúñ¿¡]", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishChars();
    [GeneratedRegex(@"\b(el|la|los|las|de|del|que|es|en|un|una|por|para|con|como|qué|cómo|dónde|esto|este|esta|y|no|se|mi|tu)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpanishWords();
    [GeneratedRegex(@"\b(the|a|an|of|to|is|in|it|and|what|how|where|this|that|you|my|your|on|for|with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishWords();

    public static bool LooksSpanish(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Es;
        int es = SpanishWords().Count(text) + 2 * SpanishChars().Count(text);
        int en = EnglishWords().Count(text);
        return es > en;
    }

    private static string T(string es, string en) => Es ? es : en;

    // ── Overlay feedback ────────────────────────────────────────────────────
    public static string NoMic => T("No oigo tu micrófono", "I can't hear your microphone");
    public static string NoConnect => T("No pude conectarme", "I couldn't connect");
    public static string NotHeard => T("No te escuché bien", "Sorry, I didn't catch that");
    public static string NoAnswer => T("No pude responder", "Sorry, I couldn't answer");
    public static string Refused => T("No puedo ayudar con eso", "I can't help with that");
    public static string NeedKeys => T("Falta configurar tus claves API", "Add your API keys in Settings");
    public static string HandsFree => T("Manos libres: toca el atajo para terminar", "Hands-free: tap the shortcut to finish");
    public static string CopiedPaste => T("Copiado: pulsa Ctrl+V para pegar", "Copied: press Ctrl+V to paste");
    public static string LearnedWord(string word) => T($"Aprendí «{word}»", $"Learned \"{word}\"");
    public static string TutorWatching => T("Te sigo guiando…", "I'll keep guiding you…");
    public static string NoWriteText => T("No supe qué escribir", "I couldn't work out what to write");
    public static string[] Utterances => Es
        ? ["¡aquí!", "¡este!", "¡por aquí!", "¡clic aquí!", "¡aquí está!", "¡lo encontré!"]
        : ["right here!", "this one!", "over here!", "click this!", "here it is!", "found it!"];

    // ── Tray ────────────────────────────────────────────────────────────────
    public static string Ready(string hotkey) => T($"¡Clicky listo! Mantén {hotkey} para hablar.", $"Clicky ready! Hold {hotkey} to talk.");
    public static string HoldToTalk(string hotkey) => T($"Mantén {hotkey} para hablar", $"Hold {hotkey} to talk");
    public static string PanelReady => T("Listo", "Ready");
    public static string PanelListening => T("Escuchando…", "Listening…");
    public static string PanelThinking => T("Pensando…", "Thinking…");
    public static string PanelSpeaking => T("Hablando…", "Speaking…");
    public static string PanelLastAnswer => T("Última respuesta", "Last answer");
    public static string PanelNoAnswer => T("Aún no hay respuestas. Mantén el atajo y pregunta algo.", "No answers yet. Hold the shortcut and ask something.");
    public static string PanelDictations => T("Dictados recientes (clic para copiar)", "Recent dictations (click to copy)");
    public static string PanelCopy => T("Copiar", "Copy");
    public static string PanelCopied => T("Copiado ✓", "Copied ✓");
    public static string PanelShortcuts(string ask, string dictate, string write) => T(
        $"Preguntar: {ask}   ·   Dictar: {dictate}   ·   Escribir: {write}",
        $"Ask: {ask}   ·   Dictate: {dictate}   ·   Write: {write}");
    public static string MenuSettings => T("Configuración…", "Settings…");
    public static string MenuLog => T("Ver registro", "View log");
    public static string MenuFolder => T("Abrir carpeta de datos", "Open data folder");
    public static string MenuQuit => T("Salir de Clicky", "Quit Clicky");
    public static string AlreadyRunning => T("Clicky ya está abierto. Búscalo en la bandeja del sistema.", "Clicky is already running. Check the system tray.");

    // ── Setup window ────────────────────────────────────────────────────────
    public static string SetupTitle => T("Clicky — Configuración", "Clicky — Setup");
    public static string SetupIntro => T(
        "Tus claves se guardan cifradas con tu cuenta de Windows.",
        "Your keys are stored encrypted with your Windows account.");
    public static string KeyClaude => T("Clave API de Claude (Anthropic)", "Claude (Anthropic) API key");
    public static string KeyAssembly => T("Clave API de AssemblyAI (voz a texto)", "AssemblyAI API key (speech-to-text)");
    public static string KeyEleven => T("Clave API de ElevenLabs (opcional)", "ElevenLabs API key (optional)");
    public static string Saved => T("guardada", "saved");
    public static string NotSet => T("sin configurar", "not set");
    public static string Test => T("Probar", "Test");
    public static string Testing => T("Probando…", "Testing…");
    public static string TestOk => T("✓ funciona", "✓ works");
    public static string TestFail(string why) => T($"✗ {why}", $"✗ {why}");
    public static string VoiceLabel => T("Voz", "Voice");
    public static string VoiceAuto => T("Automática (ElevenLabs si hay clave, si no Windows)", "Automatic (ElevenLabs if a key is set, else Windows)");
    public static string VoiceEleven => "ElevenLabs";
    public static string VoiceWindows => T("Windows (gratis, sin conexión)", "Windows (free, offline)");
    public static string VoiceNameLabel => T("Voz elegida", "Voice name");
    public static string VoiceByLanguage => T("Automática según el idioma", "Automatic by language");
    public static string Preview => T("Escuchar", "Preview");
    public static string LoadingVoices => T("Cargando voces…", "Loading voices…");
    public static string VoicesFailed(string why) => T($"No se pudieron cargar las voces: {why}", $"Could not load voices: {why}");
    public static string ElevenFreeNote => T(
        "Plan gratis: solo voces predefinidas o tuyas por API.",
        "Free plan: only premade or your own voices work through the API.");
    public static string PreviewText => T("Hola, soy Clicky. Así sueno.", "Hi, I'm Clicky. This is how I sound.");
    public static string MicLabel => T("Micrófono", "Microphone");
    public static string MicDefault => T("Predeterminado de Windows", "Windows default");
    public static string MicHint => T("Habla: la barra debe moverse.", "Speak: the bar should move.");
    public static string TabKeys => T("Claves", "Keys");
    public static string TabVoice => T("Voz", "Voice");
    public static string TabShortcuts => T("Atajos", "Shortcuts");
    public static string TabDictation => T("Dictado", "Dictation");
    public static string TabGeneral => T("General", "General");
    public static string HotkeyAsk => T("Preguntar a Clicky (mantener)", "Ask Clicky (hold)");
    public static string HotkeyDictate => T("Dictar en el cuadro de texto (mantener)", "Dictate into the text box (hold)");
    public static string HotkeyWrite => T("Escribir con la pantalla como contexto (mantener)", "Write using the screen as context (hold)");
    public static string HotkeyHelp => T(
        "Mantén el atajo y habla; suelta para terminar. Dictar y Escribir: doble toque = manos libres (toca otra vez para terminar). Esc cancela. Se aceptan solo modificadores, p. ej. Ctrl+Win.",
        "Hold the shortcut and talk; release to finish. Dictate and Write: double-tap = hands-free (tap again to finish). Esc cancels. Modifier-only shortcuts like Ctrl+Win work.");
    public static string HotkeyChange => T("Cambiar", "Change");
    public static string HotkeyPress => T("Pulsa la combinación… (Esc cancela)", "Press the combination… (Esc cancels)");
    public static string HotkeyInvalid => T("Usa Ctrl, Alt o Win con otra tecla, o dos modificadores (Ctrl+Win).", "Use Ctrl, Alt or Win with another key, or two modifiers (Ctrl+Win).");
    public static string HotkeyDuplicate(string combo) => T($"{combo} ya está asignado a otra acción.", $"{combo} is already assigned to another action.");
    public static string HotkeyDefault => T("Restablecer", "Reset");
    public static string HotkeyOff => T("Desactivar", "Turn off");
    public static string Off => T("Desactivado", "Off");
    public static string CleanupLabel => T("Limpiar el dictado con IA (puntuación, muletillas, autocorrecciones)", "Clean up dictation with AI (punctuation, fillers, self-corrections)");
    public static string LearnLabel => T("Aprender palabras que corrijo justo después de dictar", "Learn words I correct right after dictating");
    public static string DictionaryLabel => T("Diccionario personal (una palabra o nombre por línea)", "Personal dictionary (one word or name per line)");
    public static string DictionaryHint => T("Se usan para reconocer y escribir bien nombres, marcas y términos.", "Used to recognize and spell names, brands and terms correctly.");
    public static string VoiceSpeedLabel => T("Velocidad de la voz", "Voice speed");
    public static string CopyAnswersLabel => T("Copiar cada respuesta al portapapeles", "Copy each answer to the clipboard");
    public static string TutorLabel => T("Modo tutor: seguir mirando la pantalla y guiarte paso a paso", "Tutor mode: keep watching the screen and guide you step by step");
    public static string AutostartLabel => T("Iniciar Clicky con Windows", "Start Clicky with Windows");
    public static string AlwaysShowCursor => T(
        "Mostrar siempre el cursor azul (si no, solo mientras usas Clicky)",
        "Always show the blue cursor (otherwise only while Clicky is active)");
    public static string ModelLabel => T("Modelo diario", "Everyday model");
    public static string EscalationLabel => T("Preguntas difíciles", "Hard questions");
    public static string EscalationOff => T("Desactivado", "Off");
    public static string LanguageLabel => T("Idioma", "Language");
    public static string LanguageAuto => T("Automático", "Automatic");
    public static string Save => T("Guardar", "Save");
    public static string Cancel => T("Cancelar", "Cancel");
    public static string SaveFailed(string why) => T($"No se pudo guardar: {why}", $"Could not save: {why}");
}
