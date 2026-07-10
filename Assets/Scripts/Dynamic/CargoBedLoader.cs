using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// staging_export.json(정적 배치)을 읽어 트럭 적재함(bedAnchor)에 화물을 싣는다.
/// 정적 1:10 배치를 scale배로 확대해 트럭에 올림(전복 임계는 스케일 불변).
/// 자유 화물 = 동적 Rigidbody(쏠림/굴러감, 접촉으로 트럭에 작용),
/// 고정 화물 = FixedJoint로 트럭에 결박(같이 움직이며 하중 기여).
/// </summary>
public class CargoBedLoader : MonoBehaviour
{
    [Header("트럭 연결")]
    public Transform bedAnchor;    // 적재함 바닥 rear-left 코너 = 원점(트럭 본체의 자식). 트레이는 여기서 +x(우)·+z(앞)로 뻗음
    public Rigidbody truckBody;    // 고정 화물을 결박할 트럭 Rigidbody

    [Header("화물 종류 (정적과 동일하게)")]
    public CargoType[] cargoTypes;

    [Header("스케일")]
    public float scale = 10f;      // 위치·크기 배율 (1:10 → 실물)
    public float massScale = 100f; // 질량 배율 (화물이 트럭에 유의미하게 작용하도록 튜닝)

    [Header("배치 선택 (우선순위: layoutPath > caseName > Cases 폴더 첫 파일)")]
    [Tooltip("케이스 파일명 (확장자 생략 가능). 예: case03_left_heavy_pipes")]
    public string caseName = "";
    [Tooltip("체크=TestCases 폴더에서 caseName 찾기, 해제=Cases 폴더")]
    public bool useTestFolder = false;
    [Tooltip("특정 파일을 직접 지정할 때만 사용 (정적 씬 저장본을 쓰려면 여기에 경로 입력)")]
    public string layoutPath = "";

    [Header("적재함 트레이")]
    public bool buildTray = true;
    public Material trayMaterial;

    [Header("물리 재질 (착지 에너지 소산 — 강체에 없는 '쿵 자리잡기'를 마찰로 대신)")]
    [Tooltip("트레이·화물 표면 마찰 (높을수록 안 미끄러지고 착지 후 자리 잡음)")]
    public float surfaceFriction = 0.8f;
    [Tooltip("반발 계수 (0 = 안 튐)")]
    public float surfaceBounciness = 0f;

    private PhysicMaterial gripMat;

    /// <summary>트레이·화물 공용 접지 재질 (마찰↑·반발 0). 처음 요청 시 1회 생성.</summary>
    private PhysicMaterial GripMaterial()
    {
        if (gripMat == null)
        {
            gripMat = new PhysicMaterial("CargoGrip")
            {
                dynamicFriction = surfaceFriction,
                staticFriction = surfaceFriction,
                bounciness = surfaceBounciness,
                frictionCombine = PhysicMaterialCombine.Maximum, // 상대 표면과 만나도 높은 마찰 유지
                bounceCombine = PhysicMaterialCombine.Minimum,   // 상대가 튀어도 반발 0 유지
            };
        }
        return gripMat;
    }

    private void ApplyGrip(GameObject go)
    {
        foreach (Collider col in go.GetComponentsInChildren<Collider>())
            col.sharedMaterial = GripMaterial();
    }

    [Header("씬 인벤토리 (비우면 자동 검색)")]
    [Tooltip("지정/발견되면 CargoFactory 대신 인벤토리 진열품을 복제해 적재 — 씬에서 바꾼 모양이 반영됨")]
    public CargoInventory inventory;

    public class LoadedCargo
    {
        public CargoType type;
        public GameObject go;
        public Rigidbody rb;
        public bool secured;
        public Vector3 initialLocal; // bedAnchor 기준 초기 위치(이동거리 측정용)
    }

    public IReadOnlyList<LoadedCargo> Loaded => loaded;
    public string LastLoadedPath { get; private set; }
    /// <summary>트레이 바닥 반폭(x)·반길이(z), 스케일 적용된 m. 이탈 판정용.</summary>
    public Vector2 TrayHalfXZ { get; private set; } = new Vector2(1.05f, 3.05f); // 0.21×0.61 bed의 반치수(×scale10) — Load 전 초기값
    private readonly List<LoadedCargo> loaded = new List<LoadedCargo>();
    private Transform trayRoot;

