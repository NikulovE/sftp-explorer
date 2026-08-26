using System.Text;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class LocalPathSafetyTests
{
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape.txt")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("COM1.log")]
    [InlineData("report.")]
    [InlineData("report ")]
    [InlineData("file:name")]
    public void CombineChildRejectsHostileOrAmbiguousWindowsNames(string remoteName)
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => LocalPathSafety.CombineChild(temp.Path, remoteName));
    }

    [Fact]
    public void CombineChildAcceptsSafeUnicodeNameInsideRoot()
    {
        using var temp = new TemporaryDirectory();

        var result = LocalPathSafety.CombineChild(temp.Path, "данные-2026.txt");

        Assert.Equal(
            System.IO.Path.Combine(System.IO.Path.GetFullPath(temp.Path), "данные-2026.txt"),
            result);
    }

    [Fact]
    public void ReserveChildRejectsCaseInsensitiveCollision()
    {
        using var temp = new TemporaryDirectory();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        LocalPathSafety.ReserveChild(temp.Path, "Readme.txt", reserved);

        Assert.Throws<IOException>(() =>
            LocalPathSafety.ReserveChild(temp.Path, "README.TXT", reserved));
    }

    [Fact]
    public void ReserveChildRejectsUnicodeNormalizationCollision()
    {
        using var temp = new TemporaryDirectory();
        var reserved = new HashSet<string>(StringComparer.Ordinal);
        var composed = "caf\u00e9.txt";
        var decomposed = "cafe\u0301.txt";
        Assert.NotEqual(composed, decomposed);
        Assert.Equal(composed.Normalize(NormalizationForm.FormC), decomposed.Normalize(NormalizationForm.FormC));

        LocalPathSafety.ReserveChild(temp.Path, composed, reserved);

        Assert.Throws<IOException>(() =>
            LocalPathSafety.ReserveChild(temp.Path, decomposed, reserved));
    }

    [Fact]
    public void CleanupGuardAcceptsOnlyStrictDescendants()
    {
        using var temp = new TemporaryDirectory();
        var child = System.IO.Path.Combine(temp.Path, "owned", "item");
        var outside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(temp.Path)!, "outside");

        Assert.Equal(System.IO.Path.GetFullPath(child), LocalPathSafety.EnsureStrictDescendant(temp.Path, child));
        Assert.Throws<InvalidOperationException>(() =>
            LocalPathSafety.EnsureStrictDescendant(temp.Path, temp.Path));
        Assert.Throws<InvalidOperationException>(() =>
            LocalPathSafety.EnsureStrictDescendant(temp.Path, outside));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSingleNameRejectsBlankNames(string remoteName)
    {
        Assert.Throws<ArgumentException>(() => LocalPathSafety.ValidateSingleName(remoteName));
    }

    [Fact]
    public void ValidateSingleNameRejectsOverlongNames()
    {
        var tooLong = new string('a', 256);
        Assert.Throws<ArgumentException>(() => LocalPathSafety.ValidateSingleName(tooLong));
        // Exactly 255 characters is still a valid Windows file name.
        LocalPathSafety.ValidateSingleName(new string('a', 255));
    }

    [Theory]
    [InlineData("file\u0001.txt")]
    [InlineData("line\nbreak.txt")]
    public void ValidateSingleNameRejectsControlCharacters(string remoteName)
    {
        Assert.Throws<ArgumentException>(() => LocalPathSafety.ValidateSingleName(remoteName));
    }

    [Theory]
    [InlineData("LPT9.txt")]
    [InlineData("aux.log")]
    [InlineData("CON")]
    public void ValidateSingleNameRejectsReservedDeviceStems(string remoteName)
    {
        Assert.Throws<ArgumentException>(() => LocalPathSafety.ValidateSingleName(remoteName));
    }

    [Fact]
    public void ValidateSingleNameAcceptsNamesWhoseStemOnlyResemblesADevice()
    {
        // "COM10" is not a reserved Windows device name, only COM1-COM9 are.
        LocalPathSafety.ValidateSingleName("com10.bin");
        LocalPathSafety.ValidateSingleName("connection.txt");
    }

    [Fact]
    public void GetCollisionKeyNormalizesCaseAndUnicode()
    {
        var composed = "caf\u00e9.txt";
        var decomposed = "cafe\u0301.TXT";

        Assert.Equal(
            LocalPathSafety.GetCollisionKey(composed),
            LocalPathSafety.GetCollisionKey(decomposed));
    }

    [Fact]
    public void ReserveChildReturnsTheSafeChildPath()
    {
        using var temp = new TemporaryDirectory();
        var reserved = new HashSet<string>(StringComparer.Ordinal);

        var result = LocalPathSafety.ReserveChild(temp.Path, "report.txt", reserved);

        Assert.Equal(
            System.IO.Path.Combine(System.IO.Path.GetFullPath(temp.Path), "report.txt"),
            result);
    }

    [Fact]
    public void EnsureStrictDescendantRejectsSiblingDirectoriesWithSharedPrefix()
    {
        using var temp = new TemporaryDirectory();
        var parent = System.IO.Path.GetDirectoryName(temp.Path)!;
        var sibling = System.IO.Path.Combine(parent, Path.GetFileName(temp.Path) + "-evil", "item");

        Assert.Throws<InvalidOperationException>(() =>
            LocalPathSafety.EnsureStrictDescendant(temp.Path, sibling));
    }
}
