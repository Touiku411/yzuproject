using UnityEngine;

public class CompassionateExpression : MonoBehaviour
{
    [Header("臉部網格與狀態")]
    public SkinnedMeshRenderer faceMesh;
    public Animator bodyAnimator;

    [Header("表情設定 (ARKit)")]
    [Tooltip("慈悲微笑的強度 (建議 5~15 之間，太大會像在假笑)")]
    public float idleSmileWeight = 8f;
    [Tooltip("表情切換的平滑度")]
    public float transitionSpeed = 3f;

    private int smileLeftIndex = -1;
    private int smileRightIndex = -1;
    private float currentSmileWeight = 0f;

    void Start()
    {
        if (faceMesh != null)
        {
            // 自動尋找正確的表情編號
            smileLeftIndex = GetBlendShapeIndex("MouthSmileLeft");
            smileRightIndex = GetBlendShapeIndex("MouthSmileRight");
        }
    }

    private int GetBlendShapeIndex(string name)
    {
        int index = faceMesh.sharedMesh.GetBlendShapeIndex(name);
        if (index == -1)
        {
            string lowerName = char.ToLower(name[0]) + name.Substring(1);
            index = faceMesh.sharedMesh.GetBlendShapeIndex(lowerName);
        }
        return index;
    }

    void LateUpdate()
    {
        if (faceMesh == null || smileLeftIndex == -1 || smileRightIndex == -1) return;

        // 1. 判斷大師是否正在說話 (根據 Animator 的狀態機)
        bool isTalking = false;
        if (bodyAnimator != null)
        {
            isTalking = bodyAnimator.GetBool("IsTalking");
        }

        // 2. 如果沒有在說話，目標微笑值為設定值；如果在說話，把控制權還給 A2F (目標為 0)
        float targetWeight = isTalking ? 0f : idleSmileWeight;

        // 平滑過渡數值，讓表情變化更自然
        currentSmileWeight = Mathf.Lerp(currentSmileWeight, targetWeight, Time.deltaTime * transitionSpeed);

        // 3. 💡 智慧融合機制：取 A2F 數值與底層微笑的「最大值」
        // 這樣可以保證大師至少保有底層的微笑，但如果 A2F 傳來更燦爛的笑容，也不會被蓋掉
        if (!isTalking || currentSmileWeight > 0.1f)
        {
            float a2fSmileL = faceMesh.GetBlendShapeWeight(smileLeftIndex);
            float a2fSmileR = faceMesh.GetBlendShapeWeight(smileRightIndex);

            faceMesh.SetBlendShapeWeight(smileLeftIndex, Mathf.Max(a2fSmileL, currentSmileWeight));
            faceMesh.SetBlendShapeWeight(smileRightIndex, Mathf.Max(a2fSmileR, currentSmileWeight));
        }
    }
}