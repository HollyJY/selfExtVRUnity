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
    public float microphoneReadyTimeout = 3f;
    public float minValidRecordingSeconds = 0.25f;
    public float silencePeakThreshold = 0.003f;
    public float silenceRmsThreshold = 0.001f;

    [Header("Mic Monitoring")]
    public bool monitorPlayback = true;
    public AudioSource monitorAudioSource;
    [Range(0f, 1f)]
    public float monitorVolume = 1f;

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
    private bool isStartingRecording = false;
    private bool recordingFailed = false;
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

        SetupMonitorAudioSource();

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
                StopRecordingAndSave();
            }
            else if (recordingFailed && !isStartingRecording)
            {
                StartRecording();
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
        if (isRecording || isStartingRecording)
            return;

        StartCoroutine(StartRecordingRoutine());
    }

    private IEnumerator StartRecordingRoutine()
    {
        isStartingRecording = true;
        recordingFailed = false;
        recordingCompleted = false;
        TraceVoiceSampleMicStep("voice_sample_mic_flow_begin", $"participantId={participantId}; configuredDevice={microphoneDevice}; devices={GetMicrophoneDevicesForLog()}; sampleRate={sampleRate}; maxDuration={maxRecordDuration}");

        // Refresh participant id from global state before saving files
        if (GameFlowManager.Instance != null && !string.IsNullOrEmpty(GameFlowManager.Instance.participantId))
        {
            participantId = GameFlowManager.Instance.participantId;
            TraceVoiceSampleMicStep("voice_sample_participant_synced", $"participantId={participantId}");
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        const string androidMicPermission = "android.permission.RECORD_AUDIO";
        TraceVoiceSampleMicStep("voice_sample_permission_check", $"permission={UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidMicPermission)}");
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidMicPermission))
        {
            Debug.Log("VoiceSample mic status: requesting Android RECORD_AUDIO permission.");
            TraceVoiceSampleMicStep("voice_sample_permission_request", androidMicPermission);
            UnityEngine.Android.Permission.RequestUserPermission(androidMicPermission);

            float permissionWait = 0f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidMicPermission) && permissionWait < microphoneReadyTimeout)
            {
                permissionWait += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(androidMicPermission))
            {
                TraceVoiceSampleMicStep("voice_sample_permission_denied", $"waited={permissionWait:F2}s");
                FailRecordingStart("Android microphone permission denied or timed out.");
                yield break;
            }
        }
