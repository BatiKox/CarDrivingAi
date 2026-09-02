using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Zbiera checkpointy i układa je w kolejności jazdy. Linie w Scene = kierunek.
/// </summary>
public class TrackCircuit : MonoBehaviour
{
    public static TrackCircuit Instance { get; private set; }

    readonly List<Checkpoint> ordered = new List<Checkpoint>();

    public int Count => ordered.Count;

    void Awake()
    {
        Instance = this;
        Rebuild();
    }

    void OnEnable()
    {
        Instance = this;
        Rebuild();
    }

    void OnDisable()
    {
        if (Instance == this)
            Instance = null;
    }

    public void Rebuild()
    {
        ordered.Clear();
        Checkpoint[] found = FindObjectsByType<Checkpoint>(FindObjectsInactive.Exclude);
        ordered.AddRange(found);
        ordered.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public Checkpoint Get(int index)
    {
        if (ordered.Count == 0)
            return null;
        int wrapped = ((index % ordered.Count) + ordered.Count) % ordered.Count;
        return ordered[wrapped];
    }

    void OnDrawGizmos()
    {
        Checkpoint[] found = FindObjectsByType<Checkpoint>(FindObjectsInactive.Exclude);
        if (found == null || found.Length < 2)
            return;

        System.Array.Sort(found, (a, b) => a.Order.CompareTo(b.Order));
        Gizmos.color = Color.cyan;
        int wallMask = LayerMask.GetMask("Wall");
        for (int i = 0; i < found.Length; i++)
        {
            Checkpoint from = found[i];
            Checkpoint to = found[(i + 1) % found.Length];
            if (from == null || to == null)
                continue;

            Vector2 a = from.transform.position;
            Vector2 b = to.transform.position;
            Vector2 delta = b - a;
            float dist = delta.magnitude;
            bool blocked = dist > 0.08f
                && Physics2D.Raycast(a, delta / dist, dist, wallMask).collider != null;
            Gizmos.color = blocked ? Color.red : Color.cyan;
            Gizmos.DrawLine(a, b);
        }
    }
}
