using UnityEngine;

/// <summary>
/// 정적 씬 원클릭 부트스트랩. 빈 GameObject에 이것 하나만 붙이면
/// 카메라·조명 확인 후 CargoPlacer + CoG마커 + 지지영역 + uGUI 대시보드를 자동 구성한다.
/// (인스펙터 수동 연결 불필요. 설정을 바꾸고 싶으면 생성된 StaticSceneRoot에서 조절)
/// </summary>
public class StaticSceneSetup : MonoBehaviour
{
    [Header("카메라 시야 좌우 치우침 (우측 패널 보정)")]
    [Range(0f, 0.5f)] public float panelShift = 0.15f;

    [Header("트레이 크기 (m) — 기본 64×24cm, 벽 6cm")]
    public float bedWidthX = 0.64f;
    public float bedLengthZ = 0.24f;
    public float wallHeight = 0.06f;

    [Header("불러오기 (Play 후 대시보드 '불러오기' 버튼으로 로드)")]
    [Tooltip("불러올 파일명 (확장자 생략 가능). 비우면 로드 안 함. 예: case03_left_heavy_pipes, test_2026...")]
    public string loadFileName = "";
    [Tooltip("체크=TestCases 폴더, 해제=Cases 폴더")]
    public bool loadFromTestFolder = false;
    [Tooltip("체크 시 Play하자마자 자동으로 불러옴 (버튼 안 눌러도 됨)")]
    public bool autoLoadOnPlay = false;

    private bool initialized;

    void Awake()
    {
        Initialize();
    }

    void Start()
    {
        Initialize();
    }

    private void Initialize()
    {
        if (initialized) return;
        initialized = true;

        Debug.Log("[StaticSceneSetup] initializing static scene bootstrap");

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

        var existingRoot = GameObject.Find("StaticSceneRoot");
        var root = existingRoot != null ? existingRoot : new GameObject("StaticSceneRoot");

        var placer = root.AddComponent<CargoPlacer>();
        placer.panelShift = panelShift;
        placer.bedWidthX = bedWidthX;
        placer.bedLengthZ = bedLengthZ;
        placer.wallHeight = wallHeight;
        placer.loadFileName = loadFileName;           // 부트스트랩에서 지정한 불러오기 대상 전달
        placer.loadFromTestFolder = loadFromTestFolder;
        placer.autoLoadOnPlay = autoLoadOnPlay;
        // AddComponent 시점에 placer.Awake가 기본 크기로 로드셀을 이미 계산했으므로 재적용
        if (placer.useDefaultSupports)
            placer.supports = SupportConfig.Default(bedWidthX, bedLengthZ);

        var cog = root.AddComponent<CoGMarkerController>();
        cog.placer = placer;

        var zone = root.AddComponent<SafetyZoneVisualizer>();
        zone.placer = placer;

        var ui = root.AddComponent<StaticDashboardUGUI>();
        ui.placer = placer;
        ui.cogMarker = cog;
        ui.safetyZone = zone;

        var agent = root.AddComponent<PlacementAgent>();
        agent.cols = 6;
        agent.rows = 16;
        agent.manifestMin = 3;
        agent.manifestMax = 5;
        agent.autoRunHeuristicEpisodes = false;
        agent.autoRunEpisodeCount = 10;
        agent.autoRunStepDelay = 0.01f;
        agent.verboseLog = true;

        var runner = root.AddComponent<PlacementLoopRunner>();
        runner.agent = agent;
        runner.episodeCount = 10;
        runner.stepDelay = 0.01f;
        runner.runOnStart = false;

        var recorder = root.AddComponent<PlacementTrainingRecorder>();
        recorder.agent = agent;

        var chart = root.AddComponent<PlacementTrainingChart>();
        recorder.chart = chart;

        var summary = root.AddComponent<PlacementRunSummary>();
        summary.recorder = recorder;

        var optimizer = root.AddComponent<PlacementGAOptimizer>();
        optimizer.agent = agent;
        optimizer.populationSize = 6;
        optimizer.generations = 3;
        optimizer.mutationRate = 0.3f;

        Debug.Log("[StaticSceneSetup] bootstrap completed");
    }
}
