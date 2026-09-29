using System.Collections.ObjectModel;
using OmlTerminal.Core.Models;
using OmlTerminal.Core.Persistence;

namespace OmlTerminal.App.ViewModels;

/// <summary>One folder in the sidebar tree. "/" in a SessionProfile.Folder or AppSettings.Folders entry is a
/// path separator, so "Home/Movies/Action" becomes three nested SessionTreeFolder levels, MobaXterm/SecureCRT-style.</summary>
public sealed class SessionTreeFolder
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public List<SessionTreeFolder> Subfolders { get; } = new();
    public List<SessionProfile> Sessions { get; } = new();

    /// <summary>Sessions in this folder plus every descendant subfolder - what "delete this folder" would affect.</summary>
    public int TotalCount => Sessions.Count + Subfolders.Sum(f => f.TotalCount);
    public string HeaderText => $"{Name} ({TotalCount})";
}


/// <summary>One row in the Ctrl+K command palette - either a jump-to-session entry or an app action.</summary>
public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public required string Badge { get; init; }
    public required Action Execute { get; init; }
}

/// <summary>Thin holder over the Core stores. Validation lives in JsonSessionStore, not here.</summary>
public sealed class MainViewModel
{
    public const double MinFont = 8, MaxFont = 40, DefaultFont = 14;

    private readonly JsonSessionStore _sessionStore = new();
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly CredentialStore _credentialStore = new();

    public ObservableCollection<SessionProfile> Sessions { get; } = new();

    /// <summary>The password manager's vault. Empty until unlocked when a master password is set.</summary>
    public ObservableCollection<Credential> Credentials { get; } = new();
    public AppSettings Settings { get; }

    /// <summary>True while a master password is set but has not been entered yet. Sessions stay empty and read-only until unlocked.</summary>
    public bool IsLocked { get; private set; }

    public MainViewModel()
    {
        Settings = _settingsStore.Load();
        if (Settings.HasMasterPassword) IsLocked = true;
        else LoadSessions();
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var s in _sessionStore.Load()) Sessions.Add(s);
        Credentials.Clear();
        foreach (var c in _credentialStore.Load().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Credentials.Add(c);
    }

    // ---------- password manager ----------

    /// <summary>The profile to actually connect with: its linked vault credential's username/passwords filled in.</summary>
    public SessionProfile Resolve(SessionProfile p) => Core.Models.Credentials.Resolve(p, Credentials);

    public void SaveCredential(Credential c)
    {
        EnsureUnlocked();
        c.UpdatedUtc = DateTime.UtcNow;
        var list = Credentials.Where(x => x.Id != c.Id).Append(c).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _credentialStore.Save(list);
        Credentials.Clear();
        foreach (var x in list) Credentials.Add(x);
    }

    /// <summary>Deletes a credential. Sessions that used it get its username/password copied back onto themselves,
    /// so they keep working instead of silently losing their login.</summary>
    public void DeleteCredential(Credential c)
    {
        EnsureUnlocked();
        var users = Sessions.Where(s => s.CredentialId == c.Id).ToList();
        if (users.Count > 0)
        {
            var updated = Sessions.Select(s =>
            {
                if (s.CredentialId != c.Id) return s;
                var copy = Resolve(s);
                copy.CredentialId = null;
                return copy;
            }).ToList();
            _sessionStore.Save(updated);
            Sessions.Clear();
            foreach (var s in updated) Sessions.Add(s);
        }
        var remaining = Credentials.Where(x => x.Id != c.Id).ToList();
        _credentialStore.Save(remaining);
        Credentials.Remove(c);
    }

    /// <summary>Links (or, with null, unlinks) a credential on many sessions in one save.</summary>
    public void AssignCredential(Guid? credentialId, IEnumerable<SessionProfile> targets)
    {
        EnsureUnlocked();
        var ids = targets.Select(t => t.Id).ToHashSet();
        var updated = Sessions.Select(s =>
        {
            if (!ids.Contains(s.Id) || s.CredentialId == credentialId) return s;
            var copy = s.Clone();
            copy.CredentialId = credentialId;
            return copy;
        }).ToList();
        _sessionStore.Save(updated);
        Sessions.Clear();
        foreach (var s in updated) Sessions.Add(s);
    }

