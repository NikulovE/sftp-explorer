using Microsoft.Terminal.Wpf;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// Adapts the native Windows Terminal renderer to an SSH.NET ShellStream.
/// Authentication remains entirely inside SSH.NET; this connection only carries
/// terminal input, output, and resize notifications.
/// </summary>
internal sealed class SshTerminalConnection : ITerminalConnection
{
    private readonly Action<string> _inputReceived;
    private readonly Action<uint, uint> _resizeRequested;
    private volatile bool _inputEnabled;

    public SshTerminalConnection(
        Action<string> inputReceived,
        Action<uint, uint> resizeRequested)
    {
        _inputReceived = inputReceived;
        _resizeRequested = resizeRequested;
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public void Start()
    {
        // SSH.NET owns the remote process and PTY lifecycle.
    }

    public void WriteInput(string data)
    {
        if (_inputEnabled && data.Length != 0)
        {
            _inputReceived(data);
        }
    }

    public void Resize(uint rowHeight, uint columnWidth)
    {
        _resizeRequested(columnWidth, rowHeight);
    }

    public void Close()
    {
        _inputEnabled = false;
    }

    public void SetInputEnabled(bool enabled)
    {
        _inputEnabled = enabled;
    }

    public void WriteOutput(string data)
    {
        if (data.Length != 0)
        {
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));
        }
    }
}
