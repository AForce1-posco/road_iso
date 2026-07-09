# 📓 작업 일지 (WORKLOG) — road_iso / CargoRiskSimulation

> **날짜순 "언제 뭘 했고, 왜 그렇게 결정했고, 뭘 겪었나" 기록.**
> "지금 상황·다음 액션"은 `STATUS.md`, 스펙은 `RL_Applied_Spec.md`가 담당 — 이 파일은 **역사**를 담당한다.
> 규칙: 작업 세션이 끝날 때마다 아래에 항목 추가(최신이 아래). 결정에는 반드시 **이유**를 남길 것.

---

## 2026-07-01 — 정적/동적 씬 MVP (구 CargoRiskSimulation)

- **정적 씬 MVP**: 물리 기반 자유 배치(격자 스냅 없음), 바구니형 트레이, 배치/제거/회전/고정 토글, 대시보드(총중량·LTR·CoG·4점하중), CoG 마커·안전영역 시각화.
- **동적 씬 MVP 코드**: "평가자" 역할 — 정적 배치(json)를 트럭에 싣고 급선회 주행 → Roll/LTR/화물이동/전복 측정 → CSV. 공용 코어(`CargoFactory`·`CargoLayoutIO`)를 Common으로 추출(정적/동적 단일 소스).

## 2026-07-02 — ⚠️ 메인 개발처를 road_iso로 이전

- **결정**: 메인을 `unity/roadTest/road_iso`(Unity 2020.3.48f1, Built-in RP)로 이전. **이유**: road_iso의 트랙(RoadArchitect 도로+ISO 경로+PurePursuit 주행+DataLogger)이 완성형. 구 프로젝트의 EasyRoads 동적 씬은 폐기.
- 정적 씬 재작성(Common+Static 스크립트, `StaticSceneSetup` 원클릭 부트스트랩). 2020.3 함정: `rb.velocity`, 구 Input, Arial.ttf, URP 셰이더→Standard 폴백.

## 2026-07-03 — 동적 파이프라인 구축

- `CargoBedLoader`(json→적재, FixedJoint/자유해제, massScale 100) + `CargoRiskRecorder`(Roll·횡G·4륜 LTR·화물이동·전복→results.csv+HUD) + `DynamicSceneController`(Cases 폴더 전체 루프 오케스트레이션).
- 물리 정확도: timestep 0.01(100Hz), maxDepenetrationVelocity 2, CargoGrip 마찰재. 질량비 1:100 초과 시 관통(솔버 한계) 확인.
- 경로 중앙화 `CargoPaths`, 화물 스펙 CSV화(`Assets/Data/cargo_catalog.csv`).

## 2026-07-04~05 — 실측 반영 · RL 설계 · S1~S4 (대형 마일스톤)

- **실측 화물 16종** 반영(`적재물데이터.xlsx`), **"톤백"→"포대" 전면 개명**(코드·CSV·케이스 JSON — CargoBedLoader가 이름 매칭이라 JSON까지 함께).
- **적재함 확정: 안쪽 61(z)×21(x)×27(y) cm** (길이 62→61 정정, 전 구성요소 반영). 축 매핑: x=좌우/y=높이/z=주행.
- **캘리브레이션 도구** `CalibrationRunner`(로드셀 실측 CoG vs 계산 비교→CSV).
- **RL 설계 문서** `RL_StaticPlacement_Design.md`(MDP·규제·보상 LE0.5/CGS0.4/SS0.1·DOE). 규칙 원본 = Hard_Constraint/Reward_Design docx + 손글씨.
- **S1** `RuleChecker`(Hard 9규칙, H11/H12는 사용자 결정으로 제거 — 해석충돌/비구속) + 테스트 10시나리오.
- **S2** `RewardCalculator`(LE/CGS/SS 0~1 정규화 가중합 + Step shaping) + 검증(좋은배치 0.65 > 나쁜배치 0.54).
- **S3** `PlacementAgent`(관측=높이맵+CoG+질량+편차+남은수, 행동 3브랜치, 마스킹). ⚠️ 함정 2개 해결: 중복 베이스 Agent 컴포넌트 제거 / **ActionSpec은 Awake()에서 세팅**(ML-Agents 2.0.2 LazyInitialize 순서상 Initialize()는 늦음 → "Action Mask too large").
- **S4 PPO 1차 학습**: env `mlagents-x86`(Rosetta x86_64+py3.9.13+torch1.8.1, cattrs 패치 필수 — 레시피 `RL_Env_Setup.md`). **Mean Reward −1.67→−0.36 (우상향, 미수렴)** — 4cm 격자(96셀) 기준.
- **ML-Agents 패키지 2.0.2 고정 결정**: 2.2.1-exp 시도 → Barracuda 연쇄 업그레이드로 DLL 참조 깨짐 → 롤백.
- `PlacementVisualizer` 신설(표시 전용, placed를 실물 화물 모양으로). **H2 개정**: 앞(+z, 캐빈) 오버행 금지·뒤(−z)만 파이프 허용.
- 배치 케이스 2000개(선반패커) **파이프 방향 버그 발견·제자리 패치**(euler (90,90,0)→(90,0,0), 백업 후).

## 2026-07-05 (밤) — 위험 데이터셋 재생성

