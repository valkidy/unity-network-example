using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Renders blast_cut's OBJ output inside a Unity project clone, assembled and
// exploded, with Blast's cut faces (material 1000) in a flat brown so they can be
// told apart from the model's own surface. Copy into Assets/Editor of a
// throwaway project clone; it is not meant to live in the real project.
public static class BlastSpikeView
{

    /// <summary>
    /// -executeMethod BlastSpikeView.Run -blastObj body_voronoi8,body_slice_noise [-blastData dir]
    /// </summary>
    public static void Run()
    {
        int code = 0;
        try
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string data = Argument("-blastData") ?? Path.Combine(projectRoot, "Tools", "BlastSpike", "data");
            string shots = Path.Combine(projectRoot, "shots");
            Directory.CreateDirectory(shots);
            foreach (string name in (Argument("-blastObj") ?? "body_voronoi8").Split(','))
            {
                Camera cam = Stage();
                GameObject built = Build(Path.Combine(data, name + ".obj"), out int chunks, out int hugeTris);
                Debug.Log(name + ": chunks=" + chunks + " trianglesOver2m2=" + hugeTris);
                Shoot(cam, "blast_" + name + "_assembled.png");
                Explode(built.transform.GetChild(0), 0.9f);
                Shoot(cam, "blast_" + name + "_exploded.png");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError(e);
            code = 1;
        }

        EditorApplication.Exit(code);
    }

    private static Camera Stage()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.37f, 0.42f);
        var lightGo = new GameObject("Sun");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.5f;
        lightGo.transform.rotation = Quaternion.Euler(45f, 35f, 0f);
        var camGo = new GameObject("Cam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        cam.fieldOfView = 40f;
        camGo.transform.position = new Vector3(13f, 8f, 13f);
        camGo.transform.LookAt(new Vector3(0f, 4.5f, 0f));
        return cam;
    }

    private static GameObject Build(string path, out int chunkCount, out int hugeTriangles)
    {
        var atlas = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Props/tower/tower.prefab").GetComponentInChildren<MeshRenderer>().sharedMaterial;
        var interior = new Material(atlas) { name = "Interior", color = new Color(0.35f, 0.2f, 0.12f), mainTexture = null };
        var root = new GameObject("blast");
        var node = new GameObject("geometry");
        node.transform.SetParent(root.transform, false);
        node.transform.localRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);
        node.transform.localPosition = new Vector3(0f, -1.51f, 0f);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var groups = new List<(string name, List<int> ext, List<int> inn)>();
        List<int> current = null;
        hugeTriangles = 0;
        foreach (string line in File.ReadLines(path))
        {
            string[] p = line.Split(' ');
            switch (p[0])
            {
                case "v": positions.Add(V3(p)); break;
                case "vn": normals.Add(V3(p)); break;
                case "vt": uvs.Add(new Vector2(F(p[1]), F(p[2]))); break;
                case "g": groups.Add((p[1], new List<int>(), new List<int>())); current = groups[groups.Count - 1].ext; break;
                case "usemtl": current = p[1] == "interior" ? groups[groups.Count - 1].inn : groups[groups.Count - 1].ext; break;
                case "f":
                    for (int k = 1; k <= 3; ++k) current.Add(int.Parse(p[k].Split('/')[0]) - 1);
                    int n = current.Count;
                    Vector3 a = positions[current[n - 3]], b = positions[current[n - 2]], c = positions[current[n - 1]];
                    if (Vector3.Cross(b - a, c - a).magnitude * 0.5f > 2f) ++hugeTriangles;
                    break;
            }
        }

        foreach (var g in groups)
        {
            var map = new Dictionary<int, int>();
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            int[] Remap(List<int> src)
            {
                var dst = new int[src.Count];
                for (int i = 0; i < src.Count; ++i)
                {
                    if (!map.TryGetValue(src[i], out int m)) { m = pos.Count; map[src[i]] = m; pos.Add(positions[src[i]]); nrm.Add(normals[src[i]]); uv.Add(uvs[src[i]]); }
                    dst[i] = m;
                }
                return dst;
            }
            int[] ext = Remap(g.ext); int[] inn = Remap(g.inn);
            var bounds = new Bounds(pos[0], Vector3.zero);
            foreach (Vector3 q in pos) bounds.Encapsulate(q);
            for (int i = 0; i < pos.Count; ++i) pos[i] -= bounds.center;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetUVs(0, uv);
            mesh.subMeshCount = 2; mesh.SetTriangles(ext, 0); mesh.SetTriangles(inn, 1);
            mesh.RecalculateBounds();
            var chunk = new GameObject(g.name);
            chunk.transform.SetParent(node.transform, false);
            chunk.transform.localPosition = bounds.center;
            chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
            chunk.AddComponent<MeshRenderer>().sharedMaterials = new[] { atlas, interior };
        }

        chunkCount = groups.Count;
        return root;
    }

    private static void Explode(Transform node, float distance)
    {
        var bounds = new Bounds(node.GetChild(0).localPosition, Vector3.zero);
        for (int i = 1; i < node.childCount; ++i) bounds.Encapsulate(node.GetChild(i).localPosition);
        var random = new System.Random(3);
        for (int i = 0; i < node.childCount; ++i)
        {
            Transform piece = node.GetChild(i);
            Vector3 away = piece.localPosition - bounds.center;
            piece.localPosition += away.normalized * distance;
            piece.localRotation = Quaternion.Euler(random.Next(-25, 25), random.Next(-25, 25), random.Next(-25, 25));
        }
    }

    private static string Argument(string name)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private static Vector3 V3(string[] p) => new Vector3(F(p[1]), F(p[2]), F(p[3]));

    private static void Shoot(Camera cam, string name)
    {
        var rt = new RenderTexture(1300, 950, 24, RenderTextureFormat.ARGB32);
        var request = new RenderPipeline.StandardRequest { destination = rt };
        RenderPipeline.SubmitRenderRequest(cam, request);
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0f, 0f, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "shots", name);
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Debug.Log("Wrote " + name);
    }
}
