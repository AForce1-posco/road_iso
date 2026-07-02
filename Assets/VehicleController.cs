using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class VehicleController : MonoBehaviour
{
    [Header("Wheel Colliders")]
    public WheelCollider frontLeft;
    public WheelCollider frontRight;
    public WheelCollider rearLeft;
    public WheelCollider rearRight;

    [Header("Wheel Meshes")]
    public Transform frontLeftMesh;
    public Transform frontRightMesh;
    public Transform rearLeftMesh;
    public Transform rearRightMesh;

    [Header("Drive")]
    public float maxMotorTorque = 3500f;
    public float brakeTorque = 6000f;
    public float maxSpeedKmh = 70f;

    [Header("Steering")]
    public float maxSteerAngle = 32f;
    public float minSteerAngleAtHighSpeed = 10f;
    public float steerSpeed = 90f;

    [Header("Vehicle Body")]
    public Vector3 centerOfMass = new Vector3(0f, -0.8f, 0f);

    [HideInInspector] public float targetSteer = 0f;      // -1 ~ 1
    [HideInInspector] public float targetThrottle = 1f;   // 0 ~ 1
    [HideInInspector] public float targetBrake = 0f;      // 0 ~ 1

    private Rigidbody rb;
    private float currentSteer;

    public float CurrentSpeedKmh { get; private set; }
    public float CurrentSteerAngle { get; private set; }

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = 3500f;
        rb.centerOfMass = centerOfMass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    void FixedUpdate()
    {
        UpdateSpeed();
        ApplySteering();
        ApplyDrive();
        UpdateWheelVisuals();
    }

    void UpdateSpeed()
    {
        CurrentSpeedKmh = rb.velocity.magnitude * 3.6f;
    }

    void ApplySteering()
    {
        float speedRatio = Mathf.Clamp01(CurrentSpeedKmh / maxSpeedKmh);

        float steerLimit = Mathf.Lerp(
            maxSteerAngle,
            minSteerAngleAtHighSpeed,
            speedRatio
        );

        float desiredSteer = Mathf.Clamp(targetSteer, -1f, 1f) * steerLimit;

        currentSteer = Mathf.MoveTowards(
            currentSteer,
            desiredSteer,
            steerSpeed * Time.fixedDeltaTime
        );

        frontLeft.steerAngle = currentSteer;
        frontRight.steerAngle = currentSteer;

        CurrentSteerAngle = currentSteer;
    }

    void ApplyDrive()
    {
        float throttle = Mathf.Clamp01(targetThrottle);
        float brake = Mathf.Clamp01(targetBrake);

        float torque = 0f;

        if (CurrentSpeedKmh < maxSpeedKmh)
            torque = throttle * maxMotorTorque;

        rearLeft.motorTorque = torque;
        rearRight.motorTorque = torque;

        float brakePower = brake * brakeTorque;

        if (throttle < 0.05f && brake < 0.05f)
            brakePower = brakeTorque * 0.2f;

        frontLeft.brakeTorque = brakePower;
        frontRight.brakeTorque = brakePower;
        rearLeft.brakeTorque = brakePower;
        rearRight.brakeTorque = brakePower;
    }

    void UpdateWheelVisuals()
    {
        UpdateSingleWheel(frontLeft, frontLeftMesh);
        UpdateSingleWheel(frontRight, frontRightMesh);
        UpdateSingleWheel(rearLeft, rearLeftMesh);
        UpdateSingleWheel(rearRight, rearRightMesh);
    }

    void UpdateSingleWheel(WheelCollider col, Transform mesh)
    {
        if (col == null || mesh == null) return;

        col.GetWorldPose(out Vector3 pos, out Quaternion rot);
        mesh.position = pos;
        mesh.rotation = rot;
    }

    public void SetInput(float steer, float throttle, float brake)
    {
        targetSteer = Mathf.Clamp(steer, -1f, 1f);
        targetThrottle = Mathf.Clamp01(throttle);
        targetBrake = Mathf.Clamp01(brake);
    }
}