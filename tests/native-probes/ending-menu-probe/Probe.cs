using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UI.Menus.Pause;
using UI.Game_Over;

[BepInPlugin("community.tinyrogues.ending-menu-probe", "Tiny Rogues Ending Menu Probe", "0.1.0")]
public sealed class EndingMenuProbePlugin : BasePlugin
{
    private const string Switch = "-tinyrogues-ending-menu-probe";
    internal static string OutputPath = "";
    internal static string SaveRoot = "";

    public override void Load()
    {
        if (!Environment.GetCommandLineArgs().Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            Log.LogInfo("Ending menu probe idle; dedicated switch not present.");
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        OutputPath = Path.Combine(pluginDirectory, "ending-menu-capture.jsonl");
        SaveRoot = Path.Combine(pluginDirectory, "ending-menu-probe-isolated-saves");
        try
        {
            Directory.CreateDirectory(SaveRoot);
            new Harmony("community.tinyrogues.ending-menu-probe.save-isolation")
                .PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("Save_System.SaveManager.SaveFilePath redirected to isolated directory: " + SaveRoot);
        }
        catch (Exception error)
        {
            Log.LogError("Save path isolation could not be installed; aborting the probe: " + error);
            Application.Quit(1);
            return;
        }
        AddComponent<EndingMenuProbe>();
        Log.LogInfo("Ending menu probe armed; automated title-to-run flow enabled.");
    }
}

[HarmonyPatch(typeof(Save_System.SaveManager), nameof(Save_System.SaveManager.SaveFilePath))]
internal static class SavePathIsolationPatch
{
    private static bool _logged;

