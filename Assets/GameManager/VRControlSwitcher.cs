using UnityEngine;
using UnityEngine.InputSystem;
using Unity.XR.CoreUtils;
using System.Collections;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Переключатель режимов VR.
/// 
/// Кнопка Toggle  — переключить режим дрон/человек
/// Кнопка Y       — включить/выключить YOLO детекцию (только в режиме дрона)
/// 
/// В режиме ЧЕЛОВЕКА:
///   - DroneMovementVR выключен
///   - DronePassiveHover включён → дрон висит на месте
///   - YOLO выключена (включается только кнопкой Y в режиме дрона)
/// 
/// В режиме ДРОНА:
///   - DronePassiveHover выключен
///   - DroneMovementVR включён
///   - YOLO — управляется кнопкой Y
/// </summary>
public class VRControlSwitcher : MonoBehaviour
{
    [Header("XR Rig")]
    [SerializeField] private XROrigin xrOrigin;
    [SerializeField] private CharacterController characterController;

    [Header("Locomotion Providers (Action Based)")]
    [SerializeField] private ActionBasedContinuousMoveProvider continuousMoveProvider;
    [SerializeField] private ActionBasedSnapTurnProvider snapTurnProvider;
    [SerializeField] private ActionBasedContinuousTurnProvider continuousTurnProvider;
    [SerializeField] private LocomotionProvider[] additionalLocomotionProviders;

    [Header("Controllers")]
    [SerializeField] private MonoBehaviour droneController;

    [Header("Drone Passive Hover")]
    [Tooltip("DronePassiveHover на объекте дрона — держит дрон в режиме человека")]
    [SerializeField] private DronePassiveHover dronePassiveHover;

    [Header("Drone Detection System")]
    [SerializeField] private YOLOPoseDetector yoloDetector;
    [SerializeField] private HumanVisualizationManager visualizationManager;

    [Header("Drone Camera (монитор)")]
    [SerializeField] private DroneCameraController droneCameraController;

    [Header("Input")]
    [SerializeField] private InputActionReference toggleAction;   // переключить режим

    // Кнопка Y правого контроллера — задаётся прямо здесь, Asset не нужен
    private InputAction yoloAction = new InputAction(
        name: "YoloToggle",
        type: InputActionType.Button,
        binding: "<XRController>{LeftHand}/secondaryButton"
    );

    [Header("Drone Physics")]
    [SerializeField] private Rigidbody droneRb;

    [Header("Transition Settings")]
    [SerializeField] private bool useSmoothTransition = true;

    [Header("Fade Effect (Optional)")]
    [SerializeField] private CanvasGroup fadeCanvas;
    [SerializeField] private float fadeDuration = 0.2f;

    [Header("Audio (Optional)")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip switchToDroneSound;
    [SerializeField] private AudioClip switchToHumanSound;

    [Header("Gravity Settings")]
    [SerializeField] private float gravityStrength = 9.81f;
    [SerializeField] private float maxFallSpeed    = 20f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = true;

    private bool  droneMode;
    private bool  isTransitioning;
    private bool  yoloEnabled = false;   // YOLO выключена по умолчанию
    private float verticalVelocity = 0f;
    private float cachedCCStepOffset;

    // ────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        if (toggleAction != null)
        {
            toggleAction.action.Enable();
            toggleAction.action.performed += OnToggle;
        }

        yoloAction.Enable();
        yoloAction.performed += OnYoloToggle;
    }

    void OnDisable()
    {
        if (toggleAction != null)
        {
            toggleAction.action.performed -= OnToggle;
            toggleAction.action.Disable();
        }

        yoloAction.performed -= OnYoloToggle;
        yoloAction.Disable();
    }

    void Start()
    {
        if (xrOrigin == null)
        {
            Debug.LogError("[VRControlSwitcher] XROrigin не назначен!");
            enabled = false;
            return;
        }

        if (characterController != null)
            cachedCCStepOffset = characterController.stepOffset;

        // YOLO выключена при старте — включается только кнопкой Y в режиме дрона
        if (yoloDetector != null)
            yoloDetector.SetActive(false);

        // Стартуем в режиме человека
        SetDroneModeActive(false);
        Log("Инициализирован — режим ЧЕЛОВЕКА. Y = YOLO toggle (только в режиме дрона)");
    }

    void Update()
    {
        if (!CanApplyGravity()) return;

        verticalVelocity = characterController.isGrounded
            ? -0.5f
            : Mathf.Max(verticalVelocity - gravityStrength * Time.deltaTime, -maxFallSpeed);

        characterController.Move(Vector3.up * verticalVelocity * Time.deltaTime);
    }

    bool CanApplyGravity()
    {
        if (isTransitioning) return false;
        if (characterController == null) return false;
        if (!characterController.enabled) return false;
        if (!characterController.gameObject.activeInHierarchy) return false;
        return true;
    }

    // ── Переключение режима ──────────────────────────────────────────

    void OnToggle(InputAction.CallbackContext ctx)
    {
        if (isTransitioning) return;
        droneMode = !droneMode;
        Log($"TOGGLE → {(droneMode ? "DRONE" : "HUMAN")}");

        if (useSmoothTransition)
            StartCoroutine(SmoothTransition(droneMode));
        else
            SetDroneModeActive(droneMode);
    }

    // ── Кнопка Y: YOLO вкл/выкл в любом режиме ─────────────────────

