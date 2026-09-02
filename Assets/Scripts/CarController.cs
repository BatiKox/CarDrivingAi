using UnityEngine;

/// <summary>
/// Fizyka samochodu 2D z widoku z góry (top-down).
/// Nie czyta klawiatury — AI steruje wyłącznie przez <see cref="SetInputs"/>.
/// Przód sprita to góra tekstury, więc kierunek jazdy to <c>transform.up</c>.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(CarSensors))]
public class CarController : MonoBehaviour
{
    [Header("Napęd")]
    [Tooltip("Siła przyspieszenia w newtonach (przód i tył).")]
    [SerializeField] float accelerationForce = 8f;

    [Tooltip("Maksymalna prędkość liniowa.")]
    [SerializeField] float maxSpeed = 8f;

    [Tooltip("Prędkość skrętu w stopniach na sekundę przy pełnym skęcie.")]
    [SerializeField] float turnSpeed = 180f;

    [Header("Wygląd")]
    [Tooltip("Skala auta. 1 = rozmiar sprite'a, mniejsze wartości = ciaśniejszy pojazd na torze.")]
    [Range(0.05f, 2f)]
    [SerializeField] float carScale = 0.35f;

    [Header("Uślizg")]
    [Tooltip("0 = pełna przyczepność (brak uślizgu), 1 = lód (cała prędkość boczna zostaje).")]
    [Range(0f, 1f)]
    [SerializeField] float driftFactor = 0.25f;

    [Header("Bezczynność")]
    [Tooltip("Po tylu sekundach postoju auto ginę, żeby pokolenie nie wisiało w nieskończoność.")]
    [SerializeField] float maxIdleTime = 4f;

    [SerializeField] float idleSpeedThreshold = 0.15f;

    [Tooltip("Jeśli auto nie zaliczy żadnej bramy przez tyle sekund, ginie (kończy tkwienie na starcie).")]
    [SerializeField] float maxTimeWithoutCheckpoint = 7f;

    [Header("Kierunek")]
    [Tooltip("Ile skrętu bierzemy z kąta do następnej bramy (0 = tylko sieć, 1 = mocna asysta).")]
    [Range(0f, 1f)]
    [SerializeField] float headingAssist = 0.2f;

    [Tooltip("Przez pierwsze sekundy życia: mix sieci i celowania w checkpoint.")]
    [Range(0f, 1f)]
    [SerializeField] float launchHeadingAssist = 0.55f;

    [Tooltip("Jak długo trwa asysta startu (sekundy). Potem steruje głównie sieć.")]
    [SerializeField] float launchDuration = 1.2f;

    [Tooltip("Limit prędkości tylko podczas asysty startu.")]
    [SerializeField] float launchMaxSpeed = 5f;

    [Tooltip("Awaryjne odpychanie od ścian, tylko gdy czujnik jest bardzo blisko.")]
    [Range(0f, 1f)]
    [SerializeField] float wallAvoidStrength = 0.7f;

    Rigidbody2D rb;
    CarSensors sensors;
    SpriteRenderer spriteRenderer;
    Color aliveColor;

    float accelerationInput;
    float steeringInput;
    float idleTimer;
    float noCheckpointTimer;
    float aliveTime;
    float lapStartTime;
    Vector2 lastPosition;
    bool isDead;
    int generationIndex = 1;

    /// <summary>Sieć neuronowa przypisana przez PopulationManager (nie serializowana w prefabie).</summary>
    public NeuralNetwork Brain { get; set; }

    public CarSensors Sensors => sensors;

    public bool IsDead => isDead;

    /// <summary>Ustawiane przez PopulationManager — asysta słabnie w kolejnych pokoleniach.</summary>
    public void SetGeneration(int generation)
    {
        generationIndex = Mathf.Max(1, generation);
    }

    /// <summary>5 czujników + kąt do następnej bramy + bliskość bramy.</summary>
    public const int NetworkInputCount = CarSensors.SensorCount + 2;

    [Header("Tor")]
    [Tooltip("Nagroda za zaliczenie checkpointu.")]
    [SerializeField] float checkpointReward = 12f;

    [Tooltip("Nagroda za przejechanie mety (okrążenie).")]
    [SerializeField] float lapReward = 40f;

    [Tooltip("Mnożnik dystansu przez prędkość. 0 = tylko droga, wyższe = szybsze auto ma wyższy fitness.")]
    [SerializeField] float speedFitness = 0.2f;

