using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// BinPacker 실행기 (에디터). 랜덤 manifest N개를 그리디 빈패커로 배치 → JSON 케이스 저장 + 통계 로그.
/// 빈 GameObject에 붙이고 인스펙터 우클릭 "Run BinPacker" (또는 runOnStart).
/// 출력: Assets/Data/Cases_binpack/binpackNNN.json (위험 Cases/ 와 분리 — 덮어쓰기 없음).
/// 설계: Docs/BinPacker_Design.md
/// </summary>
public class BinPackerRunner : MonoBehaviour
{
    [Header("패킹 모드 — Stable=안정성 최적(BC 교사) / Dense=고전 빈패킹(공간만)")]
    public BinPacker.PackMode packMode = BinPacker.PackMode.Stable;

    [Header("규제/보상 (RL과 동일하게 유지)")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();
    public int cols = 11;   // 2cm급 격자 (RL과 동일해야 함)
    public int rows = 31;

    [Header("생성 설정")]
    public int numCases = 100;
    public int manifestMin = 3;
    public int manifestMax = 5;
    [Tooltip("사용할 화물 종류(RL 풀과 동일 권장). 비우면 카탈로그 전체")]
    public string[] usableTypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006",
        "C-001","T-001","P-001","P-002","P-003","S-001"
    };
    [Tooltip("Assets/ 기준 하위 폴더")]
    public string outputSubdir = "Data/Cases_binpack";
    public int seed = 42;

    [Header("실행")]
    public bool runOnStart = false;

    void Start() { if (runOnStart) Run(); }

    [ContextMenu("Run BinPacker")]
    public void Run()
    {
        Random.InitState(seed);

        // 카탈로그 → 풀
        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;
        var pool = new List<CargoType>();
        if (usableTypeIds != null && usableTypeIds.Length > 0)
        {
            foreach (var id in usableTypeIds) if (cat.TryGetValue(id, out var t)) pool.Add(t);
        }
        else foreach (var t in cat.Values) pool.Add(t);
        if (pool.Count == 0) { Debug.LogError("[BinPacker] 사용 가능한 화물 풀이 비었음"); return; }

        var packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        var reward = new RewardCalculator(rewardConfig, ruleConfig);
        string dir = Path.Combine(Application.dataPath, outputSubdir);
        Directory.CreateDirectory(dir);

        int totItems = 0, totPlaced = 0, fullCases = 0;
        float sumReward = 0f;
        var reasonAgg = new Dictionary<string, int>();   // 미적재 사유 합계 (전 케이스)

        for (int i = 1; i <= numCases; i++)
        {
            int n = Random.Range(manifestMin, manifestMax + 1);
            var manifest = new List<CargoType>();
            for (int k = 0; k < n; k++) manifest.Add(pool[Random.Range(0, pool.Count)]);

            var unplaced = new List<CargoType>();
            var reasons = new List<BinPacker.UnplacedReason>();
            var placed = packer.Pack(manifest, unplaced, reasons);
            foreach (var r in reasons) { reasonAgg.TryGetValue(r.dominant, out int c); reasonAgg[r.dominant] = c + 1; }

            totItems += n;
            totPlaced += placed.Count;
            if (unplaced.Count == 0) fullCases++;

            if (placed.Count > 0)
            {
                var items = new List<RuleChecker.PlacedItem>();
                foreach (var p in placed)
                    items.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });
                sumReward += reward.Final(items).total;
            }

            WriteJson(dir, i, placed);
        }

        var rs = new System.Text.StringBuilder();
        foreach (var kv in reasonAgg) rs.Append($"{kv.Key}×{kv.Value} · ");
        Debug.Log($"[BinPacker] {numCases}케이스 생성 → {dir}\n" +
                  $"배치 성공률 {100f * totPlaced / Mathf.Max(1, totItems):F1}% ({totPlaced}/{totItems}) · " +
                  $"전량 적재 케이스 {fullCases}/{numCases} · 평균 최종보상 {sumReward / numCases:F3}\n" +
                  (reasonAgg.Count > 0 ? $"미적재 사유 합계: {rs.ToString().TrimEnd(' ', '·')}" : "미적재 없음"));
    }

    private void WriteJson(string dir, int idx, List<BinPacker.Placement> placed)
    {
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in placed)
            file.cargo.Add(new CargoLayoutEntry
            {
                type = p.type.name,
                localPos = p.center,
                localEuler = p.euler,
                secured = true,
            });
        File.WriteAllText(Path.Combine(dir, $"binpack{idx:D3}.json"), JsonUtility.ToJson(file, true));
    }
}
