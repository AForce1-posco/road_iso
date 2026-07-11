#!/usr/bin/env python3
"""동적 주행 통합 시계열 CSV → LayoutID별 실측 p95(|LTR_Total|).
surrogate 라벨(p95LTR)과 동일 계산식 (case9001로 검증: 라벨 0.4836 = 재계산 0.4838).
사용:  python Assets/Data/Results/measure_p95.py <combined_timeseries.csv>
"""
import sys, argparse
import numpy as np, pandas as pd

def grade(p95, cuts=(0.47, 0.50, 0.53)):
    return 3 if p95 >= cuts[2] else 2 if p95 >= cuts[1] else 1 if p95 >= cuts[0] else 0

GNAME = {0: "안전", 1: "주의", 2: "위험", 3: "고위험"}

def measure_p95(csv_path):
    """LayoutID → (실측 p95|LTR|, 프레임수, run개수). run 여러개면 전체 프레임 합산."""
    d = pd.read_csv(csv_path, usecols=["LayoutID", "SampleIndex", "LTR_Total"])
    out = {}
    for lid, g in d.groupby("LayoutID"):
        a = np.abs(g["LTR_Total"].values)
        nruns = int((g["SampleIndex"] == 0).sum())
        out[lid] = (float(np.percentile(a, 95)), len(a), nruns)
    return out

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("csv")
    a = ap.parse_args()
    res = measure_p95(a.csv)
    print(f"실측 p95LTR — {a.csv}")
    print(f"{'LayoutID':<24}{'p95LTR':>9}{'/100':>6}{'등급':>6}{'프레임':>8}{'runs':>6}")
    for lid in sorted(res):
        p, n, nr = res[lid]
        g = grade(p)
        warn = "  ⚠️여러run" if nr > 1 else ""
        print(f"{lid:<24}{p:>9.4f}{p*100:>6.0f}{g:>4} {GNAME[g]}{n:>8}{nr:>6}{warn}")
