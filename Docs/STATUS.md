# 📍 현재 상황 (핸드오프 문서) — road_iso / CargoRiskSimulation

> **이 파일부터 읽으세요.** 채팅/세션이 바뀌어도 이 한 장이면 "지금 어디까지 왔고, 무엇을 하는 중이고, 다음에 뭘 하면 되는지"를 파악할 수 있게 유지한다.
> 세부는 각 전문 문서로 링크. **코드가 항상 최종 진실**(문서와 코드가 다르면 코드 우선, 그리고 이 문서를 고칠 것).
> 최종 갱신: **2026-07-07 오후 (v2 무효벽 진단→보류 · v1+CGS 단독 보상 학습 중 · ⭐예측기 도착)**.

> ⚠️ **브랜치 주의**: 현재 작업 브랜치 = **`minimal-cycle-boxes`** (순수 3D BPP 최소 사이클 = **§3b**). RL 탐색벽/Option C 작업은 **`3dbpp` 브랜치에 커밋(29e167b) 보존** — 아래 §3(RL)은 그 맥락. 복귀 `git checkout 3dbpp`.

---

## 0. 프로젝트 한 줄

화물 **적재 위험도 디지털 트윈**. 1:10 목업(로드셀 실측)과 오차검증하며, **RL이 안전한 적재 배치**를 학습하고 동적 주행으로 검증한다.
- **메인 개발처**: `unity/roadTest/road_iso` (Unity **2020.3.48f1**, Built-in RP, 구 Input, uGUI). *(구 `CargoRiskSimulation`은 트랙 미완성으로 이관 — 폐기)*
- **좌표 규약**: x=좌우, y=높이, z=주행/길이. 원점=트레이 중심, 바닥 top y=0.01. 적재함 안쪽 **61(z)×21(x)×27(y) cm**, 겉 64×24(벽 1.5cm).

### 목표 정의 (2026-07-06 확정)
- **주 목표**: 주문받은 **고정 화물 목록**(예: 코일3·파이프1·박스5)을 **규칙을 지키며 안전하게** 배치하는 최적 정책. + 그 배치의 위험도 예측.
- **부 목표**: 동적 주행 시 **전복이 안 나는 배치인지**, 유사 케이스의 전복/휘청임을 **미리 예측**.
- **우선순위**: **안전이 주(主)**, 촘촘함은 **부차/조건**(고정 주문이 다 들어가기 위한 feasibility + 안전 동점 시 타이브레이커). 순수 3D BPP(밀도 최대화)가 아님 — 극단 밀도는 CoG↑로 안전과 상충.
- **역할 분담**: **위험도(동적 주행·예측기 ④⑤)는 다른 담당자 작업.** 이쪽(정적 배치 RL)은 그걸 기다리며 진행. → 지금 RL 보상은 정적 근사(proxy), 최종엔 예측기 보상으로 교체. **⭐ 2026-07-07 예측기 1차 도착**(`Scripts/Modeling/RiskModel.cs`, playground 머지) — 피처 검토 후 보상 연결이 다음 과제.

---

## 1. 전체 파이프라인 (큰 그림)

```
① 빈패커(다양·안정 배치 생성)  →  ② 동적 주행(급선회)  →  ③ 피처/라벨 추출
   →  ④ 위험 예측기 학습(→ONNX)  →  ⑤ 예측기를 RL 보상으로 연결  →  ⑥ RL 2차 학습  →  ⑦ 동적 검증
```
- RL 정적 배치 학습 라인(S1~S6): **S1 규칙 → S2 보상 → S3 에이전트 → S4 학습 → S5 동적검증 → S6 동적예측보상**.
- ⚠️ 동적 주행은 느려서 RL 보상에 직접 못 씀 → **예측기(④)를 거쳐야** 한다. 크럭스 = 피처 일관성(예측기=RL보상 동일 피처)·예측기 Unity 탑재(Barracuda).

---

