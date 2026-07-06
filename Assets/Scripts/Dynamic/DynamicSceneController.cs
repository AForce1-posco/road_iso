using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 동적 씬 오케스트레이션.
/// runAllCases=true: Assets/Data/Cases의 모든 케이스를 이름순으로 1회씩 주행.
/// PathFailure / ISO 완주 / 전복 / Timeout 발생 시 현재 Run 종료 후 다음 케이스로 이동.
/// </summary>
public class DynamicSceneController : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public CargoBedLoader loader;
    public CargoRiskRecorder recorder;
    public PurePursuitController pursuit;
    public VehicleController vehicle;
    public DataLogger dataLogger;
    public RoadPathGenerator roadPath;

    [Header("실행 모드")]
    public bool runAllCases = true;

    [Header("케이스 범위")]
    public int startCaseNumber = 0;
    public int endCaseNumber = 0;

    [Header("타이밍")]
    public float settleTime = 0.5f;
    public float maxDuration = 1800f;
    public float rolloverGrace = 2f;

    [Header("Path Failure")]
    [Tooltip("도로/path 중심선에서 이 거리 이상 벗어나면 경로 이탈 후보로 판단합니다.")]
    public float pathFailureDistance = 15f;

    [Tooltip("경로 이탈 후보 상태가 이 시간 이상 지속되면 PathFailure로 확정합니다.")]
    public float pathFailureDuration = 1.0f;

    [Tooltip("Run 시작 직후 이 시간 동안은 PathFailure 판정을 하지 않습니다.")]
    public float pathFailureIgnoreStartTime = 3.0f;

    [Tooltip("true면 ISO 경로 전환 전후 모두 path deviation을 계산합니다.")]
    public bool enablePathFailureDetection = true;

    [Header("종료 동작")]
    public bool quitPlayOnFinish = false;

    [Header("물리 정확도")]
    public float physicsTimestep = 0.01f;
    public float maxDepenetrationVelocity = 2f;

    [Header("배속")]
    public float simSpeed = 10f;

    private Rigidbody truckRb;
    private Vector3 startPos;
    private Quaternion startRot;

    void Awake()
    {
        if (physicsTimestep > 0f)
            Time.fixedDeltaTime = physicsTimestep;

        if (maxDepenetrationVelocity > 0f)
            Physics.defaultMaxDepenetrationVelocity = maxDepenetrationVelocity;

        Time.timeScale = Mathf.Max(1f, simSpeed);
        Time.maximumDeltaTime = Mathf.Clamp(
            Time.fixedDeltaTime * Mathf.Max(1f, simSpeed) * 3f,
            0.33f,
            1f
        );

        if (loader == null) loader = FindObjectOfType<CargoBedLoader>();
        if (recorder == null) recorder = FindObjectOfType<CargoRiskRecorder>();
        if (pursuit == null) pursuit = FindObjectOfType<PurePursuitController>();
        if (vehicle == null) vehicle = FindObjectOfType<VehicleController>();
        if (dataLogger == null) dataLogger = FindObjectOfType<DataLogger>();
        if (roadPath == null) roadPath = FindObjectOfType<RoadPathGenerator>();

        if (pursuit != null)
            pursuit.enabled = false;
    }

    void OnDisable()
    {
        Time.timeScale = 1f;
    }

    void Start()
    {
        StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {
        yield return null;

        if (loader == null || recorder == null || pursuit == null || vehicle == null)
        {
            Debug.LogError(
                "DynamicSceneController: 참조 미지정 — 중단. " +
                $"loader={(loader != null)}, recorder={(recorder != null)}, " +
                $"pursuit={(pursuit != null)}, vehicle={(vehicle != null)}"
            );
            yield break;
        }

        truckRb = vehicle.GetComponent<Rigidbody>();
        startPos = truckRb.position;
        startRot = truckRb.rotation;

        if (maxDepenetrationVelocity > 0f)
            truckRb.maxDepenetrationVelocity = maxDepenetrationVelocity;

        var cases = new List<string>();

        if (runAllCases && Directory.Exists(CargoPaths.CasesDir))
        {
            cases.AddRange(Directory.GetFiles(CargoPaths.CasesDir, "*.json"));

            cases.Sort((a, b) =>
            {
                int na = ExtractCaseNumber(a);
                int nb = ExtractCaseNumber(b);

                if (na >= 0 && nb >= 0)
                    return na.CompareTo(nb);

                return System.StringComparer.Ordinal.Compare(a, b);
            });

            if (startCaseNumber > 0 || endCaseNumber > 0)
            {
                cases = cases.FindAll(path =>
                {
                    int num = ExtractCaseNumber(path);

                    if (num < 0) return false;
                    if (startCaseNumber > 0 && num < startCaseNumber) return false;
                    if (endCaseNumber > 0 && num > endCaseNumber) return false;

                    return true;
                });

                Debug.Log($"케이스 범위 필터 {startCaseNumber}~{endCaseNumber} → {cases.Count}개 선택");
            }
        }

        if (runAllCases && cases.Count == 0)
            Debug.LogWarning("Cases 폴더에 케이스가 없음 — loader 설정대로 1회만 주행");

        int total = (runAllCases && cases.Count > 0) ? cases.Count : 1;

        string batchRunId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (dataLogger != null)
            dataLogger.BeginBatch(batchRunId);

        for (int i = 0; i < total; i++)
        {
            string caseLabel = "(단일)";

            if (runAllCases && cases.Count > 0)
            {
                loader.layoutPath = cases[i];
                caseLabel = Path.GetFileNameWithoutExtension(cases[i]);
            }

            Debug.Log($"═══════ [{i + 1}/{total}] {caseLabel} ═══════");

            yield return RunOneCase();

            if (i < total - 1)
                yield return ResetForNextRun();
        }

        if (dataLogger != null)
            dataLogger.EndBatch();

        Debug.Log($"■ 전체 완료: {total}개 케이스");

#if UNITY_EDITOR
        if (quitPlayOnFinish)
            UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private static int ExtractCaseNumber(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        int i = 0;
        while (i < name.Length && !char.IsDigit(name[i]))
            i++;

        int j = i;
        while (j < name.Length && char.IsDigit(name[j]))
            j++;

        if (j > i && int.TryParse(name.Substring(i, j - i), out int n))
            return n;

        return -1;
    }

    private IEnumerator RunOneCase()
    {
        int count = loader.Load();

        if (count < 0)
        {
            Debug.LogError("적재 실패 — 이 케이스 건너뜀");
            yield break;
        }

        float t0 = Time.time;

        while (Time.time - t0 < settleTime)
        {
            vehicle.SetInput(0f, 0f, 1f);
            yield return new WaitForFixedUpdate();
        }

        loader.ReleaseFreeCargo();
        yield return new WaitForFixedUpdate();

        string caseName = string.IsNullOrEmpty(loader.LastLoadedPath)
            ? "(알 수 없음)"
            : Path.GetFileNameWithoutExtension(loader.LastLoadedPath);

        float totalMass = 0f;
        float securedMass = 0f;

        foreach (var c in loader.Loaded)
        {
            float m = c.type.massKg * loader.massScale;
            totalMass += m;

            if (c.secured)
                securedMass += m;
        }

        float securedFrac = totalMass > 0f ? securedMass / totalMass : 0f;

        recorder.Begin();

        if (dataLogger != null)
            dataLogger.BeginRun(caseName, count, totalMass, securedFrac);

        pursuit.enabled = true;

        Debug.Log($"▶ 주행 시작 — 배치 케이스: {caseName} (화물 {count}개)");

        float start = Time.time;
        float rolloverAt = -1f;
        float pathFailureTimer = 0f;
        string endReason = "NORMAL";

        while (true)
        {
            float elapsed = Time.time - start;

            float pathDeviation = 0f;
            bool pathFailureCandidate = false;

            if (enablePathFailureDetection && elapsed >= pathFailureIgnoreStartTime)
            {
                pathDeviation = CalculatePathDeviation();

                if (pathDeviation >= pathFailureDistance)
                {
                    pathFailureTimer += Time.fixedDeltaTime;
                    pathFailureCandidate = pathFailureTimer >= pathFailureDuration;
                }
                else
                {
                    pathFailureTimer = 0f;
                }

                if (dataLogger != null)
                    dataLogger.SetPathStatus(pathDeviation, pathFailureCandidate);
            }

            if (pathFailureCandidate)
            {
                endReason = "PATH_FAILURE";

                if (dataLogger != null)
                    dataLogger.SetRunEndReason(endReason);

                Debug.LogWarning(
                    $"종료: PATH_FAILURE — PathDeviation={pathDeviation:F2}m"
                );

                break;
            }

            if (pursuit.Finished)
            {
                endReason = "NORMAL";

                if (dataLogger != null)
                    dataLogger.SetRunEndReason(endReason);

                Debug.Log("종료: ISO 구간 완주");
                break;
            }

            if (recorder.RolloverDetected)
            {
                if (rolloverAt < 0f)
                {
                    rolloverAt = Time.time;
                    endReason = "ROLLOVER";

                    if (dataLogger != null)
                        dataLogger.SetRunEndReason(endReason);

                    Debug.Log("전복 감지!");
                }

                if (Time.time - rolloverAt > rolloverGrace)
                    break;
            }

            if (elapsed > maxDuration)
            {
                endReason = "TIMEOUT";

                if (dataLogger != null)
                    dataLogger.SetRunEndReason(endReason);

                Debug.LogWarning("종료: TIMEOUT");
                break;
            }

            yield return new WaitForFixedUpdate();
        }

        pursuit.enabled = false;
        vehicle.SetInput(0f, 0f, 1f);

        recorder.Finish();

        if (dataLogger != null)
        {
            dataLogger.SetRunEndReason(endReason);
            dataLogger.EndRun();
        }
    }

    private float CalculatePathDeviation()
    {
        if (roadPath == null)
            return 0f;

        List<Vector3> path = null;

        if (pursuit != null && pursuit.UsingISO)
            path = roadPath.isoPoints;
        else
            path = roadPath.roadPoints;

        if (path == null || path.Count == 0)
            return 0f;

        Vector3 truckPos = truckRb != null
            ? truckRb.position
            : vehicle.transform.position;

        float bestDistSqr = float.MaxValue;

        for (int i = 0; i < path.Count; i++)
        {
            Vector2 a = new Vector2(truckPos.x, truckPos.z);
            Vector2 b = new Vector2(path[i].x, path[i].z);

            float d = Vector2.SqrMagnitude(a - b);

            if (d < bestDistSqr)
                bestDistSqr = d;
        }

        return Mathf.Sqrt(bestDistSqr);
    }

    private IEnumerator ResetForNextRun()
    {
        truckRb.velocity = Vector3.zero;
        truckRb.angularVelocity = Vector3.zero;
        truckRb.position = startPos;
        truckRb.rotation = startRot;

        pursuit.Restart();

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
    }
}