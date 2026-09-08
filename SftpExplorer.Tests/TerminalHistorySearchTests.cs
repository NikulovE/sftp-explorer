using SftpExplorerWinUI.Helpers;

namespace SftpExplorerWinUI.Tests;

public sealed class TerminalHistorySearchTests
{
    [Fact]
    public void FindsEveryCaseInsensitiveOccurrenceAndRetainsLineNumbers()
    {
        var result = TerminalHistorySearch.Find(
            "first error\nERROR: one error\ncomplete\n",
            "error");

        Assert.Equal(3, result.MatchCount);
        Assert.Equal(2, result.MatchingLineCount);
        Assert.Collection(
            result.Matches,
            match =>
            {
                Assert.Equal(1, match.LineNumber);
                Assert.Equal("first error", match.Text);
                Assert.Equal(1, match.OccurrenceCount);
            },
            match =>
            {
                Assert.Equal(2, match.LineNumber);
                Assert.Equal("ERROR: one error", match.Text);
                Assert.Equal(2, match.OccurrenceCount);
            });
    }

    [Fact]
    public void LimitsDisplayedLinesWithoutDiscardingTheTotalMatchCount()
    {
        var result = TerminalHistorySearch.Find(
            "match one\nmatch two\nmatch three",
            "match",
            maximumDisplayedMatchingLines: 2);

        Assert.Equal(3, result.MatchCount);
        Assert.Equal(3, result.MatchingLineCount);
        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(2, result.Matches[1].LineNumber);
    }

    [Fact]
    public void EmptyQueryDoesNotMatchEveryPosition()
    {
        var result = TerminalHistorySearch.Find("terminal output", "   ");

        Assert.Equal(0, result.MatchCount);
        Assert.Equal(0, result.MatchingLineCount);
        Assert.Empty(result.Matches);
    }
}
