using System;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using NativeWebSocket;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class RealTimeAudioChat : MonoBehaviour
{
    WebSocket websocket;

    public AudioSource audioSource;  // 再生用AudioSource
    public AudioSource externalAudioSource; //リップシンク用 uLipSyncのコンポーネントがついてるオブジェクトに渡す用

    // インスペクタでAPIキー、instructions、voiceを指定
    [SerializeField]
    private string apiKey;
    [SerializeField]
    private string instructions;
    [SerializeField]
    private string voice;

    public Text subtitleText; // 画面に表示する文字列用

    private const int sampleRate = 24000; //送るサンプリングレート
    private const int responseSampleRate = 24800; //ここの数値を高くすると声が可愛くなる。デフォルトは24000
    private const int channels = 1;

    private Queue<byte[]> audioDataQueue = new Queue<byte[]>();  // サーバーから受け取る音声データのキュー
    private Queue<AudioClip> audioClipQueue = new Queue<AudioClip>();  // 再生するAudioClipのキュー

    private StringBuilder currentTranscript = new StringBuilder();  // 現在の字幕を保持

    private void Start()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("APIキーが設定されていません。インスペクタビューで設定してください。");
            return;
        }

        InitializeWebSocket();
        StartMicrophoneInput();
    }

    private async void InitializeWebSocket()
    {
        string url = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview-2024-10-01";

        websocket = new WebSocket(url, new Dictionary<string, string>
        {
            { "Authorization", "Bearer " + apiKey },
            { "OpenAI-Beta", "realtime=v1" }
        });

        websocket.OnOpen += async () => //WebSocket接続直後に実行される
        {
            Debug.Log("WebSocketに接続しました。");

            var sessionUpdateRequest = new
            {
                type = "session.update", 
                session = new
                {
                    modalities = new[] { "text", "audio" },
                    instructions = instructions,
                    voice = voice,
                    input_audio_format = "pcm16",
                    output_audio_format = "pcm16"
                }
            };

            string sessionUpdateMessage = JsonConvert.SerializeObject(sessionUpdateRequest);
            await websocket.SendText(sessionUpdateMessage); //WebSocket開始直後にsession.update
            Debug.Log("セッション更新リクエストを送信しました。");

            var initRequest = new
            {
                type = "response.create",
                response = new //これは必要ないと思う。type="response.create"だけでいけるはず
                {
                    //modalities = new[] { "audio", "text" },
                    //instructions = "会話の始まりです。ユーザーに挨拶してください。"
                }
            };

            string initMessage = JsonConvert.SerializeObject(initRequest);
            await websocket.SendText(initMessage); //話しかける前に最初に一回しゃべってもらう
            Debug.Log("初期リクエストを送信しました。");
        };

        websocket.OnMessage += (bytes) => //データが帰って来た時のリスナー
        {
            var message = Encoding.UTF8.GetString(bytes);
            Debug.Log("メッセージを受信しました: " + message);

            try
            {
                JObject response = JObject.Parse(message);
                string messageType = response["type"].ToString();

                if (messageType == "response.audio.delta") //受信したデータに特定のキーが入っているかチェック。これは回答音声の差分
                {
                    string base64AudioData = response["delta"].ToString();
                    if (!string.IsNullOrEmpty(base64AudioData))
                    {
                        byte[] audioBytes = Convert.FromBase64String(base64AudioData);
                        lock (audioDataQueue)
                        {
                            audioDataQueue.Enqueue(audioBytes);
                        }
                        ProcessAudioData(); //受け取った音声差分を処理
                    }
                }
                else if (messageType == "response.audio_transcript.delta") //字幕差分
                {
                    string transcriptDelta = response["delta"].ToString();
                    Debug.Log("Transcript delta: " + transcriptDelta);
                    AppendToSubtitle(transcriptDelta);  // 現在の字幕に追加
                }
                else if (messageType == "response.audio_transcript.done") //一回に返ってくる字幕が全部返ってきたとき
                {
                    Debug.Log("Transcript done.");
                    StartCoroutine(ClearSubtitleAfterDelay(20f));  // 字幕表示処理＆20秒後に字幕をクリア
                }
                else
                {
                    // その他のメッセージタイプを処理
                }
            }
            catch (Exception e)
            {
                Debug.LogError("メッセージのパース中にエラーが発生しました: " + e.Message);
            }
        };

        websocket.OnError += (e) =>
        {
            Debug.LogError("WebSocketエラー: " + e);
        };

        websocket.OnClose += (e) =>
        {
            Debug.Log("WebSocket接続が閉じられました。");
        };

        await websocket.Connect();
    }

    private void StartMicrophoneInput() //マイク入力
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("マイクデバイスが検出されませんでした。マイクを接続してください。");
            return;
        }

        // マイク入力を開始
        string micDevice = Microphone.devices[0];  // 最初のマイクを選択
        AudioClip micClip = Microphone.Start(micDevice, true, 10, sampleRate); // 10秒のループクリップ
        while (!(Microphone.GetPosition(micDevice) > 0)) { }
        Debug.Log("マイク入力を開始しました。");

        StartCoroutine(SendMicrophoneDataCoroutine(micClip));
    }

    private IEnumerator SendMicrophoneDataCoroutine(AudioClip micClip) //音声データを加工して送信
    {
        int lastSamplePos = 0;

        while (true)
        {
            int currentSamplePos = Microphone.GetPosition(null);
            int samplesToRead = currentSamplePos - lastSamplePos;
            if (samplesToRead < 0)
            {
                samplesToRead += micClip.samples;
            }

            if (samplesToRead > 0)
            {
                float[] micData = new float[samplesToRead * channels];
                micClip.GetData(micData, lastSamplePos % micClip.samples);

                // PCM16に変換
                byte[] pcmData = FloatArrayToPCM16(micData);

                // Base64エンコード
                string base64Audio = Convert.ToBase64String(pcmData);

                // メッセージを作成して送信
                var audioEvent = new
                {
                    type = "input_audio_buffer.append", //これが一番重要。こまかくなった音声データを送信。input_audio_buffer.commitやconversation.item.createは必要なし。
                    audio = base64Audio
                };

                string audioMessage = JsonConvert.SerializeObject(audioEvent);

                // 非同期でメッセージを送信
                if (websocket != null && websocket.State == WebSocketState.Open)
                {
                    SendAudioMessage(audioMessage);
                }
                else
                {
                    Debug.LogWarning("WebSocket接続が開いていません。音声データを送信できませんでした。");
                }

                lastSamplePos = currentSamplePos;
            }

            yield return null;
        }
    }

    // 非同期でWebSocketにメッセージを送信するメソッド
    private async void SendAudioMessage(string audioMessage)
    {
        try
        {
            if (websocket != null && websocket.State == WebSocketState.Open)
            {
                await websocket.SendText(audioMessage);
            }
            else
            {
                Debug.LogWarning("WebSocket接続が開いていません。音声データを送信できませんでした。");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("音声データの送信中にエラーが発生しました: " + e.Message);
        }
    }

    private byte[] FloatArrayToPCM16(float[] audioData) //送信時の音声処理
    {
        byte[] pcmData = new byte[audioData.Length * 2];
        for (int i = 0; i < audioData.Length; i++)
        {
            short pcmSample = (short)(audioData[i] * short.MaxValue);
            pcmData[i * 2] = (byte)(pcmSample & 0xff);
            pcmData[i * 2 + 1] = (byte)((pcmSample >> 8) & 0xff);
        }
        return pcmData;
    }

    private void Update()
    {
        if (websocket != null)
        {
            websocket.DispatchMessageQueue();
        }

        // 再生が終わったら次の音声を再生
        if (!audioSource.isPlaying && audioClipQueue.Count > 0)
        {
            TryPlayNextAudio();
        }
    }

    private void ProcessAudioData() //受け取った音声差分を処理
    {
        lock (audioDataQueue)
        {
            while (audioDataQueue.Count > 0)
            {
                byte[] audioBytes = audioDataQueue.Dequeue();
                AudioClip audioClip = CreateAudioClipFromPCM(audioBytes);
                if (audioClip != null)
                {
                    audioClipQueue.Enqueue(audioClip);
                }
            }
        }
    }

    private void TryPlayNextAudio() //受け取った音声差分を処理
    {
        if (audioClipQueue.Count > 0)
        {
            AudioClip nextClip = audioClipQueue.Dequeue();
            audioSource.clip = nextClip;
            audioSource.Play();

            // 別のAudioSourceオブジェクトに渡す場合。リップシンク用
            if (externalAudioSource != null)
            {
                externalAudioSource.clip = nextClip;
                externalAudioSource.Play();
            }
        }
    }

    private AudioClip CreateAudioClipFromPCM(byte[] pcmData) //受け取った音声差分を処理
    {
        float[] audioFloats = PCM16ToFloatArray(pcmData);
        AudioClip audioClip = AudioClip.Create("ReceivedAudio", audioFloats.Length, channels, responseSampleRate, false);
        audioClip.SetData(audioFloats, 0);
        return audioClip;
    }

    private float[] PCM16ToFloatArray(byte[] pcmData) //受け取った音声差分を処理
    {
        float[] audioFloats = new float[pcmData.Length / 2];
        for (int i = 0; i < pcmData.Length; i += 2)
        {
            short pcmSample = BitConverter.ToInt16(pcmData, i);
            audioFloats[i / 2] = pcmSample / (float)short.MaxValue;
        }
        return audioFloats;
    }

    private async void OnApplicationQuit() //アプリ停止時
    {
        if (websocket != null)
        {
            await websocket.Close();
        }
    }

    private void AppendToSubtitle(string transcript) //送られてくる字幕を逐次追加
    {
        currentTranscript.Append(transcript + " ");
        if (subtitleText != null)
        {
            subtitleText.text = currentTranscript.ToString();
        }
        else
        {
            Debug.LogWarning("表示用のTextコンポーネントが設定されていません。");
        }
    }

    private IEnumerator ClearSubtitleAfterDelay(float delay) //字幕を一定時間で消す
    {
        yield return new WaitForSeconds(delay);
        currentTranscript.Clear();
        if (subtitleText != null)
        {
            subtitleText.text = "";
        }
    }
}