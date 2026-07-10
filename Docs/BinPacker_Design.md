# 3D 빈패커 설계 (BinPacker) — baseline + 워밍스타트

> ⚠️ **일부 스테일 (2026-07-08)**: 본문 §2/§3/§8의 **1cm·1281셀·"RL과 완전 정합"** 표현은 **과거**입니다. 현재 코드 기본은 **2cm·11×31=341셀**(`BinPacker.cs:34`). **현재 진실은 `STATUS.md` 상단 표 참조.**

> 목적: RL(PlacementAgent)의 **비교 baseline** + **워밍스타트(BC) 교사**.
> 위험 데이터셋은 `generate_risk_cases.py`가 담당하므로, 빈패커는 **stable(규칙 지키며 잘 싣기)** 만 담당.
> 핵심: **RL 환경(RuleChecker·RewardCalculator·격자 드롭)을 그대로 재사용**해, 신경망 대신 그리디 휴리스틱으로 배치.
> 작성 2026-07-06. feature/3d-binpacker 브랜치.

---

## 1. 한 문장 정의

매 스텝 **RuleChecker로 유효한 후보 배치들 중 RewardCalculator 점수가 최대인 곳**에 화물을 놓는 **그리디 격자 패커**.
→ RL이 배우려는 목표(안정성·중앙·밀집)를 그리디로 최대화 = 공정한 baseline이자 좋은 BC 교사.

## 2. 좌표·격자 (RL과 동일)

- 좌표: x=좌우, y=높이, z=주행/길이, 원점=트레이 중심 (RuleChecker와 동일).
- 트레이: 안쪽 0.21(x) × 0.61(z) × 0.27(y). 바닥 top y=0.01.
- 격자: cols=21(x) × rows=61(z) = **1281셀 (1cm)** (2026-07-06 RL과 함께 4cm→1cm. RL과 동일해야 BC 정합).

## 3. 알고리즘 (`Pack(manifest)`)

```
입력: manifest(실을 CargoType 리스트), RuleConfig, RewardConfig
출력: List<Placement> (놓은 것), unplaced(못 놓은 것)

1. 정렬: manifest를 질량 내림차순(무거운 것 먼저). 파이프는 무거워 자연히 앞쪽.
2. placed = []  (Placement 리스트)
   placedItems = []  (RuleChecker.PlacedItem 캐시 — 규칙/보상 입력용)
3. 각 type(정렬 순서)마다:
   bestScore = -∞, best = none
   rotN = (파이프면 1[rot0만], 아니면 2[0/90])
   for cell in 0..1280:
     for rot in 0..rotN-1:
       cand = MakeCandidate(placedItems, type, cell, rot)
              ├ (x,z) = CellCenter(cell)
              ├ half  = rot==1 ? (sz,sy,sx)/2 : (sx,sy,sz)/2   # yaw90은 x↔z 스왑
              ├ restBottom = 그 셀 기둥의 현재 꼭대기(겹치는 placed의 max top, 없으면 바닥)
              └ center = (x, restBottom+half.y, z)
       item = PlacedItem(type, center, half)
       if not RuleChecker.IsValid(placedItems, item): continue   # 규칙 위반 후보 버림
       s = Score(placedItems, item)
       if s > bestScore: bestScore=s, best=cand
   if best 있음: placed.add(best); placedItems.add(item(best))
   else: unplaced.add(type)   # 이 화물은 못 놓음(스킵)
4. return placed
```

### 3.1 후보 생성 (Extreme-Point on grid)
- **1281셀 × 회전** 이 후보 집합. 각 셀 중심에 화물을 **낙하 안착**(RestBottom: 바닥 또는 그 기둥을 덮는 기존 화물의 최상단).
- → 이것이 격자 위의 Extreme Point(놓을 수 있는 표면점) 역할. 연속 EP보다 단순하지만 RL과 완전 정합.
- 파이프는 rot=0만(길이 z축, H4). rot=1은 RuleChecker가 어차피 걸러냄(중복 계산 방지로 rot0만 시도).

