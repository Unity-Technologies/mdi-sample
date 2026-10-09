using UnityEngine;

public class TargetFPS : MonoBehaviour
{
    public float targetFPS = 120f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        QualitySettings.vSyncCount = 0; 
        Application.targetFrameRate = (int)targetFPS;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
