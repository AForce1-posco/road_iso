using UnityEngine;

public class FollowCamera : MonoBehaviour
{
    public Transform target;
    public Rigidbody targetRb;

    [Header("Base Offset")]
    public Vector3 offset = new Vector3(0f, 4.5f, -8f);

    [Header("Follow")]
    public float followSmooth = 6f;

    [Header("Rotation")]
    public float rotationSmooth = 4f;

    [Header("Look Ahead")]
    public float lookAheadDistance = 8f;

    [Header("Speed Camera")]
    public float maxBackOffset = -14f;
    public float speedFactor = 0.35f;

    public enum TruckFramePosition { Center, TruckOnLeft, TruckOnRight }

    [Header("발표용 화면 프레이밍")]
    [Tooltip("Center=화면 중앙, TruckOnLeft=트럭 왼쪽(오른쪽에 UI 자리), TruckOnRight=트럭 오른쪽(왼쪽에 UI 자리)")]
    public TruckFramePosition framePosition = TruckFramePosition.Center;
    [Tooltip("카메라 자체를 옆으로 얼마나 이동시킬지 (월드 단위)")]
    public float cameraSideShift = 4f;
    [Tooltip("조준점(바라보는 지점)도 같이 옆으로 밀어서 효과를 더 뚜렷하게 함")]
    public float aimSideShift = 3f;

    Camera cam;

    void Start()
    {
        cam = GetComponent<Camera>();
    }

    void LateUpdate()
    {
        if (target == null)
            return;

        float speed = 0f;

        if (targetRb != null)
            speed = targetRb.velocity.magnitude;

        // 속도에 따라 카메라 뒤로 이동
        float back =
            Mathf.Lerp(
                offset.z,
                maxBackOffset,
                Mathf.Clamp01(speed * speedFactor / 20f));

        // 프레이밍 방향에 따른 좌우 이동량 계산
        // TruckOnLeft: 트럭이 화면 왼쪽 → 카메라를 트럭 기준 오른쪽으로 이동 (+)
        // TruckOnRight: 트럭이 화면 오른쪽 → 카메라를 트럭 기준 왼쪽으로 이동 (-)
        float sideSign = 0f;
        if (framePosition == TruckFramePosition.TruckOnLeft) sideSign = 1f;
        else if (framePosition == TruckFramePosition.TruckOnRight) sideSign = -1f;

        Vector3 dynamicOffset =
            new Vector3(offset.x + sideSign * cameraSideShift, offset.y, back);

        Vector3 desiredPos =
            target.TransformPoint(dynamicOffset);

        transform.position =
            Vector3.Lerp(
                transform.position,
                desiredPos,
                followSmooth * Time.deltaTime);

        // 트럭 앞쪽을 바라봄 (프레이밍용으로 조준점도 같이 옆으로 살짝 이동)
        Vector3 lookTarget =
            target.position +
            target.forward * lookAheadDistance +
            target.right * (sideSign * aimSideShift);

        Quaternion targetRot =
            Quaternion.LookRotation(
                lookTarget - transform.position,
                Vector3.up);

        transform.rotation =
            Quaternion.Slerp(
                transform.rotation,
                targetRot,
                rotationSmooth * Time.deltaTime);

        // 속도에 따른 FOV
        if (cam != null)
        {
            float targetFOV =
                Mathf.Lerp(60f, 75f,
                Mathf.Clamp01(speed / 20f));

            cam.fieldOfView =
                Mathf.Lerp(
                    cam.fieldOfView,
                    targetFOV,
                    2f * Time.deltaTime);
        }
    }
}