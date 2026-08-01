using NetworkExample.Kernel;
using NetworkExample.UnityDemo.Items;
using NetworkExample.UnityDemo.Rendering;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class NetworkItemPropInputTests : InputTestFixture
    {
        private GameObject gameObject;
        private NetworkItemPropInputSampler itemSampler;
        private Keyboard keyboard;

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            keyboard = InputSystem.AddDevice<Keyboard>();
            gameObject = new GameObject("NetworkItemPropInputTests");
            itemSampler = gameObject.AddComponent<NetworkItemPropInputSampler>();
            // EditMode does not invoke MonoBehaviour.OnEnable. Prime the sampler so
            // its programmatic InputActions are enabled before queuing device state.
            itemSampler.SampleCommands();
        }

        [TearDown]
        public override void TearDown()
        {
            if (gameObject != null)
            {
                Object.DestroyImmediate(gameObject);
            }

            keyboard = null;
            base.TearDown();
        }

        [TestCase(Key.Q, ItemPropInputCommand.Throw)]
        [TestCase(Key.E, ItemPropInputCommand.Pickup)]
        [TestCase(Key.F, ItemPropInputCommand.Use)]
        [TestCase(Key.Tab, ItemPropInputCommand.SelectNextItem)]
        public void SampleCommands_WhenMappedKeyIsPressed_ReturnsCommand(
            Key key,
            ItemPropInputCommand expected)
        {
            SetKeys(key);

            ItemPropInputCommand commands = itemSampler.SampleCommands();

            Assert.That(commands, Is.EqualTo(expected));
        }

        [Test]
        public void SampleCommands_WhileKeyRemainsHeld_DoesNotRepeatCommand()
        {
            SetKeys(Key.Q);

            ItemPropInputCommand first = itemSampler.SampleCommands();
            ItemPropInputCommand held = itemSampler.SampleCommands();

            Assert.That(first, Is.EqualTo(ItemPropInputCommand.Throw));
            Assert.That(held, Is.EqualTo(ItemPropInputCommand.None));
        }

        [Test]
        public void DisableAndEnable_AfterRelease_ClearsPressedState()
        {
            SetKeys(Key.E);
            itemSampler.SampleCommands();

            gameObject.SetActive(false);
            SetKeys();
            gameObject.SetActive(true);

            Assert.That(
                itemSampler.SampleCommands(),
                Is.EqualTo(ItemPropInputCommand.None));
        }

        [Test]
        public void DigitWeaponSelectionKey_DoesNotTriggerItemCommand()
        {
            SetKeys(Key.Digit2);

            Assert.That(
                itemSampler.SampleCommands(),
                Is.EqualTo(ItemPropInputCommand.None));
        }

        private void SetKeys(params Key[] keys)
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
            InputSystem.Update();
        }
    }

    public sealed class LocalInventorySelectionModelTests
    {
        [Test]
        public void ReplaceItems_SortsByContainerAndSlotAndSelectsFirst()
        {
            var model = new LocalInventorySelectionModel();

            model.ReplaceItems(new[]
            {
                Item(22, 3004, 2, 4),
                Item(11, 3003, 1, 5),
                Item(12, 3002, 1, 1),
            });

            Assert.That(model.SelectedItemInstanceId, Is.EqualTo(12));
            Assert.That(model.Items[0].item_instance_id, Is.EqualTo(12));
            Assert.That(model.Items[1].item_instance_id, Is.EqualTo(11));
            Assert.That(model.Items[2].item_instance_id, Is.EqualTo(22));
        }

        [Test]
        public void SelectNext_WrapsAcrossSnapshotItems()
        {
            var model = new LocalInventorySelectionModel();
            model.ReplaceItems(new[]
            {
                Item(11, 3002, 1, 0),
                Item(12, 3003, 1, 1),
            });

            Assert.That(model.SelectNext(out KernelItemInstanceView second), Is.True);
            Assert.That(second.item_instance_id, Is.EqualTo(12));
            Assert.That(model.SelectNext(out KernelItemInstanceView first), Is.True);
            Assert.That(first.item_instance_id, Is.EqualTo(11));
        }

        [Test]
        public void ReplaceItems_PreservesIdentityAndFallsBackWhenRemoved()
        {
            var model = new LocalInventorySelectionModel();
            model.ReplaceItems(new[]
            {
                Item(11, 3002, 1, 0),
                Item(12, 3003, 1, 1),
            });
            model.SelectNext(out _);

            model.ReplaceItems(new[]
            {
                Item(12, 3003, 1, 4),
                Item(13, 3004, 1, 5),
            });
            Assert.That(model.SelectedItemInstanceId, Is.EqualTo(12));

            model.ReplaceItems(new[] { Item(13, 3004, 1, 5) });
            Assert.That(model.SelectedItemInstanceId, Is.EqualTo(13));
        }

        [Test]
        public void ContainerReady_RejectsNonReadySyncStates()
        {
            foreach (KernelInventorySyncState state in new[]
            {
                KernelInventorySyncState.NotAvailable,
                KernelInventorySyncState.Syncing,
                KernelInventorySyncState.Desynced,
            })
            {
                Assert.That(
                    NetworkItemPropController.IsContainerReady(
                        new KernelInventoryContainerView { sync_state = (byte)state }),
                    Is.False);
            }

            Assert.That(
                NetworkItemPropController.IsContainerReady(
                    new KernelInventoryContainerView
                    {
                        sync_state = (byte)KernelInventorySyncState.Ready,
                    }),
                Is.True);
        }

        private static KernelItemInstanceView Item(
            ulong instanceId,
            uint templateId,
            ulong containerId,
            ushort slot)
        {
            return new KernelItemInstanceView
            {
                item_instance_id = instanceId,
                item_template_id = templateId,
                inventory_container_id = containerId,
                slot = slot,
                quantity = 1,
            };
        }
    }

    public sealed class ItemPropRequestAndTargetTests
    {
        [Test]
        public void CreateRequest_AllocatesUniqueNonZeroIdsAndPopulatesUseFields()
        {
            var sender = new ItemPropRequestSender();

            KernelGameplayRequest first = sender.CreateRequest(
                7,
                70,
                KernelDomainAction.Consume,
                selectedItemInstanceId: 700);
            KernelGameplayRequest second = sender.CreateRequest(
                7,
                70,
                KernelDomainAction.Throw,
                selectedItemInstanceId: 701,
                requestedQuantity: 1,
                throwDirection: new KernelVec3(0f, 0f, 1f));

            Assert.That(first.request_id, Is.Not.Zero);
            Assert.That(second.request_id, Is.Not.Zero);
            Assert.That(second.request_id, Is.Not.EqualTo(first.request_id));
            Assert.That(first.requester_peer, Is.EqualTo(7));
            Assert.That(first.instigator_net_id, Is.EqualTo(70));
            Assert.That(first.domain_action, Is.EqualTo((byte)KernelDomainAction.Consume));
            Assert.That(first.selected_item_instance_id, Is.EqualTo(700));
            Assert.That(second.requested_quantity, Is.EqualTo(1));
            Assert.That(second.throw_direction.z, Is.EqualTo(1f));
        }

        [Test]
        public void Submit_WithNullClient_IsRejected()
        {
            var sender = new ItemPropRequestSender();
            KernelGameplayRequest request = sender.CreateRequest(
                1,
                2,
                KernelDomainAction.Consume,
                selectedItemInstanceId: 3);

            Assert.That(sender.Submit(null, request), Is.False);
        }

        [Test]
        public void ThrowDirection_RejectsZeroAndNonFiniteVectors()
        {
            Assert.That(ItemPropTargetSelector.IsFiniteNonZero(Vector3.zero), Is.False);
            Assert.That(
                ItemPropTargetSelector.IsFiniteNonZero(
                    new Vector3(float.NaN, 0f, 1f)),
                Is.False);
            Assert.That(ItemPropTargetSelector.IsFiniteNonZero(Vector3.forward), Is.True);
        }

        [Test]
        public void PickupTarget_SelectsBestAlignedEligiblePlacedItemProp()
        {
            RenderEntityState[] states =
            {
                Player(10, Vector3.zero),
                Prop(20, 200, 3003, new Vector3(0.8f, 0f, 2f)),
                Prop(21, 201, 3004, new Vector3(0f, 0f, 2.5f)),
                Prop(22, 202, 3004, new Vector3(0f, 0f, -1f)),
            };

            bool found = ItemPropTargetSelector.TrySelectPickupTarget(
                states,
                states.Length,
                10,
                Vector3.forward,
                3f,
                out RenderEntityState target);

            Assert.That(found, Is.True);
            Assert.That(target.net_id, Is.EqualTo(21));
        }

        [Test]
        public void PickupTarget_ExcludesPureCarriedInFlightAndOutOfRangeProps()
        {
            RenderEntityState pureProp = Prop(20, 0, 0, Vector3.forward);
            RenderEntityState carrying = Prop(21, 201, 3003, Vector3.forward);
            carrying.world_item_mode = (byte)KernelWorldItemMode.Carrying;
            RenderEntityState inFlight = Prop(22, 202, 3003, Vector3.forward);
            inFlight.world_item_mode = (byte)KernelWorldItemMode.InFlight;
            RenderEntityState outOfRange = Prop(23, 203, 3003, Vector3.forward * 4f);
            RenderEntityState nonProp = Prop(24, 204, 3003, Vector3.forward);
            nonProp.entity_type = KernelEntityType.Projectile;
            RenderEntityState[] states =
            {
                Player(10, Vector3.zero),
                pureProp,
                carrying,
                inFlight,
                outOfRange,
                nonProp,
            };

            Assert.That(
                ItemPropTargetSelector.TrySelectPickupTarget(
                    states,
                    states.Length,
                    10,
                    Vector3.forward,
                    3f,
                    out _),
                Is.False);
        }

        private static RenderEntityState Player(uint netId, Vector3 position)
        {
            return new RenderEntityState
            {
                net_id = netId,
                entity_type = KernelEntityType.Actor,
                position = ToKernel(position),
            };
        }

        private static RenderEntityState Prop(
            uint netId,
            ulong instanceId,
            uint templateId,
            Vector3 position)
        {
            return new RenderEntityState
            {
                net_id = netId,
                entity_type = KernelEntityType.Prop,
                item_instance_id = instanceId,
                template_id = templateId,
                world_item_mode = (byte)KernelWorldItemMode.Placed,
                position = ToKernel(position),
            };
        }

        private static KernelVec3 ToKernel(Vector3 value)
        {
            return new KernelVec3(value.x, value.y, value.z);
        }
    }

    public sealed class NetworkItemPropRenderingTests
    {
        private GameObject root;
        private GameObject registryObject;
        private NetworkPrefabRegistry registry;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("NetworkItemPropRenderingTestsRoot");
            registryObject = new GameObject("NetworkItemPropRenderingTests");
            registry = registryObject.AddComponent<NetworkPrefabRegistry>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(registryObject);
        }

        [Test]
        public void DefaultCatalog_RegistersPickupPropsAndDedicatedFireFloor()
        {
            NetworkPrefabCatalog catalog = Resources.Load<NetworkPrefabCatalog>(
                NetworkPrefabRegistry.DefaultCatalogResourcePath);

            Assert.That(catalog.TryGetPropPrefab(3003, out GameObject potion), Is.True);
            Assert.That(catalog.TryGetPropPrefab(3004, out GameObject bottle), Is.True);
            Assert.That(potion, Is.Not.SameAs(bottle));
            Assert.That(catalog.PropFallback, Is.Not.Null);
            Assert.That(
                catalog.TryGetProjectilePrefab(4, out GameObject fireFloor),
                Is.True);
            Assert.That(
                catalog.TryGetProjectilePrefab(3, out GameObject regularProjectile),
                Is.True);
            Assert.That(fireFloor, Is.Not.SameAs(regularProjectile));
            Assert.That(fireFloor.GetComponent<NetworkProjectileView>(), Is.Not.Null);
            Assert.That(fireFloor.transform.Find("AreaVisual"), Is.Not.Null);
            Assert.That(
                fireFloor.transform.Find("AreaVisual").localScale,
                Is.EqualTo(new Vector3(1f, 0.025f, 1f)));
        }

        [Test]
        public void PropPrefab_ExactItemBindingWinsAndPurePropUsesFallback()
        {
            var exactPrefab = new GameObject("ExactProp");
            var fallbackPrefab = new GameObject("FallbackProp");
            var catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
            try
            {
                exactPrefab.transform.localScale = Vector3.one * 2f;
                fallbackPrefab.transform.localScale = Vector3.one * 3f;
                catalog.Configure(null, null);
                catalog.ConfigureProps(
                    new[]
                    {
                        new NetworkPrefabCatalog.PropPrefabBinding(3003, exactPrefab),
                    },
                    fallbackPrefab);
                registry.Configure(catalog);

                GameObject exact = registry.InstantiateVisual(
                    new RenderEntityState
                    {
                        net_id = 1,
                        entity_type = KernelEntityType.Prop,
                        template_id = 3003,
                    },
                    root.transform);
                LogAssert.Expect(
                    LogType.Warning,
                    "No exact prop item prefab is registered for template id 0; " +
                    "using the configured fallback.");
                GameObject fallback = registry.InstantiateVisual(
                    new RenderEntityState
                    {
                        net_id = 2,
                        entity_type = KernelEntityType.Prop,
                    },
                    root.transform);

                Assert.That(exact.name, Is.EqualTo("NetEntity_Prop"));
                Assert.That(fallback.name, Is.EqualTo("NetEntity_Prop"));
                Assert.That(exact.transform.localScale, Is.EqualTo(Vector3.one * 2f));
                Assert.That(fallback.transform.localScale, Is.EqualTo(Vector3.one * 3f));
            }
            finally
            {
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(exactPrefab);
                Object.DestroyImmediate(fallbackPrefab);
                registry.Configure(null);
            }
        }

        [Test]
        public void PropPrefab_DuplicateIdSkipsNullAndWarnsDuringValidation()
        {
            var registeredPrefab = new GameObject("RegisteredProp");
            var catalog = ScriptableObject.CreateInstance<NetworkPrefabCatalog>();
            try
            {
                catalog.Configure(null, null);
                catalog.ConfigureProps(
                    new[]
                    {
                        new NetworkPrefabCatalog.PropPrefabBinding(3003, null),
                        new NetworkPrefabCatalog.PropPrefabBinding(3003, registeredPrefab),
                    },
                    registeredPrefab);
                registry.Configure(catalog);

                LogAssert.Expect(
                    LogType.Warning,
                    "Network prefab catalog prop item template id 3003 has no prefab.");
                LogAssert.Expect(
                    LogType.Warning,
                    "Network prefab catalog contains duplicate prop item template id 3003; " +
                    "the first non-null prefab wins.");

                GameObject visual = registry.InstantiateVisual(
                    new RenderEntityState
                    {
                        net_id = 3,
                        entity_type = KernelEntityType.Prop,
                        template_id = 3003,
                    },
                    root.transform);

                Assert.That(visual, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(catalog);
                Object.DestroyImmediate(registeredPrefab);
                registry.Configure(null);
            }
        }
    }
}
