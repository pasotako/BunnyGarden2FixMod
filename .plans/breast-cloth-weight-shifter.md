# Plan: BreastClothWeightShifter

## ゴール
`mesh_costume*` SMR の breast 領域 vertex の `boneWeights` を flatten amount に比例して `R/L_breast1_skinJT` から `Spine3_skinJT` へ再配分する機構を追加する。これにより [[flatten-bone-rotation-asymmetry]] の motion mismatch (flatten 後の skin は collider 動きで radius=0 → ほぼ静止、cloth は cup 位置で large radius → 大きく揺れて skin を突き抜け表示) を、cloth 側を「breast bone を見ない」状態にして解消する。

## 前提 (diag で確定済、`docs/costume-system.md` mesh_costume の breast 骨構造 セクション参照)
- `R/L_breast1_skinJT` の親骨 = `Spine3_skinJT` (6 char 共通)
- `Spine3_skinJT` は全 SMR の `bones[]` 内に既存 → 4-slot 制約下でも parent slot 確保可
- `mesh_costume*` SMR GameObject 上に MagicaCloth は attach されていない → `originalMesh` 巻き戻しリスク低
- breast-weighted vert 規模: 11-23% / 312-1552 verts/char
- `mesh_costume_sleeve` (LUNA) は breast-weighted 0 verts (sleeve は no-op skip)

## スコープ

### 新規ファイル
- `BunnyGarden2FixMod/Patches/CostumeChanger/BreastClothWeightShifter.cs`

### 既存ファイル変更 (各 1〜数行)
- `BunnyGarden2FixMod/Patches/CostumeChanger/BreastFlattenSetupPatch.cs`: Postfix 末尾に `BreastClothWeightShifter.ApplyFor` 1 行追加
- `BunnyGarden2FixMod/Patches/CostumeChanger/TopsLoader.cs`:
  - Apply 完了直後 (L503-504 付近、既存 ApplyOverlay + Tuner.ApplyFor の隣) に `ApplyFor` 1 行追加
  - RestoreFor 経路 (L470, L888, L1002 付近、既存 `BreastFlattenApplier.RestoreFor` の隣) に `BreastClothWeightShifter.RestoreFor` 1 行ずつ追加
  - `OnDistancePreserveParamChanged` 経由の cache 無効化フックは既存 `*BreastFlatten` SettingChanged 購読を流用 (新規モジュール側でも同 config 購読 → 単純 Refresh で対処、TopsLoader 側変更不要)
- `BunnyGarden2FixMod/Plugin.cs`: Awake で `BreastClothWeightShifter.Initialize(host)` 1 行追加

## API

```csharp
internal static class BreastClothWeightShifter
{
    public static void Initialize(GameObject parent);   // SettingChanged + scene unload 購読
    public static void ApplyFor(GameObject character, CharID charId);
    public static void RestoreFor(GameObject character);
    internal static void ClearScene();                  // scene unload で preserved 以外を清掃
}
```

設計は `BreastFlattenApplier` の構造 (Entry / EnsureSceneUnloadSubscribed / OnConfigChanged / Refresh) を踏襲。

## Algorithm (per SMR)