## 2. ✅ 완료된 것

| 단계 | 상태 | 핵심 |
|---|---|---|
| 정적 씬 MVP | ✅ | 물리 배치·대시보드·CoG/안전영역 시각화 |
| 동적 파이프라인 | ✅ | `CargoBedLoader`/`CargoRiskRecorder`/`DynamicSceneController` → results.csv (LTR·전복·화물이동) |
| **S1 규칙** | ✅ | `RuleChecker.cs` — Hard 9규칙(H1과적7kg·H2경계·H3겹침·H4~H6파이프·H7/H10포대·H8지지율70%·H13높이27). H11/H12 제거 |
| **S2 보상** | ✅ | `RewardCalculator.cs` — LE 0.5 / CGS 0.4 / SS 0.1 (0~1 정규화), `Final`+`Step` |
| **S3 에이전트** | ✅ | `PlacementAgent.cs` — 관측 1299, 행동 (종류12·셀1281·회전2), 마스킹, 페널티 |
| **S4 학습(1차)** | ✅ | PPO 씬 끝까지 돎. Mean Reward −1.67→−0.36(미수렴). env `mlagents-x86` |
| 캘리브레이션 | ✅ | 로드셀 실측 vs 계산 CoG 오차 0.1~0.3cm(x·y 양호) |
| 위험 데이터셋 | ✅ | `generate_risk_cases.py` → `Cases/` **1000개**(예측기④ 학습용, 하드룰 의도위반 허용). 옛 2000개는 archive 백업 |
| 격자 4cm→1cm | ⚠️ | RL·빈패커 전부 **1281셀** 통일. **BUT 이게 탐색벽의 근본 원인 → 2cm 완화 검토 중**(§3). 기존 `placement_v1`(4cm) onnx 비호환 |
| **빈패커 Phase1** | ✅ | `BinPacker.cs`+`Runner`+`Visualizer`. Phase1 검증 20케이스 성공률 89%·평균보상 **0.698** |
| **빈패커 Phase2 코드** | ✅ | `Decide()`(다음 한 수)+`PlacementAgent.Heuristic` 연결+rl_config `behavioral_cloning`+진단(`DiagnoseUnplaced`) |
| **A안 게이팅 풀** | ✅ | `BinPackerVisualizer.GenerateGatedManifestPool`→`gated_manifests.txt`(5000) + `PlacementAgent.useGatedPool`. 리셋당 Pack 0회·데모 정합 |
| **분포 진단 도구** | ✅ | `BinPackerVisualizer.DiagnoseManifestDistribution` — ManifestRealistic 통과율·교사완주율 p·에이전트 천장 추정 출력 |

---

## 3. 🔴 지금 진행 중 — S4 재학습: 1281셀 "탐색 벽" 돌파 (격자 완화 + GAIL)

**중간 목표**: 예측기(④, 다른 담당자)가 오기 전까지 **임시 정적 보상**으로 RL을 *학습 가능한* 상태까지 끌어올린다. 현재 핵심 장애 = **행동공간 과대(1281셀)**.

