using SftpExplorerWinUI.Helpers;
using SftpExplorerWinUI.Models;

namespace SftpExplorerWinUI.Tests;

public sealed class ConnectionSearchTests
{
    [Theory]
    [InlineData("   ")]
    [InlineData("PROD")]
    [InlineData("10.1.2")]
    [InlineData("ADMIN")]
    [InlineData("servers")]
    [InlineData("database")]
    [InlineData("2222")]
    public void MatchesVisibleConnectionFields(string query)
    {
        var connection = new SavedConnection { Name = "Prod", Hostname = "10.1.2.3", Username = "admin", Group = "Servers", Notes = "Database", Port = 2222 };
        Assert.True(ConnectionSearch.Matches(connection, query));
        Assert.False(ConnectionSearch.Matches(connection, "missing"));
    }
}
