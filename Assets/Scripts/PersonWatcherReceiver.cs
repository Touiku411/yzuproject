using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

// Attach one receiver to an active GameObject in the Unity scene.
public class PersonWatcherReceiver : MonoBehaviour
{
    [Header("UDP")]
    [Range(1, 65535)] public int port = 5065;
    public bool logPackets = true;
    [Min(0.1f)] public float nearestTimeoutSeconds = 2f;

    [Header("Nearest person (runtime)")]
    public bool hasNearest;
    public int nearestId = -1;
    public int personCount;
    public float distanceMeters;
    public Vector3 cameraPositionMeters;
    public Vector3 unityPositionMeters;
    public bool inZone;
    public float dwellSeconds;
    public bool hasHeadPose;
    public bool facingCamera;
    public float yawDegrees;
    public float pitchDegrees;

    [Header("Last event (runtime)")]
    public string lastEvent;
    public int lastPersonId = -1;

    [Header("Events: argument is the person's tracking ID")]
    public PersonIdEvent onStandTrigger = new PersonIdEvent();
    public PersonIdEvent onLeftZone = new PersonIdEvent();

    private UdpClient receiver;
    private float lastNearestAt;

    private void OnEnable()
    {
        ClearNearest();
        lastEvent = "";
        lastPersonId = -1;
        try
        {
            receiver = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            // Receive must never wait for a packet inside Unity's Update().
            receiver.Client.Blocking = false;
            Debug.Log($"[PersonWatcher] Listening on UDP {port}", this);
        }
        catch (Exception exception)
        {
            CloseReceiver();
            Debug.LogError($"[PersonWatcher] Cannot listen on UDP {port}: {exception.Message}", this);
            enabled = false;
        }
    }

    private void Update()
    {
        // Bounded polling keeps all Unity API calls and event callbacks on the main thread.
        for (int index = 0; receiver != null && index < 32; index++)
        {
            byte[] bytes;
            try
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                bytes = receiver.Receive(ref sender);
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode == SocketError.WouldBlock)
                    break;

                Debug.LogError($"[PersonWatcher] UDP receive failed: {exception.Message}", this);
                CloseReceiver();
                ClearNearest();
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            WatcherMessage message;
            try
            {
                message = JsonUtility.FromJson<WatcherMessage>(Encoding.UTF8.GetString(bytes));
            }
            catch (ArgumentException exception)
            {
                Debug.LogWarning($"[PersonWatcher] Invalid JSON: {exception.Message}", this);
                continue;
            }

            if (message != null && message.person != null)
                HandleMessage(message);
        }

        // Python does not send NEAREST when nobody has an estimated distance.
        // Expire the previous observation instead of keeping it forever.
        if (hasNearest && Time.realtimeSinceStartup - lastNearestAt > nearestTimeoutSeconds)
            ClearNearest();
    }

    private void HandleMessage(WatcherMessage message)
    {
        PersonData person = message.person;
        switch (message.@event)
        {
            case "NEAREST":
                if (person.cam_xyz_m == null || person.cam_xyz_m.Length != 3)
                    return;

                lastNearestAt = Time.realtimeSinceStartup;
                hasNearest = true;
                nearestId = person.id;
                personCount = message.count;
                cameraPositionMeters = new Vector3(
                    person.cam_xyz_m[0], person.cam_xyz_m[1], person.cam_xyz_m[2]);
                // Python camera coordinates: right/down/forward. Unity: right/up/forward.
                unityPositionMeters = new Vector3(
                    cameraPositionMeters.x, -cameraPositionMeters.y, cameraPositionMeters.z);
                // This is the same optical-axis depth as Python's distance_m.
                distanceMeters = cameraPositionMeters.z;
                inZone = person.in_zone;
                dwellSeconds = person.dwell_s;
                hasHeadPose = person.head != null && !string.IsNullOrEmpty(person.head.src);
                facingCamera = hasHeadPose && person.head.facing_camera;
                yawDegrees = hasHeadPose ? person.head.yaw : 0f;
                pitchDegrees = hasHeadPose ? person.head.pitch : 0f;

                if (logPackets)
                    Debug.Log($"[PersonWatcher] NEAREST ID={nearestId} distance={distanceMeters:F2}m count={personCount}", this);
                break;

            case "STAND_TRIGGER":
                if (logPackets)
                    Debug.Log($"[PersonWatcher] STAND_TRIGGER ID={person.id}", this);
                onStandTrigger.Invoke(person.id);
                break;

            case "LEFT_ZONE":
                // A lost person's event can contain only id and speed; no position is required.
                if (logPackets)
                    Debug.Log($"[PersonWatcher] LEFT_ZONE ID={person.id}", this);
                onLeftZone.Invoke(person.id);
                break;

            default:
                return;
        }

        lastEvent = message.@event;
        lastPersonId = person.id;
    }

    private void ClearNearest()
    {
        hasNearest = false;
        nearestId = -1;
        personCount = 0;
        distanceMeters = 0f;
        cameraPositionMeters = Vector3.zero;
        unityPositionMeters = Vector3.zero;
        inZone = false;
        dwellSeconds = 0f;
        hasHeadPose = false;
        facingCamera = false;
        yawDegrees = 0f;
        pitchDegrees = 0f;
    }

    private void OnDisable()
    {
        CloseReceiver();
        ClearNearest();
    }

    private void CloseReceiver()
    {
        if (receiver == null)
            return;
        receiver.Close();
        receiver = null;
    }

    [Serializable]
    public class PersonIdEvent : UnityEvent<int> { }

    [Serializable]
    private class WatcherMessage
    {
        // @event is a C# identifier escape; the JSON field is still named "event".
        public string @event;
        public double t;
        public int count;
        public PersonData person;
    }

    [Serializable]
    private class PersonData
    {
        public int id;
        public float[] cam_xyz_m;
        public bool in_zone;
        public float dwell_s;
        public HeadData head;
        // Other JSON fields are ignored. In particular, event distance_m can be null;
        // NEAREST uses cam_xyz_m[2] above, so missing distance is never treated as zero.
    }

    [Serializable]
    private class HeadData
    {
        public float yaw;
        public float pitch;
        public bool facing_camera;
        public string src;
    }
}
