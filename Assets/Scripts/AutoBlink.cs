using System.Collections;
using UnityEngine;

public class AutoBlink : MonoBehaviour
{
    [Header("目標臉部網格")]
    public SkinnedMeshRenderer faceMesh;

    [Header("眨眼表情名稱 (ARKit 標準)")]
    public string blinkLeftName = "EyeBlinkLeft";
    public string blinkRightName = "EyeBlinkRight";

    [Header("時間設定")]
    [Tooltip("最短幾秒眨一次眼")]
    public float minBlinkInterval = 2.0f;
    [Tooltip("最長幾秒眨一次眼")]
    public float maxBlinkInterval = 5.0f;
    [Tooltip("眨眼動作的總時間 (秒)")]
    public float blinkSpeed = 0.15f;

    private int blinkLeftIndex = -1;
    private int blinkRightIndex = -1;
    private float currentBlinkWeight = 0f;
    private bool isBlinking = false;

    void Start()
    {
        if (faceMesh != null)
        {
            // 自動尋找正確的表情編號 (相容大小寫)
            blinkLeftIndex = GetBlendShapeIndex(blinkLeftName);
            blinkRightIndex = GetBlendShapeIndex(blinkRightName);
        }

        if (blinkLeftIndex != -1 || blinkRightIndex != -1)
        {
            // 開啟隨機眨眼計時器
            StartCoroutine(BlinkRoutine());
        }
        else
        {
            Debug.LogWarning("⚠️ 找不到眨眼表情，請確認名稱是否正確！");
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

    IEnumerator BlinkRoutine()
    {
        while (true)
        {
            // 1. 隨機等待 2 ~ 5 秒
            float waitTime = Random.Range(minBlinkInterval, maxBlinkInterval);
            yield return new WaitForSeconds(waitTime);

            // 2. 觸發眨眼動畫
            yield return StartCoroutine(DoBlink());
        }
    }

    IEnumerator DoBlink()
    {
        isBlinking = true;
        float halfDuration = blinkSpeed / 2f;
        float timer = 0f;

        // 上半段：快速閉眼 (0 -> 100)
        while (timer < halfDuration)
        {
            timer += Time.deltaTime;
            currentBlinkWeight = Mathf.Lerp(0f, 100f, timer / halfDuration);
            yield return null;
        }

        timer = 0f;

        // 下半段：快速睜眼 (100 -> 0)
        while (timer < halfDuration)
        {
            timer += Time.deltaTime;
            currentBlinkWeight = Mathf.Lerp(100f, 0f, timer / halfDuration);
            yield return null;
        }

        currentBlinkWeight = 0f;
        isBlinking = false;
    }

    // 💡 關鍵：使用 LateUpdate 確保眨眼數值在 A2F 運算完之後才覆寫上去
    void LateUpdate()
    {
        if (isBlinking && faceMesh != null)
        {
            if (blinkLeftIndex != -1) faceMesh.SetBlendShapeWeight(blinkLeftIndex, currentBlinkWeight);
            if (blinkRightIndex != -1) faceMesh.SetBlendShapeWeight(blinkRightIndex, currentBlinkWeight);
        }
    }
}