using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityModManagerNet;

namespace PracticeStats
{
    public static class Main
    {
        private static UnityModManager.ModEntry mod;
        private static bool enabled = true;
        private static readonly PracticeSession session = new PracticeSession();
        private static string attemptsText = "100";
        private static string status = "Select a range with Shift + Left Click";
        private static bool hasEditorRange;
        private static int selectedCount;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUnload = OnUnload;
            Log("PracticeStats v0.3.0 loaded (ADOFAI 3.4.0 target).");
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
                RuntimeOverlay.EnsureAttached();
                SyncRangeFromEditor(false);

                if (UnityBridge.GetKeyDown("F8")) TogglePractice();
                if (UnityBridge.GetKeyDown("F9"))
                {
                    session.ResetStats();
                    status = "Stats reset";
                }

                session.Tick();
                if (session.Completed && !session.WaitingForContinue)
                    status = "Completed";
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
                SyncRangeFromEditor(false);

                RGui.Label("PracticeStats - ADOFAI 3.4.0");
                RGui.Label("Range source: editor selection (Shift + Left Click)");
                RGui.Space(6f);

                if (hasEditorRange)
                    RGui.Label("Selected range: " + session.StartFloor + " -> " + session.EndFloor + " (" + selectedCount + " tiles selected)");
                else
                {
                    RGui.Label("Selected range: none");
                    RGui.Label("In the level editor, select a tile range with Shift + Left Click.");
                }

