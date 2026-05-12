using UnityEngine;


public class DroneVisualEffects : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private DroneMovementVR droneController;
    
    [Header("Propeller Effects")]
    [SerializeField] private Transform[] propellerMeshes;
    [SerializeField] private float baseRotationSpeed = 500f;
    [SerializeField] private float maxRotationSpeed = 2000f;
    
    [Header("Particle Systems")]
    [SerializeField] private ParticleSystem[] thrusterParticles;
    [SerializeField] private ParticleSystem boostParticles;
    
    [Header("Lights")]
    [SerializeField] private Light[] navigationLights;
    [SerializeField] private float lightBlinkSpeed = 2f;
    
    [Header("Trail Renderer")]
    [SerializeField] private TrailRenderer[] propellerTrails;
    
    [Header("Tilt Visualization")]
    [SerializeField] private Transform bodyMesh;
    [SerializeField] private float maxVisualTilt = 20f;
    
    [Header("Sound Effects")]
    [SerializeField] private AudioSource propellerSound;
    [SerializeField] private AudioSource windSound;
    [SerializeField] private float minPropellerPitch = 0.8f;
    [SerializeField] private float maxPropellerPitch = 1.5f;

    private float lightTimer;
    private bool lightsOn;

    void Update()
    {
        if (droneController == null || !droneController.enabled)
        {
            StopAllEffects();
            return;
        }

        float throttle = droneController.throttle01;
        
        UpdatePropellers(throttle);
        UpdateParticles(throttle);
        UpdateLights();
        UpdateTrails(throttle);
        UpdateSounds(throttle);
        UpdateBodyTilt();
    }

    void UpdatePropellers(float throttle)
    {
        if (propellerMeshes == null || propellerMeshes.Length == 0) return;

        float rotationSpeed = Mathf.Lerp(baseRotationSpeed, maxRotationSpeed, throttle);

        foreach (var propeller in propellerMeshes)
        {
            if (propeller != null)
            {
                propeller.Rotate(Vector3.up, rotationSpeed * Time.deltaTime, Space.Self);
            }
        }
    }

    void UpdateParticles(float throttle)
    {
        if (thrusterParticles != null)
        {
            foreach (var ps in thrusterParticles)
            {
                if (ps != null)
                {
                    var emission = ps.emission;
                    emission.rateOverTime = Mathf.Lerp(10f, 50f, throttle);
                    
                    var main = ps.main;
                    main.startSpeed = Mathf.Lerp(2f, 8f, throttle);
                }
            }
        }

        if (boostParticles != null)
        {
            if (throttle > 0.8f && !boostParticles.isPlaying)
            {
                boostParticles.Play();
            }
            else if (throttle <= 0.8f && boostParticles.isPlaying)
            {
                boostParticles.Stop();
            }
        }
    }

    void UpdateLights()
    {
        if (navigationLights == null || navigationLights.Length == 0) return;

        lightTimer += Time.deltaTime * lightBlinkSpeed;
        
        if (lightTimer >= 1f)
        {
            lightTimer = 0f;
            lightsOn = !lightsOn;
        }

        foreach (var light in navigationLights)
        {
            if (light != null)
            {
                light.enabled = lightsOn;
            }
        }
    }

    void UpdateTrails(float throttle)
    {
        if (propellerTrails == null) return;

        foreach (var trail in propellerTrails)
        {
            if (trail != null)
            {
                trail.emitting = throttle > 0.3f;
                
                trail.startWidth = Mathf.Lerp(0.05f, 0.2f, throttle);
            }
        }
    }

    void UpdateSounds(float throttle)
    {
        if (propellerSound != null)
        {
            if (!propellerSound.isPlaying)
            {
                propellerSound.Play();
            }

            propellerSound.pitch = Mathf.Lerp(minPropellerPitch, maxPropellerPitch, throttle);
            propellerSound.volume = Mathf.Lerp(0.4f, 0.8f, throttle);
        }

        if (windSound != null && droneController != null)
        {
            float speed = droneController.GetCurrentSpeed();
            float normalizedSpeed = Mathf.Clamp01(speed / 10f);
            
            if (normalizedSpeed > 0.1f && !windSound.isPlaying)
            {
                windSound.Play();
            }
            else if (normalizedSpeed <= 0.1f && windSound.isPlaying)
            {
                windSound.Stop();
            }

            windSound.volume = normalizedSpeed * 0.6f;
        }
    }

    void UpdateBodyTilt()
    {
        if (bodyMesh == null || droneController == null) return;

        Vector3 localVelocity = transform.InverseTransformDirection(
            droneController.GetComponent<Rigidbody>().velocity
        );

        float pitchTilt = -localVelocity.z * 5f; 
        float rollTilt = localVelocity.x * 5f;   

        pitchTilt = Mathf.Clamp(pitchTilt, -maxVisualTilt, maxVisualTilt);
        rollTilt = Mathf.Clamp(rollTilt, -maxVisualTilt, maxVisualTilt);

        Quaternion targetRotation = Quaternion.Euler(pitchTilt, 0f, rollTilt);
        bodyMesh.localRotation = Quaternion.Slerp(
            bodyMesh.localRotation,
            targetRotation,
            Time.deltaTime * 3f
        );
    }

    void StopAllEffects()
    {
        if (thrusterParticles != null)
        {
            foreach (var ps in thrusterParticles)
            {
                if (ps != null) ps.Stop();
            }
        }

        if (boostParticles != null)
        {
            boostParticles.Stop();
        }

        if (propellerTrails != null)
        {
            foreach (var trail in propellerTrails)
            {
                if (trail != null) trail.emitting = false;
            }
        }

        if (propellerSound != null && propellerSound.isPlaying)
        {
            propellerSound.Stop();
        }

        if (windSound != null && windSound.isPlaying)
        {
            windSound.Stop();
        }
    }

    void OnDisable()
    {
        StopAllEffects();
    }
}