    // 트럭 원래 질량·무게중심 (적재 반영 후 복원용)
    private bool truckCaptured;
    private float origTruckMass;
    private Vector3 origTruckCoM;

    private static readonly Color[] Palette =
    {
        new Color(0.30f, 0.55f, 0.85f), new Color(0.90f, 0.55f, 0.25f),
        new Color(0.35f, 0.72f, 0.45f), new Color(0.62f, 0.45f, 0.80f),
        new Color(0.30f, 0.72f, 0.72f), new Color(0.85f, 0.40f, 0.45f),
        new Color(0.85f, 0.75f, 0.35f),
    };

    public static string CasesDir => CargoPaths.CasesDir;

    public string ResolvedPath
    {
        get
        {
            // 1) 직접 경로 지정이 최우선
            if (!string.IsNullOrEmpty(layoutPath)) return layoutPath;

            // 2) 케이스 이름 지정 → Cases 또는 TestCases 폴더의 <이름>.json
            if (!string.IsNullOrEmpty(caseName))
            {
                string n = caseName.EndsWith(".json") ? caseName : caseName + ".json";
                string dir = useTestFolder ? CargoPaths.TestCasesDir : CargoPaths.CasesDir;
                return Path.Combine(dir, n);
            }

            // 3) 비워두면 Cases 폴더의 첫 파일(정렬순 = case01)
            if (Directory.Exists(CargoPaths.CasesDir))
            {
                string[] files = Directory.GetFiles(CargoPaths.CasesDir, "*.json");
                if (files.Length > 0)
                {
                    System.Array.Sort(files, System.StringComparer.Ordinal);
                    return files[0];
                }
            }

            // 4) 케이스가 하나도 없으면 정적 씬 저장본 폴백
            return CargoLayoutFile.DefaultPath;
        }
    }

    void Awake()
    {
        // 인스펙터에서 비워두면 정적 씬과 같은 실측 카탈로그 사용 → 이름 매칭 보장
        if (cargoTypes == null || cargoTypes.Length == 0)
            cargoTypes = CargoCatalog.CreateDefault();

        if (inventory == null) inventory = FindObjectOfType<CargoInventory>();

        // 자동 연결: 트럭 Rigidbody, 그리고 트럭 자식 중 이름이 "BedAnchor"인 Transform
        if (truckBody == null)
        {
            var vc = FindObjectOfType<VehicleController>();
            if (vc != null) truckBody = vc.GetComponent<Rigidbody>();
        }
        if (bedAnchor == null && truckBody != null)
        {
            foreach (Transform tr in truckBody.GetComponentsInChildren<Transform>())
                if (tr.name == "BedAnchor") { bedAnchor = tr; break; }
        }
    }

    /// <summary>저장 파일을 읽어 트럭에 적재. 성공 시 화물 수 반환, 실패 시 -1.</summary>
    public int Load() => Load(ResolvedPath);

