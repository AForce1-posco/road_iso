using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// 주행 1회(run) 동안 위험 지표를 샘플링해 집계하고, 종료 시 results.csv에 1행을 추가한다.
/// 측정: 차체 Roll·횡가속(g)·4륜 접지력 기반 LTR·화물 이동거리(max)·트레이 이탈 수·전복.
/// DataLogger(시계열 로그)와 독립 — 이건 "배치 1개 = 결과 1행"용.
/// </summary>
public class CargoRiskRecorder : MonoBehaviour
{
    [Header("참조")]
    public Rigidbody truckBody;
    public VehicleController vehicle;      // 4륜 WheelCollider 접근용
    public CargoBedLoader loader;
    public PurePursuitController pursuit;  // ISO 구간 표시용 (없어도 동작)

    [Header("판정 임계 (조절 가능)")]
    public float rolloverDot = 0.5f;       // truck.up·world.up 이 값 미만이면 전복
    [Tooltip("이탈 판정: 트레이 반폭 바깥 여유 (m, 스케일 후)")]
    public float fallMarginM = 0.3f;
    [Tooltip("이탈 판정: 트레이 바닥보다 이만큼 아래로 내려가면 낙하 (m)")]
    public float fallBelowM = 0.3f;

    [Header("위험 등급 임계 (0안전/1주의/2위험/3전복)")]
    public float cautionRollDeg = 6f;
    public float dangerRollDeg = 12f;
    public float cautionLtr = 0.6f;
    public float dangerLtr = 0.9f;
    [Tooltip("주의 등급 화물 이동거리 (m, 스케일 후)")]
    public float cautionShiftM = 0.15f;

    [Header("출력")]
    public string resultsPath = "";        // 비우면 Assets/Data/Results/results.csv

    [Header("실시간 표시")]
    [Tooltip("Game 뷰 좌상단에 현재/최대값 HUD 표시")]
    public bool showHud = true;
    [Tooltip("콘솔에 현재값 찍는 주기 (s). 0이면 끔")]
    public float consoleLogEvery = 1f;

    public bool Recording { get; private set; }
    public bool RolloverDetected { get; private set; }

    // 현재값 (HUD·콘솔용)
    private float curSpeedKmh, curRollDeg, curLatG, curLtr;
    private float lastConsoleLog;

    // ── 집계값 ──
    private float maxAbsRollDeg, maxAbsPitchDeg, maxLatAccelG, maxAbsLtr, maxShiftM;
    private int fellCount;
    private bool[] fell;               // 화물별 이탈 플래그 (한 번 이탈은 유지)
    private float[] maxShiftPer;       // 화물별 최대 이동
    private Vector3 lastVelocity;
    private float startTime;

    // 적재 직후 스냅샷 (해제·이탈과 무관하게 초기 특징 보존)
    private int cargoCount;
    private float totalMassKg, securedFrac;
    private Vector3 initCogLocal;      // bedAnchor 로컬 (x=좌우, z=전후, y=바닥 위 높이)
    private string sourceLayout = "";

    private string ResolvedResultsPath => string.IsNullOrEmpty(resultsPath)
        ? Path.Combine(Application.dataPath, "Data/Results/results.csv")
        : resultsPath;

    void Awake()
    {
        // 인스펙터에서 비워두면 자동 검색
        if (vehicle == null) vehicle = FindObjectOfType<VehicleController>();
        if (truckBody == null && vehicle != null) truckBody = vehicle.GetComponent<Rigidbody>();
        if (loader == null) loader = FindObjectOfType<CargoBedLoader>();
        if (pursuit == null) pursuit = FindObjectOfType<PurePursuitController>();
    }

