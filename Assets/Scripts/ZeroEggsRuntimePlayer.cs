using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Animations;
using UnityEngine.Playables;
using System.Threading.Tasks;


public class ZeroEggsRuntimePlayer : MonoBehaviour
{
    public string apiUrl = "http://127.0.0.1:8001";
    public GameObject sourceRigPrefab;
    public Animator targetAnimator;
    public AudioSource audioOutput;
    public AudioClip referenceAudio;
    public AnimationClip referenceClip;
    public bool keepInPlace = true;

    public enum FileMotionEmotion
    {
        neutral,
        joy,
        sad,
        angry,
        relaxed
    }
    [Header("A2F臉部動畫")]
    public A2FController a2fController;

    [Header("音檔風格")]
    public FileMotionEmotion fileMotionEmotion =
        FileMotionEmotion.neutral;

    [Serializable]
    class Skeleton
    {
        public int schema;
        public string[] names;
        public float[] rest_positions;
        public float[] reference_positions;
        public float[] reference_rotations;
    }

    [Serializable]
    class WireMotion
    {
        public int schema, frame_count;
        public string[] names;
        public float fps, duration, generation_seconds;
        public string positions_b64, rotations_b64;
    }

    class Motion
    {
        public WireMotion wire;
        public float[] positions, rotations;
    }

    Skeleton skeleton;
    Motion prepared, active;
    GameObject sourceObject;
    Animator sourceAnimator;
    Transform[] bones;
    Quaternion[] bindAxes;
    Quaternion alignment, sourceBindRotation;
    Vector3 offset, sourceBindPosition;
    float scale;

    HumanPoseHandler sourceHandler, targetHandler;
    HumanPose sourcePose, targetPose, beforePose;

    UnityWebRequest activeRequest;
    AudioSource playingOutput;
    bool ready, initializing, restoreAnimator;
    double startTime;
    float audioDuration;

    public bool IsPlaying { get; private set; }

    void OnEnable()
    {
        StartCoroutine(Initialize());
    }

    IEnumerator Initialize()
    {
        initializing = true;

        using (var request = UnityWebRequest.Get(apiUrl + "/skeleton"))
        {
            request.timeout = 30;
            activeRequest = request;
            yield return request.SendWebRequest();
            activeRequest = null;

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    BuildRig(JsonUtility.FromJson<Skeleton>(
                        request.downloadHandler.text));

                    ready = true;
                    Debug.Log("ZeroEGGS runtime 骨架已就緒", this);
                }
                catch (Exception error)
                {
                    Cleanup();
                    Debug.LogError(error.Message, this);
                }
            }
            else
            {
                Debug.LogError(
                    "ZeroEGGS API 無法連線：" + request.error, this);
            }
        }

        initializing = false;
    }

    Vector3 Rest(int i)
    {
        return new Vector3(
            skeleton.rest_positions[i * 3],
            skeleton.rest_positions[i * 3 + 1],
            skeleton.rest_positions[i * 3 + 2]);
    }

    static Quaternion BodyFrame(
        Vector3 hips, Vector3 left, Vector3 right, Vector3 head)
    {
        Vector3 lateral = (right - left).normalized;
        Vector3 up = Vector3.ProjectOnPlane(
            head - hips, lateral).normalized;
        Vector3 forward = Vector3.Cross(lateral, up).normalized;

        if (up.sqrMagnitude < .5f || forward.sqrMagnitude < .5f)
            throw new Exception("來源骨架無法建立座標軸");

        return Quaternion.LookRotation(forward, up);
    }
    Vector3 ReferencePosition(int i)
{
    return new Vector3(
        skeleton.reference_positions[i * 3],
        skeleton.reference_positions[i * 3 + 1],
        skeleton.reference_positions[i * 3 + 2]);
}

Quaternion ReferenceRotation(int i)
{
    return new Quaternion(
        skeleton.reference_rotations[i * 4],
        skeleton.reference_rotations[i * 4 + 1],
        skeleton.reference_rotations[i * 4 + 2],
        skeleton.reference_rotations[i * 4 + 3]).normalized;
}

