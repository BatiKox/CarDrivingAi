using UnityEngine;

/// <summary>
/// Brama na torze. Ustaw order rosnąco w kierunku jazdy (0, 1, 2…).
/// Ostatnia z isFinish to meta.
/// </summary>
[RequireComponent(typeof(BoxCollider2D))]
public class Checkpoint : MonoBehaviour
{
    [Tooltip("Kolejność na torze. 0 = pierwsza brama po starcie.")]
    [SerializeField] int order;

    [Tooltip("Zaznacz na mecie. Po przejechaniu auto wraca do bramy 0 (kolejne okrążenie).")]
    [SerializeField] bool isFinish;

    public int Order => order;
    public bool IsFinish => isFinish;

    void Reset()
    {
        BoxCollider2D box = GetComponent<BoxCollider2D>();
        box.isTrigger = true;
        box.size = new Vector2(2f, 0.35f);
    }

    void OnValidate()
    {
        BoxCollider2D box = GetComponent<BoxCollider2D>();
        if (box != null)
            box.isTrigger = true;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        CarController car = other.GetComponentInParent<CarController>();
        if (car != null)
            car.NotifyCheckpoint(this);
    }

    void OnDrawGizmos()
    {
        Gizmos.color = isFinish ? Color.red : (order == 0 ? Color.green : Color.yellow);
        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        BoxCollider2D box = GetComponent<BoxCollider2D>();
        Vector3 size = box != null ? (Vector3)box.size : new Vector3(2f, 0.35f, 0.1f);
        Gizmos.DrawWireCube(Vector3.zero, size);
        Gizmos.matrix = old;
    }
}
