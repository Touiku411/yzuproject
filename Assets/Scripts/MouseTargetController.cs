using UnityEngine;
using UnityEngine.InputSystem; // 💡 必須加入這行來呼叫新版輸入系統

public class MouseTargetController : MonoBehaviour
{
    [Header("目標與相機的距離")]
    public float distanceFromCamera = 2.0f;
    public float smoothSpeed = 10f;

    void Update()
    {
        if (Camera.main == null)
        {
            Debug.LogError("⚠️ 找不到 MainCamera！");
            return;
        }

        // 💡 防呆：確認目前系統抓得到滑鼠
        if (Mouse.current == null) return;

        // 💡 改用新版 Input System 的語法抓取滑鼠 2D 座標
        Vector2 mousePos2D = Mouse.current.position.ReadValue();

        if (distanceFromCamera <= 0) distanceFromCamera = 2.0f;

        // 將 2D 座標加上 Z 軸深度轉換為 3D 向量
        Vector3 mouseScreenPos = new Vector3(mousePos2D.x, mousePos2D.y, distanceFromCamera);

        // 轉換座標並平滑移動
        Vector3 targetWorldPos = Camera.main.ScreenToWorldPoint(mouseScreenPos);
        transform.position = Vector3.Lerp(transform.position, targetWorldPos, Time.deltaTime * smoothSpeed);
    }
}