#!/usr/bin/env python3
"""배치 JSON → surrogate 예측 p95LTR.
Unity의 RefinementAgent.BuildRiskFeatures(9피처) + LayoutRiskModel.Predict(트리순회)를 그대로 복제.
사용:  python Assets/Data/Results/predict_p95.py <layout.json> [--model layout_risk_p95_9feat]
※ road_iso 루트에서 실행 (카탈로그·모델 상대경로 사용).
"""
import json, sys, csv, argparse

CATALOG = "Assets/Data/cargo_catalog.csv"
RES = "Assets/Resources"
FLOOR_TOP_Y = 0.01          # RuleConfig.floorTopY (BuildRiskFeatures maxTop 초기값)
MODEL_MASS_SCALE = 100.0    # RefinementAgent.modelMassScaleKg

def load_catalog(path=CATALOG):
    cat = {}
    with open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            cat[r["name"].strip()] = (
                float(r["sizeX_cm"]) / 100.0, float(r["sizeY_cm"]) / 100.0,
                float(r["sizeZ_cm"]) / 100.0, float(r["massKg"]))
    return cat

def build_features(layout, cat):
    """RefinementAgent.BuildRiskFeatures 복제 (9피처). 관성은 raw massKg, TotalMassKg만 ×100."""
    items = []
    for c in layout["cargo"]:
        nm = c["type"].strip()
        if nm not in cat:
            print(f"[경고] 카탈로그에 '{nm}' 없음 → 건너뜀", file=sys.stderr); continue
        sx, sy, sz, m = cat[nm]
        ey = c.get("localEuler", {}).get("y", 0.0)
        rot = abs(ey - 90) < 1 or abs(ey - 270) < 1          # 90/270 → x,z 스왑
        Dx, Dz = (sz, sx) if rot else (sx, sz)
        p = c["localPos"]
        items.append((m, p["x"], p["y"], p["z"], Dx, sy, Dz))
    if not items:
        return None
    M = sum(i[0] for i in items)
    cogx = sum(i[0] * i[1] for i in items) / M
    cogy = sum(i[0] * i[2] for i in items) / M
    cogz = sum(i[0] * i[3] for i in items) / M
    ixx = iyy = izz = 0.0
    maxtop = FLOOR_TOP_Y
    for m, px, py, pz, Dx, Dy, Dz in items:
        ibx = m / 12.0 * (Dy * Dy + Dz * Dz)
        iby = m / 12.0 * (Dx * Dx + Dz * Dz)
        ibz = m / 12.0 * (Dx * Dx + Dy * Dy)
        ox, oy, oz = px - cogx, py - cogy, pz - cogz
        ixx += ibx + m * (oy * oy + oz * oz)
        iyy += iby + m * (ox * ox + oz * oz)
        izz += ibz + m * (ox * ox + oy * oy)
        maxtop = max(maxtop, py + Dy / 2.0)
    return {"CargoCount": len(items), "TotalMassKg": M * MODEL_MASS_SCALE,
            "CogX": cogx, "CogY": cogy, "CogZ": cogz, "MaxHeightM": maxtop,
            "InertiaXX": ixx, "InertiaYY": iyy, "InertiaZZ": izz}

def load_model(name):
    with open(f"{RES}/{name}.json", encoding="utf-8") as f:
        return json.load(f)

def predict(model, feat):
    """LayoutRiskModel.Predict 복제: featureNames 순서로 벡터화(없으면 0) → 트리 순회 합산 → clamp01."""
    fn = model["featureNames"]
    x = [feat.get(n, 0.0) for n in fn]
    fi, th, lc, rc, lv = (model["featureIndex"], model["threshold"],
                          model["leftChild"], model["rightChild"], model["leafValue"])
    total = model["baseScore"]
    for root in model["treeRoots"]:
        idx = root
        while fi[idx] != -1:
            idx = lc[idx] if x[fi[idx]] < th[idx] else rc[idx]
        total += lv[idx]
    return max(0.0, min(1.0, total))

def grade(p95, cuts=(0.47, 0.50, 0.53)):
    return 3 if p95 >= cuts[2] else 2 if p95 >= cuts[1] else 1 if p95 >= cuts[0] else 0

GNAME = {0: "안전", 1: "주의", 2: "위험", 3: "고위험"}

def predict_p95(layout_path, model_name="layout_risk_p95_9feat"):
    layout = json.load(open(layout_path, encoding="utf-8"))
    feat = build_features(layout, load_catalog())
    if feat is None:
        return None
    return predict(load_model(model_name), feat)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("layout")
    ap.add_argument("--model", default="layout_risk_p95_9feat")
    a = ap.parse_args()
    p = predict_p95(a.layout, a.model)
    print(f"{a.layout}")
    print(f"  예측 p95LTR = {p:.4f}  ({p*100:.0f}/100)  등급 {grade(p)} {GNAME[grade(p)]}  [model={a.model}]")
