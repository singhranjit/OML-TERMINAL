namespace OmlTerminal.Core.Voice;

public static class CidrHelper
{
    /// <summary>Converts a /prefix length to a dotted-decimal netmask (e.g. 24 -> "255.255.255.0"). Null if out of range.</summary>
    public static string? MaskFromPrefix(int prefixLength)
    {
        if (prefixLength is < 0 or > 32) return null;
        uint mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        return $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
    }
}
