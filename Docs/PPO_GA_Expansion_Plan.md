# PPO + Action Masking + GA 확장 계획

## Phase 1: 기본 연결 확인
목표:
- PlacementAgent가 RewardConfig / RuleConfig를 외부에서 주입받는다.
- GA optimizer가 후보 파라미터를 적용한다.
- 콘솔에서 후보/적용 결과가 보인다.

중간 평가 기준:
- GA optimizer가 실행된다.
- PlacementAgent에 파라미터가 반영된다.
- 콘솔 로그가 정상적으로 출력된다.

---

## Phase 2: PPO 학습 신호 확인
목표:
- RewardCalculator가 실제로 의미 있는 보상을 내보낸다.
- Action masking이 불가능 행동을 차단한다.
- 에피소드가 정상 종료된다.

확장 내용:
- PlacementAgent에서 episode 단위 메트릭을 수집한다.
- Reward/constraint가 실제로 학습 가능한 형태인지 확인한다.
- PlacementEvaluationLogger로 중간 지표를 로깅한다.

중간 평가 기준:
- 에피소드가 정상 종료된다.
- invalidCount가 증가하지 않거나, 증가해도 이유가 명확하다.
- reward가 양/음수 모두 나오는지 확인한다.

---

## Phase 3: constraint 강화
목표:
- RuleChecker 기반 제약이 action masking과 일치한다.
- 불가능 행동은 거의 선택되지 않는다.

확장 내용:
- 마스킹 조건을 RuleChecker 기준으로 더 엄격하게 맞춘다.
- 후보 배치가 불가능할 때 reward penalty가 즉시 적용되도록 확인한다.

중간 평가 기준:
- 잘못된 행동 선택이 크게 줄어든다.
- invalid action이 거의 없거나, 있더라도 명확히 추적된다.

---

## Phase 4: GA + PPO 통합 평가
목표:
- PPO 학습 성능과 GA 파라미터 탐색이 함께 작동한다.
- 보상 파라미터(stepScale, wLE, wCGS, wSS)와 규제 파라미터(supportRatioMin)가 성능에 영향을 준다.

확장 내용:
- GA가 후보를 만들고, 각 후보를 짧은 평가로 테스트한다.
- fitness를 reward/valid ratio/invalid ratio로 계산한다.

최종 평가 기준:
- 유효 배치 비율이 높다.
- invalid action이 낮다.
- reward가 안정적으로 증가한다.
- action masking이 제약을 잘 지킨다.
