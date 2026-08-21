namespace Lodge.Core.Domain.Enums;

/// <summary>
/// What kind of reconciliation event an action responds to. STATE actions confirm a
/// scalar signal's current value; ADD/DELETE/MODIFY confirm one item of a keyed
/// collection (or scalar list) being present, absent, or reshaped.
/// </summary>
public enum SignalTrigger
{
    /// <summary>The signal's current scalar value (state-match rule).</summary>
    STATE,

    /// <summary>An item key that is present in desired state but not confirmed present.</summary>
    ADD,

    /// <summary>An item key that is confirmed present but no longer in desired state.</summary>
    DELETE,

    /// <summary>An item key whose desired body differs from its last confirmed body. Informational drift only: never gates a capability's active state.</summary>
    MODIFY
}
