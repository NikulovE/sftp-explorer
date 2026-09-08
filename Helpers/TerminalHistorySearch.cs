namespace SftpExplorerWinUI.Helpers;

internal sealed record TerminalHistorySearchMatch(int LineNumber, string Text, int OccurrenceCount);

internal sealed record TerminalHistorySearchResult(
    int MatchCount,
    int MatchingLineCount,
    IReadOnlyList<TerminalHistorySearchMatch> Matches);

internal static class TerminalHistorySearch
{
    internal const int MaximumDisplayedMatchingLines = 100;

    internal static TerminalHistorySearchResult Find(
        string history,
        string query,
        int maximumDisplayedMatchingLines = MaximumDisplayedMatchingLines)
    {
        if (string.IsNullOrWhiteSpace(history) || string.IsNullOrWhiteSpace(query))
        {
            return new TerminalHistorySearchResult(0, 0, Array.Empty<TerminalHistorySearchMatch>());
        }

        var matches = new List<TerminalHistorySearchMatch>(
            Math.Min(Math.Max(0, maximumDisplayedMatchingLines), 32));
        var matchCount = 0;
        var matchingLineCount = 0;
        var lines = history.Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var occurrences = CountOccurrences(line, query);
            if (occurrences == 0)
            {
                continue;
            }

            matchingLineCount++;
            matchCount += occurrences;
            if (matches.Count < maximumDisplayedMatchingLines)
            {
                matches.Add(new TerminalHistorySearchMatch(index + 1, line, occurrences));
            }
        }

        return new TerminalHistorySearchResult(matchCount, matchingLineCount, matches);
    }

    private static int CountOccurrences(string text, string query)
    {
        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var matchIndex = text.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                break;
            }

            count++;
            startIndex = matchIndex + query.Length;
        }

        return count;
    }
}
