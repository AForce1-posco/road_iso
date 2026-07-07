using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
// TestRoad1(Unity 2020.3)용: 구 Input Manager 사용 (New Input System 아님)

/// <summary>
/// 물리 기반 자유 배치. 격자 스냅 없이 실제 크기로 놓고, 콜라이더로 표면에 안착시킨다.
/// 물리 토글(고정↔물리)로 "설계 배치"와 "실제로 굴러가/무너지나"를 오간다.
/// 화물별 고정/자유(F)는 설계서의 고정방식(secured/free)에 대응.
///
/// 조작: 좌클릭=배치 / 우클릭=제거 / R=요(yaw) 회전 / T=눕히기(pitch) / F=고정·해제
/// 계산은 하지 않는다 — OnLayoutChanged로 화물 목록만 발행.
/// </summary>
public class CargoPlacer : MonoBehaviour
{
    public enum CameraMode { Angled, TopDown }

    [Header("적재함 (바구니형 트레이, m) — 실측 21(좌우 X) × 61(주행/길이 Z), 겉 64×24·벽두께 1.5 cm")]
    public float bedWidthX = 0.21f;    // 바닥 폭(X, 좌우)  — 실측 21cm
    public float bedLengthZ = 0.61f;   // 바닥 길이(Z, 주행) — 실측 61cm
    public float wallHeight = 0.06f;   // 사방 벽 높이 (바구니)
    public float floorThickness = 0.01f;
    public float wallThickness = 0.015f;
    public bool hasWalls = true;
    public Material bedMaterial;

    [Tooltip("최대 적재 높이(적층 한도, 바닥 위 m). 상단에 빨간 테두리로 표시")]
    public float maxStackHeightM = 0.27f;
    public bool showHeightLimit = true;

    [Header("배치 스냅")]
    [Tooltip("근접(원점 쪽) 코너를 격자선에 스냅 (정렬)")]
    public bool snapToGrid = true;
    [Tooltip("근처 화물/벽 면에 자석처럼 딱 붙임 (밀착)")]
    public bool snapToCargo = false;
    [Tooltip("격자 스냅이 이웃과 겹치면 다음 빈 격자칸으로 밀기")]
    public bool pushToNextGrid = true;
    [Tooltip("자석 스냅 감지 거리(m)")]
    public float magnetSnapDistM = 0.03f;
    [Tooltip("배치 전 스냅 위치를 반투명 고스트로 미리보기")]
    public bool showGhostPreview = true;

    [Header("참조 격자 (눈금용 = 스냅 단위)")]
    public bool showGrid = true;
    public float gridCellSizeM = 0.01f;   // 1cm — 스냅도 이 단위로
    public Material gridMaterial;
    public Color gridColor = new Color(1f, 1f, 1f, 0.25f);

    [Tooltip("적재함 4코너에 FL/FR/RL/RR 표시 (front=+z=운전석)")]
    public bool showCornerLabels = true;

    [Header("화물 종류 (질량은 목업 분동값)")]
    public CargoType[] cargoTypes;

    [Tooltip("화물 DB CSV (name,massKg,sizeX,sizeY,sizeZ,shape). 지정하면 cargoTypes를 이걸로 채움")]
    public TextAsset cargoDatabaseCsv;

    [Header("로드셀 4점 (실측 위치로 조절)")]
    public bool useDefaultSupports = true;
    public SupportConfig supports;

    [Header("판정 / 기타")]
    public float emptyMassKg = 0f;
    public RiskThresholds thresholds = RiskThresholds.Default;
    public Color cogMarkerColor = Color.red;
    [Tooltip("최대 적재 한계(kg). 0이면 제한 없음. 초과 시 대시보드 경고")]
    public float maxPayloadKg = 0f;

    [Header("물리")]
    public float fallThreshold = 0.08f; // 바닥면보다 이만큼 아래로 떨어지면 낙하로 보고 제거

    [Header("저장 (비우면 persistentDataPath 사용)")]
    public string savePath = "";

    [Header("불러오기 (수정 후 재저장 가능)")]
    [Tooltip("특정 파일을 직접 지정 (절대경로 또는 Assets 상대경로). 채우면 loadFileName·폴더 무시하고 이걸 최우선. 동적 씬 CargoBedLoader.layoutPath 와 동일. 예: /.../Assets/Data/Results/boxpack001_best.json")]
    public string layoutPath = "";
    [Tooltip("불러올 파일명 (확장자 생략 가능). 비우면 기본 저장본. 예: case03_left_heavy_pipes, test_20260703_...")]
    public string loadFileName = "";
    [Tooltip("체크=TestCases 폴더에서, 해제=Cases 폴더에서 불러옴")]
    public bool loadFromTestFolder = false;
    [Tooltip("체크 시 Play하자마자 자동 로드 (layoutPath 또는 loadFileName)")]
    public bool autoLoadOnPlay = false;

    [Header("카메라")]
    public bool autoFrameCamera = true;
    public CameraMode cameraMode = CameraMode.Angled;
    [Range(0f, 0.5f)] public float panelShift = 0.33f;

    // ── 공개 정보 ──────────────────────────────────────────────────────────
    public event Action<IReadOnlyList<PlacedCargo>> OnLayoutChanged;
    public IReadOnlyList<PlacedCargo> Placed => placedData;
    public float EmptyMassKg => emptyMassKg;
    public RiskThresholds Thresholds => thresholds;
    public float BedTopY => transform.position.y + floorThickness; // 화물이 놓이는 바닥면
    public bool HasWalls => hasWalls;
    public float WallTopY => transform.position.y + floorThickness + (hasWalls ? wallHeight : 0f);
    public bool PhysicsOn => physicsOn;
    public Func<bool> PointerOverUI;

