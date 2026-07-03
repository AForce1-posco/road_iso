using System;
using UnityEngine;

/// <summary>
/// 화물 형상.
/// Box=상자, Drum=세로 원통, Pipe=길이(Z)로 누운 원통,
/// Coil=중공 원판(도넛, 축=세로. 구멍은 시각 표현 — 물리 콜라이더는 convex라 막혀 있음),
/// Sack=자루(톤백: 둥근 비주얼 + 안 구르는 박스 콜라이더로 근사).
/// </summary>
public enum CargoShape { Box, Drum, Pipe, Coil, Sack }

/// <summary>
/// 화물 한 종류의 정의. CargoPlacer의 배열을 비워두면 CargoCatalog 기본값(실측 CSV)이 자동 적용된다.
/// 질량은 실제 1:10 목업 분동값.
/// </summary>
[Serializable]
public class CargoType
{
    [Tooltip("화물 ID (예: B-001)")]
    public string id = "";

    [Tooltip("화물 이름 (저장 파일에서 참조 키로도 사용)")]
    public string name = "Cargo";

    [Tooltip("질량 (kg). 목업 분동 실측값")]
    public float massKg = 0.1f;

    [Tooltip("크기 (m). x=폭/외경, y=높이, z=길이/깊이. Pipe는 z=길이, Coil은 x=z=외경")]
    public Vector3 sizeM = new Vector3(0.1f, 0.1f, 0.1f);

    [Tooltip("형상")]
    public CargoShape shape = CargoShape.Box;

    [Tooltip("Coil 전용: 내경 (m). 도넛 구멍 지름")]
    public float innerDiameterM = 0f;

    [Tooltip("실제 목업 재고 수 (0=미기재). 배치 제한은 없고 UI 안내용")]
    public int stockCount = 0;

    [Tooltip("비고 (예: 3개 번들 처리)")]
    public string note = "";

    [Tooltip("적용할 머티리얼 (색/Metallic/Roughness). 비우면 종류별 자동 색")]
    public Material material;
}
