using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NetworkExample.UnityDemo.EditorTools
{
    /// <summary>
    /// Exports every image a GLB carries as a 1024x1024 PNG under 2 MB, one ZIP
    /// per GLB, through the Python script in Tools/GlbTextureExtractor.
    /// </summary>
    /// <remarks>
    /// A separate process rather than C#, because the work is Pillow's: resizing,
    /// then re-compressing and reducing colours until a PNG fits. The script runs
    /// in its own .venv under the tool's folder, which Prepare Environment makes,
    /// so nothing is installed into the machine's Python -- see
    /// Tools/GlbTextureExtractor/README.md.
    ///
    /// Input, output and the .venv live under Tools/, outside Assets, so neither
    /// the GLBs nor the ZIPs are imported by Unity.
    /// </remarks>
    public sealed class GlbTextureExtractor : EditorWindow
    {
        /// <summary>Where the tool lives, from the project root.</summary>
        public const string ToolDirectory = "Tools/GlbTextureExtractor";

        private const string MenuPath = "Tools/Network Example/GLB Texture Extractor...";
        private const string Title = "GLB Texture Extractor";
        private const string LogPrefix = "GlbTextureExtractor: ";

        private static bool running;

        private string python;
        private string status = "Place GLB files in input, then click Batch Export.";

        private static string Root =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath), ToolDirectory);

        // Per project, because the .venv it makes is per checkout.
        private static string PythonPreference =>
            "NetworkExample.GlbTextureExtractor.Python." + Application.dataPath;

        private static bool IsWindows => Application.platform == RuntimePlatform.WindowsEditor;

        private static string EnvironmentPython =>
            Path.Combine(Root, IsWindows ? ".venv/Scripts/python.exe" : ".venv/bin/python");

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<GlbTextureExtractor>(Title);
        }

        private void OnEnable()
        {
            python = EditorPrefs.GetString(PythonPreference, IsWindows ? "python" : "python3");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("GLB → 1024×1024 PNG → ZIP", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Exports all GLBs directly inside input. Each PNG is below 2 MB. " +
                    "Successful exports replace matching ZIPs; failed exports keep the previous ZIP.",
                MessageType.Info);
            EditorGUILayout.LabelField("Input", Path.Combine(Root, "input"));
            EditorGUILayout.LabelField("Output", Path.Combine(Root, "output"));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open input"))
                {
                    OpenFolder("input");
                }

                if (GUILayout.Button("Open output"))
                {
                    OpenFolder("output");
                }
            }

            using (new EditorGUI.DisabledScope(running))
            {
                EditorGUI.BeginChangeCheck();
                python = EditorGUILayout.TextField("Python 3.10+ executable", python);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PythonPreference, python);
                }

                EditorGUILayout.HelpBox(
                    "First use: install Python 3.10+ if needed, then click Prepare Environment. " +
                        "This creates a local .venv and downloads Pillow. " +
                        "Use an absolute Python path if Unity cannot find Python.",
                    MessageType.None);
                if (GUILayout.Button("Prepare Environment"))
                {
                    RunOperation(true);
                }

                using (new EditorGUI.DisabledScope(!File.Exists(EnvironmentPython)))
                {
                    if (GUILayout.Button("Batch Export"))
                    {
                        RunOperation(false);
                    }
                }
            }

            EditorGUILayout.HelpBox(running ? "Working… See Console for results." : status, MessageType.None);
        }

        private static void OpenFolder(string name)
        {
            string folder = Path.Combine(Root, name);
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        private async void RunOperation(bool prepare)
        {
            if (running)
            {
                return;
            }

            running = true;
            // A domain reload mid-run would drop the awaiting continuation and
            // leave the window stuck on "Working".
            EditorApplication.LockReloadAssemblies();
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, "input"));
                Directory.CreateDirectory(Path.Combine(Root, "output"));
                if (prepare)
                {
                    await RunProcess(python, "-m venv " + Quote(Path.Combine(Root, ".venv")));
                    await RunProcess(
                        EnvironmentPython,
                        "-m pip install -r " + Quote(Path.Combine(Root, "requirements.txt")));
                    status = "Environment ready. Place GLBs in input and click Batch Export.";
                }
                else
                {
                    await RunProcess(
                        EnvironmentPython,
                        Quote(Path.Combine(Root, "extract_glb_textures.py")) + " --batch");
                    status = "Batch export complete. ZIPs are in output.";
                    OpenFolder("output");
                }

                Debug.Log(LogPrefix + status);
            }
            catch (Exception exception)
            {
                status = "Operation failed. See Console for details. Successful ZIPs remain in output.";
                Debug.LogError(LogPrefix + exception.Message);
            }
            finally
            {
                running = false;
                EditorApplication.UnlockReloadAssemblies();
                if (this != null)
                {
                    Repaint();
                }
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static async Task RunProcess(string executable, string arguments)
        {
            var start = new ProcessStartInfo(executable, arguments)
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                // Both streams read at once: pip can fill one pipe while this
                // waits on the other.
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit());
                string output = await stdout;
                string errors = await stderr;
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Debug.Log(output);
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        executable + " exited with code " + process.ExitCode + ".\n" + errors);
                }

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    Debug.Log(errors);
                }
            }
        }
    }
}
