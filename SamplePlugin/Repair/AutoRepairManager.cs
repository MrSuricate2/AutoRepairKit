using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace SamplePlugin.Repair;

/// <summary>
/// State machine that watches equipped-gear condition and, once it drops below the configured
/// threshold, carries out a repair using either Dark Matter (self-repair) or a registered repair NPC.
///
/// The NPC path drives the game's own repair UI (the same addons/functions the native windows use),
/// so this needs the exact native addon names and vnavmesh's IPC surface to keep matching the
/// running game/plugin versions. See <see cref="VNavmeshIpc"/> and <see cref="AtkAddonHelper"/>
/// for the pieces most likely to need adjustment after a patch.
/// </summary>
public sealed class AutoRepairManager : IDisposable
{
    private const float InteractDistance = 3.5f;
    private static readonly ConditionFlag[] UnsafeConditions =
    [
        ConditionFlag.InCombat,
        ConditionFlag.Casting,
        ConditionFlag.Casting87,
        ConditionFlag.Crafting,
        ConditionFlag.ExecutingCraftingAction,
        ConditionFlag.PreparingToCraft,
        ConditionFlag.Gathering,
        ConditionFlag.ExecutingGatheringAction,
        ConditionFlag.Fishing,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.WatchingCutscene,
        ConditionFlag.WatchingCutscene78,
        ConditionFlag.TradeOpen,
    ];

    private enum State
    {
        Idle,
        MovingToNpc,
        WaitingForMenu,
        WaitingForRepairWindow,
        Cooldown,
    }

    private readonly Plugin plugin;
    private readonly VNavmeshIpc navmesh = new();

    private State state = State.Idle;
    private DateTime stateEnteredAt = DateTime.MinValue;
    private DateTime lastThresholdCheck = DateTime.MinValue;
    private DateTime cooldownUntil = DateTime.MinValue;
    private RepairMode activeMode = RepairMode.Off;
    private RepairNpcEntry? targetNpc;
    private int moveRetries;

    // FFXIVClientStructs class names are always "Addon" + the real native addon name used by
    // IAddonLifecycle/GetAddonByName. AddonRepair -> "Repair" (this one was wrongly left as
    // "AddonRepair" in an earlier version, which meant the listener below never fired).
    private const string RepairAddonName = "Repair";

    /// <summary>Exposed so the gauge/config windows can show what the manager is currently doing.</summary>
    public string StatusText { get; private set; } = string.Empty;

    /// <summary>True while a repair sequence is actively running (movement/menu/window), so the UI can offer a Cancel button.</summary>
    public bool IsActive => state is State.MovingToNpc or State.WaitingForMenu or State.WaitingForRepairWindow;

    private readonly LinkedList<string> messageHistory = new();

    /// <summary>Last few status/error messages, most recent first, so nothing gets lost in the chat log.</summary>
    public IReadOnlyCollection<string> MessageHistory => messageHistory;

