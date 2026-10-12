using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UI.Text.World_Text_Boxes;

[BepInPlugin("community.tinyrogues.world-module-probe", "Tiny Rogues World Module Probe", "0.1.0")]
public sealed class WorldModuleProbePlugin : BasePlugin
{
    private const string Switch = "-tinyrogues-world-module-probe";
    internal static string OutputPath = "";
    internal static string SaveRoot = "";

    public override void Load()
    {
        if (!Environment.GetCommandLineArgs().Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            Log.LogInfo("World module probe idle; dedicated switch not present.");
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        OutputPath = Path.Combine(pluginDirectory, "world-module-capture.jsonl");
        SaveRoot = Path.Combine(pluginDirectory, "world-module-probe-isolated-saves");
        try
        {
            Directory.CreateDirectory(SaveRoot);
            new Harmony("community.tinyrogues.world-module-probe.save-isolation")
                .PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("Save_System.SaveManager.SaveFilePath redirected to isolated directory: " + SaveRoot);
        }
        catch (Exception error)
        {
            Log.LogError("Save path isolation could not be installed; aborting the probe: " + error);
            Application.Quit(1);
            return;
        }
        AddComponent<WorldModuleProbe>();
        Log.LogInfo("World module probe armed; automated title-to-run flow enabled.");
    }
}

[HarmonyPatch(typeof(Save_System.SaveManager), nameof(Save_System.SaveManager.SaveFilePath))]
internal static class SavePathIsolationPatch
{
    private static bool _logged;

    private static void Postfix(ref string __result)
    {
        if (string.IsNullOrWhiteSpace(WorldModuleProbePlugin.SaveRoot))
            throw new InvalidOperationException("Save path isolation root is not configured.");

        var original = __result ?? "";
        var fileName = Path.GetFileName(original);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("SaveManager returned an invalid save file path.");

        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(original))))
            .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        var safeName = Path.GetFileNameWithoutExtension(fileName) + "_" + digest + Path.GetExtension(fileName);
        __result = Path.GetFullPath(Path.Combine(WorldModuleProbePlugin.SaveRoot, safeName));

        var rootPrefix = Path.GetFullPath(WorldModuleProbePlugin.SaveRoot) + Path.DirectorySeparatorChar;
        if (!__result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Redirected save path escaped the isolated directory.");

        if (!_logged)
        {
            _logged = true;
            BepInEx.Logging.Logger.CreateLogSource("WorldModuleProbe.SaveIsolation")
                .LogInfo("Save path redirected; original path is not used for IO.");
        }
    }
}

[HarmonyPatch]
internal static class BlockDiagnosticSaveWrites
{
    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods() =>
        typeof(Save_System.SaveManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "Save");
    private static bool Prefix() => false;
}