    [Tooltip("Docelowy czas okrążenia (s). Szybsze okrążenie dostaje bonus.")]
    [SerializeField] float targetLapTime = 14f;

    [Tooltip("Bonus za każdą sekundę poniżej Target Lap Time.")]
    [SerializeField] float lapTimeBonus = 8f;

    [Tooltip("Po tylu okrążeniach auto kończy (pokolenie może się zrotować). 0 = bez limitu.")]
    [SerializeField] int maxLaps = 2;

    [Tooltip("Dystans, przy którym wejście 'bliskość bramy' spada do 0.")]
    [SerializeField] float navDistance = 8f;

    readonly float[] networkInputs = new float[NetworkInputCount];
    int nextCheckpointIndex;
    int laps;

    /// <summary>Suma oceny: jazda w stronę następnej bramy + bonusy za bramy i metę.</summary>
    public float Fitness { get; private set; }

    public int Laps => laps;
    public float LastLapTime { get; private set; }
    public float BestLapTime { get; private set; }

    /// <summary>Wywoływane po zaliczeniu pełnego okrążenia (czas w sekundach gry).</summary>
    public event System.Action<CarController, float> LapCompleted;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        sensors = GetComponent<CarSensors>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            if (spriteRenderer.sprite == null)
                spriteRenderer.sprite = FindCarSprite();
            if (spriteRenderer.sprite != null)
                aliveColor = spriteRenderer.color;
        }

        var box = GetComponent<BoxCollider2D>();
        if (box != null && spriteRenderer != null && spriteRenderer.sprite != null)
            box.size = spriteRenderer.sprite.bounds.size * 0.9f;

        ApplyScale(carScale);

        // Top-down: brak grawitacji, płynna interpolacja, ciągłe kolizje przy wyższych prędkościach.
        rb.gravityScale = 0f;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.linearDamping = 0.4f;
        rb.angularDamping = 2f;
    }

    /// <summary>Ustawia skalę auta (wywołuje też PopulationManager przy spawnie).</summary>
    public void ApplyScale(float scale)
    {
        carScale = Mathf.Clamp(scale, 0.05f, 2f);
        transform.localScale = Vector3.one * carScale;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        transform.localScale = Vector3.one * Mathf.Clamp(carScale, 0.05f, 2f);
    }
