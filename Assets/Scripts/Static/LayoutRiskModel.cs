using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 배치(layout) → 위험도(리스크 점수) 예측기.
/// scikit-learn / XGBoost 등에서 뽑은 트리 앙상블을 JSON 트리 포맷으로 export해 순회 평가한다.
/// (export: scratchpad/export_rf_to_json.py → Assets/Resources/&lt;resourceName&gt;.json)
///
/// ⭐ 스왑 가능한 모듈: 더 좋은 모델이 나오면 <b>이 코드는 그대로 두고</b> Resources의 JSON만 교체하면 된다.
/// 단 경계(계약)는 <b>featureNames(입력 피처 순서)</b> 다 — Predict(Dictionary)로 이름 기준 정합을 보장한다.
///
/// 순회 규약(sklearn RandomForest export와 파리티 검증됨, max|diff|~1e-18):
///   total = baseScore; 각 트리 root에서 featureIndex[idx]==-1(leaf)까지 x[fi]&lt;thr ? left : right; total += leaf; clamp01.
///   (RF 평균은 export 시 leaf값을 트리수로 나눠 sum=mean이 되게 처리함)
/// </summary>
[System.Serializable]
public class LayoutRiskModelData
{
    public float baseScore;
    public int[] featureIndex;
    public float[] threshold;
    public int[] leftChild;
    public int[] rightChild;
    public float[] leafValue;
    public int[] treeRoots;
    public string[] featureNames;
}

public class LayoutRiskModel
{
    private LayoutRiskModelData data;
    private readonly HashSet<string> warnedMissing = new HashSet<string>();

    public bool Loaded { get; private set; }
    public string[] FeatureNames => data != null ? data.featureNames : null;

    public LayoutRiskModel(string resourceName = "layout_risk_treedata") => Load(resourceName);

    public void Load(string resourceName)
    {
        Loaded = false;
        var json = Resources.Load<TextAsset>(resourceName);
        if (json == null)
        {
            Debug.LogError($"[LayoutRiskModel] Resources/{resourceName}.json 을 못 찾음. export_rf_to_json.py 로 생성했는지 확인.");
            return;
        }
        data = JsonUtility.FromJson<LayoutRiskModelData>(json.text);
        Loaded = data != null && data.treeRoots != null && data.treeRoots.Length > 0
                 && data.featureNames != null && data.featureNames.Length > 0;
        if (Loaded)
            Debug.Log($"[LayoutRiskModel] 로드: 트리 {data.treeRoots.Length}, 노드 {data.featureIndex.Length}, 피처 {data.featureNames.Length} [{string.Join(", ", data.featureNames)}]");
        else
            Debug.LogError($"[LayoutRiskModel] {resourceName}.json 파싱 실패 또는 빈 모델.");
    }

    /// <summary>이름→값 맵으로 예측(피처 순서를 모델 쪽에 맞춰줌 = 스왑 안전). 없는 피처는 0(1회 경고).</summary>
    public float Predict(IDictionary<string, float> named)
    {
        if (!Loaded) return 0f;
        var x = new float[data.featureNames.Length];
        for (int i = 0; i < x.Length; i++)
        {
            if (!named.TryGetValue(data.featureNames[i], out x[i]))
            {
                x[i] = 0f;
                if (warnedMissing.Add(data.featureNames[i]))
                    Debug.LogWarning($"[LayoutRiskModel] 피처 '{data.featureNames[i]}' 미제공 → 0 사용");
            }
        }
        return Predict(x);
    }

    /// <summary>featureNames 순서에 맞춘 벡터로 예측.</summary>
    public float Predict(float[] x)
    {
        if (!Loaded) return 0f;
        float total = data.baseScore;
        foreach (int root in data.treeRoots)
        {
            int idx = root;
            while (data.featureIndex[idx] != -1)
                idx = x[data.featureIndex[idx]] < data.threshold[idx] ? data.leftChild[idx] : data.rightChild[idx];
            total += data.leafValue[idx];
        }
        return Mathf.Clamp01(total);
    }
}
