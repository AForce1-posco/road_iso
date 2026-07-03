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

    void Awake()
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

        var root = new GameObject("StaticSceneRoot");

        var placer = root.AddComponent<CargoPlacer>();
        placer.panelShift = panelShift;
        placer.bedWidthX = bedWidthX;
        placer.bedLengthZ = bedLengthZ;
        placer.wallHeight = wallHeight;
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
    }
}
