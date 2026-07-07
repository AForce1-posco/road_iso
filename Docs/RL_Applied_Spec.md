# RL 정적 배치 — 실제 적용 명세 (Rule · Action · Reward)

> **이 문서는 "지금 코드에 실제로 들어간 것"을 사람이 보기 쉽게 정리한 것**이다.
> 설계 문서(`RL_StaticPlacement_Design.md`)와 달리 **현재 소스 코드가 기준**(source of truth).
> 최종 확인: **2026-07-07** (minimal-cycle-boxes 브랜치 기준 — 격자 **2cm(11×31=341셀)** · Option C · v2 RefinementAgent §11). 격자 변경으로 1cm/4cm 시절 onnx와 비호환. 값이 바뀌면 이 문서도 갱신할 것.
> ⚠️ **씬 오버라이드 주의**: RewardConfig는 직렬화 필드라 **씬 인스펙터 값이 코드 기본값을 덮는다** — RLTraining 씬은 현재 **wLE=0·wCGS=1·wSS=0**(CGS 단독 실험, §4.5).
> 코드 위치는 맨 아래 §8 색인 참조.

---

## 0. 개념: Constraint = Rule + Reward

배치가 지켜야 할 조건(**Constraint**)을 성격에 따라 둘로 나눠 구현했다.

| | **Rule (Hard)** | **Reward (Soft)** |
|---|---|---|
| 질문 | 이 배치가 **가능한가?** (O/X) | 이 배치가 **얼마나 좋은가?** (점수) |
| 구현 | `RuleChecker` — 위반 시 **행동 마스킹**(아예 못 고름) | `RewardCalculator` — 점수로 **유도** |
| 성격 | 불가능/절대금지 (흑백) | 좋고 나쁨의 정도 (회색) |

원칙: **물리적으로 불가능/법적으로 금지 = Rule**, **좋고 나쁨의 정도 = Reward.**

---

## 1. 환경 상수 (확정값)

| 항목 | 값 | 비고 |
|---|---|---|
| 적재함 **안쪽(사용영역)** | **61(주행/길이) × 21(좌우) × 27(높이) cm** | 겉(벽 바깥) = 64×24, 벽두께 1.5cm |
| Unity 로컬 치수 | x=0.21, z=0.61, y한도=0.27 (m) | 원점=트레이 중심, 바닥 top y=0.01 |
| **축 매핑** | **x = 좌우 / y = 높이 / z = 주행·길이** | 설계문서 x(주행)=Unity z |
| 최대 적재중량(payload) | **7 kg (목업)** | = 700 kg 실차 (massScale 100) |
| 격자 | **11(x) × 31(z) = 341 셀** | **2cm 격자** (2026-07-06, 1cm 탐색벽으로 2cm 완화. 1cm=1281셀 시절 onnx 비호환) |
| 화물 풀 | **12종** (트레이에 정상 배치 가능한 것) + SYN 합성박스 6종(최소사이클용) | B-007·긴 파이프 제외. `useFixedManifest` 시 그 manifest 종류만 |

---

## 2. Rule — Hard 제약 (RuleChecker.cs)

`IsValid(placed, cand, out reason)` 가 아래를 **순서대로** 검사, 하나라도 걸리면 `false`(→ 마스킹/페널티).

| ID | 규칙 | 판정 / 임계값 |
|---|---|---|
| **H1** | 과적 금지 | 총질량 + 후보 ≤ **7 kg** |
| **H2** | 적재함 경계 내부 | AABB가 x[±0.105]·z[±0.305]·바닥 내부. **단 파이프는 z(길이) 오버행 허용** |
| **H3** | 화물 겹침 금지 | 3D AABB 교차 없음 |
| **H4** | 파이프 주행축(z) 배치 | 파이프 halfSize.z가 최장 (길이가 z 방향) |
| **H5** | 파이프 바닥층 | 파이프 밑면 ≈ 바닥(y=0.01) |
| **H6** | 파이프 위·아래 타화물 금지 | 파이프 기둥(x,z 겹침)에 다른 화물 상/하 금지 |
| **H7/H10** | 포대 위엔 포대만 | 후보가 비포대인데 받침이 포대면 금지 |
| **H8** | 밑면 지지율 ≥ **70%** | 바닥=100%, 적층=받침과 XZ 겹침면적/후보 밑면적 ≥ 0.70 |
| **H13** | 높이 ≤ **27 cm** | 후보 top ≤ 0.01 + 0.27 = 0.28 m |

