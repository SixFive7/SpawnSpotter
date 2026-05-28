namespace SpawnSpotter.Pipeline;

/// <summary>Atomic per-classification counters. Read by the UX status line; updated by the pipeline sink.</summary>
internal sealed class Counters
{
    private long _steal, _maybeSteal, _sessionLock, _userAltTab, _userClick, _userOther, _shellTransient, _pipelinePressure, _prevWindowClosed, _focusRestored, _sameApp;
    private long _droppedAtIngest;

    public long Steal => Volatile.Read(ref _steal);
    public long MaybeSteal => Volatile.Read(ref _maybeSteal);
    public long SessionLock => Volatile.Read(ref _sessionLock);
    public long UserAltTab => Volatile.Read(ref _userAltTab);
    public long UserClick => Volatile.Read(ref _userClick);
    public long UserOther => Volatile.Read(ref _userOther);
    public long ShellTransient => Volatile.Read(ref _shellTransient);
    public long PipelinePressure => Volatile.Read(ref _pipelinePressure);
    public long PrevWindowClosed => Volatile.Read(ref _prevWindowClosed);
    public long FocusRestored => Volatile.Read(ref _focusRestored);
    public long SameApp => Volatile.Read(ref _sameApp);
    public long DroppedAtIngest => Volatile.Read(ref _droppedAtIngest);

    public void IncrementSteal() => Interlocked.Increment(ref _steal);
    public void IncrementMaybeSteal() => Interlocked.Increment(ref _maybeSteal);
    public void IncrementSessionLock() => Interlocked.Increment(ref _sessionLock);
    public void IncrementUserAltTab() => Interlocked.Increment(ref _userAltTab);
    public void IncrementUserClick() => Interlocked.Increment(ref _userClick);
    public void IncrementUserOther() => Interlocked.Increment(ref _userOther);
    public void IncrementShellTransient() => Interlocked.Increment(ref _shellTransient);
    public void IncrementPipelinePressure() => Interlocked.Increment(ref _pipelinePressure);
    public void IncrementPrevWindowClosed() => Interlocked.Increment(ref _prevWindowClosed);
    public void IncrementFocusRestored() => Interlocked.Increment(ref _focusRestored);
    public void IncrementSameApp() => Interlocked.Increment(ref _sameApp);
    public void IncrementDroppedAtIngest() => Interlocked.Increment(ref _droppedAtIngest);
}
