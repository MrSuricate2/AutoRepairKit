using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace SamplePlugin.Materia;

/// <summary>
/// Watches equipped gear for items at 100% spiritbond with materia melded, and extracts the materia
/// automatically (or on demand). Extraction destroys the item, so until the exact native entry point
/// is confirmed against a live game, this deliberately stops short of auto-confirming the game's own
/// Yes/No dialog - it opens it and logs its exact text, but a human has to click it.
/// </summary>
public sealed class AutoMateriaExtractionManager : IDisposable
{
    // FFXIVClientStructs class name AddonMaterializeDialog -> native addon name "MaterializeDialog".
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
        WaitingForDialog,
        Cooldown,
    }

    private readonly Plugin plugin;

    private State state = State.Idle;
    private DateTime stateEnteredAt = DateTime.MinValue;
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;
    private string pendingItemName = string.Empty;

    public string StatusText { get; private set; } = string.Empty;
    public bool IsActive => state == State.WaitingForDialog;

    private readonly LinkedList<string> messageHistory = new();
    public IReadOnlyCollection<string> MessageHistory => messageHistory;

    private readonly List<string> debugLog = [];
    public IReadOnlyList<string> DebugLog => debugLog;

    public AutoMateriaExtractionManager(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, DialogAddonName, OnDialogSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
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

    /// <summary>Lets the config window trigger an extraction attempt immediately.</summary>
    public void RequestManualExtraction()
    {
        if (state != State.Idle && state != State.Cooldown)
            return;

        StartExtraction();
    }

    /// <summary>If a confirmation dialog is currently up, decline it instead of confirming.</summary>
    public unsafe void Cancel()
    {
        if (state != State.WaitingForDialog)
            return;

        var addon = Plugin.GameGui.GetAddonByName<AddonMaterializeDialog>(DialogAddonName, 1);
        if (addon != null)
        {
            addon->AtkUnitBase.FireCallbackInt(1);
            addon->AtkUnitBase.Close(true);
        }

        LogMessage("Extraction annulée par l'utilisateur.");
        EnterCooldown(TimeSpan.FromSeconds(15), "Annulé.");
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
                    StartExtraction();
                break;

            case State.WaitingForDialog:
                if (now - stateEnteredAt > TimeSpan.FromSeconds(8))
                {
                    const string msg = "La boîte de confirmation d'extraction ne s'est pas ouverte, réessai plus tard.";
                    LogMessage(msg);
                    EnterCooldown(TimeSpan.FromSeconds(30), msg);
                }
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

    private unsafe void StartExtraction()
    {
        if (!MateriaCandidateFinder.TryFindExtractableItem(out var index, out var itemId, out var itemName))
        {
            LogMessage("Aucune pièce à 100% de lien avec matéria mélangée.");
            EnterCooldown(TimeSpan.FromMinutes(2), string.Empty);
            return;
        }

        var inventoryManager = InventoryManager.Instance();
        var itemPtr = inventoryManager != null ? inventoryManager->GetInventorySlot(InventoryType.EquippedItems, index) : null;
        if (itemPtr == null)
        {
            LogDebug("GetInventorySlot a retourné null juste avant l'appel MaterializeItem.");
            EnterCooldown(TimeSpan.FromSeconds(30), "Erreur interne.");
            return;
        }

        var eventFramework = EventFramework.Instance();
        if (eventFramework == null)
        {
            LogDebug("EventFramework.Instance() a retourné null.");
            EnterCooldown(TimeSpan.FromSeconds(30), "Impossible d'accéder au module d'extraction.");
            return;
        }

        // NB: of the 3 MaterializeEntryId values, Retrieve turned out to be a different, non-destructive
        // "remove a melded materia" service (silently pulled one off, no dialog, no spiritbond change),
        // and Desynth did nothing at all (likely the unrelated, skill-gated Desynthesis feature). Purify
        // is what's left; unconfirmed until a live test proves it out (see OnDialogSetup below, which
        // reads out the dialog's own text instead of auto-confirming).
        LogDebug($"MaterializeItem: itemId={itemId} ({itemName}), index={index}, entry=Purify");
        eventFramework->MaterializeItem(itemPtr, MaterializeEntryId.Purify);

        pendingItemName = itemName;
        StatusText = $"Extraction en cours sur {itemName}...";
        state = State.WaitingForDialog;
        stateEnteredAt = DateTime.Now;
    }

    private unsafe void OnDialogSetup(AddonEvent type, AddonArgs args)
    {
        LogDebug($"Addon '{DialogAddonName}' PostSetup reçu (state={state}).");

        if (state != State.WaitingForDialog)
        {
            LogDebug("Ignoré: pas en attente de confirmation d'extraction.");
            return;
        }

        var addon = (AddonMaterializeDialog*)(void*)args.Addon.Address;
        if (addon == null)
            return;

        // Verification step: read out exactly what the game is asking before we ever auto-click
        // anything again, given Retrieve's wrong guess already had a real (if non-destructive) effect.
        var dialogText = addon->Text != null ? addon->Text->NodeText.ToString() : "(texte introuvable)";
        var itemNameText = addon->ItemName != null ? addon->ItemName->NodeText.ToString() : "(nom introuvable)";
        LogDebug($"Contenu de la boîte: texte='{dialogText}' item='{itemNameText}'");

        const string msg = "Boîte de confirmation ouverte - à confirmer manuellement en jeu pour cette vérification.";
        LogMessage(msg);
        EnterCooldown(TimeSpan.FromMinutes(2), msg);
    }

    private void EnterCooldown(TimeSpan duration, string statusMessage)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = statusMessage;
    }
}
