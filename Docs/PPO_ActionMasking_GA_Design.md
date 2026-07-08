# PPO + Action Masking + Genetic Algorithm 설계안

## 1. 목표

이 프로젝트에서 정적 화물 배치 최적화를 다음 두 층으로 분리한다.

1. PPO 레이어
   - 배치 정책(what/where/rotation)을 학습한다.
   - 상태는 PlacementAgent가 제공한다.
   - 행동은 discrete action space로 정의한다.
   - 불가능한 행동은 action masking으로 차단한다.

2. GA 레이어
   - PPO의 학습 하이퍼파라미터와 reward/규제 가중치를 자동 탐색한다.
   - 후보 해(Genome)를 생성하고, 각 후보에 대해 짧은 학습/평가를 실행한다.
   - 성능이 높은 후보만 유지하면서 세대를 진화시킨다.

---

## 2. 핵심 구조

### A. PPO 학습 환경
- 환경 엔티티: PlacementAgent
- 상태(state): 현재 적재 높이맵, CoG, 총질량, 남은 화물 목록 등
- 행동(action): (itemType, cellIndex, rotation)
- 제약(constraint): RuleChecker로 검사
- 보상(reward): RewardCalculator로 계산

### B. GA 최적화 엔진
- Genome = PPO 하이퍼파라미터 + reward 가중치 + 규제 파라미터
- 예시 변수
  - learning_rate
  - batch_size
  - buffer_size
  - num_epoch
  - beta
  - epsilon
  - gamma
  - reward.wLE
  - reward.wCGS
  - reward.wSS
  - rule.supportRatioMin
  - rule.maxPayloadKg

### C. 평가 함수(Fitness)
각 후보는 짧은 학습/평가 후 아래 지표로 점수를 받는다.
- 유효 배치 비율
- 평균 최종 reward
- 평균 Stability Margin
- 평균 LTR 감소
- 규제 위반 수

Fitness 예시:

```text
fitness = 0.4 * mean_reward
        + 0.2 * valid_ratio
        + 0.2 * stability_score
        + 0.1 * cg_centrality
        + 0.1 * packing_score
```

---

## 3. 파일별 변경 포인트

### 3.1 기존 파일 유지/확장

#### [Assets/Scripts/Static/PlacementAgent.cs]
역할:
- state 생성
- action 생성
- action masking
- reward 부여
- episode 종료 처리

변경 사항:
- `WriteDiscreteActionMask()`는 이미 있는 로직을 더 명확히 유지한다.
- `RewardConfig`/`RuleConfig`를 외부에서 주입받을 수 있게 한다.
- 학습 모드와 평가 모드 구분 가능하게 한다.
- `OnEpisodeBegin()`에서 랜덤 manifest와 환경 초기화를 한다.

#### [Assets/Scripts/Static/RuleChecker.cs]
역할:
- 제약 조건 검사

변경 사항:
- `RuleConfig`를 외부에서 조정 가능하게 유지한다.
- GA 후보가 바뀌어도 바로 반영되도록 `RuleConfig` 객체를 주입 가능하게 한다.

#### [Assets/Scripts/Static/RewardCalculator.cs]
역할:
- reward 계산

변경 사항:
- `RewardConfig`를 외부 파라미터로 받는다.
- GA가 튜닝한 가중치가 코드로 바로 반영되도록 한다.

---

### 3.2 새로 추가할 파일

#### 1) [Assets/Scripts/Static/Training/PlacementTrainingConfig.cs]
PPO 학습 설정을 담는 클래스.

```csharp
[System.Serializable]
public class PlacementTrainingConfig
{
    public float learningRate = 3.0e-4f;
    public int batchSize = 128;
    public int bufferSize = 4096;
    public int numEpoch = 3;
    public float beta = 0.005f;
    public float epsilon = 0.2f;
    public float gamma = 0.99f;

    public string ToYamlText() { /* YAML 생성 */ }
}
```

역할:
- GA가 탐색한 하이퍼파라미터를 저장한다.
- `config_ppo.yaml` 템플릿을 생성한다.

#### 2) [Assets/Scripts/Static/Training/PlacementGenome.cs]
GA 개체(유전자) 정의.

```csharp
public class PlacementGenome
{
    public PlacementTrainingConfig training;
    public RewardConfig reward;
    public RuleConfig rule;
    public float fitness;
}
```

#### 3) [Assets/Scripts/Static/Training/PlacementGAOptimizer.cs]
GA 최적화 엔진.

