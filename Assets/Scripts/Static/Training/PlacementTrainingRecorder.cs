using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 에피소드 메트릭을 CSV로 기록해 시각화/분석에 쓸 수 있게 하는 기록기.
/// </summary>
public class PlacementTrainingRecorder : MonoBehaviour
{
    public PlacementAgent agent;
    public PlacementTrainingChart chart;
    public string outputFileName = "placement_training_log.csv";

    private readonly List<string> rows = new List<string>();
    private string outputPath;

    void Start()
    {
        if (agent == null)
            agent = FindObjectOfType<PlacementAgent>();

        if (agent != null)
            agent.EpisodeFinished += HandleEpisodeFinished;

        if (chart == null)
            chart = FindObjectOfType<PlacementTrainingChart>();

        outputPath = Path.Combine(Application.dataPath, outputFileName);
        File.WriteAllText(outputPath, "episodeIndex,reason,totalReward,validPlacements,invalidPlacements,stepCount,stepScale,wLE,wCGS,wSS,supportRatioMin\n");
        Debug.Log($"[PlacementTrainingRecorder] logging to {outputPath}");
    }

    void OnDestroy()
    {
        if (agent != null)
            agent.EpisodeFinished -= HandleEpisodeFinished;
    }

    void HandleEpisodeFinished(PlacementEpisodeMetrics m)
    {
        string row = string.Join(
            ",",
            m.episodeIndex.ToString(),
            m.reason,
            m.totalReward.ToString("F6"),
            m.validPlacements.ToString(),
            m.invalidPlacements.ToString(),
            m.stepCount.ToString(),
            m.stepScale.ToString("F6"),
            m.wLE.ToString("F6"),
            m.wCGS.ToString("F6"),
            m.wSS.ToString("F6"),
            m.supportRatioMin.ToString("F6")
        );
        rows.Add(row);
        File.AppendAllText(outputPath, row + "\n");

        if (chart != null)
            chart.Refresh();

        var summary = FindObjectOfType<PlacementRunSummary>();
        if (summary != null)
            summary.PrintSummary();

        Debug.Log($"[PlacementTrainingRecorder] {row}");
    }
}
