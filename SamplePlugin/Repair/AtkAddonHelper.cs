using System;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace SamplePlugin.Repair;

/// <summary>
/// Small helpers for driving the native "SelectString"/"SelectIconString" list addons, used to
/// auto-pick the "repair" option out of a repair NPC's shop menu. Matching is done by keyword
/// against several client languages rather than by fixed index, since menu order/content differs
/// per NPC and per game patch.
/// </summary>
public static class AtkAddonHelper
{
    private static readonly string[] RepairKeywords =
    [
        "repair",       // English
        "réparation",   // French
        "réparer",
        "reparieren",   // German
        "instandsetzen",
        "修理",           // Japanese
    ];

    private static bool ContainsRepairKeyword(string entryText)
    {
        foreach (var keyword in RepairKeywords)
        {
            if (entryText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Looks for a "repair" entry in an open SelectString list and clicks it. Returns true if clicked.</summary>
    public static unsafe bool TryClickRepairEntry(AddonSelectString* addon)
    {
        if (addon == null)
            return false;

        var popup = addon->PopupMenu;
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var text = popup.EntryNames[i].ToString();
            if (!ContainsRepairKeyword(text))
                continue;

            addon->AtkUnitBase.FireCallbackInt(i);
            return true;
        }

        return false;
    }

    /// <summary>Same as <see cref="TryClickRepairEntry(AddonSelectString*)"/> but for the icon-list variant.</summary>
    public static unsafe bool TryClickRepairEntry(AddonSelectIconString* addon)
    {
        if (addon == null)
            return false;

        var popup = addon->PopupMenu;
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var text = popup.EntryNames[i].ToString();
            if (!ContainsRepairKeyword(text))
                continue;

            addon->AtkUnitBase.FireCallbackInt(i);
            return true;
        }

        return false;
    }
}
