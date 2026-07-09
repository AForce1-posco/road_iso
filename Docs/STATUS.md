# 📍 현재 상황 (핸드오프 문서) — road_iso / CargoRiskSimulation

> **이 파일부터 읽으세요.** 채팅/세션이 바뀌어도 이 한 장이면 "지금 어디까지 왔고, 무엇을 하는 중이고, 다음에 뭘 하면 되는지"를 파악할 수 있게 유지한다.
> 세부는 각 전문 문서로 링크. **코드가 항상 최종 진실**(문서와 코드가 다르면 코드 우선, 그리고 이 문서를 고칠 것).
> 최종 갱신: **2026-07-09 (B 사이클 완결 · surrogate(p95) 보상 RL · refinement > from-scratch 검증)**.

> ⚠️ **브랜치**: 현재 체크아웃 = **`0707_playground`** (= minimal-cycle-boxes 계열, **§3b가 현재**). 과거 1281셀 탐색벽/Option C 사투는 **`3dbpp` 브랜치 보존** — **아래 §3은 "역사"** (현재 아님). 복귀 `git checkout 3dbpp`.

---

## ⚡ 최신 (2026-07-09) — B 사이클 완결: surrogate 보상 RL로 baseline보다 안전한 배치 발견

| 항목 | 값 | 비고 |
|---|---|---|
| **보상 = surrogate** | `-리스크(p95\|LTR\|)` (배치→동적위험 예측기) | 정적 CGS proxy 졸업 → 실측 주행 기반 예측기 보상으로 전환 |
| **surrogate 모델** | **RandomForest**(머신러닝, DL 아님), 13피처, 라벨 p95, **R²0.96** | `Resources/layout_risk_p95.json`, `LayoutRiskModel.cs`가 트리JSON 로드(**스왑가능**: JSON만 교체) |
| **학습 데이터** | 같은 manifest 배치 86개(case9001~9086, `gen_layouts.py`) 주행 | ⚠️ **results.csv 요약버그**(max_abs_ltr=0) — 라벨은 **시계열 `LTR_Total` 직접계산 필수** |
| **from-scratch** `b_p95` | PlacementAgent+useSurrogateReward, 300k, 주행 frac−66%·max−14%·p95 무승부 | |
| **refinement** `refine_p95` | RefinementAgent+surrogate훅, **155k(2배 빠름)·자립99.96%**, 주행 **p95−10.6%·max−16%** | ⭐ **종합 승**(견고지표·속도·안정) |
| **결론** | **refinement > from-scratch.** 보상라벨 **p95**(frac 직접최적화=**reward hacking**으로 실패, +135%) | |
| **부산물** | reward hacking 실증 · `CargoBedLoader` 기즈모 좌표버그 수정 · RefinementAgent에 SaveLayout+Visualizer+surrogate훅 추가 | |

> **다음 후보**: ①더 어려운 manifest(무거운·파이프·비대칭 — CogX 단순답 안 통하는 곳에서 RL 진가) ②멀티시드 refinement(국소최적 회피) ③좌표 원점 정리(bedAnchor=트레이중심 확인·CogY floorTop 1cm) ④룰 커리큘럼(`supportRatioMin` 인스펙터 0.5→0.7).
> ⚠️ **모델 호환**: `.onnx`는 학습 에이전트 규격(obs/action)에 묶임 — refine모델을 PlacementAgent로 못 돌림. 팀원 공유 시 `RefinementAgent.cs`+`LayoutRiskModel.cs`+`.onnx` 세트 필요.

---

## ⚡ 현재 진실 (검증됨 2026-07-08) — 여기만 봐도 됨

