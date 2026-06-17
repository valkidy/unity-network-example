using System.Text;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Immediate-mode visual debugger overlay. Draws collider shapes, entity facing
    /// directions and a ground grid with Unity <see cref="GL"/> (so it renders in the
    /// Game view and in standalone builds), and a stats panel via <see cref="OnGUI"/>.
    /// Both the client and host runners feed it each frame through <see cref="Capture"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkDebugView : MonoBehaviour
    {
        [SerializeField]
        private bool enableVisualDebug = true;

        [SerializeField]
        private bool drawGrid = true;

        [SerializeField]
        private bool drawColliders = true;

        [SerializeField]
        private bool drawDirections = true;

        [SerializeField]
        private bool drawStats = true;

        [SerializeField]
        private float directionLength = 1.0f;

        [SerializeField]
        private int maxColliderShapes = 512;

        [SerializeField]
        private int gridHalfExtent = 16;

        [Header("Entity type colors")]
        [SerializeField]
        private Color playerColor = Color.green;

        [SerializeField]
        private Color enemyColor = Color.red;

        [SerializeField]
        private Color projectileColor = Color.yellow;

        [SerializeField]
        private Color areaEffectColor = Color.cyan;

        [SerializeField]
        private Color beamColor = Color.magenta;

        [SerializeField]
        private Color unknownColor = Color.gray;

        [SerializeField]
        private Color gridColor = new Color(0.3f, 0.3f, 0.3f, 1f);

        private Material lineMaterial;
        private GUIStyle statsStyle;
        private readonly StringBuilder statsBuilder = new StringBuilder(256);

        private RenderEntityState[] renderStates;
        private int renderStateCount;
        private KernelColliderShapeView[] colliderShapes;
        private int colliderShapeCount;
        private KernelNetworkStats networkStats;
        private bool hasNetworkStats;

        public void SetEnabled(bool value)
        {
            enableVisualDebug = value;
        }

        /// <summary>
        /// Captures the current frame's render states and queries collider shapes and
        /// network stats from the kernel. Called from the runner's Update.
        /// </summary>
        public void Capture(NetworkExample.Kernel.Kernel kernel, RenderEntityState[] states, int count)
        {
            renderStates = states;
            renderStateCount = count;

            if (kernel == null || !enableVisualDebug)
            {
                colliderShapeCount = 0;
                hasNetworkStats = false;
                return;
            }

            if (colliderShapes == null || colliderShapes.Length != Mathf.Max(1, maxColliderShapes))
            {
                colliderShapes = new KernelColliderShapeView[Mathf.Max(1, maxColliderShapes)];
            }

            uint shapeCount = kernel.QueryColliderShapes(null, colliderShapes);
            colliderShapeCount = shapeCount > (uint)colliderShapes.Length
                ? colliderShapes.Length
                : (int)shapeCount;

            hasNetworkStats = kernel.TryGetNetworkStats(out networkStats);
        }

        private void OnRenderObject()
        {
            if (!enableVisualDebug)
            {
                return;
            }

            EnsureLineMaterial();
            lineMaterial.SetPass(0);

            // Draw in world space: keep the active camera view/projection and pin the
            // model matrix to identity (ignore this GameObject's transform).
            GL.PushMatrix();
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);

            if (drawGrid)
            {
                DrawGrid();
            }

            if (drawColliders && colliderShapes != null)
            {
                for (int index = 0; index < colliderShapeCount; ++index)
                {
                    DrawColliderShape(colliderShapes[index]);
                }
            }

            if (drawDirections && renderStates != null)
            {
                for (int index = 0; index < renderStateCount; ++index)
                {
                    DrawDirection(renderStates[index]);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        private void OnGUI()
        {
            if (!enableVisualDebug || !drawStats)
            {
                return;
            }

            EnsureStatsStyle();

            int players = 0;
            int enemies = 0;
            int projectiles = 0;
            int areaEffects = 0;
            int beams = 0;
            int others = 0;
            if (renderStates != null)
            {
                for (int index = 0; index < renderStateCount; ++index)
                {
                    switch (renderStates[index].entity_type)
                    {
                        case KernelEntityType.Player:
                            players++;
                            break;
                        case KernelEntityType.Enemy:
                            enemies++;
                            break;
                        case KernelEntityType.Projectile:
                            projectiles++;
                            break;
                        case KernelEntityType.AreaEffect:
                            areaEffects++;
                            break;
                        case KernelEntityType.Beam:
                            beams++;
                            break;
                        default:
                            others++;
                            break;
                    }
                }
            }

            statsBuilder.Clear();
            if (hasNetworkStats)
            {
                statsBuilder
                    .Append("Ping ")
                    .Append((networkStats.rtt_us / 1000f).ToString("0.0"))
                    .Append(" ms\n");
                statsBuilder
                    .Append("Loss ")
                    .Append((networkStats.loss_ratio * 100f).ToString("0.0"))
                    .Append("%  Jitter ")
                    .Append((networkStats.jitter_us / 1000f).ToString("0.0"))
                    .Append(" ms\n");
                statsBuilder
                    .Append("Pkts ")
                    .Append(networkStats.packet_count_sent)
                    .Append("  avg ")
                    .Append(networkStats.average_packet_size)
                    .Append("B  max ")
                    .Append(networkStats.max_packet_size)
                    .Append("B\n");
            }
            else
            {
                statsBuilder.Append("Network stats unavailable\n");
            }

            statsBuilder
                .Append("Pop  P:")
                .Append(players)
                .Append(" E:")
                .Append(enemies)
                .Append(" Proj:")
                .Append(projectiles);
            if (areaEffects > 0 || beams > 0 || others > 0)
            {
                statsBuilder
                    .Append(" Area:")
                    .Append(areaEffects)
                    .Append(" Beam:")
                    .Append(beams)
                    .Append(" Other:")
                    .Append(others);
            }

            const float width = 320f;
            const float height = 110f;
            const float margin = 10f;
            Rect rect = new Rect(Screen.width - width - margin, margin, width, height);
            GUI.Label(rect, statsBuilder.ToString(), statsStyle);
        }

        private void OnDestroy()
        {
            if (lineMaterial != null)
            {
                Destroy(lineMaterial);
                lineMaterial = null;
            }
        }

        private void DrawGrid()
        {
            GL.Color(gridColor);
            int extent = Mathf.Max(1, gridHalfExtent);
            for (int line = -extent; line <= extent; ++line)
            {
                GL.Vertex3(line, 0f, -extent);
                GL.Vertex3(line, 0f, extent);
                GL.Vertex3(-extent, 0f, line);
                GL.Vertex3(extent, 0f, line);
            }
        }

        private void DrawColliderShape(KernelColliderShapeView shape)
        {
            GL.Color(ColorForType((KernelEntityType)shape.entity_type));
            Vector3 center = ToVector3(shape.world_center);

            switch ((KernelColliderShapeType)shape.shape_type)
            {
                case KernelColliderShapeType.Sphere:
                    DrawWireSphere(center, shape.radius);
                    break;
                case KernelColliderShapeType.Aabb:
                    DrawAxisAlignedBox(center, ToVector3(shape.half_extents));
                    break;
                case KernelColliderShapeType.OrientedBox:
                    DrawOrientedBox(center, ToVector3(shape.half_extents), ToQuaternion(shape.world_rotation));
                    break;
                case KernelColliderShapeType.Segment:
                    Line(ToVector3(shape.segment_start), ToVector3(shape.segment_end));
                    if (shape.radius > 0f)
                    {
                        DrawWireSphere(center, shape.radius);
                    }
                    break;
            }
        }

        private void DrawDirection(RenderEntityState state)
        {
            if (state.net_id == 0)
            {
                return;
            }

            Vector3 position = ToVector3(state.position);
            Vector3 forward = ToQuaternion(state.rotation) * Vector3.forward;
            GL.Color(BrightColorForType(state.entity_type));
            Line(position, position + forward * directionLength);
        }

        private static void Line(Vector3 a, Vector3 b)
        {
            GL.Vertex(a);
            GL.Vertex(b);
        }

        private static void DrawAxisAlignedBox(Vector3 center, Vector3 halfExtents)
        {
            DrawBoxFromCorners(
                center + new Vector3(-halfExtents.x, -halfExtents.y, -halfExtents.z),
                center + new Vector3(halfExtents.x, -halfExtents.y, -halfExtents.z),
                center + new Vector3(halfExtents.x, -halfExtents.y, halfExtents.z),
                center + new Vector3(-halfExtents.x, -halfExtents.y, halfExtents.z),
                center + new Vector3(-halfExtents.x, halfExtents.y, -halfExtents.z),
                center + new Vector3(halfExtents.x, halfExtents.y, -halfExtents.z),
                center + new Vector3(halfExtents.x, halfExtents.y, halfExtents.z),
                center + new Vector3(-halfExtents.x, halfExtents.y, halfExtents.z));
        }

        private static void DrawOrientedBox(Vector3 center, Vector3 halfExtents, Quaternion rotation)
        {
            DrawBoxFromCorners(
                center + rotation * new Vector3(-halfExtents.x, -halfExtents.y, -halfExtents.z),
                center + rotation * new Vector3(halfExtents.x, -halfExtents.y, -halfExtents.z),
                center + rotation * new Vector3(halfExtents.x, -halfExtents.y, halfExtents.z),
                center + rotation * new Vector3(-halfExtents.x, -halfExtents.y, halfExtents.z),
                center + rotation * new Vector3(-halfExtents.x, halfExtents.y, -halfExtents.z),
                center + rotation * new Vector3(halfExtents.x, halfExtents.y, -halfExtents.z),
                center + rotation * new Vector3(halfExtents.x, halfExtents.y, halfExtents.z),
                center + rotation * new Vector3(-halfExtents.x, halfExtents.y, halfExtents.z));
        }

        private static void DrawBoxFromCorners(
            Vector3 c0, Vector3 c1, Vector3 c2, Vector3 c3,
            Vector3 c4, Vector3 c5, Vector3 c6, Vector3 c7)
        {
            // bottom
            Line(c0, c1);
            Line(c1, c2);
            Line(c2, c3);
            Line(c3, c0);
            // top
            Line(c4, c5);
            Line(c5, c6);
            Line(c6, c7);
            Line(c7, c4);
            // verticals
            Line(c0, c4);
            Line(c1, c5);
            Line(c2, c6);
            Line(c3, c7);
        }

        private static void DrawWireSphere(Vector3 center, float radius)
        {
            const int segments = 16;
            Vector3 prevXy = center + new Vector3(radius, 0f, 0f);
            Vector3 prevXz = center + new Vector3(radius, 0f, 0f);
            Vector3 prevYz = center + new Vector3(0f, radius, 0f);
            for (int step = 1; step <= segments; ++step)
            {
                float angle = step / (float)segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle) * radius;
                float sin = Mathf.Sin(angle) * radius;

                Vector3 xy = center + new Vector3(cos, sin, 0f);
                Vector3 xz = center + new Vector3(cos, 0f, sin);
                Vector3 yz = center + new Vector3(0f, cos, sin);

                Line(prevXy, xy);
                Line(prevXz, xz);
                Line(prevYz, yz);

                prevXy = xy;
                prevXz = xz;
                prevYz = yz;
            }
        }

        private Color ColorForType(KernelEntityType type)
        {
            switch (type)
            {
                case KernelEntityType.Player:
                    return playerColor;
                case KernelEntityType.Enemy:
                    return enemyColor;
                case KernelEntityType.Projectile:
                    return projectileColor;
                case KernelEntityType.AreaEffect:
                    return areaEffectColor;
                case KernelEntityType.Beam:
                    return beamColor;
                default:
                    return unknownColor;
            }
        }

        private Color BrightColorForType(KernelEntityType type)
        {
            Color color = ColorForType(type);
            return Color.Lerp(color, Color.white, 0.4f);
        }

        private void EnsureLineMaterial()
        {
            if (lineMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
            lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        }

        private void EnsureStatsStyle()
        {
            if (statsStyle != null)
            {
                return;
            }

            statsStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperRight,
                fontSize = 14,
                richText = false,
            };
            statsStyle.normal.textColor = Color.white;
        }

        private static Vector3 ToVector3(KernelVec3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static Quaternion ToQuaternion(KernelQuat value)
        {
            if (value.x == 0f && value.y == 0f && value.z == 0f && value.w == 0f)
            {
                return Quaternion.identity;
            }

            return new Quaternion(value.x, value.y, value.z, value.w);
        }
    }
}
