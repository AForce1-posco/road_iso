#!/usr/bin/env python3
"""랜덤 배치 Dense vs Stable 예측 p95 비교 (같은 매니페스트).
BinPackerRunner의 Run Random Batch를 Dense·Stable 같은 seed로 돌린 뒤:
  python Assets/Data/Results/compare_modes.py
→ 매니페스트별 예측 p95(Dense/Stable) + 어느 모드가 낮은지 + 평균/승수.
※ 예측은 surrogate(검증 R0.95)라 주행 대신 빠른 스크리닝용. 최종은 몇 개 골라 주행 확인.
"""
import glob, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from predict_p95 import predict_p95, grade, GNAME

DIR = "Assets/Data/Cases_binpack"

def idx_of(path):
    # rand_Dense_007.json -> 7
    s = os.path.splitext(os.path.basename(path))[0]
    return s.split("_")[-1]

def main():
    dense = {idx_of(p): p for p in glob.glob(f"{DIR}/rand_Dense_*.json")}
    stable = {idx_of(p): p for p in glob.glob(f"{DIR}/rand_Stable_*.json")}
    keys = sorted(set(dense) & set(stable))
    if not keys:
        print(f"rand_Dense_* / rand_Stable_* 짝을 못 찾음 ({DIR}). 두 모드 다 Run Random Batch 했는지 확인."); return

    print(f"{'매니페스트':<10}{'Dense예측':>10}{'Stable예측':>11}{'더낮은쪽':>10}")
    D, S = [], []
    dwin = swin = tie = 0
    for k in keys:
        pd_ = predict_p95(dense[k]); ps = predict_p95(stable[k])
        if pd_ is None or ps is None: continue
        D.append(pd_); S.append(ps)
        if abs(pd_-ps) < 0.005: win = "≈"; tie += 1
        elif pd_ < ps: win = "Dense"; dwin += 1
        else: win = "Stable"; swin += 1
        print(f"rand_{k:<5}{pd_:>10.3f}{ps:>11.3f}{win:>10}")
    if D:
        D, S = np.array(D), np.array(S)
        print(f"\n평균 예측 p95:  Dense={D.mean():.3f}   Stable={S.mean():.3f}")
        print(f"더 안전한(낮은) 횟수:  Dense {dwin} / Stable {swin} / 비슷 {tie}  (n={len(D)})")
        print(f"→ 평균·승수로 어느 모드가 대체로 안전한지 판단. 차이 큰 매니페스트 몇 개를 골라 실제 주행으로 확인 권장.")

if __name__ == "__main__":
    main()