**임계값 (RuleConfig, 인스펙터 튜닝 가능):**
`trayLateralM 0.21` · `trayLengthM 0.61` · `heightLimitM 0.27` · `floorTopY 0.01` · `maxPayloadKg 7` · `supportRatioMin 0.70` · `eps 0.005`

**제거된 규칙 (사용자 결정):**
- ~~H11 (CoG 전후 ±15%)~~ — 원본 해석 충돌 → 제거. CoG 중앙 유도는 Reward의 CGS가 담당.
- ~~H12 (전/후축 하중 10kg)~~ — payload 7kg이면 절대 안 걸리는 죽은 규칙 → 제거. 축하중은 동적 검증에서 실측.

> **측정치 제공**: `RuleChecker.Evaluate()`는 판정 외에 총질량·지지율·CoG를 반환 → Reward가 재사용.

---

## 3. Action — 행동공간 + 마스킹 (PlacementAgent.cs)

### 3.1 행동공간 (이산 3브랜치)
`ActionSpec.MakeDiscrete(종류수, 셀수, 2)`

| 브랜치 | 의미 | 크기 |
|---|---|---|
| branch0 | **화물 종류** 선택 (풀에서) | 풀 크기 (기본 12 / `useFixedManifest` 시 그 manifest의 distinct 종류 수 — boxpack 케이스 3) |
| branch1 | **격자 셀** 선택 | **341 (11×31, 2cm)** |
| branch2 | **회전** | 2 (0° / 90°) |

- 셀 인덱스 → 셀 중심 (x,z) → 화물을 그 위에서 **낙하 안착**(RestBottom: 바닥 또는 기존 화물 위).
- 회전 90°(rot=1) = halfSize의 x↔z 스왑.

### 3.2 마스킹 (WriteDiscreteActionMask)
- branch0: **남은 수 0인 종류** 차단.
- branch1: **높이 한도까지 꽉 찬 셀** 차단.
- branch2(회전): 종류의존이라 사전 마스킹 불가 → **무효 조합은 보상 페널티로** 학습.

### 3.3 적용 (OnActionReceived) — Option C 반영 (2026-07-06~)
1. 후보 화물·셀·회전으로 `PlacedItem` 생성 (안착 높이 계산)
2. `RuleChecker.IsValid` 검사
   - **유효** → 배치 확정 + `AddReward(Step)` (shaping). 목록 다 놓으면 `Final` 보상 + `EndEpisode`.
   - **무효** + `guaranteedCompletion` **ON(기본)** → **−0.02**(`invalidStepPenalty`) + **빈패커가 대신 한 수**(`PlaceByTeacher`) → 항상 완주. 교사도 막히면 partial Final − 남은개수×0.1(`unplacedPenalty`) + 종료.
   - **무효** + OFF(구방식) → `Fail()`: −0.05, 20회 반복 시 −0.5 + `EndEpisode`. (붕괴 원인이라 안 씀)

> ⚠️ **마스킹이 약함**: 지금은 "빈 종류·꽉 찬 셀"만 막고, 겹침·경계·지지율·파이프규칙·회전은 못 막아 **무효 행동이 페널티로 새어나옴** → 1차 학습이 -0.36에 머문 원인. (개선: 커리큘럼/워밍스타트/마스킹 강화)

---

## 4. Reward — 3목적함수 가중합 (RewardCalculator.cs)

### 4.1 최종 보상 (Final — 목록 다 놓았을 때)
```
R = w_LE·LE + w_CGS·CGS + w_SS·SS
   w_LE = 0.50,  w_CGS = 0.40,  w_SS = 0.10   (RewardConfig, DOE로 조정)
```
각 목적함수는 **0~1 정규화**.