기능:
- 초기 population 생성
- 각 genome 평가
- 부모 선택
- crossover / mutation
- next generation 생성

간단한 흐름:

```csharp
public class PlacementGAOptimizer : MonoBehaviour
{
    public int populationSize = 20;
    public int generations = 20;
    public float mutationRate = 0.2f;

    void Start()
    {
        var population = InitializePopulation();
        for (int g = 0; g < generations; g++)
        {
            EvaluatePopulation(population);
            population = Evolve(population);
        }
    }
}
```

#### 4) [Assets/Scripts/Static/Training/PlacementEvaluationRunner.cs]
각 genome에 대해 짧은 실험을 실행하는 평가기.

기능:
- 테스트 시나리오 세트 로드
- PlacementAgent를 설정
- `trainingConfig`, `rewardConfig`, `ruleConfig`를 주입
- 짧은 학습/평가 실행
- 결과 수집 후 fitness 반환

---

## 4. PPO와 GA가 어떻게 연결되는가

### 단계 1: GA가 genome 생성
예시:
- genome 1: learning_rate=3e-4, wLE=0.5, wCGS=0.4, supportRatioMin=0.7
- genome 2: learning_rate=1e-4, wLE=0.6, wCGS=0.3, supportRatioMin=0.8

### 단계 2: 해당 genome에 맞는 설정 생성
- `PlacementTrainingConfig` 생성
- `RewardConfig` 생성
- `RuleConfig` 생성
- YAML 파일 생성

### 단계 3: 짧은 PPO 학습 실행
- `mlagents-learn config_ppo_generated.yaml --run-id genome_001`
- 또는 Unity에서 짧은 에피소드 평가만 수행

### 단계 4: fitness 측정
- 유효 배치 비율
- 최종 reward 평균
- 안정성 지표 평군

### 단계 5: GA가 다음 후보 생성
- 우수한 genome를 남기고
- crossover/mutation으로 다음 세대 생성

---

## 5. Action Masking은 어디서 쓰나

Action masking은 이미 [Assets/Scripts/Static/PlacementAgent.cs] 의 `WriteDiscreteActionMask()`에서 처리하는 구조가 가장 적합하다.

이 부분은 그대로 유지하고, GA가 바꿀 필요는 없다.

다만 다음 두 가지를 더 명확히 하면 좋다.

1. 마스킹 조건을 별도 헬퍼로 분리
   - `CanPlaceItem(...)`
   - `IsCellOccupied(...)`
   - `IsValidHeight(...)`

2. RuleChecker와 연결
   - 마스킹은 RuleChecker의 판정과 동일한 기준을 써서, 실제 가능한 행동과 학습 가능한 행동을 일치시킨다.

이렇게 하면 PPO가 불가능한 행동을 거의 선택하지 않게 되어 학습 효율이 좋아진다.

---

## 6. 구현 우선순위

### Phase 1: 구조 연결
1. `PlacementAgent`가 `RewardConfig`/`RuleConfig`를 외부 주입받도록 수정
2. `RewardCalculator`/`RuleChecker`가 해당 설정을 사용하도록 수정
3. 기존 action masking 유지

### Phase 2: GA 기본 구현
1. `PlacementGenome` 정의
2. `PlacementGAOptimizer` 기본 population 생성
3. `PlacementEvaluationRunner`가 하나의 genome에 대한 평가를 수행

### Phase 3: YAML 생성 자동화
1. `PlacementTrainingConfig.ToYamlText()` 구현
2. `mlagents-learn` 호출용 템플릿 생성

### Phase 4: 학습 루프 고도화
1. 여러 genome를 병렬로 평가
2. 체크포인트/로그 저장
3. 최적 genome를 자동으로 저장

---

## 7. 추천 시작점

처음에는 다음 5개만 GA로 튜닝하는 것이 가장 현실적이다.

- `wLE`
- `wCGS`
- `wSS`
- `supportRatioMin`
- `learning_rate`

이 5개만으로도 효과를 보기 좋다.

---

## 8. 한 줄 요약

- PPO는 배치 정책 학습에 사용한다.
- Action masking은 PlacementAgent의 `WriteDiscreteActionMask()`에서 유지/강화한다.
- Genetic Algorithm은 reward 가중치, RuleChecker 파라미터, PPO 하이퍼파라미터를 자동 탐색하는 용도로 붙인다.
- 가장 먼저 바꿔야 할 파일은 PlacementAgent, RewardCalculator, RuleChecker, 그리고 새로 추가할 Training/GA 관련 파일들이다.
