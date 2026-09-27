using System;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Forwards what the native kernel logs to the Unity Console, and so to
    /// Editor.log and Player.log. The kernel writes through spdlog to stdout,
    /// which a Unity Editor never shows: a client-side failure such as a
    /// prediction session ending (Error code 31) left only its code here, while
    /// the line that named the cause went nowhere.
    ///
    /// Installs itself before the first scene loads, because the kernel only
    /// captures from <see cref="KernelLog.StartCapture"/> on. Does nothing
    /// against a native library without the log capture capability.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class KernelLogBridge : MonoBehaviour
    {
        /// <summary>Lines below this level are counted but not shown.</summary>
        public static KernelLogLevel MinimumLevel = KernelLogLevel.Info;

        // The kernel keeps at most 1024 lines; one frame drains all of them.
        private const int MaxLinesPerFrame = 1024;
        private const string Prefix = "[kernel] ";

        private static KernelLogBridge instance;

        private readonly KernelLogLine[] lines = new KernelLogLine[64];
        private ulong nextSequence;
        private bool sequenceKnown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (instance != null || !StartCapture())
            {
                return;
            }

            var host = new GameObject(nameof(KernelLogBridge));
            host.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(host);
            instance = host.AddComponent<KernelLogBridge>();
        }

        private static bool StartCapture()
        {
            try
            {
                if (!KernelLog.IsSupported)
                {
                    Debug.Log(
                        "Network kernel does not capture its log; kernel messages stay on its stdout.");
                    return false;
                }

                KernelLog.StartCapture();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Network kernel log capture unavailable: " + exception.Message);
                return false;
            }
        }

        // Late, so lines the kernel wrote during this frame's update show on
        // this frame.
        private void LateUpdate()
        {
            Drain();
        }

        private void OnDestroy()
        {
            Drain();
            if (instance == this)
            {
                instance = null;
            }
        }

        private void Drain()
        {
            for (int drained = 0; drained < MaxLinesPerFrame; )
            {
                int count = KernelLog.Poll(lines);
                for (int index = 0; index < count; ++index)
                {
                    Forward(lines[index]);
                }

                drained += count;
                if (count < lines.Length)
                {
                    return;
                }
            }
        }

        private void Forward(KernelLogLine entry)
        {
            if (sequenceKnown && entry.Sequence > nextSequence)
            {
                Debug.LogWarning(
                    Prefix + (entry.Sequence - nextSequence) +
                    " log lines were dropped before this Unity frame could read them.");
            }

            sequenceKnown = true;
            nextSequence = entry.Sequence + 1;
            if (entry.Level < MinimumLevel)
            {
                return;
            }

            string line = Prefix + entry.Text;
            if (entry.Truncated)
            {
                line += " [truncated]";
            }

            if (entry.Level >= KernelLogLevel.Error)
            {
                Debug.LogError(line);
            }
            else if (entry.Level == KernelLogLevel.Warn)
            {
                Debug.LogWarning(line);
            }
            else
            {
                Debug.Log(line);
            }
        }
    }
}
