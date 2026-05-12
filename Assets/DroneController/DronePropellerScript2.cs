using UnityEngine;

/// <summary>
/// Вращение пропеллеров дрона на основе throttle.
/// Совместим с DroneMovementVR (реалистичная версия).
/// </summary>
public class DronePropellerScript2 : MonoBehaviour
{
    [Header("Rotation Settings")]
    [SerializeField] private float minSpeed = 900f;     
    [SerializeField] private float maxSpeed = 3600f;    

    private DroneMovementVR droneScript;
    private float currentSpeed;
    private bool clockwise;

    void Awake()
    {
        droneScript = GetComponentInParent<DroneMovementVR>();

        if (droneScript == null)
        {
            Debug.LogError($"[PropellerRotation] на {gameObject.name}: Не найден DroneMovementVR!");
            enabled = false;
            return;
        }

        // Определяем направление вращения по имени
        string propName = gameObject.name.ToLower();
        clockwise = propName.Contains("fl") || 
                    propName.Contains("frontleft") || 
                    propName.Contains("br") || 
                    propName.Contains("backright") || 
                    propName.Contains("rearright");

        Debug.Log($"[PropellerRotation] {gameObject.name} → {(clockwise ? "↻ CW" : "↺ CCW")}");
    }

    void Update()
    {
        if (droneScript == null) return;

        // Получаем throttle от дрона (0-1)
        float throttle = droneScript.GetThrottle();

        // Скорость вращения пропорциональна газу
        currentSpeed = Mathf.Lerp(minSpeed, maxSpeed, throttle);

        // Направление вращения
        Vector3 rotationAxis = clockwise ? Vector3.forward : -Vector3.forward;

        // Вращаем в локальном пространстве
        transform.Rotate(rotationAxis * currentSpeed * Time.deltaTime, Space.Self);
    }
}