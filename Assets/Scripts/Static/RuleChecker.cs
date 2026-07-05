using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 정적 배치 규제 판정 단일 모듈 (RL 마스킹 · 검증 · 대시보드 공유).
/// Hard(불가/절대금지) = IsValid()가 false → 행동 마스킹.
/// 측정치(지지율·축하중·CoG편차 등)는 Evaluate()로 노출 → S2 보상에서 사용.
///
/// 좌표계 (Unity 트레이 로컬, m, 원점=트레이 중심):
///   x = 좌우(lateral, 21cm)   y = 높이(27cm 적층한도)   z = 주행/길이(61cm)
///   ※ 설계문서 x(주행) = Unity z, 설계문서 y(좌우) = Unity x, 설계문서 z(높이) = Unity y.
///   바닥 top y = floorTopY. 화물 AABB는 회전(0/90) 반영된 축정렬 halfSize로 표현.
/// </summary>
public class RuleChecker
{
    public RuleConfig cfg;

    public RuleChecker(RuleConfig config = null) { cfg = config ?? new RuleConfig(); }

    // ── 배치 1개 (AABB 추상화). center/halfSize는 트레이 로컬 m. ──
    public struct PlacedItem
    {
        public CargoType type;
        public Vector3 center;    // 중심 (x=좌우, y=높이중심, z=길이)
        public Vector3 halfSize;  // 축정렬 반크기 (회전 반영)

        public CargoShape Shape => type != null ? type.shape : CargoShape.Box;
        public float Mass => type != null ? type.massKg : 0f;
        public float Bottom => center.y - halfSize.y;
        public float Top => center.y + halfSize.y;
    }

    /// <summary>측정 결과 (소프트 보상·디버그용).</summary>
    public struct RuleReport
    {
        public float totalMassKg;
        public float supportRatio;   // 후보의 밑면 지지율 (0~1)
        public Vector3 cog;          // 트레이 로컬 CoG (보상 CGS용)
        public float cogForeAftFrac; // |cog.z| / trayHalfZ (0=중앙, 1=끝)
        public float cogLateralFrac; // |cog.x| / trayHalfX
    }

    float TrayHalfX => cfg.trayLateralM * 0.5f;
    float TrayHalfZ => cfg.trayLengthM * 0.5f;
    float HeightTop => cfg.floorTopY + cfg.heightLimitM;

    // ── Hard 판정: 하나라도 걸리면 false + 사유 ─────────────────────────────
    public bool IsValid(IReadOnlyList<PlacedItem> placed, PlacedItem cand, out string reason)
    {
        if (!H1_Payload(placed, cand)) { reason = "H1 과적(>7kg)"; return false; }
        if (!H2_Bounds(cand)) { reason = "H2 적재함 경계 이탈"; return false; }
        if (!H3_NoOverlap(placed, cand)) { reason = "H3 화물 겹침"; return false; }
        if (!H4_PipeAlongZ(cand)) { reason = "H4 파이프 주행축(z) 아님"; return false; }
        if (!H5_PipeOnFloor(cand)) { reason = "H5 파이프 비바닥"; return false; }
        if (!H6_PipeNoStack(placed, cand)) { reason = "H6 파이프 상/하 적재"; return false; }
        if (!H7_H10_BagStacking(placed, cand)) { reason = "H7/H10 포대 위 비포대"; return false; }
        if (!H8_SupportRatio(placed, cand)) { reason = "H8 지지율<70%"; return false; }
        if (!H13_Height(cand)) { reason = "H13 높이>27cm"; return false; }
        reason = "";
        return true;
    }

    public bool IsValid(IReadOnlyList<PlacedItem> placed, PlacedItem cand) => IsValid(placed, cand, out _);

    // ── 개별 Hard 규칙 ───────────────────────────────────────────────────────
    // H1 총질량 ≤ 7kg (목업)
    bool H1_Payload(IReadOnlyList<PlacedItem> placed, PlacedItem cand)
        => TotalMass(placed) + cand.Mass <= cfg.maxPayloadKg + 1e-4f;

