using System;
using System.Reflection;
using UnityEngine;

public class RetargetingStatusWatcher : MonoBehaviour
{
    public float logIntervalSeconds = 1f;
    public bool logEveryFrameForFirstFrames = false;
    public int firstFrameCount = 10;

    private Component characterRetargeter;
    private Component sourceDataProvider;
    private float nextLogTime;

    private void Awake()
    {
        characterRetargeter = FindComponentByTypeName("CharacterRetargeter");
        sourceDataProvider = FindComponentByTypeName("MetaSourceDataProvider");
        LogStatus("Awake", force: true);
    }

    private void Start()
    {
        LogStatus("Start", force: true);
    }

    private void Update()
    {
        bool startupFrame = logEveryFrameForFirstFrames && Time.frameCount <= firstFrameCount;
        if (!startupFrame && Time.unscaledTime < nextLogTime)
        {
            return;
        }

        nextLogTime = Time.unscaledTime + Mathf.Max(0.1f, logIntervalSeconds);
        LogStatus("Update", force: startupFrame);
    }

    private void LateUpdate()
    {
        if (logEveryFrameForFirstFrames && Time.frameCount <= firstFrameCount)
        {
            LogStatus("LateUpdate", force: true);
        }
    }

    private void LogStatus(string phase, bool force = false)
    {
        string retargeterStatus = DescribeCharacterRetargeter(characterRetargeter);
        string providerStatus = DescribeSourceDataProvider(sourceDataProvider);
        Debug.Log(
            $"[RETARGET_WATCH] phase={phase}; frame={Time.frameCount}; object={GetPath(transform)}; " +
            $"retargeter={retargeterStatus}; sourceProvider={providerStatus}");
    }

    private string DescribeCharacterRetargeter(Component component)
    {
        if (component == null)
        {
            return "<CharacterRetargeter missing>";
        }

        Type type = component.GetType();
        object skeletonRetargeter = ReadMember(component, type, "SkeletonRetargeter");
        string skeletonStatus = DescribeSkeletonRetargeter(skeletonRetargeter);

        return
            $"type={type.FullName}, enabled={DescribeBehaviour(component)}, " +
            $"IsValid={ReadMemberString(component, type, "IsValid")}, " +
            $"RetargeterValid={ReadMemberString(component, type, "RetargeterValid")}, " +
            $"RetargetingHandle={ReadMemberString(component, type, "RetargetingHandle")}, " +
            $"DataProvider={DescribeUnityObject(ReadMember(component, type, "DataProvider"))}, " +
            $"SkeletonRetargeter=({skeletonStatus})";
    }

    private string DescribeSkeletonRetargeter(object skeletonRetargeter)
    {
        if (skeletonRetargeter == null)
        {
            return "<null>";
        }

        Type type = skeletonRetargeter.GetType();
        return
            $"IsInitialized={ReadMemberString(skeletonRetargeter, type, "IsInitialized")}, " +
            $"AppliedPose={ReadMemberString(skeletonRetargeter, type, "AppliedPose")}, " +
            $"ApplyRootScale={ReadMemberString(skeletonRetargeter, type, "ApplyRootScale")}, " +
            $"RootScale={ReadMemberString(skeletonRetargeter, type, "RootScale")}, " +
            $"HipsScale={ReadMemberString(skeletonRetargeter, type, "HipsScale")}, " +
            $"HeadScale={ReadMemberString(skeletonRetargeter, type, "HeadScale")}, " +
            $"ScaleRange={ReadMemberString(skeletonRetargeter, type, "ScaleRange")}, " +
            $"SourceJointCount={ReadMemberString(skeletonRetargeter, type, "SourceJointCount")}, " +
            $"TargetJointCount={ReadMemberString(skeletonRetargeter, type, "TargetJointCount")}";
    }

    private string DescribeSourceDataProvider(Component component)
    {
        if (component == null)
        {
            return "<MetaSourceDataProvider missing>";
        }

        Type type = component.GetType();
        return
            $"type={type.FullName}, enabled={DescribeBehaviour(component)}, " +
            $"IsPoseValid={InvokeString(component, type, "IsPoseValid")}, " +
            $"Manifestation={InvokeString(component, type, "GetManifestation")}, " +
            $"SkeletonPose={DescribePose(InvokeMember(component, type, "GetSkeletonPose"))}";
    }

    private string DescribePose(object pose)
    {
        if (pose == null)
        {
            return "<null>";
        }

        Type type = pose.GetType();
        return
            $"type={type.FullName}, " +
            $"JointCount={ReadMemberString(pose, type, "JointCount")}, " +
            $"Joints={ReadMemberString(pose, type, "Joints")}, " +
            $"IsDataValid={ReadMemberString(pose, type, "IsDataValid")}, " +
            $"RootPose={ReadMemberString(pose, type, "RootPose")}";
    }

    private Component FindComponentByTypeName(string typeName)
    {
        Component[] components = GetComponents<Component>();
        for (int i = 0; i < components.Length; i++)
        {
            Component component = components[i];
            if (component == null) continue;
            Type type = component.GetType();
            if (type.Name == typeName || type.FullName.EndsWith("." + typeName, StringComparison.Ordinal))
            {
                return component;
            }
        }

        return null;
    }

    private static string DescribeBehaviour(Component component)
    {
        if (component is Behaviour behaviour)
        {
            return $"enabled={behaviour.enabled}, activeAndEnabled={behaviour.isActiveAndEnabled}";
        }

        return "notBehaviour";
    }

    private static string ReadMemberString(object instance, Type type, string memberName)
    {
        object value = ReadMember(instance, type, memberName);
        return FormatValue(value);
    }

    private static object ReadMember(object instance, Type type, string memberName)
    {
        if (instance == null || type == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo property = type.GetProperty(memberName, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(instance);
            }
            catch (Exception e)
            {
                return $"<property error: {e.Message}>";
            }
        }

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(instance);
            }
            catch (Exception e)
            {
                return $"<field error: {e.Message}>";
            }
        }

        return "<missing>";
    }

    private static string InvokeString(object instance, Type type, string methodName)
    {
        object value = InvokeMember(instance, type, methodName);
        return FormatValue(value);
    }

    private static object InvokeMember(object instance, Type type, string methodName)
    {
        if (instance == null || type == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        MethodInfo method = type.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
        if (method == null)
        {
            return "<missing>";
        }

        try
        {
            return method.Invoke(instance, null);
        }
        catch (Exception e)
        {
            return $"<method error: {e.InnerException?.Message ?? e.Message}>";
        }
    }

    private static string FormatValue(object value)
    {
        if (value == null) return "<null>";
        if (value is UnityEngine.Object unityObject)
        {
            return DescribeUnityObject(unityObject);
        }

        return value.ToString();
    }

    private static string DescribeUnityObject(object value)
    {
        if (value == null) return "<null>";
        if (value is UnityEngine.Object unityObject)
        {
            return unityObject == null ? "<null>" : $"{unityObject.GetType().Name}:{unityObject.name}";
        }

        return value.ToString();
    }

    private static string GetPath(Transform t)
    {
        if (t == null) return "<null>";
        string path = t.name;
        Transform parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
