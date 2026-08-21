namespace Lodge.Core.Domain.Entities;

/// <summary>
/// One key/value application setting persisted in the database so it can be changed
/// from the UI at runtime (e.g. the reconciliation loop interval and enabled flag).
/// </summary>
public class Setting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
