# Kernel Plugin Change Requests — Visual Debugger Support

**Target:** `com.network-example.kernel` (currently `0.6.6`)
**Requested by:** Unity demo visual debugger (`NetworkDebugView`, `NetworkColliderCatalog`)
**Date:** 2026-06-17

## Context

We built an immediate-mode visual debugger that draws every entity's collider shape,
facing direction, a ground grid, and a stats panel. Collider shapes were meant to come
from `Kernel.QueryColliderShapes`, with the catalog (`bundle.bytes`) as the authoritative
size source.

In practice `QueryColliderShapes` returns **zero shapes** for the entities we can see on a
client (and render states sometimes carry `net_id == 0`), so we had to re-parse
`bundle.bytes` ourselves and synthesize shapes from `entity_type` + render transform. That
workaround cannot recover **per-instance** collider data — most visibly, a spammer
projectile is drawn as the generic `projectile_damage` AABB instead of its real
`sphere_damage` sphere.

The requests below are what the kernel should fix/expose so the debugger (and any client
tooling) can draw exact colliders without re-parsing the bundle.

---

## Request 1 — `QueryColliderShapes` must work on a client kernel

**Problem.** `QueryColliderShapes` ([Kernel.cs:236](../Library/PackageCache/com.network-example.kernel@31406f805a6c/Runtime/Core/Kernel.cs#L236))
returns 0 for render entities on a client. The only proven-working usage is the ABI smoke
test ([NetworkKernelManagedAbiSmoke.cs:257](../Library/PackageCache/com.network-example.kernel@31406f805a6c/Tests~/AbiSmoke/NetworkKernelManagedAbiSmoke.cs#L257)),
which runs against a **server** kernel with server-created entities. Collider instances
appear to be server-internal and are not materialized for client-side render entities,
even though the client loads the same catalog.

**Request.** Make collider shapes queryable on a client kernel for the entities it renders —
either by materializing collider instances client-side from the loaded catalog, or by
deriving them on demand from each render entity's transform + binding.

**Acceptance.** With a client connected to a server, `QueryColliderShapes` returns the
player/enemy hit colliders and live projectile colliders that match the entities returned
by `GetRenderStatesAtTime`.

## Request 2 — Define `null` / zero-mask query semantics ("query all")

**Problem.** A `null` query (and a query with `purpose_mask == 0`) returns nothing, so there
is no documented way to "give me every collider." Callers must already know each entity's
`net_id` and a non-zero `purpose_mask`.

**Request.** Specify and implement clear semantics:
- `query == null` → return **all** colliders for all entities and all purposes.
- `purpose_mask == 0` → treat as "all purposes" (or document that 0 means none).
- `entity_net_id == 0` → "all entities".

Document this on `KernelColliderShapeQuery` ([KernelTypes.cs:740](../Library/PackageCache/com.network-example.kernel@31406f805a6c/Runtime/Core/KernelTypes.cs#L740)).

**Acceptance.** A single `QueryColliderShapes(null, buffer)` call returns every active
collider, removing the need for one query per entity.

## Request 3 — Expose the per-instance collider/template on render entities

**Problem.** `RenderEntityState` ([KernelTypes.cs:467](../Library/PackageCache/com.network-example.kernel@31406f805a6c/Runtime/Core/KernelTypes.cs#L467))
carries `entity_type` but **no** projectile-template id or collider-template id. From
`entity_type == Projectile` alone, every projectile is indistinguishable, so a client can
only resolve the generic `projectile` binding (`projectile_damage`). It cannot tell that a
spammer projectile actually uses `sphere_damage`.

**Request.** Add a per-instance identifier to `RenderEntityState` that lets a client resolve
the exact collider, e.g. `uint collider_template_id` (preferred — directly usable) and/or
`uint projectile_template_id`.

**Acceptance.** Given a render state, a client can look up the exact collider template the
entity instance is using, and the spammer projectile resolves to `sphere_damage`.

## Request 4 — Catalog read-back API (avoid re-parsing `bundle.bytes`)

**Problem.** The kernel parses `bundle.bytes` in `LoadGameplayCatalogFromMemory`, but offers
no getter for the loaded collider templates/bindings. We had to re-open the zip and parse
the YAML ourselves (`NetworkColliderCatalog`), duplicating the kernel's parsing and risking
drift from the kernel's interpretation.

**Request.** Add read-back APIs returning the parsed catalog the kernel is actually using:
- `uint GetColliderTemplates(KernelColliderTemplateDefinition[] outTemplates)`
- `uint GetColliderBindings(KernelColliderBindingDefinition[] outBindings)`
- (optional) `uint GetProjectileTemplates(KernelProjectileTemplateDefinition[] outTemplates)`

These structs already exist in the managed layer.

**Acceptance.** A client can enumerate the loaded collider templates/bindings without
touching `bundle.bytes`, and the values match what the kernel uses for collision.

## Request 5 — Document collider purpose coverage & guarantee hit/damage are queryable

**Problem.** Player/enemy colliders are `purpose: hit`; projectile/explosion colliders are
`purpose: damage`. It is unclear which purposes are persistently queryable vs. transient
(e.g. segment hit-scan colliders with `lifetime_ticks`).

**Request.** Document, per shape, when it is queryable, and guarantee persistent `hit` and
`damage` colliders are returned for the lifetime of their entity. Note transient colliders
(segments/beams) and their tick lifetimes.

**Acceptance.** Querying with `purpose_mask = Hit | Damage | Trigger` deterministically
returns the colliders that exist for each entity at the queried time.

## Request 6 — Two different collider-mapping mechanisms in the catalog

**Problem.** A projectile's collider can be decided by **two independent mechanisms**, and
they disagree:

1. **Entity-type binding** — `collider_templates/default.yaml` → `bindings:` maps an
   `entity_type` to one collider template:
   ```yaml
   bindings:
     - entity_type: projectile
       collider_template: projectile_damage   # AABB 0.1³  (generic default for ALL projectiles)
   ```
2. **Per-projectile-template override** — each projectile template names its own collider:
   ```yaml
   # projectile_templates/spammer.yaml
   name: spammer_projectile
   collider_template: sphere_damage           # sphere r=0.2  (overrides the generic default)
   ```

So `entity_type == projectile` resolves to `projectile_damage`, but the spammer instance
actually uses `sphere_damage`. Any consumer keyed on `entity_type` (the only per-instance
field in `RenderEntityState`, see Request 3) silently gets the **wrong** collider. The
precedence between the two mechanisms is also undocumented — is the binding a default that
the template overrides, or vice versa?

This is the root reason the debugger draws the spammer projectile as the generic AABB rather
than its real sphere, and it is independent of Request 3: even with a per-instance template
id, a consumer still has to know which of the two mappings wins.

**Request.** Pick one of:
- **(a) Document and enforce precedence.** State explicitly that the projectile template's
  `collider_template` overrides the `entity_type` binding, and expose the *resolved*
  collider per instance (ties into Requests 3/4) so consumers never re-derive it.
- **(b) Collapse to one mechanism.** Drop the `entity_type → collider` binding for entity
  types that already resolve their collider per template (projectiles), so there is a single
  source of truth.

**Acceptance.** Given a render entity, there is exactly one documented way to obtain the
collider the kernel actually uses, and it returns `sphere_damage` for a spammer projectile.

---

## Priority

| # | Request | Priority | Unblocks |
|---|---------|----------|----------|
| 1 | Client-side `QueryColliderShapes` | **High** | Exact shapes on the client; removes the bundle-parsing workaround |
| 3 | Per-instance collider id on `RenderEntityState` | **High** | Correct spammer/rocket/homing projectile shapes |
| 6 | Two conflicting collider-mapping mechanisms | **High** | Single source of truth; spammer resolves to `sphere_damage` |
| 2 | `null` / zero-mask "query all" semantics | Medium | One-call collider enumeration |
| 4 | Catalog read-back API | Medium | Drop `NetworkColliderCatalog` re-parsing |
| 5 | Purpose/lifetime documentation | Low | Predictable segment/beam rendering |

## What we can remove once these land

- **Requests 1 + 2** → delete the `bundle.bytes` re-parse path
  (`NetworkColliderCatalog.cs`) and the per-entity query loop; use one
  `QueryColliderShapes(null, …)` call in `NetworkDebugView.Capture`.
- **Request 3** → projectiles (incl. spammer) render with their exact per-instance collider.
- **Request 4** → if Request 1 is partial, read-back still lets us reconstruct exact shapes
  from the kernel's own catalog instead of our YAML parser.
