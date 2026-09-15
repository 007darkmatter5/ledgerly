namespace Ledgerly.Services;

/// <summary>
/// Changes whenever all data is replaced by a restore, so caches kept for a browser session (like the active
/// ledger) are dropped instead of pointing at ids that now belong to someone else.
/// </summary>
public class DataGeneration
{
    private int _value;

    public int Value => _value;

    public void Increment() => Interlocked.Increment(ref _value);
}
