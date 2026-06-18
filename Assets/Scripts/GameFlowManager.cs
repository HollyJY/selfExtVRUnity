using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

public enum Gender
{
    None = 0,
    Male = 1,
    Female = 2,
    Other = 3
}

public class GameFlowManager : MonoBehaviour
{
    // Singleton instance so other scripts can easily access the experiment info
    public static GameFlowManager Instance { get; private set; }

    [Header("Participant Info")]
    [Tooltip("Participant ID, e.g. P001")] 
    public string participantId;

    [Tooltip("Participant gender")] 
    public Gender participantGender = Gender.None;

    [Tooltip("Conversation partner gender")] 
    public Gender talkerGender = Gender.None;

    [Tooltip("Debater gender")] 
    public Gender debaterGender = Gender.None;

    [Header("Scene Names")] 
    [Tooltip("Lobby scene (start scene)")]
    public string lobbySceneName = "0_ExpLobby";

    [Tooltip("Scene for recording voice sample")]
    public string voiceSampleSceneName = "1_VoiceSample";

    [Tooltip("Main interaction scene")]
    public string interactionSceneName = "2_InteractionScene";

    [Header("Avatar")]
    public string maleTalkerMirroredName = "male_talkerMirrored";
    public string femaleTalkerMirroredName = "female_talkerMirrored";
    public bool forceEnableSelectedAvatarRenderers = true;

    [Header("Logging")]
    [Tooltip("Write a CSV log under StreamingAssets/Audio/<session>_session (Editor) or persistentDataPath/Audio/<session>_session (build)")]
    public bool writeCsvLog = true;

    private string logDirectory;
    private string logFilePath;
    private float t0;
    private bool logReady;
    private bool voiceSampleReadyToProceed;

