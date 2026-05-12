using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// VR управление дроном.
/// Левый стик: вперёд/назад/влево/вправо
/// Правый стик X: поворот (yaw)
/// Кнопка A: подъём, Кнопка B: спуск
/// Когда ничего не нажато — дрон зависает на текущей высоте.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DroneMovementVR : MonoBehaviour
{
    [Header("Input Actions")]
    public InputActionReference moveAction;       // Левый стик (Vector2)
    public InputActionReference rotateAction;     // Правый стик (Vector2)
    public InputActionReference upAction;         // Кнопка A — подъём
    public InputActionReference downAction;       // Кнопка B — спуск

    [Header("Forces")]
    [Tooltip("Сила подъёма при нажатии A")]
    public float thrustUpForce   = 25f;
    [Tooltip("Сила спуска при нажатии B")]
    public float thrustDownForce = 15f;
    [Tooltip("Скорость движения вперёд/назад")]
    public float forwardSpeed    = 10f;
    [Tooltip("Скорость бокового движения")]
    public float sidewaysSpeed   = 10f;
    [Tooltip("Скорость поворота (yaw), градусов/сек")]
    public float yawSpeed        = 60f;

    [Header("Hover & Damping")]
    [Tooltip("Жёсткость удержания высоты (P-коэффициент)")]
    public float hoverStiffness  = 15f;
    [Tooltip("Демпфирование вертикальной скорости")]
    public float hoverDamping    = 8f;
    [Tooltip("Торможение горизонтального скольжения")]
    public float horizontalDrag  = 3f;

    [Header("Tilt (визуальный наклон)")]
    public float maxTiltAngle    = 20f;
    public float tiltSmoothing   = 0.1f;

    [Header("Audio")]
    public AudioSource droneSound;
    [SerializeField] private float minPitch      = 0.8f;
    [SerializeField] private float maxPitch      = 1.5f;
    [SerializeField] private float speedInfluence = 0.5f;

    [Header("Visual Effects")]
    public ParticleSystem[] propellerEffects;
    public Transform[]      propellerModels;
    public float            propellerRotationSpeed = 1500f;

    [Header("Debug")]
    public bool showDebugInfo = true;

    // ── приватные поля ─────────────────────────────────────────────
    private Rigidbody rb;

    private float hoverTargetY;          // целевая высота
    private bool  isHovering = false;

    private float currentYaw = 0f;

    private float tiltForward;
    private float tiltVelForward;
    private float tiltSideways;
    private float tiltVelSideways;

    private float upForce = 0f;

    // Сохранённая высота между сессиями управления
    private float savedHoverY    = 0f;
    private bool  hasSavedHoverY = false;

    // ───────────────────────────────────────────────────────────────

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass        = 2f;
        rb.drag        = 0f;    // drag управляем вручную
        rb.angularDrag = 99f;   // запрещаем физическое угловое вращение
        rb.useGravity  = true;
        rb.constraints = RigidbodyConstraints.FreezeRotation; // поворот — только MoveRotation

        if (droneSound == null)
        {
            Transform s = transform.Find("drone_sound");
            if (s != null) droneSound = s.GetComponent<AudioSource>();
        }
    }

    void OnEnable()
    {
        if (moveAction)   moveAction.action.Enable();
        if (rotateAction) rotateAction.action.Enable();
        if (upAction)     upAction.action.Enable();
        if (downAction)   downAction.action.Enable();

        // Восстанавливаем сохранённую высоту если она есть (переключение режимов)
        if (hasSavedHoverY)
        {
            hoverTargetY = savedHoverY;
        }
        else
        {
            hoverTargetY = transform.position.y;
        }
        isHovering = true;
        currentYaw = transform.eulerAngles.y;

        rb.velocity        = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        tiltForward = tiltSideways = 0f;

        SetPropellerEffects(true);
    }

    void OnDisable()
    {
        if (moveAction)   moveAction.action.Disable();
        if (rotateAction) rotateAction.action.Disable();
        if (upAction)     upAction.action.Disable();
        if (downAction)   downAction.action.Disable();

        // Сохраняем текущую высоту hover-а перед отключением
        savedHoverY    = hoverTargetY;
        hasSavedHoverY = true;

        // Гасим скорость но НЕ трогаем вертикаль — hover подхватит её при включении
        rb.velocity        = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        SetPropellerEffects(false);
        if (droneSound) droneSound.Stop();
    }

    void FixedUpdate()
    {
        // ── ввод ──────────────────────────────────────────────────
        Vector2 move   = moveAction   != null ? moveAction.action.ReadValue<Vector2>()   : Vector2.zero;
        Vector2 rotate = rotateAction != null ? rotateAction.action.ReadValue<Vector2>() : Vector2.zero;
        bool    goUp   = upAction   != null && upAction.action.IsPressed();
        bool    goDown = downAction != null && downAction.action.IsPressed();

        float forwardInput  = -move.y;   // +1 = вперёд,  -1 = назад
        float sidewaysInput = -move.x;   // +1 = вправо,  -1 = влево
        float yawInput      = rotate.x;  // +1 = поворот вправо

        // ── вертикаль ─────────────────────────────────────────────
        HandleVertical(goUp, goDown);

        // ── горизонталь ───────────────────────────────────────────
        HandleHorizontal(forwardInput, sidewaysInput);

        // ── торможение горизонтального скольжения ─────────────────
        Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        rb.AddForce(-hVel * horizontalDrag, ForceMode.Force);

        // ── ограничение скорости (как в оригинале) ────────────────
        if (rb.velocity.magnitude > 15f)
            rb.velocity = Vector3.ClampMagnitude(rb.velocity,
                Mathf.Lerp(rb.velocity.magnitude, 15f, Time.deltaTime * 5f));

        // ── поворот ───────────────────────────────────────────────
        HandleYaw(yawInput);

        // ── наклон ────────────────────────────────────────────────
        HandleTilt(forwardInput, sidewaysInput);

        // ── применяем вращение ────────────────────────────────────
        rb.MoveRotation(Quaternion.Euler(tiltForward, currentYaw, -tiltSideways));

        // ── звук и пропеллеры ─────────────────────────────────────
        DroneSound();
        UpdatePropellers();
    }

    // ── вертикаль с hover-ом ───────────────────────────────────────
    void HandleVertical(bool goUp, bool goDown)
    {
        float counterGrav = Physics.gravity.magnitude * rb.mass; // сила против гравитации

        if (goUp)
        {
            upForce      = counterGrav + thrustUpForce * rb.mass;
            hoverTargetY = transform.position.y; // обновляем цель пока летим
            isHovering   = false;
            rb.AddForce(Vector3.up * upForce, ForceMode.Force);
        }
        else if (goDown)
        {
            upForce      = counterGrav - thrustDownForce * rb.mass;
            hoverTargetY = transform.position.y;
            isHovering   = false;
            rb.AddForce(Vector3.up * upForce, ForceMode.Force);
        }
        else
        {
            // ── HOVER: фиксируем высоту ───────────────────────────
            if (!isHovering)
            {
                hoverTargetY = transform.position.y;
                isHovering   = true;
            }

            // PD-регулятор высоты
            float error     = hoverTargetY - transform.position.y;
            float velY      = rb.velocity.y;
            float hoverF    = counterGrav
                            + error * hoverStiffness * rb.mass
                            - velY  * hoverDamping   * rb.mass;

            upForce = hoverF;
            rb.AddForce(Vector3.up * hoverF, ForceMode.Force);
        }
    }

    // ── горизонталь ───────────────────────────────────────────────
    void HandleHorizontal(float fwd, float side)
    {
        if (Mathf.Abs(fwd) > 0.01f)
            rb.AddRelativeForce(Vector3.forward * fwd  * forwardSpeed  * rb.mass, ForceMode.Force);

        if (Mathf.Abs(side) > 0.01f)
            rb.AddRelativeForce(Vector3.right   * side * sidewaysSpeed * rb.mass, ForceMode.Force);
    }

    // ── поворот (yaw) ─────────────────────────────────────────────
    void HandleYaw(float yawInput)
    {
        if (Mathf.Abs(yawInput) > 0.05f)
            currentYaw += yawInput * yawSpeed * Time.fixedDeltaTime;
    }

    // ── визуальный наклон ─────────────────────────────────────────
    void HandleTilt(float fwd, float side)
    {
        tiltForward  = Mathf.SmoothDamp(tiltForward,  maxTiltAngle * fwd,  ref tiltVelForward,  tiltSmoothing);
        tiltSideways = Mathf.SmoothDamp(tiltSideways, maxTiltAngle * side, ref tiltVelSideways, tiltSmoothing);
    }

    // ── звук ──────────────────────────────────────────────────────
    void DroneSound()
    {
        if (droneSound == null) return;
        if (!droneSound.isPlaying) droneSound.Play();

        float thrustFactor = Mathf.InverseLerp(0f, 450f, Mathf.Abs(upForce));
        float hSpeed       = new Vector3(rb.velocity.x, 0, rb.velocity.z).magnitude;
        float speedFactor  = hSpeed / 20f;

        float target = Mathf.Clamp(
            minPitch + (maxPitch - minPitch) * (thrustFactor + speedFactor * speedInfluence),
            minPitch, maxPitch);

        droneSound.pitch = Mathf.Lerp(droneSound.pitch, target, Time.deltaTime * 10f);
    }

    // ── пропеллеры ────────────────────────────────────────────────
    void UpdatePropellers()
    {
        if (propellerModels == null) return;
        float factor = Mathf.InverseLerp(0f, 450f, Mathf.Abs(upForce));
        float speed  = propellerRotationSpeed * (0.3f + factor * 0.7f);
        foreach (var p in propellerModels)
            if (p) p.Rotate(Vector3.up, speed * Time.fixedDeltaTime);
    }

    void SetPropellerEffects(bool active)
    {
        if (propellerEffects == null) return;
        foreach (var e in propellerEffects)
            if (e) { if (active) e.Play(); else e.Stop(); }
    }

    // ── Gizmos ────────────────────────────────────────────────────
    void OnDrawGizmos()
    {
        if (!showDebugInfo || !Application.isPlaying) return;

        Gizmos.color = Color.blue;  Gizmos.DrawRay(transform.position, transform.forward * 2f);
        Gizmos.color = Color.red;   Gizmos.DrawRay(transform.position, transform.right   * 2f);
        Gizmos.color = Color.green; Gizmos.DrawRay(transform.position, transform.up      * 2f);
        if (rb != null) { Gizmos.color = Color.yellow; Gizmos.DrawRay(transform.position, rb.velocity); }

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(
            new Vector3(transform.position.x, hoverTargetY, transform.position.z),
            new Vector3(1f, 0.02f, 1f));

#if UNITY_EDITOR
        if (rb != null)
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 3f,
                $"VR DRONE\n" +
                $"Hover Y: {hoverTargetY:F1} | Cur Y: {transform.position.y:F1}\n" +
                $"Speed: {rb.velocity.magnitude:F1} m/s | Yaw: {currentYaw:F0}°",
                new GUIStyle { normal = new GUIStyleState { textColor = Color.white } });
#endif
    }

    // ── Public API ────────────────────────────────────────────────
    public void ResetDrone()
    {
        rb.velocity        = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        transform.rotation = Quaternion.identity;

        hoverTargetY   = transform.position.y;
        savedHoverY    = transform.position.y;
        hasSavedHoverY = false;
        isHovering     = true;
        currentYaw     = 0f;
        tiltForward    = tiltSideways = 0f;
    }

    public float GetThrottle()           => Mathf.InverseLerp(0f, 450f, Mathf.Abs(upForce));
    public float throttle01               => GetThrottle();
    public float GetCurrentSpeed()       => rb.velocity.magnitude;
    public float GetAltitude()           => transform.position.y;
    public float GetHoverTarget()        => hoverTargetY;
    public float GetCurrentYRotation()   => currentYaw;
    public Vector3 GetRotationAngles()   => new Vector3(tiltForward, currentYaw, tiltSideways);
}