using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 적재 배치 저장 스키마 (정적=저장, 동적=로드 공용).
/// 설계 의도(무슨 화물을 어디 놨나)만 담고, 로드셀 위치·질량·임계값 등 설정은 담지 않는다.
/// 화물은 이름으로 참조 → 배열 순서가 바뀌어도 매칭. 위치·회전은 트레이 로컬 좌표.
/// </summary>
[Serializable]
public class CargoLayoutBed
{
    public float widthX, lengthZ, wallHeight;
}

[Serializable]
public class CargoLayoutEntry
{
    public string type;        // 화물 종류 이름(안정 참조)
    public Vector3 localPos;   // 트레이 기준 로컬 좌표
    public Vector3 localEuler; // 트레이 기준 로컬 회전
    public bool secured;       // 결박(고정) 여부
}

[Serializable]
public class CargoLayoutFile
{
    public int version = 1;

    // 생성기가 미리 계산해 저장하는 배치 요약(있으면 그대로 사용). 실축(m, kg). 구버전 JSON엔 없을 수 있음.
    public Vector3 cog;
    public float maxHeight;
    public float totalMass;
    public int cargoCount;

    public CargoLayoutBed bed;
    public List<CargoLayoutEntry> cargo = new List<CargoLayoutEntry>();

    public static string DefaultPath =>
        System.IO.Path.Combine(Application.persistentDataPath, "staging_export.json");
}
