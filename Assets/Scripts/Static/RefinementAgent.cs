using System.Collections.Generic;
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

    [Header("에피소드")]
    [Tooltip("한 에피소드에 허용하는 재배치 수")]
    public int stepsPerEpisode = 25;
    [Tooltip("무효 이동(겹침/이탈) 시 작은 페널티")]
    public float invalidMovePenalty = 0.02f;

    [Header("디버그")]
    public bool verboseLog = false;

    // ── 내부 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private BinPacker packer;
    private List<CargoType> manifestList;                                   // 시작 배치용
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private int numItems;
    private int stepCount;
    private float prevFinal;
    private bool setupDone;
    private float startFinal;      // 에피소드 시작(빈패커) Final — 개선량 계산 기준
    private int validMoves;        // 이번 에피소드 유효 이동 수 (계측)
    private float minHalfXZ;       // manifest 중 가장 작은 화물의 최소 반치수 (경계 마스킹용)

    /// <summary>시각화용 읽기 전용 배치.</summary>
    public IReadOnlyList<RuleChecker.PlacedItem> PlacedItems => placed;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;
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

        manifestList = CargoManifest.Resolve(startManifest, "", out _);
        numItems = Mathf.Max(1, manifestList.Count);

        // 경계 마스킹용: 회전 포함, 가장 작은 화물의 최소 반치수. 이보다 좁게 남은 가장자리 셀은
        // 어떤 화물 중심을 놓아도 트레이를 벗어나므로 미리 막는다.
        minHalfXZ = float.MaxValue;
        foreach (var t in manifestList)
            minHalfXZ = Mathf.Min(minHalfXZ, Mathf.Min(t.sizeM.x, t.sizeM.z) * 0.5f);
        if (minHalfXZ == float.MaxValue) minHalfXZ = 0f;

        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(numItems, NumCells, 2); // 아이템·셀·회전
        if (MaxStep == 0) MaxStep = stepsPerEpisode + 2;

        Debug.Log($"[RefinementAgent] obs={ObsSize}, action=({numItems},{NumCells},2), 시작화물={numItems}개, mode={startPackMode}");
    }

    public override void OnEpisodeBegin()
    {
        // 빈패커로 시작 배치 생성 (결정론적 → 매 에피소드 동일 = boxpack001)
        var unplaced = new List<CargoType>();
        var packed = packer.Pack(manifestList, unplaced);
        placed.Clear();
        foreach (var p in packed)
            placed.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });

        stepCount = 0;
        validMoves = 0;
        prevFinal = placed.Count > 0 ? reward.Final(placed).total : 0f;
        startFinal = prevFinal;
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
        // 2) CoG
        Vector3 cog = Cog();
        sensor.AddObservation(HalfX > 1e-6f ? cog.x / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? cog.z / HalfZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));
        // 3) 총질량
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));
        // 4) CoG 편차(절대)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z) / HalfZ : 0f);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(아이템)·branch2(회전)은 마스킹 불가:
        //  - 아이템 16개는 전부 실재 → 다 유효 후보.
        //  - 회전은 어떤 아이템을 고를지에 의존 → 조합 마스킹 불가(ML-Agents는 브랜치별 독립 마스킹).
        // branch1(셀)만 막는다. 브랜치 독립 마스킹이라 겹침(아이템·회전 조합 의존)은 못 막고,
        // "어떤 화물도 못 놓는 셀"만 근사로 제거한다.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                bool block =
                    // (1) 높이 한도까지 꽉 찬 셀 — 그 위엔 아무것도 못 올림
                    HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f ||
                    // (2) 최소 화물조차 중심으로 놓으면 트레이를 벗어나는 가장자리 셀
                    cc.x - minHalfXZ < -HalfX - 1e-4f || cc.x + minHalfXZ > HalfX + 1e-4f ||
                    cc.y - minHalfXZ < -HalfZ - 1e-4f || cc.y + minHalfXZ > HalfZ + 1e-4f;
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
            float now = reward.Final(placed).total;
            AddReward(now - prevFinal);      // ΔFinal (개선분). 누적 = 시작 대비 개선
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
            if (verboseLog) Debug.Log($"[Refine 종료] Final={prevFinal:F3} (시작 {startFinal:F3}, Δ{prevFinal - startFinal:+0.000;-0.000}), 유효 {validMoves}/{stepsPerEpisode}");
            EndEpisode();
        }
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
    private Vector2 CellCenter(int c, int r)
    {
        float cw = ruleConfig.trayLateralM / cols, cd = ruleConfig.trayLengthM / rows;
        return new Vector2(-HalfX + (c + 0.5f) * cw, -HalfZ + (r + 0.5f) * cd);
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
}
