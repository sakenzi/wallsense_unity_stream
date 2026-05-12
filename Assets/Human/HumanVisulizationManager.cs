using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Рисует скелет человека двумя способами:
/// 1. LineRenderer — 3D линии в мире над человеком (видны через стены)
/// 2. Overlay Quad — текстура поверх монитора
/// </summary>
public class HumanVisualizationManager : MonoBehaviour
{
    [Header("3D Скелет (LineRenderer)")]
    public bool  show3D          = true;
    public float lineWidth       = 0.05f;
    public Color skeletonColor3D = new Color(0f, 1f, 0.5f, 1f);
    public Color jointColor3D    = new Color(1f, 1f, 0f, 1f);
    public float jointRadius     = 0.06f;
    [Tooltip("Виден через стены (X-Ray эффект)")]
    public bool  xrayMode        = true;
    public Material lineMaterial;

    [Header("Overlay на мониторе")]
    public bool  showOverlay     = true;
    public DroneCameraController droneCameraController;
    public float overlayOffset   = 0.01f;
    public Color skeletonColorUI = new Color(0f, 1f, 0.5f, 1f);
    public Color jointColorUI    = new Color(1f, 0.4f, 0f, 1f);
    public float uiLineWidth     = 4f;
    public float uiJointRadius   = 7f;
    public int   overlayTexWidth  = 512;
    public int   overlayTexHeight = 512;

    [Header("Overlay размер (если авто не работает)")]
    [Tooltip("0 = авто из монитора")]
    public float manualOverlayWidth  = 0f;
    public float manualOverlayHeight = 0f;

    [Header("YOLO")]
    public YOLOPoseDetector yoloDetector;

    [Header("Общие настройки")]
    [Range(0f, 1f)]
    public float keypointThreshold = 0.3f;
    public int   maxPeople         = 5;

    [Header("Debug")]
    public bool forceVisible = false;

    // ── приватные ────────────────────────────────────────────────────
    private class PersonSkeleton3D
    {
        public GameObject    root;
        public LineRenderer[] bones;
        public GameObject[]   joints;
    }

    private List<PersonSkeleton3D> pool3D = new List<PersonSkeleton3D>();

    private Texture2D    overlayTex;
    private Color32[]    overlayPixels;
    private GameObject   overlayQuad;
    private Transform    monitorTransformCached;

    private bool isVisible = false;

    // Материал X-Ray — ZTest Always чтобы рисовать поверх всего
    private Material xrayLineMat;
    private Material xrayJointMat;

    private static readonly (int, int)[] SkeletonPairs = {
        (0,1),(0,2),(1,3),(2,4),
        (5,6),(5,11),(6,12),(11,12),
        (5,7),(7,9),(6,8),(8,10),
        (11,13),(13,15),(12,14),(14,16)
    };
    private const int NUM_KEYPOINTS = 17;

    // ────────────────────────────────────────────────────────────────

    void Awake()
    {
        BuildXRayMaterials();

        for (int i = 0; i < maxPeople; i++)
            pool3D.Add(CreateSkeleton3D(i));
        SetAllVisible3D(false);

        InitOverlay();
    }

    void Start()
    {
        if (forceVisible)
        {
            isVisible = true;
            Debug.Log("[SkeletonViz] forceVisible=true");
        }
    }

    void LateUpdate()
    {
        if (overlayQuad == null || monitorTransformCached == null) return;
        overlayQuad.transform.position = monitorTransformCached.position
                                         - monitorTransformCached.forward * overlayOffset;
        overlayQuad.transform.rotation = monitorTransformCached.rotation;
    }

    // ── X-Ray материалы ──────────────────────────────────────────────
    void BuildXRayMaterials()
    {
        // Используем Hidden/Internal-Colored который поддерживает ZTest
        // Это встроенный Unity шейдер который всегда доступен
        Shader s = Shader.Find("Hidden/Internal-Colored");
        if (s == null) s = Shader.Find("Sprites/Default");

        xrayLineMat = new Material(s);
        xrayLineMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        xrayLineMat.SetInt("_ZWrite", 0);
        xrayLineMat.renderQueue = 5000; // поверх всего

        xrayJointMat = new Material(s);
        xrayJointMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        xrayJointMat.SetInt("_ZWrite", 0);
        xrayJointMat.renderQueue = 5000;
    }

