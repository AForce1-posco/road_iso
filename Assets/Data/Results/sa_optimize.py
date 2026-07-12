#!/usr/bin/env python3
"""Surrogate 직접 최적화 (Simulated Annealing) — "빈패커가 최적인가, 더 나은 배치가 있는가" 결판.
빈패커 배치 JSON을 시작점으로, 박스 (x,z,회전)을 흔들어 예측 p99를 최소화한다.
검증(H2 경계·H8 지지율70%·H13 높이·드롭 안착)을 지키는 유효 배치만 평가.
사용: python Assets/Data/Results/sa_optimize.py <binpacker_layout.json> [--iters 8000] [--model layout_risk_lgbm500] [--seed 0]
→ 빈패커 시작 p99 vs SA 최저 p99 + best 배치 저장. SA가 확 낮추면 "더 나은 배치가 있는데 빈패커/RL이 못 찾은 것".
※ road_iso 루트에서 실행.
"""
import json, sys, os, math, random, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from predict_p95 import load_catalog, build_features, load_model, predict, grade, GNAME

W, L = 0.21, 0.61            # 트레이 폭(x)·길이(z)
FLOOR, HLIM = 0.01, 0.27     # 바닥 top, 높이 한도
EPS = 1e-4                   # 기하(겹침) 판정용
BEPS = 6e-3                  # 경계·높이 허용오차 (RuleConfig.eps=0.005 반영, 빈패커 셀반올림 수용)
SUPPORT_MIN = 0.70           # 엄격 (H8 그대로)

def dims(box, rot):          # 회전 반영 (Dx,Dy,Dz)
    sx, sy, sz = box["s"]
    return (sz, sy, sx) if rot else (sx, sy, sz)

def overlap(ax, adx, bx, bdx):  # 1D 겹침 길이
    return max(0.0, min(ax + adx / 2, bx + bdx / 2) - max(ax - adx / 2, bx - bdx / 2))

def layout_from_state(boxes, state):
    """state=[(x,z,rot)]. 부피 내림차순으로 드롭 안착 → 각 박스 y·유효성 계산. 유효하면 layout dict, 아니면 None."""
    order = sorted(range(len(boxes)), key=lambda i: -boxes[i]["vol"])
    placed = []  # (x,z,Dx,Dy,Dz,top)
    ys = [0.0] * len(boxes)
    for i in order:
        x, z, rot = state[i]
        Dx, Dy, Dz = dims(boxes[i], rot)
        # 경계
        if x - Dx/2 < -BEPS or x + Dx/2 > W + BEPS or z - Dz/2 < -BEPS or z + Dz/2 > L + BEPS:
            return None
        # 드롭: 겹치는 기존 박스들의 최대 top
        rest = FLOOR
        for (px, pz, pDx, pDy, pDz, ptop) in placed:
            if overlap(x, Dx, px, pDx) > EPS and overlap(z, Dz, pz, pDz) > EPS:
                rest = max(rest, ptop)
        top = rest + Dy
        if top > FLOOR + HLIM + BEPS:      # 높이 한도 H13
            return None
        # 지지율 H8: 바닥이면 100%, 아니면 rest 높이 박스들과의 겹침면적/밑면적
        foot = Dx * Dz
        if rest <= FLOOR + EPS:
            sup = 1.0
        else:
            a = 0.0
            for (px, pz, pDx, pDy, pDz, ptop) in placed:
                if abs(ptop - rest) < 1e-3:
                    a += overlap(x, Dx, px, pDx) * overlap(z, Dz, pz, pDz)
            sup = min(1.0, a / foot)
        if sup < SUPPORT_MIN - EPS:
            return None
        ys[i] = rest + Dy/2
        placed.append((x, z, Dx, Dy, Dz, top))
    cargo = []
    for i, b in enumerate(boxes):
        x, z, rot = state[i]
        cargo.append({"type": b["name"], "localPos": {"x": x, "y": ys[i], "z": z},
                      "localEuler": {"x": 0, "y": 90 if rot else 0}, "secured": True})
    return {"version": 1, "bed": {"widthX": W, "lengthZ": L, "wallHeight": 0.06}, "cargo": cargo}

