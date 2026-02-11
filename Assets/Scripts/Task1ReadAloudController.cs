using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class Task1ReadAloudController : MonoBehaviour
{
    [Header("UI Panels")]
    public CanvasGroup introPanel;          // Instruction panel shown first
    public CanvasGroup readingPanel;        // Panel with paragraph to read
    public CanvasGroup finishedPanel;       // Panel shown after recording completes
    public TextMeshProUGUI readingText;     // Paragraph content on screen
    public TextMeshProUGUI timerText;       // Optional timer label
    public Slider progressBar;              // Optional progress bar

    [Header("Recording Settings")]
    public string microphoneDevice = "";    // Leave empty to use default device
    public int sampleRate = 16000;
    public float maxRecordDuration = 90f;   // Seconds of recording
    [Tooltip("Minimum recording seconds before trigger can stop recording. 0 = no limit.")]
    [Min(0f)]
    public float minTriggerStopSeconds = 30f;

    [Header("File Settings")]
    public string participantId = "test00";
    public string taskName = "VoiceSample";
    public string audioFolderName = "Recordings";
    public string commandFolderName = "ServerCommands";
    public string paragraphId = "DutchWinter01";

    [TextArea(3, 10)]
    public string paragraphContent;         // Same text as readingText

    [Header("Upload Settings")]
    public bool uploadAfterSave = true;
    public string uploadUrl = "http://192.168.37.177:7000/api/v1/upload";
    public string uploadDest = "meta";
    public string uploadFileName = "sample_user.wav";
    public int uploadTrialId = 1;

    [Header("Fade Settings")]
    public float fadeDuration = 0.5f;

    private AudioClip recordingClip;
    private bool isRecording = false;
    private bool recordingCompleted = false;
    private bool hasProceededToNext = false;
    private bool finishedPanelShown = false;
    private float recordingStartTime;
    private string currentBaseFileName = "";

    void Start()
    {
        // Ensure initial UI state
        if (introPanel != null)
        {
            introPanel.gameObject.SetActive(true);
            introPanel.alpha = 1f;
        }

        if (readingPanel != null)
        {
            readingPanel.gameObject.SetActive(false);
            readingPanel.alpha = 0f;
        }

        if (readingText != null && !string.IsNullOrEmpty(paragraphContent))
        {
            readingText.text = paragraphContent;
        }

        if (finishedPanel != null)
        {
            finishedPanel.gameObject.SetActive(false);
            finishedPanel.alpha = 0f;
        }
        finishedPanelShown = false;

        // Pull participant id from global manager if available
        if (GameFlowManager.Instance != null && !string.IsNullOrEmpty(GameFlowManager.Instance.participantId))
        {
            participantId = GameFlowManager.Instance.participantId;
        }

        if (progressBar != null)
        {
            progressBar.minValue = 0f;
            progressBar.maxValue = 1f;
            progressBar.value = 0f;
        }

        if (timerText != null)
        {
            timerText.text = "";
        }

        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.SetVoiceSampleReadyToProceed(false);
        }
    }

    void Update()
    {
        // Input to start/stop recording
        if (DetectProceedPressed())
        {
            if (!isRecording && IsIntroVisible())
            {
                BeginTaskFromIntro();
            }
            else if (isRecording)
            {
                if (minTriggerStopSeconds <= 0f || (Time.time - recordingStartTime) >= minTriggerStopSeconds)
                {
                    StopRecordingAndSave();
                }
            }
            else if (recordingCompleted && finishedPanelShown && !hasProceededToNext)
            {
                ProceedToNextScene();
            }
        }

        if (isRecording)
        {
            UpdateRecordingUI();
            CheckAutoStop();
        }
    }

    /// <summary>
    /// Call this from your input logic when the participant confirms
    /// they are ready to start reading.
    /// </summary>
    public void BeginTaskFromIntro()
    {
        if (!gameObject.activeInHierarchy)
            return;

        StartCoroutine(BeginTaskRoutine());
    }

    private IEnumerator BeginTaskRoutine()
    {
        // Fade out intro panel
        if (introPanel != null)
        {
            yield return StartCoroutine(FadeCanvasGroup(introPanel, 1f, 0f, fadeDuration));
            introPanel.gameObject.SetActive(false);
        }

        // Prepare reading panel
        if (readingPanel != null)
        {
            readingPanel.gameObject.SetActive(true);
            if (readingText != null && !string.IsNullOrEmpty(paragraphContent))
            {
                readingText.text = paragraphContent;
            }
            yield return StartCoroutine(FadeCanvasGroup(readingPanel, 0f, 1f, fadeDuration));
        }

        // Start recording
        StartRecording();
    }

    private void StartRecording()
    {
        if (isRecording)
            return;

        // Refresh participant id from global state before saving files
        if (GameFlowManager.Instance != null && !string.IsNullOrEmpty(GameFlowManager.Instance.participantId))
        {
            participantId = GameFlowManager.Instance.participantId;
        }

        // Choose microphone: prefer HMD mic if present, otherwise default system mic
        if (string.IsNullOrEmpty(microphoneDevice))
        {
            microphoneDevice = PickBestMicrophone();
            if (string.IsNullOrEmpty(microphoneDevice))
            {
                Debug.LogError("No microphone devices found.");
                return;
            }
        }

        recordingClip = Microphone.Start(microphoneDevice, false, Mathf.CeilToInt(maxRecordDuration), sampleRate);
        recordingStartTime = Time.time;
        isRecording = true;

        if (timerText != null)
        {
            timerText.text = "Recording...";
        }

        GameFlowManager.Instance?.LogEvent("voice_sample_recording_started", $"mic={microphoneDevice}", "voiceSample");
    }

    private string PickBestMicrophone()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0) return "";

        // Simple heuristic: look for HMD/VR keywords first
        foreach (var dev in devices)
        {
            var lower = dev.ToLowerInvariant();
            if (lower.Contains("oculus") || lower.Contains("hmd") || lower.Contains("vr") || lower.Contains("headset"))
            {
                Debug.Log($"Using HMD microphone: {dev}");
                return dev;
            }
        }

        // Fallback to the first available device
        Debug.Log($"Using default microphone: {devices[0]}");
        return devices[0];
    }

    private void UpdateRecordingUI()
    {
        float elapsed = Time.time - recordingStartTime;
        float t = Mathf.Clamp01(elapsed / maxRecordDuration);

        if (progressBar != null)
        {
            progressBar.value = t;
        }

        if (timerText != null)
        {
            int remaining = Mathf.Max(0, Mathf.CeilToInt(maxRecordDuration - elapsed));
            timerText.text = $"Please read aloud. Time left: {remaining}s";
        }
    }

    private void CheckAutoStop()
    {
        float elapsed = Time.time - recordingStartTime;
        if (elapsed >= maxRecordDuration)
        {
            StopRecordingAndSave();
        }
    }

    /// <summary>
    /// You can call this manually if you want to stop early.
    /// </summary>
    public void StopRecordingAndSave()
    {
        if (!isRecording)
            return;

        isRecording = false;

        int position = Microphone.GetPosition(microphoneDevice);
        Microphone.End(microphoneDevice);

        if (recordingClip == null)
        {
            Debug.LogError("Recording clip is null.");
            return;
        }

        // Trim clip to actual length
        float[] samples = new float[position * recordingClip.channels];
        recordingClip.GetData(samples, 0);

        AudioClip trimmedClip = AudioClip.Create(
            "TrimmedRecording",
            position,
            recordingClip.channels,
            recordingClip.frequency,
            false
        );
        trimmedClip.SetData(samples, 0);

        recordingClip = trimmedClip;

        // Build file names
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        currentBaseFileName = $"{participantId}_{taskName}_{paragraphId}_{timeStamp}";

        // Create folders under StreamingAssets/Audio/<participantId>/ (or persistentDataPath fallback)
#if UNITY_EDITOR
        string basePath = Application.streamingAssetsPath;
#else
        string basePath = Application.streamingAssetsPath;
#endif
        string participantFolder = participantId + "_session";
        string audioFolderPath = Path.Combine(basePath, "Audio", participantFolder);
        string commandFolderPath = Path.Combine(basePath, commandFolderName);
        Directory.CreateDirectory(audioFolderPath);
        Directory.CreateDirectory(commandFolderPath);

        // Save wav with fixed filename
        string wavFileName = $"VoiceSample_{participantId}.wav";
        string wavPath = Path.Combine(audioFolderPath, wavFileName);
        WavUtility.SaveWav(wavPath, recordingClip);
        Debug.Log($"Saved audio to {wavPath}");

        string relativeWavPath = Path.Combine("Audio", participantFolder, wavFileName).Replace("\\", "/");

        // Save command file with text content
        string txtPath = Path.Combine(commandFolderPath, currentBaseFileName + ".txt");
        File.WriteAllText(txtPath, paragraphContent);
        Debug.Log($"Saved command text to {txtPath}");

        GameFlowManager.Instance?.LogEvent("voice_sample_saved", $"wav={relativeWavPath}", "voiceSample");

        if (uploadAfterSave)
        {
            StartCoroutine(UploadWavToServer(wavPath, participantId));
        }

        // Update UI state
        if (timerText != null)
        {
            timerText.text = "Recording finished. Thank you. Press space to continue.";
        }

        if (progressBar != null)
        {
            progressBar.value = 1f;
        }

        recordingCompleted = true;
        finishedPanelShown = false;
        StartCoroutine(ShowFinishedPanel());
    }

    private IEnumerator UploadWavToServer(string wavPath, string sessionId)
    {
        if (string.IsNullOrEmpty(uploadUrl))
        {
            Debug.LogWarning("Upload URL is empty. Skipping upload.");
            yield break;
        }

        if (!File.Exists(wavPath))
        {
            Debug.LogWarning($"Upload skipped; file not found at {wavPath}");
            yield break;
        }

        WWWForm form = new WWWForm();
        form.AddField("session_id", sessionId);
        form.AddField("trial_id", uploadTrialId);
        form.AddField("dest", uploadDest);
        form.AddField("filename", uploadFileName);

        byte[] fileData = File.ReadAllBytes(wavPath);
        form.AddBinaryData("file", fileData, Path.GetFileName(wavPath), "audio/wav");

        using (UnityWebRequest request = UnityWebRequest.Post(uploadUrl, form))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"Upload failed: {request.error}");
            }
            else
            {
                Debug.Log($"Upload succeeded: {request.downloadHandler.text}");
            }
        }
    }

    private IEnumerator ShowFinishedPanel()
    {
        if (readingPanel != null)
        {
            yield return StartCoroutine(FadeCanvasGroup(readingPanel, 1f, 0f, fadeDuration));
            readingPanel.gameObject.SetActive(false);
        }

        if (finishedPanel != null)
        {
            finishedPanel.gameObject.SetActive(true);
            yield return StartCoroutine(FadeCanvasGroup(finishedPanel, 0f, 1f, fadeDuration));
        }
        finishedPanelShown = true;
        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.SetVoiceSampleReadyToProceed(true);
        }
    }

    private void ProceedToNextScene()
    {
        hasProceededToNext = true;
        GameFlowManager.Instance?.LogEvent("voice_sample_finished_proceed", "", "voiceSample");
        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.GoToInteractionScene();
        }
    }

    private bool IsIntroVisible()
    {
        return introPanel != null && introPanel.gameObject.activeInHierarchy && introPanel.alpha > 0.01f;
    }

    private bool DetectProceedPressed()
    {
        // Right-hand controller trigger (OVR) or keyboard space bar
        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            return true;
        }
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            return true;
        }
