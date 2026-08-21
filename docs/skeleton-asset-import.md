# Skeleton asset import guide

How to bring a kernel skeleton asset into Unity so the kernel pose renders
correctly. Written after the `simplified_monster_sim_v4` integration; every rule
below is one bug that actually shipped.

The kernel drives presentation by overwriting **every bone's local transform**
each frame (`KernelSkeletonPoseApplicator`). That makes the Unity hierarchy a
slave to the ozz runtime skeleton, so anything the importer changes about the
hierarchy or the coordinate convention becomes a visible defect.

## Rules

### 1. Import the `.glb`, never a converted `.fbx`

The `.glb` under `game_server/skeleton_assets/raw/` is the same file the `.ozz`
runtime skeleton is generated from. Import it with **glTFast**
(`com.unity.cloud.gltfast`); it reproduces the skeleton node for node.

A Blender `.glb -> .fbx` round-trip silently damaged all of the following, and
cost 9.4 m of mean per-bone error against the native capture:

| Damage | Consequence |
|---|---|
| extra `world` node above `SIM_Root` carrying the -90° X axis conversion | applied on top of every bone — dominant error |
| `LOC_FrontArc`, `LOC_Com`, `LOC_Mouth` dropped | `KernelSkeletonBinding` requires all 41 transforms |
| `SKIN_BindCarrier` reparented to the file root, demoted to a static mesh | binding parent mismatch, skinning lost |
| every `GEO_*` node rotated 90° | mesh parts oriented wrong |

`KernelSkeletonBinding.TryValidate` **cannot** catch the `world` node: `SIM_Root`
is the skeleton root, so its parent is never validated. It reports all 41 bones
bound while the pose is wrong.

### 2. `PreservePrefabBindPose = false`

The prefab bind pose must not be used as a delta base. glTFast and ozz disagree
on the bind pose (see rule 3), so delta mode lands ~8 m off per bone. Writing the
native local transforms verbatim reproduces the native capture exactly.

### 3. Un-mirror the geometry

glTF is right-handed, Unity is left-handed. **glTFast negates X on both node
transforms and mesh vertices** — self-consistent, so the import looks fine. The
ozz converter instead keeps the raw glTF values:

| node | raw `.glb` | ozz / native | glTFast in Unity |
|---|---|---|---|
| `LOC_FrontArc` | +5.000 | +5.000 | **-5.000** |
| `JNT_LegRearRight_Hip` | +4.200 | +4.200 | **-4.200** |

Because the applicator overwrites the nodes with native values, the joints end up
in glTF space while the meshes stay mirrored, and the rig renders inside out.

glTFast's conversion is per-node-local (`v_unity = M * v_gltf`,
`M = diag(-1,1,1)`), so re-applying `M` on a **child** of each bone cancels it
exactly. The child is not part of the 41-bone binding, so the applicator never
touches it.

### 4. Drop the skinned bind carrier

`SKIN_BindCarrier` is a rigging artifact, not visible geometry — the body is
drawn by the `GEO_*` parts, and the native capture does not render it at all. Its
bind matrices live in glTFast's mirrored space and a child transform cannot
cancel that the way it can for rigid meshes, so it renders as a large flat quad.
Remove the `SkinnedMeshRenderer`.

### 5. Assign materials — the sources have none

Both skeleton `.glb` files declare `materials: 0` and no primitive references a
material. glTFast correctly leaves every material slot empty, so the rig is
present in the hierarchy and **never drawn**. Assign a placeholder to every
unassigned slot.

The same trap applies to `GameObject.CreatePrimitive`, which hands out the
built-in `Default-Material`.

### 6. Match the shader to the *active* render pipeline

> This project ships the URP package and `UniversalRenderPipelineGlobalSettings`
> but has **no `UniversalRenderPipelineAsset` assigned** in Graphics settings
> (`m_CustomRenderPipeline: {fileID: 0}`), so it runs the **built-in** pipeline.
> Every `Universal Render Pipeline/Lit` material therefore draws magenta —
> including the pre-existing `AgentPlaceholder`, `PlayerPlaceholder` and
> `ProjectilePlaceholder` materials.

Pick the shader from `GraphicsSettings.currentRenderPipeline` rather than
hardcoding one. Assigning a URP asset is a project-wide decision; once it is
assigned, re-run the prefab builder and it switches to URP/Lit automatically.

## Pipeline entry point

`Tools ▸ Network Example ▸ Build Actor Rig Prefabs`
([NetworkActorRigPrefabBuilder.cs](../Assets/Editor/NetworkActorRigPrefabBuilder.cs))
applies rules 2-6 and repairs rule 1's damage defensively, so a regressed source
is corrected rather than silently mis-posed. Batch entry point:
`NetworkExample.UnityDemo.EditorTools.NetworkActorRigPrefabBuilder.BuildBatch`.

Adding a new skeleton needs: the `.glb`, the skeleton asset id and content hash
from its `*.skeleton_manifest.json` in the gameplay bundle, and — if
`KernelSkeletonBinding` has no built-in profile for it — an explicit bone-name
array in manifest order with `AutoMapKnownSkeleton = false`.

## Acceptance gate

The Editor-side gate used to live in `LocomotionTestVerifier` and the
`LocomotionTest` scene, both removed along with the scripted listen-host
locomotion rig: it presented the authority's own pose, which is not what a
client renders, so passing it said nothing about the dedicated-server path.
Verify an import against the native capture instead, in the kernel repository:

```bash
bazel run --config=macos -c opt //engine/src/tests/kernel_tests:locomotion_capture -- --sampling=300 --path="+X"
```

The check that matters is **worst bone delta**: every presented bone is compared
against a procedurally generated rig that writes the native locals verbatim, so
its FK is the native pose by construction. Threshold 1 mm; the correct import
scores `0.0000 m`. Bone count, binding validity and **no renderer with a null
material** are worth eyeballing on the built prefab.

Bone *positions* alone are not sufficient evidence -- they were exact while the
meshes were still mirrored. Look at a render before calling it correct, and
compare against `capture/locomotion_tests/native_locomotion.mp4`.

## Gotchas

- `cameraOffset` must suit the subject's scale. The monster is ~30 m across and
  the original `(18, 14, -24)` framed it from point-blank underneath, which reads
  as a broken pose.
