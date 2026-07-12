using UnityEngine;

/// <summary>
/// 정적 배치 피처(CG, 관성모멘트, 질량 등 13개) → 위험도 예측 (LightGBM, 500개 배치 학습).
/// RiskModel(XGBoost, 동적 프레임 단위)과는 완전히 다른 용도 —
/// 화물 로드 시점에 딱 1번 호출해서 "이 배치가 위험한지"를 시뮬레이션 없이 판단.
/// </summary>
[System.Serializable]
public class StaticRiskModelData
{
    public int nFeatures;
    public int[] featureIndex;
    public float[] threshold;
    public int[] leftChild;
    public int[] rightChild;
    public float[] leafValue;
    public int[] treeRoots;
    public string[] featureNames;
}

public class StaticRiskModel : MonoBehaviour
{
    private StaticRiskModelData data;
    private bool loaded = false;

    void Awake()
    {
        LoadModel();
    }

    void LoadModel()
    {
        TextAsset jsonFile = Resources.Load<TextAsset>("surrogate_risk_treedata");
        if (jsonFile == null)
        {
            Debug.LogError("surrogate_risk_treedata.json을 Resources 폴더에서 못 찾았습니다.");
            return;
        }
        data = JsonUtility.FromJson<StaticRiskModelData>(jsonFile.text);
        loaded = true;
        Debug.Log($"정적 위험도 모델 로드 완료 — 트리 {data.treeRoots.Length}개, 피처 {data.nFeatures}개");
    }

    /// <summary>
    /// 피처 순서 (원본 학습 스케일 그대로 넣을 것, 정규화 안 함):
    /// TargetSpeedKmh, RoadBankAngleDeg, RoadSlopeDeg, VehicleBaseMassKg,
    /// CargoCount, TotalMassKg, CogX, CogY, CogZ, MaxHeightM,
    /// InertiaXX, InertiaYY, InertiaZZ
    /// </summary>
    public float Predict(float[] x)
    {
        if (!loaded || data == null) return 0f;
        if (x.Length != data.nFeatures)
        {
            Debug.LogError($"피처 개수 불일치: 입력 {x.Length}개, 모델은 {data.nFeatures}개 필요");
            return 0f;
        }

        float total = 0f;  // LightGBM은 XGBoost와 달리 base_score 없이 트리 합만으로 예측
        foreach (int root in data.treeRoots)
        {
            int idx = root;
            while (data.featureIndex[idx] != -1)
            {
                // LightGBM 분기 규칙: x <= threshold 면 왼쪽, 아니면 오른쪽
                if (x[data.featureIndex[idx]] <= data.threshold[idx])
                    idx = data.leftChild[idx];
                else
                    idx = data.rightChild[idx];
            }
            total += data.leafValue[idx];
        }
        return Mathf.Clamp01(total);
    }
}