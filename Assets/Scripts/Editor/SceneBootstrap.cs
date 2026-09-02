using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

/// <summary>
/// Jednorazowa konfiguracja sceny z poziomu edytora (uruchamiana przez Unity MCP).
/// Tworzy tag/warstwę Wall, import sprite'ów, tor, prefab auta, spawn i PopulationManager.
/// EditorApplication.ExecuteMenuItem nie obsługuje własnych MenuItem — stąd flaga + InitializeOnLoad.
/// </summary>
[InitializeOnLoad]
public static class SceneBootstrap
{
    const string SetupFlagPath = "Assets/Scripts/Editor/RUN_SETUP.flag";
    const string WallName = "Wall";
    const string TracePath = "Assets/Trace.png";
    const string CarSpritePath = "Assets/CarSprite.png";
    const string PrefabFolder = "Assets/Prefabs";
    const string PrefabPath = "Assets/Prefabs/Car.prefab";
    const int WallLayerIndex = 6;
    const float TrackPpu = 100f;
    const float CarPpu = 1500f;
    const int SampleStep = 2;

    static SceneBootstrap()
    {
        EditorApplication.delayCall += TryRunFromFlag;
    }

    static void TryRunFromFlag()
    {
        string flagPath = System.IO.Path.Combine(Application.dataPath, "Scripts/Editor/RUN_SETUP.flag");
        if (!System.IO.File.Exists(flagPath))
            return;

        System.IO.File.Delete(flagPath);
        string metaPath = flagPath + ".meta";
        if (System.IO.File.Exists(metaPath))
            System.IO.File.Delete(metaPath);

        SetupTrackAndCar();
    }

    [MenuItem("CarDrivingAI/Setup Track And Car")]
    public static void SetupTrackAndCar()
    {
        Debug.Log("SceneBootstrap: start");
        try
        {
            SetupTrackAndCarInternal();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    static void SetupTrackAndCarInternal()
    {
        EnsureTagAndLayer();
        ConfigureTexture(TracePath, TrackPpu, true);
        ConfigureTexture(CarSpritePath, CarPpu, false);

        ClearPreviousSetup();

        Sprite trackSprite = LoadSprite(TracePath);
        Sprite carSprite = LoadSprite(CarSpritePath);
        if (trackSprite == null || carSprite == null)
        {
            Debug.LogError("SceneBootstrap: nie znaleziono sprite'ów Trace.png / CarSprite.png.");
            return;
        }

        GameObject track = CreateTrack(trackSprite);
        Vector3 spawnPos;
        Quaternion spawnRot;
        FindSpawnOnRoad(trackSprite, out spawnPos, out spawnRot);

        GameObject car = CreateCar(carSprite, spawnPos, spawnRot);
        GameObject prefab = SaveCarPrefab(car);
        UnityEngine.Object.DestroyImmediate(car);

        GameObject spawnPoint = new GameObject("SpawnPoint");
        spawnPoint.transform.SetPositionAndRotation(spawnPos, spawnRot);

        GameObject managerGo = new GameObject("PopulationManager");
        PopulationManager manager = managerGo.AddComponent<PopulationManager>();
        SerializedObject so = new SerializedObject(manager);
        so.FindProperty("carPrefab").objectReferenceValue = prefab;
        so.FindProperty("spawnPoint").objectReferenceValue = spawnPoint.transform;
        so.FindProperty("populationSize").intValue = 120;
        so.ApplyModifiedProperties();

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.orthographic = true;
            cam.orthographicSize = 5.6f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
        }

        EditorUtility.SetDirty(track);
        EditorUtility.SetDirty(spawnPoint);
        so.ApplyModifiedProperties();
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"SceneBootstrap: gotowe. Prefab={PrefabPath}, spawn={spawnPos}, rotZ={spawnRot.eulerAngles.z:F1}");
    }

    static void EnsureTagAndLayer()
    {
        if (System.Array.IndexOf(InternalEditorUtility.tags, WallName) < 0)
            InternalEditorUtility.AddTag(WallName);

        SerializedObject tagManager = new SerializedObject(
            AssetDatabase.LoadAssetAtPath<UnityEngine.Object>("ProjectSettings/TagManager.asset"));
        SerializedProperty layers = tagManager.FindProperty("layers");
        SerializedProperty layerProp = layers.GetArrayElementAtIndex(WallLayerIndex);
        if (layerProp.stringValue != WallName)
        {
            layerProp.stringValue = WallName;
            tagManager.ApplyModifiedProperties();
        }
    }

