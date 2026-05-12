using UnityEngine;

/// <summary>
/// Камера дрона рендерит в RenderTexture которая отображается на мониторе в сцене.
/// YOLOPoseDetector читает с той же текстуры — повторный Camera.Render() не нужен.
/// Вешать на тот же GameObject что и Camera дрона.
/// </summary>
[RequireComponent(typeof(Camera))]
public class DroneCameraController : MonoBehaviour
{
    [Header("Render Texture")]
    [Tooltip("RenderTexture для монитора. Назначь её же на материал монитора И в YOLOPoseDetector.")]
    [SerializeField] private RenderTexture droneRenderTexture;

    [Header("Camera Settings")]
    [SerializeField] private float fieldOfView = 60f;
    [Tooltip("Рендерить каждые N кадров. 1 = каждый кадр, 2-3 = экономия GPU.")]
    [SerializeField] [Range(1, 10)] private int renderEveryNFrames = 1;

    [Header("Stabilization")]
    [Tooltip("Стабилизировать горизонт — убирает крен дрона с картинки")]
    [SerializeField] private bool stabilizeHorizon = true;
    [SerializeField] private float stabilizationSpeed = 5f;

    [Header("Monitor Reference (опционально)")]
    [Tooltip("Renderer монитора — скрипт сам назначит текстуру на материал")]
    [SerializeField] private Renderer monitorRenderer;


    private Camera droneCamera;
    private int frameCounter;

    private void Awake()
    {
        droneCamera = GetComponent<Camera>();
        droneCamera.fieldOfView = fieldOfView;

        if (droneRenderTexture != null)
            droneCamera.targetTexture = droneRenderTexture;
        else
            Debug.LogWarning("[DroneCameraController] RenderTexture не назначена!");
    }

    private void Start()
    {
        if (monitorRenderer != null && droneRenderTexture != null)
        {
            Material mat = monitorRenderer.material;

            // Перебираем все известные слоты — работает с Built-in, URP и HDRP
            string[] slots = new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_UnlitColorMap" };
            bool assigned = false;

            foreach (var slot in slots)
            {
                if (mat.HasProperty(slot))
                {
                    mat.SetTexture(slot, droneRenderTexture);
                    Debug.Log($"[DroneCameraController] Текстура → {monitorRenderer.gameObject.name} [{slot}]");
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
                Debug.LogWarning("[DroneCameraController] Не удалось найти текстурный слот. " +
                                 "Укажи слот вручную в поле Texture Slot Name.");
        }
    }

    private void LateUpdate()
    {
        if (stabilizeHorizon)
        {
            Vector3 e = transform.eulerAngles;
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.Euler(e.x, e.y, 0f),
                Time.deltaTime * stabilizationSpeed
            );
        }

        if (renderEveryNFrames > 1)
        {
            frameCounter++;
            if (frameCounter >= renderEveryNFrames)
            {
                frameCounter = 0;
                droneCamera.Render();
            }
            droneCamera.enabled = false;
        }
        else
        {
            droneCamera.enabled = true;
        }
    }

    /// <summary>
    /// YOLOPoseDetector вызывает этот метод чтобы получить текстуру автоматически.
    /// </summary>
    public RenderTexture GetRenderTexture() => droneRenderTexture;

    /// <summary>
    /// HumanVisualizationManager берёт transform монитора отсюда
    /// чтобы разместить overlay Quad перед экраном.
    /// </summary>
    public Renderer GetMonitorRenderer() => monitorRenderer;

    public void SetCameraActive(bool active)
    {
        droneCamera.enabled = active;

        if (!active && droneRenderTexture != null)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = droneRenderTexture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;
        }
    }

    private void OnDestroy()
    {
        if (droneCamera != null)
            droneCamera.targetTexture = null;
    }
}