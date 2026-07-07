using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

public class PassthroughBootstrap : MonoBehaviour
{
    public bool enableOnStart = true;
    public bool forceTransparentCameraBackground = true;
    public bool addPassthroughLayerIfMissing = true;
    public bool useOpaqueFallbackIfPassthroughFails = true;
    public float fallbackCheckDelaySeconds = 2.5f;
    public Color fallbackBackgroundColor = new Color(0.04f, 0.06f, 0.09f, 1f);

    void Start()
    {
        if (enableOnStart)
        {
            EnablePassthrough();
        }

        if (useOpaqueFallbackIfPassthroughFails)
        {
            StartCoroutine(FallbackIfPassthroughDoesNotInitialize());
        }
    }

    [ContextMenu("Enable Passthrough")]
    public void EnablePassthrough()
    {
        EnableOVRManagerPassthrough();
        EnsurePassthroughLayer();

        if (forceTransparentCameraBackground)
        {
            MakeCamerasTransparent();
        }
    }

    private void EnableOVRManagerPassthrough()
    {
        Type managerType = FindType("OVRManager");

        if (managerType == null)
        {
            Debug.LogWarning("PassthroughBootstrap: OVRManager type was not found.");
            return;
        }

        UnityEngine.Object manager = FindFirstObjectByType(managerType);

        if (manager == null)
        {
            GameObject managerObject = new GameObject("OVRManager_PassthroughRuntime");
            manager = managerObject.AddComponent(managerType);
            Debug.Log("PassthroughBootstrap: Created OVRManager runtime object.");
        }

        SetMember(manager, "isInsightPassthroughEnabled", true);
        SetMember(manager, "enableInsightPassthrough", true);
        Debug.Log("PassthroughBootstrap: Requested OVRManager passthrough.");
    }

    private void EnsurePassthroughLayer()
    {
        if (!addPassthroughLayerIfMissing)
        {
            return;
        }

        Type layerType = FindType("OVRPassthroughLayer");

        if (layerType == null)
        {
            Debug.LogWarning("PassthroughBootstrap: OVRPassthroughLayer type was not found.");
            return;
        }

        Component layer = FindFirstObjectByType(layerType) as Component;

        if (layer == null)
        {
            layer = gameObject.AddComponent(layerType);
            Debug.Log("PassthroughBootstrap: Added OVRPassthroughLayer.");
        }

        SetEnumMember(layer, "overlayType", "Underlay");
        SetMember(layer, "hidden", false);
        SetMember(layer, "textureOpacity", 1f);
        Debug.Log("PassthroughBootstrap: Configured passthrough layer as underlay.");
    }

    private void MakeCamerasTransparent()
    {
        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);

        foreach (Camera camera in cameras)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        Debug.Log("PassthroughBootstrap: Set " + cameras.Length + " camera backgrounds transparent.");
    }

    private IEnumerator FallbackIfPassthroughDoesNotInitialize()
    {
        yield return new WaitForSeconds(fallbackCheckDelaySeconds);

        if (IsInsightPassthroughInitialized())
        {
            Debug.Log("PassthroughBootstrap: Passthrough initialized.");
            yield break;
        }

        Camera[] cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);

        foreach (Camera camera in cameras)
        {
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = fallbackBackgroundColor;
        }

        Debug.LogWarning(
            "PassthroughBootstrap: Passthrough did not initialize after " +
            fallbackCheckDelaySeconds.ToString("F1") +
            " seconds. Restored opaque camera background so the app does not appear black.");
    }

    private static bool IsInsightPassthroughInitialized()
    {
        Type managerType = FindType("OVRManager");

        if (managerType == null)
        {
            return false;
        }

        MethodInfo method = managerType.GetMethod(
            "IsInsightPassthroughInitialized",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (method == null || method.ReturnType != typeof(bool))
        {
            return false;
        }

        return (bool)method.Invoke(null, null);
    }

    private static Type FindType(string typeName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (Assembly assembly in assemblies)
        {
            Type type = assembly.GetType(typeName);

            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static UnityEngine.Object FindFirstObjectByType(Type type)
    {
        UnityEngine.Object[] objects = FindObjectsByType(type, FindObjectsSortMode.None);
        return objects.Length > 0 ? objects[0] : null;
    }

    private static void SetMember(object target, string memberName, object value)
    {
        Type type = target.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo field = type.GetField(memberName, flags);

        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }

        PropertyInfo property = type.GetProperty(memberName, flags);

        if (property != null && property.CanWrite)
        {
            property.SetValue(target, value);
        }
    }

    private static void SetEnumMember(object target, string memberName, string enumName)
    {
        Type type = target.GetType();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo field = type.GetField(memberName, flags);

        if (field != null && field.FieldType.IsEnum)
        {
            field.SetValue(target, Enum.Parse(field.FieldType, enumName));
            return;
        }

        PropertyInfo property = type.GetProperty(memberName, flags);

        if (property != null && property.CanWrite && property.PropertyType.IsEnum)
        {
            property.SetValue(target, Enum.Parse(property.PropertyType, enumName));
        }
    }
}
