# 死亡與重生：後續工作

整理日期：2026-09-25。給下一個 session 接手用。

## 目前狀態

| 項目 | 位置 | 狀態 |
|---|---|---|
| 死亡隱藏、停止輸入、`InstigatorDead` | Unity `9dea3cd` | 已 commit，在 `claude/feat-despawn-reason-retired`，未 push |
| package lock 更新到 `bce776e` | Unity `3a0dade` | 同上 |
| 重生時 remote 不再從屍體往上滑 | kernel `cc204fe` | 已進 `bce776e` package |
| snapshot 補上 Grounded/Falling、本地改用預測值 | kernel `01444f7`，`claude/fix-replicate-ground-flags` | 已 commit，**未 push、未發布 package** |
| 重生動畫（`Flying` → `FlyToLanding`） | Unity `dad2cc9` | 已 commit，在 `claude/feat-stagger-client` |
| EditMode 測試雜訊（placeholder material） | Unity `9eb262c` | 已 commit，在 `claude/fix-placeholder-shared-material` |
| 一般落下與落地（`Falling` → `Landing`） | Unity，同一個分支 | 已 commit，用 `falling` / `falling-to-landing` |
| 擊退的飛行與落地（`ImpactFalling` → `ImpactLanding`） | Unity，同一個分支 | 已 commit，見 `impulse-stagger-interaction.md` |

## 重生動畫現在怎麼運作

- dead flag 從 1 變 0 時，`NetworkRenderStateApplier` 呼叫 `NetworkActorView.PlayRevive()`，送出 `Revive` trigger，Any State 直接進入 `Flying`，不做 blend。
- 下降途中，用往下的 raycast 量高度、用 replicated 的速度，預測還剩多久觸地。剩下的時間不超過 `landingLeadSeconds`（0.35 s）時，送出 `ReviveLanding`，進入 `FlyToLanding`。
- 後備判斷：先看到角色離地之後，`Grounded` 變成 true 時也會觸發落地。這要等 `01444f7` 發布之後才會生效，在那之前 Grounded 永遠是 false。
- 播到 `FlyToLanding` 的 80% 時，回到 `Idle`。
- Inspector 的 Falling 區可以調整：三種 landing lead（重生 0.35 s、一般落下 0.25 s、擊退 0.2 s）、判斷擊退的兩個速度門檻、重力（9.81，要跟 catalog 一致），以及哪些 layer 算地面。
- 重生高度由 kernel catalog 的 `player.respawn.height_offset` 決定。你打算改成 8 m，下降時間是 1.28 s。

## 需要準備的 clip

角色是 wizard-cat。匯入設定照現有的 clip：Generic、in place、30 fps。

### 建議（接下來最有用的）

1. ~~一般的落下 loop 和落地~~：已接好，用 `wizard-cat@falling`（loop）和 `wizard-cat@falling-to-landing`。擊退另外用 `impact-falling` 和 `impact-falling-flat`，細節在 `impulse-stagger-interaction.md`。
   - `Airborne` 由 `NetworkActorView` 決定，跟重生用同一套預測：kernel 的 Falling flag 要是 up，而且預測離觸地還超過 landing lead，才算在空中；剩下的時間進入 lead 之內，或者 Grounded 變成 true，就開始落地。所以落差很小的情況（例如走下路緣）不會進入 `Falling`。
   - controller 沒有加 `Grounded`、`Falling`、`ActorLanded` 這三個參數。`Airborne` 和 `Launched` 已經涵蓋落下和落地。`ActorLanded` 是 trigger，沒被 state 消耗就會一直留著，而且 remote 收不到可靠的 landed。
   - 在 `01444f7` 發布之前，client 收不到 Falling flag，所以這幾個 state 都不會出現。

### 看設計決定

2. **死亡動畫**（`DeathTrigger` / `Dead`）
   - 現在 dead flag 一變成 1，角色就立刻隱藏，所以就算有死亡動畫也看不到。
   - 如果要播：controller 要加參數和 state，而且 `SetBodyHidden` 要改成等動畫播完、或延遲一段固定時間再隱藏。另外要決定 splatter 在死亡那一刻噴，還是等倒地後才噴。
   - 對時間的影響：重生倒數是 4 s，死亡動畫不能播太長。

### 跟這條主線無關（程式會送出，但 controller 沒有）

- `Reloading` / `ReloadCommit`（換彈）、`CastingCommit`（施法）、`Windup` / `Recovery`（動作前搖、後搖）。等要做武器表現時再處理。

## 後續工作

1. **kernel `01444f7`：** push、合併到 main、同步到 dev-latest，然後重新發布 package。
2. **Unity：** 更新 `Packages/packages-lock.json` 並 commit。
3. **驗證：** 參考 memory 裡的 `probe-client-against-own-server`，對自己開的 server 跑一次，確認三件事：
   - log 裡的 `grounded` 會在 True 和 False 之間切換
   - 重生落地正常
   - knockback 鎖定在落地時就解除
4. **重新調 knockback 鎖定：** 之前在 pure client 上 grounded 永遠是 false，所以 `NetworkInputSampler.UpdateLocalActorState` 的鎖定每次都要等到 `KnockbackLockoutLimitSeconds` 逾時才解除。修正後會在落地時解除，0.25 s grace 那條規則也是第一次真的生效。要確認手感，必要的話重新調這個上限。
5. **push：** Unity 的 `claude/feat-despawn-reason-retired`，以及 local main 上的 `251c9b9`。
6. ~~修掉 EditMode 測試的雜訊~~：已在 `9eb262c` 修好，回到已知的 8 個失敗。
7. **驗證落下和擊退：** `01444f7` 發布之後，確認兩件事：
   - 從高處走下去時播 `Falling` → `Landing`；
   - 被 grunt slam 或爆炸打飛時播 `ImpactFalling` → `ImpactLanding`，而且 grunt slam 的 stagger 不會把飛行動畫蓋掉。

## 已知限制（這一版不處理）

- 死亡時手上拿的道具不會掉下來，角色隱藏後道具可能還留在原地。
- 無敵只擋傷害、不擋擊退。重生後的 2 s 無敵期間，如果發生 stagger，會打斷 `Flying`，這次重生的落地動畫就不會播。
- 重生抬升不到 2 m 時，本地玩家會平滑移動約 0.1 s，不會直接 snap。高度設成 8 m 就不會遇到。
