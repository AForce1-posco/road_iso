using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 무게중심(CoG)을 빨간 구체로 표시한다. CargoPlacer의 배치 변경 이벤트를 구독해
/// LoadCalculator.ComputeCoG로 위치를 계산·갱신한다. 동적 씬에서도 그대로 재사용 가능.
/// </summary>
public class CoGMarkerController : MonoBehaviour
{
    public CargoPlacer placer;
    public float markerScale = 0.05f;
    public Material markerMaterial; // 없으면 placer.cogMarkerColor로 생성

    private GameObject marker;

    void Start()
    {
        marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "CoGMarker";
        marker.transform.SetParent(transform, false);
        marker.transform.localScale = Vector3.one * markerScale;
        Destroy(marker.GetComponent<Collider>());

        var mr = marker.GetComponent<MeshRenderer>();
        if (markerMaterial != null)
        {
            mr.sharedMaterial = markerMaterial;
        }
        else
        {
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh);
            m.color = placer != null ? placer.cogMarkerColor : Color.red;
            mr.sharedMaterial = m;
        }

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

    public void SetMarkerVisible(bool visible)
    {
        if (marker != null) marker.SetActive(visible);
    }

    private void HandleLayoutChanged(IReadOnlyList<PlacedCargo> cargo)
    {
        if (placer == null || marker == null) return;

        float empty = placer.EmptyMassKg;
        Vector3 emptyCoG = new Vector3(
            placer.TrayCenterX,   // 빈 적재함 CoG = 트레이 중심 (코너 원점)
            (placer.transform.position.y + placer.BedTopY) * 0.5f,
            placer.TrayCenterZ);

        float total = LoadCalculator.ComputeTotalMass(cargo, empty);
        if (total <= 0f)
        {
            // 화물이 없으면 평판 중심에 표시
            marker.transform.position = emptyCoG;
            return;
        }

        Vector3 cog = LoadCalculator.ComputeCoG(cargo, empty, emptyCoG);
        marker.transform.position = cog;
    }
}