def score(boxes, state, cat, model):
    lay = layout_from_state(boxes, state)
    if lay is None: return None, None
    feat = build_features(lay, cat)
    return predict(model, feat), lay

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("layout")
    ap.add_argument("--iters", type=int, default=8000)
    ap.add_argument("--model", default="layout_risk_lgbm500")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--restarts", type=int, default=3)
    a = ap.parse_args()
    rng = random.Random(a.seed)
    cat = load_catalog(); model = load_model(a.model)

    start = json.load(open(a.layout, encoding="utf-8"))
    boxes = []
    for c in start["cargo"]:
        nm = c["type"].strip()
        if nm not in cat: continue
        sx, sy, sz, m = cat[nm]
        boxes.append({"name": nm, "s": (sx, sy, sz), "m": m, "vol": sx*sy*sz})
    # 시작 state = 빈패커 위치 (회전 역산)
    state0 = []
    for c, b in zip([c for c in start["cargo"] if c["type"].strip() in cat], boxes):
        p = c["localPos"]; ey = c.get("localEuler", {}).get("y", 0)
        rot = 1 if (abs(ey-90) < 1 or abs(ey-270) < 1) else 0
        state0.append((p["x"], p["z"], rot))
    base_p, base_lay = score(boxes, state0, cat, model)
    if base_p is None:
        # 시작이 우리 검증 통과 못 하면(빈패커와 규칙 미세차) 그냥 재드롭 값으로
        print("[주의] 시작배치가 SA 유효성검사 미통과 — 예측만 사용");
        base_p = predict(model, build_features(start, cat))

    best_p, best_lay = base_p, base_lay
    for r in range(a.restarts):
        state = list(state0)
        cur_p, _ = score(boxes, state, cat, model)
        if cur_p is None: cur_p = base_p
        T0, T1 = 0.05, 0.001
        for t in range(a.iters):
            T = T0 * (T1/T0) ** (t / a.iters)
            i = rng.randrange(len(boxes))
            x, z, rot = state[i]
            k = rng.random()
            if k < 0.45:   nx, nz, nr = min(max(x + rng.gauss(0, 0.02), 0), W), z, rot
            elif k < 0.9:  nx, nz, nr = x, min(max(z + rng.gauss(0, 0.05), 0), L), rot
            else:          nx, nz, nr = x, z, 1 - rot
            cand = list(state); cand[i] = (nx, nz, nr)
            p, lay = score(boxes, cand, cat, model)
            if p is None: continue
            if p < cur_p or rng.random() < math.exp(-(p - cur_p) / max(T, 1e-6)):
                state, cur_p = cand, p
                if p < best_p: best_p, best_lay = p, lay
    out = a.layout.replace(".json", "_saopt.json")
    if best_lay: json.dump(best_lay, open(out, "w"), ensure_ascii=False, indent=2)
    print(f"\n=== SA 직접 최적화 (model={a.model}, iters={a.iters}×restart{a.restarts}) ===")
    print(f"  빈패커 시작 p99 = {base_p:.4f}  (등급 {grade(base_p)} {GNAME[grade(base_p)]})")
    print(f"  SA 최저    p99 = {best_p:.4f}  (등급 {grade(best_p)} {GNAME[grade(best_p)]})")
    d = base_p - best_p
    verdict = ("★ SA가 더 낮음 → 빈패커가 놓친 더 안전한 배치 존재" if d > 0.01
               else "≈ 동률 → 빈패커가 (surrogate 기준) 최적 근처")
    print(f"  감소량 = {d:+.4f}  →  {verdict}")
    if best_lay: print(f"  best 배치 저장: {out}")

if __name__ == "__main__":
    main()
