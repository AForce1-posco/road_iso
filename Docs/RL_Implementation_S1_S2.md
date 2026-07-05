# RL 구현 기록 — S1(RuleChecker) · S2(Reward) 상세

> 이 문서는 설계문서(`RL_StaticPlacement_Design.md`)를 **실제 코드로 옮긴 기록**이다.
> 무엇을 어떻게 만들었는지, 어떤 값을 왜 정했는지, 원본 규칙과 무엇이 달라졌는지,
> 그리고 이게 전체 학습 파이프라인에서 어디에 쓰이는지를 빠짐없이 남긴다.

---

## 0. 전체 파이프라인에서의 위치

```
[화물 목록] → PlacementAgent(S3) ─매 스텝─▶ (화물,셀,회전) 후보
                     │
                     ├─ RuleChecker(S1) : 이 후보가 규칙 위반인가? (O/X)  → 위반이면 마스킹(선택 차단)
                     │
                     └─ RewardCalculator(S2) : 이 배치가 얼마나 좋은가? (점수)  → 학습 신호
                     ↓
             학습(S4) → 동적주행 검증(S5) → 동적예측 보상(S6, 2차)
```
- **S1 = 판정관(judge)**: "할 수 있냐 없냐"의 절대선. 불가능/불법은 아예 못 고르게 막는다(Action Masking).
- **S2 = 채점관(scorer)**: 가능한 배치들 중 "얼마나 좋냐"에 점수를 매겨 에이전트를 유도한다.
- 둘 다 **독립 순수 클래스**(씬 오브젝트에 안 묶임) → 테스트·재사용·DOE에 유리.

---

## 1. 환경 상수 (확정값)

| 항목 | 값 | 비고 |
|---|---|---|
| 적재함(안쪽) | **62 × 21 × 27 cm** | 62=주행/길이, 21=좌우, 27=높이 한도 |
| Unity 로컬 치수 | x=0.21, z=0.62, y한도=0.27 (m) | 원점=트레이 중심, 바닥 top y=0.01 |
| **축 매핑** | 설계 x(주행)=**Unity z** / 설계 y(좌우)=**Unity x** / 설계 z(높이)=**Unity y** | 코드·주석에 명시 |
| 최대 적재중량 | **7 kg 목업** (= 700 kg 실차, massScale 100) | H1 |
| 격자 | **4 cm → 16 × 6 ≈ 96 셀** | 안정성 목표라 ±2cm 오차 무의미 → 4cm 확정 |
| 화물 종류 | **16종** (박스7·톤백1·코일1·파이프7) | 전부 Bounding Box로 추상화 |
| 고정 여부 | 전부 고정(secured) 가정 | RL 변수 아님 |

> ⚠️ 현재 물리 트레이(CargoPlacer)는 아직 0.64×0.24 → **RuleChecker/RewardCalculator는 위 실측(0.21×0.62×0.27)을 파라미터로** 쓴다. 물리 트레이 리사이즈는 추후(§7-b).

---

## 2. S1 — RuleChecker.cs (구현 완료 · 검증 완료)

파일: `Assets/Scripts/Static/RuleChecker.cs`

### 2.1 구조
| 요소 | 역할 |
|---|---|
| `RuleChecker` | 판정 클래스. `IsValid(placed, cand, out reason)→bool` (마스킹) + `Evaluate(...)→RuleReport` (측정치) |
| `RuleConfig` | 임계값 모음 (직렬화 → 인스펙터 튜닝) |
| `PlacedItem` | 배치 1개 = 종류 + center + halfSize(AABB, 회전 반영) |
| `RuleReport` | 측정치 = 총질량·지지율·CoG (S2가 사용) |

- `PlacedItem`은 모든 화물을 **축정렬 바운딩박스(AABB)**로 추상화. 회전(0/90°)은 halfSize의 x↔z 스왑으로 반영.
- CoG는 질량가중 중심(Σm·center / Σm)으로 자체 계산 (LoadCalculator와 동일 정의).

