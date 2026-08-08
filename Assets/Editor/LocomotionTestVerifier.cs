using System;
using System.Text;
using NetworkExample.Kernel;
using NetworkExample.Kernel.Host;
using NetworkExample.Kernel.Presentation;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Host;
using NetworkExample.UnityDemo.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Batchmode check for the LocomotionTest scene setup: drives the same kernel
    /// call sequence the scene behaviour uses, builds the generated monster rig,
    /// and applies each pose through KernelSkeletonPoseApplicator. Prints a report
    /// in the shape of capture/locomotion_tests/report.txt.
    /// </summary>
    public static class LocomotionTestVerifier
    {
        private const uint ScriptedInputSeqBase = 1000000u;

        private const string ScenePath = "Assets/Scenes/LocomotionTest.unity";

        [MenuItem("Tools/Network Example/Verify Locomotion")]
        public static void Run()
        {
            int failures = VerifyScene();
            failures += Verify(300, 30, "+X", new Vector3(0f, 10f, 0f), 20u);
            if (failures != 0)
            {
                Debug.LogError("VERDICT: FAIL (" + failures + " check(s))");
            }
            else
            {
                Debug.Log("VERDICT: PASS");
            }
        }

        public static void RunBatch()
        {
            int failures;
            try
            {
                failures = VerifyScene();
                failures += Verify(300, 30, "+X", new Vector3(0f, 10f, 0f), 20u);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(2);
                return;
            }

            Debug.Log(failures == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
            EditorApplication.Exit(failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// Opens LocomotionTest.unity and checks the wiring the runtime behaviour
        /// depends on: no unresolved MonoScript references, and the observer
        /// behaviour pointing at a presentation world and a camera.
        /// </summary>
        private static int VerifyScene()
        {
            var report = new StringBuilder();
            int failures = 0;
            void Check(bool ok, string label)
            {
                report.AppendLine("  [" + (ok ? "PASS" : "FAIL") + "] " + label);
                if (!ok)
                {
                    ++failures;
                }
            }

            report.AppendLine("Actor rig prefabs");
            if (NetworkSkeletonManifests.TryGetMonsterSim(
                    out KernelSkeletonManifest monsterManifest,
                    out string monsterManifestError))
            {
                CheckRigPrefab(
                    NetworkActorRigPrefabBuilder.MonsterPrefabPath,
                    monsterManifest.AssetId,
                    monsterManifest.ContentHash,
                    monsterManifest.BoneCount,
                    Check);
            }
            else
            {
                Check(false, "monster skeleton manifest: " + monsterManifestError);
            }
            CheckRigPrefab(
                NetworkActorRigPrefabBuilder.RockRobotPrefabPath,
                NetworkActorRigPrefabBuilder.RockRobotSkeletonAssetId,
                NetworkActorRigPrefabBuilder.RockRobotSkeletonContentHash,
                18,
                Check);

            report.AppendLine("LocomotionTest scene wiring");
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Check(scene.IsValid(), "scene opened: " + ScenePath);
            if (!scene.IsValid())
            {
                Debug.Log(report.ToString());
                return failures;
            }

            int missingScripts = 0;
            NetworkKernelMonsterObserverBehaviour observer = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in
                         root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null)
                    {
                        ++missingScripts;
                        continue;
                    }
                    if (behaviour is NetworkKernelMonsterObserverBehaviour typed)
                    {
                        observer = typed;
                    }
                }
            }

            Check(missingScripts == 0, "no missing MonoBehaviour scripts");
            Check(observer != null, "NetworkKernelMonsterObserverBehaviour present");
            if (observer == null)
            {
                Debug.Log(report.ToString());
                return failures;
            }

            var serialized = new SerializedObject(observer);
            Check(
                serialized.FindProperty("presentationWorld").objectReferenceValue != null,
                "presentationWorld is assigned");
            Check(
                serialized.FindProperty("observerCamera").objectReferenceValue != null,
                "observerCamera is assigned");
            Check(
                serialized.FindProperty("gameplayCatalogEntryPath").stringValue ==
                "monster_observer_gameplay_catalog.yaml",
                "catalog entry path targets monster_observer");
            Check(
                serialized.FindProperty("entityTemplateId").uintValue == 20u,
                "capture subject uses entity template 20");

            // A scene path that fails to parse only shows up as an error at play
            // time, and a looping path that never finishes silently degrades into
            // a one-way walk into the patrol bounds.
            string authoredPath = serialized.FindProperty("pathScript").stringValue;
            bool loops = serialized.FindProperty("loopPath").boolValue;
            bool pathParsed = NetworkLocomotionPathScript.TryParse(
                authoredPath,
                Mathf.Max(1, serialized.FindProperty("tickRate").intValue),
                out NetworkLocomotionPathScript scenePath,
                out string scenePathError);
            Check(
                pathParsed,
                "scene path parses: '" + authoredPath + "'" +
                (pathParsed ? string.Empty : " -- " + scenePathError));
            if (pathParsed)
            {
                Check(
                    !loops || !scenePath.IsUnbounded,
                    "looping scene path finishes: " + scenePath.Description);
            }
            // An unbounded path is a one-way capture run by construction -- it
            // leaves any box eventually and leans on the runtime guard -- so only
            // a path that turns around on its own is held to the bounds.
            if (pathParsed && !scenePath.IsUnbounded)
            {
                Check(
                    StaysInsidePatrolBounds(
                        scenePath,
                        serialized.FindProperty("spawnPosition").vector3Value,
                        serialized.FindProperty("patrolHalfExtents").vector2Value,
                        loops,
                        out float worstOvershoot),
                    "scene path stays inside the patrol bounds: worst excursion " +
                    worstOvershoot.ToString("F2") + " m past the box");
            }

            Debug.Log(report.ToString());
            return failures;
        }

        /// <summary>
        /// Walks the authored path on paper -- instant turns, monster_sim_actor's
        /// 2.5 m/s -- and reports how far outside the patrol box it asks the
        /// subject to go. A looping path is replayed so the per-lap residue from
        /// Forward steps re-anchoring shows up.
        ///
        /// The real body yaws at 45 deg/s, so it swings about 3.2 m wider than
        /// this at every reversal; the box is authored with that much slack, and
        /// the runtime guard turns the subject back if it is not enough.
        /// </summary>
        private static bool StaysInsidePatrolBounds(
            NetworkLocomotionPathScript path,
            Vector3 spawn,
            Vector2 halfExtents,
            bool loops,
            out float worstOvershoot)
        {
            const float MoveSpeedMetersPerSecond = 2.5f;
            const int TickRateHz = 30;
            const int Laps = 5;

            worstOvershoot = 0f;
            if (halfExtents.x <= 0f && halfExtents.y <= 0f)
            {
                return true;
            }

            float step = MoveSpeedMetersPerSecond / TickRateHz;
            var position = new Vector3(spawn.x, 0f, spawn.z);
            int maxTicks = 60 * TickRateHz * (loops ? Laps : 1);
            for (int tick = 0; tick < maxTicks; ++tick)
            {
                if (path.Finished)
                {
                    if (!loops)
                    {
                        break;
                    }
                    path.Restart();
                }

                Vector2 move = path.MoveInput(position);
                position += new Vector3(move.x, 0f, move.y) * step;
                if (halfExtents.x > 0f)
                {
                    worstOvershoot = Mathf.Max(
                        worstOvershoot,
                        Mathf.Abs(position.x) - halfExtents.x);
                }
                if (halfExtents.y > 0f)
                {
                    worstOvershoot = Mathf.Max(
                        worstOvershoot,
                        Mathf.Abs(position.z) - halfExtents.y);
                }
            }

            path.Restart();
            return worstOvershoot <= 0f;
        }

        private static void CheckRigPrefab(
            string prefabPath,
            uint skeletonAssetId,
            ulong skeletonContentHash,
            int expectedBoneCount,
            System.Action<bool, string> check)
        {
            string label = System.IO.Path.GetFileNameWithoutExtension(prefabPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                check(false, label + ": prefab not found at " + prefabPath);
                return;
            }

            var binding = prefab.GetComponentInChildren<KernelSkeletonBinding>(true);
            if (binding == null)
            {
                check(false, label + ": KernelSkeletonBinding is missing");
                return;
            }
            if (prefab.GetComponentInChildren<KernelSkeletonPoseApplicator>(true) == null)
            {
                check(false, label + ": KernelSkeletonPoseApplicator is missing");
                return;
            }

            bool valid = binding.TryValidate(out string bindingError);
            check(
                valid &&
                binding.SkeletonAssetId == skeletonAssetId &&
                binding.SkeletonContentHash == skeletonContentHash &&
                binding.Bones.Length == expectedBoneCount,
                label + ": binds " + expectedBoneCount + " bones to skeleton asset " +
                skeletonAssetId + (valid ? string.Empty : " -- " + bindingError));

            // The skeleton glTF sources declare no materials, so an unpatched import
            // yields a rig that is present in the hierarchy but never drawn.
            int renderers = 0;
            int unlitSlots = 0;
            foreach (Renderer renderer in
                     prefab.GetComponentsInChildren<Renderer>(true))
            {
                ++renderers;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                {
                    ++unlitSlots;
                    continue;
                }
                for (int index = 0; index < materials.Length; ++index)
                {
                    if (materials[index] == null)
                    {
                        ++unlitSlots;
                    }
                }
            }
            check(
                renderers > 0 && unlitSlots == 0,
                label + ": " + renderers + " renderer(s), " + unlitSlots +
                " with no material");
        }

        private static int Verify(
            int samples,
            int tickRate,
            string pathText,
            Vector3 spawn,
            uint entityTemplateId)
        {
            var report = new StringBuilder();
            int failures = 0;
            void Check(bool ok, string label)
            {
                report.AppendLine("  [" + (ok ? "PASS" : "FAIL") + "] " + label);
                if (!ok)
                {
                    ++failures;
                }
            }

            report.AppendLine("Unity locomotion verification");
            report.AppendLine("  kernel ABI expected by plugin: " + KernelConstants.AbiVersion);

            if (!NetworkLocomotionPathScript.TryParse(
                    pathText,
                    tickRate,
                    out NetworkLocomotionPathScript path,
                    out string pathError))
            {
                Debug.LogError("path parse failed: " + pathError);
                return 1;
            }
            report.AppendLine("  path: " + path.Description);

            if (!NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundle, out _))
            {
                Debug.LogError("gameplay catalog bundle not found");
                return 1;
            }
            report.AppendLine("  bundle bytes: " + bundle.Length);

            // The generated rig is built from the bone layout in these same bytes.
            if (!NetworkSkeletonManifests.TryLoad(bundle, out string manifestError))
            {
                Debug.LogError("skeleton manifests not readable: " + manifestError);
                return 1;
            }

            KernelConfig config = KernelConfig.CreateDefault(KernelMode.ListenServer);
            config.tick.server_tick_rate = (uint)tickRate;
            config.tick.snapshot_rate = (uint)(tickRate / 2);

            var host = new NetworkHost();
            GameObject rig = null;
            GameObject catalogRig = null;
            string catalogRigError = null;
            try
            {
                bool started = host.Start(
                    port: 7799,
                    config: config,
                    catalog: new GameplayCatalogServerOptions
                    {
                        BundleBytes = bundle,
                        EntryPath = "monster_observer_gameplay_catalog.yaml",
                        ContentNamespace = "monster_observer",
                    },
                    loadResult: out KernelGameplayCatalogLoadResult loadResult);
                report.AppendLine(
                    "  catalog: " + NetworkGameplayCatalogBundle.FormatLoadResult(loadResult));
                Check(started, "listen host started with monster_observer catalog");
                if (!started)
                {
                    Debug.Log(report.ToString());
                    return failures;
                }

                rig = NetworkMonsterSimRigFactory.TryCreate(
                    "MonsterSimRig",
                    NetworkMonsterSimRigFactory.DefaultMarkerDiameter,
                    NetworkMonsterSimRigFactory.DefaultLinkThickness,
                    null,
                    out string rigError);
                Check(rig != null, "generated rig built" + (rig != null ? string.Empty : ": " + rigError));
                if (rig == null)
                {
                    Debug.Log(report.ToString());
                    return failures;
                }

                // The generated rig writes the native local transforms verbatim, so
                // its FK is the native pose by construction. The catalog prefab is
                // held against it bone for bone: an FBX whose hierarchy carries an
                // extra node above SIM_Root, or whose bind pose disagrees with the
                // ozz skeleton, shows up here as world-space drift.
                catalogRig = LoadCatalogRig(out catalogRigError);
                var binding = rig.GetComponent<KernelSkeletonBinding>();
                var applicator = rig.GetComponent<KernelSkeletonPoseApplicator>();
                string bindingError = "KernelSkeletonBinding is missing";
                bool bindingValid = binding != null && binding.TryValidate(out bindingError);
                Check(
                    bindingValid,
                    "generated rig binds " + (binding != null ? binding.Bones.Length : 0) +
                    " bones" + (bindingValid ? string.Empty : ": " + bindingError));

                var events = new KernelEvent[256];
                var renderStates = new RenderEntityState[256];
                var skeletonStates = new SkeletonRenderStateBuffer(4, 128);

                uint subjectNetId = 0;
                uint inputSeq = ScriptedInputSeqBase;
                Vector3 subjectPosition = spawn;
                float fixedDelta = 1f / tickRate;

                int recorded = 0;
                bool hasFirst = false;
                Vector3 first = Vector3.zero;
                Vector3 last = Vector3.zero;
                double pathLength = 0.0;
                uint boneCount = 0;
                int poseApplied = 0;
                int nonFinite = 0;
                float worstCatalogDelta = 0f;
                string firstApplyError = null;
                int maxTicks = samples + 20 * tickRate;

                for (int tick = 0; tick < maxTicks && recorded < samples; ++tick)
                {
                    host.Update(fixedDelta, events);

                    if (subjectNetId == 0)
                    {
                        var createInfo = new KernelServerEntityCreateInfo
                        {
                            owner_peer = 0u,
                            position = new KernelVec3(spawn.x, spawn.y, spawn.z),
                            rotation = new KernelQuat(0f, 0f, 0f, 1f),
                            entity_template_id = entityTemplateId,
                        };
                        if (!host.Kernel.ServerCreateEntity(createInfo, out subjectNetId) ||
                            subjectNetId == 0)
                        {
                            Check(false, "ServerCreateEntity for template " + entityTemplateId);
                            break;
                        }
                        report.AppendLine("  subject net_id: " + subjectNetId);
                    }

                    if (host.Kernel.ServerGetEntityState(
                            subjectNetId,
                            out KernelServerEntityState entityState) &&
                        entityState.valid != 0u)
                    {
                        subjectPosition = new Vector3(
                            entityState.position.x,
                            entityState.position.y,
                            entityState.position.z);
                    }

                    Vector2 move = path.MoveInput(subjectPosition);
                    host.Kernel.ServerSubmitEntityInput(
                        subjectNetId,
                        new KernelPlayerInput
                        {
                            input_seq = inputSeq++,
                            move = new KernelVec2(move.x, move.y),
                        });

                    uint stateCount = host.GetRenderStates(renderStates);
                    host.GetSkeletonRenderStates(skeletonStates);

                    int safeCount = stateCount > (uint)renderStates.Length
                        ? renderStates.Length
                        : (int)stateCount;
                    bool found = false;
                    RenderEntityState subject = default;
                    for (int index = 0; index < safeCount; ++index)
                    {
                        if (renderStates[index].net_id == subjectNetId)
                        {
                            subject = renderStates[index];
                            found = true;
                            break;
                        }
                    }
                    if (!found ||
                        skeletonStates.Result.status !=
                        KernelConstants.SkeletonRenderStatusSuccess)
                    {
                        continue;
                    }

                    int skeletonCount =
                        skeletonStates.StateCount > (uint)skeletonStates.States.Length
                            ? skeletonStates.States.Length
                            : (int)skeletonStates.StateCount;
                    bool posed = false;
                    for (int index = 0; index < skeletonCount; ++index)
                    {
                        KernelSkeletonRenderState state = skeletonStates.States[index];
                        if (state.entity_net_id != subjectNetId)
                        {
                            continue;
                        }
                        boneCount = state.bone_count;
                        if (applicator.TryApply(state, skeletonStates, out string applyError))
                        {
                            ++poseApplied;
                            posed = true;
                            if (catalogRig != null)
                            {
                                rig.transform.SetPositionAndRotation(
                                    new Vector3(
                                        subject.position.x,
                                        subject.position.y,
                                        subject.position.z),
                                    Quaternion.identity);
                                catalogRig.transform.SetPositionAndRotation(
                                    rig.transform.position,
                                    rig.transform.rotation);
                                if (catalogRig
                                        .GetComponent<KernelSkeletonPoseApplicator>()
                                        .TryApply(state, skeletonStates, out string _))
                                {
                                    worstCatalogDelta = Mathf.Max(
                                        worstCatalogDelta,
                                        WorstBoneDelta(rig, catalogRig));
                                }
                            }
                        }
                        else if (firstApplyError == null)
                        {
                            firstApplyError = applyError;
                        }
                        break;
                    }
                    if (!posed)
                    {
                        continue;
                    }

                    var position = new Vector3(
                        subject.position.x,
                        subject.position.y,
                        subject.position.z);
                    if (!IsFinite(position))
                    {
                        ++nonFinite;
                    }
                    if (!hasFirst)
                    {
                        hasFirst = true;
                        first = position;
                    }
                    else
                    {
                        // Planar, to match the native capture analyzer's path length.
                        pathLength += new Vector2(
                            position.x - last.x,
                            position.z - last.z).magnitude;
                    }
                    last = position;
                    ++recorded;
                }

                Vector3 displacement = last - first;
                float planar = new Vector2(displacement.x, displacement.z).magnitude;
                report.AppendLine("  samples:      " + recorded + " (requested " + samples + ")");
                report.AppendLine("  start:        " + first.ToString("F3"));
                report.AppendLine("  end:          " + last.ToString("F3"));
                report.AppendLine(
                    "  displacement: dX " + displacement.x.ToString("F3") +
                    "  dZ " + displacement.z.ToString("F3") +
                    "  planar " + planar.ToString("F3"));
                report.AppendLine("  path length:  " + pathLength.ToString("F3"));
                report.AppendLine("  bone count:   " + boneCount);
                report.AppendLine("  poses applied:" + poseApplied);
                if (firstApplyError != null)
                {
                    report.AppendLine("  apply error:  " + firstApplyError);
                }

                Check(recorded == samples, "sample count: " + recorded + " recorded");
                Check(
                    binding != null && boneCount == binding.Bones.Length,
                    "bone count per sample: " + boneCount);
                Check(poseApplied == recorded, "skeleton pose applied on every sample");
                Check(nonFinite == 0, "root values finite: " + nonFinite + " bad sample(s)");
                Check(displacement.x > 20f, "moved along +X: dX " + displacement.x.ToString("F3"));
                Check(
                    Mathf.Abs(displacement.z) < 1f,
                    "stayed on the +X lane: |dZ| " + Mathf.Abs(displacement.z).ToString("F3"));
                Check(RigIsPosed(rig), "generated rig has a non-identity pose");
                Check(
                    catalogRig != null,
                    "catalog rig prefab loaded" +
                    (catalogRig != null ? string.Empty : ": " + catalogRigError));
                if (catalogRig != null)
                {
                    Check(
                        worstCatalogDelta < 0.001f,
                        "catalog prefab matches the native pose: worst bone delta " +
                        worstCatalogDelta.ToString("F4") + " m");
                }
            }
            finally
            {
                host.Dispose();
                if (rig != null)
                {
                    UnityEngine.Object.DestroyImmediate(rig);
                }
                if (catalogRig != null)
                {
                    UnityEngine.Object.DestroyImmediate(catalogRig);
                }
            }

            Debug.Log(report.ToString());
            return failures;
        }

        private static GameObject LoadCatalogRig(out string error)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                NetworkActorRigPrefabBuilder.MonsterPrefabPath);
            if (prefab == null)
            {
                error = "prefab not found: " +
                    NetworkActorRigPrefabBuilder.MonsterPrefabPath;
                return null;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (instance.GetComponent<KernelSkeletonPoseApplicator>() == null)
            {
                error = "prefab has no KernelSkeletonPoseApplicator";
                UnityEngine.Object.DestroyImmediate(instance);
                return null;
            }

            error = null;
            return instance;
        }

        private static float WorstBoneDelta(GameObject a, GameObject b)
        {
            Transform[] left = a.GetComponent<KernelSkeletonBinding>().Bones;
            Transform[] right = b.GetComponent<KernelSkeletonBinding>().Bones;
            float worst = 0f;
            for (int index = 0; index < left.Length && index < right.Length; ++index)
            {
                worst = Mathf.Max(
                    worst,
                    Vector3.Distance(left[index].position, right[index].position));
            }
            return worst;
        }

        private static bool RigIsPosed(GameObject rig)
        {
            var binding = rig.GetComponent<KernelSkeletonBinding>();
            if (binding == null || binding.Bones == null)
            {
                return false;
            }
            for (int index = 0; index < binding.Bones.Length; ++index)
            {
                Transform bone = binding.Bones[index];
                if (bone.localPosition.sqrMagnitude > 1e-6f)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                   !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                   !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
