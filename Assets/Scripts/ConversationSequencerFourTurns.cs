using System;
using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.XR;

public class ConversationSequencerFourTurns : MonoBehaviour
{
    public enum Phase { None, A1, B1, A2, B2, Done }

    [Header("Refs")]
    public AgentSpeechController speakerA;  // Avatar A (plays A1, A2)
    public AgentSpeechController speakerB;  // Avatar B (plays B2)
    public MicSpeechController micB;        // Avatar B mic for B1

    [Header("SpeakerA Auto-Select")]
    public bool autoSelectSpeakerA = true;
    public string maleDebaterObjectName = "male_debater";
    public string femaleDebaterObjectName = "female_debater";

    [Header("Lines (StreamingAssets-relative paths)")]
    public string lineA1 = "Audio/dialog/npc_1A_tts.wav";
    public string lineA2 = "Audio/dialog/npc_2A_tts.wav";
    public string lineB2 = "Audio/dialog/user_2B_tts.wav";

    [Header("Timing")]
    public float startDelay = 0f;
    public float gapBetweenTurns = 0.25f;

    [Header("Control")]
    public bool autoStart = false;

    [Header("Input")]
    [Tooltip("Trigger value threshold for ending mic (0-1).")]
    [Range(0.1f, 1f)]
    public float triggerEndThreshold = 0.75f;

    [Header("B2 Gate")]
    public bool waitForB2Ready = true;
    public bool b2Ready = true;

    [Header("State (read-only)")]
    public Phase phase = Phase.None;

    private void OnEnable()
    {
        TraceSequencerStep("sequencer_on_enable", $"speakerA={DescribeObject(speakerA)}; speakerB={DescribeObject(speakerB)}; micB={DescribeObject(micB)}; xr={GetXrStatus()}");
        ResolveSpeakerAByDebaterGender();
        if (speakerA != null)
            speakerA.OnSpeechFinished.AddListener(OnAFinished);
        if (speakerB != null)
            speakerB.OnSpeechFinished.AddListener(OnBFinished);
        if (micB != null)
        {
            micB.OnMicFinished.AddListener(OnMicFinished);
            micB.OnMicStartFailed.AddListener(OnMicStartFailed);
        }
    }

    private void OnDisable()
    {
        if (speakerA != null)
            speakerA.OnSpeechFinished.RemoveListener(OnAFinished);
        if (speakerB != null)
            speakerB.OnSpeechFinished.RemoveListener(OnBFinished);
        if (micB != null)
        {
            micB.OnMicFinished.RemoveListener(OnMicFinished);
            micB.OnMicStartFailed.RemoveListener(OnMicStartFailed);
        }
    }

    private void Start()
    {
        TraceSequencerStep("sequencer_start", $"autoStart={autoStart}; phase={phase}; xr={GetXrStatus()}");
        if (autoStart)
            StartCoroutine(BeginSequence());
    }

    private void Update()
    {
        if (phase != Phase.B1 || micB == null || !micB.IsMicActive)
            return;

        if (ShouldEndMicByInput())
        {
            micB.EndMic();
        }
    }

    private void ResolveSpeakerAByDebaterGender()
    {
        TraceSequencerStep("debater_resolve_begin", $"autoSelect={autoSelectSpeakerA}; currentSpeakerA={DescribeObject(speakerA)}");
        if (!autoSelectSpeakerA)
        {
            TraceSequencerStep("debater_resolve_skipped", "autoSelectSpeakerA=false");
            return;
        }

        string gender = null;
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.debaterGender != Gender.None)
        {
            gender = gfm.debaterGender.ToString();
            TraceSequencerStep("debater_gender_from_game_flow", $"gender={gender}");
        }
        else
        {
            var trials = FindObjectOfType<TrialsController>();
            if (trials != null && !string.IsNullOrWhiteSpace(trials.debaterGender))
            {
                gender = trials.debaterGender;
                TraceSequencerStep("debater_gender_from_trials", $"gender={gender}");
            }
        }