### 2.2 Hard 규칙 (9개) — 위반 시 IsValid=false + 사유
| ID | 규칙 | 판정식 / 임계값 |
|---|---|---|
| **H1** | 과적 금지 | 총질량 + 후보 ≤ **7 kg** |
| **H2** | 적재함 경계 내부 | AABB가 x[±0.105]·z[±0.31]·바닥 내부. **파이프 z 오버행은 뒤(−z=테일게이트)만** — 앞(+z=운전석/캐빈)은 모든 화물 오버행 금지 (2026-07-05 개정, §11) |
| **H3** | 화물 겹침 금지 | 3D AABB 교차 X (격자 중첩=이걸로 커버) |
| **H4** | 파이프 주행축(z) 배치 | 파이프의 halfSize.z가 최장(길이가 z 방향) |
| **H5** | 파이프 바닥층 | 파이프 밑면 ≈ 바닥(y=0.01) |
| **H6** | 파이프 위·아래 타화물 금지 | 파이프 기둥(x,z 겹침)에 다른 화물 상/하 금지 |
| **H7/H10** | 포대 위엔 포대만 | 후보가 비포대인데 받침이 포대면 금지 (포대 위 상자 금지) |
| **H8** | 지지율 ≥ **70%** | 바닥=100%, 적층=받침 화물과 XZ 겹침면적/후보 밑면적 ≥ 0.70 |
| **H13** | 높이 ≤ **27 cm** | 후보 top ≤ 0.01+0.27 = 0.28 m |

### 2.3 제거한 규칙 (사용자 결정)
| ID | 원안 | 제거 사유 |
|---|---|---|
| ~~H11~~ | CoG 전후 15% | **원본 해석 충돌** — 손글씨 "극단 15% 금지(느슨)" vs 설계문서 "±15% 초과 금지(빡빡)" 반대로 읽힘 → 혼란 방지 위해 Hard에서 제외. (CoG 중앙 유도는 S2 보상 CGS로) |
| ~~H12~~ | 전/후축 하중 10kg | **논리적 비구속** — payload 7kg면 전+후=7 < 10 이라 절대 안 걸림(죽은 규칙) → 제외. (축하중은 동적 검증 S5에서 실측) |

### 2.4 원본 대비 채택 결정
- 규칙 원본 = `3.4.2_Hard_Constraint.docx` + `3.5_Reward_Design.docx` + 손글씨 메모.
- 설계문서(.md)는 학습성 위해 **지지율을 70%→50%로 완화**했으나, **원본(docx H8·손글씨) = 70%가 확정** → **70% 채택**.
- 원칙: "논리적으로 안 맞거나 객관적으로 틀린 것 아니면 원본대로 적용" (사용자 지시).

### 2.5 검증 (RuleCheckerTest.cs)
빈 GameObject에 `RuleCheckerTest` 추가 → Play → Console에 시나리오별 PASS/FAIL.
- 10 시나리오: 중앙박스(유효)·겹침(H3)·경계이탈(H2)·3층높이초과(H13)·파이프z축(유효)·파이프회전(H2)·포대위상자(H7)·포대위포대(유효)·지지율<70%(H8)·과적(H1).
- **결과: 10/10 PASS** (2026-07-05). 각 규칙이 의도대로 작동 확인.

---

## 3. S2 — RewardCalculator (구현 완료 · 검증 완료)

파일: `Assets/Scripts/Static/RewardCalculator.cs` + `RewardCalculatorTest.cs`

**검증(2026-07-05)**: 좋은 배치(무거운거 아래·중앙) R=0.650(CGS 0.88, SS 0.92) vs 나쁜 배치(무거운거 위·구석) R=0.539(CGS 0.47, SS 0.45) → ✅ 좋은>나쁜. 나쁜 배치가 LE는 더 높았으나(구석 밀집) 안정성 낮아 총점 역전 = "밀집만으론 안 됨"을 보상이 잡음(의도대로).

