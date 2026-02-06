using System;
using System.Collections;
using System.IO;
using UnityEngine;

public class ExperimentController : MonoBehaviour
{
    [Header("References")]
    public ConversationSequencerFourTurns sequencer;   // assign in Inspector (the same rig reused per trial)

    [Header("Config")]
    public int numberOfTrials = 6;
    [Tooltip("Base folder under StreamingAssets/persistentDataPath where audio lives. e.g., 'Audio'")]
    public string audioRoot = "Audio";

    [Header("Session")]
    public string sessionId = "";    // leave empty to input at runtime
    public string participantGender = ""; // optional freeform gender input
    public bool waitForSessionInput = true;

    [Header("Logging")]
    public bool writeCsvLog = true;
    public string logRelativePath = "logs/experiment_log.csv";  // relative under StreamingAssets (Editor) / persistentDataPath (Build)

    private string logFullPath;
    private float t0;
    private bool sessionReady = false;

    void Start()
    {
        t0 = Time.realtimeSinceStartup;
        if (!waitForSessionInput)
        {
            if (string.IsNullOrEmpty(sessionId)) sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            if (string.IsNullOrEmpty(participantGender)) participantGender = "unspecified";
            PrepareAndRun();
        }
    }

    void OnGUI()
    {
        if (!waitForSessionInput || sessionReady) return;
        const int w = 360; const int h = 170;
        var rect = new Rect(20, 20, w, h);
        GUI.Box(rect, "Enter Session Info");
        GUILayout.BeginArea(new Rect(30, 50, w - 20, h - 40));
        GUILayout.Label("Session ID:");
        sessionId = GUILayout.TextField(sessionId, 64);
        GUILayout.Space(10);
        GUILayout.Label("Participant Gender:");
        participantGender = GUILayout.TextField(participantGender, 32);
        if (GUILayout.Button("Start Experiment"))
        {
            if (string.IsNullOrEmpty(sessionId)) sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            if (string.IsNullOrEmpty(participantGender)) participantGender = "unspecified";
            PrepareAndRun();
        }
        GUILayout.EndArea();
    }

    private void PrepareAndRun()
    {
        sessionReady = true;
        SetupLogging();
        StartCoroutine(RunExperiment());
    }

    private void SetupLogging()
    {
        if (!writeCsvLog) return;
#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.persistentDataPath;
#endif
        string logFileName = sessionId + "_log.csv";
        logFullPath = Path.Combine(baseDir, "logs");
        if (!Directory.Exists(logFullPath)) Directory.CreateDirectory(logFullPath);
        logFullPath = Path.Combine(logFullPath, logFileName);
        if (!File.Exists(logFullPath))
        {
            File.WriteAllText(logFullPath, "iso_time,realtime_s,session_id,participant_gender,trial,phase,event,detail\n");
        }
        Log("experiment_start", "");
    }

    private void Log(string evt, string detail, string phase = "")
    {
        if (!writeCsvLog) return;
        string iso = DateTime.UtcNow.ToString("o");
        float rt = Time.realtimeSinceStartup - t0;
        string trialStr = currentTrialIndex > 0 ? currentTrialIndex.ToString() : "";
        string line = $"{iso},{rt:F3},{sessionId},{Escape(participantGender)},{trialStr},{phase},{Escape(evt)},{Escape(detail)}\n";
        File.AppendAllText(logFullPath, line);
#if UNITY_EDITOR
        Debug.Log($"[LOG] {evt} t={rt:F3}s detail={detail}");
#endif
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(",") || s.Contains("\n") || s.Contains("\""))
            return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }

    private int currentTrialIndex = 0;

    private IEnumerator RunExperiment()
    {
        if (sequencer == null)
        {
            Debug.LogError("ExperimentController: sequencer is not assigned.");
            yield break;
        }
        // Subscribe to events for timing logs
        HookSequencerEvents(true);

        sequencer.autoStart = false; // we'll control when each trial starts

        for (int i = 1; i <= numberOfTrials; i++)
        {
            currentTrialIndex = i;
            // Build trial folder like: Audio/<sessionId>/trial_001/
            string trialFolder = $"{audioRoot}/{sessionId}_session/trial_{i:000}/";
            // Assign line paths
            sequencer.lineA1 = trialFolder + "A1.wav";
            sequencer.lineA2 = trialFolder + "A2.wav";
            sequencer.lineB2 = trialFolder + "user_B2_tts.wav";

            // Log trial start
            Log("trial_start", trialFolder, "A1");

            // Kick off A1
            sequencer.phase = ConversationSequencerFourTurns.Phase.None; // reset visible state
            yield return StartCoroutine(sequencer.BeginSequence());

            // Wait until sequencer marks Done
            yield return new WaitUntil(() => sequencer.phase == ConversationSequencerFourTurns.Phase.Done);

            Log("trial_end", trialFolder, "Done");
            yield return null; // tiny yield between trials
        }

        // Unhook events
        HookSequencerEvents(false);
        Log("experiment_end", "");
        Debug.Log("ExperimentController: all trials finished.");
    }

    private void HookSequencerEvents(bool on)
    {
        if (sequencer == null) return;
        var A = sequencer.speakerA;
        var B = sequencer.speakerB;
        var mic = sequencer.micB;
        if (A != null)
        {
            if (on)
            {
                A.OnSpeechStarted.AddListener(OnASpeechStarted);
                A.OnSpeechFinished.AddListener(OnASpeechFinished);
            }
            else
            {
                A.OnSpeechStarted.RemoveListener(OnASpeechStarted);
                A.OnSpeechFinished.RemoveListener(OnASpeechFinished);
            }
        }
        if (B != null)
        {
            if (on)
            {
                B.OnSpeechStarted.AddListener(OnBSpeechStarted);
                B.OnSpeechFinished.AddListener(OnBSpeechFinished);
            }
            else
            {
                B.OnSpeechStarted.RemoveListener(OnBSpeechStarted);
                B.OnSpeechFinished.RemoveListener(OnBSpeechFinished);
            }
        }
        if (mic != null)
        {
            if (on)
            {
                mic.OnMicStarted.AddListener(OnMicStarted);
                mic.OnMicFinished.AddListener(OnMicFinished);
                mic.OnMicPermissionDenied.AddListener(OnMicDenied);
            }
            else
            {
                mic.OnMicStarted.RemoveListener(OnMicStarted);
                mic.OnMicFinished.RemoveListener(OnMicFinished);
                mic.OnMicPermissionDenied.RemoveListener(OnMicDenied);
            }
        }
    }

    // --- Event handlers for logging ---
    private void OnASpeechStarted() => Log("A_speech_started", sequencer.lineA1, "A1");
    private void OnASpeechFinished() => Log("A_speech_finished", sequencer.phase.ToString());

    private void OnBSpeechStarted() => Log("B_speech_started", sequencer.lineB2, "B2");
    private void OnBSpeechFinished() => Log("B_speech_finished", sequencer.phase.ToString());

    private void OnMicStarted() => Log("B_mic_started", "", "B1");
    private void OnMicFinished() => Log("B_mic_finished", "", "B1");
    private void OnMicDenied() => Log("B_mic_permission_denied", "", "B1");
}
