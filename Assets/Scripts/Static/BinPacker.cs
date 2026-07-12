using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 정적 배치 그리디 빈패커 (baseline + 워밍스타트 교사).
/// RL 환경(RuleChecker·RewardCalculator·격자 드롭)을 그대로 재사용해, 신경망 대신 휴리스틱으로 배치:
/// 매 스텝 "RuleChecker로 유효한 후보(96셀×회전) 중 RewardCalculator 점수 최대"를 선택(그리디).
///
/// 용도: (1) RL 비교 baseline (RL vs 빈패커 vs 랜덤)  (2) 워밍스타트(BC) 교사(Phase 2, Decide 사용).
/// 좌표계: RuleChecker와 동일 (x=좌우, y=높이, z=주행/길이, 원점=트레이 rear-left 코너, 중심=W/2·L/2).
/// 설계: Docs/BinPacker_Design.md
/// </summary>
public class BinPacker
{
    /// <summary>배치 1개 (저장·시각화용, 회전정보 포함).</summary>
    public struct Placement
    {
        public CargoType type;
        public Vector3 center;    // 트레이 로컬
        public Vector3 halfSize;  // 회전 반영 반크기
        public int rot;           // 0=그대로, 1=yaw90
        public Vector3 euler;     // 저장용 회전(도)
    }

    /// <summary>패킹 목적. Stable=안정성 보상 최대(RL baseline·BC 교사용) / Dense=고전 3D 빈패킹(공간 채우기만).</summary>
    public enum PackMode { Stable, Dense }
    public PackMode mode = PackMode.Stable;

    private readonly RuleConfig rcfg;
    private readonly RuleChecker rules;
    private readonly RewardCalculator reward;
    private readonly int cols, rows;

    public BinPacker(RuleConfig ruleConfig = null, RewardConfig rewardConfig = null, int cols = 11, int rows = 31)
    {
        rcfg = ruleConfig ?? new RuleConfig();
        rules = new RuleChecker(rcfg);
        reward = new RewardCalculator(rewardConfig ?? new RewardConfig(), rcfg);
        this.cols = cols;
        this.rows = rows;
    }

    private float HalfX => rcfg.trayLateralM * 0.5f;   // 중심 x = W/2 (원점 아님)
    private float HalfZ => rcfg.trayLengthM * 0.5f;    // 중심 z = L/2
    private int NumCells => cols * rows;

    /// <summary>미적재 화물 1개의 사유 요약 (모든 후보가 어떤 규칙에 걸렸는지 집계 → 최다 사유).</summary>
    public struct UnplacedReason
    {
        public CargoType type;
        public string dominant;      // 가장 많이 걸린 규칙 사유 (예: "H1 과적(>7kg)")
        public int dominantCount;    // 그 사유에 걸린 후보 수
        public int totalCandidates;  // 시도한 후보 수 (전부 무효였음)
        public override string ToString() => $"{type.id}: {dominant} ({dominantCount}/{totalCandidates}후보)";
    }

