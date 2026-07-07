#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
다양한 현실적 배치 데이터셋 생성기 (동적 주행 → 위험 예측기 학습용). 2026-07-07.
스펙:
  - 화물 조합 다양: 박스만 / 경량박스만 / 박스+파이프 / 코일만 / 파이프만 / 전부혼합 (여러 비율).
  - 적재 부피 fill: 채울 수 있는 조합은 높게, 코일/파이프만은 태생적으로 낮음. (랜덤 비겹침 배치라 부피천장 ~48%)
  - CoG(x,y,z) 다양 (안전=낮음·중앙 ~ 위험=높음·편심) — 단 하드룰 준수(현실적):
      높이 ≤27cm · 총질량 ≤7kg · 겹침X · 받쳐짐(공중부양X) · 트레이 안(파이프만 뒤 오버행) · 파이프 눕힘.
  - 좌표: x=좌우(21) · y=높이(27) · z=주행/길이(61). 원점=중심, 바닥 top=0.01.
저장: CargoLayoutFile JSON + 배치메타(composition/cogMode/fill/cog{x,y,z}/totalMass/cargoCount/maxHeight).
      (Unity JsonUtility는 모르는 필드 무시 → CargoBedLoader 로드 정상)
사용: python3 Docs/generate_cases_diverse.py
"""
import csv, json, os, random

random.seed(7)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOG = os.path.join(ROOT, "Assets/Data/cargo_catalog.csv")

# ── 설정 ─────────────────────────────────────────────────
N_CASES = 500                                                # 본생성 500개
OUT_DIR = os.path.join(ROOT, "Assets/Data/Cases")            # 본생성: 진짜 Cases 폴더

FLOOR = 0.01
HALF_X = 0.105     # x 좌우 21cm / 2
HALF_Z = 0.305     # z 주행/길이 61cm / 2
H_LIMIT = 0.27     # y 높이 한도 27cm
PAYLOAD = 7.0
GRID = 0.01
PIPE_OVERHANG = 0.20                                          # 긴 파이프는 뒤로 오버행(현실)
TRAY_VOL = (2 * HALF_X) * (2 * HALF_Z) * H_LIMIT              # 부피 기준

# ── 카탈로그 ─────────────────────────────────────────────
def load_types():
    ts = []
    with open(CATALOG, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            ts.append(dict(id=row["id"], name=row["name"], shape=row["shape"],
                           sx=float(row["sizeX_cm"]) / 100, sy=float(row["sizeY_cm"]) / 100,
                           sz=float(row["sizeZ_cm"]) / 100, mass=float(row["massKg"])))
    return ts

def vol(t): return t["sx"] * t["sy"] * t["sz"]
def snap(v): return round(round(v / GRID) * GRID, 4)

def pick_light(pool):
    """가벼울수록 자주 뽑음 — 무게예산(7kg)을 무거운 걸로 날려 몇 개만 실리는 걸 방지."""
    w = [1.0 / (t["mass"] + 0.05) for t in pool]
    return random.choices(pool, weights=w, k=1)[0]

def total_mass(pl): return sum(p["t"]["mass"] for p in pl)
def total_vol(pl): return sum(vol(p["t"]) for p in pl)

def footprint(t, yaw):
    if t["shape"] == "Pipe":
        return t["sx"], t["sz"], t["sy"], (90.0, 0.0, 0.0)   # 파이프 눕힘: 길이=z(주행)
    if yaw:
        return t["sz"], t["sx"], t["sy"], (0.0, 90.0, 0.0)
    return t["sx"], t["sz"], t["sy"], (0.0, 0.0, 0.0)

def rects_overlap(a, b):
    return not (a[1] <= b[0] + 1e-9 or a[0] >= b[1] - 1e-9 or a[3] <= b[2] + 1e-9 or a[2] >= b[3] - 1e-9)

def col_top(pl, cx, cz):
    top = FLOOR
    for p in pl:
        rx0, rx1, rz0, rz1 = p["rect"]
        if rx0 - 1e-6 <= cx <= rx1 + 1e-6 and rz0 - 1e-6 <= cz <= rz1 + 1e-6:
            top = max(top, p["top"])
    return top

def try_floor(pl, t, region=None):
    """바닥에 유효 배치. region=(x_lo,x_hi,z_lo,z_hi) 쏠림 유도. 높이·트레이·겹침 준수. 실패 None."""
    yaws = [False, True] if t["shape"] in ("Box", "Sack") else [False]
    for _ in range(60):
        yaw = random.choice(yaws)
        fx, fz, h, euler = footprint(t, yaw)
        if fx > 2 * HALF_X:
            yaw = not yaw; fx, fz, h, euler = footprint(t, yaw)
            if fx > 2 * HALF_X: return None
        if h > H_LIMIT + 1e-6: return None
        hx, hz = fx / 2, fz / 2
        cx_lo, cx_hi = -HALF_X + hx, HALF_X - hx
        rear = HALF_Z + (PIPE_OVERHANG if t["shape"] == "Pipe" else 0.0)
        cz_lo, cz_hi = -rear + hz, HALF_Z - hz
        if region:
            cx_lo, cx_hi = max(cx_lo, region[0]), min(cx_hi, region[1])
            cz_lo, cz_hi = max(cz_lo, region[2]), min(cz_hi, region[3])
        if cx_lo > cx_hi or cz_lo > cz_hi: continue
        cx = min(cx_hi, max(cx_lo, snap(random.uniform(cx_lo, cx_hi))))
        cz = min(cz_hi, max(cz_lo, snap(random.uniform(cz_lo, cz_hi))))
        rect = (cx - hx, cx + hx, cz - hz, cz + hz)
        if any(rects_overlap(rect, p["rect"]) for p in pl): continue
        return dict(cx=cx, cy=round(FLOOR + h / 2, 4), cz=cz, euler=euler,
                    rect=rect, top=round(FLOOR + h, 4), t=t)
    return None

def try_stack(pl, t, region=None):
    """기존 박스/포대 위에 얹기 (받침면 안에 들어와야 = 받쳐짐, 높이 ≤27). region 우선."""
    if t["shape"] == "Pipe": return None                     # 파이프는 위에 안 얹음(현실)
    fx, fz, h, euler = footprint(t, False)
    if h > H_LIMIT + 1e-6: return None
    hx, hz = fx / 2, fz / 2
    bases = [p for p in pl if p["t"]["shape"] in ("Box", "Sack")]
    random.shuffle(bases)
    for base in bases:
        bx0, bx1, bz0, bz1 = base["rect"]
        if fx > (bx1 - bx0) + 1e-6 or fz > (bz1 - bz0) + 1e-6: continue   # 받침면 안
        cx = round((bx0 + bx1) / 2, 4); cz = round((bz0 + bz1) / 2, 4)
        if region and not (region[0] <= cx <= region[1] and region[2] <= cz <= region[3]): continue
        st = col_top(pl, cx, cz)
        if st + h > FLOOR + H_LIMIT + 1e-6: continue          # 높이 한도
        return dict(cx=cx, cy=round(st + h / 2, 4), cz=cz, euler=euler,
                    rect=(cx - hx, cx + hx, cz - hz, cz + hz), top=round(st + h, 4), t=t)
    return None

# ── 조합(구성) 선택 ──────────────────────────────────────
def pick_pool(types):
    boxes = [t for t in types if t["shape"] == "Box"]
    coils = [t for t in types if t["shape"] == "Coil"]
    pipes = [t for t in types if t["shape"] == "Pipe"]
    sacks = [t for t in types if t["shape"] == "Sack"]
    light = [t for t in boxes if t["mass"] <= 0.2] or boxes   # 부피 채우기용 경량 박스
    r = random.random()
    if r < 0.40: return "mixed",     boxes + coils + pipes + sacks, True   # 전부 섞임
    if r < 0.60: return "box_only",  boxes, True                           # 박스만
    if r < 0.75: return "box_light", light, True                          # 경량 박스만(꽉 채우기 쉬움)
    if r < 0.90: return "box_pipe",  boxes + pipes, True                  # 박스+파이프
    if r < 0.95: return "coil_only", coils, False                         # 코일만(fill 낮음=10%쪽)
    return               "pipe_only", pipes, False                        # 파이프만(fill 낮음)

# ── 한 케이스 생성 ───────────────────────────────────────
def build_case(types):
    pool_name, pool, fill_can = pick_pool(types)
    if not pool: pool_name, pool, fill_can = "box_only", [t for t in types if t["shape"] == "Box"], True

    # fill 목표: 채울 수 있는 조합의 90%는 >50%, 나머지(코일/파이프만 + 10%)는 낮게
    if fill_can and random.random() < 0.90:
        fill_target = random.uniform(0.50, 0.80)
    else:
        fill_target = random.uniform(0.20, 0.45)

    # CoG 모드 (현실 유효 범위 내에서 무게 위치만 다양화)
    cog_mode = random.choice(["low_center", "low_center", "high", "ecc_L", "ecc_R", "front_back"])
    region = None
    if cog_mode == "ecc_L":  region = (-HALF_X, -HALF_X * 0.2, -HALF_Z, HALF_Z)
    elif cog_mode == "ecc_R": region = (HALF_X * 0.2, HALF_X, -HALF_Z, HALF_Z)
    elif cog_mode == "front_back":
        region = (-HALF_X, HALF_X, HALF_Z * 0.2, HALF_Z) if random.random() < 0.5 \
            else (-HALF_X, HALF_X, -HALF_Z, -HALF_Z * 0.2)

    placed = []
    fails = 0
    # 1) fill 목표까지 채우기 (무거운 것부터 바닥 → 낮은 CoG 기본. 바닥 차면 위로)
    while fails < 50 and total_vol(placed) / TRAY_VOL < fill_target and len(placed) < 60:
        t = pick_light(pool)
        if total_mass(placed) + t["mass"] > PAYLOAD:                     # 과적 방지 → 더 가벼운 것
            lighter = [x for x in pool if total_mass(placed) + x["mass"] <= PAYLOAD]
            if not lighter: break
            t = random.choice(lighter)
        p = try_floor(placed, t, region=region) or try_stack(placed, t, region=region)
        if p and total_mass(placed) + t["mass"] <= PAYLOAD:
            placed.append(p); fails = 0
        else:
            fails += 1

    # 2) high 모드: 위로 더 쌓아 CoG 높임 (≤27cm 유지)
    if cog_mode == "high":
        for _ in range(25):
            if total_mass(placed) >= PAYLOAD: break
            t = pick_light(pool)
            s = try_stack(placed, t)
            if s and total_mass(placed) + t["mass"] <= PAYLOAD: placed.append(s)

    return placed, pool_name, cog_mode

# ── 통계 + JSON ──────────────────────────────────────────
def stats_of(pl):
    m = total_mass(pl)
    if m < 1e-9: return dict(x=0, y=0, z=0), 0.0, 0.0, 0.0
    cx = sum(p["t"]["mass"] * p["cx"] for p in pl) / m
    cy = sum(p["t"]["mass"] * p["cy"] for p in pl) / m
    cz = sum(p["t"]["mass"] * p["cz"] for p in pl) / m
    fill = total_vol(pl) / TRAY_VOL
    max_h = (max((p["top"] for p in pl), default=FLOOR) - FLOOR)
    return dict(x=round(cx, 4), y=round(cy, 4), z=round(cz, 4)), round(fill, 3), round(m, 3), round(max_h, 4)

def to_json(pl, pool_name, cog_mode):
    cog, fill, mass, max_h = stats_of(pl)
    cargo = [dict(type=p["t"]["name"],
                  localPos=dict(x=p["cx"], y=p["cy"], z=p["cz"]),
                  localEuler=dict(x=p["euler"][0], y=p["euler"][1], z=p["euler"][2]),
                  secured=True) for p in pl]
    return dict(version=1, composition=pool_name, cogMode=cog_mode,
                fill=fill, totalMass=mass, cargoCount=len(pl), maxHeight=max_h, cog=cog,
                bed=dict(widthX=0.21, lengthZ=0.61, wallHeight=0.06), cargo=cargo)

def main():
    types = load_types()
    os.makedirs(OUT_DIR, exist_ok=True)
    for f in os.listdir(OUT_DIR):
        if f.startswith("case") and f.endswith(".json"): os.remove(os.path.join(OUT_DIR, f))

    hi_fill = 0
    for i in range(1, N_CASES + 1):
        placed, pool_name, cog_mode = build_case(types)
        if not placed:                                       # 안전장치
            p = try_floor([], random.choice(types))
            if p: placed = [p]
        data = to_json(placed, pool_name, cog_mode)
        with open(os.path.join(OUT_DIR, f"case{i:03d}.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        if data["fill"] > 0.5: hi_fill += 1

    print(f"생성 완료: {N_CASES}개 → {OUT_DIR}")
    print(f"fill>50% 비율: {hi_fill}/{N_CASES}")

if __name__ == "__main__":
    main()
