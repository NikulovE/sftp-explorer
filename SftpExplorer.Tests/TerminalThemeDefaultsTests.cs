using Microsoft.Terminal.WinUI3;

namespace SftpExplorerWinUI.Tests;

public sealed class TerminalThemeDefaultsTests
{
    [Fact]
    public void FirstNativeRendererUsesAVisibleForegroundAndCompleteAnsiPalette()
    {
        var theme = TerminalThemeDefaults.Create();

        Assert.NotEqual(theme.DefaultBackground, theme.DefaultForeground);
        Assert.Equal(16, theme.ColorTable.Length);
        Assert.NotEqual(theme.DefaultBackground, theme.ColorTable[15]);
    }
}
