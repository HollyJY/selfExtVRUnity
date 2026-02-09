using System.Diagnostics.CodeAnalysis;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class InstructionPanelController : MonoBehaviour
{
    public bool goToVoiceSample = true;
    public string customSceneName;

    private void Update()
    {
        // Use right-hand index trigger as the primary VR input
        float triggerValue = OVRInput.Get(OVRInput.Axis1D.SecondaryIndexTrigger);
        if (triggerValue > 0.1f)
        {
            Debug.Log($"Right trigger value: {triggerValue}");
        }

        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            Debug.Log("Right trigger pressed, trying to go next scene");
            TryGoNextScene();
        }

#if ENABLE_INPUT_SYSTEM
        // New Input System keyboard fallback
        if (Keyboard.current != null && Keyboard.current.rightArrowKey.wasPressedThisFrame)
        {
            Debug.Log("Keyboard right arrow pressed, trying to go next scene");
            TryGoNextScene();
        }
#else
        // Old Input Manager fallback (only works if legacy input is enabled)
        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            Debug.Log("Keyboard right arrow pressed, trying to go next scene");
            TryGoNextScene();
        }
#endif
    }

    private void TryGoNextScene()
    {
        if (GameFlowManager.Instance != null)
        {
            if (goToVoiceSample)
            {
                GameFlowManager.Instance.GoToVoiceSampleScene();
            }
            else
            {
                GameFlowManager.Instance.GoToInteractionScene();
            }
            return;
        }

        if (!string.IsNullOrEmpty(customSceneName))
        {
            SceneManager.LoadSceneAsync(customSceneName, LoadSceneMode.Single);
        }
        else
        {
            Debug.LogWarning("InstructionPanelController: No GameFlowManager and no customSceneName set.");
        }
    }
}
