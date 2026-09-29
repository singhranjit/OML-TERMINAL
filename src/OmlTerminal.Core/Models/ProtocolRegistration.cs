using Microsoft.Win32;

namespace OmlTerminal.Core.Models;

/// <summary>Registers oml-terminal:// for the current user (HKCU only, no elevation). Only ever called from an explicit user action.</summary>
public static class ProtocolRegistration
{
    private const string Root = @"Software\Classes\" + OmlNodeLink.Scheme;

    public static bool IsRegistered(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var cmd = Registry.CurrentUser.OpenSubKey(Root + @"\shell\open\command");
        return cmd?.GetValue(null) as string == CommandFor(exePath);
    }

    public static void Register(string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var k = Registry.CurrentUser.CreateSubKey(Root))
        {
            k.SetValue(null, "URL:OML Terminal");
            k.SetValue("URL Protocol", "");
        }
        using (var c = Registry.CurrentUser.CreateSubKey(Root + @"\shell\open\command"))
            c.SetValue(null, CommandFor(exePath));
    }

    private static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";
}
