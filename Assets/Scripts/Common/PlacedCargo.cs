using System;
using UnityEngine;

/// <summary>
/// 적재함에 놓인 화물 1개(데이터). 물리 중에는 worldPos가 실시간으로 갱신된다.
/// secured=true면 결박(고정)되어 물리에서 움직이지 않는다 = 설계서의 고정방식.
/// </summary>
[Serializable]
public class PlacedCargo
{
    public CargoType type;

    /// <summary>화물 중심의 월드 좌표 (CoG 계산용).</summary>
    public Vector3 worldPos;

    /// <summary>결박(고정) 여부. 물리 모드에서 고정 화물은 움직이지 않음.</summary>
    public bool secured;

    public PlacedCargo(CargoType type, Vector3 worldPos, bool secured = false)
    {
        this.type = type;
        this.worldPos = worldPos;
        this.secured = secured;
    }
}
