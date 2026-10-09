using System.Text.Json;
using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

public sealed class JsonSettingsStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(Path)) return new();
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonFile.Options) ?? new(); }
        catch (JsonException) { UnreadableFile.Keep(Path); return new(); }
    }

    public void Save(AppSettings settings) => JsonFile.WriteAtomic(Path, settings);
}
