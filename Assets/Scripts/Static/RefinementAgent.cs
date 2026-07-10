using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// v2 — Refinement 강화학습. 빈패커(Dense) 완성 배치에서 **시작** → RL이 화물을 "재배치(relocate)"해 보상 개선.
/// 시작이 유효 배치 + 무효 이동은 되돌림(no-op) → **붕괴 물리적 불가**. from-scratch PlacementAgent(v1)과 독립.
///
/// - 관측: 높이맵(cols×rows) + CoG(3) + 질량(1) + CoG편차(2)  = obs
/// - 행동: (아이템 index · 목표 셀 · 회전2) — item 하나를 집어 다른 셀로 옮김
/// - 보상: Δ목표함수(이동 후 − 이동 전). 누적 = "빈패커 대비 얼마나 개선했나". 무효 이동은 작은 페널티.
/// - 목표함수 = surrogate_risk_model_v499.onnx가 예측한 risk_score를 그대로 대체 사용
///   (기존 RewardCalculator.Final의 LE/CGS/SS 가중합은 더 이상 안 씀 — 2026-07 결정: "A. 완전 대체").
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class RefinementAgent : Agent, IPlacedCargoView
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

    [Header("에피소드")]
    [Tooltip("한 에피소드에 허용하는 재배치 수")]
    public int stepsPerEpisode = 25;
    [Tooltip("무효 이동(겹침/이탈) 시 작은 페널티")]
    public float invalidMovePenalty = 0.02f;

    [Header("Surrogate 위험도 모델 (완전 대체 — RewardCalculator.Final 대신 이 예측값의 음수를 보상으로 씀)")]
    [Tooltip("surrogate_risk_model_v499.onnx를 변환한 트리데이터로 로드한 RiskModel " +
             "(resourceName = \"surrogate_risk_model_v499_treedata\" 로 설정할 것. 비우면 자동 검색)")]
    public RiskModel surrogateModel;
    [Tooltip("risk_score(대략 0.17~0.54)가 너무 작아 학습 신호가 약하므로 곱해서 쓰는 배율")]
    public float rewardScale = 200f;
    [Tooltip("CoG 높이가 낮을수록 주는 보조 가산점 크기. surrogate가 CogY엔 거의 무감각해서(PDP로 확인됨) " +
             "\"낮게 쌓기\"를 직접 유도하려면 이 보조항이 필요함. 0이면 끔.")]
    public float heightBonusWeight = 15f;

    [Header("배치 저장 (추론/검증용 — ⚠️ 학습 중엔 반드시 OFF)")]
    [Tooltip("켜면 에피소드 종료 시 시작(빈패커)/최종(재배치) 배치를 각각 JSON으로 저장 (동적 주행 입력용)")]
    public bool saveLayoutOnComplete = false;
    [Tooltip("저장 파일명 접두어. Assets/Data/Results/<접두어>_before.json, _after.json")]
    public string layoutOutPrefix = "rl_refine";

    [Header("최고 기록 추적 (학습 중에도 항상 켜둘 것 — 가볍고, 새 기록 나올 때만 저장)")]
    [Tooltip("에피소드 끝(마지막 스텝)이 아니라, 지금까지 시도한 모든 배치(에피소드 시작 포함) 중 " +
             "목표함수가 가장 높았던 걸 계속 추적해서 Assets/Data/Results/rl_refine_best_ever.json 으로 자동 저장. " +
             "여러 에피소드·학습 전체에 걸쳐 딱 1개(진짜 최고 기록)만 남음.")]
    public bool trackGlobalBest = true;
    private readonly List<RuleChecker.PlacedItem> globalBestPlaced = new List<RuleChecker.PlacedItem>();
    private float globalBestObjective = float.NegativeInfinity;
    private bool startLayoutSaved;  // 시작 배치(rl_refine_start.json)를 이미 저장했는지 (결정적이라 1번만 저장)

    [Header("디버그")]
    public bool verboseLog = false;

    // ── 내부 ──
    private RuleChecker rules;
    private BinPacker packer;
    private List<CargoType> manifestList;                                   // 시작 배치용
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();
    private readonly List<RuleChecker.PlacedItem> startPlaced = new List<RuleChecker.PlacedItem>(); // 에피소드 시작(빈패커) 배치 스냅샷 — 저장용
    private int numItems;
    private int stepCount;
    private float prevFinal;
    private bool setupDone;
    private float startFinal;      // 에피소드 시작(빈패커) 목표함수값 — 개선량 계산 기준
    private int validMoves;        // 이번 에피소드 유효 이동 수 (계측)
    private float minHalfXZ;       // manifest 중 가장 작은 화물의 최소 반치수 (경계 마스킹용)
    private float lastRiskScore;   // 최근 예측 risk_score (통계·로그용)
    private float lastHeightBonus; // 최근 낮은높이 보너스(0~1, 통계·로그용)

    // ── surrogate 입력 스케일 (cargo_ppo_demo.py / truck_config.py와 동일) ──
    private const float SCALE_TO_REAL = 10f;   // 1:10 목업 → 실차
    private const float MASS_SCALE = 100f;     // 목업 질량 → 실차 등가 질량 (surrogate 학습 범위 41~700kg에 맞춤)
    private const float FIXED_TARGET_SPEED_KMH = 60f;
    private const float FIXED_ROAD_BANK_DEG = 0f;
    private const float FIXED_ROAD_SLOPE_DEG = 0f;
    private const float FIXED_VEHICLE_BASE_MASS_KG = 3500f;

    /// <summary>시각화용 읽기 전용 배치.</summary>
    public IReadOnlyList<RuleChecker.PlacedItem> PlacedItems => placed;

    // ── IPlacedCargoView (PlacementVisualizer 공용 인터페이스) ──
    public RuleConfig RuleConfig => ruleConfig;
    public int Cols => cols;
    public int Rows => rows;

    private float HalfX => ruleConfig.trayLateralM * 0.5f;
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;
    private int NumCells => cols * rows;
    private int ObsSize => NumCells + 3 + 1 + 2;   // 높이맵 + CoG(3) + 질량(1) + 편차(2)

    private void Awake() => Setup();
    public override void Initialize() => Setup();

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        rules = new RuleChecker(ruleConfig);
        packer = new BinPacker(ruleConfig, rewardConfig, cols, rows) { mode = startPackMode };

        if (surrogateModel == null) surrogateModel = FindObjectOfType<RiskModel>();
        if (surrogateModel == null)
            Debug.LogError("[RefinementAgent] surrogate RiskModel 미지정 — 보상이 항상 0이 됩니다. " +
                "surrogate_risk_model_v499_treedata를 resourceName으로 지정한 RiskModel을 씬에 두고 연결하세요.");

        manifestList = CargoManifest.Resolve(startManifest, "", out _);
        numItems = Mathf.Max(1, manifestList.Count);

        // 경계 마스킹용: 회전 포함, 가장 작은 화물의 최소 반치수. 이보다 좁게 남은 가장자리 셀은
        // 어떤 화물 중심을 놓아도 트레이를 벗어나므로 미리 막는다.
        minHalfXZ = float.MaxValue;
        foreach (var t in manifestList)
            minHalfXZ = Mathf.Min(minHalfXZ, Mathf.Min(t.sizeM.x, t.sizeM.z) * 0.5f);
        if (minHalfXZ == float.MaxValue) minHalfXZ = 0f;

        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(numItems, NumCells, 2); // 아이템·셀·회전
        if (MaxStep == 0) MaxStep = stepsPerEpisode + 2;

        Debug.Log($"[RefinementAgent] obs={ObsSize}, action=({numItems},{NumCells},2), 시작화물={numItems}개, mode={startPackMode}");
    }

    public override void OnEpisodeBegin()
    {
        // 빈패커로 시작 배치 생성 (결정론적 → 매 에피소드 동일 = boxpack001)
        var unplaced = new List<CargoType>();
        var packed = packer.Pack(manifestList, unplaced);
        placed.Clear();
        foreach (var p in packed)
            placed.Add(new RuleChecker.PlacedItem { type = p.type, center = p.center, halfSize = p.halfSize });

        startPlaced.Clear();
        startPlaced.AddRange(placed); // 시작(빈패커) 배치 스냅샷 — 저장·비교용

        stepCount = 0;
        validMoves = 0;
        prevFinal = placed.Count > 0 ? ComputeObjective(placed) : 0f;
        startFinal = prevFinal;
        if (trackGlobalBest) TryUpdateGlobalBest(placed, prevFinal);
        if (!startLayoutSaved)   // 시작 배치는 결정적(매 에피소드 동일)이라 딱 1번만 저장하면 충분
        {
            startLayoutSaved = true;
            string dir = Path.Combine(Application.dataPath, "Data/Results");
            Directory.CreateDirectory(dir);
            WriteLayoutJson(startPlaced, Path.Combine(dir, "rl_refine_start.json"));
        }
        if (verboseLog) Debug.Log($"[Refine 시작] {placed.Count}개, 목표함수={prevFinal:F3} (risk_score={lastRiskScore:F4})");
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
        // 2) CoG
        Vector3 cog = Cog();
        sensor.AddObservation(HalfX > 1e-6f ? cog.x / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? cog.z / HalfZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));
        // 3) 총질량
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));
        // 4) CoG 편차(절대)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z) / HalfZ : 0f);
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(아이템)·branch2(회전)은 마스킹 불가:
        //  - 아이템 16개는 전부 실재 → 다 유효 후보.
        //  - 회전은 어떤 아이템을 고를지에 의존 → 조합 마스킹 불가(ML-Agents는 브랜치별 독립 마스킹).
        // branch1(셀)만 막는다. 브랜치 독립 마스킹이라 겹침(아이템·회전 조합 의존)은 못 막고,
        // "어떤 화물도 못 놓는 셀"만 근사로 제거한다.
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                bool block =
                    // (1) 높이 한도까지 꽉 찬 셀 — 그 위엔 아무것도 못 올림
                    HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f ||
                    // (2) 최소 화물조차 중심으로 놓으면 트레이를 벗어나는 가장자리 셀
                    cc.x - minHalfXZ < -HalfX - 1e-4f || cc.x + minHalfXZ > HalfX + 1e-4f ||
                    cc.y - minHalfXZ < -HalfZ - 1e-4f || cc.y + minHalfXZ > HalfZ + 1e-4f;
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
            float now = ComputeObjective(placed);
            AddReward(now - prevFinal);      // Δ목표함수 (개선분). 누적 = 시작 대비 개선
            prevFinal = now;
            validMoves++;
            if (trackGlobalBest) TryUpdateGlobalBest(placed, now);
        }
        else
        {
            AddReward(-invalidMovePenalty);  // 무효 이동 = 되돌림 + 작은 벌점 (fail-out 없음)
        }

        if (++stepCount >= stepsPerEpisode)
        {
            // ── 계측: 유효이동 비율·빈패커 대비 개선량·surrogate risk_score를 TensorBoard로 ──
            var stats = Academy.Instance.StatsRecorder;
            stats.Add("Refine/ValidMoveRate", validMoves / (float)stepsPerEpisode);
            stats.Add("Refine/FinalImprovement", prevFinal - startFinal);
            stats.Add("Refine/FinalAbsolute", prevFinal);
            stats.Add("Surrogate/RiskScore", lastRiskScore);
            stats.Add("Surrogate/LowHeightBonus", lastHeightBonus);
            if (verboseLog) Debug.Log($"[Refine 종료] 목표함수={prevFinal:F3} (시작 {startFinal:F3}, Δ{prevFinal - startFinal:+0.000;-0.000}), " +
                $"risk_score={lastRiskScore:F4}, heightBonus={lastHeightBonus:F3}, 유효 {validMoves}/{stepsPerEpisode}");
            if (saveLayoutOnComplete) SaveLayouts();
            EndEpisode();
        }
    }

    /// <summary>목표함수 = -surrogate risk_score × rewardScale + heightBonusWeight × (낮을수록 1에 가까운 보너스).
    /// surrogate만으로는 CogY(높이)에 보상 신호가 거의 없어서(PDP로 확인: 0.2~2.6m 구간 완전히 평평),
    /// "낮게 쌓을수록 좋다"는 상식을 직접 유도하려고 보조항을 더함. 높을수록 좋음 관례 유지.</summary>
    private float ComputeObjective(IReadOnlyList<RuleChecker.PlacedItem> placedList)
    {
        if (surrogateModel == null || placedList == null || placedList.Count == 0)
        {
            lastRiskScore = 0f;
            lastHeightBonus = 0f;
            return 0f;
        }
        float[] features = BuildSurrogateFeatures(placedList);
        lastRiskScore = surrogateModel.Predict(features);
        lastHeightBonus = ComputeLowHeightBonus(placedList);
        return -lastRiskScore * rewardScale + heightBonusWeight * lastHeightBonus;
    }

    /// <summary>가장 높은 화물의 top이 낮을수록 1, 높이한도에 닿으면 0 (목업 스케일 그대로, 무차원 비율이라 스케일 무관).
    /// CoG(무게가중평균) 대신 최고점을 쓰는 이유: CoG로 하면 "무거운 걸 바닥에만 깔면 가벼운 건 눈치 안 보고
    /// 위로 쌓아도 평균은 낮게 유지"되는 편법이 통함 — 최고점 기준이어야 실제로 낮게 쌓게 유도됨.</summary>
    private float ComputeLowHeightBonus(IReadOnlyList<RuleChecker.PlacedItem> placedList)
    {
        float maxTop = ruleConfig.floorTopY;
        foreach (var p in placedList) if (p.Top > maxTop) maxTop = p.Top;
        float norm = Mathf.Clamp01((maxTop - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));
        return 1f - norm;
    }

    /// <summary>
    /// 현재 배치를 surrogate 입력 13피처로 변환.
    /// 스케일: 목업(1:10) → 실차(×10 위치, ×100 질량) — surrogate 학습 범위(화물 41~700kg)에 맞춤.
    /// 축: Unity 실제 축(x=좌우,y=높이,z=길이) → 모델 축(truck_config.py 기준 x=길이,y=높이,z=좌우)으로 스왑.
    /// 관성모멘트도 같은 이유로 XX↔ZZ를 바꿔 넣음(축이 바뀌면 롤/피치 축도 같이 바뀌므로).
    /// 관성은 각 화물을 점질량(자체 회전관성 무시)으로 본 python compute_features()와 동일한 근사를 사용
    /// (LoadCalculator.LayoutInertiaDiag는 자체관성까지 포함해 학습 데이터와 정의가 달라 여기선 안 씀).
    /// </summary>
    private float[] BuildSurrogateFeatures(IReadOnlyList<RuleChecker.PlacedItem> placedList)
    {
        float totalMassReal = 0f;
        Vector3 weighted = Vector3.zero;   // sum(m_real * center_mockup)
        float maxTopMockup = float.NegativeInfinity;

        foreach (var p in placedList)
        {
            float mReal = p.Mass * MASS_SCALE;
            totalMassReal += mReal;
            weighted += p.center * mReal;
            if (p.Top > maxTopMockup) maxTopMockup = p.Top;
        }

        Vector3 cogReal = (totalMassReal > 1e-6f ? weighted / totalMassReal : Vector3.zero) * SCALE_TO_REAL;
        float maxHeightReal = maxTopMockup * SCALE_TO_REAL;

        // 점질량 관성(자체 회전관성 제외) — Unity 실좌표계(x=좌우,y=높이,z=길이) 기준
        float ixxUnity = 0f, iyyUnity = 0f, izzUnity = 0f;
        foreach (var p in placedList)
        {
            float m = p.Mass * MASS_SCALE;
            Vector3 d = p.center * SCALE_TO_REAL - cogReal;
            ixxUnity += m * (d.y * d.y + d.z * d.z);
            iyyUnity += m * (d.x * d.x + d.z * d.z);
            izzUnity += m * (d.x * d.x + d.y * d.y);
        }

        // Unity(x=좌우,z=길이) → 모델(x=길이,z=좌우): CoG·관성 모두 x↔z 스왑
        float modelCogX = cogReal.z, modelCogY = cogReal.y, modelCogZ = cogReal.x;
        float modelIxx = izzUnity, modelIyy = iyyUnity, modelIzz = ixxUnity;

        return new float[]
        {
            FIXED_TARGET_SPEED_KMH, FIXED_ROAD_BANK_DEG, FIXED_ROAD_SLOPE_DEG, FIXED_VEHICLE_BASE_MASS_KG,
            placedList.Count, totalMassReal,
            modelCogX, modelCogY, modelCogZ,
            maxHeightReal,
            modelIxx, modelIyy, modelIzz
        };
    }

    /// <summary>지금까지(이 Play 세션 전체) 본 모든 배치 중 목표함수가 가장 높았던 것만 계속 갱신·저장.
    /// 에피소드가 40스텝 안에서 개선되다 다시 나빠지는 경우도 있고, 에피소드 "끝"이 꼭 최선은 아니므로
    /// 실제로 원하는 건 "여태 나온 것 중 최고"임 — 이걸 별도로 계속 추적.</summary>
    private void TryUpdateGlobalBest(IReadOnlyList<RuleChecker.PlacedItem> placedList, float objective)
    {
        if (objective <= globalBestObjective) return;
        globalBestObjective = objective;
        globalBestPlaced.Clear();
        globalBestPlaced.AddRange(placedList);

        string dir = Path.Combine(Application.dataPath, "Data/Results");
        Directory.CreateDirectory(dir);
        WriteLayoutJson(globalBestPlaced, Path.Combine(dir, "rl_refine_best_ever.json"));
        // AssetDatabase.Refresh()는 여기서 안 함 — 학습 중 새 기록이 자주 나올 수 있어 매번 리프레시하면
        // 에디터가 버벅임. 파일 자체는 바로 쓰이니 나중에 확인할 때만 한 번 리프레시하면 됨.
        if (verboseLog) Debug.Log($"[RefinementAgent] 새 최고기록! 목표함수={objective:F3} " +
            $"(risk={lastRiskScore:F4}, heightBonus={lastHeightBonus:F3}) -> rl_refine_best_ever.json");
    }

    /// <summary>추론/검증용: 시작(빈패커)·최종(재배치) 배치를 각각 CargoLayoutFile JSON으로 저장
    /// (동적 주행 입력용, Assets/Data/Results/&lt;prefix&gt;_before.json / _after.json).</summary>
    private void SaveLayouts()
    {
        string dir = Path.Combine(Application.dataPath, "Data/Results");
        Directory.CreateDirectory(dir);
        WriteLayoutJson(startPlaced, Path.Combine(dir, layoutOutPrefix + "_before.json"));
        WriteLayoutJson(placed, Path.Combine(dir, layoutOutPrefix + "_after.json"));
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    private void WriteLayoutJson(IReadOnlyList<RuleChecker.PlacedItem> placedList, string path)
    {
        var file = new CargoLayoutFile
        {
            version = 1,
            bed = new CargoLayoutBed { widthX = ruleConfig.trayLateralM, lengthZ = ruleConfig.trayLengthM, wallHeight = 0.06f },
            cargo = new List<CargoLayoutEntry>()
        };
        foreach (var p in placedList)
        {
            if (p.type == null) continue;
            Vector3 s = p.type.sizeM;
            float asIs = Mathf.Abs(p.halfSize.x * 2f - s.x) + Mathf.Abs(p.halfSize.z * 2f - s.z);
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
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
        Debug.Log($"[RefinementAgent] 배치 저장: {placedList.Count}개 → {path}");
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

    /// <summary>학습된 모델/트레이너 연결 없이 에디터에서 Play만 눌러도 동작 확인 가능하도록 하는 임시 행동.
    /// 무작위 아이템·셀·회전 — 학습용 데모가 아니라 "보상 파이프라인이 도는지" 확인용.</summary>
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;
        d[0] = Random.Range(0, Mathf.Max(1, placed.Count));
        d[1] = Random.Range(0, NumCells);
        d[2] = Random.Range(0, 2);
    }

    // ── 지오메트리 헬퍼 (placed는 호출 시점의 현재 배치) ──────────────────────
    private Vector2 CellCenter(int c, int r)
    {
        float cw = ruleConfig.trayLateralM / cols, cd = ruleConfig.trayLengthM / rows;
        return new Vector2(-HalfX + (c + 0.5f) * cw, -HalfZ + (r + 0.5f) * cd);
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
}
