using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVPluginLocalizer.Services;
using FFXIVPluginLocalizer.Windows;

namespace FFXIVPluginLocalizer;

/// <summary> 插件界面汉化：给其他 Dalamud 插件的界面文本做运行时汉化。MVP 为只读采集模式。 </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/plocalizer";

    public readonly WindowSystem WindowSystem = new("FFXIVPluginLocalizer");
    public AppLog AppLog { get; }
    public ImGuiHookService Hook { get; }
    public MainWindow MainWindow { get; }

    public Plugin()
    {
        AppLog = new AppLog(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "汉化日志.log"));
        Hook = new ImGuiHookService(AppLog, Log, Interop, PluginInterface.GetPluginConfigDirectory);
        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开插件界面汉化窗口"
        });

        PluginInterface.UiBuilder.Draw += DrawAll;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMain; // 插件安装器的设置按钮：本插件暂无独立设置窗，打开主窗（官方模板同款回调，缺失会在安装器报校验警告）
        Framework.Update += OnFramework;

        AppLog.Info("[插件] 插件界面汉化 已加载（MVP：只读采集模式）");
        Log.Information("插件界面汉化 已加载");
    }

    /// <summary> 统一绘制：给所有窗口加明显边框（与旧项目同风格）。 </summary>
    private void DrawAll()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 2.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.72f, 1f, 0.9f));
        try
        {
            WindowSystem.Draw();
        }
        finally
        {
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
            ImGui.PopStyleVar();
        }
    }

    private void OnFramework(IFramework framework) => Hook.TickSave();

    private void OnCommand(string command, string args) => ToggleMain();

    private void ToggleMain() => MainWindow.Toggle();

    public void Dispose()
    {
        Framework.Update -= OnFramework;
        PluginInterface.UiBuilder.Draw -= DrawAll;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMain;
        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        Hook.Dispose();
        AppLog.Info("[插件] 插件界面汉化 已卸载");
    }
}
