# 擊退（impulse）與 stagger：交互作用

整理日期：2026-09-25。依據 kernel `origin/main`（`01444f7`）的原始碼和 catalog，以及一次 server 端移動量測。行號對應那個版本。

## 結論

- **套用：** 兩者互不影響。各自觸發、各自計時，誰都不會擋掉或取消對方。
- **讀取：** 在會讀這兩個狀態的地方才有優先度，而且不同地方的優先度相反：
  - 水平移動：impulse 鎖定 > stagger。飛行途中照常飛出去，stagger 要等鎖定解除才會定住角色。
  - 拒絕動作的原因：stagger > impulse。兩者都會拒絕動作，只是回報的原因不同。
- **表現：** 動畫只看得到 stagger（`Staggered` flag 會 replicate，鎖定不會）；本地玩家的預測只看得到 impulse 鎖定。

## 擊退的來源

`KnockedBack` 這個拒絕原因只在一個地方回傳（`damage_system.cc:132`）：角色身上有 `ImpulseLockout`，而且還沒到期。

`ImpulseLockout` 也只在一個地方加上去（`systems.cc:1222`），條件是：

1. 執行了 `apply_impulse`；
2. 強度大於目標的 `impulse_resistance`（不夠的話整個 impulse 會被略過）；
3. 這個 impulse 設定了 `lockout_ticks`，而且大於 0。

沒有設 `lockout_ticks` 的 impulse 只會推動角色，不會造成 `KnockedBack`。

目前只有兩個 action graph 設了 `lockout_ticks`：

| action graph | 強度 [水平, 垂直] | lockout_ticks | stagger | 誰會觸發 |
|---|---|---|---|---|
| `action_melee_impact` | [8.0, 3.5] m/s | 28 | 120（玩家門檻，一定 stagger） | `grunt_slam_hit`：gingerbread-infantry、gingerbread_warrior 的 grunt slam |
| `action_rocket_explosion_at_target` | [12.0, 5.0] m/s | 40 | 依傷害，45 到不了門檻 | `rocket_explosion`：火箭（weapon 3）、榴彈（weapon 7）、gingerbread_mage 的榴彈、爆炸測試瓶 |

玩家的 stagger 設定（`player.yaml`）：門檻 120、持續 12 tick、之後免疫 60 tick。

`area_effect_system.cc` 裡沒看到排除發射者本人的條件，只有 `damage_source_may_damage` 一個檢查，我沒有追進去看。所以玩家自己的爆炸可能也會把自己炸飛，這點還沒驗證。

## 套用：互不影響

| | 觸發條件 | 會不會看對方 |
|---|---|---|
| impulse | 強度大於 `impulse_resistance`。水平速度以累加（`+=`）套上去，並強制設成離地。鎖定會重新計時，不會延長上一次的剩餘時間。 | 不看 stagger。角色在 stagger 中照樣會被擊退。 |
| stagger | 累積量達到門檻，而且不在 stagger 或免疫期間 | 不看鎖定。角色在擊退飛行中照樣會 stagger。 |

同一次攻擊會在同一個 tick 同時產生這兩者：impulse 在 action pass 裡立刻套用，stagger 在 tick 結束時才生效，從下一個 tick 開始定住角色（`until_tick = current + 1 + stagger_ticks`）。

## 讀取：各處的優先度

| 讀取的地方 | 優先度 | 程式位置 |
|---|---|---|
| 水平移動（server） | **impulse 鎖定 > stagger** | `player_movement.cc:338`：鎖定期間保留擊退速度，stagger 的定住要等鎖定解除才生效 |
| 垂直移動 | 兩者都不影響 | 重力照常運作，stagger 不會把 y 歸零 |
| 拒絕動作的原因 | **stagger > impulse** | `damage_system.cc:127`：同時成立時回傳 `Staggered` |
| AI 寫入速度（`set_velocity`） | 只看鎖定 | `systems.cc:2812`：stagger 期間 AI 可以寫入速度，但會在移動階段被歸零 |
| 死亡和重生 | 一起清掉 | `systems.cc:2409`、`2744` |
| snapshot / 動畫 | 只有 stagger | `Staggered` flag 會 replicate；鎖定不會 |
| client 預測（本地玩家） | 只有鎖定 | `kernel.cc:8285` 會預測鎖定，不會預測 stagger 定住 |
| Unity 輸入 | 動作：兩者都擋，擊退從 view 判斷為 `IsLaunched` 的那一格開始擋；移動：只有 stagger 會送出 0；道具（丟、撿、使用）：在空中就擋 | `NetworkInputSampler.IsActionBlocked` / `IsMovementFrozen` / `IsItemUseBlocked` |

server 每個 tick 依這個順序決定水平速度（`player_movement.cc:333` 起）：

