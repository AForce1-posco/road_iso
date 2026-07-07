using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// 시계열 데이터 로거 (병합본).
/// - 데이터 컬럼·측정·CSV 형식: origin/main 스키마 그대로 (메타/노면조건/4륜 하중/LTR/바퀴들림 등 전부 유지)
/// - 오케스트레이션: DynamicSceneController가 BeginBatch→(BeginRun→EndRun)*→EndBatch로 구동
///   · 케이스별 파일(persistentDataPath, 화물 태그 파일명) + 배치 통합 파일(Assets/Data/Results) 둘 다 출력
///   · recording 게이트로 주행 구간만 기록(안정화·리셋 구간 제외)
///   · 화물 컬럼(CargoCount/TotalMassKg/SecuredFrac) 추가
/// - FixedUpdate 기반 샘플링 → 배속(timeScale) 무관하게 interval(0.1s) 간격 보장
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DataLogger : MonoBehaviour
{
    [Header("Logging")]
    public float interval = 0.1f;
    public string fileName = "vehicle_dynamics_v3.csv";
    [Tooltip("모든 Play가 이 한 파일에 계속 append됨 (Assets/Data/Results). 각 행의 RunID로 실행 구분, LayoutID로 케이스 구분. ※ combinedChunkCases>0이면 이 이름 대신 청크 파일로 저장")]
    public string combinedFileName = "combined_timeseries_all.csv";
    [Tooltip("통합 시계열을 N 케이스마다 새 파일로 분할 (100만 줄 방지). 0=분할 안 함(한 파일). 예: 50 → combined_timeseries_001-050.csv, 051-100.csv …")]
    public int combinedChunkCases = 50;
    public bool logToConsole = false;
    public int flushEveryRows = 50;

    [Header("IDs")]
    public string datasetVersion = "V1.0";
    public string layoutID = "LAYOUT_001";   // BeginRun에서 케이스명으로 덮어씀
    public string runID = "";                // BeginBatch에서 배치 id로 덮어씀
    public string scenarioID = "ISO_DLC";

    [Header("Run Conditions")]
    public float targetSpeedKmh = 45f;
    public string frictionCondition = "DRY";
    public string roadCondition = "FLAT";
    public float roadBankAngleDeg = 0f;
    public float roadSlopeDeg = 0f;
    public float vehicleBaseMassKg = 3500f;

    [Header("References")]
    public VehicleController vehicle;
    [Tooltip("화물 정보 소스 (비우면 자동 검색). 파일명 태그 + 화물 컬럼에 사용")]
    public CargoBedLoader cargoLoader;

    [Header("Wheel Lift")]
    public float wheelLiftForceThreshold = 50f;
    public float wheelLiftDurationThreshold = 0.1f;

    private Rigidbody rb;
    private StreamWriter writer;         // 케이스별 파일
    private StreamWriter combinedWriter; // 배치 통합 파일 (또는 현재 청크)
    private int runIndexInBatch;         // 배치 내 케이스 순번(1..) — 청크 롤오버용
    private bool recording;              // BeginRun~EndRun 사이에만 true

    private float logTimer;
    private float runStartTime;
    private float lastLogTime;

    private Vector3 lastVelocity;
    private Vector3 lastEuler;
    private Vector3 lastAngularVelocity;

    private int sampleIndex;
    private int rowsSinceFlush;

    private float leftLiftTimer;
    private float rightLiftTimer;
    private bool leftWheelLift;
    private bool rightWheelLift;

    // 화물 메타 (BeginRun에서 세팅) — origin/main 스키마에 추가되는 컬럼
    private int curCargoCount;
    private float curTotalMassKg;
    private float curSecuredFrac;

    private static readonly CultureInfo CsvCulture = CultureInfo.InvariantCulture;

    // 케이스별·통합 파일 공통 헤더. origin/main 컬럼 전부 유지 + Cargo* 3개 추가(조건 블록 뒤).
    private const string HEADER =
        "DatasetVersion,LayoutID,RunID,ScenarioID,SampleIndex,RunTime,UnityTime," +
        "TargetSpeedKmh,FrictionCondition,RoadCondition,RoadBankAngleDeg,RoadSlopeDeg,VehicleBaseMassKg," +
        "CargoCount,TotalMassKg,SecuredFrac," +
        "PosX,PosY,PosZ,VelX,VelY,VelZ,SpeedKmh," +
        "AccX,AccY,AccZ,LongAcc,LatAcc,VertAcc," +
        "Roll,Pitch,Yaw,RollRate,PitchRate,YawRate," +
        "AngVelX,AngVelY,AngVelZ,AngAccX,AngAccY,AngAccZ," +
        "SteerAngle,TargetSteer,Throttle,Brake," +
        "FL_Grounded,FR_Grounded,RL_Grounded,RR_Grounded," +
        "FL_NormalForce,FR_NormalForce,RL_NormalForce,RR_NormalForce," +
        "LeftNormalForce,RightNormalForce,FrontNormalForce,RearNormalForce," +
        "LTR_Total,LTR_Front,LTR_Rear," +
        "LeftWheelLift,RightWheelLift,AnyWheelLift," +
        "FL_ForwardSlip,FR_ForwardSlip,RL_ForwardSlip,RR_ForwardSlip," +
        "FL_SideSlip,FR_SideSlip,RL_SideSlip,RR_SideSlip," +
        "MaxForwardSlip,MaxSideSlip,LegacyRolloverRisk,IsRollover";

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (vehicle == null) vehicle = GetComponent<VehicleController>();
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();
        if (string.IsNullOrWhiteSpace(runID))
            runID = "RUN_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CsvCulture);
        // 파일 열기는 BeginBatch/BeginRun에서 (recording 게이트 방식) — Start에선 초기화만.
    }

    // ── 배치 통합 파일 (전체 케이스 한 파일) ─────────────────────────────────
    /// <summary>배치 시작: Assets/Data/Results/combined_timeseries_&lt;runId&gt;.csv 열기. runId는 RunID 컬럼에도 반영.</summary>
    public void BeginBatch(string runId)
    {
        if (!string.IsNullOrEmpty(runId)) runID = runId;
        CargoPaths.EnsureAll();
        runIndexInBatch = 0;
        // 분할 OFF(0) → 지금 한 파일 염. 분할 ON → 첫 케이스(BeginRun)에서 청크 파일 염.
        if (combinedChunkCases <= 0)
            OpenCombined(combinedFileName);
    }

    /// <summary>통합/청크 파일 하나 열기. append 모드, 새 파일이면 헤더 1줄.</summary>
    private void OpenCombined(string name)
    {
        string path = Path.Combine(CargoPaths.ResultsDir, name);
        try
        {
            bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            combinedWriter = new StreamWriter(path, true);       // append 모드
            if (isNew) combinedWriter.WriteLine(HEADER);          // 새 파일일 때만 헤더 1줄
            Debug.Log($"통합 시계열 CSV {(isNew ? "생성" : "이어쓰기")}: {path}  (RunID={runID})");
        }
        catch (Exception e)
        {
            combinedWriter = null;
            Debug.LogError($"통합 시계열 파일 열기 실패: {path}\n{e.Message}");
        }
    }

    public void EndBatch()
    {
        if (combinedWriter != null)
        {
            combinedWriter.Flush();
            combinedWriter.Close();
            combinedWriter = null;
        }
    }

    // ── 케이스별 파일 + 기록 시작/종료 ───────────────────────────────────────
    /// <summary>케이스 주행 시작: LayoutID=케이스명, 화물 메타 세팅, 케이스별 파일 열고 샘플링 시작.</summary>
    public void BeginRun(string caseName, int cargoCount, float totalMassKg, float securedFrac)
    {
        // 통합 파일 분할: 배치 내 케이스 순번 기준 N개마다 새 청크 파일로 롤오버
        runIndexInBatch++;
        if (combinedChunkCases > 0 && (runIndexInBatch - 1) % combinedChunkCases == 0)
        {
            if (combinedWriter != null) { combinedWriter.Flush(); combinedWriter.Close(); combinedWriter = null; }
            int start = runIndexInBatch;
            int end = start + combinedChunkCases - 1;
            OpenCombined($"combined_timeseries_{start:D3}-{end:D3}.csv");
        }

        if (!string.IsNullOrEmpty(caseName)) layoutID = caseName;
        curCargoCount = cargoCount;
        curTotalMassKg = totalMassKg;
        curSecuredFrac = securedFrac;

        // 케이스별 상태 리셋 (각 케이스가 SampleIndex 0 · RunTime 0에서 시작)
        sampleIndex = 0;
        rowsSinceFlush = 0;
        logTimer = 0f;
        leftLiftTimer = rightLiftTimer = 0f;
        leftWheelLift = rightWheelLift = false;
        runStartTime = Time.time;
        lastLogTime = Time.time;
        if (rb != null) { lastVelocity = rb.velocity; lastAngularVelocity = rb.angularVelocity; }
        lastEuler = transform.eulerAngles;

        OpenCaseWriter();
        recording = true;
    }

    /// <summary>케이스 주행 종료: 케이스 파일 닫고 샘플링 정지 (통합 파일은 유지).</summary>
    public void EndRun()
    {
        recording = false;
        CloseCaseWriter();
    }

    /// <summary>하위호환: 케이스 파일만 닫기.</summary>
    public void StartNewFile() => CloseCaseWriter();

    private void OpenCaseWriter()
    {
        CloseCaseWriter();
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CsvCulture);

        string cargoTag = "";
        if (cargoLoader != null && cargoLoader.Loaded.Count > 0)
        {
            string layout = string.IsNullOrEmpty(cargoLoader.LastLoadedPath)
                ? "layout" : Path.GetFileNameWithoutExtension(cargoLoader.LastLoadedPath);
            float mass = 0f;
            foreach (var c in cargoLoader.Loaded) mass += c.type.massKg * cargoLoader.massScale;
            cargoTag = $"_{layout}_n{cargoLoader.Loaded.Count}_{mass:F0}kg";
        }

        string path = Path.Combine(Application.persistentDataPath, $"{baseName}_{stamp}{cargoTag}.csv");
        try
        {
            writer = new StreamWriter(path, false);
            writer.WriteLine(HEADER);
            Debug.Log("케이스 시계열 CSV 저장 시작: " + path);
        }
        catch (Exception e)
        {
            writer = null;
            Debug.LogError($"케이스 시계열 파일 열기 실패: {path}\n{e.Message}");
        }
    }

    private void CloseCaseWriter()
    {
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer = null;
        }
    }

    // ── 샘플링 (배속 무관: FixedUpdate + interval 누적) ──────────────────────
    void FixedUpdate()
    {
        if (!recording) return;
        if (writer == null && combinedWriter == null) return;

        logTimer += Time.fixedDeltaTime;
        if (logTimer < interval) return;
        logTimer -= interval;   // 누적 오차 방지
        LogData();
    }

    void LogData()
    {
        float currentTime = Time.time;
        float actualDeltaTime = currentTime - lastLogTime;
        if (actualDeltaTime <= 0.0001f) actualDeltaTime = interval;
        float runTime = currentTime - runStartTime;

        Vector3 pos = transform.position;
        Vector3 vel = rb.velocity;
        Vector3 acc = (vel - lastVelocity) / actualDeltaTime;
        Vector3 localAcc = transform.InverseTransformDirection(acc);
        float longAcc = localAcc.z, latAcc = localAcc.x, vertAcc = localAcc.y;

        Vector3 euler = transform.eulerAngles;
        float roll = NormalizeAngle(euler.z), pitch = NormalizeAngle(euler.x), yaw = NormalizeAngle(euler.y);
        float rollRate = Mathf.DeltaAngle(lastEuler.z, euler.z) / actualDeltaTime;
        float pitchRate = Mathf.DeltaAngle(lastEuler.x, euler.x) / actualDeltaTime;
        float yawRate = Mathf.DeltaAngle(lastEuler.y, euler.y) / actualDeltaTime;

        Vector3 angVel = rb.angularVelocity;
        Vector3 angAcc = (angVel - lastAngularVelocity) / actualDeltaTime;
        float speedKmh = vel.magnitude * 3.6f;

        float steerAngle = vehicle != null ? vehicle.CurrentSteerAngle : 0f;
        float targetSteer = vehicle != null ? vehicle.targetSteer : 0f;
        float throttle = vehicle != null ? vehicle.targetThrottle : 0f;
        float brake = vehicle != null ? vehicle.targetBrake : 0f;

        WheelData fl = GetWheelData(vehicle != null ? vehicle.frontLeft : null);
        WheelData fr = GetWheelData(vehicle != null ? vehicle.frontRight : null);
        WheelData rl = GetWheelData(vehicle != null ? vehicle.rearLeft : null);
        WheelData rr = GetWheelData(vehicle != null ? vehicle.rearRight : null);

        float leftNormalForce = fl.normalForce + rl.normalForce;
        float rightNormalForce = fr.normalForce + rr.normalForce;
        float frontNormalForce = fl.normalForce + fr.normalForce;
        float rearNormalForce = rl.normalForce + rr.normalForce;

        float ltrTotal = CalculateLTR(rightNormalForce, leftNormalForce);
        float ltrFront = CalculateLTR(fr.normalForce, fl.normalForce);
        float ltrRear = CalculateLTR(rr.normalForce, rl.normalForce);

        UpdateWheelLift(leftNormalForce, rightNormalForce, fl, fr, rl, rr, actualDeltaTime);
        int anyWheelLift = leftWheelLift || rightWheelLift ? 1 : 0;

        float maxForwardSlip = Mathf.Max(Mathf.Abs(fl.forwardSlip), Mathf.Abs(fr.forwardSlip),
                                         Mathf.Abs(rl.forwardSlip), Mathf.Abs(rr.forwardSlip));
        float maxSideSlip = Mathf.Max(Mathf.Abs(fl.sideSlip), Mathf.Abs(fr.sideSlip),
                                      Mathf.Abs(rl.sideSlip), Mathf.Abs(rr.sideSlip));

        float legacyRisk = CalculateLegacyRolloverRisk(roll, latAcc, rollRate, maxSideSlip);
        int isRollover = Vector3.Dot(transform.up, Vector3.up) < 0.5f ? 1 : 0;

        string line = string.Join(",",
            EscapeCsv(datasetVersion), EscapeCsv(layoutID), EscapeCsv(runID), EscapeCsv(scenarioID),
            sampleIndex.ToString(CsvCulture), F(runTime), F(currentTime),
            F(targetSpeedKmh), EscapeCsv(frictionCondition), EscapeCsv(roadCondition),
            F(roadBankAngleDeg), F(roadSlopeDeg), F(vehicleBaseMassKg),
            curCargoCount.ToString(CsvCulture), F(curTotalMassKg), F(curSecuredFrac),
            F(pos.x), F(pos.y), F(pos.z),
            F(vel.x), F(vel.y), F(vel.z), F(speedKmh),
            F(acc.x), F(acc.y), F(acc.z),
            F(longAcc), F(latAcc), F(vertAcc),
            F(roll), F(pitch), F(yaw),
            F(rollRate), F(pitchRate), F(yawRate),
            F(angVel.x), F(angVel.y), F(angVel.z),
            F(angAcc.x), F(angAcc.y), F(angAcc.z),
            F(steerAngle), F(targetSteer), F(throttle), F(brake),
            fl.grounded.ToString(CsvCulture), fr.grounded.ToString(CsvCulture),
            rl.grounded.ToString(CsvCulture), rr.grounded.ToString(CsvCulture),
            F(fl.normalForce), F(fr.normalForce), F(rl.normalForce), F(rr.normalForce),
            F(leftNormalForce), F(rightNormalForce), F(frontNormalForce), F(rearNormalForce),
            F(ltrTotal), F(ltrFront), F(ltrRear),
            BoolToInt(leftWheelLift), BoolToInt(rightWheelLift), anyWheelLift.ToString(CsvCulture),
            F(fl.forwardSlip), F(fr.forwardSlip), F(rl.forwardSlip), F(rr.forwardSlip),
            F(fl.sideSlip), F(fr.sideSlip), F(rl.sideSlip), F(rr.sideSlip),
            F(maxForwardSlip), F(maxSideSlip),
            F(legacyRisk), isRollover.ToString(CsvCulture));

        if (writer != null) writer.WriteLine(line);
        if (combinedWriter != null) combinedWriter.WriteLine(line);

        sampleIndex++;
        rowsSinceFlush++;
        if (rowsSinceFlush >= flushEveryRows)
        {
            if (writer != null) writer.Flush();
            if (combinedWriter != null) combinedWriter.Flush();
            rowsSinceFlush = 0;
        }

        if (logToConsole) Debug.Log(line);

        lastVelocity = vel;
        lastEuler = euler;
        lastAngularVelocity = angVel;
        lastLogTime = currentTime;
    }

    WheelData GetWheelData(WheelCollider wheel)
    {
        WheelData data = new WheelData();
        if (wheel == null) return data;

        if (wheel.GetGroundHit(out WheelHit hit))
        {
            data.grounded = 1;
            data.forwardSlip = hit.forwardSlip;
            data.sideSlip = hit.sidewaysSlip;
            data.normalForce = Mathf.Max(0f, hit.force);
        }
        else
        {
            data.grounded = 0;
            data.forwardSlip = 0f;
            data.sideSlip = 0f;
            data.normalForce = 0f;
        }
        return data;
    }

    float CalculateLTR(float rightForce, float leftForce)
    {
        float denominator = rightForce + leftForce;
        if (denominator <= 0.001f) return 0f;
        return Mathf.Clamp((rightForce - leftForce) / denominator, -1f, 1f);
    }

    void UpdateWheelLift(float leftForce, float rightForce,
        WheelData fl, WheelData fr, WheelData rl, WheelData rr, float deltaTime)
    {
        bool leftLiftCandidate = leftForce <= wheelLiftForceThreshold && fl.grounded == 0 && rl.grounded == 0;
        bool rightLiftCandidate = rightForce <= wheelLiftForceThreshold && fr.grounded == 0 && rr.grounded == 0;

        leftLiftTimer = leftLiftCandidate ? leftLiftTimer + deltaTime : 0f;
        rightLiftTimer = rightLiftCandidate ? rightLiftTimer + deltaTime : 0f;

        leftWheelLift = leftLiftTimer >= wheelLiftDurationThreshold;
        rightWheelLift = rightLiftTimer >= wheelLiftDurationThreshold;
    }

    float CalculateLegacyRolloverRisk(float roll, float latAcc, float rollRate, float sideSlip)
    {
        float rollRisk = Mathf.InverseLerp(5f, 30f, Mathf.Abs(roll));
        float latRisk = Mathf.InverseLerp(3f, 8f, Mathf.Abs(latAcc));
        float rollRateRisk = Mathf.InverseLerp(5f, 30f, Mathf.Abs(rollRate));
        float slipRisk = Mathf.InverseLerp(0.2f, 1.0f, Mathf.Abs(sideSlip));
        float risk = rollRisk * 0.35f + latRisk * 0.35f + rollRateRisk * 0.20f + slipRisk * 0.10f;
        return Mathf.Clamp01(risk);
    }

    float NormalizeAngle(float angle) => angle > 180f ? angle - 360f : angle;
    string F(float value) => value.ToString("F6", CsvCulture);
    string BoolToInt(bool value) => value ? "1" : "0";

    string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    void OnApplicationQuit() { CloseCaseWriter(); EndBatch(); }
    void OnDestroy() { CloseCaseWriter(); EndBatch(); }

    struct WheelData
    {
        public int grounded;
        public float forwardSlip;
        public float sideSlip;
        public float normalForce;
    }
}
