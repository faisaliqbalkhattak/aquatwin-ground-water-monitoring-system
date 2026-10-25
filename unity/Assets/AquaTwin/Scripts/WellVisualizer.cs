using UnityEngine;

namespace AquaTwin
{
    /// <summary>
    /// Drives a well's water height and TDS colour in mock mode.
    /// Assign WaterLevelPivot to a transform whose pivot sits at the well bottom.
    /// The visible cylinder should be a child of that transform.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class WellVisualizer : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Transform waterLevelPivot;
        [SerializeField] private Renderer waterRenderer;
        [SerializeField] private Renderer[] additionalWaterRenderers;
        [Tooltip("Animated meniscus and ripple root created by the presentation-scene builder.")]
        [SerializeField] private Transform waterSurfaceTransform;

        [Header("Mock Sensor Data")]
        [Range(0f, 100f)]
        [SerializeField] private float simulatedWaterDepthPercent = 65f;

        [Range(0f, 2000f)]
        [SerializeField] private float simulatedTdsLevel = 180f;

        [Range(0f, 14f)]
        [SerializeField] private float simulatedPhLevel = 7.2f;

        [Min(0.1f)]
        [SerializeField] private float totalWellDepthMeters = 6f;

        [Tooltip("Physical depth of the recorded bottle well, used only to convert depth_cm into a visual fill percentage.")]
        [Min(0.1f)]
        [SerializeField] private float recordedWellDepthCm = 20f;

        [Header("TDS Colour Gradient")]
        [ColorUsage(false, true)]
        [SerializeField] private Color cleanWaterColor = new Color(0.08f, 0.57f, 0.66f, 0.42f);

        [ColorUsage(false, true)]
        [SerializeField] private Color highTdsColor = new Color(0.50f, 0.34f, 0.12f, 0.62f);

        [Header("Optional Response Smoothing")]
        [Min(0f)]
        [SerializeField] private float responseSpeed = 8f;

        [Header("Water Scale")]
        [Min(0.0001f)]
        [SerializeField] private float fullWaterScaleY = 1f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorId = Shader.PropertyToID("_Color");
        private const float MinimumVisibleScale = 0.0001f;

        private MaterialPropertyBlock propertyBlock;
        private Vector2 authoredHorizontalScale = Vector2.one;
        private Vector3 authoredSurfaceScale = Vector3.one;
        private float displayedDepthPercent;
        private float displayedTdsLevel;
        private bool initialized;
        private bool hasRecordedDepth;
        private float recordedDepthFromTopCm;
        private float recordedDepletionRateCmPerSecond;

        /// <summary>Current mock/live water depth in the inclusive 0-100 range.</summary>
        public float WaterDepthPercent
        {
            get => simulatedWaterDepthPercent;
            set => simulatedWaterDepthPercent = Mathf.Clamp(value, 0f, 100f);
        }

        /// <summary>Current mock/live TDS value in the inclusive 0-2000 ppm range.</summary>
        public float TdsLevel
        {
            get => simulatedTdsLevel;
            set => simulatedTdsLevel = Mathf.Clamp(value, 0f, 2000f);
        }

        public float PhLevel
        {
            get => simulatedPhLevel;
            set => simulatedPhLevel = Mathf.Clamp(value, 0f, 14f);
        }

        public float TotalWellDepthMeters => totalWellDepthMeters;

        public float DepthFromTopMeters => hasRecordedDepth
            ? recordedDepthFromTopCm * 0.01f
            : totalWellDepthMeters * (1f - Mathf.Clamp01(simulatedWaterDepthPercent / 100f));

        public float DepthFromTopCentimeters => hasRecordedDepth
            ? recordedDepthFromTopCm
            : DepthFromTopMeters * 100f;

        public float DepletionRateCmPerSecond => recordedDepletionRateCmPerSecond;
        public bool HasRecordedDepth => hasRecordedDepth;

        private void OnEnable()
        {
            CacheInitialState();
            displayedDepthPercent = simulatedWaterDepthPercent;
            displayedTdsLevel = simulatedTdsLevel;
            ApplyVisualization(displayedDepthPercent, displayedTdsLevel);

            if (Application.isPlaying)
                AnimateWaterSurface();
            else
                ResetWaterSurface();
        }

        private void Update()
        {
            if (!initialized)
                CacheInitialState();

            // In Edit Mode, respond immediately so Inspector sliders feel direct.
            if (!Application.isPlaying || responseSpeed <= 0f)
            {
                displayedDepthPercent = simulatedWaterDepthPercent;
                displayedTdsLevel = simulatedTdsLevel;
            }
            else
            {
                // Exponential damping is frame-rate independent and cannot overshoot.
                float blend = 1f - Mathf.Exp(-responseSpeed * Time.deltaTime);
                displayedDepthPercent = Mathf.Lerp(
                    displayedDepthPercent,
                    simulatedWaterDepthPercent,
                    blend);
                displayedTdsLevel = Mathf.Lerp(
                    displayedTdsLevel,
                    simulatedTdsLevel,
                    blend);
            }

            ApplyVisualization(displayedDepthPercent, displayedTdsLevel);
        }

