using System.Collections.Generic;
using System.IO;

namespace NetworkExample.UnityDemo.Text3D
{
    /// <summary>
    /// Loads each font file once per process. Safe to call from any thread.
    /// </summary>
    public static class WaxGlyphFontCache
    {
        private static readonly Dictionary<string, TrueTypeFont> Fonts = new Dictionary<string, TrueTypeFont>();

        public static TrueTypeFont Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            lock (Fonts)
            {
                if (Fonts.TryGetValue(fullPath, out TrueTypeFont cached))
                {
                    return cached;
                }
            }

            // Read outside the lock: a large CJK font takes a moment, and a second caller
            // loading the same file at worst parses it twice.
            TrueTypeFont loaded = TrueTypeFont.Load(File.ReadAllBytes(fullPath));
            lock (Fonts)
            {
                if (Fonts.TryGetValue(fullPath, out TrueTypeFont existing))
                {
                    return existing;
                }

                Fonts.Add(fullPath, loaded);
                return loaded;
            }
        }
    }
}
