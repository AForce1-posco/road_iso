using UnityEngine;

/// <summary>
/// 발표용 LSTM 전용 실시간 디스플레이.
/// 기존 RiskDisplay와 독립적으로 동작 — Inspector 체크박스로 켜고 끄면 됨.
/// 화면 오른쪽에 표시되어 RiskDisplay(왼쪽)와 안 겹침.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class LSTMPresentationDisplay : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    [Tooltip("이 디스플레이 전용 EarlyWarningLSTM 인스턴스 — RiskDisplay가 쓰는 것과 반드시 달라야 함")]
    public EarlyWarningLSTM lstmModel;

    [Header("샘플링 간격 (학습 데이터와 반드시 일치)")]
    public float sampleInterval = 0.1f;

    [Header("경보 임계값")]
    public float warnThreshold = 0.4f;
    public float dangerThreshold = 0.7f;

    [Header("착지/안정화 워밍업")]
    public float ignoreStartupSeconds = 1.5f;

    [Header("그래프 이력 길이 (0.1초 단위, 150=15초)")]
    public int historyLength = 150;

    [Header("화면 위치")]
    public int panelWidth = 320;
    public int marginRight = 20;
    public int marginTop = 20;

    private Rigidbody rb;
    private Vector3 lastVelocity;
    private Vector3 lastEuler;

    private float sampleTimer = 0f;
    private float lastSampleTime = 0f;
    private float runElapsed = 0f;

    private float? currentProb = null;
    private float[] probHistory;
    private int historyIndex = 0;
    private int historyCount = 0;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        probHistory = new float[historyLength];
    }

    void Start()
    {
        lastVelocity = rb.velocity;
        lastEuler = transform.eulerAngles;
        lastSampleTime = Time.time;
    }

    void FixedUpdate()
    {
        sampleTimer += Time.fixedDeltaTime;
        if (sampleTimer < sampleInterval) return;
        sampleTimer -= sampleInterval;

        float currentTime = Time.time;
        float dt = currentTime - lastSampleTime;
        if (dt <= 0.0001f) dt = sampleInterval;
        lastSampleTime = currentTime;
        runElapsed += dt;

        Vector3 vel = rb.velocity;
        Vector3 acc = (vel - lastVelocity) / dt;
        Vector3 localAcc = transform.InverseTransformDirection(acc);
        float longAcc = localAcc.z;
        float latAcc = localAcc.x;

        Vector3 euler = transform.eulerAngles;
        float rollRate = Mathf.DeltaAngle(lastEuler.z, euler.z) / dt;
        float yawRate = Mathf.DeltaAngle(lastEuler.y, euler.y) / dt;

        float speedKmh = vel.magnitude * 3.6f;
        float steerAngle = vehicle != null ? vehicle.CurrentSteerAngle : 0f;

        float maxSideSlip = 0f;
        if (vehicle != null)
        {
            maxSideSlip = Mathf.Max(
                GetSideSlip(vehicle.frontLeft), GetSideSlip(vehicle.frontRight),
                GetSideSlip(vehicle.rearLeft), GetSideSlip(vehicle.rearRight));
        }

        float[] features = { speedKmh, latAcc, longAcc, rollRate, yawRate, steerAngle, maxSideSlip };

        bool warmingUp = runElapsed < ignoreStartupSeconds;

        if (lstmModel != null)
        {
            var p = lstmModel.PushAndPredict(features);
            currentProb = warmingUp ? null : p;
        }

        probHistory[historyIndex] = warmingUp ? 0f : (currentProb ?? 0f);
        historyIndex = (historyIndex + 1) % historyLength;
        historyCount = Mathf.Min(historyCount + 1, historyLength);

        lastVelocity = vel;
        lastEuler = euler;
    }

    float GetSideSlip(WheelCollider wheel)
    {
        if (wheel == null) return 0f;
        if (wheel.GetGroundHit(out WheelHit hit)) return Mathf.Abs(hit.sidewaysSlip);
        return 0f;
    }

    void DrawLine(Vector2 p1, Vector2 p2, Color color, float width = 3f)
    {
        Vector2 d = p2 - p1;
        float length = d.magnitude;
        if (length < 0.01f) return;
        float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;

        Matrix4x4 backup = GUI.matrix;
        Color prevColor = GUI.color;

        GUIUtility.RotateAroundPivot(angle, p1);
        GUI.color = color;
        GUI.DrawTexture(new Rect(p1.x, p1.y - width / 2f, length, width), Texture2D.whiteTexture);

        GUI.color = prevColor;
        GUI.matrix = backup;
    }

    void DrawDashedHLine(Rect area, float yNorm, Color color)
    {
        float y = area.y + area.height * (1f - yNorm);
        float dash = 6f, gap = 4f;
        float xPos = area.x;
        while (xPos < area.x + area.width)
        {
            float segEnd = Mathf.Min(xPos + dash, area.x + area.width);
            DrawLine(new Vector2(xPos, y), new Vector2(segEnd, y), color, 1.5f);
            xPos += dash + gap;
        }
    }

    void DrawProbGraph(Rect area)
    {
        GUI.Box(area, "");
        DrawDashedHLine(area, warnThreshold, new Color(0.9f, 0.8f, 0.1f, 0.6f));
        DrawDashedHLine(area, dangerThreshold, new Color(1f, 0.3f, 0.3f, 0.6f));

        if (historyCount < 2) return;

        int n = historyCount;
        float stepX = area.width / (historyLength - 1);

        Vector2 PointAt(int i)
        {
            int idx = (historyIndex - n + i + historyLength * 2) % historyLength;
            float v = Mathf.Clamp01(probHistory[idx]);
            float px = area.x + (historyLength - n + i) * stepX;
            float py = area.y + area.height * (1f - v);
            return new Vector2(px, py);
        }

        for (int i = 0; i < n - 1; i++)
            DrawLine(PointAt(i), PointAt(i + 1), new Color(1f, 0.85f, 0.2f), 3f);
    }

    void OnGUI()
    {
        int x = Screen.width - panelWidth - marginRight;
        int y = marginTop;

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
        GUIStyle bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        GUIStyle statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUIStyle smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };

        GUI.Box(new Rect(x, y, panelWidth, 240), "");
        int cy = y + 8;
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 18), "LSTM 조기경보 — 1.5초 후 위험 확률 예측", titleStyle);
        cy += 26;

        Color color = Color.gray;
        string statusText = "예측 준비 중...";
        float displayProb = 0f;

        if (currentProb.HasValue)
        {
            displayProb = currentProb.Value;
            if (displayProb >= dangerThreshold) { color = Color.red; statusText = "위험 임박"; }
            else if (displayProb >= warnThreshold) { color = new Color(0.9f, 0.8f, 0.1f); statusText = "위험 상승 중"; }
            else { color = Color.green; statusText = "안정"; }
        }

        GUIStyle bigColored = new GUIStyle(bigStyle) { normal = { textColor = color } };
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 50), $"{displayProb * 100f:F0}%", bigColored);
        cy += 50;

        GUIStyle statusColored = new GUIStyle(statusStyle) { normal = { textColor = color } };
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 24), statusText, statusColored);
        cy += 30;

        DrawProbGraph(new Rect(x + 15, cy, panelWidth - 30, 90));
        cy += 96;

        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 16), "최근 15초 예측 확률 추이 (노랑=경보선 0.4 · 빨강=위험선 0.7)", smallStyle);
    }
}