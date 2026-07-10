using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Policies;
using UnityEngine;

/// <summary>
/// S3 — 정적 배치 강화학습 에이전트 (ML-Agents, PPO).
/// 매 스텝 "남은 화물 중 무엇을(branch0) · 어느 격자셀에(branch1) · 어느 회전으로(branch2)" 배치할지 결정.
/// RuleChecker로 불가 행동 마스킹, RewardCalculator로 스텝+최종 보상.
/// 에피소드 = 랜덤 화물 목록(manifest) 하나를 다 놓거나 더 못 놓을 때까지.
///
/// 좌표계: Unity 로컬 m (x=좌우 21cm, y=높이 27한도, z=주행/길이 62cm), 원점=트레이 중심, 바닥 y=floorTop.
/// 격자: cols(x) × rows(z) 셀. 셀 중심에 화물을 "떨어뜨려" 바닥/위 화물에 안착.
/// </summary>
[RequireComponent(typeof(BehaviorParameters))]
public class PlacementAgent : Agent, IPlacedCargoView
{
    [Header("규제/보상 설정")]
    public RuleConfig ruleConfig = new RuleConfig();
    public RewardConfig rewardConfig = new RewardConfig();

    [Header("격자 (x=cols, z=rows) — 2cm급 (2026-07-06, 1cm→2cm: 탐색벽 완화)")]
    public int cols = 11;   // 좌우 21cm / 11 ≈ 1.9cm (홀수 → x=0 중앙셀 정렬)
    public int rows = 31;   // 길이 61cm / 31 ≈ 2.0cm (홀수 → z=0 중앙셀 정렬)

    [Header("에피소드 화물 목록 (커리큘럼 1단계: 3~5개)")]
    public int manifestMin = 3;
    public int manifestMax = 5;

    [Header("Manifest 현실성/게이팅")]
    [Tooltip("② 현실성(항상 적용): 파이프 밑면 폭(좌우) 합이 트레이 폭의 이 비율을 못 넘게. 파이프는 전장을 독점하고 위 적재 불가(H6)라, 다른 화물 자리를 남겨야 함")]
    [Range(0.3f, 1f)] public float pipeWidthBudget = 0.7f;
    [Tooltip("① 게이팅(데모 녹화 시만): 교사(빈패커)가 전부 실을 수 있는 조합만 채택해 데모를 깨끗이 유지. 재추첨 최대 횟수")]
    public int manifestMaxTries = 40;
    [Tooltip("에피소드에 쓸 화물 종류 풀 (트레이에 정상 배치 가능한 것만). 비우면 기본 12종")]
    public string[] usableTypeIds = {
        "B-001","B-002","B-003","B-004","B-005","B-006",
        "C-001","T-001","P-001","P-002","P-003","S-001"
    };

    [Header("단일 케이스 (고정 manifest) — boxpack PPO용")]
    [Tooltip("켜면 매 에피소드 아래 fixedManifest 를 그대로 사용(랜덤·게이팅풀 무시). 화물 풀도 이 manifest 타입으로 자동 구성 → 액션공간 최소.")]
    public bool useFixedManifest = false;
    [Tooltip("고정 적재 목록 (화물 id, 개수). useFixedManifest 켜면 매 에피소드 이 목록 그대로.")]
    public ManifestEntry[] fixedManifest = new ManifestEntry[0];
    private int[] fixedRemaining;   // 고정 manifest → pool 인덱스별 개수
    private int fixedTotal;

    [Header("A안: 게이팅 manifest 풀 (BC 정합, 권장)")]
    [Tooltip("켜면 오프라인 생성된 '교사 완주 가능' manifest 풀에서 뽑음 — 리셋당 Pack 0회(timeout 없음)·데모와 분포 일치. 파일 없으면 런타임 샘플링으로 자동 폴백")]
    public bool useGatedPool = true;
    [Tooltip("Assets/Data/ 하위 풀 파일명 (BinPackerVisualizer 'Generate Gated Manifest Pool' 로 생성)")]
    public string gatedPoolFileName = "gated_manifests.txt";
    private List<int[]> gatedManifests;   // 각 manifest = pool 인덱스 배열

    [Header("무효 행동 처리")]
    public float invalidPenalty = 0.05f;
    public int maxInvalidPerEpisode = 20;

