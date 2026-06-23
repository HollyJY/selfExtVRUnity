using System.Collections;
using System.Text;
using UnityEngine;

public class TransformScaleWatcher : MonoBehaviour
{
    [Tooltip("Only log when scale changes after the initial lifecycle logs.")]
    public bool logOnlyOnChange = true;

    [Tooltip("Log every frame for this many frames after Start. Useful for startup-only bugs.")]
    public int verboseStartupFrames = 20;

    private Vector3 lastLocalScale;
    private Vector3 lastLossyScale;
    private int framesLogged;

    private void Awake()
    {
        Capture("Awake", force: true);
        Debug.Log($"[SCALE_WATCH] {GetPath(transform)} components={DescribeComponents()}");
    }

    private void OnEnable()
    {
        Capture("OnEnable", force: true);
    }

    private void Start()
    {
        Capture("Start", force: true);
        StartCoroutine(EndOfFrameWatch());
    }

    private void Update()
    {
        Capture("Update");
    }

    private void LateUpdate()
    {
        Capture("LateUpdate");
    }

    private IEnumerator EndOfFrameWatch()
    {
        while (enabled)
        {
            yield return new WaitForEndOfFrame();
            Capture("EndOfFrame");
        }
    }

    private void Capture(string phase, bool force = false)
    {
        Vector3 local = transform.localScale;
        Vector3 lossy = transform.lossyScale;
        bool changed = local != lastLocalScale || lossy != lastLossyScale;
        bool verbose = framesLogged < verboseStartupFrames;

        if (force || verbose || !logOnlyOnChange || changed)
        {
            Debug.Log(
                $"[SCALE_WATCH] phase={phase}; frame={Time.frameCount}; object={GetPath(transform)}; " +
                $"localScale={local}; lossyScale={lossy}; parent={DescribeParent(transform.parent)}");
            framesLogged++;
        }

        lastLocalScale = local;
        lastLossyScale = lossy;
    }

    private static string DescribeParent(Transform parent)
    {
        if (parent == null) return "<none>";
        return $"{GetPath(parent)} localScale={parent.localScale} lossyScale={parent.lossyScale}";
    }

    private string DescribeComponents()
    {
        Component[] components = GetComponents<Component>();
        var sb = new StringBuilder();
        for (int i = 0; i < components.Length; i++)
        {
            if (i > 0) sb.Append("|");
            sb.Append(components[i] == null ? "<missing>" : components[i].GetType().FullName);
        }

        return sb.ToString();
    }

    private static string GetPath(Transform t)
    {
        if (t == null) return "<null>";
        var sb = new StringBuilder(t.name);
        Transform parent = t.parent;
        while (parent != null)
        {
            sb.Insert(0, parent.name + "/");
            parent = parent.parent;
        }

        return sb.ToString();
    }
}