    public int SessionsUsing(Guid credentialId) => Sessions.Count(s => s.CredentialId == credentialId);

    public bool TryUnlock(string masterPassword)
    {
        if (!IsLocked) return true;
        if (string.IsNullOrEmpty(masterPassword)) return false;
        var protector = SecretProtector.FromPassword(masterPassword, Convert.FromBase64String(Settings.MasterPasswordSalt!));
        if (!protector.Verify(Settings.MasterPasswordVerifier!)) return false;
        _sessionStore.Protector = protector;
        _credentialStore.Protector = protector;
        LoadSessions();
        IsLocked = false;
        return true;
    }

    /// <summary>Enables or changes the master password and re-saves every session encrypted. Settings are written first so the key is never lost.</summary>
    public void SetMasterPassword(string masterPassword)
    {
        EnsureUnlocked();
        var salt = SecretProtector.NewSalt();
        var protector = SecretProtector.FromPassword(masterPassword, salt);
        Settings.MasterPasswordSalt = Convert.ToBase64String(salt);
        Settings.MasterPasswordVerifier = protector.CreateVerifier();
        SaveSettings();
        _sessionStore.Protector = protector;
        _sessionStore.Save(Sessions);
        _credentialStore.Protector = protector;
        _credentialStore.Save(Credentials); // vault moves from DPAPI to the master password
    }

    /// <summary>Writes sessions back in plain text first, then forgets the master password.</summary>
    public void RemoveMasterPassword()
    {
        EnsureUnlocked();
        _sessionStore.Protector = null;
        _sessionStore.Save(Sessions);
        _credentialStore.Protector = null;
        _credentialStore.Save(Credentials); // vault falls back to Windows DPAPI, never plain text
        Settings.MasterPasswordSalt = null;
        Settings.MasterPasswordVerifier = null;
        SaveSettings();
    }

    private void EnsureUnlocked()
    {
        if (IsLocked) throw new InvalidOperationException("Unlock with the master password first.");
    }

    /// <summary>Throws SessionValidationException (before mutating anything) if the profile is invalid.</summary>
    public void Add(SessionProfile p)
    {
        EnsureUnlocked();
        _sessionStore.Save(Sessions.Append(p));
        Sessions.Add(p);
    }

    public void Replace(SessionProfile old, SessionProfile updated)
    {
        EnsureUnlocked();
        int i = Sessions.IndexOf(old);
        if (i < 0) { Add(updated); return; }
        var list = Sessions.ToList();
        list[i] = updated;
        _sessionStore.Save(list);
        Sessions[i] = updated;
    }

    public void Remove(SessionProfile p)
    {
        EnsureUnlocked();
        if (!Sessions.Contains(p)) return;
        _sessionStore.Save(Sessions.Where(s => s != p));
        Sessions.Remove(p);
    }

    /// <summary>Builds the sidebar's nested folder tree from every session's Folder path plus any explicitly
    /// created (possibly still-empty) folders. Returns root-level folders and top-level unfiled sessions
    /// separately, since unfiled sessions render as plain rows with no synthetic "(No folder)" tree node.</summary>
    public (IReadOnlyList<SessionTreeFolder> Folders, IReadOnlyList<SessionProfile> Unfiled) BuildTree(string? filter)
    {
        var q = Sessions.AsEnumerable();
        bool filtering = !string.IsNullOrWhiteSpace(filter);
        if (filtering)
        {
            var f = filter!.Trim();
            q = q.Where(s => s.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || s.Host.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || s.Folder.Contains(f, StringComparison.OrdinalIgnoreCase)
                          || s.Tags.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        var root = new SessionTreeFolder { Name = "", FullPath = "" };

        SessionTreeFolder Resolve(string folderPath)
        {
            var node = root;
            var pathSoFar = "";
            foreach (var segment in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                pathSoFar = pathSoFar.Length == 0 ? segment : $"{pathSoFar}/{segment}";
                var existing = node.Subfolders.FirstOrDefault(f => string.Equals(f.Name, segment, StringComparison.OrdinalIgnoreCase));
                node = existing ?? Add(node, segment, pathSoFar);
            }
            return node;

            static SessionTreeFolder Add(SessionTreeFolder parent, string name, string fullPath)
            {
                var created = new SessionTreeFolder { Name = name, FullPath = fullPath };
                parent.Subfolders.Add(created);
                return created;
            }
        }

        var unfiled = new List<SessionProfile>();
        foreach (var s in q.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(s.Folder)) unfiled.Add(s);
            else Resolve(s.Folder).Sessions.Add(s);
        }

        // Explicitly-created folders with no sessions (yet) only make sense to show when not filtering the list.
        if (!filtering)
            foreach (var f in Settings.Folders) Resolve(f);

        SortRecursive(root);
        return (root.Subfolders, unfiled);

        static void SortRecursive(SessionTreeFolder node)
        {
            node.Subfolders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var sub in node.Subfolders) SortRecursive(sub);
        }
    }

