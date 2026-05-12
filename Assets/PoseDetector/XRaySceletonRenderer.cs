using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Рисует скелет людей поверх всей геометрии (X-Ray эффект).
/// Данные берёт из YOLOPoseDetector.LastDetections (keypointsWorld).
/// 
/// Как работает:
/// GL.Lines с ZTest Off рисуются ПОСЛЕ всего рендера сцены — они всегда поверх,
/// даже если человек за стеной.
///
/// Настройка:
/// 1. Повесь на любой GameObject в сцене (например на Main Camera или отдельный менеджер)
/// 2. Назначь yoloDetector и targetCamera
/// </summary>
public class XRaySkeletonRenderer : MonoBehaviour
{
    [Header("Источник данных")]
    [Tooltip("YOLOPoseDetector с которого берём keypointsWorld")]
    [SerializeField] private YOLOPoseDetector yoloDetector;

    [Header("Камера для рендера")]
    [Tooltip("Камера через которую игрок смотрит на монитор (Main Camera или камера дрона)")]
    [SerializeField] private Camera targetCamera;

    [Header("Внешний вид")]
    [Tooltip("Цвет линий скелета")]
    [SerializeField] private Color skeletonColor = new Color(0f, 1f, 0.8f, 1f); // циановый
    [Tooltip("Цвет суставов (точки)")]
    [SerializeField] private Color jointColor = new Color(1f, 1f, 0f, 1f); // жёлтый
    [Tooltip("Толщина линий (в мировых единицах, рисуем через несколько параллельных лучей)")]
    [SerializeField] [Range(0.01f, 0.1f)] private float lineThickness = 0.03f;
    [Tooltip("Размер точки сустава")]
    [SerializeField] [Range(0.02f, 0.15f)] private float jointSize = 0.05f;
    [Tooltip("Минимальный confidence ключевой точки для отрисовки")]
    [SerializeField] [Range(0f, 1f)] private float minKeypointConfidence = 0.3f;

    [Header("Фильтрация костей")]
    [Tooltip("Рисовать верхнюю часть тела (плечи, руки)")]
    [SerializeField] private bool showUpperBody = true;
    [Tooltip("Рисовать нижнюю часть тела (ноги)")]
    [SerializeField] private bool showLowerBody = true;
    [Tooltip("Рисовать голову и лицо")]
    [SerializeField] private bool showHead = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugInfo = false;

    // GL материал — ZTest Off чтобы рисовать поверх всего
    private Material glMaterial;

    // Пары индексов костей из YOLOPoseDetector.SkeletonPairs:
    // 0=nose 1=left_eye 2=right_eye 3=left_ear 4=right_ear
    // 5=left_shoulder 6=right_shoulder 7=left_elbow 8=right_elbow
    // 9=left_wrist 10=right_wrist 11=left_hip 12=right_hip
    // 13=left_knee 14=right_knee 15=left_ankle 16=right_ankle

    // Голова: 0-1, 0-2, 1-3, 2-4
    private static readonly int[] headBoneIndices = { 0, 1, 2, 3 };

    // Верх: 5-6, 5-11, 6-12, 5-7, 7-9, 6-8, 8-10
    private static readonly int[] upperBoneIndices = { 4, 5, 6, 7, 8, 9, 10 };

    // Низ: 11-12, 11-13, 13-15, 12-14, 14-16
    private static readonly int[] lowerBoneIndices = { 11, 12, 13, 14, 15 };

    private void Awake()
    {
        CreateGLMaterial();

        if (targetCamera == null)
            targetCamera = Camera.main;
    }

    private void OnEnable()
    {
        Camera.onPostRender += OnPostRenderCallback;
    }

    private void OnDisable()
    {
        Camera.onPostRender -= OnPostRenderCallback;
    }

    private void OnDestroy()
    {
        if (glMaterial != null)
            Destroy(glMaterial);
    }

    /// <summary>
    /// Создаём GL материал с ZTest Always — рисуется поверх всего.
    /// </summary>
    private void CreateGLMaterial()
    {
        // Стандартный скрытый шейдер Unity для GL рендеринга
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            // Fallback если скрытый шейдер недоступен
            shader = Shader.Find("Unlit/Color");
            Debug.LogWarning("[XRaySkeleton] Hidden/Internal-Colored не найден, используем Unlit/Color");
        }

        glMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        // Ключевые настройки для X-Ray:
        glMaterial.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glMaterial.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glMaterial.SetInt("_Cull",      (int)UnityEngine.Rendering.CullMode.Off);
        // ZTest Always = рисуем поверх всей геометрии включая стены
        glMaterial.SetInt("_ZTest",     (int)UnityEngine.Rendering.CompareFunction.Always);
        glMaterial.SetInt("_ZWrite",    0);
    }

    /// <summary>
    /// Вызывается после рендера каждой камеры.
    /// Рисуем только для targetCamera.
    /// </summary>
    private void OnPostRenderCallback(Camera cam)
    {
        if (cam != targetCamera) return;
        if (yoloDetector == null) return;
        if (yoloDetector.LastDetections == null || yoloDetector.LastDetections.Count == 0) return;

        DrawSkeletons(yoloDetector.LastDetections);
    }

    private void DrawSkeletons(List<YOLOPoseDetector.PoseDetection> detections)
    {
        glMaterial.SetPass(0);

        GL.PushMatrix();
        // Матрица для мирового пространства
        GL.MultMatrix(Matrix4x4.identity);

        foreach (var det in detections)
        {
            if (!det.hasWorldCoords) continue;
            DrawSkeleton(det);
        }

        GL.PopMatrix();
    }

    private void DrawSkeleton(YOLOPoseDetector.PoseDetection det)
    {
        Vector3[] kp = det.keypointsWorld;
        float[]   conf = new float[YOLOPoseDetector.NUM_KEYPOINTS];

        // Достаём confidence из keypointsScreen.z
        for (int i = 0; i < YOLOPoseDetector.NUM_KEYPOINTS; i++)
            conf[i] = det.keypointsScreen[i].z;

        // Рисуем кости
        GL.Begin(GL.LINES);
        GL.Color(skeletonColor);

        var pairs = YOLOPoseDetector.SkeletonPairs;
        for (int p = 0; p < pairs.Length; p++)
        {
            int a = pairs[p].Item1;
            int b = pairs[p].Item2;

            // Фильтрация по группам
            if (!ShouldDrawBone(p)) continue;

            // Оба сустава должны иметь достаточный confidence
            if (conf[a] < minKeypointConfidence || conf[b] < minKeypointConfidence) continue;
            if (kp[a] == Vector3.zero || kp[b] == Vector3.zero) continue;

            // Основная линия
            GL.Vertex(kp[a]);
            GL.Vertex(kp[b]);

            // Дополнительные параллельные линии для имитации толщины
            if (lineThickness > 0.01f)
            {
                // Смещаем линию на небольшой offset в нескольких направлениях
                Vector3 dir = (kp[b] - kp[a]).normalized;
                Vector3 perp = Vector3.Cross(dir, targetCamera.transform.forward).normalized * lineThickness;

                GL.Vertex(kp[a] + perp);
                GL.Vertex(kp[b] + perp);

                GL.Vertex(kp[a] - perp);
                GL.Vertex(kp[b] - perp);
            }
        }

        GL.End();

        // Рисуем суставы (маленькие крестики вместо точек — GL не умеет точки с размером)
        GL.Begin(GL.LINES);
        GL.Color(jointColor);

        for (int i = 0; i < YOLOPoseDetector.NUM_KEYPOINTS; i++)
        {
            if (conf[i] < minKeypointConfidence) continue;
            if (kp[i] == Vector3.zero) continue;
            if (!ShouldDrawJoint(i)) continue;

            DrawJointCross(kp[i]);
        }

        GL.End();

        if (showDebugInfo)
            Debug.Log($"[XRaySkeleton] Нарисован скелет, confidence={det.confidence:F2}");
    }

    /// <summary>
    /// Рисует крестик в мировой точке — имитация точки сустава.
    /// </summary>
    private void DrawJointCross(Vector3 pos)
    {
        // Крестик в плоскости камеры
        Vector3 right = targetCamera.transform.right   * jointSize;
        Vector3 up    = targetCamera.transform.up      * jointSize;

        GL.Vertex(pos - right); GL.Vertex(pos + right);
        GL.Vertex(pos - up);    GL.Vertex(pos + up);
    }

    /// <summary>
    /// Определяет нужно ли рисовать кость по её индексу в SkeletonPairs.
    /// </summary>
    private bool ShouldDrawBone(int pairIndex)
    {
        // SkeletonPairs: 0-3 голова, 4-10 верх, 11-15 низ
        if (pairIndex <= 3)  return showHead;
        if (pairIndex <= 10) return showUpperBody;
        return showLowerBody;
    }

    /// <summary>
    /// Определяет нужно ли рисовать сустав по его индексу.
    /// </summary>
    private bool ShouldDrawJoint(int index)
    {
        if (index <= 4)  return showHead;
        if (index <= 10) return showUpperBody;
        return showLowerBody;
    }

    /// <summary>
    /// Публичный метод для включения/выключения X-Ray из VRControlSwitcher.
    /// </summary>
    public void SetXRayActive(bool active)
    {
        enabled = active;
    }
}