    private void Awake()
    {
        // Ensure only one instance exists and keep it when loading new scenes
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        t0 = Time.realtimeSinceStartup;
        TraceStartupStep("game_flow_awake", $"scene={SceneManager.GetActiveScene().name}; xr={GetXrStatus()}");
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TraceStartupStep("game_flow_enabled", $"scene={SceneManager.GetActiveScene().name}; xr={GetXrStatus()}");
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    /// <summary>
    /// Called by your UI script in 0_ExpLobby after the participant has entered their info.
    /// </summary>
    public void SetParticipantInfo(string id, Gender pGender, Gender tGender)
    {
        TraceStartupStep("participant_info_begin", $"id={id}; participant_gender={pGender}; talker_gender={tGender}; debater_gender={debaterGender}");
        participantId = id;
        participantGender = pGender;
        talkerGender = tGender;
        SetupLogging();
        ApplyParticipantAvatar();
        Log("participant_info_set", $"id={participantId}; participant_gender={participantGender}; talker_gender={talkerGender}; debater_gender={debaterGender}");
    }

    public void SetParticipantInfoWithDebater(string id, Gender pGender, Gender tGender, Gender dGender)
    {
        TraceStartupStep("participant_info_begin", $"id={id}; participant_gender={pGender}; talker_gender={tGender}; debater_gender={dGender}");
        participantId = id;
        participantGender = pGender;
        talkerGender = tGender;
        debaterGender = dGender;
        SetupLogging();
        ApplyParticipantAvatar();
        Log("participant_info_set", $"id={participantId}; participant_gender={participantGender}; talker_gender={talkerGender}; debater_gender={debaterGender}");
    }

    public void SetDebaterGender(Gender dGender)
    {
        debaterGender = dGender;
        Log("debater_gender_set", $"debater_gender={debaterGender}");
    }

    /// <summary>
    /// Simple check so the UI can know if info is complete.
    /// </summary>
    public bool HasValidParticipantInfo()
    {
        bool hasId = !string.IsNullOrWhiteSpace(participantId);
        bool hasParticipantGender = participantGender != Gender.None;
        bool hasTalkerGender = talkerGender != Gender.None;

        return hasId && hasParticipantGender && hasTalkerGender;
    }

    /// <summary>
    /// Move from lobby to the voice sample scene.
    /// Usually called after validating participant info.
    /// </summary>
    public void GoToVoiceSampleScene()
    {
        if (!HasValidParticipantInfo())
        {
            Debug.LogWarning("GameFlowManager: Participant info not complete, cannot start voice sample.");
            return;
        }

        voiceSampleReadyToProceed = false;
        TraceStartupStep("load_scene_requested", $"target={voiceSampleSceneName}; from={SceneManager.GetActiveScene().name}; xr={GetXrStatus()}");
        Log("go_to_voice_sample_scene");
        SceneManager.LoadSceneAsync(voiceSampleSceneName, LoadSceneMode.Single);
    }

    /// <summary>
    /// Move from voice sample scene to the interaction scene.
    /// Call this when recording is finished.
    /// </summary>
    public void GoToInteractionScene()
    {
        string activeScene = SceneManager.GetActiveScene().name;
        if (activeScene == voiceSampleSceneName && !voiceSampleReadyToProceed)
        {
            Debug.LogWarning("GameFlowManager: Voice sample not finished yet; blocked transition to interaction scene.");
            var stack = new System.Diagnostics.StackTrace(1, true);
            UnityEngine.Debug.LogError($"GameFlowManager: Blocked GoToInteractionScene call stack:\n{stack}");
            return;
        }

        TraceStartupStep("load_scene_requested", $"target={interactionSceneName}; from={activeScene}; xr={GetXrStatus()}");
        Log("go_to_interaction_scene");
        SceneManager.LoadSceneAsync(interactionSceneName, LoadSceneMode.Single);
    }

    /// <summary>
    /// Optional: go back to lobby if you need to restart.
    /// </summary>
    public void GoBackToLobby()
    {
        Log("go_back_to_lobby");
        SceneManager.LoadSceneAsync(lobbySceneName, LoadSceneMode.Single);
    }

    /// <summary>
    /// Public entry point so other scripts can append to the shared log.
    /// </summary>
    public void LogEvent(string evt, string detail = "", string phase = "", string trial = "")
    {
        Log(evt, detail, phase, trial);
    }

    public void LogStartupStep(string evt, string detail = "", string phase = "", string trial = "")
    {
        TraceStartupStep(evt, detail, phase, trial);
    }

    public float GetRealtimeSinceSessionStart()
    {
        return Time.realtimeSinceStartup - t0;
    }

    public string GetSessionIdForPaths()
    {
        if (string.IsNullOrWhiteSpace(participantId))
        {
            return "no_id";
        }

        const string suffix = "_session";
        if (participantId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return participantId.Substring(0, participantId.Length - suffix.Length);
        }

        return participantId;
    }

    private void SetupLogging()
    {
        if (logReady || !writeCsvLog) return;

        // Allow a best-effort ID so we still get logs even if info was skipped
        if (string.IsNullOrWhiteSpace(participantId))
        {
            participantId = "no_id";
            Debug.LogWarning("GameFlowManager: Participant ID missing, logging with placeholder 'no_id'.");
        }

#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.persistentDataPath;
#endif
        string sessionIdForPath = GetSessionIdForPaths();
        string sessionFolder = sessionIdForPath + "_session";
        logDirectory = Path.Combine(baseDir, "Audio", sessionFolder);
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        string logFileName = $"{DateTime.UtcNow:yyyyMMdd}_{sessionIdForPath}_log.csv";
        logFilePath = Path.Combine(logDirectory, logFileName);
        if (!File.Exists(logFilePath))
        {
            File.WriteAllText(logFilePath, "iso_time,realtime_s,scene,event,trial,phase,participant_id,participant_gender,talker_gender,detail\n");
        }
        logReady = true;
        Log("log_initialized", logFileName);
    }

    private void Log(string evt, string detail = "", string phase = "", string trial = "")
    {
        if (!writeCsvLog) return;
        if (!logReady) SetupLogging();
        if (!logReady) return;
        string iso = DateTime.UtcNow.ToString("o");
        float rt = GetRealtimeSinceSessionStart();
        string sceneName = SceneManager.GetActiveScene().name;
        string line = $"{iso},{rt:F3},{Escape(sceneName)},{Escape(evt)},{Escape(trial)},{Escape(phase)},{Escape(participantId)},{participantGender},{talkerGender},{Escape(detail)}\n";
        File.AppendAllText(logFilePath, line);
#if UNITY_EDITOR
        Debug.Log($"[LOG] {evt} t={rt:F3}s detail={detail}");
#endif
    }

    private void TraceStartupStep(string evt, string detail = "", string phase = "startup", string trial = "")
    {
        string message = $"[STARTUP] {evt} detail={detail}";
        Debug.Log(message);
        if (logReady)
        {
            Log(evt, detail, phase, trial);
        }
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(",") || s.Contains("\n") || s.Contains("\""))
            return '"' + s.Replace("\"", "\"\"") + '"';
        return s;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TraceStartupStep("scene_loaded", $"scene={scene.name}; mode={mode}; xr={GetXrStatus()}");
        Log("scene_loaded", scene.name);
        if (scene.name == voiceSampleSceneName)
        {
            voiceSampleReadyToProceed = false;
            TraceStartupStep("voice_sample_gate_reset", "ready=false");
        }
        TraceStartupStep("tracking_global_scene_loaded", AvatarTrackingDiagnostics.DescribeGlobalTracking(), "tracking");
        ApplyParticipantAvatar();
        StartCoroutine(ReapplyParticipantAvatarAfterSceneSettles(scene.name));
        StartCoroutine(LogTrackingStatusAfterSceneSettles(scene.name));
    }

    public void SetVoiceSampleReadyToProceed(bool ready)
    {
        voiceSampleReadyToProceed = ready;
        Log("voice_sample_ready_to_proceed", $"ready={ready}");
    }

    public bool IsVoiceSampleReadyToProceed()
    {
        return voiceSampleReadyToProceed;
    }