#else
        if (Input.GetKeyDown(KeyCode.Space))
        {
            return true;
        }
#endif
        return false;
    }

    private IEnumerator FadeOutReadingAndNotifyNext()
    {
        if (readingPanel != null)
        {
            yield return StartCoroutine(FadeCanvasGroup(readingPanel, 1f, 0f, fadeDuration));
            readingPanel.gameObject.SetActive(false);
        }

        // You can notify an experiment controller here
        // Example: ExperimentController.Instance.OnTask1Finished();
    }

    private IEnumerator FadeCanvasGroup(CanvasGroup group, float from, float to, float duration)
    {
        if (group == null)
            yield break;

        float elapsed = 0f;
        group.alpha = from;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            group.alpha = Mathf.Lerp(from, to, t);
            yield return null;
        }

        group.alpha = to;
    }

    private void OnDestroy()
    {
        if (isRecording)
        {
            Microphone.End(microphoneDevice);
        }
    }

    /// <summary>
    /// Very small utility class that writes a mono or stereo AudioClip to a wav file.
    /// </summary>
    public static class WavUtility
    {
        public static void SaveWav(string filePath, AudioClip clip)
        {
            if (clip == null)
            {
                Debug.LogError("WavUtility received null clip.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
            {
                int sampleCount = clip.samples * clip.channels;
                int frequency = clip.frequency;
                short channels = (short)clip.channels;

                // Convert samples to 16 bit integer
                float[] floatData = new float[sampleCount];
                clip.GetData(floatData, 0);

                short[] intData = new short[sampleCount];
                byte[] bytesData = new byte[sampleCount * 2];

                const float rescaleFactor = 32767f;

                for (int i = 0; i < sampleCount; i++)
                {
                    intData[i] = (short)(floatData[i] * rescaleFactor);
                    byte[] byteArr = BitConverter.GetBytes(intData[i]);
                    byteArr.CopyTo(bytesData, i * 2);
                }

                // Write header
                int byteRate = frequency * channels * 2;
                int subChunkTwoSize = sampleCount * 2;
                int chunkSize = 36 + subChunkTwoSize;

                // RIFF header
                WriteString(fileStream, "RIFF");
                WriteInt(fileStream, chunkSize);
                WriteString(fileStream, "WAVE");

                // fmt subchunk
                WriteString(fileStream, "fmt ");
                WriteInt(fileStream, 16);                    // Subchunk1Size
                WriteShort(fileStream, 1);                   // AudioFormat PCM
                WriteShort(fileStream, channels);
                WriteInt(fileStream, frequency);
                WriteInt(fileStream, byteRate);
                WriteShort(fileStream, (short)(channels * 2)); // BlockAlign
                WriteShort(fileStream, 16);                  // BitsPerSample

                // data subchunk
                WriteString(fileStream, "data");
                WriteInt(fileStream, subChunkTwoSize);
                fileStream.Write(bytesData, 0, bytesData.Length);
            }
        }

        private static void WriteInt(FileStream fs, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            fs.Write(bytes, 0, bytes.Length);
        }

        private static void WriteShort(FileStream fs, short value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            fs.Write(bytes, 0, bytes.Length);
        }

        private static void WriteString(FileStream fs, string value)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(value);
            fs.Write(bytes, 0, bytes.Length);
        }
    }
}
