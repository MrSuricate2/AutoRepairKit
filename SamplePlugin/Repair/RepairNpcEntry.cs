using System;
using System.Numerics;

namespace SamplePlugin.Repair;

/// <summary>
/// A repair NPC the player has registered (via "Capture current target" in the config window).
/// We deliberately don't ship a hardcoded list of NPC positions: those shift between patches and
/// depend on which vendor the player actually trusts/uses, so the user points the plugin at their
/// target once in-game and we remember it.
/// </summary>
[Serializable]
public class RepairNpcEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public uint DataId { get; set; }
    public ushort TerritoryId { get; set; }
    public Vector3 Position { get; set; }
}
