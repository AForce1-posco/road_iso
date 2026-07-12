using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class StageComparisonDisplay : MonoBehaviour
{
    public enum Stage { RandomPlacement, BinPacking3D, PPOOptimized }

    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    public PurePursuitController pursuit;

    [Header("이번 영상 단계")]
    [Tooltip("이 값만 바꿔가며 3번 녹화")]
    public Stage stage = Stage.RandomPlacement;

    [Header("최종 지표 노이즈 필터")]
    public int sustainFrames = 3;

    [Header("샘플링 간격 (학습 데이터와 반드시 일치)")]
    public float sampleInterval = 0.1f;

    [Header("착지/안정화 워밍업")]
    public float ignoreStartupSeconds = 1.5f;

    [Header("비교 기준점")]
    [Tooltip("Stage 1(임의배치) 촬영 후 나온 '위험도'를 여기 입력하면, Stage 2/3 영상에 감소율이 자동 표시됨. 0이면 표시 안 함.")]
    public float baselineRiskScore = 0f;

    private Rigidbody rb;
    private Vector3 lastVelocity;
    private Vector3 lastEuler;

    private float sampleTimer = 0f;
    private float lastSampleTime = 0f;
    private float runElapsed = 0f;

    private float currentSpeed = 0f;
    private float currentLatAcc = 0f;
    private float actualLTR = 0f;

    private float[] ltrWindow;
    private int windowIndex = 0;
    private int windowFilled = 0;
    private float peakRisk = 0f; // 실측 누적 최고 위험도 (0~1)

    private bool runEnded = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
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
        if (runEnded) return;

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
        float latAcc = localAcc.x;

        float speedKmh = vel.magnitude * 3.6f;

        float flForce = 0f, frForce = 0f, rlForce = 0f, rrForce = 0f;
        if (vehicle != null)
        {
            flForce = GetNormalForce(vehicle.frontLeft);
            frForce = GetNormalForce(vehicle.frontRight);
            rlForce = GetNormalForce(vehicle.rearLeft);
            rrForce = GetNormalForce(vehicle.rearRight);
        }

        float leftForce = flForce + rlForce;
        float rightForce = frForce + rrForce;
        float denom = leftForce + rightForce;
        actualLTR = denom > 0.001f ? Mathf.Clamp((rightForce - leftForce) / denom, -1f, 1f) : 0f;

        currentSpeed = speedKmh;
        currentLatAcc = latAcc;

        bool warmingUp = runElapsed < ignoreStartupSeconds;

        ltrWindow[windowIndex] = Mathf.Abs(actualLTR);
        windowIndex = (windowIndex + 1) % ltrWindow.Length;
        windowFilled = Mathf.Min(windowFilled + 1, ltrWindow.Length);

        if (windowFilled >= ltrWindow.Length)
        {
            float sustainedValue = ltrWindow[0];
            for (int i = 1; i < ltrWindow.Length; i++)
                sustainedValue = Mathf.Min(sustainedValue, ltrWindow[i]);

            if (!warmingUp && sustainedValue > peakRisk)
                peakRisk = sustainedValue;
        }

        lastVelocity = vel;
        lastEuler = transform.eulerAngles;

        bool flipped = Vector3.Dot(transform.up, Vector3.up) < 0.5f;
        bool finishedIso = pursuit != null && pursuit.Finished;
        if (flipped || finishedIso)
            runEnded = true;
    }

    float GetNormalForce(WheelCollider wheel)
    {
        if (wheel == null) return 0f;
        if (wheel.GetGroundHit(out WheelHit hit)) return Mathf.Max(0f, hit.force);
        return 0f;
    }

    string StageLabel()
    {
        switch (stage)
        {
            case Stage.RandomPlacement: return "STAGE 1 : 임의 배치";
            case Stage.BinPacking3D: return "STAGE 2 : 3D 빈패킹";
            case Stage.PPOOptimized: return "STAGE 3 : PPO 최적화";
        }
        return "";
    }

    void OnGUI()
    {
        int panelWidth = 300;
        int x = 20, y = 20;

        GUIStyle stageStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUIStyle bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        GUIStyle rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        GUIStyle improveStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        // 위험도 그대로 표시 (0=안전, 100=위험). 낮을수록 좋음.
        float riskScore = peakRisk * 100f;

        Color scoreColor = Color.green;
        if (riskScore >= 30f) scoreColor = new Color(0.9f, 0.8f, 0.1f);
        if (riskScore >= 60f) scoreColor = Color.red;

        GUI.Box(new Rect(x, y, panelWidth, 190), "");
        int cy = y + 10;

        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 22), StageLabel(), stageStyle);
        cy += 30;

        GUIStyle bigColored = new GUIStyle(bigStyle) { normal = { textColor = scoreColor } };
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 56), $"{riskScore:F0}", bigColored);
        cy += 56;

        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), "위험도 (낮을수록 안전)", labelStyle);
        cy += 22;

        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 16), $"Speed {currentSpeed:F0}km/h   LatAcc {currentLatAcc:F1}", rowStyle);
        cy += 22;

        if (baselineRiskScore > 0f && stage != Stage.RandomPlacement)
        {
            // 위험도는 낮아지는 게 개선이므로, 감소율로 표시
            float reductionPct = ((baselineRiskScore - riskScore) / baselineRiskScore) * 100f;
            Color improveColor = reductionPct >= 0f ? Color.green : Color.red;
            string sign = reductionPct >= 0f ? "-" : "+";
            GUIStyle improveColored = new GUIStyle(improveStyle) { normal = { textColor = improveColor } };
            GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 24), $"기준(Stage1) 대비 위험도 {sign}{Mathf.Abs(reductionPct):F0}%", improveColored);
        }
    }
}