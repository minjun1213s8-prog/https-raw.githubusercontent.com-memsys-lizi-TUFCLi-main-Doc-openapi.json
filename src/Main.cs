using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityModManagerNet;

namespace PracticeStats
{
    public static class Main
    {
        private static UnityModManager.ModEntry mod;
        private static bool enabled = true;
        private static readonly PracticeSession session = new PracticeSession();
        private static string startText = "0";
        private static string endText = "1";
        private static string attemptsText = "100";
        private static string status = "Ready";

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;
            Log("PracticeStats v0.1.3 loaded (ADOFAI 3.4.0 target).");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry entry, bool value)
        {
            enabled = value;
            if (!value) session.Stop();
            status = value ? "Enabled" : "Disabled";
            return true;
        }

        private static bool OnUnload(UnityModManager.ModEntry entry)
        {
            session.Stop();
            enabled = false;
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry entry, float deltaTime)
        {
            if (!enabled) return;
            try
            {
                if (UnityBridge.GetKeyDown("F6")) SetStartFromCurrent();
                if (UnityBridge.GetKeyDown("F7")) SetEndFromCurrent();
                if (UnityBridge.GetKeyDown("F8")) TogglePractice();
                if (UnityBridge.GetKeyDown("F9"))
                {
                    session.ResetStats();
                    status = "Stats reset";
                }
                session.Tick();
                if (session.Completed) status = "Completed";
            }
            catch (Exception ex)
            {
                Log("Update error: " + ex.Message);
            }
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            try
            {
                RGui.Label("PracticeStats - ADOFAI 3.4.0");
                RGui.Label("Section practice success-rate tracker");
                RGui.Space(6f);

                int current = GameBridge.CurrentFloor();
                RGui.Label("Current tile: " + (current >= 0 ? current.ToString() : "-"));

                RGui.Label("Start tile");
                startText = RGui.TextField(startText);
                if (RGui.Button("Use current as start")) SetStartFromCurrent();

                RGui.Label("End tile");
                endText = RGui.TextField(endText);
                if (RGui.Button("Use current as end")) SetEndFromCurrent();

                RGui.Label("Target attempts");
                attemptsText = RGui.TextField(attemptsText);

                RGui.Space(6f);
                if (RGui.Button(session.Running ? "Stop practice" : "Start practice")) TogglePractice();
                if (RGui.Button("Reset stats"))
                {
                    session.ResetStats();
                    status = "Stats reset";
                }

                RGui.Space(8f);
                RGui.Label("Range: " + session.StartFloor + " -> " + session.EndFloor);
                RGui.Label("Attempts: " + session.TotalAttempts + " / " + session.TargetAttempts);
                RGui.Label("Success: " + session.Successes + "    Fail: " + session.Failures);
                RGui.Label("Success rate: " + session.SuccessRate.ToString("0.00") + "%");
                RGui.Label("Streak: " + session.CurrentStreak + "    Best: " + session.BestStreak);
                RGui.Label("Status: " + status);
                RGui.Space(6f);
                RGui.Label("Hotkeys: F6 start / F7 end / F8 start-stop / F9 reset");
            }
            catch (Exception ex)
            {
                Log("GUI error: " + ex.Message);
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            ApplyTextFields();
        }

        private static void SetStartFromCurrent()
        {
            int floor = GameBridge.CurrentFloor();
            if (floor < 0) { status = "Start a level first"; return; }
            session.StartFloor = floor;
            startText = floor.ToString();
            status = "Start tile = " + floor;
        }

        private static void SetEndFromCurrent()
        {
            int floor = GameBridge.CurrentFloor();
            if (floor < 0) { status = "Start a level first"; return; }
            session.EndFloor = floor;
            endText = floor.ToString();
            status = "End tile = " + floor;
        }

        private static void TogglePractice()
        {
            if (session.Running)
            {
                session.Stop();
                status = "Practice stopped";
                return;
            }

            ApplyTextFields();
            string reason;
            if (!session.CanStart(out reason))
            {
                status = reason;
                return;
            }

            session.Start();
            status = "Practice started";
        }

        private static void ApplyTextFields()
        {
            int v;
            if (int.TryParse(startText, out v)) session.StartFloor = Math.Max(0, v);
            startText = session.StartFloor.ToString();

            if (int.TryParse(endText, out v)) session.EndFloor = Math.Max(0, v);
            endText = session.EndFloor.ToString();

            if (int.TryParse(attemptsText, out v)) session.TargetAttempts = Math.Max(1, Math.Min(100000, v));
            attemptsText = session.TargetAttempts.ToString();
        }

        internal static void SetStatus(string text)
        {
            status = text;
        }

        internal static void Log(string text)
        {
            try { if (mod != null && mod.Logger != null) mod.Logger.Log("[PracticeStats] " + text); }
            catch { }
        }
    }

