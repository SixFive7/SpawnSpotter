namespace SpawnSpotter.Pipeline;

/// <summary>Atomic per-classification counters. Read by the UX status line; updated by the pipeline sink.</summary>
internal sealed class Counters
{
    private long _steal, _sessionLock, _userAltTab, _userClick, _userOther, _pipelinePressure;
    private long _droppedAtIngest;

    public long Steal => Volatile.Read(ref _steal);
    public long SessionLock => Volatile.Read(ref _sessionLock);
    public long UserAltTab => Volatile.Read(ref _userAltTab);
    public long UserClick => Volatile.Read(ref _userClick);
    public long UserOther => Volatile.Read(ref _userOther);
    public long PipelinePressure => Volatile.Read(ref _pipelinePressure);
    public long DroppedAtIngest => Volatile.Read(ref _droppedAtIngest);

    public void IncrementSteal() => Interlocked.Increment(ref _steal);
    public void IncrementSessionLock() => Interlocked.Increment(ref _sessionLock);
    public void IncrementUserAltTab() => Interlocked.Increment(ref _userAltTab);
    public void IncrementUserClick() => Interlocked.Increment(ref _userClick);
    public void IncrementUserOther() => Interlocked.Increment(ref _userOther);
    public void IncrementPipelinePressure() => Interlocked.Increment(ref _pipelinePressure);
    public void IncrementDroppedAtIngest() => Interlocked.Increment(ref _droppedAtIngest);
}