    /// <summary>manifest를 그리디로 배치. 못 놓은 화물은 unplaced에, 그 사유는 reasons에 담김 (둘 다 널 허용).</summary>
    public List<Placement> Pack(List<CargoType> manifest, List<CargoType> unplaced = null,
                                List<UnplacedReason> reasons = null)
    {
        // Stable: 무거운 것 먼저(안정성) / Dense: 부피 큰 것 먼저(고전 BPP 정석)
        var order = new List<CargoType>(manifest);
        if (mode == PackMode.Dense)
            order.Sort((a, b) => Volume(b).CompareTo(Volume(a)));
        else
            order.Sort((a, b) => b.massKg.CompareTo(a.massKg));

        var placed = new List<Placement>();
        var items = new List<RuleChecker.PlacedItem>();   // 규칙/보상 입력 캐시

        foreach (var type in order)
        {
            if (type == null) continue;
            bool found = false;
            float bestScore = float.NegativeInfinity;
            Placement best = default;
            RuleChecker.PlacedItem bestItem = default;
            Dictionary<string, int> reject = reasons != null ? new Dictionary<string, int>() : null;
            int tried = 0;

            int rotN = type.shape == CargoShape.Pipe ? 1 : 2;   // 파이프는 rot0만(길이 z축)
            for (int cell = 0; cell < NumCells; cell++)
            {
                for (int rot = 0; rot < rotN; rot++)
                {
                    tried++;
                    Placement cand = MakeCandidate(items, type, cell, rot);
                    var item = ToItem(cand);
                    if (!rules.IsValid(items, item, out string why))  // 규칙 위반 후보 제거 (+사유 집계)
                    {
                        if (reject != null) { reject.TryGetValue(why, out int c); reject[why] = c + 1; }
                        continue;
                    }
                    float s = Score(items, item, cand);
                    if (s > bestScore) { bestScore = s; best = cand; bestItem = item; found = true; }
                }
            }

            if (found) { placed.Add(best); items.Add(bestItem); }
            else
            {
                unplaced?.Add(type);
                if (reasons != null)
                {
                    string dom = "?"; int domN = 0;
                    if (reject != null)
                        foreach (var kv in reject)
                            if (kv.Value > domN) { dom = kv.Key; domN = kv.Value; }
                    reasons.Add(new UnplacedReason { type = type, dominant = dom, dominantCount = domN, totalCandidates = tried });
                }
            }
        }
        return placed;
    }

    /// <summary>
    /// GRASP(Greedy Randomized Adaptive Search) 팩 — 시드별로 다양한 배치를 생성.
    /// Pack의 두 결정론 지점을 랜덤화: (1) 순서 = 정렬 상위 orderK개 중 랜덤, (2) 배치 = RCL(상위 후보군) 중 랜덤.
    /// rng=null이면 Pack과 동일(결정론). alpha=0이면 최고점만(=거의 Pack), 클수록 다양성↑.
    /// 용도: Pack(seed) N회 → 예측기로 채점 → best 채택 (국소최적 탈출 · refinement 시작점 다양화).
    /// </summary>
    public List<Placement> PackGrasp(List<CargoType> manifest, System.Random rng,
                                     float alpha = 0.3f, int orderK = 2, List<CargoType> unplaced = null)
    {
        if (rng == null) return Pack(manifest, unplaced);

        // 남은 화물 (Pack과 동일 정렬 키: Dense=부피, Stable=질량, 내림차순)
        var remaining = new List<CargoType>();
        foreach (var t in manifest) if (t != null) remaining.Add(t);
        if (mode == PackMode.Dense) remaining.Sort((a, b) => Volume(b).CompareTo(Volume(a)));
        else                        remaining.Sort((a, b) => b.massKg.CompareTo(a.massKg));

        var placed = new List<Placement>();
        var items = new List<RuleChecker.PlacedItem>();

        while (remaining.Count > 0)
        {
            // (1) 순서 랜덤화: 정렬 상위 orderK개 중 하나 랜덤 선택 (나머지는 정렬 유지)
            int pickN = Mathf.Min(Mathf.Max(1, orderK), remaining.Count);
            int oi = rng.Next(pickN);
            CargoType type = remaining[oi];
            remaining.RemoveAt(oi);

            // 유효 후보 전부 수집 (셀×회전)
            int rotN = type.shape == CargoShape.Pipe ? 1 : 2;
            var cands = new List<Placement>();
            var citems = new List<RuleChecker.PlacedItem>();
            var scores = new List<float>();
            float best = float.NegativeInfinity, worst = float.PositiveInfinity;
            for (int cell = 0; cell < NumCells; cell++)
                for (int rot = 0; rot < rotN; rot++)
                {
                    Placement cand = MakeCandidate(items, type, cell, rot);
                    var item = ToItem(cand);
                    if (!rules.IsValid(items, item)) continue;
                    float s = Score(items, item, cand);
                    cands.Add(cand); citems.Add(item); scores.Add(s);
                    if (s > best) best = s;
                    if (s < worst) worst = s;
                }

            if (cands.Count == 0) { unplaced?.Add(type); continue; }

            // (2) RCL: 점수가 best - alpha*(best-worst) 이상인 후보군 중 랜덤 선택
            float thr = best - alpha * (best - worst);
            var rcl = new List<int>();
            for (int i = 0; i < cands.Count; i++) if (scores[i] >= thr) rcl.Add(i);
            int pick = rcl[rng.Next(rcl.Count)];
            placed.Add(cands[pick]); items.Add(citems[pick]);
        }
        return placed;
    }

