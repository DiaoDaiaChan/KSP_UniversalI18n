// UI18n · 影子编译桩（UnityEngine 侧）
// 这里只复刻 src/UI18n.Runtime 真正用到的那一小部分 API 形状。
// 它证明的是「我们自己的代码内部自洽」（成员名 / 参数类型 / 返回类型 / 调用关系），
// 不证明 Unity 的真实签名；桩与真机 API 不符时以真机编译为准（见 tests/runtime-compile/README.md）。
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public HideFlags hideFlags;
        public static void Destroy(Object obj) { }
        public static void DestroyImmediate(Object obj) { }
        public static T[] FindObjectsOfType<T>() where T : Object { return null; }
        public static void DontDestroyOnLoad(Object target) { }
        public int GetInstanceID() { return 0; }
        public static implicit operator bool(Object o) { return !ReferenceEquals(o, null); }
    }

    [Flags]
    public enum HideFlags { None = 0, HideInHierarchy = 1, HideAndDontSave = 61 }

    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform;
        public T GetComponent<T>() where T : Component { return null; }
    }

    public class Behaviour : Component
    {
        public bool enabled;
        public bool isActiveAndEnabled;
    }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(System.Collections.IEnumerator routine) { return null; }
        public void StopCoroutine(Coroutine c) { }
        public void StopAllCoroutines() { }
    }

    public class Coroutine : Object { }

    public class ScriptableObject : Object { }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { this.name = name; }
        public Transform transform;
        public bool activeSelf;
        public bool activeInHierarchy;
        public UnityEngine.SceneManagement.Scene scene;
        public void SetActive(bool value) { }
        public T AddComponent<T>() where T : Component { return null; }
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) where T : Component { return new T[0]; }
    }

    public class Transform : Component { }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return new Vector2(0f, 0f); } }
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(0f, 0f, 0f); } }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height)
        {
            this.x = x; this.y = y; this.width = width; this.height = height;
        }
        public float xMin { get { return x; } set { x = value; } }
        public float yMin { get { return y; } set { y = value; } }
        public float xMax { get { return x + width; } set { width = value - x; } }
        public float yMax { get { return y + height; } set { height = value - y; } }
        public Vector2 position { get { return new Vector2(x, y); } }
        public Vector2 size { get { return new Vector2(width, height); } }
        public bool Contains(Vector2 point) { return false; }
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white { get { return new Color(1f, 1f, 1f, 1f); } }
        public static Color black { get { return new Color(0f, 0f, 0f, 1f); } }
        public static Color clear { get { return new Color(0f, 0f, 0f, 0f); } }
        public static Color red { get { return new Color(1f, 0f, 0f, 1f); } }
        public static Color yellow { get { return new Color(1f, 1f, 0f, 1f); } }
        public static Color gray { get { return new Color(0.5f, 0.5f, 0.5f, 1f); } }
    }

    public class Texture : Object
    {
        public int width;
        public int height;
        public FilterMode filterMode;
    }

    public enum FilterMode { Point, Bilinear, Trilinear }

    public enum TextureFormat { Alpha8 = 1, RGB24 = 3, RGBA32 = 4, ARGB32 = 5, RGBAFloat = 17 }

    public struct Color32
    {
        public byte r, g, b, a;
        public Color32(byte r, byte g, byte b, byte a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static implicit operator Color(Color32 c) { return new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f); }
        public static implicit operator Color32(Color c) { return new Color32(0, 0, 0, 255); }
    }

    public class Texture2D : Texture
    {
        public Texture2D(int width, int height) { this.width = width; this.height = height; }
        public Texture2D(int width, int height, TextureFormat format, bool mipChain)
        {
            this.width = width; this.height = height; this.format = format;
        }
        public TextureFormat format;
        public bool LoadImage(byte[] data) { return false; }
        public void SetPixels32(Color32[] colors) { }
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public class Material : Object { public Shader shader; }

    public class Shader : Object { public static Shader Find(string name) { return null; } }

    public class Font : Object
    {
        public string[] fontNames;
        public int fontSize;
        public Material material;
        public bool dynamic;
        public FontStyle fontStyle;
        public static Font CreateDynamicFontFromOSFont(string fontname, int size) { return null; }
        public static Font CreateDynamicFontFromOSFont(string[] fontnames, int size) { return null; }
        public static string[] GetOSInstalledFontNames() { return null; }
        public bool HasCharacter(char c) { return false; }
    }

    public enum FontStyle { Normal, Bold, Italic, BoldAndItalic }

    public class AssetBundle : Object
    {
        public static AssetBundle LoadFromFile(string path) { return null; }
        public Object LoadAsset(string name) { return null; }
        public T LoadAsset<T>(string name) where T : Object { return null; }
        public Object[] LoadAllAssets() { return null; }
        public string[] GetAllAssetNames() { return null; }
        public void Unload(bool unloadAllLoadedObjects) { }
    }

    public class TextAsset : Object
    {
        public string text;
        public byte[] bytes;
    }

    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() where T : Object { return null; }
        public static Object Load(string path) { return null; }
    }

    public static class Application
    {
        public static string unityVersion;
        public static string version;
        public static string dataPath;
        public static string persistentDataPath;
        public static bool isPlaying;
        public static RuntimePlatform platform;
        public static void OpenURL(string url) { }
        public static void Quit() { }
    }

    public enum RuntimePlatform { WindowsPlayer, WindowsEditor, OSXPlayer, LinuxPlayer }

    public static class SystemInfo
    {
        public static string operatingSystem;
        public static string processorType;
        public static int processorCount;
        public static int systemMemorySize;
        public static string graphicsDeviceName;
        public static string deviceUniqueIdentifier;
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception) { }
    }

    public static class Mathf
    {
        public static float Floor(float f) { return 0f; }
        public static int FloorToInt(float f) { return 0; }
        public static float Ceil(float f) { return 0f; }
        public static int CeilToInt(float f) { return 0; }
        public static float Round(float f) { return 0f; }
        public static float Max(float a, float b) { return 0f; }
        public static int Max(int a, int b) { return 0; }
        public static float Min(float a, float b) { return 0f; }
        public static int Min(int a, int b) { return 0; }
        public static float Clamp(float value, float min, float max) { return 0f; }
        public static int Clamp(int value, int min, int max) { return 0; }
        public static float Clamp01(float value) { return 0f; }
        public static float Abs(float f) { return 0f; }
        public static int Abs(int f) { return 0; }
        public static float Sqrt(float f) { return 0f; }
        public static bool Approximately(float a, float b) { return false; }
    }

    public static class Time
    {
        public static float realtimeSinceStartup;
        public static float time;
        public static float deltaTime;
        public static float unscaledDeltaTime;
        public static int frameCount;
    }

    public static class Screen
    {
        public static int width;
        public static int height;
    }

    public enum KeyCode { None = 0, Escape = 27, Return = 13, F1 = 282, F2 = 283, LeftControl = 306 }

    public static class Input
    {
        public static Vector3 mousePosition;
        public static bool GetKeyDown(KeyCode key) { return false; }
        public static bool GetMouseButtonDown(int button) { return false; }
    }

    public class Event
    {
        public EventType type;
        public Vector2 mousePosition;
        public KeyCode keyCode;
        public int button;
        public static Event current;
        public void Use() { }
    }

    public enum EventType { MouseDown, MouseUp, MouseMove, KeyDown, KeyUp, ScrollWheel, Repaint, Layout, Ignore, Used }

    public enum TextAnchor
    {
        UpperLeft, UpperCenter, UpperRight,
        MiddleLeft, MiddleCenter, MiddleRight,
        LowerLeft, LowerCenter, LowerRight
    }

    public class RectOffset
    {
        public RectOffset() { }
        public RectOffset(int left, int right, int top, int bottom)
        {
            this.left = left; this.right = right; this.top = top; this.bottom = bottom;
        }
        public int left, right, top, bottom;
    }

    public class GUIStyleState
    {
        public Color textColor;
        public Texture2D background;
    }

    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public Font font;
        public int fontSize;
        public FontStyle fontStyle;
        public bool wordWrap;
        public bool richText;
        public TextAnchor alignment;
        public float fixedWidth;
        public float fixedHeight;
        public float lineHeight;
        public GUIStyleState normal = new GUIStyleState();
        public GUIStyleState hover = new GUIStyleState();
        public GUIStyleState active = new GUIStyleState();
        public GUIStyleState onNormal = new GUIStyleState();
        public GUIStyleState focused = new GUIStyleState();
        public RectOffset padding = new RectOffset();
        public RectOffset margin = new RectOffset();
        public RectOffset border = new RectOffset();
        public RectOffset overflow = new RectOffset();
        public static GUIStyle none { get { return new GUIStyle(); } }
        public Vector2 CalcSize(GUIContent content) { return Vector2.zero; }
        public float CalcHeight(GUIContent content, float width) { return 0f; }
        public void Draw(Rect position, GUIContent content, int controlID) { }
    }

    public class GUIContent
    {
        public GUIContent() { }
        public GUIContent(string text) { this.text = text; }
        public GUIContent(string text, string tooltip) { this.text = text; this.tooltip = tooltip; }
        public GUIContent(Texture image) { this.image = image; }
        public GUIContent(string text, Texture image) { this.text = text; this.image = image; }
        public GUIContent(string text, Texture image, string tooltip) { this.text = text; this.image = image; this.tooltip = tooltip; }
        public string text;
        public Texture image;
        public string tooltip;
        public static GUIContent none { get { return new GUIContent(); } }
    }

    public class GUISkin : Object
    {
        public Font font;
        public GUIStyle box = new GUIStyle();
        public GUIStyle button = new GUIStyle();
        public GUIStyle label = new GUIStyle();
        public GUIStyle textField = new GUIStyle();
        public GUIStyle window = new GUIStyle();
        public GUIStyle scrollView = new GUIStyle();
        public GUIStyle verticalScrollbar = new GUIStyle();
        public GUIStyle horizontalSlider = new GUIStyle();
        public GUIStyle toggle = new GUIStyle();
    }

    public class GUILayoutOption { }

    public static class GUI
    {
        public delegate void WindowFunction(int id);

        public enum ToolbarButtonSize { Fixed, FitToContents }

        public static GUISkin skin;
        public static Color color;
        public static Color backgroundColor;
        public static Color contentColor;
        public static bool enabled;
        public static int depth;

        public static void Label(Rect position, string text) { }
        public static void Label(Rect position, GUIContent content) { }
        public static void Label(Rect position, string text, GUIStyle style) { }
        public static void Label(Rect position, GUIContent content, GUIStyle style) { }
        public static void Box(Rect position, string text) { }
        public static void Box(Rect position, GUIContent content) { }
        public static void Box(Rect position, GUIContent content, GUIStyle style) { }
        public static bool Button(Rect position, string text) { return false; }
        public static bool Button(Rect position, GUIContent content) { return false; }
        public static bool Button(Rect position, string text, GUIStyle style) { return false; }
        public static bool Button(Rect position, GUIContent content, GUIStyle style) { return false; }
        public static bool RepeatButton(Rect position, string text) { return false; }
        public static bool RepeatButton(Rect position, GUIContent content) { return false; }
        public static bool Toggle(Rect position, bool value, string text) { return false; }
        public static bool Toggle(Rect position, bool value, GUIContent content) { return false; }
        public static bool Toggle(Rect position, bool value, string text, GUIStyle style) { return false; }
        public static bool Toggle(Rect position, bool value, GUIContent content, GUIStyle style) { return false; }
        public static string TextField(Rect position, string text) { return null; }
        public static string TextField(Rect position, string text, int maxLength) { return null; }
        public static string TextArea(Rect position, string text) { return null; }
        public static void TextArea(Rect position, string text, int maxLength) { }
        public static Rect Window(int id, Rect clientRect, WindowFunction func, string text) { return clientRect; }
        public static Rect Window(int id, Rect clientRect, WindowFunction func, GUIContent content) { return clientRect; }
        public static Rect Window(int id, Rect clientRect, WindowFunction func, string text, GUIStyle style) { return clientRect; }
        public static void DragWindow(Rect position) { }
        public static void DragWindow() { }
        public static void BringWindowToFront(int windowID) { }
        public static void FocusControl(string name) { }
        public static void UnfocusWindow() { }
        public static void BeginGroup(Rect position) { }
        public static void EndGroup() { }
        public static void BeginClip(Rect position) { }
        public static void EndClip() { }
        public static void DrawTexture(Rect position, Texture image) { }
        public static void ScrollTo(Rect position) { }
        public static int Toolbar(Rect position, int selected, string[] texts) { return selected; }
        public static int Toolbar(Rect position, int selected, string[] texts, GUIStyle style) { return selected; }
        public static int Toolbar(Rect position, int selected, GUIContent[] contents, GUIStyle style) { return selected; }
        public static int Toolbar(Rect position, int selected, GUIContent[] contents, GUIStyle style, ToolbarButtonSize buttonSize) { return selected; }
        public static int SelectionGrid(Rect position, int selected, string[] texts, int xCount) { return selected; }
        public static int SelectionGrid(Rect position, int selected, string[] texts, int xCount, GUIStyle style) { return selected; }
        public static float HorizontalSlider(Rect position, float value, float leftValue, float rightValue) { return value; }
        public static Vector2 BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect) { return scrollPosition; }
        public static Vector2 BeginScrollView(Rect position, Vector2 scrollPosition, Rect viewRect, bool alwaysShowHorizontal, bool alwaysShowVertical) { return scrollPosition; }
        public static void EndScrollView() { }
        public static void EndScrollView(bool handleScrollWheel) { }
    }

    public static class GUIUtility
    {
        public static string systemCopyBuffer;
        public static int GetControlID(FocusType focus, Rect position) { return 0; }
        public static int GetControlID(int hint, FocusType focus) { return 0; }
    }

    public enum FocusType { Native, Keyboard, Passive }

    public static class GUILayout
    {
        public static GUILayoutOption Width(float width) { return null; }
        public static GUILayoutOption Height(float height) { return null; }
        public static GUILayoutOption MinWidth(float minWidth) { return null; }
        public static GUILayoutOption MaxWidth(float maxWidth) { return null; }
        public static GUILayoutOption ExpandWidth(bool expand) { return null; }
        public static GUILayoutOption ExpandHeight(bool expand) { return null; }
        public static void Label(string text) { }
        public static void Label(GUIContent content) { }
        public static void Label(string text, GUIStyle style) { }
        public static void Label(GUIContent content, GUIStyle style, params GUILayoutOption[] options) { }
        public static void Box(string text) { }
        public static void Box(GUIContent content) { }
        public static void Box(GUIContent content, params GUILayoutOption[] options) { }
        public static bool Button(string text) { return false; }
        public static bool Button(GUIContent content) { return false; }
        public static bool Button(string text, params GUILayoutOption[] options) { return false; }
        public static bool Button(GUIContent content, params GUILayoutOption[] options) { return false; }
        public static bool RepeatButton(string text) { return false; }
        public static bool RepeatButton(GUIContent content, params GUILayoutOption[] options) { return false; }
        public static bool Toggle(bool value, string text) { return value; }
        public static bool Toggle(bool value, string text, params GUILayoutOption[] options) { return value; }
        public static string TextField(string text) { return text; }
        public static string TextField(string text, params GUILayoutOption[] options) { return text; }
        public static Rect Window(int id, Rect screenRect, GUI.WindowFunction func, string text) { return screenRect; }
        public static void Space(float pixels) { }
        public static void FlexibleSpace() { }
        public static void BeginHorizontal(params GUILayoutOption[] options) { }
        public static void BeginHorizontal(GUIStyle style, params GUILayoutOption[] options) { }
        public static void EndHorizontal() { }
        public static void BeginVertical(params GUILayoutOption[] options) { }
        public static void BeginVertical(GUIStyle style, params GUILayoutOption[] options) { }
        public static void EndVertical() { }
        public static void BeginArea(Rect screenRect) { }
        public static void EndArea() { }
        public static Vector2 BeginScrollView(Vector2 scrollPosition, params GUILayoutOption[] options) { return scrollPosition; }
        public static void EndScrollView() { }
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        public string name;
        public int buildIndex;
        public bool isLoaded;
        public bool IsValid() { return false; }
        public GameObject[] GetRootGameObjects() { return new GameObject[0]; }
    }

    public static class SceneManager
    {
        public static int sceneCount { get { return 0; } }
        public static Scene GetSceneAt(int index) { return default(Scene); }
    }
}