- **`generate_risk_cases.py` → `Cases/` 500개**(위험350: 높이초과/과적/편심 + 촘촘·CoG이상 100 + 정상 50). 예측기(파이프라인 ④) 학습용 — 하드룰 의도적 위반 허용. 옛 2000개는 `Cases_shelf2000_archive_20260705/` 백업.
- 캘리브레이션 실행: 엑셀 17케이스 → x·y 오차 0.1~0.3cm(양호). ⚠️ 엑셀 CoG는 mm 단위(÷10 변환).

## 2026-07-06 — 격자 1cm 전환 · 빈패커 · BC 워밍스타트 (진행 중 작업)

- **파이프라인 큰 그림 정리**: ①빈패커→②동적주행→③피처/라벨→④위험예측기→⑤RL보상 연결→⑥RL 2차→⑦동적검증. 동적주행은 느려 RL 보상에 직접 못 씀(예측기 경유 필수).
- **격자 4cm→1cm 전환 (사용자 확정)**: RL+빈패커 전부 21×61=**1281셀**. 관측 114→**1299**, 행동 branch1 96→**1281**. ⚠️ placement_v1 onnx 비호환 — 행동공간 13배라 **맨땅 PPO 비추 → BC 워밍스타트와 세트** 전제.
- **빈패커 Phase 1**: `BinPacker.Pack`(그리디: RuleChecker 유효 후보 중 RewardCalculator 최고점) + `Runner`(JSON 100개) + `Visualizer`. 검증: 성공률 89%·평균보상 **0.698** (RL 1차 −0.36 대비 → BC 교사 가치 실증). PackMode Stable/Dense 추가.
- **빈패커 Phase 2 코드**: `Decide()`(다음 한 수) + `PlacementAgent.Heuristic` 연결(`binPackerHeuristic`) + rl_config `behavioral_cloning`(strength 0.5, steps 200k) + hidden 512. 진단 도구 `DiagnoseUnplaced`/`UnplacedReason`.
- **🔴 demo 오염 사건 → 해결**:
  1. 1차 기록 demo(31MB→193MB) Mean Reward **−0.45** (정상 +0.6~0.7) — 이대로 BC 하면 나쁜 습관 모방.
  2. 진단: 시연 자체는 정상(완주 시 R=0.65~0.74). **진짜 원인 = manifest 무작위 추첨이 물리적으로 못 싣는 조합을 40~50% 뽑음**(60cm 파이프 여러 개+넓은 박스 — 파이프는 전장 독점+위 적재 불가 H6). 근본 = 입력 분포가 비현실적.
  3. **해결(코드, `OnEpisodeBegin` 재추첨 루프)**: ②현실성 제약 `ManifestRealistic`(항상: 총질량≤7kg·파이프폭합≤트레이폭×0.7) + ①게이팅(demo 녹화 시: 교사 `Pack` 완주 조합만, 최대 40회 재추첨). 게이팅 트레이드오프(분포 좁아짐)는 인지하고 채택 — BC는 교사의 성공 시연만 모방하는 게 맞음.
  4. **재기록 합격: Mean Reward +0.867**, 29,553스텝/6,109에피소드(4.84스텝/ep = 완주 위주).
- **문서 정비**: 핸드오프 `STATUS.md` 신설, `RL_Applied_Spec` 관측 1299 정정+§6 manifest 게이팅 문서화, `BinPacker_Design` demo명·Phase2 상태 갱신, `RL_Implementation_S1_S2` 스테일 배너, 이 `WORKLOG.md` 신설.
- **다음**: `--run-id=placement_v2_bc` BC 워밍스타트 학습 (절차는 STATUS.md §3).

## 2026-07-06 (오후) — BC 학습 착수 시 UnityTimeOutException 원인·해결

- **증상**: `mlagents-learn --run-id=placement_v2_bc`가 "Listening on port 5004" 후 Unity Play → `mlagents_envs.exception.UnityTimeOutException`(첫 `_reset_env`에서 끊김). (그 직전엔 트레이너 없이 Play를 먼저 눌러 "Couldn't connect… Will perform inference instead"로 빠졌던 것 — 순서 문제).
- **원인**: `PlacementAgent.OnEpisodeBegin`의 **게이팅 루프**. `binPackerHeuristic`이 켜져 있으면 매 리셋마다 `heuristicPacker.Pack()`(1281셀 풀 빈패킹 solve)을 **최대 `manifestMaxTries`(40)회** 재추첨하며 돎. 데모 녹화(Heuristic Only)엔 Python 타임아웃이 없어 느려도 통과했으나, 학습(Default)은 첫 리셋을 **60초 안에 응답**해야 해서 초과 → timeout.
- **결정/해결**: **학습 시 `binPackerHeuristic` 해제**. 이유 — ① 게이팅은 데모를 깨끗이 녹화하려는 장치이고, ② Default 모드에선 `Heuristic()`이 호출조차 안 되며, ③ 현실성 제약 `ManifestRealistic`②는 `binPackerHeuristic`과 무관하게 항상 적용되어 학습 manifest 분포는 유지됨. 즉 학습에선 게이팅이 불필요한 오버헤드일 뿐.
- **부수 정리**: 실행 순서 확정(트레이너 "Listening" 확인 후 Play), 실패한 빈 run 폴더는 `--force`로 덮어쓰기. STATUS.md §3에 "학습 전 PlacementAgent 체크리스트"(binPackerHeuristic/verboseLog/Record/Visualizer/Behavior Type) 신설.