    public SupportConfig WorldSupports
    {
        get
        {
            Vector2 off = new Vector2(transform.position.x, transform.position.z);
            SupportConfig w = supports;
            w.fl.position += off; w.fr.position += off;
            w.rl.position += off; w.rr.position += off;
            return w;
        }
    }

    // ── 내부 상태 ──────────────────────────────────────────────────────────
    private class Item
    {
        public CargoType type;
        public GameObject go;
        public Rigidbody rb;
        public MeshRenderer mr;
        public Collider col;
        public bool secured;
        public Color baseColor;
        public PlacedCargo data;
    }

    private readonly List<Item> items = new List<Item>();
    private readonly List<PlacedCargo> placedData = new List<PlacedCargo>();
    private int activeTypeIndex = 0;
    private Quaternion placementRot = Quaternion.identity;
    private bool physicsOn = false;
    private Camera cam;
    private BoxCollider bedCollider;
    private Transform cargoParent;
    private GameObject gridObject;
    private GameObject ghost;   // 배치 미리보기(반투명)

    private static readonly Color[] Palette =
    {
        new Color(0.30f, 0.55f, 0.85f), new Color(0.90f, 0.55f, 0.25f),
        new Color(0.35f, 0.72f, 0.45f), new Color(0.62f, 0.45f, 0.80f),
        new Color(0.30f, 0.72f, 0.72f), new Color(0.85f, 0.40f, 0.45f),
        new Color(0.85f, 0.75f, 0.35f),
    };

    void Awake()
    {
        cam = Camera.main;
        if (cargoDatabaseCsv != null) LoadCargoDatabase();
        if (cargoTypes == null || cargoTypes.Length == 0)
        {
            // 인스펙터에서 비워두면 실측 CSV 내장 카탈로그 사용
            cargoTypes = CargoCatalog.CreateDefault();
            Debug.Log($"화물 카탈로그 기본값 적용: {cargoTypes.Length}종");
        }
        if (useDefaultSupports)
            supports = SupportConfig.Default(bedWidthX, bedLengthZ);
    }

    /// <summary>CSV(name,massKg,sizeX,sizeY,sizeZ,shape)로 cargoTypes를 채운다.</summary>
    private void LoadCargoDatabase()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<CargoType>();
        string[] lines = cargoDatabaseCsv.text.Split('\n');
        for (int i = 1; i < lines.Length; i++) // 0행은 헤더
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            string[] c = line.Split(',');
            if (c.Length < 6) continue;

            var ct = new CargoType { name = c[0].Trim() };
            float.TryParse(c[1], System.Globalization.NumberStyles.Float, ci, out ct.massKg);
            float x, y, z;
            float.TryParse(c[2], System.Globalization.NumberStyles.Float, ci, out x);
            float.TryParse(c[3], System.Globalization.NumberStyles.Float, ci, out y);
            float.TryParse(c[4], System.Globalization.NumberStyles.Float, ci, out z);
            ct.sizeM = new Vector3(x, y, z);

