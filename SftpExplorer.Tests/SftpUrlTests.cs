using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class SftpUrlTests
{
    [Theory]
    [InlineData("sftp://phone:kde-password@192.0.2.1:1739/", "phone", "kde-password")]
    [InlineData("sftp://user:p:a:ss@192.0.2.1:1739/", "user", "p:a:ss")]
    [InlineData("sftp://user%3Aname:p%40ss%3A%2F%23%25%3F+word@192.0.2.1:1739/", "user:name", "p@ss:/#%?+word")]
    [InlineData("sftp://user:%2540@192.0.2.1:1739/", "user", "%40")]
    [InlineData("sftp://user:@192.0.2.1:1739/", "user", "")]
    [InlineData("sftp://:password@192.0.2.1:1739/", "", "password")]
    public void ParsesEmbeddedCredentialsWithoutLosingPasswordCharacters(
        string url, string username, string password)
    {
        var parsed = SftpUrl.Parse(url);

        Assert.Equal("192.0.2.1", parsed.Hostname);
        Assert.Equal(1739, parsed.Port);
        Assert.Equal(username, parsed.Username);
        Assert.Equal(password, parsed.Password);
        Assert.DoesNotContain(url, parsed.ToString()!);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, parsed.ToString()!);
        }
    }

    [Theory]
    [InlineData("sftp://user@phone/", "user")]
    [InlineData("sftp://user%3Aname@phone/", "user:name")]
    [InlineData("sftp://phone/", "")]
    public void LinksWithoutPasswordStillRequireSavedOrPromptedCredentials(string url, string username)
    {
        var parsed = SftpUrl.Parse(url);

        Assert.Equal("phone", parsed.Hostname);
        Assert.Equal(22, parsed.Port);
        Assert.Equal(username, parsed.Username);
        Assert.Null(parsed.Password);
    }

    [Theory]
    [InlineData("sftp://user:secret@phone/?query")]
    [InlineData("sftp://user:secret@phone/#fragment")]
    [InlineData("sftp://user%0Aname:secret@phone/")]
    [InlineData("https://user:secret@phone/")]
    [InlineData("sftp://user:secret@/")]
    [InlineData("sftp://user:secret@phone:99999/")]
    public void InvalidLinksAreRejectedWithoutIncludingCredentialsInTheError(string url)
    {
        var error = Assert.Throws<FormatException>(() => SftpUrl.Parse(url));

        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain(url, error.ToString());
    }
}
