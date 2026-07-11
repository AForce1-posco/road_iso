using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// v2 — Refinement 강화학습. 빈패커(Dense) 완성 배치에서 **시작** → RL이 화물을 "재배치(relocate)"해 정적 보상 개선.
/// 시작이 유효 배치 + 무효 이동은 되돌림(no-op) → **붕괴 물리적 불가**. from-scratch PlacementAgent(v1)과 독립.
///
/// - 관측: 높이맵(cols×rows) + CoG(3) + 질량(1) + CoG편차(2)  = obs
/// - 행동: (아이템 index · 목표 셀 · 회전2) — item 하나를 집어 다른 셀로 옮김
/// - 보상: ΔFinal(이동 후 − 이동 전). 누적 = "빈패커 대비 얼마나 개선했나". 무효 이동은 작은 페널티.
/// - 예측기 오면 Final 대신 예측 위험도로 교체(아키텍처 그대로).
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class RefinementAgent : Agent
{
    [Header("규제/보상")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();

    [Header("격자 (PlacementAgent·빈패커와 동일)")]
    public int cols = 11;
    public int rows = 31;

    [Header("시작 배치 (빈패커 Pack)")]
    [Tooltip("이 manifest를 startPackMode로 Pack한 배치에서 시작 (boxpack001 = B-004×8·SYN-04×4·SYN-03×4)")]
    public ManifestEntry[] startManifest = {
        new ManifestEntry { typeId = "B-004", count = 8 },
        new ManifestEntry { typeId = "SYN-04", count = 4 },
        new ManifestEntry { typeId = "SYN-03", count = 4 },
    };
    [Tooltip("Dense=공간 위주(개선여지 큼) / Stable=이미 안전")]
    public BinPacker.PackMode startPackMode = BinPacker.PackMode.Dense;

    [Header("케이스 커리큘럼 (0~3단계 전체 로스터 — 절대 일부만 넣지 말 것)")]
    [Tooltip("파일명 접두사 's0_'·'s1_'·'s2_'·'s3_'로 소속 단계를 구분한다. " +
             "★중요: 여기엔 항상 0~3단계 전체 케이스를 다 넣어둔다 — 이래야 액션공간(아이템 인덱스 수)이 " +
             "학습 내내 고정되어, 단계가 바뀌어도 --initialize-from으로 이전 단계 모델을 그대로 이어받을 수 있다. " +
             "실제 이번 런이 몇 단계를 학습하는지는 아래 currentStage로 따로 지정한다.")]
    public string[] caseCsvPaths = {
        "Data/RefineCases/s0_a.csv",
        "Data/RefineCases/s1_a.csv", "Data/RefineCases/s1_b.csv", "Data/RefineCases/s1_c.csv",
        "Data/RefineCases/s1_d.csv", "Data/RefineCases/s1_e.csv",
        "Data/RefineCases/s2_a.csv", "Data/RefineCases/s2_b.csv", "Data/RefineCases/s2_c.csv",
        "Data/RefineCases/s2_d.csv", "Data/RefineCases/s2_e.csv",
        "Data/RefineCases/s3_a.csv", "Data/RefineCases/s3_b.csv", "Data/RefineCases/s3_c.csv", "Data/RefineCases/s3_d.csv",
    };

    [Header("이번 런이 학습할 단계 (0~4, 수동 지정)")]
    [Tooltip("이번 mlagents-learn 런에서 실제로 샘플링할 단계. 통과기준 넘기면 다음 단계 번호로 바꾸고 " +
             "--initialize-from=<이전 run-id>로 이어서 새 run-id 학습을 시작한다. " +
             "4단계는 액션공간이 0~3단계(11)보다 커서(20) 체크포인트를 그대로 --initialize-from 못 함 — " +
             "widen_checkpoint.py로 넓힌 체크포인트를 --initialize-from에 지정할 것.")]
    [Range(0, 4)] public int currentStage = 0;
    [Tooltip("바로 이전 단계(currentStage-1) 케이스를 섞는 비율(에피소드 시작 시점 기준). 0단계는 무시됨(이전 단계 없음). 4단계는 아래 stage4ReplayRatio를 따로 씀.")]
    [Range(0f, 1f)] public float prevStageReplayRatio = 0.2f;
    [Tooltip("이 비율이 런 시작 시점의 prevStageReplayRatio에서 0으로 선형 감소하는 데 걸리는 에피소드 수(어닐링). " +
             "0이면 어닐링 없이 prevStageReplayRatio를 계속 고정 유지.")]
    public int prevStageReplayAnnealEpisodes = 200;
    [Tooltip("성공 판정 절대 기준선. Score()(useSurrogateReward=false면 CGS Final 총점, true면 −예측위험×배율 — 둘 다 " +
             "높을수록 안전)가 이 값 이상이면 그 에피소드는 '성공'. 커리큘럼 통과율(Refine/QualityPassRate)의 기준선이므로, " +
             "0단계를 먼저 학습시켜 Score()가 어디서 안정되는지 관찰한 뒤 그 값 근처로 캘리브레이션할 것.")]
    public float successScoreThreshold = 0f;

    [Header("4단계: 완전 절차적 무작위 생성 (풀 아님, 매 에피소드 독립 샘플)")]
    [Tooltip("샘플링 대상 화물 id 풀. 기본 = 카탈로그 13종 전체(B-001..007, SYN-01..06).")]
    public string[] stage4TypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006","B-007",
        "SYN-01","SYN-02","SYN-03","SYN-04","SYN-05","SYN-06",
    };
    public Vector2Int stage4NumTypesRange = new Vector2Int(1, 8);
    public Vector2Int stage4CountPerTypeRange = new Vector2Int(1, 10);
    public Vector2Int stage4TotalItemsRange = new Vector2Int(6, 20);
    [Tooltip("이 id들은 선택 확률에 배율(stage4MismatchWeightMultiplier)이 곱해짐 — mass/부피 상관 나쁜 조합을 더 자주 노출.")]
    public string[] stage4MassVolumeMismatchIds = { "SYN-01", "SYN-06" };
    public float stage4MismatchWeightMultiplier = 1.5f;
    [Tooltip("트레이 전체 부피(trayLateralM×trayLengthM×heightLimitM) 대비 샘플 총부피 상한 비율.")]
    [Range(0.5f, 1f)] public float stage4VolumeCapFraction = 0.9f;
    [Tooltip("질량/부피 상한을 만족하는 조합을 찾을 때까지 재시도할 횟수. 다 실패하면 결정론적 최소구성으로 폴백 + 경고 로그.")]
    public int stage4MaxSampleTries = 20;
    [Tooltip("절차적 생성 대신 0~3단계 기존 케이스 중 하나를 쓸 확률(전체 단계에서, 어닐링 없음 — 최종 단계라 계속 일정 비율 유지해 까먹기 방지).")]
    [Range(0f, 1f)] public float stage4ReplayRatio = 0.15f;

    [Header("에피소드")]
    [Tooltip("한 에피소드에 허용하는 재배치 수")]
    public int stepsPerEpisode = 25;
    [Tooltip("무효 이동(겹침/이탈) 시 작은 페널티")]
    public float invalidMovePenalty = 0.02f;

    [Header("Surrogate 보상 (2026-07-09) — PlacementAgent와 같은 잣대")]
    [Tooltip("켜면 매 스텝 보상 = ΔScore(이동 후−전), Score = −리스크(surrogate). 끄면 기존 ΔFinal(CGS). refinement의 조밀보상 구조는 그대로, '무엇을 개선하나'만 교체.")]
    public bool useSurrogateReward = false;
    [Tooltip("Resources 아래 트리 JSON(확장자 제외). PlacementAgent와 동일 모델 쓰면 공정비교.")]
    public string surrogateResourceName = "layout_risk_p95";
    [Tooltip("모델 학습 질량 스케일(동적 sim massScale=100=676kg). 0중요도라 값 무관하나 학습과 맞춤.")]
    public float modelMassScaleKg = 100f;
    [Tooltip("정규화 리스크(0~1)를 보상으로 키우는 배율. ΔScore라 1로 충분.")]
    public float surrogateRewardScale = 1f;
    [Tooltip("모델의 도로/주행 상수 피처(중요도 0, 학습값에 맞춤): 속도/뱅크/경사/차체질량")]
    public float targetSpeedKmh = 60f;
    public float roadBankAngleDeg = 0f;
    public float roadSlopeDeg = 0f;
    public float vehicleBaseMassKg = 3500f;
    private LayoutRiskModel riskModel;

    [Header("배치 저장 (추론/검증용 — 학습 시 OFF)")]
    [Tooltip("켜면 에피소드 끝(재배치 완료)마다 현재 배치를 Assets/Data/Results/<layoutOutName>.json 저장. 학습 중엔 반드시 OFF.")]
    public bool saveLayoutOnComplete = false;
    public string layoutOutName = "rl_refine_layout";

    [Header("디버그")]
    public bool verboseLog = false;

    // ── 내부 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private BinPacker packer;
    private List<CargoType> manifestList;                                   // 이번 에피소드 케이스의 manifest
    private List<List<CargoType>> caseSet;                                  // 케이스 커리큘럼(0~3단계 전체 로스터)
    private int curCaseIdx;                                                 // 이번 에피소드 케이스 index
    private List<int>[] stageIndices;                                      // stageIndices[s] = caseSet 중 s단계 소속 인덱스들
    private int episodesIntoStage;                                         // 이번 런 시작 후 지난 에피소드 수 (replay 어닐링용)
    private int lastUnplacedCount;                                         // 이번 에피소드 빈패커가 못 실은 화물 수
    private List<CargoType> stage4Types;                                   // 4단계 샘플링 대상 카탈로그(13종)
    private float[] stage4Weights;                                         // stage4Types와 같은 순서의 샘플링 가중치
    private float stage4ContainerVolume;                                   // 트레이 전체 부피 (부피 상한 체크용)
    // 4단계 계측(에피소드 끝에 로깅) — OnEpisodeBegin에서 채움
    private bool stage4IsReplay;
    private int stage4NumTypesUsed;
    private int stage4TotalCount;
    private float stage4TotalMass;
    private float stage4TotalVolume;
    private float stage4MassVolumeCorr;
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private int numItems;
    private int stepCount;
    private float prevFinal;
    private bool setupDone;
    private float startFinal;      // 에피소드 시작(빈패커) Final — 개선량 계산 기준
    private int validMoves;        // 이번 에피소드 유효 이동 수 (계측)
    private bool diagLogged;       // surrogate 진단 로그 1회용
    private float minHalfXZ;       // manifest 중 가장 작은 화물의 최소 반치수 (경계 마스킹용)

    /// <summary>시각화용 읽기 전용 배치.</summary>
    public IReadOnlyList<RuleChecker.PlacedItem> PlacedItems => placed;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;   // 중심 x = W/2 (균형기준, 원점 아님)
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;    // 중심 z = L/2
    private float MaxX => ruleConfig.trayLateralM;           // 우측 경계 (좌=0)
    private float MaxZ => ruleConfig.trayLengthM;            // 앞 경계 (뒤=0)
    private int NumCells => cols * rows;
    private int ObsSize => NumCells + 3 + 1 + 2;   // 높이맵 + CoG(3) + 질량(1) + 편차(2)

    private void Awake() => Setup();
    public override void Initialize() => Setup();

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        rules = new RuleChecker(ruleConfig);
        reward = new RewardCalculator(rewardConfig, ruleConfig);
        packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = startPackMode };
        if (useSurrogateReward) riskModel = new LayoutRiskModel(surrogateResourceName);

        // 케이스 세트 구성: CSV 경로들이 있으면 각 CSV = 한 케이스, 없으면 startManifest 1개.
        // 파일명 접두사(s0_/s1_/s2_/s3_)로 소속 단계를 파싱 → stageIndices에 버킷.
        caseSet = new List<List<CargoType>>();
        stageIndices = new List<int>[4];
        for (int s = 0; s < 4; s++) stageIndices[s] = new List<int>();
        if (caseCsvPaths != null && caseCsvPaths.Length > 0)
        {
            foreach (var csv in caseCsvPaths)
            {
                if (string.IsNullOrWhiteSpace(csv)) continue;
                var m = CargoManifest.Resolve(null, csv, out string src);
                if (m != null && m.Count > 0)
                {
                    int stage = ParseStagePrefix(csv);
                    caseSet.Add(m);
                    if (stage >= 0 && stage < 4) stageIndices[stage].Add(caseSet.Count - 1);
                    else Debug.LogWarning($"[Refine] 케이스 '{csv}' 파일명에서 단계(s0_~s3_) 접두사를 못 읽음 — 어느 단계에도 안 묶임");
                    Debug.Log($"[Refine] 케이스 로드 {src}: {m.Count}개 (단계 {stage})");
                }
                else Debug.LogWarning($"[Refine] 케이스 CSV 비었음/실패: {csv}");
            }
        }
        if (caseSet.Count == 0)   // fallback: 단일 케이스(인스펙터 manifest)
        {
            caseSet.Add(CargoManifest.Resolve(startManifest, "", out _));
            stageIndices[0].Add(0);
        }

        // 4단계 샘플링 대상 로스터 + 가중치 (mass/부피 불일치 타입은 배율 적용)
        var stage4Entries = new List<ManifestEntry>();
        foreach (var id in stage4TypeIds) stage4Entries.Add(new ManifestEntry { typeId = id, count = 1 });
        stage4Types = CargoManifest.Resolve(stage4Entries, "", out _);
        stage4Weights = new float[stage4Types.Count];
        for (int i = 0; i < stage4Types.Count; i++)
        {
            bool mismatch = stage4MassVolumeMismatchIds != null && System.Array.IndexOf(stage4MassVolumeMismatchIds, stage4Types[i].id) >= 0;
            stage4Weights[i] = mismatch ? stage4MismatchWeightMultiplier : 1f;
        }
        stage4ContainerVolume = ruleConfig.trayLateralM * ruleConfig.trayLengthM * ruleConfig.heightLimitM;

        // action branch0(아이템)은 고정이라 케이스 중 최대 아이템 수 + 4단계 최대치(stage4TotalItemsRange.y)까지
        // 항상 감안한다 — currentStage가 몇이든 무관하게 always 20으로 잡아야, 앞으로 이 컴포넌트로 어떤 단계를
        // 학습하든 액션공간이 다시는 안 바뀐다(이번 3→4 전환 한 번만 widen_checkpoint.py로 넘기면 그 뒤로 쭉 고정).
        numItems = 1;
        foreach (var m in caseSet) numItems = Mathf.Max(numItems, m.Count);
        numItems = Mathf.Max(numItems, stage4TotalItemsRange.y);

        // 경계 마스킹용: 모든 케이스 + 4단계 로스터 중 가장 작은 화물의 최소 반치수. 이보다 좁게 남은
        // 가장자리 셀은 어떤 화물 중심을 놓아도 트레이를 벗어나므로 미리 막는다.
        minHalfXZ = float.MaxValue;
        foreach (var m in caseSet)
            foreach (var t in m)
                minHalfXZ = Mathf.Min(minHalfXZ, Mathf.Min(t.sizeM.x, t.sizeM.z) * 0.5f);
        foreach (var t in stage4Types)
            minHalfXZ = Mathf.Min(minHalfXZ, Mathf.Min(t.sizeM.x, t.sizeM.z) * 0.5f);
        if (minHalfXZ == float.MaxValue) minHalfXZ = 0f;

        manifestList = caseSet[0];   // 초기값(OnEpisodeBegin에서 매번 재선택)

        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(numItems, NumCells, 2); // 아이템·셀·회전
        if (MaxStep == 0) MaxStep = stepsPerEpisode + 2;

        Debug.Log($"[RefinementAgent] obs={ObsSize}, action=({numItems},{NumCells},2), 케이스={caseSet.Count}개" +
                  $"(0단계{stageIndices[0].Count}/1단계{stageIndices[1].Count}/2단계{stageIndices[2].Count}/3단계{stageIndices[3].Count}), " +
                  $"최대화물={numItems}개, 이번런 학습단계={currentStage}, mode={startPackMode}");
    }

    public override void OnEpisodeBegin()
    {
        stage4IsReplay = false;
        if (currentStage == 4)
        {
            // 4단계: stage4ReplayRatio 확률로 0~3단계 기존 케이스 재생(까먹기 방지, 어닐링 없이 계속 유지),
            // 그 외엔 매 에피소드 독립적으로 절차적 무작위 조합 생성.
            if (Random.value < stage4ReplayRatio)
            {
                var all = new List<int>();
                for (int s = 0; s < 4; s++) all.AddRange(stageIndices[s]);
                if (all.Count == 0) all.Add(0);
                curCaseIdx = all[Random.Range(0, all.Count)];
                manifestList = caseSet[curCaseIdx];
                stage4IsReplay = true;
            }
            else
            {
                curCaseIdx = -1;
                manifestList = SampleStage4Manifest();
            }
            stage4NumTypesUsed = CountDistinctTypes(manifestList);
            stage4TotalCount = manifestList.Count;
            stage4TotalMass = SumMass(manifestList);
            stage4TotalVolume = SumVolume(manifestList);
            stage4MassVolumeCorr = MassVolumeCorr(manifestList);
        }
        else
        {
            // 이번 단계(currentStage) 케이스 중 하나 랜덤 선택. 단, prevStageReplayRatio 확률로
            // 바로 이전 단계(currentStage-1) 케이스를 대신 뽑는다 — 어닐링: 런 시작 시점 비율에서
            // prevStageReplayAnnealEpisodes에 걸쳐 0으로 선형 감소(까먹기 방지, 초반엔 많이 섞고 후반엔 순수 이번 단계).
            var pool = stageIndices[currentStage];
            if (pool.Count == 0) pool = new List<int> { 0 };  // 안전망: 이 단계에 케이스가 없으면 케이스0으로 폴백

            if (currentStage > 0 && stageIndices[currentStage - 1].Count > 0)
            {
                float ratio = prevStageReplayAnnealEpisodes > 0
                    ? Mathf.Lerp(prevStageReplayRatio, 0f, Mathf.Clamp01(episodesIntoStage / (float)prevStageReplayAnnealEpisodes))
                    : prevStageReplayRatio;
                if (Random.value < ratio) pool = stageIndices[currentStage - 1];
            }
            curCaseIdx = pool[Random.Range(0, pool.Count)];
            manifestList = caseSet[curCaseIdx];
        }
        episodesIntoStage++;

        // 빈패커로 선택 케이스 시작 배치 생성 (케이스별 결정론적 Dense pack)
        var unplaced = new List<CargoType>();
        var packed = packer.Pack(manifestList, unplaced);
        lastUnplacedCount = unplaced.Count;
        placed.Clear();
        foreach (var p in packed)
            placed.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });

        stepCount = 0;
        validMoves = 0;
        prevFinal = placed.Count > 0 ? Score() : 0f;
        startFinal = prevFinal;

        // ── 진단(1회): surrogate가 빈패커 시작배치에 실제로 뱉는 위험값 ──
        // 올바른 p99 = 예측위험 ≈ 0.50(빈패커 p99), startFinal ≈ −0.50(scale1). ~0.87이면 모델/스케일 틀림.
        if (!diagLogged)
        {
            diagLogged = true;
            float rawRisk = (useSurrogateReward && riskModel != null && riskModel.Loaded)
                            ? riskModel.Predict(BuildRiskFeatures()) : float.NaN;
            Debug.Log($"[Refine 진단] surrogate='{surrogateResourceName}' loaded={(riskModel != null && riskModel.Loaded)} scale={surrogateRewardScale} " +
                      $"| 빈패커시작 예측위험={rawRisk:F4}  startFinal(Score)={startFinal:F4}  (정상 p99: 위험≈0.50·Score≈−0.50)");
        }
        if (verboseLog) Debug.Log($"[Refine 시작] {placed.Count}개, Final={prevFinal:F3}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) 높이맵
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                float h = (HeightAt(cc.x, cc.y) - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM);
                sensor.AddObservation(Mathf.Clamp01(h));
            }
        // 2) CoG 위치 (코너 원점 정규화 [0,1]: 0=좌/뒤, 1=우/앞)
        Vector3 cog = Cog();
        sensor.AddObservation(MaxX > 1e-6f ? cog.x / MaxX : 0f);
        sensor.AddObservation(MaxZ > 1e-6f ? cog.z / MaxZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));
        // 3) 총질량
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));
        // 4) CoG 편차(절대, 균형중심 W/2·L/2 기준, 0=중앙 1=끝)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x - HalfX) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z - HalfZ) / HalfZ : 0f);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(아이템): 케이스마다 아이템 수가 달라 numItems(=최대)보다 적을 수 있음.
        //   실재하지 않는 인덱스(placed.Count 이상)를 막아 무의미한 no-op 스텝을 줄인다.
        for (int i = placed.Count; i < numItems; i++)
            mask.SetActionEnabled(0, i, false);

        // branch2(회전)은 마스킹 불가(어떤 아이템을 고를지에 의존 → 조합 마스킹 불가, ML-Agents는 브랜치별 독립).
        // branch1(셀)은 아래에서 막는다. 브랜치 독립 마스킹이라 겹침(아이템·회전 조합 의존)은 못 막고,
        // "어떤 화물도 못 놓는 셀"만 근사로 제거한다.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                bool block =
                    // (1) 높이 한도까지 꽉 찬 셀 — 그 위엔 아무것도 못 올림
                    HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f ||
                    // (2) 최소 화물조차 중심으로 놓으면 트레이를 벗어나는 가장자리 셀 (코너 원점 [0,W]·[0,L])
                    cc.x - minHalfXZ < -1e-4f || cc.x + minHalfXZ > MaxX + 1e-4f ||
                    cc.y - minHalfXZ < -1e-4f || cc.y + minHalfXZ > MaxZ + 1e-4f;
                if (block) mask.SetActionEnabled(1, r * cols + c, false);
            }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int itemIdx = actions.DiscreteActions[0];
        int cellIdx = actions.DiscreteActions[1];
        int rot = actions.DiscreteActions[2];

        if (TryRelocate(itemIdx, cellIdx, rot))
        {
            float now = Score();
            AddReward(now - prevFinal);      // ΔScore (개선분). 누적 = 시작 대비 개선 (CGS Final 또는 −surrogate위험)
            prevFinal = now;
            validMoves++;
        }
        else
        {
            AddReward(-invalidMovePenalty);  // 무효 이동 = 되돌림 + 작은 벌점 (fail-out 없음)
        }

        if (++stepCount >= stepsPerEpisode)
        {
            // ── 계측: 유효이동 비율·빈패커 대비 개선량을 TensorBoard로 ──
            var stats = Academy.Instance.StatsRecorder;
            stats.Add("Refine/ValidMoveRate", validMoves / (float)stepsPerEpisode);
            stats.Add("Refine/FinalImprovement", prevFinal - startFinal);
            stats.Add("Refine/FinalAbsolute", prevFinal);
            // 성공률 지표 (커리큘럼 통과 게이팅용, 최근 summary_freq 구간 평균 = TensorBoard에서 그대로 %로 읽힘):
            //  - PlacedAllRate: 빈패커가 이번 케이스의 화물을 전부 실었는가(사실상 케이스 자체의 적재 가능성 검증 — RL 실력과 무관, 이게 낮으면 케이스가 과적/불가능하다는 뜻)
            //  - QualityPassRate: 최종 Score()가 successScoreThreshold 이상인가 — 이게 실제 RL 실력을 반영하는 '진짜' 커리큘럼 게이트
            stats.Add("Refine/PlacedAllRate", lastUnplacedCount == 0 ? 1f : 0f);
            stats.Add("Refine/QualityPassRate", prevFinal >= successScoreThreshold ? 1f : 0f);
            // 케이스별 개선량·유효이동률 — 어느 케이스에서 붕괴(개선 실패)하는지 추적 (커리큘럼 검증).
            // curCaseIdx>=0(고정 케이스 사용 시)에만 기록 — 4단계 절차적 생성(curCaseIdx=-1)일 땐 대신 Episode/* 기록.
            if (caseSet.Count > 1 && curCaseIdx >= 0)
            {
                stats.Add($"RefineCase/Improve_{curCaseIdx}", prevFinal - startFinal);
                stats.Add($"RefineCase/ValidRate_{curCaseIdx}", validMoves / (float)stepsPerEpisode);
            }
            // 4단계: "세트 이름"이 없으니 대신 이번 에피소드 조합의 특성값을 기록 (작업2)
            if (currentStage == 4)
            {
                stats.Add("Episode/NumTypes", stage4NumTypesUsed);
                stats.Add("Episode/TotalCount", stage4TotalCount);
                stats.Add("Episode/TotalMass", stage4TotalMass);
                stats.Add("Episode/TotalVolume", stage4TotalVolume);
                stats.Add("Episode/MassVolumeCorr", stage4MassVolumeCorr);
                stats.Add("Episode/IsReplaySet", stage4IsReplay ? 1f : 0f);
            }
            if (verboseLog) Debug.Log($"[Refine 종료] Final={prevFinal:F3} (시작 {startFinal:F3}, Δ{prevFinal - startFinal:+0.000;-0.000}), 유효 {validMoves}/{stepsPerEpisode}");
            if (saveLayoutOnComplete) SaveLayout();
            EndEpisode();
        }
    }

    /// <summary>CSV 파일명(예: "Data/RefineCases/s2_a.csv")에서 's숫자' 접두사를 읽어 단계 번호(0~3)를 반환. 못 읽으면 -1.</summary>
    private int ParseStagePrefix(string csvPath)
    {
        string name = Path.GetFileNameWithoutExtension(csvPath);
        if (name.Length >= 2 && (name[0] == 's' || name[0] == 'S') && char.IsDigit(name[1]))
            return name[1] - '0';
        return -1;
    }

    /// <summary>
    /// 4단계: 매 에피소드 절차적으로 화물 조합을 무작위 생성 (작업1).
    /// 1) numTypes(1~stage4NumTypesRange.y)개 distinct 타입을 가중치(stage4Weights) 기반 비복원추출로 선택
    /// 2) 타입별 count를 stage4CountPerTypeRange에서 균등 샘플
    /// 3) 총개수가 stage4TotalItemsRange 밖이거나, 총질량>maxPayloadKg, 총부피>트레이부피×stage4VolumeCapFraction 이면 통째로 재추첨
    /// 4) stage4MaxSampleTries 소진 시 결정론적 최소구성(가장 작은/가벼운 타입 하나로 최소개수)으로 폴백 + 경고 로그 — 크래시 없음 보장.
    /// </summary>
    private List<CargoType> SampleStage4Manifest()
    {
        for (int attempt = 0; attempt < stage4MaxSampleTries; attempt++)
        {
            int numTypes = Mathf.Clamp(Random.Range(stage4NumTypesRange.x, stage4NumTypesRange.y + 1), 1, stage4Types.Count);
            var idxs = WeightedDistinctSample(numTypes);

            var candidate = new List<CargoType>();
            float mass = 0f, vol = 0f;
            foreach (int ti in idxs)
            {
                int count = Random.Range(stage4CountPerTypeRange.x, stage4CountPerTypeRange.y + 1);
                var t = stage4Types[ti];
                float itemVol = t.sizeM.x * t.sizeM.y * t.sizeM.z;
                for (int k = 0; k < count; k++) { candidate.Add(t); mass += t.massKg; vol += itemVol; }
            }

            if (candidate.Count < stage4TotalItemsRange.x || candidate.Count > stage4TotalItemsRange.y) continue;
            if (mass > ruleConfig.maxPayloadKg + 1e-4f) continue;
            if (vol > stage4ContainerVolume * stage4VolumeCapFraction + 1e-6f) continue;

            return candidate;
        }

        // ── 폴백: 재시도 소진 — 가장 가벼운 타입 하나로 최소개수 구성(항상 유효 범위 안). 크래시 방지 안전망. ──
        Debug.LogWarning($"[RefinementAgent] 4단계 샘플링 {stage4MaxSampleTries}회 실패(질량/부피 상한 충족 못함) — 결정론적 폴백 사용");
        CargoType lightest = stage4Types[0];
        foreach (var t in stage4Types) if (t.massKg < lightest.massKg) lightest = t;
        var fb = new List<CargoType>();
        for (int i = 0; i < stage4TotalItemsRange.x; i++) fb.Add(lightest);
        return fb;
    }

    /// <summary>가중치(stage4Weights) 기반 비복원추출로 distinct 인덱스 n개 선택.</summary>
    private List<int> WeightedDistinctSample(int n)
    {
        var pool = new List<int>(stage4Types.Count);
        var w = new List<float>(stage4Weights);
        for (int i = 0; i < stage4Types.Count; i++) pool.Add(i);

        var result = new List<int>();
        for (int pick = 0; pick < n && pool.Count > 0; pick++)
        {
            float total = 0f; foreach (var x in w) total += x;
            float r = Random.value * total, acc = 0f; int chosen = pool.Count - 1;
            for (int i = 0; i < pool.Count; i++) { acc += w[i]; if (r <= acc) { chosen = i; break; } }
            result.Add(pool[chosen]);
            pool.RemoveAt(chosen); w.RemoveAt(chosen);
        }
        return result;
    }

    private static int CountDistinctTypes(List<CargoType> m)
    {
        var seen = new HashSet<string>();
        foreach (var t in m) seen.Add(t.id);
        return seen.Count;
    }

    private static float SumMass(List<CargoType> m)
    {
        float s = 0f; foreach (var t in m) s += t.massKg; return s;
    }

    private static float SumVolume(List<CargoType> m)
    {
        float s = 0f; foreach (var t in m) s += t.sizeM.x * t.sizeM.y * t.sizeM.z; return s;
    }

    /// <summary>조합 내 "서로 다른 타입들"의 (질량,부피) 간 피어슨 상관계수. 타입 1종뿐이면 정의 안 됨 → 0.</summary>
    private static float MassVolumeCorr(List<CargoType> m)
    {
        var distinct = new Dictionary<string, CargoType>();
        foreach (var t in m) distinct[t.id] = t;
        if (distinct.Count < 2) return 0f;

        var masses = new List<float>(); var vols = new List<float>();
        foreach (var t in distinct.Values) { masses.Add(t.massKg); vols.Add(t.sizeM.x * t.sizeM.y * t.sizeM.z); }
        int n = masses.Count;
        float meanM = 0f, meanV = 0f;
        for (int i = 0; i < n; i++) { meanM += masses[i]; meanV += vols[i]; }
        meanM /= n; meanV /= n;
        float cov = 0f, varM = 0f, varV = 0f;
        for (int i = 0; i < n; i++)
        {
            float dm = masses[i] - meanM, dv = vols[i] - meanV;
            cov += dm * dv; varM += dm * dm; varV += dv * dv;
        }
        float denom = Mathf.Sqrt(varM * varV);
        return denom > 1e-9f ? cov / denom : 0f;
    }

    /// <summary>item i를 셀·회전으로 재배치. 유효하면 이동+true, 무효면 원위치 복구+false. (인덱스 유지)</summary>
    private bool TryRelocate(int itemIdx, int cellIdx, int rot)
    {
        if (itemIdx < 0 || itemIdx >= placed.Count) return false;

        var old = placed[itemIdx];
        CargoType type = old.type;
        int c = cellIdx % cols, r = cellIdx / cols;
        Vector2 cc = CellCenter(c, r);
        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;

        placed.RemoveAt(itemIdx);                                  // 자기 자신 빼고
        float restBottom = RestBottom(cc.x, cc.y, half);           // 남은 화물 위에 낙하 안착
        var cand = new RuleChecker.PlacedItem
        {
            type = type,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
            halfSize = half
        };

        if (!rules.IsValid(placed, cand))
        {
            placed.Insert(itemIdx, old);                           // 무효 → 원위치 복구
            return false;
        }
        placed.Insert(itemIdx, cand);                              // 유효 → 이동본으로 교체(인덱스 유지)
        return true;
    }

    // ── 지오메트리 헬퍼 (placed는 호출 시점의 현재 배치) ──────────────────────
    private Vector2 CellCenter(int c, int r)   // rear-left 코너 원점
    {
        float cw = ruleConfig.trayLateralM / cols, cd = ruleConfig.trayLengthM / rows;
        return new Vector2((c + 0.5f) * cw, (r + 0.5f) * cd);
    }

    /// <summary>(x,z)에 half 화물을 떨어뜨렸을 때 안착 바닥 y. (placed는 자기 자신 제거된 상태로 호출)</summary>
    private float RestBottom(float x, float z, Vector3 half)
    {
        float rest = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) < half.x + p.halfSize.x - 1e-4f &&
                Mathf.Abs(p.center.z - z) < half.z + p.halfSize.z - 1e-4f)
                rest = Mathf.Max(rest, p.center.y + p.halfSize.y);
        return rest;
    }

    private float HeightAt(float x, float z)
    {
        float h = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) < p.halfSize.x - 1e-4f &&
                Mathf.Abs(p.center.z - z) < p.halfSize.z - 1e-4f)
                h = Mathf.Max(h, p.center.y + p.halfSize.y);
        return h;
    }

    private Vector3 Cog()
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { float pm = p.type.massKg; m += pm; w += pm * p.center; }
        return m > 1e-6f ? w / m : Vector3.zero;
    }

    private float TotalMass()
    {
        float m = 0f; foreach (var p in placed) m += p.type.massKg; return m;
    }

    /// <summary>현재 refined 배치를 동적 주행 입력용 JSON(CargoLayoutFile)으로 저장. PlacementAgent.SaveLayout과 동일 포맷.</summary>
    private void SaveLayout()
    {
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in placed)
        {
            if (p.type == null) continue;
            Vector3 s = p.type.sizeM;
            float asIs    = Mathf.Abs(p.halfSize.x * 2f - s.x) + Mathf.Abs(p.halfSize.z * 2f - s.z);
            float swapped = Mathf.Abs(p.halfSize.x * 2f - s.z) + Mathf.Abs(p.halfSize.z * 2f - s.x);
            bool rot90 = swapped < asIs - 1e-5f;
            file.cargo.Add(new CargoLayoutEntry
            {
                type = p.type.name,
                localPos = p.center,
                localEuler = rot90 ? new Vector3(0f, 90f, 0f) : Vector3.zero,
                secured = true
            });
        }
        string dir = Path.Combine(Application.dataPath, "Data/Results");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, layoutOutName + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
        Debug.Log($"[RefinementAgent] 배치 저장: {placed.Count}개 → {path}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    /// <summary>배치 점수(높을수록 좋음). surrogate 모드면 −리스크×배율, 아니면 기존 CGS Final. ΔScore가 스텝 보상.</summary>
    private float Score()
    {
        if (useSurrogateReward && riskModel != null && riskModel.Loaded)
            return -riskModel.Predict(BuildRiskFeatures()) * surrogateRewardScale;
        return reward.Final(placed).total;
    }

    /// <summary>
    /// surrogate 입력 13피처 (PlacementAgent.BuildRiskFeatures와 동일 = 두 에이전트 공정비교).
    /// Unity 규약(x=폭·y=높이·z=길이), 관성은 축정렬 박스(yaw0/90) D=2·halfSize, 평행축(적재물 CoG).
    /// </summary>
    private Dictionary<string, float> BuildRiskFeatures()
    {
        Vector3 cog = Cog();
        float ixx = 0f, iyy = 0f, izz = 0f, maxTop = ruleConfig.floorTopY;
        foreach (var p in placed)
        {
            float m = p.type.massKg;
            float Dx = p.halfSize.x * 2f, Dy = p.halfSize.y * 2f, Dz = p.halfSize.z * 2f;
            float ibx = m / 12f * (Dy * Dy + Dz * Dz);
            float iby = m / 12f * (Dx * Dx + Dz * Dz);
            float ibz = m / 12f * (Dx * Dx + Dy * Dy);
            Vector3 off = p.center - cog;
            ixx += ibx + m * (off.y * off.y + off.z * off.z);
            iyy += iby + m * (off.x * off.x + off.z * off.z);
            izz += ibz + m * (off.x * off.x + off.y * off.y);
            maxTop = Mathf.Max(maxTop, p.center.y + p.halfSize.y);
        }
        return new Dictionary<string, float>
        {
            { "TargetSpeedKmh", targetSpeedKmh },
            { "RoadBankAngleDeg", roadBankAngleDeg },
            { "RoadSlopeDeg", roadSlopeDeg },
            { "VehicleBaseMassKg", vehicleBaseMassKg },
            { "CargoCount", placed.Count },
            { "TotalMassKg", TotalMass() * modelMassScaleKg },
            { "CogX", cog.x },
            { "CogY", cog.y },
            { "CogZ", cog.z },
            { "MaxHeightM", maxTop },
            { "InertiaXX", ixx },
            { "InertiaYY", iyy },
            { "InertiaZZ", izz },
        };
    }
}
