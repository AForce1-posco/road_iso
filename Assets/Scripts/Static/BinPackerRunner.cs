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

    [Header("실행")]
    public bool runOnStart = false;

    void Start() { if (runOnStart) Run(); }

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
