using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(VehicleController))]
public class PurePursuitController : MonoBehaviour
{
    [Header("References")]
    public RoadPathGenerator roadPath;

    [Header("ISO Switch")]
    public float isoSwitchDistance = 15f;

    [Header("Normal Driving")]
    public float minLookAhead = 2.5f;
    public float maxLookAhead = 7f;
    public float lookAheadSpeedFactor = 0.07f;
    public float targetSpeedKmh = 65f;
    public float curveSlowSpeedKmh = 38f;

    [Header("ISO Driving")]
    public float isoMinLookAhead = 2.0f;
    public float isoMaxLookAhead = 5.0f;
    public float isoTargetSpeedKmh = 45f;
    public float isoCurveSpeedKmh = 35f;

    [Header("Control")]
    public float throttleGain = 0.04f;
    public float brakeGain = 0.05f;
    public float steeringSensitivity = 2.2f;
    public float wheelBase = 3.2f;

    [Header("Stop")]
    public int stopBeforeLastPoints = 5;
    public float stopDistance = 8f;

    private VehicleController vehicle;
    private List<Vector3> currentPath;
    private int nearestIndex = 0;
    private bool usingISO = false;
    private bool finished = false;

    /// <summary>ISO 구간 종료 후 정지 완료 여부 (기록 종료 신호).</summary>
    public bool Finished => finished;
    /// <summary>ISO 경로 주행 중인지 (위험 구간 = 기록 핵심 구간).</summary>
    public bool UsingISO => usingISO;

    /// <summary>배치 일괄 실행용: 다음 케이스 주행 전에 경로 추종 상태 초기화.</summary>
    public void Restart()
    {
        nearestIndex = 0;
        usingISO = false;
        finished = false;
    }

    void Start()
    {
        vehicle = GetComponent<VehicleController>();
    }

    void FixedUpdate()
    {
        if (finished)
        {
            vehicle.SetInput(0f, 0f, 1f);
            return;
        }

        if (roadPath == null)
            return;

        CheckSwitchToISO();

        currentPath = usingISO ? roadPath.isoPoints : roadPath.roadPoints;

        if (currentPath == null || currentPath.Count < 5)
            return;

        nearestIndex = FindNearestIndex(currentPath, nearestIndex);

        if (usingISO && ShouldStopAtISOEnd())
        {
            vehicle.SetInput(0f, 0f, 1f);
            finished = true;
            return;
        }

        float lookAhead = GetLookAhead();
        int targetIndex = FindLookAheadIndex(currentPath, nearestIndex, lookAhead);
        Vector3 targetPoint = currentPath[targetIndex];

        float steerInput = CalculateSteerInput(targetPoint);
        float desiredSpeed = CalculateDesiredSpeed(currentPath, nearestIndex);

        ApplySpeedControl(desiredSpeed, steerInput);
    }

    void CheckSwitchToISO()
    {
        if (usingISO)
            return;

        if (roadPath.isoPoints == null || roadPath.isoPoints.Count == 0)
            return;

        Vector3 isoStart = roadPath.isoPoints[0];
        float dist = XZDistance(transform.position, isoStart);

        if (dist < isoSwitchDistance)
        {
            usingISO = true;
            nearestIndex = 0;
        }
    }

    bool ShouldStopAtISOEnd()
    {
        int stopIndex = Mathf.Max(0, currentPath.Count - stopBeforeLastPoints);

        if (nearestIndex >= stopIndex)
            return true;

        Vector3 endPoint = currentPath[currentPath.Count - 1];
        float distToEnd = XZDistance(transform.position, endPoint);

        if (distToEnd < stopDistance)
            return true;

        return false;
    }

    float GetLookAhead()
    {
        if (usingISO)
        {
            return Mathf.Clamp(
                isoMinLookAhead + vehicle.CurrentSpeedKmh * 0.05f,
                isoMinLookAhead,
                isoMaxLookAhead
            );
        }

        return Mathf.Clamp(
            minLookAhead + vehicle.CurrentSpeedKmh * lookAheadSpeedFactor,
            minLookAhead,
            maxLookAhead
        );
    }

    int FindNearestIndex(List<Vector3> path, int startIndex)
    {
        int bestIndex = startIndex;
        float bestDist = float.MaxValue;

        int start = Mathf.Max(0, startIndex - 20);
        int end = Mathf.Min(path.Count - 1, startIndex + 120);

        for (int i = start; i <= end; i++)
        {
            float d = XZDistanceSqr(transform.position, path[i]);

            if (d < bestDist)
            {
                bestDist = d;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    int FindLookAheadIndex(List<Vector3> path, int startIndex, float lookAhead)
    {
        float dist = 0f;
        int index = startIndex;

        while (dist < lookAhead && index < path.Count - 1)
        {
            dist += Vector3.Distance(path[index], path[index + 1]);
            index++;
        }

        return index;
    }

    float CalculateSteerInput(Vector3 targetPoint)
    {
        Vector3 localTarget = transform.InverseTransformPoint(targetPoint);

        if (localTarget.z < 0.1f)
            return 0f;

        float curvature = (2f * localTarget.x) / localTarget.sqrMagnitude;
        float steerAngleRad = Mathf.Atan(curvature * wheelBase);
        float steerAngleDeg = steerAngleRad * Mathf.Rad2Deg;

        // 정규화 기준은 반드시 "지금 속도에서 실제로 낼 수 있는 조향각"이어야 함.
        // 예전엔 고정된 vehicle.maxSteerAngle(저속 기준)로 나눠서, 고속에서 조향각이
        // 줄어든 만큼 steerInput이 실제보다 작게 나와 급커브에서 조향이 덜 들어갔음.
        float steerInput = steerAngleDeg / vehicle.GetSteerLimitDeg();

        return Mathf.Clamp(steerInput * steeringSensitivity, -1f, 1f);
    }

    float CalculateDesiredSpeed(List<Vector3> path, int index)
    {
        float baseSpeed = usingISO ? isoTargetSpeedKmh : targetSpeedKmh;
        float slowSpeed = usingISO ? isoCurveSpeedKmh : curveSlowSpeedKmh;

        int i0 = index;
        int i1 = Mathf.Min(index + 8, path.Count - 1);
        int i2 = Mathf.Min(index + 16, path.Count - 1);

        Vector3 a = path[i0];
        Vector3 b = path[i1];
        Vector3 c = path[i2];

        Vector2 ab = new Vector2(b.x - a.x, b.z - a.z).normalized;
        Vector2 bc = new Vector2(c.x - b.x, c.z - b.z).normalized;

        float angle = Vector2.Angle(ab, bc);
        float t = Mathf.Clamp01(angle / 30f);

        return Mathf.Lerp(baseSpeed, slowSpeed, t);
    }

    void ApplySpeedControl(float desiredSpeed, float steerInput)
    {
        float error = desiredSpeed - vehicle.CurrentSpeedKmh;

        float throttle = Mathf.Clamp01(error * throttleGain);
        float brake = Mathf.Clamp01(-error * brakeGain);

        vehicle.SetInput(steerInput, throttle, brake);
    }

    float XZDistance(Vector3 a, Vector3 b)
    {
        return Vector2.Distance(
            new Vector2(a.x, a.z),
            new Vector2(b.x, b.z)
        );
    }

    float XZDistanceSqr(Vector3 a, Vector3 b)
    {
        return Vector2.SqrMagnitude(
            new Vector2(a.x, a.z) - new Vector2(b.x, b.z)
        );
    }
}