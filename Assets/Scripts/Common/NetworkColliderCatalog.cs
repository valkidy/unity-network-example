using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Common
{
    /// <summary>
    /// Resolved collider binding for one entity type: the template geometry combined
    /// with the binding's local offset/rotation. Used by the visual debugger to draw
    /// collider shapes straight from the catalog (bundle.bytes), independent of any
    /// runtime kernel collider query.
    /// </summary>
    public struct ColliderBindingInfo
    {
        public KernelColliderShapeType shapeType;
        public Vector3 center;
        public Vector3 halfExtents;
        public float radius;
        public float length;
        public Vector3 localPosition;
        public Quaternion localRotation;
    }

    /// <summary>
    /// Parses collider templates and bindings out of the gameplay catalog bundle
    /// (a zip of YAML files). The bundle is the authoritative source for collider
    /// sizes, matching what the kernel loads.
    /// </summary>
    public static class NetworkColliderCatalog
    {
        private struct TemplateGeom
        {
            public KernelColliderShapeType shapeType;
            public Vector3 center;
            public Vector3 halfExtents;
            public float radius;
            public float length;
        }

        public static Dictionary<KernelEntityType, ColliderBindingInfo> LoadBindings()
        {
            var bindings = new Dictionary<KernelEntityType, ColliderBindingInfo>();
            if (!NetworkGameplayCatalogBundle.TryLoadDefault(out byte[] bundleBytes, out _))
            {
                return bindings;
            }

            string yaml = ExtractColliderYaml(bundleBytes);
            if (string.IsNullOrEmpty(yaml))
            {
                Debug.LogWarning("NetworkColliderCatalog: collider_templates yaml not found in bundle.");
                return bindings;
            }

            Parse(yaml, bindings);
            return bindings;
        }

        private static string ExtractColliderYaml(byte[] bundleBytes)
        {
            using (var stream = new MemoryStream(bundleBytes))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.Replace('\\', '/').Contains("collider_templates/") &&
                        entry.FullName.EndsWith(".yaml"))
                    {
                        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                        {
                            return reader.ReadToEnd();
                        }
                    }
                }
            }

            return null;
        }

        private static void Parse(string yaml, Dictionary<KernelEntityType, ColliderBindingInfo> bindings)
        {
            var templatesByName = new Dictionary<string, TemplateGeom>();
            string section = null;
            var current = new Dictionary<string, string>();

            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine;
                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment);
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (line.Length > 0 && line[0] != ' ' && trimmed.EndsWith(":"))
                {
                    Flush(section, current, templatesByName, bindings);
                    current.Clear();
                    section = trimmed.Substring(0, trimmed.Length - 1).Trim();
                    continue;
                }

                if (trimmed.StartsWith("-"))
                {
                    Flush(section, current, templatesByName, bindings);
                    current.Clear();
                    trimmed = trimmed.Substring(1).Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                }

                int colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = trimmed.Substring(0, colon).Trim();
                string value = trimmed.Substring(colon + 1).Trim();
                current[key] = value;
            }

            Flush(section, current, templatesByName, bindings);
        }

        private static void Flush(
            string section,
            Dictionary<string, string> fields,
            Dictionary<string, TemplateGeom> templatesByName,
            Dictionary<KernelEntityType, ColliderBindingInfo> bindings)
        {
            if (fields.Count == 0 || section == null)
            {
                return;
            }

            if (section == "templates" && fields.TryGetValue("name", out string name))
            {
                templatesByName[name] = new TemplateGeom
                {
                    shapeType = MapShape(GetValue(fields, "shape")),
                    center = ParseVec3(GetValue(fields, "center")),
                    halfExtents = ParseVec3(GetValue(fields, "half_extents")),
                    radius = ParseFloat(GetValue(fields, "radius")),
                    length = ParseFloat(GetValue(fields, "length")),
                };
            }
            else if (section == "bindings" &&
                fields.TryGetValue("entity_type", out string entityTypeName) &&
                fields.TryGetValue("collider_template", out string templateName) &&
                templatesByName.TryGetValue(templateName, out TemplateGeom geom))
            {
                if (TryMapEntityType(entityTypeName, out KernelEntityType entityType))
                {
                    bindings[entityType] = new ColliderBindingInfo
                    {
                        shapeType = geom.shapeType,
                        center = geom.center,
                        halfExtents = geom.halfExtents,
                        radius = geom.radius,
                        length = geom.length,
                        localPosition = ParseVec3(GetValue(fields, "local_position")),
                        localRotation = ParseQuat(GetValue(fields, "local_rotation")),
                    };
                }
            }
        }

        private static string GetValue(Dictionary<string, string> fields, string key)
        {
            return fields.TryGetValue(key, out string value) ? value : null;
        }

        private static KernelColliderShapeType MapShape(string shape)
        {
            switch (shape)
            {
                case "sphere":
                    return KernelColliderShapeType.Sphere;
                case "oriented_box":
                    return KernelColliderShapeType.OrientedBox;
                case "segment":
                    return KernelColliderShapeType.Segment;
                default:
                    return KernelColliderShapeType.Aabb;
            }
        }

        private static bool TryMapEntityType(string name, out KernelEntityType entityType)
        {
            switch (name)
            {
                case "player":
                    entityType = KernelEntityType.Player;
                    return true;
                case "enemy":
                    entityType = KernelEntityType.Enemy;
                    return true;
                case "projectile":
                    entityType = KernelEntityType.Projectile;
                    return true;
                case "area_effect":
                    entityType = KernelEntityType.AreaEffect;
                    return true;
                case "beam":
                    entityType = KernelEntityType.Beam;
                    return true;
                default:
                    entityType = KernelEntityType.Unknown;
                    return false;
            }
        }

        private static Vector3 ParseVec3(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Vector3.zero;
            }

            value = value.Trim().TrimStart('{').TrimEnd('}');
            string[] parts = value.Split(',');
            float x = 0f;
            float y = 0f;
            float z = 0f;
            foreach (string part in parts)
            {
                int colon = part.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = part.Substring(0, colon).Trim();
                float component = ParseFloat(part.Substring(colon + 1));
                if (key == "x")
                {
                    x = component;
                }
                else if (key == "y")
                {
                    y = component;
                }
                else if (key == "z")
                {
                    z = component;
                }
            }

            return new Vector3(x, y, z);
        }

        private static Quaternion ParseQuat(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return Quaternion.identity;
            }

            value = value.Trim().TrimStart('{').TrimEnd('}');
            string[] parts = value.Split(',');
            float x = 0f;
            float y = 0f;
            float z = 0f;
            float w = 1f;
            foreach (string part in parts)
            {
                int colon = part.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = part.Substring(0, colon).Trim();
                float component = ParseFloat(part.Substring(colon + 1));
                switch (key)
                {
                    case "x":
                        x = component;
                        break;
                    case "y":
                        y = component;
                        break;
                    case "z":
                        z = component;
                        break;
                    case "w":
                        w = component;
                        break;
                }
            }

            if (x == 0f && y == 0f && z == 0f && w == 0f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(x, y, z, w);
        }

        private static float ParseFloat(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0f;
            }

            return float.TryParse(
                value.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float result)
                ? result
                : 0f;
        }
    }
}
