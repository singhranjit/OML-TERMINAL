using System.Text;
using System.Text.Json;
using OmlTerminal.Core.Persistence;
using OmlTerminal.Core.Terminal;

namespace OmlTerminal.Core.Macros;

/// <summary>One thing to send. Text may contain control characters; Enter appends a carriage return.</summary>
public sealed class MacroStep
{
    public string Text { get; set; } = "";
    public bool Enter { get; set; } = true;
}

public sealed class Macro
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int DelayMs { get; set; } = 350;
    public List<MacroStep> Steps { get; set; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Macro name is required.");
        if (Steps.Count == 0) errors.Add("Macro has no steps.");
        if (DelayMs is < 0 or > 60_000) errors.Add("Delay must be between 0 and 60000 ms.");
        return errors;
    }
}

/// <summary>Turns the bytes a user typed into macro steps. Cursor-key and other escape sequences are dropped; Backspace edits the pending line.</summary>
public sealed class MacroRecorder
{
    private enum State { Normal, Esc, Csi, Ss3 }

    private readonly List<MacroStep> _steps = new();
    private readonly StringBuilder _line = new();
    private State _state;

    public IReadOnlyList<MacroStep> Steps => _steps;

    public void OnInput(ReadOnlySpan<byte> data)
    {
        var pendingText = new List<byte>();
        void FlushText() { if (pendingText.Count > 0) { _line.Append(Encoding.UTF8.GetString(pendingText.ToArray())); pendingText.Clear(); } }

        foreach (var b in data)
        {
            switch (_state)
            {
                case State.Esc:
                    _state = b == '[' ? State.Csi : b == 'O' ? State.Ss3 : State.Normal;
                    continue;
                case State.Csi:
                    if (b is >= 0x40 and <= 0x7E) _state = State.Normal;
                    continue;
                case State.Ss3:
                    _state = State.Normal;
                    continue;
            }

            if (b == 0x1b) { FlushText(); _state = State.Esc; }
            else if (b is 0x0d or 0x0a)
            {
                FlushText();
                _steps.Add(new MacroStep { Text = _line.ToString(), Enter = true });
                _line.Clear();
            }
            else if (b is 0x7f or 0x08)
            {
                FlushText();
                if (_line.Length > 0) _line.Length--;
            }
            else pendingText.Add(b);
        }
        FlushText();
    }

    /// <summary>Ends the recording; text typed without a trailing Enter becomes a final step that does not press Enter.</summary>
    public List<MacroStep> Finish()
    {
        if (_line.Length > 0) { _steps.Add(new MacroStep { Text = _line.ToString(), Enter = false }); _line.Clear(); }
        return _steps.ToList();
    }
}

public static class MacroPlayer
{
    public static async Task PlayAsync(TerminalSession session, Macro macro, CancellationToken ct = default)
    {
        foreach (var step in macro.Steps)
        {
            ct.ThrowIfCancellationRequested();
            if (!session.IsConnected) return;
            session.Send(Encoding.UTF8.GetBytes(step.Enter ? step.Text + "\r" : step.Text));
            if (macro.DelayMs > 0) await Task.Delay(macro.DelayMs, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Sends one command line to many sessions at once. Sessions that are not connected are skipped.</summary>
public static class MultiExecutor
{
    public static int Broadcast(IEnumerable<TerminalSession> sessions, string text, bool enter = true)
    {
        var bytes = Encoding.UTF8.GetBytes(enter ? text + "\r" : text);
        int sent = 0;
        foreach (var s in sessions)
        {
            if (!s.IsConnected) continue;
            s.Send(bytes);
            sent++;
        }
        return sent;
    }
}

public sealed class JsonMacroStore(string? path = null)
{
    public string Path { get; } = path ?? System.IO.Path.Combine(AppPaths.DataDirectory, "macros.json");

    public List<Macro> Load()
    {
        if (!File.Exists(Path)) return new();
        try
        {
            var all = JsonSerializer.Deserialize<List<Macro>>(File.ReadAllText(Path), JsonFile.Options);
            return all?.Where(m => m is not null && m.Validate().Count == 0).ToList() ?? new();
        }
        catch (JsonException) { return new(); }
    }

    /// <summary>Rejects an invalid macro before touching disk, like the session store does.</summary>
    public void Save(IEnumerable<Macro> macros)
    {
        var list = macros.ToList();
        foreach (var m in list)
        {
            var errors = m.Validate();
            if (errors.Count > 0) throw new InvalidOperationException("Invalid macro: " + string.Join(" ", errors));
        }
        JsonFile.WriteAtomic(Path, list);
    }
}
