using OmlTerminal.Core.Vnc;

namespace OmlTerminal.Core.Tests;

public class X11KeysymTests
{
    [Theory]
    [InlineData('a', 'a')]
    [InlineData('Z', 'Z')]
    [InlineData('5', '5')]
    [InlineData(' ', ' ')]
    [InlineData('~', '~')]
    public void PrintableAsciiMapsToItself(char c, char expected) => Assert.Equal((uint)expected, X11Keysym.FromChar(c));

    [Theory]
    [InlineData('\r', X11Keysym.Return)]
    [InlineData('\n', X11Keysym.Return)]
    [InlineData('\b', X11Keysym.BackSpace)]
    [InlineData('\t', X11Keysym.Tab)]
    public void ControlCharactersMapToNamedKeysyms(char c, uint expected) => Assert.Equal(expected, X11Keysym.FromChar(c));

    [Fact]
    public void EscapeMapsCorrectly() => Assert.Equal(X11Keysym.Escape, X11Keysym.FromChar((char)27));

    [Fact]
    public void FunctionKeysAreSequential()
    {
        Assert.Equal(X11Keysym.F1, X11Keysym.FunctionKey(1));
        Assert.Equal(X11Keysym.F1 + 11, X11Keysym.FunctionKey(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => X11Keysym.FunctionKey(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => X11Keysym.FunctionKey(0));
    }
}
