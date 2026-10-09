using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

[ExecuteAlways]
public class FrameTiming : MonoBehaviour
{
    [FormerlySerializedAs("output")] public GameObject outputTextObject;

    public string Summary { get; private set; } = "";

    private Text m_Text;

    const int k_StatsCount = 100;
    private float[] cpuFrameTimes = new float[k_StatsCount];
    private float[] gpuFrameTimes = new float[k_StatsCount];

    UnityEngine.FrameTiming[] m_FrameTiming = new UnityEngine.FrameTiming[1];

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (!outputTextObject)
            outputTextObject = gameObject;

        outputTextObject?.TryGetComponent<Text>(out m_Text);
    }

    UnityEngine.FrameTiming GetFrameTime()
    {
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, m_FrameTiming) > 0)
        {
            return m_FrameTiming[0];
        }

        return new UnityEngine.FrameTiming();
    }

    // Update is called once per frame
    void Update()
    {
        int index = Time.frameCount % k_StatsCount;
        UnityEngine.FrameTiming ft = GetFrameTime();
        cpuFrameTimes[index] = (float)ft.cpuFrameTime;
        gpuFrameTimes[index] = (float)ft.gpuFrameTime;

        float sumCpu = 0.0f;
        float sumGpu = 0.0f;
        for (int i = 0; i < k_StatsCount; i++)
        {
            sumCpu += cpuFrameTimes[i];
            sumGpu += gpuFrameTimes[i];
        }
        float averageCpu = sumCpu / k_StatsCount;
        float averageGpu = sumGpu / k_StatsCount;

        // Output
        const string fltFormat = "0.000";
        Summary = $"GPU: {averageGpu.ToString(fltFormat)} ms\nCPU: {averageCpu.ToString(fltFormat)} ms";
        if (m_Text) m_Text.text = Summary;
    }
}