## 2026-07-06 (오후 2) — BC 학습 정체(−1.5) 원인 검증 → A안(게이팅 풀) 구현

- **증상**: `placement_v2_bc`(BC 워밍스타트)가 출발 −1.45 후 **−1.5로 오히려 하강**, v1(4cm) 궤적에 붙어 안 올라감. BC가 견인 실패.
- **코드 검증(확정)**: `PlacementAgent.Fail` = 무효 20회×(−0.05) + (−0.5) = **정확히 −1.5**. 현재 mean −1.5 = **거의 매 에피소드 fail-out**. 그리고 `ManifestRealistic`②는 질량·파이프폭만 봐서 **배치 가능성을 보장 못 함**(코드상 확실).
- **분포 진단 도구 추가**(`BinPackerVisualizer.DiagnoseManifestDistribution`) → 학습 분포(3~5, ManifestRealistic O, 게이팅 X) 5000샘플 측정:
  - ① ManifestRealistic 통과율 **91.1%** (싼 필터라 9%만 거름)
  - ② 통과분포 교사 완주율 **p=59.9%** (완주 시 평균 Final 0.701) → **40%는 교사조차 완주 불가** (지배 사유 **H2 경계이탈** 압도적 = 부피 초과)
  - ③ **에이전트 천장 추정 −0.181** = p·0.701 + (1−p)·(−1.5)
- **결론(두 원인 동시)**: (A) 40% 완주불가 manifest가 천장을 −0.18로 눌러 fail-out 유발 + (B) 1281셀 미학습으로 그 천장에도 못 감. 결정타 = **train/demo 불일치** — 데모는 게이팅(p~100%, +0.87)인데 학습은 게이팅 없음(p=60%). BC가 못 본 40%에서 헤매다 fail-out.
- ⚠️ **이전 오후 항목의 "학습 분포는 유지됨"은 틀렸음을 정정**: 게이팅을 빼면 분포가 바뀐다(못 싣는 조합 40% 유입). 게이팅 off는 timeout 땜질이었을 뿐, 분포 정합은 깨짐.
- **결정/해결 = A안(오프라인 게이팅 풀)**. 이유 — 정확(교사 Pack 그 자체, 근사 아님)·타당(데모와 100% 동일 분포)·**검증됨**(+0.867 데모가 바로 이 게이팅으로 나옴). A′(ManifestRealistic에 부피상한 추가)는 프록시라 미검증·잔여 불일치로 백업.
  - **구현**: `BinPackerVisualizer`에 `Generate Gated Manifest Pool`(완주가능 manifest N개→`Assets/Data/gated_manifests.txt`) + `PlacementAgent`에 `useGatedPool`(리셋마다 파일에서 뽑기, 파일 없으면 런타임 샘플링 폴백). 리셋당 Pack 0회 → **timeout 재발 없음** + 분포 정합. 진단은 `BinPackerRunner`가 아니라 씬에 실제로 붙은 `BinPackerVisualizer`에 넣음(3D BPP 씬 = Visualizer 단독).
- **정직한 한계**: A안은 천장을 −0.18 → ~+0.70로 올릴 뿐, 에이전트가 1281셀에서 실제 등반하는 건 별개(B) — BC강도·스텝 추가 튜닝 여지.

## 2026-07-06 (오후 3) — A안 실행 → v3_gated도 정체 → 근본원인 "1281셀 탐색벽" 확정 → 전략 재정립

### 실행·관찰
- 게이팅 풀 생성 성공: `gated_manifests.txt` 5000개, 채택률 **54.3%**(진단 예측 0.91×0.60≈55%와 일치 = 코드 정상). 길이 3/4/5 = 2130/1620/1250(완주 쉬운 3개로 쏠림), 파이프 희귀(게이팅이 파이프중 조합을 자연 탈락 — 데모와 같은 성질).
- Play 시 `[PlacementAgent] 게이팅 풀 로드: 5000개` 확인 → **A안 정상 작동.** `mlagents-learn` startup 로그에 `behavioral_cloning: strength 0.5` → **BC도 로드 확인.**
- **v3_gated 결과 = 정체**: `Mean Reward −1.5 · Std 0.000 · Episode Length ≈20`. 10k에선 −1.315(std 0.614)였다가 20k부터 −1.5(std 0)로 **붕괴.**

### 진단(확정)
- `Fail()` = 무효 20회×(−0.05)+(−0.5) = **정확히 −1.5**. Std 0 = **모든 에피소드가 유효 배치 0회로 fail-out**(하나라도 놓으면 step보상으로 편차 생김). Episode Length≈20 = maxInvalid 도달. → **완전 정체(collapse), 느린 학습 아님.**
- **근본 원인 = 1cm 격자(1281셀) 행동공간.** v1(4cm=96셀)의 13배. 물리 유효면적은 같은데 칸이 13배라 **유효칸 비율 ~1/13** → 랜덤 탐색이 20번 안에 유효칸 맞힐 확률 급락(96셀 ~96% vs 1281셀 ~18%, 거친 추정) → 완주 경험 0 → 균일 −1.5 → **PPO advantage 0 → 그래디언트 0.** BC(0.5)는 1281갈래 분포를 바늘로 못 모아 무력.
- **왜 v1은 상승했나**: 96셀이라 우연한 성공이 나와 발판 → 기어서 −1.67→−0.36(그래도 양수 미달). 즉 v1이 나은 건 "기술"이 아니라 "문제가 13배 쉬웠기 때문". **"BC/3D 더했는데 왜 더 나빠?" = 문제를 13배 키우고 그 도우미가 약했던 것.**
- 즉 정체는 manifest 분포(A안 해결)도 보상 부재도 아님 → **순수 탐색/행동공간 문제.**

