using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(AudioSource))]
public class MicSpeechController : MonoBehaviour
{
    [Header("References")]
    public Animator animator;                     // assign Avatar B's Animator
    public string talkBoolName = "IsMicTalking";  // Animator bool to drive talking state
    public AudioSource audioSource;               // will output mic monitoring to listener

    [Header("Mic Settings")]
    public string overrideDeviceName = null;      // set to force a specific device name; otherwise auto-pick
    [Tooltip("Comma-separated keywords to detect HMD mics (case-insensitive).")]
    public string hmdKeywords = "Quest,Oculus,Meta,Headset,HMD";
    public int sampleRate = 16000;                // low latency, speech-sufficient
    public int lengthSec = 10;                    // ring buffer length
    public bool loop = true;                      // ring buffer looping
    public bool monitorPlayback = true;           // if true, you hear the mic in headset/speakers
    public float microphoneReadyTimeout = 5f;     // max time to wait for Unity to report mic samples

    [Header("Limits")]
    public float maxSpeakSeconds = 120f;          // safety cap for B1

    [Header("Events")]
    public UnityEvent OnMicStarted;
    public UnityEvent OnMicFinished;
    public UnityEvent OnMicPermissionDenied;      // fired if Android mic permission is denied
    public UnityEvent OnMicStartFailed = new UnityEvent();

    [Header("Saving")]
    public bool saveOnFinish = true;
    [Tooltip("Relative path under StreamingAssets (Editor/Desktop) or persistentDataPath (Build). Example: Audio/demo-session_session/trial_001/user_B1_mic.wav")] 
    public string saveRelativePath = "";

    // captured recording (trimmed)
    private float[] lastSamples = null;
    private int lastChannels = 0;
    private int lastFrequency = 0;
    private int lastFrameCount = 0; // frames per channel

    // runtime timer for trimming
    private float recordingElapsed = 0f;

    public bool IsMicActive { get; private set; }
    public string CurrentDeviceName { get; private set; }

    void Awake()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();
        if (animator == null) animator = GetComponentInParent<Animator>();
        TraceMicStep("mic_awake", $"audioSource={DescribeObject(audioSource)}; animator={DescribeObject(animator)}; saveRelativePath={saveRelativePath}");
    }

    void Reset()
    {
        if (audioSource == null) audioSource = GetComponent<AudioSource>();
        if (animator == null) animator = GetComponentInParent<Animator>();
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = true; // monitor continuously while mic is active
        }
    }

    public void BeginMic()
    {
        TraceMicStep("mic_begin_requested", $"isActive={IsMicActive}; overrideDevice={overrideDeviceName}; sampleRate={sampleRate}; lengthSec={lengthSec}; monitorPlayback={monitorPlayback}");
        if (IsMicActive)
        {
            TraceMicStep("mic_begin_ignored", "already active");
            return;
        }
        StartCoroutine(BeginMicFlow());
    }

    public void EndMic()
    {
        TraceMicStep("mic_end_requested", $"isActive={IsMicActive}; currentDevice={CurrentDeviceName ?? "<default>"}");
        if (!IsMicActive)
        {
            TraceMicStep("mic_end_ignored", "not active");
            return;
        }
        StopMicInternal();
    }

    private IEnumerator BeginMicFlow()
    {
        TraceMicStep("mic_flow_begin", $"devices={GetMicrophoneDevicesForLog()}");
        // 1) Android/Quest: request runtime permission
#if UNITY_ANDROID && !UNITY_EDITOR
        const string ANDROID_MIC_PERM = "android.permission.RECORD_AUDIO";
        TraceMicStep("mic_permission_check", $"permission={UnityEngine.Android.Permission.HasUserAuthorizedPermission(ANDROID_MIC_PERM)}");
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ANDROID_MIC_PERM))
        {
            TraceMicStep("mic_permission_request", ANDROID_MIC_PERM);
            UnityEngine.Android.Permission.RequestUserPermission(ANDROID_MIC_PERM);
            float t = 0f;
            const float timeout = 5f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ANDROID_MIC_PERM) && t < timeout)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(ANDROID_MIC_PERM))
            {
                Debug.LogError("Mic permission denied on Android.");
                TraceMicStep("mic_permission_denied", $"waited={t:F2}s");
                OnMicPermissionDenied?.Invoke();
                yield break;
            }
        }
