# 정적 캘리브레이션 — 실제 목업 vs Unity CoG 비교

실제 목업 배치의 **측정 CoG**와, 그 배치 위치로 **계산한 CoG**를 비교해서
모델이 현실과 맞는지 검증하고 오차를 보정하는 도구.

## 좌표계 (실제 목업 규약)
- 원점 = **RL(뒤-좌) 모서리**, 단위 cm
- `x` = 좌우 (RL→RR, ~21cm) / `y` = 전후·주행 (RL→앞, ~62cm) / `z` = 높이 (~27cm)
- 화물 중심 = 각 축 `(start+end)/2`. CoG = 질량가중 중심(회전 무관).

## 입력 — `mockup_cases.csv` (단일 파일)
컬럼(헤더 이름으로 매칭, 순서 무관):
```
Case_ID, Base_ID, Type, Weight_kg, x, y, z, x_start, x_end, y_start, y_end, z_start, z_end, Cog_x, Cog_y, Cog_z
```
- **Case_ID**: 케이스 첫 행에만 기입, 이후 행은 비움(같은 케이스 연속).
- **Base_ID**: 화물 종류(B-007 등).
- **Weight_kg**: 그 화물 무게 → CoG 계산에 사용.
- **x/y/z**: 치수(참고용, 계산엔 start/end 사용).
- **x_start~z_end**: 목업 프레임 바운딩박스 → 중심 계산.
- **Cog_x/y/z**: **실측 CoG(케이스당 1개, 첫 행)**. 비우면 그 축 오차 제외.
  - loadcell은 수평(x,y)+무게만 측정 → 높이 `Cog_z`는 보통 비움.

## 사용법
1. `mockup_cases.csv`를 실측 데이터로 채운다.
2. 빈 GameObject에 **CalibrationRunner** 추가. (선택) `placer`+`visualizeCaseId`로 정적 씬 표시.
3. **Play** (또는 우클릭 → *Run Calibration Compare*).
4. 결과: `Assets/Data/Results/calibration_compare.csv`

## 출력 `calibration_compare.csv`
```
case_id, cargo_count, total_mass_kg,
unity_cog_x/y/z, real_cog_x/y/z, err_x/y/z    (err = unity − real)
```
- `Cog_*`(실측)를 비운 축은 `real`·`err`가 공란 → 그 축 비교 제외.
- 오차가 **계통적**(항상 한 방향) → 기준점/질량 보정. **랜덤** → 측정 노이즈.

## 주의
- **원점이 목업과 계산에서 같아야** 비교 유효(둘 다 RL 원점 cm).
- 정적 씬 표시는 목업 프레임을 트레이 중심으로 옮겨(가로 21·길이 62 가정) 보여줌.