#endif

    static Sprite FindCarSprite()
    {
#if UNITY_EDITOR
        Sprite fromPath = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/CarSprite.png");
        if (fromPath != null)
            return fromPath;
#endif
        Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null && (sprites[i].name == "CarSprite" || sprites[i].name == "CarSprite_0"))
                return sprites[i];
        }

        return null;
    }

    void Start()
    {
        lastPosition = rb.position;
        nextCheckpointIndex = 0;
        laps = 0;
        noCheckpointTimer = 0f;
        aliveTime = 0f;
        lapStartTime = 0f;
    }

    /// <summary>
    /// 5 raycastów + kąt do następnego checkpointu [-1, 1] + bliskość [0, 1].
    /// </summary>
    public float[] GetNetworkInputs()
    {
        float[] sensorsReadings = sensors != null ? sensors.GetReadings() : null;
        int sensorCount = CarSensors.SensorCount;
        for (int i = 0; i < sensorCount; i++)
            networkInputs[i] = sensorsReadings != null ? sensorsReadings[i] : 1f;

        networkInputs[sensorCount] = 0f;
        networkInputs[sensorCount + 1] = 0f;

        Checkpoint next = TrackCircuit.Instance != null ? TrackCircuit.Instance.Get(nextCheckpointIndex) : null;
        if (next != null)
        {
            Vector2 target = next.transform.position;
            bool clear = HasClearPath(target);
            if (clear)
            {
                Vector2 toNext = target - rb.position;
                float angle = Vector2.SignedAngle(transform.up, toNext);
                networkInputs[sensorCount] = Mathf.Clamp(angle / 90f, -1f, 1f);
                networkInputs[sensorCount + 1] = 1f - Mathf.Clamp01(toNext.magnitude / navDistance);
            }

            if (Time.timeScale <= 1.5f)
                Debug.DrawLine(transform.position, target, clear ? Color.cyan : Color.red, Time.fixedDeltaTime * 2f);
        }

        return networkInputs;
    }

    /// <summary>Wywoływane przez bramę. Liczy się tylko następna w kolejności.</summary>
    public void NotifyCheckpoint(Checkpoint checkpoint)
    {
        if (isDead || checkpoint == null)
            return;

        TrackCircuit circuit = TrackCircuit.Instance;
        if (circuit == null || circuit.Count == 0)
            return;

        Checkpoint expected = circuit.Get(nextCheckpointIndex);
        if (expected == null || checkpoint != expected)
            return;

        Fitness += checkpointReward + rb.linearVelocity.magnitude * speedFitness * 4f;
        nextCheckpointIndex++;
        noCheckpointTimer = 0f;

        if (checkpoint.IsFinish || nextCheckpointIndex >= circuit.Count)
        {
            float lapTime = Mathf.Max(0.01f, aliveTime - lapStartTime);
            float timeBonus = lapTimeBonus * Mathf.Max(0f, targetLapTime - lapTime);
            Fitness += lapReward + timeBonus;
            laps++;
            LastLapTime = lapTime;
            if (BestLapTime <= 0f || lapTime < BestLapTime)
                BestLapTime = lapTime;
            nextCheckpointIndex = 0;
            lapStartTime = aliveTime;
            LapCompleted?.Invoke(this, lapTime);

            if (maxLaps > 0 && laps >= maxLaps)
                Die();
        }
    }

    /// <summary>
    /// Wejścia z sieci. acceleration: -1..1 jest mapowane na gaz do przodu (0..1),
    /// żeby losowe sieci nie jeździły w kółko przód–tył.
    /// steering: -1 lewo, +1 prawo.
    /// </summary>
    public void SetInputs(float acceleration, float steering)
    {
        accelerationInput = Mathf.Clamp(acceleration, -1f, 1f);
        steeringInput = Mathf.Clamp(steering, -1f, 1f);
    }

    void FixedUpdate()
    {
        if (isDead)
            return;

        aliveTime += Time.fixedDeltaTime;
        ApplyDrive();
        ApplySteering();
        ApplyDrift();
        ClampSpeed();
        AccumulateFitness();
        CheckIdleDeath();
        CheckCheckpointTimeout();
    }

    bool IsLaunching => aliveTime < launchDuration;

    /// <summary>Asysta słabnie z pokoleniami, żeby sieć miała co uczyć.</summary>
    float AssistScale => Mathf.Clamp(1f / (1f + 0.28f * (generationIndex - 1)), 0.12f, 1f);

    void ApplyDrive()
    {
        // tanh [-1,1] → gaz [0,1]. Ujemny output to lekki gaz, nie wstecz.
        float throttle = (accelerationInput + 1f) * 0.5f;

        float[] r = sensors != null ? sensors.LastReadings : null;
        if (r != null && r.Length >= 5)
        {
            float frontGap = r[2];
            const float brakeFrom = 0.22f;
            if (frontGap < brakeFrom)
                throttle *= Mathf.Clamp01(frontGap / brakeFrom);
        }

        if (IsLaunching)
            throttle = Mathf.Min(throttle, 0.55f);

        rb.AddForce((Vector2)transform.up * throttle * accelerationForce);
    }

    void ApplySteering()
    {
        float speed = rb.linearVelocity.magnitude;
        float speedFactor = Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(speed / 1.2f));

        float steer = steeringInput;
        Checkpoint next = TrackCircuit.Instance != null ? TrackCircuit.Instance.Get(nextCheckpointIndex) : null;
        float assistBlend = (IsLaunching ? launchHeadingAssist : headingAssist) * AssistScale;
        if (next != null && assistBlend > 0f && HasClearPath(next.transform.position))
        {
            Vector2 toNext = (Vector2)next.transform.position - rb.position;
            float angle = Vector2.SignedAngle(transform.up, toNext);
            float assist = Mathf.Clamp(-angle / 90f, -1f, 1f);
            steer = Mathf.Lerp(steer, assist, assistBlend);
        }

        steer = ApplyEmergencyWallAvoid(steer);

        float rotationDelta = -steer * turnSpeed * speedFactor * Time.fixedDeltaTime;
        rb.MoveRotation(rb.rotation + rotationDelta);
    }

    float ApplyEmergencyWallAvoid(float steer)
    {
        float[] r = sensors != null ? sensors.LastReadings : null;
        if (r == null || r.Length < 5 || wallAvoidStrength <= 0f)
            return steer;

        const float emergencyGap = 0.2f;
        float leftGap = Mathf.Min(r[0], r[1]);
        float rightGap = Mathf.Min(r[3], r[4]);
        float frontGap = r[2];
        float closest = Mathf.Min(leftGap, Mathf.Min(rightGap, frontGap));
        if (closest >= emergencyGap)
            return steer;

        float avoid = Mathf.Clamp((1f - leftGap) - (1f - rightGap), -1f, 1f);
        float mix = Mathf.InverseLerp(emergencyGap, 0.04f, closest) * wallAvoidStrength * AssistScale;
        return Mathf.Lerp(steer, avoid, mix);
    }

    /// <summary>
    /// Zabija składową ortogonalną (boczną) prędkości.
    /// Im wyższy <see cref="driftFactor"/>, tym więcej uślizgu zostaje.
    /// </summary>
    void ApplyDrift()
    {
        Vector2 forward = transform.up;
        Vector2 right = transform.right;

        Vector2 forwardVelocity = forward * Vector2.Dot(rb.linearVelocity, forward);
        Vector2 rightVelocity = right * Vector2.Dot(rb.linearVelocity, right);

        rb.linearVelocity = forwardVelocity + rightVelocity * driftFactor;
    }

    void ClampSpeed()
    {
        float cap = maxSpeed;
        if (IsLaunching)
            cap = Mathf.Min(cap, launchMaxSpeed);

        if (rb.linearVelocity.magnitude > cap)
            rb.linearVelocity = rb.linearVelocity.normalized * cap;
    }

    void AccumulateFitness()
    {
        Vector2 delta = rb.position - lastPosition;
        lastPosition = rb.position;

        Checkpoint next = TrackCircuit.Instance != null ? TrackCircuit.Instance.Get(nextCheckpointIndex) : null;
        if (next == null)
        {
            Fitness += delta.magnitude;
            return;
        }

        Vector2 toNext = (Vector2)next.transform.position - rb.position;
        if (toNext.sqrMagnitude < 0.0001f)
            return;

        if (!HasClearPath(next.transform.position))
        {
            Fitness += Mathf.Max(0f, Vector2.Dot(delta, (Vector2)transform.up)) * 0.35f;
            return;
        }

        float toward = Vector2.Dot(delta, toNext.normalized);
        if (toward <= 0f)
            return;

        float speedScale = 1f + speedFitness * rb.linearVelocity.magnitude;
        Fitness += toward * speedScale;
    }

    static int WallMask => LayerMask.GetMask("Wall");

    bool HasClearPath(Vector2 target)
    {
        Vector2 origin = rb.position;
        Vector2 delta = target - origin;
        float dist = delta.magnitude;
        if (dist < 0.08f)
            return true;

        RaycastHit2D hit = Physics2D.Raycast(origin, delta / dist, dist, WallMask);
        return hit.collider == null;
    }

    void CheckIdleDeath()
    {
        if (rb.linearVelocity.magnitude < idleSpeedThreshold)
            idleTimer += Time.fixedDeltaTime;
        else
            idleTimer = 0f;

        if (idleTimer >= maxIdleTime)
            Die();
    }

    void CheckCheckpointTimeout()
    {
        noCheckpointTimer += Time.fixedDeltaTime;
        if (noCheckpointTimer >= maxTimeWithoutCheckpoint)
            Die();
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        if (collision.collider.CompareTag("Wall"))
            Die();
    }

    /// <summary>Zatrzymuje auto i blokuje dalszy ruch. Wywoływane też przez menedżera (timeout bezczynności).</summary>
    public void Die()
    {
        if (isDead)
            return;

        isDead = true;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        rb.bodyType = RigidbodyType2D.Kinematic;

        if (spriteRenderer != null)
            spriteRenderer.color = new Color(aliveColor.r, aliveColor.g, aliveColor.b, 0.35f);
    }

    /// <summary>Przywraca stan sprzed śmierci — używane przy recyklingu, obecnie menedżer niszczy instancje.</summary>
    public void ResetForNewGeneration(Vector2 position, float rotation)
    {
        isDead = false;
        Fitness = 0f;
        idleTimer = 0f;
        accelerationInput = 0f;
        steeringInput = 0f;
        nextCheckpointIndex = 0;
        laps = 0;
        LastLapTime = 0f;
        BestLapTime = 0f;
        noCheckpointTimer = 0f;
        aliveTime = 0f;
        lapStartTime = 0f;

        rb.bodyType = RigidbodyType2D.Dynamic;
        rb.position = position;
        rb.rotation = rotation;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;
        lastPosition = position;

        if (spriteRenderer != null)
            spriteRenderer.color = aliveColor;
    }
}
