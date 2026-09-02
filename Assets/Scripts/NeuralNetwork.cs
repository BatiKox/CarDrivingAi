using UnityEngine;

/// <summary>
/// Prosta sieć typu feedforward (wielowarstwowy perceptron).
/// Domyślna architektura: 5 wejść (czujniki) → warstwa ukryta → 2 wyjścia (gaz, skręt).
/// Funkcja aktywacji: tanh, więc wyjścia naturalnie wpadają w [-1, 1].
/// Nie dziedziczy po MonoBehaviour — to czysty model wag, którym zarządza PopulationManager.
/// </summary>
[System.Serializable]
public class NeuralNetwork
{
    /// <summary>Liczba neuronów w kolejnych warstwach, np. [5, 26, 2].</summary>
    public readonly int[] Layers;

    /// <summary>
    /// Wagi: weights[warstwaDocelowa][neuron][neuronZPoprzedniej].
    /// warstwaDocelowa zaczyna się od 1 (warstwa 0 to wejścia, nie ma do niej wag).
    /// </summary>
    float[][][] weights;

    /// <summary>Bias każdego neuronu (poza warstwą wejściową).</summary>
    float[][] biases;

    /// <summary>Bufor aktywacji — alokowany raz, żeby feedforward nie tworzył śmieci GC.</summary>
    float[][] neurons;

    public NeuralNetwork(int[] layers)
    {
        Layers = (int[])layers.Clone();
        Allocate();
        InitializeRandom();
    }

    /// <summary>Głęboka kopia (elityzm / klon rodzica przed mutacją).</summary>
    public NeuralNetwork(NeuralNetwork other)
    {
        Layers = (int[])other.Layers.Clone();
        Allocate();
        CopyWeightsFrom(other);
    }

    public NeuralNetwork Clone()
    {
        return new NeuralNetwork(this);
    }

    void Allocate()
    {
        int layerCount = Layers.Length;
        neurons = new float[layerCount][];
        weights = new float[layerCount][][];
        biases = new float[layerCount][];

        for (int l = 0; l < layerCount; l++)
        {
            neurons[l] = new float[Layers[l]];

            if (l == 0)
                continue;

            biases[l] = new float[Layers[l]];
            weights[l] = new float[Layers[l]][];
            for (int n = 0; n < Layers[l]; n++)
                weights[l][n] = new float[Layers[l - 1]];
        }
    }

    /// <summary>Losowe wagi i biasy w zakresie [-1, 1].</summary>
    public void InitializeRandom()
    {
        for (int l = 1; l < Layers.Length; l++)
        {
            for (int n = 0; n < Layers[l]; n++)
            {
                biases[l][n] = Random.Range(-1f, 1f);
                for (int p = 0; p < Layers[l - 1]; p++)
                    weights[l][n][p] = Random.Range(-1f, 1f);
            }
        }

        // Wyjście 0 = gaz: lekki plus, żeby pierwsze pokolenie jechało do przodu, a nie stało.
        int last = Layers.Length - 1;
        if (Layers[last] >= 1)
            biases[last][0] = Random.Range(0.35f, 1f);
    }

    /// <summary>
    /// Przepuszcza wektor wejść przez sieć.
    /// Zwraca tablicę wyjść o długości ostatniej warstwy (dla auta: [przyspieszenie, skręt]).
    /// </summary>
    public float[] FeedForward(float[] inputs)
    {
        int inputCount = Layers[0];
        if (inputs == null || inputs.Length != inputCount)
        {
            Debug.LogError($"NeuralNetwork.FeedForward: oczekiwano {inputCount} wejść, otrzymano {inputs?.Length ?? 0}.");
            return neurons[Layers.Length - 1];
        }

        for (int i = 0; i < inputCount; i++)
            neurons[0][i] = inputs[i];

        for (int l = 1; l < Layers.Length; l++)
        {
            for (int n = 0; n < Layers[l]; n++)
            {
                float sum = biases[l][n];
                for (int p = 0; p < Layers[l - 1]; p++)
                    sum += weights[l][n][p] * neurons[l - 1][p];

                neurons[l][n] = (float)System.Math.Tanh(sum);
            }
        }

        return neurons[Layers.Length - 1];
    }

    /// <summary>
    /// Mutacja: każdy parametr z szansą <paramref name="mutationRate"/>
    /// dostaje losowy szum z zakresu [-mutationStrength, mutationStrength].
    /// </summary>
    public void Mutate(float mutationRate, float mutationStrength)
    {
        mutationRate = Mathf.Clamp01(mutationRate);

        for (int l = 1; l < Layers.Length; l++)
        {
            for (int n = 0; n < Layers[l]; n++)
            {
                if (Random.value < mutationRate)
                    biases[l][n] += Random.Range(-mutationStrength, mutationStrength);

                for (int p = 0; p < Layers[l - 1]; p++)
                {
                    if (Random.value < mutationRate)
                        weights[l][n][p] += Random.Range(-mutationStrength, mutationStrength);
                }
            }
        }
    }

    /// <summary>
    /// Krzyżowanie jednorodne: każde waga/bias dziecka pochodzi losowo od A albo B (50/50).
    /// Rodzice nie są modyfikowani.
    /// </summary>
    public static NeuralNetwork Crossover(NeuralNetwork parentA, NeuralNetwork parentB)
    {
        if (!SameArchitecture(parentA, parentB))
        {
            Debug.LogError("NeuralNetwork.Crossover: rodzice mają różną architekturę.");
            return parentA.Clone();
        }

        NeuralNetwork child = new NeuralNetwork(parentA.Layers);
        for (int l = 1; l < child.Layers.Length; l++)
        {
            for (int n = 0; n < child.Layers[l]; n++)
            {
                child.biases[l][n] = Random.value < 0.5f
                    ? parentA.biases[l][n]
                    : parentB.biases[l][n];

                for (int p = 0; p < child.Layers[l - 1]; p++)
                {
                    child.weights[l][n][p] = Random.value < 0.5f
                        ? parentA.weights[l][n][p]
                        : parentB.weights[l][n][p];
                }
            }
        }

        return child;
    }

    static bool SameArchitecture(NeuralNetwork a, NeuralNetwork b)
    {
        if (a.Layers.Length != b.Layers.Length)
            return false;

        for (int i = 0; i < a.Layers.Length; i++)
        {
            if (a.Layers[i] != b.Layers[i])
                return false;
        }

        return true;
    }

    void CopyWeightsFrom(NeuralNetwork other)
    {
        for (int l = 1; l < Layers.Length; l++)
        {
            for (int n = 0; n < Layers[l]; n++)
            {
                biases[l][n] = other.biases[l][n];
                for (int p = 0; p < Layers[l - 1]; p++)
                    weights[l][n][p] = other.weights[l][n][p];
            }
        }
    }
}
