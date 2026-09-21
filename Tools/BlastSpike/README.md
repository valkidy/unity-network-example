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
./build/blast_cut data/roof.obj data/roof_slice_noise.obj slice 1 2 1 0.08 2.0 1 0.2
```

The last slicing argument is the grid the noisy surface is sampled on, in
metres (0.1 if left out); the cut faces' triangle count scales with it.

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

**One closed part (the conical roof, 3316 triangles): voronoi is exact,
slicing is not.**

| Cut | Time | Triangles | The roof's own surface afterwards |
|---|---|---|---|
| Voronoi, 8 sites | 0.07 s | 3316 -> 5720 | 100.00% of what went in |
| Slicing 1x1x2, no noise | 0.7 s | | 144% |
| Slicing 1x1x2, noise 0.08 | 0.7 s | | 137% |

The roof is closed but evidently not a clean solid, and Blast's slicing
answers it with fins of surface standing out of it -- up to 38 cm past the
roof's bounds -- with or without noise. They are easy to miss in an exploded
render and obvious once the pieces are put back together. The closed ground
disc slices cleanly.

## In the bake

`Assets/Editor/BlastCutter.cs` runs `build/blast_cut` from
`ShatterAssetBuilder` on every part that is both oversized and closed. A cut
is only kept if the model's own surface comes back within 0.5% of its size:
noisy slices first, Blast's voronoi cells second, the project's own clipper
last. For the tower that is the ground in noisy slices and the roof in
Blast's voronoi cells; the faces Blast makes go on a second submesh drawn
with `tower-interior.mat`. Without `build/blast_cut` the bake falls back to
the project's own clipper for everything and says so -- and produces a
different bake from the committed one.