#endif

        // Choose microphone: prefer HMD mic if present, otherwise default system mic
        if (string.IsNullOrEmpty(microphoneDevice))
        {
            TraceVoiceSampleMicStep("voice_sample_device_select_begin", $"devices={GetMicrophoneDevicesForLog()}");
            microphoneDevice = PickBestMicrophone();
            TraceVoiceSampleMicStep("voice_sample_device_selected_raw", string.IsNullOrEmpty(microphoneDevice) ? "<empty>" : microphoneDevice);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (string.IsNullOrEmpty(microphoneDevice))
            {
                microphoneDevice = null; // Android/Quest can use the platform default mic even when devices is empty.
                TraceVoiceSampleMicStep("voice_sample_device_android_default", "using platform default mic");
            }
#else
            if (string.IsNullOrEmpty(microphoneDevice))
            {
                FailRecordingStart("No microphone devices found.");
                yield break;
            }
#endif
        }

        string deviceLabel = string.IsNullOrEmpty(microphoneDevice) ? "<default>" : microphoneDevice;
        Debug.Log($"VoiceSample mic status: starting. device='{deviceLabel}', sampleRate={sampleRate}, maxRecordDuration={maxRecordDuration}s");
        if (timerText != null)
        {
            timerText.text = "Starting microphone...";
        }

        TraceVoiceSampleMicStep("voice_sample_microphone_start_call", $"device={deviceLabel}; duration={Mathf.CeilToInt(maxRecordDuration)}; sampleRate={sampleRate}");
        recordingClip = Microphone.Start(microphoneDevice, false, Mathf.CeilToInt(maxRecordDuration), sampleRate);
        if (recordingClip == null)
        {
            FailRecordingStart($"Microphone.Start returned null. device='{deviceLabel}'");
            yield break;
        }
        TraceVoiceSampleMicStep("voice_sample_microphone_clip_created", $"clip={recordingClip.name}; channels={recordingClip.channels}; frequency={recordingClip.frequency}; samples={recordingClip.samples}");

        int position = 0;
        float readyWait = 0f;
        float nextWaitLog = 0f;
        while (position <= 0 && readyWait < microphoneReadyTimeout)
        {
            try
            {
                position = Microphone.GetPosition(microphoneDevice);
            }
            catch (Exception e)
            {
                FailRecordingStart($"Microphone.GetPosition failed during startup. device='{deviceLabel}', error={e.Message}");
                yield break;
            }

            if (position > 0)
                break;

            if (readyWait >= nextWaitLog)
            {
                TraceVoiceSampleMicStep("voice_sample_waiting_for_position", $"wait={readyWait:F2}s; timeout={microphoneReadyTimeout:F2}s; isRecording={IsMicrophoneRecordingSafe(microphoneDevice)}");
                nextWaitLog += 1f;
            }

            readyWait += Time.unscaledDeltaTime;
            yield return null;
        }

        if (position <= 0)
        {
            FailRecordingStart($"Microphone did not become ready within {microphoneReadyTimeout:F1}s. device='{deviceLabel}', isRecording={IsMicrophoneRecordingSafe(microphoneDevice)}");
            yield break;
        }

        recordingStartTime = Time.time;
        isRecording = true;
        isStartingRecording = false;

        if (timerText != null)
        {
            timerText.text = "Recording...";
        }

        StartMicMonitoring(recordingClip, deviceLabel);
        Debug.Log($"VoiceSample mic status: ready. device='{deviceLabel}', initialPosition={position}, channels={recordingClip.channels}, frequency={recordingClip.frequency}");
        TraceVoiceSampleMicStep("voice_sample_mic_ready", $"device={deviceLabel}; initialPosition={position}; channels={recordingClip.channels}; frequency={recordingClip.frequency}");
        GameFlowManager.Instance?.LogEvent("voice_sample_recording_started", $"mic={deviceLabel}; initial_position={position}; channels={recordingClip.channels}; frequency={recordingClip.frequency}", "voiceSample");
    }

    private void FailRecordingStart(string reason)
    {
        TraceVoiceSampleMicStep("voice_sample_mic_start_failed", reason);
        isRecording = false;
        isStartingRecording = false;
        recordingFailed = true;
        recordingCompleted = false;

        StopMicMonitoring();
        try
        {
            Microphone.End(microphoneDevice);
        }
        catch { }

        recordingClip = null;

        if (timerText != null)
        {
            timerText.text = "Microphone failed. Press space to retry.";
        }

        if (progressBar != null)
        {
            progressBar.value = 0f;
        }

        Debug.LogError($"VoiceSample mic status: start failed. {reason}");
        GameFlowManager.Instance?.LogEvent("voice_sample_recording_failed", reason, "voiceSample");
    }

    private void FailRecordingSave(string reason)
    {
        StopMicMonitoring();
        recordingFailed = true;
        recordingCompleted = false;
        recordingClip = null;

        if (timerText != null)
        {
            timerText.text = "Recording failed or was silent. Press space to retry.";
        }

        if (progressBar != null)
        {
            progressBar.value = 0f;
        }

        Debug.LogError($"VoiceSample mic status: save rejected. {reason}");
        GameFlowManager.Instance?.LogEvent("voice_sample_recording_rejected", reason, "voiceSample");
    }

    private static void GetSignalStats(float[] samples, out float peak, out float rms)
    {
        peak = 0f;
        rms = 0f;

        if (samples == null || samples.Length == 0)
            return;

        double sumSquares = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = Mathf.Abs(samples[i]);
            if (abs > peak)
                peak = abs;

            sumSquares += samples[i] * samples[i];
        }

        rms = Mathf.Sqrt((float)(sumSquares / samples.Length));
    }

    private static bool IsMicrophoneRecordingSafe(string deviceName)
    {
        try
        {
            return Microphone.IsRecording(deviceName);
        }
        catch
        {
            return false;
        }
    }

    private string PickBestMicrophone()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogWarning("VoiceSample mic devices: none found.");
            return "";
        }

        Debug.Log($"VoiceSample mic devices: {string.Join(", ", devices)}");

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

    private void TraceVoiceSampleMicStep(string evt, string detail = "")
    {
        string message = $"[VOICE_SAMPLE_MIC_STARTUP] {evt} detail={detail}";
        Debug.Log(message);
        GameFlowManager.Instance?.LogStartupStep(evt, detail, "voiceSampleMic");
    }

    private static string GetMicrophoneDevicesForLog()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0) return "<none>";
        return string.Join("|", devices);
    }

    private void SetupMonitorAudioSource()
    {
        if (!monitorPlayback)
            return;

        if (monitorAudioSource == null)
            monitorAudioSource = GetComponent<AudioSource>();

        if (monitorAudioSource == null)
            monitorAudioSource = gameObject.AddComponent<AudioSource>();

        monitorAudioSource.playOnAwake = false;
        monitorAudioSource.loop = true;
        monitorAudioSource.volume = monitorVolume;
        monitorAudioSource.mute = false;
    }

    private void StartMicMonitoring(AudioClip clip, string deviceLabel)
    {
        if (!monitorPlayback || clip == null)
            return;

        SetupMonitorAudioSource();
        if (monitorAudioSource == null)
        {
            Debug.LogWarning($"VoiceSample mic monitor: no AudioSource available. device='{deviceLabel}'");
            return;
        }

        monitorAudioSource.Stop();
        monitorAudioSource.clip = clip;
        monitorAudioSource.loop = true;
        monitorAudioSource.volume = monitorVolume;
        monitorAudioSource.Play();
        Debug.Log($"VoiceSample mic monitor: started. device='{deviceLabel}', volume={monitorVolume:F2}");
    }

    private void StopMicMonitoring()
    {
        if (monitorAudioSource == null)
            return;

        if (monitorAudioSource.isPlaying)
            monitorAudioSource.Stop();

        monitorAudioSource.clip = null;
        Debug.Log("VoiceSample mic monitor: stopped.");
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
        string deviceLabel = string.IsNullOrEmpty(microphoneDevice) ? "<default>" : microphoneDevice;
        StopMicMonitoring();

        int position = 0;
        bool micWasRecording = false;
        try
        {
            position = Microphone.GetPosition(microphoneDevice);
            micWasRecording = Microphone.IsRecording(microphoneDevice);
        }
        catch (Exception e)
        {
            Debug.LogError($"VoiceSample mic status: failed to read stop position. device='{deviceLabel}', error={e.Message}");
        }
        Microphone.End(microphoneDevice);

        if (recordingClip == null)
        {
            FailRecordingSave("Recording clip is null.");
            return;
        }

        float elapsed = Time.time - recordingStartTime;
        int minValidFrames = Mathf.CeilToInt(minValidRecordingSeconds * recordingClip.frequency);
        if (position <= 0 && elapsed >= maxRecordDuration - 0.05f)
        {
            position = recordingClip.samples;
            Debug.LogWarning($"VoiceSample mic status: stop position was 0 at duration limit; using full clip length. device='{deviceLabel}', frames={position}");
        }

        if (position < minValidFrames)
        {
            FailRecordingSave($"Recording too short or no samples captured. device='{deviceLabel}', position={position}, minFrames={minValidFrames}, elapsed={elapsed:F2}s, micWasRecording={micWasRecording}");
            return;
        }

        // Trim clip to actual length
        float[] samples = new float[position * recordingClip.channels];
        recordingClip.GetData(samples, 0);

        GetSignalStats(samples, out float peak, out float rms);
        Debug.Log($"VoiceSample mic status: captured. device='{deviceLabel}', frames={position}, seconds={(float)position / recordingClip.frequency:F2}, peak={peak:F6}, rms={rms:F6}, micWasRecording={micWasRecording}");
        if (peak < silencePeakThreshold || rms < silenceRmsThreshold)
        {
            FailRecordingSave($"Recording appears silent. device='{deviceLabel}', peak={peak:F6}, rms={rms:F6}, peakThreshold={silencePeakThreshold:F6}, rmsThreshold={silenceRmsThreshold:F6}");
            return;
        }

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
        StopMicMonitoring();
        if (isRecording || isStartingRecording)
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