    static void ConfigureTexture(string path, float ppu, bool readable)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;

        bool alreadyOk =
            importer.textureType == TextureImporterType.Sprite &&
            importer.spriteImportMode == SpriteImportMode.Single &&
            Mathf.Abs(importer.spritePixelsPerUnit - ppu) < 0.01f &&
            importer.isReadable == readable;
        if (alreadyOk)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = ppu;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.isReadable = readable;
        importer.filterMode = FilterMode.Bilinear;
        importer.spritePivot = new Vector2(0.5f, 0.5f);
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteAlignment = (int)SpriteAlignment.Center;
        settings.spritePivot = new Vector2(0.5f, 0.5f);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        EditorUtility.SetDirty(importer);
        // SaveAndReimport przerywa setup (reload domeny) — meta jest już ustawione z zewnątrz.
    }

    static Sprite LoadSprite(string path)
    {
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static void ClearPreviousSetup()
    {
        string[] names = { "Track", "Trace", "Car", "SpawnPoint", "PopulationManager" };
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (string name in names)
            {
                if (root.name == name)
                    UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }

    static GameObject CreateTrack(Sprite sprite)
    {
        GameObject track = new GameObject("Track");
        SpriteRenderer sr = track.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = 0;
        track.transform.position = Vector3.zero;

        Texture2D tex = sprite.texture;
        bool[,] road = BuildRoadMask(tex, SampleStep);
        List<List<Vector2>> contours = TraceContours(road);
        int wallLayer = LayerMask.NameToLayer(WallName);

        int index = 0;
        foreach (List<Vector2> contour in contours)
        {
            List<Vector2> world = GridToWorld(contour, tex.width, tex.height, SampleStep, TrackPpu);
            world = Simplify(world, 0.06f);
            if (world.Count < 8)
                continue;

            GameObject wall = new GameObject($"Wall_{index++}");
            wall.transform.SetParent(track.transform, false);
            wall.tag = WallName;
            wall.layer = wallLayer;

            EdgeCollider2D edge = wall.AddComponent<EdgeCollider2D>();
            edge.SetPoints(world);
            edge.edgeRadius = 0.02f;
        }

        if (index == 0)
            Debug.LogWarning("SceneBootstrap: nie wykryto krawędzi toru — sprawdź kolory Trace.png.");
        else
            Debug.Log($"SceneBootstrap: utworzono {index} colliderów krawędzi (EdgeCollider2D) na warstwie Wall.");

        return track;
    }

    static GameObject CreateCar(Sprite sprite, Vector3 position, Quaternion rotation)
    {
        GameObject car = new GameObject("Car");
        car.transform.SetPositionAndRotation(position, rotation);

        SpriteRenderer sr = car.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = 1;

        BoxCollider2D box = car.AddComponent<BoxCollider2D>();
        box.size = sprite.bounds.size * 0.9f;

        Rigidbody2D rb = car.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.gravityScale = 0f;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.linearDamping = 0.4f;
        rb.angularDamping = 2f;
        rb.constraints = RigidbodyConstraints2D.None;

        CarController controller = car.AddComponent<CarController>();
        CarSensors sensors = car.GetComponent<CarSensors>();
        SerializedObject sensorSo = new SerializedObject(sensors);
        sensorSo.FindProperty("wallMask").intValue = LayerMask.GetMask(WallName);
        sensorSo.ApplyModifiedProperties();

        SerializedObject carSo = new SerializedObject(controller);
        carSo.ApplyModifiedProperties();

        return car;
    }

    static GameObject SaveCarPrefab(GameObject car)
    {
        if (!AssetDatabase.IsValidFolder(PrefabFolder))
            AssetDatabase.CreateFolder("Assets", "Prefabs");

        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(car, PrefabPath);
        AssetDatabase.SaveAssets();
        return prefab;
    }

    static bool IsRoad(Color c)
    {
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        bool greyish = max - min < 0.18f;
        return greyish && max > 0.28f && max < 0.82f;
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
            {
                Color c = pixels[(y * step) * tex.width + (x * step)];
                road[x, y] = IsRoad(c);
            }
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
                int cx = x;
                int cy = y;
                int dir = 0;
                int guard = 0;
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
        {
            float px = grid[i].x * step;
            float py = grid[i].y * step;
            world.Add(new Vector2(px / ppu - halfW, py / ppu - halfH));
        }

        return world;
    }

    static List<Vector2> Simplify(List<Vector2> points, float epsilon)
    {
        if (points.Count < 8)
            return points;

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[points.Count - 1] = true;
        DouglasPeucker(points, 0, points.Count - 1, epsilon, keep);

        var result = new List<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }

        return result;
    }

    static void DouglasPeucker(List<Vector2> points, int start, int end, float epsilon, bool[] keep)
    {
        float maxDist = 0f;
        int index = start;

        for (int i = start + 1; i < end; i++)
        {
            float dist = DistanceToSegment(points[i], points[start], points[end]);
            if (dist > maxDist)
            {
                maxDist = dist;
                index = i;
            }
        }

        if (maxDist > epsilon)
        {
            keep[index] = true;
            DouglasPeucker(points, start, index, epsilon, keep);
            DouglasPeucker(points, index, end, epsilon, keep);
        }
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 0.0001f)
            return Vector2.Distance(p, a);

        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    static void FindSpawnOnRoad(Sprite sprite, out Vector3 position, out Quaternion rotation)
    {
        Texture2D tex = sprite.texture;
        int w = tex.width;
        int h = tex.height;
        Color[] pixels = tex.GetPixels();

        int bestX = w / 2;
        int bestY = h / 4;
        int bestClear = -1;

        int yStart = h / 8;
        int yEnd = h / 2;
        for (int y = yStart; y < yEnd; y += 4)
        {
            for (int x = w / 4; x < (w * 3) / 4; x += 4)
            {
                if (!IsRoad(pixels[y * w + x]))
                    continue;

                int clear = Clearance(pixels, w, h, x, y);
                if (clear > bestClear)
                {
                    bestClear = clear;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        Vector2 heading = EstimateHeading(pixels, w, h, bestX, bestY);
        float ppu = TrackPpu;
        float worldX = bestX / ppu - w / (2f * ppu);
        float worldY = bestY / ppu - h / (2f * ppu);
        position = new Vector3(worldX, worldY, 0f);
        rotation = Quaternion.FromToRotation(Vector3.up, heading);
    }

    static int Clearance(Color[] pixels, int w, int h, int x, int y)
    {
        int radius = 0;
        while (radius < 80)
        {
            radius++;
            if (!IsRoad(pixels[y * w + Mathf.Clamp(x - radius, 0, w - 1)])) break;
            if (!IsRoad(pixels[y * w + Mathf.Clamp(x + radius, 0, w - 1)])) break;
            if (!IsRoad(pixels[Mathf.Clamp(y - radius, 0, h - 1) * w + x])) break;
            if (!IsRoad(pixels[Mathf.Clamp(y + radius, 0, h - 1) * w + x])) break;
        }

        return radius;
    }

    static Vector2 EstimateHeading(Color[] pixels, int w, int h, int x, int y)
    {
        Vector2[] dirs =
        {
            Vector2.up, Vector2.down, Vector2.left, Vector2.right,
            new Vector2(1, 1).normalized, new Vector2(-1, 1).normalized,
            new Vector2(1, -1).normalized, new Vector2(-1, -1).normalized
        };

        int bestLen = 0;
        Vector2 best = Vector2.up;

        foreach (Vector2 dir in dirs)
        {
            int len = 0;
            for (int s = 1; s < 200; s++)
            {
                int px = Mathf.RoundToInt(x + dir.x * s);
                int py = Mathf.RoundToInt(y + dir.y * s);
                if (px < 0 || py < 0 || px >= w || py >= h)
                    break;
                if (!IsRoad(pixels[py * w + px]))
                    break;
                len = s;
            }

            if (len > bestLen)
            {
                bestLen = len;
                best = dir;
            }
        }

        return best.sqrMagnitude > 0.01f ? best.normalized : Vector2.up;
    }
}

class SceneBootstrapTrigger : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        foreach (string path in imported)
        {
            if (!path.EndsWith("RUN_SETUP.flag"))
                continue;

            EditorApplication.delayCall += SceneBootstrap.SetupTrackAndCar;
            break;
        }
    }
}
