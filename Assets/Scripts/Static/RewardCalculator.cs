using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 정적 배치 보상 채점 모듈 (S2). "이 배치가 얼마나 좋은가"를 0~1 정규화 3목적함수 가중합으로 점수화.
///   R = w_LE·LE + w_CGS·CGS + w_SS·SS   (초기 0.50/0.40/0.10, DOE로 조정)
/// - LE (Loading Efficiency): 부피활용율·데드스페이스·격자밀집·접촉
/// - CGS (CoG Stability) ⭐: CoG x·z 중앙 수렴 + y(높이) 낮게
/// - SS (Stacking Stability): 무거운 것 아래·상단 평탄·포대 네스팅
/// 스텝 보상(shaping) + 최종 보상 둘 다 제공. Hard 위반은 RuleChecker가 마스킹하므로 여기선 가정하지 않음.
/// RewardConfig로 가중치·정규화 기준 인스펙터 튜닝(DOE용). RuleChecker(지지율·CoG) 재사용.
///
/// 좌표계: RuleChecker와 동일 (Unity 로컬 m, x=좌우 y=높이 z=주행/길이, 원점=트레이 rear-left 코너, 중심=W/2·L/2).
/// </summary>
public class RewardCalculator
{
    public RewardConfig cfg;
    private readonly RuleChecker rules;

    public RewardCalculator(RewardConfig config = null, RuleConfig ruleConfig = null)
    {
        cfg = config ?? new RewardConfig();
        rules = new RuleChecker(ruleConfig ?? new RuleConfig());
    }

    float TrayHalfX => rules.cfg.trayLateralM * 0.5f;   // 중심 x = W/2 (원점 아님)
    float TrayHalfZ => rules.cfg.trayLengthM * 0.5f;    // 중심 z = L/2
    float TrayMaxX => rules.cfg.trayLateralM;           // 우측 경계 (좌=0)
    float TrayMaxZ => rules.cfg.trayLengthM;            // 앞 경계 (뒤=0)
    float HeightLimit => rules.cfg.heightLimitM;
    float FloorTop => rules.cfg.floorTopY;
    float TrayVolume => rules.cfg.trayLateralM * rules.cfg.trayLengthM * rules.cfg.heightLimitM;

    /// <summary>보상 분해 결과.</summary>
    public struct Reward
    {
        public float total;          // 가중합 최종 점수
        public float le, cgs, ss;    // 각 목적함수 (0~1)
        public override string ToString() =>
            $"R={total:F3} (LE={le:F2}, CGS={cgs:F2}, SS={ss:F2})";
    }

    // ── 최종 보상: 배치 전체(목록 다 놓은 상태) 평가 ──────────────────────────
    public Reward Final(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        float le = LoadingEfficiency(placed);
        float cgs = CogStability(placed);
        float ss = StackingStability(placed);
        float total = cfg.wLE * le + cfg.wCGS * cgs + cfg.wSS * ss;
        return new Reward { total = total, le = le, cgs = cgs, ss = ss };
    }

    // ── 스텝 보상(shaping): 화물 하나 놓을 때마다 소폭. CoG 중앙·밀집 유도 ──────
    /// <summary>현재까지 배치(placed 포함, 방금 놓은 게 마지막)에 대한 작은 shaping 보상.</summary>
    public float Step(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        if (placed == null || placed.Count == 0) return 0f;
        float cgs = CogStability(placed);      // 중앙·낮음
        float compact = GridCompactness(placed); // 밀집
        return cfg.stepScale * (0.7f * cgs + 0.3f * compact);
    }

    // ── 목적함수 1: Loading Efficiency (0~1) ─────────────────────────────────
    public float LoadingEfficiency(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        float volUtil = VolumeUtilization(placed);   // 부피 활용율
        float compact = GridCompactness(placed);      // 격자 밀집(빈틈 적을수록↑)
        float contact = ContactScore(placed);         // 벽·화물 접촉
        return Clamp01(cfg.leVolW * volUtil + cfg.leCompactW * compact + cfg.leContactW * contact);
    }

    // 화물 총부피 / 적재함 부피
    float VolumeUtilization(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        float v = 0f;
        foreach (var p in placed) v += 8f * p.halfSize.x * p.halfSize.y * p.halfSize.z;
        return TrayVolume > 1e-9f ? Clamp01(v / TrayVolume) : 0f;
    }

    // 배치가 얼마나 뭉쳐있나: 화물 XZ 바운딩 스팬 대비 화물 밑면적 합 (넓게 퍼질수록↓)
    float GridCompactness(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        if (placed.Count == 0) return 0f;
        float minX = float.MaxValue, maxX = -float.MaxValue, minZ = float.MaxValue, maxZ = -float.MaxValue, area = 0f;
        foreach (var p in placed)
        {
            minX = Mathf.Min(minX, p.center.x - p.halfSize.x); maxX = Mathf.Max(maxX, p.center.x + p.halfSize.x);
            minZ = Mathf.Min(minZ, p.center.z - p.halfSize.z); maxZ = Mathf.Max(maxZ, p.center.z + p.halfSize.z);
            area += 4f * p.halfSize.x * p.halfSize.z;
        }
        float span = (maxX - minX) * (maxZ - minZ);
        return span > 1e-9f ? Clamp01(area / span) : 0f; // 1=빈틈없이 뭉침
    }

