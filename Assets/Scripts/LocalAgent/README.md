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
completed render observations to `LocalAgentPerception`, and the controller decides only from what
that perceives (`PerceivedActor`: last known position, visible, confirmed, known dead) plus the
agent's own position and weapon state. Only visible, confirmed enemies are shot at. Commands go
through the same input sequence and action bookkeeping as manual input. It uses the existing submission clock (30 Hz by default).
Switching modes submits one neutral/release input before the new source takes over. Manual item
commands are suppressed while enabled; inventory updates continue. The camera remains available
for observation and the manual reticle is hidden. HostMode is not switched to AI.

Navigation: `ClientRunner.AgentNavMesh` holds the synchronized catalog's navigation mesh (the
`navigation_mesh.entry_path` artifact the server's patrols use), parsed by `DetourNavMesh` and
queried with `DetourNavMeshQuery` (polygon lookup, nearest walkable point, A* plus funnel paths,
area-weighted random points). Only single-tile Detour v7 meshes without off-mesh connections are
read. The mesh is static terrain: props, nests and actors are not in it.

Exploration (`enableExploration`, on by default, needs the navigation mesh): `LocalAgentExplorer`
splits the walkable area into `explorationCellSize` cells, marks cells within `sightRadius` as seen
(no line-of-sight test) and walks a navmesh path to the nearest unseen cell. Priority is combat,
then following, then exploring. With a player to follow the agent explores within `leashRadius`
of them and walks back once farther than that, until within `followStopDistance`; alone it explores
the whole mesh. A target it has no route to, or makes no 0.5 m progress towards for `stuckSteps`
input ticks, is set aside; once everything in the area is seen or set aside the area is forgotten
and exploration starts over. Without the mesh the agent follows and fights as before.

Perception (`perceptionMode`, Omniscient by default):
- Omniscient: every living actor is perceived visible and confirmed where it is, which is what the
  agent read before perception existed.
- Limited: an actor is seen when it is within `visionRange` (40 m) of the eye (`eyeHeight`, 1.6 m)
  and `fovDegrees` (110) of where the agent last aimed (its walking direction while exploring), and a
  sight line reaches its head or feet (`headSampleHeight` 1.5 m, `footSampleHeight` 0.5 m); or when it
  is within `closeAwarenessRadius` (2.5 m). Sight lines are blocked by the kernel's Hit colliders
  (actors, props; `KernelColliderShapeSource`, shared with the debug view) and by Unity colliders on
  `sightBlockingLayers` (the terrain). Sight is tested `perceptionHz` (10) times a second; an actor
  in view is tracked where it is in between. It is confirmed after `reactionSeconds` (0.25 s) in
  sight, stays confirmed while remembered, and is forgotten `memorySeconds` (8 s) after it was last
  seen. Only a death that is seen is known; Stale records are never seen. The agent does not yet go
  looking for what it remembers, and exploration still marks cells seen by distance alone.

What a client actually receives (verified against a dedicated server):
- Snapshots carry health only for Player actors. Enemies arrive with hp = 0 and
  `VisualFlagHpUnknown`. The server sets `VisualFlagDead` from hp == 0 in the same tick for
  every actor, so liveness is decided by that flag alone and hp is never read.
- A `Stale` render state is the kernel's fill-in for an entity missing from recent snapshots
  (last known position, no dead flag), so it is never treated as alive.
- A client's `TryGetLocalWeaponState` never sets `WeaponIdValid` and reports the server's active
  slot, which moves only when an action commits. The runner maps the slot through the loadout and,
  while the server still reports another weapon than the selected one, ignores that weapon's ammo.

Limitations: in Omniscient mode synchronized entities can be off screen or behind walls. There is no
obstacle avoidance, ballistic lead, weapon selection, or item interaction. Server
collision and combat rules still decide hits. A wall may block following, and a hidden nearby enemy
may keep the agent in combat. Missing/stale/dead local actors and observations older than 0.5 s yield
neutral commands. No server AI knowledge or network protocol changes are involved.

Validation: use a server plus a manual client and an agent client; configure a known enemy template.
Check follow start/stop, combat and resumption, target departure/reselection, empty magazines, and
switching off while firing. Also verify the empty-list follow-only behavior and the limitations above.