### 3.2 유효성 필터 = RuleChecker (재사용)
- `IsValid(placedItems, 후보)` — H1~H13 그대로. 과적·겹침·경계·지지율70%·파이프규칙·높이27 위반 후보 제거.
- → 빈패커는 **항상 규칙 준수** 배치만 만듦 (위험 배치는 Python 생성기 몫).

### 3.3 스코어 = RewardCalculator (재사용)
```
Score(placed, 후보) = RewardCalculator.Final(placed + 후보).total
                    + 미세 동점보정(-0.001·(center.y + |x| + |z|))   # 동점이면 낮고 중앙 선호
```
- RL이 최적화하는 그 보상을 **매 스텝 그리디 최대화**. ⚠️2026-07-10 재설계: Final = **wCGS·CGS + wSS·SS**(LE 제외, CGS 4항·SS 2항). Stable 스코어도 이 새 Final 사용.
- **PackMode (2026-07-06 추가)**: `Stable`(기본, 위 스코어 — baseline·BC 교사용) / `Dense`(**고전 3D 빈패킹** — 스코어=LE만 + 뒤-좌-아래 구석 선호(DBL 유사), 정렬=부피 내림차순). Dense도 규칙은 그대로 적용되므로 **H1(7kg)이 공간보다 먼저 걸리는 건 동일** — 목업 화물이 무거워서(자갈·무게추) 부피로 꽉 채우기 전에 무게가 참.

### 3.4 복잡도
- 화물당 후보 ≤ 1281×2 = 2562개, 각 후보마다 IsValid + Final(O(n²)). manifest ~12개면 케이스당 ~3만 평가 — 오프라인/에디터 도구론 여전히 빠름(수십 ms).

## 4. 출력

- `Placement{type, center, halfSize, rot, euler}` → **CargoLayoutFile JSON**:
  - `localPos = center` (트레이 로컬), `localEuler` = 박스 rot0(0,0,0)/rot90(0,90,0), 파이프(90,0,0), 코일/포대(0,0,0)/(0,90,0), `secured=true`.
  - `bed = {0.21, 0.61, 0.06}`.
- 저장 폴더: **`Assets/Data/Cases_binpack/`** (위험 `Cases/`와 분리, 덮어쓰기 없음).

## 5. 파일

| 파일 | 역할 |
|---|---|
| `Assets/Scripts/Static/BinPacker.cs` | 순수 C# 클래스. `Pack(manifest)` / `Placement` 구조체 / CellCenter·RestBottom·Score |
| `Assets/Scripts/Static/BinPackerRunner.cs` | MonoBehaviour(에디터). 랜덤 manifest N개 → Pack → JSON 저장 + 배치성공률·평균보상 로그 |
| `Assets/Scripts/Static/BinPackerVisualizer.cs` | 표시 전용 시각화. 빈 씬에 빈 GO+컴포넌트 → Play → 배치를 트레이·격자·CoG·실물모양으로 표시. 우클릭 Repack |

## 6. 단계

- **Phase 1 (지금)**: `BinPacker.Pack` + `BinPackerRunner`(JSON baseline 저장·통계). → baseline 확보.
- **Phase 2 (2026-07-06 구현)**: `BinPacker.Decide()`(다음 한 수, Pack과 동일 정책) + `PlacementAgent.Heuristic` 연결(`binPackerHeuristic` 토글, 폴백=랜덤) + rl_config `behavioral_cloning`(demo_path=`Assets/Demonstrations/PlacementAgentDe.demo`, strength 0.5, steps 200k) + hidden_units 512.
  - **진단 도구**: `DiagnoseUnplaced()` / `UnplacedReason`(종류별로 모든 후보가 어떤 규칙에 걸렸는지 최다 사유 집계). `Pack`·`Decide`가 못 놓을 때 "왜"를 로그로 남김 → 미적재 주범 규명(실측: H1 과적이 최다).
  - ✅ **demo 정합 완료**: manifest 게이팅+현실성 제약(`RL_Applied_Spec.md` §6) 적용 후 재기록 **Mean Reward +0.867** 합격. A안 게이팅 풀도 구현(`GenerateGatedManifestPool`).
  - ⚠️ **그 뒤 학습 정체 (미해결)**: BC 워밍스타트(v2_bc)·게이팅풀(v3_gated) 모두 **−1.5 정체(std 0, 매 에피소드 fail-out)**. 원인 = **1281셀 탐색 벽**(빈패커 문제 아님, RL 행동공간 문제). → 격자 완화+GAIL로 대응. 상세 = **STATUS §3 / WORKLOG 2026-07-06 / RL_Applied_Spec §10**.

