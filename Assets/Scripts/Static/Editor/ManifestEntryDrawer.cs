using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ManifestEntry 인스펙터 드로어. typeId 를 자유 타이핑 대신 **카탈로그 화물 드롭다운**으로 선택 + 개수 입력.
/// 한 줄 = [화물 드롭다운(id + 이름)]  [× 개수].
/// 카탈로그(cargo_catalog.csv) 목록은 스크립트 컴파일(도메인 리로드) 시 갱신됨.
/// </summary>
[CustomPropertyDrawer(typeof(ManifestEntry))]
public class ManifestEntryDrawer : PropertyDrawer
{
    static string[] ids;      // 드롭다운 값 (화물 id)
    static string[] labels;   // 표시 라벨 "id  (이름)"

    static void EnsureCatalog()
    {
        if (ids != null) return;
        var idList = new List<string>();
        var labelList = new List<string>();
        foreach (var t in CargoCatalog.CreateDefault())
        {
            if (t == null) continue;
            idList.Add(t.id);
            labelList.Add($"{t.id}  ({t.name})");
        }
        ids = idList.ToArray();
        labels = labelList.ToArray();
    }

    /// <summary>카탈로그 변경(CSV 편집) 후 강제 갱신용 — 필요 시 메뉴에서 호출 가능.</summary>
    [MenuItem("Tools/BinPacker/Refresh Cargo Dropdown")]
    static void Refresh() { ids = null; labels = null; EnsureCatalog(); Debug.Log($"[Manifest] 카탈로그 드롭다운 갱신: {ids.Length}종"); }

    public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
    {
        EnsureCatalog();
        var idProp = prop.FindPropertyRelative("typeId");
        var countProp = prop.FindPropertyRelative("count");

        const float countW = 64f, gap = 6f;
        var idRect = new Rect(pos.x, pos.y, pos.width - countW - gap, EditorGUIUtility.singleLineHeight);
        var countRect = new Rect(pos.x + pos.width - countW, pos.y, countW, EditorGUIUtility.singleLineHeight);

        if (ids == null || ids.Length == 0)
        {
            // 카탈로그 로드 실패 시 자유 입력 폴백
            idProp.stringValue = EditorGUI.TextField(idRect, idProp.stringValue);
        }
        else
        {
            int cur = System.Array.IndexOf(ids, idProp.stringValue);
            if (cur < 0) cur = 0;                                   // 빈/미지정 → 첫 화물
            int sel = EditorGUI.Popup(idRect, cur, labels);
            idProp.stringValue = ids[Mathf.Clamp(sel, 0, ids.Length - 1)];
        }

        countProp.intValue = Mathf.Max(0, EditorGUI.IntField(countRect, countProp.intValue));
    }
}
