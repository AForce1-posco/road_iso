using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 정적 캘리브레이션: 실제 목업 배치 CSV(바운딩박스 + 실측 CoG)를 읽어,
/// 위치로 계산한 CoG와 실측 CoG를 비교 → Results/calibration_compare.csv 로 내보낸다.
///
/// 좌표계(실제 목업 규약):
///   원점 = RL(뒤-좌) 모서리, 단위 cm.
///   x = 좌우 (RL→RR),  y = 전후·주행 (RL→앞),  z = 높이 (위)
///   화물 중심 = 각 축 (start+end)/2. CoG = 질량가중 중심(회전 무관).
///
/// 입력 CSV 컬럼(헤더 이름으로 매칭 — 순서 무관):
///   Case_ID, Base_ID, Weight_kg, x_start, x_end, y_start, y_end, z_start, z_end, Cog_x, Cog_y, Cog_z
///   · Case_ID는 케이스 첫 행에만, 이후 행은 비움(같은 케이스 연속).
///   · Cog_x/y/z(실측)는 케이스당 1개 — 첫 행에 기입(비우면 그 축 오차 제외).
/// </summary>
public class CalibrationRunner : MonoBehaviour
{
    [Header("입력 (비우면 Assets/Data/Calibration/mockup_cases.csv)")]
    public string casesCsv = "";

    [Header("출력 (비우면 Assets/Data/Results/calibration_compare.csv)")]
    public string outputCsv = "";

    [Header("비주얼 (옵션) — 선택 케이스를 정적 씬에 표시")]
    public CargoPlacer placer;
    public string visualizeCaseId = "";
    [Tooltip("좌우(가로) cm / 전후(길이) cm — 목업 프레임을 Unity 트레이 중심으로 옮길 때 사용")]
    public float lateralCm = 21f;   // x 폭
    public float foreaftCm = 62f;   // y 길이

    [Header("실행")]
    public bool runOnStart = true;

    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private string CasesPath => string.IsNullOrEmpty(casesCsv) ? Path.Combine(Application.dataPath, "Data/Calibration/mockup_cases.csv") : casesCsv;
    private string OutputPath => string.IsNullOrEmpty(outputCsv) ? Path.Combine(Application.dataPath, "Data/Results/calibration_compare.csv") : outputCsv;

    void Start() { if (runOnStart) StartCoroutine(RunDeferred()); }

    // CargoPlacer.Start()가 트레이·cargoParent를 먼저 만들도록 한 프레임 대기 후 실행
    private System.Collections.IEnumerator RunDeferred()
    {
        yield return null;
        if (placer == null) placer = FindObjectOfType<CargoPlacer>(); // 참조 비면 씬에서 자동 검색
        Run();
    }

    [ContextMenu("Run Calibration Compare")]
    public void Run()
    {
        var order = new List<string>();
        var cases = ReadCases(CasesPath, order);
        if (cases.Count == 0) { Debug.LogWarning($"[Calib] 케이스 CSV 없음/빈: {CasesPath}"); return; }

        var sb = new StringBuilder();
        sb.AppendLine("case_id,cargo_count,total_mass_kg," +
                      "unity_cog_x,unity_cog_y,unity_cog_z," +
                      "real_cog_x,real_cog_y,real_cog_z," +
                      "err_x,err_y,err_z");

        var eX = new List<float>(); var eY = new List<float>(); var eZ = new List<float>();
        foreach (string id in order)
        {
            Case cs = cases[id];
            if (cs.items.Count == 0) continue;
            float M = 0f; Vector3 w = Vector3.zero;
            foreach (var it in cs.items) { M += it.mass; w += it.mass * it.center; }
            Vector3 cog = M > 0f ? w / M : Vector3.zero;

            sb.AppendLine(string.Join(",",
                id, cs.items.Count.ToString(CI), F(M),
                F(cog.x), F(cog.y), F(cog.z),
                Fn(cs.realX), Fn(cs.realY), Fn(cs.realZ),
                Diff(cog.x, cs.realX), Diff(cog.y, cs.realY), Diff(cog.z, cs.realZ)));

            if (cs.realX.HasValue) eX.Add(cog.x - cs.realX.Value);
            if (cs.realY.HasValue) eY.Add(cog.y - cs.realY.Value);
            if (cs.realZ.HasValue) eZ.Add(cog.z - cs.realZ.Value);
        }

        // 오차 통계 — CSV 하단 + 콘솔 진단 (Unity 안에서 바로 판정)
        sb.AppendLine();
        sb.AppendLine("# 오차 통계 (err = unity - real, cm)");
        sb.AppendLine("axis,mean,std,max_abs,count,verdict");
        sb.AppendLine(StatLine("x", eX));
        sb.AppendLine(StatLine("y", eY));
        sb.AppendLine(StatLine("z", eZ));

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        File.WriteAllText(OutputPath, sb.ToString());
        Debug.Log($"[Calib] 비교 저장: {OutputPath} ({order.Count}개 케이스)\n" +
                  $"■ 오차 진단\n {Diagnose("x", eX)}\n {Diagnose("y", eY)}\n {Diagnose("z", eZ)}");

        if (placer != null && !string.IsNullOrEmpty(visualizeCaseId) && cases.TryGetValue(visualizeCaseId, out Case vc))
            Visualize(vc);
    }