    // 벽·화물 접촉: 각 화물이 벽에 붙거나 서로 붙을수록 가점 (근접도 근사)
    float ContactScore(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        if (placed.Count == 0) return 0f;
        float score = 0f;
        foreach (var p in placed)
        {
            float nearWall = 0f;
            // 가장 가까운 벽까지 간격 (코너 원점: 좌우 min(left, W-right), 앞뒤 min(back, L-front))
            float gapX = Mathf.Min(p.center.x - p.halfSize.x, TrayMaxX - (p.center.x + p.halfSize.x));
            float gapZ = Mathf.Min(p.center.z - p.halfSize.z, TrayMaxZ - (p.center.z + p.halfSize.z));
            nearWall += Mathf.Clamp01(1f - gapX / cfg.contactGap);
            nearWall += Mathf.Clamp01(1f - gapZ / cfg.contactGap);
            score += Mathf.Clamp01(nearWall);
        }
        return Clamp01(score / placed.Count);
    }

    // ── 목적함수 2: CoG Stability (0~1) ⭐ ───────────────────────────────────
    public float CogStability(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        Vector3 cog = Cog(placed);
        // 좌우(x)·전후(z) 중앙 수렴: |cog-중심|/half → 중심(W/2,L/2)이면 1점
        float centerX = 1f - Clamp01(TrayHalfX > 1e-6f ? Mathf.Abs(cog.x - TrayHalfX) / TrayHalfX : 0f);
        float centerZ = 1f - Clamp01(TrayHalfZ > 1e-6f ? Mathf.Abs(cog.z - TrayHalfZ) / TrayHalfZ : 0f);
        // 높이(y) 낮게: 바닥(FloorTop)=1, 한도(FloorTop+HeightLimit)=0
        float h = HeightLimit > 1e-6f ? (cog.y - FloorTop) / HeightLimit : 0f;
        float lowY = 1f - Clamp01(h);
        return Clamp01(cfg.cgsCenterW * (0.5f * centerX + 0.5f * centerZ) + cfg.cgsLowW * lowY);
    }

    // ── 목적함수 3: Stacking Stability (0~1) ─────────────────────────────────
    public float StackingStability(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        if (placed.Count == 0) return 1f;
        float heavyBelow = HeavyBelowScore(placed);
        float flat = SurfaceFlatness(placed);
        return Clamp01(cfg.ssHeavyW * heavyBelow + cfg.ssFlatW * flat);
    }

    // 무거운 것 아래: 높이(center.y)와 질량의 음의 상관 → 무거울수록 낮으면 1
    float HeavyBelowScore(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        float totalM = 0f, penalty = 0f;
        foreach (var p in placed) totalM += p.Mass;
        if (totalM < 1e-6f) return 1f;
        // 각 화물: (자기보다 위에 있는 더 무거운 화물 질량) 만큼 벌점
        foreach (var a in placed)
            foreach (var b in placed)
                if (b.center.y > a.center.y + rules.cfg.eps && b.Mass > a.Mass)
                    penalty += (b.Mass - a.Mass);
        return Clamp01(1f - penalty / (totalM + 1e-6f));
    }

    // 상단 표면 평탄도: 최상단 화물들의 top 높이 편차가 작을수록 1
    float SurfaceFlatness(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        if (placed.Count < 2) return 1f;
        float mean = 0f; foreach (var p in placed) mean += p.Top; mean /= placed.Count;
        float var = 0f; foreach (var p in placed) var += (p.Top - mean) * (p.Top - mean);
        float std = Mathf.Sqrt(var / placed.Count);
        return 1f - Clamp01(std / cfg.flatnessRef); // 편차 flatnessRef(기본 0.1m)면 0점
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    static Vector3 Cog(IReadOnlyList<RuleChecker.PlacedItem> placed)
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { m += p.Mass; w += p.Mass * p.center; }
        return m > 1e-6f ? w / m : Vector3.zero;
    }
    static float Clamp01(float v) => Mathf.Clamp01(v);
}

/// <summary>보상 가중치·정규화 기준 (인스펙터 튜닝 / DOE용). 초기값 = 설계문서 5절.</summary>
[System.Serializable]
public class RewardConfig
{
    [Header("목적함수 가중치 (DOE로 조정, 초기 0.50/0.40/0.10)")]
    public float wLE = 0.50f;
    public float wCGS = 0.40f;
    public float wSS = 0.10f;

    [Header("스텝 shaping")]
    [Tooltip("스텝 보상 크기(최종 보상 대비 작게). 0이면 스텝 보상 끔")]
    public float stepScale = 0.05f;

    [Header("LE 내부 가중 (합 1 권장)")]
    public float leVolW = 0.4f;      // 부피 활용율
    public float leCompactW = 0.4f;  // 격자 밀집
    public float leContactW = 0.2f;  // 접촉
    [Tooltip("벽 접촉 판정 거리(m). 이 안이면 접촉으로 봄")]
    public float contactGap = 0.03f;

    [Header("CGS 내부 가중 (합 1 권장)")]
    public float cgsCenterW = 0.5f;  // x·z 중앙
    public float cgsLowW = 0.5f;     // y 낮게

    [Header("SS 내부 가중 (합 1 권장)")]
    public float ssHeavyW = 0.6f;    // 무거운 것 아래
    public float ssFlatW = 0.4f;     // 상단 평탄
    [Tooltip("평탄도 기준 편차(m). top std가 이 값이면 평탄점수 0")]
    public float flatnessRef = 0.1f;
}