### 외부 QA 리뷰(5점) 대조
- ①BinPackerRunner 현실성필터 없음(baseline JSON이 unplaced 조용히 드롭) = 정확, S5 공정비교 때 full-pack만/원manifest 저장 필요(저우선).
- ②BC 성공판정 = 리뷰는 "미검증"이나 실제론 **검증완료=실패**(우리가 앞서감).
- ③회전 자유도 제한(yaw0/90·파이프rot0, pitch 없음) = 정확, "도메인 제한 packing"으로 문서화 = 의도된 설계.
- ④**마스킹 약함**(소진종류+꽉찬셀만) = ★정확, **우리 탐색벽 원인의 정중앙.** 단 독립 3브랜치라 완벽한 유효성 마스킹은 구조상 어려움 → 종류무관 경계여백 마스킹은 가능(H2 대량 제거).
- ⑤`Pack(Dense)`=부피내림 vs `Decide()`=항상 질량내림 = 코드로 검증, 정확. Stable 교사엔 무해, Dense를 교사/비교로 쓰면 맞춰야 함.

### 전략 재정립 (사용자와 합의)
- **목표 명확화**: 주=규칙준수 안전 정적배치(고정 주문), 부=동적 전복여부·위험예측. 안전 주·촘촘 부차. (STATUS §0 반영)
- **역할 분담 확인**: 위험도(동적·예측기)는 **다른 담당자** → 이쪽에서 못 함. 기다리는 동안 RL을 붙듦.
- **"지금 RL이 의미있나?" 결론**: 지금 정적보상 RL을 갈아봐야 proxy 최적화라 최종목표 아님 + 막혀있음. **BUT 탐색벽 해결은 reward-independent 인프라라, 최종 예측기-보상 RL에도 필수 → 지금 하는 게 헛되지 않음.** 단 정적 보상 가중치 미세튜닝은 헛수고(버려짐).
- **격자 완화가 근본책인 이유**: 능력부족이 아니라 **탐색 발판** 문제. 안전적재에 1cm 정밀도 불필요 → 2cm(≈341셀)가 아마 영구적으로 옳은 해상도(임시목발 아님). 1cm 복귀는 필요할 때만 coarse→fine.

### 결정 → 다음 작업
- **격자 1cm→2cm급 완화 + GAIL 추가(strength 0.3) + BC 0.5→1.0.** run-id `placement_v4_grid2_gail`.
- ⚠️ 격자 바꾸면 **데모·게이팅풀 재생성**(demo는 action 크기 바뀌어 비호환).
- 룰/보상 병행 개선 원칙: **룰=도메인오류·과도제약 찾아 수정(탐색도 쉬워짐)** / 보상=가중치말고 **학습가능성 구조**(fail-out 절벽 등)만.
- 병행: 예측기 담당자에게 입력 피처 확인(피처 일관성).

## 2026-07-06 (오후 4) — 탐색벽 처방 적용: 격자 1cm→2cm + GAIL

- **격자 완화 `cols=11·rows=31`(≈2cm, 341셀)** 적용: `PlacementAgent`·`BinPacker`(생성자 기본)·`BinPackerRunner`·`BinPackerVisualizer` 코드 + `RLTraining`·`3D BPP` 씬 serialized 값 전부. 홀수 선택(x=0·z=0 중앙셀 정렬). obs 359·action (12,341,2)는 `Setup()`이 cols/rows로 자동 산정.
  - 이유: 1281셀(1cm)이 탐색벽 근원(유효칸 ~1/13, 랜덤 발판 못 잡음). 341셀(v1 96과 1281 사이)로 발판 확보. 안전 적재에 1cm 정밀도 불필요.
- **rl_config**: `gail` 활성화(strength 0.3, demo_path 동일) + `behavioral_cloning.strength 0.5→1.0`. GAIL = 균일 −1.5(그래디언트 0)를 깨는 밀도 있는 모방보상. 상단 스펙 주석·hidden_units 주석도 359/341로 갱신.
- ⚠️ **의존성**: 격자 바뀌어 기존 demo(action 1281)·게이팅풀 비호환 → **풀 재생성 + 데모 재기록 필수**(절차 STATUS §3). run-id `placement_v4_grid2_gail`.
- **후보 기록**(STATUS §4b): 이걸로도 std 0이면 → A) Refinement 아키텍처(빈패커 유효배치서 시작, RL은 개선연산만 → 탐색벽 회피) / B) Dense·Stable 동적 검증 흐름(정적보상 proxy 타당성 실증). 두 축은 독립.
- **개념 정리**(사용자 질문): 충돌 페널티는 빈패커가 아니라 **RL이 규칙을 배우는 신호**(빈패커=규칙 검사·유효만 배치 / RL=규칙 모름·페널티로 학습). 현재 무효행동은 reject+페널티(마스킹 구조적 한계).

## 2026-07-06 (오후 5) — 정책 붕괴 확정 → Option C(보장된 완주) 구현