    /// <summary>
    /// 현재 상태에서 "다음 한 수" 최선 행동 (Phase2 BC 교사용 — PlacementAgent.Heuristic이 호출).
    /// Pack과 동일 정책: 남은 종류 중 무거운 것부터, 유효 후보 중 최고 점수 (셀, 회전).
    /// 유효 후보가 하나도 없으면 false (호출측에서 폴백).
    /// </summary>
    public bool Decide(IReadOnlyList<RuleChecker.PlacedItem> placed, IReadOnlyList<CargoType> pool,
                       int[] remaining, out int typeIdx, out int cellIdx, out int rot)
    {
        typeIdx = -1; cellIdx = -1; rot = 0;
        var items = new List<RuleChecker.PlacedItem>(placed);

        // 남은 종류를 질량 내림차순으로 (Pack과 동일한 순서 정책)
        var idx = new List<int>();
        for (int i = 0; i < pool.Count; i++) if (remaining[i] > 0 && pool[i] != null) idx.Add(i);
        idx.Sort((a, b) => pool[b].massKg.CompareTo(pool[a].massKg));

        foreach (int ti in idx)
        {
            CargoType type = pool[ti];
            bool found = false;
            float bestScore = float.NegativeInfinity;
            int bestCell = -1, bestRot = 0;

            int rotN = type.shape == CargoShape.Pipe ? 1 : 2;
            for (int cell = 0; cell < NumCells; cell++)
                for (int r = 0; r < rotN; r++)
                {
                    Placement cand = MakeCandidate(items, type, cell, r);
                    var item = ToItem(cand);
                    if (!rules.IsValid(items, item)) continue;
                    float s = Score(items, item, cand);
                    if (s > bestScore) { bestScore = s; bestCell = cell; bestRot = r; found = true; }
                }

            if (found) { typeIdx = ti; cellIdx = bestCell; rot = bestRot; return true; }
        }
        return false;   // 어떤 남은 화물도 놓을 곳 없음 (과적/공간)
    }

    /// <summary>
    /// learn-to-pack 디코더: 주어진 type을 현재 placed 위에 놓을 최선(유효·최고점) 셀·회전을 반환.
    /// RL이 "순서(어떤 화물)"만 정하고, 이 함수가 "위치"를 최적 결정 → 나오는 배치는 항상 유효.
    /// 못 놓으면 false.
    /// </summary>
    public bool PlaceBest(IReadOnlyList<RuleChecker.PlacedItem> placed, CargoType type, out int cell, out int rot)
    {
        cell = -1; rot = 0;
        if (type == null) return false;
        var items = new List<RuleChecker.PlacedItem>(placed);
        bool found = false;
        float bestScore = float.NegativeInfinity;
        int rotN = type.shape == CargoShape.Pipe ? 1 : 2;
        for (int cc = 0; cc < NumCells; cc++)
            for (int r = 0; r < rotN; r++)
            {
                Placement cand = MakeCandidate(items, type, cc, r);
                var item = ToItem(cand);
                if (!rules.IsValid(items, item)) continue;
                float s = Score(items, item, cand);
                if (s > bestScore) { bestScore = s; cell = cc; rot = r; found = true; }
            }
        return found;
    }

