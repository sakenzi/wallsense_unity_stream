using UnityEngine;

public class FireTrigger : MonoBehaviour
{
    public AudioClip fireDetectedSound;  // звук "обнаружен пожар"
    private AudioSource audioSource;

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Drone"))
        {
            audioSource.PlayOneShot(fireDetectedSound);
            Debug.Log("🔥 Пожар обнаружен!");
        }
    }
}