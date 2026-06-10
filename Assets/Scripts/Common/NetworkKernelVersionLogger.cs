using System;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    public static class NetworkKernelVersionLogger
    {
        public static void Log()
        {
            try
            {
                KernelAbiInfo kernelInfo = KernelAbi.GetInfo();
                KernelBuildInfo buildInfo = KernelAbi.GetBuildInfo();
                GameServerAbiInfo gameServerInfo = GameServerAbi.GetInfo();
                Debug.Log(
                    "Network kernel package " +
                    NetworkKernelPackageInfo.Name +
                    "@" +
                    NetworkKernelPackageInfo.Version +
                    ": native_version=" +
                    buildInfo.module_version +
                    " git_commit=" +
                    buildInfo.git_commit +
                    " platform=" +
                    buildInfo.build_platform +
                    " config=" +
                    buildInfo.build_config +
                    " kernel_abi=" +
                    kernelInfo.abi_version +
                    " game_server_abi=" +
                    gameServerInfo.abi_version);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Network kernel version info unavailable: " + exception.Message);
            }
        }
    }
}
