#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
combined_timeseries_*.csv 각 행에 '정적 배치(layout) 요약 피처'를 컬럼으로 덧붙인다.
(LAYOUT_COLUMNS_SPEC.md 참조 — 이 스크립트가 *_layout.csv 를 생성한 재현 근거)

- 조인 키: CSV의 LayoutID 컬럼( = caseNNN ) → Assets/Data/Cases/caseNNN.json
- CoG(x,y,z), MaxHeight: JSON에 이미 계산돼 있는 값을 그대로 사용(신버전 JSON)
- 관성모멘트(InertiaXX/YY/ZZ): cargo[]+카탈로그로 계산 (적재물 CoG 기준·트레이 축·kg·m²)

좌표계/단위: 실축 SI(m, kg, kg·m²), 트레이 중심 원점(x,z)·바닥 기준은 JSON 규약 그대로.
⚠️ 커버리지: 9개 range CSV는 case080~500(=421개)만 덮음. case001~079 없음, 401-450는 절반.
   즉 "500개 전부"가 아님 — 데이터 커버리지 설명 없이 인용 금지.
사용: python3 Assets/Data/Results/augment_layout.py   (--validate 로 샘플 케이스 검증)
"""
import csv, json, glob, os, math, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # road_iso
CATALOG = os.path.join(ROOT, "Assets/Data/cargo_catalog.csv")
CASES = os.path.join(ROOT, "Assets/Data/Cases")
RESULTS = os.path.join(ROOT, "Assets/Data/Results")

NEW_COLS = ["CogX", "CogY", "CogZ", "MaxHeightM",
            "InertiaXX", "InertiaYY", "InertiaZZ"]

# ── 카탈로그: name → (dx,dy,dz [m], mass [kg]) ────────────────
def load_catalog():
    cat = {}
    with open(CATALOG, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            cat[r["name"]] = (
                float(r["sizeX_cm"]) / 100.0,
                float(r["sizeY_cm"]) / 100.0,
                float(r["sizeZ_cm"]) / 100.0,
                float(r["massKg"]),
            )
    return cat

# ── Unity Quaternion.Euler(x,y,z) 회전행렬 (적용순서 Z→X→Y, R = Ry·Rx·Rz) ──
def rot_matrix(ex, ey, ez):
    x, y, z = math.radians(ex), math.radians(ey), math.radians(ez)
    cx, sx = math.cos(x), math.sin(x)
    cy, sy = math.cos(y), math.sin(y)
    cz, sz = math.cos(z), math.sin(z)
    Rx = [[1, 0, 0], [0, cx, -sx], [0, sx, cx]]
    Ry = [[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]]
    Rz = [[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]
    def mm(A, B):
        return [[sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    return mm(Ry, mm(Rx, Rz))

def mat_mul(A, B):
    return [[sum(A[i][k] * B[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

def transpose(A):
    return [[A[j][i] for j in range(3)] for i in range(3)]

# ── 케이스 1개의 관성텐서(적재물 CoG 기준, bed축) 대각성분 ──
def inertia_diag(cargo, cog, cat):
    I = [[0.0] * 3 for _ in range(3)]
    for c in cargo:
        dx, dy, dz, m = cat[c["type"]]
        ib = [
            [m / 12.0 * (dy * dy + dz * dz), 0, 0],
            [0, m / 12.0 * (dx * dx + dz * dz), 0],
            [0, 0, m / 12.0 * (dx * dx + dy * dy)],
        ]
        e = c["localEuler"]
        R = rot_matrix(e["x"], e["y"], e["z"])
        iw = mat_mul(mat_mul(R, ib), transpose(R))  # bed축 정렬
        p = c["localPos"]
        d = (p["x"] - cog["x"], p["y"] - cog["y"], p["z"] - cog["z"])
        d2 = d[0] * d[0] + d[1] * d[1] + d[2] * d[2]
        for i in range(3):
            for j in range(3):
                iw[i][j] += m * (d2 * (1.0 if i == j else 0.0) - d[i] * d[j])
        for i in range(3):
            for j in range(3):
                I[i][j] += iw[i][j]
    return I[0][0], I[1][1], I[2][2]

# ── 전 케이스 피처 계산 → caseNNN: "cx,cy,cz,h,ixx,iyy,izz" 문자열 ──
def build_features(cat):
    feats = {}
    for f in sorted(glob.glob(os.path.join(CASES, "case*.json"))):
        j = json.load(open(f, encoding="utf-8"))
        name = os.path.splitext(os.path.basename(f))[0]
        cog = j["cog"]
        ixx, iyy, izz = inertia_diag(j["cargo"], cog, cat)
        feats[name] = "%.6f,%.6f,%.6f,%.6f,%.8f,%.8f,%.8f" % (
            cog["x"], cog["y"], cog["z"], j["maxHeight"], ixx, iyy, izz)
    return feats

def main():
    cat = load_catalog()
    feats = build_features(cat)

    if len(sys.argv) > 1 and sys.argv[1] == "--validate":
        for k in ("case080", "case001", "case500"):
            print(k, "->", feats.get(k))
        print("total cases:", len(feats))
        return

    files = sorted(glob.glob(os.path.join(RESULTS, "combined_timeseries_[0-9]*-*.csv")))
    empty_suffix = "," * (len(NEW_COLS) - 1)
    for path in files:
        out = path[:-4] + "_layout.csv"
        missing = set()
        n = 0
        with open(path, "r", encoding="utf-8", newline="") as fin, \
             open(out, "w", encoding="utf-8", newline="") as fout:
            header = fin.readline().rstrip("\n").rstrip("\r")
            fout.write(header + "," + ",".join(NEW_COLS) + "\n")
            for line in fin:
                line = line.rstrip("\n").rstrip("\r")
                if not line:
                    continue
                first = line.find(",")
                second = line.find(",", first + 1)
                layout = line[first + 1:second]
                suf = feats.get(layout)
                if suf is None:
                    missing.add(layout)
                    suf = empty_suffix
                fout.write(line + "," + suf + "\n")
                n += 1
        print("[OK] %s  rows=%d  -> %s%s" % (
            os.path.basename(path), n, os.path.basename(out),
            ("  MISSING=" + ",".join(sorted(missing)) if missing else "")))

if __name__ == "__main__":
    main()
