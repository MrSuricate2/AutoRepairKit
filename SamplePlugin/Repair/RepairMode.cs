namespace SamplePlugin.Repair;

/// <summary>
/// How the plugin should repair the player's equipment once it drops below the configured threshold.
/// </summary>
public enum RepairMode
{
    /// <summary>Auto-repair is disabled entirely.</summary>
    Off,

    /// <summary>Self-repair using Dark Matter items carried in the player's inventory.</summary>
    DarkMatter,

    /// <summary>Walk to a configured repair NPC (via vnavmesh) and repair there, free of charge.</summary>
    Npc,
}