1. 死亡：歸零。
2. 擊退鎖定期間：保留目前的水平速度，也就是擊退的速度。
3. stagger 期間：歸零。
4. 其他情況：照輸入移動；玩家沒有輸入就歸零。

鎖定會在落地的那個 tick 解除（但不會在套用的同一個 tick 解除），或是到期時解除。

## 量測：stagger 對擊退水平位移的影響

用 kernel 的 `MovementFixture`（`impulse_lockout_test.cc`）驅動真正的 character controller。膠囊體、重力、移動速度都跟 `player.yaml` 一致；server 每秒 30 tick，stagger 持續 12 tick。

| 情況 | 落地（tick） | 落地時水平位移 | 4 秒後的位移 |
|---|---|---|---|
| 近戰 [8, 3.5]，無 stagger | 20 | 5.60 m | 按住前進：22.10 m |
| 近戰，命中當下 stagger | 20 | 5.60 m | 22.10 m（完全一樣） |
| 近戰，飛行途中（+15 tick）才 stagger | 20 | 5.60 m | 20.93 m |
| 火箭 [12, 5]，無 stagger 或命中當下 stagger | 29 | 12.00 m | 27.00 m（兩者一樣） |
| 純水平擊退 [8, 0]，無 stagger 或命中當下 stagger | 在地上滑行 | 滑到鎖定到期（28 tick）：7.63 m | 兩者一樣 |
| 近戰把角色推下 6 m 高台，無 stagger | 44（鎖定在 28 就到期） | 10.30 m | |
| 同上，+20 tick 時 stagger | 44 | 9.47 m | |
| 同上，沒有輸入 | 44 | 7.47 m | |

解讀：

1. **grunt slam 和爆炸，stagger 對擊退距離完全沒有影響。** grunt slam 的 stagger 持續 13 tick，比飛行時間的 20 tick 還短，落地前就結束了。
2. **飛行途中才觸發的 stagger，只會影響落地之後。** stagger 比落地晚結束時，落地後剩下的時間會被定住；上表 +15 的情況少走了約 1.2 m。實際上很少發生：玩家 stagger 後有 2 秒免疫，而這種情況需要一次本身不會 stagger 的擊退，再加上飛行途中另一次傷害累積到門檻。
3. **只有鎖定在空中到期時，stagger 才會改變水平位移。** 例如被打下高台、飛行時間超過鎖定。鎖定一到期，水平速度立刻被改寫：有 stagger 就歸零，有輸入就以 5 m/s 在空中轉向，沒有輸入就歸零。

量測只跑 server 端的移動程式；client 預測和 Unity 上的畫面沒有實際跑。

## 已知問題

### 1. 拒絕原因的優先度讓 client 晚一步進入擊退鎖定

Unity 靠拒絕原因決定要鎖多久：`Staggered` 只擋 0.25 s，`KnockedBack` 會鎖到落地。

1. grunt slam 同時造成擊退和 stagger。在 stagger 的 13 tick 內按動作，server 回 `Staggered`，client 不會進入擊退鎖定。
2. stagger 結束了，但角色還在空中（總共飛 20 tick），server 還在擊退鎖定。
3. 這時玩家按下動作，client 會送出去，server 以 `KnockedBack` 拒絕，client 收到後才開始鎖定。

結果大約會多浪費一次來回的動作請求；按住扳機時，可能會看到一次開火被拒絕。probe 裡 5 次擊退都是這樣：lock 在離地後 0.1–0.5 s 才出現。

**已修正：** runner 每格把 view 的 `IsLaunched` 傳給 `NetworkInputSampler.UpdateLocalActorState`。view 一判斷為被打飛，就關上跟 `KnockedBack` 拒絕同一個鎖，一直到落地才解除；中間 view 為了播落地 clip 放掉 `IsLaunched` 時，鎖不會跟著解除。所以被打飛後按的第一下不會再送出去，也不用再等拒絕原因。這只靠 client 的判斷，server 的拒絕仍然是最後的保險。

**道具（這一版的規則）：** server 在空中不會擋丟、撿、使用道具。client 這一版先擋：view 一判斷為在空中（一般落下、被打飛、重生下降都算），就開始擋，一直到 grounded flag 回來才解除；擊退鎖還沒解除時也擋。擋的時候 `NetworkItemPropController.ProcessInput` 會丟掉這三種按壓（丟掉，不排隊）。切換選取的道具不受影響。走下路緣這類沒有構成落下的短暫離地不會擋。

probe（`52f21c5` server，120 s）：11 次擊退，動作鎖和道具都在離地的那一格開始擋、落地的那一格解除；3 次重生，道具從重生開始擋到落地。

### 2. 鎖定在空中到期時，擊退會突然停住