    public int Load(string path)
    {
        if (bedAnchor == null) { Debug.LogError("bedAnchor 미지정"); return -1; }
        if (!File.Exists(path)) { Debug.LogWarning($"저장 파일 없음: {path}"); return -1; }

        CargoLayoutFile file = JsonUtility.FromJson<CargoLayoutFile>(File.ReadAllText(path));
        if (file == null || file.cargo == null) { Debug.LogWarning("불러오기 실패: 파싱 오류"); return -1; }

        LastLoadedPath = path;
        Clear();
        TrayHalfXZ = new Vector2(
            (file.bed != null ? file.bed.widthX : 0.21f) * 0.5f * scale,
            (file.bed != null ? file.bed.lengthZ : 0.61f) * 0.5f * scale);
        if (buildTray) BuildTray(file.bed);

        foreach (CargoLayoutEntry e in file.cargo)
        {
            CargoType t = FindType(e.type);
            if (t == null) { Debug.LogWarning($"화물 종류 '{e.type}' 없음 — 건너뜀"); continue; }

            // 인벤토리 진열품 복제 우선 (씬에서 커스텀한 모양 반영), 없으면 팩토리 생성
            GameObject go = inventory != null ? inventory.CreateInstance(t.name, scale) : null;
            if (go == null) go = CargoFactory.Create(t, scale, ColorFor(t));
            ApplyGrip(go);                              // 화물 콜라이더에 접지 재질 (마찰↑·반발 0)
            go.transform.SetParent(bedAnchor, false); // 트럭(bedAnchor)의 자식으로 유지 → 리셋·이동을 항상 따라감
            // 저장된 localEuler는 최종 회전(형상 기본회전 포함) → 직접 덮어씀
            go.transform.localRotation = Quaternion.Euler(e.localEuler);
            go.transform.localPosition = e.localPos * scale;
            Vector3 initLocal = go.transform.localPosition;

            Rigidbody rb = null;
            if (e.secured && truckBody != null)
            {
                // 고정 화물: Rigidbody 없이 콜라이더만 트럭(bedAnchor) 자식으로 유지 →
                // 트럭 몸체의 일부(compound collider)로 용접됨. 질량·CoM은 ApplyLoadToTruck에서 수동 합산.
                // (별도 Rigidbody가 없으니 nested-rigidbody로 트럭이 멈추거나 스폰 시 튕기는 문제 없음)
            }
            else
            {
                // 자유 화물: 우선 kinematic-부모연결(안정 배치) → 출발 전 ReleaseFreeCargo()에서 물리 해제.
                rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.01f, t.massKg * massScale);
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.isKinematic = true;
            }

            loaded.Add(new LoadedCargo
            {
                type = t, go = go, rb = rb, secured = e.secured, initialLocal = initLocal
            });
        }

