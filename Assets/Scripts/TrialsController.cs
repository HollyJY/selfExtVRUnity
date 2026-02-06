using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class TrialsController : MonoBehaviour
{
    [Header("References")]
    public ConversationSequencerFourTurns sequencer;   // assign in Inspector (the same rig reused per trial)

    [Header("Config")]
    public int numberOfTrials = 6;
    [Tooltip("Base folder under StreamingAssets/persistentDataPath where audio lives. e.g., 'Audio'")]
    public string audioRoot = "Audio";

    [Header("Session")]
    public string sessionId = "";    // leave empty to input at runtime; auto-filled from GameFlowManager if available
    public string participantGender = ""; // optional freeform gender input; auto-filled from GameFlowManager
    public string talkerGender = ""; // auto-filled from GameFlowManager
    public string debaterGender = ""; // auto-filled from GameFlowManager

    [Header("Avatar")]
    public GameObject maleTalkerMirrored;
    public GameObject femaleTalkerMirrored;
    public GameObject maleDebater;
    public GameObject femaleDebater;

    [Header("Scales")]
    public ScaleQuestionnaireController scaleController;
    public GameObject scalesRoot;
    [Tooltip("If true, hide the debater avatar while the scales UI is shown.")]
    public bool hideDebaterDuringScales = true;
    [Tooltip("Show rest hint after this trial completes. Set <= 0 to disable.")]
    public int restAfterTrial = 6;

    [Header("Logging")]
    public bool writeCsvLog = true;
    public string logRelativePath = "logs/experiment_log.csv";  // relative under StreamingAssets (Editor) / persistentDataPath (Build) if GameFlowManager unavailable

    [Header("TTS")]
    [Tooltip("Endpoint for TTS POST JSON")]
    public string ttsEndpoint = "http://192.168.37.177:7003/api/v1/tts";
    [Tooltip("Relative path under StreamingAssets/Audio/<participantId> for exp info CSV (expInfo_<participantId>.csv)")]
    public string expInfoFilePrefix = "expInfo_";
    [Tooltip("Reference audio for male talker (server-side path)")]
    public string refMalePath = "sample_audios/sample_male.wav";
    [Tooltip("Reference audio for female talker (server-side path)")]
    public string refFemalePath = "sample_audios/sample_female.wav";

    [Header("B2 Service")]
    [Tooltip("Endpoint for B2 process (expects multipart form)")]
    public string b2ProcessEndpoint = "http://192.168.37.177:7000/api/v1/process";
    [Tooltip("Relative download prefix on the same host/port as B2 process endpoint")]
    public string b2DownloadPrefix = "api/v1/download";
    public string b2Lang = "en";

    private string logFullPath;
    private float t0;
    private bool sessionReady = false;
    private readonly System.Collections.Generic.Dictionary<int, string> trialContext = new System.Collections.Generic.Dictionary<int, string>();
    private readonly System.Collections.Generic.Dictionary<int, string> trialSceneId = new System.Collections.Generic.Dictionary<int, string>();
    private readonly System.Collections.Generic.Dictionary<int, int> trialCondition = new System.Collections.Generic.Dictionary<int, int>();
    private readonly System.Collections.Generic.Dictionary<int, string> trialVoiceId = new System.Collections.Generic.Dictionary<int, string>();

    void Start()
    {
        t0 = Time.realtimeSinceStartup;
        SyncFromGameFlowManager();
        LoadTrialContext();
        ApplyParticipantAvatar();
        ApplyDebaterAvatar();
        if (scalesRoot != null)
        {
            scalesRoot.SetActive(false);
        }
        HookScaleEvents(true);

        // Auto-start without GUI confirmation
        if (string.IsNullOrEmpty(sessionId)) sessionId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        if (string.IsNullOrEmpty(participantGender)) participantGender = "unspecified";
        StartCoroutine(CheckTtsHealthAndRun());
    }

    private void ApplyParticipantAvatar()
    {
        bool isFemale = string.Equals(participantGender, Gender.Female.ToString(), StringComparison.OrdinalIgnoreCase);
        bool isMale = string.Equals(participantGender, Gender.Male.ToString(), StringComparison.OrdinalIgnoreCase);

        if (isFemale)
        {
            if (femaleTalkerMirrored != null) femaleTalkerMirrored.SetActive(true);
            if (maleTalkerMirrored != null) maleTalkerMirrored.SetActive(false);
        }
        else if (isMale)
        {
            if (maleTalkerMirrored != null) maleTalkerMirrored.SetActive(true);
            if (femaleTalkerMirrored != null) femaleTalkerMirrored.SetActive(false);
        }
    }

    private void ApplyDebaterAvatar()
    {
        bool isFemale = string.Equals(debaterGender, Gender.Female.ToString(), StringComparison.OrdinalIgnoreCase);
        bool isMale = string.Equals(debaterGender, Gender.Male.ToString(), StringComparison.OrdinalIgnoreCase);

        if (isFemale)
        {
            if (femaleDebater != null) femaleDebater.SetActive(true);
            if (maleDebater != null) maleDebater.SetActive(false);
        }
        else if (isMale)
        {
            if (maleDebater != null) maleDebater.SetActive(true);
            if (femaleDebater != null) femaleDebater.SetActive(false);
        }
    }

    private void PrepareAndRun()
    {
        sessionReady = true;
        SetupLogging(); // best-effort local logging if GameFlowManager is missing
        StartCoroutine(RunExperiment());
    }

    private IEnumerator CheckTtsHealthAndRun()
    {
        var healthUrls = GetHealthCheckUrls(ttsEndpoint);
        bool healthy = false;
        string lastError = "";

        foreach (var healthUrl in healthUrls)
        {
            Debug.Log($"TrialsController: Checking TTS service health at {healthUrl}");
            using (UnityWebRequest req = UnityWebRequest.Get(healthUrl))
            {
                req.timeout = 5; // seconds
                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success)
                {
                    healthy = true;
                    Debug.Log($"TrialsController: TTS service healthy via {healthUrl}");
                    break;
                }
                else
                {
                    lastError = req.error;
                    Debug.LogWarning($"TrialsController: TTS health check failed at {healthUrl}. Error: {req.error}");
                }
            }
        }

        if (!healthy)
        {
            Debug.LogError($"TrialsController: All TTS health checks failed. Last error: {lastError}");
            yield break;
        }

        PrepareAndRun();
    }

    private System.Collections.Generic.IEnumerable<string> GetHealthCheckUrls(string ttsUrl)
    {
        var urls = new System.Collections.Generic.List<string>();
        void Add(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!urls.Contains(url)) urls.Add(url);
        }

        try
        {
            var uri = new Uri(ttsUrl);
            // Root of host (e.g., http://ip:port/)
            var rootBuilder = new UriBuilder(uri.Scheme, uri.Host, uri.Port);
            var root = rootBuilder.Uri;
            Add(new Uri(root, "healthz").ToString());
            Add(new Uri(root, "health").ToString());

            // Relative to the provided endpoint (e.g., /api/v1/tts -> ../healthz)
            Add(new Uri(uri, "../healthz").ToString());
            Add(new Uri(uri, "../health").ToString());
        }
        catch (Exception)
        {
            // ignore and fall back to string concat
        }

        // Fallback: naive appends
        Add(ttsUrl.TrimEnd('/') + "/healthz");
        Add(ttsUrl.TrimEnd('/') + "/health");

        return urls;
    }

    private void SetupLogging()
    {
        if (!writeCsvLog) return;

        // If GameFlowManager exists, use its unified logger and skip local file setup
        if (GameFlowManager.Instance != null)
        {
            return;
        }

    #if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
    #else
        string baseDir = Application.persistentDataPath;
#   endif
        string logFileName = sessionId + "_log.csv";
        logFullPath = Path.Combine(baseDir, "logs");
        if (!Directory.Exists(logFullPath)) Directory.CreateDirectory(logFullPath);
        logFullPath = Path.Combine(logFullPath, logFileName);
        if (!File.Exists(logFullPath))
        {
            File.WriteAllText(logFullPath, "iso_time,realtime_s,scene,event,trial,phase,participant_id,participant_gender,talker_gender,detail\n");
        }
        Log("experiment_start", "");
    }

    private void Log(string evt, string detail, string phase = "")
    {
        if (!writeCsvLog) return;

        string trialStr = currentTrialIndex > 0 ? currentTrialIndex.ToString() : "";

        // Prefer unified logger
        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.LogEvent(evt, detail, phase, trialStr);
            return;
        }

        string iso = DateTime.UtcNow.ToString("o");
        float rt = Time.realtimeSinceStartup - t0;
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        string line = $"{iso},{rt:F3},{Escape(sceneName)},{Escape(evt)},{trialStr},{Escape(phase)},{Escape(sessionId)},{Escape(participantGender)},{Escape(talkerGender)},{Escape(detail)}\n";
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

        Log("experiment_start", "");
        // Subscribe to events for timing logs
        HookSequencerEvents(true);

        sequencer.autoStart = false; // we'll control when each trial starts

        for (int i = 1; i <= numberOfTrials; i++)
        {
            currentTrialIndex = i;
            // Build trial folder like: Audio/<sessionId>_session/trial_001/
            string trialFolder = $"{audioRoot}/{GetSessionFolderId()}/trial_{i:000}/";
            EnsureTrialFolderExists(trialFolder);

            // Assign line paths
            string debaterFolder = GetDebaterGenderFolder();
            string sceneId = trialSceneId.ContainsKey(i) ? trialSceneId[i] : "";
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                sceneId = "unknown";
                Debug.LogWarning($"TrialsController: Missing scene_id for trial {i}, using '{sceneId}'.");
            }
            string sceneAudioBase = $"scene_audio/{debaterFolder}/";
            sequencer.lineA1 = sceneAudioBase + $"{sceneId}_{debaterFolder}.wav";
            sequencer.lineA2 = sceneAudioBase + $"npc_2A_tts_{debaterFolder}.wav";
            sequencer.lineB2 = trialFolder + "user_2B_tts.wav";
            if (sequencer.micB != null)
            {
                sequencer.micB.saveRelativePath = trialFolder + "user_1B_mic.wav";
            }
            if (sequencer != null)
            {
                sequencer.b2Ready = !sequencer.waitForB2Ready;
            }

            // Log trial start
            Log("trial_start", trialFolder, "A1");

            // Kick off A1
            sequencer.phase = ConversationSequencerFourTurns.Phase.None; // reset visible state
            yield return StartCoroutine(sequencer.BeginSequence());

            // Wait until sequencer marks Done
            yield return new WaitUntil(() => sequencer.phase == ConversationSequencerFourTurns.Phase.Done);

            // Scales UI between trials
            if (scaleController != null)
            {
                yield return StartCoroutine(RunScalesForTrial(i));
            }

            Log("trial_end", trialFolder, "Done");
            yield return null; // tiny yield between trials
        }

        // Unhook events
        HookSequencerEvents(false);
        HookScaleEvents(false);
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
    private void OnMicFinished()
    {
        Log("B_mic_finished", "", "B1");
        StartCoroutine(ProcessB2ForTrial(currentTrialIndex));
    }
    private void OnMicDenied() => Log("B_mic_permission_denied", "", "B1");

    private void HookScaleEvents(bool on)
    {
        if (scaleController == null) return;
        if (on)
        {
            scaleController.OnUiEvent += OnScaleUiEvent;
        }
        else
        {
            scaleController.OnUiEvent -= OnScaleUiEvent;
        }
    }

    private void OnScaleUiEvent(ScaleQuestionnaireController.UiEvent evt, string detail)
    {
        switch (evt)
        {
            case ScaleQuestionnaireController.UiEvent.ScaleStarted:
                Log("scale_started", "", "Scale");
                break;
            case ScaleQuestionnaireController.UiEvent.ScaleEnded:
                Log("scale_end", "", "Scale");
                break;
            case ScaleQuestionnaireController.UiEvent.ScaleSaved:
                Log("scale_saved", detail, "Scale");
                break;
            case ScaleQuestionnaireController.UiEvent.RestStarted:
                Log("rest_start", "", "Rest");
                break;
            case ScaleQuestionnaireController.UiEvent.RestEnded:
                Log("rest_end", "", "Rest");
                break;
            case ScaleQuestionnaireController.UiEvent.PostStarted:
                Log("postQues_start", "", "Post");
                break;
            case ScaleQuestionnaireController.UiEvent.PostEnded:
                Log("postQues_end", "", "Post");
                break;
        }
    }

    private IEnumerator RunScalesForTrial(int trialId)
    {
        bool isFinalTrial = trialId >= numberOfTrials;
        bool showRest = restAfterTrial > 0 && trialId == restAfterTrial && !isFinalTrial;

        SetDebaterVisible(!hideDebaterDuringScales);
        if (scalesRoot != null && !scalesRoot.activeSelf)
        {
            scalesRoot.SetActive(true);
        }

        yield return StartCoroutine(scaleController.ShowAndWait(sessionId, trialId, showRest, isFinalTrial));

        if (scalesRoot != null && scalesRoot.activeSelf)
        {
            scalesRoot.SetActive(false);
        }
        SetDebaterVisible(true);
    }

    private void SetDebaterVisible(bool visible)
    {
        if (visible)
        {
            ApplyDebaterAvatar();
            return;
        }

        if (maleDebater != null) maleDebater.SetActive(false);
        if (femaleDebater != null) femaleDebater.SetActive(false);
    }

    private void SyncFromGameFlowManager()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm != null)
        {
            // Pull values from global manager
            if (!string.IsNullOrWhiteSpace(gfm.participantId)) sessionId = gfm.participantId;
            if (gfm.participantGender != Gender.None) participantGender = gfm.participantGender.ToString();
            if (gfm.talkerGender != Gender.None) talkerGender = gfm.talkerGender.ToString();
            if (gfm.debaterGender != Gender.None) debaterGender = gfm.debaterGender.ToString();
        }

        // Defaults if still missing
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = "demo";
        if (string.IsNullOrWhiteSpace(participantGender)) participantGender = Gender.Male.ToString();
        if (string.IsNullOrWhiteSpace(talkerGender)) talkerGender = Gender.Male.ToString();
        if (string.IsNullOrWhiteSpace(debaterGender)) debaterGender = Gender.Male.ToString();
    }

    private void LoadTrialContext()
    {
        try
        {
            string baseDir = Application.streamingAssetsPath;
            string contextId = sessionId;
            const string suffix = "_session";
            if (!string.IsNullOrWhiteSpace(contextId) && contextId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                contextId = contextId.Substring(0, contextId.Length - suffix.Length);
            }
            if (string.IsNullOrWhiteSpace(contextId)) contextId = "unknown";

            string expFolder = Path.Combine(baseDir, "Audio", contextId);
            string fileBase = expInfoFilePrefix + contextId;
            string fullPath = Path.Combine(expFolder + "_session", fileBase + "_session.csv");
            //Debug.Log($"[TrialsController] Final exp info fullPath: {fullPath}");
            if (!File.Exists(fullPath))
            {
                fullPath = Path.Combine(expFolder + "_session", fileBase);
                //Debug.Log($"[TrialsController] Final exp info fullPath: {fullPath}");
            }
            if (!File.Exists(fullPath))
            {
                string altBase = expInfoFilePrefix + contextId + "_session";
                fullPath = Path.Combine(expFolder + "_session", altBase + ".csv");
                //Debug.Log($"[TrialsController] Final exp info fullPath: {fullPath}");
                if (!File.Exists(fullPath))
                {
                    fullPath = Path.Combine(expFolder + "_session", altBase);
                    //Debug.Log($"[TrialsController] Final exp info fullPath: {fullPath}");
                }
            }

            if (!File.Exists(fullPath))
            {
                Debug.LogWarning($"TrialsController: exp info file not found for sessionId '{contextId}' under {Path.Combine(baseDir, "Audio")}");
                return;
            }

            var lines = File.ReadAllLines(fullPath);
            if (lines.Length == 0) return;

            int startIndex = 0;
            int trialCol = 0;
            int contextCol = 1;
            int sceneIdCol = -1;
            int conditionCol = -1;
            int voiceIdCol = -1;

            var header = ParseCsvLine(lines[0]);
            if (header.Count > 0 && header[0].Trim().Equals("trial", StringComparison.OrdinalIgnoreCase))
            {
                startIndex = 1;
                for (int i = 0; i < header.Count; i++)
                {
                    string h = header[i].Trim().ToLowerInvariant();
                    if (h == "trial") trialCol = i;
                    else if (h == "scene_id") sceneIdCol = i;
                    else if (h == "context") contextCol = i;
                    else if (h == "condition") conditionCol = i;
                    else if (h == "raw_voice_id") voiceIdCol = i;
                }
            }

            for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = ParseCsvLine(line);
                if (parts.Count <= trialCol) continue;
                if (int.TryParse(parts[trialCol].Trim(), out int trialIdx))
                {
                    if (sceneIdCol >= 0 && parts.Count > sceneIdCol)
                        trialSceneId[trialIdx] = parts[sceneIdCol].Trim();

                    if (parts.Count > contextCol)
                        trialContext[trialIdx] = parts[contextCol].Trim();

                    int condition = 1;
                    if (conditionCol >= 0 && parts.Count > conditionCol)
                    {
                        int.TryParse(parts[conditionCol].Trim(), out condition);
                        if (condition <= 0) condition = 1;
                    }
                    trialCondition[trialIdx] = condition;

                    string voiceId = "robotic";
                    if (voiceIdCol >= 0 && parts.Count > voiceIdCol)
                    {
                        string raw = parts[voiceIdCol].Trim();
                        if (!string.IsNullOrEmpty(raw)) voiceId = raw.Trim('\'', '"');
                    }
                    trialVoiceId[trialIdx] = voiceId;
                }
            }

            Debug.Log($"TrialsController: Loaded trial context from {fullPath} (rows={trialContext.Count})");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"TrialsController: Failed to load trial context: {ex.Message}");
        }
    }

    private static System.Collections.Generic.List<string> ParseCsvLine(string line)
    {
        var result = new System.Collections.Generic.List<string>();
        if (line == null) return result;

        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Length = 0;
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private string GetSessionId()
    {
        const string suffix = "_session";
        if (string.IsNullOrWhiteSpace(sessionId)) return "unknown";
        if (sessionId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return sessionId.Substring(0, sessionId.Length - suffix.Length);
        return sessionId;
    }

    private static void AddFormField(System.Collections.Generic.List<IMultipartFormSection> sections, string name, string value, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (!allowEmpty) return;
            value = " ";
        }
        sections.Add(new MultipartFormDataSection(name, value));
    }

    // --- TTS Request，for A1 and Audio Download ---
    private IEnumerator ProcessB2ForTrial(int trialId)
    {
        if (sequencer != null && sequencer.waitForB2Ready)
            sequencer.b2Ready = false;

        string trialFolder = $"{audioRoot}/{GetSessionFolderId()}/trial_{trialId:000}/";
        string contextText = trialContext.ContainsKey(trialId) ? trialContext[trialId] : "";
        int condition = trialCondition.ContainsKey(trialId) ? trialCondition[trialId] : 1;
        string voiceId = trialVoiceId.ContainsKey(trialId) ? trialVoiceId[trialId] : "clone";

        if (string.IsNullOrEmpty(contextText))
            Debug.LogWarning($"TrialsController: Missing context for trial {trialId}.");

        string micRelativePath = trialFolder + "user_1B_mic.wav";
        string micFullPath = GetFullPathFromRelative(micRelativePath);
        if (!File.Exists(micFullPath))
        {
            Debug.LogError($"TrialsController: mic audio not found at {micFullPath}");
            if (sequencer != null && sequencer.waitForB2Ready) sequencer.b2Ready = true;
            yield break;
        }

        byte[] micAudioBytes = File.ReadAllBytes(micFullPath);
        var formSections = new System.Collections.Generic.List<IMultipartFormSection>();
        AddFormField(formSections, "session_id", GetSessionId());
        AddFormField(formSections, "trial_id", trialId.ToString());
        AddFormField(formSections, "lang", b2Lang);
        AddFormField(formSections, "condition", condition.ToString());
        AddFormField(formSections, "voice_id", voiceId);
        AddFormField(formSections, "raw_ref_path", "", false);
        AddFormField(formSections, "user_context", contextText, true);
        formSections.Add(new MultipartFormFileSection("audio", micAudioBytes, Path.GetFileName(micFullPath), "audio/wav"));

        Debug.Log(
            "TrialsController: B2 multipart payload " +
            $"trial={trialId} " +
            $"session_id={GetSessionId()} " +
            $"lang={b2Lang} " +
            $"condition={condition} " +
            $"raw_voice_id={voiceId} " +
            $"raw_ref_path='' " +
            $"user_context_len={contextText?.Length ?? 0} " +
            $"audio={micFullPath}");

        using (UnityWebRequest req = UnityWebRequest.Post(b2ProcessEndpoint, formSections))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.chunkedTransfer = false; // safer for some servers

            Log("b2_request_sent", $"trial={trialId}; mic={micRelativePath}", "B2");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"TrialsController: B2 request failed for trial {trialId}: {req.error}");
                Log("b2_request_failed", req.error, "B2");
                if (sequencer != null && sequencer.waitForB2Ready) sequencer.b2Ready = true;
                yield break;
            }

            string responseText = req.downloadHandler.text;
            Debug.Log($"TrialsController: B2 response for trial {trialId}: {responseText}");
            var response = JsonUtility.FromJson<B2Response>(responseText);
            if (response == null || string.IsNullOrEmpty(response.tts_audio_path))
            {
                Debug.LogError("TrialsController: B2 response missing tts_audio_path.");
                Log("b2_response_invalid", responseText, "B2");
                if (sequencer != null && sequencer.waitForB2Ready) sequencer.b2Ready = true;
                yield break;
            }

            Log("b2_request_ok", $"trial={trialId}; tts_audio_path={response.tts_audio_path}", "B2");
            yield return StartCoroutine(DownloadB2Audio(response.tts_audio_path, trialFolder));
        }

        if (sequencer != null && sequencer.waitForB2Ready)
            sequencer.b2Ready = true;
    }

    private string GetRefPath()
    {
        bool male = string.Equals(talkerGender, "Male", StringComparison.OrdinalIgnoreCase);
        return male ? refMalePath : refFemalePath;
    }

    private string GetSessionFolderId()
    {
        const string suffix = "_session";
        if (string.IsNullOrWhiteSpace(sessionId)) return "unknown_session";
        if (sessionId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return sessionId;
        return sessionId + suffix;
    }

    private string GetDebaterGenderFolder()
    {
        if (string.IsNullOrWhiteSpace(debaterGender)) return "male";
        if (string.Equals(debaterGender, Gender.Female.ToString(), StringComparison.OrdinalIgnoreCase)) return "female";
        if (string.Equals(debaterGender, Gender.Male.ToString(), StringComparison.OrdinalIgnoreCase)) return "male";
        return debaterGender.Trim().ToLowerInvariant();
    }


    private string GetResolvedRefPath()
    {
#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.streamingAssetsPath;
#endif
        string relative = GetRefPath();
        // If caller forgot extension, try .wav (common case for reference files)
        string candidate = Path.Combine(baseDir, relative);
        if (!Path.HasExtension(candidate))
        {
            string withExt = candidate + ".wav";
            if (File.Exists(withExt)) candidate = withExt;
        }

        // Normalize to forward slashes for the service; avoids device-specific separators.
        string normalized = candidate.Replace("\\", "/");
        Debug.Log($"TrialsController: Using ref_path '{normalized}' (from '{relative}')");
        return normalized;
    }

    private void EnsureTrialFolderExists(string trialFolder)
    {
#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.streamingAssetsPath;
#endif
        string fullFolder = Path.Combine(baseDir, trialFolder);
        if (!Directory.Exists(fullFolder)) Directory.CreateDirectory(fullFolder);
    }

    private IEnumerator DownloadB2Audio(string serverAudioPath, string trialFolder)
    {
        if (string.IsNullOrEmpty(serverAudioPath))
        {
            Debug.LogWarning("TrialsController: tts_audio_path missing in B2 response.");
            yield break;
        }

        // Server returns path like sessions/P000_session/trial_001/user_2B_tts.wav
        // Download from /api/v1/download/<path> rooted at the host/port of b2ProcessEndpoint
        Uri baseUri = new Uri(b2ProcessEndpoint);
        var rootBuilder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port);
        Uri root = rootBuilder.Uri;
        string trimmedPath = serverAudioPath.TrimStart('/');
        string downloadPrefix = b2DownloadPrefix?.Trim('/');
        Uri downloadUri = new Uri(root, $"{downloadPrefix}/{trimmedPath}");
        string downloadUrl = downloadUri.ToString();

        Debug.Log($"TrialsController: Downloading B2 audio from {downloadUrl}");
        using (UnityWebRequest req = UnityWebRequest.Get(downloadUrl))
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"TrialsController: Failed to download B2 audio {serverAudioPath}: {req.error}");
                Log("b2_download_failed", serverAudioPath, "B2");
                yield break;
            }

            byte[] data = req.downloadHandler.data;
            Debug.Log($"TrialsController: B2 audio bytes received: {data?.Length ?? 0}");

            string fullFolder = GetFullPathFromRelative(trialFolder);
            Directory.CreateDirectory(fullFolder);
            string fileName = Path.GetFileName(serverAudioPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "user_2B_tts.wav";
            string destPath = Path.Combine(fullFolder, fileName);
            File.WriteAllBytes(destPath, data);
            Debug.Log($"TrialsController: Saved B2 audio to {destPath}");

            string relativePath = Path.Combine(trialFolder, fileName).Replace("\\", "/");
            sequencer.lineB2 = relativePath;
            Log("b2_download_ok", relativePath, "B2");
        }
    }

    private string GetFullPathFromRelative(string relativePath)
    {
#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.persistentDataPath;
#endif
        return Path.Combine(baseDir, relativePath);
    }

    [Serializable]
    private class TtsRequest
    {
        public string session_id;
        public int trial_id;
        public string text;
        public string ref_path;
    }

    [Serializable]
    private class TtsResponse
    {
        public string audio_path;
        public bool fallback;
        public string session_id;
        public int trial_id;
    }

    [Serializable]
    private class B2Response
    {
        public string status;
        public string tts_audio_path;
        public string session_id;
        public int trial_id;
    }
}
