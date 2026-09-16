using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace SamplePlugin.Repair;

/// <summary>
/// Locates Dark Matter items (any tier: Dark Matter, Dark Matter Cluster, Grade N Dark Matter, ...)
/// in the player's inventory. Rather than hardcoding item ids (which get added to every time a new
/// tier ships, and would silently rot), this matches by localized name prefix so it keeps working
/// across expansions and client languages without an update.
/// </summary>
public static class DarkMatterFinder
{
    private static readonly string[] NamePrefixes =
    [
        "Dark Matter",       // English
        "Matière sombre",    // French
        "Dunkle Materie",    // German
        "ダークマター",              // Japanese
    ];

    private static readonly InventoryType[] SearchBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static HashSet<uint>? cachedItemIds;

    private static HashSet<uint> GetDarkMatterItemIds()
    {
        if (cachedItemIds != null)
            return cachedItemIds;

        var ids = new HashSet<uint>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
        {
            var name = item.Name.ToString();
            if (name.Length == 0)
                continue;

            if (NamePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                ids.Add(item.RowId);
        }

        cachedItemIds = ids;
        return ids;
    }

    /// <summary>
    /// Finds the best Dark Matter item id currently carried in the player's main inventory (not the
    /// armory/saddlebag). "Best" = highest-tier owned, since a higher grade can always repair gear a
    /// lower grade can't, while a lower grade simply can't touch current-expansion item levels.
    /// Only the item id is needed: <see cref="FFXIVClientStructs.FFXIV.Client.Game.ActionManager.UseAction"/>
    /// resolves the actual bag/slot itself, the same way a hotbar-slotted item or a "/item" macro does.
    /// </summary>
    public static unsafe bool TryFindBestItemId(out uint itemId)
    {
        itemId = 0;

        var ids = GetDarkMatterItemIds();
        if (ids.Count == 0)
            return false;

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        uint bestItemId = 0;

        foreach (var bag in SearchBags)
        {
            var inv = inventoryManager->GetInventoryContainer(bag);
            if (inv == null || !inv->IsLoaded)
                continue;

            for (var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if (item == null || item->ItemId == 0 || !ids.Contains(item->ItemId))
                    continue;

                // Higher item ids correspond to newer (higher tier) Dark Matter releases.
                if (item->ItemId > bestItemId)
                    bestItemId = item->ItemId;
            }
        }

        if (bestItemId == 0)
            return false;

        itemId = bestItemId;
        return true;
    }
}
