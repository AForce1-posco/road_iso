using UnityEngine;

/// <summary>
/// 정적 씬 부트스트랩. 빈 GameObject에 이것 하나만 붙이고,
/// 인스펙터에서 이 컴포넌트 제목을 **우클릭 → "씬에 생성 (Build)"** 하면
/// CargoPlacer + CoG마커 + 지지영역 + 대시보드가 **씬에 실제 오브젝트로** 생성돼
/// 편집 모드에서 바로 보이고 편집된다 (Play 불필요).
///
/// - 트레이 크기 등은 생성된 StaticSceneRoot의 **CargoPlacer 인스펙터에서 직접** 조절 (기본 실측 21×62).
/// - Play 시: 이미 생성돼 있으면 **그대로 재사용**(트레이 중복 생성 없음). 없으면 런타임 생성(하위호환).
/// </summary>
public class StaticSceneSetup : MonoBehaviour
{
    [Header("카메라 시야 좌우 치우침 (우측 패널 보정)")]
    [Range(0f, 0.5f)] public float panelShift = 0.15f;

    [Header("불러오기 (Play 후 대시보드 '불러오기' 버튼으로 로드)")]
    [Tooltip("불러올 파일명 (확장자 생략 가능). 비우면 로드 안 함.")]
    public string loadFileName = "";
    [Tooltip("체크=TestCases 폴더, 해제=Cases 폴더")]
    public bool loadFromTestFolder = false;
    [Tooltip("체크 시 Play하자마자 자동으로 불러옴")]
    public bool autoLoadOnPlay = false;

    void Awake()
    {
        // 편집 모드에서 미리 Build 해뒀으면 재사용 → Play 시 중복 생성 방지
        if (GameObject.Find("StaticSceneRoot") != null) return;
        EnsureCameraAndLight();
        CreateRoot();
    }

    [ContextMenu("씬에 생성 (Build)")]
    public void BuildInScene()
    {
        var old = GameObject.Find("StaticSceneRoot");
        if (old != null) DestroyImmediate(old);   // 재실행 시 깨끗이 다시
        EnsureCameraAndLight();
        var placer = CreateRoot();
        placer.RebuildVisuals();                  // 편집 모드에서 트레이 즉시 그림
        Debug.Log("[StaticSceneSetup] 씬에 생성 완료 — StaticSceneRoot의 CargoPlacer에서 트레이 크기 편집 가능. 씬 저장(Ctrl+S) 하세요.");
    }

    [ContextMenu("생성물 제거 (Clear)")]
    public void ClearScene()
    {
        var old = GameObject.Find("StaticSceneRoot");
        if (old != null) DestroyImmediate(old);
    }

    private void EnsureCameraAndLight()
    {
        if (Camera.main == null)
        {
            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            camGo.tag = "MainCamera";
        }
        if (FindObjectOfType<Light>() == null)
        {
            var lightGo = new GameObject("Directional Light");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            l.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }
    }

    private CargoPlacer CreateRoot()
    {
        var root = new GameObject("StaticSceneRoot");

        var placer = root.AddComponent<CargoPlacer>();
        placer.panelShift = panelShift;
        placer.loadFileName = loadFileName;
        placer.loadFromTestFolder = loadFromTestFolder;
        placer.autoLoadOnPlay = autoLoadOnPlay;
        // 트레이 크기는 CargoPlacer 자체 기본값(실측 21×62) 사용 → 이후 인스펙터에서 조절
        if (placer.useDefaultSupports)
            placer.supports = SupportConfig.Default(placer.bedWidthX, placer.bedLengthZ);

        var cog = root.AddComponent<CoGMarkerController>();
        cog.placer = placer;

        var zone = root.AddComponent<SafetyZoneVisualizer>();
        zone.placer = placer;

        var ui = root.AddComponent<StaticDashboardUGUI>();
        ui.placer = placer;
        ui.cogMarker = cog;
        ui.safetyZone = zone;

        return placer;
    }
}
