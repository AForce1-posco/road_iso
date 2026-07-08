using UnityEngine;

/// <summary>
/// 중간 평가용 간단한 로깅 유틸리티.
/// PPO/GA 단계마다 핵심 지표를 콘솔에 남긴다.
/// </summary>
public class PlacementEvaluationLogger : MonoBehaviour
{
    public PlacementAgent agent;

    public void LogCheckpoint(string stage, float reward, int validPlacements, int invalidPlacements, int episodeCount)
    {
        Debug.Log($"[Eval:{stage}] reward={reward:F4} valid={validPlacements} invalid={invalidPlacements} episodes={episodeCount}");
    }
}
