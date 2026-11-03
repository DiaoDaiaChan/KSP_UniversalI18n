// UI18n · 影子编译桩（KSP 侧）
// 只复刻我们真正调用到的 KSP API：ConfigNode / HighLogic / FlightGlobals / ScreenMessages /
// ApplicationLauncher / KSPAddon。目标是让我们自己的调用关系能被编译器逐条核对。
using System;
using System.Collections.Generic;
using UnityEngine;

public class ConfigNode
{
    public ConfigNode() { }
    public ConfigNode(string name) { this.name = name; }
    public string name;
    public List<ConfigNode> nodes = new List<ConfigNode>();
    public string[] values = new string[0];

    public static ConfigNode Load(string fileFullName) { return null; }
    public bool Save(string fileFullName) { return false; }
    public bool HasNode(string name) { return false; }
    public ConfigNode GetNode(string name) { return null; }
    public ConfigNode[] GetNodes(string name) { return null; }
    public bool HasValue(string name) { return false; }
    public string GetValue(string name) { return null; }
    public string GetValue(string name, string defaultValue) { return null; }
    public bool SetValue(string name, string value) { return false; }
    public bool AddValue(string name, string value) { return false; }
    public ConfigNode AddNode(ConfigNode node) { return null; }
    public ConfigNode AddNode(string name) { return null; }
}

public enum GameScenes
{
    LOADING, MAINMENU, SETTINGS, CREDITS, EDITOR, SPACECENTER, FLIGHT, TRACKSTATION, PSSCENE
}

public class Game
{
    public string Title { get; set; }
    public string Flag { get; set; }
}

public static class HighLogic
{
    public static GameScenes LoadedScene;
    public static Game CurrentGame;
    public static string SaveFolder;
    public static UnityEngine.GUISkin Skin;
}

public class GameDatabase
{
    public static GameDatabase Instance;
    public Texture2D GetTexture(string name, bool asNormal) { return null; }
}

public class ProtoCrewMember
{
    public string name;
    public string experienceTrait;
}

public class Vessel : UnityEngine.Object
{
    public string vesselName;
    public Guid id;
    public List<ProtoCrewMember> GetVesselCrew() { return null; }
}

public static class FlightGlobals
{
    public static Vessel ActiveVessel;
    public static List<Vessel> VesselsLoaded = new List<Vessel>();
}

public enum ScreenMessageStyle { UPPER_LEFT, UPPER_CENTER, UPPER_RIGHT, LOWER_LEFT, LOWER_CENTER, LOWER_RIGHT }

public class ScreenMessage
{
    public string message;
    public float duration;
    public ScreenMessageStyle style;
    public Color color;
}

public class ScreenMessagesText : UnityEngine.Object
{
    public string text;
}

public static class ScreenMessages
{
    public static void PostMessage(ScreenMessage msg, string message, ScreenMessagesText textPrefab, float duration, ScreenMessageStyle style, Color color) { }
    public static void PostScreenMessage(string message, float duration, ScreenMessageStyle style) { }
    public static void PostScreenMessage(string message, float duration) { }
}

namespace KSP
{
    namespace UI
    {
        namespace Screens
        {
            public class ApplicationLauncherButton : UnityEngine.Object
            {
                public delegate void Callback();

                public bool enabled;
                public void Enable() { }
                public void Disable() { }
            }

            public class ApplicationLauncher
            {
                public enum AppScenes
                {
                    NONE = 0, ALWAYS = 1, SPACECENTER = 2, FLIGHT = 4, EDITOR = 8,
                    TRACKSTATION = 16, MAINMENU = 32
                }

                public static ApplicationLauncher Instance;

                public ApplicationLauncherButton AddModApplication(
                    ApplicationLauncherButton.Callback onTrue,
                    ApplicationLauncherButton.Callback onFalse,
                    ApplicationLauncherButton.Callback onHover,
                    ApplicationLauncherButton.Callback onLeave,
                    ApplicationLauncherButton.Callback onEnable,
                    ApplicationLauncherButton.Callback onDisable,
                    AppScenes visibleInScenes,
                    Texture2D texture) { return null; }

                public void RemoveModApplication(ApplicationLauncherButton button) { }
            }
        }
    }
}

public static class KSPUtil
{
    public static string ApplicationRootPath;
    public static string PrintDate(DateTime date) { return null; }
}

public static class Versioning
{
    public static int version_major;
    public static int version_minor;
    public static int Revision;
    public static string BaseVersion;
}

public class GameParameters
{
    public string Difficulty;
}

public class KSPAddon : Attribute
{
    public enum Startup { Instantly, PSystemReady, MainMenu, Credits, Settings, SpaceCenter, Editor, Flight, TrackStation, Any }

    public KSPAddon(Startup startup, bool once) { this.startup = startup; this.once = once; }

    public Startup startup;
    public bool once;
}

namespace KSP
{
    namespace Localization
    {
        public static class Localizer
        {
            public static string CurrentLanguage;
            public static string Format(string template) { return template; }
            public static string Format(string template, params object[] args) { return template; }
            public static string GetStringByTag(string tag) { return null; }
        }
    }
}

public class KSPField : Attribute
{
    public string guiName;
    public bool isPersistant;
}

public class KSPEvent : Attribute
{
    public string guiName;
    public bool guiActive;
}
