using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class WebcamTargetController : MonoBehaviour
{
    [Header("追蹤設定")]
    public float moveMultiplier = 1.5f; // 目標球移動的振幅
    public float smoothSpeed = 10f;     // 移動平滑度

    [Header("眼球微動 (Saccades)")]
    [Tooltip("眼球會在目標周圍多大的範圍內隨機跳動 (單位: 公尺)")]
    public float saccadeRadius = 0.05f;
    [Tooltip("每次視線跳動後，最短會凝視多久 (秒)")]
    public float minSaccadeTime = 0.2f;
    [Tooltip("每次視線跳動後，最長會凝視多久 (秒)")]
    public float maxSaccadeTime = 1.5f;

    private Vector3 saccadeOffset; // 記錄目前的視線微小偏移量
    private float saccadeTimer = 0f; // 凝視計時器

    private UdpClient udpClient;
    private Thread receiveThread;
    private Vector3 startPos;
    private Vector3 targetPos;
    private bool isRunning = true;

    void Start()
    {
        startPos = transform.position;
        targetPos = startPos;

        // 在背景執行緒啟動 UDP 接收，避免卡死主執行緒
        udpClient = new UdpClient(5052);
        receiveThread = new Thread(ReceiveData) { IsBackground = true };
        receiveThread.Start();
    }

    private void ReceiveData()
    {
        System.Net.IPEndPoint anyIP = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (isRunning)
        {
            try
            {
                byte[] data = udpClient.Receive(ref anyIP);
                string text = Encoding.UTF8.GetString(data);
                string[] coords = text.Split(',');

                if (coords.Length == 2)
                {
                    // 將 Python 傳來的 2D 偏移量，加上目標球的初始位置
                    float offsetX = -float.Parse(coords[0]) * moveMultiplier;
                    float offsetY = float.Parse(coords[1]) * moveMultiplier;
                    targetPos = startPos + new Vector3(offsetX, offsetY, 0);
                }
            }
            catch { /* 忽略連線中斷錯誤 */ }
        }
    }

    void Update()
    {     
        // === 1. 計算眼球微動 (Saccades) ===
        saccadeTimer -= Time.deltaTime;

        // 當計時器歸零，代表眼球要跳動尋找臉上的新焦點（例如從左眼跳到右眼，或跳到鼻子）
        if (saccadeTimer <= 0f)
        {
            // 在設定的半徑範圍內，隨機產生一個微小的 X、Y 偏移量
            saccadeOffset = new Vector3(
                Random.Range(-saccadeRadius, saccadeRadius),
                Random.Range(-saccadeRadius, saccadeRadius),
                0f
            );

            // 重新設定計時器，決定下一次眼球跳動是什麼時候
            saccadeTimer = Random.Range(minSaccadeTime, maxSaccadeTime);
        }

        // === 2. 結合目標位置與微動偏移量 ===
        // 將你原本要追蹤的核心座標，加上這個極微小的隨機偏移
        Vector3 finalTargetPos = targetPos + saccadeOffset;
        // (如果是滑鼠腳本，這行請改成 Vector3 finalTargetPos = targetWorldPos + saccadeOffset;)

        // === 3. 平滑移動目標球 ===
        // 球體會朝著「加上微動後的新座標」滑順移動
        transform.position = Vector3.Lerp(transform.position, finalTargetPos, Time.deltaTime * smoothSpeed);

    }

    void OnDestroy()
    {
        isRunning = false;
        if (udpClient != null) udpClient.Close();
    }
}