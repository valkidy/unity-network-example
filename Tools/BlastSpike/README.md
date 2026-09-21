# Blast spike

Does NVIDIA's Blast authoring extension cut this project's models better than
`Assets/Scripts/Shatter`? This folder builds a small command-line tool,
`blast_cut`, on Blast 5.0.6 (the `blast/` folder of
[NVIDIA-Omniverse/PhysX](https://github.com/NVIDIA-Omniverse/PhysX), BSD-3)
and fractures OBJ files with it. It is a spike: nothing in the Unity project
depends on it.

## Build

```bash
./fetch_blast.sh                  # pinned Blast source into ./blast, patches applied
cmake -S . -B build -G Ninja      # macOS also needs: brew install boost
cmake --build build
```

Blast officially builds on Windows and Linux only. On macOS (Apple Silicon)
three changes make it build, and all are applied by the steps above:

| Problem | Fix |
|---|---|
| Blast declares its C API `extern "C"` only on Windows and Linux, so every declaration disagrees with its definition | `NV_C_EXPORT=extern "C"` in `CMakeLists.txt` |
| On ARM the vector math is scalar, `Vec3V` and `Vec4V` are different types, and `NvBlastExtApexSharedParts.cpp` assumes they are not | `patches/blast-arm-scalar.patch`, about ten lines |
| VHACD includes `<malloc.h>`, which macOS does not have | `macos-shim/malloc.h` |

The ARM patch is about ARM, not macOS: by `NsVecMath.h`'s own conditions,
linux-aarch64 takes the same scalar path, so the `Dockerfile` build on Apple
Silicon should need it too (not verified).

## Run

```bash
mkdir -p data
python3 export_glb_to_obj.py ../../Assets/Resources/Props/tower/tower-atlas.glb data/tower.obj
python3 export_glb_to_obj.py ../../Assets/Resources/Props/tower/tower-atlas.glb data/roof.obj --largest-part
./build/blast_cut data/roof.obj data/roof_voronoi8.obj voronoi 8 1
./build/blast_cut data/roof.obj data/roof_slice_noise.obj slice 1 2 1 0.08 2.0 1
```

The output OBJ has one group per leaf chunk. Faces Blast created on a cut are
`usemtl interior` (Blast's material id 1000), everything else `usemtl exterior`.

To look at the result in Unity, copy `unity/BlastSpikeView.cs` into
`Assets/Editor` of a throwaway project clone and run it in batchmode without
`-nographics`:

```bash
Unity -batchmode -projectPath <clone> -executeMethod BlastSpikeView.Run \
  -blastObj roof_voronoi8,roof_slice_noise -blastData <this folder>/data
```

It writes `<clone>/shots/*_assembled.png` and `*_exploded.png`.

## Findings (2026-09-21)

**The whole tower: unusable.** The model has open edges, and Blast's boolean
reported `Probably input mesh has open edges` 29 times. Voronoi 40 gave 326
chunks with 563 triangles over 2 m² and 16 times the source's surface area;
assembled, it is no longer recognisably the tower. Noisy slicing lost
geometry outright (35824 triangles in, 19522 out).

**One closed part (the conical roof, 3316 triangles): clean.**

| Cut | Time | Triangles | Result |
|---|---|---|---|
| Voronoi, 8 sites | 0.07 s | 3316 -> 5720 | 8 closed chunks, no warnings |
| Slicing 1x2x1, noise 0.08 @ 2.0 | 0.7 s | 3316 -> 17090 (10098 on the cuts) | 12 chunks with ragged, natural break surfaces |

Blast does not make arbitrary models cuttable: like our own clipper it needs a
closed mesh. What it adds over `MeshIslandFracturer` is noisy break surfaces
and a separate material id for them, at roughly five times the triangles on a
noisy part. The way to use it would be inside the existing bake: split parts
with `MeshIslandSplitter`, send only the closed, oversized ones to Blast, and
leave the runtime (`NetworkPropShatter` and the despawn wiring) untouched.
