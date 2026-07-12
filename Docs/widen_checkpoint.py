"""
0~3단계(액션공간 11)에서 학습된 RefinementAgent 체크포인트를 4단계(액션공간 20)로 넘어가기 위해
아이템 브랜치(branch0)만 넓혀서 새 체크포인트를 만든다.
그 외 가중치(관측 인코더 · 셀 브랜치 · 회전 브랜치 · 크리틱)는 전부 그대로 복사 —
0~3단계에서 배운 지식(공간 배치 감각 · 셀 선택 · 회전 판단 · 가치 평가)을 최대한 이어받기 위함.
새로 생기는 아이템 슬롯(예: 11~19)만 랜덤 초기화되어 그 부분만 다시 배우면 된다.

사용법:
    python Docs/widen_checkpoint.py --src stage3 --dst stage3_widened --new-items 20

이후:
    mlagents-learn Docs/rl_config_refine.yaml --run-id=stage4 --initialize-from=stage3_widened --force

주의:
    --new-items는 RefinementAgent.cs의 stage4TotalItemsRange.y(기본 20)와 반드시 일치해야 한다.
    Unity 쪽 numItems와 다르면 mlagents가 shape mismatch로 에러를 낸다.
"""
import argparse
import os
import torch

BRANCH0_WEIGHT_KEY = "action_model._discrete_distribution.branches.0.weight"
BRANCH0_BIAS_KEY = "action_model._discrete_distribution.branches.0.bias"
ACT_SIZE_KEY = "discrete_act_size_vector"

# Optimizer:value_optimizer(policy+critic 전체를 관장하는 단일 Adam)의 state는 파라미터 등록 순서로
# 정수 인덱싱된다. 이 프로젝트의 네트워크 구조(관측인코더 2층 + branch0/1/2 + critic)에서 실측 확인한 값:
# idx 10 = branches.0.weight, idx 11 = branches.0.bias. 네트워크 구조가 바뀌면 재확인 필요
# (widen() 안에서 shape로 재검증하니, 어긋나면 assert가 바로 잡아준다).
OPTIMIZER_KEY = "Optimizer:value_optimizer"
BRANCH0_WEIGHT_PARAM_IDX = 10
BRANCH0_BIAS_PARAM_IDX = 11