### 3.1 보상 공식 (문서 5절)
```
R = w_LE·LE + w_CGS·CGS + w_SS·SS
초기 가중치: w_LE = 0.50, w_CGS = 0.40, w_SS = 0.10   ← (A)안 채택
```
- 각 목적함수는 **0~1로 정규화** 후 가중합 → 총점.
- **가중치는 필드로 노출**(인스펙터 조절) → 나중에 **DOE(실험계획법)로 최적 조합 결정**. 초기값은 문서 기준선.

### 3.2 3 목적함수
| 목적함수 | 초기 W | 구성요소 |
|---|---|---|
| **Loading Efficiency (LE)** | 0.50 | 부피활용율(화물부피/적재함부피), 데드스페이스↓, 격자밀집, 벽·화물 접촉면↑ |
| **CoG Stability (CGS)** ⭐ | 0.40 | CoG x·z 중앙 수렴(0에 가까울수록↑), **CoG y(높이) 낮을수록↑** |
| **Stacking Stability (SS)** | 0.10 | 무거운 것 아래(하부 평균질량↑), 상단 표면 평탄도, 포대 네스팅 |

- **제외**: 좌우/전후 하중편차·축하중 → CoG항과 중복이라 보상서 빼고 **동적 검증(S5)에서 실측**.

### 3.3 스텝 보상 + 최종 보상 (둘 다 — 사용자 결정)
- **스텝 보상(shaping)**: 화물 하나 놓을 때마다 CoG 중앙·밀집을 소폭 가점 → 학습 초반 방향 잡기 쉬움.
- **최종 보상**: 목록 다 놓으면 위 `R` 합산 → 최종 배치 품질 평가.
- **Hard 위반**(마스킹이 못 막고 새어나온 경우): 큰 음수 + 에피소드 종료.

### 3.4 구조
- `Evaluate(배치전체) → (총점, 항목분해 LE/CGS/SS)` — 총점만이 아니라 **쪼개서 반환** → 디버깅·DOE에서 "무엇이 점수를 좌우하는지" 파악.
- RuleChecker의 `RuleReport`(지지율·CoG) 재사용 + 기하(부피·평탄도) 추가.
- RewardCalculatorTest로 몇 배치 넣어 점수 확인 예정 (RuleCheckerTest 방식).

### 3.5 LE 0.5 유지의 의미 (짚어둠)
- 문서는 "최적화 목표=안정성"이라 하면서 가중치는 LE(적재효율)를 0.5로 제일 크게 줌 → **미묘한 불일치**.
- LE 0.5면 에이전트가 "공간 채우기"를 최우선 학습 → 빽빽하되 무게중심도 챙기는 배치.
- **(A) 문서값 0.5/0.4/0.1로 시작** 후 돌려보고 안정성 부족하면 DOE로 CGS 비중↑. 필드라 언제든 변경 가능.

---

## 4. 결정 사항 총정리

| # | 결정 | 값/방식 |
|---|---|---|
| 1 | 화물 추상화 | 전부 Bounding Box (AABB) |
| 2 | 좌표·축 | Unity x=좌우·y=높이·z=주행. 설계 x(주행)=Unity z |
| 3 | 적재함 | 62×21×27 cm (파라미터) |
| 4 | payload | 7 kg 목업 |
| 5 | 격자 | 4 cm, 16×6 |
| 6 | Hard 규칙 | 9개 (H1~H8·H13). H11·H12 제거 |
| 7 | 지지율 | 70% (원본 채택) |
| 8 | 보상 | R=0.5·LE+0.4·CGS+0.1·SS, 가중치 필드노출, DOE 조정 |
| 9 | 보상 방식 | 스텝 shaping + 최종 보상 둘 다 |
| 10 | 화물 순서 | 에이전트가 선택(행동에 포함) |
| 11 | 보상 소스 | 1차=정적(LoadCalculator) → 2차=동적 예측기 |

---

## 5. 다음 단계

