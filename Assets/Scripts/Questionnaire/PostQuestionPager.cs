using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class PostQuestionPager : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private RectTransform viewport;
    [SerializeField] private RectTransform questionsRoot;
    [SerializeField] private Toggle nextPageToggle;
    [SerializeField] private Toggle finishToggle;

    [Header("Layout")]
    [SerializeField] private float extraTopPadding = 0f;
    [SerializeField] private float extraBottomPadding = 0f;
    [Tooltip("If > 0, caps the number of questions per page regardless of height.")]
    [SerializeField] private int maxQuestionsPerPage = 0;
    [Header("Debug")]
    [SerializeField] private bool logPagingDetails = false;

    private readonly List<List<GameObject>> pages = new List<List<GameObject>>();
    private int currentPageIndex;

    public void BindButtons(Toggle nextPage, Toggle finish)
    {
        if (nextPageToggle == null)
        {
            nextPageToggle = nextPage;
        }
        if (finishToggle == null)
        {
            finishToggle = finish;
        }
    }

    public void RefreshAndShowFirstPage()
    {
        BuildPages();
        ShowPage(0);
    }

    public void GoToNextPage()
    {
        ShowPage(currentPageIndex + 1);
    }

    private void BuildPages()
    {
        pages.Clear();

        if (questionsRoot == null || viewport == null)
        {
            if (logPagingDetails)
            {
                Debug.LogWarning("PostQuestionPager: Missing questionsRoot or viewport.");
            }
            return;
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(questionsRoot);

        float maxHeight = viewport.rect.height - extraTopPadding - extraBottomPadding;
        if (maxHeight <= 0f)
        {
            maxHeight = viewport.rect.height;
        }

        var candidates = CollectQuestionItems();
        if (logPagingDetails)
        {
            Debug.Log($"PostQuestionPager: Candidates={candidates.Count} root={questionsRoot.name} viewportH={viewport.rect.height:F1} maxPerPage={maxQuestionsPerPage}");
            for (int i = 0; i < candidates.Count; i++)
            {
                Debug.Log($"PostQuestionPager: Candidate[{i}]={GetPath(candidates[i])}");
            }
        }
        var layout = questionsRoot.GetComponent<VerticalLayoutGroup>();
        float spacing = layout != null ? layout.spacing : 0f;

        var currentPage = new List<GameObject>();
        float currentHeight = 0f;

        for (int i = 0; i < candidates.Count; i++)
        {
            var item = candidates[i];
            float preferred = GetPreferredHeight(item);
            float addSpacing = currentPage.Count > 0 ? spacing : 0f;

            if (maxQuestionsPerPage > 0 && currentPage.Count >= maxQuestionsPerPage)
            {
                pages.Add(currentPage);
                currentPage = new List<GameObject>();
                currentHeight = 0f;
                addSpacing = 0f;
            }

            if (currentPage.Count > 0 && currentHeight + addSpacing + preferred > maxHeight)
            {
                pages.Add(currentPage);
                currentPage = new List<GameObject>();
                currentHeight = 0f;
                addSpacing = 0f;
            }

            currentHeight += addSpacing + preferred;
            currentPage.Add(item.gameObject);
        }

        if (currentPage.Count > 0)
        {
            pages.Add(currentPage);
        }

        if (pages.Count == 0)
        {
            pages.Add(new List<GameObject>());
        }

        if (logPagingDetails)
        {
            for (int p = 0; p < pages.Count; p++)
            {
                string names = string.Join(", ", pages[p].ConvertAll(go => go != null ? go.name : "<null>"));
                Debug.Log($"PostQuestionPager: Page[{p}] count={pages[p].Count} items={names}");
            }
        }
    }

    private void ShowPage(int index)
    {
        if (pages.Count == 0)
        {
            return;
        }

        if (index < 0) index = 0;
        if (index >= pages.Count) index = pages.Count - 1;
        currentPageIndex = index;

        var all = CollectQuestionItems();
        for (int i = 0; i < all.Count; i++)
        {
            all[i].gameObject.SetActive(false);
        }

        var page = pages[currentPageIndex];
        for (int i = 0; i < page.Count; i++)
        {
            page[i].SetActive(true);
        }

        bool isLast = currentPageIndex == pages.Count - 1;
        SetActiveSafe(nextPageToggle != null ? nextPageToggle.gameObject : null, !isLast);
        SetActiveSafe(finishToggle != null ? finishToggle.gameObject : null, isLast);

        if (logPagingDetails)
        {
            Debug.Log($"PostQuestionPager: ShowPage index={currentPageIndex} isLast={isLast} pages={pages.Count}");
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(questionsRoot);
    }

    private List<RectTransform> CollectQuestionItems()
    {
        var results = new List<RectTransform>();
        if (questionsRoot == null) return results;

        for (int i = 0; i < questionsRoot.childCount; i++)
        {
            var child = questionsRoot.GetChild(i) as RectTransform;
            if (child == null) continue;
            if (!HasQuestionMarker(child)) continue;
            results.Add(child);
        }

        return results;
    }

    private static bool HasQuestionMarker(Transform root)
    {
        return root.GetComponent<QuestionItemMarker>() != null;
    }

    private static float GetPreferredHeight(RectTransform rect)
    {
        if (rect == null) return 0f;
        float preferred = LayoutUtility.GetPreferredHeight(rect);
        if (preferred <= 0f)
        {
            preferred = rect.rect.height;
        }
        return preferred;
    }

    private static void SetActiveSafe(GameObject obj, bool active)
    {
        if (obj != null)
        {
            obj.SetActive(active);
        }
    }

    private static string GetPath(Transform t)
    {
        if (t == null) return "<null>";
        var parts = new List<string>();
        var current = t;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
