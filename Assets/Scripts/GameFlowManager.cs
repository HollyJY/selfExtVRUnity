using System;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

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

    [Header("Logging")]
    [Tooltip("Write a CSV log under StreamingAssets/logs (Editor) or persistentDataPath/logs (build)")]
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
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
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
        participantId = id;
        participantGender = pGender;
        talkerGender = tGender;
        SetupLogging();
        ApplyParticipantAvatar();
        Log("participant_info_set", $"id={participantId}; participant_gender={participantGender}; talker_gender={talkerGender}; debater_gender={debaterGender}");
    }

    public void SetParticipantInfoWithDebater(string id, Gender pGender, Gender tGender, Gender dGender)
    {
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
            return;
        }

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
        logDirectory = Path.Combine(baseDir, "logs");
        if (!Directory.Exists(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        string logFileName = $"{DateTime.UtcNow:yyyyMMdd}_{participantId}_log.csv";
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
        float rt = Time.realtimeSinceStartup - t0;
        string sceneName = SceneManager.GetActiveScene().name;
        string line = $"{iso},{rt:F3},{Escape(sceneName)},{Escape(evt)},{Escape(trial)},{Escape(phase)},{Escape(participantId)},{participantGender},{talkerGender},{Escape(detail)}\n";
        File.AppendAllText(logFilePath, line);
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

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Log("scene_loaded", scene.name);
        if (scene.name == voiceSampleSceneName)
        {
            voiceSampleReadyToProceed = false;
        }
        ApplyParticipantAvatar();
    }

    public void SetVoiceSampleReadyToProceed(bool ready)
    {
        voiceSampleReadyToProceed = ready;
        Log("voice_sample_ready_to_proceed", $"ready={ready}");
    }

    private void ApplyParticipantAvatar()
    {
        if (participantGender == Gender.None) return;

        bool isFemale = participantGender == Gender.Female;
        bool isMale = participantGender == Gender.Male;
        if (!isFemale && !isMale) return;

        GameObject male = FindAvatarByName(maleTalkerMirroredName);
        GameObject female = FindAvatarByName(femaleTalkerMirroredName);

        if (isFemale)
        {
            if (female != null) female.SetActive(true);
            if (male != null) male.SetActive(false);
        }
        else if (isMale)
        {
            if (male != null) male.SetActive(true);
            if (female != null) female.SetActive(false);
        }
    }

    private static GameObject FindAvatarByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return GameObject.Find(name);
    }
}
