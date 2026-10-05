using TinyRogues.Chinese.Core;

try
{
var checks = 0;
void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); checks++; }
TranslationEntry Entry(string source, string target, string context = "*", string kind = "exact") => new() { Id = Guid.NewGuid().ToString(), Source = source, Translation = target, Context = context, Kind = kind };
TranslationPack Pack(params TranslationEntry[] entries) => new() { Entries = entries.ToList() };
Catalog Compile(TranslationPack pack) => new(new[] { (pack, false, "base") });
string Tr(Catalog catalog, string text, string context = "*") => catalog.TryTranslate(text, context, out var result) ? result : text;
void Invalid(Action action, string reason) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(reason); }
var basePack = Pack(Entry("Fire", "火焰"), Entry("Fire", "开火", "ui:controls"), Entry("Fire damage", "火焰伤害"));
var catalog = Compile(basePack);
Check(Tr(catalog, "Fire", "ui:controls") == "开火", "Context-specific meaning lost");
Check(Tr(catalog, "Fire", "ui:equipment") == "火焰", "Global fallback lost");
Check(Tr(catalog, "Fi") == "Fi", "Partial dialogue expanded");
Check(Tr(catalog, "Fire damage") == "火焰伤害", "Shared-prefix whole sentence failed");
Check(Tr(catalog, "Fire unknown") == "Fire unknown", "Unknown sentence guessed");
Check(Tr(catalog, "火焰") == "火焰", "Chinese changed");
Check(Tr(catalog, " Fire") == " Fire", "Source whitespace altered");
Check(catalog.TryTranslateLabel("<color=#ff00ff><b>Fire</b></color>", "styled-ui", out var styledLabel) && styledLabel == "<color=#ff00ff><b>火焰</b></color>", "Whole styled label did not preserve wrappers");
Check(!catalog.TryTranslateLabel("<color=red>Fire unknown</color>", "styled-ui", out _), "Styled prose translated fragments");
Check(!catalog.TryTranslateLabel("<b>Fire</i>", "styled-ui", out _), "Mismatched styled wrappers accepted");
var marker = Compile(Pack(Entry("[[25% damage]] ##skill", "[[25% 伤害]] ##技能", "skill-template")));
Check(Tr(marker, "[[25% damage]] ##skill", "skill-template") == "[[25% 伤害]] ##技能", "Visible marker prose blocked");
Invalid(() => Compile(Pack(Entry("[[25% damage]]", "[[26% 伤害]]"))), "Literal gameplay number changed");
Check(Tr(Compile(Pack(Entry("Slot1", "槽位1"))), "Slot1") == "槽位1", "Number next to an English label was miscounted");
Invalid(() => Compile(Pack(Entry("Slot1", "槽位2"))), "Number next to an English label changed");
Invalid(() => Compile(Pack(Entry("[[damage]]", "伤害"))), "Color operator removed");
var red = Compile(Pack(Entry("((emptying))", "((清空))", "description-template")));
Check(Tr(red, "((emptying))", "description-template") == "((清空))", "Visible red marker prose blocked");
Invalid(() => Compile(Pack(Entry("((emptying))", "清空", "description-template"))), "Red color operator removed");
Invalid(() => Compile(Pack(Entry("((x0))", "((y0))", "description-template"))), "Native variable inside color marker changed");
var terms = Compile(Pack(Entry("Candy", "糖果"), Entry("{term:food} grants [[+1]] Intelligence.", "{term:food}提供 [[+1]] 智力。", "dialogue", "template")));
Check(Tr(terms, "<color=#abcdef>Candy</color> grants [[+1]] Intelligence.", "dialogue") == "<color=#abcdef>糖果</color>提供 [[+1]] 智力。", "Known dynamic term lost styling or value");
Check(Tr(terms, "Unknown food grants [[+1]] Intelligence.", "dialogue") == "Unknown food grants [[+1]] Intelligence.", "Unknown term was guessed");
Check(Tr(terms, "<b>Candy</i> grants [[+1]] Intelligence.", "dialogue").StartsWith("<b>Candy"), "Malformed term wrapper accepted");
Invalid(() => Compile(Pack(Entry("{key:same}/{number:same}", "{key:same} 比 {number:same}", kind: "template"))), "Conflicting repeated variable types accepted");
var overridePack = Pack(Entry("Fire", "烈焰"));
catalog = new Catalog(new[] { (basePack, false, "base"), (overridePack, true, "user") });
Check(Tr(catalog, "Fire", "ui:controls") == "烈焰", "User override must supersede built-in contexts");
var tagged = Compile(Pack(Entry("Gain <color=red>{number:amount}</color> armor<sprite name=Armor>.", "获得 <color=red>{number:amount}</color> 点护甲<sprite name=Armor>。", kind: "template")));
Check(Tr(tagged, "Gain <color=red>+12.5</color> armor<sprite name=Armor>.") == "获得 <color=red>+12.5</color> 点护甲<sprite name=Armor>。", "Tags/number not preserved");
Check(Tr(tagged, "prefix Gain <color=red>12</color> armor<sprite name=Armor>.").StartsWith("prefix"), "Template accepted prefix");
Check(Tr(tagged, "Gain <color=red>12</color> armor<sprite name=Armor>. suffix").EndsWith("suffix"), "Template accepted suffix");
var rebound = Compile(Pack(Entry("Hold {key:binding} to heal.", "长按 {key:binding} 恢复生命。", kind: "template")));
Check(Tr(rebound, "Hold RT to heal.") == "长按 RT 恢复生命。", "Rebound controller key changed");
Check(Tr(rebound, "Hold $1 to heal.") == "长按 $1 恢复生命。", "Captured dollars interpreted as replacement");
Check(Tr(rebound, "Hold <bad> to heal.") == "Hold <bad> to heal.", "Unsafe capture accepted tag");
var repeated = Compile(Pack(Entry("{number:n}/{number:n}", "{number:n} 比 {number:n}", kind: "template")));
Check(Tr(repeated, "2/2") == "2 比 2", "Repeated variable lost");
Check(Tr(repeated, "2/3") == "2/3", "Repeated variable mismatch accepted");
Invalid(() => Compile(Pack(Entry("A<sprite name=Heart>", "甲"))), "Missing icon accepted");
Invalid(() => Compile(Pack(Entry("<color=red>A</color>", "<color=blue>甲</color>"))), "Changed color accepted");
Invalid(() => Compile(Pack(Entry("A {0}", "甲 {1}"))), "Changed formatter index accepted");
Invalid(() => Compile(Pack(Entry("A\nB", "甲乙"))), "Line break lost");
Invalid(() => Compile(Pack(Entry("A", "甲", kind: "regex"))), "Arbitrary regex accepted");
Invalid(() => Compile(Pack(Entry("Damage (damage)", "伤害", "skill-template"))), "Native value macro lost");
var prose = Compile(Pack(Entry("Damage (multiplicative)", "伤害（乘算）", "description-template")));
Check(Tr(prose, "Damage (multiplicative)", "description-template") == "伤害（乘算）", "Visible parenthetical mistaken for native macro");
Invalid(() => Compile(Pack(Entry("A", "甲"), Entry("A", "乙"))), "Ambiguous duplicate accepted");
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var files = Directory.GetFiles(Path.Combine(root, "locales", "zh-Hans"), "*.json", SearchOption.AllDirectories);
catalog = new Catalog(files.Select(file => (PackJson.Read(file), false, file)));
Check(catalog.EntryCount > 0, "Repository catalog empty");
const string foodTutorial = "(*)<color=red>Meat</color> grant [[+1]] Strength.\n(*)<color=green>Candy</color> grant [[+1]] Dexterity.\n(*)<color=blue>Candy</color> grants [[+1]] Intelligence.";
Check(Tr(catalog, foodTutorial, "dialogue") == "(*)<color=red>肉</color>提供 [[+1]] 力量。\n(*)<color=green>糖果</color>提供 [[+1]] 敏捷。\n(*)<color=blue>糖果</color>提供 [[+1]] 智力。", "Complete food tutorial template did not translate before display");
var opening = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "opening-tutorial.json")))!;
var followup = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "followup-tutorial.json")))!;
foreach (var source in followup)
    Check(Tr(catalog, source, "dialogue") != source && !System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(Tr(catalog, source, "dialogue"), "<[^>]*>", ""), "[A-Za-z]{2,}"), "Later skill tutorial missing: " + source);
