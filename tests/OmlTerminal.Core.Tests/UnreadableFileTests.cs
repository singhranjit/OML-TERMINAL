using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.Core.Tests;

public class UnreadableFileTests
{
    [Fact]
    public void A_damaged_sessions_file_is_kept_before_the_next_save_overwrites_it()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "sessions.json");
        File.WriteAllText(path, "[{\"Name\":\"CORE-SW1\",\"Host\":\"10.0.0.1\"");   // truncated mid-write
        var store = new JsonSessionStore(path);
        Assert.Empty(store.Load());
        var kept = Assert.Single(Directory.GetFiles(dir, "sessions.json.unreadable-*"));
        Assert.Contains("CORE-SW1", File.ReadAllText(kept));

        store.Save([new SessionProfile { Name = "new", Host = "10.0.0.2" }]);
        Assert.Contains("CORE-SW1", File.ReadAllText(kept)); // the damaged original survives the save

        store.Load();                                             // loading the good file adds nothing
        Assert.Single(Directory.GetFiles(dir, "sessions.json.unreadable-*"));
    }

    [Fact]
    public void Starting_twice_on_the_same_damaged_file_keeps_one_copy()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        new JsonSettingsStore(path).Load();
        new JsonSettingsStore(path).Load();
        Assert.Single(Directory.GetFiles(dir, "settings.json.unreadable-*"));
    }

    [Fact]
    public void A_damaged_vault_is_kept()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "credentials.json");
        File.WriteAllText(path, "[{\"Name\":");
        Assert.Empty(new CredentialStore(path).Load());
        Assert.Single(Directory.GetFiles(dir, "credentials.json.unreadable-*"));
    }
}
