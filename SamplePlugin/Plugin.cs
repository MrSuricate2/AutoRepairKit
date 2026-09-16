using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Materia;
using SamplePlugin.Repair;
using SamplePlugin.Windows;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private const string RepairCommandName = "/pautorepair";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }
    private RepairGaugeWindow RepairGaugeWindow { get; init; }
    public AutoRepairManager AutoRepairManager { get; init; }
    public AutoMateriaExtractionManager AutoMateriaExtractionManager { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        AutoRepairManager = new AutoRepairManager(this);
        AutoMateriaExtractionManager = new AutoMateriaExtractionManager(this);

        ConfigWindow = new ConfigWindow(this);
        RepairGaugeWindow = new RepairGaugeWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(RepairGaugeWindow);

        CommandManager.AddHandler(RepairCommandName, new CommandInfo(OnRepairCommand)
        {
            HelpMessage = "Ouvre la fenêtre de configuration de la réparation automatique."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
    }

    public void Dispose()
    {
        AutoRepairManager.Dispose();
        AutoMateriaExtractionManager.Dispose();

        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        RepairGaugeWindow.Dispose();

        CommandManager.RemoveHandler(RepairCommandName);
    }

    private void OnRepairCommand(string command, string args) => ConfigWindow.Toggle();

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}