    /// <summary>적재 완료·자유화물 해제 후 호출. 초기 특징 스냅샷 + 샘플링 시작.</summary>
    public void Begin()
    {
        if (loader == null || truckBody == null) { Debug.LogError("CargoRiskRecorder: 참조 미지정"); return; }

        var cargo = loader.Loaded;
        cargoCount = cargo.Count;
        fell = new bool[cargoCount];
        maxShiftPer = new float[cargoCount];
        sourceLayout = string.IsNullOrEmpty(loader.LastLoadedPath) ? "" : Path.GetFileName(loader.LastLoadedPath);

        totalMassKg = 0f; securedFrac = 0f;
        Vector3 weighted = Vector3.zero;
        foreach (var c in cargo)
        {
            float m = c.type.massKg * loader.massScale;
            totalMassKg += m;
            if (c.secured) securedFrac += m;
            weighted += c.initialLocal * m;
        }
        if (totalMassKg > 0f) { securedFrac /= totalMassKg; initCogLocal = weighted / totalMassKg; }

        maxAbsRollDeg = maxAbsPitchDeg = maxLatAccelG = maxAbsLtr = maxShiftM = 0f;
        fellCount = 0;
        RolloverDetected = false;
        lastVelocity = truckBody.velocity;
        startTime = Time.time;
        Recording = true;
        Debug.Log($"기록 시작: {cargoCount}개 / {totalMassKg:F0}kg / 고정 {securedFrac * 100f:F0}%");
    }

    void FixedUpdate()
    {
        if (!Recording) return;
        Transform t = truckBody.transform;

        // 차체 자세
        float roll = Mathf.Abs(NormalizeAngle(t.eulerAngles.z));
        float pitch = Mathf.Abs(NormalizeAngle(t.eulerAngles.x));
        if (roll > maxAbsRollDeg) maxAbsRollDeg = roll;
        if (pitch > maxAbsPitchDeg) maxAbsPitchDeg = pitch;

        // 횡가속 (g)
        Vector3 acc = (truckBody.velocity - lastVelocity) / Time.fixedDeltaTime;
        lastVelocity = truckBody.velocity;
        float latG = Mathf.Abs(t.InverseTransformDirection(acc).x) / 9.81f;
        if (latG > maxLatAccelG) maxLatAccelG = latG;

        // 4륜 접지력 → LTR. 뜬 바퀴는 힘 0 → |LTR|이 1로 접근
        float ltr = ComputeWheelLtr();
        if (Mathf.Abs(ltr) > maxAbsLtr) maxAbsLtr = Mathf.Abs(ltr);

        SampleCargo();

        // 전복 (차체가 절반 이상 기울면)
        if (Vector3.Dot(t.up, Vector3.up) < rolloverDot)
            RolloverDetected = true;

        // 현재값 보관 + 주기 콘솔 로그
        curSpeedKmh = truckBody.velocity.magnitude * 3.6f;
        curRollDeg = NormalizeAngle(t.eulerAngles.z);
        curLatG = latG;
        curLtr = ltr;
        if (consoleLogEvery > 0f && Time.time - lastConsoleLog >= consoleLogEvery)
        {
            lastConsoleLog = Time.time;
            string iso = pursuit != null && pursuit.UsingISO ? " [ISO]" : "";
            Debug.Log($"[{Time.time - startTime:F1}s]{iso} 속도 {curSpeedKmh:F0}km/h | " +
                      $"Roll {curRollDeg:F1}°(max {maxAbsRollDeg:F1}) | 횡G {curLatG:F2}(max {maxLatAccelG:F2}) | " +
                      $"LTR {curLtr:F2}(max {maxAbsLtr:F2}) | 화물이동 {maxShiftM:F2}m | 이탈 {fellCount}");
        }
    }

    void OnGUI()
    {
        if (!showHud || !Recording) return;
        string iso = pursuit != null && pursuit.UsingISO ? "  [ISO 구간]" : "";
        string txt =
            $" t {Time.time - startTime:F1}s   속도 {curSpeedKmh:F0} km/h{iso}\n" +
            $" Roll  {curRollDeg,6:F1}°   (max {maxAbsRollDeg:F1}°)\n" +
            $" 횡가속 {curLatG,5:F2} g  (max {maxLatAccelG:F2} g)\n" +
            $" LTR   {curLtr,6:F2}   (max {maxAbsLtr:F2})\n" +
            $" 화물이동 max {maxShiftM:F2} m   이탈 {fellCount}개" +
            (RolloverDetected ? "\n ★ 전복 감지 ★" : "");
        int h = RolloverDetected ? 126 : 108;
        GUI.Box(new Rect(10, 10, 280, h), "");
        GUI.Label(new Rect(18, 16, 268, h - 8), txt);
    }