public sealed class WorldModuleProbe : MonoBehaviour
{
    private string CaptureStage => "stage-" + _stage;
    private string _roomFixtureName = "initial";
    private Dungeon_Generation.RoomConfig[] _roomFixtures = Array.Empty<Dungeon_Generation.RoomConfig>();
    private int _roomFixtureIndex;
    private bool _afflictionsAttempted;
    private float _nextBossAttempt;
    private TMPro.TextMeshProUGUI? _diagnosticText;
    private TMPro.TextMeshProUGUI? _enemyNameText;
    private TMPro.TextMeshProUGUI? _floorNameText;
    private string Render(string source)
    {
        if (_diagnosticText == null) _diagnosticText = new GameObject("World module diagnostic label").AddComponent<TMPro.TextMeshProUGUI>();
        _diagnosticText.text = UI.Text.TextMeshProxy.ApplyColorToText(source ?? "", true, _diagnosticText);
        return _diagnosticText.text ?? "";
    }
    private string RenderResourceName(string source, bool enemyName)
    {
        var text = enemyName ? _enemyNameText : _floorNameText;
        if (text == null)
        {
            // Mirror the confirmed display hierarchy so this exercises the same
            // TMP context as the actual HUD, without spawning an actor or invoking
            // BossHealthBar.OnSpawnedBoss and its unrelated UI/actor lifecycle.
            var names = enemyName
                ? new[] { "Diagnostic Root", "Boss HUD", "Boss Name Parent", "Boss Name Text" }
                : new[] { "Diagnostic Root", "Stage Title", "Stage Title (TMP)" };
            Transform? parent = null;
            GameObject? leaf = null;
            foreach (var name in names)
            {
                leaf = new GameObject(name);
                if (parent != null) leaf.transform.SetParent(parent, false);
                parent = leaf.transform;
            }
            text = leaf!.AddComponent<TMPro.TextMeshProUGUI>();
            if (enemyName) _enemyNameText = text; else _floorNameText = text;
        }
        text.text = UI.Text.TextMeshProxy.ApplyColorToText(source ?? "", true, text);
        return text.text ?? "";
    }
    private static T WithoutCatalog<T>(Func<T> getter)
    {
        var pluginType = Type.GetType("TinyRogues.Chinese.Plugin, TinyRogues.Chinese");
        if (pluginType == null) return getter();
        var instance = pluginType.GetField("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
        var field = pluginType.GetField("Catalog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var catalog = field.GetValue(instance);
        try { field.SetValue(instance, null); return getter(); }
        finally { field.SetValue(instance, catalog); }
    }
    private const float TimeoutSeconds = 180f;
    private const float StatusLogInterval = 5f;
    private float _nextStatusLog;
    private float _deadline;
    private int _stage;
    private bool _slotSelected;
    private bool _worldFixturesCaptured;
    private bool _callbackFallbackUsed;
    private Player.PlayerClass? _chosenClass;
    private bool _finished;

    public WorldModuleProbe(IntPtr pointer) : base(pointer) { }

    private void Start()
    {
        _deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        _nextStatusLog = Time.realtimeSinceStartup;
        try { File.WriteAllText(WorldModuleProbePlugin.OutputPath, ""); }
        catch (Exception error) { Fail("Unable to initialize capture file: " + error); }
    }

    private void Update()
    {
        if (_finished) return;
        if (Time.realtimeSinceStartup > _deadline) { Fail("Overall isolated probe timeout at stage " + _stage + "; fixture=" + _roomFixtureName); return; }
        if (Time.realtimeSinceStartup >= _nextStatusLog)
        {
            LogProgress();
            _nextStatusLog = Time.realtimeSinceStartup + StatusLogInterval;
        }

        try
        {
            if (_stage == 0)
            {
                var title = UI.Title_Screen.TitleMenuController.Instance;
                if (title == null) return;
                title.PressPlay();
                _stage = 1;
                _stageTime = Time.realtimeSinceStartup;
                Log("Called TitleMenuController.PressPlay().");
                return;
            }

            if (_stage == 1)
            {
                var slotMenu = UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.Instance;
                if (!_slotSelected && slotMenu != null && UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.IsOpen)
                {
                    var slots = Resources.FindObjectsOfTypeAll<UI.Title_Screen.Save_Slot_Menu.SaveSlotPanel>()
                        .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid())
                        .OrderBy(candidate => candidate.index)
                        .ToArray();
                    if (slots.Length == 0 || slots[0].slotButton == null)
                    {
                        if (Time.realtimeSinceStartup - _stageTime > 15f) Fail("Save slot menu opened but no usable slot button appeared.");
                        return;
                    }
                    Log("Selecting first slot in the isolated, initially empty save directory.");
                    slots[0].slotButton.onClickButton.Invoke();
                    _slotSelected = true;
                    _stage = 2;
                    _stageTime = Time.realtimeSinceStartup;
                    return;
                }

                var classSelection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (classSelection != null && classSelection.IsActive)
                {
                    _stage = 3;
                    _stageTime = Time.realtimeSinceStartup;
                    Log("Class selection is active after PressPlay.");
                    return;
                }

                if (Time.realtimeSinceStartup - _stageTime > 2f)
                {
                    var title = UI.Title_Screen.TitleMenuController.Instance;
                    if (title != null)
                    {
                        title.GoToClassSelection();
                        _stage = 2;
                        _stageTime = Time.realtimeSinceStartup;
                        Log("Called TitleMenuController.GoToClassSelection().");
                    }
                    else if (Time.realtimeSinceStartup - _stageTime > 15f)
                        Fail("Title menu disappeared without opening save slots or class selection.");
                }
                return;
            }

            if (_stage == 2)
            {
                var slotMenu = UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.Instance;
                if (!_slotSelected && slotMenu != null && UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.IsOpen)
                {
                    var slots = Resources.FindObjectsOfTypeAll<UI.Title_Screen.Save_Slot_Menu.SaveSlotPanel>()
                        .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid())
                        .OrderBy(candidate => candidate.index)
                        .ToArray();
                    if (slots.Length > 0 && slots[0].slotButton != null)
                    {
                        Log("Selecting first slot in the isolated, initially empty save directory.");
                        slots[0].slotButton.onClickButton.Invoke();
                        _slotSelected = true;
                        _stage = 3;
                        _stageTime = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - _stageTime > 15f)
                        Fail("Save slot menu opened but no usable slot button appeared.");
                    return;
                }

                var selection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (selection != null && selection.IsActive)
                {
                    _stage = 3;
                    _stageTime = Time.realtimeSinceStartup;
                    Log("Class selection is active.");
                    return;
                }
                if (Time.realtimeSinceStartup - _stageTime > 15f)
                    Fail("Class selection did not become active after requesting the title transition.");
                return;
            }

            if (_stage == 3)
            {
                var classSelection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (classSelection == null || !classSelection.IsActive)
                {
                    if (Time.realtimeSinceStartup - _stageTime > 30f) Fail("Class selection became unavailable before choosing a class.");
                    return;
                }

                var classButtons = Resources.FindObjectsOfTypeAll<UI.Title_Screen.PlayerClassButton>()
                    .Where(button => button != null && button.gameObject.scene.IsValid() && button.assignedClass != null &&
                                     !button.disabled && button.assignedClass.IsUnlocked)
                    .OrderBy(button => button.buttonIndex)
                    .ToArray();
                if (classButtons.Length == 0)
                {
                    if (Time.realtimeSinceStartup - _stageTime > 15f) Fail("No unlocked player class button appeared.");
                    return;
                }

                var chosen = classButtons[0];
                _chosenClass = chosen.assignedClass;
                Log("Selecting unlocked class " + chosen.assignedClass.name + " through PlayerClassButton.Select().");
                chosen.Select();
                _stage = 4;
                _stageTime = Time.realtimeSinceStartup;
                return;
            }

            if (_stage == 4)
            {
                var playButton = UI.Title_Screen.PlayButton.Instance;
                var player = Player.Player.Instance;
                if (player != null && player.PlayerActor != null)
                {
                    Log("PlayerActor appeared while the class selection callback transitioned scenes.");
                    _stage = 5;
                    _stageTime = Time.realtimeSinceStartup;
                    return;
                }
                if (playButton != null && playButton.classWasSelected)
                {
                    Log("Class selected; invoking the play button's native OnClick entry.");
                    playButton.OnClick();
                    _stage = 5;
                    _stageTime = Time.realtimeSinceStartup;
                }
                else if (playButton != null && !_callbackFallbackUsed && Time.realtimeSinceStartup - _stageTime > 2f && _chosenClass != null)
                {
                    Log("Calling confirmed PlayButton.OnPlayerSelectsClass(PlayerClass) callback directly as a fallback.");
                    playButton.OnPlayerSelectsClass(_chosenClass);
                    _callbackFallbackUsed = true;
                }
                else if (Time.realtimeSinceStartup - _stageTime > 15f)
                    Fail("Selecting a class did not reach PlayButton.OnPlayerSelectsClass.");
                return;
            }

            if (_stage == 5)
            {
                var player = Player.Player.Instance;
                if (player == null || player.PlayerActor == null) return;
                if (_worldFixturesCaptured) return;
                _worldFixturesCaptured = true;
                Log("PlayerActor is live; sampling only scene Door and ShopOffer objects.");
                Resources.LoadAll<GameObject>("");
                Resources.LoadAll<Dungeon_Generation.RoomConfig>("");
                Resources.LoadAll<Dungeon_Generation.FloorConfig>("");
                CaptureNameResources();
                CaptureSpecificRoomDoorResources();
                CaptureWorldFixtures();
                CaptureNativeDoorBranches();
                _roomFixtures = Resources.FindObjectsOfTypeAll<Dungeon_Generation.RoomConfig>()
                    .Where(room => room != null && Regex.IsMatch(room.name ?? "", "Shop|Blacksmith|Tavern|Alchemy Lab|Arcade|Soul_Forge", RegexOptions.IgnoreCase))
                    .OrderBy(room => room.name, StringComparer.Ordinal).Take(30).ToArray();
                _roomFixtureIndex = 0;
                Log("Collected " + _roomFixtures.Length + " sorted room fixtures.");
                _stage = 6;
                EnterNextRoomFixture();
                return;
            }
            if (_stage == 6)
            {
                if (Time.realtimeSinceStartup - _stageTime < 3f) return;
                CaptureWorldFixtures();
                if (!_afflictionsAttempted && Resources.FindObjectsOfTypeAll<Dungeon_Generation.Objects.Door>()
                    .Any(door => door != null && door.gameObject.scene.IsValid() && door.leadsToReward != null))
                    CaptureNativeDoorBranches(true);
                EnterNextRoomFixture();
                return;
            }
            if (_stage == 7)
            {
                var elapsed = Time.realtimeSinceStartup - _stageTime;
                if (elapsed > 20f)
                {
                    WriteProbeSkip("live-boss-name", "No initialized scene boss and populated BossHealthBar appeared within 20 seconds after JumpToBoss(null, 0).");
                    Fail("Live boss HUD validation timed out; no synthetic boss substituted.");
                    return;
                }
                if (elapsed < 6f || Time.realtimeSinceStartup < _nextBossAttempt) return;
                _nextBossAttempt = Time.realtimeSinceStartup + 1f;
                if (!TryCaptureLiveBossName()) return;
                _finished = true;
                Log("World module and live boss HUD capture finished; exiting.");
                Application.Quit();
                return;
            }
        }
        catch (Exception error) { Fail("Probe failed: " + error); }
    }