    // ── Init Overlay ─────────────────────────────────────────────────
    void InitOverlay()
    {
        Transform monitorTransform = null;
        if (droneCameraController != null)
        {
            var mr = droneCameraController.GetMonitorRenderer();
            if (mr != null) monitorTransform = mr.transform;
        }

        if (!showOverlay || monitorTransform == null)
        {
            if (showOverlay)
                Debug.LogWarning("[SkeletonViz] monitorTransform не найден — overlay отключён");
            return;
        }

        monitorTransformCached = monitorTransform;

        overlayTex            = new Texture2D(overlayTexWidth, overlayTexHeight, TextureFormat.RGBA32, false);
        overlayTex.filterMode = FilterMode.Bilinear;
        overlayPixels         = new Color32[overlayTexWidth * overlayTexHeight];
        ClearOverlay();

        overlayQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        overlayQuad.name = "SkeletonOverlay";
        Destroy(overlayQuad.GetComponent<MeshCollider>());
        overlayQuad.transform.SetParent(null);

        // Позиция — чуть впереди монитора
        // monitorTransform.forward указывает от монитора к зрителю (или наоборот)
        // пробуй overlayOffset положительный и отрицательный
        overlayQuad.transform.position = monitorTransform.position
                                         - monitorTransform.forward * overlayOffset;
        overlayQuad.transform.rotation = monitorTransform.rotation;

        // Размер — берём реальный мировой размер монитора
        // lossyScale может иметь 0 по одной оси если монитор повёрнут на 90°
        // поэтому берём две наибольшие оси из трёх
        Vector3 ls = monitorTransform.lossyScale;
        float ax = Mathf.Abs(ls.x);
        float ay = Mathf.Abs(ls.y);
        float az = Mathf.Abs(ls.z);

        float sw, sh;
        if (manualOverlayWidth > 0f && manualOverlayHeight > 0f)
        {
            // Ручной размер из инспектора
            sw = manualOverlayWidth;
            sh = manualOverlayHeight;
        }
        else
        {
            // Автоматически: сортируем три значения и берём два наибольших
            float[] vals = new float[] { ax, ay, az };
            System.Array.Sort(vals);
            // vals[2] = max, vals[1] = mid, vals[0] = min (скорее всего ~0)
            sw = vals[2]; // ширина = наибольшее
            sh = vals[1]; // высота = среднее
        }

        overlayQuad.transform.localScale = new Vector3(sw, sh, 1f);

        // Материал с прозрачностью, рисуется поверх монитора
        var mat              = new Material(Shader.Find("Sprites/Default"));
        mat.mainTexture      = overlayTex;
        var mr2              = overlayQuad.GetComponent<MeshRenderer>();
        mr2.material         = mat;
        mr2.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr2.receiveShadows     = false;
        mr2.sortingOrder       = 10;

        overlayQuad.SetActive(false);

        Debug.Log($"[SkeletonViz] Overlay: pos={overlayQuad.transform.position} " +
                  $"scale=({sw:F2},{sh:F2}) lossyScale={ls}");
    }

    // ── Public API ───────────────────────────────────────────────────

    public void SetVisible(bool visible)
    {
        isVisible = visible;
        Debug.Log($"[SkeletonViz] SetVisible({visible})");

        if (!visible)
        {
            SetAllVisible3D(false);
            if (overlayQuad != null) overlayQuad.SetActive(false);
            ClearOverlay();
        }
    }

