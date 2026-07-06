using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// BinPacker 배치를 화면에 그려주는 표시 전용 시각화 (PlacementVisualizer의 빈패커판).
/// 사용: 아무 씬(빈 씬 권장)에 빈 GameObject 만들고 이 컴포넌트만 추가 → Play
///       → 랜덤 manifest를 빈패커로 배치해 트레이·격자·27cm한도·CoG·실물 화물 모양으로 표시.
///       인스펙터 우클릭 "Repack (새 manifest)" 로 다음 배치를 계속 넘겨봄.
/// RL 쪽(PlacementAgent/PlacementVisualizer)은 전혀 건드리지 않음 — 독립 도구.
/// 설계: Docs/BinPacker_Design.md
/// </summary>
public class BinPackerVisualizer : MonoBehaviour
{
    [Header("패킹 모드 — Stable=안정성 최적(BC 교사) / Dense=고전 빈패킹(공간만)")]
    public BinPacker.PackMode packMode = BinPacker.PackMode.Stable;

    [Header("규제/보상 (RL과 동일하게 유지)")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();
    [Tooltip("격자 — 2cm급 = 11×31 (RL과 동일해야 BC 정합)")]
    public int cols = 11;
    public int rows = 31;

    [Header("Manifest (실을 화물)")]
    public int manifestMin = 8;
    public int manifestMax = 12;
    [Tooltip("사용할 화물 종류(RL 풀과 동일 권장). 비우면 카탈로그 전체")]
    public string[] usableTypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006",
        "C-001","T-001","P-001","P-002","P-003","S-001"
    };
    [Tooltip("0이면 매번 다른 랜덤, 아니면 재현 가능한 시드")]
    public int seed = 0;

    [Header("분포 진단 (Diagnose Manifest Distribution)")]
    [Tooltip("PlacementAgent 의 pipeWidthBudget 과 동일하게 유지 — 학습 분포를 그대로 재현하려면 필수")]
    [Range(0.3f, 1f)] public float pipeWidthBudget = 0.7f;
    [Tooltip("진단 시 샘플링할 manifest 개수 (많을수록 안정)")]
    public int diagSamples = 5000;

    [Header("A안: 게이팅 풀 생성 (Generate Gated Manifest Pool)")]
    [Tooltip("생성할 '교사 완주 가능' manifest 개수 (PlacementAgent 가 리셋마다 여기서 뽑음)")]
    public int poolSize = 5000;
    [Tooltip("Assets/Data/ 하위 저장 파일명 — PlacementAgent.gatedPoolFileName 과 일치시킬 것")]
    public string poolFileName = "gated_manifests.txt";

    [Header("표시")]
    [Tooltip("표시 배율 (트레이가 21×61cm로 작아서 키워서 봄)")]
    public float displayScale = 10f;
    public bool autoCamera = true;
    [Tooltip("격자선 표시 (1cm 격자는 빽빽함 — 거슬리면 끄기)")]
    public bool showGrid = true;
    public bool showCoG = true;

    private Transform root, cargoRoot;
    private Camera vizCam;
    private GameObject cogBall, cogStem;
    private BinPacker packer;
    private RewardCalculator reward;
    private List<CargoType> pool;

    void Start()
    {
        if (seed != 0) Random.InitState(seed);

        packer = new BinPacker(ruleConfig, rewardConfig, cols, rows);
        reward = new RewardCalculator(rewardConfig, ruleConfig);
        pool = LoadPool();
        if (pool.Count == 0) { Debug.LogError("[BinPackerViz] 사용 가능한 화물 풀이 비었음"); return; }

        var go = new GameObject("BinPackerViz_Root");
        root = go.transform;
        root.SetParent(transform, false);
        root.localScale = Vector3.one * displayScale;

        cargoRoot = new GameObject("Cargo").transform;
        cargoRoot.SetParent(root, false);

        BuildTray();
        if (autoCamera) SetupCamera();
        BuildCornerLabels();
        if (showCoG) BuildCoGMarker();

        PackAndShow();
    }

