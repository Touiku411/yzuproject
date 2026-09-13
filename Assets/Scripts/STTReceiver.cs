using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class STTReceiver : MonoBehaviour
{
    [Header("STT 接收設定")]
    public int listenPort = 5053; // 必須與 Python 設定的 Port 一致

    private UdpClient udpClient;
    private Thread listenThread;

    // 用來將背景執行緒的資料傳回主執行緒 (Update)
    private string latestText = "";
    private bool hasNewText = false;

    void Start()
    {
        // 開啟背景執行緒來監聽網路封包，避免卡死 Unity 畫面
        udpClient = new UdpClient(listenPort);
        listenThread = new Thread(new ThreadStart(ListenForUDP));
        listenThread.IsBackground = true;
        listenThread.Start();

        Debug.Log($"👂 大師的耳朵已開啟，正在監聽 Port: {listenPort}");
    }

    private void ListenForUDP()
    {
        IPEndPoint anyIP = new IPEndPoint(IPAddress.Any, listenPort);
        while (true)
        {
            try
            {
                // 這裡會持續等待，直到收到 Python 傳來的資料
                byte[] data = udpClient.Receive(ref anyIP);
                string text = Encoding.UTF8.GetString(data);

                latestText = text;
                hasNewText = true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(e.ToString());
            }
        }
    }

    void Update()
    {
        // 當收到新文字時，在 Unity 的 Console 印出來
        if (hasNewText)
        {
            Debug.Log($"🗣️ 使用者說了： {latestText}");

            // 👉 未來這裡就是觸發 LLM (大語言模型) 大腦的完美時機！

            hasNewText = false; // 重置開關
        }
    }

    void OnDestroy()
    {
        if (listenThread != null) listenThread.Abort();
        if (udpClient != null) udpClient.Close();
    }
}