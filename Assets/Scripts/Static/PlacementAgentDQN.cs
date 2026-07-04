using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// 정적 배치 DQN 에이전트. PlacementAgent(PPO, 3브랜치)와 로직은 동일하지만,
/// DQN(stable-baselines3)은 Discrete 브랜치 1개만 지원하므로 (종류·셀·회전)을
/// 하나의 flat Discrete(NumTypes*NumCells*2) 액션으로 합친다.
///
/// 연구 목적: 화물 조합/형상차이 → dead space·용적률, 배치위치/적층높이 → CoG·좌우/전후 하중편차.
/// 학습 신호는 RewardCalculator(LE+CGS+SS)를 그대로 쓰고, 에피소드 종료 시 분석용 raw 지표를
/// CSV(csvPath)에 별도로 남긴다 (보상은 가중합 스칼라라 그 자체로는 분석에 못 씀).
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class PlacementAgentDQN : Agent
{
    [Header("규제/보상 설정")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();

    [Header("격자 (x=cols, z=rows)")]
    public int cols = 6;
    public int rows = 16;

    [Header("에피소드 화물 목록 (커리큘럼 1단계: 3~5개)")]
    public int manifestMin = 3;
    public int manifestMax = 5;
    public string[] usableTypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006",
        "C-001","T-001","P-001","P-002","P-003","S-001"
    };

    [Header("무효 행동 처리")]
    public float invalidPenalty = 0.05f;
    public int maxInvalidPerEpisode = 20;

    [Header("분석용 CSV 로그 (dead space/용적률/CoG 편차)")]
    public bool logEpisodeMetrics = true;
    public string csvPath = "Assets/Data/Results/dqn_placement_metrics.csv";

    [Header("디버그")]
    [Tooltip("켜면 배치/에피소드 결과를 콘솔에 찍음 (학습 시엔 끄기)")]
    public bool verboseLog = true;
    private int episodeIdx;

    // ── 내부 상태 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private List<CargoType> pool;
    private int[] remaining;
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private int invalidCount;
    private int placedTarget;
    private bool setupDone;
    private string episodeManifest;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;
    private int NumCells => cols * rows;
    private int NumTypes => pool.Count;
    private int ObsSize => NumCells + 3 + 1 + 2 + NumTypes;
    // flat 액션: a = typeIdx*(NumCells*2) + cellIdx*2 + rot
    private int NumActions => NumTypes * NumCells * 2;

    private void Awake() => Setup();

    public override void Initialize() => Setup();

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        rules = new RuleChecker(ruleConfig);
        reward = new RewardCalculator(rewardConfig, ruleConfig);

        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;
        pool = new List<CargoType>();
        foreach (var id in usableTypeIds) if (cat.TryGetValue(id, out var t)) pool.Add(t);

        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(NumActions);
        if (MaxStep == 0) MaxStep = (manifestMax + maxInvalidPerEpisode) * 3;

        if (logEpisodeMetrics) InitCsv();

        Debug.Log($"[PlacementAgentDQN] obs={ObsSize}, action=flat({NumTypes}x{NumCells}x2={NumActions}), pool={NumTypes}종");
    }

    public override void OnEpisodeBegin()
    {
        placed.Clear();
        invalidCount = 0;
        remaining = new int[NumTypes];

        placedTarget = Random.Range(manifestMin, manifestMax + 1);
        for (int i = 0; i < placedTarget; i++)
            remaining[Random.Range(0, NumTypes)]++;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) sb.Append($"{pool[i].id}x{remaining[i]} ");
        episodeManifest = sb.ToString().Trim();

        if (verboseLog)
            Debug.Log($"[에피소드 {++episodeIdx} 시작] 실을 화물 {placedTarget}개: {episodeManifest}");
        else
            episodeIdx++;
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
        int cellsX2 = NumCells * 2;

        // 종류(remaining 0)면 그 종류에 속한 모든 (셀,회전) 마스킹
        for (int t = 0; t < NumTypes; t++)
        {
            if (remaining[t] > 0) continue;
            int baseIdx = t * cellsX2;
            for (int k = 0; k < cellsX2; k++) mask.SetActionEnabled(0, baseIdx + k, false);
        }

        // 높이한도까지 꽉 찬 셀이면 모든 (종류,회전) 조합 마스킹
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                if (HeightAt(cc.x, cc.y) < ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f) continue;
                int cellIdx = r * cols + c;
                for (int t = 0; t < NumTypes; t++)
                {
                    mask.SetActionEnabled(0, t * cellsX2 + cellIdx * 2 + 0, false);
                    mask.SetActionEnabled(0, t * cellsX2 + cellIdx * 2 + 1, false);
                }
            }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int a = actions.DiscreteActions[0];
        int cellsX2 = NumCells * 2;
        int typeIdx = a / cellsX2;
        int rem = a % cellsX2;
        int cellIdx = rem / 2;
        int rot = rem % 2;

        if (typeIdx < 0 || typeIdx >= NumTypes || remaining[typeIdx] <= 0) { Fail(); return; }

        CargoType type = pool[typeIdx];
        int c = cellIdx % cols, r = cellIdx / cols;
        Vector2 cc = CellCenter(c, r);

        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;

        float restBottom = RestBottom(cc.x, cc.y, half);
        var cand = new RuleChecker.PlacedItem
        {
            type = type,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
            halfSize = half
        };

        if (!rules.IsValid(placed, cand)) { Fail(); return; }

        placed.Add(cand);
        remaining[typeIdx]--;
        invalidCount = 0;
        AddReward(reward.Step(placed));

        if (verboseLog)
            Debug.Log($"  v {type.id} 배치 @ 셀({c},{r}) rot{rot} -> 높이 {cand.Top:F3}m | 누적 {placed.Count}/{placedTarget}개");

        if (AllPlaced())
        {
            var rf = reward.Final(placed);
            AddReward(rf.total);
            if (logEpisodeMetrics) LogEpisodeMetrics(rf, completed: true);
            if (verboseLog) Debug.Log($"[에피소드 {episodeIdx} 완료] 전부 배치! 최종보상 {rf}");
            EndEpisode();
        }
    }

    private void Fail()
    {
        AddReward(-invalidPenalty);
        if (++invalidCount >= maxInvalidPerEpisode)
        {
            AddReward(-0.5f);
            if (logEpisodeMetrics) LogEpisodeMetrics(reward.Final(placed), completed: false);
            if (verboseLog) Debug.Log($"[에피소드 {episodeIdx} 중단] 무효행동 {maxInvalidPerEpisode}회 누적 (배치 {placed.Count}/{placedTarget})");
            EndEpisode();
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        // 수동/디버그: 남은 종류 아무거나 + 랜덤 셀 + 랜덤 회전
        var d = actionsOut.DiscreteActions;
        int t = 0; for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) { t = i; break; }
        int cellIdx = Random.Range(0, NumCells);
        int rot = Random.Range(0, 2);
        d[0] = t * (NumCells * 2) + cellIdx * 2 + rot;
    }

    // ── 분석용 CSV (dead space/용적률/CoG 편차 — 보상 스칼라와 별개로 원값 기록) ──
    void InitCsv()
    {
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(csvPath))
            File.WriteAllText(csvPath,
                "episode,manifest,placedCount,placedTarget,completed,volUtil,deadSpaceRatio,cogX,cogZ,cogY,lateralImbalanceFrac,foreAftImbalanceFrac,rewardTotal,le,cgs,ss\n");
    }

    void LogEpisodeMetrics(RewardCalculator.Reward rf, bool completed)
    {
        float volUtil = reward.VolumeUtilization(placed);
        float deadSpaceRatio = 1f - volUtil; // 근사: 적재함 전체부피 기준. 실제 빈틈 형상까지는 반영 안 함.
        Vector3 cog = Cog();
        float lateralImbalance = HalfX > 1e-6f ? Mathf.Abs(cog.x) / HalfX : 0f;
        float foreAftImbalance = HalfZ > 1e-6f ? Mathf.Abs(cog.z) / HalfZ : 0f;

        string line = string.Join(",", new[]
        {
            episodeIdx.ToString(),
            episodeManifest.Replace(",", ";"),
            placed.Count.ToString(),
            placedTarget.ToString(),
            completed ? "1" : "0",
            volUtil.ToString("F4"),
            deadSpaceRatio.ToString("F4"),
            cog.x.ToString("F4"),
            cog.z.ToString("F4"),
            cog.y.ToString("F4"),
            lateralImbalance.ToString("F4"),
            foreAftImbalance.ToString("F4"),
            rf.total.ToString("F4"),
            rf.le.ToString("F4"),
            rf.cgs.ToString("F4"),
            rf.ss.ToString("F4"),
        }) + "\n";
        File.AppendAllText(csvPath, line);
    }

    // ── 헬퍼 (PlacementAgent.cs와 동일) ──────────────────────────────────────
    private Vector2 CellCenter(int c, int r)
    {
        float x = -HalfX + (c + 0.5f) * ruleConfig.trayLateralM / cols;
        float z = -HalfZ + (r + 0.5f) * ruleConfig.trayLengthM / rows;
        return new Vector2(x, z);
    }

    private float HeightAt(float x, float z)
    {
        float top = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) <= p.halfSize.x && Mathf.Abs(p.center.z - z) <= p.halfSize.z)
                top = Mathf.Max(top, p.Top);
        return top;
    }

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