        private void OnValidate()
        {
            simulatedWaterDepthPercent = Mathf.Clamp(simulatedWaterDepthPercent, 0f, 100f);
            simulatedTdsLevel = Mathf.Clamp(simulatedTdsLevel, 0f, 2000f);
            simulatedPhLevel = Mathf.Clamp(simulatedPhLevel, 0f, 14f);
            totalWellDepthMeters = Mathf.Max(0.1f, totalWellDepthMeters);
            recordedWellDepthCm = Mathf.Max(0.1f, recordedWellDepthCm);
            responseSpeed = Mathf.Max(0f, responseSpeed);
            fullWaterScaleY = Mathf.Max(MinimumVisibleScale, fullWaterScaleY);

            if (!isActiveAndEnabled)
                return;

            CacheInitialState();
            displayedDepthPercent = simulatedWaterDepthPercent;
            displayedTdsLevel = simulatedTdsLevel;
            ApplyVisualization(displayedDepthPercent, displayedTdsLevel);
        }

        /// <summary>
        /// Entry point for the future ESP32/networking layer.
        /// No networking dependency is required by this visual component.
        /// </summary>
        public void SetSensorData(float waterDepthPercent, float tdsPpm)
        {
            WaterDepthPercent = waterDepthPercent;
            TdsLevel = tdsPpm;
        }

        public void SetSensorData(float waterDepthPercent, float tdsPpm, float ph)
        {
            hasRecordedDepth = false;
            WaterDepthPercent = waterDepthPercent;
            TdsLevel = tdsPpm;
            PhLevel = ph;
        }

        public void SetRecordedSensorData(float depthFromTopCm,
            float depletionRateCmPerSecond, float tdsPpm, float ph)
        {
            ApplyDistanceSensorData(depthFromTopCm, depletionRateCmPerSecond,
                tdsPpm, ph, recordedWellDepthCm);
        }

        /// <summary>
        /// Receives the physical ultrasonic distance. Conversion to water fill is
        /// deliberately owned by the Digital Twin rather than the transport client.
        /// </summary>
        public void SetLiveSensorData(float distanceFromTopCm, float tdsPpm, float ph)
        {
            ApplyDistanceSensorData(distanceFromTopCm, 0f, tdsPpm, ph,
                recordedWellDepthCm);
        }

        private void ApplyDistanceSensorData(float depthFromTopCm,
            float depletionRateCmPerSecond, float tdsPpm, float ph,
            float calibratedFullDepthCm)
        {
            hasRecordedDepth = true;
            recordedDepthFromTopCm = depthFromTopCm;
            recordedDepletionRateCmPerSecond = depletionRateCmPerSecond;
            WaterDepthPercent = (1f - depthFromTopCm /
                Mathf.Max(0.1f, calibratedFullDepthCm)) * 100f;
            TdsLevel = tdsPpm;
            PhLevel = ph;
        }

        private void CacheInitialState()
        {
            if (waterLevelPivot == null)
            {
                initialized = false;
                return;
            }

            if (!initialized && waterSurfaceTransform != null)
                authoredSurfaceScale = waterSurfaceTransform.localScale;

            // Only Y is animated. X/Z remain whatever was authored on the pivot.
            // Full height is explicit, so edit-mode serialization cannot compound scale.
            authoredHorizontalScale = new Vector2(
                waterLevelPivot.localScale.x,
                waterLevelPivot.localScale.z);

            propertyBlock ??= new MaterialPropertyBlock();
            initialized = true;
        }

        private void ApplyVisualization(float depthPercent, float tdsPpm)
        {
            if (waterLevelPivot != null)
            {
                float normalizedDepth = Mathf.Clamp01(depthPercent / 100f);
                waterLevelPivot.localScale = new Vector3(
                    authoredHorizontalScale.x,
                    fullWaterScaleY * Mathf.Max(normalizedDepth, MinimumVisibleScale),
                    authoredHorizontalScale.y);
            }

            if (waterRenderer == null)
                return;

            // Most potable-range values remain blue-green; discoloration becomes
            // increasingly visible only as contamination approaches the high range.
            float normalizedTds = Mathf.Pow(Mathf.InverseLerp(0f, 1500f, tdsPpm), 1.7f);
            Color waterColor = Color.Lerp(cleanWaterColor, highTdsColor, normalizedTds);

            ApplyColor(waterRenderer, waterColor);

            if (additionalWaterRenderers == null)
                return;

            Color surfaceColor = Color.Lerp(waterColor, Color.white, 0.24f);
            surfaceColor.a = Mathf.Clamp01(waterColor.a + 0.28f);
            for (int i = 0; i < additionalWaterRenderers.Length; i++)
                ApplyColor(additionalWaterRenderers[i], surfaceColor);
        }

        private void AnimateWaterSurface()
        {
            if (waterSurfaceTransform == null)
                return;

            float pulse = 1f + Mathf.Sin(Time.time * 1.35f) * 0.012f;
            waterSurfaceTransform.localScale = new Vector3(
                authoredSurfaceScale.x * pulse,
                authoredSurfaceScale.y,
                authoredSurfaceScale.z * pulse);
            waterSurfaceTransform.localRotation = Quaternion.Euler(
                0f,
                Mathf.Sin(Time.time * 0.5f) * 2.5f,
                0f);
        }

        private void ResetWaterSurface()
        {
            if (waterSurfaceTransform == null)
                return;

            waterSurfaceTransform.localScale = authoredSurfaceScale;
            waterSurfaceTransform.localRotation = Quaternion.identity;
        }

        private void ApplyColor(Renderer targetRenderer, Color color)
        {
            if (targetRenderer == null)
                return;

            propertyBlock ??= new MaterialPropertyBlock();
            targetRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(BaseColorId, color);
            propertyBlock.SetColor(LegacyColorId, color);
            targetRenderer.SetPropertyBlock(propertyBlock);
        }
    }
}
