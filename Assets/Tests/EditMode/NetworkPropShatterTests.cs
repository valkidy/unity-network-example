using System.Collections.Generic;
using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkPropShatterTests
    {
        private const float Tolerance = 1e-3f;

        private GameObject gameObject;
        private GameObject rootObject;
        private GameObject model;

        [SetUp]
        public void SetUp()
        {
            gameObject = new GameObject("NetworkPropShatterTests");
            rootObject = new GameObject("NetworkPropShatterTestsRoot");
            model = CreateModel();
        }

        [TearDown]
        public void TearDown()
        {
            DestroyIfPresent(gameObject);
            DestroyIfPresent(rootObject);
            DestroyIfPresent(model);
        }

        [Test]
        public void Play_ThrowsEveryPieceExceptTheOneOnTheGround()
        {
            NetworkShatterView burst = CreateBurst();

            burst.Play(Launch());
            burst.Tick(0.25f);

            // Piece 0 is the base the model stands on: it is inside the grounded
            // height, so the blast leaves it alone.
            AssertClose(burst.GetPiece(0).position, RestOf(0));
            for (int index = 1; index < burst.PieceCount; ++index)
            {
                Assert.That(
                    Vector3.Distance(burst.GetPiece(index).position, RestOf(index)),
                    Is.GreaterThan(0.1f),
                    "piece " + index + " did not move");
            }
        }

        [Test]
        public void Play_ThrowsThePiecesAwayFromTheBlast()
        {
            NetworkShatterView burst = CreateBurst();

            burst.Play(Launch());
            burst.Tick(0.25f);

            // Outwards, not in a shared direction: a burst that pushed everything
            // the same way would read as a gust rather than an explosion.
            for (int index = 1; index < burst.PieceCount; ++index)
            {
                Vector3 rest = RestOf(index);
                Vector3 moved = burst.GetPiece(index).position - rest;
                Vector3 outwards = new Vector3(rest.x, 0f, rest.z) - Vector3.zero;
                if (outwards.sqrMagnitude < 0.01f)
                {
                    continue;
                }

                Assert.That(
                    Vector3.Dot(
                        new Vector3(moved.x, 0f, moved.z).normalized,
                        outwards.normalized),
                    Is.GreaterThan(0f),
                    "piece " + index + " was pushed inwards");
            }
        }

        [Test]
        public void Play_NoPieceFallsThroughTheFloor()
        {
            NetworkShatterView burst = CreateBurst();
            burst.transform.position = new Vector3(0f, 4f, 0f);

            // A floor at the model's feet, wherever those are: the prop the burst
            // stands in for was not necessarily standing at the world origin.
            burst.Play(Launch(floorY: 4f));
            for (int frame = 0; frame < 75; ++frame)
            {
                burst.Tick(1f / 30f);
                for (int index = 0; index < burst.PieceCount; ++index)
                {
                    Assert.That(
                        burst.GetPiece(index).position.y,
                        Is.GreaterThanOrEqualTo(4f - Tolerance),
                        "piece " + index + " at " + burst.Elapsed + "s");
                }
            }
        }

        [Test]
        public void Play_OnceSettled_LeavesThePiecesWhereTheyLie()
        {
            NetworkShatterView burst = CreateBurst();

            burst.Play(Launch());
            burst.Tick(2f);
            List<Pose> settled = Capture(burst);
            burst.Tick(0.4f);

            // Still short of the sink, so nothing should have moved -- a piece
            // still turning where it lay would read as debris skidding.
            for (int index = 0; index < burst.PieceCount; ++index)
            {
                AssertClose(burst.GetPiece(index).position, settled[index].position);
                Assert.That(
                    Quaternion.Angle(burst.GetPiece(index).rotation, settled[index].rotation),
                    Is.LessThan(0.01f),
                    "piece " + index);
            }
        }

        [Test]
        public void Play_TakesThePiecesOutOfSightBeforeTheBurstEnds()
        {
            NetworkShatterView burst = CreateBurst();

            burst.Play(Launch());
            burst.Tick(2.9f);

            // 2.9s of a 3s life with a 0.5s sink: four fifths of the way down.
            for (int index = 0; index < burst.PieceCount; ++index)
            {
                Assert.That(
                    burst.GetPiece(index).position.y,
                    Is.LessThan(0f),
                    "piece " + index + " is still above the ground");
            }
        }

        [Test]
        public void Play_AtAnyFrameRate_PutsEveryPieceInTheSamePlace()
        {
            NetworkShatterView coarse = CreateBurst();
            NetworkShatterView fine = CreateBurst();

            coarse.Play(Launch(seed: 17));
            fine.Play(Launch(seed: 17));
            coarse.Tick(1f);
            for (int frame = 0; frame < 20; ++frame)
            {
                fine.Tick(0.05f);
            }

            // The arcs are solved, not stepped, so a client at 20 fps and one at
            // 200 draws the same debris in the same place at the same moment.
            for (int index = 0; index < coarse.PieceCount; ++index)
            {
                AssertClose(fine.GetPiece(index).position, coarse.GetPiece(index).position);
            }
        }

        [Test]
        public void Play_WhenItsLifeIsOver_RebuildsTheModelAndStops()
        {
            NetworkShatterView burst = CreateBurst();
            var rest = new List<Vector3>();
            for (int index = 0; index < burst.PieceCount; ++index)
            {
                rest.Add(burst.GetPiece(index).localPosition);
            }

            burst.Play(Launch());
            Assert.That(burst.Tick(2.9f), Is.True);
            Assert.That(burst.Tick(0.2f), Is.False);

            Assert.That(burst.IsPlaying, Is.False);
            Assert.That(burst.CurrentPhase, Is.EqualTo(NetworkShatterView.Phase.Idle));
            Assert.That(burst.gameObject.activeSelf, Is.False);
            for (int index = 0; index < burst.PieceCount; ++index)
            {
                // Put back where the model had them, or the next burst would be
                // thrown from wherever the last one left the wreckage.
                AssertClose(burst.GetPiece(index).localPosition, rest[index]);
            }
        }

        [Test]
        public void SettleTime_IsWhenTheArcReachesTheFloor()
        {
            // Two metres under 10 m/s^2 with no throw: t = sqrt(2h/g).
            Assert.That(
                NetworkShatterView.SolveSettleSeconds(2f, 0f, -10f),
                Is.EqualTo(Mathf.Sqrt(0.4f)).Within(Tolerance));

            // Thrown straight up at 10 m/s off the floor: a second up, a second
            // back down.
            Assert.That(
                NetworkShatterView.SolveSettleSeconds(0.0001f, 10f, -10f),
                Is.EqualTo(2f).Within(0.01f));

            Assert.That(NetworkShatterView.SolveSettleSeconds(0f, 5f, -10f), Is.Zero);
            Assert.That(
                NetworkShatterView.SolveSettleSeconds(2f, 5f, 0f),
                Is.EqualTo(float.PositiveInfinity),
                "Nothing brings it back, and the burst's own life is the only limit.");
        }

        [Test]
        public void Shatter_FromTheSameNetId_ThrowsTheSameBurst()
        {
            NetworkPropShatter shatter = CreateShatter();

            shatter.TryShatter(Vector3.zero, Quaternion.identity, 42UL);
            shatter.Tick(0.4f);
            List<Pose> first = Capture(Burst());

            Burst().Stop();
            shatter.TryShatter(Vector3.zero, Quaternion.identity, 42UL);
            shatter.Tick(0.4f);
            List<Pose> again = Capture(Burst());
            for (int index = 0; index < first.Count; ++index)
            {
                AssertClose(again[index].position, first[index].position);
            }

            Burst().Stop();
            shatter.TryShatter(Vector3.zero, Quaternion.identity, 43UL);
            shatter.Tick(0.4f);
            List<Pose> other = Capture(Burst());

            bool anyDifferent = false;
            for (int index = 0; index < first.Count && !anyDifferent; ++index)
            {
                anyDifferent =
                    Vector3.Distance(other[index].position, first[index].position) > 0.05f;
            }

            Assert.That(anyDifferent, Is.True, "Another net id should throw another burst.");
        }

        [Test]
        public void Shatter_ReusesTheInstanceOnceItsBurstIsOver()
        {
            NetworkPropShatter shatter = CreateShatter();

            Assert.That(
                shatter.TryShatter(Vector3.zero, Quaternion.identity, 9UL),
                Is.True);
            Assert.That(shatter.LiveBurstCount, Is.EqualTo(1));
            shatter.Tick(5f);
            Assert.That(shatter.LiveBurstCount, Is.Zero);

            shatter.TryShatter(Vector3.zero, Quaternion.identity, 10UL);

            Assert.That(shatter.PooledBurstCount, Is.EqualTo(1));
            Assert.That(shatter.LiveBurstCount, Is.EqualTo(1));
        }

        [Test]
        public void Shatter_StripsTheCollidersItWasHandedWithTheModel()
        {
            NetworkPropShatter shatter = CreateShatter();

            shatter.TryShatter(Vector3.zero, Quaternion.identity, 1UL);

            // A piece is drawn, not simulated: colliders riding along on the
            // model would report the debris to gameplay as it flew through it.
            Assert.That(Burst().GetComponentsInChildren<Collider>(true), Is.Empty);
        }

        [Test]
        public void Shatter_WithNoModelBound_FallsBackToTheBakedTower()
        {
            var fallbackObject = new GameObject("NetworkPropShatterFallback");
            var fallbackRoot = new GameObject("NetworkPropShatterFallbackRoot");
            try
            {
                var fallback = fallbackObject.AddComponent<NetworkPropShatter>();
                fallback.Configure(fallbackRoot.transform);

                Assert.That(
                    fallback.TryShatter(Vector3.zero, Quaternion.identity, 2UL),
                    Is.True,
                    "Resources/" + NetworkPropShatter.DefaultShatteredPrefabResourcePath +
                    " is the bake; run Network Example/Presentation/Build Tower " +
                    "Shatter Assets if it has gone missing.");
                Assert.That(
                    fallbackRoot.GetComponentInChildren<NetworkShatterView>(true).PieceCount,
                    Is.EqualTo(123));
            }
            finally
            {
                DestroyIfPresent(fallbackObject);
                DestroyIfPresent(fallbackRoot);
            }
        }

        private NetworkPropShatter CreateShatter()
        {
            NetworkPropShatter shatter = gameObject.AddComponent<NetworkPropShatter>();
            shatter.Configure(rootObject.transform, model);
            return shatter;
        }

        private NetworkShatterView Burst()
        {
            var burst = rootObject.GetComponentInChildren<NetworkShatterView>(true);
            Assert.That(burst, Is.Not.Null, "Nothing was thrown.");
            return burst;
        }

        private NetworkShatterView CreateBurst()
        {
            GameObject instance = Object.Instantiate(model, rootObject.transform, false);
            var view = instance.AddComponent<NetworkShatterView>();
            Assert.That(view.Bind(), Is.EqualTo(model.transform.GetChild(0).childCount));
            return view;
        }

        private Vector3 RestOf(int index)
        {
            // The model the burst was cloned from still stands untouched, so it
            // is what says where a piece belongs.
            return model.transform.GetChild(0).GetChild(index).position;
        }

        private static NetworkShatterLaunch Launch(float floorY = 0f, int seed = 1)
        {
            return new NetworkShatterLaunch
            {
                blastHeightFraction = 0.25f,
                nearSpeed = 6f,
                farSpeed = 3f,
                upwardBias = 0.5f,
                speedJitter = 0f,
                heavyPieceDamping = 0f,
                maxSpinDegreesPerSecond = 180f,
                groundedHeight = 0.6f,
                floorY = floorY,
                gravity = new Vector3(0f, -9.81f, 0f),
                lifeSeconds = 3f,
                sinkSeconds = 0.5f,
                seed = seed,
            };
        }

        private static List<Pose> Capture(NetworkShatterView burst)
        {
            var poses = new List<Pose>(burst.PieceCount);
            for (int index = 0; index < burst.PieceCount; ++index)
            {
                Transform piece = burst.GetPiece(index);
                poses.Add(new Pose(piece.position, piece.rotation));
            }

            return poses;
        }

        /// <summary>
        /// A stand-in for a baked model: a node carrying pieces, one of them low
        /// enough to count as the ground the model stands on.
        /// </summary>
        private static GameObject CreateModel()
        {
            var root = new GameObject("TestShattered");
            var node = new GameObject("geometry");
            node.transform.SetParent(root.transform, false);

            AddPiece(node.transform, new Vector3(0f, 0.2f, 0f));
            AddPiece(node.transform, new Vector3(1.2f, 1.5f, 0f));
            AddPiece(node.transform, new Vector3(-1.1f, 2.4f, 0.4f));
            AddPiece(node.transform, new Vector3(0.3f, 3.6f, -0.9f));
            AddPiece(node.transform, new Vector3(0f, 4.8f, 0f));
            return root;
        }

        private static void AddPiece(Transform parent, Vector3 localPosition)
        {
            GameObject piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.name = "chunk_" + parent.childCount.ToString("D3");
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = localPosition;
        }

        private static void AssertClose(Vector3 actual, Vector3 expected)
        {
            Assert.That(
                Vector3.Distance(actual, expected),
                Is.LessThan(Tolerance),
                $"expected {expected} but was {actual}");
        }

        private static void DestroyIfPresent(GameObject target)
        {
            if (target != null)
            {
                Object.DestroyImmediate(target);
            }
        }
    }
}
