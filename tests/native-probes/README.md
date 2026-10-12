# 原生模块探针

这些项目是独立的 BepInEx 诊断插件，不属于发布插件。必须使用独立游戏副本；不要复制到日常游玩的游戏目录。探针仅在专用命令行开关下运行，使用独立存档路径并阻断 `SaveManager.Save`，结束时退出测试游戏。

| 项目 | 命令行开关 | 内容 |
| --- | --- | --- |
| character-panel-probe | `-tinyrogues-character-panel-probe` | 自动进入测试局，打开角色四页并捕获 TMP 文本 |
| menu-module-probe | `-tinyrogues-menu-module-probe` | 标题菜单和六个设置页 |
| world-module-probe | `-tinyrogues-world-module-probe` | 资源名称、门与商店、指定房间、原生门初始化和真实首领 HUD |
| ending-menu-probe | `-tinyrogues-ending-menu-probe` | 暂停、放弃确认、强制死亡后的结算文本 |

用游戏自带的 `BepInEx/interop` 程序集构建，例如：

```powershell
dotnet build tests/native-probes/world-module-probe/WorldModuleProbe.csproj '-p:GameDir=你的独立游戏副本目录'
```

只把对应探针 DLL 放入该副本的 `BepInEx/plugins` 子目录。中文验证还须安装本仓库编译出的主插件、全部词库，以及 `tests/fixtures/*.json` 到主插件的 `diagnostics` 目录。英文对照临时停用中文主插件，但保留诊断探针。用上表命令行开关启动副本；JSONL 输出位于探针 DLL 所在目录。世界探针外部超时应至少 195 秒，其他探针至少 105 秒。

JSONL 区分字段枚举、资源 getter、原生初始化、场景实例及实际 HUD。不得将它们合并成“全场景通过”：`active`、`alpha` 只是文本可见性的近似条件，未证明屏幕裁剪、排版或全部操作分支。详见 [模块记录](../../docs/modules.md)。
