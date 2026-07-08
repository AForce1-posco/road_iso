using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents.Actuators;

/// <summary>
/// Unity Editor 없이도 로직 검증이 가능하도록 만든 독립 실행기.
/// Play하지 않아도 콘솔에서 실행 결과를 확인할 수 있다.
/// </summary>
public class PlacementStandaloneRunner
{
    public static void Run(int episodes = 3)
    {
        Debug.Log($"[PlacementStandaloneRunner] starting {episodes} episodes");

        var agent = new PlacementAgent();
        agent.SetupForRuntime();

        for (int ep = 0; ep < episodes; ep++)
        {
            agent.BeginEpisodeForRuntime();
            while (agent.IsEpisodeActive)
            {
                var actions = new ActionBuffers(new float[0], new int[3]);
                agent.Heuristic(actions);
                agent.OnActionReceived(actions);
            }
        }

        Debug.Log($"[PlacementStandaloneRunner] completed {episodes} episodes, reward={agent.CumulativeReward:F3}");
    }
}
