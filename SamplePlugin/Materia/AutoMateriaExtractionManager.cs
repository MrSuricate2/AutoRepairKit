using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SamplePlugin.Materia;

/// <summary>
/// Watches equipped gear for items at 100% spiritbond with materia melded, opens the game's own
/// Materia Extraction window (confirmed via RepairMe's source: ActionManager.UseAction(GeneralAction, 14)),
/// and clicks the matching row in its list - confirmed live to be a single, instant, non-destructive
/// click (gear is kept, spiritbond resets, one materia is gained). One item per open/close cycle; the
/// normal polling loop reopens the window for the next eligible item a couple of seconds later, so a
/// full stack of spiritbonded gear gets cleared out piece by piece without further input.
/// </summary>
public sealed class AutoMateriaExtractionManager : IDisposable
{
    // Confirmed via https://github.com/chalkos/RepairMe (PluginUI.cs, GeneralActionIdMateriaExtraction).
    private const uint MateriaExtractionGeneralActionId = 14;

    // Confirmed live via Dalamud's Addon Inspector: the list window's native name is "Materialize".
    private const string ListAddonName = "Materialize";

    private static readonly ConditionFlag[] UnsafeConditions =
    [
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.TradeOpen,
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
    ];

    private enum State
    {
        Idle,
        Cooldown,
    }

    private readonly Plugin plugin;

    private State state = State.Idle;
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;

    /// <summary>
    /// True only right after we ourselves opened the list, so we never auto-click a window the player
    /// opened manually through the game's own General Actions menu.
    /// </summary>
    private bool expectingListClick;

    public string StatusText { get; private set; } = string.Empty;
    public bool IsActive => false;

    private readonly LinkedList<string> messageHistory = new();
    public IReadOnlyCollection<string> MessageHistory => messageHistory;

    private readonly List<string> debugLog = [];
    public IReadOnlyList<string> DebugLog => debugLog;

