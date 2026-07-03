using UnityEngine;

/// <summary>
/// 실측 화물 목록 (cargo_input_template.csv, 1:10 목업).
/// CargoPlacer.cargoTypes를 인스펙터에서 비워두면 이 기본값이 자동으로 채워진다.
/// 번들 파이프(P-xxx)는 묶음 1개를 화물 1단위로 취급 — 질량·단면은 번들 전체 값.
/// </summary>
public static class CargoCatalog
{
    public static CargoType[] CreateDefault()
    {
        return new[]
        {
            // ── 박스 (내부 무게추) ─ 가로×세로×높이(cm), 질량(kg), 재고
            Box("B-001", "박스 B-001", 0.008f, 8f,    4f,   4.5f, 15, "내부 무게추"),
            Box("B-002", "박스 B-002", 0.023f, 13.5f, 8f,   2.5f, 10, "내부 무게추"),
            Box("B-003", "박스 B-003", 0.012f, 15.5f, 5f,   4f,   12, "내부 무게추"),
            Box("B-004", "박스 B-004", 0.030f, 16f,   10f,  4f,    4, "내부 무게추"),
            Box("B-005", "박스 B-005", 0.026f, 10f,   10f,  10f,  18, "내부 무게추"),
            Box("B-006", "박스 B-006", 0.031f, 17f,   9.5f, 6.5f,  9, "내부 무게추"),
            Box("B-007", "박스 B-007", 0.093f, 25f,   14.5f,4.5f,  6, "내부 무게추"),

            // ── 톤백 / 코일
            new CargoType
            {
                id = "T-001", name = "톤백 T-001", massKg = 0.8f,
                sizeM = new Vector3(0.07f, 0.10f, 0.07f),
                shape = CargoShape.Sack, stockCount = 1, note = "포대형",
            },
            new CargoType
            {
                id = "C-001", name = "코일 C-001", massKg = 1.0f,
                sizeM = new Vector3(0.10f, 0.02f, 0.10f),
                shape = CargoShape.Coil, innerDiameterM = 0.04f,
                stockCount = 1, note = "원통형(중공)",
            },

            // ── 파이프 번들 (묶음 1개 = 화물 1단위) ─ 단면폭×단면높이×길이(cm)
            Pipe("P-001", "흰 파이프 번들(3)", 0.82f,  6f,   6f,   60f,  2, "3개 번들 처리"),
            Pipe("P-002", "흰 파이프 번들(2)", 0.56f,  6f,   6f,   60f,  2, "2개 번들 처리"),
            Pipe("P-003", "스틸 파이프 번들(3)", 0.585f, 2f, 2f,   50f,  1, "3개 번들 처리"),
            Pipe("P-004", "스틸 파이프 번들(2)", 0.545f, 2f, 1f,   70f,  1, "2개 번들 처리"),

            // ── 배관 파이프 (안 묶임, 개별)
            Pipe("S-001", "배관 파이프 S-001", 1.078f, 4f,   4f,   39.5f, 5, "번들 제외"),
            Pipe("S-002", "배관 파이프 S-002", 1.9f,   4.5f, 4.5f, 70f,   3, "번들 제외"),
            Pipe("S-003", "배관 파이프 S-003", 2.08f,  4f,   4f,   79.5f, 3, "번들 제외"),

            // ── 기타
            Box("W-001", "납벨트 W-001", 0.5f, 9f, 7.5f, 1f, 1, ""),
        };
    }

    /// <summary>박스: 가로(x)·세로(z)·높이(y) cm 입력.</summary>
    private static CargoType Box(string id, string name, float kg,
        float wCm, float dCm, float hCm, int stock, string note)
    {
        return new CargoType
        {
            id = id, name = name, massKg = kg,
            sizeM = new Vector3(wCm, hCm, dCm) * 0.01f,
            shape = CargoShape.Box, stockCount = stock, note = note,
        };
    }

    /// <summary>파이프: 단면폭(x)·단면높이(y)·길이(z) cm 입력. 길이 방향으로 눕는다.</summary>
    private static CargoType Pipe(string id, string name, float kg,
        float wCm, float hCm, float lenCm, int stock, string note)
    {
        return new CargoType
        {
            id = id, name = name, massKg = kg,
            sizeM = new Vector3(wCm, hCm, lenCm) * 0.01f,
            shape = CargoShape.Pipe, stockCount = stock, note = note,
        };
    }
}
