using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RiskDisplay : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    public RiskModel riskModel;
    public EarlyWarningLSTM earlyWarning;
    public CargoBedLoader cargoLoader;
    public PurePursuitController pursuit;

    [Header("최종 지표 노이즈 필터")]
    public int sustainFrames = 3;

    [Header("샘플링 간격 (학습 데이터와 반드시 일치)")]
    public float sampleInterval = 0.1f;

    [Header("경보 임계값")]
    public float warnThreshold = 0.4f;
    public float dangerThreshold = 0.7f;

    [Header("착지/안정화 워밍업 — 이 시간 동안은 측정 제외")]
    public float ignoreStartupSeconds = 1.5f;

    private Rigidbody rb;
    private Vector3 lastVelocity;
    private Vector3 lastEuler;

    private float sampleTimer = 0f;
    private float lastSampleTime = 0f;
    private float runElapsed = 0f;

    private float currentRisk = 0f;          // XGBoost 예측값 (비교용, 보조)
    private float actualLTR = 0f;
    private float currentActualLTR = 0f;     // 그 순간의 실측 |LTR| (메인)
    private float currentSpeed = 0f;
    private float currentLatAcc = 0f;
    private float? earlyWarningProb = null;  // LSTM: 1.5초 후 확률 (보조)

    private float[] ltrWindow;
    private int windowIndex = 0;
    private int windowFilled = 0;
    private float peakRisk = 0f;             // 이번 주행 최고 위험도 (실측 누적)
    private float currentSustainedLTR = 0f;

    private System.Collections.Generic.List<float> fullProbHistory = new System.Collections.Generic.List<float>();
    private System.Collections.Generic.List<float> fullActualHistory = new System.Collections.Generic.List<float>();

    private bool runEnded = false;
    private bool reportGenerated = false;
    private string reportText = "";
    private Color reportColor = Color.gray;

    private int cargoCount = 0;
    private float cargoMass = 0f;
    private bool cargoInfoCached = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        if (riskModel == null) riskModel = GetComponent<RiskModel>();
        if (earlyWarning == null) earlyWarning = GetComponent<EarlyWarningLSTM>();
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();
        if (pursuit == null) pursuit = GetComponent<PurePursuitController>();

        ltrWindow = new float[Mathf.Max(1, sustainFrames)];
    }

    void Start()
    {
        lastVelocity = rb.velocity;
        lastEuler = transform.eulerAngles;
        lastSampleTime = Time.time;
    }

    void FixedUpdate()
    {
        if (runEnded)
        {
            if (!reportGenerated) GenerateReport();
            return;
        }

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
        float roll = NormalizeAngle(euler.z);
        float rollRate = Mathf.DeltaAngle(lastEuler.z, euler.z) / dt;
        float yawRate = Mathf.DeltaAngle(lastEuler.y, euler.y) / dt;

        float speedKmh = vel.magnitude * 3.6f;
        float steerAngle = vehicle != null ? vehicle.CurrentSteerAngle : 0f;

        float maxSideSlip = 0f;
        float flForce = 0f, frForce = 0f, rlForce = 0f, rrForce = 0f;
        if (vehicle != null)
        {
            maxSideSlip = Mathf.Max(
                GetSideSlip(vehicle.frontLeft), GetSideSlip(vehicle.frontRight),
                GetSideSlip(vehicle.rearLeft), GetSideSlip(vehicle.rearRight));

            flForce = GetNormalForce(vehicle.frontLeft);
            frForce = GetNormalForce(vehicle.frontRight);
            rlForce = GetNormalForce(vehicle.rearLeft);
            rrForce = GetNormalForce(vehicle.rearRight);
        }

        float leftForce = flForce + rlForce;
        float rightForce = frForce + rrForce;
        float denom = leftForce + rightForce;
        actualLTR = denom > 0.001f ? Mathf.Clamp((rightForce - leftForce) / denom, -1f, 1f) : 0f;
        currentActualLTR = Mathf.Abs(actualLTR);

        float[] features = { speedKmh, latAcc, longAcc, rollRate, yawRate, steerAngle, maxSideSlip };

        if (riskModel != null)
            currentRisk = riskModel.Predict(features);

        if (earlyWarning != null)
            earlyWarningProb = earlyWarning.PushAndPredict(features);

        currentSpeed = speedKmh;
        currentLatAcc = latAcc;

        ltrWindow[windowIndex] = currentActualLTR;
        windowIndex = (windowIndex + 1) % ltrWindow.Length;
        windowFilled = Mathf.Min(windowFilled + 1, ltrWindow.Length);

        bool warmingUp = runElapsed < ignoreStartupSeconds;

        if (windowFilled >= ltrWindow.Length)
        {
            float sustainedValue = ltrWindow[0];
            for (int i = 1; i < ltrWindow.Length; i++)
                sustainedValue = Mathf.Min(sustainedValue, ltrWindow[i]);

            currentSustainedLTR = sustainedValue;

            if (!warmingUp && sustainedValue > peakRisk)
                peakRisk = sustainedValue;
        }

        if (!warmingUp)
        {
            fullProbHistory.Add(earlyWarningProb ?? 0f);
            fullActualHistory.Add(currentSustainedLTR);
        }

        if (!cargoInfoCached && cargoLoader != null && cargoLoader.Loaded.Count > 0)
        {
            cargoCount = cargoLoader.Loaded.Count;
            cargoMass = 0f;
            foreach (var c in cargoLoader.Loaded)
                cargoMass += c.type.massKg * cargoLoader.massScale;
            cargoInfoCached = true;
        }

        lastVelocity = vel;
        lastEuler = euler;

        bool flipped = Vector3.Dot(transform.up, Vector3.up) < 0.5f;
        bool finishedIso = pursuit != null && pursuit.Finished;
        if (flipped || finishedIso)
            runEnded = true;
    }

    public void ForceEndRun() => runEnded = true;

    void GenerateReport()
    {
        reportGenerated = true;

        int warnIdx = -1, actualIdx = -1;
        for (int i = 0; i < fullProbHistory.Count; i++)
        {
            if (warnIdx < 0 && fullProbHistory[i] >= warnThreshold) warnIdx = i;
            if (actualIdx < 0 && fullActualHistory[i] >= warnThreshold) actualIdx = i;
        }

        float peakProb = fullProbHistory.Count > 0 ? Mathf.Max(fullProbHistory.ToArray()) : 0f;

        if (warnIdx < 0 || actualIdx < 0)
        {
            reportColor = Color.gray;
            reportText = $"경보 임계치 미도달 (안전 범위 내)\n최고 예측확률 {peakProb * 100f:F0}%";
            return;
        }

        float leadSeconds = (actualIdx - warnIdx) * sampleInterval;

        if (leadSeconds > 0)
        {
            reportColor = Color.green;
            reportText = $"✓ 조기감지 성공 — 실제 위험보다 {leadSeconds:F1}초 먼저 경보";
        }
        else if (leadSeconds == 0)
        {
            reportColor = new Color(0.9f, 0.8f, 0.1f);
            reportText = "경보와 실제 위험이 거의 동시에 발생";
        }
        else
        {
            reportColor = Color.red;
            reportText = $"✗ 경보 지연 — 실제 위험이 {-leadSeconds:F1}초 먼저 발생";
        }
    }

    float GetSideSlip(WheelCollider wheel)
    {
        if (wheel == null) return 0f;
        if (wheel.GetGroundHit(out WheelHit hit)) return Mathf.Abs(hit.sidewaysSlip);
        return 0f;
    }

    float GetNormalForce(WheelCollider wheel)
    {
        if (wheel == null) return 0f;
        if (wheel.GetGroundHit(out WheelHit hit)) return Mathf.Max(0f, hit.force);
        return 0f;
    }

    float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;

    void OnGUI()
    {
        int panelWidth = 280;
        int x = 20, y = 20;

        GUIStyle sectionTitle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        GUIStyle rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        GUIStyle smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        GUIStyle bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        GUIStyle riskStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        GUIStyle reportStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true };

        GUI.Box(new Rect(x, y, panelWidth, 250), "");
        int cy = y + 6;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), "▶ 실시간 위험 모니터", sectionTitle);
        cy += 22;

        // ── 메인: 실시간 위험도 (실측값) ──
        float actualScore100 = currentActualLTR * 100f;
        Color riskColor = Color.green;
        if (currentActualLTR > 0.3f) riskColor = new Color(0.9f, 0.8f, 0.1f);
        if (currentActualLTR > 0.6f) riskColor = Color.red;

        GUI.Box(new Rect(x + 10, cy, panelWidth - 20, 60), "");
        GUIStyle bigColored = new GUIStyle(bigStyle) { normal = { textColor = riskColor } };
        GUI.Label(new Rect(x + 10, cy + 4, panelWidth - 20, 38), $"{actualScore100:F0}", bigColored);
        GUI.Label(new Rect(x + 10, cy + 42, panelWidth - 20, 16), "실시간 위험도 (실측)", labelStyle);
        cy += 68;

        // ── 보조 1: XGBoost 예측값 (실측과 비교용) ──
        float xgbScore100 = currentRisk * 100f;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), $"모델 예측(XGBoost): {xgbScore100:F0}", smallStyle);
        cy += 18;

        // ── 보조 2: LSTM 1.5초 후 예측 ──
        string subText;
        Color subColor = Color.gray;
        if (runElapsed < ignoreStartupSeconds)
            subText = "예측 준비 중...";
        else if (earlyWarningProb.HasValue)
        {
            float p = earlyWarningProb.Value;
            subColor = p >= dangerThreshold ? Color.red : (p >= warnThreshold ? new Color(0.9f, 0.8f, 0.1f) : new Color(0.6f, 0.9f, 0.6f));
            subText = $"1.5초 후 예측(LSTM): {p * 100f:F0}%";
        }
        else subText = "예측 대기 중...";

        GUIStyle sub = new GUIStyle(smallStyle) { normal = { textColor = subColor } };
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), subText, sub);
        cy += 20;

        // ── Speed / LatAcc (Roll 대신 LatAcc로 변경) ──
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Speed: {currentSpeed:F1} km/h   LatAcc: {currentLatAcc:F1} m/s²", rowStyle); cy += 18;
        if (cargoInfoCached)
            GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Cargo: {cargoCount}개 / {cargoMass:F0}kg", rowStyle);
        cy += 22;

        // ── 하단: 이번 주행 최고 위험도 (실측, 누적) ──
        Color peakColor = Color.green;
        if (peakRisk > 0.3f) peakColor = new Color(0.9f, 0.8f, 0.1f);
        if (peakRisk > 0.6f) peakColor = Color.red;
        GUIStyle peakStyle = new GUIStyle(riskStyle) { normal = { textColor = peakColor } };
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 22), $"이번 주행 최고 위험도: {peakRisk * 100f:F0}", peakStyle);

        // ── 사후 검증 리포트 (완주/전복 시에만) ──
        if (runEnded && reportGenerated)
        {
            int y2 = y + 250 + 10;
            GUI.Box(new Rect(x, y2, panelWidth, 90), "");
            int cy2 = y2 + 6;
            GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 16), "■ 사후 검증 리포트 (주행 종료)", sectionTitle);
            cy2 += 22;
            GUIStyle rep = new GUIStyle(reportStyle) { normal = { textColor = reportColor } };
            GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 60), reportText, rep);
        }
    }
}