    /// <summary>목업 프레임(RL 원점) → Unity 트레이 중심 로컬로 옮겨 정적 씬에 표시.</summary>
    private void Visualize(Case cs)
    {
        var cat = CargoCatalog.CreateDefault();
        var byKey = new Dictionary<string, CargoType>();
        foreach (var t in cat) if (t != null) { byKey[t.id] = t; byKey[t.name] = t; }

        var file = new CargoLayoutFile { bed = new CargoLayoutBed { widthX = lateralCm * 0.01f, lengthZ = foreaftCm * 0.01f, wallHeight = 0.27f } };
        foreach (var it in cs.items)
        {
            if (!byKey.TryGetValue(it.baseId, out CargoType type)) continue;
            // Unity: x=좌우(중심 기준), z=전후(중심 기준), y=높이(바닥 위). cm→m.
            float ux = (it.center.x - lateralCm * 0.5f) * 0.01f;
            float uz = (it.center.y - foreaftCm * 0.5f) * 0.01f;
            float uy = it.center.z * 0.01f;
            file.cargo.Add(new CargoLayoutEntry { type = type.name, localPos = new Vector3(ux, uy, uz), localEuler = Vector3.zero, secured = true });
        }
        string tmp = Path.Combine(Application.dataPath, "Data/Calibration/_visualize_temp.json");
        File.WriteAllText(tmp, JsonUtility.ToJson(file, true));
        placer.LoadLayout(tmp);
        Debug.Log($"[Calib] 정적 씬 표시: {visualizeCaseId} ({file.cargo.Count}개)");
    }

    // ── CSV 읽기 (헤더 이름으로 컬럼 매칭) ──────────────────────────────────
    private Dictionary<string, Case> ReadCases(string path, List<string> order)
    {
        var map = new Dictionary<string, Case>();
        if (!File.Exists(path)) return map;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return map;

        var col = HeaderIndex(lines[0]);
        string cur = null;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#")) continue;
            string[] c = line.Split(',');

            string caseId = Get(c, col, "Case_ID");
            if (!string.IsNullOrEmpty(caseId))
            {
                cur = caseId;
                if (!map.ContainsKey(cur)) { map[cur] = new Case(); order.Add(cur); }
                map[cur].realX = GetNF(c, col, "Cog_x");
                map[cur].realY = GetNF(c, col, "Cog_y");
                map[cur].realZ = GetNF(c, col, "Cog_z");
            }
            if (cur == null) continue;

            string baseId = Get(c, col, "Base_ID");
            if (string.IsNullOrEmpty(baseId)) continue;
            var it = new Item
            {
                baseId = baseId,
                mass = GetF(c, col, "Weight_kg"),
                center = new Vector3(
                    Mid(GetF(c, col, "x_start"), GetF(c, col, "x_end")),
                    Mid(GetF(c, col, "y_start"), GetF(c, col, "y_end")),
                    Mid(GetF(c, col, "z_start"), GetF(c, col, "z_end"))),
            };
            map[cur].items.Add(it);
        }
        return map;
    }

    private static Dictionary<string, int> HeaderIndex(string header)
    {
        var map = new Dictionary<string, int>();
        string[] h = header.Split(',');
        for (int i = 0; i < h.Length; i++) map[h[i].Trim()] = i;
        return map;
    }
    private static string Get(string[] c, Dictionary<string, int> col, string name)
        => col.TryGetValue(name, out int i) && i < c.Length ? c[i].Trim() : "";
    private static float GetF(string[] c, Dictionary<string, int> col, string name)
        => float.TryParse(Get(c, col, name), NumberStyles.Float, CI, out float v) ? v : 0f;
    private static float? GetNF(string[] c, Dictionary<string, int> col, string name)
        => float.TryParse(Get(c, col, name), NumberStyles.Float, CI, out float v) ? v : (float?)null;

    // ── 오차 통계 / 진단 ─────────────────────────────────────────────────
    private static (float mean, float std, float maxabs, int n) Stats(List<float> e)
    {
        int n = e.Count;
        if (n == 0) return (0f, 0f, 0f, 0);
        float mean = 0f; foreach (var v in e) mean += v; mean /= n;
        float var = 0f; foreach (var v in e) var += (v - mean) * (v - mean);
        float std = n > 1 ? Mathf.Sqrt(var / (n - 1)) : 0f;
        float mx = 0f; foreach (var v in e) mx = Mathf.Max(mx, Mathf.Abs(v));
        return (mean, std, mx, n);
    }
    private static string Verdict(float mean, float std, int n)
    {
        if (n == 0) return "측정없음";
        if (Mathf.Abs(mean) >= 0.5f && Mathf.Abs(mean) >= std) return "계통편차(원점/스케일 보정 검토)";
        if (std >= 1.0f) return "흩어짐(측정노이즈/입력확인)";
        return "양호";
    }
    private static string StatLine(string axis, List<float> e)
    {
        var s = Stats(e);
        return string.Join(",", axis, F(s.mean), F(s.std), F(s.maxabs), s.n.ToString(CI), Verdict(s.mean, s.std, s.n));
    }
    private static string Diagnose(string axis, List<float> e)
    {
        var s = Stats(e);
        if (s.n == 0) return $"{axis}: 측정 없음";
        return $"{axis}: 평균 {s.mean:+0.00;-0.00}cm · std {s.std:0.00} · 최대 {s.maxabs:0.00} → {Verdict(s.mean, s.std, s.n)}";
    }

    private static float Mid(float a, float b) => (a + b) * 0.5f;
    private static string F(float v) => v.ToString("F3", CI);
    private static string Fn(float? v) => v.HasValue ? v.Value.ToString("F3", CI) : "";
    private static string Diff(float unity, float? real) => real.HasValue ? (unity - real.Value).ToString("F3", CI) : "";

    private struct Item { public string baseId; public float mass; public Vector3 center; }
    private class Case { public List<Item> items = new List<Item>(); public float? realX, realY, realZ; }
}
