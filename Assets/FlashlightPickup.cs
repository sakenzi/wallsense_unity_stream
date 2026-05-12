using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class FlashlightPickup : MonoBehaviour
{
    [Header("Свет фонарика")]
    public Light flashlight;
    public float intensity = 5f;

    private XRGrabInteractable grabInteractable;
    private bool isHeld = false;

    void Start()
    {
        grabInteractable = GetComponent<XRGrabInteractable>();

        if (flashlight == null)
            flashlight = GetComponentInChildren<Light>();

        if (flashlight != null)
            flashlight.enabled = false;

        // Проверяем что компонент найден перед подпиской
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.AddListener(OnPickUp);
            grabInteractable.selectExited.AddListener(OnDrop);
        }
        else
        {
            Debug.LogError("XRGrabInteractable не найден на " + gameObject.name);
        }
    }

    void OnPickUp(SelectEnterEventArgs args)
    {
        isHeld = true;
        if (flashlight != null)
            flashlight.enabled = true;
    }

    void OnDrop(SelectExitEventArgs args)
    {
        isHeld = false;
        if (flashlight != null)
            flashlight.enabled = false;
    }

    void OnDestroy()
    {
        // Проверяем перед отпиской — это и была причина ошибки
        if (grabInteractable != null)
        {
            grabInteractable.selectEntered.RemoveListener(OnPickUp);
            grabInteractable.selectExited.RemoveListener(OnDrop);
        }
    }
}