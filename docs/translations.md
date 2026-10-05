# 译文维护与版本更新

## 写个人覆盖包

在插件 DLL 旁创建 `overrides/my-terms.json`，保存为 UTF-8：

```json
{
  "schemaVersion": 1,
  "language": "zh-Hans",
  "name": "我的术语选择",
  "priority": 100,
  "entries": [
    {
      "id": "personal.attack-speed",
      "source": "Attack Speed",
      "translation": "攻速",
      "context": "*",
      "kind": "exact",
      "status": "translated"
    }
  ]
}
```

保存后等待日志显示 `Catalog loaded`，再重新打开相应界面。自己的覆盖文件应与内置包分开备份，升级内置译文时保留 `overrides/`。

`source` 需逐字符对应原文。`context` 可直接采用捕获记录中的值。只有想在所有语境使用相同译法时才写 `*`；例如 Fire 可能指火焰，也可能是按键“开火”，应按上下文区分。

## 字段与审阅状态

| 字段 | 用途 |
|---|---|
| `id` | 条目的维护标识；文件内必须唯一，提交内置包时在所有内置文件中唯一。 |
| `source` | 完整原始英文，保留空格、换行和格式标记。 |
| `translation` | 中文；保留数值、变量、图标和必须保护的标记。 |
| `context` | 入口语境，例如 `dialogue`、`skill-template`、`description-template`，或 `*`。 |
| `kind` | `exact` 完整精确匹配；`template` 使用受限的命名变量。 |
| `status` | `translated`、`reviewed` 会生效；`draft`、`needs-review`、`obsolete` 不会生效。 |
| `note` | 翻译依据、歧义或待验证事项，不显示给玩家。 |
| `protectedTokens` | 除通用校验之外，必须原样保留的原生变量列表。 |

`translated` 表示已经有译文，并不等于人工复核。`reviewed` 应在另一次语义和显示审阅后使用。未弄清机制的片段先标 `needs-review`，不要为了提高数量启用猜测译文。

在模板中保留同名变量及类型。`number`、`key`、`text` 原样回填；`term` 专门用于物品或属性名称，必须找到精确词条才翻译，可保留已确认的颜色标签。未知名称会让整个模板不匹配，以便记录真实漏翻。它不进行自由文本机器翻译，也不允许任意标签。例如：

```json
{
  "id": "example.damage",
  "source": "Damage: {number:amount}",
  "translation": "伤害：{number:amount}",
  "context": "*",
  "kind": "template",
  "status": "translated"
}
```

游戏自己的占位符与本项目模板变量是两套机制。对属性、技能、特质模板中的 `(damage)` 等原生变量不得改名，数值和运算标记也不得翻译。格式标签的结构和换行需要保持。遇到不清楚的标记先确认游戏解析行为，再决定能否翻译其内部文字。

已确认 `[[文字]]`、`##` 和 `((文字))` 是原生着色或格式运算标记；其中的普通文字可以翻译，例如 `[[permanent]]` 可写成 `[[永久]]`。必须保留运算符、原生变量和数字，不能把整段带括号的英文一概视为不可翻译变量。

## 从游戏更新提取差异

抽取脚本只读本地游戏英文资源，不读取其他汉化的中文词库。依赖 UnityPy，安装到自己的开发虚拟环境：

```powershell
python -m pip install -r tools/requirements.txt
python tools/extract_sources.py --game-dir "D:\SteamLibrary\steamapps\common\Tiny Rogues" --out work/source-new.json
python tools/catalog.py diff work/source-old.json work/source-new.json --out work/source-diff.json
python tools/catalog.py coverage . work/source-new.json --out work/coverage.json
```

保留对应游戏版本的旧抽取清单，人工检查新增、修改和删除项。Dialogue_EN 内部标识可以用于比较对话版本；未类型化资源和元数据项使用内容相关标识，改字后可能表现为“删除＋新增”。不能自动把旧译文绑定到新的英文。

抽取结果包含内部名称、调试文本和未使用内容。先确认文本确实向玩家展示及其完整语境，再编写译文。覆盖报告统计的是抽取来源与译文的匹配，不是实际画面的覆盖率。

## 提交前验证

```powershell
python tools/catalog.py validate .
dotnet run --project tests/CoreTests.csproj -- .
```

Python 校验面向 `locales/zh-Hans/` 中的内置包。个人覆盖包仍需通过核心加载校验和游戏日志确认；不要把“内置校验通过”当成任意覆盖文件也已通过。之后检查受影响场景：数值是否仍随属性变化、图标是否保留、长句是否被裁切、选项和提示是否正确。