    internal sealed class PracticeSession
    {
        public int StartFloor = 0;
        public int EndFloor = 1;
        public int TargetAttempts = 100;
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public int CurrentStreak { get; private set; }
        public int BestStreak { get; private set; }
        public bool Running { get; private set; }
        public bool Completed { get; private set; }
        public int TotalAttempts { get { return Successes + Failures; } }
        public float SuccessRate { get { return TotalAttempts == 0 ? 0f : (Successes * 100f / TotalAttempts); } }

        private bool attemptActive;
        private bool rewindPending;
        private int rewindDelay;
        private int ignoreFrames;
        private int lastDeaths = -1;
        private bool failLatched;

        public bool CanStart(out string reason)
        {
            if (EndFloor <= StartFloor) { reason = "End tile must be after start tile"; return false; }
            if (TargetAttempts < 1) { reason = "Target attempts must be at least 1"; return false; }
            if (GameBridge.Controller() == null) { reason = "Start a level first"; return false; }
            reason = null;
            return true;
        }

        public void Start()
        {
            if (TotalAttempts >= TargetAttempts) ResetStats();
            Running = true;
            Completed = false;
            attemptActive = false;
            failLatched = false;
            lastDeaths = GameBridge.Deaths();
            ScheduleRewind(1);
        }

        public void Stop()
        {
            Running = false;
            attemptActive = false;
            rewindPending = false;
            failLatched = false;
        }

        public void ResetStats()
        {
            Successes = 0;
            Failures = 0;
            CurrentStreak = 0;
            BestStreak = 0;
            Completed = false;
            lastDeaths = GameBridge.Deaths();
        }

        public void Tick()
        {
            if (!Running) return;
            if (TotalAttempts >= TargetAttempts)
            {
                Running = false;
                Completed = true;
                attemptActive = false;
                return;
            }

            if (rewindPending)
            {
                if (rewindDelay-- > 0) return;
                rewindPending = false;
                if (!GameBridge.Rewind(StartFloor))
                {
                    Main.SetStatus("Could not rewind to start tile");
                    Running = false;
                    return;
                }
                ignoreFrames = 10;
                attemptActive = false;
                failLatched = false;
                lastDeaths = GameBridge.Deaths();
                return;
            }

            if (ignoreFrames > 0)
            {
                ignoreFrames--;
                return;
            }

            int current = GameBridge.CurrentFloor();
            if (current < 0) return;

            int deaths = GameBridge.Deaths();
            bool failedNow = GameBridge.IsFailed();
            bool deathIncreased = deaths >= 0 && lastDeaths >= 0 && deaths > lastDeaths;
            if (deaths >= 0) lastDeaths = deaths;

            if (attemptActive && (deathIncreased || (failedNow && !failLatched)))
            {
                failLatched = true;
                RecordFail();
                return;
            }

            if (failedNow) failLatched = true;
            else failLatched = false;

            if (!attemptActive)
            {
                if (current >= StartFloor && current < EndFloor) attemptActive = true;
                return;
            }

            if (current >= EndFloor)
            {
                RecordSuccess();
            }
        }

        private void RecordSuccess()
        {
            Successes++;
            CurrentStreak++;
            if (CurrentStreak > BestStreak) BestStreak = CurrentStreak;
            Main.SetStatus("SUCCESS " + Successes + "/" + TotalAttempts);
            FinishAttempt();
        }

        private void RecordFail()
        {
            Failures++;
            CurrentStreak = 0;
            Main.SetStatus("FAIL " + Failures + "/" + TotalAttempts);
            FinishAttempt();
        }

        private void FinishAttempt()
        {
            attemptActive = false;
            if (TotalAttempts >= TargetAttempts)
            {
                Running = false;
                Completed = true;
                return;
            }
            ScheduleRewind(8);
        }

        private void ScheduleRewind(int delay)
        {
            rewindPending = true;
            rewindDelay = delay;
        }
    }

    internal static class GameBridge
    {
        private static Type controllerType;
        private static MemberInfo instanceMember;
        private static MemberInfo seqMember;
        private static MemberInfo deathsMember;
        private static MethodInfo rewindMethod;
        private static MethodInfo scrubMethod;
        private static MemberInfo failedMember;
        private static MemberInfo stateMember;

        public static object Controller()
        {
            Resolve();
            if (controllerType == null || instanceMember == null) return null;
            try { return ReadMember(null, instanceMember); } catch { return null; }
        }

        public static int CurrentFloor()
        {
            object c = Controller();
            if (c == null) return -1;
            try
            {
                object value = ReadMember(c, seqMember);
                return value == null ? -1 : Convert.ToInt32(value);
            }
            catch { return -1; }
        }

        public static int Deaths()
        {
            Resolve();
            if (deathsMember == null) return -1;
            try
            {
                object value = ReadMember(null, deathsMember);
                return value == null ? -1 : Convert.ToInt32(value);
            }
            catch { return -1; }
        }