    private float _stageTime;
    private void EnterNextRoomFixture()
    {
        if (_roomFixtureIndex >= _roomFixtures.Length)
        {
            var bossGenerator = Dungeon_Generation.DungeonGenerator.Instance;
            if (bossGenerator == null) throw new InvalidOperationException("DungeonGenerator unavailable for real boss transition.");
            _stage = 7;
            _roomFixtureName = "live-boss-floor-0";
            Log("Room fixture loop completed; entering actual boss room via JumpToBoss(null, 0).");
            bossGenerator.JumpToBoss(null, 0);
            _stageTime = Time.realtimeSinceStartup;
            _nextBossAttempt = _stageTime + 6f;
            return;
        }
        var generator = Dungeon_Generation.DungeonGenerator.Instance;
        if (generator == null) throw new InvalidOperationException("DungeonGenerator unavailable for room fixture transition.");
        var room = _roomFixtures[_roomFixtureIndex++];
        _roomFixtureName = room.name ?? "";
        generator.currentFloorIdx = 0;
        generator.currentRoomIdx = 0;
        Log("Entering room fixture " + _roomFixtureIndex + "/" + _roomFixtures.Length + ": " + _roomFixtureName);
        generator.DebugGoToNextRoom(room, generator.CurrentFloor);
        _stageTime = Time.realtimeSinceStartup;
    }