### 지금까지의 시도·결과·원인 (상세 이력 = WORKLOG 2026-07-06)
1. **demo 오염(−0.45) → 해결**: manifest에 물리적으로 못 싣는 조합 40~50% 유입 → 현실성 제약② + 게이팅① 적용 → 재기록 **Mean Reward +0.867** 합격.
2. **v2_bc (BC 워밍스타트) 정체**: 출발 −1.45 → 90k까지 −1.5 flat. BC 견인 실패.
3. **원인 검증 → A안(게이팅 풀) 구현**: `DiagnoseManifestDistribution`으로 학습분포(3~5) 측정 → 교사완주율 **p=59.9%**(40% 완주불가, 지배사유 H2 부피초과)·에이전트 천장 **−0.18**. → 오프라인 게이팅 풀로 천장을 **+0.70**로, 데모와 분포 100% 정합. **A안 자체는 정상 작동**(Play 시 "게이팅 풀 로드: 5000개" 확인).
4. **v3_gated도 정체 → 근본 원인 확정**: `Mean Reward −1.5·Std 0.000·Episode Length≈20` = **매 에피소드 fail-out, 유효 배치 0회.** BC는 로드됐으나(startup 로그 확인) 무력.
   - **진짜 벽 = 1cm 격자(1281셀) 행동공간.** v1(4cm=96셀)의 **13배**. 유효칸이 건초더미 속 바늘 → 랜덤 탐색이 20번 안에 한 번도 못 맞힘 → 모든 보상 균일 −1.5 → **PPO 그래디언트 0 → 정책 붕괴.** v1이 −0.36까지 오른 건 96셀이라 우연한 성공이 나와 발판이 생겼기 때문. **BC(strength 0.5)는 이 13배 격차를 못 메움.**
   - 결론: 정체는 **manifest 분포(A안으로 해결됨)도, 보상 부재도 아니고 → 순수 탐색/행동공간 문제.** (BC/3D를 더했는데 나빠진 게 아니라, 문제를 13배 어렵게 만들고 그걸 덮으려던 BC가 약했던 것.)

### 전략적 맥락 (왜 지금 이 인프라 작업을 하나)
- **위험도(동적·예측기 ④⑤)는 다른 담당자.** 그걸 기다리는 동안 RL을 붙든다.
- 지금 보상은 **정적 proxy**(예측기 오면 교체). 그러나 **"에이전트를 학습 가능하게 만드는 일"(탐색 벽 해결)은 보상과 무관한 인프라** → 최종 예측기-보상 RL도 **똑같은 벽**에 부딪히므로, 지금 해두면 그대로 재사용됨. **헛수고 아님.**
- ✅ **지금 할 가치**: 격자 완화·GAIL·마스킹 강화(학습가능성 = reward-independent). ⚠️ **헛수고**: 정적 보상 가중치 미세튜닝(예측기가 "좋음"을 재정의).
- ⚠️ 예측기 보상을 넣어도 **탐색 벽은 안 풀림** → 예측기 = RL을 *의미있게*, 탐색 수정 = RL을 *학습가능하게*. 둘은 별개, 둘 다 필요.

### ▶ 다음 액션 — 격자 2cm(11×31=341셀) + GAIL (run-id `placement_v4_grid2_gail`)

**✅ 코드/씬/config 적용 완료 (2026-07-06)**:
- 격자 **`cols=11·rows=31`**(≈2cm, 341셀) — `PlacementAgent`·`BinPacker`·`BinPackerRunner`·`BinPackerVisualizer` 코드 + `RLTraining`·`3D BPP` 씬 **전부**. obs 359·action (12,341,2)는 코드가 자동 산정.
- rl_config: **`gail` 활성화(strength 0.3)** + **`behavioral_cloning.strength 0.5→1.0`**.

**⬜ 남은 Unity 수작업 (순서 중요 — 격자 바뀌어 데모/풀 재생성 필수)**:
1. **게이팅 풀 재생성**: `3D BPP` 씬 `BinPackerVisualizer`(이미 11×31) → 우클릭 **"Generate Gated Manifest Pool"** → `gated_manifests.txt` 갱신(2cm 완주가능). 채택률 로그 확인.
2. **데모 재기록** (⚠️ 기존 demo는 action 1281이라 **비호환**): `RLTraining` 씬 PlacementAgent — `binPackerHeuristic`✓·`useGatedPool`✓·Behavior Type=**Heuristic Only**·Demonstration Recorder [Record✓] → Play 수 분 → 정지. `.demo` 클릭해 Mean Reward 양수(+0.7~) 확인.
3. **학습**: PlacementAgent — `binPackerHeuristic`✗·`verboseLog`✗·Record✗·`useGatedPool`✓·Behavior Type=**Default**. `mlagents-learn Docs/rl_config.yaml --run-id=placement_v4_grid2_gail --force` → "Listening" → Play.
   - ✅ 콘솔 확인: **`obs=359, action=(12,341,2)`**(격자 반영됨) + `게이팅 풀 로드: N개`.
