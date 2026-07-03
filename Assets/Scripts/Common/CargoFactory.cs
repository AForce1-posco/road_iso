using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 화물 GameObject(메시·콜라이더·머티리얼)를 만드는 공용 static 헬퍼.
/// 정적 씬(CargoPlacer)과 동적 씬(CargoBedLoader)이 같은 로직을 쓰도록 단일 소스.
/// Rigidbody는 붙이지 않음(호출자가 상황에 맞게 추가). 머티리얼은 인스턴스로 반환(개별 틴트용).
/// </summary>
public static class CargoFactory
{
    /// <summary>type을 scale배 크기로 만든 화물 오브젝트를 반환. 원통류는 convex MeshCollider(굴러감).</summary>
    public static GameObject Create(CargoType type, float scale, Color fallbackColor)
    {
        Vector3 s = type.sizeM * scale;
        GameObject go;
        switch (type.shape)
        {
            case CargoShape.Drum: // 세로 원통 (Unity 실린더는 높이 2 → y는 절반)
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.transform.localScale = new Vector3(s.x, s.y * 0.5f, s.z);
                ReplaceWithConvexMeshCollider(go);
                break;
            case CargoShape.Pipe: // 길이(Z)로 누운 원통: X축 90° 회전 → 로컬 Y가 월드 Z
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.transform.localScale = new Vector3(s.x, s.z * 0.5f, s.y);
                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                ReplaceWithConvexMeshCollider(go);
                break;
            case CargoShape.Coil: // 중공 도넛(축=세로). 구멍은 시각 표현, 콜라이더는 convex(막힘)
                go = CreateCoil(type, scale);
                break;
            case CargoShape.Sack: // 톤백: 캡슐 비주얼 + 안 구르는 박스 콜라이더 (강체 근사)
                go = CreateSack(s);
                break;
            default: // Box
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.localScale = s;
                break;
        }
        go.name = $"Cargo_{type.name}";

        var mr = go.GetComponent<MeshRenderer>();
        Material src = type.material != null ? type.material : MakeLit(fallbackColor);
        Material inst = new Material(src); // 인스턴스 (개별 틴트용)
        if (Application.isPlaying) mr.material = inst;
        else mr.sharedMaterial = inst; // 에디트 모드(인벤토리 진열)에서는 sharedMaterial만 허용

        return go;
    }

    /// <summary>에디트 모드(인벤토리)에서는 DestroyImmediate가 필요.</summary>
    private static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Object.Destroy(o);
        else Object.DestroyImmediate(o);
    }

    private static void ReplaceWithConvexMeshCollider(GameObject go)
    {
        SafeDestroy(go.GetComponent<Collider>());
        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
        mc.convex = true;
    }

    private static GameObject CreateSack(Vector3 s)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule); // 메시 기준 높이 2, 지름 1
        go.transform.localScale = new Vector3(s.x, s.y * 0.5f, s.z);
        SafeDestroy(go.GetComponent<Collider>());
        var bc = go.AddComponent<BoxCollider>();
        bc.size = new Vector3(1f, 2f, 1f); // 로컬 메시 바운드에 맞춤 → 월드에서 sizeM
        return go;
    }

    private static GameObject CreateCoil(CargoType type, float scale)
    {
        float outerR = Mathf.Max(0.001f, type.sizeM.x * 0.5f * scale);
        float innerR = Mathf.Clamp(type.innerDiameterM * 0.5f * scale, 0.0005f, outerR * 0.9f);
        float height = Mathf.Max(0.001f, type.sizeM.y * scale);

        var go = new GameObject("Coil");
        var mf = go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        mf.sharedMesh = BuildTubeMesh(outerR, innerR, height, 32);

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mf.sharedMesh;
        mc.convex = true;
        return go;
    }

    /// <summary>중공 원통(튜브) 메시. 축=Y, 원점=중심. 옆면 바깥/안쪽 + 위/아래 고리면.</summary>
    private static Mesh BuildTubeMesh(float outerR, float innerR, float height, int segments)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tris = new List<int>();
        float h = height * 0.5f;

        // 면마다 정점을 분리해 법선을 깔끔하게 유지. 정점 순서: 각 면에서 [아래i, 위i] × (segments+1)
        // Unity 규칙: Cross(v1-v0, v2-v0)가 전면 법선.
        for (int face = 0; face < 4; face++) // 0=바깥옆, 1=안쪽옆, 2=윗면, 3=아랫면
        {
            int baseIndex = verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                switch (face)
                {
                    case 0: // 바깥 옆면: 아래·위, 법선=바깥
                        verts.Add(dir * outerR + Vector3.down * h);
                        verts.Add(dir * outerR + Vector3.up * h);
                        norms.Add(dir); norms.Add(dir);
                        break;
                    case 1: // 안쪽 옆면: 아래·위, 법선=중심 방향
                        verts.Add(dir * innerR + Vector3.down * h);
                        verts.Add(dir * innerR + Vector3.up * h);
                        norms.Add(-dir); norms.Add(-dir);
                        break;
                    case 2: // 윗면 고리: 바깥·안, 법선=위
                        verts.Add(dir * outerR + Vector3.up * h);
                        verts.Add(dir * innerR + Vector3.up * h);
                        norms.Add(Vector3.up); norms.Add(Vector3.up);
                        break;
                    default: // 아랫면 고리: 바깥·안, 법선=아래
                        verts.Add(dir * outerR + Vector3.down * h);
                        verts.Add(dir * innerR + Vector3.down * h);
                        norms.Add(Vector3.down); norms.Add(Vector3.down);
                        break;
                }
            }
            for (int i = 0; i < segments; i++)
            {
                int a0 = baseIndex + i * 2;      // 이번 세그먼트 [0]=아래/바깥, [1]=위/안
                int b0 = baseIndex + (i + 1) * 2; // 다음 세그먼트
                switch (face)
                {
                    case 0: // (위0, 위1, 아래1) (위0, 아래1, 아래0)
                        tris.Add(a0 + 1); tris.Add(b0 + 1); tris.Add(b0);
                        tris.Add(a0 + 1); tris.Add(b0); tris.Add(a0);
                        break;
                    case 1: // 안쪽은 반대 감김
                        tris.Add(b0 + 1); tris.Add(a0 + 1); tris.Add(a0);
                        tris.Add(b0 + 1); tris.Add(a0); tris.Add(b0);
                        break;
                    case 2: // 윗면: (바깥0, 안0, 안1) (바깥0, 안1, 바깥1)
                        tris.Add(a0); tris.Add(a0 + 1); tris.Add(b0 + 1);
                        tris.Add(a0); tris.Add(b0 + 1); tris.Add(b0);
                        break;
                    default: // 아랫면: 반대 감김
                        tris.Add(a0); tris.Add(b0); tris.Add(b0 + 1);
                        tris.Add(a0); tris.Add(b0 + 1); tris.Add(a0 + 1);
                        break;
                }
            }
        }

        var mesh = new Mesh { name = "CoilTube" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>URP Lit(없으면 Standard) 단색 머티리얼. Built-in 파이프라인이면 자동으로 Standard.</summary>
    public static Material MakeLit(Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        return new Material(sh) { color = color };
    }
}
