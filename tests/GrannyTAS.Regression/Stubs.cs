// Minimal external boundaries for testing the real source without a running
// IL2CPP engine. These do not simulate Unity's player loop or physics.
namespace UnityEngine
{
    public enum KeyCode
    {
        None, W, A, S, D, UpArrow, DownArrow, LeftArrow, RightArrow,
        Space, LeftShift, RightShift, LeftControl, RightControl, LeftAlt,
        E, Q, F, R, G, C, X, Z, V, B, T, H, Tab, Escape, Return,
        Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9, Alpha0,
        Insert, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Backspace,
        Mouse0 = 323, Mouse1, Mouse2, JoystickButton0 = 330
    }
    public static class Input
    {
        public static readonly HashSet<KeyCode> Keys = new();
        public static readonly HashSet<KeyCode> Pressed = new();
        public static readonly Dictionary<string, float> Axes = new();
        public static readonly Dictionary<string, float> RawAxes = new();
        public static bool GetKey(KeyCode k) => Keys.Contains(k);
        public static bool GetKeyDown(KeyCode k) => Pressed.Contains(k);
        public static bool GetKeyUp(KeyCode k) => false;
        public static bool GetMouseButton(int b) => false;
        public static bool GetMouseButtonDown(int b) => false;
        public static bool GetMouseButtonUp(int b) => false;
        public static float GetAxisRaw(string name) => RawAxes.TryGetValue(name, out var value)
            ? value : Axes.GetValueOrDefault(name);
        public static float GetAxis(string name) => Axes.GetValueOrDefault(name);
    }
    public static class Time
    {
        public static float fixedDeltaTime = .02f, maximumDeltaTime = .33f,
            timeScale = 1f, captureDeltaTime, deltaTime = 1f / 60f, time, realtimeSinceStartup;
    }
    public static class QualitySettings { public static int vSyncCount = 1; }
    public static class Application { public static int targetFrameRate = -1; public static string version = "test-build"; }
    public static class PlayerPrefs
    {
        public static readonly Dictionary<string, int> Ints = new();
        public static int GetInt(string key, int fallback = 0) => Ints.GetValueOrDefault(key, fallback);
        public static void SetInt(string key, int value) => Ints[key] = value;
        public static void Save() { }
    }
    public static class Random { public static void InitState(int seed) { } }
    public static class Mathf
    {
        public static float Clamp(float v, float lo, float hi) => Math.Clamp(v, lo, hi);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int RoundToInt(float v) => (int)MathF.Round(v);
        public static float Round(float v) => MathF.Round(v);
        public static float Abs(float v) => MathF.Abs(v);
    }
    public class Object
    {
        public static T FindObjectOfType<T>() where T : class => null;
        public static T[] FindObjectsOfType<T>() => GameObject.All
            .Where(go => go.activeInHierarchy)
            .SelectMany(go => go.Components.Where(component => component.gameObject == go))
            .OfType<T>().ToArray();
    }
    public class Component : Object
    {
        public Component()
        {
            gameObject = new GameObject();
            transform = gameObject.transform;
            gameObject.Attach(this);
        }
        public Transform transform;
        public GameObject gameObject;
    }
    public class Transform
    {
        private Transform _parent;
        internal readonly List<Transform> Children = new();
        public Transform() : this(null) { }
        internal Transform(GameObject owner) { gameObject = owner ?? new GameObject(false); }
        public string name;
        public Transform parent
        {
            get => _parent;
            set
            {
                _parent?.Children.Remove(this);
                _parent = value;
                _parent?.Children.Add(this);
            }
        }
        public GameObject gameObject;
        public Vector3 position;
        public Quaternion rotation;
        public Quaternion localRotation;
        public int GetSiblingIndex() => parent != null
            ? parent.Children.IndexOf(this)
            : GameObject.Roots.IndexOf(gameObject);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public float magnitude => MathF.Sqrt(x * x + y * y + z * z);
    }
    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x=x; this.y=y; this.z=z; this.w=w; }
        public static Quaternion identity => new(0, 0, 0, 1);
        public static Quaternion Euler(float x, float y, float z)
        {
            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(y * MathF.PI / 180f, x * MathF.PI / 180f, z * MathF.PI / 180f);
            return new Quaternion(q.X, q.Y, q.Z, q.W);
        }
        public static Quaternion operator *(Quaternion a, Quaternion b)
        {
            var q = System.Numerics.Quaternion.Multiply(
                new System.Numerics.Quaternion(a.x, a.y, a.z, a.w),
                new System.Numerics.Quaternion(b.x, b.y, b.z, b.w));
            return new Quaternion(q.X, q.Y, q.Z, q.W);
        }
    }
    public class GameObject : Object
    {
        internal static readonly List<GameObject> All = new();
        internal static readonly List<GameObject> Roots = new();
        internal readonly List<Component> Components = new();
        public GameObject(bool createTransform = true)
        {
            if (createTransform)
            {
                transform = new Transform(this);
                All.Add(this);
                Roots.Add(this);
            }
        }
        public Transform transform;
        public bool activeInHierarchy = true;
        internal void Attach(Component component)
        {
            if (!Components.Contains(component)) Components.Add(component);
        }
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T();
            component.gameObject = this;
            component.transform = transform;
            Attach(component);
            return component;
        }
        public T GetComponent<T>() where T : class => Components.OfType<T>().FirstOrDefault();
        public T[] GetComponents<T>() => Components.OfType<T>().ToArray();
        public static GameObject Find(string path) => null;
    }
    public class Camera : Component { public bool isActiveAndEnabled = true; }
    public class Animator : Component { }
    public class CharacterController : Component { public bool enabled; }
    [Flags]
    public enum RigidbodyConstraints
    {
        None = 0, FreezePositionX = 2, FreezePositionY = 4, FreezePositionZ = 8,
        FreezeRotationX = 16, FreezeRotationY = 32, FreezeRotationZ = 64,
        FreezePosition = 14, FreezeRotation = 112, FreezeAll = 126,
    }
    public class Rigidbody : Component
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 velocity;
        public Vector3 angularVelocity;
        public bool isKinematic;
        public bool useGravity = true;
        public bool detectCollisions = true;
        public RigidbodyConstraints constraints;
        private bool _sleeping;
        public bool IsSleeping() => _sleeping;
        public void Sleep() => _sleeping = true;
        public void WakeUp() => _sleeping = false;
    }
}
namespace UnityEngine.AI
{
    public class NavMeshAgent : UnityEngine.Component
    {
        public bool enabled;
        public bool Warp(UnityEngine.Vector3 p) => true;
        public void ResetPath() { }
    }
    public struct NavMeshHit { }
    public static class NavMesh
    {
        public const int AllAreas = -1;
        public static bool SamplePosition(UnityEngine.Vector3 sourcePosition, out NavMeshHit hit, float maxDistance, int areaMask)
        {
            hit = default;
            return true;
        }
    }
}
namespace Il2Cpp
{
    public class Paused : UnityEngine.Component { public static bool IsPaused; public void RestartP() { } }
    public class SeedManager { public int Seed; public bool RandomizeSeed; public void GeneratePlacement() { } }
    public class MobileFPS : UnityEngine.Component
    {
        /// <summary>The controller the position pin has to disable to move the player.</summary>
        public UnityEngine.CharacterController Controller = new() { enabled = true };
        public T GetComponent<T>() where T : class => Controller as T;

