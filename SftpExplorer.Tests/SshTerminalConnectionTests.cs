using Microsoft.Terminal.Wpf;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class SshTerminalConnectionTests
{
    [Fact]
    public void InputIsDeliveredOnlyWhileEnabled()
    {
        var received = new List<string>();
        var connection = new SshTerminalConnection(received.Add, static (_, _) => { });

        // The adapter starts disabled: input before the session is ready is dropped.
        connection.WriteInput("early");
        Assert.Empty(received);

        connection.SetInputEnabled(true);
        connection.WriteInput("ls\r\n");
        Assert.Equal(new[] { "ls\r\n" }, received);

        // Empty input never reaches the backend even when enabled.
        connection.WriteInput("");
        Assert.Single(received);

        connection.Close();
        connection.WriteInput("late");
        Assert.Single(received);
    }

    [Fact]
    public void CloseDisablesFurtherInputAndIsIdempotent()
    {
        var received = new List<string>();
        var connection = new SshTerminalConnection(received.Add, static (_, _) => { });
        connection.SetInputEnabled(true);

        connection.Close();
        connection.Close();
        connection.WriteInput("after-close");

        Assert.Empty(received);
    }

    [Fact]
    public void ResizeSwapsRowsAndColumnsForTheBackend()
    {
        uint? lastColumns = null;
        uint? lastRows = null;
        var connection = new SshTerminalConnection(
            static _ => { },
            (columns, rows) =>
            {
                lastColumns = columns;
                lastRows = rows;
            });

        // The terminal control reports (rows, columns); the SSH backend wants (columns, rows).
        connection.Resize(40, 120);

        Assert.Equal(120u, lastColumns);
        Assert.Equal(40u, lastRows);
    }

    [Fact]
    public void OutputIsRaisedOnlyForNonEmptyData()
    {
        var connection = new SshTerminalConnection(static _ => { }, static (_, _) => { });
        var output = new List<string>();
        connection.TerminalOutput += (_, args) => output.Add(args.Data);

        connection.WriteOutput("remote banner\r\n");
        connection.WriteOutput("");

        Assert.Equal(new[] { "remote banner\r\n" }, output);
    }

    [Fact]
    public void StartIsASafeNoOpBecauseSshNetOwnsThePty()
    {
        var connection = new SshTerminalConnection(static _ => { }, static (_, _) => { });
        connection.Start(); // Must not throw or require a live session.
    }
}