4. 체크포인트: **Std가 0에서 벗어나는지**(= 완주 시작) + 곡선 **양수 등반**.
5. 병행: 예측기 담당자에게 **입력 피처 목록 확인**(피처 일관성 = §1 크럭스).

**진행 경과**: v4(격자2+GAIL)·v5(beta 0.02+GAIL 0.8) **둘 다 20k에서 std 0 붕괴**(10k엔 std 0.5~0.6로 배치하다가 무너짐). 격자·config로는 안 됨 → **근본 = 정책 붕괴(fail-out −1.5 절벽)** → 구조적 해결 Option C 적용.

### ✅ Option C: 보장된 완주 (2026-07-06, 코드 적용됨)
- `PlacementAgent`: 무효 행동 시 fail-out(−1.5) 대신 **작은 페널티(−0.02) + 빈패커가 대신 한 수**(`PlaceByTeacher`) → **에피소드 항상 완주**. 필드 `guaranteedCompletion`(기본 ON)·`invalidStepPenalty`·`unplacedPenalty`, 헬퍼 `TryPlace`/`PlaceByTeacher`.
- 효과: 보상이 **항상 유효 완성배치**(≈+0.6~+0.9) → std>0 → **붕괴 물리적 불가.** 최악(에이전트가 전부 무효)이어도 빈패커 바닥 **+0.7**. 행동공간·데모·BC/GAIL 불변(**재기록 불필요**).
- ▶ **실행**: Unity 컴파일 후, 학습 설정 그대로(guaranteedCompletion 기본 ON, binPackerHeuristic✗·verboseLog✗·Record✗·useGatedPool✓·Default) → `mlagents-learn Docs/rl_config.yaml --run-id=placement_v6_optC --force`.
  - 체크포인트: **Mean Reward 양수로 출발 + 붕괴 없음**(±0.6~0.9대), 이후 교사(+0.7)를 **넘는지**.
- 안 되면(양수지만 교사 못 넘음) → §4b-A 진짜 Refinement(이동 연산) 또는 마스킹 강화.

---

## 3b. 🟢 (브랜치 `minimal-cycle-boxes`) 순수 3D BPP 최소 사이클

**목표**: 파이프라인 전체(패킹→동적→PPO)를 **박스만으로 최소 규모 한 바퀴** 굴려 인터페이스 검증 + RL 학습가능성 확인. RL 완성이 아니라 **관통(vertical slice)**.

**단순화**: 화물=박스 중심(파이프/포대/코일 제외), 격자 2cm(341), 순수 **Dense** 빈패킹(안정성 없음, 공간만). 7kg 한도 유지 + 가벼운 합성 박스로 부피 채움.

**구현 완료**:
- **카탈로그 SYN 6종**(`cargo_catalog.csv`): 저밀도 합성 박스(SYN-01~06). 실측 박스는 무거워 7kg에 부피 못 채움 → SYN으로 "꽉 채운 버전" 가능. 폭 ≤10cm(트레이 21cm에 잘 들어가게).
- **`CargoManifest.cs` + `Editor/ManifestEntryDrawer.cs`**: 인스펙터 **(화물 드롭다운, 개수)** 목록 / CSV → 화물 리스트. (id 타이핑 대신 카탈로그 선택)
- **`BinPackerVisualizer`·`BinPackerRunner` 정리**: 랜덤·게이팅풀·진단 제거 → manifest 지정 → **Dense pack → 부피점유율(%) → JSON 저장**.
- **`PlacementAgent` Option C**(보장된 완주) + **`useFixedManifest`**(단일 고정 케이스).
- **`RefinementAgent.cs`(v2)** + `rl_config_refine.yaml`: 빈패커 배치서 시작 → 재배치.
- **단일 주행 파일 분리**: `CargoRiskRecorder.resultsPath`(파일명만 넣으면 Data/Results 새 파일) + `DataLogger.combinedFileName`.

