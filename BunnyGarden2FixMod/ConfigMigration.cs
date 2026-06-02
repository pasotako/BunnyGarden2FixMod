using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using BunnyGarden2FixMod.Utils;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace BunnyGarden2FixMod;

public static class ConfigMigration
{
    private readonly struct MigrationEntry(ConfigDefinition oldDef, ConfigDefinition newDef)
    {
        public ConfigDefinition OldDef { get; } = oldDef;
        public ConfigDefinition NewDef { get; } = newDef;
    }

    /// <summary>
    /// 廃止されたキーの定義（移行先なし・削除のみ）。
    /// </summary>
    private static readonly ConfigDefinition[] ObsoleteKeys =
    [
        // HideMoneyUIController 削除に伴い HideUI/Enabled は参照箇所が消滅したため廃止
        new("HideUI", "Enabled"),
    ];

    // NOTE: Migrations は配列順に適用される（ArrayMigration の foreach 順序）。
    // 例: 第一の "Camera/ControllerToggleFreeCam → Camera/ToggleFreeCamButton" の結果を
    //     第四の "Camera/ToggleFreeCamButton → Hotkey/ToggleFreeCamButton" がさらに移行する、
    //     といった連鎖移行が存在する。グループ順を変更すると新規ユーザーの 1 発移行パスが壊れる。
    private static readonly MigrationEntry[] Migrations =
    [
        // 第一
        new(new("AntiAliasing", "AntiAliasingType"), new("Graphics", "AntiAliasingType")),
        new(new("Camera", "ControllerToggleFixedFreeCam"), new("Camera", HotkeyConfig.GamepadKey("ToggleFixedFreeCam"))),
        new(new("Camera", "ControllerToggleFreeCam"), new("Camera", HotkeyConfig.GamepadKey("ToggleFreeCam"))),
        new(new("Camera", "ControllerToggleModifier"), new("Input", "ControllerModifier")),
        new(new("Camera", "ControllerToggleScreenshot"), new("General", HotkeyConfig.GamepadKey("CaptureScreenshot"))),
        new(new("Camera", "ScreenshotKey"), new("General", HotkeyConfig.KeyboardKey("CaptureScreenshot"))),
        new(new("Camera", "ControllerToggleTimeStop"), new("Time", HotkeyConfig.GamepadKey("ToggleTimeStop"))),
        new(new("Camera", "TimeStopToggleKey"), new("Time", HotkeyConfig.KeyboardKey("ToggleTimeStop"))),
        new(new("Camera", "ControllerTriggerDeadzone"), new("Input", "ControllerTriggerDeadzone")),
        new(new("CastOrder", "Enabled"), new("Cheat", "CastOrder")),
        new(new("Cheat", "Enabled"), new("Cheat", "Likability")),
        new(new("CostumeChanger", "Hotkey"), new("CostumeChanger", HotkeyConfig.KeyboardKey("Show"))),
        new(new("Resolution", "Width"), new("Graphics", "Width")),
        new(new("Resolution", "Height"), new("Graphics", "Height")),
        new(new("Resolution", "FrameRate"), new("Graphics", "FrameRate")),
        new(new("Resolution", "ExtraWidth"), new("Graphics", "ExtraWidth")),
        new(new("Resolution", "ExtraHeight"), new("Graphics", "ExtraHeight")),
        // 第二: develop の Config.Bind("Resolution", "FullscreenUltrawideEnabled") を Graphics に統合
        new(new("Resolution", "FullscreenUltrawideEnabled"), new("Graphics", "FullscreenUltrawideEnabled")),
        // 第三: ui.category 廃止に伴うセクション再編 (Appearance / Conversation / Ending / Input / Time を解体)
        new(new("Appearance", "DisableStockings"), new("CostumeChanger", "DisableStockings")),
        new(new("Conversation", "ContinueVoiceOnTap"), new("General", "ContinueVoiceOnTap")),
        new(new("Ending", "ChekiSlideshow"), new("Cheki", "ChekiSlideshow")),
        new(new("Input", "ControllerTriggerDeadzone"), new("General", "ControllerTriggerDeadzone")),
        new(new("Input", "ControllerModifier"), new("General", "ControllerModifier")),
        new(new("Time", "FastForwardSpeed"), new("General", "FastForwardSpeed")),
        new(new("Time", HotkeyConfig.KeyboardKey("ToggleTimeStop")), new("General", HotkeyConfig.KeyboardKey("ToggleTimeStop"))),
        new(new("Time", HotkeyConfig.GamepadKey("ToggleTimeStop")), new("General", HotkeyConfig.GamepadKey("ToggleTimeStop"))),
        new(new("Time", HotkeyConfig.KeyboardKey("FrameAdvance")), new("General", HotkeyConfig.KeyboardKey("FrameAdvance"))),
        new(new("Time", HotkeyConfig.GamepadKey("FrameAdvance")), new("General", HotkeyConfig.GamepadKey("FrameAdvance"))),
        new(new("Time", HotkeyConfig.KeyboardKey("FastForward")), new("General", HotkeyConfig.KeyboardKey("FastForward"))),
        new(new("Time", HotkeyConfig.GamepadKey("FastForward")), new("General", HotkeyConfig.GamepadKey("FastForward"))),
        // 第四: Hotkey セクション集約 (Camera / General / CostumeChanger に散在していた hotkey を統合)
        new(new("Camera", HotkeyConfig.KeyboardKey("ToggleFreeCam")), new("Hotkey", HotkeyConfig.KeyboardKey("ToggleFreeCam"))),
        new(new("Camera", HotkeyConfig.GamepadKey("ToggleFreeCam")), new("Hotkey", HotkeyConfig.GamepadKey("ToggleFreeCam"))),
        new(new("Camera", HotkeyConfig.KeyboardKey("ToggleFixedFreeCam")), new("Hotkey", HotkeyConfig.KeyboardKey("ToggleFixedFreeCam"))),
        new(new("Camera", HotkeyConfig.GamepadKey("ToggleFixedFreeCam")), new("Hotkey", HotkeyConfig.GamepadKey("ToggleFixedFreeCam"))),
        new(new("General", HotkeyConfig.KeyboardKey("ToggleOverlay")), new("Hotkey", HotkeyConfig.KeyboardKey("ToggleOverlay"))),
        new(new("General", HotkeyConfig.GamepadKey("ToggleOverlay")), new("Hotkey", HotkeyConfig.GamepadKey("ToggleOverlay"))),
        new(new("General", HotkeyConfig.KeyboardKey("CaptureScreenshot")), new("Hotkey", HotkeyConfig.KeyboardKey("CaptureScreenshot"))),
        new(new("General", HotkeyConfig.GamepadKey("CaptureScreenshot")), new("Hotkey", HotkeyConfig.GamepadKey("CaptureScreenshot"))),
        new(new("General", HotkeyConfig.KeyboardKey("ToggleTimeStop")), new("Hotkey", HotkeyConfig.KeyboardKey("ToggleTimeStop"))),
        new(new("General", HotkeyConfig.GamepadKey("ToggleTimeStop")), new("Hotkey", HotkeyConfig.GamepadKey("ToggleTimeStop"))),
        new(new("General", HotkeyConfig.KeyboardKey("FrameAdvance")), new("Hotkey", HotkeyConfig.KeyboardKey("FrameAdvance"))),
        new(new("General", HotkeyConfig.GamepadKey("FrameAdvance")), new("Hotkey", HotkeyConfig.GamepadKey("FrameAdvance"))),
        new(new("General", HotkeyConfig.KeyboardKey("FastForward")), new("Hotkey", HotkeyConfig.KeyboardKey("FastForward"))),
        new(new("General", HotkeyConfig.GamepadKey("FastForward")), new("Hotkey", HotkeyConfig.GamepadKey("FastForward"))),
        new(new("CostumeChanger", HotkeyConfig.KeyboardKey("Show")), new("Hotkey", HotkeyConfig.KeyboardKey("Show"))),
        new(new("CostumeChanger", HotkeyConfig.GamepadKey("Show")), new("Hotkey", HotkeyConfig.GamepadKey("Show"))),
    ];

