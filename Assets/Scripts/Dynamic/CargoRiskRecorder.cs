using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 주행 1회(run) 동안 위험 지표를 샘플링해 집계하고, 종료 시 위험 등급을 산출·반환한다.
/// 측정: 차체 Roll·횡가속(g)·4륜 접지력 기반 LTR·화물 이동거리(max)·트레이 이탈 수·전복.
/// DataLogger(시계열 로그)와 독립 — 실시간 HUD·전복감지·위험등급용.
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

    [Header("위험 등급 (surrogate와 동일: p95LTR 밴드)")]
    [Tooltip("grade: p95LTR < cut1=0안전 / <cut2=1주의 / <cut3=2위험 / ≥cut3=3고위험. layout_risk_p95 분포(사분위 0.47/0.50/0.53) 기준. 차량/트랙 바뀌면 재보정.")]
    public float p95Cut1 = 0.47f;
    public float p95Cut2 = 0.50f;
    public float p95Cut3 = 0.53f;

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

    // p95LTR 집계 (surrogate reward와 동일 지표) — FixedUpdate마다 |LTR| 버퍼 → Finish에서 p95 계산
    private readonly List<float> ltrAbsSamples = new List<float>(4096);
    private float p95Ltr;              // 이번 run의 실측 p95(|LTR|) = surrogate가 예측하려는 그 값
    private int lastGrade = -1;
    private string lastSummary = "";   // 주행 종료 후 HUD에 계속 표시할 요약

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
        ltrAbsSamples.Clear();
        p95Ltr = 0f; lastSummary = "";
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
        ltrAbsSamples.Add(Mathf.Abs(ltr));   // p95 계산용 (surrogate 지표와 동일)

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
        if (!showHud) return;

        if (Recording)
        {
            string iso = pursuit != null && pursuit.UsingISO ? "  [ISO 구간]" : "";
            string txt =
                $" t {Time.time - startTime:F1}s   속도 {curSpeedKmh:F0} km/h{iso}\n" +
                $" 현재위험 {Mathf.Abs(curLtr) * 100f,3:F0}/100  ({Mathf.Abs(curLtr):F3})\n" +
                $" Roll  {curRollDeg,6:F1}°   (max {maxAbsRollDeg:F1}°)\n" +
                $" 횡가속 {curLatG,5:F2} g  (max {maxLatAccelG:F2} g)\n" +
                $" LTR   {curLtr,6:F2}   (max {maxAbsLtr:F2})\n" +
                $" 화물이동 max {maxShiftM:F2} m   이탈 {fellCount}개" +
                (RolloverDetected ? "\n ★ 전복 감지 ★" : "");
            int h = RolloverDetected ? 140 : 122;
            GUI.Box(new Rect(10, 10, 280, h), "");
            GUI.Label(new Rect(18, 16, 268, h - 8), txt);
        }
        else if (!string.IsNullOrEmpty(lastSummary))
        {
            // 주행 종료 후: 최종 위험도(p95LTR) + 등급 계속 표시
            GUI.Box(new Rect(10, 10, 280, 92), "");
            GUI.Label(new Rect(18, 16, 268, 84), lastSummary);
        }
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

            // 코너 원점(rear-left): local x∈[0,W]·z∈[0,L] (half=W/2,L/2 → 전폭=2·half). 양 끝 밖이면 이탈
            if (!fell[i] &&
                (local.x < -fallMarginM || local.x > 2f * half.x + fallMarginM ||
                 local.z < -fallMarginM || local.z > 2f * half.y + fallMarginM ||
                 local.y < -fallBelowM))
            {
                fell[i] = true;
                fellCount++;
                Debug.Log($"화물 이탈: {c.type.name} (local={local})");
            }
        }
    }

    /// <summary>주행 종료 시 호출. 실측 p95LTR·등급 산출 → HUD/콘솔 표시. 등급 반환.</summary>
    public int Finish()
    {
        Recording = false;
        p95Ltr = Percentile(ltrAbsSamples, 95f);   // 실측 p95(|LTR|) = surrogate가 예측하려는 값
        int grade = ComputeGrade();
        lastGrade = grade;
        lastSummary =
            $" ▣ 주행 종료 위험도\n" +
            $" p95LTR  {p95Ltr * 100f:F0}/100  ({p95Ltr:F3})\n" +
            $" 등급  {grade}  {GradeName(grade)}\n" +
            $" maxLTR {maxAbsLtr:F2}  maxRoll {maxAbsRollDeg:F1}°  이탈 {fellCount}";
        Debug.Log($"기록 종료 ({Time.time - startTime:F1}s): " +
                  $"위험도 p95LTR={p95Ltr:F4} ({p95Ltr * 100f:F0}/100) 등급={grade}({GradeName(grade)}) | " +
                  $"maxLTR={maxAbsLtr:F2} maxRoll={maxAbsRollDeg:F1}° 이동={maxShiftM:F2}m 이탈={fellCount} 전복={(RolloverDetected ? 1 : 0)}");
        return grade;
    }

    /// <summary>등급 = surrogate와 동일한 p95LTR 밴드. (전복 발생 시엔 무조건 3)</summary>
    private int ComputeGrade()
    {
        if (RolloverDetected || p95Ltr >= p95Cut3) return 3;
        if (p95Ltr >= p95Cut2) return 2;
        if (p95Ltr >= p95Cut1) return 1;
        return 0;
    }

    private static string GradeName(int g) => g switch { 3 => "고위험", 2 => "위험", 1 => "주의", _ => "안전" };

    /// <summary>선형보간 백분위 (np.percentile 기본과 동일) — 라벨 계산식과 일치.</summary>
    private static float Percentile(List<float> v, float pct)
    {
        if (v.Count == 0) return 0f;
        var a = v.ToArray();
        System.Array.Sort(a);
        float idx = (pct / 100f) * (a.Length - 1);
        int lo = Mathf.FloorToInt(idx), hi = Mathf.CeilToInt(idx);
        return lo == hi ? a[lo] : Mathf.Lerp(a[lo], a[hi], idx - lo);
    }

    private static float NormalizeAngle(float a) => a > 180f ? a - 360f : a;
}
