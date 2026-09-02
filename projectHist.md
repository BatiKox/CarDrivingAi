# Historia projektu — CarDrivingAi

Gra 2D (Unity 6, URP), w której populacja samochodów uczy się jeździć po torze
prostym algorytmem genetycznym + siecią neuronową typu feedforward.

---

## 2026-09-02 — Start projektu (kroki 1–5)

### Stan wyjściowy

- Unity `6000.4.10f1`, scena `Assets/Scenes/SampleScene` (kamera ortograficzna + Global Light 2D).
- Brak skryptów w `Assets`.
- Grafiki: `Assets/Trace.png` (tor 1920×1080, szary asfalt, niebieskie tło, czarna krawędź)
  oraz `Assets/CarSprite.png` (auto z góry, przód = góra tekstury = `transform.up`).
- Tag i warstwa `Wall` są już w `TagManager` (User Layer 6).

### Ważna decyzja: collider toru

`Trace.png` jest **w całości nieprzezroczysty** (niebieskie tło to pełne piksele, nie alfa).
Unity `PolygonCollider2D` zbudowałby jeden wielki prostokąt, a nie krawędzie jezdni.

**Rozwiązanie:** `TrackWallBaker` (w `CarSensors.cs`) w Play Mode śledzi krawędź szarej jezdni
i tworzy `EdgeCollider2D` na warstwie/tagu `Wall`. Tor jest tylko grafiką.

---

## 2026-09-02 — Setup sceny przez Unity MCP

Scena `SampleScene` została złożona z Inspectora przez MCP (nie ręcznie):

| Obiekt | Co ustawione |
|---|---|
| `Track` | sprite toru, tag+layer `Wall`, `TrackWallBaker` |
| `CarPrototype` | SpriteRenderer, BoxCollider2D, Rigidbody2D (gravity 0, continuous), CarController, CarSensors; wyłączony w Play (szablon do Instantiate) |
| `SpawnPoint` | punkt startu (ok. 0, -3.2) — **przesuń na środek jezdni**, jeśli auto startuje w trawie |
| `PopulationManager` | skrypt menedżera; prefab/spawn znajdowane automatycznie po nazwach |
| Main Camera | orthographic size 5.6 |

Sprite auta MCP nie umiał podpiąć jako `Sprite` (ładował Texture2D). `CarController.Awake` ładuje `Assets/CarSprite.png` przez AssetDatabase.

Na kamerze i torze został pusty `EditorSetupRunner` — można go usunąć z Inspectora (nie wpływa na grę).

### Architektura skryptów (`Assets/Scripts/`)

```
PopulationManager  →  spawnuje populację, pętla ewolucji
        │
        ├── CarController   fizyka + śmierć + fitness
        ├── CarSensors      5× Physics2D.Raycast (warstwa Wall)
        └── NeuralNetwork   czysta klasa C# (nie MonoBehaviour)
```

Moduły nie mieszają odpowiedzialności:

| Klasa | Rola |
|---|---|
| `CarController` | `Rigidbody2D`, `SetInputs(acc, steer)`, drift, `IsDead`, `Fitness` |
| `CarSensors` | 5 raycastów, tablica `[0..1]`, `Debug.DrawRay` |
| `NeuralNetwork` | wagi + bias, tanh, mutate, crossover |
| `PopulationManager` | spawn, pętla myśląca, selekcja, nowe pokolenie |

### Fizyka (`CarController`)

