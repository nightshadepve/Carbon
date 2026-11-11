namespace UnityEngine.UI
{
    public class ScrollRect : UnityEngine.MonoBehaviour
    {
        public enum MovementType { Unrestricted = 0, Elastic = 1, Clamped = 2 }
        public MovementType movementType;
    }
}
