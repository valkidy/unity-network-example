using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// One model coming apart: every piece it was baked into, thrown outwards
    /// from a blast inside it and taken back out of sight at the end.
    /// </summary>
    /// <remarks>
    /// The pieces are whatever the instance already draws -- the shattered prefab
    /// the bake produced -- so this knows nothing about towers, nests or how the
    /// model was cut up. It knows where each piece sits inside the model, and
    /// that is what decides which way the piece goes.
    ///
    /// Nothing is simulated. Each piece is put on a closed-form arc at the
    /// moment of the burst, and every later frame evaluates that arc at the time
    /// elapsed rather than stepping the last frame forward. Two clients running
    /// at different frame rates therefore draw the same piece in the same place
    /// at the same moment, which stepped integration would not give -- and a test
    /// can ask for the state at two seconds without ticking two seconds of
    /// frames. It is the same reason <see cref="NetworkSplatView"/> solves its
    /// arc instead of integrating it, and the arc itself is that one.
    ///
    /// There is no collision here at all. A piece stops at a flat floor through
    /// the point the model stood on, because that is what the ground under a
    /// standing prop is within a metre or two, and because the alternative --
    /// a query per piece per frame, against colliders this client only has for
    /// the terrain -- costs more than the effect is worth. Pieces land in each
    /// other and through the odd slope; over the second or so before they sink
    /// away, nobody reads it as wrong.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkShatterView : MonoBehaviour
    {
        /// <summary>
        /// Where a burst is in its life. A pooled instance sits in
        /// <see cref="Phase.Idle"/> and is handed back out from there.
        /// </summary>
        public enum Phase
        {
            Idle = 0,
            Playing = 1,
        }

        private Piece[] pieces;
        private Phase phase = Phase.Idle;
        private float elapsed;
        private float lifeSeconds;
        private float sinkSeconds;
        private Vector3 gravity = Physics.gravity;

        public Phase CurrentPhase => phase;

        public bool IsPlaying => phase != Phase.Idle;

        public int PieceCount => pieces == null ? 0 : pieces.Length;

        /// <summary>Seconds since the burst started.</summary>
        public float Elapsed => elapsed;

        public Transform GetPiece(int index)
        {
            return pieces == null || index < 0 || index >= pieces.Length
                ? null
                : pieces[index].transform;
        }

        /// <summary>
        /// Takes every piece the instance draws, and the pose it draws it in.
        /// Returns how many it found.
        /// </summary>
        /// <remarks>
        /// Called once by whoever built the instance, because the rest pose is
        /// what <see cref="Stop"/> puts the pieces back to -- read it after a
        /// burst has moved them and the model would be reassembled wrong.
        /// </remarks>
        public int Bind()
        {
            MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);
            pieces = new Piece[filters.Length];
            for (int index = 0; index < filters.Length; ++index)
            {
                Transform piece = filters[index].transform;
                Mesh mesh = filters[index].sharedMesh;
                Bounds bounds = mesh != null ? mesh.bounds : new Bounds();
                pieces[index] = new Piece
                {
                    transform = piece,
                    restLocalPosition = piece.localPosition,
                    restLocalRotation = piece.localRotation,
                    // How far the piece's own centre sits above whatever it comes
                    // to rest on. Its thinnest half-extent, which is the piece
                    // lying on its flattest side -- a plank on its face rather
                    // than balanced on an end.
                    restHeight = Mathf.Min(
                        bounds.extents.x,
                        Mathf.Min(bounds.extents.y, bounds.extents.z)),
                    // Far enough that the piece is gone whichever way it ended up
                    // turned.
                    sinkDistance = bounds.size.magnitude,
                    size = bounds.size.magnitude,
                };
            }

            return pieces.Length;
        }

        /// <summary>
        /// Blows the model apart. The instance has to already stand where the
        /// model stood: the arcs are solved from where the pieces are now.
        /// </summary>
        public void Play(in NetworkShatterLaunch launch)
        {
            if (pieces == null || pieces.Length == 0 || launch.lifeSeconds <= 0f)
            {
                Stop();
                return;
            }

            RestoreRestPose();
            gameObject.SetActive(true);

            gravity = launch.gravity;
            lifeSeconds = launch.lifeSeconds;
            sinkSeconds = Mathf.Clamp(launch.sinkSeconds, 0f, launch.lifeSeconds);
            elapsed = 0f;

            Vector3 blast = ResolveBlastOrigin(launch);
            float farthest = FarthestPieceFrom(blast);
            MeasureSizes(out float smallest, out float largest);

            // The global generator is borrowed and put back rather than replaced,
            // so seeding this burst does not move anything else's randomness on
            // by a burst's worth -- and so two clients seeding from the same
            // entity get the same debris without either of them having to agree
            // on anything else.
            Random.State state = Random.state;
            Random.InitState(launch.seed);
            for (int index = 0; index < pieces.Length; ++index)
            {
                LaunchPiece(ref pieces[index], launch, blast, farthest, smallest, largest);
            }

            Random.state = state;

            phase = Phase.Playing;
            Draw();
        }

        /// <summary>
        /// Ages the burst by one frame. Returns false once it is over, which is
        /// the pool's signal to take the instance back.
        /// </summary>
        public bool Tick(float deltaSeconds)
        {
            if (phase == Phase.Idle)
            {
                return false;
            }

            elapsed += Mathf.Max(0f, deltaSeconds);
            if (elapsed >= lifeSeconds)
            {
                Stop();
                return false;
            }

            Draw();
            return true;
        }

        public void Stop()
        {
            phase = Phase.Idle;
            elapsed = 0f;
            RestoreRestPose();
            gameObject.SetActive(false);
        }

        /// <summary>
        /// When a piece thrown upwards at <paramref name="upwardSpeed"/> from
        /// <paramref name="height"/> above the floor reaches it.
        /// </summary>
        /// <remarks>
        /// The height on the arc is h + v*t + g*t^2/2, and the piece lands where
        /// that reaches zero. Of the two roots the later one is the landing --
        /// the earlier is where the arc would have come from -- and with gravity
        /// pulling down that is the one taken with the minus sign. Infinity when
        /// nothing brings the piece back.
        /// </remarks>
        public static float SolveSettleSeconds(
            float height,
            float upwardSpeed,
            float gravityY)
        {
            if (height <= 0f)
            {
                return 0f;
            }

            if (gravityY >= 0f)
            {
                return upwardSpeed < 0f
                    ? height / -upwardSpeed
                    : float.PositiveInfinity;
            }

            float twiceA = gravityY;
            float discriminant = (upwardSpeed * upwardSpeed) - (2f * gravityY * height);
            if (discriminant < 0f)
            {
                return float.PositiveInfinity;
            }

            return (-upwardSpeed - Mathf.Sqrt(discriminant)) / twiceA;
        }

        private void LaunchPiece(
            ref Piece piece,
            in NetworkShatterLaunch launch,
            Vector3 blast,
            float farthest,
            float smallest,
            float largest)
        {
            Vector3 position = piece.transform.position;
            piece.launchPosition = position;
            piece.launchRotation = piece.transform.rotation;
            piece.settlePosition = position;
            piece.velocity = Vector3.zero;
            piece.spinAxis = Vector3.up;
            piece.spinDegreesPerSecond = 0f;
            piece.settleSeconds = 0f;

            if (position.y - launch.floorY <= launch.groundedHeight)
            {
                // The ground the model was standing on. It stays, and only sinks
                // away with the rest at the end.
                return;
            }

            Vector3 outwards = position - blast;
            Vector3 direction = outwards.sqrMagnitude > 1e-6f
                ? outwards.normalized + (Vector3.up * launch.upwardBias)
                : Vector3.up;
            direction = direction.sqrMagnitude > 1e-6f
                ? direction.normalized
                : Vector3.up;

            float reach = farthest > 1e-4f
                ? Mathf.Clamp01(outwards.magnitude / farthest)
                : 0f;
            float speed = Mathf.Lerp(launch.nearSpeed, launch.farSpeed, reach);
            speed *= 1f + Random.Range(-launch.speedJitter, launch.speedJitter);
            // The roof does not leave like a shingle does. Size stands in for
            // mass, which is the one thing a bake of hollow shells cannot give.
            float heaviness = largest - smallest > 1e-4f
                ? Mathf.Clamp01((piece.size - smallest) / (largest - smallest))
                : 0f;
            speed *= Mathf.Max(0f, 1f - (launch.heavyPieceDamping * heaviness));

            piece.velocity = direction * speed;
            piece.spinAxis = Random.onUnitSphere;
            piece.spinDegreesPerSecond = Random.Range(
                -launch.maxSpinDegreesPerSecond,
                launch.maxSpinDegreesPerSecond);

            float settleY = launch.floorY + piece.restHeight;
            float settleSeconds = SolveSettleSeconds(
                position.y - settleY,
                piece.velocity.y,
                gravity.y);
            // A piece nothing brings down keeps flying until the burst is over,
            // rather than carrying an infinity into the arc.
            piece.settleSeconds = Mathf.Min(settleSeconds, lifeSeconds);
            Vector3 settled = NetworkSplatView.SampleArc(
                position,
                piece.velocity,
                gravity,
                piece.settleSeconds);
            settled.y = Mathf.Max(settled.y, settleY);
            piece.settlePosition = settled;
        }

        private void Draw()
        {
            float sinkStart = lifeSeconds - sinkSeconds;
            float sunk = sinkSeconds > 0f && elapsed > sinkStart
                ? (elapsed - sinkStart) / sinkSeconds
                : 0f;

            for (int index = 0; index < pieces.Length; ++index)
            {
                Piece piece = pieces[index];
                if (piece.transform == null)
                {
                    continue;
                }

                Vector3 position = elapsed < piece.settleSeconds
                    ? NetworkSplatView.SampleArc(
                        piece.launchPosition,
                        piece.velocity,
                        gravity,
                        elapsed)
                    : piece.settlePosition;
                if (sunk > 0f)
                {
                    position += Vector3.down * (sunk * piece.sinkDistance);
                }

                // The tumble stops when the piece does, so a settled piece is not
                // still turning where it lies.
                float spun = Mathf.Min(elapsed, piece.settleSeconds) *
                    piece.spinDegreesPerSecond;
                piece.transform.SetPositionAndRotation(
                    position,
                    Quaternion.AngleAxis(spun, piece.spinAxis) * piece.launchRotation);
            }
        }

        private void RestoreRestPose()
        {
            if (pieces == null)
            {
                return;
            }

            for (int index = 0; index < pieces.Length; ++index)
            {
                Transform piece = pieces[index].transform;
                if (piece != null)
                {
                    piece.localPosition = pieces[index].restLocalPosition;
                    piece.localRotation = pieces[index].restLocalRotation;
                }
            }
        }

        /// <summary>
        /// Where the push comes from: under the middle of the pieces, at the
        /// asked-for height through what the model reaches above its floor.
        /// </summary>
        private Vector3 ResolveBlastOrigin(in NetworkShatterLaunch launch)
        {
            Vector3 min = pieces[0].transform.position;
            Vector3 max = min;
            for (int index = 1; index < pieces.Length; ++index)
            {
                Vector3 position = pieces[index].transform.position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }

            float top = Mathf.Max(max.y, launch.floorY);
            return new Vector3(
                (min.x + max.x) * 0.5f,
                Mathf.Lerp(launch.floorY, top, Mathf.Clamp01(launch.blastHeightFraction)),
                (min.z + max.z) * 0.5f);
        }

        private float FarthestPieceFrom(Vector3 blast)
        {
            float farthest = 0f;
            for (int index = 0; index < pieces.Length; ++index)
            {
                farthest = Mathf.Max(
                    farthest,
                    Vector3.Distance(pieces[index].transform.position, blast));
            }

            return farthest;
        }

        private void MeasureSizes(out float smallest, out float largest)
        {
            smallest = float.MaxValue;
            largest = 0f;
            for (int index = 0; index < pieces.Length; ++index)
            {
                smallest = Mathf.Min(smallest, pieces[index].size);
                largest = Mathf.Max(largest, pieces[index].size);
            }

            if (smallest > largest)
            {
                smallest = largest;
            }
        }

        private struct Piece
        {
            public Transform transform;
            public Vector3 restLocalPosition;
            public Quaternion restLocalRotation;
            public float restHeight;
            public float sinkDistance;
            public float size;

            public Vector3 launchPosition;
            public Quaternion launchRotation;
            public Vector3 velocity;
            public Vector3 settlePosition;
            public Vector3 spinAxis;
            public float spinDegreesPerSecond;
            public float settleSeconds;
        }
    }
}
