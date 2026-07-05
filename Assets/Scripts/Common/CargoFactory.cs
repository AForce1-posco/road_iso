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
            case CargoShape.Pipe: // 누운 원통. 이름의 (N)이 2 이상이면 N개 다발로 비주얼 구성.
            {
                int bundle = ParseBundleCount(type.name);
                if (bundle >= 2)
                {
                    go = CreatePipeBundle(s, bundle); // 비주얼=N다발, 콜라이더=convex 헐 1개(물리 한 덩어리)
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    go.transform.localScale = new Vector3(s.x, s.z * 0.5f, s.y);
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    ReplaceWithConvexMeshCollider(go);
                }
                break;
            }
            case CargoShape.Coil: // 중공 도넛(축=세로). 구멍은 시각 표현, 콜라이더는 convex(막힘)
                go = CreateCoil(type, scale);
                break;
            case CargoShape.Sack: // 포대: 빵빵한 자루(둥근 상자) 비주얼 + 안 구르는 박스 콜라이더 (강체 근사)
                go = CreateSack(s);
                break;
            default: // Box
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.localScale = s;
                break;
        }
        go.name = $"Cargo_{type.name}";

        var mr = go.GetComponent<MeshRenderer>();
        // type.material이 지정돼 있으면 그걸 우선(텍스처 머티리얼 꽂으면 실사). 없으면 종류별 실물 재질.
        Material src = type.material != null ? type.material : MakeRealistic(type);
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
        // 빵빵한 자루형: 단위 둥근상자(±0.5) 메시를 sizeM 크기로. 모서리를 둥글려 "속이 찬 포대" 느낌.
        var go = new GameObject("Sack");
        var mf = go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        mf.sharedMesh = BuildBagMesh(6, 0.38f);
        go.transform.localScale = s;

        // 안 구르는 박스 콜라이더 (강체 근사). 메시 바운드가 ±0.5라 size=1 → 월드에서 sizeM
        var bc = go.AddComponent<BoxCollider>();
        bc.center = Vector3.zero;
        bc.size = Vector3.one;
        return go;
    }

    /// <summary>
    /// 빵빵한 자루(둥근 상자) 메시. 단위 큐브(±0.5)를 구면으로 blend 해 모서리를 둥글린다.
    /// round=0(각진 상자)~1(구). 면 중심은 ±0.5 유지 → 바운드=단위. +Y 면 하나 만들어 6방향 회전 결합.
    /// </summary>
    private static Mesh BuildBagMesh(int seg, float round)
    {
        Mesh face = BuildRoundedFaceY(seg, round);
        Quaternion[] rots =
        {
            Quaternion.identity,          Quaternion.Euler(180f, 0f, 0f),   // +Y, -Y
            Quaternion.Euler(0f, 0f, -90f), Quaternion.Euler(0f, 0f, 90f),  // +X, -X
            Quaternion.Euler(90f, 0f, 0f),  Quaternion.Euler(-90f, 0f, 0f), // +Z, -Z
        };
        var cs = new CombineInstance[6];
        for (int k = 0; k < 6; k++)
            cs[k] = new CombineInstance { mesh = face, transform = Matrix4x4.TRS(Vector3.zero, rots[k], Vector3.one) };

        var mesh = new Mesh { name = "Bag" };
        mesh.CombineMeshes(cs, true, true); // 회전 반영, 법선도 회전됨 → 6면 모두 바깥향
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>+Y 를 향하는 둥근 면 하나 (구면 blend). 법선 부호를 검사해 바깥(+Y)으로 맞춤.</summary>
    private static Mesh BuildRoundedFaceY(int seg, float round)
    {
        var verts = new List<Vector3>();
        int stride = seg + 1;
        for (int j = 0; j <= seg; j++)
            for (int i = 0; i <= seg; i++)
            {
                Vector3 v = new Vector3(-0.5f + i / (float)seg, 0.5f, -0.5f + j / (float)seg);
                verts.Add(Vector3.Lerp(v, v.normalized * 0.5f, round));
            }

        var tris = new List<int>();
        for (int j = 0; j < seg; j++)
            for (int i = 0; i < seg; i++)
            {
                int a = j * stride + i, b = a + 1, c = a + stride, d = c + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }

        var m = new Mesh();
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();

        // 바깥(+Y) 확인 — 반대면 감김 뒤집기
        Vector3 avg = Vector3.zero;
        foreach (var n in m.normals) avg += n;
        if (Vector3.Dot(avg, Vector3.up) < 0f)
        {
            for (int t = 0; t < tris.Count; t += 3) { int tmp = tris[t + 1]; tris[t + 1] = tris[t + 2]; tris[t + 2] = tmp; }
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
        }
        m.RecalculateBounds();
        return m;
    }

    /// <summary>이름 끝의 "(N)"에서 번들 개수를 읽는다. 없거나 파싱 실패 시 1.</summary>
    private static int ParseBundleCount(string name)
    {
        if (string.IsNullOrEmpty(name)) return 1;
        int open = name.LastIndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            string inner = name.Substring(open + 1, close - open - 1).Trim();
            if (int.TryParse(inner, out int n) && n >= 1) return n;
        }
        return 1;
    }

    /// <summary>
    /// 파이프 번들: 이름의 (N)만큼 얇은 파이프를 다발로 "보이게" 그린다.
    /// 방향 규칙은 단일 파이프와 동일(길이=로컬 Y, 단면=X·Z) → 저장된 회전값 그대로 호환.
    /// 콜라이더는 다발 전체를 감싸는 convex 헐 1개 = 물리는 한 덩어리(요청대로).
    /// </summary>
    private static GameObject CreatePipeBundle(Vector3 s, int bundle)
    {
        float length = s.z;                                     // 길이(로컬 Y로 눕힘)
        float R = Mathf.Max(0.0005f, Mathf.Min(s.x, s.y) * 0.5f); // 다발 외곽 반지름

        // 서브파이프 중심(단면 X·Z 평면)과 반지름 r — 원 안 원 패킹 근사
        var offsets = new List<Vector2>();
        float r;
        if (bundle <= 1) { r = R; offsets.Add(Vector2.zero); }
        else if (bundle == 2)
        {
            r = R * 0.5f;
            offsets.Add(new Vector2(-r, 0f));
            offsets.Add(new Vector2(r, 0f));
        }
        else if (bundle == 3)
        {
            r = R * 0.464f; float d = R - r;                    // 정삼각 배치
            for (int i = 0; i < 3; i++)
            {
                float a = (90f + i * 120f) * Mathf.Deg2Rad;
                offsets.Add(new Vector2(Mathf.Cos(a) * d, Mathf.Sin(a) * d));
            }
        }
        else                                                   // 4개 이상: 한 줄
        {
            r = R / bundle;
            for (int i = 0; i < bundle; i++)
                offsets.Add(new Vector2(-R + r + 2f * r * i, 0f));
        }

        // 임시 실린더 메시를 서브파이프마다 배치해 하나의 메시로 결합 (실린더 기본 축=Y)
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Mesh cyl = tmp.GetComponent<MeshFilter>().sharedMesh;
        var combines = new List<CombineInstance>();
        Vector3 scl = new Vector3(2f * r, length * 0.5f, 2f * r); // 길이는 Y(실린더 높이 2)
        foreach (Vector2 o in offsets)
        {
            combines.Add(new CombineInstance
            {
                mesh = cyl,
                transform = Matrix4x4.TRS(new Vector3(o.x, 0f, o.y), Quaternion.identity, scl)
            });
        }
        SafeDestroy(tmp);

        var mesh = new Mesh { name = "PipeBundle" };
        mesh.CombineMeshes(combines.ToArray(), true, true);
        mesh.RecalculateBounds();

        var go = new GameObject("PipeBundle");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>();

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = true;                                      // 다발 전체 = convex 헐 1덩어리

        // 단일 파이프와 동일한 기본회전 → 길이가 눕고(로컬 Y→Z), 저장 회전값 호환
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        if (mesh.vertexCount == 0)
            Debug.LogWarning($"[CargoFactory] 파이프 번들 메시 비어있음 (bundle={bundle}) — CombineMeshes 실패 의심");
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

    /// <summary>
    /// 화물 종류별 실물 재질(색·금속감·광택). 텍스처 없이 PBR 파라미터만으로 "장난감→실물" 근사.
    /// 골판지=갈색 무광, 포대=베이지 천, 코일=브러시 스틸, 흰파이프=PVC 광택, 스틸파이프=아연도금,
    /// 납벨트=짙은 금속, 드럼=파란 강철드럼.
    /// </summary>
    public static Material MakeRealistic(CargoType type)
    {
        string name = type != null ? (type.name ?? "") : "";
        string id = type != null ? (type.id ?? "") : "";
        CargoShape shape = type != null ? type.shape : CargoShape.Box;

        Color color; float metallic, smoothness;
        switch (shape)
        {
            case CargoShape.Coil: // 강철 코일 — 브러시드 스틸
                color = new Color(0.76f, 0.77f, 0.79f); metallic = 0.9f; smoothness = 0.5f; break;
            case CargoShape.Sack: // 포대 — 짜임 천(베이지), 완전 무광
                color = new Color(0.86f, 0.82f, 0.72f); metallic = 0f; smoothness = 0.05f; break;
            case CargoShape.Drum: // 드럼통 — 파란 강철
                color = new Color(0.20f, 0.35f, 0.60f); metallic = 0.6f; smoothness = 0.45f; break;
            case CargoShape.Pipe:
                if (name.Contains("흰")) // 흰 PVC 파이프 — 플라스틱 광택
                { color = new Color(0.92f, 0.92f, 0.90f); metallic = 0f; smoothness = 0.6f; }
                else // 스틸/배관 파이프 — 아연도금 회색
                { color = new Color(0.70f, 0.72f, 0.74f); metallic = 0.85f; smoothness = 0.45f; }
                break;
            default: // Box
                if (name.Contains("납") || id.StartsWith("W")) // 납벨트 — 짙은 금속
                { color = new Color(0.28f, 0.29f, 0.31f); metallic = 0.7f; smoothness = 0.3f; }
                else // 골판지 박스 — 갈색 무광 (종류별 미세 색차로 단조로움 방지)
                {
                    float v = ((id.GetHashCode() & 0xff) / 255f - 0.5f) * 0.10f;
                    color = new Color(0.62f + v, 0.47f + v * 0.8f, 0.31f + v * 0.5f);
                    metallic = 0f; smoothness = 0.12f;
                }
                break;
        }
        return MakePBR(color, metallic, smoothness);
    }

    /// <summary>색+금속감+광택 PBR 머티리얼. URP Lit / Standard 양쪽 프로퍼티명 대응.</summary>
    public static Material MakePBR(Color color, float metallic, float smoothness)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        bool urp = sh != null;
        if (!urp) sh = Shader.Find("Standard");
        var m = new Material(sh) { color = color };
        if (urp)
        {
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
        }
        else // Standard (Built-in)
        {
            m.SetColor("_Color", color);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Glossiness", smoothness);
        }
        return m;
    }
}
