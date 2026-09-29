using System.Text;
using System.Text.RegularExpressions;

namespace OmlTerminal.Core.Terminal;

/// <summary>
/// Watches the first moments of a session's output and answers login prompts with saved credentials:
///  - "Username:" / "login:" / "Password:" for Telnet and serial consoles (SSH authenticates before any output),
///  - then, if an enable password is saved and the device lands at a "hostname>" prompt, "enable" + the password.
/// Each answer is given once; after that (or after enough output) it goes quiet, so a later "Password:" prompt -
/// sudo, a second login - is always left to the user.
/// </summary>
public sealed partial class LoginAutomator
{
    private enum Stage { Login, Enable, WaitEnablePassword, Done }

    private readonly string _username, _password, _enablePassword;
    private readonly StringBuilder _tail = new();
    private Stage _stage;
    private bool _userSent, _passwordSent;
    private int _seen;

    /// <param name="answerLogin">True for Telnet/serial, where the device asks for username/password in-band.</param>
    public LoginAutomator(string username, string password, string enablePassword, bool answerLogin)
    {
        _username = username;
        _password = password;
        _enablePassword = enablePassword;
        _stage = answerLogin && (username.Length > 0 || password.Length > 0) ? Stage.Login
               : enablePassword.Length > 0 ? Stage.Enable
               : Stage.Done;
    }

    public bool IsDone => _stage == Stage.Done;

    [GeneratedRegex(@"(user\s*name|login)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UserPrompt();

    [GeneratedRegex(@"pass(word|code)?\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();

    /// <summary>A privileged-mode-capable user prompt: "sw1>", "asa/ctx1>", but not "PS C:\>" or "user@host>" (Junos).</summary>
    [GeneratedRegex(@"(^|\n)[A-Za-z0-9][A-Za-z0-9_.\-/()]{0,62}>\s*$")]
    private static partial Regex UserModePrompt();

    [GeneratedRegex(@"(^|\n)[A-Za-z0-9][A-Za-z0-9_.\-/()]{0,62}#\s*$")]
    private static partial Regex PrivilegedPrompt();

    /// <summary>Feed decoded, ANSI-stripped output. Returns text to type (including the trailing CR), or null.</summary>
    public string? OnOutput(string text)
    {
        if (_stage == Stage.Done) return null;
        _seen += text.Length;
        _tail.Append(text.Replace("\r", ""));
        if (_tail.Length > 400) _tail.Remove(0, _tail.Length - 400);
        var tail = _tail.ToString();

        string? answer = null;
        switch (_stage)
        {
            case Stage.Login:
                if (!_userSent && _username.Length > 0 && UserPrompt().IsMatch(tail)) { _userSent = true; answer = _username; }
                else if (!_passwordSent && PasswordPrompt().IsMatch(tail))
                {
                    _passwordSent = true;
                    answer = _password;
                    _stage = _enablePassword.Length > 0 ? Stage.Enable : Stage.Done;
                }
                else if (UserModePrompt().IsMatch(tail) || PrivilegedPrompt().IsMatch(tail))
                    _stage = _enablePassword.Length > 0 ? Stage.Enable : Stage.Done; // no login asked (already authenticated)
                if (_stage == Stage.Enable && answer is null) return OnEnableStage(tail);
                break;
            case Stage.Enable:
                return OnEnableStage(tail);
            case Stage.WaitEnablePassword:
                if (PasswordPrompt().IsMatch(tail)) { answer = _enablePassword; _stage = Stage.Done; }
                else if (PrivilegedPrompt().IsMatch(tail)) _stage = Stage.Done; // no enable password was needed
                break;
        }
        if (_seen > 64 * 1024) _stage = Stage.Done;
        if (answer is null) return null;
        _tail.Clear();
        return answer + "\r";
    }

    private string? OnEnableStage(string tail)
    {
        if (PrivilegedPrompt().IsMatch(tail)) { _stage = Stage.Done; return null; } // already privileged (priv 15)
        if (!UserModePrompt().IsMatch(tail)) return null;
        _stage = Stage.WaitEnablePassword;
        _tail.Clear();
        return "enable\r";
    }
}
