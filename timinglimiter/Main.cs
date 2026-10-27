using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;
using HarmonyLib;

namespace TimingLimiter
{
    public static class Main
    {
        private const string HarmonyId = "Wonerd.TimingLimiter";
        private const double Pi = Math.PI;
        private const double TwoPi = Math.PI * 2.0;

        private static UnityModManager.ModEntry mod;
        private static bool enabled = true;

        private static double limitMs = 30.0;
        private static string limitText = "30";
        private static string settingsPath;

        private static Harmony harmonyInstance;

        [ThreadStatic]
        private static bool pendingLimitResult;

        [ThreadStatic]
        private static bool pendingOverLimit;

        [ThreadStatic]
        private static double pendingSignedMs;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            LoadSettings();

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;

            if (!InstallPatches())
            {
                LogError("Could not install timing patches. TimingLimiter is inactive.");
                return false;
            }

            Log("TimingLimiter 0.1.1 loaded. Limit: ±" +
                limitMs.ToString("0.##", CultureInfo.InvariantCulture) + " ms");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry entry, bool value)
        {
            enabled = value;

            // Never let a pending hit from before a toggle affect the next one.
            pendingLimitResult = false;
            pendingOverLimit = false;
            pendingSignedMs = 0.0;

            return true;
        }

        private static bool OnUnload(UnityModManager.ModEntry entry)
        {
            enabled = false;
            pendingLimitResult = false;

            try
            {
                if (harmonyInstance != null)
                {
                    MethodInfo unpatchAll =
                        typeof(Harmony).GetMethod(
                            "UnpatchAll",
                            BindingFlags.Public |
                            BindingFlags.Instance |
                            BindingFlags.Static);

                    if (unpatchAll != null)
                    {
                        ParameterInfo[] p =
                            unpatchAll.GetParameters();

                        object target =
                            unpatchAll.IsStatic
                                ? null
                                : (object)harmonyInstance;

                        object[] args =
                            p.Length == 0
                                ? null
                                : new object[] { HarmonyId };

                        unpatchAll.Invoke(
                            target,
                            args);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Unpatch error: " + ex.Message);
            }

            harmonyInstance = null;
            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            try
            {
                RGui.Label("TimingLimiter 0.1.1 - ADOFAI 3.4.0");
                RGui.Label("Limits accepted manual hits to a custom ±ms range.");
                RGui.Label("This mod only narrows the game's normal timing window.");
                RGui.Space(8f);

                RGui.Label("Timing limit (±ms)");
                limitText = RGui.TextField(limitText);

                if (RGui.Button("Apply timing limit"))
                {
                    double parsed;
                    if (TryParseLimit(limitText, out parsed))
                    {
                        limitMs = Clamp(parsed, 0.1, 1000.0);
                        limitText =
                            limitMs.ToString(
                                "0.##",
                                CultureInfo.InvariantCulture);

                        SaveSettings();

                        Log("Timing limit changed to ±" +
                            limitText + " ms");
                    }
                    else
                    {
                        limitText =
                            limitMs.ToString(
                                "0.##",
                                CultureInfo.InvariantCulture);
                    }
                }

                RGui.Space(5f);
                RGui.Label(
                    "Current limit: ±" +
                    limitMs.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture) +
                    " ms");

                RGui.Label(
                    "Autoplay/auto tiles keep ADOFAI's own behavior. No-fail mode remains no-fail.");
            }
            catch (Exception ex)
            {
                Log("GUI error: " + ex.Message);
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            double parsed;
            if (TryParseLimit(limitText, out parsed))
            {
                limitMs = Clamp(parsed, 0.1, 1000.0);
                limitText =
                    limitMs.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture);
            }

            SaveSettings();
        }

        // Prefix for scrMisc.GetHitMargin(...).
        // We calculate the actual input timing error in milliseconds from the
        // same angle/BPM/pitch values ADOFAI feeds into its judgement function.
        private static void GetHitMarginPrefix(object[] __args)
        {
            pendingLimitResult = false;
            pendingOverLimit = false;
            pendingSignedMs = 0.0;

            if (!enabled ||
                __args == null ||
                __args.Length < 5)
                return;

            // Do not interfere with the game's autoplay mode.
            if (GameBridge.IsAutoPlay())
                return;

            try
            {
                double angle =
                    Convert.ToDouble(
                        __args[0],
                        CultureInfo.InvariantCulture);

                double targetAngle =
                    Convert.ToDouble(
                        __args[1],
                        CultureInfo.InvariantCulture);

                bool isCW =
                    Convert.ToBoolean(
                        __args[2],
                        CultureInfo.InvariantCulture);

                double bpm =
                    Convert.ToDouble(
                        __args[3],
                        CultureInfo.InvariantCulture);

                double pitch =
                    Convert.ToDouble(
                        __args[4],
                        CultureInfo.InvariantCulture);

                if (bpm <= 0.0 ||
                    pitch <= 0.0 ||
                    double.IsNaN(bpm) ||
                    double.IsInfinity(bpm) ||
                    double.IsNaN(pitch) ||
                    double.IsInfinity(pitch))
                    return;

                double diff =
                    NormalizeRadians(
                        angle - targetAngle);

                // Mirror ADOFAI's early/late direction so the sign remains
                // meaningful even though the limiter itself is symmetric.
                if (!isCW)
                    diff = -diff;

                // In ADOFAI, PI radians (180 degrees) is one beat.
                // beatMs = 60000 / (bpm * pitch)
                double signedMs =
                    diff / Pi *
                    (60000.0 / (bpm * pitch));

                pendingSignedMs = signedMs;
                pendingOverLimit =
                    Math.Abs(signedMs) >
                    limitMs + 0.0001;

                pendingLimitResult = true;
            }
            catch
            {
                pendingLimitResult = false;
                pendingOverLimit = false;
                pendingSignedMs = 0.0;
            }
        }

        // Postfix for scrMisc.IsValidHit(HitMargin).
        // The game decides its normal validity first; we only turn a valid hit
        // into an invalid one when it exceeds our tighter ±ms limit.
        private static void IsValidHitPostfix(ref bool __result)
        {
            if (!pendingLimitResult)
                return;

            try
            {
                if (enabled &&
                    pendingOverLimit &&
                    __result)
                {
                    __result = false;
                }
            }
            finally
            {
                pendingLimitResult = false;
                pendingOverLimit = false;
                pendingSignedMs = 0.0;
            }
        }

        private static bool InstallPatches()
        {
            try
            {
                Type scrMiscType =
                    ReflectionUtil.FindType(
                        "scrMisc");

                if (scrMiscType == null)
                {
                    LogError(
                        "ADOFAI scrMisc was not found. The game may have changed.");
                    return false;
                }

                MethodInfo getHitMargin =
                    scrMiscType
                        .GetMethods(
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.Static)
                        .Where(m =>
                            m.Name == "GetHitMargin")
                        .OrderByDescending(m =>
                            m.GetParameters().Length)
                        .FirstOrDefault(m =>
                            m.GetParameters().Length >= 5);

                MethodInfo isValidHit =
                    scrMiscType
                        .GetMethods(
                            BindingFlags.Public |
                            BindingFlags.NonPublic |
                            BindingFlags.Static)
                        .FirstOrDefault(m =>
                            m.Name == "IsValidHit" &&
                            m.ReturnType == typeof(bool));

                if (getHitMargin == null ||
                    isValidHit == null)
                {
                    LogError(
                        "ADOFAI judgement methods were not found. " +
                        "GetHitMargin=" + (getHitMargin != null) +
                        ", IsValidHit=" + (isValidHit != null));
                    return false;
                }

                harmonyInstance =
                    new Harmony(HarmonyId);

                MethodInfo prefixMethod =
                    typeof(Main).GetMethod(
                        "GetHitMarginPrefix",
                        BindingFlags.NonPublic |
                        BindingFlags.Static);

                MethodInfo postfixMethod =
                    typeof(Main).GetMethod(
                        "IsValidHitPostfix",
                        BindingFlags.NonPublic |
                        BindingFlags.Static);

                harmonyInstance.Patch(
                    getHitMargin,
                    prefix:
                        new HarmonyMethod(
                            prefixMethod));

                harmonyInstance.Patch(
                    isValidHit,
                    postfix:
                        new HarmonyMethod(
                            postfixMethod));

                Log(
                    "Patched " +
                    getHitMargin.DeclaringType.FullName +
                    "." +
                    getHitMargin.Name +
                    " and " +
                    isValidHit.DeclaringType.FullName +
                    "." +
                    isValidHit.Name);

                return true;
            }
            catch (Exception ex)
            {
                LogError(
                    "Patch installation error: " +
                    ex);
                return false;
            }
        }

        private static double NormalizeRadians(double value)
        {
            while (value > Pi)
                value -= TwoPi;

            while (value < -Pi)
                value += TwoPi;

            return value;
        }

        private static double Clamp(
            double value,
            double min,
            double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool TryParseLimit(
            string text,
            out double value)
        {
            if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
                return true;

            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value);
        }

        private static void LoadSettings()
        {
            try
            {
                string dir =
                    Path.GetDirectoryName(
                        typeof(Main).Assembly.Location);

                if (string.IsNullOrEmpty(dir))
                    dir = ".";

                settingsPath =
                    Path.Combine(
                        dir,
                        "TimingLimiter_Settings.dat");

                if (!File.Exists(settingsPath))
                {
                    limitText =
                        limitMs.ToString(
                            "0.##",
                            CultureInfo.InvariantCulture);
                    return;
                }

                foreach (string raw in
                    File.ReadAllLines(settingsPath))
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    int eq = raw.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key =
                        raw.Substring(0, eq).Trim();

                    string valueText =
                        raw.Substring(eq + 1).Trim();

                    if (!key.Equals(
                        "LimitMs",
                        StringComparison.OrdinalIgnoreCase))
                        continue;

                    double parsed;
                    if (TryParseLimit(
                        valueText,
                        out parsed))
                    {
                        limitMs =
                            Clamp(
                                parsed,
                                0.1,
                                1000.0);
                    }
                }

                limitText =
                    limitMs.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Log(
                    "Settings load error: " +
                    ex.Message);
            }
        }