            string s = c[5].Trim().ToLower();
            ct.shape = s == "pipe" ? CargoShape.Pipe : s == "drum" ? CargoShape.Drum : CargoShape.Box;
            list.Add(ct);
        }
        if (list.Count > 0)
        {
            cargoTypes = list.ToArray();
            Debug.Log($"화물 DB 로드: {cargoTypes.Length}종");
        }
    }

    void Start()
    {
        RebuildVisuals();
        if (cargoParent == null)
        {
            var existing = transform.Find("Cargo");
            cargoParent = existing != null ? existing : new GameObject("Cargo").transform;
            cargoParent.SetParent(transform, false);
        }
        FrameCamera();
        ApplySceneLook();
        RaiseChanged();

        if (autoLoadOnPlay && (!string.IsNullOrEmpty(layoutPath) || !string.IsNullOrEmpty(loadFileName)))
            LoadLayout(); // Play 직후 지정 배치 자동 로드 (layoutPath 우선)
    }

    /// <summary>트레이 시각물(바닥·벽·격자·라벨·높이표시)을 지우고 다시 그린다.
    /// 편집 모드(StaticSceneSetup의 Build 버튼)에서도 호출 가능 — 멱등이라 중복 안 생김.</summary>
    public void RebuildVisuals()
    {
        ClearGenerated();
        BuildBed();
        BuildGrid();
    }

    // 생성된 트레이 시각물만 제거 (화물 "Cargo"·대시보드 Canvas 등은 이름이 달라 보존).
    private void ClearGenerated()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var ch = transform.GetChild(i);
            string n = ch.name;
            if (n == "BedFloor" || n.StartsWith("Wall") || n == "GridLines"
                || n.StartsWith("Corner_") || n.StartsWith("HeightLimit_"))
                SafeDestroy(ch.gameObject);
        }
        gridObject = null;
    }

    private void SafeDestroy(UnityEngine.Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o);
        else DestroyImmediate(o);
    }

    private void ApplySceneLook()
    {
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.11f, 0.12f, 0.15f); // 부드러운 다크 슬레이트 (UI와 조화)
        }
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.36f, 0.38f, 0.42f);
    }

    void Update()
    {
        HandleKeyboard();
        HandleMouse();
        if (showHeightLimit) UpdateHeightWarnings();
    }

    // 최대 적재높이를 넘는 화물은 빨갛게 틴트, 아니면 원래 색 복원.
    private static readonly Color HeightWarnColor = new Color(0.95f, 0.15f, 0.15f);
    private void UpdateHeightWarnings()
    {
        float limitY = BedTopY + maxStackHeightM;
        foreach (Item it in items)
        {
            if (it.go == null || it.mr == null) continue;
            bool over = it.mr.bounds.max.y > limitY + 1e-4f;
            Color target = over ? HeightWarnColor : it.baseColor;
            if (it.mr.material.color != target) it.mr.material.color = target;
        }
    }

    void FixedUpdate()
    {
        if (!physicsOn) return;
        List<Item> fallen = null;
        float floorY = BedTopY;
        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            if (it.go == null) continue;
            it.data.worldPos = it.go.transform.position;
            // 트레이 밖/아래로 떨어진 화물은 로드셀 위에 없으므로 제거(계산에서 빠짐)
            if (it.go.transform.position.y < floorY - fallThreshold)
                (fallen ??= new List<Item>()).Add(it);
        }
        if (fallen != null) foreach (Item it in fallen) RemoveItem(it);
        RaiseChanged();
    }

    // ── 입력 ───────────────────────────────────────────────────────────────
    private Item dragItem; // 가운데 버튼으로 끌고 있는 화물

    private void HandleKeyboard()
    {
        if (Input.GetKeyDown(KeyCode.R)) placementRot = Quaternion.Euler(0f, 90f, 0f) * placementRot;
        if (Input.GetKeyDown(KeyCode.T)) placementRot = Quaternion.Euler(90f, 0f, 0f) * placementRot;
        if (Input.GetKeyDown(KeyCode.F)) ToggleSecuredUnderCursor();
    }

    private void HandleMouse()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return;
        bool overUI = PointerOverUI != null && PointerOverUI();
        UpdateGhost(overUI);

        // 가운데 버튼(휠 클릭) 드래그 = 이미 놓인 화물 이동
        if (Input.GetMouseButtonDown(2) && !overUI)
        {
            Ray r = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(r, out RaycastHit h, 1000f))
            {
                Item it = FindItem(h.collider);
                if (it != null)
                {
                    dragItem = it;
                    if (dragItem.rb != null) dragItem.rb.isKinematic = true; // 드래그 중 물리 정지
                }
            }
        }
        if (dragItem != null && Input.GetMouseButton(2)) { DragMove(); return; }
        if (dragItem != null && Input.GetMouseButtonUp(2))
        {
            dragItem.data.worldPos = dragItem.go.transform.position;
            if (physicsOn && !dragItem.secured && dragItem.rb != null) dragItem.rb.isKinematic = false;
            dragItem = null;
            RaiseChanged();
            return;
        }

        // 좌클릭 배치 / 우클릭 제거
        bool left = Input.GetMouseButtonDown(0);
        bool right = Input.GetMouseButtonDown(1);
        if (!left && !right) return;
        if (overUI) return;

        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return;

        if (left)
        {
            if (cargoTypes != null && cargoTypes.Length > 0) PlaceAt(hit.point);
        }
        else
        {
            Item it = FindItem(hit.collider);
            if (it != null) RemoveItem(it);
        }
    }

    /// <summary>드래그 중 화물을 트레이 바닥 평면 위에서 XZ 이동(벽 안 클램프, 긴 화물은 벽 위 안착).</summary>
    private void DragMove()
    {
        Plane plane = new Plane(Vector3.up, new Vector3(0f, BedTopY, 0f));
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!plane.Raycast(ray, out float dist)) return;
        Vector3 p = ray.GetPoint(dist);

        Vector3 ext = dragItem.mr != null ? dragItem.mr.bounds.extents : dragItem.type.sizeM * 0.5f;
        float x = ClampInside(p.x, transform.position.x, bedWidthX * 0.5f, ext.x);
        float z = ClampInside(p.z, transform.position.z, bedLengthZ * 0.5f, ext.z);
        SnapXZ(ref x, ref z, ext, dragItem);

        bool overSized = ext.x > bedWidthX * 0.5f || ext.z > bedLengthZ * 0.5f;
        float restY = (overSized && hasWalls) ? WallTopY : BedTopY;
        Vector3 pos = new Vector3(x, restY + ext.y + 0.001f, z);
        if (WouldOverlap(pos, ext, dragItem)) return; // 다른 화물과 겹치는 위치로는 이동 안 함
        dragItem.go.transform.position = pos;
        dragItem.data.worldPos = pos;
        RaiseChanged();
    }

    // ── 배치 ───────────────────────────────────────────────────────────────
    private void PlaceAt(Vector3 surfacePoint)
    {
        CargoType type = cargoTypes[activeTypeIndex];
        Item it = CreateItem(type, activeTypeIndex);

        // 임시로 위로 치워 크기 측정 (아래 레이가 자기 자신에 안 맞게)
        // 렌더러 바운드 사용: 콜라이더 바운드는 물리 동기화 전이라 이 프레임엔 신뢰 불가
        it.go.transform.position = new Vector3(0f, 1000f, 0f);
        Vector3 ext = it.mr != null ? it.mr.bounds.extents
            : (it.col != null ? it.col.bounds.extents : type.sizeM * 0.5f);

        // 트레이 안쪽으로 XZ 클램프 (벽을 뚫지 않게)
        float cx = transform.position.x, cz = transform.position.z;
        float x = ClampInside(surfacePoint.x, cx, bedWidthX * 0.5f, ext.x);
        float z = ClampInside(surfacePoint.z, cz, bedLengthZ * 0.5f, ext.z);
        SnapXZ(ref x, ref z, ext, null);

        // 트레이보다 긴 화물(X 또는 Z 축)은 바닥에 놓으면 벽을 관통 →
        // 벽 위에 걸치도록 안착면을 벽 상단으로 올린다 (현실의 "걸침").
        bool overSized = ext.x > bedWidthX * 0.5f || ext.z > bedLengthZ * 0.5f;

        float restY;
        if (overSized && hasWalls)
        {
            restY = WallTopY; // 벽 상단에 걸침 (레이캐스트 대신 벽 높이 사용)
        }
        else
        {
            // 클램프된 위치에서 아래로 레이 → 바닥 또는 기존 화물 위에 정확히 안착
            restY = BedTopY;
            float rayStartY = transform.position.y + 3f;
            if (Physics.Raycast(new Vector3(x, rayStartY, z), Vector3.down, out RaycastHit down, 5f))
                restY = down.point.y;
        }

        Vector3 center = new Vector3(x, restY + ext.y + 0.001f, z);
        it.go.transform.position = center;
        it.data.worldPos = center;

        // 이미 화물이 있는 공간과 겹치면 배치 취소 (현실적으로 불가). 위에 얹기(적층)는 허용.
        if (WouldOverlap(center, ext, null))
        {
            Destroy(it.go);
            Debug.Log("[배치 취소] 이미 화물이 있는 공간과 겹침");
            return;
        }

        if (physicsOn && !it.secured) it.rb.isKinematic = false;

        items.Add(it);
        placedData.Add(it.data);
        RaiseChanged();
    }

    private static float ClampInside(float v, float center, float innerHalf, float ext)
    {
        float min = center - innerHalf + ext;
        float max = center + innerHalf - ext;
        return min <= max ? Mathf.Clamp(v, min, max) : center;
    }

    // ── 스냅 (격자 정렬 + 자석 밀착) ──────────────────────────────────────────
    /// <summary>후보 중심(x,z)을 활성 토글에 따라 스냅. exclude는 스냅 계산에서 뺄 화물(드래그 중인 것).</summary>
    private void SnapXZ(ref float x, ref float z, Vector3 ext, Item exclude)
    {
        if (!snapToGrid && !snapToCargo) return;

        bool magX = false, magZ = false;
        if (snapToCargo)
        {
            magX = MagnetAxis(ref x, ext.x, z, ext.z, 0, exclude);
            magZ = MagnetAxis(ref z, ext.z, x, ext.x, 1, exclude);
        }
        if (snapToGrid)
        {
            if (!magX) x = GridSnapAxis(x, ext.x, transform.position.x);
            if (!magZ) z = GridSnapAxis(z, ext.z, transform.position.z);
            if (pushToNextGrid) PushOutOfOverlap(ref x, ref z, ext, exclude);
        }

        // 스냅(격자/밀기)이 트레이 밖으로 내보내지 않게 다시 안쪽으로 클램프
        x = ClampInside(x, transform.position.x, bedWidthX * 0.5f, ext.x);
        z = ClampInside(z, transform.position.z, bedLengthZ * 0.5f, ext.z);
    }

    // 근접(원점 쪽) 코너를 격자선에 스냅 → 원점 쪽 면이 항상 칸 경계에 앉음.
    private float GridSnapAxis(float c, float half, float origin)
    {
        float g = Mathf.Max(1e-4f, gridCellSizeM);
        float rel = c - origin;
        float s = rel >= 0f ? 1f : -1f;
        float nearCorner = rel - s * half;                       // 원점 쪽 면
        float snapped = Mathf.Round(nearCorner / g) * g;
        return origin + snapped + s * half;
    }

    // 축(0=x,1=z)에서 이웃 화물/벽 면에 밀착. 수직축이 겹치는 화물만 대상.
    private bool MagnetAxis(ref float c, float half, float cPerp, float halfPerp, int axis, Item exclude)
    {
        float dist = magnetSnapDistM;
        float best = c; bool found = false;
        float thisMin = c - half, thisMax = c + half;

        foreach (Item it in items)
        {
            if (it == exclude || it.go == null || it.mr == null) continue;
            Bounds b = it.mr.bounds;
            float itMin = axis == 0 ? b.min.x : b.min.z;
            float itMax = axis == 0 ? b.max.x : b.max.z;
            float itPerpMin = axis == 0 ? b.min.z : b.min.x;
            float itPerpMax = axis == 0 ? b.max.z : b.max.x;
            // 수직축에서 실제로 겹쳐야 '옆'에 있다고 봄
            if (cPerp + halfPerp <= itPerpMin || cPerp - halfPerp >= itPerpMax) continue;

            float d1 = Mathf.Abs(thisMin - itMax); // 내 앞면을 이웃 뒷면에
            if (d1 < dist) { dist = d1; best = itMax + half; found = true; }
            float d2 = Mathf.Abs(thisMax - itMin); // 내 뒷면을 이웃 앞면에
            if (d2 < dist) { dist = d2; best = itMin - half; found = true; }
        }

        // 트레이 벽에도 밀착
        float trayHalf = axis == 0 ? bedWidthX * 0.5f : bedLengthZ * 0.5f;
        float origin = axis == 0 ? transform.position.x : transform.position.z;
        float dw1 = Mathf.Abs(thisMin - (origin - trayHalf));
        if (dw1 < dist) { dist = dw1; best = origin - trayHalf + half; found = true; }
        float dw2 = Mathf.Abs(thisMax - (origin + trayHalf));
        if (dw2 < dist) { dist = dw2; best = origin + trayHalf - half; found = true; }

        if (found) c = best;
        return found;
    }

    // 격자 스냅 후 이웃과 겹치면 원점 반대 방향으로 한 칸씩 밀어 빈 칸 찾기 ("다음 11칸으로").
    private void PushOutOfOverlap(ref float x, ref float z, Vector3 ext, Item exclude)
    {
        float g = Mathf.Max(1e-4f, gridCellSizeM);
        float sx = x - transform.position.x >= 0f ? 1f : -1f;
        float sz = z - transform.position.z >= 0f ? 1f : -1f;
        int guard = 0;
        while (OverlapsExisting(x, z, ext, exclude) && guard++ < 200)
        {
            // 겹치는 이웃과의 침투가 작은 축을 골라 그 축으로 한 칸 밀기
            float px = PenetrationAxis(x, ext.x, z, ext.z, 0, exclude);
            float pz = PenetrationAxis(z, ext.z, x, ext.x, 1, exclude);
            if (px <= pz) x += sx * g; else z += sz * g;
        }
    }

    // 3D AABB 겹침 (적층=면 맞닿음은 eps로 허용). 같은 높이에서 수평 교차면 true.
    private bool WouldOverlap(Vector3 center, Vector3 ext, Item exclude)
    {
        const float eps = 0.002f;
        foreach (Item it in items)
        {
            if (it == exclude || it.go == null || it.mr == null) continue;
            Bounds b = it.mr.bounds;
            bool ox = center.x - ext.x < b.max.x - eps && center.x + ext.x > b.min.x + eps;
            bool oy = center.y - ext.y < b.max.y - eps && center.y + ext.y > b.min.y + eps;
            bool oz = center.z - ext.z < b.max.z - eps && center.z + ext.z > b.min.z + eps;
            if (ox && oy && oz) return true;
        }
        return false;
    }

    private bool OverlapsExisting(float x, float z, Vector3 ext, Item exclude)
    {
        foreach (Item it in items)
        {
            if (it == exclude || it.go == null || it.mr == null) continue;
            Bounds b = it.mr.bounds;
            bool ox = x - ext.x < b.max.x - 1e-4f && x + ext.x > b.min.x + 1e-4f;
            bool oz = z - ext.z < b.max.z - 1e-4f && z + ext.z > b.min.z + 1e-4f;
            if (ox && oz) return true;
        }
        return false;
    }

    // 겹치는 이웃과의 해당 축 최소 침투량 (밀 방향 선택용). 없으면 큰 값.
    private float PenetrationAxis(float c, float half, float cPerp, float halfPerp, int axis, Item exclude)
    {
        float best = float.MaxValue;
        foreach (Item it in items)
        {
            if (it == exclude || it.go == null || it.mr == null) continue;
            Bounds b = it.mr.bounds;
            float itMin = axis == 0 ? b.min.x : b.min.z;
            float itMax = axis == 0 ? b.max.x : b.max.z;
            float itPerpMin = axis == 0 ? b.min.z : b.min.x;
            float itPerpMax = axis == 0 ? b.max.z : b.max.x;
            if (cPerp + halfPerp <= itPerpMin || cPerp - halfPerp >= itPerpMax) continue;
            float overlap = Mathf.Min(c + half, itMax) - Mathf.Max(c - half, itMin);
            if (overlap > 0f) best = Mathf.Min(best, overlap);
        }
        return best;
    }

    // ── 배치 미리보기(고스트) ────────────────────────────────────────────────
    private void UpdateGhost(bool overUI)
    {
        if (!showGhostPreview || overUI || dragItem != null || cargoTypes == null || cargoTypes.Length == 0)
        {
            if (ghost != null) ghost.SetActive(false);
            return;
        }

        Plane plane = new Plane(Vector3.up, new Vector3(0f, BedTopY, 0f));
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!plane.Raycast(ray, out float d))
        {
            if (ghost != null) ghost.SetActive(false);
            return;
        }
        Vector3 p = ray.GetPoint(d);

        CargoType type = cargoTypes[Mathf.Clamp(activeTypeIndex, 0, cargoTypes.Length - 1)];
        Vector3 ext = RotatedExtents(type.sizeM, placementRot);
        float x = ClampInside(p.x, transform.position.x, bedWidthX * 0.5f, ext.x);
        float z = ClampInside(p.z, transform.position.z, bedLengthZ * 0.5f, ext.z);
        SnapXZ(ref x, ref z, ext, null);

        if (ghost == null) ghost = CreateGhost();

        // 실제 안착 높이(적층 반영) 계산 + 겹침 여부로 색 결정
        bool overSized = ext.x > bedWidthX * 0.5f || ext.z > bedLengthZ * 0.5f;
        float restY = (overSized && hasWalls) ? WallTopY : BedTopY;
        if (!(overSized && hasWalls))
        {
            float rayStartY = transform.position.y + 3f;
            if (Physics.Raycast(new Vector3(x, rayStartY, z), Vector3.down, out RaycastHit down, 5f))
                restY = down.point.y;
        }
        Vector3 gc = new Vector3(x, restY + ext.y + 0.001f, z);
        bool blocked = WouldOverlap(gc, ext, null);

        ghost.SetActive(true);
        ghost.transform.position = gc;
        ghost.transform.localScale = ext * 2f;
        ghost.GetComponent<MeshRenderer>().sharedMaterial.color =
            blocked ? new Color(1f, 0.3f, 0.2f, 0.4f) : new Color(0.4f, 0.9f, 1f, 0.35f);
    }

    private GameObject CreateGhost()
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        g.name = "PlacementGhost";
        var col = g.GetComponent<Collider>();
        if (col) Destroy(col);
        g.transform.SetParent(transform, false);
        g.GetComponent<MeshRenderer>().sharedMaterial = MakeGhostMat();
        return g;
    }

    private static Material MakeGhostMat()
    {
        var m = new Material(Shader.Find("Standard"));
        Color c = new Color(0.4f, 0.9f, 1f, 0.35f);
        m.SetFloat("_Mode", 3f);
        m.SetColor("_Color", c);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.DisableKeyword("_ALPHATEST_ON");
        m.EnableKeyword("_ALPHABLEND_ON");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.renderQueue = 3000;
        return m;
    }

    // 회전(placementRot) 반영한 AABB 반크기
    private static Vector3 RotatedExtents(Vector3 size, Quaternion rot)
    {
        Vector3 h = size * 0.5f;
        Matrix4x4 m = Matrix4x4.Rotate(rot);
        return new Vector3(
            Mathf.Abs(m.m00) * h.x + Mathf.Abs(m.m01) * h.y + Mathf.Abs(m.m02) * h.z,
            Mathf.Abs(m.m10) * h.x + Mathf.Abs(m.m11) * h.y + Mathf.Abs(m.m12) * h.z,
            Mathf.Abs(m.m20) * h.x + Mathf.Abs(m.m21) * h.y + Mathf.Abs(m.m22) * h.z);
    }

    private Item CreateItem(CargoType type, int typeIndex)
    {
        // 화물 생성은 공용 CargoFactory 사용 (정적/동적 단일 소스)
        GameObject go = CargoFactory.Create(type, 1f, GetCargoColor(type));
        go.transform.SetParent(cargoParent, true);
        go.transform.rotation = placementRot * go.transform.localRotation; // 형상 기본회전 위에 배치회전

        var mr = go.GetComponent<MeshRenderer>();
        Color baseColor = mr.material.color; // factory가 인스턴스 색 세팅
        Collider col = go.GetComponent<Collider>();

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Max(0.001f, type.massKg);
        rb.isKinematic = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        return new Item
        {
            type = type, go = go, rb = rb, mr = mr, col = col,
            baseColor = baseColor,
            data = new PlacedCargo(type, go.transform.position, false),
        };
    }

    private void RemoveItem(Item it)
    {
        if (it.go != null) Destroy(it.go);
        items.Remove(it);
        placedData.Remove(it.data);
        RaiseChanged();
    }

    public void Remove(PlacedCargo p)
    {
        Item it = items.Find(x => x.data == p);
        if (it != null) RemoveItem(it);
    }

    private Item FindItem(Collider c)
    {
        if (c == null) return null;
        return items.Find(x => x.col == c || (x.go != null && c.transform.IsChildOf(x.go.transform)));
    }

    private void ToggleSecuredUnderCursor()
    {
        if (cam == null) return;
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return;
        Item it = FindItem(hit.collider);
        if (it == null) return;
        SetSecured(it, !it.secured);
    }

    private void SetSecured(Item it, bool secured)
    {
        it.secured = secured;
        it.data.secured = secured;
        it.mr.material.color = secured
            ? Color.Lerp(it.baseColor, new Color(0.55f, 0.55f, 0.6f), 0.55f)
            : it.baseColor;
        if (physicsOn)
        {
            if (secured)
            {
                if (!it.rb.isKinematic) { it.rb.velocity = Vector3.zero; it.rb.angularVelocity = Vector3.zero; }
                it.rb.isKinematic = true;
            }
            else it.rb.isKinematic = false;
        }
    }

    // ── 공개 API ───────────────────────────────────────────────────────────
    public void SetActiveType(int index)
    {
        if (cargoTypes == null || cargoTypes.Length == 0) return;
        activeTypeIndex = Mathf.Clamp(index, 0, cargoTypes.Length - 1);
    }
    public int ActiveType => activeTypeIndex;

    public Color GetCargoColor(CargoType type)
    {
        if (type == null) return Color.gray;
        if (type.material != null) return type.material.color;
        int i = cargoTypes != null ? System.Array.IndexOf(cargoTypes, type) : -1;
        if (i < 0) i = 0;
        return Palette[i % Palette.Length];
    }

    public void Undo()
    {
        if (items.Count == 0) return;
        RemoveItem(items[items.Count - 1]);
    }

    public void ClearAll()
    {
        if (items.Count == 0) return;
        foreach (Item it in items) if (it.go != null) Destroy(it.go);
        items.Clear();
        placedData.Clear();
        RaiseChanged();
    }

    public void SetGridVisible(bool visible)
    {
        showGrid = visible;
        if (gridObject != null) gridObject.SetActive(visible);
    }

    public void ToggleCameraView()
    {
        cameraMode = cameraMode == CameraMode.Angled ? CameraMode.TopDown : CameraMode.Angled;
        FrameCamera();
    }

    /// <summary>물리 토글. ON=자유 화물이 중력으로 굴러/무너짐, OFF=현재 위치에 그대로 고정(복귀 안 함).</summary>
    public void SetPhysics(bool on)
    {
        physicsOn = on;
        foreach (Item it in items)
        {
            if (on)
            {
                if (!it.secured)
                {
                    it.rb.isKinematic = false;
                    it.rb.velocity = Vector3.zero;
                    it.rb.angularVelocity = Vector3.zero;
                }
            }
            else
            {
                // 현재 위치·자세 그대로 얼림 (설계 복귀 안 함 = 실시간 반영)
                if (!it.rb.isKinematic) { it.rb.velocity = Vector3.zero; it.rb.angularVelocity = Vector3.zero; }
                it.rb.isKinematic = true;
                it.data.worldPos = it.go.transform.position;
            }
        }
        RaiseChanged();
    }

    public void TogglePhysics() => SetPhysics(!physicsOn);

    // ── 카메라 ─────────────────────────────────────────────────────────────
    private void FrameCamera()
    {
        if (!autoFrameCamera) return;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Vector3 center = transform.position + Vector3.up * (floorThickness + wallHeight * 0.5f);
        float span = Mathf.Max(bedWidthX, bedLengthZ) * 1.4f;
        float vfov = cam.fieldOfView * Mathf.Deg2Rad;
        float dist = Mathf.Max(0.4f, span / (2f * Mathf.Tan(vfov * 0.5f)));

        float hfov = 2f * Mathf.Atan(Mathf.Tan(vfov * 0.5f) * Mathf.Max(0.1f, cam.aspect));
        float shift = dist * Mathf.Tan(hfov * 0.5f) * panelShift;
        Vector3 aim = center + Vector3.right * shift;

        if (cameraMode == CameraMode.TopDown)
        {
            // 위에서 내려다보되 90° 회전: 화면 위=월드 +x → 긴 축(주행 Z)이 화면 가로로
            Vector3 upT = Vector3.right;
            Vector3 camRightT = Vector3.Cross(upT, Vector3.down).normalized; // 화면 오른쪽(월드)
            Vector3 aimT = center + camRightT * shift;                        // 우측 패널 공간 확보
            cam.transform.position = aimT + Vector3.up * dist;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.down, upT);
        }
        else
        {
            // 시야 90° 회전: 카메라를 옆(-x)에 두고 +x로 봄 → 긴 축(주행 Z)이 화면 좌우로 눕는다
            Vector3 offset = new Vector3(-dist, dist * 0.7f, 0f);
            Vector3 fwd = (-offset).normalized;
            Vector3 camRight = Vector3.Cross(Vector3.up, fwd).normalized;
            Vector3 aim2 = center + camRight * shift; // 우측 패널 공간 확보 (피사체 왼쪽으로)
            cam.transform.position = aim2 + offset;
            cam.transform.LookAt(aim2);
        }
        cam.nearClipPlane = 0.01f;
    }

    private void RaiseChanged() => OnLayoutChanged?.Invoke(placedData);

    // ── 절차 생성 ─────────────────────────────────────────────────────────
    private void BuildBed()
    {
        Material floorMat = bedMaterial != null ? bedMaterial : CargoFactory.MakeLit(new Color(0.16f, 0.17f, 0.21f));
        Material wallMat = CargoFactory.MakeLit(new Color(0.24f, 0.26f, 0.31f));

        GameObject floor = MakeBox("BedFloor", new Vector3(0f, floorThickness * 0.5f, 0f),
            new Vector3(bedWidthX, floorThickness, bedLengthZ), floorMat);
        bedCollider = floor.GetComponent<BoxCollider>();

        if (showCornerLabels) BuildCornerLabels();
        if (showHeightLimit) BuildHeightLimit();

        if (!hasWalls) return;

        float wy = floorThickness + wallHeight * 0.5f;
        float wx = bedWidthX * 0.5f + wallThickness * 0.5f;
        float wz = bedLengthZ * 0.5f + wallThickness * 0.5f;
        Vector3 sideScale = new Vector3(wallThickness, wallHeight, bedLengthZ + 2f * wallThickness);
        Vector3 endScale = new Vector3(bedWidthX, wallHeight, wallThickness);
        MakeBox("WallLeft", new Vector3(-wx, wy, 0f), sideScale, wallMat);
        MakeBox("WallRight", new Vector3(wx, wy, 0f), sideScale, wallMat);
        MakeBox("WallBack", new Vector3(0f, wy, -wz), endScale, wallMat);
        MakeBox("WallFront", new Vector3(0f, wy, wz), endScale, wallMat);
    }

    private GameObject MakeBox(string name, Vector3 localPos, Vector3 scale, Material mat)
    {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = name;
        b.transform.SetParent(transform, false);
        b.transform.localPosition = localPos;
        b.transform.localScale = scale;
        b.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return b;
    }

    // 최대 적재높이(적층 한도) 표시: 상단 테두리 + 4모서리 수직 기둥 (천장면 없이 선만).
    // 배치 시각화의 와이어박스처럼 "한도 높이 상자"를 선으로 그림.
    private void BuildHeightLimit()
    {
        float baseY = floorThickness;                    // 바닥면
        float topY = floorThickness + maxStackHeightM;   // 한도 높이
        float hx = bedWidthX * 0.5f, hz = bedLengthZ * 0.5f, th = 0.006f;
        Material red = CargoFactory.MakePBR(new Color(0.95f, 0.15f, 0.15f), 0.2f, 0.5f);
        Material redDim = CargoFactory.MakePBR(new Color(0.8f, 0.3f, 0.3f), 0.2f, 0.4f);

        // 상단 테두리 (X 2 + Z 2)
        MakeBox("HeightLimit_TopXn", new Vector3(0f, topY, -hz), new Vector3(bedWidthX, th, th), red);
        MakeBox("HeightLimit_TopXp", new Vector3(0f, topY,  hz), new Vector3(bedWidthX, th, th), red);
        MakeBox("HeightLimit_TopZn", new Vector3(-hx, topY, 0f), new Vector3(th, th, bedLengthZ), red);
        MakeBox("HeightLimit_TopZp", new Vector3( hx, topY, 0f), new Vector3(th, th, bedLengthZ), red);

        // 4모서리 수직 기둥 (바닥 → 한도)
        float midY = baseY + maxStackHeightM * 0.5f;
        MakeBox("HeightLimit_PostA", new Vector3(-hx, midY, -hz), new Vector3(th, maxStackHeightM, th), redDim);
        MakeBox("HeightLimit_PostB", new Vector3( hx, midY, -hz), new Vector3(th, maxStackHeightM, th), redDim);
        MakeBox("HeightLimit_PostC", new Vector3(-hx, midY,  hz), new Vector3(th, maxStackHeightM, th), redDim);
        MakeBox("HeightLimit_PostD", new Vector3( hx, midY,  hz), new Vector3(th, maxStackHeightM, th), redDim);
    }

    // 적재함 4코너 FL/FR/RL/RR (LoadCalculator 규약: front=+z=운전석/캐빈, right=+x).
    // 앞(+z, 주황)으로는 오버행 불가, 뒤(-z, 하늘색)만 파이프 오버행 허용.
    private void BuildCornerLabels()
    {
        float hx = bedWidthX * 0.5f, hz = bedLengthZ * 0.5f, y = floorThickness + 0.008f;
        Color front = new Color(1f, 0.55f, 0.25f), rear = new Color(0.35f, 0.8f, 1f);
        MakeCornerLabel("FL", new Vector3(-hx, y,  hz), front);
        MakeCornerLabel("FR", new Vector3( hx, y,  hz), front);
        MakeCornerLabel("RL", new Vector3(-hx, y, -hz), rear);
        MakeCornerLabel("RR", new Vector3( hx, y, -hz), rear);
    }

    private void MakeCornerLabel(string text, Vector3 localPos, Color c)
    {
        var go = new GameObject("Corner_" + text);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.fontSize = 64;
        tm.characterSize = 0.015f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = c;
        go.AddComponent<LabelBillboard>(); // cam 비움 → Camera.main 향함 (거울상 방지)
    }

    private void BuildGrid()
    {
        gridObject = new GameObject("GridLines");
        gridObject.transform.SetParent(transform, false);
        var mf = gridObject.AddComponent<MeshFilter>();
        var mr = gridObject.AddComponent<MeshRenderer>();

        Material mat = gridMaterial;
        if (mat == null)
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            mat = new Material(sh) { color = gridColor };
        }
        mr.sharedMaterial = mat;

        float y = floorThickness + 0.001f;
        float hx = bedWidthX * 0.5f, hz = bedLengthZ * 0.5f;
        float step = Mathf.Max(0.005f, gridCellSizeM);
        var verts = new List<Vector3>();
        var idx = new List<int>();

        for (float x = -hx; x <= hx + 1e-4f; x += step)
            AddLine(verts, idx, new Vector3(x, y, -hz), new Vector3(x, y, hz));
        for (float z = -hz; z <= hz + 1e-4f; z += step)
            AddLine(verts, idx, new Vector3(-hx, y, z), new Vector3(hx, y, z));

        var mesh = new Mesh { name = "GridMesh" };
        mesh.SetVertices(verts);
        mesh.SetIndices(idx, MeshTopology.Lines, 0);
        mf.sharedMesh = mesh;
        gridObject.SetActive(showGrid);
    }

    private static void AddLine(List<Vector3> verts, List<int> idx, Vector3 a, Vector3 b)
    {
        int i = verts.Count;
        verts.Add(a); verts.Add(b);
        idx.Add(i); idx.Add(i + 1);
    }

    // ── 저장 / 불러오기 (설계 의도만 저장, 설정값은 인스펙터에 유지) ──────────
    private string ResolvedPath =>
        string.IsNullOrEmpty(savePath) ? CargoLayoutFile.DefaultPath : savePath;

    public void SaveLayout() => SaveLayout(ResolvedPath);

    public void SaveLayout(string path)
    {
        var file = new CargoLayoutFile
        {
            bed = new CargoLayoutBed { widthX = bedWidthX, lengthZ = bedLengthZ, wallHeight = wallHeight }
        };
        foreach (Item it in items)
        {
            if (it.go == null || it.type == null) continue;
            file.cargo.Add(new CargoLayoutEntry
            {
                type = it.type.name,
                localPos = transform.InverseTransformPoint(it.go.transform.position),
                localEuler = (Quaternion.Inverse(transform.rotation) * it.go.transform.rotation).eulerAngles,
                secured = it.secured,
            });
        }
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
        Debug.Log($"배치 저장 완료 ({file.cargo.Count}개): {path}");
    }

    /// <summary>정적 씬 테스트용 저장: Assets/Data/TestCases/test_타임스탬프.json (Cases와 분리).</summary>
    public void SaveCase()
    {
        Directory.CreateDirectory(CargoPaths.TestCasesDir);
        string path = Path.Combine(CargoPaths.TestCasesDir, $"test_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
        SaveLayout(path);
    }

    /// <summary>
    /// 불러오기 대상 경로. loadFileName이 있으면 해당 폴더(TestCases/Cases)의 그 파일,
    /// 없으면 savePath/기본 저장본. 확장자 생략 가능.
    /// </summary>
    private string LoadResolvedPath()
    {
        // 1) 직접 경로 지정(절대/상대) → 최우선 (동적 CargoBedLoader.layoutPath 와 동일)
        if (!string.IsNullOrEmpty(layoutPath))
        {
            if (layoutPath.Contains("/") || layoutPath.Contains("\\")) return layoutPath;
            return Path.Combine(Application.dataPath, layoutPath);   // 파일명만이면 Assets/ 기준
        }
        // 2) 파일명 지정 → Cases/TestCases 폴더
        if (string.IsNullOrEmpty(loadFileName)) return ResolvedPath;
        string n = loadFileName.EndsWith(".json") ? loadFileName : loadFileName + ".json";
        string dir = loadFromTestFolder ? CargoPaths.TestCasesDir : CargoPaths.CasesDir;
        return Path.Combine(dir, n);
    }

    public void LoadLayout() => LoadLayout(LoadResolvedPath());

    public void LoadLayout(string path)
    {
        if (!File.Exists(path)) { Debug.LogWarning($"저장 파일 없음: {path}"); return; }
        CargoLayoutFile file = JsonUtility.FromJson<CargoLayoutFile>(File.ReadAllText(path));
        if (file == null || file.cargo == null) { Debug.LogWarning("불러오기 실패: 파싱 오류"); return; }

        ClearAll();
        foreach (CargoLayoutEntry e in file.cargo)
        {
            CargoType t = FindType(e.type);
            if (t == null) { Debug.LogWarning($"화물 종류 '{e.type}' 없음 — 건너뜀"); continue; }
            int idx = System.Array.IndexOf(cargoTypes, t);

            Item it = CreateItem(t, idx);
            Vector3 wpos = transform.TransformPoint(e.localPos);
            Quaternion wrot = transform.rotation * Quaternion.Euler(e.localEuler);
            it.go.transform.position = wpos;
            it.go.transform.rotation = wrot;
            it.data.worldPos = wpos;
            if (e.secured) SetSecured(it, true);
            if (physicsOn && !it.secured) it.rb.isKinematic = false;

            items.Add(it);
            placedData.Add(it.data);
        }
        RaiseChanged();
        Debug.Log($"배치 불러오기 완료: {file.cargo.Count}개");
    }

    private CargoType FindType(string name)
    {
        if (cargoTypes == null) return null;
        foreach (CargoType t in cargoTypes)
            if (t != null && t.name == name) return t;
        return null;
    }
}
