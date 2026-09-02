using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Menedżer algorytmu genetycznego.
/// Każde pokolenie: spawnuje populację, steruje żywymi autami przez ich sieci,
/// a gdy wszystkie zginą — selekcja Top N%, krzyżowanie, mutacja, następna generacja.
/// </summary>
public class PopulationManager : MonoBehaviour
{
    [Header("Populacja")]
    [SerializeField] GameObject carPrefab;
    [SerializeField] Transform spawnPoint;
    [SerializeField] int populationSize = 120;

    [Header("Pojazd")]
    [Tooltip("Rozmiar wszystkich aut w populacji. 1 = oryginalny sprite, 0.35 = domyślnie mniejsze.")]
    [Range(0.05f, 2f)]
    [SerializeField] float carScale = 0.35f;

    [Tooltip("Rozrzut startu w poprzek jezdni (wzdłuż osi X spawnu). Za duży wpycha auta w bandę.")]
    [SerializeField] float spawnJitter = 0.06f;

    [Header("Sieć neuronowa")]
    [Tooltip("Liczba neuronów w pojedynczej warstwie ukrytej.")]
    [SerializeField] int hiddenNeurons = 26;

    [Header("Genetyka")]
    [Tooltip("Ułamek najlepszych osobników używanych jako rodzice (0.1 = top 10%).")]
    [Range(0.02f, 0.5f)]
    [SerializeField] float elitePercent = 0.1f;

    [Tooltip("Liczba najlepszych sieci kopiowanych bez zmian do następnego pokolenia.")]
    [SerializeField] int eliteCopies = 2;

    [Tooltip("Szansa mutacji pojedynczej wagi / biasu.")]
    [Range(0f, 1f)]
    [SerializeField] float mutationRate = 0.12f;

    [Tooltip("Maksymalna wielkość szumu dodawanego przy mutacji.")]
    [SerializeField] float mutationStrength = 0.4f;

    [Tooltip("Ułamek populacji zastępowany całkowicie losowymi sieciami (utrzymuje różnorodność).")]
    [Range(0f, 0.5f)]
    [SerializeField] float immigrantPercent = 0.12f;

    [Header("Czas")]
    [Tooltip("Maks. czas pokolenia w sekundach gry. Żywe auta są wtedy kończone. 0 = wyłączone.")]
    [SerializeField] float generationTimeLimit = 80f;

    [Tooltip("Przyspieszenie symulacji (timelapse). 1 = czas rzeczywisty, 8 = osiem razy szybciej.")]
    [Range(1f, 20f)]
    [SerializeField] float simulationSpeed = 4f;

    readonly List<CarController> cars = new List<CarController>();

    int generation = 1;
    int stagnantGenerations;
    float bestFitnessEver;
    float lastGenerationBest;
    float generationTimer;
    bool waitingForPrefab;
    float defaultMaximumDeltaTime = 0.333f;
    float bestLapThisGeneration;
    float bestLapEver;
    int lapsThisGeneration;
    StreamWriter logWriter;
    string logFilePath;

    void Awake()
    {
        if (carPrefab == null)
        {
            CarController[] carsInScene = FindObjectsByType<CarController>(FindObjectsInactive.Include);
            for (int i = 0; i < carsInScene.Length; i++)
            {
                if (carsInScene[i].gameObject.name == "CarPrototype")
                {
                    carPrefab = carsInScene[i].gameObject;
                    break;
                }
            }
        }

        if (carPrefab != null && carPrefab.scene.IsValid())
            carPrefab.SetActive(false);

        if (spawnPoint == null)
        {
            GameObject spawnGo = GameObject.Find("SpawnPoint");
            spawnPoint = spawnGo != null ? spawnGo.transform : transform;
        }

        IgnoreCarOnCarCollisions();
        Application.runInBackground = true;
        defaultMaximumDeltaTime = Time.maximumDeltaTime;
        ApplySimulationSpeed();
        OpenLog();
    }

    void OnDisable()
    {
        Time.timeScale = 1f;
        Time.maximumDeltaTime = defaultMaximumDeltaTime;
        CloseLog();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying)
            ApplySimulationSpeed();
    }
