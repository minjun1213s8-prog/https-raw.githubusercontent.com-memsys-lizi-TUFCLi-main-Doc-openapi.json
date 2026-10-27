using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

namespace TimingLimiter
{
    public static class Main
    {
        private const string HarmonyId = "Wonerd.TimingLimiter";
        private const double Pi = Math.PI;
        private const double TwoPi = Math.PI * 2.0;

        private static UnityModManager.ModEntry mod;
        private static Harmony harmony;
        private static bool enabled = true;

        private static double limitMs = 30.0;
        private static string limitText = "30";
        private static string settingsPath;

        [ThreadStatic]
        private static bool suppressLimiter;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            LoadSettings();

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;

            bool patched = InstallPatch();

            if (!patched)
            {
                // Stay loaded instead of turning red in UMM. The log contains
                // exact discovery diagnostics for future game updates.
                LogError(
                    "No compatible player Hit method was found. " +
                    "TimingLimiter is loaded but inactive.");
            }

            Log(
                "TimingLimiter 0.1.3 loaded. Limit: ±" +
                limitMs.ToString(
                    "0.##",
                    CultureInfo.InvariantCulture) +
                " ms. Patch active=" + patched);

            return true;
        }

        private static bool OnToggle(
            UnityModManager.ModEntry entry,
            bool value)
        {
            enabled = value;
            return true;
        }

        private static bool OnUnload(
            UnityModManager.ModEntry entry)
        {
            enabled = false;

            try
            {
                if (harmony != null)
                    harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                Log("Unpatch error: " + ex.Message);
            }

            harmony = null;
            return true;
        }

