// REFERENCE SHIM. This is not the Unity player.
// The repository does not contain UnityEngine.CoreModule.dll.
// Carbon recompiles the plugin against the real module on the server.
// This assembly exists only so the plugin can be type-checked here.

using System;

namespace UnityEngine
{
    public class ScriptableObject : Object
    {
    }

    public class Object
    {
        public static void Destroy(Object obj)
        {
        }

        public static bool operator ==(Object a, Object b)
        {
            return ReferenceEquals(a, b);
        }

        public static bool operator !=(Object a, Object b)
        {
            return !ReferenceEquals(a, b);
        }

        public override bool Equals(object other)
        {
            return ReferenceEquals(this, other);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() where T : class
        {
            return null;
        }
    }

    public class Behaviour : Component
    {
    }

    public class MonoBehaviour : Behaviour
    {
    }

    public class GameObject : Object
    {
        public Transform transform;
        public int layer;
        public bool activeSelf;

        public T GetComponent<T>() where T : class
        {
            return null;
        }

        public void SetActive(bool active)
        {
            activeSelf = active;
        }
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 forward;
        public Vector3 right;
        public Vector3 up;
        public Vector3 eulerAngles;

        public Vector3 TransformPoint(Vector3 local)
        {
            return local;
        }
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero
        {
            get { return new Vector3(0f, 0f, 0f); }
        }

        public static Vector3 up
        {
            get { return new Vector3(0f, 1f, 0f); }
        }

        public static Vector3 forward
        {
            get { return new Vector3(0f, 0f, 1f); }
        }

        public static Vector3 right
        {
            get { return new Vector3(1f, 0f, 0f); }
        }

        public float sqrMagnitude
        {
            get { return x * x + y * y + z * z; }
        }

        public float magnitude
        {
            get { return (float)Math.Sqrt(sqrMagnitude); }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        }

        public static Vector3 operator -(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        }

        public static Vector3 operator *(Vector3 a, float s)
        {
            return new Vector3(a.x * s, a.y * s, a.z * s);
        }

        public static Vector3 operator *(float s, Vector3 a)
        {
            return a * s;
        }

        public static float Distance(Vector3 a, Vector3 b)
        {
            return (a - b).magnitude;
        }
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity
        {
            get { return new Quaternion(0f, 0f, 0f, 1f); }
        }

        public Vector3 eulerAngles
        {
            get { return new Vector3(); }
            set { }
        }

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            return point;
        }

        public static float Angle(Quaternion a, Quaternion b)
        {
            return 0f;
        }
    }

    public struct Ray
    {
        public Vector3 origin;
        public Vector3 direction;

        public Ray(Vector3 origin, Vector3 direction)
        {
            this.origin = origin;
            this.direction = direction;
        }
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 size;
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;

        public Color(float r, float g, float b)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = 1f;
        }

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }

        public static Color magenta
        {
            get { return new Color(1f, 0f, 1f, 1f); }
        }

        public static Color white
        {
            get { return new Color(1f, 1f, 1f, 1f); }
        }
    }

    public static class Mathf
    {
        public static float Abs(float value)
        {
            return Math.Abs(value);
        }
    }

    public static class Time
    {
        public static float realtimeSinceStartup
        {
            get { return 0f; }
        }
    }

    public static class Debug
    {
        public static void Log(object message)
        {
        }
    }
}
