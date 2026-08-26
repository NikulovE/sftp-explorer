using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public void SaveTwiceRetainsPreviousValidDocumentAsBackup()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        AtomicJsonFile.Save(path, new TestDocument { Value = "first" });
        AtomicJsonFile.Save(path, new TestDocument { Value = "second" });

        var primary = AtomicJsonFile.Load(path, static () => new TestDocument());
        var backup = AtomicJsonFile.Load(
            AtomicJsonFile.GetBackupPath(path),
            static () => new TestDocument());

        Assert.Equal("second", primary.Value.Value);
        Assert.Equal("first", backup.Value.Value);
    }

    [Fact]
    public void CorruptPrimaryIsRecoveredAndRepairedFromBackup()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        AtomicJsonFile.Save(path, new TestDocument { Value = "known-good" });
        AtomicJsonFile.Save(path, new TestDocument { Value = "newer" });
        File.WriteAllText(path, "{ definitely not JSON");

        var recovered = AtomicJsonFile.Load(path, static () => new TestDocument());

        Assert.Equal(AtomicJsonLoadSource.Backup, recovered.Source);
        Assert.Equal("known-good", recovered.Value.Value);
        Assert.NotNull(recovered.PrimaryError);
        Assert.Null(recovered.RepairError);

        var repaired = AtomicJsonFile.Load(path, static () => new TestDocument());
        Assert.Equal(AtomicJsonLoadSource.Primary, repaired.Source);
        Assert.Equal("known-good", repaired.Value.Value);
    }

    [Fact]
    public void CorruptPrimaryWithoutBackupThrowsInsteadOfReturningEmptyData()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "not JSON");

        Assert.Throws<InvalidDataException>(() =>
            AtomicJsonFile.Load(path, static () => new TestDocument()));
    }

    [Fact]
    public void CorruptPrimaryAndBackupThrowInsteadOfReturningEmptyData()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        AtomicJsonFile.Save(path, new TestDocument { Value = "first" });
        AtomicJsonFile.Save(path, new TestDocument { Value = "second" });
        File.WriteAllText(path, "bad primary");
        File.WriteAllText(AtomicJsonFile.GetBackupPath(path), "bad backup");

        var error = Assert.Throws<InvalidDataException>(() =>
            AtomicJsonFile.Load(path, static () => new TestDocument()));
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Fact]
    public void MissingPrimaryAndBackupReturnNewValue()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        var result = AtomicJsonFile.Load(
            path,
            static () => new TestDocument { Value = "default" });

        Assert.Equal(AtomicJsonLoadSource.NewValue, result.Source);
        Assert.Equal("default", result.Value.Value);
    }

    [Fact]
    public void ValidPrimaryIsUsedEvenWhenBackupIsCorrupt()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        AtomicJsonFile.Save(path, new TestDocument { Value = "first" });
        AtomicJsonFile.Save(path, new TestDocument { Value = "current" });
        File.WriteAllText(AtomicJsonFile.GetBackupPath(path), "corrupt backup");

        var result = AtomicJsonFile.Load(path, static () => new TestDocument());

        Assert.Equal(AtomicJsonLoadSource.Primary, result.Source);
        Assert.Equal("current", result.Value.Value);
        Assert.Null(result.PrimaryError);
    }

    [Fact]
    public void SaveCreatesMissingParentDirectories()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "nested", "deeper", "settings.json");

        AtomicJsonFile.Save(path, new TestDocument { Value = "stored" });

        Assert.Equal("stored", AtomicJsonFile.Load(path, static () => new TestDocument()).Value.Value);
    }

    [Fact]
    public void SaveRejectsNullValuesAndBlankPaths()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        Assert.Throws<ArgumentNullException>(() => AtomicJsonFile.Save<TestDocument>(path, null!));
        Assert.Throws<ArgumentException>(() => AtomicJsonFile.Save("  ", new TestDocument()));
    }

    [Fact]
    public void LoadRejectsBlankPathsAndNullFactory()
    {
        using var temp = new TemporaryDirectory();

        Assert.Throws<ArgumentException>(() =>
            AtomicJsonFile.Load("   ", static () => new TestDocument()));
        Assert.Throws<ArgumentNullException>(() =>
            AtomicJsonFile.Load<TestDocument>(Path.Combine(temp.Path, "settings.json"), null!));
    }

    [Fact]
    public void GetBackupPathRejectsBlankPaths()
    {
        Assert.Throws<ArgumentException>(() => AtomicJsonFile.GetBackupPath(""));
    }

    [Fact]
    public void NullDocumentIsRejectedInsteadOfReturningNull()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "null");

        Assert.Throws<InvalidDataException>(() =>
            AtomicJsonFile.Load(path, static () => new TestDocument()));
    }

    [Fact]
    public void FailedRepairStillReturnsTheValidBackupAndReportsBothErrors()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");
        AtomicJsonFile.Save(path, new TestDocument { Value = "known-good" });
        AtomicJsonFile.Save(path, new TestDocument { Value = "newer" });

        // A directory at the primary location makes both the read and the
        // repair move fail while the backup remains valid.
        File.Delete(path);
        Directory.CreateDirectory(path);

        var result = AtomicJsonFile.Load(path, static () => new TestDocument());

        Assert.Equal(AtomicJsonLoadSource.Backup, result.Source);
        Assert.Equal("known-good", result.Value.Value);
        Assert.NotNull(result.PrimaryError);
        Assert.NotNull(result.RepairError);
    }

    [Fact]
    public void SaveLeavesNoTemporaryFilesBehind()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "settings.json");

        for (var i = 0; i < 5; i++)
            AtomicJsonFile.Save(path, new TestDocument { Value = $"value-{i}" });

        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".settings.json.*", SearchOption.TopDirectoryOnly));
    }

    public sealed class TestDocument
    {
        public string Value { get; set; } = string.Empty;
    }
}
