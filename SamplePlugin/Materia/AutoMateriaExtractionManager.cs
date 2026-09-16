using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace SamplePlugin.Materia;

/// <summary>
/// Watches equipped gear for items at 100% spiritbond with materia melded, and opens the game's own
/// Materia Extraction window for you - confirmed via RepairMe's own source, which uses the exact same
/// call as a "jump straight to it" shortcut: ActionManager.UseAction(GeneralAction, 14).
///
/// Selecting an item and confirming extraction inside that window is still a manual, in-game step.
/// No reference implementation (not even RepairMe) drives that part programmatically, and after three
/// wrong guesses at the lower-level MaterializeItem/MaterializeEntryId API - one of which had a real,
/// if non-destructive, effect on the user's gear - it's not worth guessing a fourth time against real
/// equipment. This still saves the trip through the General Actions menu, which is most of the value.
/// </summary>
public sealed class AutoMateriaExtractionManager : IDisposable
{
    // Confirmed via https://github.com/chalkos/RepairMe (PluginUI.cs, GeneralActionIdMateriaExtraction).
    private const uint MateriaExtractionGeneralActionId = 14;

    // FFXIVClientStructs class name AddonMaterializeDialog -> native addon name "MaterializeDialog".
    // Only listened to for logging here - see the class remarks above for why nothing gets auto-clicked.
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
        Cooldown,
    }

    private readonly Plugin plugin;

    private State state = State.Idle;
    private DateTime lastCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;

    public string StatusText { get; private set; } = string.Empty;

    /// <summary>Always false: opening the window is a one-shot action, there's nothing ongoing to cancel.</summary>
    public bool IsActive => false;

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

    /// <summary>Lets the config window open the extraction window immediately.</summary>
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

        LogDebug($"Ouverture de la fenêtre d'extraction pour itemId={itemId} ({itemName}), index={index}.");
        var result = actionManager->UseAction(ActionType.GeneralAction, MateriaExtractionGeneralActionId);
        LogDebug($"ActionManager.UseAction(GeneralAction, {MateriaExtractionGeneralActionId}) -> {result}");

        if (result)
            LogMessage($"Fenêtre d'extraction ouverte - {itemName} est prête (100% de lien). Sélectionnez-la et confirmez en jeu.");
        else
            LogMessage("Impossible d'ouvrir la fenêtre d'extraction de matéria (action générale refusée).");

        EnterCooldown(TimeSpan.FromMinutes(2), result ? "Fenêtre ouverte, à finaliser en jeu." : "Échec de l'ouverture.");
    }

    /// <summary>Logging only - see class remarks for why this never clicks anything.</summary>
    private unsafe void OnDialogSetup(AddonEvent type, AddonArgs args)
    {
        var addon = (AddonMaterializeDialog*)(void*)args.Addon.Address;
        if (addon == null)
            return;

        var dialogText = addon->Text != null ? addon->Text->NodeText.ToString() : "(texte introuvable)";
        var itemNameText = addon->ItemName != null ? addon->ItemName->NodeText.ToString() : "(nom introuvable)";
        LogDebug($"Addon '{DialogAddonName}' ouvert - texte='{dialogText}' item='{itemNameText}'.");
    }

    private void EnterCooldown(TimeSpan duration, string statusMessage)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = statusMessage;
    }
}