- Widok top-down: `gravityScale = 0`, interpolacja, continuous collision.
- Wejście tylko przez `SetInputs(float acceleration, float steering)` w zakresie `[-1, 1]`.
- Przód = `transform.up` (zgodnie ze sprite'em).
- **Drift:** `v = v_przód + v_bok * driftFactor`.
  - `0` = pełna przyczepność (zerowanie prędkości bocznej),
  - `1` = lód (cała składowa ortogonalna zostaje).
  - Domyślnie `0.25`.
- **Śmierć:** `OnCollisionEnter2D` + tag `Wall` → `isDead`, zerowanie prędkości, `bodyType = Kinematic`.
- Extra (żeby pokolenie nie wisiało): śmierć po `maxIdleTime` (domyślnie 4 s) bez ruchu.
- Unity 6: `linearVelocity` / `linearDamping` (nie stare `velocity` / `drag`).

### Czujniki (`CarSensors`)

- Kąty względem przodu: **-90, -45, 0, 45, 90**.
- Maskowanie: warstwa `Wall` (`LayerMask`).
- Normalizacja: `dystans / maxDistance`, brak trafienia = `1`.
- Wizualizacja: `Debug.DrawRay` (czerwony = blisko, zielony = daleko) + gizmos w Scene.

### Sieć (`NeuralNetwork`)

- Architektura: **5 → 26 → 2** (liczba ukrytych konfigurowalna w menedżerze).
- Aktywacja: **tanh** na warstwie ukrytej i wyjściowej (wyjścia w `[-1, 1]`).
- Wagi i **biasy** losowane z `[-1, 1]`.
- Mutacja: każda waga/bias z szansą `mutationRate` dostaje szum `±mutationStrength`.
- Krzyżowanie: **jednorodne** (każdy gen osobno od rodzica A albo B, 50/50).
- Elityzm: N najlepszych sieci idzie do następnego pokolenia bez mutacji.

### Algorytm genetyczny (`PopulationManager`)

1. Spawn `populationSize` aut z prefabu w `spawnPoint`.
2. `FixedUpdate`: czujniki → `FeedForward` → `SetInputs`.
3. Fitness = **suma przebytej drogi** (nie odległość euklidesowa od startu,
   żeby pętla toru nie zerowała oceny).
4. Gdy wszystkie `IsDead`: sortowanie, Top `elitePercent` (domyślnie 10%) jako rodzice,
   krzyżowanie + mutacja, `generation++`, nowy spawn.

### Ustawienia Inspectora (domyślne)

- Populacja: 120
- Hidden: 26
- Elite: 10% rodziców, 2 kopie elitarne
- Mutacja: 12% szansy, siła 0.4
- Zasięg czujnika: 4 jednostki świata

### Świadomie odłożone

- Checkpointy / jazda w „dobrym” kierunku po torze (fitness nie rozróżnia sensu jazdy).
- Zapis najlepszej sieci na dysk.
- Kamera śledząca lidera.

### Pliki dodane

- `Assets/Scripts/CarController.cs`
- `Assets/Scripts/CarSensors.cs` (tu też `TrackWallBaker`)
- `Assets/Scripts/NeuralNetwork.cs`
- `Assets/Scripts/PopulationManager.cs`
- `Assets/Scripts/Editor/SceneBootstrap.cs`
- `projectHist.md` (ten plik)

---

## 2026-09-02 — Rozmiar aut i brak kolizji auto–auto

- `PopulationManager.carScale` (domyślnie 0.35) skaluje całą populację. Ten sam suwak jest na `CarController` (podgląd prototypu).
- Warstwa `Car` (User Layer 7). `Physics2D.IgnoreLayerCollision(Car, Car)` — auta przenikają się, nadal biją o `Wall`.
- Auta przy spawnie dostają warstwę `Car`.

---

## 2026-09-02 — Start, meta, kierunek jazdy

- `Checkpoint` + `TrackCircuit`: bramy w kolejności `order`. Ostatnia z `isFinish` = meta (okrążenie).
- Sieć ma **7 wejść**: 5 czujników + kąt do następnej bramy + bliskość.
- Fitness nagradza tylko jazdę **w stronę** następnej bramy (jazda pod prąd nie daje punktów).
- `SpawnPoint` = miejsce i obrót startu (zielona oś Y = przód auta).

---

## 2026-09-02 — Anty „przód–tył na starcie”

- Gaz z sieci mapowany na **tylko przód** (ujemny output = słaby gaz, nie wstecz).
- Skręt działa już na małej prędkości + **Heading Assist** (skręt w stronę następnej bramy).
- Brak bramy przez 7 s = śmierć. Lekki rozrzut spawnu.

---

## 2026-09-02 — Mniej rozbić przed pierwszą bramą

Losowe sieci natychmiast skręcają w bandę; dodatkowe checkpointy tego nie zmieniają.
Do zaliczenia CP_0: mocniejszy mix w stronę bramy (`launchHeadingAssist`), limit prędkości (`launchMaxSpeed`),
hamulec przy ścianie z przodu i odpychanie od ścian z czujników (`wallAvoidStrength`).
Spawn jitter tylko w poprzek jezdni (mniejszy).

---

## 2026-09-02 — Zastój po kilku pokoleniach

Asysta (heading + unik ścian) prowadziła wszystkie auta tą samą linią, więc mutacja sieci nie zmieniała zachowania.
Asysta startu trwa tylko `launchDuration` (1.2 s) i słabnie z pokoleniem. Unik ścian tylko awaryjnie (czujnik < 0.2).
Genetyka: 12% losowych imigrantów, selekcja proporcjonalna do fitness, silniejsza mutacja przy zastoju.
Na `PopulationManager` mutacja w scenie 0.15 / 0.35 (było 0.4 / 0.4 — za mocno mieszało wagi).

---

## 2026-09-02 — Tempo i koniec pokolenia

Dwa auta mogły jechać okrążenia w nieskończoność — pokolenie nigdy się nie kończyło, więc nie było potomków.
`maxLaps` (2) kończy auto po okrążeniach. `generationTimeLimit` (80 s) zabija resztę.
Fitness nagradza prędkość: dystans × (1 + speed × speedFitness) oraz bonus za okrążenie szybsze niż `targetLapTime`.

---

## 2026-09-02 — Timelapse pokoleń

`PopulationManager.simulationSpeed` ustawia `Time.timeScale` (1–20×). Limit pokolenia liczy się w czasie gry, więc 4× skraca oczekiwanie w czasie rzeczywistym.
Powyżej 1.5× wyłączone są `Debug.Draw*`, żeby 200 aut nie dusiło klatek. Suwak też na ekranie w Play.

---

## 2026-09-02 — Skręt w bandę po bramie (LOS)

Jeśli linia do następnej bramy przecina `Wall`, nie dajemy kąta/asysty/fitness w tamtą stronę (czerwona linia).
Inaczej populacja uczy się ścinać zakręt prosto w ścianę (typowe po CP_12 → CP_13).

---

## 2026-09-02 — Logi i rekord okrążenia

Każda sesja Play zapisuje CSV w `TrainingLogs/training_*.csv` (start, każde okrążenie, koniec pokolenia).
HUD pokazuje najszybsze okrążenie w tym pokoleniu i rekord sesji.
