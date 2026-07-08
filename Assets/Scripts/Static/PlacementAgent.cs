using System.Collections;
using System.Collections.Generic;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// S3 — 정적 배치 강화학습 에이전트 (ML-Agents, PPO).
/// 매 스텝 "남은 화물 중 무엇을(branch0) · 어느 격자셀에(branch1) · 어느 회전으로(branch2)" 배치할지 결정.
/// RuleChecker로 불가 행동 마스킹, RewardCalculator로 스텝+최종 보상.
/// 에피소드 = 랜덤 화물 목록(manifest) 하나를 다 놓거나 더 못 놓을 때까지.
///
/// 좌표계: Unity 로컬 m (x=좌우 21cm, y=높이 27한도, z=주행/길이 62cm), 원점=트레이 중심, 바닥 y=floorTop.
/// 격자: cols(x) × rows(z) 셀. 셀 중심에 화물을 "떨어뜨려" 바닥/위 화물에 안착.
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class PlacementAgent : Agent
{
    [Header("규제/보상 설정")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();

    [Header("격자 (x=cols, z=rows)")]
    public int cols = 6;    // 좌우 21cm
    public int rows = 16;   // 길이 62cm

    [Header("에피소드 화물 목록 (커리큘럼 1단계: 3~5개)")]
    public int manifestMin = 3;
    public int manifestMax = 5;
    [Tooltip("에피소드에 쓸 화물 종류 풀 (트레이에 정상 배치 가능한 것만). 비우면 기본 12종")]
    public string[] usableTypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006",
        "C-001","T-001","P-001","P-002","P-003","S-001"
    };

    [Header("무효 행동 처리")]
    public float invalidPenalty = 0.05f;
    public int maxInvalidPerEpisode = 20;

    [Header("자동 루프 실행")]
    public bool autoRunHeuristicEpisodes = false;
    public int autoRunEpisodeCount = 10;
    public float autoRunStepDelay = 0.01f;

    [Header("디버그")]
    [Tooltip("켜면 배치/에피소드 결과를 콘솔에 찍음 (학습 시엔 끄기)")]
    public bool verboseLog = true;

    public event System.Action<PlacementEpisodeMetrics> EpisodeFinished;
    public PlacementEpisodeMetrics LastEpisodeMetrics { get; private set; }
    public float CumulativeReward { get; private set; }
    public int TotalValidPlacements { get; private set; }
    public int TotalInvalidPlacements { get; private set; }
    public bool IsEpisodeActive => currentEpisodeActive;

    private int episodeIdx;
    private float episodeReward;
    private int episodeValidPlacements;
    private int episodeInvalidPlacements;
    private int episodeStepCount;

    // ── 내부 상태 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private List<CargoType> pool;                  // usableTypeIds → CargoType
    private int[] remaining;                        // 종류별 남은 수 (pool 인덱스)
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private int invalidCount;
    private int placedTarget;                       // 이번 에피소드 총 배치 목표 수
    private bool setupDone;                          // Awake/Initialize 중복 방지
    private bool currentEpisodeActive;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;
    private int NumCells => cols * rows;
    private int NumTypes => pool.Count;
    private int ObsSize => NumCells + 3 + 1 + 2 + NumTypes; // 높이맵 + CoG(3) + 질량(1) + CoG편차(2) + 남은목록

    // ⚠️ obs·ActionSpec은 Awake()에서 세팅해야 한다.
    // ML-Agents 2.0.2 LazyInitialize 순서: InitializeActuators() → Initialize() → InitializeSensors().
    // 즉 액추에이터는 Initialize()보다 먼저 ActionSpec을 읽으므로, Initialize()에서 바꾸면 마스킹 branch 크기가 안 맞아
    // "Invalid Action Masking: Action Mask is too large for specified branch" 발생. Awake()는 OnEnable(LazyInitialize)보다
    // 먼저 도므로 여기서 세팅하면 액추에이터 생성 전에 올바른 스펙이 반영된다.
    private void Awake() => Setup();

    public override void Initialize() => Setup();

    public void SetupForRuntime() => Setup();
    public void BeginEpisodeForRuntime() => OnEpisodeBegin();

    public void ApplyRuntimeConfig(RewardConfig rewardCfg = null, RuleConfig ruleCfg = null)
    {
        if (rewardCfg != null) rewardConfig = rewardCfg;
        if (ruleCfg != null) ruleConfig = ruleCfg;

        if (rewardConfig == null) rewardConfig = new RewardConfig();
        if (ruleConfig == null) ruleConfig = new RuleConfig();

        rules = new RuleChecker(ruleConfig);
        reward = new RewardCalculator(rewardConfig, ruleConfig);
    }

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        ApplyRuntimeConfig(rewardConfig, ruleConfig);

        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;
        pool = new List<CargoType>();
        foreach (var id in usableTypeIds) if (cat.TryGetValue(id, out var t)) pool.Add(t);

        // 관측 크기 · 행동 스펙 코드로 설정 (인스펙터 수동세팅 불필요)
        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(NumTypes, NumCells, 2);
        if (MaxStep == 0) MaxStep = (manifestMax + maxInvalidPerEpisode) * 3;

        Debug.Log($"[PlacementAgent] obs={ObsSize}, action=({NumTypes},{NumCells},2), pool={NumTypes}종");
    }

    private void Start()
    {
        if (autoRunHeuristicEpisodes)
            StartCoroutine(AutoRunHeuristicEpisodes());
    }

    private IEnumerator AutoRunHeuristicEpisodes()
    {
        Setup();
        for (int ep = 0; ep < autoRunEpisodeCount; ep++)
        {
            OnEpisodeBegin();
            while (currentEpisodeActive)
            {
                var actions = new ActionBuffers(new float[0], new int[3]);
                Heuristic(actions);
                OnActionReceived(actions);

                if (autoRunStepDelay > 0f)
                    yield return new WaitForSeconds(autoRunStepDelay);
                else
                    yield return null;
            }
        }

        if (verboseLog)
            Debug.Log($"[PlacementAgent] 자동 루프 완료: {autoRunEpisodeCount} 에피소드");
    }

    public override void OnEpisodeBegin()
    {
        currentEpisodeActive = true;
        placed.Clear();
        invalidCount = 0;
        remaining = new int[NumTypes];
        episodeReward = 0f;
        episodeValidPlacements = 0;
        episodeInvalidPlacements = 0;
        episodeStepCount = 0;

        // 랜덤 manifest: 총 manifestMin~Max개를 풀에서 무작위 종류로
        placedTarget = Random.Range(manifestMin, manifestMax + 1);
        for (int i = 0; i < placedTarget; i++)
            remaining[Random.Range(0, NumTypes)]++;

        if (verboseLog)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) sb.Append($"{pool[i].id}×{remaining[i]} ");
            Debug.Log($"[에피소드 {++episodeIdx} 시작] 실을 화물 {placedTarget}개: {sb}");
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) 높이맵 (셀별 적재 높이 / 높이한도)
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                float h = (HeightAt(cc.x, cc.y) - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM);
                sensor.AddObservation(Mathf.Clamp01(h));
            }

        // 2) 현재 CoG (정규화)
        Vector3 cog = Cog();
        sensor.AddObservation(HalfX > 1e-6f ? cog.x / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? cog.z / HalfZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));

        // 3) 총질량 / payload
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));

        // 4) CoG 편차 절대값(좌우·전후)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z) / HalfZ : 0f);

        // 5) 종류별 남은 수 (정규화)
        for (int i = 0; i < NumTypes; i++)
            sensor.AddObservation(remaining[i] / Mathf.Max(1f, placedTarget));
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(종류): 남은 수 0인 종류 마스킹
        for (int i = 0; i < NumTypes; i++)
            if (remaining[i] <= 0) mask.SetActionEnabled(0, i, false);

        // branch1(셀): 높이한도까지 꽉 찬 셀 마스킹 (그 위엔 아무것도 못 놓음)
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                if (HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f)
                    mask.SetActionEnabled(1, r * cols + c, false);
            }
        // branch2(회전)는 종류의존이라 마스킹 불가 → 무효 조합은 보상 페널티로 학습
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int typeIdx = actions.DiscreteActions[0];
        int cellIdx = actions.DiscreteActions[1];
        int rot = actions.DiscreteActions[2]; // 0=그대로, 1=yaw90

        if (typeIdx < 0 || typeIdx >= NumTypes || remaining[typeIdx] <= 0) { Fail(); return; }

        CargoType type = pool[typeIdx];
        int c = cellIdx % cols, r = cellIdx / cols;
        Vector2 cc = CellCenter(c, r);

        // halfSize (yaw90이면 x↔z 스왑)
        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;

        // 안착 높이: 셀 위치에서 아래로 떨어뜨려 바닥 또는 기존 화물 위
        float restBottom = RestBottom(cc.x, cc.y, half);
        var cand = new RuleChecker.PlacedItem
        {
            type = type,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
            halfSize = half
        };

        if (!rules.IsValid(placed, cand)) { Fail(); return; }

        // 유효 배치
        placed.Add(cand);
        remaining[typeIdx]--;
        invalidCount = 0;
        episodeValidPlacements++;
        episodeStepCount++;
        float stepReward = reward.Step(placed);
        episodeReward += stepReward;
        AddReward(stepReward); // 스텝 shaping

        if (verboseLog)
            Debug.Log($"  ✔ {type.id} 배치 @ 셀({c},{r}) rot{rot} → 높이 {cand.Top:F3}m | 누적 {placed.Count}/{placedTarget}개");

        // 목록 다 놓음 → 최종 보상 + 종료
        if (AllPlaced())
        {
            var rf = reward.Final(placed);
            float finalReward = rf.total;
            episodeReward += finalReward;
            AddReward(finalReward);
            PublishEpisodeMetrics("completed");
            if (verboseLog) Debug.Log($"[에피소드 {episodeIdx} 완료] 전부 배치! 최종보상 {rf}");
            FinishEpisode();
            EndEpisode();
        }
    }

    private void Fail()
    {
        episodeInvalidPlacements++;
        float penalty = -invalidPenalty;
        episodeReward += penalty;
        AddReward(penalty);
        if (++invalidCount >= maxInvalidPerEpisode)
        {
            float terminalPenalty = -0.5f;
            episodeReward += terminalPenalty;
            AddReward(terminalPenalty); // 반복 실패 = 큰 감점 후 종료
            PublishEpisodeMetrics("failed");
            FinishEpisode();
            EndEpisode();
        }
    }

    private void FinishEpisode()
    {
        currentEpisodeActive = false;
    }

    private void PublishEpisodeMetrics(string reason)
    {
        LastEpisodeMetrics = new PlacementEpisodeMetrics
        {
            episodeIndex = episodeIdx,
            reason = reason,
            totalReward = episodeReward,
            validPlacements = episodeValidPlacements,
            invalidPlacements = episodeInvalidPlacements,
            stepCount = episodeStepCount,
            stepScale = rewardConfig.stepScale,
            wLE = rewardConfig.wLE,
            wCGS = rewardConfig.wCGS,
            wSS = rewardConfig.wSS,
            supportRatioMin = ruleConfig.supportRatioMin
        };

        CumulativeReward += episodeReward;
        TotalValidPlacements += episodeValidPlacements;
        TotalInvalidPlacements += episodeInvalidPlacements;

        EpisodeFinished?.Invoke(LastEpisodeMetrics);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // 수동/디버그: 남은 종류 아무거나 + 랜덤 셀 + 회전0
        var d = actionsOut.DiscreteActions;
        int t = 0; for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) { t = i; break; }
        d[0] = t; d[1] = Random.Range(0, NumCells); d[2] = 0;
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private Vector2 CellCenter(int c, int r)  // → (x, z) 트레이 로컬
    {
        float x = -HalfX + (c + 0.5f) * ruleConfig.trayLateralM / cols;
        float z = -HalfZ + (r + 0.5f) * ruleConfig.trayLengthM / rows;
        return new Vector2(x, z);
    }

    // 점(x,z)에서의 현재 적재 상단 높이 (그 점을 덮는 화물들의 최대 top; 없으면 바닥)
    private float HeightAt(float x, float z)
    {
        float top = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) <= p.halfSize.x && Mathf.Abs(p.center.z - z) <= p.halfSize.z)
                top = Mathf.Max(top, p.Top);
        return top;
    }

    // 후보 밑면(footprint)이 놓일 바닥 높이: 겹치는 기존 화물들의 최대 top
    private float RestBottom(float x, float z, Vector3 half)
    {
        float bottom = ruleConfig.floorTopY;
        foreach (var p in placed)
        {
            bool overlapX = Mathf.Abs(p.center.x - x) < p.halfSize.x + half.x;
            bool overlapZ = Mathf.Abs(p.center.z - z) < p.halfSize.z + half.z;
            if (overlapX && overlapZ) bottom = Mathf.Max(bottom, p.Top);
        }
        return bottom;
    }

    private bool AllPlaced() { foreach (var n in remaining) if (n > 0) return false; return true; }
    private float TotalMass() { float m = 0f; foreach (var p in placed) m += p.Mass; return m; }
    private Vector3 Cog()
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { m += p.Mass; w += p.Mass * p.center; }
        return m > 1e-6f ? w / m : Vector3.zero;
    }
}
