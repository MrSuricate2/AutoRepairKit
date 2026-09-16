using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace SamplePlugin.Materia;

/// <summary>
/// Finds an equipped item eligible for materia extraction: spiritbond at 100% and at least one
/// materia melded. Extraction destroys the item, so this deliberately only ever looks at equipped
/// gear (not stray items sitting in bags) and only ever returns one candidate at a time.
/// </summary>
public static class MateriaCandidateFinder
{
    // Slot 5 (legacy Waist) and slot 13 (Soul Crystal) can't carry materia.
    private static readonly HashSet<uint> ExcludedSlotIndices = [5, 13];

    public static unsafe bool TryFindExtractableItem(out int index, out uint itemId, out string itemName)
    {
        index = -1;
        itemId = 0;
        itemName = string.Empty;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        var container = inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded)
            return false;

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();

        for (var i = 0; i < container->Size; i++)
        {
            if (ExcludedSlotIndices.Contains((uint)i))
                continue;

            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;

            // SpiritbondOrCollectability is 0-10000, where 10000 == 100.00%.
            if (item->SpiritbondOrCollectability < 10000)
                continue;

            if (item->GetMateriaCount() == 0)
                continue;

            index = i;
            itemId = item->ItemId;
            itemName = itemSheet.TryGetRow(item->ItemId, out var row) ? row.Name.ToString() : $"Item {item->ItemId}";
            return true;
        }

        return false;
    }

    /// <summary>
    /// How many equipped pieces are currently eligible. The native extraction window picks its own
    /// default selection when several pieces qualify at once - not necessarily the one this class would
    /// have picked first - so verifying "did extraction do *something*" needs a count across all
    /// equipped gear, not a single tracked slot.
    /// </summary>
    public static unsafe int CountExtractableItems()
    {
        var count = 0;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return 0;

        var container = inventoryManager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded)
            return 0;

        for (var i = 0; i < container->Size; i++)
        {
            if (ExcludedSlotIndices.Contains((uint)i))
                continue;

            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0)
                continue;

            if (item->SpiritbondOrCollectability < 10000)
                continue;

            if (item->GetMateriaCount() == 0)
                continue;

            count++;
        }

        return count;
    }
}
