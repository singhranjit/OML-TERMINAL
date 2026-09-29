namespace OmlTerminal.Core;

public static class TextLines
{
    /// <summary>Splits on \r\n, \n or a bare \r. The bare \r matters: WinUI's multi-line TextBox hands text back with
    /// "\r" alone between lines, so splitting on '\n' would turn a whole pasted list into one line.</summary>
    public static string[] Split(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
