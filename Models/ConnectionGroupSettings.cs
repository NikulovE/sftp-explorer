namespace SftpExplorerWinUI.Models;

public class ConnectionGroupSettings
{
    public string Name { get; set; } = "";
    public string Glyph { get; set; } = ConnectionAppearanceDefaults.GroupGlyph;
    public string Color { get; set; } = ConnectionAppearanceDefaults.DefaultColor;
    public bool IsExpanded { get; set; } = true;
}

public static class ConnectionAppearanceDefaults
{
    public const string ConnectionGlyph = "\uE753";
    public const string GroupGlyph = "\uE8B7";
    public const string DefaultColor = "#0078D4";
}