| 단계 | 내용 | 상태 |
|---|---|---|
| S1 | RuleChecker | ✅ 완료·검증 |
| S2 | RewardCalculator | ✅ 완료·검증 |
| S3 | ML-Agents + PlacementAgent | ✅ 완료·씬검증 |
| S4 | config.yaml + PPO 학습 | ✅ **1차 학습 완료** (§8) |
| S5 | 동적 주행으로 검증 (RL vs 랜덤/휴리스틱) | 대기 |
| S6 | 동적 예측모델 보상 교체 (2차) | 대기 |

> **부가 작업 (2026-07-05):** ⓐ "톤백"→"포대" 전면 개명 + 빵빵한 자루형 메시로 교체 (§9). ⓑ RL 배치 시각화 도구 `PlacementVisualizer` 신설 (§10).

---

## 7. S3 — PlacementAgent.cs (코드 완료)

파일: `Assets/Scripts/Static/PlacementAgent.cs` (ML-Agents 2.0.2, PPO)

### 7.1 알고리즘
- **ML-Agents의 PPO** 사용 (직접 구현 X). 우리는 "환경"(관측·행동·보상)만 정의, 학습은 Python(mlagents)이 PPO로 수행.
- Unity=시뮬레이션(배치 시행), Python=학습. 둘이 통신.

### 7.2 관측 (Observation) — `ObsSize = cols*rows + 6 + 종류수`
| 그룹 | 차원 |
|---|---|
| 높이맵 (셀별 적재높이/27cm) | cols×rows = 6×16 = 96 |
| 현재 CoG (x/half, z/half, y정규화) | 3 |
| 총질량 / payload | 1 |
| CoG 편차 |x|,|z| | 2 |
| 종류별 남은 수 (정규화) | 풀 크기(기본 12) |
→ 기본 96+6+12 = **114**. Initialize()에서 코드로 BrainParameters에 자동 설정.

### 7.3 행동 (Action) — `MakeDiscrete(종류수, 셀수, 2)`
- branch0 = 화물 종류 선택(남은 것 중), branch1 = 격자셀(96), branch2 = 회전(0/90).
- 셀 인덱스 → 셀 중심 (x,z), 화물을 그 위에 "떨어뜨려" 바닥/기존화물 위 안착.

### 7.4 마스킹 (WriteDiscreteActionMask)
- branch0: 남은 수 0인 종류 차단.
- branch1: 높이한도까지 꽉 찬 셀 차단.
- branch2(회전)는 종류의존이라 사전마스킹 불가 → **무효 조합은 보상 페널티**로 학습(-invalidPenalty, 반복 실패 시 종료).

### 7.5 보상 연결
- 유효 배치마다 `RewardCalculator.Step`(shaping) + 목록 완료 시 `Final`(3목적함수 가중합) → EndEpisode.
- 무효 행동: -0.05, 20회 반복 시 -0.5 + 종료.

### 7.6 에피소드 / 커리큘럼
- 매 에피소드 **랜덤 manifest 3~5개**(usableTypeIds 풀에서). 풀 = 트레이에 정상 배치 가능한 12종(B-007·긴파이프 제외).
- 커리큘럼: 1단계 3~5개 넉넉 → 이후 화물수↑·공간 빡빡·편심 유혹·payload 근처 (manifestMin/Max·풀로 조절).

### 7.7 씬 셋업 (필요 컴포넌트)
빈 GameObject에: **PlacementAgent** + **Behavior Parameters**(자동 추가, Behavior Name 지정) + **Decision Requester**(매 스텝 결정 요청). obs/action은 Initialize()가 코드로 설정.

---

## 6. 추후 할일 (보류 — 사용자 지시)

- **(a) 캘리브레이션 오차검증** (`CalibrationRunner`): 목업 로드셀 실측 CoG vs 계산 CoG 비교 → RL 1사이클 돈 뒤에.
- **(b) 물리 트레이 리사이즈**: CargoPlacer 0.64×0.24 → **0.21×0.62**(안쪽=실측, 벽두께 바깥, 높이 27=적층한도) + **2000 케이스 재생성**(W-001 없이 16종, 파이프 z축). 데이터 본격 수집 때.
  - RuleChecker/RewardCalculator는 실측값을 파라미터로 쓰므로 (b) 전에도 RL 진행 가능.