**진행/결과**:
- ✅ **from-scratch 단일케이스 PPO 성공**(`boxpack001_ppo`): guaranteedCompletion ON → **Mean Reward +1.13, Std ~0.09, ~25k 수렴.** Option C 검증. = **v1 baseline**.
  - ⚠️ 단 이 run의 manifest는 **B-001**×8(씬 확인), boxpack001.json은 **B-004**×8 → run명과 실제 화물 불일치(B-001이 더 쉬움). binpacker 비교하려면 B-004로 재학습 필요.
- ⚠️ **RefinementAgent(v2) 실행 → 보류** (2026-07-07, 상세 WORKLOG):
  - 1차 실패: 씬에 **빈 base `Agent` 컴포넌트**가 잘못 추가돼 그놈이 스텝 돎(관측 0 경고·에피소드 무한) → 제거로 해결. (ML-Agents `Agent`는 추상 아님 — Add Component로 추가 가능한 함정)
  - 2차(boxpack002_refine): **-0.455→-0.411(60k) flat.** 25스텝 중 무효 이동 ~20회 — "무효 회피"만 배우고 ΔFinal 신호는 페널티에 묻힘. 계측 3종(`Refine/ValidMoveRate`·`FinalImprovement`·`FinalAbsolute`)·셀 마스킹 추가했으나 **겹침은 브랜치 독립 마스킹으론 차단 불가**(조합 의존). 근본 해법=아이템 라운드로빈 — **v2는 여기서 보류**(폐기 아님).
- 🔵 **현재 진행: v1 + CGS 단독 보상** (균등배치 최소 실험): RLTraining 씬 rewardConfig **wLE=0·wCGS=1·wSS=0**(LE 밀집 항이 "펼치기"를 벌하던 것 제거). run `b001_cgs` 1.14→1.325(35k) 건강 → **stepScale=0**(순수 터미널 CGS, 기대 0.6대→0.85+) 재실험 중.
  - **판정은 곡선이 아니라 배치 모양**: Play 중 PlacementVisualizer 체크박스 켜서 Game 뷰 확인(보고 끄기). 낮고 고르게=성공 / 중앙 탑=CGS 해킹→펼침 항 추가.
- ⭐ **위험 예측기 도착** (2026-07-07, playground 머지 000432d): `Assets/Scripts/Modeling/RiskModel.cs`·`RiskDisplay.cs`·`Assets/Resources/risk_model_treedata.json`. "예측기 오면 보상 교체" 전제가 현실이 됨.

**▶ 다음 액션**:
1. **stepScale=0 run 판정**: 곡선(0.85+ 수렴 여부) + **배치 모양 눈 확인** → 펼쳐졌으면 정적 RL 여기서 멈춤(더 튜닝은 헛수고 — 예측기 보상으로 교체 예정이므로).
2. **⭐ `RiskModel.cs` 입력 피처 검토**: 예측기 입력 피처 = RL 보상/관측 피처 일관성(§1 크럭스). 예측기를 RL 보상으로 붙이는 인터페이스 설계.
3. 학습된 배치 **동적 주행 1회**(layoutPath+resultsPath) → LTR/roll 확인 = "CGS 좋은 배치가 실제로 안전한가" 검증 → 최소 사이클 완주.
4. (이후) manifest B-004 통일 → binpacker vs RL 공정 비교. v2(Refinement)는 라운드로빈 구조로 재도전 여지.

---

## 4. 📅 다음 단계 (Phase 2 이후)

