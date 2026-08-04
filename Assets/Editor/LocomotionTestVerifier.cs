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

            Debug.Log(report.ToString());
            return failures;
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

            KernelConfig config = KernelConfig.CreateDefault(KernelMode.ListenServer);
            config.tick.server_tick_rate = (uint)tickRate;
            config.tick.snapshot_rate = (uint)(tickRate / 2);

            var host = new NetworkHost();
            GameObject rig = null;
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

                rig = NetworkMonsterSimRigFactory.Create("MonsterSimRig");
                var binding = rig.GetComponent<KernelSkeletonBinding>();
                var applicator = rig.GetComponent<KernelSkeletonPoseApplicator>();
                string bindingError = "KernelSkeletonBinding is missing";
                bool bindingValid = binding != null && binding.TryValidate(out bindingError);
                Check(
                    bindingValid,
                    "generated rig binds " + KernelSkeletonBinding.DefaultBoneCount +
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
                    boneCount == KernelSkeletonBinding.DefaultBoneCount,
                    "bone count per sample: " + boneCount);
                Check(poseApplied == recorded, "skeleton pose applied on every sample");
                Check(nonFinite == 0, "root values finite: " + nonFinite + " bad sample(s)");
                Check(displacement.x > 20f, "moved along +X: dX " + displacement.x.ToString("F3"));
                Check(
                    Mathf.Abs(displacement.z) < 1f,
                    "stayed on the +X lane: |dZ| " + Mathf.Abs(displacement.z).ToString("F3"));
                Check(RigIsPosed(rig), "generated rig has a non-identity pose");
            }
            finally
            {
                host.Dispose();
                if (rig != null)
                {
                    UnityEngine.Object.DestroyImmediate(rig);
                }
            }

            Debug.Log(report.ToString());
            return failures;
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
