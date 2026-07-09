using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// RefinementAgent 의 현재 배치(placed)를 실제 화물 모양으로 그려주는 "표시 전용" 시각화.
/// PlacementVisualizer 와 동일 — 에이전트 타입만 RefinementAgent. 학습/보상/관측엔 영향 없음.
/// Refinement는 빈패커 시드에서 재배치(relocate)하므로, Play 중 화물이 옮겨지는 게 실시간으로 보인다.
///
/// - 화물 = <see cref="CargoFactory"/> 로 생성(실물 모양+재질). 회전(rot90)은 halfSize로 역산.
/// - 적재함(21×62cm) = 바닥판 + 와이어 박스 + 그리드(RL 격자와 동일).
/// - 무게중심(CoG) = 빨간 구 + 바닥 수직선(실시간).
/// [보는 법] Play 중 autoCamera 켜면 Game 뷰에 표시. 끄면 Scene 탭에서 "RefinementViz_Root" 선택 → F.
/// </summary>
[RequireComponent(typeof(RefinementAgent))]
public class RefinementVisualizer : MonoBehaviour
{
    [Header("표시")]
    [Tooltip("표시 배율 (트레이가 21×62cm로 작아서 키워서 봄)")]
    public float displayScale = 10f;

    [Tooltip("켜면 배치를 비추는 전용 카메라를 만들어 Game 뷰에서도 보이게 함")]
    public bool autoCamera = true;

    [Tooltip("격자선 표시")]
    public bool showGrid = true;

    [Tooltip("무게중심(CoG) 마커 표시")]
    public bool showCoG = true;

    private RefinementAgent agent;
    private Transform root;
    private readonly List<GameObject> spawned = new List<GameObject>();
    private int lastSig = int.MinValue;
    private GameObject cogBall, cogStem;
    private Camera vizCam;

    void Awake() => agent = GetComponent<RefinementAgent>();

    void Start()
    {
        var go = new GameObject("RefinementViz_Root");
        root = go.transform;
        root.SetParent(transform, false);
        root.localPosition = Vector3.zero;
        root.localScale = Vector3.one * displayScale;

        BuildTray();
        if (autoCamera) SetupCamera();   // vizCam 먼저 만들어 라벨 빌보드가 이걸 바라보게
        BuildCornerLabels();
        if (showCoG) BuildCoGMarker();
    }

    // ── 코너 라벨 FL/FR/RL/RR ───────────────────────────────────
    // 축 규약(LoadCalculator): front(운전석/캐빈)=+z, right=+x.
    void BuildCornerLabels()
    {
        var cfg = agent.ruleConfig;
        float hx = cfg.trayLateralM * 0.5f, hz = cfg.trayLengthM * 0.5f, y = cfg.floorTopY + 0.008f;
        Color front = new Color(1f, 0.55f, 0.25f);  // 앞(캐빈) = 주황
        Color rear  = new Color(0.35f, 0.8f, 1f);   // 뒤 = 하늘색
        MakeLabel("FL", new Vector3(-hx, y,  hz), front);
        MakeLabel("FR", new Vector3( hx, y,  hz), front);
        MakeLabel("RL", new Vector3(-hx, y, -hz), rear);
        MakeLabel("RR", new Vector3( hx, y, -hz), rear);
    }