- **v4(격자2cm+GAIL) 결과**: 10k Mean −1.384·**Std 0.557**(배치 중) → 20k Mean −1.5·**Std 0.000**(붕괴). v3와 동일 패턴 → **격자 완화만으론 안 됨.**
- **v5(beta 0.005→0.02 + GAIL 0.3→0.8) 결과**: 역시 20k std 0 붕괴. → **config 튜닝 3연속 실패.**
- **진단(확정)**: 문제는 격자 크기가 아니라 **정책 붕괴**. 10k엔 std 0.5~0.6로 화물을 놓다가, 20k까지 정책이 무효 행동으로 무너져(엔트로피 붕괴) 매 에피소드 fail-out. **근본 = fail-out −1.5 절벽**(완주 못 하면 −1.5 균일 → 그래디언트 0 → 붕괴). 재기록 데모는 +0.857로 무죄(Shapes 359·Branches [12,341,2] 정상).
- **결정 = Option C(보장된 완주)**. 사용자 refinement 아이디어의 경량판. from-scratch·데모·행동공간 다 유지하고, **무효 행동 처리만 교체**:
  - `OnActionReceived` 재작성: 에이전트 행동 무효 → fail-out 대신 **작은 페널티(−0.02) + `PlaceByTeacher()`(빈패커 Decide로 대신 한 수)** → 에피소드 항상 완주. 교사도 막히면 partial final − unplaced 감점(−1.5 아님).
  - 헬퍼 `TryPlace`(에이전트/교사 공통 배치)·`PlaceByTeacher`·`CountRemaining` 추가. 필드 `guaranteedCompletion`(ON)·`invalidStepPenalty 0.02`·`unplacedPenalty 0.1`.
  - **효과**: 보상이 항상 유효 완성배치(≈+0.6~0.9) → **std>0 보장 → 붕괴 불가.** 바닥 = 빈패커 +0.7, 잘 배우면 그 위로. 데모/BC/GAIL·행동공간 불변 → **재기록 불필요.**
- **다음**: `--run-id=placement_v6_optC --force`. 양수 출발·무붕괴·교사 초과 여부 관찰. 안 되면 §4b-A 진짜 Refinement.

## 2026-07-06 (오후 6) — 브랜치 분기: 순수 3D BPP 최소 사이클 (박스만)

- **전략 전환(사용자)**: RL 탐색벽/붕괴가 반복(v2~v6) → 파이프라인 전체를 **최소 수직 슬라이스**로 먼저 관통하기로. `3dbpp`에 RL/Option C 커밋(29e167b) 체크포인트 후 **`minimal-cycle-boxes` 브랜치 분기**. RL은 3dbpp에 보존(휴면), 복귀 `git checkout 3dbpp`. (큰 산출물 데모112MB·results439MB는 커밋 제외 + .gitignore 추가)
- **이 브랜치 목표**: "내가 manifest 지정 → 순수 Dense 3D BPP로 공간 꽉 채우기" 를 RL 잡동사니 없이 깨끗하게. 안정성·CoG 최적화 없음, **공간만**.
- **결정**:
  - 화물 = 박스 중심(파이프/포대/코일 제외 → H5/H6/H7/H10·rot0 안 씀).
  - **7kg 적재한도 유지**(현실적) — 대신 가벼운 합성 박스 추가로 부피 채우기 가능케. (통념상 고전 BPP는 무게 무시하나, 이 프로젝트는 현실 적재라 유지.)
  - 입력 = 인스펙터 (id,개수) 목록 + CSV(Assets/ 상대경로, "id,개수"). JSON manifest는 안 씀(출력이 JSON이라 혼동).
  - PlacementAgent·rl_config는 **그대로 둠**(휴면).
- **구현**:
  - **카탈로그 SYN 6종 추가**(`cargo_catalog.csv`): SYN-01~06(큰경량·중경량·틈새소형·납작패널·표준큐브·작고무거움). **이유**: 실측 박스는 밀도 높아 7kg에서 부피를 못 채움(채우려면 ~55kg 필요) → 저밀도 합성박스(예 SYN-01 5400cm³·0.2kg)로 **"꽉 채운 버전" 관측 가능**. Unity라 비현실 케이스도 테스트.
  - **`CargoManifest.cs`(신규)**: 인스펙터 `ManifestEntry[]`/CSV → CargoType 리스트 전개.
  - **`BinPackerVisualizer` 정리**: 랜덤 manifest·게이팅풀 생성·분포진단·ManifestRealistic 전부 제거 → manifest 지정 + **Dense 기본** + **부피점유율(%) 표시** + "Save Layout JSON".
  - **`BinPackerRunner` 정리**: 랜덤 배치 제거 → manifest 지정 → Dense pack → JSON 1개 저장 + 부피점유율 로그.
- **다음**: 3D BPP 씬 BinPackerVisualizer에 manifest 채우고 packMode=Dense → Play/Repack(부피점유율↑ 확인) → Save Layout JSON → (이후) 동적 주행 → PPO.

## 2026-07-06 (오후 7) — boxpack001 단일 케이스 PPO 착수

