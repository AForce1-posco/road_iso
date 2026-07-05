#!/usr/bin/env bash
# ML-Agents 파이썬 트레이너 환경 설치 (Linux x86_64)
# Unity com.unity.ml-agents 2.0.2 / 2.2.1-exp.1 (통신 API 1.5.0) ↔ 파이썬 mlagents 0.28.0
#
# 맥(arm64)과 차이:
#   - 리눅스는 네이티브 x86_64 → Rosetta / CONDA_SUBDIR 불필요. 그냥 conda env 생성.
#   - torch 1.8.1 리눅스 x86_64 휠 존재(기본은 CUDA 빌드). CPU 전용이면 아래 주석 참고.
#   - cryptography 문제(맥의 OpenSSL 링크)는 리눅스 pip 휠이 OpenSSL 자체 번들이라 대개 안 생김.
#     혹시 tensorboard import 시 OpenSSL 에러 나면 `pip install "cryptography==41.0.7"` 추가.
# 버전 핀·cattrs 패치는 OS 무관하게 동일하게 필요.
#
# 사용: 프로젝트 루트(road_iso)에서
#   bash Docs/setup_mlagents_linux.sh
set -e

ENV=mlagents

echo "=== arch 확인 (x86_64 기대) ==="
uname -m
if [ "$(uname -m)" != "x86_64" ]; then
  echo "⚠️ x86_64가 아님 → torch 1.8.1 휠이 없을 수 있음(맥 arm64와 동일 이슈). 계속하려면 Enter, 중단은 Ctrl+C"
  read _
fi

echo "=== conda 초기화 ==="
source "$(conda info --base)/etc/profile.d/conda.sh"

echo "=== 1) env 생성 (Linux는 CONDA_SUBDIR 불필요) ==="
conda create -n "$ENV" python=3.9.13 -y
conda activate "$ENV"
python -c "import platform,sys; print('arch:',platform.machine(),'| py:',sys.version.split()[0])"

echo "=== 2) mlagents + 버전 고정 ==="
pip install mlagents==0.28.0
# torch가 기본 CUDA 빌드로 깔림. GPU 없이 CPU만 쓸 거면 위 대신 아래처럼:
#   pip install mlagents==0.28.0 torch==1.8.1+cpu -f https://download.pytorch.org/whl/torch_stable.html
pip install "protobuf==3.19.6" "numpy==1.21.2" "tensorboard==2.10.1" six

echo "=== 3) cattrs 제네릭 dispatch 패치 (py3.9 functools 이슈, OS 무관) ==="
python Docs/patch_cattrs_singledispatch.py

echo "=== 4) 검증 ==="
mlagents-learn --help >/dev/null && echo "✅ mlagents-learn OK"
python -c "from torch.utils.tensorboard import SummaryWriter; print('✅ tensorboard OK')"

echo ""
echo "완료. 사용 시: conda activate $ENV"
