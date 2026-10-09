using System;
using System.Collections;
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
            Log("PracticeStats 0.1.1v loaded (ADOFAI 3.4.0 target).");
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry entry, bool value)
        {
            enabled = value;
            if (!value)
            {
                session.Stop();
                RuntimeOverlay.Hide();
            }
            status = value ? "Enabled" : "Disabled";
            return true;
        }

        private static bool OnUnload(UnityModManager.ModEntry entry)
        {
            session.Stop();
            RuntimeOverlay.Hide();
            enabled = false;
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry entry, float deltaTime)
        {
            if (!enabled) return;

            try
            {
                SyncRangeFromEditor(false);

                if (UnityBridge.GetKeyDown("F8"))
                    TogglePractice();

                if (UnityBridge.GetKeyDown("F9"))
                {
                    session.ResetStats();
                    status = "Stats reset";
                }

                session.Tick();
                UpdateRuntimeOverlay();
            }
            catch (Exception ex)
            {
                Log("Update error: " + ex);
            }
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            try
            {
                SyncRangeFromEditor(false);

                RGui.Label("PracticeStats 0.1.1v - ADOFAI 3.4.0");
                RGui.Label("Uses ADOFAI built-in practice mode");
                RGui.Label("Range: editor Shift + Left Click selection");
                RGui.Space(6f);

                if (hasEditorRange)
                    RGui.Label("Selected range: " + session.StartFloor + " -> " + session.EndFloor + " (" + selectedCount + " tiles)");
                else if (session.Running)
                    RGui.Label("Active range: " + session.StartFloor + " -> " + session.EndFloor);
                else
                    RGui.Label("Selected range: none");

                RGui.Space(6f);
                RGui.Label("Target attempts");
                attemptsText = RGui.TextField(attemptsText);

                RGui.Space(6f);
                if (RGui.Button(session.Running ? "Stop practice" : "Start practice"))
                    TogglePractice();

                if (RGui.Button("Reset stats"))
                {
                    session.ResetStats();
                    status = "Stats reset";
                }

                RGui.Space(8f);
                RGui.Label("Attempts: " + session.TotalAttempts + " / " + session.TargetAttempts);
                RGui.Label("Success: " + session.Successes + "    Fail: " + session.Failures);
                RGui.Label("Success rate: " + session.SuccessRate.ToString("0.00") + "%");
                RGui.Label("Status: " + status);
                RGui.Space(6f);
                RGui.Label("Hotkeys: F8 start/stop, F9 reset");
            }
            catch (Exception ex)
            {
                Log("GUI error: " + ex);
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
            if (session.TotalAttempts > 0)
                session.ResetStats();

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

            if (!session.Start())
            {
                status = "Could not start ADOFAI practice mode";
                return;
            }

            status = "Practice started";
        }

        private static void ApplyAttempts()
        {
            int v;
            if (int.TryParse(attemptsText, out v))
                session.TargetAttempts = Math.Max(1, Math.Min(100000, v));
            attemptsText = session.TargetAttempts.ToString();
        }

        private static void UpdateRuntimeOverlay()
        {
            if (!enabled || !session.Running)
            {
                RuntimeOverlay.Hide();
                return;
            }

            int remaining = Math.Max(0, session.TargetAttempts - session.TotalAttempts);
            string prompt = "";

            if (session.Completed)
            {
                prompt = "\n\nCOMPLETE - press ESC to return to editor";
            }
            else if (session.WaitingForSuccessContinue)
            {
                prompt = "\n\nSUCCESS - press any key";
            }
            else if (session.FailScreenActive)
            {
                prompt = "\n\nFAIL - press any key";
            }

            string text =
                "PracticeStats\n" +
                "Remaining: " + remaining + "\n" +
                "Cleared: " + session.Successes + "\n" +
                "Success rate: " + session.SuccessRate.ToString("0.00") + "%" +
                prompt;

            RuntimeOverlay.Show(text);
        }

        internal static void SetStatus(string text)
        {
            status = text;
        }

        internal static void Log(string text)
        {
            try
            {
                if (mod != null && mod.Logger != null)
                    mod.Logger.Log("[PracticeStats] " + text);
            }
            catch { }
        }
    }

    internal sealed class PracticeSession
    {
        public int StartFloor = 0;
        public int EndFloor = 1;
        public int TargetAttempts = 100;

        public int AttemptsStarted { get; private set; }
        public int Successes { get; private set; }
        public int Failures { get; private set; }
        public int CurrentStreak { get; private set; }
        public int BestStreak { get; private set; }

        public bool Running { get; private set; }
        public bool Completed { get; private set; }
        public bool WaitingForSuccessContinue { get; private set; }
        public bool FailScreenActive { get; private set; }

        public int TotalAttempts { get { return AttemptsStarted; } }
        public float SuccessRate { get { return TotalAttempts == 0 ? 0f : Successes * 100f / TotalAttempts; } }

        private int graceFrames;
        private int lastDeaths = -1;
        private bool failCounted;
        private bool attemptPending;

        private bool initializing;
        private int initializeStage;
        private int initializeDelay;
        private int initializeTimeout;

        // In the editor, ADOFAI's Won_Update does not consume the result-screen
        // continue key. We detect it, but restart on a later frame so the key
        // that dismissed the result screen never leaks into the next attempt.
        private bool continueRequested;
        private int continueDelay;

        // ResetCustomLevel rebuilds floor runtime state. The built-in practice endpoint
        // (temporary EndOfLevel portal) must be restored after every rebuild.
        private int endpointRearmFrames;

        public bool CanStart(out string reason)
        {
            if (EndFloor <= StartFloor)
            {
                reason = "End tile must be after start tile";
                return false;
            }

            if (TargetAttempts < 1)
            {
                reason = "Target attempts must be at least 1";
                return false;
            }

            if (!EditorBridge.Exists())
            {
                reason = "Open the level editor first";
                return false;
            }

            if (EditorBridge.IsPlayMode())
            {
                reason = "Stop editor playback before starting PracticeStats";
                return false;
            }

            reason = null;
            return true;
        }

        public bool Start()
        {
            if (TotalAttempts >= TargetAttempts)
                ResetStats();

            SuccessFreeze.Unfreeze();
            BuiltInPractice.Disable();

            if (!EditorBridge.PlayFromSingleFloor(StartFloor))
                return false;

            Running = true;
            Completed = false;
            WaitingForSuccessContinue = false;
            FailScreenActive = false;
            failCounted = false;
            attemptPending = false;
            continueRequested = false;
            continueDelay = 0;
            endpointRearmFrames = 0;

            initializing = true;
            initializeStage = 0;
            initializeDelay = 2;
            initializeTimeout = 240;
            graceFrames = 0;
            lastDeaths = GameBridge.Deaths();

            Main.SetStatus("Preparing built-in practice mode");
            return true;
        }

        public void Stop()
        {
            Running = false;
            WaitingForSuccessContinue = false;
            FailScreenActive = false;
            failCounted = false;
            initializing = false;
            continueRequested = false;
            attemptPending = false;
            endpointRearmFrames = 0;
            SuccessFreeze.Unfreeze();
            BuiltInPractice.Disable();
            GameBridge.SetPlayersResponsive(true);
            GameBridge.UnlockPlayerInput();
        }

        public void ResetStats()
        {
            AttemptsStarted = 0;
            Successes = 0;
            Failures = 0;
            attemptPending = false;
            CurrentStreak = 0;
            BestStreak = 0;
            Completed = false;
            lastDeaths = GameBridge.Deaths();
        }

        public void Tick()
        {
            if (!Running) return;

            if (endpointRearmFrames > 0)
            {
                // This mirrors ADOFAI's own Awake_Rewind practice-mode endpoint setup.
                // Run it for a few frames because ResetCustomLevel/Play can rebuild floors
                // across the restart boundary.
                BuiltInPractice.Configure(StartFloor, EndFloor);
                BuiltInPractice.ReapplyEndpoint(StartFloor, EndFloor);
                endpointRearmFrames--;
            }

            if (initializing)
            {
                TickInitialization();
                return;
            }

            // COMPLETE is a separate state. It never starts another attempt.
            // It only waits for a valid ADOFAI input, then exits practice and
            // returns to the editor with the range restored.
            if (Completed)
            {
                TickComplete();
                return;
            }

            // ESC / the editor stop button runs scnEditor.SwitchToEditMode(),
            // which clears GCS.practiceMode. Treat that as an explicit stop of
            // PracticeStats as well, otherwise stale session state deadlocks
            // the next F8 start.
            if (EditorBridge.Exists() && !EditorBridge.IsPlayMode())
            {
                ReturnToEditor();
                return;
            }

            string state = GameBridge.StateName();
            int current = GameBridge.CurrentFloor();
            int deaths = GameBridge.Deaths();

            if (WaitingForSuccessContinue)
            {
                TickSuccessContinue(state, current, deaths);
                return;
            }

            if (graceFrames > 0)
            {
                graceFrames--;
                if (deaths >= 0) lastDeaths = deaths;
                return;
            }

            bool deathIncreased = deaths >= 0 && lastDeaths >= 0 && deaths > lastDeaths;
            if (deaths >= 0) lastDeaths = deaths;

            bool isFail = ContainsState(state, "Fail");

            if ((deathIncreased || isFail) && !failCounted)
            {
                if (attemptPending) RecordFail();
                attemptPending = false;
                failCounted = true;
                FailScreenActive = true;

                if (TotalAttempts >= TargetAttempts)
                {
                    Completed = true;
                    Main.SetStatus("Completed - press ESC to return to editor");
                }
                else
                {
                    Main.SetStatus("Fail - press any key");
                }
                return;
            }

            if (failCounted)
            {
                if (isFail)
                {
                    FailScreenActive = true;
                    return;
                }

                if (current >= StartFloor && current < EndFloor)
                {
                    BuiltInPractice.Configure(StartFloor, EndFloor);
                    BuiltInPractice.ReapplyEndpoint(StartFloor, EndFloor);
                    endpointRearmFrames = 6;

                    failCounted = false;
                    attemptPending = true;
                    AttemptsStarted++;
                    FailScreenActive = false;
                    graceFrames = 12;
                    lastDeaths = deaths;
                    Main.SetStatus("Next attempt");
                }

                return;
            }

            bool isWon = ContainsState(state, "Won");

            // Native practice portals ultimately call scrController.OnLandOnPortal().
            // If a rebuilt editor attempt lost the portal's runtime trigger, call the
            // SAME native finish method when the player reaches the selected end tile.
            // Statistics still count only after the controller actually enters Won.
            if (!isWon && current >= EndFloor)
            {
                GameBridge.TriggerNativePracticeFinish();
                state = GameBridge.StateName();
                isWon = ContainsState(state, "Won");
            }

            if (isWon)
            {
                if (attemptPending) RecordSuccess();
                attemptPending = false;
                Completed = TotalAttempts >= TargetAttempts;
                WaitingForSuccessContinue = true;
                continueRequested = false;
                continueDelay = 0;

                // Freeze the exact successful frame. Camera/VFX/audio remain
                // stopped until the next attempt is actually being started.
                SuccessFreeze.Freeze();

                Main.SetStatus(
                    Completed
                        ? "Completed - press ESC to return to editor"
                        : "Success - press any key");
            }
        }

        private void TickSuccessContinue(string state, int current, int deaths)
        {
            if (!continueRequested)
            {
                if (!GameBridge.AnyValidInputWasTriggered()) return;

                continueRequested = true;
                continueDelay = 2;
                return;
            }

            if (continueDelay-- > 0) return;

            // If the pressed key was ESC, the editor may have switched modes
            // one frame later. Do not resurrect a new attempt in edit mode.
            if (EditorBridge.Exists() && !EditorBridge.IsPlayMode())
            {
                ReturnToEditor();
                return;
            }

            // IMPORTANT: successful-frame freeze is lifted only here, directly
            // before the next attempt is rebuilt.
            SuccessFreeze.Unfreeze();

            BuiltInPractice.Configure(StartFloor, EndFloor);

            if (!GameBridge.RestartCustomLevelBuiltIn(true))
            {
                Running = false;
                WaitingForSuccessContinue = false;
                continueRequested = false;
                BuiltInPractice.Disable();
                Main.SetStatus("Could not restart practice attempt");
                return;
            }

            endpointRearmFrames = 8;
            attemptPending = true;
            AttemptsStarted++;

            WaitingForSuccessContinue = false;
            FailScreenActive = false;
            failCounted = false;
            continueRequested = false;
            graceFrames = 20;
            lastDeaths = deaths;
            Main.SetStatus("Next attempt");
        }

        private void TickComplete()
        {
            // COMPLETE is intentionally ESC-only. Other gameplay keys must do
            // absolutely nothing so the final result cannot be dismissed by
            // an accidental hit key.
            if (EditorBridge.Exists() && !EditorBridge.IsPlayMode())
            {
                CompleteSession();
                return;
            }

            if (!continueRequested)
            {
                if (!UnityBridge.GetKeyDown("Escape")) return;

                continueRequested = true;
                continueDelay = 2;
                return;
            }

            if (continueDelay-- > 0) return;

            CompleteSession();
        }

        private void CompleteSession()
        {
            // COMPLETE must never schedule another attempt.
            SuccessFreeze.Unfreeze();

            Running = false;
            initializing = false;
            WaitingForSuccessContinue = false;
            FailScreenActive = false;
            failCounted = false;
            continueRequested = false;
            endpointRearmFrames = 0;

            SuccessFreeze.Unfreeze();
            BuiltInPractice.Disable();
            GameBridge.SetPlayersResponsive(true);
            GameBridge.UnlockPlayerInput();

            // Explicitly return to editor. The previous implementation only
            // stopped PracticeStats while leaving ADOFAI sitting in Won/Fail.
            EditorBridge.SwitchToEditMode();
            EditorBridge.RestoreRangeSelection(StartFloor, EndFloor);

            Main.SetStatus("Completed - returned to editor");
        }

        private void TickInitialization()
        {
            if (initializeTimeout-- <= 0)
            {
                Running = false;
                initializing = false;
                BuiltInPractice.Disable();
                Main.SetStatus("Practice initialization timed out");
                return;
            }

            if (initializeDelay > 0)
            {
                initializeDelay--;
                return;
            }

            // If the user immediately cancelled playback with ESC, do not try
            // to apply practice state to an editor that has already stopped.
            if (EditorBridge.Exists() && !EditorBridge.IsPlayMode())
            {
                ReturnToEditor();
                return;
            }

            if (initializeStage == 0)
            {
                BuiltInPractice.Configure(StartFloor, EndFloor);

                if (!GameBridge.RestartCustomLevelBuiltIn(true))
                {
                    Running = false;
                    initializing = false;
                    BuiltInPractice.Disable();
                    Main.SetStatus("Could not initialize built-in practice mode");
                    return;
                }

                endpointRearmFrames = 8;
                initializeStage = 1;
                initializeDelay = 2;
                return;
            }

            string state = GameBridge.StateName();
            int current = GameBridge.CurrentFloor();

            bool invalidState =
                ContainsState(state, "Won") ||
                ContainsState(state, "Fail");

            if (!invalidState && current >= StartFloor && current < EndFloor)
            {
                BuiltInPractice.Configure(StartFloor, EndFloor);
                BuiltInPractice.ReapplyEndpoint(StartFloor, EndFloor);
                endpointRearmFrames = Math.Max(endpointRearmFrames, 4);

                initializing = false;
                attemptPending = true;
                AttemptsStarted++;
                failCounted = false;
                WaitingForSuccessContinue = false;
                FailScreenActive = false;
                continueRequested = false;
                graceFrames = 12;
                lastDeaths = GameBridge.Deaths();
                Main.SetStatus("Practice started");
            }
        }

        private void ReturnToEditor()
        {
            Running = false;
            initializing = false;
            WaitingForSuccessContinue = false;
            FailScreenActive = false;
            failCounted = false;
            continueRequested = false;
            endpointRearmFrames = 0;

            BuiltInPractice.Disable();
            GameBridge.SetPlayersResponsive(true);
            GameBridge.UnlockPlayerInput();

            // Restore the user's practice range after scnEditor.SwitchToEditMode()
            // has selected only one floor. This also lets F8 be used again
            // without having to rebuild the range manually.
            EditorBridge.RestoreRangeSelection(StartFloor, EndFloor);

            Main.SetStatus("Returned to editor");
        }

        private void RecordSuccess()
        {
            Successes++;
            CurrentStreak++;
            if (CurrentStreak > BestStreak)
                BestStreak = CurrentStreak;

            // Editor practice bypasses ADOFAI's normal custom-level launch
            // flow, so the game's persisted CustomWorld_{hash}_Attempts value
            // is not incremented automatically. Count every finished practice
            // attempt here, including clears.
            MapAttemptPersistence.IncrementCurrentMapAttempt();
        }

        private void RecordFail()
        {
            Failures++;
            CurrentStreak = 0;

            // Failures must contribute to the same per-map attempt counter.
            MapAttemptPersistence.IncrementCurrentMapAttempt();
        }

        private static bool ContainsState(string state, string token)
        {
            return !string.IsNullOrEmpty(state) &&
                   state.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal static class MapAttemptPersistence
    {
        private static Type adoBaseType;
        private static MemberInfo customLevelMember;
        private static Type persistenceType;
        private static MethodInfo incrementAttemptsMethod;
        private static bool resolved;
        private static bool loggedFailure;

        public static bool IncrementCurrentMapAttempt()
        {
            Resolve();

            try
            {
                if (adoBaseType == null ||
                    customLevelMember == null ||
                    persistenceType == null ||
                    incrementAttemptsMethod == null)
                {
                    LogFailureOnce("required ADOFAI persistence API was not found");
                    return false;
                }

                object customLevel =
                    ReflectionUtil.ReadMember(null, customLevelMember);

                if (customLevel == null)
                {
                    LogFailureOnce("ADOBase.customLevel is null");
                    return false;
                }

                const BindingFlags all =
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance;

                MemberInfo levelDataMember =
                    ReflectionUtil.FindMember(customLevel.GetType(), "levelData", all);

                if (levelDataMember == null)
                {
                    LogFailureOnce("customLevel.levelData was not found");
                    return false;
                }

                object levelData =
                    ReflectionUtil.ReadMember(customLevel, levelDataMember);

                if (levelData == null)
                {
                    LogFailureOnce("customLevel.levelData is null");
                    return false;
                }

                MemberInfo hashMember =
                    ReflectionUtil.FindMember(levelData.GetType(), "Hash", all) ??
                    ReflectionUtil.FindMember(levelData.GetType(), "hash", all);

                if (hashMember == null)
                {
                    LogFailureOnce("levelData.Hash was not found");
                    return false;
                }

                object rawHash =
                    ReflectionUtil.ReadMember(levelData, hashMember);

                string hash = rawHash == null ? null : rawHash.ToString();

                if (string.IsNullOrEmpty(hash))
                {
                    LogFailureOnce("current custom-level hash is empty");
                    return false;
                }

                // This is ADOFAI's own persistent map-attempt API:
                // Persistence.IncrementCustomWorldAttempts(hash)
                // -> CustomWorld_{hash}_Attempts += 1
                // -> Persistence.Save().
                incrementAttemptsMethod.Invoke(
                    null,
                    new object[] { hash });

                return true;
            }
            catch (Exception ex)
            {
                LogFailureOnce("exception: " + ex.Message);
                return false;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            adoBaseType = ReflectionUtil.FindType("ADOBase");
            if (adoBaseType != null)
            {
                customLevelMember =
                    ReflectionUtil.FindMember(
                        adoBaseType,
                        "customLevel",
                        all);
            }

            persistenceType = ReflectionUtil.FindType("Persistence");
            if (persistenceType != null)
            {
                incrementAttemptsMethod =
                    persistenceType.GetMethods(all)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "IncrementCustomWorldAttempts")
                            return false;

                        ParameterInfo[] p = m.GetParameters();

                        return p.Length == 1 &&
                               p[0].ParameterType == typeof(string);
                    });
            }
        }

        private static void LogFailureOnce(string reason)
        {
            if (loggedFailure) return;
            loggedFailure = true;
            Main.Log("Map attempt persistence unavailable: " + reason);
        }
    }

    internal static class BuiltInPractice
    {
        private static Type gcsType;
        private static MemberInfo practiceMode;
        private static MemberInfo checkpointNum;
        private static MemberInfo practiceLength;
        private static MemberInfo checkpointBeforePractice;
        private static MemberInfo speedTrialMode;
        private static bool resolved;

        private static Type levelMakerType;
        private static MemberInfo levelMakerInstance;
        private static MemberInfo listFloorsMember;
        private static Type portalType;
        private static object endOfLevelValue;

        private static bool savedCheckpointCaptured;
        private static int savedCheckpoint;

        public static void Configure(int startFloor, int endFloor)
        {
            Resolve();
            if (gcsType == null) return;

            if (!savedCheckpointCaptured)
            {
                savedCheckpoint = ReadInt(checkpointNum, 0);
                savedCheckpointCaptured = true;
            }

            Write(checkpointBeforePractice, savedCheckpoint);
            Write(practiceMode, true);
            Write(checkpointNum, startFloor);
            Write(practiceLength, Math.Max(1, endFloor - startFloor));

            // Built-in practice mode disables speed trial. Editor playback speed remains
            // controlled by the editor itself.
            Write(speedTrialMode, false);

            GameBridge.SetCheckpointsUsed(1);
        }

        public static bool ReapplyEndpoint(int startFloor, int endFloor)
        {
            Resolve();
            ResolveEndpointSupport();

            if (levelMakerType == null || levelMakerInstance == null ||
                listFloorsMember == null || endOfLevelValue == null)
                return false;

            try
            {
                object lm = ReflectionUtil.ReadMember(null, levelMakerInstance);
                if (lm == null) return false;

                IList floors = ReflectionUtil.ReadMember(lm, listFloorsMember) as IList;
                if (floors == null || floors.Count == 0) return false;

                int endpoint = Math.Max(0, Math.Min(endFloor, floors.Count - 1));
                bool found = false;

                foreach (object floor in floors)
                {
                    if (floor == null) continue;

                    Type floorType = floor.GetType();
                    const BindingFlags all =
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static;

                    MemberInfo seqMember = ReflectionUtil.FindMember(floorType, "seqID", all);
                    MemberInfo portalMember = ReflectionUtil.FindMember(floorType, "isportal", all);
                    MemberInfo levelNumberMember = ReflectionUtil.FindMember(floorType, "levelnumber", all);

                    if (seqMember == null || portalMember == null || levelNumberMember == null)
                        continue;

                    int seq = Convert.ToInt32(ReflectionUtil.ReadMember(floor, seqMember));
                    bool wasPortal = false;
                    object oldPortal = ReflectionUtil.ReadMember(floor, portalMember);
                    if (oldPortal is bool) wasPortal = (bool)oldPortal;

                    bool shouldBeEndpoint = seq == endpoint;

                    // This is the same practice-mode endpoint rule used by
                    // scrController.Awake_Rewind: the selected endpoint becomes
                    // EndOfLevel, and any previous temporary portal is cleared.
                    if (shouldBeEndpoint || wasPortal)
                    {
                        ReflectionUtil.WriteMember(floor, portalMember, shouldBeEndpoint);
                        ReflectionUtil.WriteMember(floor, levelNumberMember, endOfLevelValue);

                        MethodInfo updateIcon = floorType.GetMethods(all)
                            .FirstOrDefault(m =>
                            {
                                if (m.Name != "UpdateIconSprite") return false;
                                ParameterInfo[] p = m.GetParameters();
                                return p.Length == 0 ||
                                       (p.Length == 1 && p[0].ParameterType == typeof(bool));
                            });

                        if (updateIcon != null)
                        {
                            ParameterInfo[] p = updateIcon.GetParameters();
                            if (p.Length == 0)
                                updateIcon.Invoke(floor, null);
                            else
                                updateIcon.Invoke(floor, new object[] { false });
                        }
                    }

                    if (shouldBeEndpoint)
                        found = true;
                }

                return found;
            }
            catch (Exception ex)
            {
                Main.Log("Practice endpoint reapply error: " + ex.Message);
                return false;
            }
        }

        private static void ResolveEndpointSupport()
        {
            if (levelMakerType != null && portalType != null) return;

            levelMakerType = ReflectionUtil.FindType("scrLevelMaker");
            portalType = ReflectionUtil.FindType("Portal");

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            if (levelMakerType != null)
            {
                levelMakerInstance = ReflectionUtil.FindMember(levelMakerType, "instance", all);
                listFloorsMember = ReflectionUtil.FindMember(levelMakerType, "listFloors", all);
            }

            if (portalType != null && portalType.IsEnum)
            {
                try { endOfLevelValue = Enum.Parse(portalType, "EndOfLevel", true); }
                catch { endOfLevelValue = null; }
            }
        }

        public static void Disable()
        {
            Resolve();
            if (gcsType == null) return;

            Write(practiceMode, false);

            if (savedCheckpointCaptured)
                Write(checkpointNum, savedCheckpoint);

            savedCheckpointCaptured = false;
            GameBridge.SetCheckpointsUsed(0);
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            gcsType = ReflectionUtil.FindType("GCS");
            if (gcsType == null) return;

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            practiceMode = ReflectionUtil.FindMember(gcsType, "practiceMode", all);
            checkpointNum = ReflectionUtil.FindMember(gcsType, "checkpointNum", all);
            practiceLength = ReflectionUtil.FindMember(gcsType, "practiceLength", all);
            checkpointBeforePractice = ReflectionUtil.FindMember(gcsType, "checkpointBeforePractice", all);
            speedTrialMode = ReflectionUtil.FindMember(gcsType, "speedTrialMode", all);
        }

        private static int ReadInt(MemberInfo member, int fallback)
        {
            try
            {
                object value = ReflectionUtil.ReadMember(null, member);
                return value == null ? fallback : Convert.ToInt32(value);
            }
            catch { return fallback; }
        }

        private static void Write(MemberInfo member, object value)
        {
            if (member == null) return;
            try { ReflectionUtil.WriteMember(null, member, value); }
            catch { }
        }
    }

    internal static class EditorBridge
    {
        private static Type editorType;
        private static MemberInfo instanceMember;
        private static MemberInfo selectedFloorsMember;
        private static MemberInfo floorsMember;
        private static MemberInfo playModeMember;
        private static MethodInfo playNoArgs;
        private static MethodInfo playWithArgs;
        private static MethodInfo multiSelectFloorsMethod;
        private static MethodInfo switchToEditModeMethod;
        private static bool resolved;

        public static bool Exists()
        {
            return Instance() != null;
        }

        public static bool IsPlayMode()
        {
            object editor = Instance();
            if (editor == null) return false;

            try
            {
                object value = ReflectionUtil.ReadMember(editor, playModeMember);
                return value is bool && (bool)value;
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
                IEnumerable floors = ReflectionUtil.ReadMember(editor, selectedFloorsMember) as IEnumerable;
                if (floors == null) return false;

                int min = int.MaxValue;
                int max = int.MinValue;

                foreach (object floor in floors)
                {
                    if (floor == null) continue;

                    int seq;
                    if (!TryGetSeqId(floor, out seq)) continue;

                    min = Math.Min(min, seq);
                    max = Math.Max(max, seq);
                    count++;
                }

                if (count == 0 || min == int.MaxValue || max == int.MinValue)
                    return false;

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

        public static bool PlayFromSingleFloor(int floorIndex)
        {
            object editor = Instance();
            if (editor == null) return false;

            try
            {
                if (playNoArgs != null)
                {
                    IList selected = ReflectionUtil.ReadMember(editor, selectedFloorsMember) as IList;
                    IList floors = ReflectionUtil.ReadMember(editor, floorsMember) as IList;

                    if (selected == null || floors == null || floorIndex < 0 || floorIndex >= floors.Count)
                    {
                        Main.Log("Could not prepare editor selection for playback.");
                        return false;
                    }

                    selected.Clear();
                    selected.Add(floors[floorIndex]);
                    playNoArgs.Invoke(editor, null);
                    return true;
                }

                if (playWithArgs != null)
                {
                    playWithArgs.Invoke(editor, new object[] { floorIndex, false });
                    return true;
                }
            }
            catch (Exception ex)
            {
                Main.Log("Editor Play error: " + ex);
            }

            return false;
        }

        public static bool SwitchToEditMode()
        {
            object editor = Instance();
            if (editor == null || switchToEditModeMethod == null)
                return false;

            try
            {
                switchToEditModeMethod.Invoke(editor, new object[] { false });
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("SwitchToEditMode error: " + ex.Message);
                return false;
            }
        }

        public static bool RestoreRangeSelection(int startFloor, int endFloor)
        {
            object editor = Instance();
            if (editor == null || multiSelectFloorsMethod == null || floorsMember == null)
                return false;

            try
            {
                IList floors = ReflectionUtil.ReadMember(editor, floorsMember) as IList;
                if (floors == null || floors.Count == 0)
                    return false;

                int start = Math.Max(0, Math.Min(startFloor, floors.Count - 1));
                int end = Math.Max(0, Math.Min(endFloor, floors.Count - 1));

                if (end <= start)
                    return false;

                multiSelectFloorsMethod.Invoke(
                    editor,
                    new object[] { floors[start], floors[end], false });

                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Restore range selection error: " + ex.Message);
                return false;
            }
        }

        private static object Instance()
        {
            Resolve();
            if (editorType == null || instanceMember == null) return null;

            try { return ReflectionUtil.ReadMember(null, instanceMember); }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            editorType = ReflectionUtil.FindType("scnEditor");
            if (editorType == null) return;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            instanceMember = ReflectionUtil.FindMember(editorType, "instance", all);
            selectedFloorsMember = ReflectionUtil.FindMember(editorType, "selectedFloors", all);
            floorsMember = ReflectionUtil.FindMember(editorType, "floors", all);
            playModeMember = ReflectionUtil.FindMember(editorType, "playMode", all);

            MethodInfo[] methods = editorType.GetMethods(all);
            playNoArgs = methods.FirstOrDefault(m => m.Name == "Play" && m.GetParameters().Length == 0);
            playWithArgs = methods.FirstOrDefault(m =>
            {
                if (m.Name != "Play") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 2 &&
                       p[0].ParameterType == typeof(int) &&
                       p[1].ParameterType == typeof(bool);
            });

            multiSelectFloorsMethod = methods.FirstOrDefault(m =>
            {
                if (m.Name != "MultiSelectFloors") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 3 &&
                       p[2].ParameterType == typeof(bool);
            });

            switchToEditModeMethod = methods.FirstOrDefault(m =>
            {
                if (m.Name != "SwitchToEditMode") return false;
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(bool);
            });
        }

        private static bool TryGetSeqId(object floor, out int seq)
        {
            seq = -1;
            if (floor == null) return false;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            MemberInfo member = ReflectionUtil.FindMember(floor.GetType(), "seqID", all);
            if (member == null) return false;

            try
            {
                object value = ReflectionUtil.ReadMember(floor, member);
                if (value == null) return false;
                seq = Convert.ToInt32(value);
                return true;
            }
            catch { return false; }
        }
    }

    internal static class CustomLevelBridge
    {
        private static Type adoBaseType;
        private static MemberInfo customLevelMember;
        private static bool resolved;

        public static bool Restart(int startFloor)
        {
            Resolve();

            object customLevel = CustomLevel();
            if (customLevel == null) return false;

            try
            {
                Type t = customLevel.GetType();
                const BindingFlags all =
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance;

                MethodInfo reset = t.GetMethods(all).FirstOrDefault(m =>
                {
                    if (m.Name != "ResetScene") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(bool);
                });

                MethodInfo play2 = t.GetMethods(all).FirstOrDefault(m =>
                {
                    if (m.Name != "Play") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 2 &&
                           p[0].ParameterType == typeof(int) &&
                           p[1].ParameterType == typeof(bool);
                });

                MethodInfo play1 = t.GetMethods(all).FirstOrDefault(m =>
                {
                    if (m.Name != "Play") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(int);
                });

                if (reset == null || (play2 == null && play1 == null))
                    return false;

                reset.Invoke(customLevel, new object[] { true });

                if (play2 != null)
                    play2.Invoke(customLevel, new object[] { startFloor, true });
                else
                    play1.Invoke(customLevel, new object[] { startFloor });

                GameBridge.SetTransitioningLevel(false);
                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Custom level restart error: " + ex);
                return false;
            }
        }

        private static object CustomLevel()
        {
            Resolve();
            if (adoBaseType == null || customLevelMember == null) return null;

            try { return ReflectionUtil.ReadMember(null, customLevelMember); }
            catch { return null; }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            adoBaseType = ReflectionUtil.FindType("ADOBase");
            if (adoBaseType == null) return;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            customLevelMember = ReflectionUtil.FindMember(adoBaseType, "customLevel", all);
        }
    }

    internal static class GameBridge
    {
        private static Type controllerType;
        private static MemberInfo instanceMember;
        private static MemberInfo seqMember;
        private static MemberInfo deathsMember;
        private static MemberInfo stateMember;
        private static MemberInfo checkpointsUsedMember;
        private static MemberInfo transitioningLevelMember;
        private static MemberInfo winTimeMember;
        private static MemberInfo chosenPlanetMember;
        private static MethodInfo resetCustomLevelMethod;
        private static MethodInfo startCoroutineMethod;
        private static MethodInfo onLandOnPortalMethod;
        private static Type portalType;
        private static object endOfLevelPortal;

        private static Type adoBaseType;
        private static MemberInfo playerManagerMember;
        private static MethodInfo anyValidInputMethod;
        private static MethodInfo setResponsiveMethod;
        private static MethodInfo unlockInputMethod;

        private static bool resolved;

        public static object Controller()
        {
            Resolve();
            if (controllerType == null || instanceMember == null) return null;

            try { return ReflectionUtil.ReadMember(null, instanceMember); }
            catch { return null; }
        }

        public static int CurrentFloor()
        {
            object controller = Controller();
            if (controller == null || seqMember == null) return -1;

            try
            {
                object value = ReflectionUtil.ReadMember(controller, seqMember);
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
                object target = IsStatic(deathsMember) ? null : Controller();
                object value = ReflectionUtil.ReadMember(target, deathsMember);
                return value == null ? -1 : Convert.ToInt32(value);
            }
            catch { return -1; }
        }

        public static string StateName()
        {
            object controller = Controller();
            if (controller == null || stateMember == null) return "";

            try
            {
                object value = ReflectionUtil.ReadMember(controller, stateMember);
                return value == null ? "" : value.ToString();
            }
            catch { return ""; }
        }

        public static void SetCheckpointsUsed(int value)
        {
            object controller = Controller();
            if (controller == null || checkpointsUsedMember == null) return;

            try { ReflectionUtil.WriteMember(controller, checkpointsUsedMember, value); }
            catch { }
        }

        public static void SetTransitioningLevel(bool value)
        {
            object controller = Controller();
            if (controller == null || transitioningLevelMember == null) return;

            try { ReflectionUtil.WriteMember(controller, transitioningLevelMember, value); }
            catch { }
        }

        public static bool AnyValidInputWasTriggered()
        {
            Resolve();

            try
            {
                object pm = PlayerManager();
                if (pm == null || anyValidInputMethod == null)
                    return UnityBridge.AnyKeyDown();

                object result = anyValidInputMethod.Invoke(pm, null);
                return result is bool && (bool)result;
            }
            catch
            {
                return UnityBridge.AnyKeyDown();
            }
        }

        public static void SetPlayersResponsive(bool responsive)
        {
            Resolve();

            try
            {
                object pm = PlayerManager();
                if (pm != null && setResponsiveMethod != null)
                    setResponsiveMethod.Invoke(pm, new object[] { responsive });
            }
            catch (Exception ex)
            {
                Main.Log("SetAllPlayerResponsive error: " + ex.Message);
            }
        }

        public static void UnlockPlayerInput()
        {
            Resolve();

            try
            {
                object pm = PlayerManager();
                if (pm != null && unlockInputMethod != null)
                    unlockInputMethod.Invoke(pm, null);
            }
            catch (Exception ex)
            {
                Main.Log("UnlockAllPlayerInput error: " + ex.Message);
            }
        }

        private static object PlayerManager()
        {
            if (adoBaseType == null || playerManagerMember == null)
                return null;

            try { return ReflectionUtil.ReadMember(null, playerManagerMember); }
            catch { return null; }
        }

        public static bool TriggerNativePracticeFinish()
        {
            object controller = Controller();
            if (controller == null) return false;

            Resolve();

            try
            {
                if (onLandOnPortalMethod == null ||
                    chosenPlanetMember == null ||
                    endOfLevelPortal == null)
                    return false;

                object planet = ReflectionUtil.ReadMember(controller, chosenPlanetMember);
                if (planet == null) return false;

                onLandOnPortalMethod.Invoke(
                    controller,
                    new object[] { planet, endOfLevelPortal, null });

                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Native practice finish error: " + ex.Message);
                return false;
            }
        }

        public static bool RestartCustomLevelBuiltIn(bool remakeFloors)
        {
            object controller = Controller();
            if (controller == null) return false;

            Resolve();

            try
            {
                if (resetCustomLevelMethod == null || startCoroutineMethod == null)
                    return false;

                // OnLandOnPortal is guarded by "if (winTime != 0) return;".
                // Awake_Rewind normally clears this latch, but editor
                // ResetCustomLevel() does not call Awake_Rewind again.
                // Re-arm the native win path before EVERY attempt.
                if (winTimeMember != null)
                    ReflectionUtil.WriteMember(controller, winTimeMember, 0f);

                if (transitioningLevelMember != null)
                    ReflectionUtil.WriteMember(controller, transitioningLevelMember, false);

                object routine = resetCustomLevelMethod.Invoke(
                    controller,
                    new object[] { remakeFloors });

                IEnumerator enumerator = routine as IEnumerator;
                if (enumerator == null)
                    return false;

                startCoroutineMethod.Invoke(
                    controller,
                    new object[] { enumerator });

                // OnLandOnPortal disables player responsiveness on a clear.
                // scrController.ResetCustomLevel only turns it back on inside
                // the scnGame branch. Level-editor practice skips that branch,
                // so without this the next attempt starts but cannot receive input.
                SetPlayersResponsive(true);
                UnlockPlayerInput();

                return true;
            }
            catch (Exception ex)
            {
                Main.Log("Built-in ResetCustomLevel error: " + ex);
                return false;
            }
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            controllerType = ReflectionUtil.FindType("scrController");
            if (controllerType == null) return;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            instanceMember = ReflectionUtil.FindMember(controllerType, "instance", all);
            seqMember = ReflectionUtil.FindMember(controllerType, "currentSeqID", all);
            deathsMember = ReflectionUtil.FindMember(controllerType, "deaths", all);
            checkpointsUsedMember = ReflectionUtil.FindMember(controllerType, "checkpointsUsed", all);
            transitioningLevelMember = ReflectionUtil.FindMember(controllerType, "transitioningLevel", all);
            winTimeMember = ReflectionUtil.FindMember(controllerType, "winTime", all);
            chosenPlanetMember = ReflectionUtil.FindMember(controllerType, "chosenPlanet", all);

            portalType = ReflectionUtil.FindType("Portal");
            if (portalType != null && portalType.IsEnum)
            {
                try { endOfLevelPortal = Enum.Parse(portalType, "EndOfLevel", true); }
                catch { endOfLevelPortal = null; }
            }

            adoBaseType = ReflectionUtil.FindType("ADOBase");
            if (adoBaseType != null)
            {
                playerManagerMember = ReflectionUtil.FindMember(adoBaseType, "playerManager", all);

                Type playerManagerType = ReflectionUtil.FindType("scrPlayerManager");
                if (playerManagerType != null)
                {
                    anyValidInputMethod = playerManagerType.GetMethods(all)
                        .FirstOrDefault(m => m.Name == "AnyValidInputWasTriggered" &&
                                             m.GetParameters().Length == 0);

                    setResponsiveMethod = playerManagerType.GetMethods(all)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "SetAllPlayerResponsive") return false;
                            ParameterInfo[] p = m.GetParameters();
                            return p.Length == 1 && p[0].ParameterType == typeof(bool);
                        });

                    unlockInputMethod = playerManagerType.GetMethods(all)
                        .FirstOrDefault(m => m.Name == "UnlockAllPlayerInput" &&
                                             m.GetParameters().Length == 0);
                }
            }

            onLandOnPortalMethod = controllerType.GetMethods(all)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "OnLandOnPortal") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 3;
                });

            resetCustomLevelMethod = controllerType.GetMethods(all)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "ResetCustomLevel") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(bool);
                });

            startCoroutineMethod = controllerType.GetMethods(all)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "StartCoroutine") return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 &&
                           typeof(IEnumerator).IsAssignableFrom(p[0].ParameterType);
                });

            stateMember =
                ReflectionUtil.FindMember(controllerType, "state", all) ??
                ReflectionUtil.FindMember(controllerType, "currentState", all);
        }

        private static bool IsStatic(MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.IsStatic;

            PropertyInfo property = member as PropertyInfo;
            if (property != null)
            {
                MethodInfo getter = property.GetGetMethod(true);
                return getter != null && getter.IsStatic;
            }

            return false;
        }
    }

    internal static class SuccessFreeze
    {
        private static bool frozen;

        private static Type cameraType;
        private static MemberInfo cameraInstanceMember;
        private static MemberInfo cameraEnabledMember;

        private static Type vfxType;
        private static MemberInfo vfxInstanceMember;
        private static MemberInfo vfxEnabledMember;

        private static bool previousCameraEnabled = true;
        private static bool previousVfxEnabled = true;
        private static bool resolved;

        public static void Freeze()
        {
            if (frozen) return;
            Resolve();

            // Only freeze post-range camera/VFX processing.
            // Do NOT touch Time.timeScale, audio, or player/planet simulation:
            // the planets and normal audio should keep running on the success screen.
            previousCameraEnabled = ReadEnabled(
                cameraInstanceMember,
                cameraEnabledMember,
                true);

            previousVfxEnabled = ReadEnabled(
                vfxInstanceMember,
                vfxEnabledMember,
                true);

            WriteEnabled(cameraInstanceMember, cameraEnabledMember, false);
            WriteEnabled(vfxInstanceMember, vfxEnabledMember, false);

            frozen = true;
        }

        public static void Unfreeze()
        {
            if (!frozen) return;
            Resolve();

            // Restore camera/VFX immediately before the next attempt is rebuilt.
            WriteEnabled(
                cameraInstanceMember,
                cameraEnabledMember,
                previousCameraEnabled);

            WriteEnabled(
                vfxInstanceMember,
                vfxEnabledMember,
                previousVfxEnabled);

            GameBridge.SetPlayersResponsive(true);
            GameBridge.UnlockPlayerInput();

            frozen = false;
        }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;

            const BindingFlags all =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance;

            cameraType = ReflectionUtil.FindType("scrCamera");
            if (cameraType != null)
            {
                cameraInstanceMember =
                    ReflectionUtil.FindMember(cameraType, "instance", all);

                cameraEnabledMember =
                    ReflectionUtil.FindMember(cameraType, "enabled", all);
            }

            vfxType = ReflectionUtil.FindType("scrVfxPlus");
            if (vfxType != null)
            {
                vfxInstanceMember =
                    ReflectionUtil.FindMember(vfxType, "instance", all);

                vfxEnabledMember =
                    ReflectionUtil.FindMember(vfxType, "enabled", all);
            }
        }

        private static bool ReadEnabled(
            MemberInfo instanceMember,
            MemberInfo enabledMember,
            bool fallback)
        {
            try
            {
                if (instanceMember == null || enabledMember == null)
                    return fallback;

                object instance =
                    ReflectionUtil.ReadMember(null, instanceMember);

                if (instance == null)
                    return fallback;

                object raw =
                    ReflectionUtil.ReadMember(instance, enabledMember);

                return raw is bool ? (bool)raw : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static void WriteEnabled(
            MemberInfo instanceMember,
            MemberInfo enabledMember,
            bool value)
        {
            try
            {
                if (instanceMember == null || enabledMember == null)
                    return;

                object instance =
                    ReflectionUtil.ReadMember(null, instanceMember);

                if (instance == null)
                    return;

                ReflectionUtil.WriteMember(instance, enabledMember, value);
            }
            catch { }
        }
    }

    internal static class RuntimeOverlay
    {
        private static object rootObject;
        private static object panelObject;
        private static object textComponent;
        private static MethodInfo setActiveMethod;
        private static PropertyInfo textProperty;
        private static bool creationFailed;

        public static void Show(string text)
        {
            if (!EnsureCreated()) return;

            try
            {
                textProperty.SetValue(textComponent, text, null);
                setActiveMethod.Invoke(rootObject, new object[] { true });
            }
            catch (Exception ex)
            {
                Main.Log("Overlay update error: " + ex.Message);
                ResetReferences();
            }
        }

        public static void Hide()
        {
            if (rootObject == null || setActiveMethod == null) return;

            try { setActiveMethod.Invoke(rootObject, new object[] { false }); }
            catch { ResetReferences(); }
        }

        private static bool EnsureCreated()
        {
            if (rootObject != null && panelObject != null &&
                textComponent != null && setActiveMethod != null &&
                textProperty != null)
                return true;

            if (creationFailed) return false;

            try
            {
                Type gameObjectType = Type.GetType(
                    "UnityEngine.GameObject, UnityEngine.CoreModule", false);

                Type rectTransformType = Type.GetType(
                    "UnityEngine.RectTransform, UnityEngine.CoreModule", false);

                Type transformType = Type.GetType(
                    "UnityEngine.Transform, UnityEngine.CoreModule", false);

                Type vector2Type = Type.GetType(
                    "UnityEngine.Vector2, UnityEngine.CoreModule", false);

                Type colorType = Type.GetType(
                    "UnityEngine.Color, UnityEngine.CoreModule", false);

                Type canvasType = Type.GetType(
                    "UnityEngine.Canvas, UnityEngine.UIModule", false);

                Type textType = Type.GetType(
                    "UnityEngine.UI.Text, UnityEngine.UI", false);

                Type imageType = Type.GetType(
                    "UnityEngine.UI.Image, UnityEngine.UI", false);

                Type fontType = Type.GetType(
                    "UnityEngine.Font, UnityEngine.TextRenderingModule", false);

                if (gameObjectType == null || rectTransformType == null ||
                    transformType == null || vector2Type == null ||
                    colorType == null || canvasType == null ||
                    textType == null || imageType == null)
                {
                    creationFailed = true;
                    return false;
                }

                ConstructorInfo goCtor = gameObjectType.GetConstructor(
                    new[] { typeof(string), typeof(Type[]) });

                if (goCtor == null)
                {
                    creationFailed = true;
                    return false;
                }

                rootObject = goCtor.Invoke(new object[]
                {
                    "PracticeStatsOverlay",
                    new Type[] { rectTransformType, canvasType }
                });

                setActiveMethod = gameObjectType.GetMethod(
                    "SetActive",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(bool) },
                    null);

                MethodInfo getComponent = gameObjectType.GetMethod(
                    "GetComponent",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(Type) },
                    null);

                PropertyInfo transformProperty = gameObjectType.GetProperty(
                    "transform",
                    BindingFlags.Public | BindingFlags.Instance);

                if (setActiveMethod == null ||
                    getComponent == null ||
                    transformProperty == null)
                    throw new MissingMethodException(
                        "Required GameObject API was not found.");

                object canvas = getComponent.Invoke(
                    rootObject,
                    new object[] { canvasType });

                PropertyInfo renderMode = canvasType.GetProperty(
                    "renderMode",
                    BindingFlags.Public | BindingFlags.Instance);

                if (renderMode != null)
                {
                    object mode = Enum.Parse(
                        renderMode.PropertyType,
                        "ScreenSpaceOverlay",
                        true);

                    renderMode.SetValue(canvas, mode, null);
                }

                PropertyInfo sortingOrder = canvasType.GetProperty(
                    "sortingOrder",
                    BindingFlags.Public | BindingFlags.Instance);

                if (sortingOrder != null)
                    sortingOrder.SetValue(canvas, 5000, null);

                MethodInfo setParent = transformType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "SetParent") return false;
                        ParameterInfo[] p = m.GetParameters();

                        return p.Length == 2 &&
                               p[0].ParameterType == transformType &&
                               p[1].ParameterType == typeof(bool);
                    });

                if (setParent == null)
                    throw new MissingMethodException(
                        "Transform.SetParent was not found.");

                object rootTransform =
                    transformProperty.GetValue(rootObject, null);

                // Opaque black panel behind the white overlay text.
                panelObject = goCtor.Invoke(new object[]
                {
                    "PracticeStatsPanel",
                    new Type[] { rectTransformType, imageType }
                });

                object panelTransform =
                    transformProperty.GetValue(panelObject, null);

                setParent.Invoke(
                    panelTransform,
                    new object[] { rootTransform, false });

                object panelRect = getComponent.Invoke(
                    panelObject,
                    new object[] { rectTransformType });

                object panelImage = getComponent.Invoke(
                    panelObject,
                    new object[] { imageType });

                if (panelRect == null || panelImage == null)
                    throw new InvalidOperationException(
                        "Overlay panel components could not be created.");

                object textObject = goCtor.Invoke(new object[]
                {
                    "PracticeStatsText",
                    new Type[] { rectTransformType, textType }
                });

                object textTransform =
                    transformProperty.GetValue(textObject, null);

                setParent.Invoke(
                    textTransform,
                    new object[] { panelTransform, false });

                object textRect = getComponent.Invoke(
                    textObject,
                    new object[] { rectTransformType });

                textComponent = getComponent.Invoke(
                    textObject,
                    new object[] { textType });

                if (textRect == null || textComponent == null)
                    throw new InvalidOperationException(
                        "Overlay text components could not be created.");

                ConstructorInfo v2Ctor = vector2Type.GetConstructor(
                    new[] { typeof(float), typeof(float) });

                ConstructorInfo colorCtor = colorType.GetConstructor(
                    new[]
                    {
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(float)
                    });

                if (v2Ctor == null || colorCtor == null)
                    throw new MissingMethodException(
                        "Vector2/Color constructor was not found.");

                object topRight =
                    v2Ctor.Invoke(new object[] { 1f, 1f });

                // Keep the overlay at the same general position as v0.4.7,
                // but give the text a little breathing room inside the panel.
                object panelPosition =
                    v2Ctor.Invoke(new object[] { -18f, -117f });

                object panelSize =
                    v2Ctor.Invoke(new object[] { 370f, 224f });

                object textPosition =
                    v2Ctor.Invoke(new object[] { -14f, -12f });

                object textSize =
                    v2Ctor.Invoke(new object[] { 342f, 198f });

                SetProperty(panelRect, "anchorMin", topRight);
                SetProperty(panelRect, "anchorMax", topRight);
                SetProperty(panelRect, "pivot", topRight);
                SetProperty(panelRect, "anchoredPosition", panelPosition);
                SetProperty(panelRect, "sizeDelta", panelSize);

                SetProperty(textRect, "anchorMin", topRight);
                SetProperty(textRect, "anchorMax", topRight);
                SetProperty(textRect, "pivot", topRight);
                SetProperty(textRect, "anchoredPosition", textPosition);
                SetProperty(textRect, "sizeDelta", textSize);

                // Semi-transparent charcoal gray: readable over bright effects without looking like a solid black block.
                object black = colorCtor.Invoke(
                    new object[] { 0.10f, 0.10f, 0.10f, 0.78f });

                SetProperty(panelImage, "color", black);
                SetProperty(panelImage, "raycastTarget", false);

                textProperty = textType.GetProperty(
                    "text",
                    BindingFlags.Public | BindingFlags.Instance);

                SetProperty(textComponent, "fontSize", 22);
                SetProperty(textComponent, "raycastTarget", false);
                SetEnumProperty(textComponent, "alignment", "UpperRight");
                SetEnumProperty(textComponent, "horizontalOverflow", "Overflow");
                SetEnumProperty(textComponent, "verticalOverflow", "Overflow");

                if (fontType != null)
                {
                    MethodInfo createFont =
                        fontType.GetMethods(
                            BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m =>
                        {
                            if (m.Name != "CreateDynamicFontFromOSFont")
                                return false;

                            ParameterInfo[] p = m.GetParameters();

                            return p.Length == 2 &&
                                   p[0].ParameterType == typeof(string) &&
                                   p[1].ParameterType == typeof(int);
                        });

                    if (createFont != null)
                    {
                        object font = createFont.Invoke(
                            null,
                            new object[] { "Arial", 22 });

                        if (font != null)
                            SetProperty(textComponent, "font", font);
                    }
                }

                setActiveMethod.Invoke(
                    rootObject,
                    new object[] { false });

                return textProperty != null;
            }
            catch (Exception ex)
            {
                Main.Log("Overlay creation error: " + ex);
                creationFailed = true;
                ResetReferences();
                return false;
            }
        }

        private static void SetProperty(
            object target,
            string name,
            object value)
        {
            if (target == null) return;

            PropertyInfo property =
                target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);

            if (property != null && property.CanWrite)
                property.SetValue(target, value, null);
        }

        private static void SetEnumProperty(
            object target,
            string name,
            string enumName)
        {
            if (target == null) return;

            PropertyInfo property =
                target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);

            if (property == null ||
                !property.CanWrite ||
                !property.PropertyType.IsEnum)
                return;

            object value =
                Enum.Parse(property.PropertyType, enumName, true);

            property.SetValue(target, value, null);
        }

        private static void ResetReferences()
        {
            rootObject = null;
            panelObject = null;
            textComponent = null;
            setActiveMethod = null;
            textProperty = null;
        }
    }

    internal static class UnityBridge
    {
        private static Type inputType;
        private static Type keyCodeType;
        private static MethodInfo getKeyDown;
        private static PropertyInfo anyKeyDown;
        private static bool resolved;

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
            if (resolved) return;
            resolved = true;

            inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule", false);
            keyCodeType = Type.GetType("UnityEngine.KeyCode, UnityEngine.CoreModule", false);

            if (inputType == null) return;

            anyKeyDown = inputType.GetProperty(
                "anyKeyDown", BindingFlags.Public | BindingFlags.Static);

            if (keyCodeType != null)
            {
                getKeyDown = inputType.GetMethod(
                    "GetKeyDown",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { keyCodeType },
                    null);
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
            if (label != null)
                label.Invoke(null, new object[] { text, noOptions });
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
            if (space != null)
                space.Invoke(null, new object[] { pixels });
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

            label = methods.FirstOrDefault(m =>
                Match(m, "Label", typeof(string), optionType.MakeArrayType()));

            textField = methods.FirstOrDefault(m =>
                Match(m, "TextField", typeof(string), optionType.MakeArrayType()) &&
                m.ReturnType == typeof(string));

            button = methods.FirstOrDefault(m =>
                Match(m, "Button", typeof(string), optionType.MakeArrayType()) &&
                m.ReturnType == typeof(bool));

            space = methods.FirstOrDefault(m =>
                Match(m, "Space", typeof(float)));
        }

        private static bool Match(MethodInfo method, string name, params Type[] types)
        {
            if (method.Name != name) return false;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != types.Length) return false;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != types[i])
                    return false;
            }

            return true;
        }
    }

    internal static class ReflectionUtil
    {
        public static Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(name, false);
                    if (type != null) return type;
                }
                catch { }
            }

            return null;
        }

        public static MemberInfo FindMember(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;

            Type current = type;
            while (current != null)
            {
                FieldInfo field = current.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (field != null) return field;

                PropertyInfo property = current.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (property != null) return property;

                current = current.BaseType;
            }

            return null;
        }

        public static object ReadMember(object instance, MemberInfo member)
        {
            if (member == null) return null;

            FieldInfo field = member as FieldInfo;
            if (field != null)
                return field.GetValue(instance);

            PropertyInfo property = member as PropertyInfo;
            if (property != null)
                return property.GetValue(instance, null);

            return null;
        }

        public static void WriteMember(object instance, MemberInfo member, object value)
        {
            if (member == null) return;

            FieldInfo field = member as FieldInfo;
            if (field != null)
            {
                object converted = ConvertFor(value, field.FieldType);
                field.SetValue(instance, converted);
                return;
            }

            PropertyInfo property = member as PropertyInfo;
            if (property != null && property.CanWrite)
            {
                object converted = ConvertFor(value, property.PropertyType);
                property.SetValue(instance, converted, null);
            }
        }

        private static object ConvertFor(object value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;
            if (target.IsEnum) return Enum.ToObject(target, value);
            return Convert.ChangeType(value, target);
        }
    }
}