- **목표**: boxpack001.json을 만든 고정 화물 목록(**B-004×8·SYN-04×4·SYN-03×4**, 16개·6.76kg)으로 현재 정적 보상/룰/액션으로 PPO → RL 배치 vs binpacker(boxpack001) 비교. 동적 예측기 전 단계라 정적 근사 최적화.
- **의미**: 분포 없는 **단일 고정 문제** = 최소 RL. "박스에서 RL 파이프라인 작동 + binpacker 초과" 최소 증명. (그간 붕괴 반복이라 단일 케이스로 작동부터 확인.)
- **구현(PlacementAgent)**: `useFixedManifest`+`fixedManifest`(ManifestEntry[]) 추가. 켜면 매 에피소드 그 목록 그대로, 풀도 그 manifest의 distinct 타입으로 자동 구성(→ 액션공간 최소, 여기선 3종 → obs 350·action (3,341,2)). `BuildFixedRemaining` 미리계산, OnEpisodeBegin에 고정 분기.
- **config**: `Docs/rl_config_box.yaml` 신설 — 순수 PPO(BC·GAIL 없음, 단일 케이스+Option C라 워밍스타트 불필요). beta 0.01, hidden 256, summary_freq 5000.
- ⚠️ boxpack001.json(B-004×8·SYN-04×4·SYN-03×4)과 3D BPP 씬 현재 인스펙터(B-004×5·B-005×4·C-001×6)가 다름 — JSON 저장 후 인스펙터 변경한 것. **PPO fixedManifest는 JSON 내용 기준**.
- **다음**: RLTraining 씬 PlacementAgent에 fixedManifest 채우고 → `--run-id=boxpack001_ppo`. 양수 등반·binpacker Final 초과 관찰.

## 2026-07-06 (오후 8) — ✅ from-scratch PPO 성공 (Option C 검증)

- **guaranteedCompletion OFF** 때: −1.85(무효 페널티 누적, MaxStep까지 헤맴). **ON으로 켜니**: Mean Reward **1.03→1.13**, Std ~0.09, **~25k에서 수렴**. 45k에서 수동 정지(Ctrl+C), onnx 저장.
- **의미**: Option C(보장된 완주) 설계가 옳았음이 실측 검증됨(양수·붕괴 없음). **그간 붕괴 반복하던 RL이 박스 단일케이스에서 드디어 학습.** = **from-scratch baseline (v1)** = `results/boxpack001_ppo/PlacementAgent.onnx`.
- ⚠️ **manifest 불일치 주의**: 이 run의 fixedManifest는 **B-001×8·SYN-03×4·SYN-04×4** (씬 확인). boxpack001.json은 **B-004×8**이라 **run-id는 boxpack001이지만 실제론 다른(더 쉬운) manifest.** B-001(8cm·0.21kg)이 작아 수렴이 깔끔했던 것. binpacker(boxpack001) vs RL 비교하려면 manifest를 B-004로 맞춰 재학습 필요.
- ⚠️ 누적보상 1.13은 step shaping 포함 → binpacker Final(0~1)과 직접비교 X. 우열은 배치/Final로 따로 비교.
- **다음**: from-scratch는 baseline으로 보존. **`RefinementAgent`(v2) 별도 스크립트** 구현(빈패커 배치서 시작→이동/회전, 정적 보상, 예측기 오면 교체) → v1 vs v2 비교. (사용자 결정: 두 버전 다 남겨 "여러 방식 시도" 기록.)

## 2026-07-07 — RefinementAgent(v2) 구현 (빈패커 배치서 시작 → 재배치)

- **사용자 원안 = Refinement**: RL이 맨땅이 아니라 **빈패커 완성 배치에서 시작**해 개선. from-scratch(v1, PlacementAgent)와 **별도 스크립트**로 공존(둘 다 보존·비교).
- **`RefinementAgent.cs` 신설**:
  - 시작: `startManifest`(B-004×8·SYN-04×4·SYN-03×4)를 **Dense Pack** → 완성 배치(결정론적=boxpack001, JSON 파싱 대신 매 에피소드 Pack).
  - 관측: 높이맵(341)+CoG(3)+질량(1)+편차(2) = 347.
  - 행동: **(아이템 index·목표 셀·회전) = (16, 341, 2)** — 화물 하나를 다른 셀로 재배치(relocate). 무효(겹침/이탈)=원위치 복구+작은 벌점 → **fail-out/붕괴 없음.**
  - 보상: **ΔFinal**(이동 후−전). 누적 = "빈패커 대비 개선량". 시작이 Dense(CoG 안 봄)라 개선 여지 큼.
  - 에피소드: `stepsPerEpisode`(25) 재배치 후 종료.
- **`Docs/rl_config_refine.yaml`**: 순수 PPO, Behavior Name=RefinementAgent, run-id `boxpack001_refine`.
- **씬 세팅(사용자)**: RefinementAgent 오브젝트 = BehaviorParameters(Behavior Name=RefinementAgent, Type=Default) + DecisionRequester(1) + RefinementAgent.
- **의미**: Dense는 공간만 채워 CoG 위험 → RL이 무거운 것 낮/중앙으로 재배치해 Final↑ = 안전 개선을 학습. 붕괴 원천봉쇄. v1(from-scratch) vs v2(refinement) TensorBoard 비교.
- **다음**: 학습 → 곡선(누적=개선량) 양수면 RL이 빈패커 개선 성공. 이후 예측기 보상으로 교체.

## 2026-07-07 (오후 1) — RefinementAgent 실행 실패 진단: 빈 base Agent 컴포넌트

