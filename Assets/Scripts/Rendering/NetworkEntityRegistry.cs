using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkEntityRegistry : MonoBehaviour
    {
        private readonly Dictionary<ulong, GameObject> entities = new Dictionary<ulong, GameObject>();
        private readonly Dictionary<ulong, GameObject> entitiesByNetId =
            new Dictionary<ulong, GameObject>();
        private readonly List<ulong> netIdsToRemove = new List<ulong>();

        public bool TryGet(ulong entityId, out GameObject entity)
        {
            return entities.TryGetValue(entityId, out entity) && entity != null;
        }

        public bool TryGetByNetId(ulong netId, out GameObject entity)
        {
            return entitiesByNetId.TryGetValue(netId, out entity) && entity != null;
        }

        public void Register(ulong entityId, GameObject entity)
        {
            entities[entityId] = entity;
        }

        public void RegisterNetId(ulong netId, GameObject entity)
        {
            if (netId == 0 || entity == null)
            {
                return;
            }

            entitiesByNetId[netId] = entity;
        }

        public bool Contains(ulong entityId)
        {
            return entities.TryGetValue(entityId, out GameObject entity) && entity != null;
        }

        public void Remove(ulong entityId)
        {
            if (!entities.TryGetValue(entityId, out GameObject entity))
            {
                return;
            }

            entities.Remove(entityId);
            if (entity != null)
            {
                RemoveNetIdsFor(entity);
                Destroy(entity);
            }
        }

        public void Clear()
        {
            foreach (GameObject entity in entities.Values)
            {
                if (entity != null)
                {
                    Destroy(entity);
                }
            }

            entities.Clear();
            entitiesByNetId.Clear();
            netIdsToRemove.Clear();
        }

        private void RemoveNetIdsFor(GameObject entity)
        {
            netIdsToRemove.Clear();
            foreach (KeyValuePair<ulong, GameObject> pair in entitiesByNetId)
            {
                if (pair.Value == entity)
                {
                    netIdsToRemove.Add(pair.Key);
                }
            }

            foreach (ulong netId in netIdsToRemove)
            {
                entitiesByNetId.Remove(netId);
            }
            netIdsToRemove.Clear();
        }
    }
}