跟 stagger 無關。鎖定到期的那一刻，水平速度會從擊退速度直接跳成輸入值或 0，沒有衰減。沒有輸入時，玩家會在空中突然停住、直直往下掉（上表 7.47 m 那一行）。被打下比約 1 m 還高的地方就會發生。

要修的話，可以改成鎖定到期後在空中保留水平速度，只在落地時才交還控制。這是 kernel 的改動。

### 3. client 預測沒有 stagger

本地玩家的預測只處理擊退鎖定，沒有 stagger 定住。平常靠 Unity 的 `IsMovementFrozen`，在收到 staggered flag 後把移動輸入送成 0，但這個 flag 要晚一個來回才收到。在「飛行途中才 stagger、落地後還被定住」的情況下，本地玩家落地後會先往前走一點，再被拉回去。

### 4. stagger 吃掉被打飛的動畫

grunt slam 每次都會 stagger，Any State 會把 animator 帶進 `Stagger`。stagger 結束回到 Idle 時，離觸地只剩約 0.31 s，已經在 0.35 s 的 landing lead 之內，`Airborne` 早就是 false 了。所以最常見的擊退來源，看不到 `Falling` 也看不到 `Landing`。

爆炸（不會 stagger）則會被 `HitReaction` 切開：本地玩家受傷時一定會播 `HitReaction`，播到 90% 才回 Idle，之後才進入 `Falling`。

**已修正：** `ImpactFalling` 從 Any State 進入，會接走 `Stagger`。而且 `IsLaunched` 期間，view 不會送出 `HitReaction` 和 `StaggerReaction` trigger（包括 remote presentation event 送來的）。probe 在修正前看到每次擊退都是 `ImpactFalling` → `HitReaction` → `ImpactFalling`，各約 0.1 s；修正後不再出現。代價是：如果 stagger 比飛行還晚結束，落地後不會補播 stagger。grunt slam 的 stagger 在空中就結束了，所以不會遇到。

## 動畫：區分「被打飛」和「掉下來」（A 方案已實作）

離地的時機只有兩種：被擊退打到空中、從高處掉下來。目前沒有跳躍（`InputButton_MoveJump` 有定義，但 kernel 沒有使用）。

| 情況 | 滯空 | 空中 state 播多久（滯空減 landing lead） |
|---|---|---|
| 爆炸擊退（垂直 5.0 m/s） | 約 1.02 s | `ImpactFalling` 約 0.82 s |
| grunt slam（垂直 3.5 m/s） | 約 0.71 s | `ImpactFalling` 約 0.51 s |
| 從高處掉下來 | 看高度 | `Falling`：落差大於約 0.31 m 才會播 |

判斷方式：

- **A（已實作）：** 在 `NetworkActorView.AdvanceFall`，離地那一刻依速度決定 `IsLaunched`：往上速度大於 1 m/s，或水平速度大於 6.5 m/s（走路是 5）。落下途中突然有往上的速度，就改成 true；飛行中不會變回 false。本地和 remote 都能用。門檻在 Inspector 的 Falling 區。
- **B（最準確）：** kernel 從 `ImpulseLockout` 產生一個新的 visual flag（下一個可用的是 `0x400`），放進 snapshot；本地玩家改用預測值。要改 kernel、發布 package，client 和 server 要用同一版。

controller（wizard-cat）：

```
Idle / Run / RunBackwards / SideStep*
   └─ Airborne && !Launched  → Falling (falling, loop)
                                 └─ !Airborne → Landing (falling-to-landing) → Idle (exit 0.8)

Any State
   └─ Airborne && Launched   → ImpactFalling (impact-falling, loop)
                                 └─ !Airborne → ImpactLanding (impact-falling-flat) → Idle (exit 1.0, blend 0.3 s)
```

- `ImpactFalling` 走 Any State，所以會蓋掉 `Stagger`、`HitReaction` 和 `Falling`，第 4 點因此解決：grunt slam 的擊退會先進 `Stagger`，下一格就被 `ImpactFalling` 接走。它排在 Any State 清單的最後，讓 `StaggerReaction`、`HitReaction` 這些 trigger 先被消耗掉，不會留到落地後才播。
- 每種落地各有自己的 landing lead，對齊各自 clip 的觸地時間：重生 0.35 s（flying-to-landing）、一般落下 0.25 s（falling-to-landing，是 flying-to-landing 加速 1.39 倍）、擊退 0.2 s（impact-falling-flat 在 0.19 s 觸地）。
- `impact-falling-flat` 結束時是躺在地上的姿勢，回到 Idle 的 0.3 s blend 就是「站起來」。如果看起來太突兀，需要一個起身的 clip。

另外，gingerbread 系列的 controller 沒有 `Airborne`，敵人被玩家炸飛時還是在播走路或 Idle。
