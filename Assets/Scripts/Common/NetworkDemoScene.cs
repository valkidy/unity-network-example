using NetworkExample.UnityDemo.CameraSystem;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    public static class NetworkDemoScene
    {
        public static Transform EnsureEntityRoot(string name)
        {
            GameObject existing = GameObject.Find(name);
            if (existing != null)
            {
                return existing.transform;
            }

            GameObject root = new GameObject(name);
            return root.transform;
        }

        public static ThirdPersonFollowCamera EnsureDefaultView()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            camera.clearFlags = CameraClearFlags.Skybox;
            ThirdPersonFollowCamera followCamera =
                camera.GetComponent<ThirdPersonFollowCamera>();
            if (followCamera == null)
            {
                followCamera = camera.gameObject.AddComponent<ThirdPersonFollowCamera>();
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                GameObject lightObject = new GameObject("Directional Light");
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            return followCamera;
        }
    }
}