    private float ComputeWheelLtr()
    {
        if (vehicle == null) return 0f;
        float left = WheelForce(vehicle.frontLeft) + WheelForce(vehicle.rearLeft);
        float right = WheelForce(vehicle.frontRight) + WheelForce(vehicle.rearRight);
        float total = left + right;
        if (total <= 1e-4f) return 0f;
        return Mathf.Clamp((right - left) / total, -1f, 1f);
    }

    private static float WheelForce(WheelCollider w)
    {
        if (w == null) return 0f;
        WheelHit hit;
        return w.GetGroundHit(out hit) ? Mathf.Max(0f, hit.force) : 0f;
    }

    private void SampleCargo()
    {
        var cargo = loader.Loaded;
        Transform bed = loader.bedAnchor;
        if (bed == null) return;
        Vector2 half = loader.TrayHalfXZ;

        for (int i = 0; i < cargo.Count && i < fell.Length; i++)
        {
            var c = cargo[i];
            if (c.go == null) continue;
            Vector3 local = bed.InverseTransformPoint(c.go.transform.position);

            float shift = Vector3.Distance(local, c.initialLocal);
            if (shift > maxShiftPer[i]) maxShiftPer[i] = shift;
            if (shift > maxShiftM) maxShiftM = shift;

            if (!fell[i] &&
                (Mathf.Abs(local.x) > half.x + fallMarginM ||
                 Mathf.Abs(local.z) > half.y + fallMarginM ||
                 local.y < -fallBelowM))
            {
                fell[i] = true;
                fellCount++;
                Debug.Log($"화물 이탈: {c.type.name} (local={local})");
            }
        }
    }

    /// <summary>주행 종료 시 호출. 집계 → results.csv 1행 append. 위험등급 반환.</summary>
    public int Finish()
    {
        Recording = false;
        int grade = ComputeGrade();
        WriteCsvRow(grade);
        Debug.Log($"기록 종료 ({Time.time - startTime:F1}s): roll {maxAbsRollDeg:F1}° / latG {maxLatAccelG:F2} / " +
                  $"LTR {maxAbsLtr:F2} / 이동 {maxShiftM:F2}m / 이탈 {fellCount} / 전복 {(RolloverDetected ? 1 : 0)} → 등급 {grade}");
        return grade;
    }

    private int ComputeGrade()
    {
        if (RolloverDetected) return 3;
        if (fellCount > 0 || maxAbsRollDeg >= dangerRollDeg || maxAbsLtr >= dangerLtr) return 2;
        if (maxAbsRollDeg >= cautionRollDeg || maxAbsLtr >= cautionLtr || maxShiftM >= cautionShiftM) return 1;
        return 0;
    }

    private void WriteCsvRow(int grade)
    {
        var ci = CultureInfo.InvariantCulture;
        string path = ResolvedResultsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "run_id,source_layout,cargo_count,total_mass_kg,secured_frac," +
                "init_cog_x,init_cog_z,init_cog_height," +
                "target_speed_kmh,duration_s," +
                "max_roll_deg,max_pitch_deg,max_lat_accel_g,max_abs_ltr," +
                "max_cargo_shift_m,cargo_fell_count,rollover,risk_grade\n");
        }

        string runId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        float targetSpeed = pursuit != null ? pursuit.isoTargetSpeedKmh : 0f;
        string row = string.Join(",",
            runId, sourceLayout,
            cargoCount.ToString(ci), totalMassKg.ToString("F1", ci), securedFrac.ToString("F2", ci),
            initCogLocal.x.ToString("F3", ci), initCogLocal.z.ToString("F3", ci), initCogLocal.y.ToString("F3", ci),
            targetSpeed.ToString("F0", ci), (Time.time - startTime).ToString("F1", ci),
            maxAbsRollDeg.ToString("F2", ci), maxAbsPitchDeg.ToString("F2", ci),
            maxLatAccelG.ToString("F3", ci), maxAbsLtr.ToString("F3", ci),
            maxShiftM.ToString("F3", ci), fellCount.ToString(ci),
            (RolloverDetected ? 1 : 0).ToString(ci), grade.ToString(ci));

        File.AppendAllText(path, row + "\n");
        Debug.Log($"results.csv 1행 추가: {path}");
    }

    private static float NormalizeAngle(float a) => a > 180f ? a - 360f : a;
}
