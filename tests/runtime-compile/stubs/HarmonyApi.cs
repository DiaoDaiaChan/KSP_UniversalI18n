// UI18n · 影子编译桩（HarmonyLib 侧）
// 只复刻我们实际用到的 Harmony 2 面：Harmony / HarmonyMethod / HarmonyPatch / AccessTools / CodeInstruction。
// 注意：桩不验证 Harmony 的真实签名（那是真机编译的事），只验证我们自己的 17 个 patch 描述能被逐条核对。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace HarmonyLib
{
    public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor }

    public enum HarmonyPatchType { All, Prefix, Postfix, Transpiler, Finalizer, ReversePatch }

    public enum Priority { First, High, Normal, Low, Last }

    public class HarmonyMethod
    {
        public HarmonyMethod() { }
        public HarmonyMethod(MethodInfo method) { this.method = method; }
        public HarmonyMethod(Type type, string name, Type[] argumentTypes = null) { }

        public MethodInfo method;
        public Type declaringType;
        public string methodName;
        public Type[] argumentTypes;
        public int priority = -1;
        public string[] before;
        public string[] after;
        public bool? debug;
    }

    public class Harmony
    {
        public const string harmonyId = "ui18n.runtime";

        public Harmony(string id) { this.Id = id; }

        public string Id { get; private set; }

        public MethodInfo Patch(MethodInfo original,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null)
        { return original; }

        public MethodInfo Patch(MethodBase original,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null)
        { return null; }

        public void PatchAll(Assembly assembly) { }
        public void PatchAll(Type type) { }
        public void UnpatchAll(string harmonyID = null) { }
        public void Unpatch(MethodBase original, MethodType type, string harmonyID = null) { }
        public void Unpatch(MethodBase original, HarmonyPatchType type, string harmonyID = null) { }
        public void UnpatchSelf() { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type declaringType) { }
        public HarmonyPatch(Type declaringType, string methodName) { }
        public HarmonyPatch(Type declaringType, string methodName, Type[] argumentTypes) { }
        public HarmonyPatch(string typeName, string methodName) { }
        public HarmonyPatch(Type declaringType, MethodType methodType) { }
        public HarmonyPatch(Type declaringType, MethodType methodType, Type[] argumentTypes) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPriority : Attribute
    {
        public HarmonyPriority(int priority) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPrefix : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class HarmonyPostfix : Attribute { }

    public static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] parameters = null, Type[] generics = null) { return null; }
        public static MethodInfo Method(string typeColonMethodname, Type[] parameters = null, Type[] generics = null) { return null; }
        public static MethodInfo DeclaredMethod(Type type, string name, Type[] parameters = null, Type[] generics = null) { return null; }
        public static ConstructorInfo Constructor(Type type, Type[] parameters = null) { return null; }
        public static FieldInfo Field(Type type, string name) { return null; }
        public static PropertyInfo Property(Type type, string name) { return null; }
        public static Type TypeByName(string name) { return null; }
        public static Type InnerType(Type type, string name) { return null; }
        public static MethodInfo[] AllMethods(Type type) { return null; }
        public static Type[] AllTypes(Assembly assembly) { return null; }
        public static object FieldRefAccess<T, F>(string fieldName) { return null; }
    }

    public class CodeInstruction
    {
        public CodeInstruction(OpCode opcode) { this.opcode = opcode; }
        public CodeInstruction(OpCode opcode, object operand) { this.opcode = opcode; this.operand = operand; }
        public CodeInstruction(OpCode opcode, object operand, params object[] labels) { this.opcode = opcode; this.operand = operand; }

        public OpCode opcode;
        public object operand;
        public List<Label> labels = new List<Label>();

        public CodeInstruction WithLabels(params Label[] labels) { return this; }
        public CodeInstruction WithLabels(IEnumerable<Label> labels) { return this; }
        public CodeInstruction MoveLabelsTo(CodeInstruction other) { return this; }
        public CodeInstruction MoveLabelsFrom(CodeInstruction other) { return this; }
        public CodeInstruction Clone() { return this; }
        public CodeInstruction Clone(OpCode opcode) { return this; }
    }
}
