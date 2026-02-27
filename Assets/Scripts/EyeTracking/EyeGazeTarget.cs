using UnityEngine;

[DisallowMultipleComponent]
public class EyeGazeTarget : MonoBehaviour
{
    [Tooltip("Unique target id written to CSV, e.g. avatar_main or agent_debater")]
    public string targetId = "";

    [Tooltip("Optional group label for analysis, e.g. avatar / agent")]
    public string targetGroup = "avatar";

    [Tooltip("Scene view helper only")]
    public Color gizmoColor = new Color(1f, 0.65f, 0f, 0.7f);

    private void Reset()
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            targetId = gameObject.name;
        }
    }

    public string GetResolvedTargetId()
    {
        return string.IsNullOrWhiteSpace(targetId) ? gameObject.name : targetId;
    }

    private void OnDrawGizmosSelected()
    {
        Collider col = GetComponent<Collider>();
        if (col == null) return;

        Gizmos.color = gizmoColor;
        Matrix4x4 old = Gizmos.matrix;

        if (col is BoxCollider box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
        }
        else if (col is SphereCollider sphere)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(sphere.center, sphere.radius);
        }
        else if (col is CapsuleCollider capsule)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(capsule.center, capsule.radius);
        }

        Gizmos.matrix = old;
    }
}
