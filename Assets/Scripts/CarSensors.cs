using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pięć promieni Physics2D.Raycast wykrywających wyłącznie warstwę Wall.
/// Zwraca znormalizowane odległości: 0 = ściana przy aucie, 1 = brak przeszkody w zasięgu.
/// </summary>
public class CarSensors : MonoBehaviour
{
    public const int SensorCount = 5;

    [Header("Raycast")]
    [Tooltip("Maksymalny zasięg każdego czujnika w jednostkach świata.")]
    [SerializeField] float maxDistance = 4f;

    [Tooltip("Warstwa ścian toru. Jeśli pusta, skrypt spróbuje LayerMask.GetMask(\"Wall\").")]
    [SerializeField] LayerMask wallMask;

    [Tooltip("Kąty względem przodu auta (transform.up), w stopniach.")]
    [SerializeField] float[] sensorAngles = { -90f, -45f, 0f, 45f, 90f };

    readonly float[] readings = new float[SensorCount];

    /// <summary>Ostatni odczyt czujników (1 = pusto, 0 = ściana przy aucie). Zera przed pierwszym pomiarem oznaczają „pusto”, nie ścianę.</summary>
    public float[] LastReadings => readings;

    void Awake()
    {
        for (int i = 0; i < readings.Length; i++)
            readings[i] = 1f;

        if (wallMask.value == 0)
            wallMask = LayerMask.GetMask("Wall");
    }

    /// <summary>
    /// Wykonuje 5 raycastów i zwraca tę samą tablicę (bez alokacji).
    /// Indeksy: 0 = -90°, 1 = -45°, 2 = 0° (przód), 3 = 45°, 4 = 90°.
    /// </summary>
    public float[] GetReadings()
    {
        Vector2 origin = transform.position;
        int count = Mathf.Min(sensorAngles.Length, SensorCount);

        for (int i = 0; i < SensorCount; i++)
        {
            float angle = i < count ? sensorAngles[i] : 0f;
            Vector2 direction = Quaternion.Euler(0f, 0f, angle) * transform.up;

            RaycastHit2D hit = Physics2D.Raycast(origin, direction, maxDistance, wallMask);
            float normalized = hit.collider != null
                ? Mathf.Clamp01(hit.distance / maxDistance)
                : 1f;

            readings[i] = normalized;
            if (Time.timeScale <= 1.5f)
                DrawDebugRay(origin, direction, normalized);
        }

        return readings;
    }

    void DrawDebugRay(Vector2 origin, Vector2 direction, float normalizedDistance)
    {
        Color color = Color.Lerp(Color.red, Color.green, normalizedDistance);
        float length = normalizedDistance * maxDistance;
        Debug.DrawRay(origin, direction * length, color, Time.fixedDeltaTime * 2f);
    }

    void OnDrawGizmosSelected()
    {
        if (sensorAngles == null)
            return;

        Gizmos.color = Color.cyan;
        Vector2 origin = transform.position;
        int count = Mathf.Min(sensorAngles.Length, SensorCount);

        for (int i = 0; i < count; i++)
        {
            Vector2 direction = Quaternion.Euler(0f, 0f, sensorAngles[i]) * transform.up;
            Gizmos.DrawLine(origin, origin + direction * maxDistance);
        }
    }
}

/// <summary>
/// Ładuje Trace.png na SpriteRenderer (widoczny w edytorze) i w Play Mode
/// piecze EdgeCollider2D z krawędzi szarej jezdni.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(SpriteRenderer))]
public class TrackWallBaker : MonoBehaviour
{
    const string WallName = "Wall";
    const int SampleStep = 2;
    const float SimplifyEpsilon = 0.06f;

    void OnEnable()
    {
        EnsureSprite();
    }

    void Awake()
    {
        EnsureSprite();
        if (!Application.isPlaying || transform.childCount > 0)
            return;

        Bake();
    }

    void EnsureSprite()
    {
        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr == null)
            return;