    public void UpdateFromPoseDetections(
        List<YOLOPoseDetector.PoseDetection> detections,
        Camera droneCamera)
    {
        int count = Mathf.Min(detections != null ? detections.Count : 0, maxPeople);
        if (forceVisible && !isVisible) isVisible = true;

        if (!isVisible)
        {
            SetAllVisible3D(false);
            if (overlayQuad != null) overlayQuad.SetActive(false);
            return;
        }

        // ── 3D ──────────────────────────────────────────────────────
        if (show3D)
        {
            for (int i = 0; i < count; i++)
            {
                Update3DSkeleton(pool3D[i], detections[i], droneCamera);
                pool3D[i].root.SetActive(true);
            }
            for (int i = count; i < pool3D.Count; i++)
                pool3D[i].root.SetActive(false);
        }
        else SetAllVisible3D(false);

        // ── Overlay ──────────────────────────────────────────────────
        if (showOverlay && overlayTex != null)
        {
            ClearOverlay();
            if (count > 0)
            {
                overlayQuad?.SetActive(true);
                for (int i = 0; i < count; i++)
                    DrawOverlaySkeleton(detections[i]);
                overlayTex.SetPixels32(overlayPixels);
                overlayTex.Apply(false);
            }
            else overlayQuad?.SetActive(false);
        }
    }

    // ── 3D Скелет ────────────────────────────────────────────────────

    PersonSkeleton3D CreateSkeleton3D(int index)
    {
        var ps  = new PersonSkeleton3D();
        ps.root = new GameObject($"Skeleton3D_{index}");
        ps.root.transform.SetParent(transform, false);

        // Выбираем материал: X-Ray или обычный
        Material boneMat  = (xrayMode && xrayLineMat  != null) ? xrayLineMat  : (lineMaterial != null ? lineMaterial : new Material(Shader.Find("Sprites/Default")));
        Material jointMat = (xrayMode && xrayJointMat != null) ? xrayJointMat : new Material(Shader.Find("Sprites/Default"));

        ps.bones = new LineRenderer[SkeletonPairs.Length];
        for (int b = 0; b < SkeletonPairs.Length; b++)
        {
            var go               = new GameObject($"Bone_{b}");
            go.transform.SetParent(ps.root.transform, false);
            var lr               = go.AddComponent<LineRenderer>();
            lr.positionCount     = 2;
            lr.startWidth        = lineWidth;
            lr.endWidth          = lineWidth;
            lr.useWorldSpace     = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            lr.material          = boneMat;
            lr.startColor        = skeletonColor3D;
            lr.endColor          = skeletonColor3D;
            ps.bones[b]          = lr;
        }

        ps.joints = new GameObject[NUM_KEYPOINTS];
        for (int k = 0; k < NUM_KEYPOINTS; k++)
        {
            var go               = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name              = $"Joint_{k}";
            go.transform.SetParent(ps.root.transform, false);
            go.transform.localScale = Vector3.one * jointRadius * 2f;
            Destroy(go.GetComponent<Collider>());
            var mr               = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;
            mr.material          = jointMat;
            mr.material.color    = jointColor3D;
            ps.joints[k]         = go;
        }

        return ps;
    }

    void Update3DSkeleton(PersonSkeleton3D ps, YOLOPoseDetector.PoseDetection det, Camera cam)
    {
        var worldPos = new Vector3[NUM_KEYPOINTS];
        var valid    = new bool[NUM_KEYPOINTS];

        for (int k = 0; k < NUM_KEYPOINTS; k++)
        {
            Vector3 kp = det.keypointsScreen[k];
            valid[k]   = kp.z >= keypointThreshold;
            if (!valid[k]) continue;

            if (det.hasWorldCoords && det.keypointsWorld[k] != Vector3.zero)
                worldPos[k] = det.keypointsWorld[k];
            else
                worldPos[k] = cam.ScreenToWorldPoint(new Vector3(
                    kp.x / (yoloDetector != null ? yoloDetector.inputWidth  : 640f) * cam.pixelWidth,
                    (1f - kp.y / (yoloDetector != null ? yoloDetector.inputHeight : 640f)) * cam.pixelHeight,
                    3f));
        }

        for (int b = 0; b < SkeletonPairs.Length; b++)
        {
            int a1 = SkeletonPairs[b].Item1, a2 = SkeletonPairs[b].Item2;
            var lr = ps.bones[b];
            if (valid[a1] && valid[a2]) { lr.enabled = true; lr.SetPosition(0, worldPos[a1]); lr.SetPosition(1, worldPos[a2]); }
            else lr.enabled = false;
        }

        for (int k = 0; k < NUM_KEYPOINTS; k++)
        {
            ps.joints[k].SetActive(valid[k]);
            if (valid[k]) ps.joints[k].transform.position = worldPos[k];
        }
    }