    public AutoRepairManager(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RepairAddonName, OnRepairAddonSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectIconString", OnSelectIconStringSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, RepairAddonName, OnRepairAddonSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectIconString", OnSelectIconStringSetup);
    }

    private void LogMessage(string message)
    {
        Plugin.ChatGui.Print($"[Auto-Repair] {message}");
        AddHistory(message);
    }

    private void AddHistory(string message)
    {
        messageHistory.AddFirst($"{DateTime.Now:HH:mm:ss} — {message}");
        while (messageHistory.Count > 10)
            messageHistory.RemoveLast();
    }

    /// <summary>Lets the config window trigger a repair immediately, bypassing the threshold check.</summary>
    public void RequestManualRepair()
    {
        if (state != State.Idle && state != State.Cooldown)
            return;

        StartRepair();
    }

    /// <summary>Aborts whatever the manager is currently doing (movement, waiting on a menu/window, ...).</summary>
    public void Cancel()
    {
        if (!IsActive)
            return;

        navmesh.Stop();
        AddHistory("Annulé par l'utilisateur.");
        EnterCooldown(TimeSpan.FromSeconds(20), "Annulé.");
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
                if (now - lastThresholdCheck < TimeSpan.FromSeconds(2))
                    break;
                lastThresholdCheck = now;

                if (!ShouldConsiderRepair())
                    break;

                var lowest = EquipmentDurabilityService.GetLowestConditionPercent();
                if (lowest < plugin.Configuration.RepairThresholdPercent)
                    StartRepair();
                break;

            case State.MovingToNpc:
                TickMovingToNpc(now);
                break;

            case State.WaitingForMenu:
                if (now - stateEnteredAt > TimeSpan.FromSeconds(8))
                {
                    const string msg = "Le PNJ n'a pas proposé de réparation dans le délai imparti, réparation manuelle nécessaire cette fois-ci.";
                    LogMessage(msg);
                    EnterCooldown(TimeSpan.FromSeconds(30), msg);
                }
                break;

            case State.WaitingForRepairWindow:
                if (now - stateEnteredAt > TimeSpan.FromSeconds(8))
                {
                    const string msg = "La fenêtre de réparation ne s'est pas ouverte, réessai plus tard.";
                    LogMessage(msg);
                    EnterCooldown(TimeSpan.FromSeconds(30), msg);
                }
                break;
        }
    }

    private bool ShouldConsiderRepair()
    {
        var config = plugin.Configuration;
        if (!config.AutoRepairEnabled || config.Mode == RepairMode.Off)
            return false;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return false;

        if (!config.PauseInUnsafeState)
            return true;

        return !Plugin.Condition.Any(UnsafeConditions);
    }

    private void StartRepair()
    {
        activeMode = plugin.Configuration.Mode;
        StatusText = "Réparation en cours...";

        if (activeMode == RepairMode.DarkMatter)
            StartDarkMatterRepair();
        else if (activeMode == RepairMode.Npc)
            StartNpcRepair();
    }

    private unsafe void StartDarkMatterRepair()
    {
        if (!DarkMatterFinder.TryFindBestStack(out var container, out var slot, out var itemId))
        {
            const string msg = "Aucune matière sombre trouvée dans l'inventaire.";
            LogMessage(msg);
            EnterCooldown(TimeSpan.FromMinutes(2), msg);
            return;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30), "Impossible d'ouvrir la fenêtre de réparation.");
            return;
        }

        agent->UseItem(itemId, container, (uint)slot, 0);
        state = State.WaitingForRepairWindow;
        stateEnteredAt = DateTime.Now;
    }

    private void StartNpcRepair()
    {
        var territory = (ushort)Plugin.ClientState.TerritoryType;

        RepairNpcEntry? entry = null;
        if (plugin.Configuration.PreferredRepairNpcByTerritory.TryGetValue(territory, out var preferredId))
            entry = plugin.Configuration.RepairNpcs.FirstOrDefault(n => n.Id == preferredId);
        entry ??= plugin.Configuration.RepairNpcs.FirstOrDefault(n => n.TerritoryId == territory);

        if (entry == null)
        {
            const string msg = "Aucun PNJ réparateur enregistré pour cette zone. Ciblez-en un et utilisez \"Enregistrer la cible\" dans la config.";
            LogMessage(msg);
            EnterCooldown(TimeSpan.FromMinutes(5), msg);
            return;
        }

        if (!navmesh.IsAvailable())
        {
            const string msg = "vnavmesh est introuvable ou non chargé, impossible de se déplacer automatiquement.";
            LogMessage(msg);
            EnterCooldown(TimeSpan.FromMinutes(5), msg);
            return;
        }

        targetNpc = entry;
        moveRetries = 0;
        navmesh.MoveTo(entry.Position);
        state = State.MovingToNpc;
        stateEnteredAt = DateTime.Now;
        StatusText = $"Déplacement vers {entry.Name}...";
    }

    private unsafe void TickMovingToNpc(DateTime now)
    {
        if (targetNpc == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30), string.Empty);
            return;
        }

        if (now - stateEnteredAt > TimeSpan.FromSeconds(90))
        {
            const string msg = "Trajet vers le PNJ réparateur trop long, abandon.";
            LogMessage(msg);
            navmesh.Stop();
            EnterCooldown(TimeSpan.FromMinutes(2), msg);
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30), string.Empty);
            return;
        }

        var npcObject = FindLiveNpc(targetNpc);
        var npcPosition = npcObject?.Position ?? targetNpc.Position;
        var distance = Vector3.Distance(localPlayer.Position, npcPosition);

        if (distance <= InteractDistance)
        {
            navmesh.Stop();

            if (npcObject == null)
            {
                const string msg = "PNJ réparateur introuvable à l'endroit enregistré.";
                LogMessage(msg);
                EnterCooldown(TimeSpan.FromMinutes(2), msg);
                return;
            }

            var gameObject = (GameObject*)(void*)npcObject.Address;
            TargetSystem.Instance()->InteractWithObject(gameObject, false);
            state = State.WaitingForMenu;
            stateEnteredAt = now;
            StatusText = $"Interaction avec {targetNpc.Name}...";
            return;
        }

        if (!navmesh.IsPathRunning())
        {
            moveRetries++;
            if (moveRetries > 3)
            {
                const string msg = "Impossible d'atteindre le PNJ réparateur (chemin bloqué ?).";
                LogMessage(msg);
                EnterCooldown(TimeSpan.FromMinutes(2), msg);
                return;
            }

            navmesh.MoveTo(npcPosition);
        }
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindLiveNpc(RepairNpcEntry entry)
    {
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDistance = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.BaseId != entry.DataId)
                continue;

            var distance = Vector3.DistanceSquared(obj.Position, entry.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = obj;
            }
        }

        return best;
    }

    private void EnterCooldown(TimeSpan duration, string statusMessage)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = statusMessage;
        targetNpc = null;
    }

    private unsafe void OnRepairAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (state != State.WaitingForRepairWindow && state != State.WaitingForMenu)
            return;

        var repairManager = RepairManager.Instance();
        if (repairManager == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30), "Impossible d'accéder au module de réparation.");
            return;
        }

        repairManager->RepairEquipped(activeMode == RepairMode.Npc);
        LogMessage("Équipement réparé automatiquement.");
        EnterCooldown(TimeSpan.FromSeconds(10), "Équipement réparé.");
    }

    private unsafe void OnSelectStringSetup(AddonEvent type, AddonArgs args)
    {
        if (state != State.WaitingForMenu || activeMode != RepairMode.Npc)
            return;

        var addon = (AddonSelectString*)(void*)args.Addon.Address;
        if (AtkAddonHelper.TryClickRepairEntry(addon))
        {
            state = State.WaitingForRepairWindow;
            stateEnteredAt = DateTime.Now;
        }
    }

    private unsafe void OnSelectIconStringSetup(AddonEvent type, AddonArgs args)
    {
        if (state != State.WaitingForMenu || activeMode != RepairMode.Npc)
            return;

        var addon = (AddonSelectIconString*)(void*)args.Addon.Address;
        if (AtkAddonHelper.TryClickRepairEntry(addon))
        {
            state = State.WaitingForRepairWindow;
            stateEnteredAt = DateTime.Now;
        }
    }
}
