using Microsoft.Terminal.WinUI3;

namespace SftpExplorerWinUI.Tests;

public sealed class TerminalRenderBufferTests
{
    [Fact]
    public void OutputReceivedBeforeNativeHwndExistsIsAvailableForItsFirstRender()
    {
        var buffer = new TerminalRenderBuffer();

        buffer.Append("\x001bc\x1b[?25h");
        buffer.Append("test@host:~$ echo ready\r\nready\r\ntest@host:~$ ");

        Assert.Equal(
            "\x001bc\x1b[?25htest@host:~$ echo ready\r\nready\r\ntest@host:~$ ",
            buffer.Snapshot());
    }

    [Fact]
    public void RecreatedRendererReceivesCompleteHistoryAfterSwitchingBackToTerminalTab()
    {
        var buffer = new TerminalRenderBuffer();
        buffer.Append("\x001bcfirst terminal line\r\n");
        buffer.Append("second terminal line\r\n");
        buffer.Append("third terminal line\r\n");

        var replayedByRecreatedRenderer = buffer.Snapshot();

        Assert.Contains("first terminal line", replayedByRecreatedRenderer);
        Assert.Contains("second terminal line", replayedByRecreatedRenderer);
        Assert.Contains("third terminal line", replayedByRecreatedRenderer);
    }

    [Fact]
    public void TwoTerminalTabsKeepIndependentCompleteHistoriesAcrossRepeatedSwitches()
    {
        var firstTab = new TerminalRenderBuffer();
        var secondTab = new TerminalRenderBuffer();
        firstTab.Append("first@host:~$ echo TAB_A\r\nTAB_A\r\n");
        secondTab.Append("second@host:~$ echo TAB_B\r\nTAB_B\r\n");

        for (var switchIndex = 0; switchIndex < 100; switchIndex++)
        {
            var visibleHistory = switchIndex % 2 == 0
                ? firstTab.Snapshot()
                : secondTab.Snapshot();

            Assert.Contains(switchIndex % 2 == 0 ? "TAB_A" : "TAB_B", visibleHistory);
            Assert.DoesNotContain(switchIndex % 2 == 0 ? "TAB_B" : "TAB_A", visibleHistory);
        }

        Assert.Contains("first@host", firstTab.Snapshot());
        Assert.Contains("second@host", secondTab.Snapshot());
    }

    [Fact]
    public void ExplicitClearDropsPriorScrollbackBeforeNextRendererRecreation()
    {
        var buffer = new TerminalRenderBuffer();
        buffer.Append("\x001bcobsolete terminal history\r\n");
        buffer.Append("\x1b[H\x1b[2J\x1b[3Jcurrent prompt");

        var replayedByRecreatedRenderer = buffer.Snapshot();

        Assert.DoesNotContain("obsolete terminal history", replayedByRecreatedRenderer);
        Assert.Contains("current prompt", replayedByRecreatedRenderer);
    }

    [Fact]
    public void EmptyAndNullOutputAreIgnored()
    {
        var buffer = new TerminalRenderBuffer();
        buffer.Append("");
        buffer.Append(null!);

        Assert.Equal(string.Empty, buffer.Snapshot());
    }

    [Fact]
    public void BufferIsCappedAtTheMaximumSizeKeepingTheNewestOutput()
    {
        const int maximumCharacters = 2_000_000;
        var buffer = new TerminalRenderBuffer();
        var marker = "END-OF-HISTORY";

        // Push the buffer past its cap: the oldest characters must be dropped.
        buffer.Append(new string('a', maximumCharacters + 1));
        buffer.Append(marker);

        var snapshot = buffer.Snapshot();

        Assert.Equal(maximumCharacters, snapshot.Length);
        Assert.EndsWith(marker, snapshot);
    }
}
