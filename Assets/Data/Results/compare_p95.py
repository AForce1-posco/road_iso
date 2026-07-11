#!/usr/bin/env python3
"""예측(surrogate) vs 실측(동적) p95LTR 비교 리포트.
- 실측: 통합 시계열 CSV에서 LayoutID별 p95|LTR|
- 예측: 배치 JSON들에서 surrogate 예측 p95 (파일명 stem == LayoutID 로 매칭)
- 출력: 배치별 예측/실측 표 + 상관(R) + (start/refined 이름쌍이면) 개선 여부
사용:
  python Assets/Data/Results/compare_p95.py --timeseries <csv> --layouts "경로/*.json" [--model layout_risk_p95_9feat]
※ 배치 JSON 파일명을 주행 시 LayoutID와 똑같이 두세요 (예: refine_case1_start.json → LayoutID 'refine_case1_start').
"""
import sys, os, glob, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from predict_p95 import predict_p95, grade, GNAME
from measure_p95 import measure_p95

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--timeseries", required=True)
    ap.add_argument("--layouts", required=True, help="배치 JSON glob (예: 'Assets/Data/Results/refine_*_*.json')")
    ap.add_argument("--model", default="layout_risk_p95_9feat")
    a = ap.parse_args()

    measured = measure_p95(a.timeseries)                       # LayoutID -> (p95, frames, runs)
    files = sorted(glob.glob(a.layouts))
    if not files:
        print(f"배치 JSON 없음: {a.layouts}"); return

    rows = []
    for fp in files:
        lid = os.path.splitext(os.path.basename(fp))[0]
        pred = predict_p95(fp, a.model)
        meas = measured.get(lid, (None, 0, 0))[0]
        rows.append((lid, pred, meas))

    print(f"\n=== 예측 vs 실측 p95LTR (model={a.model}) ===")
    print(f"{'배치(LayoutID)':<26}{'예측':>8}{'실측':>8}{'예측등급':>8}{'실측등급':>8}{'오차':>8}")
    preds, meass = [], []
    for lid, pr, me in rows:
        pg = f"{grade(pr)}" if pr is not None else "-"
        mg = f"{grade(me)}" if me is not None else "-"
        err = f"{pr-me:+.3f}" if (pr is not None and me is not None) else "  (실측無)"
        me_s = f"{me:.4f}" if me is not None else "   -"
        print(f"{lid:<26}{pr:>8.4f}{me_s:>8}{pg:>8}{mg:>8}{err:>8}")
        if pr is not None and me is not None:
            preds.append(pr); meass.append(me)

    # 상관 (surrogate 타당성)
    if len(preds) >= 3:
        import numpy as np
        r = np.corrcoef(preds, meass)[0, 1]
        mae = np.mean(np.abs(np.array(preds) - np.array(meass)))
        print(f"\n상관 R(예측,실측) = {r:.3f}   MAE = {mae:.4f}   (n={len(preds)})")
        print("  → R이 1에 가깝고 MAE 작으면 surrogate가 동적을 잘 반영 (믿을 만함)")
    else:
        print("\n(상관 계산에는 실측 매칭 3개 이상 필요)")

    # start/refined 이름쌍 개선 여부
    print("\n=== 개선 여부 (start vs refined 이름쌍) ===")
    stems = {lid: (pr, me) for lid, pr, me in rows}
    found = False
    for lid, (pr, me) in stems.items():
        if "refined" in lid:
            base = lid.replace("refined", "start")
            if base in stems:
                found = True
                sp, sm = stems[base]
                def d(a_, b_): return "낮아짐 ✅" if (a_ is not None and b_ is not None and a_ < b_) else ("높아짐 ❌" if (a_ is not None and b_ is not None) else "실측無")
                print(f"  {base} → {lid}")
                print(f"    예측 {sp:.4f} → {pr:.4f}  ({d(pr,sp)})")
                if sm is not None and me is not None:
                    print(f"    실측 {sm:.4f} → {me:.4f}  ({d(me,sm)})  ← 실제로 위험 낮췄나")
    if not found:
        print("  (start/refined 이름쌍 없음 — 파일명을 'X_start.json'/'X_refined.json'로 두면 자동 비교)")

if __name__ == "__main__":
    main()
