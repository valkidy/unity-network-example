using System.Collections.Generic;
using System.Text;
using NetworkExample.Kernel;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Immediate-mode visual debugger overlay. Draws collider shapes, entity facing
    /// directions, AI agent vision cones and a ground grid with Unity <see cref="GL"/>
    /// (so it renders in the Game view and in standalone builds), and a stats panel via
    /// <see cref="OnGUI"/>. Both the client and host runners feed it each frame through
    /// <see cref="Capture"/>.
    ///
    /// All geometry comes straight from the kernel ABI (package
    /// <c>com.network-example.kernel</c>): collider shapes from
    /// <see cref="Kernel.Kernel.QueryColliderShapes"/>, vision state from
    /// <see cref="Kernel.Kernel.QueryVisionState"/>, and the vision cone template from
    /// <see cref="Kernel.Kernel.GetColliderTemplates"/>. There is no <c>bundle.bytes</c>
    /// re-parsing — if the kernel cannot supply a shape, it is simply not drawn.
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
        private bool drawVision = true;

        [SerializeField]
        private bool drawStats = true;

        [SerializeField]
        private float directionLength = 1.0f;

        [SerializeField]
        private int maxColliderShapes = 512;

        [SerializeField]
        private int maxVisionAgents = 64;

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
        private Color visionColor = new Color(1f, 0.6f, 0.1f, 1f);

        [SerializeField]
        private Color visionTargetColor = new Color(1f, 0.2f, 0.2f, 1f);

        [SerializeField]
        private Color gridColor = new Color(0.3f, 0.3f, 0.3f, 1f);

        private Material lineMaterial;
        private GUIStyle statsStyle;
        private readonly StringBuilder statsBuilder = new StringBuilder(256);

        private RenderEntityState[] renderStates;
        private int renderStateCount;
        private KernelColliderShapeView[] colliderShapes;
        private int colliderShapeCount;
        private KernelVisionStateView[] visionStates;
        private int visionStateCount;
        private KernelNetworkStats networkStats;
        private bool hasNetworkStats;

        // Cached collider templates (id -> definition), used to resolve the geometry of an
        // agent's vision cone from its template id. Catalog is static after load, so this is
        // built once and reused.
        private readonly Dictionary<uint, KernelColliderTemplateDefinition> colliderTemplates =
            new Dictionary<uint, KernelColliderTemplateDefinition>();
        private bool colliderTemplatesLoaded;

        // Reusable lookup of net_id -> world position for the current frame's render states,
        // so vision target/visibility lines can be drawn without extra kernel queries.
        private readonly Dictionary<uint, Vector3> entityPositions = new Dictionary<uint, Vector3>();

        public void SetEnabled(bool value)
        {
            enableVisualDebug = value;
        }

        /// <summary>
        /// Captures the current frame's render states and queries collider shapes, vision
        /// state and network stats from the kernel. Called from the runner's Update.
        /// </summary>
        public void Capture(NetworkExample.Kernel.Kernel kernel, RenderEntityState[] states, int count)
        {
            renderStates = states;
            renderStateCount = count;

            if (kernel == null || !enableVisualDebug)
            {
                colliderShapeCount = 0;
                visionStateCount = 0;
                hasNetworkStats = false;
                return;
            }

            EnsureBuffers();
            CacheEntityPositions();

            // Request 1 + 2: one query-all call returns every active collider for every
            // entity and purpose. Vision-purpose colliders are handled by the vision overlay
            // below, so they are filtered out at draw time to avoid drawing the cone twice.
            uint colliderFound = kernel.QueryColliderShapes(null, colliderShapes);
            colliderShapeCount = colliderFound > (uint)colliderShapes.Length
                ? colliderShapes.Length
                : (int)colliderFound;

            if (drawVision)
            {
                EnsureColliderTemplates(kernel);
                uint visionFound = kernel.QueryVisionState(null, visionStates);
                visionStateCount = visionFound > (uint)visionStates.Length
                    ? visionStates.Length
                    : (int)visionFound;
            }
            else
            {
                visionStateCount = 0;
            }

            hasNetworkStats = kernel.TryGetNetworkStats(out networkStats);
        }

        private void EnsureBuffers()
        {
            int colliderCapacity = Mathf.Max(1, maxColliderShapes);
            if (colliderShapes == null || colliderShapes.Length != colliderCapacity)
            {
                colliderShapes = new KernelColliderShapeView[colliderCapacity];
            }

            int visionCapacity = Mathf.Max(1, maxVisionAgents);
            if (visionStates == null || visionStates.Length != visionCapacity)
            {
                visionStates = new KernelVisionStateView[visionCapacity];
            }
        }

        private void CacheEntityPositions()
        {
            entityPositions.Clear();
            if (renderStates == null)
            {
                return;
            }

            for (int index = 0; index < renderStateCount; ++index)
            {
                RenderEntityState state = renderStates[index];
                if (state.net_id != 0)
                {
                    entityPositions[state.net_id] = ToVector3(state.position);
                }
            }
        }

        // Request 4: read the loaded collider templates straight from the kernel instead of
        // re-parsing the catalog bundle.
        private void EnsureColliderTemplates(NetworkExample.Kernel.Kernel kernel)
        {
            if (colliderTemplatesLoaded)
            {
                return;
            }

            uint total = kernel.GetColliderTemplates(null);
            if (total == 0)
            {
                // No catalog yet (or kernel not ready). Try again next frame.
                return;
            }

            var buffer = new KernelColliderTemplateDefinition[total];
            uint read = kernel.GetColliderTemplates(buffer);
            colliderTemplates.Clear();
            for (int index = 0; index < read && index < buffer.Length; ++index)
            {
                colliderTemplates[buffer[index].template_id] = buffer[index];
            }

            colliderTemplatesLoaded = true;
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

            if (drawVision && visionStates != null)
            {
                for (int index = 0; index < visionStateCount; ++index)
                {
                    DrawVisionState(visionStates[index]);
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

            statsBuilder
                .Append("\nColliders ")
                .Append(colliderShapeCount);
            if (drawVision)
            {
                statsBuilder
                    .Append("  Vision agents ")
                    .Append(visionStateCount);
            }

            const float width = 320f;
            const float height = 128f;
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
            // Vision colliders are owned by the vision overlay (DrawVisionState), which can
            // anchor them at the agent's eye and draw the target/visibility lines too.
            if ((shape.purpose_flags & (uint)KernelColliderPurpose.Vision) != 0)
            {
                return;
            }

            GL.Color(ColorForType((KernelEntityType)shape.entity_type));
            Vector3 center = ToVector3(shape.world_center);

            switch ((KernelColliderShapeType)shape.shape_type)
            {
                case KernelColliderShapeType.Sphere:
                    // shape_params.x = radius.
                    DrawWireSphere(center, shape.shape_params.x);
                    break;
                case KernelColliderShapeType.Aabb:
                    // shape_params.xyz = half extents.
                    DrawAxisAlignedBox(center, ToVector3(shape.shape_params));
                    break;
                case KernelColliderShapeType.OrientedBox:
                    // shape_params.xyz = half extents, oriented by world_rotation.
                    DrawOrientedBox(
                        center,
                        ToVector3(shape.shape_params),
                        ToQuaternion(shape.world_rotation));
                    break;
                case KernelColliderShapeType.Segment:
                    // Endpoints are supplied directly; shape_params.x = radius (capsule).
                    Vector3 start = ToVector3(shape.segment_start);
                    Vector3 end = ToVector3(shape.segment_end);
                    if (shape.shape_params.x > 0f)
                    {
                        DrawWireCapsule(start, end, shape.shape_params.x);
                    }
                    else
                    {
                        Line(start, end);
                    }
                    break;
                case KernelColliderShapeType.Cone:
                    // shape_params.x = range, shape_params.y = full apex angle (degrees).
                    DrawCone(
                        center,
                        ToQuaternion(shape.world_rotation) * Vector3.forward,
                        shape.shape_params.x,
                        shape.shape_params.y);
                    break;
            }
        }

        private void DrawVisionState(KernelVisionStateView vision)
        {
            if (vision.valid == 0)
            {
                return;
            }

            Vector3 origin = ToVector3(vision.vision_origin);
            Vector3 forward = ToVector3(vision.vision_forward);
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            GL.Color(visionColor);

            // The cone geometry comes from the resolved collider template (range + angle).
            uint templateId = vision.resolved_collider_template_id != 0
                ? vision.resolved_collider_template_id
                : vision.vision_collider_template_id;
            if (colliderTemplates.TryGetValue(templateId, out KernelColliderTemplateDefinition template) &&
                (KernelColliderShapeType)template.shape_type == KernelColliderShapeType.Cone)
            {
                DrawCone(origin, forward, template.shape_params.x, template.shape_params.y);
            }
            else
            {
                // No cone template available from the kernel — fall back to a forward ray so
                // the agent's facing is still visible, but do not invent a cone shape.
                Line(origin, origin + forward * directionLength);
            }

            // Visibility lines to spotted hostiles (positions resolved from this frame's
            // render states; entities outside our render set are skipped).
            DrawVisionLinks(origin, vision.visible_hostiles, vision.visible_hostile_count, visionColor);

            // Highlight the current target candidate distinctly.
            if (vision.current_target_candidate != 0 &&
                entityPositions.TryGetValue(vision.current_target_candidate, out Vector3 targetPos))
            {
                GL.Color(visionTargetColor);
                Line(origin, targetPos);
            }
        }

        private void DrawVisionLinks(Vector3 origin, uint[] netIds, uint linkCount, Color color)
        {
            if (netIds == null)
            {
                return;
            }

            GL.Color(color);
            int safeCount = linkCount > (uint)netIds.Length ? netIds.Length : (int)linkCount;
            for (int index = 0; index < safeCount; ++index)
            {
                uint netId = netIds[index];
                if (netId != 0 && entityPositions.TryGetValue(netId, out Vector3 position))
                {
                    Line(origin, position);
                }
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

        private static void DrawWireCapsule(Vector3 start, Vector3 end, float radius)
        {
            Vector3 axis = end - start;
            float length = axis.magnitude;
            if (length < 1e-5f)
            {
                // Degenerate capsule collapses to a sphere.
                DrawWireSphere(start, radius);
                return;
            }

            axis /= length;
            Vector3 u = Vector3.Cross(axis, Vector3.up);
            if (u.sqrMagnitude < 1e-6f)
            {
                u = Vector3.Cross(axis, Vector3.right);
            }
            u.Normalize();
            Vector3 v = Vector3.Cross(axis, u);

            const int segments = 16;
            float step = Mathf.PI * 2f / segments;

            // End rings (perpendicular to the axis) at each hemisphere center.
            Vector3 prevRingStart = start + u * radius;
            Vector3 prevRingEnd = end + u * radius;
            for (int i = 1; i <= segments; ++i)
            {
                float a = i * step;
                Vector3 offset = (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * radius;
                Vector3 ringStart = start + offset;
                Vector3 ringEnd = end + offset;
                Line(prevRingStart, ringStart);
                Line(prevRingEnd, ringEnd);
                prevRingStart = ringStart;
                prevRingEnd = ringEnd;
            }

            // Connecting lines along the body at the four cardinal offsets.
            Line(start + u * radius, end + u * radius);
            Line(start - u * radius, end - u * radius);
            Line(start + v * radius, end + v * radius);
            Line(start - v * radius, end - v * radius);

            // Hemispherical caps: half arcs in the u-axis and v-axis planes.
            DrawCapArc(start, -axis, u, radius);
            DrawCapArc(start, -axis, v, radius);
            DrawCapArc(end, axis, u, radius);
            DrawCapArc(end, axis, v, radius);
        }

        // Draws a half-circle arc from the side, bulging along outward (the cap apex direction).
        private static void DrawCapArc(Vector3 center, Vector3 outward, Vector3 side, float radius)
        {
            const int segments = 8;
            float step = Mathf.PI / segments;
            Vector3 prev = center + side * radius;
            for (int i = 1; i <= segments; ++i)
            {
                float a = i * step;
                Vector3 point = center + side * (Mathf.Cos(a) * radius) + outward * (Mathf.Sin(a) * radius);
                Line(prev, point);
                prev = point;
            }
        }

        // Draws a wireframe cone with its apex at <paramref name="apex"/>, opening along
        // <paramref name="forward"/>. <paramref name="fullAngleDegrees"/> is the full apex
        // angle, so the half angle (apex to edge) is half of it.
        private static void DrawCone(Vector3 apex, Vector3 forward, float range, float fullAngleDegrees)
        {
            if (range <= 0f)
            {
                return;
            }

            forward = forward.sqrMagnitude < 1e-6f ? Vector3.forward : forward.normalized;
            float halfAngle = Mathf.Clamp(fullAngleDegrees * 0.5f, 0f, 89.9f) * Mathf.Deg2Rad;
            float baseRadius = range * Mathf.Tan(halfAngle);
            Vector3 baseCenter = apex + forward * range;

            Vector3 u = Vector3.Cross(forward, Vector3.up);
            if (u.sqrMagnitude < 1e-6f)
            {
                u = Vector3.Cross(forward, Vector3.right);
            }
            u.Normalize();
            Vector3 v = Vector3.Cross(forward, u);

            const int segments = 24;
            float step = Mathf.PI * 2f / segments;
            Vector3 prev = baseCenter + u * baseRadius;
            for (int i = 1; i <= segments; ++i)
            {
                float a = i * step;
                Vector3 point = baseCenter + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * baseRadius;
                Line(prev, point);
                prev = point;
            }

            // Slant lines from apex to the base ring at the four cardinal directions.
            Line(apex, baseCenter + u * baseRadius);
            Line(apex, baseCenter - u * baseRadius);
            Line(apex, baseCenter + v * baseRadius);
            Line(apex, baseCenter - v * baseRadius);
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

        private static Vector3 ToVector3(KernelVec4 value)
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
