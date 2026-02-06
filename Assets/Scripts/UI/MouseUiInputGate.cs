using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.XR;

public sealed class MouseUiInputGate : MonoBehaviour
{
    [Header("Policy")]
    [SerializeField] private bool allowMouseInNonXR = true;
    [Tooltip("If true, mouse is disabled when an XR device is active.")]
    [SerializeField] private bool disableMouseInXR = true;

    [Header("Refs")]
    [SerializeField] private InputSystemUIInputModule inputSystemModule;

    private void Awake()
    {
        if (inputSystemModule == null)
        {
            inputSystemModule = GetComponent<InputSystemUIInputModule>();
        }

        ApplyPolicy();
    }

    private void OnEnable()
    {
        ApplyPolicy();
    }

    private void ApplyPolicy()
    {
        if (inputSystemModule == null)
        {
            return;
        }

        bool xrActive = XRSettings.isDeviceActive;
        if (!allowMouseInNonXR)
        {
            inputSystemModule.enabled = false;
            return;
        }

        if (disableMouseInXR && xrActive)
        {
            inputSystemModule.enabled = false;
            return;
        }

        inputSystemModule.enabled = true;
    }
}
