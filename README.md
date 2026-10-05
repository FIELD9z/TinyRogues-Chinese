# Tiny Rogues Chinese

Tiny Rogues 简体中文汉化，基于 BepInEx 6。支持对话、物品和技能说明、特质、职业、精通天赋及部分界面文本。

目前处于 alpha 阶段，适配 Windows 版 **0.2.8.6**。仍有漏翻和未经游玩确认的内容，详见[已知问题与测试记录](docs/coverage.md)。

## 安装

需要 [BepInEx 6 IL2CPP](https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html)。当前测试使用 6.0.0-be.759。

1. 退出游戏。首次安装 BepInEx 后先启动一次游戏，再退出。
2. 将编译好的 `TinyRogues.Chinese` 文件夹放入 `BepInEx/plugins/`。
3. 已安装其他汉化的，请先将对应插件移到 `BepInEx/plugins/` 之外备份。不要同时加载 XUnity.AutoTranslator 或 Tiny Rogues TMPFallback。
4. 启动游戏。日志出现 `Independent localization active` 表示插件已启用。

```text
BepInEx/plugins/TinyRogues.Chinese/
├─ TinyRogues.Chinese.dll
├─ locales/zh-Hans/
└─ overrides/                 # 可选，个人译文
```

默认读取系统字体 `simhei.ttf`，安装包不包含字体。需要更换字体时，修改 `BepInEx/config/community.tinyrogues.chinese.cfg` 中 `[Font]` 的 `Path`，然后重启游戏。字体文件不存在时插件不会启用翻译。

卸载：退出游戏，将 `TinyRogues.Chinese` 文件夹移出插件目录。升级前请备份自己的 `overrides/`。

源码现已公开，安装包将在首轮游玩测试后发布。

## 修改译文

译文存放在 `locales/zh-Hans/*.json`。插件会自动重载通过校验的修改；重新打开对应界面即可检查。个人译法放在 `overrides/`，优先于内置译文。

格式、覆盖示例和更新工具见[译文维护指南](docs/translations.md)。未匹配文本可记录到 `captures/missing.jsonl`。

## 反馈与贡献

漏翻、错译和显示问题请提交 [Issue](https://github.com/FIELD9z/TinyRogues-Chinese/issues)。请附游戏版本、出现位置和截图，能提供完整英文更好。欢迎通过 PR 修改译文或修复插件，详见[贡献指南](CONTRIBUTING.md)。

## 编译

需要 .NET 9 SDK、Python 3，以及已运行过 BepInEx 的本地游戏。

```powershell
python tools/catalog.py validate .
dotnet run --project tests/CoreTests.csproj -- .
dotnet build src/Plugin/TinyRogues.Chinese.csproj -c Release "-p:GameDir=D:\Games\Tiny Rogues"
```

DLL 输出到 `src/Plugin/bin/Release/netstandard2.1/`。生成安装包：

```powershell
./tools/build_release.ps1 -GameDir "D:\Games\Tiny Rogues"
```

## 许可

本项目代码和译文贡献采用 [MIT](LICENSE) 许可。游戏原始文本及资源归相应权利人所有。项目不包含游戏文件、运行框架或字体，也不依赖其他汉化项目的插件和中文词库。

[架构](docs/architecture.md) · [术语](docs/terminology.md) · [依赖](docs/dependencies.md)