#endif

        // 2) Choose device
        TraceMicStep("mic_device_select_begin", $"overrideDevice={overrideDeviceName}; hmdKeywords={hmdKeywords}; devices={GetMicrophoneDevicesForLog()}");
        string device = ChooseBestMicDevice();
        CurrentDeviceName = device; // may be null => use default device
        TraceMicStep("mic_device_selected", $"device={device ?? "<default>"}");

        // 3) Start microphone
        if (audioSource == null)
        {
            FailMicStart("AudioSource is not assigned.");
            yield break;
        }

        audioSource.loop = monitorPlayback;
        lastSamples = null; lastChannels = 0; lastFrequency = 0; lastFrameCount = 0;
        TraceMicStep("mic_start_call", $"device={device ?? "<default>"}; loop={loop}; lengthSec={lengthSec}; sampleRate={sampleRate}");
        audioSource.clip = Microphone.Start(device, loop, lengthSec, sampleRate);
        recordingElapsed = 0f;
        if (audioSource.clip == null)
        {
            FailMicStart($"Microphone.Start returned null. Device='{device ?? "<default>"}'");
            yield break;
        }
        TraceMicStep("mic_clip_created", $"clip={audioSource.clip.name}; channels={audioSource.clip.channels}; frequency={audioSource.clip.frequency}; samples={audioSource.clip.samples}");
        // Wait until the recording position is set
        int pos = 0;
        float readyWait = 0f;
        float nextWaitLog = 0f;
        while (pos <= 0 && readyWait < microphoneReadyTimeout)
        {
            try
            {
                pos = Microphone.GetPosition(device);
            }
            catch (System.Exception e)
            {
                FailMicStart($"Microphone.GetPosition failed. Device='{device ?? "<default>"}', error={e.Message}");
                yield break;
            }

            if (pos > 0) break;

            if (readyWait >= nextWaitLog)
            {
                TraceMicStep("mic_waiting_for_position", $"wait={readyWait:F2}s; timeout={microphoneReadyTimeout:F2}s; isRecording={IsMicrophoneRecordingSafe(device)}");
                nextWaitLog += 1f;
            }

            readyWait += Time.unscaledDeltaTime;
            yield return null;
        }

        if (pos <= 0)
        {
            FailMicStart($"Microphone did not become ready within {microphoneReadyTimeout:F1}s. Device='{device ?? "<default>"}', isRecording={IsMicrophoneRecordingSafe(device)}");
            yield break;
        }

        // 4) Start monitoring playback if desired
        if (monitorPlayback)
        {
            TraceMicStep("mic_monitor_playback_start", $"audioSource={DescribeObject(audioSource)}");
            audioSource.Play();
        }

        // 5) Update state + events + animator
        IsMicActive = true;
        if (animator != null) animator.SetBool(talkBoolName, true);
        TraceMicStep("mic_started", $"device={device ?? "<default>"}; initialPosition={pos}; animator={DescribeObject(animator)}; talkBool={talkBoolName}");
        OnMicStarted?.Invoke();

        // 6) Safety timer
        float elapsed = 0f;
        while (IsMicActive && elapsed < maxSpeakSeconds)
        {
            elapsed += Time.deltaTime;
            recordingElapsed = elapsed;
            yield return null;
        }

        if (IsMicActive)
        {
            // timed out
            TraceMicStep("mic_max_duration_reached", $"maxSpeakSeconds={maxSpeakSeconds}");
            StopMicInternal();
        }
    }

    private void FailMicStart(string reason)
    {
        TraceMicStep("mic_start_failed", reason);
        if (audioSource != null)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
        }

        try
        {
            Microphone.End(CurrentDeviceName);
        }
        catch { }

        IsMicActive = false;
        if (animator != null) animator.SetBool(talkBoolName, false);
        Debug.LogError($"MicSpeechController: start failed. {reason}");
        OnMicStartFailed?.Invoke();
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

    private void StopMicInternal()
    {
        TraceMicStep("mic_stop_begin", $"device={CurrentDeviceName ?? "<default>"}; saveOnFinish={saveOnFinish}; saveRelativePath={saveRelativePath}; elapsed={recordingElapsed:F2}s");
        // animator off
        if (animator != null) animator.SetBool(talkBoolName, false);

        // capture last recording before stopping
        CaptureLastRecording();

        // stop monitoring
        if (audioSource != null)
        {
            if (audioSource.isPlaying) audioSource.Stop();
            audioSource.clip = null;
        }

        // stop recording
        try
        {
            Microphone.End(CurrentDeviceName);
        }
        catch { }

        IsMicActive = false;

        if (saveOnFinish && !string.IsNullOrEmpty(saveRelativePath))
        {
            SaveLastRecording(saveRelativePath);
        }

        TraceMicStep("mic_stop_done", $"device={CurrentDeviceName ?? "<default>"}; frames={lastFrameCount}; channels={lastChannels}; frequency={lastFrequency}");
        OnMicFinished?.Invoke();
    }

    private string ChooseBestMicDevice()
    {
        // On Android standalone builds, Unity typically ignores Microphone.devices and uses default, so return null
#if UNITY_ANDROID && !UNITY_EDITOR
        TraceMicStep("mic_device_android_default", "Unity Android build: using platform default device");
        return null; // default HMD mic on Quest
#else
        if (!string.IsNullOrEmpty(overrideDeviceName))
        {
            TraceMicStep("mic_device_override", overrideDeviceName);
            return overrideDeviceName;
        }

        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            TraceMicStep("mic_device_list_empty", "falling back to default device");
            return null; // fallback to default
        }

        // try to find an HMD device by keyword
        var keys = (hmdKeywords ?? "").Split(',');
        for (int i = 0; i < devices.Length; i++)
        {
            string name = devices[i];
            foreach (var raw in keys)
            {
                string k = raw.Trim();
                if (string.IsNullOrEmpty(k)) continue;
                if (name.ToLowerInvariant().Contains(k.ToLowerInvariant()))
                {
                    Debug.Log($"MicSpeechController: selected HMD mic '{name}'");
                    TraceMicStep("mic_device_hmd_match", $"device={name}; keyword={k}");
                    return name;
                }
            }
        }
        // otherwise default device
        Debug.Log("MicSpeechController: no HMD mic found, using default mic");
        TraceMicStep("mic_device_no_hmd_match", $"devices={GetMicrophoneDevicesForLog()}");
        return null;