    /// <summary>All known folder names: explicitly created ones plus any only referenced by a session's Folder field.</summary>
    public IReadOnlyList<string> AllFolders() =>
        Settings.Folders.Concat(Sessions.Select(s => s.Folder))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Creates an empty folder (a no-op if it, or a session already filed under it, exists). "/" nests subfolders.</summary>
    public void CreateFolder(string path)
    {
        EnsureUnlocked();
        path = path.Trim().Trim('/');
        if (string.IsNullOrEmpty(path)) return;
        if (!Settings.Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Settings.Folders.Add(path);
            SaveSettings();
        }
    }

    /// <summary>Renames a folder and every subfolder/session filed under it (matched by path prefix).</summary>
    public void RenameFolder(string oldPath, string newPath)
    {
        EnsureUnlocked();
        oldPath = oldPath.Trim().Trim('/');
        newPath = newPath.Trim().Trim('/');
        if (string.IsNullOrEmpty(newPath) || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;

        bool sessionsChanged = false;
        for (int i = 0; i < Sessions.Count; i++)
        {
            var renamed = RenamedFolder(Sessions[i].Folder, oldPath, newPath);
            if (renamed is null) continue;
            var clone = Sessions[i].Clone();
            clone.Folder = renamed;
            Sessions[i] = clone;
            sessionsChanged = true;
        }
        if (sessionsChanged) _sessionStore.Save(Sessions);

        Settings.Folders = Settings.Folders.Select(f => RenamedFolder(f, oldPath, newPath) ?? f)
            .Append(newPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveSettings();
    }

    private static string? RenamedFolder(string folder, string oldPath, string newPath)
    {
        if (string.Equals(folder, oldPath, StringComparison.OrdinalIgnoreCase)) return newPath;
        if (folder.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase)) return newPath + folder[oldPath.Length..];
        return null;
    }

    /// <summary>Deletes a folder and every subfolder under it. Sessions inside are either unfiled (moved to
    /// "(No folder)") or deleted outright, per the caller's choice.</summary>
    public void DeleteFolder(string path, bool deleteSessions)
    {
        EnsureUnlocked();
        path = path.Trim().Trim('/');
        bool InFolder(string f) => f == path || f.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);

        if (deleteSessions)
        {
            var toRemove = Sessions.Where(s => InFolder(s.Folder)).ToList();
            if (toRemove.Count > 0)
            {
                _sessionStore.Save(Sessions.Except(toRemove));
                foreach (var s in toRemove) Sessions.Remove(s);
            }
        }
        else
        {
            bool changed = false;
            for (int i = 0; i < Sessions.Count; i++)
            {
                if (!InFolder(Sessions[i].Folder)) continue;
                var clone = Sessions[i].Clone();
                clone.Folder = "";
                Sessions[i] = clone;
                changed = true;
            }
            if (changed) _sessionStore.Save(Sessions);
        }

        Settings.Folders.RemoveAll(InFolder);
        SaveSettings();
    }

    public IEnumerable<SessionProfile> Recent() =>
        Settings.RecentSessionIds.Select(id => Sessions.FirstOrDefault(s => s.Id == id)).OfType<SessionProfile>();

    public void NoteRecent(SessionProfile p)
    {
        Settings.RecentSessionIds.Remove(p.Id);
        Settings.RecentSessionIds.Insert(0, p.Id);
        if (Settings.RecentSessionIds.Count > 8) Settings.RecentSessionIds.RemoveRange(8, Settings.RecentSessionIds.Count - 8);
        SaveSettings();
    }

    public void SaveSettings() => _settingsStore.Save(Settings);
}
