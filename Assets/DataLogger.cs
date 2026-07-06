using System;
using System.Globalization;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class DataLogger : MonoBehaviour
{
    [Header("Logging")]
    public float interval = 0.1f;
    public string fileName = "vehicle_dynamics_v3.csv";
    public string combinedFileName = "combined_timeseries_all.csv";
    public bool logToConsole = false;
    public int flushEveryRows = 50;

    [Header("IDs")]
    public string datasetVersion = "V1.0";
    public string layoutID = "LAYOUT_001";
    public string runID = "";
    public string scenarioID = "ISO_DLC";

    [Header("Run Conditions")]
    [Tooltip("purePursuit이 연결되어 있으면 이 값은 무시되고 실제 목표속도를 자동으로 읽어옵니다.")]
    public float targetSpeedKmh = 45f;
    public string frictionCondition = "DRY";
    public string roadCondition = "FLAT";
    public float roadBankAngleDeg = 0f;
    public float roadSlopeDeg = 0f;
    public float vehicleBaseMassKg = 3500f;

    [Header("References")]
    public VehicleController vehicle;
    public CargoBedLoader cargoLoader;
    public PurePursuitController purePursuit;

    [Header("Wheel Lift")]
    public float wheelLiftForceThreshold = 50f;
    public float wheelLiftDurationThreshold = 0.1f;

    [Header("Path Failure")]
    [Tooltip("DynamicSceneController 또는 외부 감시 코드에서 현재 경로 이탈 거리 값을 넣어줄 수 있습니다.")]
    public float pathDeviation = 0f;

    [Tooltip("1이면 주행 중 경로 이탈로 종료된 Run입니다.")]
    public int pathFailure = 0;

    [Tooltip("RUNNING / NORMAL / PATH_FAILURE / ROLLOVER / TIMEOUT")]
    public string runEndReason = "RUNNING";

    private Rigidbody rb;
    private StreamWriter writer;
    private StreamWriter combinedWriter;
    private bool recording;

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

    private int curCargoCount;
    private float curTotalMassKg;
    private float curSecuredFrac;

    private static readonly CultureInfo CsvCulture = CultureInfo.InvariantCulture;

    private const string HEADER =
        "DatasetVersion,LayoutID,RunID,ScenarioID,SampleIndex,RunTime,UnityTime," +
        "TargetSpeedKmh,FrictionCondition,RoadCondition,RoadBankAngleDeg,RoadSlopeDeg,VehicleBaseMassKg," +
        "CargoCount,TotalMassKg,SecuredFrac," +
        "PathDeviation,PathFailure,RunEndReason," +
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

        if (vehicle == null)
            vehicle = GetComponent<VehicleController>();

        if (cargoLoader == null)
            cargoLoader = FindObjectOfType<CargoBedLoader>();

        if (purePursuit == null)
            purePursuit = GetComponent<PurePursuitController>();

        if (string.IsNullOrWhiteSpace(runID))
            runID = "RUN_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CsvCulture);
    }

    public void BeginBatch(string runId)
    {
        if (!string.IsNullOrEmpty(runId))
            runID = runId;

        CargoPaths.EnsureAll();

        string path = Path.Combine(CargoPaths.ResultsDir, combinedFileName);

        try
        {
            bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

            combinedWriter = new StreamWriter(path, true);

            if (isNew)
                combinedWriter.WriteLine(HEADER);

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

    public void BeginRun(string caseName, int cargoCount, float totalMassKg, float securedFrac)
    {
        if (!string.IsNullOrEmpty(caseName))
            layoutID = caseName;

        curCargoCount = cargoCount;
        curTotalMassKg = totalMassKg;
        curSecuredFrac = securedFrac;

        sampleIndex = 0;
        rowsSinceFlush = 0;
        logTimer = 0f;

        leftLiftTimer = 0f;
        rightLiftTimer = 0f;
        leftWheelLift = false;
        rightWheelLift = false;

        pathDeviation = 0f;
        pathFailure = 0;
        runEndReason = "RUNNING";

        runStartTime = Time.time;
        lastLogTime = Time.time;

        if (rb != null)
        {
            lastVelocity = rb.velocity;
            lastAngularVelocity = rb.angularVelocity;
        }

        lastEuler = transform.eulerAngles;

        OpenCaseWriter();
        recording = true;
    }

    public void EndRun()
    {
        recording = false;
        CloseCaseWriter();
    }

    public void StartNewFile()
    {
        CloseCaseWriter();
    }

    public void SetPathStatus(float deviation, bool isPathFailure)
    {
        pathDeviation = deviation;

        if (isPathFailure)
        {
            pathFailure = 1;

            if (runEndReason == "RUNNING")
                runEndReason = "PATH_FAILURE";
        }
    }

    public void SetRunEndReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        runEndReason = reason;

        if (reason == "PATH_FAILURE")
            pathFailure = 1;
    }

    public bool IsRecording()
    {
        return recording;
    }

    private void OpenCaseWriter()
    {
        CloseCaseWriter();

        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CsvCulture);

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

        string path = Path.Combine(
            Application.persistentDataPath,
            $"{baseName}_{stamp}{cargoTag}.csv"
        );

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

    void FixedUpdate()
    {
        if (!recording)
            return;

        if (writer == null && combinedWriter == null)
            return;

        logTimer += Time.fixedDeltaTime;

        if (logTimer < interval)
            return;

        logTimer -= interval;

        LogData();
    }

    float GetEffectiveTargetSpeedKmh()
    {
        if (purePursuit == null)
            return targetSpeedKmh;

        return purePursuit.UsingISO
            ? purePursuit.isoTargetSpeedKmh
            : purePursuit.targetSpeedKmh;
    }

    void LogData()
    {
        float currentTime = Time.time;
        float actualDeltaTime = currentTime - lastLogTime;

        if (actualDeltaTime <= 0.0001f)
            actualDeltaTime = interval;

        float runTime = currentTime - runStartTime;

        Vector3 pos = transform.position;
        Vector3 vel = rb.velocity;
        Vector3 acc = (vel - lastVelocity) / actualDeltaTime;

        Vector3 localAcc = transform.InverseTransformDirection(acc);

        float longAcc = localAcc.z;
        float latAcc = localAcc.x;
        float vertAcc = localAcc.y;

        Vector3 euler = transform.eulerAngles;

        float roll = NormalizeAngle(euler.z);
        float pitch = NormalizeAngle(euler.x);
        float yaw = NormalizeAngle(euler.y);

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
        float effectiveTargetSpeed = GetEffectiveTargetSpeedKmh();

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

        UpdateWheelLift(
            leftNormalForce,
            rightNormalForce,
            fl,
            fr,
            rl,
            rr,
            actualDeltaTime
        );

        int anyWheelLift = leftWheelLift || rightWheelLift ? 1 : 0;

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

        float legacyRisk = CalculateLegacyRolloverRisk(
            roll,
            latAcc,
            rollRate,
            maxSideSlip
        );

        int isRollover =
            Vector3.Dot(transform.up, Vector3.up) < 0.5f
                ? 1
                : 0;

        if (isRollover == 1 && runEndReason == "RUNNING")
            runEndReason = "ROLLOVER";

        string line = string.Join(
            ",",
            EscapeCsv(datasetVersion),
            EscapeCsv(layoutID),
            EscapeCsv(runID),
            EscapeCsv(scenarioID),
            sampleIndex.ToString(CsvCulture),
            F(runTime),
            F(currentTime),

            F(effectiveTargetSpeed),
            EscapeCsv(frictionCondition),
            EscapeCsv(roadCondition),
            F(roadBankAngleDeg),
            F(roadSlopeDeg),
            F(vehicleBaseMassKg),

            curCargoCount.ToString(CsvCulture),
            F(curTotalMassKg),
            F(curSecuredFrac),

            F(pathDeviation),
            pathFailure.ToString(CsvCulture),
            EscapeCsv(runEndReason),

            F(pos.x),
            F(pos.y),
            F(pos.z),

            F(vel.x),
            F(vel.y),
            F(vel.z),
            F(speedKmh),

            F(acc.x),
            F(acc.y),
            F(acc.z),

            F(longAcc),
            F(latAcc),
            F(vertAcc),

            F(roll),
            F(pitch),
            F(yaw),

            F(rollRate),
            F(pitchRate),
            F(yawRate),

            F(angVel.x),
            F(angVel.y),
            F(angVel.z),

            F(angAcc.x),
            F(angAcc.y),
            F(angAcc.z),

            F(steerAngle),
            F(targetSteer),
            F(throttle),
            F(brake),

            fl.grounded.ToString(CsvCulture),
            fr.grounded.ToString(CsvCulture),
            rl.grounded.ToString(CsvCulture),
            rr.grounded.ToString(CsvCulture),

            F(fl.normalForce),
            F(fr.normalForce),
            F(rl.normalForce),
            F(rr.normalForce),

            F(leftNormalForce),
            F(rightNormalForce),
            F(frontNormalForce),
            F(rearNormalForce),

            F(ltrTotal),
            F(ltrFront),
            F(ltrRear),

            BoolToInt(leftWheelLift),
            BoolToInt(rightWheelLift),
            anyWheelLift.ToString(CsvCulture),

            F(fl.forwardSlip),
            F(fr.forwardSlip),
            F(rl.forwardSlip),
            F(rr.forwardSlip),

            F(fl.sideSlip),
            F(fr.sideSlip),
            F(rl.sideSlip),
            F(rr.sideSlip),

            F(maxForwardSlip),
            F(maxSideSlip),

            F(legacyRisk),
            isRollover.ToString(CsvCulture)
        );

        if (writer != null)
            writer.WriteLine(line);

        if (combinedWriter != null)
            combinedWriter.WriteLine(line);

        sampleIndex++;
        rowsSinceFlush++;

        if (rowsSinceFlush >= flushEveryRows)
        {
            if (writer != null)
                writer.Flush();

            if (combinedWriter != null)
                combinedWriter.Flush();

            rowsSinceFlush = 0;
        }

        if (logToConsole)
            Debug.Log(line);

        lastVelocity = vel;
        lastEuler = euler;
        lastAngularVelocity = angVel;
        lastLogTime = currentTime;
    }

    WheelData GetWheelData(WheelCollider wheel)
    {
        WheelData data = new WheelData();

        if (wheel == null)
            return data;

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

        if (denominator <= 0.001f)
            return 0f;

        return Mathf.Clamp(
            (rightForce - leftForce) / denominator,
            -1f,
            1f
        );
    }

    void UpdateWheelLift(
        float leftForce,
        float rightForce,
        WheelData fl,
        WheelData fr,
        WheelData rl,
        WheelData rr,
        float deltaTime
    )
    {
        bool leftLiftCandidate =
            leftForce <= wheelLiftForceThreshold &&
            fl.grounded == 0 &&
            rl.grounded == 0;

        bool rightLiftCandidate =
            rightForce <= wheelLiftForceThreshold &&
            fr.grounded == 0 &&
            rr.grounded == 0;

        leftLiftTimer = leftLiftCandidate
            ? leftLiftTimer + deltaTime
            : 0f;

        rightLiftTimer = rightLiftCandidate
            ? rightLiftTimer + deltaTime
            : 0f;

        leftWheelLift = leftLiftTimer >= wheelLiftDurationThreshold;
        rightWheelLift = rightLiftTimer >= wheelLiftDurationThreshold;
    }

    float CalculateLegacyRolloverRisk(
        float roll,
        float latAcc,
        float rollRate,
        float sideSlip
    )
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
        return angle > 180f ? angle - 360f : angle;
    }

    string F(float value)
    {
        return value.ToString("F6", CsvCulture);
    }

    string BoolToInt(bool value)
    {
        return value ? "1" : "0";
    }

    string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            return "\"" + value.Replace("\"", "\"\"") + "\"";

        return value;
    }

    void OnApplicationQuit()
    {
        CloseCaseWriter();
        EndBatch();
    }

    void OnDestroy()
    {
        CloseCaseWriter();
        EndBatch();
    }

    struct WheelData
    {
        public int grounded;
        public float forwardSlip;
        public float sideSlip;
        public float normalForce;
    }
}