    [Header("Option C: 보장된 완주 (붕괴 방지, 2026-07-06)")]
    [Tooltip("켜면 RL이 무효 행동할 때 fail-out(−1.5) 대신 빈패커가 대신 한 수 놓아 에피소드를 완주 → 보상이 항상 유효배치라 정책 붕괴(std→0) 방지. 데모/BC/GAIL·행동공간 불변. 최악이어도 빈패커(+0.7) 바닥.")]
    public bool guaranteedCompletion = true;
    [Tooltip("무효 행동 1회당 작은 페널티 (붕괴 유발 −1.5 절벽 대신). 유효 배치를 살짝 선호하게")]
    public float invalidStepPenalty = 0.02f;
    [Tooltip("교사도 못 놓아 완주 실패 시 남은 화물 1개당 감점 (완주 유도, 단 −1.5 절벽 아님)")]
    public float unplacedPenalty = 0.1f;

    [Header("배치 저장 (추론/검증용 — ⚠️ 학습 시엔 반드시 OFF)")]
    [Tooltip("켜면 에피소드 완주(전부 배치) 시 배치를 Assets/Data/Results/<layoutOutName>.json 으로 저장(동적 주행 입력용). 매 에피소드 덮어씀. 학습 중엔 수만 번 쓰므로 반드시 끌 것.")]
    public bool saveLayoutOnComplete = false;
    [Tooltip("저장 파일명 (확장자 제외). Assets/Data/Results/ 아래.")]
    public string layoutOutName = "rl_layout";

    [Header("디버그")]
    [Tooltip("켜면 배치/에피소드 결과를 콘솔에 찍음 (학습 시엔 끄기)")]
    public bool verboseLog = true;
    private int episodeIdx;

    // ── 내부 상태 ──
    private RuleChecker rules;
    private RewardCalculator reward;
    private List<CargoType> pool;                  // usableTypeIds → CargoType
    private int[] remaining;                        // 종류별 남은 수 (pool 인덱스)
    private readonly List<RuleChecker.PlacedItem> placed = new List<RuleChecker.PlacedItem>();

    /// <summary>시각화용 읽기 전용 배치 목록 (PlacementVisualizer 가 사용). 학습엔 영향 없음.</summary>
    public IReadOnlyList<RuleChecker.PlacedItem> PlacedItems => placed;

    // ── IPlacedCargoView (PlacementVisualizer 공용 인터페이스) ──
    public RuleConfig RuleConfig => ruleConfig;
    public int Cols => cols;
    public int Rows => rows;

    private int invalidCount;
    private int placedTarget;                       // 이번 에피소드 총 배치 목표 수
    private bool setupDone;                          // Awake/Initialize 중복 방지

    private float HalfX => ruleConfig.trayLateralM * 0.5f;
    private float HalfZ => ruleConfig.trayLengthM * 0.5f;
    private int NumCells => cols * rows;
    private int NumTypes => pool.Count;
    private int ObsSize => NumCells + 3 + 1 + 2 + NumTypes; // 높이맵 + CoG(3) + 질량(1) + CoG편차(2) + 남은목록

    // ⚠️ obs·ActionSpec은 Awake()에서 세팅해야 한다.
    // ML-Agents 2.0.2 LazyInitialize 순서: InitializeActuators() → Initialize() → InitializeSensors().
    // 즉 액추에이터는 Initialize()보다 먼저 ActionSpec을 읽으므로, Initialize()에서 바꾸면 마스킹 branch 크기가 안 맞아
    // "Invalid Action Masking: Action Mask is too large for specified branch" 발생. Awake()는 OnEnable(LazyInitialize)보다
    // 먼저 도므로 여기서 세팅하면 액추에이터 생성 전에 올바른 스펙이 반영된다.
    private void Awake() => Setup();

    public override void Initialize() => Setup();