        private static void OnGUI(
            UnityModManager.ModEntry entry)
        {
            try
            {
                RGui.Label(
                    "TimingLimiter 0.1.3 - current ADOFAI compatibility build");
                RGui.Label(
                    "Manual hits outside the configured ±ms range fail.");
                RGui.Space(8f);

                RGui.Label("Timing limit (±ms)");
                limitText =
                    RGui.TextField(limitText);

                if (RGui.Button("Apply timing limit"))
                {
                    double parsed;

                    if (TryParseLimit(
                        limitText,
                        out parsed))
                    {
                        limitMs =
                            Clamp(
                                parsed,
                                0.1,
                                1000.0);

                        limitText =
                            limitMs.ToString(
                                "0.##",
                                CultureInfo.InvariantCulture);

                        SaveSettings();

                        Log(
                            "Timing limit changed to ±" +
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
                    "Autoplay, auto tiles, free-roam and midspin-infinite inputs are ignored.");
            }
            catch (Exception ex)
            {
                Log("GUI error: " + ex.Message);
            }
        }

        private static void OnSaveGUI(
            UnityModManager.ModEntry entry)
        {
            double parsed;

            if (TryParseLimit(
                limitText,
                out parsed))
            {
                limitMs =
                    Clamp(
                        parsed,
                        0.1,
                        1000.0);

                limitText =
                    limitMs.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture);
            }

            SaveSettings();
        }

        private static bool InstallPatch()
        {
            try
            {
                Type playerType =
                    ReflectionUtil.FindTypeFlexible(
                        "scrPlayer");

                if (playerType == null)
                {
                    LogTypeHints("player");
                    return false;
                }

                MethodInfo[] hitCandidates =
                    playerType.GetMethods(
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance)
                    .Where(m =>
                        m.Name == "Hit" &&
                        m.ReturnType == typeof(bool))
                    .ToArray();

                MethodInfo hitMethod =
                    hitCandidates.FirstOrDefault(m =>
                    {
                        ParameterInfo[] p =
                            m.GetParameters();

                        return p.Length == 1 &&
                               p[0].ParameterType ==
                                   typeof(bool);
                    });

                if (hitMethod == null)
                {
                    LogError(
                        "scrPlayer found as " +
                        playerType.FullName +
                        " but compatible Hit(bool) was not found. Candidates: " +
                        string.Join(
                            ", ",
                            hitCandidates.Select(
                                DescribeMethod)
                            .ToArray()));

                    return false;
                }

                harmony =
                    new Harmony(HarmonyId);

                MethodInfo prefix =
                    typeof(Main).GetMethod(
                        "HitPrefix",
                        BindingFlags.NonPublic |
                        BindingFlags.Static);

                harmony.Patch(
                    hitMethod,
                    prefix:
                        new HarmonyMethod(prefix));

                Log(
                    "Patched " +
                    playerType.FullName +
                    "." +
                    DescribeMethod(hitMethod));

                return true;
            }
            catch (Exception ex)
            {
                LogError(
                    "Hit patch installation error: " +
                    ex);
                return false;
            }
        }

        // Returns false to skip the original Hit only when TimingLimiter
        // intentionally rejects the manual input.
        private static bool HitPrefix(
            object __instance,
            object[] __args,
            ref bool __result)
        {
            if (!enabled ||
                suppressLimiter ||
                __instance == null)
                return true;

            bool isAuto =
                __args != null &&
                __args.Length > 0 &&
                __args[0] is bool &&
                (bool)__args[0];

            if (isAuto ||
                GameBridge.IsAutoPlay())
                return true;

            try
            {
                double signedMs;
                string skipReason;

                if (!TryCalculateHitMs(
                    __instance,
                    out signedMs,
                    out skipReason))
                {
                    return true;
                }

                if (Math.Abs(signedMs) <=
                    limitMs + 0.0001)
                    return true;

                suppressLimiter = true;

                try
                {
                    InvokeDeath(
                        __instance,
                        signedMs);
                }
                finally
                {
                    suppressLimiter = false;
                }

                __result = false;

                Log(
                    "Rejected hit: " +
                    signedMs.ToString(
                        "+0.00;-0.00;0.00",
                        CultureInfo.InvariantCulture) +
                    " ms (limit ±" +
                    limitMs.ToString(
                        "0.##",
                        CultureInfo.InvariantCulture) +
                    " ms)");

                return false;
            }
            catch (Exception ex)
            {
                // Never break normal gameplay if a future update changes a
                // member. The limiter simply allows that hit and logs it.
                Log(
                    "Hit evaluation error: " +
                    ex.Message);

                return true;
            }
        }

        private static bool TryCalculateHitMs(
            object player,
            out double signedMs,
            out string skipReason)
        {
            signedMs = 0.0;
            skipReason = null;

            object planetarySystem =
                ReflectionUtil.ReadNamed(
                    player,
                    "planetarySystem");

            if (planetarySystem == null)
            {
                skipReason = "planetarySystem";
                return false;
            }

            object chosenPlanet =
                ReflectionUtil.ReadNamed(
                    planetarySystem,
                    "chosenPlanet");

            if (chosenPlanet == null)
            {
                skipReason = "chosenPlanet";
                return false;
            }

            object currentFloor =
                ReflectionUtil.ReadNamed(
                    chosenPlanet,
                    "currfloor");

            if (currentFloor == null)
            {
                skipReason = "currfloor";
                return false;
            }

            if (ReflectionUtil.ReadBool(
                    currentFloor,
                    "freeroam",
                    false) ||
                ReflectionUtil.ReadBool(
                    currentFloor,
                    "freeroamGenerated",
                    false))
            {
                skipReason = "freeroam";
                return false;
            }

            if (ReflectionUtil.ReadBool(
                    player,
                    "midspinInfiniteMargin",
                    false))
            {
                skipReason = "midspinInfiniteMargin";
                return false;
            }

            object nextFloor =
                ReflectionUtil.ReadNamed(
                    currentFloor,
                    "nextfloor");

            if (nextFloor != null &&
                ReflectionUtil.ReadBool(
                    nextFloor,
                    "auto",
                    false))
            {
                skipReason = "auto tile";
                return false;
            }

            double angle;
            double targetAngle;
            double speed;

            if (!ReflectionUtil.TryReadDouble(
                    chosenPlanet,
                    "angle",
                    out angle) ||
                !ReflectionUtil.TryReadDouble(
                    chosenPlanet,
                    "targetExitAngle",
                    out targetAngle) ||
                !ReflectionUtil.TryReadDouble(
                    planetarySystem,
                    "speed",
                    out speed))
            {
                skipReason = "angle/speed";
                return false;
            }

            bool isCW =
                ReflectionUtil.ReadBool(
                    planetarySystem,
                    "isCW",
                    true);

            object conductor =
                GameBridge.GetConductor();

            if (conductor == null)
            {
                skipReason = "conductor";
                return false;
            }

            double bpm;

            if (!ReflectionUtil.TryReadDouble(
                    conductor,
                    "bpm",
                    out bpm))
            {
                skipReason = "bpm";
                return false;
            }

            double pitch = 1.0;

            object song =
                ReflectionUtil.ReadNamed(
                    conductor,
                    "song");

            double songPitch;

            if (song != null &&
                ReflectionUtil.TryReadDouble(
                    song,
                    "pitch",
                    out songPitch))
            {
                pitch = songPitch;
            }

            if (bpm <= 0.0 ||
                speed <= 0.0 ||
                pitch <= 0.0)
            {
                skipReason = "invalid tempo";
                return false;
            }

            double diff =
                NormalizeRadians(
                    angle - targetAngle);

            if (!isCW)
                diff = -diff;

            // ADOFAI rotates PI radians (180 degrees) per beat.
            // Effective beat time includes planetary speed and song pitch.
            signedMs =
                diff / Pi *
                (60000.0 /
                    (bpm * speed * pitch));

            if (double.IsNaN(signedMs) ||
                double.IsInfinity(signedMs))
            {
                skipReason = "invalid result";
                return false;
            }

            return true;
        }

        private static void InvokeDeath(
            object player,
            double signedMs)
        {
            MethodInfo die =
                player.GetType()
                    .GetMethods(
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Instance)
                    .Where(m =>
                        m.Name == "Die")
                    .OrderBy(m =>
                        m.GetParameters().Length)
                    .FirstOrDefault();

            if (die == null)
            {
                LogError(
                    "Timing violation detected but scrPlayer.Die was not found.");
                return;
            }

            ParameterInfo[] p =
                die.GetParameters();

            object[] args =
                new object[p.Length];

            string message =
                "TimingLimiter: " +
                signedMs.ToString(
                    "+0.00;-0.00;0.00",
                    CultureInfo.InvariantCulture) +
                "ms / ±" +
                limitMs.ToString(
                    "0.##",
                    CultureInfo.InvariantCulture) +
                "ms";

            for (int i = 0;
                 i < p.Length;
                 i++)
            {
                if (string.Equals(
                    p[i].Name,
                    "failMessage",
                    StringComparison.OrdinalIgnoreCase) &&
                    p[i].ParameterType ==
                        typeof(string))
                {
                    args[i] = message;
                }
                else if (p[i].HasDefaultValue)
                {
                    args[i] =
                        p[i].DefaultValue;
                }
                else
                {
                    args[i] =
                        ReflectionUtil.DefaultValue(
                            p[i].ParameterType);
                }
            }

            die.Invoke(
                player,
                args);
        }

        private static void LogTypeHints(string token)
        {
            try
            {
                string[] matches =
                    ReflectionUtil.FindTypeNamesContaining(
                        token,
                        24);

                LogError(
                    "Could not find scrPlayer. Loaded type hints containing '" +
                    token + "': " +
                    (matches.Length == 0
                        ? "(none)"
                        : string.Join(", ", matches)));
            }
            catch (Exception ex)
            {
                LogError(
                    "Type diagnostic error: " +
                    ex.Message);
            }
        }

        private static string DescribeMethod(
            MethodInfo method)
        {
            if (method == null)
                return "(null)";

            return method.Name +
                "(" +
                string.Join(
                    ",",
                    method.GetParameters()
                        .Select(p =>
                            p.ParameterType.Name)
                        .ToArray()) +
                ")";
        }

        private static double NormalizeRadians(
            double value)
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
                        raw.Substring(
                            0,
                            eq)
                        .Trim();

                    string valueText =
                        raw.Substring(
                            eq + 1)
                        .Trim();

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
                if (string.IsNullOrEmpty(
                    settingsPath))
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

                File.Move(
                    temp,
                    settingsPath);
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
                {
                    mod.Logger.Log(
                        "[TimingLimiter] " +
                        message);
                }
            }
            catch { }
        }

        private static void LogError(
            string message)
        {
            try
            {
                if (mod != null &&
                    mod.Logger != null)
                {
                    mod.Logger.Error(
                        "[TimingLimiter] " +
                        message);
                }
            }
            catch { }
        }
    }

    internal static class GameBridge
    {
        private static Type rdcType;
        private static MemberInfo autoMember;

        private static Type adoBaseType;
        private static MemberInfo conductorMember;

        private static Type conductorType;
        private static MemberInfo conductorInstanceMember;

        private static bool resolved;

        public static bool IsAutoPlay()
        {
            Resolve();

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

        public static object GetConductor()
        {
            Resolve();

            try
            {
                object conductor =
                    ReflectionUtil.ReadMember(
                        null,
                        conductorMember);

                if (conductor != null)
                    return conductor;
            }
            catch { }

            try
            {
                return ReflectionUtil.ReadMember(
                    null,
                    conductorInstanceMember);
            }
            catch
            {
                return null;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            const BindingFlags all =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance;

            rdcType =
                ReflectionUtil.FindTypeFlexible(
                    "RDC");

            autoMember =
                ReflectionUtil.FindMember(
                    rdcType,
                    "auto",
                    all);

            adoBaseType =
                ReflectionUtil.FindTypeFlexible(
                    "ADOBase");

            conductorMember =
                ReflectionUtil.FindMember(
                    adoBaseType,
                    "conductor",
                    all);

            conductorType =
                ReflectionUtil.FindTypeFlexible(
                    "scrConductor");

            conductorInstanceMember =
                ReflectionUtil.FindMember(
                    conductorType,
                    "instance",
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

        public static string TextField(
            string text)
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

            return result as string ??
                   text;
        }

        public static bool Button(
            string text)
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

        public static void Space(
            float pixels)
        {
            Resolve();

            if (space != null)
                space.Invoke(
                    null,
                    new object[]
                    {
                        pixels
                    });
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
                    m.ReturnType ==
                        typeof(string));

            button =
                methods.FirstOrDefault(m =>
                    Match(
                        m,
                        "Button",
                        typeof(string),
                        optionType.MakeArrayType()) &&
                    m.ReturnType ==
                        typeof(bool));

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
                if (parameters[i]
                        .ParameterType !=
                    types[i])
                    return false;
            }

            return true;
        }
    }

    internal static class ReflectionUtil
    {
        public static Type FindTypeFlexible(
            string name)
        {
            foreach (Assembly assembly in
                AppDomain.CurrentDomain
                    .GetAssemblies())
            {
                try
                {
                    Type exact =
                        assembly.GetType(
                            name,
                            false);

                    if (exact != null)
                        return exact;
                }
                catch { }

                foreach (Type type in
                    SafeGetTypes(assembly))
                {
                    if (type == null)
                        continue;

                    if (string.Equals(
                        type.Name,
                        name,
                        StringComparison.Ordinal) ||
                        string.Equals(
                        type.FullName,
                        name,
                        StringComparison.Ordinal) ||
                        (type.FullName != null &&
                         type.FullName.EndsWith(
                             "." + name,
                             StringComparison.Ordinal)))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        public static string[]
            FindTypeNamesContaining(
                string token,
                int max)
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(
                    SafeGetTypes)
                .Where(t =>
                    t != null &&
                    ((t.Name != null &&
                      t.Name.IndexOf(
                          token,
                          StringComparison.OrdinalIgnoreCase) >= 0) ||
                     (t.FullName != null &&
                      t.FullName.IndexOf(
                          token,
                          StringComparison.OrdinalIgnoreCase) >= 0)))
                .Select(t =>
                    t.FullName ??
                    t.Name)
                .Distinct()
                .Take(max)
                .ToArray();
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

                current =
                    current.BaseType;
            }

            return null;
        }

        public static object ReadNamed(
            object instance,
            string name)
        {
            if (instance == null)
                return null;

            const BindingFlags all =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance;

            MemberInfo member =
                FindMember(
                    instance.GetType(),
                    name,
                    all);

            return ReadMember(
                instance,
                member);
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
                return field.GetValue(
                    field.IsStatic
                        ? null
                        : instance);

            PropertyInfo property =
                member as PropertyInfo;

            if (property != null)
            {
                MethodInfo getter =
                    property.GetGetMethod(true);

                return property.GetValue(
                    getter != null &&
                    getter.IsStatic
                        ? null
                        : instance,
                    null);
            }

            return null;
        }

        public static bool ReadBool(
            object instance,
            string name,
            bool fallback)
        {
            try
            {
                object value =
                    ReadNamed(
                        instance,
                        name);

                return value is bool
                    ? (bool)value
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static bool TryReadDouble(
            object instance,
            string name,
            out double value)
        {
            value = 0.0;

            try
            {
                object raw =
                    ReadNamed(
                        instance,
                        name);

                if (raw == null)
                    return false;

                value =
                    Convert.ToDouble(
                        raw,
                        CultureInfo.InvariantCulture);

                return !double.IsNaN(value) &&
                       !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        public static object DefaultValue(
            Type type)
        {
            if (type == null ||
                !type.IsValueType)
                return null;

            return Activator.CreateInstance(
                type);
        }

        private static Type[] SafeGetTypes(
            Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (
                ReflectionTypeLoadException ex)
            {
                return ex.Types
                    .Where(t => t != null)
                    .ToArray();
            }
            catch
            {
                return new Type[0];
            }
        }
    }
}
