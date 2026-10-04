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
        Insert, Home, End, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, Backspace,
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
        public static double timeAsDouble, fixedTimeAsDouble;
        public static int frameCount;
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
    public static class Physics
    {
        public static bool autoSyncTransforms;
        public static int SyncCalls;
        public static void SyncTransforms() => SyncCalls++;

        /// <summary>Test hook: what a ray from an origin hits, or null for nothing.</summary>
        public static Func<Vector3, string> HitAt = _ => null;

        /// <summary>Test hook: how far along the ray any hit is.</summary>
        public static float HitDistance;

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance)
        {
            var name = HitAt(origin);
            hit = name == null ? default : new RaycastHit { collider = ColliderNamed(name), distance = HitDistance };
            return name != null;
        }

        public static RaycastHit[] RaycastAll(Vector3 origin, Vector3 direction, float maxDistance) =>
            Raycast(origin, direction, out var hit, maxDistance) ? new[] { hit } : Array.Empty<RaycastHit>();

        // The collider of a live object by that name, so identity checks see
        // the real object; otherwise a stand-in that is part of nothing.
        private static Collider ColliderNamed(string name) =>
            GameObject.All.LastOrDefault(go => go.activeInHierarchy && go.name == name)?.GetComponent<Collider>()
            ?? new Collider { gameObject = new GameObject(false) { name = name } };
    }
    public struct RaycastHit { public Collider collider; public float distance; }
    public struct Bounds { public Vector3 center, extents; }
    public class Collider : Component
    {
        public Collider() : base(detached: true) { }
        public bool enabled = true;
        /// <summary>Test hook: half-size of the box, centred on the transform.</summary>
        public Vector3 Extents;
        public Bounds bounds => new() { center = transform?.position ?? default, extents = Extents };
    }
    public class Object
    {
        public static bool DeferDestroy;
        public static readonly List<GameObject> PendingDestroy = new();
        public static T FindObjectOfType<T>() where T : class => FindObjectsOfType<T>().FirstOrDefault();
        public static T[] FindObjectsOfType<T>() => GameObject.All
            .Where(go => go.activeInHierarchy)
            .SelectMany(go => go.Components.Where(component => component.gameObject == go))
            .OfType<T>().ToArray();
        public static void Destroy(Object target)
        {
            if (target is not GameObject go) return;
            if (DeferDestroy) PendingDestroy.Add(go);
            else go.activeInHierarchy = false;
        }
        public static void FlushDestroy()
        {
            foreach (var go in PendingDestroy) go.activeInHierarchy = false;
            PendingDestroy.Clear();
        }
    }
    public class Component : Object
    {
        public Component()
        {
            gameObject = new GameObject();
            transform = gameObject.transform;
            gameObject.Attach(this);
        }
        // For a component given its GameObject by AddComponent or the caller.
        protected Component(bool detached) { }
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
        public Quaternion rotation = Quaternion.identity;
        public Vector3 localPosition;
        public Quaternion localRotation = Quaternion.identity;
        public Vector3 localScale = new(1, 1, 1);
        public Vector3 forward => Vector3.forward;
        public int childCount => Children.Count;
        public Transform GetChild(int index) => Children[index];
        public bool IsChildOf(Transform ancestor)
        {
            for (var t = this; t != null; t = t.parent) if (t == ancestor) return true;
            return false;
        }
        public int SetPoseCalls;
        // No hierarchy maths: a pinned world pose shows up in the local fields
        // too, which is what the pin's restore has to undo.
        public void SetPositionAndRotation(Vector3 p, Quaternion q)
        {
            SetPoseCalls++;
            position = p; rotation = q;
            localPosition = new Vector3(p.x + 100, p.y, p.z);
            localRotation = q;
        }
        public int GetSiblingIndex() => parent != null
            ? parent.Children.IndexOf(this)
            : GameObject.Roots.IndexOf(gameObject);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
        public static Vector3 forward => new(0, 0, 1);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float d) => new(a.x * d, a.y * d, a.z * d);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
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
        public bool activeSelf = true;
        public SceneManagement.Scene scene = SceneManagement.SceneManager.Active;
        private string _name;
        public string name { get => transform?.name ?? _name; set { if (transform != null) transform.name = value; else _name = value; } }
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
        public T GetComponentInChildren<T>() where T : class => GetComponent<T>();
        public T[] GetComponents<T>() => Components.OfType<T>().ToArray();
        public void SetActive(bool value) { activeSelf = value; activeInHierarchy = value; }
        public static GameObject Find(string path) => null;
    }
    public class Camera : Component { public bool isActiveAndEnabled = true; }
    public class AnimationState
    {
        public string name = "";
        public bool enabled;
        public float weight, time, speed = 1f;
    }
    public class Animation : Component
    {
        public readonly List<AnimationState> States = new();
        public int SampleCalls;
        public int GetClipCount() => States.Count;
        public AnimationState GetStateAtIndex(int index) => States[index];
        public AnimationState this[string name] => States.FirstOrDefault(s => s.name == name);
        public void Sample() => SampleCalls++;
    }
    public class Animator : Component { }
    public class CharacterController : Component
    {
        public bool enabled;
        public bool isGrounded;
        public Vector3 velocity;
        public float height = 2f, radius = .5f;
        public Vector3 center;
    }
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
        public bool isOnNavMesh = true;
        public UnityEngine.Vector3 velocity;
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
        /// <summary>The player's controller, attached to its own GameObject like the real one.</summary>
        public UnityEngine.CharacterController Controller;
        public UnityEngine.CharacterController characterController => Controller;
        public T GetComponent<T>() where T : class => Controller as T;

        public MobileFPS()
        {
            Controller = gameObject.AddComponent<UnityEngine.CharacterController>();
            Controller.enabled = true;
        }

        public float rotationX;
        public bool enabled = true, isAllowedToMove = true, AbleToMove = true, CamK = true;
        public bool IsCrouched, isMoving, CanFade, InWeb, WasOnPlatform, RotateXReser;
        public UnityEngine.Vector3 moveDirection;
        public float moveSpeed;
        public UnityEngine.Animation CameraAnim;
        public FallingHolder FallingHolder;
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
    public class WindowJumping { public bool IsJumping; }
    public class Days { public UnityEngine.Animator PlayerBedAnim; }
    public class FallingHolder : UnityEngine.Component
    {
        public bool isFalling, isLanding, Fell, Damaged, DeathFall, CanFallSound, CanCamSmooth;
        public float fallDuration, DurateCan;
        public UnityEngine.Animation playerAnimation;
        public void Update() { }
    }
    public class CrouchHolder : UnityEngine.Component
    {
        public bool On1, On2, isCrouching, IsCrouched, Starter, Disabled, IsBelow;
        public UnityEngine.Animation Cam, Anim;
    }
    public class PickRay : UnityEngine.Component
    {
        public UnityEngine.GameObject Player;
        public Inventory Inventory;
        public UnityEngine.GameObject Ring;
        public bool buttonClicked;
        public UnityEngine.KeyCode MainInteract = UnityEngine.KeyCode.E;
        public WindowJumping WJ = new();
        public PlayerStatus PlayerStatus = new();
        public UnityEngine.GameObject H_CR = new(), H_DSH = new(), H_FT = new(), H_PTRAP = new(), H_SG = new();
        public UnityEngine.GameObject Drop1 = new(), ItemDrop = new(), ShotgunHandMain = new(),
            ShotgunHandPipes = new(), O_FT = new(), O_PTRAP = new();
        public UnityEngine.Transform DropP = new(), DropPFreezeTrap = new();
        public float RaycastDis = 2f, RaycastCheckItemDis = 3f;
        public int CheckDropCalls, ShotgunCalls;
        public void CheckItemDropping() { CheckDropCalls++; Inventory?.DropLogic(); }
        public void PickShotgun() => ShotgunCalls++;
        public void Update() { }
    }
    public class ItemDefs
    {
        public string itemName;
        public UnityEngine.GameObject handObject;
    }
    public class ItemSeedData : UnityEngine.Component { public string itemName; }
    public class Inventory : UnityEngine.Component
    {
        public readonly List<ItemDefs> ItemDefs = new();
        public int DropCalls, PickupCalls;
        public ItemDefs GetItemDefByName(string name) => ItemDefs.FirstOrDefault(item => item.itemName == name);
        public void DropLogic()
        {
            DropCalls++;
            foreach (var item in ItemDefs) item.handObject?.SetActive(false);
        }
        public void PickupItem(string name)
        {
            PickupCalls++;
            DropLogic();
            GetItemDefByName(name)?.handObject?.SetActive(true);
            GrannyTAS.SyncTracker.OnPickup(name);
        }
    }
    public class DoorRay : UnityEngine.Component
    {
        public float RaycastDis = 2f;
        public void Update() { }
    }
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
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int Last = 0, Normal = 400, First = 800; }
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
            public readonly List<string> Lines = new();
            public void Warning(string text) => Lines.Add("W " + text);
            public void Msg(string text) => Lines.Add("M " + text);
            public void Error(string text) => Lines.Add("E " + text);
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
    public struct Scene { public string name; public int handle; }
    public static class SceneManager
    {
        public static readonly Scene Active = new() { name = "test_scene", handle = 1 };
        public static readonly List<Scene> Loaded = new() { Active };
        public static int sceneCount => Loaded.Count;
        public static Scene GetSceneAt(int index) => Loaded[index];
        public static Scene GetActiveScene() => Active;
    }
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