Check(Tr(catalog, followup[1].Replace("Mouse Right", "RT"), "dialogue").Contains("<color=#FCC926>RT</color>"), "Later skill tutorial lost rebound binding");
var panelTutorial = "If you want to know even\nmore about yourself, you can\nopen the Character Panel with <color=#FCC926>[C]</color>.";
Check(Tr(catalog, panelTutorial, "dialogue") == "想进一步了解\n自己的属性，可以按\n<color=#FCC926>[C]</color> 打开角色面板。", "Character-panel tutorial missed the captured complete sentence");
Check(Tr(catalog, panelTutorial.Replace("[C]", "[RB]"), "dialogue").Contains("<color=#FCC926>[RB]</color> 打开角色面板。"), "Character-panel tutorial lost a rebound gamepad key");
Check(Tr(catalog, "If you want to know", "dialogue") == "If you want to know", "Character-panel rule matched a typewriter prefix");
foreach (var source in opening)
{
    var translated = Tr(catalog, source, "dialogue");
    Check(translated != source && System.Text.RegularExpressions.Regex.IsMatch(translated, "[\\u4e00-\\u9fff]"), "Captured opening tutorial missing: " + source);
}
Check(Tr(catalog, opening[0].Replace(">W<", ">Up<").Replace(">A<", ">Left<").Replace(">S<", ">Down<").Replace(">D<", ">Right<"), "dialogue").Contains("<color=#A61FFF>Up</color>"), "Rebound movement binding not preserved");
Check(Tr(catalog, opening[1].Replace(">Space<", ">RB<"), "dialogue").StartsWith("按 <color=#A61FFF>RB</color>"), "Gamepad dash tutorial missing");
Check(Tr(catalog, opening[3].Replace(">Mouse Left<", ">RT<"), "dialogue").Contains("<color=#A61FFF>RT</color>"), "Gamepad attack binding not preserved");
var runtimeUI = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "runtime-ui.json")))!;
var weaponCards = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "tests", "fixtures", "weapon-cards.json")))!;
foreach (var source in weaponCards)
{
    Check(catalog.TryTranslateLabel(source, "styled-ui", out var translated), "Actual starter weapon card did not translate");
    var visible = System.Text.RegularExpressions.Regex.Replace(translated, "<[^>]*>", "");
    Check(!System.Text.RegularExpressions.Regex.IsMatch(visible, "[A-Za-z]{2,}"), "English remains in starter card: " + visible);
    var markup = new System.Text.RegularExpressions.Regex("<[^>]*>");
    Check(markup.Matches(source).Select(m => m.Value).SequenceEqual(markup.Matches(translated).Select(m => m.Value)), "Starter card colors/icons changed");
    var numbers = new System.Text.RegularExpressions.Regex(@"\d+(?:[.,]\d+)?");
    Check(numbers.Matches(markup.Replace(source, "")).Select(m => m.Value).SequenceEqual(numbers.Matches(visible).Select(m => m.Value)), "Starter card numerical values changed");
    var altered = source.Replace("224", "9876").Replace("204", "1234").Replace("141", "5678");
    Check(catalog.TryTranslateLabel(altered, "styled-ui", out var variant) && !System.Text.RegularExpressions.Regex.IsMatch(markup.Replace(variant, ""), "[A-Za-z]{2,}"), "Card matcher depends on captured damage values");
    Check(!WeaponCards.TryTranslate(catalog, "An NPC discusses " + markup.Replace(source, " ").Replace('\n', ' '), out _), "Weapon schema accepted prose instead of stat rows");
}
Check(!WeaponCards.TryTranslate(catalog, "A Common Bow does Thrusting damage.", out _), "Card terms leaked into ordinary prose");
Check(catalog.TryTranslateRichLine("•Hits <color=#FFFFFF>2</color> times.", "weapon-effect", out var hits) && hits == "•命中 <color=#FFFFFF>2</color> 次。", "Dynamic hit count lost its color or number");
Check(catalog.TryTranslateRichLine("•Evolves at Upgrade Level <color=#123abc>12</color> with the correct Material.", "weapon-effect", out var evolution) && evolution == "•升级等级达到 <color=#123abc>12</color> 且拥有所需材料时进化。", "Evolution text depends on original level/color");
Check(catalog.TryTranslateRichLine("(<color=#FFFFFF>+2.5%</color> currently)", "weapon-effect", out var currentValue) && currentValue == "（当前<color=#FFFFFF>+2.5%</color>）", "Current value suffix lost formatting");
Check(catalog.TryTranslateRichLine("•Inflicts: <color=red>Burn</color>", "weapon-effect", out var status) && !status.Contains("Burn") && status.Contains("<color=red>"), "Status value did not translate independently of its color");
Check(!catalog.TryTranslateRichLine("•Inflicts: Unknown Status", "weapon-effect", out _), "Unknown status silently counted as translated");
Check(!catalog.TryTranslateRichLine("A story about someone who Hits 2 times.", "weapon-effect", out _), "Card grammar matched ordinary prose");
var scopedRich = Compile(Pack(Entry("{term:n}", "{term:n}", "weapon-effect", "template")));
Check(!scopedRich.TryTranslateRichLine("Unknown", "weapon-effect", out _), "Unknown term-only template recursed");
var pickup = opening.Single(s => s.StartsWith("Pick up items with") && s.Contains("Drag and drop"));
var controllerPickup = opening.Single(s => s.StartsWith("Pick up items with") && s.Contains("Use "));
var controllerTarget = Tr(catalog, controllerPickup, "dialogue");
Check(controllerTarget == "按 <color=#FCC926>[A]</color> 拾取物品，\n打开<color=#FCC926>背包</color>请按 <color=#FCC926>[B]</color>。\n按 <color=#FCC926>[RT]</color>\n<color=#FCC926>装备</color>武器。", "Actual controller pickup branch did not translate before dialogue");
Check(Tr(catalog, controllerPickup.Replace("[A]", "[X]").Replace("[B]", "[Y]").Replace("[RT]", "[RB]"), "dialogue").Contains("<color=#FCC926>[RB]</color>"), "Controller equip branch lost rebound binding");
Check(!catalog.TryTranslate(controllerPickup[..^1], "dialogue", out _), "Incomplete controller dialogue prefix was expanded");
var reboundPickup = Tr(catalog, pickup.Replace("[E]", "[RB]").Replace("[F]", "[Y]"), "dialogue");
Check(reboundPickup.Contains("[RB]") && reboundPickup.Contains("[Y]") && !reboundPickup.Contains("Pick up"), "Pickup tutorial lost rebound keys");
foreach (var source in runtimeUI)
    Check(catalog.TryTranslateLabel(source, "styled-ui", out var translated) && translated != source && System.Text.RegularExpressions.Regex.IsMatch(translated, "[\\u4e00-\\u9fff]"), "Captured complete UI missing: " + source);
