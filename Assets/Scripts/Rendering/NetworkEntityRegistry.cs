using System.Collections.Generic;
using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class NetworkEntityRegistry : MonoBehaviour
    {
        private readonly Dictionary<ulong, GameObject> entities = new Dictionary<ulong, GameObject>();

        public bool TryGet(ulong entityId, out GameObject entity)
        {
            return entities.TryGetValue(entityId, out entity) && entity != null;
        }

        public void Register(ulong entityId, GameObject entity)
        {
            entities[entityId] = entity;
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
        }
    }
}
