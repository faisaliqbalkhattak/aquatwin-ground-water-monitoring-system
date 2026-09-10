using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AquaTwin
{
    [DisallowMultipleComponent]
    public sealed class RecordedTelemetryPlayback : MonoBehaviour
    {
        private readonly struct TelemetrySample
        {
            public readonly float DepthCm;
            public readonly float DepletionRate;
            public readonly float TdsPpm;
            public readonly float Ph;

            public TelemetrySample(float depthCm, float depletionRate, float tdsPpm, float ph)
            {
                DepthCm = depthCm;
                DepletionRate = depletionRate;
                TdsPpm = tdsPpm;
                Ph = ph;
            }
        }

        [SerializeField] private WellVisualizer wellVisualizer;
        [SerializeField] private bool playOnStart = true;
        [SerializeField] private bool loop;
        [Tooltip("Lead-in padding used to absorb video/application start timing differences.")]
        [Min(0f)] [SerializeField] private float startupDelaySeconds = 3f;
        [Min(0.01f)] [SerializeField] private float sampleIntervalSeconds = 1f;
        [Tooltip("Keeps the final reading visible through the end of the 46 second video.")]
        [Min(0f)] [SerializeField] private float endingHoldSeconds = 3f;
#if ENABLE_LEGACY_INPUT_MANAGER && !ENABLE_INPUT_SYSTEM
        [SerializeField] private KeyCode restartKey = KeyCode.R;
#endif

        private float playbackStartTime;
        private int appliedSampleIndex = -1;
        private bool isPlaying;

        public const float VideoDurationSeconds = 46f;
        public int CurrentSampleNumber => appliedSampleIndex + 1;
        public int SampleCount => Samples.Length;
        public bool IsPlaying => isPlaying;

        private static readonly TelemetrySample[] Samples =
        {
            new(15.68f, 0.0000f, 274.5f, 7.50f),
            new(15.68f, 0.0000f, 274.5f, 7.50f),
            new(15.68f, 0.0000f, 274.8f, 7.50f),
            new(15.68f, 0.0000f, 275.0f, 7.50f),
            new(15.68f, 0.0000f, 275.0f, 7.50f),
            new(15.68f, 0.0000f, 275.2f, 7.50f),
            new(15.68f, 0.0000f, 275.5f, 7.50f),
            new(15.68f, 0.0000f, 275.5f, 7.50f),
            new(15.68f, 0.0000f, 275.8f, 7.50f),
            new(15.68f, 0.0000f, 275.9f, 7.50f),
            new(15.68f, 0.0000f, 276.0f, 7.50f),
            new(15.68f, 0.0000f, 276.0f, 7.50f),
            new(15.68f, 0.0000f, 276.0f, 7.50f),
            new(15.68f, 0.0000f, 276.0f, 7.50f),
            new(15.68f, 0.0000f, 276.0f, 7.50f),
            new(15.33f, 0.3500f, 1205.0f, 10.00f),
            new(14.85f, 0.4800f, 1250.0f, 10.80f),
            new(14.32f, 0.5300f, 1295.0f, 11.50f),
            new(13.67f, 0.6500f, 1336.3f, 12.20f),
            new(13.02f, 0.6500f, 1361.7f, 13.00f),
            new(12.66f, 0.3600f, 1367.0f, 13.00f),
            new(12.31f, 0.3500f, 1369.0f, 13.00f),
            new(11.95f, 0.3600f, 1370.0f, 13.00f),
            new(11.64f, 0.3100f, 1370.0f, 13.00f),
            new(11.32f, 0.3200f, 1370.5f, 13.00f),
            new(10.99f, 0.3300f, 1370.9f, 13.00f),
            new(10.65f, 0.3400f, 1371.0f, 13.00f),
            new(10.32f, 0.3300f, 1371.2f, 13.00f),
            new(9.98f, 0.3400f, 1371.4f, 13.00f),
            new(9.65f, 0.3300f, 1371.4f, 13.00f),
            new(9.31f, 0.3400f, 1371.4f, 13.50f),
            new(9.00f, 0.3100f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f),
            new(9.00f, 0.0000f, 1371.9f, 13.00f)
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstall()
        {
            WellVisualizer visualizer = FindFirstObjectByType<WellVisualizer>();
            if (visualizer != null && visualizer.GetComponent<RecordedTelemetryPlayback>() == null)
            {
                RecordedTelemetryPlayback playback = visualizer.gameObject.AddComponent<RecordedTelemetryPlayback>();
                playback.wellVisualizer = visualizer;
            }
        }

        private void Start()
        {
            if (HiveMqttWellDataSource.LiveModeRequested)
            {
                isPlaying = false;
                enabled = false;
                return;
            }

            if (wellVisualizer == null)
                wellVisualizer = GetComponent<WellVisualizer>();
            if (playOnStart)
                RestartPlayback();
        }

        private void Update()
        {
            bool restartPressed = false;
#if ENABLE_INPUT_SYSTEM
            restartPressed = Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
            restartPressed = Input.GetKeyDown(restartKey);
#endif
            if (restartPressed)
                RestartPlayback();
            if (!isPlaying || wellVisualizer == null)
                return;

            float elapsed = Time.unscaledTime - playbackStartTime;
            if (elapsed < startupDelaySeconds)
                return;

            int targetIndex = Mathf.Clamp(
                Mathf.FloorToInt((elapsed - startupDelaySeconds) / sampleIntervalSeconds),
                0, Samples.Length - 1);
            if (targetIndex != appliedSampleIndex)
                ApplySample(targetIndex);

            if (elapsed >= startupDelaySeconds +
                Samples.Length * sampleIntervalSeconds + endingHoldSeconds)
            {
                if (loop) RestartPlayback();
                else
                {
                    isPlaying = false;
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
            }
        }

        [ContextMenu("Restart 46 Second Playback")]
        public void RestartPlayback()
        {
            playbackStartTime = Time.unscaledTime;
            appliedSampleIndex = -1;
            isPlaying = true;
            if (wellVisualizer != null)
                ApplySample(0);
        }

        private void ApplySample(int index)
        {
            TelemetrySample sample = Samples[index];
            wellVisualizer.SetRecordedSensorData(
                sample.DepthCm, sample.DepletionRate, sample.TdsPpm, sample.Ph);
            appliedSampleIndex = index;
        }
    }
}
