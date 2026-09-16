namespace SamplePlugin.Repair;

/// <summary>
/// Snapshot of a single equipped item's condition, used both by the auto-repair threshold check
/// and by the visual gauge.
/// </summary>
/// <param name="EquipSlotIndex">Index into the EquippedItems inventory container (0-13).</param>
/// <param name="ItemId">The equipped item's row id in the Item sheet.</param>
/// <param name="IconId">Icon id to render for this item.</param>
/// <param name="Name">Item name, used for tooltips.</param>
/// <param name="ConditionPercent">0-100, where 100 is pristine and 0 is fully broken.</param>
/// <param name="SpiritbondPercent">0-100 spiritbond progress.</param>
public readonly record struct RepairSlotInfo(
    uint EquipSlotIndex,
    uint ItemId,
    uint IconId,
    string Name,
    float ConditionPercent,
    float SpiritbondPercent);
