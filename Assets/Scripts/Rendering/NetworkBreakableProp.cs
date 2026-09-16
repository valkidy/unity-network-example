using UnityEngine;

namespace NetworkExample.UnityDemo.Rendering
{
    /// <summary>
    /// Marks a prop's art as something that comes apart when the prop is
    /// destroyed, and names the model it comes apart into.
    /// </summary>
    /// <remarks>
    /// On the art rather than in a table keyed by template id, because the
    /// despawn a client is told about carries no template id -- only a net id, an
    /// entity type and a reason. The visual standing there is the only thing that
    /// still knows what it was, so asking it is the whole lookup, and a prop that
    /// is not meant to break simply does not carry this.
    ///
    /// It is also what keeps the bake honest: the model and the pieces it breaks
    /// into are bound together by the builder that cut them, so a rebake cannot
    /// leave the two pointing at different towers.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetworkBreakableProp : MonoBehaviour
    {
        [SerializeField]
        [Tooltip(
            "The shattered model this breaks into, as baked by Network Example/" +
            "Presentation/Build Tower Shatter Assets. Left empty, whatever the " +
            "effect has bound as its default is used.")]
        private GameObject shatteredModel;

        /// <summary>Null when this leaves the choice to the effect.</summary>
        public GameObject ShatteredModel => shatteredModel;

        public void Configure(GameObject model)
        {
            shatteredModel = model;
        }
    }
}
