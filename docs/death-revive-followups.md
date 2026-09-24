# 死亡與重生：後續工作

整理日期：2026-09-25。給下一個 session 接手用。

## 目前狀態

| 項目 | 位置 | 狀態 |
|---|---|---|
| 死亡隱藏、停止輸入、`InstigatorDead` | Unity `9dea3cd` | 已 commit，在 `claude/feat-despawn-reason-retired`，未 push |
| package lock 更新到 `bce776e` | Unity `3a0dade` | 同上 |
| 重生時 remote 不再從屍體往上滑 | kernel `cc204fe` | 已進 `bce776e` package |
| snapshot 補上 Grounded/Falling、本地改用預測值 | kernel `01444f7`，`claude/fix-replicate-ground-flags` | 已 commit，**未 push、未發布 package** |
| 重生動畫（`Flying` → `FlyToLanding`） | Unity，工作目錄 | **未 commit**，混在 `claude/feat-stagger-client` 的未 commit 修改裡 |

開新分支前，先把重生動畫的這些檔案 commit 掉，不然它們會跟著帶到新分支：

- `Assets/Scripts/Rendering/NetworkActorView.cs`
- `Assets/Scripts/Rendering/NetworkRenderStateApplier.cs`
- `Assets/Tests/EditMode/NetworkRenderingTests.cs`
- `Assets/Resources/Actors/wizard-cat/wizard-cat.controller`
- `Assets/Resources/Actors/wizard-cat/wizard-cat@flying.fbx`（和 `.meta`）
- `Assets/Resources/Actors/wizard-cat/wizard-cat@fly-to-landing.fbx`（和 `.meta`）

`Client.unity` 和 `ClientRunner.cs` 上是 stagger 的修改，跟這件事無關。

## 重生動畫現在怎麼運作

- dead flag 從 1 變 0 時，`NetworkRenderStateApplier` 呼叫 `NetworkActorView.PlayRevive()`，送出 `Revive` trigger，Any State 直接進入 `Flying`，不做 blend。
- 下降途中，用往下的 raycast 量高度、用 replicated 的速度，預測還剩多久觸地。剩下的時間不超過 `reviveLandingLeadSeconds`（0.35 s）時，送出 `ReviveLanding`，進入 `FlyToLanding`。
- 後備判斷：先看到角色離地之後，`Grounded` 變成 true 時也會觸發落地。這要等 `01444f7` 發布之後才會生效，在那之前 Grounded 永遠是 false。
- 播到 `FlyToLanding` 的 80% 時，回到 `Idle`。
- Inspector 可以調整三個值：landing lead（0.35 s）、重力（9.81，要跟 catalog 一致），以及哪些 layer 算地面。
- 重生高度由 kernel catalog 的 `player.respawn.height_offset` 決定。你打算改成 8 m，下降時間是 1.28 s。

## 需要準備的 clip

角色是 wizard-cat。匯入設定照現有的 clip：Generic、in place、30 fps。

### 建議（接下來最有用的）

1. **一般的落下 loop 和落地**（`Falling`，著地時 `ActorLanded`，或者用 `Grounded` 從 false 變成 true）
   - 為什麼：`01444f7` 發布之後，client 會第一次收到正確的 Grounded/Falling。現在的 controller 沒有這兩個參數，被擊退飛起來或從高處掉下來時，都還是在播走路或 Idle。
   - 參數：`Grounded`（bool）、`Falling`（bool）、`ActorLanded`（trigger）。程式已經會送出這三個參數，controller 還沒有。
   - 注意：Landed flag 不會放進 snapshot（只維持一個 tick，client 收到的結果不可靠）。remote 玩家的「剛落地」要用 `Grounded` 從 false 變 true 來判斷，不能靠 `ActorLanded`。
   - 如果 clip 在觸地前有預備動作，可以照重生落地的做法提前觸發。那段邏輯可以抽出來共用。

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
6. **（建議）修掉 EditMode 測試的雜訊：** `NetworkPrefabRegistry.CreatePlaceholder` 在 edit mode 呼叫 `renderer.material`，Unity 會記成 error log，讓 21 個測試失敗，也會擋住新加的測試。改成 `sharedMaterial = new Material(...)` 就會回到已知的 8 個失敗。

## 已知限制（這一版不處理）

- 死亡時手上拿的道具不會掉下來，角色隱藏後道具可能還留在原地。
- 無敵只擋傷害、不擋擊退。重生後的 2 s 無敵期間，如果發生 stagger，會打斷 `Flying`，這次重生的落地動畫就不會播。
- 重生抬升不到 2 m 時，本地玩家會平滑移動約 0.1 s，不會直接 snap。高度設成 8 m 就不會遇到。
