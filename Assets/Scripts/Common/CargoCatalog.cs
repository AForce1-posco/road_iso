using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// 화물 스펙 목록 (1:10 목업). 기본은 Assets/Data/cargo_catalog.csv에서 로드,
/// CSV가 없거나 파싱 실패 시 하드코딩 폴백을 쓴다.
/// CSV 컬럼: id,name,shape,sizeX_cm,sizeY_cm,sizeZ_cm,massKg,innerDia_cm,stock,note
///  - sizeX/Y/Z_cm = CargoType.sizeM 각 성분(cm). 형상별 의미: Box=폭/높이/깊이, Pipe=단면폭/단면높이/길이
///  - stock = 실제 목업 재고 수 (정적 씬 안내용, 배치 제한 아님)
/// </summary>
public static class CargoCatalog
{
    /// <summary>CSV 우선, 실패 시 하드코딩 폴백.</summary>
    public static CargoType[] CreateDefault()
    {
        CargoType[] fromCsv = LoadCsv(CargoPaths.CatalogCsv);
        if (fromCsv != null && fromCsv.Length > 0)
        {
            Debug.Log($"화물 카탈로그 CSV 로드: {fromCsv.Length}종 ({CargoPaths.CatalogCsv})");
            return fromCsv;
        }
        Debug.LogWarning("화물 카탈로그 CSV 없음/실패 — 하드코딩 폴백 사용");
        return Hardcoded();
    }

    /// <summary>CSV 파싱. 실패 시 null.</summary>
    public static CargoType[] LoadCsv(string path)
    {
        if (!File.Exists(path)) return null;
        var ci = CultureInfo.InvariantCulture;
        var list = new List<CargoType>();
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++) // 0행 헤더
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                string[] c = line.Split(',');
                if (c.Length < 9) continue;

                float x = ParseF(c[3], ci), y = ParseF(c[4], ci), z = ParseF(c[5], ci);
                var ct = new CargoType
                {
                    id = c[0].Trim(),
                    name = c[1].Trim(),
                    shape = ParseShape(c[2].Trim()),
                    sizeM = new Vector3(x, y, z) * 0.01f,
                    massKg = ParseF(c[6], ci),
                    innerDiameterM = ParseF(c[7], ci) * 0.01f,
                    stockCount = (int)ParseF(c[8], ci),
                    note = c.Length > 9 ? c[9].Trim() : "",
                };
                list.Add(ct);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"화물 카탈로그 CSV 파싱 오류: {e.Message}");
            return null;
        }
        return list.ToArray();
    }

    private static float ParseF(string s, CultureInfo ci)
    {
        float.TryParse(s.Trim(), NumberStyles.Float, ci, out float v);
        return v;
    }

    private static CargoShape ParseShape(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "drum": return CargoShape.Drum;
            case "pipe": return CargoShape.Pipe;
            case "coil": return CargoShape.Coil;
            case "sack": return CargoShape.Sack;
            default: return CargoShape.Box;
        }
    }

    // ── 하드코딩 폴백 (CSV 유실 시) ─────────────────────────────────────────
    private static CargoType[] Hardcoded()
    {
        return new[]
        {
            Box("B-001", "박스 B-001", 0.008f, 8f,    4f,   4.5f, 15, "내부 무게추"),
            Box("B-002", "박스 B-002", 0.023f, 13.5f, 8f,   2.5f, 10, "내부 무게추"),
            Box("B-003", "박스 B-003", 0.012f, 15.5f, 5f,   4f,   12, "내부 무게추"),
            Box("B-004", "박스 B-004", 0.030f, 16f,   10f,  4f,    4, "내부 무게추"),
            Box("B-005", "박스 B-005", 0.026f, 10f,   10f,  10f,  18, "내부 무게추"),
            Box("B-006", "박스 B-006", 0.031f, 17f,   9.5f, 6.5f,  9, "내부 무게추"),
            Box("B-007", "박스 B-007", 0.093f, 25f,   14.5f,4.5f,  6, "내부 무게추"),
            Box("B-005H", "박스 B-005H", 0.51f, 10f,  10f,  10f,   1, "테스트용 0.51kg"),
            new CargoType { id = "T-001", name = "톤백 T-001", massKg = 0.8f,
                sizeM = new Vector3(0.07f, 0.10f, 0.07f), shape = CargoShape.Sack, stockCount = 1, note = "포대형" },
            new CargoType { id = "C-001", name = "코일 C-001", massKg = 1.0f,
                sizeM = new Vector3(0.10f, 0.02f, 0.10f), shape = CargoShape.Coil, innerDiameterM = 0.04f, stockCount = 1, note = "원통형(중공)" },
            Pipe("P-001", "흰 파이프 번들(3)", 0.82f,  6f,   6f,   60f,  2, "3개 번들"),
            Pipe("P-002", "흰 파이프 번들(2)", 0.56f,  6f,   6f,   60f,  2, "2개 번들"),
            Pipe("P-003", "스틸 파이프 번들(3)", 0.585f, 2f, 2f,   50f,  1, "3개 번들"),
            Pipe("P-004", "스틸 파이프 번들(2)", 0.545f, 2f, 1f,   70f,  1, "2개 번들"),
            Pipe("S-001", "배관 파이프 S-001", 1.078f, 4f,   4f,   39.5f, 5, "번들 제외"),
            Pipe("S-002", "배관 파이프 S-002", 1.9f,   4.5f, 4.5f, 70f,   3, "번들 제외"),
            Pipe("S-003", "배관 파이프 S-003", 2.08f,  4f,   4f,   79.5f, 3, "번들 제외"),
            Box("W-001", "납벨트 W-001", 0.5f, 9f, 7.5f, 1f, 1, ""),
        };
    }

    private static CargoType Box(string id, string name, float kg, float wCm, float dCm, float hCm, int stock, string note)
        => new CargoType { id = id, name = name, massKg = kg, sizeM = new Vector3(wCm, hCm, dCm) * 0.01f,
            shape = CargoShape.Box, stockCount = stock, note = note };

    private static CargoType Pipe(string id, string name, float kg, float wCm, float hCm, float lenCm, int stock, string note)
        => new CargoType { id = id, name = name, massKg = kg, sizeM = new Vector3(wCm, hCm, lenCm) * 0.01f,
            shape = CargoShape.Pipe, stockCount = stock, note = note };
}
