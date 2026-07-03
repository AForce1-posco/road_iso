using System.IO;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DataLogger : MonoBehaviour
{
    [Header("Logging")]
    public float interval = 0.1f;
    public string fileName = "vehicle_dynamics_data.csv";
    public bool logToConsole = false;

    [Header("References")]
    public VehicleController vehicle;
    [Tooltip("파일명에 붙일 화물 정보 소스 (비우면 자동 검색, 없어도 동작)")]
    public CargoBedLoader cargoLoader;

    private Rigidbody rb;
    private StreamWriter writer;         // 케이스별 시계열 파일 (기존 유지)
    private StreamWriter combinedWriter; // 배치 전체 시계열 통합 파일 (신규)

    private float timer;
    private bool recording;              // BeginRun~EndRun 사이에만 true (케이스 주행 구간)
    private Vector3 lastVelocity;
    private Vector3 lastEuler;
    private Vector3 lastAngularVelocity;

    // 통합 파일용: 현재 케이스 메타 + 배치 식별자
    private string batchId = "";
    private string curCaseName = "";
    private int curCargoCount;
    private float curTotalMassKg;
    private float curSecuredFrac;

    // 케이스별·통합 파일이 공유하는 시계열 컬럼 헤더
    private const string COLUMNS =
        "Time,PosX,PosY,PosZ,VelX,VelY,VelZ,SpeedKmh,AccX,AccY,AccZ," +
        "LongAcc,LatAcc,VertAcc,Roll,Pitch,Yaw,RollRate,PitchRate,YawRate," +
        "AngVelX,AngVelY,AngVelZ,AngAccX,AngAccY,AngAccZ," +
        "SteerAngle,TargetSteer,Throttle,Brake," +
        "FL_Grounded,FR_Grounded,RL_Grounded,RR_Grounded," +
        "FL_ForwardSlip,FR_ForwardSlip,RL_ForwardSlip,RR_ForwardSlip," +
        "FL_SideSlip,FR_SideSlip,RL_SideSlip,RR_SideSlip," +
        "MaxForwardSlip,MaxSideSlip,RolloverRisk,IsRollover";

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        if (vehicle == null)
            vehicle = GetComponent<VehicleController>();

        lastVelocity = rb.velocity;
        lastEuler = transform.eulerAngles;
        lastAngularVelocity = rb.angularVelocity;

        if (cargoLoader == null)
            cargoLoader = FindObjectOfType<CargoBedLoader>();
        // 파일 생성은 첫 샘플 시점으로 미룸 — 화물 적재(첫 프레임 이후)가 끝난 뒤
        // 파일명에 배치 정보를 넣기 위해서. 타임스탬프 덕에 run마다 새 파일(덮어쓰기 없음).
    }

    private void EnsureWriter()
    {
        if (writer != null) return;

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string cargoTag = "";
        if (cargoLoader != null && cargoLoader.Loaded.Count > 0)
        {
            string layout = string.IsNullOrEmpty(cargoLoader.LastLoadedPath)
                ? "layout"
                : Path.GetFileNameWithoutExtension(cargoLoader.LastLoadedPath);
            float mass = 0f;
            foreach (var c in cargoLoader.Loaded)
                mass += c.type.massKg * cargoLoader.massScale;
            cargoTag = $"_{layout}_n{cargoLoader.Loaded.Count}_{mass:F0}kg";
        }

        string path = Path.Combine(Application.persistentDataPath, $"{baseName}_{stamp}{cargoTag}.csv");

        try
        {
            writer = new StreamWriter(path, false);
            writer.WriteLine(COLUMNS);
            Debug.Log("케이스 시계열 CSV 저장 시작: " + path);
        }
        catch (System.Exception e)
        {
            writer = null;
            Debug.LogError($"케이스 시계열 파일 열기 실패: {path}\n{e.Message}");
        }
    }

    // 물리 스텝 기반 기록 — Update(프레임)로 하면 배속(timeScale) 시 한 프레임에 시뮬시간이 크게
    // 점프해 0.1초 간격이 깨진다. FixedUpdate는 배속과 무관하게 항상 fixedDeltaTime(0.01s)로 돌므로
    // 여기서 interval마다 찍으면 배속 몇 배든 정확히 0.1초 간격 보장.
    void FixedUpdate()
    {
        if (!recording) return;          // BeginRun 전 / EndRun 후엔 기록 안 함 (안정화·리셋 구간 제외)

        // 녹화 중인데 파일이 닫혀 있으면(예외 등) 재생성 시도 — 데이터 유실·크래시 방지
        if (writer == null) EnsureWriter();

        timer += Time.fixedDeltaTime;
        if (timer < interval)
            return;

        timer -= interval;              // 누적 오차 방지 (0으로 리셋하면 미세 드리프트)
        LogData();                        // 파일은 BeginRun()에서 이미 열림
    }

    void LogData()
    {
        Vector3 pos = transform.position;
        Vector3 vel = rb.velocity;
        Vector3 acc = (vel - lastVelocity) / interval;

        Vector3 localAcc = transform.InverseTransformDirection(acc);

        float longAcc = localAcc.z;
        float latAcc = localAcc.x;
        float vertAcc = localAcc.y;

        Vector3 euler = transform.eulerAngles;

        float roll = NormalizeAngle(euler.z);
        float pitch = NormalizeAngle(euler.x);
        float yaw = NormalizeAngle(euler.y);

        float rollRate = Mathf.DeltaAngle(lastEuler.z, euler.z) / interval;
        float pitchRate = Mathf.DeltaAngle(lastEuler.x, euler.x) / interval;
        float yawRate = Mathf.DeltaAngle(lastEuler.y, euler.y) / interval;

        Vector3 angVel = rb.angularVelocity;
        Vector3 angAcc = (angVel - lastAngularVelocity) / interval;

        float speedKmh = vel.magnitude * 3.6f;

        float steerAngle = vehicle != null ? vehicle.CurrentSteerAngle : 0f;
        float targetSteer = vehicle != null ? vehicle.targetSteer : 0f;
        float throttle = vehicle != null ? vehicle.targetThrottle : 0f;
        float brake = vehicle != null ? vehicle.targetBrake : 0f;

        WheelData fl = GetWheelData(vehicle != null ? vehicle.frontLeft : null);
        WheelData fr = GetWheelData(vehicle != null ? vehicle.frontRight : null);
        WheelData rl = GetWheelData(vehicle != null ? vehicle.rearLeft : null);
        WheelData rr = GetWheelData(vehicle != null ? vehicle.rearRight : null);

        float maxForwardSlip = Mathf.Max(
            Mathf.Abs(fl.forwardSlip),
            Mathf.Abs(fr.forwardSlip),
            Mathf.Abs(rl.forwardSlip),
            Mathf.Abs(rr.forwardSlip)
        );

        float maxSideSlip = Mathf.Max(
            Mathf.Abs(fl.sideSlip),
            Mathf.Abs(fr.sideSlip),
            Mathf.Abs(rl.sideSlip),
            Mathf.Abs(rr.sideSlip)
        );

        float rolloverRisk = CalculateRolloverRisk(roll, latAcc, rollRate, maxSideSlip);
        int isRollover = Vector3.Dot(transform.up, Vector3.up) < 0.5f ? 1 : 0;

        string line =
            $"{Time.time:F3}," +
            $"{pos.x:F3},{pos.y:F3},{pos.z:F3}," +
            $"{vel.x:F3},{vel.y:F3},{vel.z:F3},{speedKmh:F3}," +
            $"{acc.x:F3},{acc.y:F3},{acc.z:F3}," +
            $"{longAcc:F3},{latAcc:F3},{vertAcc:F3}," +
            $"{roll:F3},{pitch:F3},{yaw:F3}," +
            $"{rollRate:F3},{pitchRate:F3},{yawRate:F3}," +
            $"{angVel.x:F3},{angVel.y:F3},{angVel.z:F3}," +
            $"{angAcc.x:F3},{angAcc.y:F3},{angAcc.z:F3}," +
            $"{steerAngle:F3},{targetSteer:F3},{throttle:F3},{brake:F3}," +
            $"{fl.grounded},{fr.grounded},{rl.grounded},{rr.grounded}," +
            $"{fl.forwardSlip:F3},{fr.forwardSlip:F3},{rl.forwardSlip:F3},{rr.forwardSlip:F3}," +
            $"{fl.sideSlip:F3},{fr.sideSlip:F3},{rl.sideSlip:F3},{rr.sideSlip:F3}," +
            $"{maxForwardSlip:F3},{maxSideSlip:F3}," +
            $"{rolloverRisk:F3},{isRollover}";

        // 케이스별 파일 (열려 있을 때만 — 파일 핸들 null이어도 크래시 금지)
        if (writer != null)
        {
            writer.WriteLine(line);
            writer.Flush();
        }

        // 통합 파일에도 같은 행 + 케이스 식별 컬럼을 앞에 붙여 기록
        if (combinedWriter != null)
        {
            combinedWriter.WriteLine(
                $"{batchId},{curCaseName},{curCargoCount}," +
                $"{curTotalMassKg:F1},{curSecuredFrac:F3}," + line);
            combinedWriter.Flush();
        }

        if (logToConsole)
            Debug.Log(line);

        lastVelocity = vel;
        lastEuler = euler;
        lastAngularVelocity = angVel;
    }

    WheelData GetWheelData(WheelCollider wheel)
    {
        WheelData data = new WheelData();

        if (wheel == null)
            return data;

        WheelHit hit;

        if (wheel.GetGroundHit(out hit))
        {
            data.grounded = 1;
            data.forwardSlip = hit.forwardSlip;
            data.sideSlip = hit.sidewaysSlip;
        }
        else
        {
            data.grounded = 0;
            data.forwardSlip = 0f;
            data.sideSlip = 0f;
        }

        return data;
    }

    float CalculateRolloverRisk(float roll, float latAcc, float rollRate, float sideSlip)
    {
        float rollRisk = Mathf.InverseLerp(5f, 30f, Mathf.Abs(roll));
        float latRisk = Mathf.InverseLerp(3f, 8f, Mathf.Abs(latAcc));
        float rollRateRisk = Mathf.InverseLerp(5f, 30f, Mathf.Abs(rollRate));
        float slipRisk = Mathf.InverseLerp(0.2f, 1.0f, Mathf.Abs(sideSlip));

        float risk =
            rollRisk * 0.35f +
            latRisk * 0.35f +
            rollRateRisk * 0.20f +
            slipRisk * 0.10f;

        return Mathf.Clamp01(risk);
    }

    float NormalizeAngle(float angle)
    {
        if (angle > 180f)
            angle -= 360f;

        return angle;
    }

    /// <summary>배치(여러 케이스 일괄) 시작: 전체 케이스가 한 파일에 쌓이는 통합 시계열 파일을 연다.
    /// Assets/Data/Results/combined_timeseries_&lt;runId&gt;.csv. 케이스 식별 컬럼이 앞에 붙는다.</summary>
    public void BeginBatch(string runId)
    {
        batchId = runId;
        CargoPaths.EnsureAll();
        string path = Path.Combine(CargoPaths.ResultsDir, $"combined_timeseries_{runId}.csv");
        combinedWriter = new StreamWriter(path, false);
        combinedWriter.WriteLine("run_id,case_name,cargo_count,total_mass_kg,secured_frac," + COLUMNS);
        Debug.Log("통합 시계열 CSV 저장 시작: " + path);
    }

    /// <summary>배치 종료: 통합 파일을 닫는다.</summary>
    public void EndBatch()
    {
        if (combinedWriter != null)
        {
            combinedWriter.Flush();
            combinedWriter.Close();
            combinedWriter = null;
        }
    }

    /// <summary>케이스 주행 시작: 케이스별 새 파일을 열고 샘플링 시작.
    /// 적재(Load) 후에 호출해야 파일명·메타에 화물정보가 정확히 들어감.
    /// caseName/cargoCount/totalMass/securedFrac은 통합 파일 식별 컬럼에 쓰임.</summary>
    public void BeginRun(string caseName, int cargoCount, float totalMassKg, float securedFrac)
    {
        curCaseName = caseName;
        curCargoCount = cargoCount;
        curTotalMassKg = totalMassKg;
        curSecuredFrac = securedFrac;

        CloseWriter();                    // 혹시 열려 있던 케이스 파일 정리
        EnsureWriter();                   // 새 케이스 파일 생성 (화물 태그 포함)
        // 리셋 텔레포트 여파가 첫 샘플 미분값(가속도 등)에 튀지 않게 초기화
        if (rb != null)
        {
            lastVelocity = rb.velocity;
            lastAngularVelocity = rb.angularVelocity;
        }
        lastEuler = transform.eulerAngles;
        timer = 0f;
        recording = true;
    }

    /// <summary>케이스 주행 종료: 케이스 파일을 닫고 샘플링 정지 (통합 파일은 유지).</summary>
    public void EndRun()
    {
        recording = false;
        CloseWriter();
    }

    /// <summary>배치 일괄 실행용(하위호환): 현재 케이스 파일을 닫는다.</summary>
    public void StartNewFile() => CloseWriter();

    private void CloseWriter()
    {
        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer = null;
        }
    }

    void OnApplicationQuit() { CloseWriter(); EndBatch(); }

    void OnDestroy() { CloseWriter(); EndBatch(); }

    struct WheelData
    {
        public int grounded;
        public float forwardSlip;
        public float sideSlip;
    }
}