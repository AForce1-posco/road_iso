using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 순수 3D BPP 시각화 도구. 지정한 manifest(인스펙터 (id,개수) 목록 또는 CSV)를 빈패킹으로 배치해
/// 트레이·격자·높이한도·CoG·실물 화물 모양으로 표시. 우클릭 "Repack"으로 다시, "Save Layout JSON"으로 저장.
/// 기본은 Dense(공간 꽉 채우기). RL(PlacementAgent)과 독립. 설계: Docs/BinPacker_Design.md
/// </summary>
public class BinPackerVisualizer : MonoBehaviour
{
    [Header("패킹 모드 — Dense=공간 꽉 채우기(순수 BPP) / Stable=안정성 우선")]
    public BinPacker.PackMode packMode = BinPacker.PackMode.Dense;

    [Header("규제/격자")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();
    [Tooltip("격자 (x=cols, z=rows). 2cm급 = 11×31")]
    public int cols = 11;
    public int rows = 31;

    [Header("적재 목록 (Manifest)")]
    [Tooltip("(화물 id, 개수) 목록. manifestCsv 가 지정되면 그쪽이 우선.")]
    public ManifestEntry[] manifest = {
        new ManifestEntry { typeId = "SYN-01", count = 5 },
        new ManifestEntry { typeId = "SYN-02", count = 4 },
        new ManifestEntry { typeId = "SYN-03", count = 6 },
    };
    [Tooltip("선택: Assets/ 기준 CSV 경로(예: Data/manifest.csv). 한 줄에 'id,개수'. 지정 시 위 목록 대신 이걸 씀.")]
    public string manifestCsv = "";

    [Header("저장 (Save Layout JSON)")]
    [Tooltip("Assets/ 기준 하위 폴더")]
    public string outputSubdir = "Data/Cases_binpack";
    public string outputName = "boxpack001";

    [Header("표시")]
    public float displayScale = 10f;
    public bool autoCamera = true;
    public bool showGrid = true;
    public bool showCoG = true;

    private Transform root, cargoRoot;
    private Camera vizCam;
    private GameObject cogBall, cogStem;
    private BinPacker packer;
    private RewardCalculator reward;
    private List<BinPacker.Placement> lastPlaced = new List<BinPacker.Placement>();

    void Start()
    {
        packer = new BinPacker(ruleConfig, rewardConfig, cols, rows);
        reward = new RewardCalculator(rewardConfig, ruleConfig);

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

    [ContextMenu("Repack (manifest 다시)")]
    public void Repack()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[BinPackerViz] Play 모드에서 실행하세요"); return; }
        PackAndShow();
    }

    [ContextMenu("Save Layout JSON")]
    public void SaveLayoutJson()
    {
        if (lastPlaced == null || lastPlaced.Count == 0) { Debug.LogWarning("[BinPackerViz] 저장할 배치가 없음 (먼저 Play/Repack)"); return; }
        string dir = Path.Combine(Application.dataPath, outputSubdir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, outputName + ".json");
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in lastPlaced)
            file.cargo.Add(new CargoLayoutEntry { type = p.type.name, localPos = p.center, localEuler = p.euler, secured = true });
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
        Debug.Log($"[BinPackerViz] 레이아웃 저장: {lastPlaced.Count}개 → {path}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    // ── 패킹 실행 + 화물 그리기 ─────────────────────────────────
    private void PackAndShow()
    {
        packer.mode = packMode;   // 인스펙터에서 바꾸고 Repack하면 즉시 반영

        for (int i = cargoRoot.childCount - 1; i >= 0; i--) Destroy(cargoRoot.GetChild(i).gameObject);

        var manifestList = CargoManifest.Resolve(manifest, manifestCsv, out string src);
        if (manifestList.Count == 0) { Debug.LogWarning($"[BinPackerViz] manifest 비었음 (source={src})"); return; }

        var unplaced = new List<CargoType>();
        var reasons = new List<BinPacker.UnplacedReason>();
        var placed = packer.Pack(manifestList, unplaced, reasons);
        lastPlaced = placed;

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

        // 통계: 부피 점유율(순수 BPP 핵심 지표) + 미적재 사유
        float fillPct = FillPercent(items);
        var r = items.Count > 0 ? reward.Final(items) : default;
        var sb = new StringBuilder();
        sb.Append($"[BinPackerViz:{packMode}] manifest {manifestList.Count}개(src={src}) → 배치 {placed.Count}개 · 부피점유 {fillPct:F1}%");
        if (unplaced.Count > 0)
        {
            var byReason = new Dictionary<string, int>();
            foreach (var ur in reasons) { byReason.TryGetValue(ur.dominant, out int c); byReason[ur.dominant] = c + 1; }
            sb.Append($" (미적재 {unplaced.Count} → ");
            bool first = true;
            foreach (var kv in byReason) { if (!first) sb.Append(" · "); sb.Append($"{kv.Key}×{kv.Value}"); first = false; }
            sb.Append(")");
        }
        if (items.Count > 0) sb.Append($" | {r}");
        Debug.Log(sb.ToString());
    }

    /// <summary>배치 화물 총부피 / 트레이 내부부피 (%). 순수 BPP "꽉 채우기" 지표.</summary>
    private float FillPercent(List<RuleChecker.PlacedItem> items)
    {
        float trayVol = ruleConfig.trayLateralM * ruleConfig.trayLengthM * ruleConfig.heightLimitM;
        if (trayVol < 1e-9f) return 0f;
        float used = 0f;
        foreach (var p in items) used += (p.halfSize.x * 2f) * (p.halfSize.y * 2f) * (p.halfSize.z * 2f);
        return 100f * used / trayVol;
    }

    // ── 적재함 (바닥 + 외곽 + 격자 + 높이 한도) ─────────────────
    private void BuildTray()
    {
        float lx = ruleConfig.trayLateralM, lz = ruleConfig.trayLengthM;
        float hy = ruleConfig.heightLimitM, fy = ruleConfig.floorTopY;
        float cx = lx * 0.5f, cz = lz * 0.5f;   // rear-left 코너 원점 → 중심 = (cx, ·, cz)

        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "TrayFloor";
        Destroy(floor.GetComponent<Collider>());
        floor.transform.SetParent(root, false);
        floor.transform.localPosition = new Vector3(cx, fy - 0.003f, cz);
        floor.transform.localScale = new Vector3(lx, 0.006f, lz);
        floor.GetComponent<MeshRenderer>().sharedMaterial =
            CargoFactory.MakePBR(new Color(0.15f, 0.16f, 0.20f), 0.1f, 0.2f);

        // 외곽 와이어 박스 — x∈[0,lx], z∈[0,lz]
        var box = new List<Vector3>();
        Vector3[] c8 =
        {
            new Vector3(0f, fy, 0f), new Vector3(lx, fy, 0f), new Vector3(lx, fy, lz), new Vector3(0f, fy, lz),
            new Vector3(0f, fy + hy, 0f), new Vector3(lx, fy + hy, 0f), new Vector3(lx, fy + hy, lz), new Vector3(0f, fy + hy, lz),
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
                float x = c * lx / cols;
                g.Add(new Vector3(x, gy, 0f)); g.Add(new Vector3(x, gy, lz));
            }
            for (int r = 0; r <= rows; r++)
            {
                float z = r * lz / rows;
                g.Add(new Vector3(0f, gy, z)); g.Add(new Vector3(lx, gy, z));
            }
            MakeLines("TrayGrid", g, new Color(1f, 1f, 1f, 0.25f));
        }

        // 높이 한도: 반투명 빨간 천장 + 상단 테두리
        float topY = fy + hy;
        var ceil = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceil.name = "HeightLimitPlane";
        Destroy(ceil.GetComponent<Collider>());
        ceil.transform.SetParent(root, false);
        ceil.transform.localPosition = new Vector3(cx, topY, cz);
        ceil.transform.localScale = new Vector3(lx, 0.002f, lz);
        ceil.GetComponent<MeshRenderer>().sharedMaterial = MakeTransparent(new Color(0.95f, 0.2f, 0.2f, 0.22f));

        var barMat = CargoFactory.MakePBR(new Color(0.95f, 0.15f, 0.15f), 0.2f, 0.5f);
        Vector3[] c4 =
        {
            new Vector3(0f, topY, 0f), new Vector3(lx, topY, 0f),
            new Vector3(lx, topY, lz), new Vector3(0f, topY, lz),
        };
        for (int i = 0; i < 4; i++) MakeBar("HeightLimitEdge" + i, c4[i], c4[(i + 1) % 4], 0.006f, barMat);
    }

    private void BuildCornerLabels()
    {
        float lx = ruleConfig.trayLateralM, lz = ruleConfig.trayLengthM;
        float y = ruleConfig.floorTopY + 0.008f;
        Color front = new Color(1f, 0.55f, 0.25f);
        Color rear = new Color(0.35f, 0.8f, 1f);
        // rear-left 코너 원점: RL=(0,0) RR=(lx,0)=뒤 | FL=(0,lz) FR=(lx,lz)=앞
        MakeLabel("FL", new Vector3(0f, y, lz), front);
        MakeLabel("FR", new Vector3(lx, y, lz), front);
        MakeLabel("RL", new Vector3(0f, y, 0f), rear);
        MakeLabel("RR", new Vector3(lx, y, 0f), rear);
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
        Vector3 look = new Vector3(ruleConfig.trayLateralM * 0.5f, ruleConfig.floorTopY + 0.05f, lenZ * 0.5f); // 트레이 중심
        Vector3 eye = look + new Vector3(lenZ * 0.9f, lenZ * 0.9f, -lenZ);
        camGo.transform.localPosition = eye;
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
