using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public sealed class CsvQuestionnaireBuilder : MonoBehaviour
{
    [Header("CSV")]
    [SerializeField] private string csvRelativePath = "ExpMaterial/scale.csv";

    [Header("UI")]
    [SerializeField] private Transform controlsParent;
    [SerializeField] private RectTransform questionTemplate;
    [SerializeField] private bool useTemplateAsFirst = true;
    [SerializeField] private bool disableTemplateAfterBuild = true;
    [SerializeField] private bool clearPreviouslyGenerated = true;

    [Header("Build")]
    [SerializeField] private bool buildOnEnable = true;

    private void OnEnable()
    {
        if (buildOnEnable)
        {
            Build();
        }
    }

    [ContextMenu("Build Questionnaire")]
    public void Build()
    {
        if (controlsParent == null || questionTemplate == null)
        {
            Debug.LogError("CsvQuestionnaireBuilder: Missing Controls Parent or Question Template.");
            return;
        }

        StartCoroutine(BuildFromCsv());
    }

    private System.Collections.IEnumerator BuildFromCsv()
    {
        string csvPath = System.IO.Path.Combine(Application.streamingAssetsPath, csvRelativePath);
        string csvText = null;

        if (csvPath.Contains("://") || Application.platform == RuntimePlatform.Android)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(csvPath))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"CsvQuestionnaireBuilder: Failed to load CSV at {csvPath}. {request.error}");
                    yield break;
                }

                csvText = request.downloadHandler.text;
            }
        }
        else
        {
            if (!System.IO.File.Exists(csvPath))
            {
                Debug.LogError($"CsvQuestionnaireBuilder: CSV not found at {csvPath}");
                yield break;
            }

            csvText = System.IO.File.ReadAllText(csvPath, Encoding.UTF8);
        }

        BuildFromCsvText(csvText);
    }

    private void BuildFromCsvText(string csvText)
    {
        if (string.IsNullOrEmpty(csvText))
        {
            Debug.LogWarning("CsvQuestionnaireBuilder: CSV is empty.");
            return;
        }

        if (clearPreviouslyGenerated)
        {
            ClearGeneratedQuestions();
        }

        var rows = ReadCsv(csvText);
        if (rows.Count <= 1)
        {
            Debug.LogWarning("CsvQuestionnaireBuilder: CSV has no data rows.");
            return;
        }

        int insertIndex = questionTemplate.GetSiblingIndex();
        int questionNumber = 1;
        bool templateUsed = false;
        bool templateWasActive = questionTemplate.gameObject.activeSelf;
        if (!templateWasActive)
        {
            questionTemplate.gameObject.SetActive(true);
        }

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count < 2)
            {
                continue;
            }

            string questionText = row[1].Trim();
            if (string.IsNullOrEmpty(questionText))
            {
                continue;
            }

            List<string> choices = new List<string>();
            if (row.Count >= 3)
            {
                choices = SplitChoices(row[2]);
            }

            RectTransform questionRoot;
            if (!templateUsed && useTemplateAsFirst)
            {
                questionRoot = questionTemplate;
                questionRoot.gameObject.SetActive(true);
                templateUsed = true;
            }
            else
            {
                var instance = Instantiate(questionTemplate.gameObject, controlsParent);
                questionRoot = instance.GetComponent<RectTransform>();
                questionRoot.gameObject.AddComponent<GeneratedQuestionMarker>();
                questionRoot.gameObject.SetActive(true);
            }

            questionRoot.SetSiblingIndex(insertIndex++);
            questionRoot.name = $"Question ({questionNumber})";

            ApplyQuestionText(questionRoot, questionNumber, questionText);
            ApplyChoices(questionRoot, choices);

            questionNumber++;
        }

        if (disableTemplateAfterBuild && !(useTemplateAsFirst && templateUsed))
        {
            questionTemplate.gameObject.SetActive(false);
        }
        else if (!templateWasActive)
        {
            questionTemplate.gameObject.SetActive(false);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(controlsParent as RectTransform);
    }

    public void ResetAllToggles()
    {
        if (controlsParent == null) return;
        var groups = controlsParent.GetComponentsInChildren<ToggleGroup>(true);
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i].SetAllTogglesOff(true);
        }
    }

    private void ClearGeneratedQuestions()
    {
        var markers = controlsParent.GetComponentsInChildren<GeneratedQuestionMarker>(true);
        for (int i = 0; i < markers.Length; i++)
        {
            if (markers[i] != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(markers[i].gameObject);
                }
                else
                {
                    DestroyImmediate(markers[i].gameObject);
                }
            }
        }
    }

    private static void ApplyQuestionText(Transform questionRoot, int number, string text)
    {
        var subtitle = FindChildByNamePrefix(questionRoot, "SubTitle");
        if (subtitle == null)
        {
            Debug.LogWarning("CsvQuestionnaireBuilder: SubTitle not found in question prefab.");
            return;
        }

        var tmp = subtitle.GetComponentInChildren<TMP_Text>(true);
        if (tmp != null)
        {
            tmp.text = $"{number}. {text}";
            return;
        }

        var uiText = subtitle.GetComponentInChildren<Text>(true);
        if (uiText != null)
        {
            uiText.text = $"{number}. {text}";
        }
    }

    private static void ApplyChoices(Transform questionRoot, List<string> choices)
    {
        var section = FindChildByNamePrefix(questionRoot, "Section");
        if (section == null)
        {
            Debug.LogWarning("CsvQuestionnaireBuilder: Section not found in question prefab.");
            return;
        }

        var group = section.GetComponent<ToggleGroup>();
        if (group != null)
        {
            group.allowSwitchOff = true;
        }

        List<Transform> toggleRoots = new List<Transform>();
        for (int i = 0; i < section.childCount; i++)
        {
            var child = section.GetChild(i);
            if (child.name.StartsWith("ToggleWithTextLabel", StringComparison.Ordinal))
            {
                toggleRoots.Add(child);
            }
        }

        if (toggleRoots.Count == 0)
        {
            Debug.LogWarning("CsvQuestionnaireBuilder: No ToggleWithTextLabel found under Section.");
            return;
        }

        Transform toggleTemplate = toggleRoots[0];

        while (toggleRoots.Count > choices.Count)
        {
            var extra = toggleRoots[toggleRoots.Count - 1];
            toggleRoots.RemoveAt(toggleRoots.Count - 1);
            if (Application.isPlaying)
            {
                Destroy(extra.gameObject);
            }
            else
            {
                DestroyImmediate(extra.gameObject);
            }
        }

        while (toggleRoots.Count < choices.Count)
        {
            var clone = UnityEngine.Object.Instantiate(toggleTemplate.gameObject, section);
            toggleRoots.Add(clone.transform);
        }

        for (int i = 0; i < toggleRoots.Count; i++)
        {
            var toggleRoot = toggleRoots[i];
            toggleRoot.name = $"ToggleWithTextLabel ({i + 1})";

            var textRoot = FindChildByNamePrefix(toggleRoot, "Text");
            var tmp = textRoot != null ? textRoot.GetComponentInChildren<TMP_Text>(true) : null;
            if (tmp == null)
            {
                tmp = toggleRoot.GetComponentInChildren<TMP_Text>(true);
            }

            if (tmp != null)
            {
                tmp.text = choices[i];
            }

            var toggle = toggleRoot.GetComponentInChildren<Toggle>(true);
            if (toggle != null)
            {
                if (group != null)
                {
                    toggle.group = group;
                }

                toggle.SetIsOnWithoutNotify(false);
            }
        }

        if (group != null)
        {
            group.SetAllTogglesOff(true);
        }
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

    private static List<List<string>> ReadCsv(string content)
    {
        List<List<string>> rows = new List<List<string>>();

        int i = 0;
        int len = content.Length;
        List<string> row = new List<string>();
        StringBuilder field = new StringBuilder();
        bool inQuotes = false;

        while (i < len)
        {
            char c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < len && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    inQuotes = false;
                    i++;
                    continue;
                }

                field.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Length = 0;
                i++;
                continue;
            }

            if (c == '\r')
            {
                i++;
                continue;
            }

            if (c == '\n')
            {
                row.Add(field.ToString());
                field.Length = 0;
                rows.Add(row);
                row = new List<string>();
                i++;
                continue;
            }

            field.Append(c);
            i++;
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static List<string> SplitChoices(string raw)
    {
        List<string> choices = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return choices;
        }

        var parts = raw.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            string choice = parts[i].Trim();
            if (!string.IsNullOrEmpty(choice))
            {
                choices.Add(choice);
            }
        }

        return choices;
    }

    private sealed class GeneratedQuestionMarker : MonoBehaviour
    {
    }
}
