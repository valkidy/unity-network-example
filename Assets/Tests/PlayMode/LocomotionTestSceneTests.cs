using System.Collections;
using NetworkExample.Kernel;
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
                Object.FindFirstObjectByType<NetworkKernelMonsterObserverBehaviour>();
            Assert.That(observer, Is.Not.Null, "observer behaviour missing from scene");

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
                Object.FindFirstObjectByType<KernelSkeletonBinding>();
            Assert.That(binding, Is.Not.Null, "no kernel-bound skeleton was presented");
            Assert.That(
                binding.TryValidate(out string bindingError),
                Is.True,
                bindingError);
            Assert.That(
                binding.Bones.Length,
                Is.EqualTo(KernelSkeletonBinding.DefaultBoneCount));

            bool posed = false;
            for (int index = 0; index < binding.Bones.Length; ++index)
            {
                if (binding.Bones[index].localPosition.sqrMagnitude > 1e-6f)
                {
                    posed = true;
                    break;
                }
            }
            Assert.That(posed, Is.True, "skeleton bones were never posed by the kernel");

            Transform rigRoot = binding.transform;
            Assert.That(
                rigRoot.position.x,
                Is.GreaterThan(20f),
                "presented rig did not travel along +X");
            Assert.That(
                Mathf.Abs(rigRoot.position.z),
                Is.LessThan(1f),
                "presented rig drifted off the +X lane");

            Debug.Log(
                "PlayMode rig root: " + rigRoot.position.ToString("F3") +
                " samples=" + observer.RecordedSamples);
        }
    }
}