| 항목 | 현재 값 (코드/씬 확인) | 비고 |
|---|---|---|
| **격자** | **2cm, 11×31 = 341셀** | `PlacementAgent cols=11/rows=31` (1cm/1281은 **과거**) |
| **관측** | **359** (고정 manifest 시 350) | 높이맵341+CoG3+질량1+편차2+종류12 (1299는 **과거**) |
| **행동** | (종류, **341**, 회전2) | 고정 manifest면 종류 브랜치 축소 |
| **RLTraining 씬** | `useFixedManifest=1`(**B-004×8**)·`useGatedPool=0`·`binPackerHeuristic=0`·**`stepScale=0.05`** | 게이팅풀/데모 **미사용** |
| **보상** | `R=0.50·LE+0.40·CGS+0.10·SS` + 스텝shaping(0.05) + 무효페널티 + Option C | ⚠️ RLTraining은 **wLE=0/wCGS=1/wSS=0** 오버라이드 → **"CGS 중심 + 약한 밀집shaping"**. **"순수 CGS"는 부정확**(stepScale 0.05) |
| **v1 결과** `b001_cgs` | **300,005 steps, reward 1.553** (수렴) | `b001_cgs_nostep`은 **10,561 steps/0.623 = 미완, 결론 불가** |
| **v2** `boxpack003_refine_mask` | 빈패커(Dense) 배치서 시작 → 재배치, **ΔFinal** 보상 | action=(16,341,2), 순수 PPO |
| **⭐ 핵심 발견** | **정적 CGS ≠ 동적 안전** (중앙·저CoG 배치가 코너링서 오히려 위험 = **앞축 언로딩** 실측) | 정적 proxy 한계 실증 → 예측기 필요성 근거 |
| **다음** | 배치→동적위험을 (a) **예측기**로 학습 or (b) 후보 적으면 **직접 주행(best-of-N)**. CGS 하드튜닝은 중단 | |

> ⚠️ **알려진 버그**: `PlacementAgent.SaveLayout()`이 **파이프 회전(90,0,0)을 저장 못 함** — 박스 전용 현재 실험엔 무영향이나 파이프 레이아웃 저장 전 수정 필요. `augment_layout.py`(배치피처 CSV 생성기)는 아직 repo 밖(스크래치패드).

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
| **S3 에이전트** | ✅ | `PlacementAgent.cs` — **관측 359, 행동 (종류·셀341·회전2)** (2cm 격자), 마스킹, 페널티 · *(옛 1299/1281은 1cm 시절, §3 역사)* |
| **S4 학습(1차)** | ✅ | PPO 씬 끝까지 돎. Mean Reward −1.67→−0.36(미수렴). env `mlagents-x86` |
| 캘리브레이션 | ✅ | 로드셀 실측 vs 계산 CoG 오차 0.1~0.3cm(x·y 양호) |
| 위험 데이터셋 | ✅ | `generate_risk_cases.py` → `Cases/` **500개**(예측기④ 학습용, 하드룰 의도위반 허용). 옛 2000개는 archive 백업 |
| 격자 4cm→1cm | ⚠️ | RL·빈패커 전부 **1281셀** 통일. **BUT 이게 탐색벽의 근본 원인 → 2cm 완화 검토 중**(§3). 기존 `placement_v1`(4cm) onnx 비호환 |
| **빈패커 Phase1** | ✅ | `BinPacker.cs`+`Runner`+`Visualizer`. Phase1 검증 20케이스 성공률 89%·평균보상 **0.698** |
| **빈패커 Phase2 코드** | ✅ | `Decide()`(다음 한 수)+`PlacementAgent.Heuristic` 연결+rl_config `behavioral_cloning`+진단(`DiagnoseUnplaced`) |
| **A안 게이팅 풀** | ✅ | `BinPackerVisualizer.GenerateGatedManifestPool`→`gated_manifests.txt`(5000) + `PlacementAgent.useGatedPool`. 리셋당 Pack 0회·데모 정합 |
| **분포 진단 도구** | ✅ | `BinPackerVisualizer.DiagnoseManifestDistribution` — ManifestRealistic 통과율·교사완주율 p·에이전트 천장 추정 출력 |

---

## 3. 🗄 [과거·역사 — `3dbpp` 브랜치] 1281셀 "탐색 벽" 사투 (격자 완화 + GAIL + Option C)

