# 📍 마스터 플랜 · 핸드오프 (road_iso / CargoRiskSimulation)

> **이 파일부터 읽으세요.** 세션이 바뀌어도 이 한 장이면 "어디까지 왔고, 무엇을 왜 결정했고, 다음에 뭘 하는지"를 파악한다.
> **코드가 항상 최종 진실** (문서와 코드가 다르면 코드 우선 + 이 문서 갱신).
> 최종 갱신: **2026-07-12** · 브랜치 `model-test`.

## 🎯 최종 목표
**매니페스트(주문) → 가장 안전한 배치를 출력하는 배치기를 고른다.** 안전 = **실제 주행 p99 LTR 최저**. (순수 밀도 최대화 아님 — 안전이 주, 촘촘함은 조건.)

---

## Part A — 탐색한 방법 & 결정

| # | 실험 | 결과 | 결정 |
|---|---|---|---|
| A1 | 정적 CGS vs 동적 주행 | 중앙정렬(CGS↑)이 주행선 더 위험 = **정적≠동적** 실증 | 안전기준 = **주행 p95/p99 LTR** (CGS는 proxy) |
| A2 | 빈패커 Dense vs Stable | Dense 7쌍 중 6쌍 우세(비전복차=좌우균형 지배) | **Dense 기본 + best-of-two** |
| A3 | RL refine(surrogate) 단일 | 꽉 찬 배치서 FinalImprovement≈0 | 단일선 개선못함 → 일반화(커리큘럼) |
| A4 | RL from-scratch(Option C) | 빈패커 바닥(~0.32)서 만남 | 위치제어 바닥 ~0.32 |
| A5 | 보상 옵션(p95/p99/composite/CGS) | p95/p99 최고, **p99 held-out R=0.98/MAE0.004** | **p99 surrogate(lgbm500)** 채택 |
| A6 | surrogate 모델(RF→**LightGBM v500 p99**) | 변환 pkl=onnx=json 오차0, 주행검증 통과 | 두 RL 씬 이식·정합(speed60) |
| A7 | learn-to-pack(order-only) | 학습 성공(0.42→0.37), 단일 캡 **0.372**(Dense=Stable), 위치제어(0.318)보다 열위 | **후보 유지** — 일반화 미검증(공통평가서 판정) |
| A8 | GRASP 멀티시드 | 단일 매니페스트선 det 대비 무개선(구석편향) | 다양 매니페스트서 재확인 → 후보 유지 |
| A9 | BC/GAIL 워밍스타트(옛 3dbpp) | 랜덤분포 −1.5 붕괴 | **Option C + 매니페스트 정화**로 돌파(교훈: 입력분포가 뿌리) |
| A10 | 하이퍼파라미터(LR3e-2/β1e-4, parameterModify) | s0 즉시 붕괴(Std→0) | **LR3e-4/β1e-2 복귀**(검증된 안정값) |
| A11 | **SA 직접 최적화** (surrogate 목적함수, `sa_optimize.py`) | 엄격유효(0.70) 배치로 **예측 p99를 빈패커보다 −0.02~0.08 낮춤** | "빈패커 비최적" 시사 → 단 아래 실측서 반전 |
| A12 | **A11 주행 검증 (실측 head-to-head)** | gaming 없음(SA실측≈예측). **007: 빈패커0.504→SA0.395(−0.11), 016: 0.323→0.271(−0.05), 002: 무승부** | ⭐ **빈패커 비최적 실측 확정.** SA가 여지 있는 배치서 실제로 더 안전. RL 실패는 "빈패커 최적"이 아니라 "RL이 약한 최적화기"였음 |

## Part B — Environment 확장 이력

**B1. 규칙(RuleChecker):** H1 과적≤7kg · H2 경계 · H3 겹침 · H4~H6 파이프 · H8 지지율≥70% · H13 높이≤27cm

**B2. 그리드:** 4cm(96) → 1cm(1281, 탐색벽 붕괴원인) → **2cm(341, 표준)**. 현재 씬별: 3D BPP=1cm(21×61) · RLTraining/Refinement=2cm(11×31). 진단(예정): learn-to-pack만 2/1/0.5 비교.

**B3. 액션공간:**
| 에이전트 | 액션 |
|---|---|
| PlacementAgent(기본) | (종류, 셀341, 회전2) — 위치제어 |
| PlacementAgent sequenceMode(learn-to-pack) | (종류) — 순서만, 위치=빈패커 |
| RefinementAgent | (아이템, 셀341, 회전2, **모드2, 상대아이템**) — relocate+**swap** |

**B4. 환경 장치(단계적 추가):** ManifestRealistic(질량≤7·파이프폭0.7) · 게이팅+풀5000 · **Option C**(무효→빈패커완주,붕괴방지) · **부양수정**(AllItemsValid 전화물 재검증) · **swap 액션** · **커리큘럼 스테이징**(currentStage·replay+어닐링·stage4 절차생성·QualityPassRate)