    void SetAllVisible3D(bool v)
    {
        foreach (var ps in pool3D)
            if (ps.root != null) ps.root.SetActive(v);
    }

    // ── Overlay рисование ────────────────────────────────────────────

    void DrawOverlaySkeleton(YOLOPoseDetector.PoseDetection det)
    {
        int w = overlayTexWidth;
        int h = overlayTexHeight;

        float yoloW = yoloDetector != null ? yoloDetector.inputWidth  : 640f;
        float yoloH = yoloDetector != null ? yoloDetector.inputHeight : 640f;

        var px    = new Vector2[NUM_KEYPOINTS];
        var valid = new bool[NUM_KEYPOINTS];

        for (int k = 0; k < NUM_KEYPOINTS; k++)
        {
            Vector3 kp = det.keypointsScreen[k];
            valid[k] = kp.z >= keypointThreshold;

            if (valid[k])
            {
                // ──────── ИЗМЕНИ ЭТУ СТРОКУ ────────
                px[k] = new Vector2((1f - kp.x / yoloW) * w, (1f - kp.y / yoloH) * h);
            }
        }

        Color32 lc = skeletonColorUI;
        Color32 jc = jointColorUI;

        foreach (var pair in SkeletonPairs)
        {
            if (valid[pair.Item1] && valid[pair.Item2])
                DrawLine(px[pair.Item1], px[pair.Item2], lc, (int)uiLineWidth);
        }

        for (int k = 0; k < NUM_KEYPOINTS; k++)
        {
            if (valid[k])
                DrawCircle((int)px[k].x, (int)px[k].y, (int)uiJointRadius, jc);
        }
    }

    void DrawLine(Vector2 a, Vector2 b, Color32 color, int thickness)
    {
        int x0 = (int)a.x, y0 = (int)a.y;
        int x1 = (int)b.x, y1 = (int)b.y;
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy, half = Mathf.Max(0, thickness / 2);

        while (true)
        {
            for (int tx = -half; tx <= half; tx++)
            for (int ty = -half; ty <= half; ty++)
                SetPixelBuf(x0 + tx, y0 + ty, color);

            if (x0 == x1 && y0 == y1) break;
            int e2 = err * 2;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
        }
    }

    void DrawCircle(int cx, int cy, int r, Color32 color)
    {
        for (int x = -r; x <= r; x++)
        for (int y = -r; y <= r; y++)
            if (x * x + y * y <= r * r)
                SetPixelBuf(cx + x, cy + y, color);
    }

    void SetPixelBuf(int x, int y, Color32 col)
    {
        if (x < 0 || x >= overlayTexWidth || y < 0 || y >= overlayTexHeight) return;
        overlayPixels[y * overlayTexWidth + x] = col;
    }

    void ClearOverlay()
    {
        if (overlayPixels == null) return;
        System.Array.Clear(overlayPixels, 0, overlayPixels.Length);
    }

    void OnDestroy()
    {
        foreach (var ps in pool3D)
            if (ps.root != null) Destroy(ps.root);
        pool3D.Clear();
        if (overlayTex  != null) Destroy(overlayTex);
        if (overlayQuad != null) Destroy(overlayQuad);
        if (xrayLineMat  != null) Destroy(xrayLineMat);
        if (xrayJointMat != null) Destroy(xrayJointMat);
    }
}