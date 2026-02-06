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

    [Header("B2 Gate")]
    public bool waitForB2Ready = true;
    public bool b2Ready = true;

    [Header("State (read-only)")]
    public Phase phase = Phase.None;

    private void OnEnable()
    {
        ResolveSpeakerAByDebaterGender();
        if (speakerA != null)
            speakerA.OnSpeechFinished.AddListener(OnAFinished);
        if (speakerB != null)
            speakerB.OnSpeechFinished.AddListener(OnBFinished);
        if (micB != null)
            micB.OnMicFinished.AddListener(OnMicFinished);
    }

    private void OnDisable()
    {
        if (speakerA != null)
            speakerA.OnSpeechFinished.RemoveListener(OnAFinished);
        if (speakerB != null)
            speakerB.OnSpeechFinished.RemoveListener(OnBFinished);
        if (micB != null)
            micB.OnMicFinished.RemoveListener(OnMicFinished);
    }

    private void Start()
    {
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
        if (!autoSelectSpeakerA) return;

        string gender = null;
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.debaterGender != Gender.None)
        {
            gender = gfm.debaterGender.ToString();
        }
        else
        {
            var trials = FindObjectOfType<TrialsController>();
            if (trials != null && !string.IsNullOrWhiteSpace(trials.debaterGender))
                gender = trials.debaterGender;
        }

        if (string.IsNullOrWhiteSpace(gender)) return;

        bool isFemale = string.Equals(gender, Gender.Female.ToString(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(gender, "female", StringComparison.OrdinalIgnoreCase);
        bool isMale = string.Equals(gender, Gender.Male.ToString(), StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(gender, "male", StringComparison.OrdinalIgnoreCase);

        string targetName = isFemale ? femaleDebaterObjectName : isMale ? maleDebaterObjectName : null;
        if (string.IsNullOrEmpty(targetName)) return;

        var go = GameObject.Find(targetName);
        if (go == null)
        {
            Debug.LogWarning($"Sequencer: debater '{targetName}' not found in scene.");
            return;
        }

        var controller = go.GetComponent<AgentSpeechController>();
        if (controller == null)
        {
            Debug.LogWarning($"Sequencer: AgentSpeechController missing on '{targetName}'.");
            return;
        }

        speakerA = controller;
    }

    public IEnumerator BeginSequence()
    {
        // Safety checks
        if (speakerA == null || speakerB == null || micB == null)
        {
            Debug.LogError("Sequencer: please assign speakerA, speakerB, and micB.");
            yield break;
        }
        // Ensure controllers don't auto-play by themselves
        speakerA.playOnStart = false;
        speakerB.playOnStart = false;

        if (startDelay > 0f) yield return new WaitForSeconds(startDelay);

        // Kick off A1
        phase = Phase.A1;
        if (!string.IsNullOrEmpty(lineA1))
            speakerA.PlayLine(lineA1);
        else
            Debug.LogWarning("Sequencer: lineA1 is empty.");
    }

    private void OnAFinished()
    {
        if (phase == Phase.A1)
        {
            // A1 finished → gap → B1 mic begin
            StartCoroutine(GapThen(() =>
            {
                phase = Phase.B1;
                micB.BeginMic();
            }));
        }
        else if (phase == Phase.A2)
        {
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

    private void OnBFinished()
    {
        if (phase == Phase.B2)
        {
            phase = Phase.Done;
            Debug.Log("Conversation (events) A1 → B1(mic) → A2 → B2 finished.");
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
        bool xrActive = XRSettings.isDeviceActive;
        if (xrActive)
        {
            // Headset on: right-hand index trigger ends mic
            return OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger);
        }

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            return true;
#else
        if (Input.GetKeyDown(KeyCode.Space))
            return true;
#endif
        return false;
    }
}
