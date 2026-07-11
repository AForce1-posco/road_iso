using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class LSTMWeightsData
{
    public int inputSize;
    public int hiddenSize;
    public int windowSize;
    public float[] featureMean;
    public float[] featureStd;
    public float[] weightIh;
    public float[] weightHh;
    public float[] biasIh;
    public float[] biasHh;
    public float[] fcWeight;
    public float[] fcBias;
    public string[] featureNames;
}

public class EarlyWarningLSTM : MonoBehaviour
{
    private LSTMWeightsData data;
    private bool loaded = false;
    private Queue<float[]> buffer;

    void Awake()
    {
        LoadModel();
    }

    void LoadModel()
    {
        TextAsset jsonFile = Resources.Load<TextAsset>("lstm_weights");
        if (jsonFile == null)
        {
            Debug.LogError("lstm_weights.json을 Resources 폴더에서 못 찾았습니다.");
            return;
        }
        data = JsonUtility.FromJson<LSTMWeightsData>(jsonFile.text);
        buffer = new Queue<float[]>(data.windowSize);
        loaded = true;
        Debug.Log($"조기경보 LSTM 로드 완료 — hidden={data.hiddenSize}, window={data.windowSize}");
    }

    public float? PushAndPredict(float[] rawFeatures)
    {
        if (!loaded || data == null) return null;

        buffer.Enqueue(rawFeatures);
        if (buffer.Count > data.windowSize)
            buffer.Dequeue();

        if (buffer.Count < data.windowSize)
            return null;

        return Forward(buffer.ToArray());
    }

    float Forward(float[][] sequence)
    {
        int hidden = data.hiddenSize;
        int input = data.inputSize;

        float[] h = new float[hidden];
        float[] c = new float[hidden];

        foreach (float[] rawX in sequence)
        {
            float[] x = new float[input];
            for (int i = 0; i < input; i++)
                x[i] = (rawX[i] - data.featureMean[i]) / data.featureStd[i];

            float[] gates = new float[4 * hidden];
            for (int row = 0; row < 4 * hidden; row++)
            {
                float sum = data.biasIh[row] + data.biasHh[row];
                for (int col = 0; col < input; col++)
                    sum += data.weightIh[row * input + col] * x[col];
                for (int col = 0; col < hidden; col++)
                    sum += data.weightHh[row * hidden + col] * h[col];
                gates[row] = sum;
            }

            for (int j = 0; j < hidden; j++)
            {
                float i_gate = Sigmoid(gates[j]);
                float f_gate = Sigmoid(gates[hidden + j]);
                float g_gate = Tanh(gates[2 * hidden + j]);
                float o_gate = Sigmoid(gates[3 * hidden + j]);

                c[j] = f_gate * c[j] + i_gate * g_gate;
                h[j] = o_gate * Tanh(c[j]);
            }
        }

        float logit = data.fcBias[0];
        for (int j = 0; j < hidden; j++)
            logit += data.fcWeight[j] * h[j];

        return Sigmoid(logit);
    }

    float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));
    float Tanh(float x) => (float)System.Math.Tanh(x);
}