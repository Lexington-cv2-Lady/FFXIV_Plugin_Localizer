# 翻译插件的插件

> 运行时把其他 Dalamud 插件的英文界面文字替换为简体中文。

卫月（Dalamud）插件。给其他 Dalamud 插件的界面做运行时汉化：在 ImGui 文字绘制层把插件渲染的英文按对照表换成中文显示，不改对方插件文件。含源码提取候选、AI 机翻（多服务商）、词典管理与单词黑名单、后台自动翻译与运行时自动补译。

## 安装

卫月设置 -> 实验性 -> 自定义插件仓库，添加：

```
https://raw.githubusercontent.com/Lexington-cv2-Lady/Lexington_CV-2_Repository/main/plugin_repo.json
```

然后在插件安装器搜索「翻译插件的插件」安装。

## 流程

1. **AI 设置**：填 API Key
2. **源码提取**：从插件公开源码提取界面英文
3. **插件翻译**：AI 翻成中文，替换层即时生效

## 协议

AGPL-3.0。
