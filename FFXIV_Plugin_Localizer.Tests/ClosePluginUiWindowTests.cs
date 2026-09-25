using System;
using System.Collections.Generic;
using FFXIVPluginLocalizer;
using FFXIVPluginLocalizer.Services;
using Xunit;

namespace FFXIVPluginLocalizer.Tests;

/// <summary>
/// 体检「自动关闭其它插件窗口」反射逻辑回归测试（无头沙盒，不依赖 Dalamud 运行时）。
///
/// 背景：官方 IExposedPlugin 只暴露 OpenConfigUi/OpenMainUi、没有 Close（dalamud.dev API 确认，
/// 本工程 AtkNativeUiWalkerService.cs:137 亦记）；Atk 原生窗由体检循环的 TryClosePluginWindow 处理。
/// ImGui 窗只能反射：IExposedPlugin 接口本身不暴露插件实例，故 ClosePluginUiWindowCore 反射其
/// **具体运行时类型**取出真正的插件实例，再关闭它身上所有带 IsOpen 的 Window 成员。
///
/// 生产环境里「插件实例」是实现了 Dalamud.Plugin.IDalamudPlugin 的真实对象；
/// 无头测试不引用 Dalamud 运行时接口，故用类名命中生产 IsPluginInstance 的兜底分支
/// （v.GetType().Name == "IDalamudPlugin"）——与原接口分支走同一个判定函数，逻辑等价。
/// </summary>
public class ClosePluginUiWindowTests
{
    /// <summary> 仿真一个 Dalamud 窗（生产里是 Dalamud.Interface.Windowing.Window，标准关窗做法是 IsOpen = false）。 </summary>
    private sealed class FakeWindow
    {
        public bool IsOpen { get; set; } = true;
        public string Title { get; set; } = "";
        public string WindowName { get; set; } = "";   // 命中生产 IsCloseableWindow 的兜底分支（IsOpen + WindowName）
    }

    /// <summary> 仿真「子对象持有窗」的嵌套结构，验证递归找窗能下钻到子对象里。 </summary>
    private sealed class SubHolder
    {
        public FakeWindow Nested { get; } = new() { Title = "嵌套", WindowName = "嵌套窗" };
    }

    /// <summary> 仿真插件实例。类名故意为 IDalamudPlugin 以命中生产 IsPluginInstance 的兜底判定。 </summary>
    private sealed class IDalamudPlugin
    {
        public FakeWindow ConfigWindow { get; } = new() { Title = "配置", WindowName = "配置窗" };
        public FakeWindow Main { get; } = new() { Title = "主窗", WindowName = "主窗" };
        // 一个不带 IsOpen 的成员，不应被误关
        public string Version { get; } = "1.0";
        // 真实插件常见存法：窗装在 List<Window> 集合 / 嵌套在子对象里——验证递归找窗能覆盖
        public List<FakeWindow> Panels { get; } = new()
        {
            new FakeWindow { Title = "面板A", WindowName = "面板A" },
            new FakeWindow { Title = "面板B", WindowName = "面板B" },
        };
        public SubHolder Sub { get; } = new();
    }

    /// <summary> 仿真 IExposedPlugin 包装（只有 Name/InternalName/IsLoaded/HasConfigUi/Open*，不暴露实例）。 </summary>
    private sealed class FakeExposedPlugin
    {
        public string Name { get; } = "FakePlugin";
        public string InternalName { get; } = "fake.plugin";
        public bool IsLoaded { get; } = true;
        public bool HasConfigUi { get; } = true;
        public IDalamudPlugin Plugin { get; } = new();
        public void OpenConfigUi() { }
        public void OpenMainUi() { }
    }

    /// <summary> 仿真一个「暴露不出实例」的包装（没有 Plugin 成员，也没有任何 IDalamudPlugin 类型成员）。 </summary>
    private sealed class FakeExposedNoInstance
    {
        public string Name { get; } = "NoInstancePlugin";
        public string InternalName { get; } = "noinst.plugin";
        public bool IsLoaded { get; } = true;
        // 一个普通对象，不是 IDalamudPlugin
        public object State { get; } = new();
    }

    [Fact]
    public void ClosePluginUiWindowCore_关闭所有带IsOpen的ImGui窗()
    {
        var log = new AppLog(null); // 仅内存，不落盘
        var exposed = new FakeExposedPlugin();
        var instance = exposed.Plugin;

        // 前置：两个窗都开着
        Assert.True(instance.ConfigWindow.IsOpen);
        Assert.True(instance.Main.IsOpen);

        Plugin.ClosePluginUiWindowCore(log, exposed);

        // 反射关窗后，两个 ImGui 窗都应被设为关闭
        Assert.False(instance.ConfigWindow.IsOpen);
        Assert.False(instance.Main.IsOpen);
    }

