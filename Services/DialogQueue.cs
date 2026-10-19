namespace SftpExplorerWinUI.Services;

/// <summary>Serializes modal dialogs while keeping their continuations on the UI thread.</summary>
internal sealed class DialogQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<T> ShowAsync<T>(Func<Task<T>> show, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await show();
        }
        finally
        {
            _gate.Release();
        }
    }
}
