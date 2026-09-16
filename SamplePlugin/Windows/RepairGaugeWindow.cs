using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SamplePlugin.Repair;

namespace SamplePlugin.Windows;

/// <summary>
/// RepairMe-style always-on overlay: a compact bar showing the worst equipped item's condition,
/// expandable into a per-slot breakdown with durability and spiritbond.
/// </summary>
public class RepairGaugeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool expanded;

    public RepairGaugeWindow(Plugin plugin)
        : base("Repair Gauge##AutoRepairGauge", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
                                                  ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize |
                                                  ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public void Dispose() { }

    public override bool DrawConditions() => plugin.Configuration.ShowGauge && Plugin.ClientState.IsLoggedIn;

    public override void PreDraw()
    {
        var config = plugin.Configuration;

        if (config.GaugeLocked)
            Flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs;
        else
            Flags &= ~(ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs);

        ImGui.SetNextWindowPos(config.GaugePosition, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.75f);
    }

    public override void Draw()
    {
        var config = plugin.Configuration;
        var slots = EquipmentDurabilityService.GetEquippedGearCondition();

        // Persist window position back to config so it survives relogs, but only while unlocked
        // (avoids fighting the user every frame while they're dragging it).
        if (!config.GaugeLocked)
        {
            var pos = ImGui.GetWindowPos();
            if (pos != config.GaugePosition)
            {
                config.GaugePosition = pos;
                config.Save();
            }
        }

        var lowest = 100f;
        foreach (var slot in slots)
        {
            if (slot.ConditionPercent < lowest)
                lowest = slot.ConditionPercent;
        }

        var color = ColorFor(lowest, config);

        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, color))
        {
            ImGui.ProgressBar(lowest / 100f, new Vector2(220 * ImGuiHelpers.GlobalScale, 20 * ImGuiHelpers.GlobalScale),
                $"Équipement {lowest:0}%");
        }

        ImGui.SameLine();
        if (ImGui.ArrowButton("##ExpandGauge", expanded ? ImGuiDir.Up : ImGuiDir.Down))
            expanded = !expanded;

        if (plugin.AutoRepairManager.StatusText.Length > 0)
            ImGui.TextDisabled(plugin.AutoRepairManager.StatusText);

        if (!expanded)
            return;

        ImGui.Spacing();

        using var table = ImRaii.Table("##RepairSlots", 3, ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        foreach (var slot in slots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var icon = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(slot.IconId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(24, 24) * ImGuiHelpers.GlobalScale);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(slot.Name);

            ImGui.TableNextColumn();
            var slotColor = ColorFor(slot.ConditionPercent, config);
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, slotColor))
            {
                ImGui.ProgressBar(slot.ConditionPercent / 100f, new Vector2(140 * ImGuiHelpers.GlobalScale, 16 * ImGuiHelpers.GlobalScale),
                    $"{slot.ConditionPercent:0}%");
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"Lien: {slot.SpiritbondPercent:0}%");
        }
    }

    private static Vector4 ColorFor(float conditionPercent, Configuration config)
    {
        if (conditionPercent < config.GaugeCriticalPercent)
            return new Vector4(0.85f, 0.2f, 0.2f, 1f);
        if (conditionPercent < config.GaugeWarningPercent)
            return new Vector4(0.9f, 0.6f, 0.1f, 1f);
        return new Vector4(0.2f, 0.75f, 0.3f, 1f);
    }
}
