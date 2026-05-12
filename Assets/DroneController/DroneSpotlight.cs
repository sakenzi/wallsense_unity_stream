using UnityEngine;

public class DroneSpotlight : MonoBehaviour
{
    [Header("Настройки света")]
    public float intensity = 5f;
    public float range = 30f;
    public float spotAngle = 45f;
    public Color lightColor = Color.white;

    [Header("Плавное включение")]
    public float fadeSpeed = 2f;

    private Light spotlight;

    void Start()
    {
        // Ищем Light на этом объекте или создаём
        spotlight = GetComponent<Light>();
        if (spotlight == null)
            spotlight = gameObject.AddComponent<Light>();

        spotlight.type = LightType.Spot;
        spotlight.intensity = intensity;
        spotlight.range = range;
        spotlight.spotAngle = spotAngle;
        spotlight.color = lightColor;
        spotlight.shadows = LightShadows.Soft;
        spotlight.renderMode = LightRenderMode.ForcePixel;

        // Направить вниз
        transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
    }
}