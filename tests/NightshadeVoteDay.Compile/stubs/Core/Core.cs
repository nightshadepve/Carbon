using System;
using System.Collections;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public HideFlags hideFlags;
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
        public int GetInstanceID() { return 0; }
    }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() where T : class { return null; }
        public T GetComponentInParent<T>() where T : class { return null; }
        public T GetComponentInChildren<T>() where T : class { return null; }
    }

    public class Behaviour : Component
    {
        public bool enabled;
    }

    public class MonoBehaviour : Behaviour
    {
        public void Invoke(string methodName, float time) { }
        public void InvokeRepeating(string methodName, float time, float repeatRate) { }
        public void CancelInvoke() { }
        public void CancelInvoke(string methodName) { }
        public bool IsInvoking(string methodName) { return false; }
        public Coroutine StartCoroutine(IEnumerator routine) { return null; }
        public void StopAllCoroutines() { }
    }

    public class GameObject : Object
    {
        public T GetComponent<T>() where T : class { return null; }
        public T AddComponent<T>() where T : class { return null; }
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localScale;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero;
        public static Vector3 up;
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity;
    }

    public struct Ray
    {
        public Vector3 origin;
        public Vector3 direction;
    }

    public struct Color
    {
        public float r, g, b, a;
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 size;
    }

    public sealed class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
    }

    public sealed class Time
    {
        public static float time;
        public static float realtimeSinceStartup;
        public static float deltaTime;
    }

    public sealed class Random
    {
        public static int Range(int min, int max) { return min; }
        public static float Range(float min, float max) { return min; }
    }

    public sealed class Mathf
    {
        public static int Clamp(int value, int min, int max) { return value; }
        public static float Clamp(float value, float min, float max) { return value; }
        public static int RoundToInt(float value) { return (int)Math.Round(value); }
        public static int CeilToInt(float value) { return (int)Math.Ceiling(value); }
        public static float Max(float a, float b) { return a > b ? a : b; }
    }

    public enum HideFlags { None = 0 }
    public enum SendMessageOptions { RequireReceiver = 0 }
    public enum Space { World = 0, Self = 1 }
    public class Coroutine { }
    public struct LayerMask { public int value; }
    public class ScriptableObject : Object { }
}