    public AutoMateriaExtractionManager(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ListAddonName, OnListSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ListAddonName, OnListSetup);
    }

    private void LogMessage(string message)
    {
        Plugin.ChatGui.Print($"[Auto-Repair] [Matéria] {message}");
        messageHistory.AddFirst($"{DateTime.Now:HH:mm:ss} — {message}");
        while (messageHistory.Count > 10)
            messageHistory.RemoveLast();
        LogDebug(message);
    }

    private void LogDebug(string message)
    {
        debugLog.Add($"{DateTime.Now:HH:mm:ss.fff} [{state}] {message}");
        while (debugLog.Count > 300)
            debugLog.RemoveAt(0);
    }

    public void ClearDebugLog() => debugLog.Clear();

    /// <summary>Lets the config window trigger an extraction pass immediately.</summary>
    public void RequestManualExtraction()
    {
        if (state != State.Idle)
            return;

        OpenExtractionWindow();
    }

    /// <summary>Closes the extraction window if it's open.</summary>
    public static unsafe void Cancel()
    {
        var agent = AgentMaterialize.Instance();
        if (agent != null)
            agent->Hide();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;

        switch (state)
        {
            case State.Cooldown:
                if (now >= cooldownUntil)
                {
                    state = State.Idle;
                    StatusText = string.Empty;
                }
                break;

            case State.Idle:
                if (now - lastCheck < TimeSpan.FromSeconds(2))
                    break;
                lastCheck = now;

                if (!ShouldConsiderExtraction())
                    break;

                if (MateriaCandidateFinder.TryFindExtractableItem(out _, out _, out _))
                    OpenExtractionWindow();
                break;
        }
    }

    private bool ShouldConsiderExtraction()
    {
        var config = plugin.Configuration;
        if (!config.AutoExtractMateriaEnabled)
            return false;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return false;

        return !Plugin.Condition.Any(UnsafeConditions);
    }

    private unsafe void OpenExtractionWindow()
    {
        if (!MateriaCandidateFinder.TryFindExtractableItem(out var index, out var itemId, out var itemName))
        {
            LogMessage("Aucune pièce à 100% de lien avec matéria mélangée.");
            EnterCooldown(TimeSpan.FromMinutes(2), string.Empty);
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            LogDebug("ActionManager.Instance() a retourné null.");
            EnterCooldown(TimeSpan.FromSeconds(30), "Erreur interne.");
            return;
        }

        LogDebug($"Ouverture de la liste d'extraction pour itemId={itemId} ({itemName}), index={index}.");
        expectingListClick = true;
        var result = actionManager->UseAction(ActionType.GeneralAction, MateriaExtractionGeneralActionId);
        LogDebug($"ActionManager.UseAction(GeneralAction, {MateriaExtractionGeneralActionId}) -> {result}");

        if (!result)
        {
            expectingListClick = false;
            LogMessage("Impossible d'ouvrir la fenêtre d'extraction de matéria (action générale refusée).");
            EnterCooldown(TimeSpan.FromSeconds(30), "Échec de l'ouverture.");
            return;
        }

        StatusText = $"Extraction en cours sur {itemName}...";
        EnterCooldown(TimeSpan.FromSeconds(5), StatusText);
    }

    private unsafe void OnListSetup(AddonEvent type, AddonArgs args)
    {
        LogDebug($"Addon '{ListAddonName}' ouvert (expectingListClick={expectingListClick}).");

        if (!expectingListClick)
            return;
        expectingListClick = false;

        if (!MateriaCandidateFinder.TryFindExtractableItem(out var index, out var itemId, out var itemName))
        {
            LogDebug("Plus aucune pièce éligible au moment où la liste s'est ouverte.");
            return;
        }

        var inventoryManager = InventoryManager.Instance();
        var itemPtr = inventoryManager != null ? inventoryManager->GetInventorySlot(InventoryType.EquippedItems, index) : null;
        if (itemPtr == null)
        {
            LogDebug("GetInventorySlot a retourné null pour l'item cible.");
            return;
        }

        var agent = AgentMaterialize.Instance();
        if (agent == null)
        {
            LogDebug("AgentMaterialize.Instance() a retourné null.");
            return;
        }

        // The list defaults to whichever category was last used (the user saw Armoury Chest items,
        // not Equipped) - row indices are only meaningful within the currently active category, so
        // force it to "Equipped" first. 6 is AgentMateriaAttach.FilterCategory.Equipped; AgentMaterialize
        // doesn't expose its own named enum but shares the same agent-generator lineage, so it's the
        // best available guess - confirmed or denied by the ItemCount/Category logged right after.
        const int equippedCategory = 6;
        if (agent->Category != equippedCategory)
        {
            var categoryBefore = agent->Category;
            var catRet = new AtkValue();
            var catValues = stackalloc AtkValue[2];
            catValues[0].Type = AtkValueType.Int;
            catValues[0].Int = 0;
            catValues[1].Type = AtkValueType.Int;
            catValues[1].Int = equippedCategory;
            agent->ReceiveEvent(&catRet, catValues, 2, 0);
            LogDebug($"Changement de catégorie: {categoryBefore} -> {agent->Category} (ItemCount={agent->ItemCount}).");
        }

        var rowIndex = -1;
        for (var i = 0; i < agent->ItemCount; i++)
        {
            var entry = agent->ItemsSorted[i].Value;
            if (entry != null && entry->Item == itemPtr)
            {
                rowIndex = i;
                break;
            }
        }

        if (rowIndex < 0)
        {
            LogDebug($"Item cible (itemId={itemId}) introuvable dans la liste Materialize ({agent->ItemCount} entrées).");
            return;
        }

        var spiritbondBefore = itemPtr->SpiritbondOrCollectability;

        // FireCallbackInt(rowIndex) on the addon was tried first and confirmed live to do nothing -
        // this is a real AtkComponentList, not a simple popup menu like SelectString. Selecting a row
        // goes through the owning Agent's ReceiveEvent instead, mirroring the confirmed-working
        // AgentMateriaAttach.SelectItem pattern from github.com/Jaksuhn/ffxiv-bundleoftweaks
        // (GettingTooAttached.cs): values = [1, rowIndex, 1, 0].
        var ret = new AtkValue();
        var values = stackalloc AtkValue[4];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 1;
        values[1].Type = AtkValueType.Int;
        values[1].Int = rowIndex;
        values[2].Type = AtkValueType.Int;
        values[2].Int = 1;
        values[3].Type = AtkValueType.Int;
        values[3].Int = 0;
        agent->ReceiveEvent(&ret, values, 4, 0);

        // Re-fetch (don't trust the old pointer) and verify against real, observable state instead of
        // just assuming the click worked - the previous version logged "success" unconditionally here,
        // which turned out to be false.
        var itemPtrAfter = InventoryManager.Instance()->GetInventorySlot(InventoryType.EquippedItems, index);
        var spiritbondAfter = itemPtrAfter != null ? itemPtrAfter->SpiritbondOrCollectability : spiritbondBefore;
        LogDebug($"ReceiveEvent(select, row={rowIndex}) envoyé. Symbiose avant={spiritbondBefore}, après={spiritbondAfter}.");

        if (spiritbondAfter < spiritbondBefore)
            LogMessage($"Matéria extraite de {itemName}.");
        else
            LogMessage($"Le clic sur {itemName} n'a rien changé (symbiose toujours à {spiritbondAfter / 100f:0}%) - la sélection n'a probablement pas fonctionné.");
    }

    private void EnterCooldown(TimeSpan duration, string statusMessage)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = statusMessage;
    }
}
