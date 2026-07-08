using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// CSV 로그를 읽어서 간단한 SVG 차트를 생성하는 시각화 유틸리티.
/// Unity Editor에서 실행하면 Assets/placement_training_chart.svg로 저장된다.
/// </summary>
public class PlacementTrainingChart : MonoBehaviour
{
    public string csvFileName = "placement_training_log.csv";
    public string outputSvgFileName = "placement_training_chart.svg";

    void Start()
    {
        Refresh();
    }

    public void Refresh()
    {
        string path = Path.Combine(Application.dataPath, csvFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning("[PlacementTrainingChart] log file not found yet");
            return;
        }

        var rows = File.ReadAllLines(path);
        if (rows.Length < 2)
        {
            Debug.LogWarning("[PlacementTrainingChart] no training rows found");
            return;
        }

        var values = new List<float>();
        for (int i = 1; i < rows.Length; i++)
        {
            var parts = rows[i].Split(',');
            if (parts.Length > 2 && float.TryParse(parts[2], out float v))
                values.Add(v);
        }

        if (values.Count == 0)
        {
            Debug.LogWarning("[PlacementTrainingChart] no reward values parsed");
            return;
        }

        string svg = BuildSvg(values);
        string outPath = Path.Combine(Application.dataPath, outputSvgFileName);
        File.WriteAllText(outPath, svg);
        Debug.Log($"[PlacementTrainingChart] wrote {outPath}");
    }

    string BuildSvg(List<float> values)
    {
        int width = 900;
        int height = 360;
        int margin = 40;
        int chartW = width - margin * 2;
        int chartH = height - margin * 2;

        float min = Mathf.Min(values.ToArray());
        float max = Mathf.Max(values.ToArray());
        if (Mathf.Abs(max - min) < 1e-6f) max = min + 1f;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<svg xmlns='http://www.w3.org/2000/svg' width='900' height='360'>");
        sb.AppendLine("<rect width='100%' height='100%' fill='white' />");
        sb.AppendLine($"<line x1='{margin}' y1='{height - margin}' x2='{width - margin}' y2='{height - margin}' stroke='black' />");
        sb.AppendLine($"<line x1='{margin}' y1='{margin}' x2='{margin}' y2='{height - margin}' stroke='black' />");

        for (int i = 0; i < values.Count; i++)
        {
            float x = margin + (chartW * i / (float)Mathf.Max(1, values.Count - 1));
            float y = height - margin - ((values[i] - min) / (max - min)) * chartH;
            sb.AppendLine($"<circle cx='{x:F1}' cy='{y:F1}' r='2' fill='royalblue' />");
        }

        for (int i = 1; i < values.Count; i++)
        {
            float x0 = margin + (chartW * (i - 1) / (float)Mathf.Max(1, values.Count - 1));
            float y0 = height - margin - ((values[i - 1] - min) / (max - min)) * chartH;
            float x1 = margin + (chartW * i / (float)Mathf.Max(1, values.Count - 1));
            float y1 = height - margin - ((values[i] - min) / (max - min)) * chartH;
            sb.AppendLine($"<line x1='{x0:F1}' y1='{y0:F1}' x2='{x1:F1}' y2='{y1:F1}' stroke='royalblue' stroke-width='2' />");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
