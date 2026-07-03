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

    [Header("적재함 (바구니형 트레이, m) — 64×24, 벽 6 cm")]
    public float bedWidthX = 0.64f;    // 바닥 폭(X)
    public float bedLengthZ = 0.24f;   // 바닥 길이(Z)
    public float wallHeight = 0.06f;   // 사방 벽 높이 (바구니)
    public float floorThickness = 0.01f;
    public float wallThickness = 0.008f;
    public bool hasWalls = true;
    public Material bedMaterial;

    [Header("배치")]
    public bool snapToCm = false;      // 켜면 1cm 격자로 스냅

    [Header("참조 격자 (스냅 아님, 눈금용)")]
    public bool showGrid = true;
    public float gridCellSizeM = 0.02f;
    public Material gridMaterial;
    public Color gridColor = new Color(1f, 1f, 1f, 0.25f);

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
        BuildBed();
        BuildGrid();
        cargoParent = new GameObject("Cargo").transform;
        cargoParent.SetParent(transform, false);
        FrameCamera();
        ApplySceneLook();
        RaiseChanged();
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
    private void HandleKeyboard()
    {
        if (Input.GetKeyDown(KeyCode.R)) placementRot = Quaternion.Euler(0f, 90f, 0f) * placementRot;
        if (Input.GetKeyDown(KeyCode.T)) placementRot = Quaternion.Euler(90f, 0f, 0f) * placementRot;
        if (Input.GetKeyDown(KeyCode.F)) ToggleSecuredUnderCursor();
    }

    private void HandleMouse()
    {
        bool left = Input.GetMouseButtonDown(0);
        bool right = Input.GetMouseButtonDown(1);
        if (!left && !right) return;
        if (PointerOverUI != null && PointerOverUI()) return;
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

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
        if (snapToCm) { x = Mathf.Round(x * 100f) / 100f; z = Mathf.Round(z * 100f) / 100f; }

        // 클램프된 위치에서 아래로 레이 → 바닥 또는 기존 화물 위에 정확히 안착
        float restY = BedTopY;
        float rayStartY = transform.position.y + 3f;
        if (Physics.Raycast(new Vector3(x, rayStartY, z), Vector3.down, out RaycastHit down, 5f))
            restY = down.point.y;

        Vector3 center = new Vector3(x, restY + ext.y + 0.001f, z);
        it.go.transform.position = center;
        it.data.worldPos = center;

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
            cam.transform.position = aim + Vector3.up * dist;
            cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        }
        else
        {
            cam.transform.position = aim + new Vector3(0f, dist * 0.7f, -dist);
            cam.transform.LookAt(aim);
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

    /// <summary>배치 자동화용: Assets/Data/Cases/case_타임스탬프.json 로 케이스 누적 저장.</summary>
    public void SaveCase()
    {
        string dir = Path.Combine(Application.dataPath, "Data", "Cases");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"case_{System.DateTime.Now:yyyyMMdd_HHmmss}.json");
        SaveLayout(path);
    }

    public void LoadLayout() => LoadLayout(ResolvedPath);

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
