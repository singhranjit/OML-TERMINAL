namespace OmlTerminal.Core.Voice;

/// <summary>A downloadable speech model. Multilingual ones understand ~100 languages and any accent.</summary>
public sealed record WhisperModel(string Id, string Title, string FileName, long Bytes, bool Multilingual, bool Large, string Description)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";

    public static readonly WhisperModel Turbo = new("turbo", "Best - Whisper large-v3 turbo", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, true, true,
        "Most accurate in every language and accent. 574 MB; needs a reasonably modern CPU.");

    public static readonly WhisperModel Small = new("small", "Fast - Whisper small", "ggml-small.bin", 487_601_967, true, false,
        "Good in common languages, about twice as fast. 488 MB.");

    public static readonly WhisperModel BaseEnglish = new("base.en", "English only - Whisper base.en", "ggml-base.en.bin", 147_964_211, false, false,
        "The original English-only model. 148 MB.");

    /// <summary>In order of preference.</summary>
    public static readonly IReadOnlyList<WhisperModel> All = [Turbo, Small, BaseEnglish];
}

/// <summary>A recognized utterance and the language it was spoken in.</summary>
public sealed record Transcript(string Text, string LanguageCode, string LanguageName);

/// <summary>Every language Whisper recognizes, by its code - for the language picker and for telling the
/// assistant which language to answer in.</summary>
public static class WhisperLanguages
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["en"] = "English", ["zh"] = "Chinese", ["de"] = "German", ["es"] = "Spanish", ["ru"] = "Russian", ["ko"] = "Korean",
        ["fr"] = "French", ["ja"] = "Japanese", ["pt"] = "Portuguese", ["tr"] = "Turkish", ["pl"] = "Polish", ["ca"] = "Catalan",
        ["nl"] = "Dutch", ["ar"] = "Arabic", ["sv"] = "Swedish", ["it"] = "Italian", ["id"] = "Indonesian", ["hi"] = "Hindi",
        ["fi"] = "Finnish", ["vi"] = "Vietnamese", ["he"] = "Hebrew", ["uk"] = "Ukrainian", ["el"] = "Greek", ["ms"] = "Malay",
        ["cs"] = "Czech", ["ro"] = "Romanian", ["da"] = "Danish", ["hu"] = "Hungarian", ["ta"] = "Tamil", ["no"] = "Norwegian",
        ["th"] = "Thai", ["ur"] = "Urdu", ["hr"] = "Croatian", ["bg"] = "Bulgarian", ["lt"] = "Lithuanian", ["la"] = "Latin",
        ["mi"] = "Maori", ["ml"] = "Malayalam", ["cy"] = "Welsh", ["sk"] = "Slovak", ["te"] = "Telugu", ["fa"] = "Persian",
        ["lv"] = "Latvian", ["bn"] = "Bengali", ["sr"] = "Serbian", ["az"] = "Azerbaijani", ["sl"] = "Slovenian", ["kn"] = "Kannada",
        ["et"] = "Estonian", ["mk"] = "Macedonian", ["br"] = "Breton", ["eu"] = "Basque", ["is"] = "Icelandic", ["hy"] = "Armenian",
        ["ne"] = "Nepali", ["mn"] = "Mongolian", ["bs"] = "Bosnian", ["kk"] = "Kazakh", ["sq"] = "Albanian", ["sw"] = "Swahili",
        ["gl"] = "Galician", ["mr"] = "Marathi", ["pa"] = "Punjabi", ["si"] = "Sinhala", ["km"] = "Khmer", ["sn"] = "Shona",
        ["yo"] = "Yoruba", ["so"] = "Somali", ["af"] = "Afrikaans", ["oc"] = "Occitan", ["ka"] = "Georgian", ["be"] = "Belarusian",
        ["tg"] = "Tajik", ["sd"] = "Sindhi", ["gu"] = "Gujarati", ["am"] = "Amharic", ["yi"] = "Yiddish", ["lo"] = "Lao",
        ["uz"] = "Uzbek", ["fo"] = "Faroese", ["ht"] = "Haitian Creole", ["ps"] = "Pashto", ["tk"] = "Turkmen", ["nn"] = "Nynorsk",
        ["mt"] = "Maltese", ["sa"] = "Sanskrit", ["lb"] = "Luxembourgish", ["my"] = "Myanmar", ["bo"] = "Tibetan", ["tl"] = "Tagalog",
        ["mg"] = "Malagasy", ["as"] = "Assamese", ["tt"] = "Tatar", ["haw"] = "Hawaiian", ["ln"] = "Lingala", ["ha"] = "Hausa",
        ["ba"] = "Bashkir", ["jw"] = "Javanese", ["su"] = "Sundanese", ["yue"] = "Cantonese",
    };
}