        IgnoreTruckBodyCollisions();
        ApplyLoadToTruck();
        Debug.Log($"트럭 적재 완료: {loaded.Count}개 (scale ×{scale}, massScale ×{massScale})\n적재 파일: {path}");
        return loaded.Count;
    }

    /// <summary>
    /// 화물이 트럭 "몸체" 콜라이더에 겹쳐 스폰돼 튕겨나가는 것 방지.
    /// 트럭 몸체와는 충돌 무시, 트레이·바퀴 제외(트레이는 화물을 받쳐야 하므로).
    /// </summary>
    private void IgnoreTruckBodyCollisions()
    {
        if (truckBody == null) return;
        Collider[] bodyCols = truckBody.GetComponentsInChildren<Collider>();
        foreach (LoadedCargo c in loaded)
        {
            if (c.go == null) continue;
            Collider cargoCol = c.go.GetComponent<Collider>();
            if (cargoCol == null) continue;
            foreach (Collider bc in bodyCols)
            {
                if (bc is WheelCollider) continue;
                if (trayRoot != null && bc.transform.IsChildOf(trayRoot)) continue; // 트레이는 부딪혀야 함
                Physics.IgnoreCollision(cargoCol, bc);
            }
        }
    }

    /// <summary>커브 시작 시 호출: 자유(미고정) 화물을 물리 해제 → 실제로 쏠리고 굴러감. 고정 화물은 그대로.</summary>
    public void ReleaseFreeCargo()
    {
        int released = 0;
        foreach (LoadedCargo c in loaded)
        {
            if (c.secured || c.go == null || c.rb == null) continue;
            if (!c.rb.isKinematic) continue;
            c.go.transform.SetParent(null, true);          // 월드로 (트럭 좌표에서 독립)
            c.rb.isKinematic = false;
            c.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            if (truckBody != null)
                c.rb.velocity = truckBody.GetPointVelocity(c.go.transform.position); // 매끄러운 인계 (2020.3: velocity)
            released++;
        }
        if (released > 0) ApplyLoadToTruck(); // 해제된 게 있을 때만 트럭 무게중심 재계산 (전부 고정이면 중복 호출 안 함)
        Debug.Log($"자유 화물 물리 해제: {released}개");
    }

    public void Clear()
    {
        // 트럭 질량·무게중심 원복 (다음 적재를 깨끗한 기준에서 다시 계산)
        if (truckCaptured && truckBody != null)
        {
            truckBody.mass = origTruckMass;
            truckBody.centerOfMass = origTruckCoM;
        }
        foreach (LoadedCargo c in loaded) if (c.go != null) Destroy(c.go);
        loaded.Clear();
        if (trayRoot != null) Destroy(trayRoot.gameObject);
    }

    /// <summary>적재 화물의 총 질량·무게중심을 트럭 Rigidbody에 반영 → 배치별로 전복 거동이 달라짐.</summary>
    private void ApplyLoadToTruck()
    {
        if (truckBody == null) return;
        if (!truckCaptured)
        {
            origTruckMass = truckBody.mass;
            origTruckCoM = truckBody.centerOfMass;
            truckCaptured = true;
        }

        float m = origTruckMass;
        Vector3 weighted = origTruckMass * origTruckCoM; // 트럭 로컬 기준
        foreach (LoadedCargo c in loaded)
        {
            if (c.go == null || c.type == null) continue;
            // 물리 해제된 자유화물(동적 바디)만 제외 — 실제 접촉으로 트럭에 작용하므로 이중 계산 방지.
            // 고정 화물(rb==null, 트럭 몸체에 용접)과 아직 kinematic인 자유화물은 여기서 질량·CoM 합산.
            if (c.rb != null && !c.rb.isKinematic) continue;
            float cm = c.type.massKg * massScale;
            Vector3 localToTruck = truckBody.transform.InverseTransformPoint(c.go.transform.position);
            weighted += cm * localToTruck;
            m += cm;
        }
        truckBody.mass = m;
        truckBody.centerOfMass = weighted / m;
        Debug.Log($"적재 반영: 트럭질량 {origTruckMass:F0}→{m:F0}kg (화물총 {m - origTruckMass:F0}kg), CoM(local)={truckBody.centerOfMass}");
    }

    private void BuildTray(CargoLayoutBed bed)
    {
        float w = (bed != null ? bed.widthX : 0.21f) * scale;
        float l = (bed != null ? bed.lengthZ : 0.61f) * scale;
        float wallH = (bed != null ? bed.wallHeight : 0.06f) * scale;
        float floorT = 0.01f * scale;
        float wallT = 0.01f * scale;

        Material mat = trayMaterial != null ? trayMaterial : CargoFactory.MakeLit(new Color(0.22f, 0.24f, 0.28f));
        trayRoot = new GameObject("CargoTray").transform;
        trayRoot.SetParent(bedAnchor, false); // 트럭과 함께 움직임

        // rear-left 코너 원점: 트레이 바닥은 x∈[0,w]·z∈[0,l], 바닥 중심 = (w/2, ·, l/2)
        float cx = w * 0.5f, cz = l * 0.5f;
        MakeBox("Floor", new Vector3(cx, floorT * 0.5f, cz), new Vector3(w, floorT, l), mat);
        float wy = floorT + wallH * 0.5f;
        MakeBox("WallL", new Vector3(-wallT * 0.5f, wy, cz), new Vector3(wallT, wallH, l + 2 * wallT), mat);      // x=0 좌벽
        MakeBox("WallR", new Vector3(w + wallT * 0.5f, wy, cz), new Vector3(wallT, wallH, l + 2 * wallT), mat);  // x=w 우벽
        MakeBox("WallB", new Vector3(cx, wy, -wallT * 0.5f), new Vector3(w, wallH, wallT), mat);                 // z=0 뒤벽
        MakeBox("WallF", new Vector3(cx, wy, l + wallT * 0.5f), new Vector3(w, wallH, wallT), mat);              // z=l 앞벽
    }

    private void MakeBox(string name, Vector3 localPos, Vector3 scaleV, Material mat)
    {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = name;
        b.transform.SetParent(trayRoot, false);
        b.transform.localPosition = localPos;
        b.transform.localScale = scaleV;
        b.GetComponent<MeshRenderer>().sharedMaterial = mat;
        b.GetComponent<Collider>().sharedMaterial = GripMaterial(); // 트레이 바닥·벽 접지 재질
    }

    // Edit 모드: 트레이 윤곽선 미리보기 / Play 중: 트럭 실제 무게중심(CoM) 마젠타 구슬
    void OnDrawGizmos()
    {
        if (bedAnchor != null)
        {
            float w = 0.21f * scale, l = 0.61f * scale, h = 0.06f * scale;   // 실제 적재함 폭0.21×길이0.61
            float cx = w * 0.5f, cz = l * 0.5f;                              // rear-left 코너 원점 → 중심은 (w/2, ·, l/2)
            Gizmos.matrix = Matrix4x4.TRS(bedAnchor.position, bedAnchor.rotation, Vector3.one);
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireCube(new Vector3(cx, h * 0.5f, cz), new Vector3(w, h, l)); // 트레이 부피
            Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.9f);
            Gizmos.DrawWireCube(new Vector3(cx, 0f, cz), new Vector3(w, 0.001f, l));  // 바닥면
        }
        if (Application.isPlaying && truckBody != null)
        {
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(truckBody.worldCenterOfMass, 0.15f); // 적재 반영된 트럭 무게중심
        }
    }

    // ── 정렬 도구: bedAnchor(rear-left 코너)를 트럭 바퀴 좌우중심에 맞춰 트레이를 좌우 정중앙에 놓음 ──
    // 우클릭 컨텍스트 메뉴(에디트/플레이 모두). z(전후)는 안 건드림 — 짐칸 앞뒤 위치는 수동으로.
    [ContextMenu("Center Tray Laterally on Truck (wheels)")]
    public void CenterTrayLaterally()
    {
        Transform truck = truckBody != null ? truckBody.transform : null;
        if (truck == null) { var vc = FindObjectOfType<VehicleController>(); if (vc != null) truck = vc.transform; }
        if (bedAnchor == null && truck != null)
            foreach (Transform tr in truck.GetComponentsInChildren<Transform>())
                if (tr.name == "BedAnchor") { bedAnchor = tr; break; }
        if (truck == null || bedAnchor == null) { Debug.LogWarning("[정렬] 트럭/bedAnchor 못 찾음"); return; }

        var wheels = truck.GetComponentsInChildren<WheelCollider>();
        if (wheels.Length == 0) { Debug.LogWarning("[정렬] WheelCollider 없음 — 바퀴 기준 정렬 불가"); return; }

        // 바퀴 좌우 중심 (트럭 로컬 x) = 트럭 좌우 물리 중심선
        float sx = 0f;
        foreach (var w in wheels) sx += truck.InverseTransformPoint(w.transform.position).x;
        float wheelCenterX = sx / wheels.Length;

        // 현재 트레이 중심(=bedAnchor 로컬 (halfW,·,halfL))의 트럭-로컬 x. 치수는 기즈모와 동일 0.21×0.61.
        float halfW = 0.21f * scale * 0.5f, halfL = 0.61f * scale * 0.5f;
        Vector3 trayCenterWorld = bedAnchor.TransformPoint(new Vector3(halfW, 0f, halfL));
        float trayCenterX = truck.InverseTransformPoint(trayCenterWorld).x;

        // 좌우(truck.right)로 dx 이동 → 트레이중심 x == 바퀴중심 x
        float dx = wheelCenterX - trayCenterX;
        Vector3 before = bedAnchor.position;
        bedAnchor.position += truck.right * dx;
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEditor.EditorUtility.SetDirty(bedAnchor);
#endif
        Debug.Log($"[정렬] 트레이 좌우 중심 → 바퀴중심 정렬. 이동 Δx={dx:F4}m(스케일). 바퀴 {wheels.Length}개, 트레이중심x {trayCenterX:F3}→{wheelCenterX:F3}. bedAnchor {before} → {bedAnchor.position}");
    }

    private CargoType FindType(string name)
    {
        if (cargoTypes == null) return null;
        foreach (CargoType t in cargoTypes) if (t != null && t.name == name) return t;
        return null;
    }

    private Color ColorFor(CargoType t)
    {
        if (t != null && t.material != null) return t.material.color;
        int i = cargoTypes != null ? System.Array.IndexOf(cargoTypes, t) : -1;
        if (i < 0) i = 0;
        return Palette[i % Palette.Length];
    }
}
