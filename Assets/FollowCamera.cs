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

        Vector3 dynamicOffset =
            new Vector3(offset.x, offset.y, back);

        Vector3 desiredPos =
            target.TransformPoint(dynamicOffset);

        transform.position =
            Vector3.Lerp(
                transform.position,
                desiredPos,
                followSmooth * Time.deltaTime);

        // 트럭 앞쪽을 바라봄
        Vector3 lookTarget =
            target.position +
            target.forward * lookAheadDistance;

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