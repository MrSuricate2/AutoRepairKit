using System;
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

    /// <summary>Exposed so the gauge/config windows can show what the manager is currently doing.</summary>
    public string StatusText { get; private set; } = string.Empty;

    public AutoRepairManager(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "AddonRepair", OnRepairAddonSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectIconString", OnSelectIconStringSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "AddonRepair", OnRepairAddonSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectString", OnSelectStringSetup);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectIconString", OnSelectIconStringSetup);
    }

    /// <summary>Lets the config window trigger a repair immediately, bypassing the threshold check.</summary>
    public void RequestManualRepair()
    {
        if (state != State.Idle && state != State.Cooldown)
            return;

        StartRepair();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;

        switch (state)
        {
            case State.Cooldown:
                if (now >= cooldownUntil)
                    state = State.Idle;
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
                    Plugin.ChatGui.Print("[Auto-Repair] Le PNJ n'a pas proposé de réparation dans le délai imparti, réparation manuelle nécessaire cette fois-ci.");
                    EnterCooldown(TimeSpan.FromSeconds(30));
                }
                break;

            case State.WaitingForRepairWindow:
                if (now - stateEnteredAt > TimeSpan.FromSeconds(8))
                {
                    Plugin.ChatGui.Print("[Auto-Repair] La fenêtre de réparation ne s'est pas ouverte, réessai plus tard.");
                    EnterCooldown(TimeSpan.FromSeconds(30));
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
            Plugin.ChatGui.Print("[Auto-Repair] Aucune matière sombre trouvée dans l'inventaire.");
            EnterCooldown(TimeSpan.FromMinutes(2));
            return;
        }

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30));
            return;
        }

        agent->UseItem(itemId, container, (uint)slot, 0);
        state = State.WaitingForRepairWindow;
        stateEnteredAt = DateTime.Now;
    }

    private void StartNpcRepair()
    {
        var territory = Plugin.ClientState.TerritoryType;
        RepairNpcEntry? entry = null;
        foreach (var npc in plugin.Configuration.RepairNpcs)
        {
            if (npc.TerritoryId == territory)
            {
                entry = npc;
                break;
            }
        }

        if (entry == null)
        {
            Plugin.ChatGui.Print("[Auto-Repair] Aucun PNJ réparateur enregistré pour cette zone. Ciblez-en un et utilisez \"Enregistrer la cible\" dans la config.");
            EnterCooldown(TimeSpan.FromMinutes(5));
            return;
        }

        if (!navmesh.IsAvailable())
        {
            Plugin.ChatGui.Print("[Auto-Repair] vnavmesh est introuvable ou non chargé, impossible de se déplacer automatiquement.");
            EnterCooldown(TimeSpan.FromMinutes(5));
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
            EnterCooldown(TimeSpan.FromSeconds(30));
            return;
        }

        if (now - stateEnteredAt > TimeSpan.FromSeconds(90))
        {
            Plugin.ChatGui.Print("[Auto-Repair] Trajet vers le PNJ réparateur trop long, abandon.");
            navmesh.Stop();
            EnterCooldown(TimeSpan.FromMinutes(2));
            return;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30));
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
                Plugin.ChatGui.Print("[Auto-Repair] PNJ réparateur introuvable à l'endroit enregistré.");
                EnterCooldown(TimeSpan.FromMinutes(2));
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
                Plugin.ChatGui.Print("[Auto-Repair] Impossible d'atteindre le PNJ réparateur (chemin bloqué ?).");
                EnterCooldown(TimeSpan.FromMinutes(2));
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

    private void EnterCooldown(TimeSpan duration)
    {
        state = State.Cooldown;
        cooldownUntil = DateTime.Now + duration;
        StatusText = string.Empty;
        targetNpc = null;
    }

    private unsafe void OnRepairAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (state != State.WaitingForRepairWindow && state != State.WaitingForMenu)
            return;

        var repairManager = RepairManager.Instance();
        if (repairManager == null)
        {
            EnterCooldown(TimeSpan.FromSeconds(30));
            return;
        }

        repairManager->RepairEquipped(activeMode == RepairMode.Npc);
        Plugin.ChatGui.Print("[Auto-Repair] Équipement réparé automatiquement.");
        EnterCooldown(TimeSpan.FromSeconds(10));
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
