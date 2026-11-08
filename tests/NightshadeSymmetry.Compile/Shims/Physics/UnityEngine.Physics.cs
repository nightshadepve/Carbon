// REFERENCE SHIM. UnityEngine.PhysicsModule.dll is not in this repository.

namespace UnityEngine
{
    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2
    }

    public struct RaycastHit
    {
    }

    public class Collider : Component
    {
    }

    public static class Physics
    {
        public static int DefaultRaycastLayers
        {
            get { return -1; }
        }

        public static bool Raycast(Ray ray, out RaycastHit hitInfo, float maxDistance, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            hitInfo = new RaycastHit();
            return false;
        }
    }
}