def widen(src_path, dst_path, new_items, seed):
    ckpt = torch.load(src_path, map_location="cpu", weights_only=False)
    policy = ckpt["Policy"]

    old_w = policy[BRANCH0_WEIGHT_KEY]   # (old_items, hidden)
    old_b = policy[BRANCH0_BIAS_KEY]     # (old_items,)
    old_items, hidden = old_w.shape

    if new_items < old_items:
        raise ValueError(f"new_items({new_items}) < old_items({old_items}) — 넓히기만 지원, 줄이기는 지원 안 함")

    gen = torch.Generator().manual_seed(seed)
    # 새 슬롯은 기존 가중치와 비슷한 스케일로 랜덤 초기화 (기존 값들의 표준편차 참고)
    std_w = old_w.std().item() if old_w.numel() > 0 else 0.02

    new_w = torch.zeros((new_items, hidden), dtype=old_w.dtype)
    new_w[:old_items] = old_w
    if new_items > old_items:
        new_w[old_items:] = torch.randn((new_items - old_items, hidden), generator=gen) * std_w

    new_b = torch.zeros((new_items,), dtype=old_b.dtype)
    new_b[:old_items] = old_b  # 새 슬롯 bias는 0 (표준 관례)

    policy[BRANCH0_WEIGHT_KEY] = new_w
    policy[BRANCH0_BIAS_KEY] = new_b

    old_vec = policy[ACT_SIZE_KEY]
    policy[ACT_SIZE_KEY] = torch.tensor([[float(new_items), old_vec[0, 1].item(), old_vec[0, 2].item()]])

    # ── Adam 옵티마이저의 모멘텀 버퍼(exp_avg/exp_avg_sq)도 같은 파라미터에 대해 widen ──
    # 가중치만 넓히고 이 버퍼를 안 넓히면, --initialize-from 직후 첫 그래디언트 업데이트에서
    # "size of tensor a (old) must match size of tensor b (new)" 로 크래시난다(실제로 겪음).
    # 새로 생기는 슬롯은 0으로 채운다 — Adam 관점에서 "아직 이동 이력 없는 새 파라미터"의 정석 초기값.
    if OPTIMIZER_KEY in ckpt:
        opt_state = ckpt[OPTIMIZER_KEY]["state"]
        for idx in (BRANCH0_WEIGHT_PARAM_IDX, BRANCH0_BIAS_PARAM_IDX):
            if idx not in opt_state:
                continue  # 이 파라미터가 한 번도 업데이트 안 됐으면 state 자체가 없을 수 있음 — 넓힐 것도 없음
            for buf_key in ("exp_avg", "exp_avg_sq"):
                old_buf = opt_state[idx][buf_key]
                if old_buf.dim() == 2:
                    assert old_buf.shape == (old_items, hidden), \
                        f"idx={idx} {buf_key} shape 불일치(예상 {(old_items, hidden)}, 실제 {tuple(old_buf.shape)}) — BRANCH0_*_PARAM_IDX 재확인 필요"
                    new_buf = torch.zeros((new_items, hidden), dtype=old_buf.dtype)
                else:
                    assert old_buf.shape == (old_items,), \
                        f"idx={idx} {buf_key} shape 불일치(예상 {(old_items,)}, 실제 {tuple(old_buf.shape)}) — BRANCH0_*_PARAM_IDX 재확인 필요"
                    new_buf = torch.zeros((new_items,), dtype=old_buf.dtype)
                new_buf[:old_buf.shape[0]] = old_buf
                opt_state[idx][buf_key] = new_buf
        print(f"[widen] 옵티마이저 모멘텀 버퍼(idx {BRANCH0_WEIGHT_PARAM_IDX},{BRANCH0_BIAS_PARAM_IDX})도 {new_items}로 widen 완료")
    else:
        print(f"[widen] 경고: '{OPTIMIZER_KEY}' 없음 — 옵티마이저 상태 widen 건너뜀")

    # --initialize-from은 원래 스텝을 이어받지 않는(=새 런 취급) 의미론이라 0으로 리셋해둔다.
    if "global_step" in ckpt and "_GlobalSteps__global_step" in ckpt["global_step"]:
        ckpt["global_step"]["_GlobalSteps__global_step"] = torch.tensor([0])

    os.makedirs(os.path.dirname(dst_path), exist_ok=True)
    torch.save(ckpt, dst_path)

    print(f"[widen] {src_path} ({old_items}개 아이템) → {dst_path} ({new_items}개 아이템) 저장 완료")
    print(f"[widen] 그대로 복사됨: 관측 인코더 · 셀 브랜치(branch1) · 회전 브랜치(branch2) · 크리틱(value head)")
    print(f"[widen] 새로 랜덤 초기화됨: 아이템 슬롯 {old_items}~{new_items - 1} (기존 0~{old_items - 1}은 학습된 값 그대로)")

    # 저장 직후 재검증
    reload = torch.load(dst_path, map_location="cpu", weights_only=False)
    got_shape = tuple(reload["Policy"][BRANCH0_WEIGHT_KEY].shape)
    assert got_shape == (new_items, hidden), f"검증 실패: 저장된 shape={got_shape}"
    print(f"[widen] 검증 완료: 저장된 파일의 branch0 weight shape = {got_shape}")
    if OPTIMIZER_KEY in reload:
        reload_state = reload[OPTIMIZER_KEY]["state"]
        for idx, label in ((BRANCH0_WEIGHT_PARAM_IDX, "weight"), (BRANCH0_BIAS_PARAM_IDX, "bias")):
            if idx in reload_state:
                s = tuple(reload_state[idx]["exp_avg"].shape)
                print(f"[widen] 검증 완료: 옵티마이저 idx={idx}({label}) exp_avg shape = {s}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="RefinementAgent 체크포인트의 아이템 브랜치(branch0)를 넓혀서 --initialize-from 호환되게 만든다.")
    ap.add_argument("--src", required=True, help="원본 run-id (results/<src>/RefinementAgent/checkpoint.pt)")
    ap.add_argument("--dst", required=True, help="저장할 run-id (results/<dst>/RefinementAgent/checkpoint.pt)")
    ap.add_argument("--new-items", type=int, required=True, help="새 아이템 브랜치 크기 (RefinementAgent.cs의 stage4TotalItemsRange.y와 일치, 기본 20)")
    ap.add_argument("--results-dir", default="results", help="results 폴더 경로 (기본: ./results)")
    ap.add_argument("--seed", type=int, default=0, help="새로 생기는 슬롯 랜덤 초기화 시드")
    args = ap.parse_args()

    src_path = os.path.join(args.results_dir, args.src, "RefinementAgent", "checkpoint.pt")
    dst_path = os.path.join(args.results_dir, args.dst, "RefinementAgent", "checkpoint.pt")
    if not os.path.exists(src_path):
        raise FileNotFoundError(f"원본 체크포인트 없음: {src_path}")

    widen(src_path, dst_path, args.new_items, args.seed)
