using UnityEngine;

/// <summary>
/// TextMesh 라벨이 항상 카메라를 정면으로 바라보게(빌보드) 해서 좌우 뒤집힘(거울상) 없이 읽히게 함.
/// cam 을 비우면 Camera.main 사용.
/// </summary>
public class LabelBillboard : MonoBehaviour
{
    public Camera cam;

    void LateUpdate()
    {
        Camera c = cam != null ? cam : Camera.main;
        if (c != null) transform.rotation = c.transform.rotation;
    }
}
