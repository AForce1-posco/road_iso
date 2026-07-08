using UnityEngine;

/// <summary>
/// 빈 GameObject에 붙여서 바로 테스트할 수 있는 실행용 래퍼.
/// 이 스크립트는 PlacementGAOptimizer를 찾고, 간단한 로그를 남긴다.
/// </summary>
public class PlacementGAOptimizerRuntime : MonoBehaviour
{
    public PlacementGAOptimizer optimizer;

    void Start()
    {
        if (optimizer == null)
            optimizer = FindObjectOfType<PlacementGAOptimizer>();

        if (optimizer == null)
        {
            Debug.LogWarning("[PlacementGAOptimizerRuntime] optimizer not found");
            return;
        }

        var best = optimizer.GetBestGenome();
        if (best != null)
        {
            optimizer.ApplyGenomeToAgent(best);
            Debug.Log($"[PlacementGAOptimizerRuntime] applied best -> lr={best.learningRate:F6}, stepScale={best.stepScale:F4}, support={best.supportRatioMin:F3}");
        }
    }
}
