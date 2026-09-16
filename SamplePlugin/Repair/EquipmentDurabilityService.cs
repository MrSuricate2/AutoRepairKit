using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace SamplePlugin.Repair;

/// <summary>
/// Reads the durability/spiritbond of the player's currently equipped gear directly from the
/// game's inventory memory (FFXIVClientStructs), the same data source the native repair windows use.
/// </summary>
public static class EquipmentDurabilityService
{
    // Slot 5 (legacy Waist) and slot 13 (Soul Crystal) never carry durability.
    private static readonly HashSet<uint> NonRepairableSlotIndices = [5, 13];

    /// <summary>
    /// Returns condition info for every equipped, repairable item. Empty if the player isn't logged in
    /// or the equipped-items container isn't loaded yet.
    /// </summary>
    public static unsafe List<RepairSlotInfo> GetEquippedGearCondition()
    {
        var result = new List<RepairSlotInfo>();

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return result;

        var container = inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded)
            return result;

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        for (var i = 0; i < container->Size; i++)
        {
            if (NonRepairableSlotIndices.Contains((uint)i))
                continue;

            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;

            if (!itemSheet.TryGetRow(slot->ItemId, out var itemRow))
                continue;

            result.Add(new RepairSlotInfo(
                (uint)i,
                slot->ItemId,
                itemRow.Icon,
                itemRow.Name.ToString(),
                slot->GetConditionPercentage(),
                slot->SpiritbondOrCollectability / 100f));
        }

        return result;
    }

    /// <summary>
    /// Lowest condition percentage across all equipped, repairable gear, or 100 if nothing is equipped.
    /// </summary>
    public static float GetLowestConditionPercent()
    {
        var lowest = 100f;
        foreach (var slot in GetEquippedGearCondition())
        {
            if (slot.ConditionPercent < lowest)
                lowest = slot.ConditionPercent;
        }

        return lowest;
    }
}