    private static void Postfix(ref string __result)
    {
        if (string.IsNullOrWhiteSpace(EndingMenuProbePlugin.SaveRoot))
            throw new InvalidOperationException("Save path isolation root is not configured.");

        var original = __result ?? "";
        var fileName = Path.GetFileName(original);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("SaveManager returned an invalid save file path.");

        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(original))))
            .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        var safeName = Path.GetFileNameWithoutExtension(fileName) + "_" + digest + Path.GetExtension(fileName);
        __result = Path.GetFullPath(Path.Combine(EndingMenuProbePlugin.SaveRoot, safeName));

        var rootPrefix = Path.GetFullPath(EndingMenuProbePlugin.SaveRoot) + Path.DirectorySeparatorChar;
        if (!__result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Redirected save path escaped the isolated directory.");

        if (!_logged)
        {
            _logged = true;
            BepInEx.Logging.Logger.CreateLogSource("EndingMenuProbe.SaveIsolation")
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

public sealed class EndingMenuProbe : MonoBehaviour
{
    private const float TimeoutSeconds = 90f;
    private const float StatusLogInterval = 5f;
    private float _nextStatusLog;
    private float _stageTime;

    private static void HideGiveUpPrompt(UI.Game_Over.GiveUpPanel panel)
    {
        // The interop symbol is marked Private at the native-method level, but the generated CLR wrapper exposes this method publicly.
        panel.Hide();
    }

    private float _deadline;
    private int _stage;
    private bool _slotSelected;
    private bool _callbackFallbackUsed;
    private Player.PlayerClass? _chosenClass;
    private UI.Menus.Pause.PauseMenu? _pauseMenu;
    private UI.Game_Over.GiveUpPanel? _giveUpPanel;
    private Managers.GameManager? _gameManager;
    private float _nextDeathCaptureTime;
    private float _deathCaptureDeadline;
    private bool _finished;

    public EndingMenuProbe(IntPtr pointer) : base(pointer) { }

    private void Start()
    {
        _deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        _nextStatusLog = Time.realtimeSinceStartup;
        try { File.WriteAllText(EndingMenuProbePlugin.OutputPath, ""); }
        catch (Exception error) { Fail("Unable to initialize capture file: " + error); }
    }

    private void Update()
    {
        if (_finished) return;
        if (Time.realtimeSinceStartup > _deadline) { Fail("Timed out waiting for all four pages."); return; }
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
                _gameManager = Managers.GameManager.Instance;
                _pauseMenu = Resources.FindObjectsOfTypeAll<UI.Menus.Pause.PauseMenu>()
                    .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid());
                _giveUpPanel = Resources.FindObjectsOfTypeAll<UI.Game_Over.GiveUpPanel>()
                    .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid());
                if (_gameManager == null || _pauseMenu == null || _giveUpPanel == null)
                {
                    Fail("Valid PlayerActor found, but GameManager, scene PauseMenu, or scene GiveUpPanel is unavailable.");
                    return;
                }

                Log("Valid PlayerActor and game-over menu controls found; pausing isolated run.");
                _gameManager.PauseGame(true);
                if (!_pauseMenu.IsOpen) _pauseMenu.Show();
                _stage = 6;
                _stageTime = Time.realtimeSinceStartup;
                return;
            }

            if (_stage == 6)
            {
                if (Time.realtimeSinceStartup - _stageTime < 1f) return;
                CaptureScene("pause");
                Log("Captured pause UI; opening GiveUpPanel without activating either confirmation button.");
                _giveUpPanel!.Show();
                _stage = 7;
                _stageTime = Time.realtimeSinceStartup;
                return;
            }

            if (_stage == 7)
            {
                if (Time.realtimeSinceStartup - _stageTime < 1f) return;
                CaptureScene("give-up-confirmation");
                Log("Captured GiveUpPanel; hiding prompt and unpausing without confirmation.");
                HideGiveUpPrompt(_giveUpPanel!);
                _gameManager!.UnpauseGame();
                if (_pauseMenu!.IsOpen) _pauseMenu.Hide();
                _stage = 8;
                _stageTime = Time.realtimeSinceStartup;
                _nextDeathCaptureTime = _stageTime;
                _deathCaptureDeadline = _stageTime + 12f;
                Log("Starting GiveUpPanel.KillPlayer() on the isolated temporary run.");
                var killRoutine = _giveUpPanel!.KillPlayer();
                if (killRoutine == null) throw new InvalidOperationException("GiveUpPanel.KillPlayer() returned no coroutine.");
                StartCoroutine(killRoutine);
                return;
            }

            if (_stage == 8)
            {
                var now = Time.realtimeSinceStartup;
                if (now >= _nextDeathCaptureTime)
                {
                    var elapsed = now - _stageTime;
                    CaptureScene("death+" + elapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
                    _nextDeathCaptureTime = now + 0.5f;
                }
                if (now >= _deathCaptureDeadline)
                {
                    _finished = true;
                    Log("Completed 12 seconds of 0.5-second interval death/game-over UI captures.");
                    Application.Quit();
                }
                return;
            }
        }
        catch (Exception error) { Fail("Probe failed: " + error); }
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
            5 => "waiting for gameplay PlayerActor and scene ending-menu controls",
            6 => "paused; waiting to capture pause UI",
            7 => "GiveUpPanel open; waiting to capture confirmation UI",
            8 => "GiveUpPanel.KillPlayer coroutine; sampling ending UI every 0.5 seconds",
            _ => "unknown stage"
        };
        Log("Progress: " + stageName + "; elapsed=" + (Time.realtimeSinceStartup - (_deadline - TimeoutSeconds)).ToString("0") + "s.");
    }

    private static void Log(string message) =>
        BepInEx.Logging.Logger.CreateLogSource("EndingMenuProbe").LogInfo(message);

    private void CaptureScene(string page)
    {
        var texts = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(text => text != null && text.gameObject.scene.IsValid())
            .OrderBy(text => text.gameObject.scene.name, StringComparer.Ordinal)
            .ThenBy(text => HierarchyPath(text.transform), StringComparer.Ordinal)
            .ToArray();

        using var writer = new StreamWriter(EndingMenuProbePlugin.OutputPath, append: true, new UTF8Encoding(false));
        foreach (var text in texts)
        {
            writer.Write("{\"page\":\""); writer.Write(Escape(page));
            writer.Write("\",\"scene\":\""); writer.Write(Escape(text.gameObject.scene.name));
            writer.Write("\",\"path\":\""); writer.Write(Escape(HierarchyPath(text.transform)));
            writer.Write("\",\"active\":"); writer.Write(text.gameObject.activeInHierarchy ? "true" : "false");
            writer.Write(",\"enabled\":"); writer.Write(text.enabled ? "true" : "false");
            var alpha = EffectiveAlpha(text);
            writer.Write(",\"effectiveAlpha\":"); writer.Write(alpha.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(",\"visible\":"); writer.Write(text.gameObject.activeInHierarchy && text.enabled && alpha > 0f ? "true" : "false");
            writer.Write(",\"text\":\""); writer.Write(Escape(text.text ?? ""));
            writer.WriteLine("\"}");
        }
        writer.Flush();
    }

    private static string HierarchyPath(Transform leaf)
    {
        var parts = new System.Collections.Generic.List<string>();
        var current = leaf;
        while (current != null)
        {
            parts.Add(current.name + "[" + current.GetSiblingIndex() + "]");
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static float EffectiveAlpha(TMP_Text text)
    {
        var alpha = text.color.a;
        for (var current = text.transform; current != null; current = current.parent)
        {
            var group = current.GetComponent<CanvasGroup>();
            if (group == null) continue;
            alpha *= group.alpha;
            if (group.ignoreParentGroups) break;
        }
        return alpha;
    }

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
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(EndingMenuProbePlugin.OutputPath)!, "ending-menu-capture.failure.txt"), message); }
        catch { }
        BepInEx.Logging.Logger.CreateLogSource("EndingMenuProbe").LogError(message);
        Application.Quit(1);
    }
}