- **S4 확장**: SAC 등 알고리즘·보상가중치 비교(같은 env, run-id 나눠 TensorBoard 겹쳐보기). ⚠️ 보상 정의 바꾸면 Mean Reward 직접비교 금지(중립지표 필요).
- **S5 동적검증**: best onnx → 케이스 주행 → 실제 전복/LTR로 RL vs 빈패커 vs 랜덤 비교.
- **S6 동적예측보상**: ④ 위험 예측기 출력으로 보상의 안정 항 교체 → "실제 위험" 반영.

---

## 4b. 🔀 대안·후보 흐름 (기록 — 지금 안 함, 막히면/여유 되면 검토)

**두 후보는 서로 독립적인 축**이라 동시 채택 가능 (A=RL 내부설계 / B=파이프라인 전략).

### A. Refinement 아키텍처 (탐색벽 회피 후보)
- 지금 = **from-scratch**(RL이 빈 트레이부터 하나씩, 빈패커는 BC 교사). 무효행동을 페널티로 학습 → **1281셀 탐색벽의 근원.**
- 대안 = **Refinement**: 빈패커의 **유효 완성배치에서 시작**, RL은 "화물 교체/이동/회전" 같은 **개선 연산**만. 유효상태에서 유효연산이라 무효행동·탐색벽이 거의 사라짐. 대신 자유도는 낮음.
- **언제**: 격자완화+GAIL로도 std 0이 안 풀리면 진지하게 검토. (사용자 아이디어, 2026-07-06)

### B. Dense/Stable 동적 검증 흐름 (정적 보상 proxy 타당성 실증)
파이프라인 §1과 뼈대 동일 + **검증 실험을 명시**:
1. **Dense→동적**: "공간만 채우면 실제로 위험한가?" (순수 밀도가 위험함을 실증 = 왜 안전이 필요한지)
2. **Stable→동적**: **"정적 안정성 보상(LE/CGS/SS)만으로 동적 위험이 줄어드는가?"** ← **우리가 최적화 중인 proxy가 좋은 근사인지 검증.** Stable이 Dense보다 전복/LTR 낮으면 → 정적 RL 붙잡을 값어치 실증.
3. Stable 교사 RL 워밍스타트 (현재)
4. 동적결과 → 예측기(정적 보상이 실제 전복/LTR과 어긋나는 부분 보정)
5. 예측기 기반 RL (최종)
- **가치**: proxy 타당성을 **실측**으로 확인(아직 아무도 안 함). **RL 성공과 무관하게 지금 가능**(빈패커 배치 + 동적 파이프라인). 다른 담당자에게 넘길 **레이아웃(Dense/Stable/RL/Random)·질문 정의서** 역할.
- ⚠️ 1·2·4단계(동적·예측기) = **다른 담당자 몫.** 3단계 탐색벽은 이 흐름이 안 풀어줌(A/격자/GAIL 담당).

---

## 5. 📚 문서 색인 (무엇을 볼 때 어디로)

| 알고 싶은 것 | 문서 |
|---|---|
| **지금 상황·다음 액션** (이 파일) | `Docs/STATUS.md` |
| 날짜별 작업·결정·사건 일지 (역사) | `Docs/WORKLOG.md` |
| 코드에 실제 들어간 Rule·Action·Reward·Observation (**현재 진실**) | `Docs/RL_Applied_Spec.md` |
| 빈패커 알고리즘·PackMode·Phase | `Docs/BinPacker_Design.md` |
| S1/S2 구현 당시 기록(값 일부 스테일, 배너 참고) | `Docs/RL_Implementation_S1_S2.md` |
| RL 원설계(MDP·DOE·커리큘럼) | `Docs/RL_StaticPlacement_Design.md` |
| mlagents 환경 설치 레시피 | `Docs/RL_Env_Setup.md` |
| 학습 설정(PPO·BC 하이퍼파라미터) | `Docs/rl_config.yaml` |
| 정적 씬 에디터 셋업 | `StaticSceneSetup.md` (repo 루트) |

---

## 6. ⚙️ 환경·함정 (재현 필수)