    // H2 적재함 내부. 파이프는 길이축 z 오버행 허용하되 **뒤(-z=테일게이트)만** —
    // 앞(+z=운전석/캐빈)으로는 어떤 화물도 튀어나올 수 없음(물리적 불가). 손글씨 ④/③ Case b.
    // 축 규약: LoadCalculator 기준 front=+z, right=+x.
    bool H2_Bounds(PlacedItem c)
    {
        if (c.center.x - c.halfSize.x < -TrayHalfX - cfg.eps) return false;
        if (c.center.x + c.halfSize.x > TrayHalfX + cfg.eps) return false;
        if (c.Bottom < cfg.floorTopY - cfg.eps) return false;

        // 앞(+z=캐빈) 경계는 모든 화물 강제 — 오버행 금지
        if (c.center.z + c.halfSize.z > TrayHalfZ + cfg.eps) return false;
        // 뒤(-z=테일게이트)는 파이프만 오버행 허용
        if (c.Shape != CargoShape.Pipe && c.center.z - c.halfSize.z < -TrayHalfZ - cfg.eps) return false;

        return true;
    }

    // H3 AABB 겹침 금지 (3D). 격자 중첩(손글씨⑨)도 이걸로 커버.
    bool H3_NoOverlap(IReadOnlyList<PlacedItem> placed, PlacedItem c)
    {
        foreach (var p in placed) if (AabbOverlap(p, c)) return false;
        return true;
    }

    // H4 파이프는 주행축(z, 길이) 방향 — z 반크기가 최장
    bool H4_PipeAlongZ(PlacedItem c)
    {
        if (c.Shape != CargoShape.Pipe) return true;
        return c.halfSize.z >= c.halfSize.x - cfg.eps && c.halfSize.z >= c.halfSize.y - cfg.eps;
    }

    // H5 파이프 바닥층
    bool H5_PipeOnFloor(PlacedItem c)
    {
        if (c.Shape != CargoShape.Pipe) return true;
        return c.Bottom <= cfg.floorTopY + cfg.eps;
    }

    // H6 파이프 위/아래 타화물 금지
    bool H6_PipeNoStack(IReadOnlyList<PlacedItem> placed, PlacedItem c)
    {
        if (c.Shape == CargoShape.Pipe)
        {
            // 파이프 기둥(x,z 겹침) 위에 아무것도 없어야 함 (바닥이라 아래는 없음)
            foreach (var p in placed) if (XzOverlap(p, c) > 0f) return false;
            return true;
        }
        // 비파이프: 바로 아래가 파이프면 금지
        foreach (var p in placed)
            if (p.Shape == CargoShape.Pipe && XzOverlap(p, c) > 0f && p.Top > c.Bottom - cfg.eps)
                return false;
        return true;
    }

    // H7 포대 위 상자 금지 + H10 포대 위엔 포대만
    bool H7_H10_BagStacking(IReadOnlyList<PlacedItem> placed, PlacedItem c)
    {
        if (c.Shape == CargoShape.Sack) return true; // 포대는 포대 위 OK
        foreach (var s in Supporters(placed, c))
            if (s.Shape == CargoShape.Sack) return false; // 포대 위에 비포대 금지
        return true;
    }

    // H8 밑면 지지율 ≥ 70% (바닥이면 100%)
    bool H8_SupportRatio(IReadOnlyList<PlacedItem> placed, PlacedItem c)
        => SupportRatio(placed, c) >= cfg.supportRatioMin - 1e-4f;

    // H13 높이 27cm 초과 금지
    bool H13_Height(PlacedItem c) => c.Top <= HeightTop + cfg.eps;

