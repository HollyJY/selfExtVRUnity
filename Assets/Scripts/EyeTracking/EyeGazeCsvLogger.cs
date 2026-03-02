using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class EyeGazeCsvLogger : MonoBehaviour
{
    [Header("Gaze Sources")]
    [Tooltip("Combined gaze transform (preferred for online hit check)")]
    [SerializeField] private Transform combinedGazeOrigin;
    [Tooltip("Left eye gaze transform")]
    [SerializeField] private Transform leftGazeOrigin;
    [Tooltip("Right eye gaze transform")]
    [SerializeField] private Transform rightGazeOrigin;
    [Tooltip("If true, online hit uses combined gaze first. Otherwise left then right")]
    [SerializeField] private bool preferCombinedForHit = true;

    [Header("Pose Sources")]
    [Tooltip("HMD center eye/camera transform")]
    [SerializeField] private Transform hmdTransform;
    [Tooltip("XR Origin / TrackingSpace transform")]
    [SerializeField] private Transform xrOriginTransform;

    [Header("Experiment Markers")]
    [SerializeField] private TrialsController trialsController;

    [Header("Raycast (Debug Channel)")]
    [SerializeField] private float sampleInterval = 0.1f;
    [SerializeField] private float maxDistance = 20f;
    [SerializeField] private LayerMask targetLayers = ~0;

    [Header("Output")]
    [SerializeField] private bool writeSampleRows = true;
    [SerializeField] private bool writeEnterExitRows = true;
    [SerializeField] private bool writeRawRows = true;
    [SerializeField] private string audioRootFolder = "Audio";
    [SerializeField] private string sessionIdOverride = "";

    [Header("Debug")]
    [SerializeField] private bool debugDrawRay = false;

    private float nextSampleTime;
    private bool useGameFlowTimeBase;
    private string resolvedSessionId;
    private float localT0;

    private string outputPath;
    private string rawOutputPath;

    private StringBuilder buffer;
    private StringBuilder rawBuffer;

    private EyeGazeTarget currentTarget;

    private readonly Dictionary<Transform, Component> eyeGazeComponentCache = new Dictionary<Transform, Component>();

    private const int FlushThresholdChars = 4096;

    private void Start()
    {
        ResolveSessionContext();
        AutoResolveReferences();

        localT0 = Time.realtimeSinceStartup;
        nextSampleTime = Time.realtimeSinceStartup;

        buffer = new StringBuilder(8192);
        rawBuffer = new StringBuilder(16384);

        SetupOutputPath();
        WriteHeaderIfNeeded();
        WriteRawHeaderIfNeeded();

        AppendRawMarker("logger_start", "eye logger started");
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextSampleTime) return;

        SampleAndLog();
        nextSampleTime += sampleInterval;

        if (buffer.Length >= FlushThresholdChars)
        {
            FlushBuffer();
        }

        if (rawBuffer.Length >= FlushThresholdChars)
        {
            FlushRawBuffer();
        }
    }

    private void OnApplicationQuit()
    {
        AppendRawMarker("logger_stop", "application quit");
        FlushBuffer();
        FlushRawBuffer();
    }

    private void OnDisable()
    {
        AppendRawMarker("logger_stop", "component disabled");
        FlushBuffer();
        FlushRawBuffer();
    }

    private void SampleAndLog()
    {
        if (TryResolveHitRay(out Vector3 hitRayOrigin, out Vector3 hitRayDirection))
        {
            if (debugDrawRay)
            {
                Debug.DrawRay(hitRayOrigin, hitRayDirection * maxDistance, Color.cyan, sampleInterval);
            }

            bool hitAny = Physics.Raycast(
                hitRayOrigin,
                hitRayDirection,
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
                    AppendRow("exit", oldId, oldGroup, -1f, hitRayOrigin, hitRayDirection, Vector3.zero, Vector3.zero);
                    AppendRawMarker("eye_gaze_exit", "target=" + oldId + ";group=" + oldGroup);
                    if (GameFlowManager.Instance != null)
                    {
                        GameFlowManager.Instance.LogEvent("eye_gaze_exit", $"target={oldId};group={oldGroup}");
                    }
                }

                if (hitTarget != null)
                {
                    AppendRow("enter", hitTargetId, hitTargetGroup, currentDistance, hitRayOrigin, hitRayDirection, currentHitPoint, currentHitNormal);
                    AppendRawMarker("eye_gaze_enter", "target=" + hitTargetId + ";group=" + hitTargetGroup);
                    if (GameFlowManager.Instance != null)
                    {
                        GameFlowManager.Instance.LogEvent("eye_gaze_enter", $"target={hitTargetId};group={hitTargetGroup}");
                    }
                }

                currentTarget = hitTarget;
            }

            if (writeSampleRows)
            {
                AppendRow("sample", hitTargetId, hitTargetGroup, currentDistance, hitRayOrigin, hitRayDirection, currentHitPoint, currentHitNormal);
            }
        }

        if (writeRawRows)
        {
            AppendRawSampleRow();
        }
    }

    private bool TryResolveHitRay(out Vector3 origin, out Vector3 direction)
    {
        Transform primary = preferCombinedForHit ? combinedGazeOrigin : leftGazeOrigin;
        Transform secondary = preferCombinedForHit ? leftGazeOrigin : combinedGazeOrigin;

        if (TryGetRayFromTransform(primary, out origin, out direction)) return true;
        if (TryGetRayFromTransform(secondary, out origin, out direction)) return true;
        if (TryGetRayFromTransform(rightGazeOrigin, out origin, out direction)) return true;

        origin = Vector3.zero;
        direction = Vector3.forward;
        return false;
    }

    private static bool TryGetRayFromTransform(Transform t, out Vector3 origin, out Vector3 direction)
    {
        if (t != null)
        {
            origin = t.position;
            direction = t.forward;
            return true;
        }

        origin = Vector3.zero;
        direction = Vector3.forward;
        return false;
    }

    private void SetupOutputPath()
    {
#if UNITY_EDITOR
        string baseDir = Application.streamingAssetsPath;
#else
        string baseDir = Application.persistentDataPath;
#endif
        string dir = Path.Combine(baseDir, audioRootFolder, resolvedSessionId + "_session");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string fileName = $"{DateTime.UtcNow:yyyyMMdd}_{resolvedSessionId}_log_eye.csv";
        outputPath = Path.Combine(dir, fileName);
        string rawFileName = $"{DateTime.UtcNow:yyyyMMdd}_{resolvedSessionId}_log_eye_raw.csv";
        rawOutputPath = Path.Combine(dir, rawFileName);

        Debug.Log($"EyeGazeCsvLogger: writing to {outputPath}");
        Debug.Log($"EyeGazeCsvLogger: writing raw to {rawOutputPath}");
    }

    private void WriteHeaderIfNeeded()
    {
        if (File.Exists(outputPath)) return;

        const string header =
            "iso_time,realtime_s,scene,event,target_id,target_group,distance_m," +
            "gaze_origin_x,gaze_origin_y,gaze_origin_z," +
            "gaze_dir_x,gaze_dir_y,gaze_dir_z," +
            "hit_x,hit_y,hit_z,normal_x,normal_y,normal_z," +
            "trial_id,condition,phase,marker_detail\n";

        File.WriteAllText(outputPath, header);
    }

    private void WriteRawHeaderIfNeeded()
    {
        if (!writeRawRows) return;
        if (File.Exists(rawOutputPath)) return;

        const string header =
            "iso_time,realtime_s,scene,row_type,marker_event,marker_detail,trial_id,condition,phase," +
            "combined_valid,combined_conf,left_valid,left_conf,right_valid,right_conf," +
            "combined_origin_x,combined_origin_y,combined_origin_z,combined_dir_x,combined_dir_y,combined_dir_z," +
            "left_origin_x,left_origin_y,left_origin_z,left_dir_x,left_dir_y,left_dir_z," +
            "right_origin_x,right_origin_y,right_origin_z,right_dir_x,right_dir_y,right_dir_z," +
            "hmd_pos_x,hmd_pos_y,hmd_pos_z,hmd_rot_x,hmd_rot_y,hmd_rot_z,hmd_rot_w," +
            "xr_pos_x,xr_pos_y,xr_pos_z,xr_rot_x,xr_rot_y,xr_rot_z,xr_rot_w\n";

        File.WriteAllText(rawOutputPath, header);
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
        float rt = GetRealtimeSeconds();
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
              .Append(hitNormal.z.ToString("F5")).Append(',')
              .Append(',')
              .Append(',')
              .Append(',')
              .Append('\n');
    }

    private void AppendMarkerRow(string markerEvent, string markerDetail)
    {
        string iso = DateTime.UtcNow.ToString("o");
        float rt = GetRealtimeSeconds();
        string scene = SceneManager.GetActiveScene().name;
        GetExperimentMarkers(out string trialId, out string condition, out string phase);

        buffer.Append(iso).Append(',')
              .Append(rt.ToString("F3")).Append(',')
              .Append(Escape(scene)).Append(',')
              .Append(Escape(markerEvent)).Append(',')
              .Append(',')
              .Append(',')
              .Append("-1.0000,")
              .Append("0.00000,0.00000,0.00000,")
              .Append("0.00000,0.00000,0.00000,")
              .Append("0.00000,0.00000,0.00000,")
              .Append("0.00000,0.00000,0.00000,")
              .Append(Escape(trialId)).Append(',')
              .Append(Escape(condition)).Append(',')
              .Append(Escape(phase)).Append(',')
              .Append(Escape(markerDetail)).Append('\n');
    }

    private void AppendRawSampleRow()
    {
        GetRayPose(combinedGazeOrigin, out Vector3 cPos, out Vector3 cDir);
        GetRayPose(leftGazeOrigin, out Vector3 lPos, out Vector3 lDir);
        GetRayPose(rightGazeOrigin, out Vector3 rPos, out Vector3 rDir);

        bool cValid = combinedGazeOrigin != null;
        bool lValid = leftGazeOrigin != null;
        bool rValid = rightGazeOrigin != null;

        float cConf = -1f;
        float lConf = -1f;
        float rConf = -1f;

        TryReadEyeStatus(combinedGazeOrigin, ref cValid, ref cConf);
        TryReadEyeStatus(leftGazeOrigin, ref lValid, ref lConf);
        TryReadEyeStatus(rightGazeOrigin, ref rValid, ref rConf);

        GetPose(hmdTransform, out Vector3 hmdPos, out Quaternion hmdRot);
        GetPose(xrOriginTransform, out Vector3 xrPos, out Quaternion xrRot);

        GetExperimentMarkers(out string trialId, out string condition, out string phase);

        string iso = DateTime.UtcNow.ToString("o");
        float rt = GetRealtimeSeconds();
        string scene = SceneManager.GetActiveScene().name;

        rawBuffer.Append(iso).Append(',')
                 .Append(rt.ToString("F3")).Append(',')
                 .Append(Escape(scene)).Append(',')
                 .Append("sample").Append(',')
                 .Append(',')
                 .Append(',')
                 .Append(Escape(trialId)).Append(',')
                 .Append(Escape(condition)).Append(',')
                 .Append(Escape(phase)).Append(',')
                 .Append(Bool01(cValid)).Append(',')
                 .Append(FloatOrNaN(cConf)).Append(',')
                 .Append(Bool01(lValid)).Append(',')
                 .Append(FloatOrNaN(lConf)).Append(',')
                 .Append(Bool01(rValid)).Append(',')
                 .Append(FloatOrNaN(rConf)).Append(',')
                 .Append(Vec3(cPos)).Append(',')
                 .Append(Vec3(cDir)).Append(',')
                 .Append(Vec3(lPos)).Append(',')
                 .Append(Vec3(lDir)).Append(',')
                 .Append(Vec3(rPos)).Append(',')
                 .Append(Vec3(rDir)).Append(',')
                 .Append(Vec3(hmdPos)).Append(',')
                 .Append(Quat(hmdRot)).Append(',')
                 .Append(Vec3(xrPos)).Append(',')
                 .Append(Quat(xrRot)).Append('\n');
    }

    private void AppendRawMarker(string markerEvent, string markerDetail)
    {
        AppendMarkerRow(markerEvent, markerDetail);
        if (!writeRawRows || string.IsNullOrEmpty(rawOutputPath)) return;

        GetRayPose(combinedGazeOrigin, out Vector3 cPos, out Vector3 cDir);
        GetRayPose(leftGazeOrigin, out Vector3 lPos, out Vector3 lDir);
        GetRayPose(rightGazeOrigin, out Vector3 rPos, out Vector3 rDir);

        bool cValid = combinedGazeOrigin != null;
        bool lValid = leftGazeOrigin != null;
        bool rValid = rightGazeOrigin != null;

        float cConf = -1f;
        float lConf = -1f;
        float rConf = -1f;

        TryReadEyeStatus(combinedGazeOrigin, ref cValid, ref cConf);
        TryReadEyeStatus(leftGazeOrigin, ref lValid, ref lConf);
        TryReadEyeStatus(rightGazeOrigin, ref rValid, ref rConf);

        GetPose(hmdTransform, out Vector3 hmdPos, out Quaternion hmdRot);
        GetPose(xrOriginTransform, out Vector3 xrPos, out Quaternion xrRot);

        GetExperimentMarkers(out string trialId, out string condition, out string phase);

        string iso = DateTime.UtcNow.ToString("o");
        float rt = GetRealtimeSeconds();
        string scene = SceneManager.GetActiveScene().name;

        rawBuffer.Append(iso).Append(',')
                 .Append(rt.ToString("F3")).Append(',')
                 .Append(Escape(scene)).Append(',')
                 .Append("marker").Append(',')
                 .Append(Escape(markerEvent)).Append(',')
                 .Append(Escape(markerDetail)).Append(',')
                 .Append(Escape(trialId)).Append(',')
                 .Append(Escape(condition)).Append(',')
                 .Append(Escape(phase)).Append(',')
                 .Append(Bool01(cValid)).Append(',')
                 .Append(FloatOrNaN(cConf)).Append(',')
                 .Append(Bool01(lValid)).Append(',')
                 .Append(FloatOrNaN(lConf)).Append(',')
                 .Append(Bool01(rValid)).Append(',')
                 .Append(FloatOrNaN(rConf)).Append(',')
                 .Append(Vec3(cPos)).Append(',')
                 .Append(Vec3(cDir)).Append(',')
                 .Append(Vec3(lPos)).Append(',')
                 .Append(Vec3(lDir)).Append(',')
                 .Append(Vec3(rPos)).Append(',')
                 .Append(Vec3(rDir)).Append(',')
                 .Append(Vec3(hmdPos)).Append(',')
                 .Append(Quat(hmdRot)).Append(',')
                 .Append(Vec3(xrPos)).Append(',')
                 .Append(Quat(xrRot)).Append('\n');
    }

    private static void GetRayPose(Transform t, out Vector3 pos, out Vector3 dir)
    {
        if (t != null)
        {
            pos = t.position;
            dir = t.forward;
            return;
        }

        pos = Vector3.zero;
        dir = Vector3.zero;
    }

    private static void GetPose(Transform t, out Vector3 pos, out Quaternion rot)
    {
        if (t != null)
        {
            pos = t.position;
            rot = t.rotation;
            return;
        }

        pos = Vector3.zero;
        rot = Quaternion.identity;
    }

    private void GetExperimentMarkers(out string trialId, out string condition, out string phase)
    {
        trialId = "";
        condition = "";
        phase = "";

        if (trialsController == null) return;

        int t = trialsController.GetCurrentTrialIndexForLogging();
        int c = trialsController.GetCurrentConditionForLogging();
        string p = trialsController.GetCurrentPhaseForLogging();

        if (t > 0) trialId = t.ToString();
        if (c > 0) condition = c.ToString();
        phase = p ?? "";
    }

    private void TryReadEyeStatus(Transform gazeTransform, ref bool valid, ref float confidence)
    {
        if (gazeTransform == null) return;

        Component comp = GetOVREyeGazeComponent(gazeTransform);
        if (comp == null) return;

        bool boolRead = false;
        if (TryReadBool(comp, "IsValid", out bool v1))
        {
            valid = v1;
            boolRead = true;
        }
        else if (TryReadBool(comp, "Valid", out bool v2))
        {
            valid = v2;
            boolRead = true;
        }
        else if (TryReadBool(comp, "IsDataValid", out bool v3))
        {
            valid = v3;
            boolRead = true;
        }
        else if (TryReadBool(comp, "EyeTrackingEnabled", out bool v4))
        {
            valid = v4;
            boolRead = true;
        }

        if (!boolRead)
        {
            valid = gazeTransform != null;
        }

        if (TryReadFloat(comp, "Confidence", out float c1))
        {
            confidence = c1;
        }
        else if (TryReadFloat(comp, "confidence", out float c2))
        {
            confidence = c2;
        }
    }

    private Component GetOVREyeGazeComponent(Transform t)
    {
        if (t == null) return null;

        if (eyeGazeComponentCache.TryGetValue(t, out Component cached))
        {
            return cached;
        }

        Component[] components = t.GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component comp = components[i];
            if (comp == null) continue;
            if (comp.GetType().Name == "OVREyeGaze")
            {
                eyeGazeComponentCache[t] = comp;
                return comp;
            }
        }

        eyeGazeComponentCache[t] = null;
        return null;
    }

    private static bool TryReadBool(Component comp, string memberName, out bool value)
    {
        value = false;
        if (comp == null) return false;

        Type type = comp.GetType();
        PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.PropertyType == typeof(bool) && prop.CanRead)
        {
            object raw = prop.GetValue(comp, null);
            if (raw is bool b)
            {
                value = b;
                return true;
            }
        }

        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field != null && field.FieldType == typeof(bool))
        {
            object raw = field.GetValue(comp);
            if (raw is bool b)
            {
                value = b;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFloat(Component comp, string memberName, out float value)
    {
        value = -1f;
        if (comp == null) return false;

        Type type = comp.GetType();
        PropertyInfo prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (prop != null && prop.CanRead)
        {
            object raw = prop.GetValue(comp, null);
            if (raw is float f)
            {
                value = f;
                return true;
            }
            if (raw is double d)
            {
                value = (float)d;
                return true;
            }
        }

        FieldInfo field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (field != null)
        {
            object raw = field.GetValue(comp);
            if (raw is float f)
            {
                value = f;
                return true;
            }
            if (raw is double d)
            {
                value = (float)d;
                return true;
            }
        }

        return false;
    }

    private static string Bool01(bool value)
    {
        return value ? "1" : "0";
    }

    private static string FloatOrNaN(float value)
    {
        return value >= 0f ? value.ToString("F4") : "";
    }

    private static string Vec3(Vector3 v)
    {
        return v.x.ToString("F5") + "," + v.y.ToString("F5") + "," + v.z.ToString("F5");
    }

    private static string Quat(Quaternion q)
    {
        return q.x.ToString("F6") + "," + q.y.ToString("F6") + "," + q.z.ToString("F6") + "," + q.w.ToString("F6");
    }

    private void FlushBuffer()
    {
        if (buffer == null || buffer.Length == 0) return;
        if (string.IsNullOrEmpty(outputPath)) return;

        File.AppendAllText(outputPath, buffer.ToString());
        buffer.Length = 0;
    }

    private void FlushRawBuffer()
    {
        if (!writeRawRows) return;
        if (rawBuffer == null || rawBuffer.Length == 0) return;
        if (string.IsNullOrEmpty(rawOutputPath)) return;

        File.AppendAllText(rawOutputPath, rawBuffer.ToString());
        rawBuffer.Length = 0;
    }

    private void ResolveSessionContext()
    {
        useGameFlowTimeBase = GameFlowManager.Instance != null;
        resolvedSessionId = ResolveSessionId();
    }

    private void AutoResolveReferences()
    {
        if (trialsController == null)
        {
            trialsController = FindObjectOfType<TrialsController>();
        }
    }

    private string ResolveSessionId()
    {
        if (!string.IsNullOrWhiteSpace(sessionIdOverride))
        {
            return StripSessionSuffix(sessionIdOverride.Trim());
        }

        if (GameFlowManager.Instance != null)
        {
            return GameFlowManager.Instance.GetSessionIdForPaths();
        }

        return "no_id";
    }

    private float GetRealtimeSeconds()
    {
        if (useGameFlowTimeBase && GameFlowManager.Instance != null)
        {
            return GameFlowManager.Instance.GetRealtimeSinceSessionStart();
        }

        return Time.realtimeSinceStartup - localT0;
    }

    private static string StripSessionSuffix(string value)
    {
        const string suffix = "_session";
        if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return value.Substring(0, value.Length - suffix.Length);
        }

        return value;
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
