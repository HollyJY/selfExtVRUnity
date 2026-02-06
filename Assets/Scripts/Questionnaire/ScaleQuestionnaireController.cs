using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class ScaleQuestionnaireController : MonoBehaviour
{
    private enum UiMode
    {
        Questionnaire,
        Rest,
        Post
    }

    public enum UiEvent
    {
        ScaleStarted,
        ScaleEnded,
        RestStarted,
        RestEnded,
        PostStarted,
        PostEnded,
        ScaleSaved
    }

    [Header("Refs")]
    [SerializeField] private CsvQuestionnaireBuilder builder;
    [SerializeField] private Transform questionsRoot;
    [SerializeField] private Toggle submitToggle;
    [SerializeField] private Toggle postSubmitToggle;
    [SerializeField] private Transform postQuestionsRoot;

    [Header("Panels")]
    [SerializeField] private GameObject questionLists;
    [SerializeField] private GameObject restHint;
    [SerializeField] private GameObject postQues;

    [Header("Behavior")]
    [SerializeField] private bool disableOnStart = true;
    [SerializeField] private bool rebuildOnShow = true;
    [SerializeField] private bool hideOnSubmit = true;

    [Header("Save")]
    [SerializeField] private string outputFolderRelative = "Audio";
    [SerializeField] private string filePrefix = "scaleAns_";
    [SerializeField] private string postFilePrefix = "resPostQues_";

    private bool submitted;
    private string currentSessionId;
    private int currentTrialId;
    private bool restAfterSubmit;
    private bool postAfterSubmit;
    private UiMode mode = UiMode.Questionnaire;
    private string lastScaleSavePath;
    private string lastPostSavePath;
    private bool suppressScaleStartEvent;

    public event Action<UiEvent, string> OnUiEvent;

    private void Awake()
    {
        if (disableOnStart)
        {
            gameObject.SetActive(false);
        }
    }

    private void OnEnable()
    {
        if (submitToggle != null)
        {
            submitToggle.onValueChanged.RemoveListener(OnSubmitToggleChanged);
            submitToggle.onValueChanged.AddListener(OnSubmitToggleChanged);
        }
        if (postSubmitToggle != null)
        {
            postSubmitToggle.onValueChanged.RemoveListener(OnPostSubmitToggleChanged);
            postSubmitToggle.onValueChanged.AddListener(OnPostSubmitToggleChanged);
        }
    }

    private void OnDisable()
    {
        if (submitToggle != null)
        {
            submitToggle.onValueChanged.RemoveListener(OnSubmitToggleChanged);
        }
        if (postSubmitToggle != null)
        {
            postSubmitToggle.onValueChanged.RemoveListener(OnPostSubmitToggleChanged);
        }
    }

    public IEnumerator ShowAndWait(string sessionId, int trialId, bool showRestAfter, bool showPostAfter)
    {
        submitted = false;
        currentSessionId = NormalizeSessionId(sessionId);
        currentTrialId = trialId;
        restAfterSubmit = showRestAfter;
        postAfterSubmit = showPostAfter;
        mode = UiMode.Questionnaire;

        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        ShowQuestionnaire();
        if (rebuildOnShow && builder != null)
        {
            builder.Build();
        }

        yield return new WaitUntil(() => submitted);
    }

    private void OnSubmitToggleChanged(bool isOn)
    {
        if (!isOn)
        {
            return;
        }

        if (mode == UiMode.Rest)
        {
            RaiseEvent(UiEvent.RestEnded, "");
            submitted = true;
            if (hideOnSubmit)
            {
                gameObject.SetActive(false);
            }

            if (submitToggle != null)
            {
                submitToggle.SetIsOnWithoutNotify(false);
            }

            suppressScaleStartEvent = true;
            ShowQuestionnaire();
            return;
        }

        if (!ValidateAllAnswered(questionsRoot))
        {
            Debug.LogWarning("ScaleQuestionnaireController: Not all questions are answered.");
            if (submitToggle != null)
            {
                submitToggle.SetIsOnWithoutNotify(false);
            }
            return;
        }

        try
        {
            SaveAnswers(questionsRoot, filePrefix, out lastScaleSavePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"ScaleQuestionnaireController: Failed to save answers. {ex.Message}");
        }

        RaiseEvent(UiEvent.ScaleSaved, lastScaleSavePath ?? "");
        RaiseEvent(UiEvent.ScaleEnded, "");

        if (postAfterSubmit)
        {
            ShowPost();
            if (submitToggle != null)
            {
                submitToggle.SetIsOnWithoutNotify(false);
            }
            return;
        }

        if (restAfterSubmit)
        {
            ShowRest();
            if (submitToggle != null)
            {
                submitToggle.SetIsOnWithoutNotify(false);
            }
            return;
        }

        submitted = true;

        if (hideOnSubmit)
        {
            gameObject.SetActive(false);
        }

        if (submitToggle != null)
        {
            submitToggle.SetIsOnWithoutNotify(false);
        }
    }

    private void OnPostSubmitToggleChanged(bool isOn)
    {
        if (!isOn)
        {
            return;
        }

        if (!ValidateAllAnswered(ResolvePostRoot()))
        {
            Debug.LogWarning("ScaleQuestionnaireController: Not all post questions are answered.");
            if (postSubmitToggle != null)
            {
                postSubmitToggle.SetIsOnWithoutNotify(false);
            }
            return;
        }

        try
        {
            SaveAnswers(ResolvePostRoot(), postFilePrefix, out lastPostSavePath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"ScaleQuestionnaireController: Failed to save post answers. {ex.Message}");
        }

        RaiseEvent(UiEvent.PostEnded, lastPostSavePath ?? "");
        submitted = true;

        if (hideOnSubmit)
        {
            gameObject.SetActive(false);
        }

        if (postSubmitToggle != null)
        {
            postSubmitToggle.SetIsOnWithoutNotify(false);
        }

        suppressScaleStartEvent = true;
        ShowQuestionnaire();
    }

    private void ShowQuestionnaire()
    {
        mode = UiMode.Questionnaire;
        SetActiveSafe(questionLists, true);
        SetActiveSafe(restHint, false);
        SetActiveSafe(postQues, false);
        SetActiveSafe(submitToggle != null ? submitToggle.gameObject : null, true);
        SetActiveSafe(postSubmitToggle != null ? postSubmitToggle.gameObject : null, false);
        if (!suppressScaleStartEvent)
        {
            RaiseEvent(UiEvent.ScaleStarted, "");
        }
        suppressScaleStartEvent = false;
    }

    private void ShowRest()
    {
        mode = UiMode.Rest;
        SetActiveSafe(questionLists, false);
        SetActiveSafe(restHint, true);
        SetActiveSafe(postQues, false);
        SetActiveSafe(submitToggle != null ? submitToggle.gameObject : null, true);
        SetActiveSafe(postSubmitToggle != null ? postSubmitToggle.gameObject : null, false);
        RaiseEvent(UiEvent.RestStarted, "");
    }

    private void ShowPost()
    {
        mode = UiMode.Post;
        SetActiveSafe(questionLists, false);
        SetActiveSafe(restHint, false);
        SetActiveSafe(postQues, true);
        SetActiveSafe(submitToggle != null ? submitToggle.gameObject : null, false);
        SetActiveSafe(postSubmitToggle != null ? postSubmitToggle.gameObject : null, true);
        RaiseEvent(UiEvent.PostStarted, "");
    }

    private static void SetActiveSafe(GameObject obj, bool active)
    {
        if (obj != null)
        {
            obj.SetActive(active);
        }
    }

    private bool ValidateAllAnswered(Transform root)
    {
        var groups = GetQuestionGroups(root);
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (!group.gameObject.activeInHierarchy)
            {
                continue;
            }

            bool hasSelection = false;
            foreach (var t in group.GetComponentsInChildren<Toggle>(true))
            {
                if (t != null && t.isOn)
                {
                    hasSelection = true;
                    break;
                }
            }

            if (!hasSelection)
            {
                return false;
            }
        }

        return true;
    }

    private void SaveAnswers(Transform root, string prefix, out string savePath)
    {
        string baseDir = Application.streamingAssetsPath;
        string folder = Path.Combine(baseDir, outputFolderRelative, currentSessionId);
        Directory.CreateDirectory(folder);

        string fileName = prefix + currentSessionId + ".csv";
        string fullPath = Path.Combine(folder, fileName);
        savePath = fullPath;

        bool writeHeader = !File.Exists(fullPath);
        using (var writer = new StreamWriter(fullPath, true))
        {
            if (writeHeader)
            {
                writer.WriteLine("trial,question_index,question_text,answer_index,answer_text");
            }

            var groups = GetQuestionGroups(root);
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (!group.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var selected = GetSelectedToggle(group, out int answerIndex, out string answerText);
                if (selected == null)
                {
                    continue;
                }

                string questionText = GetQuestionText(group.transform, i + 1);
                string line = string.Join(",",
                    Escape(currentTrialId.ToString()),
                    Escape((i + 1).ToString()),
                    Escape(questionText),
                    Escape(answerIndex.ToString()),
                    Escape(answerText));

                writer.WriteLine(line);
            }
        }

        Debug.Log($"ScaleQuestionnaireController: Saved answers to {fullPath}");
    }

    private void RaiseEvent(UiEvent evt, string detail)
    {
        OnUiEvent?.Invoke(evt, detail ?? "");
    }

    private List<ToggleGroup> GetQuestionGroups(Transform root)
    {
        var resolvedRoot = root != null ? root : transform;
        var groups = new List<ToggleGroup>(resolvedRoot.GetComponentsInChildren<ToggleGroup>(true));
        return groups;
    }

    private Transform ResolvePostRoot()
    {
        if (postQuestionsRoot != null)
        {
            return postQuestionsRoot;
        }

        if (postQues != null)
        {
            return postQues.transform;
        }

        return transform;
    }

    private static Toggle GetSelectedToggle(ToggleGroup group, out int answerIndex, out string answerText)
    {
        answerIndex = 0;
        answerText = "";

        var toggles = new List<Toggle>(group.GetComponentsInChildren<Toggle>(true));
        for (int i = 0; i < toggles.Count; i++)
        {
            var t = toggles[i];
            if (t != null && t.isOn)
            {
                answerIndex = i + 1;
                answerText = GetToggleText(t.transform);
                return t;
            }
        }

        return null;
    }

    private static string GetQuestionText(Transform groupTransform, int fallbackIndex)
    {
        var questionRoot = FindQuestionRoot(groupTransform);
        if (questionRoot != null)
        {
            var subTitle = FindChildByNamePrefix(questionRoot, "SubTitle");
            if (subTitle != null)
            {
                var tmp = subTitle.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                {
                    return tmp.text;
                }

                var uiText = subTitle.GetComponentInChildren<Text>(true);
                if (uiText != null)
                {
                    return uiText.text;
                }
            }
        }

        return "Question " + fallbackIndex;
    }

    private static Transform FindQuestionRoot(Transform start)
    {
        var current = start;
        while (current != null)
        {
            if (current.name.StartsWith("Question (", StringComparison.Ordinal))
            {
                return current;
            }
            current = current.parent;
        }
        return null;
    }

    private static string GetToggleText(Transform toggleRoot)
    {
        var textRoot = FindChildByNamePrefix(toggleRoot, "Text");
        var tmp = textRoot != null ? textRoot.GetComponentInChildren<TMP_Text>(true) : null;
        if (tmp == null)
        {
            tmp = toggleRoot.GetComponentInChildren<TMP_Text>(true);
        }
        if (tmp != null)
        {
            return tmp.text;
        }

        var uiText = textRoot != null ? textRoot.GetComponentInChildren<Text>(true) : null;
        if (uiText == null)
        {
            uiText = toggleRoot.GetComponentInChildren<Text>(true);
        }
        return uiText != null ? uiText.text : "";
    }

    private static Transform FindChildByNamePrefix(Transform root, string prefix)
    {
        if (root == null)
        {
            return null;
        }

        foreach (Transform child in root)
        {
            if (child.name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return child;
            }

            var nested = FindChildByNamePrefix(child, prefix);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static string NormalizeSessionId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        const string suffix = "_session";
        if (raw.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return raw.Substring(0, raw.Length - suffix.Length);
        }

        return raw;
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(",") || s.Contains("\n") || s.Contains("\""))
        {
            return '"' + s.Replace("\"", "\"\"") + '"';
        }
        return s;
    }
}
