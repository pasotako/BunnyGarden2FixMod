---
title: BreastFlatten 補正系
---

# BreastFlatten 補正系

[CostumeChanger 仕様](costume-changer.md) の spoke（§3 主要コンポーネントから分離）。

この系がやることは 3 つ:

1. **胸を潰す (flatten)** — キャラごとに胸サイズを縮める
2. **潰したときの動きのズレを直す** — 潰すと skin と衣装の動く量が食い違うので揃える
3. **胸物理の倍率調整** — 揺れ・慣性を per-char で調整する

密結合する `SkinShrinkCoordinator` / `MeshDistancePreserver` / `NativeSmrRegistry` の詳細は hub（§3）を参照。

[`BreastFlattenApplier.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/BreastFlattenApplier.cs) (~1090 行) /
[`BreastClothWeightShifter.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/BreastClothWeightShifter.cs) (~530 行) /
[`BreastWeightShiftMath.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/BreastWeightShiftMath.cs) (~180 行) /
[`BreastClothTuner.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/BreastClothTuner.cs) (~370 行) /
[`BreastFlattenSetupPatch.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/BreastFlattenSetupPatch.cs) (~40 行) /
[`SkinUpperWeightConformer.cs`](../BunnyGarden2FixMod/Patches/CostumeChanger/SkinUpperWeightConformer.cs) (~160 行)

`BreastFlattenApplier` / `BreastClothWeightShifter` / `BreastClothTuner` の 3 つは `Plugin.Awake` で `Initialize(host)` する。

| コンポーネント | 対象 | 何をする |
|--------------|------|---------|
| `BreastFlattenApplier` | skin (`mesh_skin_upper`) | 胸の頂点を近傍平均へ寄せて潰す（flatten clone に差し替え）。同時に skin の breast1 weight を `Spine3_skinJT` へ移す。素キャラ（Tops/Bottoms 移植無）の場合は、先に skin を Babydoll へ差し替え → 衣装の内側へ押し込んでから潰す |
| `BreastClothWeightShifter` | cloth (`mesh_costume*` = Tops 候補 SMR) | 衣装側の breast1 weight を `Spine3_skinJT` へ amount 比例で移す（動き整合）。native 衣装は頂点も潰す（`FlattenClonedMesh` 共用）、移植衣装は weight 移動のみ |
| `BreastWeightShiftMath` | (共有 helper) | weight 移動の純粋な計算部分。skin 側と cloth 側が同じ計算を使うことで動きを一致させる |
| `BreastClothTuner` | 胸 MagicaCloth (`BoneSpring` / `BoneCloth`) | 揺れ / 慣性の per-char 倍率で `springConstraint.springPower` / `damping.value` / `inertiaConstraint.worldInertia` を書き換え（F9 即時反映） |
| `BreastFlattenSetupPatch` | — | `CharacterHandle.setup` Postfix。Tops/Bottoms 移植が無い素キャラに `ApplyOverlay` を適用するエントリ。`DonorPreloadRegistry.IsAnyHostParent` で preload 用の隠しキャラを除外 |

## flatten 機構（`BreastFlattenApplier`）— 胸を潰す本体

`mesh_skin_upper` の mesh を、胸を平らにした clone（名前に `_breastflat` を付ける）に差し替える。

**仕組み**: 頂点ごとに「どれだけ潰すか」の強度 `eff = amount × breastWeight`（breastWeight = その頂点の胸らしさ）を求める。これを `BreastFlattenSmoothRadius` の範囲で近傍と距離重み平均し、滑らかな強度場にする。その強度で **Laplacian relaxation**（各頂点を近傍の平均位置へ寄せる操作）を `SmoothIterations` 回かけ、胸の頂点を周囲の肌の高さまで沈める。目標形状は明示せず、強度の低い境界が「動かない anchor」になることで、胸が周囲の素肌へなだらかに溶け込む。

- 主機構は 2026-05-26 に relaxation 方式へ変更（旧方式は bone 原点への点収縮 → 線分投影）。
- `amount` は `ResolveAmount` で `[0, AmountUpperClamp]` にクランプ（上限は 2026-05-27 に 0.95 → 1.0 へ拡張、1.0 で完全に平ら）。
- 既定値: `SmoothIterations=4` / `SmoothStrength=0.8`（1 反復あたりの寄せ率）/ `SmoothRadius=0.1`。いずれかが 0 だと flatten が効かない。

### 適用は最後に被せる（後段被せ設計）

`SkinShrinkCoordinator` が素 mesh の焼き込みと push を終えた**後**の、いま表示中の mesh に対して flatten を被せる。このとき 2 種類の「素 mesh」が別々に存在することに注意:

- `OriginalSkinUpper`（rewind の戻り先）: addressables 安定な素 mesh（素の `mesh_skin_upper` か素の Babydoll）。
- `NativeSmrRegistry` の native: session 中ずっと不変な、真の native（target 元の skin_upper）。

両者は別概念で並存する。

### 素キャラ（pure-native）経路 — Tops/Bottoms 移植が無い場合（判定 = `IsPureNativeFlatten`）

この経路では skin を次の 3 ステップで処理する（`ApplyOverlay` 冒頭）:

1. **skin を Babydoll に差し替える**（`EnsureNativeSkinSwap` → `CostumeMeshSwapper.SwapSmr`）
   - 何を: 表示用の `mesh_skin_upper` を Babydoll の mesh に swap する。
   - なぜ: 衣装は「平らな Babydoll 表面」を基準に距離保存される。skin も同じ面に揃えないと整合しないため。bones も Babydoll の並びに合わせ、flatten の頂点 index 空間を一致させる。
   - swap する前の native 状態は `SmrSnapshotStore(BreastFlatten)` に退避しておく。
2. **skin を衣装の内側へ押し込む**（`PushSkinUnderCloth`）
   - 距離保存済みの衣装より内側に skin を押す（`MeshPenetrationResolver` の scatter push）。設定は `TopsSkinShrink` / `TopsSkinShrinkFalloffRadius` / `TopsSkinShrinkSampleRadius` を流用する。
3. **flatten を被せる**
   - その上に relaxation flatten を適用する。

解除時は `RestoreFor` → `RestoreNativeSkinSwap` が Babydoll → native へ巻き戻す。

なお Tops/Bottoms 移植中は、この swap・push を `SkinShrinkCoordinator` / `*Loader` 側が担当する。そのため本経路は skip される（swap snapshot が無い = gate OFF）。

### 読めない衣装では flatten を丸ごと中止（2026-05-27）

胸を覆う衣装 mesh (`mesh_costume*` = `TopsLoader.IsTopsCandidate`) が `sharedMesh.isReadable=false`（頂点を CPU から読めない設定。実機では Bunnygirl の `mesh_costume`）の場合、衣装側の頂点を潰せない。skin だけ潰すと胸の形が衣装と食い違うため、`HasUnflattenableBreastCloth` で検出し、pure-native の BreastFlatten 全体（skin の clone も cloth の weight-shift も）を中止して native のまま保つ（適用済みなら `RestoreFor` で戻す）。

- `isReadable` や `bones` は読めない mesh でも参照できるので、この判定は頂点を読まない。
- 揺れ物理 (`BreastClothTuner`) は flatten と無関係なので、この場合も維持される。

### flatten 済み skin を距離保存の基準として渡す（`GetFlattenedReferenceSmr`）

Tops 移植の距離保存（`ApplyDistancePreservePhase`）に、`targetSkinUpper` として「flatten 済みの skin 表面」を代理 SMR で供給する。こうすると移植衣装が「flatten 後の胸位置」を基準にフィットする。代理 clone は `InvalidateProxyCache()` で破棄し、次の amount 変更で作り直す。

### 谷間の衣装を沈める（cleavage delta 縮小、2026-05-27）

- **問題**: 距離保存は衣装の浮き量を「その場の skin との距離」で決める。ところが谷間の skin は胸 weight が低く、flatten relaxation の対象外でほとんど動かない。だから左右の膨らみが消えても谷間の衣装は下りず、**平らになった body の上に浮いて見える**。
- **対策**: `MeshDistancePreserver.Preserve` に `cleavageShrink` / `cleavageWidth`（Config `BreastFlattenCleavageShrink` [0,1] 既定 1.0 / `BreastFlattenCleavageWidth` [0,1.5] 既定 1.5）を追加。谷間の度合い `c = band × gate`（band = 胸骨間の bindpose 中点面からの横距離を SmoothStep / gate = 胸 weight を 0.05→0.20 で gate）に応じて、保存する浮き量を `Lerp(minOffset, d_donor_eff, 1 − cleavageShrink·c)` で最小値 `minOffset` 側へ縮め、衣装を body へ引き下ろす。
- `cleavageShrink=0` か `cleavageWidth=0` で従来と完全一致（bit-identical）。
- 効くのは native fit（`BreastClothWeightShifter.TryApplyDistancePreserve`）のみ。Tops 移植経路は `cleavageShrink:0f` 固定で対象外。

### ライブ調整（live tune）

F9 スライダー変更（`SettingChanged`）の購読は `CostumeReflectionCoordinator` に集約済み（本クラスは scene unload だけ購読する）。Coordinator が CharID ごとに `Refresh` を呼ぶ（amount=0 なら `RestoreFor` で解除 / amount>0 なら作り直し）。対象 character は env と HoleScene の両方から探す。

### public API

`Initialize(parent)` / `ApplyOverlay(character, charId)` / `RestoreFor(character)` / `ClearScene()` / `GetFlattenedReferenceSmr(preloadSkinUpper, charId)` / `InvalidateProxyCache()` / `FlattenClonedMesh(clone, smr, amount)`（頂点 flatten のコア。`BuildFlattenedClone` と cloth 側 `BreastClothWeightShifter` が共用）/ `IsPureNativeFlatten(charId)`（Tops/Bottoms override が皆無か）/ `HasUnflattenableBreastCloth(character)`（読めない衣装か）/ `IsFlattenActive(charId)`（amount>0 か）/ `ShouldSwapSkinForFlatten(character, charId)`（flatten 駆動で swap すべきかの共通判定。Bottoms/Tops の additive 分岐で使用。下記「full-body 衣装の swap」参照）。

## 動きのズレを直す（weight shift）

**なぜ必要か**: flatten で潰した skin の頂点は bone の原点近くへ寄るので、**回転半径がほぼ 0** になり、胸 bone の物理（揺れ・collider 衝突）で動く量がほとんど消える。一方で衣装 (`mesh_costume`) は潰していない元のカップ位置で大きく動く。結果、skin と衣装の動く量がズレて「skin が衣装を突き抜けて見える」（内部資料 `docs/costume-changer-pitfalls.md` #20）。

**対策**: skin（Applier 内）と cloth（`BreastClothWeightShifter`）の **両方とも、breast1 の weight を `Spine3_skinJT` へ amount 比例で移す**。これで両者が硬い胴体（Spine3）に追従するようになり、動く量が揃う。共有する純算術が `BreastWeightShiftMath`:

- `ResolveParentBoneIndex`: 移送先を `Spine3_skinJT → Spine2 → Spine1` の順で探す（実測で 6 キャラ全部 Spine3 が breast1 の親）。
- `RedistributeWeight`: breast slot を `(1 − amount)` 倍に減らし、減らした分を親 slot へ集める。`BreastWeightThreshold = 0.05` 未満の頂点は対象外。
- **数学的な性質**: Linear skinning では weight を親へ平行移動しても bind pose の頂点位置は変わらない → 静止フレームの見た目は不変、動いたときの揺れだけが抑えられる。

**移送量の調整（per-char 倍率, 2026-05-30）**: 移送率は `{Char}BreastWeightShift`（×6, 既定 0.8, 0..1）で **flatten 量に対する倍率**として調整できる。`shiftAmount = BreastWeightShiftMath.ComputeWeightShiftAmount(flatten量, 倍率) = Clamp01(flatten量 × 倍率)` を、skin (`BreastFlattenApplier.ApplyOverlay`) と cloth (`BreastClothWeightShifter.ApplyToSmr`) の **`RedistributeWeight` 呼び出しにだけ** 渡す（flatten 形状 `FlattenClonedMesh` / distance-preserve / cleavage shrink は生 `amount` のまま不変 = 胸の見た目サイズは変わらない）。倍率を下げると揺れが残る代わりに衣装の突き抜けリスクが上がる（#20 のトレードオフ）。解決の単一ソースは `BreastFlattenApplier.ResolveWeightShiftAmount(charId)`。cloth 側は snapshot の `LastShiftAmount` を cache key に含めて倍率変更で再 apply し、`CostumeReflectionCoordinator` の `Subscribe(Configs.*BreastWeightShift, char, Proxy)` で F9 ライブ反映する。倍率 1.0 にすれば `amount × 1.0 = amount` で従来と bit-identical。**既定は 0.8**（揺れを少し残す＝従来より僅かに揺れる方向。突き抜けリスクも微増）。

## 物理倍率（`BreastClothTuner`）

`ClothType.BoneSpring` / `BoneCloth` で、rootBones の配下に `R/L_breast1_skinJT` を含む `MagicaCloth` instance を胸物理として識別する（実機では `Magica Cloth_Breast` が `BoneSpring`）。揺れ / 慣性の per-char 倍率を baseline に対する乗除で適用する（`1.0` で no-op）。baseline は instanceId ごとに cache。MagicaCloth2 型はすべて reflection で扱う。

## どの経路で何が適用されるか（適用マトリクス）

| トリガ | Applier (skin flatten + skin weight shift) | WeightShifter (cloth) | Tuner (物理) |
|-------|:--:|:--:|:--:|
| 素キャラ `setup` Postfix (`BreastFlattenSetupPatch`) | ✓ | ✓ (Tops override 無なら頂点 flatten + weight shift / 有なら weight shift のみ) | ✓ |
| Tops 移植 Apply 末尾 (phase g) | ✓ | ✓ (injected donor=weight shift のみ `flattenVerts:false` / **additive 温存 costume=頂点 flatten**。下記参照) | ✓ |
| Bottoms 単独移植 Apply 末尾 | ✓ | — (native cloth は SetupPatch でカバー) | ✓ |
| `RestoreFor` (移植解除) | clone 破棄 (Tops/Bottoms 両経路) | clone 破棄 (Tops 経路) | — |

**衣装の頂点を潰す（`flattenVerts`）かどうかの区別**:

- **native 衣装**: 潰す（`flattenVerts: true`）。本体は `TryApplyDistancePreserve` → `MeshDistancePreserver.Preserve`（donor=丸い Babydoll / target=平らな Babydoll proxy で距離保存。proxy 未完やカバー外のときだけ `FlattenClonedMesh` に fallback）。
- **移植衣装（injected donor）**: 潰さない（`flattenVerts: false`）。すでに `MeshDistancePreserver` で距離保存済みで、さらに頂点 flatten を重ねると二重に縮んで破綻するため。
- **例外 — additive（full-body target）で温存された target 衣装**: 距離保存の対象外なので native 同様に潰す（`flat=true`）。同名 `mesh_costume` が「温存」と「injected」で並ぶため、`ApplyFor(flattenVerts, distancePreservedSmrIds)` が instanceID 集合で per-SMR に区別する。

native か移植かの判定は `SetupPatch`（Tops override の有無で分岐）と `TopsLoader`（非 additive は false 固定 / additive は per-SMR）が行い、適用順には依存しない。weight shift（動き整合）はどちらのモードでも共通に効く。

**補正成分の平滑化（disp 平滑化、2026-05-26）**: 距離保存（`MeshDistancePreserver.Preserve`）の出力のうち、補正分 `disp` を衣装のトポロジで Jacobi-Laplacian 平滑化（`MeshDisplacementSmoother`、Preserve 内に統合）してから適用する。native 上着（胸 flat）・別キャラ上衣の移植の**両方**に効く。反復回数・寄せ率は F9 config `DistancePreserveSmoothIterations`（既定 1）/ `DistancePreserveSmoothStrength`（既定 0.5）で調整（0 で無効）。狙いは、逆距離² サンプリングで出る高周波ノイズ（push 増幅で表面のガタつきとして見える）を、補正成分だけ均して消すこと（base 形状・boneWeight は不変、平均は縮小写像なのでトゲは生まない）。ライブ調整は `CostumeReflectionCoordinator` が一元購読し、`DistancePreserve` の cache invalidation 付きで Tops 移植・breast-native の両方へ次フレーム反映する。

## full-body 衣装が target のときの skin/cloth swap（まとめ）

衣装は「平らな Babydoll proxy」を基準に距離保存されるので、表示用の `mesh_skin_upper` も Babydoll に揃える。要点:

- **full-body base への Tops override は常に additive(重ね着)**（`additiveMode = IsFullBodyCostume(target)`、commit `17c32fd`〜）: base の元衣装・素肌・`skin_lower` を温存し donor を inject overlay する。**donor=SwimWear も additive に含む**（旧: donor=SwimWear のみ full-body swap で全置換していたが、skin_lower 不整合回避 + 他 full-body 組合せとの一貫性のため重ね着へ統一）。ワンピース水着の下半身は `mesh_costume`(Tops 候補) に内包され overlay される。KANA(2-piece) の bikini bottom(`mesh_costume_skirt`) は物理破綻のため非 overlay（既知制約。`docs/costume-system-postmortems.md` §2「KANA SwimWear bottoms 物理破綻」参照）。**非 full-body base への SwimWear donor は従来どおり swap**。BottomsLoader 経路（Bottoms override）の SwimWear donor は本変更対象外（従来挙動）。
- **skin → Babydoll への差し替え**: Tops override の非 additive は常に差し替える（donor の衣装トポロジに合わせるため）。pure-native / additive Tops / Bottoms は、共通判定 `BreastFlattenApplier.ShouldSwapSkinForFlatten`（flatten>0 かつ 胸衣装が readable）が成立するときだけ差し替える。読めない場合（Bunnygirl）は native のまま。
- **腰の継ぎ目を直す（seam conform）**: skin_upper を Babydoll に差し替えると、腰回りの boneWeight が Babydoll の並びに変わり、native の `mesh_skin_lower` との skinning が pose 次第でズレ（腰の継ぎ目が動くと段差が出る）、さらに Babydoll の腰形状が元体型と違うため継ぎ目に静的な隙間/段差が残る。`SkinUpperWeightConformer.ConformInPlace`（純関数）が、**target 自身の Babydoll `mesh_skin_lower` への近接（既定 5mm 以内）の継ぎ目頂点だけ**を native upper の weight と位置へ全置換(snap)して直す（胸域は baby-lower から遠く触れない＝flatten 保護。胸保護境界は `max(baby-lower.y)+近接距離` で自動算出。bone index は bone 名を介して Babydoll の並びに再エンコードする）。基準の Babydoll lower は swap 元と同一 asset で seam が構造的に一致し、live lower の mask/push に影響されない。フェードは廃止（二値）。Config `SkinUpperSeamConform`（既定 true）/ `SkinUpperSeamConformDist`（0.005）。
- **衣装の頂点 flatten**: 上記マトリクスの区別どおり（距離保存済みの移植衣装は潰さない、温存された target 衣装は潰す）。
- **下半身衣装の保護（lower fade、2026-05-30）**: full-body 衣装は 1 枚の `mesh_costume` が全身のため、native fit (`BreastClothWeightShifter.TryApplyDistancePreserve`) の距離保存補正が skin_lower 域（腰から下）の衣装頂点へ漏れる（disp 平滑化の滲み / baby-lower カバー外の残留 push / boneWeight blend）。`BreastClothLowerFadeMath` が **Babydoll lower の max Y（= 腰の最上端。胸の下端ではない）** を腰ラインに、Preserve 出力を base(native) へ Y フェードで引き戻す（上=full補正 / 下=native / 帯内=SmoothStep。腰ラインを跨ぐ三角形の折れ目を width フェードで緩和）。boneWeight は補間不可なので f<0.5 で二値。Config `BreastFlattenLowerFade`（既定 true）/ `BreastFlattenLowerFadeWidth`（m, 既定 0.05、0 で腰ライン二値）。**cleavage 補正（breast-weighted = 上半身のみ作用）とは頂点集合が排他**なので競合しない。非 full-body は腰ラインより下に costume 頂点がほぼ無く実質 no-op（width>0 では腰付近頂点に微小フェードが乗りうるが、Config OFF で完全 bit-identical）。injected donor（Tops 移植片、`flat=false`）は本経路を通らず対象外。**前提**: costume mesh-local Y と baby-lower mesh-local Y が同一空間（`SkinUpperWeightConformer` と同前提、実測 2026-05-30 `DIAG_LOWERFADE_YSPACE` で数値確認: babyLowerMaxY=1.1101 / costumeY=[0.3160,1.4566]。`docs/costume-system.md` 参照）。
- **重ね着（additive）で胸を押し出す**: additive では SkinShrink push（肌を内側へ）が走らないので、胸が外側の上着を突き抜ける。config `TopsAdditiveBreastPushOut`（m, 既定 0.003）が、additive で injected した上着の距離保存に、胸 weight に応じた standoff を target 法線方向へ足し、衣装を外へ押して吸収する（非 additive / native では 0 = 不変。実際の押し出し量は平滑化で目減りする）。
- **既知の限界**: additive は SkinShrink push が無い（胸は上記 push-out で代替、他部位は swap+flat だけ）。full-body 衣装に Bottoms override したときの swimsuit flatten は未実装。Tops+Bottoms 併用 + full-body は snapshot の二重管理が未解決。

## ライフサイクル

- **scene unload**: Applier / WeightShifter とも `ClearScene` で entry を破棄するが、`m_holeScene` に残る character（Unity-null でない）は温存する（memory `feedback_scene_unload_snapshot_clear` と同方針）。Tuner は明示的に Clear せず、次の `ApplyFor` の `IsValid()` チェックで自然に skip される。
- **Restore の順序**: `*Loader.RestoreFor` は、SMR snapshot を復元するループより**前**に `BreastFlattenApplier.RestoreFor` / `BreastClothWeightShifter.RestoreFor` を呼ぶ。こうしないと `OriginalSkinUpper` の再捕獲が flatten clone を「素」と誤って捕まえてしまう。

## 既知の制約

`amount > 0.5` + VIP シーン + Tops 移植 + collider 衝突モーションが重なると、上着が動いている skin に追従しきれず突き抜けて見える現象が残る（内部資料 `docs/costume-changer-pitfalls.md` #20。weight shift で緩和したが完全解消は未確定）。config 上限自体は 2026-05-27 に 1.0 へ拡張済みだが、この現象を避けるため VIP + Tops 移植時は `amount ≤ 0.5` を推奨。中期の対策案 = proxy SMR を runtime `BakeMesh` 化（別 plan 予定）。
