# Local Client Agent

On the Client scene's **Network Demo Client → ClientRunner**, enable **Enable Local Agent**.
The switch defaults to off and can be changed during Play Mode. Expand **Local Agent Settings**
and enter hostile Agent entity template IDs in **Enemy Template Ids** (not prefab IDs, collider
IDs, or net IDs). An empty list enables following only and logs a warning on activation.
The Client scene lists every `camp: enemy_side` actor in the catalog: 2, 21-26 and 28-32.
27 (allied_beam_sentry) is friendly and deliberately absent. Render states carry no camp, so
new enemy templates must be added here by hand.

The agent locks onto the nearest living remote Player. It starts moving beyond 4 m and stops
within 3 m. It stops to shoot the closest configured enemy within 15 m, then resumes following.
All distances and aim height offsets are configurable. It switches to **Preferred Weapon Id**
(default 0, the rifle; -1 keeps the current weapon) and restores the manual weapon slot when the
agent is switched off. It releases fire to reload, and does not repeatedly retry an unchanged empty weapon state.

Press-mode weapons (rocket, shotgun, grenade launcher; the player's default slot is the rocket)
fire once per press, so the agent presses and releases on alternate input ticks. Hold-mode
weapons (rifle) stay held while the sampler still tracks the fire action, and are released for
one tick when the kernel refuses it. Trigger modes are read from the synchronized catalog; an
unknown weapon is treated as press-mode, which fires every weapon (hold ones at a lower rate).

The controller lives in its own assembly depending only on Unity and Kernel. ClientRunner supplies
completed render observations and weapon state; commands go through the same input sequence and
action bookkeeping as manual input. It uses the existing submission clock (30 Hz by default).
Switching modes submits one neutral/release input before the new source takes over. Manual item
commands are suppressed while enabled; inventory updates continue. The camera remains available
for observation and the manual reticle is hidden. HostMode is not switched to AI.

What a client actually receives (verified against a dedicated server):
- Snapshots carry health only for Player actors. Enemies arrive with hp = 0 and
  `VisualFlagHpUnknown`. The server sets `VisualFlagDead` from hp == 0 in the same tick for
  every actor, so liveness is decided by that flag alone and hp is never read.
- A `Stale` render state is the kernel's fill-in for an entity missing from recent snapshots
  (last known position, no dead flag), so it is never treated as alive.
- A client's `TryGetLocalWeaponState` never sets `WeaponIdValid` and reports the server's active
  slot, which moves only when an action commits. The runner maps the slot through the loadout and,
  while the server still reports another weapon than the selected one, ignores that weapon's ammo.

Limitations: synchronized entities can be off screen or behind walls. There is no visibility test,
pathfinding, obstacle avoidance, ballistic lead, weapon selection, or item interaction. Server
collision and combat rules still decide hits. A wall may block following, and a hidden nearby enemy
may keep the agent in combat. Missing/stale/dead local actors and observations older than 0.5 s yield
neutral commands. No server AI knowledge or network protocol changes are involved.

Validation: use a server plus a manual client and an agent client; configure a known enemy template.
Check follow start/stop, combat and resumption, target departure/reselection, empty magazines, and
switching off while firing. Also verify the empty-list follow-only behavior and the limitations above.