> ⚠️ **이 절은 현재 상태가 아닙니다.** 1cm/1281셀 시절의 탐색벽 극복 이력이며, 현재는 2cm/341셀 + Option C로 §3b에서 이어집니다. **평가/발표 시 "현재"로 인용 금지.** 맥락 참고용으로만 보존.

**당시 목표**: 예측기가 오기 전 **임시 정적 보상**으로 RL을 *학습 가능한* 상태까지. 당시 핵심 장애 = **행동공간 과대(1281셀)**.

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
- 효과: 대부분 유효 완성배치(≈+0.6~+0.9)로 보상이 채워져 std>0 → 정책 붕괴를 **크게 완화**. ⚠️ 단 **"항상 완주·붕괴 불가·최악 +0.7"은 과장**: **교사(빈패커)도 못 놓는 경우**엔 partial final(= final − 남은화물×unplacedPenalty)로 **감점 종료**하고, 무효 스텝 페널티도 **누적**됨 ([PlacementAgent.cs:327](../Assets/Scripts/Static/PlacementAgent.cs:327)). 행동공간·데모·BC/GAIL 불변(재기록 불필요).
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
- 🔵 **v1 + CGS 중심 보상** (균등배치 실험): RLTraining 씬 rewardConfig **wLE=0·wCGS=1·wSS=0**(LE 밀집 항 제거). ⚠️ 단 **`stepScale=0.05`라 "순수 CGS"가 아님** — 스텝 밀집 shaping이 섞임. run `b001_cgs` = **최종 300,005 steps, reward 1.553**(수렴, 옛 "35k/1.325"는 스테일).
  - `b001_cgs_nostep`(stepScale=0 시도) = **10,561 steps / 0.623 = 학습 미완, "순수 CGS 재실험" 결론으로 인용 불가.**
  - **판정은 곡선이 아니라 배치 모양**: Play 중 PlacementVisualizer로 Game 뷰 확인. 낮고 고르게=성공 / 중앙 탑=CGS 해킹.
- ⭐ **위험 예측기 도착** (2026-07-07, playground 머지 000432d): `Assets/Scripts/Modeling/RiskModel.cs`·`RiskDisplay.cs`·`Assets/Resources/risk_model_treedata.json`. "예측기 오면 보상 교체" 전제가 현실이 됨.

### ⭐ 사이클 1 결과 (2026-07-08) — 정적 CGS ≠ 동적 안전 실증
- 파이프라인 한 바퀴(빈패킹→RL→배치→주행→위험) **완주**. RL/빈패커 배치를 **동일 조건 주행 비교**.
- **역설 발견**: 무게중심이 **중앙·낮은 "안전" 배치**(boxpack001_best, max|LTR| **0.936**)가, 뒤로 쏠린 빈패커(0.646)보다 **코너링서 더 위험**. 원인 = **앞뒤 균형 배치가 급선회 시 앞축을 언로딩**(FL 하중 ~1000N, 앞축 LTR 0.87). (⚠️ 1코스·소수배치라 일반화는 이름, 속도 2km/h 오염 있음)
- **의미**: 정적 CGS 보상의 "중앙=안전" 가정이 동적에서 깨짐 = **정적 proxy로는 안전 대변 불가 → 예측기/실측 필요**를 실증. (사이클 1의 진짜 산출물)

**▶ 다음 방향 (사이클 2)** — 큰 그림은 파이프라인 재설계 논의 참조:
1. **위험을 무엇으로 볼지 확정**: `max|LTR|`(또는 p95) 를 그라운드트루스로.
2. **최적화 방법 결정**: 후보 적으면 **best-of-N + 직접 주행**(예측기 불필요, 정확). 일반 정책 필요 시에만 **예측기**(배치→위험) 학습해 RL 보상 교체.
3. 예측기 가면: 기존 **421케이스 주행 데이터**(9개 timeseries CSV)로 **v0 예측기** → **역설쌍(best>001) 통과** 검증 → 통과 시 RL 보상 교체 → DAgger 루프.
4. **CGS 하드튜닝 중단** (예측기가 "좋음"을 재정의). manifest B-004 통일로 binpacker vs RL 공정 비교는 유효.
5. `SaveLayout` 파이프 회전 버그 수정(파이프 레이아웃 저장 전).