```
1. resolve rIdx, lIdx = bones[] 内の R_breast1_skinJT / L_breast1_skinJT index
   - 両方とも -1 → skip (warning log)
2. resolve parentIdx = bones[] 内の Spine3_skinJT index
   - 未検出 → Spine2_skinJT → Spine1_skinJT の順で fallback
   - 全部未検出 → skip (warning log)
3. amount = BreastFlattenApplier.ResolveAmount(charId) と同じロジック ([0, 0.95] clamp)
   - amount <= 0 → 既存 entry あれば RestoreFor、なければ allocation 0 で完全 no-op
     (clone も entry も作らない、Mesh 描画は素のまま)
4. baseMesh = smr.sharedMesh
   - baseMesh.name が "_breastshift" suffix を持つ → 二重 Apply 防止で skip
     (前 cycle clone が RestoreFor されずに残った想定外 case、ログ警告)
5. clone = Object.Instantiate(baseMesh)
   clone.name = baseMesh.name + "_breastshift"
6. boneWeights[] = clone.boneWeights
   touched = 0
   for i in 0..verts.Length:
     bw = boneWeights[i]
     wR = GetWeightForBone(bw, rIdx)   // rIdx<0 なら 0f
     wL = GetWeightForBone(bw, lIdx)   // lIdx<0 なら 0f
     wBreast = wR + wL
     if wBreast < 0.05: continue
     bw' = RedistributeWeight(bw, rIdx, lIdx, parentIdx, amount)
     boneWeights[i] = bw'
     touched++
7. touched == 0 → clone を Destroy + entry 不登録で早期 return
   (例: mesh_costume_sleeve は breast verts 0、無駄 allocate 防止)
8. clone.boneWeights = boneWeights
9. smr.sharedMesh = clone
10. PerSmrEntry に { baseMesh, clone, lastAmount } 登録、前 clone は Destroy
    Entry.BaseMesh は **常に baseMesh で上書き** (Tops 切替で SMR.sharedMesh が新 donor mesh に
    差し替わった case でも、新 base を素として記録し直す。stale BaseMesh 問題 [後述 Risk] 対策)
```

### RedistributeWeight (BoneWeight, rIdx, lIdx, parentIdx, amount) 詳細

```
入力: bw = { (idx0,w0), (idx1,w1), (idx2,w2), (idx3,w3) }
       rIdx, lIdx, parentIdx, amount

1. 4 slot を (idx, w) の可変配列に展開
2. breastShifted = 0
   for each slot:
     if slot.idx == rIdx or slot.idx == lIdx:
       removed = slot.w * amount
       slot.w -= removed
       breastShifted += removed
3. parent slot の確保 (病態 case も含めた網羅的分岐):
   case A: 4 slot 内に既に slot.idx == parentIdx
     → slot.w += breastShifted
   case B: 4 slot 内に weight 0 (空き) slot がある
     → 空き slot に (idx=parentIdx, w=breastShifted) を書き込む
   case C: 4 slot がすべて非ゼロ、parent 不在
     → breast 系を除く最小 weight slot (= minNonBreast) を置換
       slot.idx = parentIdx
       slot.w = breastShifted + (minNonBreast.w)  // 犠牲化した weight を吸収
   case D (病態): 4 slot 全部が breast 系 (rIdx or lIdx) で埋まっている (理論的可能性、実機未確認)
     → slot[3] (最後の slot) を強制的に parentIdx 化
       slot.idx = parentIdx
       slot.w = breastShifted  // 元 slot[3] の breast weight (既に削減済) は破棄
       (理由: 病態 case では breast over-weighted vertex なので少量の追加 weight 損失は許容)
4. 検証: sum = Σ slot.w, sum > 0 を assert
   - 数学的には bone weight 平行移動なので sum 不変 (移行前 = 1.0, 移行後 = 1.0)
   - case C/D の slot 置換でも、置換前 weight を新 slot に吸収するため sum は保たれる
   - 数値誤差防衛で sum > 0.01 のとき正規化、sum <= 0.01 (異常) はログ警告 + slot[0]=(parentIdx, 1.0) フェイルセーフ
5. (idx, w) を weight 降順 sort して BoneWeight に書き戻す
```

## Lifecycle

### Entry (per-character)
```csharp
class Entry {
    GameObject Character;
    // SMR ごとに前回 clone + base mesh を保持。SMR は InstanceID で識別
    Dictionary<int, SmrSnapshot> PerSmr;
}
class SmrSnapshot {
    SkinnedMeshRenderer Smr;
    Mesh BaseMesh;        // shift 適用直前の sharedMesh (Tops 移植直後の素)
    Mesh PrevClone;       // 今 SMR.sharedMesh に乗っている _breastshift clone
    float LastAmount;
}
```