void SampleReferencePose()
{
    var graph = PlayableGraph.Create("ZeroEGGS_Calibration");

    var transforms =
        sourceObject.GetComponentsInChildren<Transform>(true);

    Vector3[] positions = null;
    Vector3[] scales = null;
    Quaternion[] rotations = null;

    try
    {
        sourceAnimator.enabled = true;
        sourceAnimator.applyRootMotion = false;
        sourceAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

        var playable =
            AnimationClipPlayable.Create(graph, referenceClip);

        playable.SetApplyFootIK(false);
        playable.SetTime(0);
        playable.SetSpeed(0);

        var output =
            AnimationPlayableOutput.Create(
                graph, "Pose", sourceAnimator);

        output.SetSourcePlayable(playable);

        graph.Play();
        graph.Evaluate(0);

        // 保留取樣結果，避免關閉 Graph 時姿勢被重設。
        positions = new Vector3[transforms.Length];
        scales = new Vector3[transforms.Length];
        rotations = new Quaternion[transforms.Length];

        for (int i = 0; i < transforms.Length; i++)
        {
            positions[i] = transforms[i].localPosition;
            rotations[i] = transforms[i].localRotation;
            scales[i] = transforms[i].localScale;
        }
    }
    finally
    {
        sourceAnimator.enabled = false;

        if (graph.IsValid())
            graph.Destroy();

        if (positions != null)
        {
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].localPosition = positions[i];
                transforms[i].localRotation = rotations[i];
                transforms[i].localScale = scales[i];
            }
        }
    }
}
    void BuildRig(Skeleton spec)
{
    if (spec == null || spec.schema != 1 ||
        spec.names == null ||
        spec.reference_positions == null ||
        spec.reference_rotations == null ||
        spec.reference_positions.Length != spec.names.Length * 3 ||
        spec.reference_rotations.Length != spec.names.Length * 4)
    {
        throw new Exception(
            "API 缺少第一幀校準資料，請更新並重新啟動 Python");
    }

    if (!sourceRigPrefab || !referenceClip ||
        !referenceClip.humanMotion)
    {
        throw new Exception(
            "請指定來源 FBX 與其中的 Humanoid Reference Clip");
    }

    if (!targetAnimator || !targetAnimator.avatar ||
        !targetAnimator.avatar.isValid ||
        !targetAnimator.avatar.isHuman)
    {
        throw new Exception(
            "人物 Animator 必須具有有效的 Humanoid Avatar");
    }

    skeleton = spec;

    sourceObject = Instantiate(
        sourceRigPrefab, Vector3.zero, Quaternion.identity);

    sourceObject.name = "ZeroEGGS_Runtime_Source";
    sourceObject.hideFlags = HideFlags.None;

    sourceAnimator =
        sourceObject.GetComponentInChildren<Animator>();

    if (!sourceAnimator || !sourceAnimator.avatar ||
        !sourceAnimator.avatar.isValid ||
        !sourceAnimator.avatar.isHuman)
    {
        throw new Exception("來源 FBX 必須是有效的 Humanoid");
    }

    sourceAnimator.runtimeAnimatorController = null;

    foreach (var renderer in
        sourceObject.GetComponentsInChildren<Renderer>(true))
    {
        renderer.enabled = false;
    }

    var map = new Dictionary<string, Transform>();

    foreach (var item in
        sourceObject.GetComponentsInChildren<Transform>(true))
    {
        if (!map.ContainsKey(item.name))
            map.Add(item.name, item);
    }

    bones = new Transform[spec.names.Length];
    bindAxes = new Quaternion[bones.Length];

    for (int i = 0; i < bones.Length; i++)
        map.TryGetValue(spec.names[i], out bones[i]);

    int hips = Array.IndexOf(spec.names, "Hips");
    int left = Array.IndexOf(spec.names, "LeftUpLeg");
    int right = Array.IndexOf(spec.names, "RightUpLeg");
    int head = Array.IndexOf(spec.names, "Head");

    foreach (int i in new[] { hips, left, right, head })
    {
        if (i < 0 || !bones[i])
        {
            throw new Exception(
                "來源 FBX 缺少 Hips／UpLeg／Head 骨骼");
        }
    }

    // 先將來源骨架設成 FBX 動畫第 0 秒的姿勢。
    SampleReferencePose();

    var root = sourceAnimator.transform;

    Vector3 Local(int i)
    {
        return root.InverseTransformPoint(bones[i].position);
    }

    Vector3 Ref(int i)
    {
        return ReferencePosition(i);
    }

    alignment =
        BodyFrame(Local(hips), Local(left), Local(right), Local(head)) *
        Quaternion.Inverse(
            BodyFrame(Ref(hips), Ref(left), Ref(right), Ref(head)));

    float referenceHeight =
        Vector3.Distance(Ref(head), Ref(hips));

    float height =
        Vector3.Distance(Local(head), Local(hips));

    if (!(referenceHeight > 0.001f) || !(height > 0.001f))
    {
        throw new Exception(
            "校準姿勢的頭部／骨盆距離不正確");
    }

    scale = height / referenceHeight;
    offset = Local(hips) - alignment * Ref(hips) * scale;

    Quaternion inverseAlignment =
        Quaternion.Inverse(alignment);

    Quaternion inverseRoot =
        Quaternion.Inverse(root.rotation);

    int mismatchCount = 0;
    float maxError = 0;
    string worstBone = "";

    for (int i = 0; i < bones.Length; i++)
    {
        if (!bones[i]) continue;

        float error = Vector3.Distance(
            Local(i),
            alignment * Ref(i) * scale + offset);

        if (error > height * .05f)
            mismatchCount++;

        if (error > maxError)
        {
            maxError = error;
            worstBone = spec.names[i];
        }

        // 校準 BVH 與 FBX 每根骨骼的旋轉軸差異。
        Quaternion referenceRotation =
            alignment * ReferenceRotation(i) * inverseAlignment;

        bindAxes[i] =
            Quaternion.Inverse(referenceRotation) *
            inverseRoot *
            bones[i].rotation;
    }

    Debug.Log(
        $"[ZEGGS第一幀] Clip={referenceClip.name}，" +
        $"比例={scale:F4}，最大差距={maxError:F4}，" +
        $"骨骼={worstBone}，超限={mismatchCount}",
        this);

    if (mismatchCount > 0)
    {
        throw new Exception(
            "第一幀仍無法對齊；請確認 Reference Clip 未裁切且來自同一份 BVH，" +
            "保留上方診斷紀錄");
    }

    sourceHandler =
        new HumanPoseHandler(sourceAnimator.avatar, root);

    targetHandler =
        new HumanPoseHandler(
            targetAnimator.avatar, targetAnimator.transform);

    sourceHandler.GetHumanPose(ref sourcePose);
    ToLocal(ref sourcePose, sourceAnimator);

    sourceBindRotation = sourcePose.bodyRotation;
    sourceBindPosition = sourcePose.bodyPosition;
}

    static void ToLocal(ref HumanPose pose, Animator animator)
    {
        pose.bodyPosition = animator.transform.InverseTransformPoint(
            pose.bodyPosition * animator.humanScale) / animator.humanScale;

        pose.bodyRotation =
            Quaternion.Inverse(animator.transform.rotation) *
            pose.bodyRotation;
    }

    bool ReadMotion(string json)
    {
        prepared = null;

        try
        {
            var wire = JsonUtility.FromJson<WireMotion>(json);

            if (wire == null || wire.schema != 1 ||
                wire.frame_count < 1 || wire.frame_count > 7201 ||
                !(wire.fps > 0) || float.IsInfinity(wire.fps) ||
                !(wire.duration > 0) || wire.duration > 120 ||
                wire.names == null || wire.names.Length != bones.Length)
                throw new Exception("動作資料格式不正確");

            for (int i = 0; i < wire.names.Length; i++)
                if (wire.names[i] != skeleton.names[i])
                    throw new Exception("來源骨架名稱不一致");

            float[] Decode(string text, int count)
            {
                byte[] bytes = Convert.FromBase64String(text);

                if (!BitConverter.IsLittleEndian ||
                    bytes.Length != checked(count * 4))
                    throw new Exception("動作資料長度或 byte order 不正確");

                var values = new float[count];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);

                foreach (float value in values)
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new Exception("動作含 NaN/Inf");

                return values;
            }

            int count = checked(wire.frame_count * bones.Length);

            prepared = new Motion
            {
                wire = wire,
                positions = Decode(wire.positions_b64, count * 3),
                rotations = Decode(wire.rotations_b64, count * 4)
            };

            Debug.Log(
                $"ZeroEGGS 動作已準備：{wire.frame_count} 幀，" +
                $"{wire.duration:F2} 秒", this);

            return true;
        }
        catch (Exception error)
        {
            Debug.LogError(error.Message, this);
            return false;
        }
    }

    // 下一步接 TTS 時會使用這個方法。
    public IEnumerator Prepare(AudioClip clip, string emotion = "neutral")
    {
        prepared = null;
        while (initializing) yield return null;

        if (!ready || IsPlaying || !clip)
        {
            Debug.LogError("ZeroEGGS 尚未就緒或仍在播放", this);
            yield break;
        }

        byte[] bytes = null;

        try
        {
            var samples = new float[clip.samples * clip.channels];

            if (!clip.GetData(samples, 0))
                throw new Exception("AudioClip 無法讀取 PCM");

            var mono = new float[clip.samples];

            for (int i = 0; i < mono.Length; i++)
                for (int channel = 0; channel < clip.channels; channel++)
                    mono[i] +=
                        samples[i * clip.channels + channel] / clip.channels;

            bytes = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
        }
        catch (Exception error)
        {
            Debug.LogError(error.Message, this);
        }

        if (bytes == null) yield break;

        using (var request =
            new UnityWebRequest(apiUrl + "/motion", "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();

            request.SetRequestHeader(
                "Content-Type", "application/octet-stream");
            request.SetRequestHeader(
                "X-Sample-Rate", clip.frequency.ToString());

            string selectedEmotion =
            string.IsNullOrWhiteSpace(emotion)
                ? "neutral"
                : emotion.Trim().ToLowerInvariant();

            request.SetRequestHeader("X-Emotion", selectedEmotion);

            Debug.Log(
                $"[ZeroEGGS] 請求動作風格：{selectedEmotion}",
                this
            );
            
            request.timeout = 180;
            activeRequest = request;
            yield return request.SendWebRequest();
            activeRequest = null;

            if (request.result == UnityWebRequest.Result.Success)
                ReadMotion(request.downloadHandler.text);
            else
                Debug.LogError(
                    "ZeroEGGS 生成失敗：" + request.error + "\n" +
                    request.downloadHandler.text, this);
        }
    }

    public bool PlayPrepared(AudioClip clip, AudioSource output)
    {
        if (!ready || prepared == null ||
            !clip || !output || IsPlaying)
            return false;

        if (Mathf.Abs(prepared.wire.duration - clip.length) > .1f)
        {
            Debug.LogError("動作與音訊的長度不一致", this);
            prepared = null;
            return false;
        }

        targetHandler.GetHumanPose(ref beforePose);
        ToLocal(ref beforePose, targetAnimator);

        beforePose.muscles = (float[])beforePose.muscles.Clone();
        targetPose.muscles = new float[beforePose.muscles.Length];

        restoreAnimator = targetAnimator.enabled;
        targetAnimator.enabled = false;

        active = prepared;
        prepared = null;
        audioDuration = clip.length;
        startTime = AudioSettings.dspTime + .1;

        output.Stop();
        output.clip = clip;
        output.loop = false;
        output.pitch = 1;
        output.PlayScheduled(startTime);

        playingOutput = output;
        IsPlaying = true;

        Debug.Log("ZeroEGGS：語音與身體動作共同開始", this);
        return true;
    }

    void LateUpdate()
    {
        if (!IsPlaying || AudioSettings.dspTime < startTime) return;

        float elapsed = (float)(AudioSettings.dspTime - startTime);

        if (elapsed >= audioDuration + .2f)
        {
            Finish();
            return;
        }

        float frame = Mathf.Clamp(
            elapsed * active.wire.fps, 0, active.wire.frame_count - 1);

        int first = (int)frame;
        int second = Mathf.Min(first + 1, active.wire.frame_count - 1);
        float blend = frame - first;

        Vector3 Position(int f, int b)
        {
            int i = (f * bones.Length + b) * 3;
            return new Vector3(
                active.positions[i],
                active.positions[i + 1],
                active.positions[i + 2]);
        }

        Quaternion Rotation(int f, int b)
        {
            int i = (f * bones.Length + b) * 4;
            return new Quaternion(
                active.rotations[i],
                active.rotations[i + 1],
                active.rotations[i + 2],
                active.rotations[i + 3]).normalized;
        }

        var root = sourceAnimator.transform;
        Quaternion inverse = Quaternion.Inverse(alignment);

        for (int i = 0; i < bones.Length; i++)
        {
            if (!bones[i]) continue;

            Vector3 position = Vector3.Lerp(
                Position(first, i), Position(second, i), blend);

            Quaternion rotation = Quaternion.Slerp(
                Rotation(first, i), Rotation(second, i), blend);

            bones[i].position = root.TransformPoint(
                alignment * position * scale + offset);

            bones[i].rotation =
                root.rotation * alignment * rotation *
                inverse * bindAxes[i];
        }

        sourceHandler.GetHumanPose(ref sourcePose);
        ToLocal(ref sourcePose, sourceAnimator);

        Vector3 bodyPosition =
            beforePose.bodyPosition +
            sourcePose.bodyPosition -
            sourceBindPosition;

        if (keepInPlace)
        {
            bodyPosition.x = beforePose.bodyPosition.x;
            bodyPosition.z = beforePose.bodyPosition.z;
        }

        Quaternion bodyRotation =
            beforePose.bodyRotation *
            Quaternion.Inverse(sourceBindRotation) *
            sourcePose.bodyRotation;

        float weight = Mathf.Min(
            Mathf.Clamp01(elapsed / .15f),
            Mathf.Clamp01((audioDuration + .2f - elapsed) / .2f));

        targetPose.bodyPosition = Vector3.Lerp(
            beforePose.bodyPosition, bodyPosition, weight);

        targetPose.bodyRotation = Quaternion.Slerp(
            beforePose.bodyRotation, bodyRotation, weight);

        for (int i = 0; i < targetPose.muscles.Length; i++)
            targetPose.muscles[i] = Mathf.Lerp(
                beforePose.muscles[i], sourcePose.muscles[i], weight);

        targetHandler.SetHumanPose(ref targetPose);
    }

    void Finish()
    {
        if (IsPlaying && targetAnimator)
        {
            targetHandler.SetHumanPose(ref beforePose);
            targetAnimator.enabled = restoreAnimator;
        }

        IsPlaying = false;
        active = null;
        playingOutput = null;
    }

    public void StopPlayback()
    {
        if (playingOutput) playingOutput.Stop();
        Finish();
    }

    [ContextMenu("測試已驗證的動作")]
    void TestReference()
    {
        if (Application.isPlaying && !IsPlaying)
            StartCoroutine(Reference());
        else
            Debug.LogWarning("請先按 Play，並等待目前播放結束", this);
    }

    IEnumerator Reference()
    {
        while (initializing) yield return null;

        if (!referenceAudio)
        {
            Debug.LogError(
                $"[{gameObject.name}] Reference Audio 未指定", this);
            yield break;
        }

        if (!ready)
        {
            Debug.LogError(
                $"[{gameObject.name}] 骨架初始化未成功，API：{apiUrl}。" +
                "請查看前面的 API 連線或骨架校準錯誤。",
                this);
            yield break;
        }

        using (var request = UnityWebRequest.Get(apiUrl + "/reference"))
        {
            request.timeout = 30;
            activeRequest = request;
            yield return request.SendWebRequest();
            activeRequest = null;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("參考動作下載失敗：" + request.error, this);
                yield break;
            }

            if (!ReadMotion(request.downloadHandler.text))
                yield break;
        }

        if (!audioOutput)
            audioOutput = gameObject.AddComponent<AudioSource>();

        PlayPrepared(referenceAudio, audioOutput);
    }

    void Cleanup()
    {
        StopPlayback();
        ready = false;
        prepared = null;

        sourceHandler?.Dispose();
        targetHandler?.Dispose();
        sourceHandler = null;
        targetHandler = null;

        if (sourceObject) Destroy(sourceObject);
        sourceObject = null;
    }

    void OnDisable()
    {
        activeRequest?.Abort();
        activeRequest = null;
        StopAllCoroutines();
        initializing = false;
        Cleanup();
    }
    bool fileMotionTestRunning;