### 🎓 RL 일반화 커리큘럼 — "1케이스 암기 → 일반 배치"로 확장하는 법

> 현재 `b001_cgs`는 **단일 고정 manifest(B-004×8)**로 학습 = 그 한 구성만 외운 모델(버그 아님, 의도된 커리큘럼 0단계). 아무 화물 세트나 배치하는 **일반 정책**으로 키우려면 아래처럼 **한 번에 한 축씩** 난이도를 올린다. 한 번에 점프하면 유효칸이 희박해져 정책 붕괴(= 1281셀 교훈).
> ⭐ **아키텍처는 이미 일반화 가능**: 관측에 "남은 화물 구성(종류별 remaining 12)"이 들어있어 정책이 화물 세트로 조건화됨. 못 하는 이유는 구조가 아니라 **1케이스로만 먹여서**. → 확장 = **학습에 먹이는 manifest 다양성**의 문제. 신경망 구조는 안 바뀌고, 바뀌는 건 "무엇에 주목(화물구성)"·"얼마나 조심(룰 회피)".

**두 축 — 동시에 올리지 말 것 (한 축 수렴 후 다음)**
- **축A. manifest 다양성**: 1케이스 → 소수 순환 → 수량 가변 → 다종 랜덤 → 게이팅풀 전체
- **축B. 룰 엄격도**: 교사 의존 + 완화룰(예 지지율 50%) → 교사 의존↓ + 풀룰(지지율 70%)

| 단계(축A) | 변화 | 예 | 통과 기준 |
|---|---|---|---|
| 0 (완료) | 단일 고정 | B-004×8 | ✅ 수렴 1.55·붕괴 없음 |
| 1 | 소수 고정 순환 | 3~5개 배치를 에피소드마다 번갈아 | 여러 배치를 std 붕괴 없이 유지 |
| 2 | 수량 가변 | B-004 × {6,7,8,9,10} | 개수 달라도 유효 배치 |
| 3 | 다종 랜덤 | B-001·B-004·SYN 섞어 랜덤 | 조성 바뀌어도 대응 |
| 4 | 게이팅풀 전체 | `gated_manifests.txt` 5000 | ⭐ 일반 정책 완성 |
| 5 | 형상 확장 | 파이프·코일 추가 | ⚠️ **액션공간 확장 선행 필요**(아래) |

**메커니즘(코드)**: `useFixedManifest`(0단계) → `useGatedPool`(3~4단계) 토글 — 둘 다 이미 있음. 쉬운 것부터: `DiagnoseManifestDistribution`이 측정하는 교사 완주율 `p` 높은 manifest부터 시작해 점차 어려운 걸 섞음. 단계 오를수록 무효 배치↑ → **Option C(교사 대타) + BC/GAIL**로 각 단계를 받쳐 붕괴 방지.

**⭐ PPO 시작 단계에서 룰을 어떻게 넣나 — 룰은 두 종류, 넣는 곳이 다름**
- **① Feasibility(위반=무효)**: 겹침 H3·경계 H2·높이 H13·지지율 H8·과적 H1 → **구조로 강제**(보상 아님). (a) 값싼 것만 **액션 마스킹**(꽉 찬 셀·트레이 밖·소진 종류) — 겹침은 화물×셀×회전 조합의존이라 마스킹 불가. (b) 나머지는 **`IsValid` 검사 → 무효면 되돌림+소액 페널티**(v2) 또는 **교사 대타 Option C**(v1). 
- **② Quality(좋고 나쁨의 정도)**: CoG 중앙·낮게·밀집·무거운 것 아래 → **보상(CGS/LE/SS)**. Feasibility는 보상에 넣지 말 것(신호 지저분).
- **시작 세팅**: `Option C ON`(무효 시 fail-out −1.5 금지 → 교사가 완주 보장 = 보상 항상 유의미, 이게 +1.13/1.55를 만든 핵심) + 값싼 마스킹 + IsValid 되돌림 + **빡센 룰(지지율 H8) 초반 50%로 완화** → 유효율(ValidMoveRate) 오르면 70%로 조임. 겹침·경계·높이는 물리라 유지.