        public static bool IsFailed()
        {
            object c = Controller();
            if (c == null) return false;
            try
            {
                if (failedMember != null)
                {
                    object raw = ReadMember(c, failedMember);
                    if (raw is bool) return (bool)raw;
                }
                if (stateMember != null)
                {
                    object raw = ReadMember(c, stateMember);
                    string s = raw == null ? null : raw.ToString();
                    return s != null && s.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }

        public static bool Rewind(int floor)
        {
            object c = Controller();
            if (c == null) return false;
            try
            {
                if (rewindMethod != null)
                {
                    rewindMethod.Invoke(c, new object[] { floor });
                    return true;
                }
                if (scrubMethod != null)
                {
                    scrubMethod.Invoke(c, new object[] { floor, false });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Main.Log("Rewind error: " + ex.Message);
            }
            return false;
        }

        private static void Resolve()
        {
            if (controllerType != null) return;
            controllerType = FindType("scrController");
            if (controllerType == null) return;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            instanceMember = (MemberInfo)controllerType.GetProperty("instance", all) ?? controllerType.GetField("instance", all);
            seqMember = (MemberInfo)controllerType.GetField("currentSeqID", all) ?? controllerType.GetProperty("currentSeqID", all);
            deathsMember = (MemberInfo)controllerType.GetField("deaths", all) ?? controllerType.GetProperty("deaths", all);
            failedMember = FindMember(controllerType, new[] { "failed", "isFailed", "hasFailed", "gameFailed", "isGameOver", "fail" }, all);
            stateMember = FindMember(controllerType, new[] { "state", "currentState", "gameState", "playerState" }, all);
            rewindMethod = controllerType.GetMethods(all).FirstOrDefault(m => m.Name == "Start_Rewind" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(int));
            scrubMethod = controllerType.GetMethods(all).FirstOrDefault(m => m.Name == "Scrub" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(int));
        }

        private static Type FindType(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static MemberInfo FindMember(Type t, IEnumerable<string> names, BindingFlags flags)
        {
            foreach (string n in names)
            {
                MemberInfo m = (MemberInfo)t.GetField(n, flags) ?? t.GetProperty(n, flags);
                if (m != null) return m;
            }
            return null;
        }

        private static object ReadMember(object instance, MemberInfo member)
        {
            if (member == null) return null;
            FieldInfo f = member as FieldInfo;
            if (f != null) return f.GetValue(instance);
            PropertyInfo p = member as PropertyInfo;
            if (p != null) return p.GetValue(instance, null);
            return null;
        }
    }

    internal static class UnityBridge
    {
        private static Type inputType;
        private static Type keyCodeType;
        private static MethodInfo getKeyDown;

        public static bool GetKeyDown(string key)
        {
            try
            {
                if (inputType == null)
                {
                    inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule", false);
                    keyCodeType = Type.GetType("UnityEngine.KeyCode, UnityEngine.CoreModule", false);
                    if (inputType != null && keyCodeType != null)
                        getKeyDown = inputType.GetMethod("GetKeyDown", BindingFlags.Public | BindingFlags.Static, null, new[] { keyCodeType }, null);
                }
                if (getKeyDown == null || keyCodeType == null) return false;
                object code = Enum.Parse(keyCodeType, key, true);
                object result = getKeyDown.Invoke(null, new[] { code });
                return result is bool && (bool)result;
            }
            catch { return false; }
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
            if (label != null) label.Invoke(null, new object[] { text, noOptions });
        }

        public static string TextField(string text)
        {
            Resolve();
            if (textField == null) return text;
            object result = textField.Invoke(null, new object[] { text, noOptions });
            return result as string ?? text;
        }

        public static bool Button(string text)
        {
            Resolve();
            if (button == null) return false;
            object result = button.Invoke(null, new object[] { text, noOptions });
            return result is bool && (bool)result;
        }

        public static void Space(float pixels)
        {
            Resolve();
            if (space != null) space.Invoke(null, new object[] { pixels });
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            layoutType = Type.GetType("UnityEngine.GUILayout, UnityEngine.IMGUIModule", false);
            optionType = Type.GetType("UnityEngine.GUILayoutOption, UnityEngine.IMGUIModule", false);
            if (layoutType == null || optionType == null) return;
            noOptions = Array.CreateInstance(optionType, 0);
            MethodInfo[] methods = layoutType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            label = methods.FirstOrDefault(m => Match(m, "Label", typeof(string), optionType.MakeArrayType()));
            textField = methods.FirstOrDefault(m => Match(m, "TextField", typeof(string), optionType.MakeArrayType()) && m.ReturnType == typeof(string));
            button = methods.FirstOrDefault(m => Match(m, "Button", typeof(string), optionType.MakeArrayType()) && m.ReturnType == typeof(bool));
            space = methods.FirstOrDefault(m => Match(m, "Space", typeof(float)));
        }

        private static bool Match(MethodInfo m, string name, params Type[] types)
        {
            if (m.Name != name) return false;
            ParameterInfo[] p = m.GetParameters();
            if (p.Length != types.Length) return false;
            for (int i = 0; i < p.Length; i++) if (p[i].ParameterType != types[i]) return false;
            return true;
        }
    }
}
