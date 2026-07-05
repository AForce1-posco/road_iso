"""
mlagents 0.28.0 (Unity ml-agents 2.0.2용) + Python 3.9.13(conda) 호환 패치.

문제: mlagents/trainers/settings.py 가 cattr.register_structure_hook(Dict[...], func) 를 호출하는데,
cattrs(1.5.0)가 이 제네릭 타입을 functools.singledispatch.register 로 보낸다. 그런데 conda의
Python 3.9.13 functools 에는 _is_valid_dispatch_type 검증이 들어 있어 typing.Dict[...] 를 거부
→ "TypeError: Invalid first argument to register(). typing.Dict[...] is not a class." 로 import 자체가 실패.

해결: cattr/dispatch.py 의 register_cls_list 에서 single_dispatch 가 TypeError 를 내면
이미 존재하는 술어(predicate) 기반 function-dispatch 로 폴백하도록 한 줄 감싼다. 멱등(여러 번 실행 안전).

사용:
    conda activate mlagents-x86
    python Docs/patch_cattrs_singledispatch.py
"""
import cattr.dispatch as d
import io, os, re

path = d.__file__
src = io.open(path, encoding="utf-8").read()

MARKER = "# --- mlagents/py3.9.13 generic-alias fallback patch ---"
if MARKER in src:
    print("이미 패치됨:", path)
    raise SystemExit(0)

old = (
    "            else:\n"
    "                self._single_dispatch.register(cls, handler)\n"
    "                self.clear_direct()\n"
)
new = (
    "            else:\n"
    "                " + MARKER + "\n"
    "                try:\n"
    "                    self._single_dispatch.register(cls, handler)\n"
    "                except TypeError:\n"
    "                    # typing.Dict[...] 같은 제네릭은 functools 가 거부 → 술어 dispatch 로 폴백\n"
    "                    self._function_dispatch.register(\n"
    "                        (lambda c: (lambda t: t == c))(cls), handler\n"
    "                    )\n"
    "                self.clear_direct()\n"
)

if old not in src:
    raise SystemExit("패치 대상 코드를 못 찾음 (cattrs 버전이 다를 수 있음): " + path)

io.open(path, "w", encoding="utf-8").write(src.replace(old, new, 1))
print("패치 완료:", path)
