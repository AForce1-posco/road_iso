using System.Collections.Generic;

/// <summary>
/// PlacementVisualizer가 PlacementAgent/RefinementAgent 어느 쪽에 붙어도 똑같이 동작하도록
/// 필요한 최소 정보만 뽑아낸 인터페이스. 두 에이전트 모두 아래 4개를 이미 갖고 있어서
/// (필드는 그대로 두고) 아래 프로퍼티 3개만 추가하면 별도 로직 변경 없이 만족된다.
/// </summary>
public interface IPlacedCargoView
{
    RuleConfig RuleConfig { get; }
    int Cols { get; }
    int Rows { get; }
    IReadOnlyList<RuleChecker.PlacedItem> PlacedItems { get; }
}
