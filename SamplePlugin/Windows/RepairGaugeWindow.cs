using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SamplePlugin.Repair;

namespace SamplePlugin.Windows;

/// <summary>
/// RepairMe-style always-on overlay. Mirrors RepairMe's two gauge layouts:
/// a progress bar (global + expandable per-slot detail) or a compact row of tinted equipment icons.
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

        switch (config.GaugeStyle)
        {
            case GaugeStyle.Icons:
                DrawIconsStyle(slots, config);
                break;
            case GaugeStyle.ExperienceBar:
                DrawExperienceBarStyle(slots, config);
                break;
            default:
                DrawBarStyle(slots, config);
                break;
        }

        if (plugin.AutoRepairManager.StatusText.Length > 0)
            ImGui.TextDisabled(plugin.AutoRepairManager.StatusText);
    }

    private void DrawBarStyle(List<RepairSlotInfo> slots, Configuration config)
    {
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

            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(slot.IconId)).GetWrapOrEmpty();
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

    private static void DrawIconsStyle(List<RepairSlotInfo> slots, Configuration config)
    {
        var iconSize = new Vector2(28, 28) * ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var first = true;

        foreach (var slot in slots)
        {
            if (!first)
                ImGui.SameLine(0, 4 * ImGuiHelpers.GlobalScale);
            first = false;

            var cursor = ImGui.GetCursorScreenPos();
            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(slot.IconId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, iconSize);

            var color = ColorFor(slot.ConditionPercent, config);
            var colorU32 = ImGui.ColorConvertFloat4ToU32(color);
            drawList.AddRect(cursor, cursor + iconSize, colorU32, 2f, ImDrawFlags.None, 2f * ImGuiHelpers.GlobalScale);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{slot.Name}\nDurabilité: {slot.ConditionPercent:0}%\nLien matéria: {slot.SpiritbondPercent:0}%");
        }

        if (slots.Count == 0)
            ImGui.TextDisabled("Aucun équipement.");
    }

    /// <summary>Slim rounded bar mimicking the game's native experience bar: just the overall % filled in, no breakdown.</summary>
    private static void DrawExperienceBarStyle(List<RepairSlotInfo> slots, Configuration config)
    {
        var lowest = 100f;
        foreach (var slot in slots)
        {
            if (slot.ConditionPercent < lowest)
                lowest = slot.ConditionPercent;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(280, 14) * scale;
        var cursor = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = size.Y / 2f;

        drawList.AddRectFilled(cursor, cursor + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.05f, 0.05f, 0.9f)), rounding);

        var fillColor = ColorFor(lowest, config);
        var fillWidth = MathF.Round(size.X * Math.Clamp(lowest / 100f, 0f, 1f));

        if (fillWidth > 1f)
        {
            var fillMax = cursor + new Vector2(fillWidth, size.Y);
            var fillFlags = fillWidth >= size.X - 1f ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersLeft;
            drawList.AddRectFilled(cursor, fillMax, ImGui.ColorConvertFloat4ToU32(fillColor), rounding, fillFlags);

            // Subtle lighter sheen on the top half, like the native XP bar's gloss.
            var sheen = new Vector4(Math.Min(1f, fillColor.X + 0.25f), Math.Min(1f, fillColor.Y + 0.25f), Math.Min(1f, fillColor.Z + 0.25f), 0.35f);
            var sheenMin = cursor + new Vector2(1.5f, 1.5f) * scale;
            var sheenMax = new Vector2(fillMax.X - 1.5f * scale, cursor.Y + size.Y * 0.45f);
            if (sheenMax.X > sheenMin.X)
                drawList.AddRectFilled(sheenMin, sheenMax, ImGui.ColorConvertFloat4ToU32(sheen), rounding * 0.6f, ImDrawFlags.RoundCornersTop);
        }

        drawList.AddRect(cursor, cursor + size, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.6f)), rounding, ImDrawFlags.None, 1.5f * scale);

        ImGui.Dummy(size);

        var text = $"{lowest:0}%";
        var textSize = ImGui.CalcTextSize(text);
        var textPos = cursor + (size - textSize) / 2f;
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), text);
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
