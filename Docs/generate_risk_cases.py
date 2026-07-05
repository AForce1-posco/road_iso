#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
위험 위주 동적 데이터셋 생성기 — 500개 (트레이 안쪽 21×61×27cm, front=+z).
목적: 동적 주행 시 실제 전복/위험이 나오는 케이스를 확보 → S6 위험 예측기 학습용.

분포 (500개):
  70% 위험(350) = 높이초과(risk_tall) + 과적(risk_heavy) + 극단편심(risk_eccentric)
  20% 촘촘+CoG이상(100) = dense_badcog (현실처럼 꽉 채우되 질량이 한쪽으로 쏠림)
  10% 정상(50) = stable (규칙 준수·중앙·무거운 거 아래·안정)

⚠️ 위험 케이스는 하드룰(높이 27cm·과적 7kg·CoG)을 "의도적으로 위반"함 (사용자 승인).
   단 물리적 유효성은 유지: 겹침 금지 · 화물은 받쳐짐(공중부양X) · 트레이 x·z 안(파이프만 뒤 오버행).
모든 화물 secured=True(고정). 각 케이스에 mode 태그 저장(risky/dense_badcog/normal).

저장 규약(CargoLayoutFile): bed{widthX:0.21,lengthZ:0.61,wallHeight:0.06} + cargo[]{type,localPos,localEuler,secured}
사용: python3 Docs/generate_risk_cases.py
"""
import csv, json, os, random

random.seed(123)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CASES_DIR = os.path.join(ROOT, "Assets/Data/Cases")
CATALOG = os.path.join(ROOT, "Assets/Data/cargo_catalog.csv")

N_CASES = 500
FLOOR = 0.01
HALF_X = 0.105        # 0.21/2 좌우
HALF_Z = 0.305        # 0.61/2 주행
PAYLOAD = 7.0
H_LIMIT = 0.27
GRID = 0.01
PIPE_REAR_OVERHANG = 0.20

# ── 카탈로그 ──────────────────────────────────────────────
def load_types():
    ts = []
    with open(CATALOG, encoding="utf-8") as f:
        for row in csv.DictReader(f):
            ts.append(dict(
                id=row["id"], name=row["name"], shape=row["shape"],
                sx=float(row["sizeX_cm"]) / 100.0,
                sy=float(row["sizeY_cm"]) / 100.0,
                sz=float(row["sizeZ_cm"]) / 100.0,
                mass=float(row["massKg"]),
            ))
    return ts

def footprint(t, yaw):
    if t["shape"] == "Pipe":
        return t["sx"], t["sz"], t["sy"], (90.0, 0.0, 0.0)   # 파이프 길이=z(주행)
    if yaw:
        return t["sz"], t["sx"], t["sy"], (0.0, 90.0, 0.0)
    return t["sx"], t["sz"], t["sy"], (0.0, 0.0, 0.0)

def snap(v): return round(round(v / GRID) * GRID, 4)

def rects_overlap(a, b):
    return not (a[1] <= b[0] + 1e-9 or a[0] >= b[1] - 1e-9 or
                a[3] <= b[2] + 1e-9 or a[2] >= b[3] - 1e-9)

def place_floor(placed, t, region=None):
    """바닥에 놓기. region=(rx_lo,rx_hi,rz_lo,rz_hi) 중심 허용범위(쏠림 유도)."""
    yaws = [False, True] if t["shape"] in ("Box", "Sack") else [False]
    for _ in range(80):
        yaw = random.choice(yaws)
        fx, fz, h, euler = footprint(t, yaw)
        if fx > 2 * HALF_X:
            yaw = not yaw; fx, fz, h, euler = footprint(t, yaw)
            if fx > 2 * HALF_X: return None
        hx, hz = fx / 2.0, fz / 2.0
        cx_lo, cx_hi = -HALF_X + hx, HALF_X - hx
        cz_hi = HALF_Z - hz
        rear = HALF_Z + (PIPE_REAR_OVERHANG if t["shape"] == "Pipe" else 0.0)
        cz_lo = -rear + hz
        if region is not None:                       # 지정 영역과 교집합 (쏠림)
            cx_lo, cx_hi = max(cx_lo, region[0]), min(cx_hi, region[1])
            cz_lo, cz_hi = max(cz_lo, region[2]), min(cz_hi, region[3])
        if cx_lo > cx_hi or cz_lo > cz_hi: continue
        cx = min(cx_hi, max(cx_lo, snap(random.uniform(cx_lo, cx_hi))))
        cz = min(cz_hi, max(cz_lo, snap(random.uniform(cz_lo, cz_hi))))
        rect = (cx - hx, cx + hx, cz - hz, cz + hz)
        if any(rects_overlap(rect, p["rect"]) for p in placed): continue
        return dict(cx=cx, cy=round(FLOOR + h / 2.0, 4), cz=cz, euler=euler,
                    rect=rect, top=round(FLOOR + h, 4), t=t)
    return None

def stack_at(placed, t, cx, cz):
    """지정 기둥(cx,cz)의 현재 꼭대기에 얹음 (탑 쌓기용, 항상 성공)."""
    fx, fz, h, euler = footprint(t, False)
    hx, hz = fx / 2.0, fz / 2.0
    st = col_top(placed, cx, cz)
    return dict(cx=cx, cy=round(st + h / 2.0, 4), cz=cz, euler=euler,
                rect=(cx - hx, cx + hx, cz - hz, cz + hz), top=round(st + h, 4), t=t)

def build_tower(placed, box_types, region, target_h):
    """region 안 한 기둥에 같은 박스를 target_h 넘을 때까지 수직으로 쌓음(확실히 높게)."""
    base = place_floor(placed, random.choice(box_types), region=region)
    if not base: return
    placed.append(base)
    cx, cz, tb = base["cx"], base["cz"], base["t"]
    while col_top(placed, cx, cz) < target_h and len(placed) < 40:
        placed.append(stack_at(placed, tb, cx, cz))

def col_top(placed, cx, cz):
    top = FLOOR
    for p in placed:
        rx0, rx1, rz0, rz1 = p["rect"]
        if rx0 - 1e-6 <= cx <= rx1 + 1e-6 and rz0 - 1e-6 <= cz <= rz1 + 1e-6:
            top = max(top, p["top"])
    return top

def stack_on(placed, t, prefer=None):
    """기존 화물 위에 적층. prefer=화물 리스트면 그 위를 우선."""
    bases = [p for p in placed if p["t"]["shape"] in ("Box", "Sack")]
    random.shuffle(bases)
    for base in bases:
        fx, fz, h, euler = footprint(t, False)
        hx, hz = fx / 2.0, fz / 2.0
        bx0, bx1, bz0, bz1 = base["rect"]
        if fx > (bx1 - bx0) + 1e-6 or fz > (bz1 - bz0) + 1e-6: continue
        cx = round((bx0 + bx1) / 2.0, 4); cz = round((bz0 + bz1) / 2.0, 4)
        support_top = col_top(placed, cx, cz)
        rect = (cx - hx, cx + hx, cz - hz, cz + hz)
        return dict(cx=cx, cy=round(support_top + h / 2.0, 4), cz=cz, euler=euler,
                    rect=rect, top=round(support_top + h, 4), t=t)
    return None

def total_mass(placed): return sum(p["t"]["mass"] for p in placed)

# ── 프로파일 ────────────────────────────────────────────
def build_case(types, profile):
    placed = []
    boxes = [t for t in types if t["shape"] in ("Box", "Sack", "Coil")]
    stackable = [t for t in types if t["shape"] in ("Box", "Sack")]
    pipes = [t for t in types if t["shape"] == "Pipe"]
    heavy = sorted(types, key=lambda t: -t["mass"])[:6]
    small = [t for t in stackable if t["sx"] <= 0.12 and t["sz"] <= 0.12] or stackable
    LH = (-HALF_X, 0.0); RH = (0.0, HALF_X)         # 좌/우 절반

    if profile == "stable":                         # 정상: 중앙·무거운거 아래·규칙 내
        hs = sorted(types, key=lambda t: -t["mass"])[:8]
        for _ in range(random.randint(3, 6)):
            t = random.choice(hs)
            p = place_floor(placed, t)
            if p and total_mass(placed) + t["mass"] <= PAYLOAD: placed.append(p)
        for _ in range(random.randint(0, 2)):       # 가벼운 것만 살짝 적층(높이 유지)
            s = stack_on(placed, random.choice(small))
            if s and s["top"] <= FLOOR + H_LIMIT and total_mass(placed) + s["t"]["mass"] <= PAYLOAD:
                placed.append(s)

    elif profile == "dense_badcog":                 # 촘촘히 꽉 채우되 무거운 걸 한쪽 바깥에 몰아 CoG 치우침
        fails = 0
        while fails < 40 and len(placed) < 55:
            p = place_floor(placed, random.choice(boxes + pipes))
            if p: placed.append(p); fails = 0
            else: fails += 1
        outer = (HALF_X * 0.4, HALF_X) if random.random() < 0.5 else (-HALF_X, -HALF_X * 0.4)
        for _ in range(random.randint(4, 7)):       # 무거운 걸 한쪽 바깥에 몰빵
            p = place_floor(placed, random.choice(heavy), region=(outer[0], outer[1], -HALF_Z, HALF_Z))
            if p: placed.append(p)
            else:
                s = stack_on(placed, random.choice(heavy))
                if s: placed.append(s)

    elif profile == "risk_tall":                    # 위험: 한쪽에 탑 → 높은 CoG(전복)
        half = random.choice([LH, RH])
        reg = (half[0], half[1], -HALF_Z * 0.6, HALF_Z * 0.6)
        build_tower(placed, small, reg, FLOOR + random.uniform(0.34, 0.50))   # 34~50cm 확실히 초과
        if random.random() < 0.5:
            build_tower(placed, small, reg, FLOOR + random.uniform(0.30, 0.44))
        for _ in range(random.randint(1, 3)):
            p = place_floor(placed, random.choice(boxes))
            if p: placed.append(p)

    elif profile == "risk_heavy":                   # 위험: 과적(>7kg) — 무거운 박스 탑으로 확실히
        for _ in range(random.randint(2, 4)):       # 바닥에 무거운 것 몇 개
            p = place_floor(placed, random.choice(heavy))
            if p: placed.append(p)
        hbox = max(stackable, key=lambda t: t["mass"])   # 제일 무거운 박스
        base = place_floor(placed, hbox, region=(LH[0], RH[1], -HALF_Z * 0.6, HALF_Z * 0.6))
        if base:
            placed.append(base); cx, cz = base["cx"], base["cz"]
            while total_mass(placed) < PAYLOAD + 2.0 and len(placed) < 45:  # 같은 무거운 박스 수직 적층
                placed.append(stack_at(placed, hbox, cx, cz))

    elif profile == "risk_eccentric":               # 위험: 극단 측면 편심(전복 핵심) — 바깥쪽에 몰아 쌓음
        outer = (HALF_X * 0.35, HALF_X) if random.random() < 0.5 else (-HALF_X, -HALF_X * 0.35)
        reg = (outer[0], outer[1], -HALF_Z, HALF_Z)
        for _ in range(random.randint(4, 8)):
            p = place_floor(placed, random.choice(types), region=reg)
            if p: placed.append(p)
        for _ in range(random.randint(3, 7)):       # 그 바깥쪽에 더 쌓아 측면 CoG 극단
            s = stack_on(placed, random.choice(boxes))
            if s: placed.append(s)

    return placed

MODE_OF = {"stable": "normal", "dense_badcog": "dense_badcog",
           "risk_tall": "risky", "risk_heavy": "risky", "risk_eccentric": "risky"}

def to_json(placed, profile):
    cargo = []
    for p in placed:
        ex, ey, ez = p["euler"]
        cargo.append(dict(
            type=p["t"]["name"],
            localPos=dict(x=round(p["cx"], 4), y=round(p["cy"], 4), z=round(p["cz"], 4)),
            localEuler=dict(x=ex, y=ey, z=ez),
            secured=True,
        ))
    return dict(version=1, mode=MODE_OF[profile], profile=profile,
                bed=dict(widthX=0.21, lengthZ=0.61, wallHeight=0.06),
                cargo=cargo)

def main():
    types = load_types()
    os.makedirs(CASES_DIR, exist_ok=True)
    for f in os.listdir(CASES_DIR):               # 기존 case*.json 제거(백업은 archive에 있음)
        if f.startswith("case") and f.endswith(".json"):
            os.remove(os.path.join(CASES_DIR, f))

    profiles = (["risk_tall"] * 120 + ["risk_heavy"] * 115 + ["risk_eccentric"] * 115  # 350 위험
                + ["dense_badcog"] * 100                                               # 100 촘촘+CoG
                + ["stable"] * 50)                                                     # 50 정상
    random.shuffle(profiles)

    stats, over_h, over_w = {}, 0, 0
    counts = []
    for i, prof in enumerate(profiles[:N_CASES], start=1):
        placed = build_case(types, prof)
        if not placed:
            p = place_floor([], random.choice(types))
            if p: placed = [p]
        data = to_json(placed, prof)
        with open(os.path.join(CASES_DIR, f"case{i:03d}.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        stats[data["mode"]] = stats.get(data["mode"], 0) + 1
        counts.append(len(placed))
        if placed and max(p["top"] for p in placed) > FLOOR + H_LIMIT + 1e-6: over_h += 1
        if total_mass(placed) > PAYLOAD + 1e-6: over_w += 1

    print(f"생성 완료: {N_CASES}개 → {CASES_DIR}")
    print("mode 분포:", stats)
    print(f"화물 수: 최소 {min(counts)} / 평균 {sum(counts)/len(counts):.1f} / 최대 {max(counts)}")
    print(f"높이 27cm 초과 케이스: {over_h}개 / 과적(>7kg) 케이스: {over_w}개")

if __name__ == "__main__":
    main()
