using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// RewardCalculator 확인 러너. 좋은 배치 vs 나쁜 배치의 점수가 기대대로 벌어지는지 Console로 검증.
/// 사용: 빈 GameObject에 추가 → Play (또는 우클릭 → Run Reward Tests).
/// </summary>
public class RewardCalculatorTest : MonoBehaviour
{
    public RewardConfig rewardConfig = new RewardConfig();
    public bool runOnStart = true;

    private RewardCalculator rw;
    private Dictionary<string, CargoType> cat;

    void Start() { if (runOnStart) Run(); }

    [ContextMenu("Run Reward Tests")]
    public void Run()
    {
        rw = new RewardCalculator(rewardConfig);
        cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;

        // A) 좋은 배치: 무거운 큰 박스 중앙 바닥 + 가벼운 박스 그 위 중앙
        var good = L(
            Item("B-005", 0f, 0f, 0.01f),          // 무거움(0.785) 바닥 중앙
            Item("B-001", 0f, 0f, 0.11f));         // 가벼움(0.21) 위 중앙
        // B) 나쁜 배치: 가벼운 박스 바닥, 무거운 박스 위 + 한쪽 구석으로 편중
        var bad = L(
            Item("B-001", 0.08f, 0.25f, 0.01f),    // 가벼움 바닥, 구석
            Item("B-005", 0.08f, 0.25f, 0.05f));   // 무거움 위(무거운게 위=나쁨), 구석(CoG 편중)

        var rGood = rw.Final(good);
        var rBad = rw.Final(bad);

        Debug.Log($"[좋은 배치] {rGood}");
        Debug.Log($"[나쁜 배치] {rBad}");
        Debug.Log($"스텝보상: 좋음 {rw.Step(good):F3} / 나쁨 {rw.Step(bad):F3}");

        bool ok = rGood.total > rBad.total;
        Debug.Log($"{(ok ? "✅ PASS" : "❌ FAIL")} 좋은 배치 점수({rGood.total:F3}) {(ok ? ">" : "≤")} 나쁜 배치({rBad.total:F3})" +
                  $"  | CGS 좋음 {rGood.cgs:F2} vs 나쁨 {rBad.cgs:F2}, SS 좋음 {rGood.ss:F2} vs 나쁨 {rBad.ss:F2}");
    }

    private RuleChecker.PlacedItem Item(string id, float x, float z, float bottomY)
    {
        CargoType t = cat.TryGetValue(id, out var v) ? v : null;
        Vector3 half = (t != null ? t.sizeM : Vector3.one * 0.1f) * 0.5f;
        return new RuleChecker.PlacedItem { type = t, center = new Vector3(x, bottomY + half.y, z), halfSize = half };
    }
    private static List<RuleChecker.PlacedItem> L(params RuleChecker.PlacedItem[] items) => new List<RuleChecker.PlacedItem>(items);
}
