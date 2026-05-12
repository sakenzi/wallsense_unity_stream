using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Unity.Sentis;

/// <summary>
/// ОПТИМИЗИРОВАННЫЙ YOLOPoseDetector для Quest 2.
/// Читает кадр с RenderTexture монитора — не вызывает лишний Camera.Render().
/// DroneCameraController и YOLOPoseDetector используют одну и ту же RenderTexture.
/// </summary>
public class YOLOPoseDetector : MonoBehaviour
{
    [Header("Model")]
    [Tooltip("Models/yolo11n-pose.onnx")]
    public ModelAsset modelAsset;

    [Header("Camera")]
    public Camera droneCamera;

    [Header("Монитор — общая RenderTexture")]
    [Tooltip("Та же RenderTexture что назначена в DroneCameraController. " +
             "YOLO читает с неё — Camera.Render() не вызывается повторно.")]
    public RenderTexture sharedRenderTexture;

    [Header("⚡ ПРОИЗВОДИТЕЛЬНОСТЬ — НАСТРОЙТЕ ДЛЯ QUEST 2")]
    [Tooltip("Разрешение: 320 для Quest, 640 для ПК")]
    public int inputWidth  = 320;
    public int inputHeight = 320;

    [Tooltip("Секунды между инференсами: 0.5 = 2 FPS YOLO (рекомендуется для Quest)")]
    [Range(0.05f, 2f)]
    public float inferenceInterval = 0.5f;

    [Tooltip("CPU работает медленнее но стабильнее на Quest. GPU быстрее но может греться.")]
    public BackendType backendType = BackendType.CPU;

    [Tooltip("Асинхронное выполнение — не блокирует основной поток")]
    public bool asyncExecution = true;

    [Tooltip("Пропускать кадры если FPS < этого значения")]
    public bool adaptiveFPS = false;        // ← ВЫКЛЮЧЕНО: Quest 2 часто работает на 45-60 FPS
    public float minFPS = 40f;              // ← Снижено с 60 для Quest 2

    [Header("Detection Thresholds")]
    [Range(0f, 1f)] public float confidenceThreshold = 0.5f;
    [Range(0f, 1f)] public float keypointThreshold   = 0.3f;
    [Range(0f, 1f)] public float iouThreshold        = 0.45f;

    [Tooltip("Максимум людей для обработки (меньше = быстрее)")]
    public int maxDetections = 5;

    [Header("Visualization")]
    public HumanVisualizationManager visualizationManager;

    [Header("Debug")]
    public bool showDebugLogs = false;
    public bool showPerformanceStats = true;

    // ── Sentis ──────────────────────────────────────────────────────────────
    private Model runtimeModel;
    private Worker worker;
    private Tensor<float> inputTensor;

    // ── Захват кадра ─────────────────────────────────────────────────────────
    private RenderTexture captureRT;
    private Texture2D captureTexture;
    private bool needsResize;

    // ── Состояние ────────────────────────────────────────────────────────────
    private bool  isRunning = false;
    private float nextInferenceTime = 0f;
    private bool  isInferenceRunning = false;

    // ── Performance stats ────────────────────────────────────────────────────
    private float lastInferenceTime = 0f;
    private float avgInferenceTime  = 0f;
    private int   framesSinceLastInference = 0;
    private float lastFPS = 0f;

    // ── Результат ────────────────────────────────────────────────────────────
    public List<PoseDetection> LastDetections { get; private set; } = new();

    // ── Константы ────────────────────────────────────────────────────────────
    public const int NUM_KEYPOINTS  = 17;
    private const int VALUES_PER_KP = 3;
    private const int BBOX_OFFSET   = 0;
    private const int CONF_OFFSET   = 4;
    private const int KP_OFFSET     = 5;

    public static readonly string[] KeypointNames = {
        "nose", "left_eye", "right_eye", "left_ear", "right_ear",
        "left_shoulder", "right_shoulder", "left_elbow", "right_elbow",
        "left_wrist", "right_wrist", "left_hip", "right_hip",
        "left_knee", "right_knee", "left_ankle", "right_ankle"
    };

    public static readonly (int, int)[] SkeletonPairs = {
        (0,1),(0,2),(1,3),(2,4),
        (5,6),(5,11),(6,12),(11,12),
        (5,7),(7,9),(6,8),(8,10),
        (11,13),(13,15),(12,14),(14,16)
    };

