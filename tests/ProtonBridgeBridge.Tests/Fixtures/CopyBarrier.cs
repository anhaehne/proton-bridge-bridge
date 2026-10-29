namespace ProtonBridgeBridge.Tests;

internal sealed class CopyBarrier(CopyCheckpoint checkpoint)
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public Task WaitAsync(CancellationToken cancellationToken) => _reached.Task.WaitAsync(cancellationToken);

    public async Task<bool> ReachAsync(CopyCheckpoint current, CancellationToken cancellationToken)
    {
        if (current != checkpoint || Interlocked.Exchange(ref _entered, 1) != 0)
        {
            return true;
        }

        _reached.SetResult();
        return await _release.Task.WaitAsync(cancellationToken);
    }

    // Abort only the paused synthetic command after the owned proxy process has exited.
    public void Abort() => _release.TrySetResult(false);
}