    [Fact]
    public void ClosePluginUiWindowCore_无IsOpen成员时静默跳过不抛异常()
    {
        var log = new AppLog(null);
        // 没有 Plugin 成员、也找不到任何 IDalamudPlugin 实例 → 应打 Warn 并 return，不抛异常
        var exposed = new FakeExposedNoInstance();

        var ex = Record.Exception(() => Plugin.ClosePluginUiWindowCore(log, exposed));
        Assert.Null(ex);
    }

    [Fact]
    public void ClosePluginUiWindowCore_兜底分支识别出实例()
    {
        // 验证 GetPluginInstance 走「类名兜底」分支能正确取出实例（与接口分支同一函数）
        var log = new AppLog(null);
        var exposed = new FakeExposedPlugin();

        // 不抛异常即代表实例被成功取出并遍历（若取不出会走 Warn 分支但同样不抛）
        var ex = Record.Exception(() => Plugin.ClosePluginUiWindowCore(log, exposed));
        Assert.Null(ex);
        Assert.False(exposed.Plugin.ConfigWindow.IsOpen);
    }

    [Fact]
    public void ClosePluginUiWindowCore_递归关闭List集合与嵌套子对象里的窗()
    {
        // 还原真实插件两种常见存法：窗装在 List<Window> 集合、或嵌套在子对象里。
        // 旧实现只认「直接成员」与「WindowSystem.Windows」，这两类会漏掉 → 关不掉。
        var log = new AppLog(null);
        var exposed = new FakeExposedPlugin();
        var instance = exposed.Plugin;

        Assert.True(instance.ConfigWindow.IsOpen);
        Assert.All(instance.Panels, w => Assert.True(w.IsOpen));
        Assert.True(instance.Sub.Nested.IsOpen);

        Plugin.ClosePluginUiWindowCore(log, exposed);

        Assert.False(instance.ConfigWindow.IsOpen);
        Assert.All(instance.Panels, w => Assert.False(w.IsOpen));   // List 里的窗也关了
        Assert.False(instance.Sub.Nested.IsOpen);                   // 嵌套子对象里的窗也关了
    }

    // ── 真实结构回归：新版 Dalamud 的 InstalledPlugins 元素是 ExposedPlugin，
    //    它包着内部类 LocalPlugin，实例在 LocalPlugin.Instance（深度 2）——
    //    ExposedPlugin 与 LocalPlugin 本身都**不是** IDalamudPlugin，必须下钻两层才取得到。 ──

    /// <summary> 仿真 Dalamud 内部包装类 LocalPlugin（类名与生产一致，命中 IsDalamudInternalWrapper）。 </summary>
    private sealed class LocalPlugin
    {
        public IDalamudPlugin Instance { get; } = new();
    }

    /// <summary> 仿真新版 ExposedPlugin：只持有 LocalPlugin 成员，不直接暴露实例。 </summary>
    private sealed class FakeExposedPluginDeep
    {
        public string Name { get; } = "DeepPlugin";
        public string InternalName { get; } = "deep.plugin";
        public bool IsLoaded { get; } = true;
        public bool HasConfigUi { get; } = true;
        public LocalPlugin Plugin { get; } = new();   // ← 深度 1 是 LocalPlugin（非 IDalamudPlugin）
        public void OpenConfigUi() { }
        public void OpenMainUi() { }
    }

    [Fact]
    public void ClosePluginUiWindowCore_下钻两层取出实例并关窗()
    {
        // 还原真实结构：ExposedPlugin → LocalPlugin → Instance(IDalamudPlugin)。
        // 旧实现只找深度 1，故取不到实例、整个关窗被跳过（线上日志即「无法取得…插件实例」）。
        var log = new AppLog(null);
        var exposed = new FakeExposedPluginDeep();
        var instance = exposed.Plugin.Instance;

        Assert.True(instance.ConfigWindow.IsOpen);
        Assert.True(instance.Main.IsOpen);

        Plugin.ClosePluginUiWindowCore(log, exposed);

        Assert.False(instance.ConfigWindow.IsOpen);
        Assert.False(instance.Main.IsOpen);
    }

    [Fact]
    public void GetPluginInstance_下钻两层能取出IDalamudPlugin()
    {
        var exposed = new FakeExposedPluginDeep();
        var instance = Plugin.GetPluginInstance(exposed);
        Assert.NotNull(instance);
        Assert.Same(exposed.Plugin.Instance, instance);
    }
}
