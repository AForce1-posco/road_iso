#!/usr/bin/env python3
"""GRASP 멀티시드 후보들을 예측기로 채점 → best 선택 + 결정론(det) 대비 개선 확인.
사용:
  1) Unity BinPackerRunner 우클릭 "Run GRASP Batch" → grasp_<name>_det.json / _00.json ...
  2) python Assets/Data/Results/grasp_pick.py grasp_<name> [--model layout_risk_p95_9feat]
→ 시드별 예측 위험 표 + best 시드 + det 대비 감소량. best JSON 경로를 refinement 시작점으로 쓰면 됨.
"""
import sys, os, glob, argparse
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from predict_p95 import predict_p95

DIR = "Assets/Data/Cases_binpack"

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("prefix", help="예: grasp_boxpack001 (파일명 접두)")
    ap.add_argument("--model", default="layout_risk_p95_9feat")
    ap.add_argument("--dir", default=DIR)
    a = ap.parse_args()

    det_path = os.path.join(a.dir, f"{a.prefix}_det.json")
    seed_paths = sorted(glob.glob(os.path.join(a.dir, f"{a.prefix}_[0-9][0-9].json")))
    if not seed_paths:
        print(f"[에러] {a.dir}/{a.prefix}_NN.json 을 못 찾음. Run GRASP Batch 했는지 확인."); return

    det = predict_p95(det_path, a.model) if os.path.exists(det_path) else None

    rows = []
    for p in seed_paths:
        r = predict_p95(p, a.model)
        if r is not None:
            rows.append((int(os.path.basename(p).split("_")[-1].split(".")[0]), r, p))
    rows.sort(key=lambda x: x[1])

    print(f"\n=== GRASP 후보 예측 위험 (model={a.model}, 낮을수록 안전) ===")
    if det is not None:
        print(f"  결정론(det)      p95 = {det:.4f}   ← 기존 Pack 기준선")
    print(f"  {'시드':>4}   {'예측 p95':>9}")
    for seed, r, _ in rows:
        mark = "  ★best" if (seed, r) == (rows[0][0], rows[0][1]) else ""
        print(f"  {seed:>4}   {r:>9.4f}{mark}")

    best_seed, best_r, best_path = rows[0]
    print(f"\nbest = 시드 {best_seed}  (p95 {best_r:.4f})   →  {best_path}")
    if det is not None:
        d = det - best_r
        verdict = "GRASP가 더 안전" if d > 0.003 else ("사실상 동률" if abs(d) <= 0.003 else "det가 더 안전")
        print(f"det 대비 감소량 = {d:+.4f}  →  {verdict}")
        print(f"후보 분포: 최저 {rows[0][1]:.4f} / 중앙 {rows[len(rows)//2][1]:.4f} / 최고 {rows[-1][1]:.4f}  (n={len(rows)})")
    print("\n※ best JSON을 RefinementAgent 시작 배치로 쓰면 '다양한 시작점' 효과. 최종은 실제 주행으로 확인 권장.")

if __name__ == "__main__":
    main()
