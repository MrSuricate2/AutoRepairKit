namespace SamplePlugin.Repair;

/// <summary>
/// RepairMe ships two gauge layouts; we mirror both so switching feels familiar.
/// </summary>
public enum GaugeStyle
{
    /// <summary>Global progress bar, expandable into a per-slot list of bars.</summary>
    Bar,

    /// <summary>Compact row of equipment icons, tinted/bordered by condition.</summary>
    Icons,
}
