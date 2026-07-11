using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// v2 — Refinement 강화학습. 빈패커(Dense) 완성 배치에서 **시작** → RL이 화물을 "재배치(relocate)"해 정적 보상 개선.
/// 시작이 유효 배치 + 무효 이동은 되돌림(no-op) → **붕괴 물리적 불가**. from-scratch PlacementAgent(v1)과 독립.
///
/// - 관측: 높이맵(cols×rows) + CoG(3) + 질량(1) + CoG편차(2)  = obs
/// - 행동: (아이템 index · 목표 셀 · 회전2) — item 하나를 집어 다른 셀로 옮김
/// - 보상: ΔFinal(이동 후 − 이동 전). 누적 = "빈패커 대비 얼마나 개선했나". 무효 이동은 작은 페널티.
/// - 예측기 오면 Final 대신 예측 위험도로 교체(아키텍처 그대로).
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class RefinementAgent : Agent
{
    [Header("규제/보상")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();

    [Header("격자 (PlacementAgent·빈패커와 동일)")]
    public int cols = 11;
    public int rows = 31;

    [Header("시작 배치 (빈패커 Pack)")]
    [Tooltip("이 manifest를 startPackMode로 Pack한 배치에서 시작 (boxpack001 = B-004×8·SYN-04×4·SYN-03×4)")]
    public ManifestEntry[] startManifest = {
        new ManifestEntry { typeId = "B-004", count = 8 },
        new ManifestEntry { typeId = "SYN-04", count = 4 },
        new ManifestEntry { typeId = "SYN-03", count = 4 },
    };
    [Tooltip("Dense=공간 위주(개선여지 큼) / Stable=이미 안전")]
    public BinPacker.PackMode startPackMode = BinPacker.PackMode.Dense;

    [Header("케이스 커리큘럼 (1개→N개)")]
    [Tooltip("각 항목 = 한 케이스의 manifest CSV(Assets 기준 상대경로, 예: Data/refine_case1.csv). " +
             "비우면 위 startManifest 1개만 사용. 채우면 에피소드마다 랜덤으로 한 케이스 선택 → 커리큘럼. " +
             "1단계=1개, 2단계=5개 넣으면 됨.")]
    public string[] caseCsvPaths = new string[0];

    [Header("에피소드")]
    [Tooltip("한 에피소드에 허용하는 재배치 수")]
    public int stepsPerEpisode = 25;
    [Tooltip("무효 이동(겹침/이탈) 시 작은 페널티")]
    public float invalidMovePenalty = 0.02f;

    [Header("Surrogate 보상 (2026-07-09) — PlacementAgent와 같은 잣대")]
    [Tooltip("켜면 매 스텝 보상 = ΔScore(이동 후−전), Score = −리스크(surrogate). 끄면 기존 ΔFinal(CGS). refinement의 조밀보상 구조는 그대로, '무엇을 개선하나'만 교체.")]
    public bool useSurrogateReward = false;
    [Tooltip("Resources 아래 트리 JSON(확장자 제외). PlacementAgent와 동일 모델 쓰면 공정비교.")]
    public string surrogateResourceName = "layout_risk_p95";
    [Tooltip("모델 학습 질량 스케일(동적 sim massScale=100=676kg). 0중요도라 값 무관하나 학습과 맞춤.")]
    public float modelMassScaleKg = 100f;
    [Tooltip("정규화 리스크(0~1)를 보상으로 키우는 배율. ΔScore라 1로 충분.")]
    public float surrogateRewardScale = 1f;
    [Tooltip("모델의 도로/주행 상수 피처(중요도 0, 학습값에 맞춤): 속도/뱅크/경사/차체질량")]
    public float targetSpeedKmh = 60f;
    public float roadBankAngleDeg = 0f;
    public float roadSlopeDeg = 0f;
    public float vehicleBaseMassKg = 3500f;
    private LayoutRiskModel riskModel;

    [Header("배치 저장 (추론/검증용 — 학습 시 OFF)")]
    [Tooltip("켜면 에피소드 끝(재배치 완료)마다 현재 배치를 Assets/Data/Results/<layoutOutName>.json 저장. 학습 중엔 반드시 OFF.")]
    public bool saveLayoutOnComplete = false;
    public string layoutOutName = "rl_refine_layout";

    [Header("디버그")]
    public bool verboseLog = false;

    // ── 내부 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private BinPacker packer;
    private List<CargoType> manifestList;                                   // 이번 에피소드 케이스의 manifest
    private List<List<CargoType>> caseSet;                                  // 케이스 커리큘럼(1개 이상)
    private int curCaseIdx;                                                 // 이번 에피소드 케이스 index
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private int numItems;
    private int stepCount;
    private float prevFinal;
    private bool setupDone;
    private float startFinal;      // 에피소드 시작(빈패커) Final — 개선량 계산 기준
    private int validMoves;        // 이번 에피소드 유효 이동 수 (계측)
    private bool diagLogged;       // surrogate 진단 로그 1회용
    private float minHalfXZ;       // manifest 중 가장 작은 화물의 최소 반치수 (경계 마스킹용)

    /// <summary>시각화용 읽기 전용 배치.</summary>
    public IReadOnlyList<RuleChecker.PlacedItem> PlacedItems => placed;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;   // 중심 x = W/2 (균형기준, 원점 아님)
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;    // 중심 z = L/2
    private float MaxX => ruleConfig.trayLateralM;           // 우측 경계 (좌=0)
    private float MaxZ => ruleConfig.trayLengthM;            // 앞 경계 (뒤=0)
    private int NumCells => cols * rows;
    private int ObsSize => NumCells + 3 + 1 + 2;   // 높이맵 + CoG(3) + 질량(1) + 편차(2)

    private void Awake() => Setup();
    public override void Initialize() => Setup();

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        rules = new RuleChecker(ruleConfig);
        reward = new RewardCalculator(rewardConfig, ruleConfig);
        packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = startPackMode };
        if (useSurrogateReward) riskModel = new LayoutRiskModel(surrogateResourceName);

        // 케이스 세트 구성: CSV 경로들이 있으면 각 CSV = 한 케이스, 없으면 startManifest 1개.
        caseSet = new List<List<CargoType>>();
        if (caseCsvPaths != null && caseCsvPaths.Length > 0)
        {
            foreach (var csv in caseCsvPaths)
            {
                if (string.IsNullOrWhiteSpace(csv)) continue;
                var m = CargoManifest.Resolve(null, csv, out string src);
                if (m != null && m.Count > 0) { caseSet.Add(m); Debug.Log($"[Refine] 케이스 로드 {src}: {m.Count}개"); }
                else Debug.LogWarning($"[Refine] 케이스 CSV 비었음/실패: {csv}");
            }
        }
        if (caseSet.Count == 0)   // fallback: 단일 케이스(인스펙터 manifest)
            caseSet.Add(CargoManifest.Resolve(startManifest, "", out _));

        // action branch0(아이템)은 고정이라 케이스 중 최대 아이템 수로. 적은 케이스의 남는 인덱스는 no-op 처리+마스킹.
        numItems = 1;
        foreach (var m in caseSet) numItems = Mathf.Max(numItems, m.Count);

        // 경계 마스킹용: 모든 케이스 중 가장 작은 화물의 최소 반치수. 이보다 좁게 남은 가장자리 셀은
        // 어떤 화물 중심을 놓아도 트레이를 벗어나므로 미리 막는다.
        minHalfXZ = float.MaxValue;
        foreach (var m in caseSet)
            foreach (var t in m)
                minHalfXZ = Mathf.Min(minHalfXZ, Mathf.Min(t.sizeM.x, t.sizeM.z) * 0.5f);
        if (minHalfXZ == float.MaxValue) minHalfXZ = 0f;

        manifestList = caseSet[0];   // 초기값(OnEpisodeBegin에서 매번 재선택)

        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(numItems, NumCells, 2); // 아이템·셀·회전
        if (MaxStep == 0) MaxStep = stepsPerEpisode + 2;

        Debug.Log($"[RefinementAgent] obs={ObsSize}, action=({numItems},{NumCells},2), 케이스={caseSet.Count}개, 최대화물={numItems}개, mode={startPackMode}");
    }

    public override void OnEpisodeBegin()
    {
        // 케이스 커리큘럼: 매 에피소드 케이스 하나 랜덤 선택 (1개면 항상 그것).
        curCaseIdx = caseSet.Count > 1 ? Random.Range(0, caseSet.Count) : 0;
        manifestList = caseSet[curCaseIdx];

        // 빈패커로 선택 케이스 시작 배치 생성 (케이스별 결정론적 Dense pack)
        var unplaced = new List<CargoType>();
        var packed = packer.Pack(manifestList, unplaced);
        placed.Clear();
        foreach (var p in packed)
            placed.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });

        stepCount = 0;
        validMoves = 0;
        prevFinal = placed.Count > 0 ? Score() : 0f;
        startFinal = prevFinal;

        // ── 진단(1회): surrogate가 빈패커 시작배치에 실제로 뱉는 위험값 ──
        // 올바른 p99 = 예측위험 ≈ 0.50(빈패커 p99), startFinal ≈ −0.50(scale1). ~0.87이면 모델/스케일 틀림.
        if (!diagLogged)
        {
            diagLogged = true;
            float rawRisk = (useSurrogateReward && riskModel != null && riskModel.Loaded)
                            ? riskModel.Predict(BuildRiskFeatures()) : float.NaN;
            Debug.Log($"[Refine 진단] surrogate='{surrogateResourceName}' loaded={(riskModel != null && riskModel.Loaded)} scale={surrogateRewardScale} " +
                      $"| 빈패커시작 예측위험={rawRisk:F4}  startFinal(Score)={startFinal:F4}  (정상 p99: 위험≈0.50·Score≈−0.50)");
        }
        if (verboseLog) Debug.Log($"[Refine 시작] {placed.Count}개, Final={prevFinal:F3}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) 높이맵
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                float h = (HeightAt(cc.x, cc.y) - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM);
                sensor.AddObservation(Mathf.Clamp01(h));
            }
        // 2) CoG 위치 (코너 원점 정규화 [0,1]: 0=좌/뒤, 1=우/앞)
        Vector3 cog = Cog();
        sensor.AddObservation(MaxX > 1e-6f ? cog.x / MaxX : 0f);
        sensor.AddObservation(MaxZ > 1e-6f ? cog.z / MaxZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));
        // 3) 총질량
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));
        // 4) CoG 편차(절대, 균형중심 W/2·L/2 기준, 0=중앙 1=끝)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x - HalfX) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z - HalfZ) / HalfZ : 0f);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(아이템): 케이스마다 아이템 수가 달라 numItems(=최대)보다 적을 수 있음.
        //   실재하지 않는 인덱스(placed.Count 이상)를 막아 무의미한 no-op 스텝을 줄인다.
        for (int i = placed.Count; i < numItems; i++)
            mask.SetActionEnabled(0, i, false);

        // branch2(회전)은 마스킹 불가(어떤 아이템을 고를지에 의존 → 조합 마스킹 불가, ML-Agents는 브랜치별 독립).
        // branch1(셀)은 아래에서 막는다. 브랜치 독립 마스킹이라 겹침(아이템·회전 조합 의존)은 못 막고,
        // "어떤 화물도 못 놓는 셀"만 근사로 제거한다.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                bool block =
                    // (1) 높이 한도까지 꽉 찬 셀 — 그 위엔 아무것도 못 올림
                    HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f ||
                    // (2) 최소 화물조차 중심으로 놓으면 트레이를 벗어나는 가장자리 셀 (코너 원점 [0,W]·[0,L])
                    cc.x - minHalfXZ < -1e-4f || cc.x + minHalfXZ > MaxX + 1e-4f ||
                    cc.y - minHalfXZ < -1e-4f || cc.y + minHalfXZ > MaxZ + 1e-4f;
                if (block) mask.SetActionEnabled(1, r * cols + c, false);
            }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int itemIdx = actions.DiscreteActions[0];
        int cellIdx = actions.DiscreteActions[1];
        int rot = actions.DiscreteActions[2];

        if (TryRelocate(itemIdx, cellIdx, rot))
        {
            float now = Score();
            AddReward(now - prevFinal);      // ΔScore (개선분). 누적 = 시작 대비 개선 (CGS Final 또는 −surrogate위험)
            prevFinal = now;
            validMoves++;
        }
        else
        {
            AddReward(-invalidMovePenalty);  // 무효 이동 = 되돌림 + 작은 벌점 (fail-out 없음)
        }

        if (++stepCount >= stepsPerEpisode)
        {
            // ── 계측: 유효이동 비율·빈패커 대비 개선량을 TensorBoard로 ──
            var stats = Academy.Instance.StatsRecorder;
            stats.Add("Refine/ValidMoveRate", validMoves / (float)stepsPerEpisode);
            stats.Add("Refine/FinalImprovement", prevFinal - startFinal);
            stats.Add("Refine/FinalAbsolute", prevFinal);
            // 케이스별 개선량·유효이동률 — 어느 케이스에서 붕괴(개선 실패)하는지 추적 (커리큘럼 검증)
            if (caseSet.Count > 1)
            {
                stats.Add($"RefineCase/Improve_{curCaseIdx}", prevFinal - startFinal);
                stats.Add($"RefineCase/ValidRate_{curCaseIdx}", validMoves / (float)stepsPerEpisode);
            }
            if (verboseLog) Debug.Log($"[Refine 종료] Final={prevFinal:F3} (시작 {startFinal:F3}, Δ{prevFinal - startFinal:+0.000;-0.000}), 유효 {validMoves}/{stepsPerEpisode}");
            if (saveLayoutOnComplete) SaveLayout();
            EndEpisode();
        }
    }

    /// <summary>item i를 셀·회전으로 재배치. 유효하면 이동+true, 무효면 원위치 복구+false. (인덱스 유지)</summary>
    private bool TryRelocate(int itemIdx, int cellIdx, int rot)
    {
        if (itemIdx < 0 || itemIdx >= placed.Count) return false;

        var old = placed[itemIdx];
        CargoType type = old.type;
        int c = cellIdx % cols, r = cellIdx / cols;
        Vector2 cc = CellCenter(c, r);
        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;

        placed.RemoveAt(itemIdx);                                  // 자기 자신 빼고
        float restBottom = RestBottom(cc.x, cc.y, half);           // 남은 화물 위에 낙하 안착
        var cand = new RuleChecker.PlacedItem
        {
            type = type,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
            halfSize = half
        };

        if (!rules.IsValid(placed, cand))
        {
            placed.Insert(itemIdx, old);                           // 무효 → 원위치 복구
            return false;
        }
        placed.Insert(itemIdx, cand);                              // 유효 → 이동본으로 교체(인덱스 유지)
        return true;
    }

    // ── 지오메트리 헬퍼 (placed는 호출 시점의 현재 배치) ──────────────────────
    private Vector2 CellCenter(int c, int r)   // rear-left 코너 원점
    {
        float cw = ruleConfig.trayLateralM / cols, cd = ruleConfig.trayLengthM / rows;
        return new Vector2((c + 0.5f) * cw, (r + 0.5f) * cd);
    }

    /// <summary>(x,z)에 half 화물을 떨어뜨렸을 때 안착 바닥 y. (placed는 자기 자신 제거된 상태로 호출)</summary>
    private float RestBottom(float x, float z, Vector3 half)
    {
        float rest = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) < half.x + p.halfSize.x - 1e-4f &&
                Mathf.Abs(p.center.z - z) < half.z + p.halfSize.z - 1e-4f)
                rest = Mathf.Max(rest, p.center.y + p.halfSize.y);
        return rest;
    }

    private float HeightAt(float x, float z)
    {
        float h = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) < p.halfSize.x - 1e-4f &&
                Mathf.Abs(p.center.z - z) < p.halfSize.z - 1e-4f)
                h = Mathf.Max(h, p.center.y + p.halfSize.y);
        return h;
    }

    private Vector3 Cog()
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { float pm = p.type.massKg; m += pm; w += pm * p.center; }
        return m > 1e-6f ? w / m : Vector3.zero;
    }

    private float TotalMass()
    {
        float m = 0f; foreach (var p in placed) m += p.type.massKg; return m;
    }

    /// <summary>현재 refined 배치를 동적 주행 입력용 JSON(CargoLayoutFile)으로 저장. PlacementAgent.SaveLayout과 동일 포맷.</summary>
    private void SaveLayout()
    {
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in placed)
        {
            if (p.type == null) continue;
            Vector3 s = p.type.sizeM;
            float asIs    = Mathf.Abs(p.halfSize.x * 2f - s.x) + Mathf.Abs(p.halfSize.z * 2f - s.z);
            float swapped = Mathf.Abs(p.halfSize.x * 2f - s.z) + Mathf.Abs(p.halfSize.z * 2f - s.x);
            bool rot90 = swapped < asIs - 1e-5f;
            file.cargo.Add(new CargoLayoutEntry
            {
                type = p.type.name,
                localPos = p.center,
                localEuler = rot90 ? new Vector3(0f, 90f, 0f) : Vector3.zero,
                secured = true
            });
        }
        string dir = Path.Combine(Application.dataPath, "Data/Results");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, layoutOutName + ".json");
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
        Debug.Log($"[RefinementAgent] 배치 저장: {placed.Count}개 → {path}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    /// <summary>배치 점수(높을수록 좋음). surrogate 모드면 −리스크×배율, 아니면 기존 CGS Final. ΔScore가 스텝 보상.</summary>
    private float Score()
    {
        if (useSurrogateReward && riskModel != null && riskModel.Loaded)
            return -riskModel.Predict(BuildRiskFeatures()) * surrogateRewardScale;
        return reward.Final(placed).total;
    }

    /// <summary>
    /// surrogate 입력 13피처 (PlacementAgent.BuildRiskFeatures와 동일 = 두 에이전트 공정비교).
    /// Unity 규약(x=폭·y=높이·z=길이), 관성은 축정렬 박스(yaw0/90) D=2·halfSize, 평행축(적재물 CoG).
    /// </summary>
    private Dictionary<string, float> BuildRiskFeatures()
    {
        Vector3 cog = Cog();
        float ixx = 0f, iyy = 0f, izz = 0f, maxTop = ruleConfig.floorTopY;
        foreach (var p in placed)
        {
            float m = p.type.massKg;
            float Dx = p.halfSize.x * 2f, Dy = p.halfSize.y * 2f, Dz = p.halfSize.z * 2f;
            float ibx = m / 12f * (Dy * Dy + Dz * Dz);
            float iby = m / 12f * (Dx * Dx + Dz * Dz);
            float ibz = m / 12f * (Dx * Dx + Dy * Dy);
            Vector3 off = p.center - cog;
            ixx += ibx + m * (off.y * off.y + off.z * off.z);
            iyy += iby + m * (off.x * off.x + off.z * off.z);
            izz += ibz + m * (off.x * off.x + off.y * off.y);
            maxTop = Mathf.Max(maxTop, p.center.y + p.halfSize.y);
        }
        return new Dictionary<string, float>
        {
            { "TargetSpeedKmh", targetSpeedKmh },
            { "RoadBankAngleDeg", roadBankAngleDeg },
            { "RoadSlopeDeg", roadSlopeDeg },
            { "VehicleBaseMassKg", vehicleBaseMassKg },
            { "CargoCount", placed.Count },
            { "TotalMassKg", TotalMass() * modelMassScaleKg },
            { "CogX", cog.x },
            { "CogY", cog.y },
            { "CogZ", cog.z },
            { "MaxHeightM", maxTop },
            { "InertiaXX", ixx },
            { "InertiaYY", iyy },
            { "InertiaZZ", izz },
        };
    }
}
