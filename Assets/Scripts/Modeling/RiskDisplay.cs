using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class RiskDisplay : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public VehicleController vehicle;
    public RiskModel riskModel;
    public CargoBedLoader cargoLoader;

    [Header("최종 지표 노이즈 필터")]
    public int sustainFrames = 3;

    private Rigidbody rb;
    private Vector3 lastVelocity;
    private Vector3 lastEuler;

    private float currentRisk = 0f;
    private float actualLTR = 0f;
    private float currentSpeed = 0f;
    private float currentLatAcc = 0f;
    private float currentRoll = 0f;
    private float currentYawRate = 0f;

    private float[] ltrWindow;
    private int windowIndex = 0;
    private int windowFilled = 0;
    private float riskIndex = 0f;

    private int cargoCount = 0;
    private float cargoMass = 0f;
    private bool cargoInfoCached = false;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        if (riskModel == null) riskModel = GetComponent<RiskModel>();
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();

        ltrWindow = new float[Mathf.Max(1, sustainFrames)];
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

        ltrWindow[windowIndex] = Mathf.Abs(actualLTR);
        windowIndex = (windowIndex + 1) % ltrWindow.Length;
        windowFilled = Mathf.Min(windowFilled + 1, ltrWindow.Length);

        if (windowFilled >= ltrWindow.Length)
        {
            float sustainedValue = ltrWindow[0];
            for (int i = 1; i < ltrWindow.Length; i++)
                sustainedValue = Mathf.Min(sustainedValue, ltrWindow[i]);

            if (sustainedValue > riskIndex)
                riskIndex = sustainedValue;
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
        int panelWidth = 220;
        int x = 20, y = 20;

        GUIStyle rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        GUIStyle riskStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };

        float riskScore100 = currentRisk * 100f;
        float indexScore100 = riskIndex * 100f;

        Color riskColor = Color.green;
        if (currentRisk > 0.3f) riskColor = new Color(0.9f, 0.8f, 0.1f);
        if (currentRisk > 0.6f) riskColor = Color.red;

        GUI.Box(new Rect(x, y, panelWidth, 165), "");

        int cy = y + 8;
        GUIStyle r = new GUIStyle(riskStyle) { normal = { textColor = riskColor } };
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 22), $"Risk: {riskScore100:F0}", r);
        cy += 20;

        Rect barBg = new Rect(x + 10, cy, panelWidth - 20, 12);
        GUI.Box(barBg, "");
        Rect barFill = new Rect(x + 10, cy, (panelWidth - 20) * Mathf.Clamp01(currentRisk), 12);
        Color prev = GUI.color;
        GUI.color = riskColor;
        GUI.DrawTexture(barFill, Texture2D.whiteTexture);
        GUI.color = prev;
        cy += 20;

        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Speed: {currentSpeed:F1} km/h", rowStyle); cy += 16;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"LatAcc: {currentLatAcc:F2}", rowStyle); cy += 16;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Roll: {currentRoll:F1}°", rowStyle); cy += 16;
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"YawRate: {currentYawRate:F1}°/s", rowStyle); cy += 20;

        if (cargoInfoCached)
            GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 18), $"Cargo: {cargoCount}개 / {cargoMass:F0}kg", rowStyle);
        cy += 24;

        Color idxColor = Color.green;
        if (riskIndex > 0.3f) idxColor = new Color(0.9f, 0.8f, 0.1f);
        if (riskIndex > 0.6f) idxColor = Color.red;
        GUIStyle idxStyle = new GUIStyle(riskStyle) { normal = { textColor = idxColor } };
        GUI.Label(new Rect(x + 10, cy, panelWidth - 20, 22), $"RiskIndex: {indexScore100:F0}", idxStyle);
    }
}