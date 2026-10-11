using UnityEngine;

namespace Drone2.Building
{
    public enum PersonPose
    {
        Standing,
        Waving,
        SittingOnFurniture,
        SittingHeadInHands,
        SittingAgainstWall,
        HuddledKneesUp,
        CrouchingInCorner,
        LyingOnBack,
        LyingFaceDown,
        Kneeling,
    }

    /// Marks a person trapped in the building, for the drones to find.
    public class TrappedPerson : MonoBehaviour
    {
        public int id;
        public PersonPose pose;
        public int floor;
        public int roomId;
        public RoomRole roomRole;
        public bool found;

        /// World-space bounds of the person's body.
        public Bounds Bounds
        {
            get
            {
                var rs = GetComponentsInChildren<Renderer>();
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                return b;
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            var b = Bounds;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
