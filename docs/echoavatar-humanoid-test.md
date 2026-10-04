# 在專題人物測試 EchoAvatar 身體動作

這是 `Handyman_Full` 的第一版動作接收測試。Unity 維持原專題場景，EchoAvatar 繼續在同一台 Linux 電腦執行。接收端只處理動作；音效仍由專題 `ConversationManager` 的 `AudioSource` 播放。此階段先用已驗證的 MP3 傳送器手動送音訊，確認角色骨架對應後再接入每輪 TTS。

## Unity 場景

1. 切到此分支並開啟 `Assets/Scenes/SampleScene.unity`，等 Unity 完成套件匯入。
2. 在 Hierarchy 建立空物件 `EchoAvatarReceiver`，加入 `EchoAvatarHumanoidReceiver` 元件。
3. 把場景中 `Handyman_Full` 的 **Animator 元件**拖到 `Character Animator` 欄位。此 FBX 已設定 Humanoid Avatar。
4. 保持 `Apply Face Blendshapes`、`Apply Root Translation` 關閉，`Rotation Strength` 設為 1。先測身體；如果動作扭曲，可先把強度降到 0.3～0.5 比較。
5. 測試 EchoAvatar 時，暫時關閉 `A2F_Manager` 上的 **A2FController 元件**，但保留物件及其 STTReceiver。清除 `ConversationManager` Inspector 的 `A2f Controller` 指派，避免它直接呼叫 A2FController。模型原有 Animator 可保留；接收元件在 LateUpdate 寫入骨骼。
6. 進入 Play 模式。Console 應出現 `EchoAvatar: TCP Server started on port 12346...`。

## 本機啟動順序

1. Unity 先進 Play 模式並監聽 `127.0.0.1:12346`。
2. 在 `~/EchoAvatar` 啟動已測通的 `echoavatar_3080.py --model_name ./ckpts/body_g`，等它顯示連上動作接收端。
3. 保持 EchoAvatar 執行，使用之前的 `/tmp/send_echoavatar_mp3.py`，輸入專題 TTS MP3 路徑。觀察 `Handyman_Full` 的肩膀、手臂、頭和腿。保持傳送器的 socket 開著即可傳第二輪。

## 判讀結果

- 此接收端依原範例的 88 個 BVH 骨骼順序，挑出對應的 Humanoid 骨骼；略過來源的扭轉骨、眼球和手指細節。Unity 官方的 `Animator.GetBoneTransform` 依 Humanoid Avatar 取得目標骨骼。
- 目前把來源的局部旋轉增量套在 `Handyman_Full` 初始局部旋轉上，屬於第一版骨架對應。兩套骨架的局部軸可能不同；若手臂方向、手腕或腿部不自然，需要校正骨骼軸，不能把現在的對應當作完成的角色重定向。
- 臉部暫時關閉，因為需要核對 `Handyman_Full` 的 blendshape 名稱與 EchoAvatar 的 51 個輸出索引。確定順序後再打開 `Apply Face Blendshapes` 並指定臉部 `SkinnedMeshRenderer`。
- Unity 接收端可以等待 EchoAvatar 重新連線。EchoAvatar 的音訊輸入仍是一條連線；音訊傳送器退出時，原推論程式也會結束。
- 這個測試沒有修改專題 `/tts` 的自動傳送流程。角色動作確認正確後，再將每輪 TTS 音訊送進 EchoAvatar 的 `127.0.0.1:12345`，並保持同一條音訊連線供多輪對話使用。
