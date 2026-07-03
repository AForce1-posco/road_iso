using System;
using UnityEngine;

/// <summary>
/// 로드셀 1개의 수평 위치. 평판 바닥이라 높이(Y)는 필요 없어 Vector2(x, z)만.
/// 좌표계: x=폭(좌-/우+), z=길이(후-/전+), 적재함 중심이 원점.
/// </summary>
[Serializable]
public struct SupportPoint
{
    public Vector2 position; // (x, z)

    public SupportPoint(float x, float z)
    {
        position = new Vector2(x, z);
    }
}

/// <summary>
/// 4개 로드셀 배치. 인스펙터에서 실측 위치로 조절한다.
/// FL=전좌, FR=전우, RL=후좌, RR=후우 (front = +z, right = +x).
/// </summary>
[Serializable]
public struct SupportConfig
{
    public SupportPoint fl;
    public SupportPoint fr;
    public SupportPoint rl;
    public SupportPoint rr;

    /// <summary>네 지지점의 평면 중심(반력 분배 기준점).</summary>
    public Vector2 Centroid =>
        (fl.position + fr.position + rl.position + rr.position) * 0.25f;

    /// <summary>중심에서 좌우(x) 평균 거리 = bilinear 공식의 a.</summary>
    public float HalfTrack
    {
        get
        {
            Vector2 c = Centroid;
            return (Mathf.Abs(fl.position.x - c.x) + Mathf.Abs(fr.position.x - c.x)
                  + Mathf.Abs(rl.position.x - c.x) + Mathf.Abs(rr.position.x - c.x)) * 0.25f;
        }
    }

    /// <summary>중심에서 전후(z) 평균 거리 = bilinear 공식의 b.</summary>
    public float HalfBase
    {
        get
        {
            Vector2 c = Centroid;
            return (Mathf.Abs(fl.position.y - c.y) + Mathf.Abs(fr.position.y - c.y)
                  + Mathf.Abs(rl.position.y - c.y) + Mathf.Abs(rr.position.y - c.y)) * 0.25f;
        }
    }

    /// <summary>적재함 크기로부터 네 모서리 안쪽에 기본 배치를 만든다.</summary>
    public static SupportConfig Default(float bedWidthX, float bedLengthZ, float inset = 0.05f)
    {
        float ax = bedWidthX * 0.5f - inset;
        float bz = bedLengthZ * 0.5f - inset;
        return new SupportConfig
        {
            fl = new SupportPoint(-ax, bz),
            fr = new SupportPoint(ax, bz),
            rl = new SupportPoint(-ax, -bz),
            rr = new SupportPoint(ax, -bz),
        };
    }
}