**B5. 관측/보상/물리:** 관측=높이맵+CoG3+질량1+편차2(+남은목록) · 보상=CGS(Final)↔surrogate 토글, 모델 JSON교체식 · massScale100 · **speed60** · **코너원점** · CoM x=0(현재 regime, 검증됨)

## Part C — 커리큘럼 단계 (s0→s4, RefinementAgent, 2cm 고정)

| 단계 | 케이스 | 축 | 상태 |
|---|---|---|---|
| s0 | SYN-05×8 | 워밍업 | ✅ 붕괴無·ValidMove1.0·FinalImp≈0(자명) |
| s1 | 5종 각각(에피소드마다 1종 번갈아) | 종류 | ✅ **FinalImp +0.28**(다양→개선 최대)·ValidMove0.80·RefineCase[0~5] |
| s2 | B-003 개수4→8 | 개수 | ✅ **FinalImp +0.09**·ValidMove0.93·전이시작−0.15·RefineCase[1~10] |
| s3 | 혼합 다종 4케이스 | 혼합 | ⏳ **⭐ 여지 있는데 개선 나오나 = 첫 진짜 게이트** |
| s4 | 절차적 랜덤(13종) | **일반화** | ⬜ 최종 일반화 정책 |
전이: 각 단계 `--initialize-from` 이전, 액션공간 20 고정(widen 불필요). 단계당 max_steps 300k, 수렴 시 Ctrl-C. **단계 지정은 run-id 아니라 인스펙터 `currentStage`.**
**관찰: 다양성↑ → 개선↑** (s0=0 < s2=+0.09 < s1=+0.28). 단일종류(s0~2)는 여지 작아 개선 작음이 정상. 혼합(s3~4)이 진짜 시험.

### 단계별 게이트 (매 단계 이걸로 진단·조정)
```
① 건강?  Std>0 · ValidMoveRate↑ · −0.5아님 · PlacedAll1.0
   NO → 조정(LR↓·β↑) 후 재실행, 진행 금지
② 수렴?  곡선 평평 (아니면 스텝 더 / 전이로 이어짐)
③ 개선이 그 단계 "여지"에 맞나?
   단일종류(s0~2): 작아도 OK → 진행
   혼합(s3~4): 여지 있는데 부진하면 → 조정
```
**조정 도구함:** 붕괴→LR↓/β↑ · 덜수렴→스텝↑ · 유효이동안늚→액션/마스킹 · 개선약함→scale↑·CGS블렌드·시작모드(Dense/랜덤) · 망각→replay↑

## Part D — 앞으로: 후보 → 공통평가 → 결정

**후보(배치기):** C1 빈패커 best-of · C2 GRASP · C3 refine 커리큘럼 · C4 learn-to-pack · **⭐C5 빈패커→SA surrogate 직접최적화(`sa_optimize.py`)**
- **C5가 선두**: 빈패커에서 시작해 개선만 → **빈패커 지배(dominate)**. 실측 확정(007 −0.11·016 −0.05·002 동률), gaming 없음, RL 불필요. C3(refine)·C4(learn-to-pack)는 빈패커 못 넘어 사실상 탈락.

**⭐ 공통평가(반드시 같은 걸로):**
```
① 테스트 매니페스트 세트 고정(다양 랜덤 10~20, held-out)
② C1~C4 각자 그 주문들 배치 생성
③ surrogate 예측 p99 스크리닝 → ④ 상위 실제 주행 p99 → ⑤ 통일 비교
```
**결정:** 평균 주행 p99 최저 + 완주율 좋은 배치기. RL이 빈패커 못 이기면 **빈패커가 답**(열어둠). 최종 산출 = 그 배치기(모델 or 휴리스틱).

### 🎯 실행 로드맵 — "RL 결론 + SA급 성능" (증류 경로)
> 목표: 결론을 RL로 내되 SA급 품질. 방법 = **SA(선생)→RL(학생) 증류.** RL 약점(노이즈 보상 맨땅학습)을 SA 모범답안이 메움.
```
0. (선택) GRASP 몇 개 → 빈패커<GRASP<SA 중간지점 확인 (Unity RunGraspBatch)
1. SA 데이터셋: rand_Dense 다수에 sa_optimize → (매니페스트→SA최적배치) 수집 + 견고화(빈패커 대비 개선분포)
2. 상위 몇 개 실제 주행 → SA가 빈패커 넘는 폭 실측 확인 (007 −0.11·016 −0.05 재확인/확장)
3. 증류 학습: 그 SA배치들을 BC로 정책에 모방 + surrogate RL 미세조정 (learn-to-pack/placement)
4. 학습된 RL 정책 배치 → 주행 검증 → SA·빈패커와 실측 비교 → "SA급 RL 정책" 확정
```
> 상한 정직: 증류 RL 천장 = SA (매칭 가능, 초월은 어려움). 그래도 "SA급 즉시추론 RL 정책"이면 결론으로 충분.