    void MakeLabel(string text, Vector3 pos, Color c)
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
        go.AddComponent<LabelBillboard>().cam = vizCam; // 카메라 향하게 → 거울상 방지
    }

    void LateUpdate()
    {
        var items = agent.PlacedItems;
        int sig = Signature(items);
        if (sig != lastSig) { lastSig = sig; Rebuild(items); }
        if (showCoG) UpdateCoG(items);
    }

    // ── 화물 메시 ───────────────────────────────────────────────
    void Rebuild(IReadOnlyList<RuleChecker.PlacedItem> items)
    {
        foreach (var g in spawned) if (g) Destroy(g);
        spawned.Clear();
        if (items == null) return;

        foreach (var p in items)
        {
            if (p.type == null) continue;

            var wrap = new GameObject("cargo_" + p.type.id);
            wrap.transform.SetParent(root, false);
            wrap.transform.localPosition = p.center;                 // 트레이 로컬 좌표 그대로
            wrap.transform.localRotation = IsRotated90(p) ? Quaternion.Euler(0f, 90f, 0f)
                                                          : Quaternion.identity;

            var mesh = CargoFactory.Create(p.type, 1f, Color.white); // 실물 모양+재질, 실측 m 크기
            StripColliders(mesh);
            mesh.transform.SetParent(wrap.transform, false);
            spawned.Add(wrap);
        }
    }

    static bool IsRotated90(RuleChecker.PlacedItem p)
    {
        Vector3 s = p.type.sizeM;
        float asIs    = Mathf.Abs(p.halfSize.x * 2f - s.x) + Mathf.Abs(p.halfSize.z * 2f - s.z);
        float swapped = Mathf.Abs(p.halfSize.x * 2f - s.z) + Mathf.Abs(p.halfSize.z * 2f - s.x);
        return swapped < asIs - 1e-5f;
    }

    static void StripColliders(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
    }

    static int Signature(IReadOnlyList<RuleChecker.PlacedItem> items)
    {
        if (items == null) return 0;
        int h = 17 + items.Count * 131;
        foreach (var p in items)
        {
            if (p.type != null) h = h * 31 + p.type.id.GetHashCode();
            h = h * 31 + Mathf.RoundToInt(p.center.x * 1000f);
            h = h * 31 + Mathf.RoundToInt(p.center.y * 1000f);
            h = h * 31 + Mathf.RoundToInt(p.center.z * 1000f);
        }
        return h;
    }

    // ── 적재함 (바닥 + 외곽 + 그리드) ───────────────────────────
    void BuildTray()
    {
        var cfg = agent.ruleConfig;
        float lx = cfg.trayLateralM, lz = cfg.trayLengthM, hy = cfg.heightLimitM, fy = cfg.floorTopY;
        float hx = lx * 0.5f, hz = lz * 0.5f;

        // 바닥판 (얇은 어두운 상자)
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "TrayFloor";
        Destroy(floor.GetComponent<Collider>());
        floor.transform.SetParent(root, false);
        floor.transform.localPosition = new Vector3(0f, fy - 0.003f, 0f);
        floor.transform.localScale = new Vector3(lx, 0.006f, lz);
        floor.GetComponent<MeshRenderer>().sharedMaterial =
            CargoFactory.MakePBR(new Color(0.15f, 0.16f, 0.20f), 0.1f, 0.2f);

        // 외곽 와이어 박스 (적재 볼륨 12모서리)
        var box = new List<Vector3>();
        Vector3[] c8 =
        {
            new Vector3(-hx, fy, -hz), new Vector3(hx, fy, -hz), new Vector3(hx, fy, hz), new Vector3(-hx, fy, hz),
            new Vector3(-hx, fy + hy, -hz), new Vector3(hx, fy + hy, -hz), new Vector3(hx, fy + hy, hz), new Vector3(-hx, fy + hy, hz),
        };
        int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        for (int i = 0; i < 12; i++) { box.Add(c8[edges[i, 0]]); box.Add(c8[edges[i, 1]]); }
        MakeLines("TrayOutline", box, new Color(0.4f, 0.85f, 1f, 1f));

        // 그리드 (RL 격자 = 에이전트가 고르는 셀)
        if (showGrid)
        {
            var g = new List<Vector3>();
            float gy = fy + 0.001f;
            for (int c = 0; c <= agent.cols; c++)
            {
                float x = -hx + c * lx / agent.cols;
                g.Add(new Vector3(x, gy, -hz)); g.Add(new Vector3(x, gy, hz));
            }
            for (int r = 0; r <= agent.rows; r++)
            {
                float z = -hz + r * lz / agent.rows;
                g.Add(new Vector3(-hx, gy, z)); g.Add(new Vector3(hx, gy, z));
            }
            MakeLines("TrayGrid", g, new Color(1f, 1f, 1f, 0.35f));
        }

        BuildHeightLimit();
    }

    // ── 최대적재높이 표시: (A) 상단 빨간 테두리 + (B) 반투명 빨간 천장면 ──
    void BuildHeightLimit()
    {
        var cfg = agent.ruleConfig;
        float lx = cfg.trayLateralM, lz = cfg.trayLengthM;
        float hx = lx * 0.5f, hz = lz * 0.5f;
        float topY = cfg.floorTopY + cfg.heightLimitM;

        var ceil = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ceil.name = "HeightLimitPlane";
        Destroy(ceil.GetComponent<Collider>());
        ceil.transform.SetParent(root, false);
        ceil.transform.localPosition = new Vector3(0f, topY, 0f);
        ceil.transform.localScale = new Vector3(lx, 0.002f, lz);
        ceil.GetComponent<MeshRenderer>().sharedMaterial =
            MakeTransparent(new Color(0.95f, 0.2f, 0.2f, 0.22f));

        var barMat = CargoFactory.MakePBR(new Color(0.95f, 0.15f, 0.15f), 0.2f, 0.5f);
        Vector3[] c =
        {
            new Vector3(-hx, topY, -hz), new Vector3(hx, topY, -hz),
            new Vector3(hx, topY, hz),  new Vector3(-hx, topY, hz),
        };
        for (int i = 0; i < 4; i++) MakeBar("HeightLimitEdge" + i, c[i], c[(i + 1) % 4], 0.006f, barMat);
    }

    void MakeBar(string name, Vector3 a, Vector3 b, float th, Material mat)
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

    GameObject MakeLines(string name, List<Vector3> segs, Color c)
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

    // ── 무게중심 마커 ───────────────────────────────────────────
    void BuildCoGMarker()
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

    void UpdateCoG(IReadOnlyList<RuleChecker.PlacedItem> items)
    {
        if (cogBall == null) return;
        float m = 0f; Vector3 w = Vector3.zero;
        if (items != null)
            foreach (var p in items) { m += p.Mass; w += p.Mass * p.center; }

        bool has = m > 1e-6f;
        cogBall.SetActive(has);
        cogStem.SetActive(has);
        if (!has) return;

        Vector3 cog = w / m;
        cogBall.transform.localPosition = cog;

        float fy = agent.ruleConfig.floorTopY;
        float h = Mathf.Max(0.001f, cog.y - fy);
        cogStem.transform.localPosition = new Vector3(cog.x, fy + h * 0.5f, cog.z);
        cogStem.transform.localScale = new Vector3(0.004f, h, 0.004f);
    }

    // ── 재질 헬퍼 ───────────────────────────────────────────────
    static Material MakeUnlit(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        return m;
    }

    /// <summary>알파 블렌딩 반투명 재질 (URP Lit / Standard 양쪽 대응).</summary>
    static Material MakeTransparent(Color c)
    {
        int SrcA = (int)UnityEngine.Rendering.BlendMode.SrcAlpha;
        int OneMinusA = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;

        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp != null)
        {
            var m = new Material(urp);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", SrcA);
            m.SetFloat("_DstBlend", OneMinusA);
            m.SetFloat("_ZWrite", 0f);
            m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3000;
            m.SetColor("_BaseColor", c);
            return m;
        }

        var s = new Material(Shader.Find("Standard"));
        s.SetFloat("_Mode", 3f);
        s.SetInt("_SrcBlend", SrcA);
        s.SetInt("_DstBlend", OneMinusA);
        s.SetInt("_ZWrite", 0);
        s.DisableKeyword("_ALPHATEST_ON");
        s.EnableKeyword("_ALPHABLEND_ON");
        s.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        s.renderQueue = 3000;
        s.SetColor("_Color", c);
        return s;
    }

    // ── 전용 카메라 (Game 뷰용) ─────────────────────────────────
    void SetupCamera()
    {
        var camGo = new GameObject("RefinementViz_Camera");
        camGo.transform.SetParent(root, false);
        var cam = camGo.AddComponent<Camera>();
        vizCam = cam;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
        cam.depth = 10;
        cam.nearClipPlane = 0.01f;

        float lenZ = agent.ruleConfig.trayLengthM;
        Vector3 eye = new Vector3(lenZ * 0.9f, lenZ * 0.9f, -lenZ);
        camGo.transform.localPosition = eye;
        Vector3 look = new Vector3(0f, agent.ruleConfig.floorTopY + 0.05f, 0f);
        camGo.transform.localRotation = Quaternion.LookRotation((look - eye).normalized, Vector3.up);
    }

    // ── 트레이 경계 (Scene 뷰 보조 Gizmo) ───────────────────────
    void OnDrawGizmos()
    {
        if (agent == null) agent = GetComponent<RefinementAgent>();
        if (agent == null) return;
        var cfg = agent.ruleConfig;
        Transform m = root != null ? root : transform;
        Gizmos.matrix = m.localToWorldMatrix;
        Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.6f);
        Gizmos.DrawWireCube(new Vector3(0f, cfg.floorTopY + cfg.heightLimitM * 0.5f, 0f),
                            new Vector3(cfg.trayLateralM, cfg.heightLimitM, cfg.trayLengthM));
    }
}
