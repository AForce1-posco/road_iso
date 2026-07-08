using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents.Actuators;
using UnityEngine;

/// <summary>
/// 간단한 Genetic Algorithm 기반 하이퍼파라미터 탐색기.
/// 현재는 보상/규제 파라미터를 PlacementAgent에 주입해 실제로 반영되는지
/// 확인하는 최소 프로토타입입니다.
/// </summary>
public class PlacementGAOptimizer : MonoBehaviour
{
    [Header("GA 설정")]
    public int populationSize = 6;
    public int generations = 4;
    public float mutationRate = 0.3f;
    public int evaluationEpisodes = 3;
    public float evaluationStepDelay = 0.0f;

    [Header("탐색 대상")]
    public float minLearningRate = 1e-4f;
    public float maxLearningRate = 5e-4f;
    public float minStepScale = 0.01f;
    public float maxStepScale = 0.2f;
    public float minSupportRatio = 0.6f;
    public float maxSupportRatio = 0.9f;
    public float minWLE = 0.2f;
    public float maxWLE = 0.8f;
    public float minWCGS = 0.2f;
    public float maxWCGS = 0.8f;
    public float minWSS = 0.05f;
    public float maxWSS = 0.2f;

    [Header("참조")]
    public PlacementAgent agent;

    private readonly List<PlacementGenome> population = new List<PlacementGenome>();

    void Start()
    {
        if (agent == null)
            agent = FindObjectOfType<PlacementAgent>();

        StartCoroutine(RunGA());
    }

    System.Collections.IEnumerator RunGA()
    {
        InitializePopulation();
        for (int gen = 0; gen < generations; gen++)
        {
            EvaluatePopulation();
            var best = GetBestGenome();
            if (best != null)
            {
                ApplyGenomeToAgent(best);
                Debug.Log($"[PlacementGAOptimizer] generation {gen + 1}/{generations} best fitness={best.fitness:F4}");
            }

            if (gen < generations - 1)
                EvolvePopulation();

            yield return null;
        }

        EvaluatePopulation();
        var finalBest = GetBestGenome();
        if (finalBest != null)
        {
            ApplyGenomeToAgent(finalBest);
            Debug.Log($"[PlacementGAOptimizer] 최종 best fitness={finalBest.fitness:F4}");
        }
    }

    void InitializePopulation()
    {
        population.Clear();
        for (int i = 0; i < populationSize; i++)
        {
            population.Add(CreateRandomGenome());
        }
    }

    void EvaluatePopulation()
    {
        for (int i = 0; i < population.Count; i++)
        {
            population[i].fitness = 0f;
        }

        for (int i = 0; i < population.Count; i++)
        {
            population[i].fitness = EvaluateGenome(population[i]);
            Debug.Log($"[GA] fitness={population[i].fitness:F4} stepScale={population[i].stepScale:F4} support={population[i].supportRatioMin:F3} wLE={population[i].wLE:F2} wCGS={population[i].wCGS:F2} wSS={population[i].wSS:F2}");
        }

        population.Sort((a, b) => b.fitness.CompareTo(a.fitness));
    }

    float EvaluateGenome(PlacementGenome genome)
    {
        if (agent == null)
            return 0f;

        agent.ApplyRuntimeConfig(new RewardConfig
        {
            wLE = genome.wLE,
            wCGS = genome.wCGS,
            wSS = genome.wSS,
            stepScale = genome.stepScale
        }, new RuleConfig
        {
            supportRatioMin = genome.supportRatioMin
        });

        float totalReward = 0f;
        int completed = 0;
        int failed = 0;
        int validPlacements = 0;
        int invalidPlacements = 0;
        var metrics = new List<PlacementEpisodeMetrics>();

        agent.EpisodeFinished += HandleEpisodeFinished;
        for (int ep = 0; ep < evaluationEpisodes; ep++)
        {
            agent.SetupForRuntime();
            agent.BeginEpisodeForRuntime();
            while (agent.IsEpisodeActive)
            {
                var actions = new ActionBuffers(new float[0], new int[3]);
                agent.Heuristic(actions);
                agent.OnActionReceived(actions);

                if (evaluationStepDelay > 0f)
                    System.Threading.Thread.Sleep(1);
            }
        }
        agent.EpisodeFinished -= HandleEpisodeFinished;

        foreach (var m in metrics)
        {
            totalReward += m.totalReward;
            completed += m.reason == "completed" ? 1 : 0;
            failed += m.reason == "failed" ? 1 : 0;
            validPlacements += m.validPlacements;
            invalidPlacements += m.invalidPlacements;
        }

        if (metrics.Count == 0)
            return 0f;

        float successRate = completed / (float)metrics.Count;
        float avgReward = totalReward / metrics.Count;
        float validRatio = metrics.Count > 0 ? (float)validPlacements / Mathf.Max(1, validPlacements + invalidPlacements) : 0f;
        float fitness = 0.5f * successRate + 0.3f * Mathf.Clamp01(avgReward / 5f) + 0.2f * validRatio;
        return Mathf.Clamp01(fitness);

        void HandleEpisodeFinished(PlacementEpisodeMetrics m)
        {
            metrics.Add(m);
        }
    }

