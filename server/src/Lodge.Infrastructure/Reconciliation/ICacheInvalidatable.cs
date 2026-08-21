namespace Lodge.Infrastructure.Reconciliation;

/// <summary>
/// A provider whose file-backed cache can be dropped so the next read hits disk again.
/// The reconciliation coordinator invalidates these at the start of every cycle, so
/// edits to <c>mapping/</c> files are picked up continuously, not just at startup.
/// </summary>
public interface ICacheInvalidatable
{
    void Invalidate();
}