Check(catalog.TryTranslateLabel("Floor 10 - 3 <mspace=1em>12:34:56", "styled-ui", out var timed) && timed == "第 10 层 - 3 <mspace=1em>12:34:56", "Progress time or room values changed");
Check(catalog.TryTranslateRichLine("•Each point of 智力 grants this 武器 +2.5% 暴击伤害.", "weapon-effect", out var attrDebug), "Mixed attribute card failed");
Check(catalog.TryTranslateRichLine("•Attacks trigger a <color=red>Chain </color><color=blue>Lightning</color> 7.5 times per second. [Tick]", "weapon-effect", out var chain) && !chain.Contains("Chain") && !chain.Contains("Lightning") && chain.Contains("7.5") && chain.Contains("<color=red>") && chain.Contains("<color=blue>"), "A multiword action split across color tags did not translate");
Check(catalog.TryTranslateRichLine("•On hit, 35% chance to trigger a Chain Lightning that deals 123 to 456 Lightning Damage.", "weapon-effect", out var onHit) && onHit.Contains("35%") && onHit.Contains("123至456") && !onHit.Contains("chance"), "Dynamic trigger chance or range changed");
Check(!catalog.TryTranslateRichLine("•On hit, 35% chance to trigger a Future Unknown Action that deals 123 to 456 Lightning Damage.", "weapon-effect", out _), "A future unknown action silently counted as translated");
Check(catalog.TryTranslateRichLine("•Gains +9 Upgrade Level per Aura you have. ", "weapon-effect", out var auraLevel) && auraLevel.Contains("+9"), "Upgrade level gain lost its positive sign");
Check(catalog.TryTranslateRichLine("7-Set: Angel Disguise", "weapon-effect", out var setLine) && setLine.Contains("7") && setLine.Contains("天使伪装"), "Dynamic set piece count depends on a captured value");
foreach (var soulSource in new[] {
    "Grants [[37]]\nSouls<sprite name=\"Soul\">.",
    "Grants <color=#00E317>37</color>\nSouls<sprite name=\"Soul\">.",
    "Grants <color=#00E317>37</color>\n<color=#5ECCE3>Souls</color><sprite name=\"Soul\">." })
{
    var soulTarget = Tr(catalog, soulSource, "styled-input");
    Check(soulTarget.Contains("37") && soulTarget.Contains("灵魂") && !soulTarget.Contains("Grants"), "Soul reward missed a raw/partially styled/fully styled stage or depended on amount 5");
    Check(System.Text.RegularExpressions.Regex.Matches(soulSource, "<[^>]*>").Select(m => m.Value).SequenceEqual(System.Text.RegularExpressions.Regex.Matches(soulTarget, "<[^>]*>").Select(m => m.Value)), "Soul reward changed a color or icon tag");
}
var cinderStats = Tr(catalog, "Highest Cinder Win Ever: 17\nCinder Sum High Score: 38\nAverage: 4.5\nAverage Of Last 5: NaN", "ui:Canvas/Cinder Menu/Panel/Cinder Modifier Description");
Check(cinderStats == "获胜时的最高余烬等级：17\n余烬总和最高纪录：38\n平均值：4.5\n最近 5 局平均值：NaN", "Cinder statistics confused values or retained a fixed zero-state translation");
Check(Tr(catalog, "<color=#ABCDEF>37</color>/<color=#123456>81</color> Points", "styled-ui") == "<color=#ABCDEF>37</color>/<color=#123456>81</color> 点", "Progress points depended on one color or changed counters");
Check(Tr(catalog, "Progress beyond floor 7.\nHigh Score: Floor 11", "ui:Canvas/World Objective/Description Text") == "推进至第 7 层之后。\n最高纪录：第 11 层", "Objective and high score were interchanged");
Check(Tr(catalog, "<color=#A1B2C3>Cinder 19</color>", "styled-input") == "<color=#A1B2C3>余烬 19</color>", "Cinder label required an unrelated context or a fixed style color");
Check(!catalog.TryTranslateLabel("Have 7 unknown future objects equipped at the same time.\nHigh Score: 2/7 Objects Equipped", "styled-ui", out _), "Unknown objective was silently translated by a broad fragment rule");
var flaskHint = "Oh, and if you run into any trouble,\ndon't forget you can use your Flask<sprite name=Flask>\nwith holding <color=#A61FFF>Mouse 5</color> to recover a <color=#FF3326>Heart</color><sprite name=\"Heart\">.";
var flaskHintTarget = Tr(catalog, flaskHint, "dialogue");
Check(flaskHintTarget.Contains("Mouse 5") && flaskHintTarget.Contains("回复一颗") && !flaskHintTarget.Contains("recover"), "Flask tutorial lost its complete message or actual key binding");
Check(System.Text.RegularExpressions.Regex.Matches(flaskHint, "<[^>]*>").Select(m => m.Value).SequenceEqual(System.Text.RegularExpressions.Regex.Matches(flaskHintTarget, "<[^>]*>").Select(m => m.Value)), "Flask tutorial changed icon/color order");
var decoratedTitle = string.Concat("BACKUP ARMORY".Select((letter, index) => $"<color={(index % 2 == 0 ? "#FF0000" : "#00FF00")}>{letter}</color>"));
Check(catalog.TryTranslateLabel(decoratedTitle, "ui:Canvas/Main Header", out var decoratedTarget) && System.Text.RegularExpressions.Regex.Replace(decoratedTarget, "<[^>]*>", "") == "备用军械库", "A complete title with individually colored glyphs missed its translation");
Check(System.Text.RegularExpressions.Regex.Matches(decoratedTitle, "<[^>]*>").Select(m => m.Value).SequenceEqual(System.Text.RegularExpressions.Regex.Matches(decoratedTarget, "<[^>]*>").Select(m => m.Value)), "Decorated title adapter discarded or reordered color tags");
Check(!catalog.TryTranslateLabel(string.Concat("UNKNOWN FUTURE TITLE".Select(letter => $"<color=#FF0000>{letter}</color>")), "ui:Canvas/Main Header", out _), "Decorated unknown title was translated by fragments");
Console.WriteLine($"PASS: {checks} behavioral checks; {catalog.EntryCount} independently authored active entries across {files.Length} packs.");
return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.Message}");
    return 1;
}