---

## 8. S4 — PPO 1차 학습 (완료, 2026-07-05)

### 8.1 파이썬 환경
- **conda env `mlagents-x86`** (Apple Silicon → Rosetta x86). 전체 레시피·이유 = `Docs/RL_Env_Setup.md`.
- 활성화: `source /opt/anaconda3/etc/profile.d/conda.sh && conda activate mlagents-x86`
- 핵심 버전: python 3.9.13 / mlagents 0.28.0 / torch 1.8.1 / numpy 1.21.2 / protobuf 3.19.6 / tensorboard 2.10.1 / cryptography 41.0.7. **cattrs 패치**(`Docs/patch_cattrs_singledispatch.py`) 필수.

### 8.2 config — `Docs/rl_config.yaml`
- Behavior Name = **PlacementAgent** (Behavior Parameters와 일치해야 연결).
- PPO: batch_size 128 / buffer_size 2048 / lr 3e-4(linear) / beta 5e-3 / hidden 256×2 / normalize true / gamma 0.99 / max_steps 500000 / time_horizon 64.
- **알고리즘 교체**: `trainer_type: sac`로 바꾸면 SAC 실험 (환경 재작업 X). 마스킹은 ml-agents PPO/SAC 네이티브(=Maskable 기본 탑재). DQN·SB3 MaskablePPO는 `UnityToGymWrapper`+SB3 래퍼 필요(별도 작업, MultiDiscrete는 DQN 미지원).

### 8.3 학습 실행
```bash
conda activate mlagents-x86
mlagents-learn Docs/rl_config.yaml --run-id=placement_v1
# "Start training..." → Unity에서 Behavior Type=Default 로 바꾸고 RLTraining 씬 Play (port 5004)
```
- **run-id 규칙**(터미널 인자): 새 실험=새 `--run-id`, 덮어쓰기=`--force`, 이어학습=`--resume`(같은 run-id).
- **결과물** `results/<run-id>/`: `PlacementAgent.onnx`(최종) + `PlacementAgent/`(5만 스텝마다 체크포인트 .onnx/.pt) + events(TensorBoard) + configuration.yaml.
- **곡선** `tensorboard --logdir results` → `http://localhost:6006` → **Cumulative Reward 우상향**이 학습 신호.
- 중단해도 체크포인트 + Ctrl+C export로 기록 남음.

### 8.4 1차 결과
- 속도 ~205 스텝/s. **Mean Reward −1.671 → −0.361** (10k→500k, 꾸준히 우상향, **아직 미수렴**).
- 음수인 건 초반 Fail(−0.5)·무효행동(−0.05) 페널티 평균이 남아서. 학습 신호는 확실. baseline 확보.

### 8.5 학습된 모델 재생 (추론)
- `results/.../PlacementAgent.onnx` → `Assets/`로 복사 → Behavior Parameters의 **Model 슬롯**에 넣고 **Behavior Type = Inference Only**.
- ⚠️ **다시 학습**할 땐 Type을 **Default**로 (모델 슬롯은 무시됨, 빼도 됨). Inference Only면 트레이너 연결 안 함.

### 8.6 다음 (실험 로드맵)
- 같은 env로 **알고리즘/보상가중치 비교** (PPO↔SAC, w_LE/CGS/SS). run-id 나눠 TensorBoard 겹쳐보기.
  ⚠️ **보상 정의를 바꾸면 Mean Reward 직접 비교 금지** — 스케일이 달라짐. 중립지표(실제 CoG편차·S5 전복여부)로 비교.
- → **S5 동적 검증**: best onnx 배치 → 2000케이스 주행 → 실제 roll/LTR/전복 측정 → RL vs 랜덤/휴리스틱.

---

## 9. 부가 ⓐ — "톤백" → "포대" 전면 개명 + 자루형 메시 (2026-07-05)

