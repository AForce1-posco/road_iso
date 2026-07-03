using System.Collections.Generic;
using UnityEngine;

/// <summary>4점 로드셀 하중 결과 (kg). 로드셀은 질량 등가로 보고 kg로 보고한다.</summary>
public struct FourPointLoad
{
    public float FL, FR, RL, RR;

    public float Front => FL + FR;   // 전축 합
    public float Rear => RL + RR;    // 후축 합
    public float Total => FL + FR + RL + RR;
}

public enum RiskLevel { Safe, Caution, Danger }

/// <summary>위험도 판정 결과. Grade(0~2)는 정적 단계용, AI 라벨(0~3)은 다음 차수에서 확장.</summary>
public struct RiskResult
{
    public RiskLevel Level;
    public int Grade;
    public string Label; // "SAFE" / "CAUTION" / "DANGER"
}

/// <summary>LTR 임계값. 기본 &lt;0.6 SAFE, 0.6~0.85 CAUTION, &gt;0.85 DANGER.</summary>
[System.Serializable]
public struct RiskThresholds
{
    public float caution; // 이 값 이상이면 CAUTION
    public float danger;  // 이 값 초과면 DANGER

    public static RiskThresholds Default => new RiskThresholds { caution = 0.6f, danger = 0.85f };
}

/// <summary>
/// 적재 하중 계산기. MonoBehaviour가 아닌 순수 static 클래스 → 정적/동적 씬 공용.
/// 좌표계: x=폭(좌-/우+), z=길이(후-/전+), y=상.
/// </summary>
public static class LoadCalculator
{
    /// <summary>질량가중 무게중심(월드 좌표). 화물이 없으면 Vector3.zero.</summary>
    public static Vector3 ComputeCoG(IReadOnlyList<PlacedCargo> cargo)
    {
        return ComputeCoG(cargo, 0f, Vector3.zero);
    }

    /// <summary>
    /// 빈 적재함 질량(extraMass, extraCoG)을 함께 고려한 무게중심.
    /// extraMass &lt;= 0 이면 화물만 계산한다.
    /// </summary>
    public static Vector3 ComputeCoG(IReadOnlyList<PlacedCargo> cargo, float extraMass, Vector3 extraCoG)
    {
        float totalMass = Mathf.Max(0f, extraMass);
        Vector3 weighted = totalMass > 0f ? extraCoG * extraMass : Vector3.zero;

        if (cargo != null)
        {
            for (int i = 0; i < cargo.Count; i++)
            {
                PlacedCargo p = cargo[i];
                if (p == null || p.type == null) continue;
                float m = p.type.massKg;
                weighted += p.worldPos * m;
                totalMass += m;
            }
        }

        if (totalMass <= 0f) return Vector3.zero;
        return weighted / totalMass;
    }

    public static float ComputeTotalMass(IReadOnlyList<PlacedCargo> cargo, float extraMass = 0f)
    {
        float total = Mathf.Max(0f, extraMass);
        if (cargo != null)
        {
            for (int i = 0; i < cargo.Count; i++)
            {
                PlacedCargo p = cargo[i];
                if (p != null && p.type != null) total += p.type.massKg;
            }
        }
        return total;
    }

    /// <summary>
    /// 강체 평판 4점 반력(bilinear 분배). 지지점이 사각형을 이룬다고 가정.
    /// 중심 기준 정규화 u=Δx/a, v=Δz/b 로 코너별 R=(W/4)(1±u)(1±v).
    /// 음수(들림)는 0으로 클램프하므로 합이 W보다 작아질 수 있다.
    /// </summary>
    public static FourPointLoad ComputeLoads(Vector3 cog, float totalMass, SupportConfig supports)
    {
        FourPointLoad load = default;
        if (totalMass <= 0f) return load;

        Vector2 c = supports.Centroid;
        float a = supports.HalfTrack;
        float b = supports.HalfBase;

        float u = a > 1e-6f ? (cog.x - c.x) / a : 0f;
        float v = b > 1e-6f ? (cog.z - c.y) / b : 0f;

        float q = totalMass * 0.25f;
        // front = +z (v+), right = +x (u+)
        load.FL = Mathf.Max(0f, q * (1f - u) * (1f + v));
        load.FR = Mathf.Max(0f, q * (1f + u) * (1f + v));
        load.RL = Mathf.Max(0f, q * (1f - u) * (1f - v));
        load.RR = Mathf.Max(0f, q * (1f + u) * (1f - v));
        return load;
    }

    /// <summary>횡하중 전달비. (우측 - 좌측) / 총하중, 범위 -1~+1. 절대값이 클수록 좌우 쏠림.</summary>
    public static float ComputeLTR(FourPointLoad load)
    {
        float total = load.Total;
        if (total <= 1e-6f) return 0f;
        float ltr = ((load.FR + load.RR) - (load.FL + load.RL)) / total;
        return Mathf.Clamp(ltr, -1f, 1f); // 물리적으로 -1~+1 (바퀴 힘 튐 방어)
    }

    /// <summary>LTR 절대값으로 위험 등급 판정.</summary>
    public static RiskResult ComputeRisk(float ltr, RiskThresholds t)
    {
        float a = Mathf.Abs(ltr);
        if (a > t.danger)
            return new RiskResult { Level = RiskLevel.Danger, Grade = 2, Label = "DANGER" };
        if (a >= t.caution)
            return new RiskResult { Level = RiskLevel.Caution, Grade = 1, Label = "CAUTION" };
        return new RiskResult { Level = RiskLevel.Safe, Grade = 0, Label = "SAFE" };
    }

    // ── 지지영역(support polygon) 기반 전복 판정 ────────────────────────────

    /// <summary>4개 지지점을 시계방향 사각형 순서로 반환 (fl→fr→rr→rl).</summary>
    public static Vector2[] SupportPolygon(SupportConfig s)
    {
        return new[] { s.fl.position, s.fr.position, s.rr.position, s.rl.position };
    }

    /// <summary>
    /// CoG 수평 투영점(p)의 안정 여유(m). 지지다각형 안이면 +(가장 가까운 변까지 거리),
    /// 밖이면 −. 즉 값이 0 아래로 내려가면 전복.
    /// </summary>
    public static float StabilityMargin(Vector2 p, SupportConfig s)
    {
        Vector2[] poly = SupportPolygon(s);
        bool inside = PointInConvex(p, poly);
        float minDist = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Length];
            minDist = Mathf.Min(minDist, DistanceToSegment(p, a, b));
        }
        return inside ? minDist : -minDist;
    }

    private static bool PointInConvex(Vector2 p, Vector2[] poly)
    {
        bool hasPos = false, hasNeg = false;
        for (int i = 0; i < poly.Length; i++)
        {
            Vector2 a = poly[i];
            Vector2 b = poly[(i + 1) % poly.Length];
            float cross = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
            if (cross > 1e-7f) hasPos = true;
            else if (cross < -1e-7f) hasNeg = true;
            if (hasPos && hasNeg) return false;
        }
        return true;
    }

    /// <summary>
    /// 횡전복 임계 가속(SSF, 단위 g). = 반트랙폭 / CoG높이.
    /// 이 값(g) 이상의 횡가속이 붙으면 옆으로 전복. 클수록 안전. cogHeight&lt;=0이면 큰 값.
    /// </summary>
    public static float LateralTipG(float halfTrack, float cogHeight)
    {
        if (cogHeight <= 1e-4f) return 999f;
        return halfTrack / cogHeight;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 > 1e-9f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }
}