    private void Setup()
    {
        if (setupDone) return;
        setupDone = true;

        rules = new RuleChecker(ruleConfig);
        reward = new RewardCalculator(rewardConfig, ruleConfig);

        var cat = new Dictionary<string, CargoType>();
        foreach (var t in CargoCatalog.CreateDefault()) if (t != null) cat[t.id] = t;
        pool = new List<CargoType>();
        if (useFixedManifest && fixedManifest != null && fixedManifest.Length > 0)
        {
            // 고정 manifest의 distinct 타입만 풀에 (등장 순서) → 액션공간 최소
            foreach (var e in fixedManifest)
                if (e != null && !string.IsNullOrWhiteSpace(e.typeId) && cat.TryGetValue(e.typeId.Trim(), out var t) && !pool.Contains(t))
                    pool.Add(t);
        }
        else
        {
            foreach (var id in usableTypeIds) if (cat.TryGetValue(id, out var t)) pool.Add(t);
        }

        if (useFixedManifest) BuildFixedRemaining();
        else if (useGatedPool) LoadGatedPool();

        // 관측 크기 · 행동 스펙 코드로 설정 (인스펙터 수동세팅 불필요)
        var bp = GetComponent<BehaviorParameters>();
        bp.BrainParameters.VectorObservationSize = ObsSize;
        bp.BrainParameters.ActionSpec = ActionSpec.MakeDiscrete(NumTypes, NumCells, 2);
        int epLen = useFixedManifest ? fixedTotal : manifestMax;
        if (MaxStep == 0) MaxStep = (epLen + maxInvalidPerEpisode) * 3;

        Debug.Log($"[PlacementAgent] obs={ObsSize}, action=({NumTypes},{NumCells},2), pool={NumTypes}종");
    }

    /// <summary>고정 manifest → pool 인덱스별 개수(fixedRemaining) + 총합(fixedTotal) 미리 계산.</summary>
    private void BuildFixedRemaining()
    {
        fixedRemaining = new int[pool.Count];
        var idIdx = new Dictionary<string, int>();
        for (int i = 0; i < pool.Count; i++) idIdx[pool[i].id] = i;
        fixedTotal = 0;
        foreach (var e in fixedManifest)
            if (e != null && !string.IsNullOrWhiteSpace(e.typeId) && idIdx.TryGetValue(e.typeId.Trim(), out int ix))
            {
                int n = Mathf.Max(0, e.count);
                fixedRemaining[ix] += n; fixedTotal += n;
            }
        Debug.Log($"[PlacementAgent] 고정 manifest 사용: 총 {fixedTotal}개 ({pool.Count}종)");
    }

