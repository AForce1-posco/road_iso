#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
정적 배치 케이스(2000개) 생성기 — 새 트레이(21×62×27cm, front=+z) 기준.
다양한 적재 분포: 성긴 것 / 보통 / 꽉 찬 것 / 높이 초과 / 과적 / 편심(위험).

저장 규약 (CargoLayoutFile):
  bed: {widthX:0.21, lengthZ:0.62, wallHeight:0.06}
  cargo[i]: {type:이름, localPos:{x,y,z}, localEuler:{x,y,z}, secured:bool}
  좌표: x=좌우[-0.105,0.105], z=주행[-0.31,0.31], y=바닥0.01+높이/2 (적층은 +아래top).
  회전(euler, 검증된 값): 박스/포대 (0,0,0) 또는 (0,90,0), 코일 (0,0,0), 파이프 (90,90,0).
  규칙: 앞(+z) 오버행 금지(전 화물), 뒤(-z)는 파이프만 오버행 허용, 좌우 오버행 금지.
사용: python3 Docs/generate_cases.py
"""
import csv, json, os, random

random.seed(42)
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CASES_DIR = os.path.join(ROOT, "Assets/Data/Cases")
CATALOG = os.path.join(ROOT, "Assets/Data/cargo_catalog.csv")

N_CASES = 2000
FLOOR = 0.01
HALF_X = 0.105        # 0.21/2 좌우
HALF_Z = 0.31         # 0.62/2 주행
PAYLOAD = 7.0
H_LIMIT = 0.27
GRID = 0.01
PIPE_REAR_OVERHANG = 0.20   # 파이프 뒤(-z) 오버행 허용량(m)

# ── 카탈로그 로드 ──────────────────────────────────────────────
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
    """(fx, fz, height, euler) — 트레이 평면 발자국·높이·저장회전."""
    if t["shape"] == "Pipe":
        return t["sx"], t["sz"], t["sy"], (90.0, 0.0, 0.0)    # 항상 주행축(z): 길이=로컬Y→(90,0,0)로 눕히면 z방향. (90,90,0)은 x(가로)로 틀어져 버그였음
    if yaw:
        return t["sz"], t["sx"], t["sy"], (0.0, 90.0, 0.0)
    return t["sx"], t["sz"], t["sy"], (0.0, 0.0, 0.0)

def snap(v):
    return round(round(v / GRID) * GRID, 4)

def rects_overlap(a, b):
    return not (a[1] <= b[0] + 1e-9 or a[0] >= b[1] - 1e-9 or
                a[3] <= b[2] + 1e-9 or a[2] >= b[3] - 1e-9)

# ── 바닥 배치 시도 ─────────────────────────────────────────────
def place_floor(placed, t, eccentric=None):
    yaws = [False, True] if t["shape"] in ("Box", "Sack") else [False]
    for _ in range(50):
        yaw = random.choice(yaws)
        fx, fz, h, euler = footprint(t, yaw)
        # 좌우가 트레이 폭 초과면 회전으로 회피
        if fx > 2 * HALF_X:
            yaw = not yaw
            fx, fz, h, euler = footprint(t, yaw)
            if fx > 2 * HALF_X:
                return None
        hx, hz = fx / 2.0, fz / 2.0
        cx_lo, cx_hi = -HALF_X + hx, HALF_X - hx
        cz_hi = HALF_Z - hz                                   # 앞 오버행 금지
        rear = HALF_Z + (PIPE_REAR_OVERHANG if t["shape"] == "Pipe" else 0.0)
        cz_lo = -rear + hz
        if cx_lo > cx_hi or cz_lo > cz_hi:
            continue
        if eccentric == "x":
            cx = cx_hi if random.random() < 0.85 else random.uniform(cx_lo, cx_hi)
            cz = random.uniform(cz_lo, cz_hi)
        elif eccentric == "z":
            cx = random.uniform(cx_lo, cx_hi)
            cz = cz_hi if random.random() < 0.85 else random.uniform(cz_lo, cz_hi)
        else:
            cx = random.uniform(cx_lo, cx_hi)
            cz = random.uniform(cz_lo, cz_hi)
        cx = min(cx_hi, max(cx_lo, snap(cx)))
        cz = min(cz_hi, max(cz_lo, snap(cz)))
        rect = (cx - hx, cx + hx, cz - hz, cz + hz)
        if any(rects_overlap(rect, p["rect"]) for p in placed):
            continue
        return dict(cx=cx, cy=round(FLOOR + h / 2.0, 4), cz=cz, euler=euler,
                    rect=rect, top=round(FLOOR + h, 4), t=t)
    return None

def col_top(placed, cx, cz):
    """(cx,cz) 기둥의 현재 최상단 높이 (그 점을 덮는 화물들의 최대 top)."""
    top = FLOOR
    for p in placed:
        rx0, rx1, rz0, rz1 = p["rect"]
        if rx0 - 1e-6 <= cx <= rx1 + 1e-6 and rz0 - 1e-6 <= cz <= rz1 + 1e-6:
            top = max(top, p["top"])
    return top

def stack_on(placed, t):
    """기존 바닥 화물 위(그 기둥의 현재 꼭대기)에 더 작은 화물을 얹음 (적층)."""
    bases = [p for p in placed if p["t"]["shape"] in ("Box", "Sack")]
    random.shuffle(bases)
    for base in bases:
        fx, fz, h, euler = footprint(t, False)
        hx, hz = fx / 2.0, fz / 2.0
        bx0, bx1, bz0, bz1 = base["rect"]
        if fx > (bx1 - bx0) + 1e-6 or fz > (bz1 - bz0) + 1e-6:      # 받침 안에 들어와야
            continue
        cx = round((bx0 + bx1) / 2.0, 4)
        cz = round((bz0 + bz1) / 2.0, 4)
        support_top = col_top(placed, cx, cz)                      # 기둥 현재 꼭대기에 얹음
        cy = round(support_top + h / 2.0, 4)
        rect = (cx - hx, cx + hx, cz - hz, cz + hz)
        return dict(cx=cx, cy=cy, cz=cz, euler=euler, rect=rect,
                    top=round(support_top + h, 4), t=t)
    return None

def total_mass(placed):
    return sum(p["t"]["mass"] for p in placed)

def max_top(placed):
    return max((p["top"] for p in placed), default=FLOOR)

# ── 케이스 프로파일 ────────────────────────────────────────────
def build_case(types, profile):
    placed = []
    boxes = [t for t in types if t["shape"] in ("Box", "Sack", "Coil")]
    pipes = [t for t in types if t["shape"] == "Pipe"]
    heavy = sorted(types, key=lambda t: -t["mass"])[:6]

    if profile == "sparse":
        for _ in range(random.randint(2, 4)):
            p = place_floor(placed, random.choice(types))
            if p: placed.append(p)

    elif profile == "normal":
        for _ in range(random.randint(5, 9)):
            p = place_floor(placed, random.choice(types))
            if p: placed.append(p)

    elif profile == "packed":                                 # 꽉 차게
        fails = 0
        while fails < 25 and len(placed) < 40:
            p = place_floor(placed, random.choice(boxes + pipes))
            if p: placed.append(p); fails = 0
            else: fails += 1
        # 일부는 위에 적층
        for _ in range(random.randint(0, 4)):
            s = stack_on(placed, random.choice(boxes))
            if s: placed.append(s)

    elif profile == "tall":                                   # 높이 초과 유도
        for _ in range(random.randint(2, 4)):
            p = place_floor(placed, random.choice(boxes))
            if p: placed.append(p)
        for _ in range(random.randint(3, 8)):                 # 계속 쌓기
            s = stack_on(placed, random.choice(boxes))
            if s: placed.append(s)

    elif profile == "heavy":                                  # 과적 유도
        while total_mass(placed) < PAYLOAD + 1.0 and len(placed) < 30:
            p = place_floor(placed, random.choice(heavy))
            if p: placed.append(p)
            else: break

    elif profile == "eccentric":                              # 한쪽 쏠림
        axis = random.choice(["x", "z"])
        for _ in range(random.randint(4, 8)):
            p = place_floor(placed, random.choice(types), eccentric=axis)
            if p: placed.append(p)

    return placed

def to_json(placed):
    cargo = []
    for p in placed:
        ex, ey, ez = p["euler"]
        cargo.append(dict(
            type=p["t"]["name"],
            localPos=dict(x=round(p["cx"], 4), y=round(p["cy"], 4), z=round(p["cz"], 4)),
            localEuler=dict(x=ex, y=ey, z=ez),
            secured=True,
        ))
    return dict(version=1,
                bed=dict(widthX=0.21, lengthZ=0.62, wallHeight=0.06),
                cargo=cargo)

def main():
    types = load_types()
    os.makedirs(CASES_DIR, exist_ok=True)
    # 기존 case*.json 제거 (meta는 유지)
    for f in os.listdir(CASES_DIR):
        if f.startswith("case") and f.endswith(".json"):
            os.remove(os.path.join(CASES_DIR, f))

    profiles = (["sparse"] * 400 + ["normal"] * 600 + ["packed"] * 500 +
                ["tall"] * 200 + ["heavy"] * 200 + ["eccentric"] * 100)
    random.shuffle(profiles)

    stats = {}
    counts = []
    for i, prof in enumerate(profiles[:N_CASES], start=1):
        placed = build_case(types, prof)
        # 빈 케이스 방지
        if not placed:
            p = place_floor([], random.choice(types))
            if p: placed = [p]
        data = to_json(placed)
        with open(os.path.join(CASES_DIR, f"case{i:03d}.json"), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
        stats[prof] = stats.get(prof, 0) + 1
        counts.append(len(placed))

    print(f"생성 완료: {N_CASES}개")
    print("프로파일 분포:", stats)
    print(f"화물 수: 최소 {min(counts)} / 평균 {sum(counts)/len(counts):.1f} / 최대 {max(counts)}")

if __name__ == "__main__":
    main()
