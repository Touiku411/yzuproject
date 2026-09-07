using UnityEngine;

[RequireComponent(typeof(Animator))]
public class SimpleHeadLookAt : MonoBehaviour
{
    [Header("注視目標")]
    public Transform lookAtTarget;

    [Header("視線權重設定 (0~1)")]
    [Range(0f, 1f)] public float overallWeight = 1f;  // 總權重
    [Range(0f, 1f)] public float bodyWeight = 0.1f;   // 身體跟隨程度 (微幅)
    [Range(0f, 1f)] public float headWeight = 0.8f;   // 頭部轉動程度
    [Range(0f, 1f)] public float eyesWeight = 1.0f;   // 眼睛轉動程度
    [Range(0f, 1f)] public float clampWeight = 0.6f;  // 轉動極限 (數字越大，脖子能轉的角度越小，防扭斷)

    private Animator animator;

    void Start()
    {
        animator = GetComponent<Animator>();
    }

    // OnAnimatorIK 會在每幀的動畫計算完畢後自動被呼叫
    void OnAnimatorIK(int layerIndex)
    {
        if (animator != null && lookAtTarget != null)
        {
            // 1. 設定各部位跟隨目標的權重
            animator.SetLookAtWeight(overallWeight, bodyWeight, headWeight, eyesWeight, clampWeight);

            // 2. 告訴 Animator 目標的空間座標在哪裡
            animator.SetLookAtPosition(lookAtTarget.position);
        }
        else if (animator != null)
        {
            // 如果沒有目標，就把權重歸零，回到原始動畫狀態
            animator.SetLookAtWeight(0);
        }
    }
}