    void EvolvePopulation()
    {
        var next = new List<PlacementGenome>();
        for (int i = 0; i < populationSize; i++)
        {
            PlacementGenome parentA = population[i % Mathf.Max(1, populationSize / 2)];
            PlacementGenome parentB = population[(i + 1) % Mathf.Max(1, populationSize / 2)];
            next.Add(Crossover(parentA, parentB));
        }

        population.Clear();
        population.AddRange(next);
        for (int i = 0; i < population.Count; i++)
            Mutate(population[i]);
    }

    PlacementGenome Crossover(PlacementGenome a, PlacementGenome b)
    {
        return new PlacementGenome
        {
            learningRate = Random.value < 0.5f ? a.learningRate : b.learningRate,
            stepScale = Random.value < 0.5f ? a.stepScale : b.stepScale,
            supportRatioMin = Random.value < 0.5f ? a.supportRatioMin : b.supportRatioMin,
            wLE = Random.value < 0.5f ? a.wLE : b.wLE,
            wCGS = Random.value < 0.5f ? a.wCGS : b.wCGS,
            wSS = Random.value < 0.5f ? a.wSS : b.wSS,
            fitness = 0f
        };
    }

    void Mutate(PlacementGenome genome)
    {
        if (Random.value < mutationRate)
            genome.learningRate = Random.Range(minLearningRate, maxLearningRate);
        if (Random.value < mutationRate)
            genome.stepScale = Random.Range(minStepScale, maxStepScale);
        if (Random.value < mutationRate)
            genome.supportRatioMin = Random.Range(minSupportRatio, maxSupportRatio);
        if (Random.value < mutationRate)
            genome.wLE = Random.Range(minWLE, maxWLE);
        if (Random.value < mutationRate)
            genome.wCGS = Random.Range(minWCGS, maxWCGS);
        if (Random.value < mutationRate)
            genome.wSS = Random.Range(minWSS, maxWSS);
    }

    PlacementGenome CreateRandomGenome()
    {
        return new PlacementGenome
        {
            learningRate = Random.Range(minLearningRate, maxLearningRate),
            stepScale = Random.Range(minStepScale, maxStepScale),
            supportRatioMin = Random.Range(minSupportRatio, maxSupportRatio),
            wLE = Random.Range(minWLE, maxWLE),
            wCGS = Random.Range(minWCGS, maxWCGS),
            wSS = Random.Range(minWSS, maxWSS),
            fitness = 0f
        };
    }

    public void ApplyGenomeToAgent(PlacementGenome genome)
    {
        if (genome == null || agent == null)
            return;

        var rewardCfg = new RewardConfig
        {
            wLE = genome.wLE,
            wCGS = genome.wCGS,
            wSS = genome.wSS,
            stepScale = genome.stepScale
        };

        var ruleCfg = new RuleConfig
        {
            supportRatioMin = genome.supportRatioMin
        };

        agent.ApplyRuntimeConfig(rewardCfg, ruleCfg);
        Debug.Log($"[PlacementGAOptimizer] applied genome -> lr={genome.learningRate:F6}, stepScale={genome.stepScale:F4}, support={genome.supportRatioMin:F3}, wLE={genome.wLE:F2}, wCGS={genome.wCGS:F2}, wSS={genome.wSS:F2}");
    }

    public PlacementGenome GetBestGenome()
    {
        if (population.Count == 0) return null;
        return population[0];
    }
}

[System.Serializable]
public class PlacementGenome
{
    public float learningRate;
    public float stepScale;
    public float supportRatioMin;
    public float wLE;
    public float wCGS;
    public float wSS;
    public float fitness;
}