### 4.2 세 목적함수

**LE — Loading Efficiency (적재효율)** = `0.4·부피활용 + 0.4·격자밀집 + 0.2·접촉`
- 부피활용: 화물 총부피 / 적재함 부피
- 격자밀집: 화물 밑면적 합 / XZ 바운딩 스팬 (뭉칠수록↑)
- 접촉: 벽·화물에 붙을수록↑ (contactGap 0.03m)

**CGS — CoG Stability (무게중심 안정) ⭐** = `0.5·(중앙 x·z) + 0.5·(낮은 y)`
- 중앙: |CoG_x|/half, |CoG_z|/half → 0이면 1점
- 낮게: CoG 높이가 바닥이면 1, 한도(27cm)면 0

**SS — Stacking Stability (적층 안정)** = `0.6·무거운거아래 + 0.4·상단평탄`
- 무거운거아래: 자기보다 위에 더 무거운 화물 있으면 벌점
- 상단평탄: 최상단 top 높이 편차 작을수록↑ (flatnessRef 0.1m)

### 4.3 스텝 보상 (Step — 화물 하나 놓을 때마다 shaping)
```
Step = stepScale · (0.7·CGS + 0.3·격자밀집),   stepScale = 0.05
```
학습 초반 방향(중앙·밀집) 잡아주는 소액 보상.

### 4.4 페널티 (PlacementAgent에서)
| 상황 | 값 |
|---|---|
| 무효 행동 (Option C ON, 기본) | −0.02 (`invalidStepPenalty`) + 교사 대체 배치 |
| 완주 실패 시 남은 화물 1개당 (Option C) | −0.1 (`unplacedPenalty`) |
| 무효 행동 (Option C OFF, 구방식) | −0.05, 20회 반복 시 −0.5 + 종료 |

### 4.5 가중치 전체 (RewardConfig, 인스펙터 튜닝)
**코드 기본값**: `wLE 0.50 / wCGS 0.40 / wSS 0.10` · `stepScale 0.05`
**⚠️ RLTraining 씬 현재값 (2026-07-07 CGS 단독 실험)**: `wLE 0 / wCGS 1 / wSS 0` — LE의 밀집 항이 "펼치기(균등배치)"를 벌해서 제거. stepScale도 0으로 낮춰 순수 터미널 CGS 실험 중(Step의 0.3·밀집 항까지 제거 목적).
LE 내부 `leVolW 0.4 / leCompactW 0.4 / leContactW 0.2` (contactGap 0.03)
CGS 내부 `cgsCenterW 0.5 / cgsLowW 0.5`
SS 내부 `ssHeavyW 0.6 / ssFlatW 0.4` (flatnessRef 0.1)

---

## 5. Observation — 관측 (PlacementAgent.CollectObservations) = 341 + 6 + 풀크기

| 그룹 | 차원 |
|---|---|
| 높이맵 (셀별 적재높이 / 27cm) | **341 (11×31)** |
| 현재 CoG (x/half, z/half, y정규화) | 3 |
| 총질량 / payload | 1 |
| CoG 편차 \|x\|, \|z\| | 2 |
| 종류별 남은 수 (정규화) | 풀 크기 (기본 12 / 고정 manifest 3) |
| **합** | **기본 359 / boxpack 고정 케이스 350** |

---

## 6. 커리큘럼 + Manifest 생성 (에피소드)

`OnEpisodeBegin()`이 매 에피소드 **랜덤 manifest 3~5개**(`manifestMin 3`~`manifestMax 5`, 풀=배치 가능 12종)를 뽑되, **재추첨 루프**로 아래 두 필터를 통과한 조합만 채택한다 (마지막 시도 `manifestMaxTries 40`에선 무한루프 방지로 그대로 채택).

