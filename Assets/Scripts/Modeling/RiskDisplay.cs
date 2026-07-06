using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RiskDisplay : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    public RiskModel riskModel;
    public CargoBedLoader cargoLoader;

    private Rigidbody rb;
    private Vector3 lastVelocity;
    private Vector3 lastEuler;

    private float currentRisk = 0f;      // 모델 예측값
    private float actualLTR = 0f;        // 실측값 (물리엔진에서 직접 계산)
    private float currentSpeed = 0f;
    private float currentLatAcc = 0f;
    private float currentRoll = 0f;
    private float currentYawRate = 0f;

    private int cargoCount = 0;
    private float cargoMass = 0f;
    private bool cargoInfoCached = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        if (riskModel == null) riskModel = GetComponent<RiskModel>();
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();
    }

    void Start()
    {
        lastVelocity = rb.velocity;
        lastEuler = transform.eulerAngles;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

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

        // 실측 LTR = 물리엔진에서 바로 계산 (DataLogger.CalculateLTR과 동일 공식)
        float leftForce = flForce + rlForce;
        float rightForce = frForce + rrForce;
        float denom = leftForce + rightForce;
        actualLTR = denom > 0.001f ? Mathf.Clamp((rightForce - leftForce) / denom, -1f, 1f) : 0f;

        float[] features = { speedKmh, latAcc, longAcc, rollRate, yawRate, steerAngle, maxSideSlip };

        if (riskModel != null)
            currentRisk = riskModel.Predict(features);

        currentSpeed = speedKmh;
        currentLatAcc = latAcc;
        currentRoll = roll;
        currentYawRate = yawRate;

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
        int panelWidth = 320;
        int x = 20, y = 20;

        GUI.Box(new Rect(x, y, panelWidth, 260), "");

        GUIStyle titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        GUIStyle rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        GUIStyle smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Italic };

        Color riskColor = Color.green;
        if (currentRisk > 0.3f) riskColor = new Color(0.9f, 0.8f, 0.1f);
        if (currentRisk > 0.6f) riskColor = Color.red;

        int cy = y + 10;

        GUIStyle riskStyle = new GUIStyle(titleStyle);
        riskStyle.normal.textColor = riskColor;
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 28), $"모델 예측 RiskScore: {currentRisk:F3}", riskStyle);
        cy += 26;

        Rect barBg = new Rect(x + 15, cy, panelWidth - 30, 16);
        GUI.Box(barBg, "");
        Rect barFill = new Rect(x + 15, cy, (panelWidth - 30) * Mathf.Clamp01(currentRisk), 16);
        Color prevColor = GUI.color;
        GUI.color = riskColor;
        GUI.DrawTexture(barFill, Texture2D.whiteTexture);
        GUI.color = prevColor;
        cy += 24;

        // 실측값 비교 — 모델 검증용
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"실측 |LTR| (정답)   : {Mathf.Abs(actualLTR):F3}", rowStyle); cy += 18;
        float diff = Mathf.Abs(currentRisk - Mathf.Abs(actualLTR));
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 18), $"(모델 오차: {diff:F3})", smallStyle); cy += 26;

        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"속도(Speed)      : {currentSpeed:F1} km/h", rowStyle); cy += 20;
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"횡가속도(LatAcc) : {currentLatAcc:F2} m/s²", rowStyle); cy += 20;
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"기울기(Roll)     : {currentRoll:F1}°", rowStyle); cy += 20;
        GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"회전율(YawRate)  : {currentYawRate:F1}°/s", rowStyle); cy += 24;

        if (cargoInfoCached)
        {
            GUIStyle cargoStyle = new GUIStyle(rowStyle) { fontStyle = FontStyle.Italic };
            GUI.Label(new Rect(x + 15, cy, panelWidth - 30, 20), $"화물: {cargoCount}개 / 총 {cargoMass:F0}kg", cargoStyle);
        }
    }
}