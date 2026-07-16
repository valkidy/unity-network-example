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

        public static void EnsureDefaultView()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            camera.transform.SetPositionAndRotation(
                new Vector3(0f, 10f, -12f),
                Quaternion.Euler(55f, 0f, 0f));
            camera.fieldOfView = 60f;
            camera.clearFlags = CameraClearFlags.Skybox;

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                GameObject lightObject = new GameObject("Directional Light");
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
        }
    }
}