**왜 필요한가 (2026-07-06 추가):** 완전 무작위로 뽑으면 *물리적으로 못 싣는 조합*(60cm 파이프 여러 개 + 넓은 박스 등 — 파이프는 전장 독점·위 적재 불가 H6)이 40~50% 나와, 교사 시연/에피소드가 fail-out → **demo Mean Reward가 음수**로 오염됐다. 근본 원인 = 입력 분포가 비현실적. 그래서 두 단계로 거른다:

| | ② **현실성 제약** (`ManifestRealistic`, **항상 적용**) | ① **게이팅** (`binPackerHeuristic`일 때 = demo 녹화 시만) |
|---|---|---|
| 무엇 | 도메인 규칙으로 비현실 조합 배제 | 교사(빈패커 `Pack`)가 **전부 실을 수 있는** 조합만 채택 |
| 판정 | 총질량 ≤ `maxPayloadKg`(7) · 파이프 밑면 폭 합 ≤ `trayLateralM × pipeWidthBudget`(0.7) | `Pack(manifest)` 후 `unplaced.Count == 0` |
| 성격 | 설계로 근사 보장(불완전) — RL에도 그대로 적용 | 시뮬레이션으로 완주 보장 → demo 100% 깨끗 |
| 파라미터 | `pipeWidthBudget 0.7` | `manifestMaxTries 40` |

- **자기정합성**: `Pack`과 `Decide`가 같은 그리디라 "Pack 되면 Decide도 완주". 진짜 불가능 조합 + 그리디가 막히는 조합을 **둘 다** 자동 제거.
- **트레이드오프(정직)**: 게이팅은 demo 분포를 좁힘(파이프 많은 조합 손실). 더 똑똑한 패커면 풀릴 조합도 그리디라 배제될 수 있음 → 필요 시 알고리즘 교체 검토.
- 이후 단계: 화물수↑·공간 빡빡·편심·payload 근처 (파라미터로 조절).

### A안: 학습용 게이팅 풀 (`useGatedPool`, 2026-07-06 오후 추가)

**왜:** 위 ①게이팅은 리셋마다 1281셀 `Pack`을 최대 40회 돌려 **학습(Default) 첫 리셋 60초 초과 → `UnityTimeOutException`**. 그래서 학습에선 껐더니(② 현실성만) 진단상 **교사 완주율 p=59.9%**(40% 완주불가) → 에이전트 천장 −0.18로 눌리고 fail-out 정체(−1.5). 데모(게이팅 p~100%, +0.87)와 **분포 불일치**로 BC도 실패.

**방식:** 게이팅을 **런타임이 아니라 오프라인 1회**로 옮김.
- 생성: `BinPackerVisualizer.GenerateGatedManifestPool` — ②현실성 + 교사 `Pack` 완주 통과 manifest N개(기본 5000)를 `Assets/Data/gated_manifests.txt`(type id 콤마구분 1줄=1 manifest)로 저장. manifestMin/Max=**3/5**로 맞출 것.
- 소비: `PlacementAgent.OnEpisodeBegin` — `useGatedPool`이면 파일에서 **뽑기만**(리셋당 Pack 0회 → timeout 없음). 파일 없으면 런타임 재추첨으로 **자동 폴백**. 분포는 데모와 100% 동일 → BC 정합.
- 효과: 에이전트 천장 −0.18 → **~+0.70**(완주 시 평균 Final 0.701). 단, 1281셀 실등반은 별개(B) — BC strength·max_steps 튜닝 여지.
- 진단 도구 `DiagnoseManifestDistribution`(같은 컴포넌트): p·완주율·천장 추정·미완주 지배사유 출력. 3D BPP 씬엔 `BinPackerVisualizer`만 붙음(Runner 아님).

---

## 7. 보상 소스 로드맵 (지금 vs 나중)

- **1차 (현재)**: 정적 보상 = `RewardCalculator` (기하·CoG 기반 근사).
- **2차 (예정, S6)**: 동적 주행 데이터로 학습한 **전복위험 예측기** 출력으로 보상의 안정 항 교체 → "실제 위험" 반영.

---

## 8. 코드 위치 색인 (source of truth)

