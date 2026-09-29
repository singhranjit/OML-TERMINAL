using Microsoft.Win32;

namespace OmlTerminal.Core.Transports;

/// <summary>
/// plink -batch refuses algorithms PuTTY classes as weak (e.g. diffie-hellman-group1-sha1 on old IOS). PuTTY only lets
/// that threshold be moved through a saved session, so once the user explicitly allows legacy crypto for a host we
/// create one dedicated saved session and load it with -load. Nothing else in the user's PuTTY config is touched.
/// </summary>
public static class PuttyLegacyProfile
{
    public const string SessionName = "OML-Terminal-Legacy";
    private const string Root = @"Software\SimonTatham\PuTTY\Sessions\" + SessionName;

    // Everything listed before WARN is used without a warning, so legacy algorithms are moved ahead of it (strongest first).
    private static readonly (string Name, string Value)[] Settings =
    [
        ("KEX", "ecdh,dh-gex-sha1,dh-group14-sha1,dh-group1-sha1,rsa,WARN"),
        ("Cipher", "chacha20,aesgcm,aes,3des,blowfish,des,arcfour,WARN"),
        ("HostKey", "ed25519,ecdsa,rsa,dsa,WARN"),
    ];

    public static string Ensure()
    {
        if (!OperatingSystem.IsWindows()) return SessionName;
        using var key = Registry.CurrentUser.CreateSubKey(Root);
        foreach (var (name, value) in Settings) key.SetValue(name, value, RegistryValueKind.String);
        return SessionName;
    }
}
