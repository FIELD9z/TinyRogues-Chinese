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
foreach (var source in opening)
{
    var translated = Tr(catalog, source, "dialogue");
    Check(translated != source && System.Text.RegularExpressions.Regex.IsMatch(translated, "[\\u4e00-\\u9fff]"), "Captured opening tutorial missing: " + source);
}
Check(Tr(catalog, opening[0].Replace(">W<", ">Up<").Replace(">A<", ">Left<").Replace(">S<", ">Down<").Replace(">D<", ">Right<"), "dialogue").Contains("<color=#A61FFF>Up</color>"), "Rebound movement binding not preserved");
Check(Tr(catalog, opening[1].Replace(">Space<", ">RB<"), "dialogue").StartsWith("按 <color=#A61FFF>RB</color>"), "Gamepad dash tutorial missing");
Check(Tr(catalog, opening[3].Replace(">Mouse Left<", ">RT<"), "dialogue").Contains("<color=#A61FFF>RT</color>"), "Gamepad attack binding not preserved");
Console.WriteLine($"PASS: {checks} behavioral checks; {catalog.EntryCount} independently authored active entries across {files.Length} packs.");
return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL: {error.Message}");
    return 1;
}
