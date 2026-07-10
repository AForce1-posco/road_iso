using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 지지영역(4점 로드셀을 잇는 사각형)과 CoG 바닥 투영점을 그린다.
/// CoG 투영점이 지지영역 안이면 초록, 가장자리면 노랑, 벗어나면 빨강(전복).
/// LTR보다 물리적으로 직접적인 정적 전복 기준.
/// </summary>
public class SafetyZoneVisualizer : MonoBehaviour
{
    public CargoPlacer placer;

    [Header("판정 여유 (m)")]
    public float marginSafe = 0.03f;    // 이 이상 안쪽이면 안전(초록)
    public float lineWidth = 0.004f;
    public float projScale = 0.035f;    // 투영점 크기

    public Color safeColor = new Color(0.30f, 0.85f, 0.45f);
    public Color cautionColor = new Color(0.95f, 0.75f, 0.25f);
    public Color dangerColor = new Color(0.95f, 0.30f, 0.30f);

    private LineRenderer zoneLine;
    private GameObject projMarker;
    private Material zoneMat, projMat;

    void Start()
    {
        BuildZoneLine();
        BuildProjMarker();

        if (placer != null)
        {
            placer.OnLayoutChanged += HandleLayoutChanged;
            HandleLayoutChanged(placer.Placed);
        }
    }

    void OnDestroy()
    {
        if (placer != null) placer.OnLayoutChanged -= HandleLayoutChanged;
    }

    public void SetVisible(bool visible)
    {
        if (zoneLine != null) zoneLine.gameObject.SetActive(visible);
        if (projMarker != null) projMarker.SetActive(visible);
    }

    private void BuildZoneLine()
    {
        var go = new GameObject("SupportZone");
        go.transform.SetParent(transform, false);
        zoneLine = go.AddComponent<LineRenderer>();
        zoneLine.useWorldSpace = true;
        zoneLine.loop = true;
        zoneLine.widthMultiplier = lineWidth;
        zoneLine.numCornerVertices = 2;
        zoneMat = MakeUnlit(safeColor);
        zoneLine.material = zoneMat;

        if (placer == null) return;
        SupportConfig s = placer.WorldSupports;
        float y = placer.BedTopY + 0.002f;
        Vector2[] poly = LoadCalculator.SupportPolygon(s);
        zoneLine.positionCount = poly.Length;
        for (int i = 0; i < poly.Length; i++)
            zoneLine.SetPosition(i, new Vector3(poly[i].x, y, poly[i].y));
    }

    private void BuildProjMarker()
    {
        projMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projMarker.name = "CoGProjection";
        projMarker.transform.SetParent(transform, false);
        projMarker.transform.localScale = new Vector3(projScale, projScale * 0.15f, projScale);
        Destroy(projMarker.GetComponent<Collider>());
        projMat = MakeUnlit(safeColor);
        projMarker.GetComponent<MeshRenderer>().sharedMaterial = projMat;
    }

    private void HandleLayoutChanged(IReadOnlyList<PlacedCargo> cargo)
    {
        if (placer == null) return;

        float empty = placer.EmptyMassKg;
        Vector3 emptyCoG = new Vector3(
            placer.TrayCenterX,   // 빈 적재함 CoG = 트레이 중심 (코너 원점)
            (placer.transform.position.y + placer.BedTopY) * 0.5f,
            placer.TrayCenterZ);

        Vector3 cog = LoadCalculator.ComputeCoG(cargo, empty, emptyCoG);
        float total = LoadCalculator.ComputeTotalMass(cargo, empty);
        if (total <= 0f)
            cog = emptyCoG;

        Vector2 cogXZ = new Vector2(cog.x, cog.z);
        float margin = LoadCalculator.StabilityMargin(cogXZ, placer.WorldSupports);
        Color col = margin < 0f ? dangerColor : (margin < marginSafe ? cautionColor : safeColor);

        if (projMarker != null)
        {
            projMarker.transform.position = new Vector3(cog.x, placer.BedTopY + 0.003f, cog.z);
            projMat.color = col;
        }
        if (zoneMat != null) zoneMat.color = col;
    }

    private static Material MakeUnlit(Color c)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        return new Material(sh) { color = c };
    }
}