        sr.sortingOrder = 0;
        if (sr.sprite != null)
            return;

#if UNITY_EDITOR
        Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Trace.png");
        if (sprite != null)
        {
            sr.sprite = sprite;
            UnityEditor.EditorUtility.SetDirty(sr);
        }
#endif
    }

    public void Bake()
    {
        SpriteRenderer sr = GetComponent<SpriteRenderer>();
        if (sr == null || sr.sprite == null || sr.sprite.texture == null)
        {
            Debug.LogError("TrackWallBaker: brak sprite'a na torze.");
            return;
        }

        Texture2D tex = sr.sprite.texture;
        if (!tex.isReadable)
        {
            Debug.LogError("TrackWallBaker: Trace.png musi mieć Read/Write w importerze.");
            return;
        }

        int wallLayer = LayerMask.NameToLayer(WallName);
        bool[,] road = BuildRoadMask(tex, SampleStep);
        List<List<Vector2>> contours = TraceContours(road);
        int index = 0;

        foreach (List<Vector2> contour in contours)
        {
            List<Vector2> world = GridToWorld(contour, tex.width, tex.height, SampleStep, sr.sprite.pixelsPerUnit);
            world = Simplify(world, SimplifyEpsilon);
            if (world.Count < 8)
                continue;

            GameObject wall = new GameObject($"Wall_{index++}");
            wall.transform.SetParent(transform, false);
            wall.tag = WallName;
            wall.layer = wallLayer < 0 ? 0 : wallLayer;

            EdgeCollider2D edge = wall.AddComponent<EdgeCollider2D>();
            edge.points = world.ToArray();
            edge.edgeRadius = 0.02f;
        }

        Debug.Log($"TrackWallBaker: {index} krawędzi ścian.");
    }

    static bool IsRoad(Color c)
    {
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        return max - min < 0.18f && max > 0.28f && max < 0.82f;
    }

    static bool[,] BuildRoadMask(Texture2D tex, int step)
    {
        int w = tex.width / step;
        int h = tex.height / step;
        bool[,] road = new bool[w, h];
        Color[] pixels = tex.GetPixels();

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                road[x, y] = IsRoad(pixels[(y * step) * tex.width + (x * step)]);
        }

        return road;
    }

    static bool IsBoundary(bool[,] road, int x, int y)
    {
        int w = road.GetLength(0);
        int h = road.GetLength(1);
        if (!road[x, y])
            return false;
        if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
            return true;
        return !road[x - 1, y] || !road[x + 1, y] || !road[x, y - 1] || !road[x, y + 1];
    }

    static List<List<Vector2>> TraceContours(bool[,] road)
    {
        int w = road.GetLength(0);
        int h = road.GetLength(1);
        bool[,] visited = new bool[w, h];
        var contours = new List<List<Vector2>>();
        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
        int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                if (!IsBoundary(road, x, y) || visited[x, y])
                    continue;

                var contour = new List<Vector2>();
                int cx = x, cy = y, dir = 0, guard = 0;
                int max = w * h;

                do
                {
                    visited[cx, cy] = true;
                    contour.Add(new Vector2(cx, cy));
                    bool found = false;
                    for (int i = 0; i < 8; i++)
                    {
                        int nd = (dir + 6 + i) % 8;
                        int nx = cx + dx[nd];
                        int ny = cy + dy[nd];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        if (IsBoundary(road, nx, ny) && (!visited[nx, ny] || (nx == x && ny == y && contour.Count > 8)))
                        {
                            cx = nx;
                            cy = ny;
                            dir = nd;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                        break;
                    guard++;
                } while ((cx != x || cy != y) && guard < max);

                if (contour.Count >= 24)
                    contours.Add(contour);
            }
        }

        return contours;
    }

    static List<Vector2> GridToWorld(List<Vector2> grid, int texW, int texH, int step, float ppu)
    {
        var world = new List<Vector2>(grid.Count);
        float halfW = texW / (2f * ppu);
        float halfH = texH / (2f * ppu);
        for (int i = 0; i < grid.Count; i++)
            world.Add(new Vector2(grid[i].x * step / ppu - halfW, grid[i].y * step / ppu - halfH));
        return world;
    }

    static List<Vector2> Simplify(List<Vector2> points, float epsilon)
    {
        if (points.Count < 8)
            return points;

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        Douglas(points, 0, points.Count - 1, epsilon, keep);

        var result = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }

        return result;
    }

    static void Douglas(List<Vector2> points, int start, int end, float epsilon, bool[] keep)
    {
        float maxDist = 0f;
        int index = start;
        for (int i = start + 1; i < end; i++)
        {
            float dist = DistToSegment(points[i], points[start], points[end]);
            if (dist > maxDist)
            {
                maxDist = dist;
                index = i;
            }
        }

        if (maxDist > epsilon)
        {
            keep[index] = true;
            Douglas(points, start, index, epsilon, keep);
            Douglas(points, index, end, epsilon, keep);
        }
    }

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 0.0001f)
            return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }
}
