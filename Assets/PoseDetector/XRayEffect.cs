using UnityEngine;
using UnityEngine.InputSystem;

public class XRayEffect : MonoBehaviour
{
    [Header("NPC")]
    public GameObject npcObject;

    [Header("X-Ray Материалы")]
    public Material xrayMaterial;
    public Material xrayOccluded;
    public Material normalMaterial;

    private Renderer[] npcRenderers;
    private Material[][] originalMaterials;
    private bool xrayActive = false;
    private InputAction yButtonAction;

    void Awake()
    {
        yButtonAction = new InputAction(
            binding: "<XRController>{LeftHand}/secondaryButton"
        );
    }

    void OnEnable()
    {
        yButtonAction.performed += OnYPressed;
        yButtonAction.Enable();
    }

    void OnDisable()
    {
        yButtonAction.performed -= OnYPressed;
        yButtonAction.Disable();
    }

    void Start()
    {
        npcRenderers = npcObject.GetComponentsInChildren<Renderer>();

        // Сохраняем оригинальные материалы
        originalMaterials = new Material[npcRenderers.Length][];
        for (int i = 0; i < npcRenderers.Length; i++)
            originalMaterials[i] = npcRenderers[i].materials;
    }

    private void OnYPressed(InputAction.CallbackContext context)
    {
        xrayActive = !xrayActive;
        ApplyXRay(xrayActive);
    }

    private void ApplyXRay(bool active)
    {
        for (int i = 0; i < npcRenderers.Length; i++)
        {
            if (active)
            {
                npcRenderers[i].materials = new Material[] 
                { 
                    xrayMaterial, 
                    xrayOccluded 
                };
            }
            else
            {
                // Возвращаем оригинальные материалы
                npcRenderers[i].materials = originalMaterials[i];
            }
        }
    }
}