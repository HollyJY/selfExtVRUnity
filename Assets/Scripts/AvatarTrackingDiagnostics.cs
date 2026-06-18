using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

public static class AvatarTrackingDiagnostics
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly string[] InterestingTypeTokens =
    {
        "OVR",
        "OpenXR",
        "Tracking",
        "Tracked",
        "Skeleton",
        "Retarget",
        "Body",
        "Face",
        "Eye",
        "Mirror"
    };

    private static readonly string[] InterestingMemberTokens =
    {
        "Valid",
        "Tracked",
        "Tracking",
        "Initialized",
        "Enabled",
        "Confidence",
        "Skeleton",
        "Body",
        "Permission",
        "Provider",
        "Source",
        "Target"
    };

    public static string DescribeGlobalTracking()
    {
        var sb = new StringBuilder();
        sb.Append("xr=");
        sb.Append(GetXrStatus());
        sb.Append("; ovr=");
        sb.Append(GetOvrInputStatus());
        sb.Append("; ovrPlugin=");
        sb.Append(GetOvrPluginStatus());
        return sb.ToString();
    }

    public static string DescribeAvatarTracking(GameObject avatar)
    {
        if (avatar == null) return "tracking=<avatar null>";

        Component[] components = avatar.GetComponentsInChildren<Component>(true);
        int interestingCount = 0;
        var sb = new StringBuilder();

        foreach (Component component in components)
        {
            if (component == null) continue;

            Type type = component.GetType();
            if (!IsInterestingType(type)) continue;

            if (interestingCount > 0) sb.Append(" | ");
            interestingCount++;

            sb.Append(type.Name);
            sb.Append("(go=");
            sb.Append(component.gameObject.name);
            sb.Append(", active=");
            sb.Append(component.gameObject.activeInHierarchy);

            if (component is Behaviour behaviour)
            {
                sb.Append(", enabled=");
                sb.Append(behaviour.enabled);
                sb.Append(", activeAndEnabled=");
                sb.Append(behaviour.isActiveAndEnabled);
            }

            AppendInterestingMembers(sb, component, type);
            sb.Append(")");
        }

        if (interestingCount == 0)
        {
            return "trackingComponents=0";
        }

        return $"trackingComponents={interestingCount}; {sb}";
    }

    private static bool IsInterestingType(Type type)
    {
        string name = type.FullName ?? type.Name;
        for (int i = 0; i < InterestingTypeTokens.Length; i++)
        {
            if (name.IndexOf(InterestingTypeTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static void AppendInterestingMembers(StringBuilder sb, object instance, Type type)
    {
        int appended = 0;

        foreach (PropertyInfo property in type.GetProperties(InstanceFlags))
        {
            if (appended >= 10) break;
            if (property.GetIndexParameters().Length != 0) continue;
            if (!IsInterestingMember(property.Name)) continue;
            if (!IsSimpleType(property.PropertyType)) continue;

            if (TryGetValue(() => property.GetValue(instance), out object value))
            {
                sb.Append(", ");
                sb.Append(property.Name);
                sb.Append("=");
                sb.Append(FormatValue(value));
                appended++;
            }
        }

        foreach (FieldInfo field in type.GetFields(InstanceFlags))
        {
            if (appended >= 16) break;
            if (!IsInterestingMember(field.Name)) continue;
            if (!IsSimpleType(field.FieldType)) continue;

            if (TryGetValue(() => field.GetValue(instance), out object value))
            {
                sb.Append(", ");
                sb.Append(field.Name);
                sb.Append("=");
                sb.Append(FormatValue(value));
                appended++;
            }
        }
    }

    private static bool IsInterestingMember(string memberName)
    {
        for (int i = 0; i < InterestingMemberTokens.Length; i++)
        {
            if (memberName.IndexOf(InterestingMemberTokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(Vector3) ||
               type == typeof(Quaternion) ||
               typeof(UnityEngine.Object).IsAssignableFrom(type);
    }

    private static string FormatValue(object value)
    {
        if (value == null) return "<null>";
        if (value is UnityEngine.Object unityObject)
        {
            return unityObject == null ? "<null>" : unityObject.name;
        }

        return value.ToString();
    }

    private static bool TryGetValue(Func<object> getter, out object value)
    {
        try
        {
            value = getter();
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static string GetXrStatus()
    {
        try
        {
            string loadedDevice = string.IsNullOrEmpty(XRSettings.loadedDeviceName) ? "<none>" : XRSettings.loadedDeviceName;
            return $"deviceActive={XRSettings.isDeviceActive}, loadedDevice={loadedDevice}";
        }
        catch (Exception e)
        {
            return $"error={e.Message}";
        }
    }

    private static string GetOvrInputStatus()
    {
        try
        {
            bool rightTouch = OVRInput.IsControllerConnected(OVRInput.Controller.RTouch);
            bool leftTouch = OVRInput.IsControllerConnected(OVRInput.Controller.LTouch);
            bool touch = OVRInput.IsControllerConnected(OVRInput.Controller.Touch);
            bool hands = OVRInput.IsControllerConnected(OVRInput.Controller.Hands);
            return $"rightTouch={rightTouch}, leftTouch={leftTouch}, touch={touch}, hands={hands}";
        }
        catch (Exception e)
        {
            return $"error={e.Message}";
        }
    }

    private static string GetOvrPluginStatus()
    {
        Type pluginType = Type.GetType("OVRPlugin");
        if (pluginType == null) return "<OVRPlugin type not found>";

        var sb = new StringBuilder();
        AppendStaticMember(sb, pluginType, "initialized");
        AppendStaticMember(sb, pluginType, "version");
        AppendStaticMember(sb, pluginType, "nativeSDKVersion");
        AppendStaticMember(sb, pluginType, "systemHeadset");
        AppendStaticMember(sb, pluginType, "trackingOriginType");
        return sb.Length == 0 ? "<no readable OVRPlugin status>" : sb.ToString();
    }

    private static void AppendStaticMember(StringBuilder sb, Type type, string memberName)
    {
        PropertyInfo property = type.GetProperty(memberName, StaticFlags);
        if (property != null && property.GetIndexParameters().Length == 0 && TryGetValue(() => property.GetValue(null), out object propertyValue))
        {
            AppendPair(sb, memberName, propertyValue);
            return;
        }

        FieldInfo field = type.GetField(memberName, StaticFlags);
        if (field != null && TryGetValue(() => field.GetValue(null), out object fieldValue))
        {
            AppendPair(sb, memberName, fieldValue);
        }
    }

    private static void AppendPair(StringBuilder sb, string name, object value)
    {
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(name);
        sb.Append("=");
        sb.Append(FormatValue(value));
    }
}