### 결정 규칙 (누가/어떻게 고르나)
1. **Dense vs Stable** → 전역으로 하나 안 고름. **매 주문마다 둘 다 팩 → 예측 p99 낮은 쪽 채택(best-of-two)**. (Dense 대체로 우세지만 가끔 Stable 승 → best-of가 안전.)
   ⚠️ **이건 배포(최종) 규칙**이지 refine 학습에 든 게 아님. **refine 학습은 현재 시작배치 Stable 고정**(startPackMode=0). → 최종 C3 배치 생성 시엔 **best-of-two로 시작 후 refine** (학습된 재배치 스킬은 시작모드 무관 전이). refine를 더 견고히 하려면 시작모드 per-episode 랜덤화 옵션(미구현, 필요시 추가).
2. **refine(C3) vs learn-to-pack(C4) vs 빈패커(C1/C2)** → **공통 테스트셋(랜덤 10~20)으로 각자 배치 생성 → 예측 스크리닝 → 상위 실제 주행 p99 → 평균 최저+완주율 승.** 예측 아닌 **주행**이 최종 심판.
3. **커리큘럼 하이퍼파라미터** → **단계별 곡선으로 판정**: `Std>0`(붕괴無) + reward/FinalImprovement 상승 = OK / `Std→0` 붕괴 = LR↓. s0에서 LR 확정 후 s1~s4 동일 적용. `successScoreThreshold`는 s0 수렴 Score 근처로 보정.

## Part E — 진행 상태

| | 상태 |
|---|---|
| surrogate 검증·이식(lgbm500 p99) | ✅ |
| 빈패커/GRASP/learn-to-pack 구현 | ✅ |
| 커리큘럼 이식(병합·config·씬) | ✅ |
| 하이퍼파라미터 재튜닝(LR3e-4) | ✅ |
| **C3 커리큘럼 학습** | ⏳ s0✅ s1✅(+0.28) s2✅(+0.09) → **s3(⭐관전)** → s4 |
| 선택진단: learn-to-pack 그리드 2/1/0.5 | ⬜ 우선순위 낮음 |
| **공통평가 → 배치기 결정** | ⬜ 마지막 |

## Part F — 선택 진단 (deliverable 아님)
learn-to-pack 그리드 2/1/0.5 → "0.372 캡이 격자 탓인가 순서제약 탓인가". 커리큘럼과 독립·병행 가능, 최종 후보엔 영향 적음.

---

## 핵심 파일
- `BinPacker.cs`(Pack/Decide/PackGrasp/PlaceBest, Dense·Stable) · `BinPackerRunner.cs`(Run/RunRandomBatch/RunGraspBatch)
- `PlacementAgent.cs`(from-scratch + sequenceMode=learn-to-pack) · `RefinementAgent.cs`(refine+swap+부양수정+**커리큘럼 s0~s4**)
- `LayoutRiskModel.cs`(surrogate 트리JSON 로더) · `Resources/layout_risk_lgbm500.json`(p99, 현재 모델)
- config: `rl_config_seq.yaml`(learn-to-pack) · `rl_config_refine.yaml`(커리큘럼, LR3e-4/β1e-2/batch512/300k)
- 데이터: `RefineCases/`(s0~s3) · `gated_manifests.txt`(5000) · `widen_checkpoint.py`
- Python: `predict_p95.py`·`measure_p95.py`·`validate_surrogate.py`·`compare_modes.py`·`grasp_pick.py`·`convert_lgbm.py`

## 규약/주의
- 좌표=코너원점(x∈[0,0.21], z∈[0,0.61], 양수). 옛 중심원점(x음수) 배치는 surrogate OOD → 신뢰불가.
- surrogate=p99 예측(0.3~0.6). 보상=−예측×scale(refine scale10). 리워드 −0.5 고정+Std0 = 붕괴 신호(LR↓).
- 커리큘럼(RefinementAgent) 그리드는 2cm 고정(셀=RL액션이라 0.5cm면 탐색붕괴). 그리드 실험은 learn-to-pack에서만.
- 학습 크래시(UnityTimeOut)=Mac App Nap → `caffeinate -dimsu`로 감쌀 것.
- ⚠️ **커리큘럼 단계는 run-id가 아니라 인스펙터 `currentStage`가 정한다.** run-id는 이름표일 뿐. 단계 올릴 때 반드시 인스펙터 currentStage를 바꿀 것(안 바꾸면 이전 단계 반복). 확인: tfevents RefineCase 인덱스가 그 단계 케이스들로 떠야 정상.
- **병렬 실행:** Unity 에디터+mlagents-learn은 1:1 → 동시 학습 1개만. 진짜 동시(다른 실험 2개)는 **스탠드얼론 빌드** 필요: `--env=<빌드> --base-port=5005/5006` 로 프로세스 분리. 단일 실험 속도↑는 `--num-envs N`(빌드 필요). 지금 규모(단계당 20~25분)면 순차로 충분.
- **learn-to-pack 결론:** order-only 캡 = boxpack001서 0.372(Dense=Stable 디코더, 수렴). 위치제어(0.318)보다 열위. 단일 매니페스트 결론이며 **일반화는 미검증** → C4 후보로 공통평가서 재판정.
