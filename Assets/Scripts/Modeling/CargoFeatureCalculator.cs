using UnityEngine;

/// <summary>
/// CargoBedLoader.Loaded 리스트로부터 정적 위험도 모델(StaticRiskModel)에 필요한
/// 13개 피처를 계산. 화물 치수는 실제 Collider.bounds에서 런타임에 직접 읽음
/// (CargoType에 치수 필드가 없어도 동작).
/// </summary>
public class CargoFeatureCalculator : MonoBehaviour
{
    [Header("참조 (비우면 자동 검색)")]
    public CargoBedLoader cargoLoader;
    public VehicleController vehicle;
    public StaticRiskModel staticRiskModel;

    [Header("이번 주행 조건 (Inspector에서 직접 입력)")]
    public float targetSpeedKmh = 60f;
    public float roadBankAngleDeg = 0f;
    public float roadSlopeDeg = 0f;

    void Awake()
    {
        if (cargoLoader == null) cargoLoader = FindObjectOfType<CargoBedLoader>();
        if (vehicle == null) vehicle = FindObjectOfType<VehicleController>();
        if (staticRiskModel == null) staticRiskModel = GetComponent<StaticRiskModel>();
    }

    /// <summary>
    /// 화물이 로드된 직후 호출. 13개 피처 계산 후 StaticRiskModel로 예측값 반환.
    /// </summary>
    public float ComputeAndPredict()
    {
        if (cargoLoader == null || cargoLoader.Loaded.Count == 0)
        {
            Debug.LogWarning("화물이 로드되지 않음 — 계산 불가");
            return 0f;
        }

        float totalMass = 0f;
        Vector3 weightedPos = Vector3.zero;  // bedAnchor(트럭) 기준 로컬 좌표
        float maxHeight = 0f;
        float inertiaXX = 0f, inertiaYY = 0f, inertiaZZ = 0f;

        // 1차 패스: 총질량, 무게중심(CoG), 최고높이
        foreach (var c in cargoLoader.Loaded)
        {
            if (c.go == null || c.type == null) continue;
            float mass = c.type.massKg * cargoLoader.massScale;
            Vector3 localPos = cargoLoader.bedAnchor.InverseTransformPoint(c.go.transform.position);

            totalMass += mass;
            weightedPos += mass * localPos;

            Collider col = c.go.GetComponent<Collider>();
            if (col != null)
            {
                float topY = localPos.y + (col.bounds.size.y * 0.5f);
                if (topY > maxHeight) maxHeight = topY;
            }
        }

        Vector3 cog = totalMass > 0f ? weightedPos / totalMass : Vector3.zero;

        // 2차 패스: CoG 기준 관성모멘트 (박스 근사: I = m/12 * (dims^2 합) + 평행축 정리)
        foreach (var c in cargoLoader.Loaded)
        {
            if (c.go == null || c.type == null) continue;
            float mass = c.type.massKg * cargoLoader.massScale;
            Vector3 localPos = cargoLoader.bedAnchor.InverseTransformPoint(c.go.transform.position);
            Vector3 r = localPos - cog;  // CoG 기준 상대위치

            Collider col = c.go.GetComponent<Collider>();
            Vector3 size = col != null ? col.bounds.size : Vector3.one * 0.1f;

            // 박스 자체의 무게중심 기준 관성모멘트 (x=폭, y=높이, z=길이 가정)
            float ixx = mass / 12f * (size.y * size.y + size.z * size.z);
            float iyy = mass / 12f * (size.x * size.x + size.z * size.z);
            float izz = mass / 12f * (size.x * size.x + size.y * size.y);

            // 평행축 정리: 트럭 CoG 기준으로 이동
            ixx += mass * (r.y * r.y + r.z * r.z);
            iyy += mass * (r.x * r.x + r.z * r.z);
            izz += mass * (r.x * r.x + r.y * r.y);

            inertiaXX += ixx;
            inertiaYY += iyy;
            inertiaZZ += izz;
        }

        float[] features = {
            targetSpeedKmh,
            roadBankAngleDeg,
            roadSlopeDeg,
            vehicle != null ? 3500f : 3500f,  // VehicleBaseMassKg — 필요시 VehicleController에서 직접 참조로 교체
            cargoLoader.Loaded.Count,
            totalMass,
            cog.x, cog.y, cog.z,
            maxHeight,
            inertiaXX, inertiaYY, inertiaZZ
        };

        float risk = staticRiskModel != null ? staticRiskModel.Predict(features) : 0f;
        Debug.Log($"[정적 위험도 예측] CoG=({cog.x:F3},{cog.y:F3},{cog.z:F3}) 최고높이={maxHeight:F3}m 총질량={totalMass:F0}kg → 예측 위험도={risk:F3}");
        return risk;
    }
}