- **증상**: 학습은 도는데 `Fewer observations (0) made than vector observation size (347)` 경고 반복 + **에피소드 완료 0회**(Mean Reward 안 뜸).
- **진단**(Editor.log 분석): RefinementAgent 스크립트 Setup은 정상 실행됐으나(obs=347 로그), 실제 스텝을 돈 건 **같은 오브젝트에 잘못 추가된 ML-Agents base `Agent` 컴포넌트**. base Agent는 CollectObservations가 비어 있고(→ 관측 0) EndEpisode를 안 불러(→ 에피소드 무한) "Heuristic method not implemented" 경고까지 일치. ML-Agents 2.0.2의 `Agent`는 추상이 아니라 Add Component로 그냥 추가됨 + `DecisionRequester.GetComponent<Agent>()`가 첫 번째 Agent를 잡는 구조.
- **해결(사용자)**: 씬에서 빈 Agent 컴포넌트 제거 → 정상 학습 시작. ⚠️ 부수 발견: RefinementAgent.unity 씬이 디스크에 저장 안 돼 있었음(⌘S 필요).

## 2026-07-07 (오후 2) — RefinementAgent(v2) 학습 결과: "무효 회피"만 배움 → 계측·마스킹 추가

- **결과(boxpack002_refine)**: Mean Reward **-0.455 → -0.411**(60k), std ~0.03. 붕괴는 없으나(설계 의도대로) 거의 flat.
- **진단**: 에피소드 25스텝 중 **무효 이동 ~20회**(전부 무효 바닥 = -0.50). 보상 역산 유효율 ~9→18%. 즉 60k 동안 배운 건 "무효 이동 덜 하기"뿐 — 진짜 목표(ΔFinal, 한 수당 ±0.01~0.05)는 무효 페널티 신호(-0.5 규모)에 묻힘. **보상 정렬 문제**(설계 자체는 건강).
- **계측 추가**(RefinementAgent): 에피소드 끝마다 StatsRecorder → TensorBoard `Refine/ValidMoveRate`·`Refine/FinalImprovement`(끝Final−시작Final)·`Refine/FinalAbsolute`.
- **셀 마스킹 추가**(WriteDiscreteActionMask): 높이한도 꽉 찬 셀 + 최소 화물도 못 놓는 가장자리 셀 차단. ⚠️ **한계 명시**: 무효 최대 원인인 "겹침"은 (아이템×회전×셀) 조합 의존이라 ML-Agents 브랜치 독립 마스킹으론 원천 차단 불가. 근본 해법 = 행동에서 아이템 선택 제거(라운드로빈) — 보류.

## 2026-07-07 (오후 3) — 전략 전환: v2 보류, v1+CGS 단독 보상으로 최소 사이클 진행

- **결정(사용자+논의)**: 최소 사이클 목적 = "학습이 되는구나" 확인이지 RL 완성이 아님. 배치 학습은 **v1(from-scratch+Option C)이 이미 +1.13으로 검증**돼 있으므로, v2 무효 문제와 싸우는 대신 v1을 사용. v2는 보류(폐기 아님).
- **보상 단순화 = CGS 단독**: 사용자 목표 "균등배치(앞뒤·좌우 균형)"는 정적 CoG로 즉시 계산 가능(주행 불필요) = 기존 `CogStability`가 그 자체. **RLTraining 씬 rewardConfig를 wLE=0·wCGS=1·wSS=0으로 변경**(코드 수정 없음, 씬 값만). **이유**: LE의 밀집(compact) 항이 "펼치기"를 벌해서(가중치 0.5로 최대) 쏠린 배치를 못 펼치던 것 — CGS 단독으로 비로소 펼칠 유인이 생김.
- **run `b001_cgs`**: 1.140(5k) → 1.325(35k) → **최종 300,005 steps, reward 1.553 수렴**(옛 "35k/1.325"는 중간값 스테일). std 0.245→하락. 양수 출발+상승 = 건강. ⚠️ 단 stepScale=0.05의 Step shaping(0.7·CGS+0.3·**밀집**)이 포함 → **완전한 순수 CGS 아님.**
- **run `b001_cgs_nostep`** (stepScale=0 시도): **10,561 steps / 0.623 = 학습 미완** → "순수 CGS 재실험" 결론으로 **인용 불가**(제대로 안 돌아감).
- **합격 판정 기준**: 곡선이 아니라 **Scene/Game 뷰의 배치 모양**(PlacementVisualizer 잠깐 켜서 확인 — CGS는 "중앙 탑쌓기"로도 만점 가능한 허점이 있어 보상 해킹 여부는 눈으로만 판별). 낮고 고르게 깔리면 성공 / 중앙 탑이면 펼침 항 추가 필요.
- **다음**: 배치 모양 확인 → (펼쳐졌으면) 정적 RL 멈추고 동적 주행 검증으로, (뭉쳤으면) 펼침 보상 항 추가.

## 2026-07-07 (오후 4) — ⭐ 위험 예측기 도착 (playground 머지) + git 정리

- **`origin/playground` → `minimal-cycle-boxes` 머지 완결**(000432d): **다른 담당자의 위험 예측기 합류** — `Assets/Scripts/Modeling/RiskModel.cs`·`RiskDisplay.cs`·`Assets/Resources/risk_model_treedata.json`(트리 모델). STATUS의 "예측기 오면 보상 교체" 전제가 현실이 됨.
- 충돌 8건 전부 `.meta`(코드 충돌 0): 삭제 확정 3(타임시리즈 meta)·예측기 측 유지 3(Modeling.meta·Resources.meta·treedata.meta)·우리 측 유지 2(RefinementAgent 씬/타이머 meta). `SampleScene.unity.meta`가 디스크에서 증발해 있던 것 HEAD에서 복원(GUID 보존).
- `.gitignore`에 `Assets/Data/Results/*.csv.meta` 추가(65dca84) — untracked meta 노이즈 29개 정리. `results.csv.meta`는 기추적이라 유지.
- **다음(신규 우선순위)**: `RiskModel.cs` **입력 피처 검토** — 예측기 입력 = RL 보상/관측 피처 일관성이 §1 크럭스. CGS 실험 확인 후 착수.