### Scene unload (`OnSceneUnloaded`)
- `BreastFlattenApplier.ClearScene` 同様、m_holeScene preserved character は温存
- Unity-null 化した entry は delete
- 生きてる entry の clone は保持 (preserved char で shift 状態を維持)

### RestoreFor
- 当該 character の entry を取得
- 各 SmrSnapshot について `smr.sharedMesh = baseMesh` に戻す
- clone は Destroy
- entry を s_entries から削除

### config 変更 live tune
- `BreastFlattenApplier.OnConfigChanged` と同じ `*BreastFlatten` SettingChanged を購読
- BepInEx `ConfigEntry.SettingChanged` は **同期 event** で subscribe 順 sync で発火する。
  Plugin.Awake 初期化順 (Applier → Tuner → Shifter) が確定し、`Applier.Refresh`
  (skin flatten 再生成) → `Shifter.Refresh` (cloth weight shift 再生成) が同 frame 内、
  描画前に完了する → 1-frame race / out-of-sync flicker は出ない
- 同 CharID の character を env + holeScene 両方から探索 (Applier と同手法)
- 各 character について `RestoreFor` → `ApplyFor` で再生成 (cache hit でも amount 変更時は確実に再構築)

## Hook 追加箇所 (詳細 diff)

### Plugin.cs (Awake)
既存:
```csharp
BreastFlattenApplier.Initialize(host);
BreastClothTuner.Initialize(host);
```
追加:
```csharp
BreastClothWeightShifter.Initialize(host);
```

### BreastFlattenSetupPatch.cs (Postfix 末尾)
既存:
```csharp
BreastFlattenApplier.ApplyOverlay(__instance.Chara, charId);
BreastClothTuner.ApplyFor(__instance.Chara, charId);
```
追加:
```csharp
BreastClothWeightShifter.ApplyFor(__instance.Chara, charId);
```

### TopsLoader.cs Apply 完了直後 (L503-504)
既存:
```csharp
BreastFlattenApplier.ApplyOverlay(character, targetCharID);
BreastClothTuner.ApplyFor(character, targetCharID);
```
追加:
```csharp
BreastClothWeightShifter.ApplyFor(character, targetCharID);
```

### TopsLoader.cs RestoreFor 経路 (L470, L888, L1002)
既存 (各箇所):
```csharp
BreastFlattenApplier.RestoreFor(character);
```
追加 (各箇所):
```csharp
BreastClothWeightShifter.RestoreFor(character);
```

## Risk 評価

### 🟢 LOW: MagicaCloth `originalMesh` override
- diag で `mesh_costume*` GO 上に MC なしを確認
- `Magica Cloth_Breast` は `BoneSpring` 型で SMR target を持たない (bone Transform のみ駆動) → sharedMesh 巻き戻し対象外
- skirt 系 MeshCloth は `IsTopsCandidate` で除外
- → 本実装着手時点では対処不要、症状出たら null-out 経路 (`MagicaClothRebuilder` 参考) を追加

### 🟢 LOW: 4-slot weight 制約 (`Spine3` 未入 case)
- diag で `Spine3` は全 SMR の `bones[]` 内に既存 → 配列 index 解決は確実
- per-vertex の 4-slot に Spine3 が含まれているかは未検証だが、breast 領域 vertex の隣接は spine 連鎖が一般的で含まれる可能性高
- 含まれない場合: 4 番目 slot (通常 weight <0.05) を犠牲にして parent 化 → 視覚影響軽微

### 🟡 MID: LUNA host-parent guard 抜け穴 (diag で確認)
- `IsAnyHostParent` が preview/preload host 配下 LUNA を捕捉できなかった
- `BreastFlattenApplier` も同 guard 使用 → 既存挙動と同等の振る舞い
- → 本実装では既存 guard を流用、症状出たら別 plan で guard 改修

### 🔴 HIGH: Entry.BaseMesh stale on Tops swap (review 指摘対応)
- TopsLoader.Apply は ApplySkinShrinkPhase で `mesh_costume*` の sharedMesh を新 donor mesh に
  差し替える。RestoreFor が先行しないケースでは、前 cycle の `_breastshift` clone が
  smr.sharedMesh に乗ったまま新 Apply に突入する可能性
