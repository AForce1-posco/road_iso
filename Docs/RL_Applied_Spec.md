# RL 정적 배치 — 실제 적용 명세 (Rule · Action · Reward)

> **이 문서는 "지금 코드에 실제로 들어간 것"을 사람이 보기 쉽게 정리한 것**이다.
> 설계 문서(`RL_StaticPlacement_Design.md`)와 달리 **현재 소스 코드가 기준**(source of truth).
> 최종 확인: 2026-07-05 기준. 값이 바뀌면 이 문서도 갱신할 것.
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
| 격자 | **6(x) × 16(z) = 96 셀** | 셀 크기 ≈ 3.5(x)×3.8(z) cm |
| 화물 풀 | **12종** (트레이에 정상 배치 가능한 것) | B-007·긴 파이프 제외 |

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
| branch0 | **화물 종류** 선택 (풀에서) | 12 |
| branch1 | **격자 셀** 선택 | 96 (6×16) |
| branch2 | **회전** | 2 (0° / 90°) |

- 셀 인덱스 → 셀 중심 (x,z) → 화물을 그 위에서 **낙하 안착**(RestBottom: 바닥 또는 기존 화물 위).
- 회전 90°(rot=1) = halfSize의 x↔z 스왑.

### 3.2 마스킹 (WriteDiscreteActionMask)
- branch0: **남은 수 0인 종류** 차단.
- branch1: **높이 한도까지 꽉 찬 셀** 차단.
- branch2(회전): 종류의존이라 사전 마스킹 불가 → **무효 조합은 보상 페널티로** 학습.

### 3.3 적용 (OnActionReceived)
1. 후보 화물·셀·회전으로 `PlacedItem` 생성 (안착 높이 계산)
2. `RuleChecker.IsValid` 검사
   - **유효** → 배치 확정 + `AddReward(Step)` (shaping). 목록 다 놓으면 `Final` 보상 + `EndEpisode`.
   - **무효** → `Fail()`: −0.05, 20회 반복 시 −0.5 + `EndEpisode`.

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
| 무효 행동 1회 | −0.05 (`invalidPenalty`) |
| 무효 20회 반복 (`maxInvalidPerEpisode`) | −0.5 + 에피소드 종료 |

### 4.5 가중치 전체 (RewardConfig, 인스펙터 튜닝)
`wLE 0.50 / wCGS 0.40 / wSS 0.10` · `stepScale 0.05`
LE 내부 `leVolW 0.4 / leCompactW 0.4 / leContactW 0.2` (contactGap 0.03)
CGS 내부 `cgsCenterW 0.5 / cgsLowW 0.5`
SS 내부 `ssHeavyW 0.6 / ssFlatW 0.4` (flatnessRef 0.1)

---

## 5. Observation — 관측 (PlacementAgent.CollectObservations) = 114차원

| 그룹 | 차원 |
|---|---|
| 높이맵 (셀별 적재높이 / 27cm) | 96 (6×16) |
| 현재 CoG (x/half, z/half, y정규화) | 3 |
| 총질량 / payload | 1 |
| CoG 편차 \|x\|, \|z\| | 2 |
| 종류별 남은 수 (정규화) | 12 |
| **합** | **114** |

---

## 6. 커리큘럼 (에피소드)

- 매 에피소드 **랜덤 manifest 3~5개** (`manifestMin 3` ~ `manifestMax 5`), 풀 = 배치 가능 12종.
- 이후 단계: 화물수↑·공간 빡빡·편심·payload 근처 (파라미터로 조절).

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
| 페널티·커리큘럼 | `Assets/Scripts/Static/PlacementAgent.cs` | `Fail()` / `OnEpisodeBegin()` / `invalidPenalty`·`maxInvalidPerEpisode` |

## 9. 설계문서와 다른 점 (스테일 주의)

| 항목 | 설계문서 | **실제 코드(이 문서)** |
|---|---|---|
| 지지율 | 50%로 완화 서술 | **70%** (원본 채택) |
| 적재함 길이 | 62cm | **61cm** (2026-07-05 정정) |
| H11/H12 | 있음 | **제거됨** |

_원본 규칙 출처: `3.4.2_Hard_Constraint.docx` · `3.5_Reward_Design.docx` + 손글씨 메모._
