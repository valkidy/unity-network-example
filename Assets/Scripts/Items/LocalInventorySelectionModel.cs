using System.Collections.Generic;
using NetworkExample.Kernel;

namespace NetworkExample.UnityDemo.Items
{
    public sealed class LocalInventorySelectionModel
    {
        private readonly List<KernelItemInstanceView> items =
            new List<KernelItemInstanceView>();
        private ulong selectedItemInstanceId;

        public int ItemCount => items.Count;
        public ulong SelectedItemInstanceId => selectedItemInstanceId;

        public IReadOnlyList<KernelItemInstanceView> Items => items;

        public bool ReplaceItems(IEnumerable<KernelItemInstanceView> snapshot)
        {
            ulong previousSelection = selectedItemInstanceId;
            items.Clear();
            if (snapshot != null)
            {
                items.AddRange(snapshot);
            }

            items.Sort(CompareItems);
            selectedItemInstanceId = FindItem(previousSelection, out _)
                ? previousSelection
                : items.Count > 0
                    ? items[0].item_instance_id
                    : 0;
            return previousSelection != selectedItemInstanceId;
        }

        public bool TryGetSelected(out KernelItemInstanceView item)
        {
            return FindItem(selectedItemInstanceId, out item);
        }

        public bool SelectNext(out KernelItemInstanceView item)
        {
            if (items.Count == 0)
            {
                selectedItemInstanceId = 0;
                item = default;
                return false;
            }

            int selectedIndex = IndexOf(selectedItemInstanceId);
            int nextIndex = selectedIndex < 0
                ? 0
                : (selectedIndex + 1) % items.Count;
            selectedItemInstanceId = items[nextIndex].item_instance_id;
            item = items[nextIndex];
            return true;
        }

        public void Clear()
        {
            items.Clear();
            selectedItemInstanceId = 0;
        }

        private bool FindItem(ulong itemInstanceId, out KernelItemInstanceView item)
        {
            int index = IndexOf(itemInstanceId);
            if (index >= 0)
            {
                item = items[index];
                return true;
            }

            item = default;
            return false;
        }

        private int IndexOf(ulong itemInstanceId)
        {
            if (itemInstanceId == 0)
            {
                return -1;
            }

            for (int index = 0; index < items.Count; ++index)
            {
                if (items[index].item_instance_id == itemInstanceId)
                {
                    return index;
                }
            }

            return -1;
        }

        private static int CompareItems(
            KernelItemInstanceView left,
            KernelItemInstanceView right)
        {
            int containerComparison = left.inventory_container_id.CompareTo(
                right.inventory_container_id);
            if (containerComparison != 0)
            {
                return containerComparison;
            }

            int slotComparison = left.slot.CompareTo(right.slot);
            return slotComparison != 0
                ? slotComparison
                : left.item_instance_id.CompareTo(right.item_instance_id);
        }
    }
}
