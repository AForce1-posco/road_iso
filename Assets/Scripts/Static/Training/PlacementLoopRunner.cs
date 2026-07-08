using System.Collections;
using Unity.MLAgents.Actuators;
using UnityEngine;

/// <summary>
/// PlacementAgent의 에피소드 루프를 Unity의 Update/Coroutine으로 직접 돌리는 런너.
/// ML-Agents의 자동 액션 루프에 의존하지 않고, 씬에서 즉시 확인할 수 있게 한다.
/// </summary>
public class PlacementLoopRunner : MonoBehaviour
{
    public PlacementAgent agent;
    public int episodeCount = 10;
    public float stepDelay = 0.01f;
    public bool runOnStart = true;

    private int completedEpisodes;

    void Start()
    {
        if (runOnStart)
            StartCoroutine(RunLoop());
    }

    public void Run()
    {
        completedEpisodes = 0;
        StopAllCoroutines();
        StartCoroutine(RunLoop());
    }

    private IEnumerator RunLoop()
    {
        if (agent == null)
            agent = FindObjectOfType<PlacementAgent>();

        if (agent == null)
        {
            Debug.LogError("[PlacementLoopRunner] PlacementAgent not found");
            yield break;
        }

        agent.SetupForRuntime();

        while (completedEpisodes < episodeCount)
        {
            agent.BeginEpisodeForRuntime();

            while (agent.IsEpisodeActive)
            {
                var actions = new ActionBuffers(new float[0], new int[3]);
                agent.Heuristic(actions);
                agent.OnActionReceived(actions);

                if (stepDelay > 0f)
                    yield return new WaitForSeconds(stepDelay);
                else
                    yield return null;
            }

            completedEpisodes++;
        }

        Debug.Log($"[PlacementLoopRunner] completed {completedEpisodes} episodes");
    }
}