        if (string.IsNullOrWhiteSpace(gender))
        {
            TraceSequencerStep("debater_resolve_skipped", "gender is empty");
            return;
        }

        bool isFemale = string.Equals(gender, Gender.Female.ToString(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase);
        bool isMale = string.Equals(gender, Gender.Male.ToString(), StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(gender, "male", StringComparison.OrdinalIgnoreCase);

        string targetName = isFemale ? femaleDebaterObjectName : isMale ? maleDebaterObjectName : null;
        if (string.IsNullOrEmpty(targetName))
        {
            TraceSequencerStep("debater_resolve_skipped", $"unsupported_gender={gender}");
            return;
        }

        var go = FindSceneObjectByName(targetName);
        if (go == null)
        {
            Debug.LogWarning($"Sequencer: debater '{targetName}' not found in scene.");
            TraceSequencerStep("debater_resolve_failed", $"target={targetName}; reason=object_not_found");
            return;
        }

        var controller = go.GetComponent<AgentSpeechController>();
        if (controller == null)
        {
            Debug.LogWarning($"Sequencer: AgentSpeechController missing on '{targetName}'.");
            TraceSequencerStep("debater_resolve_failed", $"target={targetName}; object={DescribeObject(go)}; reason=AgentSpeechController_missing");
            return;
        }

        speakerA = controller;
        TraceSequencerStep("debater_resolve_done", $"target={targetName}; object={DescribeObject(go)}; speakerA={DescribeObject(speakerA)}");
    }

    private static GameObject FindSceneObjectByName(string name)
    {
        GameObject activeObject = GameObject.Find(name);
        if (activeObject != null) return activeObject;

        foreach (GameObject obj in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (obj.name != name) continue;
            if (!obj.scene.IsValid() || !obj.scene.isLoaded) continue;
            return obj;
        }

        return null;
    }

    public IEnumerator BeginSequence()
    {
        TraceSequencerStep("sequence_begin_requested", $"phase={phase}; speakerA={DescribeObject(speakerA)}; speakerB={DescribeObject(speakerB)}; micB={DescribeObject(micB)}");
        ResolveSpeakerAByDebaterGender();

        // Safety checks
        if (speakerA == null || speakerB == null || micB == null)
        {
            Debug.LogError("Sequencer: please assign speakerA, speakerB, and micB.");
            TraceSequencerStep("sequence_begin_failed", $"speakerA={DescribeObject(speakerA)}; speakerB={DescribeObject(speakerB)}; micB={DescribeObject(micB)}");
            yield break;
        }
        // Ensure controllers don't auto-play by themselves
        speakerA.playOnStart = false;
        speakerB.playOnStart = false;

        if (startDelay > 0f) yield return new WaitForSeconds(startDelay);

        // Kick off A1
        phase = Phase.A1;
        if (!string.IsNullOrEmpty(lineA1))
        {
            TraceSequencerStep("sequence_phase_a1_start", $"speakerA={DescribeObject(speakerA)}; lineA1={lineA1}");
            speakerA.PlayLine(lineA1);
        }
        else
        {
            Debug.LogWarning("Sequencer: lineA1 is empty.");
            TraceSequencerStep("sequence_phase_a1_missing_line", "");
        }
    }

    private void OnAFinished()
    {
        if (phase == Phase.A1)
        {
            TraceSequencerStep("sequence_phase_a1_finished", $"next=B1; micB={DescribeObject(micB)}");
            // A1 finished → gap → B1 mic begin
            StartCoroutine(GapThen(() =>
            {
                phase = Phase.B1;
                TraceSequencerStep("sequence_phase_b1_start", $"micB={DescribeObject(micB)}");
                micB.BeginMic();
            }));
        }
        else if (phase == Phase.A2)
        {
            TraceSequencerStep("sequence_phase_a2_finished", "next=B2");
            // A2 finished → gap → B2
            StartCoroutine(GapThen(() =>
            {
                StartCoroutine(WaitForB2ThenPlay());
            }));
        }
    }

    private void OnMicFinished()
    {
        if (phase == Phase.B1)
        {
            TraceSequencerStep("sequence_phase_b1_finished", $"next=A2; lineA2={lineA2}");
            // B1 finished (mic) → gap → A2
            StartCoroutine(GapThen(() =>
            {
                phase = Phase.A2;
                if (!string.IsNullOrEmpty(lineA2))
                    speakerA.PlayLine(lineA2);
                else
                    Debug.LogWarning("Sequencer: lineA2 is empty.");
            }));
        }
    }

    private void OnMicStartFailed()
    {
        if (phase == Phase.B1)
        {
            Debug.LogError("Sequencer: B1 microphone failed to start; ending sequence to avoid waiting forever.");
            TraceSequencerStep("sequence_phase_b1_mic_failed", "phase set to Done");
            phase = Phase.Done;
        }
    }

    private void OnBFinished()
    {
        if (phase == Phase.B2)
        {
            phase = Phase.Done;
            Debug.Log("Conversation (events) A1 → B1(mic) → A2 → B2 finished.");
            TraceSequencerStep("sequence_done", "");
        }
    }

    private void TraceSequencerStep(string evt, string detail = "")
    {
        string message = $"[SEQUENCE_STARTUP] {evt} detail={detail}";
        Debug.Log(message);
        GameFlowManager.Instance?.LogStartupStep(evt, detail, "sequence");
    }

    private static string DescribeObject(UnityEngine.Object obj)
    {
        if (obj == null) return "<null>";
        return obj.name;
    }

    private static string GetXrStatus()
    {
        try
        {
            string loadedDevice = string.IsNullOrEmpty(XRSettings.loadedDeviceName) ? "<none>" : XRSettings.loadedDeviceName;
            bool rightTouch = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            bool leftTouch = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            bool touch = OVRInput.IsControllerConnected(OVRInput.Controller.Touch);
            return $"deviceActive={XRSettings.isDeviceActive}; loadedDevice={loadedDevice}; rightTouch={rightTouch}; leftTouch={leftTouch}; touch={touch}";
        }
        catch (Exception e)
        {
            return $"xr_status_error={e.Message}";
        }
    }

    private IEnumerator GapThen(System.Action action)
    {
        if (gapBetweenTurns > 0f)
            yield return new WaitForSeconds(gapBetweenTurns);
        action?.Invoke();
    }

    private IEnumerator WaitForB2ThenPlay()
    {
        if (waitForB2Ready)
        {
            while (!b2Ready)
                yield return null;
        }

        phase = Phase.B2;
        if (!string.IsNullOrEmpty(lineB2))
            speakerB.PlayLine(lineB2);
        else
            Debug.LogWarning("Sequencer: lineB2 is empty.");
    }

    private bool ShouldEndMicByInput()
    {
        bool rightConnected = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
        bool leftConnected = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
        bool anyOvrController = rightConnected || leftConnected || OVRInput.IsControllerConnected(OVRInput.Controller.Touch);
        bool xrActive = XRSettings.isDeviceActive || anyOvrController;
        if (xrActive && anyOvrController)
        {
            // Prefer analog trigger threshold with rising-edge detection; fall back to GetDown.
            float rightValue = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);
            float leftValue = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger);

            bool rightEdge = rightValue >= triggerEndThreshold && prevRightTrigger < triggerEndThreshold;
            bool leftEdge = leftValue >= triggerEndThreshold && prevLeftTrigger < triggerEndThreshold;

            prevRightTrigger = rightValue;
            prevLeftTrigger = leftValue;

            if (rightEdge || leftEdge)
                return true;

            if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger) ||
                OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger))
                return true;
        }
        else
        {
            prevRightTrigger = 0f;
            prevLeftTrigger = 0f;
        }

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            return true;
#else
        if (Input.GetKeyDown(KeyCode.Space))
            return true;
#endif
        if (Input.GetMouseButtonDown(0))
            return true;
        return false;
    }

    private float prevRightTrigger = 0f;
    private float prevLeftTrigger = 0f;
}
