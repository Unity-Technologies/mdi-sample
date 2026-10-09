using UnityEngine;

public class TargetFPS : MonoBehaviour
{
    public float targetFPS = 120f;

    void Start()
    {
        QualitySettings.vSyncCount = 0; 
        Application.targetFrameRate = (int)targetFPS;
    }
}