---

## 2026-07-09 — B 사이클: surrogate(p95) 보상 RL + from-scratch vs refinement

- **팀원 예측기 정체 파악**: 준 `surrogate_risk_model_layout_v416.pkl` = **RandomForest**(sklearn, 13피처, cv에 RF/XGB/LGB/CatBoost 비교 = 트리 ML, **딥러닝 아님**). 입력=배치피처(CogX/Y/Z·MaxHeightM·InertiaXX/YY/ZZ+CargoCount·TotalMassKg+도로4). **좌표=Unity규약(x=폭·y=높이·z=길이)** 을 학습데이터 값범위로 확정(사용자가 y=길이로 오해했으나 데이터가 반증), 질량 **×100(676kg)** 스케일 학습. 팀원 `RiskModel.cs`는 별개(동적7피처→순간LTR, 입력이 배치가 아니라 RL 보상 불가).
- **A(배선 검증)**: RF.pkl→트리JSON export(RF평균=leaf/n, sklearn 파리티 1e-18) → `LayoutRiskModel.cs`(신규, 스왑가능 예측기) + `PlacementAgent.useSurrogateReward`(터미널 −risk). **그러나 주어진 모델은 질량 지배(중요도 0.79)** 라 고정 manifest(질량상수)에서 보상 평평(≈CGS) 실증 → 신호 있는 라벨로 재학습 필요(B).
- **B(재학습)**: `gen_layouts.py`로 같은 manifest 유효·다양 배치 **86개(case9001~9086)** 생성 → 배치주행(60km/h, 전부 secured). **⚠️ results.csv 요약 max_abs_ltr=0.000 버그** 발견 → **시계열 `LTR_Total`(58열) 직접계산**이 진실(maxLTR 0.75~1.0). 라벨 비교: `frac(>0.7)` 21배 스프레드지만 R²**0.58**(rare-event 노이즈) vs `p95` R²**0.96**. → **보상=p95(신뢰), 검증=frac(팀원 실지표)** 역할분담. RandomForest 재학습 → `layout_risk_p95/frac07/both.json`(정규화, scale1 통일).
- **3-way 주행검증**: 빈패커 대비 **p95 frac−66%·max−14%(최고)**, both −33%, **frac07 +135%(꼴찌)**. → **reward hacking 실증**: frac07은 R²0.58 노이즈 예측기를 최적화해 "가짜 고득점"(TensorBoard 보상 최고)냈으나 실제 주행은 가장 위험. **∴ 진짜 지표라도 예측 노이즈면 보상 금지, 신뢰 높은 대리지표(p95)로 최적화 후 실지표로 검증이 정석.**
- **refinement**: `RefinementAgent`에 surrogate훅(보상=ΔScore, Score=−risk → 누적=빈패커 대비 위험감소, +면 이김) + `SaveLayout` + `RefinementVisualizer` 추가. `refine_p95`: **155k 수렴(from-scratch 300k의 절반)·ValidMoveRate 99.96%(교사 없이 자립)·보상 +0.23**. 주행 **p95 0.398(−10.6%, from-scratch는 0.444 무승부)·max 0.717(−16%)**. **결론: refinement > from-scratch** — 견고지표(p95·max)·속도·안정 모두 우위(from-scratch는 frac 1프레임만 앞섬=노이즈). 이유: refine=조밀보상ΔScore+유효시작→p95 세밀최적화 / from-scratch=희소터미널+교사지배→좌우중앙(꼬리깎기)에 그침.
- **버그수정**: `CargoBedLoader` OnDrawGizmos·fallback 적재함 치수가 stale **0.64×0.24(폭>길이=에디터서 가로로 보임)** → 실제 **0.21×0.61**로 4곳 통일. 축(x=폭·z=길이) 연결은 원래 정상, Play는 파일값 써서 이미 정상, **미리보기 치수만 옛값**이었음.
- **기타 확인**: 트럭 에셋 `Truck_LowPoly`=SR Studios Kerala(인도) 범용 게임 로우폴리(Maya, **실차 아님**)→전복 현실성은 메시가 아닌 VehicleController 물리값(질량3500·wheelBase3.2·CoM·윤거) 소관. 주행속도=PurePursuitController `isoTargetSpeedKmh`(직진60)/`isoCurveSpeedKmh`, 앞곡률 보간+P제어.
- **미해결**: 좌표 원점 통일(bedAnchor=트레이바닥중심 확인·CogY floorTop 1cm). 룰은 현재 H1~H13 전부 상시 ON(토글 없음), 튜닝 가능한 건 `supportRatioMin`·`maxPayloadKg`·`heightLimitM`(인스펙터). 룰 변경 시 86배치+surrogate 재생성 필요(학습룰=데이터룰 정합).
- **다음**: 더 어려운 manifest / 멀티시드 refinement(국소최적) / 룰 커리큘럼 / 좌표 원점 정리.

---

_(새 항목은 이 아래에 추가)_