    [System.Serializable]
    public class PoseDetection
    {
        public Rect      bbox;
        public float     confidence;
        public Vector3[] keypointsScreen = new Vector3[NUM_KEYPOINTS];
        public Vector3[] keypointsWorld  = new Vector3[NUM_KEYPOINTS];
        public bool      hasWorldCoords;
    }

    // ────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (!ValidateSetup()) { enabled = false; return; }
        InitSentis();
        InitCapture();
        Log($"✓ YOLOPoseDetector инициализирован");
        Log($"  Разрешение ввода: {inputWidth}x{inputHeight}");
        Log($"  RenderTexture: {sharedRenderTexture.width}x{sharedRenderTexture.height}");
        Log($"  Ресайз нужен: {needsResize}");
        Log($"  Backend: {backendType}");
        Log($"  Интервал: {inferenceInterval}s ({1f / inferenceInterval:F1} FPS)");
    }

    void OnDestroy()
    {
        worker?.Dispose();
        inputTensor?.Dispose();
        if (captureRT != null)
        {
            captureRT.Release();
            Destroy(captureRT);
        }
        if (captureTexture != null)
            Destroy(captureTexture);
    }

    void Update()
    {
        if (!isRunning) return;

        framesSinceLastInference++;
        lastFPS = 1f / Time.unscaledDeltaTime;

        if (adaptiveFPS && lastFPS < minFPS)
        {
            if (showPerformanceStats && framesSinceLastInference % 60 == 0)
                Debug.Log($"[YOLO] FPS низкий ({lastFPS:F0}) — пропуск инференса");
            return;
        }

        if (Time.time < nextInferenceTime) return;
        if (isInferenceRunning) return;

        nextInferenceTime = Time.time + inferenceInterval;

        if (asyncExecution)
            StartCoroutine(RunInferenceAsync());
        else
            RunInferenceSync();
    }

    // ────────────────────────────────────────────────────────────────────────
    // INIT
    // ────────────────────────────────────────────────────────────────────────

    bool ValidateSetup()
    {
        if (modelAsset == null)
        {
            Debug.LogError("[YOLO] modelAsset не назначен!");
            return false;
        }
        if (droneCamera == null)
        {
            droneCamera = GetComponentInChildren<Camera>();
            if (droneCamera == null)
            {
                Debug.LogError("[YOLO] droneCamera не найдена!");
                return false;
            }
        }
        if (sharedRenderTexture == null)
        {
            // Пробуем получить из DroneCameraController на том же объекте
            var dcc = GetComponent<DroneCameraController>();
            if (dcc != null)
            {
                sharedRenderTexture = dcc.GetRenderTexture();
                Log("sharedRenderTexture получена из DroneCameraController автоматически");
            }

            if (sharedRenderTexture == null)
            {
                Debug.LogError("[YOLO] sharedRenderTexture не назначена и DroneCameraController не найден!");
                return false;
            }
        }
        return true;
    }

    void InitSentis()
    {
        runtimeModel = ModelLoader.Load(modelAsset);
        worker = new Worker(runtimeModel, backendType);
        Log($"Модель загружена. Backend: {backendType}");
    }

    void InitCapture()
    {
        // Проверяем нужен ли ресайз
        needsResize = sharedRenderTexture.width != inputWidth
                   || sharedRenderTexture.height != inputHeight;

        if (needsResize)
        {
            // Промежуточный RT для ресайза через Blit
            captureRT = new RenderTexture(inputWidth, inputHeight, 16, RenderTextureFormat.ARGB32)
            {
                filterMode     = FilterMode.Bilinear,
                antiAliasing   = 1,
                useDynamicScale = false,
                autoGenerateMips = false
            };
            captureRT.Create();
            Log($"Создан captureRT {inputWidth}x{inputHeight} для ресайза");
        }

        captureTexture = new Texture2D(inputWidth, inputHeight, TextureFormat.RGB24, false, true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // CAPTURE — читаем с sharedRenderTexture без повторного Camera.Render()
    // ────────────────────────────────────────────────────────────────────────

    void CaptureFrame()
    {
        var prevActive = RenderTexture.active;

        if (needsResize)
        {
            // Blit делает ресайз GPU-стороной — быстрее чем CPU ресайз
            Graphics.Blit(sharedRenderTexture, captureRT);
            RenderTexture.active = captureRT;
        }
        else
        {
            // Разрешения совпадают — читаем напрямую
            RenderTexture.active = sharedRenderTexture;
        }

        captureTexture.ReadPixels(new Rect(0, 0, inputWidth, inputHeight), 0, 0);
        captureTexture.Apply(false);

        RenderTexture.active = prevActive;
    }

    // ────────────────────────────────────────────────────────────────────────
    // INFERENCE — ASYNC
    // ────────────────────────────────────────────────────────────────────────

    IEnumerator RunInferenceAsync()
    {
        isInferenceRunning = true;
        float startTime = Time.realtimeSinceStartup;

        CaptureFrame();
        yield return null;

        inputTensor?.Dispose();
        inputTensor = PreprocessTexture(captureTexture);
        yield return null;

        worker.Schedule(inputTensor);

        // Ждём несколько кадров для асинхронного выполнения
        for (int i = 0; i < 3; i++) yield return null;

        // ИСПРАВЛЕНО: Правильный способ получения выхода
        var output = worker.PeekOutput("output0") as Tensor<float>;
        if (output == null)
        {
            Debug.LogError("[YOLO] output0 is null!");
            isInferenceRunning = false;
            yield break;
        }

        // Обрабатываем результат
        LastDetections = PostProcess(output);
        yield return null;

        if (visualizationManager != null)
            visualizationManager.UpdateFromPoseDetections(LastDetections, droneCamera);

        float inferenceTime = Time.realtimeSinceStartup - startTime;
        avgInferenceTime = Mathf.Lerp(avgInferenceTime, inferenceTime, 0.1f);
        lastInferenceTime = inferenceTime;

        if (showPerformanceStats)
        {
            Debug.Log($"[YOLO] Обнаружено: {LastDetections.Count} | " +
                      $"Время: {inferenceTime * 1000:F0}ms | " +
                      $"Кадров пропущено: {framesSinceLastInference} | " +
                      $"FPS: {lastFPS:F0}");
        }

        framesSinceLastInference = 0;
        isInferenceRunning = false;
    }

    // ────────────────────────────────────────────────────────────────────────
    // INFERENCE — SYNC
    // ────────────────────────────────────────────────────────────────────────

    void RunInferenceSync()
    {
        float startTime = Time.realtimeSinceStartup;

        CaptureFrame();

        inputTensor?.Dispose();
        inputTensor = PreprocessTexture(captureTexture);

        worker.Schedule(inputTensor);

        // ИСПРАВЛЕНО: Правильный тип
        var output = worker.PeekOutput("output0") as Tensor<float>;
        if (output == null)
        {
            Debug.LogError("[YOLO] output0 is null!");
            return;
        }

        LastDetections = PostProcess(output);

        if (visualizationManager != null)
            visualizationManager.UpdateFromPoseDetections(LastDetections, droneCamera);

        if (showPerformanceStats)
            Debug.Log($"[YOLO] Обнаружено: {LastDetections.Count} | Время: {(Time.realtimeSinceStartup - startTime) * 1000:F0}ms");
    }

    // ────────────────────────────────────────────────────────────────────────
    // PREPROCESS
    // ────────────────────────────────────────────────────────────────────────

    Tensor<float> PreprocessTexture(Texture2D tex)
    {
        var tensor = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth));
        Color[] pixels = tex.GetPixels();

        for (int y = 0; y < inputHeight; y++)
        {
            for (int x = 0; x < inputWidth; x++)
            {
                Color px = pixels[y * inputWidth + x];
                // Нормализация 0-1
                tensor[0, 0, inputHeight - 1 - y, x] = px.r;
                tensor[0, 1, inputHeight - 1 - y, x] = px.g;
                tensor[0, 2, inputHeight - 1 - y, x] = px.b;
            }
        }

        return tensor;
    }

    // ────────────────────────────────────────────────────────────────────────
    // POSTPROCESS
    // ────────────────────────────────────────────────────────────────────────

    List<PoseDetection> PostProcess(Tensor<float> output)
    {
        // Получаем данные из тензора
        // Shape: [1, num_classes, num_anchors]
        // Для YOLO11-pose: [1, 56, 8400]
        // где 56 = 4 (bbox: cx,cy,w,h) + 1 (conf) + 51 (17 keypoints * 3 values)
        
        int batch = output.shape[0];      // 1
        int numFields = output.shape[1];  // 56
        int numAnchors = output.shape[2]; // 8400
        
        if (showDebugLogs)
            Log($"Output shape: [{batch}, {numFields}, {numAnchors}]");

        var rawDetections = new List<PoseDetection>();

        // Данные доступны напрямую через индексы
        for (int i = 0; i < numAnchors; i++)
        {
            // Конфиденция на индексе 4
            float conf = output[0, CONF_OFFSET, i];
            if (conf < confidenceThreshold) continue;

            // BBox: cx, cy, w, h (индексы 0-3)
            float cx = output[0, 0, i];
            float cy = output[0, 1, i];
            float w  = output[0, 2, i];
            float h  = output[0, 3, i];

            Rect bbox = new Rect(
                (cx - w / 2f) * inputWidth,
                (cy - h / 2f) * inputHeight,
                w * inputWidth,
                h * inputHeight
            );

            // Keypoints: начинаются с индекса 5
            // Каждый keypoint имеет 3 значения: x, y, confidence
            Vector3[] kps = new Vector3[NUM_KEYPOINTS];
            for (int k = 0; k < NUM_KEYPOINTS; k++)
            {
                int xIdx = KP_OFFSET + k * VALUES_PER_KP;      // x
                int yIdx = KP_OFFSET + k * VALUES_PER_KP + 1;  // y
                int cIdx = KP_OFFSET + k * VALUES_PER_KP + 2;  // confidence
                
                kps[k] = new Vector3(
                    output[0, xIdx, i],
                    output[0, yIdx, i],
                    output[0, cIdx, i]
                );
            }

            rawDetections.Add(new PoseDetection
            {
                bbox            = bbox,
                confidence      = conf,
                keypointsScreen = kps
            });

            if (rawDetections.Count >= maxDetections * 2) break;
        }

        var filtered = NMS(rawDetections);
        if (filtered.Count > maxDetections)
            filtered = filtered.GetRange(0, maxDetections);

        ConvertToWorldCoords(filtered);
        return filtered;
    }

    // ────────────────────────────────────────────────────────────────────────
    // NMS
    // ────────────────────────────────────────────────────────────────────────

    List<PoseDetection> NMS(List<PoseDetection> detections)
    {
        detections.Sort((a, b) => b.confidence.CompareTo(a.confidence));
        var result = new List<PoseDetection>();

        while (detections.Count > 0 && result.Count < maxDetections)
        {
            var best = detections[0];
            result.Add(best);
            detections.RemoveAt(0);
            detections.RemoveAll(d => IoU(best.bbox, d.bbox) > iouThreshold);
        }

        return result;
    }

    float IoU(Rect a, Rect b)
    {
        float ix1 = Mathf.Max(a.xMin, b.xMin);
        float iy1 = Mathf.Max(a.yMin, b.yMin);
        float ix2 = Mathf.Min(a.xMax, b.xMax);
        float iy2 = Mathf.Min(a.yMax, b.yMax);

        float interArea = Mathf.Max(0, ix2 - ix1) * Mathf.Max(0, iy2 - iy1);
        float unionArea = a.width * a.height + b.width * b.height - interArea;

        return unionArea > 0 ? interArea / unionArea : 0f;
    }

    // ────────────────────────────────────────────────────────────────────────
    // WORLD COORDS
    // ────────────────────────────────────────────────────────────────────────

    void ConvertToWorldCoords(List<PoseDetection> detections)
    {
        foreach (var det in detections)
        {
            bool anyValid = false;
            for (int k = 0; k < NUM_KEYPOINTS; k++)
            {
                Vector3 kp = det.keypointsScreen[k];
                if (kp.z < keypointThreshold)
                {
                    det.keypointsWorld[k] = Vector3.zero;
                    continue;
                }

                Vector3 screenPos = new Vector3(
                    kp.x * Screen.width,
                    (1f - kp.y) * Screen.height,
                    droneCamera.nearClipPlane
                );

                Ray ray = droneCamera.ScreenPointToRay(screenPos);

                det.keypointsWorld[k] = Physics.Raycast(ray, out RaycastHit hit, 100f)
                    ? hit.point
                    : ray.GetPoint(5f);

                anyValid = true;
            }
            det.hasWorldCoords = anyValid;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PUBLIC API
    // ────────────────────────────────────────────────────────────────────────

    public void SetActive(bool active)
    {
        isRunning = active;
        Log($"Детектор {(active ? "ЗАПУЩЕН" : "ОСТАНОВЛЕН")}");
    }

    public bool IsRunning => isRunning;
    public float GetLastInferenceTime() => lastInferenceTime;
    public float GetAvgInferenceTime()  => avgInferenceTime;
    public float GetCurrentFPS()        => lastFPS;

    void Log(string msg) { if (showDebugLogs) Debug.Log($"[YOLO] {msg}"); }
}