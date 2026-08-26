using System;

namespace SftpExplorerWinUI.Models;

public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Hostname { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public SftpAuthenticationMode AuthenticationMode { get; set; } = SftpAuthenticationMode.Password;
    public long AuthenticationRevision { get; set; }
    public string? PrivateKeyPath { get; set; }
    public bool PrivateKeyRequiresPassphrase { get; set; }
    public string Group { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Glyph { get; set; } = ConnectionAppearanceDefaults.ConnectionGlyph;
    public string? EncryptedPassword { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsed { get; set; } = DateTime.Now;
    public string Color { get; set; } = ConnectionAppearanceDefaults.DefaultColor;
}

public enum SftpAuthenticationMode
{
    Password = 0,
    PrivateKey = 1
}