    // ── 측정치 (소프트 보상용) ───────────────────────────────────────────────
    public RuleReport Evaluate(IReadOnlyList<PlacedItem> placed, PlacedItem cand)
    {
        Vector3 cog = Cog(placed, cand);
        return new RuleReport
        {
            totalMassKg = TotalMass(placed) + cand.Mass,
            supportRatio = SupportRatio(placed, cand),
            cog = cog,
            cogForeAftFrac = TrayHalfZ > 1e-6f ? Mathf.Abs(cog.z) / TrayHalfZ : 0f,
            cogLateralFrac = TrayHalfX > 1e-6f ? Mathf.Abs(cog.x) / TrayHalfX : 0f,
        };
    }

    // ── 기하/물리 헬퍼 ───────────────────────────────────────────────────────
    static float TotalMass(IReadOnlyList<PlacedItem> placed)
    {
        float m = 0f; foreach (var p in placed) m += p.Mass; return m;
    }

    static bool AabbOverlap(PlacedItem a, PlacedItem b)
    {
        return Mathf.Abs(a.center.x - b.center.x) < a.halfSize.x + b.halfSize.x - 1e-4f
            && Mathf.Abs(a.center.y - b.center.y) < a.halfSize.y + b.halfSize.y - 1e-4f
            && Mathf.Abs(a.center.z - b.center.z) < a.halfSize.z + b.halfSize.z - 1e-4f;
    }

    // 두 화물의 XZ 평면 겹침 면적 (m²)
    static float XzOverlap(PlacedItem a, PlacedItem b)
    {
        float ox = Mathf.Min(a.center.x + a.halfSize.x, b.center.x + b.halfSize.x)
                 - Mathf.Max(a.center.x - a.halfSize.x, b.center.x - b.halfSize.x);
        float oz = Mathf.Min(a.center.z + a.halfSize.z, b.center.z + b.halfSize.z)
                 - Mathf.Max(a.center.z - a.halfSize.z, b.center.z - b.halfSize.z);
        return ox > 0f && oz > 0f ? ox * oz : 0f;
    }

    // 후보 바로 아래에서 받치는 화물들 (top ≈ cand.bottom)
    IEnumerable<PlacedItem> Supporters(IReadOnlyList<PlacedItem> placed, PlacedItem c)
    {
        foreach (var p in placed)
            if (Mathf.Abs(p.Top - c.Bottom) <= cfg.eps * 2f && XzOverlap(p, c) > 0f)
                yield return p;
    }

    // 밑면 지지율: 바닥이면 1.0, 아니면 받침 화물들의 XZ 겹침 면적 합 / 후보 밑면적
    float SupportRatio(IReadOnlyList<PlacedItem> placed, PlacedItem c)
    {
        if (c.Bottom <= cfg.floorTopY + cfg.eps) return 1f; // 바닥
        float footprint = (c.halfSize.x * 2f) * (c.halfSize.z * 2f);
        if (footprint <= 1e-9f) return 0f;
        float supported = 0f;
        foreach (var s in Supporters(placed, c)) supported += XzOverlap(s, c);
        return Mathf.Clamp01(supported / footprint);
    }

    // 질량가중 CoG (트레이 로컬). LoadCalculator와 동일 정의.
    static Vector3 Cog(IReadOnlyList<PlacedItem> placed, PlacedItem cand)
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { m += p.Mass; w += p.Mass * p.center; }
        m += cand.Mass; w += cand.Mass * cand.center;
        return m > 1e-6f ? w / m : Vector3.zero;
    }

}

/// <summary>규제 임계값 (인스펙터 튜닝용). 실측/문서 확정값 기본.</summary>
[System.Serializable]
public class RuleConfig
{
    [Header("적재함 (Unity 로컬 m) — 안쪽 61×21×27")]
    public float trayLateralM = 0.21f;   // x 좌우
    public float trayLengthM = 0.61f;    // z 주행/길이
    public float heightLimitM = 0.27f;   // y 적층 한도 (H13)
    public float floorTopY = 0.01f;

    [Header("규제 임계 (문서 확정)")]
    public float maxPayloadKg = 7f;          // H1
    public float supportRatioMin = 0.70f;    // H8 지지율 70%

    [Header("허용 오차")]
    public float eps = 0.005f;
}