    private List<CargoType> LoadPool()
    {
        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;
        var list = new List<CargoType>();
        if (usableTypeIds != null && usableTypeIds.Length > 0)
        {
            foreach (var id in usableTypeIds) if (cat.TryGetValue(id, out var t)) list.Add(t);
        }
        else foreach (var t in cat.Values) list.Add(t);
        return list;
    }

    [ContextMenu("Repack (새 manifest)")]
    public void Repack()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[BinPackerViz] Play 모드에서 실행하세요"); return; }
        PackAndShow();
    }

    // ── A안: 게이팅 manifest 풀 생성 ──────────────────────────────────────────
    // '교사(빈패커) 완주 가능' manifest 만 골라 Assets/Data/<poolFileName> 에 저장 → PlacementAgent 가 리셋마다 뽑음.
    // 리셋당 Pack 0회(timeout 없음) + 데모(+0.867)와 동일한 게이팅 분포 → BC 정합.
    // ⚠️ manifestMin/Max = 학습과 동일한 3~5 로 맞춘 뒤 실행. Play 안 눌러도 됨. 인스펙터 우클릭.
    [ContextMenu("Generate Gated Manifest Pool")]
    public void GenerateGatedManifestPool()
    {
        if (seed != 0) Random.InitState(seed);
        var genPool = LoadPool();
        if (genPool.Count == 0) { Debug.LogError("[게이팅풀] 사용 가능한 화물 풀이 비었음"); return; }

        var packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        var lines = new List<string>(poolSize);
        int attempts = 0, maxAttempts = Mathf.Max(poolSize * 500, 100000);

        while (lines.Count < poolSize && attempts < maxAttempts)
        {
            attempts++;
            int n = Random.Range(manifestMin, manifestMax + 1);
            var manifest = new List<CargoType>(n);
            for (int k = 0; k < n; k++) manifest.Add(genPool[Random.Range(0, genPool.Count)]);

            if (!ManifestRealistic(manifest)) continue;             // ② 현실성
            var unplaced = new List<CargoType>();
            packer.Pack(manifest, unplaced);
            if (unplaced.Count != 0) continue;                      // 교사 완주 불가 → 버림

            var ids = new string[manifest.Count];
            for (int k = 0; k < manifest.Count; k++) ids[k] = manifest[k].id;
            lines.Add(string.Join(",", ids));
        }

        string dir = Path.Combine(Application.dataPath, "Data");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, poolFileName);
        File.WriteAllLines(path, lines);

        string warn = (manifestMin != 3 || manifestMax != 5)
            ? $"\n⚠️ manifest {manifestMin}~{manifestMax} 는 학습(3~5)과 다름! 3/5 로 맞춰 다시 생성하세요."
            : "";
        Debug.Log($"[게이팅풀] {lines.Count}/{poolSize}개 생성 (시도 {attempts}회, 채택률 {100f * lines.Count / Mathf.Max(1, attempts):F1}%) → {path}{warn}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    // ── 분포 진단 ────────────────────────────────────────────────────────────
    // 학습(게이팅 OFF)의 manifest 분포에서 "교사조차 못 이기는 에피소드"가 얼마나 되는지 측정.
    // PlacementAgent.OnEpisodeBegin 의 샘플링(ManifestRealistic ②, 게이팅 없음)을 그대로 재현해
    //   ① ManifestRealistic 통과율, ② 통과분포에서 교사 완주율 p,
    //   ③ 에이전트 천장 추정 = p·(완주 시 Final) + (1−p)·(−1.5 fail-out),
    //   ④ 비교용 raw(필터 없음) 완주율, ⑤ 미완주 지배 사유 를 출력.
    // ⚠️ manifestMin/Max 를 학습과 동일한 3~5 로 맞춰야 유효하다(이 씬 기본은 시각화용 8~50).
    // 표시(Start)와 무관하게 자체 packer/pool 을 만들어 계산하므로 Play 안 눌러도 됨.
    // 인스펙터 우클릭 "Diagnose Manifest Distribution".
    [ContextMenu("Diagnose Manifest Distribution")]
    public void DiagnoseManifestDistribution()
    {
        if (seed != 0) Random.InitState(seed);
        var diagPool = LoadPool();
        if (diagPool.Count == 0) { Debug.LogError("[분포진단] 사용 가능한 화물 풀이 비었음"); return; }

        var diagPacker = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = packMode };
        var diagReward = new RewardCalculator(rewardConfig, ruleConfig);

        int realisticPass = 0;      // ManifestRealistic 통과 수
        int fullGated = 0;          // 통과분포 중 교사 완주 수
        float sumFinalFull = 0f;    // 완주 시 Final 보상 합
        float sumAgentCeil = 0f;    // 에이전트 천장 추정 합 (완주=Final, 미완주=−1.5)
        int rawFull = 0;            // 비교용: 필터 없이 교사 완주 수
        var reasonAgg = new Dictionary<string, int>();  // 미완주 지배 사유(통과분포 기준)

        const float FAIL_OUT = -1.5f;   // PlacementAgent.Fail: 20×(−0.05) + (−0.5)

        for (int i = 0; i < diagSamples; i++)
        {
            int n = Random.Range(manifestMin, manifestMax + 1);
            var manifest = new List<CargoType>(n);
            for (int k = 0; k < n; k++) manifest.Add(diagPool[Random.Range(0, diagPool.Count)]);

            // 비교용 raw (필터 없음)
            var upRaw = new List<CargoType>();
            diagPacker.Pack(manifest, upRaw);
            if (upRaw.Count == 0) rawFull++;

            // ② ManifestRealistic 필터 (학습과 동일)
            if (!ManifestRealistic(manifest)) continue;
            realisticPass++;

            var unplaced = new List<CargoType>();
            var reasons = new List<BinPacker.UnplacedReason>();
            var placed = diagPacker.Pack(manifest, unplaced, reasons);

            if (unplaced.Count == 0)
            {
                fullGated++;
                float rf = FinalReward(diagReward, placed);
                sumFinalFull += rf;
                sumAgentCeil += rf;          // 완주 → 에이전트도 이 보상 도달 가능
            }
            else
            {
                foreach (var rr in reasons) { reasonAgg.TryGetValue(rr.dominant, out int c); reasonAgg[rr.dominant] = c + 1; }
                sumAgentCeil += FAIL_OUT;    // 미완주 → 에이전트는 fail-out 바닥
            }
        }

        float realisticRate = 100f * realisticPass / Mathf.Max(1, diagSamples);
        float pFull = realisticPass > 0 ? 100f * fullGated / realisticPass : 0f;
        float meanFinalFull = fullGated > 0 ? sumFinalFull / fullGated : 0f;
        float agentCeil = realisticPass > 0 ? sumAgentCeil / realisticPass : 0f;
        float rawRate = 100f * rawFull / Mathf.Max(1, diagSamples);

        var rs = new System.Text.StringBuilder();
        foreach (var kv in reasonAgg) rs.Append($"{kv.Key}×{kv.Value} · ");

        string warn = (manifestMin != 3 || manifestMax != 5)
            ? $"\n⚠️ 주의: manifest {manifestMin}~{manifestMax} 는 학습(3~5)과 다름! 3/5 로 맞춘 뒤 다시 돌리세요."
            : "";

        Debug.Log(
            $"[분포진단] 샘플 {diagSamples}개 (manifest {manifestMin}~{manifestMax}, 풀 {diagPool.Count}종, mode={packMode})\n" +
            $"① ManifestRealistic 통과율: {realisticRate:F1}% ({realisticPass}/{diagSamples})\n" +
            $"② 통과분포에서 교사 완주율 p: {pFull:F1}% ({fullGated}/{realisticPass}) — 완주 시 평균 Final {meanFinalFull:F3}\n" +
            $"③ ★에이전트 천장 추정: {agentCeil:F3}  = p·(Final) + (1−p)·(−1.5)\n" +
            $"④ (비교) 필터 없는 raw 완주율: {rawRate:F1}%\n" +
            $"⑤ 미완주 지배 사유: {(reasonAgg.Count > 0 ? rs.ToString().TrimEnd(' ', '·') : "없음(전부 완주)")}\n" +
            $"해석: ③이 −1.5 근처면 = 못 이길 문제가 많아 천장이 낮음 → A안(게이팅) 타당. " +
            $"③이 +0.5~0.7 근처면 = 문제는 멀쩡, 현재 −1.5는 1281셀 미학습 → A안 무의미(다른 처방)." +
            warn
        );
    }

    /// <summary>PlacementAgent.ManifestRealistic 과 동일한 ② 현실성 제약(질량합·파이프폭합).</summary>
    private bool ManifestRealistic(List<CargoType> m)
    {
        float mass = 0f, pipeWidth = 0f;
        foreach (var t in m)
        {
            if (t == null) continue;
            mass += t.massKg;
            if (t.shape == CargoShape.Pipe) pipeWidth += t.sizeM.x;
        }
        if (mass > ruleConfig.maxPayloadKg + 1e-4f) return false;
        if (pipeWidth > ruleConfig.trayLateralM * pipeWidthBudget + 1e-4f) return false;
        return true;
    }

    private float FinalReward(RewardCalculator rc, List<BinPacker.Placement> placed)
    {
        if (placed == null || placed.Count == 0) return 0f;
        var items = new List<RuleChecker.PlacedItem>(placed.Count);
        foreach (var p in placed)
            items.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });
        return rc.Final(items).total;
    }

    // ── 패킹 실행 + 화물 그리기 ─────────────────────────────────
    private void PackAndShow()
    {
        packer.mode = packMode;   // 인스펙터에서 바꾸고 Repack하면 즉시 반영

        // 이전 화물 지우기
        for (int i = cargoRoot.childCount - 1; i >= 0; i--) Destroy(cargoRoot.GetChild(i).gameObject);

        // 랜덤 manifest
        int n = Random.Range(manifestMin, manifestMax + 1);
        var manifest = new List<CargoType>();
        for (int k = 0; k < n; k++) manifest.Add(pool[Random.Range(0, pool.Count)]);

        var unplaced = new List<CargoType>();
        var reasons = new List<BinPacker.UnplacedReason>();
        var placed = packer.Pack(manifest, unplaced, reasons);

        // 그리기 (PlacementVisualizer와 동일: wrap 회전 + CargoFactory 실물 모양)
        var items = new List<RuleChecker.PlacedItem>();
        foreach (var p in placed)
        {
            var wrap = new GameObject("cargo_" + p.type.id);
            wrap.transform.SetParent(cargoRoot, false);
            wrap.transform.localPosition = p.center;
            wrap.transform.localRotation = p.rot == 1 ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;

            var mesh = CargoFactory.Create(p.type, 1f, Color.white);
            foreach (var c in mesh.GetComponentsInChildren<Collider>()) Destroy(c);
            mesh.transform.SetParent(wrap.transform, false);

            items.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });
        }

        if (showCoG) UpdateCoG(items);

        // 통계 로그
        var r = items.Count > 0 ? reward.Final(items) : default;
        var sb = new StringBuilder();
        sb.Append($"[BinPackerViz:{packMode}] manifest {n}개 → 배치 {placed.Count}개");
        if (unplaced.Count > 0)
        {
            // 사유별 집계 (예: "미적재 41 → H1 과적(>7kg)×39 · H3 화물 겹침×2")
            var byReason = new Dictionary<string, int>();
            foreach (var ur in reasons) { byReason.TryGetValue(ur.dominant, out int c); byReason[ur.dominant] = c + 1; }
            sb.Append($" (미적재 {unplaced.Count} → ");
            bool first = true;
            foreach (var kv in byReason)
            {
                if (!first) sb.Append(" · ");
                sb.Append($"{kv.Key}×{kv.Value}");
                first = false;
            }
            sb.Append(")");
        }
        if (items.Count > 0) sb.Append($" | {r}");
        Debug.Log(sb.ToString());
    }

    // ── 적재함 (바닥 + 외곽 + 격자 + 27cm 한도) ─────────────────
    private void BuildTray()
    {
        float lx = ruleConfig.trayLateralM, lz = ruleConfig.trayLengthM;
        float hy = ruleConfig.heightLimitM, fy = ruleConfig.floorTopY;
        float hx = lx * 0.5f, hz = lz * 0.5f;

        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "TrayFloor";
        Destroy(floor.GetComponent<Collider>());
        floor.transform.SetParent(root, false);
        floor.transform.localPosition = new Vector3(0f, fy - 0.003f, 0f);
        floor.transform.localScale = new Vector3(lx, 0.006f, lz);
        floor.GetComponent<MeshRenderer>().sharedMaterial =
            CargoFactory.MakePBR(new Color(0.15f, 0.16f, 0.20f), 0.1f, 0.2f);

        // 외곽 와이어 박스
        var box = new List<Vector3>();
        Vector3[] c8 =
        {
            new Vector3(-hx, fy, -hz), new Vector3(hx, fy, -hz), new Vector3(hx, fy, hz), new Vector3(-hx, fy, hz),
            new Vector3(-hx, fy + hy, -hz), new Vector3(hx, fy + hy, -hz), new Vector3(hx, fy + hy, hz), new Vector3(-hx, fy + hy, hz),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        for (int i = 0; i < 12; i++) { box.Add(c8[edges[i, 0]]); box.Add(c8[edges[i, 1]]); }
        MakeLines("TrayOutline", box, new Color(0.4f, 0.85f, 1f, 1f));

        if (showGrid)
        {
            var g = new List<Vector3>();
            float gy = fy + 0.001f;
            for (int c = 0; c <= cols; c++)
            {
                float x = -hx + c * lx / cols;
                g.Add(new Vector3(x, gy, -hz)); g.Add(new Vector3(x, gy, hz));
            }
            for (int r = 0; r <= rows; r++)
            {
                float z = -hz + r * lz / rows;
                g.Add(new Vector3(-hx, gy, z)); g.Add(new Vector3(hx, gy, z));
            }
            MakeLines("TrayGrid", g, new Color(1f, 1f, 1f, 0.25f));
        }

        // 27cm 한도: 반투명 빨간 천장 + 상단 테두리
        float topY = fy + hy;
        var ceil = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceil.name = "HeightLimitPlane";
        Destroy(ceil.GetComponent<Collider>());
        ceil.transform.SetParent(root, false);
        ceil.transform.localPosition = new Vector3(0f, topY, 0f);
        ceil.transform.localScale = new Vector3(lx, 0.002f, lz);
        ceil.GetComponent<MeshRenderer>().sharedMaterial = MakeTransparent(new Color(0.95f, 0.2f, 0.2f, 0.22f));

        var barMat = CargoFactory.MakePBR(new Color(0.95f, 0.15f, 0.15f), 0.2f, 0.5f);
        Vector3[] c4 =
        {
            new Vector3(-hx, topY, -hz), new Vector3(hx, topY, -hz),
            new Vector3(hx, topY, hz),  new Vector3(-hx, topY, hz),
        };
        for (int i = 0; i < 4; i++) MakeBar("HeightLimitEdge" + i, c4[i], c4[(i + 1) % 4], 0.006f, barMat);
    }

    private void BuildCornerLabels()
    {
        float hx = ruleConfig.trayLateralM * 0.5f, hz = ruleConfig.trayLengthM * 0.5f;
        float y = ruleConfig.floorTopY + 0.008f;
        Color front = new Color(1f, 0.55f, 0.25f);
        Color rear = new Color(0.35f, 0.8f, 1f);
        MakeLabel("FL", new Vector3(-hx, y, hz), front);
        MakeLabel("FR", new Vector3(hx, y, hz), front);
        MakeLabel("RL", new Vector3(-hx, y, -hz), rear);
        MakeLabel("RR", new Vector3(hx, y, -hz), rear);
    }

    private void MakeLabel(string text, Vector3 pos, Color c)
    {
        var go = new GameObject("Label_" + text);
        go.transform.SetParent(root, false);
        go.transform.localPosition = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.fontSize = 64;
        tm.characterSize = 0.02f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = c;
        go.AddComponent<LabelBillboard>().cam = vizCam;
    }

    private void MakeBar(string name, Vector3 a, Vector3 b, float th, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(root, false);
        Vector3 dir = b - a;
        float len = dir.magnitude;
        go.transform.localPosition = (a + b) * 0.5f;
        go.transform.localRotation = len > 1e-6f ? Quaternion.FromToRotation(Vector3.right, dir.normalized)
                                                 : Quaternion.identity;
        go.transform.localScale = new Vector3(len, th, th);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private GameObject MakeLines(string name, List<Vector3> segs, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        var mesh = new Mesh { name = name };
        mesh.SetVertices(segs);
        int[] idx = new int[segs.Count];
        for (int i = 0; i < idx.Length; i++) idx[i] = i;
        mesh.SetIndices(idx, MeshTopology.Lines, 0);
        mf.sharedMesh = mesh;
        mr.sharedMaterial = MakeUnlit(c);
        return go;
    }

    // ── 무게중심 ────────────────────────────────────────────────
    private void BuildCoGMarker()
    {
        cogBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cogBall.name = "CoG";
        Destroy(cogBall.GetComponent<Collider>());
        cogBall.transform.SetParent(root, false);
        cogBall.transform.localScale = Vector3.one * 0.02f;
        cogBall.GetComponent<MeshRenderer>().sharedMaterial =
            CargoFactory.MakePBR(new Color(0.95f, 0.15f, 0.15f), 0.3f, 0.6f);

        cogStem = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cogStem.name = "CoGStem";
        Destroy(cogStem.GetComponent<Collider>());
        cogStem.transform.SetParent(root, false);
        cogStem.GetComponent<MeshRenderer>().sharedMaterial =
            CargoFactory.MakePBR(new Color(0.95f, 0.3f, 0.3f), 0f, 0.3f);

        cogBall.SetActive(false); cogStem.SetActive(false);
    }

    private void UpdateCoG(List<RuleChecker.PlacedItem> items)
    {
        if (cogBall == null) return;
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in items) { m += p.Mass; w += p.Mass * p.center; }
        bool has = m > 1e-6f;
        cogBall.SetActive(has); cogStem.SetActive(has);
        if (!has) return;

        Vector3 cog = w / m;
        cogBall.transform.localPosition = cog;
        float fy = ruleConfig.floorTopY;
        float h = Mathf.Max(0.001f, cog.y - fy);
        cogStem.transform.localPosition = new Vector3(cog.x, fy + h * 0.5f, cog.z);
        cogStem.transform.localScale = new Vector3(0.004f, h, 0.004f);
    }

    // ── 카메라 / 재질 ──────────────────────────────────────────
    private void SetupCamera()
    {
        var camGo = new GameObject("BinPackerViz_Camera");
        camGo.transform.SetParent(root, false);
        var cam = camGo.AddComponent<Camera>();
        vizCam = cam;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        cam.depth = 10;
        cam.nearClipPlane = 0.01f;

        float lenZ = ruleConfig.trayLengthM;
        Vector3 eye = new Vector3(lenZ * 0.9f, lenZ * 0.9f, -lenZ);
        camGo.transform.localPosition = eye;
        Vector3 look = new Vector3(0f, ruleConfig.floorTopY + 0.05f, 0f);
        camGo.transform.localRotation = Quaternion.LookRotation((look - eye).normalized, Vector3.up);
    }

    private static Material MakeUnlit(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    private static Material MakeTransparent(Color c)
    {
        int srcA = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
        int oneMinusA = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
        var s = new Material(Shader.Find("Standard"));
        s.SetFloat("_Mode", 3f);
        s.SetInt("_SrcBlend", srcA);
        s.SetInt("_DstBlend", oneMinusA);
        s.SetInt("_ZWrite", 0);
        s.DisableKeyword("_ALPHATEST_ON");
        s.EnableKeyword("_ALPHABLEND_ON");
        s.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        s.renderQueue = 3000;
        s.SetColor("_Color", c);
        return s;
    }
}