    private readonly struct ResetEntry(int version, ConfigDefinition def)
    {
        public int Version { get; } = version;
        public ConfigDefinition Def { get; } = def;
    }

    // 既定値が変わったキーを「一回だけ」既定へリセットするための定義。
    // 各エントリの Version より stored が小さいときだけ適用する。
    // 今後 既定値を変えたら、新しい Version を採番してこの配列に1行追加するだけでよい。
    // 【採番規約】新エントリの Version は必ず既存の最大 Version より大きくすること。
    //   marker は常に最大 Version で上書きされるため、中間/過去番号を差し込むと
    //   既存ユーザー（stored=最大）に追い越されて永久に適用されない。
    // 既定値の literal は持たず、orphaned から該当キーを削除して BindAll に既定値で bind させる。
    private static readonly ResetEntry[] ResetToDefaultMigrations =
    [
        // v1: TopsSkinShrink/BottomsSkinShrink 新既定
        new(1, new("CostumeChanger", "TopsSkinShrink")),
        new(1, new("CostumeChanger", "BottomsSkinShrink")),
    ];

    // 値マイグレーション用の現在スキーマバージョン = 配列の最大 Version（配列が空なら 0）。
    // 配列に高い Version を追加すれば自動的に繰り上がる（手動インクリメント不要）。
    private static readonly int CurrentSchemaVersion =
        ResetToDefaultMigrations.Select(e => e.Version).DefaultIfEmpty(0).Max();

