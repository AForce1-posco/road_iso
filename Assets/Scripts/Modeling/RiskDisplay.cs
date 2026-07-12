using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RiskDisplay : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    public EarlyWarningLSTM earlyWarning;
    public CargoBedLoader cargoLoader;
    public PurePursuitController pursuit;
    public CargoFeatureCalculator staticFeatureCalculator;

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

    private float actualLTR = 0f;
    private float currentActualLTR = 0f;
    private float currentSpeed = 0f;
    private float currentLatAcc = 0f;
    private float? earlyWarningProb = null;

    private float[] ltrWindow;
    private int windowIndex = 0;
    private int windowFilled = 0;
    private float peakRisk = 0f;
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

    private float? staticPredictedRisk = null;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        if (earlyWarning == null) earlyWarning = GetComponent<EarlyWarningLSTM>();
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();
        if (pursuit == null) pursuit = GetComponent<PurePursuitController>();
        if (staticFeatureCalculator == null) staticFeatureCalculator = GetComponent<CargoFeatureCalculator>();

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

            if (staticFeatureCalculator != null)
                staticPredictedRisk = staticFeatureCalculator.ComputeAndPredict();
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
        GUIStyle compareStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        GUIStyle reportStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, wordWrap = true };

        GUI.Box(new Rect(x, y, panelWidth, 200), "");
        int cy = y + 6;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), "▶ 실시간 위험 모니터", sectionTitle);
        cy += 24;

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

        // ── LSTM 1.5초 후 예측 (조기경보) ──
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

        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Speed: {currentSpeed:F1} km/h   LatAcc: {currentLatAcc:F1} m/s²", rowStyle); cy += 18;
        if (cargoInfoCached)
            GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Cargo: {cargoCount}개 / {cargoMass:F0}kg", rowStyle);

        // ── 하단 강조 패널: 사전 예측 vs 실측 최고위험도 비교 ──
        int y2 = y + 200 + 10;
        int panelHeight2 = 110;
        GUI.Box(new Rect(x, y2, panelWidth, panelHeight2), "");
        int cy2 = y2 + 8;

        GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 16), "■ 사전 예측 vs 실측 결과", sectionTitle);
        cy2 += 22;

        float staticVal = staticPredictedRisk ?? 0f;
        bool staticReady = staticPredictedRisk.HasValue;

        Color staticColor = staticVal >= dangerThreshold ? Color.red : (staticVal >= warnThreshold ? new Color(0.9f, 0.8f, 0.1f) : Color.green);
        Color peakColor = peakRisk >= dangerThreshold ? Color.red : (peakRisk >= warnThreshold ? new Color(0.9f, 0.8f, 0.1f) : Color.green);

        GUIStyle staticLabelStyle = new GUIStyle(compareStyle) { normal = { textColor = staticColor } };
        GUIStyle peakLabelStyle = new GUIStyle(compareStyle) { normal = { textColor = peakColor } };

        GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 20),
            staticReady ? $"사전 예측(LightGBM): {staticVal * 100f:F0}점" : "사전 예측 계산 중...", staticLabelStyle);
        cy2 += 22;
        GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 20), $"실측 최고 위험도: {peakRisk * 100f:F0}점", peakLabelStyle);
        cy2 += 24;

        if (staticReady)
        {
            float diff = Mathf.Abs(staticVal - peakRisk) * 100f;
            GUIStyle diffStyle = new GUIStyle(smallStyle);
            GUI.Label(new Rect(x + 10, cy2, panelWidth - 20, 18), $"오차: {diff:F0}점", diffStyle);
        }

        // ── 사후 검증 리포트 (완주/전복 시에만) ──
        if (runEnded && reportGenerated)
        {
            int y3 = y2 + panelHeight2 + 10;
            GUI.Box(new Rect(x, y3, panelWidth, 90), "");
            int cy3 = y3 + 6;
            GUI.Label(new Rect(x + 10, cy3, panelWidth - 20, 16), "■ 조기경보 사후 검증", sectionTitle);
            cy3 += 22;
            GUIStyle rep = new GUIStyle(reportStyle) { normal = { textColor = reportColor } };
            GUI.Label(new Rect(x + 10, cy3, panelWidth - 20, 60), reportText, rep);
        }
    }
}