    private void ApplyParticipantAvatar()
    {
        TraceStartupStep("avatar_apply_begin", $"participant_gender={participantGender}; male_name={maleTalkerMirroredName}; female_name={femaleTalkerMirroredName}");
        if (participantGender == Gender.None)
        {
            TraceStartupStep("avatar_apply_skipped", "participant_gender=None");
            return;
        }

        bool isFemale = participantGender == Gender.Female;
        bool isMale = participantGender == Gender.Male;
        if (!isFemale && !isMale)
        {
            TraceStartupStep("avatar_apply_skipped", $"unsupported_participant_gender={participantGender}");
            return;
        }

        GameObject male = FindAvatarByName(maleTalkerMirroredName);
        GameObject female = FindAvatarByName(femaleTalkerMirroredName);
        TraceStartupStep("avatar_lookup_result", $"male={DescribeObject(male)}; female={DescribeObject(female)}");
        TraceStartupStep("tracking_avatar_lookup_result", $"male={AvatarTrackingDiagnostics.DescribeAvatarTracking(male)}; female={AvatarTrackingDiagnostics.DescribeAvatarTracking(female)}", "tracking");

        if (isFemale)
        {
            if (female != null) female.SetActive(true);
            if (male != null) male.SetActive(false);
            if (female != null && forceEnableSelectedAvatarRenderers) EnableAvatarRenderers(female);
            TraceStartupStep("avatar_apply_done", $"selected=female; male={DescribeAvatar(male)}; female={DescribeAvatar(female)}");
            TraceStartupStep("tracking_avatar_apply_done", $"selected=female; {AvatarTrackingDiagnostics.DescribeGlobalTracking()}; selectedTracking={AvatarTrackingDiagnostics.DescribeAvatarTracking(female)}", "tracking");
        }
        else if (isMale)
        {
            if (male != null) male.SetActive(true);
            if (female != null) female.SetActive(false);
            if (male != null && forceEnableSelectedAvatarRenderers) EnableAvatarRenderers(male);
            TraceStartupStep("avatar_apply_done", $"selected=male; male={DescribeAvatar(male)}; female={DescribeAvatar(female)}");
            TraceStartupStep("tracking_avatar_apply_done", $"selected=male; {AvatarTrackingDiagnostics.DescribeGlobalTracking()}; selectedTracking={AvatarTrackingDiagnostics.DescribeAvatarTracking(male)}", "tracking");
        }
    }

    private IEnumerator ReapplyParticipantAvatarAfterSceneSettles(string sceneName)
    {
        yield return null;
        TraceStartupStep("avatar_reapply_next_frame", $"scene={sceneName}");
        ApplyParticipantAvatar();

        yield return new WaitForSeconds(0.25f);
        TraceStartupStep("avatar_reapply_after_delay", $"scene={sceneName}");
        ApplyParticipantAvatar();
    }

    private IEnumerator LogTrackingStatusAfterSceneSettles(string sceneName)
    {
        float[] delays = { 0.5f, 1.5f, 3f };
        for (int i = 0; i < delays.Length; i++)
        {
            yield return new WaitForSeconds(delays[i]);
            GameObject male = FindAvatarByName(maleTalkerMirroredName);
            GameObject female = FindAvatarByName(femaleTalkerMirroredName);
            TraceStartupStep(
                "tracking_status_delayed",
                $"scene={sceneName}; delay={delays[i]:F1}s; {AvatarTrackingDiagnostics.DescribeGlobalTracking()}; male={AvatarTrackingDiagnostics.DescribeAvatarTracking(male)}; female={AvatarTrackingDiagnostics.DescribeAvatarTracking(female)}",
                "tracking");
        }
    }

    private static GameObject FindAvatarByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

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

    private static string DescribeObject(GameObject obj)
    {
        if (obj == null) return "<null>";
        return $"{obj.name}(activeSelf={obj.activeSelf}, activeInHierarchy={obj.activeInHierarchy}, scene={obj.scene.name})";
    }

    private static void EnableAvatarRenderers(GameObject avatar)
    {
        foreach (Renderer renderer in avatar.GetComponentsInChildren<Renderer>(true))
        {
            renderer.enabled = true;
        }
    }

    private static string DescribeAvatar(GameObject avatar)
    {
        if (avatar == null) return "<null>";

        Renderer[] renderers = avatar.GetComponentsInChildren<Renderer>(true);
        int enabled = 0;
        int activeVisible = 0;
        string firstRenderer = "<none>";
        Bounds bounds = new Bounds(avatar.transform.position, Vector3.zero);
        bool hasBounds = false;

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer.enabled) enabled++;
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) activeVisible++;
            if (i == 0) firstRenderer = $"{renderer.name}(enabled={renderer.enabled}, active={renderer.gameObject.activeInHierarchy}, layer={LayerMask.LayerToName(renderer.gameObject.layer)})";

            if (renderer.enabled)
            {
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
        }

        Transform t = avatar.transform;
        return $"{DescribeObject(avatar)}; pos={t.position}; scale={t.lossyScale}; layer={LayerMask.LayerToName(avatar.layer)}; renderers={renderers.Length}; enabledRenderers={enabled}; activeVisibleRenderers={activeVisible}; firstRenderer={firstRenderer}; boundsCenter={bounds.center}; boundsSize={bounds.size}";
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
}