| 항목 | 파일 | 핵심 |
|---|---|---|
| Rule 판정 | `Assets/Scripts/Static/RuleChecker.cs` | `IsValid()` / `H1_Payload`~`H13_Height` / `RuleConfig` |
| Rule 검증 | `Assets/Scripts/Static/RuleCheckerTest.cs` | 10 시나리오 PASS/FAIL |
| Action | `Assets/Scripts/Static/PlacementAgent.cs` | `Setup()` MakeDiscrete / `WriteDiscreteActionMask()` / `OnActionReceived()` |
| Observation | `Assets/Scripts/Static/PlacementAgent.cs` | `CollectObservations()` |
| Reward | `Assets/Scripts/Static/RewardCalculator.cs` | `Final()` / `Step()` / LE·CGS·SS / `RewardConfig` |
| Reward 검증 | `Assets/Scripts/Static/RewardCalculatorTest.cs` | 좋은/나쁜 배치 점수 비교 |
| 페널티·완주(Option C) | `Assets/Scripts/Static/PlacementAgent.cs` | `guaranteedCompletion`·`invalidStepPenalty`·`unplacedPenalty` / `PlaceByTeacher()`·`TryPlace()`. (구 `Fail()`은 OFF일 때만) |
| 커리큘럼·고정manifest | `Assets/Scripts/Static/PlacementAgent.cs` | `OnEpisodeBegin()` / `useFixedManifest`·`useGatedPool` |
| **v2 Refinement** (§11, 보류) | `Assets/Scripts/Static/RefinementAgent.cs` + `Docs/rl_config_refine.yaml` | 빈패커 배치서 시작→재배치 |
| Manifest 해석 | `Assets/Scripts/Static/CargoManifest.cs` | 인스펙터/CSV → CargoType 리스트 |
| 빈패커(교사) | `Assets/Scripts/Static/BinPacker.cs`·`BinPackerRunner.cs`·`BinPackerVisualizer.cs` | Pack/Decide/게이팅풀·진단 |
| 시각화(표시 전용) | `Assets/Scripts/Static/PlacementVisualizer.cs` | Play 중 체크박스 켜서 배치 눈으로 확인 |
| 배치 저장(주행 입력) | `Assets/Scripts/Static/PlacementAgent.cs` | `saveLayoutOnComplete`·`layoutOutName` → 완주 시 CargoLayoutFile JSON (회전 halfSize 역산). ⚠️ 학습 중 OFF |
| 위험 예측기(⚠️ 동역학→LTR) | `Assets/Scripts/Modeling/RiskModel.cs`·`RiskDisplay.cs` | 입력=주행 동역학 7피처→\|LTR\|. **배치 안 봄 → RL 배치 보상 직접 불가.** RL엔 "배치→위험" 예측기 별도 필요 |

## 9. 설계문서와 다른 점 (스테일 주의)

| 항목 | 설계문서 | **실제 코드(이 문서)** |
|---|---|---|
| 지지율 | 50%로 완화 서술 | **70%** (원본 채택) |
| 적재함 길이 | 62cm | **61cm** (2026-07-05 정정) |
| H11/H12 | 있음 | **제거됨** |

_원본 규칙 출처: `3.4.2_Hard_Constraint.docx` · `3.5_Reward_Design.docx` + 손글씨 메모._

---

## 10. ⚠️ 알려진 한계 — 학습가능성 병목 (2026-07-06 진단)

RL이 정체(std 0, 매 에피소드 fail-out)한 원인을 코드 레벨로 확정한 것들. 상세 = WORKLOG 2026-07-06.

1. **★ 탐색 벽 = 행동공간 과대(1281셀).** 격자 1cm(21×61=1281). v1(4cm=96셀)의 13배. 물리 유효면적은 동일한데 칸이 13배라 유효칸 비율이 ~1/13 → 랜덤 탐색이 20회 안에 유효칸을 못 맞힘 → 완주 0 → 균일 −1.5 → 그래디언트 0 → 정책 붕괴. **처방 = 격자 2cm급 완화(≈341셀) + GAIL + 마스킹.** (안전 적재에 1cm 정밀도 불필요.)