- **対策**:
  - hook 配置で必ず `BreastClothWeightShifter.RestoreFor` を TopsLoader.RestoreFor 経路全箇所
    (L470 / L888 / L1002) に置く (plan に明記済)
  - ApplyFor の Step 4 で `baseMesh.name.EndsWith("_breastshift")` を検出した場合は
    **当該 SMR の entry を強制 RestoreFor してから再 Apply** (リカバリパス)。
    Step 10 で Entry.BaseMesh を常に最新 baseMesh で上書きする (stale 防止)
- **検証**: 実機で Tops 切替を 2 回連続発火させ、二度目の Apply で `_breastshift_breastshift`
  のような二重 suffix mesh が生まれていないか確認

### 🟢 NEGLIGIBLE: bind pose 整合性
- Linear skinning 数学性質より、weight 再配分は bind pose 形状を変えない (各 bone bind pose × bw_i の和 = vertex_world_in_bindpose for any w_i sum=1)

### 🟢 LOW: 二重 Apply
- `_breastshift` suffix 判定で skip
- `_breastflat` (skin flatten) と suffix 衝突しない (別 SMR 対象)

## テスト方針

1. **ビルド clean** (0 warning / 0 error)
2. **実機 amount=0** (default): 既存挙動と同一 (cloth boneWeights 未変更)
3. **実機 amount=0.5**: cloth が breast 揺動を半減 (Spine3 motion に部分追従)
4. **実機 amount=1.0**: cloth は breast bone 完全無視 = Spine3 のみ追従
5. **F9 slider live tune**: amount 連続変化で破綻なく追従
6. **costume 切替**: Tops 切替で cloth 復元 + 新 cloth に再適用
7. **m_holeScene preserved character**: Bar → VIP → Bar 跨ぎで shift 状態維持
8. **VIP collider 衝突モーション** (今回の元症状): skin と cloth の motion 量が揃い、skin 突き抜けが解消

## MeshDistancePreserver / GetFlattenedReferenceSmr との関係 (review 指摘対応)

- `BreastFlattenApplier.GetFlattenedReferenceSmr` は MeshDistancePreserver の `targetSkinSmrs` 用に
  「flatten 済 skin 表面」を proxy SMR として提供する。distance preserve は cloth 頂点を
  proxy skin (flatten 後 vertex 位置) からの距離で保持する
- 本機構は **cloth の boneWeights を再配分するのみで、bind pose 頂点位置は変えない**
  (Linear skinning の数学的性質)。proxy SMR が見るのは bind pose 形状なので、本機構導入後も
  proxy / distance preserve の意味論は不変。新規 proxy / shift 対応版 proxy は不要
- runtime 動作は cloth 側 motion を Spine3 由来に切り替えるだけ、distance preserve は bind pose 設定時の
  1 回計算なので影響なし

## 不採用案

### 別 config (`*BreastClothShiftAmount`) 新設
- 不採用理由: ユーザー要望「flatten 連動」を素直に実装すると同 amount で良い。dial を分けると UX 複雑化。decoupling 要望が出てから追加検討。

### vertex 直接 lerp (Option A: skin flatten と同手法を cloth に適用)
- 不採用理由: cloth silhouette が bone 原点へ collapse、衣装シルエット喪失。ユーザー指定の「bone weight 再配分」と語法不一致。

### MagicaCloth Update() Postfix で都度 sharedMesh 復元
- 不採用理由: diag で MC 影響なしを確認、不要な複雑度。

### Bottoms loader 経路への hook
- 不採用理由: BottomsLoader は mesh_costume を触らない (skirt/pants のみ)。hook 不要。

## 次フェーズ予告
- 実装完了後 → code-review skill → ユーザー実機検証 → 必要に応じ調整
- 検証 OK なら commit (関連 docs リンク + memory feedback あれば残す)
