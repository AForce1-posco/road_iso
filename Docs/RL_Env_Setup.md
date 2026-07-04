# RL 학습 파이썬 환경 셋업 (Apple Silicon / macOS arm64)

Unity `com.unity.ml-agents 2.0.2`(통신 API 1.5.0)와 짝이 맞는 파이썬 트레이너는 **mlagents 0.28.0**이다.
이건 `torch 1.8`(arm64 네이티브 휠 없음) + 구버전 protobuf/numpy를 요구해서, Apple Silicon에서 그냥 설치하면 줄줄이 깨진다.
아래는 **검증 완료된 정확한 레시피**다. (Unity 2020.3라 ml-agents 패키지 최신화 = 불가 → 이 조합에 묶임)

## 확정 버전 조합 (동작 확인 2026-07-05)

| 패키지 | 버전 | 이유 |
|---|---|---|
| arch | **x86_64 (Rosetta)** | torch 1.8은 arm64 휠 없음 → x86 휠 + Rosetta2 실행 |
| python | **3.9.13** | mlagents 0.28은 3.7~3.9만 |
| mlagents | **0.28.0** | Unity 2.0.2(API 1.5.0)의 짝 |
| torch | 1.8.1 | mlagents 0.28 강제 |
| numpy | **1.21.2** | 1.24+는 `np.float` 삭제 → mlagents buffer.py 깨짐 |
| protobuf | **3.19.6** | mlagents 생성 _pb2 코드가 3.x 필요 |
| tensorboard | **2.10.1** | 2.12+는 protobuf 6.x 요구 → 충돌 |
| six | 1.17.0 | torch.utils.tensorboard가 사용 |
| cryptography | **41.0.7** | 최신(49.x)은 OpenSSL 3 요구 → env는 OpenSSL 1.1.1w라 `_ERR_get_error_all` 심볼 없음 → tensorboard import 실패. 1.1.1 지원 버전으로 내림 |
| cattr(s) | 1.5.0 + **패치** | 아래 참조 |

## 설치 순서

```bash
source /opt/anaconda3/etc/profile.d/conda.sh

# 1) x86_64 env 생성 (Rosetta)
CONDA_SUBDIR=osx-64 conda create -n mlagents-x86 python=3.9.13 -y
conda activate mlagents-x86
conda config --env --set subdir osx-64        # 이 env는 계속 x86로 고정
python -c "import platform;print(platform.machine())"   # x86_64 확인

# 2) mlagents + 버전 고정
pip install mlagents==0.28.0
pip install "protobuf==3.19.6" "numpy==1.21.2" "tensorboard==2.10.1" six
pip install "cryptography==41.0.7"   # tensorboard 실행용 (49.x는 OpenSSL 3 요구 → 깨짐)

# 3) cattrs 제네릭 dispatch 패치 (아래 설명)
python Docs/patch_cattrs_singledispatch.py

# 4) 검증
mlagents-learn --help        # 정상 출력되면 OK
```

## cattrs 패치가 필요한 이유

`mlagents/trainers/settings.py`가 `cattr.register_structure_hook(Dict[...], func)`를 호출 →
cattrs 1.5.0이 이 제네릭을 `functools.singledispatch.register`로 보냄 → **conda의 Python 3.9.13
functools에 `_is_valid_dispatch_type` 검증이 있어 `typing.Dict[...]`를 거부**
→ `TypeError: Invalid first argument to register(). typing.Dict[...] is not a class.` 로 import 실패.

`Docs/patch_cattrs_singledispatch.py`가 cattrs의 `register_cls_list`에서 그 등록이 TypeError를 내면
이미 존재하는 술어(predicate) 기반 function-dispatch로 폴백하게 한다. **멱등**(재실행 안전).
env를 다시 만들면 이 패치도 다시 돌려야 한다 (site-packages라 재설치 시 사라짐).

## 학습 실행 (다음 단계 S4)

```bash
conda activate mlagents-x86
mlagents-learn Docs/rl_config.yaml --run-id=placement_v1
# ↑ "Start training by pressing the Play button" 뜨면 Unity에서 RLTraining 씬 Play
#   (Behavior Type = Default 로 바꿔야 트레이너와 연결됨. Heuristic Only면 연결 안 함)
```

- 결과: `results/<run-id>/PlacementAgent.onnx` → Behavior Parameters의 Model에 넣으면 학습된 배치 재생.
- 곡선 비교: `tensorboard --logdir results`
