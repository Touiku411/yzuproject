using UnityEngine;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class WebcamTargetController : MonoBehaviour
{
    [Header("追蹤設定")]
    public float moveMultiplier = 1.5f; // 目標球移動的振幅
    public float smoothSpeed = 10f;     // 移動平滑度

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
        // 平滑移動目標球
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * smoothSpeed);
    }

    void OnDestroy()
    {
        isRunning = false;
        if (udpClient != null) udpClient.Close();
    }
}