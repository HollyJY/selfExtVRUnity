using UnityEngine.Networking;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;

[RequireComponent(typeof(Animator))]
public class AgentSpeechController : MonoBehaviour
{
    [Header("References")]
    public Animator animator;
    public AudioSource audioSource;

    [Header("Content")]

    // to put the name of the audio file in the StreamingAssets folder
    public string audioFileName = "Audio/agent_question_sample/agent_helloWorld.wav"; // e.g., "Audio/line01.wav"
    public string talkBoolName = "IsTalking";

    [Header("Events")]
    public UnityEvent OnSpeechStarted;
    public UnityEvent OnSpeechFinished;

    public bool IsSpeaking { get; private set; }

    [Header("Timing")]
    public bool playOnStart = true;
    public float preDelay = 10f;      // wait before speaking
    public float postDelay = 0.05f;  // short pause after audio ends

    void Reset()
    {
        animator = GetComponent<Animator>();
        audioSource = GetComponentInChildren<AudioSource>();
    }

    void Start()
    {
        if (playOnStart && !string.IsNullOrEmpty(audioFileName))
        {
            PlayLine(audioFileName);
        }
    }

    // Helper method to guess AudioType from file extension
    private AudioType GuessAudioType(string filename)
    {
        string ext = System.IO.Path.GetExtension(filename).ToLowerInvariant();
        switch (ext)
        {
            case ".wav": return AudioType.WAV;
            case ".ogg": return AudioType.OGGVORBIS;
            case ".mp3": return AudioType.MPEG;
            case ".aiff":
            case ".aif": return AudioType.AIFF;
            default: return AudioType.UNKNOWN;
        }
    }

    public void PlayLine(string filename)
    {
        StopAllCoroutines();
        StartCoroutine(PlayRoutine(filename));
    }

    private IEnumerator PlayRoutine(string filename)
    {
        if (preDelay > 0f) yield return new WaitForSeconds(preDelay);

        IsSpeaking = true;
        OnSpeechStarted?.Invoke();

        if (animator != null) animator.SetBool(talkBoolName, true);

        if (audioSource != null && !string.IsNullOrEmpty(filename))
        {
            string rawPath = System.IO.Path.Combine(Application.streamingAssetsPath, filename);
            string url = rawPath;
#if UNITY_ANDROID && !UNITY_EDITOR
            // On Android, StreamingAssets are inside the APK. Unity provides a jar URL already via streamingAssetsPath.
            // Use the path as-is (it usually starts with jar:file:// or content:// depending on Unity version).
#else
            // In Editor / Standalone builds, prepend file:// so UnityWebRequest treats it as a local file URL.
            if (!url.StartsWith("file://"))
                url = "file://" + url;
#endif

            // Choose audio type based on filename extension (defaults to WAV if unknown)
            AudioType aType = GuessAudioType(filename);
            if (aType == AudioType.UNKNOWN)
            {
                Debug.LogWarning($"Unknown audio extension for '{filename}', defaulting to WAV.");
                aType = AudioType.WAV;
            }

            Debug.Log($"Trying to load audio from: {url}");
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, aType))
            {
                yield return www.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                if (www.result != UnityWebRequest.Result.Success)
#else
                if (www.isNetworkError || www.isHttpError)
#endif
                {
                    Debug.LogError("Failed to load audio: " + www.error + "\nURL: " + url);
                }
                else
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    audioSource.clip = clip;
                    audioSource.Play();
                    while (audioSource.isPlaying) yield return null;
                }
            }
        }

        if (postDelay > 0f) yield return new WaitForSeconds(postDelay);

        if (animator != null) animator.SetBool(talkBoolName, false);

        IsSpeaking = false;
        OnSpeechFinished?.Invoke();
    }
}