    void OnYoloToggle(InputAction.CallbackContext ctx)
    {
        yoloEnabled = !yoloEnabled;

        if (yoloDetector != null)
            yoloDetector.SetActive(yoloEnabled);
        else
            Debug.LogWarning("[VRControlSwitcher] yoloDetector == null!");

        if (visualizationManager != null)
            visualizationManager.SetVisible(yoloEnabled);
        else
            Debug.LogWarning("[VRControlSwitcher] visualizationManager == null! Назначь в инспекторе.");

        Log($"YOLO → {(yoloEnabled ? "ВКЛ ✓" : "ВЫКЛ ✗")} | Скелеты → {(yoloEnabled ? "ON" : "OFF")}");
    }

    // ── Плавный переход ─────────────────────────────────────────────

    IEnumerator SmoothTransition(bool toDrone)
    {
        isTransitioning = true;

        if (fadeCanvas != null)
            yield return StartCoroutine(Fade(0f, 1f));

        PlaySwitchSound(toDrone);
        SetDroneModeActive(toDrone);

        if (fadeCanvas != null)
            yield return StartCoroutine(Fade(1f, 0f));

        isTransitioning = false;
    }

    // ── Основное переключение ────────────────────────────────────────

    void SetDroneModeActive(bool drone)
    {
        if (drone)
        {
            // ── РЕЖИМ ДРОНА ──────────────────────────────────────────
            // 1. Пассивный hover ВЫКЛЮЧАЕМ — управление берёт DroneMovementVR
            // 2. DroneMovementVR ВКЛЮЧАЕМ
            // 3. Монитор включаем
            // 4. Locomotion игрока выключаем
            // 5. YOLO — восстанавливаем последнее состояние (yoloEnabled)

            if (dronePassiveHover != null)
                dronePassiveHover.enabled = false;

            if (droneController != null)
                droneController.enabled = true;

            if (droneCameraController != null)
                droneCameraController.SetCameraActive(true);

            SetPlayerLocomotion(false);

            // Восстанавливаем состояние YOLO которое было когда уходили из режима дрона
            if (yoloDetector != null)
                yoloDetector.SetActive(yoloEnabled);

            Log($"DRONE MODE ✓ | YOLO={yoloEnabled}");
        }
        else
        {
            // ── РЕЖИМ ЧЕЛОВЕКА ────────────────────────────────────────
            // 1. DroneMovementVR ВЫКЛЮЧАЕМ (он сохранит hoverTargetY)
            // 2. Пассивный hover ВКЛЮЧАЕМ → дрон висит на месте
            // 3. Монитор выключаем
            // 4. Locomotion игрока включаем
            // 5. YOLO ВЫКЛЮЧАЕМ — она только в режиме дрона по кнопке Y

            if (droneController != null)
                droneController.enabled = false;

            // Пассивный hover включается ПОСЛЕ выключения DroneMovementVR,
            // чтобы успел сохраниться hoverTargetY в OnDisable
            if (dronePassiveHover != null)
                dronePassiveHover.enabled = true;

            if (droneCameraController != null)
                droneCameraController.SetCameraActive(false);

            // YOLO НЕ трогаем — состояние сохраняется, кнопка Y работает в любом режиме

            SetPlayerLocomotion(true);

            Log($"HUMAN MODE ✓ | дрон висит (PassiveHover) | YOLO={yoloEnabled}");
        }
    }

    // ── Locomotion ───────────────────────────────────────────────────

    void SetPlayerLocomotion(bool active)
    {
        if (characterController != null)
        {
            if (active)
            {
                float safeMax = (characterController.height + characterController.radius * 2f) * 0.99f;
                characterController.stepOffset = Mathf.Min(cachedCCStepOffset, safeMax);
                verticalVelocity = 0f;
            }
            characterController.enabled = active;
        }

        if (continuousMoveProvider  != null) continuousMoveProvider.enabled  = active;
        if (snapTurnProvider        != null) snapTurnProvider.enabled        = active;
        if (continuousTurnProvider  != null) continuousTurnProvider.enabled  = active;

        if (additionalLocomotionProviders != null)
            foreach (var p in additionalLocomotionProviders)
                if (p != null) p.enabled = active;

        Log($"Locomotion → {(active ? "ON" : "OFF")}");
    }

    // ── Утилиты ──────────────────────────────────────────────────────

    IEnumerator Fade(float from, float to)
    {
        float elapsed = 0f;
        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            fadeCanvas.alpha = Mathf.Lerp(from, to, elapsed / fadeDuration);
            yield return null;
        }
        fadeCanvas.alpha = to;
    }

    void PlaySwitchSound(bool toDrone)
    {
        if (audioSource == null) return;
        var clip = toDrone ? switchToDroneSound : switchToHumanSound;
        if (clip != null) audioSource.PlayOneShot(clip);
    }

    void Log(string msg)
    {
        if (showDebugLogs) Debug.Log($"[VRControlSwitcher] {msg}");
    }

    // ── Public API ───────────────────────────────────────────────────

    public bool IsDroneMode     => droneMode;
    public bool IsTransitioning => isTransitioning;
    public bool IsYoloActive    => yoloEnabled;

    public void SwitchToDrone() { if (!droneMode  && !isTransitioning) OnToggle(default); }
    public void SwitchToHuman() { if (droneMode   && !isTransitioning) OnToggle(default); }
}