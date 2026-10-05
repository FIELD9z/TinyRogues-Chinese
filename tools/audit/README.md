# 文本核查工具

将本地游戏源文、旧词库英文键、运行捕获、测试输入和当前词库合并，逐条调用当前核心匹配器，并输出完整待查记录。需要 Python 3 和 .NET 9。

大型原文清单和旧插件运行数据保留在本地，不提交到源码仓库。`prepare.py` 读取本项目开发目录中已有的提取结果；目录结构不同的使用者应调整导入路径。它不是通用游戏资源提取器。

```powershell
python tools/audit/extract_sources.py <Tiny Rogues_Data目录> <补充提取目录>
python tools/audit/prepare.py --workspace <开发目录> --game-dir <游戏目录> --out <核查目录> --supplement <补充原文.jsonl>
dotnet run --project tools/audit/Audit.csproj -- . <核查目录>
python tools/audit/report.py <核查目录>
python tools/audit/test_audit.py
```

补充提取需要 Python 环境中的 UnityPy。元数据字符串按声明表完整枚举；Unity 字段读取失败逐对象登记，再从对象字节寻找长度前缀候选。原始字节候选限可打印 UTF-8、含 ASCII 字母且长度不超过 4 MiB；它不能证明其他编码、图片中文字或全部动态拼接分支已提取。字段解析失败会留在 manifest 中，即使该对象同时获得了字节候选。

`--supplement` 可以重复。每行包含 `text` 或 `source` 字段及来源信息；补充提取的 `extraction-manifest.json` 放在同目录，记录提取范围、输入哈希及失败原因。

输出的 `report.md` 提供分类索引。`checked.jsonl` 和 `all-review-items.jsonl` 保留全部检查项，没有只取前几条样本的限制。`accounting.json` 验证输入与结果一一对应、原文及来源未改变；报告拒绝哈希不符的旧结果。

只有记录了实际入口的输入才按该入口检查；提取器或旧文件推定的入口只产生查找线索。短字母、混排、技术字符串、模板、正则、长文本和停用词条均保留。无法解析的物理行另行登记，不纳入成功数。

静态匹配不等于原生调用、正常游玩、译义或排版验证。历史原生结果单独附带运行编号，不自动升级为当前版本通过。所有仍含拉丁字母的输出均进入复核，包括按键名和代码；不使用缩写白名单隐藏普通英文。

数值和 sprite 检查用于发现候选问题，不代替语义判断。例如版本号前的连字符可能被数字检测器误判为负号。技术字符串是否不需要翻译，也必须有具体用途证据才能确定。
