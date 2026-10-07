using System;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UI.Dialogue_System;
using UnityEngine;

namespace TinyRogues.Chinese;

// Opt-in native smoke test. Normal play never creates or runs this component.
public sealed class RuntimeVerification : MonoBehaviour
{
    private float _begin;
    private float _messageBegin;
    private bool _started;
    private bool _finished;
    private float _exitAt;
    private int _samples;
    private int _largest;
    private bool _sawChinese;
    private int _phase;
    private string[] _opening = Array.Empty<string>();
    private string[] _keyWords = Array.Empty<string>();
    private Il2CppSystem.Action? _choiceCallback;
    private Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip>? _choiceTooltips;
    public RuntimeVerification(IntPtr pointer) : base(pointer) { }
    private void Awake() => _begin = Time.unscaledTime;
    private void Update()
    {
        if (_exitAt > 0 && Time.unscaledTime >= _exitAt) { _exitAt = 0; Application.Quit(); }
        if (_finished) return;
        try
        {
            if (Time.unscaledTime - _begin > 35 + 2 * Math.Max(_opening.Length + 4, 15)) throw new InvalidOperationException("Native diagnostic timed out.");
            if (Time.unscaledTime - _begin < 5) return;
            var controller = DialogueSystemController.Instance;
            if (controller == null || controller.text == null) return;
            if (!_started)
            {
                VerifyUI();
                VerifyDescriptions();
                StartMessage(controller);
                _started = true;
            }
            var visible = Regex.Replace(controller.text.text ?? "", "<[^>]*>", "");
            foreach (Match word in Regex.Matches(visible, "[A-Za-z]{2,}"))
                if (!_keyWords.Any(key => key.StartsWith(word.Value, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Unexpected English appeared during translated typewriter: " + word.Value);
            _sawChinese |= Regex.IsMatch(visible, "[\u4e00-\u9fff]");
            _largest = Math.Max(_largest, controller.currentLetterCount);
            _samples++;
            if (Time.realtimeSinceStartup - _messageBegin < 2) return;
            if (!_sawChinese || _largest < 2) throw new InvalidOperationException("Native Chinese typewriter did not advance.");
            var font = controller.text.font;
            if (font == null || !font.HasCharacter('谁', true, true)) throw new InvalidOperationException("Independent Chinese fallback font failed glyph coverage.");
            if (_phase == _opening.Length + 3)
            {
                controller.text.ForceMeshUpdate(true, true);
                if (controller.text.textInfo.characterCount < 4 || controller.text.mesh.vertexCount < 4) throw new InvalidOperationException("Native Chinese dialogue mesh was not generated.");
                Plugin.Current.Info($"INDEPENDENT SELFTEST PASS: native UI exact/template/styling, Chinese font, three dialogue entrances and choice metadata, descriptions, assembled food sample, {_opening.Length} complete tutorial samples and native typewriter ({_samples} frames). No legacy translator loaded.");
                _finished = true;
                _exitAt = Time.unscaledTime + 1;
                return;
            }
            controller.HideMessage();
            if (Plugin.Current.DialogueBody != IntPtr.Zero) throw new InvalidOperationException("Dialogue ownership was not released.");
            ++_phase;
            StartMessage(controller);
        }
        catch (Exception error)
        {
            _finished = true;
            Plugin.Current.Error($"INDEPENDENT SELFTEST FAIL: {error}");
            Application.Quit(2);
        }
    }
    private void StartMessage(DialogueSystemController controller)
    {
        if (_opening.Length == 0)
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "opening-tutorial.json");
            using (var stream = File.OpenRead(path))
                _opening = (string[])new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).ReadObject(stream)!;
            var followupPath = Path.Combine(Path.GetDirectoryName(path)!, "followup-tutorial.json");
            using (var stream = File.OpenRead(followupPath))
                _opening = _opening.Concat((string[])new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).ReadObject(stream)!).ToArray();
            if (_opening.Length == 0) throw new InvalidDataException("Opening tutorial fixtures empty.");
        }
        var source = _phase >= 4 ? _opening[_phase - 4] : _phase == 3 ? "(*)<color=red>Meat</color> grant [[+1]] Strength.\n(*)<color=green>Candy</color> grant [[+1]] Dexterity.\n(*)<color=blue>Candy</color> grants [[+1]] Intelligence." : "Nothing can stop me!";
        var expected = Plugin.Current.Translate(source, "dialogue", false);
        // Binding names intentionally remain unchanged (Space, Mouse Left, etc.).
        // Native typewriting may currently show only a prefix of that binding.
        _keyWords = Regex.Matches(source, "<color=#A61FFF>([^<]+)</color>|<color=#FCC926>(\\[[^<]+\\])</color>").Cast<Match>()
            .SelectMany(binding => Regex.Matches(binding.Groups[1].Value + binding.Groups[2].Value, "[A-Za-z]+").Cast<Match>().Select(word => word.Value)).ToArray();
        if (!Regex.IsMatch(expected, "[\u4e00-\u9fff]")) throw new InvalidOperationException("Diagnostic dialogue absent from independent catalog.");
        if (_phase == 0 || _phase >= 3) controller.ShowMessage(source, Vector2.zero, 20f, null, null, false);
        else if (_phase == 1) controller.PromptMessage(source, Vector2.zero, (Il2CppSystem.Action?)null, null, false);
        else
        {
            _choiceCallback = (Il2CppSystem.Action)(Action)(() => { });
            _choiceTooltips = new Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip>();
            var choices = new Il2CppSystem.Collections.Generic.List<Choice>();
            choices.Add(new Choice("Talk", _choiceCallback, _choiceTooltips, false));
            choices.Add(new Choice("Leave", _choiceCallback, _choiceTooltips, true));
            controller.PromptMessage(source, Vector2.zero, choices.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Choice>>(), null, false, false);
            var translated = controller.givenChoices;
            if (translated.Count != 2 || translated[0].Text != "交谈" || translated[1].Text != "离开") throw new InvalidOperationException("Native choices were not translated.");
            if (translated[0].Callback.Pointer != _choiceCallback.Pointer || translated[0].Tooltips.Pointer != _choiceTooltips.Pointer || translated[0].IsCancelChoice || !translated[1].IsCancelChoice)
                throw new InvalidOperationException("Native choice callbacks/tooltips/cancel flag changed.");
        }
        if (controller.currentMessagePlain != expected) throw new InvalidOperationException("Dialogue did not receive independent complete translation.");
        _messageBegin = Time.realtimeSinceStartup;
        _largest = 0;
        _sawChinese = false;
    }

    private static void VerifyDescriptions()
    {
        var catalog = Plugin.Current.Catalog!;
        var skills = 0;
        foreach (var skill in Resources.FindObjectsOfTypeAll<Skill_System.SkillBehaviour>())
        {
            var source = skill.description;
            if (string.IsNullOrEmpty(source) || !catalog.TryTranslate(source, "skill-template", out _)) continue;
            string before;
            try { Plugin.Current.Catalog = null; before = skill.Description(false, true); }
            finally { Plugin.Current.Catalog = catalog; }
            var after = skill.Description(false, true);
            if (skill.description != source) throw new InvalidOperationException("Skill resource description was not restored.");
            VerifyRenderedValues(before, after, "skill " + skill.name);
            skills++;
        }
        var stats = 0;
        foreach (var stat in Resources.FindObjectsOfTypeAll<Stats.SimpleStat>())
        {
            var source = stat.activeDescription;
            if (string.IsNullOrEmpty(source) || !catalog.TryTranslate(source, "description-template", out var expected) || !Regex.IsMatch(expected, "[\u4e00-\u9fff]") || stat.StatWrapper == null) continue;
            var wrappers = new Il2CppSystem.Collections.Generic.List<Stats.StatWrapper>();
            wrappers.Add(stat.StatWrapper);
            var values = wrappers.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Stats.StatWrapper>>();
            var context = new Il2CppSystem.Collections.Generic.HashSet<string>();
            string before;
            try { Plugin.Current.Catalog = null; before = Stats.StatDescriptionUtility.FillInDescriptionValues(source, values, 1f, 1f, context, null, false, true); }
            finally { Plugin.Current.Catalog = catalog; }
            var after = Stats.StatDescriptionUtility.FillInDescriptionValues(source, values, 1f, 1f, context, null, false, true);
            VerifyRenderedValues(before, after, "stat " + stat.name);
            stats++;
        }
        if (skills < 3 || stats < 3) throw new InvalidOperationException($"Insufficient native resource tests: {skills} skills, {stats} stats.");
        Plugin.Current.Info($"NATIVE DESCRIPTION PASS: {skills} skills and {stats} stat templates; resource fields restored, numeric values preserved.");
        var root = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        var characters = Directory.GetFiles(Path.Combine(root, "locales", "zh-Hans"), "*.json", SearchOption.AllDirectories)
            .SelectMany(path => TinyRogues.Chinese.Core.PackJson.Read(path).Entries)
            .Where(entry => entry.Status is "translated" or "reviewed")
            .SelectMany(entry => entry.Translation).Where(character => character >= '\u3400' && character <= '\u9fff').Distinct().ToArray();
        var missing = characters.Where(character => UnityEngine.TextCore.LowLevel.FontEngine.GetGlyphIndex(character) == 0).ToArray();
        if (missing.Length != 0) throw new InvalidOperationException("Local font lacks translation glyphs: " + new string(missing));
        Plugin.Current.Info($"NATIVE FONT PASS: {characters.Length} distinct CJK glyphs in all active packs.");
        var mastery = 0;
        foreach (var perk in Resources.FindObjectsOfTypeAll<Meta_Perk_Tree.System.MetaPerk>())
        {
            if (string.IsNullOrEmpty(perk.description) || !catalog.TryTranslate(perk.description, "description-template", out _)) continue;
            var original = perk.description;
            VerifyGetter(() => perk.Description, "mastery " + perk.name);
            if (perk.description != original) throw new InvalidOperationException("Mastery description field was not restored.");
            if (!Regex.IsMatch(perk.Title, "[\u4e00-\u9fff]")) throw new InvalidOperationException("Mastery canonical title adapter failed: " + perk.name);
            mastery++;
        }
        var classes = 0;
        foreach (var playerClass in Resources.FindObjectsOfTypeAll<Player.PlayerClass>())
        {
            if (string.IsNullOrEmpty(playerClass.description) || !catalog.TryTranslate(playerClass.description, "description-template", out _)) continue;
            var original = playerClass.description;
            VerifyGetter(() => playerClass.Description, "class " + playerClass.name);
            if (playerClass.description != original) throw new InvalidOperationException("Class description field was not restored.");
            if (!Regex.IsMatch(playerClass.Name, "[\u4e00-\u9fff]")) throw new InvalidOperationException("Class name adapter failed: " + playerClass.name);
            classes++;
        }
        var cinders = 0;
        foreach (var cinder in Resources.FindObjectsOfTypeAll<Cinder_System.CinderModifier>())
        {
            if (string.IsNullOrEmpty(cinder.description) || !catalog.TryTranslate(cinder.description, "description-template", out _)) continue;
            var original = cinder.description;
            VerifyGetter(() => cinder.Description, "cinder " + cinder.name);
            if (cinder.description != original) throw new InvalidOperationException("Cinder description field was not restored.");
            cinders++;
        }
        if (mastery < 3 || classes < 3 || cinders < 3) throw new InvalidOperationException($"Insufficient progression samples: mastery={mastery}, classes={classes}, cinders={cinders}.");
        Plugin.Current.Info($"NATIVE PROGRESSION PASS: {mastery} mastery perks/titles, {classes} classes, {cinders} cinder modifiers; original fields and rendered numbers preserved.");
        var traits = 0;
        foreach (var trait in Resources.FindObjectsOfTypeAll<Traits.Trait>())
        {
            var source = trait.description;
            if (string.IsNullOrEmpty(source) || !catalog.TryTranslate(source, "description-template", out _)) continue;
            VerifyGetter(() => trait.Description(false, true), "trait " + trait.name);
            if (trait.description != source) throw new InvalidOperationException("Trait description field was not restored.");
            traits++;
        }
        var objectives = 0;
        foreach (var objective in Resources.FindObjectsOfTypeAll<World_Progression.WorldObjective>())
        {
            var main = objective.mainDescription;
            var condition = objective.conditionDescription;
            var effect = objective.effectDescription;
            if (!catalog.TryTranslate(main, "description-template", out _) && !catalog.TryTranslate(condition, "description-template", out _) && !catalog.TryTranslate(effect, "description-template", out _)) continue;
            VerifyGetter(() => objective.Description, "objective " + objective.name);
            var completeObjective = objective.Description;
            if (Regex.IsMatch(Regex.Replace(completeObjective, "<[^>]*>", ""), "[A-Za-z]"))
                throw new InvalidOperationException("English remains in generated world objective " + objective.name + ": " + completeObjective);
            if (objective.mainDescription != main || objective.conditionDescription != condition || objective.effectDescription != effect)
                throw new InvalidOperationException("World objective description fields were not restored.");
            objectives++;
        }
        Plugin.Current.Info($"NATIVE SCOPED SAMPLE PASS: {traits} traits, {objectives} world objectives; descriptive fields restored. Available title-scene resources only.");
        Plugin.Current.Info($"TESTED BUILD: game={Application.version}, Unity={Application.unityVersion}.");
        var display = new GameObject("Item names diagnostic");
        var itemNames = 0;
        var completeCards = new System.Collections.Generic.List<string>();
        var cardIssues = new System.Collections.Generic.List<string>();
        var nameIssues = new System.Collections.Generic.List<string>();
        var nativeFixtures = new System.Collections.Generic.List<string>();
        try
        {
            var label = display.AddComponent<TextMeshProUGUI>();
            foreach (var weapon in Resources.FindObjectsOfTypeAll<Weapons.Weapon>())
            {
                var name = weapon.NameTag;
                try
                {
                    string before;
                    try { Plugin.Current.Catalog = null; before = weapon.GetDescription(false, true); }
                    finally { Plugin.Current.Catalog = catalog; }
                    if (!string.IsNullOrEmpty(before))
                    {
                        completeCards.Add(before);
                        nativeFixtures.Add(NativeFixture("weapon", weapon.name, before));
                        label.text = UI.Text.TextMeshProxy.ApplyColorToText(weapon.GetDescription(false, true), true, label);
                        VerifyRenderedValues(before, label.text, "full weapon " + weapon.name);
                        string restored;
                        try { Plugin.Current.Catalog = null; restored = weapon.GetDescription(false, true); }
                        finally { Plugin.Current.Catalog = catalog; }
                        if (restored != before) throw new InvalidOperationException("Weapon descriptive fields or caches were not restored.");
                        var plain = Regex.Replace(label.text, "<[^>]*>", "");
                        if (Regex.IsMatch(plain, "[A-Za-z]{2,}")) cardIssues.Add(weapon.name + "\n" + plain);
                    }
                }
                catch (Exception error) { cardIssues.Add(weapon.name + ": GENERATION FAILED: " + error.Message); }
                if (!catalog.TryTranslateLabel(name, "styled-ui", out var expected) || !Regex.IsMatch(expected, "[\u4e00-\u9fff]"))
                {
                    if (catalog.TryTranslateLabel(name, "styled-ui", out var knownCode) && knownCode == name && Regex.IsMatch(name, "^[A-Z0-9_-]{2,12}$")) continue;
                    if (!string.IsNullOrEmpty(name) && !Regex.IsMatch(name, "[\u4e00-\u9fff]")) nameIssues.Add(weapon.name + " => " + name);
                    continue;
                }
                label.text = UI.Text.TextMeshProxy.ApplyColorToText(name, true, label);
                if (!Regex.IsMatch(label.text, "[\u4e00-\u9fff]")) throw new InvalidOperationException("Weapon label failed: " + name);
                itemNames++;
            }
        }
        finally { UnityEngine.Object.Destroy(display); }
        if (itemNames < 100) throw new InvalidOperationException("Insufficient native weapon name samples: " + itemNames);
        Plugin.Current.Info($"NATIVE ITEM LABEL PASS: {itemNames} weapon labels through the actual text styling and TMP path.");
        var directory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics");
        using (var stream = File.Create(Path.Combine(directory, "weapon-name-issues.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).WriteObject(stream, nameIssues.ToArray());
        Plugin.Current.Info($"NATIVE ITEM LABEL AUDIT: {nameIssues.Count} nonempty weapon labels not covered; already Chinese or empty labels excluded.");
        using (var stream = File.Create(Path.Combine(directory, "generated-weapon-cards.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).WriteObject(stream, completeCards.ToArray());
        using (var stream = File.Create(Path.Combine(directory, "weapon-card-issues.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).WriteObject(stream, cardIssues.ToArray());
        Plugin.Current.Info($"NATIVE WEAPON CARD AUDIT: {completeCards.Count} full descriptions generated, {cardIssues.Count} with remaining English or generation errors. Audit is not a blanket coverage pass.");
        var equipmentSources = new System.Collections.Generic.List<string>();
        var equipmentIssues = new System.Collections.Generic.List<string>();
        var equipmentLabel = new GameObject("Equipment card diagnostic").AddComponent<TextMeshProUGUI>();
        try
        {
            foreach (var equipment in Resources.FindObjectsOfTypeAll<Combat.Equipment.Equipment>())
            {
                try
                {
                    string source;
                    try { Plugin.Current.Catalog = null; source = equipment.Description(false, false, true); }
                    finally { Plugin.Current.Catalog = catalog; }
                    if (string.IsNullOrEmpty(source)) continue;
                    equipmentSources.Add(source);
                    nativeFixtures.Add(NativeFixture("equipment", equipment.name, source));
                    equipmentLabel.text = UI.Text.TextMeshProxy.ApplyColorToText(equipment.Description(false, false, true), true, equipmentLabel);
                    VerifyRenderedValues(source, equipmentLabel.text, "equipment " + equipment.name);
                    string restored;
                    try { Plugin.Current.Catalog = null; restored = equipment.Description(false, false, true); }
                    finally { Plugin.Current.Catalog = catalog; }
                    if (restored != source) throw new InvalidOperationException("Equipment descriptive fields or caches were not restored.");
                    var plain = Regex.Replace(equipmentLabel.text, "<[^>]*>", "");
                    if (Regex.IsMatch(plain, "[A-Za-z]{2,}")) equipmentIssues.Add(equipment.name + "\n" + plain);
                }
                catch (Exception error) { equipmentIssues.Add(equipment.name + ": GENERATION FAILED: " + error.Message); }
            }
        }
        finally { UnityEngine.Object.Destroy(equipmentLabel.gameObject); }
        using (var stream = File.Create(Path.Combine(directory, "generated-equipment-cards.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).WriteObject(stream, equipmentSources.ToArray());
        using (var stream = File.Create(Path.Combine(directory, "equipment-card-issues.json")))
            new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).WriteObject(stream, equipmentIssues.ToArray());
        File.WriteAllText(Path.Combine(directory, "native-card-fixtures.json"), "[" + string.Join(",", nativeFixtures) + "]");
        Plugin.Current.Info($"NATIVE EQUIPMENT CARD AUDIT: {equipmentSources.Count} full descriptions generated, {equipmentIssues.Count} with remaining English or generation errors; descriptive fields checked after rendering.");
        if (cardIssues.Concat(equipmentIssues).Any(issue => issue.Contains("GENERATION FAILED:")))
            throw new InvalidOperationException("Full card audit found a generation, value-preservation or restoration failure; see diagnostics.");
        if (nameIssues.Count > 0)
            throw new InvalidOperationException("Native weapon names are missing translations; see weapon-name-issues.json.");
    }
    private static string NativeFixture(string kind, string name, string source) =>
        "{\"id\":" + Core.PackJson.Quote(kind + ":" + name) + ",\"context\":\"native-card\",\"objectName\":\"Text\",\"nativeKind\":" + Core.PackJson.Quote(kind) + ",\"nativeName\":" + Core.PackJson.Quote(name) + ",\"source\":" + Core.PackJson.Quote(source) + "}";

    private static void VerifyGetter(Func<string> getter, string name)
    {
        var catalog = Plugin.Current.Catalog;
        string before;
        try { Plugin.Current.Catalog = null; before = getter(); }
        finally { Plugin.Current.Catalog = catalog; }
        var temporary = new GameObject("Progression text diagnostic");
        try
        {
            var label = temporary.AddComponent<TextMeshProUGUI>();
            var rendered = UI.Text.TextMeshProxy.ApplyColorToText(getter(), true, label);
            label.text = rendered;
            VerifyRenderedValues(before, label.text, name);
        }
        finally { UnityEngine.Object.Destroy(temporary); }
    }
    private static void VerifyRenderedValues(string before, string after, string name)
    {
        var plainBefore = Regex.Replace(before ?? "", "<[^>]*>", "");
        var plainAfter = Regex.Replace(after ?? "", "<[^>]*>", "");
        if (!Regex.IsMatch(plainAfter, "[\u4e00-\u9fff]")) throw new InvalidOperationException($"Native description did not translate: {name}: {before} -> {after}");
        var numbers = new Regex(@"[+\-]?\d+(?:[.,]\d+)?%?");
        var oldValues = numbers.Matches(plainBefore).Cast<Match>().Select(m => m.Value).OrderBy(x => x);
        var newValues = numbers.Matches(plainAfter).Cast<Match>().Select(m => m.Value).OrderBy(x => x);
        if (!oldValues.SequenceEqual(newValues)) throw new InvalidOperationException($"Native rendered numbers changed: {name}: {before} -> {after}");
    }
    private static void VerifyUI()
    {
        var temporary = new GameObject("Independent localization diagnostic");
        try
        {
            var label = temporary.AddComponent<TextMeshProUGUI>();
            label.text = "Attack Speed";
            if (label.text != "攻击速度") throw new InvalidOperationException("Native TMP setter failed.");
            label.SetText("Damage: {0}", 37f);
            if (label.text != "伤害：37") throw new InvalidOperationException($"Native formatted SetText failed: {label.text}");
            var styled = UI.Text.TextMeshProxy.ApplyColorToText("Weapon Damage", false, label);
            if (styled != "武器伤害") throw new InvalidOperationException("Styled equipment description adapter failed.");
            label.text = "<color=#ff00ff><b>Attack Speed</b></color>";
            if (label.text != "<color=#ff00ff><b>攻击速度</b></color>") throw new InvalidOperationException("Styled complete label failed.");
            var markers = UI.Text.TextMeshProxy.ApplyColorToText("[[至]] ##技能 [[非暴击伤害提高 +25%]]", true, label);
            if (markers != "<color=#00E317>至</color> 技能 <color=#00E317>非暴击伤害提高 +25%</color>") throw new InvalidOperationException("Native prose marker processing changed.");
            if (UI.Text.TextMeshProxy.ReplaceBracketedWithRedColor("((清空))") != "<color=red>清空</color>") throw new InvalidOperationException("Native red prose marker failed.");
            var fixtures = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "runtime-ui.json");
            if (File.Exists(fixtures))
            {
                string[] sources;
                using (var stream = File.OpenRead(fixtures))
                    sources = (string[])new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).ReadObject(stream)!;
                foreach (var source in sources)
                {
                    label.text = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label);
                    VerifyRenderedValues(source, label.text, "captured complete UI");
                    var remaining = Regex.Replace(label.text, "<[^>]*>", "");
                    // Multiplication notation is a numeric value, not prose. Only allow
                    // the identical x-number token when it was present in this input.
                    var multipliers = Regex.Matches(Regex.Replace(source, "<[^>]*>", ""), @"(?<![A-Za-z])x\d+(?:\.\d+)?").Cast<Match>().Select(m => m.Value).ToArray();
                    remaining = Regex.Replace(remaining, @"(?<![A-Za-z])x\d+(?:\.\d+)?", m => multipliers.Contains(m.Value) ? "" : m.Value);
                    if (Regex.IsMatch(remaining, "[A-Za-z]"))
                        throw new InvalidOperationException("English remains in captured complete UI: " + label.text);
                }
                Plugin.Current.Info($"NATIVE CAPTURED UI PASS: {sources.Length} complete samples; numeric values preserved; no English prose (identical source x-number multipliers retained).");
            }
            var cards = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "weapon-cards.json");
            if (!File.Exists(cards)) throw new FileNotFoundException("Starter weapon card fixtures are required for the native self-test.", cards);
            using (var stream = File.OpenRead(cards))
            {
                var sources = (string[])new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string[])).ReadObject(stream)!;
                foreach (var source in sources)
                {
                    label.text = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label);
                    VerifyRenderedValues(source, label.text, "starter weapon card");
                    if (Regex.IsMatch(Regex.Replace(label.text, "<[^>]*>", ""), "[A-Za-z]{2,}")) throw new InvalidOperationException("English remains in starter weapon card: " + label.text);
                }
                Plugin.Current.Info($"NATIVE STARTER CARD PASS: {sources.Length} captured complete cards; no English prose remains.");
            }
            label.text = "Unmapped diagnostic text QZXV";
            if (label.text != "Unmapped diagnostic text QZXV") throw new InvalidOperationException("Unknown text was changed.");
        }
        finally { UnityEngine.Object.Destroy(temporary); }
    }
}
