using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using System.Collections.Generic;

public class RayCanvasOnly : MonoBehaviour
{
    public XRRayInteractor rayInteractor;
    public XRInteractorLineVisual lineVisual;

    [Header("Все Canvas панели")]
    public List<GameObject> canvasList;

    void Start()
    {
        lineVisual.enabled = false;
    }

    void Update()
    {
        // Проверяем есть ли хоть один активный Canvas
        bool anyCanvasActive = false;
        foreach (GameObject canvas in canvasList)
        {
            if (canvas != null && canvas.activeSelf)
            {
                anyCanvasActive = true;
                break;
            }
        }

        if (!anyCanvasActive)
        {
            lineVisual.enabled = false;
            return;
        }

        bool hitsUI = rayInteractor.TryGetCurrentUIRaycastResult(
            out UnityEngine.EventSystems.RaycastResult result
        );

        lineVisual.enabled = hitsUI;
    }
}