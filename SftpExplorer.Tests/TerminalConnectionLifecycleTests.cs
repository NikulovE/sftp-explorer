using Microsoft.Terminal.WinUI3;
using Microsoft.Terminal.Wpf;

namespace SftpExplorerWinUI.Tests;

public sealed class TerminalConnectionLifecycleTests
{
    [Fact]
    public void ReattachingSameTerminalAfterTabSwitchKeepsHistoryAndDoesNotRestartSession()
    {
        var rendered = new List<string>();
        var terminal = new FakeTerminalConnection();
        var lifecycle = new TerminalConnectionLifecycle((_, output) => rendered.Add(output.Data));

        Assert.True(lifecycle.SetConnection(terminal));
        terminal.Emit("first command output\r\n");
        var outputBeforeTabSwitch = rendered.ToArray();

        var changed = lifecycle.SetConnection(terminal);

        Assert.False(changed);
        Assert.Equal(1, terminal.StartCalls);
        Assert.Equal(outputBeforeTabSwitch, rendered);
        Assert.Contains("first command output\r\n", rendered);
        Assert.Same(terminal, lifecycle.Current);
    }

    [Fact]
    public void ReplacingTerminalSessionUnsubscribesPreviousBackendAndStartsNewOne()
    {
        var rendered = new List<string>();
        var first = new FakeTerminalConnection();
        var second = new FakeTerminalConnection();
        var lifecycle = new TerminalConnectionLifecycle((_, output) => rendered.Add(output.Data));
        lifecycle.SetConnection(first);

        Assert.True(lifecycle.SetConnection(second));
        first.Emit("stale output");
        second.Emit("current output");

        Assert.Equal(1, first.StartCalls);
        Assert.Equal(1, second.StartCalls);
        Assert.DoesNotContain("stale output", rendered);
        Assert.Contains("current output", rendered);
    }

    [Fact]
    public void SwitchingBetweenTwoTerminalTabsNeverRestartsEitherBackend()
    {
        var firstRendered = new List<string>();
        var secondRendered = new List<string>();
        var firstTerminal = new FakeTerminalConnection();
        var secondTerminal = new FakeTerminalConnection();
        var firstLifecycle = new TerminalConnectionLifecycle(
            (_, output) => firstRendered.Add(output.Data));
        var secondLifecycle = new TerminalConnectionLifecycle(
            (_, output) => secondRendered.Add(output.Data));

        Assert.True(firstLifecycle.SetConnection(firstTerminal));
        Assert.True(secondLifecycle.SetConnection(secondTerminal));
        firstTerminal.Emit("TAB_A history\r\n");
        secondTerminal.Emit("TAB_B history\r\n");

        for (var switchIndex = 0; switchIndex < 100; switchIndex++)
        {
            Assert.False(firstLifecycle.SetConnection(firstTerminal));
            Assert.False(secondLifecycle.SetConnection(secondTerminal));
        }

        Assert.Equal(1, firstTerminal.StartCalls);
        Assert.Equal(1, secondTerminal.StartCalls);
        Assert.Contains("TAB_A history\r\n", firstRendered);
        Assert.DoesNotContain("TAB_B history\r\n", firstRendered);
        Assert.Contains("TAB_B history\r\n", secondRendered);
        Assert.DoesNotContain("TAB_A history\r\n", secondRendered);
    }

    [Fact]
    public void DetachingTheTerminalHidesTheCursorAndStopsDeliveringOutput()
    {
        var rendered = new List<string>();
        var terminal = new FakeTerminalConnection();
        var lifecycle = new TerminalConnectionLifecycle((_, output) => rendered.Add(output.Data));

        Assert.True(lifecycle.SetConnection(terminal));
        // Detach: the renderer must hide the cursor and drop the backend.
        Assert.True(lifecycle.SetConnection(null));
        terminal.Emit("output after detach");

        Assert.Null(lifecycle.Current);
        Assert.Contains("\x1b[?25l", rendered);
        Assert.DoesNotContain("output after detach", rendered);
    }

    [Fact]
    public void ReattachingAfterDetachStartsTheBackendAgain()
    {
        var terminal = new FakeTerminalConnection();
        var lifecycle = new TerminalConnectionLifecycle((_, _) => { });

        Assert.True(lifecycle.SetConnection(terminal));
        Assert.Equal(1, terminal.StartCalls);

        Assert.True(lifecycle.SetConnection(null));
        Assert.True(lifecycle.SetConnection(terminal));
        Assert.Equal(2, terminal.StartCalls);
    }

    [Fact]
    public void ConstructorRejectsANullRenderHandler()
    {
        Assert.Throws<ArgumentNullException>(() => new TerminalConnectionLifecycle(null!));
    }

    private sealed class FakeTerminalConnection : ITerminalConnection
    {
        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public int StartCalls { get; private set; }

        public void Start() => StartCalls++;

        public void WriteInput(string data) { }

        public void Resize(uint rows, uint columns) { }

        public void Close() { }

        public void Emit(string output) => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(output));
    }
}
