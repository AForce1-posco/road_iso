using UnityEngine;

public struct PlacementEpisodeMetrics
{
    public int episodeIndex;
    public string reason;
    public float totalReward;
    public int validPlacements;
    public int invalidPlacements;
    public int stepCount;
    public float stepScale;
    public float wLE;
    public float wCGS;
    public float wSS;
    public float supportRatioMin;
}