    /// <summary>
    /// 진단용: 현재 placed 상태에서 남은 각 종류가 "왜" 못 놓이는지 사유 집계.
    /// Decide()가 false를 낸 직후 호출하면, 종류별로 모든 후보가 어떤 규칙에 걸렸는지(최다 사유) 알 수 있음.
    /// </summary>
    public void DiagnoseUnplaced(IReadOnlyList<RuleChecker.PlacedItem> placed,
                                 IReadOnlyList<CargoType> pool, int[] remaining,
                                 List<UnplacedReason> reasons)
    {
        var items = new List<RuleChecker.PlacedItem>(placed);
        for (int ti = 0; ti < pool.Count; ti++)
        {
            if (remaining[ti] <= 0 || pool[ti] == null) continue;
            CargoType type = pool[ti];
            var reject = new Dictionary<string, int>();
            int tried = 0; bool anyValid = false;

            int rotN = type.shape == CargoShape.Pipe ? 1 : 2;
            for (int cell = 0; cell < NumCells; cell++)
                for (int r = 0; r < rotN; r++)
                {
                    tried++;
                    Placement cand = MakeCandidate(items, type, cell, r);
                    var item = ToItem(cand);
                    if (rules.IsValid(items, item, out string why)) { anyValid = true; continue; }
                    reject.TryGetValue(why, out int c); reject[why] = c + 1;
                }

            if (anyValid) continue;   // 놓을 수 있는 종류면 사유 없음
            string dom = "?"; int domN = 0;
            foreach (var kv in reject) if (kv.Value > domN) { dom = kv.Key; domN = kv.Value; }
            reasons.Add(new UnplacedReason { type = type, dominant = dom, dominantCount = domN, totalCandidates = tried });
        }
    }

    // ── 후보 생성: 셀 중심 + 낙하 안착 + 회전 ──────────────────────────────
    private Placement MakeCandidate(List<RuleChecker.PlacedItem> placed, CargoType type, int cell, int rot)
    {
        int c = cell % cols, r = cell / cols;
        Vector2 cc = CellCenter(c, r);                 // (x, z)
        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;
        float restBottom = RestBottom(placed, cc.x, cc.y, half);
        Vector3 euler = type.shape == CargoShape.Pipe ? new Vector3(90f, 0f, 0f)
                        : (rot == 1 ? new Vector3(0f, 90f, 0f) : Vector3.zero);
        return new Placement
        {
            type = type,
            halfSize = half,
            rot = rot,
            euler = euler,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
        };
    }

    private static RuleChecker.PlacedItem ToItem(Placement p) =>
        new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize };

    // 점수: Stable = (placed+후보)의 최종 보상 총점(RL 목표를 그리디 최대화) + 동점 시 낮고 중앙 선호
    //       Dense  = 공간 효율(LE)만 최대화 + 뒤-좌-아래 구석 선호 (Deep-Bottom-Left 유사, 고전 빈패킹)
    private float Score(List<RuleChecker.PlacedItem> placed, RuleChecker.PlacedItem cand, Placement candP)
    {
        var tmp = new List<RuleChecker.PlacedItem>(placed) { cand };
        if (mode == PackMode.Dense)
        {
            float le = reward.LoadingEfficiency(tmp);
            float corner = -0.002f * candP.center.y - 0.001f * (candP.center.x + candP.center.z);
            return le + corner;
        }
        float r = reward.Final(tmp).total;
        float tie = -0.001f * (candP.center.y + Mathf.Abs(candP.center.x - HalfX) + Mathf.Abs(candP.center.z - HalfZ));
        return r + tie;
    }

    private static float Volume(CargoType t) => t.sizeM.x * t.sizeM.y * t.sizeM.z;

    // ── 격자/드롭 (PlacementAgent와 동일 규약) ──────────────────────────────
    private Vector2 CellCenter(int c, int r)   // → (x 좌우, z 주행), rear-left 코너 원점
    {
        float x = (c + 0.5f) * rcfg.trayLateralM / cols;
        float z = (r + 0.5f) * rcfg.trayLengthM / rows;
        return new Vector2(x, z);
    }

    // 후보 밑면(x,z 기둥)이 안착할 바닥 높이: 겹치는 기존 화물들의 최대 top, 없으면 바닥
    private float RestBottom(List<RuleChecker.PlacedItem> placed, float x, float z, Vector3 half)
    {
        float bottom = rcfg.floorTopY;
        foreach (var p in placed)
        {
            bool ox = Mathf.Abs(p.center.x - x) < p.halfSize.x + half.x;
            bool oz = Mathf.Abs(p.center.z - z) < p.halfSize.z + half.z;
            if (ox && oz) bottom = Mathf.Max(bottom, p.Top);
        }
        return bottom;
    }
}
