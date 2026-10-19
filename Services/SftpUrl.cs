namespace SftpExplorerWinUI.Services;

// Keep credentials out of generated record ToString() output.
internal sealed class SftpUrl
{
    public string Hostname { get; }
    public int Port { get; }
    public string Username { get; }
    public string? Password { get; }

    private SftpUrl(string hostname, int port, string username, string? password)
    {
        Hostname = hostname;
        Port = port;
        Username = username;
        Password = password;
    }

    public static SftpUrl Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost))
        {
            throw new FormatException("A valid absolute sftp:// URL with a hostname is required.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("SFTP URL query strings and fragments are not supported.");
        }

        // Split before decoding: an escaped colon may belong to the username,
        // and additional colons belong to the password.
        var separator = uri.UserInfo.IndexOf(':');
        var username = Uri.UnescapeDataString(separator < 0
            ? uri.UserInfo
            : uri.UserInfo[..separator]);
        var password = separator < 0
            ? null
            : Uri.UnescapeDataString(uri.UserInfo[(separator + 1)..]);

        if (username.Any(char.IsControl))
        {
            throw new FormatException("The SFTP URL contains an invalid username.");
        }

        return new SftpUrl(uri.DnsSafeHost, uri.Port is >= 1 and <= 65535 ? uri.Port : 22,
            username, password);
    }
}
