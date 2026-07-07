using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// XGBoost에서 뽑아낸 트리 앙상블을 순회해서 위험도(|LTR|)를 예측.
/// Assets/Resources/risk_model_treedata.json 을 로드해서 사용.
/// </summary>
[System.Serializable]
public class RiskModelData
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

public class RiskModel : MonoBehaviour
{
    private RiskModelData data;
    private bool loaded = false;

    void Awake()
    {
        LoadModel();
    }

    void LoadModel()
    {
        TextAsset jsonFile = Resources.Load<TextAsset>("risk_model_treedata");
        if (jsonFile == null)
        {
            Debug.LogError("risk_model_treedata.json을 Resources 폴더에서 못 찾았습니다.");
            return;
        }
        data = JsonUtility.FromJson<RiskModelData>(jsonFile.text);
        loaded = true;
        Debug.Log($"위험도 모델 로드 완료 — 트리 {data.treeRoots.Length}개, 노드 {data.featureIndex.Length}개");
    }

    /// <summary>
    /// 피처 순서: SpeedKmh, LatAcc, LongAcc, RollRate, YawRate, SteerAngle, MaxSideSlip
    /// </summary>
    public float Predict(float[] x)
    {
        if (!loaded || data == null) return 0f;

        float total = data.baseScore;
        foreach (int root in data.treeRoots)
        {
            int idx = root;
            while (data.featureIndex[idx] != -1)
            {
                if (x[data.featureIndex[idx]] < data.threshold[idx])
                    idx = data.leftChild[idx];
                else
                    idx = data.rightChild[idx];
            }
            total += data.leafValue[idx];
        }
        return Mathf.Clamp01(total); // |LTR|은 0~1 범위이므로 클램프
    }
}