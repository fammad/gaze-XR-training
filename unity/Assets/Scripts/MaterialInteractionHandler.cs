using UnityEngine;

using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class MaterialInteractionHandler : MonoBehaviour
{
    [Header("Audio Clips")]
    [SerializeField] private AudioClip woodHit;
    [SerializeField] private AudioClip metalHit;
    [SerializeField] private AudioClip marbleHit;
    [SerializeField] private AudioClip clayHit;
    [SerializeField] private AudioClip sawingSound;
    [SerializeField] private AudioClip filingSound;

    [Header("Haptics")]
    [SerializeField] private float impactIntensity = 1.0f;
    [SerializeField] private float impactDuration = 0.2f;
    [SerializeField] private float continuousIntensity = 0.5f;

    private AudioSource audioSource;

    void Awake()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
    }

    public void OnToolHit(ToolCollisionDetector.ToolHitData data)
    {
        AudioClip clipToPlay = null;
        if (data.material.CompareTag("Wood")) clipToPlay = woodHit;
        else if (data.material.CompareTag("Metal")) clipToPlay = metalHit;
        else if (data.material.CompareTag("Marble")) clipToPlay = marbleHit;
        else if (data.material.CompareTag("Clay")) clipToPlay = clayHit;

        if (clipToPlay != null)
        {
            audioSource.PlayOneShot(clipToPlay, Mathf.Clamp01(data.strength / 5f));
        }

        float hapticStrength = Mathf.Clamp01(data.strength / 10f) * impactIntensity;
        if (data.material.CompareTag("Marble")) hapticStrength *= 1.2f; 
        
        TriggerHaptics(data.tool, hapticStrength, impactDuration);

        if (data.tool.toolType != ToolCollisionDetector.ToolType.Saw)
        {
            DeformableMaterial deform = data.material.GetComponent<DeformableMaterial>();
            if (deform != null)
            {
                bool isFiling = data.tool.toolType == ToolCollisionDetector.ToolType.File;
                deform.ApplyDeformation(data.point, data.velocity, isFiling);
            }
        }
    }

    public void OnToolStay(ToolCollisionDetector tool, GameObject material, float speed, Vector3 point)
    {
        if (tool.toolType == ToolCollisionDetector.ToolType.Saw || tool.toolType == ToolCollisionDetector.ToolType.File)
        {
            float intensity = continuousIntensity;
            if (material.CompareTag("Clay")) intensity *= 0.5f; 
            
            TriggerHaptics(tool, intensity, 0.1f);

            AudioClip continuousClip = (tool.toolType == ToolCollisionDetector.ToolType.Saw) ? sawingSound : filingSound;
            if (continuousClip != null)
            {
                if (audioSource.clip != continuousClip)
                {
                    audioSource.clip = continuousClip;
                    audioSource.loop = true;
                }
                
                if (!audioSource.isPlaying)
                {
                    audioSource.Play();
                }
                
                audioSource.volume = Mathf.Clamp(speed / 1.5f, 0.2f, 1.0f);
            }
            
            if (tool.toolType == ToolCollisionDetector.ToolType.File)
            {
                DeformableMaterial deform = material.GetComponent<DeformableMaterial>();
                if (deform != null)
                {
                    deform.ApplyDeformation(point, tool.GetComponent<Rigidbody>().linearVelocity, true);
                }
            }
        }
    }

    public void OnToolExit()
    {
        if (audioSource != null && audioSource.loop)
        {
            audioSource.Stop();
            audioSource.loop = false;
        }
    }

    private void TriggerHaptics(ToolCollisionDetector tool, float intensity, float duration)
    {
        XRGrabInteractable grab = tool.GetComponent<XRGrabInteractable>();
        if (grab != null && grab.isSelected)
        {
            float finalIntensity = Mathf.Clamp01(intensity * 1.5f);
            foreach (var interactor in grab.interactorsSelecting)
            {
                if (interactor is XRBaseInputInteractor inputInteractor)
                {
                    inputInteractor.SendHapticImpulse(finalIntensity, duration);
                    Debug.Log($"[Haptics] Sent impulse to {inputInteractor.name}: Intensity {finalIntensity:F2}, Duration {duration:F2}");
                }
            }
        }
    }
}