**정책이 어떻게 변하나**
- **0→1(다양화)**: 지금은 1케이스라 obs의 "남은 화물 구성"을 사실상 무시. manifest가 바뀌기 시작하면 그 피처를 읽어 "구성→배치"를 학습 = 일반화 출발점.
- **축B 조이면**: 무효 영역을 피하도록 보수적으로 변함 → 교사 대타 호출↓ → 자립. `Refine/ValidMoveRate`로 확인.
- **검증**: 일부 manifest로 학습 → **held-out manifest**(학습에 없던 것)로 돌려 유효·좋은 배치면 일반화 성공(예측기 GroupKFold와 같은 논리).

**⚠️ 두 가지 선행 조건**
- **파이프/코일(5단계)은 액션공간 확장 먼저**: 현재 회전 브랜치 2개(yaw만)라 파이프 눕힘(pitch 90) 표현 불가 = `SaveLayout` 파이프 회전 버그와 같은 뿌리. 박스만으로 4단계까지 가능하나 형상 확장 전 **회전 브랜치 확장 + SaveLayout 수정** 필요.
- **보상과 독립 축**: 이 manifest 다양화(일반화)는 보상이 CGS든 예측기든 무관하게 진행 → **일반화 커리큘럼과 "정적→예측기 보상 교체"는 동시 진행 가능.**
- ▶ **현실적 착수점**: 0단계(완료) → **1단계(소수 manifest 순환)**부터.

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
| 학습 설정 | **현재 사용 `Docs/rl_config_box.yaml`**(v1 단일케이스 순수 PPO) · `rl_config_refine.yaml`(v2, 보류) · `rl_config.yaml`(옛 게이팅+GAIL, 미사용) |
| demo(BC 교사) | `Assets/Demonstrations/PlacementAgentDe.demo` (1281셀=1cm 시절, +0.867) ⚠️ **현재 격자 2cm(341셀)와 비호환. 현재 v1(boxpack)은 순수 PPO라 데모 미사용** |
| A안 게이팅 풀 | `Assets/Data/gated_manifests.txt` (5000, type id 콤마구분) — 현재 v1은 useGatedPool=0이라 미사용 |
| 학습 결과 | `results/<run-id>/` — **최신**: `boxpack001_ppo`(v1 baseline +1.13) · `b001_cgs`(CGS단독) · `b001_cgs_nostep`(stepScale=0) · `boxpack002_refine`(v2 flat −0.4) · `boxpack003_refine_mask`. **옛**: `placement_v1`(4cm −0.36)·`v2_bc`·`v3_gated`·`v4_grid2_gail`·`v5_beta_gail`(정체/붕괴) |
| 동적 결과 | `Assets/Data/Results/results.csv` (235행 주행완료: max_abs_ltr·rollover·risk_grade·roll·pitch·cargo_shift…) |
| 위험 데이터셋 | `Assets/Data/Cases/` (**500**) · 빈패커 baseline `Assets/Data/Cases_binpack/` (**102**) · 빈패커 테스트 `Assets/Data/TestCases/`(binpack001~) |
| 씬 | `Assets/Scenes/RLTraining.unity`(학습·PlacementAgent v1) · `RefinementAgent.unity`(v2, 보류) · `StaticSceneRoot.unity`(정적) · `3D BPP.unity`(빈패커/진단/풀생성 = `BinPackerVisualizer` 단독) · `SampleScene.unity`(예측기 담당자) · `ConsoleTestScene.unity` |
