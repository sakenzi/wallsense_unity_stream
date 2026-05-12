using UnityEngine;

/// <summary>
/// Пассивный hover дрона — работает КОГДА DroneMovementVR выключен (режим человека).
/// Удерживает дрон на той высоте где он завис.
/// Использует тот же PD-регулятор что и DroneMovementVR.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DronePassiveHover : MonoBehaviour
{
    [Header("Hover PD (должны совпадать с DroneMovementVR)")]
    public float hoverStiffness = 15f;
    public float hoverDamping   = 8f;

    [Header("Затухание горизонтального движения")]
    public float horizontalDrag = 5f;

    [Header("Ref — основной скрипт управления")]
    [Tooltip("DroneMovementVR на этом же объекте")]
    public DroneMovementVR droneMovementVR;

    private Rigidbody rb;
    private float hoverTargetY;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (droneMovementVR == null)
            droneMovementVR = GetComponent<DroneMovementVR>();
    }

    void OnEnable()
    {
        // Берём сохранённую целевую высоту из DroneMovementVR
        // Если не получилось — берём текущую позицию
        hoverTargetY = droneMovementVR != null
            ? droneMovementVR.GetHoverTarget()
            : transform.position.y;

        // Гасим скорость чтобы дрон не продолжал лететь
        rb.velocity        = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        Debug.Log($"[PassiveHover] ON — удерживаю Y={hoverTargetY:F2}");
    }

    void OnDisable()
    {
        Debug.Log("[PassiveHover] OFF");
    }

    void FixedUpdate()
    {
        float counterGrav = Physics.gravity.magnitude * rb.mass;

        // PD-регулятор высоты — точно такой же как в DroneMovementVR
        float error  = hoverTargetY - transform.position.y;
        float velY   = rb.velocity.y;
        float force  = counterGrav
                     + error * hoverStiffness * rb.mass
                     - velY  * hoverDamping   * rb.mass;

        rb.AddForce(Vector3.up * force, ForceMode.Force);

        // Гасим горизонтальное скольжение — дрон должен висеть на месте
        Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
        rb.AddForce(-hVel * horizontalDrag, ForceMode.Force);

        // Выравниваем вращение обратно в горизонт плавно
        Quaternion targetRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        rb.MoveRotation(Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 5f));
    }
}