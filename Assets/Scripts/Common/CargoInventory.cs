using UnityEngine;

/// <summary>
/// 화물 카탈로그를 씬에 실물 오브젝트로 진열하는 인벤토리.
/// 컴포넌트 우클릭 → "인벤토리 생성/재생성" 하면 자식으로 화물들이 한 줄로 생긴다 (에디트 모드 가능).
/// CargoBedLoader는 케이스 JSON을 읽고 여기 진열된 오브젝트를 복제해 트럭에 싣는다 —
/// 진열품의 모양(머티리얼·메시)을 씬에서 바꾸면 적재물에도 그대로 반영된다.
/// 진열품 콜라이더는 꺼둔다(트럭이 지나가다 부딪히지 않게), 복제본은 다시 켠다.
/// </summary>
public class CargoInventory : MonoBehaviour
{
    [Tooltip("진열 크기 배율 — CargoBedLoader.scale과 맞출 것 (다르면 복제 시 자동 보정)")]
    public float scale = 10f;

    [Tooltip("진열 간격 (m)")]
    public float spacing = 0.8f;

    [Tooltip("비우면 CargoCatalog 기본 17종")]
    public CargoType[] cargoTypes;

    [ContextMenu("인벤토리 생성/재생성")]
    public void Rebuild()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            GameObject c = transform.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(c); else DestroyImmediate(c);
        }

        CargoType[] types = (cargoTypes != null && cargoTypes.Length > 0)
            ? cargoTypes : CargoCatalog.CreateDefault();

        float cursor = 0f;
        for (int i = 0; i < types.Length; i++)
        {
            CargoType t = types[i];
            GameObject go = CargoFactory.Create(t, scale, ColorFor(i));
            go.name = t.name;
            go.AddComponent<CargoItem>().typeName = t.name;
            go.transform.SetParent(transform, false);

            // 크기가 제각각이라 렌더러 바운드로 한 줄 진열 (긴 파이프는 +Z로 뻗음)
            var mr = go.GetComponent<MeshRenderer>();
            Vector3 size = mr != null ? mr.bounds.size : t.sizeM * scale;
            go.transform.localPosition = new Vector3(cursor + size.x * 0.5f, 0f, 0f);
            cursor += size.x + spacing;

            // 바닥에 앉히기 (바운드 최저점이 인벤토리 기준면에 오도록)
            if (mr != null)
            {
                float lift = go.transform.position.y - mr.bounds.min.y;
                go.transform.localPosition += new Vector3(0f, lift, 0f);
            }

            // 진열품은 물리 간섭 금지
            foreach (Collider col in go.GetComponentsInChildren<Collider>())
                col.enabled = false;
        }
        Debug.Log($"화물 인벤토리 진열: {types.Length}종");
    }

    /// <summary>이름이 일치하는 진열품을 복제해 반환 (콜라이더 활성화). 없으면 null.</summary>
    public GameObject CreateInstance(string typeName, float targetScale)
    {
        foreach (Transform c in transform)
        {
            CargoItem item = c.GetComponent<CargoItem>();
            if (item == null || item.typeName != typeName) continue;

            GameObject clone = Instantiate(c.gameObject);
            clone.name = "Cargo_" + typeName;
            if (scale > 0f && !Mathf.Approximately(targetScale, scale))
                clone.transform.localScale *= targetScale / scale;
            foreach (Collider col in clone.GetComponentsInChildren<Collider>())
                col.enabled = true;
            return clone;
        }
        return null;
    }

    private static readonly Color[] Palette =
    {
        new Color(0.30f, 0.55f, 0.85f), new Color(0.90f, 0.55f, 0.25f),
        new Color(0.35f, 0.72f, 0.45f), new Color(0.62f, 0.45f, 0.80f),
        new Color(0.30f, 0.72f, 0.72f), new Color(0.85f, 0.40f, 0.45f),
        new Color(0.85f, 0.75f, 0.35f),
    };

    private static Color ColorFor(int i) => Palette[i % Palette.Length];
}