                RGui.Space(6f);
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
                RGui.Label("Attempts: " + session.TotalAttempts + " / " + session.TargetAttempts);
                RGui.Label("Success: " + session.Successes + "    Fail: " + session.Failures);
                RGui.Label("Success rate: " + session.SuccessRate.ToString("0.00") + "%");
                RGui.Label("Streak: " + session.CurrentStreak + "    Best: " + session.BestStreak);
                RGui.Label("Status: " + status);
                RGui.Space(6f);
                RGui.Label("Hotkeys: F8 start/stop, F9 reset");
            }
            catch (Exception ex)
            {
                Log("GUI error: " + ex.Message);
            }
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            ApplyAttempts();
        }

        private static void SyncRangeFromEditor(bool announce)
        {
            if (session.Running) return;
            if (!EditorBridge.Exists() || EditorBridge.IsPlayMode()) return;

            int start;
            int end;
            int count;
            if (!EditorBridge.TryGetSelectionRange(out start, out end, out count) || count < 2 || end <= start)
            {
                hasEditorRange = false;
                selectedCount = count;
                return;
            }

            bool changed = !hasEditorRange || session.StartFloor != start || session.EndFloor != end;
            hasEditorRange = true;
            selectedCount = count;
            if (!changed) return;

            session.StartFloor = start;
            session.EndFloor = end;
            if (session.TotalAttempts > 0) session.ResetStats();

            if (announce || changed)
                status = "Range set: " + start + " -> " + end;
        }

        private static void TogglePractice()
        {
            if (session.Running)
            {
                session.Stop();
                status = "Practice stopped";
                return;
            }

            SyncRangeFromEditor(true);
            ApplyAttempts();

            if (!hasEditorRange)
            {
                status = "Select a range with Shift + Left Click first";
                return;
            }

            string reason;
            if (!session.CanStart(out reason))
            {
                status = reason;
                return;
            }

            session.Start();
            status = "Practice started";
        }

        private static void ApplyAttempts()
        {
            int v;
            if (int.TryParse(attemptsText, out v))
                session.TargetAttempts = Math.Max(1, Math.Min(100000, v));
            attemptsText = session.TargetAttempts.ToString();
        }

        public static void DrawRuntimeOverlay()
        {
            if (!enabled || !session.Running) return;

            int remaining = Math.Max(0, session.TargetAttempts - session.TotalAttempts);
            string prompt = "";
            if (session.WaitingForContinue)
                prompt = session.Completed
                    ? "\n\nCOMPLETE - press any key"
                    : "\n\nSUCCESS - press any key for next attempt";

            string text =
                "PracticeStats\n" +
                "Remaining: " + remaining + "\n" +
                "Cleared: " + session.Successes + "\n" +
                "Success rate: " + session.SuccessRate.ToString("0.00") + "%" +
                prompt;

            OverlayRenderer.Draw(text, session.WaitingForContinue);
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
        public bool WaitingForContinue { get; private set; }
        public int TotalAttempts { get { return Successes + Failures; } }
        public float SuccessRate { get { return TotalAttempts == 0 ? 0f : (Successes * 100f / TotalAttempts); } }

        private bool attemptActive;
        private bool startPending;
        private int startDelay;
        private int ignoreFrames;
        private int continueDelay;
        private int lastDeaths = -1;
        private bool failLatched;

        public bool CanStart(out string reason)
        {
            if (EndFloor <= StartFloor) { reason = "End tile must be after start tile"; return false; }
            if (TargetAttempts < 1) { reason = "Target attempts must be at least 1"; return false; }
            if (!EditorBridge.Exists()) { reason = "Open the level editor first"; return false; }
            reason = null;
            return true;
        }

        public void Start()
        {
            FreezeManager.Unfreeze();
            if (TotalAttempts >= TargetAttempts) ResetStats();
            Running = true;
            Completed = false;
            WaitingForContinue = false;
            attemptActive = false;
            failLatched = false;
            lastDeaths = GameBridge.Deaths();
            ScheduleStart(1);
        }

        public void Stop()
        {
            FreezeManager.Unfreeze();
            Running = false;
            WaitingForContinue = false;
            attemptActive = false;
            startPending = false;
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

            if (WaitingForContinue)
            {
                if (continueDelay > 0)
                {
                    continueDelay--;
                    return;
                }

                if (!UnityBridge.AnyKeyDown()) return;

                FreezeManager.Unfreeze();
                WaitingForContinue = false;

                if (Completed || TotalAttempts >= TargetAttempts)
                {
                    Running = false;
                    Completed = true;
                    Main.SetStatus("Completed");
                    return;
                }

                Main.SetStatus("Next attempt");
                ScheduleStart(2);
                return;
            }

            if (startPending)
            {
                HandleStartPending();
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

            failLatched = failedNow;

            if (!attemptActive)
            {
                if (current >= StartFloor && current < EndFloor) attemptActive = true;
                return;
            }

            if (current >= EndFloor)
                RecordSuccess();
        }

        private void HandleStartPending()
        {
            if (startDelay-- > 0) return;

            if (EditorBridge.Exists())
            {
                if (EditorBridge.IsPlayMode())
                {
                    if (!EditorBridge.SwitchToEditMode())
                    {
                        Main.SetStatus("Could not return to editor");
                        Running = false;
                        return;
                    }

                    startDelay = 2;
                    return;
                }

                if (EditorBridge.PlayAt(StartFloor))
                {
                    CompleteStart();
                    return;
                }
            }

            if (GameBridge.Rewind(StartFloor))
            {
                CompleteStart();
                return;
            }

            Main.SetStatus("Could not start from selected tile");
            Running = false;
        }

        private void CompleteStart()
        {
            startPending = false;
            ignoreFrames = 12;
            attemptActive = false;
            failLatched = false;
            lastDeaths = GameBridge.Deaths();
        }

        private void RecordSuccess()
        {
            Successes++;
            CurrentStreak++;
            if (CurrentStreak > BestStreak) BestStreak = CurrentStreak;

            Completed = TotalAttempts >= TargetAttempts;
            WaitingForContinue = true;
            continueDelay = 10;
            attemptActive = false;

            FreezeManager.Freeze();

            Main.SetStatus(
                Completed
                    ? "Completed - press any key"
                    : "Success - press any key for next attempt");
        }

        private void RecordFail()
        {
            Failures++;
            CurrentStreak = 0;
            Main.SetStatus("FAIL " + Failures + "/" + TotalAttempts);
            attemptActive = false;

            if (TotalAttempts >= TargetAttempts)
            {
                Running = false;
                Completed = true;
                return;
            }

            ScheduleStart(8);
        }

        private void ScheduleStart(int delay)
        {
            startPending = true;
            startDelay = delay;
        }
    }

    internal static class EditorBridge
    {
        private static Type editorType;
        private static MemberInfo instanceMember;
        private static MemberInfo selectedFloorsMember;
        private static MemberInfo playModeMember;
        private static MethodInfo playMethod;
        private static MethodInfo switchToEditMethod;
        private static bool resolved;

        public static bool Exists()
        {
            return Instance() != null;
        }

        public static object RawInstance()
        {
            return Instance();
        }

        public static bool IsPlayMode()
        {
            object editor = Instance();
            if (editor == null) return false;
            try
            {
                object raw = ReadMember(editor, playModeMember);
                return raw is bool && (bool)raw;
            }
            catch { return false; }
        }

        public static bool TryGetSelectionRange(out int first, out int last, out int count)
        {
            first = 0;
            last = 0;
            count = 0;

            object editor = Instance();
            if (editor == null || selectedFloorsMember == null) return false;

            try
            {
                object raw = ReadMember(editor, selectedFloorsMember);
                IEnumerable floors = raw as IEnumerable;
                if (floors == null) return false;

                int min = int.MaxValue;
                int max = int.MinValue;

                foreach (object floor in floors)
                {
                    if (floor == null) continue;
                    int seq;
                    if (!TryGetSeqId(floor, out seq)) continue;
                    if (seq < min) min = seq;
                    if (seq > max) max = seq;
                    count++;
                }

                if (count == 0 || min == int.MaxValue || max == int.MinValue) return false;
                first = min;
                last = max;
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Selection read error: " + ex.Message);
                return false;
            }
        }

        public static bool PlayAt(int floor)
        {
            object editor = Instance();
            if (editor == null || playMethod == null) return false;
            try
            {
                playMethod.Invoke(editor, new object[] { floor, false });
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Editor Play error: " + ex.Message);
                return false;
            }
        }

        public static bool SwitchToEditMode()
        {
            object editor = Instance();
            if (editor == null || switchToEditMethod == null) return false;
            try
            {
                switchToEditMethod.Invoke(editor, new object[] { false });
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("SwitchToEditMode error: " + ex.Message);
                return false;
            }
        }

        private static object Instance()
        {
            Resolve();
            if (editorType == null || instanceMember == null) return null;
            try { return ReadMember(null, instanceMember); }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            editorType = FindType("scnEditor");
            if (editorType == null) return;

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            instanceMember = (MemberInfo)editorType.GetField("instance", all) ?? editorType.GetProperty("instance", all);
            selectedFloorsMember = (MemberInfo)editorType.GetField("selectedFloors", all) ?? editorType.GetProperty("selectedFloors", all);
            playModeMember = (MemberInfo)editorType.GetProperty("playMode", all) ?? editorType.GetField("playMode", all);

            playMethod = editorType.GetMethods(all).FirstOrDefault(m =>
            {
                if (m.Name != "Play") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 2 && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(bool);
            });

            switchToEditMethod = editorType.GetMethods(all).FirstOrDefault(m =>
            {
                if (m.Name != "SwitchToEditMode") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(bool);
            });
        }

        private static bool TryGetSeqId(object floor, out int seq)
        {
            seq = -1;
            Type t = floor.GetType();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            MemberInfo m = (MemberInfo)t.GetField("seqID", all) ?? t.GetProperty("seqID", all);
            if (m == null) return false;
            object raw = ReadMember(floor, m);
            if (raw == null) return false;
            seq = Convert.ToInt32(raw);
            return true;
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
            if (c == null || seqMember == null) return -1;
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

    internal static class FreezeManager
    {
        private static Type timeType;
        private static PropertyInfo timeScaleProperty;
        private static Type audioListenerType;
        private static PropertyInfo audioPauseProperty;
        private static bool frozen;
        private static float previousTimeScale = 1f;
        private static bool previousAudioPause;

        public static void Freeze()
        {
            if (frozen) return;
            Resolve();

            try
            {
                if (timeScaleProperty != null)
                {
                    object raw = timeScaleProperty.GetValue(null, null);
                    if (raw != null) previousTimeScale = Convert.ToSingle(raw);
                    timeScaleProperty.SetValue(null, 0f, null);
                }
            }
            catch { }

            try
            {
                if (audioPauseProperty != null)
                {
                    object raw = audioPauseProperty.GetValue(null, null);
                    if (raw is bool) previousAudioPause = (bool)raw;
                    audioPauseProperty.SetValue(null, true, null);
                }
            }
            catch { }

            frozen = true;
        }

        public static void Unfreeze()
        {
            if (!frozen) return;
            Resolve();

            try
            {
                if (timeScaleProperty != null)
                    timeScaleProperty.SetValue(null, previousTimeScale <= 0f ? 1f : previousTimeScale, null);
            }
            catch { }

            try
            {
                if (audioPauseProperty != null)
                    audioPauseProperty.SetValue(null, previousAudioPause, null);
            }
            catch { }

            frozen = false;
        }

        private static void Resolve()
        {
            if (timeType == null)
            {
                timeType = Type.GetType("UnityEngine.Time, UnityEngine.CoreModule", false);
                if (timeType != null)
                    timeScaleProperty = timeType.GetProperty("timeScale", BindingFlags.Public | BindingFlags.Static);
            }

            if (audioListenerType == null)
            {
                audioListenerType = Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule", false);
                if (audioListenerType != null)
                    audioPauseProperty = audioListenerType.GetProperty("pause", BindingFlags.Public | BindingFlags.Static);
            }
        }
    }

    internal static class RuntimeOverlay
    {
        private static Type behaviourType;

        public static void EnsureAttached()
        {
            try
            {
                object host = EditorBridge.RawInstance() ?? GameBridge.Controller();
                if (host == null) return;

                Type overlayType = GetOrCreateBehaviourType();
                if (overlayType == null) return;

                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                PropertyInfo gameObjectProp = host.GetType().GetProperty("gameObject", all);
                if (gameObjectProp == null) return;

                object gameObject = gameObjectProp.GetValue(host, null);
                if (gameObject == null) return;

                MethodInfo getComponent = gameObject.GetType().GetMethod("GetComponent", all, null, new[] { typeof(Type) }, null);
                MethodInfo addComponent = gameObject.GetType().GetMethod("AddComponent", all, null, new[] { typeof(Type) }, null);
                if (addComponent == null) return;

                if (getComponent != null)
                {
                    object existing = getComponent.Invoke(gameObject, new object[] { overlayType });
                    if (existing != null) return;
                }

                addComponent.Invoke(gameObject, new object[] { overlayType });
            }
            catch (Exception ex)
            {
                Main.Log("Overlay attach error: " + ex.Message);
            }
        }

        private static Type GetOrCreateBehaviourType()
        {
            if (behaviourType != null) return behaviourType;

            try
            {
                Type monoBehaviour = Type.GetType("UnityEngine.MonoBehaviour, UnityEngine.CoreModule", false);
                if (monoBehaviour == null) return null;

                AssemblyName name = new AssemblyName("PracticeStats.RuntimeOverlay");
                AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
                ModuleBuilder module = assembly.DefineDynamicModule("main");
                TypeBuilder type = module.DefineType(
                    "PracticeStatsRuntimeOverlayBehaviour",
                    TypeAttributes.Public | TypeAttributes.Class,
                    monoBehaviour);

                MethodBuilder onGui = type.DefineMethod(
                    "OnGUI",
                    MethodAttributes.Public,
                    typeof(void),
                    Type.EmptyTypes);

                ILGenerator il = onGui.GetILGenerator();
                MethodInfo draw = typeof(Main).GetMethod("DrawRuntimeOverlay", BindingFlags.Public | BindingFlags.Static);
                il.Emit(OpCodes.Call, draw);
                il.Emit(OpCodes.Ret);

                behaviourType = type.CreateTypeInfo().AsType();
                return behaviourType;
            }
            catch (Exception ex)
            {
                Main.Log("Overlay type error: " + ex.Message);
                return null;
            }
        }
    }

    internal static class OverlayRenderer
    {
        private static Type guiType;
        private static Type rectType;
        private static Type screenType;
        private static MethodInfo boxMethod;
        private static MethodInfo labelMethod;
        private static ConstructorInfo rectCtor;
        private static PropertyInfo screenWidth;
        private static bool resolved;

        public static void Draw(string text, bool waiting)
        {
            Resolve();
            if (guiType == null || rectType == null || rectCtor == null || labelMethod == null) return;

            try
            {
                int width = 1280;
                if (screenWidth != null)
                {
                    object raw = screenWidth.GetValue(null, null);
                    if (raw != null) width = Convert.ToInt32(raw);
                }

                float panelWidth = 260f;
                float panelHeight = waiting ? 145f : 105f;
                float x = Math.Max(10f, width - panelWidth - 24f);
                float y = 78f;

                object panelRect = rectCtor.Invoke(new object[] { x, y, panelWidth, panelHeight });
                object textRect = rectCtor.Invoke(new object[] { x + 12f, y + 10f, panelWidth - 24f, panelHeight - 18f });

                if (boxMethod != null)
                    boxMethod.Invoke(null, new object[] { panelRect, "" });

                labelMethod.Invoke(null, new object[] { textRect, text });
            }
            catch { }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            guiType = Type.GetType("UnityEngine.GUI, UnityEngine.IMGUIModule", false);
            rectType = Type.GetType("UnityEngine.Rect, UnityEngine.CoreModule", false);
            screenType = Type.GetType("UnityEngine.Screen, UnityEngine.CoreModule", false);

            if (rectType != null)
                rectCtor = rectType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float), typeof(float) });

            if (screenType != null)
                screenWidth = screenType.GetProperty("width", BindingFlags.Public | BindingFlags.Static);

            if (guiType != null && rectType != null)
            {
                boxMethod = guiType.GetMethod("Box", BindingFlags.Public | BindingFlags.Static, null, new[] { rectType, typeof(string) }, null);
                labelMethod = guiType.GetMethod("Label", BindingFlags.Public | BindingFlags.Static, null, new[] { rectType, typeof(string) }, null);
            }
        }
    }

    internal static class UnityBridge
    {
        private static Type inputType;
        private static Type keyCodeType;
        private static MethodInfo getKeyDown;
        private static PropertyInfo anyKeyDown;

        public static bool GetKeyDown(string key)
        {
            Resolve();
            try
            {
                if (getKeyDown == null || keyCodeType == null) return false;
                object code = Enum.Parse(keyCodeType, key, true);
                object result = getKeyDown.Invoke(null, new[] { code });
                return result is bool && (bool)result;
            }
            catch { return false; }
        }

        public static bool AnyKeyDown()
        {
            Resolve();
            try
            {
                if (anyKeyDown == null) return false;
                object result = anyKeyDown.GetValue(null, null);
                return result is bool && (bool)result;
            }
            catch { return false; }
        }

        private static void Resolve()
        {
            if (inputType != null) return;

            inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule", false);
            keyCodeType = Type.GetType("UnityEngine.KeyCode, UnityEngine.CoreModule", false);

            if (inputType != null)
            {
                anyKeyDown = inputType.GetProperty("anyKeyDown", BindingFlags.Public | BindingFlags.Static);
                if (keyCodeType != null)
                    getKeyDown = inputType.GetMethod("GetKeyDown", BindingFlags.Public | BindingFlags.Static, null, new[] { keyCodeType }, null);
            }
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
