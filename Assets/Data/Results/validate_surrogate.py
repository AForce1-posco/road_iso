#!/usr/bin/env python3
"""surrogate가 동적 주행을 잘 반영하는지 검증 — CSV 하나로.
시계열 CSV에는 이제 배치 9피처(CogX…)가 들어있으므로, layout JSON 없이:
  예측 = 그 피처 → surrogate 트리
  실측 = 그 케이스 LTR_Total 의 p95(|LTR|)
  → LayoutID별 예측/실측 표 + 상관 R + MAE
사용:  python Assets/Data/Results/validate_surrogate.py <timeseries.csv> [--model layout_risk_p95_9feat]
※ CogX 등 신규 컬럼이 있는 CSV(패치 후 주행분)에서만 동작.
"""
import sys, os, argparse
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from predict_p95 import load_model, predict, grade, GNAME

FEATS = ["CargoCount", "TotalMassKg", "CogX", "CogY", "CogZ",
         "MaxHeightM", "InertiaXX", "InertiaYY", "InertiaZZ"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    ap.add_argument("--model", default="layout_risk_p95_9feat")
    a = ap.parse_args()

    need = ["LayoutID", "LTR_Total"] + FEATS
    d = pd.read_csv(a.csv, usecols=lambda c: c in need)
    miss = [c for c in need if c not in d.columns]
    if miss:
        print(f"[에러] CSV에 컬럼 없음: {miss}\n → CogX 등 신규 컬럼이 있는 (패치 후) 주행 CSV를 쓰세요."); return

    m = load_model(a.model)
    rows = []
    for lid, g in d.groupby("LayoutID"):
        feat = {k: float(g[k].iloc[0]) for k in FEATS}     # 케이스 내내 상수
        pred = predict(m, feat)
        meas = float(np.percentile(np.abs(g["LTR_Total"].values), 95))
        rows.append((lid, pred, meas, len(g)))

    print(f"\n=== surrogate 예측 vs 동적 실측 p95LTR (model={a.model}) ===")
    print(f"{'LayoutID':<24}{'예측':>8}{'실측':>8}{'오차':>8}{'예측등급':>8}{'실측등급':>8}{'프레임':>8}")
    P, M = [], []
    for lid, pr, me, n in sorted(rows):
        print(f"{lid:<24}{pr:>8.3f}{me:>8.3f}{pr-me:>+8.3f}"
              f"{grade(pr):>6} {GNAME[grade(pr)]}{grade(me):>4} {GNAME[grade(me)]}{n:>8}")
        P.append(pr); M.append(me)

    if len(P) >= 3:
        r = np.corrcoef(P, M)[0, 1]
        mae = np.mean(np.abs(np.array(P) - np.array(M)))
        # 순위상관(스피어만) — 좁은대역에서 더 신뢰
        rp = pd.Series(P).rank(); rm = pd.Series(M).rank()
        rho = np.corrcoef(rp, rm)[0, 1]
        print(f"\n상관 R(피어슨)={r:.3f}  순위상관(스피어만)={rho:.3f}  MAE={mae:.4f}  n={len(P)}")
        print("  R>0.7 쓸만 / >0.85 좋음, MAE<0.05 근접. 좁은대역이면 순위상관·MAE 우선.")
        print("  ⚠️ LTR이 정적 편향에 오염돼 있으면(직진 LTR≠0) 이 상관은 '아티팩트 학습'일 수 있음.")
    else:
        print(f"\n케이스 {len(P)}개 — 상관 계산엔 3개+ 필요. 다양한 케이스 배치를 한 CSV로 주행 후 다시 돌리세요.")

if __name__ == "__main__":
    main()
