using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class EyeGazeCsvLogger : MonoBehaviour
{
    [Header("Gaze Source")]
    [Tooltip("Use a transform that points along the current eye gaze direction, e.g. [BuildingBlock] Eye Gaze Left/Right")]
    [SerializeField] private Transform gazeOrigin;

    [Header("Raycast")]
    [SerializeField] private float sampleInterval = 0.1f;
    [SerializeField] private float maxDistance = 20f;
    [SerializeField] private LayerMask targetLayers = ~0;

    [Header("Output")]
    [SerializeField] private bool writeSampleRows = true;
    [SerializeField] private bool writeEnterExitRows = true;
    [SerializeField] private string outputSubFolder = "logs";
    [SerializeField] private string filePrefix = "eye_gaze";

    [Header("Debug")]
    [SerializeField] private bool debugDrawRay = false;

    private float nextSampleTime;
    private float sessionT0;
    private string outputPath;
    private StringBuilder buffer;
    private EyeGazeTarget currentTarget;

    private const int FlushThresholdChars = 4096;

    private void Start()
    {
        sessionT0 = Time.realtimeSinceStartup;
        nextSampleTime = sessionT0;
        buffer = new StringBuilder(8192);

        SetupOutputPath();
        WriteHeaderIfNeeded();
    }

    private void Update()
    {
        if (gazeOrigin == null) return;
        if (Time.realtimeSinceStartup < nextSampleTime) return;

        SampleAndLog();
        nextSampleTime += sampleInterval;

        if (buffer.Length >= FlushThresholdChars)
        {
            FlushBuffer();
        }
    }

    private void OnApplicationQuit()
    {
        FlushBuffer();
    }

    private void OnDisable()
    {
        FlushBuffer();
    }

    private void SampleAndLog()
    {
        Vector3 origin = gazeOrigin.position;
        Vector3 direction = gazeOrigin.forward;

        if (debugDrawRay)
        {
            Debug.DrawRay(origin, direction * maxDistance, Color.cyan, sampleInterval);
        }

        bool hitAny = Physics.Raycast(
            origin,
            direction,
            out RaycastHit hit,
            maxDistance,
            targetLayers,
            QueryTriggerInteraction.Collide);

        EyeGazeTarget hitTarget = null;
        string hitTargetId = "";
        string hitTargetGroup = "";

        if (hitAny)
        {
            hitTarget = hit.collider.GetComponentInParent<EyeGazeTarget>();
            if (hitTarget != null)
            {
                hitTargetId = hitTarget.GetResolvedTargetId();
                hitTargetGroup = hitTarget.targetGroup;
            }
        }

        float currentDistance = hitAny ? hit.distance : -1f;
        Vector3 currentHitPoint = hitAny ? hit.point : Vector3.zero;
        Vector3 currentHitNormal = hitAny ? hit.normal : Vector3.zero;

        if (writeEnterExitRows && hitTarget != currentTarget)
        {
            if (currentTarget != null)
            {
                string oldId = currentTarget.GetResolvedTargetId();
                string oldGroup = currentTarget.targetGroup;
                AppendRow("exit", oldId, oldGroup, -1f, origin, direction, Vector3.zero, Vector3.zero);
                if (GameFlowManager.Instance != null)
                {
                    GameFlowManager.Instance.LogEvent("eye_gaze_exit", $"target={oldId};group={oldGroup}");
                }
            }

            if (hitTarget != null)
            {
                AppendRow("enter", hitTargetId, hitTargetGroup, currentDistance, origin, direction, currentHitPoint, currentHitNormal);
                if (GameFlowManager.Instance != null)
                {
                    GameFlowManager.Instance.LogEvent("eye_gaze_enter", $"target={hitTargetId};group={hitTargetGroup}");
                }
            }

            currentTarget = hitTarget;
        }

        if (writeSampleRows)
        {
            AppendRow("sample", hitTargetId, hitTargetGroup, currentDistance, origin, direction, currentHitPoint, currentHitNormal);
        }
    }

    private void SetupOutputPath()
    {
        string participantId = "no_id";
        if (GameFlowManager.Instance != null && !string.IsNullOrWhiteSpace(GameFlowManager.Instance.participantId))
        {
            participantId = GameFlowManager.Instance.participantId;
        }

        string dir = Path.Combine(Application.persistentDataPath, outputSubFolder);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{participantId}_{filePrefix}.csv";
        outputPath = Path.Combine(dir, fileName);
        Debug.Log($"EyeGazeCsvLogger: writing to {outputPath}");
    }

    private void WriteHeaderIfNeeded()
    {
        if (File.Exists(outputPath)) return;

        const string header =
            "iso_time,realtime_s,scene,event,target_id,target_group,distance_m," +
            "gaze_origin_x,gaze_origin_y,gaze_origin_z," +
            "gaze_dir_x,gaze_dir_y,gaze_dir_z," +
            "hit_x,hit_y,hit_z,normal_x,normal_y,normal_z\n";

        File.WriteAllText(outputPath, header);
    }

    private void AppendRow(
        string evt,
        string targetId,
        string targetGroup,
        float distance,
        Vector3 gazePos,
        Vector3 gazeDir,
        Vector3 hitPoint,
        Vector3 hitNormal)
    {
        string iso = DateTime.UtcNow.ToString("o");
        float rt = Time.realtimeSinceStartup - sessionT0;
        string scene = SceneManager.GetActiveScene().name;

        buffer.Append(iso).Append(',')
              .Append(rt.ToString("F3")).Append(',')
              .Append(Escape(scene)).Append(',')
              .Append(Escape(evt)).Append(',')
              .Append(Escape(targetId)).Append(',')
              .Append(Escape(targetGroup)).Append(',')
              .Append(distance.ToString("F4")).Append(',')
              .Append(gazePos.x.ToString("F5")).Append(',')
              .Append(gazePos.y.ToString("F5")).Append(',')
              .Append(gazePos.z.ToString("F5")).Append(',')
              .Append(gazeDir.x.ToString("F5")).Append(',')
              .Append(gazeDir.y.ToString("F5")).Append(',')
              .Append(gazeDir.z.ToString("F5")).Append(',')
              .Append(hitPoint.x.ToString("F5")).Append(',')
              .Append(hitPoint.y.ToString("F5")).Append(',')
              .Append(hitPoint.z.ToString("F5")).Append(',')
              .Append(hitNormal.x.ToString("F5")).Append(',')
              .Append(hitNormal.y.ToString("F5")).Append(',')
              .Append(hitNormal.z.ToString("F5")).Append('\n');
    }

    private void FlushBuffer()
    {
        if (buffer == null || buffer.Length == 0) return;
        if (string.IsNullOrEmpty(outputPath)) return;

        File.AppendAllText(outputPath, buffer.ToString());
        buffer.Length = 0;
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