#endif

    void ApplySimulationSpeed()
    {
        float speed = Mathf.Clamp(simulationSpeed, 1f, 20f);
        Time.timeScale = speed;
        Time.maximumDeltaTime = Mathf.Max(defaultMaximumDeltaTime, 0.04f * speed);
    }

    static void IgnoreCarOnCarCollisions()
    {
        int carLayer = LayerMask.NameToLayer("Car");
        if (carLayer >= 0)
            Physics2D.IgnoreLayerCollision(carLayer, carLayer, true);
    }

    int[] NetworkLayers => new[] { CarController.NetworkInputCount, hiddenNeurons, 2 };

    void Start()
    {
        if (!ValidateSetup())
        {
            waitingForPrefab = true;
            return;
        }

        SpawnGeneration(CreateRandomBrains());
    }

    void Update()
    {
        // Prefab można podpiąć w Play Mode po starcie — wtedy odpalamy pierwsze pokolenie.
        if (waitingForPrefab && ValidateSetup())
        {
            waitingForPrefab = false;
            SpawnGeneration(CreateRandomBrains());
        }

        if (Mathf.Abs(Time.timeScale - simulationSpeed) > 0.01f)
            ApplySimulationSpeed();
    }

    void FixedUpdate()
    {
        if (cars.Count == 0)
            return;

        bool anyAlive = false;

        for (int i = 0; i < cars.Count; i++)
        {
            CarController car = cars[i];
            if (car == null || car.IsDead || car.Brain == null)
                continue;

            anyAlive = true;

            float[] networkInputs = car.GetNetworkInputs();
            float[] output = car.Brain.FeedForward(networkInputs);
            car.SetInputs(output[0], output[1]);
        }

        generationTimer += Time.fixedDeltaTime;
        if (anyAlive && generationTimeLimit > 0f && generationTimer >= generationTimeLimit)
        {
            for (int i = 0; i < cars.Count; i++)
            {
                if (cars[i] != null && !cars[i].IsDead)
                    cars[i].Die();
            }

            anyAlive = false;
        }

        if (!anyAlive)
            EvolveNextGeneration();
    }

    bool ValidateSetup()
    {
        if (carPrefab == null)
        {
            Debug.LogWarning("PopulationManager: podłącz prefab samochodu w Inspektorze.");
            return false;
        }

        if (carPrefab.GetComponent<CarController>() == null || carPrefab.GetComponent<CarSensors>() == null)
        {
            Debug.LogError("PopulationManager: prefab musi mieć CarController i CarSensors.");
            return false;
        }

        return true;
    }

    Vector3 SpawnPosition => spawnPoint != null ? spawnPoint.position : transform.position;
    Quaternion SpawnRotation => spawnPoint != null ? spawnPoint.rotation : transform.rotation;

    List<NeuralNetwork> CreateRandomBrains()
    {
        var brains = new List<NeuralNetwork>(populationSize);
        for (int i = 0; i < populationSize; i++)
            brains.Add(new NeuralNetwork(NetworkLayers));
        return brains;
    }

    void SpawnGeneration(List<NeuralNetwork> brains)
    {
        ClearCars();
        generationTimer = 0f;
        bestLapThisGeneration = 0f;
        lapsThisGeneration = 0;

        int count = Mathf.Min(populationSize, brains.Count);
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = SpawnPosition;
            if (spawnPoint != null && spawnJitter > 0f)
                pos += spawnPoint.right * UnityEngine.Random.Range(-spawnJitter, spawnJitter);
            GameObject instance = Instantiate(carPrefab, pos, SpawnRotation);
            instance.name = $"Car_G{generation}_{i:000}";
            instance.SetActive(true);
            AssignCarLayer(instance);

            CarController controller = instance.GetComponent<CarController>();
            controller.ApplyScale(carScale);
            controller.SetGeneration(generation);
            controller.Brain = brains[i];
            controller.LapCompleted += HandleLapCompleted;
            cars.Add(controller);
        }
    }

    void ClearCars()
    {
        for (int i = 0; i < cars.Count; i++)
        {
            if (cars[i] != null)
            {
                cars[i].LapCompleted -= HandleLapCompleted;
                Destroy(cars[i].gameObject);
            }
        }

        cars.Clear();
    }

    static void AssignCarLayer(GameObject instance)
    {
        int carLayer = LayerMask.NameToLayer("Car");
        if (carLayer < 0)
            return;

        Transform[] transforms = instance.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
            transforms[i].gameObject.layer = carLayer;
    }

    void EvolveNextGeneration()
    {
        List<CarController> ranked = new List<CarController>(cars);
        ranked.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

        float previousBest = lastGenerationBest;
        lastGenerationBest = ranked.Count > 0 ? ranked[0].Fitness : 0f;
        if (lastGenerationBest > bestFitnessEver)
            bestFitnessEver = lastGenerationBest;

        if (generation > 1 && lastGenerationBest <= previousBest + 0.75f)
            stagnantGenerations++;
        else
            stagnantGenerations = 0;

        int eliteCount = Mathf.Max(2, Mathf.RoundToInt(populationSize * elitePercent));
        eliteCount = Mathf.Min(eliteCount, ranked.Count);

        var parents = new List<NeuralNetwork>(eliteCount);
        var parentFitness = new List<float>(eliteCount);
        for (int i = 0; i < eliteCount; i++)
        {
            parents.Add(ranked[i].Brain);
            parentFitness.Add(Mathf.Max(0.01f, ranked[i].Fitness));
        }

        Debug.Log(
            $"Pokolenie {generation} zakończone. Najlepszy fitness: {lastGenerationBest:F2} " +
            $"(rekord: {bestFitnessEver:F2}). Okrążenie: {FormatLap(bestLapThisGeneration)} " +
            $"(rekord: {FormatLap(bestLapEver)}). Rodzice: {eliteCount}. Zastój: {stagnantGenerations}.");

        WriteLog(
            "generation",
            "",
            bestLapThisGeneration,
            lastGenerationBest,
            CountFinishedLaps());

        generation++;
        SpawnGeneration(Breed(parents, parentFitness));
    }

    List<NeuralNetwork> Breed(List<NeuralNetwork> parents, List<float> parentFitness)
    {
        var children = new List<NeuralNetwork>(populationSize);

        int copies = Mathf.Clamp(eliteCopies, 0, parents.Count);
        for (int i = 0; i < copies; i++)
            children.Add(parents[i].Clone());

        float rate = mutationRate;
        float strength = mutationStrength;
        if (stagnantGenerations >= 2)
        {
            rate = Mathf.Min(1f, mutationRate * 1.6f);
            strength *= 1.45f;
        }

        int immigrants = Mathf.RoundToInt(populationSize * immigrantPercent);
        if (stagnantGenerations >= 2)
            immigrants = Mathf.Max(immigrants, Mathf.RoundToInt(populationSize * 0.2f));
        immigrants = Mathf.Clamp(immigrants, 0, populationSize - copies);

        while (children.Count < populationSize - immigrants)
        {
            NeuralNetwork parentA = PickParent(parents, parentFitness);
            NeuralNetwork parentB = PickParent(parents, parentFitness);
            NeuralNetwork child = UnityEngine.Random.value < 0.35f
                ? parentA.Clone()
                : NeuralNetwork.Crossover(parentA, parentB);
            child.Mutate(rate, strength);
            children.Add(child);
        }

        while (children.Count < populationSize)
            children.Add(new NeuralNetwork(NetworkLayers));

        return children;
    }

    static NeuralNetwork PickParent(List<NeuralNetwork> parents, List<float> fitness)
    {
        float sum = 0f;
        for (int i = 0; i < fitness.Count; i++)
            sum += fitness[i];

        float pick = UnityEngine.Random.Range(0f, sum);
        float cursor = 0f;
        for (int i = 0; i < parents.Count; i++)
        {
            cursor += fitness[i];
            if (pick <= cursor)
                return parents[i];
        }

        return parents[parents.Count - 1];
    }

    void OnGUI()
    {
        const int pad = 12;
        GUI.color = Color.white;
        GUI.Label(new Rect(pad, pad, 480, 24), $"Pokolenie: {generation}");
        GUI.Label(new Rect(pad, pad + 22, 480, 24), $"Populacja: {cars.Count}  |  żywi: {CountAlive()}");
        GUI.Label(new Rect(pad, pad + 44, 480, 24), $"Najlepszy (to pokolenie / rekord): {lastGenerationBest:F1} / {bestFitnessEver:F1}");
        GUI.Label(new Rect(pad, pad + 66, 480, 24), $"Zastój pokoleń: {stagnantGenerations}");
        if (generationTimeLimit > 0f)
            GUI.Label(new Rect(pad, pad + 88, 480, 24), $"Czas pokolenia: {generationTimer:F0} / {generationTimeLimit:F0} s");
        GUI.Label(new Rect(pad, pad + 110, 220, 24), $"Prędkość: {simulationSpeed:F0}×");
        simulationSpeed = GUI.HorizontalSlider(new Rect(pad + 130, pad + 116, 200, 20), simulationSpeed, 1f, 20f);
        GUI.Label(new Rect(pad, pad + 138, 520, 24),
            $"Najszybsze okrążenie: {FormatLap(bestLapThisGeneration)}  |  rekord: {FormatLap(bestLapEver)}");
        if (!string.IsNullOrEmpty(logFilePath))
            GUI.Label(new Rect(pad, pad + 160, 620, 24), $"Log: {logFilePath}");
    }

    int CountAlive()
    {
        int alive = 0;
        for (int i = 0; i < cars.Count; i++)
        {
            if (cars[i] != null && !cars[i].IsDead)
                alive++;
        }

        return alive;
    }

    int CountFinishedLaps()
    {
        int n = 0;
        for (int i = 0; i < cars.Count; i++)
        {
            if (cars[i] != null && cars[i].Laps > 0)
                n++;
        }

        return n;
    }

    void HandleLapCompleted(CarController car, float lapTime)
    {
        lapsThisGeneration++;
        if (bestLapThisGeneration <= 0f || lapTime < bestLapThisGeneration)
            bestLapThisGeneration = lapTime;

        bool isRecord = bestLapEver <= 0f || lapTime < bestLapEver;
        if (isRecord)
            bestLapEver = lapTime;

        WriteLog("lap", car != null ? car.name : "", lapTime, car != null ? car.Fitness : 0f, lapsThisGeneration);
        if (isRecord)
            Debug.Log($"Nowy rekord okrążenia: {lapTime:F2} s ({car?.name})");
    }

    static string FormatLap(float seconds)
    {
        if (seconds <= 0.001f)
            return "brak";
        return $"{seconds:F2} s";
    }

    static string LogDirectory
    {
        get
        {
#if UNITY_EDITOR
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "TrainingLogs");
#else
            return Path.Combine(Application.persistentDataPath, "TrainingLogs");
#endif
        }
    }

    void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            logFilePath = Path.Combine(LogDirectory, $"training_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            logWriter = new StreamWriter(logFilePath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            logWriter.WriteLine("time,event,generation,car,lap_s,best_lap_s,fitness,best_fitness,laps_in_gen,finished_cars,alive,stagnation");
            WriteLog("session_start", "", 0f, 0f, 0);
            Debug.Log($"Log treningu: {logFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Nie udało się otworzyć logu: {ex.Message}");
            logWriter = null;
            logFilePath = null;
        }
    }

    void CloseLog()
    {
        if (logWriter == null)
            return;

        try
        {
            WriteLog("session_end", "", bestLapEver, bestFitnessEver, lapsThisGeneration);
            logWriter.Flush();
            logWriter.Dispose();
        }
        catch (Exception)
        {
            // Stop Play może zamykać strumień dwukrotnie.
        }

        logWriter = null;
    }

    void WriteLog(string eventName, string car, float lapTime, float fitness, int finishedCars)
    {
        if (logWriter == null)
            return;

        try
        {
            string lap = lapTime > 0.001f ? lapTime.ToString("F3") : "";
            string best = bestLapEver > 0.001f ? bestLapEver.ToString("F3") : "";
            logWriter.WriteLine(
                $"{DateTime.Now:HH:mm:ss},{eventName},{generation},{EscapeCsv(car)},{lap},{best}," +
                $"{fitness:F2},{bestFitnessEver:F2},{lapsThisGeneration},{finishedCars},{CountAlive()},{stagnantGenerations}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Zapis logu nieudany: {ex.Message}");
        }
    }

    static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

public class EditorSetupRunner : MonoBehaviour
{
}