- **ML-Agents 패키지 = `com.unity.ml-agents 2.0.2` 고정**(2.2.1-exp로 올리면 Barracuda 2.3.1로 딸려 올라가 플러그인 DLL 참조 깨짐). 파이썬 `mlagents 0.28.0`/API 1.5.0은 공통.
- **학습 env `mlagents-x86`**: Rosetta x86_64 + python 3.9.13 + torch 1.8.1 + numpy 1.21.2 + protobuf 3.19.6 + tensorboard 2.10.1 + cryptography 41.0.7. ⚠️ `patch_cattrs_singledispatch.py` 필수(멱등). 활성화: `source /opt/anaconda3/etc/profile.d/conda.sh && conda activate mlagents-x86`.
- **학습 연결**: `mlagents-learn … --run-id=<id>` → "Start training…" 뜨면 Unity에서 Behavior Type=**Default**로 바꾸고 Play(port 5004). 추론만 볼 땐 Inference Only. 이어학습 `--resume`(같은 id), 덮어쓰기 `--force`, 새 실험은 새 id.
- **학습 중엔** `PlacementVisualizer`·`verboseLog` **끄기**(느려짐).
- **rl_config.yaml 한글 주석** → 윈도우(cp949) `UnicodeDecodeError` 시 `set PYTHONUTF8=1` 후 실행.
- ⚠️ **git 브랜치**: `rl_config`·`results`가 `main`에 커밋됨. `static` 브랜치 체크아웃 시 안 보임(삭제 아님). 코드는 양쪽 다 있음.
- **작업 스타일(yujalami)**: 현실성 최우선("장난감 같다" 싫어함)·큰 변경 전 "이렇게 이해했는데 맞아?"로 확인 후 진행·코드는 Claude 작성/Unity 에디터 수작업은 본인 → 절차 생성으로 수작업 최소화 선호.

---

## 7. 🗂 핵심 파일 위치

| | 경로 |
|---|---|
| RL 에이전트 v1 (from-scratch, 현재 사용) | `Assets/Scripts/Static/PlacementAgent.cs` |
| RL 에이전트 v2 (Refinement, 보류) | `Assets/Scripts/Static/RefinementAgent.cs` + `Docs/rl_config_refine.yaml` |
| ⭐ 위험 예측기 (다른 담당자, 07-07 합류) | `Assets/Scripts/Modeling/RiskModel.cs` · `RiskDisplay.cs` · `Assets/Resources/risk_model_treedata.json` |
| 규칙 / 보상 | `Assets/Scripts/Static/RuleChecker.cs` · `RewardCalculator.cs` ⚠️ RLTraining 씬은 wLE=0·wCGS=1·wSS=0 오버라이드 |
| 빈패커 | `Assets/Scripts/Static/BinPacker.cs` · `BinPackerRunner.cs` · `BinPackerVisualizer.cs` |
| 학습 설정 | `Docs/rl_config.yaml` |
| demo(BC 교사) | `Assets/Demonstrations/PlacementAgentDe.demo` (1281셀용, +0.867) ⚠️ **격자 바꾸면 재기록 필수** |
| A안 게이팅 풀 | `Assets/Data/gated_manifests.txt` (5000, type id 콤마구분) |
| 학습 결과 | `results/<run-id>/` — `placement_v1`(4cm, −0.36) · `placement_v2_bc`(정체) · `placement_v3_gated`(정체 std0) |
| 동적 결과 | `Assets/Data/Results/results.csv` (234행 주행완료: max_abs_ltr·rollover·risk_grade·roll·pitch·cargo_shift…) |
| 위험 데이터셋 | `Assets/Data/Cases/` (**1000**) · 빈패커 baseline `Assets/Data/Cases_binpack/` (**200**) |
| 씬 | `Assets/Scenes/RLTraining.unity`(학습·PlacementAgent) · `StaticSceneRoot.unity` · `3D BPP.unity`(빈패커/진단/풀생성 = `BinPackerVisualizer` 단독) |
