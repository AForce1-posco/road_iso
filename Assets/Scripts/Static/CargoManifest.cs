using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>인스펙터에서 지정하는 적재 목록 1줄 = (화물 id, 개수).</summary>
[System.Serializable]
public class ManifestEntry
{
    public string typeId;
    public int count = 1;
}

/// <summary>
/// 순수 BPP 입력용 manifest 해석기. 인스펙터 (id,개수) 목록 또는 CSV 파일을 CargoType 리스트로 전개.
/// CSV 형식: 한 줄에 "id,개수" (헤더 'id,...' 줄은 무시). 경로는 Assets/ 기준 상대경로.
/// </summary>
public static class CargoManifest
{
    /// <summary>csvRelPath 가 지정되면 CSV 우선, 아니면 인스펙터 entries 를 씀. source 에 출처 표기.</summary>
    public static List<CargoType> Resolve(IReadOnlyList<ManifestEntry> entries, string csvRelPath, out string source)
    {
        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;

        List<(string id, int count)> pairs;
        if (!string.IsNullOrWhiteSpace(csvRelPath))
        {
            pairs = LoadCsv(csvRelPath);
            source = $"CSV:{csvRelPath}";
        }
        else
        {
            pairs = new List<(string, int)>();
            if (entries != null)
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrWhiteSpace(e.typeId))
                        pairs.Add((e.typeId.Trim(), Mathf.Max(0, e.count)));
            source = "Inspector";
        }

        var manifest = new List<CargoType>();
        foreach (var (id, count) in pairs)
        {
            if (!cat.TryGetValue(id, out var t)) { Debug.LogWarning($"[Manifest] 카탈로그에 없는 화물 id: '{id}' — 건너뜀"); continue; }
            for (int k = 0; k < count; k++) manifest.Add(t);
        }
        return manifest;
    }

    private static List<(string id, int count)> LoadCsv(string relPath)
    {
        var pairs = new List<(string, int)>();
        string path = Path.Combine(Application.dataPath, relPath);
        if (!File.Exists(path)) { Debug.LogWarning($"[Manifest] CSV 없음: {path}"); return pairs; }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 2) continue;
            string id = c[0].Trim();
            if (id.Equals("id", System.StringComparison.OrdinalIgnoreCase)) continue; // 헤더 스킵
            if (!int.TryParse(c[1].Trim(), out int n)) continue;
            pairs.Add((id, Mathf.Max(0, n)));
        }
        return pairs;
    }
}