## 7. 사용 (순수 BPP, `minimal-cycle-boxes` 브랜치 기준)

**입력 = manifest 지정** (`CargoManifest.cs`): 인스펙터 `manifest` 목록 — 각 줄이 **[카탈로그 화물 드롭다운] [개수]** (`Editor/ManifestEntryDrawer.cs` 로 id 타이핑 대신 목록 선택). 또는 `manifestCsv`(Assets/ 상대경로, 한 줄 "id,개수"). CSV에 화물 추가 후 드롭다운이 안 바뀌면 메뉴 **Tools ▸ BinPacker ▸ Refresh Cargo Dropdown**. 랜덤 생성·게이팅풀·분포진단은 이 브랜치에서 제거됨.

- **BinPackerVisualizer** (화면): `manifest` 채우고 `packMode=Dense` → Play/우클릭 **"Repack"** → 트레이·화물 표시 + 콘솔에 **부피점유율(%)**. 우클릭 **"Save Layout JSON"** → `Assets/Data/<outputSubdir>/<outputName>.json`.
- **BinPackerRunner** (화면 없이): 동일 manifest 입력 → 우클릭 **"Run BinPacker (Pack manifest)"** → JSON 1개 저장 + 부피점유율·Final 로그.
- **합성 화물 SYN-01~06** (`cargo_catalog.csv`): 저밀도 박스 → 7kg 한도 안에서 공간을 꽉 채우는 테스트용.

> 참고: 예전 랜덤 batch(`numCases`)·게이팅풀 생성·분포진단은 **RL 지원 기능**이라 `3dbpp` 브랜치에 있음(이 브랜치에선 제거).

## 8. 한계 (정직)

- 엄밀한 연속 EP가 아니라 **1cm 격자 그리디** → 1cm면 밀도 손실은 사실상 무시 가능. 대신 **RL과 완전 정합**(공정 baseline·BC). 목적상 이게 맞음.
- 그리디라 전역 최적은 아님(각 스텝 최선). baseline·워밍스타트엔 충분.
- ⚠️ **`Decide()`는 항상 질량 내림차순** → `Pack(Stable)`과는 정합하지만 **`Pack(Dense)`(부피 내림차순)와는 정렬 불일치.** 현재 BC 교사=Stable이라 무해하나, **Dense를 교사/비교로 쓰려면 Decide 정렬도 mode에 맞춰야 함**(외부 QA 리뷰 지적, 검증됨).
- ⚠️ **`BinPackerRunner`는 현실성 필터·게이팅 없이** raw 랜덤 manifest를 `Pack()`에 넣고 **못 실은 화물은 조용히 드롭한 채 baseline JSON 저장.** S5 동적검증에서 RL과 공정 비교하려면 **원 manifest+unplaced 함께 저장하거나 `unplaced.Count==0`(full-pack)만 baseline으로** 쓸 것. (RL 학습 쪽 동일 문제는 A안 게이팅 풀로 이미 해결됨.)
- ⚠️ **회전 자유도 = 도메인 제한** (박스/포대/코일 yaw 0/90, 파이프 rot0, **pitch 없음**). 수동 `CargoPlacer`의 T 눕히기(pitch)와 다름 — "순수 3D BPP"가 아니라 **도메인 제한 3D packing**임을 명시.
