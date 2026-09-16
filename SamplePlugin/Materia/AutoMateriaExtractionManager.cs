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
/// and clicks through it - confirmed non-destructive live (gear is kept, spiritbond resets, one materia
/// is gained). One item per open/close cycle; the normal polling loop reopens the window for the next
/// eligible item a couple of seconds later, so a full stack of spiritbonded gear clears out piece by
/// piece without further input.
///
/// The actual click sequence (FireCallback with a specific AtkValue pair, no row/index lookup needed)
/// is lifted from PunishXIV/Artisan's Spiritbond.cs (RawInformation/Spiritbond.cs,
/// ExtractFirstMateria/ConfirmMateriaDialog) - a real, widely-used plugin that already solved this;
/// three earlier from-scratch attempts here (FireCallbackInt on the addon, ReceiveEvent on the agent
/// with a row index, forcing the agent's category) all verifiably did nothing.
/// </summary>
public sealed class AutoMateriaExtractionManager : IDisposable
{
    // Confirmed via https://github.com/chalkos/RepairMe (PluginUI.cs, GeneralActionIdMateriaExtraction).
    private const uint MateriaExtractionGeneralActionId = 14;

    // Confirmed live via Dalamud's Addon Inspector: the list window's native name is "Materialize".
    private const string ListAddonName = "Materialize";
    private const string DialogAddonName = "MaterializeDialog";

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
        WaitingToClick,
        WaitingToVerify,
        Cooldown,
    }

    private readonly Plugin plugin;

    private State state = State.Idle;
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;
    private DateTime clickAt = DateTime.MinValue;
    private DateTime verifyAt = DateTime.MinValue;

    /// <summary>
    /// True only right after we ourselves opened the list, so we never auto-click a window the player
    /// opened manually through the game's own General Actions menu.
    /// </summary>
    private bool expectingListClick;
    private int eligibleCountBeforeClick;
    private string trackedItemName = string.Empty;

    /// <summary>
    /// Set by the manual "Extraire maintenant" button; keeps the polling loop going (independently of
    /// the auto-enable setting) until every currently-ready piece has been processed, instead of
    /// requiring one button click per item.
    /// </summary>
    private bool manualBatchActive;

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
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, DialogAddonName, OnDialogSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ListAddonName, OnListSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, DialogAddonName, OnDialogSetup);
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

    /// <summary>
    /// Lets the config window trigger an extraction pass immediately, and keeps going on its own
    /// (every ~2s, via the normal polling loop) until no eligible piece remains.
    /// </summary>
    public void RequestManualExtraction()
    {
        manualBatchActive = true;

        if (state == State.Idle)
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

                if (!ShouldConsiderExtraction() && !manualBatchActive)
                    break;

                if (MateriaCandidateFinder.TryFindExtractableItem(out _, out _, out _))
                    OpenExtractionWindow();
                else
                    manualBatchActive = false;
                break;

            case State.WaitingToClick:
                if (now >= clickAt)
                    PerformClick();
                break;

            case State.WaitingToVerify:
                if (now >= verifyAt)
                    VerifyAndReport();
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
            manualBatchActive = false;
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
        trackedItemName = itemName;
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

    private void OnListSetup(AddonEvent type, AddonArgs args)
    {
        LogDebug($"Addon '{ListAddonName}' ouvert (expectingListClick={expectingListClick}).");

        if (!expectingListClick)
            return;
        expectingListClick = false;

        // The window picks its own default selection when several pieces qualify at once - not
        // necessarily the one OpenExtractionWindow() found first - so track the total eligible count
        // rather than one specific slot; see MateriaCandidateFinder.CountExtractableItems.
        eligibleCountBeforeClick = MateriaCandidateFinder.CountExtractableItems();

        // Artisan's own polling waits ~500ms between opening the list and clicking - the list's
        // backing data likely isn't fully populated yet on the same tick PostSetup fires. Clicking
        // immediately (the first thing tried here) verifiably did nothing.
        clickAt = DateTime.Now.AddMilliseconds(500);
        state = State.WaitingToClick;
        LogDebug("Attente ~500ms avant le clic (le temps que la liste se charge).");
    }

    private unsafe void PerformClick()
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(ListAddonName, 1);
        if (addon == null)
        {
            LogDebug($"Addon '{ListAddonName}' introuvable au moment du clic (fermé entre-temps ?).");
            EnterCooldown(TimeSpan.FromSeconds(10), string.Empty);
            return;
        }

        // PunishXIV/Artisan's exact call (RawInformation/Spiritbond.cs, ExtractFirstMateria): no row
        // lookup needed, just this fixed FireCallback.
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = 0 };
        addon->FireCallback(1, values);
        LogDebug("FireCallback(1, [Int=2, UInt=0]) envoyé sur 'Materialize' (méthode Artisan).");

        // Extraction is presumably server-validated (it changes real inventory/item state), so the
        // local eligible-item count doesn't update instantly - checking 1ms after the click showed
        // "no change" even when the extraction genuinely went through. Give it a moment.
        verifyAt = DateTime.Now.AddSeconds(1.5);
        state = State.WaitingToVerify;
    }

    /// <summary>
    /// Artisan also handles a MaterializeDialog confirmation appearing after the click (rare per live
    /// testing here - the user saw an instant, dialog-less extraction - but kept as a safety net).
    /// </summary>
    private unsafe void OnDialogSetup(AddonEvent type, AddonArgs args)
    {
        LogDebug($"Addon '{DialogAddonName}' ouvert.");

        var addon = (AtkUnitBase*)(void*)args.Addon.Address;
        if (addon == null)
            return;

        addon->FireCallbackInt(0);
        LogDebug("Confirmation 'Oui' envoyée sur MaterializeDialog.");

        verifyAt = DateTime.Now.AddSeconds(1.5);
        state = State.WaitingToVerify;
    }

    private void VerifyAndReport()
    {
        var eligibleCountAfter = MateriaCandidateFinder.CountExtractableItems();
        LogDebug($"Pièces éligibles avant={eligibleCountBeforeClick}, après={eligibleCountAfter}.");

        if (eligibleCountAfter < eligibleCountBeforeClick)
        {
            LogMessage($"Matéria extraite ({trackedItemName} ou une autre pièce prête - {eligibleCountAfter} restante(s)).");
            EnterCooldown(TimeSpan.FromSeconds(10), "Matéria extraite.");
        }
        else if (eligibleCountBeforeClick > 0)
        {
            LogMessage("Le clic n'a extrait aucune matéria (aucun changement détecté).");
            EnterCooldown(TimeSpan.FromSeconds(30), "Échec du clic.");
        }
        else
        {
            EnterCooldown(TimeSpan.FromSeconds(10), string.Empty);
        }
    }

    private void EnterCooldown(TimeSpan duration, string statusMessage)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = statusMessage;
    }
}
