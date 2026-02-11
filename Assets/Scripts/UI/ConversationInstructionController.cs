using UnityEngine;

public class ConversationInstructionController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private ConversationSequencerFourTurns sequencer;

    [Header("Instruction Panels")]
    [SerializeField] private GameObject b1Answer;
    [SerializeField] private GameObject b2Listen;

    [Header("Behavior")]
    [SerializeField] private bool hideOnStart = true;
    [SerializeField] private bool enableLogs = true;

    private ConversationSequencerFourTurns.Phase lastPhase = ConversationSequencerFourTurns.Phase.None;
    private bool lastASpeaking;
    private bool lastBSpeaking;

    private void Awake()
    {
        if (hideOnStart)
        {
            SetB1Answer(false);
            SetB2Listen(false);
        }

        LogState("Awake");
    }

    private void OnEnable()
    {
        LogState("OnEnable (before hook)");
        HookEvents(true);
        LogState("OnEnable (after hook)");
    }

    private void OnDisable()
    {
        LogState("OnDisable (before unhook)");
        HookEvents(false);
        LogState("OnDisable (after unhook)");
    }

    private void Update()
    {
        if (sequencer == null)
        {
            LogOnceMissingSequencer();
            return;
        }

        var currentPhase = sequencer.phase;
        if (currentPhase != lastPhase)
        {
            Log("Phase changed: " + lastPhase + " -> " + currentPhase);

            if (currentPhase == ConversationSequencerFourTurns.Phase.B1)
            {
                SetB1Answer(true);
            }
            else if (currentPhase == ConversationSequencerFourTurns.Phase.A2)
            {
                SetB1Answer(false);
            }
            else if (currentPhase == ConversationSequencerFourTurns.Phase.Done)
            {
                SetB1Answer(false);
                SetB2Listen(false);
            }

            lastPhase = currentPhase;
        }

        var speakerA = sequencer.speakerA;
        var speakerB = sequencer.speakerB;
        bool aSpeaking = speakerA != null && speakerA.IsSpeaking;
        bool bSpeaking = speakerB != null && speakerB.IsSpeaking;

        if (currentPhase == ConversationSequencerFourTurns.Phase.A2 && lastASpeaking && !aSpeaking)
        {
            // A2 finished -> show B2 listen immediately (even if B2 not ready yet)
            SetB2Listen(true);
            Log("Detected A2 end by IsSpeaking transition.");
        }

        if (currentPhase == ConversationSequencerFourTurns.Phase.B2 && lastBSpeaking && !bSpeaking)
        {
            // B2 finished -> hide B2 listen
            SetB2Listen(false);
            Log("Detected B2 end by IsSpeaking transition.");
        }

        lastASpeaking = aSpeaking;
        lastBSpeaking = bSpeaking;

        if (sequencer.phase == ConversationSequencerFourTurns.Phase.Done)
        {
            SetB1Answer(false);
            SetB2Listen(false);
        }
    }

    private void HookEvents(bool hook)
    {
        if (sequencer == null)
        {
            Log("HookEvents skipped: sequencer is null.");
            return;
        }

        var speakerA = sequencer.speakerA;
        var speakerB = sequencer.speakerB;
        var micB = sequencer.micB;

        if (hook)
        {
            Log("HookEvents: subscribe to speakerA/speakerB/micB.");
            if (speakerA != null) speakerA.OnSpeechFinished.AddListener(OnASpeechFinished);
            if (speakerB != null) speakerB.OnSpeechFinished.AddListener(OnBSpeechFinished);
            if (micB != null) micB.OnMicFinished.AddListener(OnMicFinished);
        }
        else
        {
            Log("HookEvents: unsubscribe from speakerA/speakerB/micB.");
            if (speakerA != null) speakerA.OnSpeechFinished.RemoveListener(OnASpeechFinished);
            if (speakerB != null) speakerB.OnSpeechFinished.RemoveListener(OnBSpeechFinished);
            if (micB != null) micB.OnMicFinished.RemoveListener(OnMicFinished);
        }
    }

    private void OnASpeechFinished()
    {
        if (sequencer == null)
        {
            return;
        }

        Log("OnASpeechFinished phase=" + sequencer.phase);
        if (sequencer.phase == ConversationSequencerFourTurns.Phase.A1)
        {
            // A1 end -> show B1 answer instructions
            SetB1Answer(true);
        }
        else if (sequencer.phase == ConversationSequencerFourTurns.Phase.A2)
        {
            // A2 end -> show B2 listen instructions
            SetB2Listen(true);
        }
    }

    private void OnMicFinished()
    {
        if (sequencer == null)
        {
            return;
        }

        Log("OnMicFinished phase=" + sequencer.phase);
        if (sequencer.phase == ConversationSequencerFourTurns.Phase.B1)
        {
            // B1 end -> hide B1 answer instructions
            SetB1Answer(false);
        }
    }

    private void OnBSpeechFinished()
    {
        if (sequencer == null)
        {
            return;
        }

        Log("OnBSpeechFinished phase=" + sequencer.phase);
        if (sequencer.phase == ConversationSequencerFourTurns.Phase.B2 ||
            sequencer.phase == ConversationSequencerFourTurns.Phase.Done)
        {
            // B2 end -> hide B2 listen instructions
            SetB2Listen(false);
        }
    }

    private void SetB1Answer(bool show)
    {
        if (b1Answer != null)
        {
            b1Answer.SetActive(show);
            Log("SetB1Answer " + show);
        }
        else
        {
            Log("SetB1Answer skipped: b1Answer is null.");
        }
    }

    private void SetB2Listen(bool show)
    {
        if (b2Listen != null)
        {
            b2Listen.SetActive(show);
            Log("SetB2Listen " + show);
        }
        else
        {
            Log("SetB2Listen skipped: b2Listen is null.");
        }
    }

    private bool loggedMissingSequencer;
    private void LogOnceMissingSequencer()
    {
        if (loggedMissingSequencer)
        {
            return;
        }
        loggedMissingSequencer = true;
        Log("Update skipped: sequencer is null.");
    }

    private void LogState(string where)
    {
        if (!enableLogs)
        {
            return;
        }

        string seq = sequencer != null ? sequencer.name : "null";
        string phase = sequencer != null ? sequencer.phase.ToString() : "n/a";
        string a = sequencer != null && sequencer.speakerA != null ? sequencer.speakerA.name : "null";
        string b = sequencer != null && sequencer.speakerB != null ? sequencer.speakerB.name : "null";
        string mic = sequencer != null && sequencer.micB != null ? sequencer.micB.name : "null";
        string p1 = b1Answer != null ? b1Answer.name : "null";
        string p2 = b2Listen != null ? b2Listen.name : "null";
        Debug.Log($"[ConversationInstructionController] {where} seq={seq} phase={phase} speakerA={a} speakerB={b} micB={mic} b1={p1} b2={p2}", this);
    }

    private void Log(string msg)
    {
        if (!enableLogs)
        {
            return;
        }

        Debug.Log("[ConversationInstructionController] " + msg, this);
    }
}
