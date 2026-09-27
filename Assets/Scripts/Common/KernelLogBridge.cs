using System;
using System.Runtime.InteropServices;
using System.Text;
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
    /// captures from its first poll on. Does nothing against a native library
    /// without KERNEL_CAPABILITY_LOG_CAPTURE.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class KernelLogBridge : MonoBehaviour
    {
        // spdlog's order, as KernelLogLevel in kernel_types.h.
        public const int LevelTrace = 0;
        public const int LevelDebug = 1;
        public const int LevelInfo = 2;
        public const int LevelWarn = 3;
        public const int LevelError = 4;

        /// <summary>Lines below this level are counted but not shown.</summary>
        public static int MinimumLevel = LevelInfo;

        private const ulong CapabilityLogCapture = 0x0001000000000000UL;
        // KernelLogMessage: level, length, truncated, reserved (4 B each),
        // sequence (8 B), then the text.
        private const int TextOffset = 24;
        private const int TextSize = 512;
        private const int MessageSize = TextOffset + TextSize;
        private const int BatchSize = 64;
        // The kernel keeps at most 1024 lines; this drains all of them.
        private const int MaxBatchesPerFrame = 16;
        private const string Prefix = "[kernel] ";

        [DllImport("network_kernel", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint Kernel_PollLogMessages(IntPtr outMessages, uint maxMessages);

        private static KernelLogBridge instance;

        private readonly byte[] text = new byte[TextSize];
        private IntPtr buffer;
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
                if ((KernelAbi.GetInfo().capability_flags & CapabilityLogCapture) == 0)
                {
                    Debug.Log(
                        "Network kernel does not capture its log; kernel messages stay on its stdout.");
                    return false;
                }

                // Copies nothing; starts capture.
                Kernel_PollLogMessages(IntPtr.Zero, 0);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Network kernel log capture unavailable: " + exception.Message);
                return false;
            }
        }

        private void Awake()
        {
            buffer = Marshal.AllocHGlobal(MessageSize * BatchSize);
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
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
            }

            if (instance == this)
            {
                instance = null;
            }
        }

        private void Drain()
        {
            if (buffer == IntPtr.Zero)
            {
                return;
            }

            for (int batch = 0; batch < MaxBatchesPerFrame; ++batch)
            {
                int count = (int)Kernel_PollLogMessages(buffer, BatchSize);
                for (int index = 0; index < count; ++index)
                {
                    Forward(IntPtr.Add(buffer, index * MessageSize));
                }

                if (count < BatchSize)
                {
                    return;
                }
            }
        }

        private void Forward(IntPtr message)
        {
            int level = Marshal.ReadInt32(message, 0);
            int length = Mathf.Clamp(Marshal.ReadInt32(message, 4), 0, TextSize - 1);
            bool truncated = Marshal.ReadInt32(message, 8) != 0;
            ulong sequence = (ulong)Marshal.ReadInt64(message, 16);

            if (sequenceKnown && sequence > nextSequence)
            {
                Debug.LogWarning(
                    Prefix + (sequence - nextSequence) +
                    " log lines were dropped before this Unity frame could read them.");
            }

            sequenceKnown = true;
            nextSequence = sequence + 1;
            if (level < MinimumLevel)
            {
                return;
            }

            Marshal.Copy(IntPtr.Add(message, TextOffset), text, 0, length);
            string line = Prefix + Encoding.UTF8.GetString(text, 0, length);
            if (truncated)
            {
                line += " [truncated]";
            }

            if (level >= LevelError)
            {
                Debug.LogError(line);
            }
            else if (level == LevelWarn)
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