    private bool TryCaptureLiveBossName()
    {
        var bosses = Resources.FindObjectsOfTypeAll<Enemies.Enemy>()
            .Where(enemy => enemy != null && enemy.gameObject.scene.IsValid() && enemy.gameObject.activeInHierarchy && enemy.IsBoss).ToArray();
        var bars = Resources.FindObjectsOfTypeAll<UI.HUD.BossHealthBar>()
            .Where(bar => bar != null && bar.gameObject.scene.IsValid() && bar.bossNameText != null
                && bar.registeredEnemies != null && bar.registeredEnemies.Count > 0 && !string.IsNullOrEmpty(bar.bossNameText.text)).ToArray();
        if (bosses.Length == 0 || bars.Length == 0) return false;
        var generator = Dungeon_Generation.DungeonGenerator.Instance;
        foreach (var boss in bosses)
        foreach (var bar in bars)
        {
            var source = WithoutCatalog(() => boss.EnemyName);
            var hierarchy = new List<string>();
            for (var node = bar.bossNameText.transform; node != null; node = node.parent) hierarchy.Add(node.name);
            hierarchy.Reverse();
            using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
            writer.Write("{"); WriteJsonString(writer, "kind", "live-boss-name", true); WriteJsonString(writer, "captureStage", CaptureStage);
            WriteJsonString(writer, "identity", boss.name); WriteJsonString(writer, "sourceText", source ?? ""); WriteJsonString(writer, "enemyName", boss.EnemyName ?? "");
            writer.Write(",\"isBoss\":"); writer.Write(boss.IsBoss ? "true" : "false");
            WriteJsonString(writer, "bossName", UI.HUD.BossHealthBar.BossName ?? ""); WriteJsonString(writer, "text", bar.bossNameText.text ?? "");
            WriteJsonString(writer, "hierarchy", string.Join("/", hierarchy));
            writer.Write(",\"registeredEnemiesCount\":"); writer.Write(bar.registeredEnemies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteJsonString(writer, "currentFloor", generator?.CurrentFloor?.name ?? "");
            WriteJsonString(writer, "currentRoom", generator?.CurrentRoom?.RoomConfig?.name ?? "");
            WriteJsonString(writer, "scene", boss.gameObject.scene.name);
            WriteJsonString(writer, "validationScope", "actual initialized scene boss and populated BossHealthBar after native JumpToBoss; no manual boss instantiation");
            writer.WriteLine("}");
        }
        return true;
    }

    private void CaptureSpecificRoomDoorResources()
    {
        var doors = Resources.FindObjectsOfTypeAll<Dungeon_Generation.Objects.SpecificRoomDoor>()
            .Where(door => door != null && !door.gameObject.scene.IsValid()).ToArray();
        if (doors.Length == 0) WriteProbeSkip("specific-room-door-resources", "No resource-only SpecificRoomDoor loaded.");
        foreach (var door in doors)
        {
            string Read(string field, Func<string> getter)
            {
                try { return getter() ?? ""; }
                catch (Exception error) { WriteError("specific-room-door-resource:" + field, door.name, error); return ""; }
            }
            var source = Read("source-interaction", () => WithoutCatalog(() => door.InteractionText.Item1));
            var text = Read("interaction", () => door.InteractionText.Item1);
            var title = Read("room-title", () => WithoutCatalog(() => door.room == null ? "" : door.room.NoRewardTitle));
            var description = Read("room-description", () => WithoutCatalog(() => door.room == null ? "" : door.room.NoRewardDescription));
            var currentTitle = Read("normal-room-title", () => door.room == null ? "" : door.room.NoRewardTitle);
            var currentDescription = Read("normal-room-description", () => door.room == null ? "" : door.room.NoRewardDescription);
            using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
            writer.Write("{"); WriteJsonString(writer, "kind", "specific-room-door-resource", true); WriteJsonString(writer, "captureStage", CaptureStage);
            WriteJsonString(writer, "identity", door.name); WriteJsonString(writer, "roomResource", Read("room-resource", () => door.room?.name ?? ""));
            WriteJsonString(writer, "sourceText", source); WriteJsonString(writer, "text", text);
            WriteJsonString(writer, "sourceRoomTitle", title); WriteJsonString(writer, "sourceRoomDescription", description);
            WriteJsonString(writer, "roomTitle", currentTitle); WriteJsonString(writer, "roomDescription", currentDescription);
            WriteJsonString(writer, "nativeReference", Read("native-render", () => WithoutCatalog(() => Render(source))));
            WriteJsonString(writer, "renderedText", Read("render", () => Render(text)));
            WriteJsonString(writer, "validationScope", "resource-only SpecificRoomDoor prefab getters; no scene instance, initialization, interaction or visible panel demonstrated");
            writer.WriteLine("}");
        }
    }

    private void CaptureNameResources()
    {
        foreach (var enemy in Resources.FindObjectsOfTypeAll<Enemies.Enemy>().Where(item => item != null).OrderBy(item => item.name, StringComparer.Ordinal))
            try { WriteNameRecord("enemy-name", enemy.name, WithoutCatalog(() => enemy.EnemyName), enemy.EnemyName, enemy.IsBoss); }
            catch (Exception error) { WriteError("enemy-name", enemy.name, error); }
        foreach (var floor in Resources.FindObjectsOfTypeAll<Dungeon_Generation.FloorConfig>().Where(item => item != null).OrderBy(item => item.name, StringComparer.Ordinal))
            try { WriteNameRecord("floor-title", floor.name, WithoutCatalog(() => floor.FloorTitle), floor.FloorTitle); }
            catch (Exception error) { WriteError("floor-title", floor.name, error); }
        foreach (var room in Resources.FindObjectsOfTypeAll<Dungeon_Generation.RoomConfig>().Where(item => item != null).OrderBy(item => item.name, StringComparer.Ordinal))
            try
            {
                var sourceTitle = WithoutCatalog(() => room.NoRewardTitle);
                var sourceDescription = WithoutCatalog(() => room.NoRewardDescription);
                var title = room.NoRewardTitle;
                var description = room.NoRewardDescription;
                using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
                writer.Write("{"); WriteJsonString(writer, "kind", "room-config", true);
                WriteJsonString(writer, "captureStage", CaptureStage); WriteJsonString(writer, "identity", room.name);
                WriteJsonString(writer, "sourceTitle", sourceTitle ?? ""); WriteJsonString(writer, "title", title ?? "");
                WriteJsonString(writer, "nativeTitleReference", WithoutCatalog(() => Render(sourceTitle ?? ""))); WriteJsonString(writer, "renderedTitle", Render(title ?? ""));
                WriteJsonString(writer, "sourceText", sourceDescription ?? ""); WriteJsonString(writer, "text", description ?? "");
                WriteJsonString(writer, "nativeReference", WithoutCatalog(() => Render(sourceDescription ?? ""))); WriteJsonString(writer, "renderedText", Render(description ?? ""));
                writer.WriteLine("}");
            }
            catch (Exception error) { WriteError("room-config", room.name, error); }
    }

    private void WriteNameRecord(string kind, string identity, string source, string text, bool? isBoss = null)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        writer.Write("{"); WriteJsonString(writer, "kind", kind, true); WriteJsonString(writer, "captureStage", CaptureStage);
        WriteJsonString(writer, "identity", identity ?? ""); WriteJsonString(writer, "sourceText", source ?? ""); WriteJsonString(writer, "text", text ?? "");
        var enemyName = kind == "enemy-name";
        WriteJsonString(writer, "nativeReference", WithoutCatalog(() => RenderResourceName(source ?? "", enemyName)));
        WriteJsonString(writer, "renderedText", RenderResourceName(text ?? "", enemyName));
        WriteJsonString(writer, "validationScope", "resource name via HUD-equivalent TMP hierarchy; no live encounter or BossHealthBar lifecycle invoked");
        if (isBoss.HasValue) { writer.Write(",\"isBoss\":"); writer.Write(isBoss.Value ? "true" : "false"); }
        writer.WriteLine("}");
    }
    private static readonly Dungeon_Generation.Objects.Door.DoorType[] DoorTypes =
        (Dungeon_Generation.Objects.Door.DoorType[])Enum.GetValues(typeof(Dungeon_Generation.Objects.Door.DoorType));
    private static readonly Dungeon_Generation.Objects.Door.OpenState[] DoorOpenStates =
        (Dungeon_Generation.Objects.Door.OpenState[])Enum.GetValues(typeof(Dungeon_Generation.Objects.Door.OpenState));
    private static readonly Dungeon_Generation.Objects.DoorAffliction[] DoorAfflictions =
        (Dungeon_Generation.Objects.DoorAffliction[])Enum.GetValues(typeof(Dungeon_Generation.Objects.DoorAffliction));

    private static Il2CppSystem.Collections.Generic.List<Text> CopyTexts(
        Il2CppSystem.Collections.Generic.IEnumerable<Text> source) =>
        source == null ? new Il2CppSystem.Collections.Generic.List<Text>() :
            new Il2CppSystem.Collections.Generic.List<Text>(source);

