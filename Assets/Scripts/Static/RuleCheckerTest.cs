using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// RuleChecker 단위 테스트 러너. 시나리오별로 IsValid 결과가 기대와 맞는지 Console에 PASS/FAIL 출력.
/// 사용: 빈 GameObject에 이 컴포넌트 추가 → Play (또는 우클릭 → Run RuleChecker Tests).
/// 에이전트 없이 규제 판정만 검증하는 용도.
/// </summary>
public class RuleCheckerTest : MonoBehaviour
{
    public RuleConfig config = new RuleConfig();
    public bool runOnStart = true;

    private RuleChecker rc;
    private Dictionary<string, CargoType> cat;

    void Start() { if (runOnStart) Run(); }

    [ContextMenu("Run RuleChecker Tests")]
    public void Run()
    {
        rc = new RuleChecker(config);
        cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;

        int pass = 0, fail = 0;
        void Case(string name, bool expectValid, List<RuleChecker.PlacedItem> placed, RuleChecker.PlacedItem cand)
        {
            bool ok = rc.IsValid(placed, cand, out string reason);
            bool correct = ok == expectValid;
            var rep = rc.Evaluate(placed, cand);
            Debug.Log($"{(correct ? "✅ PASS" : "❌ FAIL")} [{name}] 기대={(expectValid ? "유효" : "불가")} 실제={(ok ? "유효" : "불가")}" +
                      $"{(ok ? "" : $" ({reason})")}  | 총질량 {rep.totalMassKg:F2}kg 지지율 {rep.supportRatio:P0} CoG({rep.cog.x:F3},{rep.cog.y:F3},{rep.cog.z:F3})");
            if (correct) pass++; else fail++;
        }

        var empty = new List<RuleChecker.PlacedItem>();

        // 1) 빈 트레이 중앙 박스 → 유효
        Case("중앙 박스", true, empty, Item("B-005", 0, 0, 0.01f));

        // 2) 같은 자리 겹침 → 불가(H3)
        Case("겹침", false, L(Item("B-005", 0, 0, 0.01f)), Item("B-005", 0, 0, 0.01f));

        // 3) 폭 초과(B-007 25cm > 21cm) → 불가(H2)
        Case("경계이탈", false, empty, Item("B-007", 0, 0, 0.01f));

        // 4) 3층 → 높이 0.31 > 0.28 → 불가(H13)
        Case("높이초과(3층)", false,
            L(Item("B-005", 0, 0, 0.01f), Item("B-005", 0, 0, 0.11f)),
            Item("B-005", 0, 0, 0.21f));

        // 5) 파이프 z축 바닥 → 유효
        Case("파이프 z축", true, empty, Item("S-001", 0, 0, 0.01f));

        // 6) 파이프 90° 회전(길이 x축) → 불가(H4/H2)
        Case("파이프 회전", false, empty, Item("S-001", 0, 0, 0.01f, yaw90: true));

        // 7) 포대 위 상자 → 불가(H7)
        Case("포대 위 상자", false, L(Item("T-001", 0, 0, 0.01f)), OnTop("B-001", "T-001", 0, 0));

        // 8) 포대 위 포대 → 유효(H10)
        Case("포대 위 포대", true, L(Item("T-001", 0, 0, 0.01f)), OnTop("T-001", "T-001", 0, 0));

        // 9) 지지율 부족(가장자리 걸침 50%) → 불가(H8)
        Case("지지율<70%", false, L(Item("B-005", 0, 0, 0.01f)), Item("B-001", 0.05f, 0, 0.11f));

        // 10) 과적(합 >7kg) → 불가(H1)  ※ H1이 먼저라 겹침 무관
        Case("과적", false,
            L(Item("S-003", 0, 0, 0.01f), Item("S-003", 0, 0, 0.01f),
              Item("S-003", 0, 0, 0.01f), Item("S-003", 0, 0, 0.01f)),
            Item("B-001", 0, 0, 0.01f));

        Debug.Log($"■ RuleChecker 테스트 완료: {pass}/{pass + fail} PASS" + (fail > 0 ? $"  ({fail} FAIL)" : " — 전부 통과"));
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    /// <summary>바닥/지정 높이에 화물 배치. bottomY=밑면 y. yaw90=90° 회전(x↔z 스왑).</summary>
    private RuleChecker.PlacedItem Item(string id, float x, float z, float bottomY, bool yaw90 = false)
    {
        CargoType t = cat.TryGetValue(id, out var v) ? v : null;
        Vector3 s = t != null ? t.sizeM : Vector3.one * 0.1f;
        Vector3 half = (yaw90 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;
        return new RuleChecker.PlacedItem { type = t, center = new Vector3(x, bottomY + half.y, z), halfSize = half };
    }

    /// <summary>supId 화물 위(그 top)에 얹는 화물.</summary>
    private RuleChecker.PlacedItem OnTop(string id, string supId, float x, float z)
    {
        var sup = Item(supId, x, z, 0.01f);
        return Item(id, x, z, sup.Top);
    }

    private static List<RuleChecker.PlacedItem> L(params RuleChecker.PlacedItem[] items) => new List<RuleChecker.PlacedItem>(items);
}