2. **마스킹이 약함** (`WriteDiscreteActionMask`): 지금은 **소진 종류(branch0) + 높이 꽉 찬 셀(branch1)만** 마스킹. 경계이탈/겹침/지지율/파이프 위반은 **안 막고 페널티로만** 학습. ⚠️ **구조적 제약**: 셀 유효성이 종류·회전에 의존하는데 3브랜치가 독립 동시결정이라 **완벽한 유효성 마스킹은 불가.** 단 **종류무관 경계여백 마스킹**(가장 작은 화물조차 넘칠 가장자리 셀)은 가능 → 지배 미완주사유 **H2(경계이탈) 대량 제거** 가능.

3. **fail-out 보상 절벽**: `Fail()` = 무효 20회×(−0.05) + (−0.5) = −1.5. 화물 4/5개 놓고 실패해도 ≈−1.4로 **총실패와 거의 동급**. 완주 전엔 큰 양수 보상이 없어, 완주 경험 0이면 학습신호 0. 학습가능성 구조 개선 대상(보상 소스와 무관).

4. **보상 가중치 관찰**: `LE 0.5 > CGS 0.4 > SS 0.1`. 밀도(LE)가 CoG안정성(CGS)보다 높음 → "안전 주·촘촘 부차"라면 CGS↑ 재조정 여지. **단 예측기(④) 오면 안정 항이 재정의되므로 지금 과튜닝은 헛수고.**

5. **Dense/Decide 정렬 불일치** (외부 리뷰 지적, 검증됨): `Pack(Dense)`는 부피 내림차순, `Decide()`는 항상 질량 내림차순. 현재 BC 교사=Stable(질량)이라 무해하나, **Dense를 교사/비교에 쓰면 순서 정합 필요.**

---

## 11. v2 — RefinementAgent (별도 에이전트, 2026-07-07 현재 **보류**)

> `Assets/Scripts/Static/RefinementAgent.cs` + `Docs/rl_config_refine.yaml`. v1(PlacementAgent)과 완전 독립 — 빈패커 **완성 배치에서 시작**해 재배치(relocate)로 개선하는 구조.

| 항목 | 값 |
|---|---|
| 시작 상태 | `startManifest`(B-004×8·SYN-04×4·SYN-03×4=16개)를 매 에피소드 Dense Pack (결정론 = boxpack001) |
| 관측 | 높이맵 341 + CoG 3 + 질량 1 + 편차 2 = **347** |
| 행동 | (아이템 16 · 셀 341 · 회전 2) — 화물 하나를 다른 셀로 이동 |
| 보상 | **ΔFinal**(이동 후−전), 무효 이동 = 원위치 복구 + −0.02. 누적 = 빈패커 대비 개선량 |
| 에피소드 | 25회 재배치(`stepsPerEpisode`) 후 종료 |
| 계측 (07-07 추가) | TensorBoard `Refine/ValidMoveRate` · `Refine/FinalImprovement` · `Refine/FinalAbsolute` |
| 마스킹 (07-07 추가) | branch1(셀)만: 높이 꽉 찬 셀 + 최소 화물도 못 놓는 가장자리. **겹침은 조합 의존이라 차단 불가** |

**보류 사유** (WORKLOG 07-07): 60k에서 −0.455→−0.411 flat. 25스텝 중 무효 ~20회 → 학습이 "무효 회피"에 소모, ΔFinal(±0.01~0.05) 신호가 페널티(−0.5 규모)에 묻힘. 근본 해법 = 행동에서 아이템 선택 제거(라운드로빈 순차 재배치 → 겹침까지 정확 마스킹 가능) — 재도전 시 이 구조로.

**⚠️ 씬 함정 (07-07 실전)**: ML-Agents의 base `Agent`는 추상 클래스가 아니라 Add Component로 추가 가능. 실수로 붙으면 `DecisionRequester.GetComponent<Agent>()`가 그놈을 잡아 **관측 0 경고 + 에피소드 무한**이 됨. 에이전트 오브젝트엔 Agent 파생 컴포넌트가 정확히 1개인지 확인할 것.