        public float rotationX;
        public bool enabled = true, isAllowedToMove = true, AbleToMove = true, CamK = true;
        public UnityEngine.Transform playerCamera = new();
        public UnityEngine.Camera playerCamera2 = new();
        public PlayerStatus PS = new();
        public Days SystemDay;
        public float cameraRotationSpeed = 1f, minXRotation = -45f, maxXRotation = 45f;
        public int UpdateCalls, TouchInputCalls;
        public void GetTouchInput()
        {
            TouchInputCalls++;
            var yaw = UnityEngine.Input.GetAxis("Mouse X") * cameraRotationSpeed;
            rotationX = UnityEngine.Mathf.Clamp(rotationX - UnityEngine.Input.GetAxis("Mouse Y") * cameraRotationSpeed,
                minXRotation, maxXRotation);
            playerCamera.localRotation = UnityEngine.Quaternion.Euler(rotationX, 0, 0);
            transform.rotation = transform.rotation * UnityEngine.Quaternion.Euler(0, yaw, 0);
        }
        public void Update() { UpdateCalls++; GetTouchInput(); }
    }
    public class PlayerStatus { public bool IsJumpscared, Killed; }
    public class Days { public UnityEngine.Animator PlayerBedAnim; }
    public class FallingHolder { public void Update() { } }
    public class PickRay { public void Update() { } }
    public class DoorRay { public void Update() { } }
    public class AI_Granny : UnityEngine.Component { public void StopChase() { } public void ResetAIDecision() { } }
    public class AI_Grandpa : AI_Granny { }
    public class AI_MomSpider : UnityEngine.Component { }
    public class AI_Slendrina : UnityEngine.Component { }
    public class AtticSpider : UnityEngine.Component { }
    public class RatEnemy : UnityEngine.Component { }
    public class SnowManAI : UnityEngine.Component { }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple=true)]
    public class HarmonyPatch : Attribute { public HarmonyPatch(params object[] args) { } }
    public static class AccessTools
    {
        public static System.Reflection.MethodInfo Method(Type t, string name) => t.GetMethod(name);
    }
}
namespace MelonLoader
{
    public class MelonLogger
    {
        public class Instance
        {
            public void Warning(string text) { }
            public void Msg(string text) { }
            public void Error(string text) { }
        }
    }
    public class MelonPreferences_Entry<T>
    {
        private readonly T _default;
        public T Value;
        public MelonPreferences_Entry(T value) { Value = value; _default = value; }
        public void ResetToDefault() => Value = _default;
    }
    public class MelonPreferences_Category
    {
        private readonly Dictionary<string, object> _entries = new();
        public MelonPreferences_Entry<T> CreateEntry<T>(string name, T value, string description = null)
        {
            var entry = new MelonPreferences_Entry<T>(value);
            _entries[name] = entry;
            return entry;
        }
        public void SetFilePath(string path, bool save) { }
        public void LoadFromFile(bool save)
        {
            foreach (var (key, value) in MelonPreferences.Loaded)
                _entries[key].GetType().GetField("Value").SetValue(_entries[key], value);
        }
    }
    public static class MelonPreferences
    {
        public static readonly Dictionary<string, object> Loaded = new();
        public static bool FailSave;
        public static int Saves;
        public static MelonPreferences_Category CreateCategory(string name, string label) => new();
        public static void Save() { Saves++; if (FailSave) throw new IOException("test disk error"); }
    }
}
namespace MelonLoader.Utils
{
    public static class MelonEnvironment
    {
        public static string UserDataDirectory = Path.Combine(Path.GetTempPath(), "tas-test-" + Guid.NewGuid().ToString("N"));
    }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager { public static Scene GetActiveScene() => new() { name = "test_scene" }; }
}
namespace GrannyTAS
{
    public sealed class FakeMacroGameSetup : IMacroGameSetup
    {
        public bool RestartRequired, BuildMatches = true, LoadedMatches = true;
        public int ApplyCalls;
        public float Now;
        public string RestartReason = "difficulty differs", LoadedReason = "difficulty differs";
        public float Realtime => Now;
        public void Capture(MacroFile macro) { macro.HasSetupMetadata = true; macro.GameBuild = "test-build"; }
        public bool RequiresRestart(MacroFile macro, out string reason) { reason = RestartReason; return RestartRequired; }
        public bool CanReplayBuild(MacroFile macro, out string reason) { reason = "game build mismatch"; return BuildMatches; }
        public void ApplyAndRestart(MacroFile macro) { ApplyCalls++; }
        public bool LoadedSetupMatches(MacroFile macro, out string reason) { reason = LoadedReason; return LoadedMatches; }
        public void CancelPending() { }

        // Default true so existing tests keep meaning "setup is verified" —
        // the real implementation defaults false until the accessors exist.
        public bool VerifiesLevelSetup { get; set; } = true;
    }

    public sealed class GrannyTasMod
    {
        public static GrannyTasMod Instance { get; set; }
        public MacroEngine Macro { get; set; }
    }
}
