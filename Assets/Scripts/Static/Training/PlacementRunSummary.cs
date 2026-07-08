using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// CSV 로그를 읽어서 최근 실행 요약(성공률, 평균 보상, 실패 수)을 콘솔에 출력한다.
/// </summary>
public class PlacementRunSummary : MonoBehaviour
{
    public PlacementTrainingRecorder recorder;
    public string csvFileName = "placement_training_log.csv";
    public bool printOnStart = true;
    public bool printOnEpisode = true;

    void Start()
    {
        if (recorder == null)
            recorder = FindObjectOfType<PlacementTrainingRecorder>();

        if (printOnStart)
            PrintSummary();
    }

    public void PrintSummary()
    {
        string path = Path.Combine(Application.dataPath, csvFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning("[PlacementRunSummary] no log file yet");
            return;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            Debug.LogWarning("[PlacementRunSummary] no episode rows yet");
            return;
        }

        var rewards = new List<float>();
        int completed = 0;
        int failed = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 2) continue;
            if (float.TryParse(parts[2], out float reward))
                rewards.Add(reward);
            if (parts[1] == "completed") completed++;
            if (parts[1] == "failed") failed++;
        }

        if (rewards.Count == 0)
        {
            Debug.LogWarning("[PlacementRunSummary] no reward values parsed");
            return;
        }

        float sum = 0f;
        foreach (var reward in rewards)
            sum += reward;

        float avg = rewards.Count > 0 ? sum / rewards.Count : 0f;
        float max = rewards.Count > 0 ? Mathf.Max(rewards.ToArray()) : 0f;
        float min = rewards.Count > 0 ? Mathf.Min(rewards.ToArray()) : 0f;
        float successRate = completed / (float)Mathf.Max(1, completed + failed) * 100f;

        Debug.Log($"[PlacementRunSummary] episodes={rewards.Count}, completed={completed}, failed={failed}, successRate={successRate:F1}%, avgReward={avg:F3}, maxReward={max:F3}, minReward={min:F3}");
    }
}
