using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using SamplePlugin.Repair;

namespace SamplePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;

    // --- Auto-repair ---

    /// <summary>Master switch for the whole auto-repair feature.</summary>
    public bool AutoRepairEnabled { get; set; } = false;

    /// <summary>Which method to use when a repair is triggered.</summary>
    public RepairMode Mode { get; set; } = RepairMode.DarkMatter;

    /// <summary>
    /// Repair triggers as soon as the worst-condition equipped item drops below this percentage (0-100).
    /// Nothing is repaired, and no Dark Matter is consumed, while every piece stays above this value.
    /// </summary>
    public int RepairThresholdPercent { get; set; } = 30;

    public bool PauseInCombat { get; set; } = true;
    public bool PauseWhileCraftingOrGathering { get; set; } = true;
    public bool PauseDuringCutscene { get; set; } = true;
    public bool PauseInDuty { get; set; } = true;

    /// <summary>
    /// If Dark Matter repair fails (none left, or none compatible with the equipment's ilvl), try the
    /// registered repair NPC for the current zone instead of just giving up. No-ops silently if the
    /// mode is already NPC, or if no NPC is registered for the zone (that failure gets its own message).
    /// </summary>
    public bool FallBackToNpcWhenOutOfDarkMatter { get; set; } = false;

    /// <summary>Registered "walk here and repair" NPCs.</summary>
    public List<RepairNpcEntry> RepairNpcs { get; set; } = [];

    // --- Materia extraction ---

    /// <summary>
    /// Auto-extract materia from any equipped item that reaches 100% spiritbond with materia melded.
    /// Off by default: this destroys the item, so it's opt-in even more deliberately than repair.
    /// </summary>
    public bool AutoExtractMateriaEnabled { get; set; } = false;

    /// <summary>Which registered NPC to use for a given TerritoryId, when more than one is registered for it.</summary>
    public Dictionary<ushort, Guid> PreferredRepairNpcByTerritory { get; set; } = [];

    // --- Visual gauge (RepairMe-style) ---

    public bool ShowGauge { get; set; } = true;
    public bool GaugeLocked { get; set; } = false;
    public Vector2 GaugePosition { get; set; } = new(200, 200);
    public GaugeStyle GaugeStyle { get; set; } = GaugeStyle.Bar;

    /// <summary>Gauge segments turn orange below this % and red below <see cref="GaugeCriticalPercent"/>.</summary>
    public int GaugeWarningPercent { get; set; } = 50;
    public int GaugeCriticalPercent { get; set; } = 30;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
