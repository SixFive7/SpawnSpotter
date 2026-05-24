using SpawnSpotter.Events;

namespace SpawnSpotter.Classifier;

/// <summary>
/// Pure output of <see cref="FocusClassifier.Classify"/>.
/// </summary>
public readonly record struct ClassifierResult(
    Classification Classification,
    string Note,
    // Locked-anchor view to write to the log row for this event:
    IntPtr LockedHwndBefore,
    uint LockedPidBefore,
    // Bookkeeping the caller applies after recording the row:
    bool UpdateLockedAnchor,
    bool ClearLockedAnchor,
    // Whether this row should be suppressed from the output entirely (e.g. ignore-filter drop).
    bool DropFromLog);
