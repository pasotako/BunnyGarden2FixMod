using BunnyGarden2FixMod.Utils;
using GB.Bar.MiniGame;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;
using UITKit;

namespace BunnyGarden2FixMod.Patches;

/// <summary>
/// カラオケ（リズムゲーム）のノーツ表示を改善するパッチ群。
///
/// ■ ゲーム側の仕組み
///   - ノーツのスクロール速度は「bpm * 3 [unit/秒]」で、曲の BPM に比例する（BPM 同期）。
///     初期配置: KaraokeJudge.AddNotes が localPos = 上方向 * (bpm*3 * ノーツ到達秒) に置き、
///     移動: KaraokeJudge.UpdateNotesPosition がコンテナを 下方向 * (bpm*3 * 経過秒) へ動かす。
///     経過秒が到達秒に一致した瞬間に判定ラインへ届く（判定は Karaoke 側の時刻比較で行われ、
///     本パッチはスクロールの見た目のみを変更し判定タイミングには一切影響しない）。
///   - 経過秒 (sec) は AudioSettings.dspTime の積算で、dspTime はオーディオバッファ単位
///     （既定 約21ms ≒ 47Hz）でしか進まないため、高 FPS でもノーツはカクついて見える。
///   - 判定: getJudge(delta, isInput)。delta = 入力時刻 - ノーツ時刻（負 = 早い / 正 = 遅い）。
///     GREAT(±0.1s) が最高評価で、以下 GOOD_FAST/GOOD_SLOW/NICE/BAD、見逃しは BAD_NO_INPUT。
///
/// ■ 機能
///   1. ノーツなめらか化: 表示用時刻を unscaledDeltaTime 積算 + dspTime へのドリフト補正で補間し、
///      FrameRate 設定と同じレート（0=無制限のときは 120Hz）に量子化して更新する。
///   2. ハイスピード: スクロール速度に 0.1〜10 倍の倍率を掛ける。
///   3. BPM 同期解除: 基準速度を全曲「eYe♡とらっかー」（全キャスト共通の DLC 曲・BPM 168）に統一。
///      DLC 未所有環境でも動くよう、BPM はアセット解析済みの定数 <see cref="RefBpm"/> を使う。
///   4. FAST/SLOW 判定表示: 最高評価 (GREAT) 以外の入力判定に対し、早ければ FAST・遅ければ SLOW を
///      画面に表示する（bemani 系のタイミングフィードバック）。
///
/// ■ 表示と判定のズレ防止
///   速度（基準 BPM × 倍率）は AddNotes（曲開始時に全ノーツ一括生成）の時点で確定し、
///   同じ曲の UpdateNotesPosition でも固定値を使う。初期配置と移動の速度が一致する限り
///   判定ラインへの到達時刻は元と同一のため、途中で設定を変えてもズレない（次の曲から反映）。
/// </summary>
[HarmonyPatch]
public static class KaraokeNotePatch
{
    private static readonly Vector3 NotesMoveDir = new(0f, -1f, 0f); // KaraokeJudge.NOTES_MOVE_DIR と同値

    // ── 曲セッション単位で固定するスクロール速度 ──
    private static float s_sessionSongBpm = -1f;  // AddNotes で観測した曲 BPM
    private static float s_sessionSpeed = -1f;    // その曲で使う確定速度 [unit/秒]

    // ── なめらか化の状態 ──
    private static int s_smoothFrame = -1;
    private static float s_smoothTime;
    private static bool s_smoothInit;

    /// <summary>
    /// 基準曲「eYe♡とらっかー」(eYe♡tracker) の BPM。
    /// DLC を所有していない環境でも動くよう、実行時ロードではなく解析済みの定数を使う。
    /// 出典: DlcBundles の dlc_karaoke_*_1 バンドル内 info.csv の 1 行目（全キャスト歌唱版で共通）。
    /// </summary>
    private const int RefBpm = 168;

    private static bool AnyScrollEnabled =>
        Configs.KaraokeNoteSmoothingEnabled.Value
        || Configs.KaraokeHiSpeedEnabled.Value
        || Configs.KaraokeBpmSyncOffEnabled.Value;

    // ─────────────────────────────────────────────────────────────────
    // 曲開始ごとに設定を読み直す。parseCsv は 1 曲のセットアップで 1 回だけ呼ばれ、
    // この直後に AddNotes ループが走る。ここでセッションを破棄しておくと、同じ曲を連続で
    // プレイした場合でも次の AddNotes で速度が再計算され、ハイスピード等の変更が反映される。
    [HarmonyPatch(typeof(Karaoke), "parseCsv")]
    [HarmonyPrefix]
    private static void ParseCsvPrefix()
    {
        ResetSession();
        s_smoothInit = false; // なめらか化の補間も曲頭で再同期させる
    }

