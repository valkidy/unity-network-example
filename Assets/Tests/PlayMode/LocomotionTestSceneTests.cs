using System.Collections;
using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Common;
using NetworkExample.UnityDemo.Host;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.PlayMode
{
    public sealed class LocomotionTestSceneTests
    {
        [UnityTest]
        public IEnumerator LocomotionTestScene_PosesTheKernelSkeletonAndWalksAlongX()
        {
            SceneManager.LoadScene("LocomotionTest", LoadSceneMode.Single);
            yield return null;

            NetworkKernelMonsterObserverBehaviour observer =
                Object.FindAnyObjectByType<NetworkKernelMonsterObserverBehaviour>();
            Assert.That(observer, Is.Not.Null, "observer behaviour missing from scene");

            // Wait for the rig to be instantiated, then snapshot a leg bone so the
            // test can prove the kernel is actually animating it, not just that the
            // prefab shipped a bind pose.
            // Bound by wall clock, not frames: the scene ticks the kernel off real
            // time, and batchmode renders far more frames per second than 30 Hz.
            KernelSkeletonBinding earlyBinding = null;
            float spawnDeadline = Time.realtimeSinceStartup + 30f;
            while (earlyBinding == null && Time.realtimeSinceStartup < spawnDeadline)
            {
                earlyBinding = Object.FindAnyObjectByType<KernelSkeletonBinding>();
                yield return null;
            }
            Assert.That(earlyBinding, Is.Not.Null, "no kernel-bound rig was presented");
            const int KneeBoneIndex = 6; // JNT_LegRearRight_Knee
            Quaternion earlyKnee = earlyBinding.Bones[KneeBoneIndex].localRotation;

            // The scene ticks the kernel off wall-clock time, so 300 samples at
            // 30 Hz take ~10 s; allow headroom for the spawn warm-up.
            float deadline = Time.realtimeSinceStartup + 60f;
            while (observer.RecordedSamples < 300 &&
                   Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(observer.SubjectNetId, Is.Not.Zero, "capture subject never spawned");
            Assert.That(
                observer.RecordedSamples,
                Is.EqualTo(300),
                "capture did not reach 300 samples");

            KernelSkeletonBinding binding =
                Object.FindAnyObjectByType<KernelSkeletonBinding>();
            Assert.That(binding, Is.Not.Null, "no kernel-bound skeleton was presented");
            Assert.That(
                binding.TryValidate(out string bindingError),
                Is.True,
                bindingError);
            Assert.That(
                NetworkSkeletonManifests.TryGetMonsterSim(
                    out KernelSkeletonManifest manifest,
                    out string manifestError),
                Is.True,
                manifestError);
            Assert.That(binding.Bones.Length, Is.EqualTo(manifest.BoneCount));

            Assert.That(
                binding.SkeletonAssetId,
                Is.EqualTo(manifest.AssetId),
                "presented rig is not bound to " +
                NetworkSkeletonManifests.MonsterSimSkeletonName);

            Transform rigRoot = binding.transform;
            Assert.That(
                rigRoot.position.x,
                Is.GreaterThan(20f),
                "presented rig did not travel along +X");
            Assert.That(
                Mathf.Abs(rigRoot.position.z),
                Is.LessThan(1f),
                "presented rig drifted off the +X lane");

            float kneeSwingDegrees = Quaternion.Angle(
                earlyKnee,
                binding.Bones[KneeBoneIndex].localRotation);
            Assert.That(
                kneeSwingDegrees,
                Is.GreaterThan(1f),
                "kernel never animated the leg bones (knee moved " +
                kneeSwingDegrees.ToString("F3") + " deg)");

            Debug.Log(
                "PlayMode rig root: " + rigRoot.position.ToString("F3") +
                " samples=" + observer.RecordedSamples);
        }
    }
}