    // マイグレーション進捗を記録する内部マーカー。
    // 【不変条件】このキーは絶対に Configs.yaml / BindAll に載せないこと。
    // bind すると Bind<T> が OrphanedEntries から pull+remove してしまい、orphaned 永続前提が壊れる。
    // bind しない限り Save() の Entries.Concat(OrphanedEntries) で .cfg [Migration] セクションに恒久保持される。
    private static readonly ConfigDefinition SchemaVersionDef = new("Migration", "SchemaVersion");

    public static void Migrate(ConfigFile config)
    {
        PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 設定の移行を開始します");
        var previousSaveOnConfigSet = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;

        // Save() の I/O 例外や早期 return でも SaveOnConfigSet を必ず復元する（リークすると
        // 以後の設定変更が .cfg に保存されない分かりにくい二次障害を生むため）。
        try
        {
            var orphanedEntries = GetOrphanedEntries(config);
            if (orphanedEntries == null)
            {
                PatchLogger.LogWarning(
                    $"[{nameof(ConfigMigration)}] " +
                    $"移行のための「OrphanedEntries」にアクセスできませんでした。移行はスキップされます");
                return;
            }

            // 論理順: リネーム → 値変換 → 削除。
            ArrayMigration(orphanedEntries);
            ValueMigrations(orphanedEntries);
            RemoveObsoleteKeys(orphanedEntries);

            config.Save();
            PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 設定の移行が完了しました");
        }
        finally
        {
            config.SaveOnConfigSet = previousSaveOnConfigSet;
        }
    }

    private static Dictionary<ConfigDefinition, string> GetOrphanedEntries(ConfigFile config) =>
        (Dictionary<ConfigDefinition, string>)
        AccessTools.PropertyGetter(typeof(ConfigFile), "OrphanedEntries").Invoke(config, null);

    private static void RemoveObsoleteKeys(Dictionary<ConfigDefinition, string> orphanedEntries)
    {
        // BepInEx の OrphanedEntries から廃止キーを削除する。
        // OrphanedEntries に存在しない場合（すでにアクティブなキーとして登録済みなど）はスキップ。
        foreach (var key in ObsoleteKeys)
        {
            if (!orphanedEntries.Remove(key)) continue;
            PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 廃止された設定エントリを削除しました: {key}");
        }
    }

    private static void ArrayMigration(Dictionary<ConfigDefinition, string> orphanedEntries)
    {
        // これは少しハッキーですが、エントリの移行と古いエントリの実際の削除を処理する最善の方法です
        foreach (var entry in Migrations)
        {
            // 古いエントリが存在しない場合、それはすでに移行されているか、
            // そもそも存在しなかったことを意味するため、ログなしでスキップします
            if (!orphanedEntries.TryGetValue(entry.OldDef, out var oldValue))
                continue;

            orphanedEntries.Remove(entry.OldDef);
            PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 古い設定エントリを削除しました: {entry.OldDef}");

            // 新しいエントリがすでに存在する場合、ユーザーがすでに手動で移行したか新しいエントリを変更したことを意味するため、
            // さらにログなしでスキップします。
            if (orphanedEntries.ContainsKey(entry.NewDef))
                continue;

            orphanedEntries.Add(entry.NewDef, oldValue);
            PatchLogger.LogInfo(
                $"[{nameof(ConfigMigration)}] " +
                $"新しい設定エントリに移行しました: {entry.NewDef} = {oldValue}");
        }
    }

    // スキーマバージョンゲート付きの「一回だけ」移行。bind 前なのでアクティブキーも orphaned に入っている。
    // stored より新しい Version のエントリだけを適用する（差分適用）。
    private static void ValueMigrations(Dictionary<ConfigDefinition, string> orphanedEntries)
    {
        var stored = 0;
        if (orphanedEntries.TryGetValue(SchemaVersionDef, out var storedStr))
            // parse 失敗（手書きで壊された値など）は stored=0 に落とす（=全移行を再適用）。
            int.TryParse(storedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out stored);

        foreach (var entry in ResetToDefaultMigrations)
        {
            if (entry.Version <= stored) continue;
            ResetToDefault(orphanedEntries, entry.Def);
        }

        // 新規インストール（orphaned 空）でも必ずマーカーを刻む。以後ゲートが効く。
        orphanedEntries[SchemaVersionDef] = CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);
    }

    // orphaned から該当キーを削除し、後段 BindAll が Configs.yaml の既定値で bind するようにする。
    // キーが無ければ（既に既定 / 未保存）無音 skip。
    private static void ResetToDefault(Dictionary<ConfigDefinition, string> orphanedEntries, ConfigDefinition def)
    {
        if (!orphanedEntries.Remove(def)) return;
        PatchLogger.LogInfo($"[{nameof(ConfigMigration)}] 既定値にリセットしました: {def}");
    }
}