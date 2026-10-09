using System;
using System.Collections.Generic;

namespace OmlTerminal.Core.Parsing;

/// <summary>Sorts "Gi1/0/2" before "Gi1/0/10" and "9" before "120" - the way people expect interface and number columns.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= ""; y ??= "";
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }
            int d = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
