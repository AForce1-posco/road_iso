using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 순수 3D BPP 실행기 (에디터, 화면 없이). 지정 manifest(인스펙터 (id,개수) 목록 또는 CSV)를 빈패킹으로 배치 →
/// JSON 레이아웃 1개 저장 + 통계 로그. 빈 GameObject에 붙이고 우클릭 "Run BinPacker (Pack manifest)".
/// 기본은 Dense(공간 꽉 채우기). 설계: Docs/BinPacker_Design.md
/// </summary>
public class BinPackerRunner : MonoBehaviour
{
    [Header("패킹 모드 — Dense=공간 꽉 채우기(순수 BPP) / Stable=안정성 우선")]
    public BinPacker.PackMode packMode = BinPacker.PackMode.Dense;

    [Header("규제/격자")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();
    public int cols = 11;   // 2cm급 격자
    public int rows = 31;

    [Header("적재 목록 (Manifest)")]
    [Tooltip("(화물 id, 개수) 목록. manifestCsv 가 지정되면 그쪽이 우선.")]
    public ManifestEntry[] manifest = {
        new ManifestEntry { typeId = "SYN-01", count = 5 },
        new ManifestEntry { typeId = "SYN-03", count = 8 },
    };
    [Tooltip("선택: Assets/ 기준 CSV 경로. 지정 시 위 목록 대신 CSV.")]
    public string manifestCsv = "";

    [Header("출력")]
    [Tooltip("Assets/ 기준 하위 폴더")]
    public string outputSubdir = "Data/Cases_binpack";
    public string outputName = "boxpack001";

    [Header("랜덤 배치 (자동 매니페스트)")]
    [Tooltip("우클릭 'Run Random Batch' 실행 시, 랜덤 매니페스트를 randomCount개 자동 생성해 각각 packMode로 팩→JSON(rand_<mode>_NNN.json). " +
             "같은 randomSeed로 Dense·Stable을 각각 돌리면 동일 매니페스트라 공정 비교됨.")]
    public int randomCount = 20;
    [Tooltip("매니페스트당 화물 개수 랜덤 범위")]
    public int minItems = 8;
    public int maxItems = 20;
    [Tooltip("박스류(B-*, SYN-*)만 사용")]
    public bool boxOnly = true;
    [Tooltip("재현용 시드. Dense/Stable 비교 시 같은 시드 → 같은 매니페스트 세트.")]
    public int randomSeed = 42;

    [Header("GRASP 멀티시드 — 같은 매니페스트를 시드별로 다양 배치 → 예측기로 best 선택")]
    [Tooltip("생성할 시드(=배치 후보) 개수")]
    public int graspSeeds = 12;
    [Tooltip("RCL 폭. 0=최고점만(≈결정론), 클수록 다양성↑ (0.2~0.4 권장)")]
    [Range(0f, 1f)] public float graspAlpha = 0.3f;
    [Tooltip("순서 랜덤화 폭. 정렬 상위 K개 중 랜덤으로 다음 화물 선택")]
    public int graspOrderK = 2;

    [Header("실행")]
    public bool runOnStart = false;

    void Start() { if (runOnStart) Run(); }

    [ContextMenu("Run GRASP Batch (멀티시드 → 후보 N개 + det baseline export)")]
    public void RunGraspBatch()
    {
        var manifestList = CargoManifest.Resolve(manifest, manifestCsv, out string src);
        if (manifestList.Count == 0) { Debug.LogError($"[GRASP] manifest 비었음 (source={src})"); return; }
        string dir = Path.Combine(Application.dataPath, outputSubdir);
        Directory.CreateDirectory(dir);

        // 결정론 baseline (기존 Pack) — GRASP best와 비교 기준
        var det = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        WriteJson(Path.Combine(dir, $"grasp_{outputName}_det.json"), det.Pack(manifestList));

        int total = 0, fullCount = 0;
        for (int seed = 0; seed < graspSeeds; seed++)
        {
            var packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
            var unplaced = new List<CargoType>();
            var placed = packer.PackGrasp(manifestList, new System.Random(seed), graspAlpha, graspOrderK, unplaced);
            WriteJson(Path.Combine(dir, $"grasp_{outputName}_{seed:D2}.json"), placed);
            total += placed.Count;
            if (unplaced.Count == 0) fullCount++;
        }
        Debug.Log($"[GRASP:{packMode}] {graspSeeds}시드 (alpha={graspAlpha}, orderK={graspOrderK}, src={src}) " +
                  $"→ grasp_{outputName}_*.json (+det). 완주 {fullCount}/{graspSeeds}, 배치합계 {total} → {dir}\n" +
                  $"다음: python Assets/Data/Results/grasp_pick.py grasp_{outputName}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    [ContextMenu("Run Random Batch (자동 랜덤 매니페스트 N개 팩)")]
    public void RunRandomBatch()
    {
        // 박스 카탈로그 (boxOnly면 B-*, SYN-*만)
        var boxes = new List<CargoType>();
        foreach (var t in CargoCatalog.CreateDefault())
            if (t != null && (!boxOnly || t.id.StartsWith("B-") || t.id.StartsWith("SYN-")))
                boxes.Add(t);
        if (boxes.Count == 0) { Debug.LogError("[BinPacker] 박스 카탈로그 비었음"); return; }

        var rng = new System.Random(randomSeed);
        var packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        string dir = Path.Combine(Application.dataPath, outputSubdir);
        Directory.CreateDirectory(dir);

        int totalPlaced = 0, totalUnplaced = 0;
        for (int i = 1; i <= randomCount; i++)
        {
            int total = rng.Next(minItems, maxItems + 1);
            var manifestList = new List<CargoType>();
            var remaining = new Dictionary<string, int>();
            foreach (var t in boxes) remaining[t.id] = t.stockCount;
            for (int k = 0; k < total; k++)
            {
                var avail = boxes.FindAll(t => remaining[t.id] > 0);
                if (avail.Count == 0) break;
                var pick = avail[rng.Next(avail.Count)];
                manifestList.Add(pick);
                remaining[pick.id]--;
            }

            var unplaced = new List<CargoType>();
            var placed = packer.Pack(manifestList, unplaced);
            WriteJson(Path.Combine(dir, $"rand_{packMode}_{i:D3}.json"), placed);
            totalPlaced += placed.Count; totalUnplaced += unplaced.Count;
        }
        Debug.Log($"[BinPacker:{packMode}] 랜덤 배치 {randomCount}개 (seed={randomSeed}, {minItems}~{maxItems}개, boxOnly={boxOnly}) " +
                  $"→ 총 배치 {totalPlaced}, 미적재 {totalUnplaced} → {dir}/rand_{packMode}_*.json");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    [ContextMenu("Run BinPacker (Pack manifest)")]
    public void Run()
    {
        var manifestList = CargoManifest.Resolve(manifest, manifestCsv, out string src);
        if (manifestList.Count == 0) { Debug.LogError($"[BinPacker] manifest 비었음 (source={src})"); return; }

        var packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        var reward = new RewardCalculator(rewardConfig, ruleConfig);

        var unplaced = new List<CargoType>();
        var reasons = new List<BinPacker.UnplacedReason>();
        var placed = packer.Pack(manifestList, unplaced, reasons);

        string dir = Path.Combine(Application.dataPath, outputSubdir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, outputName + ".json");
        WriteJson(path, placed);

        // 통계: 부피 점유율(순수 BPP 핵심) + Final + 미적재 사유
        float trayVol = ruleConfig.trayLateralM * ruleConfig.trayLengthM * ruleConfig.heightLimitM;
        float used = 0f;
        var items = new List<RuleChecker.PlacedItem>();
        foreach (var p in placed)
        {
            used += (p.halfSize.x * 2f) * (p.halfSize.y * 2f) * (p.halfSize.z * 2f);
            items.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });
        }
        float finalR = items.Count > 0 ? reward.Final(items).total : 0f;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[BinPacker:{packMode}] manifest {manifestList.Count}개(src={src}) → 배치 {placed.Count} · " +
                  $"부피점유 {100f * used / Mathf.Max(1e-9f, trayVol):F1}% · Final {finalR:F3} → {path}");
        if (unplaced.Count > 0)
        {
            var byReason = new Dictionary<string, int>();
            foreach (var ur in reasons) { byReason.TryGetValue(ur.dominant, out int c); byReason[ur.dominant] = c + 1; }
            sb.Append($"\n미적재 {unplaced.Count} → ");
            bool first = true;
            foreach (var kv in byReason) { if (!first) sb.Append(" · "); sb.Append($"{kv.Key}×{kv.Value}"); first = false; }
        }
        Debug.Log(sb.ToString());
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    private void WriteJson(string path, List<BinPacker.Placement> placed)
    {
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in placed)
            file.cargo.Add(new CargoLayoutEntry { type = p.type.name, localPos = p.center, localEuler = p.euler, secured = true });
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
    }
}