    // ─────────────────────────────────────────────────────────────────
    // 初期配置: 曲開始時に全ノーツを一括生成する。ここで速度を確定・固定する。
    [HarmonyPatch(typeof(KaraokeJudge), nameof(KaraokeJudge.AddNotes))]
    [HarmonyPrefix]
    private static bool AddNotesPrefix(KaraokeJudge __instance, float bpm, int timing, Color? color)
    {
        if (!AnyScrollEnabled) { ResetSession(); return true; }

        // 曲（bpm）が変わったら速度を再確定。同曲中は固定値を使い続ける。
        if (bpm != s_sessionSongBpm || s_sessionSpeed <= 0f)
        {
            s_sessionSongBpm = bpm;
            s_sessionSpeed = ComputeSpeed(bpm);
            PatchLogger.LogInfo($"[KaraokeNote] スクロール速度確定: 曲BPM={bpm}, 速度={s_sessionSpeed:F1} unit/s");
        }

        // 元実装と同じ生成手順。速度のみ s_sessionSpeed、到達秒は元の曲 BPM で計算する。
        var note = UnityEngine.Object.Instantiate(__instance.m_notesPrefab, __instance.m_notesContainer.transform);
        float noteSec = Karaoke.CalcSecondsByBPMAndTiming(bpm, timing);
        note.transform.localPosition = NotesMoveDir * (s_sessionSpeed * noteSec) * -1f;
        var noteColor = color; // Harmony analyzer がパラメータ直接参照を誤検知するためローカルへ
        if (noteColor != null) note.SetColor(noteColor.Value);
        __instance.m_notes.Add(note);
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // 毎フレームの移動。タンバリン(m_notes)とサイリウム(m_gayaNotes)で同一フレームに 2 回呼ばれる。
    [HarmonyPatch(typeof(KaraokeJudge), nameof(KaraokeJudge.UpdateNotesPosition))]
    [HarmonyPrefix]
    private static bool UpdateNotesPositionPrefix(KaraokeJudge __instance, float bpm, float sec)
    {
        if (!AnyScrollEnabled) { ResetSession(); return true; }

        // AddNotes 前に呼ばれた場合（理論上ない）でも安全なようフォールバック
        float speed = (bpm == s_sessionSongBpm && s_sessionSpeed > 0f) ? s_sessionSpeed : ComputeSpeed(bpm);

        float t = Configs.KaraokeNoteSmoothingEnabled.Value ? SmoothTime(sec) : sec;
        __instance.m_notesContainer.transform.localPosition = NotesMoveDir * (speed * t);
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // FAST/SLOW 判定表示: 判定確定の瞬間に delta の符号でフィードバックを出す。
    // GREAT（最高評価）と NONE（未確定）、BAD_NO_INPUT（見逃し・isInput=false）には出さない。
    [HarmonyPatch(typeof(Karaoke), "getJudge")]
    [HarmonyPostfix]
    private static void GetJudgePostfix(float delta, bool isInput, Karaoke.Judge __result)
    {
        if (!Configs.KaraokeFastSlowEnabled.Value) return;
        if (!isInput) return;
        if (__result == Karaoke.Judge.NONE
            || __result == Karaoke.Judge.GREAT
            || __result == Karaoke.Judge.BAD_NO_INPUT) return;

        KaraokeFastSlowView.Show(fast: delta < 0f);
    }

    // ─────────────────────────────────────────────────────────────────

    /// <summary>基準 BPM（同期解除時は eYe♡とらっかー = 168、通常は曲 BPM）× 3 × ハイスピード倍率。</summary>
    private static float ComputeSpeed(float songBpm)
    {
        float baseBpm = Configs.KaraokeBpmSyncOffEnabled.Value ? RefBpm : songBpm;

        float mult = Configs.KaraokeHiSpeedEnabled.Value
            ? Mathf.Clamp(Configs.KaraokeHiSpeed.Value, 0.1f, 10f)
            : 1f;

        return baseBpm * 3f * mult; // 3f = KaraokeJudge.NOTES_SCROLL_SPEED
    }

    private static void ResetSession()
    {
        s_sessionSongBpm = -1f;
        s_sessionSpeed = -1f;
    }

    /// <summary>
    /// dspTime 由来の階段状の経過秒 (raw) を、実フレーム時間の積算 + ドリフト補正でなめらかにし、
    /// FrameRate 設定（0 なら 120）のレートに量子化して返す。
    /// 同一フレーム内の複数回呼び出し（タンバリン/サイリウム）は同じ値を返す。
    /// </summary>
    private static float SmoothTime(float raw)
    {
        if (Time.frameCount != s_smoothFrame)
        {
            s_smoothFrame = Time.frameCount;
            if (!s_smoothInit || Mathf.Abs(s_smoothTime - raw) > 0.1f)
            {
                // 初回・曲切替・ポーズ復帰などで大きくズレたら吸着
                s_smoothTime = raw;
                s_smoothInit = true;
            }
            else
            {
                // フレーム時間で進め、オーディオ時刻とのドリフトを毎フレーム少しずつ解消する。
                // raw は最大バッファ 1 個分 (約21ms) 遅れて見えるため drift は正が定常。
                s_smoothTime += Time.unscaledDeltaTime;
                float drift = s_smoothTime - raw;
                if (drift < 0f) s_smoothTime = raw;    // 音声より遅れたら即追いつく（遅延見えを防ぐ）
                else s_smoothTime -= drift * 0.02f;    // 進み側は 2%/frame で緩やかに収束
            }
        }

        // 「降ってくるフレームレート」を FrameRate 設定に一致させる（0=無制限は 120fps 相当）
        float rate = Configs.FrameRate.Value;
        if (rate <= 0f) rate = 120f;
        return Mathf.Floor(s_smoothTime * rate) / rate;
    }

}

/// <summary>
/// FAST / SLOW のタイミングフィードバック表示。
/// 画面左（一番左のタンバリン判定アイコンの上あたり）に短時間表示してフェードアウトする。lazy シングルトン。
/// </summary>
internal sealed class KaraokeFastSlowView : MonoBehaviour
{
    private const float ShowDuration = 0.45f;
    private const float FadeDuration = 0.15f;
    private static readonly Color FastColor = new(0.45f, 0.75f, 1f);  // 早い: 青系
    private static readonly Color SlowColor = new(1f, 0.55f, 0.45f);  // 遅い: 赤系

    private static KaraokeFastSlowView s_instance;

    private PanelSettings m_settings;
    private Label m_label;
    private float m_hideAt = -1f;

    public static void Show(bool fast)
    {
        if (s_instance == null)
        {
            var go = new GameObject("KaraokeFastSlowView");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<KaraokeFastSlowView>();
        }
        s_instance.ShowInternal(fast);
    }

    private void Awake()
    {
        var font = UITRuntime.ResolveJapaneseFont(out _);
        m_settings = UITRuntime.CreatePanelSettings(sortingOrder: 9000);
        var doc = UITRuntime.AttachDocument(gameObject, m_settings);
        var root = doc.rootVisualElement;
        root.style.flexGrow = 1;
        root.pickingMode = PickingMode.Ignore;

        m_label = new Label("FAST");
        m_label.pickingMode = PickingMode.Ignore;
        m_label.style.position = Position.Absolute;
        // 一番左のタンバリン（判定ライン左端）の上あたり。左寄せで判定アイコンの真上に出す。
        m_label.style.top = Length.Percent(37f);
        m_label.style.left = Length.Percent(5f);
        m_label.style.unityTextAlign = TextAnchor.MiddleLeft;
        m_label.style.fontSize = 30;
        m_label.style.unityFontStyleAndWeight = FontStyle.Bold;
        if (font != null) m_label.style.unityFont = font;
        m_label.style.display = DisplayStyle.None;
        root.Add(m_label);
    }

    private void OnDestroy()
    {
        if (m_settings != null) { Destroy(m_settings); m_settings = null; }
        if (ReferenceEquals(s_instance, this)) s_instance = null;
    }

    private void ShowInternal(bool fast)
    {
        if (m_label == null) return;
        m_label.text = fast ? "FAST" : "SLOW";
        m_label.style.color = fast ? FastColor : SlowColor;
        m_label.style.opacity = 1f;
        m_label.style.display = DisplayStyle.Flex;
        m_hideAt = Time.unscaledTime + ShowDuration;
    }

    private void Update()
    {
        if (m_hideAt < 0f || m_label == null) return;
        float remain = m_hideAt - Time.unscaledTime;
        if (remain <= 0f)
        {
            m_label.style.display = DisplayStyle.None;
            m_hideAt = -1f;
        }
        else if (remain < FadeDuration)
        {
            m_label.style.opacity = remain / FadeDuration;
        }
    }
}
