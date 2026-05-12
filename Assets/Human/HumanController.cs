using UnityEngine;

public class SimpleHumanMovement : MonoBehaviour
{
    public float moveSpeed = 0.1f;
    public float moveRadius = 1f;
    
    private Vector3 startPos;
    private float time;
    
    void Start()
    {
        startPos = transform.position;
    }
    
    void Update()
    {
        time += Time.deltaTime * moveSpeed;
        
        float x = startPos.x + Mathf.Sin(time) * moveRadius;
        float z = startPos.z + Mathf.Cos(time) * moveRadius;
        
        transform.position = new Vector3(x, startPos.y, z);
        
        // Поворот в сторону движения
        Vector3 dir = new Vector3(Mathf.Cos(time), 0, -Mathf.Sin(time));
        if (dir != Vector3.zero)
        {
            transform.rotation = Quaternion.Slerp(
            transform.rotation,
            Quaternion.LookRotation(dir),
            Time.deltaTime * 2f);
        }
    }
}