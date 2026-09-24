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
    }

    /// <summary> 仿真插件实例。类名故意为 IDalamudPlugin 以命中生产 IsPluginInstance 的兜底判定。 </summary>
    private sealed class IDalamudPlugin
    {
        public FakeWindow ConfigWindow { get; } = new() { Title = "配置" };
        public FakeWindow Main { get; } = new() { Title = "主窗" };
        // 一个不带 IsOpen 的成员，不应被误关
        public string Version { get; } = "1.0";
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
}