[ContextMenu("用目前音檔生成新動作")]
public void GenerateMotionForCurrentAudio()
{
    if (!Application.isPlaying)
    {
        Debug.LogWarning("請先進入 Play 模式。", this);
        return;
    }

    if (fileMotionTestRunning || IsPlaying)
    {
        Debug.LogWarning("目前正在生成或播放，請等待完成。", this);
        return;
    }

    if (!referenceAudio || !audioOutput)
    {
        Debug.LogError(
            "請指定 Reference Audio 和 Audio Output。",
            this
        );
        return;
    }

    fileMotionTestRunning = true;

    StartCoroutine(GenerateAndPlayAudioFile(referenceAudio));
}

        IEnumerator GenerateAndPlayAudioFile(AudioClip clip)
    {
        string emotion = fileMotionEmotion.ToString();
        AudioSource output = audioOutput;

        try
        {
            Debug.Log(
                $"[ZeroEGGS音檔測試] 音檔：{clip.name}，" +
                $"長度：{clip.length:F2} 秒，風格：{emotion}",
                this
            );

            // 先根據同一份音訊生成身體動作。
            yield return Prepare(clip, emotion);

            if (prepared == null)
            {
                Debug.LogError(
                    "[ZeroEGGS音檔測試] 未取得動作，請查看生成錯誤。",
                    this
                );
                yield break;
            }

            // 音訊仍由 ZeroEGGS 的 AudioSource 播放。
            if (!PlayPrepared(clip, output))
            {
                Debug.LogError(
                    "[ZeroEGGS音檔測試] 語音與身體播放失敗。",
                    this
                );
                yield break;
            }

            Task a2fTask = null;

            // 同一份音訊送到 A2F，處理嘴型與表情。
            if (a2fController &&
                a2fController.isActiveAndEnabled)
            {
                try
                {
                    a2fTask = a2fController.ProcessAudioClip(clip);

                    Debug.Log(
                        "[ZeroEGGS音檔測試] 已將同一份語音送至 A2F。",
                        this
                    );
                }
                catch (Exception error)
                {
                    Debug.LogError(
                        $"[ZeroEGGS音檔測試] A2F 啟動失敗：{error.Message}",
                        this
                    );
                }
            }
            else
            {
                Debug.LogWarning(
                    "[ZeroEGGS音檔測試] A2F 未指定或未啟用。",
                    this
                );
            }

            // 身體與 A2F 都完成，才開放下一次音檔測試。
            while (IsPlaying ||
                (a2fTask != null && !a2fTask.IsCompleted))
            {
                yield return null;
            }

            if (a2fTask != null && a2fTask.IsFaulted)
            {
                Debug.LogError(
                    $"[ZeroEGGS音檔測試] A2F 處理失敗：{a2fTask.Exception}",
                    this
                );
            }
            else if (a2fTask != null && a2fTask.IsCanceled)
            {
                Debug.LogWarning(
                    "[ZeroEGGS音檔測試] A2F 處理已取消。",
                    this
                );
            }

            Debug.Log(
                "[ZeroEGGS音檔測試] 語音、身體與 A2F 處理完成。",
                this
            );
        }
        finally
        {
            fileMotionTestRunning = false;
        }
    }
}