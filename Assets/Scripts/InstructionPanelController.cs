using System.Diagnostics.CodeAnalysis;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class InstructionPanelController : MonoBehaviour
{
    public bool goToVoiceSample = true;
    public string customSceneName;
    public bool waitForXrBeforeControllerTrigger = true;
    public float controllerTriggerReadyTimeoutSeconds = 8f;

    private bool transitionInProgress;

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
            if (waitForXrBeforeControllerTrigger)
                StartCoroutine(TryGoNextSceneWhenXrReady("ovr_trigger"));
            else
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
        if (transitionInProgress) return;
        transitionInProgress = true;

        string activeScene = SceneManager.GetActiveScene().name;
        if (GameFlowManager.Instance != null &&
            activeScene == GameFlowManager.Instance.voiceSampleSceneName &&
            !GameFlowManager.Instance.IsVoiceSampleReadyToProceed())
        {
            Debug.LogWarning("InstructionPanelController: Blocked scene advance until voice sample is finished.");
            transitionInProgress = false;
            return;
        }

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
            transitionInProgress = false;
        }
    }

    private IEnumerator TryGoNextSceneWhenXrReady(string source)
    {
        if (transitionInProgress) yield break;

        float start = Time.realtimeSinceStartup;
        while (GameFlowManager.Instance != null &&
               !GameFlowManager.Instance.IsXrReadyForStartup() &&
               Time.realtimeSinceStartup - start < controllerTriggerReadyTimeoutSeconds)
        {
            if (Time.frameCount % 60 == 0)
            {
                GameFlowManager.Instance.LogStartupStep("controller_trigger_waiting_xr", $"source={source}; waited={(Time.realtimeSinceStartup - start):F2}s", "tracking");
            }
            yield return null;
        }

        if (GameFlowManager.Instance != null)
        {
            GameFlowManager.Instance.LogStartupStep("controller_trigger_xr_gate_done", $"source={source}; waited={(Time.realtimeSinceStartup - start):F2}s; ready={GameFlowManager.Instance.IsXrReadyForStartup()}", "tracking");
        }

        TryGoNextScene();
    }
}
