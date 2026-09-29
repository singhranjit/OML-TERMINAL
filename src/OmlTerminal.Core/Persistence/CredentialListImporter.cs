using OmlTerminal.Core.Models;

namespace OmlTerminal.Core.Persistence;

/// <summary>
/// Imports MobaXterm's "Credentials" / password-manager export: one line per saved credential,
/// <c>Name(username) = password</c>. Each line becomes its own vault entry rather than being merged by
/// username - the same username can appear under several differently-named credentials with different
/// passwords (e.g. a "root" credential and a separately-named "global controller" credential that also logs
/// in as root but with a different password), and a credential's display Name doesn't have to match its
/// Username at all.
/// </summary>
public static class CredentialListImporter
{
    public static List<Credential> Parse(string text)
    {
        var results = new List<Credential>();
        foreach (var rawLine in TextLines.Split(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (TryParseLine(line, out var credential)) results.Add(credential);
        }
        return results;
    }

    /// <summary>Parses one line of the form <c>Name(username) = password</c>.</summary>
    public static bool TryParseLine(string line, out Credential credential)
    {
        credential = null!;
        int eq = line.IndexOf('=');
        if (eq < 0) return false;

        var left = line[..eq].Trim();
        var password = line[(eq + 1)..].Trim();

        int openParen = left.IndexOf('(');
        int closeParen = left.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen) return false;

        var name = left[..openParen].Trim();
        var username = left[(openParen + 1)..closeParen].Trim();
        if (name.Length == 0 || username.Length == 0 || password.Length == 0) return false;

        credential = new Credential { Name = name, Username = username, Password = password };
        return true;
    }

    /// <summary>How many sessions (that don't already have a credential) got linked to which newly-imported
    /// credential, keyed by the credential's Id - and which usernames couldn't be auto-linked because more than
    /// one imported credential shares that username, so a human has to pick.</summary>
    public sealed record LinkResult(Dictionary<Guid, int> LinkedCounts, IReadOnlyList<string> AmbiguousUsernames);

    /// <summary>Links sessions to a newly-imported credential by exact username match - but only where exactly
    /// one imported credential has that username. A username claimed by more than one credential (different
    /// passwords for the same login name, on different devices) is left for the operator to resolve by hand in
    /// the session editor, rather than guessing and attaching the wrong password to a device.</summary>
    public static LinkResult LinkByUsername(IEnumerable<SessionProfile> sessions, IReadOnlyList<Credential> imported)
    {
        var byUsername = imported
            .Where(c => c.Username.Length > 0)
            .GroupBy(c => c.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var linkedCounts = new Dictionary<Guid, int>();
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            if (session.CredentialId is not null) continue;
            if (session.Username.Length == 0) continue;
            if (!byUsername.TryGetValue(session.Username, out var candidates)) continue;

            if (candidates.Count > 1) { ambiguous.Add(session.Username); continue; }

            var credential = candidates[0];
            session.CredentialId = credential.Id;
            linkedCounts[credential.Id] = linkedCounts.GetValueOrDefault(credential.Id) + 1;
        }

        return new LinkResult(linkedCounts, ambiguous.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
