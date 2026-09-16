using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using SamplePlugin.Repair;

namespace SamplePlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Auto-Repair###AutoRepairConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(500, 460);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |= ImGuiWindowFlags.NoMove;
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##AutoRepairTabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Réparation automatique"))
        {
            if (tab.Success)
                DrawRepairTab();
        }

        using (var tab = ImRaii.TabItem("Jauge"))
        {
            if (tab.Success)
                DrawGaugeTab();
        }

        using (var tab = ImRaii.TabItem("PNJ réparateurs"))
        {
            if (tab.Success)
                DrawNpcTab();
        }

        using (var tab = ImRaii.TabItem("Divers"))
        {
            if (tab.Success)
                DrawMiscTab();
        }

        using (var tab = ImRaii.TabItem("Debug"))
        {
            if (tab.Success)
                DrawDebugTab();
        }
    }

    private void DrawRepairTab()
    {
        ImGui.Spacing();

        var enabled = configuration.AutoRepairEnabled;
        if (ImGui.Checkbox("Activer la réparation automatique", ref enabled))
        {
            configuration.AutoRepairEnabled = enabled;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Méthode de réparation");
        var mode = configuration.Mode;

        if (ImGui.RadioButton("Matière sombre (sur soi)", mode == RepairMode.DarkMatter))
        {
            configuration.Mode = RepairMode.DarkMatter;
            configuration.Save();
        }

        if (ImGui.RadioButton("PNJ réparateur (déplacement automatique via vnavmesh)", mode == RepairMode.Npc))
        {
            configuration.Mode = RepairMode.Npc;
            configuration.Save();
        }

        var fallback = configuration.FallBackToNpcWhenOutOfDarkMatter;
        if (ImGui.Checkbox("Si plus de matière sombre, se replier sur le PNJ réparateur", ref fallback))
        {
            configuration.FallBackToNpcWhenOutOfDarkMatter = fallback;
            configuration.Save();
        }
        ImGui.TextDisabled("Ne fait rien si aucun PNJ n'est enregistré pour la zone actuelle (voir l'onglet \"PNJ réparateurs\").");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var threshold = configuration.RepairThresholdPercent;
        ImGui.SetNextItemWidth(250);
        if (ImGui.SliderInt("Seuil de déclenchement (%)", ref threshold, 1, 99))
        {
            configuration.RepairThresholdPercent = threshold;
            configuration.Save();
        }
        ImGui.TextDisabled("La réparation ne se déclenche que si au moins une pièce d'équipement\npasse sous ce seuil. Rien n'est réparé (ni consommé) au-dessus.");

        ImGui.Spacing();
        ImGui.TextUnformatted("Ne jamais réparer automatiquement...");

        var pauseInCombat = configuration.PauseInCombat;
        if (ImGui.Checkbox("...en combat", ref pauseInCombat))
        {
            configuration.PauseInCombat = pauseInCombat;
            configuration.Save();
        }

        var pauseCraft = configuration.PauseWhileCraftingOrGathering;
        if (ImGui.Checkbox("...en craft / récolte / pêche", ref pauseCraft))
        {
            configuration.PauseWhileCraftingOrGathering = pauseCraft;
            configuration.Save();
        }

        var pauseCutscene = configuration.PauseDuringCutscene;
        if (ImGui.Checkbox("...pendant une cinématique", ref pauseCutscene))
        {
            configuration.PauseDuringCutscene = pauseCutscene;
            configuration.Save();
        }

        var pauseDuty = configuration.PauseInDuty;
        if (ImGui.Checkbox("...en instance/donjon", ref pauseDuty))
        {
            configuration.PauseInDuty = pauseDuty;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var manager = plugin.AutoRepairManager;

        ImGui.BeginDisabled(manager.IsActive);
        if (ImGui.Button("Réparer maintenant"))
            manager.RequestManualRepair();
        ImGui.EndDisabled();

        if (manager.IsActive)
        {
            ImGui.SameLine();
            if (ImGui.Button("Annuler"))
                manager.Cancel();
        }

        if (manager.StatusText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(manager.StatusText);
        }

        if (manager.MessageHistory.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextUnformatted("Historique récent");

            using var child = ImRaii.Child("##RepairHistory", new Vector2(0, 120), true);
            if (child.Success)
            {
                foreach (var entry in manager.MessageHistory)
                    ImGui.TextWrapped(entry);
            }
        }
    }

    private void DrawGaugeTab()
    {
        ImGui.Spacing();

        var show = configuration.ShowGauge;
        if (ImGui.Checkbox("Afficher la jauge de durabilité", ref show))
        {
            configuration.ShowGauge = show;
            configuration.Save();
        }

        var locked = configuration.GaugeLocked;
        if (ImGui.Checkbox("Verrouiller la position de la jauge", ref locked))
        {
            configuration.GaugeLocked = locked;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Style (comme RepairMe)");

        var style = configuration.GaugeStyle;
        if (ImGui.RadioButton("Barre (globale + détail par pièce)", style == GaugeStyle.Bar))
        {
            configuration.GaugeStyle = GaugeStyle.Bar;
            configuration.Save();
        }

        if (ImGui.RadioButton("Icônes (une rangée par pièce d'équipement)", style == GaugeStyle.Icons))
        {
            configuration.GaugeStyle = GaugeStyle.Icons;
            configuration.Save();
        }

        if (ImGui.RadioButton("Barre d'XP (fine, façon barre d'expérience)", style == GaugeStyle.ExperienceBar))
        {
            configuration.GaugeStyle = GaugeStyle.ExperienceBar;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var warning = configuration.GaugeWarningPercent;
        ImGui.SetNextItemWidth(250);
        if (ImGui.SliderInt("Seuil orange (%)", ref warning, 1, 99))
        {
            configuration.GaugeWarningPercent = Math.Max(warning, configuration.GaugeCriticalPercent + 1);
            configuration.Save();
        }

        var critical = configuration.GaugeCriticalPercent;
        ImGui.SetNextItemWidth(250);
        if (ImGui.SliderInt("Seuil rouge (%)", ref critical, 1, 99))
        {
            configuration.GaugeCriticalPercent = Math.Min(critical, configuration.GaugeWarningPercent - 1);
            configuration.Save();
        }
    }

    private void DrawNpcTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Ciblez un PNJ capable de réparer votre équipement (généralement un vendeur " +
                           "de la Guilde des artisans/marchands ambulants), puis cliquez sur le bouton " +
                           "ci-dessous pour l'enregistrer comme réparateur pour la zone actuelle.");

        ImGui.Spacing();

        var currentTerritory = (ushort)Plugin.ClientState.TerritoryType;
        ImGui.TextUnformatted($"Zone actuelle : {GetTerritoryName(currentTerritory)}");

        var target = Plugin.TargetManager.Target;
        ImGui.BeginDisabled(target == null);
        if (ImGui.Button("Enregistrer la cible actuelle comme PNJ réparateur"))
        {
            if (target != null)
            {
                var newEntry = new RepairNpcEntry
                {
                    Name = target.Name.ToString(),
                    DataId = target.BaseId,
                    TerritoryId = currentTerritory,
                    Position = target.Position,
                };
                configuration.RepairNpcs.Add(newEntry);

                // First one registered for this zone becomes the default automatically.
                if (!configuration.PreferredRepairNpcByTerritory.ContainsKey(currentTerritory))
                    configuration.PreferredRepairNpcByTerritory[currentTerritory] = newEntry.Id;

                configuration.Save();
            }
        }
        ImGui.EndDisabled();

        if (target != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Cible: {target.Name}");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var candidatesHere = configuration.RepairNpcs.Where(n => n.TerritoryId == currentTerritory).ToList();
        ImGui.TextUnformatted("PNJ à utiliser dans cette zone");

        var effectivePreferredId = GetEffectivePreferredNpcId(currentTerritory);
        var preferredEntry = candidatesHere.FirstOrDefault(n => n.Id == effectivePreferredId) ?? candidatesHere.FirstOrDefault();
        var comboLabel = preferredEntry?.Name ?? "Aucun PNJ enregistré pour cette zone";

        ImGui.SetNextItemWidth(300);
        ImGui.BeginDisabled(candidatesHere.Count == 0);
        using (var combo = ImRaii.Combo("##PreferredNpcCombo", comboLabel))
        {
            if (combo.Success)
            {
                foreach (var candidate in candidatesHere)
                {
                    var isSelected = candidate.Id == preferredEntry?.Id;
                    if (ImGui.Selectable(candidate.Name, isSelected))
                    {
                        configuration.PreferredRepairNpcByTerritory[currentTerritory] = candidate.Id;
                        configuration.Save();
                    }
                }
            }
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("PNJ enregistrés");

        using var table = ImRaii.Table("##RepairNpcTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Nom");
        ImGui.TableSetupColumn("Zone");
        ImGui.TableSetupColumn("Position");
        ImGui.TableSetupColumn("Actif");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        for (var i = configuration.RepairNpcs.Count - 1; i >= 0; i--)
        {
            var npc = configuration.RepairNpcs[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(npc.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetTerritoryName(npc.TerritoryId));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{npc.Position.X:0}, {npc.Position.Y:0}, {npc.Position.Z:0}");

            ImGui.TableNextColumn();
            var isPreferred = GetEffectivePreferredNpcId(npc.TerritoryId) == npc.Id;
            ImGui.TextUnformatted(isPreferred ? "★" : "");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Supprimer##{i}"))
            {
                if (configuration.PreferredRepairNpcByTerritory.TryGetValue(npc.TerritoryId, out var pref) && pref == npc.Id)
                    configuration.PreferredRepairNpcByTerritory.Remove(npc.TerritoryId);

                configuration.RepairNpcs.RemoveAt(i);
                configuration.Save();
            }
        }
    }

    private void DrawMiscTab()
    {
        ImGui.Spacing();

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Fenêtre de configuration déplaçable", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }
    }

    /// <summary>
    /// The NPC that would actually be used for a given zone: the explicitly chosen one if any,
    /// otherwise the first one registered for that zone (mirrors AutoRepairManager's own fallback,
    /// so a lone registered NPC always shows as active even before it's ever been picked from the combo).
    /// </summary>
    private Guid? GetEffectivePreferredNpcId(ushort territoryId)
    {
        if (configuration.PreferredRepairNpcByTerritory.TryGetValue(territoryId, out var id))
            return id;

        return configuration.RepairNpcs.FirstOrDefault(n => n.TerritoryId == territoryId)?.Id;
    }

    private void DrawDebugTab()
    {
        var manager = plugin.AutoRepairManager;

        ImGui.Spacing();
        ImGui.TextUnformatted($"Version du plugin : {Plugin.PluginInterface.Manifest.AssemblyVersion}");
        ImGui.TextUnformatted($"DalamudApiLevel : {Plugin.PluginInterface.Manifest.DalamudApiLevel}");
        ImGui.TextUnformatted($"vnavmesh détecté : {(manager.IsVNavmeshAvailable() ? "oui" : "non")}");
        ImGui.TextUnformatted($"Zone actuelle : {GetTerritoryName((ushort)Plugin.ClientState.TerritoryType)}");
        ImGui.TextUnformatted($"Mode configuré : {configuration.Mode}, seuil : {configuration.RepairThresholdPercent}%");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Copier les informations de debug"))
            ImGui.SetClipboardText(BuildDebugReport());

        ImGui.SameLine();
        if (ImGui.Button("Vider le journal"))
            manager.ClearDebugLog();

        ImGui.Spacing();
        ImGui.TextUnformatted($"Journal détaillé ({manager.DebugLog.Count} entrées, le plus récent en bas)");

        using var child = ImRaii.Child("##DebugLogChild", new Vector2(0, 0), true);
        if (child.Success)
        {
            foreach (var line in manager.DebugLog)
                ImGui.TextWrapped(line);

            if (manager.DebugLog.Count > 0)
                ImGui.SetScrollHereY(1f);
        }
    }

    private string BuildDebugReport()
    {
        var manager = plugin.AutoRepairManager;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== Auto-Repair Kit - rapport de debug ===");
        sb.AppendLine($"Version: {Plugin.PluginInterface.Manifest.AssemblyVersion}");
        sb.AppendLine($"DalamudApiLevel: {Plugin.PluginInterface.Manifest.DalamudApiLevel}");
        sb.AppendLine($"Mode: {configuration.Mode}, Seuil: {configuration.RepairThresholdPercent}%, Repli PNJ: {configuration.FallBackToNpcWhenOutOfDarkMatter}");
        sb.AppendLine($"Pauses: combat={configuration.PauseInCombat}, craft/récolte={configuration.PauseWhileCraftingOrGathering}, cinématique={configuration.PauseDuringCutscene}, instance={configuration.PauseInDuty}");
        sb.AppendLine($"vnavmesh disponible: {manager.IsVNavmeshAvailable()}");
        sb.AppendLine($"Zone actuelle: {GetTerritoryName((ushort)Plugin.ClientState.TerritoryType)} ({Plugin.ClientState.TerritoryType})");
        sb.AppendLine($"PNJ enregistrés: {configuration.RepairNpcs.Count}");
        sb.AppendLine("--- Journal détaillé ---");

        foreach (var line in manager.DebugLog)
            sb.AppendLine(line);

        return sb.ToString();
    }

    private static string GetTerritoryName(uint territoryId)
    {
        if (Plugin.DataManager.GetExcelSheet<TerritoryType>().TryGetRow(territoryId, out var row))
        {
            var name = row.PlaceName.Value.Name.ToString();
            if (!string.IsNullOrEmpty(name))
                return name;
        }

        return $"Zone {territoryId}";
    }
}