- **개명 범위**: 코드(`CargoCatalog.cs` name·주석), `Assets/Data/cargo_catalog.csv`, **케이스 JSON 478개** 전부 `톤백 T-001` → `포대 T-001`.
- ⚠️ **왜 JSON까지?** `CargoBedLoader.cs`가 화물을 **이름(`t.name == name`)으로 매칭**함 → 카탈로그 이름만 바꾸면 2000케이스가 화물을 못 찾음. **id(`T-001`)는 불변**이라 RL 모델·풀은 영향 없음.
- **모양 교체**: `CargoFactory.CreateSack` 캡슐 → **빵빵한 자루형 절차 메시**(`BuildBagMesh`: 단위 큐브를 구면으로 blend해 모서리 둥글림, 면 중심 ±0.5 유지 → 바운드=단위). 콜라이더는 안 구르는 BoxCollider. 정적·동적·RL 씬 전부 이 모양(CargoFactory 공용).

## 10. 부가 ⓑ — RL 배치 시각화 `PlacementVisualizer.cs` (2026-07-05)

파일: `Assets/Scripts/Static/PlacementVisualizer.cs` (표시 전용, **학습/보상/관측 무영향**).

- **필요 이유**: PlacementAgent는 배치를 **순수 수학(AABB, `PlacedItem`)으로만** 계산 → 3D를 안 그림. 이 컴포넌트 없으면 씬에 아무것도 안 보이고 Console 로그만 남음.
- **연결**: PlacementAgent에 공개 접근자 `PlacedItems`(읽기전용) 추가 → 시각화가 그걸 읽어 그림.
- **그리는 것**:
  - 화물 = **CargoFactory 재사용**(실물 모양·재질). 회전(rot90)은 halfSize로 역산해 wrapper yaw 90°.
  - 적재함(0.21×0.62×0.27) = 바닥판 + 하늘색 와이어박스 + **6×16 그리드**(RL 96셀과 동일).
  - **최대높이 27cm** = (A) 상단 빨간 굵은 테두리 + (B) 반투명 빨간 천장면.
  - **무게중심(CoG)** = 빨간 구 + 바닥까지 수직선 (실시간).
  - `autoCamera`(Game뷰 전용 카메라), `displayScale`(기본 10배), `showGrid`/`showCoG` 토글.
- ⚠️ **학습 중엔 끄기**: time_scale 20배라 눈으로 못 보고, 매 배치 메시 생성이 학습을 느리게 함. 추론(Inference Only) 1배속 재생 때만 사용.

---

## 11. 부가 ⓒ — 축 규약 & 오버행 규칙 개정 + 코너 라벨 (2026-07-05)

- **축 규약 확정** (`LoadCalculator.cs` 명시): **front(운전석/캐빈) = +z**, **right = +x**.
  - FL(−x,+z) · FR(+x,+z) = **앞** / RL(−x,−z) · RR(+x,−z) = **뒤**.
- **H2 오버행 개정**: 기존엔 파이프가 z 양쪽으로 오버행 가능 → **물리적 오류**(앞은 운전석이라 불가).
  - 수정: 앞(+z) 경계는 **모든 화물 강제**, 뒤(−z)만 파이프 오버행 허용. `RuleChecker.H2_Bounds`.
  - ⚠️ 기존 `.onnx`는 옛 규칙으로 학습됨 → 새 규칙 하에선 앞 오버행 시도가 **Fail 처리**(재생 시 앞 오버행 안 보임). 깔끔한 모델은 **재학습** 필요.
- **코너 라벨 FL/FR/RL/RR**: RL 시각화(`PlacementVisualizer`)와 정적 씬(`CargoPlacer`, `showCornerLabels` 토글) 둘 다 4코너 TextMesh. 앞=주황, 뒤=하늘색.

---

_기록 시작 2026-07-05. S1~S4(PPO 1차 학습) 완료. 포대 개명·배치 시각화·H2 오버행 개정(앞 금지) 반영. 이 문서는 구현 진행에 따라 갱신._
