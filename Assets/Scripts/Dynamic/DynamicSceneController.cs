using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 동적 씬 오케스트레이션.
/// runAllCases=true(기본): Assets/Data/Cases의 모든 케이스를 이름순으로 1회씩 주행 —
///   케이스마다 [적재 → 안정화 → 자유화물 해제 → 주행 → results.csv 1행 + 시계열 CSV 1개 → 트럭 리셋].
/// runAllCases=false: 케이스 1개만 (CargoBedLoader의 caseName/layoutPath 설정을 따름).
///
/// 순서가 중요한 이유:
/// - VehicleController.Start()가 트럭 질량(3500)·CoM을 덮어쓰므로 적재 반영은 첫 프레임 이후여야 함.
/// - 출발 전에 자유화물을 해제해야 주행 내내 실제로 쏠리고 굴러감(현실 조건).
/// </summary>
public class DynamicSceneController : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public CargoBedLoader loader;
    public CargoRiskRecorder recorder;
    public PurePursuitController pursuit;
    public VehicleController vehicle;
    public DataLogger dataLogger;

    [Header("실행 모드")]
    [Tooltip("true: Cases 폴더 전체를 번호순으로 일괄 주행 / false: 1개만(loader 설정 따름)")]
    public bool runAllCases = true;

    [Header("케이스 범위 (중단 후 원하는 지점부터 재개용)")]
    [Tooltip("이 번호 '이상'의 케이스만 주행 (caseNNN.json의 NNN). 0이면 처음부터. 예: 1001 → 자유배치부터")]
    public int startCaseNumber = 0;
    [Tooltip("이 번호 '이하'의 케이스만 주행. 0이면 끝까지. 예: start 1001 + end 2000 → 자유배치만")]
    public int endCaseNumber = 0;

    [Header("타이밍")]
    [Tooltip("적재 후 자유화물 해제 전 안정화 시간 (s)")]
    public float settleTime = 0.5f;
    [Tooltip("케이스당 최대 주행 시간 (s). 완주가 기본이고, 트럭이 끼임/이탈로 영영 못 끝내는 예외 상황에만 걸리는 안전망")]
    public float maxDuration = 1800f;
    [Tooltip("전복 감지 후 이만큼 더 기록하고 종료 (화물 쏟아지는 것까지 담기)")]
    public float rolloverGrace = 2f;

    [Header("종료 동작")]
    public bool quitPlayOnFinish = false;

    [Header("물리 정확도 (방지턱 사출 방지 — 현실에 없는 계산 오차 제거)")]
    [Tooltip("물리 계산 주기 (s). 0.01 = 초당 100회 (기본 0.02의 2배 정밀). 0이면 변경 안 함")]
    public float physicsTimestep = 0.01f;
    [Tooltip("겹침 복구 최대 사출 속도 (m/s). Unity 기본 10은 화물을 발사시킴. 0이면 변경 안 함")]
    public float maxDepenetrationVelocity = 2f;

    [Header("배속 (데이터 수집 가속 — 물리 정확도는 유지)")]
    [Tooltip("시뮬 배속. 10 = 10배 빨리 (10분→1분). 물리 스텝 크기는 그대로라 결과 동일. " +
             "CPU가 못 따라가면 실제 배속이 이보다 낮아질 뿐(안전). 화물 튐이 과장되면 낮출 것. 권장 5~20")]
    public float simSpeed = 10f;

    private Rigidbody truckRb;
    private Vector3 startPos;
    private Quaternion startRot;

    void Awake()
    {
        // 물리 정확도 설정 — 화물 생성(Load)보다 먼저 적용돼야 새 Rigidbody에 반영됨
        if (physicsTimestep > 0f) Time.fixedDeltaTime = physicsTimestep;
        if (maxDepenetrationVelocity > 0f) Physics.defaultMaxDepenetrationVelocity = maxDepenetrationVelocity;

        // 배속: timeScale만 올리고 fixedDeltaTime(물리 스텝 크기)은 그대로 → 스텝 정확도 보존.
        // maximumDeltaTime을 넉넉히 잡아야 Unity가 배속을 조기에 제한하지 않음(못 따라가면 자동으로 느려질 뿐).
        Time.timeScale = Mathf.Max(1f, simSpeed);
        Time.maximumDeltaTime = Mathf.Clamp(Time.fixedDeltaTime * Mathf.Max(1f, simSpeed) * 3f, 0.33f, 1f);

        // 인스펙터에서 비워두면 씬에서 자동 검색 (각 1개씩만 존재하는 컴포넌트들)
        if (loader == null) loader = FindObjectOfType<CargoBedLoader>();
        if (recorder == null) recorder = FindObjectOfType<CargoRiskRecorder>();
        if (pursuit == null) pursuit = FindObjectOfType<PurePursuitController>();
        if (vehicle == null) vehicle = FindObjectOfType<VehicleController>();
        if (dataLogger == null) dataLogger = FindObjectOfType<DataLogger>();

        // 적재가 끝나기 전에 출발하지 않게 잠금
        if (pursuit != null) pursuit.enabled = false;
    }

    void OnDisable()
    {
        // 배속 원복 — Play 종료/씬 전환 후 timeScale이 남아 다음 실행을 꼬이게 하지 않도록
        Time.timeScale = 1f;
    }

    void Start()
    {
        StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {
        yield return null; // 모든 Start 실행 대기 (VehicleController가 질량 설정을 끝내도록)

        if (loader == null || recorder == null || pursuit == null || vehicle == null)
        {
            Debug.LogError("DynamicSceneController: 참조 미지정 — 중단. " +
                $"loader={(loader != null)}, recorder={(recorder != null)}, " +
                $"pursuit={(pursuit != null)}, vehicle={(vehicle != null)} " +
                "(false인 컴포넌트가 씬에 없다는 뜻 — 트럭에 추가됐는지 확인)");
            yield break;
        }

        truckRb = vehicle.GetComponent<Rigidbody>();
        startPos = truckRb.position;
        startRot = truckRb.rotation;
        // 트럭은 씬에 이미 존재하던 바디라 전역 기본값 변경이 소급 안 됨 → 직접 적용
        if (maxDepenetrationVelocity > 0f) truckRb.maxDepenetrationVelocity = maxDepenetrationVelocity;

        // 케이스 목록 — 파일명의 번호(caseNNN)로 정렬 (3자리/4자리 섞여도 진짜 숫자순 보장)
        var cases = new List<string>();
        if (runAllCases && Directory.Exists(CargoPaths.CasesDir))
        {
            cases.AddRange(Directory.GetFiles(CargoPaths.CasesDir, "*.json"));
            cases.Sort((a, b) =>
            {
                int na = ExtractCaseNumber(a), nb = ExtractCaseNumber(b);
                if (na >= 0 && nb >= 0) return na.CompareTo(nb);
                return System.StringComparer.Ordinal.Compare(a, b);
            });

            // 번호 범위 필터 (중단 후 재개용). 0이면 무제한.
            if (startCaseNumber > 0 || endCaseNumber > 0)
            {
                cases = cases.FindAll(path =>
                {
                    int num = ExtractCaseNumber(path);
                    if (num < 0) return false; // 번호 없는 파일은 범위 지정 시 제외
                    if (startCaseNumber > 0 && num < startCaseNumber) return false;
                    if (endCaseNumber > 0 && num > endCaseNumber) return false;
                    return true;
                });
                Debug.Log($"케이스 범위 필터 {startCaseNumber}~{endCaseNumber} → {cases.Count}개 선택");
            }
        }
        if (runAllCases && cases.Count == 0)
            Debug.LogWarning("Cases 폴더에 (범위 내) 케이스가 없음 — loader 설정대로 1회만 주행");

        int total = (runAllCases && cases.Count > 0) ? cases.Count : 1;

        // 통합 시계열 파일 하나 열기 (모든 케이스가 여기에 쌓임)
        string batchRunId = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if (dataLogger != null) dataLogger.BeginBatch(batchRunId);

        for (int i = 0; i < total; i++)
        {
            if (runAllCases && cases.Count > 0)
                loader.layoutPath = cases[i]; // 명시 지정 (caseName보다 우선)

            Debug.Log($"═══════ 케이스 {i + 1} / {total} ═══════");
            yield return RunOneCase();

            if (i < total - 1)
                yield return ResetForNextRun();
        }

        if (dataLogger != null) dataLogger.EndBatch();
        Debug.Log($"■ 전체 완료: {total}개 케이스 → results.csv + 케이스별 시계열 {total}개 + 통합 시계열 1개 (combined_timeseries_{batchRunId}.csv)");
#if UNITY_EDITOR
        if (quitPlayOnFinish) UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private IEnumerator RunOneCase()
    {
        int count = loader.Load();
        if (count < 0)
        {
            Debug.LogError("적재 실패 — 이 케이스 건너뜀");
            yield break;
        }

        // 안정화: 제동 상태로 대기
        float t0 = Time.time;
        while (Time.time - t0 < settleTime)
        {
            vehicle.SetInput(0f, 0f, 1f);
            yield return new WaitForFixedUpdate();
        }

        loader.ReleaseFreeCargo();
        yield return new WaitForFixedUpdate(); // 해제 반영 한 프레임

        // 케이스 메타 계산 (통합 CSV 식별 컬럼용)
        string caseName = string.IsNullOrEmpty(loader.LastLoadedPath)
            ? "(알 수 없음)"
            : Path.GetFileNameWithoutExtension(loader.LastLoadedPath);
        float totalMass = 0f, securedMass = 0f;
        foreach (var c in loader.Loaded)
        {
            float m = c.type.massKg * loader.massScale;
            totalMass += m;
            if (c.secured) securedMass += m;
        }
        float securedFrac = totalMass > 0f ? securedMass / totalMass : 0f;

        recorder.Begin();
        // 시계열도 이 시점부터 (주행 구간만 → 케이스당 파일 1개 + 통합 파일에 누적)
        if (dataLogger != null) dataLogger.BeginRun(caseName, count, totalMass, securedFrac);
        pursuit.enabled = true;
        Debug.Log($"▶ 주행 시작 — 배치 케이스: {caseName} (화물 {count}개)");

        // 종료 대기: ISO 완주 / 전복(+grace) / 타임아웃
        float start = Time.time;
        float rolloverAt = -1f;
        while (true)
        {
            if (pursuit.Finished) { Debug.Log("종료: ISO 구간 완주"); break; }
            if (recorder.RolloverDetected)
            {
                if (rolloverAt < 0f) { rolloverAt = Time.time; Debug.Log("전복 감지!"); }
                if (Time.time - rolloverAt > rolloverGrace) break;
            }
            if (Time.time - start > maxDuration) { Debug.LogWarning("종료: 타임아웃"); break; }
            yield return new WaitForFixedUpdate();
        }

        pursuit.enabled = false;
        vehicle.SetInput(0f, 0f, 1f);
        recorder.Finish();
        if (dataLogger != null) dataLogger.EndRun(); // 시계열 파일 닫기 (이 케이스 = 이 파일 1개)
    }

    /// <summary>트럭을 시작 지점으로 되돌리고 다음 케이스 준비. 화물 정리는 다음 Load()의 Clear()가 수행.</summary>
    private IEnumerator ResetForNextRun()
    {
        truckRb.velocity = Vector3.zero;
        truckRb.angularVelocity = Vector3.zero;
        truckRb.position = startPos;
        truckRb.rotation = startRot;
        pursuit.Restart();
        // 시계열 파일은 각 케이스의 EndRun()에서 이미 닫힘 → 다음 케이스 BeginRun()에서 새로 염

        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
    }
}