        private static void SaveSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(settingsPath))
                    return;

                string temp =
                    settingsPath + ".tmp";

                File.WriteAllLines(
                    temp,
                    new[]
                    {
                        "LimitMs=" +
                        limitMs.ToString(
                            "0.####",
                            CultureInfo.InvariantCulture)
                    });

                if (File.Exists(settingsPath))
                    File.Delete(settingsPath);

                File.Move(temp, settingsPath);
            }
            catch (Exception ex)
            {
                Log(
                    "Settings save error: " +
                    ex.Message);
            }
        }

        private static void Log(string message)
        {
            try
            {
                if (mod != null &&
                    mod.Logger != null)
                    mod.Logger.Log(
                        "[TimingLimiter] " +
                        message);
            }
            catch { }
        }

        private static void LogError(string message)
        {
            try
            {
                if (mod != null &&
                    mod.Logger != null)
                    mod.Logger.Error(
                        "[TimingLimiter] " +
                        message);
            }
            catch { }
        }
    }

    internal static class GameBridge
    {
        private static Type rdcType;
        private static MemberInfo autoMember;
        private static bool resolved;

        public static bool IsAutoPlay()
        {
            Resolve();

            if (rdcType == null ||
                autoMember == null)
                return false;

            try
            {
                object raw =
                    ReflectionUtil.ReadMember(
                        null,
                        autoMember);

                return raw is bool &&
                       (bool)raw;
            }
            catch
            {
                return false;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            rdcType =
                ReflectionUtil.FindType("RDC");

            if (rdcType == null)
                return;

            const BindingFlags all =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance;

            autoMember =
                ReflectionUtil.FindMember(
                    rdcType,
                    "auto",
                    all);
        }
    }

    internal static class RGui
    {
        private static Type layoutType;
        private static Type optionType;
        private static object noOptions;

        private static MethodInfo label;
        private static MethodInfo textField;
        private static MethodInfo button;
        private static MethodInfo space;

        private static bool resolved;

        public static void Label(string text)
        {
            Resolve();

            if (label != null)
                label.Invoke(
                    null,
                    new object[]
                    {
                        text,
                        noOptions
                    });
        }

        public static string TextField(string text)
        {
            Resolve();

            if (textField == null)
                return text;

            object result =
                textField.Invoke(
                    null,
                    new object[]
                    {
                        text,
                        noOptions
                    });

            return result as string ?? text;
        }

        public static bool Button(string text)
        {
            Resolve();

            if (button == null)
                return false;

            object result =
                button.Invoke(
                    null,
                    new object[]
                    {
                        text,
                        noOptions
                    });

            return result is bool &&
                   (bool)result;
        }

        public static void Space(float pixels)
        {
            Resolve();

            if (space != null)
                space.Invoke(
                    null,
                    new object[] { pixels });
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            layoutType =
                Type.GetType(
                    "UnityEngine.GUILayout, UnityEngine.IMGUIModule",
                    false);

            optionType =
                Type.GetType(
                    "UnityEngine.GUILayoutOption, UnityEngine.IMGUIModule",
                    false);

            if (layoutType == null ||
                optionType == null)
                return;

            noOptions =
                Array.CreateInstance(
                    optionType,
                    0);

            MethodInfo[] methods =
                layoutType.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Static);

            label =
                methods.FirstOrDefault(m =>
                    Match(
                        m,
                        "Label",
                        typeof(string),
                        optionType.MakeArrayType()));

            textField =
                methods.FirstOrDefault(m =>
                    Match(
                        m,
                        "TextField",
                        typeof(string),
                        optionType.MakeArrayType()) &&
                    m.ReturnType == typeof(string));

            button =
                methods.FirstOrDefault(m =>
                    Match(
                        m,
                        "Button",
                        typeof(string),
                        optionType.MakeArrayType()) &&
                    m.ReturnType == typeof(bool));

            space =
                methods.FirstOrDefault(m =>
                    Match(
                        m,
                        "Space",
                        typeof(float)));
        }

        private static bool Match(
            MethodInfo method,
            string name,
            params Type[] types)
        {
            if (method.Name != name)
                return false;

            ParameterInfo[] parameters =
                method.GetParameters();

            if (parameters.Length !=
                types.Length)
                return false;

            for (int i = 0;
                 i < parameters.Length;
                 i++)
            {
                if (parameters[i].ParameterType !=
                    types[i])
                    return false;
            }

            return true;
        }
    }

    internal static class ReflectionUtil
    {
        public static Type FindType(string name)
        {
            foreach (Assembly assembly in
                AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type =
                        assembly.GetType(
                            name,
                            false);

                    if (type != null)
                        return type;
                }
                catch { }
            }

            return null;
        }

        public static MemberInfo FindMember(
            Type type,
            string name,
            BindingFlags flags)
        {
            if (type == null)
                return null;

            Type current = type;

            while (current != null)
            {
                FieldInfo field =
                    current.GetField(
                        name,
                        flags |
                        BindingFlags.DeclaredOnly);

                if (field != null)
                    return field;

                PropertyInfo property =
                    current.GetProperty(
                        name,
                        flags |
                        BindingFlags.DeclaredOnly);

                if (property != null)
                    return property;

                current = current.BaseType;
            }

            return null;
        }

        public static object ReadMember(
            object instance,
            MemberInfo member)
        {
            if (member == null)
                return null;

            FieldInfo field =
                member as FieldInfo;

            if (field != null)
                return field.GetValue(instance);

            PropertyInfo property =
                member as PropertyInfo;

            if (property != null)
                return property.GetValue(
                    instance,
                    null);

            return null;
        }
    }
}