#endif
    }

    private void TraceMicStep(string evt, string detail = "")
    {
        string message = $"[MIC_STARTUP] {evt} detail={detail}";
        Debug.Log(message);
        GameFlowManager.Instance?.LogStartupStep(evt, detail, "mic");
    }

    private static string GetMicrophoneDevicesForLog()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0) return "<none>";
        return string.Join("|", devices);
    }

    private static string DescribeObject(UnityEngine.Object obj)
    {
        if (obj == null) return "<null>";
        return obj.name;
    }

    /// <summary>
    /// Capture the most recent recording segment from the ring buffer into lastSamples/lastChannels/lastFrequency.
    /// Must be called BEFORE Microphone.End and BEFORE clearing audioSource.clip.
    /// </summary>
    private void CaptureLastRecording()
    {
        var clip = audioSource != null ? audioSource.clip : null;
        if (clip == null) { lastSamples = null; lastChannels = 0; lastFrequency = 0; lastFrameCount = 0; return; }

        int freq = clip.frequency;
        int channels = clip.channels;
        int totalFrames = clip.samples; // frames per channel

        // How many frames were actually spoken
        int wantedFrames = Mathf.Clamp(Mathf.RoundToInt(recordingElapsed * freq), 0, totalFrames);
        if (wantedFrames <= 0)
        {
            lastSamples = null; lastChannels = 0; lastFrequency = 0; lastFrameCount = 0; return;
        }

        // Where the microphone stopped writing
        int pos = 0;
        try { pos = Microphone.GetPosition(CurrentDeviceName); } catch { pos = 0; }
        pos = Mathf.Clamp(pos, 0, totalFrames);

        // We need the last 'wantedFrames' ending at 'pos', with wrap-around if needed
        int start = pos - wantedFrames;
        if (start < 0) start += totalFrames;

        float[] data;
        if (start + wantedFrames <= totalFrames)
        {
            // contiguous copy
            data = new float[wantedFrames * channels];
            clip.GetData(data, start);
        }
        else
        {
            // wrapped copy: two segments
            int firstFrames = totalFrames - start;
            int secondFrames = wantedFrames - firstFrames;

            float[] part1 = new float[firstFrames * channels];
            float[] part2 = new float[secondFrames * channels];
            clip.GetData(part1, start);
            clip.GetData(part2, 0);

            data = new float[wantedFrames * channels];
            System.Array.Copy(part1, 0, data, 0, part1.Length);
            System.Array.Copy(part2, 0, data, part1.Length, part2.Length);
        }

        lastSamples = data;
        lastChannels = channels;
        lastFrequency = freq;
        lastFrameCount = wantedFrames;
    }

    /// <summary>
    /// Save the last captured recording as a 16-bit PCM WAV at the given relative path.
    /// In Editor/Desktop it writes under StreamingAssets, in builds under persistentDataPath.
    /// </summary>
    public void SaveLastRecording(string relativePath)
    {
        if (lastSamples == null || lastSamples.Length == 0 || lastChannels <= 0 || lastFrequency <= 0)
        {
            Debug.LogWarning("MicSpeechController.SaveLastRecording: nothing captured to save.");
            return;
        }

        string baseDir;
#if UNITY_EDITOR
        baseDir = Application.streamingAssetsPath;
#else
        baseDir = Application.persistentDataPath;
#endif
        string fullPath = Path.Combine(baseDir, relativePath);
        string dir = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        byte[] wavBytes = EncodeWav16(lastSamples, lastChannels, lastFrequency);
        File.WriteAllBytes(fullPath, wavBytes);
        Debug.Log($"Saved mic recording to: {fullPath}  (frames={lastFrameCount}, ch={lastChannels}, sr={lastFrequency})");
    }

    // ---- WAV encoding (16-bit PCM) ----
    private static byte[] EncodeWav16(float[] samples, int channels, int sampleRate)
    {
        int sampleCount = samples.Length; // interleaved
        int byteRate = sampleRate * channels * 2; // 16-bit
        int subchunk2Size = sampleCount * 2;
        int chunkSize = 36 + subchunk2Size;

        using (var ms = new MemoryStream(44 + subchunk2Size))
        using (var bw = new BinaryWriter(ms))
        {
            // RIFF header
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt  subchunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);              // Subchunk1Size for PCM
            bw.Write((short)1);        // AudioFormat = 1 (PCM)
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)(channels * 2)); // BlockAlign
            bw.Write((short)16);       // BitsPerSample

            // data subchunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);

            // PCM data (clamp and convert to int16)
            for (int i = 0; i < sampleCount; i++)
            {
                float f = Mathf.Clamp(samples[i], -1f, 1f);
                short s = (short)Mathf.RoundToInt(f * 32767f);
                bw.Write(s);
            }

            bw.Flush();
            return ms.ToArray();
        }
    }
}