    public override void OnEpisodeBegin()
    {
        placed.Clear();
        invalidCount = 0;
        remaining = new int[NumTypes];

        if (useFixedManifest && fixedRemaining != null)
        {
            // 단일 케이스: 매 에피소드 같은 고정 manifest (랜덤 없음)
            placedTarget = fixedTotal;
            System.Array.Copy(fixedRemaining, remaining, NumTypes);
        }
        else if (useGatedPool && gatedManifests != null && gatedManifests.Count > 0)
        {
            // A안: 오프라인 게이팅 풀에서 뽑기 — 리셋당 Pack 0회(timeout 없음)·데모와 분포 일치.
            var m = gatedManifests[Random.Range(0, gatedManifests.Count)];
            placedTarget = m.Length;
            foreach (int ti in m) remaining[ti]++;
        }
        else
        {
            // 폴백: 런타임 샘플링 — ② 현실성(항상) + 데모 녹화면 ① 교사 완주 게이팅.
            // 마지막 시도(manifestMaxTries)에선 조건 불만족이어도 그대로 채택(무한루프 방지).
            if (binPackerHeuristic && heuristicPacker == null)
                heuristicPacker = new BinPacker(ruleConfig, rewardConfig, cols, rows);
            for (int attempt = 0; ; attempt++)
            {
                System.Array.Clear(remaining, 0, remaining.Length);
                placedTarget = Random.Range(manifestMin, manifestMax + 1);
                var manifest = new List<CargoType>(placedTarget);
                for (int i = 0; i < placedTarget; i++)
                {
                    int ti = Random.Range(0, NumTypes);
                    remaining[ti]++;
                    manifest.Add(pool[ti]);
                }
                if (attempt >= manifestMaxTries) break;                 // 안전장치: 그만 채택

                if (!ManifestRealistic(manifest)) continue;             // ② 현실성 위반 → 재추첨
                if (binPackerHeuristic)                                  // ① 데모 녹화: 교사 완주 가능해야 채택
                {
                    var unplaced = new List<CargoType>();
                    heuristicPacker.Pack(manifest, unplaced);
                    if (unplaced.Count != 0) continue;                  // 못 실음 → 재추첨
                }
                break;                                                  // 조건 통과 → 채택
            }
        }

        if (verboseLog)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) sb.Append($"{pool[i].id}×{remaining[i]} ");
            Debug.Log($"[에피소드 {++episodeIdx} 시작] 실을 화물 {placedTarget}개: {sb}");
        }
    }

    /// <summary>② 현실성 제약: 실제 적재에서 나올 법한 조합인지. 총질량 ≤ 적재한도, 파이프 밑면 폭 합 ≤ 트레이 폭·예산.</summary>
    private bool ManifestRealistic(List<CargoType> m)
    {
        float mass = 0f, pipeWidth = 0f;
        foreach (var t in m)
        {
            if (t == null) continue;
            mass += t.massKg;
            if (t.shape == CargoShape.Pipe) pipeWidth += t.sizeM.x;   // 파이프 좌우 폭(밑면)
        }
        if (mass > ruleConfig.maxPayloadKg + 1e-4f) return false;                 // 적재중량 초과
        if (pipeWidth > ruleConfig.trayLateralM * pipeWidthBudget + 1e-4f) return false; // 파이프가 바닥 폭 독점
        return true;
    }

    /// <summary>A안: 오프라인 게이팅 풀 로드 (type id 콤마구분 1줄 = 1 manifest → pool 인덱스 배열).</summary>
    private void LoadGatedPool()
    {
        gatedManifests = new List<int[]>();
        string path = Path.Combine(Application.dataPath, "Data", gatedPoolFileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[PlacementAgent] 게이팅 풀 파일 없음: {path}\n→ 런타임 샘플링으로 폴백. (BinPackerVisualizer 'Generate Gated Manifest Pool' 로 먼저 생성하세요)");
            return;
        }
        var idToIdx = new Dictionary<string, int>();
        for (int i = 0; i < pool.Count; i++) idToIdx[pool[i].id] = i;

        int skipped = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var ids = line.Split(',');
            var idxs = new List<int>(ids.Length);
            bool ok = true;
            foreach (var id in ids)
            {
                if (idToIdx.TryGetValue(id.Trim(), out int idx)) idxs.Add(idx);
                else { ok = false; break; }   // 풀에 없는 종류 → 이 manifest 버림
            }
            if (ok && idxs.Count > 0) gatedManifests.Add(idxs.ToArray());
            else skipped++;
        }
        Debug.Log($"[PlacementAgent] 게이팅 풀 로드: {gatedManifests.Count}개 manifest{(skipped > 0 ? $" (스킵 {skipped})" : "")} ← {gatedPoolFileName}");
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // 1) 높이맵 (셀별 적재 높이 / 높이한도)
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                float h = (HeightAt(cc.x, cc.y) - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM);
                sensor.AddObservation(Mathf.Clamp01(h));
            }

        // 2) 현재 CoG (정규화)
        Vector3 cog = Cog();
        sensor.AddObservation(HalfX > 1e-6f ? cog.x / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? cog.z / HalfZ : 0f);
        sensor.AddObservation((cog.y - ruleConfig.floorTopY) / Mathf.Max(1e-4f, ruleConfig.heightLimitM));

        // 3) 총질량 / payload
        sensor.AddObservation(TotalMass() / Mathf.Max(1e-4f, ruleConfig.maxPayloadKg));

        // 4) CoG 편차 절대값(좌우·전후)
        sensor.AddObservation(HalfX > 1e-6f ? Mathf.Abs(cog.x) / HalfX : 0f);
        sensor.AddObservation(HalfZ > 1e-6f ? Mathf.Abs(cog.z) / HalfZ : 0f);

        // 5) 종류별 남은 수 (정규화)
        for (int i = 0; i < NumTypes; i++)
            sensor.AddObservation(remaining[i] / Mathf.Max(1f, placedTarget));
    }

    public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
    {
        // branch0(종류): 남은 수 0인 종류 마스킹
        for (int i = 0; i < NumTypes; i++)
            if (remaining[i] <= 0) mask.SetActionEnabled(0, i, false);

        // branch1(셀): 높이한도까지 꽉 찬 셀 마스킹 (그 위엔 아무것도 못 놓음)
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                Vector2 cc = CellCenter(c, r);
                if (HeightAt(cc.x, cc.y) >= ruleConfig.floorTopY + ruleConfig.heightLimitM - 1e-3f)
                    mask.SetActionEnabled(1, r * cols + c, false);
            }
        // branch2(회전)는 종류의존이라 마스킹 불가 → 무효 조합은 보상 페널티로 학습
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        int typeIdx = actions.DiscreteActions[0];
        int cellIdx = actions.DiscreteActions[1];
        int rot = actions.DiscreteActions[2]; // 0=그대로, 1=yaw90

        bool placedOk = TryPlace(typeIdx, cellIdx, rot);   // 유효하면 놓고 step 보상 + true

        if (!placedOk)
        {
            if (guaranteedCompletion)
            {
                // Option C: fail-out(−1.5) 대신 작은 페널티 + 교사가 대신 한 수 → 완주 보장 → 정책 붕괴 방지.
                AddReward(-invalidStepPenalty);
                if (!PlaceByTeacher())
                {
                    // 에이전트가 자리를 막아 교사도 못 놓음 → 완주 실패(단 −1.5 절벽 아님).
                    float partial = reward.Final(placed).total - unplacedPenalty * CountRemaining();
                    AddReward(partial);
                    if (verboseLog) Debug.Log($"[에피소드 {episodeIdx} 완주실패] {placed.Count}/{placedTarget} (교사도 막힘) R={partial:F3}");
                    EndEpisode();
                    return;
                }
            }
            else { Fail($"무효 typeIdx={typeIdx} cell={cellIdx} rot={rot}"); return; }  // 토글 off = 기존 fail-out
        }

        // 목록 다 놓음 → 최종 보상 + 종료
        if (AllPlaced())
        {
            var rf = reward.Final(placed);
            AddReward(rf.total);
            if (saveLayoutOnComplete) SaveLayout();   // 추론/검증용: 완성배치를 주행 입력 JSON으로
            if (verboseLog) Debug.Log($"[에피소드 {episodeIdx} 완료] 전부 배치! 최종보상 {rf}");
            EndEpisode();
        }
    }

    /// <summary>에이전트/교사 공통 배치: (종류,셀,회전)이 유효하면 놓고 step 보상 + true. 무효면 아무것도 안 하고 false.</summary>
    private bool TryPlace(int typeIdx, int cellIdx, int rot)
    {
        if (typeIdx < 0 || typeIdx >= NumTypes || remaining[typeIdx] <= 0) return false;

        CargoType type = pool[typeIdx];
        int c = cellIdx % cols, r = cellIdx / cols;
        Vector2 cc = CellCenter(c, r);
        Vector3 s = type.sizeM;
        Vector3 half = (rot == 1 ? new Vector3(s.z, s.y, s.x) : s) * 0.5f;  // yaw90이면 x↔z 스왑
        float restBottom = RestBottom(cc.x, cc.y, half);                   // 셀에서 낙하 안착
        var cand = new RuleChecker.PlacedItem
        {
            type = type,
            center = new Vector3(cc.x, restBottom + half.y, cc.y),
            halfSize = half
        };
        if (!rules.IsValid(placed, cand)) return false;

        placed.Add(cand);
        remaining[typeIdx]--;
        invalidCount = 0;
        AddReward(reward.Step(placed)); // 스텝 shaping
        if (verboseLog) Debug.Log($"  ✔ {type.id} 배치 @ 셀({c},{r}) rot{rot} → 높이 {cand.Top:F3}m | {placed.Count}/{placedTarget}");
        return true;
    }

    /// <summary>Option C 폴백: 교사(빈패커)의 다음 유효 최선 수를 대신 둔다. 놓을 수 있으면 true.</summary>
    private bool PlaceByTeacher()
    {
        if (heuristicPacker == null) heuristicPacker = new BinPacker(ruleConfig, rewardConfig, cols, rows);
        if (!heuristicPacker.Decide(placed, pool, remaining, out int ti, out int ci, out int ri)) return false;
        return TryPlace(ti, ci, ri);   // Decide는 유효를 보장 → TryPlace 성공
    }

    private int CountRemaining()
    {
        int n = 0;
        for (int i = 0; i < NumTypes; i++) n += remaining[i];
        return n;
    }

    /// <summary>현재 완성 배치를 동적 주행 입력용 JSON(CargoLayoutFile)으로 저장. 회전은 halfSize↔sizeM로 역산.</summary>
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
        Debug.Log($"[PlacementAgent] 배치 저장: {placed.Count}개 → {path}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    private void Fail(string reason = "")
    {
        AddReward(-invalidPenalty);
        invalidCount++;
        if (verboseLog)
            Debug.Log($"  ✘ 실패({invalidCount}/{maxInvalidPerEpisode}) {reason} | 누적 {placed.Count}/{placedTarget}개");
        if (invalidCount >= maxInvalidPerEpisode)
        {
            AddReward(-0.5f); // 반복 실패 = 큰 감점 후 종료
            if (verboseLog)
                Debug.Log($"[에피소드 {episodeIdx} 실패종료] {maxInvalidPerEpisode}회 헛짚음 → fail-out (−1.5) | {placed.Count}/{placedTarget}개만 배치");
            EndEpisode();
        }
    }

    [Header("휴리스틱 (BC 교사)")]
    [Tooltip("켜면 Heuristic이 빈패커(Stable)의 최선 행동을 시연 → Demonstration Recorder로 .demo 기록용. 끄면 기존 랜덤(비교 baseline)")]
    public bool binPackerHeuristic = true;
    private BinPacker heuristicPacker;

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var d = actionsOut.DiscreteActions;

        // BC 교사: 빈패커(Stable)의 "다음 한 수" — 유효·최고점 행동을 시연
        if (binPackerHeuristic)
        {
            if (heuristicPacker == null) heuristicPacker = new BinPacker(ruleConfig, rewardConfig, cols, rows);
            if (heuristicPacker.Decide(placed, pool, remaining, out int ti, out int ci, out int ri))
            {
                d[0] = ti; d[1] = ci; d[2] = ri;
                return;
            }
            // 놓을 곳 없음 → 왜 못 놓는지 사유 집계해서 로그 (진단용)
            if (verboseLog)
            {
                var reasons = new List<BinPacker.UnplacedReason>();
                heuristicPacker.DiagnoseUnplaced(placed, pool, remaining, reasons);
                var sb = new System.Text.StringBuilder("  ⚠ [교사 포기] 남은 화물 배치 불가 → ");
                foreach (var rr in reasons) sb.Append($"[{rr}] ");
                Debug.Log(sb.ToString());
            }
            // 아래 폴백 (무효 행동 → Fail 경로로 에피소드 정리)
        }

        // 폴백/랜덤 baseline: 남은 종류 아무거나 + 랜덤 셀 + 회전0
        int t = 0; for (int i = 0; i < NumTypes; i++) if (remaining[i] > 0) { t = i; break; }
        d[0] = t; d[1] = Random.Range(0, NumCells); d[2] = 0;
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private Vector2 CellCenter(int c, int r)  // → (x, z) 트레이 로컬
    {
        float x = -HalfX + (c + 0.5f) * ruleConfig.trayLateralM / cols;
        float z = -HalfZ + (r + 0.5f) * ruleConfig.trayLengthM / rows;
        return new Vector2(x, z);
    }

    // 점(x,z)에서의 현재 적재 상단 높이 (그 점을 덮는 화물들의 최대 top; 없으면 바닥)
    private float HeightAt(float x, float z)
    {
        float top = ruleConfig.floorTopY;
        foreach (var p in placed)
            if (Mathf.Abs(p.center.x - x) <= p.halfSize.x && Mathf.Abs(p.center.z - z) <= p.halfSize.z)
                top = Mathf.Max(top, p.Top);
        return top;
    }

    // 후보 밑면(footprint)이 놓일 바닥 높이: 겹치는 기존 화물들의 최대 top
    private float RestBottom(float x, float z, Vector3 half)
    {
        float bottom = ruleConfig.floorTopY;
        foreach (var p in placed)
        {
            bool overlapX = Mathf.Abs(p.center.x - x) < p.halfSize.x + half.x;
            bool overlapZ = Mathf.Abs(p.center.z - z) < p.halfSize.z + half.z;
            if (overlapX && overlapZ) bottom = Mathf.Max(bottom, p.Top);
        }
        return bottom;
    }

    private bool AllPlaced() { foreach (var n in remaining) if (n > 0) return false; return true; }
    private float TotalMass() { float m = 0f; foreach (var p in placed) m += p.Mass; return m; }
    private Vector3 Cog()
    {
        float m = 0f; Vector3 w = Vector3.zero;
        foreach (var p in placed) { m += p.Mass; w += p.Mass * p.center; }
        return m > 1e-6f ? w / m : Vector3.zero;
    }
}
