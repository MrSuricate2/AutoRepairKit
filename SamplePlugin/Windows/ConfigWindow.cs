using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
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
            MinimumSize = new Vector2(420, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(460, 420);
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

        ImGui.BeginDisabled(!enabled);

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

        var pauseUnsafe = configuration.PauseInUnsafeState;
        if (ImGui.Checkbox("Ne jamais réparer en combat / craft / cinématique / en instance", ref pauseUnsafe))
        {
            configuration.PauseInUnsafeState = pauseUnsafe;
            configuration.Save();
        }

        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Réparer maintenant"))
            plugin.AutoRepairManager.RequestManualRepair();

        ImGui.SameLine();
        ImGui.TextDisabled(plugin.AutoRepairManager.StatusText);
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

        var target = Plugin.TargetManager.Target;
        ImGui.BeginDisabled(target == null);
        if (ImGui.Button("Enregistrer la cible actuelle comme PNJ réparateur"))
        {
            if (target != null)
            {
                configuration.RepairNpcs.Add(new RepairNpcEntry
                {
                    Name = target.Name.ToString(),
                    DataId = target.BaseId,
                    TerritoryId = (ushort)Plugin.ClientState.TerritoryType,
                    Position = target.Position,
                });
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

        ImGui.TextUnformatted("PNJ enregistrés");

        using var table = ImRaii.Table("##RepairNpcTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Nom");
        ImGui.TableSetupColumn("Zone (Id)");
        ImGui.TableSetupColumn("Position");
        ImGui.TableSetupColumn("");
        ImGui.TableHeadersRow();

        for (var i = configuration.RepairNpcs.Count - 1; i >= 0; i--)
        {
            var npc = configuration.RepairNpcs[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(npc.Name);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(npc.TerritoryId.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{npc.Position.X:0}, {npc.Position.Y:0}, {npc.Position.Z:0}");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Supprimer##{i}"))
            {
                configuration.RepairNpcs.RemoveAt(i);
                configuration.Save();
            }
        }
    }

    private void DrawMiscTab()
    {
        ImGui.Spacing();

        var configValue = configuration.SomePropertyToBeSavedAndWithADefault;
        if (ImGui.Checkbox("Random Config Bool", ref configValue))
        {
            configuration.SomePropertyToBeSavedAndWithADefault = configValue;
            configuration.Save();
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }
    }
}