    private static Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip> CopyTooltips(
        Il2CppSystem.Collections.Generic.IEnumerable<UI.Text.Tag_Boxes.ITooltip> source) =>
        source == null ? new Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip>() :
            new Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip>(source);

    private void CaptureWorldFixtures()
    {
        var doors = Resources.FindObjectsOfTypeAll<Dungeon_Generation.Objects.Door>()
            .Where(door => door != null && door.gameObject != null && door.gameObject.scene.IsValid())
            .OrderBy(door => door.name, StringComparer.Ordinal).ToArray();
        var offers = Resources.FindObjectsOfTypeAll<Shops.ShopOffer>()
            .Where(offer => offer != null && offer.gameObject != null && offer.gameObject.scene.IsValid())
            .OrderBy(offer => offer.name, StringComparer.Ordinal).ToArray();

        foreach (var door in doors) CaptureDoor(door);
        CaptureSpecificRoomDoors();
        foreach (var offer in offers) CaptureOffer(offer);
        foreach (var tmp in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>().Where(item => item.gameObject.scene.IsValid() && item.gameObject.activeInHierarchy && item.enabled))
        {
            using var row = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
            row.Write("{"); WriteJsonString(row, "kind", "scene-tmp", true); WriteJsonString(row, "captureStage", CaptureStage);
            WriteJsonString(row, "identity", tmp.gameObject.name); WriteJsonString(row, "text", tmp.text ?? ""); row.WriteLine("}");
        }
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        writer.Write("{\"kind\":\"summary\",\"captureStage\":\"" + Escape(CaptureStage) + "\",\"scope\":\"scene-only\",\"doorCount\":");
        writer.Write(doors.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.Write(",\"shopOfferCount\":");
        writer.Write(offers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteJsonString(writer, "roomFixture", _roomFixtureName);
        writer.WriteLine(",\"spikeState\":\"no per-instance spike member in confirmed metadata\"}");
    }

    private sealed class DoorDisplaySnapshot
    {
        public string Title = "", Description = "", Interaction = "", ProviderTitle = "", Sprite = "", Children = "", Type = "", Affliction = "";
    }
    private static DoorDisplaySnapshot ReadDoorDisplay(Dungeon_Generation.Objects.Door door) => new()
    {
        Title = door.rewardTitle?.text ?? "",
        Description = door.rewardDesc?.text ?? "",
        Interaction = door.InteractionText.Item1 ?? "",
        ProviderTitle = door.GetTitle(true) ?? "",
        Sprite = door.spriteRenderer?.sprite?.name ?? "",
        Children = string.Join("|", door.GetComponentsInChildren<SpriteRenderer>(true).Select(renderer =>
            renderer.gameObject.name + ":" + (renderer.sprite?.name ?? "") + ":active=" + renderer.gameObject.activeInHierarchy)),
        Type = door.doorType.ToString(), Affliction = door.doorAffliction.ToString()
    };
    private static void InvokeDoorInitializer(Dungeon_Generation.Objects.Door door, string name)
    {
        var method = typeof(Dungeon_Generation.Objects.Door).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null) ?? throw new MissingMethodException(typeof(Dungeon_Generation.Objects.Door).FullName, name);
        method.Invoke(door, null);
    }
    private void CaptureNativeDoorBranches(bool afflictionsOnly = false)
    {
        var doors = Resources.FindObjectsOfTypeAll<Dungeon_Generation.Objects.Door>()
            .Where(item => item != null && item.gameObject.scene.IsValid()).OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        var door = doors.FirstOrDefault(item => item.leadsToReward != null) ?? doors.FirstOrDefault();
        if (door == null) { WriteProbeSkip("native-door-branches", "No live scene Door."); return; }
        var reward = door.leadsToReward ?? door.LeadsToReward;
        if (afflictionsOnly && reward == null) return;
        var originalType = door.doorType; var originalOpen = door.openState; var originalAffliction = door.doorAffliction;
        var originalLock = door.lockedOrBlocked; var originalAttempt = door.hasBeenAttemptedToLockPick;
        var originalRevealed = door.isRevealed; var originalTransforming = door.isTransforming;
        var originalDestination = door.leadsTo; var originalReward = door.leadsToReward;
        var originalAccursedRoom = originalDestination != null && originalDestination.IsAccursedRoom;
        var originalClosedSprite = door.closedSprite; var originalLockedSprite = door.lockedSprite;
        var originalBlockedSprite = door.blockedSprite; var originalOpenedSprite = door.openedSprite;
        var originalMuncherClosedSprite = door.muncherClosedSprite;
        var originalTitle = door.rewardTitle?.text ?? ""; var originalDesc = door.rewardDesc?.text ?? "";
        var originalTitleColor = door.rewardTitle != null ? door.rewardTitle.color : Color.white;
        var originalDescColor = door.rewardDesc != null ? door.rewardDesc.color : Color.white;
        var originalAlpha = door.myTextBox != null ? door.myTextBox.alpha : 0f;
        var originalRewardPanelActive = door.rewardPanel != null && door.rewardPanel.gameObject.activeSelf;
        var sprites = door.GetComponentsInChildren<SpriteRenderer>(true).ToArray();
        var spriteValues = sprites.Select(item => item.sprite).ToArray(); var spriteColors = sprites.Select(item => item.color).ToArray();
        var spriteActive = sprites.Select(item => item.gameObject.activeSelf).ToArray();
        var accumulator = Dungeon_Generation.Objects.Door.SpikyDoorChanceAccumulator;
        var spikeCounter = Dungeon_Generation.Objects.Door.SpikyDoorCounter;
        var blockAccumulator = Dungeon_Generation.Objects.Door.BlockLockChanceAccumulator;
        var blockCounter = Dungeon_Generation.Objects.Door.BlockLockCounter;
        void Restore()
        {
            door.doorType = originalType; door.openState = originalOpen; door.doorAffliction = originalAffliction;
            door.lockedOrBlocked = originalLock; door.hasBeenAttemptedToLockPick = originalAttempt;
            door.isRevealed = originalRevealed; door.isTransforming = originalTransforming;
            door.leadsTo = originalDestination; door.leadsToReward = originalReward;
            if (originalDestination != null) originalDestination.IsAccursedRoom = originalAccursedRoom;
            door.closedSprite = originalClosedSprite; door.lockedSprite = originalLockedSprite;
            door.blockedSprite = originalBlockedSprite; door.openedSprite = originalOpenedSprite;
            door.muncherClosedSprite = originalMuncherClosedSprite;
            if (door.rewardTitle != null) { door.rewardTitle.text = originalTitle; door.rewardTitle.color = originalTitleColor; }
            if (door.rewardDesc != null) { door.rewardDesc.text = originalDesc; door.rewardDesc.color = originalDescColor; }
            if (door.myTextBox != null) door.myTextBox.alpha = originalAlpha;
            if (door.rewardPanel != null) door.rewardPanel.gameObject.SetActive(originalRewardPanelActive);
            for (var index = 0; index < sprites.Length; index++)
                if (sprites[index] != null) { sprites[index].sprite = spriteValues[index]; sprites[index].color = spriteColors[index]; sprites[index].gameObject.SetActive(spriteActive[index]); }
            Dungeon_Generation.Objects.Door.SpikyDoorChanceAccumulator = accumulator;
            Dungeon_Generation.Objects.Door.SpikyDoorCounter = spikeCounter;
            Dungeon_Generation.Objects.Door.BlockLockChanceAccumulator = blockAccumulator;
            Dungeon_Generation.Objects.Door.BlockLockCounter = blockCounter;
        }
        var completed = 0;
        void Branch(string name, Action initialize)
        {
            try
            {
                var before = WithoutCatalog(() => { Restore(); initialize(); return ReadDoorDisplay(door); });
                Restore(); initialize(); var after = ReadDoorDisplay(door);
                WriteNativeDoorRecord(door.name, name, before, after);
                completed++;
            }
            catch (Exception error) { WriteError("native-door-branch:" + name, door.name, error); }
            finally { Restore(); }
        }
        try
        {
            if (!afflictionsOnly)
                foreach (var method in new[] { "MakeLockedDoor", "MakeBlockedDoor", "MakeDoorToMuncher" })
                    Branch(method, () => InvokeDoorInitializer(door, method));
            if (reward == null)
                WriteProbeSkip("native-door-afflictions", "Live initial Door has no assigned Reward; no asset reward substituted.");
            else
            {
                _afflictionsAttempted = true;
                foreach (var affliction in DoorAfflictions)
                    Branch("SetUpAffliction+SetUpLeadsToReward:" + affliction, () =>
                    {
                        door.doorType = Dungeon_Generation.Objects.Door.DoorType.Normal;
                        door.doorAffliction = affliction;
                        InvokeDoorInitializer(door, "SetUpAffliction");
                        door.SetUpLeadsToReward(reward);
                    });
            }
            Log("Native door initialization pairs captured: " + completed + "; this is not the field-enumeration count.");
        }
        finally { Restore(); }
    }
    private void WriteNativeDoorRecord(string identity, string branch, DoorDisplaySnapshot source, DoorDisplaySnapshot current)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        writer.Write("{"); WriteJsonString(writer, "kind", "native-door-branch", true); WriteJsonString(writer, "captureStage", CaptureStage);
        WriteJsonString(writer, "identity", identity); WriteJsonString(writer, "branch", branch);
        WriteJsonString(writer, "sourceTitle", source.Title); WriteJsonString(writer, "title", current.Title);
        WriteJsonString(writer, "sourceDescription", source.Description); WriteJsonString(writer, "description", current.Description);
        WriteJsonString(writer, "sourceInteraction", source.Interaction); WriteJsonString(writer, "interaction", current.Interaction);
        WriteJsonString(writer, "nativeReference", WithoutCatalog(() => Render(source.Interaction))); WriteJsonString(writer, "renderedInteraction", Render(current.Interaction));
        WriteJsonString(writer, "sourceProviderTitle", source.ProviderTitle); WriteJsonString(writer, "providerTitle", current.ProviderTitle);
        WriteJsonString(writer, "sourceDoorType", source.Type); WriteJsonString(writer, "doorType", current.Type);
        WriteJsonString(writer, "sourceAffliction", source.Affliction); WriteJsonString(writer, "affliction", current.Affliction);
        WriteJsonString(writer, "sourceSprite", source.Sprite); WriteJsonString(writer, "sprite", current.Sprite);
        WriteJsonString(writer, "sourceChildSprites", source.Children); WriteJsonString(writer, "childSprites", current.Children);
        WriteJsonString(writer, "validationScope", "native initializers and actual panel snapshots; no Interact, collision, combat or save writes; does not prove spiky/Cursed equivalence");
        writer.WriteLine("}");
    }
    private void WriteProbeSkip(string phase, string reason)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        writer.Write("{"); WriteJsonString(writer, "kind", "probe-skip", true); WriteJsonString(writer, "captureStage", CaptureStage);
        WriteJsonString(writer, "phase", phase); WriteJsonString(writer, "reason", reason); writer.WriteLine("}");
    }
    private void CaptureSpecificRoomDoors()
    {
        var doors = Resources.FindObjectsOfTypeAll<Dungeon_Generation.Objects.SpecificRoomDoor>().Where(item => item != null && item.gameObject.scene.IsValid()).ToArray();
        if (doors.Length == 0) WriteProbeSkip("specific-room-doors", "No live SpecificRoomDoor in this room fixture; resource-only instances excluded.");
        foreach (var door in doors)
            try
            {
                var source = WithoutCatalog(() => door.InteractionText.Item1) ?? "";
                var text = door.InteractionText.Item1 ?? "";
                var sourceTitle = WithoutCatalog(() => door.room == null ? "" : door.room.NoRewardTitle);
                var sourceDesc = WithoutCatalog(() => door.room == null ? "" : door.room.NoRewardDescription);
                using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
                writer.Write("{"); WriteJsonString(writer, "kind", "specific-room-door", true); WriteJsonString(writer, "captureStage", CaptureStage);
                WriteJsonString(writer, "identity", door.name); WriteJsonString(writer, "sourceText", source); WriteJsonString(writer, "text", text);
                WriteJsonString(writer, "nativeReference", WithoutCatalog(() => Render(source))); WriteJsonString(writer, "renderedText", Render(text));
                WriteJsonString(writer, "sourceRoomTitle", sourceTitle ?? ""); WriteJsonString(writer, "roomTitle", door.room?.NoRewardTitle ?? "");
                WriteJsonString(writer, "sourceRoomDescription", sourceDesc ?? ""); WriteJsonString(writer, "roomDescription", door.room?.NoRewardDescription ?? "");
                WriteJsonString(writer, "doorType", door.doorType.ToString()); WriteJsonString(writer, "openState", door.openState.ToString());
                WriteJsonString(writer, "sprite", door.spriteRenderer?.sprite?.name ?? "");
                WriteJsonString(writer, "panelTexts", door.myTextBox == null ? "" : string.Join("|", door.myTextBox.GetComponentsInChildren<TMPro.TMP_Text>(true).Select(item => item.gameObject.name + ":" + (item.text ?? ""))));
                WriteJsonString(writer, "validationScope", "live SpecificRoomDoor interaction and serialized panel/RoomConfig; no ITextProvider GetTitle/GetTexts exists; panel not activated");
                writer.WriteLine("}");
            }
            catch (Exception error) { WriteError("specific-room-door", door.name, error); }
    }
    private void CaptureDoor(Dungeon_Generation.Objects.Door door)
    {
        var originalType = door.doorType;
        var originalOpen = door.openState;
        var originalLock = door.lockedOrBlocked;
        var originalLockPick = door.hasBeenAttemptedToLockPick;
        var originalAffliction = door.doorAffliction;
        try
        {
            foreach (var doorType in DoorTypes)
            foreach (var openState in DoorOpenStates)
            foreach (var lockedOrBlocked in new[] { false, true })
            foreach (var lockPickAttempted in new[] { false, true })
            foreach (var affliction in DoorAfflictions)
            {
                var state = "DoorType=" + doorType + ";OpenState=" + openState +
                    ";lockedOrBlocked=" + lockedOrBlocked + ";hasBeenAttemptedToLockPick=" + lockPickAttempted +
                    ";DoorAffliction=" + affliction;
                try
                {
                    door.doorType = doorType;
                    door.openState = openState;
                    door.lockedOrBlocked = lockedOrBlocked;
                    door.hasBeenAttemptedToLockPick = lockPickAttempted;
                    door.doorAffliction = affliction;
                    foreach (var details in new[] { false, true })
                    foreach (var baseValues in new[] { false, true })
                    {
                        var sourceTitle = WithoutCatalog(() => door.GetTitle(details));
                        var sourceInteraction = WithoutCatalog(() => door.InteractionText.Item1);
                        var interaction = door.InteractionText;
                        using (var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false)))
                        {
                            writer.Write("{");
                            WriteJsonString(writer, "kind", "door-state", true);
                            WriteJsonString(writer, "captureStage", CaptureStage);
                            WriteJsonString(writer, "identity", door.name + "[" + state + "]");
                            WriteJsonString(writer, "title", door.GetTitle(details) ?? "");
                            WriteJsonString(writer, "sourceTitle", sourceTitle ?? "");
                            WriteJsonString(writer, "text", interaction.Item1 ?? "");
                            WriteJsonString(writer, "sourceText", sourceInteraction ?? "");
                            WriteJsonString(writer, "nativeReference", WithoutCatalog(() => Render(sourceInteraction ?? "")));
                            WriteJsonString(writer, "renderedText", Render(interaction.Item1 ?? ""));
                            WriteJsonString(writer, "rewardTitle", door.rewardTitle == null ? "" : door.rewardTitle.text ?? "");
                            WriteJsonString(writer, "rewardDescription", door.rewardDesc == null ? "" : door.rewardDesc.text ?? "");
                            writer.Write(",\"showDetails\":"); writer.Write(details ? "true" : "false");
                            writer.Write(",\"showBaseValues\":"); writer.Write(baseValues ? "true" : "false");
                            writer.WriteLine("}");
                        }
                        try
                        {
                            var copied = CopyTooltips(door.GetTooltips());
                            WriteDoorTextRecords(door.name + "[" + state + "]", door.GetTitle(details), details, baseValues, copied);
                        }
                        catch (Exception error) { WriteError("door-tooltips", door.name + "[" + state + "]", error); }
                    }
                }
                catch (Exception error) { WriteError("door-state", door.name + "[" + state + "]", error); }
            }
        }
        catch (Exception error) { WriteError("door-enumeration", door.name, error); }
        finally
        {
            try
            {
                door.doorType = originalType;
                door.openState = originalOpen;
                door.lockedOrBlocked = originalLock;
                door.hasBeenAttemptedToLockPick = originalLockPick;
                door.doorAffliction = originalAffliction;
            }
            catch (Exception error) { WriteError("door-restore", door.name, error); }
        }
    }

    private void CaptureOffer(Shops.ShopOffer offer)
    {
        var originalPrice = offer.price;
        try
        {
            foreach (var price in new[] { originalPrice, 0, 1, 99, 999 }.Distinct())
            {
                try
                {
                    offer.price = price;
                    foreach (var details in new[] { false, true })
                    foreach (var baseValues in new[] { false, true })
                    {
                        // ShopOffer.GetTexts returns Il2CppSystem.Collections.Generic.IEnumerable<Text>.
                        var originalTitle = WithoutCatalog(() => offer.GetTitle(details));
                        var originalTexts = WithoutCatalog(() => CopyTexts(offer.GetTexts(details, baseValues)));
                        var title = offer.GetTitle(details);
                        var copied = CopyTexts(offer.GetTexts(details, baseValues));
                        using (var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false)))
                        {
                            writer.Write("{"); WriteJsonString(writer, "kind", "shop-offer-state", true); WriteJsonString(writer, "captureStage", CaptureStage);
                            WriteJsonString(writer, "identity", offer.name); WriteJsonString(writer, "sourceTitle", originalTitle ?? ""); WriteJsonString(writer, "title", title ?? "");
                            WriteJsonString(writer, "nativeTitleReference", WithoutCatalog(() => Render(originalTitle ?? ""))); WriteJsonString(writer, "renderedTitle", Render(title ?? ""));
                            writer.Write(",\"price\":"); writer.Write(price.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.Write(",\"textCount\":"); writer.Write(copied.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            writer.Write(",\"showDetails\":"); writer.Write(details ? "true" : "false"); writer.Write(",\"showBaseValues\":"); writer.Write(baseValues ? "true" : "false");
                            writer.WriteLine("}");
                        }
                        WriteTextRecords("shop-offer-price=" + price, offer.name, title ?? "",
                            details, baseValues, copied, originalTitle ?? "", originalTexts);
                    }
                }
                catch (Exception error) { WriteError("shop-offer-price=" + price, offer.name, error); }
            }
        }
        catch (Exception error) { WriteError("shop-offer", offer.name, error); }
        finally
        {
            try { offer.price = originalPrice; }
            catch (Exception error) { WriteError("shop-offer-restore-price", offer.name, error); }
        }
    }

    private void WriteDoorTextRecords(string identity, string title, bool details, bool baseValues,
        Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip> tooltips)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        for (var i = 0; i < tooltips.Count; ++i)
        {
            var tooltip = tooltips[i];
            if (tooltip == null)
            {
                WriteError("door-tooltip-entry", identity + ";index=" + i,
                    new InvalidOperationException("Tooltip entry is null."));
                continue;
            }
            var tooltipKey = tooltip.TooltipKey ?? "";
            var renderer = tooltip.GetTooltipText;
            if (renderer == null)
            {
                WriteError("door-tooltip-delegate", identity + ";index=" + i, new InvalidOperationException("Tooltip text delegate is null."));
                continue;
            }
            var text = renderer.Invoke(details, baseValues) ?? "";
            writer.Write("{\"kind\":\"door-tooltip\",\"identity\":\""); writer.Write(Escape(identity));
            writer.Write("\",\"title\":\""); writer.Write(Escape(title ?? ""));
            writer.Write("\",\"tooltipKey\":\""); writer.Write(Escape(tooltipKey));
            writer.Write("\",\"showDetails\":"); writer.Write(details ? "true" : "false");
            writer.Write(",\"showBaseValues\":"); writer.Write(baseValues ? "true" : "false");
            writer.Write(",\"textIndex\":"); writer.Write(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(",\"text\":\""); writer.Write(Escape(text)); writer.Write("\"");
            WriteJsonString(writer, "captureStage", CaptureStage); writer.WriteLine("}");
        }
    }

    private void WriteTextRecords(string kind, string identity, string title, bool details, bool baseValues,
        Il2CppSystem.Collections.Generic.List<Text> copied, string originalTitle, Il2CppSystem.Collections.Generic.List<Text> originalTexts)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        for (var i = 0; i < copied.Count; ++i)
        {
            var text = copied[i];
            writer.Write("{");
            WriteJsonString(writer, "kind", kind, first: true);
            WriteJsonString(writer, "identity", identity);
            WriteJsonString(writer, "title", title ?? "");
            WriteJsonString(writer, "captureStage", CaptureStage);
            WriteJsonString(writer, "sourceTitle", originalTitle ?? "");
            WriteJsonString(writer, "renderedTitle", Render(title ?? ""));
            WriteJsonString(writer, "nativeTitleReference", WithoutCatalog(() => Render(originalTitle ?? "")));
            writer.Write(",\"showDetails\":"); writer.Write(details ? "true" : "false");
            writer.Write(",\"showBaseValues\":"); writer.Write(baseValues ? "true" : "false");
            writer.Write(",\"textIndex\":"); writer.Write(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WriteJsonString(writer, "text", text == null ? "" : text.Description ?? "");
            var source = i < originalTexts.Count && originalTexts[i] != null ? originalTexts[i].Description ?? "" : "";
            WriteJsonString(writer, "sourceText", source);
            WriteJsonString(writer, "nativeReference", WithoutCatalog(() => Render(source)));
            WriteJsonString(writer, "renderedText", Render(text == null ? "" : text.Description ?? ""));
            writer.WriteLine("}");
        }
    }

    private void WriteError(string phase, string identity, Exception error)
    {
        using var writer = new StreamWriter(WorldModuleProbePlugin.OutputPath, true, new UTF8Encoding(false));
        writer.Write("{\"kind\":\"error\",\"phase\":\""); writer.Write(Escape(phase));
        writer.Write("\",\"identity\":\""); writer.Write(Escape(identity));
        writer.Write("\",\"error\":\""); writer.Write(Escape(error.ToString())); writer.WriteLine("\"}");
        Log("Captured probe exception " + phase + " for " + identity + ": " + error.Message);
    }

    private void WriteJsonString(StreamWriter writer, string name, string value, bool first = false)
    {
        writer.Write(first ? "\"" : ",\""); writer.Write(Escape(name));
        writer.Write("\":\""); writer.Write(Escape(value)); writer.Write("\"");
        if (name == "captureStage") WriteJsonString(writer, "roomFixture", _roomFixtureName);
    }

    private void LogProgress()
    {
        var stageName = _stage switch
        {
            0 => "waiting for title menu",
            1 => "press-play transition/save slot",
            2 => "waiting for class selection",
            3 => "choosing class",
            4 => "waiting for class callback",
            5 => "waiting for PlayerActor; then capturing scene doors and shop offers",
            6 => "capturing room fixture " + _roomFixtureIndex + "/" + _roomFixtures.Length + ": " + _roomFixtureName,
            7 => "waiting for real scene boss and populated BossHealthBar",
            _ => "unknown stage"
        };
        Log("Progress: " + stageName + "; elapsed=" + (Time.realtimeSinceStartup - (_deadline - TimeoutSeconds)).ToString("0") + "s.");
    }

    private static void Log(string message) =>
        BepInEx.Logging.Logger.CreateLogSource("WorldModuleProbe").LogInfo(message);

    private static string Escape(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (ch < 0x20) result.Append("\\u").Append(((int)ch).ToString("x4"));
                    else result.Append(ch);
                    break;
            }
        }
        return result.ToString();
    }

    private void Fail(string message)
    {
        if (_finished) return;
        _finished = true;
        try { WriteError("probe-fatal", "", new InvalidOperationException(message)); }
        catch { }
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(WorldModuleProbePlugin.OutputPath)!, "world-module-capture.failure.txt"), message); }
        catch { }
        BepInEx.Logging.Logger.CreateLogSource("WorldModuleProbe").LogError(message);
        Application.Quit(1);
    }
}
