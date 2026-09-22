using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public class STTReceiver : MonoBehaviour
{
    public event Action<string> OnTranscriptReceived;

    [Header("STT 接收設定")]
    public int listenPort = 5053;

    private UdpClient udpClient;
    private Thread listenThread;

    private string latestText = "";
    private bool hasNewText = false;

    void Start()
    {
        udpClient = new UdpClient(listenPort);

        listenThread = new Thread(new ThreadStart(ListenForUDP));
        listenThread.IsBackground = true;
        listenThread.Start();

        Debug.Log($"👂 大師的耳朵已開啟，正在監聽 Port: {listenPort}");
    }

    private void ListenForUDP()
    {
        IPEndPoint anyIP =
            new IPEndPoint(IPAddress.Any, listenPort);

        while (true)
        {
            try
            {
                byte[] data = udpClient.Receive(ref anyIP);

                string text =
                    Encoding.UTF8.GetString(data);

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
        if (hasNewText)
        {
            string text = latestText;

            hasNewText = false;

            Debug.Log($"🗣️ 使用者說了： {text}");

            OnTranscriptReceived?.Invoke(text);
        }
    }

    void OnDestroy()
    {
        if (listenThread != null)
            listenThread.Abort();

        if (udpClient != null)
            udpClient.Close();
    }
}