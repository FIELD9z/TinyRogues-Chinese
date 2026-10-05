# 依赖与发布边界

本项目原创插件代码和新译文单独维护。游戏适配通过通用框架完成，不调用其他汉化作者的插件或词库。

| 依赖 | 使用方式 | 官方来源 |
|---|---|---|
| BepInEx 6 IL2CPP | 游戏中的插件加载运行环境，编译时引用本地程序集。 | [BepInEx](https://github.com/BepInEx/BepInEx) |
| Harmony | 方法补丁接口，引用 BepInEx 环境中的 `0Harmony.dll`。 | [Harmony](https://github.com/pardeike/Harmony) |
| Il2CppInterop | IL2CPP 互操作运行环境，使用 BepInEx 环境中的版本。 | [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) |
| Unity / TextMeshPro / 游戏互操作程序集 | 由自己的游戏安装和 BepInEx 生成，仅本地引用。 | [Unity TextMeshPro 文档](https://docs.unity3d.com/Packages/com.unity.textmeshpro@3.0/manual/index.html) |
| UnityPy | 开发时只读抽取原始英文候选文本，当前 requirements 固定 1.25.4。 | [UnityPy](https://github.com/K0lb3/UnityPy) |
| .NET SDK / Python | 编译、独立测试和维护工具。 | [.NET](https://dotnet.microsoft.com/)、[Python](https://www.python.org/) |
| 本地中文字体 | 运行时读取用户配置的字体文件；默认查找系统 `simhei.ttf`。 | 由用户本机提供 |

依赖的许可证由对应项目自身规定，不能用本项目许可覆盖。发布若将来决定附带某个第三方组件，应先核对所选版本的许可证及分发要求，随包提供需要的许可文件。当前发布包不附带这些依赖。

## 包内应有与不应有

应有：自己的插件 DLL、自己的中文译文 JSON、安装与贡献文档、适用的项目许可。

不应有：游戏可执行文件、游戏 DLL／资源包、`BepInEx/interop/`、Unity 程序集、原作者汉化插件或词库、未取得分发许可的字体、个人捕获日志和原始抽取资源清单。

译文条目保存英文源句用于匹配和维护。游戏原始文字、角色、物品及品牌仍属于各自权利人。本项目许可仅适用于本项目可授权的原创贡献。
