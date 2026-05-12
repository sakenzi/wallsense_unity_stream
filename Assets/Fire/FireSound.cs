using UnityEngine;

public class FireSound : MonoBehaviour
{
    [Header("Настройки")]
    public float maxDistance = 100f;
    public float minDistance = 10f;
    public float maxVolume = 2f;

    private AudioSource audioSource;
    private Transform player;
    private Transform drone;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();

        // Ищем игрока через Main Camera (XR Origin)
        Camera cam = Camera.main;
        if (cam != null)
            player = cam.transform;
        else
            player = GameObject.FindWithTag("Player").transform;

        // Ищем дрон по тегу
        GameObject droneObj = GameObject.FindWithTag("Drone");
        if (droneObj != null)
            drone = droneObj.transform;
    }

    void Update()
    {
        if (player == null) return;

        // Считаем расстояние от игрока
        float distancePlayer = Vector3.Distance(transform.position, player.position);

        // Считаем расстояние от дрона (если есть)
        float distanceDrone = drone != null
            ? Vector3.Distance(transform.position, drone.position)
            : float.MaxValue;

        // Берём минимальное расстояние — кто ближе тот и даёт громкость
        float closestDistance = Mathf.Min(distancePlayer, distanceDrone);

        // Считаем громкость
        float volume = 1f - Mathf.InverseLerp(minDistance, maxDistance, closestDistance);
        volume = Mathf.Clamp01(volume) * maxVolume;

        // Плавно меняем громкость
        audioSource.volume = Mathf.Lerp(audioSource.volume, volume, Time.deltaTime * 3f);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, minDistance);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, maxDistance);
    }
}